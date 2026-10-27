using System.Security.Cryptography;
using Medicore.Licensing.Core;

namespace Medicore.Server.Core;

/// <summary>
/// التسلسل الكامل بعد التحقق من الترخيص: فك مفتاح الكود من الترخيص ← فك تشفير app.mcb
/// ← فك الكود في مجلد التشغيل المؤقت ← متغيرات البيئة التي يتحقق منها PHP.
/// </summary>
public static class LicensedLauncher
{
    public static RuntimeWorkspace PrepareWorkspace(LicensePayload license, HostPaths paths)
    {
        var bundlePath = paths.FindBundle() ?? throw new FileNotFoundException(
            "ملف كود النظام app.mcb غير موجود بجانب البرنامج أو في جذر المشروع. جهّزه بنفس code.key الذي أصدرت به الترخيص:\n" +
            "dotnet run --project Medicore.Packager -- pack ../medicore-C-/Medicore.Web app.mcb code.key", paths.BundlePath);
        var zip = DecryptBundle(license, File.ReadAllBytes(bundlePath));
        RuntimeWorkspace.CleanupStale(paths.RuntimeRoot);
        return RuntimeWorkspace.Create(paths.RuntimeRoot, zip, paths);
    }

    /// <summary>
    /// مجلد تشغيل ثانٍ لنسخة جديدة (تحديث عن بُعد) بجانب النسخة العاملة — بدون تنظيف مجلدات التشغيل
    /// (النسخة الحالية ما زالت تعمل). يرفض حزمة لا تُفك بترخيص هذا الجهاز أو ليست نظام Medicore.
    /// </summary>
    public static RuntimeWorkspace PrepareUpdateWorkspace(LicensePayload license, HostPaths paths, byte[] bundle)
    {
        var zip = DecryptBundle(license, bundle);
        if (!CodeBundle.LooksLikeMedicore(zip))
            throw new InvalidDataException("الملف لا يحتوي نظام Medicore (لا يوجد index.php و api).");
        return RuntimeWorkspace.Create(paths.RuntimeRoot, zip, paths);
    }

    private static byte[] DecryptBundle(LicensePayload license, byte[] bundle)
    {
        var key = CodeKeyProtector.Unwrap(license);
        try
        {
            return CodeBundle.Decrypt(bundle, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// متغيرات البيئة لعملية PHP — license/license_manager.php يرفض العمل بدون MEDICORE_HOST_TOKEN
    /// (أي أن الكود لا يعمل خارج Medicore Server المرخَّص).
    /// </summary>
    public static Dictionary<string, string> PhpEnvironment(LicensePayload license, HostPaths paths) => new()
    {
        ["MEDICORE_HOST_TOKEN"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
        ["MEDICORE_LICENSE_ID"] = license.LicenseId,
        ["MEDICORE_LICENSEE"] = license.PharmacyName,
        ["MEDICORE_EDITION"] = license.Edition,
        ["MEDICORE_MAX_USERS"] = license.MaxUsers.ToString(),
        ["MEDICORE_LICENSE_EXPIRES"] = license.ExpiresAtUtc?.ToString("yyyy-MM-dd") ?? "",
        ["MEDICORE_DATA_DIR"] = paths.DataDir,
    };
}
