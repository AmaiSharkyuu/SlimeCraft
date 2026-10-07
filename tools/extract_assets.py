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


def _windows_is_french():
    try:
        import ctypes
        return (ctypes.windll.kernel32.GetUserDefaultUILanguage() & 0x3FF) == 0x0C
    except Exception:
        return False


FR = _windows_is_french()


def say(fr, en):
    print(fr if FR else en)


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
    say("Minecraft 1.20.1 est introuvable sur ce PC.", "Minecraft 1.20.1 was not found on this PC.")
    say("Ouvre le launcher Minecraft officiel, lance la version 1.20.1 une fois (jusqu'au menu), ferme-la,",
        "Open the official Minecraft launcher, play version 1.20.1 once (up to the title screen), close it,")
    say("puis relance cet outil.", "then run this tool again.")
    if FROZEN:
        input("Entree pour fermer..." if FR else "Press Enter to close...")
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

# English is the fallback (block names in blocks.txt); every language Slime Rancher offers gets its own
# Minecraft translation file, and the plugin follows the language chosen in Slime Rancher's options.
lang = json.loads(jar_bytes("lang/en_us.json").decode("utf-8"))
LANGS = ["en_us", "de_de", "es_es", "fr_fr", "ru_ru", "sv_se", "zh_cn", "ja_jp", "pt_br", "ko_kr"]

# --- Blocks ---------------------------------------------------------------------------------------
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mcblocks
nblocks = mcblocks.extract(jar, jar_bytes, jar_img, save, write, lang)

# A few UI strings straight from Minecraft's French translation.
UI_KEYS = ["itemGroup.buildingBlocks", "itemGroup.inventory", "container.inventory", "itemGroup.search",
           "key.inventory", "itemGroup.coloredBlocks", "itemGroup.natural", "itemGroup.functional",
           "gameMode.survival", "gameMode.creative", "gameMode.changed", "container.crafting", "container.chest",
           "container.shulkerBox", "container.barrel", "inventory.binSlot"]
block_ids = [line.split("|")[0] for line in open(os.path.join(OUT, "blocks.txt"), encoding="utf-8").read().splitlines()]
for code in LANGS:
    try:
        data = lang if code == "en_us" else json.loads(obj_bytes("lang/" + code + ".json").decode("utf-8"))
    except KeyError:
        continue
    out = [k + "=" + data[k] for k in UI_KEYS if k in data]
    out += ["block." + i + "=" + data["block.minecraft." + i] for i in block_ids if "block.minecraft." + i in data]
    write("lang/" + code + ".txt", "\n".join(v.replace("\n", " ") for v in out), "w")

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

say("Termine : %d blocs et %d sons extraits. Tu peux lancer Slime Rancher." % (nblocks, len(want)),
    "Done: %d blocks and %d sounds extracted. You can start Slime Rancher." % (nblocks, len(want)))
if FROZEN:
    input("Entree pour fermer..." if FR else "Press Enter to close...")
