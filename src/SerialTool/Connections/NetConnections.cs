using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SerialTool;

public static class NetUtil
{
    /// <summary>解析主机名或 IP（优先 IPv4）。</summary>
    public static async Task<IPAddress> ResolveAsync(string host)
    {
        host = host.Trim();
        if (host.Length == 0) throw new ArgumentException("主机地址为空");
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var all = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
        return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.First();
    }

    public static string Describe(Exception ex) => ex switch
    {
        SocketException se => se.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => "连接被拒绝（对方端口未监听）",
            SocketError.AddressAlreadyInUse => "端口已被占用",
            SocketError.TimedOut => "连接超时",
            SocketError.HostNotFound or SocketError.NoData => "找不到主机",
            SocketError.NetworkUnreachable or SocketError.HostUnreachable => "网络不可达",
            SocketError.AccessDenied => "没有权限使用该端口",
            _ => se.Message,
        },
        _ => ex.Message,
    };
}

/// <summary>TCP 客户端。</summary>
public sealed class TcpClientConnection : ConnectionBase
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private Thread? _reader;

    private TcpClientConnection(TcpClient client, string name, ConcurrentQueue<DataChunk>? output) : base(output)
    {
        _client = client;
        _stream = client.GetStream();
        Name = name;
    }

    public override ConnectionKind Kind => ConnectionKind.TcpClient;
    public override string Name { get; }

    public static async Task<TcpClientConnection> ConnectAsync(string host, int port, ConcurrentQueue<DataChunk>? output, int timeoutMs = 5000)
    {
        var ip = await NetUtil.ResolveAsync(host).ConfigureAwait(false);
        var client = new TcpClient(ip.AddressFamily) { NoDelay = true };
        try
        {
            var connect = client.ConnectAsync(ip, port);
            if (await Task.WhenAny(connect, Task.Delay(timeoutMs)).ConfigureAwait(false) != connect)
                throw new TimeoutException($"连接 {host}:{port} 超时");
            await connect.ConfigureAwait(false);
            client.SendTimeout = 5000;
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return new TcpClientConnection(client, $"TCP {host}:{port}", output);
    }

    protected override void StartReading()
    {
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "TcpClientReader" };
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
                n = _stream.Read(buf, 0, buf.Length);
            }
            catch (Exception ex)
            {
                if (!Stopping) Fault("TCP 连接已断开：" + NetUtil.Describe(ex.InnerException ?? ex));
                break;
            }
            if (n <= 0)
            {
                if (!Stopping) Fault("对方已关闭 TCP 连接");
                break;
            }
            var data = new byte[n];
            Buffer.BlockCopy(buf, 0, data, 0, n);
            OnReceived(data);
        }
    }

    protected override bool WriteCore(byte[] data, out string? target)
    {
        target = null;
        try
        {
            _stream.Write(data, 0, data.Length);
        }
        catch (System.IO.IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
        {
            throw new TimeoutException();
        }
        return true;
    }

    protected override void CloseCore()
    {
        try { _client.Client.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
        try { _client.Close(); } catch { /* ignore */ }
    }

    protected override void JoinReaders()
    {
        try { _reader?.Join(2000); } catch { /* ignore */ }
    }
}

/// <summary>TCP 服务器：监听本地端口，可同时接受多个客户端；发送可选“全部”或“选中的客户端”。</summary>
public sealed class TcpServerConnection : ConnectionBase
{
    public sealed class ClientInfo
    {
        internal ClientInfo(int id, TcpClient client)
        {
            Id = id;
            Client = client;
            Endpoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
            Connected = DateTime.Now;
        }

        public int Id { get; }
        public string Endpoint { get; }
        public DateTime Connected { get; }
        internal TcpClient Client { get; }
        internal Thread? Reader { get; set; }
        public long RxBytes;
        public override string ToString() => $"#{Id}  {Endpoint}";
    }

    private readonly TcpListener _listener;
    private readonly object _lock = new();
    private readonly List<ClientInfo> _clients = new();
    private Thread? _acceptThread;
    private int _nextId;
    private volatile int[]? _targets;

    private TcpServerConnection(TcpListener listener, int port, ConcurrentQueue<DataChunk>? output) : base(output)
    {
        _listener = listener;
        LocalPort = port;
    }

    public override ConnectionKind Kind => ConnectionKind.TcpServer;
    public override string Name => $"TCP 服务器 :{LocalPort}";
    public int LocalPort { get; }

    /// <summary>客户端列表变化（后台线程）。</summary>
    public event Action? ClientsChanged;

    /// <summary>发送目标：null = 全部客户端；否则只发给这些 Id。</summary>
    public int[]? SendTargets
    {
        get => _targets;
        set => _targets = value;
    }

    public IReadOnlyList<ClientInfo> Clients
    {
        get { lock (_lock) return _clients.ToList(); }
    }

    /// <summary>在本地端口开始监听（port=0 自动分配）。</summary>
    public static TcpServerConnection Listen(int port, ConcurrentQueue<DataChunk>? output)
    {
        var l = new TcpListener(IPAddress.Any, port);
        l.Start();
        int actual = ((IPEndPoint)l.LocalEndpoint).Port;
        return new TcpServerConnection(l, actual, output);
    }

    protected override void StartReading()
    {
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "TcpServerAccept" };
        _acceptThread.Start();
    }

    private void AcceptLoop()
    {
        while (!Stopping)
        {
            TcpClient c;
            try
            {
                c = _listener.AcceptTcpClient();
            }
            catch (Exception ex)
            {
                if (!Stopping) Fault("TCP 服务器停止监听：" + NetUtil.Describe(ex));
                break;
            }
            c.NoDelay = true;
            c.SendTimeout = 5000;
            var info = new ClientInfo(Interlocked.Increment(ref _nextId), c);
            lock (_lock) _clients.Add(info);
            info.Reader = new Thread(() => ClientLoop(info)) { IsBackground = true, Name = "TcpServerClient" + info.Id };
            info.Reader.Start();
            RaiseInfo($"客户端已连接：{info.Endpoint}");
            RaiseClientsChanged();
        }
    }

    private void ClientLoop(ClientInfo info)
    {
        var buf = new byte[64 * 1024];
        NetworkStream s;
        try { s = info.Client.GetStream(); }
        catch { RemoveClient(info, null); return; }
        while (!Stopping)
        {
            int n;
            try { n = s.Read(buf, 0, buf.Length); }
            catch { n = 0; }
            if (n <= 0) break;
            var data = new byte[n];
            Buffer.BlockCopy(buf, 0, data, 0, n);
            Interlocked.Add(ref info.RxBytes, n);
            OnReceived(data, info.Endpoint);
        }
        RemoveClient(info, Stopping ? null : $"客户端已断开：{info.Endpoint}");
    }

    private void RemoveClient(ClientInfo info, string? message)
    {
        bool removed;
        lock (_lock) removed = _clients.Remove(info);
        try { info.Client.Close(); } catch { /* ignore */ }
        if (!removed) return;
        if (message != null) RaiseInfo(message);
        RaiseClientsChanged();
    }

    /// <summary>主动断开指定客户端。</summary>
    public void Disconnect(int id)
    {
        ClientInfo? c;
        lock (_lock) c = _clients.FirstOrDefault(x => x.Id == id);
        if (c != null) RemoveClient(c, $"已断开客户端：{c.Endpoint}");
    }

    private void RaiseClientsChanged()
    {
        try { ClientsChanged?.Invoke(); } catch { /* ignore */ }
    }

    protected override bool WriteCore(byte[] data, out string? target)
    {
        List<ClientInfo> targets;
        var ids = _targets;
        lock (_lock) targets = ids == null ? _clients.ToList() : _clients.Where(c => ids.Contains(c.Id)).ToList();
        target = null;
        if (targets.Count == 0)
        {
            RaiseWarning(ids == null ? "没有已连接的客户端，数据未发出" : ids.Length == 0 ? "未选中任何客户端，数据未发出" : "选中的客户端都已断开，数据未发出");
            return false;
        }
        int ok = 0;
        foreach (var c in targets)
        {
            try
            {
                c.Client.GetStream().Write(data, 0, data.Length);
                ok++;
            }
            catch
            {
                RemoveClient(c, $"向 {c.Endpoint} 发送失败，已断开");
            }
        }
        if (ok == 0) return false;
        target = targets.Count == 1 ? targets[0].Endpoint : $"{ok} 个客户端";
        return true;
    }

    protected override void CloseCore()
    {
        try { _listener.Stop(); } catch { /* ignore */ }
        List<ClientInfo> all;
        lock (_lock)
        {
            all = _clients.ToList();
            _clients.Clear();
        }
        foreach (var c in all)
        {
            try { c.Client.Close(); } catch { /* ignore */ }
        }
    }

    protected override void JoinReaders()
    {
        try { _acceptThread?.Join(2000); } catch { /* ignore */ }
    }
}

/// <summary>UDP：绑定本地端口，发往远程 host:port；未指定远程地址时回复最后一个来包的地址。</summary>
public sealed class UdpConnection : ConnectionBase
{
    private const int MaxDatagram = 65507;
    private readonly UdpClient _udp;
    private readonly IPEndPoint? _remote;
    private volatile IPEndPoint? _lastSender;
    private Thread? _reader;

    private UdpConnection(UdpClient udp, IPEndPoint? remote, ConcurrentQueue<DataChunk>? output) : base(output)
    {
        _udp = udp;
        _remote = remote;
        LocalPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    public override ConnectionKind Kind => ConnectionKind.Udp;
    public int LocalPort { get; }
    public override string Name => _remote == null ? $"UDP :{LocalPort}" : $"UDP :{LocalPort} → {_remote}";

    /// <param name="localPort">0 = 系统分配</param>
    /// <param name="remoteHost">可为空（只接收 / 回复来源）</param>
    public static async Task<UdpConnection> OpenAsync(int localPort, string? remoteHost, int remotePort, ConcurrentQueue<DataChunk>? output)
    {
        IPEndPoint? remote = null;
        if (!string.IsNullOrWhiteSpace(remoteHost) && remotePort > 0)
            remote = new IPEndPoint(await NetUtil.ResolveAsync(remoteHost!).ConfigureAwait(false), remotePort);
        var udp = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
        try
        {
            udp.EnableBroadcast = true;
            // Windows：对端端口不可达时不要让后续 Receive 抛 ConnectionReset
            if (OperatingSystem.IsWindows())
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
            }
        }
        catch
        {
            // ignore
        }
        return new UdpConnection(udp, remote, output);
    }

    protected override void StartReading()
    {
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "UdpReader" };
        _reader.Start();
    }

    private void ReadLoop()
    {
        while (!Stopping)
        {
            byte[] data;
            IPEndPoint? ep = null;
            try
            {
                data = _udp.Receive(ref ep);
            }
            catch (SocketException se) when (se.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            catch (Exception ex)
            {
                if (!Stopping) Fault("UDP 接收出错：" + NetUtil.Describe(ex));
                break;
            }
            if (ep != null) _lastSender = ep;
            OnReceived(data, ep?.ToString());
        }
    }

    protected override bool WriteCore(byte[] data, out string? target)
    {
        var dest = _remote ?? _lastSender;
        target = null;
        if (dest == null)
        {
            RaiseWarning("UDP 未设置远程地址，且尚未收到任何数据包，无法确定发送目标");
            return false;
        }
        for (int off = 0; off < data.Length; off += MaxDatagram)
        {
            int n = Math.Min(MaxDatagram, data.Length - off);
            if (off == 0 && n == data.Length) _udp.Send(data, n, dest);
            else _udp.Send(data.AsSpan(off, n).ToArray(), n, dest);
        }
        target = _remote == null ? dest.ToString() : null;
        return true;
    }

    protected override void CloseCore()
    {
        try { _udp.Close(); } catch { /* ignore */ }
    }

    protected override void JoinReaders()
    {
        try { _reader?.Join(2000); } catch { /* ignore */ }
    }
}

/// <summary>
/// 串口转发：把本地连接（主连接，通常是串口）与一个网络端点双向桥接。
/// 本地 → 网络：主窗口把本地连接收到的数据交给 <see cref="FromLocal"/>；
/// 网络 → 本地：通过 sendToLocal 委托写回本地连接（本地重连后依然有效）。
/// </summary>
public sealed class SerialBridge
{
    private readonly IConnection _net;
    private readonly Func<byte[], bool> _sendToLocal;
    private long _toNet, _toLocal, _dropped;

    public SerialBridge(IConnection network, Func<byte[], bool> sendToLocal)
    {
        _net = network;
        _sendToLocal = sendToLocal;
        _net.Received += OnNetReceived;
    }

    public IConnection Network => _net;
    public long LocalToNetBytes => Interlocked.Read(ref _toNet);
    public long NetToLocalBytes => Interlocked.Read(ref _toLocal);
    public long DroppedBytes => Interlocked.Read(ref _dropped);

    public void Start() => _net.Start();

    /// <summary>本地连接收到数据（可在任何线程调用）。</summary>
    public void FromLocal(byte[] data)
    {
        if (data.Length == 0) return;
        if (_net.Send(data)) Interlocked.Add(ref _toNet, data.Length);
        else Interlocked.Add(ref _dropped, data.Length);
    }

    private void OnNetReceived(byte[] data)
    {
        bool ok;
        try { ok = _sendToLocal(data); }
        catch { ok = false; }
        if (ok) Interlocked.Add(ref _toLocal, data.Length);
        else Interlocked.Add(ref _dropped, data.Length);
    }

    public void Stop()
    {
        _net.Received -= OnNetReceived;
        _net.Stop();
    }
}
