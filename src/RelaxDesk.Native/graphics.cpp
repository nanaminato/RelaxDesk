#include "internal.h"
thread_local std::string rd_error;
RD uint32_t rd_abi_version(){return 1;}
RD const char* rd_last_error(){return rd_error.c_str();}
void gpu::create(IDXGIAdapter* adapter) {
    check(D3D11CreateDevice(adapter,adapter?D3D_DRIVER_TYPE_UNKNOWN:D3D_DRIVER_TYPE_HARDWARE,nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT|D3D11_CREATE_DEVICE_VIDEO_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&commands),"D3D11CreateDevice hardware");
    ComPtr<ID3D10Multithread> multithread;
    if(SUCCEEDED(commands.As(&multithread)))multithread->SetMultithreadProtected(TRUE);
}
void capture_context::initialize() {
    duplication.Reset();texture.Reset();staging.Reset();graphics={};
    ComPtr<IDXGIFactory1> factory;check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)),"CreateDXGIFactory1");
    // Choose the attached primary physical output and its adapter, never a different logon session.
    for(UINT a=0;;++a){ComPtr<IDXGIAdapter1> adapter;HRESULT hr=factory->EnumAdapters1(a,&adapter);if(hr==DXGI_ERROR_NOT_FOUND)break;check(hr,"EnumAdapters1");
        for(UINT o=0;;++o){ComPtr<IDXGIOutput> candidate;hr=adapter->EnumOutputs(o,&candidate);if(hr==DXGI_ERROR_NOT_FOUND)break;check(hr,"EnumOutputs");
            DXGI_OUTPUT_DESC desc{};check(candidate->GetDesc(&desc),"GetDesc output");
            if(!desc.AttachedToDesktop||desc.DesktopCoordinates.left!=0||desc.DesktopCoordinates.top!=0)continue;
            if(desc.Rotation!=DXGI_MODE_ROTATION_IDENTITY)check(DXGI_ERROR_UNSUPPORTED,"rotated output unsupported");
            graphics.create(adapter.Get());ComPtr<IDXGIOutput1> output1;check(candidate.As(&output1),"IDXGIOutput1");
            check(output1->DuplicateOutput(graphics.device.Get(),&duplication),"DuplicateOutput primary display");output=desc;return;
        }
    }
    check(DXGI_ERROR_NOT_FOUND,"no attached primary DXGI output");
}
void capture_context::acquire(uint32_t timeout,rd_frame& result) {
    lifetime.verify();if(!duplication)initialize();
    DXGI_OUTDUPL_FRAME_INFO info{};ComPtr<IDXGIResource> resource;
    HRESULT hr=duplication->AcquireNextFrame(timeout,&info,&resource);
    if(hr==DXGI_ERROR_WAIT_TIMEOUT)throw failure{S_FALSE};
    if(hr==DXGI_ERROR_ACCESS_LOST||hr==DXGI_ERROR_DEVICE_REMOVED||hr==DXGI_ERROR_DEVICE_RESET){duplication.Reset();texture.Reset();staging.Reset();check(hr,"duplication lost; recreate capture and encoder");}
    check(hr,"AcquireNextFrame");
    struct frame_guard{IDXGIOutputDuplication* d;~frame_guard(){if(d)d->ReleaseFrame();}} release{duplication.Get()};
    ComPtr<ID3D11Texture2D> source;check(resource.As(&source),"desktop texture");D3D11_TEXTURE2D_DESC desc{};source->GetDesc(&desc);
    if(desc.Width>7680||desc.Height>4320||desc.Format!=DXGI_FORMAT_B8G8R8A8_UNORM)check(DXGI_ERROR_UNSUPPORTED,"unsupported capture dimensions/format");
    if(!texture){desc.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET;desc.MiscFlags=0;desc.Usage=D3D11_USAGE_DEFAULT;desc.CPUAccessFlags=0;check(graphics.device->CreateTexture2D(&desc,nullptr,&texture),"create owned desktop texture");}
    D3D11_TEXTURE2D_DESC owned{};texture->GetDesc(&owned);if(owned.Width!=desc.Width||owned.Height!=desc.Height)check(DXGI_ERROR_ACCESS_LOST,"resolution changed; recreate capture and encoder");
    graphics.commands->CopyResource(texture.Get(),source.Get()); // GPU -> GPU only; no staging on encode path.
    dirty.clear();moves.clear();UINT bytes=0;
    if(info.TotalMetadataBufferSize){
        dirty.resize((info.TotalMetadataBufferSize+sizeof(RECT)-1)/sizeof(RECT));
        check(duplication->GetFrameDirtyRects(static_cast<UINT>(dirty.size()*sizeof(RECT)),dirty.data(),&bytes),"GetFrameDirtyRects");dirty.resize(bytes/sizeof(RECT));
        moves.resize((info.TotalMetadataBufferSize+sizeof(DXGI_OUTDUPL_MOVE_RECT)-1)/sizeof(DXGI_OUTDUPL_MOVE_RECT));
        check(duplication->GetFrameMoveRects(static_cast<UINT>(moves.size()*sizeof(DXGI_OUTDUPL_MOVE_RECT)),moves.data(),&bytes),"GetFrameMoveRects");moves.resize(bytes/sizeof(DXGI_OUTDUPL_MOVE_RECT));
    }
    if(info.PointerShapeBufferSize){pointer.resize(info.PointerShapeBufferSize);check(duplication->GetFramePointerShape(static_cast<UINT>(pointer.size()),pointer.data(),&bytes,&shape),"GetFramePointerShape");pointer.resize(bytes);}
    if(info.LastMouseUpdateTime.QuadPart)pointer_position=info.PointerPosition;
    result={desc.Width,desc.Height,info.LastPresentTime.QuadPart,info.AccumulatedFrames,static_cast<uint32_t>(dirty.size()),static_cast<uint32_t>(moves.size()),static_cast<uint32_t>(pointer.size()),
        pointer_position.Position.x,pointer_position.Position.y,static_cast<uint32_t>(pointer_position.Visible),shape.Type,shape.Width,shape.Height,shape.Pitch,shape.HotSpot.x,shape.HotSpot.y};
    // Return ReleaseFrame errors rather than silently continuing with an invalid duplication.
    hr=duplication->ReleaseFrame();release.d=nullptr;
    if(FAILED(hr)){duplication.Reset();check(hr,"ReleaseFrame");}
}
RD int32_t rd_capture_create(void** context){if(!context)return E_POINTER;*context=nullptr;return guarded([&]{auto c=std::make_unique<capture_context>();c->initialize();*context=c.release();});}
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
