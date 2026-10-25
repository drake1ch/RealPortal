using System.Collections.Generic;
using UnityEngine;

namespace RealPortal
{
    // OWNER: gpu (S3), written by the integrator. The game camera's window passes with the WindowComposite
    // (Docs\S2S3-CONTRACTS.md §8.1, §9): what UpdatePatch and UpdateSky (PortalRenderer.cs) need to know about it, and the
    // composite's share of PortalRenderer.Destroy.
    //
    // While the manager drives the frame (Pipeline set) and Win.CompositeMRT is in force, the patch buffer records one
    // AUXBASE pass and one AUXMRT draw per window instead of the depth copies and the DEPTHPATCH, SKYDEPTH and MASK draws;
    // the renderer's public depth textures (SkyDepthTexture, ViewDepthTexture, WindowMaskTexture) then point at the
    // composite's targets. Those belong to the composite: the renderer never Kills them (ScreenDepth would, on a size
    // change, and Destroy would) and lets go of them when the mechanism goes off. With Win.Occlusion and without the MRT
    // pass the patch buffer records the ids alone (WINDOWID). Without a manager, or with neither in force, both methods
    // are exactly as after S1.
    public sealed partial class PortalRenderer
    {
        // The renderer's public textures that are the composite's targets right now (never Killed by the renderer).
        bool _skyAlias, _viewAlias, _maskAlias;
        // This frame's patch buffer recorded the AUX pass: the composite's targets hold the fog copy, the view depth, the
        // sky depth and the mask (UpdateSky takes them from there).
        bool _auxThisFrame;
        // What the attached buffers were recorded with (a change takes them off and puts them on again, as for the
        // post-S1 modes): the MRT pass, the ids alone, RESTOREVIEW, and the composite's targets they bind by reference.
        bool _mrtPatched, _idsPatched, _restoreViewPatched;
        RenderTexture _auxFogPatched, _auxViewPatched;
        // The frame whose patch buffer draws the visibility ids (AUXMRT or WINDOWID): only then are they counted.
        int _idsDrawnFrame = -100;

        // The composite of the manager driving this frame, or null (no manager, nothing of S2/S3 in force: post-S1).
        WindowComposite DrivingComposite => Pipeline != null ? Pipeline.Composite as WindowComposite : null;

        /// <summary>This frame's patch buffer draws the visibility ids (the manager schedules the counts only then).</summary>
        internal bool IdsDrawnThisFrame => _idsDrawnFrame == Time.frameCount;

        // Some window the game camera may see this frame: shown (fresh, stale or resolved), or in the frustum with a
        // visibility id (hidden by occlusion, or the swirl: its count is how it is seen again). None: the ids are not drawn.
        static bool AnyWindowInView(IList<PortalWindow> windows)
        {
            for (int i = 0; i < windows.Count; i++)
            {
                PortalWindow w = windows[i];
                if (w.RenderedThisFrame)
                    return true;
                WindowState s = w.State;
                if (s != null && s.VisId != 0 && s.InFrustum)
                    return true;
            }
            return false;
        }

        // A texture of the renderer's that is about to be made or reused by ScreenDepth must not be the composite's.
        void DropSkyAlias()
        {
            if (_skyAlias)
            {
                SkyDepthTexture = null;
                _skyAlias = false;
            }
        }

        void DropViewAlias()
        {
            if (_viewAlias)
            {
                ViewDepthTexture = null;
                _viewAlias = false;
            }
        }

        void DropMaskAlias()
        {
            if (_maskAlias)
            {
                WindowMaskTexture = null;
                _maskAlias = false;
            }
        }

        // Window draws this frame put on the game camera, for the lab's draw count (WindowComposite.DrawsLastFrame): only
        // while the manager drives the frame.
        void CountCompositeDraws(int n)
        {
            if (n > 0)
                DrivingComposite?.CountDraws(n);
        }

        // Destroy runs right after this and Kills the renderer's textures: the composite's own targets are not the
        // renderer's to Kill (the composite releases them in its Dispose).
        partial void DestroyComposite()
        {
            DropSkyAlias();
            DropViewAlias();
            DropMaskAlias();
            _auxThisFrame = false;
            _auxFogPatched = _auxViewPatched = null;
        }
    }
}
