-- 回环应答：收到什么就加前缀回发什么（停止前一直运行）
local count = 0
function on_receive(data)
  count = count + 1
  status = "已收到 " .. count .. " 包"
  log("收到：" .. hex(data))
  send("ECHO:" .. data)
end
log("等待数据……会话：" .. session.name)
