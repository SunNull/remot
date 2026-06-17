namespace Remot.Server.Execution;

public interface ICommandRunner
{
    /// <param name="onLine">每收到一行输出即异步回调(H1 流式);null=不流式。</param>
    Task<CommandRunResult> RunAsync(CommandSpec spec, CancellationToken ct = default, Func<StreamLine, Task>? onLine = null);
}

/// <summary>一行流式输出(isStderr 区分标准错误)。</summary>
public sealed record StreamLine(bool IsStderr, string Line);
