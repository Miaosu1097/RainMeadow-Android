#if MEADOW_STEAM_PROBE
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2.Authentication;
using UnityEngine;

namespace RainMeadow
{
    internal sealed class SteamCredentialInput : IAuthenticator
    {
        private delegate IDisposable? OpenAndroidEditor(string title, string initialText, bool secure, Action<string?> done);

        private static readonly OpenAndroidEditor? OpenWithAndroidIme =
            Type.GetType("RainWorldAndroidIme.TextInput, AndroidIME", throwOnError: false)
                ?.GetMethod("TryOpen", BindingFlags.Public | BindingFlags.Static)
                ?.CreateDelegate(typeof(OpenAndroidEditor)) as OpenAndroidEditor;

        private readonly Action<string> update;
        private readonly CancellationToken token;
        private volatile TaskCompletionSource<string>? guardAnswer;
        private TouchScreenKeyboard? keyboard;
        private IDisposable? androidEditor;
        private Action<string?>? completeEdit;
        private string password = "";

        internal string AccountName { get; private set; } = "";
        internal bool HasPassword => password.Length > 0;
        internal bool IsEditing => completeEdit != null;
        internal bool HasGuardRequest => guardAnswer is { Task.IsCompleted: false };

        internal SteamCredentialInput(Action<string> update, CancellationToken token)
        {
            this.update = update;
            this.token = token;
        }

        internal void EditAccount() => Open("Steam account name", AccountName, false, value =>
        {
            if (value != null)
                AccountName = value.Trim();
        });

        internal void EditPassword() => Open("Steam password", "", true, value =>
        {
            if (value != null)
                password = value;
        });

        internal AuthSessionDetails TakeCredentials()
        {
            var credentials = new AuthSessionDetails
            {
                Username = AccountName,
                Password = password,
                DeviceFriendlyName = "Rain Meadow Android",
                IsPersistentSession = true,
                Authenticator = this,
            };
            password = "";
            return credentials;
        }

        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
            => RequestGuardCode(previousCodeWasIncorrect
                ? "Code rejected. Get a fresh code from Steam Guard."
                : "Get the current code from Steam Guard, then tap Enter code.");

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
            => RequestGuardCode(previousCodeWasIncorrect
                ? "Code rejected. Check the Steam Guard email again."
                : "Check your email for the Steam Guard code.");

        public Task<bool> AcceptDeviceConfirmationAsync()
        {
            update("Approve the sign-in in Steam Mobile, then return here.");
            return Task.FromResult(true);
        }

        private async Task<string> RequestGuardCode(string prompt)
        {
            token.ThrowIfCancellationRequested();
            var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancel = token.Register(() => answer.TrySetCanceled());
            guardAnswer = answer;
            update(prompt);
            try
            {
                return await answer.Task.ConfigureAwait(false);
            }
            finally
            {
                guardAnswer = null;
            }
        }

        internal void EditGuardCode()
        {
            var answer = guardAnswer;
            if (answer == null || answer.Task.IsCompleted)
                return;

            Open("Steam Guard code", "", true, value =>
            {
                if (!string.IsNullOrWhiteSpace(value))
                    answer.TrySetResult(value!.Trim());
            });
        }

        private void Open(string title, string initialText, bool secure, Action<string?> callback)
        {
            if (token.IsCancellationRequested || IsEditing)
                return;

            completeEdit = callback;
            androidEditor = OpenWithAndroidIme?.Invoke(title, initialText, secure, CompleteInput);
            if (androidEditor != null)
                return;

            keyboard = TouchScreenKeyboard.Open(initialText, TouchScreenKeyboardType.ASCIICapable,
                autocorrection: false, multiline: false, secure: secure,
                alert: false, textPlaceholder: title, characterLimit: 0);
            if (keyboard == null)
            {
                completeEdit = null;
                update("Android keyboard unavailable. Enable Android Input Method and restart.");
            }
        }

        internal void Update()
        {
            if (token.IsCancellationRequested)
            {
                Close();
                return;
            }
            if (keyboard == null || keyboard.status == TouchScreenKeyboard.Status.Visible)
                return;

            CompleteInput(keyboard.status == TouchScreenKeyboard.Status.Done ? keyboard.text : null);
        }

        private void CompleteInput(string? text)
        {
            var callback = completeEdit;
            CloseEditor();
            if (!token.IsCancellationRequested)
                callback?.Invoke(text);
        }

        private void CloseEditor()
        {
            completeEdit = null;
            androidEditor?.Dispose();
            androidEditor = null;
            if (keyboard != null)
            {
                keyboard.text = "";
                keyboard.active = false;
                keyboard = null;
            }
        }

        internal void Close()
        {
            password = "";
            CloseEditor();
        }
    }
}
#endif
