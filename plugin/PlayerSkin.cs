using System.IO;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// The player's skin: amai.png (slim arms) when the assets tool found Amai's own skin on this PC, otherwise
    /// Minecraft's default Steve (wide arms).
    /// </summary>
    public static class PlayerSkin
    {
        public static bool Slim => File.Exists(Path.Combine(McData.Root, "amai.png"));

        public static Texture2D Texture => McTex.Get(Slim ? "../amai" : "../steve");
    }
}
