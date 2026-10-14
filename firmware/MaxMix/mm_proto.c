/* MaxMix protocol v2 — portable C implementation. See mm_proto.h. */
#include "mm_proto.h"

#include <string.h>

uint8_t mm_crc8(const uint8_t *data, uint8_t len) {
    uint8_t crc = 0;
    for (uint8_t i = 0; i < len; i++) {
        crc ^= data[i];
        for (uint8_t b = 0; b < 8; b++) {
            crc = (crc & 0x01) ? (uint8_t)((crc >> 1) ^ 0x8C) : (uint8_t)(crc >> 1);
        }
    }
    return crc;
}

void mm_decoder_init(mm_decoder_t *d) { d->n = 0; }

static void drop_front(mm_decoder_t *d, uint8_t k) {
    if (k >= d->n) {
        d->n = 0;
        return;
    }
    memmove(d->buf, d->buf + k, (size_t)(d->n - k));
    d->n = (uint8_t)(d->n - k);
}

void mm_decoder_push(mm_decoder_t *d, uint8_t byte) {
    if (d->n >= MM_FRAME_MAX) {
        drop_front(d, 1); /* never overflow; resync will sort it out */
    }
    d->buf[d->n++] = byte;
}

uint8_t mm_decoder_poll(mm_decoder_t *d, mm_frame_t *out) {
    for (;;) {
        /* Discard leading garbage up to the next SOF. */
        uint8_t i = 0;
        while (i < d->n && d->buf[i] != MM_SOF) i++;
        drop_front(d, i);
        if (d->n < 4) return 0;

        uint8_t len = d->buf[1];
        if (len > MM_MAX_PAYLOAD) {
            drop_front(d, 1); /* spurious SOF */
            continue;
        }
        uint8_t total = (uint8_t)(len + 4);
        if (d->n < total) return 0;

        if (mm_crc8(&d->buf[1], (uint8_t)(len + 2)) == d->buf[3 + len]) {
            out->type = d->buf[2];
            out->len = len;
            memcpy(out->payload, &d->buf[3], len);
            drop_front(d, total);
            return 1;
        }
        drop_front(d, 1); /* bad CRC: resync */
    }
}

uint8_t mm_encode(uint8_t type, const uint8_t *payload, uint8_t len, uint8_t *out) {
    if (len > MM_MAX_PAYLOAD) return 0;
    out[0] = MM_SOF;
    out[1] = len;
    out[2] = type;
    if (len) memcpy(&out[3], payload, len);
    out[3 + len] = mm_crc8(&out[1], (uint8_t)(len + 2));
    return (uint8_t)(len + 4);
}

void mm_vol_pack(const mm_volume_t *v, uint8_t out[2]) {
    out[0] = (uint8_t)((v->id & 0x7F) | (v->is_default ? 0x80 : 0));
    out[1] = (uint8_t)((v->volume & 0x7F) | (v->is_muted ? 0x80 : 0));
}

void mm_vol_unpack(const uint8_t in[2], mm_volume_t *v) {
    v->id = in[0] & 0x7F;
    v->is_default = (in[0] & 0x80) ? 1 : 0;
    v->volume = in[1] & 0x7F;
    v->is_muted = (in[1] & 0x80) ? 1 : 0;
}

#define EXPECT(f, t, n) \
    if ((f)->type != (t) || (f)->len != (n)) return 0

uint8_t mm_parse_hello(const mm_frame_t *f, uint8_t *proto_version) {
    EXPECT(f, MM_T_HELLO, 1);
    *proto_version = f->payload[0];
    return 1;
}

uint8_t mm_parse_session_info(const mm_frame_t *f, uint8_t *mode, uint8_t *current, uint8_t *count) {
    EXPECT(f, MM_T_SESSION_INFO, 3);
    *mode = f->payload[0];
    *current = f->payload[1];
    *count = f->payload[2];
    return 1;
}

uint8_t mm_parse_session_data(const mm_frame_t *f, mm_session_t *s) {
    EXPECT(f, MM_T_SESSION_DATA, 3 + MM_NAME_BYTES);
    if (f->payload[0] > MM_SLOT_NEXT) return 0;
    s->slot = f->payload[0];
    mm_vol_unpack(&f->payload[1], &s->volume);
    memcpy(s->name, &f->payload[3], MM_NAME_BYTES);
    s->name[MM_NAME_BYTES - 1] = '\0'; /* defensive: always terminated */
    return 1;
}

uint8_t mm_parse_volume_set(const mm_frame_t *f, uint8_t *slot, mm_volume_t *v) {
    EXPECT(f, MM_T_VOLUME_SET, 3);
    if (f->payload[0] > MM_SLOT_NEXT) return 0;
    *slot = f->payload[0];
    mm_vol_unpack(&f->payload[1], v);
    return 1;
}

uint8_t mm_parse_lighting(const mm_frame_t *f, mm_lighting_t *l) {
    EXPECT(f, MM_T_LIGHTING_CONFIG, 16);
    const uint8_t *p = f->payload;
    if (p[0] > MM_LIGHT_LAST || p[8] > MM_LIGHT_LAST) return 0;
    l->mode = p[0];
    l->speed = p[1];
    l->color_min.r = p[2];
    l->color_min.g = p[3];
    l->color_min.b = p[4];
    l->color_max.r = p[5];
    l->color_max.g = p[6];
    l->color_max.b = p[7];
    l->idle_mode = p[8];
    l->idle_timeout_s = p[9];
    l->volume_min.r = p[10];
    l->volume_min.g = p[11];
    l->volume_min.b = p[12];
    l->volume_max.r = p[13];
    l->volume_max.g = p[14];
    l->volume_max.b = p[15];
    return 1;
}

uint8_t mm_parse_devcfg(const mm_frame_t *f, mm_devcfg_t *c) {
    EXPECT(f, MM_T_DEVICE_CONFIG, 3);
    c->acceleration = f->payload[0];
    c->sleep_after_s = f->payload[1];
    c->continuous_scroll = (f->payload[2] & 0x01) ? 1 : 0;
    c->ring_reversed = (f->payload[2] & 0x02) ? 1 : 0;
    return 1;
}

uint8_t mm_encode_hello_ack(uint8_t major, uint8_t minor, uint8_t caps, uint8_t *out) {
    uint8_t p[3] = {major, minor, caps};
    return mm_encode(MM_T_HELLO_ACK, p, 3, out);
}

uint8_t mm_encode_pong(uint8_t *out) { return mm_encode(MM_T_PONG, 0, 0, out); }

uint8_t mm_encode_volume_change(uint8_t slot, const mm_volume_t *v, uint8_t *out) {
    uint8_t p[3];
    p[0] = slot;
    mm_vol_pack(v, &p[1]);
    return mm_encode(MM_T_VOLUME_CHANGE, p, 3, out);
}

uint8_t mm_encode_session_request(uint8_t direction, uint8_t *out) {
    return mm_encode(MM_T_SESSION_REQUEST, &direction, 1, out);
}

uint8_t mm_encode_button(uint8_t event, uint8_t *out) {
    return mm_encode(MM_T_BUTTON, &event, 1, out);
}
