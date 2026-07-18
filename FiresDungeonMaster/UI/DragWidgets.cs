using UnityEngine;
using UnityEngine.EventSystems;

namespace FiresDungeonMaster.UI
{
    /// <summary>
    /// Drag a window by its title bar — same behaviour as Core's ConfigPanel.DragMove, shared here so both the
    /// section editor and the dungeon-type modal are movable like every other Fires custom UI. Moves the assigned
    /// Target RectTransform by the pointer delta (divided by the canvas scale so 1px drag == 1px on screen).
    /// </summary>
    public sealed class DragMove : MonoBehaviour, IDragHandler
    {
        public RectTransform Target;

        public void OnDrag(PointerEventData e)
        {
            if (Target == null) return;
            Target.anchoredPosition += e.delta / CanvasScale(Target);
        }

        internal static float CanvasScale(Component c)
        {
            var canvas = c.GetComponentInParent<Canvas>();
            return canvas != null && canvas.scaleFactor > 0f ? canvas.scaleFactor : 1f;
        }
    }

    /// <summary>
    /// Resize a window by a bottom-right grip — mirrors Core's ConfigPanel.DragResize. Grows/shrinks the Target's
    /// sizeDelta (x by +dx, y by -dy because the panel pivot is top-left), clamped to MinSize.
    /// </summary>
    public sealed class DragResize : MonoBehaviour, IDragHandler
    {
        public RectTransform Target;
        public Vector2 MinSize = new Vector2(380f, 240f);

        public void OnDrag(PointerEventData e)
        {
            if (Target == null) return;
            Vector2 d = e.delta / DragMove.CanvasScale(Target);
            var s = Target.sizeDelta + new Vector2(d.x, -d.y);
            Target.sizeDelta = new Vector2(Mathf.Max(MinSize.x, s.x), Mathf.Max(MinSize.y, s.y));
        }
    }
}
