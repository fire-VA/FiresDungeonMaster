using UnityEngine;

namespace FiresDungeonMaster.Editor
{
    /// <summary>
    /// DeskController — the hammer-placeable Dungeon Blueprint DESK (FiresDungeonDesk). A flavour piece: a wood
    /// table strewn with books, scrolls, a feather, a candle and an inkwell, whose sole gameplay function is an
    /// [E] Interact that opens the Saved Section Browser (FiresDungeonMaster.UI.SectionBrowserUI) — the same UI the
    /// firesdungeon_browser console command toggles.
    ///
    /// Interaction wiring is a VERBATIM copy of VerdantsAscent.Modules.Wayshrines.Wayshrine (the proven hover+[E]
    /// recipe the user activates fine in-game):
    ///   class ... : MonoBehaviour, Hoverable, Interactable
    ///   GetHoverText -> name + "[$KEY_Use] ..."     (shown to EVERYONE; the wayshrine never gates its label)
    ///   GetHoverName -> the piece name              (unconditional, so the crosshair label always resolves)
    ///   Interact(user, hold, alt) { if (hold) return false; if (user as Player != Player.m_localPlayer) return false; act; return true; }
    /// The wayshrine's Interact has NO admin gate and NO client gate on the action — it just opens its flow for the
    /// local player. The desk now matches that exactly: the only guards are `hold` and the local-player check. The
    /// prior build added an admin gate + an IsClientOnly gate BEFORE the action, which is the one thing the working
    /// wayshrine does not do; those extra gates are removed so [E] fires identically. Dedi-safety lives inside
    /// SectionBrowserUI.Show() (its own IsClientOnly() no-op), matching how the wayshrine defers its client-only work.
    ///
    /// How [E] reaches here (assembly_valheim/Player.cs): FindHoverObject (:2960) sets m_hovering to the collider's
    /// GameObject only when that GameObject carries a Hoverable — the collider + this component share the root, so
    /// hover resolves (label shows). Player.Update (:481) then calls Interact(m_hovering, false, alt), and
    /// Player.Interact (:2980) resolves go.GetComponentInParent<Interactable>() == this DeskController and calls
    /// Interact(...). The [DESK-DBG] logging below proves that whole path fires and traces into Show().
    ///
    /// Collider contract: the baked prefab carries a solid BoxCollider on this same root GameObject (added in
    /// FootprintRegistration.RegisterPrefabs on the 'piece' layer, which is in Player.m_interactMask). Awake
    /// re-fits it to the desk's renderer bounds on the placed instance as a safety net (a default 1x1x1 box would
    /// let the aim ray sail past the wide, low table); this is idempotent and headless-safe.
    /// </summary>
    public class DeskController : MonoBehaviour, Hoverable, Interactable
    {
        private const string DeskDisplayName = "Dungeon Blueprint Desk";

        private void Awake() => FitBlockingCollider();

        // Put the desk on the SOLID 'piece' layer and size its BoxCollider to the visible mesh, so the player
        // physically collides with it (no walk-through) AND the hover ray lands on this collider (so GetHoverText /
        // Interact fire — the Hoverable is on THIS same GameObject). Runs on the placed instance where the renderer
        // bounds are real. Idempotent.
        private void FitBlockingCollider()
        {
            int pieceLayer = LayerMask.NameToLayer("piece");
            if (pieceLayer >= 0) gameObject.layer = pieceLayer; // solid + in m_interactMask

            var box = GetComponent<BoxCollider>();
            if (box == null) box = gameObject.AddComponent<BoxCollider>();
            box.isTrigger = false;

            // Fit to the combined renderer bounds (world AABB -> this transform's local space). The desk mesh sits
            // on child renderers; without this the collider stays the default 1x1x1 cube at the origin and the aim
            // ray misses the wide, low table entirely.
            Bounds? local = ComputeLocalRendererBounds();
            if (local.HasValue)
            {
                box.center = local.Value.center;
                box.size = local.Value.size;
            }
            FiresLogger.LogDebug($"[DESK-DBG] fit collider layer={LayerMask.LayerToName(gameObject.layer)} " +
                      $"size={box.size} center={box.center} fromBounds={local.HasValue}"); // TEMP
        }

        // Union of every child MeshRenderer's world bounds, transformed into THIS GameObject's local space, so the
        // BoxCollider (which is local to this transform) wraps the whole desk regardless of child offsets/scale.
        private Bounds? ComputeLocalRendererBounds()
        {
            var renderers = GetComponentsInChildren<MeshRenderer>(true);
            if (renderers == null || renderers.Length == 0) return null;

            bool any = false;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            foreach (var mr in renderers)
            {
                if (mr == null) continue;
                Bounds wb = mr.bounds;            // world-space AABB
                foreach (Vector3 corner in BoundsCorners(wb))
                {
                    Vector3 lp = transform.InverseTransformPoint(corner);
                    if (!any) { min = max = lp; any = true; continue; }
                    min = Vector3.Min(min, lp);
                    max = Vector3.Max(max, lp);
                }
            }
            if (!any) return null;
            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }

        private static Vector3[] BoundsCorners(Bounds b)
        {
            Vector3 c = b.center, e = b.extents;
            return new[]
            {
                c + new Vector3( e.x,  e.y,  e.z), c + new Vector3( e.x,  e.y, -e.z),
                c + new Vector3( e.x, -e.y,  e.z), c + new Vector3( e.x, -e.y, -e.z),
                c + new Vector3(-e.x,  e.y,  e.z), c + new Vector3(-e.x,  e.y, -e.z),
                c + new Vector3(-e.x, -e.y,  e.z), c + new Vector3(-e.x, -e.y, -e.z),
            };
        }

        // ---------------- Hoverable (wayshrine shape) ----------------

        public string GetHoverText()
        {
            string useKey = "$KEY_Use";
            if (Localization.instance != null) useKey = Localization.instance.Localize(useKey);
            return $"<color=orange><b>{DeskDisplayName}</b></color>\n" +
                   $"Browse and place your saved dungeon sections.\n" +
                   $"[<color=yellow><b>{useKey}</b></color>] Open Section Browser";
        }

        public float GetHoverOffset() => 0f;

        public string GetHoverName() => DeskDisplayName;

        // ---------------- Interactable (wayshrine call shape + admin gate) ----------------
        //
        // Wayshrine.Interact call shape (`hold` + local-player checks), plus the ADMIN gate: the bench behind this
        // desk is dungeon-authoring tooling (save/place sections, generate dungeons) — never a player-facing station.
        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold) return false;
            if (user as Player != Player.m_localPlayer) return false;

            if (!SectionFootprintController.IsAdmin())
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Only an admin may use the blueprint desk.");
                return true;
            }

            FiresDungeonMaster.UI.BlueprintBenchPanel.Show();
            return true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
    }
}
