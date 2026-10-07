using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft redstone on the 20 Hz tick. Every block change marks the cells around it; each tick the marked cells
    /// are re-evaluated: dust networks recompute their power (15 at a source, one less per step), torches invert the
    /// block they hang on, repeaters and comparators pass the signal on after their delay, and lamps, doors, trapdoors,
    /// gates, pistons, note blocks, dispensers, droppers and TNT react to it. Pressure plates feel the player, Minecraft
    /// mobs, dropped items and Slime Rancher's slimes, the daylight sensor reads the Far Far Range clock, and a
    /// dispenser loaded with slimes fires them out like a vacpack.
    /// Power is Minecraft's: a component powers its neighbours, a conductor block is powered "strongly" by a component
    /// pointing into it (and then powers its own neighbours, dust included) or "weakly" by dust (and then powers
    /// everything around it except dust).
    /// </summary>
    public static class Redstone
    {
        private static readonly Dir[] All6 = { Dir.Down, Dir.Up, Dir.North, Dir.South, Dir.West, Dir.East };
        private static readonly Dir[] Horiz = BlockRules.Horizontal;
        private const int ActorMask = (1 << 9) | (1 << 11) | (1 << 15) | (1 << 16) | (1 << 21);

        private static long tick;
        private static HashSet<Vector3Int> dirty = new HashSet<Vector3Int>();
        private static readonly SortedDictionary<long, List<Vector3Int>> scheduled = new SortedDictionary<long, List<Vector3Int>>();
        private static readonly HashSet<Vector3Int> pending = new HashSet<Vector3Int>();
        private static readonly HashSet<Vector3Int> sensors = new HashSet<Vector3Int>();      // pressure plates and daylight sensors
        private static readonly Dictionary<Vector3Int, int> comparatorOut = new Dictionary<Vector3Int, int>();
        private static readonly Dictionary<Vector3Int, long> plateSince = new Dictionary<Vector3Int, long>();
        private static readonly HashSet<Vector3Int> observerFire = new HashSet<Vector3Int>();
        private static bool busy;

        // ------------------------------------------------------------------------------------------------ hooks

        /// <summary>BlockWorld.Set calls this for every change (placing, breaking, loading, redstone itself).</summary>
        public static void OnSet(Vector3Int c, BState old, BState now)
        {
            if (now != null && IsSensor(now)) sensors.Add(c); else sensors.Remove(c);
            if (now == null || now.Block.Class != "ComparatorBlock") comparatorOut.Remove(c);
            Mark(c);
            if (!busy) PistonBroken(c, old, now);
            // observers looking at this cell see the change
            foreach (var d in All6)
            {
                var o = Step(c, d);
                var os = BlockWorld.Get(o);
                if (os != null && os.Block.Class == "ObserverBlock" && os["facing"] == BlockWorld.Name(BlockWorld.Opposite(d)) && os["powered"] != "true" && observerFire.Add(o))
                    Schedule(o, 2);
            }
        }

        /// <summary>Forget everything when the world is cleared or reloaded.</summary>
        public static void Clear()
        {
            dirty.Clear(); scheduled.Clear(); pending.Clear(); sensors.Clear(); comparatorOut.Clear(); plateSince.Clear(); observerFire.Clear();
        }

        private static bool IsSensor(BState s)
        {
            var cls = s.Block.Class;
            return cls == "PressurePlateBlock" || cls == "WeightedPressurePlateBlock" || cls == "DaylightDetectorBlock";
        }

        /// <summary>Marks a cell and everything within two steps: what a change can power directly or through a block.</summary>
        private static void Mark(Vector3Int c)
        {
            for (int x = -2; x <= 2; x++)
                for (int y = -2; y <= 2; y++)
                    for (int z = -2; z <= 2; z++)
                        if (Mathf.Abs(x) + Mathf.Abs(y) + Mathf.Abs(z) <= 2) dirty.Add(new Vector3Int(c.x + x, c.y + y, c.z + z));
        }

        private static void Schedule(Vector3Int c, int ticks)
        {
            if (!pending.Add(c)) return;
            long at = tick + Mathf.Max(1, ticks);
            if (!scheduled.TryGetValue(at, out var l)) scheduled[at] = l = new List<Vector3Int>();
            l.Add(c);
        }

        // ------------------------------------------------------------------------------------------------ tick

        public static void Tick()
        {
            tick++;
            if (tick % 2 == 0) Sensors();

            // scheduled updates that are due
            while (scheduled.Count > 0)
            {
                long first = -1;
                foreach (var k in scheduled.Keys) { first = k; break; }
                if (first > tick) break;
                var list = scheduled[first];
                scheduled.Remove(first);
                foreach (var c in list) { pending.Remove(c); Due(c); }
            }

            // marked cells; a change marks more cells, handled in the same tick (bounded, so a bad loop can't freeze the game)
            int budget = 20000;
            var done = new HashSet<Vector3Int>();
            while (dirty.Count > 0 && budget > 0)
            {
                var now = dirty;
                dirty = new HashSet<Vector3Int>();
                foreach (var c in now)
                {
                    if (--budget <= 0) { dirty.Add(c); continue; }
                    Evaluate(c, done);
                }
                done.Clear();
            }
        }

        /// <summary>A delayed change is due: torches, repeaters, comparators, observers, lamps turning off.</summary>
        private static void Due(Vector3Int c)
        {
            var s = BlockWorld.Get(c);
            if (s == null) { observerFire.Remove(c); return; }
            switch (s.Block.Class)
            {
                case "RedstoneTorchBlock":
                case "WallRedstoneTorchBlock":
                {
                    bool lit = !TorchInput(c, s);
                    if ((s["lit"] == "true") != lit) Put(c, s.With("lit", B(lit)));
                    break;
                }
                case "RepeaterBlock":
                {
                    if (Locked(c, s)) break;
                    bool on = RearInput(c, s) > 0;
                    if ((s["powered"] == "true") != on)
                    {
                        Put(c, s.With("powered", B(on)));
                        // a pulse shorter than the delay still comes out whole: look again after the delay
                        Schedule(c, Delay(s));
                    }
                    break;
                }
                case "ComparatorBlock":
                {
                    int o = ComparatorCompute(c, s);
                    comparatorOut.TryGetValue(c, out int was);
                    if (o != was || (s["powered"] == "true") != (o > 0))
                    {
                        comparatorOut[c] = o;
                        Put(c, s.With("powered", B(o > 0)));
                        Mark(c);
                    }
                    break;
                }
                case "ObserverBlock":
                    if (observerFire.Remove(c))
                    {
                        Put(c, s.With("powered", "true"));
                        Schedule(c, 2);
                    }
                    else if (s["powered"] == "true") Put(c, s.With("powered", "false"));
                    break;
                case "RedstoneLampBlock":
                    if (s["lit"] == "true" && Input(c) == 0) Put(c, s.With("lit", "false"));
                    break;
                default:
                    Evaluate(c, null);
                    break;
            }
        }

        /// <summary>Brings the component at `c` in line with the power around it.</summary>
        private static void Evaluate(Vector3Int c, HashSet<Vector3Int> done)
        {
            var s = BlockWorld.Get(c);
            if (s == null) return;
            switch (s.Block.Class)
            {
                case "RedStoneWireBlock":
                case "RedstoneWireBlock":
                    if (done == null || !done.Contains(c)) Wire(c, done ?? new HashSet<Vector3Int>());
                    break;
                case "RedstoneTorchBlock":
                case "WallRedstoneTorchBlock":
                    if ((s["lit"] == "true") == TorchInput(c, s)) Schedule(c, 2);
                    break;
                case "RepeaterBlock":
                {
                    bool locked = Locked(c, s);
                    if (s.Block.HasProp("locked") && (s["locked"] == "true") != locked) { Put(c, s.With("locked", B(locked))); s = BlockWorld.Get(c); }
                    if (!locked && (s["powered"] == "true") != (RearInput(c, s) > 0)) Schedule(c, Delay(s));
                    break;
                }
                case "ComparatorBlock":
                {
                    comparatorOut.TryGetValue(c, out int was);
                    if (ComparatorCompute(c, s) != was || (s["powered"] == "true") != (was > 0)) Schedule(c, 2);
                    break;
                }
                case "RedstoneLampBlock":
                {
                    bool on = Input(c) > 0;
                    if (on && s["lit"] != "true") Put(c, s.With("lit", "true"));
                    else if (!on && s["lit"] == "true") Schedule(c, 4);
                    break;
                }
                case "DoorBlock":
                {
                    var other = c + (s["half"] == "lower" ? Vector3Int.up : Vector3Int.down);
                    var o = BlockWorld.Get(other);
                    bool on = Input(c) > 0 || (o != null && o.Block == s.Block && Input(other) > 0);
                    if ((s["powered"] == "true") == on) break;
                    bool wasOpen = s["open"] == "true";
                    Put(c, s.With("powered", B(on)).With("open", B(on)));
                    if (o != null && o.Block == s.Block) Put(other, o.With("powered", B(on)).With("open", B(on)));
                    if (wasOpen != on) Sounds.Play(Wood(s) ? (on ? "block.wooden_door.open" : "block.wooden_door.close") : (on ? "block.iron_door.open" : "block.iron_door.close"), BlockWorld.Center(c));
                    break;
                }
                case "TrapdoorBlock":
                case "FenceGateBlock":
                {
                    bool on = Input(c) > 0;
                    if ((s["powered"] == "true") == on) break;
                    bool wasOpen = s["open"] == "true";
                    Put(c, s.With("powered", B(on)).With("open", B(on)));
                    if (wasOpen != on)
                    {
                        string snd = s.Block.Class == "FenceGateBlock" ? "block.fence_gate." : s.Block.Id == "iron_trapdoor" ? "block.iron_trapdoor." : "block.wooden_trapdoor.";
                        Sounds.Play(snd + (on ? "open" : "close"), BlockWorld.Center(c));
                    }
                    break;
                }
                case "PistonBlock":
                {
                    var f = F(s);
                    bool on = Input(c, f) > 0;
                    if (on && s["extended"] != "true") Extend(c, s, f);
                    else if (!on && s["extended"] == "true") Retract(c, s, f);
                    break;
                }
                case "TntBlock":
                    if (Input(c) > 0) { BlockWorld.Remove(c); Projectiles.Tnt(BlockWorld.Center(c)); }
                    break;
                case "NoteBlock":
                {
                    bool on = Input(c) > 0;
                    if ((s["powered"] == "true") == on) break;
                    Put(c, s.With("powered", B(on)));
                    if (on) PlayNote(c, s);
                    break;
                }
                case "DispenserBlock":
                case "DropperBlock":
                {
                    bool on = Input(c) > 0;
                    if ((s["triggered"] == "true") == on) break;
                    Put(c, s.With("triggered", B(on)));
                    if (on) Dispense(c, s);
                    break;
                }
                case "PoweredRailBlock":
                {
                    bool on = Input(c) > 0;
                    if ((s["powered"] == "true") != on) Put(c, s.With("powered", B(on)));
                    break;
                }
            }
        }

        private static void Put(Vector3Int c, BState s)
        {
            bool was = busy;
            busy = true;
            try { BlockWorld.Set(c, s, false); }
            finally { busy = was; }
        }

        private static string B(bool b) => b ? "true" : "false";
        private static bool Wood(BState s) => s.Block.Sound == "wood" || s.Block.Sound == "nether_wood" || s.Block.Sound == "bamboo_wood" || s.Block.Sound == "cherry_wood";
        private static Dir F(BState s) => BlockRules.ParseDir(s["facing"]);
        private static Vector3Int Step(Vector3Int c, Dir d) => c + BlockWorld.Offset(d);
        private static int Int(string v) => int.TryParse(v, out int i) ? i : 0;

        // ------------------------------------------------------------------------------------------------ power

        private static bool IsWire(BState s) => s != null && (s.Block.Class == "RedStoneWireBlock" || s.Block.Class == "RedstoneWireBlock");

        /// <summary>A block that carries power through itself: a full opaque cube (not glass, leaves, slime, a component...).</summary>
        public static bool Conductor(BState s)
        {
            if (s == null || !s.FullCube || !s.Block.Opaque) return false;
            var cls = s.Block.Class;
            if (cls.Contains("Glass") || cls == "LeavesBlock" || cls == "SlimeBlock" || cls == "HoneyBlock" || cls == "IceBlock" || cls == "BarrierBlock") return false;
            if (IsSource(s) || cls == "PistonBlock" || cls == "ObserverBlock") return false;
            return true;
        }

        /// <summary>Does this block emit power on its own (rather than carry it)?</summary>
        private static bool IsSource(BState s)
        {
            switch (s.Block.Class)
            {
                case "LeverBlock": case "ButtonBlock": case "RedstoneTorchBlock": case "WallRedstoneTorchBlock":
                case "RepeaterBlock": case "ComparatorBlock": case "PressurePlateBlock": case "WeightedPressurePlateBlock":
                case "DaylightDetectorBlock": case "ObserverBlock": case "TargetBlock":
                    return true;
                default:
                    return s.Block.Id == "redstone_block";
            }
        }

        /// <summary>
        /// Power the component in `n` sends to its neighbour in direction `d`; `strong` says whether it powers a conductor
        /// there strongly (that block then powers dust too).
        /// </summary>
        private static int Emit(Vector3Int n, BState s, Dir d, out bool strong)
        {
            strong = false;
            switch (s.Block.Class)
            {
                case "LeverBlock":
                case "ButtonBlock":
                    if (s["powered"] != "true") return 0;
                    strong = d == Attached(s);
                    return 15;
                case "RedstoneTorchBlock":
                case "WallRedstoneTorchBlock":
                    if (s["lit"] != "true" || d == TorchBase(s)) return 0;
                    strong = d == Dir.Up;
                    return 15;
                case "RepeaterBlock":
                    if (s["powered"] != "true" || d != BlockWorld.Opposite(F(s))) return 0;
                    strong = true;
                    return 15;
                case "ComparatorBlock":
                    if (d != BlockWorld.Opposite(F(s))) return 0;
                    strong = true;
                    return ComparatorOutput(n);
                case "ObserverBlock":
                    if (s["powered"] != "true" || d != BlockWorld.Opposite(F(s))) return 0;
                    strong = true;
                    return 15;
                case "PressurePlateBlock":
                    if (s["powered"] != "true") return 0;
                    strong = d == Dir.Down;
                    return 15;
                case "WeightedPressurePlateBlock":
                    strong = d == Dir.Down;
                    return Int(s["power"]);
                case "DaylightDetectorBlock":
                case "TargetBlock":
                    return Int(s["power"]);
                case "RedStoneWireBlock":
                case "RedstoneWireBlock":
                {
                    int p = Int(s["power"]);
                    if (p == 0 || d == Dir.Up) return 0;
                    if (d == Dir.Down) return p;
                    return WirePoints(s, d) ? p : 0;
                }
            }
            return s.Block.Id == "redstone_block" ? 15 : 0;
        }

        /// <summary>Where a lever or button hangs.</summary>
        private static Dir Attached(BState s)
        {
            var face = s["face"];
            if (face == "floor") return Dir.Down;
            if (face == "ceiling") return Dir.Up;
            return BlockWorld.Opposite(F(s));
        }

        private static Dir TorchBase(BState s) => s.Block.Class == "WallRedstoneTorchBlock" ? BlockWorld.Opposite(F(s)) : Dir.Down;

        /// <summary>
        /// Power a conductor at `b` passes on. Strong power (a torch under it, a repeater into it, a lever on it) reaches
        /// everything; dust pointing into it only reaches components that are not dust (`forWire` false).
        /// </summary>
        private static int BlockPower(Vector3Int b, bool forWire)
        {
            int best = 0;
            foreach (var d in All6)
            {
                var n = Step(b, d);
                var ns = BlockWorld.Get(n);
                if (ns == null || Conductor(ns)) continue;
                int p = Emit(n, ns, BlockWorld.Opposite(d), out bool strong);
                if (p == 0) continue;
                if (IsWire(ns)) { if (forWire) continue; }
                else if (!strong) continue;
                if (p > best) best = p;
                if (best >= 15) return 15;
            }
            return best;
        }

        /// <summary>Power arriving at the component in `c` from all sides (except `skip`).</summary>
        public static int Input(Vector3Int c, Dir skip = Dir.None)
        {
            int best = 0;
            foreach (var d in All6)
            {
                if (d == skip) continue;
                best = Mathf.Max(best, InputFrom(c, d, false));
                if (best >= 15) return 15;
            }
            return best;
        }

        /// <summary>Power coming into `c` from its neighbour in direction `d`.</summary>
        private static int InputFrom(Vector3Int c, Dir d, bool forWire)
        {
            var n = Step(c, d);
            var ns = BlockWorld.Get(n);
            if (ns == null) return 0;
            if (Conductor(ns)) return BlockPower(n, forWire);
            if (forWire && IsWire(ns)) return 0;
            return Emit(n, ns, BlockWorld.Opposite(d), out _);
        }

        private static bool TorchInput(Vector3Int c, BState s)
        {
            var b = Step(c, TorchBase(s));
            var bs = BlockWorld.Get(b);
            return Conductor(bs) && BlockPower(b, false) > 0;
        }

        private static int RearInput(Vector3Int c, BState s)
        {
            var f = F(s);
            int p = InputFrom(c, f, false);
            if (p == 0)
            {
                // dust behind a repeater powers it even when the dust does not point at it
                var w = BlockWorld.Get(Step(c, f));
                if (IsWire(w)) p = Int(w["power"]);
            }
            return p;
        }

        private static int Delay(BState s) => 2 * Mathf.Max(1, Int(s["delay"]));

        /// <summary>A repeater is locked by a powered repeater or comparator pointing into its side.</summary>
        private static bool Locked(Vector3Int c, BState s)
        {
            var f = F(s);
            foreach (var side in new[] { BlockRules.Ccw(f), BlockRules.Cw(f) })
            {
                var n = Step(c, side);
                var ns = BlockWorld.Get(n);
                if (ns == null) continue;
                if ((ns.Block.Class == "RepeaterBlock" || ns.Block.Class == "ComparatorBlock") && F(ns) == side && Emit(n, ns, BlockWorld.Opposite(side), out _) > 0) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------------ comparator

        private static int ComparatorCompute(Vector3Int c, BState s)
        {
            var f = F(s);
            var back = Step(c, f);
            int rear = RearInput(c, s);
            int measured = Measure(back);
            if (measured < 0 && Conductor(BlockWorld.Get(back))) measured = Measure(Step(back, f));
            if (measured >= 0) rear = Mathf.Max(rear, measured);
            int side = 0;
            foreach (var sd in new[] { BlockRules.Ccw(f), BlockRules.Cw(f) })
            {
                var n = Step(c, sd);
                var ns = BlockWorld.Get(n);
                if (ns == null) continue;
                if (IsWire(ns)) side = Mathf.Max(side, Int(ns["power"]));
                else if (ns.Block.Id == "redstone_block") side = 15;
                else if (ns.Block.Class == "RepeaterBlock" || ns.Block.Class == "ComparatorBlock") side = Mathf.Max(side, Emit(n, ns, BlockWorld.Opposite(sd), out _));
            }
            if (s["mode"] == "subtract") return Mathf.Max(0, rear - side);
            return rear >= side ? rear : 0;
        }

        /// <summary>Comparator reading of a container (or cake, composter...); -1 when the block can't be measured.</summary>
        private static int Measure(Vector3Int b)
        {
            var s = BlockWorld.Get(b);
            if (s == null) return -1;
            switch (s.Block.Class)
            {
                case "CakeBlock": return (7 - Int(s["bites"])) * 2;
                case "ComposterBlock": return Int(s["level"]);
            }
            if (BlockWorld.ContainerSize(s) <= 0) return -1;
            var e = BlockWorld.Entity(b);
            if (e?.Items == null || e.Items.Length == 0) return 0;
            float fill = 0; bool any = false;
            foreach (var it in e.Items)
                if (it != null && it.Count > 0) { fill += it.Count / (float)Mathf.Max(1, it.MaxStack); any = true; }
            return any ? Mathf.FloorToInt(1 + fill / e.Items.Length * 14f) : 0;
        }

        public static int ComparatorOutput(Vector3Int c) => comparatorOut.TryGetValue(c, out int o) ? o : 0;

        // ------------------------------------------------------------------------------------------------ dust

        /// <summary>Dust cells joined to `c`: beside it, one up (if nothing solid above `c`), one down (if the side cell is open).</summary>
        private static IEnumerable<Vector3Int> WireLinks(Vector3Int c)
        {
            bool roof = Conductor(BlockWorld.Get(c + Vector3Int.up));
            foreach (var d in Horiz)
            {
                var n = Step(c, d);
                var ns = BlockWorld.Get(n);
                if (IsWire(ns)) yield return n;
                bool solid = Conductor(ns);
                if (!roof && solid && IsWire(BlockWorld.Get(n + Vector3Int.up))) yield return n + Vector3Int.up;
                if (!solid && IsWire(BlockWorld.Get(n + Vector3Int.down))) yield return n + Vector3Int.down;
            }
        }

        /// <summary>Recomputes a whole dust network at once: outside inputs, then one less per step (a flood fill by level).</summary>
        private static void Wire(Vector3Int start, HashSet<Vector3Int> done)
        {
            var net = new List<Vector3Int>();
            var seen = new HashSet<Vector3Int> { start };
            var q = new Queue<Vector3Int>();
            q.Enqueue(start);
            while (q.Count > 0 && net.Count < 4096)
            {
                var c = q.Dequeue();
                net.Add(c);
                foreach (var n in WireLinks(c)) if (seen.Add(n)) q.Enqueue(n);
            }
            foreach (var c in net) done.Add(c);

            var power = new Dictionary<Vector3Int, int>();
            var buckets = new List<Vector3Int>[16];
            for (int i = 0; i < 16; i++) buckets[i] = new List<Vector3Int>();
            foreach (var c in net)
            {
                int p = 0;
                foreach (var d in All6) p = Mathf.Max(p, InputFrom(c, d, true));
                power[c] = p;
                if (p > 0) buckets[p].Add(c);
            }
            for (int lvl = 15; lvl > 1; lvl--)
                foreach (var c in buckets[lvl])
                {
                    if (power[c] != lvl) continue;
                    foreach (var n in WireLinks(c))
                        if (power.TryGetValue(n, out int np) && np < lvl - 1) { power[n] = lvl - 1; buckets[lvl - 1].Add(n); }
                }
            foreach (var c in net)
            {
                var s = BlockWorld.Get(c);
                var shaped = Shape(c, s).With("power", power[c].ToString());
                if (shaped != s) Put(c, shaped);
            }
        }

        /// <summary>Does this dust point towards `d` (so it powers that side)?</summary>
        private static bool WirePoints(BState s, Dir d)
        {
            var v = s[BlockWorld.Name(d)];
            return v == null || v != "none";
        }

        /// <summary>The dust's shape: side/up/none on each side, a straight line when one side connects, a cross when none does.</summary>
        public static BState Shape(Vector3Int c, BState s)
        {
            if (!s.Block.HasProp("north")) return s;
            bool roof = Conductor(BlockWorld.Get(c + Vector3Int.up));
            var con = new Dictionary<Dir, string>();
            int count = 0;
            foreach (var d in Horiz)
            {
                var n = Step(c, d);
                var ns = BlockWorld.Get(n);
                bool solid = Conductor(ns);
                string v = "none";
                if (!roof && solid && IsWire(BlockWorld.Get(n + Vector3Int.up))) v = "up";
                else if (ns != null && ConnectsTo(ns, d)) v = "side";
                else if (!solid && IsWire(BlockWorld.Get(n + Vector3Int.down))) v = "side";
                con[d] = v;
                if (v != "none") count++;
            }
            if (count == 0) foreach (var d in Horiz) con[d] = "side";
            else if (count == 1)
                foreach (var d in Horiz)
                    if (con[d] != "none") { var o = BlockWorld.Opposite(d); if (con[o] == "none") con[o] = "side"; break; }
            foreach (var d in Horiz) s = s.With(BlockWorld.Name(d), con[d]);
            return s;
        }

        /// <summary>Does dust reach out to this neighbour (lying in direction `d` from the dust)?</summary>
        private static bool ConnectsTo(BState n, Dir d)
        {
            switch (n.Block.Class)
            {
                case "RedStoneWireBlock": case "RedstoneWireBlock":
                    return true;
                case "RepeaterBlock":
                {
                    var f = F(n);
                    return f == d || f == BlockWorld.Opposite(d);
                }
                case "ObserverBlock":
                    return F(n) == d;
                case "LeverBlock": case "ButtonBlock": case "RedstoneTorchBlock": case "WallRedstoneTorchBlock": case "ComparatorBlock":
                case "PressurePlateBlock": case "WeightedPressurePlateBlock": case "DaylightDetectorBlock": case "TargetBlock":
                case "TrappedChestBlock": case "DetectorRailBlock": case "TripwireHookBlock": case "LecternBlock": case "SculkSensorBlock":
                    return true;
            }
            return n.Block.Id == "redstone_block";
        }

        // ------------------------------------------------------------------------------------------------ pistons

        private static bool Immovable(BState s)
        {
            if (s == null) return false;
            var id = s.Block.Id;
            var cls = s.Block.Class;
            if (id == "obsidian" || id == "crying_obsidian" || id == "bedrock" || id == "barrier" || id == "end_portal_frame" || id == "respawn_anchor" || id == "reinforced_deepslate") return true;
            if (cls == "PistonBlock" && s["extended"] == "true") return true;
            if (cls == "PistonHeadBlock" || cls == "MovingPistonBlock") return true;
            if (BlockWorld.ContainerSize(s) > 0) return true;   // chests, furnaces, shulker boxes: Minecraft won't move block entities
            return cls == "EnchantmentTableBlock" || cls == "BeaconBlock" || cls == "EnderChestBlock" || cls.Contains("SignBlock") || cls.Contains("BannerBlock");
        }

        /// <summary>Blocks a piston breaks instead of pushing: plants, torches, dust, anything without collision or that hangs on something.</summary>
        private static bool Breaks(BState s)
        {
            if (s.Collision.Length == 0) return true;
            var cls = s.Block.Class;
            return cls.Contains("Torch") || IsWire(s) || cls == "RepeaterBlock" || cls == "ComparatorBlock" || cls == "DoorBlock" || cls == "BedBlock"
                   || cls == "LeverBlock" || cls == "ButtonBlock" || cls.Contains("PressurePlate") || cls == "CakeBlock" || cls == "LadderBlock"
                   || cls.Contains("Carpet") || cls == "FlowerPotBlock" || cls.Contains("Coral") || cls == "CactusBlock" || cls == "SugarCaneBlock" || cls == "BambooBlock";
        }

        /// <summary>Slime Rancher's own ground or buildings in a cell (a piston can't push the Far Far Range).</summary>
        private static bool SrSolid(Vector3Int c)
        {
            foreach (var col in Physics.OverlapBox(BlockWorld.Center(c), Vector3.one * 0.45f, Quaternion.identity, SrWorld.SolidMask, QueryTriggerInteraction.Ignore))
            {
                if (BlockWorld.CellOf(col, out _) || SrWorld.ActorOf(col) != null) continue;
                if (SrWorld.Player != null && col.transform.IsChildOf(SrWorld.Player.transform)) continue;
                return true;
            }
            return false;
        }

        private static void Extend(Vector3Int c, BState s, Dir f)
        {
            var off = BlockWorld.Offset(f);
            var line = new List<Vector3Int>();
            var p = c + off;
            Vector3Int? breakAt = null;
            while (true)
            {
                var ps = BlockWorld.Get(p);
                if (ps == null) { if (SrSolid(p)) return; break; }
                if (Breaks(ps)) { breakAt = p; break; }
                if (Immovable(ps)) return;
                line.Add(p);
                if (line.Count > 12) return;
                p += off;
            }
            if (breakAt.HasValue) Interaction.BreakBlock(breakAt.Value, null, true);
            bool was = busy;
            busy = true;
            try
            {
                for (int i = line.Count - 1; i >= 0; i--) BlockWorld.Set(line[i] + off, BlockWorld.Get(line[i]), false);
                var head = McData.Block("piston_head")?.Default;
                if (head != null)
                {
                    head = head.With("facing", BlockWorld.Name(f));
                    if (head.Block.HasProp("type")) head = head.With("type", s.Block.Id == "sticky_piston" ? "sticky" : "normal");
                    if (head.Block.HasProp("short")) head = head.With("short", "false");
                    BlockWorld.Set(c + off, head, false);
                }
                else BlockWorld.Remove(c + off, false);
                BlockWorld.Set(c, s.With("extended", "true"), false);
            }
            finally { busy = was; }
            foreach (var m in line) BlockRules.NeighbourChanged(m + off);
            BlockRules.NeighbourChanged(c + off);
            PushThings(c + off, line.Count, f);
            Sounds.Play("block.piston.extend", BlockWorld.Center(c), 0.5f, Random.value * 0.25f + 0.6f);
        }

        /// <summary>The player, slimes, mobs and items in the cells the piston just filled get shoved along.</summary>
        private static void PushThings(Vector3Int first, int moved, Dir f)
        {
            Vector3 dirU = BlockWorld.Offset(f);
            for (int i = 0; i <= moved; i++)
            {
                var b = new Bounds(BlockWorld.Center(first + BlockWorld.Offset(f) * i), Vector3.one);
                var feet = SrWorld.Feet;
                if (b.Contains(feet + Vector3.up * 0.1f) || b.Contains(feet + Vector3.up * 1.2f))
                    Motor.Vel += dirU * 0.6f + (f == Dir.Up ? Vector3.up * 0.2f : Vector3.zero);
                // slimes and anything else of Slime Rancher with a rigidbody
                foreach (var col in Physics.OverlapBox(b.center, b.extents, Quaternion.identity, ActorMask, QueryTriggerInteraction.Ignore))
                {
                    var rb = col.attachedRigidbody;
                    if (rb != null && !rb.isKinematic) { rb.position += dirU * 1.01f; rb.velocity += dirU * 6f; }
                }
                foreach (var m in Mobs.All) if (m != null && b.Intersects(m.Box)) { m.transform.position += dirU; m.Vel += dirU * 0.3f; }
                foreach (var it in ItemEntity.All) if (it != null && b.Contains(it.transform.position)) { it.transform.position += dirU; it.Vel += dirU * 0.2f; }
            }
        }

        private static void Retract(Vector3Int c, BState s, Dir f)
        {
            var off = BlockWorld.Offset(f);
            var hc = c + off;
            var pc = hc + off;
            bool pulled = false;
            bool was = busy;
            busy = true;
            try
            {
                var h = BlockWorld.Get(hc);
                if (h != null && h.Block.Class == "PistonHeadBlock") BlockWorld.Remove(hc, false);
                BlockWorld.Set(c, s.With("extended", "false"), false);
                if (s.Block.Id == "sticky_piston")
                {
                    var ps = BlockWorld.Get(pc);
                    if (ps != null && !Immovable(ps) && !Breaks(ps) && BlockWorld.Get(hc) == null)
                    {
                        BlockWorld.Remove(pc, false);
                        BlockWorld.Set(hc, ps, false);
                        pulled = true;
                    }
                }
            }
            finally { busy = was; }
            if (pulled) BlockRules.NeighbourChanged(pc);
            BlockRules.NeighbourChanged(hc);
            Sounds.Play("block.piston.contract", BlockWorld.Center(c), 0.5f, Random.value * 0.15f + 0.6f);
        }

        /// <summary>Breaking either half of an extended piston takes the other half with it.</summary>
        private static void PistonBroken(Vector3Int c, BState old, BState now)
        {
            if (old == null || (now != null && now.Block == old.Block)) return;
            busy = true;
            try
            {
                if (old.Block.Class == "PistonBlock" && old["extended"] == "true")
                {
                    var hc = Step(c, F(old));
                    var h = BlockWorld.Get(hc);
                    if (h != null && h.Block.Class == "PistonHeadBlock") BlockWorld.Remove(hc, false);
                }
                else if (old.Block.Class == "PistonHeadBlock")
                {
                    var bc = Step(c, BlockWorld.Opposite(F(old)));
                    var b = BlockWorld.Get(bc);
                    if (b != null && b.Block.Class == "PistonBlock" && b["extended"] == "true") BlockWorld.Set(bc, b.With("extended", "false"), false);
                }
            }
            finally { busy = false; }
        }

        // ------------------------------------------------------------------------------------------------ sensors

        /// <summary>Pressure plates (every other tick) and daylight sensors (once a second).</summary>
        private static void Sensors()
        {
            if (sensors.Count == 0) return;
            foreach (var c in new List<Vector3Int>(sensors))
            {
                var s = BlockWorld.Get(c);
                if (s == null) { sensors.Remove(c); continue; }
                switch (s.Block.Class)
                {
                    case "PressurePlateBlock":
                    {
                        bool mobsOnly = !Wood(s);   // stone plates ignore dropped items
                        bool on = Count(c, !mobsOnly) > 0;
                        if (on) plateSince[c] = tick;
                        bool was = s["powered"] == "true";
                        string kind = Wood(s) ? "block.wooden_pressure_plate." : "block.stone_pressure_plate.";
                        if (on && !was) { Put(c, s.With("powered", "true")); Sounds.Play(kind + "click_on", BlockWorld.Center(c), 0.3f, 0.6f); }
                        else if (!on && was && tick - (plateSince.TryGetValue(c, out long t) ? t : 0) >= 20) { Put(c, s.With("powered", "false")); Sounds.Play(kind + "click_off", BlockWorld.Center(c), 0.3f, 0.5f); }
                        break;
                    }
                    case "WeightedPressurePlateBlock":
                    {
                        int n = Count(c, true);
                        int max = s.Block.Id.StartsWith("light") ? 15 : 150;
                        int p = n == 0 ? 0 : Mathf.CeilToInt(Mathf.Min(n, max) / (float)max * 15f);
                        int was = Int(s["power"]);
                        if (p > 0) plateSince[c] = tick;
                        if (p != was && (p > 0 || tick - (plateSince.TryGetValue(c, out long t) ? t : 0) >= 10))
                        {
                            Put(c, s.With("power", p.ToString()));
                            if ((p > 0) != (was > 0)) Sounds.Play(p > 0 ? "block.metal_pressure_plate.click_on" : "block.metal_pressure_plate.click_off", BlockWorld.Center(c), 0.3f, p > 0 ? 0.9f : 0.75f);
                        }
                        break;
                    }
                    case "DaylightDetectorBlock":
                    {
                        if (tick % 20 != 0) break;
                        int day = Daylight();
                        if (s["inverted"] == "true") day = 15 - day;
                        if (Int(s["power"]) != day) Put(c, s.With("power", day.ToString()));
                        break;
                    }
                }
            }
        }

        /// <summary>How many things stand on a plate: the player, Minecraft mobs, Slime Rancher's slimes and animals, and items if `items`.</summary>
        private static int Count(Vector3Int c, bool items)
        {
            var b = new Bounds(new Vector3(c.x + 0.5f, c.y + 0.15f, c.z + 0.5f), new Vector3(0.875f, 0.3f, 0.875f));
            int n = 0;
            var feet = SrWorld.Feet;
            if (b.Contains(feet) || b.Contains(feet + Vector3.up * 0.05f)) n++;
            foreach (var m in Mobs.All) if (m != null && b.Intersects(m.Box)) n++;
            var seen = new HashSet<Identifiable>();
            foreach (var col in Physics.OverlapBox(b.center, b.extents, Quaternion.identity, ActorMask, QueryTriggerInteraction.Ignore))
            {
                var a = SrWorld.ActorOf(col);
                if (a == null || !seen.Add(a)) continue;
                if (!items && !Identifiable.IsSlime(a.id) && !Identifiable.IsAnimal(a.id) && !Identifiable.IsTarr(a.id)) continue;
                n++;
            }
            if (items) foreach (var it in ItemEntity.All) if (it != null && b.Contains(it.transform.position)) n += it.Stack != null ? it.Stack.Count : 1;
            return n;
        }

        /// <summary>Sunlight on the Far Far Range clock, 0 at night to 15 at noon.</summary>
        private static int Daylight()
        {
            var td = SRSingleton<SceneContext>.Instance?.TimeDirector;
            if (td == null) return 0;
            float h = td.CurrHour();
            if (h < 6f || h > 18f) return 0;
            return Mathf.Clamp(Mathf.RoundToInt(Mathf.Sin((h - 6f) / 12f * Mathf.PI) * 15f), 0, 15);
        }

        // ------------------------------------------------------------------------------------------------ note blocks, dispensers

        /// <summary>The instrument a note block plays, from the block under it (NoteBlockInstrument.byState).</summary>
        public static string Instrument(Vector3Int c)
        {
            var below = BlockWorld.Get(c + Vector3Int.down);
            if (below == null) return "harp";
            var id = below.Block.Id;
            var snd = below.Block.Sound;
            switch (id)
            {
                case "clay": return "flute";
                case "gold_block": return "bell";
                case "packed_ice": return "chime";
                case "bone_block": return "xylophone";
                case "iron_block": return "iron_xylophone";
                case "soul_sand": return "cow_bell";
                case "pumpkin": return "didgeridoo";
                case "emerald_block": return "bit";
                case "hay_block": return "banjo";
                case "glowstone": return "pling";
            }
            if (id.EndsWith("_wool")) return "guitar";
            if (snd == "wood" || snd == "nether_wood" || snd == "bamboo_wood" || snd == "cherry_wood") return "bass";
            if (snd == "sand" || snd == "gravel" || id.Contains("concrete_powder")) return "snare";
            if (snd == "glass" || id.Contains("glass") || id == "sea_lantern" || id == "beacon") return "hat";
            if (below.FullCube && below.Block.Opaque) return "basedrum";
            return "harp";
        }

        public static void PlayNote(Vector3Int c, BState s)
        {
            if (BlockWorld.Get(c + Vector3Int.up) != null) return;   // a block on top muffles it
            int note = Int(s["note"]);
            Sounds.Play("block.note_block." + Instrument(c), BlockWorld.Center(c), 3f, Mathf.Pow(2f, (note - 12) / 12f));
            Particles.Note(BlockWorld.Center(c) + Vector3.up * 0.7f, note);
        }

        /// <summary>
        /// A dispenser fires arrows, snowballs, eggs, fireworks, lights TNT, and shoots Slime Rancher actors out at
        /// vacpack speed; anything else (and everything from a dropper) is dropped in front of it.
        /// </summary>
        private static void Dispense(Vector3Int c, BState s)
        {
            var e = BlockWorld.Entity(c, true, 9);
            if (e.Items == null) e.Items = new ItemStack[9];
            var full = new List<int>();
            for (int i = 0; i < e.Items.Length; i++) if (e.Items[i] != null && e.Items[i].Count > 0) full.Add(i);
            if (full.Count == 0) { Sounds.Play("block.dispenser.fail", BlockWorld.Center(c), 1f, 1.2f); return; }
            int k = full[Random.Range(0, full.Count)];
            var it = e.Items[k];
            var f = F(s);
            Vector3 dir = BlockWorld.Offset(f);
            var at = BlockWorld.Center(c) + dir * 0.7f;
            var one = it.Copy(1);
            bool shoot = s.Block.Class == "DispenserBlock";
            string snd = "block.dispenser.dispense";
            if (shoot && one.IsSr) SrBridge.SpawnStack(one, at, dir * 20f + Vector3.up * 2f, true);
            else if (shoot && one.Id.EndsWith("arrow")) { Arrow.Create(one, at, dir + Vector3.up * 0.1f, 1.1f, 6f); snd = "block.dispenser.launch"; }
            else if (shoot && (one.Id == "snowball" || one.Id == "egg" || one.Id == "splash_potion" || one.Id == "experience_bottle")) { Thrown.Create(one, at, dir + Vector3.up * 0.1f, 1.1f); snd = "block.dispenser.launch"; }
            else if (shoot && one.Id == "firework_rocket") { Projectiles.Firework(at, 1, dir * 0.5f); snd = "block.dispenser.launch"; }
            else if (shoot && one.Id == "tnt") Projectiles.Tnt(BlockWorld.Center(Step(c, f)));
            else ItemEntity.Spawn(one, at - Vector3.up * 0.15f, dir * 0.3f + new Vector3(Random.Range(-0.03f, 0.03f), 0.1f, Random.Range(-0.03f, 0.03f)), 0.3f);
            it.Count--;
            if (it.Count <= 0) e.Items[k] = null;
            Persist.Dirty = true;
            Sounds.Play(snd, BlockWorld.Center(c));
            Particles.Smoke(at);
        }
    }
}
