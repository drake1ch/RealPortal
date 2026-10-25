// WindowScreen helper passes (FARWORLD-V3 section 4.4 Phase A, the V1 spike Win.Virtual): a window drawn without a camera
// into screen-sized targets by one command buffer executed at the game camera's MainPreCull. The game's own materials
// write the G-buffer (their Deferred passes), the shadow casters (their ShadowCaster passes), the lighting (the game's
// deferred shading and reflections shaders) and the sky (RenderSettings.skybox); this shader only does what no game
// shader does:
//   0 SAVE       the oblique device depth of the window's G-buffer pass (its near plane is the exit portal) turned into
//                the standard device depth of the game camera's projection, per pixel and exactly (spec section 4.4
//                "Standard depth": eye depth = c3 / (z_ndc - c0 a - c1 b + c2)); writes SV_Depth into StdDepth, which
//                every later consumer reads (lighting, the shadow mask, the sky's depth test, the crop);
//   1 SHADOWMASK the sun's screen-space shadow mask as Unity's Internal-ScreenSpaceShadows computes it with Stable Fit
//                (SHADOWS_SPLIT_SPHERES) and soft shadows (PCF 5x5 of UnityShadowLibrary.cginc), from StdDepth and the
//                window's own cascades (WindowScreenShadows);
//   2 SHADOWMASKHARD the same with hard shadows (one tap);
//   3 CROP       the window's rect out of the screen-sized targets into the textures the composite reads as a slot
//                camera's render: colour (GB3 after lighting and sky), linear eye depth (what LINEARDEPTH gives a
//                window camera), G-buffer normals, and the standard device depth (for the game's fog material).
//
// All passes are drawn as a clip-space full-screen triangle (as PostProcessing v2 and WindowResolve.shader): uv is the
// logical (OpenGL) screen position of the pixel, in the render-texture convention, so every target reads the same pixel of
// every source. The projections are given in the OpenGL convention (Unity's Camera.projectionMatrix); device depth is
// converted with UNITY_REVERSED_Z, so nothing depends on GL.GetGPUProjectionMatrix's per-API row flips.
Shader "Hidden/RealPortal/WindowScreen"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off
        ZWrite Off
        ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"

        struct v2fTri
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        // Full-screen triangle in clip space (vertices (-1,-1), (-1,3), (3,-1), identity matrices); uv in Unity's
        // render-texture convention, flipped in y where UNITY_UV_STARTS_AT_TOP.
        v2fTri vertTri(float4 vertex : POSITION)
        {
            v2fTri o;
            o.pos = float4(vertex.xy, 0, 1);
            o.uv = vertex.xy * 0.5 + 0.5;
            #if UNITY_UV_STARTS_AT_TOP
                o.uv.y = 1 - o.uv.y;
            #endif
            return o;
        }

        // The game camera's projection this frame, OpenGL convention: P00, P11, P02, P12 (TAA jitter in P02/P12). Rows 0, 1
        // and 3 are the same for the oblique and the standard projection, so they give both the pixel's view ray.
        float4 _RP_ProjXY;
        // The standard projection's row 2 (s2, s3; s0 = s1 = 0), its far plane, and the device depth of nothing (the
        // clear value: 0 under reversed Z, else 1).
        float4 _RP_StdZ;

        // OpenGL NDC z of a device depth, and back.
        float glZ(float d)
        {
            #if defined(UNITY_REVERSED_Z)
                return 1 - 2 * d;
            #else
                return 2 * d - 1;
            #endif
        }

        float deviceOfGl(float z)
        {
            #if defined(UNITY_REVERSED_Z)
                return 0.5 - 0.5 * z;
            #else
                return 0.5 * z + 0.5;
            #endif
        }

        // Eye depth (metres along the view) of a standard device depth: z_ndc = -s2 + s3 / eye.
        float stdEyeDepth(float d)
        {
            return _RP_StdZ.y / (glZ(d) + _RP_StdZ.x);
        }

        // The pixel's view ray in camera space (+z forward, x right, y up), at unit depth.
        float3 rayOf(float2 uv)
        {
            float2 ndc = uv * 2 - 1;
            return float3((ndc.x + _RP_ProjXY.z) / _RP_ProjXY.x, (ndc.y + _RP_ProjXY.w) / _RP_ProjXY.y, 1);
        }

        // ---- SAVE ----
        sampler2D_float _RP_ObliqueDepth;
        // The oblique projection's row 2 (OpenGL): c0, c1, c2, c3.
        float4 _RP_OblRow2;

        // ---- SHADOWMASK: the window's cascades (WindowScreenShadows): world to atlas (uv, device depth), the split
        // spheres and their squared radii, and x: _LightShadowData.r (1 - strength), y: 1 for a single cascade.
        UNITY_DECLARE_SHADOWMAP(_ShadowMapTexture);
        float4 _ShadowMapTexture_TexelSize;
        #define SHADOWMAPSAMPLER_AND_TEXELSIZE_DEFINED
        #include "UnityShadowLibrary.cginc"

        sampler2D_float _RP_StdDepth;
        float4x4 _RP_CamToWorld;
        float4x4 _RP_WorldToShadow[4];
        float4 _RP_SplitSpheres[4];
        float4 _RP_SplitSqRadii;
        float4 _RP_ShadowParams;

        // Internal-ScreenSpaceShadows' getCascadeWeights_splitSpheres.
        fixed4 cascadeWeights(float3 wpos)
        {
            float3 c0 = wpos - _RP_SplitSpheres[0].xyz;
            float3 c1 = wpos - _RP_SplitSpheres[1].xyz;
            float3 c2 = wpos - _RP_SplitSpheres[2].xyz;
            float3 c3 = wpos - _RP_SplitSpheres[3].xyz;
            float4 d2 = float4(dot(c0, c0), dot(c1, c1), dot(c2, c2), dot(c3, c3));
            fixed4 w = float4(d2 < _RP_SplitSqRadii);
            w.yzw = saturate(w.yzw - w.xyz);
            return w;
        }

        // Internal-ScreenSpaceShadows' getShadowCoord, and getShadowCoord_SingleCascade (SHADOWS_SINGLE_CASCADE).
        float4 shadowCoord(float4 wpos, fixed4 w)
        {
            if (_RP_ShadowParams.y > 0.5)
                return float4(mul(_RP_WorldToShadow[0], wpos).xyz, 0);
            float3 s0 = mul(_RP_WorldToShadow[0], wpos).xyz;
            float3 s1 = mul(_RP_WorldToShadow[1], wpos).xyz;
            float3 s2 = mul(_RP_WorldToShadow[2], wpos).xyz;
            float3 s3 = mul(_RP_WorldToShadow[3], wpos).xyz;
            float4 c = float4(s0 * w[0] + s1 * w[1] + s2 * w[2] + s3 * w[3], 1);
            #if defined(UNITY_REVERSED_Z)
                float none = 1 - dot(w, float4(1, 1, 1, 1));
                c.z += none;
            #endif
            return c;
        }

        // The world position of the pixel from StdDepth (Unity: the ray to the far plane times Linear01Depth).
        float4 worldOf(float2 uv)
        {
            float d = tex2Dlod(_RP_StdDepth, float4(uv, 0, 0)).r;
            float3 vpos = rayOf(uv) * stdEyeDepth(d);
            return mul(_RP_CamToWorld, float4(vpos, 1));
        }

        // ---- CROP ----
        sampler2D _RP_CropColor;
        sampler2D_float _RP_CropDepth;
        sampler2D _RP_CropNormal;
        // The rect (screen units, OpenGL convention: x, y from the bottom left, w, h).
        float4 _RP_CropRect;
        ENDCG

        Pass
        {
            Name "SAVE"
            ZWrite On
            ColorMask 0
            CGPROGRAM
            #pragma vertex vertTri
            #pragma fragment frag
            #pragma target 3.5

            void frag(v2fTri i, out float depth : SV_Depth)
            {
                float d = tex2Dlod(_RP_ObliqueDepth, float4(i.uv, 0, 0)).r;
                // Nothing drawn here: the sky.
                if (d == _RP_StdZ.w)
                {
                    depth = _RP_StdZ.w;
                    return;
                }
                float2 ndc = i.uv * 2 - 1;
                float a = (ndc.x + _RP_ProjXY.z) / _RP_ProjXY.x;
                float b = (ndc.y + _RP_ProjXY.w) / _RP_ProjXY.y;
                float eye = _RP_OblRow2.w / (glZ(d) - _RP_OblRow2.x * a - _RP_OblRow2.y * b + _RP_OblRow2.z);
                depth = deviceOfGl(-_RP_StdZ.x + _RP_StdZ.y / max(eye, 1e-6));
            }
            ENDCG
        }

        Pass
        {
            Name "SHADOWMASK"
            CGPROGRAM
            #pragma vertex vertTri
            #pragma fragment frag
            #pragma target 3.5

            fixed4 frag(v2fTri i) : SV_Target
            {
                float4 wpos = worldOf(i.uv);
                fixed4 w = cascadeWeights(wpos.xyz);
                float4 coord = shadowCoord(wpos, w);
                half shadow = UnitySampleShadowmap_PCF5x5(coord, float3(0, 0, 0));
                shadow = lerp(_RP_ShadowParams.x, 1.0f, shadow);
                return shadow;
            }
            ENDCG
        }

        Pass
        {
            Name "SHADOWMASKHARD"
            CGPROGRAM
            #pragma vertex vertTri
            #pragma fragment frag
            #pragma target 3.5

            fixed4 frag(v2fTri i) : SV_Target
            {
                float4 wpos = worldOf(i.uv);
                fixed4 w = cascadeWeights(wpos.xyz);
                float4 coord = shadowCoord(wpos, w);
                fixed shadow = UNITY_SAMPLE_SHADOW(_ShadowMapTexture, coord);
                shadow = lerp(_RP_ShadowParams.x, 1.0, shadow);
                return shadow;
            }
            ENDCG
        }

        Pass
        {
            Name "CROP"
            CGPROGRAM
            #pragma vertex vertTri
            #pragma fragment frag
            #pragma target 3.5
            #pragma exclude_renderers nomrt

            void frag(v2fTri i,
                out half4 outColor : SV_Target0,
                out float outEye : SV_Target1,
                out half4 outNormal : SV_Target2,
                out float outDepth : SV_Target3)
            {
                float2 uv = _RP_CropRect.xy + i.uv * _RP_CropRect.zw;
                float4 at = float4(uv, 0, 0);
                outColor = half4(tex2Dlod(_RP_CropColor, at).rgb, 1);
                float d = tex2Dlod(_RP_CropDepth, at).r;
                outEye = d == _RP_StdZ.w ? _RP_StdZ.z : stdEyeDepth(d);
                outNormal = tex2Dlod(_RP_CropNormal, at);
                outDepth = d;
            }
            ENDCG
        }
    }
    Fallback Off
}
