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

/* True when idle lighting should be active after `inactive_ms` of no input. */
uint8_t mm_is_idle(const mm_lighting_t *cfg, uint32_t inactive_ms);

#ifdef __cplusplus
}
#endif

#endif /* MM_LIGHT_H */
