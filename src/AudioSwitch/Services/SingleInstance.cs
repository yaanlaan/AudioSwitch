using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AudioSwitch.Services;

/// <summary>
/// 单实例：命名互斥量判定，命名管道把 "show" 之类的命令传给已运行的实例。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\AudioSwitch.SingleInstance.v1";
    private const string PipeName = "AudioSwitch.Ipc.v1";

    private readonly CancellationTokenSource _cancellation = new();
    private Mutex? _mutex;
    private Task? _serverTask;
    private bool _disposed;

    public SingleInstance()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: false, MutexName, out bool createdNew);
            IsFirstInstance = createdNew;
            if (!IsFirstInstance)
            {
                _mutex.Dispose();
                _mutex = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"单实例互斥量创建失败，按主实例运行：{ex.Message}");
            IsFirstInstance = true;
        }
    }

    public bool IsFirstInstance { get; }

    public event Action<string>? MessageReceived;

    public void StartServer()
    {
        if (!IsFirstInstance || _serverTask is not null)
        {
            return;
        }

        _serverTask = Task.Run(RunServerAsync);
    }

    private async Task RunServerAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_cancellation.Token).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                string? line = await reader.ReadLineAsync(_cancellation.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    MessageReceived?.Invoke(line.Trim());
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"单实例管道异常：{ex.Message}");
                try
                {
                    await Task.Delay(200, _cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>通知已运行的实例；没有实例时返回 false。</summary>
    public static bool NotifyFirstInstance(string message, int timeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(message);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"连接已有实例失败：{ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();

        try
        {
            _serverTask?.Wait(500);
        }
        catch
        {
            // 退出时忽略
        }

        _cancellation.Dispose();
        _mutex?.Dispose();
        _mutex = null;
    }
}
