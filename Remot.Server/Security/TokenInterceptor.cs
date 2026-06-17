using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Remot.Server.Security;

/// <summary>校验请求元数据里的 authorization token(格式:Bearer &lt;token&gt;)。</summary>
public sealed class TokenInterceptor : Interceptor
{
    public const string Header = "authorization";
    private const string Prefix = "Bearer ";
    private readonly byte[] _expectedTokenBytes;

    public TokenInterceptor(string expectedToken)
    {
        // C3:空/空白 token 直接拒绝构造 —— 服务端不会以无认证状态启动。
        if (string.IsNullOrWhiteSpace(expectedToken))
            throw new ArgumentException("Remot token 不能为空:请检查 server.json。");
        _expectedTokenBytes = Encoding.UTF8.GetBytes(expectedToken);
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    { EnsureAuthenticated(context); return await continuation(request, context); }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    { EnsureAuthenticated(context); await continuation(request, responseStream, context); }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    { EnsureAuthenticated(context); return await continuation(requestStream, context); }

    private void EnsureAuthenticated(ServerCallContext context)
    {
        var auth = context.RequestHeaders.GetValue(Header);
        var token = auth is not null && auth.StartsWith(Prefix, StringComparison.Ordinal)
            ? auth[Prefix.Length..]
            : auth;
        // M1:定长时间比较,防时序侧信道。
        var tokenBytes = token is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(token);
        if (tokenBytes.Length != _expectedTokenBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(tokenBytes, _expectedTokenBytes))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "invalid token"));
    }
}
