using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text.Json.Serialization;

namespace SerialTool;

/// <summary>一段收到或发出的原始数据。</summary>
public sealed record DataChunk(bool IsTx, byte[] Data, DateTime Time);

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

    // 窗口
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public double SendPanelHeight { get; set; }

    public static List<QuickCommand> DefaultQuickCommands() => new()
    {
        new QuickCommand { Name = "AT", Content = "AT" },
        new QuickCommand { Name = "版本 AT+GMR", Content = "AT+GMR" },
        new QuickCommand { Name = "复位 AT+RST", Content = "AT+RST" },
        new QuickCommand { Name = "HEX 示例", Content = "AA 55 01 00", IsHex = true },
    };
}
