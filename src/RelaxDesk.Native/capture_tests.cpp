#include "internal.h"
#include "capture_frame.h"
#include <sstream>
#include <cmath>

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
            D3D11_SUBRESOURCE_DATA initial{pixels,width*(format==DXGI_FORMAT_R16G16B16A16_FLOAT?8u:4u),0};ComPtr<ID3D11Texture2D> texture;check(c.graphics.device->CreateTexture2D(&d,pixels?&initial:nullptr,&texture),"self-test texture");ComPtr<IDXGIResource> resource;check(texture.As(&resource),"self-test resource");return resource;
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
        pixels[1]=0xff705030;pixels[10]=0xff103050;
        auto regional=make(4,4,DXGI_FORMAT_B8G8R8A8_UNORM,pixels);
        c.dirty={{2,2,3,3}};c.moves={{POINT{0,0},RECT{1,0,2,1}}};
        require(c.accept_surface(desktop,regional.Get())&&c.last_copy_pixels==2,"small dirty and move regions use two pixel copies");
        check(rd_capture_pixels(&c,reinterpret_cast<uint8_t*>(readback),sizeof(readback)),"regional desktop readback");
        require(memcmp(pixels,readback,sizeof(pixels))==0,"partial GPU copies match the full reference surface");
        uint64_t measured_pixels=0;check(rd_capture_copy_pixels(&c,&measured_pixels),"regional copy metric");
        require(measured_pixels==2,"reported copy area matches the regional GPU operations");
        pixels[5]=0xff223344;
        auto overlapping=make(4,4,DXGI_FORMAT_B8G8R8A8_UNORM,pixels);
        c.dirty={{1,1,2,2}};c.moves={{POINT{0,0},RECT{1,1,2,2}},{POINT{2,2},RECT{1,1,2,2}}};
        require(c.accept_surface(desktop,overlapping.Get())&&c.last_copy_pixels==3,"overlapping move and dirty destinations use final source pixels");
        check(rd_capture_pixels(&c,reinterpret_cast<uint8_t*>(readback),sizeof(readback)),"overlapping regional readback");
        require(memcmp(pixels,readback,sizeof(pixels))==0,"overlapping copies preserve the intended final surface");
        c.moves.clear();
        auto coalesced=desktop;coalesced.RectsCoalesced=TRUE;
        require(c.accept_surface(coalesced,overlapping.Get())&&c.last_copy_pixels==16,"coalesced updates use a full copy");
        auto backlog=desktop;backlog.AccumulatedFrames=2;
        require(c.accept_surface(backlog,overlapping.Get())&&c.last_copy_pixels==16,"accumulated updates use a full copy");
        auto masked=desktop;masked.ProtectedContentMaskedOut=TRUE;
        pixels[15]=0xff000000;auto protected_surface=make(4,4,DXGI_FORMAT_B8G8R8A8_UNORM,pixels);
        require(c.accept_surface(masked,protected_surface.Get())&&c.last_copy_pixels==16,"protected mask transition refreshes the complete texture");
        check(rd_capture_pixels(&c,reinterpret_cast<uint8_t*>(readback),sizeof(readback)),"masked full readback");
        require(readback[15]==0xff000000,"masked pixel outside dirty rectangles is not retained");
        require(c.accept_surface(desktop,overlapping.Get())&&c.last_copy_pixels==16,"removing the protected mask refreshes the complete texture");
        check(rd_capture_pixels(&c,reinterpret_cast<uint8_t*>(readback),sizeof(readback)),"unmasked full readback");
        require(readback[15]==0xff332211,"unmask restores source pixel outside the dirty rectangles");
        c.dirty={{0,0,5,1}};c.moves.clear();
        require(c.accept_surface(desktop,regional.Get())&&c.last_copy_pixels==16,"invalid rectangle falls back to a full copy");
        c.dirty.clear();
        auto unsupported=make(4,4,DXGI_FORMAT_R32_FLOAT);HRESULT hr=guarded([&]{c.accept_surface(desktop,unsupported.Get());});
        require(hr==DXGI_ERROR_UNSUPPORTED&&rd_error.find("format=41")!=std::string::npos&&rd_error.find("output_format=87")!=std::string::npos,"unknown format reports actual texture description");
        auto read=[&](){check(rd_capture_pixels(&c,reinterpret_cast<uint8_t*>(readback),sizeof(readback)),"color test readback");};
        auto rgba=make(4,4,DXGI_FORMAT_R8G8B8A8_UNORM,pixels);require(c.accept_surface(desktop,rgba.Get()),"RGBA SDR conversion accepted");read();require(readback[0]==0xff112233,"RGBA converts channels without second gamma");
        uint32_t gray[16];for(auto& value:gray)value=0xff808080;
        auto srgb=make(4,4,DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,gray);require(c.accept_surface(desktop,srgb.Get()),"sRGB view accepted");read();require(abs(static_cast<int>(readback[0]&255)-128)<=1,"sRGB view transfer is not double-applied");
        uint16_t fp16[64];uint16_t values[]={0,0x31c3,0x3c00,0x4a40}; // 0, .18, 1, 12.5 scRGB
        for(int i=0;i<16;++i){fp16[i*4]=fp16[i*4+1]=fp16[i*4+2]=values[i%4];fp16[i*4+3]=0x3c00;}
        auto hdr=make(4,4,DXGI_FORMAT_R16G16B16A16_FLOAT,fp16);c.color.white_nits=80;c.color.hdr=true;
        require(c.accept_surface(desktop,hdr.Get()),"FP16 HDR is tone-mapped instead of disconnected");read();
        require((readback[0]&0xffffff)==0,"HDR black stays black");
        require(abs(static_cast<int>(readback[1]&255)-118)<=2,"scRGB mid-gray gets linear-to-sRGB transfer");
        require(abs(static_cast<int>(readback[2]&255)-243)<=2,"HDR paper-white shoulder matches SDR target");
        require((readback[3]&255)>(readback[2]&255)&&(readback[3]&255)<=255,"HDR highlight has headroom above reference white");
        for(int i=0;i<16;++i){fp16[i*4]=0x4000;fp16[i*4+1]=0x3800;fp16[i*4+2]=0x3000;fp16[i*4+3]=0x3c00;}
        auto colored=make(4,4,DXGI_FORMAT_R16G16B16A16_FLOAT,fp16);require(c.accept_surface(desktop,colored.Get()),"colored scRGB highlight accepted");read();require(abs(static_cast<int>((readback[0]>>16)&255)-252)<=2&&abs(static_cast<int>((readback[0]>>8)&255)-135)<=2&&abs(static_cast<int>(readback[0]&255)-70)<=2,"scRGB shoulder preserves channel ratios before gamma");
        for(int i=0;i<16;++i){fp16[i*4]=fp16[i*4+1]=fp16[i*4+2]=0x35c3;fp16[i*4+3]=0x3c00;} // .36 scRGB / 160nit SDR white = .18
        auto scaled=make(4,4,DXGI_FORMAT_R16G16B16A16_FLOAT,fp16);c.color.white_nits=160;require(c.accept_surface(desktop,scaled.Get()),"SDR white-level calibrated HDR accepted");read();require(abs(static_cast<int>(readback[0]&255)-118)<=2,"Windows SDR white scaling does not wash out mid-gray");
        auto pq=[](double nits){double p=pow(nits/10000.0,2610.0/16384.0);return static_cast<uint32_t>(lround(pow((3424.0/4096.0+2413.0/128.0*p)/(1+2392.0/128.0*p),2523.0/32.0)*1023));};
        uint32_t pq_pixels[16];for(int i=0;i<16;++i){auto code=pq(i%4==0?0:i%4==1?14.4:i%4==2?80:1000);pq_pixels[i]=code|(code<<10)|(code<<20)|(3u<<30);}
        auto hdr10=make(4,4,DXGI_FORMAT_R10G10B10A2_UNORM,pq_pixels);c.color.white_nits=80;require(c.accept_surface(desktop,hdr10.Get()),"PQ BT2020 HDR10 accepted");read();require(abs(static_cast<int>(readback[1]&255)-118)<=3&&abs(static_cast<int>(readback[2]&255)-243)<=3,"PQ EOTF/BT2020-to-709 matches reference neutrals");
        for(auto& value:pq_pixels)value=pq(14.4)|(3u<<30);auto pq_red=make(4,4,DXGI_FORMAT_R10G10B10A2_UNORM,pq_pixels);require(c.accept_surface(desktop,pq_red.Get()),"PQ BT2020 primary accepted");read();require(abs(static_cast<int>((readback[0]>>16)&255)-149)<=3&&(readback[0]&0xffff)==0,"BT2020 primary is transformed to BT709 and gamut clipped");
        c.color.hdr=false;for(auto& value:pq_pixels)value=512u|(512u<<10)|(512u<<20)|(3u<<30);auto rgb10=make(4,4,DXGI_FORMAT_R10G10B10A2_UNORM,pq_pixels);require(c.accept_surface(desktop,rgb10.Get()),"SDR RGB10 accepted");read();require(abs(static_cast<int>(readback[0]&255)-128)<=1,"SDR RGB10 is not mistaken for PQ");
        c.duplication_desc.ModeDesc.Width=8;hr=guarded([&]{c.accept_surface(pointer,nullptr);});require(hr==DXGI_ERROR_ACCESS_LOST,"pointer update cannot hide mode changes");c.duplication_desc.ModeDesc.Width=4;
        auto resized=make(8,4,DXGI_FORMAT_B8G8R8A8_UNORM);hr=guarded([&]{c.accept_surface(desktop,resized.Get());});require(hr==DXGI_ERROR_ACCESS_LOST,"new image dimensions require capture/encoder recreation");
    }
    text="PASS "+std::to_string(tests)+" capture/color/release WARP contracts; scRGB/PQ/SDR white/transfer validated on GPU textures; no physical HDR desktop validation";*report=text.c_str();
});}
