#include "audio.hpp"
#include <iostream>
#include <SDL2/SDL.h>

AudioPlayer::AudioPlayer() {}

AudioPlayer::~AudioPlayer() {
    close();
}

bool AudioPlayer::init() {
    close();

    SDL_AudioSpec wanted, obtained;
    SDL_zero(wanted);
    wanted.freq = 48000;
    wanted.format = AUDIO_S16LSB; // Signed 16-bit little-endian
    wanted.channels = 2;
    wanted.samples = 240; // 5ms buffer matching the stream packets
    wanted.callback = nullptr; // Queue-based audio delivery

    _device = SDL_OpenAudioDevice(nullptr, 0, &wanted, &obtained, 0);
    if (_device == 0) {
        std::cerr << "[audio] failed to open SDL audio device: " << SDL_GetError() << std::endl;
        return false;
    }

    SDL_PauseAudioDevice(_device, 0); // Start playback immediately
    _initialized = true;
    return true;
}

void AudioPlayer::close() {
    if (_initialized) {
        SDL_CloseAudioDevice(_device);
        _device = 0;
        _initialized = false;
    }
}

void AudioPlayer::play(const uint8_t* pcmData, uint32_t len) {
    if (!_initialized || _muted) return;

    // Prevent audio backlog lag by purging queue if it grows beyond 40ms of latency.
    // 48000 samples/sec * 2 channels * 2 bytes/sample * 0.040s = 7680 bytes
    uint32_t maxQueuedBytes = 48000 * 2 * 2 * 40 / 1000; 
    if (SDL_GetQueuedAudioSize(_device) > maxQueuedBytes) {
        SDL_ClearQueuedAudio(_device);
    }

    SDL_QueueAudio(_device, pcmData, len);
}
