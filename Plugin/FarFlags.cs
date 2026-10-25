namespace RealPortal
{
    // Switches for twenty portals at once (Docs\PORTALS20-PLAN.md): plain fields hot paths read, set by their engine
    // features (WindowFeatures.Register: "feat Far.<Name> on|off" in the lab, section "Engine" in the config). Their
    // defaults are set there (on once the lab gate passed); off is the behaviour from before each.
    internal static class FarFlags
    {
        // A1
        internal static bool MistWarmSpread, MistTickShown;
        // A2
        internal static bool DrainCalm;
        // A3
        internal static bool ParticleWindowGate;
        // A4
        internal static bool DeferInteriors;
        // A5 (PacerBoostAll: the pacer keeps the 8 ms near every loading portal, not only the one walked up to)
        internal static bool LoadPacer, PacerBoostAll;
        // A7
        internal static bool GrassGrowBudget, GrassViewNear;
        // A8
        internal static bool KeepAliveSlice, KeepAliveVerify, NoDupAreas;
        // A9
        internal static bool CoreFirst;
        // A10
        internal static bool LinkScaling;
        // B1 (FreezeVerify: lab, the update lists FreezeLists hands over checked against the game's)
        internal static bool FreezeLists, FreezeVerify;
        // B2
        internal static bool SleepBudget;
        // Round 2 (PORTALS20-PLAN.md §7): far heightmaps, far grass renderers, particle catch-up.
        internal static bool HeightmapDrainSkip, FarRegenBudget, GrassGate, ParticleCatchupCull;
        // Round 3: the scene's keep-alive pass, far creatures' AI, zone spawning, arriving in a hall.
        internal static bool KeepAliveFast, AiStaggerFar, ZoneSpawnSplit, ArrivalPacer;
        // FARWORLD-V3 V0 (Docs\FARWORLD-V3.md §7 V0): far particle systems inside windows rendered this frame stepped every
        // frame (capped by count), not caught up round-robin within a millisecond; the mist ticks with the renders.
        internal static bool InViewStep;
        // FARWORLD-V3 V2, the FarWorld foundation with objects still real (Docs\FARWORLD-V3.md §7 V2; ARCHITECTURE-v2.md
        // §3.1-§3.4, §7 S4). V2 is the master: off, RemoteAreas runs exactly as before and nothing of FarWorld exists. The
        // others are its parts and act only with it (FarWorld.JournalOn and the rest test it too): Journal, the registry
        // followed through the engine's ZdoJournal (needs Registry; off: read from the game's tables when asked, as a relist); Registry,
        // far content kept per sector (SectorRegistry; off: RemoteAreas lists its areas itself); Readiness3,
        // CanShow/CanThrow/CanWalk; VirtualEnd, the far end from FrameCache while its portal is not loaded.
        internal static bool V2, Journal, Registry, Readiness3, VirtualEnd;
        // Parity (Docs\PARITY-PLAN.md §2, §3): a window shows exactly what the game would show standing there, and the base
        // game stays as without the mod. Each is on by default except Spawning (D3, the user's decision 25.10: no new
        // spawns at a far side); off is the behaviour from before each. Far areas, ownership, loading and crossing (P1, P3,
        // P4, P5, P6, P15, P26, P27, P28, P38):
        internal static bool SectorSleep, ClaimLikeGame, SideReady, CrossAtOnce, TrackUntilListed, DupEdge, MpParity, LiveRecheck,
            VisibleNear, FullAreaZones;
        // Far grass, the removal tick and the arrival switch of the weather (P11, P29, P47, P37, P40):
        internal static bool GrassAhead, GrassGateAhead, GrassTargetedEase, KeepAliveEveryTick, ArriveOnCross;
        // The far side's effects, weather and the game's own gates there (P12, P16 with P35, P17, P20, P21 with P34 and
        // P46, P30, P36):
        internal static bool ParticlesByView, LocalWeatherSim, Viewer, Spawning, MistParity, WeatherParity, ProbeLive, SlowVisibleNear;
        // FARWORLD-V3 V3 (Docs\FARWORLD-V3.md §7 V3; ARCHITECTURE-v2.md §3.5-§3.7, §3.12): far objects the classifier proves
        // static that lie outside every far area's active area are proxies drawn into the windows, not instances (FarProxies).
        // Acts only with Far.V2, Far.Registry and Far.Journal; off by default until its lab gate.
        internal static bool Proxies;
    }
}
