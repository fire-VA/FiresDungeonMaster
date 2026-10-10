using UnityEngine;
using FiresDungeonMaster.Sections;

namespace FiresDungeonMaster.UI
{
    /// <summary>
    /// SectionPlanRenderer — draws a top-down SCHEMATIC "blueprint" plan of a saved section into a Texture2D for the
    /// blueprint-bench PLAN VIEW: the footprint rectangle, wall openings per edge tag (doorway/open = centered gap
    /// with amber jambs, window = amber tick), and each captured object as a small cyan mark. Pure line-art from
    /// section DATA — no 3D render — so it's cheap and matches the drafting aesthetic. Live 3D thumbnails are a
    /// later, optional layer. Client-only (only ever called from the client UI).
    /// </summary>
    public static class SectionPlanRenderer
    {
        private static readonly Color Clear = new Color(0f, 0f, 0f, 0f);          // transparent (sits on the blueprint bg)
        private static readonly Color Wall  = new Color(0.918f, 0.953f, 1f, 1f);  // pale-cyan ink
        private static readonly Color Amber = new Color(1f, 0.824f, 0.541f, 1f);  // doorway jambs + window
        private static readonly Color Obj   = new Color(0.404f, 0.910f, 0.976f, 0.9f);

        private const float CellMetres = 8f;

        public static Texture2D Render(SectionBlueprint bp, int size = 256)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var px = new Color[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = Clear;

            if (bp != null)
            {
                float fx = Mathf.Max(1, bp.Footprint.x) * CellMetres;
                float fz = Mathf.Max(1, bp.Footprint.y) * CellMetres;
                float span = Mathf.Max(fx, fz);
                int pad = Mathf.RoundToInt(size * 0.15f);
                float scale = (size - 2 * pad) / span;               // px per metre
                float cx = size * 0.5f, cy = size * 0.5f;
                float hxp = fx * 0.5f * scale, hzp = fz * 0.5f * scale;
                int L = Mathf.RoundToInt(cx - hxp), R = Mathf.RoundToInt(cx + hxp);
                int B = Mathf.RoundToInt(cy - hzp), T = Mathf.RoundToInt(cy + hzp);

                // Edges: north=+Z(top), south=-Z(bottom), east=+X(right), west=-X(left).
                EdgeH(px, size, L, R, T, EdgeTag(bp, "north"));
                EdgeH(px, size, L, R, B, EdgeTag(bp, "south"));
                EdgeV(px, size, B, T, R, EdgeTag(bp, "east"));
                EdgeV(px, size, B, T, L, EdgeTag(bp, "west"));

                if (bp.Objects != null && bp.Objects.Count > 0)
                {
                    // Fit the object markers INSIDE the plan box regardless of raw spread: centre on the object bounds
                    // and scale uniformly to the box, so a big/uncentred blueprint doesn't spill dots outside the frame.
                    float onX = 1e9f, oxX = -1e9f, onZ = 1e9f, oxZ = -1e9f;
                    foreach (var o in bp.Objects) { if (o == null) continue; onX = Mathf.Min(onX, o.Pos.x); oxX = Mathf.Max(oxX, o.Pos.x); onZ = Mathf.Min(onZ, o.Pos.z); oxZ = Mathf.Max(oxZ, o.Pos.z); }
                    float ocx = (onX + oxX) * 0.5f, ocz = (onZ + oxZ) * 0.5f;
                    float ospan = Mathf.Max(Mathf.Max(oxX - onX, oxZ - onZ), 0.5f);
                    float fit = Mathf.Min(R - L, T - B) / ospan;
                    foreach (var o in bp.Objects)
                    {
                        if (o == null) continue;
                        int ox = Mathf.RoundToInt(cx + (o.Pos.x - ocx) * fit);
                        int oy = Mathf.RoundToInt(cy + (o.Pos.z - ocz) * fit);
                        Disc(px, size, ox, oy, 2, Obj);
                    }
                }
            }

            tex.SetPixels(px);
            tex.Apply(false, false);
            return tex;
        }

        private static string EdgeTag(SectionBlueprint bp, string face)
        {
            if (bp.EdgeTags != null && bp.EdgeTags.TryGetValue(face, out var list) && list != null && list.Count > 0)
                return list[0].ToLowerInvariant();
            return "wall";
        }

        // Horizontal wall row at y from x=L..R. doorway/open => centered gap with amber jambs; window => amber tick.
        private static void EdgeH(Color[] px, int size, int L, int R, int y, string tag)
        {
            int mid = (L + R) / 2, half = Mathf.Max(3, Mathf.RoundToInt((R - L) * 0.18f));
            bool opening = tag == "doorway" || tag == "open";
            bool window = tag == "window";
            for (int x = L; x <= R; x++)
            {
                bool inMid = x > mid - half && x < mid + half;
                if (opening && inMid) { if (x == mid - half + 1 || x == mid + half - 1) VBar(px, size, x, y, 3, Amber); continue; }
                Color c = (window && inMid) ? Amber : Wall;
                VBar(px, size, x, y, 3, c);
            }
        }

        private static void EdgeV(Color[] px, int size, int B, int T, int x, string tag)
        {
            int mid = (B + T) / 2, half = Mathf.Max(3, Mathf.RoundToInt((T - B) * 0.18f));
            bool opening = tag == "doorway" || tag == "open";
            bool window = tag == "window";
            for (int y = B; y <= T; y++)
            {
                bool inMid = y > mid - half && y < mid + half;
                if (opening && inMid) { if (y == mid - half + 1 || y == mid + half - 1) HBar(px, size, x, y, 3, Amber); continue; }
                Color c = (window && inMid) ? Amber : Wall;
                HBar(px, size, x, y, 3, c);
            }
        }

        private static void VBar(Color[] px, int size, int x, int y, int thick, Color c)
        { for (int t = -thick / 2; t <= thick / 2; t++) Set(px, size, x, y + t, c); }

        private static void HBar(Color[] px, int size, int x, int y, int thick, Color c)
        { for (int t = -thick / 2; t <= thick / 2; t++) Set(px, size, x + t, y, c); }

        private static void Disc(Color[] px, int size, int cx, int cy, int r, Color c)
        {
            for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                    if (x * x + y * y <= r * r) Set(px, size, cx + x, cy + y, c);
        }

        private static void Set(Color[] px, int size, int x, int y, Color c)
        {
            if (x < 0 || y < 0 || x >= size || y >= size) return;
            px[y * size + x] = c;
        }
    }
}
