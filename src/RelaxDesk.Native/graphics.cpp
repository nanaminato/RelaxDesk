#include "internal.h"
#include "capture_frame.h"
#include <dxgi1_5.h>
#include <sstream>
thread_local std::string rd_error;
RD uint32_t rd_abi_version(){return 2;}
RD void rd_get_live_resources(rd_live_resources* resources){if(resources)*resources={live_resources[0].load(),live_resources[1].load(),live_resources[2].load(),live_resources[3].load(),live_resources[4].load(),live_resources[5].load(),live_resources[6].load()};}
RD const char* rd_last_error(){return rd_error.c_str();}
void gpu::create(IDXGIAdapter* adapter) {
    check(D3D11CreateDevice(adapter,adapter?D3D_DRIVER_TYPE_UNKNOWN:D3D_DRIVER_TYPE_HARDWARE,nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT|D3D11_CREATE_DEVICE_VIDEO_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&commands),"D3D11CreateDevice hardware");
    ComPtr<ID3D10Multithread> multithread;
    if(SUCCEEDED(commands.As(&multithread)))multithread->SetMultithreadProtected(TRUE);
}
void capture_context::initialize() {
    duplication.Reset();texture.Reset();staging.Reset();converter.reset();color_output.Reset();graphics={};
    dirty.clear();moves.clear();pointer.clear();shape={};pointer_position={};
    duplication_desc={};format_negotiated=false;last_desktop_time=0;
    source_format=DXGI_FORMAT_UNKNOWN;color={};color_checked=0;
    ComPtr<IDXGIFactory1> factory;check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)),"CreateDXGIFactory1");
    // Choose the attached primary physical output and its adapter, never a different logon session.
    for(UINT a=0;;++a){ComPtr<IDXGIAdapter1> adapter;HRESULT hr=factory->EnumAdapters1(a,&adapter);if(hr==DXGI_ERROR_NOT_FOUND)break;check(hr,"EnumAdapters1");
        for(UINT o=0;;++o){ComPtr<IDXGIOutput> candidate;hr=adapter->EnumOutputs(o,&candidate);if(hr==DXGI_ERROR_NOT_FOUND)break;check(hr,"EnumOutputs");
            DXGI_OUTPUT_DESC desc{};check(candidate->GetDesc(&desc),"GetDesc output");
            if(!desc.AttachedToDesktop)continue;
            if(selected_device.empty()?(desc.DesktopCoordinates.left!=0||desc.DesktopCoordinates.top!=0):selected_device!=desc.DeviceName)continue;
            if(desc.Rotation!=DXGI_MODE_ROTATION_IDENTITY)check(DXGI_ERROR_UNSUPPORTED,"rotated output unsupported");
            output=desc;candidate.As(&color_output);refresh_color();
            graphics.create(adapter.Get());ComPtr<IDXGIOutput5> output5;
            HRESULT newer=candidate.As(&output5);
            if(SUCCEEDED(newer)){
                // Keep HDR/scRGB precision until our GPU tone mapper, rather
                // than asking DXGI to clip it into an eight-bit SDR surface.
                DXGI_FORMAT supported[]={DXGI_FORMAT_R16G16B16A16_FLOAT,DXGI_FORMAT_R10G10B10A2_UNORM,DXGI_FORMAT_B8G8R8A8_UNORM,DXGI_FORMAT_R8G8B8A8_UNORM};
                newer=output5->DuplicateOutput1(graphics.device.Get(),0,4,supported,&duplication);
                if(SUCCEEDED(newer))format_negotiated=true;
            }
            if(!format_negotiated){
                if(newer!=E_NOINTERFACE&&newer!=E_NOTIMPL&&newer!=DXGI_ERROR_UNSUPPORTED)check(newer,"DuplicateOutput1 HDR/SDR primary display");
                ComPtr<IDXGIOutput1> output1;check(candidate.As(&output1),"IDXGIOutput1");
                check(output1->DuplicateOutput(graphics.device.Get(),&duplication),"DuplicateOutput primary display");
            }
            duplication->GetDesc(&duplication_desc);output=desc;return;
        }
    }
    check(DXGI_ERROR_NOT_FOUND,"selected DXGI output is not attached");
}
bool capture_context::accept_surface(const DXGI_OUTDUPL_FRAME_INFO& info,IDXGIResource* resource) {
    auto action=classify_capture(info,texture!=nullptr);
    if(action==capture_action::wait)return false;
    if(action==capture_action::cached){
        D3D11_TEXTURE2D_DESC owned{};texture->GetDesc(&owned);
        if((duplication_desc.ModeDesc.Width&&owned.Width!=duplication_desc.ModeDesc.Width)||(duplication_desc.ModeDesc.Height&&owned.Height!=duplication_desc.ModeDesc.Height))check(DXGI_ERROR_ACCESS_LOST,"cached desktop mode changed; recreate capture and encoder");
        return true; // Pointer-only notification: never inspect/copy a new image.
    }
    if(!resource)check(E_POINTER,"desktop update has no texture resource");
    ComPtr<ID3D11Texture2D> source;check(resource->QueryInterface(IID_PPV_ARGS(&source)),"desktop texture");D3D11_TEXTURE2D_DESC desc{};source->GetDesc(&desc);
    auto detail=[&](const char* reason){std::ostringstream s;s<<reason<<" width="<<desc.Width<<" height="<<desc.Height<<" format="<<desc.Format<<" output_format="<<DXGI_FORMAT_B8G8R8A8_UNORM<<" mips="<<desc.MipLevels<<" array="<<desc.ArraySize<<" samples="<<desc.SampleDesc.Count<<" present_qpc="<<info.LastPresentTime.QuadPart<<" mouse_qpc="<<info.LastMouseUpdateTime.QuadPart<<" accumulated="<<info.AccumulatedFrames<<" metadata_bytes="<<info.TotalMetadataBufferSize<<" dupl_width="<<duplication_desc.ModeDesc.Width<<" dupl_height="<<duplication_desc.ModeDesc.Height<<" dupl_format="<<duplication_desc.ModeDesc.Format<<" api="<<(format_negotiated?"DuplicateOutput1-HDR-SDR":"DuplicateOutput");return s.str();};
    if(!capture_dimensions(desc.Width,desc.Height))check(DXGI_ERROR_UNSUPPORTED,detail("unsupported capture dimensions (pixel cap 8294400)").c_str());
    bool supported=desc.Format==DXGI_FORMAT_B8G8R8A8_UNORM||desc.Format==DXGI_FORMAT_B8G8R8A8_UNORM_SRGB||desc.Format==DXGI_FORMAT_B8G8R8X8_UNORM||desc.Format==DXGI_FORMAT_B8G8R8X8_UNORM_SRGB||desc.Format==DXGI_FORMAT_R8G8B8A8_UNORM||desc.Format==DXGI_FORMAT_R8G8B8A8_UNORM_SRGB||desc.Format==DXGI_FORMAT_R10G10B10A2_UNORM||desc.Format==DXGI_FORMAT_R16G16B16A16_FLOAT;
    if(!supported)check(DXGI_ERROR_UNSUPPORTED,detail("unsupported capture format (supported: BGRA/RGBA8, RGB10, FP16 scRGB)").c_str());
    if(desc.MipLevels!=1||desc.ArraySize!=1||desc.SampleDesc.Count!=1)check(DXGI_ERROR_UNSUPPORTED,detail("unsupported capture texture layout").c_str());
    if(!texture){auto owned=desc;owned.Format=DXGI_FORMAT_B8G8R8A8_UNORM;owned.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET;owned.MiscFlags=0;owned.Usage=D3D11_USAGE_DEFAULT;owned.CPUAccessFlags=0;check(graphics.device->CreateTexture2D(&owned,nullptr,&texture),"create owned desktop texture");}
    D3D11_TEXTURE2D_DESC owned{};texture->GetDesc(&owned);if(owned.Width!=desc.Width||owned.Height!=desc.Height)check(DXGI_ERROR_ACCESS_LOST,detail("resolution changed; recreate capture and encoder").c_str());
    if(color_output&&(source_format!=desc.Format||GetTickCount64()-color_checked>=1000))refresh_color();
    if(desc.Format==DXGI_FORMAT_B8G8R8A8_UNORM)graphics.commands->CopyResource(texture.Get(),source.Get());
    else {if(!converter)converter=std::make_unique<desktop_converter>();converter->convert(graphics,source.Get(),texture.Get(),color);}
    source_format=desc.Format;last_desktop_time=info.LastPresentTime.QuadPart;return true;
}
void capture_context::acquire(uint32_t timeout,rd_frame& result) {
    lifetime.verify();if(!duplication)initialize();
    DXGI_OUTDUPL_FRAME_INFO info{};ComPtr<IDXGIResource> resource;
    HRESULT hr=duplication->AcquireNextFrame(timeout,&info,&resource);
    if(hr==DXGI_ERROR_WAIT_TIMEOUT)throw failure{S_FALSE};
    if(hr==DXGI_ERROR_ACCESS_LOST||hr==DXGI_ERROR_DEVICE_REMOVED||hr==DXGI_ERROR_DEVICE_RESET){duplication.Reset();texture.Reset();staging.Reset();check(hr,"duplication lost; recreate capture and encoder");}
    check(hr,"AcquireNextFrame");
    capture_lease release{duplication.Get()};
    if(info.TotalMetadataBufferSize>1024*1024||info.PointerShapeBufferSize>1024*1024)check(E_INVALIDARG,"capture metadata cap exceeded");
    bool has_desktop=accept_surface(info,resource.Get());
    dirty.clear();moves.clear();UINT bytes=0;
    if(info.TotalMetadataBufferSize){
        dirty.resize((info.TotalMetadataBufferSize+sizeof(RECT)-1)/sizeof(RECT));
        check(duplication->GetFrameDirtyRects(static_cast<UINT>(dirty.size()*sizeof(RECT)),dirty.data(),&bytes),"GetFrameDirtyRects");dirty.resize(bytes/sizeof(RECT));
        moves.resize((info.TotalMetadataBufferSize+sizeof(DXGI_OUTDUPL_MOVE_RECT)-1)/sizeof(DXGI_OUTDUPL_MOVE_RECT));
        check(duplication->GetFrameMoveRects(static_cast<UINT>(moves.size()*sizeof(DXGI_OUTDUPL_MOVE_RECT)),moves.data(),&bytes),"GetFrameMoveRects");moves.resize(bytes/sizeof(DXGI_OUTDUPL_MOVE_RECT));
    }
    if(info.PointerShapeBufferSize){pointer.resize(info.PointerShapeBufferSize);check(duplication->GetFramePointerShape(static_cast<UINT>(pointer.size()),pointer.data(),&bytes,&shape),"GetFramePointerShape");pointer.resize(bytes);}
    if(info.LastMouseUpdateTime.QuadPart)pointer_position=info.PointerPosition;
    D3D11_TEXTURE2D_DESC desc{};if(texture)texture->GetDesc(&desc);
    result={desc.Width,desc.Height,last_desktop_time,info.AccumulatedFrames,static_cast<uint32_t>(dirty.size()),static_cast<uint32_t>(moves.size()),static_cast<uint32_t>(pointer.size()),
        pointer_position.Position.x,pointer_position.Position.y,static_cast<uint32_t>(pointer_position.Visible),shape.Type,shape.Width,shape.Height,shape.Pitch,shape.HotSpot.x,shape.HotSpot.y};
    // Return ReleaseFrame errors rather than silently continuing with an invalid duplication.
    hr=release.finish();
    if(FAILED(hr)){duplication.Reset();check(hr,"ReleaseFrame");}
    if(!has_desktop)throw failure{S_FALSE};
}
RD int32_t rd_capture_create(void** context){if(!context)return E_POINTER;*context=nullptr;return guarded([&]{auto c=std::make_unique<capture_context>();c->initialize();*context=c.release();});}
RD int32_t rd_capture_create_display(const wchar_t* name,void** context){if(!context)return E_POINTER;*context=nullptr;if(!name||wcsnlen_s(name,33)==0||wcsnlen_s(name,33)>31)return E_INVALIDARG;return guarded([&]{auto c=std::make_unique<capture_context>();c->selected_device=name;c->initialize();*context=c.release();});}
RD int32_t rd_capture_next(void* context,uint32_t timeout,rd_frame* frame){if(!context||!frame||timeout>1000)return E_INVALIDARG;return guarded([&]{static_cast<capture_context*>(context)->acquire(timeout,*frame);});}
RD int32_t rd_capture_blob(void* context,uint32_t kind,const void** data,uint32_t* bytes){if(!context||!data||!bytes)return E_INVALIDARG;return guarded([&]{auto& c=*static_cast<capture_context*>(context);c.lifetime.verify();
    if(kind==0){*data=c.dirty.data();*bytes=static_cast<uint32_t>(c.dirty.size()*sizeof(RECT));}
    else if(kind==1){*data=c.moves.data();*bytes=static_cast<uint32_t>(c.moves.size()*sizeof(DXGI_OUTDUPL_MOVE_RECT));}
    else if(kind==2){*data=c.pointer.data();*bytes=static_cast<uint32_t>(c.pointer.size());}else check(E_INVALIDARG,"blob kind");});}
RD int32_t rd_capture_pixels(void* context,uint8_t* data,uint32_t bytes){if(!context||!data)return E_INVALIDARG;return guarded([&]{auto& c=*static_cast<capture_context*>(context);c.lifetime.verify();if(!c.texture)check(E_UNEXPECTED,"capture before readback");
    D3D11_TEXTURE2D_DESC desc{};c.texture->GetDesc(&desc);if(bytes!=static_cast<uint64_t>(desc.Width)*desc.Height*4)check(E_INVALIDARG,"readback capacity");
    if(!c.staging){desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;desc.MiscFlags=0;check(c.graphics.device->CreateTexture2D(&desc,nullptr,&c.staging),"readback texture");}
    c.graphics.commands->CopyResource(c.staging.Get(),c.texture.Get());D3D11_MAPPED_SUBRESOURCE mapped{};check(c.graphics.commands->Map(c.staging.Get(),0,D3D11_MAP_READ,0,&mapped),"readback Map");
    for(UINT y=0;y<desc.Height;++y)memcpy(data+y*desc.Width*4,static_cast<uint8_t*>(mapped.pData)+y*mapped.RowPitch,desc.Width*4);c.graphics.commands->Unmap(c.staging.Get(),0);
});}
RD void rd_capture_destroy(void* context){delete static_cast<capture_context*>(context);}
