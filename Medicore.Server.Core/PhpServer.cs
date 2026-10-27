using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Medicore.Server.Core;

public enum ServerState { Stopped, Starting, Running, Failed }

/// <summary>
/// يشغّل PHP المحمول (php/php داخل حزمة البرنامج) كسيرفر ويب على الشبكة المحلية،
/// مع مراقبة: لو توقف PHP فجأة يُعاد تشغيله تلقائياً. رقم عملية PHP يُحفظ في ملف (PidFile)
/// فيُوقَف أي PHP يتيم من تشغيل سابق أُنهي قسرياً قبل التشغيل التالي.
/// </summary>
public sealed class PhpServer : IDisposable
{
    private static readonly string[] Extensions =
    {
        "pdo", "mysqlnd", "pdo_sqlite", "sqlite3", "mbstring", "openssl", "fileinfo", "curl", "gd", "gd2", "zip", "intl", "exif", "pdo_mysql", "mysqli",
    };

    private readonly object _gate = new();
    private Process? _process;
    private bool _stopping;
    private readonly Queue<DateTime> _restarts = new();
    private StartOptions? _options;
    private bool _launching; // Exited قد يُطلق على نفس الخيط أثناء فحص HasExited في Launch

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? LastError { get; private set; }

    public event Action<string>? Log;
    public event Action<ServerState>? StateChanged;

    public sealed record StartOptions(
        string PhpExe,
        string DocRoot,
        string BindAddress,
        int Port,
        IReadOnlyDictionary<string, string> Environment,
        string LogsDir,
        string SessionsDir,
        string? PidFile = null);

    /// <summary>
    /// php/php داخل حزمة البرنامج، وإلا PHP مثبَّت (Homebrew/MacPorts) — برامج الماك المفتوحة من Finder
    /// لا ترث PATH الخاص بالـ Terminal، فنبحث في المسارات المعروفة صراحةً.
    /// </summary>
    public static string? LocatePhp(string appDir)
    {
        foreach (var p in new[] { Path.Combine(appDir, "php", "php"), Path.Combine(appDir, "php", "bin", "php") })
            if (File.Exists(p)) return p;
        var candidates = (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, "php"))
            .Concat(new[] { "/opt/homebrew/bin/php", "/usr/local/bin/php", "/opt/local/bin/php" });
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>يوقف PHP بقي يعمل من تشغيل سابق انتهى قسرياً (يحجز المنفذ فيفشل التشغيل الجديد).</summary>
    public static void KillOrphan(string pidFile)
    {
        try
        {
            if (!File.Exists(pidFile)) return;
            if (int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) && pid != System.Environment.ProcessId)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (p.ProcessName.StartsWith("php", StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(5000);
                    }
                }
                catch (ArgumentException)
                {
                    // انتهت بالفعل
                }
            }
            File.Delete(pidFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            // غير حرج: فحص المنفذ يُظهر السبب إن بقي مشغولاً
        }
    }

    /// <summary>
    /// المنفذ مشغول فقط إن كان برنامج آخر "يستمع" عليه فعلاً (LISTEN). لا نجرّب الحجز بأنفسنا: بعد إيقاف
    /// السيرفر يُبقي ويندوز اتصالات المتصفح المغلقة (TIME_WAIT) على المنفذ دقيقتين تقريباً، فيفشل الحجز
    /// التجريبي ويُظن المنفذ مستخدماً — فلا يعود السيرفر للعمل بعد استيراد قاعدة البيانات أو تغيير المنفذ.
    /// </summary>
    public static bool IsPortFree(string bindAddress, int port)
    {
        try
        {
            var bind = IPAddress.Parse(bindAddress);
            bool anyBind = bind.Equals(IPAddress.Any) || bind.Equals(IPAddress.IPv6Any);
            foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (ep.Port != port) continue;
                if (anyBind || ep.Address.Equals(bind) || ep.Address.Equals(IPAddress.Any) || ep.Address.Equals(IPAddress.IPv6Any))
                    return false;
            }
            return true;
        }
        catch (NetworkInformationException)
        {
            try
            {
                var l = new TcpListener(IPAddress.Parse(bindAddress), port);
                l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                l.Start();
                l.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }

    /// <summary>عناوين IPv4 للجهاز على الشبكة المحلية (ليتصل بها التطبيق والأجهزة الأخرى).</summary>
    public static IReadOnlyList<string> LanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address.ToString())
            .Distinct()
            .ToList();

    /// <summary>
    /// PHP بمجلد ext (نسخة ويندوز محمولة): نتجاهل php.ini تماماً (-n) ونفعّل الإضافات المطلوبة من ext مباشرة.
    /// PHP المحمول للماك (static-php-cli) كل إضافاته مدمجة، و PHP الخاص بـ Homebrew يقرأ php.ini الخاص به.
    ///
    /// في كل الحالات: include_path بدون "." (مثل XAMPP) — قرص الماك (APFS) مثل ويندوز لا يفرّق بين الحروف
    /// الكبيرة والصغيرة، فـ require 'Medicine.php' كان سيطابق views/medicines/medicine.php من مجلد الصفحة
    /// بدل models/Medicine.php من مجلد الملف المستدعي.
    /// </summary>
    public static List<string> ExtensionArguments(string phpExe)
    {
        var args = new List<string>();
        var phpDir = Path.GetDirectoryName(phpExe)!;
        var ext = Path.Combine(phpDir, "ext");
        if (Directory.Exists(ext))
        {
            args.AddRange(new[] { "-n", "-d", $"extension_dir={ext}" });
            foreach (var e in Extensions)
            {
                if (File.Exists(Path.Combine(ext, $"php_{e}.dll")) || File.Exists(Path.Combine(ext, $"{e}.so")))
                    args.AddRange(new[] { "-d", $"extension={e}" });
            }
        }
        var pear = Path.Combine(phpDir, "PEAR");
        args.AddRange(new[] { "-d", $"include_path={(Directory.Exists(pear) ? pear : phpDir)}" });
        return args;
    }

    /// <summary>
    /// OPcache للسيرفر: مع -n (تجاهل php.ini) يكون معطّلاً، فيعيد PHP ترجمة كل ملفات النظام في كل طلب
    /// (نقطة البيع وحدها ~4300 سطر). السيرفر المدمج عملية واحدة دائمة فتبقى الملفات المترجمة في الذاكرة.
    /// validate_timestamps يبقى مفعّلاً حتى تُقرأ ملفات التحديث الجديدة تلقائياً.
    /// </summary>
    public static List<string> OpcacheArguments(string phpExe)
    {
        var ext = Path.Combine(Path.GetDirectoryName(phpExe)!, "ext");
        var loadable = File.Exists(Path.Combine(ext, "php_opcache.dll")) || File.Exists(Path.Combine(ext, "opcache.so"));
        // PHP المحمول للماك (static-php-cli) يحتوي OPcache مدمجاً، و PHP الخاص بـ Homebrew يحمّله من php.ini
        if (!loadable && !HasBuiltinOpcache(phpExe)) return new();
        var args = new List<string>();
        foreach (var d in new[]
        {
            loadable ? "zend_extension=opcache" : null, "opcache.enable=1", "opcache.enable_cli=1", "opcache.memory_consumption=128",
            "opcache.interned_strings_buffer=16", "opcache.max_accelerated_files=10000", "opcache.validate_timestamps=1",
            "opcache.revalidate_freq=2",
        })
            if (d is not null) args.AddRange(new[] { "-d", d });
        return args;
    }

    private static readonly Dictionary<string, bool> OpcacheProbe = new();

    /// <summary>هل OPcache محمَّل في PHP هذا بدون إضافة خارجية؟ (يُفحص مرة واحدة لكل ملف PHP)</summary>
    private static bool HasBuiltinOpcache(string phpExe)
    {
        lock (OpcacheProbe)
        {
            if (OpcacheProbe.TryGetValue(phpExe, out var cached)) return cached;
            bool result = false;
            try
            {
                var psi = new ProcessStartInfo(phpExe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var a in new[] { "-r", "echo extension_loaded('Zend OPcache') ? 'yes' : 'no';" }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                var output = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                result = output.Trim() == "yes";
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
            }
            OpcacheProbe[phpExe] = result;
            return result;
        }
    }

    public static List<string> BuildArguments(StartOptions o)
    {
        var args = new List<string> { "-S", $"{o.BindAddress}:{o.Port}", "-t", o.DocRoot };
        args.AddRange(ExtensionArguments(o.PhpExe));
        args.AddRange(OpcacheArguments(o.PhpExe));
        foreach (var d in new[]
        {
            "memory_limit=512M", "upload_max_filesize=64M", "post_max_size=64M", "max_execution_time=300",
            "display_errors=0", "log_errors=1", $"error_log={Path.Combine(o.LogsDir, "php_errors.log")}",
            $"session.save_path={o.SessionsDir}", "session.gc_maxlifetime=86400", "expose_php=0",
        })
            args.AddRange(new[] { "-d", d });
        // الموجّه آخر وسيط (هو "السكربت"): يمنع تنزيل قاعدة البيانات والنسخ مباشرة من /medicore/database/
        var router = RuntimeWorkspace.RouterScriptOf(o.DocRoot);
        if (File.Exists(router)) args.Add(router);
        return args;
    }

    public void Start(StartOptions options)
    {
        lock (_gate)
        {
            if (_process is { HasExited: false }) return;
            _options = options;
            _stopping = false;
            _restarts.Clear();
            Launch();
        }
    }

    private void Launch()
    {
        _launching = true;
        try
        {
            LaunchCore();
        }
        finally
        {
            _launching = false;
        }
    }

    private void LaunchCore()
    {
        // فشل عابر في أول لحظات التشغيل (منفذ يتحرر للتو...) يُعاد تلقائياً قبل إعلان الفشل
        const int attempts = 4;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            var result = TryLaunch(isLast: attempt == attempts);
            if (result != LaunchResult.RetryableExit) return;
            Log?.Invoke($"⚠️ توقف PHP أثناء التشغيل — محاولة {attempt + 1} من {attempts}...");
            Thread.Sleep(1500);
        }
    }

    private enum LaunchResult { Done, RetryableExit }

    private LaunchResult TryLaunch(bool isLast)
    {
        var o = _options!;
        SetState(ServerState.Starting);
        if (!IsPortFree(o.BindAddress, o.Port))
        {
            Fail($"المنفذ {o.Port} مستخدم من برنامج آخر (غالباً MAMP/XAMPP أو Apache المدمج في الماك أو AirPlay Receiver). أوقفه أو غيّر المنفذ.");
            return LaunchResult.Done;
        }

        var psi = new ProcessStartInfo(o.PhpExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // مجلد PHP نفسه: أي مسار نسبي في إعداداته يُحل على قرصه (السيرفر المدمج ينتقل لمجلد كل سكربت عند تنفيذه)
            WorkingDirectory = Path.GetDirectoryName(o.PhpExe)!,
        };
        var arguments = BuildArguments(o);
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        if (_restarts.Count == 0)
            Log?.Invoke($"PHP: {o.PhpExe} {string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
        foreach (var (k, v) in o.Environment) psi.Environment[k] = v;

        try
        {
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke(e.Data); };
            p.Exited += OnExited;
            p.Start();
            if (o.PidFile is not null) TryWritePid(o.PidFile, p.Id);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _process = p;
        }
        catch (Exception ex)
        {
            Fail("تعذّر تشغيل PHP: " + ex.Message);
            return LaunchResult.Done;
        }

        // ننتظر حتى يقبل السيرفر الاتصالات فعلاً
        var host = o.BindAddress == "0.0.0.0" ? "127.0.0.1" : o.BindAddress;
        for (int i = 0; i < 50; i++)
        {
            if (_process.HasExited) break;
            try
            {
                using var c = new TcpClient();
                c.Connect(host, o.Port);
                LastError = null;
                SetState(ServerState.Running);
                return LaunchResult.Done;
            }
            catch (SocketException)
            {
                Thread.Sleep(100);
            }
        }
        if (_process.HasExited)
        {
            if (!isLast) return LaunchResult.RetryableExit;
            Diagnose(o);
            Fail($"توقف PHP فور تشغيله (رمز {FormatExit(_process.ExitCode)}) — راجع السجل أعلاه.");
        }
        else SetState(ServerState.Running);
        return LaunchResult.Done;
    }

    private void OnExited(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            // فشل عند بدء التشغيل نفسه (مشخَّص في Launch): إعادة المحاولة لن تفيد
            if (_stopping || !ReferenceEquals(sender, _process) || State == ServerState.Failed || _launching) return;
            Log?.Invoke($"⚠️ توقف PHP (رمز {FormatExit(_process?.ExitCode ?? 0)}) — إعادة التشغيل...");
            var now = DateTime.UtcNow;
            while (_restarts.Count > 0 && now - _restarts.Peek() > TimeSpan.FromMinutes(1)) _restarts.Dequeue();
            if (_restarts.Count >= 5)
            {
                Fail("توقف PHP أكثر من 5 مرات خلال دقيقة — تم إيقاف إعادة التشغيل التلقائي.");
                return;
            }
            _restarts.Enqueue(now);
        }
        Task.Delay(1500).ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (!_stopping) Launch();
            }
        });
    }

    private static string FormatExit(int code) => code < 0 ? $"{code} / 0x{code:X8}" : code.ToString();

    /// <summary>
    /// عند توقف PHP فوراً: نشغّل نفس PHP بنفس الإضافات لطباعة سبب الفشل (مكتبة ناقصة، إضافة تالفة...)
    /// + آخر أسطر سجل أخطاء PHP.
    /// </summary>
    private void Diagnose(StartOptions o)
    {
        try
        {
            var psi = new ProcessStartInfo(o.PhpExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(o.PhpExe)!,
            };
            foreach (var a in ExtensionArguments(o.PhpExe)) psi.ArgumentList.Add(a);
            foreach (var a in new[] { "-d", "display_startup_errors=1", "-d", "display_errors=1", "-r", "echo 'PHP ', PHP_VERSION, ' OK - ext: ', implode(',', get_loaded_extensions());" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit(10000);
            Log?.Invoke($"تشخيص PHP (رمز {FormatExit(p.ExitCode)}): {(output.Length == 0 ? "(لا يوجد مخرجات)" : output)}");
            if (o.Port < 1024)
                Log?.Invoke($"💡 إن ظهر أعلاه \"Permission denied\" للمنفذ {o.Port}: جرّب منفذاً آخر مثل 8080 من خانة المنفذ.");
        }
        catch (Exception ex)
        {
            Log?.Invoke("تعذّر تشخيص PHP: " + ex.Message);
        }
        try
        {
            var log = Path.Combine(o.LogsDir, "php_errors.log");
            if (File.Exists(log))
                foreach (var line in File.ReadLines(log).TakeLast(5)) Log?.Invoke("php_errors.log: " + line);
        }
        catch
        {
            // السجل مقفل
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopping = true;
            var p = _process;
            _process = null;
            if (p is { HasExited: false })
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                catch
                {
                    // انتهى بالفعل
                }
            }
            p?.Dispose();
            if (_options?.PidFile is { } pidFile)
            {
                try
                {
                    File.Delete(pidFile);
                }
                catch (IOException)
                {
                }
            }
            SetState(ServerState.Stopped);
        }
    }

    private static void TryWritePid(string file, int pid)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, pid.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Fail(string message)
    {
        LastError = message;
        Log?.Invoke("⛔ " + message);
        SetState(ServerState.Failed);
    }

    private void SetState(ServerState s)
    {
        State = s;
        StateChanged?.Invoke(s);
    }

    public void Dispose() => Stop();
}
