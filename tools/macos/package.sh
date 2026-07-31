#!/usr/bin/env bash
set -euo pipefail

RID="${1:-osx-arm64}"
case "$RID" in
  osx-arm64|osx-x64) ;;
  *) echo "RID must be osx-arm64 or osx-x64" >&2; exit 2 ;;
esac

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT="$REPO_ROOT/src/linux/Shell/TradingTerminal.App.Avalonia/TradingTerminal.App.Avalonia.csproj"
MACOS_DIR="$REPO_ROOT/src/linux/Shell/TradingTerminal.App.Avalonia/MacOS"
STAGE_ROOT="$REPO_ROOT/tmp/macos-package/$RID"
PUBLISH_DIR="$STAGE_ROOT/publish"
APP="$STAGE_ROOT/DaxAlgo Terminal.app"
APP_CONTENTS="$APP/Contents"
APP_MACOS="$APP_CONTENTS/MacOS"
APP_RESOURCES="$APP_CONTENTS/Resources"
ICONSET="$STAGE_ROOT/DaxAlgoTerminal.iconset"
MASTER_ICON="$STAGE_ROOT/AppIcon-1024.png"
ZIP="$STAGE_ROOT/DaxAlgo-Terminal-$RID.zip"
CONFIGURATION="${CONFIGURATION:-Release}"
IDENTITY="${CODESIGN_IDENTITY:--}"
DAXQ_MODE="${DAXQ_VM_MODE:-auto}"
IB_API_MODE="${IB_API_MODE:-required}"

case "$DAXQ_MODE" in
  auto|required|off) ;;
  *) echo "DAXQ_VM_MODE must be auto, required, or off" >&2; exit 2 ;;
esac
case "$IB_API_MODE" in
  auto|required|off) ;;
  *) echo "IB_API_MODE must be auto, required, or off" >&2; exit 2 ;;
esac

if [[ "$IDENTITY" == "-" ]]; then
  SIGN_ARGS=(--force --sign -)
else
  SIGN_ARGS=(--force --options runtime --timestamp --sign "$IDENTITY")
fi

case "$STAGE_ROOT" in
  "$REPO_ROOT"/tmp/macos-package/osx-arm64|"$REPO_ROOT"/tmp/macos-package/osx-x64) ;;
  *) echo "Refusing unsafe staging path: $STAGE_ROOT" >&2; exit 3 ;;
esac

rm -rf "$STAGE_ROOT"
mkdir -p "$PUBLISH_DIR" "$APP_MACOS" "$APP_RESOURCES" "$ICONSET"

DAXQ_LIBRARY=""
DAXQ_PUBLISH_ARGS=()
IB_PUBLISH_ARGS=()
if [[ "$IB_API_MODE" == "off" ]]; then
  IB_PUBLISH_ARGS+=(-p:DisableIbApi=true)
elif [[ -n "${TWS_API_CLIENT_DLL:-}" ]]; then
  [[ -f "$TWS_API_CLIENT_DLL" ]] || {
    echo "TWS_API_CLIENT_DLL does not name an existing official CSharpAPI.dll." >&2
    exit 6
  }
  IB_PUBLISH_ARGS+=(-p:TwsApiClientDll="$TWS_API_CLIENT_DLL")
fi

if [[ "$DAXQ_MODE" != "off" ]]; then
  DAXQ_READY=true
  for variable in DAXQ_VM_LICENSE_KEY_SHA256_HEX DAXQ_VM_LICENSE_ISSUER DAXQ_VM_LICENSE_AUDIENCE; do
    [[ -n "${!variable:-}" ]] || DAXQ_READY=false
  done
  [[ "$IDENTITY" != "-" ]] || DAXQ_READY=false

  if [[ "$DAXQ_READY" == true ]]; then
    DAXQ_NATIVE_DIR="$STAGE_ROOT/daxq-native"
    DAXQ_TEST_DIR="$STAGE_ROOT/daxq-native-test"
    DAXQ_VM_HARDENED_RELEASE=OFF DAXQ_VM_RUN_TESTS=ON \
      bash "$REPO_ROOT/tools/daxq-vm/build-macos.sh" "$RID" "$DAXQ_TEST_DIR"
    DAXQ_VM_HARDENED_RELEASE=ON DAXQ_VM_RUN_TESTS=OFF \
      bash "$REPO_ROOT/tools/daxq-vm/build-macos.sh" "$RID" "$DAXQ_NATIVE_DIR"
    DAXQ_LIBRARY="$DAXQ_NATIVE_DIR/libdaxq_vm.dylib"
    codesign "${SIGN_ARGS[@]}" "$DAXQ_LIBRARY"
    codesign --verify --strict --verbose=2 "$DAXQ_LIBRARY"

    DAXQ_TEAM="$(codesign --display --verbose=4 "$DAXQ_LIBRARY" 2>&1 \
      | sed -n 's/^TeamIdentifier=//p' | head -n 1)"
    [[ -n "$DAXQ_TEAM" && "$DAXQ_TEAM" != "not set" ]] || {
      echo "The signed DAXQ VM has no Apple Developer Team identifier." >&2
      exit 5
    }
    DAXQ_HASH="$(shasum -a 256 "$DAXQ_LIBRARY" | awk '{print $1}')"
    DAXQ_PUBLISH_ARGS+=(
      -p:DaxqNativeVmSha256="$DAXQ_HASH"
      -p:DaxqNativeVmMacTeamIdentifier="$DAXQ_TEAM"
    )
  elif [[ "$DAXQ_MODE" == "required" ]]; then
    echo "A protected DAXQ release requires a Developer ID identity and all DAXQ_VM_LICENSE_* values." >&2
    exit 5
  else
    echo "Protected DAXQ runtime omitted: release signing identity or DAXQ license pins are unavailable." >&2
  fi
fi

dotnet publish "$PROJECT" \
  --configuration "$CONFIGURATION" \
  --runtime "$RID" \
  --self-contained true \
  -p:PublishSingleFile=false \
  "${DAXQ_PUBLISH_ARGS[@]}" \
  "${IB_PUBLISH_ARGS[@]}" \
  --output "$PUBLISH_DIR"

if [[ ! -f "$PUBLISH_DIR/CSharpAPI.dll" && ! -f "$PUBLISH_DIR/IBApi.dll" ]]; then
  if [[ "$IB_API_MODE" == "required" ]]; then
    echo "Interactive Brokers support is missing. Install/build the official TWS C# API or set TWS_API_CLIENT_DLL." >&2
    exit 6
  elif [[ "$IB_API_MODE" == "auto" ]]; then
    echo "Interactive Brokers support omitted because the official TWS C# API was not found." >&2
  fi
fi

if [[ -n "$DAXQ_LIBRARY" ]]; then
  cp "$DAXQ_LIBRARY" "$PUBLISH_DIR/libdaxq_vm.dylib"
fi

cp -R "$PUBLISH_DIR"/. "$APP_MACOS"/
mv "$APP_MACOS/TradingTerminal.App.Avalonia" "$APP_MACOS/DaxAlgoTerminal"
chmod +x "$APP_MACOS/DaxAlgoTerminal"
cp "$MACOS_DIR/Info.plist" "$APP_CONTENTS/Info.plist"

# Build an Apple icon set from the copied Windows product logo without distorting its aspect ratio.
sips --resampleHeightWidthMax 900 "$MACOS_DIR/AppIcon.png" --out "$MASTER_ICON" >/dev/null
sips --padToHeightWidth 1024 1024 --padColor 00000000 "$MASTER_ICON" --out "$MASTER_ICON" >/dev/null
for spec in \
  "16 icon_16x16.png" "32 icon_16x16@2x.png" \
  "32 icon_32x32.png" "64 icon_32x32@2x.png" \
  "128 icon_128x128.png" "256 icon_128x128@2x.png" \
  "256 icon_256x256.png" "512 icon_256x256@2x.png" \
  "512 icon_512x512.png" "1024 icon_512x512@2x.png"; do
  size="${spec%% *}"
  name="${spec#* }"
  sips --resampleHeightWidth "$size" "$size" "$MASTER_ICON" --out "$ICONSET/$name" >/dev/null
done
iconutil --convert icns "$ICONSET" --output "$APP_RESOURCES/DaxAlgoTerminal.icns"

# Sign nested Mach-O files first, then seal the application bundle.
while IFS= read -r -d '' candidate; do
  if file "$candidate" | grep -q "Mach-O"; then
    if [[ -n "$DAXQ_LIBRARY" && "$candidate" == "$APP_MACOS/libdaxq_vm.dylib" ]]; then
      continue
    fi
    codesign "${SIGN_ARGS[@]}" "$candidate"
  fi
done < <(find "$APP_MACOS" -type f -print0)

codesign "${SIGN_ARGS[@]}" \
  --entitlements "$MACOS_DIR/DaxAlgoTerminal.entitlements" \
  "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"

if [[ -n "${NOTARY_KEYCHAIN_PROFILE:-}" ]]; then
  if [[ "$IDENTITY" == "-" ]]; then
    echo "Notarization requires a Developer ID Application signing identity." >&2
    exit 4
  fi
  xcrun notarytool submit "$ZIP" --keychain-profile "$NOTARY_KEYCHAIN_PROFILE" --wait
  xcrun stapler staple "$APP"
  ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"
  spctl --assess --type execute --verbose=2 "$APP"
fi

echo "$APP"
echo "$ZIP"
