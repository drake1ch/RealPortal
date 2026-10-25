using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace RealPortal
{
    // Win.Virtual (FARWORLD-V3 §4.4 Phase A, §4.5 L0, §4.6; the V1 spike): one window drawn without a camera. One command
    // buffer, recorded at the window's turn in the game camera's MainPreCull (its far side set up by PortalRenderer
    // .BeforeRender as for a slot render) and run at once (Graphics.ExecuteCommandBuffer), draws into screen-sized targets
    // (WindowScreen, WS) inside the window's rect (scissor):
    //   A2 header  view V_w (a camera at C' = M·eye), projection P_w = the game camera's projection with this frame's TAA
    //              jitter and the exit portal as its oblique near plane; the camera builtins by hand (P27: _WorldSpaceCameraPos,
    //              _ProjectionParams, _ZBufferParams, _ScreenParams, unity_OrthoParams, unity_CameraToWorld/WorldToCamera,
    //              unity_CameraWorldClipPlanes); UNITY_HDR_ON and LIGHTPROBE_SH; the far ambient as SH in each draw's block
    //              (Ambient; P26: SH does not reach a command-buffer draw by itself);
    //   A2 body    the far side's static content (IWindowScreenContent.RecordBody: proxies, terrain, the far core's static
    //              meshes) with each material's Deferred pass into GB0-3 + depth;
    //   SAVE       the oblique depth to the standard depth of the game camera's projection (StdDepth, WindowScreen.shader);
    //   A4 shadows the sun's cascades for a camera at C' (WindowScreenShadows), their ShadowCaster passes into the atlas, and
    //              the screen-space mask with Unity's Internal-ScreenSpaceShadows math (WindowScreen.shader);
    //   A5 light   the game's deferred reflections (the default reflection, then the far side's probe blended over it as
    //              Unity blends probes), added to GB3, and the game's deferred shading (Toon, route L0, as P36 drove it:
    //              _LightAsQuad 0, the far sun's (color x intensity).linear, the shadow globals set globally and the mask as
    //              the material's _ShadowMapTexture) on a view-space quad;
    //   A6 sky     RenderSettings.skybox where StdDepth is far (a projection whose depth is the far plane, tested against
    //              StdDepth);
    //   crop       the rect into the textures the composite reads from a slot camera (colour, linear eye depth, normals),
    //              and the game's fog over them with the far fog values (the slot camera's fog command buffer's material);
    //   A7 restore every global and keyword set here, from a snapshot taken right before recording.
    // Not drawn (spike scope; the slot path has them): transparents and particles (Phase C), water, grass, creatures and
    // other moving objects, point lights, the window's ambient occlusion, nested windows.
    // Main thread; nothing allocated per frame once the targets exist (they follow the screen size).
    public sealed class WindowScreen : IDisposable
    {
        const string ShaderName = "Hidden/RealPortal/WindowScreen";
        const string PassSave = "SAVE", PassMask = "SHADOWMASK", PassMaskHard = "SHADOWMASKHARD", PassCrop = "CROP";

        readonly Material _mat;
        readonly int _passSave = -1, _passMask = -1, _passMaskHard = -1, _passCrop = -1;
        Material _toon, _refl, _reflProbe, _reflAdd;
        Shader _toonShader;
        LocalKeyword _kwDirectional, _kwScreen, _kwSoft;
        bool _kwOk;
        readonly CommandBuffer _cb = new CommandBuffer { name = "RealPortal window screen" };
        readonly WindowScreenShadows _shadows = new WindowScreenShadows();

        // The targets (screen-sized) and the shadow atlas.
        RenderTexture _gb0, _gb1, _gb2, _gb3, _depth, _std, _refl0, _mask, _atlas;
        int _w, _h, _atlasW, _atlasH, _atlasBits;
        readonly RenderTargetIdentifier[] _gbuffer = new RenderTargetIdentifier[4];
        readonly RenderTargetIdentifier[] _crop = new RenderTargetIdentifier[4];
        Mesh _triangle, _quad, _sky;
        readonly Vector3[] _quadVertices = new Vector3[4];
        readonly Vector3[] _quadNormals = new Vector3[4];
        readonly MaterialPropertyBlock _mpbSave = new MaterialPropertyBlock(), _mpbMask = new MaterialPropertyBlock(), _mpbCrop = new MaterialPropertyBlock(),
            _mpbDefault = new MaterialPropertyBlock(), _mpbProbe = new MaterialPropertyBlock();
        readonly Vector4[] _clipPlanes = new Vector4[6];
        readonly Vector3[] _corners = new Vector3[4];

        // Lab: the shadow atlas depth (16 as Unity's directional shadow maps are assumed to be; 24 or 32 to compare).
        internal static int LabShadowBits = 16;

        static readonly int s_rawDepth = Shader.PropertyToID("_RP_ScreenRawDepth");
        static readonly int s_fogTemp = Shader.PropertyToID("_RP_ScreenFogTemp");
        static readonly int s_obliqueDepth = Shader.PropertyToID("_RP_ObliqueDepth");
        static readonly int s_oblRow2 = Shader.PropertyToID("_RP_OblRow2");
        static readonly int s_projXY = Shader.PropertyToID("_RP_ProjXY");
        static readonly int s_stdZ = Shader.PropertyToID("_RP_StdZ");
        static readonly int s_stdDepth = Shader.PropertyToID("_RP_StdDepth");
        static readonly int s_camToWorld = Shader.PropertyToID("_RP_CamToWorld");
        static readonly int s_worldToShadow = Shader.PropertyToID("_RP_WorldToShadow");
        static readonly int s_splitSpheres = Shader.PropertyToID("_RP_SplitSpheres");
        static readonly int s_splitSqRadii = Shader.PropertyToID("_RP_SplitSqRadii");
        static readonly int s_shadowParams = Shader.PropertyToID("_RP_ShadowParams");
        static readonly int s_shadowMap = Shader.PropertyToID("_ShadowMapTexture");
        static readonly int s_shadowMapTexel = Shader.PropertyToID("_ShadowMapTexture_TexelSize");
        static readonly int s_cropColor = Shader.PropertyToID("_RP_CropColor");
        static readonly int s_cropDepth = Shader.PropertyToID("_RP_CropDepth");
        static readonly int s_cropNormal = Shader.PropertyToID("_RP_CropNormal");
        static readonly int s_cropRect = Shader.PropertyToID("_RP_CropRect");
        // The game's lighting materials.
        static readonly int s_lightAsQuad = Shader.PropertyToID("_LightAsQuad");
        static readonly int s_srcBlend = Shader.PropertyToID("_SrcBlend");
        static readonly int s_dstBlend = Shader.PropertyToID("_DstBlend");
        static readonly int s_gbuffer0 = Shader.PropertyToID("_CameraGBufferTexture0");
        static readonly int s_gbuffer1 = Shader.PropertyToID("_CameraGBufferTexture1");
        static readonly int s_gbuffer2 = Shader.PropertyToID("_CameraGBufferTexture2");
        static readonly int s_cameraDepth = Shader.PropertyToID("_CameraDepthTexture");
        static readonly int s_reflections = Shader.PropertyToID("_CameraReflectionsTexture");
        static readonly int s_lightDir = Shader.PropertyToID("_LightDir");
        static readonly int s_lightColor = Shader.PropertyToID("_LightColor");
        static readonly int s_specCube = Shader.PropertyToID("unity_SpecCube0");
        static readonly int s_specHdr = Shader.PropertyToID("unity_SpecCube0_HDR");
        static readonly int s_specMin = Shader.PropertyToID("unity_SpecCube0_BoxMin");
        static readonly int s_specMax = Shader.PropertyToID("unity_SpecCube0_BoxMax");
        static readonly int s_specPos = Shader.PropertyToID("unity_SpecCube0_ProbePosition");
        static readonly int s_specPos1 = Shader.PropertyToID("unity_SpecCube1_ProbePosition");
        // The header (camera builtins by hand, P27), the shadow globals and the light the casters are drawn for.
        static readonly int s_camPos = Shader.PropertyToID("_WorldSpaceCameraPos");
        static readonly int s_projParams = Shader.PropertyToID("_ProjectionParams");
        static readonly int s_zParams = Shader.PropertyToID("_ZBufferParams");
        static readonly int s_screenParams = Shader.PropertyToID("_ScreenParams");
        static readonly int s_orthoParams = Shader.PropertyToID("unity_OrthoParams");
        static readonly int s_cameraToWorld = Shader.PropertyToID("unity_CameraToWorld");
        static readonly int s_worldToCamera = Shader.PropertyToID("unity_WorldToCamera");
        static readonly int s_clipPlanesId = Shader.PropertyToID("unity_CameraWorldClipPlanes");
        static readonly int s_lightShadowData = Shader.PropertyToID("_LightShadowData");
        static readonly int s_fadeCenter = Shader.PropertyToID("unity_ShadowFadeCenterAndType");
        static readonly int s_shadowBias = Shader.PropertyToID("unity_LightShadowBias");
        static readonly int s_lightPos0 = Shader.PropertyToID("_WorldSpaceLightPos0");

        // Globals snapshotted before recording and put back at the end of the buffer (A7).
        static readonly int[] s_vectorGlobals = { s_camPos, s_projParams, s_zParams, s_screenParams, s_orthoParams, s_lightShadowData, s_fadeCenter, s_shadowBias, s_lightPos0 };
        static readonly int[] s_matrixGlobals = { s_cameraToWorld, s_worldToCamera };
        readonly Vector4[] _savedVectors = new Vector4[9];
        readonly Matrix4x4[] _savedMatrices = new Matrix4x4[2];
        readonly Vector4[] _savedPlanes = new Vector4[6];
        bool _savedPlanesOk;
        Texture _savedDepthTexture;
        static readonly List<Vector4> s_planeList = new List<Vector4>(6);
        // Keywords this buffer sets globally (on or off), and their state before it.
        static readonly string[] s_keywordNames =
        {
            "UNITY_HDR_ON", "LIGHTPROBE_SH", "DIRECTIONAL", "POINT", "SPOT", "POINT_COOKIE", "DIRECTIONAL_COOKIE", "SHADOWS_SCREEN", "SHADOWS_DEPTH",
            "SHADOWS_CUBE", "SHADOWS_SOFT", "SHADOWS_SHADOWMASK", "LIGHTMAP_SHADOW_MIXING",
        };
        const int KwHdr = 0, KwProbeSh = 1, KwFirstLight = 2, KwShadowsDepth = 8;
        readonly GlobalKeyword[] _keywords = new GlobalKeyword[s_keywordNames.Length];
        readonly bool[] _keywordsWere = new bool[s_keywordNames.Length];

        // ---- ambient (P26: the far side's flat ambient as SH, in each draw's block) ---------------------------------------
        static SphericalHarmonicsL2 s_ambient;
        static bool s_ambientSet;
        static readonly SphericalHarmonicsL2[] s_ambientInstances = new SphericalHarmonicsL2[1023];
        static readonly SphericalHarmonicsL2[] s_ambientOne = new SphericalHarmonicsL2[1];
        static readonly Vector4[] s_ambientVectors = new Vector4[7];
        static readonly MaterialPropertyBlock s_ambientConvert = new MaterialPropertyBlock();
        static readonly List<Vector4> s_shList = new List<Vector4>(1);
        static readonly int[] s_shIds =
        {
            Shader.PropertyToID("unity_SHAr"), Shader.PropertyToID("unity_SHAg"), Shader.PropertyToID("unity_SHAb"), Shader.PropertyToID("unity_SHBr"),
            Shader.PropertyToID("unity_SHBg"), Shader.PropertyToID("unity_SHBb"), Shader.PropertyToID("unity_SHC"),
        };

        // Draws recorded by the last Render (body, casters and passes).
        internal int LastDraws;

        // Lab: main-thread ms of the virtual renders by part, summed over LabProfRenders: the proxies' cull job, their
        // emission and their sort (ProxyDraw), heightmaps and core objects (the content's listing), the body's recording,
        // the casters' recording, the rest of the recording, Graphics.ExecuteCommandBuffer.
        internal static readonly double[] LabProf = new double[9];
        internal static long LabProfRenders;
        internal static readonly double TickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        static readonly string[] s_profNames = { "cull job", "emit", "sort", "maps", "core", "body", "casters", "passes", "execute" };

        internal static string DescribeProf()
        {
            if (LabProfRenders == 0)
                return "profile: no virtual render";
            var sb = new System.Text.StringBuilder("profile per render (ms):");
            for (int i = 0; i < LabProf.Length; i++)
                sb.Append(' ').Append(s_profNames[i]).Append(' ').Append((LabProf[i] / LabProfRenders).ToString("0.000"));
            return sb.ToString();
        }

        public WindowScreen(Shader shader)
        {
            if (shader == null || !shader.isSupported || SystemInfo.supportedRenderTargetCount < 4)
                return;
            _mat = new Material(shader) { name = "RealPortalWindowScreen", hideFlags = HideFlags.HideAndDontSave };
            _passSave = _mat.FindPass(PassSave);
            _passMask = _mat.FindPass(PassMask);
            _passMaskHard = _mat.FindPass(PassMaskHard);
            _passCrop = _mat.FindPass(PassCrop);
            for (int i = 0; i < s_keywordNames.Length; i++)
                _keywords[i] = GlobalKeyword.Create(s_keywordNames[i]);
        }

        /// <summary>The shader and its passes are there.</summary>
        public bool Ready => _mat != null && _passSave >= 0 && _passMask >= 0 && _passMaskHard >= 0 && _passCrop >= 0;

        internal bool HasTargets => _gb0 != null || _atlas != null;

        internal static string ShaderAssetName => "WindowScreen";

        internal WindowScreenShadows Shadows => _shadows;

        // ---- the game's lighting materials --------------------------------------------------------------------------

        // The deferred shading (Toon) and reflections shaders as the game set them (GraphicsSettings), made once.
        bool EnsureLighting()
        {
            Shader toon = GraphicsSettings.GetShaderMode(BuiltinShaderType.DeferredShading) == BuiltinShaderMode.UseCustom
                ? GraphicsSettings.GetCustomShader(BuiltinShaderType.DeferredShading) : null;
            if (toon == null)
                toon = Shader.Find("Hidden/Internal-DeferredShading");
            if (toon == null)
                return false;
            if (_toon == null || _toonShader != toon)
            {
                if (_toon != null)
                    PortalMath.Kill(_toon);
                _toon = new Material(toon) { name = "RealPortalWindowScreen deferred shading", hideFlags = HideFlags.HideAndDontSave };
                _toonShader = toon;
                LocalKeywordSpace space = toon.keywordSpace;
                _kwDirectional = space.FindKeyword("DIRECTIONAL");
                _kwScreen = space.FindKeyword("SHADOWS_SCREEN");
                _kwSoft = space.FindKeyword("SHADOWS_SOFT");
                _kwOk = _kwDirectional.isValid;
            }
            if (_refl == null)
            {
                Shader refl = GraphicsSettings.GetShaderMode(BuiltinShaderType.DeferredReflections) == BuiltinShaderMode.UseCustom
                    ? GraphicsSettings.GetCustomShader(BuiltinShaderType.DeferredReflections) : null;
                if (refl == null)
                    refl = Shader.Find("Hidden/Internal-DeferredReflections");
                if (refl != null)
                {
                    _refl = new Material(refl) { name = "RealPortalWindowScreen reflections", hideFlags = HideFlags.HideAndDontSave };
                    _reflProbe = new Material(refl) { name = "RealPortalWindowScreen reflections probe", hideFlags = HideFlags.HideAndDontSave };
                    _reflAdd = new Material(refl) { name = "RealPortalWindowScreen reflections add", hideFlags = HideFlags.HideAndDontSave };
                }
            }
            return _kwOk;
        }

        // ---- targets ----------------------------------------------------------------------------------------------

        static RenderTexture Color(int w, int h, RenderTextureFormat f, bool srgb, string name)
        {
            var rt = new RenderTexture(w, h, 0, f, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear)
            {
                name = "RealPortal WS " + name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, useMipMap = false, autoGenerateMips = false,
                hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            return rt;
        }

        static RenderTexture Depth(int w, int h, string name)
        {
            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.Depth, 32) { msaaSamples = 1 };
            try
            {
                desc.depthStencilFormat = GraphicsFormat.D32_SFloat;
            }
            catch (Exception)
            {
            }
            var rt = new RenderTexture(desc) { name = "RealPortal WS " + name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            rt.Create();
            return rt;
        }

        void EnsureTargets(int w, int h)
        {
            if (_gb0 != null && _w == w && _h == h && _gb0.IsCreated() && _gb3.IsCreated() && _depth.IsCreated() && _std.IsCreated())
                return;
            ReleaseScreen();
            bool srgb = QualitySettings.activeColorSpace == ColorSpace.Linear;
            _gb0 = Color(w, h, RenderTextureFormat.ARGB32, srgb, "GB0");
            _gb1 = Color(w, h, RenderTextureFormat.ARGB32, srgb, "GB1");
            _gb2 = Color(w, h, RenderTextureFormat.ARGB2101010, false, "GB2");
            _gb3 = Color(w, h, RenderTextureFormat.DefaultHDR, false, "GB3");
            _refl0 = Color(w, h, RenderTextureFormat.DefaultHDR, false, "reflections");
            _mask = Color(w, h, RenderTextureFormat.R8, false, "shadow mask");
            _depth = Depth(w, h, "depth");
            _std = Depth(w, h, "StdDepth");
            _w = w;
            _h = h;
            _gbuffer[0] = _gb0;
            _gbuffer[1] = _gb1;
            _gbuffer[2] = _gb2;
            _gbuffer[3] = _gb3;
        }

        void EnsureAtlas(int w, int h)
        {
            int bits = LabShadowBits == 24 || LabShadowBits == 32 ? LabShadowBits : 16;
            if (_atlas != null && _atlasW == w && _atlasH == h && _atlasBits == bits && _atlas.IsCreated())
                return;
            if (_atlas != null)
                PortalMath.Kill(_atlas);
            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.Shadowmap, bits) { msaaSamples = 1 };
            _atlas = new RenderTexture(desc)
            {
                name = "RealPortal WS shadow atlas", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave,
            };
            _atlas.Create();
            _atlasW = w;
            _atlasH = h;
            _atlasBits = bits;
        }

        void ReleaseScreen()
        {
            foreach (RenderTexture rt in new[] { _gb0, _gb1, _gb2, _gb3, _refl0, _mask, _depth, _std })
                if (rt != null)
                    PortalMath.Kill(rt);
            _gb0 = _gb1 = _gb2 = _gb3 = _refl0 = _mask = _depth = _std = null;
            _w = _h = 0;
        }

        /// <summary>Frees the screen-sized targets and the atlas (Win.Virtual off; they come back at the next render).</summary>
        internal void ReleaseTargets()
        {
            ReleaseScreen();
            if (_atlas != null)
                PortalMath.Kill(_atlas);
            _atlas = null;
            _atlasW = _atlasH = 0;
            _shadows.Clear();
        }

        // ---- meshes ------------------------------------------------------------------------------------------------

        Mesh Triangle()
        {
            if (_triangle != null)
                return _triangle;
            _triangle = new Mesh { name = "RealPortal WS triangle", hideFlags = HideFlags.HideAndDontSave };
            _triangle.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 3f, 0f), new Vector3(3f, -1f, 0f) };
            _triangle.triangles = new[] { 0, 1, 2 };
            _triangle.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f);
            _triangle.UploadMeshData(true);
            return _triangle;
        }

        // The lighting quad in view space (OpenGL camera space, -z forward) at 1.5 x the near plane, 2 % past the jittered
        // frustum (drawn with an identity view): with _LightAsQuad 0 the deferred shaders take each pixel's ray from its view
        // position, exactly the projection's ray through the pixel. Normals hold the same rays for _LightAsQuad 1. Clockwise
        // as seen from the camera (P36's winding).
        void UpdateQuad(in VirtualView v)
        {
            if (_quad == null)
            {
                _quad = new Mesh { name = "RealPortal WS light quad", hideFlags = HideFlags.HideAndDontSave };
                _quad.MarkDynamic();
                _quad.vertices = _quadVertices;
                _quad.normals = _quadNormals;
                _quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            }
            Matrix4x4 inv = v.Proj.inverse;
            float t = Mathf.Max(v.Near * 1.5f, 0.01f);
            const float spread = 1.02f;
            for (int k = 0; k < 4; k++)
            {
                float x = k < 2 ? -spread : spread, y = k == 0 || k == 3 ? -spread : spread;
                Vector3 p = inv.MultiplyPoint(new Vector3(x, y, -1f));
                p *= t / -p.z;
                _quadVertices[k] = p;
                _quadNormals[k] = new Vector3(p.x / t, p.y / t, 1f);
            }
            _quad.vertices = _quadVertices;
            _quad.normals = _quadNormals;
            _quad.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
        }

        // A unit sphere for the sky (the skybox shaders take the view direction from the vertex).
        Mesh Sky()
        {
            if (_sky != null)
                return _sky;
            const int lon = 64, lat = 32;
            var vertices = new Vector3[(lon + 1) * (lat + 1)];
            var tris = new int[lon * lat * 6];
            for (int j = 0; j <= lat; j++)
            {
                float th = Mathf.PI * j / lat, st = Mathf.Sin(th), ct = Mathf.Cos(th);
                for (int i = 0; i <= lon; i++)
                {
                    float ph = 2f * Mathf.PI * i / lon;
                    vertices[j * (lon + 1) + i] = new Vector3(st * Mathf.Cos(ph), ct, st * Mathf.Sin(ph));
                }
            }
            int k = 0;
            for (int j = 0; j < lat; j++)
                for (int i = 0; i < lon; i++)
                {
                    int a = j * (lon + 1) + i, b = a + lon + 1;
                    tris[k++] = a;
                    tris[k++] = b;
                    tris[k++] = a + 1;
                    tris[k++] = a + 1;
                    tris[k++] = b;
                    tris[k++] = b + 1;
                }
            _sky = new Mesh { name = "RealPortal WS sky", hideFlags = HideFlags.HideAndDontSave, vertices = vertices, triangles = tris };
            _sky.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            _sky.UploadMeshData(true);
            return _sky;
        }

        // ---- ambient ------------------------------------------------------------------------------------------------

        /// <summary>The far side's ambient now (RenderSettings, as BeforeRender left it) as Unity's ambient probe: flat (the
        /// game's mode) built as Unity builds it, else the ambient probe; the per-instance arrays and the seven constants
        /// are refreshed only when it changed.</summary>
        internal static void UpdateAmbient()
        {
            SphericalHarmonicsL2 sh;
            if (RenderSettings.ambientMode == AmbientMode.Flat)
            {
                sh = new SphericalHarmonicsL2();
                Color c = RenderSettings.ambientLight;
                sh.AddAmbientLight(QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c);
            }
            else
                sh = RenderSettings.ambientProbe;
            if (s_ambientSet && sh == s_ambient)
                return;
            s_ambient = sh;
            s_ambientSet = true;
            for (int i = 0; i < s_ambientInstances.Length; i++)
                s_ambientInstances[i] = sh;
            s_ambientOne[0] = sh;
            s_ambientConvert.Clear();
            s_ambientConvert.CopySHCoefficientArraysFrom(s_ambientOne);
            for (int k = 0; k < s_shIds.Length; k++)
            {
                s_shList.Clear();
                s_ambientConvert.GetVectorArray(s_shIds[k] , s_shList);
                s_ambientVectors[k] = s_shList.Count > 0 ? s_shList[0] : Vector4.zero;
            }
        }

        // Unity's per-instance SH arrays are named after these with "Array" (MaterialPropertyBlock.CopySHCoefficientArraysFrom).
        /// <summary>The far ambient into a draw's block: the seven unity_SH* constants, and for an instanced draw of
        /// `instances` the per-instance arrays too (a variant with instanced SH reads those; one without, the constants).</summary>
        internal static void AddAmbient(MaterialPropertyBlock b, int instances)
        {
            for (int k = 0; k < s_shIds.Length; k++)
                b.SetVector(s_shIds[k], s_ambientVectors[k]);
            if (instances > 0)
                b.CopySHCoefficientArraysFrom(s_ambientInstances, 0, 0, Mathf.Min(instances, s_ambientInstances.Length));
        }

        // ---- the header ----------------------------------------------------------------------------------------------

        static Vector4 ZBufferParams(float n, float f)
        {
            if (SystemInfo.usesReversedZBuffer)
            {
                float x = -1f + f / n;
                return new Vector4(x, 1f, x / f, 1f / f);
            }
            float y = 1f - f / n;
            return new Vector4(y, f / n, y / f, f / n / f);
        }

        // The camera builtins of a camera at C' rendering into a texture (P27: set by hand after SetViewProjectionMatrices).
        void RecordHeader(in VirtualView v)
        {
            _cb.SetGlobalVector(s_camPos, new Vector4(v.Eye.x, v.Eye.y, v.Eye.z, 1f));
            _cb.SetGlobalVector(s_projParams, new Vector4(SystemInfo.graphicsUVStartsAtTop ? -1f : 1f, v.Near, v.Far, 1f / v.Far));
            _cb.SetGlobalVector(s_zParams, ZBufferParams(v.Near, v.Far));
            _cb.SetGlobalVector(s_screenParams, new Vector4(v.Width, v.Height, 1f + 1f / v.Width, 1f + 1f / v.Height));
            _cb.SetGlobalVector(s_orthoParams, Vector4.zero);
            Matrix4x4 c2w = Matrix4x4.TRS(v.Eye, v.Rotation, Vector3.one);
            _cb.SetGlobalMatrix(s_cameraToWorld, c2w);
            _cb.SetGlobalMatrix(s_worldToCamera, c2w.inverse);
            _cb.SetGlobalVectorArray(s_clipPlanesId, _clipPlanes);
        }

        void Snapshot()
        {
            for (int i = 0; i < s_vectorGlobals.Length; i++)
                _savedVectors[i] = Shader.GetGlobalVector(s_vectorGlobals[i]);
            for (int i = 0; i < s_matrixGlobals.Length; i++)
                _savedMatrices[i] = Shader.GetGlobalMatrix(s_matrixGlobals[i]);
            s_planeList.Clear();
            Shader.GetGlobalVectorArray(s_clipPlanesId, s_planeList);
            _savedPlanesOk = s_planeList.Count == 6;
            if (_savedPlanesOk)
                s_planeList.CopyTo(_savedPlanes);
            _savedDepthTexture = Shader.GetGlobalTexture(s_cameraDepth);
            for (int i = 0; i < _keywords.Length; i++)
                _keywordsWere[i] = Shader.IsKeywordEnabled(_keywords[i]);
        }

        void RecordRestore()
        {
            _cb.DisableScissorRect();
            for (int i = 0; i < s_vectorGlobals.Length; i++)
                _cb.SetGlobalVector(s_vectorGlobals[i], _savedVectors[i]);
            for (int i = 0; i < s_matrixGlobals.Length; i++)
                _cb.SetGlobalMatrix(s_matrixGlobals[i], _savedMatrices[i]);
            if (_savedPlanesOk)
                _cb.SetGlobalVectorArray(s_clipPlanesId, _savedPlanes);
            if (_savedDepthTexture != null)
                _cb.SetGlobalTexture(s_cameraDepth, _savedDepthTexture);
            for (int i = 0; i < _keywords.Length; i++)
            {
                if (_keywordsWere[i])
                    _cb.EnableKeyword(_keywords[i]);
                else
                    _cb.DisableKeyword(_keywords[i]);
            }
        }

        // ---- one window -----------------------------------------------------------------------------------------

        /// <summary>
        /// Draws view `v` with `content` (its BeforeRender done) into `color` (HDR), `eye` (RFloat linear eye depth) and
        /// `normal` (ARGB2101010), each the rect's size. False (with why) when it cannot be drawn as the game would draw it
        /// (nothing recorded then); an exception leaves nothing executed either (the caller renders the slot instead).
        /// </summary>
        internal bool Render(in VirtualView v, IWindowScreenContent content, RenderTexture color, RenderTexture eye, RenderTexture normal, out string why)
        {
            why = null;
            LastDraws = 0;
            if (!Ready)
            {
                why = "no WindowScreen shader (a bundle without it)";
                return false;
            }
            if (!EnsureLighting())
            {
                why = "the game's deferred shading shader has no DIRECTIONAL variant";
                return false;
            }
            Light sun = content.Sun;
            bool shadows = WindowScreenShadows.Casting(sun);
            bool lit = sun != null && sun.isActiveAndEnabled && sun.type == LightType.Directional && sun.intensity > 0f;
            if (shadows)
            {
                int res = content.ShadowMapSize;
                if (res <= 0)
                {
                    why = "no shadow map size";
                    return false;
                }
                _shadows.Fit(v, sun, res);
                EnsureAtlas(_shadows.AtlasW, _shadows.AtlasH);
            }
            EnsureTargets(v.Width, v.Height);
            UpdateAmbient();
            UpdateQuad(v);
            for (int i = 0; i < 6; i++)
                _clipPlanes[i] = new Vector4(v.ViewPlanes[i].normal.x, v.ViewPlanes[i].normal.y, v.ViewPlanes[i].normal.z, v.ViewPlanes[i].distance);
            var px = new Rect(Mathf.Round(v.Rect.x * v.Width), Mathf.Round(v.Rect.y * v.Height), Mathf.Round(v.Rect.width * v.Width), Mathf.Round(v.Rect.height * v.Height));
            Matrix4x4 c2w = Matrix4x4.TRS(v.Eye, v.Rotation, Vector3.one);
            var projXY = new Vector4(v.Proj.m00, v.Proj.m11, v.Proj.m02, v.Proj.m12);
            float clear = SystemInfo.usesReversedZBuffer ? 0f : 1f;
            var stdZ = new Vector4(v.Proj.m22, v.Proj.m23, v.Far, clear);
            int draws = 0;

            long r0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double bodyMs, castersMs = 0;
            Snapshot();
            _cb.Clear();
            // Light keywords off for the whole buffer (the lighting materials carry their own); HDR and SH on.
            for (int i = KwFirstLight; i < _keywords.Length; i++)
                _cb.DisableKeyword(_keywords[i]);
            _cb.EnableKeyword(_keywords[KwHdr]);
            _cb.EnableKeyword(_keywords[KwProbeSh]);

            // ---- A2: the G-buffer --------------------------------------------------------------------------------
            _cb.SetRenderTarget(_gbuffer, _depth);
            _cb.ClearRenderTarget(true, true, UnityEngine.Color.clear, 1f);
            _cb.EnableScissorRect(px);
            _cb.SetViewProjectionMatrices(v.View, v.Oblique);
            RecordHeader(v);
            long b0 = System.Diagnostics.Stopwatch.GetTimestamp();
            draws += content.RecordBody(_cb, v);
            bodyMs = (System.Diagnostics.Stopwatch.GetTimestamp() - b0) * TickMs;

            // ---- SAVE: the oblique depth as the standard depth -------------------------------------------------------
            _cb.SetRenderTarget(_std);
            _cb.ClearRenderTarget(true, false, UnityEngine.Color.clear, 1f);
            _cb.EnableScissorRect(px);
            _mpbSave.Clear();
            _mpbSave.SetTexture(s_obliqueDepth, _depth);
            _mpbSave.SetVector(s_oblRow2, v.Oblique.GetRow(2));
            _mpbSave.SetVector(s_projXY, projXY);
            _mpbSave.SetVector(s_stdZ, stdZ);
            _cb.DrawMesh(Triangle(), Matrix4x4.identity, _mat, 0, _passSave, _mpbSave);
            draws++;

            // ---- A4: the sun's shadows --------------------------------------------------------------------------
            if (shadows)
            {
                _cb.DisableScissorRect();
                _cb.SetRenderTarget(_atlas);
                _cb.ClearRenderTarget(true, false, UnityEngine.Color.clear, 1f);
                Vector3 toLight = -sun.transform.forward;
                _cb.SetGlobalVector(s_lightPos0, new Vector4(toLight.x, toLight.y, toLight.z, 0f));
                // A directional light's casters: multi_compile_shadowcaster's SHADOWS_DEPTH variant, as Unity draws them.
                _cb.EnableKeyword(_keywords[KwShadowsDepth]);
                long s0 = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int i = 0; i < _shadows.Count; i++)
                {
                    _cb.SetViewport(_shadows.Viewport[i]);
                    _cb.SetViewProjectionMatrices(_shadows.CasterView[i], _shadows.CasterProj[i]);
                    // The camera's builtins stay C''s while its casters render (the vegetation's distance clip reads them).
                    RecordHeader(v);
                    _cb.SetGlobalVector(s_shadowBias, _shadows.Bias[i]);
                    draws += content.RecordCasters(_cb, v, _shadows.Cascades[i]);
                }
                castersMs = (System.Diagnostics.Stopwatch.GetTimestamp() - s0) * TickMs;
                _cb.DisableKeyword(_keywords[KwShadowsDepth]);
                _cb.SetRenderTarget(_mask);
                _cb.ClearRenderTarget(false, true, UnityEngine.Color.white);
                _cb.EnableScissorRect(px);
                _mpbMask.Clear();
                _mpbMask.SetTexture(s_stdDepth, _std);
                _mpbMask.SetTexture(s_shadowMap, _atlas);
                _mpbMask.SetVector(s_shadowMapTexel, new Vector4(1f / _atlasW, 1f / _atlasH, _atlasW, _atlasH));
                _mpbMask.SetMatrix(s_camToWorld, c2w);
                _mpbMask.SetMatrixArray(s_worldToShadow, _shadows.WorldToShadow);
                _mpbMask.SetVectorArray(s_splitSpheres, _shadows.SplitSpheres);
                _mpbMask.SetVector(s_splitSqRadii, _shadows.SplitSqRadii);
                _mpbMask.SetVector(s_shadowParams, new Vector4(_shadows.ShadowData.x, _shadows.Count == 1 ? 1f : 0f, 0f, 0f));
                _mpbMask.SetVector(s_projXY, projXY);
                _mpbMask.SetVector(s_stdZ, stdZ);
                _cb.DrawMesh(Triangle(), Matrix4x4.identity, _mat, 0, WindowScreenShadows.Soft(sun) ? _passMask : _passMaskHard, _mpbMask);
                draws++;
            }

            // ---- A5: reflections, then the sun ----------------------------------------------------------------------
            SetLightingInputs(_toon);
            if (_refl != null)
            {
                SetLightingInputs(_refl);
                SetLightingInputs(_reflProbe);
                _refl.SetFloat(s_srcBlend, (float)BlendMode.One);
                _refl.SetFloat(s_dstBlend, (float)BlendMode.Zero);
                _reflProbe.SetFloat(s_srcBlend, (float)BlendMode.SrcAlpha);
                _reflProbe.SetFloat(s_dstBlend, (float)BlendMode.OneMinusSrcAlpha);
                _reflAdd.SetTexture(s_reflections, _refl0);
                _reflAdd.SetFloat(s_srcBlend, (float)BlendMode.One);
                _reflAdd.SetFloat(s_dstBlend, (float)BlendMode.One);
                _cb.SetRenderTarget(_refl0);
                _cb.ClearRenderTarget(false, true, UnityEngine.Color.clear);
                _cb.EnableScissorRect(px);
                _cb.SetViewProjectionMatrices(Matrix4x4.identity, v.Proj);
                RecordHeader(v);
                // The default reflection (the sky's) everywhere, as P36 matched it.
                if (ReflectionProbe.defaultTexture != null)
                {
                    SetProbe(_mpbDefault, ReflectionProbe.defaultTexture, ReflectionProbe.defaultTextureHDRDecodeValues,
                        new Vector3(-1e9f, -1e9f, -1e9f), new Vector3(1e9f, 1e9f, 1e9f), Vector4.zero, 1f);
                    _cb.DrawMesh(_quad, Matrix4x4.identity, _refl, 0, 0, _mpbDefault);
                    draws++;
                }
                ReflectionProbe probe = content.Probe(v);
                if (probe != null && probe.texture != null)
                {
                    Bounds b = probe.bounds;
                    Vector3 at = probe.transform.position;
                    SetProbe(_mpbProbe, probe.texture, probe.textureHDRDecodeValues, b.min, b.max, new Vector4(at.x, at.y, at.z, probe.boxProjection ? 1f : 0f),
                        Mathf.Max(probe.blendDistance, 1e-4f));
                    _cb.DrawMesh(_quad, Matrix4x4.identity, _reflProbe, 0, 0, _mpbProbe);
                    draws++;
                }
                // Pass 1 adds the reflections buffer to the light buffer (a Blit, as the probe used it).
                _cb.Blit(_refl0, _gb3, _reflAdd, _reflAdd.passCount > 1 ? 1 : 0);
                draws++;
            }
            if (lit)
            {
                _toon.SetFloat(s_srcBlend, (float)BlendMode.One);
                _toon.SetFloat(s_dstBlend, (float)BlendMode.One);
                _toon.SetVector(s_lightDir, new Vector4(sun.transform.forward.x, sun.transform.forward.y, sun.transform.forward.z, 0f));
                Color lc = sun.color * sun.intensity;
                _toon.SetColor(s_lightColor, QualitySettings.activeColorSpace == ColorSpace.Linear ? lc.linear : lc);
                _toon.SetTexture(s_shadowMap, shadows ? (Texture)_mask : Texture2D.whiteTexture);
                _toon.SetKeyword(_kwDirectional, true);
                if (_kwScreen.isValid)
                    _toon.SetKeyword(_kwScreen, shadows);
                if (_kwSoft.isValid)
                    _toon.SetKeyword(_kwSoft, shadows && WindowScreenShadows.Soft(sun));
                _cb.SetRenderTarget(_gb3);
                _cb.EnableScissorRect(px);
                _cb.SetViewProjectionMatrices(Matrix4x4.identity, v.Proj);
                RecordHeader(v);
                if (shadows)
                {
                    // In the UnityShadows cbuffer, which a material value never reaches (P36).
                    _cb.SetGlobalVector(s_lightShadowData, _shadows.ShadowData);
                    _cb.SetGlobalVector(s_fadeCenter, _shadows.FadeCenter);
                }
                _cb.DrawMesh(_quad, Matrix4x4.identity, _toon, 0, 0);
                draws++;
            }

            // ---- A6: the far sky where StdDepth is far ----------------------------------------------------------------
            Material sky = RenderSettings.skybox;
            if (sky != null && sky.passCount == 1)
            {
                // The projection's depth row is its w row: every sky fragment at the far plane, so the depth test against
                // StdDepth keeps it to pixels nothing was drawn in (x and y rasterise as the game camera's).
                Matrix4x4 skyProj = v.Proj;
                skyProj.SetRow(2, skyProj.GetRow(3));
                _cb.SetRenderTarget(_gb3, _std);
                _cb.EnableScissorRect(px);
                _cb.SetViewProjectionMatrices(v.View, skyProj);
                RecordHeader(v);
                _cb.DrawMesh(Sky(), Matrix4x4.TRS(v.Eye, Quaternion.identity, Vector3.one * 10f), sky, 0, 0);
                draws++;
            }

            // ---- the rect into the window's textures, and the game's fog over them ------------------------------------
            int cw = color.width, ch = color.height;
            _cb.DisableScissorRect();
            _cb.GetTemporaryRT(s_rawDepth, cw, ch, 0, FilterMode.Point, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
            _crop[0] = color;
            _crop[1] = eye;
            _crop[2] = normal;
            _crop[3] = s_rawDepth;
            _cb.SetRenderTarget(_crop, color);
            _mpbCrop.Clear();
            _mpbCrop.SetTexture(s_cropColor, _gb3);
            _mpbCrop.SetTexture(s_cropDepth, _std);
            _mpbCrop.SetTexture(s_cropNormal, _gb2);
            _mpbCrop.SetVector(s_cropRect, new Vector4(v.Rect.x, v.Rect.y, v.Rect.width, v.Rect.height));
            _mpbCrop.SetVector(s_projXY, projXY);
            _mpbCrop.SetVector(s_stdZ, stdZ);
            _cb.DrawMesh(Triangle(), Matrix4x4.identity, _mat, 0, _passCrop, _mpbCrop);
            draws++;
            Corners(v);
            Material fog = content.Fog(v, _corners, out int fogPass);
            if (fog != null)
            {
                // FogComponent's own two blits, over the rect (its corner rays are the rect's, as a slot camera's are).
                _cb.GetTemporaryRT(s_fogTemp, cw, ch, 0, FilterMode.Bilinear, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
                _cb.SetGlobalTexture(s_cameraDepth, s_rawDepth);
                _cb.Blit(color, s_fogTemp);
                _cb.Blit(s_fogTemp, color, fog, fogPass);
                _cb.ReleaseTemporaryRT(s_fogTemp);
                draws += 2;
            }
            _cb.ReleaseTemporaryRT(s_rawDepth);

            // ---- A7: everything back ------------------------------------------------------------------------------
            RecordRestore();
            long e0 = System.Diagnostics.Stopwatch.GetTimestamp();
            RenderTexture active = RenderTexture.active;
            Graphics.ExecuteCommandBuffer(_cb);
            RenderTexture.active = active;
            _cb.Clear();
            long e1 = System.Diagnostics.Stopwatch.GetTimestamp();
            LabProf[5] += bodyMs;
            LabProf[6] += castersMs;
            LabProf[7] += (e0 - r0) * TickMs - bodyMs - castersMs;
            LabProf[8] += (e1 - e0) * TickMs;
            LabProfRenders++;
            LastDraws = draws;
            return true;
        }

        void SetLightingInputs(Material m)
        {
            m.SetFloat(s_lightAsQuad, 0f);
            m.SetTexture(s_gbuffer0, _gb0);
            m.SetTexture(s_gbuffer1, _gb1);
            m.SetTexture(s_gbuffer2, _gb2);
            m.SetTexture(s_cameraDepth, _std);
        }

        // A reflection probe's uniforms for Internal-DeferredReflections in a draw's block (P36: the probe through a block);
        // unity_SpecCube1_ProbePosition.w is the probe's blend distance there.
        static void SetProbe(MaterialPropertyBlock b, Texture cube, Vector4 hdr, Vector3 min, Vector3 max, Vector4 position, float blend)
        {
            b.Clear();
            b.SetTexture(s_specCube, cube);
            b.SetVector(s_specHdr, hdr);
            b.SetVector(s_specMin, new Vector4(min.x, min.y, min.z, 1f));
            b.SetVector(s_specMax, new Vector4(max.x, max.y, max.z, 1f));
            b.SetVector(s_specPos, position);
            b.SetVector(s_specPos1, new Vector4(0f, 0f, 0f, blend));
        }

        // The rect's corner rays (world directions from C', as ViewportPointToRay of the slot camera gives the fog): top
        // left, top right, bottom left, bottom right.
        void Corners(in VirtualView v)
        {
            Rect r = v.Rect;
            _corners[0] = Ray(v, r.xMin, r.yMax);
            _corners[1] = Ray(v, r.xMax, r.yMax);
            _corners[2] = Ray(v, r.xMin, r.yMin);
            _corners[3] = Ray(v, r.xMax, r.yMin);
        }

        static Vector3 Ray(in VirtualView v, float x, float y)
        {
            var local = new Vector3((2f * x - 1f + v.Proj.m02) / v.Proj.m00, (2f * y - 1f + v.Proj.m12) / v.Proj.m11, 1f);
            return (v.Rotation * local).normalized;
        }

        public void Dispose()
        {
            ReleaseTargets();
            _cb.Release();
            PortalMath.Kill(_mat);
            PortalMath.Kill(_toon);
            PortalMath.Kill(_refl);
            PortalMath.Kill(_reflProbe);
            PortalMath.Kill(_reflAdd);
            PortalMath.Kill(_triangle);
            PortalMath.Kill(_quad);
            PortalMath.Kill(_sky);
        }
    }
}
