#include <windows.h>
#include <cstdio>
#include <vector>
#include <cstring>
#include "api.h"

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
    if(argc==2&&!strcmp(argv[1],"--gdi"))return gdi_test();
    void* capture=nullptr;HRESULT hr=rd_capture_create(&capture);printf("DXGI create HRESULT=0x%08lx operation=%s\n",hr,rd_last_error());if(FAILED(hr))return 2;
    rd_frame frame{};for(int i=0;i<20;++i){hr=rd_capture_next(capture,100,&frame);if(hr!=S_FALSE)break;}
    printf("DXGI acquire HRESULT=0x%08lx %ux%u dirty=%u move=%u pointer_bytes=%u operation=%s\n",hr,frame.width,frame.height,frame.dirty_count,frame.move_count,frame.pointer_bytes,rd_last_error());rd_capture_destroy(capture);return hr==S_OK?0:3;
}
