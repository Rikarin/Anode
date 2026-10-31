# Ableton Link behind a C ABI (C++), over the Link SDK's own C extension (abl_link). See include/an_link.h and
# docs/09-phase-8.md ("Sync"). Link is GPL-2.0-or-later (docs/adr/0008-third-party-licensing.md).

include(FetchContent)

# Pinned: bump deliberately and re-run the Link tests (engine/tests/Anode.Engine.Tests/Sync/LinkTests.cs). Upstream's
# own CMakeLists is not used (it builds the examples and tests); build.sh fetches both pins into engine/artifacts/deps
# instead: the SDK, and the standalone Asio its modules/asio-standalone submodule pins at that commit.
FetchContent_Declare(link_sdk
  GIT_REPOSITORY https://github.com/Ableton/link.git
  GIT_TAG 9c9091275e707ab09d09a5a608fcdb84bf0dec85 # Link-4.1 (2026-08-14)
  SOURCE_SUBDIR _anode_does_not_use_upstream_cmake)
FetchContent_Declare(link_asio
  GIT_REPOSITORY https://github.com/chriskohlhoff/asio.git
  GIT_TAG 8806a6803cde7054c3049d3666d3ec36786568c5 # Link-4.1's modules/asio-standalone
  SOURCE_SUBDIR _anode_does_not_use_upstream_cmake)
FetchContent_MakeAvailable(link_sdk link_asio)
set(AN_LINK_UPSTREAM_VERSION "Link 4.1+9c90912")

if(APPLE)
  set(AN_LINK_PLATFORM LINK_PLATFORM_MACOSX=1 LINK_PLATFORM_UNIX=1)
elseif(WIN32)
  set(AN_LINK_PLATFORM LINK_PLATFORM_WINDOWS=1 INCL_EXTRA_HTON_FUNCTIONS=1 _WIN32_WINNT=0x0601)
else()
  set(AN_LINK_PLATFORM LINK_PLATFORM_LINUX=1 LINK_PLATFORM_UNIX=1)
endif()

# Link's C extension: third-party code, warnings off.
add_library(an_link_abl OBJECT ${link_sdk_SOURCE_DIR}/extensions/abl_link/src/abl_link.cpp)
target_include_directories(an_link_abl SYSTEM PRIVATE
  ${link_sdk_SOURCE_DIR}/include ${link_sdk_SOURCE_DIR}/extensions/abl_link/include ${link_asio_SOURCE_DIR}/include)
target_compile_definitions(an_link_abl PRIVATE ${AN_LINK_PLATFORM} ASIO_NO_TYPEID=1 ASIO_STANDALONE=1)
if(MSVC)
  target_compile_options(an_link_abl PRIVATE /w /EHsc)
else()
  target_compile_options(an_link_abl PRIVATE -w -O2)
endif()

add_library(an_link SHARED src/an_link.cpp $<TARGET_OBJECTS:an_link_abl>)
target_include_directories(an_link PUBLIC include)
target_include_directories(an_link SYSTEM PRIVATE ${link_sdk_SOURCE_DIR}/extensions/abl_link/include)
target_compile_definitions(an_link PRIVATE AN_LINK_UPSTREAM_VERSION="${AN_LINK_UPSTREAM_VERSION}")
if(MSVC)
  target_compile_options(an_link PRIVATE /W4 /WX /EHsc)
  target_link_libraries(an_link PRIVATE ws2_32 iphlpapi winmm)
else()
  target_compile_options(an_link PRIVATE -Wall -Wextra -Werror -O2)
  if(NOT APPLE)
    find_package(Threads REQUIRED)
    target_link_libraries(an_link PRIVATE Threads::Threads atomic)
  endif()
endif()
cth_output_to_artifacts(an_link)
