#if MEADOW_STEAM_PROBE
using System;
using System.IO;
using System.Linq;
using System.Threading;
using BepInEx.Logging;
using Menu.Remix.MixedUI;
using Menu;
using Newtonsoft.Json.Linq;
using QRCoder;
using SteamKit2.Authentication;
using SteamKit2.Discovery;
using UnityEngine;
using UnityEngine.Networking;

namespace RainMeadow
{
    internal sealed class SteamProbeDialog : Dialog
    {
        private const string DirectoryUrl =
            "https://api.steampowered.com/ISteamDirectory/GetCMListForConnect/v1/?cellid=0&cmtype=netfilter&maxcount=3";

        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Rain Meadow Steam");
        private readonly Action<SteamLobbySession> onSignedIn;
        private readonly SimpleButton? accountButton;
        private readonly SimpleButton? passwordButton;
        private readonly SimpleButton? signInButton;
        private readonly SimpleButton? guardButton;
        private readonly MenuLabel statusLabel;
        private readonly SteamCredentialInput? credentialInput;
        private readonly CancellationTokenSource lifetime = new();
        private AuthSessionDetails? pendingCredentials;
        private SteamLoginTicket? pendingSavedLogin;
        private bool busy;
        private readonly string atlasName = "RainMeadowSteamQR_" + Guid.NewGuid().ToString("N");
        private readonly Vector2 qrCenter;
        private UnityWebRequest? directory;
        private UnityWebRequestAsyncOperation? lookup;
        private volatile string status = "";
        private volatile string? challenge;
        private string? displayedChallenge;
        private Texture2D? texture;
        private FSprite? qrSprite;
        private bool atlasLoaded;

        internal SteamProbeDialog(ProcessManager manager, bool useCredentials,
            Action<SteamLobbySession> onSignedIn, SteamLoginTicket? savedLogin = null)
            : base("", new Vector2(600f, 440f), manager)
        {
            this.onSignedIn = onSignedIn;
            qrCenter = new Vector2(pos.x + size.x / 2f, pos.y + 198f);
            AddLabel(useCredentials ? "Steam sign-in" : "Steam QR sign-in", size.x / 2f, 395f, big: true);
            statusLabel = AddLabel("", size.x / 2f, useCredentials ? 135f : 355f);

            var close = AddButton("Close", "CLOSE", useCredentials ? 430f : 240f, 26f, 120f);
            selectedObject = close;

            if (useCredentials)
            {
                credentialInput = new SteamCredentialInput(SetStatus, lifetime.Token);
                AddLabel("Steam account name", 132f, 317f);
                accountButton = AddButton("Enter account name", "ACCOUNT", 235f, 300f, 310f);
                AddLabel("Password", 132f, 250f);
                passwordButton = AddButton("Enter password", "PASSWORD", 235f, 233f, 310f);
                guardButton = AddButton("Enter Steam Guard code", "GUARD", 190f, 75f, 220f);
                signInButton = AddButton("Sign in", "SIGN_IN", 50f, 26f, 120f);
                signInButton.buttonBehav.greyedOut = true;
                guardButton.buttonBehav.greyedOut = true;
                selectedObject = accountButton;
                status = "Enter your account name and password to sign in.";
            }
            if (!useCredentials || savedLogin != null)
            {
                pendingSavedLogin = savedLogin;
                StartLookup();
            }
            statusLabel.text = status;
        }

        private MenuLabel AddLabel(string text, float x, float y, bool big = false)
        {
            var label = new MenuLabel(this, dialogPage, Translate(text),
                pos + new Vector2(x, y), Vector2.zero, big);
            dialogPage.subObjects.Add(label);
            return label;
        }

        private SimpleButton AddButton(string text, string signal, float x, float y, float width)
        {
            var button = new SimpleButton(this, dialogPage, Translate(text), signal,
                pos + new Vector2(x, y), new Vector2(width, 34f));
            dialogPage.subObjects.Add(button);
            dialogPage.selectables.Add(button);
            return button;
        }

        private void SetStatus(string text) => status = text;

        private void SetFailure(string text)
        {
            SetStatus(text);
            Log.LogWarning(text);
        }

        private void StartLookup()
        {
            busy = true;
            lifetime.CancelAfter(TimeSpan.FromMinutes(5));
            SetStatus(pendingSavedLogin != null
                ? "Restoring saved Steam sign-in…"
                : "Looking up Steam connection servers…");
            directory = UnityWebRequest.Get(DirectoryUrl);
            directory.timeout = 12;
            directory.redirectLimit = 0;
            lookup = directory.SendWebRequest();
        }

        public override void Update()
        {
            base.Update();

            if (lookup?.isDone == true)
                FinishLookup();

            var pendingChallenge = challenge;
            if (displayedChallenge != pendingChallenge)
            {
                ClearQr();
                displayedChallenge = pendingChallenge;
                if (displayedChallenge != null)
                {
                    try
                    {
                        DrawQr(displayedChallenge);
                    }
                    catch (Exception error)
                    {
                        SetFailure("Steam QR display failed (" + error.GetType().Name + ").");
                        ClearQr();
                    }
                }
            }

            if (credentialInput is SteamCredentialInput input)
            {
                input.Update();
                bool canEdit = !busy && !input.IsEditing && !lifetime.IsCancellationRequested;
                accountButton!.buttonBehav.greyedOut = !canEdit;
                passwordButton!.buttonBehav.greyedOut = !canEdit;
                signInButton!.buttonBehav.greyedOut = !canEdit || input.AccountName.Length == 0 || !input.HasPassword;
                guardButton!.buttonBehav.greyedOut = !input.HasGuardRequest || input.IsEditing;
                var accountText = input.AccountName.Length == 0
                    ? Translate("Enter account name")
                    : LabelTest.TrimText(input.AccountName, 286f, false);
                var passwordText = input.HasPassword ? "********" : Translate("Enter password");
                if (accountButton.menuLabel.text != accountText)
                    accountButton.menuLabel.text = accountText;
                if (passwordButton.menuLabel.text != passwordText)
                    passwordButton.menuLabel.text = passwordText;
            }

            if (statusLabel.text != status)
                statusLabel.text = status;
        }

        private void FinishLookup()
        {
            lookup = null;
            using var request = directory!;
            directory = null;
            if (request.result != UnityWebRequest.Result.Success)
            {
                FinishAttempt("Steam server lookup failed (HTTP " + request.responseCode + ").");
                return;
            }

            try
            {
                var available = JObject.Parse(request.downloadHandler.text)["response"]?["serverlist"] as JArray;
                var servers = available?.Select(entry =>
                    (string?)entry["type"] == "netfilter"
                        && (string?)entry["endpoint"] is string endpoint
                        && ServerRecord.TryCreateSocketServer(endpoint, out var server) ? server : null)
                    .OfType<ServerRecord>().Take(3).ToArray();
                if (servers == null || servers.Length == 0)
                    throw new InvalidDataException("Steam returned no TCP servers.");

                RunSignIn(servers, lifetime.Token);
            }
            catch (Exception error)
            {
                FinishAttempt("Steam server lookup failed (" + error.GetType().Name + ").");
            }
        }

        private async void RunSignIn(ServerRecord[] servers, CancellationToken token)
        {
            var savedLogin = pendingSavedLogin;
            pendingSavedLogin = null;
            try
            {
                SteamLobbySession? session;
                if (savedLogin != null)
                {
                    session = await SteamLobbyProbe.RunWithSavedLoginAsync(SetStatus, savedLogin, token, servers);
                }
                else if (pendingCredentials is AuthSessionDetails credentials)
                {
                    pendingCredentials = null;
                    session = await SteamLobbyProbe.RunWithCredentialsAsync(SetStatus, credentials, token, servers);
                }
                else
                {
                    session = await SteamLobbyProbe.RunAsync(SetStatus,
                        next =>
                        {
                            challenge = next;
                            if (next != null)
                                SetStatus("Scan this code with Steam Guard on another device.");
                        },
                        false, token, servers);
                }

                if (session == null)
                    return;
                if (token.IsCancellationRequested)
                {
                    session.Dispose();
                    return;
                }

                var ticket = session.TakeLoginTicket();
                bool saved = savedLogin != null || ticket != null && SteamSavedLogin.Save(ticket);
                manager.StopSideProcess(this);
                onSignedIn(session);
                if (!saved)
                {
                    manager.ShowDialog(new DialogNotify(Translate(
                        "Signed in to Steam. Could not save this sign-in."), new Vector2(560f, 180f), manager, null));
                }
            }
            catch (OperationCanceledException)
            {
                SetStatus(!token.IsCancellationRequested
                    ? "Steam ended the active request. Re-enter your password to retry."
                    : "Steam sign-in cancelled or timed out. Reopen the dialog to retry.");
            }
            catch (AuthenticationException error)
            {
                SetFailure($"Steam authentication returned {error.Result}. Re-enter your password to retry.");
            }
            catch (SteamLobbyProbe.ReplyTimeoutException error)
            {
                SetFailure(error.Message);
            }
            catch (SteamLobbyProbe.SteamResultException error)
            {
                if (savedLogin != null && error.IsRejectedLogin && !token.IsCancellationRequested)
                {
                    SteamSavedLogin.Clear();
                    SetFailure("Steam rejected the saved sign-in. Sign in again to continue.");
                }
                else
                {
                    SetFailure(error.Message);
                }
            }
            catch (Exception error)
            {
                SetFailure($"Steam sign-in failed during this step ({error.GetType().Name}).\n{status}");
            }
            finally
            {
                FinishAttempt();
            }
        }

        private void FinishAttempt(string? message = null)
        {
            pendingSavedLogin = null;
            if (pendingCredentials != null)
            {
                pendingCredentials.Password = null;
                pendingCredentials.Username = null;
                pendingCredentials = null;
            }

            if (!lifetime.IsCancellationRequested)
                lifetime.CancelAfter(Timeout.Infinite);
            busy = false;
            if (message != null)
                SetFailure(message);
        }

        private void DrawQr(string url)
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            int width = data.ModuleMatrix.Count;
            var colors = new Color32[width * width];
            for (int y = 0; y < width; y++)
            for (int x = 0; x < width; x++)
                colors[(width - y - 1) * width + x] = data.ModuleMatrix[y][x]
                    ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255);

            texture = new Texture2D(width, width, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            texture.SetPixels32(colors);
            texture.Apply(false, true);
            Futile.atlasManager.LoadAtlasFromTexture(atlasName, texture, false);
            atlasLoaded = true;
            qrSprite = new FSprite(atlasName) { x = qrCenter.x, y = qrCenter.y, scale = 240f / width };
            container.AddChild(qrSprite);
        }

        private void ClearQr()
        {
            qrSprite?.RemoveFromContainer();
            qrSprite = null;
            if (atlasLoaded)
                Futile.atlasManager.UnloadAtlas(atlasName);
            atlasLoaded = false;
            if (texture != null)
                UnityEngine.Object.Destroy(texture);
            texture = null;
        }

        public override void Singal(MenuObject sender, string message)
        {
            switch (message)
            {
                case "ACCOUNT" when !busy:
                    credentialInput?.EditAccount();
                    break;
                case "PASSWORD" when !busy:
                    credentialInput?.EditPassword();
                    break;
                case "GUARD":
                    credentialInput?.EditGuardCode();
                    break;
                case "SIGN_IN" when !busy && credentialInput is { HasPassword: true } input
                    && !input.IsEditing && input.AccountName.Length > 0 && !lifetime.IsCancellationRequested:
                    pendingCredentials = input.TakeCredentials();
                    StartLookup();
                    break;
                case "CLOSE":
                    manager.StopSideProcess(this);
                    break;
            }
        }

        public override void ShutDownProcess()
        {
            lifetime.Cancel();
            credentialInput?.Close();
            FinishAttempt();
            lifetime.Dispose();
            directory?.Abort();
            directory?.Dispose();
            directory = null;
            lookup = null;
            ClearQr();
            base.ShutDownProcess();
        }
    }
}
#endif
