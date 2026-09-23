using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using TelegramWebDAV.Database;
using TelegramWebDAV.Config;
using TelegramWebDAV.Services;

namespace TelegramWebDAV.Server
{
    public class WebDavServer
    {
        private readonly HttpListener _listener;
        private readonly int _port;
        private bool _isRunning;
        private readonly NodeRepository _repository;
        private readonly TelegramService _telegramService;

        public WebDavServer(ConfigManager configManager, NodeRepository repository, TelegramService telegramService)
        {
            var settings = configManager.Load();
            _port = settings.Server.Port > 0 ? settings.Server.Port : 37000;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _repository = repository;
            _telegramService = telegramService;
        }

        public WebDavServer(int port = 37000)
        {
            _port = port;
            _listener = new HttpListener();
            // Настраиваем префиксы. Windows WebDAV клиент по умолчанию ищет корень.
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            
            // Инициализация слоя доступа к данным
            var dbManager = new DatabaseManager();
            dbManager.InitializeDatabase();
            _repository = new NodeRepository(dbManager);

            // Инициализация сервисов
            var configManager = new ConfigManager();
            _telegramService = new TelegramService(configManager);
            _telegramService.ConnectAsync().Wait();
        }

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                _listener.Start();
                _isRunning = true;
                AppLogger.Info("WebDAV", $"WebDAV Сервер запущен на http://localhost:{_port}/");
                
                // Запускаем фоновый цикл обработки входящих запросов
                Task.Run(ListenLoopAsync);
            }
            catch (HttpListenerException ex)
            {
                AppLogger.Error("WebDAV", $"Ошибка запуска сервера: {ex.Message}. Возможно, требуется запуск от имени администратора для резервирования порта.");
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _listener.Stop();
            AppLogger.Info("WebDAV", "WebDAV Сервер остановлен.");
        }

        private async Task ListenLoopAsync()
        {
            while (_isRunning)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequestAsync(context)); // Не блокируем цикл
                }
                catch (HttpListenerException)
                {
                    // Игнорируем исключения при остановке листенера
                    if (!_isRunning) break;
                }
                catch (Exception ex)
                {
                    AppLogger.Error("WebDAV", $"Неожиданная ошибка в цикле WebDAV: {ex.Message}", ex);
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            string localPath = request.Url?.LocalPath ?? "/";
            AppLogger.Debug("WebDAV", $"[{request.HttpMethod}] {localPath}");

            try
            {
                // Добавляем обязательные заголовки для WebDAV
                response.AppendHeader("DAV", "1, 2"); // Теперь мы полностью поддерживаем локи и свойства на словах и на деле!
                response.AppendHeader("Allow", "OPTIONS, PROPFIND, GET, PUT, MKCOL, DELETE, MOVE, HEAD, LOCK, UNLOCK, PROPPATCH");
                response.AppendHeader("Server", "TelegramWebDAV/1.0");

                switch (request.HttpMethod)
                {
                    case "OPTIONS":
                        await WebDavMiddleware.HandleOptionsAsync(context);
                        break;
                    case "PROPFIND":
                        await WebDavMiddleware.HandlePropfindAsync(context, _repository);
                        break;
                    case "GET":
                        await WebDavMiddleware.HandleGetAsync(context, _repository, _telegramService);
                        break;
                    case "PUT":
                        await WebDavMiddleware.HandlePutAsync(context, _repository, _telegramService);
                        break;
                    case "MKCOL":
                        await WebDavMiddleware.HandleMkColAsync(context, _repository);
                        break;
                    case "DELETE":
                        await WebDavMiddleware.HandleDeleteAsync(context, _repository, _telegramService);
                        break;
                    case "MOVE":
                        await WebDavMiddleware.HandleMoveAsync(context, _repository);
                        break;
                    case "HEAD":
                        await WebDavMiddleware.HandleHeadAsync(context);
                        break;
                    case "LOCK":
                        await WebDavMiddleware.HandleLockAsync(context);
                        break;
                    case "UNLOCK":
                        await WebDavMiddleware.HandleUnlockAsync(context);
                        break;
                    case "PROPPATCH":
                        await WebDavMiddleware.HandleProppatchAsync(context);
                        break;
                    default:
                        response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                        break;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("WebDAV", $"Ошибка обработки запроса [{request.HttpMethod}] {localPath}: {ex.Message}", ex);
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
            finally
            {
                // Важно всегда закрывать поток ответа, иначе клиент зависнет
                if (response.OutputStream.CanWrite)
                {
                    response.OutputStream.Close();
                }
            }
        }
    }
}
