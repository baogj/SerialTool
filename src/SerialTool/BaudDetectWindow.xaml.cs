using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SerialTool.Features;

namespace SerialTool;

public partial class BaudDetectWindow : Window
{
    private readonly string _port;
    private readonly int _dataBits;
    private readonly Parity _parity;
    private readonly StopBits _stopBits;
    private readonly AppSettings _settings;
    private readonly Encoding _encoding;
    private readonly ObservableCollection<BaudScore> _results = new();
    private readonly List<CheckBox> _rateChecks = new();
    private CancellationTokenSource? _cts;

    public BaudDetectWindow(string port, int dataBits, Parity parity, StopBits stopBits, AppSettings settings, Encoding encoding)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        _port = port;
        _dataBits = dataBits;
        _parity = parity;
        _stopBits = stopBits;
        _settings = settings;
        _encoding = encoding;
        string p = parity switch { Parity.None => "N", Parity.Odd => "O", Parity.Even => "E", Parity.Mark => "M", _ => "S" };
        string s = stopBits switch { StopBits.Two => "2", StopBits.OnePointFive => "1.5", _ => "1" };
        HeaderText.Text = $"串口：{port}    格式：{dataBits}{p}{s}（取自主窗口设置）";
        foreach (int r in BaudScorer.StandardRates)
        {
            var cb = new CheckBox { Content = r.ToString(CultureInfo.InvariantCulture), IsChecked = true, Margin = new Thickness(0, 2, 12, 2), Tag = r };
            _rateChecks.Add(cb);
            RatesPanel.Children.Add(cb);
        }
        ProbeChk.IsChecked = settings.BaudProbe;
        ProbeBox.Text = settings.BaudProbeText;
        ListenBox.Text = settings.BaudListenMs.ToString(CultureInfo.InvariantCulture);
        ResultGrid.ItemsSource = _results;
        Closing += OnClosing;
    }

    public int? SelectedBaud { get; private set; }
    public bool OpenAfterApply { get; private set; }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _cts?.Cancel();
        _settings.BaudProbe = ProbeChk.IsChecked == true;
        _settings.BaudProbeText = ProbeBox.Text;
        if (int.TryParse(ListenBox.Text.Trim(), out int ms) && ms >= 100 && ms <= 10000) _settings.BaudListenMs = ms;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var rates = _rateChecks.Where(c => c.IsChecked == true).Select(c => (int)c.Tag).ToList();
        if (rates.Count == 0)
        {
            BestText.Text = "请至少勾选一个波特率";
            return;
        }
        if (!int.TryParse(ListenBox.Text.Trim(), out int listenMs) || listenMs < 100 || listenMs > 10000)
        {
            BestText.Text = "监听时间需为 100 ~ 10000 ms";
            return;
        }
        byte[]? probe = ProbeChk.IsChecked == true && ProbeBox.Text.Length > 0 ? EscapeUtil.Encode(ProbeBox.Text, _encoding) : null;

        _results.Clear();
        SelectedBaud = null;
        ApplyBtn.IsEnabled = ApplyOpenBtn.IsEnabled = false;
        StartBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        Progress.Value = 0;
        BestText.Text = "识别中…";
        var cts = new CancellationTokenSource();
        _cts = cts;
        int done = 0;
        try
        {
            var results = await BaudDetector.RunAsync(
                (baud, ct) => Task.Run(() => Sample(baud, probe, listenMs, ct), ct),
                rates,
                r => Dispatcher.BeginInvoke(() =>
                {
                    _results.Add(r);
                    done++;
                    Progress.Value = done * 100.0 / rates.Count;
                }),
                cts.Token);
            await Dispatcher.InvokeAsync(() => { }); // 等结果行加完
            ShowBest(results);
        }
        catch (OperationCanceledException)
        {
            BestText.Text = "已停止";
            ShowBest(_results.ToList(), partial: true);
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            StartBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
        }
    }

    private void ShowBest(List<BaudScore> results, bool partial = false)
    {
        var best = BaudScorer.Best(results);
        if (best == null)
        {
            if (!partial)
                BestText.Text = results.All(r => r.Bytes == 0)
                    ? "所有波特率都没有收到数据：请确认设备在发送，或勾选“发送探测”。"
                    : "没有可信的结果（得分均低于 40）。可尝试延长监听时间或检查数据位/校验位。";
            return;
        }
        SelectedBaud = best.Baud;
        ResultGrid.SelectedItem = best;
        ResultGrid.ScrollIntoView(best);
        BestText.Text = $"推荐：{best.Baud}（得分 {best.Score}，{best.Note}）" + (partial ? "（未完成全部波特率）" : "");
        ApplyBtn.IsEnabled = ApplyOpenBtn.IsEnabled = true;
    }

    /// <summary>以指定波特率打开串口，可选发送探测，然后监听一段时间。</summary>
    private byte[] Sample(int baud, byte[]? probe, int listenMs, CancellationToken ct)
    {
        using var sp = new SerialPort(_port, baud, _parity, _dataBits, _stopBits)
        {
            ReadTimeout = 50,
            WriteTimeout = 500,
            Handshake = Handshake.None,
            DtrEnable = _settings.Dtr,
            RtsEnable = _settings.Rts,
            ParityReplace = 0,
        };
        sp.Open();
        try
        {
            Thread.Sleep(30);
            sp.DiscardInBuffer();
            if (probe != null) sp.Write(probe, 0, probe.Length);
            var buf = new List<byte>();
            var sw = Stopwatch.StartNew();
            var tmp = new byte[4096];
            while (sw.ElapsedMilliseconds < listenMs && buf.Count < 4096)
            {
                ct.ThrowIfCancellationRequested();
                int n = sp.BytesToRead;
                if (n > 0)
                {
                    int r = sp.Read(tmp, 0, Math.Min(n, tmp.Length));
                    for (int i = 0; i < r; i++) buf.Add(tmp[i]);
                }
                else Thread.Sleep(10);
            }
            return buf.ToArray();
        }
        finally
        {
            try { sp.Close(); } catch { /* ignore */ }
            Thread.Sleep(40); // 部分 USB 串口驱动关闭后需要片刻才能再次打开
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void ResultGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ResultGrid.SelectedItem is BaudScore s && _cts == null)
        {
            SelectedBaud = s.Baud;
            BestText.Text = $"已选择：{s.Baud}（得分 {s.Score}）";
            ApplyBtn.IsEnabled = ApplyOpenBtn.IsEnabled = true;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (ResultGrid.SelectedItem is BaudScore s) SelectedBaud = s.Baud;
        if (SelectedBaud == null) return;
        DialogResult = true;
    }

    private void ApplyOpen_Click(object sender, RoutedEventArgs e)
    {
        OpenAfterApply = true;
        Apply_Click(sender, e);
    }
}
