#if MEADOW_STEAM_PROBE
using System;
using System.Globalization;
using System.IO;
using System.Linq;
#if !MEADOW_STEAM_GAMEPLAY
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Parameters;
#endif
using ProtoBuf;

namespace RainMeadow
{
    internal static class SteamNetworkIdentityCheck
    {
#if !MEADOW_STEAM_GAMEPLAY
        internal static async Task<string> RunAsync(SteamLobbySession session, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ulong steamId = session.SteamAccountId;
            if (steamId == 0)
                throw new SteamLobbyProbe.SteamConnectionClosedException();

            var publicKey = CreateProbePublicKey();
            var response = await session.RequestNetworkCertificateAsync(publicKey, token).ConfigureAwait(false);
            return Inspect(response.Certificate, response.CAKeyID, response.CASignature,
                publicKey, steamId, SteamLobbySession.RainWorldAppId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        internal static byte[] CreateProbePublicKey()
        {
            // This is a certificate-issuance check. The ephemeral private key is never exported.
            var seed = new byte[Ed25519PrivateKeyParameters.KeySize];
            using var rng = RandomNumberGenerator.Create();
            try
            {
                rng.GetBytes(seed);
                return new Ed25519PrivateKeyParameters(seed, 0).GeneratePublicKey().GetEncoded();
            }
            finally
            {
                Array.Clear(seed, 0, seed.Length);
            }
        }

        internal static string Inspect(byte[]? certificate, ulong caKeyId, byte[]? signature,
            byte[] publicKey, ulong steamId, uint appId, long utcSeconds)
        {
            string? error = Validate(certificate, caKeyId, signature, publicKey, steamId, appId, utcSeconds, out bool scoped);
            return error ?? (scoped
                ? "Steam returned a Rain World networking certificate bound to this account and probe key."
                : "Steam returned a matching account certificate without an explicit Rain World scope.");
        }

#endif

        internal static string? Validate(byte[]? certificate, ulong caKeyId, byte[]? signature,
            byte[] publicKey, ulong steamId, uint appId, long utcSeconds, out bool scoped)
        {
            scoped = false;
            if (certificate == null || certificate.Length == 0)
                return "Steam replied without a networking certificate.";
            if (certificate.Length > 8192 || caKeyId == 0 || signature?.Length != 64)
                return "Steam returned an incomplete networking certificate.";

            CertificateClaims claims;
            try
            {
                using var data = new MemoryStream(certificate, writable: false);
                claims = ProtoBuf.Serializer.Deserialize<CertificateClaims>(data);
            }
            catch (Exception error) when (error is ProtoException or IOException or OverflowException)
            {
                return "Steam returned an unreadable networking certificate.";
            }

            if (claims.KeyType != 1 || claims.KeyData == null || !claims.KeyData.SequenceEqual(publicKey))
                return "The networking certificate public key does not match the request.";
            if (!MatchesSteamId(claims, steamId))
                return "Steam returned a certificate without a matching Steam account identity.";
            if (claims.Expires <= utcSeconds || claims.Created > utcSeconds + 300)
                return "The networking certificate is outside its validity period.";
            scoped = claims.AppIds.Contains(appId);
            return !scoped && claims.AppIds.Length != 0
                ? "Steam returned a networking certificate scoped to a different application."
                : null;
        }

        private static bool MatchesSteamId(CertificateClaims claims, ulong expected)
        {
            ulong legacy = claims.LegacySteamId;
            ulong binary = claims.LegacyIdentity?.SteamId ?? 0;
            string? text = claims.Identity;
            if ((legacy != 0 && legacy != expected) || (binary != 0 && binary != expected))
                return false;

            if (text is { Length: > 0 })
            {
                return text.StartsWith("steamid:", StringComparison.Ordinal)
                    && ulong.TryParse(text.Substring("steamid:".Length), NumberStyles.None,
                        CultureInfo.InvariantCulture, out ulong parsed)
                    && parsed == expected;
            }

            return legacy == expected || binary == expected;
        }

        // CMsgSteamDatagramCertificate, in Valve's steamnetworkingsockets_messages_certs.proto.
        [ProtoContract]
        internal sealed class CertificateClaims
        {
            [ProtoMember(1)] public int KeyType { get; set; }
            [ProtoMember(2)] public byte[]? KeyData { get; set; }
            [ProtoMember(4, DataFormat = DataFormat.FixedSize)] public ulong LegacySteamId { get; set; }
            [ProtoMember(8, DataFormat = DataFormat.FixedSize)] public uint Created { get; set; }
            [ProtoMember(9, DataFormat = DataFormat.FixedSize)] public uint Expires { get; set; }
            [ProtoMember(10)] public uint[] AppIds { get; set; } = Array.Empty<uint>();
            [ProtoMember(11)] public LegacyIdentityClaims? LegacyIdentity { get; set; }
            [ProtoMember(12)] public string? Identity { get; set; }
        }

        [ProtoContract]
        internal sealed class LegacyIdentityClaims
        {
            [ProtoMember(16, DataFormat = DataFormat.FixedSize)] public ulong SteamId { get; set; }
        }
    }
}
#endif
