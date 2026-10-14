using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// What happens when you eat a slime like a Minecraft steak. Every slime fills a bit of hunger and gives an
    /// effect that fits it: a Boom slime blows up in your face, a Quantum slime teleports you, a Dervish slime
    /// lifts you like its tornado, a Gold slime is a golden apple. A largo gives the effects of both its slimes.
    /// </summary>
    public static class SlimeSnack
    {
        public static void Eaten(Identifiable.Id id)
        {
            bool largo = Identifiable.IsLargo(id);
            Hunger.Eat(largo ? 8 : 4, largo ? 0.6f : 0.3f);

            var name = id.ToString();
            name = name.EndsWith("_LARGO") ? name.Substring(0, name.Length - 6) : name.EndsWith("_SLIME") ? name.Substring(0, name.Length - 6) : name;
            foreach (var kind in name.Split('_')) Effect(kind);

            var label = SrItems.Name(id);
            Chat.ActionBar(McData.French ? "Miam, un " + label + " !" : "Yum, a " + label + "!");
        }

        private static void Effect(string kind)
        {
            var eye = SrWorld.Eye;
            switch (kind)
            {
                case "PINK": Hunger.Eat(2, 0.6f); break;                                    // just a good snack
                case "ROCK": Effects.Add("resistance", 600, 1); break;
                case "TABBY": Effects.Add("speed", 600, 1); break;
                case "PHOSPHOR": Effects.Add("night_vision", 1200, 0); break;
                case "RAD": Effects.Add("poison", 100, 0); break;
                case "BOOM": Game.Later(0.4f, () => Projectiles.Explode(SrWorld.Eye + Camera.main.transform.forward * 1.2f, 1.2f)); break;
                case "HONEY": Effects.Add("regeneration", 200, 1); break;
                case "HUNTER": Effects.Add("strength", 600, 0); Effects.Add("speed", 600, 0); break;
                case "CRYSTAL": Effects.Add("absorption", 2400, 1); break;
                case "QUANTUM": Teleport.Random(8f); break;
                case "DERVISH": Effects.Add("levitation", 60, 2); Effects.Add("slow_falling", 300, 0); break;
                case "MOSAIC": Effects.Add("haste", 600, 1); break;
                case "TANGLE": Effects.Add("jump_boost", 600, 2); break;
                case "FIRE": Effects.Add("fire_resistance", 1200, 0); break;
                case "SABER": Effects.Add("strength", 600, 1); break;
                case "PUDDLE": Effects.Add("water_breathing", 2400, 0); break;
                case "GOLD": Effects.Add("absorption", 2400, 0); Effects.Add("regeneration", 100, 1); break;
                case "LUCKY": XpOrb.Spawn(eye + Camera.main.transform.forward, 30); break;
                case "QUICKSILVER": Effects.Add("speed", 300, 2); Effects.Add("haste", 300, 1); break;
                case "TARR": Effects.Add("wither", 200, 0); Effects.Add("hunger", 600, 1); break;
                case "GLITCH": Teleport.Random(16f); Effects.Add("levitation", 30, 0); break;
            }
        }
    }
}
