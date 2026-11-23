#include "internal.h"
#include <wincodec.h>

namespace {
constexpr uint32_t maximum_pixels=16*1024*1024,maximum_png=8*1024*1024;
struct clipboard_com {
    bool initialized=false;
    clipboard_com(){HRESULT hr=CoInitializeEx(nullptr,COINIT_MULTITHREADED);if(hr!=RPC_E_CHANGED_MODE){check(hr,"clipboard COM initialization");initialized=true;}}
    ~clipboard_com(){if(initialized)CoUninitialize();}
};
void bounds(uint32_t width,uint32_t height){check(width&&height&&width<=8192&&height<=8192&&uint64_t(width)*height<=maximum_pixels?S_OK:E_INVALIDARG,"clipboard image dimensions");}
ComPtr<IWICImagingFactory> factory(){ComPtr<IWICImagingFactory> result;check(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&result)),"clipboard WIC factory");return result;}
}

int32_t rd_clipboard_image_decode(const uint8_t* data,uint32_t bytes,uint32_t format,uint32_t width,uint32_t height,uint8_t* bgra,uint32_t capacity){return guarded([&]{
    bounds(width,height);check(data&&bytes&&bgra&&capacity>=uint64_t(width)*height*4&&(format==1||format==2)&&bytes<=(format==1?maximum_png:maximum_pixels*4+8192)?S_OK:E_INVALIDARG,"clipboard decode arguments");
    clipboard_com lifetime;auto imaging=factory();ComPtr<IWICStream> stream;
    check(imaging->CreateStream(&stream),"clipboard WIC stream");check(stream->InitializeFromMemory(const_cast<BYTE*>(data),bytes),"clipboard memory stream");
    ComPtr<IWICBitmapDecoder> decoder;check(imaging->CreateDecoder(format==1?GUID_ContainerFormatPng:GUID_ContainerFormatBmp,&GUID_VendorMicrosoft,&decoder),"clipboard in-box decoder");
    check(decoder->Initialize(stream.Get(),WICDecodeMetadataCacheOnDemand),"clipboard decoder initialization");
    UINT frames=0;check(decoder->GetFrameCount(&frames),"clipboard frame count");check(frames==1?S_OK:E_INVALIDARG,"clipboard single image required");
    ComPtr<IWICBitmapFrameDecode> frame;check(decoder->GetFrame(0,&frame),"clipboard image frame");UINT actual_width=0,actual_height=0;check(frame->GetSize(&actual_width,&actual_height),"clipboard frame dimensions");
    check(actual_width==width&&actual_height==height?S_OK:E_INVALIDARG,"clipboard image size mismatch");
    ComPtr<IWICFormatConverter> converter;check(imaging->CreateFormatConverter(&converter),"clipboard BGRA converter");
    check(converter->Initialize(frame.Get(),GUID_WICPixelFormat32bppBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom),"clipboard BGRA initialization");
    check(converter->CopyPixels(nullptr,width*4,capacity,bgra),"clipboard bounded pixel decode");
});}

int32_t rd_clipboard_image_encode(const uint8_t* bgra,uint32_t bytes,uint32_t width,uint32_t height,uint8_t* png,uint32_t capacity,uint32_t* written){return guarded([&]{
    bounds(width,height);check(bgra&&bytes==uint64_t(width)*height*4&&png&&written&&capacity&&capacity<=maximum_png?S_OK:E_INVALIDARG,"clipboard encode arguments");*written=0;
    clipboard_com lifetime;auto imaging=factory();ComPtr<IWICStream> stream;check(imaging->CreateStream(&stream),"clipboard encoder stream");
    check(stream->InitializeFromMemory(png,capacity),"clipboard bounded output stream");ComPtr<IWICBitmapEncoder> encoder;
    check(imaging->CreateEncoder(GUID_ContainerFormatPng,&GUID_VendorMicrosoft,&encoder),"clipboard in-box encoder");check(encoder->Initialize(stream.Get(),WICBitmapEncoderNoCache),"clipboard PNG initialization");
    ComPtr<IWICBitmapFrameEncode> frame;ComPtr<IPropertyBag2> options;check(encoder->CreateNewFrame(&frame,&options),"clipboard PNG frame");check(frame->Initialize(options.Get()),"clipboard PNG frame initialization");
    check(frame->SetSize(width,height),"clipboard PNG size");WICPixelFormatGUID pixel=GUID_WICPixelFormat32bppBGRA;check(frame->SetPixelFormat(&pixel),"clipboard PNG pixel format");check(IsEqualGUID(pixel,GUID_WICPixelFormat32bppBGRA)?S_OK:WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT,"clipboard PNG BGRA requirement");
    check(frame->WritePixels(height,width*4,bytes,const_cast<BYTE*>(bgra)),"clipboard bounded PNG encode");check(frame->Commit(),"clipboard PNG frame commit");check(encoder->Commit(),"clipboard PNG commit");
    ULARGE_INTEGER position{};LARGE_INTEGER zero{};check(stream->Seek(zero,STREAM_SEEK_CUR,&position),"clipboard PNG output size");check(position.QuadPart&&position.QuadPart<=capacity?S_OK:E_INVALIDARG,"clipboard PNG byte limit");*written=static_cast<uint32_t>(position.QuadPart);
});}
