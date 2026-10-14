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
static mm_devcfg_t devcfg = {50, 0, 0, 0}; /* sleep 0 = never */
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

/* A frame's bytes arrive back-to-back; a partial frame idle for longer than
 * this was damaged (e.g. RX overrun while drawing) and is dropped, so the
 * decoder never sits on a bogus length swallowing the following PINGs. */
#define RX_STALE_MS 20

static void poll_serial(void) {
    static uint32_t last_byte_ms = 0;
    uint32_t now = millis();
    if (decoder.n > 0 && now - last_byte_ms > RX_STALE_MS) mm_decoder_init(&decoder);
    while (Serial.available() > 0) {
        last_byte_ms = millis();
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

/* Exponential acceleration ported from the 1.5.x firmware:
 *   speed = detents/second, divisor = max((1 - accel/100) * 400, 1)
 *   step  = 1 + speed^2 / divisor  (reset to 1 when the direction changes).
 * At 100% a quick spin sweeps the whole range; at 0% every detent is 1 point. */
#define ACCEL_DIVISOR_MAX 400UL
static uint8_t accel_step(int8_t dir) {
    static uint32_t last_turn = 0;
    static int8_t last_dir = 0;
    uint32_t now = millis(), dt = now - last_turn;
    last_turn = now;
    uint8_t same_dir = dir == last_dir;
    last_dir = dir;
    if (!same_dir || devcfg.acceleration == 0) return 1;
    if (dt == 0) dt = 1;
    uint32_t speed = 1000UL / dt;
    uint32_t divisor = ((100UL - devcfg.acceleration) * ACCEL_DIVISOR_MAX) / 100UL;
    if (divisor < 1) divisor = 1;
    uint32_t step = 1 + (speed * speed) / divisor;
    return (uint8_t)(step > 100 ? 100 : step);
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
    int16_t vol = (int16_t)v->volume + (int16_t)dir * accel_step(dir);
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
/*
 * OLED presentation mirrors the original MaxMix 1.5.x firmware:
 *   navigate: big app name (size 2, horizontal scroll if long) + selection
 *             arrows on the edges + a volume bar with end caps below;
 *   edit:     small app name + volume bar + big right-aligned volume number;
 *   muted:    the bar is outlined instead of filled.
 */
#define CH_W1 5  /* size-1 glyph width */
#define CH_H1 7
#define CH_W2 10 /* size-2 glyph width */
#define CH_H2 14
#define SIDE_MARGIN 11 /* room for the selection arrows */
#define ARROW_SIZE 3
#define BAR_Y (CH_H2 + 4) /* thin bar just under the name, like 1.5.x */
#define BAR_H 6
#define DOT_SMALL 2
#define DOT_BIG 4
#define DOT_GAP 4
#define DOTS_MAX 8
#define SCROLL_HOLD_MS 1500UL /* pause before a long name starts scrolling */

static char shown_name[MM_NAME_BYTES];
static uint32_t name_scroll_ms = 0;
static uint32_t last_render_ms = 0;
static uint8_t scrolling = 0;

/* Name at `size` (1 or 2) inside [x, x+area), scrolling when it does not fit. */
static void draw_name(const char *name, uint8_t size, uint8_t x, uint8_t area) {
    const uint8_t adv = size == 2 ? CH_W2 + 2 : CH_W1 + 1;
    const uint8_t rowh = (size == 2 ? CH_H2 : CH_H1) + 2;
    uint16_t textw = (uint16_t)strlen(name) * adv;
    oled.setTextSize(size);
    oled.setTextColor(SSD1306_WHITE);
    if (textw > area) {
        scrolling = 1;
        uint16_t span = textw + adv * 3;
        uint16_t off = 0;
        uint32_t elapsed = millis() - name_scroll_ms;
        if (elapsed > SCROLL_HOLD_MS)
            off = (uint16_t)(((elapsed - SCROLL_HOLD_MS) * (adv * 3UL) / 1000UL) % span);
        oled.setCursor((int16_t)x - (int16_t)off, 0);
        oled.print(name);
        oled.print(F("   "));
        oled.print(name);
    } else {
        oled.setCursor(x, 0);
        oled.print(name);
    }
    /* Clip: clear everything outside the name area on the name row. */
    oled.fillRect(0, 0, x, rowh, SSD1306_BLACK);
    oled.fillRect(x + area, 0, OLED_WIDTH - x - area, rowh, SSD1306_BLACK);
}

/* Original 1.5.x bar: | <gap> fill <gap> |, filled or outlined when muted. */
static void draw_volbar(uint8_t vol, uint8_t muted, uint8_t x0, uint8_t x1, uint8_t y, uint8_t h) {
    oled.drawFastVLine(x0, y, h, SSD1306_WHITE);
    oled.drawFastVLine(x1, y, h, SSD1306_WHITE);
    const uint8_t bx = x0 + 3, inner = (uint8_t)(x1 - 2 - bx);
    uint8_t w = (uint8_t)(((uint16_t)vol * inner) / 100);
    if (w > 0) {
        if (muted) oled.drawRect(bx, y, w, h, SSD1306_WHITE);
        else oled.fillRect(bx, y, w, h, SSD1306_WHITE);
    }
}

static void draw_arrows(uint8_t left, uint8_t right) {
    const uint8_t cy = CH_H2 / 2;
    if (left)
        oled.fillTriangle(0, cy, ARROW_SIZE, cy - ARROW_SIZE, ARROW_SIZE, cy + ARROW_SIZE, SSD1306_WHITE);
    if (right)
        oled.fillTriangle(OLED_WIDTH - 1, cy, OLED_WIDTH - 1 - ARROW_SIZE, cy - ARROW_SIZE,
                          OLED_WIDTH - 1 - ARROW_SIZE, cy + ARROW_SIZE, SSD1306_WHITE);
}

/* Right-aligned size-2 number ending at right_x. Returns its left x. */
static uint8_t draw_volnum(uint8_t vol, uint8_t right_x, uint8_t y) {
    uint8_t digits = vol > 99 ? 3 : (vol > 9 ? 2 : 1);
    uint8_t x = (uint8_t)(right_x - digits * CH_W2 - (digits - 1) * 2);
    oled.setTextSize(2);
    oled.setTextColor(SSD1306_WHITE);
    oled.setCursor(x, y);
    oled.print(vol);
    return x;
}

/* Bottom dot group (like the 1.5.x mode dots): one dot per session, the
 * current one bigger. More than DOTS_MAX sessions are scaled onto the dots. */
static void draw_dots(uint8_t current, uint8_t count) {
    if (count < 2) return;
    uint8_t n = count > DOTS_MAX ? DOTS_MAX : count;
    uint8_t sel = count > DOTS_MAX ? (uint8_t)(((uint16_t)current * n) / count) : current;
    uint8_t width = (uint8_t)((n - 1) * (DOT_SMALL + DOT_GAP) + DOT_BIG);
    uint8_t x = (OLED_WIDTH - width) / 2;
    const uint8_t cy = OLED_HEIGHT - DOT_BIG / 2 - 1;
    for (uint8_t i = 0; i < n; i++) {
        uint8_t d = i == sel ? DOT_BIG : DOT_SMALL;
        oled.fillRect(x, cy - d / 2, d, d, SSD1306_WHITE);
        x += d + DOT_GAP;
    }
}

static void draw_display(void) {
    if (!display_on) return;
    /* A redraw blocks ~15ms on I2C; never start one while serial bytes wait. */
    if (Serial.available() > 0) return;

    /* Reset the scroll animation whenever the shown name changes. */
    const char *name = (connected && has_session[MM_SLOT_CURRENT])
                           ? sessions[MM_SLOT_CURRENT].name : "";
    if (strncmp(shown_name, name, MM_NAME_BYTES) != 0) {
        strncpy(shown_name, name, MM_NAME_BYTES);
        shown_name[MM_NAME_BYTES - 1] = 0;
        name_scroll_ms = millis();
        display_dirty = 1;
    }

    /* Render on a change, or keep ~25fps while a long name scrolls. */
    uint32_t now = millis();
    if (!display_dirty && !(scrolling && now - last_render_ms >= 40)) return;
    display_dirty = 0;
    last_render_ms = now;
    scrolling = 0;

    oled.clearDisplay();
    oled.setTextColor(SSD1306_WHITE);

    if (!connected) {
        oled.setTextSize(2);
        oled.setCursor(SIDE_MARGIN, 0);
        oled.print(F("MaxMix"));
        oled.setTextSize(1);
        oled.setCursor(SIDE_MARGIN, BAR_Y);
        oled.print(F("Waiting for host"));
    } else if (!has_session[MM_SLOT_CURRENT]) {
        oled.setTextSize(2);
        oled.setCursor(SIDE_MARGIN, 0);
        oled.print(F("No apps"));
    } else {
        const mm_session_t *s = &sessions[MM_SLOT_CURRENT];
        const uint8_t right = OLED_WIDTH - SIDE_MARGIN;
        if (!edit_mode) {
            /* Navigate (1.5.x select screen): arrows + big full-width name + thin bar. */
            draw_name(s->name, 2, SIDE_MARGIN, right - SIDE_MARGIN);
            draw_arrows(session_count > 1, session_count > 1);
            draw_volbar(s->volume.volume, s->volume.is_muted, SIDE_MARGIN, right - 1, BAR_Y, BAR_H);
        } else {
            /* Edit (1.5.x ApplicationEditScreen): small name top-left, tall bar
             * on the left, big volume number on the right. No arrows. */
            const uint8_t y = CH_H1 + 4;
            draw_name(s->name, 1, SIDE_MARGIN, right - SIDE_MARGIN);
            uint8_t num_x = draw_volnum(s->volume.volume, right, y);
            uint8_t bar_x1 = (uint8_t)(right - 3 * CH_W2 - 2 * 2 - 6);
            if (bar_x1 > num_x - 4) bar_x1 = num_x - 4;
            draw_volbar(s->volume.volume, s->volume.is_muted, SIDE_MARGIN, bar_x1, y, CH_H2);
        }
        draw_dots(session_current, session_count);
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
    Wire.setClock(400000); /* fast I2C, as the original, for smooth scrolling */
    oled.begin(SSD1306_SWITCHCAPVCC, OLED_ADDRESS);
    oled.setRotation(OLED_ROTATION);
    oled.setTextWrap(false); /* long names scroll instead of wrapping */
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
