#!/usr/bin/env bash
# Build on macOS with .NET 8 SDK, Rust 1.91 and Xcode command line tools.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
if [[ "$(uname -s)" != Darwin ]]; then
  echo "This script requires macOS (Apple SDK, codesign and hdiutil)." >&2
  exit 1
fi
for tool in dotnet cargo rustup xcrun codesign hdiutil ditto; do
  command -v "$tool" >/dev/null || { echo "Missing tool: $tool" >&2; exit 1; }
done
export MACOSX_DEPLOYMENT_TARGET=14.0
export SDKROOT="$(xcrun --sdk macosx --show-sdk-path)"
# A fresh staging folder avoids shipping stale files, with no recursive deletion.
mkdir -p "$ROOT/artifacts"
BUILD="$(mktemp -d "$ROOT/artifacts/macos-intel.XXXXXX")"
APP="$BUILD/stage/AgentLink.app"
BIN="$APP/Contents/MacOS"
mkdir -p "$BIN" "$APP/Contents/Resources"

rustup target add x86_64-apple-darwin
cargo build --locked --release --manifest-path native/iroh-transport/Cargo.toml --target x86_64-apple-darwin
dotnet publish src/RayLink.App/RayLink.App.csproj -c Release -r osx-x64 \
  --self-contained true -p:UseAppHost=true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=None -p:DebugSymbols=false -o "$BIN"
cp native/iroh-transport/target/x86_64-apple-darwin/release/raylink-iroh-transport "$BIN/AgentLink.Transport"
chmod +x "$BIN/AgentLink" "$BIN/AgentLink.Transport"
# Both binaries must contain an Intel slice, not Windows or ARM-only binaries.
lipo "$BIN/AgentLink" -verify_arch x86_64
lipo "$BIN/AgentLink.Transport" -verify_arch x86_64
cp THIRD_PARTY_NOTICES.md "$APP/Contents/Resources/"
cp -R third-party/iroh "$APP/Contents/Resources/iroh"
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>com.raylink.agent</string>
<key>CFBundleName</key><string>AgentLink</string>
<key>CFBundleExecutable</key><string>AgentLink</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
<key>CFBundleVersion</key><string>0.2.0</string>
<key>CFBundleShortVersionString</key><string>0.2.0</string>
<key>LSMinimumSystemVersion</key><string>14.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSLocalNetworkUsageDescription</key><string>AgentLink uses the network to connect to peers and exchange messages.</string>
</dict></plist>
PLIST
plutil -lint "$APP/Contents/Info.plist"
# Internal test package: ad-hoc signature only, NOT Developer ID/notarized.
# No hardened runtime here; .NET JIT requires entitlements when that is enabled.
codesign --force --sign - "$BIN/AgentLink.Transport"
codesign --force --sign - "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"
cat > "$BUILD/stage/READ-ME.txt" <<'NOTICE'
AgentLink Intel Mac test build / Intel Mac 测试版
Target: macOS 14 or later, Intel x64. Includes .NET and Iroh.
Drag AgentLink.app into Applications, then open it and click 启动服务.
This package is ad-hoc signed, NOT Apple Developer ID signed or notarized.
Only open it if you trust its source. macOS may require approval in Privacy & Security.
Do not disable Gatekeeper globally. No personal settings or identity keys are bundled.
Cross-network operation must still be tested on two real devices.
NOTICE
ln -s /Applications "$BUILD/stage/Applications"
hdiutil create -volname "AgentLink Intel" -srcfolder "$BUILD/stage" -format UDZO "$BUILD/AgentLink-macOS-Intel.dmg"
hdiutil verify "$BUILD/AgentLink-macOS-Intel.dmg"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$BUILD/AgentLink-macOS-Intel.app.zip"
(cd "$BUILD" && shasum -a 256 AgentLink-macOS-Intel.dmg AgentLink-macOS-Intel.app.zip > SHA256SUMS.txt)
echo "Packages: $BUILD"
