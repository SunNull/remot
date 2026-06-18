using System.Collections.Concurrent;

namespace Remot.Server.Execution;

/// <summary>优化3:持久会话池。session id → ShellSession(internal)。
/// 对外只暴露 Open/Exists/RunAsync/Close,不泄漏 ShellSession 类型。客户端用完应 CloseSession;服务退出时 Dispose 全部。
/// BUG-4:空闲会话自动清理(默认 30 分钟),防止客户端崩溃后 shell 进程泄漏。</summary>
public sealed class SessionManager : IDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (ShellSession Session, DateTime LastUsed)> _sessions = new();
    private readonly Timer _sweepTimer;

    public SessionManager()
    {
        _sweepTimer = new Timer(_ => SweepIdle(), null, SweepInterval, SweepInterval);
    }

    public string Open(string shell, string? cwd)
    {
        var id = Guid.NewGuid().ToString("N");
        _sessions[id] = (new ShellSession(shell, cwd), DateTime.UtcNow);
        return id;
    }

    public bool Exists(string id) => _sessions.TryGetValue(id, out var e) && !e.Session.IsClosed;

    public async Task<CommandRunResult> RunAsync(string id, string command, int? timeoutMs, CancellationToken ct, Func<StreamLine, Task>? onLine)
    {
        if (!_sessions.TryGetValue(id, out var e) || e.Session.IsClosed)
            return new CommandRunResult(-1, "", "", 0, false, "session 不存在或已关闭");
        _sessions[id] = (e.Session, DateTime.UtcNow);   // 刷新活跃时间
        return await e.Session.RunAsync(command, timeoutMs, ct, onLine);
    }

    public bool Close(string id)
    {
        if (_sessions.TryRemove(id, out var e)) { e.Session.Dispose(); return true; }
        return false;
    }

    private void SweepIdle()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        foreach (var kv in _sessions)
        {
            if (kv.Value.Session.IsClosed || kv.Value.LastUsed < cutoff)
            {
                if (_sessions.TryRemove(kv.Key, out var e))
                    try { e.Session.Dispose(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        _sweepTimer.Dispose();
        foreach (var s in _sessions.Values) s.Session.Dispose();
        _sessions.Clear();
    }
}
