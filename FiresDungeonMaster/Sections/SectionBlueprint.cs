using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FiresDungeonMaster.Sections
{
    /// <summary>
    /// SectionBlueprint - the data model for a dungeon section (a modular room).
    /// Combines the standard Infinity-Hammer/PlanBuild Blueprint fields (name, pieces,
    /// snap points) with FiresDungeonMaster-specific metadata: footprint (XZ grid cells),
    /// story count, module role (center/edge/doorway/corridor/entry/exit/junction/transition),
    /// dungeon-type name (the grouping/compatibility scope), per-edge tags, and snap-type.
    ///
    /// Piece data matches VABlueprintLoader.Piece structure:
    ///   name;id;x;y;z;qx;qy;qz;qw;extraInfo;sx;sy;sz;data;chance
    /// (15 columns, indices 0-14).
    ///
    /// Can optionally wrap VABlueprintLoader.Blueprint or use its own parallel model.
    /// This implementation uses a clean parallel model to avoid circular dependencies.
    /// </summary>
    public class SectionBlueprint
    {
        /// <summary>
        /// Stable, immutable section ID (hash-based from footprint+stories+role+dungeontype).
        /// Used as the ZNetScene/DungeonDB key, NOT the display name.
        /// </summary>
        public string SectionId { get; set; } = "";

        /// <summary>
        /// Display name (derived from .blueprint filename, no extension).
        /// Per FAP policy: filename is the source of truth for piece-tab labels.
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Optional description (from #description: header).
        /// </summary>
        public string Description { get; set; } = "";

        /// <summary>
        /// Optional category (from #category: header).
        /// Used by Infinity-Hammer for menu grouping.
        /// </summary>
        public string Category { get; set; } = "";

        /// <summary>
        /// Optional center-piece name (from #center: header).
        /// Used by Center() to re-origin all pieces; this piece's position becomes (0,0,0),
        /// and its forward direction (+Z) orients the blueprint.
        /// </summary>
        public string CenterPiece { get; set; } = "";

        /// <summary>
        /// Coordinates from #coordinates: header (XZY order, Valheim convention).
        /// Typically (0, 0, 0); used for reference/documentation.
        /// </summary>
        public Vector3 Coordinates { get; set; } = Vector3.zero;

        /// <summary>
        /// Rotation from #rotation: header (XZY order, Valheim convention).
        /// Typically (0, 0, 0); used for reference/documentation.
        /// </summary>
        public Vector3 Rotation { get; set; } = Vector3.zero;

        /// <summary>
        /// Footprint: grid cells (X, Z) occupied by this section.
        /// e.g., (3, 2) = a 3-cell wide by 2-cell deep room in dungeon grid coordinates.
        /// </summary>
        public Vector2Int Footprint { get; set; } = Vector2Int.one;

        /// <summary>
        /// Story count / height tier (int >= 1).
        /// Multi-story sections stack vertically; single-story = 1.
        /// </summary>
        public int Stories { get; set; } = 1;

        /// <summary>
        /// Module role enum: center, edge, doorway, corridor, entry, exit, junction, transition, etc.
        /// Drives placement logic and snap-compatibility rules.
        /// </summary>
        public SectionRole Role { get; set; } = SectionRole.Center;

        /// <summary>
        /// Dungeon-type name (string): the grouping + compatibility scope.
        /// Auto-gen assembles a dungeon from all sections sharing this name.
        /// e.g., "crypt", "fortress", "mystical_cavern".
        /// </summary>
        public string DungeonType { get; set; } = "";

        /// <summary>
        /// Per-face edge tags (wall|doorway|open|back|left|right).
        /// Keys: "north", "south", "east", "west".
        /// </summary>
        public Dictionary<string, List<string>> EdgeTags { get; set; } = new Dictionary<string, List<string>>
        {
            { "north", new List<string>() },
            { "south", new List<string>() },
            { "east", new List<string>() },
            { "west", new List<string>() }
        };

        /// <summary>
        /// RoomConnection.m_type vocabulary string (e.g., "house_door", "cave_entrance").
        /// Used for snap-compatibility matching when joining sections.
        /// </summary>
        public string SnapType { get; set; } = "";

        /// <summary>
        /// Which outer face (0=N,1=E,2=S,3=W) is this section's dungeon MAIN ENTRANCE/EXIT — the mandatory m_entrance
        /// "first room" generation starts from. -1 = not an entrance section. In a set, only the entrance cell carries it.
        /// </summary>
        public int EntranceFace { get; set; } = -1;

        /// <summary>
        /// SET id: when a scaled (NxM-cell) box is diced on save, every resulting cell blueprint shares this id so
        /// they always place together in that fixed orientation. Empty = a standalone single-cell section.
        /// </summary>
        public string SetId { get; set; } = "";

        /// <summary>This cell's grid position (cellX, cellZ) within its set. Origin (0,0) = the -X/-Z corner cell.</summary>
        public Vector2Int SetPos { get; set; } = Vector2Int.zero;

        /// <summary>The whole set's grid size (cellsX, cellsZ). (0,0)/(1,1) = not part of a multi-cell set.</summary>
        public Vector2Int SetSize { get; set; } = Vector2Int.zero;

        /// <summary>True when this blueprint is one cell of a multi-cell set (SetId set AND set spans &gt; 1 cell).</summary>
        public bool IsSetMember => !string.IsNullOrEmpty(SetId) && (SetSize.x * SetSize.y) > 1;

        /// <summary>True when the .blueprint carried our #fdm_* headers (a DungeonMaster section); false = a plain
        /// PlanBuild / Infinity-Hammer blueprint. Set by SectionSchema.ParseFile; drives the bench's mode toggle.</summary>
        public bool IsDungeonSection { get; set; } = false;

        /// <summary>
        /// List of pieces (VABlueprintLoader.Piece compatible).
        /// Each piece: name, position, rotation (quaternion), scale, extraInfo, data blob (base64), chance.
        /// </summary>
        public List<Piece> Objects { get; set; } = new List<Piece>();

        /// <summary>
        /// Snap points (optional; typically empty for sections).
        /// </summary>
        public List<Vector3> SnapPoints { get; set; } = new List<Vector3>();

        /// <summary>
        /// Source file path (set by SectionLibrary when loading).
        /// </summary>
        public string SourcePath { get; set; } = "";

        /// <summary>
        /// Bounding-box radius (computed by Center()).
        /// </summary>
        public float Radius { get; set; } = 0f;

        /// <summary>
        /// Mint a stable section ID from footprint, stories, role, and dungeon type.
        /// Uses a simple hash: combines ToString() of each field.
        /// The ID should be immutable across saves/reloads (never use the display name).
        /// </summary>
        public void MintSectionId()
        {
            var key = $"{Footprint.x}x{Footprint.y}_{Stories}_{Role}_{DungeonType}";
            SectionId = GenerateStableHash(key);
        }

        /// <summary>
        /// Generate a stable hash from a string key (used for section IDs).
        /// Returns a hex string (8 chars) so it's safe for file/ZNetScene names.
        /// </summary>
        private static string GenerateStableHash(string key)
        {
            if (string.IsNullOrEmpty(key)) return "00000000";
            // Simple stable hash: sum up character codes, multiply by 31 (polynomial rolling hash).
            unchecked
            {
                uint hash = 5381;
                foreach (char c in key)
                    hash = ((hash << 5) + hash) ^ (uint)c;
                return hash.ToString("x8");
            }
        }

        /// <summary>
        /// Re-center every object and snap point so the blueprint's origin is either
        /// the named center piece (rotated so its forward becomes +Z) or,
        /// lacking that, the bounding-box center with Y at the lowest object.
        /// Mirrors InfinityHammer.Blueprint.Center().
        /// </summary>
        public Vector3 Center(string centerPieceOverride = "")
        {
            if (!string.IsNullOrEmpty(centerPieceOverride))
                CenterPiece = centerPieceOverride;

            if (Objects.Count == 0) return Vector3.zero;

            var bounds = new Bounds(Objects[0].Pos, Vector3.zero);
            float minY = float.MaxValue;
            Quaternion rot = Quaternion.identity;
            foreach (var p in Objects)
            {
                minY = Mathf.Min(minY, p.Pos.y);
                bounds.Encapsulate(p.Pos);
            }
            Vector3 center = new Vector3(bounds.center.x, minY, bounds.center.z);

            if (!string.IsNullOrEmpty(CenterPiece))
            {
                foreach (var p in Objects)
                {
                    if (p.Prefab == CenterPiece)
                    {
                        center = p.Pos;
                        rot = Quaternion.Inverse(p.Rot);
                        break;
                    }
                }
            }

            Radius = Mathf.Sqrt(bounds.extents.x * bounds.extents.x + bounds.extents.z * bounds.extents.z);

            foreach (var p in Objects) p.Pos -= center;
            for (int i = 0; i < SnapPoints.Count; i++) SnapPoints[i] -= center;

            if (rot != Quaternion.identity)
            {
                foreach (var p in Objects)
                {
                    p.Pos = rot * p.Pos;
                    p.Rot = rot * p.Rot;
                }
                for (int i = 0; i < SnapPoints.Count; i++)
                    SnapPoints[i] = rot * SnapPoints[i];
            }
            return center;
        }
    }

    /// <summary>
    /// Piece: a single decorated object in a section blueprint.
    /// Matches VABlueprintLoader.Piece and PlanBuild .blueprint piece-line column layout.
    /// </summary>
    public class Piece
    {
        /// <summary>
        /// Prefab name (e.g., "wood_wall", "stone_arch").
        /// </summary>
        public string Prefab = "";

        /// <summary>
        /// World position (X, Y, Z).
        /// </summary>
        public Vector3 Pos = Vector3.zero;

        /// <summary>
        /// Quaternion rotation.
        /// </summary>
        public Quaternion Rot = Quaternion.identity;

        /// <summary>
        /// Scale (X, Y, Z). Defaults to (1, 1, 1).
        /// </summary>
        public Vector3 Scale = Vector3.one;

        /// <summary>
        /// Extra metadata string (e.g., "door", "loot_anchor").
        /// Not parsed by stock PlanBuild; reserved for future FiresDungeonMaster extensions.
        /// </summary>
        public string ExtraInfo = "";

        /// <summary>
        /// Base64-encoded ZPackage blob with custom object data.
        /// Opaque to the loader; lossless round-trip if preserved.
        /// </summary>
        public string Data = "";

        /// <summary>
        /// Spawn probability (0..1). Defaults to 1.0 (always spawn).
        /// </summary>
        public float Chance = 1f;
    }

    /// <summary>
    /// Section role enum: the module's placement role in the dungeon.
    /// Drives snap-compatibility and layout logic.
    /// </summary>
    public enum SectionRole
    {
        /// <summary>Central chamber (hub, spawning point).</summary>
        Center = 0,

        /// <summary>Wall/perimeter room (connects via doorways).</summary>
        Edge = 1,

        /// <summary>Doorway/transition piece (minimal decoration).</summary>
        Doorway = 2,

        /// <summary>Long corridor (connects multiple rooms).</summary>
        Corridor = 3,

        /// <summary>Entry/entrance chamber.</summary>
        Entry = 4,

        /// <summary>Exit/boss chamber.</summary>
        Exit = 5,

        /// <summary>Junction (crossroads, T-junction).</summary>
        Junction = 6,

        /// <summary>Transition/stair/ramp (multi-story connector).</summary>
        Transition = 7
    }
}
