namespace Remot.Server.Execution;

public interface ICommandRunner
{
    Task<CommandRunResult> RunAsync(CommandSpec spec, CancellationToken ct = default);
}
