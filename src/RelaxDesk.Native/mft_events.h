#pragma once
#include "internal.h"
#include <condition_variable>
#include <deque>
#include <mutex>

// One outstanding BeginGetEvent, no Sleep(1) polling. The transform's Shutdown
// cancels its outstanding callback before the owning context releases the pump.
class mft_events final : public IMFAsyncCallback {
    resource_counter tracked{6};
    std::atomic<ULONG> references{1};
    ComPtr<IMFMediaEventGenerator> generator;
    std::mutex mutex;
    std::condition_variable ready;
    std::deque<ComPtr<IMFMediaEvent>> pending;
    bool stopped=false;
    HRESULT fault=S_OK;
public:
    explicit mft_events(IMFMediaEventGenerator* source):generator(source){}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** object) override {
        if(!object)return E_POINTER;*object=nullptr;
        if(iid!=__uuidof(IUnknown)&&iid!=__uuidof(IMFAsyncCallback))return E_NOINTERFACE;
        *object=static_cast<IMFAsyncCallback*>(this);AddRef();return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override {return ++references;}
    ULONG STDMETHODCALLTYPE Release() override {
        ULONG remaining;
        {std::lock_guard lock(mutex);remaining=--references;if(remaining)ready.notify_all();}
        if(!remaining)delete this;return remaining;
    }
    HRESULT STDMETHODCALLTYPE GetParameters(DWORD*,DWORD*) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE Invoke(IMFAsyncResult* result) override {
        ComPtr<IMFMediaEvent> event;HRESULT hr=generator->EndGetEvent(result,&event);
        {
            std::lock_guard lock(mutex);
            if(stopped)return S_OK;
            if(FAILED(hr)){fault=hr;ready.notify_all();return S_OK;}
            if(pending.size()>=64){fault=HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);ready.notify_all();return S_OK;}
            pending.push_back(event);ready.notify_all();
        }
        hr=generator->BeginGetEvent(this,nullptr);
        if(FAILED(hr)){std::lock_guard lock(mutex);if(!stopped)fault=hr;ready.notify_all();}
        return S_OK;
    }
    void start(){check(generator->BeginGetEvent(this,nullptr),"encoder BeginGetEvent");}
    void stop(){std::lock_guard lock(mutex);stopped=true;pending.clear();ready.notify_all();}
    void wait_released(){
        std::unique_lock lock(mutex);
        ready.wait_for(lock,std::chrono::seconds(2),[&]{return references.load()==1;});
    }
    MediaEventType next(ULONGLONG deadline){
        ComPtr<IMFMediaEvent> event;
        {
            std::unique_lock lock(mutex);
            while(pending.empty()&&!stopped&&SUCCEEDED(fault)){
                ULONGLONG now=GetTickCount64();if(now>=deadline)check(HRESULT_FROM_WIN32(WAIT_TIMEOUT),"hardware encoder event timeout");
                ready.wait_for(lock,std::chrono::milliseconds(deadline-now));
            }
            check(fault,"encoder asynchronous event pump");if(stopped)check(MF_E_SHUTDOWN,"encoder events stopped");
            event=std::move(pending.front());pending.pop_front();
        }
        HRESULT status;check(event->GetStatus(&status),"event status");check(status,"async encoder event failure");
        MediaEventType type;check(event->GetType(&type),"event type");return type;
    }
};
