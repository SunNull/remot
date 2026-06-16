# Remot 设计文档

- **日期**:2026-06-16
- **状态**:已确认,待实现
- **作者**:brainstorming 会话产出

## 1. 背景与目标

当前在多台 Windows 测试服务器上频繁执行「编译 → 停 NSSM 服务 → 替换文件 → 启 NSSM 服务」的部署循环。现有手段依赖 mstsc 远程桌面手动操作,或配置繁琐且不稳定的 OpenSSH。本项目的目标是构建一个**面向 AI agent 的远程执行 + 文件传输工具**,替代这些手工流程。

**核心诉求**:

1. 彻底告别 OpenSSH 在 Windows 上的配置地狱与不稳定。
2. 远程执行任意命令(单行 / 多行脚本块 / 调用脚本文件),返回**结构化结果**。
3. 文件传输:从开发机把具体文件推到服务器(替代 mstsc 复制粘贴)。
4. 最终供 AI agent(Claude Code,经 MCP)调用,实现部署循环全自动化。

**非目标(YAGNI)**:

- 不做 GUI 客户端。
- 不做反向连接 / NAT 打洞(同局域网、直连可达)。
- 不做内建的「部署」组合操作 + 回滚——部署编排由 agent 用 `run_command` + `upload` 原语自行完成。
- 不做多用户 / 权限分级(单 token 共享)。

## 2. 典型场景(驱动设计)

服务以 NSSM 注册,命名规律 `Service<端口>`(如 `Service29298`)。一次部署:

```
run_command(["nssm stop Service29298"])
upload([{src:"./bin/Service.exe", dst:"C:\\服务目录\\Service.exe"}, ...])   // 覆盖
run_command(["nssm start Service29298"])
```

agent 自动拼出这三步即可完成部署。

## 3. 技术栈

- **运行时**:.NET 11 preview(锁定具体 preview SDK 版本),`<LangVersion>preview</LangVersion>`,启用 nullable,保持 AOT/trim 友好。
- **语言**:C# 14/15 最新特性。充分使用 `field` 半自动属性、extension members、collection expressions(`[..]`)、primary constructors、`required`、`record` DTO、file-scoped namespaces、global usings、`params` 集合、`System.Threading.Lock`、`TimeProvider`。
- **协议**:**gRPC over HTTP/2 + TLS**。选它的原因:
  - HTTP/2 多路复用 → 一条连接并发跑多命令 / 多文件上传(快)。
  - 双向流式 RPC → 命令输出实时推、文件分块流式传(零全量缓冲)。
  - 强类型契约(`.proto` 代码生成),client/server 共用。
  - .NET 里 gRPC 服务端唯一路径是 `Grpc.AspNetCore`(Kestrel);旧 `Grpc.Core`(C-core)已被弃用并移除。
- **服务端宿主**:ASP.NET Core 的 **Kestrel** + `Grpc.AspNetCore`;以 **Windows 服务**方式常驻(`Microsoft.Extensions.Hosting.WindowsServices`)。

## 4. 整体架构

一个解决方案,五个项目:

```
Remot.sln
├─ Remot.Protocol   // .proto + 生成的 client/server stub(共享契约)
├─ Remot.Server     // 被控端守护进程,跑在每台 Windows 服务器
├─ Remot.Client     // 客户端类库(gRPC 调用 + 配置 + 重试 + 配对解析)
├─ Remot.Cli        // 控制台入口(remot run / upload / download / pair / target),依赖 Remot.Client
└─ Remot.Mcp        // MCP server(stdio),Claude Code 通过它调用,依赖 Remot.Client
```

数据流:

```
Claude Code ──stdio/JSON-RPC──▶ Remot.Mcp ─┐
                                            ├─(均依赖)──▶ Remot.Client ──gRPC/HTTP2+TLS──▶ Remot.Server(各目标机)
命令行用户 ────────────────────▶ Remot.Cli ─┘
```

每个项目职责单一、可独立测试。`Remot.Client` 是薄类库(封装 gRPC 调用 + 配置 + 重试 + 配对解析),`Remot.Cli` 与 `Remot.Mcp` 都依赖它,不重复协议逻辑。

## 5. 协议设计

```protobuf
service RemotService {
  rpc RunCommand (CommandRequest)   returns (stream CommandOutput);  // 服务端流:实时输出
  rpc Upload     (stream FileChunk) returns (TransferResult);        // 客户端流:分块上传
  rpc Download   (FileRequest)      returns (stream FileChunk);      // 服务端流:分块下载
}

message CommandRequest {
  repeated Command commands = 1;          // 批量,每条可任意复杂
  string shell = 2;                       // "pwsh"|"powershell"|"cmd",默认 pwsh
  string cwd = 3;                         // 工作目录,可选
  map<string,string> env = 4;             // 环境变量,可选
  int32 timeout_ms = 5;                   // 单条命令超时,可选
  bool merge_streams = 6;                 // stdout/stderr 是否按到达时间合并
}
message Command { string text = 1; }      // 单行或多行脚本块

message CommandOutput {
  oneof kind {
    StreamChunk   chunk  = 1;             // 增量输出(带 stdout/stderr 标记)
    CommandResult result = 2;             // 每条命令一条结果,按 index 对应
  }
}
message CommandResult {
  int32 index = 1;                        // 对应 commands 数组下标
  int32 exit_code = 2;
  string stdout = 3;
  string stderr = 4;
  int64 duration_ms = 5;
  bool timed_out = 6;
  string error = 7;                       // 进程启动失败等结构化错误
}

message FileChunk {
  oneof kind {
    FileHeader header = 1;                // 首块:目标路径 / 期望 sha256 / size
    bytes data = 2;                       // 1~4MB 分块
  }
}
message FileHeader {
  string dest_path = 1;
  string expected_sha256 = 2;
  int64 size = 3;
  bool overwrite = 4;                     // 默认 true
}
message TransferResult { bool ok = 1; string dest = 2; int64 bytes = 3; string error = 4; }
message FileRequest { string path = 1; }
```

## 6. 命令执行的健壮性(重点设计)

这是核心质量要求,逐条兜底:

- **绝不死锁**:用 `System.Diagnostics.Process` 异步读 stdout/stderr(`RedirectStandardOutput/Error` + `ReadAsync` / `BeginOutputReadLine`)。永远不出现「输出塞满管道 → 主进程阻塞」的经典死锁。
- **超时/取消连根拔起**:用 Windows **Job Object**(`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`)包裹派生进程。超时或客户端连接断开时整个进程树一起被杀——包括 `nssm` 拉起的子服务进程,不留孤儿。超时控制用 `CancellationToken` + `TimeProvider`(可测试)。
- **编码不乱码**:中文 Windows 的 GBK 控制台输出是重灾区。强制以 UTF-8 读取(`StandardOutputEncoding = Encoding.UTF8`),对 PowerShell 预置 `[Console]::OutputEncoding`,必要时 `chcp 65001`;提供 `encoding` 参数兜底。返回的是已正确解码的字符串。
- **可靠退出码**:`WaitForExitAsync` 后取 `ExitCode`;进程未启动(找不到 exe / 权限不足)时返回结构化 `error`,而不是抛异常把 agent 干崩。
- **大输出保护**:stdout/stderr 各设上限(默认 5MB,可配),超限截断并追加 `...[truncated, total N bytes]` 标记,防止把 agent 上下文撑爆。`merge_streams` 可选按到达时间合并输出。
- **shell 可选**:`pwsh`(默认)/ `powershell` / `cmd` 明确指定。

## 7. 文件传输的快速(重点设计)

针对「高频部署、一次几十个文件」场景:

- **流式分块**:1~4MB 的 chunk 直接 pipe 到目标文件流,**零全量缓冲**(不把整个文件读进内存)。
- **并发上传**:HTTP/2 多路复用,一条连接上同时传多个文件(默认并发 8)。整批文件的总耗时 ≈ 最慢一个,不是串行累加。
- **跳过未改动**:上传前发 `{path, size, sha256}` 预检,服务端比对一致即跳过。高频重复部署时大部分文件没变,传输量降到接近 0。
- **完整性校验**:服务端写完算 SHA256 比对,不一致直接报错并清理半成品。
- **可选压缩**:文本/日志类走 gRPC 内置 gzip;二进制(dll/exe)不压(压不动反费 CPU),按文件类型决定。
- **持久连接**:每目标一条常驻 HTTP/2 连接,免重复握手。
- **进度**:CLI 实时显示 bytes/total;agent 拿最终的 `{ok, dest, bytes}`。

## 8. 被控端部署与常驻

- **发布形态**:单文件自包含 exe(可选 NativeAOT,若 .NET 11 下 gRPC 兼容则启用,否则单文件 trimmed)。
- **常驻方式 = Windows 服务**(不是控制台窗口):
  - `.NET` 原生支持服务:`builder.Services.AddWindowsService(o => o.ServiceName = "RemotServer")`。
  - 跑在系统 session 0(无界面、无窗口),由 SCM 托管。
  - 开机自启、失败自动重启(配置 recovery:1/2/后续失败递增重启)。
  - 关窗口、注销用户、重启服务器均不影响。
- **一键安装**:`Remot.Server.exe install` 自动完成:
  1. 用 `sc create` / `ServiceController` 注册为 Windows 服务,设自动启动 + 失败重启策略。
  2. **自动开防火墙端口**(默认 7070),无需手动配 Windows 防火墙。
  3. 启动服务。
  4. 首次启动时在 `%ProgramData%\Remot\server.json` 自动生成 **token** + **自签 TLS 证书**。
  5. 打印一行**配对串**(见第 9 节)。
- **控制命令**:`install | uninstall | start | stop | status | run`(无参 / `run` = 控制台调试模式,此时关窗口才会退出)。
- **可观测性**:日志写 `%ProgramData%\Remot\logs\` + Windows 事件日志;`sc query RemotServer` / `Get-Service` 看状态。

## 9. 配对(Pairing)——把认证配置压成一行

认证用 **token + TLS + 证书指纹锁定**(无 CA 也能防中间人),但用户全程不直接接触这些字段:

- 服务端 `install` 打包 `host + 端口 + token + 证书指纹` 成一行配对串:
  ```
  remot://pair#eyJ0IjoidGVzdDI5Mjk4IiwiaG9zdCI6...
  ```
- 开发机执行一次:
  ```
  remot pair "remot://pair#eyJ0..."
  ```
  该命令自动完成 TLS 连接、证书指纹锁定、token 存储,并写入 `remot.targets.json`。

最终服务端体验 = 拷 exe + 跑一次 `install` + 复制打印的一行。开发机体验 = 每台服务器 `remot pair` 一次。比 OpenSSH 少 ACL / sshd_config / known_hosts 三个坑。

## 10. 配置与目标管理

开发机 `remot.targets.json`(由 `pair` 自动生成,通常不需手编):

```jsonc
{
  "targets": {
    "test29298": { "host": "192.168.1.20", "port": 7070, "token": "…", "certFingerprint": "…" },
    "prod29300": { "host": "192.168.2.15", "port": 7070, "token": "…", "certFingerprint": "…" }
  }
}
```

CLI 汇总:`remot target add|list|remove`、`remot pair`、`remot run -t <name> "<cmd>"`、`remot upload -t <name> <src> <dst>`、`remot download -t <name> <remotePath> <localPath>`。

## 11. agent 集成(MCP)

`Remot.Mcp` 作为 stdio MCP server,Claude Code 自动发现并暴露三个工具:

- `remot_run(target, commands[], shell?, cwd?, timeout_ms?)` → 每条命令结构化结果。
- `remot_upload(target, files[{src, dst}])` → 每个文件结果。
- `remot_download(target, remotePath, localPath)` → 结果。

使用 C# 官方 MCP SDK(`ModelContextProtocol` NuGet),依赖 `Remot.Client`。

## 12. 错误处理

全部返回结构化结果,不向 agent 抛异常:

- 连接失败 → 带目标名 + 原因。
- 命令超时 → 带已收到的部分输出 + `timed_out: true`。
- 进程未启动 → 结构化 error。
- 上传校验失败 → 带哪个文件 + 清理半成品。

客户端统一 `RemotResult<T>`(成功/失败二态 + 错误码 + 消息 + 详情)。服务端用 gRPC interceptor 做 token 校验,校验失败返回标准 `UNAUTHENTICATED` 状态码。

## 13. 安全

- LAN 环境,但「远程 shell」权限极高。
- 基线:**token(请求头)+ TLS + 证书指纹锁定**(防中间人,无需 CA)。
- 可选:来源 IP 白名单。
- token 仅存于服务端 `server.json` 与客户端 `remot.targets.json`,传输只走 TLS。
- 不提供命令白名单(保持全 shell 能力,靠 token 保密 + 网络边界)。

## 14. 测试策略

xUnit。重点单测:

- **命令执行器**:用接口抽象 `Process`(可注入假进程),验证超时杀进程树、编码解码、大输出截断、退出码、启动失败结构化错误。
- **分块上传**:chunker 切分、SHA256 校验、半成品清理。
- **跳过未改动预检**:size+sha 命中跳过的逻辑。
- **配对串**:打包 / 解析往返一致性。
- **目标配置**:`remot.targets.json` 解析与 `pair` 写入。

集成测试:本机起一个 Server loopback,跑真实 `cmd /c echo` 与小文件上传 / 下载往返,验证端到端。

## 15. 待后续决策(实现期细化)

- NativeAOT 是否启用:取决于 .NET 11 preview 下 gRPC server 的 AOT 兼容性,实现期验证。
- 并发上传数(默认 8)与 chunk 大小(默认 2MB):实现期做一次本地基准调参。
- 证书自签 vs 内置 CA:当前选自签 + 指纹锁定,若后续需要可换内置 CA 统一发证。
