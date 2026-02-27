cmake_minimum_required(VERSION 3.28)
project(vm LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

include(FetchContent)
FetchContent_Declare(
    doctest
    GIT_REPOSITORY https://github.com/doctest/doctest.git
    GIT_TAG        v2.4.12
    GIT_SHALLOW    TRUE
)
FetchContent_MakeAvailable(doctest)

add_library(vm STATIC)
target_sources(vm
    PUBLIC FILE_SET CXX_MODULES FILES
        src/vm.cppm
    PRIVATE
        src/vm_impl.cpp
)
target_compile_features(vm PUBLIC cxx_std_23)

# Sane warning defaults for project sources.
if(MSVC)
    target_compile_options(vm PRIVATE /W4 /permissive-)
else()
    target_compile_options(vm PRIVATE -Wall -Wextra -Wpedantic)
endif()

include(GNUInstallDirs)

install(TARGETS vm
    EXPORT vm-targets
    ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR}
    FILE_SET CXX_MODULES
        DESTINATION ${CMAKE_INSTALL_LIBDIR}/cmake/vm/cxx_modules
)

install(EXPORT vm-targets
    NAMESPACE vm::
    DESTINATION ${CMAKE_INSTALL_LIBDIR}/cmake/vm
)

include(CMakePackageConfigHelpers)

file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/vm-config.cmake" [=[
include(CMakeFindDependencyMacro)
include("${CMAKE_CURRENT_LIST_DIR}/vm-targets.cmake")
]=])

install(FILES "${CMAKE_CURRENT_BINARY_DIR}/vm-config.cmake"
    DESTINATION ${CMAKE_INSTALL_LIBDIR}/cmake/vm
)

include(CTest)

if(BUILD_TESTING)
    add_executable(StellaVM_test tests/StellaVM_test.cpp)
    target_compile_definitions(StellaVM_test PRIVATE DOCTEST_CONFIG_IMPLEMENT_WITH_MAIN)
    target_link_libraries(StellaVM_test PRIVATE
        vm
        doctest::doctest
    )
    add_test(NAME StellaVM_test COMMAND StellaVM_test)
endif()
