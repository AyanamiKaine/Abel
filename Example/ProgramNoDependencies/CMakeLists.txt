cmake_minimum_required(VERSION 3.28)
project(hello_world LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_executable(hello_world)
target_sources(hello_world
    PRIVATE
        main.cpp
)

# Sane warning defaults for project sources.
if(MSVC)
    target_compile_options(hello_world PRIVATE /W4 /permissive-)
else()
    target_compile_options(hello_world PRIVATE -Wall -Wextra -Wpedantic)
endif()
