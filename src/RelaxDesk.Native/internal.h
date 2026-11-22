#pragma once
#define RD_EXPORTS
#include "api.h"
#include <windows.h>
#include <d3d11.h>
#include <d3d10.h>
#include <dxgi1_2.h>
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
using Microsoft::WRL::ComPtr;
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
struct capture_context {
    runtime lifetime; gpu graphics;
    ComPtr<IDXGIOutputDuplication> duplication;
    ComPtr<ID3D11Texture2D> texture, staging;
    std::vector<RECT> dirty; std::vector<DXGI_OUTDUPL_MOVE_RECT> moves; std::vector<uint8_t> pointer;
    DXGI_OUTDUPL_POINTER_SHAPE_INFO shape{};
    DXGI_OUTDUPL_POINTER_POSITION pointer_position{};
    DXGI_OUTPUT_DESC output{};
    void initialize(); void acquire(uint32_t timeout, rd_frame& info);
};
inline ComPtr<IMFMediaType> video_type(GUID subtype, uint32_t width, uint32_t height, uint32_t fps) {
    ComPtr<IMFMediaType> type;check(MFCreateMediaType(&type),"MFCreateMediaType");
    check(type->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video),"major type");check(type->SetGUID(MF_MT_SUBTYPE,subtype),"subtype");
    check(MFSetAttributeSize(type.Get(),MF_MT_FRAME_SIZE,width,height),"frame size");check(MFSetAttributeRatio(type.Get(),MF_MT_FRAME_RATE,fps,1),"frame rate");
    check(MFSetAttributeRatio(type.Get(),MF_MT_PIXEL_ASPECT_RATIO,1,1),"pixel aspect");check(type->SetUINT32(MF_MT_INTERLACE_MODE,MFVideoInterlace_Progressive),"interlace");return type;
}
