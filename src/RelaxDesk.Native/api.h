#pragma once
#include <stdint.h>
#ifdef RD_EXPORTS
#define RD extern "C" __declspec(dllexport)
#else
#define RD extern "C" __declspec(dllimport)
#endif
// ABI v2: operations return HRESULT (queries/destruction are explicitly typed).
// S_FALSE means timeout/no output/not presented. Rebuild managed/native outputs together.
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
struct rd_decode_timing { double submit_ms, output_ms, conversion_ms, readback_ms, total_ms; uint64_t readback_bytes; };
struct rd_render_timing { double draw_ms, present_ms; uint64_t presents, skipped, resizes, cursor_upload_bytes, full_frame_readback_bytes, hidden, occluded, busy; };
struct rd_live_resources { int64_t captures, encoders, decoders, frames, renderers, windows, callbacks; };
struct rd_encode_timing {
    double upload_submit_ms, surface_setup_ms, color_submit_ms, input_wait_ms, submit_ms, output_wait_ms, pack_ms, total_ms, gpu_upload_ms, gpu_color_ms;
    uint64_t upload_bytes;
    uint32_t accepted_settings, gpu_timing_valid;
};
RD uint32_t rd_abi_version();
RD int32_t rd_capture_diagnostics(const char** json);
RD int32_t rd_capture_self_test(const char** report);
RD int32_t rd_capture_color_status(void* context,const char** json);
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
RD int32_t rd_encoder_timing(void* encoder, rd_encode_timing* timing);
RD int32_t rd_decoder_create(uint32_t width, uint32_t height, int hardware, void** decoder);
RD int32_t rd_decoder_frame(void* decoder, const uint8_t* data, uint32_t bytes, int64_t time_100ns, uint8_t* bgra, uint32_t capacity);
RD void rd_decoder_destroy(void* decoder);
RD int32_t rd_decoder_gpu_frame(void* decoder, const uint8_t* data, uint32_t bytes, int64_t time_100ns, void** frame);
RD int32_t rd_decoder_timing(void* decoder, rd_decode_timing* timing);
RD void rd_gpu_frame_destroy(void* frame);
RD int32_t rd_window_create(void* parent, void** child);
RD int32_t rd_window_destroy(void* child);
RD int32_t rd_renderer_create(void* frame, void* hwnd, void** renderer);
RD int32_t rd_renderer_draw(void* renderer, void* frame, const uint8_t* metadata, uint32_t bytes);
RD int32_t rd_renderer_present(void* renderer, rd_render_timing* timing);
RD int32_t rd_renderer_diagnostics(void* renderer, const char** json);
RD int32_t rd_renderer_probe_pixel(void* renderer, uint32_t x, uint32_t y, uint32_t* bgra);
RD void rd_renderer_destroy(void* renderer);
RD void rd_get_live_resources(rd_live_resources* resources);
RD const char* rd_last_error();
