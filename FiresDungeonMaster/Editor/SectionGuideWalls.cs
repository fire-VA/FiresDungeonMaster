using System.Collections.Generic;
using UnityEngine;

namespace FiresDungeonMaster.Editor
{
    using EdgeTag = SectionFootprintController.EdgeTag;

    /// <summary>
    /// SectionGuideWalls — materializes the translucent-blue WYSIWYG guide panels on a footprint's four faces, so a
    /// face tagged Wall/Doorway/Window/Back shows the actual wall/door/window mesh (with collision) instead of just
    /// a colored orb. Ports the approved render mockup geometry (FiresDungeonGuideRender): a solid wall is one box;
    /// a doorway/window is a real opening built from left/right/below/above segments. Built in FOOTPRINT-LOCAL space
    /// — floor top at Y=0, walls rise to Y=BoxSize.y (the bake normalizes the frame bottom to Y=0, X/Z centered) —
    /// so panels track the box on resize when the controller rebuilds. Client-only (the controller guards).
    ///
    /// Collision: every segment carries a SOLID (non-trigger) BoxCollider on the 'static_solid' layer, exactly like
    /// the baked Floor slab, so build pieces gain support/snap against a guide wall and the context driver's ray
    /// (RayMask includes static_solid) resolves a right-click on a wall back to the footprint -> that face's editor.
    /// </summary>
    internal static class SectionGuideWalls
    {
        private const float WallThickness = 0.5f;   // guide panel depth (matches the render mockup)
        private const float DoorW = 1.6f;           // doorway opening width  (human-door sized, absolute metres)
        private const float DoorH = 2.6f;           // doorway opening height (floor-anchored)
        private const float WinW  = 1.6f;           // window opening width
        private const float WinH  = 1.6f;           // window opening height (square)
        private const float WinSill = 1.0f;         // window raised this far off the floor

        // Translucent guide palette (mirrors the render mockup + the per-edge tag color language). Alpha is the
        // hologram translucency; Sprites/Default is unlit so the colour IS the look (no emission needed/used).
        private static readonly Color SolidColor = new Color(0.16f, 0.80f, 0.92f, 0.42f);  // cyan  = solid wall / back
        private static readonly Color DoorColor  = new Color(0.30f, 0.90f, 0.55f, 0.42f);  // green = doorway
        private static readonly Color WinColor   = new Color(0.95f, 0.82f, 0.35f, 0.42f);  // amber = window

        private static Material _solidMat, _doorMat, _winMat;

        // Structured guide diagnostics (shader resolution, mesh verts, material alpha/queue, renderer state, world
        // placement + bounds). Gated on the mod's "Verbose Logging" config (default off).
        internal static bool Verbose => FiresLogger.VerboseEnabled;
        internal static void Log(string msg) { if (Verbose) UnityEngine.Debug.Log("[GUIDE-DBG] " + msg); }

        /// <summary>A face shows a guide panel unless it is fully Open (an Open face keeps its edge orb instead).</summary>
        internal static bool FaceHasPanel(EdgeTag tag) => tag != EdgeTag.Open;

        /// <summary>Build one face's guide panel under <paramref name="parent"/> (footprint-local). Returns the segment
        /// count (0 = Open / nothing built). No-op for Open.</summary>
        internal static int BuildFace(Transform parent, int face, Vector3 box, EdgeTag tag)
        {
            if (!FaceHasPanel(tag)) return 0;

            float hx = box.x * 0.5f, hz = box.z * 0.5f, H = box.y, T = WallThickness;
            float width; Vector3 pos; float yaw;
            switch (face)
            {
                case 0: width = box.x; pos = new Vector3(0f, H * 0.5f, hz - T * 0.5f); yaw = 0f; break;   // N +Z
                case 1: width = box.z; pos = new Vector3(hx - T * 0.5f, H * 0.5f, 0f); yaw = 90f; break;  // E +X
                case 2: width = box.x; pos = new Vector3(0f, H * 0.5f, -(hz - T * 0.5f)); yaw = 0f; break; // S -Z
                default: width = box.z; pos = new Vector3(-(hx - T * 0.5f), H * 0.5f, 0f); yaw = 90f; break; // W -X
            }

            var wall = new GameObject("Guide_" + FaceName(face) + "_" + tag);
            wall.transform.SetParent(parent, false);
            wall.transform.localPosition = pos;
            wall.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

            // Wall-local frame: width along X, height H along Y, thickness T along Z, centered at origin (bottom = -H/2).
            switch (tag)
            {
                case EdgeTag.Doorway:
                {
                    float floor = SectionFootprintController.FloorOffset;
                    float dw = Mathf.Min(DoorW, width - 0.4f);
                    // Opening spans the 1m floor riser PLUS a full DoorH of clearance above the snapped floor, so you
                    // can still walk through DoorH of doorway once a floor snaps in ~1m up (floor-anchored from Y=0).
                    float dh = Mathf.Min(DoorH + floor, H - 0.2f);
                    if (dw <= 0.05f || dh <= 0.05f) { AddSeg(wall.transform, "solid", width, H, T, Vector3.zero, DoorMat()); break; }
                    AddHoleWall(wall.transform, width, H, T, dw, dh, dh * 0.5f, DoorMat());
                    break;
                }
                case EdgeTag.Window:
                {
                    float floor = SectionFootprintController.FloorOffset;
                    float ww = Mathf.Min(WinW, width - 0.4f);
                    float wh = Mathf.Min(WinH, H - 1.4f - floor);
                    float sill = Mathf.Clamp(WinSill + floor, 0.2f, H - wh - 0.2f);   // sill measured above the snapped floor
                    if (ww <= 0.05f || wh <= 0.05f) { AddSeg(wall.transform, "solid", width, H, T, Vector3.zero, WinMat()); break; }
                    AddHoleWall(wall.transform, width, H, T, ww, wh, sill + wh * 0.5f, WinMat());
                    break;
                }
                default: // Wall / Back / Left / Right — a solid panel
                    AddSeg(wall.transform, "solid", width, H, T, Vector3.zero, SolidMat());
                    break;
            }

            int segs = wall.transform.childCount;
            Log($"face={FaceName(face)} tag={tag} width={width:F2} H={H:F2} T={T} segs={segs} " +
                $"wallLocal={pos} wallWorld={wall.transform.position}");
            return segs;
        }

        // Post-build verification: log the deterministic signals that tell us whether the walls are set up correctly,
        // independent of what the user reports seeing. If renderers>0 with a real shader + verts + sane world bounds
        // near the footprint but the user still sees nothing, the problem is runtime transparency/sorting (a different
        // fix) rather than a broken build. Renderer.isVisible is only meaningful after a camera renders, so it may read
        // 0 on the build frame — it's logged for context, not as a pass/fail.
        internal static void LogVerify(Transform root, int panels)
        {
            if (!Verbose) return;
            if (root == null) { Log("verify: guideRoot NULL"); return; }
            var rends = root.GetComponentsInChildren<MeshRenderer>(true);
            if (rends.Length == 0) { Log($"verify: panels={panels} but NO MeshRenderers under guideRoot — nothing to draw"); return; }

            Bounds b = rends[0].bounds;
            int enabled = 0, visible = 0, verts = 0, nullMat = 0, nullShader = 0, cols = 0;
            string shader = "?", matName = "?"; Color col = default; int rq = -1; float alpha = -1f;
            foreach (var r in rends)
            {
                b.Encapsulate(r.bounds);
                if (r.enabled) enabled++;
                if (r.isVisible) visible++;
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) verts += mf.sharedMesh.vertexCount;
                if (r.GetComponent<BoxCollider>() != null) cols++;
                var m = r.sharedMaterial;
                if (m == null) { nullMat++; continue; }
                matName = m.name;
                if (m.shader == null) { nullShader++; shader = "NULL"; }
                else shader = m.shader.name;
                if (m.HasProperty("_Color")) { col = m.GetColor("_Color"); alpha = col.a; }
                rq = m.renderQueue;
            }
            Log($"verify: panels={panels} renderers={rends.Length} enabled={enabled} visibleNow={visible} colliders={cols} " +
                $"verts={verts} nullMat={nullMat} nullShader={nullShader} shader='{shader}' mat='{matName}' color={col} alpha={alpha:F2} renderQueue={rq}");
            Log($"verify: worldBounds center={b.center} size={b.size}  (compare center to the footprint position above)");
        }

        /// <summary>Destroy every guide GameObject + its generated meshes under <paramref name="root"/> (no leaks).</summary>
        internal static void Clear(Transform root)
        {
            if (root == null) return;
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                var child = root.GetChild(i).gameObject;
                foreach (var mf in child.GetComponentsInChildren<MeshFilter>(true))
                    if (mf.sharedMesh != null) Object.Destroy(mf.sharedMesh);
                Object.Destroy(child);
            }
        }

        // A wall with a rectangular opening, built as up to four box segments (left, right, below, above the hole).
        // holeCenterY is measured from the panel BOTTOM (local -H/2). Mirrors FiresDungeonGuideRender.MakeWallWithHole.
        private static void AddHoleWall(Transform wall, float width, float H, float T, float holeW, float holeH, float holeCenterY, Material mat)
        {
            float hw = width * 0.5f;
            float bottom = -H * 0.5f;
            float holeBottom = bottom + holeCenterY - holeH * 0.5f;
            float holeTop = bottom + holeCenterY + holeH * 0.5f;
            float holeLeft = -holeW * 0.5f, holeRight = holeW * 0.5f;
            float top = H * 0.5f;

            AddSeg(wall, "L", holeLeft - (-hw), H, T, new Vector3((-hw + holeLeft) * 0.5f, 0f, 0f), mat);   // left jamb
            AddSeg(wall, "R", hw - holeRight, H, T, new Vector3((holeRight + hw) * 0.5f, 0f, 0f), mat);     // right jamb
            if (holeBottom > bottom + 0.01f)                                                                 // sill (window)
                AddSeg(wall, "B", holeW, holeBottom - bottom, T, new Vector3(0f, (bottom + holeBottom) * 0.5f, 0f), mat);
            if (top > holeTop + 0.01f)                                                                        // lintel
                AddSeg(wall, "A", holeW, top - holeTop, T, new Vector3(0f, (holeTop + top) * 0.5f, 0f), mat);
        }

        // One box segment: fresh generated mesh (a runtime hologram), translucent material, shadows off. NO collider
        // and NO build-mask layer — the guide walls must NEVER intercept the hammer's place-ray (both piece_nonsolid
        // AND static_solid are in Player.m_placeRayMask), because these boxes are platforms to build WITHIN. A
        // right-click on a wall is resolved by the controller's ray-vs-wall marker test, not a physics collider.
        private static void AddSeg(Transform parent, string name, float sx, float sy, float sz, Vector3 localPos, Material mat)
        {
            if (sx <= 0.01f || sy <= 0.01f) return;
            var seg = new GameObject(name);
            seg.transform.SetParent(parent, false);
            seg.transform.localPosition = localPos;

            seg.AddComponent<MeshFilter>().sharedMesh = BoxMesh(sx, sy, sz);
            var mr = seg.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        private static string FaceName(int face)
        {
            switch (face) { case 0: return "N"; case 1: return "E"; case 2: return "S"; default: return "W"; }
        }

        // ── materials (cached; created once, reused across every footprint + rebuild) ──
        private static Material SolidMat() => _solidMat != null ? _solidMat : (_solidMat = MakeTransparent(SolidColor));
        private static Material DoorMat()  => _doorMat  != null ? _doorMat  : (_doorMat  = MakeTransparent(DoorColor));
        private static Material WinMat()   => _winMat   != null ? _winMat   : (_winMat   = MakeTransparent(WinColor));

        // Valheim's runtime does NOT ship the Standard shader (Shader.Find("Standard")==null — the [GUIDE-DBG] log
        // proved it, which is why v0.1.16/17 threw ArgumentNullException on new Material(null) and NO wall rendered).
        // Use the SAME proven recipe the env box (a translucent box that DOES render in-game) uses: Sprites/Default
        // is unlit, alpha-blended, respects mat.color's alpha + is present in Valheim (patrol routes + env box render
        // with it). renderQueue 3000 = the transparent queue. Logs which shader resolved so any gap is obvious.
        private static Material MakeTransparent(Color color)
        {
            var shader = Shader.Find("Sprites/Default")
                      ?? Shader.Find("UI/Default")
                      ?? Shader.Find("Legacy Shaders/Transparent/Diffuse")
                      ?? Shader.Find("Unlit/Transparent");
            if (shader == null)
            {
                UnityEngine.Debug.LogWarning("[GUIDE-DBG] no transparent shader available (Sprites/Default / UI/Default / Legacy Transparent / Unlit/Transparent all null) — guide walls cannot render");
                return null;
            }
            var m = new Material(shader) { color = color, renderQueue = 3000 };
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            Log($"material shader='{shader.name}' color={color} alpha={color.a:F2} rq={m.renderQueue}");
            return m;
        }

        // Axis-aligned box mesh (sx x sy x sz), centered at origin. 24 verts / 6 quads. Generated at runtime — never
        // CreatePrimitive (whose built-in mesh does not survive; a plain generated mesh renders fine at runtime).
        private static Mesh BoxMesh(float sx, float sy, float sz)
        {
            float hx = sx * 0.5f, hy = sy * 0.5f, hz = sz * 0.5f;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var tris = new List<int>();

            void Face(Vector3 o, Vector3 u, Vector3 v, Vector3 n)
            {
                int b = verts.Count;
                verts.Add(o); verts.Add(o + u); verts.Add(o + u + v); verts.Add(o + v);
                for (int i = 0; i < 4; i++) norms.Add(n);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
            }

            Face(new Vector3(-hx,  hy, -hz), new Vector3(sx, 0, 0), new Vector3(0, 0, sz), Vector3.up);
            Face(new Vector3(-hx, -hy,  hz), new Vector3(sx, 0, 0), new Vector3(0, 0, -sz), Vector3.down);
            Face(new Vector3(-hx, -hy, -hz), new Vector3(0, sy, 0), new Vector3(sx, 0, 0), Vector3.back);
            Face(new Vector3( hx, -hy,  hz), new Vector3(0, sy, 0), new Vector3(-sx, 0, 0), Vector3.forward);
            Face(new Vector3(-hx, -hy,  hz), new Vector3(0, sy, 0), new Vector3(0, 0, -sz), Vector3.left);
            Face(new Vector3( hx, -hy, -hz), new Vector3(0, sy, 0), new Vector3(0, 0, sz), Vector3.right);

            var mesh = new Mesh { name = "GuideBox" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
