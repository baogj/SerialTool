using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SerialTool.Features;

namespace SerialTool;

public partial class UpdateWindow : Window
{
    private readonly AppSettings _settings;
    private readonly string _current;
    private ReleaseInfo? _release;
    private ReleaseAsset? _asset;
    private CancellationTokenSource? _cts;
    private bool _downloading;

    public UpdateWindow(AppSettings settings, string currentVersion, ReleaseInfo? prefetched = null)
    {
        InitializeComponent();
        _settings = settings;
        _current = currentVersion;
        AutoChk.IsChecked = settings.CheckUpdateOnStartup;
        Loaded += async (_, _) =>
        {
            if (prefetched != null) Show(prefetched);
            else await CheckAsync();
        };
        Closing += (_, e) =>
        {
            if (_downloading && MessageBox.Show(this, "正在下载更新，确定取消并关闭？", "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _cts?.Cancel();
        };
    }

    private string Build => UpdateChecker.IsLiteBuild ? "lite 版（框架依赖，需要 .NET 6 桌面运行时）" : "完整版（自包含）";

    private async Task CheckAsync()
    {
        HeadText.Text = "正在检查更新…";
        StatusText.Text = "";
        VersionText.Text = $"当前版本 v{_current} · {Build}";
        AssetText.Text = "";
        NotesBox.Text = "";
        DownloadBtn.IsEnabled = false;
        SkipBtn.IsEnabled = false;
        RetryBtn.IsEnabled = false;
        try
        {
            using var client = UpdateChecker.CreateClient(TimeSpan.FromSeconds(15));
            var r = await UpdateChecker.FetchLatestAsync(client);
            _settings.LastUpdateCheck = DateTime.Now;
            Show(r);
        }
        catch (Exception ex)
        {
            HeadText.Text = "无法检查更新";
            StatusText.Text = (ex is HttpRequestException or TaskCanceledException
                ? "网络不可用或 GitHub 无法访问（离线时可稍后再试，或点「打开发布页」手动下载）。\n"
                : "") + "详情：" + ex.Message;
        }
        finally { RetryBtn.IsEnabled = true; }
    }

    private void Show(ReleaseInfo r)
    {
        _release = r;
        _asset = UpdateChecker.PickAsset(r, UpdateChecker.IsLiteBuild);
        bool newer = UpdateChecker.IsNewer(r.TagName, _current);
        HeadText.Text = newer ? $"发现新版本 {r.TagName}" : $"已是最新版本（v{_current}）";
        VersionText.Text = $"当前版本 v{_current} · {Build}  ·  最新版本 {r.TagName}" + (r.PublishedAt is DateTime p ? $"（发布于 {p.ToLocalTime():yyyy-MM-dd HH:mm}）" : "");
        AssetText.Text = _asset != null
            ? $"将下载：{_asset.Name}（{_asset.Size / 1024.0 / 1024.0:F1} MB），失败时自动改用镜像 ghproxy.net"
            : "发布中没有找到与当前版本匹配的 exe，请打开发布页手动下载。";
        NotesBox.Text = string.IsNullOrWhiteSpace(r.Body) ? "（无更新说明）" : r.Body.Replace("\r\n", "\n").Replace("\n", "\r\n");
        DownloadBtn.IsEnabled = newer && _asset != null && CanSelfUpdate(out _);
        SkipBtn.IsEnabled = newer;
        if (newer && !CanSelfUpdate(out var why)) StatusText.Text = why;
        if (newer && string.Equals(_settings.SkippedUpdateVersion, r.TagName, StringComparison.OrdinalIgnoreCase))
            StatusText.Text = "（此版本之前被设为跳过，启动时不再提示）";
    }

    private static bool CanSelfUpdate(out string reason)
    {
        reason = "";
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            reason = "当前不是以独立 exe 方式运行，无法自动替换，请手动下载。";
            return false;
        }
        return true;
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_release == null || _asset == null || _downloading) return;
        string exe = Environment.ProcessPath!;
        string dir = Path.GetDirectoryName(exe) ?? ".";
        string dest = Path.Combine(dir, Path.GetFileName(exe) + ".new");
        _downloading = true;
        _cts = new CancellationTokenSource();
        DownloadBtn.IsEnabled = false;
        RetryBtn.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        StatusText.Text = "正在连接…";
        try
        {
            using var client = UpdateChecker.CreateClient(TimeSpan.FromMinutes(10));
            var progress = new Progress<(long done, long total, string url)>(p =>
            {
                double pct = p.total > 0 ? p.done * 100.0 / p.total : 0;
                Progress.Value = pct;
                string src = p.url.StartsWith(UpdateChecker.MirrorPrefix) ? "镜像 ghproxy.net" : "GitHub";
                StatusText.Text = $"正在从 {src} 下载：{p.done / 1024.0 / 1024.0:F1} / {p.total / 1024.0 / 1024.0:F1} MB（{pct:F0}%）";
            });
            string used = await UpdateChecker.DownloadAsync(client, UpdateChecker.CandidateUrls(_asset.DownloadUrl), _asset.Size, dest, progress, _cts.Token);
            _downloading = false;
            Progress.Value = 100;
            StatusText.Text = $"下载完成并已校验大小（{_asset.Size} 字节）" + (used.StartsWith(UpdateChecker.MirrorPrefix) ? "，来源：镜像" : "") + "。";
            if (MessageBox.Show(this, $"{_release.TagName} 已下载完成。\n\n现在重启 SerialTool 完成更新吗？\n（会先关闭当前连接并保存设置）", "检查更新",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                StatusText.Text += " 新版本已保存为 " + Path.GetFileName(dest) + "，下次点「下载并更新」会重新下载。";
                DownloadBtn.IsEnabled = true;
                return;
            }
            UpdateChecker.ReplaceExecutable(exe, dest);
            App.RestartExe = exe;
            Close();
            Application.Current.MainWindow?.Close();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消下载。";
            DownloadBtn.IsEnabled = true;
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusText.Text = "没有写入程序目录的权限（例如放在 Program Files 下），请手动下载替换：" + ex.Message;
            DownloadBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "更新失败：" + ex.Message;
            DownloadBtn.IsEnabled = true;
        }
        finally
        {
            _downloading = false;
            RetryBtn.IsEnabled = true;
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private void Page_Click(object sender, RoutedEventArgs e)
    {
        string url = !string.IsNullOrEmpty(_release?.HtmlUrl) ? _release!.HtmlUrl : UpdateChecker.ReleasesPageUrl;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "无法打开浏览器：" + ex.Message; }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_release == null) return;
        _settings.SkippedUpdateVersion = _release.TagName;
        StatusText.Text = $"已跳过 {_release.TagName}：启动时的自动检查不再提示此版本（仍可手动更新）。";
    }

    private void AutoChk_Click(object sender, RoutedEventArgs e) => _settings.CheckUpdateOnStartup = AutoChk.IsChecked == true;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
