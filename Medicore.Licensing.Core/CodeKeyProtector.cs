using System;
using System.Security.Cryptography;
using System.Text;

namespace Medicore.Licensing.Core;

/// <summary>
/// يغلّف مفتاح تشفير الكود (32 بايت، يبقى عندك في code.key) داخل كل ترخيص،
/// مربوطاً ببصمة الجهاز ومعرّف الترخيص. الترخيص نفسه موقَّع بالمفتاح الخاص،
/// فلا يمكن تعديل المفتاح المغلَّف أو نقله لترخيص/جهاز آخر.
///
/// ⚠️ حدود واقعية: من يملك جهازاً مرخَّصاً وخبرة هندسة عكسية لبرنامج .NET يستطيع
/// نظرياً استخراج المفتاح وقت التشغيل — هذا الحد موجود في أي حماية تعمل على جهاز
/// العميل. ما يضمنه هذا التصميم: النسخة الموزَّعة بدون ترخيص صادر منك لا تحتوي
/// أي مفتاح، فلا يمكن قراءة الكود أو تشغيله منها.
/// </summary>
public static class CodeKeyProtector
{
    public const int KeySize = 32;

    // ملح ثابت داخل البرنامج — يمنع اشتقاق مفتاح التغليف من بيانات الترخيص وحدها بأدوات عامة
    private static readonly byte[] Pepper = Convert.FromBase64String("q3VtbWVkaWNvcmUtY29kZS1rZXktdjEtN2Y0YTljMmU=");

    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(KeySize);

    private static byte[] WrappingKey(string hardwareId, string licenseId)
    {
        var info = Encoding.UTF8.GetBytes($"MEDICORE-CODEKEY-v1|{hardwareId.Trim().ToUpperInvariant()}|{licenseId}");
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, Pepper, KeySize, salt: null, info: info);
    }

    /// <summary>يُستخدم في أداة إصدار التراخيص فقط.</summary>
    public static string Wrap(byte[] codeKey, string hardwareId, string licenseId)
    {
        if (codeKey.Length != KeySize) throw new ArgumentException("مفتاح الكود يجب أن يكون 32 بايت.");
        return Convert.ToBase64String(AesGcmBox.Seal(WrappingKey(hardwareId, licenseId), codeKey));
    }

    /// <summary>يفك المفتاح لترخيص تم التحقق من توقيعه وبصمته مسبقاً (LicenseValidator).</summary>
    public static byte[] Unwrap(LicensePayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.CodeKey))
            throw new InvalidOperationException("هذا الترخيص لا يحتوي مفتاح تشغيل النظام — اطلب ترخيصاً محدَّثاً.");
        return AesGcmBox.Open(WrappingKey(payload.HardwareId, payload.LicenseId), Convert.FromBase64String(payload.CodeKey));
    }
}

/// <summary>AES-256-GCM: nonce(12) + tag(16) + ciphertext.</summary>
internal static class AesGcmBox
{
    public static byte[] Seal(byte[] key, byte[] plain, byte[]? aad = null)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plain, cipher, tag, aad);
        var result = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, 12);
        cipher.CopyTo(result, 28);
        return result;
    }

    public static byte[] Open(byte[] key, ReadOnlySpan<byte> box, byte[]? aad = null)
    {
        if (box.Length < 28) throw new CryptographicException("بيانات مشفّرة تالفة.");
        var plain = new byte[box.Length - 28];
        using (var gcm = new AesGcm(key, 16)) gcm.Decrypt(box[..12], box[28..], box.Slice(12, 16), plain, aad);
        return plain;
    }
}
