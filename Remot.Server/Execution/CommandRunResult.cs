namespace Remot.Server.Execution;

public sealed record CommandRunResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    long DurationMs,
    bool TimedOut,
    string? Error);
