namespace Remot.Server.Execution;

public interface ICommandRunner
{
    /// <param name="onLine">每收到一行输出即异步回调(H1 流式);null=不流式。</param>
    Task<CommandRunResult> RunAsync(CommandSpec spec, CancellationToken ct = default, Func<StreamLine, Task>? onLine = null);

    /// <summary>优化2:把多条命令拼成一个脚本,在单个 shell 进程里顺序执行,sentinel 切分每条结果。
    /// 省 (N-1) 次 shell 启动。仅 PowerShell(pwsh/powershell)。</summary>
    /// <param name="onLine">(命令序号, 行) 回调。</param>
    Task<IReadOnlyList<CommandRunResult>> RunBatchAsync(IReadOnlyList<CommandSpec> specs, CancellationToken ct = default, Func<int, StreamLine, Task>? onLine = null);
}

/// <summary>一行流式输出(isStderr 区分标准错误)。</summary>
public sealed record StreamLine(bool IsStderr, string Line);
