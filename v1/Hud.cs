using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's in-game HUD drawn over Slime Rancher: hotbar, hearts (Beatrix's health), hunger shanks
    /// (her energy), the experience bar (how far the day has gone, with the Newbucks as the green level number),
    /// crosshair and the held item's name. Slime Rancher's own bars and vac slots are hidden.
    /// </summary>
    public static class Hud
    {
        private static float nameTimer;
        private static int lastHealth = -1, heartBlink;
        private static float blinkUntil;

        public static void ShowItemName() => nameTimer = 2f;

        private static readonly System.Collections.Generic.List<KeyValuePair> messages = new System.Collections.Generic.List<KeyValuePair>();
        private struct KeyValuePair { public string Text; public float Until; }

        /// <summary>A line in Minecraft's chat area (bottom left), fading out after a few seconds.</summary>
        public static void Message(string text)
        {
            messages.Add(new KeyValuePair { Text = text, Until = Time.unscaledTime + 8f });
            if (messages.Count > 6) messages.RemoveAt(0);
        }

        private static void DrawMessages(int h)
        {
            int y = h - 48;
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                float left = messages[i].Until - Time.unscaledTime;
                if (left <= 0) { messages.RemoveAt(i); continue; }
                float a = Mathf.Clamp01(left / 1f);
                float w = McFont.Width(messages[i].Text);
                McGui.Fill(2, y - 1, Mathf.Max(w + 4, 320 / 1.0f * 0 + w + 4), 9, new Color(0, 0, 0, 0.5f * a));
                McFont.Draw(messages[i].Text, 4, y, new Color(1, 1, 1, a));
                y -= 9;
            }
        }

        public static Color ItemColor(ItemStack s)
        {
            if (s == null || !s.IsActor) return Color.white;
            try { return SRSingleton<GameContext>.Instance.LookupDirector.GetColor(s.Actor); } catch { return Color.white; }
        }

        public static void Draw()
        {
            var icons = McAssets.Tex("gui/icons.png");
            var widgets = McAssets.Tex("gui/widgets.png");
            int w = McGui.ScaledWidth, h = McGui.ScaledHeight;
            var ps = SRSingleton<SceneContext>.Instance.PlayerState;

            DrawMessages(h);

            // crosshair (Minecraft inverts what is behind it; a soft white reads the same on Slime Rancher's colours)
            if (View.I == null || View.I.Perspective == 0)
            {
                var old = GUI.color;
                GUI.color = new Color(1, 1, 1, 0.85f);
                McGui.Blit(icons, (w - 15) / 2, (h - 15) / 2, 0, 0, 15, 15);
                GUI.color = old;
            }

            // hotbar
            int x0 = w / 2 - 91;
            McGui.Blit(widgets, x0, h - 22, 0, 0, 182, 22);
            McGui.Blit(widgets, x0 - 1 + Inventory.Selected * 20, h - 22 - 1, 0, 22, 24, 22);
            for (int i = 0; i < 9; i++) DrawItem(Inventory.Slots[i], x0 + i * 20 + 3, h - 19);

            // experience bar: the day's progress; level: Newbucks
            var td = SRSingleton<SceneContext>.Instance.TimeDirector;
            float day = td.CurrDayFraction();
            McGui.Blit(icons, x0, h - 29, 0, 64, 182, 5);
            McGui.Blit(icons, x0, h - 29, 0, 69, (int)(day * 183f), 5);
            var money = ps.GetCurrency().ToString();
            float mx = (w - McFont.Width(money)) / 2f, my = h - 31 - 4;
            var black = new Color(0, 0, 0, 1);
            McFont.Draw(money, mx + 1, my, black, false); McFont.Draw(money, mx - 1, my, black, false);
            McFont.Draw(money, mx, my + 1, black, false); McFont.Draw(money, mx, my - 1, black, false);
            McFont.Draw(money, mx, my, new Color(0.5f, 1f, 0.125f), false);

            // hearts: 100 SR health = 20 Minecraft health; upgrades add rows like Minecraft's health boost
            int hp = Mathf.CeilToInt(ps.GetCurrHealth() / 5f);
            int maxHp = Mathf.CeilToInt(ps.GetMaxHealth() / 5f);
            if (lastHealth >= 0 && hp < lastHealth) { blinkUntil = Time.time + 1f; }
            lastHealth = hp;
            bool blink = Time.time < blinkUntil && ((int)(Time.time * 6f)) % 2 == 0;
            int hearts = Mathf.CeilToInt(maxHp / 2f);
            int rows = Mathf.CeilToInt(hearts / 10f);
            int rowStep = Mathf.Max(10 - (rows - 2), 3);
            int baseY = h - 39;
            for (int i = hearts - 1; i >= 0; i--)
            {
                int row = i / 10;
                int hx = x0 + (i % 10) * 8;
                int hy = baseY - row * rowStep;
                if (hp <= 4) hy += Random.Range(0, 2);
                McGui.Blit(icons, hx, hy, 16 + (blink ? 9 : 0), 0, 9, 9);
                if (i * 2 + 1 < hp) McGui.Blit(icons, hx, hy, 52, 0, 9, 9);
                else if (i * 2 + 1 == hp) McGui.Blit(icons, hx, hy, 61, 0, 9, 9);
            }

            // hunger: energy scaled to 20 points
            int food = Mathf.CeilToInt(ps.GetCurrEnergy() / (float)Mathf.Max(1, ps.GetMaxEnergy()) * 20f);
            for (int i = 0; i < 10; i++)
            {
                int fx = w / 2 + 91 - i * 8 - 9;
                int fy = h - 39;
                if (food <= 0 && (int)(Time.time * 20f) % (food * 3 + 1) == 0) fy += Random.Range(-1, 2);
                McGui.Blit(icons, fx, fy, 16, 27, 9, 9);
                if (i * 2 + 1 < food) McGui.Blit(icons, fx, fy, 52, 27, 9, 9);
                else if (i * 2 + 1 == food) McGui.Blit(icons, fx, fy, 61, 27, 9, 9);
            }

            // held item name, fading out like Minecraft's
            if (nameTimer > 0)
            {
                nameTimer -= Time.unscaledDeltaTime;
                var held = Inventory.Held;
                if (held != null)
                {
                    float a = Mathf.Clamp01(nameTimer / 0.5f);
                    int ny = h - 59;
                    if (rows > 1) ny -= (rows - 1) * rowStep;
                    var name = held.Name;
                    McFont.DrawCentered(name, w / 2f, ny, new Color(1, 1, 1, a));
                }
            }
        }

        /// <summary>An item in a 16x16 slot at (x, y) in GUI pixels, with its stack count, as in any Minecraft slot.</summary>
        public static void DrawItem(ItemStack s, float x, float y)
        {
            if (s == null) return;
            if (s.IsBlock)
            {
                var icon = s.Block.Icon;
                if (icon != null) McGui.BlitSized(icon, x, y, 16, 16, 0, 0, icon.width, icon.height, icon.width, icon.height);
            }
            else
            {
                var sp = s.SrIcon;
                if (sp != null) McGui.Sprite(sp, x, y, 16, 16);
                else McGui.Fill(x + 2, y + 2, 12, 12, ItemColor(s));
            }
            if (s.Count > 1)
            {
                var c = s.Count.ToString();
                McFont.Draw(c, x + 19 - 2 - McFont.Width(c) + 1, y + 6 + 3, Color.white);
            }
        }
    }
}
