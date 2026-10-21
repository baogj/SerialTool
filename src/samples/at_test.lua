-- AT 指令测试：逐条发送并等待 OK
local cmds = { "AT", "ATE0", "AT+GMR", "AT+CSQ" }
local ok = 0
for i, cmd in ipairs(cmds) do
  clear()
  send(cmd .. "\r\n")
  local resp = waitfor("OK", 1000)
  if resp then
    ok = ok + 1
    log("✓ " .. cmd)
  else
    log("✗ " .. cmd .. "：超时")
  end
  status = string.format("%d/%d 通过", ok, i)
  sleep(100)
end
log("完成：" .. status)
