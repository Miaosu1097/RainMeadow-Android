#ifdef MEADOW_VALVE_SDR
#include <dlfcn.h>
#include <isteamnetworkingsockets.h>
#include <isteamnetworkingmessages.h>
#include <isteamnetworkingutils.h>
#include <steamnetworkingcustomsignaling.h>
#else
#include <steam/steamnetworkingsockets.h>
#include <steam/isteamnetworkingmessages.h>
#include <steam/isteamnetworkingutils.h>
#include <steam/steamnetworkingcustomsignaling.h>

#include "steamnetworkingsockets/clientlib/csteamnetworkingsockets.h"
#include "steamnetworkingsockets/steamnetworkingsockets_certstore.h"
#include "steamnetworkingsockets_messages.pb.h"
#include "steamnetworkingsockets_messages_certs.pb.h"
#endif

#include <algorithm>
#include <cstring>
#include <cstdio>
#include <deque>
#include <mutex>
#include <vector>

namespace {
#ifdef MEADOW_VALVE_SDR
using InitValve = bool (*)(char*);
using KillValve = void (*)();
using SetNetworkConfig = bool (*)(const void*, size_t, char*);

ISteamNetworkingSockets* valveSockets;
ISteamNetworkingMessages* valveMessages;
ISteamNetworkingUtils* valveUtils;
KillValve killValve;
SetNetworkConfig applyValveConfig;
uint64 valveIdentity;

ISteamNetworkingSockets* Sockets() { return valveSockets; }
ISteamNetworkingMessages* Messages() { return valveMessages; }

template <typename Function>
Function ResolveValve(void* library, const char* name) {
    return reinterpret_cast<Function>(dlsym(library, name));
}
#else
using namespace SteamNetworkingSocketsLib;
ISteamNetworkingSockets* Sockets() { return SteamNetworkingSockets(); }
ISteamNetworkingMessages* Messages() { return SteamNetworkingMessages(); }
#endif
constexpr int maxSignal = 8192;
constexpr int maxPacket = 65536;
constexpr size_t maxSignals = 128;

struct Signal {
    uint64 peer;
    std::vector<unsigned char> bytes;
};

std::mutex queueMutex;
std::deque<Signal> outgoing;
std::vector<uint64> allowed;
bool started;
bool certificateInstalled;

bool IsAllowed(uint64 peer) {
    std::lock_guard<std::mutex> guard(queueMutex);
    return peer != 0 && std::find(allowed.begin(), allowed.end(), peer) != allowed.end();
}

bool Enqueue(uint64 peer, const void* data, int size) {
    if (peer == 0 || size < 1 || size > maxSignal) return false;
    const auto* bytes = static_cast<const unsigned char*>(data);
    std::lock_guard<std::mutex> guard(queueMutex);
    if (outgoing.size() >= maxSignals) return true; // GNS retries dropped rendezvous datagrams.
    outgoing.push_back({ peer, { bytes, bytes + size } });
    return true;
}

class ConnectionSignaling final : public ISteamNetworkingConnectionSignaling {
public:
    bool SendSignal(HSteamNetConnection, const SteamNetConnectionInfo_t& info, const void* data, int size) override {
        return Enqueue(info.m_identityRemote.GetSteamID64(), data, size);
    }
    void Release() override { }
};

ConnectionSignaling signaling;

ISteamNetworkingConnectionSignaling* CreateSignaling(ISteamNetworkingSockets*, const SteamNetworkingIdentity& identity,
    int, int) {
    return IsAllowed(identity.GetSteamID64()) ? &signaling : nullptr;
}

class ReceiveContext final : public ISteamNetworkingSignalingRecvContext {
public:
    ISteamNetworkingConnectionSignaling* OnConnectRequest(HSteamNetConnection, const SteamNetworkingIdentity& identity, int) override {
        return IsAllowed(identity.GetSteamID64()) ? &signaling : nullptr;
    }

    void SendRejectionSignal(const SteamNetworkingIdentity& identity, const void* data, int size) override {
        Enqueue(identity.GetSteamID64(), data, size);
    }
};

void AcceptSession(SteamNetworkingMessagesSessionRequest_t* request) {
    if (IsAllowed(request->m_identityRemote.GetSteamID64()))
        Messages()->AcceptSessionWithUser(request->m_identityRemote);
}
}

#define MEADOW_API extern "C" __attribute__((visibility("default")))

#ifdef MEADOW_VALVE_SDR
MEADOW_API int meadow_transport_init_valve(void* library, uint32 app, uint64 id, SteamNetworkingErrMsg& error) {
    if (started || !library || app == 0 || id == 0) return 0;
    const auto setApp = ResolveValve<void (*)(uint32)>(library, "SteamDatagram_SetAppID");
    const auto setUniverse = ResolveValve<void (*)(bool, EUniverse)>(library, "SteamDatagram_SetUniverse");
    const auto init = ResolveValve<InitValve>(library, "SteamDatagramClient_Init");
    const auto kill = ResolveValve<KillValve>(library, "SteamDatagramClient_Kill");
    const auto sockets = ResolveValve<ISteamNetworkingSockets* (*)()>(library, "SteamNetworkingSockets_LibV13");
    const auto messages = ResolveValve<ISteamNetworkingMessages* (*)()>(library, "SteamNetworkingMessages_LibV2");
    const auto utils = ResolveValve<ISteamNetworkingUtils* (*)()>(library, "SteamNetworkingUtils_LibV4");
    const auto config = ResolveValve<SetNetworkConfig>(library, "SteamDatagram_SetNetworkConfig");
    if (!setApp || !setUniverse || !init || !kill || !sockets || !messages || !utils || !config) {
        std::snprintf(error, sizeof(error), "Valve standalone SDR interfaces are unavailable.");
        return 0;
    }

    setApp(app);
    setUniverse(false, k_EUniversePublic);
    if (!init(error)) return 0;

    auto* networkingUtils = utils();
    auto* networkingSockets = sockets();
    auto* networkingMessages = messages();
    if (!networkingUtils || !networkingSockets || !networkingMessages ||
        !networkingUtils->SetGlobalConfigValuePtr(k_ESteamNetworkingConfig_Callback_CreateConnectionSignaling,
            reinterpret_cast<void*>(CreateSignaling)) ||
        !networkingUtils->SetGlobalCallback_MessagesSessionRequest(AcceptSession)) {
        kill();
        std::snprintf(error, sizeof(error), "Valve could not install the Steam P2P callbacks.");
        return 0;
    }

    valveSockets = networkingSockets;
    valveMessages = networkingMessages;
    valveUtils = networkingUtils;
    killValve = kill;
    applyValveConfig = config;
    valveIdentity = id;
    started = true;
    return 1;
}
#else
MEADOW_API int meadow_transport_init(uint32 app, uint64 id, SteamNetworkingErrMsg& error) {
    if (started || app == 0 || id == 0) return 0;
    SteamNetworkingIdentity identity;
    identity.Clear();
    identity.SetSteamID64(id);
    if (!GameNetworkingSockets_Init(&identity, error)) return 0;
    started = true;
    auto* utils = static_cast<SteamNetworkingSocketsLib::CSteamNetworkingUtils*>(SteamNetworkingUtils());
    utils->SetAppID(app);
    utils->SetGlobalConfigValuePtr(k_ESteamNetworkingConfig_Callback_CreateConnectionSignaling,
        reinterpret_cast<void*>(CreateSignaling));
    utils->SetGlobalCallback_MessagesSessionRequest(AcceptSession);
    utils->SetGlobalConfigValueString(k_ESteamNetworkingConfig_P2P_STUN_ServerList, "stun.l.google.com:19302");
    utils->SetGlobalConfigValueInt32(k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable,
        k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_Private | k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_Public);
    return 1;
}
#endif

MEADOW_API void meadow_transport_close() {
    if (!started) return;
#ifdef MEADOW_VALVE_SDR
    killValve();
    valveSockets = nullptr;
    valveMessages = nullptr;
    valveUtils = nullptr;
#else
    GameNetworkingSockets_Kill();
#endif
    started = false;
    certificateInstalled = false;
    std::lock_guard<std::mutex> guard(queueMutex);
    allowed.clear();
    outgoing.clear();
}

#ifdef MEADOW_VALVE_SDR
MEADOW_API int meadow_transport_sdr_config(const void* config, int size, SteamNetworkingErrMsg& error) {
    if (!started || !config || size <= 0 || size > 1024 * 1024) return 0;
    if (!applyValveConfig(config, static_cast<size_t>(size), error)) return 0;
    valveUtils->CheckPingDataUpToDate(1e10f);
    return 1;
}

MEADOW_API int meadow_transport_relay_status(SteamNetworkingErrMsg& error) {
    if (!started) return k_ESteamNetworkingAvailability_Unknown;
    SteamRelayNetworkStatus_t status{};
    auto availability = valveUtils->GetRelayNetworkStatus(&status);
    std::snprintf(error, sizeof(error), "%s", status.m_debugMsg);
    return availability;
}

MEADOW_API int meadow_transport_cert_request(void* request, int capacity, SteamNetworkingErrMsg& error) {
    if (!started || !request || capacity < 1 || capacity > maxSignal) return 0;
    int size = capacity;
    return Sockets()->GetCertificateRequest(&size, request, error) ? size : 0;
}

MEADOW_API int meadow_transport_set_signed_cert(const void* certificate, int size, SteamNetworkingErrMsg& error) {
    if (!started || !certificate || size < 1 || size > maxSignal) return 0;
    if (!Sockets()->SetCertificate(certificate, size, error)) return 0;
    SteamNetworkingIdentity identity;
    if (!Sockets()->GetIdentity(&identity) || identity.GetSteamID64() != valveIdentity) {
        std::snprintf(error, sizeof(error), "Valve network identity differs from the signed-in Steam account.");
        return 0;
    }
    certificateInstalled = true;
    return 1;
}
#else
MEADOW_API int meadow_transport_key(unsigned char* key, SteamNetworkingErrMsg& error) {
    if (!started) return 0;
    int size = 0;
    if (!SteamNetworkingSockets()->GetCertificateRequest(&size, nullptr, error) || size < 1 || size > maxSignal)
        return 0;

    std::vector<unsigned char> request(size);
    if (!SteamNetworkingSockets()->GetCertificateRequest(&size, request.data(), error)) return 0;
    CMsgSteamDatagramCertificateRequest decoded;
    if (!decoded.ParseFromArray(request.data(), size) || decoded.cert().key_data().size() != 32) return 0;
    std::memcpy(key, decoded.cert().key_data().data(), 32);
    return 1;
}

MEADOW_API int meadow_transport_add_ca(const char* base64, SteamNetworkingErrMsg& error) {
    if (!started || !base64 || strnlen(base64, maxSignal) >= maxSignal) return 0;
    return SteamNetworkingSocketsLib::CertStore_AddCertFromBase64(base64, error) ? 1 : 0;
}

MEADOW_API void meadow_transport_revoke_ca(uint64 key) {
    if (started && key != 0) SteamNetworkingSocketsLib::CertStore_AddKeyRevocation(key);
}

MEADOW_API int meadow_transport_set_cert(const void* certificate, int size,
    uint64 key, const void* signature, SteamNetworkingErrMsg& error) {
    if (!started || size < 1 || size > maxSignal || key == 0 || !certificate || !signature) return 0;
    CMsgSteamDatagramCertificateSigned reply;
    reply.set_cert(certificate, size);
    reply.set_ca_key_id(key);
    reply.set_ca_signature(signature, 64);
    CMsgSteamDatagramCertificate claims;
    if (!SteamNetworkingSocketsLib::CertStore_CheckCert(reply, claims, time(nullptr), error))
        return 0;

    auto envelope = reply.SerializeAsString();
    certificateInstalled = SteamNetworkingSockets()->SetCertificate(envelope.data(), static_cast<int>(envelope.size()), error);
    return certificateInstalled ? 1 : 0;
}
#endif

MEADOW_API void meadow_transport_set_peers(const uint64* ids, int count) {
    std::lock_guard<std::mutex> guard(queueMutex);
    if (!ids || count <= 0 || count > 256) allowed.clear();
    else allowed.assign(ids, ids + count);
}

MEADOW_API int meadow_transport_pop_signal(uint64* peer, void* data, int capacity) {
    std::lock_guard<std::mutex> guard(queueMutex);
    if (outgoing.empty()) return 0;
    auto& signal = outgoing.front();
    int length = static_cast<int>(signal.bytes.size());
    if (length > capacity) return -1;
    *peer = signal.peer;
    std::memcpy(data, signal.bytes.data(), length);
    outgoing.pop_front();
    return length;
}

MEADOW_API int meadow_transport_signal(uint64 source, const void* data, int size) {
    if (!started || !certificateInstalled || size < 1 || size > maxSignal || !IsAllowed(source)) return 0;
#ifndef MEADOW_VALVE_SDR
    CMsgSteamNetworkingP2PRendezvous rendezvous;
    SteamNetworkingIdentity identity;
    if (!rendezvous.ParseFromArray(data, size)
        || !identity.ParseString(rendezvous.from_identity().c_str())
        || identity.GetSteamID64() != source)
        return 0;
#endif
    ReceiveContext context;
    return Sockets()->ReceivedP2PCustomSignal(data, size, &context) ? 1 : 0;
}

MEADOW_API int meadow_transport_send(uint64 peer, int channel, const void* data, int size, int reliable) {
    if (!started || !certificateInstalled || !IsAllowed(peer) || channel < 0 || channel > 1
        || size < 1 || size > maxPacket) return k_EResultInvalidParam;
    SteamNetworkingIdentity identity;
    identity.Clear();
    identity.SetSteamID64(peer);
    return Messages()->SendMessageToUser(identity, data, size,
        reliable ? k_nSteamNetworkingSend_ReliableNoNagle : k_nSteamNetworkingSend_UnreliableNoNagle, channel);
}

MEADOW_API int meadow_transport_read(int channel, uint64* peer, void* data, int capacity) {
    if (!started || !certificateInstalled || channel < 0 || channel > 1) return 0;
    Sockets()->RunCallbacks();
    SteamNetworkingMessage_t* message = nullptr;
    int result = Messages()->ReceiveMessagesOnChannel(channel, &message, 1);
    if (result != 1 || !message) return result <= 0 ? result : -1;

    int size = message->m_cbSize;
    *peer = message->m_identityPeer.GetSteamID64();
    if (size <= 0 || size > capacity || !IsAllowed(*peer)) size = -1;
    else std::memcpy(data, message->m_pData, size);
    message->Release();
    return size;
}

#ifdef MEADOW_VALVE_SDR
MEADOW_API int meadow_transport_state(uint64 peer, int* relayed) {
    *relayed = 0;
#else
MEADOW_API int meadow_transport_state(uint64 peer) {
#endif
    if (!started || !certificateInstalled || !IsAllowed(peer)) return -1;
    SteamNetworkingIdentity identity;
    identity.Clear();
    identity.SetSteamID64(peer);
#ifdef MEADOW_VALVE_SDR
    SteamNetConnectionInfo_t info{};
    auto state = Messages()->GetSessionConnectionInfo(identity, &info, nullptr);
    *relayed = (info.m_nFlags & k_nSteamNetworkConnectionInfoFlags_Relayed) != 0;
    return state;
#else
    return Messages()->GetSessionConnectionInfo(identity, nullptr, nullptr);
#endif
}
