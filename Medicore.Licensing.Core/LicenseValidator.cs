using System;
using System.Security.Cryptography;
using System.Text.Json;

namespace Medicore.Licensing.Core;

/// <summary>
/// يتحقق من صحة ملف ترخيص باستخدام المفتاح العام فقط. هذه هي الفئة الوحيدة
/// من مكتبة الترخيص التي يجب أن تُشحن داخل تطبيق العميل (WPF) — لا تحتوي
/// على أي عملية توقيع (تلك موجودة فقط في أداة إصدار التراخيص الخاصة بكم،
/// والتي تبقى على جهازكم أنتم ولا تُوزَّع مع البرنامج إطلاقاً).
///
/// أمان هذا التصميم يعتمد بالكامل على إبقاء المفتاح الخاص (private key) سرياً
/// عندكم. أي شخص يملك المفتاح الخاص يمكنه توليد تراخيص صالحة لأي جهاز.
/// </summary>
public sealed class LicenseValidator
{
    private readonly RSA _publicKey;

    /// <param name="publicKeyXml">المفتاح العام بصيغة XML (كما يُصدَّر من RSA.ToXmlString(false))</param>
    public LicenseValidator(string publicKeyXml)
    {
        _publicKey = RSA.Create();
        _publicKey.FromXmlString(publicKeyXml);
    }

    /// <summary>
    /// يتحقق من ملف ترخيص مقابل بصمة الجهاز الحالية.
    /// </summary>
    /// <param name="licenseFilePath">مسار ملف الترخيص (.lic)</param>
    /// <param name="currentHardwareId">بصمة الجهاز الحالي (من HardwareIdentifier.GetCurrentId())</param>
    /// <param name="nowUtc">اختياري — للاختبار؛ الافتراضي هو الوقت الفعلي الآن</param>
    public LicenseValidationResult Validate(string licenseFilePath, string currentHardwareId, DateTime? nowUtc = null)
    {
        LicenseFileEnvelope envelope;
        try
        {
            envelope = LicenseFileEnvelope.LoadFromFile(licenseFilePath);
        }
        catch (System.IO.FileNotFoundException)
        {
            return LicenseValidationResult.Fail(LicenseStatus.FileNotFound, "لم يتم العثور على ملف الترخيص. الرجاء تفعيل النظام أولاً.");
        }
        catch (Exception)
        {
            return LicenseValidationResult.Fail(LicenseStatus.Malformed, "ملف الترخيص تالف أو غير قابل للقراءة.");
        }

        return ValidateEnvelope(envelope, currentHardwareId, nowUtc);
    }

    /// <summary>نفس التحقق لكن من محتوى الملف مباشرة (نصاً) بدل قراءة قرص — مفيد للاختبار أو التفعيل عبر لصق نص الترخيص</summary>
    public LicenseValidationResult ValidateJson(string licenseJson, string currentHardwareId, DateTime? nowUtc = null)
    {
        LicenseFileEnvelope envelope;
        try
        {
            envelope = LicenseFileEnvelope.FromJson(licenseJson);
        }
        catch (Exception)
        {
            return LicenseValidationResult.Fail(LicenseStatus.Malformed, "نص الترخيص غير صالح.");
        }

        return ValidateEnvelope(envelope, currentHardwareId, nowUtc);
    }

    private LicenseValidationResult ValidateEnvelope(LicenseFileEnvelope envelope, string currentHardwareId, DateTime? nowUtc)
    {
        byte[] payloadBytes, signatureBytes;
        try
        {
            payloadBytes = envelope.GetPayloadBytes();
            signatureBytes = envelope.GetSignatureBytes();
        }
        catch (FormatException)
        {
            return LicenseValidationResult.Fail(LicenseStatus.Malformed, "ملف الترخيص تالف (Base64 غير صالح).");
        }

        // 1) التحقق من التوقيع الرقمي أولاً — قبل الوثوق بأي حقل داخل البيانات
        bool signatureOk = _publicKey.VerifyData(
            payloadBytes,
            signatureBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );

        if (!signatureOk)
        {
            return LicenseValidationResult.Fail(
                LicenseStatus.InvalidSignature,
                "توقيع ملف الترخيص غير صحيح — إمّا أن الملف مُعدَّل، أو غير صادر من الجهة المرخِّصة، " +
                "أو (السبب الأكثر شيوعاً أثناء الإعداد الأولي) أن المفتاح العام المُدمَج في هذا التطبيق " +
                "لا يطابق المفتاح الخاص الذي وُقِّع به هذا الملف. تأكد أن EmbeddedPublicKey.Xml يحتوي " +
                "بالضبط نفس محتوى public.key.xml الحالي، ثم أعد البناء."
            );
        }

        LicensePayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<LicensePayload>(payloadBytes)
                ?? throw new InvalidOperationException();
        }
        catch (Exception)
        {
            return LicenseValidationResult.Fail(LicenseStatus.Malformed, "تعذّرت قراءة بيانات الترخيص رغم صحة التوقيع.");
        }

        // 2) مطابقة بصمة الجهاز — تمنع نسخ نفس ملف الترخيص لجهاز آخر
        if (!string.Equals(payload.HardwareId, currentHardwareId, StringComparison.OrdinalIgnoreCase))
        {
            return LicenseValidationResult.Fail(
                LicenseStatus.HardwareMismatch,
                "هذا الترخيص صادر لجهاز آخر ولا يعمل على هذا الجهاز.",
                payload
            );
        }

        // 3) التحقق من الانتهاء (إن وُجد تاريخ انتهاء)
        if (payload.IsExpired(nowUtc))
        {
            return LicenseValidationResult.Fail(
                LicenseStatus.Expired,
                $"انتهت صلاحية هذا الترخيص بتاريخ {payload.ExpiresAtUtc:yyyy-MM-dd}.",
                payload
            );
        }

        return LicenseValidationResult.Ok(payload);
    }
}
