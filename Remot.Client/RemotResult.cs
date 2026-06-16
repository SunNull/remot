namespace Remot.Client;

public readonly record struct RemotResult<T>(bool Ok, T? Value, string? Error)
{
    public static RemotResult<T> Success(T v) => new(true, v, null);
    public static RemotResult<T> Fail(string err) => new(false, default, err);
}
