using UnityEngine;
using FiresCore.UI.ContextMenu;

namespace FiresDungeonMaster.Editor
{
    /// <summary>
    /// Shift+Alt right-click a placed entry door to open its EntryDoorPanel. Registers a FiresContextMenu CLAIM (same
    /// Core driver the footprint uses): the driver frees the cursor + raycasts on Shift+Alt right-click and hands each
    /// claim the ContextTarget. Unlike the footprint (which needs a ray-vs-sphere marker source), a door's own mesh
    /// collider is the target — so this resolves the EntryDoorController straight off the physics hit and returns true
    /// to open the panel + suppress the shared companion menu. Returns false for non-door hits so other claims run.
    ///
    /// Registered lazily the first time a real door spawns (EntryDoorController.Awake). Client-only.
    /// </summary>
    public static class EntryDoorContextProvider
    {
        private static bool _registered;
        private static FiresContextMenu.TargetClaim _claim;

        public static void EnsureRegistered()
        {
            if (_registered) return;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return; // headless
            _registered = true;
            _claim = HandleClaim;
            FiresContextMenu.RegisterClaim(_claim);
            FiresLogger.LogDebug("[FiresDungeonMaster] entry-door context claim registered (Shift+Alt right-click a door).");
        }

        private static bool HandleClaim(ContextTarget target, Vector2 screenPos)
        {
            var door = Resolve(target, out bool viaExitArch);
            if (door == null) return false;                // not a door → let other claims / the shared menu handle it
            if (!EntryDoorController.IsAdmin()) return true; // non-admins can't configure; still claim so no companion menu

            if (!viaExitArch) return false;   // 0.1.199: the surface door uses the entrance menu (ProxyEntrance.PickRow); its panel is the "Door settings..." row
            // Interior exit arch -> the post-placement dungeon editor (dungeon options + the paired env box).
            FiresDungeonMaster.UI.DungeonEditPanel.Open(door, door.FindPairedEnvBox());
            return true;
        }

        // Resolves the owning entry door from either end of the portal. viaExitArch = true when the hit was the
        // interior exit arch (not the surface entry-door mesh) — that end opens the dungeon editor instead.
        private static EntryDoorController Resolve(ContextTarget target, out bool viaExitArch)
        {
            viaExitArch = false;
            if (target == null) return null;
            var go = target.GameObject;
            var d = go != null ? go.GetComponentInParent<EntryDoorController>() : null;
            if (d == null && target.Collider != null) d = target.Collider.GetComponentInParent<EntryDoorController>();
            if (d != null) return d;                       // surface entry-door mesh

            // The interior EXIT arch is the dungeon's other end — Shift+Alt right-clicking it configures the SAME
            // dungeon. The arch mesh lives under the generated portal ("FiresDM_<type>Portal"/its Exit child), and the
            // owning entry door is the generated door in the exit's XZ zone (the interior sits at that zone's centre).
            var hitGo = target.Collider != null ? target.Collider.gameObject : go;
            if (IsDungeonPortalChild(hitGo))
            {
                d = EntryDoorController.FindGeneratedDoorInZone(target.Point);
                if (d != null) { viaExitArch = true; return d; }
            }
            return null;
        }

        private static bool IsDungeonPortalChild(GameObject hit)
        {
            for (var t = hit != null ? hit.transform : null; t != null; t = t.parent)
            {
                string n = t.gameObject.name;
                if (n.IndexOf("FiresDM_", System.StringComparison.Ordinal) >= 0 &&
                    (n.IndexOf("Portal", System.StringComparison.Ordinal) >= 0 ||
                     n.IndexOf("Exit", System.StringComparison.Ordinal) >= 0))
                    return true;
            }
            return false;
        }
    }
}
