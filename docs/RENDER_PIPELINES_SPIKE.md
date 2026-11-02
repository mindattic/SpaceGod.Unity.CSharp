# URP vs HDRP: spike findings (U.SPC-T428)

Question: we run URP 17.4 today. Per Unity's [render pipeline feature comparison](https://docs.unity3d.com/6000.1/Documentation/Manual/render-pipelines-feature-comparison.html),
what photoreal ground does HDRP cover that URP doesn't, what would our custom rendering need to
work on both, can one build ship both pipelines and switch on restart, and is any of it worth the
cost?

Short answer: stay on URP. Nothing HDRP adds fits how this game actually renders a universe, the
custom shader surface would double, and the one place HDRP's signature feature (a physically based
sky) would genuinely help — atmospheric entry at a docking ring — is a narrow, separable experiment,
not a reason to migrate the whole renderer. See **Recommendation** and **Tickets filed**.

## What's actually in the project today

Eleven hand-written shaders (1,058 lines, `Assets/UnitySpace/Resources/Shaders/*.shader`:
`Planet`, `GasGiant`, `Sun`, `Ring`, `Stars`, `Ship`, `Asteroids`, `Fx`, `Glow`, `Line`, `Points`),
every one:

- Tagged `"RenderPipeline" = "UniversalPipeline"` and gated to it; Unity renders them as pink/error
  shaders under any other pipeline (HDRP included) until they get an HDRP `SubShader`.
- `#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"` — URP's
  umbrella include, not shared with HDRP.
- Lit by **custom globals**, not Unity lights: `UnitySpaceApp.cs` (`~2357-2369`) calls
  `Shader.SetGlobalVector("_SunPositionWS"/"_SunColor"/"_Sun2PositionWS"/"_Sun2Color", …)` every
  frame from wherever the Sun (and, in Alpha Centauri, a second star) sits relative to the
  floating-origin camera; `SolarCommon.hlsl` declares them and the shared noise functions. No
  shader here reads a `Light` component, casts or receives a shadow map, or runs through either
  pipeline's light loop. The only two real `Light` components in the whole codebase
  (`ModelViewer.cs:156`, `ShipThumbnails.cs:110`) are directional lights in isolated preview/thumbnail
  scenes, unrelated to the game's own rendering.
- Use only pipeline-neutral vertex plumbing where it matters for portability, plus one
  URP-specific convenience call: `GetVertexPositionInputs()` (`Planet`, `Sun`, `Ship`) is defined in
  URP's `ShaderVariablesFunctions.hlsl`, not in the shared `com.unity.render-pipelines.core`
  package. The functions it wraps — `TransformObjectToWorld`, `TransformWorldToHClip`,
  `TransformObjectToWorldNormal`, `TransformObjectToHClip` — live in `com.unity.render-pipelines.core/ShaderLibrary/SpaceTransforms.hlsl`,
  which URP and HDRP both depend on and which `Asteroids`, `Ring`, `Stars`, `Fx` already call
  directly.
- `Asteroids.shader` draws all 111,267 belt bodies from one `StructuredBuffer<float4>`, solving
  Kepler's equation per vertex and positioned via `Graphics.RenderPrimitives` (`AsteroidBelt.cs:70`)
  — a modern, SRP-agnostic Unity 6 API with no pipeline-specific path.

The renderer configuration is equally plain: `UnitySpace_URP_Renderer.asset` has **zero** custom
render features and no assigned post-process data; `UnitySpaceApp.cs:479-482` sets
`allowHDR = false`, `allowMSAA = true` directly on `Camera.main`; there is no `Volume` component,
no post-processing profile, and no use of Unity's exposure/bloom/color-grading stack anywhere in
the project. Whatever "photoreal" the game has today, it comes entirely from the hand-written
shaders above, not from URP's built-in feature set.

The floating-origin camera (`Assets/UnitySpace/Runtime/Camera/SpaceCamera.cs`) is pure C# state
(`DVec3` doubles, `Quaternion`s) with no rendering-API or pipeline dependency at all: it never
touches a `Camera`, a `Light`, or a shader. `UnitySpaceApp` reads its `ScenePosition`/`Rotation`
each frame and places the one real `Camera` at the Unity origin, converts `nearClipPlane` from
`NearestSurface` and sets `farClipPlane = 5e8f`. None of that cares which SRP is active.

## What HDRP adds (Unity's feature-comparison table)

| Area | URP 17 | HDRP | Fits this game? |
| --- | --- | --- | --- |
| Physical Sky / Atmospheric Scattering / Gradient Sky | No | Yes | Built for *one* atmosphere around the camera's current ground, not for many distant planets each needing their own rim. See below. |
| Volumetric Clouds / Local Volumetrics / Fog Scattering | No | Yes | Same mismatch: a single-atmosphere feature. No in-cloud or in-atmosphere flight exists today except docking-ring descent. |
| Ray-traced AO / GI / reflections / shadows / recursive rendering | No | Yes (DX12 + RTX hardware only) | Nothing in the game casts or receives shadows from a real light today; adopting ray tracing means adopting Unity's real-time lighting model first, which the custom shaders deliberately bypass. |
| Area lights with real-time shadows | Rect/disc, baked only | Rect (real-time + baked), tube, disc | The project uses no `Light` components in gameplay rendering at all (ships, planets, rings are all custom-lit from `_SunPositionWS`). |
| Exposure | Fixed only | Fixed, Automatic/eye adaptation, Curve, Physical Camera, Histogram | Real win *if* a Volume/post-process stack existed — it doesn't yet, on either pipeline. |
| Physical Camera | Affects FOV only | Affects FOV, exposure, bloom, depth of field | Same: needs the Volume stack first. |

(Full detail: [render-pipelines-feature-comparison.html](https://docs.unity3d.com/6000.1/Documentation/Manual/render-pipelines-feature-comparison.html).)

The pattern across every row: HDRP's photoreal features are built around Unity's real-time lighting
model (lights, shadow maps, Volumes, the physically based sky's "Sun Source" light) and a
single-planet, player-stands-on-the-ground frame of reference. This game's renderer is the opposite
on purpose — floating origin, multiple suns driven by plain globals, planets as small rim-lit
objects seen from orbit or interplanetary distance, and a custom atmosphere-rim shader *per planet*
rather than one sky volume for "the" atmosphere. Physically Based Sky models the sky as seen from
inside one atmosphere; it has no notion of "also render Jupiter's atmosphere as a background object
2 AU away," which is what `Planet.shader`'s rim term already does cheaply, per planet, with
`_AtmoColor`/`_AtmoStrength`.

The one place in the design brief where a player genuinely sits inside a single atmosphere is
docking-ring descent ("it drops tail-first down the line to the city, glowing with entry plasma
through the atmosphere," README "Docking rings"). That is the one spot HDRP's Physically Based Sky
and volumetric fog would be a plausible, bounded upgrade — not a reason to re-platform the whole
renderer.

## What would need to change to run on both pipelines

1. **Every one of the 11 shaders needs a second `SubShader`** tagged
   `"RenderPipeline" = "HDRenderPipeline"`, including HDRP's own core include and pass-tag
   boilerplate (HDRP hand-written passes need `#define SHADERPASS SHADERPASS_FORWARD_UNLIT` and
   HDRP's `ShaderPass`/`Material` includes — noticeably more boilerplate per pass than URP, though
   mechanical, since none of these shaders need HDRP's physically based light loop: they'd use
   HDRP's lightweight **Unlit** pass template, not **Lit**). The actual shading math (Lambert
   terminator, limb darkening, ring opacity bands, Kepler-solve-per-vertex, additive FX shapes) is
   already pipeline-neutral and could move into shared `.hlsl` files (`SolarCommon.hlsl` already is
   one) included from both `SubShader` blocks, so the duplication is the plumbing, not the look.
2. **Drop `GetVertexPositionInputs()`** (`Planet`, `Sun`, `Ship`) in favour of calling
   `TransformObjectToWorld`/`TransformWorldToHClip`/`TransformObjectToWorldNormal` directly, as the
   other eight shaders already do — a few lines per file, makes the vertex stage identical on both
   pipelines, and is worth doing regardless of the HDRP decision for consistency.
3. **Camera setup** (`UnitySpaceApp.cs:479-482`): `allowHDR = false` would need to become
   pipeline-conditional — HDRP always renders HDR and ignores the flag — and `allowMSAA` has no
   effect under HDRP (HDRP's AA is chosen through Frame Settings/Camera Frame Settings instead of
   `Camera.allowMSAA`). Small, mechanical, but another thing that forks on pipeline.
4. **Lighting input**: HDRP's own sky and any HDRP Volume override that wants a sun direction reads
   a `Light` flagged "Sun Source," not a shader global. Keeping the existing
   `Shader.SetGlobalVector` approach as the single source of truth (and skipping HDRP's sky/Volume
   system entirely except where explicitly adopted, as in the docking-ring idea above) avoids having
   to maintain two parallel "where is the Sun" paths.
5. **Materials and project settings**: `UnitySpace_URP_Renderer.asset` and
   `UniversalRenderPipelineGlobalSettings.asset` have no HDRP equivalents checked in; HDRP needs its
   own `HDRenderPipelineAsset` and `HDRenderPipelineGlobalSettings`, plus the
   `com.unity.render-pipelines.high-definition` package (not currently in `Packages/manifest.json`,
   which only lists `com.unity.render-pipelines.universal: 17.4.0`). Kenney/GLTF-imported ship
   models and any Standard/URP Lit materials on them would need Unity's Render Pipeline Converter
   run and checked by hand — it doesn't touch the 11 custom shaders.
6. **The floating-origin camera needs nothing.** `SpaceCamera.cs` has zero rendering-API surface;
   it stays identical on either pipeline. This is the one part of the ask with a one-line answer.

## Can one build ship both pipelines and switch on restart?

Mechanically, yes, with caveats. Both SRP packages can be installed in the same project side by
side. Unity's own docs for `GraphicsSettings`/`QualitySettings` document reassigning the active
`RenderPipelineAsset` from C# in a built player — the new pipeline becomes active as soon as the
asset is assigned. But the same docs warn that doing so **destroys the current render pipeline
instance and recreates it** (`CreatePipelineAsset.CreatePipeline()`), call it "can be a
computationally resource-intensive operation," and require "that the assets and code in your
project are compatible with the new render pipeline" — i.e. every material's shader must already
have a `SubShader` tagged for whichever pipeline becomes active, or it renders pink.

That makes a **live, mid-session** URP↔HDRP toggle a bad idea here regardless: it would recreate
the whole pipeline the instant the player touched the setting, and because URP and HDRP are
different SRPs entirely (not two quality tiers of the same one, which is the scenario
`QualitySettings.renderPipeline` is meant for), everything the active pipeline owns — the camera's
Frame Settings vs. URP renderer data, the sky, any Volume stack, shadow settings — would have to be
rebuilt from whichever savegame/settings state the player last used. The "switch on restart" framing
in the ticket is the right one: persist the chosen pipeline as a setting, assign it to
`GraphicsSettings`/`QualitySettings` **before the first camera renders**, and relaunch the render
path (practically, relaunch the app, or at minimum reload the scene before any frame is drawn) rather
than hot-swap while flying.

The cost of actually doing this: both pipelines' shader variants ship in the build (longer shader
compile/strip time, larger build), both sets of package dependencies are pulled in, and every new
shader or render feature from here on is reviewed twice. Unity itself treats
URP-and-HDRP-in-one-project as a migration path (converting *from* one *to* the other), not a
supported "pick one of two first-class pipelines at launch" pattern — there's no documented or
tooled support for shipping both as co-equal, switchable targets, only the raw asset-reassignment
mechanism described above.

## Cost

| Item | Rough cost |
| --- | --- |
| Drop `GetVertexPositionInputs` for shared core transform calls (3 files) | Under half a day; worth doing regardless of pipeline choice |
| HDRP `SubShader` + shared `.hlsl` math for all 11 shaders | Several days: each is small, but HDRP's hand-written-pass boilerplate is new ground for this codebase, and testing (atmosphere rims, ring shadowing, the Kepler-solve asteroid vertex shader, additive FX blending) needs doing per pipeline |
| HDRP package, `HDRenderPipelineAsset`/global settings, camera Frame Settings path, material conversion for imported ship models | 1-2 days setup, ongoing dual maintenance after |
| Build/CI cost of shipping both (variants, package size, compile time) | Permanent tax on every future shader/material change |
| Platform cost | HDRP drops WebGL and has materially weaker mobile support than URP; ray tracing is Windows+DX12+RTX only. The project targets Windows today, but URP keeps every door open that HDRP closes. |
| Photoreal payoff for the above | Low: none of HDRP's standout rows (ray tracing, area-light shadows, volumetric clouds, multi-planet physically based sky) fit a floating-origin, multi-star, custom-lit-per-object renderer that doesn't use real-time lights at all today |

## Recommendation

Stay on URP 17. Don't migrate, and don't ship both pipelines in one build — the cost (doubled
shader surface, doubled package/material maintenance, a real build-size and CI tax, narrower
platform reach) buys features that don't fit how this game is actually lit and rendered. Two things
are worth doing instead, both much cheaper than an HDRP migration:

1. **Keep going with the render-pipeline abstraction** already filed as U.SPC-T429 (requires this
   spike): put every pipeline-specific piece behind a pipeline-agnostic layer so a future pipeline
   decision — HDRP or otherwise — is a contained swap instead of a search-and-replace across the
   codebase, regardless of whether it's ever exercised.
2. **Turn on URP's own Volume stack** (exposure — including the automatic/histogram modes URP
   *does* support once a Volume profile exists — bloom, physical camera, colour grading). The
   project uses none of it today on either pipeline (`allowHDR = false`, no `Volume` component
   anywhere), so this is a strictly cheaper way to get most of the "photoreal exposure/camera" ground
   HDRP's table claims, without touching a single shader.

HDRP's physically based sky remains worth a narrow, separate look specifically for docking-ring
atmospheric entry, once that sequence gets a dedicated visual pass — filed below as its own
low-priority experiment, deliberately scoped away from "migrate the renderer."

## Tickets filed

- **U.SPC-T429** (already open, requires this ticket): render-pipeline abstraction groundwork.
  Unaffected in direction by this finding — still worth doing so the decision stays cheap to
  revisit.
- **U.SPC-T451** (requires this ticket): wire up URP's Volume stack (exposure, physical camera,
  bloom, colour grading) as the cheaper alternative to an HDRP migration for "photoreal" gains.
- **U.SPC-T452** (requires this ticket and U.SPC-T429): narrow HDRP Physically Based Sky experiment
  for docking-ring atmospheric entry only, gated behind the pipeline-abstraction layer, explicitly
  not a renderer migration.

**Follow-up (U.SPC-T452).** The HDRP path that U.SPC-T430 later built drew its physically based sky for whichever
planet the camera was nearest, anywhere in a system. It is now scoped to docking-ring atmospheric entry alone, behind
the pipeline abstraction: `PlanetAtmospheres.EntryBody` (`Core/Rendering/Photoreal.cs`) names the ringed world while
the ship the camera flies or follows (or the ship it escorts) is in descent, landed or ascent through a ring, and
`UnitySpaceApp.UpdateAtmosphere` hands only that world's atmosphere to `IRenderBackend.SetAtmosphere`, clearing it
everywhere else, so orbit and open space draw every planet with the shaders' own rims on HDRP as on URP. Volumetric
fog follows, since HDRP's backend enables it only inside the sky's air. URP is untouched. Revisit when the descent
gets its dedicated visual pass.

Both new tickets are filed `open`, pending approval per house rules (chores/features wait for a
yes; this spike is research, not a fix).
