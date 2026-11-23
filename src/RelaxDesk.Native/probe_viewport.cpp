#include <windows.h>
#include <cstdio>
#include <vector>
#include <cstring>
#include <stdexcept>
#include <cmath>
#include "api.h"

int viewport_render_contracts(){
    HWND parent=nullptr;void *child=nullptr,*encoder=nullptr,*decoder=nullptr,*frame=nullptr,*renderer=nullptr;int checks=0;
    auto require=[](HRESULT hr){if(FAILED(hr)){printf("viewport_hr=0x%08lx operation=%s\n",hr,rd_last_error());throw std::runtime_error("viewport graphics call failed");}};
    try{
        parent=CreateWindowExW(0,L"STATIC",L"RelaxDesk viewport draw contracts",WS_OVERLAPPEDWINDOW|WS_CLIPCHILDREN,100,100,360,240,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
        if(!parent)throw std::runtime_error("viewport root creation failed");ShowWindow(parent,SW_SHOWNOACTIVATE);
        require(rd_window_create(parent,&child));MoveWindow(static_cast<HWND>(child),0,0,320,180,TRUE);
        require(rd_encoder_create(nullptr,640,360,30,4000000,1,&encoder));require(rd_decoder_create(640,360,1,&decoder));
        std::vector<uint8_t> pixels(640*360*4);
        for(int y=0;y<360;++y)for(int x=0;x<640;++x){auto p=&pixels[(y*640+x)*4];p[0]=x<320?40:180;p[1]=y<180?30:160;p[2]=x<320?160:40;p[3]=255;}
        for(int i=0;i<20&&!frame;++i){const uint8_t* data=nullptr;uint32_t bytes=0;int key=0;int64_t stamp=0;
            HRESULT hr=rd_encoder_bgra(encoder,pixels.data(),static_cast<uint32_t>(pixels.size()),i*333333LL,i==0,&data,&bytes,&key,&stamp);require(hr);if(hr==S_FALSE)continue;
            hr=rd_decoder_gpu_frame(decoder,data,bytes,stamp,&frame);require(hr);
        }
        if(!frame)throw std::runtime_error("viewport fixture produced no decoded GPU frame");require(rd_renderer_create(frame,child,&renderer));
        std::vector<uint8_t> metadata(68);int fields[14]={1,0,0,4,510,300,1,2,1,1,4,0,0,1};memcpy(metadata.data(),fields,sizeof(fields));metadata[64]=250;metadata[65]=10;metadata[66]=20;metadata[67]=255;
        auto draw=[&](int zoom,double x,double y){require(rd_renderer_set_view(renderer,zoom,x,y));HRESULT hr=rd_renderer_draw(renderer,frame,metadata.data(),static_cast<uint32_t>(metadata.size()));require(hr);if(hr!=S_OK)throw std::runtime_error("viewport draw skipped");++checks;};
        auto pixel=[&](int x,int y,int b,int g,int r,int tolerance){uint32_t color=0;require(rd_renderer_probe_pixel(renderer,x,y,&color));if(abs(static_cast<int>(color&255)-b)>tolerance||abs(static_cast<int>((color>>8)&255)-g)>tolerance||abs(static_cast<int>((color>>16)&255)-r)>tolerance)throw std::runtime_error("viewport crop/pointer pixel mismatch");++checks;};
        draw(0,.5,.5);pixel(20,20,40,30,160,15);pixel(300,160,180,160,40,15);pixel(255,150,250,10,20,0);
        draw(100,0,0);pixel(20,20,40,30,160,15);pixel(300,160,40,30,160,15);
        draw(100,1,1);pixel(20,20,180,160,40,15);pixel(190,120,250,10,20,0);
        draw(200,1,1);pixel(20,20,180,160,40,15);pixel(60,60,250,10,20,0);
        draw(25,.5,.5);pixel(10,10,0,0,0,0);pixel(100,60,40,30,160,15);
        // Retained-frame redraw changes the crop without receiving/decoding a new frame.
        draw(100,0,1);pixel(20,20,40,160,160,15);
        if(rd_renderer_set_view(renderer,401,0,0)>=0)throw std::runtime_error("invalid renderer zoom accepted");++checks;
        rd_renderer_destroy(renderer);renderer=nullptr;rd_gpu_frame_destroy(frame);frame=nullptr;rd_decoder_destroy(decoder);decoder=nullptr;rd_encoder_destroy(encoder);encoder=nullptr;require(rd_window_destroy(child));child=nullptr;DestroyWindow(parent);parent=nullptr;
        rd_live_resources live{};rd_get_live_resources(&live);if(live.captures||live.encoders||live.decoders||live.frames||live.renderers||live.windows||live.callbacks)throw std::runtime_error("viewport resource leak");++checks;
        printf("PASS %d actual GPU viewport draw/crop/1:1/zoom/pan/cursor/retained-redraw/resource contracts; real H264 GPU decode and explicit test-only pixel probes; no Present/physical visibility/capture/input claim\n",checks);return 0;
    }catch(const std::exception& error){printf("FAIL viewport draw contracts: %s\n",error.what());if(renderer)rd_renderer_destroy(renderer);if(frame)rd_gpu_frame_destroy(frame);if(decoder)rd_decoder_destroy(decoder);if(encoder)rd_encoder_destroy(encoder);if(child)rd_window_destroy(child);if(parent)DestroyWindow(parent);return 1;}
}
