using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using TelegramWebDAV.Database;
using TelegramWebDAV.Models;
using TelegramWebDAV.Services;

namespace TelegramWebDAV.Server
{
    public static class WebDavMiddleware
    {
        public static Task HandleOptionsAsync(HttpListenerContext context)
        {
            // OPTIONS сообщает клиентам (в т.ч. Проводнику Windows), какие методы поддерживаются
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            // Allow заголовки уже добавлены в основном пайплайне
            return Task.CompletedTask;
        }

        public static async Task HandlePropfindAsync(HttpListenerContext context, NodeRepository repository, bool hideTrashFromRoot = true)
        {
            // Извлекаем путь (убирая параметры запроса)
            string localPath = context.Request.Url?.LocalPath ?? "/";
            string path = Uri.UnescapeDataString(localPath);
            
            // Если Проводник Windows запрашивает desktop.ini для папки, виртуально отдаем FolderType=Generic
            if (path.EndsWith("/desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                byte[] iniBytes = Encoding.UTF8.GetBytes("[.ShellClassInfo]\r\nFolderType=Generic\r\n[ViewState]\r\nFolderType=Generic\r\n");
                context.Response.StatusCode = 207;
                context.Response.ContentType = "text/xml; charset=\"utf-8\"";
                XNamespace dIni = "DAV:";
                var propIni = new XElement(dIni + "prop",
                    new XElement(dIni + "creationdate", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")),
                    new XElement(dIni + "getlastmodified", DateTime.UtcNow.ToString("R")),
                    new XElement(dIni + "resourcetype"),
                    new XElement(dIni + "getcontentlength", iniBytes.Length),
                    new XElement(dIni + "getcontenttype", "text/plain")
                );
                var propstatIni = new XElement(dIni + "propstat", propIni, new XElement(dIni + "status", "HTTP/1.1 200 OK"));
                string escapedHrefIni = string.Join("/", Array.ConvertAll(path.Split('/'), Uri.EscapeDataString));
                var responseIni = new XElement(dIni + "response", new XElement(dIni + "href", escapedHrefIni), propstatIni);
                var multistatusIni = new XElement(dIni + "multistatus", new XAttribute(XNamespace.Xmlns + "D", dIni.NamespaceName), responseIni);
                var docIni = new XDocument(new XDeclaration("1.0", "utf-8", null), multistatusIni);
                using (var msIni = new MemoryStream())
                {
                    docIni.Save(msIni);
                    byte[] buffer = msIni.ToArray();
                    context.Response.ContentLength64 = buffer.Length;
                    await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                }
                return;
            }

            // WebDAV обычно запрашивает Depth=1 (папка + прямые дети) или Depth=0 (только сам элемент)
            int depth = 1; 
            string? depthHeader = context.Request.Headers["Depth"];
            if (depthHeader == "0") depth = 0;
            else if (depthHeader == "1") depth = 1;

            // Находим целевой элемент в базе SQLite
            var targetNode = repository.GetNodeByPath(path);
            
            if (targetNode == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            // ВАЖНО: Windows WebDAV требует статус 207 Multi-Status
            context.Response.StatusCode = 207;
            context.Response.ContentType = "text/xml; charset=\"utf-8\"";
            
            XNamespace d = "DAV:";
            var responseElements = new List<XElement>();
            
            // 1. Добавляем сам целевой элемент в XML
            responseElements.Add(CreateResponseElement(d, path, targetNode));

            // 2. Если запрошен Depth=1 и это папка, добавляем её внутренности (детей)
            if (depth == 1 && targetNode.IsDir)
            {
                var children = repository.GetChildren(targetNode.Id);
                foreach (var child in children)
                {
                    // Если включено скрытие корзины и мы находимся в корневом каталоге диска, пропускаем .Trash
                    if (hideTrashFromRoot && targetNode.ParentId == null && (child.Name.Equals(".Trash", StringComparison.OrdinalIgnoreCase) || repository.IsTrashFolder(child.Id)))
                    {
                        continue;
                    }

                    // Формируем URL путь для дочернего элемента
                    string childPath = path.TrimEnd('/') + "/" + child.Name;
                    responseElements.Add(CreateResponseElement(d, childPath, child));
                }
            }

            // Оборачиваем в корневой тэг <D:multistatus>
            var multistatus = new XElement(d + "multistatus",
                new XAttribute(XNamespace.Xmlns + "D", d.NamespaceName),
                responseElements
            );

            // Конвертируем XDocument в байты и отправляем
            var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), multistatus);
            using (var ms = new MemoryStream())
            {
                doc.Save(ms);
                byte[] buffer = ms.ToArray();
                context.Response.ContentLength64 = buffer.Length;
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
        }

        private static XElement CreateResponseElement(XNamespace d, string path, Node node)
        {
            // Дата создания (ISO 8601) и дата изменения (RFC 1123)
            var creationDate = node.CreatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
            var lastModified = node.UpdatedAt.ToUniversalTime().ToString("R");

            var prop = new XElement(d + "prop",
                new XElement(d + "creationdate", creationDate),
                new XElement(d + "getlastmodified", lastModified)
            );

            if (node.IsDir)
            {
                prop.Add(new XElement(d + "resourcetype", new XElement(d + "collection")));
            }
            else
            {
                prop.Add(new XElement(d + "resourcetype"));
                prop.Add(new XElement(d + "getcontentlength", node.Size));
                prop.Add(new XElement(d + "getcontenttype", "application/octet-stream"));
            }

            var propstat = new XElement(d + "propstat",
                prop,
                new XElement(d + "status", "HTTP/1.1 200 OK")
            );

            // Безопасное экранирование сегментов URL
            string escapedHref = string.Join("/", Array.ConvertAll(path.Split('/'), Uri.EscapeDataString));

            return new XElement(d + "response",
                new XElement(d + "href", escapedHref),
                propstat
            );
        }

        private static bool GetParentPathAndName(string fullPath, out string parentPath, out string name)
        {
            parentPath = "/";
            name = "";
            fullPath = fullPath.TrimEnd('/');
            if (string.IsNullOrEmpty(fullPath)) return false; // Корневая папка

            int lastSlash = fullPath.LastIndexOf('/');
            if (lastSlash < 0) return false;

            name = fullPath.Substring(lastSlash + 1);
            parentPath = fullPath.Substring(0, lastSlash);
            if (string.IsNullOrEmpty(parentPath)) parentPath = "/";

            return true;
        }

        public static async Task HandleGetAsync(HttpListenerContext context, NodeRepository repository, TelegramService telegramService)
        {
            string localPath = context.Request.Url?.LocalPath ?? "/";
            string path = Uri.UnescapeDataString(localPath);

            // Виртуальный desktop.ini для поддержки вида 'Общие элементы' в проводнике
            if (path.EndsWith("/desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                byte[] iniBytes = Encoding.UTF8.GetBytes("[.ShellClassInfo]\r\nFolderType=Generic\r\n[ViewState]\r\nFolderType=Generic\r\n");
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.ContentLength64 = iniBytes.Length;
                await context.Response.OutputStream.WriteAsync(iniBytes, 0, iniBytes.Length);
                context.Response.OutputStream.Close();
                return;
            }

            var node = repository.GetNodeByPath(path);
            if (node == null || node.IsDir)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            long totalSize = node.Size;
            long start = 0;
            long end = totalSize - 1;
            bool isRange = false;

            string? rangeHeader = context.Request.Headers["Range"];
            string userAgent = context.Request.UserAgent ?? "Неизвестный клиент";

            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
            {
                isRange = true;
                string[] ranges = rangeHeader.Substring(6).Split('-');
                if (ranges.Length >= 1 && long.TryParse(ranges[0], out long parsedStart))
                    start = parsedStart;
                if (ranges.Length >= 2 && long.TryParse(ranges[1], out long parsedEnd))
                    end = parsedEnd;
                else
                    end = totalSize - 1;
            }

            if (start >= totalSize || end >= totalSize || start > end)
            {
                AppLogger.Warn("WebDAV", $"[GET] Недопустимый Range '{rangeHeader}' для '{node.Name}' (размер {totalSize}) | Клиент: {userAgent}");
                context.Response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                context.Response.AddHeader("Content-Range", $"bytes */{totalSize}");
                return;
            }

            long length = end - start + 1;

            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength64 = length;
            context.Response.AddHeader("Accept-Ranges", "bytes");

            if (isRange)
            {
                context.Response.StatusCode = (int)HttpStatusCode.PartialContent;
                context.Response.AddHeader("Content-Range", $"bytes {start}-{end}/{totalSize}");
                AppLogger.Info("WebDAV", $"[GET Range] '{node.Name}' | Запрос диапазона: {rangeHeader} (смещение {start}, длина {length} из {totalSize} байт) | В БД HeaderCache: {node.HeaderCacheBytes?.Length ?? 0} байт | Клиент: {userAgent}");
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                AppLogger.Info("WebDAV", $"[GET Full] '{node.Name}' | Запрос ПОЛНОГО файла ({totalSize} байт, Range отсутствует) | В БД HeaderCache: {node.HeaderCacheBytes?.Length ?? 0} байт | Клиент: {userAgent}");
            }

            // 1. Проверяем попадание в HeaderCacheBytes (предварительно сохраненный в SQLite заголовок)
            if (node.HeaderCacheBytes != null && node.HeaderCacheBytes.Length > 0)
            {
                // Сценарий А: Range-запрос полностью укладывается в размер HeaderCache (например, чтение первых 64 КБ)
                if (isRange && start >= 0 && end < node.HeaderCacheBytes.Length)
                {
                    int offset = (int)start;
                    int count = (int)length;
                    await context.Response.OutputStream.WriteAsync(node.HeaderCacheBytes, offset, count);
                    await context.Response.OutputStream.FlushAsync();
                    AppLogger.Info("WebDAV", $"[GET HeaderCache HIT] '{node.Name}' диапазон {start}-{end} ({count} байт) отдан мгновенно из локальной БД SQLite.");
                    try { context.Response.OutputStream.Close(); } catch { }
                    return;
                }

                // Сценарий Б: Полный GET или Range с начала файла.
                // Мгновенно отдаем первые 128 КБ заголовка клиенту из SQLite прямо в поток!
                // Если Проводник или AIMP запрашивал свойства для всплывающей подсказки (InfoTip),
                // он распарсит теги и сразу закроет сокет, избежав обращения к Telegram!
                if (start == 0 && node.TgMessageId.HasValue)
                {
                    int headerLen = Math.Min((int)length, node.HeaderCacheBytes.Length);
                    try
                    {
                        await context.Response.OutputStream.WriteAsync(node.HeaderCacheBytes, 0, headerLen);
                        await context.Response.OutputStream.FlushAsync();
                        AppLogger.Info("WebDAV", $"[GET Pre-Stream] '{node.Name}': первые {headerLen} байт заголовка мгновенно отданы клиенту из SQLite.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("WebDAV", $"Клиент закрыл соединение при отправке заголовка '{node.Name}': {ex.Message}");
                        return;
                    }

                    // Даем короткую паузу 150 мс на случай, если Проводник только читал теги для подсказки и уже закрыл дескриптор файла
                    await Task.Delay(150);

                    // Продолжаем скачивание остатка файла через TelegramService
                    try
                    {
                        await telegramService.DownloadFileAsync(node.TgMessageId.Value, context.Response.OutputStream, start, length, alreadySentBytes: headerLen);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("WebDAV", $"Сквозная передача '{node.Name}' завершена/прервана: {ex.Message}");
                    }
                    try { context.Response.OutputStream.Close(); } catch { }
                    return;
                }
            }

            if (node.TgMessageId.HasValue)
            {
                await telegramService.DownloadFileAsync(node.TgMessageId.Value, context.Response.OutputStream, start, length);
            }
            else if (node.HeaderCacheBytes != null && node.HeaderCacheBytes.Length > 0)
            {
                int offset = (int)start;
                int count = (int)Math.Min(length, node.HeaderCacheBytes.Length - offset);
                if (count > 0 && offset < node.HeaderCacheBytes.Length)
                {
                    await context.Response.OutputStream.WriteAsync(node.HeaderCacheBytes, offset, count);
                }
            }
            
            try { context.Response.OutputStream.Close(); } catch { }
        }
        
        public static Task HandleHeadAsync(HttpListenerContext context)
        {
            // HEAD работает так же как GET, но возвращает только заголовки (размер файла)
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return Task.CompletedTask;
        }

        public static async Task HandlePutAsync(HttpListenerContext context, NodeRepository repository, TelegramService telegramService)
        {
            string localPath = context.Request.Url?.LocalPath ?? "/";
            string path = Uri.UnescapeDataString(localPath);

            // Прямая запись/загрузка файлов в корзину запрещена
            if (path.Equals("/.Trash", StringComparison.OrdinalIgnoreCase) || 
                path.StartsWith("/.Trash/", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Warn("WebDAV", $"Попытка прямой записи в корзину отклонена: {path}");
                context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                context.Response.OutputStream.Close();
                return;
            }

            if (!GetParentPathAndName(path, out string parentPath, out string name))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Conflict;
                return;
            }

            var parentNode = repository.GetNodeByPath(parentPath);
            if (parentNode == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.Conflict;
                return;
            }

            if (repository.IsNodeInTrash(parentNode.Id))
            {
                AppLogger.Warn("WebDAV", $"Попытка записи файла в подкаталог корзины отклонена: {path}");
                context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                context.Response.OutputStream.Close();
                return;
            }

            long contentLength = context.Request.ContentLength64;
            string? contentRangeHeader = context.Request.Headers["Content-Range"];
            long offset = 0;
            long totalSize = contentLength;
            bool isResumableChunk = false;

            // Поддержка умного софта для бэкапа и клиентов с докачкой (например, Rclone, Cyberduck, GoodSync)
            // Заголовок формата: Content-Range: bytes 500000-999999/2000000
            if (!string.IsNullOrEmpty(contentRangeHeader) && contentRangeHeader.StartsWith("bytes "))
            {
                try
                {
                    string rangePart = contentRangeHeader.Substring(6).Trim(); // "500000-999999/2000000"
                    string[] parts = rangePart.Split('/');
                    if (parts.Length == 2)
                    {
                        if (long.TryParse(parts[1], out long parsedTotal))
                        {
                            totalSize = parsedTotal;
                        }

                        string[] byteBounds = parts[0].Split('-');
                        if (byteBounds.Length == 2 && long.TryParse(byteBounds[0], out long parsedStart))
                        {
                            offset = parsedStart;
                            isResumableChunk = true;
                        }
                    }
                }
                catch
                {
                    // Если заголовок поврежден, падаем обратно на обычную загрузку
                    offset = 0;
                    totalSize = contentLength;
                    isResumableChunk = false;
                }
            }

            try 
            {
                if (isResumableChunk)
                {
                    // Загружаем чанк в Telegram
                    int? tgMessageId = await telegramService.UploadFileChunkAsync(context.Request.InputStream, name, offset, totalSize);
                    
                    // Обновляем позицию докачки и статус в SQLite
                    repository.UpdateUploadProgress(parentNode.Id, name, offset + contentLength, totalSize, tgMessageId);

                    // Если файл полностью догружен последним куском - 201 Created / 204 No Content, иначе 200/206
                    if (tgMessageId.HasValue || (offset + contentLength >= totalSize))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.Created;
                    }
                    else
                    {
                        // Подтверждаем принятие диапазона чанка для клиента бэкапа
                        context.Response.StatusCode = (int)HttpStatusCode.Accepted; // 202 Accepted
                        context.Response.AddHeader("Range", $"bytes=0-{offset + contentLength - 1}");
                    }
                }
                else
                {
                    // Проверяем, является ли загружаемый файл аудио
                    AudioMetadataResult? audioMeta = null;
                    Stream uploadStream = context.Request.InputStream;
                    long uploadLength = contentLength;
                    string? audioTempPath = null;

                    if (AudioMetadataExtractor.IsAudioFile(name))
                    {
                        // Для аудиофайлов до 10 МБ буферизуем в MemoryStream
                        if (contentLength > 0 && contentLength <= 10 * 1024 * 1024)
                        {
                            var ms = new MemoryStream();
                            await context.Request.InputStream.CopyToAsync(ms);
                            ms.Position = 0;

                            audioMeta = AudioMetadataExtractor.ExtractFromStream(ms, name);
                            ms.Position = 0; // Перематываем назад перед отправкой

                            uploadStream = ms;
                            uploadLength = ms.Length;
                        }
                        else
                        {
                            // Для больших аудиофайлов (более 10 МБ, например, часовые FLAC/MP3 миксы)
                            // пишем во временный файл, чтобы не перегружать ОЗУ, считываем теги, а потом льем в Telegram
                            string tempDir = Path.Combine(Path.GetTempPath(), "TelegramWebDAV_AudioTemp");
                            Directory.CreateDirectory(tempDir);
                            audioTempPath = Path.Combine(tempDir, $"{Guid.NewGuid()}_{name}");

                            using (var fs = new FileStream(audioTempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                await context.Request.InputStream.CopyToAsync(fs);
                            }

                            var fileStreamForMeta = new FileStream(audioTempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                            audioMeta = AudioMetadataExtractor.ExtractFromStream(fileStreamForMeta, name);
                            fileStreamForMeta.Position = 0;

                            uploadStream = fileStreamForMeta;
                            uploadLength = fileStreamForMeta.Length;
                        }
                    }

                    try
                    {
                        byte[]? inlineBytes = null;
                        int? tgMessageId = null;

                        // Если размер файла <= 1 байт (пустой плейсхолдер Проводника или probe Total Commander)
                        if (uploadLength <= 1)
                        {
                            if (uploadLength == 1)
                            {
                                using (var ms = new MemoryStream())
                                {
                                    await uploadStream.CopyToAsync(ms);
                                    inlineBytes = ms.ToArray();
                                }
                            }
                            // Не отправляем в Telegram, оставляем tgMessageId = null
                            AppLogger.Info("WebDAV", $"Запрос PUT для '{name}' размера {uploadLength} байт зарегистрирован локально без загрузки в Telegram.");
                        }
                        else
                        {
                            // Стандартный монолитный PUT от обычного Проводника Windows или Total Commander.
                            // Если это НЕ аудиофайл, то внутри UploadFileAsync сработает его собственная
                            // надежная гибридная буферизация (RAM для мелких, диск для крупных).
                            tgMessageId = await telegramService.UploadFileAsync(uploadStream, name, uploadLength);
                        }
                        
                        // Записываем инфу в базу с метаданными и встроенными байтами при необходимости
                        repository.CreateOrUpdateFile(parentNode.Id, name, totalSize, tgMessageId, audioMeta, inlineBytes);

                        context.Response.StatusCode = (int)HttpStatusCode.Created;
                    }
                    finally
                    {
                        if (uploadStream != context.Request.InputStream)
                        {
                            try { uploadStream.Dispose(); } catch { }
                        }
                        if (audioTempPath != null)
                        {
                            try { File.Delete(audioTempPath); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("WebDAV", $"Ошибка загрузки файла {name}: {ex.Message}", ex);
                // В случае ошибки возвращаем 500. Проводник или софт бэкапа перехватят и повторят
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
            finally
            {
                context.Response.OutputStream.Close();
            }
        }

        public static Task HandleMkColAsync(HttpListenerContext context, NodeRepository repository)
        {
            string localPath = context.Request.Url?.LocalPath ?? "/";
            string path = Uri.UnescapeDataString(localPath);

            // Создание папок напрямую в корзине запрещено
            if (path.Equals("/.Trash", StringComparison.OrdinalIgnoreCase) || 
                path.StartsWith("/.Trash/", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Warn("WebDAV", $"Попытка создания папки в корзине отклонена: {path}");
                context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return Task.CompletedTask;
            }

            if (!GetParentPathAndName(path, out string parentPath, out string name))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Conflict; // Нельзя создать корень
                return Task.CompletedTask;
            }

            var parentNode = repository.GetNodeByPath(parentPath);
            if (parentNode == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.Conflict; // Нет родительской папки
                return Task.CompletedTask;
            }

            if (repository.IsNodeInTrash(parentNode.Id))
            {
                AppLogger.Warn("WebDAV", $"Попытка создания папки внутри подкаталога корзины отклонена: {path}");
                context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return Task.CompletedTask;
            }

            bool created = repository.CreateFolder(parentNode.Id, name);
            context.Response.StatusCode = created ? (int)HttpStatusCode.Created : (int)HttpStatusCode.MethodNotAllowed;
            return Task.CompletedTask;
        }

        public static async Task HandleDeleteAsync(HttpListenerContext context, NodeRepository repository, Services.TelegramService telegramService)
        {
            string localPath = context.Request.Url?.LocalPath ?? "/";
            string path = Uri.UnescapeDataString(localPath);
            var node = repository.GetNodeByPath(path);

            if (node == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            // Защита системной папки .Trash от удаления снаружи (например, при Ctrl+A в корне):
            // Папка корзины защищена от удаления; возвращаем 204 No Content, чтобы клиент продолжил без ошибок
            if (repository.IsTrashFolder(node.Id) || path.TrimEnd('/').Equals("/.Trash", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Warn("WebDAV", "Запрос на удаление папки '.Trash' отклонен: системная корзина защищена от удаления.");
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }

            // Проверяем, находится ли файл уже в корзине или помечен ли он как удаленный.
            // Но также, если путь начинается с "/.Trash/", то это перманентное удаление содержимого корзины!
            bool isPermanent = node.IsDeleted || path.StartsWith("/.Trash/", StringComparison.OrdinalIgnoreCase);

            if (isPermanent)
            {
                // По рекурсии получаем все дочерние узлы, если это папка, чтобы очистить их файлы в Telegram
                var nodesToDelete = new List<Models.Node> { node };
                GetNodesRecursive(node, repository, nodesToDelete);

                // Собираем все непустые ID сообщений в Telegram для пакетного удаления
                var tgMessageIds = new List<int>();
                var dbNodeIds = new List<int>();

                foreach (var n in nodesToDelete)
                {
                    dbNodeIds.Add(n.Id);
                    if (n.TgMessageId.HasValue && n.TgMessageId.Value > 0)
                    {
                        tgMessageIds.Add(n.TgMessageId.Value);
                    }
                }

                if (tgMessageIds.Count > 0)
                {
                    AppLogger.Info("WebDAV", $"Перманентное удаление: сначала пакетно удаляем {tgMessageIds.Count} сообщений из Telegram...");
                    bool tgSuccess = await telegramService.DeleteFilesFromTelegramAsync(tgMessageIds);
                    if (!tgSuccess)
                    {
                        AppLogger.Error("WebDAV", "Сбой при удалении файлов из Telegram. Отменяем удаление из базы данных, чтобы избежать расхождений.");
                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                        return;
                    }
                }

                repository.PermanentDeleteNodes(dbNodeIds);
                AppLogger.Info("WebDAV", $"Успешно удалено {dbNodeIds.Count} узлов из базы данных навсегда.");
            }
            else
            {
                // Обычное мягкое удаление в корзину
                repository.SoftDeleteNode(node.Id);
                AppLogger.Info("WebDAV", $"Узел '{node.Name}' перемещен в корзину (.Trash).");
            }

            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
        }

        private static void GetNodesRecursive(Models.Node parentNode, NodeRepository repository, List<Models.Node> result)
        {
            if (!parentNode.IsDir) return;
            var children = repository.GetChildren(parentNode.Id);
            foreach (var child in children)
            {
                result.Add(child);
                if (child.IsDir)
                {
                    GetNodesRecursive(child, repository, result);
                }
            }
        }

        public static Task HandleMoveAsync(HttpListenerContext context, NodeRepository repository)
        {
            string localPath = context.Request.Url?.LocalPath ?? "/";
            string path = Uri.UnescapeDataString(localPath);
            var sourceNode = repository.GetNodeByPath(path);

            if (sourceNode == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return Task.CompletedTask;
            }

            // Защита системной папки .Trash от перемещения или переименования
            if (repository.IsTrashFolder(sourceNode.Id) || path.TrimEnd('/').Equals("/.Trash", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Warn("WebDAV", "Попытка переименования или перемещения системной папки '.Trash' отклонена.");
                context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return Task.CompletedTask;
            }

            string? destinationHeader = context.Request.Headers["Destination"];
            if (string.IsNullOrEmpty(destinationHeader))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return Task.CompletedTask;
            }

            Uri destUri = new Uri(destinationHeader);
            string destPath = Uri.UnescapeDataString(destUri.LocalPath);

            if (!GetParentPathAndName(destPath, out string destParentPath, out string destName))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Conflict;
                return Task.CompletedTask;
            }

            var destParentNode = repository.GetNodeByPath(destParentPath);
            if (destParentNode == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.Conflict;
                return Task.CompletedTask;
            }

            repository.MoveNode(sourceNode.Id, destParentNode.Id, destName);
            context.Response.StatusCode = (int)HttpStatusCode.Created;
            return Task.CompletedTask;
        }

        public static async Task HandleLockAsync(HttpListenerContext context)
        {
            string localPath = context.Request.Url?.LocalPath ?? "/";
            string path = Uri.UnescapeDataString(localPath);
            
            // Генерируем уникальный токен блокировки
            string lockToken = "opaquelocktoken:" + Guid.NewGuid().ToString();
            
            // Windows требует возврата заголовка Lock-Token
            context.Response.AddHeader("Lock-Token", $"<{lockToken}>");
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "text/xml; charset=\"utf-8\"";

            XNamespace d = "DAV:";
            var activeLock = new XElement(d + "activelock",
                new XElement(d + "locktype", new XElement(d + "write")),
                new XElement(d + "lockscope", new XElement(d + "exclusive")),
                new XElement(d + "depth", "Infinity"),
                new XElement(d + "timeout", "Second-3600"),
                new XElement(d + "locktoken", new XElement(d + "href", lockToken)),
                new XElement(d + "lockroot", new XElement(d + "href", string.Join("/", Array.ConvertAll(path.Split('/'), Uri.EscapeDataString))))
            );

            var prop = new XElement(d + "prop",
                new XElement(d + "lockdiscovery", activeLock)
            );

            var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), prop);
            using (var ms = new MemoryStream())
            {
                doc.Save(ms);
                byte[] buffer = ms.ToArray();
                context.Response.ContentLength64 = buffer.Length;
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
        }

        public static Task HandleUnlockAsync(HttpListenerContext context)
        {
            // UNLOCK возвращает статус 204 No Content в случае успеха
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
            return Task.CompletedTask;
        }

        public static async Task HandleProppatchAsync(HttpListenerContext context)
        {
            string localPath = context.Request.Url?.LocalPath ?? "/";
            string path = Uri.UnescapeDataString(localPath);

            string requestBody = "";
            try
            {
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                {
                    requestBody = await reader.ReadToEndAsync();
                }
            }
            catch { }

            context.Response.StatusCode = 207; // Multi-Status
            context.Response.ContentType = "text/xml; charset=\"utf-8\"";

            XNamespace d = "DAV:";
            var propElements = new List<XElement>();
            try
            {
                if (!string.IsNullOrEmpty(requestBody))
                {
                    var xdoc = XDocument.Parse(requestBody);
                    var setProps = xdoc.Descendants(d + "prop").Descendants();
                    foreach (var prop in setProps)
                    {
                        propElements.Add(new XElement(prop.Name));
                    }
                }
            }
            catch { }

            if (propElements.Count == 0)
            {
                propElements.Add(new XElement(d + "getlastmodified"));
            }

            var propstat = new XElement(d + "propstat",
                new XElement(d + "prop", propElements),
                new XElement(d + "status", "HTTP/1.1 200 OK")
            );

            string escapedHref = string.Join("/", Array.ConvertAll(path.Split('/'), Uri.EscapeDataString));

            var responseElement = new XElement(d + "response",
                new XElement(d + "href", escapedHref),
                propstat
            );

            var multistatus = new XElement(d + "multistatus",
                new XAttribute(XNamespace.Xmlns + "D", d.NamespaceName),
                responseElement
            );

            var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), multistatus);
            using (var ms = new MemoryStream())
            {
                doc.Save(ms);
                byte[] buffer = ms.ToArray();
                context.Response.ContentLength64 = buffer.Length;
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
        }
    }
}
