using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Medicore.Licensing.Core;
using Medicore.Server.Core;

namespace Medicore.Mac;

/// <summary>
/// Medicore Server: بعد التحقق من الترخيص يفك كود النظام المشفّر ويشغّله بـ PHP المحمول
/// على الشبكة المحلية. إغلاق النافذة يخفيها فقط (السيرفر مستمر) — الخروج من أيقونة شريط القوائم.
/// </summary>
public partial class ServerWindow : Window
{
    private const int MaxLogLines = 400;

    private readonly LicensePayload _license;
    private readonly HostPaths _paths = HostPaths.Default();
    private readonly HostSettings _settings;
    private readonly PhpServer _server = new();
    private readonly Queue<string> _log = new();
    private RuntimeWorkspace? _workspace;
    private Dictionary<string, string>? _env;
    private bool _closing;
    private bool _loadingOptions;
    private TrayIcon? _tray;
    private NativeMenuItem? _trayStatusItem;
    private bool _openBrowserPending;
    private readonly LogTail _errorTail;
    private readonly DispatcherTimer _errorTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly CodeUpdater _updater;
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _updating;

    public ServerWindow() : this(new LicensePayload { PharmacyName = "معاينة" })
    {
    }

    public ServerWindow(LicensePayload license)
    {
        InitializeComponent();
        _license = license;
        _settings = HostSettings.Load(_paths.SettingsFile);
        _updater = new CodeUpdater(_paths);
        _updateTimer.Tick += async (_, _) => await ApplyPendingUpdateAsync();
        PortBox.Text = _settings.Port.ToString();
        CreateTrayIcon();

        LicenseeText.Text = "🔒 مرخَّص لـ: " + license.PharmacyName;
        var expiry = license.ExpiresAtUtc is { } e ? $"ينتهي في {e:yyyy-MM-dd}" : "ترخيص دائم";
        LicenseDetailsText.Text = $"{expiry} • الباقة: {license.Edition} • بصمة الجهاز: {App.CurrentHardwareId}";

        _server.Log += line => Dispatcher.UIThread.Post(() => AppendLog(line));
        _server.StateChanged += s => Dispatcher.UIThread.Post(() => UpdateState(s));

        // أخطاء PHP (سبب أي HTTP ERROR 500) تظهر في السجل مباشرة
        _paths.EnsureDataDirs();
        _errorTail = new LogTail(Path.Combine(_paths.LogsDir, "php_errors.log"));
        _errorTimer.Tick += (_, _) =>
        {
            foreach (var line in _errorTail.ReadNew()) AppendLog("❗ " + line);
        };
        _errorTimer.Start();

        _loadingOptions = true;
        TrayCheck.IsChecked = _settings.CloseToTray;
        SleepCheck.IsChecked = _settings.PreventSleep;
        try
        {
            StartupCheck.IsChecked = MacIntegration.IsStartupEnabled();
            MacIntegration.RefreshStartupPath();
        }
        catch (Exception)
        {
            StartupCheck.IsEnabled = false;
        }
        _loadingOptions = false;

        Closing += Window_Closing;
    }

    /// <summary>
    /// تجهيز الكود وتشغيل السيرفر — يُستدعى من App سواء ظهرت النافذة أو بدأ البرنامج مخفياً
    /// (التشغيل التلقائي مع تسجيل الدخول).
    /// </summary>
    public async Task InitializeServerAsync()
    {
        UpdateState(ServerState.Starting);
        try
        {
            // PHP بقي يعمل من تشغيل سابق انتهى قسرياً يحجز المنفذ
            PhpServer.KillOrphan(_paths.PhpPidFile);
            // فك الكود في الخلفية (بضع ثوانٍ) حتى لا تتجمد النافذة
            _workspace = await Task.Run(() => LicensedLauncher.PrepareWorkspace(_license, _paths));
            _env = LicensedLauncher.PhpEnvironment(_license, _paths);
            AppendLog($"✅ تم تجهيز النظام ({_workspace.FileCount} ملف).");
            try
            {
                _updater.Announce(_paths.FindBundle());
                _updateTimer.Start(); // التحديث عن بُعد من صفحة الإعدادات في النظام
            }
            catch (Exception ex)
            {
                AppendLog("⚠️ التحديث عن بُعد غير متاح: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            ShowFatal(ex.Message);
            return;
        }

        CheckDatabase();
        if (_settings.AutoStart) await StartServerAsync();
    }

    private void CheckDatabase()
    {
        if (File.Exists(_paths.DatabaseFile))
        {
            WarningBorder.IsVisible = false;
            NewDatabaseButton.IsVisible = false;
            return;
        }
        WarningText.Text = "⚠️ لا توجد قاعدة بيانات بعد. لنقل بيانات صيدلية تعمل حالياً (على ويندوز أو XAMPP)، اضغط \"استيراد قاعدة بيانات\" " +
                           "واختر ملف medicore.sqlite (من مجلد البيانات في النسخة القديمة: C:\\ProgramData\\Medicore\\data\\database أو XAMPP). " +
                           "ولصيدلية جديدة اضغط \"قاعدة بيانات جديدة\".";
        WarningBorder.IsVisible = true;
        NewDatabaseButton.IsVisible = true;
    }

    private async Task StartServerAsync()
    {
        if (_workspace is null || _env is null) return;
        var php = PhpServer.LocatePhp(_paths.AppDir);
        if (php is null)
        {
            ShowFatal("لم يتم العثور على PHP. هذه النسخة من البرنامج ناقصة (مجلد php داخل الحزمة) — أعد تثبيتها، " +
                      "أو ثبّت PHP عبر Homebrew للتطوير: brew install php");
            return;
        }
        var options = new PhpServer.StartOptions(php, _workspace.DocRoot, _settings.BindAddress, _settings.Port, _env,
            _paths.LogsDir, _paths.SessionsDir, _paths.PhpPidFile);
        await Task.Run(() => _server.Start(options));
    }

    // ══════════════ التحديث عن بُعد (CodeUpdater) ══════════════

    /// <summary>
    /// تحديث وصل من صفحة النظام: تجهيز النسخة الجديدة والقديمة تعمل، ثم تبديل PHP إليها (ثانية أو ثانيتين)
    /// وفحص أن النظام يستجيب؛ وإلا العودة فوراً للنسخة السابقة.
    /// </summary>
    private async Task ApplyPendingUpdateAsync()
    {
        if (_updating || _closing || _workspace is null || _env is null) return;
        CodeUpdater.Pending? pending;
        try
        {
            pending = _updater.TakePending();
        }
        catch (Exception ex)
        {
            AppendLog("⚠️ تعذّر قراءة التحديث: " + ex.Message);
            return;
        }
        if (pending is null) return;

        _updating = true;
        var label = pending.Rollback ? "الرجوع للنسخة السابقة" : "تحديث النظام";
        RuntimeWorkspace? fresh = null;
        try
        {
            AppendLog($"⬆️ {label}: التحقق من الملف ({pending.Bundle.Length / 1024.0 / 1024.0:0.00} MB)...");
            _updater.Status("verifying", "جارٍ التحقق من الملف وتجهيز النسخة الجديدة...");
            try
            {
                fresh = await Task.Run(() => LicensedLauncher.PrepareUpdateWorkspace(_license, _paths, pending.Bundle));
            }
            catch (Exception ex)
            {
                AppendLog("⛔ رُفض التحديث: " + ex.Message);
                _updater.Status("failed", "رُفض الملف: " + ex.Message + " — النظام مستمر على النسخة الحالية.");
                return;
            }

            var currentPath = _paths.FindBundle();
            var previous = currentPath is not null ? await Task.Run(() => File.ReadAllBytes(currentPath)) : null;
            var old = _workspace;
            _updater.Status("switching", "جارٍ التبديل للنسخة الجديدة (ثوانٍ)...");
            await Task.Run(_server.Stop);
            _workspace = fresh;
            await StartServerAsync();
            if (_server.State == ServerState.Running && await SystemRespondsAsync())
            {
                await Task.Run(() => _updater.Commit(pending.Bundle, previous, pending.Rollback));
                fresh = null;
                await Task.Run(old.Dispose);
                AppendLog($"✅ {label}: تم ({_workspace.FileCount} ملف).");
                _updater.Status("done", pending.Rollback ? "تم الرجوع للنسخة السابقة." : "تم تحديث النظام بنجاح.");
                return;
            }

            // النسخة الجديدة لا تعمل: العودة فوراً للسابقة
            AppendLog("⛔ النسخة الجديدة لم تعمل — العودة للنسخة السابقة...");
            await Task.Run(_server.Stop);
            _workspace = old;
            await StartServerAsync();
            _updater.Status("failed", "النسخة الجديدة لم تعمل على هذا الجهاز، فأُعيدت النسخة السابقة تلقائياً. راجع سجل أخطاء PHP.");
        }
        catch (Exception ex)
        {
            AppendLog("⛔ فشل التحديث: " + ex.Message);
            _updater.Status("failed", "فشل التحديث: " + ex.Message);
            if (_server.State != ServerState.Running) await StartServerAsync();
        }
        finally
        {
            if (fresh is not null && !ReferenceEquals(fresh, _workspace)) await Task.Run(fresh.Dispose);
            _updating = false;
        }
    }

    /// <summary>هل يجيب النظام فعلاً (وليس فقط المنفذ مفتوح)؟ صفحة الدخول ونقطة API بدون خطأ 500.</summary>
    private async Task<bool> SystemRespondsAsync()
    {
        var host = _settings.BindAddress == "0.0.0.0" ? "127.0.0.1" : _settings.BindAddress;
        var root = $"http://{host}:{_settings.Port}/{RuntimeWorkspace.AppFolder}/";
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var page = await http.GetAsync(root);
                var api = await http.GetAsync(root + "api/v1/events.php");
                if ((int)page.StatusCode < 500 && (int)api.StatusCode < 500) return true;
                AppendLog($"⚠️ فحص النسخة الجديدة: {(int)page.StatusCode} / {(int)api.StatusCode}");
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }
            await Task.Delay(1000);
        }
        return false;
    }

    // ══════════════ العمل في الخلفية (أيقونة شريط القوائم أعلى الشاشة) ══════════════

    private void CreateTrayIcon()
    {
        _trayStatusItem = new NativeMenuItem("جاري التجهيز...") { IsEnabled = false };
        var showItem = new NativeMenuItem("فتح Medicore Server");
        showItem.Click += (_, _) => ShowFromTray();
        var browserItem = new NativeMenuItem("فتح النظام في المتصفح");
        browserItem.Click += (_, _) => OpenSystemInBrowser();
        var exitItem = new NativeMenuItem("خروج (إيقاف النظام)");
        exitItem.Click += async (_, _) => await ExitApplicationAsync();

        var menu = new NativeMenu();
        menu.Items.Add(_trayStatusItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(showItem);
        menu.Items.Add(browserItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://MedicoreServer/Assets/medicore.png"))),
            ToolTipText = "Medicore Server",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowFromTray();
        if (Application.Current is { } app) TrayIcon.SetIcons(app, new TrayIcons { _tray });

        // "فتح النظام في المتصفح" أيضاً في قائمة أيقونة الـ Dock (ضغطة يمين)
        var dockBrowserItem = new NativeMenuItem("فتح النظام في المتصفح");
        dockBrowserItem.Click += (_, _) => OpenSystemInBrowser();
        if (Application.Current is { } current) NativeDock.SetMenu(current, new NativeMenu { dockBrowserItem });
    }

    /// <summary>اختصار "Medicore": يفتح النظام في المتصفح فور جاهزية السيرفر (أو يُظهر النافذة إن فشل التشغيل).</summary>
    public void OpenBrowserWhenReady()
    {
        switch (_server.State)
        {
            case ServerState.Running:
                OpenSystemInBrowser();
                break;
            case ServerState.Failed:
                ShowFromTray();
                break;
            default:
                _openBrowserPending = true; // يُفتح عند وصول الحالة إلى "يعمل" (UpdateState)
                break;
        }
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>الخروج النهائي: إيقاف النظام وحذف الكود المفكوك وإغلاق البرنامج.</summary>
    public async Task ExitApplicationAsync()
    {
        if (_server.State == ServerState.Running)
        {
            ShowFromTray();
            if (!await Dialogs.Confirm(this, "الخروج يوقف النظام على كل الأجهزة (التطبيق ونقاط البيع). هل تريد الخروج؟",
                    "إيقاف Medicore Server", Dialogs.Kind.Warning))
                return;
        }
        Shutdown();
        App.Quit();
    }

    private async void StartupCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loadingOptions) return;
        try
        {
            MacIntegration.SetStartup(StartupCheck.IsChecked == true);
            AppendLog(StartupCheck.IsChecked == true
                ? "✅ سيعمل النظام تلقائياً في الخلفية عند تشغيل الماك وتسجيل الدخول."
                : "تم إلغاء التشغيل التلقائي مع تسجيل الدخول.");
        }
        catch (Exception ex)
        {
            await Dialogs.Error(this, "تعذّر تغيير التشغيل التلقائي: " + ex.Message);
        }
    }

    private void TrayCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loadingOptions) return;
        _settings.CloseToTray = TrayCheck.IsChecked == true;
        _settings.Save(_paths.SettingsFile);
    }

    private void SleepCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loadingOptions) return;
        _settings.PreventSleep = SleepCheck.IsChecked == true;
        _settings.Save(_paths.SettingsFile);
        MacIntegration.KeepAwake(_settings.PreventSleep && _server.State == ServerState.Running);
    }

    private async void Shortcuts_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await Task.Run(MacIntegration.CreateDesktopShortcut);
            await Dialogs.Info(this, "تم إنشاء اختصار \"Medicore\" على سطح المكتب: يفتح النظام في المتصفح " +
                                     "(ويشغّل السيرفر في الخلفية إن لم يكن يعمل). يمكنك سحبه إلى الـ Dock.", "تم");
        }
        catch (Exception ex)
        {
            await Dialogs.Error(this, "تعذّر إنشاء الاختصار: " + ex.Message);
        }
    }

    private void UpdateState(ServerState state)
    {
        var (text, color) = state switch
        {
            ServerState.Running => ("السيرفر يعمل", Color.FromRgb(0x05, 0x96, 0x69)),
            ServerState.Starting => ("جاري التشغيل...", Color.FromRgb(0xD9, 0x77, 0x06)),
            ServerState.Failed => ("تعذّر التشغيل", Color.FromRgb(0xDC, 0x26, 0x26)),
            _ => ("السيرفر متوقف", Colors.Gray),
        };
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(color);
        StatusDetailText.Text = state == ServerState.Failed ? _server.LastError ?? "" :
            state == ServerState.Running ? "إغلاق هذه النافذة لا يوقف النظام — يبقى يعمل في الخلفية (أيقونة أعلى الشاشة)." : "";
        StartButton.IsEnabled = state is ServerState.Stopped or ServerState.Failed && _workspace is not null;
        StopButton.IsEnabled = state is ServerState.Running or ServerState.Starting;
        ShowUrls(state == ServerState.Running ? BuildUrls() : new List<string>());
        if (_tray is not null) _tray.ToolTipText = "Medicore Server — " + text;
        if (_trayStatusItem is not null) _trayStatusItem.Header = (state == ServerState.Running ? "🟢 " : "⚪️ ") + text;
        MacIntegration.KeepAwake(_settings.PreventSleep && state == ServerState.Running);
        if (_openBrowserPending && state == ServerState.Running)
        {
            _openBrowserPending = false;
            OpenSystemInBrowser();
        }
        else if (_openBrowserPending && state == ServerState.Failed)
        {
            _openBrowserPending = false;
            ShowFromTray(); // يرى المستخدم سبب الفشل بدل ألا يحدث شيء
        }
    }

    private void ShowUrls(IEnumerable<string> urls)
    {
        UrlsPanel.Children.Clear();
        foreach (var url in urls)
        {
            var link = new Button
            {
                Content = url,
                Classes = { "link" },
                FlowDirection = FlowDirection.LeftToRight,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                FontFamily = new FontFamily("Menlo, Monaco, monospace"),
                FontSize = 13,
            };
            link.Click += (_, _) => MacIntegration.OpenUrl(url);
            UrlsPanel.Children.Add(link);
        }
    }

    private List<string> BuildUrls()
    {
        var port = _settings.Port == 80 ? "" : $":{_settings.Port}";
        var hosts = PhpServer.LanAddresses().ToList();
        hosts.Add("localhost");
        return hosts.Select(h => $"http://{h}{port}/{RuntimeWorkspace.AppFolder}/").ToList();
    }

    private void AppendLog(string line)
    {
        _log.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
        while (_log.Count > MaxLogLines) _log.Dequeue();
        LogBox.Text = string.Join(Environment.NewLine, _log);
        LogBox.CaretIndex = LogBox.Text.Length;
    }

    private void ShowFatal(string message)
    {
        StatusText.Text = "تعذّر تشغيل النظام";
        StatusDot.Fill = Brushes.Red;
        StatusDetailText.Text = message;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        AppendLog("⛔ " + message);
        // بدأ مخفياً (مع تسجيل الدخول أو من اختصار النظام): نُظهر النافذة ليرى سبب الفشل
        _openBrowserPending = false;
        ShowFromTray();
    }

    private async void Start_Click(object? sender, RoutedEventArgs e) => await StartServerAsync();

    private async void Stop_Click(object? sender, RoutedEventArgs e) => await Task.Run(_server.Stop);

    private void OpenBrowser_Click(object? sender, RoutedEventArgs e) => OpenSystemInBrowser();

    private void OpenSystemInBrowser()
    {
        var port = _settings.Port == 80 ? "" : $":{_settings.Port}";
        MacIntegration.OpenUrl($"http://localhost{port}/{RuntimeWorkspace.AppFolder}/");
    }

    private void OpenDataFolder_Click(object? sender, RoutedEventArgs e)
    {
        _paths.EnsureDataDirs();
        MacIntegration.RevealFolder(_paths.DataDir);
    }

    private async void OpenErrorLog_Click(object? sender, RoutedEventArgs e)
    {
        var log = Path.Combine(_paths.LogsDir, "php_errors.log");
        if (!File.Exists(log))
        {
            await Dialogs.Info(this, "لا توجد أخطاء مسجلة بعد.", "سجل أخطاء PHP");
            return;
        }
        MacIntegration.OpenTextFile(log);
    }

    private async void ImportDatabase_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "اختر قاعدة البيانات (medicore.sqlite)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("قاعدة بيانات SQLite") { Patterns = new[] { "*.sqlite", "*.db" } },
                FilePickerFileTypes.All,
            },
        });
        var source = files.FirstOrDefault()?.TryGetLocalPath();
        if (source is null) return;

        var msg = File.Exists(_paths.DatabaseFile)
            ? "سيتم استبدال قاعدة البيانات الحالية (تُحفظ نسخة منها في database/backups). متابعة؟"
            : "سيتم إيقاف السيرفر لحظياً واستيراد قاعدة البيانات. متابعة؟";
        if (!await Dialogs.Confirm(this, msg, "استيراد قاعدة بيانات")) return;

        var wasRunning = _server.State == ServerState.Running;
        await Task.Run(_server.Stop);
        try
        {
            var php = PhpServer.LocatePhp(_paths.AppDir);
            AppendLog("جاري استيراد قاعدة البيانات: " + source);
            var result = await Task.Run(() => DatabaseImport.Import(source, _paths, php));
            AppendLog($"✅ تم استيراد قاعدة البيانات ({result.Summary}).");
            if (result.BackupFile is not null) AppendLog("النسخة السابقة محفوظة في: " + result.BackupFile);
            CheckDatabase();
        }
        catch (Exception ex)
        {
            AppendLog("⛔ فشل الاستيراد: " + ex.Message);
            await Dialogs.Error(this, "تعذّر الاستيراد: " + ex.Message);
        }
        if (wasRunning || _settings.AutoStart) await StartServerAsync();
    }

    private async void NewDatabase_Click(object? sender, RoutedEventArgs e)
    {
        if (_workspace is null) return;
        var php = PhpServer.LocatePhp(_paths.AppDir);
        if (php is null)
        {
            await Dialogs.Error(this, "لم يتم العثور على PHP.");
            return;
        }
        if (!await Dialogs.Confirm(this, "سيتم إنشاء قاعدة بيانات جديدة فارغة لصيدلية جديدة. متابعة؟", "قاعدة بيانات جديدة"))
            return;
        try
        {
            var ws = _workspace;
            var output = await Task.Run(() => DatabaseSetup.CreateFresh(php, ws, _paths));
            AppendLog(output.Trim());
            CheckDatabase();
            await Dialogs.Info(this, $"تم إنشاء قاعدة البيانات.\n\nالدخول: {DatabaseSetup.DefaultUser} / {DatabaseSetup.DefaultPassword}\n" +
                                     "غيّر كلمة المرور فوراً من إدارة المستخدمين.", "تم");
        }
        catch (Exception ex)
        {
            await Dialogs.Error(this, ex.Message);
        }
    }

    private async void SavePort_Click(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text?.Trim(), out var port) || port is < 1 or > 65535)
        {
            await Dialogs.Info(this, "رقم المنفذ غير صالح.", "تنبيه");
            return;
        }
        _settings.Port = port;
        _settings.Save(_paths.SettingsFile);
        AppendLog($"تم حفظ المنفذ {port} — إعادة التشغيل...");
        await Task.Run(_server.Stop);
        await StartServerAsync();
        if (port != 80)
            await Dialogs.Info(this, $"تذكّر تحديث عنوان السيرفر في التطبيق ليشمل المنفذ :{port}.", "تنبيه");
    }

    private async void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing) return;
        if (_settings.CloseToTray)
        {
            // زر الإغلاق يخفي النافذة فقط — السيرفر مستمر (الخروج من أيقونة شريط القوائم)
            e.Cancel = true;
            Hide();
            return;
        }
        e.Cancel = true;
        await ExitApplicationAsync();
    }

    /// <summary>يوقف PHP ويحذف الكود المفكوك — يُستدعى أيضاً عند Cmd+Q وتسجيل الخروج من الماك.</summary>
    public void Shutdown()
    {
        if (_closing) return;
        _closing = true;
        _errorTimer.Stop();
        _updateTimer.Stop();
        if (_tray is not null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
            _tray = null;
        }
        _server.Stop();
        MacIntegration.KeepAwake(false);
        _workspace?.Dispose();
        _workspace = null;
    }
}
