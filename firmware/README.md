# MaxMix firmware v2 (Arduino Nano / ATmega328p)

Replacement firmware for the v2 protocol: framed messages with a CRC, and LED
ring lighting rendered on the device itself.

## Layout

| File | Purpose |
| --- | --- |
| `MaxMix/mm_proto.[ch]` | Portable protocol core (no Arduino dependencies) |
| `MaxMix/mm_light.[ch]` | Portable lighting: static/pulse/fade/rainbow/carousel, 1–8 colour palette, volume gauge, idle |
| `MaxMix/MaxMix.ino` | Hardware glue: serial, encoder, button, OLED, NeoPixel ring |
| `MaxMix/Config.h` | Pins and hardware constants |
| `test/` | Host tests, checked against `vectors.h` (generated from the Rust crate) |

## Precompiled firmware

Each Linux release ships `maxmix-firmware-v2.hex`, built with the defaults
below for the original kit. Flashing instructions are in the
[main README](../README.md#2-flash-the-v2-firmware).

## Before you build: check `Config.h`

The defaults match the original kit: 8-LED ring (`PIXELS_COUNT`), 128×32
SSD1306 OLED at `0x3C`, mounted upside down (`OLED_ROTATION 2`), ring
brightness 128/255. The pins follow the original kit: NeoPixels on D12,
encoder on A1/A2, switch on A3, OLED on A4/A5.

## Commands

```bash
make test      # host tests for the portable core (gcc)
make vectors   # regenerate test/vectors.h after a protocol change

# One-time setup (Arch):
sudo pacman -S --needed arduino-cli avrdude
arduino-cli core install arduino:avr
arduino-cli lib install "Adafruit NeoPixel" "Adafruit SSD1306" "Adafruit GFX Library"

make build                     # compile for the Nano (new bootloader, 115200)
make upload PORT=/dev/ttyUSB0  # flash
```

If the upload fails to sync, the board has the old bootloader. Use
`make upload FQBN=arduino:avr:nano:cpu=atmega328old`.

Back up the stock firmware before flashing (restore with `-U flash:w:...`):

```bash
avrdude -p m328p -c arduino -P /dev/ttyUSB0 -b 115200 -U flash:r:backup/maxmix-1.5.6-flash.hex:i
```

## Controls

| Input | Action |
| --- | --- |
| Turn | Navigate apps (navigate mode) or change volume (edit mode) |
| Tap | Toggle between navigate and edit modes (applied after 300 ms, to tell it from a double tap) |
| Double tap | Mute or unmute the current app |
| Long press | Mute or unmute the current app |

The encoder is decoded in a pin-change interrupt, so no detent is lost while
the OLED redraws.

## Ring behaviour

| Situation | Ring |
| --- | --- |
| Normal | The lighting mode, using the palette |
| Volume changes (knob or PC) | Fills from the *low* to the *high* volume colour for 1.5 s |
| Edit mode | The volume fill, the whole time |
| Current app muted | The volume fill, dim and slowly breathing |
| Idle | The idle mode, after the idle timeout |
