include(CMakeFindDependencyMacro)
find_dependency(log CONFIG REQUIRED)
include("${CMAKE_CURRENT_LIST_DIR}/math_module-targets.cmake")
