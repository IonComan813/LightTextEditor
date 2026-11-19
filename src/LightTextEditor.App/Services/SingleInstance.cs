using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace LightTextEditor.App.Services;

/// <summary>
/// 保证每个用户只运行一个实例。后启动的实例通过命名管道把命令行参数（要打开的文件）发给第一个实例。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;

    public bool IsFirstInstance { get; }

    /// <summary>收到其他实例发来的参数（在后台线程触发）。</summary>
    public event Action<string[]>? ArgumentsReceived;

    public SingleInstance()
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "|LightTextEditor")))[..16];
        _pipeName = "LightTextEditor-" + id;
        _mutex = new Mutex(initiallyOwned: true, @"Local\LightTextEditor-" + id, out bool createdNew);
        IsFirstInstance = createdNew;
    }

    public bool SendToFirstInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(3000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            // 相对路径要按本进程的工作目录解析后再发送。
            foreach (var a in args)
                writer.WriteLine(LooksLikePath(a) ? Path.GetFullPath(a) : a);
            writer.WriteLine(); // 空行表示"激活窗口"
            writer.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool LooksLikePath(string arg) => !arg.StartsWith('-');

    public void StartListening()
    {
        if (!IsFirstInstance || _cts is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var args = new List<string>();
                    while (await reader.ReadLineAsync(token) is { } line)
                    {
                        if (line.Length == 0) break;
                        args.Add(line);
                    }
                    ArgumentsReceived?.Invoke(args.ToArray());
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) { await Task.Delay(200, CancellationToken.None); }
            }
        }, token);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (IsFirstInstance)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
