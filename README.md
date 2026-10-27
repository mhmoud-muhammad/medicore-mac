# Medicore Server للماك 🍎

نسخة الماك من **Medicore Server** — نفس نظام `medicore-C-` تماماً، يعمل على أجهزة ماك (Apple Silicon و Intel):

1. يتحقق من الترخيص (توقيع RSA + بصمة جهاز الماك + الانتهاء).
2. يفك **مفتاح الكود** المغلَّف داخل الترخيص، ثم يفك `app.mcb` (نظام PHP مشفّراً) إلى مجلد تشغيل مؤقت.
3. يشغّل PHP المحمول (مدمج داخل البرنامج — لا يحتاج XAMPP ولا Homebrew) على المنفذ 80 للشبكة المحلية:
   نفس الروابط `http://IP/medicore/...` — **تطبيق الموبايل ونقاط البيع تعمل بدون أي تعديل**.
4. عند الخروج يوقف PHP ويحذف الكود المفكوك.

| نفس ما في ويندوز | الفرق في الماك |
|---|---|
| نفس نظام PHP (`Medicore.Web` في مستودع `medicore-C-`) ونفس ملف `app.mcb` | البرنامج `Medicore Server.app` بدل `Medicore.App.exe` |
| نفس أداة إصدار التراخيص (`Medicore.LicenseGenerator`) ونفس المفاتيح | البصمة تُحسب من Hardware UUID + الرقم التسلسلي للماك |
| نفس التحديث عن بُعد (`push-update.ps1` أو صفحة الإعدادات) | أيقونة في **شريط القوائم أعلى الشاشة** بدل بجانب الساعة |
| نفس قاعدة البيانات (`medicore.sqlite`) — تُنقل من ويندوز كما هي | التشغيل التلقائي عبر LaunchAgent بدل الريجستري |

---

## 📦 تجهيز ملف التثبيت (DMG)

### الطريقة 1: على جهاز ماك عندك
مرة واحدة:
```bash
brew install --cask dotnet-sdk          # أو من https://dotnet.microsoft.com/download/dotnet/10.0
git clone https://github.com/mhmoud-muhammad/medicore-C-.git
git clone https://github.com/mhmoud-muhammad/medicore-mac.git
cp /path/to/code.key medicore-mac/       # نفس code.key المحمَّل في أداة التراخيص (سرّي — مستثنى من Git)
```
كل إصدار:
```bash
cd medicore-mac
./build-mac.sh --version 1.0.0
```
الناتج في `dist/`:
- `MedicoreServer-1.0.0-AppleSilicon.dmg` — لأجهزة M1/M2/M3/M4 وما بعدها
- `MedicoreServer-1.0.0-Intel.dmg` — لأجهزة ماك Intel

السكربت يشفّر `../medicore-C-/Medicore.Web` إلى `app.mcb`، ينزّل PHP محمولاً للماك
([static-php-cli](https://static-php.dev) — ملف واحد بكل الإضافات: sqlite, mbstring, openssl, curl, gd, zip, OPcache)،
يجمّع البرنامج ويوقّعه، ويصنع DMG. خيارات مفيدة:

| الخيار | الغرض |
|---|---|
| `--arch arm64` أو `--arch x64` | معمارية واحدة فقط |
| `--bundle app.mcb` | استخدام `app.mcb` جاهز (مثلاً نفس الملف من بناء ويندوز) بدل التشفير |
| `--sign "Developer ID Application: …"` | توقيع بشهادة Apple (انظر "التثبيت بسلاسة" أدناه) |
| `--notarize PROFILE` | توثيق Apple — يختفي أي تحذير عند العميل |

### الطريقة 2: GitHub Actions (بدون إعداد أي شيء على جهازك)
1. في هذا المستودع: Settings ← Secrets and variables ← Actions ← أضف:
   - `MEDICORE_CODE_KEY`: محتوى ملف `code.key`
   - `MEDICORE_C_TOKEN`: [Personal Access Token](https://github.com/settings/personal-access-tokens) بصلاحية قراءة Contents لمستودع `medicore-C-`
2. Actions ← **بناء نسخة الماك** ← Run workflow ← أدخل الإصدار.
3. حمّل ملفات DMG من Artifacts في صفحة التشغيل.

---

## 🖥️ التثبيت على ماك الصيدلية

1. افتح ملف DMG المناسب للجهاز ( → About This Mac: Chip **Apple M…** = AppleSilicon، **Intel** = Intel)،
   واسحب **Medicore Server** إلى مجلد **Applications**.
2. أول فتح — البرنامج غير موقَّع بشهادة Apple (إلا لو بنيته بـ `--sign`)، فيظهر تحذير:
   - افتح **System Settings ← Privacy & Security**، انزل للأسفل واضغط **Open Anyway** ثم **Open**.
   - أو من Terminal مرة واحدة: `xattr -dr com.apple.quarantine "/Applications/Medicore Server.app"`
3. يظهر سؤال **"Do you want the application "php" to accept incoming network connections?"** ← **Allow**
   (ضروري حتى تصل نقاط البيع والتطبيق للسيرفر).
4. شاشة **تفعيل الترخيص** تعرض بصمة الجهاز ← اضغط "نسخ" وأرسلها لنفسك ← أصدر ترخيصاً من
   **نفس** أداة التراخيص على ويندوز (`Medicore.LicenseGenerator` — `code.key` محمَّل فيها) ← انقل `license.lic`
   للماك ← "اختيار ملف الترخيص وتثبيته".
5. قاعدة البيانات:
   - **صيدلية تعمل حالياً على ويندوز:** انسخ `C:\ProgramData\Medicore\data\database\medicore.sqlite` (أوقف
     Medicore Server على ويندوز أولاً) ← في الماك اضغط "🗄 استيراد قاعدة بيانات". ثم انسخ محتوى مجلد
     `uploads` القديم إلى مجلد البيانات (زر "📁 مجلد البيانات").
   - **صيدلية جديدة:** "🆕 قاعدة بيانات جديدة" (الدخول: admin / password — غيّرها فوراً).
6. فعّل ☑ **تشغيل تلقائي عند تسجيل الدخول** واضغط **📌 اختصار Medicore على سطح المكتب**.
7. في التطبيق/نقاط البيع: عنوان السيرفر = أحد العناوين المعروضة في النافذة (مثل `http://192.168.1.20/medicore/`).
   يُفضَّل تثبيت IP للماك من الراوتر (DHCP Reservation) حتى لا يتغيّر.

### الاستخدام اليومي
- **أيقونة Medicore في شريط القوائم أعلى الشاشة**: حالة السيرفر، فتح النافذة، فتح النظام في المتصفح، والخروج.
- ☑ **الإغلاق يُبقي النظام يعمل في الخلفية** (افتراضي): زر الإغلاق الأحمر يخفي النافذة فقط.
  الخروج النهائي من أيقونة شريط القوائم ← "خروج (إيقاف النظام)". (Cmd+Q يوقف النظام أيضاً.)
- ☑ **منع الجهاز من النوم أثناء عمل السيرفر** (افتراضي): الماك لا ينام فتبقى نقاط البيع متصلة (الشاشة تنطفئ عادياً).
  في MacBook يُفضّل إبقاؤه موصولاً بالشاحن.
- ☑ **تشغيل تلقائي عند تسجيل الدخول**: يبدأ السيرفر في الخلفية مع تشغيل الماك. لجهاز صيدلية مخصص فعّل أيضاً
  تسجيل الدخول التلقائي: System Settings ← Users & Groups ← Automatically log in as.
  (يظهر إشعار من macOS "Background Items Added" — طبيعي.) لو توقف السيرفر بخطأ يعيد macOS تشغيله تلقائياً.
- **اختصار "Medicore" على سطح المكتب** يفتح النظام في المتصفح مباشرة (ويشغّل السيرفر في الخلفية إن لم يكن يعمل).

### ⬆️ التحديث
- **كود النظام (PHP):** تماماً مثل ويندوز — النظام ← الإعدادات ← تحديث النظام ← ارفع `app.mcb`، أو
  `push-update.ps1 -Servers <IP الماك>` من جهازك. **نفس ملف `app.mcb` لويندوز والماك.**
- **البرنامج نفسه:** ملف DMG الأحدث ← اسحبه فوق القديم في Applications (Replace). البيانات والترخيص
  في `~/Library/Application Support/Medicore` لا تتأثر.

### ✨ التثبيت بسلاسة تامة (اختياري، يُنصح به عند التوزيع على صيدليات كثيرة)
بدون شهادة Apple يحتاج العميل خطوة "Open Anyway" مرة واحدة، ويسأل جدار الحماية عن php بعد كل تحديث للبرنامج.
مع حساب [Apple Developer](https://developer.apple.com/programs/) (99$ سنوياً):
```bash
xcrun notarytool store-credentials medicore-notary --apple-id you@example.com --team-id TEAMID
./build-mac.sh --version 1.0.0 --sign "Developer ID Application: Your Name (TEAMID)" --notarize medicore-notary
```
فيفتح البرنامج مباشرة بدون أي تحذير، ويتذكر جدار الحماية السماح عبر التحديثات.

---

## 📂 أماكن الملفات على الماك

| المكان | المحتوى |
|---|---|
| `/Applications/Medicore Server.app/Contents/Resources/` | `app.mcb` (الكود المشفّر) + `php/php` (PHP محمول) |
| `~/Library/Application Support/Medicore/license.lic` | الترخيص |
| `~/Library/Application Support/Medicore/data/` | قاعدة البيانات، المرفقات، السجلات، الجلسات، الإعدادات (`host.json`) — **لا يُحذف أبداً** |
| `~/Library/Application Support/Medicore/data/app/` | نسخة التحديث عن بُعد والنسخة السابقة |
| `~/Library/Application Support/Medicore/.rt/` | الكود المفكوك أثناء التشغيل فقط (يُحذف عند الإغلاق) |
| `~/Library/LaunchAgents/com.medicore.server.plist` | التشغيل التلقائي (موجود فقط إن كان مفعّلاً) |

النسخ الاحتياطي: انسخ مجلد `data` كاملاً (أو استخدم النسخ الاحتياطي من داخل النظام كالمعتاد).

---

## 🧩 هيكل المستودع

| المشروع | الغرض |
|---|---|
| `Medicore.Mac` | البرنامج (Avalonia UI — واجهة ماك أصلية): شاشة التفعيل، نافذة السيرفر، أيقونة شريط القوائم |
| `Medicore.Licensing.Mac` | بصمة جهاز الماك (`ioreg`: IOPlatformUUID + IOPlatformSerialNumber) |
| `Medicore.Licensing.Core` | التحقق من الترخيص وتشفير الكود — **منسوخ كما هو من medicore-C-** |
| `Medicore.Server.Core` | تشغيل PHP، فك الكود، الاستيراد، التحديث عن بُعد — من medicore-C- مع تعديلات الماك |
| `Medicore.Packager` | تشفير نظام PHP إلى `app.mcb` — **منسوخ كما هو من medicore-C-** |
| `Medicore.Mac.Tests` | 73 اختبار: الترخيص، الحزمة، السيرفر، التحديث عن بُعد، أجزاء الماك، وتشغيل فعلي لنظام PHP |
| `build-mac.sh` | بناء `Medicore Server.app` و DMG |
| `macos/` | الأيقونة (`medicore.icns` من أيقونة تطبيق الموبايل) وصلاحيات التوقيع |

تعديلات الماك في `Medicore.Server.Core` (مقارنة بـ medicore-C-):
- `HostPaths`: المسارات أعلاه، و `app.mcb`/`php` من `Contents/Resources` داخل حزمة البرنامج.
- `PhpServer`: البحث عن PHP داخل الحزمة ثم Homebrew؛ OPcache المدمج في PHP المحمول؛ `include_path` بدون `.`
  (قرص الماك لا يفرّق بين الحروف الكبيرة والصغيرة مثل ويندوز)؛ وبدل Job Object في ويندوز: ملف `php.pid`
  يُوقَف به PHP يتيم لو أُنهي البرنامج قسرياً.
- `HostSettings.PreventSleep` (منع النوم).

> ⚠️ أي تعديل مستقبلي على منطق الترخيص أو التشفير في `medicore-C-` يجب نسخه هنا أيضاً
> (`Medicore.Licensing.Core` و `Medicore.Packager` متطابقان حرفياً).

### التطوير على الماك
```bash
brew install php                                            # للتطوير فقط (النسخة النهائية تحمل PHP خاصاً بها)
dotnet run --project Medicore.Packager -- pack ../medicore-C-/Medicore.Web app.mcb code.key   # app.mcb في جذر المستودع
dotnet run --project Medicore.Mac                           # أو F5 في VS Code
MEDICORE_PHP_DIR=../medicore-C-/Medicore.Web dotnet run --project Medicore.Mac.Tests
```

---

## ⚠️ حدود الاختبار (بصراحة)
بنيت هذه النسخة واختبرتها فعلياً في بيئة لينكس: كل الاختبارات الـ73 ناجحة، ومنها تشغيل حقيقي لنظام PHP
عبر روابط symlink كما في الماك (فك الحزمة، قاعدة بيانات جديدة، تسجيل دخول admin، حماية ملفات قاعدة البيانات،
إشعارات السداد)، وشغّلت البرنامج نفسه على شاشة افتراضية (شاشة التفعيل ونافذة السيرفر تعمل وتخدم النظام،
والنسخة الثانية تُسلّم للأولى، والإيقاف يحذف الكود المفكوك). كما بنيت نسخة osx-arm64 فعلياً وتأكدت أن
مكتباتها تعمل على **macOS 12 (Monterey) أو أحدث**.

لم يتسنَّ لي تشغيلها على جهاز ماك حقيقي: أجزاء الماك تحديداً (`ioreg`، `codesign`، `hdiutil`، أيقونة شريط
القوائم، LaunchAgent، `caffeinate`) مكتوبة بالأوامر والواجهات القياسية لـ macOS — شغّل `./build-mac.sh` على
جهازك وأخبرني بأي خطأ يظهر تحديداً.
