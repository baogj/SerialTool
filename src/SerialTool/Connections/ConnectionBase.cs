using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SerialTool;

public enum ConnectionKind { Serial, TcpClient, TcpServer, Udp }

/// <summary>
/// 一条“连接”的统一抽象（串口 / TCP 客户端 / TCP 服务器 / UDP）。
/// 接收到的数据与成功写出的数据都进入输出队列（由 UI 定时器显示），
/// 同时通过 <see cref="Received"/> 在后台线程即时通知（供 Modbus 主站、串口转发使用）。
/// </summary>
public interface IConnection
{
    ConnectionKind Kind { get; }
    /// <summary>简短名称，如 “COM3” / “TCP 192.168.1.10:502”。</summary>
    string Name { get; }
    long PendingBytes { get; }
    event Action<string>? Faulted;
    event Action<string>? Warning;
    event Action<string>? Info;
    /// <summary>收到数据（后台线程调用，处理函数必须快速返回）。</summary>
    event Action<byte[]>? Received;
    void Start();
    bool Send(byte[] data);
    bool Send(byte[] data, int markStart, int markLength);
    void Stop();
}

/// <summary>公共部分：发送队列 + 写线程 + 回显 + 错误上报。子类负责读线程和实际写出。</summary>
public abstract class ConnectionBase : IConnection
{
    private const long MaxPendingBytes = 32L * 1024 * 1024;

    private readonly ConcurrentQueue<DataChunk>? _out;
    private readonly BlockingCollection<(byte[] Data, int MarkStart, int MarkLength)> _sendQueue =
        new(new ConcurrentQueue<(byte[], int, int)>());
    private Thread? _writer;
    private volatile bool _stopping;
    private int _stopCalled;
    private int _faulted;
    private long _pending;

    protected ConnectionBase(ConcurrentQueue<DataChunk>? output)
    {
        _out = output;
    }

    public abstract ConnectionKind Kind { get; }
    public abstract string Name { get; }
    public long PendingBytes => Interlocked.Read(ref _pending);
    protected bool Stopping => _stopping;

    public event Action<string>? Faulted;
    public event Action<string>? Warning;
    public event Action<string>? Info;
    public event Action<byte[]>? Received;

    public void Start()
    {
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = Name + " Writer" };
        _writer.Start();
        StartReading();
    }

    /// <summary>启动读线程（以及 TCP 服务器的接受线程）。</summary>
    protected abstract void StartReading();

    /// <summary>实际写出。返回本次回显的来源/目标描述（null = 不标注）；抛出 TimeoutException 视为警告，其它异常视为断开。
    /// 返回 false 表示没有发出（例如服务器没有客户端），此时不回显。</summary>
    protected abstract bool WriteCore(byte[] data, out string? target);

    /// <summary>停止时关闭底层资源，使阻塞的读线程退出。</summary>
    protected abstract void CloseCore();

    /// <summary>等待读线程等退出。</summary>
    protected virtual void JoinReaders() { }

    public bool Send(byte[] data) => Send(data, -1, 0);

    public bool Send(byte[] data, int markStart, int markLength)
    {
        if (_stopping || data.Length == 0) return false;
        if (Interlocked.Read(ref _pending) + data.Length > MaxPendingBytes) return false;
        Interlocked.Add(ref _pending, data.Length);
        bool ok;
        try
        {
            ok = _sendQueue.TryAdd((data, markStart, markLength));
        }
        catch (InvalidOperationException)
        {
            ok = false; // 已 CompleteAdding
        }
        if (!ok) Interlocked.Add(ref _pending, -data.Length);
        return ok;
    }

    /// <summary>子类读到数据时调用。</summary>
    protected void OnReceived(byte[] data, string? source = null)
    {
        if (data.Length == 0) return;
        _out?.Enqueue(new DataChunk(false, data, DateTime.Now) { Source = source });
        var h = Received;
        if (h == null) return;
        try { h(data); } catch { /* 订阅者异常不影响读线程 */ }
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var (data, markStart, markLength) in _sendQueue.GetConsumingEnumerable())
            {
                Interlocked.Add(ref _pending, -data.Length);
                if (_stopping) break;
                try
                {
                    if (WriteCore(data, out string? target))
                        _out?.Enqueue(new DataChunk(true, data, DateTime.Now) { MarkStart = markStart, MarkLength = markLength, Source = target });
                }
                catch (TimeoutException)
                {
                    RaiseWarning($"发送超时（可能被流控阻塞），{data.Length} 字节未能完整发出");
                }
                catch (Exception ex)
                {
                    if (_stopping) break;
                    Fault($"写入出错，已断开：{ex.Message}");
                    break;
                }
            }
        }
        catch (Exception)
        {
            // 队列被释放等，直接退出
        }
    }

    protected void Fault(string message)
    {
        if (_stopping) return;
        if (Interlocked.Exchange(ref _faulted, 1) == 0)
            SafeInvoke(Faulted, message);
    }

    protected void RaiseWarning(string message) => SafeInvoke(Warning, message);
    protected void RaiseInfo(string message) => SafeInvoke(Info, message);

    private static void SafeInvoke(Action<string>? handler, string message)
    {
        try { handler?.Invoke(message); } catch { /* ignore */ }
    }

    /// <summary>关闭并等待线程退出。可能阻塞（驱动异常时），应在后台线程调用。</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopCalled, 1) != 0) return;
        _stopping = true;
        try { _sendQueue.CompleteAdding(); } catch { /* ignore */ }
        try { CloseCore(); } catch { /* ignore */ }
        try { JoinReaders(); } catch { /* ignore */ }
        try { _writer?.Join(2000); } catch { /* ignore */ }
    }
}
