namespace Remot.Client.Config;

public sealed record Target(string Name, string Host, int Port, string Token, string CertFingerprint);
