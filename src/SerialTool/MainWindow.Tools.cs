using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using SerialTool.Features;

namespace SerialTool;

/// <summary>v1.4：帧模板解析 / Modbus 从站 / XModem·YModem / 会话录制回放 / 波特率识别。</summary>
public partial class MainWindow
{
    // ---------- 帧解析 ----------
    private List<FrameTemplate> _frameTemplates = new();
    private FrameParser? _frameParserRx, _frameParserTx;
    private readonly ObservableCollection<FrameRow> _frameRows = new();
    private const int MaxFrameRows = 5000;
    private string FrameTemplatePath => FrameTemplateStore.PathIn(Path.GetDirectoryName(SettingsStore.FilePath) ?? AppContext.BaseDirectory);

    // ---------- Modbus 从站 ----------
    private ModbusDataModel _slaveModel = new(100);
    private ModbusSlave? _slave;
    private readonly object _slaveLock = new();
    private volatile bool _slaveEnabled;
    private readonly ObservableCollection<SlaveRow> _slaveRows = new();
    private readonly ObservableCollection<SlaveLogRow> _slaveLog = new();
    private readonly ConcurrentQueue<ModbusSlaveLogEntry> _slaveLogQueue = new();
    private const int MaxSlaveLog = 2000;

    // ---------- 文件传输 ----------
    private volatile FeedLink? _xferLink;
    private CancellationTokenSource? _xferCts;
    private volatile bool _transferActive;
    private List<string> _xferFiles = new();

    // ---------- 录制 / 回放 ----------
    private SessionRecorder? _sessionRec;
    private string? _sessionRecPath;
    private List<RecordEntry>? _replayEntries;
    private SessionReplayer? _replayer;
    private CancellationTokenSource? _replayCts;
    private string? _replayError;

    private static readonly List<Opt<XModemVariant>> XferOptions = new()
    {
        new("XModem（128 字节，校验和）", XModemVariant.XModemChecksum),
        new("XModem（128 字节，CRC16）", XModemVariant.XModemCrc),
        new("XModem-1K（1024 字节，CRC16）", XModemVariant.XModem1K),
        new("YModem（批量，1K，CRC16）", XModemVariant.YModem),
    };
    private static readonly List<Opt<ModbusFraming>> SlaveFramingOptions = new()
    {
        new("RTU（带 CRC）", ModbusFraming.Rtu),
        new("Modbus TCP", ModbusFraming.Tcp),
    };
    private static readonly List<Opt<string>> ReplayTargetOptions = new()
    {
        new("接收区显示", "View"),
        new("主连接发送", "Port"),
    };
    private static readonly List<Opt<string>> ReplayContentOptions = new()
    {
        new("全部", "All"),
        new("仅接收 RX", "Rx"),
        new("仅发送 TX", "Tx"),
    };
    private static readonly List<Opt<double>> ReplaySpeedOptions = new()
    {
        new("0.5x", 0.5), new("1x（原始节奏）", 1), new("2x", 2), new("最快", 0),
    };

    // ======================================================================
    // 初始化 / 设置
    // ======================================================================

    private void InitControlsV14()
    {
        var s = _settings;
        // 帧模板
        _frameTemplates = FrameTemplateStore.Load(FrameTemplatePath);
        if (!File.Exists(FrameTemplatePath))
        {
            try { FrameTemplateStore.Save(FrameTemplatePath, _frameTemplates); } catch (Exception ex) { App.LogCrash(ex); }
        }
        FrameGrid.ItemsSource = _frameRows;
        FrameEnableChk.IsChecked = s.FrameParseEnabled;
        FrameTxChk.IsChecked = s.FrameParseTx;
        RefreshFrameTemplates(s.FrameTemplateName);

        // 从站
        _slaveModel = new ModbusDataModel(s.Slave.Size);
        _slave = new ModbusSlave(_slaveModel);
        SlaveIdBox.Text = s.Slave.SlaveId.ToString(CultureInfo.InvariantCulture);
        SlaveSizeBox.Text = s.Slave.Size.ToString(CultureInfo.InvariantCulture);
        SlaveFramingCombo.ItemsSource = SlaveFramingOptions;
        SlaveFramingCombo.SelectedItem = SlaveFramingOptions.FirstOrDefault(o => o.Value.ToString() == s.Slave.Framing) ?? SlaveFramingOptions[0];
        SlaveGrid.ItemsSource = _slaveRows;
        SlaveLogGrid.ItemsSource = _slaveLog;
        RebuildSlaveRows();
        ApplySlaveOptions();

        // 文件传输
        XferProtoCombo.ItemsSource = XferOptions;
        XferProtoCombo.SelectedItem = XferOptions.FirstOrDefault(o => o.Value.ToString() == s.TransferProtocol) ?? XferOptions[3];
        XferDirBox.Text = string.IsNullOrWhiteSpace(s.TransferDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SerialTool", "received")
            : s.TransferDir;

        // 回放
        ReplayTargetCombo.ItemsSource = ReplayTargetOptions;
        ReplayTargetCombo.SelectedItem = ReplayTargetOptions.FirstOrDefault(o => o.Value == s.ReplayTarget) ?? ReplayTargetOptions[0];
        ReplayContentCombo.ItemsSource = ReplayContentOptions;
        ReplayContentCombo.SelectedItem = ReplayContentOptions.FirstOrDefault(o => o.Value == s.ReplayContent) ?? ReplayContentOptions[0];
        ReplaySpeedCombo.ItemsSource = ReplaySpeedOptions;
        ReplaySpeedCombo.SelectedItem = ReplaySpeedOptions.FirstOrDefault(o => o.Value.ToString(CultureInfo.InvariantCulture) == s.ReplaySpeed) ?? ReplaySpeedOptions[1];
    }

    private void CollectSettingsV14()
    {
        var s = _settings;
        s.FrameTemplateName = (FrameTplCombo.SelectedItem as FrameTemplate)?.Name ?? "";
        s.FrameParseEnabled = FrameEnableChk.IsChecked == true;
        s.FrameParseTx = FrameTxChk.IsChecked == true;
        if (int.TryParse(SlaveIdBox.Text.Trim(), out int id) && id >= 1 && id <= 247) s.Slave.SlaveId = id;
        s.Slave.Framing = ((SlaveFramingCombo.SelectedItem as Opt<ModbusFraming>)?.Value ?? ModbusFraming.Rtu).ToString();
        s.Slave.Size = _slaveModel.Size;
        s.TransferProtocol = ((XferProtoCombo.SelectedItem as Opt<XModemVariant>)?.Value ?? XModemVariant.YModem).ToString();
        s.TransferDir = XferDirBox.Text.Trim();
        s.ReplayTarget = (ReplayTargetCombo.SelectedItem as Opt<string>)?.Value ?? "View";
        s.ReplayContent = (ReplayContentCombo.SelectedItem as Opt<string>)?.Value ?? "All";
        s.ReplaySpeed = ((ReplaySpeedCombo.SelectedItem as Opt<double>)?.Value ?? 1).ToString(CultureInfo.InvariantCulture);
    }

    private void InitV14()
    {
        _statsTimer.Tick += (_, _) =>
        {
            UpdateSlaveStats();
            UpdateRecordUi();
        };
    }

    private void ShutdownV14()
    {
        try { _xferCts?.Cancel(); } catch { /* ignore */ }
        try { _replayCts?.Cancel(); } catch { /* ignore */ }
        StopSessionRecording(null);
    }

    /// <summary>主连接即将关闭：取消正在进行的文件传输与“发送到主连接”的回放。</summary>
    private void OnMainConnectionClosingV14()
    {
        if (_xferCts != null)
        {
            try { _xferCts.Cancel(); } catch { /* ignore */ }
        }
        if (_replayCts != null && (ReplayTargetCombo.SelectedItem as Opt<string>)?.Value == "Port")
        {
            try { _replayCts.Cancel(); } catch { /* ignore */ }
        }
    }

    // ======================================================================
    // 后台接收钩子 / UI 刷新钩子
    // ======================================================================

    /// <summary>主连接收到数据（后台线程）。返回 true 表示数据被文件传输独占。</summary>
    private bool OnConnReceivedV14(byte[] data)
    {
        var link = _xferLink;
        if (link != null)
        {
            link.Feed(data);
            return true;
        }
        if (_slaveEnabled) HandleSlaveRx(data);
        return false;
    }

    /// <summary>FlushQueue 对每个数据块调用。返回 false 表示本块不再显示（文件传输期间）。</summary>
    private bool ProcessChunkV14(DataChunk c)
    {
        if (!c.Replayed)
        {
            var rec = _sessionRec;
            if (rec != null)
            {
                try { rec.Write(c.IsTx, c.Data, c.Time, c.Source); }
                catch (Exception ex) { StopSessionRecording("录制写入失败，已停止：" + ex.Message); }
            }
        }
        if (_transferActive && !c.Replayed) return false;
        FeedFrameParsers(c);
        FeedAsciiParsers(c);
        return true;
    }

    private void FlushV14()
    {
        FlushFrameRows();
        FlushAsciiRows();
        FlushSlaveLog();
        UpdateReplayUi();
    }

    // ======================================================================
    // 帧解析
    // ======================================================================

    public sealed class FrameRow
    {
        public string Time { get; init; } = "";
        public string Dir { get; init; } = "";
        public string Status { get; init; } = "";
        public string[] Values { get; init; } = Array.Empty<string>();
        public string Raw { get; init; } = "";
        public bool Ok { get; init; }
    }

    private readonly List<FrameRow> _pendingFrameRows = new();

    private void RefreshFrameTemplates(string? select)
    {
        FrameTplCombo.ItemsSource = null;
        FrameTplCombo.ItemsSource = _frameTemplates;
        FrameTplCombo.SelectedItem = _frameTemplates.FirstOrDefault(t => t.Name == select) ?? _frameTemplates.FirstOrDefault();
        ResetFrameParsers();
    }

    private void ResetFrameParsers()
    {
        var tpl = FrameTplCombo.SelectedItem as FrameTemplate;
        string? err = tpl?.Validate();
        if (tpl == null || err != null)
        {
            _frameParserRx = _frameParserTx = null;
            FrameInfoText.Text = tpl == null ? "没有模板，请点“编辑模板…”新建。" : "模板无效：" + err;
        }
        else
        {
            _frameParserRx = new FrameParser(tpl);
            _frameParserTx = new FrameParser(tpl);
            FrameInfoText.Text = DescribeTemplate(tpl) + "    模板文件：" + FrameTemplatePath;
        }
        BuildFrameColumns(tpl);
        UpdateFrameStats();
    }

    private static string DescribeTemplate(FrameTemplate t)
    {
        var sb = new StringBuilder();
        sb.Append("帧头 ").Append(t.Header);
        if (t.HasLength) sb.Append($" | 长度@{t.LengthOffset} {t.LengthSize}B{(t.LengthSize > 1 ? (t.LengthBigEndian ? " 大端" : " 小端") : "")}");
        else if (t.FixedLength > 0) sb.Append($" | 固定长度 {t.FixedLength}");
        if (t.Checksum != ChecksumKind.None) sb.Append($" | {t.Checksum} 自 {t.ChecksumFrom}");
        if (!string.IsNullOrWhiteSpace(t.Tail)) sb.Append(" | 帧尾 ").Append(t.Tail);
        sb.Append($" | {t.Fields.Count} 个字段");
        return sb.ToString();
    }

    private void BuildFrameColumns(FrameTemplate? tpl)
    {
        FrameGrid.Columns.Clear();
        _frameRows.Clear();
        FrameGrid.Columns.Add(new DataGridTextColumn { Header = "时间", Binding = new Binding(nameof(FrameRow.Time)), Width = 96 });
        FrameGrid.Columns.Add(new DataGridTextColumn { Header = "方向", Binding = new Binding(nameof(FrameRow.Dir)), Width = 44 });
        FrameGrid.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new Binding(nameof(FrameRow.Status)), Width = 150 });
        if (tpl != null)
        {
            for (int i = 0; i < tpl.Fields.Count; i++)
            {
                var f = tpl.Fields[i];
                string header = string.IsNullOrEmpty(f.Unit) ? f.Name : $"{f.Name} ({f.Unit})";
                FrameGrid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding($"Values[{i}]"), Width = DataGridLength.Auto, MinWidth = 70 });
            }
        }
        FrameGrid.Columns.Add(new DataGridTextColumn { Header = "原始帧", Binding = new Binding(nameof(FrameRow.Raw)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 160 });
    }

    private void FeedFrameParsers(DataChunk c)
    {
        if (FrameEnableChk.IsChecked != true) return;
        var p = c.IsTx ? (FrameTxChk.IsChecked == true ? _frameParserTx : null) : _frameParserRx;
        if (p == null) return;
        foreach (var f in p.Push(c.Data, c.Time, c.IsTx))
        {
            string[] values = new string[p.Template.Fields.Count];
            for (int i = 0; i < values.Length; i++)
            {
                var name = p.Template.Fields[i].Name;
                values[i] = f.Fields.FirstOrDefault(x => x.Name == name)?.Value ?? "";
            }
            // 去掉单位（列头已带单位）
            for (int i = 0; i < values.Length; i++)
            {
                var unit = p.Template.Fields[i].Unit;
                if (!string.IsNullOrEmpty(unit) && values[i].EndsWith(" " + unit, StringComparison.Ordinal))
                    values[i] = values[i][..^(unit.Length + 1)];
            }
            _pendingFrameRows.Add(new FrameRow
            {
                Time = f.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                Dir = f.IsTx ? "TX" : "RX",
                Status = f.Ok ? "✓ 正确" : "✗ " + (f.Error ?? "错误"),
                Values = values,
                Raw = Checksum.ToHex(f.Raw),
                Ok = f.Ok,
            });
        }
    }

    private void FlushFrameRows()
    {
        if (_pendingFrameRows.Count == 0) return;
        var rows = _pendingFrameRows.Count > 1000 ? _pendingFrameRows.Skip(_pendingFrameRows.Count - 1000).ToList() : _pendingFrameRows.ToList();
        _pendingFrameRows.Clear();
        int overflow = _frameRows.Count + rows.Count - MaxFrameRows;
        if (overflow > 0)
        {
            if (overflow >= _frameRows.Count) _frameRows.Clear();
            else for (int i = 0; i < overflow; i++) _frameRows.RemoveAt(0);
        }
        foreach (var r in rows) _frameRows.Add(r);
        UpdateFrameStats();
        if (AutoScrollChk.IsChecked == true && FrameGrid.IsVisible && _frameRows.Count > 0)
        {
            var last = CollectionViewSource.GetDefaultView(_frameRows).Cast<object>().LastOrDefault();
            if (last != null) FrameGrid.ScrollIntoView(last);
        }
    }

    private void UpdateFrameStats()
    {
        long ok = (_frameParserRx?.OkCount ?? 0) + (_frameParserTx?.OkCount ?? 0);
        long bad = (_frameParserRx?.BadCount ?? 0) + (_frameParserTx?.BadCount ?? 0);
        long skip = (_frameParserRx?.SkippedBytes ?? 0) + (_frameParserTx?.SkippedBytes ?? 0);
        FrameStatsText.Text = $"正确 {ok}  错误 {bad}  丢弃 {skip} 字节";
    }

    private void FrameTpl_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading && FrameGrid.Columns.Count > 0) return;
        ResetFrameParsers();
    }

    private void FrameOption_Click(object sender, RoutedEventArgs e)
    {
        _frameParserRx?.Reset();
        _frameParserTx?.Reset();
    }

    private void FrameBadOnly_Click(object sender, RoutedEventArgs e)
    {
        var view = CollectionViewSource.GetDefaultView(_frameRows);
        view.Filter = FrameBadOnlyChk.IsChecked == true ? o => o is FrameRow r && !r.Ok : null;
    }

    private void FrameClear_Click(object sender, RoutedEventArgs e)
    {
        _frameRows.Clear();
        _pendingFrameRows.Clear();
        ResetFrameParsers();
    }

    private void FrameEdit_Click(object sender, RoutedEventArgs e)
    {
        var current = (FrameTplCombo.SelectedItem as FrameTemplate)?.Name;
        var dlg = new FrameTemplateWindow(_frameTemplates, current) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _frameTemplates = dlg.Result;
        try
        {
            FrameTemplateStore.Save(FrameTemplatePath, _frameTemplates);
            SetStatus($"帧模板已保存（{_frameTemplates.Count} 个）：{FrameTemplatePath}");
        }
        catch (Exception ex)
        {
            SetStatus("保存帧模板失败：" + ex.Message, true);
        }
        RefreshFrameTemplates(dlg.SelectedName ?? current);
    }

    private void FrameExport_Click(object sender, RoutedEventArgs e)
    {
        if (_frameRows.Count == 0)
        {
            SetStatus("没有可导出的帧", true);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "导出解析结果",
            FileName = $"frames_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", FrameGrid.Columns.Select(c => Csv(c.Header?.ToString() ?? ""))));
            foreach (var r in _frameRows)
                sb.AppendLine(string.Join(",", new[] { r.Time, r.Dir, r.Status }.Concat(r.Values).Append(r.Raw).Select(Csv)));
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            SetStatus($"已导出 {_frameRows.Count} 帧到 {dlg.FileName}");
        }
        catch (Exception ex)
        {
            SetStatus("导出失败：" + ex.Message, true);
        }
    }

    private static string Csv(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    // ======================================================================
    // Modbus 从站
    // ======================================================================

    public sealed class SlaveRow : INotifyPropertyChanged
    {
        private readonly ModbusDataModel _m;
        public SlaveRow(ModbusDataModel m, int address)
        {
            _m = m;
            Address = address;
        }

        public int Address { get; }

        public string HoldingText
        {
            get { lock (_m.SyncRoot) return Address < _m.Size ? _m.Holding[Address].ToString(CultureInfo.InvariantCulture) : ""; }
            set { if (TryParseReg(value, out ushort v)) lock (_m.SyncRoot) if (Address < _m.Size) _m.Holding[Address] = v; Raise(nameof(HoldingText)); }
        }

        public string InputText
        {
            get { lock (_m.SyncRoot) return Address < _m.Size ? _m.Input[Address].ToString(CultureInfo.InvariantCulture) : ""; }
            set { if (TryParseReg(value, out ushort v)) lock (_m.SyncRoot) if (Address < _m.Size) _m.Input[Address] = v; Raise(nameof(InputText)); }
        }

        public bool Coil
        {
            get { lock (_m.SyncRoot) return Address < _m.Size && _m.Coils[Address]; }
            set { lock (_m.SyncRoot) if (Address < _m.Size) _m.Coils[Address] = value; Raise(nameof(Coil)); }
        }

        public bool Discrete
        {
            get { lock (_m.SyncRoot) return Address < _m.Size && _m.DiscreteInputs[Address]; }
            set { lock (_m.SyncRoot) if (Address < _m.Size) _m.DiscreteInputs[Address] = value; Raise(nameof(Discrete)); }
        }

        public static bool TryParseReg(string? s, out ushort v)
        {
            v = 0;
            s = s?.Trim() ?? "";
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ushort.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= -32768 && i <= 65535)
            {
                v = unchecked((ushort)i);
                return true;
            }
            return false;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        public void RefreshAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public sealed class SlaveLogRow
    {
        public string TimeText { get; init; } = "";
        public int Slave { get; init; }
        public string Summary { get; init; } = "";
        public string Request { get; init; } = "";
        public string Response { get; init; } = "";
        public bool IsError { get; init; }
    }

    private void RebuildSlaveRows()
    {
        _slaveRows.Clear();
        for (int i = 0; i < _slaveModel.Size; i++) _slaveRows.Add(new SlaveRow(_slaveModel, i));
    }

    private void ApplySlaveOptions()
    {
        if (_slave == null) return;
        lock (_slaveLock)
        {
            if (int.TryParse(SlaveIdBox.Text.Trim(), out int id) && id >= 1 && id <= 247) _slave.SlaveId = (byte)id;
            else SlaveIdBox.Text = _slave.SlaveId.ToString(CultureInfo.InvariantCulture);
            _slave.Framing = (SlaveFramingCombo.SelectedItem as Opt<ModbusFraming>)?.Value ?? ModbusFraming.Rtu;
            _slave.Reset();
        }
    }

    private void HandleSlaveRx(byte[] data)
    {
        var slave = _slave;
        var conn = _conn;
        if (slave == null || conn == null) return;
        List<(byte[]? Response, ModbusSlaveLogEntry Log)> results;
        lock (_slaveLock) results = slave.Push(data, DateTime.Now);
        foreach (var (resp, log) in results)
        {
            if (resp != null) conn.Send(resp);
            _slaveLogQueue.Enqueue(log);
        }
    }

    private void FlushSlaveLog()
    {
        if (_slaveLogQueue.IsEmpty) return;
        bool any = false;
        var refresh = new HashSet<int>();
        while (_slaveLogQueue.TryDequeue(out var e))
        {
            _slaveLog.Add(new SlaveLogRow
            {
                TimeText = e.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                Slave = e.Slave,
                Summary = e.Summary,
                Request = e.Request,
                Response = e.Response,
                IsError = e.IsError,
            });
            any = true;
            if (e.WrittenArea != '\0')
                for (int a = e.WrittenStart; a < e.WrittenStart + e.WrittenCount && a < _slaveRows.Count; a++) refresh.Add(a);
        }
        while (_slaveLog.Count > MaxSlaveLog) _slaveLog.RemoveAt(0);
        foreach (int a in refresh) _slaveRows[a].RefreshAll();
        if (any && SlaveLogGrid.IsVisible && _slaveLog.Count > 0) SlaveLogGrid.ScrollIntoView(_slaveLog[^1]);
    }

    private void UpdateSlaveStats()
    {
        var s = _slave;
        if (s == null) return;
        SlaveStatsText.Text = $"请求 {s.Requests}  异常 {s.Exceptions}  CRC错 {s.CrcErrors}";
    }

    private void SlaveEnable_Click(object sender, RoutedEventArgs e)
    {
        ApplySlaveOptions();
        _slaveEnabled = SlaveEnableChk.IsChecked == true;
        SetStatus(_slaveEnabled
            ? $"Modbus 从站已启用：站号 {_slave?.SlaveId}，{(_slave?.Framing == ModbusFraming.Tcp ? "Modbus TCP" : "RTU")}，地址 0~{_slaveModel.Size - 1}"
            : "Modbus 从站已停用");
    }

    private void SlaveOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplySlaveOptions();
    }

    private void SlaveResize_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SlaveSizeBox.Text.Trim(), out int n) || n < 1 || n > 10000)
        {
            SetStatus("表大小需为 1 ~ 10000", true);
            SlaveSizeBox.Text = _slaveModel.Size.ToString(CultureInfo.InvariantCulture);
            return;
        }
        _slaveModel.Resize(n);
        RebuildSlaveRows();
        SetStatus($"从站数据表大小：{n}（地址 0 ~ {n - 1}）");
    }

    private void SlaveZero_Click(object sender, RoutedEventArgs e)
    {
        lock (_slaveModel.SyncRoot)
        {
            Array.Clear(_slaveModel.Coils);
            Array.Clear(_slaveModel.DiscreteInputs);
            Array.Clear(_slaveModel.Holding);
            Array.Clear(_slaveModel.Input);
        }
        foreach (var r in _slaveRows) r.RefreshAll();
    }

    private void SlaveClearLog_Click(object sender, RoutedEventArgs e)
    {
        _slaveLog.Clear();
        while (_slaveLogQueue.TryDequeue(out _)) { }
    }

    // ======================================================================
    // 文件传输
    // ======================================================================

    private XModemVariant CurrentXferVariant() => (XferProtoCombo.SelectedItem as Opt<XModemVariant>)?.Value ?? XModemVariant.YModem;

    private void XferPickFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择要发送的文件",
            Multiselect = CurrentXferVariant() == XModemVariant.YModem,
            Filter = "所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        _xferFiles = dlg.FileNames.ToList();
        XferFilesBox.Text = string.Join("; ", _xferFiles.Select(Path.GetFileName));
        XferFilesBox.ToolTip = string.Join("\n", _xferFiles);
    }

    private void XferPickDir_Click(object sender, RoutedEventArgs e)
    {
        // WPF 没有统一的目录对话框（兼容 .NET 6），用“保存文件”对话框选择目录
        var dlg = new SaveFileDialog
        {
            Title = "选择接收目录（进入目标文件夹后点“保存”）",
            FileName = "选择此文件夹",
            Filter = "文件夹|*.folder",
            CheckPathExists = true,
            OverwritePrompt = false,
        };
        if (Directory.Exists(XferDirBox.Text)) dlg.InitialDirectory = XferDirBox.Text;
        if (dlg.ShowDialog(this) != true) return;
        var dir = Path.GetDirectoryName(dlg.FileName);
        if (!string.IsNullOrEmpty(dir)) XferDirBox.Text = dir;
    }

    private void XferLog(string line)
    {
        XferLogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\r\n");
        XferLogBox.ScrollToEnd();
    }

    private async void XferSend_Click(object sender, RoutedEventArgs e)
    {
        var variant = CurrentXferVariant();
        if (_xferFiles.Count == 0)
        {
            SetStatus("请先选择要发送的文件", true);
            return;
        }
        if (variant != XModemVariant.YModem && _xferFiles.Count > 1)
        {
            SetStatus("XModem 一次只能发送一个文件，请改选 YModem 进行批量发送", true);
            return;
        }
        List<TransferFile> files;
        try
        {
            files = _xferFiles.Select(f => new TransferFile { Name = Path.GetFileName(f), Data = File.ReadAllBytes(f) }).ToList();
        }
        catch (Exception ex)
        {
            SetStatus("读取文件失败：" + ex.Message, true);
            return;
        }
        long total = files.Sum(f => (long)f.Data.Length);
        await RunTransferAsync($"发送 {files.Count} 个文件（{total:N0} 字节，{variant}）", (xm, ct) =>
        {
            xm.Send(files, variant, ct);
            return Task.FromResult<List<TransferFile>?>(null);
        });
    }

    private async void XferRecv_Click(object sender, RoutedEventArgs e)
    {
        var variant = CurrentXferVariant();
        string dir = XferDirBox.Text.Trim();
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            SetStatus("接收目录无效：" + ex.Message, true);
            return;
        }
        var got = await RunTransferAsync($"等待接收（{variant}），保存到 {dir}", (xm, ct) => Task.FromResult<List<TransferFile>?>(xm.Receive(variant, ct)));
        if (got == null) return;
        foreach (var f in got)
        {
            string name = string.IsNullOrEmpty(f.Name) ? $"xmodem_{DateTime.Now:yyyyMMdd_HHmmss}.bin" : SanitizeFileName(f.Name);
            string path = UniquePath(Path.Combine(dir, name));
            try
            {
                File.WriteAllBytes(path, f.Data);
                XferLog($"已保存：{path}（{f.Data.Length:N0} 字节）");
            }
            catch (Exception ex)
            {
                XferLog($"保存 {name} 失败：{ex.Message}");
            }
        }
        SetStatus($"接收完成：{got.Count} 个文件，保存到 {dir}");
    }

    private static string SanitizeFileName(string name)
    {
        name = name.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "received.bin" : name;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path) ?? "";
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    private async Task<List<TransferFile>?> RunTransferAsync(string title, Func<XYModem, CancellationToken, Task<List<TransferFile>?>> work)
    {
        var conn = _conn;
        if (conn == null)
        {
            SetStatus(NotOpenMessage, true);
            return null;
        }
        if (_xferCts != null) return null;
        StopTimedSend(null);
        try { _script?.Stop(); } catch { /* ignore */ }

        var link = new FeedLink(d => conn.Send(d));
        var cts = new CancellationTokenSource();
        _xferCts = cts;
        _transferActive = true;
        _xferLink = link;
        SetXferUi(true);
        XferProgress.Value = 0;
        XferLog(title);
        SetStatus("文件传输中：" + title + "（接收区显示已暂停）");

        var xm = new XYModem(link);
        DateTime lastUi = DateTime.MinValue;
        xm.Progress += p => Dispatcher.BeginInvoke(() =>
        {
            if (p.Message != null) XferLog((string.IsNullOrEmpty(p.FileName) ? "" : p.FileName + "：") + p.Message);
            if (p.Done >= 0)
            {
                if (p.Total > 0) XferProgress.Value = Math.Min(100, p.Done * 100.0 / p.Total);
                XferStatusText.Text = p.Total >= 0
                    ? $"{p.FileName}  {p.Done:N0} / {p.Total:N0} 字节  重试 {p.Retries}"
                    : $"{p.FileName}  {p.Done:N0} 字节  重试 {p.Retries}";
            }
        });

        List<TransferFile>? result = null;
        try
        {
            result = await Task.Run(() => work(xm, cts.Token));
            XferProgress.Value = 100;
            XferLog($"传输完成（重试 {xm.TotalRetries} 次）");
            XferStatusText.Text = "完成";
            SetStatus("文件传输完成");
        }
        catch (OperationCanceledException)
        {
            XferLog("已取消");
            XferStatusText.Text = "已取消";
            SetStatus("文件传输已取消", true);
            result = null;
        }
        catch (Exception ex)
        {
            XferLog("失败：" + ex.Message);
            XferStatusText.Text = "失败";
            SetStatus("文件传输失败：" + ex.Message, true);
            result = null;
        }
        finally
        {
            _xferLink = null;
            // 留一点时间让对方的残余字节（ACK / CAN）回来，不显示到接收区
            await Task.Delay(150);
            _transferActive = false;
            _xferCts = null;
            cts.Dispose();
            SetXferUi(false);
        }
        return result;
    }

    private void SetXferUi(bool running)
    {
        XferSendBtn.IsEnabled = XferRecvBtn.IsEnabled = XferProtoCombo.IsEnabled = !running;
        XferCancelBtn.IsEnabled = running;
    }

    private void XferCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _xferCts?.Cancel(); } catch { /* ignore */ }
    }

    // ======================================================================
    // 会话录制
    // ======================================================================

    private void Rec_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionRec != null)
        {
            StopSessionRecording(null);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "保存会话录制",
            FileName = $"session_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl",
            Filter = "会话录制 (*.jsonl)|*.jsonl|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _sessionRec = SessionRecorder.Create(dlg.FileName, $"SerialTool {_conn?.Name ?? "未连接"}");
            _sessionRecPath = dlg.FileName;
            RecBtn.Content = "停止录制";
            RecBtn.Style = (Style)FindResource("DangerButton");
            RecDot.Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            UpdateRecordUi();
            SetStatus("开始录制：" + dlg.FileName);
        }
        catch (Exception ex)
        {
            _sessionRec = null;
            SetStatus("无法创建录制文件：" + ex.Message, true);
        }
    }

    private void StopSessionRecording(string? error)
    {
        var r = _sessionRec;
        if (r == null) return;
        _sessionRec = null;
        try { r.Dispose(); } catch { /* ignore */ }
        if (RecBtn == null) return;
        RecBtn.Content = "开始录制…";
        RecBtn.Style = (Style)FindResource("AccentButton");
        RecDot.Fill = IdleBrush;
        RecStatusText.Text = $"已保存 {r.Count:N0} 条 / {r.Bytes:N0} 字节：{_sessionRecPath}";
        if (!_closingWindow) SetStatus(error ?? ("录制已停止：" + _sessionRecPath), error != null);
        if (_replayEntries == null && _sessionRecPath != null && !_closingWindow) LoadReplayFile(_sessionRecPath);
    }

    private void UpdateRecordUi()
    {
        var r = _sessionRec;
        if (r == null) return;
        try { r.Flush(); } catch { /* ignore */ }
        RecStatusText.Text = $"录制中 {r.Count:N0} 条 / {r.Bytes:N0} 字节 → {_sessionRecPath}";
    }

    // ======================================================================
    // 回放
    // ======================================================================

    private void ReplayOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "打开会话录制",
            Filter = "会话录制 (*.jsonl)|*.jsonl|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        LoadReplayFile(dlg.FileName);
    }

    private void LoadReplayFile(string path)
    {
        try
        {
            var list = SessionReader.ReadFile(path, out int bad);
            _replayEntries = list;
            ReplayFileBox.Text = path;
            double dur = list.Count > 0 ? (list[^1].OffsetMs - list[0].OffsetMs) / 1000.0 : 0;
            int rx = list.Count(x => !x.IsTx);
            ReplayStatusText.Text = $"{list.Count} 条（RX {rx} / TX {list.Count - rx}），时长 {dur:0.0} 秒" + (bad > 0 ? $"，{bad} 行无法解析" : "");
            ReplayProgress.Value = 0;
        }
        catch (Exception ex)
        {
            SetStatus("读取录制文件失败：" + ex.Message, true);
        }
    }

    private void ReplayStart_Click(object sender, RoutedEventArgs e)
    {
        if (_replayer != null) return;
        if (_replayEntries == null || _replayEntries.Count == 0)
        {
            SetStatus("请先打开一个录制文件", true);
            return;
        }
        string target = (ReplayTargetCombo.SelectedItem as Opt<string>)?.Value ?? "View";
        string content = (ReplayContentCombo.SelectedItem as Opt<string>)?.Value ?? "All";
        if (target == "Port" && _conn == null)
        {
            SetStatus("回放到主连接需要先打开串口 / 网络连接", true);
            return;
        }
        if (target == "Port" && _transferActive)
        {
            SetStatus("文件传输进行中，无法回放到主连接", true);
            return;
        }
        var rp = new SessionReplayer(_replayEntries)
        {
            Speed = (ReplaySpeedCombo.SelectedItem as Opt<double>)?.Value ?? 1,
            Filter = content switch
            {
                "Rx" => en => !en.IsTx,
                "Tx" => en => en.IsTx,
                _ => null,
            },
        };
        var cts = new CancellationTokenSource();
        _replayer = rp;
        _replayCts = cts;
        _replayError = null;
        ReplayBtn.IsEnabled = false;
        ReplayPauseBtn.IsEnabled = ReplayStopBtn.IsEnabled = true;
        ReplayPauseBtn.Content = "暂停";
        ReplayTargetCombo.IsEnabled = ReplayContentCombo.IsEnabled = false;
        SetStatus($"开始回放（{(target == "Port" ? "发送到主连接" : "接收区")}，{(ReplaySpeedCombo.SelectedItem as Opt<double>)?.Text}）");

        Action<RecordEntry> deliver;
        if (target == "Port")
        {
            deliver = en =>
            {
                var c = _conn;
                if (c == null)
                {
                    _replayError = "主连接已断开";
                    cts.Cancel();
                    return;
                }
                c.Send(en.Data);
                // 最快速度时避免把发送队列塞爆
                while (c.PendingBytes > 64 * 1024 && !cts.IsCancellationRequested) Thread.Sleep(5);
            };
        }
        else
        {
            deliver = en =>
            {
                _queue.Enqueue(new DataChunk(en.IsTx, en.Data, DateTime.Now) { Replayed = true, Source = en.Source });
                while (_queue.Count > 4000 && !cts.IsCancellationRequested) Thread.Sleep(5);
            };
        }

        rp.RunAsync(deliver, null, cts.Token).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            bool cancelled = t.IsCanceled || t.Exception?.InnerException is OperationCanceledException;
            string msg = t.IsFaulted && !cancelled ? "回放出错：" + t.Exception!.InnerException?.Message
                : _replayError != null ? "回放中止：" + _replayError
                : cancelled ? $"回放已停止（{rp.Position}/{_replayEntries?.Count}）" : "回放完成";
            _replayer = null;
            _replayCts = null;
            cts.Dispose();
            ReplayBtn.IsEnabled = true;
            ReplayPauseBtn.IsEnabled = ReplayStopBtn.IsEnabled = false;
            ReplayPauseBtn.Content = "暂停";
            ReplayTargetCombo.IsEnabled = ReplayContentCombo.IsEnabled = true;
            ReplayProgress.Value = cancelled || _replayError != null ? ReplayProgress.Value : 100;
            ReplayStatusText.Text = msg;
            SetStatus(msg, _replayError != null || (t.IsFaulted && !cancelled));
        }), TaskScheduler.Default);
    }

    private void ReplayPause_Click(object sender, RoutedEventArgs e)
    {
        var rp = _replayer;
        if (rp == null) return;
        if (rp.IsPaused)
        {
            rp.Resume();
            ReplayPauseBtn.Content = "暂停";
        }
        else
        {
            rp.Pause();
            ReplayPauseBtn.Content = "继续";
        }
    }

    private void ReplayStop_Click(object sender, RoutedEventArgs e)
    {
        try { _replayCts?.Cancel(); } catch { /* ignore */ }
        _replayer?.Resume();
    }

    private void ReplaySpeed_Changed(object sender, SelectionChangedEventArgs e)
    {
        var rp = _replayer;
        if (rp != null && ReplaySpeedCombo.SelectedItem is Opt<double> o) rp.Speed = o.Value;
    }

    private void UpdateReplayUi()
    {
        var rp = _replayer;
        var list = _replayEntries;
        if (rp == null || list == null || list.Count == 0) return;
        ReplayProgress.Value = rp.Position * 100.0 / list.Count;
        ReplayStatusText.Text = (rp.IsPaused ? "已暂停 " : "回放中 ") + $"{rp.Position}/{list.Count}";
    }

    // ======================================================================
    // 波特率识别
    // ======================================================================

    private async void BaudDetect_Click(object sender, RoutedEventArgs e)
    {
        if (PortCombo.SelectedItem is not PortItem item)
        {
            SetStatus("请先选择要识别的串口", true);
            return;
        }
        if (_conn is SerialSession ss && string.Equals(ss.PortName, item.Name, StringComparison.OrdinalIgnoreCase))
        {
            var r = MessageBox.Show(this, $"识别波特率需要独占 {item.Name}，是否先关闭当前串口？", "波特率识别",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;
            _reconnectPort = null;
            await CloseAsync($"{item.Name} 已关闭，开始识别波特率");
        }
        var dlg = new BaudDetectWindow(item.Name,
            (DataBitsCombo.SelectedItem as Opt<int>)?.Value ?? 8,
            (ParityCombo.SelectedItem as Opt<System.IO.Ports.Parity>)?.Value ?? System.IO.Ports.Parity.None,
            (StopBitsCombo.SelectedItem as Opt<System.IO.Ports.StopBits>)?.Value ?? System.IO.Ports.StopBits.One,
            _settings, CurrentEncoding())
        { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedBaud is not int baud) return;
        BaudCombo.Text = baud.ToString(CultureInfo.InvariantCulture);
        _settings.BaudRate = baud;
        SetStatus($"已应用波特率 {baud}");
        if (dlg.OpenAfterApply && _conn == null && CurrentConnKind() == ConnectionKind.Serial)
            await OpenAsync(item.Name, true);
    }
}
