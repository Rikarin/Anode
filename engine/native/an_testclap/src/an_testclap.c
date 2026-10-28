/* SPDX-License-Identifier: GPL-3.0-or-later
 * anode-test-sine: the CLAP instrument the engine's tests host. Eight sine voices; parameter 1 "Gain" (0…1) and
 * parameter 7 "Octave" (−2…2, stepped; ids are deliberately not 0, 1); a state of its parameters plus the number of
 * notes it has played. It plays CLAP note events and raw MIDI notes, and it checks the host's threading: every call
 * made on the wrong thread is logged through the host's log extension as CLAP_LOG_HOST_MISBEHAVING, which the tests
 * look for. */
#include <clap/clap.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

#define VOICES 8
#define PI 3.14159265358979323846
#define PARAM_GAIN 1
#define PARAM_OCTAVE 7
#define STATE_MAGIC 0x53544E41u /* "ANTS" */
#define STATE_VERSION 1u

typedef struct voice {
    int key; /* -1: free */
    int channel;
    double phase;
    double step;
    double level;
} voice;

typedef struct sine {
    clap_plugin_t plugin;
    const clap_host_t *host;
    const clap_host_log_t *log;
    const clap_host_thread_check_t *threads;
    double rate;
    double gain;
    double octave;
    uint32_t notes_played;
    int active;
    int processing;
    voice voices[VOICES];
} sine;

static const char *const features[] = {CLAP_PLUGIN_FEATURE_INSTRUMENT, CLAP_PLUGIN_FEATURE_SYNTHESIZER,
                                       CLAP_PLUGIN_FEATURE_STEREO, NULL};

static const clap_plugin_descriptor_t descriptor = {
    .clap_version = CLAP_VERSION_INIT,
    .id = "org.anode.test-sine",
    .name = "Anode Test Sine",
    .vendor = "Anode",
    .url = "https://github.com/anode-daw/anode",
    .manual_url = "",
    .support_url = "",
    .version = "1.0.0",
    .description = "A sine instrument for Anode's engine tests.",
    .features = features,
};

static sine *self(const clap_plugin_t *plugin) { return (sine *)plugin->plugin_data; }

static void say(sine *s, clap_log_severity severity, const char *message)
{
    if (s->log) s->log->log(s->host, severity, message);
}

static void expect_main(sine *s, const char *what)
{
    if (s->threads && !s->threads->is_main_thread(s->host)) say(s, CLAP_LOG_HOST_MISBEHAVING, what);
}

static void expect_audio(sine *s, const char *what)
{
    if (s->threads && !s->threads->is_audio_thread(s->host)) say(s, CLAP_LOG_HOST_MISBEHAVING, what);
}

static double frequency(const sine *s, int key)
{
    return 440.0 * pow(2.0, (key - 69) / 12.0 + s->octave);
}

static void note_on(sine *s, int channel, int key, double velocity)
{
    if (key < 0 || key > 127) return;
    int slot = 0;
    for (int i = 0; i < VOICES; i++) {
        if (s->voices[i].key < 0) {
            slot = i;
            break;
        }
    }
    voice *v = &s->voices[slot];
    v->key = key;
    v->channel = channel;
    v->phase = 0;
    v->step = 2.0 * PI * frequency(s, key) / s->rate;
    v->level = velocity;
    s->notes_played++;
}

static void note_off(sine *s, int channel, int key)
{
    for (int i = 0; i < VOICES; i++) {
        voice *v = &s->voices[i];
        if (v->key >= 0 && (key < 0 || v->key == key) && (channel < 0 || v->channel == channel)) v->key = -1;
    }
}

static void set_param(sine *s, clap_id id, double value)
{
    if (id == PARAM_GAIN) s->gain = value < 0 ? 0 : value > 1 ? 1 : value;
    if (id == PARAM_OCTAVE) s->octave = round(value < -2 ? -2 : value > 2 ? 2 : value);
}

static void handle(sine *s, const clap_event_header_t *header)
{
    if (header->space_id != CLAP_CORE_EVENT_SPACE_ID) return;
    switch (header->type) {
    case CLAP_EVENT_NOTE_ON: {
        const clap_event_note_t *note = (const clap_event_note_t *)header;
        note_on(s, note->channel, note->key, note->velocity);
        break;
    }
    case CLAP_EVENT_NOTE_OFF:
    case CLAP_EVENT_NOTE_CHOKE: {
        const clap_event_note_t *note = (const clap_event_note_t *)header;
        note_off(s, note->channel, note->key);
        break;
    }
    case CLAP_EVENT_MIDI: {
        const clap_event_midi_t *midi = (const clap_event_midi_t *)header;
        uint8_t kind = midi->data[0] & 0xF0, channel = midi->data[0] & 0x0F;
        if (kind == 0x90 && midi->data[2] > 0) note_on(s, channel, midi->data[1], midi->data[2] / 127.0);
        else if (kind == 0x80 || kind == 0x90) note_off(s, channel, midi->data[1]);
        else if (kind == 0xB0 && (midi->data[1] == 120 || midi->data[1] == 123)) note_off(s, -1, -1);
        break;
    }
    case CLAP_EVENT_PARAM_VALUE: {
        const clap_event_param_value_t *param = (const clap_event_param_value_t *)header;
        set_param(s, param->param_id, param->value);
        break;
    }
    default: break;
    }
}

static bool sine_init(const clap_plugin_t *plugin)
{
    sine *s = self(plugin);
    s->log = (const clap_host_log_t *)s->host->get_extension(s->host, CLAP_EXT_LOG);
    s->threads = (const clap_host_thread_check_t *)s->host->get_extension(s->host, CLAP_EXT_THREAD_CHECK);
    expect_main(s, "init off the main thread");
    return true;
}

static void sine_destroy(const clap_plugin_t *plugin)
{
    sine *s = self(plugin);
    expect_main(s, "destroy off the main thread");
    if (s->active) say(s, CLAP_LOG_HOST_MISBEHAVING, "destroyed while active");
    free(s);
}

static bool sine_activate(const clap_plugin_t *plugin, double rate, uint32_t min_frames, uint32_t max_frames)
{
    (void)min_frames;
    (void)max_frames;
    sine *s = self(plugin);
    expect_main(s, "activate off the main thread");
    s->rate = rate;
    s->active = 1;
    return true;
}

static void sine_deactivate(const clap_plugin_t *plugin)
{
    sine *s = self(plugin);
    expect_main(s, "deactivate off the main thread");
    if (s->processing) say(s, CLAP_LOG_HOST_MISBEHAVING, "deactivated while processing");
    s->active = 0;
}

static bool sine_start_processing(const clap_plugin_t *plugin)
{
    sine *s = self(plugin);
    expect_audio(s, "start_processing off the audio thread");
    if (!s->active) say(s, CLAP_LOG_HOST_MISBEHAVING, "start_processing while inactive");
    s->processing = 1;
    return true;
}

static void sine_stop_processing(const clap_plugin_t *plugin)
{
    sine *s = self(plugin);
    expect_audio(s, "stop_processing off the audio thread");
    s->processing = 0;
}

static void sine_reset(const clap_plugin_t *plugin)
{
    sine *s = self(plugin);
    for (int i = 0; i < VOICES; i++) s->voices[i].key = -1;
}

static clap_process_status sine_process(const clap_plugin_t *plugin, const clap_process_t *process)
{
    sine *s = self(plugin);
    expect_audio(s, "process off the audio thread");
    if (!s->processing) say(s, CLAP_LOG_HOST_MISBEHAVING, "process before start_processing");
    if (process->audio_outputs_count < 1 || process->audio_outputs[0].channel_count < 1) return CLAP_PROCESS_ERROR;
    float *left = process->audio_outputs[0].data32[0];
    float *right = process->audio_outputs[0].channel_count > 1 ? process->audio_outputs[0].data32[1] : NULL;
    uint32_t count = process->in_events ? process->in_events->size(process->in_events) : 0;
    uint32_t next = 0;
    for (uint32_t i = 0; i < process->frames_count; i++) {
        while (next < count) {
            const clap_event_header_t *header = process->in_events->get(process->in_events, next);
            if (header->time > i) break;
            handle(s, header);
            next++;
        }
        double sum = 0;
        for (int v = 0; v < VOICES; v++) {
            voice *voice = &s->voices[v];
            if (voice->key < 0) continue;
            sum += sin(voice->phase) * voice->level;
            voice->phase += voice->step;
            if (voice->phase > 2.0 * PI) voice->phase -= 2.0 * PI;
        }
        float sample = (float)(sum * s->gain * 0.25);
        left[i] = sample;
        if (right) right[i] = sample;
    }
    while (next < count) handle(s, process->in_events->get(process->in_events, next++));
    return CLAP_PROCESS_CONTINUE;
}

/* ---- params ---- */

static uint32_t params_count(const clap_plugin_t *plugin)
{
    expect_main(self(plugin), "params.count off the main thread");
    return 2;
}

static bool params_info(const clap_plugin_t *plugin, uint32_t index, clap_param_info_t *info)
{
    (void)plugin;
    memset(info, 0, sizeof *info);
    if (index == 0) {
        info->id = PARAM_GAIN;
        info->flags = CLAP_PARAM_IS_AUTOMATABLE;
        strcpy(info->name, "Gain");
        info->min_value = 0;
        info->max_value = 1;
        info->default_value = 0.5;
        return true;
    }
    if (index == 1) {
        info->id = PARAM_OCTAVE;
        info->flags = CLAP_PARAM_IS_AUTOMATABLE | CLAP_PARAM_IS_STEPPED;
        strcpy(info->name, "Octave");
        info->min_value = -2;
        info->max_value = 2;
        info->default_value = 0;
        return true;
    }
    return false;
}

static bool params_value(const clap_plugin_t *plugin, clap_id id, double *value)
{
    sine *s = self(plugin);
    if (id == PARAM_GAIN) *value = s->gain;
    else if (id == PARAM_OCTAVE) *value = s->octave;
    else return false;
    return true;
}

static bool params_to_text(const clap_plugin_t *plugin, clap_id id, double value, char *out, uint32_t size)
{
    (void)plugin;
    if (size < 8) return false;
    static const char *const octaves[] = {"-2", "-1", "0", "+1", "+2"};
    if (id == PARAM_OCTAVE) {
        int i = (int)round(value) + 2;
        if (i < 0 || i > 4) return false;
        strcpy(out, octaves[i]);
        return true;
    }
    if (id == PARAM_GAIN) {
        int percent = (int)round(value * 100);
        out[0] = 0;
        char digits[8];
        int n = 0;
        do {
            digits[n++] = (char)('0' + percent % 10);
            percent /= 10;
        } while (percent > 0 && n < 4);
        int o = 0;
        while (n > 0) out[o++] = digits[--n];
        out[o++] = '%';
        out[o] = 0;
        return true;
    }
    return false;
}

static bool params_from_text(const clap_plugin_t *plugin, clap_id id, const char *text, double *value)
{
    (void)plugin;
    (void)id;
    *value = atof(text);
    return true;
}

static void params_flush(const clap_plugin_t *plugin, const clap_input_events_t *in, const clap_output_events_t *out)
{
    (void)out;
    sine *s = self(plugin);
    if (s->processing) expect_audio(s, "params.flush off the audio thread while processing");
    uint32_t count = in ? in->size(in) : 0;
    for (uint32_t i = 0; i < count; i++) handle(s, in->get(in, i));
}

static const clap_plugin_params_t params = {
    .count = params_count,
    .get_info = params_info,
    .get_value = params_value,
    .value_to_text = params_to_text,
    .text_to_value = params_from_text,
    .flush = params_flush,
};

/* ---- state ---- */

static bool write_all(const clap_ostream_t *stream, const void *data, uint64_t size)
{
    const uint8_t *bytes = (const uint8_t *)data;
    while (size > 0) {
        int64_t written = stream->write(stream, bytes, size);
        if (written <= 0) return false;
        bytes += written;
        size -= (uint64_t)written;
    }
    return true;
}

static bool read_all(const clap_istream_t *stream, void *data, uint64_t size)
{
    uint8_t *bytes = (uint8_t *)data;
    while (size > 0) {
        int64_t read = stream->read(stream, bytes, size);
        if (read <= 0) return false;
        bytes += read;
        size -= (uint64_t)read;
    }
    return true;
}

typedef struct saved {
    uint32_t magic;
    uint32_t version;
    double gain;
    double octave;
    uint32_t notes_played;
    uint32_t reserved;
} saved;

static bool state_save(const clap_plugin_t *plugin, const clap_ostream_t *stream)
{
    sine *s = self(plugin);
    expect_main(s, "state.save off the main thread");
    saved state = {STATE_MAGIC, STATE_VERSION, s->gain, s->octave, s->notes_played, 0};
    return write_all(stream, &state, sizeof state);
}

static bool state_load(const clap_plugin_t *plugin, const clap_istream_t *stream)
{
    sine *s = self(plugin);
    expect_main(s, "state.load off the main thread");
    saved state;
    if (!read_all(stream, &state, sizeof state) || state.magic != STATE_MAGIC || state.version != STATE_VERSION)
        return false;
    set_param(s, PARAM_GAIN, state.gain);
    set_param(s, PARAM_OCTAVE, state.octave);
    s->notes_played = state.notes_played;
    return true;
}

static const clap_plugin_state_t state = {.save = state_save, .load = state_load};

/* ---- ports, latency ---- */

static uint32_t audio_ports_count(const clap_plugin_t *plugin, bool is_input)
{
    (void)plugin;
    return is_input ? 0 : 1;
}

static bool audio_ports_get(const clap_plugin_t *plugin, uint32_t index, bool is_input, clap_audio_port_info_t *info)
{
    (void)plugin;
    if (is_input || index != 0) return false;
    memset(info, 0, sizeof *info);
    info->id = 0;
    strcpy(info->name, "Output");
    info->flags = CLAP_AUDIO_PORT_IS_MAIN;
    info->channel_count = 2;
    info->port_type = CLAP_PORT_STEREO;
    info->in_place_pair = CLAP_INVALID_ID;
    return true;
}

static const clap_plugin_audio_ports_t audio_ports = {.count = audio_ports_count, .get = audio_ports_get};

static uint32_t note_ports_count(const clap_plugin_t *plugin, bool is_input)
{
    (void)plugin;
    return is_input ? 1 : 0;
}

static bool note_ports_get(const clap_plugin_t *plugin, uint32_t index, bool is_input, clap_note_port_info_t *info)
{
    (void)plugin;
    if (!is_input || index != 0) return false;
    memset(info, 0, sizeof *info);
    info->id = 0;
    info->supported_dialects = CLAP_NOTE_DIALECT_CLAP | CLAP_NOTE_DIALECT_MIDI;
    info->preferred_dialect = CLAP_NOTE_DIALECT_CLAP;
    strcpy(info->name, "Notes");
    return true;
}

static const clap_plugin_note_ports_t note_ports = {.count = note_ports_count, .get = note_ports_get};

static uint32_t latency_get(const clap_plugin_t *plugin)
{
    expect_main(self(plugin), "latency.get off the main thread");
    return 0;
}

static const clap_plugin_latency_t latency = {.get = latency_get};

static const void *sine_extension(const clap_plugin_t *plugin, const char *id)
{
    (void)plugin;
    if (!strcmp(id, CLAP_EXT_PARAMS)) return &params;
    if (!strcmp(id, CLAP_EXT_STATE)) return &state;
    if (!strcmp(id, CLAP_EXT_AUDIO_PORTS)) return &audio_ports;
    if (!strcmp(id, CLAP_EXT_NOTE_PORTS)) return &note_ports;
    if (!strcmp(id, CLAP_EXT_LATENCY)) return &latency;
    return NULL;
}

static void sine_on_main_thread(const clap_plugin_t *plugin) { expect_main(self(plugin), "on_main_thread"); }

/* ---- factory and entry ---- */

static uint32_t factory_count(const clap_plugin_factory_t *factory)
{
    (void)factory;
    return 1;
}

static const clap_plugin_descriptor_t *factory_descriptor(const clap_plugin_factory_t *factory, uint32_t index)
{
    (void)factory;
    return index == 0 ? &descriptor : NULL;
}

static const clap_plugin_t *factory_create(const clap_plugin_factory_t *factory, const clap_host_t *host,
                                           const char *id)
{
    (void)factory;
    if (!clap_version_is_compatible(host->clap_version) || strcmp(id, descriptor.id) != 0) return NULL;
    sine *s = (sine *)calloc(1, sizeof *s);
    if (!s) return NULL;
    s->host = host;
    s->gain = 0.5;
    s->rate = 48000;
    for (int i = 0; i < VOICES; i++) s->voices[i].key = -1;
    s->plugin.desc = &descriptor;
    s->plugin.plugin_data = s;
    s->plugin.init = sine_init;
    s->plugin.destroy = sine_destroy;
    s->plugin.activate = sine_activate;
    s->plugin.deactivate = sine_deactivate;
    s->plugin.start_processing = sine_start_processing;
    s->plugin.stop_processing = sine_stop_processing;
    s->plugin.reset = sine_reset;
    s->plugin.process = sine_process;
    s->plugin.get_extension = sine_extension;
    s->plugin.on_main_thread = sine_on_main_thread;
    return &s->plugin;
}

static const clap_plugin_factory_t factory = {
    .get_plugin_count = factory_count,
    .get_plugin_descriptor = factory_descriptor,
    .create_plugin = factory_create,
};

static bool entry_init(const char *path)
{
    (void)path;
    return true;
}

static void entry_deinit(void) { }

static const void *entry_factory(const char *id) { return strcmp(id, CLAP_PLUGIN_FACTORY_ID) == 0 ? &factory : NULL; }

CLAP_EXPORT const clap_plugin_entry_t clap_entry = {
    .clap_version = CLAP_VERSION_INIT,
    .init = entry_init,
    .deinit = entry_deinit,
    .get_factory = entry_factory,
};
