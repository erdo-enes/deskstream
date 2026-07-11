#pragma once

#include <cstdint>
#include <SDL2/SDL_audio.h>

class AudioPlayer {
private:
    SDL_AudioDeviceID _device = 0;
    bool _initialized = false;
    bool _muted = false;

public:
    AudioPlayer();
    ~AudioPlayer();

    bool init();
    void close();
    
    void play(const uint8_t* pcmData, uint32_t len);
    void setMute(bool mute) { _muted = mute; }
    bool isMuted() const { return _muted; }
};
