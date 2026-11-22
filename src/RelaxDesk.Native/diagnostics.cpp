#include "internal.h"
#include <dxgi1_6.h>
#include <wtsapi32.h>
#include <sstream>
#include <iomanip>

namespace {
std::string utf8(const WCHAR* text) {
    int length=static_cast<int>(wcslen(text));int bytes=WideCharToMultiByte(CP_UTF8,0,text,length,nullptr,0,nullptr,nullptr);
    std::string result(bytes,'\0');WideCharToMultiByte(CP_UTF8,0,text,length,result.data(),bytes,nullptr,nullptr);return result;
}
std::string quote(const std::string& text) {
    std::ostringstream s;s<<'"';for(unsigned char c:text){if(c=='"'||c=='\\')s<<'\\'<<c;else if(c<32)s<<"\\u"<<std::hex<<std::setw(4)<<std::setfill('0')<<static_cast<int>(c);else s<<c;}s<<'"';return s.str();
}
std::string object_name(HANDLE object) {
    WCHAR name[256]{};DWORD needed=0;if(!object||!GetUserObjectInformationW(object,UOI_NAME,name,sizeof(name),&needed))return "unavailable";return utf8(name);
}
std::string result(HRESULT hr) {
    std::ostringstream s;s<<"{\"hresult\":\"0x"<<std::hex<<std::setw(8)<<std::setfill('0')<<static_cast<uint32_t>(hr)<<"\",\"reason\":";
    const char* reason=hr==E_ACCESSDENIED?"access denied: current session/desktop or security restriction; do not elevate/bypass":
        hr==DXGI_ERROR_ACCESS_LOST?"desktop/output changed; recreate capture and encoder":hr==DXGI_ERROR_DEVICE_REMOVED?"device removed; recreate device, capture and encoder":
        hr==DXGI_ERROR_DEVICE_RESET?"device reset; recreate device, capture and encoder":hr==DXGI_ERROR_UNSUPPORTED?"output/driver does not support duplication":
        hr==DXGI_ERROR_NOT_CURRENTLY_AVAILABLE?"duplication currently unavailable (including session/consumer limits)":hr==DXGI_ERROR_SESSION_DISCONNECTED?"session disconnected":
        hr==DXGI_ERROR_WAIT_TIMEOUT?"no desktop update before timeout":SUCCEEDED(hr)?"success":"API failed; preserve HRESULT";
    s<<quote(reason)<<'}';return s.str();
}
}
RD int32_t rd_capture_diagnostics(const char** json) {
    if(!json)return E_POINTER;
    static thread_local std::string report;
    return guarded([&]{
        DWORD session=0;check(ProcessIdToSessionId(GetCurrentProcessId(),&session)?S_OK:HRESULT_FROM_WIN32(GetLastError()),"ProcessIdToSessionId");
        LPWSTR information=nullptr;DWORD length=0;USHORT protocol=0xffff;WTS_CONNECTSTATE_CLASS state=WTSDown;
        if(WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE,session,WTSClientProtocolType,&information,&length)){if(length>=sizeof(USHORT))protocol=*reinterpret_cast<USHORT*>(information);WTSFreeMemory(information);}
        if(WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE,session,WTSConnectState,&information,&length)){if(length>=sizeof(state))state=*reinterpret_cast<WTS_CONNECTSTATE_CLASS*>(information);WTSFreeMemory(information);}
        USEROBJECTFLAGS flags{};DWORD needed=0;GetUserObjectInformationW(GetProcessWindowStation(),UOI_FLAGS,&flags,sizeof(flags),&needed);
        HDESK input=OpenInputDesktop(0,FALSE,DESKTOP_READOBJECTS);DWORD input_error=input?0:GetLastError();std::string input_name=object_name(input);if(input)CloseDesktop(input);
        HANDLE token=nullptr;bool elevated=false;DWORD integrity=0;
        if(OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token)){TOKEN_ELEVATION elevation{};if(GetTokenInformation(token,TokenElevation,&elevation,sizeof(elevation),&needed))elevated=elevation.TokenIsElevated!=0;
            GetTokenInformation(token,TokenIntegrityLevel,nullptr,0,&needed);std::vector<uint8_t> bytes(needed);
            if(needed&&GetTokenInformation(token,TokenIntegrityLevel,bytes.data(),needed,&needed)){auto label=reinterpret_cast<TOKEN_MANDATORY_LABEL*>(bytes.data());auto sid=label->Label.Sid;integrity=*GetSidSubAuthority(sid,*GetSidSubAuthorityCount(sid)-1);}CloseHandle(token);}
        bool remote=protocol==2||GetSystemMetrics(SM_REMOTESESSION)!=0;bool visible=(flags.dwFlags&WSF_VISIBLE)!=0;
        const char* environment=!visible||session==0?"noninteractive":remote?"rdp":session==WTSGetActiveConsoleSessionId()&&protocol==0?"local-console":"other-session";
        std::ostringstream s;s<<"{\"process_id\":"<<GetCurrentProcessId()<<",\"session_id\":"<<session<<",\"active_console_session\":"<<WTSGetActiveConsoleSessionId()
            <<",\"protocol\":"<<protocol<<",\"connection_state\":"<<state<<",\"environment\":"<<quote(environment)<<",\"window_station\":"<<quote(object_name(GetProcessWindowStation()))
            <<",\"station_visible\":"<<(visible?"true":"false")<<",\"thread_desktop\":"<<quote(object_name(GetThreadDesktop(GetCurrentThreadId())))<<",\"input_desktop\":"<<quote(input_name)
            <<",\"input_desktop_error\":"<<input_error<<",\"elevated\":"<<(elevated?"true":"false")<<",\"integrity_rid\":"<<integrity<<",\"display_devices\":[";
        bool first=true;for(DWORD i=0;i<64;++i){DISPLAY_DEVICEW display{};display.cb=sizeof(display);if(!EnumDisplayDevicesW(nullptr,i,&display,0))break;if(!first)s<<',';first=false;
            s<<"{\"name\":"<<quote(utf8(display.DeviceName))<<",\"description\":"<<quote(utf8(display.DeviceString))<<",\"flags\":"<<display.StateFlags<<'}';}
        s<<"],\"adapters\":[";ComPtr<IDXGIFactory1> factory;check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)),"diagnostic DXGI factory");first=true;
        for(UINT a=0;a<64;++a){ComPtr<IDXGIAdapter1> adapter;HRESULT hr=factory->EnumAdapters1(a,&adapter);if(hr==DXGI_ERROR_NOT_FOUND)break;check(hr,"diagnostic EnumAdapters1");
            DXGI_ADAPTER_DESC1 desc{};check(adapter->GetDesc1(&desc),"diagnostic adapter desc");if(!first)s<<',';first=false;
            s<<"{\"index\":"<<a<<",\"description\":"<<quote(utf8(desc.Description))<<",\"luid_high\":"<<desc.AdapterLuid.HighPart<<",\"luid_low\":"<<desc.AdapterLuid.LowPart<<",\"software\":"<<((desc.Flags&DXGI_ADAPTER_FLAG_SOFTWARE)?"true":"false")<<",\"outputs\":[";
            bool first_output=true;for(UINT o=0;o<64;++o){ComPtr<IDXGIOutput> output;hr=adapter->EnumOutputs(o,&output);if(hr==DXGI_ERROR_NOT_FOUND)break;check(hr,"diagnostic EnumOutputs");DXGI_OUTPUT_DESC d{};check(output->GetDesc(&d),"diagnostic output desc");if(!first_output)s<<',';first_output=false;
                s<<"{\"index\":"<<o<<",\"name\":"<<quote(utf8(d.DeviceName))<<",\"attached\":"<<(d.AttachedToDesktop?"true":"false")<<",\"rotation\":"<<d.Rotation<<",\"rect\":["<<d.DesktopCoordinates.left<<','<<d.DesktopCoordinates.top<<','<<d.DesktopCoordinates.right<<','<<d.DesktopCoordinates.bottom<<']';
                ComPtr<IDXGIOutput6> output6;DXGI_OUTPUT_DESC1 color{};
                if(SUCCEEDED(output.As(&output6))&&SUCCEEDED(output6->GetDesc1(&color)))s<<",\"color_space\":"<<color.ColorSpace<<",\"hdr_active\":"<<(color.ColorSpace==DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020?"true":"false")<<",\"bits_per_color\":"<<color.BitsPerColor<<",\"min_luminance\":"<<color.MinLuminance<<",\"max_luminance\":"<<color.MaxLuminance<<",\"max_full_frame_luminance\":"<<color.MaxFullFrameLuminance;
                gpu device;hr=guarded([&]{device.create(adapter.Get());});s<<",\"device\":"<<result(hr);
                if(SUCCEEDED(hr)){ComPtr<IDXGIDevice> dxgi;check(device.device.As(&dxgi),"diagnostic device adapter");ComPtr<IDXGIAdapter> actual;check(dxgi->GetAdapter(&actual),"diagnostic GetAdapter");DXGI_ADAPTER_DESC actual_desc{};check(actual->GetDesc(&actual_desc),"diagnostic actual desc");bool matches=actual_desc.AdapterLuid.HighPart==desc.AdapterLuid.HighPart&&actual_desc.AdapterLuid.LowPart==desc.AdapterLuid.LowPart;
                    s<<",\"device_output_adapter_match\":"<<(matches?"true":"false");ComPtr<IDXGIOutput1> output1;hr=output.As(&output1);ComPtr<IDXGIOutputDuplication> duplication;
                    if(SUCCEEDED(hr)&&d.AttachedToDesktop)hr=output1->DuplicateOutput(device.device.Get(),&duplication);else if(SUCCEEDED(hr))hr=DXGI_ERROR_NOT_FOUND;s<<",\"duplicate_output\":"<<result(hr);}
                s<<'}';}
            s<<"]}";}
        s<<"],\"guidance\":\"For physical-desktop acceptance run this command and the dxgi host from the authorized Windows Home local console; never switch sessions or weaken policy to force access.\"}";
        report=s.str();*json=report.c_str();
    });
}
