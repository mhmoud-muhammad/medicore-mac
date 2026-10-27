using System.Diagnostics;

namespace Medicore.Server.Core;

/// <summary>
/// قاعدة بيانات جديدة لصيدلية جديدة: يشغّل install/init_db.php من الكود المفكوك (schema.sql)
/// على ملف قاعدة البيانات في مجلد البيانات الدائم. المستخدم الافتراضي: admin / password.
/// </summary>
public static class DatabaseSetup
{
    public const string DefaultUser = "admin";
    public const string DefaultPassword = "password";

    public static string CreateFresh(string phpExe, RuntimeWorkspace workspace, HostPaths paths)
    {
        if (File.Exists(paths.DatabaseFile) && new FileInfo(paths.DatabaseFile).Length > 0)
            throw new InvalidOperationException("قاعدة البيانات موجودة بالفعل ولن تُستبدل.");
        var script = Path.Combine(workspace.AppRoot, "install", "init_db.php");
        if (!File.Exists(script))
            throw new FileNotFoundException("سكربت إنشاء قاعدة البيانات غير موجود في هذه النسخة.", script);

        paths.EnsureDataDirs();
        var psi = new ProcessStartInfo(phpExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(phpExe)!,
        };
        foreach (var a in PhpServer.ExtensionArguments(phpExe)) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(paths.DatabaseFile);

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 || !File.Exists(paths.DatabaseFile) || output.Contains('❌'))
            throw new InvalidOperationException("تعذّر إنشاء قاعدة البيانات:\n" + output.Trim());
        return output;
    }
}
