# DAXQ VM

This directory builds the Pro-only native execution engine for frozen DAXQ format v1 / VM ABI 3.
It accepts the authenticated plaintext bytecode, constant-pool, opcode-map, host-map, and entrypoint
sections through the C ABI in `include/daxq_vm.h`; package ZIP handling and decryption remain outside
the VM.

The VM pre-verifies bytecode before creating a handle, owns fixed callback-local stack, local,
buffer, state-staging, emit, and log storage, and never exposes file, network, process, reflection,
or general native-call facilities. Host callbacks are the only external surface. `emit` and `log`
callbacks are delivered only after a successful `RET`; the bridge must keep its callback context
transactional until `daxq_vm_invoke` returns success so that callback failures, watchdog faults, and
provisional `rng` progress can be discarded atomically.

Build with CMake 3.20 or later. The target is `daxq_vm`; enabling CTest also builds
`daxq_vm_tests`. Strict floating-point compilation is set explicitly and fast-math/FMA contraction
is not enabled.

## macOS

`bash tools/daxq-vm/build-macos.sh osx-arm64` (or `osx-x64`) builds the matching dylib and runs the
native test suite. Hardened builds require `DAXQ_VM_HARDENED_RELEASE=ON` plus the three
`DAXQ_VM_LICENSE_*` pin values. Hardened output is intentionally not usable until it has been signed.

`tools/macos/package.sh` owns release staging. With a Developer ID identity and the license pins it
builds and tests the portable VM, builds the hardened VM, signs it, embeds its exact SHA-256 and Team
identifier into the managed host, and places `libdaxq_vm.dylib` beside the app executable. Set
`DAXQ_VM_MODE=required` to make absence of any release input fatal; the default `auto` mode omits the
protected runtime and lets the managed host fail closed when those private release inputs are absent.
