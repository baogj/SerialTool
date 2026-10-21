using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SerialTool.Features;

namespace SerialTool;

public partial class MainWindow
{
    // ======================================================================
    // ScriptEngine.IHost
    // ======================================================================

    bool ScriptEngine.IHost.IsPortOpen => _session != null;
    string? ScriptEngine.IHost.PortName => _session?.PortName;
    Encoding ScriptEngine.IHost.Encoding => CurrentEncoding();

    bool ScriptEngine.IHost.SendText(string text, bool escape)
    {
        bool ok = false;
        Dispatcher.Invoke(() =>
        {
            // 临时尊重 escape 选项
            bool prev = EscapeChk.IsChecked == true;
            EscapeChk.IsChecked = escape;
            ok = SendNow(text, false, false);
            EscapeChk.IsChecked = prev;
        });
        return ok;
    }

    bool ScriptEngine.IHost.SendHex(string hex, out string? error)
    {
        string? err = null;
        bool ok = false;
        Dispatcher.Invoke(() =>
        {
            if (!HexUtil.TryParse(hex, out byte[] data, out err) || data.Length == 0)
            {
                ok = false;
                return;
            }
            var s = _session;
            if (s == null)
            {
                err = "串口未打开";
                ok = false;
                return;
            }
            ok = s.Send(data);
            if (!ok) err = "发送队列已满";
        });
        error = err;
        return ok;
    }

    void ScriptEngine.IHost.Log(string message)
    {
        Dispatcher.BeginInvoke(() => SetStatus("[脚本] " + message));
    }

    // ======================================================================
    // 协议模式
    // ======================================================================

    private void ProtocolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        FlushQueue();
        ApplyProtocolModeUi();
        _modbus.Reset();
        _atParser = new AtLineParser(CurrentEncoding());
        string mode = ProtocolCombo.SelectedItem as string ?? "普通";
        ProtocolHint.Text = mode == "普通" ? "" : $"模式：{mode}";
        SetStatus("协议模式：" + mode);
    }

    private void ApplyProtocolModeUi()
    {
        string mode = ProtocolCombo.SelectedItem as string ?? "普通";
        bool proto = mode != "普通";
        RxBox.Visibility = proto ? Visibility.Collapsed : Visibility.Visible;
        ProtocolList.Visibility = proto ? Visibility.Visible : Visibility.Collapsed;
        FrameText.Visibility = mode == "Modbus RTU" ? Visibility.Visible : Visibility.Collapsed;
        if (!proto) FrameText.Text = "";
    }

    private void AppendProtocolLine(ProtocolLine line)
    {
        _protocolLines.Add(line);
        while (_protocolLines.Count > MaxProtocolLines)
            _protocolLines.RemoveAt(0);
        // 也写日志（纯文本）
        WriteLog(line.Text + "\r\n");
    }

    // ======================================================================
    // 波形
    // ======================================================================

    private void WaveOption_Click(object sender, RoutedEventArgs e)
    {
        _waveBuf.ShowTx = WaveShowTxChk.IsChecked == true;
        _waveElement?.InvalidateVisual();
    }

    private void WavePause_Click(object sender, RoutedEventArgs e)
    {
        _waveBuf.Paused = WavePauseChk.IsChecked == true;
        _waveElement?.InvalidateVisual();
    }

    private void WaveWindow_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (WaveWindowCombo.SelectedItem is int n)
        {
            _waveBuf.WindowLength = n;
            _waveElement?.InvalidateVisual();
        }
    }

    private void WaveClear_Click(object sender, RoutedEventArgs e)
    {
        _waveBuf.Clear();
        _waveElement?.InvalidateVisual();
    }

    // ======================================================================
    // 脚本
    // ======================================================================

    private async void ScriptRun_Click(object sender, RoutedEventArgs e)
    {
        if (_script == null) return;
        if (_script.IsRunning)
        {
            SetStatus("脚本已在运行", true);
            return;
        }
        ScriptOutBox.Clear();
        try
        {
            await _script.RunAsync(ScriptBox.Text);
        }
        catch (Exception ex)
        {
            ScriptOutBox.AppendText("启动失败：" + ex.Message + "\r\n");
        }
    }

    private void ScriptStop_Click(object sender, RoutedEventArgs e)
    {
        _script?.Stop();
    }

    private void ScriptSampleAt_Click(object sender, RoutedEventArgs e)
    {
        ScriptBox.Text = ScriptEngine.SampleAtPoll;
    }

    private void ScriptSampleModbus_Click(object sender, RoutedEventArgs e)
    {
        ScriptBox.Text = ScriptEngine.SampleModbusRead;
    }

    // ======================================================================
    // 吞吐率
    // ======================================================================

    private void UpdateThroughputUi()
    {
        _meter.Tick();
        RateText.Text = $"RX: {ThroughputMeter.FormatRate(_meter.RxRateBps)}  TX: {ThroughputMeter.FormatRate(_meter.TxRateBps)}";
        PeakText.Text = $"峰值 RX {ThroughputMeter.FormatRate(_meter.RxPeakBps)} / TX {ThroughputMeter.FormatRate(_meter.TxPeakBps)}";
        var el = _meter.SessionElapsed;
        SessionText.Text = $"会话 {el.Hours:00}:{el.Minutes:00}:{el.Seconds:00}";
        if ((ProtocolCombo.SelectedItem as string) == "Modbus RTU")
            FrameText.Text = $"帧 OK {_meter.FrameOk} / ERR {_meter.FrameErr}";
        else
            FrameText.Text = "";

        // 平均速率可放在 tooltip
        RateText.ToolTip = $"平均 RX {ThroughputMeter.FormatRate(_meter.RxAvgBps)} / TX {ThroughputMeter.FormatRate(_meter.TxAvgBps)}";
    }
}
