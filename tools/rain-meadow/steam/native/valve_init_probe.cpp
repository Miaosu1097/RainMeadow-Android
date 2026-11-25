#include <dlfcn.h>
#include <unistd.h>

#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <iterator>
#include <string>

#include <isteamnetworkingutils.h>
#include <steamuniverse.h>

template <typename Function>
Function lookup(void *library, const char *name) {
    auto function = reinterpret_cast<Function>(dlsym(library, name));
    if (!function) {
        std::fprintf(stderr, "%s: %s\n", name, dlerror());
        std::exit(2);
    }
    return function;
}

int main(int argc, char **argv) {
    if (argc != 3) {
        std::fprintf(stderr, "Usage: %s VALVE_ANDROID_LIBRARY SDR_CONFIG_JSON\n", argv[0]);
        return 1;
    }
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    std::ifstream configFile(argv[2], std::ios::binary);
    std::string config((std::istreambuf_iterator<char>(configFile)), {});
    if (!configFile.is_open() || config.empty() || config.size() > 1024 * 1024) {
        std::fprintf(stderr, "Invalid SDR config input\n");
        return 1;
    }
    if (!dlopen("libandroid.so", RTLD_NOW | RTLD_GLOBAL)) {
        std::fprintf(stderr, "Android platform preload: %s\n", dlerror());
        return 2;
    }
    void *library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!library) {
        std::fprintf(stderr, "Valve library load: %s\n", dlerror());
        return 2;
    }
    std::puts("Valve library loaded alongside libandroid.so");

    using SetUniverse = void (*)(bool steamChinaRealm, EUniverse universe);
    const auto setUniverse = lookup<SetUniverse>(library, "SteamDatagram_SetUniverse");
    const auto setApp = lookup<void (*)(uint32)>(library, "SteamDatagram_SetAppID");
    const auto init = lookup<bool (*)(char *)>(library, "SteamDatagramClient_Init");
    const auto kill = lookup<void (*)()>(library, "SteamDatagramClient_Kill");
    const auto sockets = lookup<void *(*)()>(library, "SteamNetworkingSockets_LibV13");
    const auto messages = lookup<void *(*)()>(library, "SteamNetworkingMessages_LibV2");
    const auto utils = lookup<ISteamNetworkingUtils *(*)()>(library, "SteamNetworkingUtils_LibV4");
    const auto setConfig = lookup<bool (*)(const void *, size_t, char *)>(
        library, "SteamDatagram_SetNetworkConfig");

    // These standalone entry points are specific to the probed Android build.
    char error[1024]{};
    setApp(312520);
    setUniverse(false, k_EUniversePublic);
    if (!init(error)) {
        std::fprintf(stderr, "SteamDatagramClient_Init: %s\n", error);
        return 3;
    }
    std::puts("SteamDatagramClient_Init succeeded for AppID 312520");

    auto *networkingUtils = utils();
    bool hasSockets = sockets() != nullptr;
    bool hasMessages = messages() != nullptr;
    std::printf("Interfaces: Sockets013=%d Messages002=%d Utils004=%d\n",
                hasSockets, hasMessages, networkingUtils != nullptr);
    if (!hasSockets || !hasMessages || !networkingUtils) {
        kill();
        return 4;
    }

    if (!setConfig(config.data(), config.size(), error)) {
        std::fprintf(stderr, "SteamDatagram_SetNetworkConfig: %s\n", error);
        kill();
        return 5;
    }
    std::printf("SDR config accepted; %d relay points of presence\n",
                networkingUtils->GetPOPCount());

    // The SDK implements InitRelayNetworkAccess with this call.
    networkingUtils->CheckPingDataUpToDate(1e10f);
    SteamRelayNetworkStatus_t status{};
    ESteamNetworkingAvailability availability = k_ESteamNetworkingAvailability_Unknown;
    for (int seconds = 0; seconds <= 20; ++seconds) {
        availability = networkingUtils->GetRelayNetworkStatus(&status);
        if (seconds == 0 || seconds % 5 == 0 ||
            availability <= k_ESteamNetworkingAvailability_Failed ||
            availability == k_ESteamNetworkingAvailability_Current) {
            std::printf("Relay status at %ds: %d, %s\n",
                        seconds, availability, status.m_debugMsg);
        }
        if (availability == k_ESteamNetworkingAvailability_Current ||
            availability <= k_ESteamNetworkingAvailability_Failed) {
            break;
        }
        if (seconds < 20) sleep(1);
    }
    kill();
    std::puts("SteamDatagramClient_Kill completed");
    return availability == k_ESteamNetworkingAvailability_Current ? 0 : 6;
}
