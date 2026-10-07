"""
Pulls the Minecraft 1.20.1 files SlimeCraft needs out of the player's own copy (the official launcher's files,
or a Fabric Loom cache), so the mod never ships Mojang's assets itself: every texture, sound and translation is
read from their install and written next to the plugin. Built into SlimeCraft-Assets.exe for players.

Output (plugin/assets):
  gui/*.png, font/*.png, steve.png        straight copies
  font.txt                                 bitmap font providers, flattened from font/include/default.json
  blocks/<id>_<face>.png                   one 16x16 texture per block face, already tinted/composited
  blocks.txt                               the block table the plugin builds its palette from
  sounds/<path>.ogg                        the handful of sounds we play
"""
import io, json, os, shutil, sys, zipfile
from PIL import Image

HOME = os.path.expanduser("~")
FROZEN = getattr(sys, "frozen", False)  # running as SlimeCraft-Assets.exe


def find_minecraft():
    """The player's own Minecraft 1.20.1: the official launcher's files, or a Fabric Loom cache for modders."""
    candidates = []
    mc = os.path.join(os.environ.get("APPDATA", HOME), ".minecraft")
    candidates.append((os.path.join(mc, "versions", "1.20.1", "1.20.1.jar"),
                       os.path.join(mc, "assets", "indexes", "5.json"), os.path.join(mc, "assets")))
    loom = os.path.join(HOME, ".gradle", "caches", "fabric-loom")
    candidates.append((os.path.join(loom, "1.20.1", "minecraft-client.jar"),
                       os.path.join(loom, "assets", "indexes", "1.20.1-5.json"), os.path.join(loom, "assets")))
    for jar_path, index_path, assets in candidates:
        if os.path.isfile(jar_path) and os.path.isfile(index_path):
            return jar_path, index_path, assets
    return None


found = find_minecraft()
if found is None:
    print("Minecraft 1.20.1 est introuvable sur ce PC.")
    print("Ouvre le launcher Minecraft officiel, lance la version 1.20.1 une fois (jusqu'au menu), ferme-la,")
    print("puis relance cet outil.")
    if FROZEN:
        input("Appuie sur Entree pour fermer...")
    sys.exit(1)
JAR, INDEX, ASSETS = found
if len(sys.argv) > 1:
    OUT = sys.argv[1]
elif FROZEN:
    OUT = os.path.join(os.path.dirname(sys.executable), "assets")  # next to SlimeCraft.dll
else:
    OUT = os.path.join(os.path.dirname(__file__), "..", "plugin", "assets")
print("Minecraft :", JAR)
print("Destination :", os.path.abspath(OUT))

jar = zipfile.ZipFile(JAR)
index = json.load(open(INDEX))["objects"]


def jar_bytes(path):
    return jar.read("assets/minecraft/" + path)


def jar_img(path):
    return Image.open(io.BytesIO(jar_bytes(path))).convert("RGBA")


def obj_bytes(key):
    h = index["minecraft/" + key]["hash"]
    return open(os.path.join(ASSETS, "objects", h[:2], h), "rb").read()


def save(img, rel):
    p = os.path.join(OUT, rel)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    img.save(p)


def write(rel, data, mode="wb"):
    p = os.path.join(OUT, rel)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, mode, **({} if "b" in mode else {"encoding": "utf-8"})) as f:
        f.write(data)


if os.path.isdir(OUT):
    shutil.rmtree(OUT)

# --- GUI, font, skin -------------------------------------------------------------------------------
for g in ["gui/widgets.png", "gui/icons.png",
          "gui/container/creative_inventory/tabs.png",
          "gui/container/creative_inventory/tab_items.png",
          "gui/container/creative_inventory/tab_inventory.png",
          "font/ascii.png", "font/accented.png", "font/nonlatin_european.png",
          "entity/player/wide/steve.png", "misc/underwater.png",
          "gui/container/generic_54.png", "gui/container/shulker_box.png", "gui/container/inventory.png"]:
    save(jar_img("textures/" + g), g.replace("entity/player/wide/", ""))

for i in range(10):
    save(jar_img(f"textures/block/destroy_stage_{i}.png"), f"blocks/destroy_stage_{i}.png")

# The font: only the bitmap providers, in order, as "file|height|ascent" followed by the rows.
fonts = json.loads(jar_bytes("font/include/default.json"))
lines = []
for p in fonts["providers"]:
    if p.get("type") != "bitmap":
        continue
    lines.append("P|" + p["file"].split("/")[-1] + "|" + str(p.get("height", 8)) + "|" + str(p["ascent"]))
    for row in p["chars"]:
        lines.append("R|" + row)
write("font.txt", "\n".join(lines), "w")

lang = json.loads(obj_bytes("lang/fr_fr.json").decode("utf-8"))

# --- Blocks ---------------------------------------------------------------------------------------
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mcblocks
nblocks = mcblocks.extract(jar, jar_bytes, jar_img, save, write, lang)

# A few UI strings straight from Minecraft's French translation.
ui = {k: lang[k] for k in ["itemGroup.buildingBlocks", "itemGroup.inventory", "container.inventory",
                           "itemGroup.search", "key.inventory", "itemGroup.coloredBlocks", "itemGroup.natural", "itemGroup.functional", "gameMode.survival", "gameMode.creative", "gameMode.changed", "container.crafting", "container.chest", "container.shulkerBox", "container.barrel"] if k in lang}
write("lang.txt", "\n".join(k + "=" + v for k, v in ui.items()), "w")

# --- Sounds ---------------------------------------------------------------------------------------
want = []
for g, n in (("cloth", 4), ("grass", 4), ("gravel", 4), ("sand", 4), ("snow", 4), ("stone", 4), ("wood", 4)):
    want += [f"dig/{g}{k}" for k in range(1, n + 1)]
for g, n in (("cloth", 4), ("grass", 6), ("gravel", 4), ("sand", 5), ("snow", 4), ("stone", 6), ("wood", 6)):
    want += [f"step/{g}{k}" for k in range(1, n + 1)]
want += ["random/glass1", "random/glass2", "random/glass3", "random/pop", "random/click", "random/eat1",
         "random/eat2", "random/eat3", "random/burp", "random/splash", "random/break",
         "damage/hit1", "damage/hit2", "damage/hit3", "damage/fallsmall", "damage/fallbig"]
want += [f"mob/slime/small{k}" for k in range(1, 6)] + [f"mob/slime/big{k}" for k in range(1, 5)]
want += [f"mob/slime/attack{k}" for k in range(1, 3)]
want += ["block/chest/open", "block/chest/close1", "block/chest/close2", "block/chest/close3",
         "block/shulker_box/open", "block/shulker_box/close", "block/barrel/open1", "block/barrel/open2", "block/barrel/close"]
want += [f"entity/player/attack/{t}{k}" for t, n in (("strong", 6), ("weak", 4), ("knockback", 4)) for k in range(1, n + 1)]
for w in want:
    write("sounds/" + w + ".ogg", obj_bytes("sounds/" + w + ".ogg"))

print("Termine :", nblocks, "blocs et", len(want), "sons extraits. Tu peux lancer Slime Rancher.")
if FROZEN:
    input("Appuie sur Entree pour fermer...")
