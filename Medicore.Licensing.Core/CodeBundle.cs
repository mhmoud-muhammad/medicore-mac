using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Medicore.Licensing.Core;

/// <summary>
/// حزمة كود النظام المشفّرة (app.mcb): أرشيف ZIP لملفات PHP مشفّر بـ AES-256-GCM
/// بمفتاح الكود. الصيغة: "MCB1" + nonce(12) + tag(16) + ciphertext، والرأس "MCB1"
/// داخل البيانات المصادَق عليها فلا يمكن العبث به.
/// </summary>
public static class CodeBundle
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MCB1");

    /// <summary>ما لا يُضمَّن في الحزمة: البيانات (تبقى في مجلد البيانات الدائم) وملفات التطوير.</summary>
    public static readonly string[] DefaultExcludes =
    {
        ".git/", ".github/", ".vscode/", ".idea/", "database/", "uploads/", "medicore-theme-update/",
        "*.log", "*.zip", "*.lic", "*.md", ".gitignore", "license/.dev", "license/New Text Document.txt",
    };

    public static byte[] Encrypt(byte[] zip, byte[] codeKey)
    {
        var box = AesGcmBox.Seal(codeKey, zip, Magic);
        return Magic.Concat(box).ToArray();
    }

    public static byte[] Decrypt(byte[] bundle, byte[] codeKey)
    {
        if (bundle.Length < 4 || !bundle.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("ملف كود النظام (app.mcb) غير صالح.");
        try
        {
            return AesGcmBox.Open(codeKey, bundle.AsSpan(4), Magic);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidDataException("تعذّر فك كود النظام: الترخيص لا يطابق هذه النسخة، أو الملف معدَّل.");
        }
    }

    public static bool IsExcluded(string relativePath, IEnumerable<string> excludes)
    {
        var rel = relativePath.Replace('\\', '/');
        foreach (var raw in excludes)
        {
            var ex = raw.Replace('\\', '/');
            if (ex.EndsWith('/'))
            {
                if (rel.StartsWith(ex, StringComparison.OrdinalIgnoreCase) || rel.Contains("/" + ex, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (ex.StartsWith("*."))
            {
                if (rel.EndsWith(ex[1..], StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(rel, ex, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>يضغط مجلد كود PHP (مع الاستثناءات) إلى ZIP في الذاكرة.
    /// <paramref name="extra"/>: ملفات نصية إضافية (مثلاً build.json بتاريخ البناء).</summary>
    public static byte[] Zip(string sourceDir, IEnumerable<string>? excludes = null, Action<string>? onFile = null,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        var ex = (excludes ?? DefaultExcludes).ToArray();
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in extra ?? new Dictionary<string, string>())
            {
                using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                var rel = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                if (IsExcluded(rel, ex)) continue;
                zip.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
                onFile?.Invoke(rel);
            }
        }
        return ms.ToArray();
    }

    /// <summary>هل الأرشيف يبدو نظام Medicore (index.php و api/)؟ — يمنع تفعيل حزمة خاطئة في التحديث عن بُعد.</summary>
    public static bool LooksLikeMedicore(byte[] zipBytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            return zip.GetEntry("index.php") is not null && zip.Entries.Any(e => e.FullName.StartsWith("api/", StringComparison.Ordinal));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>يفك ZIP (بعد فك التشفير) إلى مجلد، مع منع مسارات الخروج من المجلد (zip slip).</summary>
    public static int ExtractTo(byte[] zipBytes, string targetDir)
    {
        var root = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
        int count = 0;
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var dest = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("مسار غير صالح داخل حزمة الكود.");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
            count++;
        }
        return count;
    }
}
