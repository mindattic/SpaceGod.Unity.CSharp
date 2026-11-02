using System.Collections.Generic;
using Unity.SpaceGod.Core;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace Unity.SpaceGod.Rendering.Urp
{
    /// <summary>
    /// The Universal Render Pipeline's backend (U.SPC-T429), the first implementation of <see cref="IRenderBackend"/>
    /// and the only code in the game that references URP. On top of what every pipeline does the same way it gives each
    /// camera URP's per-camera data (a camera added at runtime gets none of its own), turns post-processing into a URP
    /// volume with URP's own overrides, and reads the renderer features off the active URP renderers.
    /// <para>
    /// The game's own camera sees through URP's Volume stack (U.SPC-T451), the cheaper photoreal path than HDRP: HDR,
    /// exposure (fixed, metered automatically or on a histogram of the view and adapting like an eye, or set by the
    /// camera's physical ISO, shutter and aperture), bloom on the star's glare, a tone curve and colour grading, as
    /// <see cref="UrpPostProcessSettings"/> chooses. Off, the camera draws LDR with no post-processing, as before.
    /// The adapting meters read the scene's colour before exposure back from the GPU a frame late (a few dozen pixels
    /// boxed down from the full image), so they cost a handful of small blits and never stall a frame.
    /// </para>
    /// </summary>
    public sealed class UrpRenderBackend : NeutralRenderBackend
    {
        const string VolumeName = "PostProcessVolume";

        static readonly int SunBrightnessId = Shader.PropertyToID(RenderGlobals.SunBrightness);

        static UrpRenderBackend live;

        /// <summary>The game's own cameras, which see by the Volume stack, and the look the game asked of each camera.</summary>
        readonly List<Camera> mainCameras = new List<Camera>();
        readonly Dictionary<Camera, PostProcessLook> gameLooks = new Dictionary<Camera, PostProcessLook>();
        readonly Dictionary<Camera, float> physicalFov = new Dictionary<Camera, float>();

        CameraLook look = CameraLook.Default;

        // Adapting exposure: the stops in effect, the stops the last reading asked for, and the reading under way.
        double adaptedStops = double.NaN, targetStops;
        float lastAdaptTime = -1f;
        readonly ExposureMeterPass meterPass = new ExposureMeterPass();
        RenderTexture meterTexture;
        RTHandle meterHandle;
        bool readbackPending;
        readonly List<double> meterLuminances = new List<double>();

        public UrpRenderBackend() : base(RenderPipelineProfiles.Universal)
        {
            live = this;
            look = UrpPostProcessSettings.Current;
            targetStops = look.Compensation;
            Shader.SetGlobalFloat(SunBrightnessId, StarGlare);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void RegisterAtStartup()
        {
            RenderBackend.Register(RenderPipelineProfiles.Universal.Id, () => new UrpRenderBackend());
            RenderPipelineManager.beginContextRendering -= BeginContext;
            RenderPipelineManager.beginContextRendering += BeginContext;
            RenderPipelineManager.beginCameraRendering -= BeginCamera;
            RenderPipelineManager.beginCameraRendering += BeginCamera;
            RenderPipelineManager.endContextRendering -= EndContext;
            RenderPipelineManager.endContextRendering += EndContext;
        }

#if UNITY_EDITOR
        // Editor tools (scene setup, previews) draw outside play mode too.
        [UnityEditor.InitializeOnLoadMethod]
        static void RegisterInEditor() => RegisterAtStartup();
#endif

        /// <summary>The URP backend made last (null before the first), which the settings menu's choices reach.</summary>
        internal static UrpRenderBackend Existing => live;

        static bool UrpRunning => RenderPipelineManager.currentPipeline is UniversalRenderPipeline;

        float StarGlare => look.Enabled ? (float)System.Math.Max(1.0, look.SunBrightness) : 1f;

        static UniversalAdditionalCameraData CameraData(Camera camera)
        {
            var data = camera.GetComponent<UniversalAdditionalCameraData>();
            if (data == null) data = camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            return data;
        }

        public override void SetupCamera(Camera camera, CameraRole role)
        {
            base.SetupCamera(camera, role);
            // A camera added purely at runtime (unlike one placed in the scene) gets no URP camera data of its own,
            // and without it URP may not render it correctly at all.
            CameraData(camera);
            mainCameras.RemoveAll(c => c == null);
            if (role == CameraRole.Main)
            {
                if (!mainCameras.Contains(camera)) mainCameras.Add(camera);
                Apply(camera);
            }
            else
            {
                mainCameras.Remove(camera);
            }
        }

        public override void SetPostProcessing(Camera camera, PostProcessLook look)
        {
            if (camera == null) return;
            gameLooks[camera] = look;
            Apply(camera);
        }

        /// <summary>Puts <paramref name="value"/> into effect on every one of the game's own cameras (the settings menu's choice).</summary>
        internal void SetLook(CameraLook value)
        {
            bool adapting = look.Adapts;
            look = value;
            if (!look.Adapts || !adapting)
            {
                adaptedStops = double.NaN;
                targetStops = look.Compensation;
            }
            // Another pipeline running (the editor holds both): its own backend draws, and this one waits to be made afresh.
            if (!UrpRunning) return;
            Shader.SetGlobalFloat(SunBrightnessId, StarGlare);
            mainCameras.RemoveAll(c => c == null);
            foreach (var camera in mainCameras) Apply(camera);
        }

        bool IsMain(Camera camera) => mainCameras.Contains(camera);

        /// <summary>The stops the game's own camera is exposed by now: the adapted metering, or the fixed or physical exposure.</summary>
        double MainStops => look.Adapts ? (double.IsNaN(adaptedStops) ? look.Compensation : adaptedStops) : CameraExposure.FixedStops(look);

        void Apply(Camera camera)
        {
            if (camera == null) return;
            var data = CameraData(camera);
            gameLooks.TryGetValue(camera, out var game);
            bool main = IsMain(camera);
            bool stack = main && look.Enabled;
            bool on = stack || game.Enabled;

            // HDR keeps the star's glare above white for bloom and the tone curve; off, the camera draws LDR as before.
            if (main && Profile.HonoursCameraHdr) camera.allowHDR = stack;
            data.renderPostProcessing = on;
            ApplyPhysicalCamera(camera, stack && look.Metering == CameraMetering.PhysicalCamera);

            var existing = camera.transform.Find(VolumeName);
            if (!on)
            {
                if (existing != null) existing.gameObject.SetActive(false);
                return;
            }

            Volume volume;
            if (existing != null)
            {
                existing.gameObject.SetActive(true);
                volume = existing.GetComponent<Volume>();
            }
            else
            {
                var go = new GameObject(VolumeName) { layer = camera.gameObject.layer, hideFlags = HideFlags.DontSave };
                go.transform.SetParent(camera.transform, false);
                volume = go.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.sharedProfile = ScriptableObject.CreateInstance<VolumeProfile>();
                volume.sharedProfile.hideFlags = HideFlags.DontSave;
            }
            data.volumeLayerMask |= 1 << camera.gameObject.layer;

            var profile = volume.sharedProfile;
            float gameEv = game.Enabled ? game.ExposureEv : 0f;
            float contrast = (game.Enabled ? game.Contrast : 0f) + (stack ? (float)look.Contrast : 0f);
            float saturation = (game.Enabled ? game.Saturation : 0f) + (stack ? (float)look.Saturation : 0f);
            float bloomIntensity = stack ? (float)look.Bloom + (game.Enabled ? game.BloomIntensity : 0f) : game.BloomIntensity;
            float bloomThreshold = stack ? (float)look.BloomThreshold : game.BloomThreshold;

            var colour = Get<ColorAdjustments>(profile);
            colour.postExposure.Override((stack ? (float)MainStops : 0f) + gameEv);
            colour.contrast.Override(Mathf.Clamp(contrast, -100f, 100f));
            colour.saturation.Override(Mathf.Clamp(saturation, -100f, 100f));
            Get<WhiteBalance>(profile).temperature.Override(stack ? Mathf.Clamp((float)look.Temperature, -100f, 100f) : 0f);
            var bloom = Get<Bloom>(profile);
            bloom.active = bloomIntensity > 0f;
            bloom.intensity.Override(Mathf.Max(0f, bloomIntensity));
            bloom.threshold.Override(Mathf.Max(0f, bloomThreshold));
            Get<Tonemapping>(profile).mode.Override(!stack ? TonemappingMode.None
                : look.Tonemapping == ToneCurve.Aces ? TonemappingMode.ACES
                : look.Tonemapping == ToneCurve.Neutral ? TonemappingMode.Neutral : TonemappingMode.None);
        }

        static T Get<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (!profile.TryGet(out T component)) component = profile.Add<T>(true);
            return component;
        }

        // URP's physical camera sets the lens (and so the field of view) from a sensor and a focal length: the game keeps
        // its own field of view, set per frame, by fitting the sensor's height to it, and the shutter, aperture and ISO
        // are the exposure's (CameraExposure.PhysicalStops).
        void ApplyPhysicalCamera(Camera camera, bool on)
        {
            if (on)
            {
                float fov = camera.fieldOfView;
                if (!camera.usePhysicalProperties) physicalFov[camera] = fov;
                camera.usePhysicalProperties = true;
                camera.sensorSize = new Vector2(36f, 24f);
                camera.gateFit = Camera.GateFitMode.Vertical;
                camera.iso = Mathf.RoundToInt((float)look.Iso);
                camera.shutterSpeed = (float)look.ShutterSeconds;
                camera.aperture = (float)look.Aperture;
                camera.fieldOfView = fov;
            }
            else if (physicalFov.TryGetValue(camera, out float before))
            {
                float fov = camera.usePhysicalProperties ? camera.fieldOfView : before;
                camera.usePhysicalProperties = false;
                camera.fieldOfView = fov;
                physicalFov.Remove(camera);
            }
        }

        // ---- adapting exposure ------------------------------------------------------------------------------------

        bool Metering => look.Adapts && SystemInfo.supportsAsyncGPUReadback;

        void EnsureMeterTarget()
        {
            if (meterTexture != null && meterTexture.IsCreated()) return;
            if (meterHandle != null) meterHandle.Release();
            meterTexture = new RenderTexture(ExposureMeterPass.Size, ExposureMeterPass.Size, 0, GraphicsFormat.R16G16B16A16_SFloat)
            {
                name = "UnitySpaceExposureMeter", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear,
                useMipMap = false, autoGenerateMips = false,
            };
            meterTexture.Create();
            meterHandle = RTHandles.Alloc(meterTexture);
        }

        // Each frame: the exposure eases toward the last reading, and every one of the game's cameras takes it.
        static void BeginContext(ScriptableRenderContext context, List<Camera> cameras)
        {
            var backend = live;
            if (backend == null || !UrpRunning || !backend.look.Adapts) return;
            float now = Time.unscaledTime;
            float dt = backend.lastAdaptTime < 0f ? 0f : Mathf.Max(0f, now - backend.lastAdaptTime);
            backend.lastAdaptTime = now;
            double before = backend.adaptedStops;
            backend.adaptedStops = double.IsNaN(before) ? backend.targetStops : CameraExposure.Adapt(before, backend.targetStops, dt);
            if (!double.IsNaN(before) && System.Math.Abs(backend.adaptedStops - before) < 1e-4) return;
            backend.mainCameras.RemoveAll(c => c == null);
            foreach (var camera in backend.mainCameras)
            {
                var volume = camera.transform.Find(VolumeName);
                if (volume == null || !volume.gameObject.activeSelf) continue;
                var profile = volume.GetComponent<Volume>().sharedProfile;
                backend.gameLooks.TryGetValue(camera, out var game);
                Get<ColorAdjustments>(profile).postExposure.Override((float)backend.adaptedStops + (game.Enabled ? game.ExposureEv : 0f));
            }
        }

        // The game's own camera boxes its scene colour (before exposure) down into the meter's small target.
        static void BeginCamera(ScriptableRenderContext context, Camera camera)
        {
            var backend = live;
            if (backend == null || !UrpRunning || !backend.Metering || backend.readbackPending || !backend.IsMain(camera)) return;
            var data = camera.GetComponent<UniversalAdditionalCameraData>();
            if (data == null || !data.renderPostProcessing) return;
            backend.EnsureMeterTarget();
            backend.meterPass.Target = backend.meterHandle;
            data.scriptableRenderer.EnqueuePass(backend.meterPass);
        }

        // Once the frame is drawn, the meter's pixels come back from the GPU when they are ready (a frame or so later).
        static void EndContext(ScriptableRenderContext context, List<Camera> cameras)
        {
            var backend = live;
            if (backend == null || !backend.meterPass.Recorded) return;
            backend.meterPass.Recorded = false;
            if (backend.readbackPending || backend.meterTexture == null) return;
            backend.readbackPending = true;
            AsyncGPUReadback.Request(backend.meterTexture, 0, TextureFormat.RGBAHalf, backend.OnMeterRead);
        }

        void OnMeterRead(AsyncGPUReadbackRequest request)
        {
            readbackPending = false;
            if (request.hasError || !look.Adapts) return;
            var halves = request.GetData<ushort>();
            meterLuminances.Clear();
            for (int i = 0; i + 3 < halves.Length; i += 4)
                meterLuminances.Add(CameraExposure.Luminance(Mathf.HalfToFloat(halves[i]), Mathf.HalfToFloat(halves[i + 1]), Mathf.HalfToFloat(halves[i + 2])));
            double average = look.Metering == CameraMetering.Histogram
                ? CameraExposure.HistogramAverage(meterLuminances)
                : CameraExposure.LogAverage(meterLuminances);
            targetStops = CameraExposure.TargetStops(average, look.Compensation);
        }

        /// <summary>
        /// Boxes the camera's colour, before post-processing (and so before exposure), down by halves to the meter's
        /// <see cref="Size"/>-pixel square, which the backend reads back to meter the view.
        /// </summary>
        sealed class ExposureMeterPass : ScriptableRenderPass
        {
            public const int Size = 32;

            public RTHandle Target;
            public bool Recorded;

            public ExposureMeterPass()
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
                profilingSampler = new ProfilingSampler("Exposure meter");
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                if (Target == null || resources.isActiveTargetBackBuffer) return;
                TextureHandle source = resources.activeColorTexture;
                if (!source.IsValid()) return;
                var desc = renderGraph.GetTextureDesc(source);
                int w = desc.width, h = desc.height;
                // Each halving samples between four pixels, a 2x2 box: the chain averages the whole image, star and all.
                while (w > Size * 2 || h > Size * 2)
                {
                    w = Mathf.Max(1, w / 2);
                    h = Mathf.Max(1, h / 2);
                    var half = new TextureDesc(w, h)
                    {
                        format = GraphicsFormat.R16G16B16A16_SFloat, name = "ExposureMeterLevel", filterMode = FilterMode.Bilinear, clearBuffer = false,
                    };
                    var next = renderGraph.CreateTexture(half);
                    renderGraph.AddBlitPass(source, next, Vector2.one, Vector2.zero, passName: "Exposure meter downsample");
                    source = next;
                }
                renderGraph.AddBlitPass(source, renderGraph.ImportTexture(Target), Vector2.one, Vector2.zero, passName: "Exposure meter");
                Recorded = true;
            }
        }

        public override IReadOnlyList<string> RenderFeatures
        {
            get
            {
                var names = new List<string>();
                var asset = UniversalRenderPipeline.asset;
                if (asset == null) return names;
                foreach (var renderer in asset.rendererDataList)
                {
                    if (renderer == null) continue;
                    foreach (var feature in renderer.rendererFeatures)
                        if (feature != null && !names.Contains(feature.name)) names.Add(feature.name);
                }
                return names;
            }
        }
    }
}
