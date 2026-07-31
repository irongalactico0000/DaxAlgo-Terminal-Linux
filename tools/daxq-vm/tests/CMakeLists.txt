add_executable(daxq_vm_tests daxq_vm_tests.cpp)
target_link_libraries(daxq_vm_tests PRIVATE daxq_vm)
add_custom_command(TARGET daxq_vm_tests POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_FILE:daxq_vm>
        $<TARGET_FILE_DIR:daxq_vm_tests>
)

if(MSVC)
    target_compile_options(daxq_vm_tests PRIVATE /W4 /permissive- /fp:strict /EHsc)
else()
    target_compile_options(daxq_vm_tests PRIVATE
        -Wall
        -Wextra
        -Wpedantic
        -fno-fast-math
        -ffp-contract=off
    )
endif()

add_test(NAME daxq_vm_tests COMMAND daxq_vm_tests)
