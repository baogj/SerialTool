using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace SerialTool;

public partial class App : Application
{
    /// <summary>v1.8：自动更新完成后，退出时启动的新 exe。</summary>
    internal static string? RestartExe;

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        if (RestartExe != null)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RestartExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(RestartExe) ?? "" }); }
            catch (Exception ex) { LogCrash(ex); }
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // GBK / GB2312 等代码页编码在 .NET Core 下需要显式注册
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);

        // 可选启动参数：
        //   --software-render  强制 WPF 软件渲染（远程桌面 / 虚拟机 / 显卡驱动异常导致窗口空白时使用）
        //   --no-activate      启动时不抢占焦点（后台启动，不打断正在使用的程序）
        bool HasArg(string name) => Array.Exists(e.Args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        if (HasArg("--software-render") || Environment.GetEnvironmentVariable("SERIALTOOL_SOFTWARE_RENDER") == "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        var window = new MainWindow();
        if (HasArg("--no-activate")) window.ShowActivated = false;
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        e.Handled = true;
        try
        {
            MessageBox.Show("程序发生未处理的错误（已记录，可继续使用）：\n\n" + e.Exception.Message,
                "SerialTool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // ignore
        }
    }

    internal static void LogCrash(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            string dir = Path.GetDirectoryName(SettingsStore.FilePath) ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {ex}\r\n\r\n", Encoding.UTF8);
        }
        catch
        {
            // ignore
        }
    }
}
