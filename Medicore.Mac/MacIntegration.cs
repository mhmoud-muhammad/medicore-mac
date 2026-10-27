using System.Diagnostics;
using System.Security;

namespace Medicore.Mac;

/// <summary>
/// التكامل مع الماك: التشغيل التلقائي عند تسجيل الدخول (LaunchAgent في مكتبة المستخدم — لا يحتاج كلمة
/// مرور المدير)، اختصار "Medicore" على سطح المكتب، منع الجهاز من النوم أثناء عمل السيرفر، وفتح
/// الروابط والمجلدات.
/// </summary>
internal static class MacIntegration
{
    public const string AgentLabel = "com.medicore.server";

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MedicoreServer");

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string AgentFile => Path.Combine(Home, "Library", "LaunchAgents", AgentLabel + ".plist");

    /// <summary>حزمة البرنامج (…/Medicore Server.app) إن كان يعمل من داخلها.</summary>
    public static string? AppBundlePath
    {
        get
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(ExePath)!);
            return dir.Name == "MacOS" && dir.Parent?.Parent is { } app && app.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                ? app.FullName
                : null;
        }
    }

    // ══════════════ التشغيل مع تسجيل الدخول ══════════════

    public static bool IsStartupEnabled() => File.Exists(AgentFile);

    public static void SetStartup(bool enabled)
    {
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AgentFile)!);
            File.WriteAllText(AgentFile, BuildAgentPlist(ExePath));
        }
        else if (File.Exists(AgentFile))
        {
            File.Delete(AgentFile);
        }
    }

    /// <summary>لو نُقل البرنامج (مثلاً من التنزيلات إلى التطبيقات) بعد تفعيل التشغيل التلقائي: نحدّث المسار.</summary>
    public static void RefreshStartupPath()
    {
        if (!File.Exists(AgentFile)) return;
        var expected = BuildAgentPlist(ExePath);
        if (File.ReadAllText(AgentFile) != expected) File.WriteAllText(AgentFile, expected);
    }

    /// <summary>
    /// LaunchAgent: يبدأ السيرفر في الخلفية عند تسجيل الدخول، ويُعاد تشغيله تلقائياً لو توقف بخطأ
    /// (KeepAlive/SuccessfulExit=false) — أما الخروج المقصود من القائمة فلا يُعاد تشغيله.
    /// </summary>
    public static string BuildAgentPlist(string exePath) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{AgentLabel}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{SecurityElement.Escape(exePath)}</string>
                <string>{Program.BackgroundArg}</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
            <key>KeepAlive</key>
            <dict>
                <key>SuccessfulExit</key>
                <false/>
            </dict>
            <key>ThrottleInterval</key>
            <integer>10</integer>
            <key>LimitLoadToSessionType</key>
            <string>Aqua</string>
            <key>ProcessType</key>
            <string>Interactive</string>
        </dict>
        </plist>

        """;

    // ══════════════ اختصار سطح المكتب ══════════════

    /// <summary>
    /// "Medicore" على سطح المكتب: تطبيق صغير (AppleScript) بأيقونة النظام يفتح النظام في المتصفح ويشغّل
    /// السيرفر في الخلفية إن لم يكن يعمل — مثل اختصار ويندوز تماماً.
    /// </summary>
    public static string CreateDesktopShortcut()
    {
        var desktop = Path.Combine(Home, "Desktop");
        var target = Path.Combine(desktop, "Medicore.app");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);

        var script = $"do shell script quoted form of \"{EscapeAppleScript(ExePath)}\" & \" {Program.OpenArg} > /dev/null 2>&1 &\"";
        Run("/usr/bin/osacompile", "-o", target, "-e", script);

        // أيقونة النظام بدل أيقونة AppleScript الافتراضية
        if (AppBundlePath is { } bundle)
        {
            var icon = Path.Combine(bundle, "Contents", "Resources", "medicore.icns");
            var appletIcon = Path.Combine(target, "Contents", "Resources", "applet.icns");
            if (File.Exists(icon) && File.Exists(appletIcon))
            {
                File.Copy(icon, appletIcon, overwrite: true);
                Run("/usr/bin/touch", target); // يحدّث Finder الأيقونة
            }
        }
        return target;
    }

    private static string EscapeAppleScript(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ══════════════ منع النوم ══════════════

    private static Process? _caffeinate;

    /// <summary>
    /// caffeinate -i -s: يمنع الماك من النوم التلقائي ما دام هذا البرنامج يعمل (-w)، حتى تبقى نقاط البيع
    /// والتطبيق متصلة. الشاشة نفسها يمكن أن تنطفئ عادياً.
    /// </summary>
    public static void KeepAwake(bool enabled)
    {
        try
        {
            if (enabled && _caffeinate is not { HasExited: false })
            {
                var psi = new ProcessStartInfo("/usr/bin/caffeinate") { UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in new[] { "-i", "-s", "-w", Environment.ProcessId.ToString() }) psi.ArgumentList.Add(a);
                _caffeinate = Process.Start(psi);
            }
            else if (!enabled && _caffeinate is { HasExited: false })
            {
                _caffeinate.Kill();
                _caffeinate.Dispose();
                _caffeinate = null;
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // ليس ماك (تطوير على لينكس) — لا شيء
        }
    }

    // ══════════════ فتح الروابط والملفات ══════════════

    public static void OpenUrl(string url) => TryRun("/usr/bin/open", url);

    public static void RevealFolder(string folder) => TryRun("/usr/bin/open", folder);

    /// <summary>يفتح ملفاً نصياً في TextEdit (أو محرر النصوص الافتراضي).</summary>
    public static void OpenTextFile(string file) => TryRun("/usr/bin/open", "-t", file);

    private static void TryRun(string exe, params string[] args)
    {
        try
        {
            Run(exe, args);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // غير متاح في هذه البيئة
        }
    }

    private static void Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        if (p.ExitCode != 0) throw new InvalidOperationException(err.Trim().Length > 0 ? err.Trim() : $"{Path.GetFileName(exe)} فشل ({p.ExitCode})");
    }
}
