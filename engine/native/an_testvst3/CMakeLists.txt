# Anode's test VST3 plug-ins (src/an_testvst3.cpp): one bundle, AnodeTest.vst3, built into
# engine/artifacts/native/vst3/, which the plugin tests scan. Uses the VST3 SDK that an_plug pins.
if(NOT APPLE)
  message(STATUS "an_testvst3: only built on macOS for now (the editor view is Cocoa)")
  return()
endif()

set(AN_VST3_DIR ${CTH_ARTIFACTS_DIR}/vst3)
add_library(an_testvst3 MODULE src/an_testvst3.cpp src/an_testvst3_view.mm ${AN_VST3_SDK}/public.sdk/source/main/macmain.cpp)
target_link_libraries(an_testvst3 PRIVATE an_vst3_plugin "-framework AppKit")
set_source_files_properties(src/an_testvst3_view.mm PROPERTIES COMPILE_OPTIONS -fobjc-arc)
set_source_files_properties(${AN_VST3_SDK}/public.sdk/source/main/macmain.cpp PROPERTIES COMPILE_OPTIONS -w)
target_compile_options(an_testvst3 PRIVATE -Wall -Wextra -Werror -O2 -Wno-unused-parameter)
# A VST3 on macOS is a bundle: AnodeTest.vst3/Contents/MacOS/AnodeTest.
set_target_properties(an_testvst3 PROPERTIES OUTPUT_NAME "AnodeTest" PREFIX "" SUFFIX "" BUNDLE TRUE
  BUNDLE_EXTENSION vst3 MACOSX_BUNDLE_GUI_IDENTIFIER org.anode.test-vst3 MACOSX_BUNDLE_BUNDLE_NAME "AnodeTest"
  CXX_VISIBILITY_PRESET hidden)
foreach(kind LIBRARY RUNTIME)
  set_target_properties(an_testvst3 PROPERTIES ${kind}_OUTPUT_DIRECTORY ${AN_VST3_DIR})
  foreach(config Debug Release RelWithDebInfo MinSizeRel)
    string(TOUPPER ${config} upper)
    set_target_properties(an_testvst3 PROPERTIES ${kind}_OUTPUT_DIRECTORY_${upper} ${AN_VST3_DIR})
  endforeach()
endforeach()
