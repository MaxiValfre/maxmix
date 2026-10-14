/* MaxMix firmware v2 hardware configuration (Arduino Nano, ATmega328p). */
#ifndef MM_CONFIG_H
#define MM_CONFIG_H

#define FW_MAJOR 2
#define FW_MINOR 0
#define FW_CAPS 0x01 /* bit0: autonomous lighting */

#define SERIAL_BAUD 76800

/* Pins (same wiring as the original MaxMix kit). */
#define PIN_PIXELS 12
#define PIN_ENCODER_A A1
#define PIN_ENCODER_B A2
#define PIN_SWITCH A3
/* OLED on hardware I2C: SDA=A4, SCL=A5. */

/* Defaults match the original kit (verified on hardware). Change them for other builds. */
#define PIXELS_COUNT 8
/* Master ring brightness 0..255 (original 1.5.x: 96). Mind the USB current. */
#define PIXELS_BRIGHTNESS 128
#define OLED_WIDTH 128
#define OLED_HEIGHT 32
#define OLED_ADDRESS 0x3C
/* 0 = normal, 2 = 180 degrees (the MaxMix enclosure mounts the OLED upside down). */
#define OLED_ROTATION 2

#define LONG_PRESS_MS 600
#define FRAME_MS 33 /* ~30 fps ring refresh */

#endif
