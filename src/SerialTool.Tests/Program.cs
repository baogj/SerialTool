using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SerialTool;
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
