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

mm_rgb_t mm_hue(uint8_t h) {
    mm_rgb_t c = {0, 0, 0};
    uint8_t region = h / 43;
    uint8_t rem = (uint8_t)((h - region * 43) * 6); /* 0..252 within the sector */
    uint8_t q = (uint8_t)(255 - rem);
    switch (region) {
    case 0: c.r = 255; c.g = rem; break;
    case 1: c.r = q; c.g = 255; break;
    case 2: c.g = 255; c.b = rem; break;
    case 3: c.g = q; c.b = 255; break;
    case 4: c.r = rem; c.b = 255; break;
    default: c.r = 255; c.b = q; break;
    }
    return c;
}

/* Rotation offset 0..255 for one turn per wave period (same speed scale). */
static uint8_t rotation(uint32_t now_ms, uint8_t speed) {
    if (speed < 1) speed = 1;
    if (speed > 10) speed = 10;
    uint32_t period = 4400UL - 400UL * speed;
    return (uint8_t)(((now_ms % period) * 256UL) / period);
}

static uint8_t is_ambient(uint8_t mode) {
    return mode == MM_LIGHT_RAINBOW || mode == MM_LIGHT_CAROUSEL;
}

/* Whole-ring animated modes: position of pixel i rotated over time. */
static mm_rgb_t ambient_color(uint8_t mode, const mm_lighting_t *cfg, uint8_t i, uint8_t count,
                              uint32_t now_ms) {
    uint8_t p = (uint8_t)(((uint16_t)i * 256) / count + rotation(now_ms, cfg->speed));
    if (mode == MM_LIGHT_RAINBOW) return mm_hue(p);
    /* Carousel: min at p=0, max at p=128, back to min. */
    uint8_t t = p < 128 ? (uint8_t)(p * 2) : (uint8_t)((255 - p) * 2 + 1);
    return mm_lerp(cfg->color_min, cfg->color_max, t);
}

uint16_t mm_level_q8(uint8_t volume, uint8_t count) {
    if (volume > 100) volume = 100;
    return (uint16_t)(((uint32_t)volume * count * 256UL) / 100UL);
}

mm_rgb_t mm_render_gauge(const mm_lighting_t *cfg, uint16_t level_q8, uint8_t i, uint8_t count) {
    mm_rgb_t off = {0, 0, 0};
    if (count == 0 || i >= count) return off;
    /* Fill: LEDs below the level take volume_max, the rest volume_min, and the
     * boundary LED blends by the fractional part. 0% = all min, 100% = all max. */
    uint16_t start = (uint16_t)i * 256;
    uint16_t frac = level_q8 <= start ? 0 : level_q8 - start;
    uint8_t t = frac >= 255 ? 255 : (uint8_t)frac;
    return mm_lerp(cfg->volume_min, cfg->volume_max, t);
}

uint16_t mm_ease_q8(uint16_t cur, uint16_t target) {
    int32_t diff = (int32_t)target - (int32_t)cur;
    int32_t step = diff / 4;
    const int32_t min_step = 16;
    if (step > -min_step && step < min_step) {
        step = diff > 0 ? (diff < min_step ? diff : min_step) : (diff > -min_step ? diff : -min_step);
    }
    return (uint16_t)((int32_t)cur + step);
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
        if (is_ambient(cfg->idle_mode)) return ambient_color(cfg->idle_mode, cfg, i, count, now_ms);
        return mode_color(cfg->idle_mode, cfg->color_max, cfg->color_min, cfg->color_max,
                          now_ms, cfg->speed);
    }

    if (is_ambient(cfg->mode)) return ambient_color(cfg->mode, cfg, i, count, now_ms);

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
