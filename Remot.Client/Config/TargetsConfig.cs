using System.Text.Json;

namespace Remot.Client.Config;

public sealed class TargetsConfig
{
    public Dictionary<string, TargetDto> Targets { get; set; } = new();

    public static TargetsConfig Load(string path) =>
        File.Exists(path) ? (JsonSerializer.Deserialize<TargetsConfig>(File.ReadAllText(path)) ?? new()) : new();

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public Target? Get(string name) => Targets.TryGetValue(name, out var t) ? t.ToTarget(name) : null;
    public void Upsert(Target t) => Targets[t.Name] = TargetDto.From(t);

    public sealed class TargetDto
    {
        public string Host { get; set; } = "";
        public int Port { get; set; } = 7070;
        public string Token { get; set; } = "";
        public string CertFingerprint { get; set; } = "";
        public Target ToTarget(string name) => new(name, Host, Port, Token, CertFingerprint);
        public static TargetDto From(Target t) => new() { Host = t.Host, Port = t.Port, Token = t.Token, CertFingerprint = t.CertFingerprint };
    }
}
