using System;
using System.Collections.Generic;
using FiresDungeonMaster.Sections;
using HarmonyLib;
using UnityEngine;
// NOTE: the unqualified name 'Piece' resolves to vanilla UnityEngine/assembly_valheim Piece (the build piece) in this
// namespace, so the SECTION data model is referred to as Sections.Piece everywhere below.

namespace FiresDungeonMaster.Generation
{
    /// <summary>
    /// Spawns each runtime room's captured INTERIOR pieces (the section's <see cref="SectionBlueprint.Objects"/>)
    /// after the vanilla DungeonGenerator places that room — mirroring ExpandWorldData.Dungeon.Spawner's
    /// PlaceRoomCustomObjects postfix. Our rooms are empty shells (size + connections only); their geometry IS these
    /// pieces, spawned RELATIVE to the placed room pos/rot.
    ///
    /// Server-authoritative: gated on IsServer + mode != Client. Each piece is a REAL bundle/vanilla prefab spawned
    /// via Object.Instantiate, so its own ZNetView.Awake creates a persistent, replicated ZDO (the validated FAP
    /// SpawnBlueprintAt path). Deterministic seed off the placed room position so every peer/regen lands identically.
    /// Dedi-safe (no UI / renderer touch); MUST run headless, so it is intentionally NOT added to
    /// DedicatedServerSkipPatchTypes.
    /// </summary>
    [HarmonyPatch(typeof(DungeonGenerator))]
    internal static class DungeonMasterInteriorPatch
    {
        // DungeonGenerator.PlaceRoom(DungeonDB.RoomData roomData, Vector3 pos, Quaternion rot,
        //                            RoomConnection fromConnection, ZoneSystem.SpawnMode mode).
        // Harmony binds injected parameters by NAME — the first parameter MUST be named 'roomData' (not 'room'),
        // matching vanilla (DungeonGenerator.cs:662-667) + EWD, or the patch silently fails to apply and no interior
        // pieces ever spawn.
        [HarmonyPatch("PlaceRoom", new Type[]
        {
            typeof(DungeonDB.RoomData), typeof(Vector3), typeof(Quaternion),
            typeof(RoomConnection), typeof(ZoneSystem.SpawnMode)
        })]
        [HarmonyPostfix]
        private static void PlaceRoom_Postfix(DungeonDB.RoomData roomData, Vector3 pos, Quaternion rot, ZoneSystem.SpawnMode mode)
        {
            try
            {
                if (mode == ZoneSystem.SpawnMode.Client) return;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (roomData == null || !roomData.m_prefab.IsValid) return;

                string roomName = roomData.m_prefab.Name;
                if (string.IsNullOrEmpty(roomName)) return;
                if (!DungeonMasterRoomFactory.InteriorByRoomName.TryGetValue(roomName, out var pieces) || pieces == null || pieces.Count == 0)
                    return;

                SpawnInterior(pieces, pos, rot, roomName);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FiresDungeonMaster] interior spawn for room failed: {ex}");
            }
        }

        private static void SpawnInterior(List<Sections.Piece> pieces, Vector3 roomPos, Quaternion roomRot, string roomName)
        {
            var znet = ZNetScene.instance;
            if (znet == null) return;

            int seed = (int)roomPos.x * 4271 + (int)roomPos.y * 9187 + (int)roomPos.z * 2134;
            UnityEngine.Random.State prev = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
            int placed = 0;
            try
            {
                foreach (var piece in pieces)
                {
                    if (piece == null || string.IsNullOrEmpty(piece.Prefab)) continue;
                    if (Sections.SectionCenterpoint.Is(piece)) continue;   // centerpoint fallback marker — not real interior content
                    if (piece.Chance < 1f && UnityEngine.Random.value > piece.Chance) continue;

                    GameObject prefab = znet.GetPrefab(piece.Prefab);
                    if (prefab == null) continue; // unknown prefab (mod not installed) — skip, never crash the dungeon.

                    Vector3 spawnPos = roomPos + roomRot * piece.Pos;
                    Quaternion spawnRot = roomRot * piece.Rot;

                    GameObject go = UnityEngine.Object.Instantiate(prefab, spawnPos, spawnRot);
                    if (go == null) continue;
                    if (piece.Scale != Vector3.one && piece.Scale != Vector3.zero)
                        go.transform.localScale = piece.Scale;
                    IhPlacement.Apply(go);   // 0.1.180: Infinity Hammer's settings (Core API, soft)
                    placed++;
                }
            }
            finally
            {
                UnityEngine.Random.state = prev;
            }

            if (placed > 0)
                FiresLogger.LogVerbose($"placed {placed} interior piece(s) for room '{roomName}' at {roomPos}.");
        }
    }
}
