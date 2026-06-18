using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Remot.Server.Execution;

/// <summary>优化3:持久 shell 会话。一个常驻 powershell/pwsh(或 cmd)进程,命令从 stdin 投入,
/// sentinel 切分每条命令的输出与退出码。跨请求复用,省 shell 启动开销,保持 cwd/env。
/// 注意:命令超时会终止整个会话(持久进程内无法只杀单条命令);非线程安全,由 SessionManager 串行化。</summary>
internal sealed class ShellSession : IDisposable
{
    private static readonly Regex SentinelRe = new(@"^(?<id>__REMOT_END_\d+__):(?<code>-?\d+)\s*$", RegexOptions.Compiled);

    private readonly Process _proc;
    private readonly string _shell;
    private readonly JobObject? _job;
    private readonly Channel<(bool IsStderr, string Line)> _channel;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _seq;
    private bool _disposed;
    public bool IsClosed => _disposed;

    public ShellSession(string shell, string? cwd)
    {
        _shell = shell.ToLowerInvariant();
        var (fileName, args) = _shell switch
        {
            "cmd" => ("cmd.exe", "/Q /K"),
            "pwsh" => ("pwsh.exe", "-NoProfile -NonInteractive -Command -"),
            _ => ("powershell.exe", "-NoProfile -NonInteractive -Command -"),
        };
        var psi = new ProcessStartInfo(fileName, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrEmpty(cwd) ? Environment.CurrentDirectory : cwd!,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        _proc = new Process { StartInfo = psi };
        _proc.Start();
        try { _job = new JobObject(); if (!_job.Assign(_proc.Handle)) { _job.Dispose(); _job = null; } }
        catch { _job = null; }
        _proc.StandardInput.AutoFlush = true;
        // powershell 5.1 默认按 GBK 读 stdin(中文乱码),启动后立即切 UTF-8;pwsh 默认 UTF-8 无需
        if (_shell == "powershell")
            SafeWriteLine("[Console]::InputEncoding=[Text.Encoding]::UTF8; [Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; chcp 65001 > $null");

        _channel = Channel.CreateUnbounded<(bool, string)>(new UnboundedChannelOptions { SingleWriter = false });
        _ = Task.Run(() => ReadLoop(_proc.StandardOutput, false));
        _ = Task.Run(() => ReadLoop(_proc.StandardError, true));
    }

    private async Task ReadLoop(StreamReader reader, bool isStderr)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
                await _channel.Writer.WriteAsync((isStderr, line));
        }
        catch { }
        finally { _channel.Writer.TryComplete(); }
    }

    public async Task<CommandRunResult> RunAsync(string command, int? timeoutMs, CancellationToken ct, Func<StreamLine, Task>? onLine)
    {
        if (_disposed) return new CommandRunResult(-1, "", "", 0, false, "session 已关闭");
        await _lock.WaitAsync(ct);
        try
        {
            if (_disposed || _proc.HasExited) return new CommandRunResult(-1, "", "", 0, false, "session 进程已退出");

            // BUG-3:发命令前排空 channel 残留(上条命令的延迟输出),避免归到本条
            while (_channel.Reader.TryRead(out _)) { }

            var seq = Interlocked.Increment(ref _seq);
            var sentinel = $"__REMOT_END_{seq}__";

            if (_shell != "cmd") SafeWriteLine("$LASTEXITCODE = $null");   // 重置,避免上条外部程序退出码污染本条判断
            SafeWriteLine(command);
            SafeWriteLine(_shell == "cmd"
                ? $"echo {sentinel}:%errorlevel%"
                : $"$remot_ec = if (-not $?) {{ 1 }} elseif ($null -ne $LASTEXITCODE) {{ $LASTEXITCODE }} else {{ 0 }}; Write-Output ('{sentinel}:' + $remot_ec)");

            var sw = Stopwatch.StartNew();
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            int exitCode = -1;
            bool found = false;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeoutMs is int ms && ms > 0) timeoutCts.CancelAfter(ms);

            try
            {
                await foreach (var (isStderr, line) in _channel.Reader.ReadAllAsync(timeoutCts.Token))
                {
                    var m = SentinelRe.Match(line);
                    if (m.Success && m.Groups["id"].Value == sentinel)
                    {
                        exitCode = int.TryParse(m.Groups["code"].Value, out var c) ? c : -1;
                        found = true;
                        break;
                    }
                    if (isStderr)
                    {
                        stderr.AppendLine(line);
                        // BUG-2:stderr 也流式推送
                        if (onLine is not null) try { await onLine(new StreamLine(true, line)); } catch { }
                    }
                    else
                    {
                        stdout.AppendLine(line);
                        if (onLine is not null) try { await onLine(new StreamLine(false, line)); } catch { }
                    }
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                Dispose();   // 超时:持久进程无法只杀单条命令,终止整个会话
                return new CommandRunResult(-1, stdout.ToString(), stderr.ToString(), sw.ElapsedMilliseconds, true, "timeout(会话已终止)");
            }

            sw.Stop();
            return new CommandRunResult(found ? exitCode : -1, stdout.ToString(), stderr.ToString(), sw.ElapsedMilliseconds, false, found ? null : "session 异常结束(未收到结束标记)");
        }
        finally { _lock.Release(); }
    }

    private void SafeWriteLine(string s) { try { _proc.StandardInput.WriteLine(s); } catch { } }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _proc.StandardInput.Close(); } catch { }
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        _job?.Dispose();
        _proc.Dispose();
    }
}
