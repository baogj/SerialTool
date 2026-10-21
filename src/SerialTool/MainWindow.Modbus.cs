using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SerialTool.Features;

namespace SerialTool;

public sealed class ModbusRow
{
    public int Address { get; init; }
    public string AddressHex => "0x" + Address.ToString("X4");
    public string Raw { get; init; } = "";
    public string Value { get; init; } = "";
}

/// <summary>v1.3：Modbus 主站面板。</summary>
public partial class MainWindow
{
    private static readonly List<Opt<byte>> MbFunctionOptions =
        ModbusMaster.Functions.Select(f => new Opt<byte>(f.Name, f.Code)).ToList();
    private static readonly List<Opt<RegisterFormat>> MbFormatOptions =
        ModbusMaster.Formats.Select(f => new Opt<RegisterFormat>(f.Name, f.Format)).ToList();
    private static readonly List<Opt<WordOrder>> MbOrderOptions =
        Enum.GetValues<WordOrder>().Select(o => new Opt<WordOrder>(o.ToString(), o)).ToList();
    private static readonly List<Opt<ModbusFraming>> MbFramingOptions = new()
    {
        new("RTU（CRC16）", ModbusFraming.Rtu), new("Modbus TCP（MBAP 头）", ModbusFraming.Tcp),
    };

    private readonly ModbusTransactionWaiter _mbWaiter = new();
    private readonly ObservableCollection<ModbusRow> _mbRows = new();
    private DispatcherTimer? _mbPollTimer;
    private bool _mbBusy;
    private ushort _mbTid;
    private long _mbOk, _mbTimeout, _mbCrc, _mbExc;
    private ModbusRequest? _mbLastReq;
    private ModbusResult? _mbLastResult;

    private void InitModbusControls()
    {
        var m = _settings.Modbus;
        MbFuncCombo.ItemsSource = MbFunctionOptions;
        MbFuncCombo.SelectedItem = MbFunctionOptions.FirstOrDefault(o => o.Value == m.Function) ?? MbFunctionOptions[2];
        MbFormatCombo.ItemsSource = MbFormatOptions;
        MbFormatCombo.SelectedItem = MbFormatOptions.FirstOrDefault(o => o.Value.ToString() == m.Format) ?? MbFormatOptions[0];
        MbOrderCombo.ItemsSource = MbOrderOptions;
        MbOrderCombo.SelectedItem = MbOrderOptions.FirstOrDefault(o => o.Value.ToString() == m.Order) ?? MbOrderOptions[0];
        MbFramingCombo.ItemsSource = MbFramingOptions;
        MbFramingCombo.SelectedItem = m.TcpFraming ? MbFramingOptions[1] : MbFramingOptions[0];
        MbSlaveBox.Text = m.Slave.ToString();
        MbStartBox.Text = m.Start.ToString();
        MbQtyBox.Text = m.Quantity.ToString();
        MbValuesBox.Text = m.Values;
        MbTimeoutBox.Text = m.TimeoutMs.ToString();
        MbPollBox.Text = m.PollMs.ToString();
        MbGrid.ItemsSource = _mbRows;
        UpdateMbFunctionUi();
    }

    private void InitModbusRuntime()
    {
        _mbPollTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _mbPollTimer.Tick += async (_, _) =>
        {
            if (!_mbBusy) await MbExecuteAsync(true);
        };
    }

    private void CollectModbusSettings()
    {
        var m = _settings.Modbus;
        if (TryParseInt(MbSlaveBox.Text, out int sl)) m.Slave = sl;
        if (MbFuncCombo.SelectedItem is Opt<byte> f) m.Function = f.Value;
        if (TryParseInt(MbStartBox.Text, out int st)) m.Start = st;
        if (TryParseInt(MbQtyBox.Text, out int q)) m.Quantity = q;
        m.Values = MbValuesBox.Text;
        if (TryParseInt(MbTimeoutBox.Text, out int to) && to >= 10) m.TimeoutMs = to;
        if (TryParseInt(MbPollBox.Text, out int pm) && pm >= 50) m.PollMs = pm;
        m.Format = CurrentMbFormat.ToString();
        m.Order = CurrentMbOrder.ToString();
        m.TcpFraming = CurrentMbFraming == ModbusFraming.Tcp;
    }

    private static bool TryParseInt(string text, out int v)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    }

    private RegisterFormat CurrentMbFormat => (MbFormatCombo.SelectedItem as Opt<RegisterFormat>)?.Value ?? RegisterFormat.UInt16;
    private WordOrder CurrentMbOrder => (MbOrderCombo.SelectedItem as Opt<WordOrder>)?.Value ?? WordOrder.ABCD;
    private ModbusFraming CurrentMbFraming => (MbFramingCombo.SelectedItem as Opt<ModbusFraming>)?.Value ?? ModbusFraming.Rtu;
    private byte CurrentMbFunction => (MbFuncCombo.SelectedItem as Opt<byte>)?.Value ?? 3;

    private void MbFunc_Changed(object sender, SelectionChangedEventArgs e) => UpdateMbFunctionUi();

    private void UpdateMbFunctionUi()
    {
        if (MbExecBtn == null) return;
        byte fc = CurrentMbFunction;
        bool read = fc is 1 or 2 or 3 or 4;
        MbExecBtn.Content = read ? "读取" : "写入";
        MbQtyBox.IsEnabled = read;
        MbQtyLabel.Opacity = read ? 1 : 0.5;
        MbValuesBox.IsEnabled = !read;
        MbValuesLabel.Opacity = read ? 0.5 : 1;
        bool bits = fc is 1 or 2 or 5 or 15;
        MbFormatCombo.IsEnabled = !bits;
        MbOrderCombo.IsEnabled = !bits && ModbusMaster.Is32Bit(CurrentMbFormat);
    }

    private void MbOption_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (MbOrderCombo == null) return;
        UpdateMbFunctionUi();
        if (_mbLastReq != null && _mbLastResult != null) ShowMbRows(_mbLastReq, _mbLastResult);
    }

    private async void MbExec_Click(object sender, RoutedEventArgs e)
    {
        if (_mbBusy)
        {
            SetStatus("上一个 Modbus 请求尚未完成", true);
            return;
        }
        await MbExecuteAsync(false);
    }

    private void MbPoll_Click(object sender, RoutedEventArgs e)
    {
        if (MbPollChk.IsChecked != true)
        {
            StopModbusPoll();
            SetStatus("Modbus 轮询已停止");
            return;
        }
        if (!TryParseInt(MbPollBox.Text, out int ms) || ms < 50 || ms > 3_600_000)
        {
            MbPollChk.IsChecked = false;
            SetStatus("轮询周期需为 50 ~ 3600000 ms", true);
            return;
        }
        if (_conn == null)
        {
            MbPollChk.IsChecked = false;
            SetStatus("请先打开主连接", true);
            return;
        }
        _mbPollTimer!.Interval = TimeSpan.FromMilliseconds(ms);
        _mbPollTimer.Start();
        SetStatus($"Modbus 轮询已启动，周期 {ms} ms");
    }

    private void StopModbusPoll()
    {
        _mbPollTimer?.Stop();
        if (MbPollChk != null) MbPollChk.IsChecked = false;
    }

    private void MbResetStats_Click(object sender, RoutedEventArgs e)
    {
        _mbOk = _mbTimeout = _mbCrc = _mbExc = 0;
        UpdateMbStats();
    }

    private void UpdateMbStats() =>
        MbStatsText.Text = $"成功 {_mbOk}  超时 {_mbTimeout}  CRC错 {_mbCrc}  异常 {_mbExc}";

    private void SetMbBanner(string text, string tone)
    {
        MbBannerText.Text = text;
        string bg = tone switch { "ok" => "ToneOk", "err" => "ToneErr", "warn" => "ToneUrc", _ => "" };
        if (bg.Length == 0)
        {
            MbBanner.SetResourceReference(Border.BackgroundProperty, "RxHeaderBgBrush");
            MbBannerText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        }
        else
        {
            MbBanner.Background = ThemeManager.Get(bg);
            MbBannerText.Foreground = System.Windows.Media.Brushes.White;
            if (tone == "warn") MbBannerText.Foreground = System.Windows.Media.Brushes.Black;
        }
    }

    private async Task MbExecuteAsync(bool fromPoll)
    {
        var conn = _conn;
        if (conn == null)
        {
            if (fromPoll) StopModbusPoll();
            SetMbBanner("主连接未打开：请先在左侧打开串口或建立网络连接。", "warn");
            SetStatus(NotOpenMessage, true);
            return;
        }
        if (!TryParseInt(MbSlaveBox.Text, out int slave)) { MbInputError("从站地址无效"); return; }
        if (!TryParseInt(MbStartBox.Text, out int start)) { MbInputError("起始地址无效"); return; }
        if (!TryParseInt(MbQtyBox.Text, out int qty)) qty = 1;
        if (!TryParseInt(MbTimeoutBox.Text, out int timeout) || timeout < 10 || timeout > 60000) { MbInputError("超时需为 10~60000 ms"); return; }

        _mbTid++;
        if (!ModbusMaster.TryCreate(slave, CurrentMbFunction, start, qty, MbValuesBox.Text, CurrentMbFormat, CurrentMbOrder,
                CurrentMbFraming, _mbTid, out var req, out string? err))
        {
            MbInputError(err ?? "参数无效");
            return;
        }

        byte[] frame = ModbusMaster.BuildFrame(req);
        _mbBusy = true;
        MbExecBtn.IsEnabled = false;
        try
        {
            bool broadcast = req.Slave == 0 && req.Framing == ModbusFraming.Rtu;
            var task = broadcast ? null : _mbWaiter.Begin(req);
            var sw = Stopwatch.StartNew();
            if (!conn.Send(frame))
            {
                _mbWaiter.Cancel();
                SetMbBanner("发送失败：发送队列已满或连接正在关闭。", "err");
                return;
            }
            MbFrameText.Text = "请求：" + Checksum.ToHex(frame);
            if (broadcast)
            {
                SetMbBanner("广播写入已发送（地址 0，从站不应答）。", "ok");
                _mbOk++;
                return;
            }
            var res = await _mbWaiter.WaitAsync(task!, timeout);
            res.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            HandleMbResult(req, frame, res);
        }
        catch (Exception ex)
        {
            SetMbBanner("执行出错：" + ex.Message, "err");
        }
        finally
        {
            _mbBusy = false;
            MbExecBtn.IsEnabled = true;
            UpdateMbStats();
        }
    }

    private void MbInputError(string msg)
    {
        SetMbBanner(msg, "warn");
        SetStatus("Modbus：" + msg, true);
        StopModbusPoll();
    }

    private void HandleMbResult(ModbusRequest req, byte[] frame, ModbusResult res)
    {
        string reqHex = "请求：" + Checksum.ToHex(frame);
        string rspHex = res.Raw.Length > 0 ? "\n应答：" + Checksum.ToHex(res.Raw.Take(300).ToArray()) + (res.Raw.Length > 300 ? " …" : "") : "";
        MbFrameText.Text = reqHex + rspHex;
        string fname = MbFunctionOptions.FirstOrDefault(o => o.Value == req.Function)?.Text ?? req.Function.ToString("X2");
        switch (res.Status)
        {
            case ModbusResultStatus.Ok:
                _mbOk++;
                _mbLastReq = req;
                _mbLastResult = res;
                ShowMbRows(req, res);
                SetMbBanner(req.IsRead
                    ? $"✓ {fname} 成功：{(req.IsBitFunction ? res.Coils.Length : res.Registers.Length)} 个{(req.IsBitFunction ? "位" : "寄存器")}，用时 {res.ElapsedMs:0} ms"
                    : $"✓ {fname} 成功：地址 {res.EchoAddress}，{(req.Function is 5 or 6 ? "值" : "数量")} {(req.Function == 5 ? (res.EchoValue == 0xFF00 ? "ON" : "OFF") : res.EchoValue.ToString())}，用时 {res.ElapsedMs:0} ms", "ok");
                break;
            case ModbusResultStatus.Exception:
                _mbExc++;
                SetMbBanner($"✗ 从站返回异常响应：功能码 0x{req.Function | 0x80:X2}，异常码 {res.ExceptionCode:X2} — {ModbusMaster.ExceptionName(res.ExceptionCode)}", "err");
                SetStatus($"Modbus 异常 {res.ExceptionCode:X2}：{ModbusMaster.ExceptionName(res.ExceptionCode)}", true);
                break;
            case ModbusResultStatus.CrcError:
                _mbCrc++;
                SetMbBanner("✗ 收到应答但 CRC 校验错误（检查波特率 / 校验位 / 干扰）", "err");
                break;
            default:
                _mbTimeout++;
                SetMbBanner(res.Raw.Length > 0
                    ? $"✗ 超时：收到 {res.Raw.Length} 字节但不是匹配的应答（检查从站地址 / 功能码 / 帧格式）"
                    : "✗ 超时：没有收到应答（检查接线、从站地址、波特率，或帧格式 RTU / TCP 是否匹配）", "err");
                break;
        }
    }

    private void ShowMbRows(ModbusRequest req, ModbusResult res)
    {
        _mbRows.Clear();
        if (!req.IsRead) return;
        if (req.IsBitFunction)
        {
            for (int i = 0; i < res.Coils.Length; i++)
                _mbRows.Add(new ModbusRow { Address = req.Start + i, Raw = res.Coils[i] ? "1" : "0", Value = res.Coils[i] ? "ON" : "OFF" });
            return;
        }
        var fmt = CurrentMbFormat;
        var order = CurrentMbOrder;
        for (int i = 0; i < res.Registers.Length; i++)
            _mbRows.Add(new ModbusRow
            {
                Address = req.Start + i,
                Raw = "0x" + res.Registers[i].ToString("X4"),
                Value = ModbusMaster.FormatRegister(res.Registers, i, fmt, order),
            });
    }
}
