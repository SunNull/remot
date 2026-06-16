using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Remot.Server.Security;

/// <summary>校验请求元数据里的 authorization token(格式:Bearer &lt;token&gt;)。</summary>
public sealed class TokenInterceptor : Interceptor
{
    public const string Header = "authorization";
    private const string Prefix = "Bearer ";
    private readonly string _expectedToken;

    public TokenInterceptor(string expectedToken) => _expectedToken = expectedToken;

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        EnsureAuthenticated(context);
        return await continuation(request, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        EnsureAuthenticated(context);
        await continuation(request, responseStream, context);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        EnsureAuthenticated(context);
        return await continuation(requestStream, context);
    }

    private void EnsureAuthenticated(ServerCallContext context)
    {
        var auth = context.RequestHeaders.GetValue(Header);
        var token = auth is not null && auth.StartsWith(Prefix, StringComparison.Ordinal)
            ? auth[Prefix.Length..]
            : auth;
        if (!string.Equals(token, _expectedToken, StringComparison.Ordinal))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "invalid token"));
    }
}
