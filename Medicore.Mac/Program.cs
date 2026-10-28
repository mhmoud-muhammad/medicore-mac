using Avalonia;

namespace Medicore.Mac;

internal static class Program
{
    /// <summary>بدأ مع تسجيل الدخول للماك (LaunchAgent): السيرفر يعمل بدون إظهار النافذة.</summary>
    public const string BackgroundArg = "--background";

    /// <summary>اختصار "Medicore" على سطح المكتب: السيرفر في الخلفية ثم فتح النظام في المتصفح.</summary>
    public const string OpenArg = "--open";

    public static bool StartHidden { get; private set; }
    public static bool OpenBrowser { get; private set; }
    public static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        OpenBrowser = args.Contains(OpenArg, StringComparer.OrdinalIgnoreCase);
        // macOS نفسه يفتح البرنامج بدون --background عند تسجيل الدخول إن أُضيف لـ Login Items يدوياً أو بخيار
        // "إعادة فتح النوافذ" عند إيقاف التشغيل — في أول دقائق بعد تشغيل الجهاز نبدأ مخفياً في الحالتين
        StartHidden = OpenBrowser || args.Contains(BackgroundArg, StringComparer.OrdinalIgnoreCase)
                      || MacIntegration.StartedSoonAfterBoot();

        // نسخة واحدة فقط من السيرفر على الجهاز؛ فتح البرنامج مرة أخرى يُظهر النسخة العاملة أو يفتح النظام في المتصفح
        Instance = SingleInstance.TryAcquire();
        if (Instance is null)
        {
            SingleInstance.Signal(OpenBrowser ? SingleInstance.OpenCommand : StartHidden ? null : SingleInstance.ShowCommand);
            return 0;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            Instance.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = false })
            .LogToTrace();
}
