using System;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Threading;

namespace SerialTool;

/// <summary>
/// 一次打开的串口会话：独立的读线程 + 写线程。
/// 读到的数据与成功写出的数据都放进共享队列，由 UI 定时器批量取出显示，
/// 因此高波特率下也不会阻塞界面。
/// </summary>
public sealed class SerialSession
{
    private const long MaxPendingBytes = 32L * 1024 * 1024;

    private readonly SerialPort _port;
    private readonly ConcurrentQueue<DataChunk> _out;
    private readonly BlockingCollection<(byte[] Data, int MarkStart, int MarkLength)> _sendQueue =
        new(new ConcurrentQueue<(byte[], int, int)>());
    private Thread? _reader;
    private Thread? _writer;
    private volatile bool _stopping;
    private int _stopCalled;
    private int _faulted;
    private long _pending;

    public SerialSession(SerialPort port, ConcurrentQueue<DataChunk> output)
    {
        _port = port;
        _out = output;
        PortName = port.PortName;
    }

    public SerialPort Port => _port;
    public string PortName { get; }

    /// <summary>已入队但尚未写出的字节数。</summary>
    public long PendingBytes => Interlocked.Read(ref _pending);

    /// <summary>致命错误（如设备拔出），在后台线程触发。</summary>
    public event Action<string>? Faulted;

    /// <summary>非致命警告（如发送超时），在后台线程触发。</summary>
    public event Action<string>? Warning;

    public void Start()
    {
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "SerialReader", Priority = ThreadPriority.AboveNormal };
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "SerialWriter" };
        _reader.Start();
        _writer.Start();
    }

    public bool Send(byte[] data) => Send(data, -1, 0);

    /// <param name="markStart">校验字节起始位置（仅用于回显标注），-1 表示无</param>
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

    private void ReadLoop()
    {
        var buf = new byte[64 * 1024];
        while (!_stopping)
        {
            int n;
            try
            {
                n = _port.Read(buf, 0, buf.Length);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (Exception ex)
            {
                if (_stopping) break;
                Fault($"串口读取出错，已断开：{ex.Message}");
                break;
            }

            if (n <= 0)
            {
                Thread.Sleep(5);
                continue;
            }

            var data = new byte[n];
            Buffer.BlockCopy(buf, 0, data, 0, n);
            _out.Enqueue(new DataChunk(false, data, DateTime.Now));
        }
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
                    _port.Write(data, 0, data.Length);
                    _out.Enqueue(new DataChunk(true, data, DateTime.Now) { MarkStart = markStart, MarkLength = markLength });
                }
                catch (TimeoutException)
                {
                    SafeInvoke(Warning, $"发送超时（可能被硬件流控阻塞），{data.Length} 字节未能完整发出");
                }
                catch (Exception ex)
                {
                    if (_stopping) break;
                    Fault($"串口写入出错，已断开：{ex.Message}");
                    break;
                }
            }
        }
        catch (Exception)
        {
            // 队列被释放等，直接退出
        }
    }

    private void Fault(string message)
    {
        if (Interlocked.Exchange(ref _faulted, 1) == 0)
            SafeInvoke(Faulted, message);
    }

    private static void SafeInvoke(Action<string>? handler, string message)
    {
        try
        {
            handler?.Invoke(message);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>关闭串口并等待线程退出。可能阻塞（驱动异常时），应在后台线程调用。</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopCalled, 1) != 0) return;
        _stopping = true;
        try { _sendQueue.CompleteAdding(); } catch { /* ignore */ }
        try { if (_port.IsOpen) _port.DiscardOutBuffer(); } catch { /* ignore */ }
        try { _port.Close(); } catch { /* ignore */ }
        try { _reader?.Join(2000); } catch { /* ignore */ }
        try { _writer?.Join(2000); } catch { /* ignore */ }
        try { _port.Dispose(); } catch { /* ignore */ }
    }
}
