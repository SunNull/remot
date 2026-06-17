# Changelog

## v1.0.0 — 2026-06-17

首个可用版本。面向 AI agent 的 Windows 远程执行 + 文件传输工具,替代手工 mstsc / OpenSSH 部署循环。

### 功能
- **gRPC over HTTP/2 + TLS**:自签证书 + token,一行配对串(`remot://pair#...`)完成目标登记,无需 CA。
- **远程命令执行**:批量、结构化返回(exit_code/stdout/stderr/超时)、**流式实时输出**、超时连根杀进程树(JobObject)、UTF-8/中文不乱码、外部取消也杀树。
- **文件传输**:流式分块、SHA256 完整性校验(上传+下载)、跳过未改动预检、**并发上传(8)**、大输出截断。
- **Windows 服务常驻**:自动启动 + 失败重启 + 防火墙自动开;`install` / `uninstall` / `status` / `rotate-token`。
- **CLI** `remot pair/run/upload/download/target`(async + Ctrl+C 取消 + `--deadline`)。
- **MCP server**:暴露 `remot_run` / `remot_upload` / `remot_download` 给 Claude Code,一句话完成部署循环。

### 安全
- 路径白名单校验(`PathValidator` + `AllowedBasePaths`,防穿越/越界写)。
- token 启动期校验(空 token 拒绝启动)+ 定长时间比较(防时序侧信道)。
- 配置文件 ACL 收紧(仅当前用户/管理员/SYSTEM)+ 原子写。
- 客户端 IP 白名单(`AllowedClientIPs`)+ `rotate-token` 一键轮换(缓解配对串泄露)。
- 配对串落盘 `pairing.txt`(服务态可取回)+ 命令审计日志 `audit.log`。

### 质量保证
- **29 个测试全绿**(单元 + 真实 gRPC 端到端:run/批量/upload/download/完整性),0 警告 0 错误。
- 经第三方代码审查(40 条:6 严重 / 9 高 / 13 中 / 12 低)**全量处理**。

### 已知限制
- JobObject 挂载存在理论竞态窗口(L3,需 CREATE_SUSPENDED 才能彻底消除;当前靠立即挂入 + `entireProcessTree` 回退兜底,实际风险极低)。
- 配对串即明文凭证(未做握手/会话 token 架构);以 IP 白名单 + 轮换 + 落盘警示缓解,日常不增加配置负担。
- 客户端上传将文件读入内存(超大文件注意并发内存占用)。
- NuGet 包版本固定为缓存版本以保证离线还原;升级需网络可达。
