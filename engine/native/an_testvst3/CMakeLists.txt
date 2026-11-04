# Anode's test VST3 plug-ins (src/an_testvst3.cpp): one bundle, AnodeTest.vst3, built into
# engine/artifacts/native/vst3/, which the plugin tests scan. Uses the VST3 SDK that an_plug pins.
set(AN_VST3_DIR ${CTH_ARTIFACTS_DIR}/vst3)
if(APPLE)
  add_library(an_testvst3 MODULE src/an_testvst3.cpp src/an_testvst3_view.mm ${AN_VST3_SDK}/public.sdk/source/main/macmain.cpp)
  target_link_libraries(an_testvst3 PRIVATE an_vst3_plugin "-framework AppKit")
  set_source_files_properties(src/an_testvst3_view.mm PROPERTIES COMPILE_OPTIONS -fobjc-arc)
  set_source_files_properties(${AN_VST3_SDK}/public.sdk/source/main/macmain.cpp PROPERTIES COMPILE_OPTIONS -w)
  target_compile_options(an_testvst3 PRIVATE -Wall -Wextra -Werror -O2 -Wno-unused-parameter)
  # A VST3 on macOS is a bundle: AnodeTest.vst3/Contents/MacOS/AnodeTest.
  set_target_properties(an_testvst3 PROPERTIES OUTPUT_NAME "AnodeTest" PREFIX "" SUFFIX "" BUNDLE TRUE
    BUNDLE_EXTENSION vst3 MACOSX_BUNDLE_GUI_IDENTIFIER org.anode.test-vst3 MACOSX_BUNDLE_BUNDLE_NAME "AnodeTest"
    CXX_VISIBILITY_PRESET hidden)
elseif(WIN32)
  # AnodeTest.vst3/Contents/x86_64-win/AnodeTest.vst3; no editor there yet.
  add_library(an_testvst3 MODULE src/an_testvst3.cpp src/an_testvst3_noview.cpp ${AN_VST3_SDK}/public.sdk/source/main/dllmain.cpp)
  target_link_libraries(an_testvst3 PRIVATE an_vst3_plugin)
  target_compile_definitions(an_testvst3 PRIVATE _CRT_SECURE_NO_WARNINGS)
  set_target_properties(an_testvst3 PROPERTIES OUTPUT_NAME "AnodeTest" PREFIX "" SUFFIX ".vst3")
  set(AN_VST3_DIR ${AN_VST3_DIR}/AnodeTest.vst3/Contents/x86_64-win)
else()
  # AnodeTest.vst3/Contents/<machine>-linux/AnodeTest.so; no editor there yet.
  add_library(an_testvst3 MODULE src/an_testvst3.cpp src/an_testvst3_noview.cpp ${AN_VST3_SDK}/public.sdk/source/main/linuxmain.cpp)
  target_link_libraries(an_testvst3 PRIVATE an_vst3_plugin dl pthread)
  target_compile_options(an_testvst3 PRIVATE -Wall -Wextra -Werror -O2 -Wno-unused-parameter)
  set_source_files_properties(${AN_VST3_SDK}/public.sdk/source/main/linuxmain.cpp PROPERTIES COMPILE_OPTIONS -w)
  set_target_properties(an_testvst3 PROPERTIES OUTPUT_NAME "AnodeTest" PREFIX "" SUFFIX ".so" CXX_VISIBILITY_PRESET hidden)
  set(AN_VST3_DIR ${AN_VST3_DIR}/AnodeTest.vst3/Contents/${CMAKE_SYSTEM_PROCESSOR}-linux)
endif()
foreach(kind LIBRARY RUNTIME)
  set_target_properties(an_testvst3 PROPERTIES ${kind}_OUTPUT_DIRECTORY ${AN_VST3_DIR})
  foreach(config Debug Release RelWithDebInfo MinSizeRel)
    string(TOUPPER ${config} upper)
    set_target_properties(an_testvst3 PROPERTIES ${kind}_OUTPUT_DIRECTORY_${upper} ${AN_VST3_DIR})
  endforeach()
endforeach()
