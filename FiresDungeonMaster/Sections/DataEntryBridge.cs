using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FiresDungeonMaster.Sections
{
    /// <summary>
    /// Reflection bridge to the shared <c>Data.DataEntry</c> type (ships in ServerDevcommands / WorldEditCommands /
    /// ExpandWorld) — the exact format Infinity Hammer writes into a blueprint's per-piece data column
    /// (HammerSaveCommand: <c>data.GetBase64(pars)</c>). It lets us RESTORE that data onto a freshly-spawned ZDO the
    /// same way IH/WEC paste does (its <c>Write(pars, zdo)</c> applies straight into ZDOExtraData by uid), so a placed
    /// imported blueprint keeps its container inventories / sign text / tags — matching IH's "preserve data" toggle.
    ///
    /// Reflection-only (same no-hard-dependency pattern as <see cref="FiresDungeonMaster.Editor.FiresUndoBridge"/>): a
    /// no-op that returns false if the Data assembly isn't installed, so FiresDungeonMaster carries no compile-time dep.
    /// </summary>
    internal static class DataEntryBridge
    {
        private static bool _resolved;
        private static ConstructorInfo _ctorBase64;
        private static MethodInfo _writeToZdo;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                Type t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { t = asm.GetType("Data.DataEntry", false); } catch { t = null; }
                    if (t != null) break;
                }
                if (t == null)
                {
                    FiresLogger.LogInfo("Data.DataEntry not present (no ServerDevcommands/WorldEdit/ExpandWorld) — imported-blueprint data restore uses our format only.");
                    return;
                }
                _ctorBase64 = t.GetConstructor(new[] { typeof(string) });
                _writeToZdo = t.GetMethod("Write", new[] { typeof(Dictionary<string, string>), typeof(ZDO) });
                if (_ctorBase64 == null || _writeToZdo == null)
                    FiresLogger.LogWarning($"Data.DataEntry found but signature mismatch (ctor={_ctorBase64 != null}, write={_writeToZdo != null}).");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"DataEntry resolve failed: {ex.Message}"); }
        }

        public static bool IsAvailable { get { Resolve(); return _ctorBase64 != null && _writeToZdo != null; } }

        /// <summary>
        /// Decode an Infinity-Hammer/WEC DataEntry base64 blob and write it onto <paramref name="zdo"/>. Returns false
        /// if the type isn't available or the blob isn't that format (ctor throws) — the caller then tries other decoders.
        /// The empty <c>pars</c> dictionary means no template substitution (a captured blueprint holds concrete values).
        /// </summary>
        public static bool TryApply(ZDO zdo, string base64)
        {
            if (zdo == null || string.IsNullOrEmpty(base64)) return false;
            Resolve();
            if (_ctorBase64 == null || _writeToZdo == null) return false;
            try
            {
                object entry = _ctorBase64.Invoke(new object[] { base64 });
                if (entry == null) return false;
                _writeToZdo.Invoke(entry, new object[] { new Dictionary<string, string>(), zdo });
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"DataEntry restore failed: {ex.Message}");
                return false;
            }
        }
    }
}
