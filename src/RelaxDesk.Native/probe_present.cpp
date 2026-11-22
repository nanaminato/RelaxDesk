#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <cstdio>
#include <stdexcept>
#include "api.h"
#include "present_state.h"
using Microsoft::WRL::ComPtr;
namespace {
void check_hr(HRESULT hr){if(FAILED(hr)){printf("failure_hr=0x%08lx\n",hr);throw std::runtime_error("D3D diagnostic operation");}}
void pump(){MSG m;while(PeekMessageW(&m,nullptr,0,0,PM_REMOVE)){TranslateMessage(&m);DispatchMessageW(&m);}}
int comparison_result(unsigned top,unsigned child,unsigned top_stages,unsigned child_stages){
    if(!top&&!child)return 2;
    return top_stages==5&&child_stages==5?0:1;
}
ComPtr<IDXGISwapChain1> make_chain(IDXGIFactory2* factory,ID3D11Device* device,HWND window){
    DXGI_SWAP_CHAIN_DESC1 d{};d.Width=640;d.Height=360;d.Format=DXGI_FORMAT_B8G8R8A8_UNORM;d.SampleDesc.Count=1;d.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;d.BufferCount=2;d.SwapEffect=DXGI_SWAP_EFFECT_FLIP_DISCARD;
    ComPtr<IDXGISwapChain1> swap;check_hr(factory->CreateSwapChainForHwnd(device,window,&d,nullptr,nullptr,&swap));check_hr(factory->MakeWindowAssociation(window,DXGI_MWA_NO_ALT_ENTER));return swap;
}
}
int present_contracts(){
    present_state state;
    auto expect=[](bool value){if(!value)throw std::runtime_error("present state contract");};
    try{
        expect(!state.idle);state.presented(DXGI_ERROR_WAS_STILL_DRAWING,0);expect(!state.idle);
        state.presented(DXGI_STATUS_OCCLUDED,10);expect(state.idle);expect(!state.test_due(109));expect(state.test_due(110));
        expect(!state.tested(DXGI_STATUS_OCCLUDED,110));expect(!state.test_due(209));expect(state.test_due(210));
        expect(!state.tested(S_FALSE,210));expect(state.idle);expect(state.tested(S_OK,310));expect(!state.idle);
        expect(comparison_result(0,0,0,0)==2);expect(comparison_result(8,0,1,0)==1);
        expect(comparison_result(40,8,5,1)==1);expect(comparison_result(40,40,5,5)==0);
        printf("PASS 16 present-state/comparison contracts (no GPU/environment requirement)\n");return 0;
    }catch(const std::exception& e){printf("FAIL %s\n",e.what());return 1;}
}
// Independent of WPF, decoder, video processor and production renderer state.
// One bounded comparison, never a retry-until-success loop.
int present_diagnose(){
    HWND root=nullptr;void* child=nullptr;
    try{
        const char* environment=nullptr;check_hr(rd_capture_diagnostics(&environment));printf("environment=%s\n",environment);
        ComPtr<IDXGIFactory2> factory;check_hr(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
        ComPtr<IDXGIAdapter1> adapter;
        for(UINT i=0;;++i){ComPtr<IDXGIAdapter1> candidate;if(factory->EnumAdapters1(i,&candidate)==DXGI_ERROR_NOT_FOUND)break;
            bool attached=false;for(UINT j=0;;++j){ComPtr<IDXGIOutput> output;if(candidate->EnumOutputs(j,&output)==DXGI_ERROR_NOT_FOUND)break;DXGI_OUTPUT_DESC d{};check_hr(output->GetDesc(&d));attached|=d.AttachedToDesktop!=FALSE;}
            if(attached){adapter=candidate;break;}}
        if(!adapter)throw std::runtime_error("no attached DXGI adapter");
        ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> commands;D3D_FEATURE_LEVEL level{};
        check_hr(D3D11CreateDevice(adapter.Get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,&level,&commands));
        DXGI_ADAPTER_DESC1 desc{};check_hr(adapter->GetDesc1(&desc));printf("adapter=%ls luid=%ld:%lu feature_level=0x%x\n",desc.Description,desc.AdapterLuid.HighPart,desc.AdapterLuid.LowPart,level);
        unsigned shown[2]{},blocked[2]{},passed_stages[2]{};
        for(int kind=0;kind<2;++kind){
            root=CreateWindowExW(0,L"STATIC",L"RelaxDesk bounded presentation diagnostic",WS_OVERLAPPEDWINDOW|WS_CLIPCHILDREN,120,120,680,440,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
            if(!root)throw std::runtime_error("create diagnostic root");ShowWindow(root,SW_SHOWNOACTIVATE);
            HWND target=root;if(kind){check_hr(rd_window_create(root,&child));target=static_cast<HWND>(child);MoveWindow(target,0,0,640,360,TRUE);}
            auto swap=make_chain(factory.Get(),device.Get(),target);
            for(int stage=0;stage<6;++stage){
                if(stage==1)ShowWindow(root,SW_HIDE);
                if(stage==2)ShowWindow(root,SW_SHOWNOACTIVATE);
                if(stage==3){check_hr(swap->ResizeBuffers(2,600,340,DXGI_FORMAT_UNKNOWN,0));MoveWindow(target,kind?0:120,kind?0:120,600,340,TRUE);}
                if(stage==4){commands->ClearState();swap.Reset();commands->Flush();swap=make_chain(factory.Get(),device.Get(),target);}
                if(stage==5){swap.Reset();commands->Flush();if(child){check_hr(rd_window_destroy(child));child=nullptr;}DestroyWindow(root);root=CreateWindowExW(0,L"STATIC",L"RelaxDesk recreated HWND",WS_OVERLAPPEDWINDOW|WS_CLIPCHILDREN,120,120,680,440,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);if(!root)throw std::runtime_error("recreate root");ShowWindow(root,SW_SHOWNOACTIVATE);target=root;if(kind){check_hr(rd_window_create(root,&child));target=static_cast<HWND>(child);MoveWindow(target,0,0,640,360,TRUE);}swap=make_chain(factory.Get(),device.Get(),target);}
                unsigned ok=0,occluded=0,busy=0;HRESULT last=S_OK,test=S_OK;
                for(int n=0;n<8;++n){pump();
                    ComPtr<ID3D11Texture2D> buffer;ComPtr<ID3D11RenderTargetView> view;check_hr(swap->GetBuffer(0,IID_PPV_ARGS(&buffer)));check_hr(device->CreateRenderTargetView(buffer.Get(),nullptr,&view));float color[4]={.15f,.35f,.65f,1};commands->ClearRenderTargetView(view.Get(),color);view.Reset();buffer.Reset();
                    last=swap->Present(0,DXGI_PRESENT_DO_NOT_WAIT);if(last==S_OK)++ok;else if(last==DXGI_STATUS_OCCLUDED){++occluded;test=swap->Present(0,DXGI_PRESENT_TEST);}else if(last==DXGI_ERROR_WAS_STILL_DRAWING)++busy;else check_hr(last);Sleep(25);
                }
                printf("target=%s stage=%d visible=%d presents=%u occluded=%u busy=%u last_hr=0x%08lx test_hr=0x%08lx hwnd=%p parent=%p\n",kind?"native-child":"top-level",stage,IsWindowVisible(target),ok,occluded,busy,last,test,target,GetParent(target));
                if(stage!=1){shown[kind]+=ok;blocked[kind]+=occluded;if(ok)++passed_stages[kind];}
            }
            commands->ClearState();swap.Reset();commands->Flush();if(child){check_hr(rd_window_destroy(child));child=nullptr;}DestroyWindow(root);root=nullptr;
        }
        printf("comparison top_present=%u child_present=%u top_occluded=%u child_occluded=%u\n",shown[0],shown[1],blocked[0],blocked[1]);
        rd_live_resources live{};rd_get_live_resources(&live);if(live.windows)throw std::runtime_error("diagnostic child HWND leak");
        int result=comparison_result(shown[0],shown[1],passed_stages[0],passed_stages[1]);
        if(result==2){printf("BLOCKED independent top-level and child presentation; not specific to WPF/HwndHost or codec. Environment/driver cause requires local-console comparison.\n");return 2;}
        if(result==1){printf("FAIL partial lifecycle comparison: top_stages=%u/5 child_stages=%u/5; inspect per-stage integration before blaming RDP\n",passed_stages[0],passed_stages[1]);return 1;}
        printf("PASS independent presentation comparison; API success is not physical scanout proof\n");return 0;
    }catch(const std::exception& e){printf("FAIL presentation diagnostic: %s\n",e.what());if(child)rd_window_destroy(child);if(root)DestroyWindow(root);return 1;}
}
