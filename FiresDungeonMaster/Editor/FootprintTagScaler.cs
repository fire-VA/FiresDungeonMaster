using UnityEngine;
using UnityEngine.Rendering;

namespace FiresDungeonMaster.Editor
{
    /// <summary>
    /// FootprintTagScaler — the client-only hover + modifier + scroll driver for P3 edge/role/type tagging.
    /// A verbatim structural clone of FiresCore.Utilities.EnvironmentBoxScaler: a DontDestroyOnLoad singleton
    /// (EnsureExists() from SectionFootprintController.Awake), the same Update() menu/console/textinput gates,
    /// the same GameCamera raycast (50m, DefaultRaycastLayers, QueryTriggerInteraction.Collide), the same admin
    /// gate, the same "shift held" requirement, and the same 0.1s scroll cooldown.
    ///
    /// Difference from the env-box scaler: instead of scaling, it resolves WHICH tag marker (edge quad N/E/S/W or
    /// the center sphere) the ray hit on the hovered footprint and cycles that:
    ///   - Shift+Scroll on an edge quad     -> cycles that face's EdgeTag.
    ///   - Shift+Scroll on the center sphere -> cycles the module Role (SectionRole).
    ///   - Alt+Scroll   on the center sphere -> cycles the dungeon-type name (distinct existing names).
    ///
    /// Dedi-safe: this MonoBehaviour is NEVER created on a headless server — EnsureExists() early-returns when
    /// IsClientOnly(), and SectionFootprintController only builds its markers on a graphics client anyway. No
    /// Harmony patch is introduced, so the plugin's DedicatedServerSkipPatchTypes is unchanged.
    /// </summary>
    public class FootprintTagScaler : MonoBehaviour
    {
        private static FootprintTagScaler _instance;
        public static FootprintTagScaler Instance => _instance;

        private SectionFootprintController _hovered;
        private SectionFootprintController.MarkerHit _hoveredMarker = SectionFootprintController.MarkerHit.None;
        private float _scrollCooldown = 0f;

        private static bool IsClientOnly()
            => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        /// <summary>Create the singleton (no-op on a headless server, where there are no markers to hit).</summary>
        public static void EnsureExists()
        {
            if (IsClientOnly()) return;
            if (_instance != null) return;
            var go = new GameObject("FootprintTagScaler");
            go.AddComponent<FootprintTagScaler>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void Update()
        {
            if (_scrollCooldown > 0) _scrollCooldown -= Time.deltaTime;

            // Same gates the env-box scaler uses. Crucially this also blocks while a Fires/TextInput field is open,
            // so typing a dungeon-type name in DungeonTypeInput doesn't ALSO cycle tags via raw-Input modifier reads.
            if (Player.m_localPlayer == null) return;
            if (InventoryGui.IsVisible()) return;
            if (Menu.IsVisible()) return;
            if (Console.IsVisible()) return;
            if (TextInput.IsVisible()) return;
            if (Chat.instance != null && Chat.instance.HasFocus()) return;

            UpdateHovered();
            ProcessScrollInput();
        }

        private void UpdateHovered()
        {
            _hovered = null;
            _hoveredMarker = SectionFootprintController.MarkerHit.None;

            var cam = GameCamera.instance?.transform;
            if (cam == null) return;

            var ray = new Ray(cam.position, cam.forward);
            if (Physics.Raycast(ray, out RaycastHit hit, 50f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
            {
                var ctrl = hit.collider.GetComponentInParent<SectionFootprintController>();
                if (ctrl == null) return;
                _hovered = ctrl;
                _hoveredMarker = ctrl.ResolveMarker(hit.collider);
            }
        }

        private void ProcessScrollInput()
        {
            if (_hovered == null) return;
            if (_scrollCooldown > 0) return;
            if (!IsLocalPlayerAdmin()) return;

            float scroll = UnityEngine.Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) < 0.01f) return;

            bool shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            bool alt = UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt);
            if (!shift && !alt) return;

            int dir = scroll > 0 ? 1 : -1;

            switch (_hoveredMarker)
            {
                case SectionFootprintController.MarkerHit.North:
                case SectionFootprintController.MarkerHit.East:
                case SectionFootprintController.MarkerHit.South:
                case SectionFootprintController.MarkerHit.West:
                    if (shift) _hovered.CycleEdgeTag(_hoveredMarker, dir);
                    break;

                case SectionFootprintController.MarkerHit.Center:
                    if (alt) _hovered.CycleDungeonType(dir);
                    else if (shift) _hovered.CycleRole(dir);
                    break;

                default:
                    return; // hovering the footprint body but not a marker — ignore
            }

            _scrollCooldown = 0.1f;
        }

        private static bool IsLocalPlayerAdmin() => SectionFootprintController.IsAdmin();
    }
}
