using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Medicore.Licensing.Core;
using Medicore.Licensing.Mac;
using Medicore.Server.Core;

namespace Medicore.Mac;

public partial class App : Application
{
    /// <summary>
    /// ملف الترخيص: ~/Library/Application Support/Medicore/license.lic — خارج حزمة البرنامج فيبقى
    /// محفوظاً عند تحديث البرنامج أو استبداله، ولا يحتاج صلاحيات مدير.
    /// </summary>
    public static string LicenseFilePath { get; } = Path.Combine(HostPaths.SupportDir, "license.lic");

    public static string CurrentHardwareId { get; private set; } = "";

    public static LicensePayload? ActiveLicense { get; private set; }

    private ServerWindow? _server;
    private PosixSignalRegistration? _sigterm;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        // Cmd+Q أو تسجيل الخروج من الماك: إيقاف PHP وحذف الكود المفكوك
        desktop.ShutdownRequested += (_, _) => _server?.Shutdown();
        desktop.Exit += (_, _) => _server?.Shutdown();
        // إيقاف من launchctl أو إعادة تشغيل الجهاز (SIGTERM)
        _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
        });

        // الضغط على أيقونة البرنامج في الـ Dock والنافذة مخفية يُظهرها
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen) _server?.ShowFromTray();
            };
        }

        Program.Instance?.Listen(command => Dispatcher.UIThread.Post(() =>
        {
            if (_server is null) return;
            if (command == SingleInstance.OpenCommand) _server.OpenBrowserWhenReady();
            else _server.ShowFromTray();
        }));

        CurrentHardwareId = HardwareIdentifier.GetCurrentId();

        var result = ValidateForServer();
        if (result.IsValid)
        {
            LaunchServer(result.Payload!);
            return;
        }

        // الترخيص غير موجود/غير صالح: نعرض شاشة التفعيل بدل تشغيل البرنامج مباشرة
        var activation = new LicenseActivationWindow(result);
        activation.Closed += (_, _) =>
        {
            if (_server is null) desktop.Shutdown();
        };
        desktop.MainWindow = activation;
        activation.Show();
    }

    /// <summary>
    /// التحقق من الترخيص (التوقيع + بصمة الجهاز + الانتهاء) + وجود مفتاح تشغيل الكود بداخله.
    /// </summary>
    public static LicenseValidationResult ValidateForServer(string? file = null)
    {
        var validator = new LicenseValidator(EmbeddedPublicKey.Xml);
        var result = validator.Validate(file ?? LicenseFilePath, CurrentHardwareId);
        if (result.IsValid && string.IsNullOrWhiteSpace(result.Payload!.CodeKey))
        {
            return LicenseValidationResult.Fail(LicenseStatus.Malformed,
                "هذا الترخيص صادر بنسخة قديمة ولا يحتوي مفتاح تشغيل النظام. أرسل بصمة الجهاز لمزوّد النظام للحصول على ترخيص محدَّث.",
                result.Payload);
        }
        return result;
    }

    private void LaunchServer(LicensePayload license, bool forceShow = false)
    {
        ActiveLicense = license;
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        _server = new ServerWindow(license);
        desktop.MainWindow = _server;
        if (forceShow || !Program.StartHidden) _server.Show();
        _ = _server.InitializeServerAsync();
        if (Program.OpenBrowser) _server.OpenBrowserWhenReady();
    }

    /// <summary>
    /// تُستدعى من شاشة التفعيل بعد تثبيت ملف ترخيص جديد بنجاح — تنتقل لنافذة السيرفر مباشرة.
    /// </summary>
    public static bool TryActivateAndLaunchServer()
    {
        var result = ValidateForServer();
        if (!result.IsValid || Current is not App app) return false;
        app.LaunchServer(result.Payload!, forceShow: true);
        return true;
    }

    /// <summary>الخروج النهائي (من القائمة): إيقاف السيرفر وإغلاق البرنامج.</summary>
    public static void Quit()
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }
}
