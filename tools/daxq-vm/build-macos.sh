#!/usr/bin/env bash
set -euo pipefail

RID="${1:-osx-arm64}"
case "$RID" in
  osx-arm64) ARCH="arm64" ;;
  osx-x64) ARCH="x86_64" ;;
  *) echo "RID must be osx-arm64 or osx-x64" >&2; exit 2 ;;
esac

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
HARDENED="${DAXQ_VM_HARDENED_RELEASE:-OFF}"
case "$HARDENED" in
  ON|OFF) ;;
  *) echo "DAXQ_VM_HARDENED_RELEASE must be ON or OFF" >&2; exit 2 ;;
esac

if [[ "$HARDENED" == "ON" ]]; then
  : "${DAXQ_VM_LICENSE_KEY_SHA256_HEX:?required for a hardened DAXQ VM}"
  : "${DAXQ_VM_LICENSE_ISSUER:?required for a hardened DAXQ VM}"
  : "${DAXQ_VM_LICENSE_AUDIENCE:?required for a hardened DAXQ VM}"
fi

RUN_TESTS="${DAXQ_VM_RUN_TESTS:-}"
if [[ -z "$RUN_TESTS" ]]; then
  [[ "$HARDENED" == "ON" ]] && RUN_TESTS="OFF" || RUN_TESTS="ON"
fi
case "$RUN_TESTS" in
  ON|OFF) ;;
  *) echo "DAXQ_VM_RUN_TESTS must be ON or OFF" >&2; exit 2 ;;
esac

MODE="$(printf '%s' "$HARDENED" | tr '[:upper:]' '[:lower:]')"
BUILD_DIR="$REPO_ROOT/tmp/daxq-vm/$RID/$MODE"
OUTPUT_DIR="${2:-$REPO_ROOT/tmp/daxq-vm-output/$RID}"
case "$BUILD_DIR" in
  "$REPO_ROOT"/tmp/daxq-vm/osx-arm64/*|"$REPO_ROOT"/tmp/daxq-vm/osx-x64/*) ;;
  *) echo "Refusing unsafe DAXQ build path: $BUILD_DIR" >&2; exit 3 ;;
esac

rm -rf "$BUILD_DIR"
mkdir -p "$BUILD_DIR" "$OUTPUT_DIR"

CMAKE_ARGS=(
  -S "$SCRIPT_DIR"
  -B "$BUILD_DIR"
  -DCMAKE_BUILD_TYPE=Release
  -DCMAKE_OSX_ARCHITECTURES="$ARCH"
  -DCMAKE_OSX_DEPLOYMENT_TARGET="${MACOSX_DEPLOYMENT_TARGET:-12.0}"
  -DBUILD_TESTING="$RUN_TESTS"
  -DDAXQ_VM_HARDENED_RELEASE="$HARDENED"
)
if [[ "$HARDENED" == "ON" ]]; then
  CMAKE_ARGS+=(
    -DDAXQ_VM_LICENSE_KEY_SHA256_HEX="$DAXQ_VM_LICENSE_KEY_SHA256_HEX"
    -DDAXQ_VM_LICENSE_ISSUER="$DAXQ_VM_LICENSE_ISSUER"
    -DDAXQ_VM_LICENSE_AUDIENCE="$DAXQ_VM_LICENSE_AUDIENCE"
  )
fi

cmake "${CMAKE_ARGS[@]}"
cmake --build "$BUILD_DIR" --config Release --parallel
if [[ "$RUN_TESTS" == "ON" ]]; then
  ctest --test-dir "$BUILD_DIR" --build-config Release --output-on-failure
fi

LIBRARY="$BUILD_DIR/libdaxq_vm.dylib"
[[ -f "$LIBRARY" ]] || { echo "DAXQ VM build did not produce $LIBRARY" >&2; exit 4; }
cp "$LIBRARY" "$OUTPUT_DIR/libdaxq_vm.dylib"
lipo -verify_arch "$ARCH" "$OUTPUT_DIR/libdaxq_vm.dylib"
echo "$OUTPUT_DIR/libdaxq_vm.dylib"
