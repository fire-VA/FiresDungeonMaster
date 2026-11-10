using System;
using HarmonyLib;
using UnityEngine;

namespace FiresDungeonMaster.Editor
{
    /// <summary>
    /// Routed RPC so a CLIENT connected to a dedicated server can trigger an entry door's dungeon generation.
    /// Mirrors FiresMausoleum.MausoleumSpawnRpc: registered on ZNet.Start (both peers), the client sends the door's ZDOID and
    /// config; the server topology-gates (IsServer) + admin-gates (FiresCore.Sync.AdminSyncing.IsAdmin(sender)), checks the ZDO is
    /// an Entry Door and runs EntryDoorGeneration.GenerateForDoor. 0.1.187: the server works from the ZDO, never a door instance:
    /// a dedi only instances objects around its own reference position, so a door in the world is normally not loaded there.
    /// The host/dedi-console path stays direct (EntryDoorController short-circuits before sending).
    /// </summary>
    internal static class EntryDoorRpc
    {
        private const string RpcName = "FiresDungeonMaster_GenerateDoor"; // admin client -> server
        private const string RpcNameCleanup = "FiresDungeonMaster_DeleteDungeon"; // admin client -> server (door removed)
        private const string RpcNameResult = "FiresDungeonMaster_GenerateDoorResult"; // server -> requesting client
        private static bool _registered;
        private const int MaxResultMessageLen = 256;   // 10-23 security audit

        [HarmonyPatch(typeof(ZNet), "Start")]
        internal static class ZNet_Start_Patch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                try { Register(); DungeonLink.HookFapEntrances(); ProxyEntrance.HookMenu(); }
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
        private static void SendResultToClient(long target, bool success, string message, ZDOID doorZid, long dungeonId)
        {
            if (ZRoutedRpc.instance == null || target == 0L) return;
            var pkg = new ZPackage();
            pkg.Write(success);
            pkg.Write(message ?? "");
            pkg.Write(doorZid);
            pkg.Write(dungeonId);
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcNameResult, pkg);
        }

        private static void OnClientReceiveResult(long sender, ZPackage pkg)
        {
            // 10-23 security audit (EntryDoorRpc.cs:66-86, Low): results only from the server (the host's own loop
            // arrives with the server id too), and the shown message is clamped.
            if (ZRoutedRpc.instance == null || sender != ZRoutedRpc.instance.GetServerPeerID()) return;
            try
            {
                bool success = pkg.ReadBool();
                string message = pkg.ReadString() ?? "";
                if (message.Length > MaxResultMessageLen) message = message.Substring(0, MaxResultMessageLen);
                ZDOID doorZid = pkg.ReadZDOID();
                long dungeonId = pkg.ReadLong();

                // Persist Generated + the dungeon id on the client-owned door ZDO so the hover/interact immediately reflect success.
                if (success && doorZid != ZDOID.None)
                {
                    GameObject go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(doorZid) : null;
                    var door = go != null ? go.GetComponent<EntryDoorController>() : null;
                    if (door != null)
                    {
                        door.DungeonId = dungeonId;
                        door.Generated = true;
                        // Empty-room doors: register the spec on THIS client NOW, so the freshly-spawned interior's
                        // Portal ZDO (exit + landing pad) resolves here without waiting for a zone reload.
                        door.EnsureSpecOnThisPeer();
                    }
                    else
                    {
                        // The admin walked away: the ZDO still takes the result, or the door would forget its dungeon.
                        ZDO zdo = ZDOMan.instance?.GetZDO(doorZid);
                        if (zdo != null)
                        {
                            zdo.SetOwner(ZDOMan.GetSessionID());
                            zdo.Set(DungeonLink.HashId, dungeonId);
                            zdo.Set(EntryDoorController.HASH_GEN, 1);
                        }
                    }
                }

                string tag = success ? "[Dungeon]" : "[Dungeon — failed]";
                FiresLogger.LogDebug($"[FiresDungeonMaster] generate result from server: success={success}, {message}");
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, $"{tag} {message}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FiresDungeonMaster] generate-result handler threw: {ex}");
            }
        }

        /// <summary>Ask the server to tear down a dungeon by its id, see <see cref="DungeonLink.Teardown"/>.</summary>
        public static void RequestCleanup(long dungeonId, ZDOID door, ZDOID keep, bool destroyDoor)
        {
            if (ZRoutedRpc.instance == null) return;
            ZNetPeer server = ZNet.instance?.GetServerPeer();
            if (server == null) return;
            var pkg = new ZPackage();
            pkg.Write(dungeonId);
            pkg.Write(door);
            pkg.Write(keep);
            pkg.Write(destroyDoor);
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
                long id = pkg.ReadLong();
                ZDOID door = pkg.ReadZDOID();
                ZDOID keep = pkg.ReadZDOID();
                bool destroyDoor = pkg.ReadBool();
                ZDO doorZdo = door != ZDOID.None ? ZDOMan.instance.GetZDO(door) : null;
                if (doorZdo == null || !DungeonLink.IsLinkable(doorZdo))
                {
                    Debug.LogWarning($"[FiresDungeonMaster] cleanup from {sender}: {door} is not an Entry Door here; refusing.");
                    return;
                }
                // The id must be this door's own, once the door's ZDO has it; an unsynced door may still read 0.
                long known = doorZdo.GetLong(DungeonLink.HashId, 0L);
                if (known != 0L && known != id)
                {
                    Debug.LogWarning($"[FiresDungeonMaster] cleanup from {sender}: id {id} is not door {door}'s dungeon {known}; refusing.");
                    return;
                }
                DungeonLink.Teardown(id, door, keep, destroyDoor);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FiresDungeonMaster] cleanup RPC handler threw: {ex}");
            }
        }

        public static string RequestGenerate(EntryDoorController door, EntryDoorGeneration.DoorConfig config) =>
            RequestGenerate(door != null ? door.GetComponent<ZNetView>() : null, config, door != null ? door.DungeonId : 0L);

        internal static string RequestGenerate(ZNetView nview, EntryDoorGeneration.DoorConfig config, long oldId)
        {
            if (ZRoutedRpc.instance == null) return "network not ready.";
            ZNetPeer server = ZNet.instance?.GetServerPeer();
            if (server == null) return "no server peer (are you connected?).";

            if (nview == null || !nview.IsValid()) return "door is not networked yet.";

            var pkg = new ZPackage();
            pkg.Write(nview.GetZDO().m_uid);
            pkg.Write(config.DungeonType ?? "");
            pkg.Write(config.Environment ?? "");
            pkg.Write(config.KeyPrefab ?? "");
            pkg.Write(config.IsVanilla);
            pkg.Write(config.EmptyBox);
            pkg.Write(oldId);
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
                    SendResultToClient(sender, false, "refused: you are not an admin on this server.", ZDOID.None, 0L);
                    return; // admin gate
                }

                ZDOID zid = pkg.ReadZDOID();
                var config = new EntryDoorGeneration.DoorConfig
                {
                    DungeonType = pkg.ReadString(),
                    Environment = pkg.ReadString(),
                    KeyPrefab = pkg.ReadString(),
                    IsVanilla = pkg.ReadBool(),
                    EmptyBox = pkg.ReadBool(),
                };
                long oldId = pkg.ReadLong();
                ZDO zdo = ZDOMan.instance.GetZDO(zid);
                if (zdo == null || !DungeonLink.IsLinkable(zdo))
                {
                    Debug.LogWarning($"[FiresDungeonMaster] door {zid} is not an Entry Door here (from {sender}) — cannot generate.");
                    SendResultToClient(sender, false, "the server has no such door yet. Wait a moment and retry.", ZDOID.None, 0L);
                    return;
                }
                // The id to replace comes from the door's ZDO when it has one; the client's value only covers a ZDO still syncing, and must be this door's.
                long known = zdo.GetLong(DungeonLink.HashId, 0L);
                if (known != 0L) oldId = known;
                Vector3 at = zdo.GetPosition(); Quaternion rot = zdo.GetRotation();
                zdo = ProxyEntrance.EmptyIfFull(zdo, out bool replaced, out string refused);
                if (zdo == null) { SendResultToClient(sender, false, refused, ZDOID.None, 0L); return; }
                if (replaced) { zid = zdo.m_uid; oldId = 0L; }
                string result = EntryDoorGeneration.GenerateForDoor(config, oldId, zid, at, rot, out long dungeonId);
                bool success = dungeonId != 0L;
                if (replaced && success) zdo.Set(DungeonLink.HashId, dungeonId);   // the server made this proxy and owns it; the client may not have it yet
                FiresLogger.LogDebug($"[FiresDungeonMaster] admin {sender} door generate: success={success}, {result}");
                SendResultToClient(sender, success, result, replaced ? ZDOID.None : zid, dungeonId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FiresDungeonMaster] door RPC handler threw: {ex}");
                SendResultToClient(sender, false, "server error during generation (see server log).", ZDOID.None, 0L);
            }
        }
    }
}
