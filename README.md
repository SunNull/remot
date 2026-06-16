# Remot

面向 AI agent 的 Windows 远程执行 + 文件传输工具。用一句话替代手工 mstsc / OpenSSH 部署循环:agent 自动完成「停服务 → 上传新文件 → 启服务」。

- **gRPC over HTTP/2 + TLS**:自签证书 + token,一行配对串完成目标登记,无需 CA。
- **远程命令执行**:批量、结构化返回(exit_code/stdout/stderr/超时)、超时连根杀进程树、UTF-8 不乱码、外部取消也杀树。
- **文件传输**:流式分块、SHA256 完整性校验、跳过未改动、并发。
- C# / **.NET 11**;被控端可注册为 Windows 服务常驻;客户端可作 CLI 或 MCP 工具。

---

## 项目结构

| 项目 | 说明 |
|---|---|
| `Remot.Protocol` | gRPC 契约(`remot.proto`,共享) |
| `Remot.Server` | 被控端守护进程,Windows 服务常驻 |
| `Remot.Client` | 客户端类库 |
| `Remot.Cli` | 命令行工具 `remot` |
| `Remot.Mcp` | MCP server,供 Claude Code 调用 |

协议:`RunCommand`(服务端流)、`Upload`(客户端流)、`Download`(服务端流)、`CheckFile`(预检)。

---

## 前置要求

- **构建/开发**:需要 .NET 11 preview SDK(`dotnet --version` 应以 `11.` 开头)。
- **被控端目标机**:**无需装 .NET**——用 `publish.ps1` 产出的单文件自包含 exe,拷过去直接跑。
- **客户端机器**:同样可用单文件 exe,或本机有 .NET 11 时用 `dotnet run`。

---

## 快速开始

### 1. 发布产物
```
pwsh publish.ps1        # 产出 publish/{server,cli,mcp} 三个单文件 exe
```

### 2. 被控端(每台 Windows 服务器,一次性)
把 `publish/server/Remot.Server.exe` 拷到服务器,**以管理员身份**运行:
```
Remot.Server.exe install
```
它会:注册 Windows 服务(自动启动 + 失败重启)、开防火墙端口(默认 7070)、生成自签证书和 token,并打印一行配对串:
```
remot://pair#eyJ0...
```
> 调试时可**不加参数**运行 `Remot.Server.exe`,以控制台模式启动(功能与服务模式一致,仅未注册成服务、需管理员)。

### 3. 开发机登记目标
```
remot pair "remot://pair#eyJ0..."
```
配对串里带 host/port/token/证书指纹;若自动探测的 IP 不对,用 `--host` 覆盖:
```
remot pair --host 192.168.1.20 "remot://pair#eyJ0..."
```

### 4. 使用
```
remot run      -t <目标> [--shell powershell|pwsh|cmd] "命令..."
remot upload   -t <目标> <本地src> <远程dst> [<src> <dst> ...]
remot download -t <目标> <远程路径> <本地路径>
remot target   list
```

命令默认走 **Windows PowerShell**(`powershell`,所有 Windows 都有、Unicode 友好)。批量执行时每条独立返回结构化结果。

### 5. agent 集成(Claude Code)
把 `publish/mcp/Remot.Mcp.exe` 注册为 MCP server(stdio)。Claude Code 自动获得三个工具:
- `remot_run(target, commands[], shell?, timeout_ms?)`
- `remot_upload(target, files[], dests[])`
- `remot_download(target, remotePath, localPath)`

### 典型部署循环
```
remot_run  (stop)   → "nssm stop YourService"
remot_upload        → [所有编译产物 → 服务目录]
remot_run  (start)  → "nssm start YourService"
```
全部 `exit=0` 即部署成功。

---

## 实战要点(shell 与中文)

| 情况 | 建议 |
|---|---|
| 含**中文**的命令或输出 | 用 `powershell` 或 `pwsh`(**不要用 `cmd`**——控制台代码页会让中文乱码) |
| 目标机**没装 PowerShell 7** | 用默认 `powershell`(Windows PowerShell 5.1,必装)或 `cmd` |
| 纯英文命令(如 `nssm stop/start`) | `cmd` / `powershell` 均可 |

默认 shell 已设为 `powershell`,开箱即用。

---

## 故障排查

- **`run` 返回 `exit=-1` 且无输出**:目标 shell 启动失败(多半是 `pwsh` 未安装)。改用 `--shell powershell` 或 `--shell cmd`。
- **中文乱码**:见上表,换 `powershell`。
- **连不上 / 超时**:确认目标机 7070 端口可达、防火墙已开(`install` 会自动开)。
- **token 无效**:重新 `pair`(服务端 `%ProgramData%\Remot\server.json` 的 token 必须与客户端 `~/.remot/targets.json` 一致)。
- **NuGet 还原卡住(国内网络)**:`Remot.Protocol` 已把包版本固定为离线可用版本;若仍慢,可配国内镜像源。

---

## 开发
```
dotnet build       # 编译全方案
dotnet test        # 24 个测试(单元 + 端到端)
pwsh publish.ps1   # 发布单文件 exe
```
解决方案文件为 `Remot.slnx`(新 XML 格式)。各项目共享 `Directory.Build.props`(开启 `Nullable`、`TreatWarningsAsErrors`、`LangVersion=preview`)。

---

## 架构要点

- **命令健壮性**:异步采集 stdout/stderr(管道排空防丢尾部)、`JobObject` 整树超时杀除、外部取消也杀树、大输出截断、启动失败结构化返回。
- **传输**:服务端临时文件落盘 + SHA256 校验 + 失败清理;客户端上传前 `CheckFile` 预检跳过未改动。
- **安全**:token(请求头)+ TLS + 证书指纹锁定;LAN 使用;密钥仅存本地配置文件。
