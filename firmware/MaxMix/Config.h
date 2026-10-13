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

/* VERIFY against your hardware: ring size and OLED geometry/address. */
#define PIXELS_COUNT 8
#define OLED_WIDTH 128
#define OLED_HEIGHT 32
#define OLED_ADDRESS 0x3C

#define LONG_PRESS_MS 600
#define FRAME_MS 33 /* ~30 fps ring refresh */

#endif
