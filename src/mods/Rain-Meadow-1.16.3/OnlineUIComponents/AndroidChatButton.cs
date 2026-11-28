using UnityEngine;

namespace RainMeadow;

internal sealed class AndroidChatButton : MonoBehaviour
{
    private ChatHud chat = null!;
    private string label = "";
    private GUIStyle? style;
    private int? finger;

    internal void Initialize(ChatHud chatHud, string text)
    {
        chat = chatHud;
        label = text;
    }

    private static Rect Bounds
    {
        get
        {
            Rect area = Screen.safeArea;
            float height = area.height * 0.055f;
            float width = height * 2.6f;
            return new Rect(area.center.x - width / 2, Screen.height - area.yMin - height * 1.35f, width, height);
        }
    }

    private void OnGUI()
    {
        if (!chat.CanOpenAndroidChat || Event.current.type != EventType.Repaint) return;
        Rect bounds = Bounds;
        style ??= new GUIStyle(GUI.skin.button);
        style.fontSize = Mathf.Max(12, Mathf.RoundToInt(bounds.height * 0.38f));
        GUI.Label(bounds, label, style);
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused) finger = null;
    }

    private void Update()
    {
        if (!chat.CanOpenAndroidChat)
        {
            finger = null;
            return;
        }

        Rect bounds = Bounds;
        for (int i = 0; i < Input.touchCount; i++)
        {
            Touch touch = Input.GetTouch(i);
            bool overButton = bounds.Contains(new Vector2(touch.position.x, Screen.height - touch.position.y));
            if (finger == null && touch.phase == TouchPhase.Began && overButton)
                finger = touch.fingerId;
            else if (finger == touch.fingerId && touch.phase is TouchPhase.Ended or TouchPhase.Canceled)
            {
                finger = null;
                if (touch.phase == TouchPhase.Ended && overButton)
                    chat.OpenAndroidChat();
                break;
            }
        }
    }
}
