using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\TurtleAIQuartetHub.SingleInstance";
    private const string PipeName = "TurtleAIQuartetHub.Commands";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _listeningCancellation = new();
    private readonly bool _isPrimary;

    public SingleInstanceCoordinator()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
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
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
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
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_listeningCancellation.Token);
                using var reader = new StreamReader(server);
                var payload = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(payload))
                {
                    continue;
                }

                var args = JsonSerializer.Deserialize<string[]>(payload) ?? [];
                CommandReceived?.Invoke(args);
            }
            catch (OperationCanceledException)
            {
                break;
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
