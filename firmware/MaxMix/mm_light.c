/* MaxMix autonomous ring lighting. See mm_light.h. */
#include "mm_light.h"

static uint8_t lerp8(uint8_t a, uint8_t b, uint8_t t) {
    /* int32: on AVR int is 16-bit and (b - a) * t reaches +/-65025. */
    return (uint8_t)(a + (((int32_t)b - (int32_t)a) * (int32_t)t) / 255);
}

mm_rgb_t mm_lerp(mm_rgb_t a, mm_rgb_t b, uint8_t t) {
    mm_rgb_t c;
    c.r = lerp8(a.r, b.r, t);
    c.g = lerp8(a.g, b.g, t);
    c.b = lerp8(a.b, b.b, t);
    return c;
}

mm_rgb_t mm_scale(mm_rgb_t c, uint8_t k) {
    mm_rgb_t o;
    o.r = (uint8_t)(((uint16_t)c.r * k) / 255);
    o.g = (uint8_t)(((uint16_t)c.g * k) / 255);
    o.b = (uint8_t)(((uint16_t)c.b * k) / 255);
    return o;
}

uint8_t mm_wave(uint32_t now_ms, uint8_t speed) {
    if (speed < 1) speed = 1;
    if (speed > 10) speed = 10;
    /* speed 1 -> 4000ms period, speed 10 -> 400ms. */
    uint32_t period = 4400UL - 400UL * speed;
    uint32_t phase = now_ms % period;
    uint32_t half = period / 2;
    uint32_t v = phase < half ? (phase * 255UL) / half : ((period - phase) * 255UL) / half;
    return (uint8_t)(v > 255 ? 255 : v);
}

uint8_t mm_pixel_index(uint8_t i, uint8_t count, uint8_t reversed) {
    if (count == 0) return 0;
    return reversed ? (uint8_t)(count - 1 - i) : i;
}

static mm_rgb_t mode_color(uint8_t mode, mm_rgb_t base, mm_rgb_t lo, mm_rgb_t hi,
                           uint32_t now_ms, uint8_t speed) {
    switch (mode) {
    case MM_LIGHT_PULSE: {
        /* Breathe between 20% and 100% brightness. */
        uint8_t k = (uint8_t)(51 + ((uint16_t)mm_wave(now_ms, speed) * 204) / 255);
        return mm_scale(base, k);
    }
    case MM_LIGHT_FADE:
        return mm_lerp(lo, hi, mm_wave(now_ms, speed));
    default:
        return base;
    }
}

mm_rgb_t mm_render_pixel(const mm_lighting_t *cfg, uint8_t volume, uint8_t i,
                         uint8_t count, uint32_t now_ms, uint8_t idle) {
    mm_rgb_t off = {0, 0, 0};
    if (count == 0 || i >= count) return off;

    if (idle) {
        return mode_color(cfg->idle_mode, cfg->color_max, cfg->color_min, cfg->color_max,
                          now_ms, cfg->speed);
    }

    if (volume > 100) volume = 100;
    /* Number of lit pixels, rounded to nearest. */
    uint8_t lit = (uint8_t)(((uint16_t)volume * count + 50) / 100);
    if (i >= lit) return off;

    uint8_t t = count > 1 ? (uint8_t)(((uint16_t)i * 255) / (count - 1)) : 255;
    mm_rgb_t base = mm_lerp(cfg->color_min, cfg->color_max, t);
    return mode_color(cfg->mode, base, cfg->color_min, cfg->color_max, now_ms, cfg->speed);
}

uint8_t mm_is_idle(const mm_lighting_t *cfg, uint32_t inactive_ms) {
    if (cfg->idle_timeout_s == 0) return 0;
    return inactive_ms >= (uint32_t)cfg->idle_timeout_s * 1000UL;
}
