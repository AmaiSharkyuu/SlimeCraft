using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>Where a block is being placed: the clicked face and point, and how the player looks.</summary>
    public struct PlaceCtx
    {
        public Vector3Int Cell;      // the cell the new block goes into
        public Dir Side;             // face of the clicked block (MC direction); the new block sits on that side
        public Vector3 Hit;          // hit point in Unity world space
        public Vector3 Look;         // look direction in Minecraft space
        public bool Sneaking;
        public bool ReplacingClicked;
    }

    /// <summary>
    /// Minecraft's block behaviour that matters in a world without redstone ticks: placement states
    /// (getPlacementState of each block class), connections (fences, walls, panes, stairs shapes), what needs
    /// support (plants, torches, doors) and breaks without it, falling blocks, double blocks (doors, beds, tall
    /// plants) and right-click interactions (doors, trapdoors, gates, levers, buttons, cake...).
    /// Subnautica's own terrain and objects count as solid support, so a torch can go on a rock.
    /// </summary>
    public static class BlockRules
    {
        public static readonly Dir[] Horizontal = { Dir.South, Dir.West, Dir.North, Dir.East }; // Minecraft's horizontal id order

        public static int Horiz(Dir d) => System.Array.IndexOf(Horizontal, d);
        public static Dir Ccw(Dir d) => d == Dir.North ? Dir.West : d == Dir.West ? Dir.South : d == Dir.South ? Dir.East : d == Dir.East ? Dir.North : d;
        public static Dir Cw(Dir d) => d == Dir.North ? Dir.East : d == Dir.East ? Dir.South : d == Dir.South ? Dir.West : d == Dir.West ? Dir.North : d;
        public static bool IsHorizontal(Dir d) => d >= Dir.North && d <= Dir.East;
        public static string Axis(Dir d) => d == Dir.Up || d == Dir.Down ? "y" : d == Dir.North || d == Dir.South ? "z" : "x";

        public static Vector3 McLook(Vector3 unityForward) => new Vector3(unityForward.x, unityForward.y, -unityForward.z);

        public static Dir HorizontalFacing(Vector3 mcLook)
        {
            if (Mathf.Abs(mcLook.x) > Mathf.Abs(mcLook.z)) return mcLook.x > 0 ? Dir.East : Dir.West;
            return mcLook.z > 0 ? Dir.South : Dir.North;
        }

        public static Dir Facing(Vector3 mcLook)
        {
            float ax = Mathf.Abs(mcLook.x), ay = Mathf.Abs(mcLook.y), az = Mathf.Abs(mcLook.z);
            if (ay > ax && ay > az) return mcLook.y > 0 ? Dir.Up : Dir.Down;
            return HorizontalFacing(mcLook);
        }

        public static Dir ParseDir(string s) => ModelBaker.ParseDir(s);
        public static string N(Dir d) => BlockWorld.Name(d);

        /// <summary>Fraction (0..1) of the hit point inside its cell, in Minecraft space.</summary>
        private static Vector3 Frac(PlaceCtx c, Vector3Int cell)
        {
            var p = c.Hit - (Vector3)cell;
            return new Vector3(p.x, p.y, 1f - p.z);
        }

        // ------------------------------------------------------------------------------------------------ placement

        /// <summary>The block to place for an item (walls variants for torches, signs, banners, skulls, coral fans).</summary>
        public static BlockDef BlockFor(ItemDef item, Dir side)
        {
            var b = item.Block;
            if (b == null) return null;
            if (IsHorizontal(side))
            {
                var wall = WallVariant(b.Id);
                if (wall != null) return wall;
            }
            return b;
        }

        private static BlockDef WallVariant(string id)
        {
            int i = id.LastIndexOf('_');
            string cand = i < 0 ? "wall_" + id : id.Substring(0, i + 1) + "wall_" + id.Substring(i + 1);
            var w = McData.Block(cand);
            if (w != null && (w.Class.Contains("Wall") || w.Class.Contains("Torch") || w.Class.Contains("Fan"))) return w;
            return null;
        }

        public static BState PlacementState(BlockDef b, PlaceCtx c)
        {
            var s = b.Default;
            var face = HorizontalFacing(c.Look);
            var frac = Frac(c, c.Cell);
            string cls = b.Class;

            if (b.HasProp("waterlogged")) s = s.With("waterlogged", "false");

            switch (cls)
            {
                case "SlabBlock":
                case "OxidizableSlabBlock":
                {
                    var existing = BlockWorld.Get(c.Cell);
                    if (existing != null && existing.Block == b && existing["type"] != "double") return existing.With("type", "double");
                    bool top = c.Side == Dir.Down || (c.Side != Dir.Up && Frac(c, c.Cell).y > 0.5f);
                    return s.With("type", top ? "top" : "bottom");
                }
                case "StairsBlock":
                case "OxidizableStairsBlock":
                {
                    bool top = c.Side == Dir.Down || (c.Side != Dir.Up && frac.y > 0.5f);
                    s = s.With("facing", N(face)).With("half", top ? "top" : "bottom");
                    return s.With("shape", StairShape(s, c.Cell));
                }
                case "PillarBlock":
                case "HayBlock":
                case "BoneBlock":
                case "BasaltBlock":
                    return s.With("axis", Axis(c.Side));
                case "TrapdoorBlock":
                {
                    if (IsHorizontal(c.Side)) s = s.With("facing", N(c.Side)).With("half", frac.y > 0.5f ? "top" : "bottom");
                    else s = s.With("facing", N(Opp(face))).With("half", c.Side == Dir.Up ? "bottom" : "top");
                    return s;
                }
                case "DoorBlock":
                {
                    if (BlockWorld.Has(c.Cell + Vector3Int.up)) return null;
                    s = s.With("facing", N(face)).With("half", "lower");
                    return s.With("hinge", DoorHinge(c, face));
                }
                case "BedBlock":
                {
                    var head = c.Cell + BlockWorld.Offset(face);
                    if (BlockWorld.Has(head)) return null;
                    return s.With("facing", N(face)).With("part", "foot");
                }
                case "TallPlantBlock":
                case "TallFlowerBlock":
                case "TallSeagrassBlock":
                    if (BlockWorld.Has(c.Cell + Vector3Int.up)) return null;
                    return s.With("half", "lower");
                case "LadderBlock":
                case "WallTorchBlock":
                case "WallRedstoneTorchBlock":
                case "WallSignBlock":
                case "WallBannerBlock":
                case "WallSkullBlock":
                case "WitherSkullWallBlock":
                case "CoralWallFanBlock":
                case "DeadCoralWallFanBlock":
                case "WallHangingSignBlock":
                    if (!IsHorizontal(c.Side)) return null;
                    return s.With("facing", N(c.Side));
                case "ButtonBlock":
                case "LeverBlock":
                case "GrindstoneBlock":
                {
                    if (c.Side == Dir.Up) return s.With("face", "floor").With("facing", N(face));
                    if (c.Side == Dir.Down) return s.With("face", "ceiling").With("facing", N(face));
                    return s.With("face", "wall").With("facing", N(c.Side));
                }
                case "SignBlock":
                case "BannerBlock":
                case "SkullBlock":
                case "WitherSkullBlock":
                case "PiglinHeadBlock":
                case "HangingSignBlock":
                {
                    float yaw = Mathf.Atan2(-c.Look.x, c.Look.z) * Mathf.Rad2Deg; // Minecraft yaw
                    int rot = Mathf.FloorToInt((yaw + 180f) * 16f / 360f + 0.5f) & 15;
                    return s.HasRot() ? s.With("rotation", rot.ToString()) : s;
                }
                case "AnvilBlock":
                    return s.With("facing", N(Cw(face)));
                case "HopperBlock":
                {
                    var f = Opp(c.Side);
                    return s.With("facing", f == Dir.Up ? "down" : N(f));
                }
                case "FenceGateBlock":
                    return s.With("facing", N(face));
                case "CandleBlock":
                case "SeaPickleBlock":
                {
                    var existing = BlockWorld.Get(c.Cell);
                    var key = cls == "CandleBlock" ? "candles" : "pickles";
                    if (existing != null && existing.Block == b) { int n = int.Parse(existing[key]); if (n < 4) return existing.With(key, (n + 1).ToString()); return null; }
                    return s;
                }
                case "SnowBlock":
                {
                    var existing = BlockWorld.Get(c.Cell);
                    if (existing != null && existing.Block == b) { int n = int.Parse(existing["layers"]); if (n < 8) return existing.With("layers", (n + 1).ToString()); return null; }
                    return s;
                }
                case "AmethystClusterBlock":
                case "EndRodBlock":
                case "LightningRodBlock":
                    return s.With("facing", N(c.Side));
            }

            if (s.Block.HasProp("facing"))
            {
                // FacingBlock (observer, piston, dispenser, barrel) looks at the player in 3D; HorizontalFacing blocks (furnace, chest...) face them.
                var vals = PropValues(b, "facing");
                bool vertical = System.Array.IndexOf(vals, "up") >= 0;
                var f = vertical ? Opp(Facing(c.Look)) : Opp(face);
                if (cls == "ObserverBlock") f = Facing(c.Look);
                s = s.With("facing", N(f));
            }
            if (s.Block.HasProp("axis") && s["axis"] != null) s = s.With("axis", Axis(c.Side));
            if (s.Block.HasProp("rotation")) { float yaw = Mathf.Atan2(-c.Look.x, c.Look.z) * Mathf.Rad2Deg; s = s.With("rotation", (Mathf.FloorToInt((yaw + 180f) * 16f / 360f + 0.5f) & 15).ToString()); }
            if (s.Block.HasProp("half") && PropValues(b, "half")[0] == "top") s = s.With("half", c.Side == Dir.Down || (c.Side != Dir.Up && frac.y > 0.5f) ? "top" : "bottom");
            return Connect(s, c.Cell);
        }

        private static bool HasRot(this BState s) => s.Block.HasProp("rotation");

        public static string[] PropValues(BlockDef b, string p)
        {
            foreach (var kv in b.Props) if (kv.Key == p) return kv.Value;
            return new string[0];
        }

        public static Dir Opp(Dir d) => BlockWorld.Opposite(d);

        private static string DoorHinge(PlaceCtx c, Dir face)
        {
            var left = Ccw(face);
            var frac = Frac(c, c.Cell);
            // MC: hinge on the side the player clicked towards, unless a neighbouring door says otherwise
            float off = face == Dir.North ? frac.x : face == Dir.South ? 1 - frac.x : face == Dir.East ? frac.z : 1 - frac.z;
            var leftDoor = BlockWorld.Get(c.Cell + BlockWorld.Offset(left));
            if (leftDoor != null && leftDoor.Block.Class == "DoorBlock" && leftDoor["hinge"] == "left") return "right";
            return off < 0.5f ? "left" : "right";
        }

        // ------------------------------------------------------------------------------------------------ connections

        public static BState Connect(BState s, Vector3Int cell)
        {
            switch (s.Block.Class)
            {
                case "FenceBlock":
                    foreach (var d in Horizontal) s = s.With(N(d), FenceConnects(s, cell, d) ? "true" : "false");
                    return s;
                case "PaneBlock":
                case "StainedGlassPaneBlock":
                    foreach (var d in Horizontal) s = s.With(N(d), PaneConnects(cell, d) ? "true" : "false");
                    return s;
                case "WallBlock":
                {
                    var above = BlockWorld.Get(cell + Vector3Int.up);
                    foreach (var d in Horizontal)
                    {
                        bool con = WallConnects(cell, d);
                        bool tall = above != null && (above.Block.Class == "WallBlock" ? above[N(d)] != "none" : above.FullCube);
                        s = s.With(N(d), con ? (tall ? "tall" : "low") : "none");
                    }
                    bool post = (above != null && above.Block.Class == "WallBlock" && above["up"] == "true")
                                || !(s["north"] != "none" && s["south"] != "none" && s["east"] == "none" && s["west"] == "none")
                                   && !(s["east"] != "none" && s["west"] != "none" && s["north"] == "none" && s["south"] == "none");
                    return s.With("up", post ? "true" : "false");
                }
                case "StairsBlock":
                case "OxidizableStairsBlock":
                    return s.With("shape", StairShape(s, cell));
                case "FenceGateBlock":
                {
                    var f = ParseDir(s["facing"]);
                    bool wall = IsWall(BlockWorld.Get(cell + BlockWorld.Offset(Cw(f)))) || IsWall(BlockWorld.Get(cell + BlockWorld.Offset(Ccw(f))));
                    return s.With("in_wall", wall ? "true" : "false");
                }
                case "GrassBlock":
                case "SnowyBlock":
                case "MyceliumBlock":
                {
                    var above = BlockWorld.Get(cell + Vector3Int.up);
                    return s.With("snowy", above != null && (above.Block.Id == "snow" || above.Block.Id == "snow_block") ? "true" : "false");
                }
            }
            return s;
        }

        private static bool IsWall(BState s) => s != null && s.Block.Class == "WallBlock";

        private static bool SolidFace(Vector3Int cell)
        {
            var n = BlockWorld.Get(cell);
            return n != null && n.FullCube && n.Block.Opaque;
        }

        private static bool FenceConnects(BState s, Vector3Int cell, Dir d)
        {
            var n = BlockWorld.Get(cell + BlockWorld.Offset(d));
            if (n == null) return false;
            if (n.Block.Tags.Contains("fences") && n.Block.Tags.Contains("wooden_fences") == s.Block.Tags.Contains("wooden_fences")) return true;
            if (n.Block.Class == "FenceGateBlock") { var f = ParseDir(n["facing"]); return Axis(f) != Axis(d); }
            return n.FullCube && n.Block.Opaque && !n.Block.Tags.Contains("leaves");
        }

        private static bool PaneConnects(Vector3Int cell, Dir d)
        {
            var n = BlockWorld.Get(cell + BlockWorld.Offset(d));
            if (n == null) return false;
            if (n.Block.Class == "PaneBlock" || n.Block.Class == "StainedGlassPaneBlock" || n.Block.Tags.Contains("walls")) return true;
            return n.FullCube && (n.Block.Opaque || n.Block.Class.Contains("Glass")) && !n.Block.Tags.Contains("leaves");
        }

        private static bool WallConnects(Vector3Int cell, Dir d)
        {
            var n = BlockWorld.Get(cell + BlockWorld.Offset(d));
            if (n == null) return false;
            if (n.Block.Tags.Contains("walls") || n.Block.Class == "PaneBlock" || n.Block.Class == "StainedGlassPaneBlock") return true;
            if (n.Block.Class == "FenceGateBlock") { var f = ParseDir(n["facing"]); return Axis(f) != Axis(d); }
            return n.FullCube && n.Block.Opaque;
        }

        private static bool IsStairs(BState s) => s != null && (s.Block.Class == "StairsBlock" || s.Block.Class == "OxidizableStairsBlock");

        /// <summary>StairsBlock.getStairShape, unchanged.</summary>
        private static string StairShape(BState s, Vector3Int cell)
        {
            var dir = ParseDir(s["facing"]);
            var front = BlockWorld.Get(cell + BlockWorld.Offset(dir));
            if (IsStairs(front) && s["half"] == front["half"])
            {
                var d2 = ParseDir(front["facing"]);
                if (Axis(d2) != Axis(dir) && DifferentOrientation(s, cell, Opp(d2)))
                    return d2 == Ccw(dir) ? "outer_left" : "outer_right";
            }
            var back = BlockWorld.Get(cell + BlockWorld.Offset(Opp(dir)));
            if (IsStairs(back) && s["half"] == back["half"])
            {
                var d3 = ParseDir(back["facing"]);
                if (Axis(d3) != Axis(dir) && DifferentOrientation(s, cell, d3))
                    return d3 == Ccw(dir) ? "inner_left" : "inner_right";
            }
            return "straight";
        }

        private static bool DifferentOrientation(BState s, Vector3Int cell, Dir d)
        {
            var n = BlockWorld.Get(cell + BlockWorld.Offset(d));
            return !IsStairs(n) || n["facing"] != s["facing"] || n["half"] != s["half"];
        }

        // ------------------------------------------------------------------------------------------------ updates

        /// <summary>After a change at `cell`: refresh connections around it, break what lost its support, start falling blocks.</summary>
        public static void NeighbourChanged(Vector3Int cell)
        {
            var queue = new List<Vector3Int> { cell };
            for (int d = 0; d < 6; d++) queue.Add(cell + BlockWorld.Offset((Dir)d));
            foreach (var c in queue)
            {
                var s = BlockWorld.Get(c);
                if (s == null) continue;
                var ns = Connect(s, c);
                if (ns != s) BlockWorld.Set(c, ns, false);
                if (!Supported(ns, c))
                {
                    Interaction.BreakBlock(c, null, true);
                    continue;
                }
                if (Falls(ns) && !SolidBelow(c)) FallingBlock.Begin(c, ns);
            }
        }

        public static bool Falls(BState s)
        {
            var cls = s.Block.Class;
            return cls == "SandBlock" || cls == "GravelBlock" || cls == "ConcretePowderBlock" || cls == "AnvilBlock" || cls == "DragonEggBlock" || cls == "SuspiciousSandBlock";
        }

        /// <summary>A Minecraft block or anything solid of Subnautica's (terrain, wrecks, bases) directly under the cell.</summary>
        public static bool SolidBelow(Vector3Int c) => SolidAt(c + Vector3Int.down, Dir.Up);

        /// <summary>Is there something to hold on to in cell `c` (whose face `face` touches the block we test)?</summary>
        public static bool SolidAt(Vector3Int c, Dir face)
        {
            var s = BlockWorld.Get(c);
            if (s != null) return s.Collision.Length > 0 && s.Block.Class != "LeavesBlock" || s.Block.Tags.Contains("leaves");
            // Subnautica geometry: probe from the block we test towards that cell
            var from = BlockWorld.Center(c - BlockWorld.Offset(BlockWorld.Opposite(face)));
            var dir = (BlockWorld.Center(c) - from).normalized;
            return Physics.Raycast(from, dir, out var hit, 0.75f, SnWorld.SolidMask, QueryTriggerInteraction.Ignore) && !BlockWorld.CellOf(hit.collider, out _);
        }

        public static bool Supported(BState s, Vector3Int c)
        {
            string cls = s.Block.Class;
            switch (cls)
            {
                case "TorchBlock": case "RedstoneTorchBlock": case "SignBlock": case "BannerBlock": case "PressurePlateBlock":
                case "WeightedPressurePlateBlock": case "RailBlock": case "PoweredRailBlock": case "DetectorRailBlock": case "CarpetBlock":
                case "DyedCarpetBlock": case "CandleBlock": case "RedstoneWireBlock": case "SnowBlock": case "RepeaterBlock": case "ComparatorBlock":
                case "FlowerPotBlock": case "BrewingStandBlock": case "CakeBlock":
                    return SolidBelow(c);
                case "FlowerBlock": case "SaplingBlock": case "FernBlock": case "MushroomPlantBlock": case "DeadBushBlock": case "WitherRoseBlock":
                case "SweetBerryBushBlock": case "AzaleaBlock": case "FungusBlock": case "RootsBlock": case "SeagrassBlock": case "CoralBlock":
                case "DeadCoralBlock": case "CoralFanBlock": case "DeadCoralFanBlock": case "SeaPickleBlock": case "CropBlock": case "BeetrootsBlock":
                case "CarrotsBlock": case "PotatoesBlock": case "KelpBlock": case "SugarCaneBlock": case "CactusBlock": case "BambooBlock":
                {
                    var below = BlockWorld.Get(c + Vector3Int.down);
                    if ((cls == "SugarCaneBlock" || cls == "CactusBlock" || cls == "BambooBlock" || cls == "KelpBlock") && below != null && below.Block == s.Block) return true;
                    return SolidBelow(c);
                }
                case "TallPlantBlock": case "TallFlowerBlock": case "TallSeagrassBlock": case "DoorBlock":
                {
                    if (s["half"] == "upper") { var below = BlockWorld.Get(c + Vector3Int.down); return below != null && below.Block == s.Block && below["half"] == "lower"; }
                    var up = BlockWorld.Get(c + Vector3Int.up);
                    if (up == null || up.Block != s.Block) return false;
                    return cls == "DoorBlock" || SolidBelow(c);
                }
                case "BedBlock":
                {
                    var f = ParseDir(s["facing"]);
                    var other = c + BlockWorld.Offset(s["part"] == "foot" ? f : Opp(f));
                    var o = BlockWorld.Get(other);
                    return o != null && o.Block == s.Block;
                }
                case "LadderBlock": case "WallTorchBlock": case "WallRedstoneTorchBlock": case "WallSignBlock": case "WallBannerBlock":
                case "CoralWallFanBlock": case "DeadCoralWallFanBlock": case "WallSkullBlock": case "WallHangingSignBlock":
                {
                    var f = ParseDir(s["facing"]);
                    return SolidAt(c - BlockWorld.Offset(f), f);
                }
                case "ButtonBlock": case "LeverBlock":
                {
                    var face = s["face"];
                    if (face == "floor") return SolidBelow(c);
                    if (face == "ceiling") return SolidAt(c + Vector3Int.up, Dir.Down);
                    var f = ParseDir(s["facing"]);
                    return SolidAt(c - BlockWorld.Offset(f), f);
                }
            }
            return true;
        }

        /// <summary>Places the second half of doors, beds and tall plants.</summary>
        public static void PlaceCompanion(Vector3Int c, BState s)
        {
            switch (s.Block.Class)
            {
                case "DoorBlock": case "TallPlantBlock": case "TallFlowerBlock": case "TallSeagrassBlock":
                    BlockWorld.Set(c + Vector3Int.up, s.With("half", "upper"), false);
                    break;
                case "BedBlock":
                    BlockWorld.Set(c + BlockWorld.Offset(ParseDir(s["facing"])), s.With("part", "head"), false);
                    break;
            }
        }

        // ------------------------------------------------------------------------------------------------ interaction

        /// <summary>Right click on a block (not sneaking). Returns true when the block did something.</summary>
        public static bool Use(Vector3Int c, BState s)
        {
            string cls = s.Block.Class;
            switch (cls)
            {
                case "DoorBlock":
                {
                    if (s.Block.Id == "iron_door") return false;
                    bool open = s["open"] != "true";
                    var other = c + (s["half"] == "lower" ? Vector3Int.up : Vector3Int.down);
                    BlockWorld.Set(c, s.With("open", open ? "true" : "false"), false);
                    var o = BlockWorld.Get(other);
                    if (o != null && o.Block == s.Block) BlockWorld.Set(other, o.With("open", open ? "true" : "false"), false);
                    Sounds.Play(Woodish(s) ? (open ? "block.wooden_door.open" : "block.wooden_door.close") : (open ? "block.iron_door.open" : "block.iron_door.close"), BlockWorld.Center(c));
                    return true;
                }
                case "TrapdoorBlock":
                {
                    if (s.Block.Id == "iron_trapdoor") return false;
                    bool open = s["open"] != "true";
                    BlockWorld.Set(c, s.With("open", open ? "true" : "false"), false);
                    Sounds.Play(open ? "block.wooden_trapdoor.open" : "block.wooden_trapdoor.close", BlockWorld.Center(c));
                    return true;
                }
                case "FenceGateBlock":
                {
                    bool open = s["open"] != "true";
                    var ns = s.With("open", open ? "true" : "false");
                    if (open)
                    {
                        // a gate opens away from the player
                        var look = HorizontalFacing(McLook(MainCamera.camera.transform.forward));
                        if (Opp(ParseDir(s["facing"])) == look) ns = ns.With("facing", N(look));
                    }
                    BlockWorld.Set(c, ns, false);
                    Sounds.Play(open ? "block.fence_gate.open" : "block.fence_gate.close", BlockWorld.Center(c));
                    return true;
                }
                case "LeverBlock":
                {
                    bool on = s["powered"] != "true";
                    BlockWorld.Set(c, s.With("powered", on ? "true" : "false"), false);
                    Sounds.Play("block.lever.click", BlockWorld.Center(c), 0.3f, on ? 0.6f : 0.5f);
                    return true;
                }
                case "ButtonBlock":
                {
                    if (s["powered"] == "true") return true;
                    BlockWorld.Set(c, s.With("powered", "true"), false);
                    bool wood = Woodish(s);
                    Sounds.Play(wood ? "block.wooden_button.click_on" : "block.stone_button.click_on", BlockWorld.Center(c));
                    Game.Later(wood ? 1.5f : 1f, () =>
                    {
                        var now = BlockWorld.Get(c);
                        if (now != null && now.Block == s.Block) { BlockWorld.Set(c, now.With("powered", "false"), false); Sounds.Play(wood ? "block.wooden_button.click_off" : "block.stone_button.click_off", BlockWorld.Center(c)); }
                    });
                    return true;
                }
                case "CakeBlock":
                {
                    if (!Hunger.CanEat(false) && !GameMode.Creative) return false;
                    Hunger.Eat(2, 0.1f);
                    Sounds.Play("entity.generic.eat", BlockWorld.Center(c));
                    int bites = int.Parse(s["bites"]) + 1;
                    if (bites >= 7) BlockWorld.Remove(c);
                    else BlockWorld.Set(c, s.With("bites", bites.ToString()), false);
                    return true;
                }
                case "NoteBlock":
                {
                    int note = (int.Parse(s["note"]) + 1) % 25;
                    BlockWorld.Set(c, s.With("note", note.ToString()), false);
                    Sounds.Play("block.note_block.harp", BlockWorld.Center(c), 3f, Mathf.Pow(2f, (note - 12) / 12f));
                    return true;
                }
                case "RepeaterBlock":
                {
                    int delay = int.Parse(s["delay"]) % 4 + 1;
                    BlockWorld.Set(c, s.With("delay", delay.ToString()), false);
                    return true;
                }
                case "BedBlock":
                    return Sleep.Try(c, s);
            }
            return Screens.OpenFor(c, s);
        }

        private static bool Woodish(BState s) => s.Block.Sound == "wood" || s.Block.Sound == "nether_wood" || s.Block.Sound == "bamboo_wood" || s.Block.Sound == "cherry_wood";
    }
}
