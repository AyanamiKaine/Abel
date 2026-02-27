cmake_minimum_required(VERSION 3.28)
project(sdl_example LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Dependency build options
set(SDL_SHARED OFF CACHE BOOL "")
set(SDL_STATIC ON CACHE BOOL "")
set(SDL_TEST_LIBRARY OFF CACHE BOOL "")
set(SDL_TESTS OFF CACHE BOOL "")
set(SDL_EXAMPLES OFF CACHE BOOL "")

include(FetchContent)
FetchContent_Declare(
    sdl3
    GIT_REPOSITORY https://github.com/libsdl-org/SDL.git
    GIT_TAG        release-3.4.2
    GIT_SHALLOW    TRUE
)
FetchContent_MakeAvailable(sdl3)

add_executable(sdl_example)
target_sources(sdl_example
    PRIVATE
        main.cpp
)

# Sane warning defaults for project sources.
if(MSVC)
    target_compile_options(sdl_example PRIVATE /W4 /permissive-)
else()
    target_compile_options(sdl_example PRIVATE -Wall -Wextra -Wpedantic)
endif()
target_link_libraries(sdl_example PRIVATE
    SDL3::SDL3
)
