# A tiny CLAP instrument for the engine's tests (docs/03-phase-2.md, workstream A): a polyphonic sine with two
# parameters and a state. Built into engine/artifacts/native/clap/, which the tests and the host probe scan.

include(FetchContent)

# Pinned: the CLAP headers (MIT, header-only). build.sh fetches this commit into engine/artifacts/deps and points
# FETCHCONTENT_SOURCE_DIR_CLAP there; the engine's C# ABI (Anode.Engine/Plugins/Clap/ClapAbi.cs) follows the same
# version.
FetchContent_Declare(clap
  GIT_REPOSITORY https://github.com/free-audio/clap.git
  GIT_TAG cd94482ba5941ae410809b6fbaed3bc851044270 # 1.2.9
  SOURCE_SUBDIR _anode_does_not_use_upstream_cmake)
FetchContent_MakeAvailable(clap)

set(AN_CLAP_DIR ${CTH_ARTIFACTS_DIR}/clap)
add_library(an_testclap MODULE src/an_testclap.c)
target_include_directories(an_testclap SYSTEM PRIVATE ${clap_SOURCE_DIR}/include)
set_target_properties(an_testclap PROPERTIES OUTPUT_NAME "anode-test-sine" PREFIX "" SUFFIX ".clap")
if(APPLE)
  # A CLAP on macOS is a bundle: anode-test-sine.clap/Contents/MacOS/anode-test-sine.
  set_target_properties(an_testclap PROPERTIES BUNDLE TRUE BUNDLE_EXTENSION clap SUFFIX ""
    MACOSX_BUNDLE_GUI_IDENTIFIER org.anode.test-sine
    MACOSX_BUNDLE_BUNDLE_NAME "Anode Test Sine")
endif()
if(MSVC)
  target_compile_options(an_testclap PRIVATE /W4 /WX)
else()
  target_compile_options(an_testclap PRIVATE -Wall -Wextra -Werror -O2)
  if(NOT APPLE)
    target_link_libraries(an_testclap PRIVATE m)
  endif()
endif()
foreach(kind LIBRARY RUNTIME)
  set_target_properties(an_testclap PROPERTIES ${kind}_OUTPUT_DIRECTORY ${AN_CLAP_DIR})
  foreach(config Debug Release RelWithDebInfo MinSizeRel)
    string(TOUPPER ${config} upper)
    set_target_properties(an_testclap PROPERTIES ${kind}_OUTPUT_DIRECTORY_${upper} ${AN_CLAP_DIR})
  endforeach()
endforeach()
