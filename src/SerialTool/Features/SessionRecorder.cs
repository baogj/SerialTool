using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SerialTool.Features;

/// <summary>录制文件中的一条记录（JSON Lines，每行一个对象）。</summary>
public sealed class RecordEntry
{
    /// <summary>距录制开始的毫秒数。</summary>
    [JsonPropertyName("t")] public double OffsetMs { get; set; }
    /// <summary>"rx" 或 "tx"。</summary>
    [JsonPropertyName("dir")] public string Dir { get; set; } = "rx";
    /// <summary>数据（十六进制，空格分隔）。</summary>
    [JsonPropertyName("hex")] public string Hex { get; set; } = "";
    /// <summary>绝对时间（ISO 8601，本地时区），仅供参考。</summary>
    [JsonPropertyName("time")] public string? Time { get; set; }
    /// <summary>网络连接时的远端地址。</summary>
    [JsonPropertyName("src")] public string? Source { get; set; }

    [JsonIgnore] public bool IsTx => string.Equals(Dir, "tx", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public byte[] Data => FrameHex.TryParse(Hex, out var d) ? d : Array.Empty<byte>();
}

/// <summary>会话录制：把收发数据连同时间戳写入 .jsonl 文件。</summary>
public sealed class SessionRecorder : IDisposable
{
    public const string HeaderType = "serialtool-session";
    private static readonly JsonSerializerOptions Opts = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly TextWriter _w;
    private readonly object _lock = new();
    private DateTime? _start;

    public SessionRecorder(TextWriter writer, string? description = null)
    {
        _w = writer;
        var header = new Dictionary<string, object?>
        {
            ["type"] = HeaderType,
            ["version"] = 1,
            ["created"] = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
            ["description"] = description,
        };
        _w.WriteLine(JsonSerializer.Serialize(header, Opts));
    }

    public static SessionRecorder Create(string path, string? description = null) =>
        new(new StreamWriter(path, false, new UTF8Encoding(false)), description);

    public long Count { get; private set; }
    public long Bytes { get; private set; }
    public string? Path { get; init; }

    public void Write(bool isTx, byte[] data, DateTime time, string? source = null)
    {
        if (data.Length == 0) return;
        lock (_lock)
        {
            _start ??= time;
            var e = new RecordEntry
            {
                OffsetMs = Math.Round(Math.Max(0, (time - _start.Value).TotalMilliseconds), 3),
                Dir = isTx ? "tx" : "rx",
                Hex = ToHex(data),
                Time = time.ToString("o", CultureInfo.InvariantCulture),
                Source = source,
            };
            _w.WriteLine(JsonSerializer.Serialize(e, Opts));
            Count++;
            Bytes += data.Length;
        }
    }

    public void Flush()
    {
        lock (_lock) _w.Flush();
    }

    private static string ToHex(byte[] d)
    {
        var sb = new StringBuilder(d.Length * 3);
        for (int i = 0; i < d.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(d[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _w.Flush();
            _w.Dispose();
        }
    }
}

public static class SessionReader
{
    /// <summary>读取 .jsonl 录制文件；跳过文件头和无法解析的行。</summary>
    public static List<RecordEntry> Read(TextReader reader, out int badLines)
    {
        var list = new List<RecordEntry>();
        badLines = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) { badLines++; continue; }
                if (doc.RootElement.TryGetProperty("type", out _)) continue; // 文件头
                var e = doc.RootElement.Deserialize<RecordEntry>();
                if (e == null || !FrameHex.TryParse(e.Hex, out _)) { badLines++; continue; }
                list.Add(e);
            }
            catch (JsonException)
            {
                badLines++;
            }
        }
        list.Sort((a, b) => a.OffsetMs.CompareTo(b.OffsetMs));
        return list;
    }

    public static List<RecordEntry> ReadFile(string path, out int badLines)
    {
        using var r = new StreamReader(path, Encoding.UTF8);
        return Read(r, out badLines);
    }
}

/// <summary>回放：按原始间隔（乘以速度系数）依次投递记录。</summary>
public sealed class SessionReplayer
{
    private readonly IReadOnlyList<RecordEntry> _entries;
    private readonly ManualResetEventSlim _running = new(true);

    public SessionReplayer(IReadOnlyList<RecordEntry> entries)
    {
        _entries = entries;
    }

    /// <summary>速度倍数：0.5 / 1 / 2 …；&lt;= 0 或 double.PositiveInfinity 表示尽快。可在回放中修改。</summary>
    public double Speed { get; set; } = 1;
    /// <summary>为 true 时只回放 RX（或只回放 TX，由 Filter 决定）。null = 全部。</summary>
    public Func<RecordEntry, bool>? Filter { get; set; }
    /// <summary>单次等待的上限，避免录制中的长时间空闲（毫秒；0 = 不限制）。</summary>
    public int MaxGapMs { get; set; }

    public bool IsPaused => !_running.IsSet;
    public int Position { get; private set; }

    public void Pause() => _running.Reset();
    public void Resume() => _running.Set();

    /// <summary>执行回放。deliver 在后台线程调用。progress(已完成, 总数)。</summary>
    public Task RunAsync(Action<RecordEntry> deliver, Action<int, int>? progress, CancellationToken ct) =>
        Task.Run(() => Run(deliver, progress, ct), CancellationToken.None);

    public void Run(Action<RecordEntry> deliver, Action<int, int>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        double lastOffset = _entries.Count > 0 ? _entries[0].OffsetMs : 0;
        double baseReal = 0;       // virtualMs 对应的真实时间点
        for (int i = 0; i < _entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var e = _entries[i];
            double gap = Math.Max(0, e.OffsetMs - lastOffset);
            if (MaxGapMs > 0) gap = Math.Min(gap, MaxGapMs);
            lastOffset = e.OffsetMs;

            double speed = Speed;
            bool max = speed <= 0 || double.IsInfinity(speed);
            if (!max)
            {
                double target = baseReal + gap / speed;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!_running.IsSet)
                    {
                        double pausedAt = sw.Elapsed.TotalMilliseconds;
                        _running.Wait(ct);
                        double pausedFor = sw.Elapsed.TotalMilliseconds - pausedAt;
                        target += pausedFor;
                    }
                    double now = sw.Elapsed.TotalMilliseconds;
                    double left = target - now;
                    if (left <= 0) break;
                    int wait = (int)Math.Min(left, 50);
                    if (wait >= 1) ct.WaitHandle.WaitOne(wait);
                    else Thread.SpinWait(50);
                }
                baseReal = Math.Max(target, 0);
            }
            else
            {
                _running.Wait(ct);
                baseReal = sw.Elapsed.TotalMilliseconds;
            }

            if (Filter == null || Filter(e)) deliver(e);
            Position = i + 1;
            progress?.Invoke(i + 1, _entries.Count);
        }
    }
}
