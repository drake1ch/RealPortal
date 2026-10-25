// Screen-space portal window: samples the view rendered behind the partner portal at this pixel's screen
// position. _RP_Viewport maps the current camera's viewport to full-screen coordinates (0,0,1,1 for the main
// camera, the scissor rect while a window camera renders a nested window).
//
// The game's camera renders deferred, so the window goes into the G-buffer as a black, unlit surface whose
// colour is emission, with the normals seen through the portal (turned into this side's frame). The window
// cameras use the forward pass for nested windows.
//
// Helper passes, driven by PortalRenderer through command buffers:
//   2 DEPTHPATCH  draws the window into a copy of the game camera's depth texture with the depth of what is
//                 seen through it, so fog, SSAO, DOF, sun shafts and TAA treat the view as real scenery;
//   3 LINEARDEPTH a window camera's depth texture as linear eye depth (what DEPTHPATCH reads);
//   4 COPYDEPTH   a camera's depth texture as is (the base of the patched copy);
//   5 MOTION      the game camera's motion vectors for the view seen through the window (TAA, motion blur);
//   6 MOTIONSCREEN the whole screen's motion vectors on the frame the camera is carried through a portal;
//   7 SKYDEPTH    where the view through the window shows sky (the game camera's second sun shafts pass, from
//                 the sun seen through the window);
//   8 MASK        1 where a window is what the camera sees;
//   9 MIX         an image with the windows' pixels taken from another one (by that mask);
//  10 SOLID       1 wherever an object shows (the player's mask on the frame the camera jumps through a portal);
//  11 RESTORE     the window's pixels as they were before the game camera's opaque image effects (its SSAO and
//                 fog, which the window camera already applied to the view).
//
// Window Pipeline v2 (S3, Docs\S2S3-CONTRACTS.md section 8.1; append-only, the indices are fixed and the renderer finds them
// by name, so a bundle without them reads -1 and the old passes above are used):
//  12 AUXBASE     full screen, once per frame, into the five aux targets (MRT): the camera's raw depth twice (the fog
//                 copy and the view depth), "near" for the sky depth, 0 for the mask and the id (Win.CompositeMRT);
//  13 AUXMRT      per window the camera may see, into the same five targets: what DEPTHPATCH (fog copy and view
//                 depth), SKYDEPTH and MASK wrote with one draw each, plus the window's visibility id (Win.Occlusion);
//  14 WINDOWID    the id alone (occlusion without the MRT composite);
//  15 RESTOREVIEW what RESTORE puts back, shaded again from the view instead of read from a full-screen copy taken
//                 before the opaque image effects (Win.RestoreFromWindow); inside a level-0 window render the nested
//                 table's windows and the entry's deeper level, over that view's own effects (Win.NestedLive).
//
// Inside a window camera's render the other windows show their fallback: the renderer sets the globals
// _RP_InWindowRender = 1 and _RP_RenderingWindowId (the one window that shows its deeper level there, 0: none)
// once per render, and each window compares that with its own _RP_WindowId. With the globals unset (0, a plugin
// from before them) _Active alone decides, as it always did.
//
// Win.VanillaLook (Docs\PARITY-PLAN.md P2, P7; global _RP_FallbackMode = 1): a window whose activeNow() is 0 (not rendered
// this frame: out of reach, under the pixel floor, or another window inside a window render) is discarded by FORWARD and
// DEFERRED, so the portal shows what the game shows there, an empty ring. Unset (0, a plugin from before it, or the switch
// off) it is the procedural fallback disc (_FallbackColor), as it always was. The helper passes skip such a window by
// _Active already. P43: while a reflection probe renders (global _RP_ProbeRender = 1, set by the game side under the same
// switch) activeNow() is 0 for every window, so probes see the empty ring too, never a window's crop of the screen.
//
// Win.NestedLive (Docs\PARITY-PLAN.md P7): inside a level-0 window render the renderer sets a table of up to four other
// windows it rendered at level 1 for that view (global _RP_NestedIds, 0 = an empty entry; _RP_NestedTex0-3 their views,
// _RP_NestedRect0-3 the screen rects those cover in the frame _RP_Viewport maps to). A window in the table counts as active
// there and shows its entry's view (nestedSlot, nestedView), not its material's texture; every other window keeps the test
// above. Unset (0, a plugin from before it, or the switch off) nothing changes.
//
// Win.TransparentDepth (Docs\PARITY-PLAN.md P42): mode 3 of the fog copy (_RP_PatchNear in DEPTHPATCH, _RP_FogPatchMode in
// AUXMRT) writes the depth seen through the window, never nearer than the window's own surface, or sky for a window without
// a view depth. The renderer then binds that copy for this side's transparent objects too. A bundle from before it reads 3
// as sky, but the renderer only asks for it when _RP_Parity is there.
//
// Win.PointDepth (FARWORLD-V3 section 7 V0; global _RP_PointDepth = 1): DEPTHPATCH, MOTION, SKYDEPTH and AUXMRT read the
// view depth of the one texel a pixel falls in (viewDepthAt), never a blend of the texels around it: across a silhouette
// a blend is a depth nothing in the view has, and the motion vector made from it is wrong there. Unset (0, a plugin
// from before it, or the switch off) they read it as they always did.
Shader "RealPortal/Window"
{
    Properties
    {
        _MainTex ("View", 2D) = "black" {}
        _Active ("Active", Float) = 0
        _RP_TexRect ("Screen rect of the view", Vector) = (0, 0, 1, 1)
        _RP_ViewDepth ("Linear depth of the view", 2D) = "black" {}
        _RP_DepthScale ("View depth to this side's metres (the view through a smaller exit is scaled)", Float) = 1
        _RP_ViewNormal ("G-buffer normals of the view", 2D) = "black" {}
        _RP_HasDepth ("Has depth", Float) = 0
        _RP_HasNormal ("Has normals", Float) = 0
        _FallbackColor ("Fallback", Color) = (0.25, 0.45, 0.8, 1)
        // Per material. The two globals it is compared with must stay out of this block (a material value would
        // hide the global one).
        _RP_WindowId ("Window id", Float) = 0
        // Per material (S3). The renderer also takes their presence as the sign of a bundle with passes 12-15.
        _RP_VisId ("Visibility id (0: not counted)", Float) = 0
        _RP_Resolved ("The view is a resolve output (alpha 0: disoccluded)", Float) = 0
        // Per material, always 1. The renderer takes its presence as the sign of a bundle with the nested table, the fog
        // copy's mode 3 and the probe discard (Docs\PARITY-PLAN.md P7, P42, P43).
        _RP_Parity ("Parity passes", Float) = 1
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _MainTex;
    sampler2D _RP_ViewDepth;
    sampler2D _RP_ViewNormal;
    float _Active;
    float _RP_HasDepth;
    float _RP_HasNormal;
    float4 _FallbackColor;
    float4 _RP_Viewport;
    float4 _RP_TexRect;
    float4x4 _RP_NormalRot;
    float _RP_PatchNear;
    float _RP_DepthScale;
    float _RP_WindowId;
    float _RP_InWindowRender;     // global
    float _RP_RenderingWindowId;  // global
    float _RP_PointDepth;         // global (Win.PointDepth)
    float _RP_FallbackMode;       // global (Win.VanillaLook)
    float _RP_ProbeRender;        // global (Win.VanillaLook P43: a reflection probe is rendering)
    float4 _RP_ViewDepth_TexelSize;
    UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
    // Win.NestedLive (globals): the windows the current window render shows at level 1 (0: an empty entry), their views,
    // and the screen rects those views cover (in the frame _RP_Viewport maps to).
    float4 _RP_NestedIds;
    sampler2D _RP_NestedTex0;
    sampler2D _RP_NestedTex1;
    sampler2D _RP_NestedTex2;
    sampler2D _RP_NestedTex3;
    float4 _RP_NestedRect0, _RP_NestedRect1, _RP_NestedRect2, _RP_NestedRect3;
    float _RP_Parity;             // per material, 1

    // This window's entry in the nested table of the current window render, or -1 (none, outside a window render, in a
    // reflection probe's render). One return: FXC overflows its stack on this as a chain of early returns.
    int nestedSlot()
    {
        float4 d = abs(_RP_NestedIds - _RP_WindowId);
        int slot = d.x < 0.5 ? 0 : d.y < 0.5 ? 1 : d.z < 0.5 ? 2 : d.w < 0.5 ? 3 : -1;
        return _RP_InWindowRender < 0.5 || _RP_ProbeRender > 0.5 || _RP_Parity < 0.5 ? -1 : slot;
    }

    // _Active as the current render sees it: inside a window render every window but the one named by
    // _RP_RenderingWindowId shows its fallback, unless it is in that render's nested table; in a reflection probe's
    // render none shows anything (P43).
    float activeNow()
    {
        float active = _Active;
        if (_RP_InWindowRender > 0.5 && abs(_RP_WindowId - _RP_RenderingWindowId) > 0.5)
            active = nestedSlot() >= 0 ? 1 : 0;
        return _RP_ProbeRender > 0.5 ? 0 : active;
    }

    struct v2f
    {
        float4 pos : SV_POSITION;
        float4 screen : TEXCOORD0;
        float3 local : TEXCOORD1;
        float3 normal : TEXCOORD2;
        float3 world : TEXCOORD3;
    };

    v2f vert(appdata_base v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.screen = ComputeScreenPos(o.pos);
        o.local = v.vertex.xyz;
        o.normal = UnityObjectToWorldNormal(float3(0, 0, 1));
        o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
        return o;
    }

    float _RP_Reproject;
    float4x4 _RP_ReprojVP;

    // Where this pixel falls in the view texture (which covers only the window's screen rect).
    float2 viewUV(v2f i)
    {
        float4 tr = _RP_TexRect.z > 0 ? _RP_TexRect : float4(0, 0, 1, 1);
        if (_RP_Reproject > 0.5)
        {
            // An earlier view: where this point of the opening was on screen when it was rendered.
            float4 c = mul(_RP_ReprojVP, float4(i.world, 1));
            float2 was = c.xy / max(c.w, 1e-5) * 0.5 + 0.5;
            return (was - tr.xy) / tr.zw;
        }
        float2 uv = i.screen.xy / i.screen.w;
        float4 vp = _RP_Viewport;
        if (vp.z <= 0) vp = float4(0, 0, 1, 1);
        uv = vp.xy + uv * vp.zw;
        return (uv - tr.xy) / tr.zw;
    }

    // The view's linear depth at `uv` (viewUV). Win.PointDepth: the value of the texel `uv` falls in, fetched at that
    // texel's centre (exact on the Point-filtered depth textures the renderer then makes, and on a bilinear one too: the
    // view textures have no mips, so the centre reads that texel alone).
    float viewDepthAt(float2 uv)
    {
        if (_RP_PointDepth > 0.5 && _RP_ViewDepth_TexelSize.z > 0)
            uv = (floor(uv * _RP_ViewDepth_TexelSize.zw) + 0.5) * _RP_ViewDepth_TexelSize.xy;
        return tex2D(_RP_ViewDepth, uv).r;
    }

    // Win.NestedLive: the level-1 view of nested table entry `slot` at this pixel (never reprojected: rendered this frame
    // for this render). Fetched at mip 0, as the view textures have no mips, so the fetch may sit in a branch.
    float4 nestedView(v2f i, int slot)
    {
        float4 tr = slot == 0 ? _RP_NestedRect0 : slot == 1 ? _RP_NestedRect1 : slot == 2 ? _RP_NestedRect2 : _RP_NestedRect3;
        float2 uv = i.screen.xy / i.screen.w;
        float4 vp = _RP_Viewport;
        if (vp.z <= 0) vp = float4(0, 0, 1, 1);
        uv = (vp.xy + uv * vp.zw - tr.xy) / max(tr.zw, 1e-6);
        float4 at = float4(uv, 0, 0);
        float4 c;
        if (slot == 0)
            c = tex2Dlod(_RP_NestedTex0, at);
        else if (slot == 1)
            c = tex2Dlod(_RP_NestedTex1, at);
        else if (slot == 2)
            c = tex2Dlod(_RP_NestedTex2, at);
        else
            c = tex2Dlod(_RP_NestedTex3, at);
        return c;
    }

    float4 shade(v2f i)
    {
        float active = saturate(activeNow());
        // Win.VanillaLook: nothing of its own to show here, nothing drawn (the game's empty ring).
        if (_RP_FallbackMode > 0.5 && active < 0.5)
            discard;
        float4 view = tex2D(_MainTex, viewUV(i));
        // Win.NestedLive: a window of this render's nested table shows its level-1 view.
        int slot = active > 0.5 ? nestedSlot() : -1;
        if (slot >= 0)
            view = nestedView(i, slot);
        float r = length(i.local.xy);
        float a = atan2(i.local.y, i.local.x);
        float swirl = 0.5 + 0.5 * sin(r * 9.0 - _Time.y * 2.5 + a * 3.0);
        float4 fallback = _FallbackColor * (0.55 + 0.45 * swirl);
        float4 c = lerp(fallback, view, active);
        c.a = 1;
        return c;
    }

    // ---- Window Pipeline v2 (S3): helpers of passes 12-15 ----------------------------------------------------------
    float _RP_VisId;          // per material: the window's visibility id (WindowState.VisId), 0 = not counted
    float _RP_Resolved;       // per material: _MainTex/_RP_ViewDepth are a WindowResolve output
    float _RP_FogPatchMode;   // global: what _RP_PatchNear is for the fog copy (1 right at the camera, 2 as sky, 3 the view depth)

    // Raw depth (the camera's own encoding) right at the near plane, and of the sky (far plane).
    float rawNearDepth()
    {
        #if defined(UNITY_REVERSED_Z)
            return 1;
        #else
            return 0;
        #endif
    }

    float rawSkyDepth()
    {
        #if defined(UNITY_REVERSED_Z)
            return 0;
        #else
            return 1;
        #endif
    }

    // Inverse of LinearEyeDepth with the camera's own depth parameters (what DEPTHPATCH writes).
    float rawOfEyeDepth(float z)
    {
        return (1.0 / z - _ZBufferParams.w) / _ZBufferParams.z;
    }

    // The window is what the camera sees at this pixel (the test of DEPTHPATCH, SKYDEPTH, MASK and RESTORE: something
    // in front of it keeps its own depth). Also gives the scene's raw depth there.
    bool windowSeen(v2f i, out float rawScene)
    {
        rawScene = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.screen.xy / i.screen.w);
        float scene = LinearEyeDepth(rawScene);
        float self = LinearEyeDepth(i.pos.z);
        return scene >= self - 0.02 * self - 0.01;
    }

    // Full-screen triangle for AUXBASE (CommandBuffer.DrawMesh with clip-space vertices and an identity matrix). The
    // uv follows Unity's render-texture convention (as PostProcessing v2 does), so a target pixel reads the same pixel
    // of the camera's depth texture, as a Blit would.
    struct v2fTriangle
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    v2fTriangle vertTriangle(float4 vertex : POSITION)
    {
        v2fTriangle o;
        o.pos = float4(vertex.xy, 0, 1);
        o.uv = vertex.xy * 0.5 + 0.5;
        #if UNITY_UV_STARTS_AT_TOP
            o.uv.y = 1 - o.uv.y;
        #endif
        return o;
    }
    ENDCG

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+10" "IgnoreProjector"="True" }
        Cull Off
        ZWrite On
        ZTest LEqual

        Pass
        {
            Name "FORWARD"
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // nestedView's tex2Dlod
            #pragma target 3.0

            float4 frag(v2f i) : SV_Target
            {
                return shade(i);
            }
            ENDCG
        }

        Pass
        {
            Name "DEFERRED"
            Tags { "LightMode"="Deferred" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragDeferred
            #pragma exclude_renderers nomrt
            // nestedView's tex2Dlod
            #pragma target 3.0
            #pragma multi_compile ___ UNITY_HDR_ON

            void fragDeferred(v2f i,
                out half4 outDiffuse : SV_Target0,
                out half4 outSpecular : SV_Target1,
                out half4 outNormal : SV_Target2,
                out half4 outEmission : SV_Target3)
            {
                float4 c = shade(i);
                // Toward the camera on either face (seen from behind, the front normal would read as fully
                // occluded to the game's ambient occlusion).
                float3 n = normalize(i.normal);
                if (dot(n, _WorldSpaceCameraPos - i.world) < 0)
                    n = -n;
                // (A nested table entry's normals are not this material's: it keeps the one toward the camera.)
                if (_RP_HasNormal > 0.5 && activeNow() > 0.5 && nestedSlot() < 0)
                {
                    float3 seen = tex2D(_RP_ViewNormal, viewUV(i)).rgb * 2 - 1;
                    if (dot(seen, seen) > 0.01)
                        n = normalize(mul((float3x3)_RP_NormalRot, seen));
                }
                outDiffuse = half4(0, 0, 0, 1);
                outSpecular = half4(0, 0, 0, 0);
                outNormal = half4(n * 0.5 + 0.5, 1);
                #ifndef UNITY_HDR_ON
                    c.rgb = exp2(-c.rgb);
                #endif
                outEmission = half4(c.rgb, 1);
            }
            ENDCG
        }

        Pass
        {
            Name "DEPTHPATCH"
            // Only run on request (command buffers); untagged passes would be drawn by forward rendering.
            Tags { "LightMode"="RealPortalUtility" }
            ZWrite Off
            ZTest Always
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float frag(v2f i) : SV_Target
            {
                // The effects copy (window at the far plane or right at the camera) needs no view depth.
                if (_Active < 0.5 || (_RP_PatchNear < 0.5 && _RP_HasDepth < 0.5))
                    discard;
                // Only where the window is what the camera sees: something in front of it keeps its own depth.
                float scene = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.screen.xy / i.screen.w));
                float self = LinearEyeDepth(i.pos.z);
                if (scene < self - 0.02 * self - 0.01)
                    discard;
                float seen = max(viewDepthAt(viewUV(i)) * _RP_DepthScale, 1e-3);
                // Win.TransparentDepth (3): the copy the game's opaque effects and this side's transparent objects read gets
                // the depth seen through the window, never nearer than the window's own surface (RESTORE's test still finds
                // the window there), or sky for a window without a view depth.
                if (_RP_PatchNear > 2.5)
                    return _RP_HasDepth > 0.5 ? rawOfEyeDepth(max(seen, self)) : rawSkyDepth();
                // The copy the game's fog and SSAO read: the window as sky (both leave it alone; the window camera
                // fogs and shades its own view), or, when the fog does not skip the sky, right at the camera.
                if (_RP_PatchNear > 1.5)
                {
                    #if defined(UNITY_REVERSED_Z)
                        return 0;
                    #else
                        return 1;
                    #endif
                }
                float z = _RP_PatchNear > 0.5 ? _ProjectionParams.y : seen;
                // Inverse of LinearEyeDepth with the game camera's own depth parameters.
                return (1.0 / z - _ZBufferParams.w) / _ZBufferParams.z;
            }
            ENDCG
        }

        Pass
        {
            Name "LINEARDEPTH"
            // Only run on request (command buffers); untagged passes would be drawn by forward rendering.
            Tags { "LightMode"="RealPortalUtility" }
            ZWrite Off
            ZTest Always
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag

            float frag(v2f_img i) : SV_Target
            {
                return LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv));
            }
            ENDCG
        }

        Pass
        {
            Name "COPYDEPTH"
            // Only run on request (command buffers); untagged passes would be drawn by forward rendering.
            Tags { "LightMode"="RealPortalUtility" }
            ZWrite Off
            ZTest Always
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag

            float frag(v2f_img i) : SV_Target
            {
                return SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
            }
            ENDCG
        }

        Pass
        {
            Name "MOTION"
            // Only run on request (command buffers); untagged passes would be drawn by forward rendering.
            Tags { "LightMode"="RealPortalUtility" }
            ZWrite Off
            ZTest Always
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4x4 _RP_CurVP, _RP_PrevVP, _RP_CamToWorld;
            float4 _RP_Proj;   // non-jittered projection: m00, m11, m02, m12

            float2 frag(v2f i) : SV_Target
            {
                if (_Active < 0.5 || _RP_HasDepth < 0.5)
                    discard;
                float2 uv = i.screen.xy / i.screen.w;
                float scene = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
                float self = LinearEyeDepth(i.pos.z);
                if (scene < self - 0.02 * self - 0.01)
                    discard;
                // What is seen here lies at the view depth z along this pixel's ray: in the game camera's own view
                // space that is the same point the window camera sees (the two views differ by the portal pair).
                // Its motion is then the game camera's own motion, as for any static scenery.
                float z = max(viewDepthAt(viewUV(i)) * _RP_DepthScale, 1e-3);
                float2 ndc = uv * 2 - 1;
                float3 view = float3((ndc.x + _RP_Proj.z) * z / _RP_Proj.x, (ndc.y + _RP_Proj.w) * z / _RP_Proj.y, -z);
                float4 world = mul(_RP_CamToWorld, float4(view, 1));
                float4 now = mul(_RP_CurVP, world), before = mul(_RP_PrevVP, world);
                return (now.xy / now.w - before.xy / before.w) * 0.5;
            }
            ENDCG
        }

        Pass
        {
            Name "MOTIONSCREEN"
            // Only run on request (command buffers); untagged passes would be drawn by forward rendering.
            Tags { "LightMode"="RealPortalUtility" }
            ZWrite Off
            ZTest Always
            Cull Off
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag

            float4x4 _RP_CurVP, _RP_PrevVP, _RP_CamToWorld;
            float4 _RP_Proj;
            sampler2D _RP_KeepMotion;

            // The frame the camera is carried through a portal: the whole screen's motion as if it had not jumped
            // (_RP_PrevVP is last frame's view carried through the portal), so TAA keeps its history. Where
            // _RP_KeepMotion is set (the player, carried along with the camera), the game's own vectors stay.
            float2 frag(v2f_img i) : SV_Target
            {
                if (tex2D(_RP_KeepMotion, i.uv).r > 0.5)
                    discard;
                float z = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv));
                float2 ndc = i.uv * 2 - 1;
                float3 view = float3((ndc.x + _RP_Proj.z) * z / _RP_Proj.x, (ndc.y + _RP_Proj.w) * z / _RP_Proj.y, -z);
                float4 world = mul(_RP_CamToWorld, float4(view, 1));
                float4 now = mul(_RP_CurVP, world), before = mul(_RP_PrevVP, world);
                return (now.xy / now.w - before.xy / before.w) * 0.5;
            }
            ENDCG
        }

        Pass
        {
            Name "SKYDEPTH"
            // Only run on request (command buffers); untagged passes would be drawn by forward rendering.
            Tags { "LightMode"="RealPortalUtility" }
            ZWrite Off
            ZTest Always
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            // Drawn into a target cleared to "near": the window's pixels where its view shows sky become sky,
            // for the sun shafts of the sun seen through the window.
            float frag(v2f i) : SV_Target
            {
                if (_Active < 0.5 || _RP_HasDepth < 0.5)
                    discard;
                float2 uv = i.screen.xy / i.screen.w;
                float scene = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
                float self = LinearEyeDepth(i.pos.z);
                if (scene < self - 0.02 * self - 0.01)
                    discard;
                float z = viewDepthAt(viewUV(i));
                if (z < _ProjectionParams.z * 0.98)
                    discard;
                #if defined(UNITY_REVERSED_Z)
                    return 0;
                #else
                    return 1;
                #endif
            }
            ENDCG
        }

        Pass
        {
            Name "MASK"
            // Only run on request (command buffers); untagged passes would be drawn by forward rendering.
            Tags { "LightMode"="RealPortalUtility" }
            ZWrite Off
            ZTest Always
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            // 1 where the window is what the camera sees (into a target cleared to 0).
            float frag(v2f i) : SV_Target
            {
                if (_Active < 0.5)
                    discard;
                float2 uv = i.screen.xy / i.screen.w;
                float scene = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
                float self = LinearEyeDepth(i.pos.z);
                if (scene < self - 0.02 * self - 0.01)
                    discard;
                return 1;
            }
            ENDCG
        }

        Pass
        {
            Name "MIX"
            // Only run on request (Graphics.Blit); untagged passes would be drawn by forward rendering.
            Tags { "LightMode"="RealPortalUtility" }
            ZWrite Off
            ZTest Always
            Cull Off
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag

            sampler2D _RP_Inside;
            sampler2D _RP_WindowMask;

            // The image with the windows' pixels taken from _RP_Inside (the sun shafts of the sun seen through them).
            float4 frag(v2f_img i) : SV_Target
            {
                return lerp(tex2D(_MainTex, i.uv), tex2D(_RP_Inside, i.uv), tex2D(_RP_WindowMask, i.uv).r);
            }
            ENDCG
        }

        Pass
        {
            Name "SOLID"
            // Only run on request (command buffers); untagged passes would be drawn by forward rendering.
            Tags { "LightMode"="RealPortalUtility" }
            ZWrite Off
            ZTest Always
            Cull Off
            CGPROGRAM
            #pragma vertex vertSolid
            #pragma fragment frag

            float4 vertSolid(float4 vertex : POSITION) : SV_POSITION
            {
                return UnityObjectToClipPos(vertex);
            }

            // 1 wherever the drawn object shows (any renderer, skinned too).
            float frag() : SV_Target
            {
                return 1;
            }
            ENDCG
        }

        Pass
        {
            Name "RESTORE"
            // Only run on request (command buffers); untagged passes would be drawn by forward rendering.
            Tags { "LightMode"="RealPortalUtility" }
            ZWrite Off
            ZTest Always
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            sampler2D _RP_BeforeEffects;

            float4 frag(v2f i) : SV_Target
            {
                if (_Active < 0.5)
                    discard;
                float2 uv = i.screen.xy / i.screen.w;
                float scene = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
                float self = LinearEyeDepth(i.pos.z);
                if (scene < self - 0.02 * self - 0.01)
                    discard;
                return tex2D(_RP_BeforeEffects, uv);
            }
            ENDCG
        }

        Pass
        {
            Name "AUXBASE"
            // Only run on request (command buffers); untagged passes would be drawn by forward rendering.
            Tags { "LightMode"="RealPortalUtility" }
            ZWrite Off
            ZTest Always
            Cull Off
            CGPROGRAM
            #pragma vertex vertTriangle
            #pragma fragment frag
            #pragma target 3.5
            #pragma exclude_renderers nomrt

            // The five aux targets before any window is drawn into them: what the two COPYDEPTH blits, the sky depth's
            // clear to "near" and the mask's clear to 0 gave, plus "no window" in the id target.
            void frag(v2fTriangle i,
                out float outFog : SV_Target0,
                out float outView : SV_Target1,
                out float outSky : SV_Target2,
                out float outMask : SV_Target3,
                out uint outId : SV_Target4)
            {
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                outFog = raw;
                outView = raw;
                outSky = rawNearDepth();
                outMask = 0;
                outId = 0;
            }
            ENDCG
        }

        Pass
        {
            Name "AUXMRT"
            // Only run on request (command buffers); untagged passes would be drawn by forward rendering.
            Tags { "LightMode"="RealPortalUtility" }
            ZWrite Off
            ZTest Always
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma exclude_renderers nomrt

            // One draw per window instead of DEPTHPATCH (fog copy), DEPTHPATCH (view depth), SKYDEPTH and MASK, each
            // target getting exactly what that pass wrote where the window is seen. Where a pass would have skipped the
            // window (not active, no view depth, no sky) its target gets the AUXBASE value again, which is what was there
            // unless two windows overlap on screen: the renderer draws the windows without a view first for that reason.
            // T4 is the window's visibility id, with bit 15 where a resolved view marks the pixel disoccluded.
            void frag(v2f i,
                out float outFog : SV_Target0,
                out float outView : SV_Target1,
                out float outSky : SV_Target2,
                out float outMask : SV_Target3,
                out uint outId : SV_Target4)
            {
                float rawScene;
                if (!windowSeen(i, rawScene))
                    discard;
                float2 uv = viewUV(i);
                float viewZ = viewDepthAt(uv);
                float viewAlpha = tex2D(_MainTex, uv).a;
                bool active = _Active > 0.5;
                bool hasDepth = active && _RP_HasDepth > 0.5;
                float patched = rawOfEyeDepth(max(viewZ * _RP_DepthScale, 1e-3));

                // Fog copy (DEPTHPATCH with _RP_PatchNear = _RP_FogPatchMode): the window as sky, or right at the camera, or
                // (Win.TransparentDepth, 3) at the depth seen through it but never nearer than its surface, sky without one.
                outFog = rawScene;
                if (active)
                {
                    if (_RP_FogPatchMode > 2.5)
                        outFog = hasDepth ? rawOfEyeDepth(max(max(viewZ * _RP_DepthScale, 1e-3), LinearEyeDepth(i.pos.z))) : rawSkyDepth();
                    else if (_RP_FogPatchMode > 1.5)
                        outFog = rawSkyDepth();
                    else if (_RP_FogPatchMode > 0.5)
                        outFog = rawOfEyeDepth(_ProjectionParams.y);
                    else if (hasDepth)
                        outFog = patched;
                }
                // View depth (DEPTHPATCH with _RP_PatchNear = 0): the depth seen through the window.
                outView = hasDepth ? patched : rawScene;
                // Sky depth (SKYDEPTH into a target cleared to near): sky where the view shows sky.
                outSky = hasDepth && viewZ >= _ProjectionParams.z * 0.98 ? rawSkyDepth() : rawNearDepth();
                // Mask (MASK into a target cleared to 0).
                outMask = active ? 1 : 0;
                uint id = (uint)(_RP_VisId + 0.5) & 0xFF;
                if (active && _RP_Resolved > 0.5 && viewAlpha < 0.5)
                    id |= 0x8000;
                outId = id;
            }
            ENDCG
        }

        Pass
        {
            Name "WINDOWID"
            // Only run on request (command buffers); untagged passes would be drawn by forward rendering.
            Tags { "LightMode"="RealPortalUtility" }
            ZWrite Off
            ZTest Always
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5

            // The id alone (AUXMRT's T4), into a target cleared to 0: occlusion without the MRT composite.
            uint frag(v2f i) : SV_Target
            {
                float rawScene;
                if (!windowSeen(i, rawScene))
                    discard;
                float viewAlpha = tex2D(_MainTex, viewUV(i)).a;
                uint id = (uint)(_RP_VisId + 0.5) & 0xFF;
                if (_Active > 0.5 && _RP_Resolved > 0.5 && viewAlpha < 0.5)
                    id |= 0x8000;
                return id;
            }
            ENDCG
        }

        Pass
        {
            Name "RESTOREVIEW"
            // Only run on request (command buffers); untagged passes would be drawn by forward rendering.
            Tags { "LightMode"="RealPortalUtility" }
            ZWrite Off
            ZTest Always
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // nestedView's tex2Dlod (shade)
            #pragma target 3.0

            // RESTORE without the full-screen copy taken before the opaque image effects: the window's colour shaded
            // again exactly as DEFERRED shades it. Undecoded: on an LDR camera the lighting pass has already decoded the
            // exp2 emission by now, so the target holds the plain colour there too. Active as the current render sees it
            // (activeNow): on the game camera that is _Active; inside a level-0 window render (Win.NestedLive) a window of
            // its nested table passes too, and shade() gives its level-1 view, put back over that view's own fog and AO.
            float4 frag(v2f i) : SV_Target
            {
                if (activeNow() < 0.5)
                    discard;
                float rawScene;
                if (!windowSeen(i, rawScene))
                    discard;
                return shade(i);
            }
            ENDCG
        }
    }
    Fallback Off
}
