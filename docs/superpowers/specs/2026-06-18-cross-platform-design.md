# Remot 跨平台设计文档(Windows + Linux)

- **日期**:2026-06-18
- **状态**:已确认,待实现
- **范围**:服务端 + MCP 客户端,Windows + Linux

---

## 1. 目标

在保持现有核心功能(命令执行 + 文件传输)的前提下,将 Remot 扩展到 Linux 平台。服务端和 MCP 客户端都能在 Windows 和 Linux 上运行。不重构现有代码,只抽象平台差异。

## 2. 非目标(YAGNI)

- macOS 支持(以后再说)
- Docker 容器化
- 负载均衡 / 多服务端集群
- Web UI

## 3. 设计原则

- **不重构,只抽象**:现有 Windows 代码不动,抽成接口,Linux 各写一套实现。
- **运行时选实现**:启动时按 `OperatingSystem.IsLinux()` / `IsWindows()` 选对应实现注入 DI。
- **协议最小变更**:只加一个 `GetInfo` RPC,现有 RPC 不改。
- **客户端透明**:agent 不需要知道目标机是什么系统;`remot_info` 查一下就知道。

## 4. 平台抽象层

### 4.1 目录结构

```
Remot.Server/Platform/
├── IProcessTreeKiller.cs
├── IServiceManager.cs
├── IFileProtector.cs
├── IShellRegistry.cs
├── IElevationChecker.cs
├── IPlatformPaths.cs
├── PlatformModule.cs            # DI 注册(按 OS 选实现)
├── Windows/
│   ├── JobObjectKiller.cs
│   ├── WindowsServiceManager.cs
│   ├── AclFileProtector.cs
│   ├── WindowsShellRegistry.cs
│   ├── UacElevationChecker.cs
│   └── WindowsPaths.cs
└── Linux/
    ├── ProcessGroupKiller.cs
    ├── SystemdServiceManager.cs
    ├── ChmodFileProtector.cs
    ├── LinuxShellRegistry.cs
    ├── SudoElevationChecker.cs
    └── LinuxPaths.cs
```

### 4.2 接口定义

#### IProcessTreeKiller — 进程树杀除

```csharp
interface IProcessTreeKiller
{
    /// <summary>创建一个跟踪器,与子进程绑定;Dispose 时杀整树。</summary>
    IDisposable Track(Process process);
}
```

| 平台 | 实现 | 机制 |
|---|---|---|
| Windows | `JobObjectKiller` | 现有 JobObject(P/Invoke kernel32,`KILL_ON_JOB_CLOSE`) |
| Linux | `ProcessGroupKiller` | 子进程 `setsid` 开新进程组,Dispose 时 `kill -PGID -KILL` |

Linux 实现要点:
- `Process.Start` 前 `psi.Environment["TERM"] = "dumb"`(避免 bash 等终端行为)
- 子进程启动后读取 `/proc/<pid>/pgid` 或用 `setsid` 命令包裹
- 杀树:`kill -KILL -<pgid>`(负 PGID 杀整组)
- 回退:`process.Kill(entireProcessTree: true)`(.NET 内置)

#### IServiceManager — 服务注册

```csharp
interface IServiceManager
{
    string ServiceName { get; }
    void Install(string exePath, int port);
    void Uninstall();
    void Stop();
    void Start();
}
```

| 平台 | 实现 | 机制 |
|---|---|---|
| Windows | `WindowsServiceManager` | 现有 sc.exe + netsh |
| Linux | `SystemdServiceManager` | 写 `/etc/systemd/system/remot.service` + `systemctl enable --now` |

Linux unit 文件模板:
```ini
[Unit]
Description=Remot Remote Execution & File Transfer
After=network.target

[Service]
Type=simple
ExecStart=/usr/local/bin/remot-server
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

Linux 不需要防火墙规则(iptables 通常默认开放;如需限制,管理员自行配)。

#### IShellRegistry — 可用执行环境探测

```csharp
record ShellInfo(string Name, string Path, bool SupportsPersistent);

interface IShellRegistry
{
    ShellInfo Default { get; }
    IReadOnlyList<ShellInfo> Available { get; }
    (string FileName, string Args) BuildCommand(string shell, string text);
    (string FileName, string Args) BuildPersistent(string shell);
    (string FileName, string Args) BuildBatch(IReadOnlyList<string> commands);
}
```

| 平台 | 探测 | 默认 |
|---|---|---|
| Windows | pwsh → powershell → cmd(现有 ShellDetector 逻辑) | pwsh 或 powershell |
| Linux | bash → sh → zsh → pwsh(如果装了) | bash |

Linux 命令构造:
- 单条:`("bash", "-c \"" + text + "\"")`
- 批量:`("bash", "-c '" + string.Join("\n", commands) + "; echo __END__:$?" + "'")`
- 会话:`("bash", "--noprofile --norc -i")`

Linux bash 会话与 PowerShell 的差异:
- bash `-i`(交互模式)从 stdin 逐行读取执行,**原生支持**,无 cmd /K 的 prompt 干扰问题
- sentinel 用 `$?` 替代 `$LASTEXITCODE`(bash 退出码语义)
- UTF-8 原生,无需编码转换

#### IFileProtector — 文件权限

```csharp
interface IFileProtector
{
    void Restrict(string path);
}
```

| 平台 | 实现 |
|---|---|
| Windows | ACL(现有 FileProtection:`Administrators` + `SYSTEM` + 当前用户) |
| Linux | `chmod 600` + `chown root:root`(仅 root 可读写) |

#### IElevationChecker — 提权检测

```csharp
interface IElevationChecker
{
    bool IsElevated();
}
```

| 平台 | 实现 |
|---|---|
| Windows | `WindowsIdentity` + `WindowsPrincipal.IsInRole(Administrator)` |
| Linux | `getuid() == 0`(P/Invoke libc 或 `Environment.UserName == "root"`) |

#### IPlatformPaths — 路径

```csharp
interface IPlatformPaths
{
    string DataDir { get; }       // 服务端配置/证书/日志
    string ExeDir { get; }       // 安装目录
    string ClientConfigDir { get; } // 客户端 targets.json
}
```

| 路径 | Windows | Linux |
|---|---|---|
| DataDir | `%ProgramData%\Remot\` | `/etc/remot/` |
| ExeDir | `C:\Program Files\Remot\` | `/usr/local/bin/`(或 `/opt/remot/`) |
| ClientConfigDir | `~/.remot/` | `~/.config/remot/` |

## 5. 协议变更

### 新增 RPC

```protobuf
rpc GetInfo(google.protobuf.Empty) returns (ServerInfo);

message ServerInfo {
  string os = 1;                              // "windows" / "linux"
  string server_version = 2;
  repeated ShellInfo environments = 3;
  string default_environment = 4;
}

message ShellInfo {
  string name = 1;                            // "bash" / "pwsh" / "cmd"
  string path = 2;                            // 可执行路径
}
```

### 现有 RPC

不变。`shell` 字段已有,值从 `"pwsh"/"powershell"/"cmd"` 扩展到任意(`"bash"/"sh"/"zsh"` 等)。服务端按 `IShellRegistry` 校验。

## 6. 服务端改动

### Program.cs

- 注入 `PlatformModule`(按 OS 选所有平台实现)
- 安装向导:`IElevationChecker` 替代 `IsElevated()`;Linux 用 sudo 检测,提示 `sudo ./remot-server install`
- 服务管理:`IServiceManager` 替代直接调 sc.exe
- 路径:`IPlatformPaths` 替代硬编码 `%ProgramData%`

### ProcessFactory

- 删掉硬编码 shell 路径,改为从 `IShellRegistry.BuildCommand()` 获取
- 编码:Linux 无需 `chcp 65001`(默认 UTF-8)
- 进程树:`IProcessTreeKiller.Track(process)` 替代直接 `new JobObject()`

### ShellSession

- 从 `IShellRegistry.BuildPersistent()` 获取进程启动参数
- Linux bash `-i` 模式:原生逐行读取,sentinel 用 `$?` 替代 `$LASTEXITCODE`
- 编码:`new UTF8Encoding(false)` 两平台通用

### FileProtection

- 改用 `IFileProtector.Restrict()`,实现各平台一套

### ServiceInstaller

- 改为 `IServiceManager`,Windows 用现有代码,Linux 写 systemd

## 7. MCP/客户端改动

### 新增 remot_info 工具

```csharp
[McpServerTool]
public async Task<string> remot_info(string target)
{
    var r = await _client.GetInfoAsync(target);
    // 返回:os=Linux, environments=[bash, sh, pwsh], default=bash
}
```

### ClipboardHelper

- Linux 跳过(返回 null/空),客户端回退到手动粘贴配对串
- 未来可加 `xclip` / `wl-copy` 支持

### Config 路径

- `TargetsConfig` / `ServerConfig` 用 `IPlatformPaths.ClientConfigDir`(MCP 侧)

### MCP 工具描述

- shell 参数描述加 `"bash"`: `"shell: bash/pwsh/powershell/cmd(留空=服务端自动)"`

## 8. 速度对比

| 环节 | Windows | Linux | 提升 |
|---|---|---|---|
| shell 启动 | pwsh ~300ms / powershell ~500ms | bash ~5ms | 60-100x |
| 编码转换 | chcp 65001 + UTF-8 转换 | 无(原生 UTF-8) | 省一层 |
| 更新 exe | 停进程 → 等文件释放 → 覆盖 → 重启 | 直接覆盖(不锁) | 无停机 |
| 服务停止 | STOP_PENDING → 按 PID 杀 | systemctl stop(瞬时) | 无等待 |
| 文件传输 | gRPC + 流式(现有) | 相同 | 无差异 |

## 9. 安装流程对比

### Windows(不变)

```
双击 Remot.Server.exe → Y → UAC 提权 → 安装服务 → 配对串到剪贴板
```

### Linux(新增)

```bash
# 下载
curl -L https://github.com/SunNull/remot/releases/latest/download/remot-server-linux -o /usr/local/bin/remot-server
chmod +x /usr/local/bin/remot-server

# 安装(需要 sudo)
sudo remot-server install

# 输出:
# ✓ systemd 服务已注册
# ✓ 配对串: remot://pair#...
```

Linux 安装向导:
- 检测 `getuid() == 0`,非 root 提示 `sudo`
- 写 `/etc/systemd/system/remot.service`
- `systemctl daemon-reload && systemctl enable --now remot`
- 打印配对串到终端(Linux 无剪贴板,复制即可)

## 10. 发布

```bash
# Windows(现有)
dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true

# Linux(新增)
dotnet publish -r linux-x64 --self-contained -p:PublishSingleFile=true
```

GitHub Release 增加一个附件:`Remot.Server-linux-x64`(单文件)。

MCP 同理:`Remot.Mcp-linux-x64`。

## 11. 测试

### 单元测试

- 每个平台接口的实现各自测试(Windows 实现在 Windows CI 跑,Linux 实现在 Linux CI 跑)
- Mock 接口测试上层逻辑(CommandRunner / ShellSession 不直接依赖具体实现)

### 集成测试

- `E2ETests`:现有 Windows 端到端不变
- 新增 `LinuxE2ETests`(Linux CI):bash 命令执行 + 文件传输回路 + GetInfo

### GitHub Actions

```yaml
matrix:
  - os: windows-latest
  - os: ubuntu-latest
```

## 12. 不涉及的部分

- gRPC 协议层:已跨平台,不改
- 文件传输逻辑(客户端 FileChunker + 服务端 FileReceiver):已跨平台,不改
- Hasher:已跨平台,不改
- CommandGuard:已从配置驱动,不改
- 审计日志:已跨平台,不改

## 13. 实现顺序

1. 抽接口 + Windows 实现迁入(不改行为,纯重构)
2. Linux 实现(进程组 + systemd + bash)
3. 协议加 GetInfo RPC + remot_info 工具
4. 路径/配置/剪贴板适配
5. Linux CI + 集成测试
6. 发布 linux-x64 产物
