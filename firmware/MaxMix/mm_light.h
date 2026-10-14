/*
 * MaxMix autonomous ring lighting — portable C (no Arduino deps).
 *
 * The host sends a LightingConfig once; the firmware renders every frame
 * locally from (config, volume, time, idle). Integer-only math so it is cheap
 * on an ATmega328p and deterministic for host tests.
 */
#ifndef MM_LIGHT_H
#define MM_LIGHT_H

#include <stdint.h>
#include "mm_proto.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Linear blend a->b by t/255. */
mm_rgb_t mm_lerp(mm_rgb_t a, mm_rgb_t b, uint8_t t);

/* Scale brightness by k/255. */
mm_rgb_t mm_scale(mm_rgb_t c, uint8_t k);

/* Triangle wave 0..255..0 over a period derived from speed (1..10). */
uint8_t mm_wave(uint32_t now_ms, uint8_t speed);

/* Physical pixel index for logical index i (applies ring reversal). */
uint8_t mm_pixel_index(uint8_t i, uint8_t count, uint8_t reversed);

/*
 * Colour of logical pixel `i` of `count`.
 * Active: pixels below the volume level show the min->max gradient, the rest
 *         are off; the mode modulates (pulse) or cycles (fade) the colours.
 * Idle:   the whole ring renders `idle_mode` regardless of volume.
 */
mm_rgb_t mm_render_pixel(const mm_lighting_t *cfg, uint8_t volume, uint8_t i,
                         uint8_t count, uint32_t now_ms, uint8_t idle);

/* Palette as a gradient: t=0 first colour, t=255 last, through every stop. */
mm_rgb_t mm_palette_linear(const mm_lighting_t *cfg, uint8_t t);

/* Palette as a loop: p 0..255 walks 1 -> 2 -> ... -> n -> back to 1. */
mm_rgb_t mm_palette_cyclic(const mm_lighting_t *cfg, uint8_t p);

/* Fully saturated hue wheel colour, h in 0..255 (0 red, 85 green, 170 blue). */
mm_rgb_t mm_hue(uint8_t h);

/* Lit length of the volume gauge in q8 pixel units (pixels * 256). */
uint16_t mm_level_q8(uint8_t volume, uint8_t count);

/*
 * Volume gauge pixel as a fill between the volume colours: LEDs below
 * `level_q8` show volume_max, the rest volume_min, and the boundary LED blends
 * by the fractional part, so 0% is all min, 100% all max and the fill moves
 * smoothly instead of jumping a whole LED at a time.
 */
mm_rgb_t mm_render_gauge(const mm_lighting_t *cfg, uint16_t level_q8, uint8_t i, uint8_t count);

/* One easing step from `cur` towards `target` (call once per frame). */
uint16_t mm_ease_q8(uint16_t cur, uint16_t target);

/* True when idle lighting should be active after `inactive_ms` of no input. */
uint8_t mm_is_idle(const mm_lighting_t *cfg, uint32_t inactive_ms);

#ifdef __cplusplus
}
#endif

#endif /* MM_LIGHT_H */
