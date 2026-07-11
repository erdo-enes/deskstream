#include "input.hpp"
#include <cstring>
#include <iostream>

#ifndef __SWITCH__
// Mocks for compilation on standard host OS (macOS/Linux/Windows)
static void padConfigureInput(int, int) {}
static void padInitializeDefault(PadState*) {}
static void padUpdate(PadState*) {}
static uint64_t padGetButtons(PadState*) { return 0; }
static HidAnalogStickState padGetStickPos(PadState*, int) { return {0, 0}; }
enum {
    HidNpadButton_A = 1, HidNpadButton_B = 2, HidNpadButton_X = 4, HidNpadButton_Y = 8,
    HidNpadButton_L = 16, HidNpadButton_R = 32, HidNpadButton_ZL = 64, HidNpadButton_ZR = 128,
    HidNpadButton_Plus = 256, HidNpadButton_Minus = 512, HidNpadButton_StickL = 1024, HidNpadButton_StickR = 2048,
    HidNpadButton_Up = 4096, HidNpadButton_Down = 8192, HidNpadButton_Left = 16384, HidNpadButton_Right = 32768
};
#endif

InputManager::InputManager() {}

InputManager::~InputManager() {}

bool InputManager::init() {
#ifdef __SWITCH__
    padConfigureInput(1, HidNpadStyleSet_NpadStandard);
    padInitializeDefault(&_pad);
#else
    padInitializeDefault(&_pad);
#endif
    _initialized = true;
    _sequence = 0;
    return true;
}

bool InputManager::pollInput(GamepadPacket& packet, bool forceHeartbeat) {
    if (!_initialized) return false;

    padUpdate(&_pad);
    uint64_t buttonsHeld = padGetButtons(&_pad);
    HidAnalogStickState stickL = padGetStickPos(&_pad, 0);
    HidAnalogStickState stickR = padGetStickPos(&_pad, 1);

    static uint64_t prevButtonsHeld = 0;
    static int16_t prevLeftX = 0, prevLeftY = 0, prevRightX = 0, prevRightY = 0;

    uint16_t xButtons = 0;
    if (buttonsHeld & HidNpadButton_Up)    xButtons |= 0x0001;
    if (buttonsHeld & HidNpadButton_Down)  xButtons |= 0x0002;
    if (buttonsHeld & HidNpadButton_Left)  xButtons |= 0x0004;
    if (buttonsHeld & HidNpadButton_Right) xButtons |= 0x0008;
    if (buttonsHeld & HidNpadButton_Plus)  xButtons |= 0x0010; // Start
    if (buttonsHeld & HidNpadButton_Minus) xButtons |= 0x0020; // Back
    if (buttonsHeld & HidNpadButton_StickL) xButtons |= 0x0040;
    if (buttonsHeld & HidNpadButton_StickR) xButtons |= 0x0080;
    if (buttonsHeld & HidNpadButton_L)      xButtons |= 0x0100;
    if (buttonsHeld & HidNpadButton_R)      xButtons |= 0x0200;
    
    // Nintendo Switch to Xbox Layout Mapping:
    // Switch B -> Xbox A
    // Switch A -> Xbox B
    // Switch Y -> Xbox X
    // Switch X -> Xbox Y
    if (buttonsHeld & HidNpadButton_B) xButtons |= 0x1000;
    if (buttonsHeld & HidNpadButton_A) xButtons |= 0x2000;
    if (buttonsHeld & HidNpadButton_Y) xButtons |= 0x4000;
    if (buttonsHeld & HidNpadButton_X) xButtons |= 0x8000;

    uint8_t leftTrigger = (buttonsHeld & HidNpadButton_ZL) ? 255 : 0;
    uint8_t rightTrigger = (buttonsHeld & HidNpadButton_ZR) ? 255 : 0;

    int16_t lX = (int16_t)stickL.x;
    int16_t lY = (int16_t)stickL.y;
    int16_t rX = (int16_t)stickR.x;
    int16_t rY = (int16_t)stickR.y;

    bool changed = (buttonsHeld != prevButtonsHeld) || 
                   (lX != prevLeftX) || (lY != prevLeftY) ||
                   (rX != prevRightX) || (rY != prevRightY);

    if (changed || forceHeartbeat) {
        prevButtonsHeld = buttonsHeld;
        prevLeftX = lX; prevLeftY = lY;
        prevRightX = rX; prevRightY = rY;

        packet.magic[0] = 'D'; packet.magic[1] = 'S'; packet.magic[2] = 'G'; packet.magic[3] = 'P';
        packet.version = 1;
        packet.controllerId = 0;
        packet.buttons = htons(xButtons);
        packet.leftTrigger = leftTrigger;
        packet.rightTrigger = rightTrigger;
        packet.leftX = htons(lX);
        packet.leftY = htons(lY);
        packet.rightX = htons(rX);
        packet.rightY = htons(rY);
        packet.sequence = htonl(_sequence++);
        packet.reserved = 0;

        return true;
    }

    return false;
}
