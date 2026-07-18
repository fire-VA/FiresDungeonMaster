using System;
using HarmonyLib;
using UnityEngine;

namespace FiresDungeonMaster.Editor
{
    /// <summary>
    /// Placement diagnostics for Infinity-Hammer blueprint placement. IH funnels EVERY placed piece (single- and
    /// multi-object paths alike) through <c>InfinityHammer.BaseSelection.PostProcessPlaced(GameObject)</c>; we postfix
    /// it (soft dep, hooked by reflection so nothing breaks when IH is absent) and log each piece's WORLD position,
    /// distance from the player, and flags for pieces that landed at the world origin or absurdly far away.
    ///
    /// This is the "if IH says it's placing, show me WHERE" instrumentation: after one placement the log's
    /// [FDM-BPPLACE] lines reveal whether missing pieces went to (0,0,0)/off-map (a positioning bug) or landed correctly
    /// near the player (pointing instead at collapse or streaming). Logged only during a deliberate placement, never
    /// per-frame. Hooked lazily the first time we hand a blueprint to IH.
    /// </summary>
    internal static class BlueprintPlaceDiag
    {
        private static bool _patched;
        private static int _n;
        private static float _lastT;
        private static Vector3 _min, _max;
        private static int _origin, _far;

        /// <summary>Patch IH's per-piece placement sink once (no-op if IH not loaded / already patched).</summary>
        public static void EnsurePatched()
        {
            if (_patched) return;
            _patched = true;
            try
            {
                var t = AccessTools.TypeByName("InfinityHammer.BaseSelection");
                var m = t != null ? AccessTools.Method(t, "PostProcessPlaced") : null;
                if (m == null) { FiresLogger.LogWarning("[FDM-BPPLACE] IH BaseSelection.PostProcessPlaced not found — placement diag off."); return; }
                new Harmony("com.Fire.FiresDungeonMaster.ihdiag").Patch(m, postfix: new HarmonyMethod(typeof(BlueprintPlaceDiag), nameof(OnPlaced)));
                FiresLogger.LogInfo("[FDM-BPPLACE] hooked IH BaseSelection.PostProcessPlaced — placement positions will be logged.");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FDM-BPPLACE] could not hook IH placement diag: {ex.Message}"); }
        }

        private static void OnPlaced(GameObject obj)
        {
            if (obj == null) return;
            try
            {
                float now = Time.realtimeSinceStartup;
                if (_n > 0 && now - _lastT > 0.75f) Flush();          // gap → a new placement started; summarise the last one
                if (_n == 0) { _min = Vector3.positiveInfinity; _max = Vector3.negativeInfinity; _origin = 0; _far = 0; }

                Vector3 p = obj.transform.position;
                _min = Vector3.Min(_min, p); _max = Vector3.Max(_max, p);
                var player = Player.m_localPlayer;
                float d = player != null ? Vector3.Distance(p, player.transform.position) : -1f;
                bool atOrigin = p.magnitude < 2f;
                bool far = d > 200f;
                if (atOrigin) _origin++;
                if (far) _far++;
                _n++; _lastT = now;

                FiresLogger.LogInfo(
                    $"[FDM-BPPLACE] #{_n} '{obj.name}' @ ({p.x:F1},{p.y:F1},{p.z:F1}) dPlayer={d:F1}m" +
                    (atOrigin ? "  <<AT-ORIGIN>>" : "") + (far ? "  <<FAR>>" : ""));
            }
            catch { }
        }

        private static void Flush()
        {
            FiresLogger.LogInfo(
                $"[FDM-BPPLACE] === BATCH: {_n} pieces placed; bbox size=({(_max.x-_min.x):F1},{(_max.y-_min.y):F1},{(_max.z-_min.z):F1}) " +
                $"min=({_min.x:F1},{_min.y:F1},{_min.z:F1}) max=({_max.x:F1},{_max.y:F1},{_max.z:F1}); atOrigin={_origin} far(>200m)={_far} ===");
            _n = 0;
        }
    }
}
