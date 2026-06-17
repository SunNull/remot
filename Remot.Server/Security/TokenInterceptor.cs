using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Remot.Server.Security;

/// <summary>校验请求元数据里的 authorization token(格式:Bearer &lt;token&gt;),并可选限制来源 IP。</summary>
public sealed class TokenInterceptor : Interceptor
{
    public const string Header = "authorization";
    private const string Prefix = "Bearer ";
    private readonly byte[] _expectedTokenBytes;
    private readonly IReadOnlyList<string> _allowedClientIPs;

    public TokenInterceptor(string expectedToken, IReadOnlyList<string>? allowedClientIPs = null)
    {
        // C3:空/空白 token 直接拒绝构造
        if (string.IsNullOrWhiteSpace(expectedToken))
            throw new ArgumentException("Remot token 不能为空:请检查 server.json。");
        _expectedTokenBytes = Encoding.UTF8.GetBytes(expectedToken);
        _allowedClientIPs = allowedClientIPs ?? Array.Empty<string>();
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
        where TRequest : class where TResponse : class
    { EnsureAuthenticated(context); return await continuation(request, context); }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
        where TRequest : class where TResponse : class
    { EnsureAuthenticated(context); await continuation(request, responseStream, context); }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
        where TRequest : class where TResponse : class
    { EnsureAuthenticated(context); return await continuation(requestStream, context); }

    private void EnsureAuthenticated(ServerCallContext context)
    {
        // C6 缓解:客户端 IP 白名单 —— 配对串即便泄露,非白名单 IP 也用不了
        if (_allowedClientIPs.Count > 0)
        {
            var ip = ExtractIp(context.Peer);
            if (ip is null || !_allowedClientIPs.Contains(ip, StringComparer.OrdinalIgnoreCase))
                throw new RpcException(new Status(StatusCode.PermissionDenied, "ip not allowed"));
        }

        var auth = context.RequestHeaders.GetValue(Header);
        var token = auth is not null && auth.StartsWith(Prefix, StringComparison.Ordinal) ? auth[Prefix.Length..] : auth;
        var tokenBytes = token is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(token);
        // M1:定长时间比较
        if (tokenBytes.Length != _expectedTokenBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(tokenBytes, _expectedTokenBytes))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "invalid token"));
    }

    private static string? ExtractIp(string peer)
    {
        // gRPC Peer 格式:"ipv4:1.2.3.4:54321" / "ipv6:[::1]:54321" / "dns:host:port"
        if (peer.StartsWith("ipv4:", StringComparison.Ordinal))
        { var rest = peer[5..]; var c = rest.IndexOf(':'); return c < 0 ? rest : rest[..c]; }
        if (peer.StartsWith("ipv6:", StringComparison.Ordinal))
        { var s = peer.IndexOf('['); var e = peer.IndexOf(']'); if (s >= 0 && e > s) return peer[(s + 1)..e]; }
        return null;
    }
}
