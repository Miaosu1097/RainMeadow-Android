#if MEADOW_STEAM_GAMEPLAY
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamMatchmaking = SteamKit2.SteamMatchmaking;
using Steamworks;

namespace RainMeadow;

internal sealed class ManagedSteamGameplay : MatchmakingManager
{
    private static ManagedSteamGameplay? active;
    internal static SteamLobbySession? BrowserSession => active?.Session;
    internal static bool Owns(SteamLobbySession? session) =>
        session != null && active is ManagedSteamGameplay current && ReferenceEquals(current.Session, session) && current.roomId != 0;
    internal SteamLobbySession Session { get; }

    private AndroidSteamTransport? transport;
    private CancellationTokenSource? roomOperation;
    private bool roomOperationCancelled;
    internal bool HasPendingRoomOperation => roomOperation != null;
    private SteamMatchmaking.Lobby? roster;
    private ulong roomId;
    private ulong ownerId;
    private string? pendingCampaign;
    private Task? campaignUpdate;
    private readonly SteamTextFilter textFilter;
    private Stopwatch? connectionDeadline;
    private int signalsSent;
    private int signalsReceived;
    private readonly byte[] packet = new byte[65536];
    private readonly byte[] rendezvous = new byte[8192];

    private ManagedSteamGameplay(SteamLobbySession session)
    {
        Session = session;
        textFilter = new SteamTextFilter(session);
    }

    internal static void Activate(SteamLobbySession session)
    {
        if (!ReferenceEquals(active?.Session, session))
        {
            if (active != null) CloseSession(active.Session);
            active = new ManagedSteamGameplay(session);
            instances[MatchMakingDomain.Steam] = active;
        }

        active!.textFilter.Prepare();
        if (currentDomain != MatchMakingDomain.Steam)
            currentDomain = MatchMakingDomain.Steam;
        if (OnlineManager.netIO is not ManagedNetIO)
            OnlineManager.netIO = new ManagedNetIO(OnlineManager.netIO);
    }

    internal static void CloseSession(SteamLobbySession? session)
    {
        if (session != null && ReferenceEquals(active?.Session, session))
        {
            if (currentDomain == MatchMakingDomain.Steam)
                currentDomain = MatchMakingDomain.LAN;
            active?.LeaveLobby();
            active = null;
            instances.Remove(MatchMakingDomain.Steam);
        }
        session?.Dispose();
    }

    public override MatchMakingDomain domain => MatchMakingDomain.Steam;
    public override void initializeMePlayer() =>
        OnlineManager.mePlayer = new OnlinePlayer(new ManagedPlayerId(Session.SteamAccountId, "Me")) { isMe = true };
    public override MeadowPlayerId GetEmptyId() => new ManagedPlayerId(0, "");
    public override string GetLobbyID() => roomId == 0 ? "" : roomId.ToString();
    public override string GetCurrentLobbyJoinCode(string? password) => roomId == 0 ? "" : FormatJoinCode(password, roomId);
    public override void OpenInvitationOverlay() { }

    public override bool IsDev(MeadowPlayerId player) => player is SteamMatchmakingManager.SteamPlayerId steam
        && SteamMatchmakingManager.IsTrustedSteamAccount(steam.steamID.m_SteamID);

    public override bool IsTrustedCommunity(MeadowPlayerId player) => IsDev(player);

    public override void FilterMessage(ref string message) => message = textFilter.Apply(message);

    public override string FilterTeamName(string name) => textFilter.Apply(name);

    internal override void UpdateLobbyCampaign(string campaign)
    {
        if (roomId != 0 && ownerId == Session.SteamAccountId && OnlineManager.lobby?.isOwner == true)
            pendingCampaign = campaign;
    }

    private async Task PublishCampaignAsync(ulong publishingRoom, string campaign)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await Session.UpdateRoomCampaignAsync(publishingRoom, campaign, timeout.Token);
        }
        catch (Exception error)
        {
            if (roomId == publishingRoom)
                RainMeadow.Warn("Steam lobby campaign update failed: " + error.Message);
        }
    }

    protected override LobbyInfo? LobbyInfoFromJoinArgs(string[] parts, int argsStart) =>
        parts.Length > argsStart && ulong.TryParse(parts[argsStart], out ulong id)
            ? new ManagedSteamLobbyInfo(new SteamLobbySummary(id, new Dictionary<string, string>(), 0, 0))
            : null;

    public override async void RequestLobbyList()
    {
        try
        {
            var rooms = await Session.GetRoomsAsync(RainMeadow.MeadowVersionStr, CancellationToken.None);
            if (ReferenceEquals(active, this) && currentDomain == domain)
                OnLobbyListReceivedEvent(true, rooms.Select(r => (LobbyInfo)new ManagedSteamLobbyInfo(r)).ToArray());
        }
        catch (Exception error)
        {
            RainMeadow.Warn("Steam list request failed: " + error.GetType().Name);
            if (ReferenceEquals(active, this) && currentDomain == domain)
                OnLobbyListReceivedEvent(false, Array.Empty<LobbyInfo>());
        }
    }

    public override void CreateLobby(LobbyVisibility visibility, string mode, string? password, int? maxPlayers, bool pinned = false)
        => CreateHostedLobby(visibility, mode, password, maxPlayers, pinned, null);

    internal async void CreateHostedLobby(LobbyVisibility visibility, string mode, string? password,
        int? maxPlayers, bool pinned, string? campaign)
    {
        if (roomOperation != null || roomId != 0)
            return;
        if (!Session.IsConnected || Session.SteamAccountId == 0)
        {
            OnLobbyJoinedEvent(false, "Sign in to Steam before creating a lobby.");
            return;
        }

        using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        roomOperation = attempt;
        AndroidSteamTransport? opened = null;
        try
        {
            var type = ToSteamLobbyType(visibility);
            int capacity = Math.Max(2, Math.Min(32, maxPlayers ?? 4));
            var metadata = SteamMatchmakingManager.BuildLobbyMetadata(Session.PersonaName, mode, password, pinned);
            metadata[CAMPAIGN_KEY] = campaign ?? "";

            opened = await AndroidSteamTransport.ConnectAsync(Session, attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
            Session.Signaling.Reset();
            var hosted = await Session.CreateRoomAsync(type, capacity, metadata, attempt.Token);
            ulong createdRoomId = hosted.SteamID.ConvertToUInt64();
            if (attempt.IsCancellationRequested)
            {
                Session.LeaveRoom(createdRoomId);
                attempt.Token.ThrowIfCancellationRequested();
            }

            roomId = createdRoomId;
            transport = opened;
            opened = null;
            MAX_LOBBY = capacity;
            OnlineManager.lobby = new Lobby(new OnlineGameMode.OnlineGameModeType(mode), OnlineManager.mePlayer, password);
            OnlineManager.lobby.meadowTimeline = campaign;
            ApplyRoster(hosted);
        }
        catch (Exception error)
        {
            if (ReferenceEquals(roomOperation, attempt) && !roomOperationCancelled)
            {
                string message = error is OperationCanceledException ? "Steam lobby creation timed out or was cancelled."
                    : error is InvalidOperationException or SteamLobbyProbe.ReplyTimeoutException or SteamLobbyProbe.SteamResultException or FileNotFoundException
                        ? error.Message : "Steam lobby creation failed (" + error.GetType().Name + ").";
                RainMeadow.Warn(message);
                JoinLobby(false, message);
            }
            return;
        }
        finally
        {
            opened?.Dispose();
            roomOperation = null;
            roomOperationCancelled = false;
        }

        JoinLobby(true);
    }

    internal static SteamKit2.ELobbyType ToSteamLobbyType(LobbyVisibility visibility) => visibility switch
    {
        LobbyVisibility.Public => SteamKit2.ELobbyType.Public,
        LobbyVisibility.FriendsOnly => SteamKit2.ELobbyType.FriendsOnly,
        LobbyVisibility.Private => SteamKit2.ELobbyType.Private,
        _ => throw new ArgumentOutOfRangeException(nameof(visibility)),
    };

    internal void CancelPendingCreation()
    {
        if (roomId == 0 && roomOperation != null)
            LeaveLobby();
    }

    public override async void RequestJoinLobby(LobbyInfo lobby, string? password)
    {
        if (roomOperation != null || roomId != 0)
            return;
        if (lobby is not ManagedSteamLobbyInfo steamLobby)
        {
            OnLobbyJoinedEvent(false, "Steam join is unavailable.");
            return;
        }

        using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        roomOperation = attempt;
        AndroidSteamTransport? opened = null;
        try
        {
            OnlineManager.currentlyJoiningLobby = lobby;
            opened = await AndroidSteamTransport.ConnectAsync(Session, attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
            Session.Signaling.Reset();

            var joined = await Session.JoinRoomAsync(steamLobby.LobbyId, attempt.Token);
            if (attempt.IsCancellationRequested)
            {
                Session.LeaveRoom(steamLobby.LobbyId);
                attempt.Token.ThrowIfCancellationRequested();
            }
            roomId = steamLobby.LobbyId;
            transport = opened;
            opened = null;
            ApplyRoster(joined);
            var owner = GetLobbyOwner();
            if (owner == null || owner.isMe)
                throw new InvalidOperationException("Steam lobby has no remote host.");
            if (!joined.Metadata.TryGetValue(MODE_KEY, out string? mode) || string.IsNullOrEmpty(mode))
                throw new InvalidOperationException("Steam lobby has no Rain Meadow game mode.");

            signalsSent = 0;
            signalsReceived = 0;
            connectionDeadline = Stopwatch.StartNew();
            OnlineManager.lobby = new Lobby(new OnlineGameMode.OnlineGameModeType(mode), owner, password);
        }
        catch (Exception error)
        {
            if (ReferenceEquals(roomOperation, attempt) && !roomOperationCancelled)
            {
                string message = error is OperationCanceledException ? "Steam join timed out or was cancelled."
                    : error is InvalidOperationException or SteamLobbyProbe.ReplyTimeoutException or FileNotFoundException
                        ? error.Message : "Steam join failed (" + error.GetType().Name + ").";
                RainMeadow.Warn(message);
                JoinLobby(false, message);
            }
        }
        finally
        {
            opened?.Dispose();
            roomOperation = null;
            roomOperationCancelled = false;
        }
    }

    public override void JoinLobby(bool success, string failReason = "")
    {
        if (!success) LeaveLobby();
        else connectionDeadline = null;
        OnLobbyJoinedEvent(success, failReason);
    }

    public override void LeaveLobby()
    {
        if (roomOperation != null)
        {
            roomOperationCancelled = true;
            roomOperation.Cancel();
        }
        connectionDeadline = null;
        if (roomId != 0)
        {
            ulong previous = roomId;
            roomId = 0;
            Session.LeaveRoom(previous);
            OnLobbyLeavingEvent();
        }
        ownerId = 0;
        pendingCampaign = null;
        campaignUpdate = null;
        roster = null;
        transport?.Dispose();
        transport = null;
        Session.Signaling.Reset();
    }

    public override OnlinePlayer GetLobbyOwner() => FindPlayer(ownerId)!;

    private static OnlinePlayer? FindPlayer(ulong id) =>
        OnlineManager.players.FirstOrDefault(p => p.id is ManagedPlayerId steam && steam.steamID.m_SteamID == id);

    private void ApplyRoster(SteamMatchmaking.Lobby lobby)
    {
        roster = lobby;
        ownerId = lobby.OwnerSteamID?.ConvertToUInt64() ?? 0;
        var members = lobby.Members.ToDictionary(p => p.SteamID.ConvertToUInt64(), p => p.PersonaName);
        if (ownerId != 0 && !members.ContainsKey(ownerId)) members[ownerId] = "Steam host";
        foreach (var old in OnlineManager.players.ToArray())
        {
            if (!old.isMe && old.id is ManagedPlayerId steam && !members.ContainsKey(steam.steamID.m_SteamID))
                HandleDisconnect(old);
        }
        foreach (var member in members)
        {
            if (member.Key == Session.SteamAccountId || FindPlayer(member.Key) != null) continue;
            var player = new OnlinePlayer(new ManagedPlayerId(member.Key, member.Value ?? ""));
            OnlineManager.players.Add(player);
            HandleJoin(player);
        }
        transport!.SetPeers(members.Keys.Where(id => id != Session.SteamAccountId).ToArray());
        OnPlayerListReceivedEvent(playerList.ToArray());
    }

    private void Send(OnlinePlayer player, int channel, byte[] data, int size, bool reliable)
    {
        if (player.id is ManagedPlayerId steam)
            transport?.Send(steam.steamID.m_SteamID, channel, data, size, reliable);
    }

    private void Pump()
    {
        Session.Pump();
        if (roomId == 0 || transport is not AndroidSteamTransport net) return;
        if (!Session.IsConnected)
        {
            JoinLobby(false, "The Steam connection closed.");
            return;
        }

        var nextRoster = Session.CachedRoom(roomId);
        if (nextRoster != null && nextRoster.Members.Count > 0 && !ReferenceEquals(roster, nextRoster))
            ApplyRoster(nextRoster);
        if (pendingCampaign is string campaign && (campaignUpdate == null || campaignUpdate.IsCompleted)
            && ownerId == Session.SteamAccountId && OnlineManager.lobby?.isOwner == true)
        {
            pendingCampaign = null;
            campaignUpdate = PublishCampaignAsync(roomId, campaign);
        }

        for (int i = 0; i < 32 && Session.Signaling.Next(out var signal); ++i)
            if (net.ReceiveSignal(signal.peer, signal.data)) signalsReceived++;

        for (int channel = 0; channel <= 1; ++channel)
        {
            for (int i = 0; i < 32; ++i)
            {
                int size = net.Read(channel, out ulong peer, packet);
                if (size == 0) break;
                if (size < 0 || FindPlayer(peer) is not OnlinePlayer player) continue;
                try
                {
                    if (channel == 0)
                    {
                        Array.Copy(packet, OnlineManager.serializer.buffer, size);
                        OnlineManager.serializer.ReadData(player, size);
                    }
                    else
                    {
                        var custom = new byte[size];
                        Array.Copy(packet, custom, size);
                        CustomManager.ReadCustom(player, custom);
                    }
                }
                catch (Exception error)
                {
                    if (channel == 0) OnlineManager.serializer.EndRead();
                    RainMeadow.Warn("Steam game packet rejected: " + error.GetType().Name);
                }
                if (!ReferenceEquals(net, transport)) return;
            }
        }
        for (int i = 0; i < 32; ++i)
        {
            int size = net.PopSignal(out ulong peer, rendezvous);
            if (size <= 0) break;
            var signal = new byte[size];
            Array.Copy(rendezvous, signal, size);
            Session.Signaling.Send(peer, signal);
            signalsSent++;
        }

        if (connectionDeadline is Stopwatch deadline)
        {
            int state = net.State(ownerId);
            if (state == 3)
            {
                connectionDeadline = null;
            }
            else if (deadline.Elapsed.TotalSeconds >= 20)
            {
#if MEADOW_STEAM_SDR
                RainMeadow.Warn($"Steam P2P timed out. State={state}, signals sent={signalsSent}, received={signalsReceived}. {net.RelaySummary()}");
                JoinLobby(false, "Steam P2P could not establish a connection to the host.");
#else
                RainMeadow.Warn($"Steam direct route timed out. State={state}, signals sent={signalsSent}, received={signalsReceived}.");
                JoinLobby(false, "Steam P2P could not reach the host directly. This build cannot use Steam relay.");
#endif
            }
        }
    }

    private sealed class ManagedPlayerId : SteamMatchmakingManager.SteamPlayerId
    {
        internal ManagedPlayerId(ulong id, string persona)
        {
            steamID = new CSteamID(id);
            name = string.IsNullOrEmpty(persona) ? "Steam player" : persona;
        }
        public override string GetPersonaName() => DisplayName;
        public override bool canOpenProfileLink => false;
        public override void OpenProfileLink() { }
    }

    private sealed class ManagedNetIO : NetIO
    {
        private readonly NetIO fallback;
        internal ManagedNetIO(NetIO fallback) => this.fallback = fallback;
        private static ManagedSteamGameplay? Steam => currentDomain == MatchMakingDomain.Steam ? active : null;
        public override void ForgetEverything() => fallback.ForgetEverything();
        public override void ForgetPlayer(OnlinePlayer player)
        {
            if (Steam == null) fallback.ForgetPlayer(player);
        }
        public override void SendP2P(OnlinePlayer player, Packet packet, SendType sendType, bool start_conversation = false)
        {
            if (Steam == null) fallback.SendP2P(player, packet, sendType, start_conversation);
        }
        public override void RecieveData()
        {
            if (Steam is ManagedSteamGameplay backend) backend.Pump();
            else fallback.RecieveData();
        }
        public override void SendSessionData(OnlinePlayer player)
        {
            if (Steam is not ManagedSteamGameplay backend)
            {
                fallback.SendSessionData(player);
                return;
            }
            try
            {
                int size = checked((int)OnlineManager.serializer.WriteData(player));
                backend.Send(player, 0, OnlineManager.serializer.buffer, size, false);
            }
            catch (Exception error)
            {
                OnlineManager.serializer.EndWrite();
                RainMeadow.Warn("Steam game state send failed: " + error.GetType().Name);
            }
        }
        public override void SendCustomData(OnlinePlayer player, CustomPacket packet, SendType sendType)
        {
            if (Steam is not ManagedSteamGameplay backend)
            {
                fallback.SendCustomData(player, packet, sendType);
                return;
            }
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            packet.SteamEncode(stream, writer);
            writer.Flush();
            backend.Send(player, 1, stream.GetBuffer(), checked((int)stream.Length), sendType == SendType.Reliable);
        }
    }
}
#endif
