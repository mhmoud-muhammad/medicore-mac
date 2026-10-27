namespace Medicore.Mac;

/// <summary>
/// المفتاح العام المُدمَج داخل التطبيق. آمن تماماً أن يكون هذا الملف علنياً
/// (حتى لو رفعتوه Git عام) — المفتاح العام لا يمكّن أحداً من إصدار تراخيص
/// جديدة، فقط من التحقق من تراخيص موقَّعة بالمفتاح الخاص المقابل له.
///
/// هذا هو مفتاحكم الحقيقي (وُلِّد عندكم فعلياً وصدرتم به رخصة "صيدلية رزن")
/// — ثابت الآن ولن يتغيّر في أي تحديث قادم مني، حتى لا تحتاجوا إعادة توليد
/// المفاتيح أو إعادة إصدار التراخيص مرة أخرى.
/// </summary>
internal static class EmbeddedPublicKey
{
    public const string Xml =
        "<RSAKeyValue><Modulus>z7yB9MjqoZPo5wnSKsKSDCYMlq4+4DwlsO9Z/5g85pfDpEJ7V3JoMhZWtfrgLGOsaNcdGNDaZEu7IJI/B9325+V8DU9/waSQluI1U5BpGBOTBskzBb+dxqz+xBHASXDfzK2n4pLqdsaVxySQc+C5aHiQ/1YiUz/2eVlGqNFL6wP+MKLQr6KJ0a2/U2WUO3QeKtVRsxZ6zMu7z3WtihIfWg+AH0WQTw7lDchYk8Z45jijuMaR5okJv+LT6hYKHCivPZZCVCPXGn91Px3mkEPV6Rp+CmO2yFR/Xski98iRxeJZM8U5OlGC6Ee9hZqHBdq4mA3uBcyWoF/GgJxABkY7aSmgZsuVy5A91NOKQmmqavwIdZr/4nC0efa6whBCMV2bZ/CaXbzYqGb/A9PLBHXon22JYs04yWGbhwC2bZqo5bBO9YNxcqyYQO0OVtioRrnJCKF0yP1d4yOvJk5CPxzKc9uVUiutN2f5gKcI7yD0vlzSCL0hKOnnV3lw6sFcFaLJ</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
}
