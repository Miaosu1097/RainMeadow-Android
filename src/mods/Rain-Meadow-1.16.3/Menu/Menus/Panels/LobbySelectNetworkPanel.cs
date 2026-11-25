using System;
using System.Linq;
using Menu;
using Menu.Remix;
using Menu.Remix.MixedUI;
using RainMeadow.UI.Components.Patched;
using RainMeadow.UI.Dialogs;
using UnityEngine;

namespace RainMeadow.UI.Menus.Panels;

class LobbySelectNetworkPanel : PositionedMenuObject
{
    public event Action? OnDirectConnectButtonClick;
    public event Action? OnDomainChanged;

    public SimplerButton directConnectButton;
    private readonly OpComboBox2 domainComboBox;
#if MEADOW_STEAM_PROBE
    internal event Action<SteamLobbySession>? OnSteamSignedIn;
    internal event Action? OnSteamSignedOut;
#if !MEADOW_STEAM_GAMEPLAY
    internal event Action? OnSteamIdentityCheck;
    internal bool SteamIdentityCheckPending { get; set; }
    private SimplerButton? steamIdentityButton;
#endif
    private SimplerButton? steamSignInButton;
    private bool managedSteamAvailable;
    internal bool IsManagedSteamSelected => managedSteamAvailable
        && domainComboBox.value == MatchmakingManager.MatchMakingDomain.Steam.value;
#endif

    public LobbySelectNetworkPanel(Menu.Menu menu, MenuObject owner, Vector2 pos)
        : base(menu, owner, pos)
    {
        MenuTabWrapper tabWrapper = new(menu, this);

        directConnectButton = new SimplerButton(
            menu,
            this,
            menu.Translate("Direct Connect"),
            new Vector2(20, 445),
            new Vector2(160f, 30f),
            menu.Translate("Directly connect to a local IP")
        );
        directConnectButton.buttonBehav.greyedOut =
            MatchmakingManager.currentDomain == MatchmakingManager.MatchMakingDomain.LAN;
        directConnectButton.OnClick += (btn) => OnDirectConnectButtonClick?.Invoke();

        ProperlyAlignedMenuLabel domainLabel = new(
            menu,
            this,
            menu.Translate("Lobby Domain"),
            new Vector2(20, 415),
            new Vector2(200f, 20f),
            false
        );

        domainComboBox = new(
            new Configurable<MatchmakingManager.MatchMakingDomain>(
                MatchmakingManager.supported_matchmakers.Contains(MatchmakingManager.currentDomain)
                    ? MatchmakingManager.currentDomain : MatchmakingManager.MatchMakingDomain.LAN
            ),
            new Vector2(20, 385),
            125f,
            [
                .. MatchmakingManager.supported_matchmakers.Select(x => new ListItem(
                    x.value,
                    menu.Translate(x.value)
                )),
            ]
        );
        domainComboBox.OnChange += () =>
        {
#if MEADOW_STEAM_PROBE
            if (!IsManagedSteamSelected)
#endif
            {
                var domain = new MatchmakingManager.MatchMakingDomain(domainComboBox.value, false);
                if (MatchmakingManager.currentDomain != domain)
                    MatchmakingManager.currentDomain = domain;
            }
            OnDomainChanged?.Invoke();
        };

        new PatchedUIelementWrapper(tabWrapper, domainComboBox);
        subObjects.AddRange([tabWrapper, directConnectButton, domainLabel]);

#if MEADOW_STEAM_PROBE
        if (Application.platform == RuntimePlatform.Android &&
            !MatchmakingManager.supported_matchmakers.Contains(MatchmakingManager.MatchMakingDomain.Steam))
        {
            AddSteamButtons(menu);
        }
#endif
    }

#if MEADOW_STEAM_PROBE
    private void AddSteamButtons(Menu.Menu menu)
    {
        steamSignInButton = new SimplerButton(menu, this, menu.Translate("Sign in to Steam"), new Vector2(20, 335),
            new Vector2(220f, 30f), "Sign in and confirm with Steam Guard on this phone");
        steamSignInButton.OnClick += _ => OpenSteamSignIn(SteamSavedLogin.Load());

        var qr = new SimplerButton(menu, this, menu.Translate("Sign in with Steam QR code"), new Vector2(20, 295),
            new Vector2(220f, 30f), "Sign in with Steam Guard on another device");
        qr.OnClick += _ => menu.manager.ShowDialog(new SteamProbeDialog(menu.manager, useCredentials: false, SignedIn));

        var signOut = new SimplerButton(menu, this, menu.Translate("Sign out of Steam"), new Vector2(20, 255),
            new Vector2(220f, 30f), "Disconnect from Steam and remove the saved sign-in from this device");
        signOut.OnClick += _ =>
        {
            bool cleared = SteamSavedLogin.Clear();
            OnSteamSignedOut?.Invoke();
            if (!cleared)
            {
                menu.manager.ShowDialog(new NotifyDialog(menu.manager,
                    "Could not remove the saved Steam sign-in.", UIUtils.DIALOG_SIZE));
            }
        };
        subObjects.AddRange([steamSignInButton, qr, signOut]);
#if !MEADOW_STEAM_GAMEPLAY
        steamIdentityButton = new SimplerButton(menu, this, "Check Steam Network ID", new Vector2(20, 215),
            new Vector2(220f, 30f), "Request a Rain World networking certificate for this Steam account");
        steamIdentityButton.OnClick += _ => OnSteamIdentityCheck?.Invoke();
        steamIdentityButton.buttonBehav.greyedOut = true;
        subObjects.Add(steamIdentityButton);
#endif
    }

    internal void RestoreSteamSignIn()
    {
        if (steamSignInButton != null && !managedSteamAvailable && SteamSavedLogin.Load() is SteamLoginTicket saved)
            OpenSteamSignIn(saved);
    }

    private void OpenSteamSignIn(SteamLoginTicket? saved)
    {
        menu.manager.ShowDialog(new SteamProbeDialog(menu.manager, useCredentials: true, SignedIn, saved));
    }

    private void SignedIn(SteamLobbySession session)
    {
        OnSteamSignedIn?.Invoke(session);
    }

    internal void EnableManagedSteam()
    {
        string steam = MatchmakingManager.MatchMakingDomain.Steam.value;
        if (!managedSteamAvailable)
        {
            managedSteamAvailable = true;
            domainComboBox.AddItems(false, new ListItem(steam, menu.Translate(steam)));
        }

        domainComboBox.value = steam;
    }

    internal void DisableManagedSteam()
    {
        if (!managedSteamAvailable)
            return;

        if (IsManagedSteamSelected)
            domainComboBox.value = MatchmakingManager.MatchMakingDomain.LAN.value;
        managedSteamAvailable = false;
        domainComboBox.RemoveItems(false, MatchmakingManager.MatchMakingDomain.Steam.value);
    }
#endif

    public override void Update()
    {
        base.Update();
#if MEADOW_STEAM_PROBE && !MEADOW_STEAM_GAMEPLAY
        if (steamIdentityButton != null)
            steamIdentityButton.buttonBehav.greyedOut = !IsManagedSteamSelected || SteamIdentityCheckPending;
#endif
        directConnectButton.buttonBehav.greyedOut =
#if MEADOW_STEAM_PROBE
            IsManagedSteamSelected ||
#endif
            MatchmakingManager.currentDomain != MatchmakingManager.MatchMakingDomain.LAN;
    }
}
