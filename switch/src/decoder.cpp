#include "decoder.hpp"
#include <iostream>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/imgutils.h>
}

H264Decoder::H264Decoder() {}

H264Decoder::~H264Decoder() {
    close();
}

bool H264Decoder::init(int width, int height) {
    close();
    _width = width;
    _height = height;

    _codec = avcodec_find_decoder(AV_CODEC_ID_H264);
    if (!_codec) {
        std::cerr << "[decoder] H.264 decoder not found" << std::endl;
        return false;
    }

    _ctx = avcodec_alloc_context3(_codec);
    if (!_ctx) {
        std::cerr << "[decoder] failed to allocate codec context" << std::endl;
        return false;
    }

    _ctx->width = width;
    _ctx->height = height;
    
    // Enable low-latency decoding optimizations
    _ctx->flags |= AV_CODEC_FLAG_LOW_DELAY;
    _ctx->flags2 |= AV_CODEC_FLAG2_FAST;
    _ctx->thread_count = 4; // Utilize all available Switch CPU cores
    _ctx->thread_type = FF_THREAD_SLICE;

    if (avcodec_open2(_ctx, _codec, nullptr) < 0) {
        std::cerr << "[decoder] failed to open codec" << std::endl;
        avcodec_free_context(&_ctx);
        _ctx = nullptr;
        return false;
    }

    _frame = av_frame_alloc();
    _packet = av_packet_alloc();
    _initialized = true;
    return true;
}

void H264Decoder::close() {
    if (_initialized) {
        if (_frame) av_frame_free(&_frame);
        if (_packet) av_packet_free(&_packet);
        if (_ctx) {
            avcodec_free_context(&_ctx);
            _ctx = nullptr;
        }
        _initialized = false;
    }
}

void H264Decoder::flush() {
    if (_initialized && _ctx) avcodec_flush_buffers(_ctx);
}

bool H264Decoder::decode(const uint8_t* data, size_t size, uint32_t frameId,
                        uint8_t*& yPlane, uint8_t*& uPlane, uint8_t*& vPlane,
                        int& yPitch, int& uPitch, int& vPitch, uint32_t& decodedFrameId) {
    if (!_initialized) return false;

    _packet->data = const_cast<uint8_t*>(data);
    _packet->size = (int)size;
    _packet->pts = frameId;
    _packet->dts = frameId;

    int ret = avcodec_send_packet(_ctx, _packet);
    if (ret < 0) return false;

    ret = avcodec_receive_frame(_ctx, _frame);
    if (ret == 0) {
        yPlane = _frame->data[0];
        uPlane = _frame->data[1];
        vPlane = _frame->data[2];
        yPitch = _frame->linesize[0];
        uPitch = _frame->linesize[1];
        vPitch = _frame->linesize[2];
        int64_t outputId = _frame->best_effort_timestamp;
        if (outputId == AV_NOPTS_VALUE) outputId = _frame->pts;
        decodedFrameId = outputId == AV_NOPTS_VALUE
            ? frameId
            : static_cast<uint32_t>(outputId);
        return true;
    }

    return false;
}
