#pragma once

#include <cstdint>
#include <arpa/inet.h>

#ifdef __SWITCH__
#include <switch.h>
#else
// Define mock structs if compiling on host for testing
typedef uint64_t HidNpadButton;
struct HidAnalogStickState {
    int32_t x;
    int32_t y;
};
struct PadState {
    int dummy;
};
#endif

#pragma pack(push, 1)
struct GamepadPacket {
    char magic[4] = {'D', 'S', 'G', 'P'};
    uint8_t version = 1;
    uint8_t controllerId = 0;
    uint16_t buttons = 0;
    uint8_t leftTrigger = 0;
    uint8_t rightTrigger = 0;
    int16_t leftX = 0;
    int16_t leftY = 0;
    int16_t rightX = 0;
    int16_t rightY = 0;
    uint32_t sequence = 0;
    uint16_t reserved = 0;
};
#pragma pack(pop)

class InputManager {
private:
    PadState _pad;
    uint32_t _sequence = 0;
    bool _initialized = false;

public:
    InputManager();
    ~InputManager();

    bool init();
    
    // Reads controller state and builds a big-endian GamepadPacket.
    // Returns true if button state has changed, or if 250ms have elapsed (heartbeat).
    bool pollInput(GamepadPacket& packet, bool forceHeartbeat);
};
