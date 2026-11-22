#pragma once
#include <stdint.h>
#ifdef RD_EXPORTS
#define RD extern "C" __declspec(dllexport)
#else
#define RD extern "C" __declspec(dllimport)
#endif
// ABI v1: all functions return HRESULT. S_FALSE means timeout/no output.
// Contexts are thread-affine; destroy on their creating thread. No C++ exceptions cross the ABI.
struct rd_frame {
    uint32_t width, height;
    int64_t timestamp; // QPC, not wall time
    uint32_t accumulated, dirty_count, move_count, pointer_bytes;
    int32_t pointer_x, pointer_y;
    uint32_t pointer_visible, pointer_type, pointer_width, pointer_height, pointer_pitch;
    int32_t hotspot_x, hotspot_y;
};
struct rd_rect { int32_t left, top, right, bottom; };
struct rd_move { int32_t x, y; rd_rect destination; };
RD uint32_t rd_abi_version();
RD int32_t rd_capture_create(void** context);
RD int32_t rd_capture_next(void* context, uint32_t timeout_ms, rd_frame* frame);
RD int32_t rd_capture_blob(void* context, uint32_t kind, const void** data, uint32_t* bytes);
RD int32_t rd_capture_pixels(void* context, uint8_t* data, uint32_t bytes);
RD void rd_capture_destroy(void* context);
RD int32_t rd_encoder_create(void* capture, uint32_t width, uint32_t height, uint32_t fps, uint32_t bitrate, int hardware, void** encoder);
RD int32_t rd_encoder_frame(void* encoder, int64_t time_100ns, int force_keyframe, const uint8_t** data, uint32_t* bytes, int* keyframe, int64_t* output_time);
RD int32_t rd_encoder_bgra(void* encoder, const uint8_t* data, uint32_t bytes, int64_t time_100ns, int force_keyframe, const uint8_t** output, uint32_t* length, int* keyframe, int64_t* output_time);
RD void rd_encoder_destroy(void* encoder);
RD const char* rd_encoder_name(void* encoder);
RD int32_t rd_decoder_create(uint32_t width, uint32_t height, int hardware, void** decoder);
RD int32_t rd_decoder_frame(void* decoder, const uint8_t* data, uint32_t bytes, int64_t time_100ns, uint8_t* bgra, uint32_t capacity);
RD void rd_decoder_destroy(void* decoder);
RD const char* rd_last_error();
