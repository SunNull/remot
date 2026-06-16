namespace Remot.Server.Execution;
public interface IProcessFactory
{
    IProcessAdapter Start(CommandSpec spec);
}
