#pragma once
#include <windows.h>
#include <dxgi.h>

// Only S_OK means that a real Present succeeded. A successful HRESULT alone
// must not turn status notifications or TEST calls into displayed-frame counts.
struct present_state {
    bool idle=false;
    ULONGLONG next_test=0;
    static constexpr ULONGLONG interval=100;
    bool test_due(ULONGLONG now) const { return idle && now>=next_test; }
    void presented(HRESULT hr,ULONGLONG now) {
        if(hr==DXGI_STATUS_OCCLUDED){idle=true;next_test=now+interval;}
    }
    bool tested(HRESULT hr,ULONGLONG now) {
        if(hr==S_OK){idle=false;return true;}
        next_test=now+interval;return false;
    }
};
