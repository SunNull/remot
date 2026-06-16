namespace Remot.Server.Execution;

public sealed record CommandSpec(
    string Text,
    string Shell = "pwsh",
    string? Cwd = null,
    IReadOnlyDictionary<string, string>? Env = null,
    int? TimeoutMs = null,
    bool MergeStreams = false,
    long MaxOutputBytes = 5 * 1024 * 1024);
