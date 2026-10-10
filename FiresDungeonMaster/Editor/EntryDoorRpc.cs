using System;
using HarmonyLib;
using UnityEngine;

namespace FiresDungeonMaster.Editor
{
    /// <summary>
    /// Routed RPC so a CLIENT connected to a dedicated server can trigger an entry door's dungeon generation.
    /// Mirrors FiresMausoleum.MausoleumSpawnRpc exactly: registered on ZNet.Start (both peers), the client packs the
    /// door's ZDOID and invokes the server; the server topology-gates (IsServer) + admin-gates
    /// (FiresCore.Sync.AdminSyncing.IsAdmin(sender)), resolves the door instance from the ZDOID, reads its ZDO-synced
    /// config, and runs EntryDoorGeneration.GenerateForDoor. The host/dedi-console path stays direct (EntryDoorController
    /// short-circuits before sending).
    /// </summary>
    internal static class EntryDoorRpc
    {
        private const string RpcName = "FiresDungeonMaster_GenerateDoor"; // admin client -> server
        private const string RpcNameCleanup = "FiresDungeonMaster_DeleteDungeon"; // admin client -> server (door removed)
        private const string RpcNameResult = "FiresDungeonMaster_GenerateDoorResult"; // server -> requesting client
        private static bool _registered;

        [HarmonyPatch(typeof(ZNet), "Start")]
        internal static class ZNet_Start_Patch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                try { Register(); }
                catch (Exception ex) { Debug.LogError($"[FiresDungeonMaster] door RPC register failed: {ex}"); }
            }
        }

        [HarmonyPatch(typeof(ZNet), "Shutdown")]
        internal static class ZNet_Shutdown_Patch
        {
            [HarmonyPostfix]
            private static void Postfix() { _registered = false; }
        }

        private static void Register()
        {
            if (ZRoutedRpc.instance == null || _registered) return;
            _registered = true;
            ZRoutedRpc.instance.Register<ZPackage>(RpcName, new Action<long, ZPackage>(OnServerReceive));
            ZRoutedRpc.instance.Register<ZPackage>(RpcNameCleanup, new Action<long, ZPackage>(OnServerReceiveCleanup));
            // Server -> requesting client: the real generation result. Registered on both peers (only the client that
            // asked ever receives its own result).
            ZRoutedRpc.instance.Register<ZPackage>(RpcNameResult, new Action<long, ZPackage>(OnClientReceiveResult));
        }

        // Server → the requesting client: deliver the true outcome so the client stops showing the optimistic
        // "requesting…" string and reports whether generation actually worked. The door's ZDOID rides along so the
        // client can set the Generated flag on ITS OWN door ZDO — the client placed the door and OWNS that ZDO, so a
        // server-side write is authoritative-fought and gets clobbered by the owner; the owner writing it is what
        // actually sticks (that was the "generated on the server but the door still says not generated" bug).
        private static void SendResultToClient(long target, bool success, string message, ZDOID doorZid)
        {
            if (ZRoutedRpc.instance == null || target == 0L) return;
            var pkg = new ZPackage();
            pkg.Write(success);
            pkg.Write(message ?? "");
            pkg.Write(doorZid);
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcNameResult, pkg);
        }

        private static void OnClientReceiveResult(long sender, ZPackage pkg)
        {
            try
            {
                bool success = pkg.ReadBool();
                string message = pkg.ReadString();
                ZDOID doorZid = pkg.ReadZDOID();

                // Persist Generated on the client-owned door ZDO so the hover/interact immediately reflect success.
                if (success && doorZid != ZDOID.None)
                {
                    GameObject go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(doorZid) : null;
                    var door = go != null ? go.GetComponent<EntryDoorController>() : null;
                    if (door != null)
                    {
                        door.Generated = true;
                        // Empty-room doors: register the spec on THIS client NOW, so the freshly-spawned interior's
                        // Portal ZDO (exit + landing pad) resolves here without waiting for a zone reload.
                        door.EnsureSpecOnThisPeer();
                    }
                }

                string tag = success ? "[Dungeon]" : "[Dungeon — failed]";
                Debug.Log($"[FiresDungeonMaster] generate result from server: success={success}, {message}");
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, $"{tag} {message}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FiresDungeonMaster] generate-result handler threw: {ex}");
            }
        }

        /// <summary>Ask the server to tear down the dungeon a removed door spawned (door pos → interior at +5000).</summary>
        public static void RequestCleanup(Vector3 doorPos)
        {
            if (ZRoutedRpc.instance == null) return;
            ZNetPeer server = ZNet.instance?.GetServerPeer();
            if (server == null) return;
            var pkg = new ZPackage();
            pkg.Write(doorPos);
            ZRoutedRpc.instance.InvokeRoutedRPC(server.m_uid, RpcNameCleanup, pkg);
        }

        private static void OnServerReceiveCleanup(long sender, ZPackage pkg)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (!FiresCore.Sync.AdminSyncing.IsAdmin(sender))
                {
                    Debug.LogWarning($"[FiresDungeonMaster] non-admin {sender} requested dungeon cleanup; refusing.");
                    return;
                }
                Vector3 doorPos = pkg.ReadVector3();
                EntryDoorGeneration.CleanupDungeonForDoor(doorPos);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FiresDungeonMaster] cleanup RPC handler threw: {ex}");
            }
        }

        public static string RequestGenerate(EntryDoorController door, Vector3 pos, Quaternion rot)
        {
            if (ZRoutedRpc.instance == null) return "network not ready.";
            ZNetPeer server = ZNet.instance?.GetServerPeer();
            if (server == null) return "no server peer (are you connected?).";

            var nview = door != null ? door.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid()) return "door is not networked yet.";

            var pkg = new ZPackage();
            pkg.Write(nview.GetZDO().m_uid);   // the door resolves its own config from its ZDO on the server
            ZRoutedRpc.instance.InvokeRoutedRPC(server.m_uid, RpcName, pkg);
            // Neutral pending string — the true success/failure arrives async via RpcNameResult and replaces this.
            return "requesting dungeon generation from server…";
        }

        private static void OnServerReceive(long sender, ZPackage pkg)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return; // topology gate
                if (!FiresCore.Sync.AdminSyncing.IsAdmin(sender))
                {
                    Debug.LogWarning($"[FiresDungeonMaster] non-admin {sender} requested door generation; refusing.");
                    SendResultToClient(sender, false, "refused: you are not an admin on this server.", ZDOID.None);
                    return; // admin gate
                }

                ZDOID zid = pkg.ReadZDOID();
                GameObject go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zid) : null;
                var door = go != null ? go.GetComponent<EntryDoorController>() : null;
                if (door == null)
                {
                    Debug.LogWarning($"[FiresDungeonMaster] door {zid} not instanced on server (out of range?) — cannot generate.");
                    SendResultToClient(sender, false, "server could not find the door (out of loaded range?). Stand near it and retry.", ZDOID.None);
                    return;
                }

                string result = EntryDoorGeneration.GenerateForDoor(door, door.transform.position, door.transform.rotation);
                bool success = !string.IsNullOrEmpty(result) && result.StartsWith("dungeon spawned", StringComparison.OrdinalIgnoreCase);
                Debug.Log($"[FiresDungeonMaster] admin {sender} door generate: success={success}, {result}");
                SendResultToClient(sender, success, result, zid);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FiresDungeonMaster] door RPC handler threw: {ex}");
                SendResultToClient(sender, false, "server error during generation (see server log).", ZDOID.None);
            }
        }
    }
}
