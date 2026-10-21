using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerialTool;

/// <summary>
/// 设置保存位置：若 exe 同目录存在 settings.json（便携模式）则使用它，
/// 否则使用 %AppData%\SerialTool\settings.json。
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath { get; } = ResolvePath();

    private static string ResolvePath()
    {
        try
        {
            string portable = Path.Combine(AppContext.BaseDirectory, "settings.json");
            if (File.Exists(portable)) return portable;
        }
        catch
        {
            // ignore
        }

        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SerialTool");
        return Path.Combine(dir, "settings.json");
    }

    public static AppSettings Load()
    {
        AppSettings? s = null;
        try
        {
            if (File.Exists(FilePath))
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath, Encoding.UTF8), Options);
        }
        catch (Exception ex)
        {
            App.LogCrash(new Exception("读取设置失败，已使用默认设置", ex));
        }

        s ??= new AppSettings();
        s.History ??= new List<HistoryItem>();
        s.QuickCommands ??= AppSettings.DefaultQuickCommands();
        s.History.RemoveAll(h => h == null || h.Text == null);
        s.QuickCommands.RemoveAll(q => q == null);
        foreach (var q in s.QuickCommands)
        {
            q.Name ??= "";
            q.Content ??= "";
        }
        s.SendText ??= "";
        s.Encoding ??= "UTF-8";
        s.LineEnding ??= "";
        if (s.BaudRate <= 0) s.BaudRate = 115200;
        if (s.DataBits < 5 || s.DataBits > 8) s.DataBits = 8;
        if (s.TimerIntervalMs < 1) s.TimerIntervalMs = 1000;
        if (s.FrameGapMs < 0) s.FrameGapMs = 20;
        s.ProtocolMode ??= "普通";
        if (s.WaveWindowLength < 64) s.WaveWindowLength = 1024;
        s.ScriptText ??= "";
        s.Theme ??= "Light";
        s.Checksum ??= "None";
        s.HighlightRules ??= AppSettings.DefaultHighlightRules();
        s.HighlightRules.RemoveAll(r => r == null);
        s.Modbus ??= new ModbusMasterSettings();
        s.Modbus.Values ??= "";
        if (s.Modbus.TimeoutMs < 10) s.Modbus.TimeoutMs = 1000;
        if (s.Modbus.PollMs < 50) s.Modbus.PollMs = 1000;
        s.MonitorPorts ??= new List<MonitorPortSettings>();
        s.MonitorPorts.RemoveAll(m => m == null);
        foreach (var m in s.MonitorPorts)
        {
            m.PortName ??= "";
            m.Alias ??= "";
            m.Format ??= "8N1";
            m.Color ??= "#2563EB";
            if (m.BaudRate <= 0) m.BaudRate = 115200;
        }
        s.ConnType ??= "Serial";
        s.RemoteHost ??= "127.0.0.1";
        s.BridgeType ??= "TcpServer";
        s.BridgeHost ??= "127.0.0.1";
        if (s.CurveWindow < 50) s.CurveWindow = 500;
        s.FrameTemplateName ??= "";
        s.Slave ??= new ModbusSlaveSettings();
        s.Slave.Framing ??= "Rtu";
        if (s.Slave.SlaveId < 1 || s.Slave.SlaveId > 247) s.Slave.SlaveId = 1;
        if (s.Slave.Size < 1 || s.Slave.Size > 10000) s.Slave.Size = 100;
        s.TransferProtocol ??= "YModem";
        s.TransferDir ??= "";
        s.ReplaySpeed ??= "1";
        s.ReplayTarget ??= "View";
        s.ReplayContent ??= "All";
        s.BaudProbeText ??= "AT\\r\\n";
        if (s.BaudListenMs < 100 || s.BaudListenMs > 10000) s.BaudListenMs = 600;
        s.ScriptLanguage ??= "DSL";
        s.LuaScriptText ??= "";
        s.TestSuitePath ??= "";
        if (s.TestLoops < 1) s.TestLoops = 1;
        s.Gateway ??= new GatewaySettings();
        s.Gateway.Mode ??= "TcpToRtu";
        s.Gateway.RemoteHost ??= "127.0.0.1";
        s.Gateway.SessionId ??= "";
        if (s.Gateway.TimeoutMs < 50 || s.Gateway.TimeoutMs > 60000) s.Gateway.TimeoutMs = 1000;
        AppSettings.MigrateSessions(s);
        foreach (var r in s.HighlightRules)
        {
            r.Name ??= "";
            r.Pattern ??= "";
            r.Color ??= "#FDE047";
            r.ReplyContent ??= "";
            if (r.ReplyDelayMs < 0) r.ReplyDelayMs = 0;
            if (r.CooldownMs < 0) r.CooldownMs = 0;
        }
        return s;
    }

    public static bool Save(AppSettings s)
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Options), new UTF8Encoding(false));
            File.Move(tmp, FilePath, true);
            return true;
        }
        catch (Exception ex)
        {
            App.LogCrash(new Exception("保存设置失败", ex));
            return false;
        }
    }
}
