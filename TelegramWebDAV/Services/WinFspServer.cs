using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Threading;
using Fsp;
using Fsp.Interop;
using Microsoft.Win32;
using TelegramWebDAV.Config;
using TelegramWebDAV.Database;
using TelegramWebDAV.Models;
using FileInfo = Fsp.Interop.FileInfo;

namespace TelegramWebDAV.Services
{
    /// <summary>
    /// Сервис виртуальной файловой системы WinFsp (FUSE для Windows).
    /// Предоставляет нативный поблочный доступ к файлам Telegram прямо в оперативную память,
    /// минуя системную службу Windows WebClient и кэширование на системном диске C: (TfsStore\Tfs_DAV).
    /// </summary>
    public class WinFspServer : IDisposable
    {
        private readonly ConfigManager _configManager;
        private readonly NodeRepository _repository;
        private readonly TelegramService _telegramService;
        private FileSystemHost? _host;
        private string? _currentMountPoint;
        private readonly object _lock = new object();

        public bool IsMounted => _host != null;
        public string? MountPoint => _currentMountPoint;

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        private static bool _dllLoaded = false;
        private static readonly object _dllLock = new object();

        /// <summary>
        /// Гарантирует регистрацию нативной библиотеки WinFsp (winfsp-x64.dll) в системном пути DLL Windows.
        /// </summary>
        public static bool EnsureWinFspNativeDllLoaded()
        {
            if (_dllLoaded) return true;
            lock (_dllLock)
            {
                if (_dllLoaded) return true;
                try
                {
                    WinFspPatcher.PatchIfNeeded();

                    string? installDir = null;
                    using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp") ??
                                     Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp"))
                    {
                        if (key != null)
                        {
                            installDir = key.GetValue("InstallDir") as string;
                        }
                    }

                    if (string.IsNullOrEmpty(installDir))
                    {
                        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                        if (Directory.Exists(Path.Combine(pf86, "WinFsp"))) installDir = Path.Combine(pf86, "WinFsp");
                        else if (Directory.Exists(Path.Combine(pf, "WinFsp"))) installDir = Path.Combine(pf, "WinFsp");
                    }

                    if (!string.IsNullOrEmpty(installDir))
                    {
                        string binPath = Path.Combine(installDir, "bin");
                        if (Directory.Exists(binPath))
                        {
                            SetDllDirectory(binPath);
                            string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                            if (!currentPath.Contains(binPath, StringComparison.OrdinalIgnoreCase))
                            {
                                Environment.SetEnvironmentVariable("PATH", binPath + Path.PathSeparator + currentPath);
                            }

                            // Выбираем соответствующую DLL по архитектуре процесса (x64 или x86)
                            string dllName = Environment.Is64BitProcess ? "winfsp-x64.dll" : "winfsp-x86.dll";
                            string fullDllPath = Path.Combine(binPath, dllName);

                            if (File.Exists(fullDllPath))
                            {
                                try
                                {
                                    // Прямая предзагрузка нативной библиотеки в память процесса Windows через .NET 8 API
                                    NativeLibrary.Load(fullDllPath);
                                    AppLogger.Info("WinFsp", $"Нативная библиотека {dllName} успешно предзагружена в память процесса из {fullDllPath}");
                                }
                                catch (Exception loadEx)
                                {
                                    AppLogger.Warn("WinFsp", $"Не удалось предзагрузить {dllName} через NativeLibrary.Load: {loadEx.Message}");
                                }
                            }

                            AppLogger.Info("WinFsp", $"Зарегистрирован путь к нативным DLL WinFsp: {binPath}");
                        }
                    }

                    _dllLoaded = true;
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("WinFsp", $"Предупреждение при регистрации путей WinFsp: {ex.Message}");
                    return false;
                }
            }
        }

        public WinFspServer(
            ConfigManager configManager,
            NodeRepository repository,
            TelegramService telegramService)
        {
            _configManager = configManager;
            _repository = repository;
            _telegramService = telegramService;
            EnsureWinFspNativeDllLoaded();
        }

        /// <summary>
        /// Проверяет, установлен ли драйвер WinFsp в текущей операционной системе Windows.
        /// </summary>
        public static bool IsAvailable()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return false;

            EnsureWinFspNativeDllLoaded();

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp") ??
                                Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp");
                if (key != null)
                {
                    string? installDir = key.GetValue("InstallDir") as string;
                    if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
                        return true;
                }
            }
            catch
            {
                // Игнорируем ошибки доступа к реестру
            }

            // Дополнительная проверка наличия в системной папке или PATH
            try
            {
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (File.Exists(Path.Combine(pf86, "WinFsp", "bin", "winfsp-x64.dll")) ||
                    File.Exists(Path.Combine(pf, "WinFsp", "bin", "winfsp-x64.dll")))
                {
                    return true;
                }

                string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                if (File.Exists(Path.Combine(systemDir, "winfsp-x64.dll")) ||
                    File.Exists(Path.Combine(systemDir, "winfsp-x86.dll")))
                {
                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Монтирует файловую систему на указанную букву диска (например "Z:" или "Z:\").
        /// </summary>
        public bool Start(string driveLetter, out string errorMessage)
        {
            errorMessage = string.Empty;

            lock (_lock)
            {
                if (IsMounted)
                {
                    Stop();
                }

                string formattedLetter = driveLetter.Trim().ToUpper();
                if (!formattedLetter.EndsWith(":")) formattedLetter += ":";

                try
                {
                    var fileSystem = new TelegramWinFspFileSystem(_configManager, _repository, _telegramService);
                    _host = new FileSystemHost(fileSystem);

                    var settings = _configManager.Load();
                    _host.FileSystemName = "TelegramFS";
                    _host.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
                    _host.VolumeSerialNumber = 0x54454C47; // TELG
                    _host.SectorSize = 4096;
                    _host.SectorsPerAllocationUnit = 1;
                    _host.CaseSensitiveSearch = false;
                    _host.CasePreservedNames = true;
                    _host.UnicodeOnDisk = true;

                    AppLogger.Info("WinFsp", $"Попытка монтирования виртуального диска {formattedLetter} через WinFsp...");

                    int result = _host.Mount(formattedLetter, null, false, 0);
                    if (result != FileSystemBase.STATUS_SUCCESS)
                    {
                        errorMessage = $"Код ошибки WinFsp: 0x{result:X8}";
                        AppLogger.Warn("WinFsp", $"Не удалось смонтировать диск {formattedLetter}: {errorMessage}");
                        _host.Dispose();
                        _host = null;
                        return false;
                    }

                    _currentMountPoint = formattedLetter;
                    AppLogger.Info("WinFsp", $"Виртуальный диск {formattedLetter} успешно смонтирован через WinFsp (прямой стриминг в ОЗУ).");
                    return true;
                }
                catch (Exception ex)
                {
                    string details = ex.InnerException != null ? $"{ex.Message} (Детали: {ex.InnerException.Message})" : ex.Message;
                    errorMessage = details;
                    AppLogger.Error("WinFsp", $"Исключение при монтировании диска {formattedLetter}: {details}", ex);
                    if (_host != null)
                    {
                        try { _host.Dispose(); } catch { }
                        _host = null;
                    }
                    return false;
                }
            }
        }

        /// <summary>
        /// Размонтирует файловую систему WinFsp.
        /// </summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (_host != null)
                {
                    try
                    {
                        AppLogger.Info("WinFsp", $"Размонтирование диска {_currentMountPoint}...");
                        _host.Unmount();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("WinFsp", $"Предупреждение при размонтировании WinFsp: {ex.Message}");
                    }
                    finally
                    {
                        _host.Dispose();
                        _host = null;
                        _currentMountPoint = null;
                    }
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }

    /// <summary>
    /// Реализация интерфейсов ядра файловой системы Windows для WinFsp.
    /// </summary>
    internal class TelegramWinFspFileSystem : FileSystemBase
    {
        private readonly ConfigManager _configManager;
        private readonly NodeRepository _repository;
        private readonly TelegramService _telegramService;

        private const int NT_STATUS_UNSUCCESSFUL = unchecked((int)0xC0000001);
        private const int NT_STATUS_END_OF_FILE = unchecked((int)0xC0000011);
        private const int NT_STATUS_FILE_IS_A_DIRECTORY = unchecked((int)0xC00000BA);
        private const int NT_STATUS_OBJECT_NAME_NOT_FOUND = unchecked((int)0xC0000034);

        public TelegramWinFspFileSystem(
            ConfigManager configManager,
            NodeRepository repository,
            TelegramService telegramService)
        {
            _configManager = configManager;
            _repository = repository;
            _telegramService = telegramService;
        }

        public override int Init(object host)
        {
            return STATUS_SUCCESS;
        }

        public override int GetVolumeInfo(out VolumeInfo volumeInfo)
        {
            volumeInfo = default;
            volumeInfo.TotalSize = 1024UL * 1024 * 1024 * 1024 * 2; // 2 ТБ виртуального пространства
            volumeInfo.FreeSize = 1024UL * 1024 * 1024 * 1024 * 1;  // 1 ТБ свободного места
            string driveName = _configManager.Load().Server.DriveName ?? "Telegram Drive";
            volumeInfo.SetVolumeLabel(driveName);
            return STATUS_SUCCESS;
        }

        public override int GetSecurityByName(
            string fileName,
            out uint fileAttributes,
            ref byte[] securityDescriptor)
        {
            string cleanPath = NormalizePath(fileName);
            if (cleanPath == "/")
            {
                fileAttributes = (uint)FileAttributes.Directory;
                return STATUS_SUCCESS;
            }

            var node = _repository.GetNodeByPath(cleanPath);
            if (node == null)
            {
                fileAttributes = 0;
                return NT_STATUS_OBJECT_NAME_NOT_FOUND;
            }

            fileAttributes = node.IsDir ? (uint)FileAttributes.Directory : (uint)FileAttributes.Normal;
            return STATUS_SUCCESS;
        }

        public override int Open(
            string fileName,
            uint createOptions,
            uint grantedAccess,
            out object fileNode,
            out object fileDesc,
            out FileInfo fileInfo,
            out string normalizedName)
        {
            string cleanPath = NormalizePath(fileName);
            Node? node;

            if (cleanPath == "/")
            {
                node = new Node
                {
                    Id = 1,
                    ParentId = null,
                    Name = "/",
                    IsDir = true,
                    Size = 0,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
            }
            else
            {
                node = _repository.GetNodeByPath(cleanPath);
                if (node == null)
                {
                    fileNode = null!;
                    fileDesc = null!;
                    fileInfo = default;
                    normalizedName = null!;
                    return NT_STATUS_OBJECT_NAME_NOT_FOUND;
                }
            }

            fileNode = node;
            fileDesc = new FspNodeContext(node);
            FillFileInfo(node, out fileInfo);
            normalizedName = fileName;
            return STATUS_SUCCESS;
        }

        public override void Close(object fileNode, object fileDesc)
        {
            // Освобождение ресурсов при закрытии дескриптора файла
        }

        public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
        {
            var node = (Node)fileNode;
            if (node.Id > 1)
            {
                var fresh = _repository.GetNodeById(node.Id);
                if (fresh != null) node = fresh;
            }
            FillFileInfo(node, out fileInfo);
            return STATUS_SUCCESS;
        }

        public override bool ReadDirectoryEntry(
            object fileNode,
            object fileDesc,
            string pattern,
            string marker,
            ref object context,
            out string fileName,
            out FileInfo fileInfo)
        {
            var dirNode = (Node)fileNode;
            if (!dirNode.IsDir)
            {
                fileName = default!;
                fileInfo = default;
                return false;
            }

            if (context == null)
            {
                var children = _repository.GetChildren(dirNode.Id);
                var settings = _configManager.Load();
                if (settings.Server.HideTrashFromRoot && dirNode.Id == 1)
                {
                    children = children.FindAll(c => !string.Equals(c.Name, ".Trash", StringComparison.OrdinalIgnoreCase));
                }

                children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                context = new FspDirectoryEnumContext(children);
            }

            var dirEnum = (FspDirectoryEnumContext)context;
            if (marker != null)
            {
                while (dirEnum.Index < dirEnum.Items.Count &&
                       string.Compare(dirEnum.Items[dirEnum.Index].Name, marker, StringComparison.OrdinalIgnoreCase) <= 0)
                {
                    dirEnum.Index++;
                }
            }

            if (dirEnum.Index < dirEnum.Items.Count)
            {
                var child = dirEnum.Items[dirEnum.Index++];
                fileName = child.Name;
                FillFileInfo(child, out fileInfo);
                return true;
            }

            fileName = default!;
            fileInfo = default;
            return false;
        }

        public override int Read(
            object fileNode,
            object fileDesc,
            IntPtr buffer,
            ulong offset,
            uint length,
            out uint bytesTransferred)
        {
            var node = (Node)fileNode;
            if (node.IsDir)
            {
                bytesTransferred = 0;
                return NT_STATUS_FILE_IS_A_DIRECTORY;
            }

            if (offset >= (ulong)node.Size)
            {
                bytesTransferred = 0;
                return NT_STATUS_END_OF_FILE;
            }

            ulong remaining = (ulong)node.Size - offset;
            uint toRead = (uint)Math.Min((ulong)length, remaining);

            if (toRead == 0)
            {
                bytesTransferred = 0;
                return STATUS_SUCCESS;
            }

            try
            {
                if (node.TgMessageId == null)
                {
                    bytesTransferred = 0;
                    return NT_STATUS_UNSUCCESSFUL;
                }

                unsafe
                {
                    // Прямой стриминг в предоставленный ядром Windows буфер без создания файлов на диске C:!
                    using var memStream = new UnmanagedMemoryStream((byte*)buffer.ToPointer(), toRead, toRead, FileAccess.Write);
                    _telegramService.DownloadFileAsync(node.TgMessageId.Value, memStream, (long)offset, (long)toRead, node.Name)
                                    .GetAwaiter().GetResult();
                    bytesTransferred = (uint)memStream.Position;
                }
                return STATUS_SUCCESS;
            }
            catch (Exception ex)
            {
                AppLogger.Error("WinFsp", $"Ошибка прямого чтения '{node.Name}' (смещение {offset}, длина {toRead}): {ex.Message}", ex);
                bytesTransferred = 0;
                return NT_STATUS_UNSUCCESSFUL;
            }
        }

        private static void FillFileInfo(Node node, out FileInfo fileInfo)
        {
            fileInfo = default;
            fileInfo.FileAttributes = node.IsDir ? (uint)FileAttributes.Directory : (uint)FileAttributes.Normal;
            fileInfo.FileSize = (ulong)node.Size;
            fileInfo.AllocationSize = (ulong)((node.Size + 4095) / 4096 * 4096);
            fileInfo.CreationTime = (ulong)node.CreatedAt.ToFileTimeUtc();
            fileInfo.LastWriteTime = (ulong)node.UpdatedAt.ToFileTimeUtc();
            fileInfo.LastAccessTime = (ulong)node.UpdatedAt.ToFileTimeUtc();
            fileInfo.ChangeTime = (ulong)node.UpdatedAt.ToFileTimeUtc();
            fileInfo.IndexNumber = (ulong)node.Id;
            fileInfo.HardLinks = 1;
        }

        private static string NormalizePath(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "/";
            string p = fileName.Replace('\\', '/').Trim();
            if (!p.StartsWith("/")) p = "/" + p;
            p = p.TrimEnd('/');
            return string.IsNullOrEmpty(p) ? "/" : p;
        }

        private class FspNodeContext
        {
            public Node Node { get; }
            public FspNodeContext(Node node) => Node = node;
        }

        private class FspDirectoryEnumContext
        {
            public List<Node> Items { get; }
            public int Index { get; set; }
            public FspDirectoryEnumContext(List<Node> items) => Items = items;
        }
    }
}
