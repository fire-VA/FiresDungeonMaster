using System;
using System.Collections.Generic;
using BepInEx;
using FiresCore.Lifecycle;
using FiresCore.Sync;

namespace FiresDungeonMaster
{
    /// <summary>
    /// FiresDungeonMaster — an in-world MODULAR DUNGEON BLUEPRINT EDITOR + procedural generator. Hard-depends on
    /// FiresUnifiedCore and drives its shared FiresDungeon.Core engine (the same one FiresMausoleum's crypt rides):
    /// the player hammer-places a wireframe room FOOTPRINT, tags its 6 edge circles (wall / doorway / open / ...)
    /// and its centre sphere (module role + a dungeon-type name), free-builds/decorates inside, SAVES it as a proper
    /// Infinity-Hammer/PlanBuild .blueprint "section" (with additive #fdm_* headers), snaps sections together, and
    /// generates a dungeon from every section sharing a dungeon-type name. Placement = both a Dungeon-Builder hammer
    /// tab (FAP-coexisting) and world-gen by flagged dungeon type.
    ///
    /// This is the thin BepInEx shell, cloned from the proven FiresMausoleum Core-dungeon-consumer template (the
    /// memorial domain dropped). Editor + section + generation modules are added in phases (see project memory).
    /// </summary>
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency("com.Fire.FiresUnifiedCore", BepInDependency.DependencyFlags.HardDependency)]
    public class FiresDungeonMasterPlugin : FiresMod<FiresDungeonMasterPlugin>
    {
        public const string PluginGUID = "com.Fire.FiresDungeonMaster";
        public const string PluginName = "FiresDungeonMaster";
        public const string PluginVersion = "0.1.114";

        internal static BepInEx.Logging.ManualLogSource Log;

        internal ConfigSync configSync;

        // The footprint category tab-grow patch (FootprintCategoryRegistry+Hud_Awake_GrowTabs_Patch) touches
        // client-only UI (Hud.m_pieceCategoryTabs, UIInputHandler, Player.SetBuildCategory). It is skipped on a
        // dedicated server so PatchAll never imports those refs into Mono's headless IL rewriter and native-crashes.
        // The PieceTable.UpdateAvailable grow-prefix is dedi-safe (PieceTable exists headless) and stays active.
        protected override IReadOnlyCollection<string> DedicatedServerSkipPatchTypes =>
            new[]
            {
                "FiresDungeonMaster.FootprintCategoryRegistry+Hud_Awake_GrowTabs_Patch",
                // P5: client-only placement-ghost doorway snap. Player + m_placementGhost are client-side only.
                "FiresDungeonMaster.Editor.FootprintSnapController",
                // P7: client-only saved-section browser hotkey (Shift+B). Touches Hud/Player/Console/Chat — skip headless.
                "FiresDungeonMaster.UI.SectionBrowserUI+Hud_Update_Hotkey_Patch",
                // Blueprint bench Esc/cursor driver — patches Hud.Update (client-only type). Skip on a dedi.
                "FiresDungeonMaster.UI.BlueprintBenchPanel+Hud_Update_Patch",
                // Notebook editor Esc/cursor driver — patches Hud.Update (client-only type). Skip on a dedi.
                "FiresDungeonMaster.UI.NotebookEditorPanel+Hud_Update_Patch",
                // Edit-in-Box grab — patches Player.UpdatePlacement (client-only build placement). Skip on a dedi.
                "FiresDungeonMaster.Editor.SectionPlaceGrabController+Player_UpdatePlacement_GrabBlock",
                // Entry-door config panel Esc/cursor driver — patches Hud.Update (client-only type). Skip on a dedi.
                // (EntryDoorRpc's ZNet.Start/Shutdown patches are NOT skipped — the server must register the RPC.)
                "FiresDungeonMaster.UI.EntryDoorPanel+Hud_Update_Patch",
                // Dungeon editor (exit-arch) Esc/cursor driver — patches Hud.Update (client-only type). Skip on a dedi.
                "FiresDungeonMaster.UI.DungeonEditPanel+Hud_Update_Patch",
                // Entry-door placement diagnostic — patches Player.PlacePiece (client-only type). Skip on a dedi.
                "FiresDungeonMaster.Editor.EntryDoorController+Player_PlacePiece_Diag",
                // Logout safety net — patches ZNet.Shutdown but touches client-only panels/InputBlock. Skip on a dedi.
                "FiresDungeonMaster.UI.EntryDoorPanel+ZNet_Shutdown_PanelGuard",
                // Lockpick minigame logout guard — patches ZNet.Shutdown, touches client-only UI. Skip on a dedi.
                "FiresDungeonMaster.UI.LockpickPanel+ZNet_Shutdown_Guard",
            };

        protected override void Setup()
        {
            Log = Logger;
            try { FiresDungeonMasterBanner.PrintBig(); }
            catch (Exception ex) { Logger.LogWarning($"big banner failed: {ex.Message}"); }

            configSync = new ConfigSync(PluginGUID)
            {
                DisplayName = PluginName,
                CurrentVersion = PluginVersion,
                MinimumRequiredVersion = PluginVersion,
            };

            // Section authoring config (centerpoint fallback prefab, ...). Bound before any save can run.
            DungeonMasterConfig.Bind(Config);

            // Load the editor bundle + acquire the footprint prefab(s). The ZNetScene.Awake / ObjectDB.Awake /
            // PieceTable.UpdateAvailable / Hud.Awake Harmony patches in Ascend.cs are auto-discovered by the
            // FiresMod base Harmony.PatchAll; this is the one wiring call they need to have something to register.
            FootprintRegistration.LoadBundleAndPrefabs();

            // Register the "Lockpicking" custom skill through the shared FUC skill system BEFORE any character loads
            // (so vanilla ZPackage persistence keeps it). The icon is refreshed to the lockpick's inventory icon once
            // ObjectDB.Awake builds it (Ascend.ConfigureItems). Registered on both peers — harmless on a dedi.
            FiresDungeonMaster.Editor.LockpickSkill.Register();

            // P4: all-object section capture/save + load/place console commands (firesdungeon_save / _list / _place).
            FiresDungeonMaster.Modules.DungeonMasterCommands.Register();

            // P6: dungeon-generation console commands (firesdungeon_types / _sections / _generate / _worldgen). The
            // DungeonGenerator.PlaceRoom interior-spawn postfix (DungeonMasterInteriorPatch) is auto-discovered by the
            // FiresMod base Harmony.PatchAll; the runtime dungeon rides FiresDungeon.Core via the bridge.
            FiresDungeonMaster.Generation.DungeonGenerationCommands.Register();

            // Phase 1+: bind config; load the editor bundle; register the footprint hammer piece, the section
            // library (PlanBuild .blueprint round-trip), the tag gizmos, capture, and the per-dungeon DungeonSpec
            // assembly that hands sections to FiresCore.Bridge.FiresDungeonBridge.
            Logger.LogInfo($"{PluginName} v{PluginVersion} setup complete (P6 generation wired).");
        }

        // Server-side: ensure the Core dungeon coroutine host is up (shared with FiresMausoleum). No-op on a client.
        protected override void WorldStart()
        {
            try { FiresCore.Bridge.FiresDungeonBridge.EnsureService(); }
            catch (Exception ex) { Logger.LogError($"[FiresDungeonMaster] service start failed: {ex}"); }

            // FIX #2 (reload persistence): re-register a spec for every dungeon-type in the section library BEFORE any
            // saved DG's SetupAvailableRooms runs on reload, so a previously-generated dungeon's rooms re-inject after a
            // world reload. Idempotent (replace-by-DG-prefix). No-op until the baked Location template ships.
            int libraryTypes = 0;
            try
            {
                var lib = FiresDungeonMaster.Sections.FdmPaths.OpenLibrary();
                libraryTypes = FiresDungeonMaster.Generation.DungeonMasterSpecBuilder.RegisterAllFromLibrary(lib);
            }
            catch (Exception ex) { Logger.LogError($"[FiresDungeonMaster] reload spec re-register failed: {ex}"); }

            // Build + register EVERY vanilla dungeon-type spec on THIS peer (server AND client). A remote client rebuilds
            // a dungeon's non-replicating room ROOTS (walls/floors) by instantiating its DungeonGenerator ZDO — which
            // only resolves if the client's ZNetScene has the renamed FiresDM_<type>DG prefab, i.e. only if the client
            // BUILT that vanilla spec. Without this the client saw generated props (icicles) but no walls. Fixed set, so
            // no clientâ†”server sync needed; idempotent.
            int vanillaTypes = 0;
            try
            {
                vanillaTypes = FiresDungeonMaster.Generation.DungeonMasterSpecBuilder.RegisterAllVanillaTypes();
            }
            catch (Exception ex) { Logger.LogError($"[FiresDungeonMaster] vanilla-type pre-register failed: {ex}"); }

            EmitDungeonLoadSummary(libraryTypes, vanillaTypes);
            FiresDungeonMasterBanner.Print();
        }

        private static void EmitDungeonLoadSummary(int libraryTypes, int vanillaTypes)
        {
            try
            {
                int rooms = FiresDungeonMaster.Generation.DungeonMasterRoomFactory.InteriorByRoomName.Count;
                int pieces = 0;
                foreach (var interior in FiresDungeonMaster.Generation.DungeonMasterRoomFactory.InteriorByRoomName.Values)
                    pieces += interior?.Count ?? 0;
                FiresCore.Logging.LoadSummary.For(PluginName).EmitMiniBox("ðŸ° DUNGEONS", new[]
                {
                    $"{vanillaTypes} vanilla theme(s)",
                    $"{libraryTypes} library type(s)",
                    $"{rooms} room(s) in DungeonDB",
                    $"{pieces} interior piece(s)",
                });
            }
            catch (Exception ex) { FiresLogger.LogWarning($"dungeon summary failed: {ex.Message}"); }
        }

        protected override void TitleScene(bool isFirstBoot) { }
        protected override void Shutdown() { }
    }
}
