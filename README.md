# Remot

面向 AI agent 的 Windows 远程执行 + 文件传输工具。替代手工 mstsc / OpenSSH 部署:agent 一句话完成「停服务 → 上传文件 → 启服务」。

- gRPC over HTTP/2 + TLS,自签证书 + token,一行配对串完成登记。
- 远程命令执行(批量、结构化返回、超时杀进程树、UTF-8 不乱码)。
- 文件传输(流式、SHA256 校验、跳过未改动)。
- C# / .NET 11。

## 项目结构
| 项目 | 说明 |
|---|---|
| Remot.Protocol | gRPC 契约(共享) |
| Remot.Server | 被控端,Windows 服务常驻 |
| Remot.Client | 客户端类库 |
| Remot.Cli | 命令行工具 `remot` |
| Remot.Mcp | MCP server,供 Claude Code 调用 |

## 快速开始

### 1. 被控端(每台 Windows 服务器,一次性)
发布后把 `publish/server/Remot.Server.exe` 拷到服务器,**以管理员身份**运行:
```
Remot.Server.exe install
```
它会:注册 Windows 服务(自动启动 + 失败重启)、开防火墙端口(默认 7070)、生成自签证书和 token、并打印一行配对串:
```
remot://pair#eyJ0...
```

### 2. 开发机登记目标
```
remot pair "remot://pair#eyJ0..."
```
(若自动探测的 IP 不对,用 `remot pair --host <真实IP> "<配对串>"` 覆盖。)

### 3. 使用
```
remot run    -t <目标> "nssm stop Service29298"
remot upload -t <目标> 本地文件 远程路径
remot download -t <目标> 远程路径 本地路径
```

### 4. agent 集成(Claude Code)
把 `publish/mcp/Remot.Mcp.exe` 注册为 MCP server(stdio)。Claude Code 自动获得三个工具:`remot_run`、`remot_upload`、`remot_download`,即可完成完整部署循环。

### 典型部署循环
```
run_command(["nssm stop Service29298"])
upload([{src:"./bin/Service.exe", dst:"C:\\服务目录\\Service.exe"}, ...])
run_command(["nssm start Service29298"])
```

## 开发
```
dotnet build
dotnet test
pwsh publish.ps1
```

.NET 11 preview SDK。`Remot.Server.exe`(无参)以控制台模式运行,便于调试。

## 安全
- token + TLS + 证书指纹锁定(防中间人,无需 CA)。
- LAN 环境使用;token 仅存于服务端 `server.json` 与客户端 `targets.json`。
