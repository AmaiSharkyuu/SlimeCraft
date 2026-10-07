using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's in-game HUD (InGameHud) drawn over Subnautica with Minecraft's own GUI textures and font:
    /// hotbar and offhand, crosshair with the attack indicator, hearts (absorption, poison, wither, blinking on
    /// damage and regeneration wave), armour, hunger (the hunger effect turns it green), air bubbles (Subnautica's
    /// oxygen), a thirst row for Subnautica's water, experience bar and level, held item name, status effects,
    /// the action bar, titles and the F3 debug screen. Subnautica's own bars and quick slots are hidden.
    /// </summary>
    public static class Hud
    {
        private static float nameTimer;
        private static int lastHp = -1;
        private static float blinkUntil;
        private static string actionBar; private static float actionUntil;
        private static string title, subtitle; private static float titleUntil;
        private static int regenWave = -1;
        private static int ticks; private static float tickAcc;

        public static void ShowItemName() => nameTimer = 2f;
        public static void ActionBar(string s) { actionBar = s; actionUntil = Time.unscaledTime + 3f; }
        public static void Title(string t, string sub) { title = t; subtitle = sub; titleUntil = Time.unscaledTime + 5f; }

        private static Texture2D Icons => McTex.Get("gui/icons");
        private static Texture2D Widgets => McTex.Get("gui/widgets");

        public static void Draw()
        {
            if (View.I != null && View.I.HideGui) { Chat.DrawMessages(); return; }
            int w = McGui.ScaledWidth, h = McGui.ScaledHeight;
            tickAcc += Time.unscaledDeltaTime;
            while (tickAcc >= 0.05f) { tickAcc -= 0.05f; ticks++; }

            if (View.I != null && View.I.Perspective == 0 && !Screens.Open) Crosshair(w, h);
            if (!Motor.SuspendedByGame || true)
            {
                Hotbar(w, h);
                if (!GameMode.Creative) StatusBars(w, h);
                XpBar(w, h);
            }
            HeldName(w, h);
            EffectIcons(w);
            ActionAndTitle(w, h);
            Chat.DrawMessages();
            if (DebugScreen.Shown) DebugScreen.Draw(w, h);
        }

        private static void Crosshair(int w, int h)
        {
            var old = GUI.color;
            GUI.color = new Color(1, 1, 1, 0.9f);
            McGui.Blit(Icons, (w - 15) / 2, (h - 15) / 2, 0, 0, 15, 15);
            GUI.color = old;
            // the attack indicator under the crosshair while the cooldown recovers
            if (View.I != null)
            {
                float f = View.I.Cooldown(0f);
                bool full = f >= 1f && Interaction.Current.Mob != null || f >= 1f && Interaction.Current.Actor != null;
                int x = w / 2 - 8, y = h / 2 - 7 + 16;
                if (full) McGui.Blit(Icons, x, y, 68, 94, 16, 16);
                else if (f < 1f)
                {
                    int l = (int)(f * 17f);
                    McGui.Blit(Icons, x, y, 36, 94, 16, 4);
                    McGui.Blit(Icons, x, y, 52, 94, l, 4);
                }
            }
        }

        private static void Hotbar(int w, int h)
        {
            int x0 = w / 2 - 91;
            McGui.Blit(Widgets, x0, h - 22, 0, 0, 182, 22);
            McGui.Blit(Widgets, x0 - 1 + Inventory.Selected * 20, h - 22 - 1, 0, 22, 24, 22);
            if (Inventory.Offhand != null)
            {
                McGui.Blit(Widgets, x0 - 29, h - 23, 24, 22, 29, 24);
                DrawItem(Inventory.Offhand, x0 - 26, h - 19);
            }
            for (int i = 0; i < 9; i++) DrawItem(Inventory.Main[i], x0 + i * 20 + 3, h - 19);
        }

        private static void StatusBars(int w, int h)
        {
            var icons = Icons;
            int x0 = w / 2 - 91, x1 = w / 2 + 91;
            int hp = Mathf.CeilToInt(Health.Hp);
            int maxHp = Mathf.CeilToInt(Health.MaxHp);
            if (lastHp >= 0 && hp < lastHp) blinkUntil = Time.unscaledTime + 1f;
            else if (lastHp >= 0 && hp > lastHp) blinkUntil = Time.unscaledTime + 0.5f;
            lastHp = hp;
            bool blink = Time.unscaledTime < blinkUntil && (ticks / 3) % 2 == 1;
            int absorb = Mathf.CeilToInt(Health.Absorption);
            int hearts = Mathf.CeilToInt((maxHp + absorb) / 2f);
            int rows = Mathf.CeilToInt(hearts / 10f);
            int rowStep = Mathf.Max(10 - (rows - 2), 3);
            int baseY = h - 39;
            if (Effects.Has("regeneration")) regenWave = ticks % Mathf.CeilToInt(maxHp + 5f); else regenWave = -1;
            int u = 16;
            if (Effects.Has("poison")) u += 36; else if (Effects.Has("wither")) u += 72;
            bool hardcore = false;
            int v = hardcore ? 45 : 0;
            int maxHearts = Mathf.CeilToInt(maxHp / 2f);
            for (int i = hearts - 1; i >= 0; i--)
            {
                int row = i / 10;
                int hx = x0 + (i % 10) * 8;
                int hy = baseY - row * rowStep;
                if (hp + absorb <= 4) hy += Random.Range(0, 2);
                if (i == regenWave) hy -= 2;
                McGui.Blit(icons, hx, hy, 16 + (blink ? 9 : 0), v, 9, 9);
                if (i >= maxHearts)
                {
                    int ai = i * 2 - maxHearts * 2;
                    McGui.Blit(icons, hx, hy, 160 + (ai + 1 < absorb ? 0 : 9), v, 9, 9);
                    continue;
                }
                if (blink && i * 2 + 1 < lastHp) McGui.Blit(icons, hx, hy, u + 54, v, 9, 9);
                if (i * 2 + 1 < hp) McGui.Blit(icons, hx, hy, u + 36, v, 9, 9);
                else if (i * 2 + 1 == hp) McGui.Blit(icons, hx, hy, u + 45, v, 9, 9);
            }
            // armour
            int armor = Armor.Points;
            int armorY = baseY - (rows - 1) * rowStep - 10;
            if (armor > 0)
                for (int i = 0; i < 10; i++)
                {
                    int ax = x0 + i * 8;
                    if (i * 2 + 1 < armor) McGui.Blit(icons, ax, armorY, 34, 9, 9, 9);
                    else if (i * 2 + 1 == armor) McGui.Blit(icons, ax, armorY, 25, 9, 9, 9);
                    else McGui.Blit(icons, ax, armorY, 16, 9, 9, 9);
                }
            // hunger
            int food = Hunger.Food;
            bool hungerFx = Effects.Has("hunger");
            for (int i = 0; i < 10; i++)
            {
                int fx = x1 - i * 8 - 9, fy = h - 39;
                if (Hunger.Saturation <= 0f && ticks % (food * 3 + 1) == 0) fy += Random.Range(-1, 2);
                McGui.Blit(icons, fx, fy, 16 + (hungerFx ? 117 : 0), 27, 9, 9);
                int uu = hungerFx ? 88 : 52;
                if (i * 2 + 1 < food) McGui.Blit(icons, fx, fy, uu, 27, 9, 9);
                else if (i * 2 + 1 == food) McGui.Blit(icons, fx, fy, uu + 9, 27, 9, 9);
            }
            int rightY = h - 49;
            // air: Minecraft's bubbles, shown underwater or while refilling
            if (Motor.Submerged || Health.Air < 300)
            {
                int air = Mathf.Max(0, Health.Air);
                int full = Mathf.CeilToInt((air - 2) * 10f / 300f), partial = Mathf.CeilToInt(air * 10f / 300f) - full;
                for (int i = 0; i < full + partial; i++)
                    McGui.Blit(icons, x1 - i * 8 - 9, rightY, i < full ? 16 : 25, 18, 9, 9);
                rightY -= 10;
            }
        }

        private static void DrawTex(Texture t, Rect uv, float x, float y, float w, float h, float alpha = 1f)
        {
            if (t == null) return;
            var old = GUI.color;
            GUI.color = new Color(1, 1, 1, alpha);
            GUI.DrawTextureWithTexCoords(new Rect(x * McGui.Scale, y * McGui.Scale, w * McGui.Scale, h * McGui.Scale), t, uv, true);
            GUI.color = old;
        }

        private static void XpBar(int w, int h)
        {
            int x0 = w / 2 - 91;
            McGui.Blit(Icons, x0, h - 29, 0, 64, 182, 5);
            int filled = (int)(Xp.Progress * 183f);
            if (filled > 0) McGui.Blit(Icons, x0, h - 29, 0, 69, filled, 5);
            if (Xp.Level > 0)
            {
                var s = Xp.Level.ToString();
                float mx = (w - McFont.Width(s)) / 2f, my = h - 31 - 4;
                var black = Color.black;
                McFont.Draw(s, mx + 1, my, black, false); McFont.Draw(s, mx - 1, my, black, false);
                McFont.Draw(s, mx, my + 1, black, false); McFont.Draw(s, mx, my - 1, black, false);
                McFont.Draw(s, mx, my, new Color(0.5f, 1f, 0.125f), false);
            }
        }

        private static void HeldName(int w, int h)
        {
            if (nameTimer <= 0) return;
            nameTimer -= Time.unscaledDeltaTime;
            var held = Inventory.Held;
            if (held == null) return;
            float a = Mathf.Clamp01(nameTimer / 0.5f);
            int y = h - 59;
            if (GameMode.Creative) y += 14;
            var c = held.NameColor;
            McFont.DrawCentered(held.Name, w / 2f, y, new Color(c.r, c.g, c.b, a));
        }

        /// <summary>Status effect icons, top right (InGameHud.renderStatusEffectOverlay).</summary>
        private static void EffectIcons(int w)
        {
            int good = 0, bad = 0;
            var inv = McTex.Get("gui/container/inventory");
            foreach (var e in Effects.Active.Values)
            {
                if (!McData.Effects.TryGetValue(e.Id, out var def)) continue;
                bool beneficial = def.Category == "beneficial";
                int x = w - 25 * (beneficial ? ++good : ++bad);
                int y = beneficial ? 1 : 26;
                McGui.Blit(inv, x, y, e.Ambient ? 165 : 141, 166, 24, 24);
                float alpha = e.Duration > 200 ? 1f : 0.5f + Mathf.Sin(e.Duration * Mathf.PI / 5f) * 0.25f + 0.25f;
                DrawTex(McTex.Get("mob_effect/" + e.Id), new Rect(0, 0, 1, 1), x + 3, y + 3, 18, 18, alpha);
            }
        }

        private static void ActionAndTitle(int w, int h)
        {
            if (actionBar != null && Time.unscaledTime < actionUntil)
            {
                float a = Mathf.Clamp01((actionUntil - Time.unscaledTime) / 0.5f);
                McFont.DrawCentered(actionBar, w / 2f, h - 68, new Color(1, 1, 1, a));
            }
            if (title != null && Time.unscaledTime < titleUntil)
            {
                float a = Mathf.Clamp01((titleUntil - Time.unscaledTime) / 1f);
                // titles are drawn at 4x and subtitles at 2x the GUI scale
                var m = GUI.matrix;
                GUIUtility.ScaleAroundPivot(Vector2.one * 4f, new Vector2(Screen.width / 2f, Screen.height / 2f - 10 * McGui.Scale));
                McFont.DrawCentered(title, w / 2f, h / 2f - 10 - 4, new Color(1, 1, 1, a));
                GUI.matrix = m;
                if (subtitle != null)
                {
                    GUIUtility.ScaleAroundPivot(Vector2.one * 2f, new Vector2(Screen.width / 2f, Screen.height / 2f + 5 * McGui.Scale));
                    McFont.DrawCentered(subtitle, w / 2f, h / 2f + 5 - 4, new Color(1, 1, 1, a));
                    GUI.matrix = m;
                }
            }
        }

        /// <summary>A stack in a 16x16 slot at (x, y) in GUI pixels, with its count and durability bar.</summary>
        public static void DrawItem(ItemStack s, float x, float y)
        {
            if (s == null) return;
            var tex = ItemRender.Icon(s, out var uv);
            if (tex != null) GUI.DrawTextureWithTexCoords(new Rect(x * McGui.Scale, y * McGui.Scale, 16 * McGui.Scale, 16 * McGui.Scale), tex, uv, true);
            if (s.IsMc && s.Enchanted)
            {
                var old = GUI.color;
                GUI.color = new Color(0.6f, 0.3f, 1f, 0.35f + 0.15f * Mathf.Sin(Time.unscaledTime * 3f));
                GUI.DrawTextureWithTexCoords(new Rect(x * McGui.Scale, y * McGui.Scale, 16 * McGui.Scale, 16 * McGui.Scale), McTex.Get("misc/enchanted_glint_item"), new Rect(Time.unscaledTime * 0.1f % 1f, 0, 0.25f, 0.25f), true);
                GUI.color = old;
            }
            if (s.Damageable && s.Damage > 0)
            {
                // ItemStack.getItemBarStep / Color
                float f = Mathf.Max(0f, (s.Item.Durability - s.Damage) / (float)s.Item.Durability);
                int step = Mathf.RoundToInt(13f - s.Damage * 13f / s.Item.Durability);
                McGui.Fill(x + 2, y + 13, 13, 2, Color.black);
                McGui.Fill(x + 2, y + 13, step, 1, Color.HSVToRGB(f / 3f, 1f, 1f));
            }
            if (s.Count > 1)
            {
                var c = s.Count.ToString();
                McFont.Draw(c, x + 19 - 2 - McFont.Width(c), y + 6 + 3, Color.white);
            }
        }
    }

    /// <summary>The F3 debug screen, with the lines that make sense here.</summary>
    public static class DebugScreen
    {
        public static bool Shown;
        private static float fps, fpsAt;

        public static void Draw(int w, int h)
        {
            if (Time.unscaledTime > fpsAt) { fps = 1f / Mathf.Max(0.0001f, Time.unscaledDeltaTime); fpsAt = Time.unscaledTime + 0.5f; }
            if (SrWorld.Player == null) return;
            var feet = SrWorld.Feet;
            var cam = Camera.main.transform;
            float mcYaw = Mathf.Repeat(cam.eulerAngles.y - 180f + 180f, 360f) - 180f;
            var facing = BlockRules.HorizontalFacing(BlockRules.McLook(cam.forward));
            var lines = new List<string>
            {
                "Minecraft 1.20.1 (SlimeCraft/" + (GameMode.Creative ? "creative" : "survival") + ")",
                Mathf.RoundToInt(fps) + " fps",
                "",
                string.Format(System.Globalization.CultureInfo.InvariantCulture, "XYZ: {0:F3} / {1:F5} / {2:F3}", feet.x, feet.y, -feet.z),
                string.Format("Block: {0} {1} {2}", Mathf.FloorToInt(feet.x), Mathf.FloorToInt(feet.y), Mathf.FloorToInt(-feet.z)),
                string.Format(System.Globalization.CultureInfo.InvariantCulture, "Facing: {0} ({1:F1} / {2:F1})", BlockWorld.Name(facing), mcYaw, cam.eulerAngles.x > 180 ? cam.eulerAngles.x - 360 : cam.eulerAngles.x),
                "Region: " + SRSingleton<SceneContext>.Instance.RegionRegistry.GetCurrentRegionSetId(),
                "Blocks: " + BlockWorld.Blocks.Count + "  Mobs: " + Mobs.All.Count + "  Items: " + ItemEntity.All.Count,
            };
            var t = Interaction.Current;
            if (t.IsBlock) { var s = BlockWorld.Get(t.Cell); lines.Add(""); lines.Add("Targeted Block: " + t.Cell.x + ", " + t.Cell.y + ", " + (-t.Cell.z)); lines.Add("minecraft:" + s?.Block.Id); if (s != null) foreach (var kv in s.Props) lines.Add(kv.Key + ": " + kv.Value); }
            else if (t.Mob != null) { lines.Add(""); lines.Add("Targeted Entity: minecraft:" + t.Mob.Type); }
            else if (t.SrObject != null) { lines.Add(""); lines.Add("Slime Rancher: " + (t.Actor != null ? t.Actor.id.ToString() : t.SrObject.name) + " (" + SrWorld.SurfaceAt(t.Hit) + ")"); }
            int y = 2;
            foreach (var l in lines)
            {
                if (l.Length > 0) { McGui.Fill(1, y - 1, McFont.Width(l) + 2, 9, new Color(0.31f, 0.31f, 0.31f, 0.56f)); McFont.Draw(l, 2, y, new Color(0.88f, 0.88f, 0.88f), false); }
                y += 9;
            }
        }
    }
}
