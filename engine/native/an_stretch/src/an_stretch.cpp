// SPDX-License-Identifier: GPL-3.0-or-later
#include "an_stretch.h"

#include <exception>
#include <memory>
#include <random>
#include <string>

#include "signalsmith-stretch/signalsmith-stretch.h"

#ifndef AN_STRETCH_UPSTREAM_VERSION
#define AN_STRETCH_UPSTREAM_VERSION "unknown"
#endif

namespace
{
constexpr long kSeed = 0x416e6f64; // fixed, so renders are reproducible

// The stretcher's random engine (it randomises phases when slowing down more than 2x). Upstream keeps its engine
// private and never reseeds it, so a reset stream would depend on everything processed before the reset. This one
// forwards to the engine of the instance being called (set by an_stretch::use), which reset and output_seek reseed: a stream
// started by either is a function of its input alone, as Anode's flex renderers need (docs/flex/README.md).
struct Random
{
  using result_type = std::minstd_rand::result_type;
  static constexpr result_type min() { return std::minstd_rand::min(); }
  static constexpr result_type max() { return std::minstd_rand::max(); }
  explicit Random(long) {}
  result_type operator()() { return (*current)(); }
  static thread_local std::minstd_rand* current;
};
thread_local std::minstd_rand* Random::current = nullptr;

using Stretch = signalsmith::stretch::SignalsmithStretch<float, Random>;

thread_local std::string last_error;

int32_t fail(const std::string& message)
{
  last_error = message;
  return -1;
}
} // namespace

struct an_stretch
{
  Stretch stretch{kSeed};
  std::minstd_rand random{static_cast<std::minstd_rand::result_type>(kSeed)};
  double sample_rate = 0;

  // Points the stretcher's random engine at this instance's for the duration of a call.
  Stretch& use()
  {
    Random::current = &random;
    return stretch;
  }
};

namespace
{
template <class Configure>
int32_t create(int32_t channels, double sample_rate, an_stretch** result, Configure configure)
{
  if (!result)
    return fail("invalid arguments");
  *result = nullptr;
  if (channels < 1 || channels > 64 || !(sample_rate >= 8000 && sample_rate <= 768000))
    return fail("channels must be 1..64 and the sample rate 8 kHz..768 kHz");
  try
  {
    auto s = std::make_unique<an_stretch>();
    s->sample_rate = sample_rate;
    configure(*s);
    *result = s.release();
    return 0;
  }
  catch (const std::exception& e)
  {
    return fail(e.what());
  }
}
} // namespace

extern "C" {

int32_t an_stretch_create(int32_t channels, double sample_rate, int32_t preset, int32_t split_computation,
                          an_stretch** stretch)
{
  if (preset != AN_STRETCH_PRESET_DEFAULT && preset != AN_STRETCH_PRESET_CHEAPER)
    return fail("unknown preset");
  return create(channels, sample_rate, stretch, [=](an_stretch& s) {
    const auto rate = static_cast<float>(s.sample_rate);
    if (preset == AN_STRETCH_PRESET_CHEAPER)
      s.stretch.presetCheaper(channels, rate, split_computation != 0);
    else
      s.stretch.presetDefault(channels, rate, split_computation != 0);
  });
}

int32_t an_stretch_create_custom(int32_t channels, double sample_rate, int32_t block_frames, int32_t interval_frames,
                                 int32_t split_computation, an_stretch** stretch)
{
  if (block_frames < 64 || interval_frames < 16 || interval_frames > block_frames)
    return fail("block must be >= 64 frames and the interval 16..block frames");
  return create(channels, sample_rate, stretch, [=](an_stretch& s) {
    s.stretch.configure(channels, block_frames, interval_frames, split_computation != 0);
  });
}

void an_stretch_destroy(an_stretch* stretch)
{
  delete stretch;
}

void an_stretch_reset(an_stretch* stretch)
{
  stretch->random.seed(static_cast<std::minstd_rand::result_type>(kSeed));
  stretch->stretch.reset();
}

void an_stretch_set_transpose_factor(an_stretch* stretch, double factor, double tonality_limit_hz)
{
  stretch->stretch.setTransposeFactor(static_cast<float>(factor),
                                      static_cast<float>(tonality_limit_hz / stretch->sample_rate));
}

void an_stretch_set_transpose_semitones(an_stretch* stretch, double semitones, double tonality_limit_hz)
{
  stretch->stretch.setTransposeSemitones(static_cast<float>(semitones),
                                         static_cast<float>(tonality_limit_hz / stretch->sample_rate));
}

void an_stretch_set_formant_factor(an_stretch* stretch, double factor, int32_t compensate_pitch)
{
  stretch->stretch.setFormantFactor(static_cast<float>(factor), compensate_pitch != 0);
}

void an_stretch_set_formant_base(an_stretch* stretch, double base_hz)
{
  stretch->stretch.setFormantBase(static_cast<float>(base_hz / stretch->sample_rate));
}

void an_stretch_process(an_stretch* stretch, const float* const* input, int32_t input_frames, float* const* output,
                        int32_t output_frames)
{
  stretch->use().process(input, input_frames, output, output_frames);
}

void an_stretch_seek(an_stretch* stretch, const float* const* input, int32_t input_frames, double playback_rate)
{
  stretch->use().seek(input, input_frames, playback_rate);
}

int32_t an_stretch_seek_length(const an_stretch* stretch)
{
  return stretch->stretch.seekLength();
}

void an_stretch_output_seek(an_stretch* stretch, const float* const* input, int32_t input_frames)
{
  stretch->random.seed(static_cast<std::minstd_rand::result_type>(kSeed));
  stretch->use().outputSeek(input, input_frames);
}

int32_t an_stretch_output_seek_length(const an_stretch* stretch, double playback_rate)
{
  return stretch->stretch.outputSeekLength(static_cast<float>(playback_rate));
}

void an_stretch_flush(an_stretch* stretch, float* const* output, int32_t output_frames, double playback_rate)
{
  stretch->use().flush(output, output_frames, static_cast<float>(playback_rate));
}

int32_t an_stretch_exact(an_stretch* stretch, const float* const* input, int32_t input_frames, float* const* output,
                         int32_t output_frames)
{
  if (output_frames < 1)
    return fail("output_frames must be positive");
  try
  {
    stretch->random.seed(static_cast<std::minstd_rand::result_type>(kSeed));
    return stretch->use().exact(input, input_frames, output, output_frames) ? 0
                                                                               : fail("input shorter than a seek");
  }
  catch (const std::exception& e)
  {
    return fail(e.what());
  }
}

int32_t an_stretch_input_latency(const an_stretch* stretch)
{
  return stretch->stretch.inputLatency();
}

int32_t an_stretch_output_latency(const an_stretch* stretch)
{
  return stretch->stretch.outputLatency();
}

int32_t an_stretch_block_frames(const an_stretch* stretch)
{
  return stretch->stretch.blockSamples();
}

int32_t an_stretch_interval_frames(const an_stretch* stretch)
{
  return stretch->stretch.intervalSamples();
}

const char* an_stretch_version(void)
{
  return AN_STRETCH_UPSTREAM_VERSION;
}

const char* an_stretch_last_error(void)
{
  return last_error.c_str();
}
}
