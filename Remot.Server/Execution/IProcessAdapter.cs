using System.Diagnostics;
namespace Remot.Server.Execution;

public interface IProcessAdapter : IDisposable
{
    int Id { get; }
    StreamWriter StandardInput { get; }
    void BeginOutputRead();
    void BeginErrorRead();
    event DataReceivedEventHandler OutputDataReceived;
    event DataReceivedEventHandler ErrorDataReceived;
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct);
    int ExitCode { get; }
    bool HasExited { get; }
    IntPtr Handle { get; }            // 用于挂 JobObject
    void KillEntireTree();            // JobObject 关闭即杀整树
}
