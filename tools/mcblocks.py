"""
Every Minecraft block SlimeCraft can build, read straight from the jar's blockstates and models: plain cubes,
columns (logs), slabs, stairs, fences, carpets, crossed plants (flowers, saplings), torches, plus the
containers (chests, barrels, shulker boxes). Writes blocks.txt and one PNG per face texture.

blocks.txt columns: id|name|render|sound|hardness|light|top|side|bottom|front|shape|tab|survival
"""
import json
from PIL import Image

GRASS = (0x91, 0xBD, 0x59)
FOLIAGE = {"spruce_leaves": (0x61, 0x99, 0x61), "birch_leaves": (0x80, 0xA7, 0x55)}
DEFAULT_FOLIAGE = (0x77, 0xAB, 0x2F)
COLORS = ["white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray",
          "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black"]

KNOWN = {"block/cube_all": "cube", "block/leaves": "cube", "block/cube_column": "column",
         "block/cube_column_horizontal": "column", "block/cube_bottom_top": "cube", "block/cube_top": "cube",
         "block/cube": "cube", "block/orientable": "cube", "block/orientable_with_bottom": "cube",
         "block/template_glazed_terracotta": "cube", "block/cube_mirrored_all": "cube",
         "block/cube_north_west_mirrored_all": "cube", "block/slab": "slab", "block/inner_stairs": "stairs",
         "block/stairs": "stairs", "block/fence_post": "fence", "block/carpet": "carpet",
         "block/cross": "cross", "block/tinted_cross": "cross", "block/template_torch": "torch"}

LIGHT = {"glowstone": 15, "sea_lantern": 15, "jack_o_lantern": 15, "shroomlight": 15, "ochre_froglight": 15,
         "verdant_froglight": 15, "pearlescent_froglight": 15, "torch": 14, "soul_torch": 10, "redstone_torch": 7,
         "magma_block": 3, "crying_obsidian": 10, "beacon": 15}
TRANSLUCENT = ("stained_glass", "ice", "slime_block", "honey_block")
SKIP = ("_wall_", "potted_", "infested_", "petrified", "_bed", "barrier", "structure_void", "moving", "cave_air",
        "void_air", "piston_head", "spawner", "budding", "command_block", "jigsaw", "structure_block",
        "frosted_ice", "dirt_path", "farmland", "redstone_wire", "tripwire", "fire", "attached", "lava", "water")

# The free palette of survival mode: plain building materials, nothing precious.
SURVIVAL = {"oak_planks", "spruce_planks", "birch_planks", "oak_log", "spruce_log", "birch_log", "cobblestone",
            "stone", "stone_bricks", "bricks", "glass", "torch", "oak_fence", "spruce_fence", "white_wool",
            "oak_slab", "oak_stairs", "cobblestone_slab", "cobblestone_stairs", "stone_brick_slab",
            "stone_brick_stairs", "dirt", "sand", "sandstone", "oak_leaves", "hay_block", "crafting_table",
            "poppy", "dandelion", "white_carpet", "chest", "barrel", "shulker_box", "gravel", "mossy_cobblestone"}


def tint(img, rgb):
    r, g, b, a = img.split()
    r = r.point(lambda v: v * rgb[0] // 255)
    g = g.point(lambda v: v * rgb[1] // 255)
    b = b.point(lambda v: v * rgb[2] // 255)
    return Image.merge("RGBA", (r, g, b, a))


def sound_of(i):
    if any(w in i for w in ("wool", "carpet")): return "cloth"
    if any(w in i for w in ("glass", "ice", "glowstone", "sea_lantern", "froglight", "beacon", "amethyst")): return "glass"
    if any(w in i for w in ("planks", "_log", "_wood", "_stem", "hyphae", "fence", "bookshelf", "crafting", "barrel",
                            "chest", "pumpkin", "melon", "torch", "bamboo", "mushroom", "note_block", "jukebox",
                            "loom", "cartography", "fletching", "smithing")): return "wood"
    if ("sand" in i and "sandstone" not in i) or "concrete_powder" in i: return "sand"
    if any(w in i for w in ("gravel", "dirt", "clay", "soul_soil", "podzol", "mycelium", "mud", "rooted")): return "gravel"
    if "snow" in i or "powder_snow" in i: return "snow"
    if any(w in i for w in ("grass", "leaves", "moss", "hay", "sponge", "sapling", "flower", "tulip", "orchid", "daisy",
                            "fern", "bush", "azalea", "dandelion", "poppy", "allium", "bluet", "lily", "rose", "petals",
                            "slime", "honey", "nylium", "wart", "shroomlight", "target", "dried_kelp", "roots",
                            "cornflower", "torchflower", "pitcher", "dead_bush", "shulker")): return "grass"
    return "stone"


def hardness_of(i, shape):
    if shape in ("cross", "torch"): return 0.0
    if "obsidian" in i: return 50.0
    if "deepslate" in i or "_ore" in i: return 3.0
    if any(w in i for w in ("iron_block", "diamond_block", "emerald_block", "netherite", "ancient")): return 5.0
    if any(w in i for w in ("wool", "carpet")): return 0.8
    if any(w in i for w in ("glass", "glowstone", "sea_lantern", "leaves")): return 0.3
    return {"wood": 2.0, "sand": 0.5, "gravel": 0.6, "grass": 0.6, "snow": 0.2, "cloth": 0.8}.get(sound_of(i), 1.5)


NATURAL_WORDS = ("_ore", "_log", "leaves", "dirt", "grass", "sand", "gravel", "clay", "moss", "snow", "ice",
                 "netherrack", "end_stone", "mushroom", "coral", "mycelium", "podzol", "mud", "_stem", "nylium",
                 "wart", "shroomlight", "basalt", "blackstone", "calcite", "tuff", "dripstone", "amethyst",
                 "obsidian", "bone_block", "pumpkin", "melon", "hay_block", "sponge", "magma", "soul_soil",
                 "glowstone", "stone", "deepslate", "granite", "diorite", "andesite", "raw_")
BUILT_WORDS = ("brick", "polished", "planks", "smooth", "cut_", "chiseled", "tile", "pillar", "stripped")


def tab_of(i, shape):
    colored_kinds = ("wool", "carpet", "concrete", "terracotta", "glass", "shulker")
    if (any(i.startswith(c + "_") for c in COLORS) and any(w in i for w in colored_kinds)) or i in ("terracotta", "glass", "tinted_glass", "shulker_box"):
        return "colored"
    if shape in ("chest", "barrel") or i in ("crafting_table", "bookshelf", "torch", "soul_torch", "redstone_torch",
                                             "furnace", "smoker", "blast_furnace", "loom", "cartography_table",
                                             "fletching_table", "smithing_table", "jukebox", "note_block", "target",
                                             "glowstone", "sea_lantern", "shroomlight", "jack_o_lantern") or "froglight" in i:
        return "functional"
    if shape == "cross":
        return "natural"
    if shape in ("slab", "stairs", "fence") or any(w in i for w in BUILT_WORDS):
        return "building"
    if any(w in i for w in NATURAL_WORDS):
        return "natural"
    return "building"


def extract(jar, jar_bytes, jar_img, save, write, lang):
    def tex(name):
        img = jar_img("textures/block/" + name + ".png")
        if img.height > img.width:  # animated strip: first frame
            img = img.crop((0, 0, img.width, img.width))
        return img

    def model_json(name):
        return json.loads(jar_bytes("models/" + name.split(":")[-1] + ".json"))

    def resolve(name):
        textures = {}
        m = model_json(name)
        template = None
        while True:
            for k, v in m.get("textures", {}).items():
                textures.setdefault(k, v)
            par = m.get("parent")
            if not par:
                break
            par = par.split(":")[-1]
            template = par
            if par in KNOWN:
                break
            try:
                m = model_json(par)
            except KeyError:
                break

        def get(k, depth=0):
            v = textures.get(k)
            if v is None or depth > 6:
                return None
            if v.startswith("#"):
                return get(v[1:], depth + 1)
            return v.split(":")[-1].replace("block/", "")
        return template, get

    def faces_for(t, get):
        if t in ("block/cube_all", "block/leaves", "block/cube_mirrored_all", "block/cube_north_west_mirrored_all"):
            a = get("all"); return a, a, a, a
        if t == "block/template_glazed_terracotta":
            a = get("pattern"); return a, a, a, a
        if t in ("block/cube_column", "block/cube_column_horizontal"):
            return get("end"), get("side"), get("end"), get("side")
        if t in ("block/cube_bottom_top", "block/slab", "block/inner_stairs", "block/stairs"):
            return get("top"), get("side"), get("bottom"), get("side")
        if t == "block/cube_top":
            return get("top"), get("side"), get("side"), get("side")
        if t == "block/cube":
            return get("up"), get("east") or get("north"), get("down"), get("north")
        if t in ("block/orientable", "block/orientable_with_bottom"):
            return get("top"), get("side"), get("bottom") or get("top"), get("front")
        if t == "block/fence_post":
            a = get("texture"); return a, a, a, a
        if t == "block/carpet":
            a = get("wool"); return a, a, a, a
        if t in ("block/cross", "block/tinted_cross"):
            a = get("cross"); return a, a, a, a
        if t == "block/template_torch":
            a = get("torch"); return a, a, a, a
        return None

    entries = []
    for n in sorted(x for x in jar.namelist() if x.startswith("assets/minecraft/blockstates/")):
        i = n.split("/")[-1][:-5]
        if any(k in i for k in SKIP) or i in ("air", "light"):
            continue
        d = json.loads(jar.read(n))
        if "variants" in d:
            v = list(d["variants"].values())[0]
            v = v[0] if isinstance(v, list) else v
            mname = v["model"]
        else:
            ap = d["multipart"][0]["apply"]
            mname = (ap[0] if isinstance(ap, list) else ap)["model"]
        if i.endswith("_stairs"):
            mname = mname.replace("_inner", "").replace("_outer", "")
        if i == "grass_block":
            entries.append((i, "cube", "@grass_top", "@grass_side", "dirt", "@grass_side")); continue
        try:
            template, get = resolve(mname)
        except KeyError:
            continue
        if template not in KNOWN:
            continue
        shape = KNOWN[template]
        if template == "block/template_torch" and i not in ("torch", "soul_torch", "redstone_torch"):
            continue
        f = faces_for(template, get)
        if not f or any(x is None for x in f):
            continue
        top, side, bottom, front = f
        if "leaves" in i and "cherry" not in i and "azalea" not in i:
            top = side = bottom = front = "@" + top
        if template == "block/tinted_cross":
            top = side = bottom = front = "@tint_" + top
        entries.append((i, shape, top, side, bottom, front))

    def make(name):
        if name == "@grass_top":
            return tint(tex("grass_block_top"), GRASS)
        if name == "@grass_side":
            return Image.alpha_composite(tex("grass_block_side"), tint(tex("grass_block_side_overlay"), GRASS))
        if name.startswith("@tint_"):
            return tint(tex(name[6:]), GRASS)
        if name.startswith("@"):
            leaf = name[1:]
            return tint(tex(leaf), FOLIAGE.get(leaf, DEFAULT_FOLIAGE))
        return tex(name)

    rows = []
    done = set()
    order_hint = {"cube": 0, "column": 0, "stairs": 1, "slab": 2, "fence": 3, "carpet": 4, "cross": 5, "torch": 6}

    for (i, shape, top, side, bottom, front) in entries:
        faces = []
        ok = True
        for name in (top, side, bottom, front):
            fname = name.lstrip("@").replace("tint_", "tinted_")
            if fname not in done:
                try:
                    save(make(name), "blocks/" + fname + ".png")
                except KeyError:
                    ok = False
                    break
                done.add(fname)
            faces.append(fname)
        if not ok:
            continue
        if shape == "torch":
            render = "torch"
        elif shape == "cross":
            render = "cross"
        elif any(w in i for w in TRANSLUCENT):
            render = "translucent"
        elif "glass" in i or "leaves" in i or i in ("mangrove_roots", "scaffolding"):
            render = "cutout"
        else:
            render = "opaque"
        name = lang.get("block.minecraft." + i, i)
        rows.append([i, name, render, sound_of(i), str(hardness_of(i, shape)), str(LIGHT.get(i, 0)),
                     faces[0], faces[1], faces[2], faces[3], shape, tab_of(i, shape), "1" if i in SURVIVAL else "0"])

    # Containers: chests and shulker boxes are entity models; their textures go to entity/ and the plugin
    # builds the boxes itself. The barrel is a normal cube from above, only flagged as a container.
    save(jar_img("textures/entity/chest/normal.png"), "entity/chest.png")
    rows.append(["chest", lang.get("block.minecraft.chest", "chest"), "opaque", "wood", "2.5", "0",
                 "entity/chest", "entity/chest", "entity/chest", "entity/chest", "chest", "functional", "1"])
    for c in [None] + COLORS:
        bid = "shulker_box" if c is None else c + "_shulker_box"
        tname = "shulker" if c is None else "shulker_" + c
        save(jar_img("textures/entity/shulker/" + tname + ".png"), "entity/" + tname + ".png")
        rows.append([bid, lang.get("block.minecraft." + bid, bid), "opaque", "stone", "2.0", "0",
                     "entity/" + tname, "entity/" + tname, "entity/" + tname, "entity/" + tname, "shulker",
                     "colored" if c else "functional", "1" if c is None else "0"])
    for r in rows:
        if r[0] == "barrel":
            r[10] = "barrel"
            r[11] = "functional"

    def family(r):
        i, shape = r[0], r[10]
        base = i
        for suf in ("_stairs", "_slab", "_fence", "_carpet", "_planks", "_log", "_wood", "_stem", "_hyphae"):
            if base.endswith(suf):
                base = base[: -len(suf)]
        if base.startswith("stripped_"):
            base = base[9:] + "~"
        if base.endswith("_brick"):
            base += "s"
        return (base, order_hint.get(shape, 9), i)

    rows.sort(key=family)
    write("blocks.txt", "\n".join("|".join(r) for r in rows), "w")
    return len(rows)
