using System.Diagnostics;
using System.Text;

namespace Medicore.Server.Core;

/// <summary>
/// استيراد قاعدة بيانات (من XAMPP مثلاً) بأمان — يُستدعى والسيرفر متوقف:
///  1) يتأكد أن الملف قاعدة SQLite فعلاً وليس قاعدة البيانات الحالية نفسها.
///  2) نسخة احتياطية كاملة من الحالية (مع -wal) في database\backups.
///  3) النسخ إلى ملف مؤقت أولاً ثم الاستبدال — فشل النسخ لا يترك قاعدة تالفة أو ناقصة.
///  4) فحص أن القاعدة الجديدة تُفتح وسليمة (PHP: PRAGMA quick_check) — وإلا تُستعاد السابقة.
///  5) مسح الجلسات القديمة (تشير لمستخدمي القاعدة السابقة) — يسجّل الجميع الدخول من جديد.
/// الملف المصدر يُقرأ بمشاركة كاملة فيعمل حتى لو كان مفتوحاً في برنامج آخر.
/// </summary>
public static class DatabaseImport
{
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    public static bool LooksLikeSqlite(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[SqliteHeader.Length];
            return fs.Read(head, 0, head.Length) == head.Length && head.AsSpan().SequenceEqual(SqliteHeader);
        }
        catch (IOException)
        {
            return false;
        }
    }

    public sealed record Result(string Summary, string? BackupFile);

    public static Result Import(string source, HostPaths paths, string? phpExe)
    {
        source = Path.GetFullPath(source);
        if (!File.Exists(source)) throw new FileNotFoundException("الملف غير موجود.", source);
        if (IsCurrentDatabase(source, paths))
            throw new InvalidOperationException("هذا الملف هو قاعدة البيانات الحالية نفسها — اختر ملف medicore.sqlite من النسخة القديمة (XAMPP).");
        if (!LooksLikeSqlite(source))
            throw new InvalidDataException("الملف المختار ليس قاعدة بيانات SQLite (medicore.sqlite).");

        paths.EnsureDataDirs();
        var target = paths.DatabaseFile;
        string? backup = null;
        if (File.Exists(target))
        {
            var dir = Path.Combine(paths.DatabaseDir, "backups");
            Directory.CreateDirectory(dir);
            backup = Path.Combine(dir, $"before_import_{DateTime.Now:yyyyMMdd_HHmmss}.sqlite");
            CopyShared(target, backup);
            if (File.Exists(target + "-wal")) CopyShared(target + "-wal", backup + "-wal");
        }

        var temp = target + ".importing";
        DeleteSet(temp);
        CopyShared(source, temp);
        if (File.Exists(source + "-wal")) CopyShared(source + "-wal", temp + "-wal"); // تغييرات لم تُدمج بعد (وضع WAL)

        DeleteSet(target);
        File.Move(temp, target);
        if (File.Exists(temp + "-wal")) File.Move(temp + "-wal", target + "-wal");

        string summary;
        try
        {
            summary = phpExe is null ? "" : Verify(phpExe, target);
        }
        catch
        {
            // القاعدة المستوردة لا تعمل: نعيد السابقة كما كانت
            DeleteSet(target);
            if (backup is not null)
            {
                File.Copy(backup, target);
                if (File.Exists(backup + "-wal")) File.Copy(backup + "-wal", target + "-wal");
            }
            throw;
        }

        ClearSessions(paths);
        return new Result(summary, backup);
    }

    private static bool IsCurrentDatabase(string source, HostPaths paths)
    {
        if (string.Equals(source, Path.GetFullPath(paths.DatabaseFile), StringComparison.OrdinalIgnoreCase)) return true;
        // نفس الملف عبر رابط مجلد التشغيل المؤقت (rt\...\www\medicore\database)
        return source.StartsWith(Path.GetFullPath(paths.RuntimeRoot), StringComparison.OrdinalIgnoreCase)
               && source.EndsWith(Path.Combine("database", "medicore.sqlite"), StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyShared(string from, string to)
    {
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None);
        src.CopyTo(dst);
    }

    private static void DeleteSet(string db)
    {
        foreach (var f in new[] { db, db + "-wal", db + "-shm", db + "-journal" })
            if (File.Exists(f)) File.Delete(f);
    }

    private static void ClearSessions(HostPaths paths)
    {
        if (!Directory.Exists(paths.SessionsDir)) return;
        foreach (var f in Directory.GetFiles(paths.SessionsDir, "sess_*"))
        {
            try
            {
                File.Delete(f);
            }
            catch (IOException)
            {
                // جلسة مقفلة — تنتهي وحدها
            }
        }
    }

    /// <summary>يفتح القاعدة بـ PHP نفسه (pdo_sqlite) ويفحصها. يرجع ملخصاً: عدد الجداول والمستخدمين والأدوية.</summary>
    private static string Verify(string phpExe, string dbFile)
    {
        const string code =
            "try { $d = new PDO('sqlite:' . $argv[1]); $d->setAttribute(PDO::ATTR_ERRMODE, PDO::ERRMODE_EXCEPTION);" +
            " $c = $d->query('PRAGMA quick_check')->fetchColumn();" +
            " $t = $d->query(\"SELECT name FROM sqlite_master WHERE type='table'\")->fetchAll(PDO::FETCH_COLUMN);" +
            " $n = fn($x) => in_array($x, $t) ? (int)$d->query(\"SELECT COUNT(*) FROM $x\")->fetchColumn() : -1;" +
            " echo 'CHECK=', $c, ';TABLES=', count($t), ';USERS=', $n('users'), ';MEDICINES=', $n('medicines'); }" +
            " catch (Throwable $e) { echo 'ERROR=', $e->getMessage(); }";
        var psi = new ProcessStartInfo(phpExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(phpExe)!,
        };
        foreach (var a in PhpServer.ExtensionArguments(phpExe)) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-r", code, "--", dbFile }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(60000);

        var values = output.Split(';').Select(x => x.Split('=', 2)).Where(x => x.Length == 2)
            .ToDictionary(x => x[0].Trim(), x => x[1].Trim());
        if (values.TryGetValue("ERROR", out var err))
            throw new InvalidDataException("تعذّر فتح قاعدة البيانات المستوردة: " + err);
        if (!values.TryGetValue("CHECK", out var check) || check != "ok")
            throw new InvalidDataException("قاعدة البيانات المستوردة تالفة" + (check is null ? "" : $" ({check})") + " — تم الإبقاء على السابقة.");
        if (values.GetValueOrDefault("USERS") == "-1")
            throw new InvalidDataException("الملف ليس قاعدة بيانات نظام Medicore (لا يوجد جدول المستخدمين) — تم الإبقاء على السابقة.");
        return $"{values.GetValueOrDefault("TABLES")} جدول، {values.GetValueOrDefault("USERS")} مستخدم، {values.GetValueOrDefault("MEDICINES")} دواء";
    }
}
