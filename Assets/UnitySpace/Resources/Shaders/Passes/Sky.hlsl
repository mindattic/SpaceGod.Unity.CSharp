// The pass of Unity.SpaceGod/Sky (U.SPC-T595), shared by its URP and HDRP SubShaders: the shader includes
// PipelineCore.hlsl first, so this code is the same on every pipeline.
TEXTURE2D(_MainTex);
SAMPLER(sampler_MainTex);

CBUFFER_START(UnityPerMaterial)
    float4 _MainTex_ST;
    float _Brightness;
    float4 _AxisX;
    float4 _AxisY;
    float4 _AxisZ;
CBUFFER_END

struct Attributes
{
    float4 positionOS : POSITION;
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 dirOS : TEXCOORD0;
};

Varyings vert(Attributes v)
{
    Varyings o;
    float3 dirWS = mul((float3x3)UNITY_MATRIX_M, v.positionOS.xyz);
    float4 cs = mul(UNITY_MATRIX_VP, float4(dirWS, 0.0));
    #if UNITY_REVERSED_Z
        cs.z = cs.w * 1e-7;
    #else
        cs.z = cs.w * (1.0 - 1e-7);
    #endif
    o.positionCS = cs;
    o.dirOS = v.positionOS.xyz;
    return o;
}

half4 frag(Varyings i) : SV_Target
{
    float3 d = normalize(i.dirOS);
    // The direction in the panorama's own frame: longitude from its x toward its y, latitude toward its z.
    float fx = dot(d, _AxisX.xyz), fy = dot(d, _AxisY.xyz), fz = dot(d, _AxisZ.xyz);
    float lon = atan2(fy, fx);
    float lat = asin(clamp(fz, -1.0, 1.0));
    float2 uv = float2(0.5 - lon * (0.5 / PI), 0.5 + lat / PI);
    // u jumps from 1 to 0 where longitude wraps: take the gradient from whichever of u and u + 0.5 is smooth there,
    // so the seam does not drop to the smallest mip as a visible line.
    float2 uvA = uv, uvB = float2(frac(uv.x + 0.5), uv.y);
    float2 dxA = ddx(uvA), dyA = ddy(uvA), dxB = ddx(uvB), dyB = ddy(uvB);
    float2 gx = abs(dxA.x) <= abs(dxB.x) ? dxA : dxB;
    float2 gy = abs(dyA.x) <= abs(dyB.x) ? dyA : dyB;
    half3 c = SAMPLE_TEXTURE2D_GRAD(_MainTex, sampler_MainTex, uv, gx, gy).rgb;
    return PipelineExpose(half4(c * _Brightness, 1));
}
