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
public sealed class SerialSession : ConnectionBase
{
    private readonly SerialPort _port;
    private Thread? _reader;

    public SerialSession(SerialPort port, ConcurrentQueue<DataChunk>? output) : base(output)
    {
        _port = port;
        PortName = port.PortName;
    }

    public SerialPort Port => _port;
    public string PortName { get; }
    public override ConnectionKind Kind => ConnectionKind.Serial;
    public override string Name => PortName;

    protected override void StartReading()
    {
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "SerialReader", Priority = ThreadPriority.AboveNormal };
        _reader.Start();
    }

    private void ReadLoop()
    {
        var buf = new byte[64 * 1024];
        while (!Stopping)
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
                if (Stopping) break;
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
            OnReceived(data);
        }
    }

    protected override bool WriteCore(byte[] data, out string? target)
    {
        target = null;
        _port.Write(data, 0, data.Length);
        return true;
    }

    protected override void CloseCore()
    {
        try { if (_port.IsOpen) _port.DiscardOutBuffer(); } catch { /* ignore */ }
        try { _port.Close(); } catch { /* ignore */ }
    }

    protected override void JoinReaders()
    {
        try { _reader?.Join(2000); } catch { /* ignore */ }
        try { _port.Dispose(); } catch { /* ignore */ }
    }
}
