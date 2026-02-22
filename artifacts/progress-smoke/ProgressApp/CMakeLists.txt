cmake_minimum_required(VERSION 3.28)
project(ProgressApp LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Dependency build options
set(FMT_DOC OFF CACHE BOOL "")
set(FMT_TEST OFF CACHE BOOL "")
set(SPDLOG_FMT_EXTERNAL ON CACHE BOOL "")
set(SPDLOG_BUILD_BENCH OFF CACHE BOOL "")
set(SPDLOG_BUILD_TESTS OFF CACHE BOOL "")
set(SPDLOG_BUILD_EXAMPLE OFF CACHE BOOL "")

include(FetchContent)
FetchContent_Declare(
    fmt
    GIT_REPOSITORY https://github.com/fmtlib/fmt.git
    GIT_TAG        11.1.4
    GIT_SHALLOW    TRUE
)
FetchContent_Declare(
    spdlog
    GIT_REPOSITORY https://github.com/gabime/spdlog.git
    GIT_TAG        v1.15.1
    GIT_SHALLOW    TRUE
)
FetchContent_MakeAvailable(fmt spdlog)

add_executable(ProgressApp)
target_sources(ProgressApp
    PRIVATE
        main.cpp
)
target_link_libraries(ProgressApp PRIVATE
    fmt::fmt
    spdlog::spdlog
)
