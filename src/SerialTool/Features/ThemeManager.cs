using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace SerialTool.Features;

public enum ThemeMode { Light, Dark, System }

/// <summary>行着色类别，实际颜色随主题变化。</summary>
public enum LineTone { Plain, Tx, Cmd, Ok, Err, Urc, Info, Meta }

/// <summary>
/// 主题：把所有 *Brush 资源替换为当前配色（界面一律使用 DynamicResource），
/// 并同步窗口标题栏深色模式。“跟随系统”读取 AppsUseLightTheme 并监听系统切换。
/// </summary>
public static class ThemeManager
{
    private static readonly Dictionary<string, string> Light = new()
    {
        ["WindowBg"] = "#F3F5F8", ["CardBg"] = "#FFFFFF", ["CardBorder"] = "#E3E7ED",
        ["Text"] = "#1F2937", ["TextMuted"] = "#6B7280", ["Title"] = "#111827", ["FieldLabel"] = "#4B5563",
        ["InputBg"] = "#FFFFFF", ["InputFg"] = "#111827", ["InputBorder"] = "#D1D5DB",
        ["ButtonBg"] = "#F3F4F6", ["ButtonFg"] = "#111827", ["ButtonBorder"] = "#D1D5DB",
        ["Accent"] = "#2563EB", ["Danger"] = "#DC2626",
        ["RxBg"] = "#FFFFFF", ["RxFg"] = "#1F2937", ["RxHeaderBg"] = "#F1F5F9", ["RxHeaderFg"] = "#111827",
        ["RxSelection"] = "#BFDBFE",
        ["PanelButtonBg"] = "#E5E7EB", ["PanelButtonFg"] = "#111827", ["PanelButtonBorder"] = "#CBD5E1",
        ["StatusBarBg"] = "#E5E7EB", ["StatusFg"] = "#1F2937", ["StatusError"] = "#B91C1C",
        ["TabBg"] = "#E2E8F0", ["TabFg"] = "#334155", ["TabSelBg"] = "#2563EB", ["TabSelFg"] = "#FFFFFF",
        ["PopupBg"] = "#FFFFFF", ["HoverBg"] = "#EFF6FF", ["SelectionBg"] = "#DBEAFE", ["GridLine"] = "#E5E7EB",
        ["ScriptOutBg"] = "#F8FAFC", ["ScriptOutFg"] = "#1E3A8A", ["ScrollThumb"] = "#CBD5E1", ["Splitter"] = "#CBD5E1",
        ["TopBarBg"] = "#FFFFFF",
        ["StatRx"] = "#1D4ED8", ["StatTx"] = "#15803D", ["StatRate"] = "#B45309", ["StatPeak"] = "#7C3AED",
        ["StatMuted"] = "#6B7280", ["StatFrame"] = "#BE185D",
        // 行颜色 / 波形
        ["TonePlain"] = "#1F2937", ["ToneTx"] = "#15803D", ["ToneCmd"] = "#0369A1", ["ToneOk"] = "#15803D",
        ["ToneErr"] = "#DC2626", ["ToneUrc"] = "#A16207", ["ToneInfo"] = "#7C3AED", ["ToneMeta"] = "#1D4ED8",
        ["WaveBg"] = "#FFFFFF", ["WaveGrid"] = "#E5E7EB", ["WaveAxis"] = "#9CA3AF", ["WaveRx"] = "#2563EB",
        ["WaveTx"] = "#16A34A", ["WaveText"] = "#374151",
    };

    private static readonly Dictionary<string, string> Dark = new()
    {
        ["WindowBg"] = "#111827", ["CardBg"] = "#1F2937", ["CardBorder"] = "#374151",
        ["Text"] = "#E5E7EB", ["TextMuted"] = "#9CA3AF", ["Title"] = "#F9FAFB", ["FieldLabel"] = "#D1D5DB",
        ["InputBg"] = "#111827", ["InputFg"] = "#F3F4F6", ["InputBorder"] = "#4B5563",
        ["ButtonBg"] = "#374151", ["ButtonFg"] = "#F3F4F6", ["ButtonBorder"] = "#4B5563",
        ["Accent"] = "#3B82F6", ["Danger"] = "#EF4444",
        ["RxBg"] = "#1E1E1E", ["RxFg"] = "#D4D4D4", ["RxHeaderBg"] = "#252526", ["RxHeaderFg"] = "#E5E7EB",
        ["RxSelection"] = "#264F78",
        ["PanelButtonBg"] = "#3A3D41", ["PanelButtonFg"] = "#E5E7EB", ["PanelButtonBorder"] = "#4B5563",
        ["StatusBarBg"] = "#0B1220", ["StatusFg"] = "#E5E7EB", ["StatusError"] = "#FCA5A5",
        ["TabBg"] = "#252526", ["TabFg"] = "#D1D5DB", ["TabSelBg"] = "#3B82F6", ["TabSelFg"] = "#FFFFFF",
        ["PopupBg"] = "#1F2937", ["HoverBg"] = "#374151", ["SelectionBg"] = "#1E3A8A", ["GridLine"] = "#374151",
        ["ScriptOutBg"] = "#0B1220", ["ScriptOutFg"] = "#93C5FD", ["ScrollThumb"] = "#4B5563", ["Splitter"] = "#374151",
        ["TopBarBg"] = "#1F2937",
        ["StatRx"] = "#93C5FD", ["StatTx"] = "#86EFAC", ["StatRate"] = "#FCD34D", ["StatPeak"] = "#A78BFA",
        ["StatMuted"] = "#9CA3AF", ["StatFrame"] = "#F9A8D4",
        ["TonePlain"] = "#D4D4D4", ["ToneTx"] = "#86EFAC", ["ToneCmd"] = "#7DD3FC", ["ToneOk"] = "#4ADE80",
        ["ToneErr"] = "#F87171", ["ToneUrc"] = "#FDE047", ["ToneInfo"] = "#C4B5FD", ["ToneMeta"] = "#93C5FD",
        ["WaveBg"] = "#111827", ["WaveGrid"] = "#374151", ["WaveAxis"] = "#6B7280", ["WaveRx"] = "#60A5FA",
        ["WaveTx"] = "#4ADE80", ["WaveText"] = "#D1D5DB",
    };

    private static readonly Dictionary<string, SolidColorBrush> Current = new();
    private static bool _systemHooked;

    public static ThemeMode Mode { get; private set; } = ThemeMode.Light;
    public static bool IsDark { get; private set; }
    public static int Version { get; private set; }
    public static event Action? Changed;

    public static readonly (ThemeMode Mode, string Name)[] Options =
    {
        (ThemeMode.Light, "浅色"), (ThemeMode.Dark, "深色"), (ThemeMode.System, "跟随系统"),
    };

    public static ThemeMode Parse(string? s) => s switch
    {
        "Dark" or "深色" => ThemeMode.Dark,
        "System" or "跟随系统" => ThemeMode.System,
        _ => ThemeMode.Light,
    };

    public static void Apply(ThemeMode mode)
    {
        Mode = mode;
        IsDark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemPrefersDark());
        if (mode == ThemeMode.System && !_systemHooked)
        {
            _systemHooked = true;
            try { SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged; } catch { /* ignore */ }
        }

        var palette = IsDark ? Dark : Light;
        var res = Application.Current?.Resources;
        Current.Clear();
        foreach (var kv in palette)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kv.Value));
            b.Freeze();
            Current[kv.Key] = b;
            if (res != null) res[kv.Key + "Brush"] = b;
        }
        Version++;

        if (Application.Current != null)
            foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
        Changed?.Invoke();
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (Mode != ThemeMode.System) return;
        if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.VisualStyle) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            bool dark = SystemPrefersDark();
            if (dark != IsDark) Apply(ThemeMode.System);
        });
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    public static Brush Get(string key) =>
        Current.TryGetValue(key, out var b) ? b : Brushes.Gray;

    public static Color GetColor(string key) =>
        Current.TryGetValue(key, out var b) ? b.Color : Colors.Gray;

    public static Brush ToneBrush(LineTone t) => Get("Tone" + t);

    /// <summary>窗口创建后自动同步标题栏颜色。</summary>
    public static void Attach(Window w)
    {
        w.SourceInitialized += (_, _) => ApplyTitleBar(w);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void ApplyTitleBar(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int)); // 旧版 Win10
        }
        catch
        {
            // 非 Windows 10+ 或无 DWM：忽略
        }
    }
}
