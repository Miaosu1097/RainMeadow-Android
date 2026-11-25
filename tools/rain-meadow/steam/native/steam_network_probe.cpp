#include "isteamclient.h"
#include "isteamnetworkingmessages.h"
#include "isteamnetworkingutils.h"
#include "isteamuser.h"
#include "isteamutils.h"
#include "socket_loopback.hpp"

#include <dlfcn.h>
#include <errno.h>
#include <inttypes.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <time.h>
#include <unistd.h>

namespace {
constexpr AppId_t rain_world_app = 312520;
constexpr int probe_channel = 312520;
constexpr uint64_t timeout_ms = 45000;
constexpr int send_flags = k_nSteamNetworkingSend_ReliableNoNagle;
volatile sig_atomic_t interrupted = 0;

struct Callback {
    HSteamUser user;
    int32_t type;
    const void *data;
    int32_t size;
};

struct Client {
    void *library = nullptr;
    void *(*create_interface)(const char *, int *) = nullptr;
    HSteamPipe (*create_pipe)() = nullptr;
    HSteamUser (*connect_user)(HSteamPipe) = nullptr;
    bool (*logged_on)(HSteamUser, HSteamPipe) = nullptr;
    void (*release_user)(HSteamPipe, HSteamUser) = nullptr;
    bool (*release_pipe)(HSteamPipe) = nullptr;
    bool (*get_callback)(HSteamPipe, Callback *, int32_t *) = nullptr;
    void (*free_callback)(HSteamPipe) = nullptr;
    HSteamPipe pipe = 0;
    HSteamUser user = 0;

    ~Client() {
        if (user)
            release_user(pipe, user);
        if (pipe)
            release_pipe(pipe);
        if (library)
            dlclose(library);
    }

    template <class Function>
    bool import(Function &function, const char *name) {
        function = reinterpret_cast<Function>(dlsym(library, name));
        if (!function)
            fprintf(stderr, "Steam client export missing: %s\n", name);
        return function != nullptr;
    }

    bool load(const char *path) {
        library = dlopen(path, RTLD_NOW | RTLD_LOCAL);
        if (!library) {
            fprintf(stderr, "Cannot load Steam client library: %s\n", dlerror());
            return false;
        }
        return import(create_interface, "CreateInterface")
            && import(create_pipe, "Steam_CreateSteamPipe")
            && import(connect_user, "Steam_ConnectToGlobalUser")
            && import(logged_on, "Steam_BLoggedOn")
            && import(release_user, "Steam_ReleaseUser")
            && import(release_pipe, "Steam_BReleaseSteamPipe")
            && import(get_callback, "Steam_BGetCallback")
            && import(free_callback, "Steam_FreeLastCallback");
    }

    bool attach() {
        pipe = create_pipe();
        if (pipe)
            user = connect_user(pipe);
        return user && logged_on(user, pipe);
    }
};

enum class Mode { Inspect, Check, Loopback, Ping, Listen };

void on_interrupt(int) {
    interrupted = 1;
}

uint64_t milliseconds() {
    timespec t{};
    clock_gettime(CLOCK_MONOTONIC, &t);
    return uint64_t(t.tv_sec) * 1000 + uint64_t(t.tv_nsec) / 1000000;
}

void pause_frame() {
    timespec delay{0, 20000000};
    nanosleep(&delay, nullptr);
}

bool parse_peer(const char *value, uint64_t &id) {
    if (!value[0] || strspn(value, "0123456789") != strlen(value))
        return false;
    errno = 0;
    id = strtoull(value, nullptr, 10);
    CSteamID account(static_cast<uint64>(id));
    return errno == 0 && account.IsValid() && account.BIndividualAccount();
}

uint32_t callback_registered(int32_t type) {
    return type == SteamNetworkingMessagesSessionRequest_t::k_iCallback
        || type == SteamNetworkingMessagesSessionFailed_t::k_iCallback;
}

class CallbackRegistration : ISteamClient {
public:
    static void configure(ISteamClient &client) {
        const auto register_filter = &CallbackRegistration::Set_SteamAPI_CCheckCallbackRegisteredInProcess;
        (client.*register_filter)(callback_registered);
    }
};

// The headers in the pinned Proton tree omit the SDK's inline networking helpers.
bool is_peer(const SteamNetworkingIdentity &identity, const SteamNetworkingIdentity &peer) {
    return identity.m_eType == k_ESteamNetworkingIdentityType_SteamID
        && identity.m_cbSize == sizeof(identity.data.m_steamID64)
        && identity.data.m_steamID64 == peer.data.m_steamID64;
}

bool pump(Client &client, ISteamNetworkingMessages &messages, const SteamNetworkingIdentity &peer) {
    for (int i = 0; i < 32; ++i) {
        Callback event{};
        int32_t call = 0;
        if (!client.get_callback(client.pipe, &event, &call))
            break;

        bool failed = false;
        if (event.data && event.user == client.user) {
            if (event.type == SteamNetworkingMessagesSessionRequest_t::k_iCallback
                && event.size == sizeof(SteamNetworkingMessagesSessionRequest_t)) {
                const auto &request = *static_cast<const SteamNetworkingMessagesSessionRequest_t *>(event.data);
                if (is_peer(request.m_identityRemote, peer))
                    messages.AcceptSessionWithUser(peer);
            }
            if (event.type == SteamNetworkingMessagesSessionFailed_t::k_iCallback
                && event.size == sizeof(SteamNetworkingMessagesSessionFailed_t)) {
                const auto &failure = *static_cast<const SteamNetworkingMessagesSessionFailed_t *>(event.data);
                if (is_peer(failure.m_info.m_identityRemote, peer)) {
                    fprintf(stderr, "Steam peer session failed (reason %d).\n", failure.m_info.m_eEndReason);
                    failed = true;
                }
            }
        }
        client.free_callback(client.pipe);
        if (failed)
            return false;
    }
    return true;
}

constexpr char ping_prefix[] = "RainMeadow-Probe/1/";
constexpr size_t prefix_size = sizeof(ping_prefix) - 1;
constexpr size_t ping_size = prefix_size + 16;

bool is_probe(const SteamNetworkingMessage_t &packet, const SteamNetworkingIdentity &peer) {
    if (!is_peer(packet.m_identityPeer, peer) || packet.m_nChannel != probe_channel
        || packet.m_cbSize != ping_size || !packet.m_pData)
        return false;
    const auto *payload = static_cast<const char *>(packet.m_pData);
    if (memcmp(payload, ping_prefix, prefix_size) != 0)
        return false;
    for (size_t i = prefix_size; i < ping_size; ++i) {
        if (!memchr("0123456789abcdef", payload[i], 16))
            return false;
    }
    return true;
}

int exchange(Client &client, ISteamNetworkingMessages &messages, const SteamNetworkingIdentity &peer, Mode mode) {
    char nonce[ping_size + 1]{};
    if (mode == Mode::Ping) {
        const uint64_t id = (milliseconds() << 16) ^ uint64_t(getpid());
        snprintf(nonce, sizeof(nonce), "%s%016" PRIx64, ping_prefix, id);
        EResult sent = messages.SendMessageToUser(peer, nonce, ping_size, send_flags, probe_channel);
        if (sent != k_EResultOK) {
            fprintf(stderr, "Steam refused the probe packet (EResult %d).\n", sent);
            return 6;
        }
    } else {
        printf("Waiting up to 45 seconds for the specified peer.\n");
    }

    const uint64_t deadline = milliseconds() + timeout_ms;
    uint64_t echo_sent_at = 0;
    while (!interrupted && milliseconds() < deadline) {
        if (!pump(client, messages, peer))
            return echo_sent_at ? 0 : 6;

        SteamNetworkingMessage_t *packets[8]{};
        const int count = messages.ReceiveMessagesOnChannel(probe_channel, packets, 8);
        if (count < 0 || count > 8) {
            fprintf(stderr, "Steam receive returned %d.\n", count);
            return 6;
        }
        bool echoed = false;
        EResult response = k_EResultOK;
        for (int i = 0; i < count; ++i) {
            auto *packet = packets[i];
            if (!packet)
                continue;
            if (is_probe(*packet, peer)) {
                if (mode == Mode::Ping)
                    echoed |= memcmp(packet->m_pData, nonce, ping_size) == 0;
                else if (!echo_sent_at) {
                    response = messages.SendMessageToUser(peer, packet->m_pData, ping_size, send_flags, probe_channel);
                    if (response == k_EResultOK) {
                        echo_sent_at = milliseconds();
                        printf("Probe request received and echo queued.\n");
                    }
                }
            }
            packet->m_pfnRelease(packet);
        }
        if (echoed) {
            printf("Steam peer round trip verified.\n");
            return 0;
        }
        if (response != k_EResultOK) {
            fprintf(stderr, "Steam refused the echo packet (EResult %d).\n", response);
            return 6;
        }
        if (echo_sent_at && milliseconds() - echo_sent_at >= 3000)
            return 0;
        pause_frame();
    }
    if (echo_sent_at)
        return 0;
    fprintf(stderr, interrupted ? "Probe interrupted.\n" : "Steam peer probe timed out.\n");
    return interrupted ? 130 : 7;
}
}

int main(int argc, char **argv) {
    setvbuf(stdout, nullptr, _IONBF, 0);
    Mode mode = Mode::Inspect;
    uint64_t target = 0;
    if (argc == 3 && strcmp(argv[2], "inspect") == 0) {
        mode = Mode::Inspect;
    } else if (argc == 3 && strcmp(argv[2], "check") == 0) {
        mode = Mode::Check;
    } else if (argc == 3 && strcmp(argv[2], "loopback") == 0) {
        mode = Mode::Loopback;
    } else if (argc == 4 && (strcmp(argv[2], "ping") == 0 || strcmp(argv[2], "listen") == 0)
        && parse_peer(argv[3], target)) {
        mode = strcmp(argv[2], "ping") == 0 ? Mode::Ping : Mode::Listen;
    } else {
        fprintf(stderr, "Usage: %s STEAMCLIENT_LIBRARY inspect|check|loopback|ping STEAMID64|listen STEAMID64\n", argv[0]);
        return 2;
    }

    if (setenv("SteamAppId", "312520", 1) || setenv("SteamGameId", "312520", 1)) {
        perror("Steam application environment");
        return 2;
    }

    Client client;
    if (!client.load(argv[1]))
        return 3;

    if (mode == Mode::Inspect) {
        int factory_status = 0;
        if (!client.create_interface(STEAMCLIENT_INTERFACE_VERSION, &factory_status)) {
            fprintf(stderr, "Native Steam rejected %s (factory result %d).\n",
                STEAMCLIENT_INTERFACE_VERSION, factory_status);
            return 5;
        }
        printf("Native Steam client library loaded and %s resolved.\n", STEAMCLIENT_INTERFACE_VERSION);
        return 0;
    }

    if (!client.attach()) {
        fprintf(stderr, "No authenticated native Steam session available (pipe=%d, user=%d).\n", client.pipe, client.user);
        return 4;
    }

    int factory_status = 0;
    auto *interface = static_cast<ISteamClient *>(client.create_interface(STEAMCLIENT_INTERFACE_VERSION, &factory_status));
    if (!interface) {
        fprintf(stderr, "Native Steam rejected %s (factory result %d).\n", STEAMCLIENT_INTERFACE_VERSION, factory_status);
        return 5;
    }
    CallbackRegistration::configure(*interface);
    auto *user = interface->GetISteamUser(client.user, client.pipe, STEAMUSER_INTERFACE_VERSION);
    auto *utils = interface->GetISteamUtils(client.pipe, STEAMUTILS_INTERFACE_VERSION);
    if (!utils || !user || utils->GetAppID() != rain_world_app) {
        fprintf(stderr, "Native Steam did not provide the Rain World application context.\n");
        return 5;
    }

    auto *messages = static_cast<ISteamNetworkingMessages *>(
        interface->GetISteamGenericInterface(client.user, client.pipe, STEAMNETWORKINGMESSAGES_INTERFACE_VERSION));
    if (!messages)
        messages = static_cast<ISteamNetworkingMessages *>(
            client.create_interface(STEAMNETWORKINGMESSAGES_INTERFACE_VERSION, &factory_status));
    if (!messages) {
        fprintf(stderr, "SteamNetworkingMessages is unavailable in this session.\n");
        return 5;
    }

    auto *networking = static_cast<ISteamNetworkingUtils *>(
        interface->GetISteamGenericInterface(client.user, client.pipe, STEAMNETWORKINGUTILS_INTERFACE_VERSION));
    if (!networking)
        networking = static_cast<ISteamNetworkingUtils *>(
            client.create_interface(STEAMNETWORKINGUTILS_INTERFACE_VERSION, &factory_status));
    if (networking) {
        networking->CheckPingDataUpToDate(1e10f);
        printf("Initial relay availability: %d\n", networking->GetRelayNetworkStatus(nullptr));
    }

    printf("Rain World Steam application context and SteamNetworkingMessages are available.\n");
    if (mode == Mode::Check)
        return 0;
    if (mode == Mode::Loopback) {
        auto *sockets = static_cast<ISteamNetworkingSockets *>(
            interface->GetISteamGenericInterface(client.user, client.pipe, STEAMNETWORKINGSOCKETS_INTERFACE_VERSION));
        if (!sockets)
            sockets = static_cast<ISteamNetworkingSockets *>(
                client.create_interface(STEAMNETWORKINGSOCKETS_INTERFACE_VERSION, &factory_status));
        if (!sockets) {
            fprintf(stderr, "SteamNetworkingSockets is unavailable in this session.\n");
            return 5;
        }
        return MeadowSocketProbe::run(*sockets);
    }
    if (target == user->GetSteamID().ConvertToUint64()) {
        fprintf(stderr, "Use a different Steam account for the remote peer.\n");
        return 2;
    }

    struct sigaction stop{};
    stop.sa_handler = on_interrupt;
    sigemptyset(&stop.sa_mask);
    sigaction(SIGINT, &stop, nullptr);
    sigaction(SIGTERM, &stop, nullptr);

    SteamNetworkingIdentity peer{};
    peer.m_eType = k_ESteamNetworkingIdentityType_SteamID;
    peer.m_cbSize = sizeof(peer.data.m_steamID64);
    peer.data.m_steamID64 = target;
    const int result = exchange(client, *messages, peer, mode);
    messages->CloseChannelWithUser(peer, probe_channel);
    return result;
}
