cmake_minimum_required(VERSION 3.28)
project(log LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_library(log STATIC)
target_sources(log
    PUBLIC FILE_SET CXX_MODULES FILES
        src/log.cppm
    PRIVATE
        src/log_impl.cpp
)
target_compile_features(log PUBLIC cxx_std_23)

include(GNUInstallDirs)

install(TARGETS log
    EXPORT log-targets
    ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR}
    FILE_SET CXX_MODULES
        DESTINATION ${CMAKE_INSTALL_LIBDIR}/cmake/log/cxx_modules
)

install(EXPORT log-targets
    NAMESPACE log::
    DESTINATION ${CMAKE_INSTALL_LIBDIR}/cmake/log
)

include(CMakePackageConfigHelpers)

file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/log-config.cmake" [=[
include(CMakeFindDependencyMacro)
include("${CMAKE_CURRENT_LIST_DIR}/log-targets.cmake")
]=])

install(FILES "${CMAKE_CURRENT_BINARY_DIR}/log-config.cmake"
    DESTINATION ${CMAKE_INSTALL_LIBDIR}/cmake/log
)
