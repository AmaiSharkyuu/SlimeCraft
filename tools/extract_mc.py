r"""
Builds SlimeCraft's assets from the player's own Minecraft 1.20.1 files, so the mod never ships Mojang's assets.
Built into SlimeCraft-Assets.exe for players; run with Python by modders.

Inputs
  * the 1.20.1 client jar and asset index of the official launcher (%APPDATA%\.minecraft), or a Fabric Loom cache:
    textures, models, blockstates, loot tables, sounds and every language file
  * data/mcdata.json, written by running Minecraft's own code (the SubnautiCraft dumper): block states and shapes,
    items, recipes, tags, enchantments, effects, potions, mob stats
  * data/mclayers.json (render layers read from the decompiled client) and data/mobmodels.json (entity models)

Output (plugin/assets, or the "assets" folder next to the .exe)
  textures/<block|item|entity|gui|...>/*.png    every texture (animations listed in mc.json)
  mc.json                                        blocks, items, models, blockstates, recipes, tags, loot,
                                                 enchantments, effects, sound events, English names, tints, layers
  lang/<code>.json                               Minecraft's own texts in every language Slime Rancher offers
  sounds/<path>.ogg, font.txt, steve.png, amai.png (Amai's skin, when it is on this PC), mobmodels.json
"""
import io, json, os, re, shutil, sys, zipfile
from PIL import Image

FROZEN = getattr(sys, "frozen", False)
HERE = os.path.dirname(sys.executable) if FROZEN else os.path.dirname(os.path.abspath(__file__))
BUNDLE = getattr(sys, "_MEIPASS", HERE)  # the .exe carries data/ inside
HOME = os.path.expanduser("~")


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
    mc = os.path.join(os.environ.get("APPDATA", HOME), ".minecraft")
    loom = os.path.join(HOME, ".gradle", "caches", "fabric-loom")
    for jar_path, index_path, assets in (
            (os.path.join(mc, "versions", "1.20.1", "1.20.1.jar"), os.path.join(mc, "assets", "indexes", "5.json"), os.path.join(mc, "assets")),
            (os.path.join(loom, "1.20.1", "minecraft-client.jar"), os.path.join(loom, "assets", "indexes", "1.20.1-5.json"), os.path.join(loom, "assets"))):
        if os.path.isfile(jar_path) and os.path.isfile(index_path):
            return jar_path, index_path, assets
    return None


found = find_minecraft()
if found is None:
    say("Minecraft 1.20.1 est introuvable sur ce PC.", "Minecraft 1.20.1 was not found on this PC.")
    say("Ouvre le launcher Minecraft officiel, lance la version 1.20.1 une fois (jusqu'au menu), ferme-la, puis relance cet outil.",
        "Open the official Minecraft launcher, play version 1.20.1 once (up to the title screen), close it, then run this tool again.")
    if FROZEN:
        input("Entree pour fermer..." if FR else "Press Enter to close...")
    sys.exit(1)
JAR, INDEX, ASSETS = found
DATA_DIR = os.path.join(BUNDLE, "data") if FROZEN else os.path.join(HERE, "..", "data")
DATA = os.path.join(DATA_DIR, "mcdata.json")
LAYERS = os.path.join(DATA_DIR, "mclayers.json")
MOBMODELS = os.path.join(DATA_DIR, "mobmodels.json")
DECOMP = os.path.join(HERE, "..", "..", "SubnautiCraft", "decomp", "mc", "net", "minecraft")
SKIN = os.path.join(HOME, "Documents", "Mods", "Skin Amai", "amai.png")
if len(sys.argv) > 1:
    OUT = sys.argv[1]
elif FROZEN:
    OUT = os.path.join(HERE, "assets")
else:
    OUT = os.path.join(HERE, "..", "plugin", "assets")
LANGS = ["en_us", "de_de", "es_es", "fr_fr", "ru_ru", "sv_se", "zh_cn", "ja_jp", "pt_br", "ko_kr"]
say("Minecraft : " + JAR, "Minecraft: " + JAR)
say("Destination : " + os.path.abspath(OUT), "Destination: " + os.path.abspath(OUT))

jar = zipfile.ZipFile(JAR)
names = set(jar.namelist())
index = json.load(open(INDEX))["objects"]
data = json.load(open(DATA, encoding="utf-8"))


def jar_json(path):
    return json.loads(jar.read("assets/minecraft/" + path))


def obj_bytes(key):
    h = index["minecraft/" + key]["hash"]
    return open(os.path.join(ASSETS, "objects", h[:2], h), "rb").read()


def write(rel, b):
    p = os.path.join(OUT, rel)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, "wb") as f:
        f.write(b)


if os.path.isdir(OUT):
    shutil.rmtree(OUT)
os.makedirs(OUT)

# ------------------------------------------------------------------------------------------------ textures
anim = {}
for n in sorted(names):
    if not n.startswith("assets/minecraft/textures/") or not n.endswith(".png"):
        continue
    rel = n[len("assets/minecraft/textures/"):]
    top = rel.split("/")[0]
    if top not in ("block", "item", "entity", "gui", "font", "particle", "misc", "environment", "mob_effect", "models", "map", "trims"):
        continue
    img = Image.open(io.BytesIO(jar.read(n))).convert("RGBA")
    meta = n + ".mcmeta"
    if meta in names and img.height > img.width and top in ("block", "item", "misc", "entity"):
        m = json.loads(jar.read(meta)).get("animation", {})
        w = img.width
        frames = img.height // w
        anim[rel[:-4]] = {"frames": frames, "time": m.get("frametime", 1), "order": [f if isinstance(f, int) else f["index"] for f in m.get("frames", [])], "interpolate": m.get("interpolate", False)}
        # keep the whole strip: the plugin animates it by moving the UV window
    b = io.BytesIO()
    img.save(b, "PNG")
    write("textures/" + rel, b.getvalue())

if os.path.isfile(SKIN):
    shutil.copy(SKIN, os.path.join(OUT, "amai.png"))  # Amai's own skin (slim arms); everyone else plays Steve
if os.path.isfile(MOBMODELS):
    shutil.copy(MOBMODELS, os.path.join(OUT, "mobmodels.json"))
write("steve.png", jar.read("assets/minecraft/textures/entity/player/wide/steve.png"))

# ------------------------------------------------------------------------------------------------ font
fonts = jar_json("font/include/default.json")
lines = []
for p in fonts["providers"]:
    if p.get("type") != "bitmap":
        continue
    lines.append("P|" + p["file"].split("/")[-1] + "|" + str(p.get("height", 8)) + "|" + str(p["ascent"]))
    for row in p["chars"]:
        lines.append("R|" + row)
write("font.txt", "\n".join(lines).encode("utf-8"))

# ------------------------------------------------------------------------------------------------ models
raw_models = {}
for n in names:
    if n.startswith("assets/minecraft/models/") and n.endswith(".json"):
        raw_models[n[len("assets/minecraft/models/"):-5]] = json.loads(jar.read(n))


def norm(ref):
    ref = ref.replace("minecraft:", "")
    return ref


def resolve(name, seen=None):
    """Flattens a model: inherits elements, textures and display from the parent chain and resolves #refs."""
    name = norm(name)
    m = raw_models.get(name)
    if m is None:
        return None
    chain = []
    cur = name
    while cur and cur in raw_models:
        chain.append(raw_models[cur])
        cur = norm(raw_models[cur].get("parent", "")) or None
        if cur in ("builtin/generated", "builtin/entity"):
            chain.append({"__builtin": cur})
            break
    textures, display, elements, builtin, gui_light, ao = {}, {}, None, None, None, True
    for m in reversed(chain):
        if "__builtin" in m:
            builtin = m["__builtin"]
            continue
        textures.update(m.get("textures", {}))
        for k, v in m.get("display", {}).items():
            display[k] = v
        if "elements" in m:
            elements = m["elements"]
        if "gui_light" in m:
            gui_light = m["gui_light"]
        if "ambientocclusion" in m:
            ao = m["ambientocclusion"]

    def tex(t, depth=0):
        while t and t.startswith("#") and depth < 10:
            t = textures.get(t[1:])
            depth += 1
        return norm(t) if t else None

    out = {}
    if builtin:
        out["builtin"] = builtin.split("/")[1]
    if builtin == "builtin/generated" and elements is None:
        layers = []
        for i in range(5):
            t = tex("#layer%d" % i)
            if t:
                layers.append(t)
        out["layers"] = layers
    if elements:
        els = []
        for e in elements:
            faces = {}
            for d, f in e.get("faces", {}).items():
                ff = {"tex": tex(f["texture"])}
                if "uv" in f: ff["uv"] = f["uv"]
                if f.get("rotation"): ff["rot"] = f["rotation"]
                if "tintindex" in f: ff["tint"] = f["tintindex"]
                if "cullface" in f: ff["cull"] = f["cullface"]
                faces[d] = ff
            ee = {"from": e["from"], "to": e["to"], "faces": faces}
            if "rotation" in e: ee["rotation"] = e["rotation"]
            if e.get("shade") is False: ee["shade"] = False
            els.append(ee)
        out["elements"] = els
    if display:
        out["display"] = display
    if gui_light:
        out["gui_light"] = gui_light
    p = tex("#particle")
    if p:
        out["particle"] = p
    return out


blockstates = {}
for n in names:
    if n.startswith("assets/minecraft/blockstates/") and n.endswith(".json"):
        blockstates[n[len("assets/minecraft/blockstates/"):-5]] = json.loads(jar.read(n))

used = set()


def collect(bs):
    def add(v):
        for m in (v if isinstance(v, list) else [v]):
            used.add(norm(m["model"]))
    if "variants" in bs:
        for v in bs["variants"].values():
            add(v)
    for part in bs.get("multipart", []):
        add(part["apply"])


for bs in blockstates.values():
    collect(bs)
for k in data["items"]:
    if "item/" + k in raw_models:
        used.add("item/" + k)
        for o in raw_models["item/" + k].get("overrides", []):
            used.add(norm(o["model"]))

models = {}
for m in sorted(used):
    r = resolve(m)
    if r is not None:
        models[m] = r

item_models = {}
for k in data["items"]:
    m = raw_models.get("item/" + k)
    if m is None:
        continue
    ov = [{"predicate": o["predicate"], "model": norm(o["model"])} for o in m.get("overrides", [])]
    item_models[k] = {"model": "item/" + k, "overrides": ov}

# ------------------------------------------------------------------------------------------------ names
en = jar_json("lang/en_us.json")


def tr(key):
    return en.get(key) or key


KEEP_PREFIX = ("gameMode.", "container.", "itemGroup.", "key.", "menu.", "deathScreen.", "chat.", "commands.",
               "effect.", "enchantment.", "potion.", "item.minecraft.potion", "item.minecraft.splash_potion",
               "item.minecraft.lingering_potion", "item.minecraft.tipped_arrow", "death.", "gui.", "options.",
               "argument.", "attribute.", "multiplayer.player", "selectWorld.gameMode", "item.durability",
               "item.modifiers", "item.color", "book.", "advancements.", "subtitles.", "stat.", "mount.",
               "createWorld.", "debug.", "title.", "entity.minecraft.", "inventory.", "block.minecraft.", "item.minecraft.")
lang_keys = {k: v for k, v in en.items() if k.startswith(KEEP_PREFIX)}
# every language Slime Rancher offers, from Minecraft's own translations (missing keys fall back to English)
for code in LANGS:
    if code == "en_us":
        texts = en
    else:
        try:
            texts = json.loads(obj_bytes("lang/" + code + ".json").decode("utf-8"))
        except KeyError:
            continue
    write("lang/" + code + ".json", json.dumps({k: v for k, v in texts.items() if k.startswith(KEEP_PREFIX)}, ensure_ascii=False, separators=(",", ":")).encode("utf-8"))

# ------------------------------------------------------------------------------------------------ render layers and tints
layers = {}
render_layers = os.path.join(DECOMP, "client", "render", "RenderLayers.java")
if os.path.isfile(render_layers):
    # modder's machine: read the render layers from the decompiled client and keep them for players
    src = open(render_layers, encoding="utf-8").read()
    var_layer = dict(re.findall(r"RenderLayer (renderLayer\d?) = RenderLayer\.get(\w+)\(\)", src))
    for var, blk in [(v, b) for b, v in re.findall(r"map\.put\(Blocks\.(\w+), (renderLayer\d?)\)", src)]:
        kind = var_layer.get(var, "Solid").lower()
        layers[blk.lower()] = "translucent" if kind in ("translucent", "tripwire") else "cutout"
    if not FROZEN:
        with open(LAYERS, "w", encoding="utf-8") as f:
            json.dump(layers, f, separators=(",", ":"))
else:
    layers = json.load(open(LAYERS, encoding="utf-8"))
for b, o in data["blocks"].items():
    if o["class"] in ("LeavesBlock", "CherryLeavesBlock", "MangroveLeavesBlock"):
        layers[b] = "cutout"
layers["water"] = "translucent"
layers["bubble_column"] = "translucent"

GRASS, FOLIAGE, WATER = 0x91BD59, 0x77AB2F, 0x3F76E4
tints = {}
for b in ("grass_block", "grass", "tall_grass", "fern", "large_fern", "potted_fern", "sugar_cane"):
    tints[b] = GRASS
for b in ("oak_leaves", "jungle_leaves", "acacia_leaves", "dark_oak_leaves", "mangrove_leaves", "vine"):
    tints[b] = FOLIAGE
tints.update({"spruce_leaves": 0x619961, "birch_leaves": 0x80A755, "lily_pad": 0x208030, "water": WATER,
              "water_cauldron": WATER, "bubble_column": WATER, "melon_stem": 0xE0C71C, "pumpkin_stem": 0xE0C71C,
              "attached_melon_stem": 0xE0C71C, "attached_pumpkin_stem": 0xE0C71C, "redstone_wire": 0x9F0000})

# ------------------------------------------------------------------------------------------------ loot tables (block drops)
loot = {}
for n in names:
    pass
# Loot tables live in the jar's data folder (same zip for the client jar).
for n in jar.namelist():
    if n.startswith("data/minecraft/loot_tables/blocks/") and n.endswith(".json"):
        loot[n.split("/")[-1][:-5]] = json.loads(jar.read(n))
ent_loot = {}
for n in jar.namelist():
    if n.startswith("data/minecraft/loot_tables/entities/") and n.endswith(".json") and n.count("/") == 4:
        ent_loot[n.split("/")[-1][:-5]] = json.loads(jar.read(n))
    elif n.startswith("data/minecraft/loot_tables/gameplay/") and n.endswith(".json"):
        # fishing and friends, keyed by their path ("gameplay/fishing/fish"); "fishing" is the root table
        key = n[len("data/minecraft/loot_tables/"):-5]
        ent_loot[key] = json.loads(jar.read(n))
        if key == "gameplay/fishing":
            ent_loot["fishing"] = ent_loot[key]

# ------------------------------------------------------------------------------------------------ sounds
sounds_json = json.loads(obj_bytes("sounds.json").decode("utf-8"))
KEEP = ("block.", "item.", "entity.player.", "entity.generic.", "entity.experience_orb.", "entity.item.",
        "entity.arrow.", "entity.firework_rocket.", "entity.drowned.", "entity.guardian.", "entity.elder_guardian.",
        "entity.cod.", "entity.salmon.", "entity.tropical_fish.", "entity.pufferfish.", "entity.squid.",
        "entity.glow_squid.", "entity.dolphin.", "entity.turtle.", "entity.axolotl.", "entity.trident.",
        "entity.fishing_bobber.", "entity.boat.", "entity.lightning_bolt.", "entity.tnt.", "entity.egg.",
        "entity.snowball.", "entity.ender_pearl.", "entity.splash_potion.", "entity.experience_bottle.",
        "entity.painting.", "entity.item_frame.", "entity.leash_knot.", "entity.armor_stand.", "entity.minecart.",
        "entity.zombie.", "entity.skeleton.", "entity.creeper.", "entity.spider.", "entity.enderman.",
        "ui.", "ambient.underwater.", "random.", "music_disc.", "enchant.", "entity.puffer_fish.",
        "entity.hostile.", "entity.player.splash", "weather.rain")
SKIP = ("block.note_block.", "music_disc.")
events = {}
files = set()
for ev, d in sounds_json.items():
    if not ev.startswith(KEEP) or ev.startswith(SKIP):
        continue
    lst = []
    for s in d.get("sounds", []):
        if isinstance(s, str):
            s = {"name": s}
        if s.get("type") == "event":
            lst.append({"event": s["name"].replace("minecraft:", ""), "weight": s.get("weight", 1)})
            continue
        nm = s["name"].replace("minecraft:", "")
        lst.append({"file": nm, "volume": s.get("volume", 1), "pitch": s.get("pitch", 1), "weight": s.get("weight", 1)})
        files.add(nm)
    events[ev] = lst
total = 0
for f in sorted(files):
    key = "sounds/" + f + ".ogg"
    if "minecraft/" + key not in index:
        continue
    b = obj_bytes(key)
    total += len(b)
    write(key, b)

# ------------------------------------------------------------------------------------------------ write
mc = {
    "blocks": data["blocks"], "shapes": data["shapes"], "items": data["items"],
    "blockTags": data["blockTags"], "itemTags": data["itemTags"], "recipes": data["recipes"], "fuel": data["fuel"],
    "enchantments": data["enchantments"], "effects": data["effects"], "potions": data["potions"],
    "brewing": data.get("brewing", []), "mobs": data["mobs"],
    "models": models, "blockstates": blockstates, "itemModels": item_models,
    "names": {**{k: tr(o["key"]) for k, o in data["items"].items()}},
    "blockNames": {k: tr(o["key"]) for k, o in data["blocks"].items()},
    "lang": lang_keys, "layers": layers, "tints": tints, "loot": loot, "entityLoot": ent_loot,
    "sounds": events, "animations": anim,
}
write("mc.json", json.dumps(mc, ensure_ascii=False, separators=(",", ":")).encode("utf-8"))
print("models", len(models), "blockstates", len(blockstates), "items", len(item_models), "sound events", len(events),
      "sound files", len(files), "%.1f MB" % (total / 1e6), "animations", len(anim))
say("Termine. Tu peux lancer Slime Rancher.", "Done. You can start Slime Rancher.")
if FROZEN:
    input("Entree pour fermer..." if FR else "Press Enter to close...")
