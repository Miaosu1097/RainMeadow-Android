#if MEADOW_STEAM_PROBE
using System;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2.Authentication;
using UnityEngine;

namespace RainMeadow
{
    internal sealed class SteamCredentialInput : IAuthenticator
    {
        private readonly Action<string> update;
        private readonly CancellationToken token;
        private volatile TaskCompletionSource<string>? guardAnswer;
        private AndroidTextEditor? editor;
        private string password = "";

        internal string AccountName { get; private set; } = "";
        internal bool HasPassword => password.Length > 0;
        internal bool IsEditing => editor != null;
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

            editor = AndroidTextEditor.TryOpen(title, initialText, secure,
                TouchScreenKeyboardType.ASCIICapable, 0, text =>
            {
                editor = null;
                if (!token.IsCancellationRequested)
                    callback(text);
            });
            if (editor == null)
                update("Android keyboard unavailable. Enable Android Input Method and restart.");
        }

        internal void Update()
        {
            if (token.IsCancellationRequested)
            {
                Close();
                return;
            }
            editor?.Update();
        }

        internal void Close()
        {
            password = "";
            editor?.Dispose();
            editor = null;
        }
    }
}
#endif
