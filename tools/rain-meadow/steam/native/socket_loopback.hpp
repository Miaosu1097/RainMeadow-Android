#pragma once

#include "isteamnetworkingsockets.h"

#include <stdint.h>
#include <stdio.h>
#include <time.h>

namespace MeadowSocketProbe {

// Rain Meadow 0.1.16.3 emits these exact bytes for an empty tick update and a
// custom packet with key "probe" and the five-byte payload below.
constexpr unsigned char session_packet[] = {
    100, 0, 0, 0, 7, 0, 99, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0
};
constexpr unsigned char custom_packet[] = {
    5, 'p', 'r', 'o', 'b', 'e', 5, 0, 0, 255, 128, 1, 2
};

inline uint64_t now_ms() {
    timespec time{};
    clock_gettime(CLOCK_MONOTONIC, &time);
    return uint64_t(time.tv_sec) * 1000 + uint64_t(time.tv_nsec) / 1000000;
}

inline bool send_and_receive(ISteamNetworkingSockets &sockets, HSteamNetConnection sender,
    HSteamNetConnection receiver, const unsigned char *payload, uint32 size, int flags) {
    const EResult sent = sockets.SendMessageToConnection(sender, payload, size, flags, nullptr);
    if (sent != k_EResultOK) {
        fprintf(stderr, "Socket pair send returned EResult %d.\n", sent);
        return false;
    }

    const uint64_t deadline = now_ms() + 3000;
    while (now_ms() < deadline) {
        SteamNetworkingMessage_t *message = nullptr;
        const int received = sockets.ReceiveMessagesOnConnection(receiver, &message, 1);
        if (received == 1 && message) {
            const bool matches = message->m_conn == receiver
                && message->m_cbSize == static_cast<int>(size)
                && message->m_pData && memcmp(message->m_pData, payload, size) == 0
                && (message->m_nFlags & k_nSteamNetworkingSend_Reliable)
                    == (flags & k_nSteamNetworkingSend_Reliable);
            message->m_pfnRelease(message);
            if (!matches)
                fprintf(stderr, "Socket pair altered the message or its delivery flags.\n");
            return matches;
        }
        if (received != 0) {
            fprintf(stderr, "Socket pair receive returned %d.\n", received);
            return false;
        }
        const timespec delay{0, 10000000};
        nanosleep(&delay, nullptr);
    }
    fprintf(stderr, "Socket pair receive timed out.\n");
    return false;
}

inline int run(ISteamNetworkingSockets &sockets) {
    HSteamNetConnection first = k_HSteamNetConnection_Invalid;
    HSteamNetConnection second = k_HSteamNetConnection_Invalid;
    if (!sockets.CreateSocketPair(&first, &second, true, nullptr, nullptr)) {
        fprintf(stderr, "SteamNetworkingSockets could not create a UDP loopback pair.\n");
        return 6;
    }

    const bool session = send_and_receive(sockets, first, second, session_packet,
        sizeof(session_packet), k_nSteamNetworkingSend_UnreliableNoNagle);
    const bool custom = send_and_receive(sockets, second, first, custom_packet,
        sizeof(custom_packet), k_nSteamNetworkingSend_ReliableNoNagle);
    sockets.CloseConnection(first, 0, nullptr, false);
    sockets.CloseConnection(second, 0, nullptr, false);
    if (!session || !custom)
        return 6;
    printf("Local UDP socket pair preserved the session and custom frames in both directions.\n");
    return 0;
}

}
