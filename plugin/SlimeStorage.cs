using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// The Slime Storage: 45 slots where slimes and largos wait, safe and warm, saved with the game. Opened from the
    /// slime button next to the survival and creative inventories; shift-click moves slimes in and out.
    /// </summary>
    public class SlimeStorageScreen : McScreen
    {
        private const int Rows = 5;

        public SlimeStorageScreen()
        {
            Title = McData.ModText("slimeStorage");
            Background = "gui/container/generic_54";
            H = 114 + Rows * 18;
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < 9; c++)
                {
                    int k = r * 9 + c;
                    Slots.Add(new Slot { X = 8 + c * 18, Y = 18 + r * 18, Get = () => Inventory.SlimeStorage[k], Set = v => Inventory.SlimeStorage[k] = v, CanPlace = IsSlime });
                }
            AddPlayerInventory(8, 18 + Rows * 18 + 13);
        }

        public static bool IsSlime(ItemStack v) => v != null && v.IsSr && Identifiable.IsSlime(v.Actor);

        protected override void DrawBackground(float mx, float my)
        {
            var tex = McTex.Get(Background);
            McGui.Blit(tex, Left, Top, 0, 0, W, Rows * 18 + 17);
            McGui.Blit(tex, Left, Top + Rows * 18 + 17, 0, 126, W, 96);
        }

        public override void OnClose() => Persist.Dirty = true;
    }

    /// <summary>The little slime button beside the player's inventory that opens the Slime Storage.</summary>
    public static class SlimeStorageButton
    {
        private static readonly ItemStack icon = new ItemStack(Identifiable.Id.PINK_SLIME, 1);

        private static Rect Box(int left, int top) => new Rect(left - 25, top + 4, 22, 22);

        public static void Draw(int left, int top, float mx, float my)
        {
            var r = Box(left, top);
            bool hover = r.Contains(new Vector2(mx, my));
            // a Minecraft button frame: light top-left edge, dark bottom-right edge, grey face
            McGui.Fill(r.x, r.y, r.width, r.height, new Color(0, 0, 0, 1));
            McGui.Fill(r.x + 1, r.y + 1, r.width - 2, r.height - 2, hover ? new Color(0.73f, 0.73f, 0.73f) : new Color(0.55f, 0.55f, 0.55f));
            McGui.Fill(r.x + 1, r.y + 1, r.width - 2, 1, new Color(1, 1, 1, 0.6f));
            McGui.Fill(r.x + 1, r.y + 1, 1, r.height - 2, new Color(1, 1, 1, 0.6f));
            Hud.DrawItem(icon, r.x + 3, r.y + 3);
            if (hover) Tooltip.DrawLines(new System.Collections.Generic.List<string> { McData.ModText("slimeStorage") }, mx, my);
        }

        public static bool Click(int left, int top, float mx, float my)
        {
            if (!Box(left, top).Contains(new Vector2(mx, my))) return false;
            Sounds.Play("ui.button.click", null, 0.25f);
            var cursor = Inventory.Cursor;
            Inventory.Cursor = null;
            Screens.Show(new SlimeStorageScreen());
            Inventory.Cursor = cursor;
            return true;
        }
    }
}
