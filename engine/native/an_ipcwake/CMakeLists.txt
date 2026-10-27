# Cross-process wake primitives for the plugin sandbox (C). See include/an_ipcwake.h and docs/spikes/S4-sandbox-wake.md.
# Windows is not built yet: its design (named events over a file mapping) is documented in the header.
if(WIN32)
  message(STATUS "an_ipcwake: no Windows backend yet, skipped")
  return()
endif()

add_library(an_ipcwake SHARED src/an_ipcwake_posix.c src/an_ipcwake_workgroup.c)
target_include_directories(an_ipcwake PUBLIC include)

if(APPLE)
  target_link_libraries(an_ipcwake PRIVATE "-framework CoreAudio" "-framework CoreFoundation")
elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  find_package(Threads REQUIRED)
  target_link_libraries(an_ipcwake PRIVATE Threads::Threads rt)
endif()

target_compile_options(an_ipcwake PRIVATE -Wall -Wextra -Werror -O2)

cth_output_to_artifacts(an_ipcwake)
