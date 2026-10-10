using UnityEngine;

namespace FiresDungeonMaster.UI
{
    /// <summary>
    /// 0.1.128 (Fire: resize corners showed a plain square). A drawn resize grip: three diagonal lines in the panel's gold,
    /// on a transparent 32x32 texture, so the corner reads as "drag to resize" without depending on a font glyph or a
    /// vanilla sprite that may not exist.
    /// </summary>
    internal static class GripSprite
    {
        static Sprite _sprite;

        internal static Sprite Get(Color ink)
        {
            if (_sprite != null) return _sprite;
            const int N = 32;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "VAResizeGrip" };
            var clear = new Color(0f, 0f, 0f, 0f);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    // Lines x + y = c (pixel rows from the bottom-right), three of them, 3 px thick, in the lower-right triangle.
                    int d = (N - 1 - x) + y;
                    bool line = (d >= 4 && d <= 6) || (d >= 12 && d <= 14) || (d >= 20 && d <= 22);
                    tex.SetPixel(x, y, line ? ink : clear);
                }
            tex.Apply();
            _sprite = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
            _sprite.name = "VAResizeGrip";
            return _sprite;
        }
    }
}

namespace FiresDungeonMaster.UI
{
    internal static class GripFix
    {
        static readonly string[] Generic = { "UISprite", "Background", "InputFieldBackground", "Knob", "UIMask" };

        /// <summary>A resize handle with no sprite, or Unity's plain built-in square, gets the drawn grip. Logs what it resolved.</summary>
        internal static void Apply(GameObject grip, string panel)
        {
            var img = grip != null ? grip.GetComponent<UnityEngine.UI.Image>() : null;
            if (img == null) return;
            string was = img.sprite != null ? img.sprite.name : "none";
            if (img.sprite == null || System.Array.IndexOf(Generic, img.sprite.name) >= 0)
            {
                img.sprite = GripSprite.Get(new Color(0.55f, 0.42f, 0.20f, 1f));
                img.color = Color.white;
                img.type = UnityEngine.UI.Image.Type.Simple;
            }
            FiresLogger.LogVerbose($"{panel}: resize handle sprite {was} -> {img.sprite.name}");
        }
    }
}
