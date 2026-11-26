#if MEADOW_STEAM_PROBE
namespace RainMeadow;

internal sealed class ManagedSteamLobbyInfo : LobbyInfo
{
    internal readonly ulong LobbyId;

    internal ManagedSteamLobbyInfo(SteamLobbySummary room) : base(
        Utils.GetTranslatedLobbyName(room.Name), room.Mode, room.Players, room.HasPassword,
        room.Capacity, room.RequiredMods, room.BannedMods, room.Campaign, room.WhitelistMods)
    {
        LobbyId = room.Id;
        pinned = room.Pinned;
    }
    public override MatchmakingManager.MatchMakingDomain domain => MatchmakingManager.MatchMakingDomain.Steam;
    protected override object[] JoinCodeArgs => new object[] { LobbyId };
}
#endif
