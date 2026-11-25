using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

internal static class LanProbe
{
    internal static void Inspect(Action<string> report)
    {
        report("NetworkInterface assembly: " + typeof(NetworkInterface).Assembly.FullName
            + ", module=" + typeof(NetworkInterface).Module.ModuleVersionId);
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    var addresses = adapter.GetIPProperties().UnicastAddresses
                        .Select(entry => entry.Address)
                        .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                        .ToArray();
                    var type = adapter.NetworkInterfaceType;
                    var status = adapter.OperationalStatus;
                    bool eligible = adapter.Supports(NetworkInterfaceComponent.IPv4)
                        && (type == NetworkInterfaceType.Ethernet || type == NetworkInterfaceType.Wireless80211)
                        && status == OperationalStatus.Up
                        && addresses.Any(address => !address.Equals(IPAddress.Loopback));
                    report("interface=" + adapter.Name + " type=" + type + " status=" + status
                        + " ipv4-count=" + addresses.Length + " meadow-filter=" + eligible);
                }
                catch (Exception error)
                {
                    report("interface-properties: " + error.GetType().Name + ": " + error.Message);
                }
            }
        }
        catch (Exception error)
        {
            report("interface-enumeration: " + error.GetType().Name + ": " + error.Message);
        }

        var candidate = RainMeadow.AndroidLanAddresses.Read(report);
        report("android candidate nonloopback IPv4 count: "
            + candidate.Count(address => !IPAddress.IsLoopback(address))
            + " primary-is-loopback=" + IPAddress.IsLoopback(candidate[0]));

        try
        {
            using (var receiver = NewSocket())
            using (var sender = NewSocket())
            {
                receiver.EnableBroadcast = true;
                receiver.Bind(new IPEndPoint(IPAddress.Any, 0));
                receiver.ReceiveTimeout = 1000;
                int port = ((IPEndPoint)receiver.LocalEndPoint).Port;
                var message = new byte[] { 0x52, 0x4D };
                sender.SendTo(message, new IPEndPoint(IPAddress.Loopback, port));
                var received = new byte[message.Length];
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int count = receiver.ReceiveFrom(received, ref from);
                if (count != message.Length || !message.SequenceEqual(received))
                    throw new InvalidOperationException("Loopback datagram mismatch");
                report("udp-ipv4 bind and loopback: OK");
            }
        }
        catch (SocketException error)
        {
            report("udp-ipv4 socket error: " + error.SocketErrorCode + ": " + error.Message);
        }
        catch (Exception error)
        {
            report("udp-ipv4: " + error.GetType().Name + ": " + error.Message);
        }
    }

    private static Socket NewSocket()
    {
        return new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    }

    private static void Main()
    {
        Inspect(Console.WriteLine);
    }
}
