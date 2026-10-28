/*
 * an_plug on macOS: the sandbox's main loop and plugin editor windows (spike S6, docs/spikes/S6-plugin-windows.md).
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include "an_plug_internal.h"

#import <AppKit/AppKit.h>
#import <CoreGraphics/CoreGraphics.h>

@interface AnEditorDelegate : NSObject <NSWindowDelegate>
@property(nonatomic, assign) anplug::Plugin *plugin;
@end

namespace anplug {

struct EditorWindow {
    NSWindow *window;
    NSView *container;
    AnEditorDelegate *delegate;
};

static void editor_dispose(Plugin *plugin, bool closeWindow) {
    auto *e = plugin->editor;
    if (!e) {
        return;
    }
    plugin->editor = nullptr;
    plugin->detachEditor();
    e->delegate.plugin = nullptr;
    e->window.delegate = nil;
    if (closeWindow) {
        [e->window close];
    }
    delete e;
}

/* A window's bounds (top-left origin, points), any process's. */
static bool window_bounds(int64_t window, CGRect &bounds) {
    auto list = CGWindowListCopyWindowInfo(kCGWindowListOptionIncludingWindow, static_cast<CGWindowID>(window));
    if (!list) {
        return false;
    }
    auto ok = false;
    if (CFArrayGetCount(list) > 0) {
        auto info = static_cast<CFDictionaryRef>(CFArrayGetValueAtIndex(list, 0));
        if (auto b = static_cast<CFDictionaryRef>(CFDictionaryGetValue(info, kCGWindowBounds))) {
            ok = CGRectMakeWithDictionaryRepresentation(b, &bounds);
        }
    }
    CFRelease(list);
    return ok;
}

/* Positions and orders an editor window: at (x, y) (top-left, from the main screen's top-left); x < 0 places it
 * over the parent's top-left corner (cascaded), or centres it without a parent. */
static void place(NSWindow *window, int64_t parent, int32_t x, int32_t y) {
    auto *screen = NSScreen.screens.firstObject;
    auto top = screen ? screen.frame.size.height : 900;
    CGRect bounds;
    if (x < 0 && parent > 0 && window_bounds(parent, bounds)) {
        x = static_cast<int32_t>(bounds.origin.x) + 48;
        y = static_cast<int32_t>(bounds.origin.y) + 72;
    }
    if (x < 0) {
        [window center];
    } else {
        [window setFrameTopLeftPoint:NSMakePoint(x, top - y)];
    }
    if (parent > 0) {
        // Directly above the parent (another process's window: ordering works across processes, ownership does not).
        [window orderWindow:NSWindowAbove relativeTo:static_cast<NSInteger>(parent)];
    } else {
        [window orderFrontRegardless];
    }
}

int32_t editor_open(Plugin *plugin, const char *title, int64_t parent, int32_t x, int32_t y, int32_t flags) {
    if (![NSThread isMainThread]) {
        return fail("editor windows open on the main thread");
    }
    if (plugin->editor) {
        // Already open: move and re-order it (the app's window moved, or the user asked for it again).
        place(plugin->editor->window, parent, x, y);
        return static_cast<int32_t>(plugin->editor->window.windowNumber);
    }
    [NSApplication sharedApplication];
    auto frame = NSMakeRect(0, 0, 400, 300);
    NSWindow *window;
    if (flags & AN_PLUG_EDITOR_FLOATING) {
        auto *panel = [[NSPanel alloc]
            initWithContentRect:frame
                      styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskUtilityWindow
                        backing:NSBackingStoreBuffered
                          defer:NO];
        panel.floatingPanel = YES;
        panel.hidesOnDeactivate = NO;
        panel.becomesKeyOnlyIfNeeded = YES;
        panel.level = NSFloatingWindowLevel;
        // Also over the app's full-screen space, as Logic's plug-in windows are.
        panel.collectionBehavior =
            NSWindowCollectionBehaviorFullScreenAuxiliary | NSWindowCollectionBehaviorMoveToActiveSpace;
        window = panel;
    } else {
        window = [[NSWindow alloc]
            initWithContentRect:frame
                      styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskMiniaturizable
                        backing:NSBackingStoreBuffered
                          defer:NO];
    }
    window.releasedWhenClosed = NO;
    window.title = [NSString stringWithUTF8String:title];
    auto *container = [[NSView alloc] initWithFrame:frame];
    window.contentView = container;
    auto *delegate = [[AnEditorDelegate alloc] init];
    delegate.plugin = plugin;
    window.delegate = delegate;
    plugin->editor = new EditorWindow{window, container, delegate};

    int32_t width = 0, height = 0;
    if (!plugin->attachEditor((__bridge void *)container, width, height)) {
        plugin->editor->delegate.plugin = nullptr;
        window.delegate = nil;
        [window close];
        delete plugin->editor;
        plugin->editor = nullptr;
        return -1;
    }
    if (width > 0 && height > 0) {
        editor_resize(plugin, width, height);
    }
    place(window, parent, x, y);
    return static_cast<int32_t>(window.windowNumber);
}

void editor_close(Plugin *plugin) {
    if (!plugin->editor) {
        return;
    }
    if (![NSThread isMainThread]) {
        dispatch_sync(dispatch_get_main_queue(), ^{ editor_dispose(plugin, true); });
        return;
    }
    editor_dispose(plugin, true);
}

int32_t editor_window(Plugin *plugin) {
    return plugin->editor ? static_cast<int32_t>(plugin->editor->window.windowNumber) : 0;
}

void editor_resize(Plugin *plugin, int32_t width, int32_t height) {
    if (!plugin->editor || width <= 0 || height <= 0) {
        return;
    }
    auto *window = plugin->editor->window;
    auto topLeft = NSMakePoint(NSMinX(window.frame), NSMaxY(window.frame));
    [window setContentSize:NSMakeSize(width, height)];
    [window setFrameTopLeftPoint:topLeft];
    plugin->editor->container.frame = NSMakeRect(0, 0, width, height);
}

} // namespace anplug

@implementation AnEditorDelegate
- (void)windowWillClose:(NSNotification *)notification {
    (void)notification;
    auto *plugin = self.plugin;
    if (!plugin) {
        return;
    }
    self.plugin = nullptr;
    // The user closed it: tear the view down after AppKit is done with the window.
    dispatch_async(dispatch_get_main_queue(), ^{
        if (plugin->editor && plugin->editor->delegate == self) {
            anplug::editor_dispose(plugin, false);
            plugin->emit(2, -1, 0);
        }
    });
}
@end

extern "C" {

void an_plug_main_run(void) {
    @autoreleasepool {
        [NSApplication sharedApplication];
        [NSApp setActivationPolicy:NSApplicationActivationPolicyAccessory];
        [NSApp run];
    }
}

void an_plug_main_quit(void) {
    dispatch_async(dispatch_get_main_queue(), ^{
        [NSApp stop:nil];
        // stop: takes effect after the next event; post one.
        auto *event = [NSEvent otherEventWithType:NSEventTypeApplicationDefined
                                         location:NSZeroPoint
                                    modifierFlags:0
                                        timestamp:0
                                     windowNumber:0
                                          context:nil
                                          subtype:0
                                            data1:0
                                            data2:0];
        [NSApp postEvent:event atStart:YES];
    });
}

void an_plug_main_call(void (*fn)(void *), void *ctx) {
    if ([NSThread isMainThread]) {
        fn(ctx);
    } else {
        dispatch_sync_f(dispatch_get_main_queue(), ctx, fn);
    }
}

int32_t an_plug_app_window(int32_t pid) {
    // The largest normal-layer window on screen that the process owns: an app's main window.
    auto list = CGWindowListCopyWindowInfo(kCGWindowListOptionOnScreenOnly | kCGWindowListExcludeDesktopElements,
                                           kCGNullWindowID);
    if (!list) {
        return 0;
    }
    int32_t best = 0;
    double bestArea = 0;
    for (CFIndex i = 0; i < CFArrayGetCount(list); i++) {
        auto info = static_cast<CFDictionaryRef>(CFArrayGetValueAtIndex(list, i));
        int32_t owner = 0, layer = -1, number = 0;
        if (auto n = static_cast<CFNumberRef>(CFDictionaryGetValue(info, kCGWindowOwnerPID))) {
            CFNumberGetValue(n, kCFNumberSInt32Type, &owner);
        }
        if (auto n = static_cast<CFNumberRef>(CFDictionaryGetValue(info, kCGWindowLayer))) {
            CFNumberGetValue(n, kCFNumberSInt32Type, &layer);
        }
        if (auto n = static_cast<CFNumberRef>(CFDictionaryGetValue(info, kCGWindowNumber))) {
            CFNumberGetValue(n, kCFNumberSInt32Type, &number);
        }
        CGRect bounds = CGRectZero;
        if (auto b = static_cast<CFDictionaryRef>(CFDictionaryGetValue(info, kCGWindowBounds))) {
            CGRectMakeWithDictionaryRepresentation(b, &bounds);
        }
        auto area = bounds.size.width * bounds.size.height;
        if (owner == pid && layer == 0 && area > bestArea) {
            best = number;
            bestArea = area;
        }
    }
    CFRelease(list);
    return best;
}

int32_t an_plug_window_z(int64_t window) {
    auto list = CGWindowListCopyWindowInfo(kCGWindowListOptionOnScreenOnly, kCGNullWindowID);
    if (!list) {
        return -1;
    }
    int32_t z = -1;
    for (CFIndex i = 0; i < CFArrayGetCount(list); i++) {
        auto info = static_cast<CFDictionaryRef>(CFArrayGetValueAtIndex(list, i));
        int32_t number = 0;
        if (auto n = static_cast<CFNumberRef>(CFDictionaryGetValue(info, kCGWindowNumber))) {
            CFNumberGetValue(n, kCFNumberSInt32Type, &number);
        }
        if (number == window) {
            z = static_cast<int32_t>(i);
            break;
        }
    }
    CFRelease(list);
    return z;
}

int32_t an_plug_window_info(int64_t window, int32_t *pid, int32_t *x, int32_t *y, int32_t *width, int32_t *height,
                            int32_t *layer, int32_t *onscreen) {
    auto list = CGWindowListCopyWindowInfo(kCGWindowListOptionIncludingWindow, static_cast<CGWindowID>(window));
    if (!list) {
        return -1;
    }
    int32_t result = -1;
    if (CFArrayGetCount(list) > 0) {
        auto info = static_cast<CFDictionaryRef>(CFArrayGetValueAtIndex(list, 0));
        auto number = [](CFDictionaryRef d, CFStringRef key) {
            int32_t v = 0;
            if (auto n = static_cast<CFNumberRef>(CFDictionaryGetValue(d, key))) {
                CFNumberGetValue(n, kCFNumberSInt32Type, &v);
            }
            return v;
        };
        *pid = number(info, kCGWindowOwnerPID);
        *layer = number(info, kCGWindowLayer);
        auto on = static_cast<CFBooleanRef>(CFDictionaryGetValue(info, kCGWindowIsOnscreen));
        *onscreen = on && CFBooleanGetValue(on) ? 1 : 0;
        CGRect bounds = CGRectZero;
        if (auto b = static_cast<CFDictionaryRef>(CFDictionaryGetValue(info, kCGWindowBounds))) {
            CGRectMakeWithDictionaryRepresentation(b, &bounds);
        }
        *x = static_cast<int32_t>(bounds.origin.x);
        *y = static_cast<int32_t>(bounds.origin.y);
        *width = static_cast<int32_t>(bounds.size.width);
        *height = static_cast<int32_t>(bounds.size.height);
        result = 0;
    }
    CFRelease(list);
    return result;
}

} // extern "C"
