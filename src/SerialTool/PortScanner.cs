using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace SerialTool;

public static class PortScanner
{
    /// <summary>快速获取串口名（读注册表，开销很小，可用于轮询热插拔）。</summary>
    public static string[] GetNames()
    {
        try
        {
            return SerialPort.GetPortNames()
                .Select(n => n.Trim('\0', ' '))
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(SortKey)
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static int SortKey(string name)
    {
        int i = name.Length;
        while (i > 0 && char.IsDigit(name[i - 1])) i--;
        return i < name.Length && int.TryParse(name.Substring(i), out int v) ? v : int.MaxValue;
    }

    /// <summary>通过 WMI 获取设备友好名称（如 “USB-SERIAL CH340”）。较慢，请在后台线程调用。</summary>
    public static Dictionary<string, string> GetDescriptions()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            using var results = searcher.Get();
            foreach (ManagementBaseObject mo in results)
            {
                using (mo)
                {
                    if (mo["Name"] is not string name) continue;
                    var m = Regex.Match(name, @"\((COM\d+)\)", RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    string desc = name.Remove(m.Index, m.Length).Trim();
                    map[m.Groups[1].Value] = desc;
                }
            }
        }
        catch
        {
            // WMI 不可用时只显示端口名
        }
        return map;
    }
}
