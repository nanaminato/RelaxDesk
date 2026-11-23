#include "internal.h"
#include <shellscalingapi.h>
#include <algorithm>

namespace {
struct enumeration { std::vector<rd_display> values; HRESULT error=S_OK; };
BOOL CALLBACK monitor(HMONITOR handle,HDC,LPRECT,LPARAM data) noexcept {
    auto& state=*reinterpret_cast<enumeration*>(data);
    try {
        if(state.values.size()>=32){state.error=E_INVALIDARG;return FALSE;}
        MONITORINFOEXW info{};info.cbSize=sizeof(info);
        if(!GetMonitorInfoW(handle,&info)){state.error=HRESULT_FROM_WIN32(GetLastError());return FALSE;}
        DEVMODEW mode{};mode.dmSize=sizeof(mode);
        if(!EnumDisplaySettingsExW(info.szDevice,ENUM_CURRENT_SETTINGS,&mode,0)){state.error=E_FAIL;return FALSE;}
        DEVICE_SCALE_FACTOR scale=SCALE_100_PERCENT;
        if(FAILED(GetScaleFactorForMonitor(handle,&scale))){state.error=E_FAIL;return FALSE;}
        // Nominal scale-derived DPI, not raw physical DPI/custom fractional DPI.
        UINT x=(static_cast<UINT>(scale)*96+50)/100,y=x;
        rd_display display{};wcscpy_s(display.name,info.szDevice);
        display.x=info.rcMonitor.left;display.y=info.rcMonitor.top;
        display.width=info.rcMonitor.right-info.rcMonitor.left;display.height=info.rcMonitor.bottom-info.rcMonitor.top;
        display.rotation=static_cast<int32_t>(mode.dmDisplayOrientation*90);
        display.dpi_x=x;display.dpi_y=y;display.primary=(info.dwFlags&MONITORINFOF_PRIMARY)!=0;display.hdr=-1;
        state.values.push_back(display);return TRUE;
    } catch(...) {state.error=E_OUTOFMEMORY;return FALSE;}
}
}
RD int32_t rd_display_list(rd_display* displays,uint32_t capacity,uint32_t* count,int32_t include_hdr){
    if(!count||!displays||capacity<1||capacity>32)return E_INVALIDARG;*count=0;
    return guarded([&]{
        auto prior=SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if(!prior)check(HRESULT_FROM_WIN32(GetLastError()),"display DPI awareness context");
        struct restore { DPI_AWARENESS_CONTEXT prior;~restore(){SetThreadDpiAwarenessContext(prior);} } scope{prior};
        enumeration state;BOOL ok=EnumDisplayMonitors(nullptr,nullptr,monitor,reinterpret_cast<LPARAM>(&state));
        check(state.error,"enumerate physical display metadata");if(!ok)check(E_FAIL,"EnumDisplayMonitors");
        if(state.values.empty()||state.values.size()>capacity)check(E_INVALIDARG,"display count exceeds capacity");
        // Metadata query only: never create D3D devices or duplication before consent.
        ComPtr<IDXGIFactory1> factory;
        if(include_hdr&&SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))))for(UINT a=0;a<64;++a){
            ComPtr<IDXGIAdapter1> adapter;if(FAILED(factory->EnumAdapters1(a,&adapter)))break;
            for(UINT o=0;o<64;++o){ComPtr<IDXGIOutput> output;if(FAILED(adapter->EnumOutputs(o,&output)))break;
                ComPtr<IDXGIOutput6> newer;DXGI_OUTPUT_DESC1 description{};
                if(FAILED(output.As(&newer))||FAILED(newer->GetDesc1(&description)))continue;
                for(auto& value:state.values)if(wcscmp(value.name,description.DeviceName)==0)value.hdr=description.ColorSpace==DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020?1:0;
            }
        }
        std::copy(state.values.begin(),state.values.end(),displays);*count=static_cast<uint32_t>(state.values.size());
    });
}
