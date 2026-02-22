#----------------------------------------------------------------
# Generated CMake target import file for configuration "Debug".
#----------------------------------------------------------------

# Commands may need to know the format version.
set(CMAKE_IMPORT_FILE_VERSION 1)

# Import target "log::log" for configuration "Debug"
set_property(TARGET log::log APPEND PROPERTY IMPORTED_CONFIGURATIONS DEBUG)
set_target_properties(log::log PROPERTIES
  IMPORTED_LINK_INTERFACE_LANGUAGES_DEBUG "CXX"
  IMPORTED_LOCATION_DEBUG "${_IMPORT_PREFIX}/lib/log.lib"
  )

list(APPEND _cmake_import_check_targets log::log )
list(APPEND _cmake_import_check_files_for_log::log "${_IMPORT_PREFIX}/lib/log.lib" )

# Commands beyond this point should not need to know the version.
set(CMAKE_IMPORT_FILE_VERSION)
