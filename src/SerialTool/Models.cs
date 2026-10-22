using System;
using System.Linq;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text.Json.Serialization;

namespace SerialTool;

/// <summary>一段收到或发出的原始数据。</summary>
public sealed record DataChunk(bool IsTx, byte[] Data, DateTime Time)
{
    /// <summary>发送时自动追加的校验字节位置（-1 = 无），用于在文本回显里以 [HEX] 形式显示。</summary>
    public int MarkStart { get; init; } = -1;
    public int MarkLength { get; init; }
    /// <summary>网络连接的来源 / 目标（如 TCP 服务器的客户端地址、UDP 来包地址），串口为 null。</summary>
    public string? Source { get; init; }
    /// <summary>v1.4：来自会话回放（不触发自动回复 / 脚本 / 从站 / 录制）。</summary>
    public bool Replayed { get; init; }
}

/// <summary>下拉框选项：显示文本 + 实际值。</summary>
public sealed class Opt<T>
{
    public Opt(string text, T value)
    {
        Text = text;
        Value = value;
    }

    public string Text { get; }
    public T Value { get; }
    public override string ToString() => Text;
}

public sealed class PortItem
{
    public PortItem(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public string Name { get; }
    public string Description { get; }
    public override string ToString() => string.IsNullOrEmpty(Description) ? Name : $"{Name}  {Description}";
}

public sealed class QuickCommand
{
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
    public bool IsHex { get; set; }

    public QuickCommand Clone() => new() { Name = Name, Content = Content, IsHex = IsHex };
}

public sealed class HistoryItem
{
    public string Text { get; set; } = "";
    public bool IsHex { get; set; }

    [JsonIgnore]
    public string Display
    {
        get
        {
            string s = Text.Replace("\r\n", "↵").Replace('\n', '↵').Replace('\r', '↵');
            if (s.Length > 120) s = s.Substring(0, 120) + "…";
            return (IsHex ? "[HEX] " : "") + s;
        }
    }
}

public sealed class AppSettings
{
    // 串口参数
    public string? PortName { get; set; }
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Parity Parity { get; set; } = Parity.None;
    public Handshake Handshake { get; set; } = Handshake.None;
    public bool Dtr { get; set; }
    public bool Rts { get; set; }
    public bool AutoReconnect { get; set; } = true;

    // 接收
    public bool HexDisplay { get; set; }
    public bool ShowTimestamp { get; set; }
    public bool ShowTx { get; set; }
    public bool AutoScroll { get; set; } = true;
    public string Encoding { get; set; } = "UTF-8";
    public int FrameGapMs { get; set; } = 20;

    // 发送
    public bool HexSend { get; set; }
    public bool Escape { get; set; }
    public string LineEnding { get; set; } = "";
    public int TimerIntervalMs { get; set; } = 1000;
    public string SendText { get; set; } = "";
    public List<HistoryItem> History { get; set; } = new();
    public List<QuickCommand> QuickCommands { get; set; } = DefaultQuickCommands();

    // 协议 / 波形 / 脚本
    public string ProtocolMode { get; set; } = "普通"; // 普通 / AT / Modbus RTU
    public bool WaveShowTx { get; set; } = true;
    public int WaveWindowLength { get; set; } = 1024;
    public string ScriptText { get; set; } = "";

    // v1.2
    public string Theme { get; set; } = "Light";             // Light / Dark / System
    public string Checksum { get; set; } = "None";          // ChecksumKind 名称
    public bool HighlightEnabled { get; set; } // 默认关闭，保持 v1.1 的普通文本视图
    public bool AutoReplyEnabled { get; set; }
    public List<SerialTool.Features.HighlightRule> HighlightRules { get; set; } = DefaultHighlightRules();

    // v1.3：网络连接 / 转发 / Modbus 主站 / 多端口 / 曲线
    public string ConnType { get; set; } = "Serial";       // Serial / TcpClient / TcpServer / Udp
    public string RemoteHost { get; set; } = "127.0.0.1";
    public int RemotePort { get; set; } = 8080;
    public int LocalPort { get; set; } = 8080;
    public string BridgeType { get; set; } = "TcpServer";
    public string BridgeHost { get; set; } = "127.0.0.1";
    public int BridgePort { get; set; } = 9000;
    public int BridgeLocalPort { get; set; } = 9000;
    public ModbusMasterSettings Modbus { get; set; } = new();
    public List<MonitorPortSettings> MonitorPorts { get; set; } = new();
    public int CurveWindow { get; set; } = 500;

    // v1.4：帧模板 / Modbus 从站 / 文件传输 / 录制回放 / 波特率识别
    public string FrameTemplateName { get; set; } = "";
    public bool FrameParseEnabled { get; set; }
    public bool FrameParseTx { get; set; }
    public ModbusSlaveSettings Slave { get; set; } = new();
    public string TransferProtocol { get; set; } = "YModem";
    public string TransferDir { get; set; } = "";
    public string ReplaySpeed { get; set; } = "1";
    public string ReplayTarget { get; set; } = "View";
    public string ReplayContent { get; set; } = "All";
    public bool BaudProbe { get; set; } = true;
    public string BaudProbeText { get; set; } = "AT\\r\\n";
    public int BaudListenMs { get; set; } = 600;

    // v1.5：会话标签页 / 批量测试 / Modbus 网关 / Lua
    /// <summary>会话标签页列表。第 0 项是“主会话”（使用上面的旧字段，保存时同步一份快照），其余为附加会话。</summary>
    public List<SessionSettings> Sessions { get; set; } = new();
    public int ActiveSessionIndex { get; set; }
    public string ScriptLanguage { get; set; } = "DSL";     // 主会话脚本类型：DSL / Lua
    public string LuaScriptText { get; set; } = "";
    public string TestSuitePath { get; set; } = "";
    public int TestLoops { get; set; } = 1;
    public GatewaySettings Gateway { get; set; } = new();

    // v1.6：多项发送
    public List<SerialTool.Features.MultiSendItem> MultiSendItems { get; set; } = new();
    public bool MultiSendLoop { get; set; }

    /// <summary>
    /// 迁移 / 规范化会话列表：v1.4 及以前没有 Sessions，把原来的单会话迁移为“会话 1”（第 1 个标签页）。
    /// 返回 true 表示做了迁移。
    /// </summary>
    public static bool MigrateSessions(AppSettings s)
    {
        bool migrated = false;
        s.Sessions ??= new List<SessionSettings>();
        s.Sessions.RemoveAll(x => x == null);
        if (s.Sessions.Count == 0)
        {
            s.Sessions.Add(SessionSettings.FromLegacy(s, "会话 1"));
            migrated = true;
        }
        var ids = new HashSet<string>();
        for (int i = 0; i < s.Sessions.Count; i++)
        {
            var x = s.Sessions[i];
            x.Normalize();
            if (string.IsNullOrWhiteSpace(x.Name)) x.Name = $"会话 {i + 1}";
            if (!ids.Add(x.Id))
            {
                x.Id = Guid.NewGuid().ToString("N");
                ids.Add(x.Id);
            }
        }
        if (s.ActiveSessionIndex < 0 || s.ActiveSessionIndex >= s.Sessions.Count) s.ActiveSessionIndex = 0;
        return migrated;
    }

    // 窗口
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public double SendPanelHeight { get; set; }

    public static List<SerialTool.Features.HighlightRule> DefaultHighlightRules() => new()
    {
        new() { Name = "错误", Pattern = "ERROR", Color = "#F87171", Enabled = true },
        new() { Name = "信号", Pattern = @"\+CSQ: ?\d+", IsRegex = true, Color = "#FDE047", Enabled = true },
        new() { Name = "心跳应答示例", Pattern = "PING", Color = "#A78BFA", Enabled = false,
                ReplyEnabled = true, ReplyContent = "PONG", ReplyDelayMs = 0, CooldownMs = 1000 },
    };

    public static List<QuickCommand> DefaultQuickCommands() => new()
    {
        new QuickCommand { Name = "AT", Content = "AT" },
        new QuickCommand { Name = "版本 AT+GMR", Content = "AT+GMR" },
        new QuickCommand { Name = "复位 AT+RST", Content = "AT+RST" },
        new QuickCommand { Name = "HEX 示例", Content = "AA 55 01 00", IsHex = true },
    };
}

public sealed class ModbusMasterSettings
{
    public int Slave { get; set; } = 1;
    public int Function { get; set; } = 3;
    public int Start { get; set; }
    public int Quantity { get; set; } = 10;
    public string Values { get; set; } = "";
    public int TimeoutMs { get; set; } = 1000;
    public int PollMs { get; set; } = 1000;
    public string Format { get; set; } = "UInt16";
    public string Order { get; set; } = "ABCD";
    public bool TcpFraming { get; set; }
}

public sealed class MonitorPortSettings
{
    public string PortName { get; set; } = "";
    public int BaudRate { get; set; } = 115200;
    public string Format { get; set; } = "8N1";
    public string Alias { get; set; } = "";
    public string Color { get; set; } = "#2563EB";
    public bool Hex { get; set; }
}

public sealed class ModbusSlaveSettings
{
    public int SlaveId { get; set; } = 1;
    public string Framing { get; set; } = "Rtu";
    public int Size { get; set; } = 100;
}

/// <summary>一个会话（标签页）的全部独立设置：连接参数 + 显示 / 发送选项 + 脚本。</summary>
public sealed class SessionSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "会话";
    // 连接
    public string ConnType { get; set; } = "Serial";       // Serial / TcpClient / TcpServer / Udp
    public string PortName { get; set; } = "";
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Parity Parity { get; set; } = Parity.None;
    public Handshake Handshake { get; set; } = Handshake.None;
    public bool Dtr { get; set; }
    public bool Rts { get; set; }
    public string RemoteHost { get; set; } = "127.0.0.1";
    public int RemotePort { get; set; } = 8080;
    public int LocalPort { get; set; } = 8080;
    // 接收显示
    public bool HexDisplay { get; set; }
    public bool ShowTimestamp { get; set; }
    public bool ShowTx { get; set; }
    public string Encoding { get; set; } = "UTF-8";
    public int FrameGapMs { get; set; } = 20;
    public string ProtocolMode { get; set; } = "普通";
    public bool AutoReplyEnabled { get; set; }
    // 发送
    public bool HexSend { get; set; }
    public bool Escape { get; set; }
    public string Checksum { get; set; } = "None";
    public string LineEnding { get; set; } = "";
    public int TimerIntervalMs { get; set; } = 1000;
    public string SendText { get; set; } = "";
    // 脚本
    public string ScriptLanguage { get; set; } = "DSL";
    public string ScriptText { get; set; } = "";
    public string DslScriptText { get; set; } = "";
    public string LuaScriptText { get; set; } = "";
    public List<SerialTool.Features.MultiSendItem> MultiSendItems { get; set; } = new();
    public bool MultiSendLoop { get; set; }

    public SessionSettings Clone()
    {
        var c = (SessionSettings)MemberwiseClone();
        c.MultiSendItems = MultiSendItems?.Select(x => x.Clone()).ToList() ?? new List<SerialTool.Features.MultiSendItem>();
        return c;
    }

    public void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id;
        Name ??= "";
        ConnType ??= "Serial";
        PortName ??= "";
        RemoteHost ??= "127.0.0.1";
        Encoding ??= "UTF-8";
        ProtocolMode ??= "普通";
        Checksum ??= "None";
        LineEnding ??= "";
        SendText ??= "";
        ScriptLanguage ??= "DSL";
        ScriptText ??= "";
        DslScriptText ??= "";
        LuaScriptText ??= "";
        MultiSendItems ??= new List<SerialTool.Features.MultiSendItem>();
        MultiSendItems.RemoveAll(x => x == null);
        foreach (var it in MultiSendItems) it.Normalize();
        if (BaudRate <= 0) BaudRate = 115200;
        if (DataBits < 5 || DataBits > 8) DataBits = 8;
        if (TimerIntervalMs < 10) TimerIntervalMs = 1000;
        if (FrameGapMs < 0) FrameGapMs = 20;
        if (RemotePort <= 0 || RemotePort > 65535) RemotePort = 8080;
        if (LocalPort < 0 || LocalPort > 65535) LocalPort = 8080;
    }

    /// <summary>从 v1.4 的单会话字段生成会话设置（迁移 / 主会话快照）。</summary>
    public static SessionSettings FromLegacy(AppSettings s, string name, string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"),
        Name = name,
        ConnType = s.ConnType ?? "Serial",
        PortName = s.PortName ?? "",
        BaudRate = s.BaudRate,
        DataBits = s.DataBits,
        StopBits = s.StopBits,
        Parity = s.Parity,
        Handshake = s.Handshake,
        Dtr = s.Dtr,
        Rts = s.Rts,
        RemoteHost = s.RemoteHost ?? "127.0.0.1",
        RemotePort = s.RemotePort,
        LocalPort = s.LocalPort,
        HexDisplay = s.HexDisplay,
        ShowTimestamp = s.ShowTimestamp,
        ShowTx = s.ShowTx,
        Encoding = s.Encoding ?? "UTF-8",
        FrameGapMs = s.FrameGapMs,
        ProtocolMode = s.ProtocolMode ?? "普通",
        AutoReplyEnabled = s.AutoReplyEnabled,
        HexSend = s.HexSend,
        Escape = s.Escape,
        Checksum = s.Checksum ?? "None",
        LineEnding = s.LineEnding ?? "",
        TimerIntervalMs = s.TimerIntervalMs,
        SendText = s.SendText ?? "",
        ScriptLanguage = s.ScriptLanguage ?? "DSL",
        ScriptText = s.ScriptText ?? "",
        MultiSendItems = s.MultiSendItems?.Select(x => x.Clone()).ToList() ?? new List<SerialTool.Features.MultiSendItem>(),
        MultiSendLoop = s.MultiSendLoop,
    };
}

public sealed class GatewaySettings
{
    public string Mode { get; set; } = "TcpToRtu";   // TcpToRtu / RtuToTcp
    public int TcpPort { get; set; } = 502;
    public string RemoteHost { get; set; } = "127.0.0.1";
    public int RemotePort { get; set; } = 502;
    public int TimeoutMs { get; set; } = 1000;
    public int UnitOverride { get; set; }            // 0 = 不改写
    public string SessionId { get; set; } = "";
}
