using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Slime Rancher things in the Minecraft inventory. The vacpack is gone, so there is no second inventory to keep
    /// in step: a slime, plort or carrot is simply a count in a Minecraft slot ("virtual" in the engine's terms), and
    /// turns back into the real actor when it is put down, thrown, dropped or spilled from a container.
    /// </summary>
    public static class SrBridge
    {
        public static bool Suspend;

        /// <summary>Every Slime Rancher item lives as a plain count in the Minecraft slots.</summary>
        public static bool IsVirtual(Identifiable.Id t) => t != Identifiable.Id.NONE;

        public static bool StandsIn(ItemStack s, Identifiable.Id t) => false;

        /// <summary>Nothing to reconcile: the Minecraft slots are the only inventory.</summary>
        public static void Update() { }

        /// <summary>Puts a real Slime Rancher actor into the world, in the current region (zone) of the player.</summary>
        public static GameObject Spawn(Identifiable.Id id, Vector3 pos, Quaternion rot)
        {
            GameObject prefab = null;
            try { prefab = SRSingleton<GameContext>.Instance.LookupDirector.GetPrefab(id); } catch { }
            if (prefab == null) return null;
            var region = SRSingleton<SceneContext>.Instance.RegionRegistry.GetCurrentRegionSetId();
            var go = SRBehaviour.InstantiateActor(prefab, region, pos, rot);
            if (go != null) PhysicsUtil.RestoreFreezeRotationConstraints(go);
            return go;
        }

        /// <summary>Spawns the actors of a stack at a point, flung like Minecraft items (spilled containers, death, drops).</summary>
        public static void SpawnStack(ItemStack s, Vector3 at, Vector3 vel, bool launched)
        {
            for (int i = 0; i < s.Count; i++)
            {
                var go = Spawn(s.Actor, at + Random.insideUnitSphere * 0.2f, Quaternion.identity);
                if (go == null) continue;
                var rb = go.GetComponent<Rigidbody>();
                if (rb != null) rb.velocity = vel + Random.insideUnitSphere * 0.5f;
                // Counts as shot by Beatrix, so silos, feeders and the Plort Market accept it.
                if (launched) { var vac = go.GetComponent<Vacuumable>(); if (vac != null) vac.Launch(Vacuumable.LaunchSource.PLAYER); }
            }
        }
    }
}
