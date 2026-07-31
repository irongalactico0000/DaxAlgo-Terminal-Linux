cmake_minimum_required(VERSION 3.20)

project(DaxqVm VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

option(DAXQ_VM_PROTECTOR_SYMBOLS
    "Emit non-shipping PDB and MAP files for the licensed protector projects"
    OFF)
option(DAXQ_VM_HARDENED_RELEASE
    "Build the fail-closed licensed runtime used as protector input"
    OFF)
set(DAXQ_VM_LICENSE_KEY_SHA256_HEX "" CACHE STRING
    "SHA-256 of the 64-byte raw P-256 licensing verification key (X || Y)")
set(DAXQ_VM_LICENSE_ISSUER "" CACHE STRING
    "Exact production issuer accepted by the hardened native license verifier")
set(DAXQ_VM_LICENSE_AUDIENCE "" CACHE STRING
    "Exact production audience accepted by the hardened native license verifier")

add_library(daxq_vm SHARED
    src/daxq_vm.cpp
)

target_compile_definitions(daxq_vm PRIVATE DAXQ_VM_BUILD)
target_include_directories(daxq_vm
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
)

if(WIN32)
    target_link_libraries(daxq_vm PRIVATE bcrypt wintrust crypt32)
elseif(APPLE)
    find_library(SECURITY_FRAMEWORK Security REQUIRED)
    find_library(COREFOUNDATION_FRAMEWORK CoreFoundation REQUIRED)
    target_link_libraries(daxq_vm PRIVATE
        ${SECURITY_FRAMEWORK}
        ${COREFOUNDATION_FRAMEWORK}
    )
endif()

if(DAXQ_VM_HARDENED_RELEASE)
    string(LENGTH "${DAXQ_VM_LICENSE_KEY_SHA256_HEX}" DAXQ_VM_LICENSE_KEY_HASH_LENGTH)
    if(NOT DAXQ_VM_LICENSE_KEY_HASH_LENGTH EQUAL 64 OR
       NOT DAXQ_VM_LICENSE_KEY_SHA256_HEX MATCHES "^[0-9A-Fa-f]+$")
        message(FATAL_ERROR
            "DAXQ_VM_LICENSE_KEY_SHA256_HEX must contain exactly 64 hexadecimal characters")
    endif()
    if(DAXQ_VM_LICENSE_ISSUER STREQUAL "" OR DAXQ_VM_LICENSE_AUDIENCE STREQUAL "")
        message(FATAL_ERROR
            "Hardened builds require non-empty DAXQ_VM_LICENSE_ISSUER and DAXQ_VM_LICENSE_AUDIENCE")
    endif()
    if(NOT DAXQ_VM_LICENSE_ISSUER MATCHES "^[A-Za-z0-9][A-Za-z0-9._:/-]*$" OR
       NOT DAXQ_VM_LICENSE_AUDIENCE MATCHES "^[A-Za-z0-9][A-Za-z0-9._:/-]*$")
        message(FATAL_ERROR
            "DAXQ VM issuer and audience may contain only claim-safe ASCII characters")
    endif()
    target_compile_definitions(daxq_vm PRIVATE
        DAXQ_VM_HARDENED_RELEASE=1
        "DAXQ_VM_LICENSE_KEY_SHA256_HEX=\"${DAXQ_VM_LICENSE_KEY_SHA256_HEX}\""
        "DAXQ_VM_LICENSE_ISSUER=\"${DAXQ_VM_LICENSE_ISSUER}\""
        "DAXQ_VM_LICENSE_AUDIENCE=\"${DAXQ_VM_LICENSE_AUDIENCE}\""
    )
endif()

if(MSVC)
    target_compile_options(daxq_vm PRIVATE /W4 /permissive- /fp:strict /EHsc /GS /guard:cf)
    target_link_options(daxq_vm PRIVATE
        /guard:cf
        /DYNAMICBASE
        /HIGHENTROPYVA
        /NXCOMPAT
        /CETCOMPAT
    )
    if(DAXQ_VM_PROTECTOR_SYMBOLS)
        target_compile_options(daxq_vm PRIVATE /Zi)
        target_link_options(daxq_vm PRIVATE
            /DEBUG:FULL
            "/MAP:$<TARGET_FILE_DIR:daxq_vm>/daxq_vm.map"
        )
        set_target_properties(daxq_vm PROPERTIES PDB_NAME daxq_vm)
    endif()
else()
    target_compile_options(daxq_vm PRIVATE
        -Wall
        -Wextra
        -Wpedantic
        -fstack-protector-strong
        -fno-fast-math
        -ffp-contract=off
    )
endif()

set_target_properties(daxq_vm PROPERTIES
    OUTPUT_NAME daxq_vm
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES
)

install(TARGETS daxq_vm
    RUNTIME DESTINATION bin
    LIBRARY DESTINATION lib
    ARCHIVE DESTINATION lib
)
install(FILES include/daxq_vm.h DESTINATION include)

include(CTest)
if(BUILD_TESTING)
    add_subdirectory(tests)
endif()
