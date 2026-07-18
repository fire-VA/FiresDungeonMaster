using System;
using System.Reflection;
using UnityEngine;

namespace FiresDungeonMaster.Editor
{
    /// <summary>
    /// Bridges our blueprint placements into the SHARED undo stack that World Edit Commands + Infinity Hammer use
    /// (WorldEditCommands.UndoHelper → ServerDevcommands.UndoManager), so a placed blueprint can be reverted with their
    /// undo/redo. Driven by REFLECTION so WorldEditCommands stays an OPTIONAL dependency — if it isn't loaded, every
    /// call is a no-op and placement still works, just without undo integration.
    ///
    /// Usage: Begin() → place objects → AddSpawn(zdo) for each spawned ZDO → End(). End() finalizes one undo action
    /// whose Undo removes exactly those ZDOs (and Redo re-spawns them) — the same shape IH's own placements register.
    /// </summary>
    internal static class FiresUndoBridge
    {
        private static bool _resolved;
        private static bool _ok;
        private static MethodInfo _begin, _end, _addSpawn;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                Type t = Type.GetType("WorldEditCommands.UndoHelper, WorldEditCommands");
                if (t == null)
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        t = a.GetType("WorldEditCommands.UndoHelper");
                        if (t != null) break;
                    }
                if (t == null) return;
                _begin = t.GetMethod("BeginAction", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                _end = t.GetMethod("EndAction", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                _addSpawn = t.GetMethod("AddSpawnAction", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ZDO) }, null);
                _ok = _begin != null && _end != null && _addSpawn != null;
                if (!_ok) FiresLogger.LogWarning("WorldEditCommands.UndoHelper found but its methods didn't resolve — placements won't be undoable.");
            }
            catch (Exception ex) { _ok = false; FiresLogger.LogWarning($"undo bridge resolve failed: {ex.Message}"); }
        }

        public static bool Available { get { Resolve(); return _ok; } }

        public static void Begin() { Resolve(); if (_ok) try { _begin.Invoke(null, null); } catch { } }
        public static void AddSpawn(ZDO zdo) { if (_ok && zdo != null) try { _addSpawn.Invoke(null, new object[] { zdo }); } catch { } }
        public static void End() { if (_ok) try { _end.Invoke(null, null); } catch { } }
    }
}
