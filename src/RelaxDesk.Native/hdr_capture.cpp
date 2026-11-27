#include "internal.h"
#include "capture_frame.h"
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>

using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice;

struct hdr_notifications {
    winrt::handle available{CreateEventW(nullptr,FALSE,FALSE,nullptr)};
    std::atomic_bool closed=false;
};

struct hdr_capture {
    GraphicsCaptureItem item{nullptr};
    Direct3D11CaptureFramePool pool{nullptr};
    GraphicsCaptureSession session{nullptr};
    bool embedded_cursor=true;
    std::shared_ptr<hdr_notifications> notifications=std::make_shared<hdr_notifications>();
    GraphicsCaptureItem::Closed_revoker closed_subscription;
    Direct3D11CaptureFramePool::FrameArrived_revoker frame_subscription;

    ~hdr_capture(){
        frame_subscription.revoke();
        closed_subscription.revoke();
        try{if(session)session.Close();}catch(...){}
        try{if(pool)pool.Close();}catch(...){}
    }
};

void capture_context::start_hdr_capture(){
    try{
        if(!GraphicsCaptureSession::IsSupported())check(DXGI_ERROR_UNSUPPORTED,"Windows Graphics Capture HDR unavailable");
        auto capture=std::make_shared<hdr_capture>();
        if(!capture->notifications->available)check(HRESULT_FROM_WIN32(GetLastError()),"HDR capture frame event");
        auto factory=winrt::get_activation_factory<GraphicsCaptureItem,IGraphicsCaptureItemInterop>();
        winrt::check_hresult(factory->CreateForMonitor(output.Monitor,winrt::guid_of<GraphicsCaptureItem>(),winrt::put_abi(capture->item)));
        auto size=capture->item.Size();
        if(size.Width<=0||size.Height<=0||!capture_dimensions(size.Width,size.Height))
            check(DXGI_ERROR_UNSUPPORTED,"HDR capture dimensions exceed capture limit");
        ComPtr<IDXGIDevice> dxgi;check(graphics.device.As(&dxgi),"HDR capture DXGI device");
        winrt::com_ptr<::IInspectable> inspectable;
        winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.Get(),inspectable.put()));
        capture->pool=Direct3D11CaptureFramePool::CreateFreeThreaded(inspectable.as<IDirect3DDevice>(),
            DirectXPixelFormat::R16G16B16A16Float,2,size);
        capture->closed_subscription=capture->item.Closed(winrt::auto_revoke,
            [notifications=capture->notifications](auto const&,auto const&){
                notifications->closed.store(true);SetEvent(notifications->available.get());
            });
        capture->frame_subscription=capture->pool.FrameArrived(winrt::auto_revoke,
            [notifications=capture->notifications](auto const&,auto const&){SetEvent(notifications->available.get());});
        capture->session=capture->pool.CreateCaptureSession(capture->item);
        // Embedded cursors cannot be removed by the Viewer on mouse leave.
        // Windows 10 2004+ supports cursor-free WGC; use the Viewer's local
        // cursor until this backend supplies independent pointer shapes.
        if(auto cursor_session=capture->session.try_as<IGraphicsCaptureSession2>()){
            cursor_session.IsCursorCaptureEnabled(false);
            capture->embedded_cursor=false;
        }
        capture->session.StartCapture();
        hdr_session=std::move(capture);
    }catch(const winrt::hresult_error& error){check(error.code(),"start Windows Graphics Capture FP16");}
}

void capture_context::acquire_hdr(uint32_t timeout,rd_frame& result){
    last_copy_pixels=0;
    try{
        auto deadline=GetTickCount64()+timeout;
        Direct3D11CaptureFrame frame{nullptr};
        for(;;){
            if(hdr_session->notifications->closed.load())check(DXGI_ERROR_ACCESS_LOST,"HDR capture monitor closed");
            frame=hdr_session->pool.TryGetNextFrame();
            if(frame)break;
            auto now=GetTickCount64();
            if(now>=deadline)throw failure{S_FALSE};
            auto wait=WaitForSingleObject(hdr_session->notifications->available.get(),static_cast<DWORD>(deadline-now));
            if(wait==WAIT_TIMEOUT)throw failure{S_FALSE};
            if(wait==WAIT_FAILED)check(HRESULT_FROM_WIN32(GetLastError()),"wait for HDR capture frame");
        }
        auto size=frame.ContentSize();
        auto access=frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        ComPtr<ID3D11Texture2D> pixels;
        winrt::check_hresult(access->GetInterface(IID_PPV_ARGS(&pixels)));
        D3D11_TEXTURE2D_DESC desc{};pixels->GetDesc(&desc);
        if(desc.Format!=DXGI_FORMAT_R16G16B16A16_FLOAT)
            check(DXGI_ERROR_UNSUPPORTED,"Windows Graphics Capture did not supply FP16");
        if(size.Width!=static_cast<int>(desc.Width)||size.Height!=static_cast<int>(desc.Height))
            check(DXGI_ERROR_ACCESS_LOST,"HDR capture resolution changed; recreate capture and encoder");
        ComPtr<IDXGIResource> resource;check(pixels.As(&resource),"HDR capture surface resource");
        DXGI_OUTDUPL_FRAME_INFO info{};info.LastPresentTime.QuadPart=ticks();info.AccumulatedFrames=1;
        // No separate DXGI pointer or dirty metadata is available.
        accept_surface(info,resource.Get());
        result={};result.width=desc.Width;result.height=desc.Height;
        result.timestamp=last_desktop_time;result.accumulated=1;
        result.pointer_type=hdr_session->embedded_cursor?3:0;
    }catch(const winrt::hresult_error& error){check(error.code(),"acquire Windows Graphics Capture FP16 frame");}
}
