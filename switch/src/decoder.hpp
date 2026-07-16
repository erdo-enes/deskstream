#pragma once

#include <cstdint>
#include <cstddef>

struct AVCodec;
struct AVCodecContext;
struct AVFrame;
struct AVPacket;

class H264Decoder {
private:
    const AVCodec* _codec = nullptr;
    AVCodecContext* _ctx = nullptr;
    AVFrame* _frame = nullptr;
    AVPacket* _packet = nullptr;
    
    int _width = 0;
    int _height = 0;
    bool _initialized = false;

public:
    H264Decoder();
    ~H264Decoder();

    bool init(int width, int height);
    void close();
    void flush();

    // Decodes H.264 Annex-B access unit.
    // If a frame is ready, returns true and updates pointers to Y, U, V planes.
    bool decode(const uint8_t* data, size_t size, uint32_t frameId,
                uint8_t*& yPlane, uint8_t*& uPlane, uint8_t*& vPlane,
                int& yPitch, int& uPitch, int& vPitch, uint32_t& decodedFrameId);
};
