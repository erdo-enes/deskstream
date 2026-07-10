SDKROOT := $(shell xcrun --sdk macosx --show-sdk-path)
CC := xcrun --sdk macosx clang
ARCH ?= arm64
DEPLOYMENT_TARGET ?= 13.0
BUILD_DIR := build
TEST_BINARY := $(BUILD_DIR)/core-tests
APP_NAME := DeskStream
APP_BUNDLE := $(BUILD_DIR)/$(APP_NAME).app
APP_EXECUTABLE := $(APP_BUNDLE)/Contents/MacOS/$(APP_NAME)
CORE_SOURCES := $(sort $(wildcard Core/*.m))
CORE_HEADERS := $(sort $(wildcard Core/*.h))
TEST_SOURCES := $(sort $(wildcard Tests/*.m))
APP_SOURCES := $(sort $(wildcard App/*.m))
APP_HEADERS := $(sort $(wildcard App/*.h))

OBJCFLAGS := -isysroot $(SDKROOT) \
	-arch $(ARCH) \
	-mmacosx-version-min=$(DEPLOYMENT_TARGET) \
	-fobjc-arc -fblocks -fmodules -fmodules-cache-path=$(BUILD_DIR)/ModuleCache \
	-std=gnu11 \
	-Wall -Wextra -Werror \
	-I Core -I App

FRAMEWORKS := -framework Foundation -framework Security
APP_FRAMEWORKS := $(FRAMEWORKS) \
	-framework AppKit \
	-framework AVFoundation \
	-framework CoreMedia \
	-framework QuartzCore \
	-framework CoreGraphics \
	-framework GameController \
	-framework CoreHaptics

.PHONY: all app core-test test clean

all: app core-test

app: $(APP_BUNDLE)

core-test: $(TEST_BINARY)

test: $(TEST_BINARY)
	$(TEST_BINARY)

$(TEST_BINARY): $(CORE_SOURCES) $(CORE_HEADERS) $(TEST_SOURCES) Makefile
	mkdir -p $(BUILD_DIR)
	$(CC) $(OBJCFLAGS) $(CORE_SOURCES) $(TEST_SOURCES) $(FRAMEWORKS) -o $@

$(APP_BUNDLE): $(CORE_SOURCES) $(CORE_HEADERS) $(APP_SOURCES) $(APP_HEADERS) Resources/Info.plist Makefile
	mkdir -p $(APP_BUNDLE)/Contents/MacOS $(APP_BUNDLE)/Contents/Resources
	$(CC) $(OBJCFLAGS) $(CORE_SOURCES) $(APP_SOURCES) $(APP_FRAMEWORKS) -o $(APP_EXECUTABLE)
	cp Resources/Info.plist $(APP_BUNDLE)/Contents/Info.plist
	/usr/bin/codesign --force --sign - --timestamp=none $(APP_BUNDLE)
	touch $(APP_BUNDLE)

clean:
	rm -rf $(BUILD_DIR)
