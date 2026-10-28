/*
 * The test gain's editor: a plain NSView with a slider per parameter (no VSTGUI). GPL-3.0-or-later
 */
#include "an_testvst3.h"

#include "public.sdk/source/common/pluginview.h"

#import <AppKit/AppKit.h>

#include <cstring>

@interface AnTestEditorView : NSView
@property(nonatomic, assign) antest::Controller *controller;
- (void)changed:(NSSlider *)slider;
- (void)setValue:(double)value ofParameter:(int)parameter;
@end

@implementation AnTestEditorView
- (void)changed:(NSSlider *)slider {
    if (self.controller) {
        self.controller->edit(static_cast<Steinberg::int32>(slider.tag), slider.doubleValue);
    }
}

- (void)setValue:(double)value ofParameter:(int)parameter {
    for (NSView *v in self.subviews) {
        if ([v isKindOfClass:NSSlider.class] && v.tag == parameter) {
            ((NSSlider *)v).doubleValue = value;
        }
    }
}
@end

namespace antest {

using namespace Steinberg;

class TestView : public CPluginView {
public:
    explicit TestView(Controller *controller) : CPluginView(nullptr), controller(controller) {
        ViewRect r(0, 0, 320, 110);
        setRect(r);
    }

    tresult PLUGIN_API isPlatformTypeSupported(FIDString type) SMTG_OVERRIDE {
        return std::strcmp(type, kPlatformTypeNSView) == 0 ? kResultTrue : kResultFalse;
    }

    void attachedToParent() SMTG_OVERRIDE {
        auto *parent = (__bridge NSView *)systemWindow;
        auto *v = [[AnTestEditorView alloc] initWithFrame:NSMakeRect(0, 0, 320, 110)];
        v.controller = controller;
        int32 n = 0;
        auto *s = specs(Kind::Gain, n);
        for (int32 i = 0; i < n; i++) {
            auto y = 70 - i * 45;
            auto *label = [NSTextField labelWithString:[NSString stringWithUTF8String:s[i].name]];
            label.frame = NSMakeRect(12, y, 60, 22);
            [v addSubview:label];
            auto *slider = [NSSlider sliderWithValue:controller->getParamNormalized(static_cast<Vst::ParamID>(i))
                                            minValue:0
                                            maxValue:1
                                              target:v
                                              action:@selector(changed:)];
            slider.tag = i;
            slider.continuous = YES;
            slider.frame = NSMakeRect(76, y, 230, 22);
            [v addSubview:slider];
        }
        [parent addSubview:v];
        view = v;
        controller->view = (__bridge void *)v;
    }

    void removedFromParent() SMTG_OVERRIDE {
        if (view) {
            view.controller = nullptr;
            [view removeFromSuperview];
            view = nil;
        }
        controller->view = nullptr;
    }

private:
    Controller *controller;
    AnTestEditorView *view = nil;
};

IPlugView *create_view(Controller *controller) { return new TestView(controller); }

void view_update(void *view, int32 id, double value) {
    auto *v = (__bridge AnTestEditorView *)view;
    if ([NSThread isMainThread]) {
        [v setValue:value ofParameter:id];
    }
}

} // namespace antest
