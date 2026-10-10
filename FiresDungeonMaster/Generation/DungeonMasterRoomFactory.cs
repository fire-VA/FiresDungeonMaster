using System.Collections.Generic;
using System.Linq;
using FiresCore.Net;
using FiresDungeonMaster.Sections;
using UnityEngine;
// NOTE: the unqualified name 'Piece' resolves to vanilla UnityEngine/assembly_valheim Piece (the build piece) in this
// namespace, so the SECTION data model is referred to as Sections.Piece everywhere below.

namespace FiresDungeonMaster.Generation
{
    /// <summary>
    /// Turns a captured <see cref="SectionBlueprint"/> into a RUNTIME-built vanilla <see cref="Room"/> (the same
    /// technique ExpandWorldData.RoomLoading.CreateRoomData uses): a bare new GameObject + Room component whose
    /// <see cref="Room.m_size"/> comes from the section footprint, with one <see cref="RoomConnection"/> child per
    /// DOORWAY-tagged edge placed at that edge's midpoint and oriented OUTWARD (the +Z/forward convention vanilla
    /// CalculateRoomPosRot expects). Each connection bakes a SOLID 'DoorBlocker' child (so the Core
    /// <see cref="FiresCore.Dungeon.DungeonDoorBlockerController"/> can wall off / open it per-peer, exactly like a
    /// baked crypt room). The Room is never added to ZNetScene.m_prefabs — it lives only in DungeonDB (addressed by
    /// SoftReference / m_loadedRoom), so it sidesteps the empty-AssetID runtime-prefab trap. Its real geometry is the
    /// section's interior PIECES, spawned separately by <see cref="DungeonMasterInteriorPatch"/>.
    ///
    /// FIX #3 (origin colliders): the room template is parked UNDER the permanently-inactive Fires_PrefabHolder
    /// (NetworkObjectHelper.PrefabHolder, DontDestroyOnLoad), NOT left active at world origin. Its root keeps
    /// activeSelf==true (it is only inactive-in-HIERARCHY because the holder is inactive), so its baked DoorBlocker
    /// colliders are NOT live at (0,0,0). DungeonGenerator.PlaceRoom does SoftReferenceableAssets.Utils.Instantiate
    /// on the room ROOT and re-parents the clone under the DG (active hierarchy), which activates the placed copy —
    /// so the controller + blockers come alive on the placed clone exactly like a normal baked room prefab, while the
    /// template itself never has live colliders at origin. Verified against DungeonGenerator.cs:662-734.
    /// </summary>
    internal static class DungeonMasterRoomFactory
    {
        /// <summary>Vertical metres per story tier — drives Room.m_size.y (height must fit the 64-zone too).</summary>
        internal const int StoryHeight = 8;

        /// <summary>One footprint cell = 8 metres (matches SectionEditSpawner/plan renderer + the baked DefaultBox 8x_x8).
        /// Room.m_size is in METRES — the footprint is in CELLS, so it must be multiplied by this.</summary>
        private const int CellMetres = 8;

        /// <summary>Vanilla DungeonGenerator.m_zoneSize default is 64; a room's size corners must fit inside it.</summary>
        private const int ZoneFit = 64;

        // Solid blocker slab dimensions (mirror FiresMausoleumBuilder.AddDoorBlocker: DoorW/H + margin, depth>wall).
        private const float DoorW = 3.0f;
        private const float DoorH = 3.0f;
        private const float BlockerDepth = 0.7f;

        /// <summary>The doorway edge tags that become a RoomConnection (case-insensitive). "open" is a passable join.</summary>
        private static readonly HashSet<string> DoorwayTags =
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "doorway", "open" };

        /// <summary>
        /// The interior-piece table: runtime room NAME -> the section's Objects (pos/rot/scale RELATIVE to the
        /// section's footprint center). <see cref="DungeonMasterInteriorPatch"/> reads this in its PlaceRoom postfix
        /// keyed on the placed room's name. Rebuilt every time a spec is (re)built; keyed by name so a section edit
        /// that re-mints the same deterministic name replaces its interior cleanly.
        /// </summary>
        internal static readonly Dictionary<string, List<Sections.Piece>> InteriorByRoomName =
            new Dictionary<string, List<Sections.Piece>>(System.StringComparer.Ordinal);

        /// <summary>
        /// Deterministic, collision-free room name for a section. SectionId alone collides across sections that share
        /// footprint+stories+role+dungeontype (P1 mint key excludes Name), so the author-given Name is folded in and
        /// the result is stable-hashed. Keeping this stable across sessions is what lets DungeonGenerator.Load
        /// resolve a saved room by hash (DungeonGenerator.Save stores only the hash+pos+rot).
        /// </summary>
        internal static string RoomName(SectionBlueprint section)
        {
            string dt = string.IsNullOrEmpty(section.DungeonType) ? "none" : section.DungeonType;
            string disc = string.IsNullOrEmpty(section.Name) ? section.SectionId : section.Name;
            string suffix = StableHex($"{section.SectionId}|{disc}");
            return $"FiresDM_{Sanitize(dt)}_{suffix}";
        }

        /// <summary>
        /// Build the runtime Room shell for one section. Returns the Room (its GameObject is the room prefab the
        /// DungeonDB SoftReference resolves to). Also records the section's Objects into <see cref="InteriorByRoomName"/>.
        /// The template is parked inactive-in-hierarchy under Fires_PrefabHolder (no live origin colliders).
        /// </summary>
        internal static Room BuildRoom(SectionBlueprint section, Room.Theme theme)
        {
            string name = RoomName(section);

            // Park the template under the permanently-inactive Fires_PrefabHolder: its root activeSelf stays true
            // (so PlaceRoom's Utils.Instantiate yields an active placed clone) while it is inactive-in-hierarchy, so
            // its baked DoorBlocker colliders are never live at world origin. DontDestroyOnLoad rides on the holder.
            var go = new GameObject(name);
            go.transform.SetParent(NetworkObjectHelper.PrefabHolder, false);
            var room = go.AddComponent<Room>();

            // Footprint is in CELLS; Room.m_size is in METRES → multiply by CellMetres (8). The old code used the raw
            // cell count, so a 1x1 section was a 1m room with connections 0.5m apart — the generator could never chain
            // them on the 8m grid (that's why custom dungeons packed into a tiny clump).
            int fx = Mathf.Max(1, section.Footprint.x) * CellMetres;
            int fz = Mathf.Max(1, section.Footprint.y) * CellMetres;
            int fy = Mathf.Max(1, section.Stories) * StoryHeight;

            room.m_size = new Vector3Int(
                Mathf.Clamp(fx, 1, ZoneFit),
                Mathf.Clamp(fy, 1, ZoneFit),
                Mathf.Clamp(fz, 1, ZoneFit));

            room.m_theme = theme;
            room.m_enabled = true;
            room.m_weight = 1f;
            room.m_faceCenter = false;
            room.m_perimeter = false;
            room.m_divider = false;

            // Explicit per-face EntranceFace is authoritative (only ONE cell of a set carries it); role is the legacy
            // fallback and is ignored for set members so a Center-role set doesn't turn every cell into an entrance.
            bool isEntrance = section.EntranceFace >= 0 || (!section.IsSetMember && IsEntranceRole(section.Role));
            bool isEndCap = IsEndCapRole(section.Role);
            room.m_entrance = isEntrance;
            room.m_endCap = isEndCap;
            room.m_endCapPrio = isEndCap ? 1 : 0;
            room.m_minPlaceOrder = 0;

            // One shared connection type per dungeon-type so any doorway mates any doorway (simplest reliable join).
            // RoomConnections are real CHILDREN; Room.GetConnections() (publicized) lazily discovers them via
            // GetComponentsInChildren<RoomConnection>(false), and Room.OnEnable nulls the private cache, so we do NOT
            // assign room.m_roomConnections directly (it would be wiped on activation) — child discovery is the
            // vanilla path and is desync-free.
            string connType = ConnectionType(section.DungeonType);
            BuildConnections(room, section, connType, isEntrance);

            // Record the interior (clone the list so a later capture re-save doesn't mutate the placed dungeon's copy).
            InteriorByRoomName[name] = section.Objects != null
                ? section.Objects.Select(Clone).ToList()
                : new List<Sections.Piece>();

            return room;
        }

        private static void BuildConnections(Room room, SectionBlueprint section, string connType, bool isEntrance)
        {
            Vector3 half = new Vector3(room.m_size.x / 2f, 0f, room.m_size.z / 2f);

            var faces = new (string face, Vector3 pos, Quaternion rot)[]
            {
                ("north", new Vector3(0f, 0f,  half.z), Quaternion.identity),
                ("south", new Vector3(0f, 0f, -half.z), Quaternion.Euler(0f, 180f, 0f)),
                ("east",  new Vector3( half.x, 0f, 0f),  Quaternion.Euler(0f,  90f, 0f)),
                ("west",  new Vector3(-half.x, 0f, 0f),  Quaternion.Euler(0f, 270f, 0f)),
            };

            // When a specific entrance face is set, the entrance connection goes on THAT face; otherwise (legacy
            // role-based) the first doorway-tagged face becomes the entrance.
            string entranceFace = (isEntrance && section.EntranceFace >= 0) ? FaceIndexName(section.EntranceFace) : null;
            bool entranceAssigned = false;
            foreach (var f in faces)
            {
                bool isEntFace = entranceFace != null && f.face == entranceFace;
                if (!IsDoorway(section, f.face) && !isEntFace) continue;   // doorway faces + the explicit entrance face

                bool isEntranceConn = isEntrance && !entranceAssigned && (entranceFace == null || isEntFace);
                AddConnection(room.transform, connType, f.pos, f.rot, isEntranceConn);
                if (isEntranceConn) entranceAssigned = true;
            }

            // An entrance room with NO doorway-tagged edge still needs an entrance connection or FindStartRoom crashes
            // (it calls GetEntrance() which returns the m_entrance connection). Synthesize on the entrance face (or north).
            if (isEntrance && !entranceAssigned)
            {
                var ef = entranceFace != null ? System.Array.Find(faces, x => x.face == entranceFace) : faces[0];
                AddConnection(room.transform, connType, ef.pos, ef.rot, entrance: true);
            }
        }

        private static string FaceIndexName(int f)
        {
            switch (f) { case 0: return "north"; case 1: return "east"; case 2: return "south"; default: return "west"; }
        }

        private static void AddConnection(Transform parent, string connType, Vector3 localPos, Quaternion localRot, bool entrance)
        {
            var cgo = new GameObject(connType);
            cgo.transform.SetParent(parent, false);
            cgo.transform.localPosition = localPos;
            cgo.transform.localRotation = localRot;

            var conn = cgo.AddComponent<RoomConnection>();
            conn.m_type = connType;
            conn.m_allowDoor = true;
            conn.m_doorOnlyIfOtherAlsoAllowsDoor = false;
            conn.m_entrance = entrance;

            // Bake a SOLID blocker slab as a child of the connection. The connection's forward (+Z local) is the
            // OUTWARD wall normal; the slab fills the opening. The Core DungeonDoorBlockerController (attached to the
            // room by FootprintRegistration.AttachDoorBlockerToRoom) opens entrance/paired doorways per-peer and
            // leaves unused ones solid. Child name MUST equal DungeonDoorBlockerController.BlockerChildName.
            AddDoorBlocker(cgo.transform);
        }

        private static void AddDoorBlocker(Transform conn)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); // ships a solid BoxCollider (isTrigger=false)
            go.name = DungeonMasterNames.DoorBlockerChild; // "DoorBlocker"
            go.transform.SetParent(conn, false);
            go.transform.localPosition = new Vector3(0f, DoorH * 0.5f, BlockerDepth * 0.5f);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = new Vector3(DoorW + 0.4f, DoorH + 0.4f, BlockerDepth);
            // The blocker rides the room template under the inactive Fires_PrefabHolder, so its collider is not live
            // at world origin; PlaceRoom's Utils.Instantiate activates it on the placed clone. The default material's
            // MeshRenderer is inert on a headless server and the placed dungeon is at y+5000; the collider is the point.
            go.SetActive(true);
        }

        private static bool IsDoorway(SectionBlueprint section, string face)
        {
            if (section.EdgeTags == null) return false;
            if (!section.EdgeTags.TryGetValue(face, out var tags) || tags == null) return false;
            return tags.Any(t => DoorwayTags.Contains(t));
        }

        private static bool IsEntranceRole(SectionRole role) =>
            role == SectionRole.Entry || role == SectionRole.Center;

        private static bool IsEndCapRole(SectionRole role) =>
            role == SectionRole.Exit || role == SectionRole.Edge;

        /// <summary>One shared connection type per dungeon-type: any doorway mates any doorway.</summary>
        internal static string ConnectionType(string dungeonType) =>
            $"FiresDM_{Sanitize(string.IsNullOrEmpty(dungeonType) ? "none" : dungeonType)}_door";

        private static Sections.Piece Clone(Sections.Piece p) => new Sections.Piece
        {
            Prefab = p.Prefab,
            Pos = p.Pos,
            Rot = p.Rot,
            Scale = p.Scale,
            ExtraInfo = p.ExtraInfo,
            Data = p.Data,
            Chance = p.Chance,
        };

        private static string Sanitize(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        // DJB2, same family as SectionBlueprint.GenerateStableHash — deterministic 8-hex.
        private static string StableHex(string key)
        {
            if (string.IsNullOrEmpty(key)) return "00000000";
            unchecked
            {
                uint hash = 5381;
                foreach (char c in key)
                    hash = ((hash << 5) + hash) ^ (uint)c;
                return hash.ToString("x8");
            }
        }
    }
}
