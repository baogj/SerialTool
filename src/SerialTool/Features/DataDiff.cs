using System;
using System.Collections.Generic;
using System.Linq;

namespace SerialTool.Features;

/// <summary>一处差异：A 中 [AStart, AStart+ALen) 对应 B 中 [BStart, BStart+BLen)。ALen=0 为插入，BLen=0 为删除。</summary>
public readonly record struct DiffHunk(int AStart, int ALen, int BStart, int BLen)
{
    public string Kind => ALen == 0 ? "插入" : BLen == 0 ? "删除" : "修改";
}

public sealed class DiffResult
{
    public List<DiffHunk> Hunks { get; } = new();
    public int LengthA { get; init; }
    public int LengthB { get; init; }
    /// <summary>差异过多或数据过大，退化为按位置逐个比较（未做对齐）。</summary>
    public bool Approximate { get; set; }
    public bool Identical => Hunks.Count == 0;
    /// <summary>A 中不同的单元数（被删除/修改的字节或行）。</summary>
    public int ChangedA => Hunks.Sum(h => h.ALen);
    /// <summary>B 中不同的单元数（插入/修改的字节或行）。</summary>
    public int ChangedB => Hunks.Sum(h => h.BLen);
    /// <summary>差异单元数：每处差异取两侧较大者之和。</summary>
    public int DifferingCount => Hunks.Sum(h => Math.Max(h.ALen, h.BLen));
}

/// <summary>数据对比：Myers O(ND) 差分（字节或文本行），超过上限时退化为按位置比较。不依赖 WPF。</summary>
public static class DataDiff
{
    /// <summary>Myers 算法允许的最大编辑距离（超过后退化，避免内存/耗时失控）。</summary>
    public const int DefaultMaxEdits = 4000;
    /// <summary>参与对比的最大单元数（两侧之和）。</summary>
    public const int MaxTokens = 4_000_000;

    public static DiffResult CompareBytes(byte[] a, byte[] b, bool align = true, int maxEdits = DefaultMaxEdits) =>
        Compare(a.Select(x => (int)x).ToArray(), b.Select(x => (int)x).ToArray(), align, maxEdits);

    public static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1); // 末尾换行不算一行
        return lines.ToArray();
    }

    public static DiffResult CompareLines(string[] a, string[] b, bool ignoreCase = false, bool ignoreWhitespace = false, bool align = true, int maxEdits = DefaultMaxEdits)
    {
        var ids = new Dictionary<string, int>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        int Id(string s)
        {
            if (ignoreWhitespace) s = new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (!ids.TryGetValue(s, out int id)) { id = ids.Count; ids[s] = id; }
            return id;
        }
        return Compare(a.Select(Id).ToArray(), b.Select(Id).ToArray(), align, maxEdits);
    }

    public static DiffResult Compare(int[] a, int[] b, bool align = true, int maxEdits = DefaultMaxEdits)
    {
        var result = new DiffResult { LengthA = a.Length, LengthB = b.Length };
        if (!align)
        {
            Positional(a, 0, a.Length, b, 0, b.Length, result.Hunks);
            return result;
        }
        // 去掉公共前后缀
        int pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
        int suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;
        int aS = pre, aE = a.Length - suf, bS = pre, bE = b.Length - suf;
        int n = aE - aS, m = bE - bS;
        if (n == 0 && m == 0) return result;
        if (n == 0 || m == 0)
        {
            result.Hunks.Add(new DiffHunk(aS, n, bS, m));
            return result;
        }
        if (n + m > MaxTokens || !Myers(a, aS, n, b, bS, m, maxEdits, result.Hunks))
        {
            result.Hunks.Clear();
            result.Approximate = true;
            Positional(a, aS, aE, b, bS, bE, result.Hunks);
        }
        return result;
    }

    /// <summary>按位置逐个比较，连续不同的位置合并为一处差异；长度不同的尾部作为插入/删除。</summary>
    private static void Positional(int[] a, int aS, int aE, int[] b, int bS, int bE, List<DiffHunk> hunks)
    {
        int n = aE - aS, m = bE - bS, common = Math.Min(n, m);
        int i = 0;
        while (i < common)
        {
            if (a[aS + i] == b[bS + i]) { i++; continue; }
            int start = i;
            while (i < common && a[aS + i] != b[bS + i]) i++;
            hunks.Add(new DiffHunk(aS + start, i - start, bS + start, i - start));
        }
        if (n > m) Append(hunks, new DiffHunk(aS + common, n - common, bS + common, 0));
        else if (m > n) Append(hunks, new DiffHunk(aS + common, 0, bS + common, m - common));
    }

    private static void Append(List<DiffHunk> hunks, DiffHunk h)
    {
        if (hunks.Count > 0)
        {
            var last = hunks[^1];
            if (last.AStart + last.ALen == h.AStart && last.BStart + last.BLen == h.BStart)
            {
                hunks[^1] = new DiffHunk(last.AStart, last.ALen + h.ALen, last.BStart, last.BLen + h.BLen);
                return;
            }
        }
        hunks.Add(h);
    }

    /// <summary>Myers 贪心最短编辑脚本。超过 maxEdits 时返回 false。</summary>
    private static bool Myers(int[] a, int aOff, int n, int[] b, int bOff, int m, int maxEdits, List<DiffHunk> hunks)
    {
        int max = Math.Min(n + m, Math.Max(1, maxEdits));
        int offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        int dFound = -1;
        for (int d = 0; d <= max; d++)
        {
            // 只保存 [-d, d] 区间，节省内存
            var snap = new int[2 * d + 1];
            for (int k = -d; k <= d; k++) snap[k + d] = v[k + offset];
            trace.Add(snap);
            for (int k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset])) x = v[k + 1 + offset];
                else x = v[k - 1 + offset] + 1;
                int y = x - k;
                while (x < n && y < m && a[aOff + x] == b[bOff + y]) { x++; y++; }
                v[k + offset] = x;
                if (x >= n && y >= m) { dFound = d; break; }
            }
            if (dFound >= 0) break;
        }
        if (dFound < 0) return false;

        // 回溯得到编辑序列（逆序）：0=相等 1=删除(A) 2=插入(B)
        var ops = new List<(int kind, int ax, int by)>();
        int cx = n, cy = m;
        for (int d = dFound; d > 0; d--)
        {
            var vv = trace[d]; // 第 d 步开始前的 V（即第 d-1 步结束后）
            int k = cx - cy;
            int Get(int kk) => vv[kk + d];
            int prevK = (k == -d || (k != d && Get(k - 1) < Get(k + 1))) ? k + 1 : k - 1;
            int prevX = Get(prevK);
            int prevY = prevX - prevK;
            while (cx > prevX && cy > prevY) { ops.Add((0, cx - 1, cy - 1)); cx--; cy--; }
            if (prevK == k + 1) ops.Add((2, cx, cy - 1)); // 插入 b[cy-1]
            else ops.Add((1, cx - 1, cy));                // 删除 a[cx-1]
            cx = prevX; cy = prevY;
        }
        ops.Reverse();

        // 合并为差异块
        int ai = 0, bi = 0;
        int hs = -1, hbs = -1, hal = 0, hbl = 0;
        void Flush()
        {
            if (hs >= 0) Append(hunks, new DiffHunk(aOff + hs, hal, bOff + hbs, hbl));
            hs = -1; hal = 0; hbl = 0;
        }
        // 开头的相等部分（d=0 时的 snake）在 ops 中不显式出现，用坐标推进处理
        foreach (var (kind, ax, by) in ops)
        {
            if (kind == 1)
            {
                if (ai < ax || bi < by) { Flush(); ai = ax; bi = by; }
                if (hs < 0) { hs = ai; hbs = bi; }
                hal++; ai++;
            }
            else if (kind == 2)
            {
                if (ai < ax || bi < by) { Flush(); ai = ax; bi = by; }
                if (hs < 0) { hs = ai; hbs = bi; }
                hbl++; bi++;
            }
            else
            {
                Flush();
                ai = ax + 1; bi = by + 1;
            }
        }
        Flush();
        return true;
    }

    /// <summary>差异导航：forward=true 取下一处，false 取上一处，首尾循环。count=0 返回 -1。</summary>
    public static int Navigate(int count, int current, bool forward)
    {
        if (count <= 0) return -1;
        if (current < 0 || current >= count) return forward ? 0 : count - 1;
        return forward ? (current + 1) % count : (current - 1 + count) % count;
    }

    /// <summary>把差异应用回 A，应能得到 B（用于自检/测试）。</summary>
    public static int[] Apply(int[] a, int[] b, DiffResult r)
    {
        var outList = new List<int>();
        int ai = 0;
        foreach (var h in r.Hunks)
        {
            while (ai < h.AStart) outList.Add(a[ai++]);
            for (int j = 0; j < h.BLen; j++) outList.Add(b[h.BStart + j]);
            ai += h.ALen;
        }
        while (ai < a.Length) outList.Add(a[ai++]);
        return outList.ToArray();
    }
}
