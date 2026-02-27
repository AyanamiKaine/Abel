cmake_minimum_required(VERSION 3.28)
project(math_module LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_library(math_module STATIC)
target_sources(math_module
    PUBLIC FILE_SET CXX_MODULES FILES
        src/math.cppm
    PRIVATE
        src/math_impl.cpp
)
target_compile_features(math_module PUBLIC cxx_std_23)

# Sane warning defaults for project sources.
if(MSVC)
    target_compile_options(math_module PRIVATE /W4 /permissive-)
else()
    target_compile_options(math_module PRIVATE -Wall -Wextra -Wpedantic)
endif()

include(GNUInstallDirs)

install(TARGETS math_module
    EXPORT math_module-targets
    ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR}
    FILE_SET CXX_MODULES
        DESTINATION ${CMAKE_INSTALL_LIBDIR}/cmake/math_module/cxx_modules
)

install(EXPORT math_module-targets
    NAMESPACE math_module::
    DESTINATION ${CMAKE_INSTALL_LIBDIR}/cmake/math_module
)

include(CMakePackageConfigHelpers)

file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/math_module-config.cmake" [=[
include(CMakeFindDependencyMacro)
include("${CMAKE_CURRENT_LIST_DIR}/math_module-targets.cmake")
]=])

install(FILES "${CMAKE_CURRENT_BINARY_DIR}/math_module-config.cmake"
    DESTINATION ${CMAKE_INSTALL_LIBDIR}/cmake/math_module
)
