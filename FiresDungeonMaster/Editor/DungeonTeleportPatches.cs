using System;
using HarmonyLib;

namespace FiresDungeonMaster.Editor
{
    /// <summary>
    /// Routes entry/exit for our generated dungeons through the FiresDungeonMaster flow instead of the raw vanilla
    /// <see cref="Teleport"/> behaviour:
    ///   • the walk-in trigger (<c>Teleport.OnTriggerEnter</c>) is blocked on our dungeon teleports — you can't stumble
    ///     in, and you can't bypass a LOCKED door by walking into its gateway;
    ///   • the surface GATEWAY's own hover + [E] are suppressed so ONLY the entry door drives entry (through its lock
    ///     gate). The door calls the gateway's vanilla <c>Interact</c> behind <see cref="DoorDrivingTeleport"/> so the
    ///     real teleport logic (boss-portal check, stats, enter text) still runs for that one driven call;
    ///   • the interior EXIT keeps its vanilla [E] (leaving is unrestricted); only its walk-in trigger is blocked so you
    ///     can't get wedged in a mis-oriented arch.
    /// Our teleports are identified by the baked "FiresDM_" name prefix, so vanilla portals/dungeon entrances are never
    /// touched. Pure client-side interaction; harmless on a dedicated server (no local player ever triggers these).
    /// </summary>
    internal static class DungeonTeleportPatches
    {
        // Raised by the entry door while it drives the gateway's teleport itself, so the Interact gate lets that ONE
        // call through (any other caller — i.e. a direct player interact on the gateway — stays blocked).
        internal static bool DoorDrivingTeleport;

        private static bool IsOurTeleport(Teleport t) =>
            t != null && t.gameObject.name.IndexOf("FiresDM_", StringComparison.Ordinal) >= 0;

        private static bool IsGateway(Teleport t) =>
            t != null && t.gameObject.name.IndexOf("Gateway", StringComparison.Ordinal) >= 0;

        private static bool IsExit(Teleport t) =>
            t != null && t.gameObject.name.IndexOf("Exit", StringComparison.Ordinal) >= 0;

        // Block the walk-in auto-teleport on every one of our dungeon teleports (gateway + exit).
        [HarmonyPatch(typeof(Teleport), "OnTriggerEnter")]
        internal static class Teleport_OnTriggerEnter_Block
        {
            private static bool Prefix(Teleport __instance) => !IsOurTeleport(__instance);
        }

        // Suppress the surface gateway's own hover so only the entry door's [E] shows (the exit keeps its Exit hover).
        [HarmonyPatch(typeof(Teleport), "GetHoverText")]
        internal static class Teleport_GetHoverText_Suppress
        {
            private static bool Prefix(Teleport __instance, ref string __result)
            {
                if (IsOurTeleport(__instance) && IsGateway(__instance)) { __result = ""; return false; }
                return true;
            }
        }

        // Gate our dungeon teleports' [E]:
        //   • the surface GATEWAY is blocked unless the entry door is driving it (the lock bypass);
        //   • the interior EXIT returns the player to the ENTRY DOOR (you leave where you came in) instead of the
        //     gateway, which now sits at the zone centre after the zone-centre generation fix. Falls through to vanilla
        //     (lands at the gateway) if no door is found. Client-only (guards on the local player).
        [HarmonyPatch(typeof(Teleport), "Interact")]
        internal static class Teleport_Interact_Gate
        {
            private static bool Prefix(Teleport __instance, Humanoid character, bool hold, ref bool __result)
            {
                if (!IsOurTeleport(__instance)) return true;

                if (IsGateway(__instance) && !DoorDrivingTeleport) { __result = false; return false; }

                if (IsExit(__instance) && !hold)
                {
                    var p = character as Player;
                    if (p != null && p == Player.m_localPlayer && EntryDoorController.ExitToDoor(__instance.transform.position, p))
                    {
                        __result = true;
                        return false;
                    }
                }
                return true;
            }
        }
    }
}
