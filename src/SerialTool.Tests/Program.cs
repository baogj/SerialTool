using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SerialTool;
using SerialTool.Features;

if (args.Length == 2 && args[0] == "--dump-samples")
{
    System.IO.Directory.CreateDirectory(args[1]);
    TestSuite.Sample().Save(System.IO.Path.Combine(args[1], "示例测试用例.json"));
    foreach (var (n, code) in new[] { ("at_test.lua", LuaSamples.AtTest), ("modbus_poll.lua", LuaSamples.ModbusPoll), ("echo_on_receive.lua", LuaSamples.Echo) })
        System.IO.File.WriteAllText(System.IO.Path.Combine(args[1], n), code, new UTF8Encoding(false));
    Console.WriteLine("samples written");
    return 0;
}
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


// ======================================================================
// v1.3：Modbus 主站
// ======================================================================
ModbusRequest Req(int slave, byte fc, int start, int qty, string values = "", RegisterFormat f = RegisterFormat.UInt16,
    WordOrder o = WordOrder.ABCD, ModbusFraming fr = ModbusFraming.Rtu, ushort tid = 1)
{
    if (!ModbusMaster.TryCreate(slave, fc, start, qty, values, f, o, fr, tid, out var r, out var e)) throw new Exception(e);
    return r;
}
Check("MB build 03 read", "01 03 00 00 00 0A C5 CD", Hx(ModbusMaster.BuildFrame(Req(1, 3, 0, 10))));
Check("MB build 06 write single", "01 06 00 01 00 03 98 0B", Hx(ModbusMaster.BuildFrame(Req(1, 6, 1, 1, "3"))));
Check("MB build 10 write multiple (spec example)", "11 10 00 01 00 02 04 00 0A 01 02 C6 F0", Hx(ModbusMaster.BuildFrame(Req(0x11, 0x10, 1, 0, "10,0x0102"))));
Check("MB build 05 coil ON (spec example)", "11 05 00 AC FF 00 4E 8B", Hx(ModbusMaster.BuildFrame(Req(0x11, 5, 0xAC, 1, "ON"))));
Check("MB build 0F coils (spec example)", "11 0F 00 13 00 0A 02 CD 01 BF 0B", Hx(ModbusMaster.BuildFrame(Req(0x11, 0x0F, 0x13, 0, "1,0,1,1,0,0,1,1,1,0"))));
Check("MB build Modbus TCP MBAP", "00 07 00 00 00 06 01 03 00 00 00 0A", Hx(ModbusMaster.BuildFrame(Req(1, 3, 0, 10, fr: ModbusFraming.Tcp, tid: 7))));
Check("MB build 10 Float32 CDAB", "01 10 00 00 00 02 04 E9 79 42 F6", Hx(ModbusMaster.BuildPdu(Req(1, 0x10, 0, 0, "123.456", RegisterFormat.Float32, WordOrder.CDAB)).Prepend((byte)1).ToArray()));
CheckTrue("MB range check: 126 regs rejected", !ModbusMaster.TryCreate(1, 3, 0, 126, "", RegisterFormat.UInt16, WordOrder.ABCD, ModbusFraming.Rtu, 1, out _, out var e126) && e126 != null);
CheckTrue("MB broadcast read rejected", !ModbusMaster.TryCreate(0, 3, 0, 1, "", RegisterFormat.UInt16, WordOrder.ABCD, ModbusFraming.Rtu, 1, out _, out _));
CheckTrue("MB bad value rejected", !ModbusMaster.TryCreate(1, 6, 0, 1, "abc", RegisterFormat.UInt16, WordOrder.ABCD, ModbusFraming.Rtu, 1, out _, out _));
CheckTrue("MB value 70000 out of range", !ModbusMaster.TryCreate(1, 6, 0, 1, "70000", RegisterFormat.UInt16, WordOrder.ABCD, ModbusFraming.Rtu, 1, out _, out _));

var rq = Req(1, 3, 0, 2);
var rsp = Checksum.Append(ChecksumKind.ModbusCrc16, H("01 03 04 00 0A 01 02"));
var pr = ModbusMaster.TryParse(rsp.ToList(), rq);
CheckTrue("MB parse 03 response", pr.Status == ModbusResultStatus.Ok && pr.Registers.Length == 2 && pr.Registers[0] == 10 && pr.Registers[1] == 0x0102);
var withNoise = new byte[] { 0x00, 0xFF, 0x01 }.Concat(rsp).ToList();
CheckTrue("MB parse skips leading noise", ModbusMaster.TryParse(withNoise, rq).Status == ModbusResultStatus.Ok);
CheckTrue("MB parse partial = Incomplete", ModbusMaster.TryParse(rsp.Take(5).ToList(), rq).Status == ModbusResultStatus.Incomplete);
var exc = Checksum.Append(ChecksumKind.ModbusCrc16, H("01 83 02"));
var pe = ModbusMaster.TryParse(exc.ToList(), rq);
CheckTrue("MB parse exception 02", pe.Status == ModbusResultStatus.Exception && pe.ExceptionCode == 2);
Check("MB exception name", "非法数据地址", ModbusMaster.ExceptionName(2));
var corrupt = rsp.ToArray(); corrupt[4] ^= 0x40;
CheckTrue("MB parse CRC error detected", ModbusMaster.TryParse(corrupt.ToList(), rq).Status == ModbusResultStatus.CrcError);
var coilRsp = Checksum.Append(ChecksumKind.ModbusCrc16, H("11 01 05 CD 6B B2 0E 1B"));
var pc = ModbusMaster.TryParse(coilRsp.ToList(), Req(0x11, 1, 0x13, 37));
CheckTrue("MB parse 01 coils (37 bits)", pc.Status == ModbusResultStatus.Ok && pc.Coils.Length == 37 && pc.Coils[0] && !pc.Coils[1] && pc.Coils[2] && pc.Coils[36]);
var wr = Checksum.Append(ChecksumKind.ModbusCrc16, H("01 06 00 01 00 03"));
var pw = ModbusMaster.TryParse(wr.ToList(), Req(1, 6, 1, 1, "3"));
CheckTrue("MB parse 06 echo", pw.Status == ModbusResultStatus.Ok && pw.EchoAddress == 1 && pw.EchoValue == 3);
var tcpReq = Req(1, 3, 0, 1, fr: ModbusFraming.Tcp, tid: 0x1234);
var pt = ModbusMaster.TryParse(H("12 34 00 00 00 05 01 03 02 00 2A").ToList(), tcpReq);
CheckTrue("MB parse Modbus TCP response", pt.Status == ModbusResultStatus.Ok && pt.Registers.Length == 1 && pt.Registers[0] == 42);
CheckTrue("MB TCP wrong transaction id ignored", ModbusMaster.TryParse(H("12 35 00 00 00 05 01 03 02 00 2A").ToList(), tcpReq).Status == ModbusResultStatus.Incomplete);
var ptex = ModbusMaster.TryParse(H("12 34 00 00 00 03 01 83 01").ToList(), tcpReq);
CheckTrue("MB TCP exception response", ptex.Status == ModbusResultStatus.Exception && ptex.ExceptionCode == 1);

// 32 位字节序：123.456f = 0x42F6E979
uint fbits = BitConverter.SingleToUInt32Bits(123.456f);
Check("float bits", "42F6E979", fbits.ToString("X8"));
string Regs(ushort[] r) => string.Join(" ", r.Select(x => x.ToString("X4")));
Check("order ABCD", "42F6 E979", Regs(ModbusMaster.SplitU32(fbits, WordOrder.ABCD)));
Check("order CDAB", "E979 42F6", Regs(ModbusMaster.SplitU32(fbits, WordOrder.CDAB)));
Check("order BADC", "F642 79E9", Regs(ModbusMaster.SplitU32(fbits, WordOrder.BADC)));
Check("order DCBA", "79E9 F642", Regs(ModbusMaster.SplitU32(fbits, WordOrder.DCBA)));
foreach (var ord in Enum.GetValues<WordOrder>())
{
    var rr = ModbusMaster.SplitU32(fbits, ord);
    Check($"Float32 decode {ord}", "123.456", ModbusMaster.FormatRegister(rr, 0, RegisterFormat.Float32, ord));
    Check($"Float32 second reg {ord}", "（续）", ModbusMaster.FormatRegister(rr, 1, RegisterFormat.Float32, ord));
}
Check("Int32 -2 CDAB regs", "FFFE FFFF", Regs(ModbusMaster.SplitU32(unchecked((uint)-2), WordOrder.CDAB)));
Check("Int32 decode -2 CDAB", "-2", ModbusMaster.FormatRegister(new ushort[] { 0xFFFE, 0xFFFF }, 0, RegisterFormat.Int32, WordOrder.CDAB));
Check("UInt32 decode ABCD", "65536", ModbusMaster.FormatRegister(new ushort[] { 0x0001, 0x0000 }, 0, RegisterFormat.UInt32, WordOrder.ABCD));
Check("Int16 decode", "-1", ModbusMaster.FormatRegister(new ushort[] { 0xFFFF }, 0, RegisterFormat.Int16, WordOrder.ABCD));
Check("Hex decode", "0x00FF", ModbusMaster.FormatRegister(new ushort[] { 255 }, 0, RegisterFormat.Hex, WordOrder.ABCD));
Check("Binary decode", "0000 0000 1111 0101", ModbusMaster.FormatRegister(new ushort[] { 0xF5 }, 0, RegisterFormat.Binary, WordOrder.ABCD));
CheckTrue("Float32 encode 1.5 ABCD", ModbusMaster.TryParseRegisterValues("1.5", RegisterFormat.Float32, WordOrder.ABCD, out var f15, out _) && Regs(f15) == "3FC0 0000");
CheckTrue("Int16 encode -1", ModbusMaster.TryParseRegisterValues("-1", RegisterFormat.Int16, WordOrder.ABCD, out var m1, out _) && m1[0] == 0xFFFF);

// 事务等待：分段到达 / 超时 / CRC 错误
var waiter = new ModbusTransactionWaiter();
var wt = waiter.Begin(rq);
waiter.Feed(rsp.Take(3).ToArray());
waiter.Feed(rsp.Skip(3).ToArray());
var wres = await waiter.WaitAsync(wt, 500);
CheckTrue("MB waiter: split response OK", wres.Status == ModbusResultStatus.Ok && wres.Registers.Length == 2);
var wt2 = waiter.Begin(rq);
var wres2 = await waiter.WaitAsync(wt2, 80);
CheckTrue("MB waiter: timeout", wres2.Status == ModbusResultStatus.Timeout);
var wt3 = waiter.Begin(rq);
waiter.Feed(corrupt);
var wres3 = await waiter.WaitAsync(wt3, 80);
CheckTrue("MB waiter: CRC error reported", wres3.Status == ModbusResultStatus.CrcError);

// ======================================================================
// v1.3：数值行解析 / 过滤 / 行拆分 / CSV
// ======================================================================
string Vals(string line) => string.Join(";", NumericLineParser.Parse(line).Select(v => $"{v.Name}={v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
Check("numeric named", "temp=23.5;hum=40", Vals("temp=23.5,hum=40"));
Check("numeric named with colon/space", "T=-1.5;P=1013", Vals("T: -1.5  P = 1013"));
Check("numeric csv values", "CH1=1.2;CH2=3.4;CH3=5", Vals("1.2, 3.4 5"));
Check("numeric tab/semicolon", "CH1=10;CH2=-20;CH3=0.5", Vals("10\t-20;0.5"));
Check("numeric exponent", "x=-1500", Vals("x:-1.5e3"));
Check("numeric chinese name", "温度=25.1", Vals("温度=25.1"));
Check("numeric none", "", Vals("AT+GMR"));
Check("numeric skip 12abc", "", Vals("ERROR 12abc"));

var lf = LineFilter.Create("error", false, true, out _)!;
CheckTrue("filter include match (ignore case)", lf.Passes("got ERROR here", true));
CheckTrue("filter include no match", !lf.Passes("all ok", true));
CheckTrue("filter exclude", lf.Passes("all ok", false) && !lf.Passes("ERROR", false));
var lfc = LineFilter.Create("error", false, false, out _)!;
CheckTrue("filter case-sensitive", !lfc.IsMatch("ERROR") && lfc.IsMatch("error"));
var lfr = LineFilter.Create(@"T=\d+", true, true, out _)!;
CheckTrue("filter regex", lfr.IsMatch("T=25;") && !lfr.IsMatch("T=x"));
CheckTrue("filter regex FindAll", lfr.FindAll("T=1 T=22 t=3").Count == 3);
CheckTrue("filter bad regex error", LineFilter.Create("([", true, true, out var ferr) == null && ferr != null);
CheckTrue("filter empty = null", LineFilter.Create("", false, true, out _) == null);
var lfn = LineFilter.Create("ab", false, true, out _)!;
CheckTrue("FindNext forward wraps", lfn.FindNext("ab..ab", 3, false).Start == 4 && lfn.FindNext("ab..ab", 5, false).Start == 0);
CheckTrue("FindNext backward wraps", lfn.FindNext("ab..ab", 4, true).Start == 0 && lfn.FindNext("ab..ab", 0, true).Start == 4);

var asm = new LineAssembler(enc);
var l1 = asm.Push(A("ab"), t0).ToList();
var l2 = asm.Push(A("c\r\nde"), t0.AddMilliseconds(5)).ToList();
CheckTrue("assembler joins split line", l1.Count == 0 && l2.Count == 1 && l2[0].Text == "abc" && Hx(l2[0].Bytes) == "61 62 63 0D 0A");
CheckTrue("assembler no flush before gap", asm.Flush(t0.AddMilliseconds(20), 100) == null);
var fl = asm.Flush(t0.AddMilliseconds(200), 100);
CheckTrue("assembler flush after gap", fl != null && fl.Text == "de");
var utf = new LineAssembler(enc);
var zh = enc.GetBytes("温度=1\n");
var zl = utf.Push(zh.Take(2).ToArray(), t0).Concat(utf.Push(zh.Skip(2).ToArray(), t0)).ToList();
CheckTrue("assembler UTF-8 split char", zl.Count == 1 && zl[0].Text == "温度=1");

var csv = new System.IO.StringWriter();
CsvExport.WriteLines(csv, new[] { new LineRecord { Time = t0, IsTx = false, Bytes = A("a,\"b\"\r\n"), Text = "a,\"b\"" } });
Check("CSV row escaping", "2026-01-01 00:00:00.000,接收,,7,61 2C 22 62 22 0D 0A,\"a,\"\"b\"\"\"", csv.ToString().Split('\n')[1].TrimEnd('\r'));
var cd = new CurveData();
cd.Add(t0, NumericLineParser.Parse("a=1,b=2"));
cd.Add(t0, NumericLineParser.Parse("b=3,c=4"));
var ccsv = new System.IO.StringWriter();
cd.WriteCsv(ccsv);
var crows = ccsv.ToString().Replace("\r", "").Trim().Split('\n');
CheckTrue("curve channels merged a,b,c", cd.Channels.Count == 3 && crows[0] == "时间,a,b,c");
Check("curve CSV row 2 (missing a)", "2026-01-01 00:00:00.000,,3,4", crows[2]);

// ======================================================================
// v1.3：网络回环（TCP / UDP / 串口转发）
// ======================================================================
async Task<bool> WaitFor(Func<bool> cond, int ms = 3000)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; await Task.Delay(10); }
    return cond();
}
string Drain(ConcurrentQueue<DataChunk> q, bool tx)
{
    var sb = new StringBuilder();
    foreach (var c in q) if (c.IsTx == tx) sb.Append(Encoding.ASCII.GetString(c.Data));
    return sb.ToString();
}

// TCP
var srvQ = new ConcurrentQueue<DataChunk>();
var cliQ = new ConcurrentQueue<DataChunk>();
var srv = TcpServerConnection.Listen(0, srvQ);
srv.Start();
var cli = await TcpClientConnection.ConnectAsync("127.0.0.1", srv.LocalPort, cliQ);
cli.Start();
CheckTrue("TCP server sees client", await WaitFor(() => srv.Clients.Count == 1));
cli.Send(A("hello"));
CheckTrue("TCP client -> server", await WaitFor(() => Drain(srvQ, false) == "hello"));
CheckTrue("TCP client TX echo", await WaitFor(() => Drain(cliQ, true) == "hello"));
CheckTrue("TCP server chunk has client source", srvQ.Any(c => !c.IsTx && c.Source != null && c.Source.Contains("127.0.0.1")));
srv.Send(A("world"));
CheckTrue("TCP server -> client (all)", await WaitFor(() => Drain(cliQ, false) == "world"));
var cli2Q = new ConcurrentQueue<DataChunk>();
var cli2 = await TcpClientConnection.ConnectAsync("localhost", srv.LocalPort, cli2Q);
cli2.Start();
CheckTrue("TCP server sees 2 clients", await WaitFor(() => srv.Clients.Count == 2));
int firstId = srv.Clients.OrderBy(c => c.Id).First().Id;
srv.SendTargets = new[] { firstId };
srv.Send(A("only1"));
await Task.Delay(200);
CheckTrue("TCP send to selected client only", Drain(cliQ, false) == "worldonly1" && Drain(cli2Q, false) == "");
srv.SendTargets = null;
cli2.Stop();
CheckTrue("TCP client disconnect detected", await WaitFor(() => srv.Clients.Count == 1));
string? cliFault = null;
cli.Faulted += m => cliFault = m;
srv.Stop();
CheckTrue("TCP client faulted when server stops", await WaitFor(() => cliFault != null));
cli.Stop();
bool refused = false;
try { await TcpClientConnection.ConnectAsync("127.0.0.1", 1, null, 2000); } catch (Exception ex) { refused = NetUtil.Describe(ex).Length > 0; }
CheckTrue("TCP connect refused -> exception", refused);

// UDP
var uaQ = new ConcurrentQueue<DataChunk>();
var ubQ = new ConcurrentQueue<DataChunk>();
var ua = await UdpConnection.OpenAsync(0, null, 0, uaQ);
ua.Start();
var ub = await UdpConnection.OpenAsync(0, "127.0.0.1", ua.LocalPort, ubQ);
ub.Start();
string? uaWarn = null;
ua.Warning += m => uaWarn = m;
ua.Send(A("x"));
CheckTrue("UDP no target -> warning, not sent", await WaitFor(() => uaWarn != null) && !uaQ.Any(c => c.IsTx));
ub.Send(A("ping"));
CheckTrue("UDP b -> a", await WaitFor(() => Drain(uaQ, false) == "ping"));
ua.Send(A("pong"));
CheckTrue("UDP a replies to last sender", await WaitFor(() => Drain(ubQ, false) == "pong"));
ua.Stop();
ub.Stop();

// 串口转发：伪终端（pty）上的 SerialSession ⇄ TCP 服务器
int ptm = -1;
try
{
    ptm = Pty.posix_openpt(2 | 0x100); // O_RDWR | O_NOCTTY
    if (ptm < 0 || Pty.grantpt(ptm) != 0 || Pty.unlockpt(ptm) != 0) throw new Exception("posix_openpt 失败");
    string slave = Marshal.PtrToStringAnsi(Pty.ptsname(ptm))!;
    var sp = new System.IO.Ports.SerialPort(slave, 115200) { ReadTimeout = 100, WriteTimeout = 1000 };
    sp.Open();
    var serQ = new ConcurrentQueue<DataChunk>();
    var ser = new SerialSession(sp, serQ);
    var bnet = TcpServerConnection.Listen(0, null);
    var bridge = new SerialBridge(bnet, d => ser.Send(d));
    ser.Received += bridge.FromLocal;
    ser.Start();
    bridge.Start();
    var masterRx = new ConcurrentQueue<byte>();
    var reader = new Thread(() =>
    {
        var buf = new byte[4096];
        while (true)
        {
            long n = Pty.read(ptm, buf, 4096);
            if (n <= 0) break;
            for (int i = 0; i < n; i++) masterRx.Enqueue(buf[i]);
        }
    }) { IsBackground = true };
    reader.Start();
    var tq = new ConcurrentQueue<DataChunk>();
    var tc = await TcpClientConnection.ConnectAsync("127.0.0.1", bnet.LocalPort, tq);
    tc.Start();
    await WaitFor(() => bnet.Clients.Count == 1);
    tc.Send(A("net2ser"));
    CheckTrue($"bridge: TCP -> serial (pty {slave})", await WaitFor(() => Encoding.ASCII.GetString(masterRx.ToArray()) == "net2ser"));
    var wbytes = A("ser2net");
    Pty.write(ptm, wbytes, wbytes.Length);
    CheckTrue("bridge: serial -> TCP", await WaitFor(() => Drain(tq, false) == "ser2net"));
    CheckTrue("bridge: counters", await WaitFor(() => bridge.LocalToNetBytes == 7 && bridge.NetToLocalBytes == 7));
    CheckTrue("bridge: serial view shows RX + TX echo", Drain(serQ, false) == "ser2net" && await WaitFor(() => Drain(serQ, true) == "net2ser"));
    tc.Stop();
    bridge.Stop();
    ser.Stop();
}
catch (Exception ex)
{
    fail++;
    Console.WriteLine("FAIL  bridge pty test: " + ex.Message);
}
finally
{
    if (ptm >= 0) Pty.close(ptm);
}


// =====================================================================
// v1.4：自定义帧模板
// =====================================================================
{
    var tpl = FrameTemplate.Sample();
    CheckTrue("frame: sample template valid", tpl.Validate() == null);
    var fp = new FrameParser(tpl);
    var r = fp.Push(H("AA 55 05 01 00 FA 01 C2 C3"), t0);
    Check("frame: sample count", "1", r.Count.ToString());
    CheckTrue("frame: sample ok", r.Count == 1 && r[0].Ok);
    Check("frame: sample 命令", "1", r[0].Fields.First(f => f.Name == "命令").Value);
    Check("frame: sample 温度", "25 ℃", r[0].Fields.First(f => f.Name == "温度").Value);
    Check("frame: sample 湿度", "45 %", r[0].Fields.First(f => f.Name == "湿度").Value);

    // 负温度：-12.5 ℃ = 0xFF83
    var neg = H("AA 55 05 02 FF 83 00 64");
    byte s = 0; foreach (var b in neg.Skip(2)) s += b;
    var fNeg = fp.Push(neg.Concat(new[] { s }).ToArray(), t0);
    Check("frame: negative I16 scaled", "-12.5 ℃", fNeg.Single().Fields[1].Value);

    // 校验错误
    var fBad = fp.Push(H("AA 55 05 01 00 FA 01 C2 C4"), t0);
    CheckTrue("frame: bad checksum flagged", fBad.Count == 1 && !fBad[0].Ok && fBad[0].Error!.Contains("校验"));
    Check("frame: bad checksum still decodes fields", "25 ℃", fBad[0].Fields[1].Value);

    // 跨块拆分 + 前导垃圾 + 连续两帧
    var fp2 = new FrameParser(tpl);
    var all = new List<ParsedFrame>();
    all.AddRange(fp2.Push(H("00 11 AA"), t0));
    all.AddRange(fp2.Push(H("55 05 01"), t0.AddMilliseconds(10)));
    all.AddRange(fp2.Push(H("00 FA 01 C2"), t0.AddMilliseconds(20)));
    CheckTrue("frame: split chunks -> nothing yet", all.Count == 0 && fp2.Pending > 0);
    all.AddRange(fp2.Push(H("C3 AA 55 05 01 00 FA 01 C2 C3"), t0.AddMilliseconds(30)));
    CheckTrue("frame: split chunks -> 2 ok frames", all.Count == 2 && all.All(f => f.Ok));
    CheckTrue("frame: skipped garbage counted", fp2.SkippedBytes == 2);
    Check("frame: ok/bad counters", "2/0", $"{fp2.OkCount}/{fp2.BadCount}");

    // 半帧超时丢弃
    var fp3 = new FrameParser(tpl);
    fp3.Push(H("AA 55 05 01"), t0);
    var late = fp3.Push(H("AA 55 05 01 00 FA 01 C2 C3"), t0.AddMilliseconds(5000));
    CheckTrue("frame: partial timeout resyncs", late.Count == 1 && late[0].Ok);

    // 帧尾 + 2 字节小端长度（计入整帧）+ Modbus CRC + u32 LE + float BE
    var t2 = new FrameTemplate
    {
        Name = "t2", Header = "7E", Tail = "0D 0A", HasLength = true, LengthOffset = 1, LengthSize = 2, LengthBigEndian = false,
        LengthCountsHeader = true, LengthCountsLengthField = true, LengthCountsChecksum = true, LengthCountsTail = true,
        Checksum = ChecksumKind.ModbusCrc16, ChecksumFrom = 0,
        Fields = new()
        {
            new FrameField { Name = "计数", Offset = 3, Type = FrameFieldType.U32, BigEndian = false },
            new FrameField { Name = "电压", Offset = 7, Type = FrameFieldType.Float, BigEndian = true, Unit = "V" },
        },
    };
    CheckTrue("frame: t2 valid", t2.Validate() == null);
    byte[] body = H("7E 0F 00 78 56 34 12 3F C0 00 00");
    byte[] f2 = Checksum.Append(ChecksumKind.ModbusCrc16, body).Concat(H("0D 0A")).ToArray();
    var p2 = new FrameParser(t2);
    var o2 = p2.Push(f2, t0);
    CheckTrue("frame: t2 ok", o2.Count == 1 && o2[0].Ok);
    Check("frame: t2 u32 LE", "305419896", o2[0].Fields[0].Value);
    Check("frame: t2 float BE", "1.5 V", o2[0].Fields[1].Value);
    var f2bad = (byte[])f2.Clone(); f2bad[^1] = 0x00;
    var o3 = p2.Push(f2bad, t0);
    CheckTrue("frame: t2 bad tail flagged", o3.Count >= 1 && !o3[0].Ok && o3[0].Error!.Contains("尾"));
    // 非法长度
    var p3 = new FrameParser(t2);
    var o4 = p3.Push(H("7E 02 00 7E 0F 00"), t0);
    CheckTrue("frame: invalid length flagged", o4.Count >= 1 && !o4[0].Ok);

    // 固定长度、无长度字段
    var t3 = new FrameTemplate { Name = "fixed", Header = "5A", HasLength = false, FixedLength = 4, Checksum = ChecksumKind.Xor8, ChecksumFrom = 1,
        Fields = new() { new FrameField { Name = "v", Offset = 1, Type = FrameFieldType.I16, BigEndian = true } } };
    CheckTrue("frame: fixed valid", t3.Validate() == null);
    var o5 = new FrameParser(t3).Push(H("5A FF FE 01"), t0);
    CheckTrue("frame: fixed-length Xor8 ok", o5.Count == 1 && o5[0].Ok && o5[0].Fields[0].Value == "-2");

    // JSON 持久化
    var json = FrameTemplateStore.Serialize(new List<FrameTemplate> { tpl, t2 });
    var back = FrameTemplateStore.Deserialize(json)!;
    CheckTrue("frame: json round trip", back.Count == 2 && back[1].Checksum == ChecksumKind.ModbusCrc16 && back[1].Fields[1].Type == FrameFieldType.Float && json.Contains("\"Float\""));
    var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "st-tpl-" + Guid.NewGuid().ToString("N"));
    System.IO.Directory.CreateDirectory(dir);
    var tp = FrameTemplateStore.PathIn(dir);
    var loaded0 = FrameTemplateStore.Load(tp);
    CheckTrue("frame: missing file -> sample", loaded0.Count == 1 && loaded0[0].Name == tpl.Name);
    FrameTemplateStore.Save(tp, back);
    CheckTrue("frame: save/load", FrameTemplateStore.Load(tp).Count == 2);
    System.IO.Directory.Delete(dir, true);
}

// =====================================================================
// v1.4：Modbus 从站
// =====================================================================
{
    byte[] Rtu(string hex) => Checksum.Append(ChecksumKind.ModbusCrc16, H(hex));
    var model = new ModbusDataModel(100);
    model.Holding[0] = 0x1234; model.Holding[1] = 0x5678; model.Input[0] = 0x0011;
    model.Coils[0] = true; model.Coils[2] = true; model.DiscreteInputs[1] = true;
    var slave = new ModbusSlave(model) { SlaveId = 1 };
    var now = t0;
    string? One(string req)
    {
        now = now.AddMilliseconds(500);
        var r = slave.Push(Rtu(req), now);
        return r.Count == 1 && r[0].Response != null ? Hx(r[0].Response!) : (r.Count == 0 ? "none" : "noresp");
    }
    Check("slave FC01", Hx(Rtu("01 01 01 05")), One("01 01 00 00 00 08"));
    Check("slave FC02", Hx(Rtu("01 02 01 02")), One("01 02 00 00 00 03"));
    Check("slave FC03", Hx(Rtu("01 03 04 12 34 56 78")), One("01 03 00 00 00 02"));
    Check("slave FC04", Hx(Rtu("01 04 02 00 11")), One("01 04 00 00 00 01"));
    Check("slave FC05 echo", Hx(Rtu("01 05 00 05 FF 00")), One("01 05 00 05 FF 00"));
    CheckTrue("slave FC05 wrote coil", model.Coils[5]);
    Check("slave FC05 off", Hx(Rtu("01 05 00 05 00 00")), One("01 05 00 05 00 00"));
    CheckTrue("slave FC05 cleared coil", !model.Coils[5]);
    Check("slave FC06 echo", Hx(Rtu("01 06 00 0A AB CD")), One("01 06 00 0A AB CD"));
    CheckTrue("slave FC06 wrote", model.Holding[10] == 0xABCD);
    Check("slave FC0F", Hx(Rtu("01 0F 00 14 00 0A")), One("01 0F 00 14 00 0A 02 CD 01"));
    CheckTrue("slave FC0F coils", string.Concat(model.Coils.Skip(20).Take(10).Select(c => c ? '1' : '0')) == "1011001110");
    Check("slave FC10", Hx(Rtu("01 10 00 1E 00 02")), One("01 10 00 1E 00 02 04 00 0A 01 02"));
    CheckTrue("slave FC10 regs", model.Holding[30] == 10 && model.Holding[31] == 0x0102);
    // 异常
    Check("slave exc 02 (FC03 out of range)", Hx(Rtu("01 83 02")), One("01 03 00 63 00 02"));
    Check("slave exc 02 (FC01 out of range)", Hx(Rtu("01 81 02")), One("01 01 00 64 00 01"));
    Check("slave exc 02 (FC06 out of range)", Hx(Rtu("01 86 02")), One("01 06 00 64 00 01"));
    Check("slave exc 02 (FC10 out of range)", Hx(Rtu("01 90 02")), One("01 10 00 63 00 02 04 00 01 00 02"));
    Check("slave exc 02 (FC0F out of range)", Hx(Rtu("01 8F 02")), One("01 0F 00 62 00 03 01 07"));
    Check("slave exc 03 (qty 0)", Hx(Rtu("01 83 03")), One("01 03 00 00 00 00"));
    Check("slave exc 03 (FC05 bad value)", Hx(Rtu("01 85 03")), One("01 05 00 00 12 34"));
    Check("slave exc 03 (FC10 byte count)", Hx(Rtu("01 90 03")), One("01 10 00 00 00 02 02 00 01"));
    Check("slave exc 01 (FC07)", Hx(Rtu("01 87 01")), One("01 07"));
    CheckTrue("slave exception counter", slave.Exceptions == 9);
    // 其他站号忽略、广播不应答
    Check("slave other id ignored", "none", One("02 03 00 00 00 01"));
    Check("slave broadcast no response", "noresp", One("00 06 00 0B 00 07"));
    CheckTrue("slave broadcast executed", model.Holding[11] == 7);
    // CRC 错误 + 拆分
    now = now.AddMilliseconds(500);
    var badCrc = Rtu("01 03 00 00 00 01"); badCrc[^1] ^= 0xFF;
    CheckTrue("slave bad crc no response", slave.Push(badCrc, now).All(x => x.Response == null) && slave.CrcErrors > 0);
    now = now.AddMilliseconds(500);
    var req = Rtu("01 03 00 01 00 01");
    var rA = slave.Push(req.Take(3).ToArray(), now);
    var rB = slave.Push(req.Skip(3).ToArray(), now.AddMilliseconds(5));
    CheckTrue("slave split request", rA.Count == 0 && rB.Count == 1 && Hx(rB[0].Response!) == Hx(Rtu("01 03 02 56 78")));
    // Modbus TCP
    var tcp = new ModbusSlave(model) { SlaveId = 1, Framing = ModbusFraming.Tcp };
    var tr = tcp.Push(H("00 07 00 00 00 06 01 03 00 00 00 01"), t0);
    Check("slave TCP FC03", "00 07 00 00 00 05 01 03 02 12 34", tr.Count == 1 ? Hx(tr[0].Response!) : "none");
    var te = tcp.Push(H("00 08 00 00 00 06 01 04 00 63 00 05"), t0);
    Check("slave TCP exception", "00 08 00 00 00 03 01 84 02", te.Count == 1 ? Hx(te[0].Response!) : "none");
    CheckTrue("slave log written area", slave.Push(Rtu("01 06 00 02 00 09"), now.AddSeconds(1)).Single().Log.WrittenArea == 'H');
}

// =====================================================================
// v1.4：XModem / YModem 回环
// =====================================================================
{
    var rnd = new Random(1234);
    byte[] Rand(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }
    (List<TransferFile>? got, string? sendErr, string? recvErr, int retries) Loop(XModemVariant v, List<TransferFile> files, Func<byte[], byte[]>? corrupt = null)
    {
        var (a, b) = MemoryLink.CreatePair(corrupt, null);
        var tx = new XYModem(a) { AckTimeoutMs = 3000, StartTimeoutMs = 10000 };
        var rx = new XYModem(b) { BlockTimeoutMs = 3000, StartIntervalMs = 500, StartTimeoutMs = 10000 };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var st = tx.SendAsync(files, v, cts.Token);
        var rt = rx.ReceiveAsync(v, cts.Token);
        string? se = null, re = null; List<TransferFile>? got = null;
        try { st.Wait(); } catch (AggregateException e) { se = e.InnerException!.Message; }
        try { got = rt.Result; } catch (AggregateException e) { re = e.InnerException!.Message; }
        return (got, se, re, tx.TotalRetries + rx.TotalRetries);
    }
    var d1 = Rand(1000);
    var x1 = Loop(XModemVariant.XModemChecksum, new() { new TransferFile { Name = "a.bin", Data = d1 } });
    CheckTrue("xmodem checksum loopback", x1.sendErr == null && x1.recvErr == null && x1.got![0].Data.SequenceEqual(d1));
    var d2 = Rand(5000).Select(b => b == 0x1A ? (byte)0x1B : b).ToArray(); // XModem 去掉末尾 0x1A 填充
    var x2 = Loop(XModemVariant.XModemCrc, new() { new TransferFile { Name = "b.bin", Data = d2 } });
    CheckTrue("xmodem crc loopback", x2.sendErr == null && x2.recvErr == null && x2.got![0].Data.SequenceEqual(d2));
    var x3 = Loop(XModemVariant.XModem1K, new() { new TransferFile { Name = "c.bin", Data = d2 } });
    CheckTrue("xmodem-1k loopback", x3.sendErr == null && x3.recvErr == null && x3.got![0].Data.SequenceEqual(d2));

    // 损坏一个数据块 → NAK → 重发成功
    int writes = 0;
    byte[] CorruptThird(byte[] d)
    {
        if (d.Length > 100 && ++writes == 3) { var c = (byte[])d.Clone(); c[50] ^= 0x55; return c; }
        return d;
    }
    var x4 = Loop(XModemVariant.XModemCrc, new() { new TransferFile { Name = "d.bin", Data = d2 } }, CorruptThird);
    CheckTrue("xmodem corrupted block retried", x4.sendErr == null && x4.recvErr == null && x4.got![0].Data.SequenceEqual(d2) && x4.retries >= 1);
    writes = 0;
    var x4b = Loop(XModemVariant.XModemChecksum, new() { new TransferFile { Name = "d.bin", Data = d1 } }, CorruptThird);
    CheckTrue("xmodem checksum corrupted block retried", x4b.sendErr == null && x4b.got![0].Data.SequenceEqual(d1) && x4b.retries >= 1);

    // YModem 批量：含 1K 块、短文件、空文件，且其中一块损坏
    var y1 = Rand(3000); var y2 = Rand(100); var y3 = Array.Empty<byte>();
    writes = 0;
    var ys = Loop(XModemVariant.YModem, new()
    {
        new TransferFile { Name = "固件.bin", Data = y1 },
        new TransferFile { Name = "cfg.txt", Data = y2 },
        new TransferFile { Name = "empty.dat", Data = y3 },
    }, CorruptThird);
    CheckTrue("ymodem batch no errors", ys.sendErr == null && ys.recvErr == null);
    CheckTrue("ymodem batch 3 files", ys.got != null && ys.got.Count == 3);
    CheckTrue("ymodem names", ys.got != null && ys.got.Select(f => f.Name).SequenceEqual(new[] { "固件.bin", "cfg.txt", "empty.dat" }));
    CheckTrue("ymodem exact sizes (padding trimmed)", ys.got != null && ys.got[0].Data.SequenceEqual(y1) && ys.got[1].Data.SequenceEqual(y2) && ys.got[2].Data.Length == 0);
    CheckTrue("ymodem corrupted block retried", ys.retries >= 1);

    // 块构造
    var blk = XYModem.BuildBlock(1, new byte[128], true);
    CheckTrue("xmodem block layout", blk.Length == 133 && blk[0] == XYModem.SOH && blk[1] == 1 && blk[2] == 0xFE);
    CheckTrue("xmodem 1k block STX", XYModem.BuildBlock(2, new byte[1024], true)[0] == XYModem.STX);

    // 取消：接收方未启动时发送方取消
    {
        var (a, b) = MemoryLink.CreatePair();
        var tx = new XYModem(a);
        using var cts = new CancellationTokenSource(300);
        bool cancelled = false;
        try { tx.Send(new List<TransferFile> { new() { Name = "x", Data = d1 } }, XModemVariant.XModemCrc, cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        var sent = new List<int>(); int bb; while ((bb = b.ReadByte(50, CancellationToken.None)) >= 0) sent.Add(bb);
        CheckTrue("xmodem cancel sends CAN", cancelled && sent.Count(x => x == XYModem.CAN) >= 2);
    }
    // 接收方收到 CAN
    {
        var (a, b) = MemoryLink.CreatePair();
        var rx = new XYModem(b) { StartIntervalMs = 200, StartTimeoutMs = 2000 };
        a.Write(new byte[] { XYModem.CAN, XYModem.CAN });
        string? msg = null;
        try { rx.Receive(XModemVariant.XModemCrc, CancellationToken.None); } catch (TransferException e) { msg = e.Message; }
        CheckTrue("xmodem receiver handles CAN", msg != null && msg.Contains("取消"));
    }
}

// XYModem + 从站经真实 TCP 连接（与主窗口相同的接线：Received → FeedLink / ModbusSlave，发送走 IConnection.Send）
{
    var srvT = TcpServerConnection.Listen(0, null);
    srvT.Start();
    var cliT = await TcpClientConnection.ConnectAsync("127.0.0.1", srvT.LocalPort, null);
    cliT.Start();
    await WaitFor(() => srvT.Clients.Count == 1, 3000);
    var linkS = new FeedLink(d => srvT.Send(d));
    var linkC = new FeedLink(d => cliT.Send(d));
    srvT.Received += linkS.Feed;
    cliT.Received += linkC.Feed;
    var ymPayload = new byte[20000]; new Random(7).NextBytes(ymPayload);
    var sendT = new XYModem(linkS).SendAsync(new List<TransferFile> { new() { Name = "fw.bin", Data = ymPayload } }, XModemVariant.YModem, CancellationToken.None);
    var recvT = new XYModem(linkC) { StartIntervalMs = 500 }.ReceiveAsync(XModemVariant.YModem, CancellationToken.None);
    bool fin = Task.WaitAll(new Task[] { sendT, recvT }, 20000);
    CheckTrue("ymodem over TCP connection", fin && recvT.Result.Count == 1 && recvT.Result[0].Name == "fw.bin" && recvT.Result[0].Data.SequenceEqual(ymPayload));
    srvT.Received -= linkS.Feed;
    cliT.Received -= linkC.Feed;

    // Modbus TCP 从站挂在服务器端
    var mdl = new ModbusDataModel(10); mdl.Holding[3] = 0xBEEF;
    var sl = new ModbusSlave(mdl) { SlaveId = 1, Framing = ModbusFraming.Tcp };
    srvT.Received += d => { foreach (var (resp, _) in sl.Push(d, DateTime.Now)) if (resp != null) srvT.Send(resp); };
    var gotResp = new List<byte>();
    cliT.Received += d => { lock (gotResp) gotResp.AddRange(d); };
    cliT.Send(H("12 34 00 00 00 06 01 03 00 03 00 01"));
    await WaitFor(() => { lock (gotResp) return gotResp.Count >= 11; }, 3000);
    lock (gotResp) Check("slave over TCP connection", "12 34 00 00 00 05 01 03 02 BE EF", Hx(gotResp.ToArray()));
    cliT.Stop();
    srvT.Stop();
}

// =====================================================================
// v1.4：录制 / 回放
// =====================================================================
{
    var sw = new System.IO.StringWriter();
    var rec = new SessionRecorder(sw, "test");
    rec.Write(false, H("01 02 03"), t0);
    rec.Write(true, A("AT\r\n"), t0.AddMilliseconds(100), "192.168.1.5:502");
    rec.Write(false, A("OK\r\n"), t0.AddMilliseconds(200));
    rec.Write(false, Array.Empty<byte>(), t0.AddMilliseconds(250)); // 空数据忽略
    rec.Flush();
    string text = sw.ToString();
    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    CheckTrue("record: header + 3 lines", lines.Length == 4 && lines[0].Contains(SessionRecorder.HeaderType));
    var entries = SessionReader.Read(new System.IO.StringReader(text + "garbage line\n{\"t\":5,\"dir\":\"rx\",\"hex\":\"ZZ\"}\n"), out int badLines);
    CheckTrue("record: read back 3 entries", entries.Count == 3);
    Check("record: bad lines counted", "2", badLines.ToString());
    CheckTrue("record: round trip data", entries[0].Data.SequenceEqual(H("01 02 03")) && entries[1].IsTx && !entries[2].IsTx
        && Encoding.ASCII.GetString(entries[2].Data) == "OK\r\n" && entries[1].Source == "192.168.1.5:502");
    CheckTrue("record: offsets", Math.Abs(entries[1].OffsetMs - 100) < 0.01 && Math.Abs(entries[2].OffsetMs - 200) < 0.01);

    var outList = new List<RecordEntry>();
    var rp = new SessionReplayer(entries) { Speed = 0 };
    var swt = System.Diagnostics.Stopwatch.StartNew();
    rp.Run(e => outList.Add(e), null, CancellationToken.None);
    CheckTrue("replay: max speed delivers all quickly", outList.Count == 3 && swt.ElapsedMilliseconds < 100);
    CheckTrue("replay: payload identical", outList.Select(e => e.Hex).SequenceEqual(entries.Select(e => e.Hex)));

    outList.Clear();
    var rp1 = new SessionReplayer(entries) { Speed = 1 };
    swt.Restart();
    rp1.Run(e => outList.Add(e), null, CancellationToken.None);
    long el1 = swt.ElapsedMilliseconds;
    CheckTrue($"replay: 1x keeps timing ({el1} ms)", el1 >= 190 && el1 < 600);

    var rp2 = new SessionReplayer(entries) { Speed = 2 };
    swt.Restart();
    rp2.Run(_ => { }, null, CancellationToken.None);
    long el2 = swt.ElapsedMilliseconds;
    CheckTrue($"replay: 2x halves timing ({el2} ms)", el2 >= 90 && el2 < 400);

    var rph = new SessionReplayer(entries) { Speed = 0.5 };
    swt.Restart();
    rph.Run(_ => { }, null, CancellationToken.None);
    long elh = swt.ElapsedMilliseconds;
    CheckTrue($"replay: 0.5x doubles timing ({elh} ms)", elh >= 390 && elh < 900);

    // 过滤（仅 RX）+ 暂停/继续 + 停止
    var rxOnly = new List<RecordEntry>();
    var rpp = new SessionReplayer(entries) { Speed = 1, Filter = e => !e.IsTx };
    rpp.Pause();
    swt.Restart();
    var task = rpp.RunAsync(e => { lock (rxOnly) rxOnly.Add(e); }, null, CancellationToken.None);
    Thread.Sleep(300);
    int duringPause; lock (rxOnly) duringPause = rxOnly.Count;
    rpp.Resume();
    task.Wait();
    CheckTrue("replay: paused delivers nothing", duringPause == 0);
    CheckTrue("replay: filter rx only", rxOnly.Count == 2 && rxOnly.All(e => !e.IsTx));
    CheckTrue("replay: pause extends time", swt.ElapsedMilliseconds >= 480);

    using var stop = new CancellationTokenSource(50);
    bool stopped = false;
    try { new SessionReplayer(entries) { Speed = 1 }.Run(_ => { }, null, stop.Token); } catch (OperationCanceledException) { stopped = true; }
    CheckTrue("replay: stop cancels", stopped);

    var fpath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "st-rec-" + Guid.NewGuid().ToString("N") + ".jsonl");
    using (var fr = SessionRecorder.Create(fpath)) { fr.Write(false, Encoding.UTF8.GetBytes("中文 hi"), t0); }
    var fe = SessionReader.ReadFile(fpath, out _);
    CheckTrue("record: file round trip", fe.Count == 1 && Encoding.UTF8.GetString(fe[0].Data) == "中文 hi");
    System.IO.File.Delete(fpath);
}

// =====================================================================
// v1.4：波特率识别
// =====================================================================
{
    var good = BaudScorer.Score(115200, A("Hello from device v1.2\r\nTemp=25.3 Hum=45\r\nOK\r\n"));
    var garbage = BaudScorer.Score(9600, H("00 FF 00 F8 80 00 FF FE 00 00 E0 FF 00 80 F0 00 FF 00"));
    var binary = BaudScorer.Score(19200, H("8C 13 91 E7 05 B2 7F 03 99 C1 11 A4 06 D8 3E 02"));
    var cn = BaudScorer.Score(57600, Encoding.UTF8.GetBytes("设备启动完成，版本 1.0\r\n"));
    CheckTrue($"baud: text scores high ({good.Score})", good.Score >= 80);
    CheckTrue($"baud: 00/FF garbage scores low ({garbage.Score})", garbage.Score < 20);
    CheckTrue($"baud: random binary scores low ({binary.Score})", binary.Score < 40);
    CheckTrue($"baud: UTF-8 Chinese text acceptable ({cn.Score})", cn.Score >= 60);
    CheckTrue("baud: empty scores 0", BaudScorer.Score(1200, Array.Empty<byte>()).Score == 0);
    CheckTrue("baud: tiny sample discounted", BaudScorer.Score(1200, A("ok")).Score < good.Score);
    CheckTrue("baud: 12 standard rates 1200..921600", BaudScorer.StandardRates.Length == 12 && BaudScorer.StandardRates[0] == 1200 && BaudScorer.StandardRates[^1] == 921600);

    var probeSeen = new List<int>();
    var results = BaudDetector.RunAsync((baud, ct) =>
    {
        probeSeen.Add(baud);
        if (baud == 38400) throw new InvalidOperationException("busy");
        byte[] d = baud == 115200 ? A("AT\r\nOK\r\n+VER:1.0\r\n") : baud == 57600 ? H("41 00 FF 4B 0A") : H("00 FF 80 00 FE FF 00 F0");
        return Task.FromResult(d);
    }, BaudScorer.StandardRates, null, CancellationToken.None).Result;
    CheckTrue("baud: detector tried all", probeSeen.Count == 12 && results.Count == 12);
    Check("baud: best rate", "115200", BaudScorer.Best(results)?.Baud.ToString() ?? "none");
    CheckTrue("baud: sampler error reported", results.First(r => r.Baud == 38400).Note.Contains("失败"));
    CheckTrue("baud: no good rate -> null", BaudScorer.Best(results.Where(r => r.Baud != 115200)) == null);
}


// =====================================================================
// v1.5：多会话标签页 —— 设置迁移与会话隔离
// =====================================================================
{
    Console.WriteLine("\n== v1.5 会话 ==");
    var legacyJson = "{\"ConnType\":\"TcpClient\",\"PortName\":\"COM7\",\"BaudRate\":9600,\"RemoteHost\":\"10.0.0.5\",\"RemotePort\":4001,\"HexSend\":true,\"Checksum\":\"ModbusCrc16\",\"SendText\":\"01 03 00 00 00 01\",\"ScriptText\":\"send AT\"}";
    var legacy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(legacyJson)!;
    legacy.Sessions = null!;
    bool mig = AppSettings.MigrateSessions(legacy);
    CheckTrue("migrate v1.4 -> 1 session", mig && legacy.Sessions.Count == 1);
    var ms0 = legacy.Sessions[0];
    CheckTrue("migrated tab 1 keeps connection", ms0.Name == "会话 1" && ms0.ConnType == "TcpClient" && ms0.PortName == "COM7" && ms0.BaudRate == 9600 && ms0.RemoteHost == "10.0.0.5" && ms0.RemotePort == 4001);
    CheckTrue("migrated tab 1 keeps send area", ms0.HexSend && ms0.Checksum == "ModbusCrc16" && ms0.SendText == "01 03 00 00 00 01" && ms0.ScriptText == "send AT");
    CheckTrue("migrate is idempotent", !AppSettings.MigrateSessions(legacy) && legacy.Sessions.Count == 1);
    legacy.Sessions.Add(new SessionSettings { Id = ms0.Id, Name = "" });
    legacy.ActiveSessionIndex = 9;
    AppSettings.MigrateSessions(legacy);
    CheckTrue("duplicate id fixed, empty name filled, index clamped", legacy.Sessions[1].Id != ms0.Id && legacy.Sessions[1].Name == "会话 2" && legacy.ActiveSessionIndex == 0);
    var rt = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(legacy))!;
    CheckTrue("session list round-trips JSON", rt.Sessions.Count == 2 && rt.Sessions[0].RemotePort == 4001 && rt.Sessions[1].Id == legacy.Sessions[1].Id);

    // 两个会话：一个 TCP 服务器、一个 TCP 客户端，各自的设置 / 缓冲 / 计数器互不影响
    var sa = new SessionCore(new SessionSettings { Name = "A", ConnType = "TcpServer", LocalPort = 0, LineEnding = "\r\n" });
    var connA = TcpServerConnection.Listen(0, sa.Output);
    sa.Attach(connA);
    var sb2 = new SessionCore(new SessionSettings { Name = "B", ConnType = "TcpClient", RemoteHost = "127.0.0.1", RemotePort = connA.LocalPort, HexSend = true, Checksum = "ModbusCrc16" });
    await sb2.OpenAsync();
    CheckTrue("session B connected", sb2.IsOpen && await WaitFor(() => connA.Clients.Count == 1));
    var rxA = new RxCollector(sa.Channel);
    var rxB = new RxCollector(sb2.Channel);
    CheckTrue("session A sends text + its line ending", sa.Send("hello", false, out _));
    CheckTrue("B receives A's text", await WaitFor(() => Encoding.ASCII.GetString(rxB.Snapshot()) == "hello\r\n"));
    CheckTrue("session B sends hex + its CRC", sb2.Send("01 03 00 00 00 01", true, out _));
    CheckTrue("A receives B's frame with CRC", await WaitFor(() => Hx(rxA.Snapshot()) == "01 03 00 00 00 01 84 0A"));
    CheckTrue("settings independent", sa.Settings.LineEnding == "\r\n" && sb2.Settings.LineEnding == "" && !sa.Settings.HexSend && sb2.Settings.HexSend);
    CheckTrue("counters independent", sa.TxBytes == 7 && sa.RxBytes == 8 && sb2.TxBytes == 8 && sb2.RxBytes == 7);
    CheckTrue("output queues independent", sa.Output.Where(c => !c.IsTx).Sum(c => c.Data.Length) == 8 && sb2.Output.Where(c => !c.IsTx).Sum(c => c.Data.Length) == 7);
    rxA.Clear();
    CheckTrue("clearing A's buffer leaves B's", rxA.Count == 0 && rxB.Count == 7);
    CheckTrue("channel names", sa.Channel.Name == "A" && sb2.Channel.ToString() == "B");
    await sb2.CloseAsync();
    CheckTrue("B closed, A still open", !sb2.IsOpen && sa.IsOpen && !sb2.Channel.IsOpen && sa.Channel.IsOpen);
    CheckTrue("closed session send fails", !sb2.Send("x", false, out var sbErr) && sbErr != null);
    rxA.Dispose(); rxB.Dispose();
    await sa.CloseAsync();
}

// =====================================================================
// v1.5：批量测试
// =====================================================================
{
    Console.WriteLine("\n== v1.5 批量测试 ==");
    var dev = new FakeChannel("模拟设备");
    int atCount = 0;
    var mdl15 = new ModbusDataModel(10); mdl15.Holding[0] = 0x0102; mdl15.Holding[1] = 0x0304;
    var slave15 = new ModbusSlave(mdl15) { SlaveId = 1 };
    dev.Responder = d =>
    {
        var t = Encoding.ASCII.GetString(d);
        if (t == "AT\r\n") return A("\r\nOK\r\n");
        if (t == "AT+FLAKY\r\n") return ++atCount >= 3 ? A("OK\r\n") : A("ERROR\r\n");
        if (t == "AT+VER\r\n") return A("+VER: 1.5.0\r\n");
        if (t == "AT+SILENT\r\n") return null;
        if (d.Length > 0 && d[0] == 1) return slave15.Push(d, DateTime.Now).Select(x => x.Response).FirstOrDefault(x => x != null);
        if (d.Length > 0 && d[0] == 2) return H("02 83 02 C0 F1");
        return null;
    };
    var suite = new TestSuite
    {
        Name = "单元测试",
        Steps =
        {
            new TestStep { Name = "AT", Send = "AT\\r\\n", Expect = ExpectMode.Contains, ExpectValue = "OK" },
            new TestStep { Name = "版本", Send = "AT+VER\\r\\n", Expect = ExpectMode.Regex, ExpectValue = @"\d+\.\d+\.\d+" },
            new TestStep { Name = "不稳定", Send = "AT+FLAKY\\r\\n", Expect = ExpectMode.Contains, ExpectValue = "OK", Retries = 3, TimeoutMs = 150 },
            new TestStep { Name = "无应答", Send = "AT+SILENT\\r\\n", Expect = ExpectMode.Contains, ExpectValue = "OK", TimeoutMs = 200 },
            new TestStep { Name = "读寄存器", Send = "01 03 00 00 00 02", SendHex = true, Checksum = "ModbusCrc16", Expect = ExpectMode.ModbusValid, TimeoutMs = 500 },
            new TestStep { Name = "精确", Send = "01 06 00 01 12 34", SendHex = true, Checksum = "ModbusCrc16", Expect = ExpectMode.ExactHex, ExpectValue = "01 06 00 01 12 34 ?? ??" },
            new TestStep { Name = "异常应答", Send = "02 03 00 00 00 01", SendHex = true, Checksum = "ModbusCrc16", Expect = ExpectMode.ModbusValid, TimeoutMs = 300 },
            new TestStep { Name = "禁用", Enabled = false, Send = "AT\\r\\n" },
            new TestStep { Name = "只发", Send = "AT\\r\\n", Expect = ExpectMode.None },
        },
    };
    var runner = new TestRunner(dev);
    var started = new List<int>();
    var finished = new List<TestStepResult>();
    runner.StepStarted += (l, i) => started.Add(i);
    runner.StepFinished += r => finished.Add(r);
    var sw15 = System.Diagnostics.Stopwatch.StartNew();
    var rep = await runner.RunAsync(suite, 2, CancellationToken.None);
    sw15.Stop();
    var tr1 = rep.Results.Where(r => r.Loop == 1).ToList();
    CheckTrue("runner ran 2 loops x 9 steps", rep.Results.Count == 18 && started.Count == 18 && finished.Count == 18);
    CheckTrue("contains -> pass", tr1[0].Verdict == TestVerdict.Pass);
    CheckTrue("regex -> pass", tr1[1].Verdict == TestVerdict.Pass && tr1[1].Message.Contains("1.5.0"));
    CheckTrue("retries -> pass on 3rd attempt", tr1[2].Verdict == TestVerdict.Pass && tr1[2].Attempts == 3);
    CheckTrue("timeout -> fail", tr1[3].Verdict == TestVerdict.Fail && tr1[3].Message.Contains("超时") && tr1[3].DurationMs >= 180);
    CheckTrue("modbus valid -> pass", tr1[4].Verdict == TestVerdict.Pass && tr1[4].ReceivedHex.StartsWith("01 03 04 01 02 03 04"));
    CheckTrue("exact hex with wildcard -> pass", tr1[5].Verdict == TestVerdict.Pass);
    CheckTrue("modbus exception -> fail", tr1[6].Verdict == TestVerdict.Fail && tr1[6].Message.Contains("异常"));
    CheckTrue("disabled -> skipped", tr1[7].Verdict == TestVerdict.Skipped);
    CheckTrue("expect none -> pass", tr1[8].Verdict == TestVerdict.Pass);
    CheckTrue("sent hex recorded with CRC", tr1[4].SentHex == "01 03 00 00 00 02 C4 0B");
    Check("report counts (2 fails x 2 loops)", "12/4/2", $"{rep.Passed}/{rep.Failed}/{rep.Skipped}");
    Check("pass rate", "75.0", rep.PassRate.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
    var csv15 = rep.ToCsv();
    CheckTrue("CSV header + rows + summary", csv15.StartsWith("循环,步骤,名称,开始时间,耗时(ms),发送HEX") && csv15.Split('\n').Count(l => l.StartsWith("1,") || l.StartsWith("2,")) == 18 && csv15.Contains("通过率,75.0%"));
    CheckTrue("CSV escapes quotes/commas", csv15.Contains("\"+VER: 1.5.0\\r\\n\"") || csv15.Contains("+VER: 1.5.0\\r\\n"));
    var html = rep.ToHtml();
    CheckTrue("HTML report has rows colored + rate", html.Contains("<html") && html.Contains("class=\"pass\"") && html.Contains("class=\"fail\"") && html.Contains("75.0%") && html.Contains("单元测试"));
    CheckTrue("HTML encodes content", !html.Contains("<b>+VER") && TestSuite.Sample().Steps.Count >= 4);

    // 失败即中止（独立通道，避免上一步异步应答迟到干扰）
    var stopDev = new FakeChannel("中止测试") { DelayMs = 0, Responder = _ => null };
    var stopSuite = new TestSuite { Steps = { new TestStep { Send = "AT+SILENT\r\n", ExpectValue = "OK", TimeoutMs = 80, ContinueOnFail = false }, new TestStep { Send = "AT\r\n", ExpectValue = "OK" } } };
    var rep2 = new TestRunner(stopDev).Run(stopSuite, 3, CancellationToken.None);
    CheckTrue("continue-on-fail=false aborts run", rep2.Aborted && rep2.Results.Count == 1 && rep2.Results[0].Verdict == TestVerdict.Fail);
    // 取消
    var cts15 = new CancellationTokenSource(150);
    var rep3 = new TestRunner(dev).Run(new TestSuite { Steps = { new TestStep { Send = "AT+SILENT\\r\\n", ExpectValue = "OK", TimeoutMs = 5000 } } }, 1, cts15.Token);
    CheckTrue("cancel stops a waiting step", rep3.Cancelled);
    // 未连接
    var closed = new FakeChannel("断开") { Open = false };
    var rep4 = new TestRunner(closed).Run(new TestSuite { Steps = { new TestStep { Send = "AT", ExpectValue = "OK" } } }, 1, CancellationToken.None);
    CheckTrue("closed session -> error verdict", rep4.Results[0].Verdict == TestVerdict.Error && rep4.Results[0].Message.Contains("未连接"));
    // JSON
    var js = TestSuite.Sample().ToJson();
    var back = TestSuite.FromJson(js);
    CheckTrue("suite JSON round trip (enum as string)", js.Contains("\"ModbusValid\"") && back.Steps.Count == TestSuite.Sample().Steps.Count && back.Steps[3].Expect == ExpectMode.ModbusValid && back.Steps[3].Checksum == "ModbusCrc16");
    CheckTrue("wildcard hex parser", TestRunner.TryParseWildHex("01 ?? xx 0A", out var wh) && wh.Length == 4 && wh[1] == null && wh[2] == null && wh[3] == 0x0A && !TestRunner.TryParseWildHex("0G", out _));
    CheckTrue("exact hex mismatch fails fast", TestRunner.Evaluate(new TestStep { Expect = ExpectMode.ExactHex }, null, new byte?[] { 1, 2 }, Array.Empty<byte>(), H("01 03"), Encoding.ASCII, false).Item1 == TestRunner.EvalState.Fail);
}

// =====================================================================
// v1.5：Modbus 网关
// =====================================================================
{
    Console.WriteLine("\n== v1.5 Modbus 网关 ==");
    var mbap = H("00 07 00 00 00 06 11 03 00 6B 00 03");
    var rtu15 = ModbusGatewayCodec.MbapToRtu(mbap);
    Check("MBAP -> RTU + CRC", "11 03 00 6B 00 03 76 87", Hx(rtu15));
    Check("MBAP -> RTU unit override", "05 03 00 6B 00 03", Hx(ModbusGatewayCodec.MbapToRtu(mbap, 5)).Substring(0, 17));
    CheckTrue("RTU -> PDU (CRC ok)", ModbusGatewayCodec.TryRtuToPdu(ModbusGatewayCodec.BuildRtu(1, H("03 02 BE EF")), out var u15, out var pdu15) && u15 == 1 && Hx(pdu15) == "03 02 BE EF");
    CheckTrue("RTU bad CRC rejected", !ModbusGatewayCodec.TryRtuToPdu(H("01 03 02 BE EF 00 00"), out _, out _));
    Check("build MBAP keeps tid", "AB CD 00 00 00 05 11 03 02 00 01", Hx(ModbusGatewayCodec.BuildMbap(0xABCD, 0x11, H("03 02 00 01"))));
    CheckTrue("parse MBAP complete/partial/bad", ModbusGatewayCodec.TryParseMbap(mbap, out int mfl) == 1 && mfl == 12 && ModbusGatewayCodec.TryParseMbap(mbap.Take(9).ToArray(), out _) == 0 && ModbusGatewayCodec.TryParseMbap(H("00 01 00 05 00 06 01 03 00 00"), out _) == -1);
    CheckTrue("RTU response length", ModbusGatewayCodec.RtuResponseLength(H("01 03 04")) == 9 && ModbusGatewayCodec.RtuResponseLength(H("01 83")) == 5 && ModbusGatewayCodec.RtuResponseLength(H("01 10")) == 8 && ModbusGatewayCodec.RtuResponseLength(H("01")) == 0);
    CheckTrue("RTU request length", ModbusGatewayCodec.RtuRequestLength(H("01 03")) == 8 && ModbusGatewayCodec.RtuRequestLength(H("01 10 00 00 00 02 04")) == 13 && ModbusGatewayCodec.RtuRequestLength(H("01 10 00")) == 0);

    // 网关 + 模拟 RTU 从站（v1.4 ModbusSlave）
    var gm = new ModbusDataModel(200); gm.Holding[0x6B] = 0x022B; gm.Holding[0x6C] = 0x0000; gm.Holding[0x6D] = 0x0064;
    var gslave = new ModbusSlave(gm) { SlaveId = 0x11 };
    var bus = new FakeChannel("RTU 总线") { DelayMs = 5 };
    bool busSilent = false;
    bus.Responder = d => busSilent ? null : gslave.Push(d, DateTime.Now).Select(x => x.Response).FirstOrDefault(x => x != null);
    using var transport = new ChannelRtuTransport(bus);
    await using var gw = new ModbusTcpToRtuGateway(transport) { TimeoutMs = 300 };
    var glog = new ConcurrentQueue<GatewayLogEntry>();
    gw.Log += e => glog.Enqueue(e);
    var direct = await gw.ProcessAsync(mbap);
    Check("gateway direct process", "00 07 00 00 00 09 11 03 06 02 2B 00 00 00 64", Hx(direct!));
    busSilent = true;
    var tmo = await gw.ProcessAsync(H("12 34 00 00 00 06 11 03 00 00 00 01"));
    Check("gateway timeout -> exception 0B, tid kept", "12 34 00 00 00 03 11 83 0B", Hx(tmo!));
    busSilent = false;
    CheckTrue("gateway stats + log", gw.Stats.Requests == 2 && gw.Stats.Responses == 1 && gw.Stats.Timeouts == 1 && glog.Count == 2 && glog.Any(e => e.IsError && e.Result.Contains("0B")));

    gw.Start(0, System.Net.IPAddress.Loopback);
    CheckTrue("gateway listening", gw.IsRunning && gw.Port > 0);
    // 两个 TCP 客户端并发请求：事务号各自对应，总线串行
    async Task<byte[]> MbClient(ushort tidBase, int n, ushort reg)
    {
        using var tc = new System.Net.Sockets.TcpClient();
        await tc.ConnectAsync(System.Net.IPAddress.Loopback, gw.Port);
        var ns = tc.GetStream();
        var all = new List<byte>();
        for (int i = 0; i < n; i++)
        {
            ushort tid = (ushort)(tidBase + i);
            var req = ModbusGatewayCodec.BuildMbap(tid, 0x11, new byte[] { 3, (byte)(reg >> 8), (byte)reg, 0, 1 });
            await ns.WriteAsync(req);
            var buf = new byte[11];
            int got = 0;
            using var rc = new CancellationTokenSource(3000);
            while (got < 6 || got < 6 + ((buf[4] << 8) | buf[5])) { int k = await ns.ReadAsync(buf.AsMemory(got, (got < 6 ? 6 : 6 + ((buf[4] << 8) | buf[5])) - got), rc.Token); if (k <= 0) break; got += k; }
            all.AddRange(buf.Take(got));
        }
        return all.ToArray();
    }
    var ta = MbClient(0x1000, 5, 0x6B);
    var tb = MbClient(0x2000, 5, 0x6D);
    await Task.WhenAll(ta, tb);
    bool tidsOk = true;
    for (int i = 0; i < 5; i++)
    {
        var fa = ta.Result.Skip(i * 11).Take(11).ToArray();
        var fb = tb.Result.Skip(i * 11).Take(11).ToArray();
        tidsOk &= ModbusGatewayCodec.TransactionId(fa) == 0x1000 + i && Hx(fa.Skip(6).ToArray()) == "11 03 02 02 2B";
        tidsOk &= ModbusGatewayCodec.TransactionId(fb) == 0x2000 + i && Hx(fb.Skip(6).ToArray()) == "11 03 02 00 64";
    }
    CheckTrue("TCP loopback via gateway -> fake RTU slave, tid mapping (2 clients x 5)", tidsOk);
    CheckTrue("bus saw serialized RTU frames", bus.Sent.Count == 12 && bus.MaxConcurrent == 1);
    busSilent = true;
    var tt = await MbClient(0x3000, 1, 0x6B);
    Check("TCP loopback timeout -> 0B", "30 00 00 00 00 03 11 83 0B", Hx(tt.Take(9).ToArray()));
    busSilent = false;
    await gw.StopAsync();
    CheckTrue("gateway stopped", !gw.IsRunning);

    // 反向：串口 RTU 主站 → 远端 Modbus TCP 从站
    var rm = new ModbusDataModel(20); rm.Holding[2] = 0x1234;
    var tslave = new ModbusSlave(rm) { SlaveId = 1, Framing = ModbusFraming.Tcp };
    var remote = TcpServerConnection.Listen(0, null);
    bool remoteSilent = false;
    remote.Received += d => { if (remoteSilent) return; foreach (var (resp, _) in tslave.Push(d, DateTime.Now)) if (resp != null) remote.Send(resp); };
    remote.Start();
    var serialSide = new FakeChannel("串口");
    var serialRx = new List<byte>();
    serialSide.SentHook = d => { lock (serialRx) serialRx.AddRange(d); };
    await using var rgw = new ModbusRtuToTcpGateway(serialSide, "127.0.0.1", remote.LocalPort) { TimeoutMs = 300 };
    rgw.Start();
    var req1 = ModbusGatewayCodec.BuildRtu(1, H("03 00 02 00 01"));
    serialSide.Inject(req1.Take(3).ToArray());
    await Task.Delay(20);
    serialSide.Inject(req1.Skip(3).ToArray());
    CheckTrue("reverse: RTU master gets RTU reply", await WaitFor(() => { lock (serialRx) return serialRx.Count >= 7; }));
    lock (serialRx) { Check("reverse: reply with CRC", Hx(ModbusGatewayCodec.BuildRtu(1, H("03 02 12 34"))), Hx(serialRx.ToArray())); serialRx.Clear(); }
    remoteSilent = true;
    serialSide.Inject(ModbusGatewayCodec.BuildRtu(1, H("03 00 02 00 01")));
    CheckTrue("reverse: timeout reply arrives", await WaitFor(() => { lock (serialRx) return serialRx.Count >= 5; }));
    lock (serialRx) { Check("reverse: timeout -> RTU exception 0B", Hx(ModbusGatewayCodec.BuildRtu(1, H("83 0B"))), Hx(serialRx.ToArray())); serialRx.Clear(); }
    CheckTrue("reverse: tid increments", rgw.LastTransactionId == 2 && rgw.Stats.Timeouts == 1 && rgw.Stats.Responses == 1);
    remoteSilent = false;
    // 迟到的旧应答（tid 2）应被丢弃，下一次请求仍匹配正确
    serialSide.Inject(ModbusGatewayCodec.BuildRtu(1, H("03 00 02 00 01")));
    CheckTrue("reverse: next request after timeout ok", await WaitFor(() => { lock (serialRx) return serialRx.Count >= 7; }));
    lock (serialRx) { Check("reverse: stale reply skipped", Hx(ModbusGatewayCodec.BuildRtu(1, H("03 02 12 34"))), Hx(serialRx.Take(7).ToArray())); serialRx.Clear(); }
    await rgw.StopAsync();
    remote.Stop();
    var rgw2 = new ModbusRtuToTcpGateway(serialSide, "127.0.0.1", 1) { ConnectTimeoutMs = 1000 };
    var unreach = await rgw2.ProcessAsync(ModbusGatewayCodec.BuildRtu(1, H("03 00 00 00 01")));
    Check("reverse: remote unreachable -> 0A", Hx(ModbusGatewayCodec.BuildRtu(1, H("83 0A"))), Hx(unreach!));
}

// =====================================================================
// v1.5：Lua 脚本
// =====================================================================
{
    Console.WriteLine("\n== v1.5 Lua ==");
    var lch = new FakeChannel("Lua 会话");
    lch.Responder = d =>
    {
        var t = Encoding.ASCII.GetString(d);
        if (t == "AT\r\n") return A("\r\nOK\r\n");
        if (t.StartsWith("PING")) return A("PONG " + t.Substring(4));
        return null;
    };
    var lh = new LuaScriptHost(lch);
    var lout = new ConcurrentQueue<string>();
    lh.Output += m => lout.Enqueue(m);
    var lr = lh.Execute(@"
send('AT\r\n')
local r = waitfor('OK', 1000)
log('got', r ~= nil)
sendhex('01 03 00 00 00 01', 'modbus')
status = 'done'
return 1", CancellationToken.None);
    CheckTrue("lua send/waitfor ok", lr.Status == LuaRunStatus.Ok && lout.Contains("got\ttrue"));
    CheckTrue("lua sendhex with modbus crc", lch.Sent.Count == 2 && Hx(lch.Sent[1]) == "01 03 00 00 00 01 84 0A");
    CheckTrue("lua status variable", lh.Status == "done");

    lout.Clear();
    lh.Execute(@"log(hex(crc16('123456789'), 4)) log(crc16(unhex('01 03 00 00 00 0A'))) log(hex(checksum('modbus', unhex('01 03 00 00 00 0A'))))
log(sum8('123456789'), xor8('123456789'), hex(crc16_ccitt('123456789'), 4)) log(hex(append_checksum('xor8', unhex('AA 55'))))
log(waitfor('NOPE', 50) == nil) log(session.name, session.isopen())", CancellationToken.None);
    var lo = lout.ToArray();
    CheckTrue("lua crc16 = 4B37", lo.Length > 0 && lo[0] == "4B37");
    CheckTrue("lua crc16 numeric", lo.Length > 1 && lo[1] == (0xCDC5).ToString());
    CheckTrue("lua checksum bytes", lo.Length > 2 && lo[2] == "C5 CD");
    CheckTrue("lua sum8/xor8/ccitt", lo.Length > 3 && lo[3] == "221\t49\t29B1");
    CheckTrue("lua append_checksum", lo.Length > 4 && lo[4] == "AA 55 FF");
    CheckTrue("lua waitfor timeout -> nil", lo.Length > 5 && lo[5] == "true");
    CheckTrue("lua session table", lo.Length > 6 && lo[6] == "Lua 会话\ttrue");

    var er = lh.Execute("local a = 1\nlocal b = 2\nlocal c = nil + a\n", CancellationToken.None);
    CheckTrue("lua runtime error line number", er.Status == LuaRunStatus.Error && er.Line == 3);
    var se = lh.Execute("x = 1\nif x then\n  y = = 2\nend", CancellationToken.None);
    CheckTrue("lua syntax error line number", se.Status == LuaRunStatus.Error && se.Line == 3 && se.Message.Contains("语法"));
    var ae = lh.Execute("log('a')\n\nsendhex('ZZ')", CancellationToken.None);
    CheckTrue("lua API error line number", ae.Status == LuaRunStatus.Error && ae.Line == 3);

    // 取消死循环
    var lcts = new CancellationTokenSource(300);
    var lsw = System.Diagnostics.Stopwatch.StartNew();
    var cr = lh.Execute("local n = 0\nwhile true do n = n + 1 end", lcts.Token);
    CheckTrue("lua cancel busy loop", cr.Status == LuaRunStatus.Cancelled && lsw.ElapsedMilliseconds < 3000);
    var lcts2 = new CancellationTokenSource(200);
    var cr2 = lh.Execute("sleep(10000)", lcts2.Token);
    CheckTrue("lua cancel sleep", cr2.Status == LuaRunStatus.Cancelled);

    // on_receive 回调 + 后台运行 + Stop
    lch.Sent.Clear();
    lout.Clear();
    var bg = new LuaScriptHost(lch);
    bool runningSeen = false;
    bg.RunningChanged += r => { if (r) runningSeen = true; };
    var bgOut = new ConcurrentQueue<string>();
    bg.Output += m => bgOut.Enqueue(m);
    var bgTask = bg.RunAsync("count = 0\nfunction on_receive(d)\n  count = count + 1\n  status = 'n=' .. count\n  send('ECHO:' .. d)\nend\nlog('ready')");
    CheckTrue("lua background ready", await WaitFor(() => bgOut.Contains("ready")) && bg.IsRunning && runningSeen);
    lch.Inject(A("abc"));
    CheckTrue("lua on_receive echoes", await WaitFor(() => lch.Sent.Any(x => Encoding.ASCII.GetString(x) == "ECHO:abc")));
    CheckTrue("lua status from callback", await WaitFor(() => bg.Status == "n=1"));
    bg.Stop();
    var bgr = await bgTask;
    CheckTrue("lua stop background", bgr.Status == LuaRunStatus.Cancelled && !bg.IsRunning);
    CheckTrue("lua byte string round trip", LuaScriptHost.ToByteString(new byte[] { 0, 0x80, 0xFF }) == "\u0000\u0080\u00FF" && Hx(new LuaScriptHost(lch).FromLuaString("\u0001\u00FF")) == "01 FF");
    CheckTrue("lua samples compile", LuaSamples.All.All(sm => { try { new MoonSharp.Interpreter.Script().LoadString(sm.Code); return true; } catch { return false; } }));
}


// =====================================================================
// v1.6：多项发送
// =====================================================================
{
    Console.WriteLine("\n== v1.6 多项发送 ==");
    var sent = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var eng = new MultiSendEngine { DelayFn = (ms, ct) => { if (ct.IsCancellationRequested) return false; if (ms > 0) ct.WaitHandle.WaitOne(Math.Min(ms, 5)); return !ct.IsCancellationRequested; } };
    var items = new List<MultiSendItem>
    {
        new() { Enabled = true, Name = "A", Content = "A", DelayMs = 10 },
        new() { Enabled = false, Name = "skip", Content = "X", DelayMs = 10 },
        new() { Enabled = true, Name = "B", Content = "B", DelayMs = 10 },
        new() { Enabled = true, Name = "C", Content = "C", DelayMs = 10 },
        new() { Enabled = true, Name = "empty", Content = "", DelayMs = 10 },
    };
    var order = new List<string>();
    eng.ItemSent += (_, it) => order.Add(it.Content);
    await eng.StartAsync(items, false, it => { sent.Enqueue(it.Content); return null; });
    await eng.WaitAsync();
    Check("sequence skips unselected/empty", "A,B,C", string.Join(",", order));
    CheckTrue("one pass loops=1", eng.LoopsCompleted == 1 && eng.SendsCompleted == 3 && eng.State == MultiSendState.Idle);

    // 循环两轮后停止
    order.Clear();
    var eng2 = new MultiSendEngine { DelayFn = (ms, ct) => !ct.IsCancellationRequested };
    eng2.ItemSent += (_, it) =>
    {
        order.Add(it.Content);
        if (order.Count >= 5) eng2.Stop(); // A B C A B → stop
    };
    await eng2.StartAsync(items, true, it => null);
    await eng2.WaitAsync();
    CheckTrue("loop continues then stop", string.Join(",", order).StartsWith("A,B,C,A,B") && eng2.State == MultiSendState.Idle);

    // 暂停 / 继续
    order.Clear();
    var eng3 = new MultiSendEngine();
    eng3.DelayFn = (ms, ct) =>
    {
        // 让暂停有机会卡住
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < Math.Max(ms, 80))
        {
            if (ct.IsCancellationRequested) return false;
            if (ct.WaitHandle.WaitOne(10)) return false;
        }
        return true;
    };
    var started = new ManualResetEventSlim(false);
    eng3.ItemSent += (_, it) =>
    {
        order.Add(it.Content);
        if (order.Count == 1) started.Set();
    };
    var t = eng3.StartAsync(new List<MultiSendItem>
    {
        new() { Content = "1", DelayMs = 200 },
        new() { Content = "2", DelayMs = 200 },
        new() { Content = "3", DelayMs = 10 },
    }, false, it => null);
    CheckTrue("pause: first item sent", started.Wait(3000));
    eng3.Pause();
    CheckTrue("state paused", await WaitFor(() => eng3.State == MultiSendState.Paused, 2000));
    int atPause = order.Count;
    await Task.Delay(300);
    CheckTrue("paused freezes sequence", order.Count == atPause);
    eng3.Resume();
    await eng3.WaitAsync();
    Check("resume continues", "1,2,3", string.Join(",", order));
    CheckTrue("resume ends idle", eng3.State == MultiSendState.Idle && eng3.CurrentIndex < 0);

    // 发送失败中止
    var eng4 = new MultiSendEngine { DelayFn = (ms, ct) => true };
    string? finErr = "not-set";
    eng4.Finished += e => finErr = e;
    await eng4.StartAsync(new List<MultiSendItem> { new() { Content = "ok" }, new() { Content = "bad" }, new() { Content = "later" } }, false,
        it => it.Content == "bad" ? "模拟失败" : null);
    await eng4.WaitAsync();
    Check("fail stops with error", "模拟失败", finErr);
    CheckTrue("fail did not send later", eng4.SendsCompleted == 1);

    // Clone / normalize / settings round trip
    var msItem = new MultiSendItem { Name = "n", Content = "01 02", Hex = true, Checksum = "ModbusCrc16", DelayMs = -5, Enabled = true };
    msItem.Normalize();
    CheckTrue("normalize delay", msItem.DelayMs == 0);
    var app = new AppSettings { MultiSendLoop = true, MultiSendItems = { msItem.Clone() } };
    var json = System.Text.Json.JsonSerializer.Serialize(app);
    var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
    CheckTrue("settings persist multi-send", back.MultiSendLoop && back.MultiSendItems.Count == 1 && back.MultiSendItems[0].Hex && back.MultiSendItems[0].Checksum == "ModbusCrc16");
    var sess = SessionSettings.FromLegacy(back, "会话 1");
    CheckTrue("fromlegacy copies multi-send", sess.MultiSendLoop && sess.MultiSendItems.Count == 1 && sess.Clone().MultiSendItems[0].Content == "01 02");
}


// =====================================================================
// v1.7：ASCII 行解析
// =====================================================================
{
    Console.WriteLine("\n== v1.7 ASCII 行解析 ==");
    var aT0 = new DateTime(2026, 1, 1, 12, 0, 0);
    var aEnc = Encoding.UTF8;
    var aKv = AsciiLineTemplate.SampleKeyValue();
    CheckTrue("ascii kv template valid", aKv.Validate() == null);
    var aPk = new AsciiLineParser(aKv);
    var aR1 = aPk.Push(aEnc.GetBytes("temp=23.5,hum=40\r\n"), aT0, false, aEnc);
    CheckTrue("ascii kv one line", aR1.Count == 1 && aR1[0].Ok);
    Check("ascii kv temp", "23.5 ℃", aR1[0].Fields.First(f => f.Name == "温度").Value);
    Check("ascii kv hum", "40 %", aR1[0].Fields.First(f => f.Name == "湿度").Value);

    var aPk2 = new AsciiLineParser(aKv);
    CheckTrue("ascii split mid-line pending", aPk2.Push(aEnc.GetBytes("temp=1"), aT0, false, aEnc).Count == 0 && aPk2.PendingBytes > 0);
    CheckTrue("ascii split after CR still pending", aPk2.Push(aEnc.GetBytes(".5,hum=2\r"), aT0.AddMilliseconds(10), false, aEnc).Count == 0);
    var aR2 = aPk2.Push(aEnc.GetBytes("\nmore=x\n"), aT0.AddMilliseconds(20), false, aEnc);
    CheckTrue("ascii CRLF across chunks", aR2.Count >= 1 && aR2[0].Ok && aR2[0].Fields.First(f => f.Name == "温度").Value.StartsWith("1.5"));

    var aCsv = AsciiLineTemplate.SampleCsv();
    var aPc = new AsciiLineParser(aCsv);
    var aLf = aPc.Push(aEnc.GetBytes("1,2.5,ok\n2,3,bad\r3,4,x\r\n"), aT0, false, aEnc);
    CheckTrue("ascii LF/CR/CRLF all work", aLf.Count == 3 && aLf.All(x => x.Ok));
    Check("ascii csv col0", "1", aLf[0].Fields[0].Value);
    Check("ascii csv float", "2.5", aLf[0].Fields[1].Value);

    var aPlain = new AsciiLineTemplate
    {
        Name = "plain", Mode = AsciiSplitMode.Delimiter, Delimiter = ",", Prefix = "", Suffix = "",
        Fields = { new AsciiField { Name = "a", Index = 0 }, new AsciiField { Name = "b", Index = 1 } },
    };
    CheckTrue("ascii empty prefix/suffix ok", aPlain.Validate() == null);
    CheckTrue("ascii no header required", new AsciiLineParser(aPlain).Push(aEnc.GetBytes("hello,world\n"), aT0, false, aEnc)[0].Ok);

    var aWithP = aPlain.Clone();
    aWithP.Prefix = "DATA:";
    var aPf = new AsciiLineParser(aWithP);
    var aRf = aPf.Push(aEnc.GetBytes("skip,me\nDATA:1,2\n"), aT0, false, aEnc);
    CheckTrue("ascii prefix filter", aRf.Count == 1 && aRf[0].Ok && aRf[0].Fields[0].Value == "1" && aPf.SkippedLines == 1);

    var aWithS = aPlain.Clone();
    aWithS.Suffix = ";";
    var aPs = new AsciiLineParser(aWithS);
    var aRs = aPs.Push(aEnc.GetBytes("a,b;\nx,y\n"), aT0, false, aEnc);
    CheckTrue("ascii suffix filter", aRs.Count == 1 && aRs[0].Ok && aPs.SkippedLines == 1);

    var aRxTpl = new AsciiLineTemplate
    {
        Name = "re", Mode = AsciiSplitMode.Regex, Delimiter = @"ID=(\d+)\s+VAL=([-+.\d]+)",
        Fields = { new AsciiField { Name = "id", Index = 0, Type = AsciiFieldType.Int }, new AsciiField { Name = "val", Index = 1, Type = AsciiFieldType.Float } },
    };
    CheckTrue("ascii regex valid", aRxTpl.Validate() == null);
    var aRr = new AsciiLineParser(aRxTpl).Push(aEnc.GetBytes("ID=12 VAL=-3.5\n"), aT0, false, aEnc);
    CheckTrue("ascii regex extract", aRr.Count == 1 && aRr[0].Ok && aRr[0].Fields[0].Value == "12" && aRr[0].Fields[1].Value == "-3.5");

    var aFw = new AsciiLineTemplate
    {
        Name = "fw", Mode = AsciiSplitMode.FixedWidth,
        Fields =
        {
            new AsciiField { Name = "code", Start = 0, Length = 3, Type = AsciiFieldType.Text },
            new AsciiField { Name = "num", Start = 3, Length = 4, Type = AsciiFieldType.Int, Scale = 0.1, Unit = "V" },
        },
    };
    var aRw = new AsciiLineParser(aFw).Push(aEnc.GetBytes("ABC0125\n"), aT0, false, aEnc);
    CheckTrue("ascii fixed width", aRw.Count == 1 && aRw[0].Ok && aRw[0].Fields[0].Value == "ABC" && aRw[0].Fields[1].Value == "12.5 V");

    var aHx = new AsciiLineTemplate
    {
        Name = "hx", Mode = AsciiSplitMode.Delimiter, Delimiter = ",",
        Fields = { new AsciiField { Name = "bin", Index = 0, Type = AsciiFieldType.Hex } },
    };
    Check("ascii hex field", "0A FF", new AsciiLineParser(aHx).Push(aEnc.GetBytes("0aff\n"), aT0, false, aEnc)[0].Fields[0].Value);

    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var aGbk = Encoding.GetEncoding("GBK");
    var aTg = aPlain.Clone();
    aTg.Encoding = "GBK";
    var aChinese = aGbk.GetBytes("你好,世界\n");
    var aRg = new AsciiLineParser(aTg).Push(aChinese, aT0, false, Encoding.UTF8);
    CheckTrue("ascii GBK encoding", aRg.Count == 1 && aRg[0].Ok && aRg[0].Fields[0].Value == "你好" && aRg[0].Fields[1].Value == "世界");

    var aPt = new AsciiLineParser(new AsciiLineTemplate { Name = "t", PartialTimeoutMs = 50, Fields = { new AsciiField { Name = "a", Index = 0 } }, Mode = AsciiSplitMode.Delimiter, Delimiter = "," });
    aPt.Push(aEnc.GetBytes("incomp"), aT0, false, aEnc);
    var aLate = aPt.Push(aEnc.GetBytes("lete,1\n"), aT0.AddMilliseconds(200), false, aEnc);
    CheckTrue("ascii partial timeout drops stale", aLate.Count == 1 && aLate[0].Ok && aLate[0].Fields[0].Value == "lete");

    var aBadP = new AsciiLineParser(aCsv);
    var aRb = aBadP.Push(aEnc.GetBytes("only-one\n"), aT0, false, aEnc);
    CheckTrue("ascii missing column -> error", aRb.Count == 1 && !aRb[0].Ok && aBadP.BadCount == 1);

    var aPath = Path.Combine(Path.GetTempPath(), "ascii_tpl_" + Guid.NewGuid().ToString("N") + ".json");
    AsciiLineTemplateStore.Save(aPath, new List<AsciiLineTemplate> { aKv, aCsv });
    var aLoaded = AsciiLineTemplateStore.Load(aPath);
    CheckTrue("ascii store round trip", aLoaded.Count == 2 && aLoaded[0].Name == aKv.Name && aLoaded[1].Fields.Count == 3);
    try { File.Delete(aPath); } catch { }

    var aBin = FrameTemplate.Sample();
    CheckTrue("binary frame still valid", aBin.Validate() == null && new FrameParser(aBin).Push(H("AA 55 05 01 00 FA 01 C2 C3"), aT0).Count == 1);
}

// ====================== v1.8：数据转换 ======================
{
    Check("dc float 1.0 ABCD", "3F 80 00 00", Hx(DataConvert.TryEncode(NumType.Float, "1.0", ByteOrder.ABCD, out var dcB1, out _) ? dcB1 : Array.Empty<byte>()));
    DataConvert.TryEncode(NumType.Float, "1", ByteOrder.DCBA, out var dcB2, out _);
    Check("dc float 1.0 DCBA", "00 00 80 3F", Hx(dcB2));
    DataConvert.TryEncode(NumType.Float, "1", ByteOrder.BADC, out var dcB3, out _);
    Check("dc float 1.0 BADC", "80 3F 00 00", Hx(dcB3));
    DataConvert.TryEncode(NumType.Float, "1", ByteOrder.CDAB, out var dcB4, out _);
    Check("dc float 1.0 CDAB", "00 00 3F 80", Hx(dcB4));
    foreach (var (o, exp) in new[] { (ByteOrder.ABCD, "12 34 56 78"), (ByteOrder.DCBA, "78 56 34 12"), (ByteOrder.BADC, "34 12 78 56"), (ByteOrder.CDAB, "56 78 12 34") })
    {
        DataConvert.TryEncode(NumType.Int32, "305419896", o, out var dcI, out _);
        Check($"dc int32 0x12345678 {o}", exp, Hx(dcI));
        DataConvert.TryDecode(NumType.UInt32, H(exp), o, out var dcBack);
        Check($"dc uint32 decode {o}", "305419896", dcBack);
    }
    DataConvert.TryEncode(NumType.Int16, "-2", ByteOrder.ABCD, out var dcS1, out _);
    Check("dc int16 -2", "FF FE", Hx(dcS1));
    DataConvert.TryEncode(NumType.Int16, "-2", ByteOrder.DCBA, out var dcS2, out _);
    Check("dc int16 -2 little", "FE FF", Hx(dcS2));
    DataConvert.TryEncode(NumType.Int16, "0xFFFF", ByteOrder.ABCD, out var dcS3, out _);
    DataConvert.TryDecode(NumType.Int16, dcS3, ByteOrder.ABCD, out var dcS3v);
    Check("dc int16 0xFFFF = -1", "-1", dcS3v);
    CheckTrue("dc uint16 65536 out of range", !DataConvert.TryEncode(NumType.UInt16, "65536", ByteOrder.ABCD, out _, out var dcErr1) && dcErr1.Contains("范围"));
    CheckTrue("dc int8 -129 out of range", !DataConvert.TryEncode(NumType.Int8, "-129", ByteOrder.ABCD, out _, out _));
    CheckTrue("dc uint32 negative rejected", !DataConvert.TryEncode(NumType.UInt32, "-1", ByteOrder.ABCD, out _, out _));
    CheckTrue("dc bad text rejected", !DataConvert.TryEncode(NumType.Int32, "12a", ByteOrder.ABCD, out _, out _));
    DataConvert.TryEncode(NumType.UInt64, "18446744073709551615", ByteOrder.ABCD, out var dcU64, out _);
    Check("dc uint64 max", "FF FF FF FF FF FF FF FF", Hx(dcU64));
    DataConvert.TryEncode(NumType.Int64, "-9223372036854775808", ByteOrder.ABCD, out var dcI64, out _);
    Check("dc int64 min", "80 00 00 00 00 00 00 00", Hx(dcI64));
    DataConvert.TryEncode(NumType.Double, "1", ByteOrder.ABCD, out var dcD1, out _);
    Check("dc double 1.0", "3F F0 00 00 00 00 00 00", Hx(dcD1));
    DataConvert.TryDecode(NumType.Double, H("00 00 00 00 00 00 F0 3F"), ByteOrder.DCBA, out var dcD2);
    Check("dc double decode little", "1", dcD2);
    DataConvert.TryDecode(NumType.Float, H("40 48 F5 C3"), ByteOrder.ABCD, out var dcF3);
    Check("dc float 3.14", "3.14", dcF3);
    DataConvert.TryDecode(NumType.Float, H("F5 C3 40 48"), ByteOrder.CDAB, out var dcF4);
    Check("dc float 3.14 CDAB", "3.14", dcF4);
    Check("dc reorder 64-bit CDAB", "07 08 05 06 03 04 01 02", Hx(DataConvert.Reorder(H("01 02 03 04 05 06 07 08"), ByteOrder.CDAB)));
    Check("dc reorder 64-bit BADC", "02 01 04 03 06 05 08 07", Hx(DataConvert.Reorder(H("01 02 03 04 05 06 07 08"), ByteOrder.BADC)));
    Check("dc reorder 16-bit CDAB unchanged", "01 02", Hx(DataConvert.Reorder(H("01 02"), ByteOrder.CDAB)));
    bool dcInvol = true;
    foreach (var o in DataConvert.AllOrders)
        foreach (var len in new[] { 2, 4, 8 })
        {
            var src = Enumerable.Range(1, len).Select(x => (byte)x).ToArray();
            if (!DataConvert.Reorder(DataConvert.Reorder(src, o), o).SequenceEqual(src)) dcInvol = false;
        }
    CheckTrue("dc reorder is involution", dcInvol);
    bool dcRound = true;
    foreach (var t in DataConvert.AllTypes)
        foreach (var o in DataConvert.AllOrders)
        {
            string v = t is NumType.Float or NumType.Double ? "-12.5" : t.ToString().StartsWith("U") ? "200" : "-100";
            if (!DataConvert.TryEncode(t, v, o, out var dcEncR, out _) || !DataConvert.TryDecode(t, dcEncR, o, out var dec) || dec != v) { dcRound = false; Console.WriteLine($"  round fail {t} {o}"); }
        }
    CheckTrue("dc all types × orders round trip", dcRound);
    Check("dc endian swap 4", "04 03 02 01 08 07 06 05 09", Hx(DataConvert.EndianSwap(H("01 02 03 04 05 06 07 08 09"), 4)));
    Check("dc endian swap 2", "02 01 04 03", Hx(DataConvert.EndianSwap(H("01 02 03 04"), 2)));
    var dcGbk = EncodingUtil.Get("GBK");
    DataConvert.TryParse("中A", ByteFormat.Text, dcGbk, out var dcT1, out _);
    Check("dc GBK text → hex", "D6 D0 41", Hx(dcT1));
    DataConvert.TryParse("中A", ByteFormat.Text, new UTF8Encoding(false), out var dcT2, out _);
    Check("dc UTF-8 text → hex", "E4 B8 AD 41", Hx(dcT2));
    Check("dc hex → GBK text", "中A", DataConvert.Format(H("D6 D0 41"), ByteFormat.Text, dcGbk));
    DataConvert.TryParse("1,2 255;-1", ByteFormat.Decimal, dcGbk, out var dcDec, out _);
    Check("dc decimal list", "01 02 FF FF", Hx(dcDec));
    CheckTrue("dc decimal 256 rejected", !DataConvert.TryParse("256", ByteFormat.Decimal, dcGbk, out _, out _));
    Check("dc format decimal", "1 2 255", DataConvert.Format(H("01 02 FF"), ByteFormat.Decimal, dcGbk));
    DataConvert.TryParse("00001111 0b11110000", ByteFormat.Binary, dcGbk, out var dcBin, out _);
    Check("dc binary parse", "0F F0", Hx(dcBin));
    DataConvert.TryParse("0000000111111111", ByteFormat.Binary, dcGbk, out var dcBin2, out _);
    Check("dc binary 16-bit run", "01 FF", Hx(dcBin2));
    Check("dc format binary", "00001010 11111111", DataConvert.Format(H("0A FF"), ByteFormat.Binary, dcGbk));
    DataConvert.TryParse("0x01,AABB 3", ByteFormat.Hex, dcGbk, out var dcHex, out _);
    Check("dc hex flexible", "01 AA BB 03", Hx(dcHex));
    CheckTrue("dc hex odd rejected", !DataConvert.TryParse("ABC", ByteFormat.Hex, dcGbk, out _, out _));
    Check("dc decode array int16", "1,-1,256", string.Join(",", DataConvert.DecodeArray(NumType.Int16, H("00 01 FF FF 01 00 07"), ByteOrder.ABCD)));
}

// ====================== v1.8：加密 / 编码计算器 ======================
{
    var ccK128 = H("2b7e151628aed2a6abf7158809cf4f3c");
    var ccK192 = H("8e73b0f7da0e6452c810f32b809079e562f8ead2522c6b7b");
    var ccK256 = H("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");
    var ccPt = H("6bc1bee22e409f96e93d7e117393172a ae2d8a571e03ac9c9eb76fac45af8e51");
    var ccIv = H("000102030405060708090a0b0c0d0e0f");
    var ccCtr = H("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
    string HL(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));
    Check("aes128 ecb NIST", "3ad77bb40d7a3660a89ecaf32466ef97f5d3d58503b9699de785895a96fdbaaf", HL(CryptoCalc.AesEncrypt(ccK128, Array.Empty<byte>(), ccPt, CipherMode2.ECB, PadMode.None)));
    Check("aes192 ecb NIST", "bd334f1d6e45f25ff712a214571fa5cc974104846d0ad3ad7734ecb3ecee4eef", HL(CryptoCalc.AesEncrypt(ccK192, Array.Empty<byte>(), ccPt, CipherMode2.ECB, PadMode.None)));
    Check("aes256 ecb NIST", "f3eed1bdb5d2a03c064b5a7e3db181f8591ccb10d410ed26dc5ba74a31362870", HL(CryptoCalc.AesEncrypt(ccK256, Array.Empty<byte>(), ccPt, CipherMode2.ECB, PadMode.None)));
    Check("aes128 cbc NIST", "7649abac8119b246cee98e9b12e9197d5086cb9b507219ee95db113a917678b2", HL(CryptoCalc.AesEncrypt(ccK128, ccIv, ccPt, CipherMode2.CBC, PadMode.None)));
    Check("aes256 cbc NIST", "f58c4c04d6e5f1ba779eabfb5f7bfbd69cfc4e967edb808d679f777bc6702c7d", HL(CryptoCalc.AesEncrypt(ccK256, ccIv, ccPt, CipherMode2.CBC, PadMode.None)));
    Check("aes128 ctr NIST", "874d6191b620e3261bef6864990db6ce9806f66b7970fdff8617187bb9fffdff", HL(CryptoCalc.AesEncrypt(ccK128, ccCtr, ccPt, CipherMode2.CTR, PadMode.None)));
    Check("aes192 ctr NIST", "1abc932417521ca24f2b0459fe7e6e0b090339ec0aa6faefd5ccc2c6f4ce8e94", HL(CryptoCalc.AesEncrypt(ccK192, ccCtr, ccPt, CipherMode2.CTR, PadMode.None)));
    Check("aes256 ctr NIST", "601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c5", HL(CryptoCalc.AesEncrypt(ccK256, ccCtr, ccPt, CipherMode2.CTR, PadMode.None)));
    Check("aes128 cbc NIST decrypt", HL(ccPt), HL(CryptoCalc.AesDecrypt(ccK128, ccIv, H("7649abac8119b246cee98e9b12e9197d5086cb9b507219ee95db113a917678b2"), CipherMode2.CBC, PadMode.None)));
    Check("aes ctr partial block", "874d6191b6", HL(CryptoCalc.AesEncrypt(ccK128, ccCtr, ccPt.Take(5).ToArray(), CipherMode2.CTR, PadMode.PKCS7)));
    var ccMsg = A("hello, serial");
    var ccEnc = CryptoCalc.AesEncrypt(ccK128, ccIv, ccMsg, CipherMode2.CBC, PadMode.PKCS7);
    CheckTrue("aes pkcs7 length 16", ccEnc.Length == 16);
    Check("aes pkcs7 round trip", "hello, serial", Encoding.ASCII.GetString(CryptoCalc.AesDecrypt(ccK128, ccIv, ccEnc, CipherMode2.CBC, PadMode.PKCS7)));
    CheckTrue("aes pkcs7 full block adds block", CryptoCalc.AesEncrypt(ccK128, ccIv, new byte[16], CipherMode2.ECB, PadMode.PKCS7).Length == 32);
    var ccZ = CryptoCalc.AesEncrypt(ccK256, ccIv, ccMsg, CipherMode2.ECB, PadMode.Zero);
    Check("aes zero pad round trip", "hello, serial", Encoding.ASCII.GetString(CryptoCalc.AesDecrypt(ccK256, ccIv, ccZ, CipherMode2.ECB, PadMode.Zero)));
    bool ccThrowLen = false; try { CryptoCalc.AesEncrypt(H("01 02"), ccIv, ccMsg, CipherMode2.ECB, PadMode.PKCS7); } catch (ArgumentException) { ccThrowLen = true; }
    CheckTrue("aes bad key length", ccThrowLen);
    bool ccThrowNone = false; try { CryptoCalc.AesEncrypt(ccK128, ccIv, ccMsg, CipherMode2.ECB, PadMode.None); } catch (ArgumentException) { ccThrowNone = true; }
    CheckTrue("aes none padding needs block multiple", ccThrowNone);
    bool ccThrowPad = false; try { CryptoCalc.AesDecrypt(H("000102030405060708090a0b0c0d0e0f"), ccIv, ccEnc, CipherMode2.CBC, PadMode.PKCS7); } catch (System.Security.Cryptography.CryptographicException) { ccThrowPad = true; }
    CheckTrue("aes wrong key → padding error", ccThrowPad);

    var ccCheck = A("123456789");
    foreach (var c in CryptoCalc.Crcs)
        Check($"crc check {c.Name}", c.Format(c.Check), c.Format(c.Compute(ccCheck)));
    Check("crc32 123456789", "CBF43926", CryptoCalc.FindCrc("CRC-32")!.Format(CryptoCalc.FindCrc("CRC-32")!.Compute(ccCheck)));
    Check("crc32c 123456789", "E3069283", CryptoCalc.FindCrc("CRC-32C (Castagnoli)")!.Format(CryptoCalc.FindCrc("CRC-32C (Castagnoli)")!.Compute(ccCheck)));
    var ccModbus = CryptoCalc.FindCrc("CRC-16/MODBUS")!;
    Check("crc16 modbus matches Checksum (LE bytes)", Hx(Checksum.Compute(ChecksumKind.ModbusCrc16, H("01 03 00 00 00 0A"))), Hx(ccModbus.ToBytes(ccModbus.Compute(H("01 03 00 00 00 0A")), true)));
    Check("crc32 empty", "00000000", CryptoCalc.FindCrc("CRC-32")!.Format(CryptoCalc.FindCrc("CRC-32")!.Compute(Array.Empty<byte>())));

    Check("md5 abc", "900150983cd24fb0d6963f7d28e17f72", HL(CryptoCalc.Md5(A("abc"))));
    Check("sha1 abc", "a9993e364706816aba3e25717850c26c9cd0d89d", HL(CryptoCalc.Sha1(A("abc"))));
    Check("sha256 abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", HL(CryptoCalc.Sha256(A("abc"))));
    Check("sha256 empty", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", HL(CryptoCalc.Sha256(Array.Empty<byte>())));
    Check("sha256 448-bit", "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1", HL(CryptoCalc.Sha256(A("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"))));

    Check("base64 encode", "aGVsbG8=", CryptoCalc.Base64Encode(A("hello")));
    Check("base64 decode no pad", "hello", Encoding.ASCII.GetString(CryptoCalc.Base64Decode("aGVsbG8")));
    Check("base64 decode url-safe", "FB FF", Hx(CryptoCalc.Base64Decode("-_8=")));
    bool ccB64Bad = false; try { CryptoCalc.Base64Decode("a"); } catch (FormatException) { ccB64Bad = true; }
    CheckTrue("base64 bad length", ccB64Bad);
    Check("url encode utf8", "a%20b%26%E4%B8%AD~", CryptoCalc.UrlEncode("a b&中~", new UTF8Encoding(false)));
    Check("url decode", "a b&中", CryptoCalc.UrlDecode("a+b%26%E4%B8%AD", new UTF8Encoding(false)));
    Check("url decode gbk", "中", CryptoCalc.UrlDecode("%D6%D0", EncodingUtil.Get("GBK")));
    Check("url decode keeps bad %", "100%", CryptoCalc.UrlDecode("100%", new UTF8Encoding(false)));
    Check("xor key", "FE FD FC", Hx(CryptoCalc.Xor(H("01 02 03"), H("FF"))));
    Check("xor repeating key", "00 00 02 06", Hx(CryptoCalc.Xor(H("01 02 03 04"), H("01 02"))));
    Check("xor all (BCC)", "04", CryptoCalc.XorAll(H("01 02 03 04")).ToString("X2"));
    Check("bcd encode odd", "01 23 45", Hx(CryptoCalc.BcdEncode("12345")));
    Check("bcd decode", "1234", CryptoCalc.BcdDecode(H("12 34")));
    bool ccBcdBad = false; try { CryptoCalc.BcdDecode(H("1A")); } catch (FormatException) { ccBcdBad = true; }
    CheckTrue("bcd invalid nibble", ccBcdBad);
}

// ====================== v1.8：接收触发 ======================
{
    var trT0 = new DateTime(2026, 1, 1, 12, 0, 0);
    var trOk = new TriggerRule { Name = "ok", Pattern = "OK", CooldownMs = 1000, Actions = { new TriggerAction { Kind = TriggerActionKind.Toast } } };
    var trHex = new TriggerRule { Name = "hex", Pattern = "AA 55", IsHex = true, CooldownMs = 0, Actions = { new TriggerAction { Kind = TriggerActionKind.Snapshot } } };
    var trRe = new TriggerRule { Name = "re", Pattern = @"temp=\d+", IsRegex = true, CooldownMs = 0, Actions = { new TriggerAction { Kind = TriggerActionKind.Sound } } };
    var trOff = new TriggerRule { Name = "off", Pattern = "OK", Enabled = false, Actions = { new TriggerAction { Kind = TriggerActionKind.ClosePort } } };
    var trNoAct = new TriggerRule { Name = "noact", Pattern = "OK" };
    var trEng = new TriggerEngine();
    trEng.SetRules(new[] { trOk, trHex, trRe, trOff, trNoAct }, new UTF8Encoding(false));
    var trH1 = trEng.Push(A("xxOK\r\n"), trT0);
    CheckTrue("trigger text match", trH1.Count == 1 && trH1[0].Rule == trOk && trH1[0].Count == 1);
    CheckTrue("trigger disabled / no-action rules ignored", trOff.HitCount == 0 && trNoAct.HitCount == 0 && !trNoAct.IsUsable);
    CheckTrue("trigger cooldown suppresses", trEng.Push(A("OK"), trT0.AddMilliseconds(300)).Count == 0 && trOk.HitCount == 1 && trEng.Suppressed >= 1);
    var trH2 = trEng.Push(A("OK"), trT0.AddMilliseconds(1500));
    CheckTrue("trigger fires after cooldown", trH2.Count == 1 && trOk.HitCount == 2 && trH2[0].Count == 2);
    CheckTrue("trigger same bytes not re-matched", trEng.Push(A("zz"), trT0.AddMilliseconds(5000)).Count == 0);
    trEng.Push(A("O"), trT0.AddSeconds(10));
    CheckTrue("trigger split across chunks", trEng.Push(A("K"), trT0.AddSeconds(10.01)).Any(h => h.Rule == trOk));
    CheckTrue("trigger hex", trEng.Push(H("01 AA 55 02"), trT0.AddSeconds(20)).Any(h => h.Rule == trHex));
    CheckTrue("trigger hex split", trEng.Push(H("AA"), trT0.AddSeconds(21)).Count == 0 && trEng.Push(H("55"), trT0.AddSeconds(21)).Any(h => h.Rule == trHex));
    CheckTrue("trigger regex", trEng.Push(A("temp=42\n"), trT0.AddSeconds(30)).Any(h => h.Rule == trRe));
    CheckTrue("trigger hit count + last hit", trHex.HitCount == 2 && trRe.HitCount == 1 && trRe.LastHit == trT0.AddSeconds(30));
    var trBad = new TriggerRule { Pattern = "([", IsRegex = true, Actions = { new TriggerAction() } };
    var trEng2 = new TriggerEngine();
    trEng2.SetRules(new[] { trBad }, Encoding.ASCII);
    CheckTrue("trigger invalid regex reported", trEng2.Errors.ContainsKey(0) && trEng2.Push(A("(["), trT0).Count == 0);
    var trIc = new TriggerRule { Pattern = "error", IgnoreCase = true, CooldownMs = 0, Actions = { new TriggerAction() } };
    var trEng3 = new TriggerEngine();
    trEng3.SetRules(new[] { trIc }, Encoding.ASCII);
    CheckTrue("trigger ignore case", trEng3.Push(A("FATAL ERROR"), trT0).Count == 1);
    // 与自动回复相互独立：自动回复引擎看不到触发规则
    var trAr = new AutoReplyEngine();
    trAr.SetRules(new List<CompiledRule> { new CompiledRule(new HighlightRule { Pattern = "OK", ReplyEnabled = false }, 0, Encoding.ASCII) });
    CheckTrue("trigger independent from auto-reply", trAr.Push(A("OK"), trT0).Count == 0 && new TriggerEngine().Push(A("OK"), trT0).Count == 0);
    Check("trigger expand", "ok 第 3 次 12:00:00", TriggerEngine.Expand("{rule} 第 {count} 次 {time}", new TriggerHit(trOk, 3, trT0)));
    CheckTrue("trigger expand default", TriggerEngine.Expand("", new TriggerHit(trOk, 1, trT0)).Contains("ok"));
    var trPath = Path.Combine(Path.GetTempPath(), "trig_" + Guid.NewGuid().ToString("N") + ".json");
    TriggerRuleStore.Save(trPath, new[] { trOk, trHex });
    var trLoaded = TriggerRuleStore.Load(trPath);
    CheckTrue("trigger store round trip", trLoaded.Count == 2 && trLoaded[0].Pattern == "OK" && trLoaded[0].HitCount == trOk.HitCount && trLoaded[1].IsHex && trLoaded[1].Actions[0].Kind == TriggerActionKind.Snapshot);
    CheckTrue("trigger store enum as string", File.ReadAllText(trPath).Contains("\"Snapshot\""));
    TriggerRuleStore.Save(trPath, new[] { trRe });
    CheckTrue("trigger store overwrite", TriggerRuleStore.Load(trPath).Count == 1);
    try { File.Delete(trPath); } catch { }
    CheckTrue("trigger store missing → samples", TriggerRuleStore.Load(trPath + ".none").Count >= 1);
    var trClone = trOk.Clone(); trClone.Actions[0].Param = "x";
    CheckTrue("trigger clone deep", trOk.Actions[0].Param == "");
}

// ====================== v1.8：检查更新 ======================
{
    CheckTrue("ver 1.8.0 > 1.7.0", UpdateChecker.Compare("v1.8.0", "1.7.0") > 0);
    CheckTrue("ver 1.8 == 1.8.0", UpdateChecker.Compare("1.8", "v1.8.0") == 0);
    CheckTrue("ver 1.10.0 > 1.9.9", UpdateChecker.Compare("1.10.0", "1.9.9") > 0);
    CheckTrue("ver beta < release", UpdateChecker.Compare("1.8.0-beta", "1.8.0") < 0);
    CheckTrue("ver beta < rc", UpdateChecker.Compare("1.8.0-beta", "1.8.0-rc") < 0);
    CheckTrue("ver build metadata ignored", UpdateChecker.Compare("1.8.0+abc", "1.8.0") == 0);
    CheckTrue("ver IsNewer false when same", !UpdateChecker.IsNewer("V1.7.0", "1.7.0.0"));
    CheckTrue("ver older", UpdateChecker.Compare("1.2.9", "1.3") < 0);
    string upJson = @"{""tag_name"":""v1.9.0"",""name"":""SerialTool v1.9.0"",""body"":""## 新功能\n- 测试"",""html_url"":""https://github.com/baogj/SerialTool/releases/tag/v1.9.0"",""draft"":false,""prerelease"":false,""published_at"":""2026-10-01T08:00:00Z"",
        ""assets"":[{""name"":""SerialTool.exe"",""size"":70000000,""browser_download_url"":""https://github.com/baogj/SerialTool/releases/download/v1.9.0/SerialTool.exe""},
                    {""name"":""SerialTool-lite.exe"",""size"":3000000,""browser_download_url"":""https://github.com/baogj/SerialTool/releases/download/v1.9.0/SerialTool-lite.exe""}]}";
    var upRel = UpdateChecker.ParseRelease(upJson);
    CheckTrue("release parse", upRel.TagName == "v1.9.0" && upRel.Version == "1.9.0" && upRel.Body.Contains("新功能") && upRel.Assets.Count == 2 && upRel.PublishedAt.HasValue);
    CheckTrue("release pick full", UpdateChecker.PickAsset(upRel, false)?.Name == "SerialTool.exe" && UpdateChecker.PickAsset(upRel, false)!.Size == 70000000);
    CheckTrue("release pick lite", UpdateChecker.PickAsset(upRel, true)?.Name == "SerialTool-lite.exe");
    CheckTrue("release no lite asset → null", UpdateChecker.PickAsset(UpdateChecker.ParseRelease(@"{""tag_name"":""v1"",""assets"":[{""name"":""SerialTool.exe"",""browser_download_url"":""x""}]}"), true) == null);
    string upErr = "";
    try { UpdateChecker.ParseRelease(@"{""message"":""Not Found""}"); } catch (FormatException ex) { upErr = ex.Message; }
    CheckTrue("release error message", upErr.Contains("Not Found"));
    var upList = UpdateChecker.ParseRelease(@"[{""tag_name"":""v2.0.0-beta"",""prerelease"":true},{""tag_name"":""v1.9.1""}]");
    CheckTrue("release list skips prerelease", upList.TagName == "v1.9.1");
    var upCands = UpdateChecker.CandidateUrls("https://github.com/a/b.exe");
    CheckTrue("mirror url", upCands.Count == 2 && upCands[1] == "https://ghproxy.net/https://github.com/a/b.exe");
    var upNow = new DateTime(2026, 10, 27, 9, 0, 0);
    CheckTrue("auto check off by default flag", !UpdateChecker.ShouldAutoCheck(false, null, upNow));
    CheckTrue("auto check first time", UpdateChecker.ShouldAutoCheck(true, null, upNow));
    CheckTrue("auto check once per day", !UpdateChecker.ShouldAutoCheck(true, upNow.AddHours(-23), upNow) && UpdateChecker.ShouldAutoCheck(true, upNow.AddHours(-24), upNow));
    CheckTrue("lite flag false in net8 tests", !UpdateChecker.IsLiteBuild);

    // 本地 HTTP 服务：第一个地址 404，镜像地址成功
    int upPort;
    { var tl = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); tl.Start(); upPort = ((System.Net.IPEndPoint)tl.LocalEndpoint).Port; tl.Stop(); }
    var upPayload = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();
    var upListener = new System.Net.HttpListener();
    upListener.Prefixes.Add($"http://127.0.0.1:{upPort}/");
    upListener.Start();
    var upServer = Task.Run(async () =>
    {
        while (upListener.IsListening)
        {
            System.Net.HttpListenerContext ctx;
            try { ctx = await upListener.GetContextAsync(); } catch { break; }
            string p = ctx.Request.Url!.AbsolutePath;
            if (p == "/good.exe")
            {
                ctx.Response.ContentLength64 = upPayload.Length;
                await ctx.Response.OutputStream.WriteAsync(upPayload, 0, upPayload.Length);
            }
            else if (p == "/latest")
            {
                var b = Encoding.UTF8.GetBytes(upJson);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(b, 0, b.Length);
            }
            else ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
    });
    using var upClient = UpdateChecker.CreateClient(TimeSpan.FromSeconds(10));
    var upFetched = await UpdateChecker.FetchLatestAsync(upClient, $"http://127.0.0.1:{upPort}/latest");
    CheckTrue("fetch latest via http", upFetched.TagName == "v1.9.0");
    string upFetchErr = "";
    try { await UpdateChecker.FetchLatestAsync(upClient, $"http://127.0.0.1:{upPort}/nope"); } catch (System.Net.Http.HttpRequestException ex) { upFetchErr = ex.Message; }
    CheckTrue("fetch latest 404 error", upFetchErr.Contains("404"));
    var upDir = Path.Combine(Path.GetTempPath(), "upd_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(upDir);
    var upDest = Path.Combine(upDir, "new.exe");
    long upLastProg = 0;
    var upUsed = await UpdateChecker.DownloadAsync(upClient, new[] { $"http://127.0.0.1:{upPort}/missing.exe", $"http://127.0.0.1:{upPort}/good.exe" }, upPayload.Length, upDest,
        new SyncProgress<(long done, long total, string url)>(p => upLastProg = p.done));
    CheckTrue("download falls back to mirror", upUsed.EndsWith("/good.exe") && File.ReadAllBytes(upDest).SequenceEqual(upPayload));
    CheckTrue("download progress reported", upLastProg == upPayload.Length);
    CheckTrue("download no .part left", !File.Exists(upDest + ".part"));
    bool upSizeFail = false;
    try { await UpdateChecker.DownloadAsync(upClient, new[] { $"http://127.0.0.1:{upPort}/good.exe" }, 12345, Path.Combine(upDir, "bad.exe")); }
    catch (IOException ex) { upSizeFail = ex.Message.Contains("12345"); }
    CheckTrue("download size mismatch rejected", upSizeFail && !File.Exists(Path.Combine(upDir, "bad.exe")));
    bool upOffline = false;
    try { await UpdateChecker.DownloadAsync(upClient, new[] { "http://127.0.0.1:1/x.exe" }, 0, Path.Combine(upDir, "off.exe")); }
    catch (IOException) { upOffline = true; }
    CheckTrue("download offline → IOException", upOffline);
    upListener.Stop();
    upListener.Close();

    var upCur = Path.Combine(upDir, "SerialTool.exe");
    File.WriteAllText(upCur, "old");
    File.WriteAllText(upDest, "new");
    var upOld = UpdateChecker.ReplaceExecutable(upCur, upDest);
    CheckTrue("replace exe", File.ReadAllText(upCur) == "new" && File.ReadAllText(upOld) == "old" && upOld.EndsWith(".old") && !File.Exists(upDest));
    CheckTrue("cleanup .old", UpdateChecker.CleanupOld(upCur) == 1 && !File.Exists(upOld) && File.Exists(upCur));
    bool upRollback = false;
    try { UpdateChecker.ReplaceExecutable(upCur, Path.Combine(upDir, "does-not-exist.exe")); } catch { upRollback = File.ReadAllText(upCur) == "new"; }
    CheckTrue("replace rollback on failure", upRollback);
    try { Directory.Delete(upDir, true); } catch { }
}

// ====================== v1.8：数据对比 ======================
{
    var dfR1 = DataDiff.CompareBytes(H("01 02 03 04 05"), H("01 02 FF 04 05 06"));
    CheckTrue("diff bytes hunks", dfR1.Hunks.Count == 2 && dfR1.Hunks[0] == new DiffHunk(2, 1, 2, 1) && dfR1.Hunks[1] == new DiffHunk(5, 0, 5, 1));
    CheckTrue("diff bytes counts", dfR1.DifferingCount == 2 && dfR1.ChangedA == 1 && dfR1.ChangedB == 2 && !dfR1.Approximate);
    CheckTrue("diff identical", DataDiff.CompareBytes(H("01 02"), H("01 02")).Identical);
    CheckTrue("diff empty vs data", DataDiff.CompareBytes(Array.Empty<byte>(), H("01 02")).Hunks.Single() == new DiffHunk(0, 0, 0, 2));
    var dfIns = DataDiff.CompareBytes(H("01 02 03"), H("01 09 02 03"));
    CheckTrue("diff aligned insert", dfIns.Hunks.Count == 1 && dfIns.Hunks[0] == new DiffHunk(1, 0, 1, 1) && dfIns.DifferingCount == 1);
    var dfPos = DataDiff.CompareBytes(H("01 02 03"), H("01 09 02 03"), align: false);
    CheckTrue("diff positional", dfPos.DifferingCount == 3 && dfPos.Hunks.Count == 1 && dfPos.Hunks[0] == new DiffHunk(1, 2, 1, 3));
    var dfPos2 = DataDiff.CompareBytes(H("01 02 03 04"), H("01 FF 03 FF"), align: false);
    CheckTrue("diff positional separate hunks", dfPos2.Hunks.Count == 2 && dfPos2.DifferingCount == 2);
    var dfL = DataDiff.CompareLines(DataDiff.SplitLines("a\nb\nc\n"), DataDiff.SplitLines("a\r\nx\r\nc\r\nd"));
    CheckTrue("diff lines", dfL.Hunks.Count == 2 && dfL.Hunks[0] == new DiffHunk(1, 1, 1, 1) && dfL.Hunks[1] == new DiffHunk(3, 0, 3, 1) && dfL.LengthA == 3);
    CheckTrue("diff lines ignore case/space", DataDiff.CompareLines(new[] { "Hello World" }, new[] { "hello  world" }, ignoreCase: true, ignoreWhitespace: true).Identical);
    var dfRng = new Random(1234);
    bool dfOk = true;
    for (int it = 0; it < 300 && dfOk; it++)
    {
        var a = Enumerable.Range(0, dfRng.Next(0, 40)).Select(_ => dfRng.Next(0, 4)).ToArray();
        var b = Enumerable.Range(0, dfRng.Next(0, 40)).Select(_ => dfRng.Next(0, 4)).ToArray();
        var r = DataDiff.Compare(a, b);
        if (!DataDiff.Apply(a, b, r).SequenceEqual(b)) { dfOk = false; Console.WriteLine($"  apply fail {it}"); break; }
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        if (r.ChangedA + r.ChangedB != a.Length + b.Length - 2 * lcs[0, 0]) { dfOk = false; Console.WriteLine($"  not minimal {it}"); }
        for (int h = 1; h < r.Hunks.Count; h++)
            if (r.Hunks[h].AStart < r.Hunks[h - 1].AStart + r.Hunks[h - 1].ALen) dfOk = false;
    }
    CheckTrue("diff random: apply == B and minimal (vs LCS DP)", dfOk);
    var dfBigA = Enumerable.Range(0, 2000).Select(i => (byte)dfRng.Next(256)).ToArray();
    var dfBigB = Enumerable.Range(0, 2000).Select(i => (byte)dfRng.Next(256)).ToArray();
    var dfApprox = DataDiff.CompareBytes(dfBigA, dfBigB, maxEdits: 50);
    CheckTrue("diff falls back when too many edits", dfApprox.Approximate && DataDiff.Apply(dfBigA.Select(x => (int)x).ToArray(), dfBigB.Select(x => (int)x).ToArray(), dfApprox).SequenceEqual(dfBigB.Select(x => (int)x)));
    var dfLarge = new byte[200_000]; dfRng.NextBytes(dfLarge);
    var dfLarge2 = (byte[])dfLarge.Clone(); dfLarge2[1000] ^= 1; dfLarge2[150_000] ^= 1;
    var dfSw = System.Diagnostics.Stopwatch.StartNew();
    var dfLr = DataDiff.CompareBytes(dfLarge, dfLarge2);
    CheckTrue($"diff 200KB two changes fast ({dfSw.ElapsedMilliseconds} ms)", dfLr.DifferingCount == 2 && dfLr.Hunks.Count == 2 && !dfLr.Approximate && dfSw.ElapsedMilliseconds < 5000);
    CheckTrue("diff navigate", DataDiff.Navigate(3, -1, true) == 0 && DataDiff.Navigate(3, 2, true) == 0 && DataDiff.Navigate(3, 0, false) == 2 && DataDiff.Navigate(0, 0, true) == -1);
}

Console.WriteLine($"\n结果：{pass} 通过，{fail} 失败");
return fail == 0 ? 0 : 1;

static class Pty
{
    [DllImport("libc", SetLastError = true)] public static extern int posix_openpt(int flags);
    [DllImport("libc", SetLastError = true)] public static extern int grantpt(int fd);
    [DllImport("libc", SetLastError = true)] public static extern int unlockpt(int fd);
    [DllImport("libc", SetLastError = true)] public static extern IntPtr ptsname(int fd);
    [DllImport("libc", SetLastError = true)] public static extern long read(int fd, byte[] buf, long count);
    [DllImport("libc", SetLastError = true)] public static extern long write(int fd, byte[] buf, long count);
    [DllImport("libc", SetLastError = true)] public static extern int close(int fd);
}

/// <summary>测试用通道：记录发送内容，可按请求自动应答。</summary>
sealed class FakeChannel : IByteChannel
{
    private int _active;
    public FakeChannel(string name) { Name = name; }
    public string Name { get; }
    public bool Open { get; set; } = true;
    public bool IsOpen => Open;
    public int DelayMs { get; set; } = 1;
    public Func<byte[], byte[]?>? Responder { get; set; }
    public Action<byte[]>? SentHook { get; set; }
    public List<byte[]> Sent { get; } = new();
    public int MaxConcurrent { get; private set; }
    public event Action<byte[]>? Received;
    public void Inject(byte[] d) => Received?.Invoke(d);
    public bool Send(byte[] data)
    {
        if (!Open) return false;
        lock (Sent) Sent.Add(data);
        SentHook?.Invoke(data);
        var resp = Responder?.Invoke(data);
        int now = Interlocked.Increment(ref _active);
        lock (Sent) MaxConcurrent = Math.Max(MaxConcurrent, now);
        Task.Run(async () =>
        {
            await Task.Delay(DelayMs);
            if (resp != null) Received?.Invoke(resp);
            Interlocked.Decrement(ref _active);
        });
        return true;
    }
}

sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _a;
    public SyncProgress(Action<T> a) => _a = a;
    public void Report(T value) => _a(value);
}
