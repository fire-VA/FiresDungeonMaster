using System;
using UnityEngine;

namespace FiresDungeonMaster.UI
{
    /// <summary>
    /// Renders a static 3D thumbnail of a blueprint's pieces — the "working preview picture" the bench shows for a
    /// selected blueprint. Mirrors kg_Blueprint's ModelPreview: a dedicated off-screen camera + point light frame the
    /// instantiated pieces at a far, empty spawn point and capture them to a Sprite. Pieces are built inside
    /// ZNetView.StartGhostInit so NO ZDOs / network objects are ever created (the same ghost trick the placement
    /// preview uses). The point light's short range lights only the model, never the live world. Client-only.
    /// </summary>
    internal static class BlueprintPreviewRenderer
    {
        private static readonly Vector3 Spawn = new Vector3(25000f, 25000f, 25000f);
        private const int Res = 512;

        private static Camera _cam;
        private static Light _light;
        private static RenderTexture _rt;
        private static Sprite _lastSprite;   // destroyed when replaced so repeated selects don't leak textures

        private static bool IsClientOnly() => SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

        private static void EnsureRig()
        {
            if (_cam != null) return;

            var camGo = new GameObject("FDM_BlueprintPreviewCam");
            UnityEngine.Object.DontDestroyOnLoad(camGo);
            _cam = camGo.AddComponent<Camera>();
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.055f, 0.065f, 0.085f, 1f);
            _cam.fieldOfView = 22f;
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 100000f;
            _cam.enabled = false;   // driven manually via Render()
            _rt = new RenderTexture(Res, Res, 24);
            _cam.targetTexture = _rt;

            var lightGo = new GameObject("FDM_BlueprintPreviewLight");
            UnityEngine.Object.DontDestroyOnLoad(lightGo);
            _light = lightGo.AddComponent<Light>();
            _light.type = LightType.Point;   // short-range: lights only the far-away model, not the world
            _light.intensity = 2.2f;
            _light.enabled = false;
        }

        /// <summary>Render <paramref name="bp"/> to a Sprite (null on failure / headless / empty). Caller assigns it.</summary>
        public static Sprite RenderSprite(FiresDungeonMaster.Sections.SectionBlueprint bp)
        {
            var tex = Capture(bp);
            if (tex == null) return null;
            if (_lastSprite != null)
            {
                if (_lastSprite.texture != null) UnityEngine.Object.Destroy(_lastSprite.texture);
                UnityEngine.Object.Destroy(_lastSprite);
            }
            _lastSprite = Sprite.Create(tex, new Rect(0, 0, Res, Res), new Vector2(0.5f, 0.5f), 100f);
            return _lastSprite;
        }

        /// <summary>0.1.189: a small copy for the bench list, as PNG bytes for its disk cache (null on failure).</summary>
        public static byte[] RenderThumbPng(FiresDungeonMaster.Sections.SectionBlueprint bp, int size)
        {
            var big = Capture(bp);
            if (big == null) return null;
            var small = RenderTexture.GetTemporary(size, size, 0);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(big, small);
                RenderTexture.active = small;
                var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                tex.Apply();
                byte[] png = ImageCodec.Encode(tex);
                UnityEngine.Object.Destroy(tex);
                return png;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(small);
                UnityEngine.Object.Destroy(big);
            }
        }

        private static Texture2D Capture(FiresDungeonMaster.Sections.SectionBlueprint bp)
        {

            if (IsClientOnly() || bp == null || bp.Objects == null || bp.Objects.Count == 0 || ZNetScene.instance == null) return null;
            EnsureRig();

            GameObject root = null;
            try
            {
                root = new GameObject("FDM_BlueprintPreviewModel");
                root.transform.position = Spawn;

                int built = 0;
                ZNetView.StartGhostInit();
                try
                {
                    foreach (var obj in bp.Objects)
                    {
                        if (obj == null || FiresDungeonMaster.Sections.SectionCenterpoint.Is(obj)) continue;
                        var prefab = ZNetScene.instance.GetPrefab(obj.Prefab);
                        if (prefab == null) continue;
                        var piece = UnityEngine.Object.Instantiate(prefab, root.transform);
                        piece.transform.localPosition = obj.Pos;
                        piece.transform.localRotation = obj.Rot;
                        if (obj.Scale != Vector3.zero && obj.Scale != Vector3.one) piece.transform.localScale = obj.Scale;
                        Strip(piece);
                        built++;
                    }
                }
                finally { ZNetView.FinishGhostInit(); }
                if (built == 0) return null;

                Bounds b = ComputeBounds(root);
                float radius = Mathf.Max(0.75f, b.extents.magnitude);
                Vector3 dir = new Vector3(0.65f, 0.5f, -0.8f).normalized;   // three-quarter view
                float dist = radius / Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.2f;
                _cam.transform.position = b.center + dir * dist;
                _cam.transform.rotation = Quaternion.LookRotation(b.center - _cam.transform.position, Vector3.up);
                _light.transform.position = b.center + new Vector3(radius, radius * 1.5f, -radius);
                _light.range = radius * 8f;

                _light.enabled = true;
                RenderTexture prevActive = RenderTexture.active;
                _cam.Render();
                RenderTexture.active = _rt;
                var tex = new Texture2D(Res, Res, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                tex.ReadPixels(new Rect(0, 0, Res, Res), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                _light.enabled = false;

                return tex;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"blueprint preview render failed: {ex.Message}");
                return null;
            }
            finally { if (root != null) UnityEngine.Object.Destroy(root); }
        }

        private static Bounds ComputeBounds(GameObject root)
        {
            var rends = root.GetComponentsInChildren<Renderer>();
            Bounds b = new Bounds(root.transform.position, Vector3.one);
            bool seeded = false;
            foreach (var r in rends)
            {
                // Skip ParticleSystemRenderer by name (its module assembly isn't referenced) — particle bounds are
                // huge/animated and would wreck the framing.
                if (r == null || r.GetType().Name == "ParticleSystemRenderer") continue;
                if (!seeded) { b = r.bounds; seeded = true; }
                else b.Encapsulate(r.bounds);
            }
            return b;
        }

        private static void Strip(GameObject piece)
        {
            foreach (var c in piece.GetComponentsInChildren<Collider>(true)) if (c != null) UnityEngine.Object.Destroy(c);
            foreach (var rb in piece.GetComponentsInChildren<Rigidbody>(true)) if (rb != null) UnityEngine.Object.Destroy(rb);
            foreach (var mb in piece.GetComponentsInChildren<MonoBehaviour>(true)) if (mb != null) mb.enabled = false;
        }
    }
}

namespace FiresDungeonMaster.UI
{
    /// <summary>0.1.189: Unity's PNG calls, bound by name: their module assembly needs a netstandard reference this project does not compile against.</summary>
    internal static class ImageCodec
    {
        private static readonly System.Type T = System.Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule", false);
        private static readonly System.Func<UnityEngine.Texture2D, byte[]> Enc = T == null ? null :
            (System.Func<UnityEngine.Texture2D, byte[]>)System.Delegate.CreateDelegate(typeof(System.Func<UnityEngine.Texture2D, byte[]>), T.GetMethod("EncodeToPNG", new[] { typeof(UnityEngine.Texture2D) }));
        private static readonly System.Func<UnityEngine.Texture2D, byte[], bool> Dec = T == null ? null :
            (System.Func<UnityEngine.Texture2D, byte[], bool>)System.Delegate.CreateDelegate(typeof(System.Func<UnityEngine.Texture2D, byte[], bool>), T.GetMethod("LoadImage", new[] { typeof(UnityEngine.Texture2D), typeof(byte[]) }));

        internal static byte[] Encode(UnityEngine.Texture2D t) => Enc?.Invoke(t);
        internal static bool Load(UnityEngine.Texture2D t, byte[] png) => Dec != null && Dec(t, png);
    }
}
