namespace Medicore.Server.Core;

/// <summary>
/// مجلد التشغيل المؤقت: يُفك فيه كود PHP بعد التحقق من الترخيص، ويُحذف عند الإغلاق.
/// البنية: rt\&lt;id&gt;\www (جذر السيرفر) و rt\&lt;id&gt;\www\medicore (الكود) — فتبقى الروابط
/// نفسها كما في XAMPP: http://IP/medicore/... (التطبيق لا يحتاج أي تعديل).
/// </summary>
public sealed class RuntimeWorkspace : IDisposable
{
    public const string AppFolder = "medicore";
    private static readonly string[] LinkedFolders = { "database", "uploads" };

    public string Root { get; }
    public string DocRoot => Path.Combine(Root, "www");
    public string AppRoot => Path.Combine(DocRoot, AppFolder);
    /// <summary>سكربت التوجيه لسيرفر PHP (خارج جذر الويب): يمنع تنزيل قاعدة البيانات والنسخ والمفاتيح مباشرة.</summary>
    public string RouterScript => RouterScriptOf(DocRoot);

    public static string RouterScriptOf(string docRoot) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(docRoot))!, "router.php");

    /// <summary>
    /// سيرفر PHP المدمج يرسل أي ملف تحت الجذر كما هو — ومجلد database (قاعدة البيانات، النسخ الاحتياطية،
    /// صور الإشعارات، مفاتيح Firebase) مربوط داخل الجذر. هذا الموجّه يرد 404 عليها، ويترك الباقي كما هو
    /// (return false = يتصرف السيرفر كالمعتاد: ملف ثابت أو سكربت PHP).
    /// </summary>
    public const string RouterSource = """
        <?php
        // Medicore Server — يمنع الوصول المباشر لملفات البيانات (يُنشأ تلقائياً)
        $p = rawurldecode((string) parse_url($_SERVER['REQUEST_URI'] ?? '/', PHP_URL_PATH));
        $p = strtolower(str_replace('\\', '/', $p));
        $p = preg_replace('#/+#', '/', preg_replace('#/(\./)+#', '/', $p));
        if (str_contains($p, '..') || str_contains($p, "\0")
            || preg_match('#/(database|license)(/|$)#', $p)
            || preg_match('#\.(sqlite3?|db|sqlite-(wal|shm|journal)|db-(wal|shm|journal)|key|pem|p12|log|sql|bak|mcb|lic|env|ini)$#', rtrim($p, '/. '))) {
            http_response_code(404);
            header('Content-Type: text/plain; charset=utf-8');
            echo 'Not Found';
            return true;
        }
        return false;
        """;
    public int FileCount { get; private set; }

    private RuntimeWorkspace(string root) => Root = root;

    /// <summary>يحذف بقايا تشغيل سابق انتهى فجأة (انقطاع كهرباء، إنهاء قسري...).</summary>
    public static void CleanupStale(string runtimeRoot)
    {
        if (!Directory.Exists(runtimeRoot)) return;
        foreach (var dir in Directory.GetDirectories(runtimeRoot))
        {
            try
            {
                SafeDelete(dir);
            }
            catch
            {
                // قد يكون مستخدماً من نسخة أخرى تعمل — نتركه
            }
        }
    }

    public static RuntimeWorkspace Create(string runtimeRoot, byte[] codeZip, HostPaths paths)
    {
        paths.EnsureDataDirs();
        var ws = new RuntimeWorkspace(Path.Combine(runtimeRoot, Guid.NewGuid().ToString("N")));
        try
        {
            var dir = Directory.CreateDirectory(ws.AppRoot);
            if (OperatingSystem.IsWindows())
                new DirectoryInfo(runtimeRoot).Attributes |= FileAttributes.Hidden;

            ws.FileCount = Licensing.Core.CodeBundle.ExtractTo(codeZip, ws.AppRoot);

            foreach (var name in LinkedFolders)
            {
                var link = Path.Combine(ws.AppRoot, name);
                if (Directory.Exists(link)) Directory.Delete(link, recursive: true);
                DirectoryLink.Create(link, Path.Combine(paths.DataDir, name));
            }

            File.WriteAllText(ws.RouterScript, RouterSource);
            // الجذر يحوّل إلى النظام مباشرة
            File.WriteAllText(Path.Combine(ws.DocRoot, "index.php"), "<?php header('Location: /" + AppFolder + "/'); exit;\n");
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    /// <summary>يحذف الروابط أولاً (حتى لا يمس الحذف قاعدة البيانات) ثم الكود المفكوك.</summary>
    private static void SafeDelete(string root)
    {
        var app = Path.Combine(root, "www", AppFolder);
        foreach (var name in LinkedFolders)
        {
            var link = Path.Combine(app, name);
            var info = new DirectoryInfo(link);
            if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
                DirectoryLink.Remove(link);
        }
        // أي رابط آخر غير متوقَّع: لا نحذف ما وراءه
        foreach (var d in Directory.Exists(root) ? Directory.GetDirectories(root, "*", SearchOption.AllDirectories) : Array.Empty<string>())
        {
            var info = new DirectoryInfo(d);
            if (info.LinkTarget is not null) DirectoryLink.Remove(d);
        }
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    public void Dispose()
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                SafeDelete(Root);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(300); // PHP يغلق ملفاته للتو
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }
    }
}
