#if MEADOW_STEAM_GAMEPLAY
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
#if MEADOW_STEAM_SDR
using ProtoBuf;
#endif
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace RainMeadow;

internal sealed class AndroidSteamTransport : IDisposable
{
    private const int ErrorSize = 1024;
    private static IntPtr library;
#if MEADOW_STEAM_SDR
    private static IntPtr valveLibrary;
#endif
    private readonly byte[] error = new byte[ErrorSize];

#if !MEADOW_STEAM_SDR
    [DllImport("libdl.so")] private static extern IntPtr dlopen(string path, int flags);
    [DllImport("libdl.so")] private static extern IntPtr dlsym(IntPtr handle, string symbol);
    [DllImport("libdl.so")] private static extern IntPtr dlerror();
#endif

#if MEADOW_STEAM_SDR
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitValveFunc(IntPtr valve, uint app, ulong steamId, [Out] byte[] error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ConfigFunc(byte[] config, int size, [Out] byte[] error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RelayStatusFunc([Out] byte[] error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CertRequestFunc([Out] byte[] request, int capacity, [Out] byte[] error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SignedCertFunc(byte[] envelope, int size, [Out] byte[] error);
#else
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFunc(uint app, ulong steamId, [Out] byte[] error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int KeyFunc([Out] byte[] key, [Out] byte[] error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AddCaFunc(string cert, [Out] byte[] error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RevokeCaFunc(ulong key);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CertFunc(byte[] cert, int len, ulong caId, byte[] signature, [Out] byte[] error);
#endif
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CloseFunc();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PeersFunc(ulong[] peers, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PopSignalFunc(out ulong peer, [Out] byte[] buffer, int capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SignalFunc(ulong peer, byte[] data, int len);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SendFunc(ulong peer, int channel, byte[] data, int len, int reliable);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadFunc(int channel, out ulong peer, [Out] byte[] buffer, int capacity);
#if MEADOW_STEAM_SDR
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StateFunc(ulong peer, out int relayed);
#else
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StateFunc(ulong peer);
#endif

    private readonly CloseFunc close;
#if MEADOW_STEAM_SDR
    private readonly ConfigFunc sdrConfig;
    private readonly RelayStatusFunc relayStatus;
    private readonly CertRequestFunc certRequest;
    private readonly SignedCertFunc signedCert;
#else
    private readonly KeyFunc key;
    private readonly AddCaFunc addCa;
    private readonly RevokeCaFunc revokeCa;
    private readonly CertFunc setCert;
#endif
    private readonly PeersFunc setPeers;
    private readonly PopSignalFunc pop;
    private readonly SignalFunc signal;
    private readonly SendFunc send;
    private readonly ReadFunc read;
    private readonly StateFunc state;
    private bool initialized;

    private AndroidSteamTransport(ulong steamId)
    {
        if (library == IntPtr.Zero)
        {
            const string libName = "libmeadow_steam_transport.so";
            string folder = Path.Combine(Path.GetDirectoryName(typeof(AndroidSteamTransport).Assembly.Location) ?? "",
                "steam-probe");
            string source = Path.Combine(folder, libName);
            if (!File.Exists(source))
                source = AssetManager.ResolveFilePath("plugins/steam-probe/" + libName);
            using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
            using var cache = activity.Call<AndroidJavaObject>("getCodeCacheDir");
            string cacheDirectory = cache.Call<string>("getAbsolutePath");
#if MEADOW_STEAM_SDR
            (library, valveLibrary) = AndroidSteamNativeLoader.Load(source, activity, cacheDirectory);
#else
            folder = Path.GetDirectoryName(source)!;
            string stl = Path.Combine(cacheDirectory, "libc++_shared.so");
            File.Copy(Path.Combine(folder, "libc++_shared.so"), stl, overwrite: true);
            if (dlopen(stl, 2) == IntPtr.Zero)
                throw new InvalidOperationException("Steam C++ runtime load failed. " + Marshal.PtrToStringAnsi(dlerror()));

            string destination = Path.Combine(cacheDirectory, libName);
            File.Copy(source, destination, overwrite: true);
            library = dlopen(destination, 2);
            if (library == IntPtr.Zero)
                throw new InvalidOperationException("Steam network library load failed. " + Marshal.PtrToStringAnsi(dlerror()));
#endif
        }

        close = Resolve<CloseFunc>("close");
#if MEADOW_STEAM_SDR
        var init = Resolve<InitValveFunc>("init_valve");
        sdrConfig = Resolve<ConfigFunc>("sdr_config");
        relayStatus = Resolve<RelayStatusFunc>("relay_status");
        certRequest = Resolve<CertRequestFunc>("cert_request");
        signedCert = Resolve<SignedCertFunc>("set_signed_cert");
#else
        var init = Resolve<InitFunc>("init");
        key = Resolve<KeyFunc>("key");
        addCa = Resolve<AddCaFunc>("add_ca");
        revokeCa = Resolve<RevokeCaFunc>("revoke_ca");
        setCert = Resolve<CertFunc>("set_cert");
#endif
        setPeers = Resolve<PeersFunc>("set_peers");
        pop = Resolve<PopSignalFunc>("pop_signal");
        signal = Resolve<SignalFunc>("signal");
        send = Resolve<SendFunc>("send");
        read = Resolve<ReadFunc>("read");
        state = Resolve<StateFunc>("state");

#if MEADOW_STEAM_SDR
        if (init(valveLibrary, SteamLobbySession.RainWorldAppId, steamId, error) != 1)
#else
        if (init(SteamLobbySession.RainWorldAppId, steamId, error) != 1)
#endif
            throw Failure("Steam network library initialization failed.");
        initialized = true;
    }

    internal static async Task<AndroidSteamTransport> ConnectAsync(SteamLobbySession session, CancellationToken token)
    {
        var transport = new AndroidSteamTransport(session.SteamAccountId);
        try
        {
            using var request = UnityWebRequest.Get("https://api.steampowered.com/ISteamApps/GetSDRConfig/v1?appid=312520");
            request.timeout = 12;
            request.redirectLimit = 0;
            var operation = request.SendWebRequest();
            while (!operation.isDone)
                await Task.Delay(25, token);
            token.ThrowIfCancellationRequested();
            if (request.result != UnityWebRequest.Result.Success || request.downloadedBytes > 1024 * 1024)
                throw new InvalidOperationException("Steam network configuration could not be downloaded.");

            var config = JObject.Parse(request.downloadHandler.text);
            if (config.Value<bool?>("success") != true || config["certs"] is not JArray roots || roots.Count == 0)
                throw new InvalidOperationException("Steam network configuration has no certificate chain.");

#if MEADOW_STEAM_SDR
            byte[] configBytes = request.downloadHandler.data;
            if (transport.sdrConfig(configBytes, configBytes.Length, transport.error) != 1)
                throw transport.Failure("Valve rejected the Steam relay configuration.");

            byte[] certificateRequest = new byte[8192];
            int size = transport.certRequest(certificateRequest, certificateRequest.Length, transport.error);
            if (size <= 0 || size > certificateRequest.Length)
                throw transport.Failure("Valve could not generate a Steam connection key.");
            using var unsigned = new MemoryStream(certificateRequest, 0, size, writable: false);
            var claims = ProtoBuf.Serializer.Deserialize<SdrCertificateRequest>(unsigned).Certificate;
            if (claims?.KeyType != 1 || claims.KeyData is not { Length: 32 } publicKey)
                throw new InvalidOperationException("Valve returned an invalid Steam connection key.");

            var reply = await session.RequestNetworkCertificateAsync(publicKey, token);
            token.ThrowIfCancellationRequested();
            string? certError = SteamNetworkIdentityCheck.Validate(reply.Certificate, reply.CAKeyID, reply.CASignature,
                publicKey, session.SteamAccountId, SteamLobbySession.RainWorldAppId,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out bool scoped);
            if (certError != null || !scoped)
                throw new InvalidOperationException(certError ?? "Steam networking certificate has no Rain World scope.");

            using var signed = new MemoryStream();
            ProtoBuf.Serializer.Serialize(signed, new SdrSignedCertificate
            {
                Certificate = reply.Certificate,
                CaKeyId = reply.CAKeyID,
                Signature = reply.CASignature,
            });
            byte[] envelope = signed.ToArray();
            if (transport.signedCert(envelope, envelope.Length, transport.error) != 1)
                throw transport.Failure("Valve could not install the Steam networking certificate.");
#else
            foreach (var entry in roots.Values<string>())
            {
                if (entry == null || transport.addCa(entry, transport.error) != 1)
                    throw transport.Failure("Steam network configuration has an invalid certificate.");
            }
            if (config["revoked_keys"] is JArray revoked)
            {
                foreach (var entry in revoked)
                    if (ulong.TryParse(entry.ToString(), out var keyId))
                        transport.revokeCa(keyId);
            }

            byte[] publicKey = new byte[32];
            if (transport.key(publicKey, transport.error) != 1)
                throw transport.Failure("Steam connection key generation failed.");
            var reply = await session.RequestNetworkCertificateAsync(publicKey, token);
            if (reply.Certificate == null || reply.CASignature?.Length != 64
                || transport.setCert(reply.Certificate, reply.Certificate.Length, reply.CAKeyID,
                    reply.CASignature, transport.error) != 1)
                throw transport.Failure("Steam networking certificate installation failed.");
#endif

            return transport;
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    internal void SetPeers(ulong[] peers) => setPeers(peers, peers.Length);
    internal int PopSignal(out ulong peer, byte[] buffer) => pop(out peer, buffer, buffer.Length);
    internal bool ReceiveSignal(ulong peer, byte[] data) => signal(peer, data, data.Length) == 1;

#if MEADOW_STEAM_SDR
    internal string RelaySummary()
    {
        Array.Clear(error, 0, error.Length);
        int status = relayStatus(error);
        int length = Array.IndexOf(error, (byte)0);
        return $"SDR status {status}: " + Encoding.UTF8.GetString(error, 0, length < 0 ? error.Length : length);
    }

    [ProtoContract]
    private sealed class SdrCertificateRequest
    {
        [ProtoMember(1)] public SteamNetworkIdentityCheck.CertificateClaims? Certificate { get; set; }
    }

    [ProtoContract]
    private sealed class SdrSignedCertificate
    {
        [ProtoMember(4)] public byte[]? Certificate { get; set; }
        [ProtoMember(5, DataFormat = DataFormat.FixedSize)] public ulong CaKeyId { get; set; }
        [ProtoMember(6)] public byte[]? Signature { get; set; }
    }
#endif
    internal int Send(ulong peer, int channel, byte[] data, int size, bool reliable) => send(peer, channel, data, size, reliable ? 1 : 0);
    internal int Read(int channel, out ulong peer, byte[] buffer) => read(channel, out peer, buffer, buffer.Length);
#if MEADOW_STEAM_SDR
    internal int State(ulong peer) => state(peer, out _);
#else
    internal int State(ulong peer) => state(peer);
#endif

    private InvalidOperationException Failure(string reason)
    {
        int length = Array.IndexOf(error, (byte)0);
        return new InvalidOperationException(length > 0 ? reason + " " + Encoding.UTF8.GetString(error, 0, length) : reason);
    }

    private static T Resolve<T>(string suffix) where T : Delegate
    {
        string symbol = "meadow_transport_" + suffix;
#if MEADOW_STEAM_SDR
        IntPtr address = AndroidSteamNativeLoader.Resolve(library, symbol);
#else
        IntPtr address = dlsym(library, symbol);
#endif
        if (address == IntPtr.Zero)
            throw new MissingMethodException("Steam network library interface is incomplete.");
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    public void Dispose()
    {
        if (!initialized) return;
        initialized = false;
        close();
    }
}
#endif
