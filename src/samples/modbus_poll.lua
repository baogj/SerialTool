-- Modbus RTU 轮询：每秒读站号 1 的保持寄存器 0~1
local function read_regs(slave, addr, count)
  local req = string.char(slave, 0x03, math.floor(addr / 256), addr % 256, math.floor(count / 256), count % 256)
  local crc = crc16(req)
  clear()
  send(req .. string.char(crc % 256, math.floor(crc / 256)))
  local resp = read(500, 5 + count * 2)
  if not resp then return nil, "超时" end
  local body = resp:sub(1, #resp - 2)
  local rc = crc16(body)
  if resp:byte(#resp - 1) ~= rc % 256 or resp:byte(#resp) ~= math.floor(rc / 256) then return nil, "CRC 错误" end
  if resp:byte(2) >= 0x80 then return nil, "异常码 " .. resp:byte(3) end
  local vals = {}
  for i = 0, count - 1 do
    vals[#vals + 1] = resp:byte(4 + i * 2) * 256 + resp:byte(5 + i * 2)
  end
  return vals
end

for n = 1, 10 do
  local v, err = read_regs(1, 0, 2)
  if v then
    log(string.format("第 %d 次：R0=%d R1=%d", n, v[1], v[2]))
    status = "R0=" .. v[1]
  else
    log("第 " .. n .. " 次失败：" .. err)
  end
  sleep(1000)
end
