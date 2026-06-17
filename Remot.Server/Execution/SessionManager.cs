using System.Collections.Concurrent;

namespace Remot.Server.Execution;

/// <summary>优化3:持久会话池。session id → ShellSession(internal)。
/// 对外只暴露 Open/Exists/RunAsync/Close,不泄漏 ShellSession 类型。客户端用完应 CloseSession;服务退出时 Dispose 全部。</summary>
public sealed class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ShellSession> _sessions = new();

    public string Open(string shell, string? cwd)
    {
        var id = Guid.NewGuid().ToString("N");
        _sessions[id] = new ShellSession(shell, cwd);
        return id;
    }

    public bool Exists(string id) => _sessions.TryGetValue(id, out var s) && !s.IsClosed;

    public async Task<CommandRunResult> RunAsync(string id, string command, int? timeoutMs, CancellationToken ct, Func<StreamLine, Task>? onLine)
    {
        if (!_sessions.TryGetValue(id, out var s) || s.IsClosed)
            return new CommandRunResult(-1, "", "", 0, false, "session 不存在或已关闭");
        return await s.RunAsync(command, timeoutMs, ct, onLine);
    }

    public bool Close(string id)
    {
        if (_sessions.TryRemove(id, out var s)) { s.Dispose(); return true; }
        return false;
    }

    public void Dispose()
    {
        foreach (var s in _sessions.Values) s.Dispose();
        _sessions.Clear();
    }
}
