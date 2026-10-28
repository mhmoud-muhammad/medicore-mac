#!/usr/bin/env bash
# ============================================================================
#  تجهيز Medicore Server للماك (على جهاز ماك عندك — يحتاج code.key و .NET SDK 10)
#
#    ./build-mac.sh                       # Apple Silicon + Intel، الإصدار 1.0.0
#    ./build-mac.sh --version 1.2.0 --arch arm64
#    ./build-mac.sh --bundle ~/Downloads/app.mcb          # app.mcb جاهز (من بناء ويندوز مثلاً)
#    ./build-mac.sh --sign "Developer ID Application: Name (TEAMID)" --notarize medicore-notary
#
#  نظام PHP يؤخذ من مستودع medicore-C- بجانب هذا المستودع (../medicore-C-/Medicore.Web) ويُشفَّر
#  بـ code.key — نفس الملف app.mcb يعمل على ويندوز والماك.
#
#  الناتج في dist/:
#    MedicoreServer-<الإصدار>-AppleSilicon.dmg   لأجهزة M1/M2/M3/M4...
#    MedicoreServer-<الإصدار>-Intel.dmg          لأجهزة Intel
#  كل ملف DMG يحتوي "Medicore Server.app" (البرنامج + app.mcb المشفّر + PHP محمول) — يُسحب إلى التطبيقات.
# ============================================================================
set -euo pipefail
cd "$(dirname "$0")"
ROOT="$(pwd)"

VERSION="1.0.0"
ARCHS="arm64 x64"
WEB_DIR="../medicore-C-/Medicore.Web"
BUNDLE=""
CODE_KEY="${MEDICORE_CODE_KEY:-}"
PHP_VERSION="8.3 8.2 8.1"
PHP_ARM64=""
PHP_X64=""
SIGN_ID=""
NOTARY_PROFILE=""
MAKE_DMG=1
NO_SYSTEM=0
# أقدم macOS مدعوم: .NET 8 يعمل على 10.15 (Catalina) فأحدث على Intel — .NET 9/10 يحتاجان macOS 12.
# أجهزة Apple Silicon كلها بدأت بـ macOS 11.
MIN_MACOS_X64="10.15"
MIN_MACOS_ARM64="11.0"

usage() {
    sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
    cat <<'EOF'
الخيارات:
  --version X.Y.Z        رقم الإصدار (الافتراضي 1.0.0)
  --arch arm64|x64|all   المعمارية (الافتراضي: الاثنتان)
  --web DIR              مجلد نظام PHP (الافتراضي ../medicore-C-/Medicore.Web)
  --bundle FILE          app.mcb جاهز بدل تشفير مجلد PHP
  --code-key FILE        مفتاح الكود (الافتراضي: MEDICORE_CODE_KEY أو code.key هنا أو في ../medicore-C-)
  --php-version "8.3 8.2" إصدارات PHP المحمول المقبولة بالترتيب (الافتراضي 8.3 8.2 8.1)
  --php-arm64 FILE       ملف php جاهز (بدل التنزيل) لـ Apple Silicon
  --php-x64 FILE         ملف php جاهز (بدل التنزيل) لـ Intel
  --sign IDENTITY        توقيع بشهادة Developer ID (بدونه: توقيع محلي ad-hoc)
  --notarize PROFILE     توثيق Apple (xcrun notarytool store-credentials PROFILE) — يحتاج --sign
  --no-dmg               البرنامج (.app) فقط بدون DMG
  --without-system       بدون app.mcb داخل البرنامج (بلا code.key): يختار العميل ملف app.mcb عند أول تشغيل
EOF
    exit "${1:-0}"
}

die() { echo "⛔ $*" >&2; exit 1; }
step() { printf '\n\033[36m▶ %s\033[0m\n' "$*"; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) VERSION="$2"; shift 2 ;;
        --arch) ARCHS="$2"; [[ "$ARCHS" == "all" ]] && ARCHS="arm64 x64"; shift 2 ;;
        --web) WEB_DIR="$2"; shift 2 ;;
        --bundle) BUNDLE="$2"; shift 2 ;;
        --code-key) CODE_KEY="$2"; shift 2 ;;
        --php-version) PHP_VERSION="$2"; shift 2 ;;
        --php-arm64) PHP_ARM64="$2"; shift 2 ;;
        --php-x64) PHP_X64="$2"; shift 2 ;;
        --sign) SIGN_ID="$2"; shift 2 ;;
        --notarize) NOTARY_PROFILE="$2"; shift 2 ;;
        --no-dmg) MAKE_DMG=0; shift ;;
        --without-system) NO_SYSTEM=1; shift ;;
        -h|--help) usage 0 ;;
        *) echo "خيار غير معروف: $1"; usage 2 ;;
    esac
done

[[ "$(uname)" == "Darwin" ]] || die "هذا السكربت يعمل على الماك فقط (codesign و hdiutil)."
command -v dotnet >/dev/null || die "ثبّت .NET SDK 10 أولاً: https://dotnet.microsoft.com/download/dotnet/10.0 (أو: brew install --cask dotnet-sdk)"
[[ -n "$NOTARY_PROFILE" && -z "$SIGN_ID" ]] && die "--notarize يحتاج --sign بشهادة Developer ID."
for a in $ARCHS; do [[ "$a" == "arm64" || "$a" == "x64" ]] || die "معمارية غير معروفة: $a"; done

DIST="$ROOT/dist"
WORK="$ROOT/.build"
CACHE="$ROOT/.cache"
mkdir -p "$DIST" "$WORK" "$CACHE"

# ── 1) كود النظام المشفّر app.mcb ──────────────────────────────────────────
step "1) كود النظام المشفّر (app.mcb)"
MCB="$WORK/app.mcb"
rm -f "$MCB"
if [[ "$NO_SYSTEM" == "1" ]]; then
    echo "بدون app.mcb — البرنامج يطلب ملف النظام عند أول تشغيل."
elif [[ -n "$BUNDLE" ]]; then
    [[ -f "$BUNDLE" ]] || die "لا يوجد $BUNDLE"
    cp "$BUNDLE" "$MCB"
    echo "استخدام الحزمة الجاهزة: $BUNDLE"
else
    if [[ -z "$CODE_KEY" ]]; then
        for k in "$ROOT/code.key" "$ROOT/../medicore-C-/code.key"; do
            [[ -f "$k" ]] && { CODE_KEY="$k"; break; }
        done
    fi
    [[ -n "$CODE_KEY" && -f "$CODE_KEY" ]] || die "لا يوجد code.key — ضعه في جذر هذا المستودع أو مرّر --code-key (أو --bundle app.mcb جاهز)."
    [[ -f "$WEB_DIR/index.php" ]] || die "لا يوجد نظام PHP في $WEB_DIR — انسخ مستودع medicore-C- بجانب هذا المستودع أو مرّر --web."
    dotnet run --project "$ROOT/Medicore.Packager" -c Release -- pack "$WEB_DIR" "$MCB" "$CODE_KEY"
fi

# أقدم macOS يحتاجه ملف Mach-O لمعمارية معيّنة (minos أو version). فارغ إن تعذّر الفحص.
macho_minos() {
    local f="$1" arch="$2"
    command -v otool >/dev/null || return 0
    otool -arch "$arch" -l "$f" 2>/dev/null \
        | awk '/LC_BUILD_VERSION|LC_VERSION_MIN_MACOSX/{f=1} f&&($1=="minos"||$1=="version"){print $2; exit}'
}

# هل الإصدار $1 أقدم من أو يساوي $2؟
version_le() { [[ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -1)" == "$2" ]]; }

# ── PHP المحمول للماك (static-php-cli: ملف واحد، كل الإضافات مدمجة، لا يحتاج Homebrew) ──
# يختار أحدث PHP (8.3 ثم 8.2 ثم 8.1) يعمل على أقدم macOS مدعوم لهذه المعمارية: إصدارات PHP المحمول
# الحديثة مبنية لـ macOS 12، والأجهزة القديمة (مثل MacBook Air 2013 على Big Sur 11) تحتاج بناءً أقدم.
fetch_php() {
    local arch="$1" dest="$2" min="$4" listing file candidates tried=0
    local base="https://dl.static-php.dev/static-php-cli/common"
    listing="$(curl -fsSL "$base/")" || die "تعذّر الاتصال بـ dl.static-php.dev — نزّل php يدوياً ومرّره بـ --php-$3"
    candidates="$(for v in $PHP_VERSION; do
        grep -oE "php-${v//./\\.}\.[0-9]+-cli-macos-${arch}\.tar\.gz" <<<"$listing" | sort -uV -r
    done | awk '!seen[$0]++')"
    [[ -n "$candidates" ]] || die "لم يُعثر على PHP ${PHP_VERSION} للماك (${arch}) في $base"
    for file in $candidates; do
        (( tried++ < 25 )) || break
        if [[ ! -f "$CACHE/$file" ]]; then
            curl -fsSL -o "$CACHE/$file.part" "$base/$file" || { echo "  تعذّر تنزيل $file"; continue; }
            mv "$CACHE/$file.part" "$CACHE/$file"
        fi
        local tmp; tmp="$(mktemp -d)"
        tar -xzf "$CACHE/$file" -C "$tmp" 2>/dev/null
        if [[ ! -f "$tmp/php" ]]; then rm -rf "$tmp"; continue; fi
        local need; need="$(macho_minos "$tmp/php" "$([[ $arch == aarch64 ]] && echo arm64 || echo x86_64)")"
        if [[ -z "$need" ]] || version_le "$need" "$min"; then
            cp "$tmp/php" "$dest"
            rm -rf "$tmp"
            echo "PHP: $file (يحتاج macOS ${need:-؟})"
            return 0
        fi
        echo "  $file يحتاج macOS $need — أحدث من $min، تجربة إصدار أقدم..."
        rm -f "$CACHE/$file"
        rm -rf "$tmp"
    done
    die "لا يوجد PHP محمول (${PHP_VERSION}) يعمل على macOS $min لـ ${arch} — مرّر ملف php مبنياً لـ macOS $min بـ --php-$3"
}

sign_file() {
    local f="$1"
    if [[ -n "$SIGN_ID" ]]; then
        codesign --force --timestamp --options runtime --entitlements "$ROOT/macos/entitlements.plist" --sign "$SIGN_ID" "$f"
    else
        codesign --force --sign - "$f"
    fi
}

for ARCH in $ARCHS; do
    if [[ "$ARCH" == "arm64" ]]; then LABEL="AppleSilicon"; PHP_ARCH="aarch64"; MACHO_ARCH="arm64"; PHP_LOCAL="$PHP_ARM64"; MIN_MACOS="$MIN_MACOS_ARM64"
    else LABEL="Intel"; PHP_ARCH="x86_64"; MACHO_ARCH="x86_64"; PHP_LOCAL="$PHP_X64"; MIN_MACOS="$MIN_MACOS_X64"; fi

    APP="$WORK/$ARCH/Medicore Server.app"
    step "2) بناء البرنامج — $LABEL ($ARCH)"
    rm -rf "$WORK/$ARCH"
    mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources/php"
    dotnet publish "$ROOT/Medicore.Mac" -c Release -r "osx-$ARCH" --self-contained true \
        -p:PublishReadyToRun=true -p:DebugType=none -p:Version="$VERSION" \
        -o "$APP/Contents/MacOS"

    step "3) PHP المحمول — $LABEL"
    if [[ -n "$PHP_LOCAL" ]]; then
        [[ -f "$PHP_LOCAL" ]] || die "لا يوجد $PHP_LOCAL"
        cp "$PHP_LOCAL" "$APP/Contents/Resources/php/php"
    else
        fetch_php "$PHP_ARCH" "$APP/Contents/Resources/php/php" "$ARCH" "$MIN_MACOS"
    fi
    chmod +x "$APP/Contents/Resources/php/php"
    # فحص الإضافات المطلوبة (إن أمكن تشغيل هذه المعمارية على جهازك)
    if mods="$("$APP/Contents/Resources/php/php" -m 2>/dev/null)"; then
        for m in pdo_sqlite sqlite3 mbstring openssl curl gd zip fileinfo; do
            grep -qix "$m" <<<"$mods" || die "PHP المحمول لا يحتوي الإضافة $m"
        done
        echo "✅ إضافات PHP المطلوبة موجودة."
    else
        echo "⚠️ تعذّر تشغيل PHP لهذه المعمارية هنا للفحص (Intel على Apple Silicon بدون Rosetta؟) — تخطي الفحص."
    fi

    step "4) تجميع Medicore Server.app — $LABEL"
    [[ -f "$MCB" ]] && cp "$MCB" "$APP/Contents/Resources/app.mcb"
    cp "$ROOT/macos/medicore.icns" "$APP/Contents/Resources/medicore.icns"
    cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>Medicore Server</string>
    <key>CFBundleDisplayName</key>
    <string>Medicore Server</string>
    <key>CFBundleIdentifier</key>
    <string>com.medicore.server</string>
    <key>CFBundleExecutable</key>
    <string>MedicoreServer</string>
    <key>CFBundleIconFile</key>
    <string>medicore.icns</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>CFBundleDevelopmentRegion</key>
    <string>ar</string>
    <key>LSMinimumSystemVersion</key>
    <string>${MIN_MACOS}</string>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.business</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <!-- السيرفر يعمل في الخلفية: بدون App Nap (macOS يبطئ البرامج المخفية لتوفير الطاقة) -->
    <key>NSAppSleepDisabled</key>
    <true/>
    <key>NSSupportsAutomaticTermination</key>
    <false/>
    <key>NSSupportsSuddenTermination</key>
    <false/>
</dict>
</plist>
EOF

    step "4ب) فحص أقدم macOS مدعوم ($MIN_MACOS) — $LABEL"
    if command -v otool >/dev/null; then
        too_new=""
        while IFS= read -r -d '' f; do
            file -b "$f" | grep -q "Mach-O" || continue
            v="$(macho_minos "$f" "$MACHO_ARCH")"
            [[ -z "$v" ]] || version_le "$v" "$MIN_MACOS" || too_new+="  ${f#"$APP/"} يحتاج macOS $v"$'\n'
        done < <(find "$APP/Contents" -type f -print0)
        [[ -z "$too_new" ]] || die "ملفات لا تعمل على macOS $MIN_MACOS:"$'\n'"$too_new"
        echo "✅ كل الملفات تعمل على macOS $MIN_MACOS أو أحدث."
    else
        echo "⚠️ otool غير موجود (xcode-select --install) — تخطي الفحص."
    fi

    step "5) التوقيع — $LABEL"
    xattr -cr "$APP"
    # كل ملف في Contents/MacOS (macOS يعامل ملفات .dll هناك ككود يجب توقيعه) و PHP، ثم الحزمة كاملة.
    # الملف التنفيذي الرئيسي يُوقَّع آخراً مع الحزمة: توقيعه يتحقق من توقيع كل ما بجانبه.
    while IFS= read -r -d '' f; do
        sign_file "$f"
    done < <(find "$APP/Contents/MacOS" "$APP/Contents/Resources/php" -type f ! -path "$APP/Contents/MacOS/MedicoreServer" -print0)
    sign_file "$APP"
    codesign --verify --deep --strict "$APP"
    echo "✅ التوقيع سليم ($( [[ -n "$SIGN_ID" ]] && echo "$SIGN_ID" || echo "ad-hoc محلي"))"

    if [[ "$MAKE_DMG" == "1" ]]; then
        step "6) ملف التثبيت DMG — $LABEL"
        DMG="$DIST/MedicoreServer-$VERSION-$LABEL.dmg"
        STAGE="$WORK/$ARCH/dmg"
        rm -rf "$STAGE" "$DMG"
        mkdir -p "$STAGE"
        cp -R "$APP" "$STAGE/"
        ln -s /Applications "$STAGE/Applications"
        hdiutil create -volname "Medicore Server" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
        [[ -n "$SIGN_ID" ]] && codesign --force --timestamp --sign "$SIGN_ID" "$DMG"
        if [[ -n "$NOTARY_PROFILE" ]]; then
            echo "توثيق Apple (قد يستغرق دقائق)..."
            xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
            xcrun stapler staple "$DMG"
        fi
        echo "✅ $DMG"
    else
        rm -rf "$DIST/Medicore Server ($LABEL).app"
        cp -R "$APP" "$DIST/Medicore Server ($LABEL).app"
        echo "✅ $DIST/Medicore Server ($LABEL).app"
    fi
done

printf '\n\033[32m✅ جاهز: %s\033[0m\n' "$DIST"
ls -1 "$DIST"
