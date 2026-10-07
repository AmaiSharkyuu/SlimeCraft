"""
Reads Minecraft's entity model definitions (getTexturedModelData in the decompiled client) and writes them as
JSON part trees for the plugin: cuboids with their box UVs, mirroring and dilation, pivots and rotations.
Loops (squid tentacles, guardian spikes) and the biped base are expanded by small hand-written rules below.

Output: plugin/assets/mobmodels.json  { name: {"tex": [w, h], "parts": [ {name, pivot, rot, cuboids, children} ]} }
"""
import json, math, os, re

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "..", "decomp", "mc", "net", "minecraft", "client", "render", "entity", "model")
OUT = os.path.join(HERE, "..", "plugin", "assets", "mobmodels.json")


def num(expr, env):
    e = expr.strip()
    e = re.sub(r"\(float\)\s*", "", e)
    e = re.sub(r"(\d+(?:\.\d+)?)[FfDd]\b", r"\1", e)
    e = e.replace("Math.PI", "math.pi").replace("MathHelper.PI", "math.pi")
    for k, v in env.items():
        e = re.sub(r"\b%s\b" % re.escape(k), repr(v), e)
    return float(eval(e, {"math": math}))


def split_args(s):
    out, depth, cur = [], 0, ""
    for ch in s:
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            depth -= 1
        if ch == "," and depth == 0:
            out.append(cur)
            cur = ""
        else:
            cur += ch
    if cur.strip():
        out.append(cur)
    return [a.strip() for a in out]


def find_call(text, start):
    """Returns the argument string of the call whose '(' is at text[start]."""
    depth = 0
    for i in range(start, len(text)):
        if text[i] == "(":
            depth += 1
        elif text[i] == ")":
            depth -= 1
            if depth == 0:
                return text[start + 1:i], i
    return None, len(text)


def parse_builder(b, env):
    cuboids = []
    u = v = 0
    mirror = False
    for m in re.finditer(r"\.(uv|mirrored|cuboid)\(", b):
        args, _ = find_call(b, m.end() - 1)
        name = m.group(1)
        a = split_args(args) if args else []
        if name == "uv":
            u, v = int(num(a[0], env)), int(num(a[1], env))
        elif name == "mirrored":
            mirror = True if not a else a[0].strip() == "true"
        else:
            if a and a[0].startswith('"'):
                a = a[1:]
            vals = []
            dil = 0.0
            cmirror = mirror
            for x in a:
                if x.strip() in ("true", "false"):
                    cmirror = x.strip() == "true"
                    continue
                try:
                    vals.append(num(x, env))
                except Exception:
                    d = env.get("__dil_" + x.strip())
                    if d is None and "new Dilation(" in x:
                        d = num(re.search(r"new Dilation\(([^)]*)\)", x).group(1), env)
                    if d is None and ".add(" in x:
                        base, add = re.match(r"(\w+)\.add\(([^)]*)\)", x.strip()).groups()
                        d = env.get("__dil_" + base, 0.0) + num(add, env)
                    dil = d or 0.0
            if len(vals) >= 6:
                c = {"uv": [u, v], "pos": vals[0:3], "size": vals[3:6], "dil": dil, "mirror": cmirror}
                if len(vals) >= 9:
                    c["dil"] = vals[6]
                cuboids.append(c)
    return cuboids


def parse_transform(t, env):
    t = t.strip()
    if t.endswith("NONE") or t == "ModelTransform.NONE":
        return [0, 0, 0], [0, 0, 0]
    m = re.match(r"ModelTransform\.(pivot|of|rotation)\((.*)\)$", t, re.S)
    if not m:
        return [0, 0, 0], [0, 0, 0]
    a = [num(x, env) for x in split_args(m.group(2))]
    if m.group(1) == "pivot":
        return a[0:3], [0, 0, 0]
    if m.group(1) == "rotation":
        return [0, 0, 0], a[0:3]
    return a[0:3], a[3:6]


def parse_method(text, env):
    """Parses the body of a getTexturedModelData-like method into a part tree."""
    root = {"name": "root", "pivot": [0, 0, 0], "rot": [0, 0, 0], "cuboids": [], "children": []}
    vars_ = {"modelPartData": root}
    tex = [64, 64]
    for m in re.finditer(r"Dilation (\w+) = new Dilation\(([^)]*)\)", text):
        env["__dil_" + m.group(1)] = num(m.group(2), env)
    for m in re.finditer(r"float (\w+) = ([^;]+);", text):
        try:
            env[m.group(1)] = num(m.group(2), env)
        except Exception:
            pass
    builders = {}
    for m in re.finditer(r"ModelPartBuilder (\w+) = (ModelPartBuilder\.create\(\)[^;]*);", text, re.S):
        builders[m.group(1)] = m.group(2)
    for m in re.finditer(r"(?:ModelPartData (\w+) = )?(\w+)\.addChild\(", text):
        args, _ = find_call(text, m.end() - 1)
        a = split_args(args)
        if len(a) < 3 or not a[0].startswith('"'):
            continue
        parent = vars_.get(m.group(2))
        if parent is None:
            continue
        name = a[0].strip('"')
        cub = parse_builder(builders.get(a[1].strip(), a[1]), env)
        try:
            pivot, rot = parse_transform(a[2], env)
        except Exception:
            pivot, rot = [0, 0, 0], [0, 0, 0]
        part = {"name": name, "pivot": pivot, "rot": rot, "cuboids": cub, "children": []}
        parent["children"].append(part)
        if m.group(1):
            vars_[m.group(1)] = part
    t = re.search(r"TexturedModelData\.of\(\w+, (\d+), (\d+)\)", text)
    if t:
        tex = [int(t.group(1)), int(t.group(2))]
    return {"tex": tex, "parts": root["children"]}


def method_body(src, name="getTexturedModelData"):
    i = src.find(name + "(")
    if i < 0:
        return None
    j = src.find("{", i)
    depth = 0
    for k in range(j, len(src)):
        if src[k] == "{":
            depth += 1
        elif src[k] == "}":
            depth -= 1
            if depth == 0:
                return src[j:k + 1]
    return None


def read(cls):
    p = os.path.join(SRC, cls + ".java")
    return open(p, encoding="utf-8").read()


models = {}
simple = {
    "cod": ("CodEntityModel", None), "salmon": ("SalmonEntityModel", None), "dolphin": ("DolphinEntityModel", None),
    "turtle": ("TurtleEntityModel", None), "axolotl": ("AxolotlEntityModel", None), "frog": ("FrogEntityModel", None),
    "tadpole": ("TadpoleEntityModel", None), "pufferfish_big": ("LargePufferfishEntityModel", None),
    "pufferfish_medium": ("MediumPufferfishEntityModel", None), "pufferfish_small": ("SmallPufferfishEntityModel", None),
    "tropical_fish_a": ("SmallTropicalFishEntityModel", None), "tropical_fish_b": ("LargeTropicalFishEntityModel", None),
}
for key, (cls, _) in simple.items():
    try:
        body = method_body(read(cls))
        models[key] = parse_method(body, {"dilation": 0.0})
    except Exception as e:
        print("failed", key, e)

# squid: 8 tentacles around the body (SquidEntityModel loop)
sq = {"tex": [64, 32], "parts": [{"name": "body", "pivot": [0, 8, 0], "rot": [0, 0, 0], "cuboids": [{"uv": [0, 0], "pos": [-6, -8, -6], "size": [12, 16, 12], "dil": 0.02, "mirror": False}], "children": []}]}
for k in range(8):
    d = k * math.pi * 2.0 / 8.0
    f = math.cos(d) * 5.0
    h = math.sin(d) * 5.0
    l = d * -1 + math.pi / 2  # (float)(k * Math.PI * -2.0 / 8.0 + Math.PI / 2)
    sq["parts"].append({"name": "tentacle%d" % k, "pivot": [f, 15, h], "rot": [0, l, 0], "cuboids": [{"uv": [48, 0], "pos": [-1, 0, -1], "size": [2, 18, 2], "dil": 0, "mirror": False}], "children": []})
models["squid"] = sq

# guardian: head with eye, tail segments and 12 spikes (GuardianEntityModel)
try:
    g = read("GuardianEntityModel")
    body = method_body(g)
    gm = parse_method(body, {})
    pitches = [1.75, 0.25, 0.0, 0.0, 0.5, 0.5, 0.5, 0.5, 1.25, 0.75, 0.0, 0.0]
    yaws = [0.0, 0.0, 0.0, 0.0, 0.25, 1.75, 1.25, 0.75, 0.0, 0.0, 0.0, 0.0]
    rolls = [0.0, 0.0, 0.25, 1.75, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.75, 1.25]
    px = [0.0, 0.0, 8.0, -8.0, -8.0, 8.0, 8.0, -8.0, 0.0, 0.0, 8.0, -8.0]
    py = [-8.0, -8.0, -8.0, -8.0, 0.0, 0.0, 0.0, 0.0, 8.0, 8.0, 8.0, 8.0]
    pz = [8.0, -8.0, 0.0, 0.0, -8.0, -8.0, 8.0, 8.0, 8.0, -8.0, 0.0, 0.0]
    head = gm["parts"][0]
    for i in range(12):
        # getSpikePivot with animation 0 / extension 0: angle = 1 + cos(...)*0.01 - 0 ~ 1
        head["children"].append({"name": "spike%d" % i, "pivot": [px[i], 16 + py[i], pz[i]], "rot": [math.pi * pitches[i], math.pi * yaws[i], math.pi * rolls[i]],
                                 "cuboids": [{"uv": [0, 0], "pos": [-1, -4.5, -1], "size": [2, 9, 2], "dil": 0, "mirror": False}], "children": []})
    models["guardian"] = gm
except Exception as e:
    print("guardian", e)

# drowned: the zombie biped with the drowned's own arms and legs (DrownedEntityModel)
def biped(dil=0.0):
    def c(u, v, x, y, z, sx, sy, sz, d=dil, mirror=False):
        return {"uv": [u, v], "pos": [x, y, z], "size": [sx, sy, sz], "dil": d, "mirror": mirror}
    return [
        {"name": "head", "pivot": [0, 0, 0], "rot": [0, 0, 0], "cuboids": [c(0, 0, -4, -8, -4, 8, 8, 8)], "children": [
            {"name": "hat", "pivot": [0, 0, 0], "rot": [0, 0, 0], "cuboids": [c(32, 0, -4, -8, -4, 8, 8, 8, dil + 0.5)], "children": []}]},
        {"name": "body", "pivot": [0, 0, 0], "rot": [0, 0, 0], "cuboids": [c(16, 16, -4, 0, -2, 8, 12, 4)], "children": []},
        {"name": "right_arm", "pivot": [-5, 2, 0], "rot": [0, 0, 0], "cuboids": [c(40, 16, -3, -2, -2, 4, 12, 4)], "children": []},
        {"name": "left_arm", "pivot": [5, 2, 0], "rot": [0, 0, 0], "cuboids": [c(32, 48, -1, -2, -2, 4, 12, 4)], "children": []},
        {"name": "right_leg", "pivot": [-1.9, 12, 0], "rot": [0, 0, 0], "cuboids": [c(16, 48, -2, 0, -2, 4, 12, 4)], "children": []},
        {"name": "left_leg", "pivot": [1.9, 12, 0], "rot": [0, 0, 0], "cuboids": [c(0, 16, -2, 0, -2, 4, 12, 4, mirror=True)], "children": []},
    ]
models["drowned"] = {"tex": [64, 64], "parts": biped()}
models["drowned_outer"] = {"tex": [64, 64], "parts": biped(0.25)}

for k, m in models.items():
    n = sum(1 for _ in m["parts"])
    print(k, m["tex"], n, "parts")
json.dump(models, open(OUT, "w"), separators=(",", ":"))
