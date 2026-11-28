#include "internal.h"
#include <codecapi.h>
#include <wmcodecdsp.h>
#include <d3dcompiler.h>
#include <algorithm>
#include <cmath>
#include <sstream>
#include <iomanip>
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
uint8_t clamp_round(float v){return static_cast<uint8_t>(std::clamp(static_cast<int>(std::lround(v)),0,255));}

// Baseline CPU conversions preserved for deterministic A/B testing in rd_color_convert_nv12_roundtrip.
void bgra_nv12_legacy(const uint8_t* src,uint8_t* dst,UINT w,UINT h) {
    for(UINT y=0;y<h;++y)for(UINT x=0;x<w;++x){auto p=src+(static_cast<size_t>(y)*w+x)*4;dst[y*w+x]=clamp(((47*p[2]+157*p[1]+16*p[0]+128)>>8)+16);}
    auto uv=dst+static_cast<size_t>(w)*h;
    for(UINT y=0;y<h;y+=2)for(UINT x=0;x<w;x+=2){int r=0,g=0,b=0;for(UINT dy=0;dy<2;++dy)for(UINT dx=0;dx<2;++dx){auto p=src+((static_cast<size_t>(y+dy))*w+x+dx)*4;r+=p[2];g+=p[1];b+=p[0];}r/=4;g/=4;b/=4;uv[(y/2)*w+x]=clamp(((-26*r-87*g+112*b+128)>>8)+128);uv[(y/2)*w+x+1]=clamp(((112*r-102*g-10*b+128)>>8)+128);}
}
void nv12_bgra_legacy(const uint8_t* src,LONG stride,uint8_t* dst,UINT w,UINT h,UINT coded_height) {
    if(stride<static_cast<LONG>(w))check(MF_E_INVALIDMEDIATYPE,"unsupported NV12 stride");auto uv=src+static_cast<size_t>(stride)*coded_height;
    for(UINT y=0;y<h;++y)for(UINT x=0;x<w;++x){int c=src[y*stride+x]-16,d=uv[(y/2)*stride+(x&~1u)]-128,e=uv[(y/2)*stride+(x&~1u)+1]-128;auto p=dst+(static_cast<size_t>(y)*w+x)*4;p[2]=clamp((298*c+459*e+128)>>8);p[1]=clamp((298*c-55*d-136*e+128)>>8);p[0]=clamp((298*c+541*d+128)>>8);p[3]=255;}
}

// Explicit CPU fallback: limited-range BT.709 4:2:0 with 5-tap horizontal ClearType dipole cancellation
// and foreground saturation weighting so neutral strokes stay neutral and 1px colored text stays vivid.
void bgra_nv12(const uint8_t* src,uint8_t* dst,UINT w,UINT h) {
    for(UINT y=0;y<h;++y)for(UINT x=0;x<w;++x){
        auto p=src+(static_cast<size_t>(y)*w+x)*4;
        float y0=0.2126f*p[2]+0.7152f*p[1]+0.0722f*p[0];
        dst[y*w+x]=clamp_round(16.0f+(219.0f/255.0f)*y0);
    }
    auto uv=dst+static_cast<size_t>(w)*h;
    const int iw=static_cast<int>(w),ih=static_cast<int>(h);
    for(int y=0;y<ih;y+=2){
        for(int x=0;x<iw;x+=2){
            float u_sum=0.0f,v_sum=0.0f,w_sum=0.0f;
            for(int dy=0;dy<2;++dy){
                int py=std::min(y+dy,ih-1);
                float cb_row[6],cr_row[6],mag_row[6];
                for(int k=0;k<6;++k){
                    int sx=std::clamp(x-2+k,0,iw-1);
                    auto p=src+(static_cast<size_t>(py)*w+sx)*4;
                    float y0=0.2126f*p[2]+0.7152f*p[1]+0.0722f*p[0];
                    float cb=(p[0]-y0)*(1.0f/(1.8556f*255.0f));
                    float cr=(p[2]-y0)*(1.0f/(1.5748f*255.0f));
                    cb_row[k]=cb;cr_row[k]=cr;
                    mag_row[k]=std::sqrt(cb*cb+cr*cr);
                }
                for(int dx=0;dx<2;++dx){
                    float net_cb=cb_row[dx]+cb_row[dx+1]+cb_row[dx+2]+cb_row[dx+3]+cb_row[dx+4];
                    float net_cr=cr_row[dx]+cr_row[dx+1]+cr_row[dx+2]+cr_row[dx+3]+cr_row[dx+4];
                    float abs5=mag_row[dx]+mag_row[dx+1]+mag_row[dx+2]+mag_row[dx+3]+mag_row[dx+4];
                    float coh=abs5>(3.0f/255.0f)?std::sqrt(net_cb*net_cb+net_cr*net_cr)/abs5:1.0f;
                    float keep=std::clamp((coh-0.28f)*(1.0f/(0.62f-0.28f)),0.0f,1.0f);
                    float cb=cb_row[2+dx]*keep,cr=cr_row[2+dx]*keep;
                    float mag=mag_row[2+dx]*keep;
                    float weight=1.0f+keep*(mag*20.0f);
                    u_sum+=(128.0f+224.0f*cb)*weight;
                    v_sum+=(128.0f+224.0f*cr)*weight;
                    w_sum+=weight;
                }
            }
            uv[(y/2)*w+x]=clamp_round(u_sum/w_sum);
            uv[(y/2)*w+x+1]=clamp_round(v_sum/w_sum);
        }
    }
}

// CPU NV12 -> BGRA with Luma-Guided Bilateral Chroma Reconstruction.
void nv12_bgra(const uint8_t* src,LONG stride,uint8_t* dst,UINT w,UINT h,UINT coded_height) {
    if(stride<static_cast<LONG>(w))check(MF_E_INVALIDMEDIATYPE,"unsupported NV12 stride");
    auto uv=src+static_cast<size_t>(stride)*coded_height;
    const int uv_w=static_cast<int>(w/2),uv_h=static_cast<int>(h/2);
    std::vector<float> y_block(static_cast<size_t>(uv_w)*uv_h);
    for(int cy=0;cy<uv_h;++cy){
        const uint8_t* r0=src+static_cast<size_t>(cy*2)*stride;
        const uint8_t* r1=src+static_cast<size_t>(cy*2+1)*stride;
        for(int cx=0;cx<uv_w;++cx){
            y_block[static_cast<size_t>(cy)*uv_w+cx]=0.25f*(r0[cx*2]+r0[cx*2+1]+r1[cx*2]+r1[cx*2+1]);
        }
    }
    for(int y=0;y<static_cast<int>(h);++y){
        float uy=(y+0.5f)*0.5f-0.5f;
        int cy0=static_cast<int>(std::floor(uy));
        float fy=uy-cy0;
        int py0=std::clamp(cy0,0,uv_h-1),py1=std::clamp(cy0+1,0,uv_h-1);
        for(int x=0;x<static_cast<int>(w);++x){
            float cur_y=static_cast<float>(src[static_cast<size_t>(y)*stride+x]);
            float ux=(x+0.5f)*0.5f-0.5f;
            int cx0=static_cast<int>(std::floor(ux));
            float fx=ux-cx0;
            int px0=std::clamp(cx0,0,uv_w-1),px1=std::clamp(cx0+1,0,uv_w-1);
            const int pts_x[4]={px0,px1,px0,px1};
            const int pts_y[4]={py0,py0,py1,py1};
            const float sw[4]={(1.0f-fx)*(1.0f-fy),fx*(1.0f-fy),(1.0f-fx)*fy,fx*fy};
            float u_sum=0.0f,v_sum=0.0f,w_sum=0.0f;
            for(int k=0;k<4;++k){
                float yb=y_block[static_cast<size_t>(pts_y[k])*uv_w+pts_x[k]];
                float dy=cur_y-yb;
                float rw=1.0f/(1.0f+(dy*dy)*(1.0f/(14.0f*14.0f)));
                float wk=sw[k]*rw+1e-5f;
                const uint8_t* uvp=uv+static_cast<size_t>(pts_y[k])*stride+pts_x[k]*2;
                u_sum+=uvp[0]*wk;
                v_sum+=uvp[1]*wk;
                w_sum+=wk;
            }
            float y_lin=(cur_y-16.0f)*(255.0f/219.0f);
            float cb=(u_sum/w_sum-128.0f)*(255.0f/224.0f);
            float cr=(v_sum/w_sum-128.0f)*(255.0f/224.0f);
            auto p=dst+(static_cast<size_t>(y)*w+x)*4;
            p[2]=clamp_round(y_lin+1.5748f*cr);
            p[1]=clamp_round(y_lin-0.187324f*cb-0.468124f*cr);
            p[0]=clamp_round(y_lin+1.8556f*cb);
            p[3]=255;
        }
    }
}

const char* codec_color_shader=R"(
Texture2D<float4> srcBgra : register(t0);
Texture2D<float>  texY    : register(t0);
Texture2D<float2> texUV   : register(t1);

cbuffer CodecColorParams : register(b0) {
    int2 srcSize;
    int2 pad0;
};

float4 vs(uint id : SV_VertexID) : SV_Position {
    float2 p = id == 0 ? float2(-1.0, -1.0) : id == 1 ? float2(-1.0, 3.0) : float2(3.0, -1.0);
    return float4(p, 0.0, 1.0);
}

float ps_encode_y(float4 pos : SV_Position) : SV_Target {
    int2 p = clamp(int2(pos.xy), int2(0, 0), srcSize - 1);
    float3 rgb = srcBgra.Load(int3(p, 0)).rgb;
    float y0 = dot(rgb, float3(0.2126, 0.7152, 0.0722));
    return (16.0 + 219.0 * y0) / 255.0;
}

float2 ps_encode_uv(float4 pos : SV_Position) : SV_Target {
    int2 baseP = int2(pos.xy) * 2;
    float2 uSum = 0.0;
    float wSum = 0.0;
    [unroll]
    for (int dy = 0; dy < 2; ++dy) {
        int py = min(baseP.y + dy, srcSize.y - 1);
        float2 uvRow[6];
        float magRow[6];
        [unroll]
        for (int k = 0; k < 6; ++k) {
            int sx = clamp(baseP.x - 2 + k, 0, srcSize.x - 1);
            float3 c = srcBgra.Load(int3(sx, py, 0)).rgb;
            float y0 = dot(c, float3(0.2126, 0.7152, 0.0722));
            float2 uv = float2((c.b - y0) / 1.8556, (c.r - y0) / 1.5748);
            uvRow[k] = uv;
            magRow[k] = length(uv);
        }
        [unroll]
        for (int dx = 0; dx < 2; ++dx) {
            float2 net5 = uvRow[dx] + uvRow[dx+1] + uvRow[dx+2] + uvRow[dx+3] + uvRow[dx+4];
            float abs5 = magRow[dx] + magRow[dx+1] + magRow[dx+2] + magRow[dx+3] + magRow[dx+4];
            float coh = abs5 > (3.0 / 255.0) ? length(net5) / abs5 : 1.0;
            float keep = saturate((coh - 0.28) / (0.62 - 0.28));
            float2 uv = uvRow[2 + dx] * keep;
            float w = 1.0 + keep * (magRow[2 + dx] * keep * 20.0);
            uSum += (float2(128.0, 128.0) + 224.0 * uv) * w;
            wSum += w;
        }
    }
    return (uSum / wSum) / 255.0;
}

float4 ps_decode_bgra(float4 pos : SV_Position) : SV_Target {
    int2 sp = clamp(int2(pos.xy), int2(0, 0), srcSize - 1);
    float curY = texY.Load(int3(sp, 0)) * 255.0;
    float2 uvCoord = (float2(sp) + 0.5) * 0.5 - 0.5;
    int2 c0 = int2(floor(uvCoord));
    float2 f = uvCoord - float2(c0);
    int2 uvMax = (srcSize / 2) - 1;
    int2 pts[4] = {
        clamp(c0, int2(0, 0), uvMax),
        clamp(c0 + int2(1, 0), int2(0, 0), uvMax),
        clamp(c0 + int2(0, 1), int2(0, 0), uvMax),
        clamp(c0 + int2(1, 1), int2(0, 0), uvMax)
    };
    float sw[4] = { (1.0 - f.x) * (1.0 - f.y), f.x * (1.0 - f.y), (1.0 - f.x) * f.y, f.x * f.y };
    float2 uvSum = 0.0;
    float wSum = 0.0;
    [unroll]
    for (int k = 0; k < 4; ++k) {
        int2 yb = pts[k] * 2;
        float yBlock = 0.25 * (
            texY.Load(int3(yb, 0)) +
            texY.Load(int3(yb + int2(1, 0), 0)) +
            texY.Load(int3(yb + int2(0, 1), 0)) +
            texY.Load(int3(yb + int2(1, 1), 0))) * 255.0;
        float dy = curY - yBlock;
        float rw = 1.0 / (1.0 + (dy * dy) / (14.0 * 14.0));
        float w = sw[k] * rw + 1e-5;
        uvSum += texUV.Load(int3(pts[k], 0)) * 255.0 * w;
        wSum += w;
    }
    float2 uv = uvSum / wSum;
    float yLin = (curY - 16.0) * (1.0 / 219.0);
    float2 cbcr = (uv - 128.0) * (1.0 / 224.0);
    float3 rgb;
    rgb.r = yLin + 1.5748 * cbcr.y;
    rgb.g = yLin - 0.187324 * cbcr.x - 0.468124 * cbcr.y;
    rgb.b = yLin + 1.8556 * cbcr.x;
    return float4(saturate(rgb), 1.0);
}
)";

struct encoder_context {
    resource_counter tracked{1};
    runtime lifetime;capture_context* capture=nullptr;std::unique_ptr<capture_context> synthetic;UINT width,height,fps;bool hardware=false,input_ready=false,hlsl_color=false;
    ComPtr<IMFTransform> transform;ComPtr<IMFMediaEventGenerator> events;ComPtr<mft_events> event_pump;
    rd_encode_timing timing{};UINT settings=0;int64_t total_start=0;
    ComPtr<ID3D11Query> disjoint,upload_start,upload_end,color_start,color_end;
    ComPtr<IMFDXGIDeviceManager> manager;ComPtr<ID3D11VideoDevice> video;ComPtr<ID3D11VideoContext> video_commands;
    ComPtr<ID3D11VideoProcessorEnumerator> enumerator;ComPtr<ID3D11VideoProcessor> processor;
    ComPtr<ID3D11VertexShader> color_vs;ComPtr<ID3D11PixelShader> encode_y_ps,encode_uv_ps;
    ComPtr<ID3D11Buffer> color_cb;ComPtr<ID3D11RasterizerState> color_rs;
    ComPtr<ID3D11Texture2D> readable_bgra;ComPtr<ID3D11ShaderResourceView> cached_bgra_srv;ID3D11Texture2D* cached_bgra_tex=nullptr;
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
    void init_hlsl_color(){
        hlsl_color=false;
        auto* dev=capture->graphics.device.Get();
        D3D11_TEXTURE2D_DESC probe{};probe.Width=width;probe.Height=height;probe.MipLevels=probe.ArraySize=1;probe.Format=DXGI_FORMAT_NV12;probe.SampleDesc.Count=1;probe.BindFlags=D3D11_BIND_RENDER_TARGET;
        ComPtr<ID3D11Texture2D> test_nv12;
        if(FAILED(dev->CreateTexture2D(&probe,nullptr,&test_nv12)))return;
        D3D11_RENDER_TARGET_VIEW_DESC ry{},ruv{};
        ry.Format=DXGI_FORMAT_R8_UNORM;ry.ViewDimension=D3D11_RTV_DIMENSION_TEXTURE2D;
        ruv.Format=DXGI_FORMAT_R8G8_UNORM;ruv.ViewDimension=D3D11_RTV_DIMENSION_TEXTURE2D;
        ComPtr<ID3D11RenderTargetView> ty,tuv;
        if(FAILED(dev->CreateRenderTargetView(test_nv12.Get(),&ry,&ty))||FAILED(dev->CreateRenderTargetView(test_nv12.Get(),&ruv,&tuv)))return;
        ComPtr<ID3DBlob> vs,psy,psuv,err;
        if(FAILED(D3DCompile(codec_color_shader,strlen(codec_color_shader),nullptr,nullptr,nullptr,"vs","vs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&vs,&err)))return;
        if(FAILED(D3DCompile(codec_color_shader,strlen(codec_color_shader),nullptr,nullptr,nullptr,"ps_encode_y","ps_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&psy,&err)))return;
        if(FAILED(D3DCompile(codec_color_shader,strlen(codec_color_shader),nullptr,nullptr,nullptr,"ps_encode_uv","ps_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&psuv,&err)))return;
        if(FAILED(dev->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&color_vs)))return;
        if(FAILED(dev->CreatePixelShader(psy->GetBufferPointer(),psy->GetBufferSize(),nullptr,&encode_y_ps)))return;
        if(FAILED(dev->CreatePixelShader(psuv->GetBufferPointer(),psuv->GetBufferSize(),nullptr,&encode_uv_ps)))return;
        int cb_data[4]={static_cast<int>(width),static_cast<int>(height),0,0};
        D3D11_BUFFER_DESC bd{};bd.ByteWidth=16;bd.Usage=D3D11_USAGE_IMMUTABLE;bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        D3D11_SUBRESOURCE_DATA init{cb_data,0,0};
        if(FAILED(dev->CreateBuffer(&bd,&init,&color_cb)))return;
        D3D11_RASTERIZER_DESC rs{};rs.FillMode=D3D11_FILL_SOLID;rs.CullMode=D3D11_CULL_NONE;rs.DepthClipEnable=TRUE;
        if(FAILED(dev->CreateRasterizerState(&rs,&color_rs)))return;
        hlsl_color=true;
    }
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
            init_hlsl_color();
            D3D11_QUERY_DESC query{D3D11_QUERY_TIMESTAMP_DISJOINT,0};check(capture->graphics.device->CreateQuery(&query,&disjoint),"encode disjoint query");query.Query=D3D11_QUERY_TIMESTAMP;
            for(auto target : {std::addressof(upload_start),std::addressof(upload_end),std::addressof(color_start),std::addressof(color_end)})check(capture->graphics.device->CreateQuery(&query,target->ReleaseAndGetAddressOf()),"encode timestamp query");
        }
        if(SUCCEEDED(codec_property(transform.Get(),CODECAPI_AVLowLatencyMode,1,true)))settings|=1; if(SUCCEEDED(codec_property(transform.Get(),CODECAPI_AVEncMPVDefaultBPictureCount,0)))settings|=2;
        if(SUCCEEDED(codec_property(transform.Get(),CODECAPI_AVEncCommonRateControlMode,eAVEncCommonRateControlMode_CBR)))settings|=4;codec_property(transform.Get(),CODECAPI_AVEncCommonMeanBitRate,bitrate);
        // Use a 3-second GOP (capped at [fps, 300]) so 1Hz IDR frames do not periodically starve CBR text P-frames.
        UINT gop=std::clamp(fps*3u,std::max(fps,1u),300u);
        if(SUCCEEDED(codec_property(transform.Get(),CODECAPI_AVEncMPVGOPSize,gop)))settings|=8;
        auto output=video_type(MFVideoFormat_H264,width,height,fps);check(output->SetUINT32(MF_MT_AVG_BITRATE,bitrate),"bitrate");
        // Prefer High Profile (8x8 transform improves sharp text stroke energy compaction); fall back to Main if rejected.
        check(output->SetUINT32(MF_MT_MPEG2_PROFILE,eAVEncH264VProfile_High),"H264 High profile");
        if(FAILED(transform->SetOutputType(0,output.Get(),0))){
            check(output->SetUINT32(MF_MT_MPEG2_PROFILE,eAVEncH264VProfile_Main),"H264 Main profile");
            check(transform->SetOutputType(0,output.Get(),0),"encoder output H264");
        }
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
        if(hlsl_color){
            auto* dev=capture->graphics.device.Get();
            auto* ctx=capture->graphics.commands.Get();
            ID3D11Texture2D* src_tex=capture->texture.Get();
            if(!(source.BindFlags&D3D11_BIND_SHADER_RESOURCE)){
                if(!readable_bgra){
                    D3D11_TEXTURE2D_DESC rd=source;rd.BindFlags=D3D11_BIND_SHADER_RESOURCE;rd.Usage=D3D11_USAGE_DEFAULT;rd.CPUAccessFlags=0;rd.MiscFlags=0;
                    check(dev->CreateTexture2D(&rd,nullptr,&readable_bgra),"readable BGRA texture");
                }
                ctx->CopyResource(readable_bgra.Get(),src_tex);
                src_tex=readable_bgra.Get();
            }
            if(cached_bgra_tex!=src_tex||!cached_bgra_srv){
                cached_bgra_srv.Reset();
                check(dev->CreateShaderResourceView(src_tex,nullptr,&cached_bgra_srv),"encoder BGRA SRV");
                cached_bgra_tex=src_tex;
            }
            D3D11_RENDER_TARGET_VIEW_DESC ry{},ruv{};
            ry.Format=DXGI_FORMAT_R8_UNORM;ry.ViewDimension=D3D11_RTV_DIMENSION_TEXTURE2D;
            ruv.Format=DXGI_FORMAT_R8G8_UNORM;ruv.ViewDimension=D3D11_RTV_DIMENSION_TEXTURE2D;
            ComPtr<ID3D11RenderTargetView> y_rtv,uv_rtv;
            check(dev->CreateRenderTargetView(nv12.Get(),&ry,&y_rtv),"NV12 Y RTV");
            check(dev->CreateRenderTargetView(nv12.Get(),&ruv,&uv_rtv),"NV12 UV RTV");
            timing.surface_setup_ms=elapsed_ms(setup_start);
            auto color_submit_start=ticks();
            if(profile_open)ctx->End(color_start.Get());
            ctx->OMSetBlendState(nullptr,nullptr,0xffffffff);
            ctx->IASetInputLayout(nullptr);
            ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            ctx->VSSetShader(color_vs.Get(),nullptr,0);
            ctx->RSSetState(color_rs.Get());
            ID3D11Buffer* cb=color_cb.Get();
            ctx->PSSetConstantBuffers(0,1,&cb);
            ID3D11ShaderResourceView* srv=cached_bgra_srv.Get();
            ID3D11RenderTargetView* rtv=y_rtv.Get();
            ctx->OMSetRenderTargets(1,&rtv,nullptr);
            ctx->PSSetShaderResources(0,1,&srv);
            ctx->PSSetShader(encode_y_ps.Get(),nullptr,0);
            D3D11_VIEWPORT vpy{0.0f,0.0f,static_cast<float>(width),static_cast<float>(height),0.0f,1.0f};
            ctx->RSSetViewports(1,&vpy);
            ctx->Draw(3,0);
            rtv=uv_rtv.Get();
            ctx->OMSetRenderTargets(1,&rtv,nullptr);
            ctx->PSSetShader(encode_uv_ps.Get(),nullptr,0);
            D3D11_VIEWPORT vpuv{0.0f,0.0f,static_cast<float>(width/2),static_cast<float>(height/2),0.0f,1.0f};
            ctx->RSSetViewports(1,&vpuv);
            ctx->Draw(3,0);
            ID3D11ShaderResourceView* null_srv=nullptr;
            ctx->PSSetShaderResources(0,1,&null_srv);
            ctx->OMSetRenderTargets(0,nullptr,nullptr);
            if(profile_open){ctx->End(color_end.Get());ctx->End(disjoint.Get());profile_open=false;profile_pending=true;}
            timing.color_submit_ms=elapsed_ms(color_submit_start);
        }else{
            D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC in{};in.ViewDimension=D3D11_VPIV_DIMENSION_TEXTURE2D;
            ComPtr<ID3D11VideoProcessorInputView> input;check(video->CreateVideoProcessorInputView(capture->texture.Get(),enumerator.Get(),&in,&input),"BGRA processor input");
            D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC out{};out.ViewDimension=D3D11_VPOV_DIMENSION_TEXTURE2D;
            ComPtr<ID3D11VideoProcessorOutputView> output;check(video->CreateVideoProcessorOutputView(nv12.Get(),enumerator.Get(),&out,&output),"NV12 processor output");
            RECT area{0,0,static_cast<LONG>(width),static_cast<LONG>(height)};video_commands->VideoProcessorSetStreamSourceRect(processor.Get(),0,TRUE,&area);video_commands->VideoProcessorSetStreamDestRect(processor.Get(),0,TRUE,&area);video_commands->VideoProcessorSetOutputTargetRect(processor.Get(),TRUE,&area);
            D3D11_VIDEO_PROCESSOR_COLOR_SPACE rgb{};rgb.RGB_Range=0;rgb.YCbCr_Matrix=1;rgb.Nominal_Range=2;
            D3D11_VIDEO_PROCESSOR_COLOR_SPACE yuv{};yuv.YCbCr_Matrix=1;yuv.Nominal_Range=1;
            video_commands->VideoProcessorSetStreamColorSpace(processor.Get(),0,&rgb);video_commands->VideoProcessorSetOutputColorSpace(processor.Get(),&yuv);
            D3D11_VIDEO_PROCESSOR_STREAM stream{};stream.Enable=TRUE;stream.pInputSurface=input.Get();timing.surface_setup_ms=elapsed_ms(setup_start);auto color_submit_start=ticks();if(profile_open)capture->graphics.commands->End(color_start.Get());check(video_commands->VideoProcessorBlt(processor.Get(),output.Get(),0,1,&stream),"GPU BGRA -> NV12");if(profile_open){capture->graphics.commands->End(color_end.Get());capture->graphics.commands->End(disjoint.Get());profile_open=false;profile_pending=true;}timing.color_submit_ms=elapsed_ms(color_submit_start);
        }
        ComPtr<IMFMediaBuffer> buffer;check(MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D),nv12.Get(),0,FALSE,&buffer),"MFCreateDXGISurfaceBuffer");ComPtr<IMFSample> sample;check(MFCreateSample(&sample),"GPU sample");check(sample->AddBuffer(buffer.Get()),"GPU sample buffer");return sample;
    }
};
struct decoder_context {
    resource_counter tracked{2};
    rd_decode_timing timing{};
    runtime lifetime;UINT width,height,coded_width=0,coded_height=0;LONG stride=0;bool hardware,hlsl_decode=false,hlsl_checked=false;ComPtr<IMFTransform> transform;gpu graphics;
    ComPtr<IMFDXGIDeviceManager> manager;ComPtr<ID3D11VideoDevice> video;ComPtr<ID3D11VideoContext> video_commands;
    ComPtr<ID3D11VideoProcessorEnumerator> enumerator;ComPtr<ID3D11VideoProcessor> processor;
    ComPtr<ID3D11Texture2D> converted,staging,nv12_copy;
    ComPtr<ID3D11RenderTargetView> converted_rtv;
    ComPtr<ID3D11ShaderResourceView> nv12_copy_y,nv12_copy_uv;
    ComPtr<ID3D11VertexShader> decode_vs;ComPtr<ID3D11PixelShader> decode_ps;
    ComPtr<ID3D11Buffer> decode_cb;ComPtr<ID3D11RasterizerState> decode_rs;
    UINT copy_w=0,copy_h=0;
    decoder_context(UINT w,UINT h,bool gpu_decode):width(w),height(h),hardware(gpu_decode){
        check(CoCreateInstance(CLSID_CMSH264DecoderMFT,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&transform)),"H264 decoder");
        if(hardware){graphics.create();ComPtr<IMFAttributes> attributes;check(transform->GetAttributes(&attributes),"decoder attributes");UINT32 aware=0;check(attributes->GetUINT32(MF_SA_D3D11_AWARE,&aware),"decoder D3D11 aware");if(!aware)check(MF_E_UNSUPPORTED_D3D_TYPE,"decoder lacks D3D11 support");
            UINT token=0;check(MFCreateDXGIDeviceManager(&token,&manager),"decoder device manager");check(manager->ResetDevice(graphics.device.Get(),token),"decoder ResetDevice");check(transform->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,reinterpret_cast<ULONG_PTR>(manager.Get())),"decoder D3D manager");
            ComPtr<IMFAttributes> out_attr;if(SUCCEEDED(transform->GetOutputStreamAttributes(0,&out_attr))&&out_attr){UINT32 binds=0;out_attr->GetUINT32(MF_SA_D3D11_BINDFLAGS,&binds);out_attr->SetUINT32(MF_SA_D3D11_BINDFLAGS,binds|D3D11_BIND_SHADER_RESOURCE);}
        }
        // Inbox H264 decoder uses VT_UI4, unlike the encoder's VT_BOOL (documented exception).
        codec_property(transform.Get(),CODECAPI_AVLowLatencyMode,1);
        auto input=video_type(MFVideoFormat_H264,w,h,30);check(transform->SetInputType(0,input.Get(),0),"decoder input H264");select_output();start(transform.Get());
    }
    ~decoder_context(){shutdown(transform.Get());}
    void init_hlsl_decode(const D3D11_TEXTURE2D_DESC& source){
        if(hlsl_checked&&copy_w==source.Width&&copy_h==source.Height)return;
        hlsl_checked=true;hlsl_decode=false;
        auto* dev=graphics.device.Get();
        if(!converted){
            D3D11_TEXTURE2D_DESC desc{};desc.Width=width;desc.Height=height;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.BindFlags=D3D11_BIND_RENDER_TARGET;
            check(dev->CreateTexture2D(&desc,nullptr,&converted),"decoder BGRA texture");
            desc.BindFlags=0;desc.Usage=D3D11_USAGE_STAGING;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
            check(dev->CreateTexture2D(&desc,nullptr,&staging),"WPF readback staging");
        }
        if(!converted_rtv&&FAILED(dev->CreateRenderTargetView(converted.Get(),nullptr,&converted_rtv)))return;
        if(!decode_vs){
            ComPtr<ID3DBlob> vs,ps,err;
            if(FAILED(D3DCompile(codec_color_shader,strlen(codec_color_shader),nullptr,nullptr,nullptr,"vs","vs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&vs,&err)))return;
            if(FAILED(D3DCompile(codec_color_shader,strlen(codec_color_shader),nullptr,nullptr,nullptr,"ps_decode_bgra","ps_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&ps,&err)))return;
            if(FAILED(dev->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&decode_vs)))return;
            if(FAILED(dev->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&decode_ps)))return;
            int cb_data[4]={static_cast<int>(width),static_cast<int>(height),0,0};
            D3D11_BUFFER_DESC bd{};bd.ByteWidth=16;bd.Usage=D3D11_USAGE_IMMUTABLE;bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
            D3D11_SUBRESOURCE_DATA init{cb_data,0,0};
            if(FAILED(dev->CreateBuffer(&bd,&init,&decode_cb)))return;
            D3D11_RASTERIZER_DESC rs{};rs.FillMode=D3D11_FILL_SOLID;rs.CullMode=D3D11_CULL_NONE;rs.DepthClipEnable=TRUE;
            if(FAILED(dev->CreateRasterizerState(&rs,&decode_rs)))return;
        }
        nv12_copy_y.Reset();nv12_copy_uv.Reset();nv12_copy.Reset();
        D3D11_TEXTURE2D_DESC nd{};nd.Width=source.Width;nd.Height=source.Height;nd.MipLevels=nd.ArraySize=1;nd.Format=DXGI_FORMAT_NV12;nd.SampleDesc.Count=1;nd.BindFlags=D3D11_BIND_SHADER_RESOURCE;
        if(FAILED(dev->CreateTexture2D(&nd,nullptr,&nv12_copy)))return;
        D3D11_SHADER_RESOURCE_VIEW_DESC sy{},suv{};
        sy.Format=DXGI_FORMAT_R8_UNORM;sy.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;sy.Texture2D.MipLevels=1;
        suv.Format=DXGI_FORMAT_R8G8_UNORM;suv.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;suv.Texture2D.MipLevels=1;
        if(FAILED(dev->CreateShaderResourceView(nv12_copy.Get(),&sy,&nv12_copy_y))||FAILED(dev->CreateShaderResourceView(nv12_copy.Get(),&suv,&nv12_copy_uv)))return;
        copy_w=source.Width;copy_h=source.Height;hlsl_decode=true;
    }
    void gpu_pixels(IMFMediaBuffer* buffer,uint8_t* bgra){
        auto conversion_start=ticks();
        ComPtr<IMFDXGIBuffer> surface;check(buffer->QueryInterface(IID_PPV_ARGS(&surface)),"GPU decode must return DXGI texture");
        ComPtr<ID3D11Texture2D> texture;check(surface->GetResource(IID_PPV_ARGS(&texture)),"decoder texture");UINT subresource=0;check(surface->GetSubresourceIndex(&subresource),"decoder subresource");
        D3D11_TEXTURE2D_DESC source{};texture->GetDesc(&source);
        init_hlsl_decode(source);
        if(hlsl_decode){
            auto* dev=graphics.device.Get();
            auto* ctx=graphics.commands.Get();
            ComPtr<ID3D11ShaderResourceView> y_srv,uv_srv;
            if(source.BindFlags&D3D11_BIND_SHADER_RESOURCE){
                D3D11_SHADER_RESOURCE_VIEW_DESC sy{},suv{};
                sy.Format=DXGI_FORMAT_R8_UNORM;suv.Format=DXGI_FORMAT_R8G8_UNORM;
                if(source.ArraySize>1){
                    sy.ViewDimension=suv.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2DARRAY;
                    sy.Texture2DArray.MipLevels=suv.Texture2DArray.MipLevels=1;
                    sy.Texture2DArray.FirstArraySlice=suv.Texture2DArray.FirstArraySlice=subresource;
                    sy.Texture2DArray.ArraySize=suv.Texture2DArray.ArraySize=1;
                }else{
                    sy.ViewDimension=suv.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;
                    sy.Texture2D.MipLevels=suv.Texture2D.MipLevels=1;
                }
                if(FAILED(dev->CreateShaderResourceView(texture.Get(),&sy,&y_srv))||FAILED(dev->CreateShaderResourceView(texture.Get(),&suv,&uv_srv))){y_srv.Reset();uv_srv.Reset();}
            }
            if(!y_srv||!uv_srv){
                ctx->CopySubresourceRegion(nv12_copy.Get(),0,0,0,0,texture.Get(),subresource,nullptr);
                y_srv=nv12_copy_y;uv_srv=nv12_copy_uv;
            }
            ctx->OMSetBlendState(nullptr,nullptr,0xffffffff);
            ctx->IASetInputLayout(nullptr);
            ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            ctx->VSSetShader(decode_vs.Get(),nullptr,0);
            ctx->PSSetShader(decode_ps.Get(),nullptr,0);
            ctx->RSSetState(decode_rs.Get());
            ID3D11Buffer* cb=decode_cb.Get();
            ctx->PSSetConstantBuffers(0,1,&cb);
            ID3D11RenderTargetView* rtv=converted_rtv.Get();
            ctx->OMSetRenderTargets(1,&rtv,nullptr);
            ID3D11ShaderResourceView* srvs[2]={y_srv.Get(),uv_srv.Get()};
            ctx->PSSetShaderResources(0,2,srvs);
            D3D11_VIEWPORT vp{0.0f,0.0f,static_cast<float>(width),static_cast<float>(height),0.0f,1.0f};
            ctx->RSSetViewports(1,&vp);
            ctx->Draw(3,0);
            ID3D11ShaderResourceView* empty[2]={nullptr,nullptr};
            ctx->PSSetShaderResources(0,2,empty);
            ctx->OMSetRenderTargets(0,nullptr,nullptr);
        }else{
            if(!processor){check(graphics.device.As(&video),"decoder video device");check(graphics.commands.As(&video_commands),"decoder video commands");
                D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};content.InputFrameFormat=D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;content.InputWidth=source.Width;content.InputHeight=source.Height;content.OutputWidth=width;content.OutputHeight=height;content.InputFrameRate=content.OutputFrameRate={30,1};content.Usage=D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
                check(video->CreateVideoProcessorEnumerator(&content,&enumerator),"decoder processor enumerator");check(video->CreateVideoProcessor(enumerator.Get(),0,&processor),"decoder NV12 -> BGRA processor");
            }
            D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC in{};in.ViewDimension=D3D11_VPIV_DIMENSION_TEXTURE2D;in.Texture2D.ArraySlice=subresource;
            ComPtr<ID3D11VideoProcessorInputView> input;check(video->CreateVideoProcessorInputView(texture.Get(),enumerator.Get(),&in,&input),"decoder processor input");
            D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC out{};out.ViewDimension=D3D11_VPOV_DIMENSION_TEXTURE2D;ComPtr<ID3D11VideoProcessorOutputView> output;check(video->CreateVideoProcessorOutputView(converted.Get(),enumerator.Get(),&out,&output),"decoder processor output");
            RECT area{0,0,static_cast<LONG>(width),static_cast<LONG>(height)};video_commands->VideoProcessorSetStreamSourceRect(processor.Get(),0,TRUE,&area);video_commands->VideoProcessorSetStreamDestRect(processor.Get(),0,TRUE,&area);video_commands->VideoProcessorSetOutputTargetRect(processor.Get(),TRUE,&area);
            D3D11_VIDEO_PROCESSOR_COLOR_SPACE yuv{};yuv.YCbCr_Matrix=1;yuv.Nominal_Range=1;D3D11_VIDEO_PROCESSOR_COLOR_SPACE rgb{};rgb.YCbCr_Matrix=1;rgb.Nominal_Range=2;video_commands->VideoProcessorSetStreamColorSpace(processor.Get(),0,&yuv);video_commands->VideoProcessorSetOutputColorSpace(processor.Get(),&rgb);
            D3D11_VIDEO_PROCESSOR_STREAM stream{};stream.Enable=TRUE;stream.pInputSurface=input.Get();check(video_commands->VideoProcessorBlt(processor.Get(),output.Get(),0,1,&stream),"GPU NV12 -> BGRA");
        }
        timing.conversion_ms=elapsed_ms(conversion_start);auto readback_start=ticks();graphics.commands->CopyResource(staging.Get(),converted.Get());D3D11_MAPPED_SUBRESOURCE mapped{};check(graphics.commands->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped),"WPF readback Map");
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

struct sps_bit_reader {
    std::vector<uint8_t> rbsp;
    size_t pos=0;
    sps_bit_reader(const uint8_t* nalu,size_t len){
        for(size_t i=0;i<len;++i){
            if(i+2<len&&nalu[i]==0&&nalu[i+1]==0&&nalu[i+2]==3){rbsp.push_back(0);rbsp.push_back(0);i+=2;}
            else rbsp.push_back(nalu[i]);
        }
    }
    uint32_t bit(){if(pos/8>=rbsp.size())return 0;uint32_t b=(rbsp[pos/8]>>(7-(pos%8)))&1u;++pos;return b;}
    uint32_t bits(int n){uint32_t v=0;for(int i=0;i<n;++i)v=(v<<1)|bit();return v;}
    uint32_t ue(){int z=0;while(pos/8<rbsp.size()&&bit()==0)++z;return ((1u<<z)-1u)+bits(z);}
};

void parse_h264_sps(const uint8_t* data,size_t len,uint32_t& profile_idc,uint32_t& chroma_format_idc){
    profile_idc=0;chroma_format_idc=0;
    for(size_t i=0;i+4<len;++i){
        size_t sc=0;
        if(data[i]==0&&data[i+1]==0&&data[i+2]==1)sc=3;
        else if(data[i]==0&&data[i+1]==0&&data[i+2]==0&&data[i+3]==1)sc=4;
        if(!sc)continue;
        size_t start_idx=i+sc;if(start_idx>=len)break;
        if((data[start_idx]&0x1f)==7){
            size_t end_idx=len;
            for(size_t j=start_idx+1;j+3<len;++j)if(data[j]==0&&data[j+1]==0&&(data[j+2]==1||(data[j+2]==0&&data[j+3]==1))){end_idx=j;break;}
            sps_bit_reader br(data+start_idx+1,end_idx-(start_idx+1));
            profile_idc=br.bits(8);br.bits(16);br.ue();
            chroma_format_idc=1;
            if(profile_idc==100||profile_idc==110||profile_idc==122||profile_idc==244||profile_idc==44)chroma_format_idc=br.ue();
            return;
        }
    }
}

std::string json_escape(const std::string& s){
    std::ostringstream o;o<<'"';
    for(unsigned char c:s){if(c=='"'||c=='\\')o<<'\\'<<c;else if(c<32)o<<' ';else o<<c;}
    o<<'"';return o.str();
}
}

RD int32_t rd_encoder_set_bitrate(void* encoder, uint32_t bitrate) {
    if (!encoder || bitrate < 100000 || bitrate > 100000000) return E_INVALIDARG;
    auto* context = static_cast<encoder_context*>(encoder);
    return codec_property(context->transform.Get(), CODECAPI_AVEncCommonMeanBitRate, bitrate);
}
RD int32_t rd_encoder_create(void* capture,uint32_t width,uint32_t height,uint32_t fps,uint32_t bitrate,int hardware,void** encoder){if(!encoder||!width||!height||(width&1)||(height&1)||width>7680||height>4320||fps<1||fps>144||bitrate<100000||bitrate>100000000)return E_INVALIDARG;*encoder=nullptr;return guarded([&]{auto e=std::make_unique<encoder_context>(width,height,fps);e->initialize(static_cast<capture_context*>(capture),hardware!=0,bitrate);*encoder=e.release();});}
RD int32_t rd_encoder_frame(void* encoder,int64_t time,int force,const uint8_t** data,uint32_t* bytes,int* key,int64_t* output_time){if(!encoder||!data||!bytes||!key||!output_time)return E_INVALIDARG;HRESULT result=S_OK;HRESULT hr=guarded([&]{auto& e=*static_cast<encoder_context*>(encoder);e.lifetime.verify();if(!e.hardware||!e.capture)check(E_INVALIDARG,"GPU encode requires hardware capture context");e.begin_timing();if(e.profile_open)e.capture->graphics.commands->End(e.upload_end.Get());auto sample=e.gpu_sample();result=e.encode(sample.Get(),time,force!=0,data,bytes,key,output_time);e.finish_timing();});return FAILED(hr)?hr:result;}
RD int32_t rd_encoder_bgra(void* encoder,const uint8_t* data,uint32_t bytes,int64_t time,int force,const uint8_t** output,uint32_t* length,int* key,int64_t* output_time){if(!encoder||!data||!output||!length||!key||!output_time)return E_INVALIDARG;HRESULT result=S_OK;HRESULT hr=guarded([&]{auto& e=*static_cast<encoder_context*>(encoder);e.lifetime.verify();if(bytes!=static_cast<uint64_t>(e.width)*e.height*4)check(E_INVALIDARG,"BGRA encoder input");e.begin_timing();
    if(e.hardware){if(!e.synthetic)check(E_INVALIDARG,"BGRA upload only allowed for explicit synthetic test encoder");if(!e.capture->texture){D3D11_TEXTURE2D_DESC desc{};desc.Width=e.width;desc.Height=e.height;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;check(e.capture->graphics.device->CreateTexture2D(&desc,nullptr,&e.capture->texture),"synthetic BGRA texture");}auto upload_start=ticks();e.capture->graphics.commands->UpdateSubresource(e.capture->texture.Get(),0,nullptr,data,e.width*4,0);e.timing.upload_submit_ms=elapsed_ms(upload_start);e.timing.upload_bytes=bytes;if(e.profile_open)e.capture->graphics.commands->End(e.upload_end.Get());auto sample=e.gpu_sample();result=e.encode(sample.Get(),time,force!=0,output,length,key,output_time);e.finish_timing();return;}
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

RD int32_t rd_color_convert_nv12_roundtrip(const uint8_t* src_bgra,uint32_t width,uint32_t height,int hardware,int optimized,uint8_t* nv12_out,uint32_t nv12_capacity,uint8_t* recon_bgra_out,uint32_t bgra_capacity){
    if(!src_bgra||!nv12_out||!recon_bgra_out||!width||!height||(width&1)||(height&1)||static_cast<uint64_t>(width)*height>33177600)return E_INVALIDARG;
    const uint64_t expected_nv12=static_cast<uint64_t>(width)*height*3/2;
    const uint64_t expected_bgra=static_cast<uint64_t>(width)*height*4;
    if(nv12_capacity!=expected_nv12||bgra_capacity!=expected_bgra)return E_INVALIDARG;
    return guarded([&]{
        runtime rt;
        if(!hardware){
            if(optimized){bgra_nv12(src_bgra,nv12_out,width,height);nv12_bgra(nv12_out,static_cast<LONG>(width),recon_bgra_out,width,height,height);}
            else{bgra_nv12_legacy(src_bgra,nv12_out,width,height);nv12_bgra_legacy(nv12_out,static_cast<LONG>(width),recon_bgra_out,width,height,height);}
            return;
        }
        gpu graphics;graphics.create();
        auto* dev=graphics.device.Get();auto* ctx=graphics.commands.Get();
        D3D11_TEXTURE2D_DESC bd{};bd.Width=width;bd.Height=height;bd.MipLevels=bd.ArraySize=1;bd.Format=DXGI_FORMAT_B8G8R8A8_UNORM;bd.SampleDesc.Count=1;bd.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA init{src_bgra,width*4,0};
        ComPtr<ID3D11Texture2D> src_tex,dst_tex;
        check(dev->CreateTexture2D(&bd,&init,&src_tex),"roundtrip src BGRA");
        check(dev->CreateTexture2D(&bd,nullptr,&dst_tex),"roundtrip dst BGRA");
        D3D11_TEXTURE2D_DESC nd{};nd.Width=width;nd.Height=height;nd.MipLevels=nd.ArraySize=1;nd.Format=DXGI_FORMAT_NV12;nd.SampleDesc.Count=1;nd.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
        ComPtr<ID3D11Texture2D> nv_tex;check(dev->CreateTexture2D(&nd,nullptr,&nv_tex),"roundtrip NV12");
        if(optimized){
            ComPtr<ID3DBlob> vs_b,psy_b,psuv_b,psd_b,err;
            check(D3DCompile(codec_color_shader,strlen(codec_color_shader),nullptr,nullptr,nullptr,"vs","vs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&vs_b,&err),"roundtrip vs");
            check(D3DCompile(codec_color_shader,strlen(codec_color_shader),nullptr,nullptr,nullptr,"ps_encode_y","ps_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&psy_b,&err),"roundtrip encode y");
            check(D3DCompile(codec_color_shader,strlen(codec_color_shader),nullptr,nullptr,nullptr,"ps_encode_uv","ps_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&psuv_b,&err),"roundtrip encode uv");
            check(D3DCompile(codec_color_shader,strlen(codec_color_shader),nullptr,nullptr,nullptr,"ps_decode_bgra","ps_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&psd_b,&err),"roundtrip decode bgra");
            ComPtr<ID3D11VertexShader> vs;ComPtr<ID3D11PixelShader> psy,psuv,psd;
            check(dev->CreateVertexShader(vs_b->GetBufferPointer(),vs_b->GetBufferSize(),nullptr,&vs),"roundtrip VS");
            check(dev->CreatePixelShader(psy_b->GetBufferPointer(),psy_b->GetBufferSize(),nullptr,&psy),"roundtrip PSY");
            check(dev->CreatePixelShader(psuv_b->GetBufferPointer(),psuv_b->GetBufferSize(),nullptr,&psuv),"roundtrip PSUV");
            check(dev->CreatePixelShader(psd_b->GetBufferPointer(),psd_b->GetBufferSize(),nullptr,&psd),"roundtrip PSD");
            int cb_data[4]={static_cast<int>(width),static_cast<int>(height),0,0};
            D3D11_BUFFER_DESC cbd{};cbd.ByteWidth=16;cbd.Usage=D3D11_USAGE_IMMUTABLE;cbd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;D3D11_SUBRESOURCE_DATA cbi{cb_data,0,0};
            ComPtr<ID3D11Buffer> cb;check(dev->CreateBuffer(&cbd,&cbi,&cb),"roundtrip CB");
            ComPtr<ID3D11ShaderResourceView> src_srv,y_srv,uv_srv;
            check(dev->CreateShaderResourceView(src_tex.Get(),nullptr,&src_srv),"roundtrip src SRV");
            D3D11_RENDER_TARGET_VIEW_DESC ry{},ruv{};ry.Format=DXGI_FORMAT_R8_UNORM;ry.ViewDimension=D3D11_RTV_DIMENSION_TEXTURE2D;ruv.Format=DXGI_FORMAT_R8G8_UNORM;ruv.ViewDimension=D3D11_RTV_DIMENSION_TEXTURE2D;
            ComPtr<ID3D11RenderTargetView> y_rtv,uv_rtv,dst_rtv;
            check(dev->CreateRenderTargetView(nv_tex.Get(),&ry,&y_rtv),"roundtrip Y RTV");
            check(dev->CreateRenderTargetView(nv_tex.Get(),&ruv,&uv_rtv),"roundtrip UV RTV");
            check(dev->CreateRenderTargetView(dst_tex.Get(),nullptr,&dst_rtv),"roundtrip dst RTV");
            D3D11_SHADER_RESOURCE_VIEW_DESC sy{},suv{};sy.Format=DXGI_FORMAT_R8_UNORM;sy.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;sy.Texture2D.MipLevels=1;suv.Format=DXGI_FORMAT_R8G8_UNORM;suv.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;suv.Texture2D.MipLevels=1;
            check(dev->CreateShaderResourceView(nv_tex.Get(),&sy,&y_srv),"roundtrip Y SRV");
            check(dev->CreateShaderResourceView(nv_tex.Get(),&suv,&uv_srv),"roundtrip UV SRV");
            ctx->IASetInputLayout(nullptr);ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);ctx->VSSetShader(vs.Get(),nullptr,0);
            ID3D11Buffer* cbp=cb.Get();ctx->PSSetConstantBuffers(0,1,&cbp);
            ID3D11ShaderResourceView* s0=src_srv.Get();ID3D11RenderTargetView* rtv=y_rtv.Get();
            ctx->OMSetRenderTargets(1,&rtv,nullptr);ctx->PSSetShaderResources(0,1,&s0);ctx->PSSetShader(psy.Get(),nullptr,0);
            D3D11_VIEWPORT vpy{0.0f,0.0f,static_cast<float>(width),static_cast<float>(height),0.0f,1.0f};ctx->RSSetViewports(1,&vpy);ctx->Draw(3,0);
            rtv=uv_rtv.Get();ctx->OMSetRenderTargets(1,&rtv,nullptr);ctx->PSSetShader(psuv.Get(),nullptr,0);
            D3D11_VIEWPORT vpuv{0.0f,0.0f,static_cast<float>(width/2),static_cast<float>(height/2),0.0f,1.0f};ctx->RSSetViewports(1,&vpuv);ctx->Draw(3,0);
            ctx->OMSetRenderTargets(0,nullptr,nullptr);
            rtv=dst_rtv.Get();ctx->OMSetRenderTargets(1,&rtv,nullptr);
            ID3D11ShaderResourceView* nvs[2]={y_srv.Get(),uv_srv.Get()};ctx->PSSetShaderResources(0,2,nvs);ctx->PSSetShader(psd.Get(),nullptr,0);ctx->RSSetViewports(1,&vpy);ctx->Draw(3,0);
            ID3D11ShaderResourceView* empty[2]={nullptr,nullptr};ctx->PSSetShaderResources(0,2,empty);ctx->OMSetRenderTargets(0,nullptr,nullptr);
        }else{
            ComPtr<ID3D11VideoDevice> vdev;check(graphics.device.As(&vdev),"roundtrip video device");
            ComPtr<ID3D11VideoContext> vctx;check(graphics.commands.As(&vctx),"roundtrip video ctx");
            D3D11_VIDEO_PROCESSOR_CONTENT_DESC cd{};cd.InputFrameFormat=D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;cd.InputWidth=cd.OutputWidth=width;cd.InputHeight=cd.OutputHeight=height;cd.InputFrameRate=cd.OutputFrameRate={30,1};cd.Usage=D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
            ComPtr<ID3D11VideoProcessorEnumerator> vpe;ComPtr<ID3D11VideoProcessor> vp;
            check(vdev->CreateVideoProcessorEnumerator(&cd,&vpe),"roundtrip VP enum");check(vdev->CreateVideoProcessor(vpe.Get(),0,&vp),"roundtrip VP");
            D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC ivd{};ivd.ViewDimension=D3D11_VPIV_DIMENSION_TEXTURE2D;
            D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ovd{};ovd.ViewDimension=D3D11_VPOV_DIMENSION_TEXTURE2D;
            ComPtr<ID3D11VideoProcessorInputView> in_b,in_n;ComPtr<ID3D11VideoProcessorOutputView> out_n,out_b;
            check(vdev->CreateVideoProcessorInputView(src_tex.Get(),vpe.Get(),&ivd,&in_b),"VP in BGRA");
            check(vdev->CreateVideoProcessorOutputView(nv_tex.Get(),vpe.Get(),&ovd,&out_n),"VP out NV12");
            check(vdev->CreateVideoProcessorInputView(nv_tex.Get(),vpe.Get(),&ivd,&in_n),"VP in NV12");
            check(vdev->CreateVideoProcessorOutputView(dst_tex.Get(),vpe.Get(),&ovd,&out_b),"VP out BGRA");
            RECT rc{0,0,static_cast<LONG>(width),static_cast<LONG>(height)};
            vctx->VideoProcessorSetStreamSourceRect(vp.Get(),0,TRUE,&rc);vctx->VideoProcessorSetStreamDestRect(vp.Get(),0,TRUE,&rc);vctx->VideoProcessorSetOutputTargetRect(vp.Get(),TRUE,&rc);
            D3D11_VIDEO_PROCESSOR_COLOR_SPACE cs_rgb{},cs_yuv{};cs_rgb.YCbCr_Matrix=1;cs_rgb.Nominal_Range=2;cs_yuv.YCbCr_Matrix=1;cs_yuv.Nominal_Range=1;
            vctx->VideoProcessorSetStreamColorSpace(vp.Get(),0,&cs_rgb);vctx->VideoProcessorSetOutputColorSpace(vp.Get(),&cs_yuv);
            D3D11_VIDEO_PROCESSOR_STREAM st{};st.Enable=TRUE;st.pInputSurface=in_b.Get();
            check(vctx->VideoProcessorBlt(vp.Get(),out_n.Get(),0,1,&st),"VP BGRA->NV12");
            vctx->VideoProcessorSetStreamColorSpace(vp.Get(),0,&cs_yuv);vctx->VideoProcessorSetOutputColorSpace(vp.Get(),&cs_rgb);
            st.pInputSurface=in_n.Get();
            check(vctx->VideoProcessorBlt(vp.Get(),out_b.Get(),0,1,&st),"VP NV12->BGRA");
        }
        D3D11_TEXTURE2D_DESC sn=nd;sn.BindFlags=0;sn.Usage=D3D11_USAGE_STAGING;sn.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> stage_nv;check(dev->CreateTexture2D(&sn,nullptr,&stage_nv),"stage NV12");
        ctx->CopyResource(stage_nv.Get(),nv_tex.Get());
        D3D11_MAPPED_SUBRESOURCE map{};check(ctx->Map(stage_nv.Get(),0,D3D11_MAP_READ,0,&map),"map NV12");
        const auto* base_p=static_cast<const uint8_t*>(map.pData);
        for(UINT y=0;y<height;++y)memcpy(nv12_out+static_cast<size_t>(y)*width,base_p+static_cast<size_t>(y)*map.RowPitch,width);
        const auto* uv_src=base_p+static_cast<size_t>(height)*map.RowPitch;
        auto* uv_dst=nv12_out+static_cast<size_t>(width)*height;
        for(UINT y=0;y<height/2;++y)memcpy(uv_dst+static_cast<size_t>(y)*width,uv_src+static_cast<size_t>(y)*map.RowPitch,width);
        ctx->Unmap(stage_nv.Get(),0);
        D3D11_TEXTURE2D_DESC sb=bd;sb.BindFlags=0;sb.Usage=D3D11_USAGE_STAGING;sb.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> stage_b;check(dev->CreateTexture2D(&sb,nullptr,&stage_b),"stage BGRA");
        ctx->CopyResource(stage_b.Get(),dst_tex.Get());
        check(ctx->Map(stage_b.Get(),0,D3D11_MAP_READ,0,&map),"map BGRA");
        base_p=static_cast<const uint8_t*>(map.pData);
        for(UINT y=0;y<height;++y)memcpy(recon_bgra_out+static_cast<size_t>(y)*width*4,base_p+static_cast<size_t>(y)*map.RowPitch,width*4);
        ctx->Unmap(stage_b.Get(),0);
    });
}

RD int32_t rd_codec_capabilities(const char** json){
    if(!json)return E_POINTER;
    static thread_local std::string report;
    return guarded([&]{
        runtime rt;gpu graphics;bool gpu_ok=SUCCEEDED(guarded([&]{graphics.create();}));
        bool nv12_rtv=false,nv12_srv=false;
        if(gpu_ok){
            D3D11_TEXTURE2D_DESC td{};td.Width=64;td.Height=64;td.MipLevels=td.ArraySize=1;td.Format=DXGI_FORMAT_NV12;td.SampleDesc.Count=1;td.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
            ComPtr<ID3D11Texture2D> t;
            if(SUCCEEDED(graphics.device->CreateTexture2D(&td,nullptr,&t))){
                D3D11_RENDER_TARGET_VIEW_DESC ry{},ruv{};ry.Format=DXGI_FORMAT_R8_UNORM;ry.ViewDimension=D3D11_RTV_DIMENSION_TEXTURE2D;ruv.Format=DXGI_FORMAT_R8G8_UNORM;ruv.ViewDimension=D3D11_RTV_DIMENSION_TEXTURE2D;
                ComPtr<ID3D11RenderTargetView> r0,r1;nv12_rtv=SUCCEEDED(graphics.device->CreateRenderTargetView(t.Get(),&ry,&r0))&&SUCCEEDED(graphics.device->CreateRenderTargetView(t.Get(),&ruv,&r1));
                D3D11_SHADER_RESOURCE_VIEW_DESC sy{},suv{};sy.Format=DXGI_FORMAT_R8_UNORM;sy.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;sy.Texture2D.MipLevels=1;suv.Format=DXGI_FORMAT_R8G8_UNORM;suv.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;suv.Texture2D.MipLevels=1;
                ComPtr<ID3D11ShaderResourceView> s0,s1;nv12_srv=SUCCEEDED(graphics.device->CreateShaderResourceView(t.Get(),&sy,&s0))&&SUCCEEDED(graphics.device->CreateShaderResourceView(t.Get(),&suv,&s1));
            }
        }
        // Probe H.264 High 4:4:4 Predictive (profile=244) actual bitstream SPS vs advertised SetOutputType.
        std::string h264_hw_name="none";
        bool h264_hw_found=false,h264_high_420=false,h264_prof244_accepted=false,h264_true_444=false;
        uint32_t prof244_actual_profile=0,prof244_actual_chroma=0;
        if(gpu_ok){
            MFT_REGISTER_TYPE_INFO out_h264{MFMediaType_Video,MFVideoFormat_H264};
            IMFActivate** acts=nullptr;UINT32 count=0;
            if(SUCCEEDED(MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER,MFT_ENUM_FLAG_HARDWARE|MFT_ENUM_FLAG_SORTANDFILTER,nullptr,&out_h264,&acts,&count))){
                if(count>0){
                    h264_hw_found=true;
                    WCHAR* friendly=nullptr;UINT32 flen=0;
                    if(SUCCEEDED(acts[0]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute,&friendly,&flen))&&friendly){
                        if(flen>0){int nb=WideCharToMultiByte(CP_UTF8,0,friendly,static_cast<int>(flen),nullptr,0,nullptr,nullptr);if(nb>0){h264_hw_name.resize(nb);WideCharToMultiByte(CP_UTF8,0,friendly,static_cast<int>(flen),h264_hw_name.data(),nb,nullptr,nullptr);}}
                        CoTaskMemFree(friendly);
                    }
                    ComPtr<IMFDXGIDeviceManager> mgr;ComPtr<ID3D11Texture2D> nv;ComPtr<IMFTransform> enc;
                    if(SUCCEEDED(acts[0]->ActivateObject(IID_PPV_ARGS(&enc)))&&enc){
                        ComPtr<IMFAttributes> attr;enc->GetAttributes(&attr);UINT32 async=0;if(attr)attr->GetUINT32(MF_TRANSFORM_ASYNC,&async);
                        if(async&&attr)attr->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK,TRUE);
                        UINT tok=0;
                        if(SUCCEEDED(MFCreateDXGIDeviceManager(&tok,&mgr))&&SUCCEEDED(mgr->ResetDevice(graphics.device.Get(),tok)))
                            enc->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,reinterpret_cast<ULONG_PTR>(mgr.Get()));
                        codec_property(enc.Get(),CODECAPI_AVLowLatencyMode,1,true);
                        codec_property(enc.Get(),CODECAPI_AVEncMPVDefaultBPictureCount,0);
                        auto omt_high=video_type(MFVideoFormat_H264,640,360,30);omt_high->SetUINT32(MF_MT_AVG_BITRATE,4000000);omt_high->SetUINT32(MF_MT_MPEG2_PROFILE,eAVEncH264VProfile_High);
                        if(SUCCEEDED(enc->SetOutputType(0,omt_high.Get(),0)))h264_high_420=true;
                        auto omt_244=video_type(MFVideoFormat_H264,640,360,30);omt_244->SetUINT32(MF_MT_AVG_BITRATE,4000000);omt_244->SetUINT32(MF_MT_MPEG2_PROFILE,244u);
                        bool streaming_started=false;
                        if(SUCCEEDED(enc->SetOutputType(0,omt_244.Get(),0))){
                            h264_prof244_accepted=true;
                            auto imt=video_type(MFVideoFormat_NV12,640,360,30);
                            if(SUCCEEDED(enc->SetInputType(0,imt.Get(),0))){
                                ComPtr<IMFMediaEventGenerator> gen;ComPtr<mft_events> pump;
                                try{
                                    if(async&&SUCCEEDED(enc.As(&gen))&&gen){pump.Attach(new mft_events(gen.Get()));pump->start();}
                                    if(SUCCEEDED(enc->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING,0))&&SUCCEEDED(enc->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM,0))){
                                        streaming_started=true;
                                        D3D11_TEXTURE2D_DESC td{};td.Width=640;td.Height=360;td.MipLevels=td.ArraySize=1;td.Format=DXGI_FORMAT_NV12;td.SampleDesc.Count=1;td.BindFlags=D3D11_BIND_RENDER_TARGET;
                                        if(SUCCEEDED(graphics.device->CreateTexture2D(&td,nullptr,&nv))&&nv){
                                            ComPtr<IMFMediaBuffer> mb;ComPtr<IMFSample> smp;
                                            if(SUCCEEDED(MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D),nv.Get(),0,FALSE,&mb))&&mb&&SUCCEEDED(MFCreateSample(&smp))&&smp&&SUCCEEDED(smp->AddBuffer(mb.Get()))){
                                                smp->SetSampleTime(0);smp->SetSampleDuration(333333);
                                                ULONGLONG dl=GetTickCount64()+1500;
                                                if(pump){while(pump->next(dl)!=METransformNeedInput){}}
                                                if(SUCCEEDED(enc->ProcessInput(0,smp.Get(),0))){
                                                    if(pump){while(pump->next(dl)!=METransformHaveOutput){}}
                                                    for(int a=0;a<2;++a){
                                                        try{
                                                            auto out_s=transform_output(enc.Get());
                                                            ComPtr<IMFMediaBuffer> cb_b;
                                                            if(out_s&&SUCCEEDED(out_s->ConvertToContiguousBuffer(&cb_b))&&cb_b){
                                                                BYTE* p=nullptr;DWORD n=0;
                                                                if(SUCCEEDED(cb_b->Lock(&p,nullptr,&n))&&p&&n>0){
                                                                    parse_h264_sps(p,n,prof244_actual_profile,prof244_actual_chroma);
                                                                    cb_b->Unlock();
                                                                    h264_true_444=(prof244_actual_chroma==3);
                                                                }
                                                            }
                                                            break;
                                                        }catch(failure fe){
                                                            if(fe.hr==MF_E_TRANSFORM_STREAM_CHANGE){
                                                                ComPtr<IMFMediaType> cmt;if(SUCCEEDED(enc->GetOutputAvailableType(0,0,&cmt))&&cmt)enc->SetOutputType(0,cmt.Get(),0);
                                                                if(pump){while(pump->next(dl)!=METransformHaveOutput){}}
                                                                continue;
                                                            }
                                                            break;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }catch(...){}
                                if(pump)pump->stop();
                                if(streaming_started)shutdown(enc.Get());
                                else{ComPtr<IMFShutdown> sd;if(SUCCEEDED(enc.As(&sd))&&sd)sd->Shutdown();}
                                if(pump)pump->wait_released();
                                pump.Reset();gen.Reset();enc.Reset();
                            }
                        }
                        if(enc){ComPtr<IMFShutdown> sd;if(SUCCEEDED(enc.As(&sd))&&sd)sd->Shutdown();enc.Reset();}
                        acts[0]->ShutdownObject();
                    }
                    for(UINT32 i=0;i<count;++i)acts[i]->Release();
                }
                if(acts)CoTaskMemFree(acts);
            }
        }
        // Check inbox H.264 decoder 4:4:4 output capability.
        bool h264_decoder_444_output=false;
        {
            ComPtr<IMFTransform> dec;
            if(SUCCEEDED(CoCreateInstance(CLSID_CMSH264DecoderMFT,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&dec)))){
                auto in_mt=video_type(MFVideoFormat_H264,320,192,30);
                if(SUCCEEDED(dec->SetInputType(0,in_mt.Get(),0))){
                    for(DWORD i=0;;++i){
                        ComPtr<IMFMediaType> omt;if(FAILED(dec->GetOutputAvailableType(0,i,&omt)))break;
                        GUID sub{};omt->GetGUID(MF_MT_SUBTYPE,&sub);
                        if(sub==MFVideoFormat_AYUV||sub==MFVideoFormat_ARGB32||sub==MFVideoFormat_RGB32)h264_decoder_444_output=true;
                    }
                }
                ComPtr<IMFShutdown> sd;if(SUCCEEDED(dec.As(&sd))&&sd)sd->Shutdown();
            }
        }
        // Check HEVC hardware encoder 4:4:4 input & hardware decoder presence.
        bool hevc_hw_encoder=false,hevc_rext_444_input=false,hevc_hw_decoder=false;
        if(gpu_ok){
            MFT_REGISTER_TYPE_INFO out_hevc{MFMediaType_Video,MFVideoFormat_HEVC};
            IMFActivate** acts=nullptr;UINT32 count=0;
            if(SUCCEEDED(MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER,MFT_ENUM_FLAG_HARDWARE|MFT_ENUM_FLAG_SORTANDFILTER,nullptr,&out_hevc,&acts,&count))){
                if(count>0){
                    hevc_hw_encoder=true;
                    ComPtr<IMFDXGIDeviceManager> mgr;ComPtr<IMFTransform> enc;
                    if(SUCCEEDED(acts[0]->ActivateObject(IID_PPV_ARGS(&enc)))&&enc){
                        ComPtr<IMFAttributes> attr;enc->GetAttributes(&attr);UINT32 async=0;if(attr)attr->GetUINT32(MF_TRANSFORM_ASYNC,&async);
                        if(async&&attr)attr->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK,TRUE);
                        UINT tok=0;
                        if(SUCCEEDED(MFCreateDXGIDeviceManager(&tok,&mgr))&&SUCCEEDED(mgr->ResetDevice(graphics.device.Get(),tok)))
                            enc->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,reinterpret_cast<ULONG_PTR>(mgr.Get()));
                        auto omt=video_type(MFVideoFormat_HEVC,640,360,30);omt->SetUINT32(MF_MT_AVG_BITRATE,4000000);omt->SetUINT32(MF_MT_MPEG2_PROFILE,4);
                        if(SUCCEEDED(enc->SetOutputType(0,omt.Get(),0))){
                            for(DWORD k=0;k<16;++k){
                                ComPtr<IMFMediaType> imt;if(FAILED(enc->GetInputAvailableType(0,k,&imt)))break;
                                GUID sub{};imt->GetGUID(MF_MT_SUBTYPE,&sub);
                                if(sub==MFVideoFormat_AYUV||sub==MFVideoFormat_ARGB32)hevc_rext_444_input=true;
                            }
                        }
                        ComPtr<IMFShutdown> sd;if(SUCCEEDED(enc.As(&sd))&&sd)sd->Shutdown();enc.Reset();
                        acts[0]->ShutdownObject();
                    }
                    for(UINT32 i=0;i<count;++i)acts[i]->Release();
                }
                if(acts)CoTaskMemFree(acts);
            }
            MFT_REGISTER_TYPE_INFO in_hevc{MFMediaType_Video,MFVideoFormat_HEVC};
            acts=nullptr;count=0;
            if(SUCCEEDED(MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER,MFT_ENUM_FLAG_HARDWARE|MFT_ENUM_FLAG_SORTANDFILTER,&in_hevc,nullptr,&acts,&count))){
                if(count>0){
                    hevc_hw_decoder=true;
                    for(UINT32 i=0;i<count;++i)acts[i]->Release();
                }
                if(acts)CoTaskMemFree(acts);
            }
        }
        std::ostringstream s;
        s<<"{\"d3d11_available\":"<<(gpu_ok?"true":"false")
         <<",\"nv12_planar_rtv\":"<<(nv12_rtv?"true":"false")
         <<",\"nv12_planar_srv\":"<<(nv12_srv?"true":"false")
         <<",\"h264_hw_encoder\":"<<(h264_hw_found?"true":"false")
         <<",\"h264_hw_encoder_name\":"<<json_escape(h264_hw_name)
         <<",\"h264_high_profile_420\":"<<(h264_high_420?"true":"false")
         <<",\"h264_profile244_set_output_accepted\":"<<(h264_prof244_accepted?"true":"false")
         <<",\"h264_profile244_actual_sps_profile_idc\":"<<prof244_actual_profile
         <<",\"h264_profile244_actual_sps_chroma_format_idc\":"<<prof244_actual_chroma
         <<",\"h264_true_444_encode\":"<<(h264_true_444?"true":"false")
         <<",\"h264_inbox_decoder_444_output\":"<<(h264_decoder_444_output?"true":"false")
         <<",\"hevc_hw_encoder\":"<<(hevc_hw_encoder?"true":"false")
         <<",\"hevc_rext_444_input\":"<<(hevc_rext_444_input?"true":"false")
         <<",\"hevc_hw_decoder\":"<<(hevc_hw_decoder?"true":"false")
         <<",\"android_mediacodec_444_compatible\":false}";
        report=s.str();*json=report.c_str();
    });
}
