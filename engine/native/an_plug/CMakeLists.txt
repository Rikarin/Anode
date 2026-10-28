# an_plug: a C ABI over the VST3 SDK, AudioUnits (macOS) and CLAP, for the anode-plugin sandbox and the scanner
# (docs/adr/0003-plugin-hosting.md, docs/05-phase-4.md workstream E). See include/an_plug.h.

include(FetchContent)

# Pinned: the VST3 SDK v3.8.0_build_66 (MIT), as its three submodules (the SDK's own repository only links them).
# build.sh fetches these commits into engine/artifacts/deps and points FETCHCONTENT_SOURCE_DIR_* there.
FetchContent_Declare(vst3_base
  GIT_REPOSITORY https://github.com/steinbergmedia/vst3_base.git
  GIT_TAG 3d2e82f8e6bff59c1d8b7a27491a29c2286b5206 # v3.8.0_build_66
  SOURCE_SUBDIR _anode_does_not_use_upstream_cmake)
FetchContent_Declare(vst3_pluginterfaces
  GIT_REPOSITORY https://github.com/steinbergmedia/vst3_pluginterfaces.git
  GIT_TAG 31d6eeba6daaa3e2a8bfbe3e7a90ca0b7fbfbc1c # v3.8.0_build_66
  SOURCE_SUBDIR _anode_does_not_use_upstream_cmake)
FetchContent_Declare(vst3_public_sdk
  GIT_REPOSITORY https://github.com/steinbergmedia/vst3_public_sdk.git
  GIT_TAG a3911a4615dabbfdfd9d181ee26b05c70c289a95 # v3.8.0_build_66
  SOURCE_SUBDIR _anode_does_not_use_upstream_cmake)
FetchContent_MakeAvailable(vst3_base vst3_pluginterfaces vst3_public_sdk)

# The SDK's sources include each other from its root ("base/...", "pluginterfaces/...", "public.sdk/..."): lay the
# three folders out as the SDK's own repository does.
set(AN_VST3_SDK ${CMAKE_CURRENT_BINARY_DIR}/vst3sdk CACHE INTERNAL "")
file(MAKE_DIRECTORY ${AN_VST3_SDK})
file(CREATE_LINK ${vst3_base_SOURCE_DIR} ${AN_VST3_SDK}/base SYMBOLIC)
file(CREATE_LINK ${vst3_pluginterfaces_SOURCE_DIR} ${AN_VST3_SDK}/pluginterfaces SYMBOLIC)
file(CREATE_LINK ${vst3_public_sdk_SOURCE_DIR} ${AN_VST3_SDK}/public.sdk SYMBOLIC)
set(S ${AN_VST3_SDK})

# What both hosts and plug-ins need (the SDK's "sdk_common" plus the interface ids).
add_library(an_vst3_common STATIC
  ${S}/base/source/baseiids.cpp ${S}/base/source/fbuffer.cpp ${S}/base/source/fdebug.cpp
  ${S}/base/source/fdynlib.cpp ${S}/base/source/fobject.cpp ${S}/base/source/fstreamer.cpp
  ${S}/base/source/fstring.cpp ${S}/base/source/timer.cpp ${S}/base/source/updatehandler.cpp
  ${S}/base/thread/source/fcondition.cpp ${S}/base/thread/source/flock.cpp
  ${S}/pluginterfaces/base/conststringtable.cpp ${S}/pluginterfaces/base/coreiids.cpp
  ${S}/pluginterfaces/base/funknown.cpp ${S}/pluginterfaces/base/ustring.cpp
  ${S}/public.sdk/source/common/commoniids.cpp ${S}/public.sdk/source/common/memorystream.cpp
  ${S}/public.sdk/source/common/commonstringconvert.cpp ${S}/public.sdk/source/vst/utility/stringconvert.cpp
  ${S}/public.sdk/source/vst/vstinitiids.cpp)
target_include_directories(an_vst3_common SYSTEM PUBLIC ${S})
target_compile_definitions(an_vst3_common PUBLIC RELEASE=1)
target_compile_options(an_vst3_common PRIVATE -w)
if(APPLE)
  target_link_libraries(an_vst3_common PUBLIC "-framework CoreFoundation")
endif()

# The hosting side, for an_plug.
set(AN_VST3_HOSTING
  ${S}/public.sdk/source/vst/hosting/module.cpp ${S}/public.sdk/source/vst/hosting/hostclasses.cpp
  ${S}/public.sdk/source/vst/hosting/parameterchanges.cpp ${S}/public.sdk/source/vst/hosting/eventlist.cpp
  ${S}/public.sdk/source/vst/hosting/connectionproxy.cpp ${S}/public.sdk/source/vst/hosting/pluginterfacesupport.cpp)
if(APPLE)
  list(APPEND AN_VST3_HOSTING ${S}/public.sdk/source/vst/hosting/module_mac.mm
    ${S}/public.sdk/source/common/threadchecker_mac.mm)
elseif(WIN32)
  list(APPEND AN_VST3_HOSTING ${S}/public.sdk/source/vst/hosting/module_win32.cpp
    ${S}/public.sdk/source/common/threadchecker_win32.cpp)
else()
  list(APPEND AN_VST3_HOSTING ${S}/public.sdk/source/vst/hosting/module_linux.cpp
    ${S}/public.sdk/source/common/threadchecker_linux.cpp)
endif()
add_library(an_vst3_hosting STATIC ${AN_VST3_HOSTING})
target_link_libraries(an_vst3_hosting PUBLIC an_vst3_common)
target_compile_options(an_vst3_hosting PRIVATE -w)
if(APPLE)
  set_source_files_properties(${S}/public.sdk/source/vst/hosting/module_mac.mm
    ${S}/public.sdk/source/common/threadchecker_mac.mm PROPERTIES COMPILE_OPTIONS -fobjc-arc)
endif()

# The plug-in side, for the test plug-ins (an_testvst3).
add_library(an_vst3_plugin STATIC
  ${S}/public.sdk/source/vst/vstaudioeffect.cpp ${S}/public.sdk/source/vst/vstbus.cpp
  ${S}/public.sdk/source/vst/vstcomponent.cpp ${S}/public.sdk/source/vst/vstcomponentbase.cpp
  ${S}/public.sdk/source/vst/vsteditcontroller.cpp ${S}/public.sdk/source/vst/vstparameters.cpp
  ${S}/public.sdk/source/main/pluginfactory.cpp ${S}/public.sdk/source/common/pluginview.cpp)
target_link_libraries(an_vst3_plugin PUBLIC an_vst3_common)
target_compile_options(an_vst3_plugin PRIVATE -w)

# The shim.
set(AN_PLUG_SOURCES src/an_plug.cpp src/an_plug_vst3.cpp)
if(APPLE)
  list(APPEND AN_PLUG_SOURCES src/an_plug_mac.mm src/an_plug_au.mm)
else()
  list(APPEND AN_PLUG_SOURCES src/an_plug_stub.cpp)
endif()
add_library(an_plug SHARED ${AN_PLUG_SOURCES})
target_include_directories(an_plug PUBLIC include PRIVATE src)
target_link_libraries(an_plug PRIVATE an_vst3_hosting)
target_compile_options(an_plug PRIVATE -Wall -Wextra -Werror -O2)
if(APPLE)
  set_source_files_properties(src/an_plug_mac.mm src/an_plug_au.mm PROPERTIES COMPILE_OPTIONS -fobjc-arc)
  target_link_libraries(an_plug PRIVATE "-framework AppKit" "-framework AudioToolbox" "-framework AudioUnit"
    "-framework CoreAudio" "-framework CoreAudioKit" "-framework CoreGraphics" "-framework CoreFoundation")
elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  target_link_libraries(an_plug PRIVATE dl pthread)
endif()

cth_output_to_artifacts(an_plug)
