using System;
using System.Security.Cryptography;
using Medicore.Licensing.Core;
using Medicore.Licensing.Mac;
using Medicore.Server.Core;

// ============================================================================
// اختبارات تلقائية لنسخة الماك (الترخيص + السيرفر + أجزاء الماك) — شغّلوها بعد أي تعديل:
//     dotnet run --project Medicore.Mac.Tests
// التشغيل الفعلي لنظام PHP (سيناريو 10) يحتاج php ومجلد النظام:
//     MEDICORE_PHP_DIR=../medicore-C-/Medicore.Web dotnet run --project Medicore.Mac.Tests
// ============================================================================

int passed = 0, failed = 0;

void Check(string name, bool condition)
{
    if (condition) { Console.WriteLine($"  ✅ {name}"); passed++; }
    else { Console.WriteLine($"  ❌ {name}"); failed++; }
}

Console.WriteLine("=== اختبارات نظام الترخيص ===\n");

using var rsa = RSA.Create(3072);
string privateXml = rsa.ToXmlString(true);
string publicXml = rsa.ToXmlString(false);

var signer = new LicenseSignerForTests(privateXml);
var validator = new LicenseValidator(publicXml);

const string RealHardwareId = "AAAA-BBBB-CCCC-DDDD";
const string OtherHardwareId = "1111-2222-3333-4444";

// --- سيناريو 1: ترخيص صحيح على نفس الجهاز ---
Console.WriteLine("سيناريو 1: ترخيص صالح");
var goodPayload = new LicensePayload { PharmacyName = "صيدلية الاختبار", HardwareId = RealHardwareId };
var goodEnvelope = signer.Issue(goodPayload);
var r1 = validator.ValidateJson(goodEnvelope.ToJson(), RealHardwareId);
Check("الترخيص صالح", r1.IsValid);
Check("الحالة Valid", r1.Status == LicenseStatus.Valid);
Check("اسم الصيدلية صحيح", r1.Payload?.PharmacyName == "صيدلية الاختبار");

// --- سيناريو 2: نفس الملف على جهاز مختلف ---
Console.WriteLine("\nسيناريو 2: نفس الترخيص على جهاز مختلف (يجب الرفض)");
var r2 = validator.ValidateJson(goodEnvelope.ToJson(), OtherHardwareId);
Check("مرفوض", !r2.IsValid);
Check("السبب: عدم تطابق الجهاز", r2.Status == LicenseStatus.HardwareMismatch);

// --- سيناريو 3: تعديل البيانات بعد التوقيع (تزوير) ---
// ملاحظة: البيانات مخزَّنة كـ Base64 بداخل الملف، فتعديل النص الخام مباشرة
// (كما قد يحاول متطفل بسيط) لن يغيّر شيئاً فعلياً — لذلك نُحاكي هنا تزويراً
// حقيقياً: فك تشفير الحمولة، تعديلها، ثم إعادة ترميزها بـ Base64 من جديد،
// لكن بدون إعادة التوقيع بالمفتاح الخاص (لأن المهاجم لا يملكه) — هذا بالضبط
// ما يجب أن يوقفه التحقق من التوقيع الرقمي.
Console.WriteLine("\nسيناريو 3: محاولة تزوير (تعديل الحمولة بعد التوقيع)");
var parsedForTamper = LicenseFileEnvelope.FromJson(goodEnvelope.ToJson());
string decodedPayload = System.Text.Encoding.UTF8.GetString(parsedForTamper.GetPayloadBytes());
// نستهدف حقلاً ASCII (edition) بدل الاسم العربي، لأن System.Text.Json يُرمّز
// الأحرف غير اللاتينية كـ \u.... افتراضياً، فالبحث عن النص العربي الخام لن
// يطابق شيئاً (وهذا بالضبط ما كشفه فشل هذا الاختبار في أول تشغيل له!).
string tamperedPayload = decodedPayload.Replace("\"edition\":\"Standard\"", "\"edition\":\"Pro-Hacked\"");
if (tamperedPayload == decodedPayload)
    throw new Exception("فشل إعداد اختبار التزوير نفسه — النص المستهدف للاستبدال غير موجود في الحمولة المُسلسَلة.");
parsedForTamper.PayloadBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(tamperedPayload));
// التوقيع (SignatureBase64) بقي كما هو من الأصل — أي غير مطابق للحمولة الجديدة
var r3 = validator.ValidateJson(parsedForTamper.ToJson(), RealHardwareId);
Check("مرفوض", !r3.IsValid);
Check("السبب: توقيع غير صحيح", r3.Status == LicenseStatus.InvalidSignature);

// --- سيناريو 4: ترخيص منتهي الصلاحية ---
Console.WriteLine("\nسيناريو 4: ترخيص منتهي الصلاحية");
var expiredPayload = new LicensePayload
{
    PharmacyName = "صيدلية منتهية",
    HardwareId = RealHardwareId,
    ExpiresAtUtc = DateTime.UtcNow.AddDays(-1),
};
var expiredEnvelope = signer.Issue(expiredPayload);
var r4 = validator.ValidateJson(expiredEnvelope.ToJson(), RealHardwareId);
Check("مرفوض", !r4.IsValid);
Check("السبب: منتهي", r4.Status == LicenseStatus.Expired);

// --- سيناريو 5: ترخيص دائم (بلا تاريخ انتهاء) ---
Console.WriteLine("\nسيناريو 5: ترخيص دائم بلا تاريخ انتهاء");
var perpetualPayload = new LicensePayload { PharmacyName = "صيدلية دائمة", HardwareId = RealHardwareId, ExpiresAtUtc = null };
var perpetualEnvelope = signer.Issue(perpetualPayload);
var r5 = validator.ValidateJson(perpetualEnvelope.ToJson(), RealHardwareId, DateTime.UtcNow.AddYears(50));
Check("لا يزال صالحاً بعد 50 سنة", r5.IsValid);

// --- سيناريو 6: توقيع بمفتاح خاص مختلف (مفتاح غير معتمَد) ---
Console.WriteLine("\nسيناريو 6: ترخيص موقَّع بمفتاح خاص مزيّف (غير مطابق للمفتاح العام المُدمج)");
using var rogueRsa = RSA.Create(3072);
var rogueSigner = new LicenseSignerForTests(rogueRsa.ToXmlString(true));
var rogueEnvelope = rogueSigner.Issue(new LicensePayload { PharmacyName = "دخيل", HardwareId = RealHardwareId });
var r6 = validator.ValidateJson(rogueEnvelope.ToJson(), RealHardwareId);
Check("مرفوض", !r6.IsValid);
Check("السبب: توقيع غير صحيح", r6.Status == LicenseStatus.InvalidSignature);

// --- سيناريو 7: ملف تالف تماماً ---
Console.WriteLine("\nسيناريو 7: ملف ترخيص تالف/غير صالح البتة");
var r7 = validator.ValidateJson("{ this is not valid json at all", RealHardwareId);
Check("مرفوض", !r7.IsValid);
Check("السبب: تالف", r7.Status == LicenseStatus.Malformed);

// --- سيناريو 8: مفتاح الكود داخل الترخيص ---
Console.WriteLine("\nسيناريو 8: مفتاح تشغيل الكود المغلَّف داخل الترخيص");
var codeKey = CodeKeyProtector.GenerateKey();
var keyedPayload = new LicensePayload { PharmacyName = "صيدلية الكود", HardwareId = RealHardwareId };
keyedPayload.CodeKey = CodeKeyProtector.Wrap(codeKey, RealHardwareId.ToLowerInvariant(), keyedPayload.LicenseId);
var keyedEnvelope = signer.Issue(keyedPayload);
var r8 = validator.ValidateJson(keyedEnvelope.ToJson(), RealHardwareId);
Check("الترخيص صالح", r8.IsValid);
Check("يُفك مفتاح الكود نفسه (البصمة غير حساسة لحالة الأحرف)", r8.Payload != null && CodeKeyProtector.Unwrap(r8.Payload).SequenceEqual(codeKey));
var movedPayload = new LicensePayload { PharmacyName = "منقول", HardwareId = OtherHardwareId, LicenseId = keyedPayload.LicenseId, CodeKey = keyedPayload.CodeKey };
Check("نقل المفتاح المغلَّف لترخيص جهاز آخر لا يفكه", Throws(() => CodeKeyProtector.Unwrap(movedPayload)));
var otherIdPayload = new LicensePayload { HardwareId = RealHardwareId, CodeKey = keyedPayload.CodeKey };
Check("ولا لترخيص آخر على نفس الجهاز", Throws(() => CodeKeyProtector.Unwrap(otherIdPayload)));
Check("ترخيص قديم بلا مفتاح كود يُرفض بوضوح", Throws(() => CodeKeyProtector.Unwrap(goodPayload)));

// --- سيناريو 9: حزمة الكود المشفّرة ---
Console.WriteLine("\nسيناريو 9: حزمة الكود المشفّرة (app.mcb)");
var src = Directory.CreateTempSubdirectory("mcb-src").FullName;
Directory.CreateDirectory(Path.Combine(src, "api", "v1"));
Directory.CreateDirectory(Path.Combine(src, "database"));
Directory.CreateDirectory(Path.Combine(src, ".git"));
File.WriteAllText(Path.Combine(src, "index.php"), "<?php echo 'secret-code';");
File.WriteAllText(Path.Combine(src, "api", "v1", "x.php"), "<?php echo 1;");
File.WriteAllText(Path.Combine(src, "database", "medicore.sqlite"), "DATA");
File.WriteAllText(Path.Combine(src, ".git", "config"), "x");
File.WriteAllText(Path.Combine(src, "debug.log"), "x");
var bundleBytes = CodeBundle.Encrypt(CodeBundle.Zip(src), codeKey);
Check("الكود غير مقروء داخل الحزمة", System.Text.Encoding.UTF8.GetString(bundleBytes).IndexOf("secret-code", StringComparison.Ordinal) < 0);
var outDir = Directory.CreateTempSubdirectory("mcb-out").FullName;
int extracted = CodeBundle.ExtractTo(CodeBundle.Decrypt(bundleBytes, codeKey), outDir);
Check("يُفك بالمفتاح الصحيح (ملفان فقط: بلا database و.git والسجلات)", extracted == 2 && File.ReadAllText(Path.Combine(outDir, "index.php")).Contains("secret-code"));
Check("مفتاح خاطئ يُرفض", Throws(() => CodeBundle.Decrypt(bundleBytes, CodeKeyProtector.GenerateKey())));
var tampered = (byte[])bundleBytes.Clone();
tampered[^1] ^= 0xFF;
Check("تعديل بايت واحد في الحزمة يُرفض", Throws(() => CodeBundle.Decrypt(tampered, codeKey)));
Directory.Delete(src, true);
Directory.Delete(outDir, true);

// --- سيناريو 9ب: متابعة سجل أخطاء PHP ---
Console.WriteLine("\nسيناريو 9ب: عرض أخطاء PHP الجديدة في نافذة السيرفر");
var logFile = Path.Combine(Directory.CreateTempSubdirectory("mcs-log").FullName, "php_errors.log");
File.WriteAllText(logFile, "old error before start\n");
var tail = new LogTail(logFile);
Check("لا يُعرض ما قبل التشغيل", tail.ReadNew().Count == 0);
File.AppendAllText(logFile, "PHP Deprecated:  x\nPHP Fatal error:  Uncaught Error in index.php:12\nPHP Warning:  partial");
var newLines = tail.ReadNew();
Check("يُعرض الخطأ الجديد فقط بدون Deprecated، والسطر غير المكتمل ينتظر",
    newLines.Count == 1 && newLines[0].Contains("Fatal error"));
File.AppendAllText(logFile, " line\n");
Check("يكتمل السطر لاحقاً", tail.ReadNew() is var more && more.Count == 1 && more[0] == "PHP Warning:  partial line");

// --- سيناريو 9ج: فحص المنفذ ---
Console.WriteLine("\nسيناريو 9ج: فحص المنفذ (برنامج يستمع فعلاً فقط)");
var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
probe.Start();
int probePort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
Check("منفذ عليه برنامج يستمع = مشغول", !PhpServer.IsPortFree("0.0.0.0", probePort) && !PhpServer.IsPortFree("127.0.0.1", probePort));
probe.Stop();
Check("بعد إغلاقه = متاح", PhpServer.IsPortFree("0.0.0.0", probePort));

// --- سيناريو 10 (اختياري): تشغيل فعلي لنظام PHP من الحزمة المشفّرة ---
var phpDir = Environment.GetEnvironmentVariable("MEDICORE_PHP_DIR");
var php = PhpServer.LocatePhp(AppContext.BaseDirectory);
if (!string.IsNullOrEmpty(phpDir) && php != null)
{
    Console.WriteLine("\nسيناريو 10: تشغيل النظام الفعلي عبر Medicore Server (PHP حقيقي)");
    var root = Directory.CreateTempSubdirectory("mcs-e2e").FullName;
    var paths = new HostPaths(Path.Combine(root, "app"), Path.Combine(root, "data"), Path.Combine(root, "rt"));
    Directory.CreateDirectory(paths.AppDir);
    File.WriteAllBytes(paths.BundlePath, CodeBundle.Encrypt(CodeBundle.Zip(phpDir), codeKey));
    paths.EnsureDataDirs();
    var db = Path.Combine(phpDir, "database", "medicore.sqlite");
    if (File.Exists(db)) File.Copy(db, paths.DatabaseFile);

    // بقايا تشغيل سابق انتهى فجأة تُحذف (دون المساس بالبيانات)
    var stale = Path.Combine(paths.RuntimeRoot, "stale", "www", RuntimeWorkspace.AppFolder);
    Directory.CreateDirectory(stale);
    DirectoryLink.Create(Path.Combine(stale, "database"), paths.DatabaseDir);

    var ws = LicensedLauncher.PrepareWorkspace(keyedPayload, paths);
    Check("تنظيف بقايا التشغيل السابق دون المساس بالبيانات", !Directory.Exists(Path.Combine(paths.RuntimeRoot, "stale")) && Directory.Exists(paths.DatabaseDir));
    bool freshDb = !File.Exists(paths.DatabaseFile);
    if (freshDb)
    {
        DatabaseSetup.CreateFresh(php, ws, paths);
        Check("إنشاء قاعدة بيانات جديدة (install/init_db.php)", File.Exists(paths.DatabaseFile));
        Check("لا تُستبدل قاعدة بيانات موجودة", Throws(() => DatabaseSetup.CreateFresh(php, ws, paths)));
    }
    Check("الكود مفكوك في مجلد التشغيل", File.Exists(Path.Combine(ws.AppRoot, "index.php")) && ws.FileCount > 100);
    Check("database مربوط بمجلد البيانات", File.Exists(Path.Combine(ws.AppRoot, "database", "medicore.sqlite")) == File.Exists(paths.DatabaseFile));

    int port;
    using (var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)) { l.Start(); port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
    using var server = new PhpServer();
    server.Start(new PhpServer.StartOptions(php, ws.DocRoot, "127.0.0.1", port, LicensedLauncher.PhpEnvironment(keyedPayload, paths), paths.LogsDir, paths.SessionsDir, paths.PhpPidFile));
    Check("السيرفر يعمل", server.State == ServerState.Running);
    Check("رقم عملية PHP محفوظ (لإيقافه لو أُنهي البرنامج قسرياً)", File.Exists(paths.PhpPidFile));
    using var http = new HttpClient();
    var res = http.GetAsync($"http://127.0.0.1:{port}/medicore/api/v1/price_increase.php").Result;
    var body = res.Content.ReadAsStringAsync().Result;
    Check($"API يرد عبر /medicore/ (مرخَّص → يطلب تسجيل الدخول فقط: {(int)res.StatusCode})", (int)res.StatusCode == 401 && !body.Contains("unlicensed"));
    // ملفات البيانات لا تُنزَّل مباشرة (سيرفر PHP المدمج يرسل أي ملف تحت الجذر بدون الموجّه)
    File.WriteAllText(Path.Combine(paths.DatabaseDir, "probe.sqlite"), "secret");
    Directory.CreateDirectory(Path.Combine(paths.DatabaseDir, "backups"));
    File.WriteAllText(Path.Combine(paths.DatabaseDir, "backups", "probe.txt"), "secret");
    var leaks = new[]
    {
        "/medicore/database/probe.sqlite", "/medicore/DATABASE/probe.sqlite", "/medicore//database/probe.sqlite",
        "/medicore/./database/probe.sqlite", "/medicore/database%2fprobe.sqlite", "/medicore/%64atabase/probe.sqlite",
        "/medicore/database/backups/probe.txt", "/medicore/database/probe.sqlite.", "/medicore/assets/../database/probe.sqlite",
    }.Where(u =>
    {
        var r = http.GetAsync($"http://127.0.0.1:{port}{u}").Result;
        return r.IsSuccessStatusCode || r.Content.ReadAsStringAsync().Result.Contains("secret");
    }).ToList();
    Check($"قاعدة البيانات والنسخ لا تُنزَّل من /medicore/database/ ({(leaks.Count == 0 ? "محمية" : string.Join(" ، ", leaks))})", leaks.Count == 0);
    var css = http.GetAsync($"http://127.0.0.1:{port}/medicore/assets/css/bootstrap.min.css").Result;
    Check($"الملفات العادية (assets) تعمل مع الموجّه ({(int)css.StatusCode})", css.IsSuccessStatusCode);
    if (freshDb)
    {
        var login = http.PostAsync($"http://127.0.0.1:{port}/medicore/api/v1/auth/login.php",
            new StringContent("{\"username\":\"admin\",\"password\":\"password\"}", System.Text.Encoding.UTF8, "application/json")).Result;
        var loginBody = login.Content.ReadAsStringAsync().Result;
        Check($"تسجيل الدخول admin على القاعدة الجديدة ({(int)login.StatusCode})", login.IsSuccessStatusCode && loginBody.Contains("token"));
        // إشعار سداد مورد محفوظ في uploads (رابط لمجلد البيانات — symlink على الماك) يُعرض
        var token = System.Text.Json.JsonDocument.Parse(loginBody).RootElement.GetProperty("data").GetProperty("token").GetString();
        Directory.CreateDirectory(Path.Combine(paths.UploadsDir, "receipts"));
        File.WriteAllBytes(Path.Combine(paths.UploadsDir, "receipts", "r1.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var dbInsert = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(php, new[] { "-r",
            "$d = new PDO('sqlite:' . $argv[1]); $d->exec(\"INSERT INTO supplier_payments (invoice_id, amount, receipt_image) VALUES (1, 10, 'uploads/receipts/r1.png')\"); echo $d->lastInsertId();",
            paths.DatabaseFile }) { RedirectStandardOutput = true })!;
        var paymentId = dbInsert.StandardOutput.ReadToEnd().Trim();
        dbInsert.WaitForExit();
        var receiptReq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/medicore/api/v1/settlements.php?receipt={paymentId}");
        receiptReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var receipt = http.SendAsync(receiptReq).Result;
        Check($"إشعار سداد المورد من مجلد المرفقات المربوط يُعرض ({(int)receipt.StatusCode})",
            receipt.IsSuccessStatusCode && receipt.Content.Headers.ContentType?.MediaType == "image/png");
        var web = http.GetAsync($"http://127.0.0.1:{port}/medicore/views/auth/login.php").Result;
        Check($"صفحة الدخول في المتصفح تعمل ({(int)web.StatusCode})", web.IsSuccessStatusCode);
    }

    // نفس الكود المفكوك بدون Medicore Server (نسخه وتشغيله بـ PHP عادي) → مرفوض
    int port2 = port + 1;
    var raw = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(php, $"-S 127.0.0.1:{port2} -t {ws.DocRoot}") { RedirectStandardError = true, RedirectStandardOutput = true });
    Thread.Sleep(700);
    var res2 = http.GetAsync($"http://127.0.0.1:{port2}/medicore/api/v1/price_increase.php").Result;
    var body2 = res2.Content.ReadAsStringAsync().Result;
    raw!.Kill();
    Check($"تشغيل الكود بدون Medicore Server مرفوض ({(int)res2.StatusCode})", (int)res2.StatusCode == 403 && body2.Contains("unlicensed"));

    // استيراد قاعدة بيانات أثناء التشغيل: إيقاف ← استيراد ← إعادة تشغيل على نفس المنفذ فوراً
    var importDb = Environment.GetEnvironmentVariable("MEDICORE_IMPORT_DB");
    if (!string.IsNullOrEmpty(importDb) && File.Exists(importDb))
    {
        var before = File.ReadAllBytes(paths.DatabaseFile).Length;
        server.Stop();
        var junk = Path.Combine(root, "not-a-db.sqlite");
        File.WriteAllText(junk, "hello");
        Check("رفض ملف ليس قاعدة SQLite", Throws(() => DatabaseImport.Import(junk, paths, php)));
        Check("… والقاعدة الحالية لم تتغير", File.ReadAllBytes(paths.DatabaseFile).Length == before);
        Check("رفض استيراد القاعدة الحالية نفسها", Throws(() => DatabaseImport.Import(paths.DatabaseFile, paths, php))
            && Throws(() => DatabaseImport.Import(Path.Combine(ws.AppRoot, "database", "medicore.sqlite"), paths, php)));
        File.WriteAllText(Path.Combine(paths.SessionsDir, "sess_old"), "user_id|i:99;");
        var imported = DatabaseImport.Import(importDb, paths, php);
        Check($"استيراد القاعدة ({imported.Summary})", imported.Summary.Contains("مستخدم") && imported.BackupFile != null && File.Exists(imported.BackupFile));
        Check("مسح الجلسات القديمة", !File.Exists(Path.Combine(paths.SessionsDir, "sess_old")));
        Check("المنفذ متاح فوراً بعد الإيقاف", PhpServer.IsPortFree("127.0.0.1", port));
        server.Start(new PhpServer.StartOptions(php, ws.DocRoot, "127.0.0.1", port, LicensedLauncher.PhpEnvironment(keyedPayload, paths), paths.LogsDir, paths.SessionsDir));
        Check("السيرفر عاد للعمل بعد الاستيراد", server.State == ServerState.Running);
        var afterWeb = http.GetAsync($"http://127.0.0.1:{port}/medicore/views/auth/login.php").Result;
        var afterApi = http.GetAsync($"http://127.0.0.1:{port}/medicore/api/v1/price_increase.php").Result;
        Check($"النظام يعمل على المتصفح بعد الاستيراد ({(int)afterWeb.StatusCode}/{(int)afterApi.StatusCode})",
            afterWeb.IsSuccessStatusCode && (int)afterApi.StatusCode == 401);
    }

    server.Stop();
    ws.Dispose();
    Check("إيقاف: الكود المفكوك حُذف", !Directory.Exists(ws.Root));
    Check("إيقاف: قاعدة البيانات باقية", !File.Exists(db) || File.Exists(paths.DatabaseFile));
    Directory.Delete(root, true);
}
else
{
    Console.WriteLine("\n(تخطي سيناريو التشغيل الفعلي: عيّن MEDICORE_PHP_DIR لمجلد نظام PHP ووفّر php)");
}

// --- التحديث عن بُعد (CodeUpdater) ---
Console.WriteLine("\nسيناريو: التحديث عن بُعد");
{
    var tmp = Path.Combine(Path.GetTempPath(), "mc-upd-" + Guid.NewGuid().ToString("N"));
    var paths = new HostPaths(Path.Combine(tmp, "app") + Path.DirectorySeparatorChar, Path.Combine(tmp, "data"), Path.Combine(tmp, "rt"));
    Directory.CreateDirectory(paths.AppDir);
    var installedV1 = new byte[] { 1, 1, 1 };
    File.WriteAllBytes(Path.Combine(paths.AppDir, "app.mcb"), installedV1);
    var up = new CodeUpdater(paths);
    up.Announce(paths.FindBundle());
    Check("host.json يعلن دعم التحديث", File.ReadAllText(up.HostInfoFile).Contains("\"remote_update\": 1"));
    Check("بدون تحديث: يُستخدم المثبَّت", paths.FindBundle() == paths.FindInstalledBundle());
    Check("لا طلبات معلّقة", up.TakePending() is null);

    var v2 = new byte[] { 2, 2, 2, 2 };
    File.WriteAllBytes(up.IncomingFile, v2);
    var p2 = up.TakePending();
    Check("يلتقط الملف الوارد ويزيله", p2 is { Rollback: false } && p2.Bundle.SequenceEqual(v2) && !File.Exists(up.IncomingFile));
    up.Commit(v2, installedV1, rollback: false);
    Check("بعد التحديث: تُستخدم نسخة التحديث", paths.FindBundle() == up.OverrideFile && File.ReadAllBytes(paths.FindBundle()!).SequenceEqual(v2));
    Check("النسخة السابقة محفوظة", File.ReadAllBytes(up.PreviousFile).SequenceEqual(installedV1));
    Check("active.json: مصدر remote", File.ReadAllText(up.ActiveInfoFile).Contains("\"remote\""));

    File.WriteAllText(up.RollbackRequestFile, "1");
    var back = up.TakePending();
    Check("طلب التراجع يعيد النسخة السابقة", back is { Rollback: true } && back.Bundle.SequenceEqual(installedV1));
    up.Commit(back!.Bundle, v2, rollback: true);
    Check("بعد التراجع: العاملة هي السابقة، والتحديث صار السابق",
        File.ReadAllBytes(paths.FindBundle()!).SequenceEqual(installedV1) && File.ReadAllBytes(up.PreviousFile).SequenceEqual(v2));

    // تثبيت إصدار أحدث بملف التثبيت (app.mcb المثبَّت تغيّر): يتقدّم على نسخة التحديث عن بُعد
    File.WriteAllBytes(Path.Combine(paths.AppDir, "app.mcb"), new byte[] { 3, 3, 3 });
    Check("ملف التثبيت الأحدث يتقدّم على التحديث عن بُعد", paths.FindBundle() == paths.FindInstalledBundle());

    File.Delete(up.PreviousFile);
    File.WriteAllText(up.RollbackRequestFile, "1");
    Check("تراجع بلا نسخة سابقة: يُرفض بحالة واضحة", up.TakePending() is null && File.ReadAllText(up.StatusFile).Contains("failed"));
    Directory.Delete(tmp, true);

    // حزمة مشفّرة بمفتاح آخر أو ليست نظام Medicore
    var k1 = CodeKeyProtector.GenerateKey();
    var srcDir = Path.Combine(Path.GetTempPath(), "mc-src-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(srcDir, "api"));
    File.WriteAllText(Path.Combine(srcDir, "index.php"), "<?php");
    File.WriteAllText(Path.Combine(srcDir, "api", "x.php"), "<?php");
    var zipOk = CodeBundle.Zip(srcDir, extra: new Dictionary<string, string> { ["build.json"] = "{}" });
    Check("حزمة نظام Medicore صالحة", CodeBundle.LooksLikeMedicore(zipOk));
    Check("build.json داخل الحزمة", new System.IO.Compression.ZipArchive(new MemoryStream(zipOk)).GetEntry("build.json") is not null);
    File.Delete(Path.Combine(srcDir, "index.php"));
    Check("حزمة بلا index.php مرفوضة", !CodeBundle.LooksLikeMedicore(CodeBundle.Zip(srcDir)));
    Check("مفتاح آخر لا يفك الحزمة", Throws(() => CodeBundle.Decrypt(CodeBundle.Encrypt(zipOk, k1), CodeKeyProtector.GenerateKey())));
    Directory.Delete(srcDir, true);
}


// --- نسخة الماك: بصمة الجهاز ---
Console.WriteLine("\nسيناريو ماك 1: بصمة جهاز الماك");
const string sampleIoreg = """
+-o J314sAP  <class IOPlatformExpertDevice, id 0x100000215, registered, matched, active, busy 0 (85 ms), retain 39>
    {
      "IOPolledInterface" = "AppleARMWatchdogTimerHibernateHandler is not serializable"
      "IOPlatformSerialNumber" = "C02XG0FDJGH5"
      "IOPlatformUUID" = "1A2B3C4D-5E6F-7081-92A3-B4C5D6E7F809"
      "model" = <"MacBookPro18,3">
    }
""";
var (uuid, serial) = HardwareIdentifier.ParseIoreg(sampleIoreg);
Check("قراءة IOPlatformUUID و IOPlatformSerialNumber من ioreg", uuid == "1A2B3C4D-5E6F-7081-92A3-B4C5D6E7F809" && serial == "C02XG0FDJGH5");
var macId = HardwareIdentifier.Compute(uuid!, serial!);
Check($"صيغة البصمة مثل ويندوز XXXX-XXXX-XXXX-XXXX ({macId})", System.Text.RegularExpressions.Regex.IsMatch(macId, "^[0-9A-F]{4}(-[0-9A-F]{4}){3}$"));
Check("ثابتة لنفس الجهاز (وغير حساسة لحالة الأحرف والمسافات)", HardwareIdentifier.Compute(" " + uuid!.ToLowerInvariant(), serial!) == macId);
Check("تختلف لجهاز آخر", HardwareIdentifier.Compute("00000000-0000-0000-0000-000000000000", serial!) != macId
                         && HardwareIdentifier.Compute(uuid!, "OTHERSERIAL") != macId);
Check("مخرجات فارغة لا تُسقط البرنامج", HardwareIdentifier.ParseIoreg("") == (null, null));
Check("GetCurrentId يعمل حتى بدون ioreg (لينكس)", System.Text.RegularExpressions.Regex.IsMatch(HardwareIdentifier.GetCurrentId(), "^[0-9A-F]{4}(-[0-9A-F]{4}){3}$"));
var macPayload = new LicensePayload { PharmacyName = "صيدلية الماك", HardwareId = macId };
macPayload.CodeKey = CodeKeyProtector.Wrap(codeKey, macId, macPayload.LicenseId);
var rMac = validator.ValidateJson(signer.Issue(macPayload).ToJson(), macId);
Check("ترخيص صادر لبصمة الماك يعمل ويفك مفتاح الكود", rMac.IsValid && CodeKeyProtector.Unwrap(rMac.Payload!).SequenceEqual(codeKey));

// --- نسخة الماك: أماكن الملفات ---
Console.WriteLine("\nسيناريو ماك 2: أماكن ملفات البرنامج والبيانات");
{
    var bundleRoot = Directory.CreateTempSubdirectory("mc-app").FullName;
    var macos = Path.Combine(bundleRoot, "Medicore Server.app", "Contents", "MacOS");
    var resources = Path.Combine(bundleRoot, "Medicore Server.app", "Contents", "Resources");
    Directory.CreateDirectory(macos);
    Directory.CreateDirectory(resources);
    Check("داخل حزمة .app: app.mcb و php من Contents/Resources", HostPaths.ResourcesDir(macos + "/").TrimEnd('/') == resources);
    Check("أثناء التطوير: مجلد البرنامج كما هو", HostPaths.ResourcesDir(bundleRoot + "/") == bundleRoot + "/");
    Directory.CreateDirectory(Path.Combine(resources, "php"));
    var fakePhp = Path.Combine(resources, "php", "php");
    File.WriteAllText(fakePhp, "");
    Check("PHP المحمول داخل الحزمة يُفضَّل على المثبَّت", PhpServer.LocatePhp(resources) == fakePhp);
    var defaults = HostPaths.Default();
    Check("البيانات في ~/Library/Application Support/Medicore/data (تبقى عند تحديث البرنامج)",
        defaults.DataDir.EndsWith(Path.Combine("Library", "Application Support", "Medicore", "data")));
    var phpArgs = PhpServer.ExtensionArguments("/opt/homebrew/bin/php");
    Check("include_path بدون \".\" (قرص الماك لا يفرّق بين الحروف مثل ويندوز)",
        phpArgs.Contains("include_path=/opt/homebrew/bin") && !phpArgs.Contains("-n"));
    Directory.Delete(bundleRoot, true);
}

// --- نسخة الماك: PHP يتيم من تشغيل سابق أُنهي قسرياً ---
Console.WriteLine("\nسيناريو ماك 3: إيقاف PHP يتيم يحجز المنفذ");
if (php is not null)
{
    var pidDir = Directory.CreateTempSubdirectory("mc-pid").FullName;
    var pidFile = Path.Combine(pidDir, "php.pid");
    var orphan = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(php, "-r \"sleep(60);\"") { UseShellExecute = false })!;
    File.WriteAllText(pidFile, orphan.Id.ToString());
    PhpServer.KillOrphan(pidFile);
    Check("PHP اليتيم أُوقف وملف الرقم حُذف", orphan.WaitForExit(5000) && !File.Exists(pidFile));
    var notPhp = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
    File.WriteAllText(pidFile, notPhp.Id.ToString());
    PhpServer.KillOrphan(pidFile);
    Check("رقم عملية أُعيد استخدامه لبرنامج آخر لا يُوقَف", !notPhp.HasExited);
    notPhp.Kill();
    Directory.Delete(pidDir, true);
}
else
{
    Console.WriteLine("  (تخطي: لا يوجد php)");
}

Console.WriteLine($"\n=== النتيجة: {passed} نجح، {failed} فشل ===");
return failed == 0 ? 0 : 1;

bool Throws(Action a)
{
    try { a(); return false; } catch { return true; }
}

// نسخة مصغّرة من LicenseSigner لتشغيل الاختبارات بدون الاعتماد على مشروع
// Medicore.LicenseGenerator (لتفادي دورة اعتماد دائرية بين مشاريع الاختبار والإصدار)
internal sealed class LicenseSignerForTests
{
    private readonly RSA _key;
    public LicenseSignerForTests(string privateKeyXml)
    {
        _key = RSA.Create();
        _key.FromXmlString(privateKeyXml);
    }

    public LicenseFileEnvelope Issue(LicensePayload payload)
    {
        byte[] payloadBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload, LicenseFileEnvelope.PayloadJsonOptions);
        byte[] signature = _key.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return LicenseFileEnvelope.Create(payloadBytes, signature);
    }
}
