using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SerialTool;

/// <summary>Win32 小工具：窗口不透明度（分层窗口）、不抢焦点、提示音。</summary>
internal static class Win32Ui
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int LWA_ALPHA = 0x2;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool MessageBeep(uint uType);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    /// <summary>设置窗口不透明度（0.3~1.0）。AllowsTransparency 不能在运行时切换，因此直接使用 WS_EX_LAYERED。</summary>
    public static bool SetOpacity(Window w, double opacity)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return false;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            if (opacity >= 0.999)
            {
                if ((ex & WS_EX_LAYERED) != 0) SetWindowLong(hwnd, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
                return true;
            }
            if ((ex & WS_EX_LAYERED) == 0) SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);
            byte a = (byte)Math.Round(Math.Clamp(opacity, 0.3, 1.0) * 255);
            return SetLayeredWindowAttributes(hwnd, 0, a, LWA_ALPHA);
        }
        catch { return false; }
    }

    /// <summary>窗口显示时不激活、不出现在 Alt+Tab 中（用于通知弹窗）。</summary>
    public static void MakeNoActivate(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }
        catch { }
    }

    /// <summary>播放提示音：系统声音名称或 .wav 路径（异步，不阻塞）。</summary>
    public static void PlaySoundSpec(string? spec)
    {
        try
        {
            spec = (spec ?? "").Trim();
            if (spec.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(spec))
            {
                const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_FILENAME = 0x20000;
                PlaySound(spec, IntPtr.Zero, SND_ASYNC | SND_NODEFAULT | SND_FILENAME);
                return;
            }
            uint type = spec.ToLowerInvariant() switch
            {
                "asterisk" => 0x40,
                "hand" => 0x10,
                "question" => 0x20,
                "beep" => 0xFFFFFFFF,
                _ => 0x30, // Exclamation
            };
            MessageBeep(type);
        }
        catch { }
    }
}

/// <summary>右下角通知弹窗：不抢焦点（WS_EX_NOACTIVATE + ShowActivated=false），几秒后自动消失，可叠放多条。</summary>
internal static class Toast
{
    private static readonly System.Collections.Generic.List<Window> Open = new();
    private const int MaxOpen = 5;

    public static void Show(string title, string message, int ms = 4000)
    {
        try
        {
            while (Open.Count >= MaxOpen) { var old = Open[0]; Open.RemoveAt(0); old.Close(); }
            var res = Application.Current.Resources;
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                Topmost = true,
                Width = 320,
                SizeToContent = SizeToContent.Height,
                Background = res["PopupBgBrush"] as Brush ?? Brushes.White,
                BorderBrush = res["AccentBrush"] as Brush ?? Brushes.SteelBlue,
                BorderThickness = new Thickness(1, 1, 1, 3),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
            };
            var panel = new StackPanel { Margin = new Thickness(12, 8, 12, 10) };
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = res["TitleBrush"] as Brush ?? Brushes.Black, FontSize = 13 });
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = res["TextBrush"] as Brush ?? Brushes.Black, Margin = new Thickness(0, 4, 0, 0), FontSize = 12.5 });
            w.Content = panel;
            w.SourceInitialized += (_, _) => Win32Ui.MakeNoActivate(w);
            w.MouseLeftButtonUp += (_, _) => w.Close();
            w.Closed += (_, _) => { Open.Remove(w); Relayout(); };
            w.SizeChanged += (_, _) => Relayout();
            w.Left = -10000; w.Top = -10000;
            w.Show();
            Open.Add(w);
            Relayout();
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, _) => { t.Stop(); w.Close(); };
            t.Start();
        }
        catch (Exception ex) { App.LogCrash(ex); }
    }

    private static void Relayout()
    {
        var wa = SystemParameters.WorkArea;
        double bottom = wa.Bottom - 10;
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var w = Open[i];
            w.Left = wa.Right - w.Width - 10;
            w.Top = bottom - w.ActualHeight;
            bottom = w.Top - 8;
        }
    }
}

/// <summary>系统托盘气泡（Shell_NotifyIcon，不依赖 WinForms）。首次使用时添加托盘图标，程序退出时移除。</summary>
internal sealed class TrayNotifier : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    private const int NIIF_INFO = 0x1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr ExtractIcon(IntPtr hInst, string file, int index);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    private readonly IntPtr _hwnd;
    private IntPtr _icon;
    private bool _added, _ownsIcon;

    public TrayNotifier(Window owner) => _hwnd = new WindowInteropHelper(owner).Handle;

    private NOTIFYICONDATA Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 0x5354,
        uFlags = flags,
        hIcon = _icon,
        szTip = "SerialTool 串口调试助手",
        szInfo = "",
        szInfoTitle = "",
    };

    public void Show(string title, string text)
    {
        try
        {
            if (_hwnd == IntPtr.Zero) return;
            if (!_added)
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) { _icon = ExtractIcon(IntPtr.Zero, exe, 0); _ownsIcon = _icon != IntPtr.Zero && _icon != (IntPtr)1; }
                if (!_ownsIcon) _icon = LoadIcon(IntPtr.Zero, (IntPtr)32516); // IDI_INFORMATION
                var add = Data(NIF_ICON | NIF_TIP);
                _added = Shell_NotifyIcon(NIM_ADD, ref add);
                if (!_added) return;
            }
            var d = Data(NIF_INFO | NIF_ICON | NIF_TIP);
            d.szInfo = text.Length > 250 ? text.Substring(0, 250) : text;
            d.szInfoTitle = title.Length > 60 ? title.Substring(0, 60) : title;
            d.dwInfoFlags = NIIF_INFO;
            Shell_NotifyIcon(NIM_MODIFY, ref d);
        }
        catch (Exception ex) { App.LogCrash(ex); }
    }

    public void Dispose()
    {
        try
        {
            if (_added) { var d = Data(0); Shell_NotifyIcon(NIM_DELETE, ref d); _added = false; }
            if (_ownsIcon && _icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
        }
        catch { }
    }
}
