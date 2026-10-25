namespace RealPortal
{
    // What the vanilla portal effect inside the frame (the swirl, its sparks and light: TeleportWorld.m_target_found)
    // does on a live real portal. The "connected" burst and the glowing frame are the same in both.
    public enum PortalEffectsMode
    {
        // Only while the portal does not let someone through (ore, a creature the settings stop): the swirl stands
        // over the wall then, and the rest of the time the view through the window stays clear.
        Window,
        // As the game shows it on an open portal: whenever the closest player stands within activation range and may
        // pass. It covers part of the view through the window.
        Vanilla,
    }
}
