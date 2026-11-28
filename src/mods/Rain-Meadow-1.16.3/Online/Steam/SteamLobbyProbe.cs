#if MEADOW_STEAM_PROBE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Discovery;
#if MEADOW_STEAM_GAMEPLAY
using System.Globalization;
using System.IO;
using ProtoBuf;
using SteamKit2.Internal;
#endif

namespace RainMeadow
{
    internal static class SteamLobbyProbe
    {
        internal static Task<SteamLobbySession?> RunAsync(
            Action<string> update,
            Action<string?> showChallenge,
            bool verifyQrOnly,
            CancellationToken cancellationToken,
            IReadOnlyList<ServerRecord>? tcpServers = null)
        {
            return RunSessionAsync(update, async client =>
            {
                try
                {
                    update("Requesting Steam Guard QR code…");
                    var auth = await client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
                    {
                        DeviceFriendlyName = "Rain Meadow Android",
                        IsPersistentSession = true,
                    }).ConfigureAwait(false);

                    void ShowCurrentChallenge()
                    {
                        ValidateChallenge(auth.ChallengeURL);
                        showChallenge(auth.ChallengeURL);
                    }

                    auth.ChallengeURLChanged = ShowCurrentChallenge;
                    update("Steam issued a sign-in QR challenge.");
                    ShowCurrentChallenge();
                    return verifyQrOnly
                        ? null
                        : SteamLoginTicket.From(await auth.PollingWaitForResultAsync(cancellationToken).ConfigureAwait(false));
                }
                finally
                {
                    showChallenge(null);
                }
            }, cancellationToken, tcpServers);
        }

        internal static async Task<SteamLobbySession?> RunWithCredentialsAsync(
            Action<string> update,
            AuthSessionDetails credentials,
            CancellationToken cancellationToken,
            IReadOnlyList<ServerRecord> tcpServers)
        {
            try
            {
                return await RunSessionAsync(update, async client =>
                {
                    CredentialsAuthSession auth;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        update("Requesting Steam key and submitting encrypted credentials…");
                        auth = await WaitForReply(client.Authentication.BeginAuthSessionViaCredentialsAsync(credentials),
                            "Steam credential request", cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        credentials.Password = null;
                        credentials.Username = null;
                    }

                    update("Steam accepted the sign-in request. Checking Steam Guard…");
                    return SteamLoginTicket.From(await auth.PollingWaitForResultAsync(cancellationToken).ConfigureAwait(false));
                }, cancellationToken, tcpServers).ConfigureAwait(false);
            }
            finally
            {
                credentials.Password = null;
                credentials.Username = null;
            }
        }

        internal static Task<SteamLobbySession?> RunWithSavedLoginAsync(
            Action<string> update, SteamLoginTicket ticket, CancellationToken cancellationToken,
            IReadOnlyList<ServerRecord> tcpServers)
        {
            return RunSessionAsync(update, _ => Task.FromResult<SteamLoginTicket?>(ticket), cancellationToken, tcpServers);
        }

        internal static async Task<T> WaitForReply<T>(Task<T> reply, string stage, CancellationToken token)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            var deadline = Task.Delay(TimeSpan.FromSeconds(25), wait.Token);
            if (await Task.WhenAny(reply, deadline).ConfigureAwait(false) != reply)
            {
                // Closing the Steam client also completes jobs still pending on that connection.
                _ = reply.ContinueWith(task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                token.ThrowIfCancellationRequested();
                throw new ReplyTimeoutException(stage);
            }

            wait.Cancel();
            return await reply.ConfigureAwait(false);
        }

        internal sealed class ReplyTimeoutException : TimeoutException
        {
            internal ReplyTimeoutException(string stage) : base(stage + " timed out.") { }
        }

        internal sealed class SteamResultException : Exception
        {
            internal bool IsRejectedLogin => Result is EResult.Expired or EResult.Revoked
                or EResult.InvalidPassword or EResult.AccessDenied;

            private EResult Result { get; }

            internal SteamResultException(string stage, EResult result) : base($"{stage} returned {result}.")
            {
                Result = result;
            }
        }

        private static async Task<SteamLobbySession?> RunSessionAsync(
            Action<string> update,
            Func<SteamClient, Task<SteamLoginTicket?>> authenticate,
            CancellationToken cancellationToken,
            IReadOnlyList<ServerRecord>? tcpServers)
        {
            update("Connecting to Steam…");
            var client = await ConnectAsync(tcpServers, cancellationToken).ConfigureAwait(false);
            bool transferred = false;
            try
            {
                var user = client.GetHandler<SteamUser>()!;
                var result = await authenticate(client).ConfigureAwait(false);
                if (result == null)
                    return null;
                cancellationToken.ThrowIfCancellationRequested();

                update("Waiting for Steam login…");
                user.LogOn(new SteamUser.LogOnDetails
                {
                    Username = result.AccountName,
                    AccessToken = result.RefreshToken,
                    ShouldRememberPassword = true,
                });

                var login = await WaitForReply(WaitFor<SteamUser.LoggedOnCallback>(client, cancellationToken),
                    "Steam login response", cancellationToken).ConfigureAwait(false);
                if (login.Result != EResult.OK)
                    throw new SteamResultException("Steam login", login.Result);

                var session = new SteamLobbySession(client, result);
                update("Signed in to Steam.");
                transferred = true;
                return session;
            }
            finally
            {
                if (!transferred)
                    client.Disconnect();
            }
        }

        private static async Task<SteamClient> ConnectAsync(
            IReadOnlyList<ServerRecord>? tcpServers, CancellationToken cancellationToken)
        {
            var configuration = SteamConfiguration.Create(builder => builder
                .WithProtocolTypes(tcpServers == null ? ProtocolTypes.WebSocket : ProtocolTypes.Tcp)
                .WithConnectionTimeout(TimeSpan.FromSeconds(8)));

            int count = tcpServers?.Count ?? 1;
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var client = new SteamClient(configuration);
#if MEADOW_STEAM_GAMEPLAY
                client.AddHandler(new SteamP2PSignaling());
                client.AddHandler(new SteamLobbyChat());
#endif
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    client.Connect(tcpServers?[i]);
                    await WaitFor<SteamClient.ConnectedCallback>(client, attempt.Token).ConfigureAwait(false);
                    return client;
                }
                catch (SteamConnectionClosedException)
                {
                    client.Disconnect();
                }
                catch (OperationCanceledException)
                {
                    client.Disconnect();
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            throw new InvalidOperationException("No Steam connection server completed the encrypted handshake.");
        }

        internal sealed class SteamConnectionClosedException : Exception
        {
        }

        private static void ValidateChallenge(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || uri.Host != "s.team"
                || !uri.AbsolutePath.StartsWith("/q/", StringComparison.Ordinal))
                throw new InvalidOperationException("Steam returned an unexpected QR challenge address.");
        }

        private static async Task<T> WaitFor<T>(SteamClient client, CancellationToken cancellationToken)
            where T : CallbackMsg
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // SteamKit 2.5 GetCallback() only peeks at the head of the queue.
                var callback = client.GetCallback(freeLast: true);
                if (callback is T expected)
                    return expected;
                if (callback is SteamClient.DisconnectedCallback)
                    throw new SteamConnectionClosedException();
                if (callback == null)
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal sealed class SteamLoginTicket
    {
        internal string AccountName { get; }
        internal string RefreshToken { get; }

        internal SteamLoginTicket(string accountName, string refreshToken)
        {
            if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(refreshToken))
                throw new ArgumentException("Steam returned incomplete sign-in data.");

            AccountName = accountName;
            RefreshToken = refreshToken;
        }

        internal static SteamLoginTicket From(AuthPollResult result) => new(result.AccountName, result.RefreshToken);
    }

    internal sealed class SteamLobbySession : IDisposable
    {
        internal const uint RainWorldAppId = 312520;
        private readonly SteamClient client;
        private readonly SteamMatchmaking matchmaking;
        private SteamLoginTicket? loginTicket;
        private int disposed;

        internal SteamLobbySession(SteamClient client, SteamLoginTicket? loginTicket = null)
        {
            this.client = client;
            matchmaking = client.GetHandler<SteamMatchmaking>()!;
            this.loginTicket = loginTicket;
#if MEADOW_STEAM_GAMEPLAY
            AnnouncePlaying();
#endif
        }

#if MEADOW_STEAM_GAMEPLAY
        private void AnnouncePlaying()
        {
            try
            {
                var message = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayedWithDataBlob);
                message.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed { game_id = RainWorldAppId });
                client.Send(message);
            }
            catch (Exception exception)
            {
                RainMeadow.Warn($"Could not announce playing app: {exception.Message}");
            }
        }
#endif

        internal SteamLoginTicket? TakeLoginTicket() => Interlocked.Exchange(ref loginTicket, null);

        internal bool IsConnected => Volatile.Read(ref disposed) == 0 && client.IsConnected;
        internal ulong SteamAccountId => client.SteamID?.ConvertToUInt64() ?? 0;

        internal void Pump()
        {
            ICallbackMsg? callback;
            while (Volatile.Read(ref disposed) == 0 && (callback = client.GetCallback(freeLast: true)) != null)
            {
                if (callback is SteamClient.DisconnectedCallback or SteamUser.LoggedOffCallback)
                    Dispose();
            }
        }

        internal Task<SteamNetworking.NetworkingCertificateCallback> RequestNetworkCertificateAsync(
            byte[] publicKey, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsConnected || client.GetHandler<SteamNetworking>() is not SteamNetworking networking)
                throw new SteamLobbyProbe.SteamConnectionClosedException();

            var job = networking.RequestNetworkingCertificate(RainWorldAppId, publicKey);
            return SteamLobbyProbe.WaitForReply(job.ToTask(), "Steam networking certificate request", token);
        }

        internal async Task<SteamLobbySummary[]> GetRoomsAsync(string version, CancellationToken token,
            Func<ulong, bool>? isTrustedOwner = null)
        {
            token.ThrowIfCancellationRequested();
            if (!IsConnected)
                throw new SteamLobbyProbe.SteamConnectionClosedException();

            var filters = new List<SteamMatchmaking.Lobby.Filter>
            {
                new SteamMatchmaking.Lobby.DistanceFilter(ELobbyDistanceFilter.Worldwide),
                new SteamMatchmaking.Lobby.StringFilter("client", ELobbyComparison.Equal, "Meadow_" + version),
            };

            var job = matchmaking.GetLobbyList(RainWorldAppId, filters)
                ?? throw new SteamLobbyProbe.SteamConnectionClosedException();
            var list = await SteamLobbyProbe.WaitForReply(job.ToTask(), "Steam lobby query", token).ConfigureAwait(false);
            if (list.Result != EResult.OK)
                throw new SteamLobbyProbe.SteamResultException("Steam lobby query", list.Result);
            if (list.Lobbies == null) return Array.Empty<SteamLobbySummary>();

            using var pinLookup = CancellationTokenSource.CreateLinkedTokenSource(token);
            pinLookup.CancelAfter(TimeSpan.FromSeconds(5));
            using var pinLookupSlots = new SemaphoreSlim(8);
            var rooms = await Task.WhenAll(list.Lobbies.Select(room =>
                    SummarizeRoomAsync(room, isTrustedOwner, pinLookupSlots, pinLookup.Token)))
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return rooms;
        }

        private async Task<SteamLobbySummary> SummarizeRoomAsync(SteamMatchmaking.Lobby room, Func<ulong, bool>? isTrustedOwner,
            SemaphoreSlim pinLookupSlots, CancellationToken token)
        {
            ulong lobbyId = room.SteamID.ConvertToUInt64();
            var summary = new SteamLobbySummary(lobbyId, room.Metadata, room.NumMembers, room.MaxMembers);
            if (isTrustedOwner == null || !room.Metadata.TryGetValue(SteamLobbySummary.PinnedKey, out var pinValue)
                || !bool.TryParse(pinValue, out bool pinRequested) || !pinRequested) return summary;

            // Steam's list response can omit the owner or carry a cached owner. Confirm both fields.
            try
            {
                await pinLookupSlots.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var job = matchmaking.GetLobbyData(RainWorldAppId, room.SteamID);
                    if (job == null) return summary;
                    var details = (await SteamLobbyProbe.WaitForReply(job.ToTask(), "Steam pinned lobby lookup", token)
                        .ConfigureAwait(false)).Lobby;
                    if (details == null || details.SteamID.ConvertToUInt64() != lobbyId) return summary;
                    bool trustedOwner = details.OwnerSteamID is SteamID owner
                        && isTrustedOwner(owner.ConvertToUInt64());
                    return new SteamLobbySummary(lobbyId, details.Metadata, details.NumMembers, details.MaxMembers, trustedOwner);
                }
                finally
                {
                    pinLookupSlots.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return summary;
            }
            catch (Exception)
            {
                return summary;
            }
        }

#if MEADOW_STEAM_GAMEPLAY
        internal SteamP2PSignaling Signaling => client.GetHandler<SteamP2PSignaling>()!;
        internal SteamLobbyChat Chat => client.GetHandler<SteamLobbyChat>()!;

        internal SteamMatchmaking.Lobby? CachedRoom(ulong roomId) =>
            matchmaking.GetLobby(RainWorldAppId, new SteamID(roomId));

        internal string PersonaName
        {
            get
            {
                string? name = client.GetHandler<SteamFriends>()?.GetPersonaName();
                return name is { Length: > 0 } ? name : "Steam player";
            }
        }

        internal async Task<SteamMatchmaking.Lobby> CreateRoomAsync(ELobbyType type, int maxPlayers,
            IReadOnlyDictionary<string, string> metadata, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsConnected)
                throw new SteamLobbyProbe.SteamConnectionClosedException();
            var job = matchmaking.CreateLobby(RainWorldAppId, type, maxPlayers, metadata: metadata)
                ?? throw new SteamLobbyProbe.SteamConnectionClosedException();
            SteamMatchmaking.CreateLobbyCallback created;
            try
            {
                created = await SteamLobbyProbe.WaitForReply(job.ToTask(), "Steam lobby creation", token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or SteamLobbyProbe.ReplyTimeoutException)
            {
                // Closing the session also cleans up a lobby whose creation reply arrived late.
                Dispose();
                throw;
            }

            if (created.Result != EResult.OK)
                throw new SteamLobbyProbe.SteamResultException("Steam lobby creation", created.Result);
            if (created.LobbySteamID is not SteamID id || id.ConvertToUInt64() == 0)
            {
                Dispose();
                throw new InvalidOperationException("Steam did not return the new lobby ID.");
            }

            ulong room = id.ConvertToUInt64();
            var lobby = CachedRoom(room);
            if (!token.IsCancellationRequested && lobby?.OwnerSteamID is SteamID owner && owner.ConvertToUInt64() == SteamAccountId)
                return lobby;
            LeaveRoom(room);
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Steam did not confirm this account as the new lobby owner.");
        }

        internal async Task<SteamMatchmaking.Lobby> JoinRoomAsync(ulong roomId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var job = matchmaking.JoinLobby(RainWorldAppId, new SteamID(roomId))
                ?? throw new SteamLobbyProbe.SteamConnectionClosedException();
            try
            {
                var joined = await SteamLobbyProbe.WaitForReply(job.ToTask(), "Steam lobby join", token).ConfigureAwait(false);
                if (joined.ChatRoomEnterResponse != EChatRoomEnterResponse.Success || joined.Lobby == null)
                    throw new InvalidOperationException("Steam lobby join returned " + joined.ChatRoomEnterResponse + ".");
                return joined.Lobby;
            }
            catch (OperationCanceledException)
            {
                // The membership reply can arrive after a cancelled join.
                Dispose();
                throw;
            }
            catch (SteamLobbyProbe.ReplyTimeoutException)
            {
                Dispose();
                throw;
            }
        }

        internal async Task<T?> ReadTextFilterSettingAsync<TRequest, T>(string method, TRequest request,
            CancellationToken token) where TRequest : ProtoBuf.IExtensible, new() where T : class, ProtoBuf.IExtensible, new()
        {
            var messages = client.GetHandler<SteamUnifiedMessages>();
            if (!IsConnected || messages == null) return null;
            var job = messages.SendMessage(method, request);
            var reply = await SteamLobbyProbe.WaitForReply(job.ToTask(), method, token).ConfigureAwait(false);
            return reply.Result == EResult.OK ? reply.GetDeserializedResponse<T>() : null;
        }

        internal async Task UpdateRoomCampaignAsync(ulong roomId, string campaign, CancellationToken token)
        {
            var room = CachedRoom(roomId);
            if (!IsConnected || room?.OwnerSteamID?.ConvertToUInt64() != SteamAccountId) return;
            var metadata = room.Metadata.ToDictionary(entry => entry.Key, entry => entry.Value);
            if (metadata.TryGetValue(MatchmakingManager.CAMPAIGN_KEY, out var previous) && previous == campaign) return;
            metadata[MatchmakingManager.CAMPAIGN_KEY] = campaign;

            token.ThrowIfCancellationRequested();
            var job = matchmaking.SetLobbyData(RainWorldAppId, room.SteamID, room.LobbyType, room.MaxMembers, room.LobbyFlags, metadata);
            var result = await SteamLobbyProbe.WaitForReply(job.ToTask(), "Steam lobby campaign update", token).ConfigureAwait(false);
            if (result.Result != EResult.OK)
                throw new SteamLobbyProbe.SteamResultException("Steam lobby campaign update", result.Result);
        }

        internal void LeaveRoom(ulong roomId)
        {
            if (IsConnected)
                matchmaking.LeaveLobby(RainWorldAppId, new SteamID(roomId));
        }
#endif

#if !MEADOW_STEAM_GAMEPLAY
        internal async Task<SteamLobbyJoinResult> TestJoinAsync(ulong roomId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsConnected)
                throw new SteamLobbyProbe.SteamConnectionClosedException();

            var id = new SteamID(roomId);
            var job = matchmaking.JoinLobby(RainWorldAppId, id)
                ?? throw new SteamLobbyProbe.SteamConnectionClosedException();
            try
            {
                var joined = await SteamLobbyProbe.WaitForReply(job.ToTask(), "Steam lobby join", token).ConfigureAwait(false);
                var result = new SteamLobbyJoinResult(joined.ChatRoomEnterResponse, joined.Lobby?.NumMembers ?? 0);
                if (!result.Success)
                    return result;

                var leave = matchmaking.LeaveLobby(RainWorldAppId, id)
                    ?? throw new SteamLobbyProbe.SteamConnectionClosedException();
                var response = await SteamLobbyProbe.WaitForReply(leave.ToTask(), "Steam lobby leave", token).ConfigureAwait(false);
                if (response.Result != EResult.OK)
                    throw new SteamLobbyProbe.SteamResultException("Steam lobby leave", response.Result);
                return result;
            }
            catch
            {
                // A late successful join must not leave the test account occupying a room.
                Dispose();
                throw;
            }
        }

#endif

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                TakeLoginTicket();
                client.Disconnect();
            }
        }
    }

#if MEADOW_STEAM_GAMEPLAY
    internal sealed class SteamP2PSignaling : ClientMsgHandler
    {
        private readonly Queue<(ulong peer, byte[] data)> incoming = new();

        internal bool Next(out (ulong peer, byte[] data) signal)
        {
            lock (incoming)
            {
                if (incoming.Count == 0) { signal = default; return false; }
                signal = incoming.Dequeue();
                return true;
            }
        }

        internal void Reset()
        {
            lock (incoming) incoming.Clear();
        }

        internal void Send(ulong peer, byte[] rendezvous)
        {
            ulong self = Client.SteamID?.ConvertToUInt64() ?? 0;
            if (self == 0 || peer == 0 || self == peer) return;
            var message = new ClientMsgProtobuf<CMsgClientP2PConnectionInfo>(EMsg.ClientP2PConnectionInfo);
            message.Body.steam_id_src = self;
            message.Body.steam_id_dest = peer;
            message.Body.app_id = SteamLobbySession.RainWorldAppId;
            message.Body.rendezvous = rendezvous;
            Client.Send(message);
        }

        public override void HandleMsg(IPacketMsg packet)
        {
            if (packet.MsgType != EMsg.ClientP2PConnectionInfo || !packet.IsProto) return;
            var body = new ClientMsgProtobuf<CMsgClientP2PConnectionInfo>(packet).Body;
            ulong self = Client.SteamID?.ConvertToUInt64() ?? 0;
            if (RejectionReason(self, body) != null) return;
            lock (incoming)
            {
                if (incoming.Count < 128)
                    incoming.Enqueue((body.steam_id_src, body.rendezvous!));
            }
        }

        internal static string? RejectionReason(ulong self, CMsgClientP2PConnectionInfo body)
        {
            if (self == 0) return "no self id";
            if (body.steam_id_dest != 0 && body.steam_id_dest != self) return "not addressed to us";
            if (body.steam_id_src == 0) return "no source";
            if (body.steam_id_src == self) return "from self";
            if (body.app_id != SteamLobbySession.RainWorldAppId) return "wrong app id";
            if (body.rendezvous is not { Length: > 0 and <= 8192 }) return "bad rendezvous size";

            try
            {
                using var encoded = new MemoryStream(body.rendezvous, writable: false);
                var rendezvous = ProtoBuf.Serializer.Deserialize<RendezvousIdentities>(encoded);
                if (rendezvous.From != SteamIdentity(body.steam_id_src))
                    return "rendezvous source does not match Steam";
                if (!string.IsNullOrEmpty(rendezvous.To))
                    return rendezvous.To == SteamIdentity(self) ? null : "rendezvous targets another identity";

                // Steam CM can omit its recipient field when forwarding. If the peer also
                // omits the identity, only let Valve match an existing connection by ID.
                return body.steam_id_dest != 0 || rendezvous.ConnectionId != 0
                    ? null : "rendezvous has no recipient";
            }
            catch (Exception error) when (error is ProtoException or IOException or OverflowException)
            {
                return "malformed rendezvous";
            }
        }

        private static string SteamIdentity(ulong id) => "steamid:" + id.ToString(CultureInfo.InvariantCulture);

        [ProtoContract]
        private sealed class RendezvousIdentities
        {
            [ProtoMember(1, DataFormat = DataFormat.FixedSize)] public uint ConnectionId { get; set; }
            [ProtoMember(8)] public string? From { get; set; }
            [ProtoMember(10)] public string? To { get; set; }
        }
    }
#endif

#if !MEADOW_STEAM_GAMEPLAY
    internal readonly struct SteamLobbyJoinResult
    {
        internal readonly EChatRoomEnterResponse Response;
        internal readonly int Members;
        internal bool Success => Response == EChatRoomEnterResponse.Success;

        internal SteamLobbyJoinResult(EChatRoomEnterResponse response, int members)
        {
            Response = response;
            Members = members;
        }
    }

#endif

    internal readonly struct SteamLobbySummary
    {
        internal const string PinnedKey = "pinned";
        internal readonly ulong Id;
        internal readonly string Name;
        internal readonly string Mode;
        internal readonly int Players;
        internal readonly int Capacity;
        internal readonly bool HasPassword;
        internal readonly string RequiredMods;
        internal readonly string BannedMods;
        internal readonly string Campaign;
        internal readonly string WhitelistMods;
        internal readonly bool Pinned;

        internal SteamLobbySummary(ulong id, IReadOnlyDictionary<string, string> metadata, int players, int capacity, bool trustedOwner = false)
        {
            Id = id;
            Name = ReadLabel(metadata, "name", "Unnamed lobby");
            Mode = ReadLabel(metadata, "mode", "Unknown mode");
            Players = players;
            Capacity = capacity;
            HasPassword = ReadBoolean(metadata, "password");
            RequiredMods = Read(metadata, "mods");
            BannedMods = Read(metadata, "banned_mods");
            Campaign = ReadLabel(metadata, "campaign", "");
            WhitelistMods = Read(metadata, "whitelistmods");
            Pinned = trustedOwner && ReadBoolean(metadata, PinnedKey);
        }

        private static string Read(IReadOnlyDictionary<string, string> metadata, string key)
            => metadata.TryGetValue(key, out var value) ? value ?? "" : "";

        private static bool ReadBoolean(IReadOnlyDictionary<string, string> metadata, string key)
            => bool.TryParse(Read(metadata, key), out var value) && value;

        private static string ReadLabel(IReadOnlyDictionary<string, string> metadata, string key, string fallback)
        {
            var label = new string(Read(metadata, key).Where(character => !char.IsControl(character)
                && character != '\u2028' && character != '\u2029').Take(160).ToArray()).Trim();
            return label.Length == 0 ? fallback : label;
        }
    }
}
#endif
