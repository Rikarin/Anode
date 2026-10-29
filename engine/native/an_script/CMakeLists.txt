# Scripter's JavaScript engine (C), embedding QuickJS-ng behind a C ABI. See include/an_script.h and
# docs/adr/0009-scripter.md.

include(FetchContent)

# Pinned: bump deliberately and re-run the Scripter tests (engine/tests/Anode.Dsp.Tests/Scripter). MIT. Upstream's own
# CMakeLists is not used (it builds the qjs tools and quickjs-libc, whose std and os modules Scripter must not have);
# build.sh fetches the pin into engine/artifacts/deps instead.
FetchContent_Declare(quickjs_ng
  GIT_REPOSITORY https://github.com/quickjs-ng/quickjs.git
  GIT_TAG 6d46d07d04041b40f4f49eaa7fdebe44c314c699 # v0.17.0 (2026-09-18)
  SOURCE_SUBDIR _anode_does_not_use_upstream_cmake)
FetchContent_MakeAvailable(quickjs_ng)
set(AN_SCRIPT_UPSTREAM_VERSION "quickjs-ng 0.17.0+6d46d07")

# QuickJS-ng itself, without quickjs-libc: upstream's flags, warnings off (third-party code).
add_library(an_script_quickjs OBJECT
  ${quickjs_ng_SOURCE_DIR}/dtoa.c
  ${quickjs_ng_SOURCE_DIR}/libregexp.c
  ${quickjs_ng_SOURCE_DIR}/libunicode.c
  ${quickjs_ng_SOURCE_DIR}/quickjs.c)
target_compile_definitions(an_script_quickjs PRIVATE _GNU_SOURCE)
if(MSVC)
  target_compile_options(an_script_quickjs PRIVATE /w /experimental:c11atomics)
  target_compile_definitions(an_script_quickjs PRIVATE WIN32_LEAN_AND_MEAN _WIN32_WINNT=0x0601)
else()
  target_compile_options(an_script_quickjs PRIVATE -w -funsigned-char -fwrapv $<$<CONFIG:Release>:-O2>)
endif()

add_library(an_script SHARED src/an_script.c src/arena.c $<TARGET_OBJECTS:an_script_quickjs>)
target_include_directories(an_script PUBLIC include)
# Third-party headers: SYSTEM, so our warning flags apply to our code only.
target_include_directories(an_script SYSTEM PRIVATE ${quickjs_ng_SOURCE_DIR})
target_compile_definitions(an_script PRIVATE AN_SCRIPT_UPSTREAM_VERSION="${AN_SCRIPT_UPSTREAM_VERSION}")
if(MSVC)
  target_compile_options(an_script PRIVATE /W4 /WX /experimental:c11atomics)
else()
  target_compile_options(an_script PRIVATE -Wall -Wextra -Werror $<$<CONFIG:Release>:-O2>)
  target_link_libraries(an_script PRIVATE m)
endif()
cth_output_to_artifacts(an_script)
