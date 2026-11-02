using Unity.SpaceGod.Core;
using UnityEngine;

namespace Unity.SpaceGod.UI
{
    /// <summary>
    /// The Help screen (U.SPC-T494, U.SPC-T497): the in-game manual, the whole screen. A contents list of the
    /// <see cref="GameManual"/> chapters down the left; the chapter picked on the right, its topics and then the
    /// keys for it from <see cref="ControlsHelp"/>, scrolling when it runs past the bottom (the last chapter is every
    /// key). Up / Down (or Previous / Next) step through the chapters. The main menu opens it from Help, the game
    /// from H / F1 and the Esc menu's Help.
    /// </summary>
    public sealed class HelpScreen
    {
        const float Margin = 40, Gutter = 28, KeyW = 150, LineGap = 3, TopicGap = 14, HeadingH = 24, ContentsRowH = 30;

        bool stylesReady;
        GUIStyle panel, title, chapterTitle, heading, keys, text, small, button, entry, entrySelected;
        Texture2D panelTexture, buttonTexture, buttonActiveTexture;
        Vector2 scroll, contentsScroll;
        int chapter;

        /// <summary>Draws the screen; true once the player has clicked Back (Esc and the keys that open it are the owner's to handle).</summary>
        public bool Draw(string closeHint)
        {
            EnsureStyles();
            var chapters = GameManual.Chapters;
            chapter = Mathf.Clamp(chapter, 0, chapters.Count - 1);
            HandleKeys(chapters.Count);

            var screen = new Rect(0, 0, Screen.width, Screen.height);
            GUI.Box(screen, GUIContent.none, panel);
            GUI.Label(new Rect(Margin, 22, screen.width - 2 * Margin, 30), "MANUAL", title);
            GUI.Label(new Rect(Margin, 54, screen.width - 2 * Margin, 18),
                "Everything in the game, by chapter  ·  Up / Down for the next chapter  ·  " + closeHint, small);

            float top = 86, bottom = screen.height - 64;
            float contentsW = Mathf.Clamp(screen.width * 0.24f, 180, 300);
            DrawContents(new Rect(Margin, top, contentsW, bottom - top), chapters);

            float x = Margin + contentsW + Gutter;
            var view = new Rect(x, top, screen.width - Margin - x, bottom - top);
            DrawChapter(chapters[chapter], view);

            if (chapter > 0 && GUI.Button(new Rect(x, screen.height - 50, 120, 32), "Previous", button)) Select(chapter - 1);
            if (chapter < chapters.Count - 1 && GUI.Button(new Rect(x + 132, screen.height - 50, 120, 32), "Next", button)) Select(chapter + 1);
            return GUI.Button(new Rect(screen.width - Margin - 120, screen.height - 50, 120, 32), "Back", button);
        }

        void HandleKeys(int count)
        {
            var e = Event.current;
            if (e == null || e.type != EventType.KeyDown) return;
            if (e.keyCode == KeyCode.DownArrow && chapter < count - 1) { Select(chapter + 1); e.Use(); }
            else if (e.keyCode == KeyCode.UpArrow && chapter > 0) { Select(chapter - 1); e.Use(); }
        }

        void Select(int index)
        {
            if (index == chapter) return;
            chapter = index;
            scroll = Vector2.zero;
        }

        void DrawContents(Rect area, System.Collections.Generic.IReadOnlyList<ManualChapter> chapters)
        {
            GUI.Label(new Rect(area.x, area.y, area.width, HeadingH - 4), "CONTENTS", heading);
            GUI.DrawTexture(new Rect(area.x, area.y + HeadingH - 5, area.width, 1), buttonActiveTexture);
            var list = new Rect(area.x, area.y + HeadingH, area.width, area.height - HeadingH);
            float contentH = chapters.Count * ContentsRowH;
            contentsScroll = GUI.BeginScrollView(list, contentsScroll, new Rect(0, 0, list.width - 16, contentH));
            for (int i = 0; i < chapters.Count; i++)
            {
                var row = new Rect(0, i * ContentsRowH, list.width - 16, ContentsRowH - 4);
                if (GUI.Button(row, (i + 1) + ".  " + chapters[i].Title, i == chapter ? entrySelected : entry)) Select(i);
            }
            GUI.EndScrollView();
        }

        void DrawChapter(ManualChapter c, Rect view)
        {
            float w = view.width - 16;
            float contentH = ChapterHeight(c, w);
            scroll = GUI.BeginScrollView(view, scroll, new Rect(0, 0, w, contentH));
            float y = 0;
            GUI.Label(new Rect(0, y, w, 28), c.Title.ToUpperInvariant(), chapterTitle);
            y += 28;
            float sh = small.CalcHeight(new GUIContent(c.Summary), w);
            GUI.Label(new Rect(0, y, w, sh), c.Summary, small);
            y += sh + TopicGap;

            foreach (var t in c.Topics)
            {
                GUI.Label(new Rect(0, y, w, HeadingH - 4), t.Heading, heading);
                y += HeadingH - 2;
                float th = text.CalcHeight(new GUIContent(t.Text), w);
                GUI.Label(new Rect(0, y, w, th), t.Text, text);
                y += th + TopicGap;
            }

            foreach (var section in GameManual.ControlsOf(c))
                y = DrawSection(section, y, w);
            GUI.EndScrollView();
        }

        float ChapterHeight(ManualChapter c, float w)
        {
            float h = 28 + small.CalcHeight(new GUIContent(c.Summary), w) + TopicGap;
            foreach (var t in c.Topics)
                h += HeadingH - 2 + text.CalcHeight(new GUIContent(t.Text), w) + TopicGap;
            foreach (var section in GameManual.ControlsOf(c))
            {
                h += HeadingH;
                foreach (var line in section.Lines) h += LineHeight(line, w) + LineGap;
                h += TopicGap;
            }
            return h;
        }

        float LineHeight(HelpLine line, float w) =>
            Mathf.Max(18, text.CalcHeight(new GUIContent(line.Text), w - KeyW));

        float DrawSection(HelpSection section, float y, float w)
        {
            GUI.Label(new Rect(0, y, w, HeadingH - 4), "KEYS: " + section.Title, heading);
            GUI.DrawTexture(new Rect(0, y + HeadingH - 5, w, 1), buttonActiveTexture);
            y += HeadingH;
            foreach (var line in section.Lines)
            {
                float h = LineHeight(line, w);
                if (line.Keys.Length > 0) GUI.Label(new Rect(0, y, KeyW - 8, h), line.Keys, keys);
                GUI.Label(new Rect(KeyW, y, w - KeyW, h), line.Text, line.Keys.Length > 0 ? text : small);
                y += h + LineGap;
            }
            return y + TopicGap;
        }

        void EnsureStyles()
        {
            if (stylesReady) return;
            stylesReady = true;
            // The HUD's look, near opaque: the screen is the whole view.
            panelTexture = Hud.Solid(new Color(0.02f, 0.035f, 0.06f, 0.96f), Color.clear);
            buttonTexture = Hud.Solid(new Color(0.12f, 0.18f, 0.24f, 0.95f), new Color(0.3f, 0.5f, 0.6f, 0.6f));
            buttonActiveTexture = Hud.Solid(new Color(0.1f, 0.3f, 0.42f, 0.95f), new Color(0.45f, 0.85f, 1f, 0.8f));
            panel = new GUIStyle { normal = { background = panelTexture } };
            title = new GUIStyle { fontSize = 24, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            chapterTitle = new GUIStyle { fontSize = 19, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            heading = new GUIStyle { fontSize = 14, fontStyle = FontStyle.Bold, normal = { textColor = Hud.Accent } };
            keys = new GUIStyle { fontSize = 13, fontStyle = FontStyle.Bold, normal = { textColor = Color.white }, wordWrap = true };
            text = new GUIStyle { fontSize = 13, normal = { textColor = new Color(0.86f, 0.9f, 0.94f) }, wordWrap = true };
            small = new GUIStyle(text) { normal = { textColor = Hud.Dim }, fontStyle = FontStyle.Italic };
            button = new GUIStyle
            {
                fontSize = 14, alignment = TextAnchor.MiddleCenter, border = new RectOffset(2, 2, 2, 2),
                normal = { background = buttonTexture, textColor = Color.white },
                hover = { background = buttonActiveTexture, textColor = Color.white },
                active = { background = buttonActiveTexture, textColor = Hud.Accent },
            };
            entry = new GUIStyle(button) { alignment = TextAnchor.MiddleLeft, fontSize = 13, padding = new RectOffset(10, 6, 0, 0) };
            entrySelected = new GUIStyle(entry)
            {
                normal = { background = buttonActiveTexture, textColor = Hud.Accent },
                hover = { background = buttonActiveTexture, textColor = Hud.Accent },
            };
        }

        /// <summary>The screen's own textures go with whatever owns it (U.SPC-T366): DontSave outlives the scene otherwise.</summary>
        public void DestroyOwnedTextures()
        {
            if (panelTexture != null) Object.Destroy(panelTexture);
            if (buttonTexture != null) Object.Destroy(buttonTexture);
            if (buttonActiveTexture != null) Object.Destroy(buttonActiveTexture);
            panelTexture = buttonTexture = buttonActiveTexture = null;
            stylesReady = false;
        }
    }
}
