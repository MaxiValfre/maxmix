# MaxMix firmware helpers.
#   make test     - build & run the portable C core tests on the host (gcc)
#   make vectors  - regenerate test/vectors.h from the Rust protocol crate
#   make build    - compile the sketch for Arduino Nano (needs arduino-cli)
#   make upload PORT=/dev/ttyUSB0 - flash the Nano (old bootloader, CH340)

CC ?= gcc
CFLAGS ?= -std=c99 -Wall -Wextra -Werror -O2
FQBN ?= arduino:avr:nano:cpu=atmega328old
PORT ?= /dev/ttyUSB0

.PHONY: test vectors build upload clean

test: build-test/test_core
	./build-test/test_core

build-test/test_core: test/test_core.c test/vectors.h MaxMix/mm_proto.c MaxMix/mm_light.c MaxMix/mm_proto.h MaxMix/mm_light.h
	mkdir -p build-test
	$(CC) $(CFLAGS) -o $@ test/test_core.c MaxMix/mm_proto.c MaxMix/mm_light.c

vectors:
	cd ../linux && cargo run -q -p maxmix-protocol --example c_vectors > ../firmware/test/vectors.h

build:
	arduino-cli compile --fqbn $(FQBN) MaxMix

upload: build
	arduino-cli upload --fqbn $(FQBN) -p $(PORT) MaxMix

clean:
	rm -rf build-test
