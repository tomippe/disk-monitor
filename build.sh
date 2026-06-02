#!/bin/bash
set -e

# ===== Disk Monitor ビルドスクリプト =====

APP_EXE="ProcessMonitor"
APP_BUNDLE_FILE="Disk Monitor.app"
BUNDLE_ID="jp.tomippe.diskmonitor"
BUILD_DIR="build"
ZIP_NAME="disk-monitor_mac.zip"
DIST_DIR="../apps.tomippe.jp/disk-monitor"
MACOSX_DEPLOYMENT_TARGET="11.0"

DIRECT_BUNDLE="$BUILD_DIR/$APP_BUNDLE_FILE"

SIGNING_IDENTITY="Developer ID Application: TOMIHIDE OTA (4U63Y3X98K)"
KEYCHAIN_PROFILE="TOMIHIDE OTA"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

source "$SCRIPT_DIR/../build-common/version.sh"
source "$SCRIPT_DIR/../build-common/ftp-upload.sh"
source "$SCRIPT_DIR/../build-common/git-commit.sh"
source "$SCRIPT_DIR/../build-common/mac-sparkle-lib.sh"

APP_ONLY=false
COMMIT_MSG=""
NO_VERUP=false
while [ $# -gt 0 ]; do
    case "$1" in
        -app) APP_ONLY=true ;;
        -cm) shift; COMMIT_MSG="$1" ;;
        -noverup) NO_VERUP=true ;;
    esac
    shift || true
done

VERSION=$(version_read)

echo "🔨 Disk Monitor v$VERSION をビルド中..."

rm -rf "$BUILD_DIR"
mkdir -p "$BUILD_DIR"

# ---------- アイコン生成 (角丸付き) ----------
if [ -f "icon.avif" ]; then
    echo "🎨 AppIcon.icns を生成中..."
    ICON_TMP="$BUILD_DIR/icon_tmp"
    ICONSET="$BUILD_DIR/AppIcon.iconset"
    rm -rf "$ICON_TMP" "$ICONSET"
    mkdir -p "$ICON_TMP" "$ICONSET"

    # avif → png
    ffmpeg -y -i icon.avif "$ICON_TMP/icon_src.png" 2>/dev/null

    # 角丸マスクを適用（squircle 約22.37%）
    swift - "$ICON_TMP/icon_src.png" "$ICON_TMP/icon_rounded.png" 0.2237 << 'SWIFT_EOF'
import Cocoa
let args = CommandLine.arguments
let (inp, out, frac) = (args[1], args[2], Double(args[3])!)
let img = NSImage(contentsOfFile: inp)!
let sz = img.size; let r = sz.width * CGFloat(frac)
let res = NSImage(size: sz); res.lockFocus()
NSBezierPath(roundedRect: NSRect(origin:.zero,size:sz), xRadius:r, yRadius:r).addClip()
img.draw(in: NSRect(origin:.zero,size:sz)); res.unlockFocus()
let bmp = NSBitmapImageRep(data: res.tiffRepresentation!)!
try! bmp.representation(using:.png,properties:[:])!.write(to:URL(fileURLWithPath:out))
SWIFT_EOF

    # 各サイズを生成
    for sz in 16 32 128 256 512; do
        sips -z $sz $sz "$ICON_TMP/icon_rounded.png" --out "$ICONSET/icon_${sz}x${sz}.png" > /dev/null
        sz2=$((sz * 2))
        sips -z $sz2 $sz2 "$ICON_TMP/icon_rounded.png" --out "$ICONSET/icon_${sz}x${sz}@2x.png" > /dev/null
    done

    iconutil -c icns "$ICONSET" -o AppIcon.icns
    rm -rf "$ICON_TMP" "$ICONSET"
fi
# ---------- アイコン生成ここまで ----------

MOVE_SWIFT="$SCRIPT_DIR/../build-common/MoveToApplicationsFolder.swift"
TOMIPPE_ABOUT="$SCRIPT_DIR/../build-common/TomippeAppAbout.swift"
SWIFT_SOURCES="ProcessMonitor.swift $MOVE_SWIFT $TOMIPPE_ABOUT"
for src in $SWIFT_SOURCES; do
    if [ ! -f "$src" ]; then
        echo "❌ $src がありません。"
        exit 1
    fi
done
SWIFT_FLAGS="-parse-as-library -framework Cocoa -framework CoreServices -framework ServiceManagement -F Sparkle.framework/.. -framework Sparkle -Xlinker -rpath -Xlinker @executable_path/../Frameworks"
UNIVERSAL_BIN="$BUILD_DIR/${APP_EXE}_universal"

echo "📦 コンパイル中 (arm64)..."
swiftc -o "$BUILD_DIR/${APP_EXE}_arm64" $SWIFT_FLAGS \
    -target "arm64-apple-macosx${MACOSX_DEPLOYMENT_TARGET}" $SWIFT_SOURCES

echo "📦 コンパイル中 (x86_64)..."
swiftc -o "$BUILD_DIR/${APP_EXE}_x86_64" $SWIFT_FLAGS \
    -target "x86_64-apple-macosx${MACOSX_DEPLOYMENT_TARGET}" $SWIFT_SOURCES

echo "📦 Universal Binary を作成中..."
lipo -create \
    "$BUILD_DIR/${APP_EXE}_arm64" \
    "$BUILD_DIR/${APP_EXE}_x86_64" \
    -output "$UNIVERSAL_BIN"

rm "$BUILD_DIR/${APP_EXE}_arm64" "$BUILD_DIR/${APP_EXE}_x86_64"

create_app_bundle() {
    local BUNDLE_PATH="$1"
    local CONTENTS="$BUNDLE_PATH/Contents"
    local MACOS="$CONTENTS/MacOS"
    local RESOURCES="$CONTENTS/Resources"

    mkdir -p "$MACOS" "$RESOURCES"
    cp "$UNIVERSAL_BIN" "$MACOS/$APP_EXE"

    local FRAMEWORKS="$CONTENTS/Frameworks"
    mkdir -p "$FRAMEWORKS"
    local SPARKLE_REAL
    SPARKLE_REAL="$(readlink -f Sparkle.framework)"
    mac_sparkle_embed "$SPARKLE_REAL" "$FRAMEWORKS/Sparkle.framework"

    local ICON_BLOCK=""
    if [ -f "AppIcon.icns" ]; then
        cp "AppIcon.icns" "$RESOURCES/"
        ICON_BLOCK="
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>"
    fi
    if [ -f "$SCRIPT_DIR/../auto-mount/Resources/AppsLogo.png" ]; then
        cp "$SCRIPT_DIR/../auto-mount/Resources/AppsLogo.png" "$RESOURCES/"
    elif [ -f "Resources/AppsLogo.png" ]; then
        cp "Resources/AppsLogo.png" "$RESOURCES/"
    fi

    cat > "$CONTENTS/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleLocalizations</key>
    <array>
        <string>en</string>
        <string>ja</string>
        <string>zh-Hans</string>
    </array>
    <key>CFBundleExecutable</key>
    <string>$APP_EXE</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleName</key>
    <string>Disk Monitor</string>
    <key>CFBundleDisplayName</key>
    <string>Disk Monitor</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>LSMinimumSystemVersion</key>
    <string>$MACOSX_DEPLOYMENT_TARGET</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>ITSAppUsesNonExemptEncryption</key>
    <false/>
    <key>NSAppleEventsUsageDescription</key>
    <string>Disk Monitor uses Finder to calculate Trash size and empty the Trash.</string>
    <key>NSHumanReadableCopyright</key>
    <string>Copyright © 2026 tomippe. All rights reserved.</string>
    <key>SUFeedURL</key>
    <string>https://apps.tomippe.jp/disk-monitor/appcast.xml</string>
    <key>SUPublicEDKey</key>
    <string>7jgkjdF0DqNYkF0fUgdoi926jh1HAckFWMdZQreq7HI=</string>${ICON_BLOCK}
</dict>
</plist>
PLIST

    echo -n "APPL????" > "$CONTENTS/PkgInfo"

    for LPROJ in Resources/*.lproj; do
        if [ -d "$LPROJ" ]; then
            local LANG
            LANG=$(basename "$LPROJ")
            mkdir -p "$RESOURCES/$LANG"
            cp "$LPROJ"/* "$RESOURCES/$LANG/"
        fi
    done
}

echo ""
echo "📁 アプリバンドルを作成中 ($APP_BUNDLE_FILE)..."
create_app_bundle "$DIRECT_BUNDLE"
rm -f "$UNIVERSAL_BIN"

if $APP_ONLY; then
    echo ""
    echo "🔏 アドホック署名中..."
    codesign --force --deep --sign - --identifier "$BUNDLE_ID" "$DIRECT_BUNDLE"
    echo ""
    echo "✅ Disk Monitor v$VERSION — アプリバンドル作成完了! (-app モード)"
    echo "  open \"$DIRECT_BUNDLE\""
    exit 0
fi

echo ""
echo "========== 直接配布版（署名・ノータライズ）=========="

echo ""
echo "🔏 コード署名中..."
mac_sparkle_sign_app "$DIRECT_BUNDLE" "$SIGNING_IDENTITY" "$BUNDLE_ID"

echo ""
echo "📤 ノータライズ送信中..."
mac_create_dist_zip "$DIRECT_BUNDLE" "$BUILD_DIR/$ZIP_NAME"

xcrun notarytool submit "$BUILD_DIR/$ZIP_NAME" \
    --keychain-profile "$KEYCHAIN_PROFILE" \
    --wait

echo ""
echo "📎 ステープル中..."
xcrun stapler staple "$DIRECT_BUNDLE" || true

mac_create_dist_zip "$DIRECT_BUNDLE" "$BUILD_DIR/$ZIP_NAME"

echo ""
echo "🔍 配布前検査（ZIP 内容 + デスクトップ基準 + 第2検査機）..."
chmod +x "$SCRIPT_DIR/../build-common/verify-mac-distribution-post.sh"
"$SCRIPT_DIR/../build-common/verify-mac-distribution-post.sh" "$DIRECT_BUNDLE" "$BUILD_DIR/$ZIP_NAME"

echo ""
echo "📂 配布用ディレクトリにコピーしています..."
if [ -d "$DIST_DIR" ]; then
    rm -f "$DIST_DIR/$ZIP_NAME"
else
    mkdir -p "$DIST_DIR"
fi
cp "$BUILD_DIR/$ZIP_NAME" "$DIST_DIR/$ZIP_NAME"

python3 -c "
import json, os
path = '$DIST_DIR/manifest.json'
data = {}
if os.path.exists(path):
    with open(path) as f: data = json.load(f)
data['name'] = 'DiskMonitor'
data['version'] = '$VERSION'
data['mac_version'] = '$VERSION'
with open(path, 'w') as f: json.dump(data, f)
"

echo ""
echo "📋 appcast.xml を生成中..."
SPARKLE_ACCOUNT="ed25519"
"$SCRIPT_DIR/Sparkle_bin/generate_appcast" \
    --account "$SPARKLE_ACCOUNT" \
    --download-url-prefix "https://apps.tomippe.jp/disk-monitor/" \
    --link "https://apps.tomippe.jp/disk-monitor/" \
    "$DIST_DIR"
ftp_upload_dir "$DIST_DIR" "disk-monitor"

if ! $NO_VERUP; then
    echo ""
    echo "📝 次回用バージョンを更新しています..."
    version_save_next "$VERSION"
fi

git_commit_build "$VERSION" "$COMMIT_MSG"

echo ""
echo "✅ Disk Monitor v$VERSION — ビルド・配布完了!"
echo "  $DIST_DIR/$ZIP_NAME"
echo ""
