using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealPortal.EditorTools
{
    // Offscreen checks of the portal math and the window renderer, no game needed.
    // Builds a test scene, renders views through a window and compares each window pixel with a reference
    // render taken directly from the matching viewpoint behind the partner portal.
    // Batch mode: Unity.exe -batchmode -quit -projectPath Unity -executeMethod RealPortal.EditorTools.RealPortalTests.Run
    public static class RealPortalTests
    {
        const int W = 960, H = 540;
        const int WindowLayer = 8;
        static readonly StringBuilder Report = new StringBuilder();
        static int s_failures;

        static string OutDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "TestOut"));

        [MenuItem("Real Portal/Run offscreen tests")]
        public static void Run()
        {
            Report.Clear();
            s_failures = 0;
            Directory.CreateDirectory(OutDir);
            MathChecks();
            RenderChecks();
            Report.AppendLine(s_failures == 0 ? "RESULT: all checks passed" : $"RESULT: {s_failures} check(s) failed");
            File.WriteAllText(Path.Combine(OutDir, "report.txt"), Report.ToString());
            Debug.Log("[RealPortalTests]\n" + Report);
            if (Application.isBatchMode)
                EditorApplication.Exit(s_failures == 0 ? 0 : 1);
        }

        static void Check(bool ok, string what)
        {
            Report.AppendLine((ok ? "ok   " : "FAIL ") + what);
            if (!ok)
                s_failures++;
        }

        static PortalFrame Frame(Vector3 center, float yaw) =>
            new PortalFrame { Center = center, Rotation = Quaternion.Euler(0f, yaw, 0f), HalfSize = new Vector2(1.4f, 1.4f), Round = true };

        static void MathChecks()
        {
            PortalFrame a = Frame(new Vector3(0f, 1.4f, 0f), 0f);
            PortalFrame b = Frame(new Vector3(25f, 1.4f, 8f), 90f);
            Matrix4x4 ab = PortalMath.Pair(a, b), ba = PortalMath.Pair(b, a);
            Matrix4x4 round = ba * ab;
            float err = 0f;
            for (int i = 0; i < 16; i++)
                err = Mathf.Max(err, Mathf.Abs(round[i] - Matrix4x4.identity[i]));
            Check(err < 1e-4f, $"Pair(B,A)*Pair(A,B) = identity (max err {err:g3})");

            Vector3 front = a.Center + a.Forward * 2f + Vector3.up * 0.3f;
            Vector3 mapped = ab.MultiplyPoint3x4(front);
            Check(Mathf.Abs(b.SignedDistance(mapped) + 2f) < 1e-3f, $"2 m in front of A maps to 2 m behind B ({b.SignedDistance(mapped):0.###})");
            Check(Mathf.Abs(mapped.y - front.y) < 1e-4f, "height above the portal centre is kept");
            Vector3 into = -a.Forward * 5f;
            Vector3 outOf = ab.MultiplyVector(into);
            Check(Vector3.Dot(outOf.normalized, b.Forward) > 0.9999f && Mathf.Abs(outOf.magnitude - 5f) < 1e-4f,
                "velocity into A's front leaves B's front at the same speed");
            Vector3 intoBack = a.Forward * 3f;
            Check(Vector3.Dot(ab.MultiplyVector(intoBack).normalized, -b.Forward) > 0.9999f, "entering A from the back leaves B's back");
            Quaternion q = PortalMath.Rotation(ab);
            Check(Quaternion.Angle(q, Quaternion.Euler(0f, 90f + 180f, 0f)) < 0.01f, $"pair rotation is a yaw of 270 deg ({q.eulerAngles})");

            bool crossed = a.Crosses(a.Center + new Vector3(0.5f, 0.2f, 0.3f), a.Center + new Vector3(0.5f, 0.2f, -0.3f), out Vector3 hit, out float t);
            Check(crossed && Mathf.Abs(t - 0.5f) < 1e-4f && Mathf.Abs(hit.z) < 1e-4f, "segment through the opening crosses at the plane");
            Check(!a.Crosses(a.Center + new Vector3(1.3f, 1.3f, 0.3f), a.Center + new Vector3(1.3f, 1.3f, -0.3f), out _, out _),
                "segment outside the round opening does not cross");
            Check(!a.Crosses(a.Center + new Vector3(0f, 0f, 0.3f), a.Center + new Vector3(0f, 0f, 0.1f), out _, out _),
                "segment that stays in front does not cross");

            // Oblique projection must keep the far side and drop points between the camera and the plane.
            Matrix4x4 p = Matrix4x4.Perspective(60f, 16f / 9f, 0.3f, 500f);
            Matrix4x4 view = Matrix4x4.TRS(new Vector3(0f, 1.5f, 5f), Quaternion.identity * Quaternion.Euler(0f, 180f, 0f), new Vector3(1f, 1f, -1f)).inverse;
            Vector4 plane = PortalMath.CameraSpacePlane(view, Vector3.zero, Vector3.back, 0f);
            Matrix4x4 obl = PortalMath.Oblique(p, plane);
            Vector4 nearPt = obl * view * new Vector4(0f, 1.5f, 2f, 1f);
            Vector4 farPt = obl * view * new Vector4(0f, 1.5f, -2f, 1f);
            Check(nearPt.z < -nearPt.w, "oblique plane clips a point between camera and portal");
            Check(farPt.z >= -farPt.w && farPt.z <= farPt.w, "oblique plane keeps a point behind the portal");
        }

        class Scene
        {
            public Camera Main;
            public PortalRenderer Renderer;
            public PortalWindow A, B;
            public List<PortalWindow> Windows;
            public RenderTexture Target;
            public Texture2D Magenta;
            public Material Util;
        }

        static void RenderChecks()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(RealPortalBuild.ShaderPath);
            Check(shader != null && shader.isSupported, "window shader loads and is supported");
            var windowMat = new Material(shader);
            var util = new Material(shader);

            BuildWorld();
            var s = new Scene();
            var camGo = new GameObject("Main");
            s.Main = camGo.AddComponent<Camera>();
            s.Main.clearFlags = CameraClearFlags.SolidColor;
            s.Main.backgroundColor = new Color(0.55f, 0.7f, 0.9f);
            s.Main.nearClipPlane = 0.3f;
            s.Main.farClipPlane = 1000f;
            s.Main.fieldOfView = 65f;
            s.Main.allowHDR = false;
            s.Main.allowMSAA = false;
            s.Target = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            s.Main.targetTexture = s.Target;
            s.Magenta = new Texture2D(1, 1);
            s.Magenta.SetPixel(0, 0, Color.magenta);
            s.Magenta.Apply();
            s.Util = util;

            var settings = new PortalRenderSettings { Resolution = 1f, ClearFlags = CameraClearFlags.SolidColor, ShadowDistance = -1f, LodBias = 1f };
            s.Renderer = new PortalRenderer(settings);

            PortalFrame fa = Frame(new Vector3(0f, 1.4f, 0f), 0f);
            PortalFrame fb = Frame(new Vector3(25f, 1.4f, 8f), 90f);
            s.A = new PortalWindow(fa, null, windowMat, WindowLayer);
            s.B = new PortalWindow(fb, null, windowMat, WindowLayer);
            s.A.Partner = s.B;
            s.B.Partner = s.A;
            s.Windows = new List<PortalWindow> { s.A, s.B };
            AddFrameModel(fa);
            AddFrameModel(fb);

            foreach (PortalClip clip in new[] { PortalClip.NearPlane, PortalClip.Oblique })
            {
                settings.Clip = clip;
                string tag = clip == PortalClip.NearPlane ? "" : "_oblique";
                Shot(s, "front" + tag, new Vector3(0.6f, 1.7f, 6f), fa.Center);
                Shot(s, "angled" + tag, new Vector3(5f, 1.6f, 3f), fa.Center);
                Shot(s, "behind" + tag, new Vector3(-1f, 1.8f, -5f), fa.Center);
                Shot(s, "far_small" + tag, new Vector3(3f, 2f, 45f), fa.Center);
                Shot(s, "at_plane" + tag, new Vector3(0.2f, 1.5f, 0.12f), new Vector3(0.1f, 1.3f, -5f));
                Shot(s, "at_plane_back" + tag, new Vector3(-0.3f, 1.6f, -0.1f), new Vector3(-0.3f, 1.4f, 5f));
                Shot(s, "look_at_b" + tag, new Vector3(31f, 1.7f, 9f), fb.Center);

                // The game's camera renders deferred (HDR) and the window camera follows it; the window goes
                // through its G-buffer pass there.
                s.Main.renderingPath = RenderingPath.DeferredShading;
                s.Main.allowHDR = true;
                Shot(s, "front_deferred" + tag, new Vector3(0.6f, 1.7f, 6f), fa.Center);
                Shot(s, "angled_deferred" + tag, new Vector3(5f, 1.6f, 3f), fa.Center);
                Shot(s, "at_plane_deferred" + tag, new Vector3(0.2f, 1.5f, 0.12f), new Vector3(0.1f, 1.3f, -5f));
                s.Main.renderingPath = RenderingPath.Forward;
                s.Main.allowHDR = false;
            }
            settings.Clip = PortalClip.NearPlane;

            // Half resolution, only as a picture.
            settings.Resolution = 0.5f;
            Shot(s, "front_half_res", new Vector3(0.6f, 1.7f, 6f), fa.Center, compare: false);
            settings.Resolution = 1f;

            // Facing portals for recursion pictures.
            fb.Center = new Vector3(0f, 1.4f, 12f);
            fb.Rotation = Quaternion.Euler(0f, 180f, 0f);
            settings.Recursion = 0;
            Shot(s, "facing_recursion0", new Vector3(0.4f, 1.7f, 6f), fa.Center, compare: false);
            settings.Recursion = 1;
            Shot(s, "facing_recursion1", new Vector3(0.4f, 1.7f, 6f), fa.Center, compare: false);
            Check(s.Renderer.RendersLastFrame == 2, $"recursion 1 renders the nested level ({s.Renderer.RendersLastFrame} renders, 1 with recursion 0)");
            settings.Recursion = 0;

            s.Renderer.Destroy();
            s.A.Destroy();
            s.B.Destroy();
        }

        static void Shot(Scene s, string name, Vector3 eye, Vector3 lookAt, bool compare = true)
        {
            s.Main.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(lookAt - eye));
            s.Renderer.RenderAll(s.Main, s.Windows);
            Texture2D img = Capture(s.Main, s.Target);
            Save(img, name);
            if (!compare)
                return;

            // Which pixels show the entry window: draw the same frame with it forced to pure magenta.
            PortalWindow entry = Nearest(s, eye, lookAt);
            entry.ShowTexture(s.Magenta);
            Texture2D mask = Capture(s.Main, s.Target);
            entry.ShowOwn();

            // Reference: the view from the mapped viewpoint, clipped at the exit plane, without the exit window.
            Texture2D truth = Reference(s, entry, eye, s.Main.transform.rotation);
            Save(truth, name + "_reference");

            Color[] a = img.GetPixels(), m = mask.GetPixels(), t = truth.GetPixels();
            int window = 0, bad = 0;
            for (int i = 0; i < a.Length; i++)
            {
                Color c = m[i];
                if (!(c.r > 0.98f && c.g < 0.02f && c.b > 0.98f))
                    continue;
                window++;
                float d = Mathf.Max(Mathf.Abs(a[i].r - t[i].r), Mathf.Max(Mathf.Abs(a[i].g - t[i].g), Mathf.Abs(a[i].b - t[i].b)));
                if (d > 0.08f)
                    bad++;
            }
            float frac = window > 0 ? (float)bad / window : 1f;
            Check(window > 200 && frac < 0.02f,
                $"{name}: {window} window px, {bad} differ from reference ({frac:P2}), renders {s.Renderer.RendersLastFrame}");

            // Virtual depth: what the main camera's depth effects read in the window must be the depth seen
            // through it (the reference camera's own depth).
            if (!s.Renderer.Settings.VirtualDepth)
                return;
            float[] dm = Depth(s, s.Main), dr = ReferenceDepth(s, entry, eye, s.Main.transform.rotation);
            int dbad = 0;
            for (int i = 0; i < a.Length; i++)
            {
                Color c = m[i];
                if (!(c.r > 0.98f && c.g < 0.02f && c.b > 0.98f))
                    continue;
                if (Mathf.Abs(dm[i] - dr[i]) > 0.03f * dr[i] + 0.05f)
                    dbad++;
            }
            float dfrac = window > 0 ? (float)dbad / window : 1f;
            Check(dfrac < 0.03f, $"{name}: depth behind the window = depth seen through it in {100f - dfrac * 100f:0.0}% of window px");
        }

        // Linear eye depth from the camera's _CameraDepthTexture as its later passes see it.
        static float[] Depth(Scene s, Camera cam)
        {
            var rt = new RenderTexture(W, H, 0, RenderTextureFormat.RFloat);
            var cb = new CommandBuffer { name = "test depth" };
            cb.Blit(BuiltinRenderTextureType.CameraTarget, rt, s.Util, 3);
            cam.depthTextureMode |= DepthTextureMode.Depth;
            cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, cb);
            cam.targetTexture = s.Target;
            cam.Render();
            cam.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, cb);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RFloat, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            float[] d = tex.GetPixels().Select(c => c.r).ToArray();
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);
            return d;
        }

        static PortalWindow Nearest(Scene s, Vector3 eye, Vector3 lookAt)
        {
            float da = Vector3.Distance(lookAt, s.A.Frame.Center), db = Vector3.Distance(lookAt, s.B.Frame.Center);
            if (Mathf.Abs(da - db) < 0.01f)
                return Vector3.Distance(eye, s.A.Frame.Center) < Vector3.Distance(eye, s.B.Frame.Center) ? s.A : s.B;
            return da < db ? s.A : s.B;
        }

        static float[] ReferenceDepth(Scene s, PortalWindow entry, Vector3 eye, Quaternion rot)
        {
            float[] d = null;
            Reference(s, entry, eye, rot, cam => d = Depth(s, cam));
            return d;
        }

        static Texture2D Reference(Scene s, PortalWindow entry, Vector3 eye, Quaternion rot, System.Action<Camera> extra = null)
        {
            PortalFrame exit = entry.Partner.Frame;
            Matrix4x4 m = PortalMath.Pair(entry.Frame, exit);
            var go = new GameObject("Reference");
            Camera cam = go.AddComponent<Camera>();
            cam.CopyFrom(s.Main);
            cam.enabled = false;
            cam.transform.SetPositionAndRotation(m.MultiplyPoint3x4(eye), PortalMath.Rotation(m) * rot);
            float far = Mathf.Min(s.Main.farClipPlane, s.Renderer.Settings.FarClip);
            if (s.Renderer.Settings.Clip == PortalClip.NearPlane)
            {
                cam.nearClipPlane = PortalRenderer.NearToOpening(exit, cam.worldToCameraMatrix, s.Main.nearClipPlane);
                cam.farClipPlane = far;
            }
            else
            {
                Matrix4x4 p = Matrix4x4.Perspective(s.Main.fieldOfView, s.Main.aspect, s.Main.nearClipPlane, far);
                float side = exit.SignedDistance(cam.transform.position);
                Vector3 visible = side > 0f ? -exit.Forward : exit.Forward;
                if (Mathf.Abs(side) > s.Main.nearClipPlane + 0.05f)
                    cam.projectionMatrix = PortalMath.Oblique(p, PortalMath.CameraSpacePlane(cam.worldToCameraMatrix, exit.Center, visible, 0.01f));
            }
            bool wasOn = entry.Partner.Renderer.enabled;
            entry.Partner.Renderer.enabled = false;
            foreach (PortalWindow w in s.Windows)
                w.ShowFallback();
            Texture2D img = Capture(cam, s.Target);
            extra?.Invoke(cam);
            entry.Partner.Renderer.enabled = wasOn;
            foreach (PortalWindow w in s.Windows)
                w.ShowOwn();
            Object.DestroyImmediate(go);
            return img;
        }

        static Texture2D Capture(Camera cam, RenderTexture rt)
        {
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            return tex;
        }

        static void Save(Texture2D tex, string name) =>
            File.WriteAllBytes(Path.Combine(OutDir, name + ".png"), tex.EncodeToPNG());

        // Asymmetric props so a wrong rotation or mirror is obvious in the pictures.
        static void BuildWorld()
        {
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            light.shadows = LightShadows.Soft;

            Prim(PrimitiveType.Plane, new Vector3(0f, 0f, 0f), new Vector3(30f, 1f, 30f), new Color(0.45f, 0.5f, 0.42f));
            for (int x = -40; x <= 60; x += 10)
                for (int z = -40; z <= 60; z += 10)
                    Prim(PrimitiveType.Cube, new Vector3(x, 0.05f, z), new Vector3(0.4f, 0.1f, 0.4f), new Color(0.3f, 0.3f, 0.3f));

            // Around A (origin, facing +Z): warm colours.
            Prim(PrimitiveType.Cube, new Vector3(-3f, 1f, 4f), new Vector3(1f, 2f, 1f), new Color(0.9f, 0.2f, 0.1f));
            Prim(PrimitiveType.Sphere, new Vector3(3f, 0.7f, 3f), Vector3.one * 1.4f, new Color(1f, 0.6f, 0.1f));
            Prim(PrimitiveType.Cube, new Vector3(0f, 3f, -8f), new Vector3(6f, 6f, 1f), new Color(0.7f, 0.1f, 0.4f));

            // Around B (25, 8, facing +X): cold colours, plus a tall pole on B's left.
            Prim(PrimitiveType.Cube, new Vector3(30f, 1f, 5f), new Vector3(1f, 2f, 1f), new Color(0.1f, 0.3f, 0.9f));
            Prim(PrimitiveType.Sphere, new Vector3(29f, 0.8f, 11f), Vector3.one * 1.6f, new Color(0.1f, 0.8f, 0.3f));
            Prim(PrimitiveType.Cylinder, new Vector3(27f, 4f, 12f), new Vector3(0.5f, 4f, 0.5f), new Color(0.95f, 0.95f, 0.2f));
            Prim(PrimitiveType.Cube, new Vector3(18f, 3f, 8f), new Vector3(1f, 6f, 6f), new Color(0.2f, 0.8f, 0.8f));
            Prim(PrimitiveType.Cube, new Vector3(45f, 2f, 0f), new Vector3(3f, 4f, 3f), new Color(0.5f, 0.2f, 0.9f));
        }

        static void AddFrameModel(PortalFrame f)
        {
            // A ring of thin posts around the opening, like the portal frame.
            for (int i = 0; i < 12; i++)
            {
                float a = Mathf.PI * 2f * i / 12f;
                Vector3 p = f.Center + f.Rotation * new Vector3(Mathf.Cos(a) * 1.6f, Mathf.Sin(a) * 1.6f, 0f);
                Prim(PrimitiveType.Cube, p, new Vector3(0.25f, 0.25f, 0.3f), new Color(0.35f, 0.22f, 0.1f)).transform.rotation = f.Rotation;
            }
        }

        static GameObject Prim(PrimitiveType type, Vector3 pos, Vector3 scale, Color color)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.transform.position = pos;
            go.transform.localScale = scale;
            var mat = new Material(Shader.Find("Standard")) { color = color };
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }
    }
}
