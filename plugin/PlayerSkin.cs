using System.IO;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// The player's skin: amai.png (slim arms) when the assets tool found Amai's own skin on this PC, otherwise
    /// Minecraft's default Steve (wide arms). "/skin steve" and "/skin amai" switch between the two; the choice is
    /// kept in skin.txt next to the plugin.
    /// </summary>
    public static class PlayerSkin
    {
        private static bool? steve;
        private static string ChoiceFile => Path.Combine(McData.Root, "../skin.txt");
        private static bool HasAmai => File.Exists(Path.Combine(McData.Root, "amai.png"));

        public static bool UseSteve
        {
            get
            {
                if (steve == null)
                    try { steve = File.Exists(ChoiceFile) && File.ReadAllText(ChoiceFile).Trim() == "steve"; } catch { steve = false; }
                return steve.Value || !HasAmai;
            }
        }

        public static bool Slim => !UseSteve;

        public static Texture2D Texture => McTex.Get(UseSteve ? "../steve" : "../amai");

        /// <summary>Switches skin, saves the choice and rebuilds the hand and the body. False when Amai's skin is not installed.</summary>
        public static bool Set(bool toSteve)
        {
            if (!toSteve && !HasAmai) return false;
            steve = toSteve;
            try { File.WriteAllText(ChoiceFile, toSteve ? "steve" : "amai"); } catch { }
            View.I?.RefreshSkin();
            return true;
        }
    }
}
