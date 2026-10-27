using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Medicore.Licensing.Mac;

/// <summary>
/// بصمة فريدة وثابتة لجهاز الماك الحالي، تُستخدم لربط الترخيص بجهاز بعينه. تعتمد على مصدرين
/// من اللوحة الأم نفسها (IOPlatformExpertDevice) حتى يصعب تزويرها:
///
///   1) IOPlatformUUID — معرّف العتاد الذي تعرضه Apple في "معلومات النظام" (Hardware UUID)،
///      ثابت طوال عمر الجهاز: لا يتغيّر بإعادة تثبيت macOS أو فرمتة القرص.
///   2) IOPlatformSerialNumber — الرقم التسلسلي للجهاز.
///
/// الصيغة نفسها كنسخة ويندوز (XXXX-XXXX-XXXX-XXXX) فتعمل أداة إصدار التراخيص كما هي بدون أي تعديل.
/// </summary>
public static class HardwareIdentifier
{
    public static string GetCurrentId()
    {
        var (uuid, serial) = ReadPlatformIds();
        return Compute(uuid ?? "NO-PLATFORM-UUID", serial ?? "");
    }

    /// <summary>البصمة من القيمتين — منفصلة عن قراءة العتاد لتُختبر على أي نظام.</summary>
    public static string Compute(string platformUuid, string serial)
    {
        string raw = $"MAC|{platformUuid.Trim().ToUpperInvariant()}|{serial.Trim().ToUpperInvariant()}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        string hex = Convert.ToHexString(hash.AsSpan(0, 8)); // 16 حرف hex
        return string.Join("-", Enumerable.Range(0, 4).Select(i => hex.Substring(i * 4, 4)));
    }

    private static (string? Uuid, string? Serial) ReadPlatformIds()
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/sbin/ioreg")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-rd1", "-c", "IOPlatformExpertDevice" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return ParseIoreg(output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (null, null);
        }
    }

    /// <summary>يستخرج القيمتين من مخرجات: ioreg -rd1 -c IOPlatformExpertDevice</summary>
    public static (string? Uuid, string? Serial) ParseIoreg(string output)
    {
        static string? Value(string text, string key)
        {
            var m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*=\\s*\"([^\"]*)\"");
            return m.Success && m.Groups[1].Value.Trim().Length > 0 ? m.Groups[1].Value.Trim() : null;
        }
        return (Value(output, "IOPlatformUUID"), Value(output, "IOPlatformSerialNumber"));
    }
}
