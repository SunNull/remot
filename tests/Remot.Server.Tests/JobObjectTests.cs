using Remot.Server.Execution;
using System.Diagnostics;
using Xunit;

namespace Remot.Server.Tests;

public class JobObjectTests
{
    [Fact]
    public async Task Disposing_job_kills_child_process_tree()
    {
        // cmd 起一个 ping 长跑子进程,验证关 JobObject 后子进程也消失
        var psi = new ProcessStartInfo("cmd.exe", "/c ping -t 127.0.0.1 > nul")
        { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true };
        var p = new Process { StartInfo = psi };

        using var job = new JobObject();
        p.Start();
        Assert.True(job.Assign(p.Handle));   // 启动后立即挂入 job
        Assert.False(p.HasExited);

        var childId = p.Id;
        job.Dispose();                       // 关 job → 整树杀

        await Task.Delay(300);
        bool stillAlive;
        try { stillAlive = !Process.GetProcessById(childId).HasExited; }
        catch (ArgumentException) { stillAlive = false; }            // 进程已不存在
        catch (System.ComponentModel.Win32Exception) { stillAlive = false; }
        Assert.False(stillAlive);
    }
}
