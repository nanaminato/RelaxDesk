#include <windows.h>
#include <cstdio>
#include <vector>
#include <cstring>
#include <stdexcept>
#include "api.h"
int present_contracts();
int present_diagnose();

static void require(HRESULT hr,const char* step){if(FAILED(hr)){printf("FAIL %s HRESULT=0x%08lx operation=%s\n",step,hr,rd_last_error());throw std::runtime_error(step);}}
static int render_test(){
    HWND parent=nullptr;void* child=nullptr;void* encoder=nullptr;void* decoder=nullptr;void* renderer=nullptr;void* frame=nullptr;
    try{
        parent=CreateWindowExW(0,L"STATIC",L"RelaxDesk GPU render verification",WS_OVERLAPPEDWINDOW,100,100,660,410,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
        if(!parent)throw std::runtime_error("test top window");ShowWindow(parent,SW_SHOWNOACTIVATE);
        require(rd_window_create(parent,&child),"create child");MoveWindow(static_cast<HWND>(child),0,0,640,360,TRUE);
        require(rd_encoder_create(nullptr,640,360,30,4000000,1,&encoder),"hardware encoder");require(rd_decoder_create(640,360,1,&decoder),"hardware decoder");
        std::vector<uint8_t> pixels(640*360*4);for(size_t i=0;i<pixels.size();i+=4){pixels[i]=50;pixels[i+1]=80;pixels[i+2]=160;pixels[i+3]=255;}
        std::vector<uint8_t> metadata(68);int fields[14]={1,0,0,4,10,10,1,2,1,1,4,0,0,1};memcpy(metadata.data(),fields,sizeof(fields));metadata[64]=250;metadata[65]=10;metadata[66]=20;metadata[67]=255;
        rd_render_timing stats{};uint64_t readback=0;int successful=0;
        for(int i=0;i<70;++i){MSG message;while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}
            if(i==20)MoveWindow(static_cast<HWND>(child),0,0,600,340,TRUE);
            if(i==30)ShowWindow(parent,SW_MINIMIZE);if(i==35)ShowWindow(parent,SW_SHOWNOACTIVATE);
            if(i<5){
                bool mono=i==2;metadata.assign(mono?66:68,0);int pointer_fields[14]={1,0,0,mono?2:4,10,10,1,mono?1:(i==1||i==3?4:2),1,mono?2:1,mono?1:4,0,0,1};memcpy(metadata.data(),pointer_fields,sizeof(pointer_fields));
                if(mono){metadata[64]=128;metadata[65]=128;}
                else if(i==1){metadata[64]=metadata[65]=metadata[66]=metadata[67]=255;}
                else if(i==3){metadata[64]=10;metadata[65]=20;metadata[66]=30;metadata[67]=0;}
                else if(i==4){metadata[64]=200;metadata[65]=0;metadata[66]=100;metadata[67]=128;}
                else {metadata[64]=250;metadata[65]=10;metadata[66]=20;metadata[67]=255;}
            }
            if(i==5){int32_t extreme[2]={INT32_MAX,INT32_MIN};memcpy(metadata.data()+16,extreme,sizeof(extreme));}
            if(i==6){int32_t restored[2]={10,10};memcpy(metadata.data()+16,restored,sizeof(restored));}
            const uint8_t* data=nullptr;uint32_t bytes=0;int key=0;int64_t time=0;
            HRESULT hr=rd_encoder_bgra(encoder,pixels.data(),static_cast<uint32_t>(pixels.size()),i*333333LL,0,&data,&bytes,&key,&time);require(hr,"encode");if(hr==S_FALSE)continue;
            hr=rd_decoder_gpu_frame(decoder,data,bytes,time,&frame);require(hr,"decode surface");if(hr==S_FALSE)continue;
            rd_decode_timing timing{};require(rd_decoder_timing(decoder,&timing),"decode timing");readback+=timing.readback_bytes;
            if(!renderer)require(rd_renderer_create(frame,child,&renderer),"create renderer");
            hr=rd_renderer_draw(renderer,frame,metadata.data(),static_cast<uint32_t>(metadata.size()));require(hr,"draw");
            if(hr==S_OK){if(i>0&&i<5){uint32_t probe=0;require(rd_renderer_probe_pixel(renderer,10,10,&probe),"GPU pointer operation pixel");int expected_b=i==1||i==2?205:i==3?10:125;int expected_g=i==1||i==2?175:i==3?20:40;int expected_r=i==1||i==2?95:i==3?30:130;
                if(abs(static_cast<int>(probe&255)-expected_b)>15||abs(static_cast<int>((probe>>8)&255)-expected_g)>15||abs(static_cast<int>((probe>>16)&255)-expected_r)>15)throw std::runtime_error("GPU monochrome/masked/alpha pointer mismatch");}
                if(i==0){uint32_t color=0;require(rd_renderer_probe_pixel(renderer,10,10,&color),"GPU cursor pixel");if((color&0xffffff)!=0x140afa)throw std::runtime_error("GPU cursor color mismatch");uint32_t background=0;require(rd_renderer_probe_pixel(renderer,100,100,&background),"GPU video pixel");int r=(background>>16)&255,g=(background>>8)&255,b=background&255;if(abs(r-160)>15||abs(g-80)>15||abs(b-50)>15)throw std::runtime_error("GPU video color mismatch");}
                hr=rd_renderer_present(renderer,&stats);require(hr,"present");if(hr==S_OK)++successful;}
            rd_gpu_frame_destroy(frame);frame=nullptr;Sleep(10);
        }
        if(successful<30||stats.resizes<2||!stats.skipped||readback||stats.full_frame_readback_bytes)throw std::runtime_error("render/pause/resize/readback assertions");
        printf("PASS actual GPU SwapChain presents=%d skipped=%llu resizes=%llu full_frame_readback_bytes=%llu cursor_upload_bytes=%llu (explicit test-only 1-pixel probes)\n",successful,stats.skipped,stats.resizes,readback,stats.cursor_upload_bytes);
        rd_renderer_destroy(renderer);renderer=nullptr;rd_decoder_destroy(decoder);decoder=nullptr;rd_encoder_destroy(encoder);encoder=nullptr;require(rd_window_destroy(child),"destroy child");child=nullptr;DestroyWindow(parent);parent=nullptr;
        rd_live_resources live{};rd_get_live_resources(&live);if(live.captures||live.encoders||live.decoders||live.frames||live.renderers||live.windows||live.callbacks)throw std::runtime_error("native resource leak");printf("PASS native contexts/frame leases/render HWND/async callbacks released: all live counters zero\n");return 0;
    }catch(const std::exception& error){printf("FAIL render test: %s\n",error.what());if(frame)rd_gpu_frame_destroy(frame);if(renderer)rd_renderer_destroy(renderer);if(decoder)rd_decoder_destroy(decoder);if(encoder)rd_encoder_destroy(encoder);if(child)rd_window_destroy(child);if(parent)DestroyWindow(parent);return 1;}
}

static bool handle(const char* name,HGDIOBJ value,DWORD expected){DWORD error=GetLastError();DWORD type=GetObjectType(value);printf("%s handle=%p type=%lu expected=%lu last_error=%lu\n",name,value,type,expected,error);return value&&type==expected;}
static int gdi_test(){
    DWORD session=0;ProcessIdToSessionId(GetCurrentProcessId(),&session);
    WCHAR name[256]{};DWORD needed=0;GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()),UOI_NAME,name,sizeof(name),&needed);
    printf("session=%lu active_console=%lu thread=%lu desktop=%ls remote_session=%d\n",session,WTSGetActiveConsoleSessionId(),GetCurrentThreadId(),name,GetSystemMetrics(SM_REMOTESESSION));
    SetProcessDPIAware();int width=GetSystemMetrics(SM_CXSCREEN),height=GetSystemMetrics(SM_CYSCREEN);
    HDC screen=GetDC(nullptr);if(!handle("GetDC",screen,OBJ_DC))return 1;
    HDC memory=CreateCompatibleDC(screen);if(!handle("CreateCompatibleDC",memory,OBJ_MEMDC)){ReleaseDC(nullptr,screen);return 1;}
    HBITMAP bitmap=CreateCompatibleBitmap(screen,width,height);bool ok=handle("CreateCompatibleBitmap",bitmap,OBJ_BITMAP);
    HGDIOBJ previous=ok?SelectObject(memory,bitmap):nullptr;ok=ok&&previous&&previous!=HGDI_ERROR;
    printf("SelectObject previous=%p current=%p bitmap=%p\n",previous,GetCurrentObject(memory,OBJ_BITMAP),bitmap);
    if(ok){SetLastError(0);BOOL copied=BitBlt(memory,0,0,width,height,screen,0,0,SRCCOPY|CAPTUREBLT);DWORD error=GetLastError();printf("BitBlt CAPTUREBLT result=%d error=%lu\n",copied,error);
        if(!copied){SetLastError(0);copied=BitBlt(memory,0,0,width,height,screen,0,0,SRCCOPY);error=GetLastError();printf("BitBlt SRCCOPY result=%d error=%lu\n",copied,error);}ok=copied!=FALSE;
        HGDIOBJ removed=SelectObject(memory,previous);printf("Deselect removed=%p expected=%p\n",removed,bitmap);
        if(ok){BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=width;info.bmiHeader.biHeight=-height;info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;
            std::vector<unsigned char> pixels(static_cast<size_t>(width)*height*4);SetLastError(0);int lines=GetDIBits(screen,bitmap,0,height,pixels.data(),&info,DIB_RGB_COLORS);error=GetLastError();printf("GetDIBits lines=%d expected=%d error=%lu BITMAPINFOHEADER=%zu BITMAPINFO=%zu\n",lines,height,error,sizeof(BITMAPINFOHEADER),sizeof(BITMAPINFO));ok=lines==height;}
    }
    if(previous&&previous!=HGDI_ERROR)SelectObject(memory,previous);if(bitmap)DeleteObject(bitmap);DeleteDC(memory);ReleaseDC(nullptr,screen);return ok?0:1;
}
int main(int argc,char** argv){
    if(argc==2&&!strcmp(argv[1],"--capture-contracts")){
        const char* report=nullptr;HRESULT hr=rd_capture_self_test(&report);if(FAILED(hr)){printf("FAIL capture contracts HRESULT=0x%08lx operation=%s\n",hr,rd_last_error());return 1;}printf("%s\n",report);
        rd_live_resources live{};rd_get_live_resources(&live);if(live.captures||live.encoders||live.decoders||live.frames||live.renderers||live.windows||live.callbacks){printf("FAIL capture test native resources not released\n");return 1;}printf("PASS capture test resources released\n");return 0;
    }
    if(argc==2&&!strcmp(argv[1],"--present-contracts"))return present_contracts();
    if(argc==2&&!strcmp(argv[1],"--present-diagnose"))return present_diagnose();
    if(argc==2&&!strcmp(argv[1],"--render-test"))return render_test();
    if(argc==2&&!strcmp(argv[1],"--diagnose")){const char* json=nullptr;HRESULT hr=rd_capture_diagnostics(&json);if(FAILED(hr)){printf("diagnostics HRESULT=0x%08lx operation=%s\n",hr,rd_last_error());return 1;}printf("%s\n",json);return 0;}
    if(argc==2&&!strcmp(argv[1],"--gdi"))return gdi_test();
    void* capture=nullptr;HRESULT hr=rd_capture_create(&capture);printf("DXGI create HRESULT=0x%08lx operation=%s\n",hr,rd_last_error());if(FAILED(hr))return 2;
    rd_frame frame{};for(int i=0;i<20;++i){hr=rd_capture_next(capture,100,&frame);if(hr!=S_FALSE)break;}
    printf("DXGI acquire HRESULT=0x%08lx %ux%u dirty=%u move=%u pointer_bytes=%u operation=%s\n",hr,frame.width,frame.height,frame.dirty_count,frame.move_count,frame.pointer_bytes,rd_last_error());rd_capture_destroy(capture);return hr==S_OK?0:3;
}
