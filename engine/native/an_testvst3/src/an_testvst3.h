/* Anode's test VST3 plug-ins: shared between the processing code and the editor view. GPL-3.0-or-later */
#pragma once

#include "public.sdk/source/vst/vsteditcontroller.h"

namespace antest {

enum class Kind { Gain, Delay, Misbehave, Synth };

struct ParamSpec {
    Steinberg::int32 id;
    const char *name;
    const char *unit;
    double def;
    Steinberg::int32 steps;
};

const ParamSpec *specs(Kind kind, Steinberg::int32 &count);

class Controller : public Steinberg::Vst::EditController {
public:
    explicit Controller(Kind kind) : kind(kind) {}
    Steinberg::tresult PLUGIN_API initialize(Steinberg::FUnknown *context) SMTG_OVERRIDE;
    Steinberg::tresult PLUGIN_API setComponentState(Steinberg::IBStream *state) SMTG_OVERRIDE;
    Steinberg::tresult PLUGIN_API setParamNormalized(Steinberg::Vst::ParamID id,
                                                     Steinberg::Vst::ParamValue value) SMTG_OVERRIDE;
    Steinberg::IPlugView *PLUGIN_API createView(Steinberg::FIDString name) SMTG_OVERRIDE;

    /* From the editor: a user gesture on parameter `id`. */
    void edit(Steinberg::int32 id, double value);
    /* The open view (an_testvst3_view.mm), or null. */
    void *view = nullptr;

private:
    Kind kind;
};

/* The editor (an_testvst3_view.mm). */
Steinberg::IPlugView *create_view(Controller *controller);
void view_update(void *view, Steinberg::int32 id, double value);

} // namespace antest
