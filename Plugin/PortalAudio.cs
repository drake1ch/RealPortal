using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // Sound through real portals. The game's sound effects (ZSFX) play where they happen, and you hear them from
    // the audio listener on the game camera. A sound by the far end of a live portal you stand near gets a twin on
    // your side, where it would be heard through the opening (the far place laid over yours through the pair), kept
    // in step with it (clip, volume, pitch, time) until it stops. While the camera arm reaches back through a
    // portal, the listener stands where the camera would be on your side, so you hear your own surroundings.
    internal sealed class PortalAudio
    {
        static readonly HashSet<ZSFX> s_all = new HashSet<ZSFX>();
        static readonly AccessTools.FieldRef<ZSFX, AudioSource> s_source = AccessTools.FieldRefAccess<ZSFX, AudioSource>("m_audioSource");

        sealed class Twin
        {
            public AudioSource From, To;
            public PortalLink Link;
            public int SeenFrame;
        }

        readonly Dictionary<AudioSource, Twin> _twins = new Dictionary<AudioSource, Twin>();
        readonly Stack<AudioSource> _pool = new Stack<AudioSource>();
        readonly List<AudioSource> _drop = new List<AudioSource>();
        GameObject _root;
        AudioListener _ear, _game;

        // Lab: twins playing now, and made so far.
        internal int Count => _twins.Count;
        internal static int LabMade;
        // Lab: where the last twin was heard from (degrees off the direction of its portal) and how far.
        internal static string LabLastHeard = "";
        internal Vector3 LabEar => _ear != null && _ear.enabled ? _ear.transform.position : _game != null ? _game.transform.position : Vector3.zero;

        internal static void Register(ZSFX sfx) => s_all.Add(sfx);
        internal static void Unregister(ZSFX sfx) => s_all.Remove(sfx);

        // Every frame once the camera has its place (from the game camera's pre-cull).
        internal void Update(PortalSystem sys, Camera cam)
        {
            if (!Plugin.Sound.Value)
            {
                Clear();
                return;
            }
            PlaceEar(sys, cam);
            AudioListener active = _ear != null && _ear.enabled ? _ear : _game;
            if (active == null)
                return;
            Vector3 ear = active.transform.position;
            int frame = Time.frameCount;
            IReadOnlyList<PortalLink> live = sys.Active;
            if (live.Count > 0 && FarFlags.LinkScaling)
                HearNear(live, ear, frame);
            else if (live.Count > 0)
                foreach (ZSFX sfx in s_all)
                {
                    if (sfx == null)
                        continue;
                    AudioSource src = s_source(sfx);
                    if (src == null || !src.isPlaying || src.spatialBlend <= 0f || src.clip == null)
                        continue;
                    Vector3 at = src.transform.position;
                    float reach = src.maxDistance;
                    foreach (PortalLink link in live)
                    {
                        if (link == null || link.Partner == null)
                            continue;
                        // You by this portal, the sound by its partner, and within its hearing distance through it.
                        Vector3 here = link.Frame.Center, there = link.Partner.Frame.Center;
                        if ((ear - here).sqrMagnitude > reach * reach)
                            continue;
                        if (Hear(src, link, at, here, there, reach, ear, frame))
                            break;
                    }
                }
            _drop.Clear();
            foreach (var kv in _twins)
                if (kv.Value.SeenFrame != frame)
                    _drop.Add(kv.Key);
            foreach (AudioSource src in _drop)
            {
                Twin twin = _twins[src];
                _twins.Remove(src);
                Give(twin.To);
            }
        }

        // The sound at `at` heard through this link (you within its hearing distance of `here`, checked by the caller):
        // its twin made or kept in step. False when it does not carry through this one.
        bool Hear(AudioSource src, PortalLink link, Vector3 at, Vector3 here, Vector3 there, float reach, Vector3 ear, int frame)
        {
            if ((at - there).sqrMagnitude > reach * reach || (at - there).sqrMagnitude > (at - here).sqrMagnitude)
                return false;
            // Heard from the opening, as far away as the way through it: you can tell which portal it
            // comes from. Up close (looking through) from where it shows through the opening.
            Vector3 seen = PortalMath.Pair(link.Partner.Frame, link.Frame).MultiplyPoint3x4(at);
            float toPortal = Vector3.Distance(ear, here), path = toPortal + Vector3.Distance(at, there);
            if (path > reach)
                return false;
            Vector3 fromSeen = (seen - ear).normalized, fromPortal = toPortal > 0.01f ? (here - ear) / toPortal : fromSeen;
            Vector3 heard = ear + Vector3.Slerp(fromSeen, fromPortal, Mathf.InverseLerp(1f, 3f, toPortal)) * path;
            if (!_twins.TryGetValue(src, out Twin twin))
            {
                twin = new Twin { From = src, To = Take(), Link = link };
                Copy(src, twin.To);
                twin.To.time = Mathf.Min(src.time, src.clip.length - 0.01f);
                twin.To.Play();
                _twins[src] = twin;
                LabMade++;
            }
            twin.Link = link;
            twin.SeenFrame = frame;
            Follow(twin, heard);
            LabLastHeard = $"{src.clip.name} {Vector3.Angle(heard - ear, here - ear):0} deg off its portal ({toPortal:0.0} m away), " +
                           $"{path:0.0} m by the way through (straight to where it shows {Vector3.Distance(seen, ear):0.0} m, {Vector3.Angle(seen - ear, here - ear):0} deg off)";
            return true;
        }

        // Far.LinkScaling: every playing sound asked every live portal (forty in a hall of twenty) for its frame and
        // partner. The portals with a partner are listed once a frame with how far you are from each; a sound that
        // carries to none of them is passed over before its other checks, and only those within its hearing distance
        // of you are asked, in the same order (the first that carries it gets the twin, as before).
        struct NearLink
        {
            public PortalLink Link;
            public Vector3 Here, There;
            public float EarSq;
        }

        NearLink[] _near = new NearLink[16];

        void HearNear(IReadOnlyList<PortalLink> live, Vector3 ear, int frame)
        {
            int count = 0;
            float nearestSq = float.MaxValue;
            for (int i = 0; i < live.Count; i++)
            {
                PortalLink link = live[i];
                if (link == null || link.Partner == null)
                    continue;
                if (count == _near.Length)
                    System.Array.Resize(ref _near, count * 2);
                Vector3 here = link.Frame.Center;
                float earSq = (ear - here).sqrMagnitude;
                _near[count++] = new NearLink { Link = link, Here = here, There = link.Partner.Frame.Center, EarSq = earSq };
                nearestSq = Mathf.Min(nearestSq, earSq);
            }
            if (count == 0)
                return;
            foreach (ZSFX sfx in s_all)
            {
                if (sfx == null)
                    continue;
                AudioSource src = s_source(sfx);
                if (src == null)
                    continue;
                float reach = src.maxDistance;
                if (nearestSq > reach * reach || !src.isPlaying || src.spatialBlend <= 0f || src.clip == null)
                    continue;
                Vector3 at = src.transform.position;
                for (int i = 0; i < count; i++)
                {
                    ref NearLink n = ref _near[i];
                    if (n.EarSq > reach * reach)
                        continue;
                    if (Hear(src, n.Link, at, n.Here, n.There, reach, ear, frame))
                        break;
                }
            }
            // Not holding on to links the game may destroy before the next frame.
            System.Array.Clear(_near, 0, count);
        }

        static void Follow(Twin twin, Vector3 heard)
        {
            AudioSource from = twin.From, to = twin.To;
            to.transform.position = heard;
            to.volume = from.volume;
            to.pitch = from.pitch;
            if (to.clip != from.clip)
            {
                to.clip = from.clip;
                to.time = 0f;
                to.Play();
            }
            else if (Mathf.Abs(to.time - from.time) > 0.15f && from.time < from.clip.length - 0.05f)
                to.time = from.time;
            if (!to.isPlaying)
                to.Play();
        }

        static void Copy(AudioSource from, AudioSource to)
        {
            to.clip = from.clip;
            to.loop = from.loop;
            to.volume = from.volume;
            to.pitch = from.pitch;
            to.spatialBlend = from.spatialBlend;
            to.rolloffMode = from.rolloffMode;
            to.minDistance = from.minDistance;
            to.maxDistance = from.maxDistance;
            to.spread = from.spread;
            to.dopplerLevel = from.dopplerLevel;
            to.priority = from.priority;
            to.panStereo = from.panStereo;
            to.outputAudioMixerGroup = from.outputAudioMixerGroup;
            to.bypassEffects = from.bypassEffects;
            to.bypassListenerEffects = from.bypassListenerEffects;
            to.bypassReverbZones = from.bypassReverbZones;
            to.reverbZoneMix = from.reverbZoneMix;
            if (from.rolloffMode == AudioRolloffMode.Custom)
                to.SetCustomCurve(AudioSourceCurveType.CustomRolloff, from.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
            to.SetCustomCurve(AudioSourceCurveType.SpatialBlend, from.GetCustomCurve(AudioSourceCurveType.SpatialBlend));
            to.SetCustomCurve(AudioSourceCurveType.Spread, from.GetCustomCurve(AudioSourceCurveType.Spread));
        }

        AudioSource Take()
        {
            if (_pool.Count > 0)
            {
                AudioSource s = _pool.Pop();
                s.gameObject.SetActive(true);
                return s;
            }
            if (_root == null)
            {
                _root = new GameObject("RealPortalAudio");
                Object.DontDestroyOnLoad(_root);
            }
            var go = new GameObject("RealPortalSoundTwin");
            go.transform.SetParent(_root.transform, false);
            AudioSource src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            return src;
        }

        void Give(AudioSource s)
        {
            if (s == null)
                return;
            s.Stop();
            s.clip = null;
            s.gameObject.SetActive(false);
            _pool.Push(s);
        }

        // The listener on your side while the camera stands across a portal.
        void PlaceEar(PortalSystem sys, Camera cam)
        {
            if (_game == null || !_game.gameObject.activeInHierarchy)
            {
                _game = null;
                foreach (AudioListener l in Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
                    if (l != _ear)
                    {
                        _game = l;
                        break;
                    }
            }
            if (_game == null || cam == null)
                return;
            bool through = sys.CameraThroughRecently && !PortalSystem.Suspended;
            if (through)
            {
                if (_ear == null)
                {
                    if (_root == null)
                    {
                        _root = new GameObject("RealPortalAudio");
                        Object.DontDestroyOnLoad(_root);
                    }
                    var go = new GameObject("RealPortalEar");
                    go.transform.SetParent(_root.transform, false);
                    _ear = go.AddComponent<AudioListener>();
                    _ear.enabled = false;
                }
                Matrix4x4 back = sys.CameraMatrix.inverse;
                _ear.transform.SetPositionAndRotation(back.MultiplyPoint3x4(cam.transform.position), PortalMath.Rotation(back) * cam.transform.rotation);
            }
            if (_ear == null)
                return;
            // One listener at a time (AudioMan finds the enabled one again when it changes).
            if (through && !_ear.enabled)
            {
                _game.enabled = false;
                _ear.enabled = true;
            }
            else if (!through && _ear.enabled)
            {
                _ear.enabled = false;
                _game.enabled = true;
            }
        }

        internal void Clear()
        {
            foreach (Twin twin in _twins.Values)
                Give(twin.To);
            _twins.Clear();
            if (_ear != null && _ear.enabled)
            {
                _ear.enabled = false;
                if (_game != null)
                    _game.enabled = true;
            }
        }
    }
}
