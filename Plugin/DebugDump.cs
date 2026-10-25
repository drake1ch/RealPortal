using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using UnityEngine;

namespace RealPortal
{
    // On the debug key: renders the view through the nearest live window (as the player sees it now) into PNGs,
    // whole and one layer at a time on a magenta background, and writes camera and renderer details next to them.
    internal static class DebugDump
    {
        static readonly Color Magenta = new Color(1f, 0f, 1f, 1f);

        internal static string Write(PortalRenderer renderer, Camera main, List<PortalWindow> windows)
        {
            PortalWindow w = windows.Where(x => x.Enabled && x.Partner != null)
                .OrderBy(x => Vector3.Distance(x.Frame.Center, main.transform.position)).FirstOrDefault();
            if (w == null)
            {
                Plugin.Log.LogInfo("Debug dump: no live portal window");
                return null;
            }
            string dir = Path.Combine(Paths.BepInExRootPath, "RealPortalDebug", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(dir);
            int width = Mathf.Max(64, main.pixelWidth / 2), height = Mathf.Max(64, main.pixelHeight / 2);
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            rt.Create();
            var info = new StringBuilder();
            try
            {
                Vector3 camPos = PortalRenderer.ViewPosition(w, main.transform.position);
                info.AppendLine($"main camera {main.transform.position} rot {main.transform.rotation.eulerAngles}, fov {main.fieldOfView}, near {main.nearClipPlane}, path {main.actualRenderingPath}");
                info.AppendLine($"entry centre {w.Frame.Center} fwd {w.Frame.Forward}; exit centre {w.Partner.Frame.Center} fwd {w.Partner.Frame.Forward}");
                info.AppendLine($"window camera {camPos}, {w.Partner.Frame.SignedDistance(camPos):0.00} m from the exit plane");
                if (ZoneSystem.instance != null)
                    info.AppendLine($"ground under window camera {ZoneSystem.instance.GetGroundHeight(camPos):0.00}, liquid {Floating.GetLiquidLevel(camPos):0.00}");
                info.AppendLine($"settings: path {renderer.Settings.RenderingPath}, clear {renderer.Settings.ClearFlags}, far {renderer.Settings.FarClip}, " +
                                $"res {renderer.Settings.Resolution}, pixel lights {QualitySettings.pixelLightCount}");

                renderer.RenderDebug(main, windows, w, rt, main.cullingMask, null);
                Save(rt, Path.Combine(dir, "view.png"), out _);
                WriteLive(renderer, main, windows, w, dir, info);
                info.AppendLine();
                info.AppendLine("layers drawn in the window (pixels not magenta):");
                for (int layer = 0; layer < 32; layer++)
                {
                    string name = LayerMask.LayerToName(layer);
                    if ((main.cullingMask & (1 << layer)) == 0)
                        continue;
                    renderer.RenderDebug(main, windows, w, rt, 1 << layer, Magenta);
                    Read(rt, out Texture2D tex);
                    int covered = tex.GetPixels32().Count(c => !(c.r > 250 && c.g < 5 && c.b > 250));
                    if (covered > 0)
                    {
                        File.WriteAllBytes(Path.Combine(dir, $"layer_{layer:00}_{name}.png"), tex.EncodeToPNG());
                        info.AppendLine($"  {layer,2} {name,-20} {100f * covered / (width * height):0.0}%");
                    }
                    UnityEngine.Object.Destroy(tex);
                }
                info.AppendLine();
                info.AppendLine("renderers within 40 m of the window camera on the camera side of the exit plane (should be clipped):");
                foreach (string line in BehindExit(w, camPos))
                    info.AppendLine("  " + line);
            }
            catch (Exception e)
            {
                info.AppendLine("ERROR " + e);
            }
            finally
            {
                rt.Release();
                UnityEngine.Object.Destroy(rt);
            }
            info.AppendLine();
            info.AppendLine("window camera components and command buffers:");
            foreach (Behaviour b in renderer.Camera.GetComponents<Behaviour>())
                info.AppendLine($"  component {b.GetType().FullName} enabled {b.enabled}");
            foreach (UnityEngine.Rendering.CameraEvent evt in Enum.GetValues(typeof(UnityEngine.Rendering.CameraEvent)))
                foreach (UnityEngine.Rendering.CommandBuffer cb in renderer.Camera.GetCommandBuffers(evt))
                    info.AppendLine($"  {evt}: {cb.name}");
            File.WriteAllText(Path.Combine(dir, "info.txt"), info.ToString());
            Plugin.Log.LogInfo($"Debug dump written to {dir}");
            return dir;
        }

        // The live window texture of the nearest window, for a few frames in a row (to catch flicker).
        internal static void LiveFrame(Camera main, List<PortalWindow> windows, string dir, int index)
        {
            PortalWindow w = windows.Where(x => x.Enabled && x.Partner != null && x.RenderedThisFrame && x.Texture != null)
                .OrderBy(x => Vector3.Distance(x.Frame.Center, main.transform.position)).FirstOrDefault();
            if (w == null)
                return;
            var copy = new RenderTexture(w.Texture.width, w.Texture.height, 0, RenderTextureFormat.ARGB32);
            copy.Create();
            Graphics.Blit(w.Texture, copy);
            Save(copy, Path.Combine(dir, $"window_frame{index}_f{Time.frameCount}.png"), out _);
            copy.Release();
            UnityEngine.Object.Destroy(copy);
        }

        // The exact texture the window shows this frame (cropped to its screen rect), and the same crop rendered
        // again with and without shadows.
        static void WriteLive(PortalRenderer renderer, Camera main, List<PortalWindow> windows, PortalWindow w, string dir, StringBuilder info)
        {
            if (!w.RenderedThisFrame || w.Texture == null)
            {
                info.AppendLine("live window texture: not rendered this frame");
                return;
            }
            RenderTexture live = w.Texture;
            Rect r = w.TextureRect;
            info.AppendLine($"live window texture {live.width}x{live.height} {live.format}, screen rect {r}");
            var copy = new RenderTexture(live.width, live.height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            copy.Create();
            try
            {
                Graphics.Blit(live, copy);
                Save(copy, Path.Combine(dir, "window_live.png"), out _);
                renderer.RenderDebug(main, windows, w, copy, main.cullingMask, null, r, 0f);
                Save(copy, Path.Combine(dir, "window_noshadows.png"), out _);
                renderer.RenderDebug(main, windows, w, copy, main.cullingMask, null, r, QualitySettings.shadowDistance);
                Save(copy, Path.Combine(dir, "window_shadows.png"), out _);
            }
            finally
            {
                copy.Release();
                UnityEngine.Object.Destroy(copy);
            }
        }

        static IEnumerable<string> BehindExit(PortalWindow w, Vector3 camPos)
        {
            PortalFrame exit = w.Partner.Frame;
            float camSide = Mathf.Sign(exit.SignedDistance(camPos));
            return UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)
                .Where(r => r.enabled && r.gameObject.activeInHierarchy && r.bounds.SqrDistance(camPos) < 40f * 40f
                            && Mathf.Sign(exit.SignedDistance(r.bounds.center)) == camSide)
                .OrderBy(r => r.bounds.SqrDistance(camPos))
                .Take(40)
                .Select(r => $"{NameOf(r.transform)} layer {LayerMask.LayerToName(r.gameObject.layer)}, " +
                             $"shader {(r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "none")}, " +
                             $"{Mathf.Sqrt(r.bounds.SqrDistance(camPos)):0.0} m");
        }

        static string NameOf(Transform t) => t.parent != null ? t.parent.name + "/" + t.name : t.name;

        static void Read(RenderTexture rt, out Texture2D tex)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
        }

        static void Save(RenderTexture rt, string path, out Texture2D tex)
        {
            Read(rt, out tex);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
        }
    }
}
