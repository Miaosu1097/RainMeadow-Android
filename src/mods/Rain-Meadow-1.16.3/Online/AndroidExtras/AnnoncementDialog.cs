using System;
using System.Collections.Generic;
using Menu;
using UnityEngine;

namespace RainMeadow
{
    /// <summary>
    /// Displays player announcements.
    ///
    /// The layout supports the following features:
    ///   Adjustable title, subtitle, and version labels
    ///   A centered title sized to fit the available width
    ///   The announcement version and time below the title
    ///   The Meadow version in the lower-left corner
    ///   Text-sized custom buttons in the lower-right corner
    ///   Rich text with scrolling for longer announcements
    ///
    /// This Dialog subclass adds its controls to dialogPage.
    /// The Signal override dispatches button actions.
    ///
    /// Close the dialog with manager.StopSideProcess(this).
    /// When the main process is already MainMenu, requesting a switch to MainMenu
    /// leaves the dialog open.
    /// </summary>
    internal sealed class AnnoncementDialog : Dialog
    {
        // ---- Layout ----
        private const float PadX = 36f;
        private const float PadY = 24f;

        // Font sizes are scaled by 1.5 except for button text.
        private const float TitleMaxScale = 2.04f;   // 1.36 * 1.5
        private const float TitleMinScale = 0.93f;   // 0.62 * 1.5
        private const float TitleBoxH = 112f;
        private const float SubtitleScale = 1.08f;   // 0.72 * 1.5
        private const float BodyScale = 1.23f;       // 0.82 * 1.5
        private const float VersionScale = 0.93f;    // 0.62 * 1.5
        private const float LineGap = 30f;           // Minimum line height.

        // Button text size and height are fixed.
        private const float ButtonH = 36f;
        private const float ButtonPadX = 24f;
        private const float ButtonGap = 12f;
        private const float ButtonTextScale = 0.75f;

        private const string SignalPrefix = "RMExtra_BTN_";

        /// <summary>Sizes the dialog to fit the current screen.</summary>
        private static Vector2 CalcSize()
        {
            float sw = UnityEngine.Screen.width;
            float sh = UnityEngine.Screen.height;
            float w = Mathf.Min(900f, sw - 60f);
            float h = Mathf.Min(580f, sh - 60f);
            return new Vector2(w, h);
        }

        // ---- Content ----
        private readonly string title;
        private readonly string subtitle;
        private readonly string bodyRich;
        private readonly string versionLine;
        private readonly Action onUpdate;
        private readonly List<ButtonSpec> buttons;

        // Optional image frames played at the configured frame rate.
        private readonly List<Texture2D> frames;
        private readonly float fps;
        private FSprite imageSprite;
        private FAtlas[] frameAtlases;
        private float animTimer;
        private int animIndex;

        // ---- Body scrolling ----
        private sealed class Piece
        {
            internal MenuLabel label;
            internal float baseY;   // Vertical position at scrollY == 0.
            internal float halfH;
        }

        private readonly List<Piece> pieces = new List<Piece>();
        private MenuLabel measureLabel;

        private float bodyTop;
        private float bodyBottom;
        private float contentH;
        private float viewH;
        private float scrollY;
        private float maxScroll;

        private FSprite scrollTrack;
        private FSprite scrollThumb;

        internal AnnoncementDialog(ProcessManager manager,
                                   string title,
                                   string subtitle,
                                   string bodyRich,
                                   string versionLine,
                                   List<ButtonSpec> buttons,
                                   Action onUpdate,
                                   List<Texture2D> frames = null,
                                   float fps = 8f)
            : base("", CalcSize(), manager)
        {
            this.title = title ?? "";
            this.subtitle = subtitle ?? "";
            this.bodyRich = bodyRich ?? "";
            this.versionLine = versionLine ?? "";
            this.buttons = buttons ?? new List<ButtonSpec>();
            this.onUpdate = onUpdate;
            this.frames = frames ?? new List<Texture2D>();
            this.fps = fps <= 0f ? 8f : fps;
        }

        internal sealed class ButtonSpec
        {
            internal string text;
            internal string url;      // Opens the URL in a browser when set.
            internal bool isUpdate;   // Starts the update download when true.
        }

        // ============ Build ============

        /// <summary>
        /// Builds the dialog controls.
        /// Returns false if construction fails so the caller can leave
        /// the announcement unread.
        /// </summary>
        internal bool Build()
        {
            try
            {
                float left = pos.x + PadX;
                float top = pos.y + size.y - PadY;

                top = PlaceTitle(top);
                if (!string.IsNullOrEmpty(subtitle)) top = PlaceSubtitle(top - 6f);
                top -= 10f;
                top = PlaceDivider(top);

                if (frames.Count > 0) top = PlaceImage(top);

                bodyTop = top - 8f;
                bodyBottom = pos.y + PadY + ButtonH + 20f;
                viewH = Mathf.Max(1f, bodyTop - bodyBottom);

                measureLabel = NewLabel("", false);
                measureLabel.label.isVisible = false;
                measureLabel.pos = new Vector2(-10000f, -10000f);
                Add(measureLabel);

                BuildBody(left, bodyTop);

                if (!string.IsNullOrEmpty(versionLine)) PlaceVersion(left, pos.y + PadY);
                PlaceButtons();
                PlaceScrollbar();
                ApplyScroll(0f);

                UnityEngine.Debug.Log("[AndroidExtras] dialog built: pieces=" + pieces.Count
                                      + " contentH=" + contentH + " viewH=" + viewH
                                      + " maxScroll=" + maxScroll);
                return true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[AndroidExtras] dialog build error: " + e);
                return false;
            }
        }

        private float PlaceTitle(float top)
        {
            if (string.IsNullOrEmpty(title)) return top;

            float boxW = size.x - PadX * 2f;

            // Reduce the scale until the title fits within the available width.
            float scale = TitleMaxScale;
            var label = NewLabel(title, true);
            label.label.alignment = FLabelAlignment.Center;
            label.label.anchorY = 0.5f;

            for (int i = 0; i < 18; i++)
            {
                label.label.scale = scale;
                float w = label.label.textRect.width * scale;
                if (w <= boxW) break;
                scale -= 0.07f;
                if (scale < TitleMinScale) { scale = TitleMinScale; break; }
            }

            label.label.scale = scale;
            label.label.color = new Color(1f, 0.94f, 0.78f);

            float yCenter = top - TitleBoxH / 2f;
            label.pos = new Vector2(pos.x + size.x / 2f, yCenter);
            Add(label);

            return top - TitleBoxH - 2f;
        }

        private float PlaceSubtitle(float top)
        {
            var label = NewLabel(subtitle, false);
            label.label.alignment = FLabelAlignment.Center;
            label.label.anchorY = 0.5f;
            label.label.scale = SubtitleScale;
            label.label.color = new Color(0.72f, 0.72f, 0.78f);

            float h = label.label.textRect.height * SubtitleScale;
            label.pos = new Vector2(pos.x + size.x / 2f, top - h / 2f);
            Add(label);

            return top - h - 2f;
        }

        private float PlaceDivider(float top)
        {
            var sprite = new FSprite("pixel", true);
            sprite.color = new Color(0.32f, 0.32f, 0.38f);
            sprite.alpha = 0.85f;
            sprite.scaleX = size.x - PadX * 2f;
            sprite.scaleY = 1.2f;
            sprite.SetPosition(new Vector2(pos.x + size.x / 2f, top));
            (dialogPage as MenuObject).Container.AddChild(sprite);

            return top - 16f;
        }

        // ============ Body wrapping and scrolling ============

        private float Measure(string text, float scale)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            measureLabel.label.text = text;
            measureLabel.label.scale = scale;
            return measureLabel.label.textRect.width * scale;
        }

        /// <summary>Finds the longest text prefix that fits the available width.</summary>
        private int FitChars(string text, float scale, float avail)
        {
            if (avail <= 0f) return 0;
            int lo = 0;
            int hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Measure(text.Substring(0, mid), scale) <= avail) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        private void BuildBody(float left, float top)
        {
            if (string.IsNullOrEmpty(bodyRich)) { contentH = 0f; return; }

            var segments = RichText.Parse(bodyRich);
            float right = pos.x + size.x - PadX - 12f;

            float cursorX = left;
            float cursorY = top;
            float lineH = 0f;

            foreach (var seg in segments)
            {
                if (seg.isNewline)
                {
                    cursorY -= Mathf.Max(lineH, LineGap);
                    cursorX = left;
                    lineH = 0f;
                    continue;
                }
                if (string.IsNullOrEmpty(seg.text)) continue;

                float scale = BodyScale * seg.scale;
                string rest = seg.text;

                while (rest.Length > 0)
                {
                    float avail = right - cursorX;
                    float fullW = Measure(rest, scale);

                    if (fullW <= avail)
                    {
                        AddPiece(rest, cursorX, cursorY, scale, seg, ref lineH);
                        cursorX += fullW;
                        rest = "";
                        break;
                    }

                    // Split the run to fill the remaining space on this line.
                    if (cursorX > left + 0.5f)
                    {
                        int n = FitChars(rest, scale, avail);
                        if (n <= 0) n = 1;
                        string piece = rest.Substring(0, n);
                        rest = rest.Substring(n);

                        AddPiece(piece, cursorX, cursorY, scale, seg, ref lineH);
                        cursorX += Measure(piece, scale);
                        cursorY -= Mathf.Max(lineH, LineGap);
                        cursorX = left;
                        lineH = 0f;
                        continue;
                    }

                    // Split an oversized run at the start of a line.
                    int fit = FitChars(rest, scale, avail);
                    if (fit <= 0) fit = 1;
                    string head = rest.Substring(0, fit);
                    rest = rest.Substring(fit);

                    AddPiece(head, cursorX, cursorY, scale, seg, ref lineH);
                    cursorX += Measure(head, scale);
                    cursorY -= Mathf.Max(lineH, LineGap);
                    cursorX = left;
                    lineH = 0f;
                }
            }

            float contentBottom = cursorY - Mathf.Max(lineH, LineGap);
            contentH = Mathf.Max(0f, top - contentBottom);
        }

        private void AddPiece(string text, float x, float y, float scale,
                              RichText.Segment seg, ref float lineH)
        {
            var label = NewLabel(text, seg.bold);
            label.label.scale = scale;
            label.label.color = seg.color;

            float w = label.label.textRect.width * scale;
            float h = label.label.textRect.height * scale;

            label.pos = new Vector2(x + w / 2f, y - h / 2f);
            Add(label);

            pieces.Add(new Piece { label = label, baseY = y - h / 2f, halfH = h / 2f });

            lineH = Mathf.Max(lineH, h);
        }

        // ============ Scrolling ============

        public override void Update()
        {
            base.Update();

            try
            {
                TickAnimation();

                if (maxScroll <= 0.5f) return;

                float delta = 0f;

                // Touch dragging.
                if (Input.touchCount > 0)
                {
                    var t = Input.GetTouch(0);
                    if (t.phase == TouchPhase.Moved)
                        delta -= t.deltaPosition.y * 1.6f;
                }

                // Disable mouse wheel handling if the axis is unavailable.
                if (wheelAxisOk)
                {
                    try
                    {
                        float wheel = Input.GetAxis("Mouse ScrollWheel");
                        if (Mathf.Abs(wheel) > 0.001f) delta += wheel * 900f;
                    }
                    catch { wheelAxisOk = false; }
                }

                // Keyboard and controller input.
                if (Input.GetKey(KeyCode.UpArrow)) delta += 320f * Time.deltaTime;
                if (Input.GetKey(KeyCode.DownArrow)) delta -= 320f * Time.deltaTime;

                if (Mathf.Abs(delta) > 0.01f) ApplyScroll(scrollY + delta);
            }
            catch { }
        }

        private static bool wheelAxisOk = true;

        private void ApplyScroll(float value)
        {
            scrollY = Mathf.Clamp(value, 0f, maxScroll);

            for (int i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                if (p.label == null) continue;

                float y = p.baseY + scrollY;
                p.label.pos = new Vector2(p.label.pos.x, y);

                // Keep partially visible lines enabled.
                // Hide lines that have scrolled entirely out of view.
                p.label.label.isVisible =
                    (y + p.halfH) > bodyBottom - 60f && (y - p.halfH) < bodyTop + 60f;
            }

            UpdateThumb();
        }

        private void PlaceScrollbar()
        {
            float x = pos.x + size.x - 22f;
            float mid = (bodyTop + bodyBottom) / 2f;
            float trackH = bodyTop - bodyBottom;
            if (trackH <= 0f) return;

            scrollTrack = new FSprite("pixel", true);
            scrollTrack.color = new Color(0.30f, 0.30f, 0.36f);
            scrollTrack.alpha = 0.55f;
            scrollTrack.scaleX = 5f;
            scrollTrack.scaleY = trackH;
            scrollTrack.SetPosition(new Vector2(x, mid));
            (dialogPage as MenuObject).Container.AddChild(scrollTrack);

            var rect = (dialogPage as MenuObject).Container;
            scrollThumb = new FSprite("pixel", true);
            scrollThumb.color = new Color(0.86f, 0.86f, 0.94f);
            scrollThumb.alpha = 0.9f;
            scrollThumb.scaleX = 5f;
            scrollThumb.SetPosition(new Vector2(x, mid));
            rect.AddChild(scrollThumb);

            maxScroll = Mathf.Max(0f, contentH - viewH);
            bool need = maxScroll > 0.5f;
            scrollTrack.isVisible = need;
            scrollThumb.isVisible = need;
        }

        private void UpdateThumb()
        {
            if (scrollTrack == null || scrollThumb == null) return;

            bool need = maxScroll > 0.5f;
            scrollTrack.isVisible = need;
            scrollThumb.isVisible = need;
            if (!need) return;

            float trackH = bodyTop - bodyBottom;
            float thumbH = Mathf.Max(30f, trackH * (viewH / Mathf.Max(1f, contentH)));
            thumbH = Mathf.Min(thumbH, trackH);

            float t = scrollY / maxScroll;                     // Zero corresponds to the top of the scroll area.
            float topY = bodyTop - thumbH / 2f;
            float bottomY = bodyBottom + thumbH / 2f;
            float y = Mathf.Lerp(topY, bottomY, t);

            scrollThumb.scaleY = thumbH;
            scrollThumb.SetPosition(new Vector2(scrollThumb.x, y));
        }

        // ============ Images ============

        /// <summary>
        /// Scales the image to fit the available width while preserving its aspect ratio.
        /// Caps the image area at 34 percent of the dialog height.
        /// </summary>
        private float PlaceImage(float top)
        {
            try
            {
                float availW = size.x - PadX * 2f;
                float maxH = size.y * 0.34f;

                var tex = frames[0];
                if (tex == null) return top;

                float ratio = (float)tex.height / Mathf.Max(1, tex.width);
                float w = availW;
                float h = w * ratio;
                if (h > maxH)
                {
                    h = maxH;
                    w = h / Mathf.Max(0.0001f, ratio);
                }

                frameAtlases = new FAtlas[frames.Count];
                for (int i = 0; i < frames.Count; i++)
                {
                    frameAtlases[i] = Futile.atlasManager.LoadAtlasFromTexture(
                        "RMExtra_Frame_" + i, frames[i], false);
                }

                var at = frameAtlases[0];
                if (at == null || at.elements.Count == 0) return top;

                imageSprite = new FSprite(at.elements[0]);
                imageSprite.scaleX = w / Mathf.Max(1f, tex.width);
                imageSprite.scaleY = h / Mathf.Max(1f, tex.height);
                imageSprite.SetPosition(new Vector2(pos.x + size.x / 2f, top - h / 2f));

                (dialogPage as MenuObject).Container.AddChild(imageSprite);

                return top - h - 12f;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[AndroidExtras] place image failed: " + e);
                return top;
            }
        }

        private void TickAnimation()
        {
            if (imageSprite == null || frameAtlases == null || frameAtlases.Length <= 1) return;

            animTimer += Time.deltaTime;
            float interval = 1f / fps;
            if (animTimer < interval) return;

            animTimer -= interval;
            animIndex = (animIndex + 1) % frameAtlases.Length;

            var at = frameAtlases[animIndex];
            if (at != null && at.elements.Count > 0)
                imageSprite.element = at.elements[0];
        }

        // ============ Footer ============

        private void PlaceVersion(float left, float bottomY)
        {
            var label = NewLabel(versionLine, false);
            label.label.alignment = FLabelAlignment.Left;
            label.label.anchorY = 0.5f;
            label.label.scale = VersionScale;
            label.label.color = new Color(0.56f, 0.56f, 0.62f);

            float h = label.label.textRect.height * VersionScale;
            float w = label.label.textRect.width * VersionScale;
            label.pos = new Vector2(left + w / 2f, bottomY + h / 2f + 2f);
            Add(label);
        }

        private void PlaceButtons()
        {
            if (buttons.Count == 0)
                buttons.Add(new ButtonSpec { text = "关闭" });

            // Measure the buttons using the fixed text size.
            var widths = new float[buttons.Count];
            for (int i = 0; i < buttons.Count; i++)
            {
                var probe = NewLabel(buttons[i].text ?? "", false);
                probe.label.scale = ButtonTextScale;
                float tw = probe.label.textRect.width * probe.label.scale;
                RemoveNode(probe);
                widths[i] = Mathf.Max(104f, tw + ButtonPadX * 2f);
            }

            // Lay out the buttons from right to left.
            float x = pos.x + size.x - PadX - 16f;
            float y = pos.y + PadY + ButtonH / 2f;

            for (int i = buttons.Count - 1; i >= 0; i--)
            {
                float w = widths[i];
                x -= w;

                string signal = SignalPrefix + i;
                var btn = new SimpleButton(this, dialogPage, buttons[i].text ?? "", signal,
                                           new Vector2(x + w / 2f, y),
                                           new Vector2(w, ButtonH));

                dialogPage.subObjects.Add(btn);
                x -= ButtonGap;
            }
        }

        // ============ Button actions ============

        public override void Singal(MenuObject sender, string message)
        {
            if (message != null && message.StartsWith(SignalPrefix, StringComparison.Ordinal))
            {
                int idx;
                if (int.TryParse(message.Substring(SignalPrefix.Length), out idx)
                    && idx >= 0 && idx < buttons.Count)
                {
                    var spec = buttons[idx];

                    if (spec.isUpdate)
                    {
                        CloseSelf();
                        if (onUpdate != null) onUpdate();
                        return;
                    }

                    if (!string.IsNullOrEmpty(spec.url))
                    {
                        try { Application.OpenURL(spec.url); } catch { }
                        return;   // Keep the dialog open so the player can follow other links.
                    }

                    CloseSelf();
                    return;
                }
            }

            base.Singal(sender, message);
        }

        /// <summary>
        /// Closes this dialog.
        /// Stop the dialog as a side process attached to the current main loop.
        /// A switch to the current main process is ignored.
        /// </summary>
        private void CloseSelf()
        {
            try
            {
                manager.StopSideProcess(this);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[AndroidExtras] close failed: " + e);
            }
        }

        // ============ Utilities ============

        private MenuLabel NewLabel(string text, bool big)
        {
            return new MenuLabel(this, dialogPage, text ?? "", Vector2.zero,
                                 new Vector2(0f, 0f), big, null);
        }

        private void Add(MenuObject obj)
        {
            dialogPage.subObjects.Add(obj);
        }

        private void RemoveNode(MenuObject obj)
        {
            try
            {
                dialogPage.subObjects.Remove(obj);
                obj.RemoveSprites();
            }
            catch { }
        }
    }
}