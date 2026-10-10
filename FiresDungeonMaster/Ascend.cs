using System;
using System.Collections.Generic;
using System.Reflection;
using FiresCore.Logging;
using FiresDungeonMaster.Editor;
using HarmonyLib;
using UnityEngine;

namespace FiresDungeonMaster
{
    /// <summary>
    /// FiresDungeonMaster Phase 2 — footprint piece registration (Jotunn-free, mirroring FiresMausoleum /
    /// FiresWayshrines). Loads the 'firesdungeonmaster' bundle, attaches the runtime components to the baked
    /// empty "FiresDungeonFootprint" container at ZNetScene.Awake, and wires the resulting Piece into the
    /// Hammer's PieceTable under a custom "Dungeon Sections" category at ObjectDB.Awake.
    ///
    /// The empty-baked-prefab + runtime-attach scheme is the proven FiresEnvBox approach: a bare new GameObject
    /// in m_prefabs is the empty-AssetID trap, so the named container is baked into the bundle and the
    /// ZNetView / SectionFootprintController / Piece / BoxCollider are added here, AFTER the prefab is in
    /// m_prefabs by name.
    ///
    /// FAP coexistence: the "Dungeon Sections" category is obtained from FAP's OWN shared VACategoryRegistry via
    /// reflection when FAP is loaded (so the int is shared and FAP's Hud tab-strip patch renders our tab), with a
    /// self-contained fallback (FootprintCategoryRegistry + a Hud.Awake tab grow) when FAP is absent. We never
    /// patch Player.PlacePiece, so FAP's placement prefixes are untouched.
    /// </summary>
    public static class FootprintRegistration
    {
        internal const string BundleResourceName = "firesdungeonmaster";
        internal const string FootprintPrefabName = "FiresDungeonFootprint";
        internal const string DeskPrefabName = "FiresDungeonDesk";
        internal const string DungeonSectionsCategory = "Dungeon Sections";

        // The prefab(s) this mod owns: the footprint container (P2), the blueprint DESK (P2b — opens the section
        // browser on [E]), and the baked Location/Interior/DG/Portal template + dark-crypt env box (P6 generation,
        // mirroring the FiresMausoleum crypt bundle).
        private static readonly List<GameObject> _prefabs = new List<GameObject>();

        // Downloaded dungeon art (baked by FiresDungeonMasterBuilder.BuildDoorsAndKeys): the placeable entry-door
        // variants (hammer pieces; EntryDoorController is attached in Phase 2) and the two keys (spawnable props now;
        // ItemDrop-ified in Phase 2). Each carries a real Standard-shader "fdmpbr_*" material — NOT "_Copy" — so
        // FixShaders leaves it and it renders with the downloaded wood/iron/copper textures.
        internal static readonly string[] DoorPrefabNames = { "FiresDoorMedievalA", "FiresDoorMedievalB", "FiresDoorMedievalC", "FiresDoorArched" };
        internal static readonly string[] KeyPrefabNames = { "FiresKeyOld", "FiresKeyCopper", "FiresKeySkeleton", "FiresKeySkeletonOrnate" };
        // Real ItemDrop inventory items: the keys open a matching-key door; the lockpick opens the pick minigame.
        // (The two skeleton keys register the moment the rebuilt bundle carries them; loaders skip absent prefabs.)
        internal static readonly string[] ItemPrefabNames = { "FiresKeyOld", "FiresKeyCopper", "FiresKeySkeleton", "FiresKeySkeletonOrnate", "FiresLockpick" };
        private static readonly Dictionary<string, string> _pieceDisplayNames = new Dictionary<string, string>
        {
            { "FiresDoorMedievalA", "Dungeon Door — Iron-Banded" },
            { "FiresDoorMedievalB", "Dungeon Door — Arched Oak" },
            { "FiresDoorMedievalC", "Dungeon Door — Planked" },
            { "FiresDoorArched",    "Dungeon Door — Grand Arch" },
            { "FiresKeyOld",        "Old Dungeon Key" },
            { "FiresKeyCopper",     "Copper Dungeon Key" },
            { "FiresLockpick",      "Lockpick" },
        };

        // ── RPG item rarity ────────────────────────────────────────────────────────────────────────────────────
        // Each key/lockpick carries a rarity that colours its name and heads its tooltip (WoW-style ladder). Keys open
        // a matching door outright; higher-rarity keys are just rarer finds, not stronger. The lockpick is a plain tool.
        internal enum Rarity { None, Common, Uncommon, Rare, Epic, Unique }

        private static string RarityHex(Rarity r)
        {
            switch (r)
            {
                case Rarity.Common: return "#9d9d9d";
                case Rarity.Uncommon: return "#1eff00";
                case Rarity.Rare: return "#0070dd";
                case Rarity.Epic: return "#a335ee";
                case Rarity.Unique: return "#ff8000";
                default: return "#ffffff";
            }
        }

        private struct ItemMeta { public string Display; public string Desc; public Rarity Rarity; }

        // Display name + flavour + rarity for every FDM inventory item. New skeleton keys are added here alongside their
        // bundle prefabs (FiresKeySkeleton = Rare, FiresKeySkeletonOrnate = Unique).
        private static readonly Dictionary<string, ItemMeta> _itemMeta = new Dictionary<string, ItemMeta>
        {
            { "FiresLockpick", new ItemMeta {
                Display = "Lockpick", Rarity = Rarity.None,
                Desc = "A slender spring-steel pick for coaxing dungeon tumblers. Setting pins hones your Lockpicking — but a clumsy slip can snap it." } },
            { "FiresKeyOld", new ItemMeta {
                Display = "Old Dungeon Key", Rarity = Rarity.Common,
                Desc = "A rust-pitted iron key, older than the crypt it opens. Turns the lock of its matching dungeon door in a single turn." } },
            { "FiresKeyCopper", new ItemMeta {
                Display = "Copper Dungeon Key", Rarity = Rarity.Uncommon,
                Desc = "A verdigris-green copper key, its teeth still bright. Opens its matching dungeon door outright — no picking required." } },
            { "FiresKeySkeleton", new ItemMeta {
                Display = "Skeleton Key", Rarity = Rarity.Rare,
                Desc = "A blackened skeleton key filed to slip a great many wards. Opens its matching dungeon door without fuss." } },
            { "FiresKeySkeletonOrnate", new ItemMeta {
                Display = "Ornate Skeleton Key", Rarity = Rarity.Unique,
                Desc = "An ornate skeleton key wrought for one vault door alone — there is no other like it. Opens its matching dungeon door." } },
        };

        private static ItemMeta GetItemMeta(string name)
        {
            if (_itemMeta.TryGetValue(name, out var m)) return m;
            string disp = _pieceDisplayNames.TryGetValue(name, out var dn) ? dn : name;
            return new ItemMeta { Display = disp, Desc = "A key that opens a matching dungeon door.", Rarity = Rarity.Common };
        }

        private static readonly HashSet<string> _ownedPrefabNames = new HashSet<string>
        {
            FootprintPrefabName,
            DeskPrefabName,
            FiresDungeonMaster.Generation.DungeonMasterNames.LocationTemplate,
            FiresDungeonMaster.Generation.DungeonMasterNames.InteriorTemplate,
            FiresDungeonMaster.Generation.DungeonMasterNames.EnvBox,
            "EnvBox_vad",
            "FiresDoorMedievalA", "FiresDoorMedievalB", "FiresDoorMedievalC", "FiresDoorArched",
            "FiresKeyOld", "FiresKeyCopper", "FiresKeySkeleton", "FiresKeySkeletonOrnate", "FiresLockpick",
        };

        private static bool IsItem(string n) => Array.IndexOf(ItemPrefabNames, n) >= 0;
        private static bool IsDoor(string n) => Array.IndexOf(DoorPrefabNames, n) >= 0;


        private static AssetBundle _bundle;

        /// <summary>
        /// Baked bundle Sprite by name (e.g. "FiresDoorArched_icon"); null if absent/unloaded. One-time
        /// LoadAllAssets&lt;Sprite&gt; cache — the typed single-asset lookup (LoadAsset&lt;Sprite&gt;(name)) returns null when
        /// the Texture2D main asset shadows the Sprite sub-asset (bit FiresMausoleum's icons). Logs the names once.
        /// </summary>
        private static Dictionary<string, Sprite> _bundleSprites;

        internal static Sprite LoadBundleSprite(string name)
        {
            try
            {
                if (_bundleSprites == null)
                {
                    _bundleSprites = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
                    if (_bundle != null)
                        foreach (Sprite s in _bundle.LoadAllAssets<Sprite>())
                            if (s != null) _bundleSprites[s.name] = s;
                    FiresLogger.LogVerbose($"bundle sprites loaded: {_bundleSprites.Count} " +
                                           $"[{string.Join(", ", _bundleSprites.Keys)}]");
                }
                return _bundleSprites.TryGetValue(name, out Sprite sprite) ? sprite : null;
            }
            catch { return null; }
        }

        // ── bundle load (call from Plugin.Setup) ──────────────────────────────────

        internal static void LoadBundleAndPrefabs()
        {
            try
            {
                _bundle = GetAssetBundleFromResources(BundleResourceName);
                if (_bundle == null)
                {
                    FiresLogger.LogWarning("bundle not found — footprint piece will not place until the bundle ships.");
                    return;
                }
                CollectPrefabs(_bundle.LoadAllAssets<GameObject>());
                FiresLogger.LogInfo($"bundle loaded; {_prefabs.Count} footprint prefab(s).");
            }
            catch (Exception ex) { Debug.LogError($"[FiresDungeonMaster] bundle/prefab load failed: {ex}"); }
        }

        private static void CollectPrefabs(GameObject[] assets)
        {
            if (assets == null) return;
            foreach (GameObject p in assets)
            {
                if (p == null) continue;
                if (!_ownedPrefabNames.Contains(p.name)) continue;
                UnityEngine.Object.DontDestroyOnLoad(p);
                if (!_prefabs.Contains(p)) _prefabs.Add(p);
            }
        }

        private static AssetBundle GetAssetBundleFromResources(string name)
        {
            foreach (AssetBundle loaded in AssetBundle.GetAllLoadedAssetBundles())
                if (loaded != null && string.Equals(loaded.name, name, StringComparison.OrdinalIgnoreCase))
                    return loaded;

            Assembly asm = Assembly.GetExecutingAssembly();
            string resourceName = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
            if (resourceName == null) { Debug.LogError($"[FiresDungeonMaster] no embedded resource ending with '{name}'"); return null; }

            using (System.IO.Stream stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream == null) return null;
                using var ms = new System.IO.MemoryStream();
                stream.CopyTo(ms);
                return AssetBundle.LoadFromMemory(ms.ToArray());   // full copy — disposed-stream lazy re-read crashes the dedi
            }
        }

        /// <summary>
        /// Load a UI prefab (e.g. the cloned 'FiresSectionBrowserUI' section browser) by name from the already-loaded
        /// 'firesdungeonmaster' bundle. The bundle is shared with the footprint editor prefabs — this just resolves a
        /// GameObject out of it for the client-only UI to Instantiate. Null on a dedicated server (bundle never loads)
        /// or before the first bake. Used by FiresDungeonMaster.UI.SectionBrowserUI.Init.
        /// </summary>
        internal static GameObject LoadUiPrefab(string prefabName)
        {
            if (_bundle == null) LoadBundleAndPrefabs();
            if (_bundle == null) return null;
            return _bundle.LoadAsset<GameObject>(prefabName);
        }

        // ── ZNetScene.Awake: attach runtime components + register by name ──────────

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        internal static class ZNetScenePatches
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                try { RegisterPrefabs(); }
                catch (Exception ex) { Debug.LogError($"[FiresDungeonMaster] ZNetScene register failed: {ex}"); }
            }
        }

        [HarmonyPatch(typeof(ObjectDB), "Awake")]
        internal static class ObjectDBPatches
        {
            [HarmonyPostfix]
            private static void Postfix(ObjectDB __instance)
            {
                if (FiresCore.Pieces.HammerPieces.IsMainMenuObjectDB(__instance)) return;
                // Order matters: swap the "_Copy" box materials to live vanilla FIRST (the proven render path),
                // then wire the now-visible footprint piece into the Hammer.
                try { FixShaders(); }
                catch (Exception ex) { Debug.LogWarning($"[FiresDungeonMaster] shader fix skipped: {ex.Message}"); }
                try { AddPiecesToHammer(); }
                catch (Exception ex) { Debug.LogError($"[FiresDungeonMaster] Hammer register failed: {ex}"); }
                try { ConfigureItems(); }
                catch (Exception ex) { Debug.LogError($"[FiresDungeonMaster] item register failed: {ex}"); }
            }
        }

        // EXACT clone of FiresMausoleum.FixShaders — the proven render path for every Fires bundle prefab whose
        // surfaces carry "<vanilla>_Copy" materials. Warm() harvests every live vanilla shader+material off
        // ZNetScene, then ApplyToPrefab(..., AssetSwapMode.Both) replaces each "_Copy" material wholesale with the
        // matching LIVE vanilla material (real game shader + texture). This is why the crypt ROOMS render; the
        // footprint box now renders by the SAME method. Runs once per prefab on ObjectDB.Awake.
        private static void FixShaders()
        {
            EnsurePrefabsLoaded();
            FiresCore.Services.VanillaAssetResolver.RepairPrefabsOnce(_prefabs, FiresCore.Services.VanillaAssetResolver.AssetSwapMode.Both, "[FiresDungeonMaster]");
        }

        private static void RegisterPrefabs()
        {
            ZNetScene znet = ZNetScene.instance;
            if (znet == null) return;

            EnsurePrefabsLoaded();

            // Scrub fake-null refs left from a previous session before re-adding (leftover dead entries make other
            // mods NRE iterating m_prefabs / m_namedPrefabs).
            FiresCore.World.NetworkPrefabs.ScrubDeadEntries(znet);

            foreach (GameObject prefab in _prefabs)
            {
                if (prefab == null) continue;

                // The Location + Interior templates are NOT ZNetScene-registered here: they are cloned per
                // dungeon-type by DungeonMasterSpecBuilder, which renames their DG/Portal ZNetView children and
                // registers THOSE renamed names via RegisterLocationTemplateChildren. Keep the template alive only.
                if (prefab.name == FiresDungeonMaster.Generation.DungeonMasterNames.LocationTemplate ||
                    prefab.name == FiresDungeonMaster.Generation.DungeonMasterNames.InteriorTemplate)
                    continue;

                if (znet.GetPrefab(prefab.name) != null) continue;

                // Dark-crypt env box: the FiresMausoleum/FiresMemorial recipe — persistent ZNetView +
                // EnvironmentBoxController defaulted ON THE PREFAB to DungeonBlack + Hidden + no vanilla paint, then
                // RegisterByName so the Core EnsureEnvBox can spawn it by name. (Idempotent across relogin / dual-mod.)
                if (prefab.name == FiresDungeonMaster.Generation.DungeonMasterNames.EnvBox)
                {
                    ZNetView envNview = prefab.GetComponent<ZNetView>() ?? prefab.AddComponent<ZNetView>();
                    envNview.m_persistent = true;
                    var envCtrl = prefab.GetComponent<FiresCore.Utilities.EnvironmentBoxController>()
                                  ?? prefab.AddComponent<FiresCore.Utilities.EnvironmentBoxController>();
                    envCtrl.CurrentSkyboxMode = FiresCore.Utilities.EnvironmentBoxController.SkyboxMode.DungeonBlack;
                    envCtrl.CurrentVisibility = FiresCore.Utilities.EnvironmentBoxController.VisibilityMode.Hidden;
                    envCtrl.AffectsVanillaPaint = false;
                    FiresCore.World.NetworkPrefabs.RegisterByName(znet, prefab);
                    continue;
                }

                // The ORIGINAL RPGMaker hammer-placeable environment box, restored — mirrors the monolith's
                // SetupEnvironmentBoxPiece field-for-field: persistent Solid ZNetView with synced initial scale
                // (Shift+Scroll scaling), a Piece configured for clip-everything free placement, an indestructible
                // no-support WearNTear (hammer-removable), and the FUC EnvironmentBoxController at its original
                // defaults (10x5x10, scalable 2..200/100). Goes on the VANILLA hammer under Misc (AddPiecesToHammer
                // special-cases it), exactly as it used to. Same prefab name so old worlds' EnvBox_vad ZDOs resolve.
                if (prefab.name == "EnvBox_vad")
                {
                    ZNetView envVadNv = prefab.GetComponent<ZNetView>() ?? prefab.AddComponent<ZNetView>();
                    envVadNv.m_persistent = true;
                    envVadNv.m_type = ZDO.ObjectType.Solid;
                    envVadNv.m_syncInitialScale = true;

                    Piece envVadPiece = prefab.GetComponent<Piece>() ?? prefab.AddComponent<Piece>();
                    envVadPiece.m_name = "Environment Box";
                    envVadPiece.m_description = "Placeable environment volume: forces environment/biome/skybox inside, scalable, admin-configurable via hover controls.";
                    envVadPiece.m_allowedInDungeons = true;
                    envVadPiece.m_canBeRemoved = true;
                    envVadPiece.m_clipEverything = true;
                    envVadPiece.m_clipGround = true;
                    envVadPiece.m_groundPiece = false;
                    envVadPiece.m_groundOnly = false;
                    envVadPiece.m_cultivatedGroundOnly = false;
                    envVadPiece.m_waterPiece = false;
                    envVadPiece.m_noInWater = false;
                    envVadPiece.m_notOnWood = false;
                    envVadPiece.m_notOnTiltingSurface = false;
                    envVadPiece.m_notOnFloor = false;
                    envVadPiece.m_allowRotatedOverlap = true;
                    envVadPiece.m_onlyInBiome = Heightmap.Biome.None;
                    envVadPiece.m_enabled = true;
                    if (envVadPiece.m_resources == null) envVadPiece.m_resources = new Piece.Requirement[0];

                    var envVadWnt = prefab.GetComponent<WearNTear>() ?? prefab.AddComponent<WearNTear>();
                    envVadWnt.m_noRoofWear = false;
                    envVadWnt.m_noSupportWear = false;
                    envVadWnt.m_materialType = WearNTear.MaterialType.Stone;
                    envVadWnt.m_health = 1000f;
                    envVadWnt.m_damages = new HitData.DamageModifiers();
                    envVadWnt.m_hitNoise = 0f;
                    envVadWnt.m_destroyNoise = 0f;
                    envVadWnt.m_autoCreateFragments = false;
                    envVadWnt.m_supports = false;
                    envVadWnt.m_triggerPrivateArea = false;

                    var envVadCtrl = prefab.GetComponent<FiresCore.Utilities.EnvironmentBoxController>()
                                     ?? prefab.AddComponent<FiresCore.Utilities.EnvironmentBoxController>();
                    // Default = the VANILLA interior env box (64 x 500 x 64) = 100% on the panel's scale slider;
                    // Max allows the slider's full 1000%.
                    envVadCtrl.BoxSize = new Vector3(64f, 500f, 64f);
                    envVadCtrl.MinSize = new Vector3(2f, 2f, 2f);
                    envVadCtrl.MaxSize = new Vector3(640f, 5000f, 640f);

                    FiresCore.World.NetworkPrefabs.RegisterByName(znet, prefab);
                    continue;
                }

                // The blueprint DESK (P2b): a flavour piece that opens the Saved Section Browser on [E]. Registered
                // identically to the footprint — persistent ZNetView (so a placed desk owns + persists a ZDO), a
                // SOLID blocking collider on the 'piece' layer (which BOTH physically blocks the player AND is in
                // Player.m_interactMask, so you can't walk through the desk yet [E]/hover still resolve — unlike
                // 'piece_nonsolid', which hovers but is walk-through), and a DeskController for the hover + Interact.
                // DeskController.Awake re-fits this collider to the desk's renderer bounds on the placed instance
                // (Player.FindHoverObject needs the hit collider's OWN GameObject to carry the Hoverable AND the
                // collider to actually cover the visible table). Baked *_Copy materials are swapped to live vanilla
                // by FixShaders() at ObjectDB.Awake.
                if (prefab.name == DeskPrefabName)
                {
                    ZNetView deskNview = prefab.GetComponent<ZNetView>() ?? prefab.AddComponent<ZNetView>();
                    deskNview.m_persistent = true;

                    BoxCollider deskCol = prefab.GetComponent<BoxCollider>() ?? prefab.AddComponent<BoxCollider>();
                    deskCol.isTrigger = false;
                    int deskLayer = LayerMask.NameToLayer("piece");
                    if (deskLayer >= 0) prefab.layer = deskLayer;

                    if (prefab.GetComponent<FiresDungeonMaster.Editor.DeskController>() == null)
                        prefab.AddComponent<FiresDungeonMaster.Editor.DeskController>();

                    FiresCore.World.NetworkPrefabs.RegisterByName(znet, prefab);
                    continue;
                }

                // Downloaded DOOR / KEY prefabs (Phase 1 art): persistent ZNetView so a placed door/key owns a ZDO,
                // and the baked "Body" MeshCollider promoted to the 'piece' layer (solid + hoverable + placeable —
                // like the desk). Doors gain the EntryDoorController + keys become ItemDrops in Phase 2; here they are
                // just valid, renderable, placeable/spawnable game objects. Their fdmpbr_* materials are NOT "_Copy",
                // so FixShaders leaves them and they render with the downloaded textures.
                if (IsDoor(prefab.name))
                {
                    ZNetView dv = prefab.GetComponent<ZNetView>() ?? prefab.AddComponent<ZNetView>();
                    dv.m_persistent = true;
                    FiresCore.Pieces.HammerPieces.SetLayerRecursive(prefab, LayerMask.NameToLayer("piece"));
                    if (prefab.GetComponent<FiresDungeonMaster.Editor.EntryDoorController>() == null)
                        prefab.AddComponent<FiresDungeonMaster.Editor.EntryDoorController>();
                    FiresCore.World.NetworkPrefabs.RegisterByName(znet, prefab);
                    continue;
                }

                // Keys + lockpick → real ItemDrop items (physics drop + pickup). m_itemData.m_shared is filled at
                // ObjectDB.Awake (ConfigureItems) once a vanilla item is available to clone.
                if (IsItem(prefab.name))
                {
                    SetupItemPrefab(prefab);
                    FiresCore.World.NetworkPrefabs.RegisterByName(znet, prefab);
                    continue;
                }

                // The footprint editor container (P2): persistent ZNetView so placed footprints own + persist a ZDO.
                ZNetView nview = prefab.GetComponent<ZNetView>();
                if (nview == null) nview = prefab.AddComponent<ZNetView>();
                nview.m_persistent = true;

                // SOLID (non-trigger) build-box collider on the 'piece_nonsolid' layer. Vanilla
                // Player.UpdatePlacementGhost derives the ghost's snap offset from the closest point on a NON-trigger
                // convex collider; a trigger-only box leaves that offset at zero and the ghost collapses to
                // (2*aimPoint + normal*50), so the footprint lands at ~double the aim coords ~225m away. 'piece_nonsolid'
                // is in m_placeRayMask + m_interactMask (place-ray + hover hit it) but does not physically block the
                // player. The controller re-asserts this in Awake; setting it on the prefab keeps the ghost correct too.
                BoxCollider col = prefab.GetComponent<BoxCollider>();
                if (col == null) col = prefab.AddComponent<BoxCollider>();
                col.isTrigger = false;
                int pieceNonsolidLayer = LayerMask.NameToLayer("piece_nonsolid");
                if (pieceNonsolidLayer >= 0) prefab.layer = pieceNonsolidLayer;

                // The footprint editor controller (Core EnvironmentBoxController clone).
                if (prefab.GetComponent<SectionFootprintController>() == null)
                    prefab.AddComponent<SectionFootprintController>();

                // The box renderers carry "<vanilla>_Copy" materials baked by FiresDungeonMasterBuilder (stone_Copy /
                // stonefloor_2x2_Copy). They are swapped to the LIVE vanilla materials by FixShaders() at ObjectDB.Awake
                // via FiresCore.Services.VanillaAssetResolver.ApplyToPrefab(..., Both) — the EXACT proven path that makes
                // the FiresMausoleum crypt ROOMS render in-game. No manual per-piece material grab here.

                FiresCore.World.NetworkPrefabs.RegisterByName(znet, prefab);
            }
        }


        // ── ItemDrop items (keys + lockpick) ───────────────────────────────────────────────────────────────────
        // ZNetScene time: give the baked mesh prefab what a dropped item needs (ZNetView, Rigidbody, convex collider,
        // ItemDrop). The baked non-convex MeshCollider is disabled (invalid with a non-kinematic Rigidbody). The
        // ItemDrop's m_itemData.m_shared is filled at ObjectDB.Awake (ConfigureItems), before any instance spawns.
        private static void SetupItemPrefab(GameObject prefab)
        {
            var nv = prefab.GetComponent<ZNetView>() ?? prefab.AddComponent<ZNetView>();
            nv.m_persistent = true;

            int itemLayer = LayerMask.NameToLayer("item");
            if (itemLayer >= 0) FiresCore.Pieces.HammerPieces.SetLayerRecursive(prefab, itemLayer);

            foreach (var mc in prefab.GetComponentsInChildren<MeshCollider>(true)) mc.enabled = false;
            if (prefab.GetComponent<BoxCollider>() == null)
            {
                var bc = prefab.AddComponent<BoxCollider>();
                bc.size = new Vector3(0.35f, 0.2f, 0.35f); bc.center = new Vector3(0f, 0.1f, 0f);
            }
            var rb = prefab.GetComponent<Rigidbody>() ?? prefab.AddComponent<Rigidbody>();
            rb.mass = 1f; rb.useGravity = true;

            if (prefab.GetComponent<ItemDrop>() == null) prefab.AddComponent<ItemDrop>();
        }

        private static readonly HashSet<string> _itemsConfigured = new HashSet<string>();

        private static void ConfigureItems()
        {
            if (ObjectDB.instance == null) return;
            EnsurePrefabsLoaded();
            var basePrefab = ObjectDB.instance.GetItemPrefab("Wood") ?? ObjectDB.instance.GetItemPrefab("Coins");
            var baseShared = basePrefab != null ? basePrefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
            if (baseShared == null) { Debug.LogWarning("[FiresDungeonMaster] no base item to clone item data from"); return; }

            var hashField = typeof(ObjectDB).GetField("m_itemByHash", BindingFlags.NonPublic | BindingFlags.Instance);
            var itemByHash = hashField?.GetValue(ObjectDB.instance) as Dictionary<int, GameObject>;

            foreach (var name in ItemPrefabNames)
            {
                var prefab = _prefabs.Find(p => p != null && p.name == name);
                if (prefab == null) continue;
                var id = prefab.GetComponent<ItemDrop>();
                if (id == null) continue;

                if (_itemsConfigured.Add(name))
                {
                    var meta = GetItemMeta(name);
                    string hex = RarityHex(meta.Rarity);
                    var shared = CloneShared(baseShared);
                    // Rarity colours the name and heads the tooltip (WoW-style); a tool (None) stays plain white.
                    shared.m_name = meta.Rarity == Rarity.None ? meta.Display : $"<color={hex}>{meta.Display}</color>";
                    shared.m_description = meta.Rarity == Rarity.None
                        ? meta.Desc
                        : $"<color={hex}><b>{meta.Rarity}</b></color>\n{meta.Desc}";
                    shared.m_itemType = ItemDrop.ItemData.ItemType.Material;
                    shared.m_maxStackSize = name == "FiresLockpick" ? 10 : 20;
                    shared.m_weight = 0.2f;
                    shared.m_maxQuality = 1;
                    shared.m_teleportable = true;
                    shared.m_questItem = false;
                    // Placeholder chip for now — the REAL render is deferred to the safe-to-render window. ObjectDB.Awake
                    // fires mid-loading-screen, where Camera.Render + ReadPixels captures uninitialized VRAM (the
                    // rainbow-noise icon bug). Same gate FAP's VAPieceIconRenderer uses for its piece icons.
                    shared.m_icons = new Sprite[] { GenIcon(name) };
                    id.m_itemData.m_shared = shared;
                    id.m_itemData.m_stack = 1;
                    id.m_itemData.m_quality = 1;
                    id.m_itemData.m_durability = 100f;
                    id.m_itemData.m_dropPrefab = prefab;
                }

                if (ObjectDB.instance.GetItemPrefab(name) == null)
                {
                    ObjectDB.instance.m_items.Add(prefab);
                    if (itemByHash != null) { int h = name.GetStableHashCode(); if (!itemByHash.ContainsKey(h)) itemByHash[h] = prefab; }
                    FiresLogger.LogVerbose($"registered item '{name}' to ObjectDB.");
                }
            }

            // Real transparent icons (FUC ItemIconRenderer) once it is actually SAFE to render, then the skill icon.
            ScheduleItemIconRenders();
        }

        // Item icons must NOT render during the loading screen: Camera.Render + ReadPixels there reads uninitialized
        // VRAM and every icon comes out as rainbow noise. Mirror FAP's VAPieceIconRenderer gate — wait for a live,
        // non-teleporting local player and a streamed-in active area, give the spawn-finalization frames some slack,
        // THEN render one icon per frame, swapping each sprite into the item's EXISTING m_icons array (every held
        // stack shares that array, so inventories update in place).
        private static bool _iconRendersScheduled;
        private const int IconSlackFrames = 30;

        private static void ScheduleItemIconRenders()
        {
            if (_iconRendersScheduled) return;
            var plugin = FiresDungeonMasterPlugin.Instance;
            if (plugin == null) return;
            _iconRendersScheduled = true;
            plugin.StartCoroutine(RenderItemIconsWhenSafe());
        }

        private static bool IsSafeToRender()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return false;
            var p = Player.m_localPlayer;
            if (p == null || p.IsTeleporting()) return false;
            if (ZNetScene.instance == null || ZoneSystem.instance == null) return false;
            return ZoneSystem.instance.IsActiveAreaLoaded();
        }

        private static System.Collections.IEnumerator RenderItemIconsWhenSafe()
        {
            while (!IsSafeToRender()) yield return null;
            for (int i = 0; i < IconSlackFrames; i++) yield return null;

            foreach (var name in ItemPrefabNames)
            {
                var prefab = _prefabs.Find(p => p != null && p.name == name);
                var shared = prefab?.GetComponent<ItemDrop>()?.m_itemData?.m_shared;
                if (shared == null || shared.m_icons == null || shared.m_icons.Length == 0) continue;
                Sprite real = null;
                try { real = FiresCore.UI.ItemIconRenderer.Render(prefab); }
                catch (Exception ex) { FiresLogger.LogWarning($"icon render '{name}': {ex.Message}"); }
                if (real != null) shared.m_icons[0] = real;
                else FiresLogger.LogWarning($"icon render '{name}' returned null — keeping the placeholder chip.");
                yield return null;
            }

            // Give the Lockpicking skill the lockpick's real inventory icon (Register is idempotent).
            var lockpickIcons = _prefabs.Find(p => p != null && p.name == "FiresLockpick")
                ?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_icons;
            if (lockpickIcons != null && lockpickIcons.Length > 0 && lockpickIcons[0] != null)
                FiresDungeonMaster.Editor.LockpickSkill.Register(lockpickIcons[0]);
            FiresLogger.LogInfo("item icons rendered post-load (safe-to-render gate).");
        }

        // Shallow field-copy of a vanilla SharedData (its lists shared as read-only bases) → a fresh instance to override.
        private static ItemDrop.ItemData.SharedData CloneShared(ItemDrop.ItemData.SharedData src)
        {
            var dst = new ItemDrop.ItemData.SharedData();
            foreach (var f in typeof(ItemDrop.ItemData.SharedData).GetFields(BindingFlags.Public | BindingFlags.Instance))
                try { f.SetValue(dst, f.GetValue(src)); } catch { }
            return dst;
        }

        // Placeholder icon (coloured chip) so the item renders in the grid without an NRE. Mesh-rendered icons later.
        private static readonly Dictionary<string, Sprite> _iconCache = new Dictionary<string, Sprite>();
        private static Sprite GenIcon(string name)
        {
            if (_iconCache.TryGetValue(name, out var s) && s != null) return s;
            Color c = name == "FiresLockpick" ? new Color(0.72f, 0.74f, 0.78f)
                    : name == "FiresKeyCopper" ? new Color(0.80f, 0.52f, 0.28f)
                    : new Color(0.42f, 0.42f, 0.46f);
            const int sz = 64;
            var tex = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
            var px = new Color[sz * sz];
            for (int y = 0; y < sz; y++)
                for (int x = 0; x < sz; x++)
                {
                    float dx = (x - sz / 2f) / (sz / 2f), dy = (y - sz / 2f) / (sz / 2f);
                    float m = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));
                    px[y * sz + x] = m < 0.82f ? (m > 0.72f ? c * 0.55f : c) : new Color(0, 0, 0, 0);
                }
            tex.SetPixels(px); tex.Apply();
            var sp = Sprite.Create(tex, new Rect(0, 0, sz, sz), new Vector2(0.5f, 0.5f), 100f);
            _iconCache[name] = sp;
            return sp;
        }

        // ── ObjectDB.Awake: wire the Piece into the Hammer under "Dungeon Sections" ─

        private static void AddPiecesToHammer()
        {
            if (ObjectDB.instance == null || ZNetScene.instance == null) return;

            EnsurePrefabsLoaded(); // heal stale prefabs regardless of ObjectDB.Awake vs ZNetScene.Awake order

            PieceTable table = FiresCore.Pieces.HammerPieces.HammerTable();
            if (table == null) { Debug.LogWarning("[FiresDungeonMaster] Hammer PieceTable not ready"); return; }

            // Allocate / register the custom category BEFORE assigning it to any piece and BEFORE
            // PieceTable.UpdateAvailable runs (which would otherwise size m_availablePieces to 8 and throw).
            Piece.PieceCategory category = CategoryProvider.GetOrAddDungeonSections(table);

            foreach (GameObject prefab in _prefabs)
            {
                if (prefab == null) continue;
                if (IsItem(prefab.name)) continue;   // keys + lockpick are inventory items, not hammer build pieces
                // The generation TEMPLATES + env box are internal (cloned/spawned by the engine), never hammer pieces.
                if (prefab.name == FiresDungeonMaster.Generation.DungeonMasterNames.LocationTemplate ||
                    prefab.name == FiresDungeonMaster.Generation.DungeonMasterNames.InteriorTemplate ||
                    prefab.name == FiresDungeonMaster.Generation.DungeonMasterNames.EnvBox) continue;

                if (ZNetScene.instance.GetPrefab(prefab.name) == null) RegisterPrefabs();

                Piece piece = prefab.GetComponent<Piece>();
                if (piece == null) piece = prefab.AddComponent<Piece>();
                if (string.IsNullOrEmpty(piece.m_name))
                    piece.m_name = _pieceDisplayNames.TryGetValue(prefab.name, out var dn) ? dn
                                 : (prefab.name == DeskPrefabName ? "Dungeon Blueprint Desk" : "Dungeon Section Footprint");
                // The restored EnvBox_vad lives on the vanilla Misc tab (its original home), not our custom tab.
                piece.m_category = prefab.name == "EnvBox_vad" ? Piece.PieceCategory.Misc : category;
                piece.m_enabled = true;
                piece.m_canBeRemoved = true;
                // Free to place (no build cost); no crafting station required.
                if (piece.m_resources == null) piece.m_resources = new Piece.Requirement[0];
                // Baked hammer icon from the bundle (builder BakePieceIcons); replaces the default red-shield chip.
                if (piece.m_icon == null) piece.m_icon = LoadBundleSprite(prefab.name + "_icon");

                if (FiresCore.Pieces.HammerPieces.AddToTable(table, prefab))
                    FiresLogger.LogVerbose($"added {prefab.name} to Hammer ('{DungeonSectionsCategory}')");
            }

            // 0.1.124 (Fire): every vanilla dungeon room, one hammer tab per dungeon type.
            VanillaRoomPieces.AddToHammer(table, label => CategoryProvider.GetOrAdd(table, label));
            // 0.1.125 (Fire): dungeon presets on the hammer right-click (FAP hook) + the presets window's key.
            DungeonPresetMenu.TryRegister();
            FiresDungeonMaster.UI.DungeonPresetUI.Ensure();
        }

        // ── ADMIN GATE for the build menu ─────────────────────────────────────────────────────────────────────
        // Dungeon authoring is an ADMIN-ONLY function: doors, footprint, desk and the environment box must never be
        // placeable by regular players. The pieces stay registered for everyone (ZDO resolution + ghosts of admin-
        // placed instances need the prefabs), but this postfix strips them from every category's AVAILABLE list for
        // non-admins, so they never appear in the build menu. UpdateAvailable re-runs on every hammer open /
        // category switch, so an op/deop takes effect immediately — no relog needed. Admin check = the family-
        // proven client gate (server/host, else the Core admin bridge).
        private static readonly HashSet<string> _adminOnlyPieceNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "FiresDoorMedievalA", "FiresDoorMedievalB", "FiresDoorMedievalC", "FiresDoorArched",
            FootprintPrefabName,
            DeskPrefabName,
            "EnvBox_vad",
        };

        [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
        internal static class PieceTable_UpdateAvailable_AdminGate
        {
            [HarmonyPostfix]
            [HarmonyPriority(HarmonyLib.Priority.Last)]   // after every tab-grow/injector postfix, so nothing re-adds
            private static void Postfix(PieceTable __instance)
            {
                if (__instance == null) return;
                if (Editor.SectionFootprintController.IsAdmin()) return;

                // 1.0: m_availablePieces is a FLAT HashSet<Piece>; the per-category lists this walks are
                // the private m_availablePiecesByCategory, reached through Core.
                var byCategory = FiresCore.Bridge.PieceTableAccess.ByCategory(__instance);
                if (byCategory == null) return;

                foreach (var categoryList in byCategory)
                {
                    if (categoryList == null) continue;
                    categoryList.RemoveAll(p => p != null && (_adminOnlyPieceNames.Contains(p.gameObject.name) || VanillaRoomPieces.IsRoomPiece(p.gameObject.name)));
                }
            }
        }

        // Re-acquire bundle prefabs if a relogin's Resources.UnloadUnusedAssets collected them (DontDestroyOnLoad
        // is a no-op on bundle assets), else placed footprint ZDOs stop spawning until a full restart.
        private static void EnsurePrefabsLoaded()
        {
            if (_bundle == null) return;
            if (_prefabs.Count > 0 && !_prefabs.Exists(p => p == null)) return;
            _prefabs.RemoveAll(p => p == null);
            CollectPrefabs(_bundle.LoadAllAssets<GameObject>());
            FiresLogger.LogInfo($"re-acquired footprint prefabs after relogin ({_prefabs.Count}).");
        }

        // ── P6 generation helpers ──────────────────────────────────────────────────

        /// <summary>
        /// The baked Location/DG/Portal TEMPLATE asset (DungeonMasterNames.LocationTemplate). DungeonMasterSpecBuilder
        /// clones it once per dungeon-type. Null until the bundle is loaded (rebuild the 'firesdungeonmaster' bundle).
        /// </summary>
        internal static GameObject GetLocationTemplate()
        {
            EnsurePrefabsLoaded();
            return _prefabs.Find(p => p != null && p.name == FiresDungeonMaster.Generation.DungeonMasterNames.LocationTemplate);
        }

        /// <summary>
        /// Register a per-type Location clone's renamed DG + Portal ZNetView children into ZNetScene by name, so
        /// ZoneSystem.SpawnLocation's per-piece ZDOs (keyed by GetPrefabName().GetStableHashCode()) resolve on reload.
        /// Mirrors FiresMausoleum.RegisterCryptChildren. Idempotent (RegisterByName no-ops if already present).
        /// </summary>
        internal static void RegisterLocationTemplateChildren(GameObject locationClone, string dgName, string portalName)
        {
            ZNetScene znet = ZNetScene.instance;
            if (znet == null || locationClone == null) return;

            foreach (Transform child in locationClone.GetComponentsInChildren<Transform>(true))
            {
                if (child == null || child.gameObject == locationClone) continue;
                string n = child.gameObject.name;
                if (n == dgName || n == portalName)
                    FiresCore.World.NetworkPrefabs.RegisterByName(znet, child.gameObject);
            }
        }

        /// <summary>
        /// Attach the Core per-peer doorway-blocker resolver to a generated room GameObject. DungeonGenerator.PlaceRoom
        /// Instantiates the room ROOT, so the controller rides every placed clone (server + every client) and opens
        /// entrance/paired doorways while leaving unused ones solid — this is what makes spec.BakeDoorBlockers
        /// load-bearing for generated dungeons. Idempotent.
        /// </summary>
        internal static void AttachDoorBlockerToRoom(GameObject room)
        {
            if (room == null) return;
            if (room.GetComponent<FiresCore.Dungeon.DungeonDoorBlockerController>() == null)
                room.AddComponent<FiresCore.Dungeon.DungeonDoorBlockerController>();
        }
    }

    /// <summary>
    /// Resolves the "Dungeon Sections" PieceCategory. Prefers FAP's shared VACategoryRegistry (so the category
    /// int is shared across FAP + FiresDungeonMaster and FAP's Hud tab-strip patch draws the tab); falls back to a
    /// self-contained registry + Hud tab grow when FAP is absent. Either way the category is added to the table's
    /// m_categories / m_categoryLabels and m_availablePieces is pre-grown so vanilla's index lookups never throw.
    /// </summary>
    internal static class CategoryProvider
    {
        // Cached reflection handle to FAP's VACategoryRegistry.GetOrAdd(string), or null when FAP is not loaded.
        private static MethodInfo _fapGetOrAdd;
        private static bool _fapProbed;

        // 0.1.124: any named tab (the vanilla room tabs), through FAP's registry when present.
        internal static Piece.PieceCategory GetOrAdd(PieceTable table, string label)
        {
            ProbeFap();
            Piece.PieceCategory cat;
            object value = null;
            if (_fapGetOrAdd != null)
                try { value = _fapGetOrAdd.Invoke(null, new object[] { label }); } catch (Exception ex) { Debug.LogWarning($"[FiresDungeonMaster] FAP category '{label}' failed: {ex.Message}"); }
            cat = value is Piece.PieceCategory pc ? pc : value != null ? (Piece.PieceCategory)Convert.ToInt32(value) : FootprintCategoryRegistry.GetOrAdd(label);
            AddCategoryToTable(table, cat, label);
            return cat;
        }

        internal static Piece.PieceCategory GetOrAddDungeonSections(PieceTable table)
        {
            Piece.PieceCategory cat = ResolveCategory(table);
            AddCategoryToTable(table, cat, FootprintRegistration.DungeonSectionsCategory);
            return cat;
        }

        private static Piece.PieceCategory ResolveCategory(PieceTable table)
        {
            ProbeFap();
            if (_fapGetOrAdd != null)
            {
                try
                {
                    object value = _fapGetOrAdd.Invoke(null, new object[] { FootprintRegistration.DungeonSectionsCategory });
                    if (value is Piece.PieceCategory pc) return pc;
                    return (Piece.PieceCategory)Convert.ToInt32(value);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FiresDungeonMaster] FAP VACategoryRegistry.GetOrAdd failed, using fallback: {ex.Message}");
                }
            }
            return FootprintCategoryRegistry.GetOrAdd(FootprintRegistration.DungeonSectionsCategory);
        }

        private static void ProbeFap()
        {
            if (_fapProbed) return;
            _fapProbed = true;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType("VerdantsAscent.Patches.VACategoryRegistry", throwOnError: false); }
                catch { t = null; }
                if (t == null) continue;
                _fapGetOrAdd = t.GetMethod("GetOrAdd", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (_fapGetOrAdd != null)
                {
                    FiresLogger.LogVerbose("using FAP VACategoryRegistry for 'Dungeon Sections'.");
                    return;
                }
            }
        }

        // Add the category to the table once, and ALWAYS pre-grow m_availablePieces to (int)cat+1 so vanilla's
        // per-frame index lookups (which run before UpdateAvailable) cannot crash. Idempotent.
        private static void AddCategoryToTable(PieceTable table, Piece.PieceCategory cat, string label)
        {
            int needed = (int)cat + 1;
            // 1.0 SPLIT this field: m_availablePieces is now a FLAT HashSet<Piece> and the per-category
            // lists moved to the private m_availablePiecesByCategory. PieceTableAccess is the family's
            // one reader of it - going direct compiles against the publicized assembly and then throws
            // FieldAccessException on first touch.
            var byCategory = FiresCore.Bridge.PieceTableAccess.ByCategory(table);
            if (byCategory != null)
                while (byCategory.Count < needed)
                    byCategory.Add(new List<Piece>());

            if (!table.m_categories.Contains(cat))
            {
                table.m_categories.Add(cat);
                table.m_categoryLabels.Add(label);
            }
        }
    }

    /// <summary>
    /// Fallback custom-category allocator used only when FAP is absent. Allocates ints above
    /// Piece.PieceCategory.Max (mirrors FAP's VACategoryRegistry numbering), so a world that later adds FAP keeps
    /// disjoint values. A Hud.Awake postfix grows the tab array so the tab is clickable without FAP.
    /// </summary>
    internal static class FootprintCategoryRegistry
    {
        private static readonly Dictionary<string, Piece.PieceCategory> _byName =
            new Dictionary<string, Piece.PieceCategory>(StringComparer.Ordinal);
        private static int _next = (int)Piece.PieceCategory.Max + 1; // 9

        internal static Piece.PieceCategory GetOrAdd(string name)
        {
            if (_byName.TryGetValue(name, out var existing)) return existing;
            var value = (Piece.PieceCategory)(_next++);
            _byName[name] = value;
            return value;
        }

        // Belt-and-suspenders: PieceTable.UpdateAvailable sizes m_availablePieces to exactly 8, so a piece with our
        // custom category (>=9) throws unless the list is pre-grown. This prefix grows it to the highest in-use
        // category. When FAP is present its own identical guard already ran — this is a cheap no-op then.
        [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
        internal static class PieceTable_UpdateAvailable_Grow_Patch
        {
            [HarmonyPrefix]
            private static void Prefix(PieceTable __instance) => EnsureGrown(__instance);
        }

        private static void EnsureGrown(PieceTable table)
        {
            if (table == null || table.m_availablePieces == null || table.m_categories == null) return;
            int needed = 8;
            for (int i = 0; i < table.m_categories.Count; i++)
            {
                int v = (int)table.m_categories[i];
                if (v >= (int)Piece.PieceCategory.All) continue;
                if (v + 1 > needed) needed = v + 1;
            }
            // 1.0 SPLIT this field: m_availablePieces is now a FLAT HashSet<Piece> and the per-category
            // lists moved to the private m_availablePiecesByCategory. PieceTableAccess is the family's
            // one reader of it - going direct compiles against the publicized assembly and then throws
            // FieldAccessException on first touch.
            var byCategory = FiresCore.Bridge.PieceTableAccess.ByCategory(table);
            if (byCategory != null)
                while (byCategory.Count < needed)
                    byCategory.Add(new List<Piece>());
        }

        // Grow the Hud's category-tab array so 'Dungeon Sections' has a clickable tab when FAP isn't present to do
        // it. When FAP IS present, FAP's own Hud.Awake patch already grew + laid out the tabs; this then sees
        // enough slots and no-ops.
        [HarmonyPatch(typeof(Hud), "Awake")]
        internal static class Hud_Awake_GrowTabs_Patch
        {
            [HarmonyPostfix]
            private static void Postfix(Hud __instance)
            {
                try { GrowTabs(__instance); }
                catch (Exception ex) { Debug.LogWarning($"[FiresDungeonMaster] Hud tab grow skipped: {ex.Message}"); }
            }
        }

        private static void GrowTabs(Hud hud)
        {
            if (hud == null || hud.m_pieceCategoryTabs == null || hud.m_pieceCategoryTabs.Length == 0) return;

            PieceTable table = FiresCore.Pieces.HammerPieces.HammerTable();
            if (table == null) return;

            int needed = table.m_categories.Count;
            int have = hud.m_pieceCategoryTabs.Length;
            if (needed <= have) return; // FAP (or vanilla) already has enough tabs

            GameObject template = hud.m_pieceCategoryTabs[0];
            if (template == null) return;
            Transform parent = template.transform.parent;

            var expanded = new GameObject[needed];
            Array.Copy(hud.m_pieceCategoryTabs, expanded, have);
            for (int i = have; i < needed; i++)
            {
                GameObject copy = UnityEngine.Object.Instantiate(template, parent);
                copy.name = $"PieceCategoryTab_FDM_{i}";
                int captured = i;
                var handler = copy.GetComponent<UIInputHandler>();
                if (handler != null)
                {
                    handler.m_onLeftDown = null;
                    handler.m_onLeftDown += (_) =>
                    {
                        if (Player.m_localPlayer != null) Player.m_localPlayer.SetBuildCategory(captured);
                    };
                }
                copy.SetActive(true);
                expanded[i] = copy;
            }
            hud.m_pieceCategoryTabs = expanded;
        }
    }
}
