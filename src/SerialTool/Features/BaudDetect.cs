using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SerialTool.Features;

public sealed class BaudScore
{
    public int Baud { get; init; }
    public int Bytes { get; init; }
    /// <summary>0–100，越高越可能是正确波特率。</summary>
    public double Score { get; init; }
    public double PrintableRatio { get; init; }
    public string Preview { get; init; } = "";
    public string Note { get; init; } = "";
}

/// <summary>根据收到的数据评估波特率是否正确。</summary>
public static class BaudScorer
{
    public static readonly int[] StandardRates =
        { 1200, 2400, 4800, 9600, 14400, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };

    /// <summary>
    /// 评分规则：
    /// 可打印 ASCII / 常见控制符（\r \n \t）占比是主要依据；
    /// 0x00 / 0xFF 大量出现（错误波特率下常见的帧错误产物）扣分；
    /// 出现成对的 CRLF、字母数字比例高加分；样本太少则打折。
    /// </summary>
    public static BaudScore Score(int baud, byte[] data)
    {
        if (data.Length == 0)
            return new BaudScore { Baud = baud, Bytes = 0, Score = 0, Note = "无数据" };

        int printable = 0, alnum = 0, zeros = 0, ffs = 0, high = 0, crlf = 0, ctrl = 0;
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b >= 0x20 && b < 0x7F)
            {
                printable++;
                if ((b >= '0' && b <= '9') || (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z')) alnum++;
            }
            else if (b == '\r' || b == '\n' || b == '\t')
            {
                printable++;
                if (b == '\n' && i > 0 && data[i - 1] == '\r') crlf++;
            }
            else if (b == 0x00) zeros++;
            else if (b == 0xFF) ffs++;
            else if (b >= 0x80) high++;
            else ctrl++;
        }
        double n = data.Length;
        double pr = printable / n;
        double score = pr * 70
                       + Math.Min(1.0, alnum / Math.Max(1.0, printable)) * 15
                       + Math.Min(crlf, 3) * 5
                       - (zeros + ffs) / n * 40
                       - ctrl / n * 20;

        // 合法 UTF-8 的多字节序列不当作噪声（中文输出）
        if (high > 0 && IsValidUtf8(data))
        {
            score += high / n * 60;
            pr = (printable + high) / n;
        }

        // 样本太少时可信度低
        double confidence = Math.Min(1.0, n / 16.0);
        score = Math.Clamp(score * confidence, 0, 100);

        string note = data.Length < 4 ? "样本很少" :
            (zeros + ffs) / n > 0.3 ? "大量 00/FF（帧错误特征）" :
            pr > 0.9 ? "文本数据" : pr > 0.6 ? "部分可读" : "多为二进制";

        return new BaudScore
        {
            Baud = baud,
            Bytes = data.Length,
            Score = Math.Round(score, 1),
            PrintableRatio = Math.Round(pr, 3),
            Preview = MakePreview(data),
            Note = note,
        };
    }

    private static bool IsValidUtf8(byte[] data)
    {
        try
        {
            new System.Text.UTF8Encoding(false, true).GetString(data);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string MakePreview(byte[] data)
    {
        var sb = new System.Text.StringBuilder();
        foreach (byte b in data.Take(48))
        {
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
            else if (b == '\r') sb.Append("\\r");
            else if (b == '\n') sb.Append("\\n");
            else sb.Append('·');
        }
        if (data.Length > 48) sb.Append('…');
        return sb.ToString();
    }

    /// <summary>选出最佳结果：分数最高，且需超过阈值和一定字节数。</summary>
    public static BaudScore? Best(IEnumerable<BaudScore> scores, double minScore = 40) =>
        scores.Where(s => s.Bytes >= 2 && s.Score >= minScore)
              .OrderByDescending(s => s.Score)
              .ThenByDescending(s => s.Bytes)
              .FirstOrDefault();
}

/// <summary>依次在各波特率下采样并评分。sampler(baud, ct) 返回在该波特率下收到的数据。</summary>
public static class BaudDetector
{
    public static async Task<List<BaudScore>> RunAsync(
        Func<int, CancellationToken, Task<byte[]>> sampler,
        IEnumerable<int> rates,
        Action<BaudScore>? onResult,
        CancellationToken ct)
    {
        var results = new List<BaudScore>();
        foreach (int baud in rates)
        {
            ct.ThrowIfCancellationRequested();
            byte[] data;
            string? err = null;
            try
            {
                data = await sampler(baud, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                data = Array.Empty<byte>();
                err = ex.Message;
            }
            var s = BaudScorer.Score(baud, data);
            if (err != null) s = new BaudScore { Baud = baud, Note = "打开失败：" + err };
            results.Add(s);
            onResult?.Invoke(s);
        }
        return results;
    }
}
