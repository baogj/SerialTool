# SerialTool 串口调试助手

一个 Windows 桌面串口（COM 口）调试工具，C# .NET 8 WPF 编写，单个 `SerialTool.exe`，**免安装、无需另装 .NET 运行库**，双击即可运行（Windows 10/11 x64）。

当前版本：**v1.1.0**

## 主要功能

**串口设置**
- 串口列表自动显示设备名称（如 `COM3  USB-SERIAL CH340`），每秒自动检测插拔，也可点“刷新”或按 F5
- 波特率：常用预设（110 ~ 3000000），也可直接输入自定义值（回车生效）
- 数据位 5/6/7/8，停止位 1/1.5/2，校验 无/奇/偶/标记/空格
- 流控：无 / RTS-CTS / XON-XOFF / 两者同时
- DTR、RTS 手动开关（启用 RTS/CTS 流控时 RTS 由硬件自动控制）
- 串口打开后修改波特率等参数会立即生效，无需重新打开
- “自动重连”：设备被拔出后自动关闭，重新插回时自动打开

**接收**
- 文本 / HEX 显示，可选时间戳（精确到毫秒）、显示发送内容（`发→` / `收←`）
- “分帧”：开启时间戳或显示发送时，数据间隔超过设定毫秒数或遇到换行就另起一行
- 编码：UTF-8 / GBK / ASCII（多字节汉字被拆成两包也能正确显示）
- 自动滚动、暂停显示（暂停期间数据会缓存，取消暂停后补上）、清空
- “保存接收…”把当前接收区保存为文本；“记录到文件”把显示内容实时追加写入日志
- 接收区最多保留约 150 万字符，超出后自动裁掉最早的内容，长时间运行不卡顿

**协议解析（v1.1）**
- 左侧「协议」下拉：`普通` / `AT` / `Modbus RTU`
- **AT**：按行着色——命令（青）、OK（绿）、ERROR/BUSY/+CME（红）、+URC（黄）；虚拟化列表，高波特率下仍流畅
- **Modbus RTU**：流式帧检测 + CRC16 校验，显示地址、功能码、长度、CRC✓/✗；正确帧绿色、错误帧红色；状态栏统计帧 OK/ERR
- 协议模式与 HEX/文本显示互为切换视图（选协议时用着色列表，选普通时用原 TextBox）

**字节流波形（v1.1）**
- 顶部「波形」页：把最近 RX（可选 TX）字节画成滚动条带图，Y 轴 0–255
- 暂停、清空、窗口长度 256–8192 可调；纯 WPF 绘制，无额外依赖
- 与接收刷新同路径批量更新，不卡 UI

**脚本自动化（v1.1）**
- 「脚本」页：内置轻量命令语言（无 Roslyn/Lua 依赖，单文件体积小）
- 命令：`SEND` / `SENDHEX` / `SLEEP` / `WAITFOR` / `WAITREGEX` / `LOG` / `CLEARRX` / `STATUS`，`#` 注释
- 后台线程运行，可随时停止；提供 AT 轮询、Modbus 读保持寄存器示例按钮
- `SEND` 支持 `\r \n \t \0 \\ \xHH` 转义

**吞吐率与统计（v1.1）**
- 状态栏：瞬时 RX/TX 速率（1 秒窗口）、峰值、会话时长、总字节数
- Modbus 模式下额外显示帧 OK/ERR 计数
- 「计数清零」同时重置字节计数与吞吐/会话统计

**发送**
- 文本 / HEX 发送（HEX 支持 `AA 55 01`、`AA5501`、`0xAA,0x55` 等写法）；切换模式时自动转换发送区内容
- 行尾自动追加：无 / `\r` / `\n` / `\r\n`（对文本、HEX、快捷指令都生效）
- “解析转义符”：文本中可写 `\r \n \t \0 \\ \xHH`
- 发送按钮或 **Ctrl+Enter** 发送；定时（周期）发送，周期单位 ms（最小 10 ms）
- 发送历史（最近 50 条，下拉选择即可重新填入）
- 快捷指令按钮：点“编辑快捷指令…”自定义名称、内容、是否 HEX，可排序，自动保存
- 发送文件：把任意文件的原始字节发出去（≤16 MB）

**其他**
- 读写都在后台线程进行，界面每 50 ms 批量刷新一次，高波特率下也不卡
- 设备被拔出、读写出错时会自动关闭串口并在状态栏提示，不会崩溃

## 新功能简要用法

1. **AT 着色**：协议选「AT」，打开串口后发 `AT`，回显里 OK/ERROR 会着色。
2. **Modbus**：协议选「Modbus RTU」，用 HEX 发送完整帧（含 CRC），接收区会拆帧并标 CRC 结果。
3. **波形**：切到「波形」页，有数据进来即可看到蓝色 RX 曲线（可选绿色 TX）。
4. **脚本**：切到「脚本」页 →「AT 示例」→「运行」；需先打开串口。`WAITFOR OK 2000` 表示最多等 2 秒直到接收缓冲出现 `OK`。
5. **吞吐**：打开串口后状态栏右侧自动刷新速率；「计数清零」重置。

## 设置保存位置

- 默认：`%AppData%\SerialTool\settings.json`（关闭程序时自动保存串口参数、选项、协议模式、波形窗口、脚本内容、发送内容、历史、快捷指令、窗口大小）
- 便携模式：在 `SerialTool.exe` 同目录下新建一个空的 `settings.json`（内容写 `{}`），以后设置就保存在 exe 旁边
- 程序出错时的详细信息写在同目录的 `error.log`

## 发布物说明

| 文件 | 说明 |
|------|------|
| `SerialTool.exe` | .NET 8 自包含单文件（约 64 MB），免装运行库 |
| `SerialTool-lite.exe` | .NET 6 框架依赖单文件（约 0.9 MB），需本机已装 .NET 6 Windows Desktop |

## 从源码编译

需要 .NET 8 SDK（Windows 或 Linux 都可以编译）：

```
cd src/SerialTool
# 自包含完整版
dotnet publish -c Release -f net8.0-windows -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish

# 小体积（需目标机有 .NET 6 Desktop）
dotnet publish -c Release -f net6.0-windows -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -o publish-lite
```

在 Linux 上编译依赖项目里已开启的 `EnableWindowsTargeting`。
