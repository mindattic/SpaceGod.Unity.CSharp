using System.Collections.Generic;
using Unity.SpaceGod.Core;
using UnityEngine;

namespace Unity.SpaceGod.Rendering
{
    /// <summary>
    /// Renders a small offscreen snapshot of each <see cref="ShipClass"/>'s hull -- the same model a
    /// real ship uses (<see cref="ShipModels.Build"/>), just a bare throwaway <see cref="Ship"/> never
    /// added to any <see cref="Fleet"/> -- for the spawn screen's picker grid (<see cref="UI.Hud"/>).
    /// Renders straight to a <see cref="RenderTexture"/> via a manual <see cref="Camera.Render"/> call
    /// rather than an enabled camera, so it never competes with or needs to disable the game's own
    /// camera (unlike <see cref="ModelViewer"/>, whose camera draws to the screen itself). Cached per
    /// class and livery: each one is built and rendered once, not every frame.
    /// </summary>
    public static class ShipThumbnails
    {
        const int Size = 192;
        static readonly int Layer = LayerMask.NameToLayer("ModelViewer");

        static Camera camera;
        // Keyed by livery too: the spawn screen shows the same class in the home or the hostile colours.
        static readonly Dictionary<(ShipClass, Faction), RenderTexture> cache = new Dictionary<(ShipClass, Faction), RenderTexture>();

        /// <summary>A cached snapshot of <paramref name="cls"/>'s hull in <paramref name="faction"/>'s livery, rendering it the first time asked.</summary>
        public static Texture For(ShipClass cls, Faction faction)
        {
            var key = (cls, ShipMeshFactory.Canon(faction));
            if (cache.TryGetValue(key, out var tex) && tex != null) return tex;
            // US-T70: asked from OnGUI (the spawn screen's grid), which Unity 6 runs inside URP's camera stack, a manual
            // Camera.Render throws ("UniversalCameraData has already been created") and left a blank snapshot cached for
            // good. Such a request is rendered at the next LateUpdate instead, outside rendering; the grid draws the cell
            // without its picture until then (one frame), as for a null snapshot.
            if (Event.current != null)
            {
                Pending.Request(key, cls, faction);
                return null;
            }
            tex = Render(cls, faction);
            cache[key] = tex;
            return tex;
        }

        /// <summary>Renders the snapshots asked for during OnGUI at the next LateUpdate (US-T70).</summary>
        sealed class Pending : MonoBehaviour
        {
            static Pending instance;
            readonly Dictionary<(ShipClass, Faction), Faction> wanted = new Dictionary<(ShipClass, Faction), Faction>();

            public static void Request((ShipClass, Faction) key, ShipClass cls, Faction faction)
            {
                if (instance == null)
                {
                    var go = new GameObject("ShipThumbnailQueue") { hideFlags = HideFlags.HideAndDontSave };
                    Object.DontDestroyOnLoad(go);
                    instance = go.AddComponent<Pending>();
                }
                instance.wanted[key] = faction;
            }

            void LateUpdate()
            {
                if (wanted.Count == 0) return;
                var todo = new List<KeyValuePair<(ShipClass, Faction), Faction>>(wanted);
                wanted.Clear();
                foreach (var kv in todo)
                    if (!cache.TryGetValue(kv.Key, out var tex) || tex == null) cache[kv.Key] = Render(kv.Key.Item1, kv.Value);
            }
        }

        /// <summary>Drops every cached snapshot (e.g. if a faction's livery colour could change later).</summary>
        public static void Clear()
        {
            foreach (var tex in cache.Values) if (tex != null) Object.DestroyImmediate(tex);
            cache.Clear();
        }

        static RenderTexture Render(ShipClass cls, Faction faction)
        {
            EnsureCamera();

            var root = new GameObject("ShipThumbPreview") { hideFlags = HideFlags.HideAndDontSave };
            // Off to one side, but near enough the origin for float precision: at 1e6 world units the hull's
            // vertices snapped to a 1/16-unit grid (6% of a one-unit hull), stair-stepping every edge. The
            // preview lives only inside this call and on its own layer, so nothing else ever draws it.
            root.transform.position = new Vector3(1000f, 1000f, 1000f);

            var ship = new Ship { Id = 0, Spec = ShipClassSpec.Of(cls), Faction = faction };
            var renderers = new List<Renderer>();
            ShipModels.Build(ship, root.transform, renderers, out _, out _);
            SetLayerRecursively(root);

            var bounds = new Bounds(root.transform.position, Vector3.one * 0.01f);
            bool any = false;
            foreach (var r in renderers)
            {
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            float radius = Mathf.Max(bounds.extents.magnitude, 0.05f);

            camera.transform.position = bounds.center + Quaternion.Euler(18f, -35f, 0f) * Vector3.back * (radius * 2.6f);
            camera.transform.LookAt(bounds.center);
            camera.nearClipPlane = Mathf.Max(radius * 0.01f, 0.001f);
            camera.farClipPlane = radius * 10f;

            var rt = new RenderTexture(Size, Size, 16, RenderTextureFormat.ARGB32) { name = "ShipThumb_" + cls };
            // Ship.shader lights only from the scene's global star(s), not Unity lights: light the hull from
            // a fixed key direction for the snapshot, not from wherever the game's Sun happens to be just now.
            var backend = RenderBackend.Active;
            var gameLight = backend.Lighting;
            var key = bounds.center + Quaternion.Euler(35f, -30f, 0f) * Vector3.back * (radius * 1000f);
            backend.Lighting = new StarLighting
            {
                SunPosition = key, SunColor = new Vector4(1.2f, 1.2f, 1.2f, 1f), Sun2Position = key, Sun2Color = Vector4.zero,
            };
            camera.targetTexture = rt;
            camera.Render();
            camera.targetTexture = null;
            backend.Lighting = gameLight;

            Object.DestroyImmediate(root);
            return rt;
        }

        static void EnsureCamera()
        {
            if (camera != null) return;
            var go = new GameObject("ShipThumbnailCamera") { hideFlags = HideFlags.HideAndDontSave };
            camera = go.AddComponent<Camera>();
            RenderBackend.Active.SetupCamera(camera, CameraRole.Offscreen);
            // Never auto-rendered into the normal camera stack -- only Render() called explicitly above.
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.05f, 0.06f, 0.09f, 1f);
            camera.cullingMask = 1 << Layer;

            RenderBackend.Active.AddKeyLight(go.transform, "ShipThumbnailLight", Quaternion.Euler(35f, -30f, 0f), 1.2f, 1 << Layer,
                HideFlags.HideAndDontSave);
        }

        static void SetLayerRecursively(GameObject root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layer;
        }
    }
}
