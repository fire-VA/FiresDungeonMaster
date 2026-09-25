using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Bridge;
using FiresCore.Dungeon;
using FiresCore.Net;
using FiresCore.Utilities;
using FiresDungeonMaster.Sections;
using HarmonyLib;
using UnityEngine;

namespace FiresDungeonMaster.Generation
{
    /// <summary>
    /// The P6 GENERATION recipe (CORRECTED to mirror FiresMausoleum EXACTLY): build a runtime <see cref="DungeonSpec"/>
    /// from ALL captured sections sharing one DUNGEON-TYPE name, and register it through the existing FiresDungeon.Core
    /// engine (the SAME engine the crypt rides).
    ///
    /// FiresMausoleum ships ONE crypt Location as a baked bundle prefab and never clones/activates it — its DG/Portal
    /// ZNetView children carry activeSelf==true, which is all ZoneSystem.SpawnLocation's
    /// Utils.GetEnabledComponentsInChildren&lt;ZNetView&gt; checks (it reads activeSelf, NOT activeInHierarchy — verified
    /// against ZoneSystem.cs:1355 + the activeSelf gate at the per-piece Instantiate). FiresDungeonMaster authors MANY
    /// dungeon-types from ONE baked Location template, so it clones the template once per type:
    ///   • NetworkObjectHelper.InstantiateInactiveClone the baked Location (parented under the permanently-inactive
    ///     Fires_PrefabHolder, so its ZNetViews never Awake — no rogue ZNetScene instance, NO ghost-ZDO leak),
    ///   • rename the DG child to FiresDM_&lt;type&gt;DG so FiresDungeonRegistry.MatchForDg dispatches THIS type's rooms,
    ///   • set dg.m_themes, attach FiresDungeonPortalLink, register the renamed DG + Portal ZNetView children into
    ///     ZNetScene by name (FootprintRegistration.RegisterLocationTemplateChildren) so SpawnLocation's per-piece
    ///     ZDOs resolve on reload — exactly Ascend.RegisterCryptChildren,
    ///   • LEAVE IT INACTIVE under the holder (NEVER SetActive). Its DG/Portal children keep activeSelf==true, so
    ///     GetEnabledComponentsInChildren still enumerates them; SpawnLocation's own Instantiate of those children is
    ///     what creates the live persistent ZDOs. No SetActive/ghost-init dance on the template.
    /// Only the ROOMS stay fully runtime (they legitimately have no ZNetView and live only in DungeonDB).
    /// </summary>
    internal static class DungeonMasterSpecBuilder
    {
        // One built spec per dungeon-type name (Ordinal-ignorecase so "Crypt"/"crypt" share a spec/DG).
        private static readonly Dictionary<string, DungeonSpec> _specs =
            new Dictionary<string, DungeonSpec>(StringComparer.OrdinalIgnoreCase);

        // The per-type INACTIVE Location template clone (kept alive under Fires_PrefabHolder so the SoftReference
        // asset stays valid). Never activated — its DG/Portal children's activeSelf==true is all the engine needs.
        private static readonly Dictionary<string, GameObject> _locationClones =
            new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);

        // Yaw applied to the interior exit arch so its opening faces the teleport landing (baked 180° was 90° off).
        // If it lands the wrong way in-game, flip this 90 <-> 270.
        private const float ExitArchYaw = 90f;

        // NAMED themes only (EWD drops unnamed bits). Picked deterministically per dungeon-type; isolation is by
        // DgNamePrefix (SetupAvailableRooms), so two types may share a theme harmlessly.
        private static readonly Room.Theme[] NamedThemes =
        {
            Room.Theme.Crypt,
            Room.Theme.SunkenCrypt,
            Room.Theme.Cave,
            Room.Theme.ForestCrypt,
            Room.Theme.GoblinCamp,
            Room.Theme.MeadowsVillage,
            Room.Theme.DvergerTown,
            Room.Theme.AshlandRuins,
            Room.Theme.FortressRuins,
        };

        // Vanilla dungeon LABEL (from EntryDoorController.VanillaDungeons) → the stock Room.Theme whose DungeonDB rooms
        // Valheim tags with that theme. We spawn our OWN Location/DG/portal template but leave RoomPrefabNames empty, so
        // the Core's SetupAvailableRooms postfix is a no-op and the DG keeps the stock rooms vanilla populated for the
        // theme. ("Infested Mine" has no dedicated Room.Theme in this build → the closest stock rooms are DvergerTown.)
        private static readonly Dictionary<string, Room.Theme> VanillaThemes =
            new Dictionary<string, Room.Theme>(StringComparer.OrdinalIgnoreCase)
            {
                { "Forest Crypt",  Room.Theme.ForestCrypt },
                { "Sunken Crypt",  Room.Theme.SunkenCrypt },
                // Room.Theme.Crypt (bit 1) is a legacy theme with NO stock rooms in the current build — the actual
                // burial-crypt rooms (Crypt2/3/4) are tagged ForestCrypt. Mapping "Meadows Crypt" there gave 0 rooms
                // → vanilla FindStartRoom IndexOutOfRange. There is no meadows-specific crypt in vanilla, so it borrows
                // the forest-crypt room set (which is what its rooms actually are).
                { "Meadows Crypt", Room.Theme.ForestCrypt },
                { "Frost Cave",    Room.Theme.Cave },
                { "Troll Cave",    Room.Theme.Cave },
                { "Infested Mine", Room.Theme.DvergerTown },
                { "Goblin Camp",   Room.Theme.GoblinCamp },
                { "Ashlands Ruins", Room.Theme.AshlandRuins },
                { "Fortress Ruins", Room.Theme.FortressRuins },
            };

        internal static string DgNamePrefixFor(string dungeonType) =>
            "FiresDM_" + Sanitize(dungeonType) + "DG";

        internal static string LocationNameFor(string dungeonType) =>
            "FiresDM_" + Sanitize(dungeonType) + "Location";

        internal static string PortalNameFor(string dungeonType) =>
            "FiresDM_" + Sanitize(dungeonType) + "Portal";

        /// <summary>Resolve the spec for a type if already built, else null (no build side-effect).</summary>
        internal static DungeonSpec TryGet(string dungeonType)
        {
            if (string.IsNullOrEmpty(dungeonType)) return null;
            _specs.TryGetValue(dungeonType, out var spec);
            return spec;
        }

        /// <summary>
        /// FIX #2 (reload persistence): build + register a spec for EVERY distinct dungeon-type in the shared
        /// PlanBuild section library. Called from Plugin.WorldStart so a previously-generated dungeon's DG
        /// (name FiresDM_&lt;type&gt;DG) has a matching registered spec BEFORE its SetupAvailableRooms runs on reload —
        /// without this, a generated dungeon's rooms never inject after a world reload (specs were only built
        /// on-demand via firesdungeon_generate). Idempotent: BuildAndRegister/RegisterDungeon replace by DG prefix,
        /// so re-running this every WorldStart is harmless. No-op (logs a warning) until the baked template ships.
        /// </summary>
        internal static int RegisterAllFromLibrary(SectionLibrary library)
        {
            if (library == null) return 0;

            List<SectionBlueprint> all;
            try { all = library.ListAllSections(); }
            catch (Exception ex) { Debug.LogWarning($"[FiresDungeonMaster] reload re-register: library read failed: {ex.Message}"); return 0; }
            if (all == null || all.Count == 0) return 0;

            var types = all
                .Select(s => s.DungeonType)
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            int registered = 0;
            foreach (var type in types)
            {
                var spec = BuildAndRegister(library, type, out string reason);
                if (spec != null) registered++;
                else FiresLogger.LogVerbose($"reload re-register skipped '{type}': {reason}");
            }
            if (registered > 0)
                FiresLogger.LogVerbose($"reload re-register: {registered}/{types.Count} dungeon-type spec(s) registered for save/reload.");
            return registered;
        }

        /// <summary>
        /// Build + register a spec for EVERY vanilla dungeon type (<see cref="VanillaThemes"/>) on THIS peer. Called from
        /// Plugin.WorldStart, which runs on the server AND every client — this is the load-bearing half of the "vanilla
        /// dungeons show no walls on a dedicated server" fix.
        ///
        /// A generated dungeon's structural walls/floors are static mesh on each room ROOT, which does NOT replicate;
        /// remote clients rebuild those roots themselves by instantiating the dungeon's DungeonGenerator ZDO (whose
        /// prefab is <c>FiresDM_&lt;type&gt;DG</c>) and running DungeonGenerator.Awake→Load→Spawn(Client). For that ZDO to
        /// resolve, the client's ZNetScene must contain the renamed DG prefab — which only happens when the client BUILDS
        /// the type's spec (BuildAndRegisterVanilla → BuildLocationClone → RegisterLocationTemplateChildren). Vanilla
        /// specs were previously built only on-demand on the server, so the client never had the DG prefab and the ZDO
        /// silently failed to instantiate — walls never appeared while the independently-replicated room props (icicles)
        /// did. Registering all vanilla specs on every peer here mirrors how FiresMausoleum ships its one crypt spec on
        /// all peers (why the crypt's rooms DO render). The set is fixed, so no client↔server sync is needed. Idempotent
        /// (BuildAndRegisterVanilla replaces by DG prefix and tears down the previous clone).
        /// </summary>
        internal static int RegisterAllVanillaTypes()
        {
            int registered = 0;
            foreach (var label in VanillaThemes.Keys)
            {
                var spec = BuildAndRegisterVanilla(label, out string reason);
                if (spec != null) registered++;
                else FiresLogger.LogVerbose($"vanilla pre-register skipped '{label}': {reason}");
            }
            if (registered > 0)
                FiresLogger.LogVerbose($"pre-registered {registered}/{VanillaThemes.Count} vanilla dungeon-type spec(s) " +
                                       "on this peer (DG prefabs in ZNetScene + rooms in DungeonDB so remote clients rebuild walls).");
            return registered;
        }

        /// <summary>
        /// (Re)build the spec for <paramref name="dungeonType"/> from every section of that type in the shared
        /// PlanBuild library, register it with Core, and return it. Idempotent: the registry replaces by DgNamePrefix
        /// and a stale Location clone is torn down + rebuilt so section edits take effect. Returns null with a reason
        /// in <paramref name="reason"/> when the type has no usable sections / the baked template isn't loaded.
        /// </summary>
        internal static DungeonSpec BuildAndRegister(SectionLibrary library, string dungeonType, out string reason)
        {
            reason = "";
            if (library == null) { reason = "section library unavailable."; return null; }
            if (string.IsNullOrEmpty(dungeonType)) { reason = "no dungeon-type name."; return null; }

            GameObject baseTemplate = FootprintRegistration.GetLocationTemplate();
            if (baseTemplate == null)
            {
                reason = "baked Location/DG template not loaded (rebuild the 'firesdungeonmaster' bundle).";
                return null;
            }

            List<SectionBlueprint> sections;
            try { sections = library.ListSectionsByDungeonType(dungeonType); }
            catch (Exception ex) { reason = "library read failed: " + ex.Message; return null; }

            if (sections == null || sections.Count == 0)
            {
                reason = $"no saved sections for dungeon-type '{dungeonType}'.";
                return null;
            }

            if (!sections.Any(IsEntranceSection))
            {
                reason = $"dungeon-type '{dungeonType}' has no Entry/Center section to start from (FindStartRoom needs one).";
                return null;
            }

            Room.Theme theme = ThemeFor(dungeonType);
            string dgPrefix = DgNamePrefixFor(dungeonType);
            string locName = LocationNameFor(dungeonType);
            string portalName = PortalNameFor(dungeonType);

            // ── runtime rooms ──────────────────────────────────────────────────────
            var assets = new FiresDungeonAssets();
            var roomNames = new List<string>();
            var allAssets = new List<GameObject>();
            foreach (var section in sections)
            {
                Room room = DungeonMasterRoomFactory.BuildRoom(section, theme);
                // Wire the Core per-peer door-blocker resolver onto the generated room (makes BakeDoorBlockers
                // load-bearing): PlaceRoom Instantiates the room root, carrying the controller into every clone.
                FootprintRegistration.AttachDoorBlockerToRoom(room.gameObject);
                allAssets.Add(room.gameObject);
                roomNames.Add(room.gameObject.name);
            }

            // ── per-type INACTIVE Location clone (mirrors the baked crypt) ──────────
            GameObject locClone = BuildLocationClone(baseTemplate, locName, dgPrefix, portalName, theme,
                gatewayPrefix: "FiresDM_" + Sanitize(dungeonType) + "Gateway",
                exitPrefix: "FiresDM_" + Sanitize(dungeonType) + "Exit",
                copyVanillaSettings: false);
            allAssets.Add(locClone);

            // Hand the Location clone + every room GO to the spec's Assets so Assets.Get(name) resolves them and the
            // engine's RegisterRooms uses SoftRefFor on each (no extra bake required; the template was baked).
            assets.SetPrefabs(allAssets);

            var spec = new DungeonSpec
            {
                DgNamePrefix = dgPrefix,
                CryptLocationPrefabName = locName,
                InteriorPrefabName = DungeonMasterNames.InteriorTemplate,
                NamedTheme = theme,
                RoomPrefabNames = roomNames.ToArray(),

                EnvBoxPrefabName = DungeonMasterNames.EnvBox,
                EnvName = "Crypt",
                SkyboxMode = EnvironmentBoxController.SkyboxMode.DungeonBlack,          // dark dungeon interior
                EnvBoxVisibility = EnvironmentBoxController.VisibilityMode.Hidden,      // shell invisible from outside
                EnvBoxBiome = Heightmap.Biome.None,
                // Generous fixed size: the interior arrival point lands ~35m ABOVE the DG anchor the box is centered on,
                // so a 72m box put the camera through the ceiling for a frame (sky/light leak). 120m keeps arrival well
                // inside until ScaleEnvBoxToRooms (which currently bails on a rooms=0 timing miss) can tighten it.
                EnvBoxSize = 120f,
                EnvBoxDedupeRadius = 40f,

                // P6 default: cursor/command-spawn ONLY (no auto world-gen flood). Opt a type into world-gen via
                // EnableWorldGen(type) which sets this descriptor and re-registers.
                NearSpawnLocation = null,

                GatewayNamePrefix = "FiresDM_" + Sanitize(dungeonType) + "Gateway",
                ExitNamePrefix = "FiresDM_" + Sanitize(dungeonType) + "Exit",
                InteriorYOffset = 5000f,
                AnchorInteriorToZoneCenter = true, // vanilla-exact: DG lands at the zone centre so real m_zoneSize fills it
                BakeDoorBlockers = true, // the generated rooms bake DoorBlocker children + carry the Core controller

                EnabledGate = null,
                LogTag = $"[FiresDungeonMaster:{dungeonType}]",
                OnDungeonGenerated = null,
                Assets = assets,
            };

            // Tear down a previous Location clone for this type before swapping in the new one (section edits).
            if (_locationClones.TryGetValue(dungeonType, out var oldClone) && oldClone != null && oldClone != locClone)
                NetworkObjectHelper.SafeDestroy(oldClone);
            _locationClones[dungeonType] = locClone;
            _specs[dungeonType] = spec;

            FiresDungeonBridge.RegisterDungeon(spec);
            if (FiresLogger.VerboseEnabled)
                Debug.Log($"{spec.LogTag} registered: {roomNames.Count} room(s), theme {theme}, DG '{dgPrefix}', " +
                          $"location template '{locName}' (inactive holder, activeSelf children).");
            return spec;
        }

        /// <summary>
        /// Build + register a VANILLA-dungeon spec: our own Location/DG/portal template driven with a stock Room.Theme
        /// and NO custom rooms. Because RoomPrefabNames is empty, the Core's SetupAvailableRooms postfix leaves the stock
        /// rooms vanilla already populated for the theme — so the DG generates a genuine Valheim dungeon (Forest Crypt /
        /// Sunken Crypt / Frost Cave / Infested Mine) that our entry door + gateway teleport into exactly like a custom
        /// one. No re-bake, no FUC change. Returns null with a reason if the label is unknown or the template is missing.
        /// </summary>
        internal static DungeonSpec BuildAndRegisterVanilla(string vanillaLabel, out string reason)
        {
            reason = "";
            if (string.IsNullOrEmpty(vanillaLabel)) { reason = "no vanilla dungeon selected."; return null; }
            if (!VanillaThemes.TryGetValue(vanillaLabel, out Room.Theme theme))
            {
                reason = $"unknown vanilla dungeon '{vanillaLabel}'.";
                return null;
            }

            GameObject baseTemplate = FootprintRegistration.GetLocationTemplate();
            if (baseTemplate == null)
            {
                reason = "baked Location/DG template not loaded (rebuild the 'firesdungeonmaster' bundle).";
                return null;
            }

            string dgPrefix = DgNamePrefixFor(vanillaLabel);
            string locName = LocationNameFor(vanillaLabel);
            string portalName = PortalNameFor(vanillaLabel);
            string gatewayPrefix = "FiresDM_" + Sanitize(vanillaLabel) + "Gateway";
            string exitPrefix = "FiresDM_" + Sanitize(vanillaLabel) + "Exit";

            var assets = new FiresDungeonAssets();
            GameObject locClone = BuildLocationClone(baseTemplate, locName, dgPrefix, portalName, theme, gatewayPrefix, exitPrefix, copyVanillaSettings: true);
            assets.SetPrefabs(new List<GameObject> { locClone });

            var spec = new DungeonSpec
            {
                DgNamePrefix = dgPrefix,
                CryptLocationPrefabName = locName,
                InteriorPrefabName = DungeonMasterNames.InteriorTemplate,
                NamedTheme = theme,
                RoomPrefabNames = new string[0],   // ← empty → SetupAvailableRooms postfix leaves the STOCK rooms in place

                EnvBoxPrefabName = DungeonMasterNames.EnvBox,
                EnvName = "Crypt",
                SkyboxMode = EnvironmentBoxController.SkyboxMode.DungeonBlack,          // dark dungeon interior
                EnvBoxVisibility = EnvironmentBoxController.VisibilityMode.Hidden,      // shell invisible from outside
                EnvBoxBiome = Heightmap.Biome.None,
                // Generous fixed size: the interior arrival point lands ~35m ABOVE the DG anchor the box is centered on,
                // so a 72m box put the camera through the ceiling for a frame (sky/light leak). 120m keeps arrival well
                // inside until ScaleEnvBoxToRooms (which currently bails on a rooms=0 timing miss) can tighten it.
                EnvBoxSize = 120f,
                EnvBoxDedupeRadius = 40f,

                NearSpawnLocation = null,
                GatewayNamePrefix = gatewayPrefix,
                ExitNamePrefix = exitPrefix,
                InteriorYOffset = 5000f,
                AnchorInteriorToZoneCenter = true, // vanilla-exact: DG lands at the zone centre so real m_zoneSize fills it
                BakeDoorBlockers = false,   // stock rooms carry their own door pieces
                EnabledGate = null,
                LogTag = $"[FiresDungeonMaster:{vanillaLabel}(vanilla)]",
                OnDungeonGenerated = null,
                Assets = assets,
            };

            if (_locationClones.TryGetValue(vanillaLabel, out var oldClone) && oldClone != null && oldClone != locClone)
                NetworkObjectHelper.SafeDestroy(oldClone);
            _locationClones[vanillaLabel] = locClone;
            _specs[vanillaLabel] = spec;

            FiresDungeonBridge.RegisterDungeon(spec);
            if (FiresLogger.VerboseEnabled)
                Debug.Log($"{spec.LogTag} registered vanilla passthrough: theme {theme}, DG '{dgPrefix}' (stock rooms).");
            return spec;
        }

        // A theme bit no vanilla room carries (real themes top out at 1<<13); a DG driven with it finds zero rooms, so
        // combined with spec.EmptyInterior nothing generates — just the baked portal/landing-pad/exit + the env box.
        private const Room.Theme EmptyRoomTheme = (Room.Theme)(1 << 20);

        /// <summary>
        /// Build + register an EMPTY-ROOM spec for a door: our Location/DG/portal template + env box, but the
        /// DungeonGenerator generates NO rooms (empty theme + spec.EmptyInterior skips Generate). The player teleports
        /// into a single enclosed dark room, lands on the baked landing pad, and leaves through the baked exit door.
        /// Keyed separately from the same type's full/vanilla spec so an empty door and a full door never collide.
        /// </summary>
        internal static DungeonSpec BuildAndRegisterEmpty(string label, string environment, out string reason)
        {
            reason = "";
            if (string.IsNullOrEmpty(label)) label = "Room";

            GameObject baseTemplate = FootprintRegistration.GetLocationTemplate();
            if (baseTemplate == null)
            {
                reason = "baked Location/DG template not loaded (rebuild the 'firesdungeonmaster' bundle).";
                return null;
            }

            string key = "Empty:" + label;
            string tag = Sanitize(label) + "Empty";
            string dgPrefix = "FiresDM_" + tag + "DG";
            string locName = "FiresDM_" + tag + "Location";
            string portalName = "FiresDM_" + tag + "Portal";
            string gatewayPrefix = "FiresDM_" + tag + "Gateway";
            string exitPrefix = "FiresDM_" + tag + "Exit";

            var assets = new FiresDungeonAssets();
            GameObject locClone = BuildLocationClone(baseTemplate, locName, dgPrefix, portalName, EmptyRoomTheme, gatewayPrefix, exitPrefix, copyVanillaSettings: false);
            assets.SetPrefabs(new List<GameObject> { locClone });

            string env = (!string.IsNullOrEmpty(environment) && environment != "Nothing") ? environment : "Crypt";
            var spec = new DungeonSpec
            {
                DgNamePrefix = dgPrefix,
                CryptLocationPrefabName = locName,
                InteriorPrefabName = DungeonMasterNames.InteriorTemplate,
                NamedTheme = EmptyRoomTheme,
                RoomPrefabNames = new string[0],
                EmptyInterior = true,   // engine skips DungeonGenerator.Generate → no rooms, no PlaceStartRoom NRE

                EnvBoxPrefabName = DungeonMasterNames.EnvBox,
                EnvName = env,
                SkyboxMode = EnvironmentBoxController.SkyboxMode.DungeonBlack,
                // The empty room IS the box — show its shell (visible inside and out) so the admin can see the
                // volume they're building in, per the original placeable-box behaviour.
                EnvBoxVisibility = EnvironmentBoxController.VisibilityMode.Full,
                EnvBoxBiome = Heightmap.Biome.None,
                EnvBoxSize = 64f,   // one zone; the Core clamps to its (64,500,64) minimum around the landing pad
                EnvBoxDedupeRadius = 40f,

                NearSpawnLocation = null,
                GatewayNamePrefix = gatewayPrefix,
                ExitNamePrefix = exitPrefix,
                InteriorYOffset = 5000f,
                AnchorInteriorToZoneCenter = true,
                BakeDoorBlockers = false,
                EnabledGate = null,
                LogTag = $"[FiresDungeonMaster:{label}(empty)]",
                OnDungeonGenerated = null,
                Assets = assets,
            };

            if (_locationClones.TryGetValue(key, out var oldClone) && oldClone != null && oldClone != locClone)
                NetworkObjectHelper.SafeDestroy(oldClone);
            _locationClones[key] = locClone;
            _specs[key] = spec;

            FiresDungeonBridge.RegisterDungeon(spec);
            if (FiresLogger.VerboseEnabled)
                Debug.Log($"{spec.LogTag} registered EMPTY room (portal + landing pad + exit + env box; no procedural rooms).");
            return spec;
        }

        /// <summary>
        /// Opt a type into world-gen: (re)build its spec with a near-spawn ZoneLocation descriptor. The engine's
        /// ZoneSystem.SetupLocations postfix then appends it on the next SetupLocations run (world reload).
        /// </summary>
        internal static DungeonSpec EnableWorldGen(SectionLibrary library, string dungeonType, ZoneLocationDescriptor descriptor, out string reason)
        {
            var spec = BuildAndRegister(library, dungeonType, out reason);
            if (spec == null) return null;
            spec.NearSpawnLocation = descriptor ?? DefaultWorldGenDescriptor();
            FiresDungeonBridge.RegisterDungeon(spec); // idempotent replace-by-prefix
            reason = $"world-gen enabled for '{dungeonType}' (takes effect on next world load).";
            return spec;
        }

        internal static ZoneLocationDescriptor DefaultWorldGenDescriptor() => new ZoneLocationDescriptor
        {
            Biome = Heightmap.Biome.All,
            BiomeArea = Heightmap.BiomeArea.Everything,
            Quantity = 1,
            Unique = false,
            Prioritized = false,
            CenterFirst = false,
            MinDistanceFromCenter = 500f,
            MaxDistanceFromCenter = 6000f,
            ExteriorRadius = 16f,
            InteriorRadius = 32f,
            ClearArea = true,
            RandomRotation = true,
            MinAltitude = 1f,
        };

        /// <summary>
        /// Clone the baked Location template into a per-type INACTIVE template. The clone is created under the
        /// permanently-inactive Fires_PrefabHolder (so its ZNetViews never Awake — no rogue ZNetScene instance, no
        /// ghost-ZDO leak; vanilla ZNetView.Awake unconditionally creates a persistent ZDO BEFORE the ghost check, so
        /// any SetActive on the template would leak persistent ZDOs — verified against ZNetView.cs:69-85). Its DG/Portal
        /// children are renamed per type and REGISTERED into ZNetScene by name (so SpawnLocation's per-piece ZDOs
        /// resolve on reload), and it is LEFT INACTIVE — its children's activeSelf==true is all
        /// Utils.GetEnabledComponentsInChildren checks, so SpawnLocation still enumerates + clones the DG/Portal.
        /// This is the proven-working FiresMausoleum path (inactive prefab, never SetActive).
        /// </summary>
        private static GameObject BuildLocationClone(GameObject baseTemplate, string locName, string dgName,
            string portalName, Room.Theme theme, string gatewayPrefix, string exitPrefix, bool copyVanillaSettings)
        {
            // 1) inactive clone under Fires_PrefabHolder (DontDestroyOnLoad). NEVER activated.
            GameObject clone = NetworkObjectHelper.InstantiateInactiveClone(baseTemplate, locName);

            // 2) rename the DG + Portal children per type, set the DG theme. We find by the baked child names
            //    (DungeonMasterNames.*).
            DungeonGenerator dg = null;
            GameObject portalGo = null;
            Transform gatewayT = null;
            Transform exitT = null;
            foreach (Transform t in clone.GetComponentsInChildren<Transform>(true))
            {
                if (t == clone.transform) continue;
                if (t.gameObject.name == DungeonMasterNames.DgChild)
                {
                    t.gameObject.name = dgName; // FiresDM_<type>DG -> MatchForDg dispatch
                    dg = t.gameObject.GetComponent<DungeonGenerator>();
                }
                else if (t.gameObject.name == DungeonMasterNames.PortalChild)
                {
                    portalGo = t.gameObject;
                    portalGo.name = portalName;
                }
                else if (t.gameObject.name == DungeonMasterNames.GatewayChild)
                {
                    t.gameObject.name = gatewayPrefix;
                    gatewayT = t;
                }
                else if (t.gameObject.name == DungeonMasterNames.ExitChild)
                {
                    t.gameObject.name = exitPrefix;
                    exitT = t;
                }
                else if (t.gameObject.name == "ExitDoor")
                {
                    // The exit arch was baked facing 180° but its OPENING sits 90° off the teleport landing, so you
                    // arrive at its side. Re-yaw the visual arch to face the -Z landing you step out through. Runtime
                    // (no re-bake) so the direction is a one-line flip if it lands the wrong way (90 <-> 270).
                    t.localRotation = Quaternion.Euler(0f, ExitArchYaw, 0f);
                }
            }

            // DIAG: the baked child LOCAL offsets relative to the Location root. The DG's local XZ is the load-bearing
            // unknown for zone-centre anchoring — if it is NOT ~0, spawning the Location at the zone centre lands the DG
            // off-centre by exactly this XZ, and the zone-centre fix needs to subtract it. Logged once per spec build.
            if (FiresLogger.VerboseEnabled)
            {
                Vector3 dgL = dg != null ? dg.transform.localPosition : Vector3.zero;
                Vector3 gwL = gatewayT != null ? gatewayT.localPosition : Vector3.zero;
                Vector3 exL = exitT != null ? exitT.localPosition : Vector3.zero;
                float dgXZ = new Vector2(dgL.x, dgL.z).magnitude;
                Debug.Log($"[FDM-PLACE] template '{locName}' baked child LOCAL offsets: DG={dgL} (xz-mag={dgXZ:F2}), " +
                          $"gateway={gwL}, exit={exL}. For zone-centre anchoring the DG xz-mag MUST be ~0 " +
                          $"(else the DG lands off-centre by that XZ and rooms clip on one side).");
            }

            if (dg != null)
            {
                dg.m_themes = theme;
                // The DG now lands at the ZONE CENTRE (DungeonSpec.AnchorInteriorToZoneCenter → SpawnDungeonAt), so the
                // placement Bounds(zoneCentre, m_zoneSize) is centred on it — no off-centre clipping, no balloon needed.
                // VANILLA dungeons get the REAL stock m_zoneSize below (TryCopyVanillaDgSettings). CUSTOM section-sets
                // have no stock source, so keep a generous bound here; a hand-authored set is limited by its own room
                // pool + m_maxRooms (it cannot sprawl like the huge vanilla stock pool did), and the env box auto-scales.
                dg.m_zoneSize = new Vector3(128f, 96f, 128f);
            }

            // VANILLA dungeons: copy the real stock DungeonGenerator tuning (door types, tile grid, room counts, tilt)
            // from a matching vanilla dungeon so ours generates the SAME full proc-gen layout — the baked template's
            // empty m_doorTypes + generic 8m tiles were why vanilla came out as a sparse 4-room stub. Custom section
            // sets keep the baked tuning (their rooms carry their own connectors + door blockers).
            if (copyVanillaSettings && dg != null) TryCopyVanillaDgSettings(dg, theme);

            // Attach the Core safety-net portal linker (re-pairs the gateway<->exit teleports locally on every peer).
            if (portalGo != null)
            {
                var link = portalGo.GetComponent<FiresDungeonPortalLink>() ?? portalGo.AddComponent<FiresDungeonPortalLink>();
                link.Configure(gatewayPrefix, exitPrefix, 5000f);
            }

            // The interior env box is driven by OUR EnvironmentBoxController (Core EnsureEnvBox), NOT vanilla's
            // Location.Awake m_interiorPrefab path. Disable m_hasInterior so vanilla doesn't ALSO instantiate the baked
            // InteriorTemplate EnvZone box — two boxes would both call SetForceEnvironment and fight (the baked one has
            // no environment set, so it would clear our dungeon env → the interior stops being dark). Single driver.
            var cloneLoc = clone.GetComponent<Location>();
            if (cloneLoc != null) cloneLoc.m_hasInterior = false;

            // 3) register the renamed DG + Portal names into ZNetScene so per-piece ZDOs resolve on reload.
            FootprintRegistration.RegisterLocationTemplateChildren(clone, dgName, portalName);

            // 4) leave the clone INACTIVE under the holder. No SetActive, no ghost-init: ZNetView.Awake never runs on
            //    the template, so no persistent ghost ZDO is leaked. SpawnLocation enumerates the children by their
            //    activeSelf==true and Instantiates them itself (that Instantiate is what creates the live ZDOs).
            return clone;
        }

        // Representative stock dungeon Locations per theme — we read their DungeonGenerator to clone its real tuning.
        private static readonly Dictionary<Room.Theme, string[]> VanillaDgSources = new Dictionary<Room.Theme, string[]>
        {
            { Room.Theme.ForestCrypt, new[] { "Crypt3", "Crypt2", "Crypt4" } },
            { Room.Theme.SunkenCrypt, new[] { "SunkenCrypt4", "SunkenCrypt3", "SunkenCrypt2", "SunkenCrypt1" } },
            { Room.Theme.Cave,        new[] { "MountainCave02", "MountainCave01", "TrollCave02", "TrollCave" } },
            { Room.Theme.DvergerTown, new[] { "Mistlands_DvergrTownEntrance", "Mistlands_DvergrBossEntrance", "Mistlands_DvergrTownEntrance1" } },
            { Room.Theme.Crypt,       new[] { "Crypt2", "Crypt3", "Crypt4" } },
            { Room.Theme.GoblinCamp,  new[] { "GoblinCamp2" } },
            // The 1.0 Ashlands dungeon locations are literally named AshlandRuins / FortressRuins (checked in the 1.0 rip);
            // CharredFortress has no DungeonGenerator.
            { Room.Theme.AshlandRuins, new[] { "AshlandRuins" } },
            { Room.Theme.FortressRuins, new[] { "FortressRuins" } },
        };

        /// <summary>
        /// Copy a stock dungeon's DungeonGenerator tuning onto <paramref name="target"/> so it proc-gens the SAME layout
        /// vanilla does. The load-bearing field is <c>m_doorTypes</c> (the door prefabs that connect rooms) — without it
        /// the generator can only place the entrance. Reads the source DG off the vanilla Location prefab via its
        /// SoftReference (loaded, never instantiated). Returns false (keeping the baked tuning) if no source is found.
        /// </summary>
        private static bool TryCopyVanillaDgSettings(DungeonGenerator target, Room.Theme theme)
        {
            var zs = ZoneSystem.instance;
            if (target == null || zs == null || zs.m_locations == null) return false;

            DungeonGenerator src = null;
            if (VanillaDgSources.TryGetValue(theme, out var names))
                foreach (var nm in names) { src = LoadDgFromLocation(zs, nm); if (src != null) break; }
            if (src == null) src = FindDgByTheme(zs, theme);
            if (src == null)
            {
                Debug.LogWarning($"[FiresDungeonMaster] no stock DG found for theme {theme}; vanilla dungeon keeps generic tuning (may be sparse).");
                return false;
            }

            target.m_algorithm = src.m_algorithm;
            // NOTE: do NOT copy src.m_themes here. The DG's theme must stay the spec's NamedTheme (which RegisterRooms
            // also sets) — that's the theme vanilla SetupAvailableRooms filters rooms by. A source Location's own DG
            // theme isn't guaranteed to equal the mapped theme, so copying it could point a type at the wrong rooms.
            target.m_doorTypes = new List<DungeonGenerator.DoorDef>(src.m_doorTypes);
            target.m_doorChance = src.m_doorChance;
            target.m_maxTilt = src.m_maxTilt;
            target.m_tileWidth = src.m_tileWidth;
            target.m_gridSize = src.m_gridSize;
            target.m_spawnChance = src.m_spawnChance;
            target.m_perimeterSections = src.m_perimeterSections;
            target.m_perimeterBuffer = src.m_perimeterBuffer;
            target.m_alternativeFunctionality = src.m_alternativeFunctionality;
            // Do NOT copy src.m_useCustomInteriorTransform. Real MountainCave/Crypt Locations set it true AND wire a
            // Location.m_interiorTransform + m_generator; our baked Location has neither, so ZoneSystem.SpawnLocation's
            // custom-interior branch (which sets m_originalPosition and re-anchors the interior) never runs. Copying the
            // flag onto the DG alone would only skew PlaceRoom's per-room seed (pos-transform.position vs pos) and mis-
            // match the Location. Force false to match our template and keep the room seed frame consistent.
            target.m_useCustomInteriorTransform = false;
            target.m_maxRooms = src.m_maxRooms;
            target.m_minRooms = src.m_minRooms;
            // Full parity with EWD's Spawner.Override — the camp-radius (CampRadial dungeons), required-room minimum and
            // the minAltitude all feed the same vanilla Generate/PlaceRooms path; copying a subset left some themes short.
            target.m_campRadiusMin = src.m_campRadiusMin;
            target.m_campRadiusMax = src.m_campRadiusMax;
            target.m_minRequiredRooms = src.m_minRequiredRooms;
            target.m_requiredRooms = src.m_requiredRooms;
            target.m_minAltitude = src.m_minAltitude;

            // Copy the REAL zone size verbatim (exactly what EWD's Spawner.Override does) so ours fills the zone the
            // SAME way vanilla does — no balloon. This is only correct because the DG now lands at the ZONE CENTRE
            // (DungeonSpec.AnchorInteriorToZoneCenter → FiresDungeonCore.SpawnDungeonAt snaps the spawn to the zone
            // centre). Generate re-pins m_zoneCenter to the DG's world zone centre, so a centred DG + real m_zoneSize
            // gives a vanilla-sized dungeon; the old off-centre anchor is why a one-zone size clipped to ~4 rooms and
            // the 256 balloon then over-generated to ~181.
            target.m_zoneSize = src.m_zoneSize;
            target.m_zoneCenter = Vector3.zero;   // Generate overwrites this to the zone centre; kept sane meanwhile

            FiresLogger.LogVerbose($"cloned vanilla DG tuning from '{src.gameObject.name}' (theme {theme}): " +
                                   $"doorTypes={target.m_doorTypes.Count}, rooms {target.m_minRooms}-{target.m_maxRooms}, tile={target.m_tileWidth}, " +
                                   $"alt={target.m_alternativeFunctionality}, algo={target.m_algorithm}, zoneSize={target.m_zoneSize}.");
            return true;
        }

        private static DungeonGenerator LoadDgFromLocation(ZoneSystem zs, string prefabName)
        {
            var zl = zs.m_locations.Find(l => l != null && l.m_prefab.IsValid && l.m_prefab.Name == prefabName);
            if (zl == null) return null;
            zl.m_prefab.Load();   // ref-counted; leave loaded so the DoorDef prefab refs stay valid
            var prefab = zl.m_prefab.Asset;
            var dg = prefab != null ? prefab.GetComponentInChildren<DungeonGenerator>(true) : null;
            if (dg == null) zl.m_prefab.Release();
            return dg;
        }

        // Fallback: scan likely dungeon Locations for one whose DG matches the theme (name-heuristic gated so we don't
        // load hundreds of surface prefabs). Each probe is a synchronous location load, so reaching this at all means a
        // VanillaDgSources entry no longer matches the game — say so, and release every location that didn't match.
        private static DungeonGenerator FindDgByTheme(ZoneSystem zs, Room.Theme theme)
        {
            FiresLogger.LogWarning($"no VanillaDgSources location gave a DungeonGenerator for theme {theme}; scanning every " +
                                   "likely location instead (slow: one synchronous load each). Update VanillaDgSources.");
            foreach (var zl in zs.m_locations)
            {
                if (zl == null || !zl.m_prefab.IsValid) continue;
                string n = zl.m_prefab.Name ?? "";
                bool likely =
                    n.IndexOf("Crypt", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Cave", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Dvergr", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Mine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Dungeon", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Goblin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Camp", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Fortress", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Ruin", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Charred", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Ashland", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Mistlands", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!likely) continue;
                zl.m_prefab.Load();
                var dg = zl.m_prefab.Asset != null ? zl.m_prefab.Asset.GetComponentInChildren<DungeonGenerator>(true) : null;
                if (dg != null && (dg.m_themes & theme) != 0) return dg;
                zl.m_prefab.Release();
            }
            return null;
        }

        // Starts async loads of the one stock location each registered vanilla theme copies its tuning from, as soon
        // as the location list exists, so the synchronous Load() in TryCopyVanillaDgSettings finds them resident when
        // WorldStart builds the specs. Only the first name per theme: the one that is actually used.
        internal static void WarmVanillaDgSources(ZoneSystem zs)
        {
            if (zs == null || zs.m_locations == null) return;
            var firstSourceNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var theme in VanillaThemes.Values.Distinct())
                if (VanillaDgSources.TryGetValue(theme, out var names) && names.Length > 0)
                    firstSourceNames.Add(names[0]);

            foreach (var zl in zs.m_locations)
            {
                if (zl == null || !zl.m_prefab.IsValid || !firstSourceNames.Contains(zl.m_prefab.Name)) continue;
                if (zl.m_prefab.IsLoaded || zl.m_prefab.IsLoading) continue;
                zl.m_prefab.LoadAsync();
            }
        }

        private static Room.Theme ThemeFor(string dungeonType)
        {
            uint h = 5381;
            unchecked { foreach (char c in dungeonType) h = ((h << 5) + h) ^ (uint)c; }
            return NamedThemes[(int)(h % (uint)NamedThemes.Length)];
        }

        private static bool IsEntranceSection(SectionBlueprint s) =>
            s.Role == SectionRole.Entry || s.Role == SectionRole.Center;

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "none";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), "Start")]
    public static class WarmVanillaDgSourcesOnZoneSystemStart
    {
        [HarmonyPostfix]
        static void Postfix(ZoneSystem __instance) => DungeonMasterSpecBuilder.WarmVanillaDgSources(__instance);
    }
}
