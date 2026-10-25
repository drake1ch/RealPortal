using System;
using HarmonyLib;
using RealPortal.Engine;
using UnityEngine;

namespace RealPortal
{
    // FarWorld (Docs\FARWORLD-V3.md §3, §7 V2; ARCHITECTURE-v2.md §3.1): the far-side data layer's facade and bootstrap.
    // V2 is the foundation with objects still real: the far content of every sector a far area reaches is kept in a
    // SectorRegistry from the engine's ZDO journal (ZdoEvents) instead of relisted, areas that overlap share it, and
    // RemoteAreas creates the same objects from its lists. Partial, so the other V2 parts (areas, readiness, the virtual
    // far end) add their entry points in their own files.
    //
    // Switches (WindowFeatures: "feat Far.<Name> on|off", section "Engine"): Far.V2 is the master; Far.Journal,
    // Far.Registry, Far.Readiness3 and Far.VirtualEnd are its parts and act only with it (JournalOn and the rest below
    // test both). The engine's ZdoJournal (default on) records only while somebody is subscribed, i.e. only while the
    // registry has the journal open.
    //  - Off (Far.V2 or Far.Registry): there is no registry (Registry is null), nothing is subscribed to the journal or
    //    the instance events, and RemoteAreas runs exactly as before. The only cost is one bool test a frame.
    //  - On: the registry is made once a world stands (ZNet, ZDOMan, ZNetScene, ZoneSystem) and dropped when the scene
    //    goes, when a switch turns it off, or after an error (then it stays off until a switch is set again, and
    //    RemoteAreas takes over as before). Each drop or make moves Generation and raises RegistryChanged; handles from
    //    a dropped registry are dead (AreaSectors.Live false).
    // Update runs once a frame: from PortalSystem.Update, first thing (before RemoteAreas.Update), and from a ZNetScene.Update
    // postfix should nothing have called it that frame. The registry also drains the journal before every answer
    // (SectorRegistry.Sync), so the keep-alive pass inside ZNetScene.Update is never behind the game either way.
    [HarmonyPatch]
    internal static partial class FarWorld
    {
        static SectorRegistry s_registry;
        static int s_frame = -1, s_ensureFrame = -1;
        static bool s_switched = true;
        // Stopped after an error: stays off until a switch is set again.
        static bool s_broken;
        static int s_generation;
        static int s_errors;
        static float s_errorAt;

        // The switches as they act (each part only with the master).
        internal static bool On => FarFlags.V2;
        internal static bool RegistryOn => FarFlags.V2 && FarFlags.Registry;
        internal static bool JournalOn => FarFlags.V2 && FarFlags.Registry && FarFlags.Journal;
        internal static bool Readiness3On => FarFlags.V2 && FarFlags.Readiness3;
        internal static bool VirtualEndOn => FarFlags.V2 && FarFlags.VirtualEnd;

        // Moves whenever the registry is made or dropped.
        internal static int Generation => s_generation;
        internal static event Action RegistryChanged;

        // This world's registry, made on first use while Far.V2 and Far.Registry are on and a world stands; null
        // otherwise (then far areas list themselves, as before).
        internal static SectorRegistry Registry
        {
            get
            {
                Ensure();
                return s_registry;
            }
        }

        // Bumped by every portal change the journal saw (made, connected or tagged, destroyed; anywhere), 0 without
        // the journal: for whoever pairs portals or keeps their frames.
        internal static long PortalRevision => s_registry != null ? s_registry.PortalRevision : 0;

        // A switch was set (WindowFeatures' Bind; also as they register in Plugin.Awake, before any world): the next
        // Update or query brings FarWorld up or down. Clears a stop after an error.
        internal static void Switched()
        {
            s_switched = true;
            s_broken = false;
        }

        // Once a frame (PortalSystem.Update first thing, or the ZNetScene.Update postfix): the registry made or dropped as
        // the switches and the world say, then its frame (journal drained, sectors built within budget, one audited).
        internal static void Update()
        {
            int frame = Time.frameCount;
            if (frame == s_frame)
                return;
            s_frame = frame;
            if (s_registry == null && !FarFlags.V2)
            {
                s_switched = false;
                if (FarProxies.Alive)
                    FarProxies.Update(null);
                return;
            }
            try
            {
                Ensure();
                s_registry?.Update();
                // Far.Proxies: the proxies follow the registry's frame (its builds and the journal drained above).
                FarProxies.Update(s_registry);
            }
            catch (Exception e)
            {
                Failed(e);
            }
        }

        // The scene went (PortalSystem.Clear, the ZNetScene.OnDestroy postfix): everything of this world is dropped.
        internal static void Clear()
        {
            Stop("the scene went");
            FarProxies.Clear();
        }

        static void Ensure()
        {
            int frame = Time.frameCount;
            if (frame == s_ensureFrame && !s_switched)
                return;
            s_ensureFrame = frame;
            s_switched = false;
            bool want = RegistryOn && !s_broken && WorldStands();
            if (want && s_registry == null)
                Start();
            else if (!want && s_registry != null)
                Stop(s_broken ? "an error" : !RegistryOn ? "switched off" : "no world");
        }

        static bool WorldStands() =>
            ZNet.instance != null && ZDOMan.instance != null && ZNetScene.instance != null && ZoneSystem.instance != null;

        static void Start()
        {
            s_registry = new SectorRegistry(++s_generation);
            Plugin.Log.LogInfo($"FarWorld: on (generation {s_generation}; journal {(JournalOn ? "on" : "off")}, readiness3 {(Readiness3On ? "on" : "off")}, " +
                               $"virtual end {(VirtualEndOn ? "on" : "off")}; engine ZdoJournal {(ZdoJournalModule.Feature.On ? "on" : "off")}, " +
                               $"{(ZdoJournal.IsInstalled ? ZdoJournal.SelfCheck : "journal not installed")})");
            Raise();
        }

        static void Stop(string why)
        {
            if (s_registry == null)
                return;
            SectorRegistry r = s_registry;
            s_registry = null;
            s_generation++;
            try
            {
                r.Dispose();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("FarWorld: dropping the registry: " + e);
            }
            Plugin.Log.LogInfo($"FarWorld: off ({why})");
            Raise();
        }

        static void Raise()
        {
            try
            {
                RegistryChanged?.Invoke();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("FarWorld: RegistryChanged handler: " + e);
            }
        }

        // An exception in FarWorld's own frame: logged (at most one every 10 s in full), and FarWorld stops until a
        // switch is set again, so RemoteAreas goes on as before instead of failing every frame.
        static void Failed(Exception e)
        {
            s_errors++;
            if (Time.unscaledTime >= s_errorAt)
            {
                s_errorAt = Time.unscaledTime + 10f;
                Plugin.Log.LogError($"FarWorld: error {s_errors}, stopping until a Far.* switch is set again: {e}");
            }
            s_broken = true;
            Stop("an error");
        }

        // Drives Update once a frame while FarWorld is on, should PortalSystem.Update not have yet (this comes after the
        // game's object pass, whose keep-alive drained the journal through the registry already).
        [HarmonyPostfix, HarmonyPatch(typeof(ZNetScene), "Update")]
        static void SceneUpdated()
        {
            if (FarFlags.V2 || s_registry != null)
                Update();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(ZNetScene), "OnDestroy")]
        static void SceneGone()
        {
            if (s_registry != null)
                Stop("the scene went");
            FarProxies.Clear();
        }

        // Lab ("ab settle=far"): no registry, or one that has caught up with the world.
        internal static bool LabSettled => s_registry == null || s_registry.LabSettled;

        // Lab: the switches, the registry and the engine's journal and instance events in one line.
        internal static string Describe() =>
            $"FarWorld: Far.V2 {OnOff(FarFlags.V2)} (journal {OnOff(FarFlags.Journal)}, registry {OnOff(FarFlags.Registry)}, " +
            $"readiness3 {OnOff(FarFlags.Readiness3)}, virtual end {OnOff(FarFlags.VirtualEnd)}), generation {s_generation}" +
            (s_broken ? $", stopped after {s_errors} error(s)" : "") + "; " +
            (s_registry != null ? s_registry.Describe() : "no registry") + "; " + ZdoJournal.LabStats() + "; instance events " +
            (SectorInstanceIndexModule.InstanceEventsLive ? $"live (epoch {SectorInstanceIndexModule.InstanceEpoch})" : "not live") +
            (FarFlags.Proxies || FarProxies.Drawing ? "; " + FarProxies.Describe() : "");

        static string OnOff(bool on) => on ? "on" : "off";
    }
}
