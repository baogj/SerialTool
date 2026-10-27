using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace SerialTool.Features;

/// <summary>接收触发动作类型。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TriggerActionKind
{
    /// <summary>右下角弹出通知（不抢焦点）。参数：通知文本（可含 {rule} {count} {time} {match}）。</summary>
    Toast,
    /// <summary>系统托盘气泡通知。参数同 Toast。</summary>
    TrayBalloon,
    /// <summary>播放提示音。参数：Asterisk/Beep/Exclamation/Hand/Question 或 .wav 文件路径。</summary>
    Sound,
    /// <summary>开始记录日志。参数：日志文件路径（空 = 程序目录下 logs\trigger_时间.log）。</summary>
    StartLog,
    StopLog,
    /// <summary>把接收区快照保存为文件。参数：目录（空 = 程序目录下 snapshots）。</summary>
    Snapshot,
    /// <summary>暂停接收显示。</summary>
    PauseDisplay,
    /// <summary>停止定时发送与多项发送。</summary>
    StopSend,
    /// <summary>关闭端口 / 连接。</summary>
    ClosePort,
    /// <summary>运行脚本。参数：.lua 文件路径；为空则运行“脚本”页当前的脚本。</summary>
    RunScript,
}

public sealed class TriggerAction
{
    public TriggerActionKind Kind { get; set; } = TriggerActionKind.Toast;
    public string Param { get; set; } = "";

    public TriggerAction Clone() => (TriggerAction)MemberwiseClone();

    public static string Label(TriggerActionKind k) => k switch
    {
        TriggerActionKind.Toast => "弹出通知",
        TriggerActionKind.TrayBalloon => "托盘气泡",
        TriggerActionKind.Sound => "播放声音",
        TriggerActionKind.StartLog => "开始记录日志",
        TriggerActionKind.StopLog => "停止记录日志",
        TriggerActionKind.Snapshot => "保存接收区快照",
        TriggerActionKind.PauseDisplay => "暂停显示",
        TriggerActionKind.StopSend => "停止定时/多项发送",
        TriggerActionKind.ClosePort => "关闭端口",
        _ => "运行脚本",
    };

    public override string ToString() => string.IsNullOrWhiteSpace(Param) ? Label(Kind) : $"{Label(Kind)}（{Param}）";
}

/// <summary>接收触发规则：RX 数据匹配后执行一个或多个动作。与自动回复相互独立。</summary>
public sealed class TriggerRule
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public string Pattern { get; set; } = "";
    public bool IsRegex { get; set; }
    public bool IsHex { get; set; }
    public bool IgnoreCase { get; set; }
    /// <summary>同一规则两次触发的最短间隔（毫秒）。</summary>
    public int CooldownMs { get; set; } = 2000;
    public List<TriggerAction> Actions { get; set; } = new();
    /// <summary>累计命中次数（持久化，可在编辑器中清零）。</summary>
    public long HitCount { get; set; }
    public DateTime? LastHit { get; set; }

    [JsonIgnore] public bool IsUsable => Enabled && !string.IsNullOrEmpty(Pattern) && Actions.Count > 0;

    public TriggerRule Clone()
    {
        var c = (TriggerRule)MemberwiseClone();
        c.Actions = Actions.Select(a => a.Clone()).ToList();
        return c;
    }

    public void Normalize()
    {
        Name ??= "";
        Pattern ??= "";
        Actions ??= new List<TriggerAction>();
        Actions.RemoveAll(a => a == null);
        foreach (var a in Actions) a.Param ??= "";
        if (CooldownMs < 0) CooldownMs = 0;
        if (HitCount < 0) HitCount = 0;
    }

    public string ActionsSummary => Actions.Count == 0 ? "（无动作）" : string.Join("、", Actions.Select(a => TriggerAction.Label(a.Kind)));

    /// <summary>转成高亮规则以复用 CompiledRule / AutoReplyEngine 的匹配逻辑（不会产生真正的自动回复）。</summary>
    public HighlightRule ToMatcherRule() => new()
    {
        Enabled = Enabled && Actions.Count > 0,
        Name = Name,
        Pattern = Pattern,
        IsRegex = IsRegex,
        IsHex = IsHex,
        IgnoreCase = IgnoreCase,
        ReplyEnabled = true,
        CooldownMs = CooldownMs,
    };

    /// <summary>示例规则（首次使用时提供）。</summary>
    public static List<TriggerRule> Samples() => new()
    {
        new TriggerRule
        {
            Enabled = false, Name = "出错提醒", Pattern = "ERROR", IgnoreCase = true, CooldownMs = 3000,
            Actions = { new TriggerAction { Kind = TriggerActionKind.Toast, Param = "收到 ERROR（第 {count} 次）" }, new TriggerAction { Kind = TriggerActionKind.Sound, Param = "Exclamation" } },
        },
        new TriggerRule
        {
            Enabled = false, Name = "崩溃时保存现场", Pattern = "(?i)hard ?fault|panic", IsRegex = true, CooldownMs = 10000,
            Actions = { new TriggerAction { Kind = TriggerActionKind.Snapshot }, new TriggerAction { Kind = TriggerActionKind.PauseDisplay }, new TriggerAction { Kind = TriggerActionKind.StopSend } },
        },
    };
}

/// <summary>一次触发：规则 + 触发时的累计次数。</summary>
public readonly record struct TriggerHit(TriggerRule Rule, long Count, DateTime Time);

/// <summary>
/// 接收触发引擎：复用 CompiledRule + AutoReplyEngine（滚动窗口、跨分包匹配、每条规则冷却），
/// 但使用独立实例，与自动回复互不影响。
/// </summary>
public sealed class TriggerEngine
{
    private readonly AutoReplyEngine _matcher = new() { MaxRepliesPerSecond = 50 };
    private List<TriggerRule> _rules = new();
    private List<CompiledRule> _compiled = new();

    public Encoding Encoding
    {
        get => _matcher.Encoding;
        set => _matcher.Encoding = value;
    }

    /// <summary>因冷却或全局限速被抑制的次数。</summary>
    public long Suppressed => _matcher.Suppressed;

    public IReadOnlyList<TriggerRule> Rules => _rules;

    /// <summary>编译错误：规则序号 → 错误信息。</summary>
    public Dictionary<int, string> Errors { get; } = new();

    public void SetRules(IEnumerable<TriggerRule> rules, Encoding encoding)
    {
        _rules = rules.ToList();
        Encoding = encoding;
        Errors.Clear();
        _compiled = new List<CompiledRule>();
        for (int i = 0; i < _rules.Count; i++)
        {
            var cr = new CompiledRule(_rules[i].ToMatcherRule(), i, encoding);
            if (cr.Error != null && _rules[i].Enabled && !string.IsNullOrEmpty(_rules[i].Pattern)) Errors[i] = cr.Error;
            _compiled.Add(cr);
        }
        _matcher.SetRules(_compiled);
    }

    public void Reset() => _matcher.Reset();

    /// <summary>送入 RX 数据，返回本次触发的规则（已累加命中次数）。</summary>
    public List<TriggerHit> Push(byte[] data, DateTime now)
    {
        var hits = new List<TriggerHit>();
        if (_rules.Count == 0 || data.Length == 0) return hits;
        foreach (var cr in _matcher.Push(data, now))
        {
            var rule = _rules[cr.Index];
            if (!rule.IsUsable) continue;
            rule.HitCount++;
            rule.LastHit = now;
            hits.Add(new TriggerHit(rule, rule.HitCount, now));
        }
        return hits;
    }

    /// <summary>展开通知文本中的占位符。</summary>
    public static string Expand(string template, TriggerHit hit)
    {
        string t = string.IsNullOrWhiteSpace(template) ? "触发规则：{rule}（第 {count} 次）" : template;
        string name = string.IsNullOrWhiteSpace(hit.Rule.Name) ? hit.Rule.Pattern : hit.Rule.Name;
        return t.Replace("{rule}", name)
                .Replace("{count}", hit.Count.ToString())
                .Replace("{time}", hit.Time.ToString("HH:mm:ss"))
                .Replace("{pattern}", hit.Rule.Pattern);
    }
}

/// <summary>触发规则的持久化（独立文件 rx_triggers.json，与 settings.json 同目录）。</summary>
public static class TriggerRuleStore
{
    public const string FileName = "rx_triggers.json";

    private static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string PathIn(string dir) => System.IO.Path.Combine(dir, FileName);

    public static List<TriggerRule> Load(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return TriggerRule.Samples();
            var list = System.Text.Json.JsonSerializer.Deserialize<List<TriggerRule>>(System.IO.File.ReadAllText(path), Options) ?? new();
            list.RemoveAll(r => r == null);
            foreach (var r in list) r.Normalize();
            return list;
        }
        catch
        {
            return TriggerRule.Samples();
        }
    }

    public static void Save(string path, IEnumerable<TriggerRule> rules)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(rules.ToList(), Options);
        string tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, json, new UTF8Encoding(false));
        if (System.IO.File.Exists(path)) System.IO.File.Replace(tmp, path, null);
        else System.IO.File.Move(tmp, path);
    }
}
