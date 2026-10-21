using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace SerialTool;

public partial class App : Application
{
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

        var window = new MainWindow();
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
