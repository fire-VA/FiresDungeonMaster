using HarmonyLib;
using UnityEngine;

namespace FiresDungeonMaster.Editor
{
    /// <summary>
    /// EntryDoorController — the runtime brain on a placed dungeon ENTRY DOOR (one of the FiresDoor* prefabs). An
    /// admin places the door with the hammer, then Shift+Alt right-clicks it (EntryDoorContextProvider) — or presses
    /// [E] — to open the EntryDoorPanel and set: which DUNGEON it generates (a vanilla dungeon or a custom section
    /// set), the interior ENVIRONMENT, the DISPLAY NAME shown on discovery, and the LOCK mode (open / a key prefab /
    /// an RPGMaker quest). Selecting Generate builds the dungeon behind the door (server-authoritative via
    /// EntryDoorRpc → the shared FiresDungeon.Core engine). Config lives in the door's own ZDO so it persists and
    /// syncs to every peer. Mirrors SectionFootprintController's ZDO/ownership idiom + DeskController's hover/interact.
    ///
    /// Attached mod-side at ZNetScene registration (FootprintRegistration), like the footprint + desk — the Unity
    /// builder can't reference this type. Client-only behaviour is guarded; the ZDO config is readable on the server.
    /// </summary>
    public class EntryDoorController : MonoBehaviour, Hoverable, Interactable, IRemoved
    {
        public enum DoorLockMode { Open = 0, Key = 1, Quest = 2 }

        // Curated pick-lists the panel cycles. Vanilla dungeon labels map to real theme names in EntryDoorGeneration;
        // custom dungeon-types come from the saved section library at panel-open time. ALL vanilla dungeons (caves
        // included) are pure mesh Room prefabs — none carve terrain for walls. Their walls render at the y+5000 interior
        // once the client has BUILT the type's spec (DG prefab in ZNetScene) — see RegisterAllVanillaTypes /
        // reference_dungeon_door_pipeline "CLIENT WALLS". Mapped to Room.Theme in DungeonMasterSpecBuilder.VanillaThemes.
        public static readonly string[] VanillaDungeons =
        {
            "Forest Crypt", "Sunken Crypt", "Meadows Crypt",
            "Frost Cave", "Infested Mine", "Troll Cave",
            "Goblin Camp", "Ashlands Ruins", "Fortress Ruins",
        };
        public static readonly string[] Environments = { "Crypt", "SunkenCrypt", "Caves", "Mistlands", "Ashlands", "Meadows", "Nothing" };
        public static readonly string[] KeyOptions = { "FiresKeyOld", "FiresKeyCopper", "FiresKeySkeleton", "FiresKeySkeletonOrnate" };
        public static readonly string[] LockDifficulties = { "Easy", "Intermediate", "Hard", "Very Hard" };   // LockpickPanel quality presets

        private ZNetView m_nview;

        private static readonly int HASH_DTYPE = "fdmdoor_dtype".GetStableHashCode();
        private static readonly int HASH_VANILLA = "fdmdoor_vanilla".GetStableHashCode();
        private static readonly int HASH_ENV = "fdmdoor_env".GetStableHashCode();
        private static readonly int HASH_DNAME = "fdmdoor_dname".GetStableHashCode();
        private static readonly int HASH_LOCK = "fdmdoor_lock".GetStableHashCode();
        private static readonly int HASH_KEY = "fdmdoor_key".GetStableHashCode();
        private static readonly int HASH_LOCKDIFF = "fdmdoor_lockdiff".GetStableHashCode();
        private static readonly int HASH_QUEST = "fdmdoor_quest".GetStableHashCode();
        private static readonly int HASH_EMPTY = "fdmdoor_empty".GetStableHashCode();
        private static readonly int HASH_GEN = "fdmdoor_gen".GetStableHashCode();
        private static readonly int HASH_UNLOCKED = "fdmdoor_unlocked".GetStableHashCode();

        private void Awake()
        {
            m_nview = GetComponent<ZNetView>();
            EntryDoorContextProvider.EnsureRegistered();   // self-guards headless; lazy-registers the Shift+Alt claim
            FitDoorCollider();
            EnsureSpecOnThisPeer();
        }

        // Empty-room specs are built per-door at GENERATE time on the SERVER only — unlike vanilla/library types,
        // nothing re-registers them on other peers or after a restart. Without the spec on THIS peer the replicated
        // Portal ZDO (exit teleport + landing pad + exit door) can't resolve its renamed prefab, so the interior never
        // appears ("the way beyond hasn't finished forming") and a server restart orphans the saved interior everywhere.
        // The door's ZDO carries the full config and replicates to every peer, so each peer rebuilds the spec locally —
        // names are deterministic from DungeonType. Called from Awake (zone load, client AND dedi reload), from the
        // generate-result RPC (fresh generate on a remote client), and lazily from TeleportIntoDungeon.
        private static readonly System.Collections.Generic.HashSet<string> _emptySpecsEnsured =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

        internal void EnsureSpecOnThisPeer()
        {
            if (m_nview == null || !m_nview.IsValid()) return;   // placement ghost / not networked yet
            if (!EmptyBox || !Generated || string.IsNullOrEmpty(DungeonType)) return;
            if (!_emptySpecsEnsured.Add(DungeonType)) return;
            try
            {
                var spec = FiresDungeonMaster.Generation.DungeonMasterSpecBuilder.BuildAndRegisterEmpty(DungeonType, Environment, out string reason);
                if (spec == null)
                {
                    _emptySpecsEnsured.Remove(DungeonType);   // e.g. bundle not loaded yet — retry on the next door load / [E]
                    Debug.LogWarning($"[FiresDungeonMaster] empty-room spec ensure ('{DungeonType}') failed on this peer: {reason}");
                }
            }
            catch (System.Exception ex)
            {
                _emptySpecsEnsured.Remove(DungeonType);
                Debug.LogError($"[FiresDungeonMaster] empty-room spec ensure ('{DungeonType}') threw: {ex}");
            }
        }

        // The baked door carries a NON-CONVEX MeshCollider on its Body child. Valheim's UpdatePlacementGhost derives
        // the ghost snap from the closest point on a collider, which a non-convex MeshCollider can't provide — so the
        // door places at a bad position you never see (the footprint hit the same class of bug). Mirror the proven
        // DESK instead: disable the MeshCollider(s) and fit a SOLID convex BoxCollider on THIS root (the Hoverable's
        // GameObject, on the 'piece' layer in m_interactMask) so placement snaps, the door blocks, and hover/[E] fire.
        private void FitDoorCollider()
        {
            // The baked mesh faces AWAY from the player when placed (Valheim orients a piece's +Z away from you).
            // Flip the visual 180° so the door's front faces the player — no mesh re-bake. Runs on the ghost too
            // (Awake fires on the placement ghost), so the preview matches the placed result. Absolute (baked=identity).
            var body = transform.Find("Body");
            if (body != null) body.localRotation = Quaternion.Euler(0f, 180f, 0f);

            int pieceLayer = LayerMask.NameToLayer("piece");
            if (pieceLayer >= 0) gameObject.layer = pieceLayer;

            foreach (var mc in GetComponentsInChildren<MeshCollider>(true))
                if (mc != null) mc.enabled = false;

            var box = GetComponent<BoxCollider>() ?? gameObject.AddComponent<BoxCollider>();
            box.isTrigger = false;
            var local = ComputeLocalRendererBounds();
            if (local.HasValue) { box.center = local.Value.center; box.size = local.Value.size; }
        }

        // ── DIAGNOSTICS — prove WHERE the door lands + its render/network state. Verbose-gated. ────────────────
        private void Start()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            if (!FiresLogger.VerboseEnabled) return;
            DumpState("Start");
            StartCoroutine(DelayedDump());
        }

        private System.Collections.IEnumerator DelayedDump()
        {
            yield return null; yield return null; yield return null;
            if (this != null) DumpState("t+3frames");
        }

        private void DumpState(string when)
        {
            var mrs = GetComponentsInChildren<MeshRenderer>(true);
            string rinfo = "no-renderers";
            if (mrs != null && mrs.Length > 0)
            {
                var mr = mrs[0];
                var mat = mr.sharedMaterial;
                var mf = mr.GetComponent<MeshFilter>();
                rinfo = $"renderers={mrs.Length} r0.enabled={mr.enabled} shader='{(mat != null ? mat.shader?.name : "NULL-MAT")}' " +
                        $"mesh={(mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "NULL")} r0.worldBoundsCtr={mr.bounds.center} r0.worldBoundsSize={mr.bounds.size}";
            }
            var zdo = m_nview != null ? m_nview.GetZDO() : null;
            Debug.Log($"[ENTRYDOOR-DIAG] {name} @{when} pos={transform.position} lossyScale={transform.lossyScale} " +
                      $"activeInHierarchy={gameObject.activeInHierarchy} layer={LayerMask.LayerToName(gameObject.layer)} " +
                      $"nviewValid={(m_nview != null && m_nview.IsValid())} zdoNull={zdo == null} {rinfo}");
        }

        private void OnDestroy()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            if (!FiresLogger.VerboseEnabled) return;
            Debug.Log($"[ENTRYDOOR-DIAG] {name} OnDestroy pos={transform.position}");
        }

        private Bounds? ComputeLocalRendererBounds()
        {
            var renderers = GetComponentsInChildren<MeshRenderer>(true);
            if (renderers == null || renderers.Length == 0) return null;
            bool any = false; Vector3 min = Vector3.zero, max = Vector3.zero;
            foreach (var mr in renderers)
            {
                if (mr == null) continue;
                Bounds wb = mr.bounds;
                foreach (Vector3 corner in BoundsCorners(wb))
                {
                    Vector3 lp = transform.InverseTransformPoint(corner);
                    if (!any) { min = max = lp; any = true; continue; }
                    min = Vector3.Min(min, lp); max = Vector3.Max(max, lp);
                }
            }
            if (!any) return null;
            var b = new Bounds(); b.SetMinMax(min, max); return b;
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

        private static bool IsClientOnly() => SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
        internal static bool IsAdmin() => SectionFootprintController.IsAdmin();

        // ── ZDO-backed config (persists per placed door, syncs to every peer) ─────────────────────────────────
        private ZDO Zdo => (m_nview != null && m_nview.IsValid()) ? m_nview.GetZDO() : null;

        public string DungeonType { get => Zdo?.GetString(HASH_DTYPE, "") ?? ""; set => SetStr(HASH_DTYPE, value); }
        public bool IsVanilla { get => (Zdo?.GetInt(HASH_VANILLA, 1) ?? 1) != 0; set => SetInt(HASH_VANILLA, value ? 1 : 0); }
        public string Environment { get => Zdo?.GetString(HASH_ENV, "Crypt") ?? "Crypt"; set => SetStr(HASH_ENV, value); }
        public string DisplayName { get => Zdo?.GetString(HASH_DNAME, "") ?? ""; set => SetStr(HASH_DNAME, value); }
        public DoorLockMode Lock { get => (DoorLockMode)(Zdo?.GetInt(HASH_LOCK, 0) ?? 0); set => SetInt(HASH_LOCK, (int)value); }
        public string KeyPrefab { get => Zdo?.GetString(HASH_KEY, KeyOptions[0]) ?? KeyOptions[0]; set => SetStr(HASH_KEY, value); }
        public string LockDifficulty { get => Zdo?.GetString(HASH_LOCKDIFF, "Intermediate") ?? "Intermediate"; set => SetStr(HASH_LOCKDIFF, value); }
        public string QuestId { get => Zdo?.GetString(HASH_QUEST, "") ?? ""; set => SetStr(HASH_QUEST, value); }
        public bool EmptyBox { get => (Zdo?.GetInt(HASH_EMPTY, 0) ?? 0) != 0; set => SetInt(HASH_EMPTY, value ? 1 : 0); }
        public bool Generated { get => (Zdo?.GetInt(HASH_GEN, 0) ?? 0) != 0; set => SetInt(HASH_GEN, value ? 1 : 0); }
        public bool Unlocked { get => (Zdo?.GetInt(HASH_UNLOCKED, 0) ?? 0) != 0; set => SetInt(HASH_UNLOCKED, value ? 1 : 0); }

        private void SetStr(int hash, string v) { ClaimOwnershipIfNeeded(); Zdo?.Set(hash, v ?? ""); }
        private void SetInt(int hash, int v) { ClaimOwnershipIfNeeded(); Zdo?.Set(hash, v); }

        private void ClaimOwnershipIfNeeded()
        {
            if (m_nview != null && m_nview.IsValid() && !m_nview.IsOwner())
                m_nview.ClaimOwnership();
        }

        // Base chance a pick snaps on a fumbled [E], and how much of that a maxed Lockpicking skill removes (0.7 = a
        // master snaps at 30% of the base rate). Effective chance = BaseBreakChance * (1 - BreakSkillRelief * factor).
        private const float BaseBreakChance = 0.15f;
        private const float BreakSkillRelief = 0.7f;

        // True if the inventory holds an item whose drop-prefab has this name (e.g. "FiresLockpick" or a door's KeyPrefab).
        private static bool HasItem(Inventory inv, string prefabName)
        {
            if (inv == null || string.IsNullOrEmpty(prefabName)) return false;
            var all = inv.GetAllItems();
            for (int i = 0; i < all.Count; i++)
            {
                var it = all[i];
                if (it != null && it.m_dropPrefab != null && it.m_dropPrefab.name == prefabName) return true;
            }
            return false;
        }

        // Consume ONE lockpick from the picker's own inventory when a pick snaps. The player owns their inventory ZDO,
        // so this local removal IS authoritative (same path vanilla uses to consume arrows/potions) and replicates.
        private static void ConsumeOneLockpick(Player picker)
        {
            var inv = picker?.GetInventory();
            if (inv == null) return;
            var all = inv.GetAllItems();
            for (int i = 0; i < all.Count; i++)
            {
                var it = all[i];
                if (it != null && it.m_dropPrefab != null && it.m_dropPrefab.name == "FiresLockpick")
                {
                    inv.RemoveItem(it, 1);
                    return;
                }
            }
        }

        public string DisplayNameOrDefault() => string.IsNullOrEmpty(DisplayName) ? "Unnamed Dungeon" : DisplayName;

        // ── Generate: build the dungeon behind this door (server-authoritative) ───────────────────────────────
        public string Generate()
        {
            Vector3 pos = transform.position;
            Quaternion rot = transform.rotation;
            if (ZNet.instance != null && ZNet.instance.IsServer())
                return EntryDoorGeneration.GenerateForDoor(this, pos, rot);   // host / dedi console — direct
            return EntryDoorRpc.RequestGenerate(this, pos, rot);              // remote client — ask the server
        }

        // ── Hoverable ─────────────────────────────────────────────────────────────────────────────────────────
        public string GetHoverText()
        {
            string useKey = Localization.instance != null ? Localization.instance.Localize("$KEY_Use") : "E";
            if (IsAdmin())
            {
                string dungeon = string.IsNullOrEmpty(DungeonType) ? "<color=#ff8080>unset</color>" : DungeonType;
                string mode = EmptyBox ? "Empty Room" : "Full Dungeon";
                string lockStr = Lock == DoorLockMode.Open ? "Open" : Lock == DoorLockMode.Key ? ($"Key:{KeyPrefab} ({LockDifficulty})") : "Quest";
                return $"<color=orange><b>{DisplayNameOrDefault()}</b></color>\n" +
                       $"Dungeon: <color=yellow>{dungeon}</color>  ·  {mode}  ·  Lock: {lockStr}  ·  {(Generated ? "<color=#7cff7c>generated</color>" : "not generated")}\n" +
                       $"[<color=yellow><b>Shift+Alt+RightClick</b></color>] Configure   ·   [<color=yellow><b>{useKey}</b></color>] Enter";
            }
            // Non-admin: just the door's public face.
            if (Lock != DoorLockMode.Open && !Generated)
                return $"<color=orange><b>{DisplayNameOrDefault()}</b></color>\n<color=#ff8080>Locked</color>";
            return $"<color=orange><b>{DisplayNameOrDefault()}</b></color>\n[<color=yellow><b>{useKey}</b></color>] Enter";
        }

        public string GetHoverName() => DisplayNameOrDefault();

        // ── Interactable ──────────────────────────────────────────────────────────────────────────────────────
        // [E] is ENTER only — never config (config is Shift+Alt right-click, via EntryDoorContextProvider). The real
        // walk-through teleport is the generated dungeon's gateway sitting at this door; [E] gives status + a nudge.
        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold) return false;
            if (user as Player != Player.m_localPlayer) return false;

            // A KEY lock: the matching KEY item opens it outright; a LOCKPICK item opens the pick minigame; with
            // neither, the "this door requires a key" cue plays. Once opened, it's Unlocked.
            if (Lock == DoorLockMode.Key && !Unlocked)
            {
                var inv = (user as Player)?.GetInventory();
                if (HasItem(inv, KeyPrefab))
                {
                    Unlocked = true;
                    FiresDungeonMaster.UI.LockpickAudio.PlayDoorUnlock();
                    Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "<color=#7cff7c>The key turns — the lock clicks open.</color>");
                    return true;
                }
                if (HasItem(inv, "FiresLockpick"))
                {
                    var picker = user as Player;
                    float factor = LockpickSkill.LocalFactor();
                    float effBreak = BaseBreakChance * (1f - BreakSkillRelief * factor);   // Lockpicking sharply lowers snap chance
                    // The panel's unlock beat plays the door-unlock SFX itself — onSuccess only flips state + messages.
                    FiresDungeonMaster.UI.LockpickPanel.Open(LockDifficulty, 5, effBreak, factor,
                        onSuccess: () => { Unlocked = true; Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "<color=#7cff7c>The lock clicks open.</color>"); },
                        onFail: () => Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "The lock holds fast."),
                        onBreak: () => { ConsumeOneLockpick(picker); Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "<color=#ff8080>Your lockpick snapped!</color>"); });
                    return true;
                }
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "This door requires a key.");
                FiresDungeonMaster.UI.LockpickAudio.PlayRequiresKey();
                return true;
            }
            if (Lock == DoorLockMode.Quest && !Unlocked)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "This door is sealed by a quest.");
                return true;
            }
            if (!Generated)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, IsAdmin()
                    ? "Not generated. Shift+Alt right-click to configure, then Generate."
                    : "The way beyond is not yet open.");
                return true;
            }
            return TeleportIntoDungeon(user as Player);
        }

        // The entry door and this dungeon's interior EXIT are the two ends of ONE portal — [E] teleports the player
        // DIRECTLY between them (no gateway intermediary; the gateway now sits at the zone centre and is unused). Lands
        // just in front of the exit on its interior side, lifted a touch so the point isn't rejected as inside geometry.
        // Client-side (the walk-in trigger + gateway hover/interact are suppressed by DungeonTeleportPatches).
        private bool TeleportIntoDungeon(Player player)
        {
            if (player == null) return false;

            EnsureSpecOnThisPeer();   // backstop: an empty-room interior can't instantiate here until its spec exists

            // The interior is at (zoneCentre.xz, +5000) of the door's zone — find OUR exit teleport there.
            Vector3 anchor = ZoneSystem.GetZonePos(ZoneSystem.GetZone(transform.position));
            Teleport exit = FindDungeonExit(anchor);
            if (exit == null)
            {
                Debug.Log($"[FDM-PLACE] door enter '{DungeonType}': no exit teleport near zone centre {anchor} — not generated on this peer yet?");
                player.Message(MessageHud.MessageType.Center, "The way beyond hasn't finished forming.");
                return true;
            }

            LandingInFrontOf(exit.transform, InteriorLandOutward, out Vector3 pt, out Quaternion rot);
            Debug.Log($"[FDM-PLACE] door enter '{DungeonType}': exit={exit.transform.position} -> land {pt} facing {rot.eulerAngles}.");
            FiresDungeonMaster.UI.LockpickAudio.PlayDoorOpen();
            player.TeleportTo(pt, rot, false);
            // Announce the dungeon's configured DISPLAY NAME on entry — the vanilla biome/location discovery banner.
            // This is what makes the rename actually SHOW: our direct portal bypasses vanilla's location announce.
            MessageHud.instance?.ShowBiomeFoundMsg(DisplayNameOrDefault(), true);
            return true;
        }

        // ── door <-> exit portal landing (shared by both ends) ─────────────────────────────────────────────────────
        private const float TeleportFrontDist = 2f;    // metres out in front of the target piece
        private const float TeleportYPad = 0.5f;       // small lift so Valheim doesn't reject the point as inside ground/geometry
        private const bool InteriorLandOutward = false; // exit arch: land on its -forward (dungeon-interior) side — flip if it lands you outside
        private const bool SurfaceLandOutward = false;  // entry door: land on its -forward (face) side — the door's visual front is flipped 180° in FitDoorCollider, so its FACE is -forward; land there, 2m out, not behind it

        // Landing just in front of <paramref name="target"/> (on its forward or -forward side per <paramref name="outward"/>),
        // lifted by TeleportYPad, facing that way. Used by both entry (→ exit) and exit (→ door) so the two ends match.
        private static void LandingInFrontOf(Transform target, bool outward, out Vector3 pos, out Quaternion rot)
        {
            Vector3 fwd = target.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
            fwd.Normalize();
            if (!outward) fwd = -fwd;
            pos = target.position + fwd * TeleportFrontDist + Vector3.up * TeleportYPad;
            rot = Quaternion.LookRotation(fwd, Vector3.up);
        }

        // The interior EXIT teleport of THIS door's dungeon (a Portal child ~y+5000). Matched by the FiresDM_+Exit name
        // and nearest XZ to the zone-centre anchor — the dungeon sits above the zone centre (same XZ / zone) at +5000.
        private static Teleport FindDungeonExit(Vector3 anchor)
        {
            Vector2 aXZ = new Vector2(anchor.x, anchor.z);
            Teleport best = null;
            float bestSqr = 34f * 34f;
            foreach (var t in UnityEngine.Object.FindObjectsByType<Teleport>(FindObjectsSortMode.None))
            {
                if (t == null) continue;
                string n = t.gameObject.name;
                if (n.IndexOf("Exit", System.StringComparison.Ordinal) < 0 ||
                    n.IndexOf("FiresDM_", System.StringComparison.Ordinal) < 0) continue;
                Vector2 xz = new Vector2(t.transform.position.x, t.transform.position.z);
                float sqr = (xz - aXZ).sqrMagnitude;
                if (sqr < bestSqr) { bestSqr = sqr; best = t; }
            }
            return best;
        }

        // Called from DungeonTeleportPatches when the local player uses an interior EXIT teleport: return them to the
        // ENTRY DOOR that spawned this dungeon (you leave where you came in), rather than the gateway that now sits at
        // the zone centre. The interior is at (zoneCentre.xz, +5000) of the DOOR's zone, so the door is the generated
        // EntryDoorController in the exit's XZ zone. Lands just in front of the door. Returns false if no door found
        // (the caller then lets vanilla exit to the gateway). Client-side.
        /// <summary>The GENERATED entry door whose dungeon owns this XZ zone (interior sits at that zone's centre +5000).</summary>
        public static EntryDoorController FindGeneratedDoorInZone(Vector3 pos)
        {
            Vector2i zone = ZoneSystem.GetZone(pos);
            foreach (var d in UnityEngine.Object.FindObjectsByType<EntryDoorController>(FindObjectsSortMode.None))
            {
                if (d == null || !d.Generated) continue;
                if (ZoneSystem.GetZone(d.transform.position) == zone) return d;
            }
            return null;
        }

        /// <summary>
        /// The environment box spawned for THIS dungeon's interior (FiresDungeonCore.EnsureEnvBox places one box at the
        /// zone centre, +5000). Matched by the box nearest — in XZ — to our zone centre, at interior altitude, in our
        /// zone. Null until it spawns on this peer. Client-safe; used by the exit-arch dungeon editor to pair the box.
        /// </summary>
        public FiresCore.Utilities.EnvironmentBoxController FindPairedEnvBox()
        {
            Vector2i zone = ZoneSystem.GetZone(transform.position);
            Vector3 anchor = ZoneSystem.GetZonePos(zone);
            FiresCore.Utilities.EnvironmentBoxController best = null;
            float bestXZ = float.MaxValue;
            foreach (var b in UnityEngine.Object.FindObjectsByType<FiresCore.Utilities.EnvironmentBoxController>(FindObjectsSortMode.None))
            {
                if (b == null) continue;
                Vector3 p = b.transform.position;
                if (p.y < 2500f) continue;                        // interior altitude only (skip any surface-placed boxes)
                if (ZoneSystem.GetZone(p) != zone) continue;      // must belong to OUR dungeon's zone
                float dxz = (new Vector2(p.x, p.z) - new Vector2(anchor.x, anchor.z)).sqrMagnitude;
                if (dxz < bestXZ) { bestXZ = dxz; best = b; }
            }
            return best;
        }

        public static bool ExitToDoor(Vector3 exitPos, Player player)
        {
            if (player == null) return false;
            EntryDoorController door = FindGeneratedDoorInZone(exitPos);
            if (door == null) return false;

            // Symmetric with entry: land just in front of the door on its outside, lifted a touch, facing out.
            LandingInFrontOf(door.transform, SurfaceLandOutward, out Vector3 pt, out Quaternion rot);
            FiresDungeonMaster.UI.LockpickAudio.PlayDoorOpen();
            player.TeleportTo(pt, rot, false);
            return true;
        }

        // ── IRemoved: the hammer removed this door → tear down the dungeon it spawned (env box + portal + rooms at
        // y+5000). Fires on the removing peer only (Player.RemovePiece), before the door is destroyed. ─────────────
        public void OnRemoved()
        {
            if (!Generated) return;
            Vector3 doorPos = transform.position;
            if (ZNet.instance != null && ZNet.instance.IsServer())
                EntryDoorGeneration.CleanupDungeonForDoor(doorPos);   // host / dedi-console — direct
            else
                EntryDoorRpc.RequestCleanup(doorPos);                  // remote client — ask the server
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;

        // DIAG: capture the placement-ghost transform the moment a door piece is placed, so we see WHERE
        // Valheim is putting it even if the instance never spawns near us. Client-only (skipped on the dedi).
        // Verbose-gated.
        [HarmonyPatch(typeof(Player), "PlacePiece")]
        internal static class Player_PlacePiece_Diag
        {
            private static void Prefix(Player __instance, Piece piece)
            {
                if (piece == null || __instance == null) return;
                if (!FiresLogger.VerboseEnabled) return;
                var ghost = __instance.m_placementGhost;
                string n = ghost != null ? ghost.name : (piece.gameObject != null ? piece.gameObject.name : "");
                if (string.IsNullOrEmpty(n) || n.IndexOf("FiresDoor", System.StringComparison.Ordinal) < 0) return;
                if (ghost != null)
                    Debug.Log($"[ENTRYDOOR-DIAG] PlacePiece ghost='{ghost.name}' ghostPos={ghost.transform.position} " +
                              $"ghostRot={ghost.transform.rotation.eulerAngles} ghostScale={ghost.transform.lossyScale} playerPos={__instance.transform.position}");
                else
                    Debug.Log($"[ENTRYDOOR-DIAG] PlacePiece piece='{piece.gameObject.name}' NO GHOST playerPos={__instance.transform.position}");
            }
        }
    }
}
