#include "isteamclient.h"
#include "isteamnetworkingmessages.h"
#include "isteamuser.h"
#include "isteamutils.h"
#include <stdlib.h>
#include <stdio.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

struct Callback {
    HSteamUser user;
    int32_t type;
    const void *data;
    int32_t size;
};

struct Frame {
    uint64 sender;
    int channel;
    uint32 size;
    char bytes[64];
};

const uint64 account_a = 76561197960265729ULL;
const uint64 account_b = 76561197960265730ULL;
static uint64 our_id = 0;
static int fd = -1;
static bool accepted = false;
static SteamAPI_CheckCallbackRegistered_t filter = nullptr;
static SteamNetworkingMessagesSessionRequest_t request{};

static const char *socket_path(uint64 id) {
    return id == account_a ? "mock-a.sock" : "mock-b.sock";
}

static sockaddr_un local_address(uint64 id) {
    sockaddr_un address{};
    address.sun_family = AF_UNIX;
    strcpy(address.sun_path, socket_path(id));
    return address;
}

static void release_packet(SteamNetworkingMessage_t *packet) {
    free(packet->m_pData);
    free(packet);
}

class Messages : public ISteamNetworkingMessages {
public:
    EResult SendMessageToUser(const SteamNetworkingIdentity &id, const void *data, uint32 size,
        int flags, int channel) override {
        Frame frame{};
        if (size > sizeof(frame.bytes) || flags != k_nSteamNetworkingSend_ReliableNoNagle)
            return k_EResultInvalidParam;
        frame.sender = our_id;
        frame.channel = channel;
        frame.size = size;
        memcpy(frame.bytes, data, size);
        const auto to = local_address(id.data.m_steamID64);
        if (sendto(fd, &frame, sizeof(frame), 0, reinterpret_cast<const sockaddr *>(&to), sizeof(to))
            != sizeof(frame))
            return k_EResultNoConnection;
        return k_EResultOK;
    }

    int ReceiveMessagesOnChannel(int channel, SteamNetworkingMessage_t **out, int count) override {
        if (!accepted || count < 1)
            return 0;
        Frame frame{};
        if (recv(fd, &frame, sizeof(frame), MSG_DONTWAIT) != sizeof(frame))
            return 0;
        if (frame.channel != channel || frame.size > sizeof(frame.bytes))
            return 0;
        auto *packet = static_cast<SteamNetworkingMessage_t *>(calloc(1, sizeof(SteamNetworkingMessage_t)));
        packet->m_pData = malloc(frame.size);
        memcpy(packet->m_pData, frame.bytes, frame.size);
        packet->m_cbSize = frame.size;
        packet->m_nChannel = channel;
        packet->m_identityPeer.m_eType = k_ESteamNetworkingIdentityType_SteamID;
        packet->m_identityPeer.m_cbSize = sizeof(frame.sender);
        packet->m_identityPeer.data.m_steamID64 = frame.sender;
        packet->m_pfnRelease = release_packet;
        out[0] = packet;
        return 1;
    }

    bool AcceptSessionWithUser(const SteamNetworkingIdentity &peer) override {
        accepted = peer.data.m_steamID64 == request.m_identityRemote.data.m_steamID64;
        return accepted;
    }
    bool CloseSessionWithUser(const SteamNetworkingIdentity &) override { return true; }
    bool CloseChannelWithUser(const SteamNetworkingIdentity &, int channel) override {
        return channel == 312520;
    }
    ESteamNetworkingConnectionState GetSessionConnectionInfo(const SteamNetworkingIdentity &,
        SteamNetConnectionInfo_t *, SteamNetConnectionRealTimeStatus_t *) override {
        return k_ESteamNetworkingConnectionState_Connected;
    }
};

static Messages messages;
struct Object { void **vtable; };
static void *user_methods[32]{};
static void *utils_methods[38]{};
static void *client_methods[42]{};
static Object steam_user{user_methods};
static Object steam_utils{utils_methods};
static Object steam_client{client_methods};

CSteamID self_id(void *) { return CSteamID(our_id); }
uint32 app_id(void *) { return getenv("RM_MOCK_WRONG_APP") ? 480 : 312520; }
void set_filter(void *, SteamAPI_CheckCallbackRegistered_t f) { filter = f; }
void *get_user(void *, HSteamUser, HSteamPipe, const char *version) {
    return strcmp(version, STEAMUSER_INTERFACE_VERSION) == 0 ? &steam_user : nullptr;
}
void *get_utils(void *, HSteamPipe, const char *version) {
    return strcmp(version, STEAMUTILS_INTERFACE_VERSION) == 0 ? &steam_utils : nullptr;
}
void *generic(void *, HSteamUser, HSteamPipe, const char *version) {
    return strcmp(version, STEAMNETWORKINGMESSAGES_INTERFACE_VERSION) == 0 ? &messages : nullptr;
}

extern "C" void *CreateInterface(const char *version, int *result) {
    if (strcmp(version, STEAMCLIENT_INTERFACE_VERSION) != 0)
        return nullptr;
    user_methods[2] = reinterpret_cast<void *>(self_id);
    utils_methods[9] = reinterpret_cast<void *>(app_id);
    client_methods[5] = reinterpret_cast<void *>(get_user);
    client_methods[9] = reinterpret_cast<void *>(get_utils);
    client_methods[12] = reinterpret_cast<void *>(generic);
    client_methods[34] = reinterpret_cast<void *>(set_filter);
    *result = 0;
    return &steam_client;
}

extern "C" HSteamPipe Steam_CreateSteamPipe() {
    const char *role = getenv("RM_MOCK_ROLE");
    if (!role)
        return 0;
    our_id = strcmp(role, "listen") == 0 ? account_b : account_a;
    fd = socket(AF_UNIX, SOCK_DGRAM, 0);
    const auto self = local_address(our_id);
    if (fd < 0 || bind(fd, reinterpret_cast<const sockaddr *>(&self), sizeof(self))) {
        if (fd >= 0)
            close(fd);
        return 0;
    }
    return 1;
}

extern "C" HSteamUser Steam_ConnectToGlobalUser(HSteamPipe) { return 2; }
extern "C" bool Steam_BLoggedOn(HSteamUser, HSteamPipe) { return true; }
extern "C" void Steam_ReleaseUser(HSteamPipe, HSteamUser) {}
extern "C" bool Steam_BReleaseSteamPipe(HSteamPipe) {
    close(fd);
    unlink(socket_path(our_id));
    return true;
}
extern "C" bool Steam_BGetCallback(HSteamPipe, Callback *callback, int32_t *) {
    if (accepted || !filter || !filter(SteamNetworkingMessagesSessionRequest_t::k_iCallback))
        return false;
    Frame frame{};
    if (recv(fd, &frame, sizeof(frame), MSG_PEEK | MSG_DONTWAIT) != sizeof(frame))
        return false;
    request.m_identityRemote.m_eType = k_ESteamNetworkingIdentityType_SteamID;
    request.m_identityRemote.m_cbSize = sizeof(frame.sender);
    request.m_identityRemote.data.m_steamID64 = frame.sender;
    *callback = {2, SteamNetworkingMessagesSessionRequest_t::k_iCallback, &request, sizeof(request)};
    return true;
}
extern "C" void Steam_FreeLastCallback(HSteamPipe) {}
