#pragma once
#define RD_EXPORTS
#include "api.h"
#include <windows.h>
#include <d3d11.h>
#include <d3d10.h>
#include <dxgi1_6.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mftransform.h>
#include <wrl/client.h>
#include <vector>
#include <string>
#include <stdexcept>
#include <memory>
#include <cstring>
#include <atomic>
#include <chrono>
using Microsoft::WRL::ComPtr;
inline std::atomic<int64_t> live_resources[7]{};
struct resource_counter {
    size_t index;
    explicit resource_counter(size_t i):index(i){++live_resources[index];}
    ~resource_counter(){--live_resources[index];}
};
inline int64_t ticks(){LARGE_INTEGER value;QueryPerformanceCounter(&value);return value.QuadPart;}
inline double elapsed_ms(int64_t start){static double frequency=[](){LARGE_INTEGER f;QueryPerformanceFrequency(&f);return static_cast<double>(f.QuadPart);}();return (ticks()-start)*1000.0/frequency;}
extern thread_local std::string rd_error;
struct failure { HRESULT hr; };
inline void check(HRESULT hr, const char* operation) { if(FAILED(hr)) {rd_error=operation; throw failure{hr};} }
template<class F> HRESULT guarded(F&& f) noexcept {
    try { f(); return S_OK; } catch(failure e) {return e.hr;} catch(std::bad_alloc&) {rd_error="allocation";return E_OUTOFMEMORY;} catch(...) {rd_error="native exception";return E_FAIL;}
}
struct runtime {
    DWORD thread=GetCurrentThreadId(); bool com=false, mf=false;
    runtime() {HRESULT hr=CoInitializeEx(nullptr, COINIT_MULTITHREADED);check(hr,"CoInitializeEx MTA");com=true;hr=MFStartup(MF_VERSION,MFSTARTUP_LITE);if(FAILED(hr)){CoUninitialize();com=false;check(hr,"MFStartup");}mf=true;}
    void verify() {if(thread!=GetCurrentThreadId())check(RPC_E_WRONG_THREAD,"context used on different thread");}
    ~runtime(){if(mf)MFShutdown();if(com)CoUninitialize();}
};
struct gpu {
    ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> commands;
    void create(IDXGIAdapter* adapter=nullptr);
};
struct desktop_color {
    bool hdr=false,white_known=false;
    DXGI_COLOR_SPACE_TYPE output_space=DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
    float white_nits=80;
};
struct desktop_converter {
    ComPtr<ID3D11Texture2D> readable;
    ComPtr<ID3D11ShaderResourceView> view;
    ComPtr<ID3D11VertexShader> vertex;
    ComPtr<ID3D11PixelShader> pixel;
    ComPtr<ID3D11Buffer> constants;
    void convert(gpu& graphics,ID3D11Texture2D* source,ID3D11Texture2D* target,const desktop_color& color);
};
struct capture_context {
    std::wstring selected_device;
    resource_counter tracked{0};
    runtime lifetime; gpu graphics;
    ComPtr<IDXGIOutputDuplication> duplication;
    ComPtr<ID3D11Texture2D> texture, staging;
    std::vector<RECT> dirty; std::vector<DXGI_OUTDUPL_MOVE_RECT> moves; std::vector<uint8_t> pointer;
    DXGI_OUTDUPL_POINTER_SHAPE_INFO shape{};
    DXGI_OUTDUPL_POINTER_POSITION pointer_position{};
    DXGI_OUTPUT_DESC output{};
    DXGI_OUTDUPL_DESC duplication_desc{};
    bool format_negotiated=false;
    int64_t last_desktop_time=0;
    bool previous_protected_mask=false;
    uint64_t last_copy_pixels=0;
    desktop_color color;
    ComPtr<IDXGIOutput6> color_output;
    std::unique_ptr<desktop_converter> converter;
    DXGI_FORMAT source_format=DXGI_FORMAT_UNKNOWN;
    ULONGLONG color_checked=0;
    void refresh_color();
    bool accept_surface(const DXGI_OUTDUPL_FRAME_INFO& info,IDXGIResource* resource);
    void initialize(); void acquire(uint32_t timeout, rd_frame& info);
};
struct gpu_frame {
    resource_counter tracked{3};
    ComPtr<IMFSample> sample;
    ComPtr<ID3D11Texture2D> texture;
    UINT subresource=0,width=0,height=0;
};
inline ComPtr<IMFMediaType> video_type(GUID subtype, uint32_t width, uint32_t height, uint32_t fps) {
    ComPtr<IMFMediaType> type;check(MFCreateMediaType(&type),"MFCreateMediaType");
    check(type->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video),"major type");check(type->SetGUID(MF_MT_SUBTYPE,subtype),"subtype");
    check(MFSetAttributeSize(type.Get(),MF_MT_FRAME_SIZE,width,height),"frame size");check(MFSetAttributeRatio(type.Get(),MF_MT_FRAME_RATE,fps,1),"frame rate");
    check(MFSetAttributeRatio(type.Get(),MF_MT_PIXEL_ASPECT_RATIO,1,1),"pixel aspect");check(type->SetUINT32(MF_MT_INTERLACE_MODE,MFVideoInterlace_Progressive),"interlace");return type;
}
