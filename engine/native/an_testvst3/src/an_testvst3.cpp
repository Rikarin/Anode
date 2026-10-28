/*
 * Anode's test VST3 plug-ins (docs/05-phase-4.md, workstream E), built over the VST3 SDK (MIT) into
 * engine/artifacts/native/vst3/AnodeTest.vst3. One bundle, four classes:
 *
 *   Anode Test Gain       a gain (-24..+12 dB) and a one-pole low-pass "Tone" (fully open at 1), a state and a
 *                         minimal editor (two sliders, an_testvst3_view.mm). The effect the tests and the golden path
 *                         use.
 *   Anode Test Delay      a 64-sample delay that reports its latency, plus a stereo sidechain added at "Sidechain".
 *   Anode Test Misbehave  passes audio through until "Crash" is on (it then crashes the process) or "Stall" is
 *                         above 0 (it then sleeps Stall x 100 ms per block): for crash isolation and deadline tests.
 *   Anode Test Synth      a sine per note (an instrument), at "Level".
 *
 * A copy of the bundle whose name contains "CrashOnScan" or "HangOnScan" crashes or hangs as soon as it is loaded.
 *
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include "an_testvst3.h"

#include "base/source/fstreamer.h"
#include "pluginterfaces/base/ibstream.h"
#include "pluginterfaces/vst/ivstevents.h"
#include "pluginterfaces/vst/ivstparameterchanges.h"
#include "public.sdk/source/main/pluginfactory.h"
#include "public.sdk/source/vst/vstaudioeffect.h"
#include "public.sdk/source/vst/utility/stringconvert.h"

#include <cmath>
#include <cstring>
#include <unistd.h>

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace antest {

// Class ids (stable: projects and the tests name them).
static const FUID GainProcessorUID(0x414E4F44, 0x45544731, 0x00000001, 0x00000001);
static const FUID GainControllerUID(0x414E4F44, 0x45544731, 0x00000001, 0x00000002);
static const FUID DelayProcessorUID(0x414E4F44, 0x45544731, 0x00000002, 0x00000001);
static const FUID DelayControllerUID(0x414E4F44, 0x45544731, 0x00000002, 0x00000002);
static const FUID MisbehaveProcessorUID(0x414E4F44, 0x45544731, 0x00000003, 0x00000001);
static const FUID MisbehaveControllerUID(0x414E4F44, 0x45544731, 0x00000003, 0x00000002);
static const FUID SynthProcessorUID(0x414E4F44, 0x45544731, 0x00000004, 0x00000001);
static const FUID SynthControllerUID(0x414E4F44, 0x45544731, 0x00000004, 0x00000002);

const ParamSpec *specs(Kind kind, int32 &count) {
    static const ParamSpec gain[] = {{0, "Gain", "dB", 24.0 / 36.0, 0}, {1, "Tone", "", 1.0, 0}};
    static const ParamSpec delay[] = {{0, "Sidechain", "", 1.0, 0}};
    static const ParamSpec misbehave[] = {{0, "Crash", "", 0.0, 1}, {1, "Stall", "", 0.0, 0}};
    static const ParamSpec synth[] = {{0, "Level", "", 0.5, 0}};
    switch (kind) {
        case Kind::Gain: count = 2; return gain;
        case Kind::Delay: count = 1; return delay;
        case Kind::Misbehave: count = 2; return misbehave;
        case Kind::Synth: count = 1; return synth;
    }
    count = 0;
    return nullptr;
}

constexpr int32 kDelaySamples = 64;
constexpr int32 kVoices = 16;
constexpr int32 kStateVersion = 1;

class Processor : public AudioEffect {
public:
    explicit Processor(Kind kind) : kind(kind) {
        int32 n = 0;
        auto *s = specs(kind, n);
        for (int32 i = 0; i < n; i++) {
            values[i] = s[i].def;
        }
        switch (kind) {
            case Kind::Gain: setControllerClass(GainControllerUID); break;
            case Kind::Delay: setControllerClass(DelayControllerUID); break;
            case Kind::Misbehave: setControllerClass(MisbehaveControllerUID); break;
            case Kind::Synth: setControllerClass(SynthControllerUID); break;
        }
    }

    tresult PLUGIN_API initialize(FUnknown *context) SMTG_OVERRIDE {
        auto r = AudioEffect::initialize(context);
        if (r != kResultOk) {
            return r;
        }
        if (kind != Kind::Synth) {
            addAudioInput(STR16("Stereo In"), SpeakerArr::kStereo);
        }
        if (kind == Kind::Delay) {
            addAudioInput(STR16("Sidechain"), SpeakerArr::kStereo, kAux, 0);
        }
        addAudioOutput(STR16("Stereo Out"), SpeakerArr::kStereo);
        if (kind == Kind::Synth) {
            addEventInput(STR16("Events"), 1);
        }
        return kResultOk;
    }

    tresult PLUGIN_API setBusArrangements(SpeakerArrangement *inputs, int32 numIns, SpeakerArrangement *outputs,
                                          int32 numOuts) SMTG_OVERRIDE {
        for (int32 i = 0; i < numIns; i++) {
            if (inputs[i] != SpeakerArr::kStereo) {
                return kResultFalse;
            }
        }
        for (int32 i = 0; i < numOuts; i++) {
            if (outputs[i] != SpeakerArr::kStereo) {
                return kResultFalse;
            }
        }
        return AudioEffect::setBusArrangements(inputs, numIns, outputs, numOuts);
    }

    tresult PLUGIN_API canProcessSampleSize(int32 size) SMTG_OVERRIDE {
        return size == kSample32 ? kResultTrue : kResultFalse;
    }

    uint32 PLUGIN_API getLatencySamples() SMTG_OVERRIDE { return kind == Kind::Delay ? kDelaySamples : 0; }

    tresult PLUGIN_API setupProcessing(ProcessSetup &setup) SMTG_OVERRIDE {
        sampleRate = setup.sampleRate;
        return AudioEffect::setupProcessing(setup);
    }

    tresult PLUGIN_API setActive(TBool state) SMTG_OVERRIDE {
        clear();
        return AudioEffect::setActive(state);
    }

    tresult PLUGIN_API setProcessing(TBool state) SMTG_OVERRIDE {
        if (!state) {
            clear();
        }
        return kResultOk;
    }

    tresult PLUGIN_API process(ProcessData &data) SMTG_OVERRIDE {
        if (auto *changes = data.inputParameterChanges) {
            for (int32 i = 0; i < changes->getParameterCount(); i++) {
                auto *queue = changes->getParameterData(i);
                if (!queue || queue->getPointCount() <= 0) {
                    continue;
                }
                int32 offset;
                ParamValue v;
                if (queue->getPoint(queue->getPointCount() - 1, offset, v) == kResultOk &&
                    queue->getParameterId() < 2) {
                    values[queue->getParameterId()] = v;
                }
            }
        }
        if (kind == Kind::Synth && data.inputEvents) {
            // Events apply at the block start: enough for a test instrument.
            for (int32 i = 0; i < data.inputEvents->getEventCount(); i++) {
                Event e{};
                if (data.inputEvents->getEvent(i, e) != kResultOk) {
                    continue;
                }
                if (e.type == Event::kNoteOnEvent) {
                    auto &voice = voices[nextVoice++ % kVoices];
                    voice.pitch = e.noteOn.pitch;
                    voice.velocity = e.noteOn.velocity;
                    voice.phase = 0;
                    voice.on = true;
                } else if (e.type == Event::kNoteOffEvent) {
                    for (auto &voice : voices) {
                        if (voice.on && voice.pitch == e.noteOff.pitch) {
                            voice.on = false;
                        }
                    }
                }
            }
        }
        if (data.numOutputs < 1 || data.numSamples <= 0) {
            return kResultOk;
        }
        auto frames = data.numSamples;
        float **out = data.outputs[0].channelBuffers32;
        float *const *in = data.numInputs > 0 ? data.inputs[0].channelBuffers32 : nullptr;
        switch (kind) {
            case Kind::Gain: {
                auto gain = static_cast<float>(std::pow(10.0, (-24.0 + 36.0 * values[0]) / 20.0));
                auto open = values[1] >= 1.0;
                auto fc = 20.0 * std::pow(1000.0, values[1]);
                auto a = static_cast<float>(1.0 - std::exp(-2.0 * M_PI * fc / sampleRate));
                for (int c = 0; c < 2; c++) {
                    for (int32 i = 0; i < frames; i++) {
                        auto x = in ? in[c][i] : 0.0f;
                        if (!open) {
                            z[c] += a * (x - z[c]);
                            x = z[c];
                        }
                        out[c][i] = x * gain;
                    }
                }
                break;
            }
            case Kind::Delay: {
                float *const *sc = data.numInputs > 1 && data.inputs[1].numChannels == 2
                                       ? data.inputs[1].channelBuffers32
                                       : nullptr;
                auto level = static_cast<float>(values[0]);
                for (int c = 0; c < 2; c++) {
                    auto w = writePos;
                    for (int32 i = 0; i < frames; i++) {
                        auto x = in ? in[c][i] : 0.0f;
                        auto y = line[c][w];
                        line[c][w] = x;
                        w = (w + 1) % kDelaySamples;
                        out[c][i] = y + (sc ? sc[c][i] * level : 0.0f);
                    }
                }
                writePos = (writePos + frames) % kDelaySamples;
                break;
            }
            case Kind::Misbehave: {
                if (values[0] >= 0.5) {
                    volatile int *crash = nullptr;
                    *crash = 1; // deliberately: crash isolation tests
                }
                if (values[1] > 0) {
                    usleep(static_cast<useconds_t>(values[1] * 100000));
                }
                for (int c = 0; c < 2; c++) {
                    if (in && in[c] != out[c]) {
                        std::memcpy(out[c], in[c], sizeof(float) * static_cast<size_t>(frames));
                    } else if (!in) {
                        std::memset(out[c], 0, sizeof(float) * static_cast<size_t>(frames));
                    }
                }
                break;
            }
            case Kind::Synth: {
                std::memset(out[0], 0, sizeof(float) * static_cast<size_t>(frames));
                std::memset(out[1], 0, sizeof(float) * static_cast<size_t>(frames));
                auto level = static_cast<float>(values[0]);
                for (auto &voice : voices) {
                    if (!voice.on) {
                        continue;
                    }
                    auto step = 2.0 * M_PI * 440.0 * std::pow(2.0, (voice.pitch - 69) / 12.0) / sampleRate;
                    for (int32 i = 0; i < frames; i++) {
                        auto s = static_cast<float>(std::sin(voice.phase)) * voice.velocity * level * 0.25f;
                        out[0][i] += s;
                        out[1][i] += s;
                        voice.phase += step;
                    }
                    voice.phase = std::fmod(voice.phase, 2.0 * M_PI);
                }
                break;
            }
        }
        return kResultOk;
    }

    tresult PLUGIN_API getState(IBStream *state) SMTG_OVERRIDE {
        IBStreamer s(state, kLittleEndian);
        s.writeInt32(kStateVersion);
        s.writeDouble(values[0]);
        s.writeDouble(values[1]);
        return kResultOk;
    }

    tresult PLUGIN_API setState(IBStream *state) SMTG_OVERRIDE {
        IBStreamer s(state, kLittleEndian);
        int32 version = 0;
        if (!s.readInt32(version) || version != kStateVersion) {
            return kResultFalse;
        }
        double a = 0, b = 0;
        if (!s.readDouble(a) || !s.readDouble(b)) {
            return kResultFalse;
        }
        values[0] = a;
        values[1] = b;
        return kResultOk;
    }

private:
    void clear() {
        z[0] = z[1] = 0;
        std::memset(line, 0, sizeof line);
        writePos = 0;
        for (auto &voice : voices) {
            voice.on = false;
        }
    }

    struct Voice {
        bool on = false;
        int16 pitch = 0;
        float velocity = 0;
        double phase = 0;
    };

    Kind kind;
    double values[2] = {0, 0};
    double sampleRate = 48000;
    float z[2] = {0, 0};
    float line[2][kDelaySamples] = {};
    int32 writePos = 0;
    Voice voices[kVoices];
    uint32 nextVoice = 0;
};

tresult PLUGIN_API Controller::initialize(FUnknown *context) {
    auto r = EditController::initialize(context);
    if (r != kResultOk) {
        return r;
    }
    int32 n = 0;
    auto *s = specs(kind, n);
    for (int32 i = 0; i < n; i++) {
        String128 title{}, unit{};
        StringConvert::convert(s[i].name, title);
        StringConvert::convert(s[i].unit, unit);
        parameters.addParameter(title, unit, s[i].steps, s[i].def, ParameterInfo::kCanAutomate, s[i].id);
    }
    return kResultOk;
}

tresult PLUGIN_API Controller::setComponentState(IBStream *state) {
    IBStreamer s(state, kLittleEndian);
    int32 version = 0;
    if (!s.readInt32(version) || version != kStateVersion) {
        return kResultFalse;
    }
    double a = 0, b = 0;
    if (!s.readDouble(a) || !s.readDouble(b)) {
        return kResultFalse;
    }
    int32 n = 0;
    specs(kind, n);
    if (n > 0) {
        setParamNormalized(0, a);
    }
    if (n > 1) {
        setParamNormalized(1, b);
    }
    return kResultOk;
}

tresult PLUGIN_API Controller::setParamNormalized(ParamID id, ParamValue value) {
    auto r = EditController::setParamNormalized(id, value);
    if (view) {
        view_update(view, static_cast<int32>(id), value);
    }
    return r;
}

IPlugView *PLUGIN_API Controller::createView(FIDString name) {
    if (kind != Kind::Gain || std::strcmp(name, ViewType::kEditor) != 0) {
        return nullptr;
    }
    return create_view(this);
}

void Controller::edit(int32 id, double value) {
    beginEdit(static_cast<ParamID>(id));
    EditController::setParamNormalized(static_cast<ParamID>(id), value);
    performEdit(static_cast<ParamID>(id), value);
    endEdit(static_cast<ParamID>(id));
}

template <Kind K>
FUnknown *createProcessor(void *) {
    return static_cast<IAudioProcessor *>(new Processor(K));
}

template <Kind K>
FUnknown *createController(void *) {
    return static_cast<IEditController *>(new Controller(K));
}

} // namespace antest

using namespace antest;

extern char gPath[]; // the bundle's path (macmain.cpp)

// A copy of the bundle renamed to contain "CrashOnScan" or "HangOnScan" misbehaves as soon as it is loaded: the
// scanner tests' blocklist cases.
bool InitModule() {
    if (std::strstr(gPath, "CrashOnScan")) {
        volatile int *crash = nullptr;
        *crash = 1;
    }
    if (std::strstr(gPath, "HangOnScan")) {
        for (;;) {
            sleep(1);
        }
    }
    return true;
}
bool DeinitModule() { return true; }

#define AN_TEST_CLASS(name, cats, proc, ctrl, kind)                                                                  \
    DEF_CLASS2(INLINE_UID_FROM_FUID(proc), PClassInfo::kManyInstances, kVstAudioEffectClass, name,                  \
               Vst::kDistributable, cats, "1.0.0", kVstVersionString, createProcessor<kind>)                         \
    DEF_CLASS2(INLINE_UID_FROM_FUID(ctrl), PClassInfo::kManyInstances, kVstComponentControllerClass,                 \
               name " Controller", 0, "", "1.0.0", kVstVersionString, createController<kind>)

BEGIN_FACTORY_DEF("Anode", "https://github.com/anode-daw", "mailto:test@anode.invalid")
AN_TEST_CLASS("Anode Test Gain", "Fx|Tools", GainProcessorUID, GainControllerUID, Kind::Gain)
AN_TEST_CLASS("Anode Test Delay", "Fx|Delay", DelayProcessorUID, DelayControllerUID, Kind::Delay)
AN_TEST_CLASS("Anode Test Misbehave", "Fx|Tools", MisbehaveProcessorUID, MisbehaveControllerUID, Kind::Misbehave)
AN_TEST_CLASS("Anode Test Synth", "Instrument|Synth", SynthProcessorUID, SynthControllerUID, Kind::Synth)
END_FACTORY
