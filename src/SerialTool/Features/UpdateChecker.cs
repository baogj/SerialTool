using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SerialTool.Features;

public sealed class ReleaseAsset
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string DownloadUrl { get; set; } = "";
}

public sealed class ReleaseInfo
{
    public string TagName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Body { get; set; } = "";
    public string HtmlUrl { get; set; } = "";
    public DateTime? PublishedAt { get; set; }
    public bool Prerelease { get; set; }
    public bool Draft { get; set; }
    public List<ReleaseAsset> Assets { get; set; } = new();

    public string Version => UpdateChecker.NormalizeVersion(TagName);
}

/// <summary>检查更新：GitHub Releases API、版本比较、下载（含镜像回退与大小校验）、自替换。不依赖 WPF。</summary>
public static class UpdateChecker
{
    public const string Owner = "baogj";
    public const string Repo = "SerialTool";
    public static string LatestApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
    public static string ReleasesPageUrl => $"https://github.com/{Owner}/{Repo}/releases";
    public const string MirrorPrefix = "https://ghproxy.net/";

    /// <summary>当前是否为 lite 版（net6 框架依赖版）；完整版为 net8 自包含。</summary>
    public static bool IsLiteBuild =>
#if NET8_0_OR_GREATER
        false;
#else
        true;
#endif

    // ---------------- 版本比较 ----------------

    /// <summary>"v1.8.0" / "V1.8" / "1.8.0-beta" → "1.8.0" / "1.8" / "1.8.0-beta"。</summary>
    public static string NormalizeVersion(string? v)
    {
        v = (v ?? "").Trim();
        if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = v.Substring(1);
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v.Substring(0, plus); // 去掉构建元数据
        return v;
    }

    /// <summary>
    /// 比较版本号：按数字段比较（缺失段视为 0，"1.8" == "1.8.0"）；
    /// 数字相同时，带预发布后缀（-beta 等）的版本小于正式版，后缀之间按字典序比较。
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        Split(NormalizeVersion(a), out var na, out var pa);
        Split(NormalizeVersion(b), out var nb, out var pb);
        int n = Math.Max(na.Count, nb.Count);
        for (int i = 0; i < n; i++)
        {
            long x = i < na.Count ? na[i] : 0, y = i < nb.Count ? nb[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        if (pa.Length == 0 && pb.Length == 0) return 0;
        if (pa.Length == 0) return 1;
        if (pb.Length == 0) return -1;
        return Math.Sign(string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsNewer(string? latest, string? current) => Compare(latest, current) > 0;

    private static void Split(string v, out List<long> nums, out string pre)
    {
        nums = new List<long>();
        pre = "";
        int dash = v.IndexOf('-');
        if (dash >= 0) { pre = v.Substring(dash + 1); v = v.Substring(0, dash); }
        foreach (var part in v.Split('.'))
        {
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            nums.Add(long.TryParse(digits, out var x) ? x : 0);
        }
    }

    // ---------------- 解析 ----------------

    /// <summary>解析 GitHub /releases/latest（或 /releases 列表中一项）的 JSON。</summary>
    public static ReleaseInfo ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            // /releases 列表：取第一个非草稿、非预发布版本
            foreach (var el in root.EnumerateArray())
            {
                var r = ParseElement(el);
                if (!r.Draft && !r.Prerelease) return r;
            }
            throw new FormatException("发布列表中没有正式版本");
        }
        return ParseElement(root);
    }

    private static ReleaseInfo ParseElement(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) throw new FormatException("发布信息格式错误");
        if (!el.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
        {
            string msg = el.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "缺少 tag_name";
            throw new FormatException("GitHub 返回：" + msg);
        }
        var r = new ReleaseInfo
        {
            TagName = tag.GetString() ?? "",
            Name = Str(el, "name"),
            Body = Str(el, "body"),
            HtmlUrl = Str(el, "html_url"),
            Prerelease = el.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True,
            Draft = el.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True,
        };
        if (el.TryGetProperty("published_at", out var pub) && pub.ValueKind == JsonValueKind.String && pub.TryGetDateTime(out var dt))
            r.PublishedAt = dt;
        if (el.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                r.Assets.Add(new ReleaseAsset
                {
                    Name = Str(a, "name"),
                    Size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var s) ? s : 0,
                    DownloadUrl = Str(a, "browser_download_url"),
                });
            }
        }
        return r;
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>选择与当前构建匹配的 exe：lite 版选名字含 "lite" 的 .exe，完整版选不含 "lite" 的 .exe。</summary>
    public static ReleaseAsset? PickAsset(ReleaseInfo r, bool lite)
    {
        var exes = r.Assets.Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && a.DownloadUrl.Length > 0).ToList();
        var pick = lite
            ? exes.FirstOrDefault(a => a.Name.IndexOf("lite", StringComparison.OrdinalIgnoreCase) >= 0)
            : exes.FirstOrDefault(a => a.Name.IndexOf("lite", StringComparison.OrdinalIgnoreCase) < 0);
        return pick;
    }

    /// <summary>下载地址候选：原始地址 + ghproxy.net 镜像。</summary>
    public static List<string> CandidateUrls(string url) => new() { url, MirrorPrefix + url };

    /// <summary>启动时自动检查：开启且距上次检查已满 24 小时（时钟回拨时也允许检查）。</summary>
    public static bool ShouldAutoCheck(bool enabled, DateTime? lastCheck, DateTime now)
    {
        if (!enabled) return false;
        if (lastCheck == null) return true;
        var span = now - lastCheck.Value;
        return span < TimeSpan.Zero || span >= TimeSpan.FromHours(24);
    }

    public static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var c = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("SerialTool-UpdateChecker/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static async Task<ReleaseInfo> FetchLatestAsync(HttpClient client, string? apiUrl = null, CancellationToken ct = default)
    {
        using var resp = await client.GetAsync(apiUrl ?? LatestApiUrl, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string msg = body;
            try { using var d = JsonDocument.Parse(body); if (d.RootElement.TryGetProperty("message", out var m)) msg = m.GetString() ?? body; } catch { }
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}：{(msg.Length > 200 ? msg.Substring(0, 200) : msg)}");
        }
        return ParseRelease(body);
    }

    /// <summary>
    /// 依次尝试候选地址下载到 destPath；expectedSize &gt; 0 时校验文件大小。
    /// 返回实际使用的地址；全部失败时抛出包含每个地址错误原因的异常。
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient client, IReadOnlyList<string> urls, long expectedSize, string destPath,
        IProgress<(long done, long total, string url)>? progress = null, CancellationToken ct = default)
    {
        var errors = new List<string>();
        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            string tmp = destPath + ".part";
            try
            {
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
                long total = resp.Content.Headers.ContentLength ?? expectedSize;
                if (expectedSize > 0 && resp.Content.Headers.ContentLength is long cl && cl != expectedSize)
                    throw new InvalidDataException($"服务器返回大小 {cl} 与预期 {expectedSize} 不符");
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buf = new byte[81920];
                    long done = 0;
                    int n;
                    var last = DateTime.MinValue;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                        done += n;
                        if (expectedSize > 0 && done > expectedSize) throw new InvalidDataException("下载数据超过预期大小");
                        var now = DateTime.UtcNow;
                        if ((now - last).TotalMilliseconds > 100) { progress?.Report((done, total, url)); last = now; }
                    }
                    progress?.Report((done, total, url));
                }
                long len = new FileInfo(tmp).Length;
                if (len == 0) throw new InvalidDataException("下载的文件为空");
                if (expectedSize > 0 && len != expectedSize) throw new InvalidDataException($"文件大小 {len} 与预期 {expectedSize} 不符");
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tmp, destPath);
                return url;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDelete(tmp);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(tmp);
                errors.Add($"{url}：{ex.Message}");
            }
        }
        throw new IOException("下载失败：\n" + string.Join("\n", errors));
    }

    /// <summary>
    /// 替换正在运行的 exe：先把当前 exe 改名为 .old（Windows 允许重命名运行中的文件），
    /// 再把新文件移动到原位置。失败时回滚。返回 .old 路径。
    /// </summary>
    public static string ReplaceExecutable(string currentExe, string newFile)
    {
        string old = currentExe + ".old";
        if (File.Exists(old))
        {
            try { File.Delete(old); }
            catch { old = currentExe + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".old"; }
        }
        File.Move(currentExe, old);
        try
        {
            File.Move(newFile, currentExe);
        }
        catch
        {
            try { File.Move(old, currentExe); } catch { /* 回滚失败时保留 .old */ }
            throw;
        }
        return old;
    }

    /// <summary>启动时清理上次更新留下的 *.old 文件（可能仍被占用，失败忽略）。</summary>
    public static int CleanupOld(string currentExe)
    {
        int n = 0;
        try
        {
            string dir = Path.GetDirectoryName(currentExe) ?? ".";
            string name = Path.GetFileName(currentExe);
            foreach (var f in Directory.GetFiles(dir, name + ".*old"))
            {
                try { File.Delete(f); n++; } catch { }
            }
        }
        catch { }
        return n;
    }

    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}
