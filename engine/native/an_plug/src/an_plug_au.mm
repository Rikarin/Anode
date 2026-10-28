/*
 * an_plug: Audio Units (v2, and v3 through the v2 bridge) over AudioToolbox. SPDX-License-Identifier: GPL-3.0-or-later
 */
#include "an_plug_internal.h"

#import <AppKit/AppKit.h>
#import <AudioToolbox/AudioToolbox.h>
#import <CoreAudioKit/CoreAudioKit.h>

#include <cmath>
#include <cstring>

/* AUCocoaUIBase's factory method (the SDK's header hides the protocol behind AudioToolboxCore). */
@protocol AnCocoaViewFactory <NSObject>
- (NSView *)uiViewForAudioUnit:(AudioUnit)unit withSize:(NSSize)size;
@end

namespace anplug {

namespace {

std::string fourcc(OSType v) {
    char c[4] = {static_cast<char>(v >> 24), static_cast<char>(v >> 16), static_cast<char>(v >> 8),
                 static_cast<char>(v)};
    bool printable = true;
    for (char ch : c) {
        printable = printable && ch >= 0x20 && ch < 0x7F && ch != '.';
    }
    if (printable) {
        return std::string(c, 4);
    }
    char buf[16];
    snprintf(buf, sizeof buf, "0x%08X", static_cast<unsigned>(v));
    return buf;
}

bool parseFourcc(const std::string &s, OSType &v) {
    if (s.size() == 4) {
        v = (static_cast<OSType>(static_cast<uint8_t>(s[0])) << 24) |
            (static_cast<OSType>(static_cast<uint8_t>(s[1])) << 16) |
            (static_cast<OSType>(static_cast<uint8_t>(s[2])) << 8) | static_cast<OSType>(static_cast<uint8_t>(s[3]));
        return true;
    }
    if (s.size() == 10 && s[0] == '0' && s[1] == 'x') {
        v = static_cast<OSType>(strtoul(s.c_str() + 2, nullptr, 16));
        return true;
    }
    return false;
}

std::string componentId(const AudioComponentDescription &d) {
    return fourcc(d.componentType) + "." + fourcc(d.componentSubType) + "." + fourcc(d.componentManufacturer);
}

bool parseId(const std::string &id, AudioComponentDescription &d) {
    auto a = id.find('.');
    auto b = a == std::string::npos ? a : id.find('.', a + 1);
    if (b == std::string::npos) {
        return false;
    }
    d = {};
    return parseFourcc(id.substr(0, a), d.componentType) && parseFourcc(id.substr(a + 1, b - a - 1), d.componentSubType) &&
           parseFourcc(id.substr(b + 1), d.componentManufacturer);
}

std::string cfString(CFStringRef s) {
    if (!s) {
        return {};
    }
    char buf[1024];
    if (CFStringGetCString(s, buf, sizeof buf, kCFStringEncodingUTF8)) {
        return buf;
    }
    return {};
}

void describeComponent(AudioComponent comp, Plugin &p) {
    AudioComponentDescription d{};
    AudioComponentGetDescription(comp, &d);
    p.format = AN_PLUG_AU;
    p.id = componentId(d);
    CFStringRef name = nullptr;
    AudioComponentCopyName(comp, &name);
    auto full = cfString(name);
    if (name) {
        CFRelease(name);
    }
    auto colon = full.find(": ");
    if (colon != std::string::npos) {
        p.vendor = full.substr(0, colon);
        p.name = full.substr(colon + 2);
    } else {
        p.name = full;
        p.vendor = fourcc(d.componentManufacturer);
    }
    UInt32 version = 0;
    AudioComponentGetVersion(comp, &version);
    p.version = std::to_string(version >> 16) + "." + std::to_string((version >> 8) & 0xFF) + "." +
                std::to_string(version & 0xFF);
    p.instrument = d.componentType == kAudioUnitType_MusicDevice;
    if (!p.instrument) {
        switch (d.componentSubType) {
            case kAudioUnitSubType_Delay:
            case kAudioUnitSubType_SampleDelay: p.category = "Delay"; break;
            case kAudioUnitSubType_LowPassFilter:
            case kAudioUnitSubType_HighPassFilter:
            case kAudioUnitSubType_BandPassFilter: p.category = "Filter"; break;
            case kAudioUnitSubType_HighShelfFilter:
            case kAudioUnitSubType_LowShelfFilter:
            case kAudioUnitSubType_ParametricEQ:
            case kAudioUnitSubType_GraphicEQ:
            case kAudioUnitSubType_NBandEQ: p.category = "EQ"; break;
            case kAudioUnitSubType_DynamicsProcessor:
            case kAudioUnitSubType_MultiBandCompressor:
            case kAudioUnitSubType_PeakLimiter: p.category = "Dynamics"; break;
            case kAudioUnitSubType_MatrixReverb:
            case kAudioUnitSubType_Reverb2: p.category = "Reverb"; break;
            case kAudioUnitSubType_Distortion: p.category = "Distortion"; break;
            default: p.category = category_from_words(p.name);
        }
    }
}

const char *unitName(AudioUnitParameterUnit unit) {
    switch (unit) {
        case kAudioUnitParameterUnit_Hertz: return "Hz";
        case kAudioUnitParameterUnit_Decibels: return "dB";
        case kAudioUnitParameterUnit_Seconds: return "s";
        case kAudioUnitParameterUnit_Milliseconds: return "ms";
        case kAudioUnitParameterUnit_Percent:
        case kAudioUnitParameterUnit_EqualPowerCrossfade:
        case kAudioUnitParameterUnit_MixerFaderCurve1: return "%";
        case kAudioUnitParameterUnit_Cents: return "cents";
        case kAudioUnitParameterUnit_RelativeSemiTones: return "st";
        case kAudioUnitParameterUnit_Degrees: return "deg";
        case kAudioUnitParameterUnit_BPM: return "bpm";
        case kAudioUnitParameterUnit_Pan: return "pan";
        default: return "";
    }
}

struct AuPlugin final : Plugin {
    AudioComponent comp = nullptr;
    AudioUnit unit = nullptr;
    AudioComponentDescription desc{};
    std::vector<AudioUnitParameterID> ids;
    std::unique_ptr<std::atomic<double>[]> hostSet;
    UInt32 inputElements = 0;
    double sampleRate = 48000;
    bool sidechainActive = false;
    const float *const *curIn = nullptr;
    const float *const *curSc = nullptr;
    int32_t curFrames = 0;
    std::vector<float> silence;
    AudioTimeStamp stamp{};
    AUEventListenerRef listener = nullptr;
    NSView *view = nil;
    struct {
        AudioBufferList list;
        AudioBuffer second;
    } outputs{};

    ~AuPlugin() override {
        if (listener) {
            AUListenerDispose(listener);
        }
        if (unit) {
            AudioUnitUninitialize(unit);
            AudioComponentInstanceDispose(unit);
        }
    }

    int32_t index(AudioUnitParameterID id) const {
        for (size_t i = 0; i < ids.size(); i++) {
            if (ids[i] == id) {
                return static_cast<int32_t>(i);
            }
        }
        return -1;
    }

    void readParams() {
        params.clear();
        ids.clear();
        UInt32 size = 0;
        Boolean writable = false;
        if (AudioUnitGetPropertyInfo(unit, kAudioUnitProperty_ParameterList, kAudioUnitScope_Global, 0, &size,
                                     &writable) == noErr &&
            size > 0) {
            std::vector<AudioUnitParameterID> list(size / sizeof(AudioUnitParameterID));
            AudioUnitGetProperty(unit, kAudioUnitProperty_ParameterList, kAudioUnitScope_Global, 0, list.data(), &size);
            for (auto id : list) {
                AudioUnitParameterInfo info{};
                UInt32 isz = sizeof info;
                if (AudioUnitGetProperty(unit, kAudioUnitProperty_ParameterInfo, kAudioUnitScope_Global, id, &info,
                                         &isz) != noErr) {
                    continue;
                }
                if (!(info.flags & kAudioUnitParameterFlag_IsWritable)) {
                    continue;
                }
                ParamInfo p;
                p.id = std::to_string(id);
                if ((info.flags & kAudioUnitParameterFlag_HasCFNameString) && info.cfNameString) {
                    p.name = cfString(info.cfNameString);
                    if (info.flags & kAudioUnitParameterFlag_CFNameRelease) {
                        CFRelease(info.cfNameString);
                    }
                } else {
                    p.name = info.name;
                }
                p.min = info.minValue;
                p.max = info.maxValue;
                p.def = info.defaultValue;
                p.unit = unitName(info.unit);
                if (info.unit == kAudioUnitParameterUnit_Indexed || info.unit == kAudioUnitParameterUnit_Boolean) {
                    p.steps = static_cast<int32_t>(std::lround(info.maxValue - info.minValue));
                    CFArrayRef names = nullptr;
                    UInt32 nsz = sizeof names;
                    if (AudioUnitGetProperty(unit, kAudioUnitProperty_ParameterValueStrings, kAudioUnitScope_Global, id,
                                             &names, &nsz) == noErr &&
                        names) {
                        for (CFIndex i = 0; i < CFArrayGetCount(names); i++) {
                            p.valueNames.push_back(cfString(static_cast<CFStringRef>(CFArrayGetValueAtIndex(names, i))));
                        }
                        CFRelease(names);
                    }
                }
                if (p.max <= p.min) {
                    continue;
                }
                ids.push_back(id);
                params.push_back(std::move(p));
            }
        }
        initValues();
        hostSet.reset(new std::atomic<double>[params.size()]);
        refreshValues();
    }

    void refreshValues() {
        for (size_t i = 0; i < ids.size(); i++) {
            AudioUnitParameterValue v = 0;
            if (AudioUnitGetParameter(unit, ids[i], kAudioUnitScope_Global, 0, &v) == noErr) {
                values[i].store(v);
                hostSet[i].store(v);
            }
        }
    }

    static void onEvent(void *self, void *, const AudioUnitEvent *event, UInt64, AudioUnitParameterValue value) {
        auto *p = static_cast<AuPlugin *>(self);
        if (event->mEventType != kAudioUnitEvent_ParameterValueChange) {
            return;
        }
        auto idx = p->index(event->mArgument.mParameter.mParameterID);
        if (idx < 0) {
            return;
        }
        // Our own notifications (host-set values echoed for the editor) carry the value the host set.
        if (std::fabs(value - p->hostSet[idx].load()) <= 1e-6 * std::max(1.0, std::fabs(static_cast<double>(value)))) {
            return;
        }
        p->hostSet[idx].store(value);
        p->values[idx].store(value);
        p->emit(0, idx, value);
    }

    void listen() {
        if (AUEventListenerCreate(&AuPlugin::onEvent, this, CFRunLoopGetMain(), kCFRunLoopDefaultMode, 0.01, 0.01,
                                  &listener) != noErr) {
            listener = nullptr;
            return;
        }
        for (auto id : ids) {
            AudioUnitEvent e{};
            e.mEventType = kAudioUnitEvent_ParameterValueChange;
            e.mArgument.mParameter = {unit, id, kAudioUnitScope_Global, 0};
            AUEventListenerAddEventType(listener, this, &e);
        }
    }

    static OSStatus renderInput(void *self, AudioUnitRenderActionFlags *, const AudioTimeStamp *, UInt32 bus,
                                UInt32 frames, AudioBufferList *data) {
        auto *p = static_cast<AuPlugin *>(self);
        const float *const *src = bus == 0 ? p->curIn : (p->sidechainActive ? p->curSc : nullptr);
        for (UInt32 b = 0; b < data->mNumberBuffers; b++) {
            const float *from = src ? src[std::min<UInt32>(b, 1)] : p->silence.data();
            auto bytes = sizeof(float) * std::min<UInt32>(frames, static_cast<UInt32>(p->curFrames));
            if (!data->mBuffers[b].mData) {
                data->mBuffers[b].mData = const_cast<float *>(from);
                data->mBuffers[b].mDataByteSize = static_cast<UInt32>(bytes);
            } else {
                std::memcpy(data->mBuffers[b].mData, from, bytes);
            }
        }
        return noErr;
    }

    int32_t activate(double rate, int32_t maxFrames, bool sidechain) override {
        sampleRate = rate;
        sidechainActive = sidechain && sidechainCapable;
        AudioStreamBasicDescription f{};
        f.mSampleRate = rate;
        f.mFormatID = kAudioFormatLinearPCM;
        f.mFormatFlags = static_cast<AudioFormatFlags>(kAudioFormatFlagsNativeFloatPacked) |
                         static_cast<AudioFormatFlags>(kAudioFormatFlagIsNonInterleaved);
        f.mBytesPerPacket = 4;
        f.mFramesPerPacket = 1;
        f.mBytesPerFrame = 4;
        f.mChannelsPerFrame = 2;
        f.mBitsPerChannel = 32;
        UInt32 frames = static_cast<UInt32>(maxFrames);
        AudioUnitSetProperty(unit, kAudioUnitProperty_MaximumFramesPerSlice, kAudioUnitScope_Global, 0, &frames,
                             sizeof frames);
        if (AudioUnitSetProperty(unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Output, 0, &f, sizeof f) !=
            noErr) {
            return fail("the Audio Unit does not take a stereo float output");
        }
        UInt32 inputs = instrument ? 0 : std::min<UInt32>(inputElements, sidechainActive ? 2u : 1u);
        for (UInt32 e = 0; e < inputs; e++) {
            if (AudioUnitSetProperty(unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input, e, &f, sizeof f) !=
                noErr) {
                return fail("the Audio Unit does not take a stereo float input");
            }
            AURenderCallbackStruct cb{&AuPlugin::renderInput, this};
            AudioUnitSetProperty(unit, kAudioUnitProperty_SetRenderCallback, kAudioUnitScope_Input, e, &cb, sizeof cb);
        }
        auto status = AudioUnitInitialize(unit);
        if (status != noErr) {
            return fail("AudioUnitInitialize failed (" + std::to_string(status) + ")");
        }
        silence.assign(static_cast<size_t>(maxFrames), 0.0f);
        stamp = {};
        stamp.mFlags = kAudioTimeStampSampleTimeValid;
        return 0;
    }

    void deactivate() override { AudioUnitUninitialize(unit); }

    int32_t process(const float *const *in, const float *const *sc, float *const *out, int32_t frames,
                    const an_plug_midi *midi, int32_t midiCount) override {
        curIn = in;
        curSc = sc;
        curFrames = frames;
        if (instrument || desc.componentType == kAudioUnitType_MusicEffect) {
            for (int32_t i = 0; i < midiCount; i++) {
                MusicDeviceMIDIEvent(unit, midi[i].status, midi[i].data1, midi[i].data2,
                                     static_cast<UInt32>(std::clamp(midi[i].frame, 0, frames - 1)));
            }
        }
        outputs.list.mNumberBuffers = 2;
        for (int b = 0; b < 2; b++) {
            auto &buf = outputs.list.mBuffers[b];
            buf.mNumberChannels = 1;
            buf.mDataByteSize = static_cast<UInt32>(sizeof(float) * static_cast<size_t>(frames));
            buf.mData = out[b];
        }
        AudioUnitRenderActionFlags flags = 0;
        auto status = AudioUnitRender(unit, &flags, &stamp, 0, static_cast<UInt32>(frames), &outputs.list);
        stamp.mSampleTime += frames;
        for (int b = 0; b < 2; b++) {
            // An AU may answer with its own buffers.
            if (outputs.list.mBuffers[b].mData != out[b] && outputs.list.mBuffers[b].mData) {
                std::memcpy(out[b], outputs.list.mBuffers[b].mData, sizeof(float) * static_cast<size_t>(frames));
            }
        }
        return status == noErr ? 0 : -2;
    }

    void reset() override { AudioUnitReset(unit, kAudioUnitScope_Global, 0); }

    void setParam(int32_t index, double value) override {
        hostSet[index].store(value, std::memory_order_relaxed);
        AudioUnitSetParameter(unit, ids[static_cast<size_t>(index)], kAudioUnitScope_Global, 0,
                              static_cast<AudioUnitParameterValue>(value), 0);
        controllerDirty[index].store(true, std::memory_order_relaxed);
        anyControllerDirty.store(true, std::memory_order_release);
    }

    void idle() override {
        if (anyControllerDirty.exchange(false, std::memory_order_acquire)) {
            for (size_t i = 0; i < ids.size(); i++) {
                if (controllerDirty[i].exchange(false, std::memory_order_relaxed)) {
                    // Tell the editor (and other listeners) about the host's value.
                    AudioUnitParameter param{unit, ids[i], kAudioUnitScope_Global, 0};
                    AUParameterListenerNotify(nullptr, nullptr, &param);
                }
            }
        }
        Plugin::idle();
    }

    int32_t latency() override {
        Float64 seconds = 0;
        UInt32 size = sizeof seconds;
        if (AudioUnitGetProperty(unit, kAudioUnitProperty_Latency, kAudioUnitScope_Global, 0, &seconds, &size) !=
            noErr) {
            return 0;
        }
        return static_cast<int32_t>(std::lround(seconds * sampleRate));
    }

    bool saveState(std::vector<uint8_t> &out) override {
        CFPropertyListRef plist = nullptr;
        UInt32 size = sizeof plist;
        if (AudioUnitGetProperty(unit, kAudioUnitProperty_ClassInfo, kAudioUnitScope_Global, 0, &plist, &size) !=
                noErr ||
            !plist) {
            fail("the Audio Unit has no ClassInfo");
            return false;
        }
        auto data = CFPropertyListCreateData(nullptr, plist, kCFPropertyListBinaryFormat_v1_0, 0, nullptr);
        CFRelease(plist);
        if (!data) {
            fail("could not serialise the Audio Unit's ClassInfo");
            return false;
        }
        auto bytes = CFDataGetBytePtr(data);
        out.assign(bytes, bytes + CFDataGetLength(data));
        CFRelease(data);
        return true;
    }

    bool loadState(const uint8_t *data, size_t size) override {
        auto cfData = CFDataCreate(nullptr, data, static_cast<CFIndex>(size));
        auto plist = CFPropertyListCreateWithData(nullptr, cfData, kCFPropertyListImmutable, nullptr, nullptr);
        CFRelease(cfData);
        if (!plist) {
            fail("not an Audio Unit state");
            return false;
        }
        auto status =
            AudioUnitSetProperty(unit, kAudioUnitProperty_ClassInfo, kAudioUnitScope_Global, 0, &plist, sizeof plist);
        CFRelease(plist);
        if (status != noErr) {
            fail("the Audio Unit refused the state (" + std::to_string(status) + ")");
            return false;
        }
        // Tell listeners (an open editor) that everything changed.
        AudioUnitParameter all{unit, kAUParameterListener_AnyParameter, kAudioUnitScope_Global, 0};
        AUParameterListenerNotify(nullptr, nullptr, &all);
        refreshValues();
        return true;
    }

    bool attachEditor(void *container, int32_t &width, int32_t &height) override {
        NSView *v = nil;
        UInt32 size = 0;
        Boolean writable = false;
        if (AudioUnitGetPropertyInfo(unit, kAudioUnitProperty_CocoaUI, kAudioUnitScope_Global, 0, &size, &writable) ==
                noErr &&
            size >= sizeof(AudioUnitCocoaViewInfo)) {
            std::vector<uint8_t> buffer(size);
            auto *info = reinterpret_cast<AudioUnitCocoaViewInfo *>(buffer.data());
            if (AudioUnitGetProperty(unit, kAudioUnitProperty_CocoaUI, kAudioUnitScope_Global, 0, info, &size) ==
                noErr) {
                auto classes = (size - sizeof(CFURLRef)) / sizeof(CFStringRef);
                auto *url = (__bridge NSURL *)info->mCocoaAUViewBundleLocation;
                auto *className = (__bridge NSString *)info->mCocoaAUViewClass[0];
                auto *bundle = [NSBundle bundleWithURL:url];
                Class cls = [bundle classNamed:className];
                if (cls && [cls instancesRespondToSelector:@selector(uiViewForAudioUnit:withSize:)]) {
                    NSObject<AnCocoaViewFactory> *factory = [[cls alloc] init];
                    v = [factory uiViewForAudioUnit:unit withSize:NSMakeSize(400, 300)];
                }
                if (info->mCocoaAUViewBundleLocation) {
                    CFRelease(info->mCocoaAUViewBundleLocation);
                }
                for (size_t c = 0; c < classes; c++) {
                    if (info->mCocoaAUViewClass[c]) {
                        CFRelease(info->mCocoaAUViewClass[c]);
                    }
                }
            }
        }
        if (!v) {
            // No view of its own: Apple's generic one.
            auto *generic = [[AUGenericView alloc] initWithAudioUnit:unit];
            generic.showsExpertParameters = YES;
            v = generic;
        }
        if (!v) {
            fail("no editor view");
            return false;
        }
        width = static_cast<int32_t>(v.frame.size.width);
        height = static_cast<int32_t>(v.frame.size.height);
        editor_resize(this, width, height);
        v.frameOrigin = NSZeroPoint;
        [(__bridge NSView *)container addSubview:v];
        view = v;
        return true;
    }

    void detachEditor() override {
        if (view) {
            [view removeFromSuperview];
            view = nil;
        }
    }
};

const OSType kTypes[] = {kAudioUnitType_Effect, kAudioUnitType_MusicEffect, kAudioUnitType_MusicDevice};

int32_t openComponent(AudioComponent comp, AuPlugin **result) {
    auto plugin = std::make_unique<AuPlugin>();
    plugin->comp = comp;
    AudioComponentGetDescription(comp, &plugin->desc);
    describeComponent(comp, *plugin);
    auto status = AudioComponentInstanceNew(comp, &plugin->unit);
    if (status != noErr || !plugin->unit) {
        plugin->unit = nullptr;
        return fail("AudioComponentInstanceNew failed (" + std::to_string(status) + ")");
    }
    UInt32 count = 0, size = sizeof count;
    if (AudioUnitGetProperty(plugin->unit, kAudioUnitProperty_ElementCount, kAudioUnitScope_Input, 0, &count, &size) ==
        noErr) {
        plugin->inputElements = count;
    }
    plugin->sidechainCapable = !plugin->instrument && plugin->inputElements >= 2;
    Boolean writable = false;
    UInt32 cocoaSize = 0;
    plugin->customEditor = AudioUnitGetPropertyInfo(plugin->unit, kAudioUnitProperty_CocoaUI, kAudioUnitScope_Global, 0,
                                                    &cocoaSize, &writable) == noErr &&
                           cocoaSize > 0;
    plugin->readParams();
    plugin->listen();
    *result = plugin.release();
    return 0;
}

} // namespace

int32_t au_scan(const char *path, bool instantiate, std::string &json) {
    Json j;
    j.beginArray();
    if (!path) {
        for (auto type : kTypes) {
            AudioComponentDescription d{type, 0, 0, 0, 0};
            AudioComponent comp = nullptr;
            while ((comp = AudioComponentFindNext(comp, &d)) != nullptr) {
                AuPlugin bare;
                describeComponent(comp, bare);
                bare.initValues();
                j.raw(bare.describe(0));
            }
        }
    } else {
        AudioComponentDescription d{};
        if (!parseId(path, d)) {
            return fail(std::string("not an Audio Unit id: ") + path);
        }
        auto comp = AudioComponentFindNext(nullptr, &d);
        if (!comp) {
            return fail(std::string("no Audio Unit ") + path);
        }
        if (instantiate) {
            AuPlugin *plugin = nullptr;
            if (openComponent(comp, &plugin) != 0) {
                return -1;
            }
            j.raw(plugin->describe(plugin->latency()));
            delete plugin;
        } else {
            AuPlugin bare;
            describeComponent(comp, bare);
            bare.initValues();
            j.raw(bare.describe(0));
        }
    }
    j.endArray();
    json = j.str();
    return 0;
}

int32_t au_open(const char *id, Plugin **result) {
    AudioComponentDescription d{};
    if (!parseId(id, d)) {
        return fail(std::string("not an Audio Unit id: ") + id);
    }
    auto comp = AudioComponentFindNext(nullptr, &d);
    if (!comp) {
        return fail(std::string("no Audio Unit ") + id);
    }
    AuPlugin *plugin = nullptr;
    auto r = openComponent(comp, &plugin);
    if (r == 0) {
        *result = plugin;
    }
    return r;
}

} // namespace anplug
