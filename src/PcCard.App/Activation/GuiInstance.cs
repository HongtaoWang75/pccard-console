using System.IO.Pipes;
using System.Text;
using PcCard.Core;
using PcCard.Core.Logging;

namespace PcCard.App.Activation;

/// <summary>
/// GUI 单实例：全局互斥体 + 命名管道（把第二实例的激活参数转发给首实例）。
/// </summary>
public sealed class GuiInstance : IDisposable
{
    private readonly FileLog _log;
    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private Action<string>? _handler;

    public GuiInstance(FileLog log) => _log = log;

    /// <summary>抢锁成功返回 true；已有实例在跑返回 false。</summary>
    public bool Acquire()
    {
        foreach (var name in new[] { AppPaths.GuiMutexName, @"Local\PcCard.Gui" })
        {
            try
            {
                var m = new Mutex(initiallyOwned: true, name, out var createdNew);
                if (!createdNew)
                {
                    m.Dispose();
                    return false;
                }
                _mutex = m;
                break;
            }
            catch (UnauthorizedAccessException)
            {
                // 别的会话占着全局互斥体且无权限 → 退到 Local 命名空间再试
            }
        }
        if (_mutex is null) return false;

        StartPipeServer();
        return true;
    }

    private void StartPipeServer()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        AppPaths.GuiPipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(line))
                        _handler?.Invoke(line);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Warn($"激活管道异常：{ex.Message}");
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        });
    }

    public void StartListening(Action<string> handler) => _handler = handler;

    /// <summary>把消息转发给首实例。</summary>
    public static bool Forward(string message, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", AppPaths.GuiPipeName, PipeDirection.Out);
            client.Connect((int)timeout.TotalMilliseconds);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(message);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch
        {
            // 忽略
        }
        // 互斥体由进程退出自动释放（所有权是线程绑定的，这里不做 ReleaseMutex）
        _mutex?.Dispose();
        _mutex = null;
    }
}
