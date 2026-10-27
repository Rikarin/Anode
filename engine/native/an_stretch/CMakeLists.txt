# Time stretch and pitch shift (C++), wrapping Signalsmith Stretch behind a C ABI. See include/an_stretch.h and
# docs/spikes/S5-time-stretch.md.

include(FetchContent)

# Pinned: bump deliberately and re-run the S5 spike (engine/spikes/Anode.Spikes.Stretch/run.sh). Both are MIT and
# header-only. Upstream's own CMakeLists is not used: it fetches Linear by a moving tag, shallow; build.sh fetches both
# pins into engine/artifacts/deps instead.
FetchContent_Declare(signalsmith_stretch
  GIT_REPOSITORY https://github.com/Signalsmith-Audio/signalsmith-stretch.git
  GIT_TAG 88061fd4eda56a2353c5d65c74aa3f4ccf76f9da # 1.4.0 + "Bump Linear version" (2026-10-22)
  SOURCE_SUBDIR _anode_does_not_use_upstream_cmake)
FetchContent_Declare(signalsmith_linear
  GIT_REPOSITORY https://github.com/Signalsmith-Audio/linear.git
  GIT_TAG 7bb1c8f36794d6980080ce817c91f8da8fe56a66 # 0.6.6, the version stretch's main pins
  SOURCE_SUBDIR _anode_does_not_use_upstream_cmake)
FetchContent_MakeAvailable(signalsmith_stretch signalsmith_linear)
set(AN_STRETCH_VERSION "signalsmith-stretch 1.4.0+88061fd, linear 0.6.6")

# Upstream's FFT choice: Accelerate on Apple, its own FFT elsewhere. AN_STRETCH_NOACCEL also builds an_stretch_noaccel
# (Linear's portable FFT) on Apple, so the spike can measure what Windows and Linux will get.
option(AN_STRETCH_NOACCEL "Also build an_stretch_noaccel, without Accelerate (Apple only)" ON)

function(an_stretch_library target accelerate)
  add_library(${target} SHARED src/an_stretch.cpp)
  target_include_directories(${target} PUBLIC include)
  # Third-party headers: SYSTEM, so our warning flags apply to the shim only.
  target_include_directories(${target} SYSTEM PRIVATE
    ${signalsmith_stretch_SOURCE_DIR}/include ${signalsmith_linear_SOURCE_DIR}/include)
  if(accelerate)
    target_compile_definitions(${target} PRIVATE SIGNALSMITH_USE_ACCELERATE ACCELERATE_NEW_LAPACK
      AN_STRETCH_UPSTREAM_VERSION="${AN_STRETCH_VERSION}, accelerate")
    target_link_libraries(${target} PRIVATE "-framework Accelerate")
  else()
    target_compile_definitions(${target} PRIVATE AN_STRETCH_UPSTREAM_VERSION="${AN_STRETCH_VERSION}, linear fft")
  endif()
  if(MSVC)
    target_compile_options(${target} PRIVATE /W4 /WX /bigobj)
  else()
    target_compile_options(${target} PRIVATE -Wall -Wextra -Werror $<$<CONFIG:Release>:-O3>)
  endif()
  cth_output_to_artifacts(${target})
endfunction()

if(APPLE)
  an_stretch_library(an_stretch ON)
  if(AN_STRETCH_NOACCEL)
    an_stretch_library(an_stretch_noaccel OFF)
  endif()
else()
  an_stretch_library(an_stretch OFF)
endif()
