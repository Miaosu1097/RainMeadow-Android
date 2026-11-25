using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RainMeadow
{
    internal static class AndroidLanAddresses
    {
        internal static IPAddress[] Read(Action<string> report)
        {
            var preferred = new List<IPAddress>();
            var fallback = new List<IPAddress>();
            NetworkInterface[] interfaces;
            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (SystemException error)
            {
                report("Android LAN interface enumeration: " + error.Message);
                return new[] { IPAddress.Loopback };
            }

            foreach (var adapter in interfaces)
            {
                try
                {
                    var local = IsPreferred(adapter) ? preferred : fallback;
                    foreach (var entry in adapter.GetIPProperties().UnicastAddresses)
                    {
                        // Android Mono can report valid IPv4 addresses without link type or state.
                        var address = entry.Address;
                        if (address.AddressFamily == AddressFamily.InterNetwork
                            && !address.Equals(IPAddress.Any) && !IPAddress.IsLoopback(address))
                            local.Add(address);
                    }
                }
                catch (SystemException error)
                {
                    report("Android LAN interface properties: " + error.Message);
                }
            }

            // The first address initializes the local player. Preserve the upstream
            // order for qualified interfaces and the loopback default otherwise.
            return preferred.Concat(new[] { IPAddress.Loopback }).Concat(fallback).Distinct().ToArray();
        }

        private static bool IsPreferred(NetworkInterface adapter)
        {
            try
            {
                var type = adapter.NetworkInterfaceType;
                return adapter.Supports(NetworkInterfaceComponent.IPv4)
                    && (type == NetworkInterfaceType.Ethernet || type == NetworkInterfaceType.Wireless80211)
                    && adapter.OperationalStatus == OperationalStatus.Up;
            }
            catch (SystemException)
            {
                return false;
            }
        }
    }
}
