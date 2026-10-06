using System;
using System.Buffers;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
                    _host.FileSystemName = "NTFS";
                    _host.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
                    _host.VolumeSerialNumber = 0x54454C47; // TELG
                    _host.SectorSize = 4096;
                    _host.SectorsPerAllocationUnit = 1;
                    _host.CaseSensitiveSearch = false;
                    _host.CasePreservedNames = true;
                    _host.UnicodeOnDisk = true;

                    AppLogger.Info("WinFsp", $"Попытка монтирования виртуального диска {formattedLetter} через WinFsp...");

                    int result = _host.Mount(formattedLetter, TelegramWinFspFileSystem.DefaultSecurityDescriptor, false, 0);
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
    /// Поддерживает полноценное чтение (прямой стриминг MTProto чанков в память плеера) 
    /// и запись (копирование файлов в Telegram, создание папок, переименование и удаление).
    /// </summary>
    internal class TelegramWinFspFileSystem : FileSystemBase
    {
        private readonly ConfigManager _configManager;
        private readonly NodeRepository _repository;
        private readonly TelegramService _telegramService;

        private const int NT_STATUS_SUCCESS = 0;
        private const int NT_STATUS_UNSUCCESSFUL = unchecked((int)0xC0000001);
        private const int NT_STATUS_END_OF_FILE = unchecked((int)0xC0000011);
        private const int NT_STATUS_OBJECT_NAME_NOT_FOUND = unchecked((int)0xC0000034);
        private const int NT_STATUS_OBJECT_NAME_COLLISION = unchecked((int)0xC0000035);
        private const int NT_STATUS_OBJECT_PATH_NOT_FOUND = unchecked((int)0xC000003A);
        private const int NT_STATUS_FILE_IS_A_DIRECTORY = unchecked((int)0xC00000BA);
        private const int NT_STATUS_DIRECTORY_NOT_EMPTY = unchecked((int)0xC0000101);
        private const int NT_STATUS_CANNOT_DELETE = unchecked((int)0xC0000121);

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

        // Дескриптор безопасности:
        // O:WD - Owner: World (Everyone / Все пользователи)
        // G:WD - Group: World (Everyone / Все пользователи)
        // D:P(A;;FA;;;WD) - DACL: Protected, Allow Full Access (FA) to World (WD)
        // Гарантирует полный неограниченный доступ для всех пользователей и программ Windows
        public static readonly byte[] DefaultSecurityDescriptor = CreateDefaultSecurityDescriptor();

        private static byte[] CreateDefaultSecurityDescriptor()
        {
            try
            {
                var raw = new RawSecurityDescriptor("O:WDG:WDD:P(A;;FA;;;WD)");
                byte[] binary = new byte[raw.BinaryLength];
                raw.GetBinaryForm(binary, 0);
                return binary;
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        public override int GetVolumeInfo(out VolumeInfo volumeInfo)
        {
            volumeInfo = default;
            var settings = _configManager.CurrentSettings;
            long usedBytes = _repository.GetTotalUsedSpaceBytes(settings.Server.IncludeTrashInUsedSpace);

            long baseCapacityGb = settings.Server.VirtualDiskCapacityGb > 0 ? settings.Server.VirtualDiskCapacityGb : 1024;
            long totalCapacityBytes = baseCapacityGb * 1024L * 1024L * 1024L;

            // Если включено авто-расширение: если занято более 70%, динамически увеличиваем емкость,
            // чтобы диск в Проводнике всегда имел >= 30% свободного места и не окрашивался в красный цвет
            if (settings.Server.AutoExpandDiskCapacity && usedBytes > 0)
            {
                double usageRatio = (double)usedBytes / totalCapacityBytes;
                if (usageRatio >= 0.70)
                {
                    long requiredCapacity = (long)(usedBytes / 0.70);
                    long step = 100L * 1024L * 1024L * 1024L; // Шаг округления 100 ГБ
                    long rounded = ((requiredCapacity + step - 1) / step) * step;
                    totalCapacityBytes = Math.Max(totalCapacityBytes, rounded);
                }
            }

            long freeBytes = Math.Max(0, totalCapacityBytes - usedBytes);

            volumeInfo.TotalSize = (ulong)totalCapacityBytes;
            volumeInfo.FreeSize = (ulong)freeBytes;
            string driveName = settings.Server.DriveName ?? "Telegram Drive";
            volumeInfo.SetVolumeLabel(driveName);
            return STATUS_SUCCESS;
        }

        public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
        {
            securityDescriptor = DefaultSecurityDescriptor;
            return STATUS_SUCCESS;
        }

        public override int GetSecurityByName(
            string fileName,
            out uint fileAttributes,
            ref byte[] securityDescriptor)
        {
            securityDescriptor = DefaultSecurityDescriptor;
            string cleanPath = NormalizePath(fileName);
            if (cleanPath.Contains(":")) // Alternate Data Stream (:Zone.Identifier и др.)
            {
                fileAttributes = 0;
                return NT_STATUS_OBJECT_NAME_NOT_FOUND;
            }

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
            if (cleanPath.Contains(":"))
            {
                fileNode = null!;
                fileDesc = null!;
                fileInfo = default;
                normalizedName = null!;
                return NT_STATUS_OBJECT_NAME_NOT_FOUND;
            }

            Node? node;
            if (cleanPath == "/")
            {
                node = _repository.GetRootNode() ?? new Node
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

        public override int Create(
            string fileName,
            uint createOptions,
            uint grantedAccess,
            uint fileAttributes,
            byte[] securityDescriptor,
            ulong allocationSize,
            out object fileNode,
            out object fileDesc,
            out FileInfo fileInfo,
            out string normalizedName)
        {
            string cleanPath = NormalizePath(fileName);
            if (cleanPath.Contains(":"))
            {
                fileNode = null!;
                fileDesc = null!;
                fileInfo = default;
                normalizedName = null!;
                return NT_STATUS_OBJECT_NAME_NOT_FOUND;
            }

            string parentPath = GetParentPath(cleanPath);
            string itemName = GetFileName(cleanPath);
            var parentNode = parentPath == "/" ? _repository.GetRootNode() : _repository.GetNodeByPath(parentPath);
            if (parentNode == null)
            {
                fileNode = null!;
                fileDesc = null!;
                fileInfo = default;
                normalizedName = null!;
                return NT_STATUS_OBJECT_PATH_NOT_FOUND;
            }

            bool isDir = (createOptions & FILE_DIRECTORY_FILE) != 0;
            if (isDir)
            {
                bool existed = _repository.GetNodeByPath(cleanPath) != null;
                var dirNode = _repository.EnsureDirectoryPathExists(cleanPath);
                if (dirNode == null)
                {
                    fileNode = null!;
                    fileDesc = null!;
                    fileInfo = default;
                    normalizedName = null!;
                    return NT_STATUS_UNSUCCESSFUL;
                }

                fileNode = dirNode;
                fileDesc = new FspNodeContext(dirNode);
                FillFileInfo(dirNode, out fileInfo);
                normalizedName = fileName;
                if (existed)
                {
                    AppLogger.Info("WinFsp", $"Открыт каталог: '{cleanPath}' (ID {dirNode.Id})");
                }
                else
                {
                    AppLogger.Info("WinFsp", $"Создан новый каталог: '{cleanPath}' (ID {dirNode.Id})");
                }
                return STATUS_SUCCESS;
            }
            else
            {
                _repository.CreateOrUpdateFile(parentNode.Id, itemName, 0, null);
                var node = _repository.GetNodeByPath(cleanPath);
                if (node == null)
                {
                    fileNode = null!;
                    fileDesc = null!;
                    fileInfo = default;
                    normalizedName = null!;
                    return NT_STATUS_UNSUCCESSFUL;
                }

                var ctx = new FspNodeContext(node)
                {
                    IsModified = true,
                    KnownTargetSize = allocationSize > 0 ? (long)allocationSize : -1,
                    OriginalSourcePath = TryFindSourceFile(itemName, cleanPath)
                };

                fileNode = node;
                fileDesc = ctx;
                FillFileInfo(node, out fileInfo);
                normalizedName = fileName;
                AppLogger.Info("WinFsp", $"Создан файл для записи: '{cleanPath}' (ID {node.Id}, alloc: {allocationSize})" + (!string.IsNullOrEmpty(ctx.OriginalSourcePath) ? $", источник: '{ctx.OriginalSourcePath}'" : ""));
                return STATUS_SUCCESS;
            }
        }

        public override int Overwrite(
            object fileNode,
            object fileDesc,
            uint fileAttributes,
            bool replaceFileAttributes,
            ulong allocationSize,
            out FileInfo fileInfo)
        {
            var node = (Node)fileNode;
            var ctx = (FspNodeContext)fileDesc;
            node.Size = 0;
            node.UpdatedAt = DateTime.UtcNow;
            ctx.Dispose();
            ctx.IsModified = true;
            ctx.KnownTargetSize = allocationSize > 0 ? (long)allocationSize : -1;
            ctx.TotalBytesWritten = 0;
            ctx.IsUploadStarted = false;
            ctx.PipeStream = null;
            ctx.UploadTask = null;
            ctx.UploadCts = null;
            ctx.OriginalSourcePath = TryFindSourceFile(node.Name, _repository.GetNodeFullPath(node.Id));
            FillFileInfo(node, out fileInfo);
            return STATUS_SUCCESS;
        }

        public override int Write(
            object fileNode,
            object fileDesc,
            IntPtr buffer,
            ulong offset,
            uint length,
            bool writeToEndOfFile,
            bool constrainedIo,
            out uint bytesTransferred,
            out FileInfo fileInfo)
        {
            var node = (Node)fileNode;
            var ctx = (FspNodeContext)fileDesc;
            if (node.IsDir)
            {
                bytesTransferred = 0;
                FillFileInfo(node, out fileInfo);
                return NT_STATUS_FILE_IS_A_DIRECTORY;
            }

            if (length == 0)
            {
                bytesTransferred = 0;
                FillFileInfo(node, out fileInfo);
                return STATUS_SUCCESS;
            }

            byte[] poolBuffer = ArrayPool<byte>.Shared.Rent((int)length);
            try
            {
                Marshal.Copy(buffer, poolBuffer, 0, (int)length);

                // Если размер файла известен заранее и превышает 1 МБ -> потоковый стриминг в Telegram на лету (Zero-Temp)
                long targetTotalSize = ctx.KnownTargetSize > 0 ? ctx.KnownTargetSize : (long)(offset + length);

                if (targetTotalSize > 1048576)
                {
                    // Инициализируем потоковую передачу в Telegram, если еще не запущена
                    if (!ctx.IsUploadStarted)
                    {
                        ctx.IsUploadStarted = true;
                        ctx.UploadCts = new CancellationTokenSource();
                        ctx.PipeStream = new StreamingPipeStream(targetTotalSize);

                        int parentId = node.ParentId ?? 1;
                        string nodeName = node.Name;
                        int nextVersion = _repository.GetNextVersionForFile(parentId, nodeName);
                        string parentPath = _repository.GetNodeFullPath(parentId);
                        if (parentPath == "/") parentPath = "";
                        string ext = Path.GetExtension(nodeName);
                        string nameNoExt = Path.GetFileNameWithoutExtension(nodeName);
                        string fullPathWithVersion = $"{parentPath}/{nameNoExt}_v{nextVersion}{ext}";

                        if (string.IsNullOrEmpty(ctx.OriginalSourcePath))
                        {
                            AppLogger.Info("WinFsp", $"[Write] Пробуем определить источник (Drag-and-Drop / Буфер обмена) для '{nodeName}'...");
                            ctx.OriginalSourcePath = TryFindSourceFile(nodeName, $"{parentPath}/{nodeName}");
                        }

                        AudioMetadataResult? audioMeta = null;
                        VideoMetadataResult? videoMeta = null;

                        if (!string.IsNullOrEmpty(ctx.OriginalSourcePath))
                        {
                            AppLogger.Info("WinFsp", $"[Write] Начинаем извлечение метаданных из оригинального файла: '{ctx.OriginalSourcePath}'");

                            if (VideoMetadataExtractor.IsPotentialVideo(nodeName))
                            {
                                try
                                {
                                    videoMeta = VideoMetadataExtractor.ExtractFromFile(ctx.OriginalSourcePath, nodeName);
                                    AppLogger.Info("WinFsp", $"[Write] Результат VideoMetadataExtractor для '{nodeName}': " +
                                        $"длительность = {videoMeta.DurationSeconds} сек, разрешение = {videoMeta.Width}x{videoMeta.Height}, " +
                                        $"обложка = {(videoMeta.Thumbnail != null ? $"{videoMeta.Thumbnail.Length} байт" : "НЕТ")}");
                                }
                                catch (Exception ex)
                                {
                                    AppLogger.Error("WinFsp", $"[Write] Ошибка VideoMetadataExtractor для '{ctx.OriginalSourcePath}': {ex.Message}", ex);
                                }
                            }
                            else if (AudioMetadataExtractor.IsPotentialAudio(nodeName))
                            {
                                try
                                {
                                    audioMeta = AudioMetadataExtractor.ExtractFromFile(ctx.OriginalSourcePath, nodeName);
                                    AppLogger.Info("WinFsp", $"[Write] Результат AudioMetadataExtractor для '{nodeName}': " +
                                        $"трек = '{audioMeta.Artist} - {audioMeta.Title}', длительность = {audioMeta.DurationSeconds} сек, " +
                                        $"обложка = {(audioMeta.AlbumCover != null ? $"{audioMeta.AlbumCover.Length} байт" : "НЕТ")}");
                                }
                                catch (Exception ex)
                                {
                                    AppLogger.Error("WinFsp", $"[Write] Ошибка AudioMetadataExtractor для '{ctx.OriginalSourcePath}': {ex.Message}", ex);
                                }
                            }
                        }
                        else
                        {
                            AppLogger.Warn("WinFsp", $"[Write] Исходный путь для '{nodeName}' НЕ найден в буфере обмена Windows.");
                        }

                        // Если оригинал не найден в буфере обмена (например, перетаскивание), для аудио извлекаем из первых 256 КБ
                        if (audioMeta == null && AudioMetadataExtractor.IsPotentialAudio(nodeName))
                        {
                            try
                            {
                                using var headerMs = new MemoryStream(poolBuffer, 0, (int)Math.Min((long)length, AudioMetadataExtractor.HeaderCacheSize));
                                audioMeta = AudioMetadataExtractor.ExtractFromStream(headerMs, nodeName);
                                AppLogger.Info("WinFsp", $"[Write] Резервный парсинг тегов аудио из первых 256 КБ потока для '{nodeName}': " +
                                    $"'{audioMeta.Artist} - {audioMeta.Title}', обложка: {(audioMeta.AlbumCover != null ? $"{audioMeta.AlbumCover.Length} байт" : "НЕТ")}");
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Warn("WinFsp", $"[Write] Ошибка резервного парсинга тегов из потока для '{nodeName}': {ex.Message}");
                            }
                        }

                        AppLogger.Info("WinFsp", $"Запуск прямой потоковой передачи '{nodeName}' ({targetTotalSize} байт) в Telegram на лету (Zero-Temp)" + 
                            (videoMeta != null ? $", видео: {videoMeta.Width}x{videoMeta.Height}, {videoMeta.DurationSeconds} сек, обложка: {(videoMeta.Thumbnail != null ? "ДА" : "НЕТ")}" : "") +
                            (audioMeta != null ? $", аудио: {audioMeta.DurationSeconds} сек, обложка: {(audioMeta.AlbumCover != null ? "ДА" : "НЕТ")}" : "") + "...");
                        ctx.UploadTask = Task.Run(() => _telegramService.UploadFileAsync(
                            ctx.PipeStream,
                            nodeName,
                            targetTotalSize,
                            caption: fullPathWithVersion,
                            audioMeta: audioMeta,
                            videoMeta: videoMeta,
                            originalFilePath: ctx.OriginalSourcePath
                        ));
                    }

                    if (ctx.PipeStream != null)
                    {
                        ctx.PipeStream.PushData(poolBuffer, 0, (int)length);
                    }
                }
                else
                {
                    // Для небольших файлов (<= 1 МБ) буферизируем в ОЗУ через MemoryStream
                    ctx.SmallFileBuffer ??= new MemoryStream();
                    if (writeToEndOfFile)
                    {
                        ctx.SmallFileBuffer.Seek(0, SeekOrigin.End);
                    }
                    else
                    {
                        ctx.SmallFileBuffer.Seek((long)offset, SeekOrigin.Begin);
                    }
                    ctx.SmallFileBuffer.Write(poolBuffer, 0, (int)length);
                }

                ctx.TotalBytesWritten += length;
                bytesTransferred = length;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(poolBuffer);
            }

            ctx.IsModified = true;
            if ((long)(offset + length) > node.Size)
            {
                node.Size = (long)(offset + length);
            }
            node.UpdatedAt = DateTime.UtcNow;
            FillFileInfo(node, out fileInfo);
            return STATUS_SUCCESS;
        }

        public override int SetFileSize(
            object fileNode,
            object fileDesc,
            ulong newSize,
            bool setAllocationSize,
            out FileInfo fileInfo)
        {
            var node = (Node)fileNode;
            var ctx = (FspNodeContext)fileDesc;
            if (!setAllocationSize)
            {
                node.Size = (long)newSize;
                ctx.KnownTargetSize = (long)newSize;
                ctx.IsModified = true;
            }
            else if (ctx.KnownTargetSize <= 0 && newSize > 0)
            {
                ctx.KnownTargetSize = (long)newSize;
            }
            FillFileInfo(node, out fileInfo);
            return STATUS_SUCCESS;
        }

        public override int SetBasicInfo(
            object fileNode,
            object fileDesc,
            uint fileAttributes,
            ulong creationTime,
            ulong lastAccessTime,
            ulong lastWriteTime,
            ulong changeTime,
            out FileInfo fileInfo)
        {
            var node = (Node)fileNode;
            DateTime? newUpdatedAt = null;
            DateTime? newCreatedAt = null;

            if (lastWriteTime != 0)
            {
                newUpdatedAt = DateTime.FromFileTimeUtc((long)lastWriteTime);
                node.UpdatedAt = newUpdatedAt.Value;
            }
            if (creationTime != 0)
            {
                newCreatedAt = DateTime.FromFileTimeUtc((long)creationTime);
                node.CreatedAt = newCreatedAt.Value;
            }

            if (newUpdatedAt.HasValue || newCreatedAt.HasValue)
            {
                _repository.UpdateNodeTimestamps(node.Id, newUpdatedAt, newCreatedAt);
                AppLogger.Info("WinFsp", $"Обновлены системные даты для '{node.Name}' (ID {node.Id}): Изменен: {node.UpdatedAt:yyyy-MM-dd HH:mm:ss}, Создан: {node.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            }

            FillFileInfo(node, out fileInfo);
            return STATUS_SUCCESS;
        }

        public override int CanDelete(object fileNode, object fileDesc, string fileName)
        {
            var node = (Node)fileNode;

            // Защита системной папки .Trash от удаления снаружи
            if (_repository.IsTrashFolder(node.Id) || string.Equals(fileName.Trim('/', '\\'), ".Trash", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Warn("WinFsp", "Попытка удаления системной папки '.Trash' отклонена: корзина защищена от удаления.");
                return NT_STATUS_CANNOT_DELETE;
            }

            if (node.IsDir)
            {
                var children = _repository.GetChildren(node.Id);
                if (children.Count > 0)
                {
                    return NT_STATUS_DIRECTORY_NOT_EMPTY;
                }
            }
            return STATUS_SUCCESS;
        }

        public override int SetDelete(object fileNode, object fileDesc, string fileName, bool deleteFile)
        {
            if (fileDesc is FspNodeContext ctx)
            {
                ctx.DeleteOnClose = deleteFile;
            }
            return STATUS_SUCCESS;
        }

        public override int Rename(
            object fileNode,
            object fileDesc,
            string fileName,
            string newFileName,
            bool replaceIfExists)
        {
            var node = (Node)fileNode;
            string oldClean = NormalizePath(fileName);
            string newClean = NormalizePath(newFileName);

            string newParentPath = GetParentPath(newClean);
            string newName = GetFileName(newClean);

            var targetParent = newParentPath == "/" ? _repository.GetRootNode() : _repository.GetNodeByPath(newParentPath);
            if (targetParent == null)
            {
                return NT_STATUS_OBJECT_PATH_NOT_FOUND;
            }

            try
            {
                _repository.MoveNode(node.Id, targetParent.Id, newName);
                node.Name = newName;
                node.ParentId = targetParent.Id;
                AppLogger.Info("WinFsp", $"Переименование/перемещение: '{oldClean}' -> '{newClean}'");

                return STATUS_SUCCESS;
            }
            catch (Exception ex)
            {
                AppLogger.Error("WinFsp", $"Ошибка переименования '{oldClean}' -> '{newClean}': {ex.Message}", ex);
                return NT_STATUS_UNSUCCESSFUL;
            }
        }

        public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
        {
            var node = (Node)fileNode;
            var ctx = (FspNodeContext)fileDesc;

            if ((flags & CleanupDelete) != 0 || ctx.DeleteOnClose)
            {
                // Защита от удаления системной папки корзины
                if (_repository.IsTrashFolder(node.Id) || string.Equals(fileName.Trim('/', '\\'), ".Trash", StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Warn("WinFsp", "Отклонено удаление системной папки корзины '.Trash'.");
                    ctx.Dispose();
                    return;
                }

                // Проверяем, находится ли элемент уже в корзине (.Trash) или помечен ли он как удаленный
                bool isPermanent = node.InTrash || _repository.IsNodeInTrash(node.Id);

                if (isPermanent)
                {
                    AppLogger.Info("WinFsp", $"Перманентное удаление элемента '{node.Name}' (ID {node.Id}) из корзины...");
                    var subtree = _repository.GetSubtreeNodes(node.Id);
                    var dbNodeIds = new List<int>();
                    var tgMessageIds = new List<int>();

                    foreach (var n in subtree)
                    {
                        dbNodeIds.Add(n.Id);
                        if (n.TgMessageId.HasValue && n.TgMessageId.Value > 0)
                        {
                            tgMessageIds.Add(n.TgMessageId.Value);
                        }
                        if (n.TgPreviewMessageId.HasValue && n.TgPreviewMessageId.Value > 0)
                        {
                            tgMessageIds.Add(n.TgPreviewMessageId.Value);
                        }
                    }

                    // Атомарно помещаем сообщения в гарантированную очередь удаления и удаляем узлы из базы
                    _repository.EnqueuePermanentDeletion(tgMessageIds, dbNodeIds);
                    AppLogger.Info("WinFsp", $"Удалено {dbNodeIds.Count} узлов из базы; {tgMessageIds.Count} сообщений отправлено в очередь удаления Telegram.");

                    if (tgMessageIds.Count > 0)
                    {
                        _telegramService.TriggerDeletionQueueProcessing();
                    }

                    _repository.ScheduleVacuum(3000);
                }
                else
                {
                    AppLogger.Info("WinFsp", $"Перемещение элемента '{node.Name}' (ID {node.Id}) в корзину (.Trash)...");
                    _repository.SoftDeleteNode(node.Id);
                }

                ctx.Dispose();
                return;
            }

            if (ctx.IsModified)
            {
                ctx.IsModified = false;
                int parentId = node.ParentId ?? 1;
                string nodeName = node.Name;
                DateTime targetUpdatedAt = node.UpdatedAt;
                DateTime targetCreatedAt = node.CreatedAt;

                // Вариант 1: Большой файл, который передавался на лету через StreamingPipeStream
                if (ctx.PipeStream != null && ctx.UploadTask != null)
                {
                    var pipe = ctx.PipeStream;
                    var uploadTask = ctx.UploadTask;
                    var cts = ctx.UploadCts;
                    ctx.PipeStream = null;
                    ctx.UploadTask = null;
                    ctx.UploadCts = null;

                    pipe.CompleteWriting();

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var uploadResult = await uploadTask;
                            if (uploadResult?.MessageId != null)
                            {
                                long finalLength = node.Size;
                                _repository.CreateOrUpdateFile(
                                    parentId,
                                    nodeName,
                                    finalLength,
                                    uploadResult.MessageId,
                                    uploadResult.PreviewMessageId,
                                    inlineData: null,
                                    lastModified: targetUpdatedAt,
                                    creationDate: targetCreatedAt
                                );

                                node.TgMessageId = uploadResult.MessageId;
                                node.TgPreviewMessageId = uploadResult.PreviewMessageId;
                                node.InlineData = null;
                                node.UpdatedAt = targetUpdatedAt;
                                node.CreatedAt = targetCreatedAt;

                                AppLogger.Info("WinFsp", $"Файл '{nodeName}' успешно сохранен в Telegram (Msg ID: {uploadResult.MessageId}" + (uploadResult.PreviewMessageId != null ? $", Preview ID: {uploadResult.PreviewMessageId}" : "") + $", дата изменения: {targetUpdatedAt:yyyy-MM-dd HH:mm:ss}). Загрузка на лету (Zero-Temp) завершена.");
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error("WinFsp", $"Ошибка при завершении потоковой загрузки '{nodeName}' в Telegram: {ex.Message}", ex);
                        }
                        finally
                        {
                            try { pipe.Dispose(); } catch { }
                            try { cts?.Dispose(); } catch { }
                        }
                    });
                }
                // Вариант 2: Небольшой файл (<= 1 МБ), сохраненный в памяти
                else if (ctx.SmallFileBuffer != null)
                {
                    long finalLength = ctx.SmallFileBuffer.Length;
                    byte[] fileBytes = ctx.SmallFileBuffer.ToArray();

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (finalLength <= 1)
                            {
                                byte[]? inlineBytes = finalLength == 1 ? fileBytes : null;
                                _repository.CreateOrUpdateFile(
                                    parentId,
                                    nodeName,
                                    finalLength,
                                    tgMessageId: null,
                                    tgPreviewMessageId: null,
                                    inlineData: inlineBytes,
                                    lastModified: targetUpdatedAt,
                                    creationDate: targetCreatedAt
                                );

                                node.Size = finalLength;
                                node.InlineData = inlineBytes;
                                node.TgMessageId = null;
                                node.UpdatedAt = targetUpdatedAt;
                                node.CreatedAt = targetCreatedAt;

                                AppLogger.Info("WinFsp", $"Файл '{nodeName}' ({finalLength} байт) сохранен в базе данных без отправки в Telegram (inline_data).");
                                return;
                            }

                            int nextVersion = _repository.GetNextVersionForFile(parentId, nodeName);
                            string parentPath = _repository.GetNodeFullPath(parentId);
                            if (parentPath == "/") parentPath = "";
                            string ext = Path.GetExtension(nodeName);
                            string nameNoExt = Path.GetFileNameWithoutExtension(nodeName);
                            string fullPathWithVersion = $"{parentPath}/{nameNoExt}_v{nextVersion}{ext}";

                            AudioMetadataResult? fspAudioMeta = null;
                            VideoMetadataResult? fspVideoMeta = null;

                            if (!string.IsNullOrEmpty(ctx.OriginalSourcePath) && File.Exists(ctx.OriginalSourcePath))
                            {
                                if (VideoMetadataExtractor.IsPotentialVideo(nodeName))
                                {
                                    try { fspVideoMeta = VideoMetadataExtractor.ExtractFromFile(ctx.OriginalSourcePath, nodeName); } catch { }
                                }
                                else if (AudioMetadataExtractor.IsPotentialAudio(nodeName))
                                {
                                    try { fspAudioMeta = AudioMetadataExtractor.ExtractFromFile(ctx.OriginalSourcePath, nodeName); } catch { }
                                }
                            }

                            if (fspAudioMeta == null && AudioMetadataExtractor.IsPotentialAudio(nodeName))
                            {
                                using var ms = new MemoryStream(fileBytes, false);
                                fspAudioMeta = AudioMetadataExtractor.ExtractFromStream(ms, nodeName);
                            }

                            AppLogger.Info("WinFsp", $"Отправка файла '{nodeName}' ({finalLength} байт) из памяти в Telegram (Zero-Temp)...");
                            using var uploadMs = new MemoryStream(fileBytes, false);
                            var uploadResult = await _telegramService.UploadFileAsync(
                                uploadMs,
                                nodeName,
                                finalLength,
                                caption: fullPathWithVersion,
                                audioMeta: fspAudioMeta,
                                videoMeta: fspVideoMeta,
                                originalFilePath: ctx.OriginalSourcePath
                            );

                            if (uploadResult?.MessageId != null)
                            {
                                _repository.CreateOrUpdateFile(
                                    parentId,
                                    nodeName,
                                    finalLength,
                                    uploadResult.MessageId,
                                    uploadResult.PreviewMessageId,
                                    inlineData: null,
                                    lastModified: targetUpdatedAt,
                                    creationDate: targetCreatedAt
                                );

                                node.Size = finalLength;
                                node.TgMessageId = uploadResult.MessageId;
                                node.TgPreviewMessageId = uploadResult.PreviewMessageId;
                                node.InlineData = null;
                                node.UpdatedAt = targetUpdatedAt;
                                node.CreatedAt = targetCreatedAt;

                                AppLogger.Info("WinFsp", $"Файл '{nodeName}' успешно сохранен в Telegram (Msg ID: {uploadResult.MessageId}" + (uploadResult.PreviewMessageId != null ? $", Preview ID: {uploadResult.PreviewMessageId}" : "") + $", дата изменения: {targetUpdatedAt:yyyy-MM-dd HH:mm:ss}).");
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error("WinFsp", $"Ошибка загрузки '{nodeName}' в Telegram: {ex.Message}", ex);
                        }
                    });
                }
            }
        }

        public override void Close(object fileNode, object fileDesc)
        {
            if (fileDesc is FspNodeContext ctx)
            {
                ctx.Dispose();
            }
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
                    if (node.InlineData != null && node.InlineData.Length > 0)
                    {
                        if (offset < (ulong)node.InlineData.Length)
                        {
                            uint count = (uint)Math.Min((ulong)toRead, (ulong)node.InlineData.Length - offset);
                            System.Runtime.InteropServices.Marshal.Copy(node.InlineData, (int)offset, buffer, (int)count);
                            bytesTransferred = count;
                            return STATUS_SUCCESS;
                        }
                    }

                    bytesTransferred = 0;
                    return NT_STATUS_UNSUCCESSFUL;
                }

                unsafe
                {
                    // Прямой стриминг в предоставленный ядром Windows буфер без создания промежуточных файлов на диске C:!
                    using var memStream = new UnmanagedMemoryStream((byte*)buffer.ToPointer(), toRead, toRead, FileAccess.Write);
                    _telegramService.DownloadFileAsync(node.TgMessageId.Value, memStream, (long)offset, (long)toRead, node.Name, node.Size)
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

        private static string GetParentPath(string path)
        {
            string p = NormalizePath(path);
            if (p == "/") return "/";
            int lastSlash = p.LastIndexOf('/');
            if (lastSlash <= 0) return "/";
            return p.Substring(0, lastSlash);
        }

        private static string GetFileName(string path)
        {
            string p = NormalizePath(path);
            if (p == "/") return "";
            int lastSlash = p.LastIndexOf('/');
            return lastSlash >= 0 ? p.Substring(lastSlash + 1) : p;
        }

#region Windows Drag-and-Drop & Clipboard Source Detection (NtHandles & CF_HDROP)

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "DragQueryFileW", SetLastError = true)]
        private static extern uint DragQueryFileCount(IntPtr hDrop, uint iFile, IntPtr lpszFile, uint cch);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "DragQueryFileW", SetLastError = true)]
        private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, [Out] StringBuilder lpszFile, uint cch);

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength, out int ReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandle(IntPtr hFile, [Out] StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        [DllImport("kernel32.dll")]
        private static extern uint GetFileType(IntPtr hFile);

        private const uint CF_HDROP = 15;
        private const int SystemExtendedHandleInformation = 64;
        private const uint FILE_TYPE_DISK = 0x0001;
        private const uint PROCESS_DUP_HANDLE = 0x0040;
        private const uint DUPLICATE_SAME_ACCESS = 0x00000002;
        private const uint VOLUME_NAME_DOS = 0x00000000;

        private static readonly object _sessionLock = new object();
        private static CopySession? _activeSession = null;

        private class CopySession
        {
            public List<string> PendingFiles { get; } = new List<string>();
            public List<string> SourceRoots { get; } = new List<string>();
            public int InitialCount { get; set; }

            public string? FindAndConsume(string targetFileName, string? relativeVirtualPath)
            {
                for (int i = 0; i < PendingFiles.Count; i++)
                {
                    string candidate = PendingFiles[i];
                    string? match = CheckCandidate(candidate, targetFileName, relativeVirtualPath);
                    if (match != null)
                    {
                        PendingFiles.RemoveAt(i);
                        return match;
                    }
                }

                foreach (var root in SourceRoots)
                {
                    string? match = CheckCandidate(root, targetFileName, relativeVirtualPath);
                    if (match != null)
                    {
                        return match;
                    }
                }

                return null;
            }
        }

        private static CopySession CreateCopySession(List<string> rawCandidates)
        {
            var session = new CopySession();
            var addedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in rawCandidates)
            {
                if (File.Exists(raw))
                {
                    if (addedFiles.Add(raw))
                    {
                        session.PendingFiles.Add(raw);
                    }

                    string? dir = Path.GetDirectoryName(raw);
                    if (!string.IsNullOrEmpty(dir) && !session.SourceRoots.Exists(r => string.Equals(r, dir, StringComparison.OrdinalIgnoreCase)))
                    {
                        session.SourceRoots.Add(dir);
                    }
                }
                else if (Directory.Exists(raw))
                {
                    if (!session.SourceRoots.Exists(r => string.Equals(r, raw, StringComparison.OrdinalIgnoreCase)))
                    {
                        session.SourceRoots.Add(raw);
                    }

                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(raw, "*", SearchOption.AllDirectories))
                        {
                            if (addedFiles.Add(file))
                            {
                                session.PendingFiles.Add(file);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("WinFsp", $"[Сессия] Ошибка перечисления файлов папки '{raw}': {ex.Message}");
                    }
                }
            }

            session.InitialCount = session.PendingFiles.Count;
            return session;
        }

        /// <summary>
        /// Детерминированный метод обнаружения пути к оригинальному файлу-источнику (Zero Timers).
        /// Использует честную пофайловую модель сессии копирования (CopySession):
        /// - Перехватывает пути через открытые файловые хэндлы explorer.exe (Drag-and-Drop) и системный буфер CF_HDROP (Ctrl+C/V).
        /// - Без COM/STA-вызовов (100% защита от дедлока Проводника).
        /// - Пофайлово вычеркивает каждый обработанный файл из списка сессии (0 мс доступ для всей пачки).
        /// - Автоматически завершает сессию, как только последний файл пачки взят в обработку.
        /// </summary>
        private static string? TryFindSourceFile(string targetFileName, string? relativeVirtualPath = null)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return null;

            AppLogger.Info("WinFsp", $"[Источник] Поиск оригинального файла для '{targetFileName}' (виртуальный путь: '{relativeVirtualPath}')...");

            lock (_sessionLock)
            {
                // Шаг 1: Проверяем активную сессию (0 мс)
                if (_activeSession != null)
                {
                    string? match = _activeSession.FindAndConsume(targetFileName, relativeVirtualPath);
                    if (match != null)
                    {
                        AppLogger.Info("WinFsp", $"[Источник] Найдено совпадение в активной сессии (осталось файлов: {_activeSession.PendingFiles.Count}/{_activeSession.InitialCount}): '{match}'");
                        if (_activeSession.PendingFiles.Count == 0)
                        {
                            AppLogger.Info("WinFsp", $"[Источник] Все файлы текущей сессии ({_activeSession.InitialCount} шт.) обработаны. Сессия завершена.");
                            _activeSession = null;
                        }
                        return match;
                    }
                }

                // Шаг 2: Файл не входит в активную сессию -> значит началось НОВОЕ копирование!
                AppLogger.Info("WinFsp", $"[Источник] Запрос нового дерева копирования для нового источника...");
                var freshRawCandidates = CollectAllCandidates(targetFileName);
                if (freshRawCandidates.Count > 0)
                {
                    var newSession = CreateCopySession(freshRawCandidates);
                    AppLogger.Info("WinFsp", $"[Источник] Инициализирована новая сессия копирования: файлов в очереди {newSession.PendingFiles.Count}, папок-источников {newSession.SourceRoots.Count}");

                    string? match = newSession.FindAndConsume(targetFileName, relativeVirtualPath);
                    if (match != null)
                    {
                        AppLogger.Info("WinFsp", $"[Источник] Найдено совпадение в новой сессии: '{match}'");
                        // Сохраняем сессию и запоминаем найденный корень источника (Multi-Folder Support!)
                        // Это позволяет следующим файлам из этой же папки мгновенно находиться за 0 мс без опроса дескрипторов!
                        if (_activeSession == null)
                        {
                            _activeSession = newSession;
                        }
                        else
                        {
                            foreach (var r in newSession.SourceRoots)
                            {
                                if (!_activeSession.SourceRoots.Exists(x => string.Equals(x, r, StringComparison.OrdinalIgnoreCase)))
                                {
                                    _activeSession.SourceRoots.Add(r);
                                }
                            }
                        }
                        return match;
                    }
                }

                AppLogger.Warn("WinFsp", $"[Источник] Файл '{targetFileName}' не найден среди доступных источников.");
                return null;
            }
        }

        private static List<string> CollectAllCandidates(string? targetFileName = null)
        {
            var list = new List<string>();

            // 1. Системный буфер обмена Windows (Ctrl+C / Ctrl+V, CF_HDROP)
            // Быстрое чтение структуры памяти без блокирующих вызовов
            var clipList = CollectClipboardCandidates();
            list.AddRange(clipList);

            // 2. Инспекция файловых дескрипторов (Handles) процесса explorer.exe для Drag-and-Drop
            // Работает на уровне ядра через NtQuerySystemInformation, без COM/UI сообщений
            var handleList = CollectExplorerOpenFileHandles(targetFileName);
            list.AddRange(handleList);

            return list;
        }

        private static List<string> CollectClipboardCandidates()
        {
            var results = new List<string>();
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        IntPtr hDrop = GetClipboardData(CF_HDROP);
                        if (hDrop != IntPtr.Zero)
                        {
                            uint fileCount = DragQueryFileCount(hDrop, 0xFFFFFFFFU, IntPtr.Zero, 0);
                            var sb = new StringBuilder(1024);
                            for (uint i = 0; i < fileCount; i++)
                            {
                                sb.Clear();
                                uint len = DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
                                if (len > 0)
                                {
                                    results.Add(sb.ToString());
                                }
                            }
                        }
                        break;
                    }
                    catch { }
                    finally
                    {
                        CloseClipboard();
                    }
                }
                Thread.Sleep(10);
            }
            return results;
        }

        private static ushort? _cachedFileObjectTypeIndex = null;
        private static readonly object _fileTypeIndexLock = new object();

        /// <summary>
        /// Безопасно определяет ObjectTypeIndex для типа 'File' в ядре NT.
        /// Создает временный файловый хэндл в собственном процессе и считывает его ObjectTypeIndex из таблицы NT.
        /// Гарантирует, что DuplicateHandle будет вызываться ТОЛЬКО для реальных дисковых файлов,
        /// полностью исключая зависания ядра на IPC/NamedPipe/Socket хэндлах.
        /// </summary>
        private static ushort? GetFileObjectTypeIndex()
        {
            if (_cachedFileObjectTypeIndex.HasValue)
                return _cachedFileObjectTypeIndex.Value;

            lock (_fileTypeIndexLock)
            {
                if (_cachedFileObjectTypeIndex.HasValue)
                    return _cachedFileObjectTypeIndex.Value;

                string tempFile = Path.GetTempFileName();
                try
                {
                    using (var fs = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        IntPtr myHandle = fs.SafeFileHandle.DangerousGetHandle();
                        int myPid = Process.GetCurrentProcess().Id;

                        int bufferSize = 2 * 1024 * 1024;
                        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
                        try
                        {
                            int status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, bufferSize, out int returnLength);
                            if (status != 0 && returnLength > bufferSize)
                            {
                                Marshal.FreeHGlobal(buffer);
                                bufferSize = returnLength + 256 * 1024;
                                buffer = Marshal.AllocHGlobal(bufferSize);
                                status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, bufferSize, out returnLength);
                            }

                            if (status == 0)
                            {
                                long handleCount = Marshal.ReadInt64(buffer);
                                IntPtr currentPtr = IntPtr.Add(buffer, 16);
                                int entrySize = IntPtr.Size == 8 ? 40 : 28;

                                for (long i = 0; i < handleCount; i++)
                                {
                                    IntPtr entryPtr = IntPtr.Add(currentPtr, (int)(i * entrySize));
                                    int pid = (int)Marshal.ReadInt64(IntPtr.Add(entryPtr, 8));
                                    if (pid == myPid)
                                    {
                                        IntPtr val = (IntPtr)Marshal.ReadInt64(IntPtr.Add(entryPtr, 16));
                                        if (val == myHandle)
                                        {
                                            ushort typeIndex = (ushort)Marshal.ReadInt16(IntPtr.Add(entryPtr, 30));
                                            _cachedFileObjectTypeIndex = typeIndex;
                                            AppLogger.Info("WinFsp", $"[Handles DragDrop] Системный индекс типа 'File' ядра Windows: {typeIndex}");
                                            return typeIndex;
                                        }
                                    }
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(buffer);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("WinFsp", $"[Handles DragDrop] Не удалось определить FileObjectTypeIndex: {ex.Message}");
                }
                finally
                {
                    try { File.Delete(tempFile); } catch { }
                }

                return null;
            }
        }

        /// <summary>
        /// Безопасное получение пути к файлу по дескриптору ядра с жестким таймаутом (50 мс).
        /// Изолирует системный вызов GetFinalPathNameByHandle в выделенном потоке,
        /// гарантируя, что даже при блокировке чужого дескриптора процесс и драйвер WinFsp НИКОГДА не зависнут.
        /// </summary>
        private static string? GetPathWithTimeout(IntPtr targetHandle, int timeoutMs = 50)
        {
            string? result = null;
            var thread = new Thread(() =>
            {
                try
                {
                    if (GetFileType(targetHandle) == FILE_TYPE_DISK)
                    {
                        var sb = new StringBuilder(1024);
                        uint len = GetFinalPathNameByHandle(targetHandle, sb, (uint)sb.Capacity, VOLUME_NAME_DOS);
                        if (len > 0)
                        {
                            result = sb.ToString();
                        }
                    }
                }
                catch { }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };

            thread.Start();
            if (thread.Join(timeoutMs))
            {
                return result;
            }

            return null; // Таймаут: дескриптор заблокирован ядром, не ждем его
        }

        /// <summary>
        /// Безопасное извлечение путей к файлам, открытым процессом explorer.exe при перетаскивании (Drag-and-Drop).
        /// Опрашивает открытые файловые дескрипторы через ядро Windows (NtQuerySystemInformation),
        /// не отправляя оконных сообщений и не обращаясь к OLE/COM, что полностью исключает дедлоки.
        /// </summary>
        private static List<string> CollectExplorerOpenFileHandles(string? targetFileName)
        {
            var results = new List<string>();
            try
            {
                var explorerProcesses = Process.GetProcessesByName("explorer");
                if (explorerProcesses.Length == 0)
                {
                    AppLogger.Debug("WinFsp", "[Handles DragDrop] Процессы explorer.exe не найдены.");
                    return results;
                }

                var explorerPids = new HashSet<int>();
                foreach (var p in explorerProcesses)
                {
                    explorerPids.Add(p.Id);
                }

                AppLogger.Info("WinFsp", $"[Handles DragDrop] Сканирование хэндлов для {explorerPids.Count} процессов explorer (PID: {string.Join(", ", explorerPids)})...");

                // STATUS_INFO_LENGTH_MISMATCH = 0xC0000004
                const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
                int bufferSize = 4 * 1024 * 1024; // 4 MB
                IntPtr buffer = Marshal.AllocHGlobal(bufferSize);

                try
                {
                    int status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, bufferSize, out int returnLength);
                    while ((uint)status == STATUS_INFO_LENGTH_MISMATCH || status != 0)
                    {
                        Marshal.FreeHGlobal(buffer);
                        bufferSize = Math.Max(bufferSize * 2, returnLength + 256 * 1024);
                        if (bufferSize > 64 * 1024 * 1024) // Ограничитель 64 МБ
                        {
                            AppLogger.Warn("WinFsp", $"[Handles DragDrop] Размер буфера превысил лимит ({bufferSize} байт).");
                            return results;
                        }
                        buffer = Marshal.AllocHGlobal(bufferSize);
                        status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, bufferSize, out returnLength);
                        if (status == 0) break;
                        if ((uint)status != STATUS_INFO_LENGTH_MISMATCH)
                        {
                            AppLogger.Warn("WinFsp", $"[Handles DragDrop] NtQuerySystemInformation вернул ошибку NTSTATUS: 0x{status:X8}");
                            return results;
                        }
                    }

                    long handleCount = Marshal.ReadInt64(buffer);
                    ushort? fileTypeIndex = GetFileObjectTypeIndex();
                    AppLogger.Info("WinFsp", $"[Handles DragDrop] В системе обнаружено {handleCount} дескрипторов ядра (File Type Index: {fileTypeIndex?.ToString() ?? "не определен"}).");

                    IntPtr currentPtr = IntPtr.Add(buffer, 16); // Пропуск NumberOfHandles и Reserved (16 байт)
                    int entrySize = IntPtr.Size == 8 ? 40 : 28; // SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX (40 байт на x64, 28 на x86)
                    var processHandles = new Dictionary<int, IntPtr>();
                    var pathSb = new StringBuilder(1024);
                    IntPtr currentProcess = Process.GetCurrentProcess().Handle;

                    int matchedHandles = 0;
                    try
                    {
                        for (long i = 0; i < handleCount; i++)
                        {
                            IntPtr entryPtr = IntPtr.Add(currentPtr, (int)(i * entrySize));
                            int processId = (int)Marshal.ReadInt64(IntPtr.Add(entryPtr, 8)); // UniqueProcessId

                            if (explorerPids.Contains(processId))
                            {
                                // КРИТИЧЕСКИЙ Root Cause Фильтр: проверяем тип объекта в ядре NT перед любыми DuplicateHandle!
                                // Трогаем ТОЛЬКО файловые дескрипторы (никаких Named Pipe, Socket, ALPC Port, Mutex)
                                ushort objectTypeIndex = (ushort)Marshal.ReadInt16(IntPtr.Add(entryPtr, 30));
                                if (fileTypeIndex.HasValue && objectTypeIndex != fileTypeIndex.Value)
                                {
                                    continue; // Мгновенно пропускаем не-файловый дескриптор в памяти ядра
                                }

                                matchedHandles++;
                                IntPtr handleValue = (IntPtr)Marshal.ReadInt64(IntPtr.Add(entryPtr, 16)); // HandleValue

                                if (!processHandles.TryGetValue(processId, out IntPtr hProcess))
                                {
                                    hProcess = OpenProcess(PROCESS_DUP_HANDLE, false, processId);
                                    if (hProcess == IntPtr.Zero)
                                    {
                                        int err = Marshal.GetLastWin32Error();
                                        AppLogger.Debug("WinFsp", $"[Handles DragDrop] Не удалось открыть процесс explorer PID {processId}: Win32 Error {err}");
                                    }
                                    processHandles[processId] = hProcess;
                                }

                                if (hProcess != IntPtr.Zero)
                                {
                                    if (DuplicateHandle(hProcess, handleValue, currentProcess, out IntPtr targetHandle, 0, false, DUPLICATE_SAME_ACCESS))
                                    {
                                        try
                                        {
                                            string? rawPath = GetPathWithTimeout(targetHandle, 50);
                                            if (!string.IsNullOrEmpty(rawPath))
                                            {
                                                string path = rawPath;
                                                if (path.StartsWith(@"\\?\"))
                                                {
                                                    path = path.Substring(4);
                                                }

                                                if (File.Exists(path) || Directory.Exists(path))
                                                {
                                                    string name = Path.GetFileName(path);
                                                    if (string.IsNullOrEmpty(targetFileName) ||
                                                        string.Equals(name, targetFileName, StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        if (!results.Exists(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase)))
                                                        {
                                                            AppLogger.Info("WinFsp", $"[Handles DragDrop] Обнаружен открытый файл в explorer (PID {processId}): '{path}'");
                                                            results.Add(path);

                                                            // Если мы искали конкретный целевой файл и нашли его,
                                                            // МГНОВЕННО прекращаем опрос остальных дескрипторов (Fast-Break)
                                                            if (!string.IsNullOrEmpty(targetFileName))
                                                            {
                                                                return results;
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        catch { }
                                        finally
                                        {
                                            CloseHandle(targetHandle);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        foreach (var kvp in processHandles)
                        {
                            if (kvp.Value != IntPtr.Zero)
                            {
                                CloseHandle(kvp.Value);
                            }
                        }
                    }

                    AppLogger.Info("WinFsp", $"[Handles DragDrop] Проверено дескрипторов Explorer: {matchedHandles}, найдено совпадений файлов: {results.Count}");
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("WinFsp", $"[Handles DragDrop] Исключение при сканировании хэндлов: {ex.Message}");
            }

            return results;
        }

        private static string? CheckCandidate(string candidatePath, string targetFileName, string? relativeVirtualPath)
        {
            if (File.Exists(candidatePath))
            {
                string candidateName = Path.GetFileName(candidatePath);
                if (string.Equals(candidateName, targetFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return VerifyReadable(candidatePath);
                }
                return null;
            }

            if (Directory.Exists(candidatePath))
            {
                // Сценарий 1: Прямой дочерний файл в скопированной папке
                string directChild = Path.Combine(candidatePath, targetFileName);
                if (File.Exists(directChild))
                {
                    return VerifyReadable(directChild);
                }

                // Сценарий 2: По относительному пути, если копировалось дерево папок
                if (!string.IsNullOrEmpty(relativeVirtualPath))
                {
                    string cleanVirtual = relativeVirtualPath.Trim('/', '\\').Replace('/', Path.DirectorySeparatorChar);
                    string candidateDirName = Path.GetFileName(candidatePath);
                    int idx = cleanVirtual.IndexOf(candidateDirName, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        string sub = cleanVirtual.Substring(idx + candidateDirName.Length).TrimStart(Path.DirectorySeparatorChar);
                        string candidateWithSub = Path.Combine(candidatePath, sub);
                        if (File.Exists(candidateWithSub))
                        {
                            return VerifyReadable(candidateWithSub);
                        }
                    }
                }

                // Сценарий 3: Поиск в поддиректориях папки
                try
                {
                    foreach (var file in Directory.EnumerateFiles(candidatePath, targetFileName, SearchOption.AllDirectories))
                    {
                        var match = VerifyReadable(file);
                        if (match != null) return match;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("WinFsp", $"[Источник] Ошибка перечисления файлов в папке '{candidatePath}': {ex.Message}");
                }
            }

            return null;
        }

        private static string? VerifyReadable(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                AppLogger.Info("WinFsp", $"[Источник] НАЙДЕНО СОВПАДЕНИЕ! Оригинальный файл '{filePath}' существует и доступен для чтения (размер: {fs.Length} байт).");
                return filePath;
            }
            catch (Exception fsEx)
            {
                AppLogger.Warn("WinFsp", $"[Источник] НАЙДЕНО СОВПАДЕНИЕ, но файл '{filePath}' заблокирован другим процессом: {fsEx.Message}");
                return filePath;
            }
        }

        #endregion

        private class FspNodeContext : IDisposable
        {
            public Node Node { get; set; }
            public bool IsModified { get; set; }
            public bool DeleteOnClose { get; set; }

            // Прямая потоковая загрузка без временных файлов на диске (Zero-Temp Direct Streaming)
            public StreamingPipeStream? PipeStream { get; set; }
            public Task<FileUploadResult?>? UploadTask { get; set; }
            public CancellationTokenSource? UploadCts { get; set; }
            public MemoryStream? SmallFileBuffer { get; set; }
            public long KnownTargetSize { get; set; } = -1;
            public long TotalBytesWritten { get; set; } = 0;
            public bool IsUploadStarted { get; set; } = false;
            public string? OriginalSourcePath { get; set; }

            public FspNodeContext(Node node) => Node = node;

            public void Dispose()
            {
                if (DeleteOnClose)
                {
                    try { UploadCts?.Cancel(); } catch { }
                    try { PipeStream?.Abort(); } catch { }
                }

                try { SmallFileBuffer?.Dispose(); } catch { }
                SmallFileBuffer = null;
            }
        }

        private class FspDirectoryEnumContext
        {
            public List<Node> Items { get; }
            public int Index { get; set; }
            public FspDirectoryEnumContext(List<Node> items) => Items = items;
        }
    }
}
