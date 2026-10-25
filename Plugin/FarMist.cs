using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // The Mistlands mist is not weather: ParticleMist, on one of the player's followers, fills the air around you
    // with mist near the mist volumes (Mister) of a biome it is for, at a height it takes from the ground around.
    // A window onto the Mistlands from elsewhere would show none of it, and from the Mistlands it would show the
    // mist of your own ground's height. Such a window gets a copy running over there (around where you would stand),
    // seen only in the views of that place; when you arrive, its mist is handed to the game's own, so nothing pops.
    [HarmonyPatch]
    internal static class FarMist
    {
        sealed class Copy
        {
            public ParticleMist Original, Mist;
            public ParticleSystem Ps;
            public Renderer[] Renderers;
            // The original's renderers, inactive ones too (Win.EnvGroups: looked up once instead of every view).
            public Renderer[] OriginalRenderers;
            public Matrix4x4 ToFar;
            public Vector3 Place;
            public float Until;
            // Far.MistWarmSpread: the far portal it is for (it lives as long as that portal's area or live window),
            // the frame that last wanted it, the area's stage then (3 for a live window with no area of its own: all
            // of it is loaded) and whether that window is live.
            public ZDOID Far = ZDOID.None;
            public int KeptFrame, Stage;
            public bool Live;
            // Shown only once warm; tenths of a second of filling in still to do (WarmSome), and the stage it was warm
            // at (warm before the place around the portal stood: mist volumes came there since).
            public bool Warm = true;
            public int WarmLeft, WarmStage = 3;
            // The original's own system, and the weather object it belongs to (null: none, it runs in any weather).
            public ParticleSystem OriginalPs;
            public GameObject EnvObject;
            // Run here rather than by the game (warming, Far.MistTickShown, or no live window onto it): its
            // ParticleMist disabled and its system paused, advanced by Cycle and Simulate. Far.MistTickShown's clock:
            // when it was last brought up to time, the placing still owed, and the frame it was last advanced in.
            public bool Manual;
            public float TickAt, Owed;
            public int TickFrame = -1;
            // Far.MistParity: the near portal it is seen through, whether that window is about to show
            // (WindowReach.AboutToShow) and how far the camera is from it (the order of warming); whether it is seen (a
            // view showed it this frame or the last, or its window is live and about to show), the frame a view last
            // showed it and the frame it last got a placing of its own while seen; the twins of your demisters over there.
            public PortalLink Link;
            public bool Ahead, Seen;
            public float Dist;
            public int ShownFrame = -100, PlacedFrame = -1;
            public readonly List<Twin> Twins = new List<Twin>();
        }

        // Far.MistParity: a twin of one of your demisters (the Wisplight's ball) at ToFar x its position: a force field
        // as its own, which pushes the mist there as yours pushes the mist around you, and a Demister the copy places its
        // mist around. That Demister is never in the game's list (AI mist checks, the game's own mist): only the copy's
        // own placing sees it (TwinsIn).
        sealed class Twin
        {
            public GameObject Go;
            public ParticleSystemForceField Field, Source;
            public Demister Demister;
            public bool On;
        }

        static readonly List<Copy> s_copies = new List<Copy>();
        static ParticleMist[] s_originals = new ParticleMist[0];
        static float s_findAt;
        static readonly Dictionary<Vector2Int, Heightmap.Biome> s_biomes = new Dictionary<Vector2Int, Heightmap.Biome>();
        // From the ParticleMist.Update postfix: the mists the game runs.
        internal static readonly HashSet<ParticleMist> Seen = new HashSet<ParticleMist>();
        static readonly AccessTools.FieldRef<ParticleMist> s_instance = AccessTools.StaticFieldRefAccess<ParticleMist>(AccessTools.Field(typeof(ParticleMist), "m_instance"));
        static readonly Action<ParticleMist> s_update = AccessTools.MethodDelegate<Action<ParticleMist>>(AccessTools.Method(typeof(ParticleMist), "Update"));
        static readonly List<Renderer> s_shown = new List<Renderer>();
        static readonly List<Renderer> s_hidden = new List<Renderer>();
        static ParticleSystem.Particle[] s_particles = new ParticleSystem.Particle[0];
        static readonly AccessTools.FieldRef<ParticleMist, float> s_accumulator = AccessTools.FieldRefAccess<ParticleMist, float>("m_accumulator");
        static readonly AccessTools.FieldRef<List<Mister>> s_misters = AccessTools.StaticFieldRefAccess<List<Mister>>(AccessTools.Field(typeof(Mister), "m_instances"));

        // Lab: how many copies run.
        internal static int Count => s_copies.Count;

        // Lab: copies still warming, warm steps taken and placings run for views (Far.MistTickShown) so far.
        internal static int LabWarming;
        internal static long LabWarmSteps, LabTickCycles;
        // Lab (Far.MistParity): cold copies finished right before a view showed them (cumulative) and the longest such
        // finish (ms); catch-ups queued for a copy handed back to the game or about to be seen; placings made for a copy
        // while seen (top-ups and catch-ups, without ageing it faster than real time).
        internal static long LabMistSyncWarm, LabMistCatchUps, LabMistSeenPlacings;
        internal static float LabMistSyncWarmMsMax;

        // Every frame, with the places the live windows look at and player space -> each.
        internal static void Keep(List<(Vector3 place, Matrix4x4 toFar)> places)
        {
            Player p = Player.m_localPlayer;
            Camera cam = Utils.GetMainCamera();
            if (WorldGenerator.instance == null || p == null || cam == null)
                return;
            float now = Time.time;
            bool spread = FarFlags.MistWarmSpread, parity = FarFlags.MistParity;
            if (now >= s_findAt)
            {
                // Those that ran lately (a scene-wide search costs a lot with far areas loaded).
                s_findAt = now + 5f;
                Seen.RemoveWhere(m => m == null || !m.isActiveAndEnabled);
                s_originals = new ParticleMist[Seen.Count];
                Seen.CopyTo(s_originals);
                if (s_biomes.Count > 256)
                    s_biomes.Clear();
                // Far.MistWarmSpread: a copy may live through a change of weather over there, which can bring its
                // mist or take it away (Far.MistParity: looked at every frame below, so a new weather period switches it
                // in its first frame).
                if (spread && !parity)
                    foreach (Copy c in s_copies)
                        if (c.Far != ZDOID.None && c.Mist != null)
                            SetEmission(c, EmitsThere(c));
            }
            // Where the game camera is: the game's own mist is drawn around it from when it gets there.
            Vector3 at = cam.transform.position;
            if (spread)
                KeepAreas(at, now);
            else
            {
                foreach ((Vector3 place, Matrix4x4 toFar) in places)
                {
                    // The camera is there itself: the game's own does it.
                    if ((at - place).sqrMagnitude < 300f * 300f)
                        continue;
                    // The biome there, by the 16 m square (looked up once).
                    var cell = new Vector2Int(Mathf.FloorToInt(place.x / 16f), Mathf.FloorToInt(place.z / 16f));
                    if (!s_biomes.TryGetValue(cell, out Heightmap.Biome biome))
                        s_biomes[cell] = biome = WorldGenerator.instance.GetBiome(place);
                    foreach (ParticleMist o in s_originals)
                    {
                        if (o == null || IsCopy(o) || (o.m_biome & biome) == 0)
                            continue;
                        Copy c = null;
                        foreach (Copy x in s_copies)
                            if (x.Original == o && (x.Place - place).sqrMagnitude < 1f)
                            {
                                c = x;
                                break;
                            }
                        if (c == null)
                        {
                            c = Make(o, place, toFar);
                            if (c == null)
                                continue;
                            s_copies.Add(c);
                        }
                        c.ToFar = toFar;
                        c.Until = now + 5f;
                    }
                }
            }
            int frame = Time.frameCount;
            for (int i = s_copies.Count - 1; i >= 0; i--)
            {
                Copy c = s_copies[i];
                // Far.MistWarmSpread: one lives as long as its far area or live window, not 5 s past the last frame a
                // live window wanted it; the camera arriving there still hands it over first.
                bool gone = spread && c.Far != ZDOID.None
                    ? c.KeptFrame != frame && (at - c.Place).sqrMagnitude >= 300f * 300f
                    : now > c.Until;
                if (c.Mist == null || c.Original == null || gone)
                {
                    Drop(i);
                    continue;
                }
                // The camera arrived: the game's own takes over with this mist.
                if ((at - c.Place).sqrMagnitude < 300f * 300f)
                {
                    HandOver(c);
                    Drop(i);
                    continue;
                }
                // Where the game would have it for you standing over there (already there once you have stepped
                // through while the camera has not).
                Vector3 o = c.Original.transform.position, m = c.ToFar.MultiplyPoint3x4(o);
                bool through = (o - c.Place).sqrMagnitude < (m - c.Place).sqrMagnitude;
                c.Mist.transform.position = through ? o : m;
                // Far.MistWarmSpread switched off halfway through warming: shown as it is, and the game runs it on.
                if (!spread && c.WarmLeft > 0)
                {
                    c.WarmLeft = 0;
                    c.Warm = true;
                }
                if (parity)
                {
                    // The weather over there switches its mist on or off from the frame it changes (EnvMan does the
                    // same for yours), and your demisters have their twins over there (your own are there already once
                    // you stepped through).
                    if (spread && c.Far != ZDOID.None)
                        SetEmission(c, EmitsThere(c));
                    KeepTwins(c, through);
                    KeepParity(c, spread, frame, now);
                    continue;
                }
                if (c.Twins.Count > 0)
                    DropTwins(c);
                c.Seen = false;
                // Who runs it: the game, as its own (it was so before both switches), or this class: while it warms,
                // for Far.MistTickShown, and while no live window looks onto it (it has nothing to be drawn in then).
                SetManual(c, c.WarmLeft > 0 || (c.Warm && (FarFlags.MistTickShown || (spread && !c.Live))));
            }
            if (spread)
                WarmSome();
            else
                LabWarming = 0;
        }

        // Far.MistParity: who runs a copy, and what it is owed. The game runs a warm copy a live window shows (as before),
        // and also one topping up or catching up while it is seen: its extra placings then come on top of the game's
        // own, and it ages in real time (WarmSome places them without advancing it). This class runs a cold one, one
        // topping up or catching up unseen (each placing aged a tenth of a second, as warming does), and, as before, a
        // warm one with no live window onto it or under Far.MistTickShown. A copy handed back to the game is first
        // brought up to the time it stood still (CatchUp), so it does not resume stale; one whose window is about to go
        // live is kept up to time from before (Tick, or a catch-up after a longer stop).
        static void KeepParity(Copy c, bool spread, int frame, float now)
        {
            c.Seen = c.ShownFrame >= frame - 1 || (c.Live && c.Ahead);
            bool manual = !c.Warm || (c.WarmLeft > 0 && !c.Seen) || FarFlags.MistTickShown || (spread && !c.Live);
            if (spread && c.Manual && !manual && c.Warm && CatchUp(c, now))
                manual = (c.WarmLeft > 0 && !c.Seen) || FarFlags.MistTickShown || !c.Live;
            SetManual(c, manual);
            if (spread && c.Manual && c.Warm && c.WarmLeft == 0 && c.Ahead && !c.Live && !c.Seen)
            {
                if (now - c.TickAt > TickMax)
                    CatchUp(c, now);
                else
                    Tick(c);
            }
        }

        // At most this much stood-still time is caught up (as the warm-up, a looping mist is in its steady state by then).
        const float CatchUpMax = 10f;

        // The placings a copy missed since it was last brought up to time (min(gap, CatchUpMax) / 0.1), owed to WarmSome.
        // False when there is nothing to catch up.
        static bool CatchUp(Copy c, float now)
        {
            int steps = Mathf.RoundToInt(Mathf.Min(now - c.TickAt, CatchUpMax) / 0.1f);
            c.TickAt = now;
            c.Owed = 0f;
            if (steps <= 0 || c.WarmLeft >= steps)
                return false;
            c.WarmLeft = steps;
            LabMistCatchUps++;
            return true;
        }

        // Far.MistWarmSpread: the copies wanted are those of every far area from its first stage on, once its far
        // portal stands there (the place and the way over are known then), and of every live window. One is made cold
        // and hidden and filled in a step a frame (WarmSome) while the area loads, long before its window goes live:
        // made at go-live and filled in at once, the first held a frame for 0.7-1.1 s.
        static void KeepAreas(Vector3 at, float now)
        {
            PortalSystem sys = PortalSystem.Instance;
            if (sys == null)
                return;
            int frame = Time.frameCount;
            bool made = false, parity = FarFlags.MistParity;
            // Far.MistParity: the windows about to show first (pass 0), so the one copy made a frame is theirs.
            for (int pass = parity ? 0 : 1; pass < 2; pass++)
            foreach (PortalLink link in PortalLink.All)
            {
                ZDO far = link.IsReal && !link.OneWay ? link.PartnerZdo : null;
                PortalLink partner = link.Partner;
                if (far == null || partner == null || !partner.IsReal)
                    continue;
                bool ahead = parity && WindowReach.AboutToShow(link);
                if (parity && ahead != (pass == 0))
                    continue;
                // As PortalSystem.Live: both ends live and with their windows.
                bool live = link.IsActive && link.Window != null && partner.Window != null;
                int stage = sys.Areas.Stage(far);
                if (stage < 1 && !live)
                    continue;
                Vector3 place = partner.Frame.Center;
                // The camera is there itself: the game's own does it.
                if ((at - place).sqrMagnitude < 300f * 300f)
                    continue;
                var cell = new Vector2Int(Mathf.FloorToInt(place.x / 16f), Mathf.FloorToInt(place.z / 16f));
                if (!s_biomes.TryGetValue(cell, out Heightmap.Biome biome))
                    s_biomes[cell] = biome = WorldGenerator.instance.GetBiome(place);
                Matrix4x4 toFar = PortalMath.Pair(link.Frame, partner.Frame);
                foreach (ParticleMist o in s_originals)
                {
                    if (o == null || IsCopy(o) || (o.m_biome & biome) == 0)
                        continue;
                    // Its own copy, or one made for that place's live window before the switch (taken over as warm).
                    Copy c = null;
                    foreach (Copy x in s_copies)
                        if (x.Original == o && (x.Far == far.m_uid || (x.Far == ZDOID.None && (x.Place - place).sqrMagnitude < 1f)))
                        {
                            c = x;
                            break;
                        }
                    if (c == null)
                    {
                        // One new copy a frame (each is an Instantiate); the others follow in the next frames.
                        if (made)
                            continue;
                        made = true;
                        c = Make(o, place, toFar, true);
                        if (c == null)
                            continue;
                        c.Far = far.m_uid;
                        s_copies.Add(c);
                    }
                    else if (c.Far == ZDOID.None)
                    {
                        c.Far = far.m_uid;
                        LookUpWeather(c);
                    }
                    c.Place = place;
                    c.ToFar = toFar;
                    c.Until = now + 5f;
                    c.KeptFrame = frame;
                    c.Live = live;
                    c.Stage = stage < 1 ? 3 : stage;
                    c.Link = link;
                    c.Ahead = ahead;
                    c.Dist = Vector3.Distance(at, link.Frame.Center);
                }
            }
        }

        const int WarmSteps = 100, TopUpSteps = 30;
        const double WarmBudgetMs = 1.5;
        // What a warm step took lately (ms): another is started only while it still fits into the frame's budget.
        static double s_warmStepMs = 1.0;

        // Far.MistWarmSpread: the copies' filling in, ten seconds' worth of placing and ageing each (WarmSteps tenths
        // of a second, as Make does thirty seconds at once), within WarmBudgetMs a frame for all of them; at least one
        // step a frame, so no copy waits for ever behind a slow one.
        static void WarmSome()
        {
            LabWarming = 0;
            foreach (Copy c in s_copies)
            {
                // Warm before the place around its portal stood: what came there since (more mist volumes) gets a
                // few seconds of its own, while the copy shows.
                if (c.Warm && c.WarmLeft == 0 && c.WarmStage < 2 && c.Stage >= 2)
                {
                    c.WarmLeft = TopUpSteps;
                    c.WarmStage = c.Stage;
                }
                if (c.WarmLeft > 0)
                    LabWarming++;
            }
            if (LabWarming == 0)
                return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency, spent = 0.0;
            bool first = true, parity = FarFlags.MistParity;
            int frame = Time.frameCount;
            for (Copy c = parity ? NextToWarmParity(frame) : NextToWarm(); c != null; c = parity ? NextToWarmParity(frame) : NextToWarm())
            {
                // Its weather places no mist there: nothing to fill in.
                if (!c.Ps.emission.enabled)
                {
                    WarmDone(c);
                    continue;
                }
                if (!first && spent + s_warmStepMs > WarmBudgetMs)
                    break;
                first = false;
                long s = System.Diagnostics.Stopwatch.GetTimestamp();
                // Far.MistParity: a warm copy that is seen gets the placing alone (one a frame), on top of its own; it
                // ages in real time, by the game or by Tick before its views.
                if (parity && c.Warm && c.Seen)
                {
                    Place(c);
                    c.PlacedFrame = frame;
                    LabMistSeenPlacings++;
                }
                else
                {
                    Cycle(c);
                    c.Ps.Simulate(0.1f, true, false, false);
                }
                long e = System.Diagnostics.Stopwatch.GetTimestamp();
                s_warmStepMs = s_warmStepMs * 0.8 + (e - s) * toMs * 0.2;
                spent = (e - t0) * toMs;
                LabWarmSteps++;
                if (--c.WarmLeft == 0)
                    WarmDone(c);
            }
        }

        // The next copy to warm: one not shown yet before one topping up, a live window's first, then the area furthest
        // along (its window goes live soonest). Only one this class runs (a top-up waits a frame for that).
        static Copy NextToWarm()
        {
            Copy best = null;
            int rank = -1;
            foreach (Copy c in s_copies)
            {
                if (c.WarmLeft <= 0 || !c.Manual || c.Mist == null)
                    continue;
                int r = (c.Warm ? 0 : 8) + (c.Live ? 4 : 0) + c.Stage;
                if (r > rank)
                {
                    best = c;
                    rank = r;
                }
            }
            return best;
        }

        // Far.MistParity: the next copy to warm, top up or catch up, in the order it will show: those whose window is live
        // and about to show, then those about to show that are still loading, then the rest, each nearest the camera
        // first. An unseen one only while this class runs it (a step ages it); a warm one that is seen once a frame.
        static Copy NextToWarmParity(int frame)
        {
            Copy best = null;
            int bestRank = int.MaxValue;
            foreach (Copy c in s_copies)
            {
                if (c.WarmLeft <= 0 || c.Mist == null)
                    continue;
                if (c.Warm && c.Seen ? c.PlacedFrame == frame : !c.Manual)
                    continue;
                int rank = c.Ahead ? c.Live ? 0 : 1 : 2;
                if (rank < bestRank || (rank == bestRank && c.Dist < best.Dist))
                {
                    best = c;
                    bestRank = rank;
                }
            }
            return best;
        }

        // One tenth of a second's placing for a copy the game runs, its own cadence (the accumulator) left as it was.
        static void Place(Copy c)
        {
            ParticleMist m = c.Mist;
            float acc = s_accumulator(m);
            Cycle(c);
            s_accumulator(m) = acc;
        }

        static void WarmDone(Copy c)
        {
            c.WarmLeft = 0;
            if (!c.Warm)
                c.WarmStage = c.Stage;
            c.Warm = true;
            // Far.MistTickShown takes it on from here.
            c.TickAt = Time.time;
            c.Owed = 0f;
        }

        static void SetManual(Copy c, bool manual)
        {
            if (c.Manual == manual)
                return;
            c.Manual = manual;
            c.Mist.enabled = !manual;
            if (manual)
            {
                c.Ps.Pause(true);
                c.TickAt = Time.time;
                c.Owed = 0f;
            }
            else
                c.Ps.Play(true);
        }

        static readonly List<Mister> s_near = new List<Mister>();
        // The copy Cycle runs right now (MistHeight).
        static ParticleMist s_cycling;

        // One tenth of a second of a copy's placing (ParticleMist.Update once due), with the game's list of mist volumes
        // cut to those around it for the call. The copy tests each particle it places against every volume, and with far
        // Mistlands areas loaded most of them lie kilometres away; the cut keeps every volume that could contain a point
        // it tests (its reach: local mist, demisters, the volumes it places around and those covering them), so it
        // places the same mist. (Its list is never empty while the game's is not, or the copy would stop early.)
        static void Cycle(Copy c)
        {
            ParticleMist m = c.Mist;
            List<Mister> all = s_misters();
            s_near.Clear();
            if (all != null && all.Count > 0)
            {
                float radius = 0f, end = 0f;
                foreach (Mister x in all)
                    if (x != null && x.m_radius > radius)
                        radius = x.m_radius;
                foreach (Demister d in Demister.GetDemisters())
                    if (d != null && d.m_forceField != null && d.m_forceField.endRange > end)
                        end = d.m_forceField.endRange;
                float reach = Mathf.Max(Mathf.Max(2f * radius + m.m_distantMaxRange + m.m_distantThickness, 2f * end + m.m_maxDistance), m.m_localRange) + 1f;
                Vector3 at = m.transform.position;
                foreach (Mister x in all)
                {
                    if (x == null)
                        continue;
                    float r = reach + x.m_radius;
                    if ((x.transform.position - at).sqrMagnitude < r * r)
                        s_near.Add(x);
                }
                if (s_near.Count == 0)
                    s_near.Add(all[0]);
            }
            s_misters() = s_near;
            s_cycling = m;
            try
            {
                // Due now, whatever the fixed step (five fixed steps make a tenth of a second at the game's 50 Hz).
                s_accumulator(m) = 0.1f;
                s_update(m);
            }
            finally
            {
                s_misters() = all;
                s_cycling = null;
            }
        }

        const float TickMax = 0.2f;
        const double TickBudgetMs = 1.0;
        // Far.InViewStep: placings a frame over all copies its views show, in place of TickBudgetMs, and the main-thread ms
        // past which no more are placed that frame (a backstop; the frame's first always runs; lab-tunable).
        const int TickCyclesMax = 8;
        internal static double InViewMistMs = 2.0;
        static int s_tickFrame = -1, s_tickCycles;
        static double s_tickMs;

        // Far.MistTickShown: a warm copy runs only right before a view that shows it, by the time gone since its last
        // one, at most TickMax (one unseen for long stands as it was and goes on from there; a cached window renders
        // twice a second or so, and its mist drifts on a little slower). Its placing shares TickBudgetMs a frame with
        // the other views' copies (the frame's first always runs); what does not fit stays owed for its next view.
        // Once a frame per copy. Far.InViewStep (FARWORLD-V3 §7 V0): the mist ticks with the renders that show it, and with
        // live windows that is every frame: a copy's placing owed is done at its tick, without the shared millisecond (by
        // which several copies shown in one frame put off and, past TickMax, lost theirs), within TickCyclesMax placings
        // and InViewMistMs a frame.
        static void Tick(Copy c)
        {
            int frame = Time.frameCount;
            if (c.TickFrame == frame)
                return;
            c.TickFrame = frame;
            if (s_tickFrame != frame)
            {
                s_tickFrame = frame;
                s_tickMs = 0.0;
                s_tickCycles = 0;
            }
            float now = Time.time, dt = Mathf.Min(now - c.TickAt, TickMax);
            c.TickAt = now;
            if (dt <= 0f)
                return;
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            c.Owed = Mathf.Min(c.Owed + dt, TickMax);
            bool inView = FarFlags.InViewStep;
            while (c.Owed >= 0.1f && (inView ? s_tickCycles < TickCyclesMax && (s_tickCycles == 0 || s_tickMs < InViewMistMs)
                                                : (s_tickCycles == 0 || s_tickMs < TickBudgetMs)))
            {
                Cycle(c);
                c.Owed -= 0.1f;
                s_tickCycles++;
                LabTickCycles++;
                long t = System.Diagnostics.Stopwatch.GetTimestamp();
                s_tickMs += (t - t0) * toMs;
                t0 = t;
            }
            c.Ps.Simulate(dt, true, false, false);
            s_tickMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * toMs;
        }

        static bool IsCopy(ParticleMist m)
        {
            foreach (Copy c in s_copies)
                if (c.Mist == m)
                    return true;
            return false;
        }

        static Copy Make(ParticleMist o, Vector3 place, Matrix4x4 toFar, bool cold = false)
        {
            ParticleMist keep = s_instance();
            GameObject go = UnityEngine.Object.Instantiate(o.gameObject, toFar.MultiplyPoint3x4(o.transform.position), o.transform.rotation);
            // Its Awake took over the game's "the mist" for itself.
            s_instance() = keep;
            go.name = "RealPortalFarMist";
            var c = new Copy { Original = o, Mist = go.GetComponent<ParticleMist>(), Ps = go.GetComponent<ParticleSystem>(), Place = place, ToFar = toFar };
            if (c.Mist == null || c.Ps == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }
            ParticleSystem.MainModule main = c.Ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            c.Ps.Clear();
            c.Ps.Play();
            c.Renderers = go.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer r in c.Renderers)
                r.forceRenderingOff = true;
            // Far.MistWarmSpread: left cold and hidden, run by this class from now on and filled in step by step
            // (WarmSome); whether it places any mist is up to the weather over there (the original's own switch is
            // for yours, and a window's FarEnvironment turns it on only once that window is live).
            if (cold)
            {
                LookUpWeather(c);
                SetEmission(c, EmitsThere(c));
                c.Warm = false;
                c.WarmLeft = WarmSteps;
                c.WarmStage = 0;
                SetManual(c, true);
                return c;
            }
            // Filled in at once: thirty seconds of its placing, the mist ageing as it would (ParticleMist places a
            // tenth of a second's worth per five fixed steps).
            for (int k = 0; k < 300; k++)
            {
                for (int j = 0; j < 5; j++)
                    s_update(c.Mist);
                c.Ps.Simulate(0.1f, true, false, false);
            }
            c.Ps.Play();
            return c;
        }

        // The original's own system, and the weather object it is part of: the game switches a weather's particles
        // on and off by emission alone (EnvMan.SetParticleArrayEnabled).
        static void LookUpWeather(Copy c)
        {
            c.OriginalPs = c.Original != null ? c.Original.GetComponent<ParticleSystem>() : null;
            c.EnvObject = null;
            EnvMan em = EnvMan.instance;
            if (em == null || c.Original == null)
                return;
            Transform t = c.Original.transform;
            foreach (EnvSetup env in em.m_environments)
                if (env != null && env.m_psystems != null)
                    foreach (GameObject go in env.m_psystems)
                        if (go != null && t.IsChildOf(go.transform))
                        {
                            c.EnvObject = go;
                            return;
                        }
        }

        // Whether the copy places mist: when it is part of a weather, whether the weather over there (or yours, when
        // that place has the same) has it; otherwise as the original. While that place blends into another weather, the
        // blend's (Far.WeatherParity: the old weather's systems until the blend ends, as EnvMan keeps them and as the
        // place's other weather particles switch), not the new weather's from the blend's start.
        static bool EmitsThere(Copy c)
        {
            EnvMan em = EnvMan.instance;
            if (c.EnvObject != null && em != null)
            {
                EnvSetup env = FarEnvironment.LocalWeather(c.Place) ?? em.GetCurrentEnvironment();
                if (env != null)
                    return env.m_psystems != null && Array.IndexOf(env.m_psystems, c.EnvObject) >= 0;
            }
            return c.OriginalPs != null && c.OriginalPs.emission.enabled;
        }

        static void SetEmission(Copy c, bool on)
        {
            ParticleSystem.EmissionModule emission = c.Ps.emission;
            if (emission.enabled != on)
                emission.enabled = on;
        }

        static readonly Dictionary<Vector2Int, Vector3> s_heights = new Dictionary<Vector2Int, Vector3>();

        // The ground height a copy places its mist above: ParticleMist casts 20 rays to the ground around it for it
        // every tenth of a second. While this class runs a copy (Cycle) the result is kept by the 16 m square for 10 s,
        // not by copy: the copy follows you over there. Kept only once every ray met ground (an area still loading
        // has none yet around); worked out as the game does.
        [HarmonyPrefix, HarmonyPatch(typeof(ParticleMist), "FindMaxMistAlltitude")]
        static bool MistHeight(ParticleMist __instance, float testRange, ref float minMistHeight, ref float maxMistHeight)
        {
            ZoneSystem zs = ZoneSystem.instance;
            if (!ReferenceEquals(__instance, s_cycling) || zs == null)
                return true;
            Vector3 at = __instance.transform.position;
            var cell = new Vector2Int(Mathf.FloorToInt(at.x / 16f), Mathf.FloorToInt(at.z / 16f));
            float now = Time.time;
            if (s_heights.TryGetValue(cell, out Vector3 kept) && now - kept.z < 10f)
            {
                minMistHeight = kept.x;
                maxMistHeight = kept.y;
                return false;
            }
            const int n = 20;
            float sum = 0f;
            bool ground = true;
            minMistHeight = 99999f;
            for (int i = 0; i < n; i++)
            {
                Vector2 d = UnityEngine.Random.insideUnitCircle;
                Vector3 q = at + new Vector3(d.x, 0f, d.y) * testRange;
                if (!zs.GetGroundHeight(q, out float h))
                {
                    h = q.y;
                    ground = false;
                }
                sum += h;
                if (h < minMistHeight)
                    minMistHeight = h;
            }
            maxMistHeight = sum / n + __instance.m_maxMistAltitude;
            if (ground)
            {
                if (s_heights.Count > 1024)
                    s_heights.Clear();
                s_heights[cell] = new Vector3(minMistHeight, maxMistHeight, now);
            }
            return false;
        }

        static void HandOver(Copy c)
        {
            int n = c.Ps.particleCount;
            if (s_particles.Length < n)
                s_particles = new ParticleSystem.Particle[n];
            n = c.Ps.GetParticles(s_particles, n);
            ParticleSystem own = c.Original.GetComponent<ParticleSystem>();
            // The game's own is simulated with its space at the world origin here (see PortalSystem.SpaceOf).
            if (own != null && n > own.particleCount)
                own.SetParticles(s_particles, n);
        }

        static void Drop(int i)
        {
            DropTwins(s_copies[i]);
            if (s_copies[i].Mist != null)
                UnityEngine.Object.Destroy(s_copies[i].Mist.gameObject);
            s_copies.RemoveAt(i);
        }

        // ---- Far.MistParity: your demisters over there ---------------------------------------------------------------

        static readonly AccessTools.FieldRef<SE_Demister, GameObject> s_ball = AccessTools.FieldRefAccess<SE_Demister, GameObject>("m_ballInstance");
        static readonly List<Demister> s_mine = new List<Demister>();
        static readonly List<Demister> s_ballDemisters = new List<Demister>();
        static int s_mineFrame = -1;

        // Your own demisters, once a frame: those of the balls your Wisplight status effects keep (SE_Demister). Placed
        // ones (a wisp torch in your base) stay where they are and have no twin; those at the far side are its own.
        static List<Demister> MyDemisters()
        {
            int frame = Time.frameCount;
            if (s_mineFrame == frame)
                return s_mine;
            s_mineFrame = frame;
            s_mine.Clear();
            Player p = Player.m_localPlayer;
            SEMan se = p != null ? p.GetSEMan() : null;
            if (se == null)
                return s_mine;
            foreach (StatusEffect e in se.GetStatusEffects())
            {
                GameObject ball = e is SE_Demister d ? s_ball(d) : null;
                if (ball == null)
                    continue;
                ball.GetComponentsInChildren(s_ballDemisters);
                foreach (Demister x in s_ballDemisters)
                    if (x != null && x.m_forceField != null)
                        s_mine.Add(x);
            }
            s_ballDemisters.Clear();
            return s_mine;
        }

        // A copy's twins follow your demisters, carried over by its ToFar (none while you have stepped through: yours
        // are over there then).
        static void KeepTwins(Copy c, bool through)
        {
            List<Demister> mine = through ? null : MyDemisters();
            int n = mine != null ? mine.Count : 0;
            for (int i = 0; i < n; i++)
            {
                Demister src = mine[i];
                ParticleSystemForceField from = src.m_forceField;
                Transform st = from.transform;
                Vector3 pos = c.ToFar.MultiplyPoint3x4(st.position);
                Quaternion rot = PortalMath.Rotation(c.ToFar) * st.rotation;
                Twin t = i < c.Twins.Count ? c.Twins[i] : null;
                if (t == null || t.Go == null || t.Field == null || t.Demister == null)
                {
                    if (t != null && t.Go != null)
                        UnityEngine.Object.Destroy(t.Go);
                    t = MakeTwin(from, pos, rot);
                    if (i < c.Twins.Count)
                        c.Twins[i] = t;
                    else
                        c.Twins.Add(t);
                }
                else if (t.Source != from)
                {
                    CopyField(from, t.Field);
                    t.Go.layer = from.gameObject.layer;
                    t.Source = from;
                }
                t.Go.transform.SetPositionAndRotation(pos, rot);
                t.Go.transform.localScale = st.lossyScale;
                // As the game's: its range may change, and the field may be off (Demister.DisableForcefield).
                t.Field.startRange = from.startRange;
                t.Field.endRange = from.endRange;
                bool on = from.enabled && from.gameObject.activeInHierarchy && src.isActiveAndEnabled;
                if (t.Field.enabled != on)
                    t.Field.enabled = on;
                t.On = on;
            }
            for (int i = c.Twins.Count - 1; i >= n; i--)
            {
                if (c.Twins[i].Go != null)
                    UnityEngine.Object.Destroy(c.Twins[i].Go);
                c.Twins.RemoveAt(i);
            }
        }

        static Twin MakeTwin(ParticleSystemForceField from, Vector3 pos, Quaternion rot)
        {
            var go = new GameObject("RealPortalFarDemister");
            // Inactive while it is put together: the Demister's Awake (once active) finds its field and takes its place
            // as where it last was (no burst of placing for a jump), and being disabled it never enters the game's list
            // (OnEnable is what adds it).
            go.SetActive(false);
            go.layer = from.gameObject.layer;
            go.transform.SetPositionAndRotation(pos, rot);
            go.transform.localScale = from.transform.lossyScale;
            ParticleSystemForceField field = go.AddComponent<ParticleSystemForceField>();
            CopyField(from, field);
            Demister d = go.AddComponent<Demister>();
            d.enabled = false;
            go.SetActive(true);
            return new Twin { Go = go, Field = field, Source = from, Demister = d };
        }

        static void CopyField(ParticleSystemForceField from, ParticleSystemForceField to)
        {
            to.shape = from.shape;
            to.startRange = from.startRange;
            to.endRange = from.endRange;
            to.length = from.length;
            to.directionX = from.directionX;
            to.directionY = from.directionY;
            to.directionZ = from.directionZ;
            to.gravity = from.gravity;
            to.gravityFocus = from.gravityFocus;
            to.rotationSpeed = from.rotationSpeed;
            to.rotationAttraction = from.rotationAttraction;
            to.rotationRandomness = from.rotationRandomness;
            to.drag = from.drag;
            to.multiplyDragByParticleSize = from.multiplyDragByParticleSize;
            to.multiplyDragByParticleVelocity = from.multiplyDragByParticleVelocity;
            to.vectorField = from.vectorField;
            to.vectorFieldSpeed = from.vectorFieldSpeed;
            to.vectorFieldAttraction = from.vectorFieldAttraction;
        }

        static void DropTwins(Copy c)
        {
            foreach (Twin t in c.Twins)
                if (t.Go != null)
                    UnityEngine.Object.Destroy(t.Go);
            c.Twins.Clear();
        }

        // The copy whose placing runs now (ParticleMist.Update, by the game or by Cycle) and the twins it was given.
        static Copy s_twinsFor;

        // Far.MistParity: while a copy places its mist, the twins of your demisters stand in the game's list of demisters
        // for that call alone (it places around them and none inside them, as yours do here); out again right after.
        [HarmonyPrefix, HarmonyPatch(typeof(ParticleMist), "Update")]
        static void TwinsIn(ParticleMist __instance)
        {
            if (!FarFlags.MistParity || s_copies.Count == 0 || s_twinsFor != null)
                return;
            Copy c = null;
            foreach (Copy x in s_copies)
                if (ReferenceEquals(x.Mist, __instance))
                {
                    c = x;
                    break;
                }
            if (c == null || c.Twins.Count == 0)
                return;
            List<Demister> all = Demister.GetDemisters();
            foreach (Twin t in c.Twins)
                if (t.On && t.Demister != null)
                    all.Add(t.Demister);
            s_twinsFor = c;
        }

        [HarmonyFinalizer, HarmonyPatch(typeof(ParticleMist), "Update")]
        static void TwinsOut()
        {
            Copy c = s_twinsFor;
            if (c == null)
                return;
            s_twinsFor = null;
            List<Demister> all = Demister.GetDemisters();
            foreach (Twin t in c.Twins)
            {
                if (t.Demister == null)
                    continue;
                int k = all.LastIndexOf(t.Demister);
                if (k >= 0)
                    all.RemoveAt(k);
            }
        }

        // Win.EnvGroups: the copies shown now (for the view before), valid until Hide.
        static readonly List<Copy> s_showing = new List<Copy>();
        static bool s_showingValid;

        // Far.MistWarmSpread: a copy still warming is not shown (made the old way, one is warm at once).
        static bool Shows(Copy c, Vector3 camPos) => c.Mist != null && c.Warm && (c.Place - camPos).sqrMagnitude < 600f * 600f;

        // For a view of the far side from camPos: the copies there shown, the game's own (of your place) hidden.
        internal static void Show(Vector3 camPos)
        {
            // Far.MistParity: a view never shows a place without the mist it has there.
            if (FarFlags.MistParity)
                FinishCold(camPos);
            if (!WindowFlags.EnvGroups)
            {
                ShowFor(camPos);
                return;
            }
            // Windows render one after the other with what the one before set left in place (put back after the
            // last, PortalSystem): a view that shows the same copies has nothing to change.
            if (s_showingValid && SameAsShown(camPos))
                return;
            Hide();
            ShowFor(camPos);
            s_showingValid = true;
        }

        static bool SameAsShown(Vector3 camPos)
        {
            int k = 0;
            foreach (Copy c in s_copies)
                if (Shows(c, camPos))
                {
                    if (k >= s_showing.Count || s_showing[k] != c)
                        return false;
                    k++;
                }
            return k == s_showing.Count;
        }

        // Far.MistParity: a copy still cold that this view would show (WarmLeft steps at most, WarmSteps) is warmed right
        // now, before the view renders; once per copy, as it is warm from then on. The warm order (NextToWarmParity) makes
        // this rare: it is the window that came into view before its copy was done.
        static void FinishCold(Vector3 camPos)
        {
            foreach (Copy c in s_copies)
            {
                if (c.Warm || c.Mist == null || c.Ps == null || (c.Place - camPos).sqrMagnitude >= 600f * 600f)
                    continue;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int steps = 0;
                // Cold copies are run by this class (paused), so each step ages it as WarmSome's do.
                if (c.Manual && c.Ps.emission.enabled)
                    for (; c.WarmLeft > 0; c.WarmLeft--)
                    {
                        Cycle(c);
                        c.Ps.Simulate(0.1f, true, false, false);
                        steps++;
                    }
                WarmDone(c);
                if (steps == 0)
                    continue;
                LabWarmSteps += steps;
                LabMistSyncWarm++;
                float ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                if (ms > LabMistSyncWarmMsMax)
                    LabMistSyncWarmMsMax = ms;
            }
        }

        static void ShowFor(Vector3 camPos)
        {
            bool grouped = WindowFlags.EnvGroups, parity = FarFlags.MistParity;
            int frame = Time.frameCount;
            foreach (Copy c in s_copies)
                if (Shows(c, camPos))
                {
                    c.ShownFrame = frame;
                    // Run by this class and warm (Far.MistTickShown, or no live window onto it): brought up to time
                    // right before the view (Far.MistParity: also while topping up or catching up, which then only adds
                    // placings).
                    if (c.Manual && (c.WarmLeft == 0 || parity))
                        Tick(c);
                    foreach (Renderer r in c.Renderers)
                        if (r != null && r.forceRenderingOff)
                        {
                            r.forceRenderingOff = false;
                            s_shown.Add(r);
                        }
                    if (grouped)
                    {
                        // The same as GetComponentsInChildren below (active ones only), without asking every view.
                        if (c.OriginalRenderers == null && c.Original != null)
                            c.OriginalRenderers = c.Original.GetComponentsInChildren<Renderer>(true);
                        if (c.OriginalRenderers != null)
                            foreach (Renderer r in c.OriginalRenderers)
                                if (r != null && !r.forceRenderingOff && r.gameObject.activeInHierarchy)
                                {
                                    r.forceRenderingOff = true;
                                    s_hidden.Add(r);
                                }
                        s_showing.Add(c);
                        continue;
                    }
                    foreach (Renderer r in c.Original.GetComponentsInChildren<Renderer>())
                        if (!r.forceRenderingOff)
                        {
                            r.forceRenderingOff = true;
                            s_hidden.Add(r);
                        }
                }
        }

        internal static void Hide()
        {
            foreach (Renderer r in s_shown)
                if (r != null)
                    r.forceRenderingOff = true;
            s_shown.Clear();
            foreach (Renderer r in s_hidden)
                if (r != null)
                    r.forceRenderingOff = false;
            s_hidden.Clear();
            s_showing.Clear();
            s_showingValid = false;
        }

        internal static void Clear()
        {
            Hide();
            for (int i = s_copies.Count - 1; i >= 0; i--)
                Drop(i);
            s_heights.Clear();
        }
    }
}
