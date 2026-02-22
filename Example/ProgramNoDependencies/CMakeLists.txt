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
