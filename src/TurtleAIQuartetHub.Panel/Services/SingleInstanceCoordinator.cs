using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string DefaultMutexName = @"Local\TurtleAIQuartetHub.SingleInstance";
    private const string DefaultPipeName = "TurtleAIQuartetHub.Commands";

    private readonly string _pipeName;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _listeningCancellation = new();
    private readonly bool _isPrimary;

    public SingleInstanceCoordinator()
        : this(DefaultMutexName, DefaultPipeName)
    {
    }

    internal SingleInstanceCoordinator(string mutexName, string pipeName)
    {
        _pipeName = pipeName;
        _mutex = new Mutex(true, mutexName, out var createdNew);
        _isPrimary = createdNew;
        if (_isPrimary)
        {
            _ = Task.Run(ListenLoopAsync);
        }
    }

    public event Action<string[]>? CommandReceived;

    public bool IsPrimary => _isPrimary;

    public async Task<bool> SendToPrimaryAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(900, cancellationToken);
            await using var writer = new StreamWriter(client) { AutoFlush = true };
            var payload = JsonSerializer.Serialize(args.Length == 0 ? ["--activate"] : args);
            await writer.WriteAsync(payload);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ListenLoopAsync()
    {
        while (!_listeningCancellation.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // 同時発火するフック通知を取りこぼさないよう複数インスタンスを許可し、
                // 接続確立後は即座に次の待ち受けパイプを作成して空白時間を作らない。
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_listeningCancellation.Token);
                var connected = server;
                server = null;
                _ = HandleClientConnectionAsync(connected);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                DiagnosticLog.Write(ex);
            }
        }
    }

    private async Task HandleClientConnectionAsync(NamedPipeServerStream server)
    {
        await using (server)
        {
            try
            {
                using var reader = new StreamReader(server);
                var payload = await reader.ReadToEndAsync(_listeningCancellation.Token);
                if (string.IsNullOrWhiteSpace(payload))
                {
                    return;
                }

                var args = JsonSerializer.Deserialize<string[]>(payload) ?? [];
                CommandReceived?.Invoke(args);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
            }
        }
    }

    public void Dispose()
    {
        _listeningCancellation.Cancel();
        _listeningCancellation.Dispose();

        if (_isPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _mutex.Dispose();
    }
}
