/*
 * The test plug-ins off macOS have no editor; an_plug opens none there yet. GPL-3.0-or-later
 */
#include "an_testvst3.h"

namespace antest {

Steinberg::IPlugView *create_view(Controller *) { return nullptr; }
void view_update(void *, Steinberg::int32, double) {}

} // namespace antest
