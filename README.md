# Remot

面向 AI agent 的 Windows 远程执行 + 文件传输工具。agent 一句话完成「停服务 → 上传新文件 → 启服务」,告别手工 mstsc / OpenSSH。

- **gRPC over HTTP/2 + TLS**:自签证书 + token,一行配对串完成目标登记。
- **远程命令执行**:批量(单进程多命令)、流式输出、结构化返回、超时杀进程树、UTF-8/中文、外部取消也杀树。
- **持久会话**:跨命令复用 shell 进程,保持 cwd/env,省启动开销。
- **文件传输**:流式分块、SHA256 校验(上传+下载)、跳过未改动、并发。
- **危险命令拦截**:黑名单(正则) + 服务/路径保护,全部在 server.json 可配。
- **审计日志**:每条命令记录(谁/什么/时间) + 拦截记录 + 会话生命周期。
- C# / **.NET 11**;被控端 Windows 服务常驻;客户端 MCP 工具供 Claude Code 调用。

---

## 项目结构

| 项目 | 说明 |
|---|---|
| `Remot.Protocol` | gRPC 契约(`remot.proto`)+ 共享工具(Hasher/ClipboardHelper) |
| `Remot.Server` | 被控端守护进程,Windows 服务常驻 |
| `Remot.Client` | 客户端类库(gRPC 调用 + 配置 + 配对 + 分块 + 会话) |
| `Remot.Mcp` | MCP server,供 Claude Code 调用(8 个工具) |

协议:`RunCommand`(服务端流)、`Upload`(客户端流)、`Download`(服务端流)、`CheckFile`(预检)、持久会话 `OpenSession`/`RunInSession`/`CloseSession`。

---

## 下载

从 [GitHub Releases](https://github.com/SunNull/remot/releases) 下载:
- **Remot.Server.zip** — 拷到 Windows 服务器
- **Remot.Mcp.zip** — 拷到 agent 机器

---

## 安装

### 服务端(每台 Windows 服务器,一次性)

```
解压 → 双击 Remot.Server.exe
```

```
╔══════════════════════════════════╗
║      Remot 服务端安装向导        ║
╚══════════════════════════════════╝

检测到已有配置(保留原有证书和配对串)。
重置安装(重新生成证书+token)? (y/N):    ← 回车 = 只更新 exe + 重启服务

▶ 停止旧服务 ...
▶ 安装到 C:\Program Files\Remot ...
▶ 保留原有配置(证书/token/配对串不变)
▶ 注册 Windows 服务 + 防火墙 ...
▶ 端口 7070 ✓ 已监听
✓ 全部完成!配对串已在剪贴板。
```

- **首次安装**:输入服务器名称(可选) → 自动 UAC → 生成证书 + token + 配对串。
- **更新 exe**:回车(不重置)→ 先按 PID 停旧进程 → 覆盖 exe → 重启服务。
- **重置安装**:输入 Y → 重新生成证书和 token(旧客户端需重新配对)。

### 客户端(agent 机器)

解压 `Remot.Mcp.exe`,配置 Claude Code(`~/.claude.json`):

```json
{
  "mcpServers": {
    "remot": {
      "command": "C:\\完整路径\\Remot.Mcp.exe",
      "args": ["--config", "C:\\Users\\你\\.remot\\targets.json"]
    }
  }
}
```

双击 `Remot.Mcp.exe` 可打印配置模板(含已登记目标列表 + Claude Code 配置 JSON)。

重启 Claude Code → 自动获得 8 个工具。

---

## 使用

### agent 工具(8 个)

| 工具 | 参数 | 说明 |
|---|---|---|
| `remot_pair` | pairing_string, name? | 配对新服务器 |
| `remot_list_targets` | — | 列出已配对的服务器 |
| `remot_run` | target, commands[], shell?, cwd? | 远程执行命令(批量合并一个 shell、流式) |
| `remot_upload` | target, files[{src,dst}] | 上传文件(并发+SHA256校验) |
| `remot_download` | target, remotePath, localPath | 下载文件(完整性校验) |
| `remot_open_session` | target, shell?, cwd? | 打开持久会话(跨命令保持 cwd/env) |
| `remot_run_in_session` | target, session_id, command, timeout? | 会话内执行单条命令(默认超时 30 秒) |
| `remot_close_session` | target, session_id | 关闭会话 |

### 典型部署循环

```
remot_run  → "nssm stop Service29298"
remot_upload → [所有编译产物 → 服务目录]
remot_run  → "nssm start Service29298"
```

全部 `exit=0` 即部署成功。

### 配对流程

1. 服务端安装后配对串在剪贴板(也在 `C:\ProgramData\Remot\pairing.txt`)。
2. 在 Claude Code 对话里贴配对串:`配对这个服务器: remot://pair#...`
3. agent 调 `remot_pair` 自动登记(配对串含服务器名称,便于区分多台)。

---

## 安全

| 层面 | 措施 |
|---|---|
| **传输** | TLS + 自签证书指纹锁定(防中间人) |
| **认证** | token + 定长时间比较(防时序侧信道) |
| **空 token** | 启动期拒绝(不会无认证运行) |
| **路径安全** | AllowedBasePaths 白名单(上传/下载/预检均校验) |
| **命令拦截** | server.json 配置黑名单(正则) + 受保护服务/路径 |
| **IP 白名单** | AllowedClientIPs |
| **凭证保护** | 配置文件 ACL 收紧 + 原子写 |
| **轮换** | `Remot.Server.exe rotate-token` 一键作废旧 token |
| **审计** | 每条命令 + 会话生命周期 + 拦截记录 |

### 危险命令拦截(server.json 配置)

```jsonc
{
  "BlockedCommands": ["\\bshutdown\\b", "\\bformat\\b\\s+[a-z]:", "taskkill.*SQLServer"],
  "ProtectedServices": ["RemotServer", "MSSQLSERVER"],
  "ProtectedPaths": ["C:\\Windows\\System32", "D:\\数据库"]
}
```

- 清空数组 = 关闭该类拦截
- 加正则 = 新增拦截规则
- 改完重启服务生效

### 配置文件位置

| 文件 | 位置 | 说明 |
|---|---|---|
| 服务端配置 | `C:\ProgramData\Remot\server.json` | token/证书/端口/黑名单/白名单 |
| 服务端配对串 | `C:\ProgramData\Remot\pairing.txt` | 首启或轮换后生成 |
| 服务端审计 | `C:\ProgramData\Remot\audit.log` | 命令+拦截+会话记录 |
| 客户端配置 | `~/.remot/targets.json` | 已配对的目标(host/token/指纹/名称) |

---

## 实战要点

| 情况 | 建议 |
|---|---|
| 含**中文**的命令或输出 | 用 `powershell`(默认);cmd 在中文 Windows 下可能乱码 |
| agent 不知有哪些服务器 | 调 `remot_list_targets` |
| 怀疑配对串泄露 | 服务端 `rotate-token`,agent 重新 `remot_pair` |
| 服务端更新 exe 卡住 | 管理员 cmd 手动:`sc delete RemotServer` → `taskkill /F /IM Remot.Server.exe` → copy → `sc create` |
| 会话用完 | 务必 `remot_close_session` 释放进程(30 分钟空闲也会自动清理) |

---

## 故障排查

- **`exit=-1` 且无输出**:shell 启动失败(`pwsh` 未安装)→ 用 `shell: "powershell"` 或 `"cmd"`。
- **中文乱码**:用 `powershell` 不用 `cmd`。
- **连不上/超时**:确认目标机 7070 端口可达 + 防火墙 + 服务运行(`sc query RemotServer`)。
- **SSL 证书被拒**:服务端重置过证书 → 重新 `remot_pair`(targets.json 指纹需更新)。
- **token 无效**:`rotate-token` 后旧客户端需重新配对。
- **服务端 exe 被占用无法覆盖**:`sc delete RemotServer` → `taskkill /F /IM Remot.Server.exe` → copy → `sc create`。
- **会话卡住**:查 `audit.log` 里的 session 日志(含超时诊断 + 部分 stdout/stderr);默认 30 秒超时自动终止。
- **NuGet 还原卡住**:`Remot.Protocol` 包版本固定为离线可用版本;网络不通时用国内镜像源。

---

## 开发

```
dotnet build       # 编译全方案
dotnet test        # 54 个测试(单元 + 端到端 + cmd 会话)
pwsh publish.ps1   # 发布单文件 exe(Server + MCP)
```

解决方案 `Remot.slnx`。`Directory.Build.props` 开启 `Nullable`、`TreatWarningsAsErrors`、`LangVersion=preview`。

---

## 架构要点

- **命令执行(非会话)**:每条命令独立进程(pwsh/powershell/cmd);PowerShell 多命令合并为单进程批量执行(sentinel 切分结果)。
- **命令执行(会话)**:持久 PowerShell 进程(`-Command -` 从 stdin 读);cmd 语法命令通过 `cmd /c` 包裹执行。sentinel 机制切分每条命令的输出与退出码。
- **进程树清理**:JobObject(`KILL_ON_JOB_CLOSE`)+ `entireProcessTree` 回退;超时/取消连根杀。
- **输出排空**:stdout/stderr 异步采集 + Channel 消费者 + EOF 排空,防丢尾部输出。
- **文件上传**:客户端流式哈希(零内存)→ CheckFile 预检跳过未改动 → 服务端临时文件 + SHA256 校验 + 原子 Move。
- **文件下载**:服务端先发 FileHeader(含 sha256)→ 客户端写临时文件 → 校验 → Move。
- **会话管理**:空闲 30 分钟自动清理 + 手动关闭;超时/sentinel 未收到返回部分输出 + 诊断信息。
