namespace Medicore.Licensing.Core;

public enum LicenseStatus
{
    Valid,
    FileNotFound,
    Malformed,
    InvalidSignature,
    HardwareMismatch,
    Expired,
}

public sealed class LicenseValidationResult
{
    public LicenseStatus Status { get; init; }
    public LicensePayload? Payload { get; init; }
    public string Message { get; init; } = "";

    public bool IsValid => Status == LicenseStatus.Valid;

    public static LicenseValidationResult Ok(LicensePayload payload) => new()
    {
        Status = LicenseStatus.Valid,
        Payload = payload,
        Message = "الترخيص سارٍ وصالح لهذا الجهاز.",
    };

    public static LicenseValidationResult Fail(LicenseStatus status, string message, LicensePayload? payload = null) => new()
    {
        Status = status,
        Payload = payload,
        Message = message,
    };
}
