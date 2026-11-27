#include "internal.h"
#include <codecapi.h>
#include <wmcodecdsp.h>
#include <algorithm>
#include "mft_events.h"

namespace {
ComPtr<IMFSample> sample_buffer(DWORD capacity) {
    ComPtr<IMFSample> sample;ComPtr<IMFMediaBuffer> buffer;
    check(MFCreateSample(&sample),"MFCreateSample");check(MFCreateMemoryBuffer(capacity,&buffer),"MFCreateMemoryBuffer");check(sample->AddBuffer(buffer.Get()),"AddBuffer");return sample;
}
ComPtr<IMFSample> transform_output(IMFTransform* transform) {
    MFT_OUTPUT_STREAM_INFO info{};check(transform->GetOutputStreamInfo(0,&info),"GetOutputStreamInfo");
    ComPtr<IMFSample> allocated;
    if(!(info.dwFlags&MFT_OUTPUT_STREAM_PROVIDES_SAMPLES))allocated=sample_buffer(std::max<DWORD>(info.cbSize,4u*1024*1024));
    MFT_OUTPUT_DATA_BUFFER output{};output.pSample=allocated.Get();DWORD status=0;
    HRESULT hr=transform->ProcessOutput(0,1,&output,&status);
    if(output.pEvents)output.pEvents->Release();
    ComPtr<IMFSample> sample;
    if(output.pSample==allocated.Get())sample=allocated;else sample.Attach(output.pSample);
    check(hr,"ProcessOutput");return sample;
}
HRESULT codec_property(IMFTransform* transform,const GUID& property,ULONG value,bool boolean=false) {
    ComPtr<ICodecAPI> api;HRESULT hr=transform->QueryInterface(IID_PPV_ARGS(&api));if(FAILED(hr))return hr;
    VARIANT v;VariantInit(&v);v.vt=static_cast<VARTYPE>(boolean?VT_BOOL:VT_UI4);if(boolean)v.boolVal=value?VARIANT_TRUE:VARIANT_FALSE;else v.ulVal=value;
    // Vendor optional properties may be unsupported. Actual hardware status comes from MFT enumeration, not these flags.
    return api->SetValue(&property,&v);
}
void start(IMFTransform* transform){check(transform->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING,0),"begin streaming");check(transform->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM,0),"start stream");}
void shutdown(IMFTransform* transform){if(!transform)return;transform->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH,0);ComPtr<IMFShutdown> shutdown;if(SUCCEEDED(transform->QueryInterface(IID_PPV_ARGS(&shutdown))))shutdown->Shutdown();}
uint8_t clamp(int v){return static_cast<uint8_t>(std::clamp(v,0,255));}
void bgra_nv12(const uint8_t* src,uint8_t* dst,UINT w,UINT h) {
    // Explicit CPU fallback: limited-range BT.709, 4:2:0. Hardware path never calls this function.
    for(UINT y=0;y<h;++y)for(UINT x=0;x<w;++x){auto p=src+(static_cast<size_t>(y)*w+x)*4;dst[y*w+x]=clamp(((47*p[2]+157*p[1]+16*p[0]+128)>>8)+16);}
    auto uv=dst+static_cast<size_t>(w)*h;
    for(UINT y=0;y<h;y+=2)for(UINT x=0;x<w;x+=2){int r=0,g=0,b=0;for(UINT dy=0;dy<2;++dy)for(UINT dx=0;dx<2;++dx){auto p=src+((static_cast<size_t>(y+dy))*w+x+dx)*4;r+=p[2];g+=p[1];b+=p[0];}r/=4;g/=4;b/=4;uv[(y/2)*w+x]=clamp(((-26*r-87*g+112*b+128)>>8)+128);uv[(y/2)*w+x+1]=clamp(((112*r-102*g-10*b+128)>>8)+128);}
}
void nv12_bgra(const uint8_t* src,LONG stride,uint8_t* dst,UINT w,UINT h,UINT coded_height) {
    if(stride<static_cast<LONG>(w))check(MF_E_INVALIDMEDIATYPE,"unsupported NV12 stride");auto uv=src+static_cast<size_t>(stride)*coded_height;
    for(UINT y=0;y<h;++y)for(UINT x=0;x<w;++x){int c=src[y*stride+x]-16,d=uv[(y/2)*stride+(x&~1u)]-128,e=uv[(y/2)*stride+(x&~1u)+1]-128;auto p=dst+(static_cast<size_t>(y)*w+x)*4;p[2]=clamp((298*c+459*e+128)>>8);p[1]=clamp((298*c-55*d-136*e+128)>>8);p[0]=clamp((298*c+541*d+128)>>8);p[3]=255;}
}

struct encoder_context {
    resource_counter tracked{1};
    runtime lifetime;capture_context* capture=nullptr;std::unique_ptr<capture_context> synthetic;UINT width,height,fps;bool hardware=false,input_ready=false;
    ComPtr<IMFTransform> transform;ComPtr<IMFMediaEventGenerator> events;ComPtr<mft_events> event_pump;
    rd_encode_timing timing{};UINT settings=0;int64_t total_start=0;
    ComPtr<ID3D11Query> disjoint,upload_start,upload_end,color_start,color_end;
    ComPtr<IMFDXGIDeviceManager> manager;ComPtr<ID3D11VideoDevice> video;ComPtr<ID3D11VideoContext> video_commands;
    ComPtr<ID3D11VideoProcessorEnumerator> enumerator;ComPtr<ID3D11VideoProcessor> processor;
    std::vector<uint8_t> encoded;std::string name="Microsoft software H264 MFT";
    encoder_context(UINT w,UINT h,UINT rate):width(w),height(h),fps(rate){}
    void stop_transform(){if(event_pump)event_pump->stop();shutdown(transform.Get());if(event_pump)event_pump->wait_released();event_pump.Reset();events.Reset();}
    ~encoder_context(){stop_transform();}
    bool profile_open=false,profile_pending=false;
    void resolve_gpu(){
        if(!profile_pending)return;
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT frequency{};UINT64 u0=0,u1=0,c0=0,c1=0;auto context=capture->graphics.commands.Get();
        // A single non-blocking query may flush submissions, but never waits/spins for GPU completion.
        HRESULT hr=context->GetData(disjoint.Get(),&frequency,sizeof(frequency),0);if(hr!=S_OK)return;
        if(frequency.Disjoint||!frequency.Frequency){profile_pending=false;return;}
        if(context->GetData(upload_start.Get(),&u0,sizeof(u0),0)!=S_OK||context->GetData(upload_end.Get(),&u1,sizeof(u1),0)!=S_OK||
            context->GetData(color_start.Get(),&c0,sizeof(c0),0)!=S_OK||context->GetData(color_end.Get(),&c1,sizeof(c1),0)!=S_OK)return;
        profile_pending=false;timing.gpu_timing_valid=1;timing.gpu_upload_ms=(u1-u0)*1000.0/frequency.Frequency;timing.gpu_color_ms=(c1-c0)*1000.0/frequency.Frequency;
    }
    void begin_timing(){
        timing={};timing.gpu_upload_ms=timing.gpu_color_ms=-1;timing.accepted_settings=settings;total_start=ticks();
        resolve_gpu();profile_open=disjoint&&!profile_pending;
        if(profile_open){capture->graphics.commands->Begin(disjoint.Get());capture->graphics.commands->End(upload_start.Get());}
    }
    void finish_timing(){if(!timing.gpu_timing_valid)resolve_gpu();timing.total_ms=elapsed_ms(total_start);}
    void initialize(capture_context* c,bool hw,UINT bitrate) {
        hardware=hw;capture=c;
        if(hw){
            if(!capture){synthetic=std::make_unique<capture_context>();synthetic->graphics.create();capture=synthetic.get();}
            MFT_REGISTER_TYPE_INFO input{MFMediaType_Video,MFVideoFormat_NV12},output{MFMediaType_Video,MFVideoFormat_H264};
            IMFActivate** activations=nullptr;UINT count=0;
            check(MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER,MFT_ENUM_FLAG_HARDWARE|MFT_ENUM_FLAG_SORTANDFILTER,&input,&output,&activations,&count),"enumerate hardware H264 MFT");
            struct activation_list{IMFActivate** p;UINT n;~activation_list(){for(UINT i=0;i<n;++i)p[i]->Release();CoTaskMemFree(p);}} list{activations,count};
            HRESULT last=MF_E_TOPO_CODEC_NOT_FOUND;
            // Try every enumerated GPU encoder. Initialization includes the exact capture D3D11 device manager.
            for(UINT i=0;i<count;++i){
                try {check(activations[i]->ActivateObject(IID_PPV_ARGS(&transform)),"activate hardware H264");configure(bitrate);
                    WCHAR* friendly=nullptr;UINT length=0;if(SUCCEEDED(activations[i]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute,&friendly,&length))){int bytes=WideCharToMultiByte(CP_UTF8,0,friendly,static_cast<int>(length),nullptr,0,nullptr,nullptr);name.resize(bytes);WideCharToMultiByte(CP_UTF8,0,friendly,static_cast<int>(length),name.data(),bytes,nullptr,nullptr);CoTaskMemFree(friendly);}else name="Hardware H264 MFT";return;}
                catch(failure e){last=e.hr;stop_transform();transform.Reset();manager.Reset();}
            }
            check(last,"no compatible D3D11 hardware H264 encoder");
        }else{check(CoCreateInstance(CLSID_CMSH264EncoderMFT,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&transform)),"software H264 encoder");configure(bitrate);}
    }
    void configure(UINT bitrate){
        settings=0;
        ComPtr<IMFAttributes> attributes;check(transform->GetAttributes(&attributes),"encoder attributes");
        UINT32 async=0;attributes->GetUINT32(MF_TRANSFORM_ASYNC,&async);
        if(async){check(attributes->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK,TRUE),"unlock async MFT");check(transform.As(&events),"encoder events");event_pump.Attach(new mft_events(events.Get()));}
        if(hardware){UINT32 aware=0;check(attributes->GetUINT32(MF_SA_D3D11_AWARE,&aware),"encoder D3D11 aware");if(!aware)check(MF_E_UNSUPPORTED_D3D_TYPE,"encoder lacks D3D11 texture support");
            UINT token=0;check(MFCreateDXGIDeviceManager(&token,&manager),"MFCreateDXGIDeviceManager");check(manager->ResetDevice(capture->graphics.device.Get(),token),"ResetDevice");check(transform->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,reinterpret_cast<ULONG_PTR>(manager.Get())),"encoder D3D manager");
            check(capture->graphics.device.As(&video),"ID3D11VideoDevice");check(capture->graphics.commands.As(&video_commands),"ID3D11VideoContext");
            D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc{};desc.InputFrameFormat=D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;desc.InputWidth=desc.OutputWidth=width;desc.InputHeight=desc.OutputHeight=height;desc.InputFrameRate=desc.OutputFrameRate={fps,1};desc.Usage=D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
            check(video->CreateVideoProcessorEnumerator(&desc,&enumerator),"video processor enumerator");check(video->CreateVideoProcessor(enumerator.Get(),0,&processor),"video processor BGRA to NV12");
            D3D11_QUERY_DESC query{D3D11_QUERY_TIMESTAMP_DISJOINT,0};check(capture->graphics.device->CreateQuery(&query,&disjoint),"encode disjoint query");query.Query=D3D11_QUERY_TIMESTAMP;
            for(auto target : {std::addressof(upload_start),std::addressof(upload_end),std::addressof(color_start),std::addressof(color_end)})check(capture->graphics.device->CreateQuery(&query,target->ReleaseAndGetAddressOf()),"encode timestamp query");
        }
        if(SUCCEEDED(codec_property(transform.Get(),CODECAPI_AVLowLatencyMode,1,true)))settings|=1; if(SUCCEEDED(codec_property(transform.Get(),CODECAPI_AVEncMPVDefaultBPictureCount,0)))settings|=2;
        if(SUCCEEDED(codec_property(transform.Get(),CODECAPI_AVEncCommonRateControlMode,eAVEncCommonRateControlMode_CBR)))settings|=4;codec_property(transform.Get(),CODECAPI_AVEncCommonMeanBitRate,bitrate);if(SUCCEEDED(codec_property(transform.Get(),CODECAPI_AVEncMPVGOPSize,fps)))settings|=8;
        auto output=video_type(MFVideoFormat_H264,width,height,fps);check(output->SetUINT32(MF_MT_AVG_BITRATE,bitrate),"bitrate");check(output->SetUINT32(MF_MT_MPEG2_PROFILE,eAVEncH264VProfile_Main),"H264 profile");check(transform->SetOutputType(0,output.Get(),0),"encoder output H264");
        auto input=video_type(MFVideoFormat_NV12,width,height,fps);check(input->SetUINT32(MF_MT_VIDEO_PRIMARIES,MFVideoPrimaries_BT709),"primaries");check(input->SetUINT32(MF_MT_YUV_MATRIX,MFVideoTransferMatrix_BT709),"matrix");check(input->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE,MFNominalRange_16_235),"range");check(transform->SetInputType(0,input.Get(),0),"encoder input NV12");if(event_pump)event_pump->start();start(transform.Get());
    }
    MediaEventType event_until(ULONGLONG deadline){return event_pump->next(deadline);}
    HRESULT encode(IMFSample* sample,int64_t time,bool force,const uint8_t** data,UINT* bytes,int* key,int64_t* output_time){
        lifetime.verify();*bytes=0;*data=nullptr;*key=0;
        if(force)codec_property(transform.Get(),CODECAPI_AVEncVideoForceKeyFrame,1);
        check(sample->SetSampleTime(time),"sample time");check(sample->SetSampleDuration(10000000/fps),"sample duration");
        ULONGLONG deadline=GetTickCount64()+2000;
        auto wait_start=ticks();
        if(events&&!input_ready){while(event_until(deadline)!=METransformNeedInput){} }input_ready=false;timing.input_wait_ms=elapsed_ms(wait_start);auto submit_start=ticks();
        check(transform->ProcessInput(0,sample,0),"encoder ProcessInput");timing.submit_ms=elapsed_ms(submit_start);auto output_wait_start=ticks();
        if(events){while(true){auto type=event_until(deadline);if(type==METransformNeedInput)input_ready=true;if(type==METransformHaveOutput)break;} }
        ComPtr<IMFSample> output;
        for(int attempt=0;attempt<2;++attempt){try{output=transform_output(transform.Get());break;}catch(failure e){
            if(e.hr==MF_E_TRANSFORM_STREAM_CHANGE){ComPtr<IMFMediaType> type;check(transform->GetOutputAvailableType(0,0,&type),"encoder changed output type");check(transform->SetOutputType(0,type.Get(),0),"encoder accept output change");
                if(events){while(true){auto event=event_until(deadline);if(event==METransformNeedInput)input_ready=true;if(event==METransformHaveOutput)break;}}continue;}
            if(e.hr==MF_E_TRANSFORM_NEED_MORE_INPUT)return S_FALSE;throw;}}
        timing.output_wait_ms=elapsed_ms(output_wait_start);if(!output)return S_FALSE;auto pack_start=ticks();
        ComPtr<IMFMediaBuffer> buffer;check(output->ConvertToContiguousBuffer(&buffer),"encoded contiguous buffer");BYTE* p=nullptr;DWORD n=0;check(buffer->Lock(&p,nullptr,&n),"encoded Lock");
        try{encoded.assign(p,p+n);}catch(...){buffer->Unlock();throw;}check(buffer->Unlock(),"encoded Unlock");UINT32 clean=0;output->GetUINT32(MFSampleExtension_CleanPoint,&clean);*key=clean!=0;check(output->GetSampleTime(output_time),"encoded timestamp");
        *data=encoded.data();*bytes=static_cast<UINT>(encoded.size());timing.pack_ms=elapsed_ms(pack_start);return S_OK;
    }
    ComPtr<IMFSample> gpu_sample(){
        if(!capture||!capture->texture)check(E_UNEXPECTED,"capture before GPU encode");
        auto setup_start=ticks();D3D11_TEXTURE2D_DESC source{};capture->texture->GetDesc(&source);if(source.Width!=width||source.Height!=height)check(DXGI_ERROR_ACCESS_LOST,"encoder dimensions changed");
        // New input surface per submitted sample: an asynchronous encoder may retain it after ProcessInput returns.
        D3D11_TEXTURE2D_DESC desc{};desc.Width=width;desc.Height=height;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_NV12;desc.SampleDesc.Count=1;desc.BindFlags=D3D11_BIND_RENDER_TARGET;
        ComPtr<ID3D11Texture2D> nv12;check(capture->graphics.device->CreateTexture2D(&desc,nullptr,&nv12),"NV12 input texture");
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC in{};in.ViewDimension=D3D11_VPIV_DIMENSION_TEXTURE2D;
        ComPtr<ID3D11VideoProcessorInputView> input;check(video->CreateVideoProcessorInputView(capture->texture.Get(),enumerator.Get(),&in,&input),"BGRA processor input");
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC out{};out.ViewDimension=D3D11_VPOV_DIMENSION_TEXTURE2D;
        ComPtr<ID3D11VideoProcessorOutputView> output;check(video->CreateVideoProcessorOutputView(nv12.Get(),enumerator.Get(),&out,&output),"NV12 processor output");
        RECT area{0,0,static_cast<LONG>(width),static_cast<LONG>(height)};video_commands->VideoProcessorSetStreamSourceRect(processor.Get(),0,TRUE,&area);video_commands->VideoProcessorSetStreamDestRect(processor.Get(),0,TRUE,&area);video_commands->VideoProcessorSetOutputTargetRect(processor.Get(),TRUE,&area);
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE rgb{};rgb.RGB_Range=0;rgb.YCbCr_Matrix=1;rgb.Nominal_Range=2;
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE yuv{};yuv.YCbCr_Matrix=1;yuv.Nominal_Range=1;
        video_commands->VideoProcessorSetStreamColorSpace(processor.Get(),0,&rgb);video_commands->VideoProcessorSetOutputColorSpace(processor.Get(),&yuv);
        D3D11_VIDEO_PROCESSOR_STREAM stream{};stream.Enable=TRUE;stream.pInputSurface=input.Get();timing.surface_setup_ms=elapsed_ms(setup_start);auto color_submit_start=ticks();if(profile_open)capture->graphics.commands->End(color_start.Get());check(video_commands->VideoProcessorBlt(processor.Get(),output.Get(),0,1,&stream),"GPU BGRA -> NV12");if(profile_open){capture->graphics.commands->End(color_end.Get());capture->graphics.commands->End(disjoint.Get());profile_open=false;profile_pending=true;}timing.color_submit_ms=elapsed_ms(color_submit_start);
        ComPtr<IMFMediaBuffer> buffer;check(MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D),nv12.Get(),0,FALSE,&buffer),"MFCreateDXGISurfaceBuffer");ComPtr<IMFSample> sample;check(MFCreateSample(&sample),"GPU sample");check(sample->AddBuffer(buffer.Get()),"GPU sample buffer");return sample;
    }
};
struct decoder_context {
    resource_counter tracked{2};
    rd_decode_timing timing{};
    runtime lifetime;UINT width,height,coded_width=0,coded_height=0;LONG stride=0;bool hardware;ComPtr<IMFTransform> transform;gpu graphics;
    ComPtr<IMFDXGIDeviceManager> manager;ComPtr<ID3D11VideoDevice> video;ComPtr<ID3D11VideoContext> video_commands;
    ComPtr<ID3D11VideoProcessorEnumerator> enumerator;ComPtr<ID3D11VideoProcessor> processor;
    ComPtr<ID3D11Texture2D> converted,staging;
    decoder_context(UINT w,UINT h,bool gpu_decode):width(w),height(h),hardware(gpu_decode){
        check(CoCreateInstance(CLSID_CMSH264DecoderMFT,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&transform)),"H264 decoder");
        if(hardware){graphics.create();ComPtr<IMFAttributes> attributes;check(transform->GetAttributes(&attributes),"decoder attributes");UINT32 aware=0;check(attributes->GetUINT32(MF_SA_D3D11_AWARE,&aware),"decoder D3D11 aware");if(!aware)check(MF_E_UNSUPPORTED_D3D_TYPE,"decoder lacks D3D11 support");
            UINT token=0;check(MFCreateDXGIDeviceManager(&token,&manager),"decoder device manager");check(manager->ResetDevice(graphics.device.Get(),token),"decoder ResetDevice");check(transform->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,reinterpret_cast<ULONG_PTR>(manager.Get())),"decoder D3D manager");}
        // Inbox H264 decoder uses VT_UI4, unlike the encoder's VT_BOOL (documented exception).
        codec_property(transform.Get(),CODECAPI_AVLowLatencyMode,1);
        auto input=video_type(MFVideoFormat_H264,w,h,30);check(transform->SetInputType(0,input.Get(),0),"decoder input H264");select_output();start(transform.Get());
    }
    ~decoder_context(){shutdown(transform.Get());}
    void gpu_pixels(IMFMediaBuffer* buffer,uint8_t* bgra){
        auto conversion_start=ticks();
        ComPtr<IMFDXGIBuffer> surface;check(buffer->QueryInterface(IID_PPV_ARGS(&surface)),"GPU decode must return DXGI texture");
        ComPtr<ID3D11Texture2D> texture;check(surface->GetResource(IID_PPV_ARGS(&texture)),"decoder texture");UINT subresource=0;check(surface->GetSubresourceIndex(&subresource),"decoder subresource");
        if(!processor){check(graphics.device.As(&video),"decoder video device");check(graphics.commands.As(&video_commands),"decoder video commands");
            D3D11_TEXTURE2D_DESC source{};texture->GetDesc(&source);D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};content.InputFrameFormat=D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;content.InputWidth=source.Width;content.InputHeight=source.Height;content.OutputWidth=width;content.OutputHeight=height;content.InputFrameRate=content.OutputFrameRate={30,1};content.Usage=D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
            check(video->CreateVideoProcessorEnumerator(&content,&enumerator),"decoder processor enumerator");check(video->CreateVideoProcessor(enumerator.Get(),0,&processor),"decoder NV12 -> BGRA processor");
            D3D11_TEXTURE2D_DESC desc{};desc.Width=width;desc.Height=height;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.BindFlags=D3D11_BIND_RENDER_TARGET;check(graphics.device->CreateTexture2D(&desc,nullptr,&converted),"decoder BGRA texture");desc.BindFlags=0;desc.Usage=D3D11_USAGE_STAGING;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;check(graphics.device->CreateTexture2D(&desc,nullptr,&staging),"WPF readback staging");
        }
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC in{};in.ViewDimension=D3D11_VPIV_DIMENSION_TEXTURE2D;in.Texture2D.ArraySlice=subresource;
        ComPtr<ID3D11VideoProcessorInputView> input;check(video->CreateVideoProcessorInputView(texture.Get(),enumerator.Get(),&in,&input),"decoder processor input");
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC out{};out.ViewDimension=D3D11_VPOV_DIMENSION_TEXTURE2D;ComPtr<ID3D11VideoProcessorOutputView> output;check(video->CreateVideoProcessorOutputView(converted.Get(),enumerator.Get(),&out,&output),"decoder processor output");
        RECT area{0,0,static_cast<LONG>(width),static_cast<LONG>(height)};video_commands->VideoProcessorSetStreamSourceRect(processor.Get(),0,TRUE,&area);video_commands->VideoProcessorSetStreamDestRect(processor.Get(),0,TRUE,&area);video_commands->VideoProcessorSetOutputTargetRect(processor.Get(),TRUE,&area);
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE yuv{};yuv.YCbCr_Matrix=1;yuv.Nominal_Range=1;D3D11_VIDEO_PROCESSOR_COLOR_SPACE rgb{};rgb.YCbCr_Matrix=1;rgb.Nominal_Range=2;video_commands->VideoProcessorSetStreamColorSpace(processor.Get(),0,&yuv);video_commands->VideoProcessorSetOutputColorSpace(processor.Get(),&rgb);
        D3D11_VIDEO_PROCESSOR_STREAM stream{};stream.Enable=TRUE;stream.pInputSurface=input.Get();check(video_commands->VideoProcessorBlt(processor.Get(),output.Get(),0,1,&stream),"GPU NV12 -> BGRA");timing.conversion_ms=elapsed_ms(conversion_start);auto readback_start=ticks();graphics.commands->CopyResource(staging.Get(),converted.Get());D3D11_MAPPED_SUBRESOURCE mapped{};check(graphics.commands->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped),"WPF readback Map");
        for(UINT y=0;y<height;++y)memcpy(bgra+static_cast<size_t>(y)*width*4,static_cast<uint8_t*>(mapped.pData)+y*mapped.RowPitch,width*4);graphics.commands->Unmap(staging.Get(),0);timing.readback_ms=elapsed_ms(readback_start);timing.readback_bytes=static_cast<uint64_t>(width)*height*4;
    }
    void select_output(){for(DWORD i=0;;++i){ComPtr<IMFMediaType> type;check(transform->GetOutputAvailableType(0,i,&type),"decoder output enumeration");GUID subtype;check(type->GetGUID(MF_MT_SUBTYPE,&subtype),"decoder subtype");if(subtype!=MFVideoFormat_NV12)continue;UINT w=0,h=0;check(MFGetAttributeSize(type.Get(),MF_MT_FRAME_SIZE,&w,&h),"decoder dimensions");
        // H264 macroblock padding (e.g. 1080 displayed in a 1088-line NV12 surface) is not a resolution change.
        if(w<width||h<height||w-width>31||h-height>31)check(MF_E_INVALIDMEDIATYPE,"decoder resolution mismatch");
        MFVideoArea aperture{};UINT size=0;HRESULT area=type->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE,reinterpret_cast<UINT8*>(&aperture),sizeof(aperture),&size);
        if(SUCCEEDED(area)&&(aperture.OffsetX.value||aperture.OffsetY.value||aperture.Area.cx!=static_cast<LONG>(width)||aperture.Area.cy!=static_cast<LONG>(height)))check(MF_E_INVALIDMEDIATYPE,"unsupported decoder display aperture");
        coded_width=w;coded_height=h;UINT32 pitch=w;type->GetUINT32(MF_MT_DEFAULT_STRIDE,&pitch);stride=static_cast<LONG>(pitch);check(transform->SetOutputType(0,type.Get(),0),"decoder output NV12");return;}}
    ComPtr<IMFSample> decode_sample(const uint8_t* data,UINT bytes,int64_t time){
        lifetime.verify();timing={};auto submit_start=ticks();auto input=sample_buffer(bytes);ComPtr<IMFMediaBuffer> buffer;check(input->GetBufferByIndex(0,&buffer),"input buffer");BYTE* p;check(buffer->Lock(&p,nullptr,nullptr),"input Lock");memcpy(p,data,bytes);buffer->Unlock();check(buffer->SetCurrentLength(bytes),"input length");check(input->SetSampleTime(time),"decode time");check(input->SetSampleDuration(333333),"decode duration");check(transform->ProcessInput(0,input.Get(),0),"decoder ProcessInput");timing.submit_ms=elapsed_ms(submit_start);auto output_start=ticks();
        ComPtr<IMFSample> output;for(int attempt=0;attempt<2;++attempt){try{output=transform_output(transform.Get());break;}catch(failure e){if(e.hr==MF_E_TRANSFORM_STREAM_CHANGE){select_output();continue;}if(e.hr==MF_E_TRANSFORM_NEED_MORE_INPUT)return nullptr;throw;}}
        timing.output_ms=elapsed_ms(output_start);return output;
    }
    HRESULT decode(const uint8_t* data,UINT bytes,int64_t time,uint8_t* bgra,UINT capacity){
        if(capacity!=static_cast<uint64_t>(width)*height*4)check(E_INVALIDARG,"decoder capacity");auto total_start=ticks();
        auto output=decode_sample(data,bytes,time);
        if(!output)return S_FALSE;BYTE* p=nullptr;ComPtr<IMFMediaBuffer> pixels;
        if(hardware){check(output->GetBufferByIndex(0,&pixels),"GPU decoder buffer");gpu_pixels(pixels.Get(),bgra);timing.total_ms=elapsed_ms(total_start);return S_OK;}
        check(output->ConvertToContiguousBuffer(&pixels),"decoder contiguous buffer");
        ComPtr<IMF2DBuffer> plane;if(SUCCEEDED(pixels.As(&plane))){LONG pitch;check(plane->Lock2D(&p,&pitch),"Lock2D");try{nv12_bgra(p,pitch,bgra,width,height,coded_height);}catch(...){plane->Unlock2D();throw;}plane->Unlock2D();}
        else {DWORD length=0;check(pixels->Lock(&p,nullptr,&length),"decoder Lock");if(stride<0||length<static_cast<uint64_t>(stride)*coded_height*3/2){pixels->Unlock();check(E_FAIL,"short NV12 output");}nv12_bgra(p,stride,bgra,width,height,coded_height);pixels->Unlock();}timing.total_ms=elapsed_ms(total_start);return S_OK;
    }
    HRESULT decode_gpu(const uint8_t* data,UINT bytes,int64_t time,void** frame){
        if(!hardware)check(MF_E_UNSUPPORTED_D3D_TYPE,"GPU surface decode requires D3D11 decoder");auto total_start=ticks();auto sample=decode_sample(data,bytes,time);if(!sample)return S_FALSE;
        auto result=std::make_unique<gpu_frame>();result->sample=sample;result->width=width;result->height=height;
        ComPtr<IMFMediaBuffer> buffer;check(sample->GetBufferByIndex(0,&buffer),"GPU sample buffer");ComPtr<IMFDXGIBuffer> surface;check(buffer.As(&surface),"GPU output must be DXGI surface");
        check(surface->GetResource(IID_PPV_ARGS(&result->texture)),"GPU frame texture");check(surface->GetSubresourceIndex(&result->subresource),"GPU frame subresource");timing.total_ms=elapsed_ms(total_start);*frame=result.release();return S_OK;
    }

};
}
RD int32_t rd_encoder_set_bitrate(void* encoder, uint32_t bitrate) {
    if (!encoder || bitrate < 100000 || bitrate > 100000000) return E_INVALIDARG;
    auto* context = static_cast<encoder_context*>(encoder);
    return codec_property(context->transform.Get(), CODECAPI_AVEncCommonMeanBitRate, bitrate);
}
RD int32_t rd_encoder_create(void* capture,uint32_t width,uint32_t height,uint32_t fps,uint32_t bitrate,int hardware,void** encoder){if(!encoder||!width||!height||(width&1)||(height&1)||width>7680||height>4320||fps<1||fps>144||bitrate<100000||bitrate>100000000)return E_INVALIDARG;*encoder=nullptr;return guarded([&]{auto e=std::make_unique<encoder_context>(width,height,fps);e->initialize(static_cast<capture_context*>(capture),hardware!=0,bitrate);*encoder=e.release();});}
RD int32_t rd_encoder_frame(void* encoder,int64_t time,int force,const uint8_t** data,uint32_t* bytes,int* key,int64_t* output_time){if(!encoder||!data||!bytes||!key||!output_time)return E_INVALIDARG;HRESULT result=S_OK;HRESULT hr=guarded([&]{auto& e=*static_cast<encoder_context*>(encoder);e.lifetime.verify();if(!e.hardware||!e.capture)check(E_INVALIDARG,"GPU encode requires hardware capture context");e.begin_timing();if(e.profile_open)e.capture->graphics.commands->End(e.upload_end.Get());auto sample=e.gpu_sample();result=e.encode(sample.Get(),time,force!=0,data,bytes,key,output_time);e.finish_timing();});return FAILED(hr)?hr:result;}
RD int32_t rd_encoder_bgra(void* encoder,const uint8_t* data,uint32_t bytes,int64_t time,int force,const uint8_t** output,uint32_t* length,int* key,int64_t* output_time){if(!encoder||!data||!output||!length||!key||!output_time)return E_INVALIDARG;HRESULT result=S_OK;HRESULT hr=guarded([&]{auto& e=*static_cast<encoder_context*>(encoder);e.lifetime.verify();if(bytes!=static_cast<uint64_t>(e.width)*e.height*4)check(E_INVALIDARG,"BGRA encoder input");e.begin_timing();
    if(e.hardware){if(!e.synthetic)check(E_INVALIDARG,"BGRA upload only allowed for explicit synthetic test encoder");if(!e.capture->texture){D3D11_TEXTURE2D_DESC desc{};desc.Width=e.width;desc.Height=e.height;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.BindFlags=D3D11_BIND_RENDER_TARGET;check(e.capture->graphics.device->CreateTexture2D(&desc,nullptr,&e.capture->texture),"synthetic BGRA texture");}auto upload_start=ticks();e.capture->graphics.commands->UpdateSubresource(e.capture->texture.Get(),0,nullptr,data,e.width*4,0);e.timing.upload_submit_ms=elapsed_ms(upload_start);e.timing.upload_bytes=bytes;if(e.profile_open)e.capture->graphics.commands->End(e.upload_end.Get());auto sample=e.gpu_sample();result=e.encode(sample.Get(),time,force!=0,output,length,key,output_time);e.finish_timing();return;}
    auto sample=sample_buffer(e.width*e.height*3/2);ComPtr<IMFMediaBuffer> buffer;check(sample->GetBufferByIndex(0,&buffer),"NV12 buffer");BYTE* p;check(buffer->Lock(&p,nullptr,nullptr),"NV12 Lock");auto color_start=ticks();bgra_nv12(data,p,e.width,e.height);e.timing.color_submit_ms=elapsed_ms(color_start);buffer->Unlock();check(buffer->SetCurrentLength(e.width*e.height*3/2),"NV12 length");result=e.encode(sample.Get(),time,force!=0,output,length,key,output_time);e.finish_timing();});return FAILED(hr)?hr:result;}
RD void rd_encoder_destroy(void* encoder){delete static_cast<encoder_context*>(encoder);}
RD const char* rd_encoder_name(void* encoder){return encoder?static_cast<encoder_context*>(encoder)->name.c_str():"none";}
RD int32_t rd_decoder_create(uint32_t w,uint32_t h,int hardware,void** decoder){if(!decoder||!w||!h||(w&1)||(h&1)||static_cast<uint64_t>(w)*h>33177600)return E_INVALIDARG;*decoder=nullptr;return guarded([&]{*decoder=new decoder_context(w,h,hardware!=0);});}
RD int32_t rd_decoder_frame(void* decoder,const uint8_t* data,uint32_t bytes,int64_t time,uint8_t* bgra,uint32_t capacity){if(!decoder||!data||!bgra||!bytes||bytes>32*1024*1024)return E_INVALIDARG;HRESULT result=S_OK;HRESULT hr=guarded([&]{result=static_cast<decoder_context*>(decoder)->decode(data,bytes,time,bgra,capacity);});return FAILED(hr)?hr:result;}
RD void rd_decoder_destroy(void* decoder){delete static_cast<decoder_context*>(decoder);}

RD int32_t rd_decoder_gpu_frame(void* decoder,const uint8_t* data,uint32_t bytes,int64_t time,void** frame){if(!decoder||!data||!frame||!bytes||bytes>32*1024*1024)return E_INVALIDARG;*frame=nullptr;HRESULT result=S_OK;HRESULT hr=guarded([&]{result=static_cast<decoder_context*>(decoder)->decode_gpu(data,bytes,time,frame);});return FAILED(hr)?hr:result;}
RD int32_t rd_decoder_timing(void* decoder,rd_decode_timing* timing){if(!decoder||!timing)return E_INVALIDARG;return guarded([&]{auto& d=*static_cast<decoder_context*>(decoder);d.lifetime.verify();*timing=d.timing;});}
RD void rd_gpu_frame_destroy(void* frame){delete static_cast<gpu_frame*>(frame);}

RD int32_t rd_encoder_timing(void* encoder,rd_encode_timing* timing){if(!encoder||!timing)return E_INVALIDARG;return guarded([&]{auto& e=*static_cast<encoder_context*>(encoder);e.lifetime.verify();*timing=e.timing;});}
