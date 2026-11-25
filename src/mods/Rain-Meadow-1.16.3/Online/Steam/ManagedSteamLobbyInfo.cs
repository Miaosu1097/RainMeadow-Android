#if MEADOW_STEAM_PROBE
namespace RainMeadow;

internal sealed class ManagedSteamLobbyInfo(SteamLobbySummary room) : LobbyInfo(
    Utils.GetTranslatedLobbyName(room.Name), room.Mode, room.Players, room.HasPassword,
    room.Capacity, room.RequiredMods, room.BannedMods, room.Campaign, room.WhitelistMods)
{
    internal readonly ulong LobbyId = room.Id;
    public override MatchmakingManager.MatchMakingDomain domain => MatchmakingManager.MatchMakingDomain.Steam;
    protected override object[] JoinCodeArgs => new object[] { LobbyId };
}
#endif
