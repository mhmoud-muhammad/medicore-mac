using System;
using System.Text.Json.Serialization;

namespace Medicore.Licensing.Core;

/// <summary>
/// البيانات الفعلية المُوقَّعة داخل ملف الترخيص. أي حقل هنا يُصبح جزءاً من
/// التوقيع الرقمي، فتغييره بعد الإصدار (بدون إعادة توقيع بالمفتاح الخاص)
/// يُبطل الترخيص تلقائياً.
/// </summary>
public sealed class LicensePayload
{
    /// <summary>معرّف فريد لهذا الترخيص (لأغراض التتبع والإلغاء لاحقاً إن لزم)</summary>
    [JsonPropertyName("license_id")]
    public string LicenseId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>اسم الصيدلية/العميل المُرخَّص له</summary>
    [JsonPropertyName("pharmacy_name")]
    public string PharmacyName { get; set; } = "";

    /// <summary>بيانات تواصل اختيارية (اسم المالك، هاتف...) لأغراض مرجعية فقط</summary>
    [JsonPropertyName("licensee_info")]
    public string LicenseeInfo { get; set; } = "";

    /// <summary>البصمة الفريدة للجهاز المُرخَّص له تشغيل النظام عليه (راجع HardwareIdentifier)</summary>
    [JsonPropertyName("hardware_id")]
    public string HardwareId { get; set; } = "";

    /// <summary>تاريخ إصدار الترخيص (UTC)</summary>
    [JsonPropertyName("issued_at")]
    public DateTime IssuedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// تاريخ انتهاء الترخيص (UTC)، أو null لترخيص دائم بلا انتهاء.
    /// </summary>
    [JsonPropertyName("expires_at")]
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>الإصدار/الباقة المرخَّصة (مثال: "Standard", "Pro") — لتفعيل ميزات معينة لاحقاً إن رغبتم</summary>
    [JsonPropertyName("edition")]
    public string Edition { get; set; } = "Standard";

    /// <summary>الحد الأقصى لعدد المستخدمين المسموح لهم بالنظام (0 = بلا حد)</summary>
    [JsonPropertyName("max_users")]
    public int MaxUsers { get; set; } = 0;

    /// <summary>
    /// مفتاح فك تشفير كود النظام (app.mcb) مُغلَّفاً لهذا الجهاز وهذا الترخيص تحديداً
    /// (راجع CodeKeyProtector). بدون هذا الحقل لا يستطيع Medicore Server تشغيل النظام —
    /// فالنسخة الموزَّعة وحدها (بدون ترخيص صادر منك) لا تحتوي أي مفتاح لفك الكود.
    /// </summary>
    [JsonPropertyName("code_key")]
    public string? CodeKey { get; set; }

    public bool IsExpired(DateTime? nowUtc = null)
    {
        if (ExpiresAtUtc is null) return false;
        return (nowUtc ?? DateTime.UtcNow) > ExpiresAtUtc.Value;
    }
}
