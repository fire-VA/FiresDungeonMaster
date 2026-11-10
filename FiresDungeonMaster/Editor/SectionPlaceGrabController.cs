using HarmonyLib;
using UnityEngine;
using FiresCore.UI.ContextMenu;

namespace FiresDungeonMaster.Editor
{
    using Input = UnityEngine.Input;

    /// <summary>
    /// Hammer-style "grab" placement for an Edit-in-Box: after the bench arms a section/set, a translucent box preview
    /// rides the crosshair like a normal placement ghost. Camera + movement stay FREE (walk around to position it);
    /// PageUp/PageDown nudge height, R rotates 90°, LEFT-CLICK places a real footprint there (which fills itself from
    /// the armed edit via SectionFootprintController.Awake → ApplyPendingEditDeferred), RIGHT-CLICK / Esc cancels.
    /// The hammer's own build is suppressed while grabbing so the click doesn't drop a blank footprint too. Client-only.
    /// </summary>
    public sealed class SectionPlaceGrabController : MonoBehaviour
    {
        public static SectionPlaceGrabController Instance { get; private set; }

        private const float StepSmall = 0.5f, StepLarge = 2f;
        private static readonly int PlacementMask =
            LayerMask.GetMask("Default", "static_solid", "piece", "terrain", "vehicle", "Default_small");

        private GameObject _preview;
        private bool _isGhost;   // true when _preview is the real blueprint piece-ghost (raw Place), false = sizing cube
        private Vector3 _aim, _pos;
        private float _yaw, _height;
        private bool _armed;   // wait for the bench-click to release before a click can place

        public static void Begin()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return; // headless
            if (!FiresDungeonMaster.Sections.SectionEditSpawner.HasPending) return;
            if (Player.m_localPlayer == null) return;
            if (Instance != null) Instance.End(false);

            var go = new GameObject("FiresSectionPlaceGrab");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<SectionPlaceGrabController>();
            FiresContextMenu.SuppressDriver = true;   // don't open the Alt+Shift menu on the placing/cancel clicks
            EquipHammer();                            // hold the Hammer so a placing left-click doesn't punch (fists)
            Instance.BuildPreview();
            Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                "[FiresDungeonMaster] aim + left-click to place · PageUp/PageDown height · R rotate · right-click / Esc cancel");
        }

        // Equip the Hammer from the player's inventory so a left-click while grabbing swings the build tool (which our
        // UpdatePlacement suppressor eats) instead of throwing a fist punch. No-op if the player has no hammer.
        private static void EquipHammer()
        {
            var player = Player.m_localPlayer;
            var inv = player != null ? player.GetInventory() : null;
            var hammer = inv != null ? inv.GetItem("$item_hammer") : null;
            if (hammer != null && player.GetRightItem() != hammer)
                player.EquipItem(hammer);
        }

        private void BuildPreview()
        {
            // Raw Place (bench "Place" on a blueprint): show the ACTUAL blueprint pieces as a ghost that rides the
            // crosshair — the same preview Infinity Hammer / PlanBuild / FAP show — instead of a featureless box.
            var raw = FiresDungeonMaster.Sections.SectionEditSpawner.RawPending;
            if (raw != null && BuildBlueprintGhost(raw)) { _isGhost = true; return; }

            // Edit-in-Box (and the fallback): a translucent sizing cube is correct here — you're placing an EDITABLE
            // box, not a fixed blueprint.
            _isGhost = false;
            Vector3 size = FiresDungeonMaster.Sections.SectionEditSpawner.PendingBoxSize();
            _preview = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _preview.name = "FiresSectionPreview";
            var col = _preview.GetComponent<Collider>(); if (col != null) Destroy(col);
            var mr = _preview.GetComponent<MeshRenderer>();
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default") ?? Shader.Find("Unlit/Transparent");
            var mat = new Material(sh) { color = new Color(0.28f, 0.78f, 1f, 0.28f) };
            mat.renderQueue = 3000;
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _preview.transform.localScale = size;
        }

        /// <summary>
        /// Build a live preview of the blueprint's real pieces (the "hammer preview OF the blueprint"). Instantiates
        /// each object's ZNetScene prefab under one root at the blueprint's own relative pos/rot, wrapped in
        /// ZNetView.StartGhostInit/FinishGhostInit so NO ZDOs/network objects are created (the vanilla + Infinity Hammer
        /// ghost trick), then strips colliders + disables all component logic so it's purely visual. Returns false if
        /// nothing resolved (caller falls back to the cube). Pieces are floor-anchored (SizeAndCentre floored Y to min),
        /// so the root sits at the aim point directly.
        /// </summary>
        private bool BuildBlueprintGhost(FiresDungeonMaster.Sections.SectionBlueprint bp)
        {
            if (bp.Objects == null || bp.Objects.Count == 0 || ZNetScene.instance == null) return false;

            var root = new GameObject("FiresBlueprintGhost");
            int built = 0;
            ZNetView.StartGhostInit();
            try
            {
                foreach (var obj in bp.Objects)
                {
                    if (obj == null || FiresDungeonMaster.Sections.SectionCenterpoint.Is(obj)) continue;
                    var prefab = ZNetScene.instance.GetPrefab(obj.Prefab);
                    if (prefab == null) continue;
                    var piece = Instantiate(prefab, root.transform);
                    piece.transform.localPosition = obj.Pos;
                    piece.transform.localRotation = obj.Rot;
                    if (obj.Scale != Vector3.zero && obj.Scale != Vector3.one) piece.transform.localScale = obj.Scale;
                    StripToGhost(piece);
                    built++;
                }
            }
            catch (System.Exception ex) { Debug.LogWarning($"[FiresDungeonMaster] blueprint ghost build threw: {ex.Message}"); }
            finally { ZNetView.FinishGhostInit(); }

            if (built == 0) { Destroy(root); return false; }
            _preview = root;
            return true;
        }

        // Turn an instantiated prefab into a pure visual: no physics, no gameplay logic (fire/containers/wear-n-tear),
        // no shadows. Renderers are left with their real materials so the preview shows the true blueprint.
        private static void StripToGhost(GameObject piece)
        {
            foreach (var c in piece.GetComponentsInChildren<Collider>(true)) if (c != null) Destroy(c);
            foreach (var rb in piece.GetComponentsInChildren<Rigidbody>(true)) if (rb != null) Destroy(rb);
            foreach (var mb in piece.GetComponentsInChildren<MonoBehaviour>(true)) if (mb != null) mb.enabled = false;
            foreach (var r in piece.GetComponentsInChildren<Renderer>(true)) if (r != null) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private void Update()
        {
            if (!FiresDungeonMaster.Sections.SectionEditSpawner.HasPending || Player.m_localPlayer == null) { End(false); return; }

            // Aim from the crosshair (camera-forward ray) like the hammer's place-ray — camera stays free.
            var cam = Camera.main;
            if (cam != null && Physics.Raycast(cam.transform.position, cam.transform.forward, out var hit, 3000f, PlacementMask, QueryTriggerInteraction.Ignore))
                _aim = hit.point;

            float step = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? StepLarge : StepSmall;
            if (Input.GetKeyDown(KeyCode.PageUp)) _height += step;
            if (Input.GetKeyDown(KeyCode.PageDown)) _height -= step;
            if (Input.GetKeyDown(KeyCode.R)) _yaw = (_yaw + 90f) % 360f;
            if (Input.GetKeyDown(KeyCode.Keypad0) || Input.GetKeyDown(KeyCode.Home)) { _height = 0f; _yaw = 0f; }

            Vector3 up = EntranceLink.UpAt(_aim);   // 0.1.177: a halo ring's up (world up elsewhere)
            _pos = _aim + up * _height;
            if (_preview != null)
            {
                // Piece-ghost is floor-anchored (sits ON the aim); the sizing cube is centre-anchored (+half height).
                Vector3 previewPos = _isGhost ? _pos : _pos + up * (_preview.transform.localScale.y * 0.5f);
                _preview.transform.SetPositionAndRotation(previewPos, Quaternion.FromToRotation(Vector3.up, up) * Quaternion.Euler(0f, _yaw, 0f));
            }

            if (!Input.GetMouseButton(0)) _armed = true;
            if (_armed && Input.GetMouseButtonDown(0)) { Place(); return; }
            if (Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.Escape)) { End(true); return; }
        }

        private void Place()
        {
            var rot = Quaternion.FromToRotation(Vector3.up, EntranceLink.UpAt(_aim)) * Quaternion.Euler(0f, _yaw, 0f);   // 0.1.177: stands on a halo ring's floor
            // Raw Place (bench "Place" on a PlanBuild blueprint): drop the blueprint directly at the aim — no editable box.
            if (FiresDungeonMaster.Sections.SectionEditSpawner.RawPending != null)
            {
                FiresDungeonMaster.Sections.SectionEditSpawner.PlaceRawAt(_pos, rot);
                End(false);
                return;
            }
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(FootprintRegistration.FootprintPrefabName) : null;
            if (prefab != null)
                UnityEngine.Object.Instantiate(prefab, _pos, rot);   // fills from the armed edit in Awake
            End(false);   // don't cancel the pending — the placed footprint consumes it a couple frames later
        }

        private void End(bool cancel)
        {
            if (cancel) FiresDungeonMaster.Sections.SectionEditSpawner.CancelPending();
            if (_preview != null) Destroy(_preview);
            _preview = null;
            FiresContextMenu.SuppressDriver = false;
            if (Instance == this) Instance = null;
            Destroy(gameObject);
        }

        // While grabbing, suppress the hammer's own placement so a left-click drops OUR box, not a blank footprint.
        [HarmonyPatch(typeof(Player), "UpdatePlacement", new[] { typeof(bool), typeof(float) })]
        private static class Player_UpdatePlacement_GrabBlock
        {
            private static bool Prefix() => Instance == null;
        }
    }
}
