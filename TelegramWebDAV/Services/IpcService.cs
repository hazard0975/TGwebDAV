using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TelegramWebDAV.Services
{
    public class IpcRequest
    {
        public string Command { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    public class IpcResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? Details { get; set; }
    }

    /// <summary>
    /// Межпроцессный сервер на базе локального именованного пайпа (Named Pipe) Windows.
    /// Позволяет внешним процессам (например, вызовам из контекстного меню Проводника)
    /// отправлять команды в основной запущенный процесс службы без монопольных конфликтов за session-файл или порты.
    /// </summary>
    public class IpcServer : IDisposable
    {
        public const string PipeName = "TelegramWebDAV_IPC_Pipe";

        private readonly Func<IpcRequest, Task<IpcResponse>> _handler;
        private CancellationTokenSource? _cts;
        private Task? _listenTask;
        private bool _disposed;

        public IpcServer(Func<IpcRequest, Task<IpcResponse>> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
            AppLogger.Info("IPC", $"IPC сервер запущен на канале '{PipeName}'.");
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var pipeStream = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await pipeStream.WaitForConnectionAsync(ct);

                    using var reader = new StreamReader(pipeStream, leaveOpen: true);
                    using var writer = new StreamWriter(pipeStream, leaveOpen: true) { AutoFlush = true };

                    string? line = await reader.ReadLineAsync(ct);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        IpcResponse response;
                        try
                        {
                            var req = JsonSerializer.Deserialize<IpcRequest>(line);
                            if (req != null)
                            {
                                response = await _handler(req);
                            }
                            else
                            {
                                response = new IpcResponse { Success = false, Message = "Некорректный запрос (пустой JSON)." };
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error("IPC", $"Ошибка обработки IPC запроса: {ex.Message}", ex);
                            response = new IpcResponse { Success = false, Message = $"Ошибка на сервере: {ex.Message}" };
                        }

                        string jsonResponse = JsonSerializer.Serialize(response);
                        await writer.WriteLineAsync(jsonResponse.AsMemory(), ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        AppLogger.Warn("IPC", $"Ошибка в цикле IPC сервера: {ex.Message}");
                        await Task.Delay(500, ct).ContinueWith(_ => { });
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
            try { _listenTask?.Wait(1000); } catch { }
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Клиент для отправки команд в запущенный основной процесс службы через Named Pipe.
    /// </summary>
    public static class IpcClient
    {
        public static async Task<IpcResponse?> SendCommandAsync(string command, string path, int timeoutMs = 15000)
        {
            try
            {
                using var pipeClient = new NamedPipeClientStream(".", IpcServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                using var cts = new CancellationTokenSource(timeoutMs);

                await pipeClient.ConnectAsync(cts.Token);

                using var reader = new StreamReader(pipeClient, leaveOpen: true);
                using var writer = new StreamWriter(pipeClient, leaveOpen: true) { AutoFlush = true };

                var req = new IpcRequest { Command = command, Path = path };
                string reqJson = JsonSerializer.Serialize(req);
                await writer.WriteLineAsync(reqJson.AsMemory(), cts.Token);

                string? resJson = await reader.ReadLineAsync(cts.Token);
                if (!string.IsNullOrWhiteSpace(resJson))
                {
                    return JsonSerializer.Deserialize<IpcResponse>(resJson);
                }

                return new IpcResponse { Success = false, Message = "Пустой ответ от сервера." };
            }
            catch (TimeoutException)
            {
                return new IpcResponse { Success = false, Message = "Таймаут ожидания ответа от основной службы Telegram WebDAV." };
            }
            catch (Exception ex)
            {
                return new IpcResponse { Success = false, Message = $"Не удалось связаться с запущенной службой: {ex.Message}" };
            }
        }
    }
}
