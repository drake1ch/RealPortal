// Depth reprojection of a cached window view (Window Pipeline v2, Win.Resolve; ARCHITECTURE-v2 section 4.7, contract
// Docs\S2S3-CONTRACTS.md section 8.2). A stale window's last render is re-projected onto this frame's rect for this frame's
// camera (pose and TAA jitter), so the unchanged composite passes of PortalWindow.shader read it as if it were fresh.
//
// Per output pixel: the main camera's ray through it now, carried to the far side by the portal pair of now, is the
// segment from the exit plane to the cache's far plane, clipped to the cache's frustum. Its image in the cache is a
// line (the epipolar line), along which 1/depth is linear: it is marched in `steps` equal steps in cache screen space,
// comparing the ray's depth with the cache's linear depth, and the first crossing is refined by `refine` bisections
// (plus one secant step on the last bracket, which costs no tap). Then:
//   - a crossing where the ray is behind the surface by no more than it advanced in the last bracket (plus the
//     relative tolerance `thickness`), or where the surface is continuous across the bracket, is a hit: colour and
//     normal from there, alpha 1;
//   - a crossing far behind a nearer surface, across a jump in depth (the ray passed behind something the cache shows
//     in front), is disoccluded:
//     colour of the last point the ray was in front of the cache (the background next to the occluder's edge, so the
//     background stretches), alpha 0;
//   - no crossing: the ray reached the far plane in front of everything, i.e. sky, which depends on direction only and
//     is sampled exactly at the direction's image, alpha 1 (alpha 0 if that image is outside the cache: the ray left it).
// The output depth is the far-side linear eye depth along this frame's window camera axis (what _RP_ViewDepth means).
//
// Drawn with CommandBuffer.DrawMesh(full-screen triangle in clip space, identity, material, 0, pass 0) into three
// targets of the output's size: colour (as the cache, alpha kept), RFloat depth, ARGB2101010 normals.
Shader "Hidden/RealPortal/WindowResolve"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "RESOLVE"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma exclude_renderers nomrt
            #include "UnityCG.cginc"

            // Upper bounds of the loops (the counts themselves come in _RP_ResolveParams).
            #define RP_MAX_STEPS 32
            #define RP_MAX_REFINE 8

            Texture2D _RP_CacheColor;
            Texture2D _RP_CacheNormal;
            Texture2D<float> _RP_CacheDepth;
            // Inline sampler states (Unity reads the filter and wrap mode from the names): depth is compared texel by
            // texel (a filtered depth would invent surfaces between a near edge and the background behind it); colour
            // and normals are filtered as the composite filters a fresh view.
            SamplerState rp_point_clamp_sampler;
            SamplerState rp_linear_clamp_sampler;

            float _RP_HasCacheNormal;
            float4x4 _RP_CacheVP;          // far world -> cache clip, as rendered (OpenGL convention, jitter included)
            float4 _RP_CacheEye;           // cache camera position (far side); its depth = dot(p - eye, fwd) = clip w
            float4 _RP_CacheFwd;
            float4 _RP_CacheZ;             // ZNear, ZFar (stats), Near, Far (clip planes) of the cache
            float4x4 _RP_PairMatrix;       // this side -> far side now
            float4x4 _RP_MainCamToWorld;   // main camera cameraToWorldMatrix now
            float4 _RP_MainProj;           // main camera jittered projection now: m00, m11, m02, m12
            float4 _RP_ResolveRect;        // the output's screen rect (0..1): x, y, w, h
            float4 _RP_ResolveEye;         // M_now * eye (far side)
            float4 _RP_ResolveFwd;         // normalize(M_now * forward)
            float4 _RP_ExitPlane;          // far-side exit plane: n.xyz, d (dot(n, p) + d = 0)
            float4 _RP_ResolveParams;      // steps, refine, thickness (relative), sky depth (0.98 * cache far)

            struct v2fTriangle
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            // Full-screen triangle in clip space; uv in Unity's render-texture convention (as PostProcessing v2), so
            // uv (0, 0) is the output's bottom left as the composite samples it (_RP_TexRect = the output's rect).
            v2fTriangle vert(float4 vertex : POSITION)
            {
                v2fTriangle o;
                o.pos = float4(vertex.xy, 0, 1);
                o.uv = vertex.xy * 0.5 + 0.5;
                #if UNITY_UV_STARTS_AT_TOP
                    o.uv.y = 1 - o.uv.y;
                #endif
                return o;
            }

            // Keeps the part of [t0, t1] where a + b * t >= 0 (one plane of the cache frustum; a, b linear in t because
            // clip coordinates are linear in the ray parameter).
            void clipTo(float a, float b, inout float t0, inout float t1)
            {
                if (abs(b) < 1e-9)
                {
                    if (a < 0)
                        t1 = -1;
                    return;
                }
                float t = -a / b;
                if (b > 0)
                    t0 = max(t0, t);
                else
                    t1 = min(t1, t);
            }

            float cacheDepthAt(float2 uv)
            {
                return _RP_CacheDepth.SampleLevel(rp_point_clamp_sampler, uv, 0);
            }

            // Along the segment's image: u = 0 at its near end, 1 at its far end. Screen position is linear in u,
            // and so is 1 / depth (perspective-correct).
            float2 segUV(float2 p0, float2 p1, float u)
            {
                return lerp(p0, p1, u) * 0.5 + 0.5;
            }

            float segDepth(float iw0, float iw1, float u)
            {
                return 1.0 / max(lerp(iw0, iw1, u), 1e-9);
            }

            void frag(v2fTriangle i,
                out half4 outColor : SV_Target0,
                out float outDepth : SV_Target1,
                out half4 outNormal : SV_Target2)
            {
                float steps = clamp(round(_RP_ResolveParams.x), 1, RP_MAX_STEPS);
                float refine = clamp(round(_RP_ResolveParams.y), 0, RP_MAX_REFINE);
                float thickness = max(_RP_ResolveParams.z, 0);
                float cacheNear = max(_RP_CacheZ.z, 1e-4);
                float cacheFar = max(_RP_CacheZ.w, cacheNear * 2);
                float skyDepth = _RP_ResolveParams.w > 0 ? _RP_ResolveParams.w : 0.98 * cacheFar;

                // This pixel's ray of the main camera now (with this frame's jitter), carried to the far side.
                float2 screenUV = _RP_ResolveRect.xy + i.uv * _RP_ResolveRect.zw;
                float2 ndc = screenUV * 2 - 1;
                float3 viewDir = float3((ndc.x + _RP_MainProj.z) / _RP_MainProj.x, (ndc.y + _RP_MainProj.w) / _RP_MainProj.y, -1);
                float3 worldDir = mul((float3x3)_RP_MainCamToWorld, viewDir);
                float3 o = _RP_ResolveEye.xyz;
                float3 d = normalize(mul((float3x3)_RP_PairMatrix, worldDir));
                // The output depth of the point o + d * t.
                float depthPerT = dot(d, _RP_ResolveFwd.xyz);

                // The ray in the cache's clip space, C(t) = c0 + t * cd; cd is also the image of the direction alone
                // (where the sky in that direction is in the cache).
                float4 c0 = mul(_RP_CacheVP, float4(o, 1));
                float4 cd = mul(_RP_CacheVP, float4(d, 0));
                float2 skyUV = cd.xy / max(abs(cd.w), 1e-6) * 0.5 + 0.5;
                bool skyInside = cd.w > 1e-6 && all(skyUV >= 0) && all(skyUV <= 1);

                // The visible part: beyond the exit plane (what lies between the camera and the exit is not seen),
                // between the cache's near and far planes and inside its four sides.
                float t0 = 0, t1 = 1e30;
                float planeN = dot(_RP_ExitPlane.xyz, d);
                float planeD = dot(_RP_ExitPlane.xyz, o) + _RP_ExitPlane.w;
                if (abs(planeN) > 1e-6)
                {
                    float tp = -planeD / planeN;
                    if (tp > 0)
                        t0 = tp;
                }
                clipTo(c0.w - cacheNear, cd.w, t0, t1);
                clipTo(cacheFar - c0.w, -cd.w, t0, t1);
                clipTo(c0.w + c0.x, cd.w + cd.x, t0, t1);
                clipTo(c0.w - c0.x, cd.w - cd.x, t0, t1);
                clipTo(c0.w + c0.y, cd.w + cd.y, t0, t1);
                clipTo(c0.w - c0.y, cd.w - cd.y, t0, t1);
                bool segment = t1 > t0 && t1 < 1e20;

                // 0: no crossing (sky, or the ray left the cache), 1: hit, 2: disoccluded.
                int kind = 0;
                float2 uv = cd.w > 1e-6 ? saturate(skyUV) : float2(0.5, 0.5);
                float wAt = cacheFar;     // the cache depth of the point shown, on this ray
                float uShown = 1;
                float2 p0 = 0, p1 = 0;
                float iw0 = 1, iw1 = 1;
                if (segment)
                {
                    float4 h0 = c0 + t0 * cd, h1 = c0 + t1 * cd;
                    p0 = h0.xy / h0.w;
                    p1 = h1.xy / h1.w;
                    iw0 = 1.0 / h0.w;
                    iw1 = 1.0 / h1.w;

                    // The ray is behind the cache's surface at u when the surface there is not sky and nearer than the
                    // ray: the first such u after one where it was in front is a crossing.
                    float uLo = 0, uHi = 0, sLo, sHi = 0;
                    float s = cacheDepthAt(segUV(p0, p1, 0));
                    sLo = s;
                    bool found = s < skyDepth && h0.w >= s;
                    if (!found)
                    {
                        [loop]
                        for (int k = 1; k <= RP_MAX_STEPS; k++)
                        {
                            if (k > steps)
                                break;
                            float u = k / steps;
                            s = cacheDepthAt(segUV(p0, p1, u));
                            if (s < skyDepth && segDepth(iw0, iw1, u) >= s)
                            {
                                found = true;
                                uHi = u;
                                sHi = s;
                                break;
                            }
                            uLo = u;
                            sLo = s;
                        }
                    }
                    else
                        sHi = s;

                    if (found)
                    {
                        [loop]
                        for (int j = 0; j < RP_MAX_REFINE; j++)
                        {
                            if (j >= refine)
                                break;
                            float m = 0.5 * (uLo + uHi);
                            float sm = cacheDepthAt(segUV(p0, p1, m));
                            if (sm < skyDepth && segDepth(iw0, iw1, m) >= sm)
                            {
                                uHi = m;
                                sHi = sm;
                            }
                            else
                            {
                                uLo = m;
                                sLo = sm;
                            }
                        }
                        // A hit when the ray is behind the surface by no more than it advanced within the last bracket
                        // (plus the tolerance), or when the surface is continuous across the bracket (its depth texels
                        // are stairs, so a sloped surface may be crossed at a riser). Passing behind a near edge leaves
                        // the ray far behind it, across a jump in depth: disoccluded.
                        float rLo = segDepth(iw0, iw1, uLo), rHi = segDepth(iw0, iw1, uHi);
                        bool continuous = uHi > uLo && sLo < skyDepth && abs(sHi - sLo) <= 4 * thickness * sHi;
                        if (rHi - sHi <= thickness * sHi + (rHi - rLo) || continuous)
                        {
                            // Hit: the surface lies within the last bracket. One secant step on (surface - ray) inside it.
                            kind = 1;
                            float fLo = min(sLo, skyDepth) - rLo, fHi = sHi - rHi;
                            float span = fLo - fHi;
                            float f = span > 1e-6 ? saturate(fLo / span) : 1;
                            uShown = lerp(uLo, uHi, f);
                            wAt = segDepth(iw0, iw1, uShown);
                        }
                        else
                        {
                            // Passed behind a nearer surface: what the ray meets is not in the cache. The background next
                            // to that surface's edge, at its own depth (the sky's exact colour if that is sky).
                            kind = 2;
                            uShown = uLo;
                            wAt = sLo;
                            if (sLo >= skyDepth && skyInside)
                                uShown = -1;
                        }
                    }
                    else if (!skyInside)
                    {
                        // In front of everything up to where the ray leaves the cache's sides: unknown beyond, stretched.
                        kind = 2;
                        uShown = 1;
                        wAt = h1.w;
                    }
                    if (uShown >= 0 && kind != 0)
                        uv = segUV(p0, p1, uShown);
                }
                else
                    kind = 2;

                // Depth along this frame's window camera axis: the point of this ray at cache depth wAt (w is linear in
                // t; with w constant along the ray the screen position is linear in t instead). Sky stays at the far plane.
                float depth = cacheFar;
                if (wAt < skyDepth)
                {
                    float t = abs(cd.w) > 1e-6 ? (wAt - c0.w) / cd.w : lerp(t0, t1, saturate(uShown));
                    depth = max(t, 0) * depthPerT;
                }

                half4 color = _RP_CacheColor.SampleLevel(rp_linear_clamp_sampler, uv, 0);
                outColor = half4(color.rgb, kind == 2 ? 0 : 1);
                outDepth = depth;
                // No normals in the cache: the encoding of a zero vector, which the DEFERRED pass ignores (it keeps the
                // window's face normal) even if this target is bound as the view's normals.
                outNormal = half4(0.5, 0.5, 0.5, 0);
                if (_RP_HasCacheNormal > 0.5)
                    outNormal = half4(_RP_CacheNormal.SampleLevel(rp_linear_clamp_sampler, uv, 0));
            }
            ENDCG
        }
    }
    Fallback Off
}
