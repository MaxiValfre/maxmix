/*
 * MaxMix firmware v2 (Arduino Nano / ATmega328p).
 *
 * - Protocol v2 (framed + CRC8) via the portable core in mm_proto.c.
 * - Autonomous ring lighting via mm_light.c: the host sends LightingConfig once.
 * - Sleep turns the OLED off but keeps the ring on idle lighting (if enabled).
 *
 * Controls: turn = navigate sessions (or change volume in edit mode),
 *           short press = toggle navigate/edit, long press = mute current.
 *
 * Libraries: Adafruit NeoPixel, Adafruit SSD1306, Adafruit GFX.
 */
#include <Adafruit_GFX.h>
#include <Adafruit_NeoPixel.h>
#include <Adafruit_SSD1306.h>
#include <Wire.h>

#include "Config.h"
#include "mm_light.h"
#include "mm_proto.h"

static Adafruit_NeoPixel ring(PIXELS_COUNT, PIN_PIXELS, NEO_GRB + NEO_KHZ800);
static Adafruit_SSD1306 oled(OLED_WIDTH, OLED_HEIGHT, &Wire, -1);

static mm_decoder_t decoder;
static mm_frame_t frame;
static uint8_t txbuf[MM_FRAME_MAX];

static mm_lighting_t lighting = {MM_LIGHT_STATIC, 5, {0, 0, 32}, {0, 128, 255}, MM_LIGHT_PULSE, 30};
static mm_devcfg_t devcfg = {50, 5, 0, 0};
static mm_session_t sessions[4];
static uint8_t has_session[4];
static uint8_t session_current = 0, session_count = 0;

static uint8_t connected = 0;
static uint8_t edit_mode = 0;
static uint8_t display_dirty = 1;
static uint8_t display_on = 1;
static uint32_t last_input_ms = 0;
static uint32_t last_frame_ms = 0;
static uint32_t last_host_ms = 0;

/* ---- serial ---------------------------------------------------------- */

static void send(uint8_t n) { Serial.write(txbuf, n); }

static void handle_frame(const mm_frame_t *f) {
    uint8_t a, b, c;
    last_host_ms = millis();
    switch (f->type) {
    case MM_T_HELLO:
        if (mm_parse_hello(f, &a) && a == MM_PROTO_VERSION) {
            connected = 1;
            send(mm_encode_hello_ack(FW_MAJOR, FW_MINOR, FW_CAPS, txbuf));
            display_dirty = 1;
        }
        break;
    case MM_T_PING:
        send(mm_encode_pong(txbuf));
        break;
    case MM_T_SESSION_INFO:
        if (mm_parse_session_info(f, &a, &b, &c)) {
            session_current = b;
            session_count = c;
            for (uint8_t i = 0; i < 4; i++) has_session[i] = 0;
            display_dirty = 1;
        }
        break;
    case MM_T_SESSION_DATA: {
        mm_session_t s;
        if (mm_parse_session_data(f, &s)) {
            sessions[s.slot] = s;
            has_session[s.slot] = 1;
            display_dirty = 1;
        }
        break;
    }
    case MM_T_VOLUME_SET: {
        mm_volume_t v;
        if (mm_parse_volume_set(f, &a, &v) && has_session[a]) {
            sessions[a].volume = v;
            display_dirty = 1;
        }
        break;
    }
    case MM_T_LIGHTING_CONFIG:
        mm_parse_lighting(f, &lighting);
        break;
    case MM_T_DEVICE_CONFIG:
        mm_parse_devcfg(f, &devcfg);
        break;
    default:
        break;
    }
}

static void poll_serial(void) {
    while (Serial.available() > 0) {
        mm_decoder_push(&decoder, (uint8_t)Serial.read());
        while (mm_decoder_poll(&decoder, &frame)) handle_frame(&frame);
    }
    /* Host gone (no Ping for a while): show disconnected. */
    if (connected && millis() - last_host_ms > 5000UL) {
        connected = 0;
        display_dirty = 1;
    }
}

/* ---- input ----------------------------------------------------------- */

static int8_t read_encoder(void) {
    static uint8_t prev = 0;
    static int8_t accum = 0;
    static const int8_t table[16] = {0, -1, 1, 0, 1, 0, 0, -1, -1, 0, 0, 1, 0, 1, -1, 0};
    uint8_t cur = (uint8_t)((digitalRead(PIN_ENCODER_A) << 1) | digitalRead(PIN_ENCODER_B));
    accum += table[(prev << 2) | cur];
    prev = cur;
    if (accum >= 4) { accum = 0; return 1; }
    if (accum <= -4) { accum = 0; return -1; }
    return 0;
}

static uint8_t accel_step(void) {
    static uint32_t last_turn = 0;
    uint32_t now = millis(), dt = now - last_turn;
    last_turn = now;
    if (dt > 120 || devcfg.acceleration == 0) return 1;
    /* Fast turns move up to 1 + accel/20 points (max 6). */
    return (uint8_t)(1 + (devcfg.acceleration * (120 - dt)) / (120UL * 20));
}

static void wake(void) {
    last_input_ms = millis();
    if (!display_on) {
        display_on = 1;
        oled.ssd1306_command(SSD1306_DISPLAYON);
        display_dirty = 1;
    }
}

static void on_turn(int8_t dir) {
    wake();
    if (!connected) return;
    if (dir < 0 && devcfg.ring_reversed) dir = 1;
    else if (dir > 0 && devcfg.ring_reversed) dir = -1;

    if (!edit_mode) {
        send(mm_encode_session_request(dir > 0 ? 1 : 0, txbuf));
        return;
    }
    if (!has_session[MM_SLOT_CURRENT]) return;
    mm_volume_t *v = &sessions[MM_SLOT_CURRENT].volume;
    int16_t vol = (int16_t)v->volume + (int16_t)dir * accel_step();
    v->volume = (uint8_t)(vol < 0 ? 0 : (vol > 100 ? 100 : vol));
    send(mm_encode_volume_change(MM_SLOT_CURRENT, v, txbuf));
    display_dirty = 1;
}

static void poll_switch(void) {
    static uint8_t was_down = 0, long_sent = 0;
    static uint32_t down_at = 0;
    uint8_t down = digitalRead(PIN_SWITCH) == LOW;
    uint32_t now = millis();

    if (down && !was_down) {
        down_at = now;
        long_sent = 0;
        wake();
    }
    if (down && !long_sent && now - down_at >= LONG_PRESS_MS) {
        long_sent = 1;
        if (connected && has_session[MM_SLOT_CURRENT]) {
            mm_volume_t *v = &sessions[MM_SLOT_CURRENT].volume;
            v->is_muted = !v->is_muted;
            send(mm_encode_volume_change(MM_SLOT_CURRENT, v, txbuf));
            display_dirty = 1;
        }
    }
    if (!down && was_down && !long_sent && now - down_at > 30) {
        edit_mode = !edit_mode;
        if (connected) send(mm_encode_button(edit_mode, txbuf));
        display_dirty = 1;
    }
    was_down = down;
}

/* ---- output ---------------------------------------------------------- */

static void draw_display(void) {
    if (!display_dirty || !display_on) return;
    display_dirty = 0;
    oled.clearDisplay();
    oled.setTextColor(SSD1306_WHITE);
    oled.setTextSize(1);
    oled.setCursor(0, 0);

    if (!connected) {
        oled.print(F("MaxMix v2"));
        oled.setCursor(0, 16);
        oled.print(F("Waiting for host..."));
    } else if (!has_session[MM_SLOT_CURRENT]) {
        oled.print(F("No audio apps"));
    } else {
        const mm_session_t *s = &sessions[MM_SLOT_CURRENT];
        oled.print(s->name);
        oled.setCursor(0, 12);
        oled.print(edit_mode ? F("VOL ") : F("    "));
        if (s->volume.is_muted) oled.print(F("MUTED"));
        else { oled.print(s->volume.volume); oled.print('%'); }
        oled.setCursor(OLED_WIDTH - 30, 12);
        oled.print(session_current + 1);
        oled.print('/');
        oled.print(session_count);
        /* Volume bar. */
        uint8_t w = (uint8_t)(((uint16_t)s->volume.volume * (OLED_WIDTH - 1)) / 100);
        oled.drawRect(0, 24, OLED_WIDTH, 8, SSD1306_WHITE);
        if (!s->volume.is_muted) oled.fillRect(0, 24, w, 8, SSD1306_WHITE);
    }
    oled.display();
}

static void draw_ring(void) {
    uint32_t now = millis();
    if (now - last_frame_ms < FRAME_MS) return;
    last_frame_ms = now;

    uint32_t inactive = now - last_input_ms;
    uint8_t idle = mm_is_idle(&lighting, inactive);
    uint8_t asleep = devcfg.sleep_after_s && inactive >= (uint32_t)devcfg.sleep_after_s * 1000UL;

    if (asleep && display_on) {
        display_on = 0;
        oled.ssd1306_command(SSD1306_DISPLAYOFF);
    }

    uint8_t vol = 0;
    if (has_session[MM_SLOT_CURRENT]) {
        vol = sessions[MM_SLOT_CURRENT].volume.is_muted ? 0 : sessions[MM_SLOT_CURRENT].volume.volume;
    }
    /* Asleep without idle lighting: ring off. Otherwise render autonomously. */
    uint8_t ring_off = asleep && lighting.idle_timeout_s == 0;

    for (uint8_t i = 0; i < PIXELS_COUNT; i++) {
        mm_rgb_t c = {0, 0, 0};
        if (!ring_off) c = mm_render_pixel(&lighting, vol, i, PIXELS_COUNT, now, idle || asleep);
        ring.setPixelColor(mm_pixel_index(i, PIXELS_COUNT, devcfg.ring_reversed), c.r, c.g, c.b);
    }
    ring.show();
}

/* ---- arduino --------------------------------------------------------- */

void setup() {
    Serial.begin(SERIAL_BAUD);
    pinMode(PIN_ENCODER_A, INPUT_PULLUP);
    pinMode(PIN_ENCODER_B, INPUT_PULLUP);
    pinMode(PIN_SWITCH, INPUT_PULLUP);

    mm_decoder_init(&decoder);
    ring.begin();
    ring.show();
    oled.begin(SSD1306_SWITCHCAPVCC, OLED_ADDRESS);
    oled.setRotation(OLED_ROTATION);
    last_input_ms = millis();
}

void loop() {
    poll_serial();
    int8_t dir = read_encoder();
    if (dir) on_turn(dir);
    poll_switch();
    draw_display();
    draw_ring();
}
