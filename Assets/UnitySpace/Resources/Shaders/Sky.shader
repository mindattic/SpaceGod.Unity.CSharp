// The skybox (U.SPC-T595): an equirectangular panorama on a unit cube around the camera, pushed to the far plane in the
// vertex shader like the starfield, drawn just before it. The panorama's frame axes (ecliptic, equatorial or galactic)
// come from SkyboxCatalog.FrameAxes, so a real sky map sits where its stars really are.
Shader "Unity.SpaceGod/Sky"
{
    Properties
    {
        _MainTex ("Panorama", 2D) = "black" {}
        _Brightness ("Brightness", Float) = 1
        _AxisX ("Frame longitude 0", Vector) = (1, 0, 0, 0)
        _AxisY ("Frame longitude 90", Vector) = (0, 0, 1, 0)
        _AxisZ ("Frame north", Vector) = (0, 1, 0, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "Background" "RenderPipeline" = "UniversalPipeline" "Queue" = "Background-1" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "PipelineCore.hlsl"
            #include "Passes/Sky.hlsl"
            ENDHLSL
        }
    }
    SubShader
    {
        Tags { "RenderType" = "Background" "RenderPipeline" = "HDRenderPipeline" "Queue" = "Background-1" "IgnoreProjector" = "True" }
        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #define UNITYSPACE_PIPELINE_HDRP
            #include "PipelineCore.hlsl"
            #include "Passes/Sky.hlsl"
            ENDHLSL
        }
    }
}
