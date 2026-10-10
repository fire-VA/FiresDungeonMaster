namespace FiresDungeonMaster.Generation
{
    /// <summary>
    /// Frozen string identifiers for the baked 'firesdungeonmaster' bundle prefabs that the P6 generation pipeline
    /// REUSES (mirroring FiresMausoleum.MausoleumNames). These names are hashed into ZNetScene/DungeonDB by
    /// GetStableHashCode, so renaming one orphans every placed ZDO and every saved dungeon room — keep them stable.
    ///
    /// Unlike FiresMausoleum (one crypt), FiresDungeonMaster authors MANY dungeon-types at runtime, so the baked
    /// Location/DG/Portal here are a TEMPLATE: <see cref="DungeonMasterSpecBuilder"/> clones it once per dungeon-type
    /// and renames the clone's DG child to a per-type prefix (FiresDM_&lt;type&gt;DG) so FiresDungeonRegistry.MatchForDg
    /// dispatches the right room set. The clone's renamed DG/Portal ZNetView children are registered into ZNetScene
    /// by name so SpawnLocation's per-piece ZDOs resolve on reload — exactly the Mausoleum crypt mechanism.
    /// </summary>
    internal static class DungeonMasterNames
    {
        public const string BundleResource = "firesdungeonmaster";

        // ── baked Location template (cloned + renamed per dungeon-type) ──────────
        // Surface Location placed by ZoneSystem world-gen / the spawn command. m_hasInterior=true, m_interiorPrefab
        // points at the EnvZone interior box; the DG + Portal sit as direct ZNetView children at local y+5000
        // (exactly like the crypt), which is the surfacePos+(0,5000,0) spot the Core engine assumes.
        public const string LocationTemplate = "FiresDungeonMasterLocation";
        public const string InteriorTemplate = "FiresDungeonMasterInterior"; // EnvZone-only box (no DG)
        public const string DgChild = "FiresDungeonMasterDG";                 // DungeonGenerator + persistent ZNetView
        public const string PortalChild = "FiresDungeonMasterPortal";          // Portal + persistent ZNetView (teleports)
        public const string GatewayChild = "FiresDungeonMasterGateway";        // surface entrance teleport (under Portal)
        public const string ExitChild = "FiresDungeonMasterExit";              // interior exit teleport (under Portal)

        // ── dark-crypt environment box (baked empty; ZNetView + controller attached at ZNetScene.Awake) ──
        public const string EnvBox = "FiresEnvBox"; // shared Core/Mausoleum env-box name; harmless if Mausoleum also bakes it

        // The baked 'DoorBlocker' child name MUST equal FiresCore.Dungeon.DungeonDoorBlockerController.BlockerChildName
        // so the runtime controller resolves it via conn.transform.Find. Runtime rooms bake one under each connection.
        public const string DoorBlockerChild = "DoorBlocker";
    }
}
