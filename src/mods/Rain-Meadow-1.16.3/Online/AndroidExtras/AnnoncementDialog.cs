using System;
using System.Collections.Generic;
using Menu;
using UnityEngine;

namespace RainMeadow
{
    /// <summary>
    /// 自定义公告弹窗。
    ///
    /// 为什么不用 DialogNotify：
    ///   DialogNotify 是单按钮 + 固定左对齐排版，无法满足：
    ///     · 大标题居中且字号自适应
    ///     · 副标题（公告版本 + 时间）紧跟标题下方
    ///     · 左下角显示雨甸版本
    ///     · 右下角多个自定义按钮，文字长度决定按钮宽度
    ///
    /// 做法：继承抽象类 Menu.Dialog（size/pos/dialogPage 都是 protected），
    /// 自己往 dialogPage 上挂 label 和按钮；按钮点击通过重写 Singal 分发。
    /// </summary>
    internal sealed class AnnoncementDialog : Dialog
    {
        // ---- 布局 ----
        private const float DialogW = 820f;
        private const float DialogH = 560f;
        private const float PadX = 36f;
        private const float PadY = 26f;

        private const float TitleMaxScale = 1.36f;   // 少字时放大
        private const float TitleMinScale = 0.62f;   // 多字时缩小
        private const float TitleBoxH = 82f;
        private const float TitleBoxW = DialogW - PadX * 2;

        private const float SubtitleScale = 0.72f;
        private const float BodyScale = 0.82f;
        private const float VersionScale = 0.62f;

        private const float ButtonH = 34f;
        private const float ButtonPadX = 24f;
        private const float ButtonGap = 12f;

        private const string SignalPrefix = "RMExtra_BTN_";

        // ---- 数据 ----
        private readonly string title;
        private readonly string subtitle;
        private readonly string bodyRich;
        private readonly string versionLine;
        private readonly Action onUpdate;
        private readonly List<ButtonSpec> buttons;

        // 图片（可为空）。frames 多于 1 帧时按 fps 轮播，实现类 GIF 效果。
        private readonly List<Texture2D> frames;
        private readonly float fps;
        private FSprite imageSprite;
        private FAtlas[] frameAtlases;
        private float animTimer;
        private int animIndex;

        internal AnnoncementDialog(ProcessManager manager,
                                   string title,
                                   string subtitle,
                                   string bodyRich,
                                   string versionLine,
                                   List<ButtonSpec> buttons,
                                   Action onUpdate,
                                   List<Texture2D> frames = null,
                                   float fps = 8f)
            : base("", new Vector2(DialogW, DialogH), manager)
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
            internal string url;      // 非空 → 打开浏览器
            internal bool isUpdate;   // true → 触发下载流程
        }

        // ============ 构建 ============

        internal void Build()
        {
            try
            {
                float left = pos.x + PadX;
                float top = pos.y + size.y - PadY;

                top = PlaceTitle(top);
                if (!string.IsNullOrEmpty(subtitle)) top = PlaceSubtitle(top - 4f);
                top -= 8f;
                top = PlaceDivider(top);

                // 有图片时优先占位
                if (frames.Count > 0) top = PlaceImage(top);

                float bodyBottom = pos.y + PadY + ButtonH + 18f;
                PlaceRichBody(left, top - 6f, bodyBottom);

                if (!string.IsNullOrEmpty(versionLine)) PlaceVersion(left, pos.y + PadY);

                PlaceButtons();
            }
            catch (Exception e)
            {
                Debug.LogError("[AndroidExtras] dialog build error: " + e.Message);
            }
        }

        private float PlaceTitle(float top)
        {
            if (string.IsNullOrEmpty(title)) return top;

            // scale 从大往小试，直到宽度不超出
            float scale = TitleMaxScale;
            float w = 0f;

            var label = NewLabel(title, true);
            label.label.alignment = FLabelAlignment.Center;
            label.label.anchorY = 0.5f;

            for (int i = 0; i < 14; i++)
            {
                label.label.scale = scale;
                w = label.label.textRect.width * scale;
                if (w <= TitleBoxW) break;
                scale -= 0.06f;
                if (scale < TitleMinScale) { scale = TitleMinScale; break; }
            }

            label.label.scale = scale;
            label.label.color = new Color(1f, 0.94f, 0.78f);

            float h = label.label.textRect.height * scale;
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
            label.label.color = new Color(0.70f, 0.70f, 0.75f);

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

            return top - 14f;
        }

        private void PlaceRichBody(float left, float top, float bottomLimit)
        {
            if (string.IsNullOrEmpty(bodyRich)) return;

            var segments = RichText.Parse(bodyRich);

            float cursorX = left;
            float cursorY = top;
            float lineH = 0f;

            foreach (var seg in segments)
            {
                if (seg.isNewline)
                {
                    cursorY -= Mathf.Max(lineH, 20f);
                    cursorX = left;
                    lineH = 0f;
                    continue;
                }
                if (string.IsNullOrEmpty(seg.text)) continue;

                var label = NewLabel(seg.text, seg.bold);
                label.label.scale = BodyScale * seg.scale;
                label.label.color = seg.color;

                float w = label.label.textRect.width * label.label.scale;
                float h = label.label.textRect.height * label.label.scale;

                // 超宽换行
                if (cursorX > left + 0.5f && cursorX + w > pos.x + size.x - PadX)
                {
                    cursorY -= Mathf.Max(lineH, 20f);
                    cursorX = left;
                    lineH = 0f;
                }

                // 超高度：截断
                if (cursorY - h < bottomLimit)
                {
                    var dots = NewLabel("…", false);
                    dots.label.scale = BodyScale;
                    dots.label.color = new Color(0.6f, 0.6f, 0.6f);
                    dots.pos = new Vector2(cursorX + 6f, cursorY - 8f);
                    Add(dots);
                    return;
                }

                label.pos = new Vector2(cursorX + w / 2f, cursorY - h / 2f);
                Add(label);

                cursorX += w;
                lineH = Mathf.Max(lineH, h);
            }
        }

        /// <summary>
        /// 放置图片。宽度撑满可用宽度，高度按原始宽高比缩放，
        /// 但最多占弹窗高度的 38%，避免挤掉正文。
        /// </summary>
        private float PlaceImage(float top)
        {
            try
            {
                float availW = size.x - PadX * 2f;
                float maxH = DialogH * 0.38f;

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

                // 每帧建一个 FAtlas，切换用 element
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

                return top - h - 10f;
            }
            catch (Exception e)
            {
                Debug.LogError("[AndroidExtras] place image failed: " + e.Message);
                return top;
            }
        }

        /// <summary>多帧轮播。由 Update 每帧推进。</summary>
        public override void Update()
        {
            base.Update();

            if (imageSprite == null || frameAtlases == null || frameAtlases.Length <= 1) return;

            try
            {
                animTimer += Time.deltaTime;
                float interval = 1f / fps;
                if (animTimer < interval) return;

                animTimer -= interval;
                animIndex = (animIndex + 1) % frameAtlases.Length;

                var at = frameAtlases[animIndex];
                if (at != null && at.elements.Count > 0)
                    imageSprite.element = at.elements[0];
            }
            catch { }
        }

        private void PlaceVersion(float left, float bottomY)
        {
            var label = NewLabel(versionLine, false);
            label.label.alignment = FLabelAlignment.Left;
            label.label.anchorY = 0.5f;
            label.label.scale = VersionScale;
            label.label.color = new Color(0.52f, 0.52f, 0.58f);

            float h = label.label.textRect.height * VersionScale;
            float w = label.label.textRect.width * VersionScale;
            label.pos = new Vector2(left + w / 2f, bottomY + h / 2f);
            Add(label);
        }

        private void PlaceButtons()
        {
            if (buttons.Count == 0)
                buttons.Add(new ButtonSpec { text = "关闭" });

            // 量宽
            var widths = new float[buttons.Count];
            for (int i = 0; i < buttons.Count; i++)
            {
                var probe = NewLabel(buttons[i].text ?? "", false);
                probe.label.scale = 0.75f;
                float tw = probe.label.textRect.width * probe.label.scale;
                RemoveNode(probe);
                widths[i] = Mathf.Max(104f, tw + ButtonPadX * 2f);
            }

            // 从右往左排
            float x = pos.x + size.x - PadX;
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

        // ============ 点击分发 ============

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
                        return;   // 打开链接不关闭弹窗，方便点多个
                    }

                    CloseSelf();
                    return;
                }
            }

            base.Singal(sender, message);
        }

        private void CloseSelf()
        {
            try
            {
                // 关闭 dialog 进程，回到主菜单
                manager.RequestMainProcessSwitch(ProcessManager.ProcessID.MainMenu, 0.01f);
            }
            catch (Exception e)
            {
                Debug.LogError("[AndroidExtras] close failed: " + e.Message);
            }
        }

        // ============ 工具 ============

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
            try { obj.RemoveSprites(); } catch { }
        }
    }
}