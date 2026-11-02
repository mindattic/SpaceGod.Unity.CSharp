using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.SpaceGod.Core;
using Unity.SpaceGod.Rendering;
using UnityEngine;
using UnityEngine.Networking;

namespace Unity.SpaceGod.Views
{
    /// <summary>
    /// The skybox behind the system in view (U.SPC-T595): one of <see cref="SkyboxCatalog"/>'s panoramas on a unit cube
    /// pushed to the far plane (Unity.SpaceGod/Sky), drawn just before the point-cloud starfield, which it hides when the
    /// sky has stars of its own. Every system shows the sky <see cref="Memory"/> keeps for it; <see cref="Cycle"/> (Shift+K,
    /// numpad *) moves it on. Nothing loads on the frame: a shipped panorama loads asynchronously from Resources, a
    /// dropped-in one through UnityWebRequestTexture, and a seeded sky is baked on worker threads; the old sky stays up
    /// until the new one is ready, which then fades in.
    /// </summary>
    public sealed class SkyView
    {
        /// <summary>The folder the player drops their own panoramas into (2:1 .jpg or .png): under the game's data folder.</summary>
        public static string DropInFolder => Path.Combine(Application.persistentDataPath, "Skyboxes");

        const float FadeSeconds = 0.6f;

        static readonly int MainTexId = Shader.PropertyToID("_MainTex"), BrightnessId = Shader.PropertyToID("_Brightness");
        static readonly int AxisXId = Shader.PropertyToID("_AxisX"), AxisYId = Shader.PropertyToID("_AxisY"), AxisZId = Shader.PropertyToID("_AxisZ");

        public readonly SkyboxMemory Memory = new SkyboxMemory();

        readonly GameObject root;
        readonly MeshRenderer renderer;
        readonly Material material;
        readonly Mesh mesh;
        readonly GameObject starfield;

        string systemName;
        SkyboxSpec wanted;
        string wantedKey, shownKey;
        Texture2D texture;
        bool textureFromResources;
        float fade = 1;
        int request;

        ResourceRequest pendingResource;
        UnityWebRequest pendingFile;
        Task<byte[]> pendingBake;
        int pendingBakeWidth;
        int[][] pendingBakeMips;
        CancellationTokenSource bakeCancel;
        readonly Action<GameSetting> onBrightness, onResolution;

        public SkyView(Transform parent, GameObject starfield)
        {
            this.starfield = starfield;
            root = new GameObject("Skybox");
            root.transform.SetParent(parent, false);
            mesh = BuildCube();
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            renderer = root.AddComponent<MeshRenderer>();
            material = Materials.MarkShared(Materials.Create(Materials.Sky, "Skybox"));
            material.renderQueue = 999; // just before the starfield (Background, 1000)
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.enabled = false;
            onBrightness = _ => ApplyBrightness();
            onResolution = _ => { if (wanted != null && wanted.Kind == SkyKind.Procedural) Apply(wanted); };
            SkyboxSettings.Brightness.Changed += onBrightness;
            SkyboxSettings.NebulaResolution.Changed += onResolution;
        }

        /// <summary>The sky the system in view shows (or is loading).</summary>
        public SkyboxSpec Current => wanted;

        /// <summary>Registers every panorama in <see cref="DropInFolder"/> with the catalogue (once, at start); returns how many.</summary>
        public static int DiscoverDropIns()
        {
            int found = 0;
            try
            {
                if (!Directory.Exists(DropInFolder)) return 0;
                var files = Directory.GetFiles(DropInFolder);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext != ".jpg" && ext != ".jpeg" && ext != ".png") continue;
                    string stem = Path.GetFileNameWithoutExtension(file);
                    if (SkyboxCatalog.Register(new SkyboxSpec("file:" + Path.GetFileName(file), "Your sky: " + stem, SkyKind.ImageFile, file,
                            SkyFrame.Ecliptic, false, "Dropped into " + DropInFolder, "Yours to check")))
                        found++;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning("[Unity.SpaceGod] Could not read the skybox folder " + DropInFolder + ": " + e.Message);
            }
            return found;
        }

        /// <summary>Shows the sky <paramref name="system"/> keeps (called whenever the system in view changes).</summary>
        public void Show(string system)
        {
            systemName = system;
            Apply(Memory.For(system));
        }

        /// <summary>Gives the system in view the next sky (<paramref name="step"/> back for a negative one) and keeps it; returns it.</summary>
        public SkyboxSpec Cycle(int step = 1)
        {
            var next = Memory.Cycle(systemName, step);
            Apply(next);
            return next;
        }

        void Apply(SkyboxSpec spec)
        {
            if (spec == null) return;
            wanted = spec;
            wantedKey = spec.Kind == SkyKind.Procedural ? spec.Id + "@" + systemName + "@" + SkyboxSettings.NebulaFaceSize : spec.Id;
            // Whatever was loading for the last choice is dropped, even when this one is already on screen.
            int id = ++request;
            CancelPending();
            if (wantedKey == shownKey && (texture != null || spec.Kind == SkyKind.Starfield)) return;
            switch (spec.Kind)
            {
                case SkyKind.Starfield:
                    Release();
                    renderer.enabled = false;
                    if (starfield != null) starfield.SetActive(true);
                    shownKey = wantedKey;
                    break;
                case SkyKind.Image:
                    var load = Resources.LoadAsync<Texture2D>(spec.Path);
                    pendingResource = load;
                    load.completed += _ => OnResourceLoaded(id, spec, load);
                    break;
                case SkyKind.ImageFile:
                    pendingFile = UnityWebRequestTexture.GetTexture(new Uri(spec.Path).AbsoluteUri, true);
                    pendingFile.SendWebRequest();
                    break;
                case SkyKind.Procedural:
                    int width = SkyboxSettings.NebulaFaceSize * 4;
                    ulong seed = ProceduralSky.SeedFor(systemName);
                    var mips = new int[1][];
                    pendingBakeWidth = width;
                    pendingBakeMips = mips;
                    bakeCancel = new CancellationTokenSource();
                    var token = bakeCancel.Token;
                    pendingBake = Task.Run(() =>
                    {
                        try
                        {
                            var data = ProceduralSky.BakePanorama(seed, width, out var offsets, token);
                            mips[0] = offsets;
                            return data;
                        }
                        catch (OperationCanceledException)
                        {
                            return null; // overtaken by another choice
                        }
                    });
                    break;
            }
        }

        void OnResourceLoaded(int id, SkyboxSpec spec, ResourceRequest req)
        {
            var tex = req.asset as Texture2D;
            if (id != request)
            {
                // Overtaken by another choice: let go of it unless it is the one on screen.
                if (tex != null && tex != texture) Resources.UnloadAsset(tex);
                return;
            }
            pendingResource = null;
            if (tex == null)
            {
                Debug.LogError("[Unity.SpaceGod] Missing skybox Resources/" + spec.Path);
                return;
            }
            Swap(tex, true, spec);
        }

        /// <summary>Picks up a finished load or bake and fades the sky in; once a frame.</summary>
        public void Update(float dt)
        {
            if (pendingFile != null && pendingFile.isDone)
            {
                var req = pendingFile;
                pendingFile = null;
                if (req.result == UnityWebRequest.Result.Success)
                {
                    var tex = DownloadHandlerTexture.GetContent(req);
                    tex.wrapModeU = TextureWrapMode.Repeat;
                    tex.wrapModeV = TextureWrapMode.Clamp;
                    tex.anisoLevel = 4;
                    Swap(tex, false, wanted);
                }
                else Debug.LogWarning("[Unity.SpaceGod] Could not load the skybox " + wanted?.Path + ": " + req.error);
                req.Dispose();
            }
            if (pendingBake != null && pendingBake.IsCompleted)
            {
                var bake = pendingBake;
                pendingBake = null;
                if (bake.Status == TaskStatus.RanToCompletion && bake.Result != null) Swap(Upload(bake.Result, pendingBakeWidth, pendingBakeMips[0]), false, wanted);
                else if (bake.Status == TaskStatus.Faulted) Debug.LogError("[Unity.SpaceGod] The seeded sky could not be made: " + bake.Exception?.GetBaseException().Message);
            }
            if (fade < 1)
            {
                fade = Mathf.Min(1, fade + dt / FadeSeconds);
                ApplyBrightness();
            }
        }

        static Texture2D Upload(byte[] data, int width, int[] mips)
        {
            var tex = new Texture2D(width, width / 2, TextureFormat.RGBA32, mips.Length, false)
            {
                name = "Seeded sky",
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
                hideFlags = HideFlags.DontSave,
            };
            for (int i = 0; i < mips.Length; i++) tex.SetPixelData(data, i, mips[i]);
            tex.Apply(false, true);
            return tex;
        }

        void Swap(Texture2D tex, bool fromResources, SkyboxSpec spec)
        {
            if (spec == null) return;
            var old = texture;
            bool oldFromResources = textureFromResources;
            texture = tex;
            textureFromResources = fromResources;
            material.SetTexture(MainTexId, tex);
            SkyboxCatalog.FrameAxes(spec.Frame, out var x, out var y, out var z);
            material.SetVector(AxisXId, BodyView.ToUnity(x));
            material.SetVector(AxisYId, BodyView.ToUnity(y));
            material.SetVector(AxisZId, BodyView.ToUnity(z));
            renderer.enabled = true;
            if (starfield != null) starfield.SetActive(spec.PointStars);
            shownKey = wantedKey;
            fade = 0;
            ApplyBrightness();
            if (old != null && old != tex) Free(old, oldFromResources);
        }

        void ApplyBrightness() => material.SetFloat(BrightnessId, (float)SkyboxSettings.Brightness.Value * fade * fade);

        void CancelPending()
        {
            // A Resources load cannot be stopped: its completion sees the request id moved on and lets the texture go.
            pendingResource = null;
            if (pendingFile != null)
            {
                pendingFile.Abort();
                pendingFile.Dispose();
                pendingFile = null;
            }
            pendingBake = null; // a bake under way stops at its next row and is dropped
            if (bakeCancel != null)
            {
                // Cancelled, not disposed: the worker may still be reading the token.
                bakeCancel.Cancel();
                bakeCancel = null;
            }
        }

        void Release()
        {
            if (texture == null) return;
            material.SetTexture(MainTexId, null);
            Free(texture, textureFromResources);
            texture = null;
        }

        static void Free(Texture2D tex, bool fromResources)
        {
            if (fromResources) Resources.UnloadAsset(tex);
            else UnityEngine.Object.Destroy(tex);
        }

        /// <summary>Lets go of the sky's texture, material and mesh (the game scene is going).</summary>
        public void Dispose()
        {
            ++request;
            SkyboxSettings.Brightness.Changed -= onBrightness;
            SkyboxSettings.NebulaResolution.Changed -= onResolution;
            CancelPending();
            Release();
            if (material != null) UnityEngine.Object.Destroy(material);
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
        }

        static Mesh BuildCube()
        {
            var v = new Vector3[8];
            for (int i = 0; i < 8; i++) v[i] = new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1);
            int[] t =
            {
                0, 2, 1, 1, 2, 3, // -z
                4, 5, 6, 5, 7, 6, // +z
                0, 1, 4, 1, 5, 4, // -y
                2, 6, 3, 3, 6, 7, // +y
                0, 4, 2, 2, 4, 6, // -x
                1, 3, 5, 3, 7, 5, // +x
            };
            var m = new Mesh { name = "Skybox", vertices = v, triangles = t, hideFlags = HideFlags.DontSave };
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1e9f);
            return m;
        }
    }
}
