#include "internal.h"
#include <d3dcompiler.h>
#include <algorithm>
#include <cmath>
#include <sstream>

// FP16 duplication is linear scRGB, 1.0 = 80 nits.
// Convert to linear BT.709 relative to Windows SDR
// reference white, then use a hue-preserving shoulder and sRGB encoding.
static const char* shader=R"(
cbuffer Settings : register(b0) { uint kind; float whiteNits; float2 unused; };
Texture2D<float4> image : register(t0);
float4 vs(uint id : SV_VertexID) : SV_Position {
    return float4(id==0?float2(-1,-1):id==1?float2(-1,3):float2(3,-1),0,1);
}
float3 srgb(float3 v) { return lerp(12.92*v,1.055*pow(max(v,0),1.0/2.4)-.055,step(.0031308,v)); }
float4 ps(float4 position : SV_Position) : SV_Target {
    float3 v=image.Load(int3(int2(position.xy),0)).rgb;
    v=float3(isfinite(v.r)?v.r:0,isfinite(v.g)?v.g:0,isfinite(v.b)?v.b:0);
    if(kind==0) return float4(saturate(v),1); // encoded SDR UNORM, no second gamma
    if(kind==3) return float4(saturate(srgb(max(v,0))),1); // sRGB view decoded by hardware
    v=v*80/max(whiteNits,1); // scRGB
    v=max(v,0);
    float peak=max(v.r,max(v.g,v.b));
    if(peak>.8) { float x=peak-.8; float mapped=.8+x/(1+x/.2); v*=mapped/peak; }
    return float4(saturate(srgb(v)),1);
})";

void desktop_converter::convert(gpu& graphics,ID3D11Texture2D* source,ID3D11Texture2D* target,const desktop_color& color){
    D3D11_TEXTURE2D_DESC input{},output{};source->GetDesc(&input);target->GetDesc(&output);
    if(input.Width!=output.Width||input.Height!=output.Height)check(E_INVALIDARG,"desktop conversion dimensions");
    if(input.Format==DXGI_FORMAT_R10G10B10A2_UNORM&&color.hdr)
        check(DXGI_ERROR_UNSUPPORTED,"ambiguous RGB10 HDR capture: transfer function unknown; scRGB required");
    if(!vertex){
        ComPtr<ID3DBlob> vs,ps,error;
        check(D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"vs","vs_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&vs,&error),"desktop conversion vertex shader");
        check(D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"ps","ps_4_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&ps,&error),"desktop HDR conversion pixel shader");
        check(graphics.device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex),"desktop conversion VS");
        check(graphics.device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel),"desktop conversion PS");
        D3D11_BUFFER_DESC d{};d.ByteWidth=16;d.Usage=D3D11_USAGE_DEFAULT;d.BindFlags=D3D11_BIND_CONSTANT_BUFFER;check(graphics.device->CreateBuffer(&d,nullptr,&constants),"desktop color constants");
    }
    D3D11_TEXTURE2D_DESC cached{};if(readable)readable->GetDesc(&cached);
    if(!readable||cached.Width!=input.Width||cached.Height!=input.Height||cached.Format!=input.Format){
        view.Reset();readable.Reset();auto d=input;d.BindFlags=D3D11_BIND_SHADER_RESOURCE;d.Usage=D3D11_USAGE_DEFAULT;d.CPUAccessFlags=d.MiscFlags=0;
        check(graphics.device->CreateTexture2D(&d,nullptr,&readable),"desktop shader-readable texture");
        check(graphics.device->CreateShaderResourceView(readable.Get(),nullptr,&view),"desktop shader resource view");
    }
    ComPtr<ID3D10Multithread> multithread;graphics.commands.As(&multithread);
    struct command_guard { ID3D10Multithread* p;explicit command_guard(ID3D10Multithread* value):p(value){if(p)p->Enter();}~command_guard(){if(p)p->Leave();} } guard(multithread.Get());
    // Duplication surfaces need not be shader-readable. One GPU copy supplies
    // that binding; no Map/readback/byte[] occurs on this production path.
    graphics.commands->CopyResource(readable.Get(),source);
    ComPtr<ID3D11RenderTargetView> target_view;check(graphics.device->CreateRenderTargetView(target,nullptr,&target_view),"desktop BGRA output view");
    UINT kind=input.Format==DXGI_FORMAT_R16G16B16A16_FLOAT?1:
        input.Format==DXGI_FORMAT_R8G8B8A8_UNORM_SRGB||input.Format==DXGI_FORMAT_B8G8R8A8_UNORM_SRGB||input.Format==DXGI_FORMAT_B8G8R8X8_UNORM_SRGB?3:0;
    struct settings{UINT kind;float white;float unused[2];} settings{kind,std::clamp(color.white_nits,1.0f,10000.0f),{0,0}};
    graphics.commands->UpdateSubresource(constants.Get(),0,nullptr,&settings,0,0);
    graphics.commands->ClearState();
    auto* rtv=target_view.Get();graphics.commands->OMSetRenderTargets(1,&rtv,nullptr);
    graphics.commands->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);graphics.commands->VSSetShader(vertex.Get(),nullptr,0);graphics.commands->PSSetShader(pixel.Get(),nullptr,0);
    auto* buffer=constants.Get();graphics.commands->PSSetConstantBuffers(0,1,&buffer);auto* srv=view.Get();graphics.commands->PSSetShaderResources(0,1,&srv);
    D3D11_VIEWPORT viewport{0,0,static_cast<float>(output.Width),static_cast<float>(output.Height),0,1};graphics.commands->RSSetViewports(1,&viewport);graphics.commands->Draw(3,0);
    ID3D11ShaderResourceView* empty=nullptr;graphics.commands->PSSetShaderResources(0,1,&empty);graphics.commands->OMSetRenderTargets(0,nullptr,nullptr);
    graphics.commands->ClearState();
}

void capture_context::refresh_color(){
    color_checked=GetTickCount64();color={};
    if(color_output){DXGI_OUTPUT_DESC1 d{};if(SUCCEEDED(color_output->GetDesc1(&d))){color.output_space=d.ColorSpace;color.hdr=d.ColorSpace==DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;}}
    UINT32 count=0,modes=0;if(GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS,&count,&modes)!=ERROR_SUCCESS)return;
    std::vector<DISPLAYCONFIG_PATH_INFO> paths(count);std::vector<DISPLAYCONFIG_MODE_INFO> mode(modes);
    if(QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS,&count,paths.data(),&modes,mode.data(),nullptr)!=ERROR_SUCCESS)return;
    for(UINT32 i=0;i<count;++i){
        DISPLAYCONFIG_SOURCE_DEVICE_NAME name{};name.header={DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,sizeof(name),paths[i].sourceInfo.adapterId,paths[i].sourceInfo.id};
        if(DisplayConfigGetDeviceInfo(&name.header)!=ERROR_SUCCESS||_wcsicmp(name.viewGdiDeviceName,output.DeviceName)!=0)continue;
        DISPLAYCONFIG_SDR_WHITE_LEVEL white{};white.header={DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL,sizeof(white),paths[i].targetInfo.adapterId,paths[i].targetInfo.id};
        if(DisplayConfigGetDeviceInfo(&white.header)==ERROR_SUCCESS&&white.SDRWhiteLevel>0){color.white_nits=std::clamp(80.0f*white.SDRWhiteLevel/1000.0f,1.0f,10000.0f);color.white_known=true;}break;
    }
}

RD int32_t rd_capture_color_status(void* context,const char** json){if(!context||!json)return E_INVALIDARG;static thread_local std::string text;return guarded([&]{
    auto& c=*static_cast<capture_context*>(context);c.lifetime.verify();std::ostringstream s;
    const char* transform=c.source_format==DXGI_FORMAT_R16G16B16A16_FLOAT?"scRGB-to-SDR":"SDR";
    s<<"{\"source_format\":"<<c.source_format<<",\"output_format\":87,\"hdr_active\":"<<(c.color.hdr?"true":"false")<<",\"sdr_white_nits\":"<<c.color.white_nits<<",\"white_level_known\":"<<(c.color.white_known?"true":"false")<<",\"capture_api\":\""<<(c.hdr_session?"Windows.Graphics.Capture":"DesktopDuplication")<<"\",\"transform\":\""<<transform<<"\"}";text=s.str();*json=text.c_str();
});}
