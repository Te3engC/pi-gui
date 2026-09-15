# Pi GUI（Windows）

Windows 图形客户端直接经 SSH 启动远端 `pi --mode rpc`，并通过 Pi 官方 JSONL RPC 协议发送对话、图片和中断指令。每个 GUI 连接对应一个远端 Pi 进程，所以图片不会被路由到其他 SSH 终端对话。

## 功能

- 原生 WPF 对话界面与逐字流式显示；启动时分区渐入、任务进行时状态点呼吸、回复过程显示闪烁光标；通过 Pi JSONL RPC 通信，不嵌入 CLI 终端
- 文字输入，`Ctrl+Enter` 发送
- `Ctrl+V` 从 Windows 剪贴板粘贴截图；也可选择 PNG/JPEG/WebP 文件
- “停止”当前 Pi 任务、“新对话”创建新 Pi session
- SSH 设置收纳在右上角“⚙ SSH”子窗口，支持 OpenSSH `config` 别名
- “+”可选择图片或任意文件；图片直接进入模型，其他文件通过 SSH 上传到远端工作目录的 `.pi-gui-uploads/`
- 左侧显示服务器历史会话，可读取、选择并切换到已有 Pi 对话
- 顶栏显示当前 Provider / Model；直接 RPC 连接可从下拉框切换已认证模型
- 独立“命令中心”可搜索并插入 Pi 内置、扩展、技能和提示模板命令；输入 `/` 也会给出过滤建议
- 右侧 Live Work 面板实时展示 Pi `todo` 工具创建/更新的任务，以及思考、回复、工具调用等关键过程
- 默认浅色主题；主题、字体、字号、字重和窗口尺寸保存到 Windows 的 `%LOCALAPPDATA%\\PiGui\\ui-preferences.json`
- 所有以 `/` 开头的 Pi 内置/扩展命令会原样经 RPC 发送，功能保持可用

## 前置条件

1. Windows 已安装 OpenSSH Client（一般位于 `C:\Windows\System32\OpenSSH\ssh.exe`）。
2. Windows 必须已配好到服务器的免交互 SSH 登录。先在 PowerShell 验证：

   `ssh user@<server-ip> true`

   若该命令要求输入密码、确认主机指纹或 MFA，请先在 PowerShell 完成密钥/`known_hosts` 配置。GUI 的 SSH 标准输入用于 Pi RPC，不能安全地兼作密码输入。
3. 服务器有可运行的 Pi，默认路径为 `/home/<user>/bin/pi`；Node 运行时目录默认是 `/home/<user>/local/bin`。这是必要项：非交互 SSH 默认可能会误用系统 Node.js 18，而当前 Pi 需要 Node.js 22。
4. 所选 Pi 模型必须支持图片输入，图片才能被模型接受。

## Windows 构建

在 Windows PowerShell：

`cd <本项目>\windows\PiGui`

`dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`

生成目录：`bin\Release\net8.0-windows\win-x64\publish\`

## 使用

1. 启动 `PiGui.exe`。
2. 点击右上角“⚙ SSH”，确认 SSH 目标 `user@<server-ip>`、工作目录 `/home/<user>`、Pi 路径 `/home/<user>/bin/pi`、Node 目录 `/home/<user>/local/bin`。
3. 点击“连接”。
4. 左侧选择历史对话；“刷新 / 同步”会重新读取 SSH CLI 所在服务器上的会话文件。
5. 顶栏模型下拉框会显示当前模型；可切换到远端已认证的模型。CLI 同步模式只显示提示，不允许从 GUI 切换模型。
6. 使用“/ 命令”或输入 `/` 选择命令；输入文字或粘贴图片后发送。对话内容可直接选择并用 `Ctrl+C` 复制。

## 安全边界

- 不会启动公网 HTTP 上传服务，不再需要端口 `8765`/`18765` 或上传 Token。
- 连接和图片数据仅通过现有 SSH 加密通道传输。
- GUI 会在关闭时终止它所启动的 SSH/Pi 进程；Pi 会话记录仍保存在服务器。
- GUI 和 CLI 可读取、切换同一份历史会话；不要在两个客户端同时对同一个会话发送消息。Pi 会话文件不是多写者实时协作数据库，同时写入可能造成上下文分叉。先在一侧停止，再点击“刷新 / 同步”切换即可。
