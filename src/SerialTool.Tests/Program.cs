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
