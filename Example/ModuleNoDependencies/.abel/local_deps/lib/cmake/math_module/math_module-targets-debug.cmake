#----------------------------------------------------------------
# Generated CMake target import file for configuration "Debug".
#----------------------------------------------------------------

# Commands may need to know the format version.
set(CMAKE_IMPORT_FILE_VERSION 1)

# Import target "math_module::math_module" for configuration "Debug"
set_property(TARGET math_module::math_module APPEND PROPERTY IMPORTED_CONFIGURATIONS DEBUG)
set_target_properties(math_module::math_module PROPERTIES
  IMPORTED_LINK_INTERFACE_LANGUAGES_DEBUG "CXX"
  IMPORTED_LOCATION_DEBUG "${_IMPORT_PREFIX}/lib/math_module.lib"
  )

list(APPEND _cmake_import_check_targets math_module::math_module )
list(APPEND _cmake_import_check_files_for_math_module::math_module "${_IMPORT_PREFIX}/lib/math_module.lib" )

# Commands beyond this point should not need to know the version.
set(CMAKE_IMPORT_FILE_VERSION)
