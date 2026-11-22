#include "internal.h"
#include "capture_frame.h"
#include <sstream>

RD int32_t rd_capture_self_test(const char** report){if(!report)return E_POINTER;static thread_local std::string text;return guarded([&]{
    int tests=0;
    auto require=[&](bool ok,const char* name){check(ok?S_OK:E_FAIL,name);++tests;};
    DXGI_OUTDUPL_FRAME_INFO pointer{};pointer.LastMouseUpdateTime.QuadPart=2;
    require(classify_capture(pointer,false)==capture_action::wait,"pointer-only before initial image must wait");
    require(classify_capture(pointer,true)==capture_action::cached,"pointer-only must reuse cached image");
    auto desktop=pointer;desktop.LastPresentTime.QuadPart=1;desktop.AccumulatedFrames=1;
    require(classify_capture(desktop,true)==capture_action::desktop,"real image update must copy image");
    auto accumulated=pointer;accumulated.AccumulatedFrames=1;require(classify_capture(accumulated,true)==capture_action::desktop,"accumulated image is not pointer-only");
    auto metadata=pointer;metadata.TotalMetadataBufferSize=16;require(classify_capture(metadata,true)==capture_action::desktop,"image metadata must not be ignored");
    require(capture_dimensions(3840,2160),"4K pixel boundary");require(capture_dimensions(1080,7680),"portrait/stacked dimensions within same pixel cap");
    require(!capture_dimensions(0,1080)&&!capture_dimensions(1920,0),"zero dimensions rejected");
    require(!capture_dimensions(8193,1)&&!capture_dimensions(8192,8192),"dimension and pixel caps remain bounded");
    struct fake_owner{int releases=0;HRESULT result=S_OK;HRESULT ReleaseFrame(){++releases;return result;}};
    fake_owner normal;{capture_lease frame{&normal};require(frame.finish()==S_OK,"explicit frame release");}require(normal.releases==1,"success releases exactly once");
    fake_owner exceptional;try{capture_lease frame{&exceptional};throw 1;}catch(int){}require(exceptional.releases==1,"exception releases exactly once");
    fake_owner failed;failed.result=E_FAIL;{capture_lease frame{&failed};require(frame.finish()==E_FAIL,"ReleaseFrame failure is observable");}require(failed.releases==1,"failed release is not repeated");
    {
        capture_context c;
        check(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&c.graphics.device,nullptr,&c.graphics.commands),"capture self-test WARP device");
        auto make=[&](UINT width,UINT height,DXGI_FORMAT format,const void* pixels=nullptr){
            D3D11_TEXTURE2D_DESC d{};d.Width=width;d.Height=height;d.MipLevels=d.ArraySize=d.SampleDesc.Count=1;d.Format=format;d.BindFlags=D3D11_BIND_SHADER_RESOURCE;
            D3D11_SUBRESOURCE_DATA initial{pixels,width*4,0};ComPtr<ID3D11Texture2D> texture;check(c.graphics.device->CreateTexture2D(&d,pixels?&initial:nullptr,&texture),"self-test texture");ComPtr<IDXGIResource> resource;check(texture.As(&resource),"self-test resource");return resource;
        };
        uint32_t pixels[16];for(auto& pixel:pixels)pixel=0xff332211;
        auto bgra=make(4,4,DXGI_FORMAT_B8G8R8A8_UNORM,pixels);
        auto other=make(1,1,DXGI_FORMAT_R8G8B8A8_UNORM);
        c.duplication_desc.ModeDesc.Width=c.duplication_desc.ModeDesc.Height=4;c.duplication_desc.ModeDesc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
        require(!c.accept_surface(pointer,other.Get()),"initial pointer-only does not initialize pixels from unrelated resource");
        require(c.accept_surface(desktop,bgra.Get()),"real desktop texture accepted");
        uint32_t readback[16]{};check(rd_capture_pixels(&c,reinterpret_cast<uint8_t*>(readback),sizeof(readback)),"self-test desktop readback");require(memcmp(pixels,readback,sizeof(pixels))==0,"owned image contains desktop pixels");
        auto* cached=c.texture.Get();
        require(c.accept_surface(pointer,other.Get())&&c.texture.Get()==cached,"pointer-only wrong-format resource never replaces image");
        require(c.accept_surface(pointer,nullptr),"pointer-only requires no resource dereference");
        check(rd_capture_pixels(&c,reinterpret_cast<uint8_t*>(readback),sizeof(readback)),"self-test pointer reuse readback");require(memcmp(pixels,readback,sizeof(pixels))==0,"pointer-only keeps exact previous image");
        HRESULT hr=guarded([&]{c.accept_surface(desktop,other.Get());});
        require(hr==DXGI_ERROR_UNSUPPORTED&&rd_error.find("width=1 height=1 format=28")!=std::string::npos&&rd_error.find("expected_format=87")!=std::string::npos,"real unsupported format reports actual texture description");
        auto hdr=make(4,4,DXGI_FORMAT_R16G16B16A16_FLOAT);hr=guarded([&]{c.accept_surface(desktop,hdr.Get());});
        require(hr==DXGI_ERROR_UNSUPPORTED&&rd_error.find("format=10")!=std::string::npos,"real HDR surface is not silently interpreted as SDR");
        c.duplication_desc.ModeDesc.Width=8;hr=guarded([&]{c.accept_surface(pointer,nullptr);});require(hr==DXGI_ERROR_ACCESS_LOST,"pointer update cannot hide mode changes");c.duplication_desc.ModeDesc.Width=4;
        auto resized=make(8,4,DXGI_FORMAT_B8G8R8A8_UNORM);hr=guarded([&]{c.accept_surface(desktop,resized.Get());});require(hr==DXGI_ERROR_ACCESS_LOST,"new image dimensions require capture/encoder recreation");
    }
    text="PASS "+std::to_string(tests)+" capture classification/release/WARP texture contracts; pointer-only preserves exact pixels; no Desktop Duplication or HDR physical validation";*report=text.c_str();
});}
