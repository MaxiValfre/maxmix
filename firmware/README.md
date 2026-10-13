# MaxMix firmware v2 (Arduino Nano / ATmega328p)

Replacement firmware for the v2 protocol: framed messages with a CRC, and LED
ring lighting rendered on the device itself.

## Layout

| File | Purpose |
| --- | --- |
| `MaxMix/mm_proto.[ch]` | Portable protocol core (no Arduino dependencies) |
| `MaxMix/mm_light.[ch]` | Portable lighting: static/pulse/fade, idle, ring reversal |
| `MaxMix/MaxMix.ino` | Hardware glue: serial, encoder, button, OLED, NeoPixel ring |
| `MaxMix/Config.h` | Pins and hardware constants |
| `test/` | Host tests, checked against `vectors.h` (generated from the Rust crate) |

## Before you flash: check `Config.h`

`PIXELS_COUNT`, `OLED_WIDTH`/`OLED_HEIGHT` and `OLED_ADDRESS` are marked
**VERIFY**. Set them to match your hardware. The pins follow the original kit:
NeoPixels on D12, encoder on A1/A2, switch on A3, OLED on A4/A5.

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
| Short press | Toggle between navigate and edit modes |
| Long press | Mute or unmute the current app |
