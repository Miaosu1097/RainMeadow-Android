extern alias game;

using System;
using System.Reflection;
using BepInEx;
using OpTextBox = game::Menu.Remix.MixedUI.OpTextBox;
using UnityEngine;

namespace RainWorldAndroidIme
{
    [BepInPlugin("rwandroid.ime", "Android Input Method", "0.1.1")]
    public sealed class AndroidImePlugin : BaseUnityPlugin
    {
        private Func<OpTextBox, bool>? keyboardOn;
        private Action<OpTextBox, bool>? setKeyboardOn;
        private OpTextBox? activeBox;
        private IDisposable? editor;
        private bool initialized;

        private void OnEnable()
        {
            if (Application.platform != RuntimePlatform.Android)
                return;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var keyboardState = typeof(OpTextBox).GetProperty("_KeyboardOn", flags);
            keyboardOn = (Func<OpTextBox, bool>?)keyboardState?.GetGetMethod(true)?.CreateDelegate(typeof(Func<OpTextBox, bool>));
            setKeyboardOn = (Action<OpTextBox, bool>?)keyboardState?.GetSetMethod(true)?.CreateDelegate(typeof(Action<OpTextBox, bool>));
            if (keyboardOn == null || setKeyboardOn == null)
            {
                Logger.LogError("The game text field does not expose its keyboard state.");
                return;
            }

            TextInput.Start(message => Logger.LogWarning(message));
            On.Menu.Remix.MixedUI.OpTextBox.Update += UpdateTextBox;
            On.Menu.Remix.MixedUI.OpTextBox.Deactivate += DeactivateTextBox;
            On.Menu.Remix.MixedUI.OpTextBox.Unload += UnloadTextBox;
            initialized = true;
            Logger.LogInfo("Android input method enabled for Remix text fields.");
        }

        private void Update() => TextInput.Pump();

        private void UpdateTextBox(On.Menu.Remix.MixedUI.OpTextBox.orig_Update original, OpTextBox box)
        {
            bool wasEditing = keyboardOn!(box);
            original(box);
            if (wasEditing || !keyboardOn(box) || activeBox != null)
                return;

            int inputType = box.accept switch
            {
                OpTextBox.Accept.Int => 0x1002,
                OpTextBox.Accept.Float => 0x3002,
                _ => 1,
            };

            var session = TextInput.TryOpen("Input text", box.value, box.password, inputType, result =>
            {
                activeBox = null;
                editor = null;
                try
                {
                    if (result != null)
                        box.value = result;
                }
                finally
                {
                    setKeyboardOn!(box, false);
                }
            });
            if (session == null)
            {
                setKeyboardOn!(box, false);
                return;
            }

            activeBox = box;
            editor = session;
        }

        private void DeactivateTextBox(On.Menu.Remix.MixedUI.OpTextBox.orig_Deactivate original, OpTextBox box)
        {
            CloseEditor(box);
            original(box);
        }

        private void UnloadTextBox(On.Menu.Remix.MixedUI.OpTextBox.orig_Unload original, OpTextBox box)
        {
            CloseEditor(box);
            original(box);
        }

        private void CloseEditor(OpTextBox box)
        {
            if (activeBox != box)
                return;
            editor?.Dispose();
            editor = null;
            activeBox = null;
        }

        private void OnDisable()
        {
            if (!initialized)
                return;
            On.Menu.Remix.MixedUI.OpTextBox.Update -= UpdateTextBox;
            On.Menu.Remix.MixedUI.OpTextBox.Deactivate -= DeactivateTextBox;
            On.Menu.Remix.MixedUI.OpTextBox.Unload -= UnloadTextBox;
            TextInput.Stop();
            editor = null;
            activeBox = null;
            initialized = false;
        }
    }
}
