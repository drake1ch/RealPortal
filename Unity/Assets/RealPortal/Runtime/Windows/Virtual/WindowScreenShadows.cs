using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealPortal
{
    // Win.Virtual (FARWORLD-V3 §4.4 A4, §3.5): the sun's cascades for a virtual view, as Unity fits them for a camera at C'
    // with the game camera's own (symmetric, jittered) projection, the game's shadow distance, cascade count and splits,
    // Stable Fit and the game camera's shadow map size. Unity's fit is closed source, so the cascades are taken from Unity
    // itself where it can be read:
    //  - Capture, at the game camera's onPostRender (PortalSystem.MainPostRender): the UnityShadows block Unity left on the
    //    global sheet for that render (unity_ShadowSplitSpheres, unity_ShadowSplitSqRadii, unity_WorldToShadow,
    //    _LightSplitsNear/Far, _LightShadowData, unity_ShadowFadeCenterAndType, unity_LightShadowBias). Accepted only when
    //    it is that camera's: its spheres round its eye, the first split at its near plane, finite matrices. Kept in the
    //    camera's own frame: sphere and fade centres relative to its eye and rotation, each unity_WorldToShadow row in the
    //    sun's basis then (right, up, forward), and the rows' constants once the Stable Fit snap is taken out (below).
    //  - Fit, for a view: every shape quantity (radii, splits, fade, the light-space scale and depth mapping, the atlas
    //    layout, the biases) is the game camera's, as the projection, near plane and settings are; the positions are
    //    carried: the centres to C' and its rotation, the rows into the far sun's basis (the far environment may turn the
    //    sun), and the snap done again at C'. Stable Fit snaps the sphere centre's light-space x and y to the cascade's
    //    texel grid (anchored at the world origin); which rounding and texel size Unity uses is told apart at run time:
    //    for each hypothesis (floor or round, the atlas at 1, 1/2 or 2 times the rule's size) the rows' constants
    //    t + W·snap(c) must stay the same from one capture to the next while the camera moves (they are the tile's place
    //    in the atlas); the hypothesis with the fewest jumps is used (LabSnapJumps). Unity's bias is known for its last
    //    cascade only (unity_LightShadowBias as left); the others are scaled by texel size and depth range (model).
    //  - Without a capture that applies (none yet, settings changed, the read failed, or LabForceModel): a model of the
    //    built-in fit (splits from the near plane, bounding spheres of the jittered frustum's slices, a 2x2/2x1 atlas, the
    //    fade from one lab sample). LabFitModel counts those renders; the lab compares the two.
    // Main thread only; nothing allocated per frame.
    public sealed class WindowScreenShadows
    {
        public const int MaxCascades = 4;
        const int Hypotheses = 6;

        // ---- the fit for one view (Fit) ----------------------------------------------------------------------------
        internal int Count;
        internal int AtlasW, AtlasH;
        internal readonly Matrix4x4[] WorldToShadow = new Matrix4x4[MaxCascades];
        internal readonly Vector4[] SplitSpheres = new Vector4[MaxCascades];
        internal Vector4 SplitSqRadii, ShadowData, FadeCenter;
        internal readonly Vector4[] Bias = new Vector4[MaxCascades];
        // The casters' view (the sun's rotation, at the cascade's centre: UNITY_MATRIX_V is a light view as in Unity's
        // shadow pass) and projection (with that view, onto the cascade's viewport).
        internal readonly Matrix4x4[] CasterView = new Matrix4x4[MaxCascades];
        internal readonly Matrix4x4[] CasterProj = new Matrix4x4[MaxCascades];
        internal readonly Rect[] Viewport = new Rect[MaxCascades];
        internal readonly VirtualCascade[] Cascades = new VirtualCascade[MaxCascades];
        internal Vector3 LightDir;
        Quaternion _lightRotation = Quaternion.identity;
        internal bool Calibrated;

        // ---- lab ---------------------------------------------------------------------------------------------------
        // Captures accepted and refused (with the last reason), fits from a capture and from the model, and per snap
        // hypothesis (floor x1, floor x0.5, floor x2, round x1, round x0.5, round x2) the jumps of the rows' constants
        // between captures; the model's sphere error against the last accepted capture (m, largest over its cascades).
        internal static long LabCaptures, LabCaptureRefused, LabFitCalibrated, LabFitModel;
        internal static string LabCaptureWhy = "";
        internal static readonly long[] LabSnapJumps = new long[Hypotheses];
        internal static int LabSnapPick;
        internal static float LabModelCentreError, LabModelRadiusError;
        // Lab: always the model (to compare it with the capture).
        internal static bool LabForceModel;

        // ---- the capture ----------------------------------------------------------------------------------------------
        bool _valid;
        int _count;
        // What it holds for (a change makes it not apply).
        float _near, _fov, _aspect, _distance;
        int _cascades, _res, _pixW, _pixH;
        Vector3 _split4;
        float _split2;
        ShadowProjection _projection;
        // Camera-relative sphere centres and the spheres' w, squared radii, fade centre, the rest of the block.
        readonly Vector3[] _centreCam = new Vector3[MaxCascades];
        readonly float[] _sphereW = new float[MaxCascades];
        Vector4 _sqRadii, _shadowData, _biasLast;
        Vector3 _fadeCam;
        float _fadeW;
        // Per cascade and row (u, v, depth): the row in the sun's basis (right, up, forward) at the capture; its constant
        // per hypothesis (current and previous capture).
        readonly Vector3[] _coef = new Vector3[MaxCascades * 3];
        readonly double[] _k = new double[Hypotheses * MaxCascades * 3];
        readonly double[] _kPrev = new double[Hypotheses * MaxCascades * 3];
        bool _hasPrev;
        int _atlasW, _atlasH;

        static readonly List<Vector4> s_vectors = new List<Vector4>(4);
        static readonly List<Matrix4x4> s_matrices = new List<Matrix4x4>(4);
        static readonly int s_spheres = Shader.PropertyToID("unity_ShadowSplitSpheres");
        static readonly int[] s_sphereN =
        {
            Shader.PropertyToID("unity_ShadowSplitSpheres0"), Shader.PropertyToID("unity_ShadowSplitSpheres1"),
            Shader.PropertyToID("unity_ShadowSplitSpheres2"), Shader.PropertyToID("unity_ShadowSplitSpheres3"),
        };
        static readonly int s_sqRadii = Shader.PropertyToID("unity_ShadowSplitSqRadii");
        static readonly int s_toShadow = Shader.PropertyToID("unity_WorldToShadow");
        static readonly int[] s_toShadowN =
        {
            Shader.PropertyToID("unity_WorldToShadow0"), Shader.PropertyToID("unity_WorldToShadow1"),
            Shader.PropertyToID("unity_WorldToShadow2"), Shader.PropertyToID("unity_WorldToShadow3"),
        };
        static readonly int s_splitsNear = Shader.PropertyToID("_LightSplitsNear");
        static readonly int s_shadowData = Shader.PropertyToID("_LightShadowData");
        static readonly int s_fade = Shader.PropertyToID("unity_ShadowFadeCenterAndType");
        static readonly int s_bias = Shader.PropertyToID("unity_LightShadowBias");

        // Unity's cascade count for the quality settings (it draws 1, 2 or 4).
        internal static int CascadeCount()
        {
            int n = QualitySettings.shadowCascades;
            return n >= 4 ? 4 : n >= 2 ? 2 : 1;
        }

        internal static bool Soft(Light sun) => sun != null && sun.shadows == LightShadows.Soft && QualitySettings.shadows == ShadowQuality.All;

        /// <summary>The sun casts shadows into a view now (Unity would render its shadow map).</summary>
        internal static bool Casting(Light sun) =>
            sun != null && sun.isActiveAndEnabled && sun.type == LightType.Directional && sun.shadows != LightShadows.None && sun.intensity > 0f
            && QualitySettings.shadows != ShadowQuality.Disable && QualitySettings.shadowDistance > 0f;

        // ---- capture -----------------------------------------------------------------------------------------------

        /// <summary>Right after the game camera rendered (its onPostRender): the UnityShadows block of that render, if it is
        /// that render's. `sun` is the light it was lit by, `res` the game camera's sun shadow map size by the rule (px).</summary>
        internal void Capture(Camera main, Light sun, int res)
        {
            if (main == null || !Casting(sun))
            {
                Refuse("no sun shadows in the game camera's render");
                return;
            }
            int n = CascadeCount();
            Vector3 eye = main.transform.position;
            Quaternion rot = main.transform.rotation;
            // The four spheres (an array on the sheet, else four names).
            s_vectors.Clear();
            Shader.GetGlobalVectorArray(s_spheres, s_vectors);
            bool arrays = s_vectors.Count >= MaxCascades;
            Vector4 sq = Shader.GetGlobalVector(s_sqRadii);
            Vector4 near = Shader.GetGlobalVector(s_splitsNear);
            float distance = QualitySettings.shadowDistance;
            if (Mathf.Abs(near.x - main.nearClipPlane) > 0.01f * Mathf.Max(1f, main.nearClipPlane))
            {
                Refuse("the first split is not the game camera's near plane");
                return;
            }
            for (int i = 0; i < n; i++)
            {
                Vector4 s = arrays ? s_vectors[i] : Shader.GetGlobalVector(s_sphereN[i]);
                float r2 = sq[i];
                if (!(r2 > 0f) || float.IsInfinity(r2) || float.IsNaN(s.x + s.y + s.z))
                {
                    Refuse("a cascade has no sphere");
                    return;
                }
                // Round this camera (another camera's block, kilometres away, is not this render's).
                if ((new Vector3(s.x, s.y, s.z) - eye).magnitude > Mathf.Sqrt(r2) + distance)
                {
                    Refuse("a cascade's sphere is not round the game camera");
                    return;
                }
                _centreCam[i] = Quaternion.Inverse(rot) * (new Vector3(s.x, s.y, s.z) - eye);
                _sphereW[i] = s.w;
            }
            s_matrices.Clear();
            Shader.GetGlobalMatrixArray(s_toShadow, s_matrices);
            bool matrixArray = s_matrices.Count >= MaxCascades;
            Vector3 right = sun.transform.right, up = sun.transform.up, fwd = sun.transform.forward;
            for (int i = 0; i < n; i++)
            {
                Matrix4x4 m = matrixArray ? s_matrices[i] : Shader.GetGlobalMatrix(s_toShadowN[i]);
                for (int k = 0; k < 3; k++)
                {
                    var w = new Vector3(m[k, 0], m[k, 1], m[k, 2]);
                    if (!(w.sqrMagnitude > 0f) || float.IsNaN(w.x + w.y + w.z + m[k, 3]) || float.IsInfinity(m[k, 3]))
                    {
                        Refuse("a cascade has no world-to-shadow matrix");
                        return;
                    }
                    _coef[i * 3 + k] = new Vector3(Vector3.Dot(w, right), Vector3.Dot(w, up), Vector3.Dot(w, fwd));
                }
            }
            Vector4 fade = Shader.GetGlobalVector(s_fade);
            Vector4 data = Shader.GetGlobalVector(s_shadowData);
            if (float.IsNaN(fade.x + fade.y + fade.z + data.z + data.w))
            {
                Refuse("no shadow fade");
                return;
            }
            // Settings it holds for; a change of any of them starts the snap tracking over.
            bool same = _valid && _cascades == n && _res == res && Mathf.Approximately(_near, main.nearClipPlane) && Mathf.Approximately(_fov, main.fieldOfView)
                        && Mathf.Approximately(_aspect, main.aspect) && Mathf.Approximately(_distance, distance) && _pixW == main.pixelWidth && _pixH == main.pixelHeight;
            _count = n;
            _cascades = n;
            _res = res;
            _near = main.nearClipPlane;
            _fov = main.fieldOfView;
            _aspect = main.aspect;
            _distance = distance;
            _pixW = main.pixelWidth;
            _pixH = main.pixelHeight;
            _split2 = QualitySettings.shadowCascade2Split;
            _split4 = QualitySettings.shadowCascade4Split;
            _projection = QualitySettings.shadowProjection;
            _sqRadii = sq;
            _shadowData = data;
            _biasLast = Shader.GetGlobalVector(s_bias);
            _fadeCam = Quaternion.Inverse(rot) * (new Vector3(fade.x, fade.y, fade.z) - eye);
            _fadeW = fade.w;
            // The atlas by the rule, and its height from the cascade's scales (square texels: a 2x1 atlas for two
            // cascades shows as v twice u per metre).
            float su = new Vector3(_coef[0].x, _coef[0].y, _coef[0].z).magnitude, sv = new Vector3(_coef[1].x, _coef[1].y, _coef[1].z).magnitude;
            _atlasW = Mathf.Max(16, res);
            _atlasH = sv > 0f ? Mathf.Max(16, Mathf.ClosestPowerOfTwo(Mathf.RoundToInt(_atlasW * su / sv))) : _atlasW;
            // The rows' constants per snap hypothesis, and their jumps since the last capture.
            if (!same)
                _hasPrev = false;
            Array.Copy(_k, _kPrev, _k.Length);
            for (int h = 0; h < Hypotheses; h++)
                for (int i = 0; i < n; i++)
                {
                    Matrix4x4 m = matrixArray ? s_matrices[i] : Shader.GetGlobalMatrix(s_toShadowN[i]);
                    Vector3 c = eye + rot * _centreCam[i];
                    Snap(c, right, up, i, h, out double dx, out double dy);
                    bool jumped = false;
                    for (int k = 0; k < 3; k++)
                    {
                        var w = new Vector3(m[k, 0], m[k, 1], m[k, 2]);
                        int at = (h * MaxCascades + i) * 3 + k;
                        Vector3 a = _coef[i * 3 + k];
                        _k[at] = m[k, 3] + Dot(w, c) + a.x * dx + a.y * dy;
                        if (_hasPrev && k < 2 && Math.Abs(_k[at] - _kPrev[at]) > 0.5 / (k == 0 ? _atlasW : _atlasH))
                            jumped = true;
                    }
                    if (jumped)
                        LabSnapJumps[h]++;
                }
            _hasPrev = true;
            _valid = true;
            LabCaptures++;
            // The model's spheres against Unity's, for the lab.
            ModelError(main);
        }

        // A capture refused (part of the old one may be overwritten already): the model until the next good one, and the
        // snap tracking starts over.
        void Refuse(string why)
        {
            LabCaptureRefused++;
            LabCaptureWhy = why;
            _valid = false;
            _hasPrev = false;
        }

        /// <summary>Drops the capture (the switch went off, the game camera went).</summary>
        internal void Clear()
        {
            _valid = false;
            _hasPrev = false;
        }

        // The texel size (m) along the sun's right and up of cascade `i` under hypothesis `h` (atlas x 1, 0.5, 2).
        void Texels(int i, int h, out double tx, out double ty)
        {
            double scale = (h % 3) == 0 ? 1.0 : (h % 3) == 1 ? 0.5 : 2.0;
            Vector3 u = _coef[i * 3], v = _coef[i * 3 + 1];
            // The row that varies along right (u when the light's x is the atlas u) gives its texel along right.
            double alongRight = Math.Abs(u.x) >= Math.Abs(v.x) ? Math.Abs(u.x) * _atlasW : Math.Abs(v.x) * _atlasH;
            double alongUp = Math.Abs(v.y) >= Math.Abs(u.y) ? Math.Abs(v.y) * _atlasH : Math.Abs(u.y) * _atlasW;
            tx = alongRight > 0 ? 1.0 / (alongRight * scale) : 1.0;
            ty = alongUp > 0 ? 1.0 / (alongUp * scale) : 1.0;
        }

        // Stable Fit's snap of a sphere centre: its light-space x and y (from the world origin) to the texel grid; the snap's
        // offsets along right and up (m, in double: the centre's coordinates are kilometres, the offsets centimetres).
        void Snap(Vector3 c, Vector3 right, Vector3 up, int i, int h, out double dx, out double dy)
        {
            Texels(i, h, out double tx, out double ty);
            double x = Dot(right, c), y = Dot(up, c);
            bool round = h >= 3;
            dx = (round ? Math.Round(x / tx) : Math.Floor(x / tx)) * tx - x;
            dy = (round ? Math.Round(y / ty) : Math.Floor(y / ty)) * ty - y;
        }

        static double Dot(Vector3 a, Vector3 b) => (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z;

        // The capture applies to a view of the game camera as it is now.
        bool Applies(in VirtualView v, int res)
        {
            return _valid && !LabForceModel && _cascades == CascadeCount() && _res == res && Mathf.Approximately(_near, v.Near)
                   && Mathf.Approximately(_fov, v.FieldOfView) && Mathf.Approximately(_distance, QualitySettings.shadowDistance)
                   && _pixW == v.Width && _pixH == v.Height && _projection == QualitySettings.shadowProjection
                   && Mathf.Approximately(_split2, QualitySettings.shadowCascade2Split) && _split4 == QualitySettings.shadowCascade4Split;
        }

        // ---- fit ---------------------------------------------------------------------------------------------------

        /// <summary>The cascades for view `v` lit by `sun` (casting), `res` the game camera's shadow map size by the rule.</summary>
        internal void Fit(in VirtualView v, Light sun, int res)
        {
            LightDir = sun.transform.forward;
            _lightRotation = sun.transform.rotation;
            if (Applies(v, res))
            {
                FitCaptured(v, sun);
                Calibrated = true;
                LabFitCalibrated++;
            }
            else
            {
                FitModel(v, sun, res);
                Calibrated = false;
                LabFitModel++;
            }
            for (int i = 0; i < Count; i++)
                MakeCaster(i);
        }

        void FitCaptured(in VirtualView v, Light sun)
        {
            Count = _count;
            // The snap hypothesis with the fewest jumps (floor at the rule's size first).
            int best = 0;
            for (int h = 1; h < Hypotheses; h++)
                if (LabSnapJumps[h] < LabSnapJumps[best])
                    best = h;
            LabSnapPick = best;
            double scale = (best % 3) == 0 ? 1.0 : (best % 3) == 1 ? 0.5 : 2.0;
            AtlasW = Mathf.Max(16, (int)(_atlasW * scale));
            AtlasH = Mathf.Max(16, (int)(_atlasH * scale));
            Vector3 right = sun.transform.right, up = sun.transform.up, fwd = sun.transform.forward;
            Texels(Count - 1, best, out double lastTexel, out _);
            float lastDepth = Mathf.Abs(_coef[(Count - 1) * 3 + 2].z);
            for (int i = 0; i < MaxCascades; i++)
            {
                if (i >= Count)
                {
                    WorldToShadow[i] = Matrix4x4.identity;
                    SplitSpheres[i] = new Vector4(v.Eye.x, v.Eye.y, v.Eye.z, 0f);
                    Bias[i] = Vector4.zero;
                    continue;
                }
                Vector3 c = v.Eye + v.Rotation * _centreCam[i];
                Snap(c, right, up, i, best, out double dx, out double dy);
                var m = Matrix4x4.zero;
                for (int k = 0; k < 3; k++)
                {
                    Vector3 a = _coef[i * 3 + k];
                    Vector3 w = right * a.x + up * a.y + fwd * a.z;
                    double t = _k[(best * MaxCascades + i) * 3 + k] - (Dot(w, c) + a.x * dx + a.y * dy);
                    m[k, 0] = w.x;
                    m[k, 1] = w.y;
                    m[k, 2] = w.z;
                    m[k, 3] = (float)t;
                }
                m[3, 3] = 1f;
                WorldToShadow[i] = m;
                SplitSpheres[i] = new Vector4(c.x, c.y, c.z, _sphereW[i]);
                // Unity's bias of its last cascade; the others by texel size and depth range (model).
                Texels(i, best, out double texel, out _);
                float depth = Mathf.Abs(_coef[i * 3 + 2].z);
                float t2 = lastTexel > 0 ? (float)(texel / lastTexel) : 1f;
                float d2 = lastDepth > 0f ? depth / lastDepth : 1f;
                Bias[i] = new Vector4(_biasLast.x * t2 * d2, _biasLast.y, _biasLast.z * t2, _biasLast.w);
            }
            SplitSqRadii = _sqRadii;
            for (int i = Count; i < MaxCascades; i++)
                SplitSqRadii[i] = 0f;
            Vector3 fade = v.Eye + v.Rotation * _fadeCam;
            FadeCenter = new Vector4(fade.x, fade.y, fade.z, _fadeW);
            ShadowData = new Vector4(1f - sun.shadowStrength, _shadowData.y, _shadowData.z, _shadowData.w);
        }

        // ---- the model (no capture applies) ----------------------------------------------------------------------------

        static readonly Vector3[] s_corners = new Vector3[8];

        // The bounding sphere of the jittered frustum's slice between view depths d0 and d1 (camera space, +z forward):
        // centred on the line through the slice's near and far centres, equally far from its near and far corners
        // (clamped to the slice).
        static void SliceSphere(in Matrix4x4 proj, float d0, float d1, out Vector3 centre, out float radius)
        {
            Matrix4x4 inv = proj.inverse;
            Vector3 cn = Vector3.zero, cf = Vector3.zero;
            for (int k = 0; k < 4; k++)
            {
                float x = (k & 1) == 0 ? -1f : 1f, y = (k & 2) == 0 ? -1f : 1f;
                Vector3 p = inv.MultiplyPoint(new Vector3(x, y, -1f));
                Vector3 ray = new Vector3(p.x, p.y, -p.z) / -p.z;
                s_corners[k] = ray * d0;
                s_corners[k + 4] = ray * d1;
                cn += s_corners[k];
                cf += s_corners[k + 4];
            }
            cn *= 0.25f;
            cf *= 0.25f;
            float rn2 = 0f, rf2 = 0f;
            for (int k = 0; k < 4; k++)
            {
                rn2 = Mathf.Max(rn2, (s_corners[k] - cn).sqrMagnitude);
                rf2 = Mathf.Max(rf2, (s_corners[k + 4] - cf).sqrMagnitude);
            }
            Vector3 axis = cf - cn;
            float len = axis.magnitude;
            float x0 = len > 1e-6f ? Mathf.Clamp((len * len + rf2 - rn2) / (2f * len), 0f, len) : 0f;
            centre = len > 1e-6f ? cn + axis * (x0 / len) : cn;
            radius = Mathf.Sqrt(Mathf.Max(x0 * x0 + rn2, (len - x0) * (len - x0) + rf2));
        }

        static float SplitAt(int n, int i)
        {
            if (n == 2)
                return QualitySettings.shadowCascade2Split;
            Vector3 s = QualitySettings.shadowCascade4Split;
            return i == 0 ? s.x : i == 1 ? s.y : s.z;
        }

        void FitModel(in VirtualView v, Light sun, int res)
        {
            int n = CascadeCount();
            Count = n;
            float near = v.Near, distance = Mathf.Min(QualitySettings.shadowDistance, v.Far);
            AtlasW = Mathf.Max(16, res);
            AtlasH = n == 2 ? Mathf.Max(16, res / 2) : AtlasW;
            float tw = n == 1 ? 1f : 0.5f, th = n == 4 ? 0.5f : 1f;
            Vector3 right = sun.transform.right, up = sun.transform.up, fwd = sun.transform.forward;
            float offset = QualitySettings.shadowNearPlaneOffset;
            bool reversed = SystemInfo.usesReversedZBuffer;
            for (int i = 0; i < MaxCascades; i++)
            {
                if (i >= n)
                {
                    WorldToShadow[i] = Matrix4x4.identity;
                    SplitSpheres[i] = new Vector4(v.Eye.x, v.Eye.y, v.Eye.z, 0f);
                    SplitSqRadii[i] = 0f;
                    Bias[i] = Vector4.zero;
                    continue;
                }
                float d0 = i == 0 ? near : near + (distance - near) * SplitAt(n, i - 1);
                float d1 = i == n - 1 ? distance : near + (distance - near) * SplitAt(n, i);
                SliceSphere(v.Proj, d0, d1, out Vector3 local, out float r);
                Vector3 c = v.Eye + v.Rotation * local;
                float tileX = n == 1 ? 0f : (i % 2) * 0.5f, tileY = n == 4 ? (i / 2) * 0.5f : 0f;
                float tilePx = tw * AtlasW;
                double texel = 2.0 * r / tilePx;
                double x = Vector3.Dot(right, c), y = Vector3.Dot(up, c);
                double xs = Math.Floor(x / texel) * texel, ys = Math.Floor(y / texel) * texel;
                float su = tw / (2f * r), sv = th / (2f * r);
                float range = 2f * r + offset, dc = Vector3.Dot(fwd, c);
                var m = Matrix4x4.zero;
                m.SetRow(0, new Vector4(right.x * su, right.y * su, right.z * su, (float)(-xs * su) + tileX + tw * 0.5f));
                m.SetRow(1, new Vector4(up.x * sv, up.y * sv, up.z * sv, (float)(-ys * sv) + tileY + th * 0.5f));
                // Device depth: the near plane (pancaked) at r + offset toward the sun from the centre, the far one r behind it.
                if (reversed)
                    m.SetRow(2, new Vector4(-fwd.x / range, -fwd.y / range, -fwd.z / range, (dc + r) / range));
                else
                    m.SetRow(2, new Vector4(fwd.x / range, fwd.y / range, fwd.z / range, -(dc - r - offset) / range));
                m.SetRow(3, new Vector4(0f, 0f, 0f, 1f));
                WorldToShadow[i] = m;
                SplitSpheres[i] = new Vector4(c.x, c.y, c.z, r * r);
                SplitSqRadii[i] = r * r;
                float worldBias = sun.shadowBias * (float)texel;
                Bias[i] = new Vector4(reversed ? -worldBias / range : worldBias / range, 1f, sun.shadowNormalBias * (float)texel, 0f);
            }
            // The fade from one lab sample (p36iter7: a band of 0.2 x the distance ending 0.744 x the distance from a
            // centre ahead of the camera); model only.
            float band = 0.2f * distance, end = 0.7444f * distance;
            Vector3 centre = v.Eye + v.Forward * (distance - end);
            FadeCenter = new Vector4(centre.x, centre.y, centre.z, 1f);
            ShadowData = new Vector4(1f - sun.shadowStrength, distance, 1f / band, 1f - end / band);
        }

        // Lab: the model's spheres for the game camera against the capture just taken.
        void ModelError(Camera main)
        {
            float centre = 0f, radius = 0f;
            Matrix4x4 proj = main.projectionMatrix;
            float distance = Mathf.Min(QualitySettings.shadowDistance, main.farClipPlane);
            for (int i = 0; i < _count; i++)
            {
                float d0 = i == 0 ? main.nearClipPlane : main.nearClipPlane + (distance - main.nearClipPlane) * SplitAt(_count, i - 1);
                float d1 = i == _count - 1 ? distance : main.nearClipPlane + (distance - main.nearClipPlane) * SplitAt(_count, i);
                SliceSphere(proj, d0, d1, out Vector3 local, out float r);
                centre = Mathf.Max(centre, (local - _centreCam[i]).magnitude);
                radius = Mathf.Max(radius, Mathf.Abs(r - Mathf.Sqrt(_sqRadii[i])));
            }
            LabModelCentreError = centre;
            LabModelRadiusError = radius;
        }

        // ---- the casters' projection and viewport ---------------------------------------------------------------------

        // Cascade i's atlas viewport (its sphere's square, snapped out to whole texels) and the OpenGL projection (with an
        // identity view) that maps world points to it as unity_WorldToShadow maps them to the atlas: x and y onto the
        // viewport, z to the same device depth (Unity's GPU conversion of the projection gives back that depth).
        void MakeCaster(int i)
        {
            Matrix4x4 m = WorldToShadow[i];
            Vector4 ru = m.GetRow(0), rv = m.GetRow(1), rz = m.GetRow(2);
            Vector4 sp = SplitSpheres[i];
            float r = Mathf.Sqrt(Mathf.Max(SplitSqRadii[i], 0f));
            float su = new Vector3(ru.x, ru.y, ru.z).magnitude, sv = new Vector3(rv.x, rv.y, rv.z).magnitude, sz = new Vector3(rz.x, rz.y, rz.z).magnitude;
            float uc = ru.x * sp.x + ru.y * sp.y + ru.z * sp.z + ru.w, vc = rv.x * sp.x + rv.y * sp.y + rv.z * sp.z + rv.w;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((uc - r * su) * AtlasW + 1e-3f), 0, AtlasW), x1 = Mathf.Clamp(Mathf.CeilToInt((uc + r * su) * AtlasW - 1e-3f), 0, AtlasW);
            int y0 = Mathf.Clamp(Mathf.FloorToInt((vc - r * sv) * AtlasH + 1e-3f), 0, AtlasH), y1 = Mathf.Clamp(Mathf.CeilToInt((vc + r * sv) * AtlasH - 1e-3f), 0, AtlasH);
            if (x1 <= x0)
                x1 = Mathf.Min(AtlasW, x0 + 1);
            if (y1 <= y0)
                y1 = Mathf.Min(AtlasH, y0 + 1);
            Viewport[i] = new Rect(x0, y0, x1 - x0, y1 - y0);
            float vx0 = (float)x0 / AtlasW, vw = (float)(x1 - x0) / AtlasW, vy0 = (float)y0 / AtlasH, vh = (float)(y1 - y0) / AtlasH;
            var p = Matrix4x4.zero;
            p.SetRow(0, ru * (2f / vw) - new Vector4(0f, 0f, 0f, 2f * vx0 / vw + 1f));
            p.SetRow(1, rv * (2f / vh) - new Vector4(0f, 0f, 0f, 2f * vy0 / vh + 1f));
            bool reversed = SystemInfo.usesReversedZBuffer;
            p.SetRow(2, reversed ? -2f * rz + new Vector4(0f, 0f, 0f, 1f) : 2f * rz - new Vector4(0f, 0f, 0f, 1f));
            p.SetRow(3, new Vector4(0f, 0f, 0f, 1f));
            // World -> clip as p; split into a light view at the sphere's centre and the projection after it.
            var centre = new Vector3(sp.x, sp.y, sp.z);
            Matrix4x4 viewToWorld = Matrix4x4.TRS(centre, _lightRotation, Vector3.one) * Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
            CasterView[i] = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(centre, _lightRotation, Vector3.one).inverse;
            CasterProj[i] = p * viewToWorld;
            Vector3 toLight = -LightDir;
            Cascades[i] = new VirtualCascade
            {
                Index = i,
                RowU = ru,
                RowV = rv,
                RowZ = rz,
                Tile = new Vector4(vx0, vy0, vx0 + vw, vy0 + vh),
                LightDir = LightDir,
                ScaleU = su,
                ScaleV = sv,
                ScaleZ = sz,
                FarDepth = reversed ? 0f : 1f,
                TowardLight = rz.x * toLight.x + rz.y * toLight.y + rz.z * toLight.z >= 0f ? 1f : -1f,
            };
        }

        internal static string Describe() =>
            $"shadow fit: captures {LabCaptures} (refused {LabCaptureRefused}: {LabCaptureWhy}), fits from the capture {LabFitCalibrated}, model {LabFitModel}, " +
            $"snap jumps floor x1/x0.5/x2 {LabSnapJumps[0]}/{LabSnapJumps[1]}/{LabSnapJumps[2]} round x1/x0.5/x2 {LabSnapJumps[3]}/{LabSnapJumps[4]}/{LabSnapJumps[5]} " +
            $"(picked {LabSnapPick}), model vs capture: centre {LabModelCentreError:0.###} m, radius {LabModelRadiusError:0.###} m";
    }
}
