#pragma once
#include <dxgi1_2.h>
#include <utility>

enum class capture_action { desktop, cached, wait };
inline capture_action classify_capture(const DXGI_OUTDUPL_FRAME_INFO& info,bool cached) {
    // DXGI explicitly says all three are zero when only the pointer changed.
    bool desktop=info.LastPresentTime.QuadPart!=0||info.AccumulatedFrames!=0||info.TotalMetadataBufferSize!=0;
    return desktop?capture_action::desktop:cached?capture_action::cached:capture_action::wait;
}
inline bool capture_dimensions(UINT w,UINT h) {
    return w>0&&h>0&&w<=8192&&h<=8192&&static_cast<UINT64>(w)*h<=8294400;
}
template<class Owner> struct capture_lease {
    Owner* owner;
    explicit capture_lease(Owner* value):owner(value){}
    capture_lease(const capture_lease&)=delete;
    ~capture_lease(){if(owner)owner->ReleaseFrame();}
    HRESULT finish(){auto* value=std::exchange(owner,nullptr);return value?value->ReleaseFrame():E_UNEXPECTED;}
};
