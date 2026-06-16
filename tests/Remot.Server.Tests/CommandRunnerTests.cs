using Remot.Server.Execution;
using Remot.Server.Tests.Fakes;
using Xunit;

namespace Remot.Server.Tests;

public class CommandRunnerTests
{
    [Fact]
    public async Task Captures_stdout_and_stderr_separately_and_exit_code()
    {
        var fake = new FakeFactory();
        var runner = new CommandRunner(fake);

        var t = runner.RunAsync(new CommandSpec("echo"));
        fake.Process.EmitStdout("hello-out");
        fake.Process.EmitStderr("hello-err");
        fake.Process.ExitCode = 7;
        fake.Process.Complete();
        var r = await t;

        Assert.Equal(7, r.ExitCode);
        Assert.Contains("hello-out", r.Stdout);
        Assert.Contains("hello-err", r.Stderr);
        Assert.False(r.TimedOut);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task Timeout_kills_entire_tree_and_marks_timed_out()
    {
        var fake = new FakeFactory { Process = { NeverExits = true } };
        var runner = new CommandRunner(fake);

        var r = await runner.RunAsync(new CommandSpec("long", TimeoutMs: 100));

        Assert.True(r.TimedOut);
        Assert.True(fake.Process.TreeKilled);
    }

    [Fact]
    public async Task Start_failure_returns_structured_error_not_throw()
    {
        var fake = new FakeFactory { ThrowOnStart = true };
        var runner = new CommandRunner(fake);

        var r = await runner.RunAsync(new CommandSpec("bad"));

        Assert.NotNull(r.Error);
        Assert.Contains("无法启动", r.Error);
    }

    [Fact]
    public async Task Large_output_is_truncated_with_marker()
    {
        var fake = new FakeFactory();
        var runner = new CommandRunner(fake);

        var t = runner.RunAsync(new CommandSpec("big", MaxOutputBytes: 16));
        fake.Process.EmitStdout(new string('A', 100));
        fake.Process.Complete();
        var r = await t;

        Assert.True(r.Stdout.Length < 100);
        Assert.Contains("truncated", r.Stdout, StringComparison.OrdinalIgnoreCase);
    }
}
