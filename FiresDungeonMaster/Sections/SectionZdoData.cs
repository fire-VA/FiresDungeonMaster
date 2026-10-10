using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresDungeonMaster.Sections
{
    /// <summary>
    /// SectionZdoData - a self-contained, lossless codec for a single object's ZDO state, used so a captured
    /// section round-trips EVERY object's full data: container inventories, smelter/kiln/fireplace fuel and
    /// contents, sign text, portal tags, ward permissions, item:variant, tamed names, growth timers, doors,
    /// levers, beehives. This is the FiresDungeonMaster equivalent of Infinity Hammer / ServerDevcommands'
    /// DataEntry base64 blob (HammerSaveCommand writes data?.GetBase64(pars) into the .blueprint Data column),
    /// implemented against vanilla ZDOExtraData directly so FiresDungeonMaster carries NO ServerDevcommands
    /// soft-dependency.
    ///
    /// The blob captures ONLY the per-object typed key/value tables (floats / vec3s / quaternions / ints /
    /// longs / strings / byteArrays). It deliberately omits prefab id, uid and the ZDOConnection: those
    /// reference the SOURCE world's identity/topology and must NOT clobber the freshly-instantiated paste
    /// target (whose prefab + uid + ownership are already correct, and whose connection would point at a
    /// now-defunct ZDOID). Object POSITION is also not in these tables (it is m_position, set by the spawn
    /// transform), so restoring the blob never teleports a pasted object.
    ///
    /// Wire format (versioned): byte version=1; then for each of the 7 typed tables a (int count) header
    /// followed by count x (int keyHash, value). Empty string when the source ZDO carried no extra data.
    /// </summary>
    public static class SectionZdoData
    {
        private const byte Version = 1;
        // Leading int tag so Restore can tell OUR wire format from an imported Infinity-Hammer/WEC DataEntry blob in
        // the same data column ('F','D','M','Z'). Pre-tag captures (version byte first) are still read as legacy.
        private const int Magic = 0x46444D5A;

        /// <summary>
        /// Serialize a live ZDO's typed data tables to a base64 string. Returns "" when the ZDO is null/invalid
        /// or carries no extra data (a bare structural piece) - keeping the .blueprint Data column empty in that
        /// case, exactly like Infinity Hammer's data=false objects.
        /// </summary>
        public static string Capture(ZDO zdo)
        {
            if (zdo == null) return "";
            ZDOID uid = zdo.m_uid;
            if (uid == ZDOID.None) return "";

            List<KeyValuePair<int, float>> floats = FiresCore.Bridge.ZdoExtraDataAccess.GetFloats(uid);
            List<KeyValuePair<int, Vector3>> vec3s = FiresCore.Bridge.ZdoExtraDataAccess.GetVec3s(uid);
            List<KeyValuePair<int, Quaternion>> quats = FiresCore.Bridge.ZdoExtraDataAccess.GetQuaternions(uid);
            List<KeyValuePair<int, int>> ints = FiresCore.Bridge.ZdoExtraDataAccess.GetInts(uid);
            List<KeyValuePair<int, long>> longs = FiresCore.Bridge.ZdoExtraDataAccess.GetLongs(uid);
            List<KeyValuePair<int, string>> strings = FiresCore.Bridge.ZdoExtraDataAccess.GetStrings(uid);
            List<KeyValuePair<int, byte[]>> bytes = FiresCore.Bridge.ZdoExtraDataAccess.GetByteArrays(uid);

            int total = Count(floats) + Count(vec3s) + Count(quats) + Count(ints) +
                        Count(longs) + Count(strings) + Count(bytes);
            if (total == 0) return "";

            ZPackage pkg = new ZPackage();
            pkg.Write(Magic);
            pkg.Write(Version);

            WriteFloats(pkg, floats);
            WriteVec3s(pkg, vec3s);
            WriteQuats(pkg, quats);
            WriteInts(pkg, ints);
            WriteLongs(pkg, longs);
            WriteStrings(pkg, strings);
            WriteBytes(pkg, bytes);

            return pkg.GetBase64();
        }

        /// <summary>
        /// Apply a previously-captured base64 blob onto a freshly-instantiated object's ZDO, restoring all typed
        /// data so the pasted object reconstructs its source state (inventory, fuel, text, tags). Uses the
        /// high-level ZDO.Set overloads so each write bumps the data revision and replicates to peers. No-op on
        /// empty/invalid input. Caller must own the ZDO (true right after a server-side Object.Instantiate).
        /// </summary>
        public static void Restore(ZDO zdo, string base64)
        {
            if (zdo == null || string.IsNullOrEmpty(base64)) return;
            // Route by format: OUR magic-tagged capture first, then an imported Infinity-Hammer/WEC DataEntry blob
            // (so placed imported blueprints preserve container/sign/tag data like IH's copy-data toggle), then a
            // pre-magic FDM capture as a last resort (old worlds).
            if (TryRestoreTagged(zdo, base64)) return;
            if (DataEntryBridge.TryApply(zdo, base64)) return;
            TryRestoreLegacy(zdo, base64);
        }

        // Our current wire format: [int Magic][byte Version][7 typed tables].
        private static bool TryRestoreTagged(ZDO zdo, string base64)
        {
            ZPackage pkg;
            try { pkg = new ZPackage(base64); } catch { return false; }
            try
            {
                if (pkg.ReadInt() != Magic) return false;
                if (pkg.ReadByte() != Version) return false;
                ApplyTables(zdo, pkg);
                return true;
            }
            catch { return false; }
        }

        // Pre-magic FDM captures: [byte Version][7 typed tables]. Only used if the blob is neither tagged nor DataEntry.
        private static bool TryRestoreLegacy(ZDO zdo, string base64)
        {
            ZPackage pkg;
            try { pkg = new ZPackage(base64); } catch { return false; }
            try
            {
                if (pkg.ReadByte() != Version) return false;
                ApplyTables(zdo, pkg);
                return true;
            }
            catch { return false; }
        }

        // Read the 7 typed tables (positioned just past the version byte) and Set them onto the ZDO. High-level ZDO.Set
        // bumps the data revision so writes replicate. A truncated blob throws mid-way (caught by the caller) — a
        // partially-restored chest still beats aborting the whole paste.
        private static void ApplyTables(ZDO zdo, ZPackage pkg)
        {
            int nf = pkg.ReadInt();
            for (int i = 0; i < nf; i++) { int k = pkg.ReadInt(); zdo.Set(k, pkg.ReadSingle()); }

            int nv = pkg.ReadInt();
            for (int i = 0; i < nv; i++) { int k = pkg.ReadInt(); zdo.Set(k, pkg.ReadVector3()); }

            int nq = pkg.ReadInt();
            for (int i = 0; i < nq; i++) { int k = pkg.ReadInt(); zdo.Set(k, pkg.ReadQuaternion()); }

            int ni = pkg.ReadInt();
            for (int i = 0; i < ni; i++) { int k = pkg.ReadInt(); zdo.Set(k, pkg.ReadInt()); }

            int nl = pkg.ReadInt();
            for (int i = 0; i < nl; i++) { int k = pkg.ReadInt(); zdo.Set(k, pkg.ReadLong()); }

            int ns = pkg.ReadInt();
            for (int i = 0; i < ns; i++) { int k = pkg.ReadInt(); zdo.Set(k, pkg.ReadString()); }

            int nb = pkg.ReadInt();
            for (int i = 0; i < nb; i++) { int k = pkg.ReadInt(); zdo.Set(k, pkg.ReadByteArray()); }
        }

        // -- writers --------------------------------------------------------------------------------------------

        private static int Count<T>(List<KeyValuePair<int, T>> list) => list == null ? 0 : list.Count;

        private static void WriteFloats(ZPackage pkg, List<KeyValuePair<int, float>> list)
        {
            pkg.Write(Count(list));
            if (list == null) return;
            foreach (KeyValuePair<int, float> kv in list) { pkg.Write(kv.Key); pkg.Write(kv.Value); }
        }

        private static void WriteVec3s(ZPackage pkg, List<KeyValuePair<int, Vector3>> list)
        {
            pkg.Write(Count(list));
            if (list == null) return;
            foreach (KeyValuePair<int, Vector3> kv in list) { pkg.Write(kv.Key); pkg.Write(kv.Value); }
        }

        private static void WriteQuats(ZPackage pkg, List<KeyValuePair<int, Quaternion>> list)
        {
            pkg.Write(Count(list));
            if (list == null) return;
            foreach (KeyValuePair<int, Quaternion> kv in list) { pkg.Write(kv.Key); pkg.Write(kv.Value); }
        }

        private static void WriteInts(ZPackage pkg, List<KeyValuePair<int, int>> list)
        {
            pkg.Write(Count(list));
            if (list == null) return;
            foreach (KeyValuePair<int, int> kv in list) { pkg.Write(kv.Key); pkg.Write(kv.Value); }
        }

        private static void WriteLongs(ZPackage pkg, List<KeyValuePair<int, long>> list)
        {
            pkg.Write(Count(list));
            if (list == null) return;
            foreach (KeyValuePair<int, long> kv in list) { pkg.Write(kv.Key); pkg.Write(kv.Value); }
        }

        private static void WriteStrings(ZPackage pkg, List<KeyValuePair<int, string>> list)
        {
            pkg.Write(Count(list));
            if (list == null) return;
            foreach (KeyValuePair<int, string> kv in list) { pkg.Write(kv.Key); pkg.Write(kv.Value ?? ""); }
        }

        private static void WriteBytes(ZPackage pkg, List<KeyValuePair<int, byte[]>> list)
        {
            pkg.Write(Count(list));
            if (list == null) return;
            foreach (KeyValuePair<int, byte[]> kv in list) { pkg.Write(kv.Key); pkg.Write(kv.Value ?? Array.Empty<byte>()); }
        }
    }
}
