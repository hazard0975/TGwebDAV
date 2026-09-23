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

        public static async Task HandlePropfindAsync(HttpListenerContext context, NodeRepository repository)
        {
            // Извлекаем путь (убирая параметры запроса)
            string localPath = context.Request.Url?.LocalPath ?? "/";
            string path = Uri.UnescapeDataString(localPath);
            
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
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.OK;
            }

            if (node.TgMessageId.HasValue)
            {
                await telegramService.DownloadFileAsync(node.TgMessageId.Value, context.Response.OutputStream, start, length);
            }
            
            context.Response.OutputStream.Close();
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
                    if (AudioMetadataExtractor.IsAudioFile(name))
                    {
                        audioMeta = AudioMetadataExtractor.ExtractFromStream(context.Request.InputStream, name);
                    }

                    // Стандартный монолитный PUT от обычного Проводника Windows
                    int tgMessageId = await telegramService.UploadFileAsync(context.Request.InputStream, name);
                    
                    // Записываем инфу в базу с метаданными
                    repository.CreateOrUpdateFile(parentNode.Id, name, totalSize, tgMessageId, audioMeta);

                    context.Response.StatusCode = (int)HttpStatusCode.Created;
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

            bool created = repository.CreateFolder(parentNode.Id, name);
            context.Response.StatusCode = created ? (int)HttpStatusCode.Created : (int)HttpStatusCode.MethodNotAllowed;
            return Task.CompletedTask;
        }

        public static Task HandleDeleteAsync(HttpListenerContext context, NodeRepository repository)
        {
            string localPath = context.Request.Url?.LocalPath ?? "/";
            string path = Uri.UnescapeDataString(localPath);
            var node = repository.GetNodeByPath(path);

            if (node == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return Task.CompletedTask;
            }

            // Мягкое удаление (в корзину)
            repository.SoftDeleteNode(node.Id);
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
            return Task.CompletedTask;
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
    }
}
