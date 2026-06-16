using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Remot.Client.Config;

namespace Remot.Client.Channels;

/// <summary>每目标一条持久 gRPC 通道,用证书指纹锁定(自签证书防中间人)。</summary>
public sealed class ChannelManager : IDisposable
{
    private readonly Dictionary<string, GrpcChannel> _channels = new();
    private readonly object _lock = new();

    public GrpcChannel Get(Target t)
    {
        lock (_lock)
        {
            if (_channels.TryGetValue(t.Name, out var existing)) return existing;
            var handler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                        cert is X509Certificate2 c
                        && c.GetCertHashString(HashAlgorithmName.SHA256)
                            .Equals(t.CertFingerprint, StringComparison.OrdinalIgnoreCase)
                }
            };
            var channel = GrpcChannel.ForAddress($"https://{t.Host}:{t.Port}",
                new GrpcChannelOptions { HttpHandler = handler });
            _channels[t.Name] = channel;
            return channel;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var c in _channels.Values) c.Dispose();
            _channels.Clear();
        }
    }
}
