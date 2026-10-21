using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SerialTool.Features;

/// <summary>可收发字节的通道（会话）。批量测试、Lua 脚本、Modbus 网关都通过它使用任意会话。</summary>
public interface IByteChannel
{
    string Name { get; }
    bool IsOpen { get; }
    bool Send(byte[] data);
    /// <summary>收到数据（后台线程）。</summary>
    event Action<byte[]>? Received;
}

/// <summary>一个会话对外暴露的通道：连接打开时转发连接的收发，关闭后 Send 返回 false。</summary>
public sealed class SessionChannel : IByteChannel, INotifyPropertyChanged
{
    private string _name;
    private volatile IConnection? _conn;

    public SessionChannel(string id, string name)
    {
        Id = id;
        _name = name;
    }

    public string Id { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public IConnection? Connection
    {
        get => _conn;
        set => _conn = value;
    }

    public bool IsOpen => _conn != null;
    public bool Send(byte[] data) => _conn?.Send(data) ?? false;

    public event Action<byte[]>? Received;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>由会话所有者在连接的 Received 回调中调用。</summary>
    public void RaiseReceived(byte[] data) => Received?.Invoke(data);

    public override string ToString() => _name;
}

/// <summary>
/// 一个独立会话的非界面部分：自己的设置、连接、输出队列和统计。
/// 每个会话标签页一个实例，彼此不共享任何缓冲区。
/// </summary>
public sealed class SessionCore
{
    private IConnection? _conn;
    private long _rxBytes, _txBytes;

    public SessionCore(SessionSettings settings)
    {
        Settings = settings;
        Channel = new SessionChannel(settings.Id, settings.Name);
    }

    public SessionSettings Settings { get; }
    public SessionChannel Channel { get; }
    /// <summary>收发数据（给界面批量显示）。</summary>
    public ConcurrentQueue<DataChunk> Output { get; } = new();
    public IConnection? Connection => _conn;
    public bool IsOpen => _conn != null;
    public long RxBytes => Interlocked.Read(ref _rxBytes);
    public long TxBytes => Interlocked.Read(ref _txBytes);

    /// <summary>连接意外断开（后台线程）。</summary>
    public event Action<string>? Faulted;
    public event Action<string>? Warning;

    public void CountTx(long n) => Interlocked.Add(ref _txBytes, n);
    public void ResetCounters()
    {
        Interlocked.Exchange(ref _rxBytes, 0);
        Interlocked.Exchange(ref _txBytes, 0);
    }

    /// <summary>按会话设置创建连接（未启动）。</summary>
    public static async Task<IConnection> CreateConnectionAsync(SessionSettings s, ConcurrentQueue<DataChunk>? output)
    {
        switch (s.ConnType)
        {
            case "TcpClient":
                return await TcpClientConnection.ConnectAsync(s.RemoteHost, s.RemotePort, output).ConfigureAwait(false);
            case "TcpServer":
                return TcpServerConnection.Listen(s.LocalPort, output);
            case "Udp":
                return await UdpConnection.OpenAsync(s.LocalPort, string.IsNullOrWhiteSpace(s.RemoteHost) ? null : s.RemoteHost, s.RemotePort, output).ConfigureAwait(false);
            default:
                if (string.IsNullOrWhiteSpace(s.PortName)) throw new InvalidOperationException("请先选择串口");
                var port = new SerialPort(s.PortName)
                {
                    BaudRate = s.BaudRate,
                    DataBits = s.DataBits,
                    StopBits = s.StopBits,
                    Parity = s.Parity,
                    Handshake = s.Handshake,
                    ReadTimeout = 100,
                    WriteTimeout = 3000,
                    ReadBufferSize = 1 << 20,
                    WriteBufferSize = 1 << 16,
                    ParityReplace = 0,
                    DiscardNull = false,
                    DtrEnable = s.Dtr,
                };
                if (s.Handshake is not (Handshake.RequestToSend or Handshake.RequestToSendXOnXOff)) port.RtsEnable = s.Rts;
                try
                {
                    await Task.Run(port.Open).ConfigureAwait(false);
                }
                catch
                {
                    port.Dispose();
                    throw;
                }
                return new SerialSession(port, output);
        }
    }

    public async Task OpenAsync()
    {
        if (_conn != null) return;
        var c = await CreateConnectionAsync(Settings, Output).ConfigureAwait(false);
        Attach(c);
    }

    /// <summary>挂接一个已创建的连接（测试可直接注入）。</summary>
    public void Attach(IConnection c)
    {
        c.Received += OnReceived;
        c.Faulted += msg => { if (_conn == c) Faulted?.Invoke(msg); };
        c.Warning += msg => Warning?.Invoke(msg);
        _conn = c;
        Channel.Connection = c;
        c.Start();
    }

    private void OnReceived(byte[] data)
    {
        Interlocked.Add(ref _rxBytes, data.Length);
        Channel.RaiseReceived(data);
    }

    public async Task CloseAsync()
    {
        var c = _conn;
        if (c == null) return;
        _conn = null;
        Channel.Connection = null;
        c.Received -= OnReceived;
        var t = Task.Run(c.Stop);
        await Task.WhenAny(t, Task.Delay(4000)).ConfigureAwait(false);
    }

    /// <summary>发送：内容 → 校验 → 行尾。</summary>
    public bool Send(string content, bool hex, out string? error)
    {
        error = null;
        var c = _conn;
        if (c == null) { error = "会话未连接"; return false; }
        if (!BuildPayload(content, hex, Settings.Escape, ParseChecksum(Settings.Checksum), Settings.LineEnding,
                EncodingUtil.Get(Settings.Encoding), out var data, out int ms, out int ml, out error)) return false;
        if (data.Length == 0) { error = "发送内容为空"; return false; }
        if (!c.Send(data, ms, ml)) { error = "发送队列已满"; return false; }
        CountTx(data.Length);
        return true;
    }

    public bool SendRaw(byte[] data)
    {
        var c = _conn;
        if (c == null || data.Length == 0) return false;
        if (!c.Send(data)) return false;
        CountTx(data.Length);
        return true;
    }

    public static ChecksumKind ParseChecksum(string? s) =>
        Enum.TryParse<ChecksumKind>(s, out var k) ? k : ChecksumKind.None;

    /// <summary>与主窗口相同的组包规则：内容 → [校验] → [行尾]。markStart/markLength 是校验字节位置。</summary>
    public static bool BuildPayload(string content, bool hex, bool escape, ChecksumKind checksum, string? lineEnding, Encoding enc,
        out byte[] data, out int markStart, out int markLength, out string? error)
    {
        data = Array.Empty<byte>();
        markStart = -1;
        markLength = 0;
        error = null;
        byte[] body;
        if (hex)
        {
            if (!HexUtil.TryParse(content, out body, out error)) return false;
        }
        else
        {
            body = escape ? EscapeUtil.Encode(content, enc) : enc.GetBytes(content);
        }
        if (body.Length > 0 && checksum != ChecksumKind.None)
        {
            int plain = body.Length;
            body = Checksum.Append(checksum, body);
            markStart = plain;
            markLength = body.Length - plain;
        }
        byte[] tail = string.IsNullOrEmpty(lineEnding) ? Array.Empty<byte>() : Encoding.ASCII.GetBytes(lineEnding);
        data = new byte[body.Length + tail.Length];
        Buffer.BlockCopy(body, 0, data, 0, body.Length);
        Buffer.BlockCopy(tail, 0, data, body.Length, tail.Length);
        return true;
    }
}

/// <summary>线程安全的接收缓冲：收集通道收到的字节，供“发送后等待应答”的逻辑使用。</summary>
public sealed class RxCollector : IDisposable
{
    private readonly object _lock = new();
    private readonly List<byte> _buf = new();
    private readonly IByteChannel? _channel;
    private long _version;
    public int MaxBytes { get; set; } = 1 << 20;

    public RxCollector(IByteChannel? channel = null)
    {
        _channel = channel;
        if (channel != null) channel.Received += Append;
    }

    public void Append(byte[] data)
    {
        if (data.Length == 0) return;
        lock (_lock)
        {
            _buf.AddRange(data);
            if (_buf.Count > MaxBytes) _buf.RemoveRange(0, _buf.Count - MaxBytes);
            _version++;
            Monitor.PulseAll(_lock);
        }
    }

    public int Count { get { lock (_lock) return _buf.Count; } }

    public void Clear()
    {
        lock (_lock) _buf.Clear();
    }

    public byte[] Snapshot()
    {
        lock (_lock) return _buf.ToArray();
    }

    /// <summary>取出并清空。</summary>
    public byte[] Take()
    {
        lock (_lock)
        {
            var r = _buf.ToArray();
            _buf.Clear();
            return r;
        }
    }

    /// <summary>丢弃前 n 字节。</summary>
    public void Consume(int n)
    {
        lock (_lock) _buf.RemoveRange(0, Math.Min(n, _buf.Count));
    }

    /// <summary>等待新数据到达（或超时 / 取消）。返回是否有新数据。</summary>
    public bool WaitForData(long sinceVersion, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
        lock (_lock)
        {
            while (_version == sinceVersion)
            {
                ct.ThrowIfCancellationRequested();
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) return false;
                Monitor.Wait(_lock, Math.Min(left, 50));
            }
            return true;
        }
    }

    public long Version { get { lock (_lock) return _version; } }

    public void Dispose()
    {
        if (_channel != null) _channel.Received -= Append;
    }
}
