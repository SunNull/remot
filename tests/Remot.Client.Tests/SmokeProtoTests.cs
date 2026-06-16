using Remot.Protocol;
using Xunit;

namespace Remot.Client.Tests;

public class SmokeProtoTests
{
    [Fact]
    public void Generated_types_exist()
    {
        var req = new CommandRequest { Shell = "pwsh" };
        req.Commands.Add(new Command { Text = "echo hi" });

        var client = typeof(RemotService.RemotServiceClient);
        var serverBase = typeof(RemotService.RemotServiceBase);

        Assert.NotNull(client);
        Assert.NotNull(serverBase);
        Assert.Equal("echo hi", req.Commands[0].Text);
    }
}
