using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FiresDungeonMaster.Modules
{
    /// <summary>
    /// Structure Tweaks' "All objects scalable" behaviour, copied exactly (StructureTweaksPlugin.AllScalable) —
    /// ST is a REF project here, not installed, and without it blueprint SCALE does not PERSIST: vanilla
    /// ZNetView only writes/reads the ZDO scale when the prefab's m_syncInitialScale is set, which most building
    /// pieces don't. A scaled piece then looks right when placed but snaps back to 1.0 on reload and never shows
    /// scaled to other peers. This forces m_syncInitialScale on every registered prefab (and every live instance)
    /// at ZNetScene.Awake, exactly like ST, so blueprint scale round-trips: bench save → IH/our placement → ZDO →
    /// reload → every peer. Config-gated (AllPiecesScalable, hot-reloads); originals are snapshotted so turning
    /// the toggle off restores stock flags. Fireplace particle systems switch to Hierarchy scaling so scaled
    /// braziers/fires don't render full-size flames (ST's UpdateParticles).
    /// </summary>
    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    public static class AllScalablePatch
    {
        private static readonly Dictionary<int, bool> Originals = new Dictionary<int, bool>();

        // STAND DOWN when the real Structure Tweaks is installed — ST owns m_syncInitialScale then. Running both
        // copies is not benign: each snapshots "originals" to restore on toggle-off, and whichever postfix runs
        // SECOND snapshots flags the first has already forced true (poisoned originals → pieces stay scalable
        // even with both toggles off). Scale persistence still works under ST; ours only fills ST's absence.
        private const string StructureTweaksGuid = "structure_tweaks";
        private static bool? _structureTweaksPresent;
        private static bool _standDownLogged;

        private static bool StructureTweaksPresent
        {
            get
            {
                if (!_structureTweaksPresent.HasValue)
                    _structureTweaksPresent = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(StructureTweaksGuid);
                return _structureTweaksPresent.Value;
            }
        }

        private static bool StandDown()
        {
            if (!StructureTweaksPresent) return false;
            if (!_standDownLogged)
            {
                _standDownLogged = true;
                Debug.Log("[FiresDungeonMaster] Structure Tweaks detected — AllScalable stands down (ST owns m_syncInitialScale).");
            }
            return true;
        }

        public static void Update()
        {
            if (StandDown()) return;
            if (ZNetScene.instance != null) Update(ZNetScene.instance);
        }

        private static void Update(int prefab, ZNetView view)
        {
            if (DungeonMasterConfig.AllPiecesScalable != null && DungeonMasterConfig.AllPiecesScalable.Value)
            {
                view.m_syncInitialScale = true;
            }
            else
            {
                if (!Originals.TryGetValue(prefab, out bool original)) return;
                view.m_syncInitialScale = original;
            }
        }

        private static void Update(ZNetScene scene)
        {
            var applied = new Dictionary<int, bool>();
            foreach (KeyValuePair<int, GameObject> namedPrefab in scene.m_namedPrefabs)
            {
                if (namedPrefab.Value == null) continue;
                ZNetView view = namedPrefab.Value.GetComponent<ZNetView>();
                if (view == null) continue;
                Update(namedPrefab.Key, view);
                applied[namedPrefab.Key] = view.m_syncInitialScale;
            }
            foreach (KeyValuePair<ZDO, ZNetView> instance in scene.m_instances)
            {
                int prefab = instance.Key.GetPrefab();
                if (!applied.ContainsKey(prefab)) continue;
                ZNetView view = instance.Value != null ? instance.Value.GetComponent<ZNetView>() : null;
                if (view != null) view.m_syncInitialScale = applied[prefab];
            }
        }

        private static void UpdateParticles(ZNetScene scene)
        {
            GameObject[] fireplaces = scene.m_namedPrefabs.Values
                .Where(v => v != null && (bool)v.GetComponent<Fireplace>()).ToArray();
            var results = new List<ParticleSystem>();
            foreach (GameObject go in fireplaces)
            {
                go.GetComponentsInChildren(true, results);
                foreach (ParticleSystem ps in results)
                {
                    if (ps.main.scalingMode == ParticleSystemScalingMode.Local)
                    {
                        var main = ps.main;
                        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                    }
                }
            }
        }

        [HarmonyPriority(0)]
        private static void Postfix(ZNetScene __instance)
        {
            try
            {
                if (StandDown()) return;
                foreach (KeyValuePair<int, GameObject> namedPrefab in __instance.m_namedPrefabs)
                {
                    if (Originals.ContainsKey(namedPrefab.Key) || namedPrefab.Value == null) continue;
                    ZNetView view = namedPrefab.Value.GetComponent<ZNetView>();
                    if (view != null) Originals[namedPrefab.Key] = view.m_syncInitialScale;
                }
                Update(__instance);
                UpdateParticles(__instance);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresDungeonMaster] AllScalable pass failed: {ex.Message}");
            }
        }
    }
}
