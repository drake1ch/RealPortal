using System.Collections.Generic;
using HarmonyLib;
using RealPortal.Engine;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal
{
    // OWNER: slots (S2, Grass.Targeted). Window grass with camera-targeted draws. While Grass.Targeted is effective
    // (WindowMechanisms.Effective) every slot camera is excluded from the engine's hand-on
    // (RealPortal.Engine.GrassCameraModule.ExcludeCamera; RemoveExcludedCamera when it goes off or the camera goes), and
    // in BeforeSlotRender (the LAST handler: P3(ii), a camera-targeted draw is consumed by the camera's first Render() of
    // the frame and leaks into its next frame if it does not render) it gets, when GrassCameraModule.TargetedThisFrame,
    // each FrameBatch(i) its culling mask takes and its view contains (culling sphere vs the slot camera's frustum),
    // thinned for the slot camera's position (GrassCameraModule.ViewCount with the renderer's own LOD distances), with
    // Graphics.RenderMeshInstanced(RenderParams { camera = slot camera, layer = batch layer, shadowCastingMode = Off,
    // receiveShadows, matProps, worldBounds }). When TargetedThisFrame is false the batches went out with the null camera
    // and the slot camera already draws them: submit nothing. Never SubmitFrameTo (no view test, every batch). Clutter that
    // casts shadows is not in FrameBatch (the engine keeps its null-camera draw). Docs\S2S3-CONTRACTS.md §7.6.
    //
    // The engine's GrassMainCameraOnly is what makes the game camera's grass camera-targeted (and recorded) at all; it
    // is off by default because without excluded cameras it only adds cost. So FrameSync switches it on when Grass.Targeted
    // comes into force, and back off when it goes, if it was this class that switched it on (a value the player or the lab
    // set stays). Features.Set saves the switch in the engine's config at once, so "we switched it on" is saved too, in
    // Real Portal's own config (Plugin.GrassEngineOwned), before the engine's switch is turned on: after a crash or a
    // killed game (no OnDestroy, so no Clear) the next session still knows the engine's switch is ours, and turns it off
    // whenever Grass.Targeted is not in force, as the rollback (Grass.Targeted or Win.CameraSlots off) promises.
    //
    // Cost: the frustum test reads the batch's recorded culling sphere (no native call); a renderer's position (for the
    // thinning) is read once per frame for all slots; ViewCount's formula is applied here with that position (the same
    // arithmetic as GrassCameraModule.ViewCount(renderer, viewPos, min, max), without its transform read per call).
    // The engine-side thinning of the game camera (m_lodCount) eases in over a second; a slot's count is the target
    // directly (the far side is seen as the game would draw it settled).
    // The RP InstanceRendererUpdate prefix (Patches) stays: it lets window-visible patches past the game camera's frustum
    // test and LOD cut, so they are drawn for the game camera and therefore recorded in FrameBatch.
    //
    // Win.BaseHidesFar (Docs\PARITY-PLAN.md P14.2), while Grass.Targeted is in force and the engine's camera-targeted
    // draws are in place (SlotOnly, decided at a frame's first patch): the game camera's grass is the game's. The prefix no
    // longer lets a window's patch past the game camera's frustum test and LOD (that drew window-density grass into the
    // base view); it notes the patch here instead (SlotOnlyUpdate), and a patch outside the circle the game grows round the
    // player (a far point's, which the game would not have) is not updated for the game camera at all. Each slot camera
    // then gets, besides the game camera's recorded batches, the noted patches the game camera did not draw this frame,
    // by its own view and thinning; the shared camera (a window without a slot) gets those too. Not while the game camera
    // looks at a far side (through a portal, or the lab's in-place reference): it draws that grass itself then, as before.
    // Off: the prefix as before.
    // Grass.TargetedEase (P47): a slot's count of a thinned patch eases toward its target by the time since that slot last
    // drew it, as InstanceRenderer.CustomUpdate eases m_lodCount by deltaTime (the whole count in a second); first seen,
    // or after a second away, it is the target (settled, as the gate's catch-up gives a patch after a gap). Off: the
    // target at once.
    internal static class FarGrassSubmit
    {
        // Lab: batches submitted, renders that got any, instances drawn; batches left out by a slot's view and by its mask.
        internal static long LabBatches, LabRenders, LabInstances, LabSkippedView, LabSkippedMask, LabErrors;
        // Lab (Win.BaseHidesFar): patches noted for the windows, those submitted to a window camera (the game camera had
        // not drawn them), far patches kept from the game camera.
        internal static long LabNoted, LabNotesDrawn, LabFarSkipped;

        // Grass.TargetedEase: a count eased as the game's m_lodCount, and when it was last drawn (Time.time).
        struct Eased
        {
            public float Count, At;
        }

        // The game eases a whole count in a second: past that a gap leaves it settled.
        const float EaseGap = 1f;

        sealed class Slot
        {
            public Camera Cam;
            // In the engine's excluded list (by us).
            public bool Excluded;
            // Grass.TargetedEase: this camera's counts per patch.
            public readonly Dictionary<InstanceRenderer, Eased> Counts = new Dictionary<InstanceRenderer, Eased>();

            // The game's ease (InstanceRenderer.CustomUpdate: MoveTowards by deltaTime times the instance count), the time
            // being since this camera drew the patch.
            public int Ease(InstanceRenderer r, int target, int total)
            {
                float now = Time.time;
                if (Counts.TryGetValue(r, out Eased e) && now - e.At <= EaseGap)
                {
                    if (now > e.At)
                        e.Count = Mathf.MoveTowards(e.Count, target, (now - e.At) * total);
                }
                else
                    e.Count = target;
                e.Count = Mathf.Min(e.Count, total);
                e.At = now;
                Counts[r] = e;
                return (int)e.Count;
            }
        }

        static readonly List<Slot> s_slots = new List<Slot>();
        // Grass.Targeted in force at the last FrameSync, whether GrassMainCameraOnly was switched on here (this session or,
        // read from the saved flag at the first FrameSync, an earlier one that ended without Clear), and whether that was read.
        static bool s_wanted, s_engineOwned, s_ownedLoaded;
        static readonly Plane[] s_planes = new Plane[6];
        // This frame's batches, copied once for all slots (GrassCameraModule.FrameBatch checks the frame natively on every
        // call), and per batch the renderer's position (read once per frame, only for batches some slot sees).
        static GrassCameraModule.Batch[] s_batches = new GrassCameraModule.Batch[256];
        static int s_count, s_copiedFrame = -1;
        static Vector3[] s_pos = new Vector3[256];
        static bool[] s_posRead = new bool[256];

        // Win.BaseHidesFar: this frame's noted patches (in a window's view of last frame), their culling sphere and whether
        // it is valid (a renderer the game does not cull has none: every view gets it), whether the game camera drew them
        // (then the slots get them as before: recorded, or the null camera's), and their position once read.
        static InstanceRenderer[] s_noteR = new InstanceRenderer[256];
        static BoundingSphere[] s_noteB = new BoundingSphere[256];
        static bool[] s_noteCulls = new bool[256], s_noteInMain = new bool[256], s_noteDrawn = new bool[256], s_notePosRead = new bool[256];
        static Vector3[] s_notePos = new Vector3[256];
        static int s_notes;
        // SlotOnly for this frame, and the circle the game grows its grass in (ClutterSystem.LateUpdate's centre, the reach
        // of its patches).
        static int s_slotOnlyFrame = -1;
        static bool s_slotOnly;
        static Vector3 s_clutterAt;
        static float s_clutterReach;
        static int s_clutterLayer = -1;
        static float s_easeSweepAt;
        static readonly List<InstanceRenderer> s_easeGone = new List<InstanceRenderer>();
        static readonly System.Action<InstanceRenderer> s_updateBounds =
            AccessTools.MethodDelegate<System.Action<InstanceRenderer>>(AccessTools.Method(typeof(InstanceRenderer), "UpdateBounds"));
        static readonly AccessTools.FieldRef<InstanceRenderer, BoundingSphere> s_boundsOf =
            AccessTools.FieldRefAccess<InstanceRenderer, BoundingSphere>("m_bounds");
        static readonly AccessTools.FieldRef<InstanceRenderer, int> s_instanceCount =
            AccessTools.FieldRefAccess<InstanceRenderer, int>("m_instanceCount");

        static IWindowManager Manager => PortalSystem.Instance != null ? PortalSystem.Instance.Manager : null;

        static Slot Find(Camera cam)
        {
            for (int i = 0; i < s_slots.Count; i++)
                if (ReferenceEquals(s_slots[i].Cam, cam))
                    return s_slots[i];
            return null;
        }

        // OnSlotCameraCreated: exclude it from the engine's hand-on while Grass.Targeted is effective.
        internal static void Attach(Camera cam)
        {
            if ((object)cam == null || Find(cam) != null)
                return;
            var slot = new Slot { Cam = cam };
            s_slots.Add(slot);
            IWindowManager m = Manager;
            SetExcluded(slot, m != null && m.Effective(WindowMechanism.GrassTargeted));
        }

        // OnSlotCameraDestroyed: RemoveExcludedCamera.
        internal static void Detach(Camera cam)
        {
            for (int i = s_slots.Count - 1; i >= 0; i--)
                if (ReferenceEquals(s_slots[i].Cam, cam))
                {
                    SetExcluded(s_slots[i], false);
                    s_slots.RemoveAt(i);
                }
        }

        static void SetExcluded(Slot slot, bool excluded)
        {
            if (slot.Excluded == excluded)
                return;
            slot.Excluded = excluded;
            if (excluded)
                GrassCameraModule.ExcludeCamera(slot.Cam);
            else
                GrassCameraModule.RemoveExcludedCamera(slot.Cam);
        }

        // Once per frame (PortalSystem.RenderWindows, next to the other slot syncs, after MonoUpdaters.Update): the
        // engine's GrassMainCameraOnly and the exclusions follow Grass.Targeted.
        internal static void FrameSync(IWindowManager manager)
        {
            bool want = manager != null && manager.Effective(WindowMechanism.GrassTargeted);
            bool first = !s_ownedLoaded;
            if (first)
            {
                // An earlier session switched the engine on and ended without Clear: it is still ours to switch off.
                s_ownedLoaded = true;
                s_engineOwned = Plugin.GrassEngineOwned != null && Plugin.GrassEngineOwned.Value && GrassCameraModule.Feature.On;
                if (!s_engineOwned)
                    SaveOwned(false);
            }
            if (want != s_wanted || first)
            {
                s_wanted = want;
                string name = GrassCameraModule.Feature.Name;
                if (want)
                {
                    if (!GrassCameraModule.Feature.On)
                    {
                        // Saved first: a crash right after the engine saved its switch still finds the flag.
                        SaveOwned(true);
                        if (Features.Set(name, true))
                        {
                            s_engineOwned = true;
                            Plugin.Debug("Grass.Targeted in force: the engine's GrassMainCameraOnly switched on");
                        }
                        else
                            SaveOwned(false);
                    }
                }
                else if (s_engineOwned)
                    ReleaseEngine("Grass.Targeted not in force: the engine's GrassMainCameraOnly switched back off");
            }
            for (int i = s_slots.Count - 1; i >= 0; i--)
            {
                Slot slot = s_slots[i];
                if (slot.Cam == null)
                {
                    // Gone without OnSlotCameraDestroyed (the pool went with the manager); the engine drops dead entries.
                    s_slots.RemoveAt(i);
                    continue;
                }
                SetExcluded(slot, want);
            }
            // Grass.TargetedEase: counts not drawn for longer than a gap are settled anyway; once a second they go.
            float now = Time.time;
            if (now >= s_easeSweepAt)
            {
                s_easeSweepAt = now + 1f;
                for (int i = 0; i < s_slots.Count; i++)
                    SweepEase(s_slots[i], now);
            }
        }

        static void SweepEase(Slot slot, float now)
        {
            if (slot.Counts.Count == 0)
                return;
            foreach (KeyValuePair<InstanceRenderer, Eased> e in slot.Counts)
                if (now - e.Value.At > EaseGap)
                    s_easeGone.Add(e.Key);
            for (int i = 0; i < s_easeGone.Count; i++)
                slot.Counts.Remove(s_easeGone[i]);
            s_easeGone.Clear();
        }

        // Win.BaseHidesFar (P14.2): the patches of this frame go to the window cameras only (see the class comment). Decided
        // at the frame's first patch, with this frame's notes started.
        internal static bool SlotOnly
        {
            get
            {
                int frame = Time.frameCount;
                if (frame != s_slotOnlyFrame)
                    BeginNotes(frame);
                return s_slotOnly;
            }
        }

        static void BeginNotes(int frame)
        {
            s_slotOnlyFrame = frame;
            if (s_notes > 0)
                System.Array.Clear(s_noteR, 0, s_notes);
            s_notes = 0;
            // Not while the game camera looks at a far side (carried through a portal, or to a window's far point for the
            // lab's in-place reference with the windows suspended): its far grass is what it should draw then, as before.
            PortalSystem sys = PortalSystem.Instance;
            IWindowManager m = Manager;
            s_slotOnly = WindowFlags.BaseHidesFar && Plugin.Enabled != null && Plugin.Enabled.Value && m != null
                         && m.Effective(WindowMechanism.GrassTargeted) && GrassCameraModule.Active
                         && !PortalSystem.Suspended && sys != null && !sys.CameraThroughRecently;
            if (!s_slotOnly)
                return;
            // ClutterSystem.LateUpdate grows round the player (the camera in free fly), patches within m_distance of it,
            // and drops one 2 s after it was last within: a patch's width more stays the game's.
            ClutterSystem clutter = ClutterSystem.instance;
            Player p = Player.m_localPlayer;
            Camera main = Utils.GetMainCamera();
            s_clutterAt = !GameCamera.InFreeFly() && p != null ? p.transform.position : main != null ? main.transform.position : Vector3.zero;
            s_clutterReach = clutter != null ? clutter.m_distance + clutter.m_grassPatchSize : 48f;
            if (s_clutterLayer < 0)
                s_clutterLayer = LayerMask.NameToLayer("InstanceRenderer");
        }

        // The InstanceRendererUpdate prefix under SlotOnly (after Far.GrassGate let the patch run): the game's update for
        // the game camera, untouched, for a patch of the game's own circle; none for a far one. A patch in a window's view
        // of last frame (Grass.GateAhead: widened, and ahead of windows about to show) is noted for the window cameras; for
        // one the game updates, `state` asks the postfix whether it drew it. False: the game's update is skipped.
        internal static bool SlotOnlyUpdate(PortalSystem sys, InstanceRenderer r, BoundingSphere bounds, bool dirty, ref float state)
        {
            Vector3 here = r.transform.position;
            bool far = Utils.DistanceXZ(here, s_clutterAt) > s_clutterReach;
            // Nothing to draw (the game's update returns at once).
            if (s_instanceCount(r) == 0)
                return !far;
            bool culls = r.m_frustumCull;
            if (dirty && culls)
            {
                // The game's update makes the bounds; a far patch, which it does not update, gets them as it would.
                if (!far)
                    return true;
                s_updateBounds(r);
                bounds = s_boundsOf(r);
            }
            bool inside = !culls || (FarFlags.GrassViewNear && r.m_useLod
                ? sys.InsideWindowViewNear(bounds, here, r.m_lodMaxDistance, r.m_useXZLodDistance, out _)
                : sys.InsideWindowView(bounds, out _));
            if (inside)
                Note(r, in bounds, culls, !far && (!culls || Utils.InsideMainCamera(bounds)));
            if (far)
            {
                LabFarSkipped++;
                return false;
            }
            if (inside)
                state = float.NegativeInfinity;
            return true;
        }

        static void Note(InstanceRenderer r, in BoundingSphere bounds, bool culls, bool inMain)
        {
            int i = s_notes;
            if (i == s_noteR.Length)
            {
                int size = i * 2;
                System.Array.Resize(ref s_noteR, size);
                System.Array.Resize(ref s_noteB, size);
                System.Array.Resize(ref s_noteCulls, size);
                System.Array.Resize(ref s_noteInMain, size);
                System.Array.Resize(ref s_noteDrawn, size);
                System.Array.Resize(ref s_notePosRead, size);
                System.Array.Resize(ref s_notePos, size);
            }
            s_noteR[i] = r;
            s_noteB[i] = bounds;
            s_noteCulls[i] = culls;
            s_noteInMain[i] = inMain;
            s_noteDrawn[i] = false;
            s_notePosRead[i] = false;
            s_notes = i + 1;
            LabNoted++;
        }

        // The postfix of a noted patch the game updated: whether that update drew it for the game camera (inside its view,
        // which the prefix tested as the game does, with a count left after its thinning).
        internal static void Drawn(InstanceRenderer r, bool counted)
        {
            int i = s_notes - 1;
            if (i >= 0 && ReferenceEquals(s_noteR[i], r))
                s_noteDrawn[i] = s_noteInMain[i] && counted;
        }

        static bool NotesNow => s_notes > 0 && s_slotOnlyFrame == Time.frameCount && s_slotOnly;

        // BeforeSlotRender, last handler: nothing may run between these draws and the camera's Render() (P3(ii)).
        internal static void Before(WindowState s, Camera cam)
        {
            Slot slot = Find(cam);
            if (slot == null)
            {
                // The shared camera: the engine hands it grass (or the null-camera draws reach it). Win.BaseHidesFar: the
                // window patches the game camera no longer draws come from here.
                if (NotesNow)
                    Submit(cam, null, 0);
                return;
            }
            IWindowManager m = Manager;
            bool effective = m != null && m.Effective(WindowMechanism.GrassTargeted);
            // The switch may have flipped since FrameSync: the exclusion must match before this camera's onPreCull, which
            // comes inside its Render().
            SetExcluded(slot, effective);
            if (!effective)
                return;
            // The recorded batches only while the draws went to the game camera alone (else the null camera's reach this
            // one); Win.BaseHidesFar's noted patches either way (nobody drew them).
            int n = GrassCameraModule.TargetedThisFrame ? GrassCameraModule.FrameBatchCount : 0;
            if (n == 0 && !NotesNow)
                return;
            Submit(cam, slot, n);
        }

        // PortalRenderer.BeforeNestedRender, last handler (after TierEffects): a nested window's view or a window's deeper
        // level, which the shared camera renders. Win.BaseHidesFar: the noted patches such a view shows reach it from here
        // as they reach a level-0 render (Submit tests them against this camera's own view), right before its Render()
        // (P3(ii)); the engine hands it the recorded batches as it does at level 0.
        internal static void BeforeNested(WindowState s, Camera cam)
        {
            if (NotesNow)
                Submit(cam, null, 0);
        }

        // AfterSlotRender, first handler: the draws were consumed by that Render() (or, had it thrown, leak into this
        // camera's next frame once; RenderLevel logs the throw).
        internal static void After(WindowState s, Camera cam)
        {
        }

        // This frame's batches into s_batches (once a frame; again if the engine recorded more since, which only a grass
        // pass after the first window render could do). Entries past the count are cleared, so dead patches of an earlier,
        // larger frame are not kept alive here.
        static void CopyFrame(int n)
        {
            int frame = Time.frameCount;
            if (s_copiedFrame == frame && s_count == n)
                return;
            if (s_batches.Length < n)
            {
                int size = Mathf.Max(n, s_batches.Length * 2);
                System.Array.Resize(ref s_batches, size);
                System.Array.Resize(ref s_pos, size);
                System.Array.Resize(ref s_posRead, size);
            }
            for (int i = 0; i < n; i++)
                s_batches[i] = GrassCameraModule.FrameBatch(i);
            if (s_count > n)
                System.Array.Clear(s_batches, n, s_count - n);
            System.Array.Clear(s_posRead, 0, n);
            s_count = n;
            s_copiedFrame = frame;
        }

        static void Submit(Camera cam, Slot slot, int n)
        {
            if (n > 0)
                CopyFrame(n);
            int mask = cam.cullingMask;
            Vector3 viewPos = cam.transform.position;
            // The slot camera's projection is final here (lens shift to the window's rect, jitter).
            GeometryUtility.CalculateFrustumPlanes(cam, s_planes);
            // One template, its fields set per batch: the constructor reads the default rendering layer mask natively.
            var rp = new RenderParams(null)
            {
                camera = cam,
                shadowCastingMode = ShadowCastingMode.Off,
                // As the game's Graphics.DrawMeshInstanced call (its default light probe usage).
                lightProbeUsage = LightProbeUsage.BlendProbes,
                reflectionProbeUsage = ReflectionProbeUsage.BlendProbes,
            };
            int drawn = 0;
            for (int i = 0; i < n; i++)
            {
                ref GrassCameraModule.Batch b = ref s_batches[i];
                if ((uint)b.Layer < 32u && (mask & (1 << b.Layer)) == 0)
                {
                    LabSkippedMask++;
                    continue;
                }
                if (b.BoundsValid && Outside(in b.Bounds))
                {
                    LabSkippedView++;
                    continue;
                }
                InstanceRenderer r = b.Renderer;
                if ((object)r == null || b.Mesh == null || b.Material == null)
                    continue;
                int count = ViewCount(r, ref s_pos[i], ref s_posRead[i], viewPos, slot, out _);
                if (count <= 0)
                    continue;
                rp.material = b.Material;
                rp.layer = b.Layer;
                rp.receiveShadows = b.ReceiveShadows;
                rp.matProps = b.Properties;
                rp.shadowCastingMode = ShadowCastingMode.Off;
                rp.worldBounds = b.BoundsValid ? new Bounds(b.Bounds.position, Vector3.one * (2f * b.Bounds.radius)) : default;
                try
                {
                    Graphics.RenderMeshInstanced(in rp, b.Mesh, b.Submesh, b.Matrices, count);
                    drawn++;
                    LabInstances += count;
                }
                catch (System.Exception e)
                {
                    if (++LabErrors <= 10)
                        Plugin.Log.LogWarning($"Grass.Targeted: submitting a grass batch to {cam.name} failed: {e.Message}");
                }
            }
            // Win.BaseHidesFar: the noted patches the game camera did not draw, as the game's CustomUpdate would draw them for
            // this camera (its view, its thinning; the renderer's own shadow mode: nobody else draws a shadow caster of
            // them), with the game's arguments (submesh 0, its layer, no property block, receiving shadows).
            if (NotesNow)
                for (int i = 0; i < s_notes; i++)
                {
                    if (s_noteDrawn[i])
                        continue;
                    InstanceRenderer r = s_noteR[i];
                    if (r == null || r.m_mesh == null || r.m_material == null)
                        continue;
                    int layer = s_clutterLayer;
                    if ((uint)layer < 32u && (mask & (1 << layer)) == 0)
                    {
                        LabSkippedMask++;
                        continue;
                    }
                    if (s_noteCulls[i] && Outside(in s_noteB[i]))
                    {
                        LabSkippedView++;
                        continue;
                    }
                    int count = ViewCount(r, ref s_notePos[i], ref s_notePosRead[i], viewPos, slot, out Matrix4x4[] matrices);
                    if (count <= 0)
                        continue;
                    rp.material = r.m_material;
                    rp.layer = layer >= 0 ? layer : 0;
                    rp.receiveShadows = true;
                    rp.matProps = null;
                    rp.shadowCastingMode = r.m_shadowCasting;
                    rp.worldBounds = s_noteCulls[i] ? new Bounds(s_noteB[i].position, Vector3.one * (2f * s_noteB[i].radius)) : default;
                    try
                    {
                        Graphics.RenderMeshInstanced(in rp, r.m_mesh, 0, matrices, count);
                        drawn++;
                        LabInstances += count;
                        LabNotesDrawn++;
                    }
                    catch (System.Exception e)
                    {
                        if (++LabErrors <= 10)
                            Plugin.Log.LogWarning($"Win.BaseHidesFar: submitting a window's grass patch to {cam.name} failed: {e.Message}");
                    }
                }
            if (drawn > 0)
            {
                LabBatches += drawn;
                LabRenders++;
            }
        }

        // GrassCameraModule.ViewCount(renderer, viewPos, m_lodMinDistance, m_lodMaxDistance) over all the renderer's
        // instances (the game camera's own count may be thinner: it stands elsewhere), with its position read once a frame.
        // Grass.TargetedEase: a slot camera's count eased from what it drew before.
        static int ViewCount(InstanceRenderer r, ref Vector3 pos, ref bool posRead, Vector3 viewPos, Slot slot, out Matrix4x4[] matrices)
        {
            GrassCameraModule.GetInstances(r, out matrices, out int total);
            if (matrices == null || total <= 0)
                return 0;
            total = Mathf.Min(total, matrices.Length);
            if (!r.m_useLod)
                return total;
            if (!posRead)
            {
                pos = r.transform.position;
                posRead = true;
            }
            Vector3 here = pos;
            float d = r.m_useXZLodDistance ? Utils.DistanceXZ(viewPos, here) : Vector3.Distance(viewPos, here);
            int target = Mathf.Clamp((int)((1f - Utils.LerpStep(r.m_lodMinDistance, r.m_lodMaxDistance, d)) * total), 0, total);
            return slot != null && FarFlags.GrassTargetedEase ? slot.Ease(r, target, total) : target;
        }

        // Utils.InsideMainCamera(BoundingSphere), negated, on the slot camera's planes (the game's own test).
        static bool Outside(in BoundingSphere sphere)
        {
            for (int i = 0; i < s_planes.Length; i++)
                if (s_planes[i].GetDistanceToPoint(sphere.position) < -sphere.radius)
                    return true;
            return false;
        }

        // The engine's switch off again (it was ours), then the saved flag cleared.
        static void ReleaseEngine(string why)
        {
            s_engineOwned = false;
            if (GrassCameraModule.Feature.On)
            {
                Features.Set(GrassCameraModule.Feature.Name, false);
                Plugin.Debug(why);
            }
            SaveOwned(false);
        }

        static void SaveOwned(bool owned)
        {
            if (Plugin.GrassEngineOwned != null && Plugin.GrassEngineOwned.Value != owned)
                Plugin.GrassEngineOwned.Value = owned;
        }

        // PortalSystem.OnDestroy: exclusions taken back, the engine's switch as it was before us.
        internal static void Clear()
        {
            for (int i = 0; i < s_slots.Count; i++)
            {
                SetExcluded(s_slots[i], false);
                s_slots[i].Counts.Clear();
            }
            s_slots.Clear();
            if (s_notes > 0)
                System.Array.Clear(s_noteR, 0, s_notes);
            s_notes = 0;
            s_slotOnlyFrame = -1;
            s_slotOnly = false;
            if (s_engineOwned)
                ReleaseEngine("Real Portal torn down: the engine's GrassMainCameraOnly switched back off");
            s_wanted = false;
            System.Array.Clear(s_batches, 0, s_count);
            s_count = 0;
            s_copiedFrame = -1;
            FarGrassGate.Clear();
        }
    }

    // Far.GrassGate (Docs\PORTALS20-PLAN.md §7). InstanceRenderer.CustomUpdate runs for every grass and clutter patch every
    // frame (MonoUpdaters.Update), the far areas' too: 703 calls facing twenty portals against 275 facing away, 0.76 ms of
    // their own and 0.23 in InsideWindowViewNear. A far patch is needed only in a frame in which a window onto its place
    // renders (a cached view shows its last render; a slot camera gets the frame's grass only at its render), and the
    // game camera, hundreds of metres off, thins far grass to nothing. With the switch on, a patch in a far area's zone
    // (not in the player's own active area) runs its update only in a frame in which a window onto that area renders,
    // or when the game camera could draw it (inside its view and, thinned by distance, within its LOD's end); otherwise
    // the update is skipped whole (no test, no draw, its fade left where it stood). A patch in no far area's zone runs as
    // before.
    //
    // Which windows render is known only after the pass: MonoUpdaters.Update runs first, the plan (Win.Tiers) is made in
    // GameCamera.LateUpdate (Patches.GameCameraLateUpdate) and the windows render at the game camera's onPreCull. So the
    // pass lets through the areas of last frame's renders (RenderedAtFrame; without a plan to come, every window shown
    // last frame, which the count scheduler picks from at onPreCull) and holds back the rest; right after the plan
    // (AfterPlan, a later GameCamera.LateUpdate postfix) the held patches of the areas it chose besides run their update:
    // the same call, its draws queued before any camera of the frame renders (the null-camera draw reaches every camera
    // of the frame; GrassMainCameraOnly's goes to the game camera, which renders this frame, and is recorded for the slot
    // cameras' Grass.Targeted submission at their render). Nothing is queued for a camera that does not render.
    // A window's place is its far portal's area (RemoteAreas' zones) and the zones its far point's grass reaches
    // (GrowGrass grows a clutter distance round it, which may cross into a neighbouring area's zones). A window only the
    // render-time re-plan adds (the pose moved after the plan) and a render ahead (Win.Prewarm) get no far grass in that
    // frame, as before for a view that was not shown the frame before (InsideWindowView reads last frame's views).
    // The game eases a thinned patch's count (m_lodCount) by deltaTime a frame; a patch updated after a gap gets the gap
    // as its deltaTime (at most a second, the whole fade), so the grass of a window rendered every few frames eases in
    // real time, and one shown again after a while shows it settled.
    // Off: the prefix returns at once, nothing is held. Switched off between the pass and the plan, what was held runs at
    // AfterPlan.
    // Grass.GateAhead (Docs\PARITY-PLAN.md P29): the pass also opens the places of windows about to show
    // (WindowReach.AboutToShow, the camera's last pose), and AfterPlan those about to show from the camera's final pose,
    // after making the views ahead again from that pose (PortalSystem.PrepareAhead), so a window coming into view has its
    // far grass in its first frame. The views patches are tested against are widened by the frame's camera motion there.
    internal static class FarGrassGate
    {
        // A patch updated after a gap catches up this much of its fade at most (the game's covers the whole count in 1 s).
        const float MaxGap = 1f;
        // Zones indexed as FarFreeze does (ZoneSystem.SectorToIndex without its fold of the world's outside onto sector 0).
        const int ZoneSpan = 512;

        // Lab: patches the pass held back, held ones that ran after the plan, windows of the plan whose place last frame's
        // renders had not opened; places opened for windows about to show (Grass.GateAhead).
        internal static long LabHeld, LabCaughtUp, LabOpenedLate, LabOpenedAhead;

        // Per zone, 1 + its slot when a far area loads it (0: none), and the zones marked.
        static ushort[] s_zoneSlot;
        static readonly List<int> s_marked = new List<int>();
        // Per slot: the frame a window onto it renders in, Time.time of the last such frame before, and the deltaTime its
        // patches get in that frame.
        static int[] s_openFrame = new int[256];
        static float[] s_openTime = new float[256], s_openDt = new float[256];
        static int s_slots;
        static RemoteAreas s_tableAreas;
        static int s_tableVersion;

        // This frame (set by its first CustomUpdate): gating at all, the player's zone and the reach of its active area,
        // the game camera's position, a patch's size (its position lies within that of its blades) and the reach of a
        // far point's grass.
        static int s_frame = -1;
        static bool s_on, s_hasCamera;
        static Vector2s s_me;
        static int s_near;
        static Vector3 s_cameraAt;
        static float s_patch = 8f, s_grassReach = 48f;
        // Held back by this frame's pass in a far area's zone, with that zone's slot.
        static readonly List<InstanceRenderer> s_held = new List<InstanceRenderer>();
        static readonly List<int> s_heldSlot = new List<int>();
        static bool s_catchingUp;
        static float s_errorAt;

        // The prefix (Patches.InstanceRendererUpdate), with the switch on: true when this patch's update is skipped this
        // frame. When it runs because a window onto its place renders, deltaTime becomes that place's gap.
        internal static bool Holds(InstanceRenderer r, in BoundingSphere bounds, bool dirtyBounds, ref float deltaTime)
        {
            int frame = Time.frameCount;
            if (frame != s_frame)
                Begin(frame);
            // Bounds not made yet (the update makes them) and renderers the game does not cull run as the game runs them.
            if (!s_on || dirtyBounds || !r.m_frustumCull)
                return false;
            Vector2s z = ZoneSystem.GetZone(bounds.position);
            if (Mathf.Abs(z.x - s_me.x) <= s_near && Mathf.Abs(z.y - s_me.y) <= s_near)
                return false;
            int k = Index(z.x, z.y);
            int slot = k >= 0 ? s_zoneSlot[k] - 1 : -1;
            // A zone outside every far area (a window with no area of its own, Far.NoDupAreas): no window can open it, so
            // it runs as before (InsideWindowView).
            if (slot < 0)
                return false;
            if (s_openFrame[slot] == frame)
            {
                if (s_openDt[slot] > deltaTime)
                    deltaTime = s_openDt[slot];
                return false;
            }
            if (GameCameraSees(r, in bounds))
                return false;
            if (!s_catchingUp)
            {
                s_held.Add(r);
                s_heldSlot.Add(slot);
            }
            LabHeld++;
            return true;
        }

        // What the game's update would draw for the game camera: inside its view (Utils.InsideMainCamera, the game's own
        // test) and, for a patch thinned by distance, nearer than its LOD's end (the game measures from the patch's
        // position, within a patch's size of its blades; the XZ distance is the shorter either way).
        static bool GameCameraSees(InstanceRenderer r, in BoundingSphere bounds)
        {
            if (!s_hasCamera)
                return false;
            if (r.m_useLod)
            {
                float dx = s_cameraAt.x - bounds.position.x, dz = s_cameraAt.z - bounds.position.z;
                float reach = r.m_lodMaxDistance + bounds.radius + s_patch;
                if (dx * dx + dz * dz >= reach * reach)
                    return false;
            }
            return Utils.InsideMainCamera(bounds);
        }

        // The first CustomUpdate of a frame: what is gated, and the places of last frame's renders opened (Live windows
        // render every frame, and most others again soon); without a plan to come, of every window shown last frame.
        static void Begin(int frame)
        {
            s_frame = frame;
            s_held.Clear();
            s_heldSlot.Clear();
            s_on = false;
            PortalSystem sys = PortalSystem.Instance;
            if (!FarFlags.GrassGate || sys == null || Plugin.Enabled == null || !Plugin.Enabled.Value || ZNet.instance == null || ZoneSystem.instance == null)
                return;
            RemoteAreas areas = sys.Areas;
            if (areas.Count == 0)
                return;
            Table(areas);
            s_me = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
            s_near = Mathf.Max(1, ZNet.instance.GetSyncedSimulationDistance().NearSimulationDistance);
            Camera main = Utils.GetMainCamera();
            s_hasCamera = main != null;
            s_cameraAt = s_hasCamera ? main.transform.position : default;
            ClutterSystem clutter = ClutterSystem.instance;
            s_patch = clutter != null ? clutter.m_grassPatchSize : 8f;
            s_grassReach = (clutter != null ? clutter.m_distance : 40f) + s_patch;
            s_on = true;
            IWindowManager m = sys.Manager;
            bool plan = m != null && m.WantsPlan;
            IReadOnlyList<PortalLink> live = sys.Active;
            bool ahead = FarFlags.GrassGateAhead;
            for (int i = 0; i < live.Count; i++)
            {
                PortalLink link = live[i];
                PortalWindow w = link != null ? link.Window : null;
                if (w != null && (w.RenderedAtFrame == frame - 1 || (!plan && w.ShownAtFrame >= frame - 1)))
                    Open(sys, link, frame);
                // Grass.GateAhead: and the windows about to show.
                else if (ahead && w != null && WindowReach.AboutToShow(link) && Open(sys, link, frame))
                    LabOpenedAhead++;
            }
        }

        // GameCamera.LateUpdate, after the window plan (Patches.GrassGateAfterPlan): the patches held back in the places
        // this frame's plan renders besides last frame's run their update now, before any camera of the frame renders.
        internal static void AfterPlan()
        {
            int frame = Time.frameCount;
            if (s_frame != frame || s_held.Count == 0)
                return;
            PortalSystem sys = PortalSystem.Instance;
            // Switched off (or the plugin) since the pass: everything held runs, as without the switch.
            bool all = !FarFlags.GrassGate || sys == null || Plugin.Enabled == null || !Plugin.Enabled.Value;
            bool any = all;
            if (all)
                s_on = false;
            else
            {
                // Grass.GateAhead: from the camera's final pose, the views ahead again and the places of the windows about
                // to show now.
                if (FarFlags.GrassGateAhead)
                {
                    sys.PrepareAhead(true);
                    IReadOnlyList<PortalLink> shown = sys.Active;
                    for (int i = 0; i < shown.Count; i++)
                    {
                        PortalLink link = shown[i];
                        if (link != null && link.Window != null && WindowReach.AboutToShow(link) && Open(sys, link, frame))
                        {
                            any = true;
                            LabOpenedAhead++;
                        }
                    }
                }
                IWindowManager m = sys.Manager;
                if (m != null && m.WantsPlan)
                {
                    IReadOnlyList<PortalLink> live = sys.Active;
                    for (int i = 0; i < live.Count; i++)
                    {
                        PortalLink link = live[i];
                        WindowState s = link != null && link.Window != null ? link.Window.State : null;
                        if (s != null && s.Chosen && s.PlannedFrame == frame && Open(sys, link, frame))
                        {
                            any = true;
                            LabOpenedLate++;
                        }
                    }
                }
            }
            if (!any)
            {
                s_held.Clear();
                s_heldSlot.Clear();
                return;
            }
            s_catchingUp = true;
            try
            {
                float dt = Time.deltaTime, time = Time.time;
                for (int i = 0; i < s_held.Count; i++)
                {
                    if (!all && s_openFrame[s_heldSlot[i]] != frame)
                        continue;
                    InstanceRenderer r = s_held[i];
                    // Switched off since the pass (its patch let go): the game would not update it either.
                    if (r == null || !r.isActiveAndEnabled)
                        continue;
                    // The game's update with every patch of it (the prefix lets it through now, with its place's gap).
                    r.CustomUpdate(dt, time);
                    LabCaughtUp++;
                }
            }
            catch (System.Exception e)
            {
                if (Time.unscaledTime >= s_errorAt)
                {
                    s_errorAt = Time.unscaledTime + 10f;
                    Plugin.Log.LogWarning("Far.GrassGate: far grass after the window plan failed: " + e.Message);
                }
            }
            finally
            {
                s_catchingUp = false;
                s_held.Clear();
                s_heldSlot.Clear();
            }
        }

        // The places a render of `link`'s window may show grass in: its far portal's area, and round its far point the
        // reach of the grass GrowGrass grows there. True when one of their zones was not open yet this frame.
        static bool Open(PortalSystem sys, PortalLink link, int frame)
        {
            bool opened = false;
            ZDO far = link.PartnerZdo;
            RemoteAreas areas = sys.Areas;
            if (far != null)
            {
                ZDOID id = far.m_uid;
                for (int i = 0; i < areas.Count; i++)
                {
                    if (areas.AreaPortal(i) != id)
                        continue;
                    List<Vector2s> zones = areas.AreaZones(i);
                    for (int k = 0; k < zones.Count; k++)
                        opened |= OpenZone(zones[k].x, zones[k].y, frame);
                }
            }
            if (link.Partner == null)
                return opened;
            Vector3 p = sys.FarPoint(link);
            float r = s_grassReach;
            Vector2s lo = ZoneSystem.GetZone(new Vector3(p.x - r, 0f, p.z - r)), hi = ZoneSystem.GetZone(new Vector3(p.x + r, 0f, p.z + r));
            for (int y = lo.y; y <= hi.y; y++)
                for (int x = lo.x; x <= hi.x; x++)
                    opened |= OpenZone(x, y, frame);
            return opened;
        }

        static bool OpenZone(int x, int y, int frame)
        {
            int k = Index(x, y);
            int slot = k >= 0 ? s_zoneSlot[k] - 1 : -1;
            if (slot < 0 || s_openFrame[slot] == frame)
                return false;
            float now = Time.time;
            // Never open before: -infinity, so the whole fade.
            s_openDt[slot] = Mathf.Min(now - s_openTime[slot], MaxGap);
            s_openTime[slot] = now;
            s_openFrame[slot] = frame;
            return true;
        }

        // The far areas' zones, a slot each (one where areas overlap); made again whenever an area comes or goes.
        static void Table(RemoteAreas areas)
        {
            int version = areas.AreasVersion;
            if (ReferenceEquals(areas, s_tableAreas) && version == s_tableVersion)
                return;
            s_tableAreas = areas;
            s_tableVersion = version;
            if (s_zoneSlot == null)
                s_zoneSlot = new ushort[ZoneSpan * ZoneSpan];
            Unmark();
            for (int i = 0; i < areas.Count; i++)
            {
                List<Vector2s> zones = areas.AreaZones(i);
                for (int j = 0; j < zones.Count; j++)
                {
                    int k = Index(zones[j].x, zones[j].y);
                    if (k < 0 || s_zoneSlot[k] != 0 || s_slots >= ushort.MaxValue)
                        continue;
                    if (s_slots == s_openFrame.Length)
                    {
                        int size = s_slots * 2;
                        System.Array.Resize(ref s_openFrame, size);
                        System.Array.Resize(ref s_openTime, size);
                        System.Array.Resize(ref s_openDt, size);
                    }
                    s_openFrame[s_slots] = -1;
                    s_openTime[s_slots] = float.NegativeInfinity;
                    s_openDt[s_slots] = 0f;
                    s_zoneSlot[k] = (ushort)(s_slots + 1);
                    s_marked.Add(k);
                    s_slots++;
                }
            }
        }

        static void Unmark()
        {
            if (s_zoneSlot != null)
                for (int i = 0; i < s_marked.Count; i++)
                    s_zoneSlot[s_marked[i]] = 0;
            s_marked.Clear();
            s_slots = 0;
        }

        static int Index(int x, int y)
        {
            uint ux = (uint)(x + ZoneSpan / 2), uy = (uint)(y + ZoneSpan / 2);
            return ux < (uint)ZoneSpan && uy < (uint)ZoneSpan ? (int)(uy * (uint)ZoneSpan + ux) : -1;
        }

        // FarGrassSubmit.Clear (PortalSystem.OnDestroy): nothing held, the table made again for the next areas.
        internal static void Clear()
        {
            s_held.Clear();
            s_heldSlot.Clear();
            Unmark();
            s_tableAreas = null;
            s_frame = -1;
            s_on = false;
        }
    }
}
