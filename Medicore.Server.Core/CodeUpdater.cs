using System.Security.Cryptography;
using System.Text.Json;

namespace Medicore.Server.Core;

/// <summary>
/// تحديث كود النظام عن بُعد (من صفحة الإعدادات في النظام عبر الشبكة/ZeroTier) بدون الذهاب لجهاز الصيدلية:
///
///  1. النظام (PHP، للمدير فقط) يرفع app.mcb الجديد على أجزاء إلى مجلد البيانات\updates ثم يسميه incoming.mcb.
///  2. Medicore Server يلتقطه (<see cref="TakePending"/>)، يفك تشفيره بترخيص هذا الجهاز ويجهّزه في مجلد تشغيل
///     ثانٍ والنسخة الحالية ما زالت تعمل، ثم يبدّل PHP إليه (توقف ثانية أو ثانيتين) ويفحص أن النظام يستجيب.
///  3. نجح: تُحفظ الحزمة في مجلد البيانات\app\app.mcb (Program Files لا يُكتب فيه بدون صلاحيات المدير)
///     والسابقة في app.prev.mcb للتراجع. فشل: يعود فوراً للنسخة السابقة.
///
/// الحالة تُكتب في updates\status.json فتعرضها صفحة النظام. ملف التثبيت الأحدث (Setup) يتقدّم دائماً على
/// نسخة التحديث عن بُعد: تُستخدم فقط ما دام app.mcb المثبَّت هو نفسه الذي كان وقت تفعيلها (base.sha256).
/// </summary>
public sealed class CodeUpdater
{
    /// <summary>إصدار بروتوكول التحديث عن بُعد (يقرؤه PHP من host.json ليعرف أن الميزة مدعومة).</summary>
    public const int Protocol = 1;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly HostPaths _paths;

    public CodeUpdater(HostPaths paths) => _paths = paths;

    public string UpdatesDir => UpdatesDirOf(_paths);
    public string IncomingFile => Path.Combine(UpdatesDir, "incoming.mcb");
    public string RollbackRequestFile => Path.Combine(UpdatesDir, "rollback.request");
    public string StatusFile => Path.Combine(UpdatesDir, "status.json");
    public string HostInfoFile => Path.Combine(UpdatesDir, "host.json");
    public string OverrideFile => OverrideFileOf(_paths);
    public string PreviousFile => Path.Combine(OverrideDirOf(_paths), "app.prev.mcb");
    public string ActiveInfoFile => Path.Combine(OverrideDirOf(_paths), "active.json");

    private static string UpdatesDirOf(HostPaths p) => Path.Combine(p.DataDir, "updates");
    private static string OverrideDirOf(HostPaths p) => Path.Combine(p.DataDir, "app");
    private static string OverrideFileOf(HostPaths p) => Path.Combine(OverrideDirOf(p), "app.mcb");
    private static string BaseHashFileOf(HostPaths p) => Path.Combine(OverrideDirOf(p), "base.sha256");

    public sealed record Pending(byte[] Bundle, bool Rollback);

    /// <summary>
    /// نسخة التحديث عن بُعد إن كانت سارية: موجودة، و app.mcb المثبَّت لم يتغيّر منذ تفعيلها
    /// (لو ثُبّت إصدار أحدث بملف التثبيت أو نُسخ app.mcb يدوياً، يُستخدم المثبَّت).
    /// </summary>
    public static string? ActiveOverride(HostPaths paths, string? installedBundle)
    {
        var file = OverrideFileOf(paths);
        if (!File.Exists(file)) return null;
        if (installedBundle is null) return file;
        try
        {
            var expected = File.Exists(BaseHashFileOf(paths)) ? File.ReadAllText(BaseHashFileOf(paths)).Trim() : "";
            return string.Equals(expected, Sha256(File.ReadAllBytes(installedBundle)), StringComparison.OrdinalIgnoreCase) ? file : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>عند التشغيل: يعلن للنظام أن التحديث عن بُعد مدعوم، ويسجّل النسخة العاملة الآن.</summary>
    public void Announce(string? activeBundle)
    {
        Directory.CreateDirectory(UpdatesDir);
        WriteJson(HostInfoFile, new Dictionary<string, object?>
        {
            ["remote_update"] = 1,
            ["protocol"] = Protocol,
            ["host_version"] = typeof(CodeUpdater).Assembly.GetName().Version?.ToString(),
            ["started_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        });
        if (activeBundle is null) return;
        var isOverride = string.Equals(Path.GetFullPath(activeBundle), Path.GetFullPath(OverrideFile), StringComparison.OrdinalIgnoreCase);
        var info = ReadActiveInfo();
        if (isOverride && info is not null && info.TryGetValue("source", out var src) && src?.ToString() != "installer") return;
        var bytes = File.ReadAllBytes(activeBundle);
        WriteActiveInfo(bytes, isOverride ? "remote" : "installer", File.GetLastWriteTime(activeBundle));
    }

    /// <summary>
    /// تحديث أو تراجع ينتظر التنفيذ (يُزال من مجلد الطلبات فور أخذه). null إن لم يوجد.
    /// </summary>
    public Pending? TakePending()
    {
        if (File.Exists(IncomingFile))
        {
            var bytes = ReadShared(IncomingFile);
            TryDelete(IncomingFile);
            TryDelete(RollbackRequestFile);
            return bytes.Length == 0 ? null : new Pending(bytes, false);
        }
        if (File.Exists(RollbackRequestFile))
        {
            TryDelete(RollbackRequestFile);
            if (!File.Exists(PreviousFile))
            {
                Status("failed", "لا توجد نسخة سابقة للرجوع إليها.");
                return null;
            }
            return new Pending(File.ReadAllBytes(PreviousFile), true);
        }
        return null;
    }

    /// <summary>بعد نجاح تشغيل النسخة الجديدة: تصبح هي العاملة، والسابقة تُحفظ للتراجع.</summary>
    public void Commit(byte[] newBundle, byte[]? previousBundle, bool rollback)
    {
        Directory.CreateDirectory(OverrideDirOf(_paths));
        if (previousBundle is { Length: > 0 }) WriteAtomic(PreviousFile, previousBundle);
        WriteAtomic(OverrideFile, newBundle);
        var installed = _paths.FindInstalledBundle();
        File.WriteAllText(BaseHashFileOf(_paths), installed is null ? "" : Sha256(File.ReadAllBytes(installed)));
        WriteActiveInfo(newBundle, rollback ? "rollback" : "remote", DateTime.Now);
    }

    public void Status(string state, string message)
    {
        try
        {
            Directory.CreateDirectory(UpdatesDir);
            WriteJson(StatusFile, new Dictionary<string, object?>
            {
                ["state"] = state, // pending (من PHP) | verifying | switching | done | failed
                ["message"] = message,
                ["at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            });
        }
        catch (IOException)
        {
            // الحالة للعرض فقط — لا نُفشل التحديث بسببها
        }
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private Dictionary<string, object?>? ReadActiveInfo()
    {
        try
        {
            return File.Exists(ActiveInfoFile) ? JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(ActiveInfoFile)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    private void WriteActiveInfo(byte[] bundle, string source, DateTime activatedAt)
    {
        Directory.CreateDirectory(OverrideDirOf(_paths));
        WriteJson(ActiveInfoFile, new Dictionary<string, object?>
        {
            ["sha256"] = Sha256(bundle),
            ["size"] = bundle.Length,
            ["source"] = source, // installer | remote | rollback
            ["activated_at"] = activatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            ["has_previous"] = File.Exists(PreviousFile),
        });
    }

    private static void WriteJson(string file, object value) => WriteAtomic(file, JsonSerializer.SerializeToUtf8Bytes(value, Json));

    private static void WriteAtomic(string file, byte[] bytes)
    {
        var tmp = file + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, file, overwrite: true);
    }

    private static byte[] ReadShared(string file)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                return File.ReadAllBytes(file);
            }
            catch (IOException) when (i < 10)
            {
                Thread.Sleep(300); // PHP يكمل إعادة التسمية للتو
            }
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
