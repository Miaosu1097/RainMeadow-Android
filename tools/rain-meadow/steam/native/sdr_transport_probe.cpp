#include <dlfcn.h>
#include <unistd.h>

#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <iterator>
#include <string>

#include "socket_loopback.hpp"

template <typename Function>
Function load(void* library, const char* name) {
    auto function = reinterpret_cast<Function>(dlsym(library, name));
    if (!function) {
        std::fprintf(stderr, "%s: %s\n", name, dlerror());
        std::exit(2);
    }
    return function;
}

void* openLibrary(const char* path, int flags) {
    void* library = dlopen(path, RTLD_NOW | flags);
    if (!library) {
        std::fprintf(stderr, "%s: %s\n", path, dlerror());
        std::exit(2);
    }
    return library;
}

int main(int argc, char** argv) {
    if (argc != 4) {
        std::fprintf(stderr, "Usage: %s VALVE_LIBRARY MOD_BRIDGE SDR_CONFIG\n", argv[0]);
        return 1;
    }
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    std::ifstream file(argv[3], std::ios::binary);
    std::string config((std::istreambuf_iterator<char>(file)), {});
    if (config.empty() || config.size() > 1024 * 1024) return 1;

    openLibrary("libandroid.so", RTLD_GLOBAL);
    auto* valve = openLibrary(argv[1], RTLD_LOCAL);
    auto* bridge = openLibrary(argv[2], RTLD_LOCAL);
    auto init = load<int (*)(void*, uint32, uint64, char*)>(bridge, "meadow_transport_init_valve");
    auto close = load<void (*)()>(bridge, "meadow_transport_close");
    auto applyConfig = load<int (*)(const void*, int, char*)>(bridge, "meadow_transport_sdr_config");
    auto getStatus = load<int (*)(char*)>(bridge, "meadow_transport_relay_status");
    auto getRequest = load<int (*)(void*, int, char*)>(bridge, "meadow_transport_cert_request");
    auto setCert = load<int (*)(const void*, int, char*)>(bridge, "meadow_transport_set_signed_cert");
    auto sockets = load<ISteamNetworkingSockets* (*)()>(valve, "SteamNetworkingSockets_LibV13");

    SteamNetworkingErrMsg error{};
    // The probe is offline with respect to Steam accounts. This value is only used by the
    // bridge's local identity comparison, and the invalid certificate cannot set it.
    constexpr uint64 localFixtureId = 76561197960265729ULL;
    for (int run = 0; run < 2; ++run) {
        if (init(valve, 312520, localFixtureId, error) != 1 ||
            applyConfig(config.data(), static_cast<int>(config.size()), error) != 1) {
            std::fprintf(stderr, "SDR bridge initialization: %s\n", error);
            return 3;
        }

        unsigned char certRequest[8192];
        int size = getRequest(certRequest, sizeof(certRequest), error);
        if (size < 32 || size > static_cast<int>(sizeof(certRequest))) {
            std::fprintf(stderr, "Native certificate request failed: %s\n", error);
            return 4;
        }
        std::printf("Valve produced a %d-byte certificate request.\n", size);
        const unsigned char invalidCert[] = {0xff};
        if (setCert(invalidCert, sizeof(invalidCert), error) != 0) {
            std::fprintf(stderr, "Valve accepted a malformed certificate.\n");
            return 4;
        }
        std::puts("Valve rejected a malformed certificate.");

        if (MeadowSocketProbe::run(*sockets()) != 0) return 5;

        int relayStatus = k_ESteamNetworkingAvailability_Unknown;
        for (int i = 0; i < 20; ++i) {
            relayStatus = getStatus(error);
            if (relayStatus == k_ESteamNetworkingAvailability_Current) break;
            sleep(1);
        }
        std::printf("SDR status %d: %s\n", relayStatus, error);
        close();
        if (relayStatus != k_ESteamNetworkingAvailability_Current) return 6;
    }
}
