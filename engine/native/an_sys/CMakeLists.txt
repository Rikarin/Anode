# Fixed-signature wrappers over variadic or platform-specific system calls the engine needs (C). See include/an_sys.h.
# Windows is not built yet: its counterparts (named mappings, FILE_FLAG_NO_BUFFERING, MMCSS) need no C wrapper.
if(WIN32)
  message(STATUS "an_sys: no Windows backend yet, skipped")
  return()
endif()

add_library(an_sys SHARED src/an_sys.c)
target_include_directories(an_sys PUBLIC include)

if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  find_package(Threads REQUIRED)
  target_link_libraries(an_sys PRIVATE Threads::Threads rt)
endif()

target_compile_options(an_sys PRIVATE -Wall -Wextra -Werror -O2)

cth_output_to_artifacts(an_sys)
