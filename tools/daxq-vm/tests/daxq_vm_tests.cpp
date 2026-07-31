#include "daxq_vm.h"

#include <array>
#include <atomic>
#include <bit>
#include <cfenv>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <iostream>
#include <limits>
#include <string_view>
#include <thread>
#include <vector>

#if defined(_M_IX86) || defined(_M_X64) || defined(__i386__) ||                \
    defined(__x86_64__)
#include <xmmintrin.h>
#define DAXQ_VM_TEST_X86_FP 1
#endif

namespace {

struct HostContext {
  std::int64_t current_index{42};
  int emitted{};
  std::int64_t emitted_kind{};
  double emitted_strength{};
  int logged{};
  double logged_value{};
  bool slow_parameter{};
  std::atomic<bool> block_parameter{};
  std::atomic<bool> parameter_entered{};
  std::atomic<bool> release_parameter{};
  daxq_vm_handle *destroy_on_parameter{};
};

std::vector<std::uint8_t> hex(std::string_view text) {
  auto nibble = [](char value) -> std::uint8_t {
    if (value >= '0' && value <= '9')
      return static_cast<std::uint8_t>(value - '0');
    if (value >= 'a' && value <= 'f')
      return static_cast<std::uint8_t>(value - 'a' + 10);
    if (value >= 'A' && value <= 'F')
      return static_cast<std::uint8_t>(value - 'A' + 10);
    std::abort();
  };
  if ((text.size() % 2U) != 0)
    std::abort();
  std::vector<std::uint8_t> result(text.size() / 2U);
  for (std::size_t index = 0; index < result.size(); ++index) {
    result[index] = static_cast<std::uint8_t>((nibble(text[index * 2U]) << 4U) |
                                              nibble(text[index * 2U + 1U]));
  }
  return result;
}

void append_u16(std::vector<std::uint8_t> &bytes, std::uint16_t value) {
  bytes.push_back(static_cast<std::uint8_t>(value));
  bytes.push_back(static_cast<std::uint8_t>(value >> 8U));
}

void append_u32(std::vector<std::uint8_t> &bytes, std::uint32_t value) {
  for (unsigned shift = 0; shift < 32; shift += 8) {
    bytes.push_back(static_cast<std::uint8_t>(value >> shift));
  }
}

std::vector<std::uint8_t> identity_opcode_map() {
  std::vector<std::uint8_t> bytes;
  append_u16(bytes, 34);
  for (std::uint8_t value = 1; value <= 34; ++value) {
    bytes.push_back(value);
    bytes.push_back(value);
  }
  return bytes;
}

std::vector<std::uint8_t> identity_host_map() {
  std::vector<std::uint8_t> bytes;
  append_u16(bytes, 8);
  for (std::uint16_t value = 1; value <= 8; ++value) {
    append_u16(bytes, value);
    append_u16(bytes, value);
  }
  return bytes;
}

std::vector<std::uint8_t> one_entrypoint(std::uint8_t id,
                                         std::uint8_t arguments,
                                         std::uint16_t locals,
                                         std::uint32_t code_length) {
  std::vector<std::uint8_t> bytes;
  append_u16(bytes, 0);
  bytes.push_back(1);
  bytes.push_back(id);
  bytes.push_back(arguments);
  append_u16(bytes, locals);
  append_u32(bytes, 0);
  append_u32(bytes, 0);
  append_u32(bytes, code_length);
  return bytes;
}

std::vector<std::uint8_t>
constants(std::initializer_list<std::int64_t> values) {
  std::vector<std::uint8_t> bytes;
  append_u16(bytes, static_cast<std::uint16_t>(values.size()));
  for (const auto value : values) {
    bytes.push_back(1);
    const auto bits = static_cast<std::uint64_t>(value);
    for (unsigned shift = 0; shift < 64; shift += 8) {
      bytes.push_back(static_cast<std::uint8_t>(bits >> shift));
    }
  }
  return bytes;
}

struct ConstantLiteral {
  std::uint8_t tag{};
  std::uint64_t bits{};
};

ConstantLiteral ci64(std::int64_t value) {
  return {DAXQ_VALUE_I64, std::bit_cast<std::uint64_t>(value)};
}

ConstantLiteral cf64(double value) {
  return {DAXQ_VALUE_F64, std::bit_cast<std::uint64_t>(value)};
}

std::vector<std::uint8_t>
typed_constants(std::initializer_list<ConstantLiteral> values) {
  std::vector<std::uint8_t> bytes;
  append_u16(bytes, static_cast<std::uint16_t>(values.size()));
  for (const auto value : values) {
    bytes.push_back(value.tag);
    for (unsigned shift = 0; shift < 64; shift += 8) {
      bytes.push_back(static_cast<std::uint8_t>(value.bits >> shift));
    }
  }
  return bytes;
}

daxq_vm_blob blob(const std::vector<std::uint8_t> &bytes) {
  return {bytes.data(), static_cast<std::uint32_t>(bytes.size())};
}

int32_t DAXQ_VM_CALL bar(void *, std::int64_t, std::int64_t, double *result) {
  *result = 100.0;
  return 0;
}

int32_t DAXQ_VM_CALL indicator(void *, std::int64_t indicator_id,
                               std::int64_t period, std::int64_t source,
                               double *result) {
  if (indicator_id != 1 || source != 4)
    return 1;
  *result = period == 12 ? 2.0 : 1.0;
  return 0;
}

int32_t DAXQ_VM_CALL parameter(void *opaque, std::int64_t, double *result) {
  auto &context = *static_cast<HostContext *>(opaque);
  if (context.destroy_on_parameter != nullptr) {
    daxq_vm_handle *const vm = context.destroy_on_parameter;
    context.destroy_on_parameter = nullptr;
    daxq_vm_destroy(vm);
  }
  if (context.block_parameter.load(std::memory_order_acquire)) {
    context.parameter_entered.store(true, std::memory_order_release);
    while (!context.release_parameter.load(std::memory_order_acquire)) {
      std::this_thread::yield();
    }
  }
  if (context.slow_parameter)
    std::this_thread::sleep_for(std::chrono::milliseconds(10));
  *result = 1.0;
  return 0;
}

int32_t DAXQ_VM_CALL emit(void *opaque, std::int64_t kind, double strength,
                          std::int64_t) {
  auto &context = *static_cast<HostContext *>(opaque);
  ++context.emitted;
  context.emitted_kind = kind;
  context.emitted_strength = strength;
  return 0;
}

int32_t DAXQ_VM_CALL state_marker(void *) { return 0; }

int32_t DAXQ_VM_CALL tindex(void *opaque, std::int64_t *result) {
  *result = static_cast<HostContext *>(opaque)->current_index;
  return 0;
}

int32_t DAXQ_VM_CALL rng(void *, double *result) {
  *result = 0.5;
  return 0;
}

int32_t DAXQ_VM_CALL log_value(void *opaque, std::int64_t, double value) {
  auto &context = *static_cast<HostContext *>(opaque);
  ++context.logged;
  context.logged_value = value;
  return 0;
}

daxq_vm_host_callbacks callbacks(HostContext &context) {
  return {
      DAXQ_VM_ABI_VERSION,
      sizeof(daxq_vm_host_callbacks),
      &context,
      bar,
      indicator,
      parameter,
      emit,
      state_marker,
      tindex,
      rng,
      log_value,
  };
}

struct VmOwner {
  daxq_vm_handle *value{};
  ~VmOwner() { daxq_vm_destroy(value); }
};

bool create_vm(const std::vector<std::uint8_t> &bytecode,
               const std::vector<std::uint8_t> &constant_pool,
               const std::vector<std::uint8_t> &opcode_map,
               const std::vector<std::uint8_t> &host_map,
               const std::vector<std::uint8_t> &entrypoints, VmOwner &owner) {
  const daxq_vm_create_options options{
      DAXQ_VM_ABI_VERSION, sizeof(daxq_vm_create_options),
      blob(bytecode),      blob(constant_pool),
      blob(opcode_map),    blob(host_map),
      blob(entrypoints),
  };
  return daxq_vm_create(&options, &owner.value) == DAXQ_FAULT_OK;
}

int create_fault(const std::vector<std::uint8_t> &bytecode,
                 const std::vector<std::uint8_t> &constant_pool,
                 const std::vector<std::uint8_t> &opcode_map,
                 const std::vector<std::uint8_t> &host_map,
                 const std::vector<std::uint8_t> &entrypoints) {
  daxq_vm_handle *vm{};
  const daxq_vm_create_options options{
      DAXQ_VM_ABI_VERSION, sizeof(daxq_vm_create_options),
      blob(bytecode),      blob(constant_pool),
      blob(opcode_map),    blob(host_map),
      blob(entrypoints),
  };
  const int fault = daxq_vm_create(&options, &vm);
  daxq_vm_destroy(vm);
  return fault;
}

int invoke(daxq_vm_handle *vm, std::uint8_t id, const daxq_vm_value *arguments,
           std::uint32_t argument_count,
           daxq_vm_invoke_result *output = nullptr) {
  const daxq_vm_invoke_options options{
      DAXQ_VM_ABI_VERSION,
      sizeof(daxq_vm_invoke_options),
      id,
      {},
      argument_count,
      0,
      arguments,
  };
  daxq_vm_invoke_result local{
      DAXQ_VM_ABI_VERSION, sizeof(daxq_vm_invoke_result), 0, 0, 0, 0,
  };
  const int fault = daxq_vm_invoke(vm, &options, &local);
  if (output != nullptr)
    *output = local;
  return fault;
}

daxq_vm_value i64(std::int64_t value) {
  daxq_vm_value result{};
  result.tag = DAXQ_VALUE_I64;
  result.data.i64 = value;
  return result;
}

daxq_vm_value f64(double value) {
  daxq_vm_value result{};
  result.tag = DAXQ_VALUE_F64;
  result.data.f64 = value;
  return result;
}

#define CHECK(condition)                                                       \
  do {                                                                         \
    if (!(condition)) {                                                        \
      std::cerr << __FILE__ << ':' << __LINE__                                 \
                << ": CHECK failed: " #condition << '\n';                      \
      return false;                                                            \
    }                                                                          \
  } while (false)

bool golden_ema_cross_executes() {
  const auto bytecode =
      hex("0200000201000202002102000305000002000002030002020021020003050100"
          "040000040100111a120000000200000104000205002104000318190000000400"
          "000401000f1a0d0000000206000104000205002104000322");
  const auto constant_pool =
      hex("0700010100000000000000010c00000000000000010400000000000000011a00"
          "00000000000002000000000000f03f01000000000000000001ffffffffffffffff");
  const auto opcode_map =
      hex("22000101020203030404050506060707080809090a0a0b0b0c0c0d0d0e0e0f0f"
          "10101111121213131414151516161717181819191a1a1b1b1c1c1d1d1e1e1f1f"
          "202021212222");
  const auto host_map = hex(
      "08000100010002000200030003000400040005000500060006000700070008000800");
  const auto entrypoints = hex("00000101010200000000000000000058000000");

  VmOwner vm;
  CHECK(create_vm(bytecode, constant_pool, opcode_map, host_map, entrypoints,
                  vm));
  HostContext context;
  auto table = callbacks(context);
  CHECK(daxq_vm_set_host_callbacks(vm.value, &table) == DAXQ_FAULT_OK);
  const auto argument = i64(context.current_index);
  daxq_vm_invoke_result result{};
  CHECK(invoke(vm.value, 1, &argument, 1, &result) == DAXQ_FAULT_OK);
  CHECK(result.fault == DAXQ_FAULT_OK);
  CHECK(context.emitted == 1);
  CHECK(context.emitted_kind == 1);
  CHECK(context.emitted_strength == 1.0);
  CHECK(result.executed_instructions == 20);
  return true;
}

bool numeric_and_index_faults_are_contained() {
  const auto opcodes = identity_opcode_map();
  const auto hosts = identity_host_map();
  HostContext context;

  {
    const auto bytecode = hex("0200000201000705000022");
    const auto pool = constants({std::numeric_limits<std::int64_t>::max(), 1});
    const auto entries =
        one_entrypoint(1, 1, 1, static_cast<std::uint32_t>(bytecode.size()));
    VmOwner vm;
    CHECK(create_vm(bytecode, pool, opcodes, hosts, entries, vm));
    auto table = callbacks(context);
    CHECK(daxq_vm_set_host_callbacks(vm.value, &table) == DAXQ_FAULT_OK);
    const auto argument = i64(context.current_index);
    CHECK(invoke(vm.value, 1, &argument, 1) == DAXQ_FAULT_NUMERIC);
  }
  {
    const auto bytecode = hex("0200000201000a05000022");
    const auto pool = constants({1, 0});
    const auto entries =
        one_entrypoint(1, 1, 1, static_cast<std::uint32_t>(bytecode.size()));
    VmOwner vm;
    CHECK(create_vm(bytecode, pool, opcodes, hosts, entries, vm));
    auto table = callbacks(context);
    CHECK(daxq_vm_set_host_callbacks(vm.value, &table) == DAXQ_FAULT_OK);
    const auto argument = i64(context.current_index);
    CHECK(invoke(vm.value, 1, &argument, 1) == DAXQ_FAULT_DIVIDE_BY_ZERO);
  }
  {
    const auto bytecode = hex("1b0101000500000400000200001c05010022");
    const auto pool = constants({1});
    const auto entries =
        one_entrypoint(1, 1, 2, static_cast<std::uint32_t>(bytecode.size()));
    VmOwner vm;
    CHECK(create_vm(bytecode, pool, opcodes, hosts, entries, vm));
    auto table = callbacks(context);
    CHECK(daxq_vm_set_host_callbacks(vm.value, &table) == DAXQ_FAULT_OK);
    const auto argument = i64(context.current_index);
    CHECK(invoke(vm.value, 1, &argument, 1) == DAXQ_FAULT_INDEX_OUT_OF_RANGE);
  }
  {
    const auto bytecode = hex("0600002103000105000022");
    const auto pool = constants({});
    const auto entries =
        one_entrypoint(1, 1, 1, static_cast<std::uint32_t>(bytecode.size()));
    VmOwner vm;
    CHECK(create_vm(bytecode, pool, opcodes, hosts, entries, vm));
    auto table = callbacks(context);
    CHECK(daxq_vm_set_host_callbacks(vm.value, &table) == DAXQ_FAULT_OK);
    const auto argument = i64(256);
    CHECK(invoke(vm.value, 1, &argument, 1) == DAXQ_FAULT_HOST);
  }
  return true;
}

bool unmasked_floating_exceptions_are_contained() {
#if defined(DAXQ_VM_TEST_X86_FP)
  const auto bytecode = hex("0100000101000905000022");
  const auto pool = typed_constants({
      cf64(std::numeric_limits<double>::max()),
      cf64(2.0),
  });
  const auto opcodes = identity_opcode_map();
  const auto hosts = identity_host_map();
  const auto entries =
      one_entrypoint(1, 1, 1, static_cast<std::uint32_t>(bytecode.size()));
  VmOwner vm;
  CHECK(create_vm(bytecode, pool, opcodes, hosts, entries, vm));
  HostContext context;
  auto table = callbacks(context);
  CHECK(daxq_vm_set_host_callbacks(vm.value, &table) == DAXQ_FAULT_OK);

  constexpr unsigned kExceptionFlags = 0x003fU;
  constexpr unsigned kInvalidFlag = 0x0001U;
  constexpr unsigned kOverflowMask = 0x0400U;
  const unsigned saved_mxcsr = _mm_getcsr();
  const unsigned caller_mxcsr =
      ((saved_mxcsr & ~kExceptionFlags) | kInvalidFlag) & ~kOverflowMask;
  _mm_setcsr(caller_mxcsr);
  const auto argument = i64(context.current_index);
  const int fault = invoke(vm.value, 1, &argument, 1);
  const unsigned restored_mxcsr = _mm_getcsr();
  _mm_setcsr(saved_mxcsr);

  if (fault != DAXQ_FAULT_NUMERIC)
    std::cerr << "unmasked overflow fault=" << fault << '\n';
  CHECK(fault == DAXQ_FAULT_NUMERIC);
  CHECK(restored_mxcsr == caller_mxcsr);
#endif
  return true;
}

bool budgets_abort_safely() {
  const auto opcodes = identity_opcode_map();
  const auto hosts = identity_host_map();
  const auto empty_pool = constants({});
  HostContext context;

  {
    const auto bytecode = hex("18fbffffff");
    const auto entries =
        one_entrypoint(2, 5, 0, static_cast<std::uint32_t>(bytecode.size()));
    VmOwner vm;
    CHECK(create_vm(bytecode, empty_pool, opcodes, hosts, entries, vm));
    auto table = callbacks(context);
    CHECK(daxq_vm_set_host_callbacks(vm.value, &table) == DAXQ_FAULT_OK);
    const std::array arguments{i64(context.current_index), f64(1), f64(1),
                               f64(1), f64(1)};
    daxq_vm_invoke_result result{};
    CHECK(invoke(vm.value, 2, arguments.data(),
                 static_cast<std::uint32_t>(arguments.size()),
                 &result) == DAXQ_FAULT_INSTRUCTION_BUDGET);
    CHECK(result.executed_instructions == 25'000);
  }
  {
    std::vector<std::uint8_t> bytecode;
    for (int index = 0; index < 129; ++index) {
      bytecode.push_back(0x03);
      bytecode.push_back(0x01);
    }
    for (int index = 0; index < 128; ++index)
      bytecode.push_back(0x13);
    bytecode.insert(bytecode.end(), {0x05, 0x00, 0x00});
    bytecode.push_back(0x22);
    const auto entries =
        one_entrypoint(2, 5, 1, static_cast<std::uint32_t>(bytecode.size()));
    VmOwner vm;
    CHECK(create_vm(bytecode, empty_pool, opcodes, hosts, entries, vm));
    auto table = callbacks(context);
    CHECK(daxq_vm_set_host_callbacks(vm.value, &table) == DAXQ_FAULT_OK);
    const std::array arguments{i64(context.current_index), f64(1), f64(1),
                               f64(1), f64(1)};
    CHECK(invoke(vm.value, 2, arguments.data(),
                 static_cast<std::uint32_t>(arguments.size())) ==
          DAXQ_FAULT_STACK_BUDGET);
  }
  {
    const auto bytecode = hex("0200002103000105000022");
    const auto pool = constants({0});
    const auto entries =
        one_entrypoint(2, 5, 1, static_cast<std::uint32_t>(bytecode.size()));
    VmOwner vm;
    CHECK(create_vm(bytecode, pool, opcodes, hosts, entries, vm));
    context.slow_parameter = true;
    auto table = callbacks(context);
    CHECK(daxq_vm_set_host_callbacks(vm.value, &table) == DAXQ_FAULT_OK);
    const std::array arguments{i64(context.current_index), f64(1), f64(1),
                               f64(1), f64(1)};
    CHECK(invoke(vm.value, 2, arguments.data(),
                 static_cast<std::uint32_t>(arguments.size())) ==
          DAXQ_FAULT_TIMEOUT);
    context.slow_parameter = false;
  }
  return true;
}

bool diversified_maps_execute() {
  const auto bytecode = hex("a10000a3410101a20000a4");
  const auto pool = constants({0});
  const auto opcode_map = hex("0400a102a205a321a422");
  const auto host_map = hex("010041010300");
  const auto entries =
      one_entrypoint(1, 1, 1, static_cast<std::uint32_t>(bytecode.size()));
  VmOwner vm;
  CHECK(create_vm(bytecode, pool, opcode_map, host_map, entries, vm));
  HostContext context;
  auto table = callbacks(context);
  CHECK(daxq_vm_set_host_callbacks(vm.value, &table) == DAXQ_FAULT_OK);
  const auto argument = i64(context.current_index);
  CHECK(invoke(vm.value, 1, &argument, 1) == DAXQ_FAULT_OK);
  return true;
}

bool floating_environment_is_canonical_and_restored() {
  const auto bytecode = hex("010000010100070500000202000400002108000222");
  const auto pool = hex("0300"
                        "02000000000000f03f"
                        "02000000000000a03c"
                        "010000000000000000");
  const auto opcodes = identity_opcode_map();
  const auto hosts = identity_host_map();
  const auto entries =
      one_entrypoint(1, 1, 1, static_cast<std::uint32_t>(bytecode.size()));
  VmOwner vm;
  CHECK(create_vm(bytecode, pool, opcodes, hosts, entries, vm));
  HostContext context;
  auto table = callbacks(context);
  CHECK(daxq_vm_set_host_callbacks(vm.value, &table) == DAXQ_FAULT_OK);

  const int saved_rounding = std::fegetround();
  CHECK(saved_rounding != -1);
  CHECK(std::fesetround(FE_UPWARD) == 0);
  const auto argument = i64(context.current_index);
  const int fault = invoke(vm.value, 1, &argument, 1);
  const int restored_rounding = std::fegetround();
  (void)std::fesetround(saved_rounding);

  CHECK(fault == DAXQ_FAULT_OK);
  CHECK(restored_rounding == FE_UPWARD);
  CHECK(context.logged == 1);
  CHECK(context.logged_value == 1.0);

  // Verification folds under the canonical environment and restores the
  // caller's mode.
  const auto folded_code = hex("020000010100010200070203002104000322");
  const auto folded_pool = typed_constants({
      ci64(1),
      cf64(1.0),
      cf64(std::numeric_limits<double>::denorm_min()),
      ci64(0),
  });
  const auto folded_entries =
      one_entrypoint(1, 1, 0, static_cast<std::uint32_t>(folded_code.size()));
  CHECK(std::fesetround(FE_UPWARD) == 0);
  VmOwner folded_vm;
  const bool folded_created = create_vm(folded_code, folded_pool, opcodes,
                                        hosts, folded_entries, folded_vm);
  const int verification_rounding = std::fegetround();
  (void)std::fesetround(saved_rounding);
  CHECK(folded_created);
  CHECK(verification_rounding == FE_UPWARD);
  return true;
}

bool state_and_effects_roll_back_on_fault() {
  const auto on_bar = hex("0600002000000200000201000a05000022");
  const auto on_tick = hex("0201001f0000162108000222");
  auto bytecode = on_bar;
  bytecode.insert(bytecode.end(), on_tick.begin(), on_tick.end());
  const auto pool = constants({1, 0});
  const auto opcodes = identity_opcode_map();
  const auto hosts = identity_host_map();
  std::vector<std::uint8_t> entries;
  append_u16(entries, 1);
  entries.push_back(1);
  entries.push_back(2);
  entries.push_back(1);
  entries.push_back(1);
  append_u16(entries, 1);
  append_u32(entries, 0);
  append_u32(entries, 0);
  append_u32(entries, static_cast<std::uint32_t>(on_bar.size()));
  entries.push_back(2);
  entries.push_back(5);
  append_u16(entries, 0);
  append_u32(entries, 0);
  append_u32(entries, static_cast<std::uint32_t>(on_bar.size()));
  append_u32(entries, static_cast<std::uint32_t>(on_tick.size()));

  VmOwner vm;
  CHECK(create_vm(bytecode, pool, opcodes, hosts, entries, vm));
  HostContext context;
  auto table = callbacks(context);
  CHECK(daxq_vm_set_host_callbacks(vm.value, &table) == DAXQ_FAULT_OK);
  const auto bar_argument = i64(99);
  CHECK(invoke(vm.value, 1, &bar_argument, 1) == DAXQ_FAULT_DIVIDE_BY_ZERO);

  const std::array tick_arguments{i64(context.current_index), f64(1), f64(1),
                                  f64(1), f64(1)};
  CHECK(invoke(vm.value, 2, tick_arguments.data(),
               static_cast<std::uint32_t>(tick_arguments.size())) ==
        DAXQ_FAULT_OK);
  CHECK(context.logged == 1);
  CHECK(context.logged_value == 0.0);

  const auto effect_code = hex("02000001010002020021040003"
                               "0200000202000a05000022");
  const auto effect_pool = hex("0300"
                               "010100000000000000"
                               "02000000000000f03f"
                               "010000000000000000");
  const auto effect_entries =
      one_entrypoint(1, 1, 1, static_cast<std::uint32_t>(effect_code.size()));
  VmOwner effect_vm;
  CHECK(create_vm(effect_code, effect_pool, opcodes, hosts, effect_entries,
                  effect_vm));
  HostContext effect_context;
  auto effect_table = callbacks(effect_context);
  CHECK(daxq_vm_set_host_callbacks(effect_vm.value, &effect_table) ==
        DAXQ_FAULT_OK);
  CHECK(invoke(effect_vm.value, 1, &bar_argument, 1) ==
        DAXQ_FAULT_DIVIDE_BY_ZERO);
  CHECK(effect_context.emitted == 0);
  return true;
}

bool verifier_rejects_unreachable_and_inconsistent_locals() {
  const auto opcodes = identity_opcode_map();
  const auto hosts = identity_host_map();
  const auto empty_pool = constants({});

  {
    const auto code = hex("2222");
    const auto entries =
        one_entrypoint(1, 1, 0, static_cast<std::uint32_t>(code.size()));
    CHECK(create_fault(code, empty_pool, opcodes, hosts, entries) ==
          DAXQ_FAULT_VERIFICATION);
  }
  {
    const auto code = hex("0600000200000d1907000000"
                          "02010005000022"
                          "01020005000022");
    const auto pool = typed_constants({ci64(0), ci64(1), cf64(1.0)});
    const auto entries =
        one_entrypoint(1, 1, 1, static_cast<std::uint32_t>(code.size()));
    CHECK(create_fault(code, pool, opcodes, hosts, entries) ==
          DAXQ_FAULT_VERIFICATION);
  }
  return true;
}

bool verifier_rejects_provably_invalid_host_arguments() {
  const auto opcodes = identity_opcode_map();
  const auto hosts = identity_host_map();
  auto rejected = [&](std::string_view code_hex,
                      const std::vector<std::uint8_t> &pool,
                      std::uint16_t locals) {
    const auto code = hex(code_hex);
    const auto entries =
        one_entrypoint(1, 1, locals, static_cast<std::uint32_t>(code.size()));
    return create_fault(code, pool, opcodes, hosts, entries) ==
           DAXQ_FAULT_VERIFICATION;
  };

  // The invalid field is folded through arithmetic and a local before
  // CALL_HOST.
  CHECK(rejected("020000020100070500000400000202002101000205010022",
                 constants({2, 4, 0}), 2));
  CHECK(rejected("0200000201002101000205000022", constants({1, 65536}), 1));

  const auto ind_code = "0200000201000202002102000305000022";
  CHECK(rejected(ind_code, constants({5, 1, 4}), 1));
  CHECK(rejected(ind_code, constants({1, 0, 4}), 1));
  CHECK(rejected(ind_code, constants({1, 1, 6}), 1));
  CHECK(rejected(ind_code, constants({4, 1, 1}), 1));

  CHECK(rejected("0200000c2103000105000022", constants({1}), 1));
  CHECK(rejected("0200002103000105000022", constants({256}), 1));
  CHECK(rejected("1b0100011e2103000105000022", constants({}), 1));
  CHECK(rejected("0200000101000202002104000322",
                 typed_constants({ci64(2), cf64(0.5), ci64(0)}), 0));
  CHECK(rejected("02000002010002020007160203002104000322",
                 constants({1, 1, 1, 0}), 0));
  CHECK(rejected("0200000101000202000c2104000322",
                 typed_constants({ci64(1), cf64(0.5), ci64(1)}), 0));
  CHECK(rejected("0200000c0101002108000222",
                 typed_constants({ci64(1), cf64(0.0)}), 0));

  // Non-finite constants are rejected by the container parser before
  // verification.
  const auto nonfinite_code = hex("0200000101000202002104000322");
  const auto nonfinite_pool = typed_constants(
      {ci64(1), cf64(std::numeric_limits<double>::infinity()), ci64(0)});
  const auto nonfinite_entries = one_entrypoint(
      1, 1, 0, static_cast<std::uint32_t>(nonfinite_code.size()));
  CHECK(create_fault(nonfinite_code, nonfinite_pool, opcodes, hosts,
                     nonfinite_entries) == DAXQ_FAULT_INVALID_FORMAT);
  return true;
}

bool handle_lifetime_and_raw_argument_validation_are_safe() {
  const auto opcodes = identity_opcode_map();
  const auto hosts = identity_host_map();
  const auto parameter_code = hex("0200002103000105000022");
  const auto pool = constants({0});
  const auto entries = one_entrypoint(
      1, 1, 1, static_cast<std::uint32_t>(parameter_code.size()));

  {
    VmOwner vm;
    CHECK(create_vm(parameter_code, pool, opcodes, hosts, entries, vm));
    HostContext context;
    context.destroy_on_parameter = vm.value;
    auto table = callbacks(context);
    CHECK(daxq_vm_set_host_callbacks(vm.value, &table) == DAXQ_FAULT_OK);
    daxq_vm_handle *const destroyed = vm.value;
    const auto argument = i64(context.current_index);
    CHECK(invoke(vm.value, 1, &argument, 1) == DAXQ_FAULT_OK);
    vm.value = nullptr;
    CHECK(daxq_vm_set_host_callbacks(destroyed, &table) ==
          DAXQ_FAULT_INVALID_ARGUMENT);
  }
  {
    VmOwner vm;
    CHECK(create_vm(parameter_code, pool, opcodes, hosts, entries, vm));
    HostContext context;
    context.block_parameter.store(true, std::memory_order_release);
    auto table = callbacks(context);
    CHECK(daxq_vm_set_host_callbacks(vm.value, &table) == DAXQ_FAULT_OK);
    const auto argument = i64(context.current_index);
    std::atomic<int> invoke_fault{DAXQ_FAULT_INTERNAL};
    std::thread invoking([&] {
      invoke_fault.store(invoke(vm.value, 1, &argument, 1),
                         std::memory_order_release);
    });
    const auto deadline =
        std::chrono::steady_clock::now() + std::chrono::seconds(1);
    while (!context.parameter_entered.load(std::memory_order_acquire) &&
           std::chrono::steady_clock::now() < deadline) {
      std::this_thread::yield();
    }
    const bool entered =
        context.parameter_entered.load(std::memory_order_acquire);
    daxq_vm_destroy(vm.value);
    vm.value = nullptr;
    context.release_parameter.store(true, std::memory_order_release);
    invoking.join();
    CHECK(entered);
    const int completed_fault = invoke_fault.load(std::memory_order_acquire);
    CHECK(completed_fault == DAXQ_FAULT_OK ||
          completed_fault == DAXQ_FAULT_TIMEOUT);
  }
  {
    const auto code = hex("22");
    const auto empty_pool = constants({});
    const auto tick_entries =
        one_entrypoint(2, 5, 0, static_cast<std::uint32_t>(code.size()));
    VmOwner vm;
    CHECK(create_vm(code, empty_pool, opcodes, hosts, tick_entries, vm));
    HostContext context;
    auto table = callbacks(context);
    CHECK(daxq_vm_set_host_callbacks(vm.value, &table) == DAXQ_FAULT_OK);
    const std::array arguments{
        i64(context.current_index),
        f64(std::numeric_limits<double>::infinity()),
        f64(1.0),
        f64(1.0),
        f64(1.0),
    };
    CHECK(invoke(vm.value, 2, arguments.data(),
                 static_cast<std::uint32_t>(arguments.size())) ==
          DAXQ_FAULT_INVALID_ARGUMENT);
  }
  return true;
}

bool protection_abi_fails_closed_after_evidence_or_revocation() {
#if !defined(DAXQ_VM_HARDENED_RELEASE)
  CHECK(daxq_vm_verify_integrity() == DAXQ_FAULT_OK);
  const auto code = hex("22");
  const auto pool = constants({});
  const auto opcodes = identity_opcode_map();
  const auto hosts = identity_host_map();
  const auto entries =
      one_entrypoint(1, 1, 0, static_cast<std::uint32_t>(code.size()));
  const auto argument = i64(0);

  {
    VmOwner vm;
    CHECK(create_vm(code, pool, opcodes, hosts, entries, vm));
    HostContext context;
    auto table = callbacks(context);
    CHECK(daxq_vm_set_host_callbacks(vm.value, &table) == DAXQ_FAULT_OK);
    CHECK(invoke(vm.value, 1, &argument, 1) == DAXQ_FAULT_OK);

    constexpr std::string_view payload = "{}";
    daxq_vm_license_evidence evidence{
        0,
        sizeof(daxq_vm_license_evidence),
        {reinterpret_cast<const std::uint8_t *>(payload.data()),
         static_cast<std::uint32_t>(payload.size())},
        {},
        {},
    };
    CHECK(daxq_vm_apply_license_evidence(vm.value, &evidence) ==
          DAXQ_FAULT_INVALID_ARGUMENT);
    CHECK(invoke(vm.value, 1, &argument, 1) == DAXQ_FAULT_INVALID_LIFECYCLE);
    evidence.protection_abi_version = DAXQ_VM_PROTECTION_ABI_VERSION;
    CHECK(daxq_vm_apply_license_evidence(vm.value, &evidence) ==
          DAXQ_FAULT_VERIFICATION);
    CHECK(invoke(vm.value, 1, &argument, 1) == DAXQ_FAULT_INVALID_LIFECYCLE);
  }
  {
    VmOwner vm;
    CHECK(create_vm(code, pool, opcodes, hosts, entries, vm));
    HostContext context;
    auto table = callbacks(context);
    CHECK(daxq_vm_set_host_callbacks(vm.value, &table) == DAXQ_FAULT_OK);
    CHECK(daxq_vm_revoke_license(vm.value) == DAXQ_FAULT_OK);
    CHECK(daxq_vm_revoke_license(vm.value) == DAXQ_FAULT_OK);
    CHECK(invoke(vm.value, 1, &argument, 1) == DAXQ_FAULT_INVALID_LIFECYCLE);

    constexpr std::string_view payload = "{}";
    const daxq_vm_license_evidence evidence{
        DAXQ_VM_PROTECTION_ABI_VERSION,
        sizeof(daxq_vm_license_evidence),
        {reinterpret_cast<const std::uint8_t *>(payload.data()),
         static_cast<std::uint32_t>(payload.size())},
        {},
        {},
    };
    CHECK(daxq_vm_apply_license_evidence(vm.value, &evidence) ==
          DAXQ_FAULT_INVALID_LIFECYCLE);
  }
  {
    const auto parameter_code = hex("0200002103000105000022");
    const auto parameter_pool = constants({0});
    const auto parameter_entries = one_entrypoint(
        1, 1, 1, static_cast<std::uint32_t>(parameter_code.size()));
    VmOwner vm;
    CHECK(create_vm(parameter_code, parameter_pool, opcodes, hosts,
                    parameter_entries, vm));
    HostContext context;
    context.block_parameter.store(true, std::memory_order_release);
    auto table = callbacks(context);
    CHECK(daxq_vm_set_host_callbacks(vm.value, &table) == DAXQ_FAULT_OK);
    std::atomic<int> result{DAXQ_FAULT_INTERNAL};
    std::thread invoking([&] {
      result.store(invoke(vm.value, 1, &argument, 1),
                   std::memory_order_release);
    });
    const auto deadline =
        std::chrono::steady_clock::now() + std::chrono::seconds(1);
    while (!context.parameter_entered.load(std::memory_order_acquire) &&
           std::chrono::steady_clock::now() < deadline) {
      std::this_thread::yield();
    }
    const bool entered =
        context.parameter_entered.load(std::memory_order_acquire);
    const int revoke_fault = daxq_vm_revoke_license(vm.value);
    context.release_parameter.store(true, std::memory_order_release);
    invoking.join();
    CHECK(entered);
    CHECK(revoke_fault == DAXQ_FAULT_OK);
    CHECK(result.load(std::memory_order_acquire) ==
          DAXQ_FAULT_INVALID_LIFECYCLE);
  }
  CHECK(daxq_vm_revoke_license(nullptr) == DAXQ_FAULT_INVALID_ARGUMENT);
#endif
  return true;
}

} // namespace

int main() {
  const std::array tests{
      golden_ema_cross_executes,
      numeric_and_index_faults_are_contained,
      unmasked_floating_exceptions_are_contained,
      budgets_abort_safely,
      diversified_maps_execute,
      floating_environment_is_canonical_and_restored,
      state_and_effects_roll_back_on_fault,
      verifier_rejects_unreachable_and_inconsistent_locals,
      verifier_rejects_provably_invalid_host_arguments,
      handle_lifetime_and_raw_argument_validation_are_safe,
      protection_abi_fails_closed_after_evidence_or_revocation,
  };
  for (const auto test : tests) {
    if (!test())
      return 1;
  }
  std::cout << tests.size() << " native DAXQ VM test groups passed\n";
  return 0;
}
