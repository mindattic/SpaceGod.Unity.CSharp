// The one place the game's hand-written shaders pull in a render pipeline's shader library (U.SPC-T429).
// Every shader includes this instead of a pipeline package path, and calls only the transforms URP and HDRP
// share (com.unity.render-pipelines.core's SpaceTransforms.hlsl: TransformObjectToWorld, TransformWorldToHClip,
// TransformObjectToWorldNormal, ...) and GetCameraPositionWS(), so a pass is the same code on any pipeline.
//
// Each shader carries one SubShader per pipeline (U.SPC-T430), tagged with that pipeline's "RenderPipeline":
// the URP one includes this as it is, the HDRP one defines UNITYSPACE_PIPELINE_HDRP first. The library is
// chosen here, and so is how a fragment's colour reaches the image (PipelineExpose).
#ifndef UNITYSPACE_PIPELINE_CORE_INCLUDED
#define UNITYSPACE_PIPELINE_CORE_INCLUDED

#if defined(UNITYSPACE_PIPELINE_HDRP)

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

// The luminance (cd/m^2) a shader's 1.0 stands for, and how much brighter the star's face is drawn: set by the HDRP
// backend from RenderPipelineProfiles.HighDefinition (RenderGlobals.SceneLuminance, RenderGlobals.SunBrightness).
float _UnitySpaceSceneLuminance;
float _UnitySpaceSunBrightness;

// The shaders work in display-referred colour (1.0 is white). HDRP keeps light physical and pre-exposed, so the
// colour becomes a luminance (times the scene's reference) and takes the camera's exposure: at the reference
// exposure it is exactly as drawn, and an adapting or compensated exposure brightens or darkens it with the rest.
float4 PipelineExpose(float4 c)
{
    float luminance = _UnitySpaceSceneLuminance > 0.0 ? _UnitySpaceSceneLuminance : GetInverseCurrentExposureMultiplier();
    return float4(c.rgb * (luminance * GetCurrentExposureMultiplier()), c.a);
}

// How much brighter than a lit surface's white the star's photosphere is (it glares and blooms).
float PipelineSunBrightness()
{
    return _UnitySpaceSunBrightness > 0.0 ? _UnitySpaceSunBrightness : 1.0;
}

#else

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

// URP draws the shaders' colours as they are (its Volume stack exposes and grades them afterwards, U.SPC-T451). With
// that stack on, the camera draws HDR and the URP backend sets the star's glare (RenderGlobals.SunBrightness) so its
// face blooms above a lit surface's white; off, the global is 1 and the star is as bright as it is drawn.
float _UnitySpaceSunBrightness;

float4 PipelineExpose(float4 c) { return c; }
float PipelineSunBrightness() { return _UnitySpaceSunBrightness > 0.0 ? _UnitySpaceSunBrightness : 1.0; }

#endif

#endif
