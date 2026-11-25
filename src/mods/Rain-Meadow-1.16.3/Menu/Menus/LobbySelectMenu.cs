using System.Net;
#if MEADOW_STEAM_PROBE
using System;
using System.Linq;
using System.Threading;
#endif
using Menu;
using RainMeadow.UI.Components;
using RainMeadow.UI.Dialogs;
using RainMeadow.UI.Menus.Panels;
using Steamworks;
using UnityEngine;

namespace RainMeadow.UI.Menus;

public class LobbySelectMenu : SmartMenu
{
    public LobbySelectMetadataPanel metadataPanel;
    public ProperlyAlignedMenuLabel statisticsLabel;
    public LobbyCardSelector lobbyCardSelector;
    public SimplerButton joinButton;
    private readonly SimplerButton createButton;
    private readonly LobbySelectNetworkPanel networkPanel;
    private Music.Song? androidLobbySong;
    private bool previousLobbySongLoop;
#if MEADOW_STEAM_PROBE
    private readonly CancellationTokenSource steamMenuLifetime = new();
    private SteamLobbySession? steamSession;
    private SteamLobbySession? steamListRequest;
#if !MEADOW_STEAM_GAMEPLAY
    private SteamLobbySession? steamJoinRequest;
    private SteamLobbySession? steamIdentityRequest;
#endif
    private bool restoreSteamSignIn = true;
#endif
    public DialogAsyncWaitCancellable? joiningDialog;
    public LobbyInfo? lastSelectedLobbyInfo;

    private int joiningTimeoutCount = 0;
    private bool joinResolved = false;
    private LobbyInfo? lastJoinAttempt;
    private const int FastTimeoutCount = 200;
    private int TimeoutTicks = RainMeadow.rainMeadowOptions.JoiningTimeout.Value * 40;
    private const string ERROR_Unexpected = "Something went wrong...";
    private const string ERROR_Cancelled = "Cancelled";

    public override MenuScene.SceneID GetScene =>
        ModManager.MMF ? manager.rainWorld.options.subBackground : MenuScene.SceneID.Landscape_SU;

    public LobbySelectMenu(ProcessManager manager)
        : base(manager, RainMeadow.Ext_ProcessID.LobbySelectMenu)
    {
        RainMeadow.DebugMe();

        backTarget = ProcessManager.ProcessID.MainMenu;

        scene.AddIllustration(
            new MenuIllustration(
                this,
                scene,
                "illustrations/rainmeadowtitle",
                Utils.GetMeadowTitleFileName(true),
                new Vector2(-2.99f, 265.01f),
                true,
                false
            )
        );
        scene.AddIllustration(
            new MenuIllustration(
                this,
                scene,
                "illustrations/rainmeadowtitle",
                Utils.GetMeadowTitleFileName(false),
                new Vector2(-2.99f, 265.01f),
                true,
                false
            )
        );
        scene.flatIllustrations[scene.flatIllustrations.Count - 1].sprite.shader = manager
            .rainWorld
            .Shaders["MenuText"];

        SimplerButton creditsButton = new(
            this,
            mainPage,
            Translate("CREDITS"),
            new Vector2(200, 660),
            new Vector2(110, 30),
            Translate("View Rain Meadow credits")
        );
        creditsButton.OnClick += (btn) =>
            manager.RequestMainProcessSwitch(RainMeadow.Ext_ProcessID.MeadowCredits);

        statisticsLabel = new ProperlyAlignedMenuLabel(
            this,
            mainPage,
            $"{Translate("Online:")} 0 | {Translate("Lobbies:")} 0",
            new Vector2(
                (1366f - manager.rainWorld.screenSize.x) / 2f + 5f,
                manager.rainWorld.screenSize.y - 768f + 20f
            ),
            Vector2.zero,
            false
        );

        ProperlyAlignedMenuLabel versionLabel = new(
            this,
            mainPage,
            $"{Translate("Rain Meadow Version:")} {RainMeadow.MeadowVersionStr}",
            new Vector2(
                (1366f - manager.rainWorld.screenSize.x) / 2f + 5f,
                manager.rainWorld.screenSize.y - 768f
            ),
            Vector2.zero,
            false
        );

        lobbyCardSelector = new LobbyCardSelector(this, mainPage, new Vector2(225, 105));
        lobbyCardSelector.RefreshLobbies += RefreshLobbyList;
        lobbyCardSelector.OnLobbyCardsUpdated += () =>
        {
            metadataPanel?.ClearLobbyInfo();
            lastSelectedLobbyInfo = null;
        };
        lobbyCardSelector.OnLobbyCardClicked += (lobbyInfo) =>
        {
            if (lastSelectedLobbyInfo == lobbyInfo)
                JoinLobbyChecks(lobbyInfo);
            metadataPanel?.UpdateLobbyInfo(lobbyInfo);
            lastSelectedLobbyInfo = lobbyInfo;
        };

        joinButton = new SimplerButton(
            this,
            mainPage,
            Translate("JOIN"),
            new Vector2(1056, 50),
            new Vector2(110, 30),
            Translate("Join selected lobby")
        );
        joinButton.buttonBehav.greyedOut = true;
        // button can't be clicked if SelectedLobby is null (see Update())
        joinButton.OnClick += (btn) => JoinLobbyChecks(lobbyCardSelector.SelectedLobby!);

        createButton = new(
            this,
            mainPage,
            Translate("CREATE!"),
            new Vector2(936, 50),
            new Vector2(110, 30),
            Translate("Create a new lobby")
        );
        createButton.OnClick += (btn) =>
        {
#if MEADOW_STEAM_PROBE
            if (networkPanel.IsManagedSteamSelected)
            {
#if MEADOW_STEAM_GAMEPLAY
                if (steamSession is not SteamLobbySession { IsConnected: true } session)
                    return;
                ManagedSteamGameplay.Activate(session);
                // The managed matchmaker keeps the login while the creation menu opens.
                steamSession = null;
#else
                return;
#endif
            }
#endif
            manager.RequestMainProcessSwitch(RainMeadow.Ext_ProcessID.LobbyCreateMenu);
            PlaySound(SoundID.MENU_Switch_Page_In);
        };

        TabContainer tabContainer = new(
            this,
            mainPage,
            new Vector2(830, 110),
            new Vector2(300, 500),
            true
        );

        TabContainer.Tab metadataTab = new(this, tabContainer);
        metadataPanel = new LobbySelectMetadataPanel(this, metadataTab, Vector2.zero);
        metadataTab.AddObjects(metadataPanel);
        tabContainer.AddTab(metadataTab, Translate("Selected Lobby"));

        TabContainer.Tab filterTab = new(this, tabContainer);
        LobbySelectFiltersPanel filtersPanel = new(this, filterTab, Vector2.zero);
        filtersPanel.OnFilterUpdated += lobbyCardSelector.UpdateFilters;
        filterTab.AddObjects(filtersPanel);
        tabContainer.AddTab(filterTab, Translate("Filters"));

        TabContainer.Tab networkTab = new(this, tabContainer);
        networkPanel = new(this, networkTab, Vector2.zero);
#if MEADOW_STEAM_PROBE
        networkPanel.OnSteamSignedIn += UseSteamSession;
        networkPanel.OnSteamSignedOut += SignOutSteam;
#if !MEADOW_STEAM_GAMEPLAY
        networkPanel.OnSteamIdentityCheck += CheckSteamIdentity;
#endif
#endif
        networkPanel.OnDirectConnectButtonClick += () =>
        {
            DirectConnectionDialog directConnectionDialog = new(manager, UIUtils.DIALOG_SIZE);
            directConnectionDialog.OnDirectConnectConfirm += DirectConnect;
            manager.ShowDialog(directConnectionDialog);
        };
        networkPanel.OnDomainChanged += () =>
        {
            lobbyCardSelector.UpdateLobbyInfos(System.Array.Empty<LobbyInfo>());
            UpdateStats();
            RefreshLobbyList();
        };
        networkTab.AddObjects(networkPanel);
        tabContainer.AddTab(networkTab, Translate("Network"));

        mainPage.subObjects.AddRange([
            creditsButton,
            statisticsLabel,
            versionLabel,
            joinButton,
            createButton,
            lobbyCardSelector,
            tabContainer,
        ]);

        if (
            RainMeadow.rainMeadowOptions.GetLobbyMusic(out string song)
            && !string.IsNullOrEmpty(song)
            && manager.musicPlayer is Music.MusicPlayer musicPlayer
        )
        {
            musicPlayer.MenuRequestsSong(song, 1, 0);
            if (Application.platform == RuntimePlatform.Android
                && (musicPlayer.nextSong ?? musicPlayer.song) is Music.Song menuSong
                && menuSong.context == Music.MusicPlayer.MusicContext.Menu)
            {
                androidLobbySong = menuSong;
                previousLobbySongLoop = menuSong.Loop;
                menuSong.Loop = true;
            }
        }

        // Lobby machine go!
        MatchmakingManager.OnLobbyListReceived += OnLobbyListReceived;
        MatchmakingManager.OnLobbyJoined += OnLobbyJoined;

        if (
            MatchmakingManager.supported_matchmakers.Contains(
                MatchmakingManager.MatchMakingDomain.Steam
            )
        )
            SteamNetworkingUtils.InitRelayNetworkAccess();
        RefreshLobbyList();

        if (MatchmakingManager.ConsumePendingJoinCode(out var joinCode))
        {
            if (MatchmakingManager.TryParseJoinCode(joinCode, out _, out var pendingLobbyInfo, out var joinPassword)
                && pendingLobbyInfo != null)
                RequestJoinLobby(pendingLobbyInfo, joinPassword);
            else
                RainMeadow.Error($"Failed to resolve pending join code: {joinCode}");
        }

        if (!string.IsNullOrEmpty(RainMeadow.NewVersionAvailable))
            manager.ShowDialog(new UpdateDialog(manager));
    }

    private void RefreshLobbyList()
    {
#if MEADOW_STEAM_PROBE
        if (networkPanel.IsManagedSteamSelected && steamSession is SteamLobbySession session)
        {
            RefreshSteamRooms(session);
            return;
        }
#endif
        MatchmakingManager.currentInstance.RequestLobbyList();
    }

#if MEADOW_STEAM_PROBE
    private void UseSteamSession(SteamLobbySession session)
    {
        if (steamMenuLifetime.IsCancellationRequested)
        {
            session.Dispose();
            return;
        }

#if MEADOW_STEAM_GAMEPLAY
        if (!ReferenceEquals(steamSession, session)) ManagedSteamGameplay.CloseSession(steamSession);
#else
        steamSession?.Dispose();
#endif
        steamSession = session;
        networkPanel.EnableManagedSteam();
        RefreshSteamRooms(session);
    }

    private void SignOutSteam()
    {
        restoreSteamSignIn = false;
        var session = steamSession;
        steamSession = null;
#if MEADOW_STEAM_GAMEPLAY
        ManagedSteamGameplay.CloseSession(session);
#else
        session?.Dispose();
#endif
        networkPanel.DisableManagedSteam();
    }

    private bool IsCurrentSteamView(SteamLobbySession session)
        => !steamMenuLifetime.IsCancellationRequested && ReferenceEquals(steamSession, session)
            && networkPanel.IsManagedSteamSelected;

    private async void RefreshSteamRooms(SteamLobbySession session)
    {
        if (ReferenceEquals(steamListRequest, session))
            return;
        steamListRequest = session;
        statisticsLabel.text = Translate("Requesting Steam lobbies...");
        try
        {
            var rooms = await session.GetRoomsAsync(RainMeadow.MeadowVersionStr, steamMenuLifetime.Token);
            if (!IsCurrentSteamView(session))
                return;
            LobbyInfo[] lobbies = rooms.Select(room => (LobbyInfo)new ManagedSteamLobbyInfo(room)).ToArray();
            lobbyCardSelector.UpdateLobbyInfos(lobbies);
            UpdateStats();
            if (rooms.Length == 0)
                statisticsLabel.text = Translate("No matching Steam rooms") + " | " + RainMeadow.MeadowVersionStr;
        }
        catch (Exception error)
        {
            if (IsCurrentSteamView(session))
                statisticsLabel.text = SteamRequestError(error);
        }
        finally
        {
            if (ReferenceEquals(steamListRequest, session))
                steamListRequest = null;
        }
    }

#if !MEADOW_STEAM_GAMEPLAY
    private async void CheckSteamIdentity()
    {
        var session = steamSession;
        if (session == null || !IsCurrentSteamView(session) || ReferenceEquals(steamIdentityRequest, session))
            return;

        steamIdentityRequest = session;
        networkPanel.SteamIdentityCheckPending = true;
        string report;
        try
        {
            report = await SteamNetworkIdentityCheck.RunAsync(session, steamMenuLifetime.Token);
            RainMeadow.Info(report);
        }
        catch (Exception error)
        {
            report = SteamRequestError(error);
            RainMeadow.Warn(report);
        }
        finally
        {
            if (ReferenceEquals(steamIdentityRequest, session))
            {
                steamIdentityRequest = null;
                networkPanel.SteamIdentityCheckPending = false;
            }
        }

        if (IsCurrentSteamView(session))
            manager.ShowDialog(new NotifyDialog(manager, report, new Vector2(760f, 200f)));
    }

    private async void TestSteamJoin(ManagedSteamLobbyInfo lobby)
    {
        var session = steamSession;
        if (session == null || !IsCurrentSteamView(session) || ReferenceEquals(steamJoinRequest, session))
            return;

        steamJoinRequest = session;
        string report;
        try
        {
            var result = await session.TestJoinAsync(lobby.LobbyId, steamMenuLifetime.Token);
            if (result.Success)
            {
                report = $"Steam accepted the lobby join. Members at join: {result.Members}.<LINE>" +
                    "The test left the Steam lobby after checking membership.<LINE>" +
                    "Rain Meadow game transport over Steam is pending.";
            }
            else
            {
                report = $"Steam lobby join returned {result.Response}.";
            }
            RainMeadow.Info(report.Replace("<LINE>", " "));
        }
        catch (Exception error)
        {
            report = SteamRequestError(error);
            RainMeadow.Warn(report);
        }
        finally
        {
            if (ReferenceEquals(steamJoinRequest, session))
                steamJoinRequest = null;
        }

        if (IsCurrentSteamView(session))
            manager.ShowDialog(new NotifyDialog(manager, report, new Vector2(740f, 320f)));
    }

#endif

    private static string SteamRequestError(Exception error)
        => error switch
        {
            SteamLobbyProbe.ReplyTimeoutException or SteamLobbyProbe.SteamResultException => error.Message,
            SteamLobbyProbe.SteamConnectionClosedException or OperationCanceledException => "Steam session closed. Sign in again.",
            _ => "Steam request failed (" + error.GetType().Name + ").",
        };
#endif

    public void UpdateStats()
    {
        int playerCount = 0;
        foreach (LobbyInfo lobbyInfo in lobbyCardSelector.lobbyInfos)
            playerCount += lobbyInfo.playerCount;
        statisticsLabel.text =
            $"{Translate("Online:")} {playerCount} | {Translate("Lobbies:")} {lobbyCardSelector.lobbyInfos.Length}";
    }

    public void JoinLobbyChecks(LobbyInfo lobbyInfo)
    {
#if MEADOW_STEAM_PROBE && !MEADOW_STEAM_GAMEPLAY
        if (lobbyInfo is ManagedSteamLobbyInfo steamLobby)
        {
            TestSteamJoin(steamLobby);
            return;
        }
#endif
        if (!EnsureSteamSessionForJoin(lobbyInfo))
            return;
        MatchmakingManager.MAX_LOBBY = lobbyInfo.maxPlayerCount;
        if (lobbyInfo.maxPlayerCount <= lobbyInfo.playerCount)
        {
            manager.ShowDialog(
                new NotifyDialog(
                    manager,
                    "Failed to join lobby.<LINE>Lobby is full",
                    UIUtils.DIALOG_SIZE
                )
            );
            return;
        }

        if (lobbyInfo.hasPassword)
        {
            InputDialog passwordDialog = new(manager, "Password Required", UIUtils.DIALOG_SIZE);
            passwordDialog.OnConfirm += (password) => StartJoiningLobby(lobbyInfo, password);
            manager.ShowDialog(passwordDialog);
        }
        else
            StartJoiningLobby(lobbyInfo);
    }

    // ManagedSteamLobbyInfo resolves its join code through MatchmakingManager.instances,
    // so the Steam manager must be registered before StartJoiningLobby asks for it.
    private bool EnsureSteamSessionForJoin(LobbyInfo lobbyInfo)
    {
#if MEADOW_STEAM_GAMEPLAY
        if (lobbyInfo is ManagedSteamLobbyInfo)
        {
            if (steamSession is not SteamLobbySession session || !session.IsConnected)
            {
                manager.ShowDialog(new NotifyDialog(manager, "Sign in to Steam before joining.", UIUtils.DIALOG_SIZE));
                return false;
            }
            ManagedSteamGameplay.Activate(session);
        }
#endif
        return true;
    }

    public void StartJoiningLobby(LobbyInfo lobbyInfo, string? password = null)
    {
        RainMeadowModManager.CheckMods(
            lobbyInfo.requiredMods.Split('\n'),
            lobbyInfo.bannedMods.Split('\n'),
            () => RequestJoinLobby(lobbyInfo, password),
            false,
            lobbyInfo.GetLobbyJoinCode(password),
            lobbyInfo.WhitelistModArray
        );
    }

    public void RequestJoinLobby(LobbyInfo lobbyInfo, string? password)
    {
        if (!EnsureSteamSessionForJoin(lobbyInfo))
            return;
        manager.ShowDialog(
            joiningDialog = new DialogAsyncWaitCancellable(
                this,
                Translate("Joining lobby..."),
                UIUtils.DIALOG_SIZE,
                (_) => MatchmakingManager.currentInstance.JoinLobby(false, ERROR_Cancelled)
            )
        );
        joiningTimeoutCount = 0;
        joinResolved = false;
        lastJoinAttempt = lobbyInfo;
        MatchmakingManager.lastJoinFailWasWrongPassword = false;
        MatchmakingManager.currentInstance.RequestJoinLobby(lobbyInfo, password);
    }

    public void DirectConnect(string ip, string? password)
    {
        IPEndPoint? endpoint = UDPPeerManager.GetEndPointByName(ip);
        if (endpoint == null)
        {
            manager.ShowDialog(
                new NotifyDialog(
                    manager,
                    "Invalid Address, IP Address format should be xxx.xxx.xxx.xxx:port",
                    UIUtils.DIALOG_SIZE
                )
            );
            return;
        }

        LANMatchmakingManager.LANLobbyInfo fakeLobbyInfo = new(
            endpoint,
            "Direct Connection",
            "Meadow",
            0,
            true,
            2
        );

        if (UDPPeerManager.isEndpointLocal(endpoint))
        {
            RequestJoinLobby(fakeLobbyInfo, password);
            return;
        }

        NotLocalDialog notLocalDialog = new(manager);
        notLocalDialog.OnConfirm += () => RequestJoinLobby(fakeLobbyInfo, password);
        manager.ShowDialog(notLocalDialog);
    }

    public void OnLobbyListReceived(bool ok, LobbyInfo[] lobbies)
    {
#if MEADOW_STEAM_PROBE
        if (networkPanel.IsManagedSteamSelected)
            return;
#endif
        if (!ok)
        {
            RainMeadow.Warn("Lobby IO failure");
            return;
        }

        lobbyCardSelector.UpdateLobbyInfos(lobbies);
        UpdateStats();
    }

    public void OnLobbyJoined(bool ok, string error)
    {
        joinResolved = true;

        if (joiningDialog != null)
            manager.StopSideProcess(joiningDialog);

        if (ok)
            return;

        string errorMessage = "Failed to join lobby:<LINE>" + error;
        RainMeadow.Error(errorMessage);

        if (MatchmakingManager.lastJoinFailWasWrongPassword && lastJoinAttempt is LobbyInfo retryLobby)
        {
            MatchmakingManager.lastJoinFailWasWrongPassword = false;
            InputDialog passwordDialog = new(manager, "Password Required", UIUtils.DIALOG_SIZE);
            passwordDialog.OnConfirm += (password) => RequestJoinLobby(retryLobby, password);
            manager.ShowDialog(passwordDialog);
        }
        else if (error != ERROR_Cancelled)
        {
            manager.ShowDialog(new NotifyDialog(manager, errorMessage, UIUtils.DIALOG_SIZE));
        }

        // Stop any process/menu switch when an error occur 
        if (OnlineManager.instance.manager._processSwitchQueue.Count > 0)
        {
            RainMeadow.Warn("Found process(es) in queue, clearing it!");
            OnlineManager.instance.manager._processSwitchQueue.Clear();
        }
    }

    public override void Update()
    {
        base.Update();
#if MEADOW_STEAM_PROBE
        if (restoreSteamSignIn && manager.dialog == null && manager.upcomingProcess == null)
        {
            restoreSteamSignIn = false;
#if MEADOW_STEAM_GAMEPLAY
            if (ManagedSteamGameplay.BrowserSession is SteamLobbySession { IsConnected: true } previous)
                UseSteamSession(previous);
            else
#endif
                networkPanel.RestoreSteamSignIn();
        }

        if (steamSession is SteamLobbySession session)
        {
            session.Pump();
            if (!session.IsConnected
#if MEADOW_STEAM_GAMEPLAY
                && !ManagedSteamGameplay.Owns(session)
#else
                && !ReferenceEquals(steamJoinRequest, session)
#endif
            )
            {
                steamSession = null;
#if MEADOW_STEAM_GAMEPLAY
                ManagedSteamGameplay.CloseSession(session);
#else
                session.Dispose();
#endif
                networkPanel.DisableManagedSteam();
            }
        }
        bool managedSteam = networkPanel.IsManagedSteamSelected;
#if MEADOW_STEAM_GAMEPLAY
        createButton.buttonBehav.greyedOut = managedSteam && steamSession is not { IsConnected: true };
#else
        createButton.buttonBehav.greyedOut = managedSteam;
#endif
#if MEADOW_STEAM_GAMEPLAY
        bool joiningSteam = managedSteam && joiningDialog != null && manager.dialog == joiningDialog;
        var joinText = Translate(joiningSteam ? "JOINING..." : "JOIN");
#else
        bool joiningSteam = managedSteam && steamJoinRequest != null && ReferenceEquals(steamJoinRequest, steamSession);
        var joinText = Translate(joiningSteam ? "TESTING..." : managedSteam ? "TEST JOIN" : "JOIN");
#endif
        if (joinButton.menuLabel.text != joinText)
            joinButton.menuLabel.text = joinText;
#endif
        joinButton.buttonBehav.greyedOut =
#if MEADOW_STEAM_PROBE
            joiningSteam ||
#endif
            lobbyCardSelector.SelectedLobby == null;

        if (joinResolved)
        {
            if (joiningDialog is not null && manager.dialog == joiningDialog)
            {
                RainMeadow.Warn("Joining dialog was shown after the join resolved, closing it");
                manager.StopSideProcess(joiningDialog);
            }
        }
        else if (joiningDialog is not null && OnlineManager.lobby is not null)
        {
            if (RainMeadow.rainMeadowOptions.JoiningExtraInfo.Value)
            {
                int attempts = OnlineManager.lobby.enumsChecked 
                    ? OnlineManager.lobby.joiningAttempts 
                    : OnlineManager.lobby.enumSyncAttempts;
                int step = !OnlineManager.lobby.enumsChecked
                    ? 0
                    : OnlineManager.lobby.isRequesting
                        ? 1
                        : 2;

                string text = Translate("Joining lobby...") + $" {step}/2";
                if (attempts > 2) text += "\n" + Translate("(Attempt <NUM>)").Replace("<NUM>", attempts.ToString());
                joiningDialog.SetText(text);
            }

            if (TimeoutTicks > 0)
            {
                if (OnlineManager.lobby.enumsChecked 
                && !OnlineManager.lobby.isRequesting 
                && TimeoutTicks - joiningTimeoutCount > FastTimeoutCount) // Something went wrong, you should've joined by now
                {
                    joiningTimeoutCount = TimeoutTicks - FastTimeoutCount; // Making the timeout shorter
                }

                if (++joiningTimeoutCount >= TimeoutTicks)
                {
                    if (OnlineManager.lobby.joiningEvent is not null)
                    {
                        RainMeadow.Warn("Joining process it taking too long, timing it out!");
                        OnlineManager.lobby.joiningEvent.Abort(); // safely timeout
                    }
                    else
                    {
                        RainMeadow.Error($"No process running? What's happening? Timing it out! (enumChecked <{OnlineManager.lobby.enumsChecked}>, isRequesting <{OnlineManager.lobby.isRequesting}>, isAvailable <{OnlineManager.lobby.isAvailable}>)");
                        MatchmakingManager.currentInstance.JoinLobby(false, ERROR_Unexpected);
                    }
                }
            }
        }
    }

    public override void ShutDownProcess()
    {
        RainMeadow.DebugMe();
        if (androidLobbySong != null && manager.musicPlayer is Music.MusicPlayer player
            && (player.song == androidLobbySong || player.nextSong == androidLobbySong))
        {
            androidLobbySong.Loop = previousLobbySongLoop;
        }

        MatchmakingManager.OnLobbyListReceived -= OnLobbyListReceived;
        MatchmakingManager.OnLobbyJoined -= OnLobbyJoined;
#if MEADOW_STEAM_PROBE
        networkPanel.OnSteamSignedIn -= UseSteamSession;
        networkPanel.OnSteamSignedOut -= SignOutSteam;
#if !MEADOW_STEAM_GAMEPLAY
        networkPanel.OnSteamIdentityCheck -= CheckSteamIdentity;
#endif
        steamMenuLifetime.Cancel();
#if MEADOW_STEAM_GAMEPLAY
        if (!ManagedSteamGameplay.Owns(steamSession)) ManagedSteamGameplay.CloseSession(steamSession);
#else
        steamSession?.Dispose();
#endif
        steamSession = null;
        steamMenuLifetime.Dispose();
#endif
        base.ShutDownProcess();
    }
}
