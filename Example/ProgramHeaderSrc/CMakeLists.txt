cmake_minimum_required(VERSION 3.28)
project(ProgramHeaderSrc LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_executable(ProgramHeaderSrc)

# Legacy header/src support (non-module layout).
if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/include")
    target_include_directories(ProgramHeaderSrc PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/include"
    )
endif()
file(GLOB_RECURSE ABEL_PROGRAMHEADERSRC_LEGACY_SOURCES CONFIGURE_DEPENDS
    "${CMAKE_CURRENT_SOURCE_DIR}/src/*.c"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/*.cc"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/*.cxx"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/*.cpp"
)
if(ABEL_PROGRAMHEADERSRC_LEGACY_SOURCES)
    target_sources(ProgramHeaderSrc PRIVATE ${ABEL_PROGRAMHEADERSRC_LEGACY_SOURCES})
endif()
target_sources(ProgramHeaderSrc
    PRIVATE
        main.cpp
)

# Sane warning defaults for project sources.
if(MSVC)
    target_compile_options(ProgramHeaderSrc PRIVATE /W4 /permissive-)
else()
    target_compile_options(ProgramHeaderSrc PRIVATE -Wall -Wextra -Wpedantic)
endif()
