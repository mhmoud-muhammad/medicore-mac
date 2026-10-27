using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Medicore.Licensing.Core;

/// <summary>
/// الشكل الفعلي لملف الترخيص (.lic) على القرص: البيانات (Payload) بصيغة
/// Base64 + توقيعها الرقمي بصيغة Base64 أيضاً. نتحقق من التوقيع مباشرة على
/// نفس البايتات المخزَّنة في PayloadBase64 (وليس بإعادة تسلسلها من جديد)،
/// حتى لا يتأثر التحقق بأي فرق تنسيق JSON بين وقت التوقيع ووقت التحقق.
/// </summary>
public sealed class LicenseFileEnvelope
{
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("alg")]
    public string Algorithm { get; set; } = "RSA-SHA256";

    [JsonPropertyName("payload")]
    public string PayloadBase64 { get; set; } = "";

    [JsonPropertyName("signature")]
    public string SignatureBase64 { get; set; } = "";

    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new()
    {
        WriteIndented = true,
    };

    public static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>يبني الـ envelope من الحمولة الأصلية (bytes) وتوقيعها — تُستخدم وقت الإصدار فقط</summary>
    public static LicenseFileEnvelope Create(byte[] payloadBytes, byte[] signatureBytes)
    {
        return new LicenseFileEnvelope
        {
            PayloadBase64 = Convert.ToBase64String(payloadBytes),
            SignatureBase64 = Convert.ToBase64String(signatureBytes),
        };
    }

    public byte[] GetPayloadBytes() => Convert.FromBase64String(PayloadBase64);
    public byte[] GetSignatureBytes() => Convert.FromBase64String(SignatureBase64);

    public string ToJson() => JsonSerializer.Serialize(this, EnvelopeJsonOptions);

    public static LicenseFileEnvelope FromJson(string json)
    {
        var envelope = JsonSerializer.Deserialize<LicenseFileEnvelope>(json)
            ?? throw new InvalidDataException("ملف الترخيص تالف أو بصيغة غير صحيحة.");
        return envelope;
    }

    public void SaveToFile(string path) => File.WriteAllText(path, ToJson(), Encoding.UTF8);

    public static LicenseFileEnvelope LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("ملف الترخيص غير موجود.", path);
        return FromJson(File.ReadAllText(path, Encoding.UTF8));
    }
}
