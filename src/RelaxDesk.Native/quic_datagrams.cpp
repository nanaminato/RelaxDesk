#include "third_party/msquic/msquic.h"
#include <wincrypt.h>
#include <array>
#include <atomic>
#include <condition_variable>
#include <cstring>
#include <memory>
#include <mutex>
#include <new>

namespace {
constexpr char Protocol[] = "relaxdesk-video-datagram-v2";
constexpr uint16_t DatagramCap = 1200;
constexpr size_t QueueCap = 256;
constexpr HRESULT Retry = HRESULT_FROM_WIN32(ERROR_RETRY);
constexpr HRESULT Timeout = HRESULT_FROM_WIN32(ERROR_TIMEOUT);

struct Datagram {
    uint16_t length{};
    std::array<uint8_t, DatagramCap> bytes;
};

struct SendBuffer {
    Datagram data{};
    QUIC_BUFFER buffer{};
};

struct Endpoint {
    HMODULE module{};
    const QUIC_API_TABLE* api{};
    HQUIC registration{}, configuration{}, listener{};
    std::atomic<HQUIC> connection{};
    PCCERT_CONTEXT expected{};
    bool server{}, pinned{}, connected{};
    uint16_t sendBudget{};
    HRESULT failure{};
    std::mutex mutex;
    std::condition_variable changed;
    std::array<Datagram, QueueCap> queue;
    size_t start{}, queued{};
    uint64_t queueDrops{}, receivedDatagrams{};
    std::atomic<unsigned> sending{};

    ~Endpoint() {
        if (api) {
            if (listener) api->ListenerClose(listener);
            if (auto peer = connection.load()) api->ConnectionClose(peer);
            if (configuration) api->ConfigurationClose(configuration);
            if (registration) api->RegistrationClose(registration);
            auto close = reinterpret_cast<decltype(&MsQuicClose)>(GetProcAddress(module, "MsQuicClose"));
            close(api);
        }
        if (expected) CertFreeCertificateContext(expected);
        if (module) FreeLibrary(module);
    }
};

QUIC_STATUS QUIC_API onConnection(HQUIC, void* context, QUIC_CONNECTION_EVENT* event) {
    auto& self = *static_cast<Endpoint*>(context);
    if (event->Type == QUIC_CONNECTION_EVENT_DATAGRAM_SEND_STATE_CHANGED) {
        auto& send = event->DATAGRAM_SEND_STATE_CHANGED;
        if (QUIC_DATAGRAM_SEND_STATE_IS_FINAL(send.State)) {
            delete static_cast<SendBuffer*>(send.ClientContext);
            --self.sending;
        }
        return QUIC_STATUS_SUCCESS;
    }

    std::lock_guard lock(self.mutex);
    switch (event->Type) {
    case QUIC_CONNECTION_EVENT_PEER_CERTIFICATE_RECEIVED: {
        auto cert = reinterpret_cast<const QUIC_BUFFER*>(event->PEER_CERTIFICATE_RECEIVED.Certificate);
        if (!self.expected || !cert || cert->Length != self.expected->cbCertEncoded ||
            std::memcmp(cert->Buffer, self.expected->pbCertEncoded, cert->Length) != 0) {
            self.failure = QUIC_STATUS_BAD_CERTIFICATE;
            self.changed.notify_all();
            return QUIC_STATUS_BAD_CERTIFICATE;
        }
        self.pinned = true;
        break;
    }
    case QUIC_CONNECTION_EVENT_CONNECTED:
        self.connected = self.server || self.pinned;
        break;
    case QUIC_CONNECTION_EVENT_DATAGRAM_STATE_CHANGED:
        self.sendBudget = event->DATAGRAM_STATE_CHANGED.SendEnabled ? event->DATAGRAM_STATE_CHANGED.MaxSendLength : 0;
        break;
    case QUIC_CONNECTION_EVENT_DATAGRAM_RECEIVED: {
        auto datagram = event->DATAGRAM_RECEIVED.Buffer;
        if (!self.connected || datagram->Length == 0 || datagram->Length > DatagramCap) break;
        ++self.receivedDatagrams;
        if (self.queued == QueueCap) {
            ++self.queueDrops;
            self.start = (self.start + 1) % QueueCap;
            --self.queued;
        }
        auto& slot = self.queue[(self.start + self.queued++) % QueueCap];
        slot.length = static_cast<uint16_t>(datagram->Length);
        std::memcpy(slot.bytes.data(), datagram->Buffer, slot.length);
        break;
    }
    case QUIC_CONNECTION_EVENT_SHUTDOWN_INITIATED_BY_TRANSPORT:
        if (!self.failure) self.failure = event->SHUTDOWN_INITIATED_BY_TRANSPORT.Status;
        break;
    case QUIC_CONNECTION_EVENT_SHUTDOWN_INITIATED_BY_PEER:
    case QUIC_CONNECTION_EVENT_SHUTDOWN_COMPLETE:
        if (!self.failure) self.failure = HRESULT_FROM_WIN32(ERROR_CONNECTION_ABORTED);
        break;
    default: break;
    }
    self.changed.notify_all();
    return QUIC_STATUS_SUCCESS;
}

QUIC_STATUS QUIC_API onListener(HQUIC, void* context, QUIC_LISTENER_EVENT* event) {
    if (event->Type != QUIC_LISTENER_EVENT_NEW_CONNECTION) return QUIC_STATUS_SUCCESS;
    auto& self = *static_cast<Endpoint*>(context);
    HQUIC connection = event->NEW_CONNECTION.Connection;
    HQUIC empty{};
    if (!self.connection.compare_exchange_strong(empty, connection)) return QUIC_STATUS_CONNECTION_REFUSED;
    self.api->SetCallbackHandler(connection, reinterpret_cast<void*>(onConnection), context);
    QUIC_STATUS status = self.api->ConnectionSetConfiguration(connection, self.configuration);
    if (QUIC_FAILED(status)) self.connection.store(nullptr);
    return status;
}

HRESULT loadApi(Endpoint& self, const wchar_t* dll) {
    self.module = LoadLibraryExW(dll, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!self.module) return HRESULT_FROM_WIN32(GetLastError());
    auto open = reinterpret_cast<decltype(&MsQuicOpenVersion)>(GetProcAddress(self.module, "MsQuicOpenVersion"));
    if (!open || !GetProcAddress(self.module, "MsQuicClose")) return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
    return open(QUIC_API_VERSION_2, reinterpret_cast<const void**>(&self.api));
}

HRESULT configure(Endpoint& self, const wchar_t* dll, PCCERT_CONTEXT certificate, QUIC_BUFFER& alpn,
    QUIC_CERTIFICATE_PKCS12* serverPfx) {
    HRESULT result = loadApi(self, dll);
    if (FAILED(result)) return result;
    result = self.api->RegistrationOpen(nullptr, &self.registration);
    if (FAILED(result)) return result;
    QUIC_SETTINGS settings{};
    settings.IsSet.DatagramReceiveEnabled = TRUE;
    settings.DatagramReceiveEnabled = TRUE;
    settings.IsSet.IdleTimeoutMs = TRUE;
    settings.IdleTimeoutMs = 8000;
    settings.IsSet.KeepAliveIntervalMs = TRUE;
    settings.KeepAliveIntervalMs = 1500;
    result = self.api->ConfigurationOpen(self.registration, &alpn, 1, &settings, sizeof(settings), nullptr, &self.configuration);
    if (FAILED(result)) return result;
    QUIC_CREDENTIAL_CONFIG credentials{};
    if (self.server && serverPfx) {
        credentials.Type = QUIC_CREDENTIAL_TYPE_CERTIFICATE_PKCS12;
        credentials.CertificatePkcs12 = serverPfx;
    } else if (self.server) {
        credentials.Type = QUIC_CREDENTIAL_TYPE_CERTIFICATE_CONTEXT;
        credentials.CertificateContext = const_cast<CERT_CONTEXT*>(certificate);
    } else {
        self.expected = CertDuplicateCertificateContext(certificate);
        if (!self.expected) return E_OUTOFMEMORY;
        credentials.Type = QUIC_CREDENTIAL_TYPE_NONE;
        credentials.Flags = QUIC_CREDENTIAL_FLAG_CLIENT | QUIC_CREDENTIAL_FLAG_NO_CERTIFICATE_VALIDATION |
            QUIC_CREDENTIAL_FLAG_INDICATE_CERTIFICATE_RECEIVED | QUIC_CREDENTIAL_FLAG_USE_PORTABLE_CERTIFICATES;
    }
    return self.api->ConfigurationLoadCredential(self.configuration, &credentials);
}
}

extern "C" __declspec(dllexport) HRESULT rd_quic_tls_provider(const wchar_t* runtimeDll, uint32_t* output) {
    if (!runtimeDll || !output) return E_INVALIDARG;
    Endpoint probe;
    HRESULT result = loadApi(probe, runtimeDll);
    if (FAILED(result)) return result;
    QUIC_TLS_PROVIDER provider{};
    uint32_t size = sizeof(provider);
    result = probe.api->GetParam(nullptr, QUIC_PARAM_GLOBAL_TLS_PROVIDER, &size, &provider);
    if (FAILED(result)) return result;
    if (size != sizeof(provider)) return E_UNEXPECTED;
    *output = static_cast<uint32_t>(provider);
    return S_OK;
}

static HRESULT openEndpoint(const wchar_t* runtimeDll, PCCERT_CONTEXT certificate,
    const char* address, uint16_t* port, int server, Endpoint** output, QUIC_CERTIFICATE_PKCS12* serverPfx) {
    if (!runtimeDll || !certificate || !address || !port || !output || (!server && !*port)) return E_INVALIDARG;
    *output = nullptr;
    QUIC_ADDR local{};
    if (InetPtonA(AF_INET, address, &local.Ipv4.sin_addr) == 1)
        local.Ipv4.sin_family = AF_INET;
    else if (InetPtonA(AF_INET6, address, &local.Ipv6.sin6_addr) == 1)
        local.Ipv6.sin6_family = AF_INET6;
    else
        return E_INVALIDARG;
    QuicAddrSetPort(&local, *port);
    auto self = std::unique_ptr<Endpoint>(new (std::nothrow) Endpoint);
    if (!self) return E_OUTOFMEMORY;
    self->server = server != 0;
    QUIC_BUFFER alpn{ sizeof(Protocol) - 1, reinterpret_cast<uint8_t*>(const_cast<char*>(Protocol)) };
    HRESULT result = configure(*self, runtimeDll, certificate, alpn, serverPfx);
    if (FAILED(result)) return result;
    if (self->server) {
        result = self->api->ListenerOpen(self->registration, onListener, self.get(), &self->listener);
        if (FAILED(result)) return result;
        result = self->api->ListenerStart(self->listener, &alpn, 1, &local);
        if (FAILED(result)) return result;
        uint32_t size = sizeof(local);
        result = self->api->GetParam(self->listener, QUIC_PARAM_LISTENER_LOCAL_ADDRESS, &size, &local);
        if (FAILED(result)) return result;
        *port = QuicAddrGetPort(&local);
    } else {
        HQUIC connection{};
        result = self->api->ConnectionOpen(self->registration, onConnection, self.get(), &connection);
        if (FAILED(result)) return result;
        self->connection.store(connection);
        result = self->api->ConnectionStart(connection, self->configuration, QuicAddrGetFamily(&local), address, *port);
        if (FAILED(result)) return result;
    }
    *output = self.release();
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT rd_quic_open(const wchar_t* runtimeDll, PCCERT_CONTEXT certificate,
    const char* address, uint16_t* port, int server, Endpoint** output) {
    return openEndpoint(runtimeDll, certificate, address, port, server, output, nullptr);
}

extern "C" __declspec(dllexport) HRESULT rd_quic_open_pfx(const wchar_t* runtimeDll, PCCERT_CONTEXT certificate,
    const char* address, uint16_t* port, const uint8_t* pfx, uint32_t length, const char* password, Endpoint** output) {
    if (output) *output = nullptr;
    if (!pfx || !length || length > 65536 || !password) return E_INVALIDARG;
    QUIC_CERTIFICATE_PKCS12 credentials{pfx, length, password};
    return openEndpoint(runtimeDll, certificate, address, port, 1, output, &credentials);
}

extern "C" __declspec(dllexport) HRESULT rd_quic_ready(Endpoint* self, uint16_t* budget) {
    if (!self || !budget) return E_INVALIDARG;
    std::lock_guard lock(self->mutex);
    if (self->failure) return self->failure;
    if (!self->connected || self->sendBudget == 0) return Retry;
    *budget = (std::min)(self->sendBudget, DatagramCap);
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT rd_quic_send(Endpoint* self, const uint8_t* data, uint16_t length) {
    if (!data || !length || length > DatagramCap) return E_INVALIDARG;
    uint16_t budget{};
    HRESULT status = rd_quic_ready(self, &budget);
    if (FAILED(status)) return status;
    if (length > budget) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    if (self->sending.fetch_add(1) >= QueueCap) {
        --self->sending;
        return Retry;
    }
    auto* packet = new (std::nothrow) SendBuffer;
    if (!packet) { --self->sending; return E_OUTOFMEMORY; }
    std::memcpy(packet->data.bytes.data(), data, length);
    packet->buffer = { length, packet->data.bytes.data() };
    status = self->api->DatagramSend(self->connection.load(), &packet->buffer, 1, QUIC_SEND_FLAG_NONE, packet);
    if (FAILED(status)) {
        delete packet;
        --self->sending;
    }
    return status;
}

extern "C" __declspec(dllexport) HRESULT rd_quic_receive(Endpoint* self, uint8_t* data, uint16_t capacity,
    uint16_t* length, uint32_t milliseconds) {
    if (!self || !data || !length || milliseconds > 1000) return E_INVALIDARG;
    std::unique_lock lock(self->mutex);
    if (!self->changed.wait_for(lock, std::chrono::milliseconds(milliseconds),
        [&] { return self->queued || self->failure; })) return Timeout;
    if (self->failure) return self->failure;
    auto& slot = self->queue[self->start];
    if (capacity < slot.length) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    *length = slot.length;
    std::memcpy(data, slot.bytes.data(), slot.length);
    self->start = (self->start + 1) % QueueCap;
    --self->queued;
    return S_OK;
}

struct PathStatistics {
    uint32_t rtt, variation;
    uint64_t received, reordered, queueDrops, sent, suspectedLost, spuriousLost;
};

extern "C" __declspec(dllexport) HRESULT rd_quic_statistics_v2(Endpoint* self, PathStatistics* output) {
    if (!self || !output) return E_INVALIDARG;
    uint64_t drops{};
    {
        std::lock_guard lock(self->mutex);
        if (!self->connected || self->failure || !self->receivedDatagrams) return S_FALSE;
        drops = self->queueDrops;
    }
    QUIC_STATISTICS_V2 stats{};
    uint32_t size = sizeof(stats);
    if (FAILED(self->api->GetParam(self->connection.load(), QUIC_PARAM_CONN_STATISTICS_V2, &size, &stats)) ||
        size < QUIC_STATISTICS_V2_SIZE_1) return S_FALSE;
    uint32_t variation = size >= QUIC_STATISTICS_V2_SIZE_4 ? stats.RttVariance : UINT32_MAX;
    *output = { stats.Rtt, variation, stats.RecvTotalPackets, stats.RecvReorderedPackets, drops, stats.SendTotalPackets, stats.SendSuspectedLostPackets, stats.SendSpuriousLostPackets };
    return S_OK;
}

extern "C" __declspec(dllexport) void rd_quic_close(Endpoint* self) { delete self; }
