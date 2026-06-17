using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Remot.Client.Config;

public sealed class TargetsConfig
{
    public Dictionary<string, TargetDto> Targets { get; set; } = new();

    public static TargetsConfig Load(string path) =>
        File.Exists(path) ? (JsonSerializer.Deserialize<TargetsConfig>(File.ReadAllText(path)) ?? new()) : new();

    /// <summary>H6 原子写(临时文件 + Move);C4 Windows 上收紧 ACL 仅当前用户(防止其他用户读到 token)。</summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        Restrict(tmp);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
        Restrict(path);
    }

    public Target? Get(string name) => Targets.TryGetValue(name, out var t) ? t.ToTarget(name) : null;
    public void Upsert(Target t) => Targets[t.Name] = TargetDto.From(t);

    private static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var info = new FileInfo(path);
            var sec = info.GetAccessControl();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().Name, FileSystemRights.FullControl, AccessControlType.Allow));
            info.SetAccessControl(sec);
        }
        catch { /* 无权限或非 Windows:降级 */ }
    }

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
