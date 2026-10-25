using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal
{
    // FarWorld proxies, the drawing part (Far.Proxies; ARCHITECTURE-v2.md §3.7 rungs 1-2, §3.12; FARWORLD-V3.md §3.2 b, §7 V3):
    // right before a window camera's Render() (BeforeSlotRender and BeforeNestedRender as the last handler, after the
    // grass; PortalRenderer.BeforePlainRender for renders without either: no manager, a deeper level of such a render,
    // RenderDebug), the proxies of the far area the window looks into go to that camera alone with
    // Graphics.RenderMeshInstanced (RenderParams.camera), the game's own meshes and materials. Never the game camera.
    //
    // Per render:
    //  - spans: FarProxies.CollectSpans(the window's far portal); a span culled whole by its bounds (outside the view and
    //    outside the sun's caster volume);
    //  - ProxyCullJob (Unity jobs, no Burst), per instance: not Created and not Hidden (an InActive one is drawn until its
    //    object is made: its area makes it, and the real one replaces it in the frame its Created bit is set), Distant in a
    //    Ring span; the exit-plane rule (a sphere wholly on the camera's side of the exit portal is not in view: the proxy
    //    equivalent of HideBehindExit and of the oblique clip); the view's planes (the camera's own projection: what it
    //    rasterises); outside the view, the sun's caster volume (the culling frustum up to the shadow distance and beyond the
    //    exit plane, swept toward the sun) gives a shadows-only caster; LOD by Unity's rule from C' (the window camera's
    //    position) with the GAME camera's vertical FOV and the game's lodBias and maximumLODLevel, and the same at this
    //    view's previous render (for the animated cross-fade);
    //  - cross-fade as LODGroup does it: fadeTransitionWidth bands (unity_LODFade.x = f on the level fading out, -f on the
    //    next one), or animated over the game's crossFadeAnimationDuration (not the windows' instant one, PortalSystem
    //    .SetLodCrossFade) per view and instance; LodFadeInOut's fade-in for an object that appears in a built sector (hidden
    //    0.1-0.3 s, then faded in from culled);
    //  - parts (variant parts whose LodMask holds the shown level; a part in both levels of a fade is drawn plain) into runs
    //    keyed (batch, property block, shadow mode, fade value), counting-sorted into one matrix array, cut into FarRuns;
    //  - submission: per run in chunks of at most 1023, RenderParams from the batch (layer, shadow mode, receive shadows,
    //    light and reflection probe usage, rendering layer mask), the run's AABB as worldBounds, the variant's block or (in a
    //    fade) a pooled block with its values and unity_LODFade, the material's instancing rung: the material itself when
    //    it has instancing on, else a copy with it on when its shader has instanced variants (ProxyTemplates keeps a part
    //    real when neither works), and a copy with LOD_FADE_CROSSFADE for fading runs (the plain one when the shader has no
    //    such variant: then Unity draws both levels whole, as here).
    // P3(ii): a camera-targeted draw is consumed by that camera's next Render(), which comes right after these handlers.
    // Main thread; the jobs complete inside the handler. No managed allocation per frame once the buffers have grown.
    internal static class ProxyDraw
    {
        // What one instanced draw takes (FarDrawLimits; the shaders' own cap is Unity's to split).
        const int MaxChunk = FarDrawLimits.HardMaxInstances;
        // Spheres grown by this much for the view tests (TAA jitter, float rounding): a part outside the view draws nothing.
        const float Margin = 0.05f;
        // How far toward the sun a caster may stand and still shade the view (Unity extrudes its caster volume without end;
        // a far area is a few hundred metres across).
        const float CasterReach = 1000f;
        // LodFadeInOut.m_minTriggerDistance and its Invoke delay range.
        const float LodFadeMinDistance = 20f, LodFadeDelayMin = 0.1f, LodFadeDelayMax = 0.3f;
        // CullOut.Lod: no LODGroup (every part), or no level (culled).
        internal const byte NoLevel = 254, Culled = 255;
        // Below this many candidates the cull runs inline (scheduling costs more).
        const int InlineCandidates = 256;

        // Lab: renders that drew any proxy, candidates tested, instances in view, shadows-only casters, instances in a
        // cross-fade, runs, spans culled whole, errors, LodFadeInOut hides skipped and first-frame material checks at the
        // handoff (ProxyHandoff), materials drawn through an instancing copy.
        internal static long LabRenders, LabCandidates, LabVisible, LabCasters, LabFading, LabRuns, LabSpansCulled, LabErrors;
        internal static long LabHandoffSkips, LabFirstFrameChecks;
        internal static int LabCloneMaterials, LabFadeMaterials;
        static float s_errorAt;

        // ---- frame state (FrameSync, outside the window renders) ----------------------------------------------------

        // The game's lodBias and maximumLODLevel (a window render changes the bias for its own renderers) and its LOD
        // cross-fade time (the windows make it instant for their real objects).
        static float s_lodBias = -1f, s_fadeDuration = 0.5f;
        static int s_maxLod;
        static int s_frame = -1;
        static float s_sweepAt;

        // ---- per render ---------------------------------------------------------------------------------------------

        static readonly List<ProxySpanRef> s_spans = new List<ProxySpanRef>();
        static readonly Plane[] s_frustum = new Plane[6];
        // Candidate spans (not culled whole), their first output index, and their cull ranges grouped by page.
        static ProxySpanRef[] s_cands = new ProxySpanRef[64];
        static int[] s_candOut = new int[64];
        static int s_candCount;
        static NativeArray<CullRange> s_ranges;
        static NativeArray<CullOut> s_out;
        static NativeArray<Vector4> s_planes;
        static bool[] s_grouped = new bool[64];
        // Per page group: its first range, its range count and a candidate holding its page.
        static int[] s_groupFirst = new int[64], s_groupCount = new int[64], s_groupCand = new int[64];
        // Emitted parts (unsorted): their run and matrix.
        static int[] s_emitRun = new int[1024];
        static Matrix4x4[] s_emitM = new Matrix4x4[1024];
        static int s_emits;
        // Win.Virtual: this cull is a virtual view's (its emitted parts also keep their instance's sphere, sorted along).
        static bool s_virtual;
        static Vector4[] s_emitS = new Vector4[1024];
        static NativeArray<Vector4> s_sortedS;
        // Runs and their keys; the distinct fade values of this render (index 0: none).
        struct Run
        {
            public int Batch, Mpb, Fade, Count, Start, Cursor;
            public ShadowCastingMode Mode;
            public Vector3 Min, Max;
        }
        static Run[] s_runs = new Run[256];
        static FarRun[] s_farRuns = new FarRun[256];
        static int s_runCount;
        static readonly Dictionary<long, int> s_runOf = new Dictionary<long, int>();
        static readonly Dictionary<float, int> s_fadeOf = new Dictionary<float, int>();
        static float[] s_fadeVals = new float[64];
        static int s_fadeCount;
        static NativeArray<Matrix4x4> s_sorted;
        // Blocks for fading runs (the variant's values and unity_LODFade), reused from render to render.
        static readonly List<MaterialPropertyBlock> s_fadeBlocks = new List<MaterialPropertyBlock>();
        static int s_fadeBlocksUsed;
        static readonly int s_lodFadeId = Shader.PropertyToID("unity_LODFade");
        // This render's camera mask and whether a caster on the camera's side keeps two-sided shadows (the oblique clip
        // leaves its renderer as it is; HideBehindExit makes it ShadowsOnly).
        static int s_mask;
        static bool s_obliqueClip;

        // Per registry row: the last frame a window drew its proxy in view (the handoff asks).
        static int[] s_rowDrawn = new int[1024];
        // Rows that appeared in a built sector (LodFadeInOut's fade-in): when.
        static readonly Dictionary<int, float> s_born = new Dictionary<int, float>();
        static readonly List<int> s_sweepRows = new List<int>();

        // A view: one window seen from one place (its own on-screen render, or one of its deeper or nested levels), its
        // previous render's pose for the animated cross-fade and the fades it has running (by registry row).
        struct Fade
        {
            public byte From, To;
            public float Start;
        }

        sealed class View
        {
            internal long Key;
            internal Vector3 PrevPos;
            internal float PrevK, PrevTime, UsedAt;
            internal int PrevFrame = -1;
            internal readonly Dictionary<int, Fade> Fades = new Dictionary<int, Fade>();
        }

        static readonly List<View> s_views = new List<View>();

        // ---- the material ladder --------------------------------------------------------------------------------

        // How a material is drawn instanced: itself (rung 1: instancing on), or a copy with instancing on (rung 2: its
        // shader has instanced variants); Fade, a copy of that with LOD_FADE_CROSSFADE (null: the shader has no such variant).
        sealed class MatEntry
        {
            internal Material Source, Draw, Fade;
            internal Shader Shader;
            internal bool Ok, Clone;
            internal LocalKeyword FadeKeyword;
            internal float SyncedAt;
        }

        static readonly Dictionary<Material, MatEntry> s_mats = new Dictionary<Material, MatEntry>();

        // Whether the material can be drawn instanced exactly (ProxyTemplates keeps a part real when not).
        internal static bool Instanceable(Material m) => (object)m != null && m != null && EntryOf(m).Ok;

        static MatEntry EntryOf(Material m)
        {
            if (s_mats.TryGetValue(m, out MatEntry e))
            {
                if (ReferenceEquals(e.Shader, m.shader))
                    return e;
                Drop(e);
                s_mats.Remove(m);
            }
            e = new MatEntry { Source = m, Shader = m.shader, SyncedAt = Time.unscaledTime };
            Shader shader = m.shader;
            if (shader != null)
            {
                LocalKeywordSpace space = shader.keywordSpace;
                if (m.enableInstancing)
                {
                    e.Draw = m;
                    e.Ok = true;
                }
                else if (space.FindKeyword("INSTANCING_ON").isValid)
                {
                    e.Draw = new Material(m) { name = m.name + " (RP instanced)", enableInstancing = true, hideFlags = HideFlags.HideAndDontSave };
                    e.Clone = true;
                    e.Ok = true;
                    LabCloneMaterials++;
                }
                if (e.Ok)
                {
                    LocalKeyword kw = space.FindKeyword("LOD_FADE_CROSSFADE");
                    if (kw.isValid)
                    {
                        e.FadeKeyword = kw;
                        e.Fade = new Material(e.Draw) { name = m.name + " (RP lod fade)", enableInstancing = true, hideFlags = HideFlags.HideAndDontSave };
                        e.Fade.EnableKeyword(kw);
                        LabFadeMaterials++;
                    }
                }
            }
            s_mats[m] = e;
            return e;
        }

        // A copy follows its source (a value the game set on the shared material), at most once a second, at once after a
        // graphics settings change.
        static void Resync(MatEntry e)
        {
            float now = Time.unscaledTime;
            if (now - e.SyncedAt < 1f || e.Source == null)
                return;
            e.SyncedAt = now;
            if (e.Clone && e.Draw != null)
            {
                e.Draw.CopyPropertiesFromMaterial(e.Source);
                e.Draw.enableInstancing = true;
            }
            if (e.Fade != null)
            {
                e.Fade.CopyPropertiesFromMaterial(e.Source);
                e.Fade.enableInstancing = true;
                e.Fade.EnableKeyword(e.FadeKeyword);
            }
        }

        static void Drop(MatEntry e)
        {
            if (e.Clone && e.Draw != null)
                UnityEngine.Object.Destroy(e.Draw);
            if (e.Fade != null)
                UnityEngine.Object.Destroy(e.Fade);
            e.Draw = e.Fade = null;
            e.Ok = false;
        }

        // GraphicsSettingsChanged (WindowGlue): the copies take their sources' values again before their next draw.
        internal static void SettingsChanged()
        {
            foreach (MatEntry e in s_mats.Values)
                e.SyncedAt = float.NegativeInfinity;
        }

        // ---- frame and lifetime ---------------------------------------------------------------------------------

        // FarProxies.Update (the Update phase, no window render running): the game's LOD settings for this frame.
        internal static void FrameSync()
        {
            if (PortalRenderer.InWindowRender)
                return;
            s_lodBias = QualitySettings.lodBias;
            s_maxLod = QualitySettings.maximumLODLevel;
            PortalSystem sys = PortalSystem.Instance;
            s_fadeDuration = sys != null ? sys.GameCrossFadeDuration : LODGroup.crossFadeAnimationDuration;
        }

        // The first window render of a frame: this frame's counters, and once a second what has gone stale.
        static void BeginFrame()
        {
            int frame = Time.frameCount;
            if (frame == s_frame)
                return;
            s_frame = frame;
            FarProxies.LabDrawCalls = 0;
            FarProxies.LabDrawInstances = 0;
            FarProxies.LabCullMs = 0.0;
            if (s_lodBias < 0f)
            {
                // No FrameSync yet (the proxies' first frame): the values as they stand.
                s_lodBias = QualitySettings.lodBias;
                s_maxLod = QualitySettings.maximumLODLevel;
                PortalSystem sys = PortalSystem.Instance;
                s_fadeDuration = sys != null ? sys.GameCrossFadeDuration : LODGroup.crossFadeAnimationDuration;
            }
            float now = Time.unscaledTime;
            if (now < s_sweepAt)
                return;
            s_sweepAt = now + 1f;
            float time = Time.time, dur = Mathf.Max(s_fadeDuration, 0f);
            for (int i = s_views.Count - 1; i >= 0; i--)
            {
                View v = s_views[i];
                if (now - v.UsedAt > 10f)
                {
                    s_views.RemoveAt(i);
                    continue;
                }
                if (v.Fades.Count == 0)
                    continue;
                foreach (KeyValuePair<int, Fade> f in v.Fades)
                    if (time - f.Value.Start > dur)
                        s_sweepRows.Add(f.Key);
                for (int k = 0; k < s_sweepRows.Count; k++)
                    v.Fades.Remove(s_sweepRows[k]);
                s_sweepRows.Clear();
            }
            if (s_born.Count > 0)
            {
                foreach (KeyValuePair<int, float> b in s_born)
                    if (time - b.Value > LodFadeDelayMax + dur + 1f)
                        s_sweepRows.Add(b.Key);
                for (int k = 0; k < s_sweepRows.Count; k++)
                    s_born.Remove(s_sweepRows[k]);
                s_sweepRows.Clear();
            }
        }

        // The proxies stopped (rows mean nothing any more); with `materials` the scene went too (the instancing copies go).
        internal static void Clear(bool materials)
        {
            s_views.Clear();
            s_born.Clear();
            Array.Clear(s_rowDrawn, 0, s_rowDrawn.Length);
            s_spans.Clear();
            Array.Clear(s_cands, 0, s_cands.Length);
            s_candCount = 0;
            if (!materials)
                return;
            foreach (MatEntry e in s_mats.Values)
                Drop(e);
            s_mats.Clear();
            LabCloneMaterials = LabFadeMaterials = 0;
            if (s_ranges.IsCreated)
                s_ranges.Dispose();
            if (s_out.IsCreated)
                s_out.Dispose();
            if (s_planes.IsCreated)
                s_planes.Dispose();
            if (s_sorted.IsCreated)
                s_sorted.Dispose();
            if (s_sortedS.IsCreated)
                s_sortedS.Dispose();
            s_virtualReady = false;
            VirtualPasses.Clear();
        }

        // FarProxies' builder: a row of a built sector got its proxy as a new object (placed there, or arrived over the
        // network). Its real object would run LodFadeInOut (hidden 0.1-0.3 s, then faded in) when made away from the game
        // camera.
        internal static void Born(int row)
        {
            SectorRegistry reg = FarProxies.Registry;
            Camera main = Utils.GetMainCamera();
            if (reg == null || main == null || row < 0)
                return;
            ZDO z = reg.RowZdo(row);
            if (z == null || Vector3.Distance(main.transform.position, z.GetPosition()) <= LodFadeMinDistance)
                return;
            s_born[row] = Time.time;
        }

        // The row went (its proxy with it).
        internal static void Gone(int row)
        {
            if (s_born.Count > 0)
                s_born.Remove(row);
            if ((uint)row < (uint)s_rowDrawn.Length)
                s_rowDrawn[row] = 0;
        }

        // LodFadeInOut's Invoke delay, fixed per row (the game's Random.Range draw is the real object's).
        static float BornDelay(int row)
        {
            uint h = (uint)row * 2654435761u;
            h ^= h >> 15;
            return LodFadeDelayMin + (LodFadeDelayMax - LodFadeDelayMin) * ((h & 0xFFFF) / 65535f);
        }

        // ---- the handoff (ProxyHandoff) ---------------------------------------------------------------------------

        // The ZDO's proxy was drawn in some window's view within the last two frames (ARCHITECTURE-v2.md §3.12).
        internal static bool HasVisibleProxy(ZDO zdo)
        {
            if (!FarProxies.Drawing || !FarProxies.TryGetAny(zdo, out ProxyInst inst))
                return false;
            if ((inst.Flags & (uint)ProxyFlags.Hidden) != 0 || (uint)inst.Row >= (uint)s_rowDrawn.Length)
                return false;
            int at = s_rowDrawn[inst.Row];
            return at > 0 && Time.frameCount - at <= 2;
        }

        static void MarkDrawn(int row)
        {
            if (row < 0)
                return;
            if (row >= s_rowDrawn.Length)
            {
                int size = s_rowDrawn.Length;
                while (size <= row)
                    size *= 2;
                Array.Resize(ref s_rowDrawn, size);
            }
            s_rowDrawn[row] = Time.frameCount;
        }

        // ---- the handlers ---------------------------------------------------------------------------------------

        // BeforeSlotRender, last handler: the window's on-screen render (its slot camera, or the shared one).
        internal static void Before(WindowState s, Camera cam)
        {
            if (s != null)
                Draw(s.Window, cam, ViewKey(s.Window, s.Window, PortalRenderer.WindowRenderLevel));
        }

        // BeforeNestedRender, last handler: a deeper level or a nested window of `owner`'s render, with the shared camera
        // (the window rendered is PortalSystem.RenderingWindow, set by its BeforeRender).
        internal static void BeforeNested(WindowState owner, Camera cam)
        {
            PortalSystem sys = PortalSystem.Instance;
            PortalWindow w = sys != null ? sys.RenderingWindow : null;
            if (w != null)
                Draw(w, cam, ViewKey(owner != null ? owner.Window : null, w, PortalRenderer.WindowRenderLevel));
        }

        // PortalRenderer.BeforePlainRender: a window render that raises neither of the above.
        internal static void BeforePlain(PortalWindow w, Camera cam)
        {
            if (w != null)
                Draw(w, cam, ViewKey(null, w, PortalRenderer.WindowRenderLevel));
        }

        static long ViewKey(PortalWindow owner, PortalWindow w, int level) =>
            ((long)(owner != null ? owner.Id + 1 : 0) << 32) | ((long)(w.Id & 0xFFFFFF) << 8) | (long)(level & 0xFF);

        static void Draw(PortalWindow w, Camera cam, long key)
        {
            if (!FarProxies.Drawing || w == null || cam == null || w.Partner == null)
                return;
            PortalSystem sys = PortalSystem.Instance;
            Camera main = sys != null ? sys.MainForWindows : null;
            // Never the game camera (a lab render through it, a camera carried through a portal).
            if (main == null || ReferenceEquals(cam, main) || !sys.WindowFarPortal(w, out ZDOID far))
                return;
            if (!ProxyTables.Templates.IsCreated || !ProxyTables.Parts.IsCreated || !ProxyTables.Variants.IsCreated || !ProxyTables.VariantParts.IsCreated)
                return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            BeginFrame();
            try
            {
                if (Render(w.Partner.Frame, cam, main, far, key))
                    FarProxies.DrawRan = true;
            }
            catch (Exception e)
            {
                LabErrors++;
                // Nothing proxied may stay without its picture: the proxies stop and their objects are made again.
                FarProxies.Failed(e);
            }
            finally
            {
                s_runOf.Clear();
                s_fadeOf.Clear();
                FarProxies.LabCullMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            }
        }

        // ---- one render ---------------------------------------------------------------------------------------

        // The game camera carried through a portal (PortalSystem.MainPreCull, CameraThroughFrame) stands on that far side and
        // sees it directly: the proxies of the far area round it, as a window onto it draws them, for the game camera only and
        // only on such frames (no exit plane: nothing of its own side is in that view). The lab's in-place reference
        // (Suspended) is left without them, so it shows what the real objects show.
        const long GameCameraKey = -1L;

        internal static void GameCameraThrough(Camera cam)
        {
            PortalSystem sys = PortalSystem.Instance;
            if (!FarProxies.Drawing || cam == null || sys == null || !sys.Areas.AreaOf(cam.transform.position, out Vector2s _, out Vector3 _, out ZDOID far))
                return;
            if (!ProxyTables.Templates.IsCreated || !ProxyTables.Parts.IsCreated || !ProxyTables.Variants.IsCreated || !ProxyTables.VariantParts.IsCreated)
                return;
            BeginFrame();
            try
            {
                Render(null, cam, cam, far, GameCameraKey);
            }
            catch (Exception e)
            {
                LabErrors++;
                FarProxies.Failed(e);
            }
            finally
            {
                s_runOf.Clear();
                s_fadeOf.Clear();
            }
        }

        // True when it went through the spans of the area the window shows. exit: the window's exit portal (what lies on the
        // camera's side of it is not in the view), or null for a view without one.
        static bool Render(PortalFrame exit, Camera cam, Camera main, ZDOID far, long key)
        {
            int culled = Cull(exit, cam.transform.position, cam.cullingMask, cam.projectionMatrix * cam.worldToCameraMatrix, cam.cullingMatrix,
                cam.transform.forward, main, far, key, false);
            if (culled < 0)
                return false;
            if (culled == 0 || s_emits == 0)
                return true;
            Sort();
            Submit(cam);
            return true;
        }

        // The cull and the emission of one view (a camera's, or with `virtualView` a window drawn without one, Win.Virtual):
        // -1 the area has no spans, 0 no candidate span (nothing emitted), 1 emitted (s_emits parts in s_runs). `viewVP` is
        // what is rasterised, `cullVP` what the sun's caster volume is built from, `forward` the view's forward.
        static int Cull(PortalFrame exit, Vector3 camPos, int cullingMask, Matrix4x4 viewVP, Matrix4x4 cullVP, Vector3 forward, Camera main, ZDOID far, long key,
            bool virtualView)
        {
            // A virtual view's runs are recorded right after its own cull, never after another's.
            s_virtualReady = false;
            if (FarProxies.CollectSpans(far, s_spans) == 0)
                return -1;
            Ensure(ref s_planes, 16);
            s_mask = cullingMask;
            s_obliqueClip = Plugin.Clip != null && Plugin.Clip.Value == PortalClip.Oblique;
            s_virtual = virtualView;

            // The view: what the camera rasterises (its projection: the window's rect, the oblique clip).
            GeometryUtility.CalculateFrustumPlanes(viewVP, s_frustum);
            for (int i = 0; i < 6; i++)
                s_planes[i] = PlaneVec(s_frustum[i]);
            // The exit plane, positive on the side the window shows (none: positive everywhere).
            Vector4 exitPlane = new Vector4(0f, 0f, 0f, 1e30f);
            if (exit != null)
            {
                Vector3 shown = exit.SignedDistance(camPos) > 0f ? -exit.Forward : exit.Forward;
                exitPlane = new Vector4(shown.x, shown.y, shown.z, -Vector3.Dot(shown, exit.Center));
            }
            s_planes[6] = exitPlane;

            // The sun's caster volume: the culling frustum's sides and near plane, the shadow distance along the view, the
            // exit plane (shadows land only where the window shows), swept toward the sun.
            Light sun = ShadowLight();
            bool shadows = sun != null;
            Vector3 lightDir = Vector3.down;
            int casterPlanes = 0;
            if (shadows)
            {
                lightDir = sun.transform.forward;
                GeometryUtility.CalculateFrustumPlanes(cullVP, s_frustum);
                for (int i = 0; i < 5; i++)
                    s_planes[7 + i] = PlaneVec(s_frustum[i]);
                Vector3 fwd = forward;
                s_planes[12] = new Vector4(-fwd.x, -fwd.y, -fwd.z, Vector3.Dot(fwd, camPos) + QualitySettings.shadowDistance);
                s_planes[13] = exitPlane;
                casterPlanes = 7;
            }

            // LOD: the game camera's vertical FOV and the game's lodBias (Unity's relative height, times the bias).
            float k = s_lodBias / (2f * Mathf.Tan(main.fieldOfView * 0.5f * Mathf.Deg2Rad));
            View view = ViewOf(key);
            float now = Time.time, dur = s_fadeDuration;
            bool hasPrev = view.PrevFrame >= 0 && dur > 0f && now - view.PrevTime <= dur;

            // Spans culled whole; the rest are candidates with their place in the output.
            s_candCount = 0;
            int total = 0;
            for (int i = 0; i < s_spans.Count; i++)
            {
                ProxySpanRef sp = s_spans[i];
                if (sp.Count <= 0)
                    continue;
                Vector3 c = sp.Bounds.center, ext = sp.Bounds.extents;
                float r = ext.magnitude + Margin;
                bool keep = !AabbOutside(c, ext, 0, 6) && !(Dist(exitPlane, c) < -r);
                if (!keep && shadows)
                    keep = Sweep(s_planes, 7, casterPlanes, c, r, lightDir * CasterReach);
                if (!keep)
                {
                    LabSpansCulled++;
                    continue;
                }
                if (s_candCount == s_cands.Length)
                {
                    int size = s_candCount * 2;
                    Array.Resize(ref s_cands, size);
                    Array.Resize(ref s_candOut, size);
                    Array.Resize(ref s_grouped, size);
                    Array.Resize(ref s_groupFirst, size);
                    Array.Resize(ref s_groupCount, size);
                    Array.Resize(ref s_groupCand, size);
                }
                s_cands[s_candCount] = sp;
                s_candOut[s_candCount] = total;
                s_candCount++;
                total += sp.Count;
            }
            if (s_candCount == 0)
            {
                Remember(view, camPos, k, now);
                return 0;
            }
            LabCandidates += total;
            Ensure(ref s_out, total);
            Ensure(ref s_ranges, s_candCount);

            var job = new ProxyCullJob
            {
                Templates = ProxyTables.Templates,
                LodSets = ProxyTables.LodSets,
                Planes = s_planes,
                Out = s_out,
                Ranges = s_ranges,
                CasterPlanes = casterPlanes,
                // Win.Virtual: a window drawn without a camera draws the proxies of objects that have their instance too
                // (its real renderers reach no camera there).
                Skip = virtualView ? (uint)ProxyFlags.Hidden : (uint)(ProxyFlags.Created | ProxyFlags.Hidden),
                MaxLod = s_maxLod,
                Cam = camPos,
                PrevCam = hasPrev ? view.PrevPos : camPos,
                Light = lightDir * CasterReach,
                K = k,
                PrevK = hasPrev ? view.PrevK : k,
                Margin = Margin,
                HasPrev = hasPrev,
                Shadows = shadows,
                CameraSideCasts = shadows && (s_obliqueClip || WindowFlags.ExitShadows),
            };
            // Ranges grouped by page, all written before any job runs; then one job per page (chained: they share the
            // output), or everything inline when there are few.
            Array.Clear(s_grouped, 0, s_candCount);
            int rangeCount = 0, groups = 0;
            for (int i = 0; i < s_candCount; i++)
            {
                if (s_grouped[i])
                    continue;
                NativeArray<ProxyInst> page = s_cands[i].Page;
                s_groupFirst[groups] = rangeCount;
                s_groupCand[groups] = i;
                for (int j = i; j < s_candCount; j++)
                {
                    if (s_grouped[j] || !s_cands[j].Page.Equals(page))
                        continue;
                    s_grouped[j] = true;
                    s_ranges[rangeCount++] = new CullRange
                    {
                        Start = s_cands[j].Start, Count = s_cands[j].Count, Out = s_candOut[j], DistantOnly = s_cands[j].DistantOnly ? 1 : 0,
                    };
                }
                s_groupCount[groups] = rangeCount - s_groupFirst[groups];
                groups++;
            }
            long j0 = System.Diagnostics.Stopwatch.GetTimestamp();
            JobHandle chain = default;
            bool inline = total <= InlineCandidates;
            for (int g = 0; g < groups; g++)
            {
                job.Page = s_cands[s_groupCand[g]].Page;
                job.RangeBase = s_groupFirst[g];
                int n = s_groupCount[g];
                if (inline)
                {
                    for (int r = 0; r < n; r++)
                        job.Execute(r);
                }
                else
                    chain = job.Schedule(n, 1, chain);
            }
            if (!inline)
            {
                JobHandle.ScheduleBatchedJobs();
                chain.Complete();
            }
            long j1 = System.Diagnostics.Stopwatch.GetTimestamp();

            // Emission: per instance its shown levels and fade values, per part its run.
            s_emits = 0;
            s_runCount = 0;
            s_fadeCount = 1;
            s_fadeBlocksUsed = 0;
            int visible = 0, casters = 0;
            for (int i = 0; i < s_candCount; i++)
            {
                NativeArray<ProxyInst> page = s_cands[i].Page;
                int start = s_cands[i].Start, count = s_cands[i].Count, outBase = s_candOut[i];
                for (int j = 0; j < count; j++)
                {
                    CullOut o = s_out[outBase + j];
                    if (o.Vis == 0)
                        continue;
                    ProxyInst inst = page[start + j];
                    if (o.Vis == 1)
                        visible++;
                    else
                        casters++;
                    // (Only a virtual view emits a Created one: its object is there, so its proxy is not one the handoff asks for.)
                    if (EmitInstance(in inst, o, view, hasPrev, now, dur) && o.Vis == 1 && (inst.Flags & (uint)ProxyFlags.Created) == 0)
                        MarkDrawn(inst.Row);
                }
            }
            Remember(view, camPos, k, now);
            LabVisible += visible;
            LabCasters += casters;
            if (virtualView)
            {
                WindowScreen.LabProf[0] += (j1 - j0) * WindowScreen.TickMs;
                WindowScreen.LabProf[1] += (System.Diagnostics.Stopwatch.GetTimestamp() - j1) * WindowScreen.TickMs;
            }
            return 1;
        }

        static void Remember(View view, Vector3 camPos, float k, float now)
        {
            view.PrevPos = camPos;
            view.PrevK = k;
            view.PrevTime = now;
            view.PrevFrame = Time.frameCount;
            view.UsedAt = Time.unscaledTime;
        }

        static View ViewOf(long key)
        {
            for (int i = 0; i < s_views.Count; i++)
                if (s_views[i].Key == key)
                    return s_views[i];
            var v = new View { Key = key, UsedAt = Time.unscaledTime };
            s_views.Add(v);
            return v;
        }

        // The light whose shadows the window's view gets (the game's sun or moon), or null when it casts none now.
        static Light ShadowLight()
        {
            if (QualitySettings.shadows == ShadowQuality.Disable || QualitySettings.shadowDistance <= 0f)
                return null;
            EnvMan env = EnvMan.instance;
            Light l = env != null ? env.m_dirLight : null;
            if (l == null)
                l = RenderSettings.sun;
            if (l == null || !l.isActiveAndEnabled || l.shadows == LightShadows.None || l.intensity <= 0f || l.type != LightType.Directional)
                return null;
            return l;
        }

        // ---- cross-fade and parts ------------------------------------------------------------------------------

        // The levels the instance shows (a: the one fading out or the only one, b: the one fading in) with their
        // unity_LODFade.x, and its parts emitted. True when any part was.
        static bool EmitInstance(in ProxyInst inst, CullOut o, View view, bool hasPrev, float now, float dur)
        {
            // Its LOD rule (the variant's), or the template's fields with no LOD.
            ProxyTemplateData td = inst.LodSet >= 0 ? ProxyTables.LodSets[inst.LodSet] : ProxyTables.Templates[inst.Template];
            byte a = NoLevel, b = NoLevel;
            float xa = 0f, xb = 0f;
            if (td.LodCount > 0)
            {
                byte target = o.Lod;
                // LodFadeInOut: hidden (its reference point pushed away) until the delay is over.
                float show = 0f;
                bool born = td.LodFadeInOut && s_born.Count > 0 && s_born.TryGetValue(inst.Row, out show);
                if (born)
                    show += BornDelay(inst.Row);
                if (born && now < show)
                    target = Culled;
                if (td.FadeMode == (int)LODFadeMode.CrossFade && td.AnimateCrossFading && dur > 0f)
                {
                    bool running = view.Fades.TryGetValue(inst.Row, out Fade f);
                    if (running && f.To != target)
                    {
                        // Switched again mid-fade: from where it was going.
                        f = new Fade { From = f.To, To = target, Start = now };
                        view.Fades[inst.Row] = f;
                    }
                    else if (!running)
                    {
                        byte from;
                        float start = now;
                        if (born && now >= show && (!hasPrev || view.PrevTime < show))
                        {
                            from = Culled;
                            start = show;
                        }
                        else
                            from = hasPrev ? (born && view.PrevTime < show ? Culled : o.Prev) : target;
                        if (from != target && now - start < dur)
                        {
                            f = new Fade { From = from, To = target, Start = start };
                            view.Fades[inst.Row] = f;
                            running = true;
                        }
                    }
                    float p = running ? (now - f.Start) / dur : 1f;
                    if (running && p >= 1f)
                    {
                        view.Fades.Remove(inst.Row);
                        running = false;
                    }
                    if (running)
                    {
                        a = f.From;
                        b = f.To;
                        xa = 1f - Mathf.Max(p, 0f);
                        xb = -xa;
                        LabFading++;
                    }
                    else
                        a = target;
                }
                else
                {
                    a = target;
                    // The fadeTransitionWidth band: this level fades out (f), the next one (or nothing) in (-f).
                    if (td.FadeMode == (int)LODFadeMode.CrossFade && target != Culled && target == o.Lod && o.Fade > 0f)
                    {
                        xa = o.Fade;
                        xb = -o.Fade;
                        b = target + 1 < td.LodCount ? (byte)(target + 1) : Culled;
                        LabFading++;
                    }
                }
            }
            ProxyVariantData v = ProxyTables.Variants[inst.Variant];
            int end = v.PartStart + v.PartCount;
            bool any = false;
            for (int k = v.PartStart; k < end; k++)
            {
                ProxyVariantPart vp = ProxyTables.VariantParts[k];
                float x = 0f;
                if (td.LodCount > 0 && vp.LodMask != ProxyPartData.LodAll)
                {
                    bool inA = a < 8 && (vp.LodMask & (1 << a)) != 0;
                    bool inB = b < 8 && (vp.LodMask & (1 << b)) != 0;
                    if (inA && inB)
                        x = 0f;
                    else if (inA)
                        x = xa;
                    else if (inB)
                        x = xb;
                    else
                        continue;
                    // Wholly shown (the fade's first instant) or wholly gone.
                    if (x >= 1f)
                        x = 0f;
                    else if (x <= -1f)
                        continue;
                }
                any |= Emit(in inst, in vp, o.Vis, x);
            }
            return any;
        }

        static bool Emit(in ProxyInst inst, in ProxyVariantPart vp, byte vis, float x)
        {
            ProxyBatch batch = ProxyTables.Batch(vp.Batch);
            if (batch == null || (uint)batch.Layer >= 32u || (s_mask & (1 << batch.Layer)) == 0)
                return false;
            ShadowCastingMode mode;
            if (vis == 1)
                mode = batch.Shadows;
            else if (batch.Shadows == ShadowCastingMode.Off)
                return false;
            else
                // Out of view: a caster only. Two-sided stays two-sided where its renderer would keep its mode (outside the
                // frustum, or behind the exit under the oblique clip); HideBehindExit makes it ShadowsOnly.
                mode = batch.Shadows == ShadowCastingMode.TwoSided && (vis == 2 || s_obliqueClip) ? ShadowCastingMode.TwoSided : ShadowCastingMode.ShadowsOnly;
            int fade = x == 0f ? 0 : FadeIndex(x);
            long key = (long)vp.Batch | ((long)vp.Mpb << 20) | ((long)mode << 40) | ((long)fade << 42);
            if (!s_runOf.TryGetValue(key, out int run))
            {
                run = s_runCount++;
                if (run == s_runs.Length)
                {
                    Array.Resize(ref s_runs, run * 2);
                    Array.Resize(ref s_farRuns, run * 2);
                }
                s_runs[run] = new Run
                {
                    Batch = vp.Batch, Mpb = vp.Mpb, Fade = fade, Mode = mode,
                    Min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), Max = new Vector3(float.MinValue, float.MinValue, float.MinValue),
                };
                s_runOf.Add(key, run);
            }
            ref Run r = ref s_runs[run];
            r.Count++;
            Vector3 c = inst.Center;
            float rad = inst.Radius;
            r.Min = Vector3.Min(r.Min, new Vector3(c.x - rad, c.y - rad, c.z - rad));
            r.Max = Vector3.Max(r.Max, new Vector3(c.x + rad, c.y + rad, c.z + rad));
            int e = s_emits++;
            if (e == s_emitRun.Length)
            {
                Array.Resize(ref s_emitRun, e * 2);
                Array.Resize(ref s_emitM, e * 2);
            }
            s_emitRun[e] = run;
            s_emitM[e] = inst.Root * ProxyTables.Parts[vp.Part].Local;
            // Win.Virtual: its instance's sphere, for the cascades' caster tests.
            if (s_virtual)
            {
                if (e >= s_emitS.Length)
                    Array.Resize(ref s_emitS, s_emitRun.Length);
                s_emitS[e] = new Vector4(c.x, c.y, c.z, rad);
            }
            return true;
        }

        static int FadeIndex(float x)
        {
            if (s_fadeOf.TryGetValue(x, out int i))
                return i;
            i = s_fadeCount++;
            if (i == s_fadeVals.Length)
                Array.Resize(ref s_fadeVals, i * 2);
            s_fadeVals[i] = x;
            s_fadeOf.Add(x, i);
            return i;
        }

        // ---- runs ------------------------------------------------------------------------------------------------

        // Counting sort of the emitted matrices by run into one array; the runs cut from it as FarRuns.
        static void Sort()
        {
            Ensure(ref s_sorted, s_emits);
            if (s_virtual)
                Ensure(ref s_sortedS, s_emits);
            int at = 0;
            for (int r = 0; r < s_runCount; r++)
            {
                s_runs[r].Start = at;
                s_runs[r].Cursor = 0;
                at += s_runs[r].Count;
            }
            for (int e = 0; e < s_emits; e++)
            {
                ref Run r = ref s_runs[s_emitRun[e]];
                int to = r.Start + r.Cursor++;
                s_sorted[to] = s_emitM[e];
                if (s_virtual)
                    s_sortedS[to] = s_emitS[e];
            }
            int version = FarProxies.Version;
            for (int i = 0; i < s_runCount; i++)
            {
                ref Run r = ref s_runs[i];
                ProxyBatch b = ProxyTables.Batch(r.Batch);
                Vector3 size = r.Max - r.Min;
                s_farRuns[i] = new FarRun
                {
                    Mesh = b.Mesh,
                    Submesh = b.Submesh,
                    Material = b.Material,
                    PassIndex = -1,
                    Pass = r.Mode == ShadowCastingMode.ShadowsOnly ? FarPass.ShadowCaster : FarPass.Deferred,
                    Lod = 0,
                    Shadow = r.Mode,
                    MpbBucket = r.Mpb,
                    Matrices = new NativeSlice<Matrix4x4>(s_sorted, r.Start, r.Count),
                    Bounds = new Bounds(r.Min + size * 0.5f, size),
                    Version = version,
                };
            }
            LabRuns += s_runCount;
        }

        static void Submit(Camera cam)
        {
            // One template, its fields set per run: the constructor reads the default rendering layer mask natively.
            var rp = new RenderParams(null) { camera = cam };
            int calls = 0, instances = 0;
            for (int i = 0; i < s_runCount; i++)
            {
                ref Run r = ref s_runs[i];
                ref FarRun fr = ref s_farRuns[i];
                ProxyBatch b = ProxyTables.Batch(r.Batch);
                if (b == null || b.Mesh == null || b.Material == null)
                    continue;
                MatEntry me = EntryOf(b.Material);
                if (!me.Ok)
                {
                    // Not instanceable after all (its shader changed): its states stay real from now on.
                    FarProxies.RejectMaterial(b.Material);
                    continue;
                }
                Resync(me);
                float x = r.Fade > 0 ? s_fadeVals[r.Fade] : 0f;
                bool fading = x != 0f && me.Fade != null;
                rp.material = fading ? me.Fade : me.Draw;
                rp.matProps = fading ? FadeBlock(r.Mpb, x) : ProxyTables.Mpb(r.Mpb);
                rp.layer = b.Layer;
                rp.shadowCastingMode = r.Mode;
                rp.receiveShadows = b.ReceiveShadows;
                rp.lightProbeUsage = b.LightProbes;
                rp.reflectionProbeUsage = b.ReflectionProbes;
                rp.motionVectorMode = b.MotionVectors == MotionVectorGenerationMode.ForceNoMotion ? MotionVectorGenerationMode.ForceNoMotion : MotionVectorGenerationMode.Camera;
                rp.renderingLayerMask = b.RenderingLayerMask;
                rp.worldBounds = fr.Bounds;
                int left = r.Count, start = r.Start;
                try
                {
                    while (left > 0)
                    {
                        int n = Math.Min(left, MaxChunk);
                        Graphics.RenderMeshInstanced(rp, b.Mesh, b.Submesh, s_sorted, n, start);
                        start += n;
                        left -= n;
                        calls++;
                        instances += n;
                    }
                }
                catch (Exception e)
                {
                    LabErrors++;
                    if (Time.unscaledTime >= s_errorAt)
                    {
                        s_errorAt = Time.unscaledTime + 10f;
                        Plugin.Log.LogWarning($"FarProxies: submitting {b} to {cam.name} failed (its states stay real from now on): {e.Message}");
                    }
                    FarProxies.RejectMaterial(b.Material);
                }
            }
            if (calls > 0)
                LabRenders++;
            FarProxies.LabDrawCalls += calls;
            FarProxies.LabDrawInstances += instances;
            FarProxies.LabInstanced += instances;
        }

        // A pooled block with exactly the variant's values (MaterialMan's) and unity_LODFade (x the fade value, y it
        // quantised to 16 levels as Unity's built-in variable documents).
        static MaterialPropertyBlock FadeBlock(int mpb, float x)
        {
            MaterialPropertyBlock block;
            if (s_fadeBlocksUsed < s_fadeBlocks.Count)
                block = s_fadeBlocks[s_fadeBlocksUsed];
            else
            {
                block = new MaterialPropertyBlock();
                s_fadeBlocks.Add(block);
            }
            s_fadeBlocksUsed++;
            block.Clear();
            ProxyMpb values = ProxyTables.MpbOf(mpb);
            if (values != null)
                foreach (ProxyMpbValue v in values.Values)
                {
                    if (v.Kind == ProxyMpbValue.Int)
                        block.SetInt(v.Id, (int)v.Value.x);
                    else if (v.Kind == ProxyMpbValue.Float)
                        block.SetFloat(v.Id, v.Value.x);
                    else
                        block.SetVector(v.Id, v.Value);
                }
            float y = Mathf.Sign(x) * Mathf.Floor(Mathf.Abs(x) * 16f) / 16f;
            block.SetVector(s_lodFadeId, new Vector4(x, y, 0f, 0f));
            return block;
        }

        // ---- geometry ----------------------------------------------------------------------------------------------

        static Vector4 PlaneVec(Plane p) => new Vector4(p.normal.x, p.normal.y, p.normal.z, p.distance);

        static float Dist(Vector4 p, Vector3 c) => p.x * c.x + p.y * c.y + p.z * c.z + p.w;

        // An AABB wholly behind one of the planes [from, from + count).
        static bool AabbOutside(Vector3 c, Vector3 ext, int from, int count)
        {
            for (int i = from; i < from + count; i++)
            {
                Vector4 p = s_planes[i];
                float reach = Mathf.Abs(p.x) * ext.x + Mathf.Abs(p.y) * ext.y + Mathf.Abs(p.z) * ext.z + Margin;
                if (Dist(p, c) < -reach)
                    return true;
            }
            return false;
        }

        // The sphere (c, r) swept along `ray` (toward where its shadow falls) meets the convex volume of the planes
        // [from, from + count): the segment clipped against each plane grown by r (Liang-Barsky).
        internal static bool Sweep(NativeArray<Vector4> planes, int from, int count, Vector3 c, float r, Vector3 ray)
        {
            float t0 = 0f, t1 = 1f;
            for (int i = from; i < from + count; i++)
            {
                Vector4 p = planes[i];
                float a = p.x * c.x + p.y * c.y + p.z * c.z + p.w + r;
                float b = p.x * ray.x + p.y * ray.y + p.z * ray.z;
                if (b == 0f)
                {
                    if (a < 0f)
                        return false;
                    continue;
                }
                float t = -a / b;
                if (b > 0f)
                {
                    if (t > t0)
                        t0 = t;
                }
                else if (t < t1)
                    t1 = t;
                if (t0 > t1)
                    return false;
            }
            return true;
        }

        static void Ensure<T>(ref NativeArray<T> a, int need) where T : struct
        {
            int cap = a.IsCreated ? a.Length : 0;
            if (need <= cap)
                return;
            int size = Math.Max(64, cap * 2);
            while (size < need)
                size *= 2;
            if (a.IsCreated)
                a.Dispose();
            a = new NativeArray<T>(size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        // ---- Win.Virtual: a window drawn without a camera (WindowScreenContent; FARWORLD-V3 §4.4 A2 body) -------------------
        // The same cull, LOD, cross-fade and runs as a slot render of the window, from C' (VirtualView), with one difference
        // that makes it what a camera at C' would see: the proxies of objects whose instance stands are drawn as well (their
        // real renderers reach no camera here; their proxy is their exact picture). The sun's caster volume is the rect's,
        // as a slot render's: the window's pixels are the only ones that read the shadows. The runs are then recorded
        // into the window's command buffer: each material's Deferred pass with the far ambient in its block
        // (WindowScreen.AddAmbient), and per cascade its ShadowCaster pass for the instances whose sphere can cast into it.
        // The same instancing materials as Submit (the material itself, its instancing copy, the cross-fade copy).

        static readonly Matrix4x4[] s_chunk = new Matrix4x4[MaxChunk];
        static readonly MaterialPropertyBlock s_virtualBlock = new MaterialPropertyBlock();
        // The runs of the last CollectVirtual are there to record.
        static bool s_virtualReady;
        // Lab: virtual views culled, their G-buffer and caster draws, runs left out for a material without the pass.
        internal static long LabVirtualViews, LabVirtualBodyDraws, LabVirtualCasterDraws, LabVirtualNoPass;

        /// <summary>Win.Virtual: the cull and runs of view `v` of the far area round `far`; true when there is anything to
        /// record (RecordVirtualBody, RecordVirtualCasters; valid until the next window render).</summary>
        internal static bool CollectVirtual(in VirtualView v, ZDOID far)
        {
            s_virtualReady = false;
            if (!FarProxies.Drawing || v.Window == null)
                return false;
            if (!ProxyTables.Templates.IsCreated || !ProxyTables.Parts.IsCreated || !ProxyTables.Variants.IsCreated || !ProxyTables.VariantParts.IsCreated)
                return false;
            PortalSystem sys = PortalSystem.Instance;
            Camera main = sys != null ? sys.MainForWindows : null;
            if (main == null)
                return false;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            BeginFrame();
            try
            {
                long key = ViewKey(v.HasState ? v.Window : null, v.Window, 0);
                // The sun's caster volume from the rect's frustum too: only the window's pixels read the shadows, so a
                // caster whose shadow cannot reach them changes nothing (the slot camera culls by its rect as well).
                Matrix4x4 rectVP = PortalMath.Scissor(v.Rect) * v.Proj * v.View;
                int culled = Cull(v.Exit, v.Eye, v.CullingMask, rectVP, rectVP, v.Forward, main, far, key, true);
                if (culled >= 0)
                    FarProxies.DrawRan = true;
                if (culled > 0 && s_emits > 0)
                {
                    long s0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    Sort();
                    WindowScreen.LabProf[2] += (System.Diagnostics.Stopwatch.GetTimestamp() - s0) * WindowScreen.TickMs;
                    s_virtualReady = true;
                }
                LabVirtualViews++;
                return s_virtualReady;
            }
            catch (Exception e)
            {
                LabErrors++;
                FarProxies.Failed(e);
                return false;
            }
            finally
            {
                s_virtual = false;
                s_runOf.Clear();
                s_fadeOf.Clear();
                FarProxies.LabCullMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            }
        }

        /// <summary>Win.Virtual: the collected runs that are seen (not shadows only) into `cb` with their Deferred pass.</summary>
        internal static int RecordVirtualBody(CommandBuffer cb)
        {
            if (!s_virtualReady)
                return 0;
            int draws = 0;
            for (int i = 0; i < s_runCount; i++)
                if (s_runs[i].Mode != ShadowCastingMode.ShadowsOnly)
                    draws += RecordRun(cb, i, false, default);
            LabVirtualBodyDraws += draws;
            return draws;
        }

        /// <summary>Win.Virtual: the collected runs that cast shadows into `cb` with their ShadowCaster pass, only the
        /// instances that can cast into cascade `c`.</summary>
        internal static int RecordVirtualCasters(CommandBuffer cb, in VirtualCascade c)
        {
            if (!s_virtualReady)
                return 0;
            int draws = 0;
            for (int i = 0; i < s_runCount; i++)
                if (s_runs[i].Mode != ShadowCastingMode.Off)
                    draws += RecordRun(cb, i, true, c);
            LabVirtualCasterDraws += draws;
            return draws;
        }

        static int RecordRun(CommandBuffer cb, int run, bool caster, in VirtualCascade c)
        {
            ref Run r = ref s_runs[run];
            ProxyBatch b = ProxyTables.Batch(r.Batch);
            if (b == null || b.Mesh == null || b.Material == null)
                return 0;
            MatEntry me = EntryOf(b.Material);
            if (!me.Ok)
                return 0;
            Resync(me);
            float x = r.Fade > 0 ? s_fadeVals[r.Fade] : 0f;
            bool fading = x != 0f && me.Fade != null;
            Material mat = fading ? me.Fade : me.Draw;
            Vector2Int passes = VirtualPasses.Of(mat);
            int pass = caster ? passes.y : passes.x;
            if (pass < 0)
            {
                LabVirtualNoPass++;
                return 0;
            }
            int draws = 0, n = 0, end = r.Start + r.Count;
            if (!caster)
            {
                for (int at = r.Start; at < end; at += MaxChunk)
                {
                    n = Math.Min(MaxChunk, end - at);
                    NativeArray<Matrix4x4>.Copy(s_sorted, at, s_chunk, 0, n);
                    draws += Flush(cb, b, mat, pass, r.Mpb, fading ? x : 0f, n, false);
                }
                return draws;
            }
            for (int at = r.Start; at < end; at++)
            {
                Vector4 s = s_sortedS[at];
                if (!c.Casts(new Vector3(s.x, s.y, s.z), s.w))
                    continue;
                s_chunk[n++] = s_sorted[at];
                if (n == MaxChunk)
                {
                    draws += Flush(cb, b, mat, pass, r.Mpb, fading ? x : 0f, n, true);
                    n = 0;
                }
            }
            if (n > 0)
                draws += Flush(cb, b, mat, pass, r.Mpb, fading ? x : 0f, n, true);
            return draws;
        }

        // One instanced draw of s_chunk[0..n): the variant's block values, the cross-fade's unity_LODFade (as FadeBlock), and
        // for the G-buffer the far ambient per instance. The command buffer copies the block, so one serves every draw.
        static int Flush(CommandBuffer cb, ProxyBatch b, Material mat, int pass, int mpb, float fade, int n, bool caster)
        {
            MaterialPropertyBlock block = s_virtualBlock;
            block.Clear();
            ProxyMpb values = ProxyTables.MpbOf(mpb);
            if (values != null)
                foreach (ProxyMpbValue v in values.Values)
                {
                    if (v.Kind == ProxyMpbValue.Int)
                        block.SetInt(v.Id, (int)v.Value.x);
                    else if (v.Kind == ProxyMpbValue.Float)
                        block.SetFloat(v.Id, v.Value.x);
                    else
                        block.SetVector(v.Id, v.Value);
                }
            if (fade != 0f)
            {
                float y = Mathf.Sign(fade) * Mathf.Floor(Mathf.Abs(fade) * 16f) / 16f;
                block.SetVector(s_lodFadeId, new Vector4(fade, y, 0f, 0f));
            }
            if (!caster)
                WindowScreen.AddAmbient(block, n);
            cb.DrawMeshInstanced(b.Mesh, b.Submesh, mat, pass, s_chunk, n, block);
            return 1;
        }

        // ---- lab ---------------------------------------------------------------------------------------------------

        // The last render's runs (FarDrawList types; valid until the next window render).
        internal static int LastRunCount => s_runCount;
        internal static FarRun LastRun(int i) => s_farRuns[i];

        internal static string Describe() =>
            $"drawn into {LabRenders} renders ({LabCandidates} candidates, {LabVisible} in view, {LabCasters} shadow casters only, {LabFading} fading, " +
            $"{LabRuns} runs, {LabSpansCulled} spans culled whole, {LabErrors} errors), views {s_views.Count}, born {s_born.Count}, materials " +
            $"{s_mats.Count} ({LabCloneMaterials} instancing copies, {LabFadeMaterials} fade copies), handoff skips {LabHandoffSkips}, " +
            $"first-frame checks {LabFirstFrameChecks}, LOD bias {s_lodBias:0.##}, max LOD {s_maxLod}, fade {s_fadeDuration:0.###} s";

        internal static void LabResetCounters()
        {
            LabRenders = LabCandidates = LabVisible = LabCasters = LabFading = LabRuns = LabSpansCulled = LabErrors = 0;
            LabHandoffSkips = LabFirstFrameChecks = 0;
        }

        // ---- the cull job ----------------------------------------------------------------------------------------

        // One span's instances: [Start, Start + Count) of the page, output from Out on; DistantOnly: a Ring span.
        struct CullRange
        {
            public int Start, Count, Out, DistantOnly;
        }

        // Per instance: Vis 0 not drawn, 1 in view, 2 a caster outside the view, 3 a caster on the camera's side of the exit;
        // Lod the level (NoLevel without a LODGroup, Culled), Prev the level at the view's previous render; Fade the
        // fadeTransitionWidth band's value (0: none).
        internal struct CullOut
        {
            public byte Vis, Lod, Prev, Pad;
            public float Fade;
        }

        // Planes: [0, 6) the view, 6 the exit (positive where the window shows), [7, 7 + CasterPlanes) the caster volume.
        struct ProxyCullJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<ProxyInst> Page;
            [ReadOnly] public NativeArray<CullRange> Ranges;
            [ReadOnly] public NativeArray<ProxyTemplateData> Templates;
            [ReadOnly] public NativeArray<ProxyTemplateData> LodSets;
            [ReadOnly] public NativeArray<Vector4> Planes;
            [NativeDisableParallelForRestriction] public NativeArray<CullOut> Out;
            public int RangeBase, CasterPlanes, MaxLod;
            public Vector3 Cam, PrevCam, Light;
            public float K, PrevK, Margin;
            public bool HasPrev, Shadows, CameraSideCasts;
            // The flags that leave an instance out (Created | Hidden; a virtual view, Win.Virtual: Hidden alone).
            public uint Skip;

            public void Execute(int index)
            {
                CullRange r = Ranges[RangeBase + index];
                uint skip = Skip;
                for (int i = 0; i < r.Count; i++)
                {
                    ProxyInst inst = Page[r.Start + i];
                    var o = new CullOut();
                    uint f = inst.Flags;
                    if ((f & skip) == 0 && (r.DistantOnly == 0 || (f & (uint)ProxyFlags.Distant) != 0))
                    {
                        Vector3 c = inst.Center;
                        float rad = inst.Radius + Margin;
                        Vector4 e = Planes[6];
                        byte vis;
                        if (e.x * c.x + e.y * c.y + e.z * c.z + e.w < -rad)
                            vis = CameraSideCasts && Sweep(Planes, 7, CasterPlanes, c, rad, Light) ? (byte)3 : (byte)0;
                        else if (Inside(c, rad))
                            vis = 1;
                        else
                            vis = Shadows && Sweep(Planes, 7, CasterPlanes, c, rad, Light) ? (byte)2 : (byte)0;
                        o.Vis = vis;
                        if (vis != 0)
                        {
                            ProxyTemplateData td = inst.LodSet >= 0 ? LodSets[inst.LodSet] : Templates[inst.Template];
                            if (td.LodCount == 0)
                                o.Lod = o.Prev = NoLevel;
                            else
                            {
                                o.Lod = Select(in td, inst.LodSize, Vector3.Distance(Cam, inst.LodRef), K, out o.Fade);
                                o.Prev = HasPrev ? Select(in td, inst.LodSize, Vector3.Distance(PrevCam, inst.LodRef), PrevK, out _) : o.Lod;
                            }
                        }
                    }
                    Out[r.Out + i] = o;
                }
            }

            bool Inside(Vector3 c, float rad)
            {
                for (int i = 0; i < 6; i++)
                {
                    Vector4 p = Planes[i];
                    if (p.x * c.x + p.y * c.y + p.z * c.z + p.w < -rad)
                        return false;
                }
                return true;
            }

            // LODGroup's rule: relative height = size / (distance * 2 tan(fov / 2)) * lodBias (k holds all but size and
            // distance); the first level from maximumLODLevel whose transition height it reaches, culled below the last.
            // With a fadeTransitionWidth cross-fade: inside the band at the bottom of the level's range, the fade value.
            byte Select(in ProxyTemplateData td, float size, float d, float k, out float fade)
            {
                fade = 0f;
                float rel = d > 1e-6f ? size * k / d : float.MaxValue;
                int n = td.LodCount;
                for (int i = Math.Min(MaxLod, n - 1); i < n; i++)
                {
                    float h = td.Height(i);
                    if (rel < h)
                        continue;
                    if (td.FadeMode == (int)LODFadeMode.CrossFade && !td.AnimateCrossFading)
                    {
                        float upper = i == 0 ? 1f : td.Height(i - 1);
                        float zone = td.FadeWidth(i) * (upper - h);
                        if (zone > 0f && rel < h + zone)
                            fade = (rel - h) / zone;
                    }
                    return (byte)i;
                }
                return Culled;
            }
        }
    }
}
