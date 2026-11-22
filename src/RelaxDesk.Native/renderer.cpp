#include "internal.h"
#include <dxgi1_3.h>
#include <dxgi1_4.h>
#include <d3dcompiler.h>
#include <algorithm>
#include <cmath>
#include <mutex>
#include <sstream>
#include <dwmapi.h>
#include "present_state.h"

namespace {
struct window_dpi_scope {
    DPI_AWARENESS_CONTEXT previous;
    explicit window_dpi_scope(HWND hwnd):previous(SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(hwnd))){}
    ~window_dpi_scope(){if(previous)SetThreadDpiAwarenessContext(previous);}
};
const char* cursor_shader=R"(
cbuffer Pointer : register(b0) { float4 pointerRect; int4 clipRect; uint4 details; };
Texture2D<float4> shape : register(t0);
Texture2D<float4> background : register(t1);
float4 vs(uint id : SV_VertexID) : SV_Position {
    float2 p = id==0 ? float2(-1,-1) : id==1 ? float2(-1,3) : float2(3,-1);
    return float4(p,0,1);
}
float4 ps(float4 position : SV_Position) : SV_Target {
    int2 local = int2(position.xy)-clipRect.xy;
    int2 source = clamp(int2((position.xy-pointerRect.xy)*float2(details.yz)/pointerRect.zw),int2(0,0),int2(details.yz)-1);
    uint3 b = (uint3)round(saturate(background.Load(int3(local,0)).rgb)*255);
    uint4 s = (uint4)round(saturate(shape.Load(int3(source,0)))*255);
    uint3 rgb;
    if(details.x==1) rgb=(b & s.r)^s.g;
    else if(details.x==4) rgb=s.a==0?s.rgb:(b^s.rgb);
    else rgb=(s.rgb*s.a+b*(255-s.a)+127)/255;
    return float4(float3(rgb)/255,1);
})";

struct renderer_context {
    resource_counter tracked{4};
    runtime lifetime;
    HWND hwnd=nullptr;
    gpu graphics;
    ComPtr<IDXGISwapChain1> swap;
    ComPtr<ID3D11VideoDevice> video;
    ComPtr<ID3D11VideoContext> video_commands;
    ComPtr<ID3D11VideoProcessorEnumerator> enumerator;
    ComPtr<ID3D11VideoProcessor> processor;
    ComPtr<ID3D11Texture2D> backbuffer,shape_texture,cursor_background;
    ComPtr<ID3D11VideoProcessorOutputView> output_view;
    ComPtr<ID3D11RenderTargetView> render_target;
    ComPtr<ID3D11ShaderResourceView> shape_view,background_view;
    ComPtr<ID3D11VertexShader> vertex;
    ComPtr<ID3D11PixelShader> pixel;
    ComPtr<ID3D11Buffer> constants;
    ComPtr<ID3D11RasterizerState> rasterizer;
    UINT width=0,height=0,source_width=0,source_height=0;
    UINT shape_width=0,shape_height=0,shape_type=0,shape_pitch=0,background_width=0,background_height=0;
    std::vector<uint8_t> shape_bytes;
    rd_render_timing timing{};
    bool drawn=false;
    HRESULT last_present=S_OK;
    present_state visibility;
    HRESULT last_test=S_OK;
    uint64_t test_calls=0;
    DWORD window_thread=0,window_process=0;

    renderer_context(gpu_frame& frame,HWND window):hwnd(window) {
        if(!IsWindow(hwnd))check(E_HANDLE,"renderer requires a live child HWND");
        window_thread=GetWindowThreadProcessId(hwnd,&window_process);
        if(window_process!=GetCurrentProcessId())check(E_INVALIDARG,"renderer requires an in-process HWND");
        frame.texture->GetDevice(&graphics.device);graphics.device->GetImmediateContext(&graphics.commands);
        check(graphics.device.As(&video),"renderer video device");check(graphics.commands.As(&video_commands),"renderer video context");
        ComPtr<IDXGIDevice> device;check(graphics.device.As(&device),"renderer DXGI device");ComPtr<IDXGIAdapter> adapter;check(device->GetAdapter(&adapter),"renderer adapter");
        ComPtr<IDXGIFactory2> factory;check(adapter->GetParent(IID_PPV_ARGS(&factory)),"renderer factory");
        DXGI_SWAP_CHAIN_DESC1 desc{};desc.Width=desc.Height=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;desc.BufferCount=2;desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_DISCARD;desc.Scaling=DXGI_SCALING_STRETCH;desc.AlphaMode=DXGI_ALPHA_MODE_IGNORE;
        check(factory->CreateSwapChainForHwnd(graphics.device.Get(),hwnd,&desc,nullptr,nullptr,&swap),"CreateSwapChainForHwnd");
        // This media protocol carries SDR BT.709, never PQ/HDR. Let Windows map
        // SDR to an HDR display; do not label 8-bit SDR values as HDR luminance.
        ComPtr<IDXGISwapChain3> color_swap;
        if(SUCCEEDED(swap.As(&color_swap)))check(color_swap->SetColorSpace1(DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709),"SDR swapchain color space");
        check(factory->MakeWindowAssociation(hwnd,DXGI_MWA_NO_ALT_ENTER),"swapchain window association");
        ComPtr<IDXGIDevice1> latency;if(SUCCEEDED(device.As(&latency)))latency->SetMaximumFrameLatency(1);
    }
    ~renderer_context(){
        graphics.commands->ClearState();output_view.Reset();render_target.Reset();backbuffer.Reset();swap.Reset();
        // Flush after releasing swapchain references so deferred destruction cannot
        // keep an old flip-model chain attached when this HWND is reused.
        graphics.commands->Flush();
    }

    bool resize(){
        lifetime.verify();DWORD process=0;
        if(!IsWindow(hwnd)||GetWindowThreadProcessId(hwnd,&process)!=window_thread||process!=window_process)check(E_HANDLE,"render window destroyed or replaced");
        window_dpi_scope dpi(hwnd);
        RECT client{};check(GetClientRect(hwnd,&client)?S_OK:HRESULT_FROM_WIN32(GetLastError()),"render client rectangle");
        if(!IsWindowVisible(hwnd)||IsIconic(GetAncestor(hwnd,GA_ROOT))||client.right<=0||client.bottom<=0){++timing.skipped;++timing.hidden;return false;}
        if(visibility.idle){
            if(!visibility.test_due(GetTickCount64())){++timing.skipped;++timing.occluded;return false;}
            HRESULT status=swap->Present(0,DXGI_PRESENT_TEST);last_test=status;++test_calls;
            check(status,"swapchain occlusion test");
            if(status==DXGI_STATUS_MODE_CHANGED){width=height=0;visibility.idle=false;}
            else if(!visibility.tested(status,GetTickCount64())){++timing.skipped;++timing.occluded;return false;}
        }
        UINT w=static_cast<UINT>(client.right),h=static_cast<UINT>(client.bottom);
        if(w>8192||h>8192)check(E_INVALIDARG,"render dimensions exceed cap");
        if(w!=width||h!=height){
            graphics.commands->ClearState();output_view.Reset();render_target.Reset();backbuffer.Reset();
            check(swap->ResizeBuffers(2,w,h,DXGI_FORMAT_B8G8R8A8_UNORM,0),"swapchain ResizeBuffers");width=w;height=h;++timing.resizes;
        }
        if(!backbuffer){check(swap->GetBuffer(0,IID_PPV_ARGS(&backbuffer)),"swapchain backbuffer");check(graphics.device->CreateRenderTargetView(backbuffer.Get(),nullptr,&render_target),"swapchain RTV");}
        return true;
    }
    void setup_processor(gpu_frame& frame){
        D3D11_TEXTURE2D_DESC texture{};frame.texture->GetDesc(&texture);
        if(!processor||source_width!=frame.width||source_height!=frame.height){
            output_view.Reset();processor.Reset();enumerator.Reset();source_width=frame.width;source_height=frame.height;
            D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc{};desc.InputFrameFormat=D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;desc.InputWidth=texture.Width;desc.InputHeight=texture.Height;desc.OutputWidth=source_width;desc.OutputHeight=source_height;desc.InputFrameRate=desc.OutputFrameRate={30,1};desc.Usage=D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
            check(video->CreateVideoProcessorEnumerator(&desc,&enumerator),"renderer video processor enumerator");check(video->CreateVideoProcessor(enumerator.Get(),0,&processor),"renderer video processor");
        }
        if(!output_view){D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC desc{};desc.ViewDimension=D3D11_VPOV_DIMENSION_TEXTURE2D;check(video->CreateVideoProcessorOutputView(backbuffer.Get(),enumerator.Get(),&desc,&output_view),"swapchain video output view");}
    }
    void shaders(){
        if(vertex)return;
        ComPtr<ID3DBlob> vs,ps,errors;
        check(D3DCompile(cursor_shader,strlen(cursor_shader),nullptr,nullptr,nullptr,"vs","vs_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&vs,&errors),"cursor vertex compile");
        check(D3DCompile(cursor_shader,strlen(cursor_shader),nullptr,nullptr,nullptr,"ps","ps_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&ps,&errors),"cursor pixel compile");
        check(graphics.device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex),"cursor VS");check(graphics.device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel),"cursor PS");
        D3D11_BUFFER_DESC buffer{};buffer.ByteWidth=48;buffer.Usage=D3D11_USAGE_DEFAULT;buffer.BindFlags=D3D11_BIND_CONSTANT_BUFFER;check(graphics.device->CreateBuffer(&buffer,nullptr,&constants),"cursor constants");
        D3D11_RASTERIZER_DESC state{};state.FillMode=D3D11_FILL_SOLID;state.CullMode=D3D11_CULL_NONE;state.DepthClipEnable=TRUE;check(graphics.device->CreateRasterizerState(&state,&rasterizer),"cursor rasterizer");
    }
    void pointer(const uint8_t* metadata,UINT bytes,float scale,float origin_x,float origin_y){
        if(bytes<64||bytes>1024*1024)check(E_INVALIDARG,"renderer metadata size");
        auto field=[&](UINT i){int32_t value;memcpy(&value,metadata+i*4,4);return value;};
        int dirty=field(1),moves=field(2),shape_size=field(3);
        if(field(0)!=1||dirty<0||moves<0||shape_size<0||dirty%16||moves%24||static_cast<uint64_t>(64)+dirty+moves+shape_size!=bytes)check(E_INVALIDARG,"renderer metadata framing");
        if(!field(6)||!shape_size)return;
        UINT type=static_cast<UINT>(field(7)),w=static_cast<UINT>(field(8)),full_height=static_cast<UINT>(field(9)),pitch=static_cast<UINT>(field(10));
        bool mono=type==1;UINT h=mono?full_height/2:full_height;
        if((type!=1&&type!=2&&type!=4)||!w||w>4096||!h||full_height>8192||!pitch||static_cast<uint64_t>(pitch)*full_height>shape_size||(mono?(full_height%2||pitch<(w+7)/8):pitch<w*4))check(E_INVALIDARG,"renderer pointer bounds");
        const uint8_t* source=metadata+64+dirty+moves;
        if(type==4)for(UINT y=0;y<h;++y)for(UINT x=0;x<w;++x)if(source[y*pitch+x*4+3]!=0&&source[y*pitch+x*4+3]!=255)check(E_INVALIDARG,"masked pointer alpha");
        float x=origin_x+field(4)*scale,y=origin_y+field(5)*scale,sw=w*scale,sh=h*scale;
        // Clamp in floating point before conversion: a hostile peer can send INT_MIN/MAX pointer coordinates.
        int left=static_cast<int>(std::clamp(floor(static_cast<double>(x)),0.0,static_cast<double>(width))),top=static_cast<int>(std::clamp(floor(static_cast<double>(y)),0.0,static_cast<double>(height)));
        int right=static_cast<int>(std::clamp(ceil(static_cast<double>(x+sw)),0.0,static_cast<double>(width))),bottom=static_cast<int>(std::clamp(ceil(static_cast<double>(y+sh)),0.0,static_cast<double>(height)));
        if(right<=left||bottom<=top)return;
        shaders();
        if(shape_width!=w||shape_height!=h||shape_type!=type||shape_pitch!=pitch||shape_bytes.size()!=static_cast<size_t>(shape_size)||!std::equal(shape_bytes.begin(),shape_bytes.end(),source)){
            shape_view.Reset();shape_texture.Reset();shape_bytes.assign(source,source+shape_size);shape_width=w;shape_height=h;shape_type=type;shape_pitch=pitch;
            std::vector<uint8_t> expanded;
            if(mono){if(static_cast<uint64_t>(w)*h*4>32*1024*1024)check(E_INVALIDARG,"expanded pointer cap");expanded.resize(static_cast<size_t>(w)*h*4);
                for(UINT row=0;row<h;++row)for(UINT col=0;col<w;++col){auto target=&expanded[(static_cast<size_t>(row)*w+col)*4];UINT bit=0x80>>(col%8);target[2]=(source[row*pitch+col/8]&bit)?255:0;target[1]=(source[(row+h)*pitch+col/8]&bit)?255:0;target[0]=0;target[3]=255;}source=expanded.data();}
            D3D11_TEXTURE2D_DESC desc{};desc.Width=w;desc.Height=h;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_IMMUTABLE;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
            D3D11_SUBRESOURCE_DATA initial{source,mono?w*4:pitch,0};check(graphics.device->CreateTexture2D(&desc,&initial,&shape_texture),"pointer texture");check(graphics.device->CreateShaderResourceView(shape_texture.Get(),nullptr,&shape_view),"pointer SRV");timing.cursor_upload_bytes+=static_cast<uint64_t>(w)*h*4;
        }
        UINT bw=static_cast<UINT>(right-left),bh=static_cast<UINT>(bottom-top);
        if(!cursor_background||bw!=background_width||bh!=background_height){background_view.Reset();cursor_background.Reset();background_width=bw;background_height=bh;
            D3D11_TEXTURE2D_DESC desc{};desc.Width=bw;desc.Height=bh;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;check(graphics.device->CreateTexture2D(&desc,nullptr,&cursor_background),"pointer background texture");check(graphics.device->CreateShaderResourceView(cursor_background.Get(),nullptr,&background_view),"pointer background SRV");}
        graphics.commands->OMSetRenderTargets(0,nullptr,nullptr);D3D11_BOX box{static_cast<UINT>(left),static_cast<UINT>(top),0,static_cast<UINT>(right),static_cast<UINT>(bottom),1};graphics.commands->CopySubresourceRegion(cursor_background.Get(),0,0,0,0,backbuffer.Get(),0,&box);
        struct parameters{float rect[4];int clip[4];UINT details[4];} values{{x,y,sw,sh},{left,top,right,bottom},{type,w,h,0}};
        graphics.commands->UpdateSubresource(constants.Get(),0,nullptr,&values,0,0);
        ID3D11RenderTargetView* target=render_target.Get();graphics.commands->OMSetRenderTargets(1,&target,nullptr);graphics.commands->OMSetBlendState(nullptr,nullptr,0xffffffff);
        graphics.commands->IASetInputLayout(nullptr);graphics.commands->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);graphics.commands->VSSetShader(vertex.Get(),nullptr,0);graphics.commands->PSSetShader(pixel.Get(),nullptr,0);ID3D11Buffer* buffer=constants.Get();graphics.commands->PSSetConstantBuffers(0,1,&buffer);
        ID3D11ShaderResourceView* views[]={shape_view.Get(),background_view.Get()};graphics.commands->PSSetShaderResources(0,2,views);D3D11_VIEWPORT viewport{static_cast<float>(left),static_cast<float>(top),static_cast<float>(bw),static_cast<float>(bh),0,1};graphics.commands->RSSetState(rasterizer.Get());graphics.commands->RSSetViewports(1,&viewport);graphics.commands->Draw(3,0);
        ID3D11ShaderResourceView* empty[]={nullptr,nullptr};graphics.commands->PSSetShaderResources(0,2,empty);graphics.commands->OMSetRenderTargets(0,nullptr,nullptr);
    }
    bool draw(gpu_frame& frame,const uint8_t* metadata,UINT bytes){
        auto start=ticks();drawn=false;timing.draw_ms=timing.present_ms=0;if(!resize())return false;
        ComPtr<ID3D11Device> source;frame.texture->GetDevice(&source);if(source.Get()!=graphics.device.Get())check(DXGI_ERROR_INVALID_CALL,"decoded frame uses another D3D11 device; recreate renderer");
        setup_processor(frame);
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC in{};in.ViewDimension=D3D11_VPIV_DIMENSION_TEXTURE2D;in.Texture2D.ArraySlice=frame.subresource;
        ComPtr<ID3D11VideoProcessorInputView> input;check(video->CreateVideoProcessorInputView(frame.texture.Get(),enumerator.Get(),&in,&input),"renderer NV12 input view");
        float scale=std::min(static_cast<float>(width)/frame.width,static_cast<float>(height)/frame.height);
        float ox=(width-frame.width*scale)/2,oy=(height-frame.height*scale)/2;
        RECT src{0,0,static_cast<LONG>(frame.width),static_cast<LONG>(frame.height)},destination{static_cast<LONG>(ox),static_cast<LONG>(oy),static_cast<LONG>(ox+frame.width*scale),static_cast<LONG>(oy+frame.height*scale)},target{0,0,static_cast<LONG>(width),static_cast<LONG>(height)};
        video_commands->VideoProcessorSetStreamSourceRect(processor.Get(),0,TRUE,&src);video_commands->VideoProcessorSetStreamDestRect(processor.Get(),0,TRUE,&destination);video_commands->VideoProcessorSetOutputTargetRect(processor.Get(),TRUE,&target);
        D3D11_VIDEO_COLOR black{};black.RGBA.A=1;video_commands->VideoProcessorSetOutputBackgroundColor(processor.Get(),FALSE,&black);
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE yuv{};yuv.YCbCr_Matrix=1;yuv.Nominal_Range=1;D3D11_VIDEO_PROCESSOR_COLOR_SPACE rgb{};rgb.YCbCr_Matrix=1;rgb.Nominal_Range=2;video_commands->VideoProcessorSetStreamColorSpace(processor.Get(),0,&yuv);video_commands->VideoProcessorSetOutputColorSpace(processor.Get(),&rgb);
        D3D11_VIDEO_PROCESSOR_STREAM stream{};stream.Enable=TRUE;stream.pInputSurface=input.Get();check(video_commands->VideoProcessorBlt(processor.Get(),output_view.Get(),0,1,&stream),"NV12 direct to swapchain video processor");
        pointer(metadata,bytes,scale,ox,oy);timing.draw_ms=elapsed_ms(start);drawn=true;return true;
    }
    HRESULT present(){
        lifetime.verify();if(!drawn)return S_FALSE;auto start=ticks();HRESULT hr=swap->Present(0,DXGI_PRESENT_DO_NOT_WAIT);last_present=hr;timing.present_ms=elapsed_ms(start);drawn=false;
        // Flip-model Present changes the backbuffer binding; do not retain a view to the previous buffer.
        graphics.commands->OMSetRenderTargets(0,nullptr,nullptr);output_view.Reset();render_target.Reset();backbuffer.Reset();
        if(hr==DXGI_STATUS_OCCLUDED){visibility.presented(hr,GetTickCount64());++timing.skipped;++timing.occluded;return S_FALSE;}
        if(hr==DXGI_ERROR_WAS_STILL_DRAWING){++timing.skipped;++timing.busy;return S_FALSE;}
        check(hr,"swapchain Present");
        if(hr==DXGI_STATUS_MODE_CHANGED){width=height=0;++timing.skipped;return S_FALSE;}
        if(hr!=S_OK)check(E_UNEXPECTED,"unexpected successful swapchain Present status");
        ++timing.presents;return S_OK;
    }
};
LRESULT CALLBACK child_proc(HWND hwnd,UINT message,WPARAM wparam,LPARAM lparam){if(message==WM_ERASEBKGND)return 1;return DefWindowProcW(hwnd,message,wparam,lparam);}
}
RD int32_t rd_window_create(void* parent,void** child){if(!parent||!child)return E_INVALIDARG;*child=nullptr;return guarded([&]{
    static std::once_flag once;std::call_once(once,[]{WNDCLASSW type{};type.lpfnWndProc=child_proc;type.hInstance=GetModuleHandleW(nullptr);type.lpszClassName=L"RelaxDeskD3D11Surface";check(RegisterClassW(&type)?S_OK:HRESULT_FROM_WIN32(GetLastError()),"register render window");});
    HWND hwnd=CreateWindowExW(0,L"RelaxDeskD3D11Surface",L"",WS_CHILD|WS_VISIBLE|WS_CLIPSIBLINGS|WS_CLIPCHILDREN,0,0,1,1,static_cast<HWND>(parent),nullptr,GetModuleHandleW(nullptr),nullptr);check(hwnd?S_OK:HRESULT_FROM_WIN32(GetLastError()),"create render child HWND");++live_resources[5];*child=hwnd;});}
RD int32_t rd_window_destroy(void* child){if(!child)return E_INVALIDARG;return guarded([&]{check(DestroyWindow(static_cast<HWND>(child))?S_OK:HRESULT_FROM_WIN32(GetLastError()),"destroy render child HWND");--live_resources[5];});}
RD int32_t rd_renderer_create(void* frame,void* hwnd,void** renderer){if(!frame||!hwnd||!renderer)return E_INVALIDARG;*renderer=nullptr;return guarded([&]{*renderer=new renderer_context(*static_cast<gpu_frame*>(frame),static_cast<HWND>(hwnd));});}
RD int32_t rd_renderer_draw(void* renderer,void* frame,const uint8_t* metadata,uint32_t bytes){if(!renderer||!frame||!metadata)return E_INVALIDARG;bool rendered=false;HRESULT hr=guarded([&]{rendered=static_cast<renderer_context*>(renderer)->draw(*static_cast<gpu_frame*>(frame),metadata,bytes);});return FAILED(hr)?hr:rendered?S_OK:S_FALSE;}
RD int32_t rd_renderer_present(void* renderer,rd_render_timing* timing){if(!renderer||!timing)return E_INVALIDARG;HRESULT result=S_OK;HRESULT hr=guarded([&]{auto& r=*static_cast<renderer_context*>(renderer);result=r.present();*timing=r.timing;});return FAILED(hr)?hr:result;}
RD int32_t rd_renderer_diagnostics(void* renderer,const char** json){if(!renderer||!json)return E_INVALIDARG;static thread_local std::string text;return guarded([&]{
    auto& r=*static_cast<renderer_context*>(renderer);r.lifetime.verify();ComPtr<IDXGIDevice> device;check(r.graphics.device.As(&device),"render diagnostic device");ComPtr<IDXGIAdapter> adapter;check(device->GetAdapter(&adapter),"render diagnostic adapter");DXGI_ADAPTER_DESC desc{};check(adapter->GetDesc(&desc),"render diagnostic adapter desc");
    HWND root=GetAncestor(r.hwnd,GA_ROOT);RECT window{},child{},monitor{};GetWindowRect(root,&window);GetWindowRect(r.hwnd,&child);HMONITOR target=MonitorFromWindow(root,MONITOR_DEFAULTTONEAREST);MONITORINFO info{};info.cbSize=sizeof(info);if(GetMonitorInfoW(target,&info))monitor=info.rcMonitor;
    BOOL composition=FALSE;DwmIsCompositionEnabled(&composition);DWORD cloaked=0;DwmGetWindowAttribute(root,DWMWA_CLOAKED,&cloaked,sizeof(cloaked));
    bool found=false,match=false;ComPtr<IDXGIFactory1> factory;check(adapter->GetParent(IID_PPV_ARGS(&factory)),"render diagnostic factory");
    for(UINT i=0;i<64&&!found;++i){ComPtr<IDXGIAdapter1> candidate;if(factory->EnumAdapters1(i,&candidate)==DXGI_ERROR_NOT_FOUND)break;DXGI_ADAPTER_DESC1 d{};candidate->GetDesc1(&d);
        for(UINT o=0;o<64;++o){ComPtr<IDXGIOutput> output;if(candidate->EnumOutputs(o,&output)==DXGI_ERROR_NOT_FOUND)break;DXGI_OUTPUT_DESC od{};output->GetDesc(&od);if(od.Monitor==target){found=true;match=d.AdapterLuid.HighPart==desc.AdapterLuid.HighPart&&d.AdapterLuid.LowPart==desc.AdapterLuid.LowPart;break;}}}
    DXGI_SWAP_CHAIN_DESC1 chain{};check(r.swap->GetDesc1(&chain),"render diagnostic swap desc");
    auto rect=[](RECT v){std::ostringstream s;s<<'['<<v.left<<','<<v.top<<','<<v.right<<','<<v.bottom<<']';return s.str();};std::ostringstream s;
    s<<"{\"device_luid_high\":"<<desc.AdapterLuid.HighPart<<",\"device_luid_low\":"<<desc.AdapterLuid.LowPart<<",\"window_output_found\":"<<found<<",\"device_window_adapter_match\":"<<match<<",\"window_rect\":"<<rect(window)<<",\"child_rect\":"<<rect(child)<<",\"monitor_rect\":"<<rect(monitor)<<",\"visible\":"<<IsWindowVisible(root)<<",\"iconic\":"<<IsIconic(root)<<",\"cloaked\":"<<cloaked<<",\"dpi\":"<<GetDpiForWindow(root)<<",\"dwm_enabled\":"<<composition<<",\"last_present_hr\":"<<static_cast<uint32_t>(r.last_present)<<",\"last_test_hr\":"<<static_cast<uint32_t>(r.last_test)<<",\"test_calls\":"<<r.test_calls<<",\"occlusion_idle\":"<<r.visibility.idle<<",\"hwnd_thread\":"<<r.window_thread<<",\"render_thread\":"<<GetCurrentThreadId()<<",\"swap_effect\":"<<chain.SwapEffect<<",\"swap_width\":"<<chain.Width<<",\"swap_height\":"<<chain.Height<<",\"buffer_count\":"<<chain.BufferCount<<",\"factory_current\":"<<factory->IsCurrent()<<",\"device_removed_hr\":"<<static_cast<uint32_t>(r.graphics.device->GetDeviceRemovedReason())<<'}';text=s.str();*json=text.c_str();});}
RD int32_t rd_renderer_probe_pixel(void* renderer,uint32_t x,uint32_t y,uint32_t* bgra){if(!renderer||!bgra)return E_INVALIDARG;return guarded([&]{auto& r=*static_cast<renderer_context*>(renderer);r.lifetime.verify();if(!r.drawn||x>=r.width||y>=r.height)check(E_INVALIDARG,"probe requires a drawn, unpresented frame");
    D3D11_TEXTURE2D_DESC desc{};desc.Width=desc.Height=desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_STAGING;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> pixel;check(r.graphics.device->CreateTexture2D(&desc,nullptr,&pixel),"explicit test pixel probe");D3D11_BOX box{x,y,0,x+1,y+1,1};r.graphics.commands->CopySubresourceRegion(pixel.Get(),0,0,0,0,r.backbuffer.Get(),0,&box);D3D11_MAPPED_SUBRESOURCE mapped{};check(r.graphics.commands->Map(pixel.Get(),0,D3D11_MAP_READ,0,&mapped),"test pixel Map");memcpy(bgra,mapped.pData,4);r.graphics.commands->Unmap(pixel.Get(),0);});}
RD void rd_renderer_destroy(void* renderer){delete static_cast<renderer_context*>(renderer);}
