using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace RainMeadow
{
    /// <summary>
    /// 极简富文本解析。
    ///
    /// 支持：
    ///   **文字**            粗体（改用 DisplayFont）
    ///   [color]文字[/color]  颜色，color 可为：
    ///                          red/green/blue/yellow/cyan/magenta/white/gray
    ///                          #RRGGBB
    ///   [big]文字[/big]      放大到 1.35 倍
    ///   \n                   换行
    ///
    /// 不支持斜体：Futile 的 FLabel 没有 skew，游戏字体也没有斜体变体。
    /// 遇到 *文字* 会原样输出（不解析）。
    /// </summary>
    internal static class RichText
    {
        internal sealed class Segment
        {
            internal string text;
            internal Color color = Color.white;
            internal bool bold;
            internal float scale = 1f;
            internal bool isNewline;
        }

        private static readonly Dictionary<string, Color> NamedColors =
            new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            { "red",     new Color(1f,    0.32f, 0.32f) },
            { "green",   new Color(0.42f, 0.92f, 0.44f) },
            { "blue",    new Color(0.42f, 0.64f, 1f)    },
            { "yellow",  new Color(1f,    0.86f, 0.30f) },
            { "cyan",    new Color(0.36f, 0.92f, 0.96f) },
            { "magenta", new Color(1f,    0.44f, 0.86f) },
            { "purple",  new Color(0.72f, 0.48f, 1f)    },
            { "orange",  new Color(1f,    0.60f, 0.26f) },
            { "pink",    new Color(1f,    0.60f, 0.75f) },
            { "white",   Color.white },
            { "gray",    new Color(0.66f, 0.66f, 0.70f) },
            { "grey",    new Color(0.66f, 0.66f, 0.70f) },
        };

        internal static List<Segment> Parse(string input)
        {
            var result = new List<Segment>();
            if (string.IsNullOrEmpty(input)) return result;

            var state = new State();
            var buffer = new StringBuilder();

            int i = 0;
            while (i < input.Length)
            {
                char c = input[i];

                // ---- 换行 ----
                if (c == '\n')
                {
                    Flush(result, buffer, state);
                    result.Add(new Segment { isNewline = true });
                    i++;
                    continue;
                }

                // ---- **粗体** ----
                if (c == '*' && i + 1 < input.Length && input[i + 1] == '*')
                {
                    Flush(result, buffer, state);
                    state.bold = !state.bold;
                    i += 2;
                    continue;
                }

                // ---- [tag] ----
                if (c == '[')
                {
                    int close = input.IndexOf(']', i + 1);
                    if (close > i)
                    {
                        string tag = input.Substring(i + 1, close - i - 1).Trim();

                        // 结束标签 [/xxx]
                        if (tag.StartsWith("/"))
                        {
                            string name = tag.Substring(1).Trim().ToLowerInvariant();
                            if (name == "big")
                            {
                                Flush(result, buffer, state);
                                state.scale = 1f;
                                i = close + 1;
                                continue;
                            }
                            if (NamedColors.ContainsKey(name) || name == "color")
                            {
                                Flush(result, buffer, state);
                                state.color = Color.white;
                                i = close + 1;
                                continue;
                            }
                        }
                        else
                        {
                            // [big]
                            if (tag.Equals("big", StringComparison.OrdinalIgnoreCase))
                            {
                                Flush(result, buffer, state);
                                state.scale = 1.35f;
                                i = close + 1;
                                continue;
                            }

                            // [color=red] 或 [red]
                            Color parsed;
                            string colorName = tag;
                            int eq = tag.IndexOf('=');
                            if (eq > 0) colorName = tag.Substring(eq + 1).Trim();

                            if (TryParseColor(colorName, out parsed))
                            {
                                Flush(result, buffer, state);
                                state.color = parsed;
                                i = close + 1;
                                continue;
                            }
                        }
                    }
                }

                buffer.Append(c);
                i++;
            }

            Flush(result, buffer, state);
            return result;
        }

        private static void Flush(List<Segment> result, StringBuilder buffer, State state)
        {
            if (buffer.Length == 0) return;

            result.Add(new Segment
            {
                text = buffer.ToString(),
                color = state.color,
                bold = state.bold,
                scale = state.scale,
            });
            buffer.Clear();
        }

        private static bool TryParseColor(string name, out Color color)
        {
            color = Color.white;

            if (string.IsNullOrEmpty(name)) return false;

            if (NamedColors.TryGetValue(name, out color)) return true;

            // #RRGGBB
            if (name.Length == 7 && name[0] == '#')
            {
                try
                {
                    int r = Convert.ToInt32(name.Substring(1, 2), 16);
                    int g = Convert.ToInt32(name.Substring(3, 2), 16);
                    int b = Convert.ToInt32(name.Substring(5, 2), 16);
                    color = new Color(r / 255f, g / 255f, b / 255f);
                    return true;
                }
                catch { }
            }

            return false;
        }

        private sealed class State
        {
            internal Color color = Color.white;
            internal bool bold;
            internal float scale = 1f;
        }

        /// <summary>去掉所有标记，只留纯文本（用于测量或降级显示）。</summary>
        internal static string StripMarkup(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            var segs = Parse(input);
            var sb = new StringBuilder();
            foreach (var s in segs)
            {
                if (s.isNewline) sb.Append('\n');
                else sb.Append(s.text);
            }
            return sb.ToString();
        }
    }
}