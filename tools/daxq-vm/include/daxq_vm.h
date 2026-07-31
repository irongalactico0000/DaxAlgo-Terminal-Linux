#ifndef DAXQ_VM_H
#define DAXQ_VM_H

#include <stdint.h>

#if defined(_WIN32)
#if defined(DAXQ_VM_BUILD)
#define DAXQ_VM_API __declspec(dllexport)
#else
#define DAXQ_VM_API __declspec(dllimport)
#endif
#define DAXQ_VM_CALL __cdecl
#else
#define DAXQ_VM_API __attribute__((visibility("default")))
#define DAXQ_VM_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

enum { DAXQ_VM_ABI_VERSION = 3, DAXQ_VM_PROTECTION_ABI_VERSION = 1 };

typedef enum daxq_fault {
  DAXQ_FAULT_OK = 0,
  DAXQ_FAULT_INVALID_ARGUMENT = 1,
  DAXQ_FAULT_ABI_MISMATCH = 2,
  DAXQ_FAULT_INVALID_FORMAT = 3,
  DAXQ_FAULT_VERIFICATION = 4,
  DAXQ_FAULT_ENTRYPOINT_NOT_FOUND = 5,
  DAXQ_FAULT_INVALID_LIFECYCLE = 6,
  DAXQ_FAULT_TYPE = 7,
  DAXQ_FAULT_NUMERIC = 8,
  DAXQ_FAULT_DIVIDE_BY_ZERO = 9,
  DAXQ_FAULT_INDEX_OUT_OF_RANGE = 10,
  DAXQ_FAULT_HOST = 11,
  DAXQ_FAULT_INSTRUCTION_BUDGET = 12,
  DAXQ_FAULT_STACK_BUDGET = 13,
  DAXQ_FAULT_TIMEOUT = 14,
  DAXQ_FAULT_BUFFER_LIMIT = 15,
  DAXQ_FAULT_EFFECT_LIMIT = 16,
  DAXQ_FAULT_REENTRANT = 17,
  DAXQ_FAULT_INTERNAL = 18
} daxq_fault;

typedef enum daxq_value_tag {
  DAXQ_VALUE_I64 = 1,
  DAXQ_VALUE_F64 = 2,
  DAXQ_VALUE_BOOL = 3
} daxq_value_tag;

typedef struct daxq_vm_handle daxq_vm_handle;

typedef struct daxq_vm_blob {
  const uint8_t *data;
  uint32_t length;
} daxq_vm_blob;

typedef struct daxq_vm_create_options {
  uint32_t abi_version;
  uint32_t struct_size;
  daxq_vm_blob bytecode;
  daxq_vm_blob constant_pool;
  daxq_vm_blob opcode_map;
  daxq_vm_blob host_map;
  daxq_vm_blob entrypoints;
} daxq_vm_create_options;

typedef int32_t(DAXQ_VM_CALL *daxq_bar_callback)(void *context, int64_t field,
                                                 int64_t lookback,
                                                 double *result);

typedef int32_t(DAXQ_VM_CALL *daxq_ind_callback)(void *context,
                                                 int64_t indicator,
                                                 int64_t period,
                                                 int64_t source_field,
                                                 double *result);

typedef int32_t(DAXQ_VM_CALL *daxq_param_callback)(void *context,
                                                   int64_t param_id,
                                                   double *result);

typedef int32_t(DAXQ_VM_CALL *daxq_emit_callback)(void *context, int64_t kind,
                                                  double strength,
                                                  int64_t note_id);

/* ABI slot 5 is a required typed marker. The VM never invokes it. */
typedef int32_t(DAXQ_VM_CALL *daxq_state_callback)(void *context);

typedef int32_t(DAXQ_VM_CALL *daxq_tindex_callback)(void *context,
                                                    int64_t *result);

typedef int32_t(DAXQ_VM_CALL *daxq_rng_callback)(void *context, double *result);

typedef int32_t(DAXQ_VM_CALL *daxq_log_callback)(void *context, int64_t msg_id,
                                                 double value);

typedef struct daxq_vm_host_callbacks {
  uint32_t abi_version;
  uint32_t struct_size;
  void *context;
  daxq_bar_callback bar;
  daxq_ind_callback ind;
  daxq_param_callback param;
  daxq_emit_callback emit;
  daxq_state_callback state;
  daxq_tindex_callback tindex;
  daxq_rng_callback rng;
  daxq_log_callback log;
} daxq_vm_host_callbacks;

typedef union daxq_vm_value_data {
  int64_t i64;
  double f64;
  uint64_t boolean;
} daxq_vm_value_data;

typedef struct daxq_vm_value {
  uint8_t tag;
  uint8_t reserved[7];
  daxq_vm_value_data data;
} daxq_vm_value;

typedef struct daxq_vm_invoke_options {
  uint32_t abi_version;
  uint32_t struct_size;
  uint8_t entrypoint_id;
  uint8_t reserved0[7];
  uint32_t arg_count;
  uint32_t reserved1;
  const daxq_vm_value *args;
} daxq_vm_invoke_options;

typedef struct daxq_vm_invoke_result {
  uint32_t abi_version;
  uint32_t struct_size;
  int32_t fault;
  uint32_t executed_instructions;
  uint32_t max_stack_depth;
  uint32_t reserved;
} daxq_vm_invoke_result;

/*
 * Protection ABI v1 is deliberately separate from frozen VM ABI 3. The payload
 * is the decoded UTF-8 JSON covered by the ES256 signature. public_key is the
 * raw P-256 X || Y value and signature is IEEE-P1363 r || s. Hardened builds
 * accept the key only when SHA-256(public_key) matches the compile-time
 * DAXQ_VM_LICENSE_KEY_SHA256_HEX pin.
 */
typedef struct daxq_vm_license_evidence {
  uint32_t protection_abi_version;
  uint32_t struct_size;
  daxq_vm_blob payload;
  uint8_t signature[64];
  uint8_t public_key[64];
} daxq_vm_license_evidence;

DAXQ_VM_API int32_t DAXQ_VM_CALL
daxq_vm_create(const daxq_vm_create_options *options, daxq_vm_handle **result);

DAXQ_VM_API int32_t DAXQ_VM_CALL daxq_vm_set_host_callbacks(
    daxq_vm_handle *vm, const daxq_vm_host_callbacks *callbacks);

DAXQ_VM_API int32_t DAXQ_VM_CALL
daxq_vm_invoke(daxq_vm_handle *vm, const daxq_vm_invoke_options *options,
               daxq_vm_invoke_result *result);

DAXQ_VM_API int32_t DAXQ_VM_CALL daxq_vm_apply_license_evidence(
    daxq_vm_handle *vm, const daxq_vm_license_evidence *evidence);

DAXQ_VM_API int32_t DAXQ_VM_CALL daxq_vm_revoke_license(daxq_vm_handle *vm);

DAXQ_VM_API int32_t DAXQ_VM_CALL daxq_vm_verify_integrity(void);

DAXQ_VM_API void DAXQ_VM_CALL daxq_vm_destroy(daxq_vm_handle *vm);

#ifdef __cplusplus
}
#endif

#endif
