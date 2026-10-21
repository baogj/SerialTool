using System;
using System.Collections.Generic;
using System.Text;
using SerialTool.Features;

int pass = 0, fail = 0;
void Check(string name, string expected, string actual)
{
    if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) { pass++; Console.WriteLine($"PASS  {name} = {actual}"); }
    else { fail++; Console.WriteLine($"FAIL  {name}: expected {expected}, got {actual}"); }
}
void CheckTrue(string name, bool cond) { if (cond) { pass++; Console.WriteLine($"PASS  {name}"); } else { fail++; Console.WriteLine($"FAIL  {name}"); } }
byte[] H(string s) { HexUtilLite.TryParse(s, out var b); return b; }
byte[] A(string s) => Encoding.ASCII.GetBytes(s);
string Hx(byte[] b) => Checksum.ToHex(b);

// ---- 校验算法（标准向量）----
Check("Modbus CRC16(01 03 00 00 00 0A) bytes", "C5 CD", Hx(Checksum.Compute(ChecksumKind.ModbusCrc16, H("01 03 00 00 00 0A"))));
Check("Modbus CRC16(01 03 00 00 00 01) bytes", "84 0A", Hx(Checksum.Compute(ChecksumKind.ModbusCrc16, H("01 03 00 00 00 01"))));
Check("Modbus CRC16(\"123456789\") value", "4B37", Checksum.ModbusCrc16(A("123456789")).ToString("X4"));
Check("CRC16-CCITT-FALSE(\"123456789\")", "29B1", Checksum.Crc16Ccitt(A("123456789"), 0xFFFF).ToString("X4"));
Check("CRC16-CCITT-FALSE bytes (high first)", "29 B1", Hx(Checksum.Compute(ChecksumKind.Crc16CcittFalse, A("123456789"))));
Check("CRC16 XModem(\"123456789\")", "31C3", Checksum.Crc16Ccitt(A("123456789"), 0x0000).ToString("X4"));
Check("CRC16 XModem bytes (high first)", "31 C3", Hx(Checksum.Compute(ChecksumKind.Crc16XModem, A("123456789"))));
Check("Sum8(\"123456789\") (0x1DD & FF)", "DD", Hx(Checksum.Compute(ChecksumKind.Sum8, A("123456789"))));
Check("Sum8(FF 01)", "00", Hx(Checksum.Compute(ChecksumKind.Sum8, H("FF 01"))));
Check("XOR(\"123456789\")", "31", Hx(Checksum.Compute(ChecksumKind.Xor8, A("123456789"))));
Check("XOR(AA 55)", "FF", Hx(Checksum.Compute(ChecksumKind.Xor8, H("AA 55"))));
Check("None -> empty", "", Hx(Checksum.Compute(ChecksumKind.None, A("abc"))));
Check("Append Modbus", "01 03 00 00 00 0A C5 CD", Hx(Checksum.Append(ChecksumKind.ModbusCrc16, H("01 03 00 00 00 0A"))));
Check("Modbus CRC16(empty)", "FFFF", Checksum.ModbusCrc16(Array.Empty<byte>()).ToString("X4"));
CheckTrue("Short name MODBUS", Checksum.TryParseShortName("modbus", out var k1) && k1 == ChecksumKind.ModbusCrc16);
CheckTrue("Short name CCITT", Checksum.TryParseShortName("CCITT", out var k2) && k2 == ChecksumKind.Crc16CcittFalse);
CheckTrue("Short name bad", !Checksum.TryParseShortName("foo", out _));

// ---- 高亮匹配 ----
var enc = new UTF8Encoding(false);
var rules = new List<CompiledRule>
{
    new(new HighlightRule { Pattern = "ERROR" }, 0, enc),
    new(new HighlightRule { Pattern = @"\+CSQ: \d+", IsRegex = true }, 1, enc),
    new(new HighlightRule { Pattern = "aa 55", IsHex = true }, 2, enc),
    new(new HighlightRule { Pattern = "ok", IgnoreCase = true }, 3, enc),
};
var spans = HighlightMatcher.Find("+CSQ: 23,99 then ERROR and OK", rules);
CheckTrue("highlight finds 3 spans", spans.Count == 3);
CheckTrue("highlight regex span at 0 len 8", spans.Count > 0 && spans[0].Start == 0 && spans[0].Length == 8 && spans[0].RuleIndex == 1);
CheckTrue("highlight ignore-case OK", spans.Exists(s => s.RuleIndex == 3));
var hexSpans = HighlightMatcher.Find("01 AA 55 02", rules);
CheckTrue("highlight HEX display span", hexSpans.Count == 1 && hexSpans[0].Start == 3 && hexSpans[0].Length == 5);
var bad = new CompiledRule(new HighlightRule { Pattern = "([", IsRegex = true }, 0, enc);
CheckTrue("invalid regex reported, no throw", !bad.IsValid && bad.Error != null);

// ---- 自动回复 ----
var ar = new AutoReplyEngine { Encoding = enc };
var r0 = new CompiledRule(new HighlightRule { Pattern = "PING", ReplyEnabled = true, ReplyContent = "PONG", CooldownMs = 1000 }, 0, enc);
var r1 = new CompiledRule(new HighlightRule { Pattern = "AA 55", IsHex = true, ReplyEnabled = true, CooldownMs = 0 }, 1, enc);
var r2 = new CompiledRule(new HighlightRule { Pattern = @"T=\d+;", IsRegex = true, ReplyEnabled = true, CooldownMs = 0 }, 2, enc);
ar.SetRules(new[] { r0, r1, r2 });
var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
CheckTrue("reply: split match PI+NG fires", ar.Push(A("xxPI"), t0).Count == 0 && ar.Push(A("NG\r\n"), t0.AddMilliseconds(5)).Exists(r => r.Index == 0));
CheckTrue("reply: same data not re-fired", !ar.Push(A("zz"), t0.AddMilliseconds(2000)).Exists(r => r.Index == 0));
CheckTrue("reply: cooldown suppresses", !ar.Push(A("PING"), t0.AddMilliseconds(100)).Exists(r => r.Index == 0));
CheckTrue("reply: after cooldown fires again", ar.Push(A("PING"), t0.AddMilliseconds(3000)).Exists(r => r.Index == 0));
CheckTrue("reply: HEX byte match", ar.Push(new byte[] { 0x01, 0xAA, 0x55 }, t0.AddMilliseconds(4000)).Exists(r => r.Index == 1));
CheckTrue("reply: regex match", ar.Push(A("T=25;"), t0.AddMilliseconds(5000)).Exists(r => r.Index == 2));
CheckTrue("reply: regex not re-fired", !ar.Push(A("x"), t0.AddMilliseconds(5001)).Exists(r => r.Index == 2));
var flood = new AutoReplyEngine { Encoding = enc, MaxRepliesPerSecond = 5 };
flood.SetRules(new[] { r1 });
int fired = 0;
for (int i = 0; i < 50; i++) fired += flood.Push(new byte[] { 0xAA, 0x55 }, t0.AddMilliseconds(i)).Count;
CheckTrue($"reply: global rate limit (fired {fired} <= 5)", fired == 5);

// ---- 发送回显：校验字节以 [HEX] 显示 ----
var fmt = new SerialTool.DisplayFormatter { ShowTx = true };
var sbx = new StringBuilder();
var payload = Checksum.Append(ChecksumKind.Sum8, A("AT"));                 // 41 54 95
var withLe = new byte[payload.Length + 2]; payload.CopyTo(withLe, 0); withLe[^2] = 0x0D; withLe[^1] = 0x0A;
fmt.Format(new SerialTool.DataChunk(true, withLe, t0) { MarkStart = 2, MarkLength = 1 }, sbx);
Check("TX echo text shows checksum", "发→ AT[95]\r\n", sbx.ToString());
var sbh = new StringBuilder();
var fmtHex = new SerialTool.DisplayFormatter { Hex = true };
fmtHex.Format(new SerialTool.DataChunk(false, H("01 03 00 00 00 0A C5 CD"), t0), sbh);
Check("RX HEX display", "01 03 00 00 00 0A C5 CD ", sbh.ToString());

// ---- Modbus 帧解析：用发送端校验生成的帧必须被接收端判定 CRC OK ----
var mb = new ModbusRtuParser(50);
var frame = Checksum.Append(ChecksumKind.ModbusCrc16, H("01 03 00 00 00 0A"));
var frames = mb.Push(new SerialTool.DataChunk(true, frame, t0));
CheckTrue("Modbus parser: our CRC frame is OK", frames.Count == 1 && frames[0].CrcOk && frames[0].Function == 0x03);
var badFrame = H("01 03 00 00 00 0A C5 CE");
mb.Reset();
var bf = mb.Push(new SerialTool.DataChunk(true, badFrame, t0));
CheckTrue("Modbus parser: corrupted CRC flagged", bf.Count == 1 && !bf[0].CrcOk);

Console.WriteLine($"\n结果：{pass} 通过，{fail} 失败");
return fail == 0 ? 0 : 1;
