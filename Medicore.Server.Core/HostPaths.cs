using System.Text.Json;

namespace Medicore.Server.Core;

/// <summary>
/// أماكن ملفات Medicore Server على الماك:
///  - مجلد البرنامج: Medicore Server.app/Contents/Resources — app.mcb (الكود المشفّر) + php/ (PHP محمول)
///  - مجلد البيانات الدائم (~/Library/Application Support/Medicore/data): قاعدة البيانات والمرفقات
///    والإعدادات — لا يُحذف أبداً ويبقى عبر التحديثات وحذف البرنامج.
///  - مجلد التشغيل المؤقت (~/Library/Application Support/Medicore/.rt): الكود المفكوك أثناء التشغيل فقط،
///    يُحذف عند الإغلاق (ويُنظَّف عند التشغيل التالي لو أُغلق البرنامج فجأة).
/// </summary>
public sealed record HostPaths(string AppDir, string DataDir, string RuntimeRoot)
{
    public string BundlePath => Path.Combine(AppDir, "app.mcb");

    /// <summary>
    /// كود النظام المستخدم: نسخة التحديث عن بُعد (مجلد البيانات\app\app.mcb) إن وُجدت وكانت أحدث
    /// من المثبَّتة، وإلا app.mcb المثبَّت (راجع <see cref="FindInstalledBundle"/> و <see cref="CodeUpdater"/>).
    /// </summary>
    public string? FindBundle()
    {
        var installed = FindInstalledBundle();
        return CodeUpdater.ActiveOverride(this, installed) ?? installed;
    }

    /// <summary>
    /// app.mcb بجانب البرنامج (نسخة العميل)، وإلا في المجلدات الأعلى حتى 6 مستويات — أثناء التطوير
    /// يعمل البرنامج من bin/Debug/net10.0 فيكفي وضع app.mcb في جذر الحل.
    /// </summary>
    public string? FindInstalledBundle()
    {
        var dir = new DirectoryInfo(AppDir);
        for (int i = 0; i <= 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "app.mcb");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
    public string DatabaseDir => Path.Combine(DataDir, "database");
    public string DatabaseFile => Path.Combine(DatabaseDir, "medicore.sqlite");
    public string UploadsDir => Path.Combine(DataDir, "uploads");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string SessionsDir => Path.Combine(DataDir, "sessions");
    public string SettingsFile => Path.Combine(DataDir, "host.json");
    /// <summary>رقم عملية PHP العاملة — لإيقاف PHP يتيم لو أُنهي البرنامج قسرياً (لا يوجد Job Object على الماك).</summary>
    public string PhpPidFile => Path.Combine(RuntimeRoot, "php.pid");

    /// <summary>~/Library/Application Support/Medicore — الترخيص ومجلد البيانات (لا يحتاج صلاحيات مدير).</summary>
    public static string SupportDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Medicore");

    public static HostPaths Default() => new(
        ResourcesDir(AppContext.BaseDirectory),
        Path.Combine(SupportDir, "data"),
        Path.Combine(SupportDir, ".rt"));

    /// <summary>
    /// داخل حزمة البرنامج يعمل الملف التنفيذي من Contents/MacOS بينما app.mcb و php في Contents/Resources.
    /// أثناء التطوير (dotnet run) يبقى مجلد البرنامج كما هو.
    /// </summary>
    public static string ResourcesDir(string baseDir)
    {
        var dir = new DirectoryInfo(baseDir);
        if (dir.Name == "MacOS" && dir.Parent is { Name: "Contents" } contents)
        {
            var resources = Path.Combine(contents.FullName, "Resources");
            if (Directory.Exists(resources)) return resources + Path.DirectorySeparatorChar;
        }
        return baseDir;
    }

    public void EnsureDataDirs()
    {
        foreach (var d in new[] { DataDir, DatabaseDir, UploadsDir, LogsDir, SessionsDir })
            Directory.CreateDirectory(d);
    }
}

public sealed class HostSettings
{
    public int Port { get; set; } = 80;
    public string BindAddress { get; set; } = "0.0.0.0";
    public bool AutoStart { get; set; } = true;

    /// <summary>إغلاق النافذة يخفيها والسيرفر يستمر (الخروج من أيقونة شريط القوائم أعلى الشاشة).</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>منع الماك من النوم التلقائي ما دام السيرفر يعمل (نقاط البيع والتطبيق تبقى متصلة).</summary>
    public bool PreventSleep { get; set; } = true;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static HostSettings Load(string file)
    {
        try
        {
            if (File.Exists(file))
                return JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(file)) ?? new HostSettings();
        }
        catch
        {
            // إعدادات تالفة → الافتراضي
        }
        return new HostSettings();
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, Json));
    }
}
