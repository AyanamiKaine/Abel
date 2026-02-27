cmake_minimum_required(VERSION 3.28)
project(StellaVM LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

find_package(vm CONFIG REQUIRED)

add_executable(StellaVM)
target_sources(StellaVM
    PRIVATE
        main.cpp
)

# Sane warning defaults for project sources.
if(MSVC)
    target_compile_options(StellaVM PRIVATE /W4 /permissive-)
else()
    target_compile_options(StellaVM PRIVATE -Wall -Wextra -Wpedantic)
endif()
target_link_libraries(StellaVM PRIVATE
    vm::vm
)
