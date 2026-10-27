using System.Diagnostics;

namespace Medicore.Server.Core;

/// <summary>
/// ربط مجلد داخل الكود المفكوك (database/uploads) بمجلد البيانات الدائم، فيعمل كود PHP
/// بمساراته الحالية (__DIR__ . '/../database/...') بدون أي تعديل. على ويندوز نستخدم
/// Junction (لا يحتاج صلاحيات مدير بخلاف Symbolic Link).
/// </summary>
public static class DirectoryLink
{
    public static void Create(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 || !Directory.Exists(link))
                throw new IOException($"تعذّر ربط المجلد {link}: {err.Trim()}");
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }

    /// <summary>يحذف الرابط نفسه فقط — لا يلمس محتوى مجلد البيانات.</summary>
    public static void Remove(string link)
    {
        var info = new DirectoryInfo(link);
        if (!info.Exists && info.LinkTarget is null) return;
        if (info.LinkTarget is null && (info.Attributes & FileAttributes.ReparsePoint) == 0)
            throw new IOException($"{link} ليس رابطاً — لن يُحذف.");
        if (OperatingSystem.IsWindows()) Directory.Delete(link, recursive: false);
        else File.Delete(link);
    }
}
