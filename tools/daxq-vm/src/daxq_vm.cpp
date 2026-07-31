#include "daxq_vm.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <cfenv>
#include <charconv>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <optional>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <vector>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <bcrypt.h>
#include <softpub.h>
#include <wintrust.h>
#endif

#if defined(__APPLE__)
#include <CommonCrypto/CommonDigest.h>
#include <CoreFoundation/CoreFoundation.h>
#include <Security/Security.h>
#include <dlfcn.h>
#endif

#if defined(_M_IX86) || defined(_M_X64) || defined(__i386__) ||                \
    defined(__x86_64__)
#include <xmmintrin.h>
#define DAXQ_VM_X86_FP 1
#endif

namespace {

using Fault = daxq_fault;

static_assert(sizeof(daxq_vm_value) == 16);
static_assert(offsetof(daxq_vm_value, data) == 8);

constexpr std::uint8_t kEntrypointInitialize = 0;
constexpr std::uint8_t kEntrypointOnBar = 1;
constexpr std::uint8_t kEntrypointOnTick = 2;
constexpr std::size_t kEntrypointCount = 3;
constexpr std::size_t kMaxLocals = 256;
constexpr std::size_t kMaxStateSlots = 256;
constexpr std::size_t kPhysicalStack = 512;
constexpr std::size_t kMaxBuffers = 16;
constexpr std::size_t kMaxBufferElements = 4096;
constexpr std::size_t kMaxBufferBytes = 65536;
constexpr std::size_t kMaxEmits = 8;
constexpr std::size_t kMaxLogs = 16;
constexpr std::size_t kMaximumLicensePayloadBytes = 4096;
constexpr auto kMaximumLicenseLifetime = std::chrono::hours(24);
constexpr auto kMaximumIssuedAtSkew = std::chrono::minutes(2);
std::uint8_t module_anchor{};

#if defined(DAXQ_VM_LICENSE_KEY_SHA256_HEX)
constexpr std::string_view kLicenseKeySha256Hex =
    DAXQ_VM_LICENSE_KEY_SHA256_HEX;
#else
constexpr std::string_view kLicenseKeySha256Hex{};
#endif

#if defined(DAXQ_VM_LICENSE_ISSUER)
constexpr std::string_view kLicenseIssuer = DAXQ_VM_LICENSE_ISSUER;
#elif defined(DAXQ_VM_HARDENED_RELEASE)
#error "DAXQ_VM_LICENSE_ISSUER is required for hardened releases"
#else
constexpr std::string_view kLicenseIssuer = "daxalgo-platform-development";
#endif

#if defined(DAXQ_VM_LICENSE_AUDIENCE)
constexpr std::string_view kLicenseAudience = DAXQ_VM_LICENSE_AUDIENCE;
#elif defined(DAXQ_VM_HARDENED_RELEASE)
#error "DAXQ_VM_LICENSE_AUDIENCE is required for hardened releases"
#else
constexpr std::string_view kLicenseAudience = "daxalgo-daxq-host";
#endif

void secure_zero(void *value, std::size_t length) noexcept {
  if (value == nullptr || length == 0)
    return;
#if defined(_WIN32)
  (void)SecureZeroMemory(value, length);
#else
  volatile auto *bytes = static_cast<volatile std::uint8_t *>(value);
  while (length-- != 0)
    *bytes++ = 0;
#endif
}

template <typename T> void secure_erase(std::vector<T> &values) noexcept {
  if (!values.empty())
    secure_zero(values.data(), values.size() * sizeof(T));
}

[[nodiscard]] bool fixed_equal(const std::uint8_t *left,
                               const std::uint8_t *right,
                               std::size_t length) noexcept {
  std::uint8_t difference = 0;
  for (std::size_t index = 0; index < length; ++index) {
    difference |= static_cast<std::uint8_t>(left[index] ^ right[index]);
  }
  return difference == 0;
}

[[nodiscard]] int hex_value(char value) noexcept {
  if (value >= '0' && value <= '9')
    return value - '0';
  if (value >= 'a' && value <= 'f')
    return value - 'a' + 10;
  if (value >= 'A' && value <= 'F')
    return value - 'A' + 10;
  return -1;
}

[[nodiscard]] bool
decode_license_key_pin(std::array<std::uint8_t, 32> &result) noexcept {
  if (kLicenseKeySha256Hex.size() != result.size() * 2U)
    return false;
  for (std::size_t index = 0; index < result.size(); ++index) {
    const int high = hex_value(kLicenseKeySha256Hex[index * 2U]);
    const int low = hex_value(kLicenseKeySha256Hex[index * 2U + 1U]);
    if (high < 0 || low < 0)
      return false;
    result[index] = static_cast<std::uint8_t>((high << 4U) | low);
  }
  return true;
}

#if defined(_WIN32)
[[nodiscard]] bool sha256(const std::uint8_t *data, std::size_t length,
                          std::array<std::uint8_t, 32> &result) noexcept {
  if (data == nullptr || length > std::numeric_limits<ULONG>::max())
    return false;
  BCRYPT_ALG_HANDLE algorithm{};
  BCRYPT_HASH_HANDLE hash{};
  std::vector<std::uint8_t> object;
  bool succeeded = false;
  if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr,
                                  0) < 0)
    return false;
  ULONG object_length{};
  ULONG copied{};
  if (BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH,
                        reinterpret_cast<PUCHAR>(&object_length),
                        sizeof(object_length), &copied, 0) >= 0) {
    try {
      object.resize(object_length);
      if (BCryptCreateHash(algorithm, &hash, object.data(),
                           static_cast<ULONG>(object.size()), nullptr, 0,
                           0) >= 0 &&
          BCryptHashData(hash, const_cast<PUCHAR>(data),
                         static_cast<ULONG>(length), 0) >= 0 &&
          BCryptFinishHash(hash, result.data(),
                           static_cast<ULONG>(result.size()), 0) >= 0) {
        succeeded = true;
      }
    } catch (...) {
      succeeded = false;
    }
  }
  if (hash != nullptr)
    BCryptDestroyHash(hash);
  if (algorithm != nullptr)
    BCryptCloseAlgorithmProvider(algorithm, 0);
  secure_erase(object);
  return succeeded;
}

[[nodiscard]] bool
verify_es256(const daxq_vm_license_evidence &evidence) noexcept {
  std::array<std::uint8_t, 32> expected_pin{};
  std::array<std::uint8_t, 32> actual_pin{};
  std::array<std::uint8_t, 32> digest{};
  const bool pin_valid = kLicenseKeySha256Hex.empty() ||
                         (decode_license_key_pin(expected_pin) &&
                          sha256(evidence.public_key,
                                 sizeof(evidence.public_key), actual_pin) &&
                          fixed_equal(expected_pin.data(), actual_pin.data(),
                                      expected_pin.size()));
  if (!pin_valid ||
      !sha256(evidence.payload.data, evidence.payload.length, digest)) {
    secure_zero(expected_pin.data(), expected_pin.size());
    secure_zero(actual_pin.data(), actual_pin.size());
    secure_zero(digest.data(), digest.size());
    return false;
  }

  struct PublicKeyBlob {
    BCRYPT_ECCKEY_BLOB header;
    std::uint8_t coordinates[64];
  } key_blob{{BCRYPT_ECDSA_PUBLIC_P256_MAGIC, 32}, {}};
  std::memcpy(key_blob.coordinates, evidence.public_key,
              sizeof(key_blob.coordinates));

  BCRYPT_ALG_HANDLE algorithm{};
  BCRYPT_KEY_HANDLE key{};
  bool verified = false;
  if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_ECDSA_P256_ALGORITHM,
                                  nullptr, 0) >= 0 &&
      BCryptImportKeyPair(algorithm, nullptr, BCRYPT_ECCPUBLIC_BLOB, &key,
                          reinterpret_cast<PUCHAR>(&key_blob), sizeof(key_blob),
                          0) >= 0) {
    verified = BCryptVerifySignature(key, nullptr, digest.data(),
                                     static_cast<ULONG>(digest.size()),
                                     const_cast<PUCHAR>(evidence.signature),
                                     sizeof(evidence.signature), 0) >= 0;
  }
  if (key != nullptr)
    BCryptDestroyKey(key);
  if (algorithm != nullptr)
    BCryptCloseAlgorithmProvider(algorithm, 0);
  secure_zero(&key_blob, sizeof(key_blob));
  secure_zero(expected_pin.data(), expected_pin.size());
  secure_zero(actual_pin.data(), actual_pin.size());
  secure_zero(digest.data(), digest.size());
  return verified;
}
#elif defined(__APPLE__)
[[nodiscard]] bool sha256(const std::uint8_t *data, std::size_t length,
                          std::array<std::uint8_t, 32> &result) noexcept {
  if (data == nullptr || length > std::numeric_limits<CC_LONG>::max())
    return false;
  return CC_SHA256(data, static_cast<CC_LONG>(length), result.data()) !=
         nullptr;
}

[[nodiscard]] bool encode_es256_signature(
    const std::uint8_t signature[64], std::array<std::uint8_t, 72> &result,
    std::size_t &result_length) noexcept {
  result_length = 2;
  result[0] = 0x30;
  result[1] = 0;
  for (std::size_t component = 0; component < 2; ++component) {
    const auto *source = signature + component * 32U;
    std::size_t first = 0;
    while (first < 31U && source[first] == 0)
      ++first;
    const std::size_t value_length = 32U - first;
    const bool prepend_zero = (source[first] & 0x80U) != 0;
    const std::size_t encoded_length = value_length + (prepend_zero ? 1U : 0U);
    if (result_length + 2U + encoded_length > result.size())
      return false;
    result[result_length++] = 0x02;
    result[result_length++] = static_cast<std::uint8_t>(encoded_length);
    if (prepend_zero)
      result[result_length++] = 0;
    std::memcpy(result.data() + result_length, source + first, value_length);
    result_length += value_length;
  }
  result[1] = static_cast<std::uint8_t>(result_length - 2U);
  return true;
}

[[nodiscard]] bool
verify_es256(const daxq_vm_license_evidence &evidence) noexcept {
  std::array<std::uint8_t, 32> expected_pin{};
  std::array<std::uint8_t, 32> actual_pin{};
  std::array<std::uint8_t, 32> digest{};
  std::array<std::uint8_t, 65> public_key{};
  std::array<std::uint8_t, 72> der_signature{};
  std::size_t der_length{};
  bool verified = false;

  const bool pin_valid = kLicenseKeySha256Hex.empty() ||
                         (decode_license_key_pin(expected_pin) &&
                          sha256(evidence.public_key,
                                 sizeof(evidence.public_key), actual_pin) &&
                          fixed_equal(expected_pin.data(), actual_pin.data(),
                                      expected_pin.size()));
  if (!pin_valid ||
      !sha256(evidence.payload.data, evidence.payload.length, digest) ||
      !encode_es256_signature(evidence.signature, der_signature, der_length)) {
    secure_zero(expected_pin.data(), expected_pin.size());
    secure_zero(actual_pin.data(), actual_pin.size());
    secure_zero(digest.data(), digest.size());
    secure_zero(public_key.data(), public_key.size());
    secure_zero(der_signature.data(), der_signature.size());
    return false;
  }

  public_key[0] = 0x04;
  std::memcpy(public_key.data() + 1U, evidence.public_key,
              sizeof(evidence.public_key));
  CFDataRef key_data = CFDataCreate(kCFAllocatorDefault, public_key.data(),
                                    static_cast<CFIndex>(public_key.size()));
  CFDataRef digest_data = CFDataCreate(kCFAllocatorDefault, digest.data(),
                                       static_cast<CFIndex>(digest.size()));
  CFDataRef signature_data =
      CFDataCreate(kCFAllocatorDefault, der_signature.data(),
                   static_cast<CFIndex>(der_length));
  int key_size = 256;
  CFNumberRef key_size_number = CFNumberCreate(
      kCFAllocatorDefault, kCFNumberIntType, &key_size);
  const void *attribute_keys[] = {kSecAttrKeyType, kSecAttrKeyClass,
                                  kSecAttrKeySizeInBits};
  const void *attribute_values[] = {kSecAttrKeyTypeECSECPrimeRandom,
                                    kSecAttrKeyClassPublic, key_size_number};
  CFDictionaryRef attributes = nullptr;
  SecKeyRef key = nullptr;
  CFErrorRef error = nullptr;
  if (key_data != nullptr && digest_data != nullptr && signature_data != nullptr &&
      key_size_number != nullptr) {
    attributes = CFDictionaryCreate(
        kCFAllocatorDefault, attribute_keys, attribute_values, 3,
        &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    if (attributes != nullptr)
      key = SecKeyCreateWithData(key_data, attributes, &error);
    if (key != nullptr) {
      verified = SecKeyVerifySignature(
          key, kSecKeyAlgorithmECDSASignatureDigestX962SHA256, digest_data,
          signature_data, &error);
    }
  }

  if (error != nullptr)
    CFRelease(error);
  if (key != nullptr)
    CFRelease(key);
  if (attributes != nullptr)
    CFRelease(attributes);
  if (key_size_number != nullptr)
    CFRelease(key_size_number);
  if (signature_data != nullptr)
    CFRelease(signature_data);
  if (digest_data != nullptr)
    CFRelease(digest_data);
  if (key_data != nullptr)
    CFRelease(key_data);
  secure_zero(expected_pin.data(), expected_pin.size());
  secure_zero(actual_pin.data(), actual_pin.size());
  secure_zero(digest.data(), digest.size());
  secure_zero(public_key.data(), public_key.size());
  secure_zero(der_signature.data(), der_signature.size());
  return verified;
}
#else
[[nodiscard]] bool verify_es256(const daxq_vm_license_evidence &) noexcept {
  return false;
}
#endif

class FlatJsonReader final {
public:
  FlatJsonReader(const std::uint8_t *data, std::size_t length) noexcept
      : current_(reinterpret_cast<const char *>(data)),
        end_(current_ + length) {}

  [[nodiscard]] bool consume(char expected) noexcept {
    skip_space();
    if (current_ == end_ || *current_ != expected)
      return false;
    ++current_;
    return true;
  }

  [[nodiscard]] bool read_string(std::string_view &result) noexcept {
    skip_space();
    if (current_ == end_ || *current_++ != '"')
      return false;
    const char *const start = current_;
    while (current_ != end_) {
      const unsigned char value = static_cast<unsigned char>(*current_);
      if (value == '"') {
        result =
            std::string_view(start, static_cast<std::size_t>(current_ - start));
        ++current_;
        return true;
      }
      if (value == '\\' || value < 0x20U || value > 0x7eU)
        return false;
      ++current_;
    }
    return false;
  }

  [[nodiscard]] bool read_integer(std::int64_t &result) noexcept {
    skip_space();
    if (current_ == end_)
      return false;
    const char *const start = current_;
    if (*current_ == '-')
      ++current_;
    const char *const digits = current_;
    while (current_ != end_ && *current_ >= '0' && *current_ <= '9')
      ++current_;
    if (digits == current_ || (current_ - digits > 1 && *digits == '0') ||
        (start != digits && current_ - digits == 1 && *digits == '0')) {
      return false;
    }
    const auto parsed = std::from_chars(start, current_, result);
    return parsed.ec == std::errc{} && parsed.ptr == current_;
  }

  [[nodiscard]] bool finished() noexcept {
    skip_space();
    return current_ == end_;
  }

private:
  void skip_space() noexcept {
    while (current_ != end_ && (*current_ == ' ' || *current_ == '\t' ||
                                *current_ == '\r' || *current_ == '\n')) {
      ++current_;
    }
  }

  const char *current_{};
  const char *end_{};
};

enum class LicenseTokenKind : std::uint8_t {
  Run,
  Offline,
};

struct LicenseClaims {
  LicenseTokenKind kind{};
  std::string_view token_id;
  std::string_view license_id;
  std::string_view release_id;
  std::string_view account_id;
  std::string_view device_id;
  std::chrono::system_clock::time_point issued_at{};
  std::chrono::system_clock::time_point expires_at{};
  std::chrono::system_clock::time_point access_valid_until{};
  std::int64_t revocation_sequence{};
};

[[nodiscard]] bool parse_fixed_decimal(std::string_view value,
                                       std::size_t offset, std::size_t count,
                                       int &result) noexcept {
  if (offset > value.size() || count > value.size() - offset)
    return false;
  result = 0;
  for (std::size_t index = 0; index < count; ++index) {
    const char digit = value[offset + index];
    if (digit < '0' || digit > '9')
      return false;
    result = result * 10 + (digit - '0');
  }
  return true;
}

[[nodiscard]] bool leap_year(int year) noexcept {
  return (year % 4 == 0 && year % 100 != 0) || year % 400 == 0;
}

[[nodiscard]] int days_in_month(int year, int month) noexcept {
  constexpr std::array<int, 12> lengths{31, 28, 31, 30, 31, 30,
                                        31, 31, 30, 31, 30, 31};
  return month == 2 && leap_year(year)
             ? 29
             : lengths[static_cast<std::size_t>(month - 1)];
}

[[nodiscard]] std::int64_t days_from_civil(int year, unsigned month,
                                           unsigned day) noexcept {
  year -= month <= 2U;
  const int era = (year >= 0 ? year : year - 399) / 400;
  const unsigned year_of_era = static_cast<unsigned>(year - era * 400);
  const unsigned shifted_month = month > 2U ? month - 3U : month + 9U;
  const unsigned day_of_year = (153U * shifted_month + 2U) / 5U + day - 1U;
  const unsigned day_of_era =
      year_of_era * 365U + year_of_era / 4U - year_of_era / 100U + day_of_year;
  return static_cast<std::int64_t>(era) * 146097 +
         static_cast<std::int64_t>(day_of_era) - 719468;
}

[[nodiscard]] bool
parse_timestamp(std::string_view value,
                std::chrono::system_clock::time_point &result) noexcept {
  if (value.size() < 20U || value[4] != '-' || value[7] != '-' ||
      value[10] != 'T' || value[13] != ':' || value[16] != ':') {
    return false;
  }
  int year{}, month{}, day{}, hour{}, minute{}, second{};
  if (!parse_fixed_decimal(value, 0, 4, year) ||
      !parse_fixed_decimal(value, 5, 2, month) ||
      !parse_fixed_decimal(value, 8, 2, day) ||
      !parse_fixed_decimal(value, 11, 2, hour) ||
      !parse_fixed_decimal(value, 14, 2, minute) ||
      !parse_fixed_decimal(value, 17, 2, second) || year < 2000 ||
      year > 2200 || month < 1 || month > 12 || day < 1 ||
      day > days_in_month(year, month) || hour > 23 || minute > 59 ||
      second > 59) {
    return false;
  }

  std::size_t offset = 19;
  std::int64_t nanoseconds = 0;
  if (offset < value.size() && value[offset] == '.') {
    ++offset;
    const std::size_t fractional_start = offset;
    int digits = 0;
    while (offset < value.size() && value[offset] >= '0' &&
           value[offset] <= '9') {
      if (digits >= 9)
        return false;
      nanoseconds = nanoseconds * 10 + (value[offset] - '0');
      ++digits;
      ++offset;
    }
    if (offset == fractional_start)
      return false;
    while (digits++ < 9)
      nanoseconds *= 10;
  }

  if (offset == value.size())
    return false;
  if (value[offset] == 'Z') {
    ++offset;
  } else {
    if (value.size() - offset != 6U || value[offset] != '+' ||
        value[offset + 1] != '0' || value[offset + 2] != '0' ||
        value[offset + 3] != ':' || value[offset + 4] != '0' ||
        value[offset + 5] != '0') {
      return false;
    }
    offset += 6U;
  }
  if (offset != value.size())
    return false;

  const auto seconds_since_epoch =
      days_from_civil(year, static_cast<unsigned>(month),
                      static_cast<unsigned>(day)) *
          86400 +
      hour * 3600 + minute * 60 + second;
  result = std::chrono::system_clock::time_point{
      std::chrono::duration_cast<std::chrono::system_clock::duration>(
          std::chrono::seconds(seconds_since_epoch) +
          std::chrono::nanoseconds(nanoseconds))};
  return true;
}

[[nodiscard]] bool canonical_uuid(std::string_view value) noexcept {
  if (value.size() != 36U)
    return false;
  bool nonzero = false;
  for (std::size_t index = 0; index < value.size(); ++index) {
    if (index == 8U || index == 13U || index == 18U || index == 23U) {
      if (value[index] != '-')
        return false;
      continue;
    }
    const char digit = value[index];
    if (!((digit >= '0' && digit <= '9') || (digit >= 'a' && digit <= 'f')))
      return false;
    nonzero |= digit != '0';
  }
  return nonzero;
}

[[nodiscard]] bool canonical_token_id(std::string_view value) noexcept {
  if (value.size() != 32U)
    return false;
  bool nonzero = false;
  for (const char digit : value) {
    if (!((digit >= '0' && digit <= '9') || (digit >= 'a' && digit <= 'f')))
      return false;
    nonzero |= digit != '0';
  }
  return nonzero;
}

[[nodiscard]] bool read_license_claims(const daxq_vm_blob &payload,
                                       LicenseClaims &claims) noexcept {
  FlatJsonReader reader(payload.data, payload.length);
  if (!reader.consume('{'))
    return false;
  std::uint32_t seen = 0;
  constexpr std::uint32_t kAllFields = (1U << 13U) - 1U;
  std::string_view token_kind;
  std::string_view issuer;
  std::string_view audience;
  std::string_view issued_at;
  std::string_view expires_at;
  std::string_view access_valid_until;
  std::int64_t schema_version{};

  for (;;) {
    std::string_view property;
    if (!reader.read_string(property) || !reader.consume(':'))
      return false;
    auto read_string_field = [&](std::uint32_t bit,
                                 std::string_view &target) noexcept {
      if ((seen & bit) != 0U || !reader.read_string(target))
        return false;
      seen |= bit;
      return true;
    };
    auto read_integer_field = [&](std::uint32_t bit,
                                  std::int64_t &target) noexcept {
      if ((seen & bit) != 0U || !reader.read_integer(target))
        return false;
      seen |= bit;
      return true;
    };

    bool valid = false;
    if (property == "schema_version")
      valid = read_integer_field(1U << 0U, schema_version);
    else if (property == "token_kind")
      valid = read_string_field(1U << 1U, token_kind);
    else if (property == "token_id")
      valid = read_string_field(1U << 2U, claims.token_id);
    else if (property == "license_id")
      valid = read_string_field(1U << 3U, claims.license_id);
    else if (property == "release_id")
      valid = read_string_field(1U << 4U, claims.release_id);
    else if (property == "account_id")
      valid = read_string_field(1U << 5U, claims.account_id);
    else if (property == "device_id")
      valid = read_string_field(1U << 6U, claims.device_id);
    else if (property == "issuer")
      valid = read_string_field(1U << 7U, issuer);
    else if (property == "audience")
      valid = read_string_field(1U << 8U, audience);
    else if (property == "issued_at")
      valid = read_string_field(1U << 9U, issued_at);
    else if (property == "expires_at")
      valid = read_string_field(1U << 10U, expires_at);
    else if (property == "access_valid_until") {
      valid = read_string_field(1U << 11U, access_valid_until);
    } else if (property == "revocation_seq") {
      valid = read_integer_field(1U << 12U, claims.revocation_sequence);
    }
    if (!valid)
      return false;
    if (reader.consume('}'))
      break;
    if (!reader.consume(','))
      return false;
  }

  if (!reader.finished() || seen != kAllFields || schema_version != 1 ||
      !canonical_token_id(claims.token_id) ||
      !canonical_uuid(claims.license_id) ||
      !canonical_uuid(claims.release_id) ||
      !canonical_uuid(claims.account_id) || !canonical_uuid(claims.device_id) ||
      claims.revocation_sequence < 0 || issuer != kLicenseIssuer ||
      audience != kLicenseAudience ||
      !parse_timestamp(issued_at, claims.issued_at) ||
      !parse_timestamp(expires_at, claims.expires_at) ||
      !parse_timestamp(access_valid_until, claims.access_valid_until)) {
    return false;
  }
  if (token_kind == "run_token")
    claims.kind = LicenseTokenKind::Run;
  else if (token_kind == "offline_lease")
    claims.kind = LicenseTokenKind::Offline;
  else
    return false;

  const auto now = std::chrono::system_clock::now();
  return claims.issued_at <= now + kMaximumIssuedAtSkew &&
         claims.expires_at > now && claims.expires_at > claims.issued_at &&
         claims.expires_at - claims.issued_at <= kMaximumLicenseLifetime &&
         claims.access_valid_until >= claims.expires_at &&
         claims.access_valid_until > now;
}

[[nodiscard]] bool verify_self_image() noexcept {
#if !defined(DAXQ_VM_HARDENED_RELEASE)
  return true;
#elif defined(_WIN32)
  HMODULE module{};
  if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                              GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                          reinterpret_cast<LPCWSTR>(&module_anchor), &module) ||
      module == nullptr) {
    return false;
  }

  std::vector<wchar_t> path(32768U);
  const DWORD length =
      GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
  if (length == 0 || length >= path.size() ||
      GetFileAttributesW(path.data()) == INVALID_FILE_ATTRIBUTES) {
    secure_erase(path);
    return false;
  }

  WINTRUST_FILE_INFO file{};
  file.cbStruct = sizeof(file);
  file.pcwszFilePath = path.data();
  WINTRUST_DATA trust{};
  trust.cbStruct = sizeof(trust);
  trust.dwUIChoice = WTD_UI_NONE;
  trust.fdwRevocationChecks = WTD_REVOKE_NONE;
  trust.dwUnionChoice = WTD_CHOICE_FILE;
  trust.pFile = &file;
  trust.dwStateAction = WTD_STATEACTION_VERIFY;
  trust.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE;
  GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
  const LONG status = WinVerifyTrust(nullptr, &action, &trust);
  trust.dwStateAction = WTD_STATEACTION_CLOSE;
  (void)WinVerifyTrust(nullptr, &action, &trust);
  secure_erase(path);
  return status == ERROR_SUCCESS;
#elif defined(__APPLE__)
  Dl_info module_info{};
  if (dladdr(reinterpret_cast<const void *>(&module_anchor), &module_info) == 0 ||
      module_info.dli_fname == nullptr) {
    return false;
  }

  const auto path_length = std::strlen(module_info.dli_fname);
  CFURLRef module_url = CFURLCreateFromFileSystemRepresentation(
      kCFAllocatorDefault,
      reinterpret_cast<const UInt8 *>(module_info.dli_fname), path_length,
      false);
  if (module_url == nullptr)
    return false;

  SecStaticCodeRef static_code = nullptr;
  CFErrorRef error = nullptr;
  const OSStatus create_status =
      SecStaticCodeCreateWithPath(module_url, kSecCSDefaultFlags, &static_code);
  OSStatus verify_status = create_status;
  if (create_status == errSecSuccess && static_code != nullptr) {
    verify_status = SecStaticCodeCheckValidityWithErrors(
        static_code, kSecCSStrictValidate, nullptr, &error);
  }
  if (error != nullptr)
    CFRelease(error);
  if (static_code != nullptr)
    CFRelease(static_code);
  CFRelease(module_url);
  return create_status == errSecSuccess && verify_status == errSecSuccess;
#else
  return false;
#endif
}

class FpEnvironment final {
public:
  FpEnvironment() noexcept {
#if defined(DAXQ_VM_X86_FP)
    saved_mxcsr_ = _mm_getcsr();
#endif
    saved_ = std::fegetenv(&saved_environment_) == 0;
    valid_ = saved_ && enforce();
  }

  FpEnvironment(const FpEnvironment &) = delete;
  FpEnvironment &operator=(const FpEnvironment &) = delete;

  ~FpEnvironment() {
    if (!saved_)
      return;
    (void)std::fesetenv(&saved_environment_);
#if defined(DAXQ_VM_X86_FP)
    _mm_setcsr(saved_mxcsr_);
#endif
  }

  [[nodiscard]] static bool enforce() noexcept {
#if defined(DAXQ_VM_X86_FP)
    // Make the SSE environment non-stop before touching the portable fenv. A
    // caller may arrive with an exception both pending and unmasked, in which
    // case feholdexcept itself is not a safe first operation on every CRT.
    set_canonical_mxcsr();
#endif
    std::fenv_t discarded_environment{};
    if (std::feholdexcept(&discarded_environment) != 0)
      return false;
    if (std::fesetround(FE_TONEAREST) != 0)
      return false;
#if defined(DAXQ_VM_X86_FP)
    // Some fenv implementations also update MXCSR, so apply the exact ABI state
    // again.
    set_canonical_mxcsr();
#endif
    return std::feclearexcept(FE_ALL_EXCEPT) == 0;
  }

  [[nodiscard]] bool valid() const noexcept { return valid_; }

private:
#if defined(DAXQ_VM_X86_FP)
  static void set_canonical_mxcsr() noexcept {
    // Clear pending flags (0..5), disable DAZ/FTZ (6/15), mask all exceptions
    // (7..12), and force round-to-nearest (13..14).
    constexpr unsigned kExceptionFlags = 0x003fU;
    constexpr unsigned kDenormalsAreZero = 0x0040U;
    constexpr unsigned kExceptionMasks = 0x1f80U;
    constexpr unsigned kRoundingMode = 0x6000U;
    constexpr unsigned kFlushToZero = 0x8000U;
    unsigned mxcsr = _mm_getcsr();
    mxcsr &=
        ~(kExceptionFlags | kDenormalsAreZero | kRoundingMode | kFlushToZero);
    mxcsr |= kExceptionMasks;
    _mm_setcsr(mxcsr);
  }
#endif

  std::fenv_t saved_environment_{};
#if defined(DAXQ_VM_X86_FP)
  unsigned saved_mxcsr_{};
#endif
  bool saved_{};
  bool valid_{};
};

enum class Type : std::uint8_t {
  Unknown = 0,
  I64 = 1,
  F64 = 2,
  Bool = 3,
  BufferI64 = 4,
  BufferF64 = 5,
  BufferBool = 6,
};

enum class Op : std::uint8_t {
  PushF64 = 0x01,
  PushI64 = 0x02,
  PushBool = 0x03,
  LoadLocal = 0x04,
  StoreLocal = 0x05,
  LoadArgument = 0x06,
  Add = 0x07,
  Subtract = 0x08,
  Multiply = 0x09,
  Divide = 0x0a,
  Modulo = 0x0b,
  Negate = 0x0c,
  CompareEqual = 0x0d,
  CompareNotEqual = 0x0e,
  CompareLess = 0x0f,
  CompareLessEqual = 0x10,
  CompareGreater = 0x11,
  CompareGreaterEqual = 0x12,
  BooleanAnd = 0x13,
  BooleanOr = 0x14,
  BooleanNot = 0x15,
  IntegerToFloat = 0x16,
  FloatToInteger = 0x17,
  Branch = 0x18,
  BranchTrue = 0x19,
  BranchFalse = 0x1a,
  NewBuffer = 0x1b,
  LoadElement = 0x1c,
  StoreElement = 0x1d,
  Length = 0x1e,
  LoadState = 0x1f,
  StoreState = 0x20,
  CallHost = 0x21,
  Return = 0x22,
};

struct Value {
  Type type{Type::Unknown};
  std::uint64_t bits{};

  static Value i64(std::int64_t value) noexcept {
    return {Type::I64, std::bit_cast<std::uint64_t>(value)};
  }

  static Value f64(double value) noexcept {
    return {Type::F64,
            std::bit_cast<std::uint64_t>(value == 0.0 ? 0.0 : value)};
  }

  static Value boolean(bool value) noexcept {
    return {Type::Bool, value ? 1U : 0U};
  }

  static Value buffer(Type type, std::uint64_t index) noexcept {
    return {type, index};
  }

  [[nodiscard]] std::int64_t as_i64() const noexcept {
    return std::bit_cast<std::int64_t>(bits);
  }

  [[nodiscard]] double as_f64() const noexcept {
    return std::bit_cast<double>(bits);
  }

  [[nodiscard]] bool as_bool() const noexcept { return bits != 0; }
};

[[nodiscard]] bool is_scalar(Type type) noexcept {
  return type == Type::I64 || type == Type::F64 || type == Type::Bool;
}

[[nodiscard]] bool is_numeric(Type type) noexcept {
  return type == Type::I64 || type == Type::F64;
}

[[nodiscard]] bool is_buffer(Type type) noexcept {
  return type == Type::BufferI64 || type == Type::BufferF64 ||
         type == Type::BufferBool;
}

[[nodiscard]] Type buffer_type(std::uint8_t element_tag) noexcept {
  switch (element_tag) {
  case 1:
    return Type::BufferI64;
  case 2:
    return Type::BufferF64;
  case 3:
    return Type::BufferBool;
  default:
    return Type::Unknown;
  }
}

[[nodiscard]] Type buffer_element_type(Type type) noexcept {
  switch (type) {
  case Type::BufferI64:
    return Type::I64;
  case Type::BufferF64:
    return Type::F64;
  case Type::BufferBool:
    return Type::Bool;
  default:
    return Type::Unknown;
  }
}

[[nodiscard]] std::size_t logical_element_width(Type buffer) noexcept {
  return buffer == Type::BufferBool ? 1U : 8U;
}

class Reader {
public:
  Reader(const std::uint8_t *data, std::size_t length) noexcept
      : data_(data), length_(length) {}

  [[nodiscard]] bool read_u8(std::uint8_t &value) noexcept {
    if (remaining() < 1)
      return false;
    value = data_[position_++];
    return true;
  }

  [[nodiscard]] bool read_u16(std::uint16_t &value) noexcept {
    if (remaining() < 2)
      return false;
    value = static_cast<std::uint16_t>(data_[position_]) |
            (static_cast<std::uint16_t>(data_[position_ + 1]) << 8U);
    position_ += 2;
    return true;
  }

  [[nodiscard]] bool read_u32(std::uint32_t &value) noexcept {
    if (remaining() < 4)
      return false;
    value = static_cast<std::uint32_t>(data_[position_]) |
            (static_cast<std::uint32_t>(data_[position_ + 1]) << 8U) |
            (static_cast<std::uint32_t>(data_[position_ + 2]) << 16U) |
            (static_cast<std::uint32_t>(data_[position_ + 3]) << 24U);
    position_ += 4;
    return true;
  }

  [[nodiscard]] bool read_u64(std::uint64_t &value) noexcept {
    if (remaining() < 8)
      return false;
    value = 0;
    for (std::size_t index = 0; index < 8; ++index) {
      value |= static_cast<std::uint64_t>(data_[position_ + index])
               << (index * 8U);
    }
    position_ += 8;
    return true;
  }

  [[nodiscard]] std::size_t remaining() const noexcept {
    return length_ - position_;
  }
  [[nodiscard]] std::size_t position() const noexcept { return position_; }
  [[nodiscard]] bool at_end() const noexcept { return position_ == length_; }

private:
  const std::uint8_t *data_{};
  std::size_t length_{};
  std::size_t position_{};
};

[[nodiscard]] std::uint16_t read_u16_at(const std::vector<std::uint8_t> &bytes,
                                        std::size_t offset) noexcept {
  return static_cast<std::uint16_t>(bytes[offset]) |
         (static_cast<std::uint16_t>(bytes[offset + 1]) << 8U);
}

[[nodiscard]] std::uint32_t read_u32_at(const std::vector<std::uint8_t> &bytes,
                                        std::size_t offset) noexcept {
  return static_cast<std::uint32_t>(bytes[offset]) |
         (static_cast<std::uint32_t>(bytes[offset + 1]) << 8U) |
         (static_cast<std::uint32_t>(bytes[offset + 2]) << 16U) |
         (static_cast<std::uint32_t>(bytes[offset + 3]) << 24U);
}

struct Instruction {
  Op op{};
  std::uint32_t relative_offset{};
  std::uint32_t next_offset{};
  std::uint16_t operand_u16{};
  std::uint8_t operand_u8{};
  std::int32_t branch_delta{};
  std::uint16_t host_id{};
  std::int32_t branch_target_index{-1};
};

struct Entrypoint {
  bool present{};
  std::uint8_t id{};
  std::uint8_t argument_count{};
  std::uint16_t local_count{};
  std::uint32_t code_offset{};
  std::uint32_t code_length{};
  std::vector<Instruction> instructions;
  std::vector<std::int32_t> boundary_to_instruction;
};

struct AbstractValue {
  Type type{Type::Unknown};
  bool known{};
  std::uint64_t bits{};

  static AbstractValue unknown(Type type) noexcept { return {type, false, 0}; }

  static AbstractValue constant(Value value) noexcept {
    return {value.type, true, value.bits};
  }

  [[nodiscard]] Value value() const noexcept { return {type, bits}; }
};

struct AbstractState {
  std::vector<AbstractValue> stack;
  std::array<AbstractValue, kMaxLocals> locals{};
  std::array<std::uint8_t, kMaxLocals> initialized{};

  AbstractState() { initialized.fill(0); }
};

struct Buffer {
  Type type{Type::Unknown};
  std::uint16_t length{};
  std::array<std::uint64_t, kMaxBufferElements> elements{};
};

struct EmitRecord {
  std::int64_t kind{};
  double strength{};
  std::int64_t note_id{};
};

struct LogRecord {
  std::int64_t message_id{};
  double value{};
};

struct Frame {
  std::array<Value, kPhysicalStack> stack{};
  std::size_t stack_size{};
  std::size_t max_stack_depth{};
  std::array<Value, kMaxLocals> locals{};
  std::array<std::uint8_t, kMaxLocals> local_initialized{};
  std::array<Value, 5> arguments{};
  std::array<Buffer, kMaxBuffers> buffers{};
  std::size_t buffer_count{};
  std::size_t aggregate_buffer_bytes{};
  std::array<Value, kMaxStateSlots> staged_state{};
  std::array<EmitRecord, kMaxEmits> emits{};
  std::size_t emit_count{};
  std::array<LogRecord, kMaxLogs> logs{};
  std::size_t log_count{};

  void reset(const std::array<Value, kMaxStateSlots> &state,
             std::size_t state_count) noexcept {
    stack_size = 0;
    max_stack_depth = 0;
    local_initialized.fill(0);
    buffer_count = 0;
    aggregate_buffer_bytes = 0;
    emit_count = 0;
    log_count = 0;
    std::copy_n(state.begin(), state_count, staged_state.begin());
  }
};

struct Budget {
  std::uint32_t instructions{};
  std::size_t stack{};
  std::chrono::milliseconds timeout{};
};

[[nodiscard]] Budget budget_for(std::uint8_t entrypoint) noexcept {
  switch (entrypoint) {
  case kEntrypointInitialize:
    return {1'000'000U, 512U, std::chrono::milliseconds(250)};
  case kEntrypointOnBar:
    return {100'000U, 256U, std::chrono::milliseconds(25)};
  case kEntrypointOnTick:
    return {25'000U, 128U, std::chrono::milliseconds(5)};
  default:
    return {};
  }
}

[[nodiscard]] std::uint8_t
required_argument_count(std::uint8_t entrypoint) noexcept {
  switch (entrypoint) {
  case kEntrypointInitialize:
    return 0;
  case kEntrypointOnBar:
    return 1;
  case kEntrypointOnTick:
    return 5;
  default:
    return std::numeric_limits<std::uint8_t>::max();
  }
}

[[nodiscard]] Type argument_type(std::uint8_t entrypoint,
                                 std::size_t index) noexcept {
  if (entrypoint == kEntrypointOnBar)
    return index == 0 ? Type::I64 : Type::Unknown;
  if (entrypoint == kEntrypointOnTick)
    return index == 0 ? Type::I64 : Type::F64;
  return Type::Unknown;
}

[[nodiscard]] bool checked_add(std::int64_t left, std::int64_t right,
                               std::int64_t &result) noexcept {
  if ((right > 0 && left > std::numeric_limits<std::int64_t>::max() - right) ||
      (right < 0 && left < std::numeric_limits<std::int64_t>::min() - right)) {
    return false;
  }
  result = left + right;
  return true;
}

[[nodiscard]] bool checked_subtract(std::int64_t left, std::int64_t right,
                                    std::int64_t &result) noexcept {
  if ((right < 0 && left > std::numeric_limits<std::int64_t>::max() + right) ||
      (right > 0 && left < std::numeric_limits<std::int64_t>::min() + right)) {
    return false;
  }
  result = left - right;
  return true;
}

[[nodiscard]] bool checked_multiply(std::int64_t left, std::int64_t right,
                                    std::int64_t &result) noexcept {
  if (left == 0 || right == 0) {
    result = 0;
    return true;
  }
  if ((left == -1 && right == std::numeric_limits<std::int64_t>::min()) ||
      (right == -1 && left == std::numeric_limits<std::int64_t>::min())) {
    return false;
  }
  if (left > 0) {
    if ((right > 0 &&
         left > std::numeric_limits<std::int64_t>::max() / right) ||
        (right < 0 &&
         right < std::numeric_limits<std::int64_t>::min() / left)) {
      return false;
    }
  } else {
    if ((right > 0 &&
         left < std::numeric_limits<std::int64_t>::min() / right) ||
        (right < 0 &&
         right < std::numeric_limits<std::int64_t>::max() / left)) {
      return false;
    }
  }
  result = left * right;
  return true;
}

[[nodiscard]] bool normalize_finite(double input, double &output) noexcept {
  if (!std::isfinite(input))
    return false;
  output = input == 0.0 ? 0.0 : input;
  return true;
}

class Vm {
public:
  ~Vm() {
    secure_erase(bytecode_);
    secure_erase(constants_);
    for (auto &entrypoint : entrypoints_) {
      secure_erase(entrypoint.instructions);
      secure_erase(entrypoint.boundary_to_instruction);
      entrypoint.present = false;
      entrypoint.id = 0;
      entrypoint.argument_count = 0;
      entrypoint.local_count = 0;
      entrypoint.code_offset = 0;
      entrypoint.code_length = 0;
    }
    secure_zero(opcode_decode_.data(), sizeof(opcode_decode_));
    secure_zero(host_decode_.data(), sizeof(host_decode_));
    secure_zero(state_types_.data(), sizeof(state_types_));
    secure_zero(state_.data(), sizeof(state_));
    secure_zero(&callbacks_, sizeof(callbacks_));
    secure_zero(&frame_, sizeof(frame_));
    {
      const std::lock_guard lock(license_mutex_);
      secure_zero(license_id_.data(), license_id_.size());
      secure_zero(release_id_.data(), release_id_.size());
      secure_zero(account_id_.data(), account_id_.size());
      secure_zero(device_id_.data(), device_id_.size());
      authorized_until_system_ = {};
      authorized_until_steady_ = {};
      revocation_sequence_ = -1;
      enforcement_started_ = false;
      evidence_applied_ = false;
      run_token_applied_ = false;
      license_binding_set_ = false;
      license_revoked_ = true;
    }
  }

  Fault load(const daxq_vm_create_options &options) {
    FpEnvironment fp_environment;
    if (!fp_environment.valid())
      return DAXQ_FAULT_INTERNAL;
    bytecode_.assign(options.bytecode.data,
                     options.bytecode.data + options.bytecode.length);

    if (const auto fault = parse_constant_pool(options.constant_pool);
        fault != DAXQ_FAULT_OK)
      return fault;
    if (const auto fault = parse_opcode_map(options.opcode_map);
        fault != DAXQ_FAULT_OK)
      return fault;
    if (const auto fault = parse_host_map(options.host_map);
        fault != DAXQ_FAULT_OK)
      return fault;
    if (const auto fault = parse_entrypoints(options.entrypoints);
        fault != DAXQ_FAULT_OK)
      return fault;

    for (auto &entrypoint : entrypoints_) {
      if (!entrypoint.present)
        continue;
      if (const auto fault = decode(entrypoint); fault != DAXQ_FAULT_OK)
        return fault;
    }
    if (const auto fault = verify_constant_first_use(); fault != DAXQ_FAULT_OK)
      return fault;
    for (const auto &entrypoint : entrypoints_) {
      if (!entrypoint.present)
        continue;
      if (const auto fault = verify(entrypoint); fault != DAXQ_FAULT_OK)
        return fault;
    }
    secure_erase(bytecode_);
    std::vector<std::uint8_t>{}.swap(bytecode_);
    secure_zero(opcode_decode_.data(), sizeof(opcode_decode_));
    secure_zero(host_decode_.data(), sizeof(host_decode_));
    for (auto &entrypoint : entrypoints_) {
      secure_erase(entrypoint.boundary_to_instruction);
      std::vector<std::int32_t>{}.swap(entrypoint.boundary_to_instruction);
    }
    return DAXQ_FAULT_OK;
  }

  Fault
  apply_license_evidence(const daxq_vm_license_evidence &evidence) noexcept {
    {
      const std::lock_guard lock(license_mutex_);
      enforcement_started_ = true;
      if (license_revoked_)
        return DAXQ_FAULT_INVALID_LIFECYCLE;
    }
    auto reject = [this]() noexcept {
      const std::lock_guard lock(license_mutex_);
      evidence_applied_ = false;
      return DAXQ_FAULT_VERIFICATION;
    };
    if (!verify_self_image() || !verify_es256(evidence))
      return reject();

    LicenseClaims claims{};
    if (!read_license_claims(evidence.payload, claims))
      return reject();
    const auto system_now = std::chrono::system_clock::now();
    const auto steady_now = std::chrono::steady_clock::now();
    const auto remaining = claims.expires_at - system_now;
    if (remaining <= std::chrono::system_clock::duration::zero() ||
        remaining > kMaximumLicenseLifetime) {
      return reject();
    }

    const std::lock_guard lock(license_mutex_);
    if (license_revoked_)
      return DAXQ_FAULT_INVALID_LIFECYCLE;
    if (license_binding_set_) {
      if (!binding_matches(license_id_, claims.license_id) ||
          !binding_matches(release_id_, claims.release_id) ||
          !binding_matches(account_id_, claims.account_id) ||
          !binding_matches(device_id_, claims.device_id)) {
        evidence_applied_ = false;
        return DAXQ_FAULT_VERIFICATION;
      }
    } else {
      if (claims.kind != LicenseTokenKind::Run) {
        evidence_applied_ = false;
        return DAXQ_FAULT_VERIFICATION;
      }
      copy_binding(license_id_, claims.license_id);
      copy_binding(release_id_, claims.release_id);
      copy_binding(account_id_, claims.account_id);
      copy_binding(device_id_, claims.device_id);
      license_binding_set_ = true;
    }
    if (claims.revocation_sequence < revocation_sequence_ ||
        (claims.kind == LicenseTokenKind::Offline && !run_token_applied_)) {
      evidence_applied_ = false;
      return DAXQ_FAULT_VERIFICATION;
    }

    revocation_sequence_ = claims.revocation_sequence;
    run_token_applied_ |= claims.kind == LicenseTokenKind::Run;
    evidence_applied_ = true;
    if (claims.expires_at > authorized_until_system_) {
      authorized_until_system_ = claims.expires_at;
      authorized_until_steady_ =
          steady_now +
          std::chrono::duration_cast<std::chrono::steady_clock::duration>(
              remaining);
    }
    return DAXQ_FAULT_OK;
  }

  Fault revoke_license() noexcept {
    const std::lock_guard lock(license_mutex_);
    enforcement_started_ = true;
    license_revoked_ = true;
    evidence_applied_ = false;
    authorized_until_system_ = {};
    authorized_until_steady_ = {};
    return DAXQ_FAULT_OK;
  }

  void reject_license_evidence_attempt() noexcept {
    const std::lock_guard lock(license_mutex_);
    enforcement_started_ = true;
    evidence_applied_ = false;
  }

  Fault set_host_callbacks(const daxq_vm_host_callbacks &callbacks) noexcept {
    if (invoking_.test_and_set(std::memory_order_acq_rel))
      return DAXQ_FAULT_REENTRANT;
    struct ClearFlag {
      std::atomic_flag &flag;
      ~ClearFlag() { flag.clear(std::memory_order_release); }
    } clear{invoking_};
    if (callbacks.state == nullptr || callbacks.bar == nullptr ||
        callbacks.ind == nullptr || callbacks.param == nullptr ||
        callbacks.emit == nullptr || callbacks.tindex == nullptr ||
        callbacks.rng == nullptr || callbacks.log == nullptr) {
      return DAXQ_FAULT_INVALID_ARGUMENT;
    }
    callbacks_ = callbacks;
    callbacks_set_ = true;
    return DAXQ_FAULT_OK;
  }

  Fault invoke(const daxq_vm_invoke_options &options,
               daxq_vm_invoke_result &result) noexcept {
    if (invoking_.test_and_set(std::memory_order_acq_rel))
      return DAXQ_FAULT_REENTRANT;
    struct ClearFlag {
      std::atomic_flag &flag;
      ~ClearFlag() { flag.clear(std::memory_order_release); }
    } clear{invoking_};

    result.executed_instructions = 0;
    result.max_stack_depth = 0;

    FpEnvironment fp_environment;
    if (!fp_environment.valid())
      return DAXQ_FAULT_INTERNAL;

    if (!license_allows_dispatch())
      return DAXQ_FAULT_INVALID_LIFECYCLE;
    if (!callbacks_set_)
      return DAXQ_FAULT_INVALID_LIFECYCLE;
    if (options.entrypoint_id >= kEntrypointCount ||
        !entrypoints_[options.entrypoint_id].present) {
      return DAXQ_FAULT_ENTRYPOINT_NOT_FOUND;
    }
    if (options.entrypoint_id == kEntrypointInitialize &&
        initialize_succeeded_) {
      return DAXQ_FAULT_INVALID_LIFECYCLE;
    }

    const auto &entrypoint = entrypoints_[options.entrypoint_id];
    if (options.arg_count != entrypoint.argument_count ||
        (options.arg_count != 0 && options.args == nullptr)) {
      return DAXQ_FAULT_INVALID_ARGUMENT;
    }

    frame_.reset(state_, state_count_);
    for (std::size_t index = 0; index < options.arg_count; ++index) {
      const auto &input = options.args[index];
      const Type expected = argument_type(entrypoint.id, index);
      if (input.tag != static_cast<std::uint8_t>(expected))
        return DAXQ_FAULT_INVALID_ARGUMENT;
      if (!std::all_of(std::begin(input.reserved), std::end(input.reserved),
                       [](std::uint8_t value) { return value == 0; })) {
        return DAXQ_FAULT_INVALID_ARGUMENT;
      }
      if (expected == Type::I64) {
        frame_.arguments[index] = Value::i64(input.data.i64);
      } else if (expected == Type::F64) {
        double normalized{};
        if (!normalize_finite(input.data.f64, normalized))
          return DAXQ_FAULT_INVALID_ARGUMENT;
        frame_.arguments[index] = Value::f64(normalized);
      } else {
        return DAXQ_FAULT_INVALID_ARGUMENT;
      }
    }

    const auto budget = budget_for(entrypoint.id);
    const auto deadline = std::chrono::steady_clock::now() + budget.timeout;
    std::int32_t instruction_index = 0;
    Fault fault = DAXQ_FAULT_OK;
    bool returned = false;

    while (!returned) {
      if (std::chrono::steady_clock::now() >= deadline) {
        fault = DAXQ_FAULT_TIMEOUT;
        break;
      }
      if (result.executed_instructions >= budget.instructions) {
        fault = DAXQ_FAULT_INSTRUCTION_BUDGET;
        break;
      }
      if (instruction_index < 0 ||
          static_cast<std::size_t>(instruction_index) >=
              entrypoint.instructions.size()) {
        fault = DAXQ_FAULT_INTERNAL;
        break;
      }

      ++result.executed_instructions;
      const Instruction &instruction =
          entrypoint.instructions[static_cast<std::size_t>(instruction_index)];
      std::int32_t next_index = instruction_index + 1;
      fault = execute_instruction(entrypoint, instruction, budget.stack,
                                  next_index, returned);
      result.max_stack_depth =
          static_cast<std::uint32_t>(frame_.max_stack_depth);
      if (fault != DAXQ_FAULT_OK)
        break;
      instruction_index = next_index;
    }

    if (fault != DAXQ_FAULT_OK)
      return fault;
    if (std::chrono::steady_clock::now() >= deadline)
      return DAXQ_FAULT_TIMEOUT;
    if (!license_allows_dispatch())
      return DAXQ_FAULT_INVALID_LIFECYCLE;

    for (std::size_t index = 0; index < frame_.emit_count; ++index) {
      if (!license_allows_dispatch())
        return DAXQ_FAULT_INVALID_LIFECYCLE;
      const auto &record = frame_.emits[index];
      const auto callback_fault = callbacks_.emit(
          callbacks_.context, record.kind, record.strength, record.note_id);
      if (!FpEnvironment::enforce())
        return DAXQ_FAULT_INTERNAL;
      if (callback_fault != 0) {
        return DAXQ_FAULT_HOST;
      }
      if (std::chrono::steady_clock::now() >= deadline)
        return DAXQ_FAULT_TIMEOUT;
    }
    for (std::size_t index = 0; index < frame_.log_count; ++index) {
      if (!license_allows_dispatch())
        return DAXQ_FAULT_INVALID_LIFECYCLE;
      const auto &record = frame_.logs[index];
      const auto callback_fault =
          callbacks_.log(callbacks_.context, record.message_id, record.value);
      if (!FpEnvironment::enforce())
        return DAXQ_FAULT_INTERNAL;
      if (callback_fault != 0) {
        return DAXQ_FAULT_HOST;
      }
      if (std::chrono::steady_clock::now() >= deadline)
        return DAXQ_FAULT_TIMEOUT;
    }

    if (!license_allows_dispatch())
      return DAXQ_FAULT_INVALID_LIFECYCLE;
    std::copy_n(frame_.staged_state.begin(), state_count_, state_.begin());
    if (entrypoint.id == kEntrypointInitialize)
      initialize_succeeded_ = true;
    return DAXQ_FAULT_OK;
  }

private:
  static bool binding_matches(const std::array<char, 36> &stored,
                              std::string_view candidate) noexcept {
    return candidate.size() == stored.size() &&
           fixed_equal(reinterpret_cast<const std::uint8_t *>(stored.data()),
                       reinterpret_cast<const std::uint8_t *>(candidate.data()),
                       stored.size());
  }

  static void copy_binding(std::array<char, 36> &destination,
                           std::string_view source) noexcept {
    std::memcpy(destination.data(), source.data(), destination.size());
  }

  [[nodiscard]] bool license_allows_dispatch() noexcept {
    const std::lock_guard lock(license_mutex_);
#if !defined(DAXQ_VM_HARDENED_RELEASE)
    if (!enforcement_started_)
      return true;
#endif
    if (!evidence_applied_ || license_revoked_)
      return false;
    return std::chrono::system_clock::now() < authorized_until_system_ &&
           std::chrono::steady_clock::now() < authorized_until_steady_;
  }

  Fault parse_constant_pool(const daxq_vm_blob &blob) {
    Reader reader(blob.data, blob.length);
    std::uint16_t count{};
    if (!reader.read_u16(count))
      return DAXQ_FAULT_INVALID_FORMAT;
    if (reader.remaining() != static_cast<std::size_t>(count) * 9U)
      return DAXQ_FAULT_INVALID_FORMAT;

    constants_.clear();
    constants_.reserve(count);
    for (std::uint16_t index = 0; index < count; ++index) {
      std::uint8_t tag{};
      std::uint64_t bits{};
      if (!reader.read_u8(tag) || !reader.read_u64(bits))
        return DAXQ_FAULT_INVALID_FORMAT;
      if (tag == static_cast<std::uint8_t>(Type::I64)) {
        constants_.push_back(Value::i64(std::bit_cast<std::int64_t>(bits)));
      } else if (tag == static_cast<std::uint8_t>(Type::F64)) {
        double normalized{};
        if (!normalize_finite(std::bit_cast<double>(bits), normalized))
          return DAXQ_FAULT_INVALID_FORMAT;
        constants_.push_back(Value::f64(normalized));
      } else {
        return DAXQ_FAULT_INVALID_FORMAT;
      }
    }
    return reader.at_end() ? DAXQ_FAULT_OK : DAXQ_FAULT_INVALID_FORMAT;
  }

  Fault parse_opcode_map(const daxq_vm_blob &blob) noexcept {
    opcode_decode_.fill(0);
    std::array<std::uint8_t, 0x23> canonical_seen{};
    Reader reader(blob.data, blob.length);
    std::uint16_t count{};
    if (!reader.read_u16(count) || count > 255 ||
        reader.remaining() != static_cast<std::size_t>(count) * 2U) {
      return DAXQ_FAULT_INVALID_FORMAT;
    }
    std::uint16_t previous_encoded{};
    for (std::uint16_t index = 0; index < count; ++index) {
      std::uint8_t encoded{};
      std::uint8_t canonical{};
      if (!reader.read_u8(encoded) || !reader.read_u8(canonical) ||
          encoded == 0 || encoded <= previous_encoded || canonical == 0 ||
          canonical > 0x22 || canonical_seen[canonical] != 0) {
        return DAXQ_FAULT_INVALID_FORMAT;
      }
      previous_encoded = encoded;
      canonical_seen[canonical] = 1;
      opcode_decode_[encoded] = canonical;
    }
    return reader.at_end() ? DAXQ_FAULT_OK : DAXQ_FAULT_INVALID_FORMAT;
  }

  Fault parse_host_map(const daxq_vm_blob &blob) noexcept {
    host_decode_.fill(0);
    std::array<std::uint8_t, 9> canonical_seen{};
    Reader reader(blob.data, blob.length);
    std::uint16_t count{};
    if (!reader.read_u16(count) || count > 8 ||
        reader.remaining() != static_cast<std::size_t>(count) * 4U) {
      return DAXQ_FAULT_INVALID_FORMAT;
    }
    std::uint16_t previous_encoded{};
    for (std::uint16_t index = 0; index < count; ++index) {
      std::uint16_t encoded{};
      std::uint16_t canonical{};
      if (!reader.read_u16(encoded) || !reader.read_u16(canonical) ||
          encoded == 0 || encoded <= previous_encoded || canonical == 0 ||
          canonical > 8 || canonical_seen[canonical] != 0) {
        return DAXQ_FAULT_INVALID_FORMAT;
      }
      previous_encoded = encoded;
      canonical_seen[canonical] = 1;
      host_decode_[encoded] = canonical;
    }
    return reader.at_end() ? DAXQ_FAULT_OK : DAXQ_FAULT_INVALID_FORMAT;
  }

  Fault parse_entrypoints(const daxq_vm_blob &blob) noexcept {
    Reader reader(blob.data, blob.length);
    std::uint16_t state_count{};
    if (!reader.read_u16(state_count) || state_count > kMaxStateSlots) {
      return DAXQ_FAULT_INVALID_FORMAT;
    }
    state_count_ = state_count;
    for (std::size_t index = 0; index < state_count_; ++index) {
      std::uint8_t tag{};
      if (!reader.read_u8(tag) || tag < 1 || tag > 3)
        return DAXQ_FAULT_INVALID_FORMAT;
      state_types_[index] = static_cast<Type>(tag);
      if (tag == static_cast<std::uint8_t>(Type::I64))
        state_[index] = Value::i64(0);
      else if (tag == static_cast<std::uint8_t>(Type::F64))
        state_[index] = Value::f64(0.0);
      else
        state_[index] = Value::boolean(false);
    }

    std::uint8_t entry_count{};
    if (!reader.read_u8(entry_count) || entry_count == 0 ||
        entry_count > kEntrypointCount ||
        reader.remaining() != static_cast<std::size_t>(entry_count) * 16U) {
      return DAXQ_FAULT_INVALID_FORMAT;
    }

    std::uint8_t previous_id = 0;
    bool first = true;
    for (std::uint8_t index = 0; index < entry_count; ++index) {
      std::uint8_t id{};
      std::uint8_t arg_count{};
      std::uint16_t local_count{};
      std::uint32_t reserved{};
      std::uint32_t code_offset{};
      std::uint32_t code_length{};
      if (!reader.read_u8(id) || !reader.read_u8(arg_count) ||
          !reader.read_u16(local_count) || !reader.read_u32(reserved) ||
          !reader.read_u32(code_offset) || !reader.read_u32(code_length) ||
          id >= kEntrypointCount || (!first && id <= previous_id) ||
          reserved != 0 || code_length == 0 ||
          arg_count != required_argument_count(id) ||
          local_count > kMaxLocals) {
        return DAXQ_FAULT_INVALID_FORMAT;
      }
      first = false;
      previous_id = id;
      auto &entrypoint = entrypoints_[id];
      entrypoint.present = true;
      entrypoint.id = id;
      entrypoint.argument_count = arg_count;
      entrypoint.local_count = local_count;
      entrypoint.code_offset = code_offset;
      entrypoint.code_length = code_length;
    }
    if (!reader.at_end() || (!entrypoints_[kEntrypointOnBar].present &&
                             !entrypoints_[kEntrypointOnTick].present)) {
      return DAXQ_FAULT_INVALID_FORMAT;
    }

    std::vector<const Entrypoint *> by_offset;
    by_offset.reserve(entry_count);
    for (const auto &entrypoint : entrypoints_) {
      if (entrypoint.present)
        by_offset.push_back(&entrypoint);
    }
    std::sort(by_offset.begin(), by_offset.end(),
              [](const Entrypoint *left, const Entrypoint *right) {
                return left->code_offset < right->code_offset;
              });
    std::uint64_t expected_offset = 0;
    for (const auto *entrypoint : by_offset) {
      if (entrypoint->code_offset != expected_offset)
        return DAXQ_FAULT_INVALID_FORMAT;
      expected_offset += entrypoint->code_length;
      if (expected_offset > bytecode_.size())
        return DAXQ_FAULT_INVALID_FORMAT;
    }
    return expected_offset == bytecode_.size() ? DAXQ_FAULT_OK
                                               : DAXQ_FAULT_INVALID_FORMAT;
  }

  Fault decode(Entrypoint &entrypoint) {
    entrypoint.instructions.clear();
    entrypoint.boundary_to_instruction.assign(
        static_cast<std::size_t>(entrypoint.code_length) + 1U, -1);
    std::uint32_t relative = 0;
    while (relative < entrypoint.code_length) {
      const std::size_t absolute =
          static_cast<std::size_t>(entrypoint.code_offset) + relative;
      const std::uint8_t encoded = bytecode_[absolute];
      const std::uint8_t canonical = opcode_decode_[encoded];
      if (encoded == 0 || canonical == 0)
        return DAXQ_FAULT_VERIFICATION;

      Instruction instruction{};
      instruction.op = static_cast<Op>(canonical);
      instruction.relative_offset = relative;
      std::uint32_t operand_bytes = 0;
      switch (instruction.op) {
      case Op::PushF64:
      case Op::PushI64:
      case Op::LoadLocal:
      case Op::StoreLocal:
      case Op::LoadArgument:
      case Op::LoadState:
      case Op::StoreState:
        operand_bytes = 2;
        break;
      case Op::PushBool:
        operand_bytes = 1;
        break;
      case Op::Branch:
      case Op::BranchTrue:
      case Op::BranchFalse:
        operand_bytes = 4;
        break;
      case Op::NewBuffer:
        operand_bytes = 3;
        break;
      case Op::CallHost:
        operand_bytes = 3;
        break;
      default:
        operand_bytes = 0;
        break;
      }
      const std::uint64_t next =
          static_cast<std::uint64_t>(relative) + 1U + operand_bytes;
      if (next > entrypoint.code_length)
        return DAXQ_FAULT_VERIFICATION;
      instruction.next_offset = static_cast<std::uint32_t>(next);
      const std::size_t operand = absolute + 1U;

      switch (instruction.op) {
      case Op::PushF64:
      case Op::PushI64:
      case Op::LoadLocal:
      case Op::StoreLocal:
      case Op::LoadArgument:
      case Op::LoadState:
      case Op::StoreState:
        instruction.operand_u16 = read_u16_at(bytecode_, operand);
        break;
      case Op::PushBool:
        instruction.operand_u8 = bytecode_[operand];
        break;
      case Op::Branch:
      case Op::BranchTrue:
      case Op::BranchFalse:
        instruction.branch_delta =
            std::bit_cast<std::int32_t>(read_u32_at(bytecode_, operand));
        break;
      case Op::NewBuffer:
        instruction.operand_u8 = bytecode_[operand];
        instruction.operand_u16 = read_u16_at(bytecode_, operand + 1U);
        break;
      case Op::CallHost: {
        const auto encoded_host = read_u16_at(bytecode_, operand);
        instruction.host_id = host_decode_[encoded_host];
        instruction.operand_u8 = bytecode_[operand + 2U];
        if (encoded_host == 0 || instruction.host_id == 0 ||
            instruction.host_id == 5) {
          return DAXQ_FAULT_VERIFICATION;
        }
        break;
      }
      default:
        break;
      }

      switch (instruction.op) {
      case Op::PushF64:
        if (instruction.operand_u16 >= constants_.size() ||
            constants_[instruction.operand_u16].type != Type::F64)
          return DAXQ_FAULT_VERIFICATION;
        break;
      case Op::PushI64:
        if (instruction.operand_u16 >= constants_.size() ||
            constants_[instruction.operand_u16].type != Type::I64)
          return DAXQ_FAULT_VERIFICATION;
        break;
      case Op::PushBool:
        if (instruction.operand_u8 > 1)
          return DAXQ_FAULT_VERIFICATION;
        break;
      case Op::LoadLocal:
      case Op::StoreLocal:
        if (instruction.operand_u16 >= entrypoint.local_count)
          return DAXQ_FAULT_VERIFICATION;
        break;
      case Op::LoadArgument:
        if (instruction.operand_u16 >= entrypoint.argument_count)
          return DAXQ_FAULT_VERIFICATION;
        break;
      case Op::NewBuffer:
        if (buffer_type(instruction.operand_u8) == Type::Unknown ||
            instruction.operand_u16 > kMaxBufferElements)
          return DAXQ_FAULT_VERIFICATION;
        break;
      case Op::LoadState:
      case Op::StoreState:
        if (instruction.operand_u16 >= state_count_)
          return DAXQ_FAULT_VERIFICATION;
        break;
      case Op::CallHost:
        if (!valid_host_arity(instruction.host_id, instruction.operand_u8)) {
          return DAXQ_FAULT_VERIFICATION;
        }
        break;
      default:
        break;
      }

      entrypoint.boundary_to_instruction[relative] =
          static_cast<std::int32_t>(entrypoint.instructions.size());
      entrypoint.instructions.push_back(instruction);
      relative = instruction.next_offset;
    }
    entrypoint.boundary_to_instruction[entrypoint.code_length] =
        static_cast<std::int32_t>(entrypoint.instructions.size());

    for (auto &instruction : entrypoint.instructions) {
      if (instruction.op != Op::Branch && instruction.op != Op::BranchTrue &&
          instruction.op != Op::BranchFalse)
        continue;
      const std::int64_t target =
          static_cast<std::int64_t>(instruction.next_offset) +
          static_cast<std::int64_t>(instruction.branch_delta);
      if (target < 0 || target >= entrypoint.code_length ||
          entrypoint.boundary_to_instruction[static_cast<std::size_t>(target)] <
              0) {
        return DAXQ_FAULT_VERIFICATION;
      }
      instruction.branch_target_index =
          entrypoint.boundary_to_instruction[static_cast<std::size_t>(target)];
    }
    return DAXQ_FAULT_OK;
  }

  [[nodiscard]] static bool valid_host_arity(std::uint16_t host,
                                             std::uint8_t arity) noexcept {
    switch (host) {
    case 1:
      return arity == 2;
    case 2:
      return arity == 3;
    case 3:
      return arity == 1;
    case 4:
      return arity == 3;
    case 6:
      return arity == 0;
    case 7:
      return arity == 0;
    case 8:
      return arity == 2;
    default:
      return false;
    }
  }

  Fault verify_constant_first_use() const {
    std::vector<const Entrypoint *> ordered;
    ordered.reserve(kEntrypointCount);
    for (const auto &entrypoint : entrypoints_) {
      if (entrypoint.present)
        ordered.push_back(&entrypoint);
    }
    std::sort(ordered.begin(), ordered.end(),
              [](const Entrypoint *left, const Entrypoint *right) {
                return left->code_offset < right->code_offset;
              });

    std::vector<std::uint8_t> seen(constants_.size(), 0);
    std::size_t next_first_use = 0;
    for (const auto *entrypoint : ordered) {
      for (const auto &instruction : entrypoint->instructions) {
        if (instruction.op != Op::PushI64 && instruction.op != Op::PushF64)
          continue;
        const auto index = static_cast<std::size_t>(instruction.operand_u16);
        if (seen[index] != 0)
          continue;
        if (index != next_first_use)
          return DAXQ_FAULT_VERIFICATION;
        seen[index] = 1;
        ++next_first_use;
      }
    }
    return next_first_use == constants_.size() ? DAXQ_FAULT_OK
                                               : DAXQ_FAULT_VERIFICATION;
  }

  [[nodiscard]] static AbstractValue
  fold_binary(Op op, const AbstractValue &left,
              const AbstractValue &right) noexcept {
    if (!left.known || !right.known)
      return AbstractValue::unknown(left.type);
    const Value a = left.value();
    const Value b = right.value();
    if (left.type == Type::I64) {
      std::int64_t result{};
      switch (op) {
      case Op::Add:
        if (!checked_add(a.as_i64(), b.as_i64(), result)) {
          return AbstractValue::unknown(Type::I64);
        }
        break;
      case Op::Subtract:
        if (!checked_subtract(a.as_i64(), b.as_i64(), result)) {
          return AbstractValue::unknown(Type::I64);
        }
        break;
      case Op::Multiply:
        if (!checked_multiply(a.as_i64(), b.as_i64(), result)) {
          return AbstractValue::unknown(Type::I64);
        }
        break;
      case Op::Divide:
        if (b.as_i64() == 0 ||
            (a.as_i64() == std::numeric_limits<std::int64_t>::min() &&
             b.as_i64() == -1))
          return AbstractValue::unknown(Type::I64);
        result = a.as_i64() / b.as_i64();
        break;
      case Op::Modulo:
        if (b.as_i64() == 0 ||
            (a.as_i64() == std::numeric_limits<std::int64_t>::min() &&
             b.as_i64() == -1))
          return AbstractValue::unknown(Type::I64);
        result = a.as_i64() % b.as_i64();
        break;
      default:
        return AbstractValue::unknown(Type::I64);
      }
      return AbstractValue::constant(Value::i64(result));
    }

    if ((op == Op::Divide || op == Op::Modulo) && b.as_f64() == 0.0) {
      return AbstractValue::unknown(Type::F64);
    }
    double raw{};
    switch (op) {
    case Op::Add: {
      volatile double value = a.as_f64() + b.as_f64();
      raw = value;
      break;
    }
    case Op::Subtract: {
      volatile double value = a.as_f64() - b.as_f64();
      raw = value;
      break;
    }
    case Op::Multiply: {
      volatile double value = a.as_f64() * b.as_f64();
      raw = value;
      break;
    }
    case Op::Divide: {
      volatile double value = a.as_f64() / b.as_f64();
      raw = value;
      break;
    }
    case Op::Modulo: {
      volatile double quotient = a.as_f64() / b.as_f64();
      volatile double truncated = std::trunc(quotient);
      volatile double product = truncated * b.as_f64();
      volatile double value = a.as_f64() - product;
      raw = value;
      break;
    }
    default:
      return AbstractValue::unknown(Type::F64);
    }
    double normalized{};
    return normalize_finite(raw, normalized)
               ? AbstractValue::constant(Value::f64(normalized))
               : AbstractValue::unknown(Type::F64);
  }

  [[nodiscard]] static AbstractValue
  fold_negate(const AbstractValue &input) noexcept {
    if (!input.known)
      return AbstractValue::unknown(input.type);
    const Value value = input.value();
    if (input.type == Type::I64) {
      if (value.as_i64() == std::numeric_limits<std::int64_t>::min()) {
        return AbstractValue::unknown(Type::I64);
      }
      return AbstractValue::constant(Value::i64(-value.as_i64()));
    }
    double normalized{};
    return normalize_finite(-value.as_f64(), normalized)
               ? AbstractValue::constant(Value::f64(normalized))
               : AbstractValue::unknown(Type::F64);
  }

  [[nodiscard]] static AbstractValue
  fold_compare(Op op, const AbstractValue &left,
               const AbstractValue &right) noexcept {
    if (!left.known || !right.known)
      return AbstractValue::unknown(Type::Bool);
    const Value a = left.value();
    const Value b = right.value();
    bool result{};
    if (left.type == Type::I64)
      result = compare_values(op, a.as_i64(), b.as_i64());
    else if (left.type == Type::F64)
      result = compare_values(op, a.as_f64(), b.as_f64());
    else {
      result = a.as_bool() == b.as_bool();
      if (op == Op::CompareNotEqual)
        result = !result;
    }
    return AbstractValue::constant(Value::boolean(result));
  }

  [[nodiscard]] static AbstractValue
  fold_float_to_integer(const AbstractValue &input) noexcept {
    if (!input.known)
      return AbstractValue::unknown(Type::I64);
    const double number = input.value().as_f64();
    constexpr double kPositiveLimit = 9223372036854775808.0;
    constexpr double kNegativeLimit = -9223372036854775808.0;
    if (!std::isfinite(number) || number >= kPositiveLimit ||
        number < kNegativeLimit) {
      return AbstractValue::unknown(Type::I64);
    }
    return AbstractValue::constant(
        Value::i64(static_cast<std::int64_t>(std::trunc(number))));
  }

  Fault verify(const Entrypoint &entrypoint) const {
    if (entrypoint.instructions.empty())
      return DAXQ_FAULT_VERIFICATION;
    std::vector<std::optional<AbstractState>> incoming(
        entrypoint.instructions.size());
    std::vector<std::size_t> worklist;
    std::array<Type, kMaxLocals> entrypoint_local_types{};
    entrypoint_local_types.fill(Type::Unknown);
    incoming[0] = AbstractState{};
    worklist.push_back(0);

    while (!worklist.empty()) {
      const std::size_t index = worklist.back();
      worklist.pop_back();
      AbstractState state = *incoming[index];
      const auto &instruction = entrypoint.instructions[index];

      auto pop = [&state](Type expected,
                          AbstractValue *actual = nullptr) -> bool {
        if (state.stack.empty())
          return false;
        const AbstractValue value = state.stack.back();
        state.stack.pop_back();
        if (actual != nullptr)
          *actual = value;
        return expected == Type::Unknown || value.type == expected;
      };
      auto push_unknown = [&state](Type type) {
        state.stack.push_back(AbstractValue::unknown(type));
      };
      auto push_constant = [&state](Value value) {
        state.stack.push_back(AbstractValue::constant(value));
      };

      switch (instruction.op) {
      case Op::PushF64:
      case Op::PushI64:
        push_constant(constants_[instruction.operand_u16]);
        break;
      case Op::PushBool:
        push_constant(Value::boolean(instruction.operand_u8 != 0));
        break;
      case Op::LoadLocal: {
        const auto local = instruction.operand_u16;
        if (state.initialized[local] == 0 ||
            state.locals[local].type == Type::Unknown) {
          return DAXQ_FAULT_VERIFICATION;
        }
        state.stack.push_back(state.locals[local]);
        break;
      }
      case Op::StoreLocal: {
        AbstractValue actual{};
        if (!pop(Type::Unknown, &actual))
          return DAXQ_FAULT_VERIFICATION;
        auto &global_type = entrypoint_local_types[instruction.operand_u16];
        if (global_type != Type::Unknown && global_type != actual.type) {
          return DAXQ_FAULT_VERIFICATION;
        }
        global_type = actual.type;
        state.locals[instruction.operand_u16] = actual;
        state.initialized[instruction.operand_u16] = 1;
        break;
      }
      case Op::LoadArgument:
        push_unknown(argument_type(entrypoint.id, instruction.operand_u16));
        break;
      case Op::Add:
      case Op::Subtract:
      case Op::Multiply:
      case Op::Divide:
      case Op::Modulo: {
        AbstractValue right{};
        AbstractValue left{};
        if (!pop(Type::Unknown, &right) || !pop(Type::Unknown, &left) ||
            !is_numeric(left.type) || left.type != right.type) {
          return DAXQ_FAULT_VERIFICATION;
        }
        state.stack.push_back(fold_binary(instruction.op, left, right));
        break;
      }
      case Op::Negate: {
        AbstractValue actual{};
        if (!pop(Type::Unknown, &actual) || !is_numeric(actual.type)) {
          return DAXQ_FAULT_VERIFICATION;
        }
        state.stack.push_back(fold_negate(actual));
        break;
      }
      case Op::CompareEqual:
      case Op::CompareNotEqual: {
        AbstractValue right{};
        AbstractValue left{};
        if (!pop(Type::Unknown, &right) || !pop(Type::Unknown, &left) ||
            !is_scalar(left.type) || left.type != right.type) {
          return DAXQ_FAULT_VERIFICATION;
        }
        state.stack.push_back(fold_compare(instruction.op, left, right));
        break;
      }
      case Op::CompareLess:
      case Op::CompareLessEqual:
      case Op::CompareGreater:
      case Op::CompareGreaterEqual: {
        AbstractValue right{};
        AbstractValue left{};
        if (!pop(Type::Unknown, &right) || !pop(Type::Unknown, &left) ||
            !is_numeric(left.type) || left.type != right.type) {
          return DAXQ_FAULT_VERIFICATION;
        }
        state.stack.push_back(fold_compare(instruction.op, left, right));
        break;
      }
      case Op::BooleanAnd:
      case Op::BooleanOr: {
        AbstractValue right{};
        AbstractValue left{};
        if (!pop(Type::Bool, &right) || !pop(Type::Bool, &left)) {
          return DAXQ_FAULT_VERIFICATION;
        }
        if (left.known && right.known) {
          const bool value =
              instruction.op == Op::BooleanAnd
                  ? left.value().as_bool() && right.value().as_bool()
                  : left.value().as_bool() || right.value().as_bool();
          push_constant(Value::boolean(value));
        } else {
          push_unknown(Type::Bool);
        }
        break;
      }
      case Op::BooleanNot: {
        AbstractValue actual{};
        if (!pop(Type::Bool, &actual))
          return DAXQ_FAULT_VERIFICATION;
        if (actual.known)
          push_constant(Value::boolean(!actual.value().as_bool()));
        else
          push_unknown(Type::Bool);
        break;
      }
      case Op::IntegerToFloat: {
        AbstractValue actual{};
        if (!pop(Type::I64, &actual))
          return DAXQ_FAULT_VERIFICATION;
        if (actual.known)
          push_constant(
              Value::f64(static_cast<double>(actual.value().as_i64())));
        else
          push_unknown(Type::F64);
        break;
      }
      case Op::FloatToInteger: {
        AbstractValue actual{};
        if (!pop(Type::F64, &actual))
          return DAXQ_FAULT_VERIFICATION;
        state.stack.push_back(fold_float_to_integer(actual));
        break;
      }
      case Op::Branch:
        break;
      case Op::BranchTrue:
      case Op::BranchFalse:
        if (!pop(Type::Bool))
          return DAXQ_FAULT_VERIFICATION;
        break;
      case Op::NewBuffer:
        state.stack.push_back({
            buffer_type(instruction.operand_u8),
            true,
            instruction.operand_u16,
        });
        break;
      case Op::LoadElement: {
        AbstractValue buffer{};
        if (!pop(Type::I64) || !pop(Type::Unknown, &buffer) ||
            !is_buffer(buffer.type)) {
          return DAXQ_FAULT_VERIFICATION;
        }
        push_unknown(buffer_element_type(buffer.type));
        break;
      }
      case Op::StoreElement: {
        AbstractValue value{};
        AbstractValue buffer{};
        if (!pop(Type::Unknown, &value) || !pop(Type::I64) ||
            !pop(Type::Unknown, &buffer) || !is_buffer(buffer.type) ||
            buffer_element_type(buffer.type) != value.type)
          return DAXQ_FAULT_VERIFICATION;
        break;
      }
      case Op::Length: {
        AbstractValue buffer{};
        if (!pop(Type::Unknown, &buffer) || !is_buffer(buffer.type)) {
          return DAXQ_FAULT_VERIFICATION;
        }
        if (buffer.known)
          push_constant(Value::i64(static_cast<std::int64_t>(buffer.bits)));
        else
          push_unknown(Type::I64);
        break;
      }
      case Op::LoadState:
        push_unknown(state_types_[instruction.operand_u16]);
        break;
      case Op::StoreState:
        if (!pop(state_types_[instruction.operand_u16]))
          return DAXQ_FAULT_VERIFICATION;
        break;
      case Op::CallHost:
        if (!verify_host_call(instruction.host_id, entrypoint.id,
                              state.stack)) {
          return DAXQ_FAULT_VERIFICATION;
        }
        break;
      case Op::Return:
        if (!state.stack.empty())
          return DAXQ_FAULT_VERIFICATION;
        break;
      }

      std::array<std::int32_t, 2> successors{-1, -1};
      std::size_t successor_count = 0;
      if (instruction.op == Op::Branch) {
        successors[successor_count++] = instruction.branch_target_index;
      } else if (instruction.op == Op::BranchTrue ||
                 instruction.op == Op::BranchFalse) {
        successors[successor_count++] = instruction.branch_target_index;
        if (index + 1 >= entrypoint.instructions.size())
          return DAXQ_FAULT_VERIFICATION;
        successors[successor_count++] = static_cast<std::int32_t>(index + 1);
      } else if (instruction.op != Op::Return) {
        if (index + 1 >= entrypoint.instructions.size())
          return DAXQ_FAULT_VERIFICATION;
        successors[successor_count++] = static_cast<std::int32_t>(index + 1);
      }

      for (std::size_t successor_offset = 0; successor_offset < successor_count;
           ++successor_offset) {
        const auto successor =
            static_cast<std::size_t>(successors[successor_offset]);
        if (!incoming[successor].has_value()) {
          incoming[successor] = state;
          worklist.push_back(successor);
          continue;
        }
        bool changed = false;
        auto &merged = *incoming[successor];
        if (merged.stack.size() != state.stack.size())
          return DAXQ_FAULT_VERIFICATION;
        for (std::size_t stack_index = 0; stack_index < state.stack.size();
             ++stack_index) {
          if (merged.stack[stack_index].type != state.stack[stack_index].type) {
            return DAXQ_FAULT_VERIFICATION;
          }
          merge_constant(merged.stack[stack_index], state.stack[stack_index],
                         changed);
        }
        for (std::size_t local = 0; local < entrypoint.local_count; ++local) {
          const std::uint8_t initialized = static_cast<std::uint8_t>(
              merged.initialized[local] != 0 && state.initialized[local] != 0);
          if (initialized != merged.initialized[local]) {
            merged.initialized[local] = initialized;
            merged.locals[local] =
                AbstractValue::unknown(entrypoint_local_types[local]);
            changed = true;
          } else if (initialized != 0) {
            if (merged.locals[local].type != state.locals[local].type) {
              return DAXQ_FAULT_VERIFICATION;
            }
            merge_constant(merged.locals[local], state.locals[local], changed);
          }
        }
        if (changed)
          worklist.push_back(successor);
      }
    }
    return std::all_of(incoming.begin(), incoming.end(),
                       [](const auto &state) { return state.has_value(); })
               ? DAXQ_FAULT_OK
               : DAXQ_FAULT_VERIFICATION;
  }

  static void merge_constant(AbstractValue &existing,
                             const AbstractValue &candidate,
                             bool &changed) noexcept {
    if (existing.known &&
        (!candidate.known || existing.bits != candidate.bits)) {
      existing.known = false;
      existing.bits = 0;
      changed = true;
    }
  }

  [[nodiscard]] static bool pop_abstract(std::vector<AbstractValue> &stack,
                                         Type expected,
                                         AbstractValue *value = nullptr) {
    if (stack.empty() || stack.back().type != expected)
      return false;
    if (value != nullptr)
      *value = stack.back();
    stack.pop_back();
    return true;
  }

  [[nodiscard]] static bool
  verify_host_call(std::uint16_t host, std::uint8_t entrypoint_id,
                   std::vector<AbstractValue> &stack) {
    AbstractValue a{};
    AbstractValue b{};
    AbstractValue c{};
    switch (host) {
    case 1:
      if (!pop_abstract(stack, Type::I64, &b) ||
          !pop_abstract(stack, Type::I64, &a) ||
          (a.known && (a.value().as_i64() < 1 || a.value().as_i64() > 5)) ||
          (b.known && (b.value().as_i64() < 0 || b.value().as_i64() > 65535)))
        return false;
      stack.push_back(AbstractValue::unknown(Type::F64));
      return true;
    case 2:
      if (!pop_abstract(stack, Type::I64, &c) ||
          !pop_abstract(stack, Type::I64, &b) ||
          !pop_abstract(stack, Type::I64, &a) ||
          (a.known && (a.value().as_i64() < 1 || a.value().as_i64() > 4)) ||
          (b.known && (b.value().as_i64() < 1 || b.value().as_i64() > 65535)) ||
          (c.known && (c.value().as_i64() < 1 || c.value().as_i64() > 5)) ||
          (a.known && c.known && a.value().as_i64() == 4 &&
           c.value().as_i64() != 4)) {
        return false;
      }
      stack.push_back(AbstractValue::unknown(Type::F64));
      return true;
    case 3:
      if (!pop_abstract(stack, Type::I64, &a) ||
          (a.known && (a.value().as_i64() < 0 || a.value().as_i64() > 255)))
        return false;
      stack.push_back(AbstractValue::unknown(Type::F64));
      return true;
    case 4:
      if (!pop_abstract(stack, Type::I64, &c) ||
          !pop_abstract(stack, Type::F64, &b) ||
          !pop_abstract(stack, Type::I64, &a) ||
          (a.known && a.value().as_i64() != -1 && a.value().as_i64() != 0 &&
           a.value().as_i64() != 1) ||
          (b.known && (!std::isfinite(b.value().as_f64()) ||
                       b.value().as_f64() < 0.0 || b.value().as_f64() > 1.0)) ||
          (c.known && c.value().as_i64() < 0))
        return false;
      return true;
    case 6:
      if (entrypoint_id == kEntrypointInitialize) {
        stack.push_back(AbstractValue::constant(Value::i64(0)));
      } else {
        stack.push_back(AbstractValue::unknown(Type::I64));
      }
      return true;
    case 7:
      stack.push_back(AbstractValue::unknown(Type::F64));
      return true;
    case 8:
      if (!pop_abstract(stack, Type::F64, &b) ||
          !pop_abstract(stack, Type::I64, &a) ||
          (a.known && a.value().as_i64() < 0) ||
          (b.known && !std::isfinite(b.value().as_f64())))
        return false;
      return true;
    default:
      return false;
    }
  }

  Fault push(Value value, std::size_t stack_limit) noexcept {
    if (frame_.stack_size >= stack_limit ||
        frame_.stack_size >= frame_.stack.size()) {
      return DAXQ_FAULT_STACK_BUDGET;
    }
    frame_.stack[frame_.stack_size++] = value;
    frame_.max_stack_depth =
        std::max(frame_.max_stack_depth, frame_.stack_size);
    return DAXQ_FAULT_OK;
  }

  Fault pop(Value &value, Type expected = Type::Unknown) noexcept {
    if (frame_.stack_size == 0)
      return DAXQ_FAULT_TYPE;
    value = frame_.stack[--frame_.stack_size];
    return expected == Type::Unknown || value.type == expected
               ? DAXQ_FAULT_OK
               : DAXQ_FAULT_TYPE;
  }

  Fault execute_instruction(const Entrypoint &entrypoint,
                            const Instruction &instruction,
                            std::size_t stack_limit, std::int32_t &next_index,
                            bool &returned) noexcept {
    switch (instruction.op) {
    case Op::PushF64:
    case Op::PushI64:
      return push(constants_[instruction.operand_u16], stack_limit);
    case Op::PushBool:
      return push(Value::boolean(instruction.operand_u8 != 0), stack_limit);
    case Op::LoadLocal:
      if (frame_.local_initialized[instruction.operand_u16] == 0)
        return DAXQ_FAULT_TYPE;
      return push(frame_.locals[instruction.operand_u16], stack_limit);
    case Op::StoreLocal: {
      Value value{};
      if (const auto fault = pop(value); fault != DAXQ_FAULT_OK)
        return fault;
      frame_.locals[instruction.operand_u16] = value;
      frame_.local_initialized[instruction.operand_u16] = 1;
      return DAXQ_FAULT_OK;
    }
    case Op::LoadArgument:
      return push(frame_.arguments[instruction.operand_u16], stack_limit);
    case Op::Add:
    case Op::Subtract:
    case Op::Multiply:
    case Op::Divide:
    case Op::Modulo:
      return execute_binary_numeric(instruction.op, stack_limit);
    case Op::Negate:
      return execute_negate(stack_limit);
    case Op::CompareEqual:
    case Op::CompareNotEqual:
    case Op::CompareLess:
    case Op::CompareLessEqual:
    case Op::CompareGreater:
    case Op::CompareGreaterEqual:
      return execute_compare(instruction.op, stack_limit);
    case Op::BooleanAnd:
    case Op::BooleanOr: {
      Value right{};
      Value left{};
      if (pop(right, Type::Bool) != DAXQ_FAULT_OK ||
          pop(left, Type::Bool) != DAXQ_FAULT_OK) {
        return DAXQ_FAULT_TYPE;
      }
      const bool result = instruction.op == Op::BooleanAnd
                              ? left.as_bool() && right.as_bool()
                              : left.as_bool() || right.as_bool();
      return push(Value::boolean(result), stack_limit);
    }
    case Op::BooleanNot: {
      Value value{};
      if (pop(value, Type::Bool) != DAXQ_FAULT_OK)
        return DAXQ_FAULT_TYPE;
      return push(Value::boolean(!value.as_bool()), stack_limit);
    }
    case Op::IntegerToFloat: {
      Value value{};
      if (pop(value, Type::I64) != DAXQ_FAULT_OK)
        return DAXQ_FAULT_TYPE;
      double normalized{};
      if (!normalize_finite(static_cast<double>(value.as_i64()), normalized))
        return DAXQ_FAULT_NUMERIC;
      return push(Value::f64(normalized), stack_limit);
    }
    case Op::FloatToInteger: {
      Value value{};
      if (pop(value, Type::F64) != DAXQ_FAULT_OK)
        return DAXQ_FAULT_TYPE;
      const double number = value.as_f64();
      constexpr double kPositiveLimit = 9223372036854775808.0;
      constexpr double kNegativeLimit = -9223372036854775808.0;
      if (!std::isfinite(number) || number >= kPositiveLimit ||
          number < kNegativeLimit) {
        return DAXQ_FAULT_NUMERIC;
      }
      return push(Value::i64(static_cast<std::int64_t>(std::trunc(number))),
                  stack_limit);
    }
    case Op::Branch:
      next_index = instruction.branch_target_index;
      return DAXQ_FAULT_OK;
    case Op::BranchTrue:
    case Op::BranchFalse: {
      Value condition{};
      if (pop(condition, Type::Bool) != DAXQ_FAULT_OK)
        return DAXQ_FAULT_TYPE;
      const bool take = instruction.op == Op::BranchTrue ? condition.as_bool()
                                                         : !condition.as_bool();
      if (take)
        next_index = instruction.branch_target_index;
      return DAXQ_FAULT_OK;
    }
    case Op::NewBuffer:
      return execute_new_buffer(instruction, stack_limit);
    case Op::LoadElement:
      return execute_load_element(stack_limit);
    case Op::StoreElement:
      return execute_store_element();
    case Op::Length: {
      Value handle{};
      if (pop(handle) != DAXQ_FAULT_OK || !is_buffer(handle.type) ||
          handle.bits >= frame_.buffer_count)
        return DAXQ_FAULT_TYPE;
      return push(Value::i64(frame_.buffers[handle.bits].length), stack_limit);
    }
    case Op::LoadState:
      return push(frame_.staged_state[instruction.operand_u16], stack_limit);
    case Op::StoreState: {
      Value value{};
      if (pop(value, state_types_[instruction.operand_u16]) != DAXQ_FAULT_OK) {
        return DAXQ_FAULT_TYPE;
      }
      frame_.staged_state[instruction.operand_u16] = value;
      return DAXQ_FAULT_OK;
    }
    case Op::CallHost:
      return execute_host_call(entrypoint, instruction.host_id, stack_limit);
    case Op::Return:
      if (frame_.stack_size != 0)
        return DAXQ_FAULT_TYPE;
      returned = true;
      return DAXQ_FAULT_OK;
    }
    return DAXQ_FAULT_INTERNAL;
  }

  Fault execute_binary_numeric(Op op, std::size_t stack_limit) noexcept {
    Value right{};
    Value left{};
    if (pop(right) != DAXQ_FAULT_OK || pop(left) != DAXQ_FAULT_OK ||
        left.type != right.type || !is_numeric(left.type))
      return DAXQ_FAULT_TYPE;

    if (left.type == Type::I64) {
      const auto a = left.as_i64();
      const auto b = right.as_i64();
      std::int64_t result{};
      switch (op) {
      case Op::Add:
        if (!checked_add(a, b, result))
          return DAXQ_FAULT_NUMERIC;
        break;
      case Op::Subtract:
        if (!checked_subtract(a, b, result))
          return DAXQ_FAULT_NUMERIC;
        break;
      case Op::Multiply:
        if (!checked_multiply(a, b, result))
          return DAXQ_FAULT_NUMERIC;
        break;
      case Op::Divide:
        if (b == 0)
          return DAXQ_FAULT_DIVIDE_BY_ZERO;
        if (a == std::numeric_limits<std::int64_t>::min() && b == -1) {
          return DAXQ_FAULT_NUMERIC;
        }
        result = a / b;
        break;
      case Op::Modulo:
        if (b == 0)
          return DAXQ_FAULT_DIVIDE_BY_ZERO;
        if (a == std::numeric_limits<std::int64_t>::min() && b == -1) {
          return DAXQ_FAULT_NUMERIC;
        }
        result = a % b;
        break;
      default:
        return DAXQ_FAULT_INTERNAL;
      }
      return push(Value::i64(result), stack_limit);
    }

    const double a = left.as_f64();
    const double b = right.as_f64();
    if ((op == Op::Divide || op == Op::Modulo) && b == 0.0) {
      return DAXQ_FAULT_DIVIDE_BY_ZERO;
    }
    double raw{};
    switch (op) {
    case Op::Add:
      raw = a + b;
      break;
    case Op::Subtract:
      raw = a - b;
      break;
    case Op::Multiply:
      raw = a * b;
      break;
    case Op::Divide:
      raw = a / b;
      break;
    case Op::Modulo: {
      volatile double quotient = a / b;
      volatile double truncated = std::trunc(quotient);
      volatile double product = truncated * b;
      raw = a - product;
      break;
    }
    default:
      return DAXQ_FAULT_INTERNAL;
    }
    double normalized{};
    if (!normalize_finite(raw, normalized))
      return DAXQ_FAULT_NUMERIC;
    return push(Value::f64(normalized), stack_limit);
  }

  Fault execute_negate(std::size_t stack_limit) noexcept {
    Value value{};
    if (pop(value) != DAXQ_FAULT_OK || !is_numeric(value.type))
      return DAXQ_FAULT_TYPE;
    if (value.type == Type::I64) {
      if (value.as_i64() == std::numeric_limits<std::int64_t>::min())
        return DAXQ_FAULT_NUMERIC;
      return push(Value::i64(-value.as_i64()), stack_limit);
    }
    double normalized{};
    if (!normalize_finite(-value.as_f64(), normalized))
      return DAXQ_FAULT_NUMERIC;
    return push(Value::f64(normalized), stack_limit);
  }

  Fault execute_compare(Op op, std::size_t stack_limit) noexcept {
    Value right{};
    Value left{};
    if (pop(right) != DAXQ_FAULT_OK || pop(left) != DAXQ_FAULT_OK ||
        left.type != right.type || !is_scalar(left.type))
      return DAXQ_FAULT_TYPE;
    bool result{};
    if (left.type == Type::I64) {
      result = compare_values(op, left.as_i64(), right.as_i64());
    } else if (left.type == Type::F64) {
      result = compare_values(op, left.as_f64(), right.as_f64());
    } else if (op == Op::CompareEqual || op == Op::CompareNotEqual) {
      result = left.as_bool() == right.as_bool();
      if (op == Op::CompareNotEqual)
        result = !result;
    } else {
      return DAXQ_FAULT_TYPE;
    }
    return push(Value::boolean(result), stack_limit);
  }

  template <typename T>
  [[nodiscard]] static bool compare_values(Op op, T left, T right) noexcept {
    switch (op) {
    case Op::CompareEqual:
      return left == right;
    case Op::CompareNotEqual:
      return left != right;
    case Op::CompareLess:
      return left < right;
    case Op::CompareLessEqual:
      return left <= right;
    case Op::CompareGreater:
      return left > right;
    case Op::CompareGreaterEqual:
      return left >= right;
    default:
      return false;
    }
  }

  Fault execute_new_buffer(const Instruction &instruction,
                           std::size_t stack_limit) noexcept {
    if (frame_.buffer_count >= kMaxBuffers)
      return DAXQ_FAULT_BUFFER_LIMIT;
    const Type type = buffer_type(instruction.operand_u8);
    const std::size_t bytes =
        static_cast<std::size_t>(instruction.operand_u16) *
        logical_element_width(type);
    if (bytes > kMaxBufferBytes - frame_.aggregate_buffer_bytes)
      return DAXQ_FAULT_BUFFER_LIMIT;
    const std::size_t index = frame_.buffer_count++;
    auto &buffer = frame_.buffers[index];
    buffer.type = type;
    buffer.length = instruction.operand_u16;
    std::fill_n(buffer.elements.begin(), buffer.length, 0U);
    frame_.aggregate_buffer_bytes += bytes;
    return push(Value::buffer(type, index), stack_limit);
  }

  Fault execute_load_element(std::size_t stack_limit) noexcept {
    Value index_value{};
    Value handle{};
    if (pop(index_value, Type::I64) != DAXQ_FAULT_OK ||
        pop(handle) != DAXQ_FAULT_OK || !is_buffer(handle.type) ||
        handle.bits >= frame_.buffer_count)
      return DAXQ_FAULT_TYPE;
    const auto index = index_value.as_i64();
    auto &buffer = frame_.buffers[handle.bits];
    if (index < 0 || static_cast<std::uint64_t>(index) >= buffer.length) {
      return DAXQ_FAULT_INDEX_OUT_OF_RANGE;
    }
    Value result{buffer_element_type(buffer.type),
                 buffer.elements[static_cast<std::size_t>(index)]};
    return push(result, stack_limit);
  }

  Fault execute_store_element() noexcept {
    Value value{};
    Value index_value{};
    Value handle{};
    if (pop(value) != DAXQ_FAULT_OK ||
        pop(index_value, Type::I64) != DAXQ_FAULT_OK ||
        pop(handle) != DAXQ_FAULT_OK || !is_buffer(handle.type) ||
        handle.bits >= frame_.buffer_count ||
        buffer_element_type(handle.type) != value.type) {
      return DAXQ_FAULT_TYPE;
    }
    const auto index = index_value.as_i64();
    auto &buffer = frame_.buffers[handle.bits];
    if (index < 0 || static_cast<std::uint64_t>(index) >= buffer.length) {
      return DAXQ_FAULT_INDEX_OUT_OF_RANGE;
    }
    buffer.elements[static_cast<std::size_t>(index)] = value.bits;
    return DAXQ_FAULT_OK;
  }

  Fault execute_host_call(const Entrypoint &entrypoint, std::uint16_t host,
                          std::size_t stack_limit) noexcept {
    Value a{};
    Value b{};
    Value c{};
    double number{};
    double normalized{};
    std::int64_t integer{};
    switch (host) {
    case 1:
      if (pop(b, Type::I64) != DAXQ_FAULT_OK ||
          pop(a, Type::I64) != DAXQ_FAULT_OK) {
        return DAXQ_FAULT_TYPE;
      }
      if (a.as_i64() < 1 || a.as_i64() > 5 || b.as_i64() < 0 ||
          b.as_i64() > 65535) {
        return DAXQ_FAULT_HOST;
      }
      {
        const auto callback_fault =
            callbacks_.bar(callbacks_.context, a.as_i64(), b.as_i64(), &number);
        if (!FpEnvironment::enforce())
          return DAXQ_FAULT_INTERNAL;
        if (callback_fault != 0)
          return DAXQ_FAULT_HOST;
      }
      if (!normalize_finite(number, normalized))
        return DAXQ_FAULT_NUMERIC;
      return push(Value::f64(normalized), stack_limit);
    case 2:
      if (pop(c, Type::I64) != DAXQ_FAULT_OK ||
          pop(b, Type::I64) != DAXQ_FAULT_OK ||
          pop(a, Type::I64) != DAXQ_FAULT_OK)
        return DAXQ_FAULT_TYPE;
      if (a.as_i64() < 1 || a.as_i64() > 4 || b.as_i64() < 1 ||
          b.as_i64() > 65535 || c.as_i64() < 1 || c.as_i64() > 5 ||
          (a.as_i64() == 4 && c.as_i64() != 4)) {
        return DAXQ_FAULT_HOST;
      }
      {
        const auto callback_fault = callbacks_.ind(
            callbacks_.context, a.as_i64(), b.as_i64(), c.as_i64(), &number);
        if (!FpEnvironment::enforce())
          return DAXQ_FAULT_INTERNAL;
        if (callback_fault != 0)
          return DAXQ_FAULT_HOST;
      }
      if (!normalize_finite(number, normalized))
        return DAXQ_FAULT_NUMERIC;
      return push(Value::f64(normalized), stack_limit);
    case 3:
      if (pop(a, Type::I64) != DAXQ_FAULT_OK)
        return DAXQ_FAULT_TYPE;
      if (a.as_i64() < 0 || a.as_i64() > 255)
        return DAXQ_FAULT_HOST;
      {
        const auto callback_fault =
            callbacks_.param(callbacks_.context, a.as_i64(), &number);
        if (!FpEnvironment::enforce())
          return DAXQ_FAULT_INTERNAL;
        if (callback_fault != 0)
          return DAXQ_FAULT_HOST;
      }
      if (!normalize_finite(number, normalized))
        return DAXQ_FAULT_NUMERIC;
      return push(Value::f64(normalized), stack_limit);
    case 4:
      if (pop(c, Type::I64) != DAXQ_FAULT_OK ||
          pop(b, Type::F64) != DAXQ_FAULT_OK ||
          pop(a, Type::I64) != DAXQ_FAULT_OK)
        return DAXQ_FAULT_TYPE;
      if ((a.as_i64() != -1 && a.as_i64() != 0 && a.as_i64() != 1) ||
          !normalize_finite(b.as_f64(), normalized) || normalized < 0.0 ||
          normalized > 1.0 || c.as_i64() < 0)
        return DAXQ_FAULT_HOST;
      if (frame_.emit_count >= kMaxEmits)
        return DAXQ_FAULT_EFFECT_LIMIT;
      frame_.emits[frame_.emit_count++] = {a.as_i64(), normalized, c.as_i64()};
      return DAXQ_FAULT_OK;
    case 6: {
      const auto callback_fault =
          callbacks_.tindex(callbacks_.context, &integer);
      if (!FpEnvironment::enforce())
        return DAXQ_FAULT_INTERNAL;
      if (callback_fault != 0)
        return DAXQ_FAULT_HOST;
    }
      if (integer != (entrypoint.id == kEntrypointInitialize
                          ? 0
                          : frame_.arguments[0].as_i64())) {
        return DAXQ_FAULT_HOST;
      }
      return push(Value::i64(integer), stack_limit);
    case 7: {
      const auto callback_fault = callbacks_.rng(callbacks_.context, &number);
      if (!FpEnvironment::enforce())
        return DAXQ_FAULT_INTERNAL;
      if (callback_fault != 0)
        return DAXQ_FAULT_HOST;
    }
      if (!normalize_finite(number, normalized))
        return DAXQ_FAULT_NUMERIC;
      if (normalized < 0.0 || normalized >= 1.0)
        return DAXQ_FAULT_HOST;
      return push(Value::f64(normalized), stack_limit);
    case 8:
      if (pop(b, Type::F64) != DAXQ_FAULT_OK ||
          pop(a, Type::I64) != DAXQ_FAULT_OK) {
        return DAXQ_FAULT_TYPE;
      }
      if (a.as_i64() < 0 || !normalize_finite(b.as_f64(), normalized))
        return DAXQ_FAULT_HOST;
      if (frame_.log_count >= kMaxLogs)
        return DAXQ_FAULT_EFFECT_LIMIT;
      frame_.logs[frame_.log_count++] = {a.as_i64(), normalized};
      return DAXQ_FAULT_OK;
    default:
      return DAXQ_FAULT_INTERNAL;
    }
  }

  std::vector<std::uint8_t> bytecode_;
  std::vector<Value> constants_;
  std::array<std::uint8_t, 256> opcode_decode_{};
  std::array<std::uint16_t, 65536> host_decode_{};
  std::array<Entrypoint, kEntrypointCount> entrypoints_{};
  std::array<Type, kMaxStateSlots> state_types_{};
  std::array<Value, kMaxStateSlots> state_{};
  std::size_t state_count_{};
  daxq_vm_host_callbacks callbacks_{};
  bool callbacks_set_{};
  bool initialize_succeeded_{};
  std::atomic_flag invoking_ = ATOMIC_FLAG_INIT;
  Frame frame_{};
  std::mutex license_mutex_;
  std::array<char, 36> license_id_{};
  std::array<char, 36> release_id_{};
  std::array<char, 36> account_id_{};
  std::array<char, 36> device_id_{};
  std::chrono::system_clock::time_point authorized_until_system_{};
  std::chrono::steady_clock::time_point authorized_until_steady_{};
  std::int64_t revocation_sequence_{-1};
  bool enforcement_started_{};
  bool evidence_applied_{};
  bool run_token_applied_{};
  bool license_binding_set_{};
  bool license_revoked_{};
};

[[nodiscard]] bool valid_blob(const daxq_vm_blob &blob) noexcept {
  return blob.data != nullptr && blob.length != 0;
}

[[nodiscard]] bool valid_abi_header(std::uint32_t abi, std::uint32_t size,
                                    std::size_t expected) noexcept {
  return abi == DAXQ_VM_ABI_VERSION && size >= expected;
}

void initialize_result(daxq_vm_invoke_result &result, Fault fault) noexcept {
  result.abi_version = DAXQ_VM_ABI_VERSION;
  result.struct_size = sizeof(daxq_vm_invoke_result);
  result.fault = fault;
  result.executed_instructions = 0;
  result.max_stack_depth = 0;
  result.reserved = 0;
}

} // namespace

struct daxq_vm_handle {
  std::unique_ptr<Vm> implementation;
};

namespace {

std::mutex handle_registry_mutex;
std::unordered_map<daxq_vm_handle *, std::shared_ptr<daxq_vm_handle>>
    handle_registry;

[[nodiscard]] std::shared_ptr<daxq_vm_handle>
acquire_handle(daxq_vm_handle *raw) {
  if (raw == nullptr)
    return {};
  const std::lock_guard lock(handle_registry_mutex);
  const auto found = handle_registry.find(raw);
  return found == handle_registry.end() ? std::shared_ptr<daxq_vm_handle>{}
                                        : found->second;
}

} // namespace

extern "C" DAXQ_VM_API int32_t DAXQ_VM_CALL
daxq_vm_create(const daxq_vm_create_options *options, daxq_vm_handle **result) {
  try {
    if (result == nullptr)
      return DAXQ_FAULT_INVALID_ARGUMENT;
    *result = nullptr;
    if (options == nullptr)
      return DAXQ_FAULT_INVALID_ARGUMENT;
    if (!valid_abi_header(options->abi_version, options->struct_size,
                          sizeof(*options))) {
      return DAXQ_FAULT_ABI_MISMATCH;
    }
    if (!valid_blob(options->bytecode) || !valid_blob(options->constant_pool) ||
        !valid_blob(options->opcode_map) || !valid_blob(options->host_map) ||
        !valid_blob(options->entrypoints)) {
      return DAXQ_FAULT_INVALID_ARGUMENT;
    }
    if (!verify_self_image())
      return DAXQ_FAULT_VERIFICATION;

    auto handle = std::make_shared<daxq_vm_handle>();
    handle->implementation = std::make_unique<Vm>();
    const Fault fault = handle->implementation->load(*options);
    if (fault != DAXQ_FAULT_OK)
      return fault;
    daxq_vm_handle *const raw = handle.get();
    {
      const std::lock_guard lock(handle_registry_mutex);
      handle_registry.emplace(raw, handle);
    }
    *result = raw;
    return DAXQ_FAULT_OK;
  } catch (const std::bad_alloc &) {
    return DAXQ_FAULT_INTERNAL;
  } catch (...) {
    return DAXQ_FAULT_INTERNAL;
  }
}

extern "C" DAXQ_VM_API int32_t DAXQ_VM_CALL daxq_vm_set_host_callbacks(
    daxq_vm_handle *vm, const daxq_vm_host_callbacks *callbacks) {
  try {
    auto handle = acquire_handle(vm);
    if (!handle || handle->implementation == nullptr || callbacks == nullptr) {
      return DAXQ_FAULT_INVALID_ARGUMENT;
    }
    if (!valid_abi_header(callbacks->abi_version, callbacks->struct_size,
                          sizeof(*callbacks))) {
      return DAXQ_FAULT_ABI_MISMATCH;
    }
    return handle->implementation->set_host_callbacks(*callbacks);
  } catch (...) {
    return DAXQ_FAULT_INTERNAL;
  }
}

extern "C" DAXQ_VM_API int32_t DAXQ_VM_CALL
daxq_vm_invoke(daxq_vm_handle *vm, const daxq_vm_invoke_options *options,
               daxq_vm_invoke_result *result) {
  try {
    auto handle = acquire_handle(vm);
    if (!handle || handle->implementation == nullptr || options == nullptr ||
        result == nullptr) {
      return DAXQ_FAULT_INVALID_ARGUMENT;
    }
    if (!valid_abi_header(options->abi_version, options->struct_size,
                          sizeof(*options)) ||
        !valid_abi_header(result->abi_version, result->struct_size,
                          sizeof(*result))) {
      return DAXQ_FAULT_ABI_MISMATCH;
    }
    if (!std::all_of(std::begin(options->reserved0),
                     std::end(options->reserved0),
                     [](std::uint8_t value) { return value == 0; }) ||
        options->reserved1 != 0) {
      initialize_result(*result, DAXQ_FAULT_INVALID_ARGUMENT);
      return DAXQ_FAULT_INVALID_ARGUMENT;
    }
    initialize_result(*result, DAXQ_FAULT_OK);
    const Fault fault = handle->implementation->invoke(*options, *result);
    result->fault = fault;
    return fault;
  } catch (...) {
    if (result != nullptr)
      initialize_result(*result, DAXQ_FAULT_INTERNAL);
    return DAXQ_FAULT_INTERNAL;
  }
}

extern "C" DAXQ_VM_API int32_t DAXQ_VM_CALL daxq_vm_apply_license_evidence(
    daxq_vm_handle *vm, const daxq_vm_license_evidence *evidence) {
  try {
    auto handle = acquire_handle(vm);
    if (!handle || handle->implementation == nullptr) {
      return DAXQ_FAULT_INVALID_ARGUMENT;
    }
    if (evidence == nullptr ||
        evidence->protection_abi_version != DAXQ_VM_PROTECTION_ABI_VERSION ||
        evidence->struct_size < sizeof(*evidence) ||
        evidence->payload.data == nullptr || evidence->payload.length == 0 ||
        evidence->payload.length > kMaximumLicensePayloadBytes) {
      handle->implementation->reject_license_evidence_attempt();
      return DAXQ_FAULT_INVALID_ARGUMENT;
    }
    return handle->implementation->apply_license_evidence(*evidence);
  } catch (...) {
    return DAXQ_FAULT_INTERNAL;
  }
}

extern "C" DAXQ_VM_API int32_t DAXQ_VM_CALL
daxq_vm_revoke_license(daxq_vm_handle *vm) {
  try {
    auto handle = acquire_handle(vm);
    if (!handle || handle->implementation == nullptr)
      return DAXQ_FAULT_INVALID_ARGUMENT;
    return handle->implementation->revoke_license();
  } catch (...) {
    return DAXQ_FAULT_INTERNAL;
  }
}

extern "C" DAXQ_VM_API int32_t DAXQ_VM_CALL daxq_vm_verify_integrity(void) {
  try {
    return verify_self_image() ? DAXQ_FAULT_OK : DAXQ_FAULT_VERIFICATION;
  } catch (...) {
    return DAXQ_FAULT_INTERNAL;
  }
}

extern "C" DAXQ_VM_API void DAXQ_VM_CALL daxq_vm_destroy(daxq_vm_handle *vm) {
  try {
    std::shared_ptr<daxq_vm_handle> owner;
    {
      const std::lock_guard lock(handle_registry_mutex);
      const auto found = handle_registry.find(vm);
      if (found == handle_registry.end())
        return;
      owner = std::move(found->second);
      handle_registry.erase(found);
    }
  } catch (...) {
  }
}
