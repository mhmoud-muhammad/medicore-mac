using Medicore.Licensing.Core;

// ============================================================================
// أداة المطوّر (لا تُشحن للعميل): تجهيز كود PHP المشفّر لـ Medicore Server.
//
//   dotnet run --project Medicore.Packager -- keygen code.key
//       يولّد مفتاح تشفير الكود مرة واحدة (احفظه مع private.key.xml — سرّي تماماً)
//
//   dotnet run --project Medicore.Packager -- pack <مجلد medicore> app.mcb code.key
//       يضغط كود PHP (بدون database/uploads/.git...) ويشفّره → app.mcb بجانب Medicore.App.exe
// ============================================================================

static int Usage()
{
    Console.WriteLine("الاستخدام:");
    Console.WriteLine("  keygen <code.key>                         توليد مفتاح الكود (مرة واحدة)");
    Console.WriteLine("  pack <php-dir> <out.mcb> <code.key>       تشفير كود PHP إلى حزمة");
    Console.WriteLine("  verify <app.mcb> <code.key>               التحقق من أن الحزمة تُفك بهذا المفتاح");
    return 2;
}

static byte[] ReadKey(string path)
{
    var key = Convert.FromBase64String(File.ReadAllText(path).Trim());
    if (key.Length != CodeKeyProtector.KeySize) throw new InvalidDataException("code.key غير صالح.");
    return key;
}

try
{
    switch (args.FirstOrDefault())
    {
        case "keygen" when args.Length == 2:
            if (File.Exists(args[1]))
            {
                Console.Error.WriteLine($"⛔ {args[1]} موجود بالفعل — مفتاح جديد يُبطل كل التراخيص الصادرة بالمفتاح الحالي. احذفه يدوياً إن كنت متأكداً.");
                return 1;
            }
            File.WriteAllText(args[1], Convert.ToBase64String(CodeKeyProtector.GenerateKey()));
            Console.WriteLine($"✅ تم توليد مفتاح الكود: {args[1]} — احفظه في مكان آمن ولا ترفعه على Git.");
            return 0;

        case "pack" when args.Length == 4:
        {
            var src = Path.GetFullPath(args[1]);
            if (!File.Exists(Path.Combine(src, "index.php")) || !Directory.Exists(Path.Combine(src, "api")))
            {
                Console.Error.WriteLine($"⛔ {src} لا يبدو مجلد نظام Medicore (لا يوجد index.php و api/).");
                return 1;
            }
            int files = 0;
            // build.json: تاريخ بناء النسخة — يظهر في النظام (الإعدادات ← تحديث النظام)
            var build = System.Text.Json.JsonSerializer.Serialize(new { built_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") });
            var zip = CodeBundle.Zip(src, onFile: _ => files++, extra: new Dictionary<string, string> { ["build.json"] = build });
            var bundle = CodeBundle.Encrypt(zip, ReadKey(args[3]));
            File.WriteAllBytes(args[2], bundle);
            Console.WriteLine($"✅ {files} ملف → {args[2]} ({bundle.Length / 1024.0 / 1024.0:0.00} MB مشفّرة)");
            return 0;
        }

        case "verify" when args.Length == 3:
        {
            var zip = CodeBundle.Decrypt(File.ReadAllBytes(args[1]), ReadKey(args[2]));
            using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
            Console.WriteLine($"✅ الحزمة سليمة: {archive.Entries.Count} ملف.");
            return 0;
        }

        default:
            return Usage();
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("⛔ " + ex.Message);
    return 1;
}
