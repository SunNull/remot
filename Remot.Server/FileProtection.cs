using System.Security.AccessControl;
using System.Security.Principal;

namespace Remot.Server;

/// <summary>文件权限收紧:Windows 上仅当前用户/管理员/SYSTEM 可访问(凭证落盘保护)。</summary>
internal static class FileProtection
{
    public static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var info = new FileInfo(path);
            var sec = info.GetAccessControl();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var identity in new[] { WindowsIdentity.GetCurrent().Name, "Administrators", "SYSTEM" })
                sec.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
            info.SetAccessControl(sec);
        }
        catch { /* 无权限或非 Windows:降级为默认权限 */ }
    }
}
