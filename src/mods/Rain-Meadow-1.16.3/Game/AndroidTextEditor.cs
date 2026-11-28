using System;
using System.Reflection;
using UnityEngine;

namespace RainMeadow;

internal sealed class AndroidTextEditor : IDisposable
{
    private delegate IDisposable? OpenAndroidEditor(string title, string initialText, bool secure, Action<string?> done);

    private static readonly OpenAndroidEditor? OpenWithAndroidIme =
        Type.GetType("RainWorldAndroidIme.TextInput, AndroidIME", throwOnError: false)
            ?.GetMethod("TryOpen", BindingFlags.Public | BindingFlags.Static)
            ?.CreateDelegate(typeof(OpenAndroidEditor)) as OpenAndroidEditor;

    private Action<string?>? complete;
    private IDisposable? nativeEditor;
    private TouchScreenKeyboard? keyboard;

    internal static AndroidTextEditor? TryOpen(string title, string initialText, bool secure,
        TouchScreenKeyboardType type, int characterLimit, Action<string?> complete)
    {
        var editor = new AndroidTextEditor { complete = complete };
        editor.nativeEditor = OpenWithAndroidIme?.Invoke(title, initialText, secure, editor.Finish);
        if (editor.nativeEditor != null) return editor;

        editor.keyboard = TouchScreenKeyboard.Open(initialText, type,
            autocorrection: false, multiline: false, secure: secure,
            alert: false, textPlaceholder: title, characterLimit: characterLimit);
        return editor.keyboard == null ? null : editor;
    }

    internal void Update()
    {
        if (keyboard == null || keyboard.status == TouchScreenKeyboard.Status.Visible) return;
        Finish(keyboard.status == TouchScreenKeyboard.Status.Done ? keyboard.text : null);
    }

    private void Finish(string? text)
    {
        var callback = complete;
        Dispose();
        callback?.Invoke(text);
    }

    public void Dispose()
    {
        complete = null;
        nativeEditor?.Dispose();
        nativeEditor = null;
        if (keyboard == null) return;
        keyboard.text = "";
        keyboard.active = false;
        keyboard = null;
    }
}
