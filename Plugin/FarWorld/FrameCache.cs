using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace RealPortal
{
    // FrameCache (ARCHITECTURE-v2.md §3.9; FARWORLD-V3.md §7 V2): the opening of every portal prefab as PortalLink measured
    // it on a real one (the wooden disc from the Geometry config, any other frame by its 48 rays, or the trigger's box),
    // relative to the portal's root, so that a far portal which is not loaded can be stood in for by its VirtualEnd: the
    // same opening placed at the far ZDO's pose. Kept in BepInEx\cache\RealPortal\frames.json, one entry per prefab (its
    // name and stable hash), and only entries this mod's version wrote are read back (another version measures again).
    // A prefab never seen real has no entry: its far end is not known until it has loaded once.
    // Learnt only under Far.V2 and Far.VirtualEnd (PortalLink.Refresh, once per measurement). Written at most every 5 s
    // after a change, and when the scene goes (FarWorld.ClearAreas) or the game quits (PortalSystem.OnDestroy).
    internal static class FrameCache
    {
        internal sealed class Entry
        {
            public string Prefab;
            public int Hash;
            // The opening's centre from the portal's root (the root's rotation undone, world metres), its half size, form
            // and how high its centre stands above the portal's floor (PortalFrame.BaseDrop).
            public Vector3 Local;
            public Vector2 Half;
            public bool Round;
            public Vector2[] Shape;
            public float Drop;
            public string Source;
        }

        const int FileVersion = 1;
        const float SaveDelay = 5f;
        // Two measurements of one prefab this close are the same opening (what PortalLink measures is deterministic; the
        // margin only absorbs float noise from the root's pose).
        const float SameMetres = 0.005f, SameShape = 0.002f;

        static readonly Dictionary<int, Entry> s_entries = new Dictionary<int, Entry>();
        static bool s_loaded, s_dirty;
        static float s_saveAt;
        static int s_version;

        // Lab: entries read from the file, learnt (new or measured differently), files written, write or read errors.
        internal static int LabLoaded, LabLearned, LabRelearned, LabSaves, LabErrors;

        // Moves whenever an entry is added or replaced (VirtualEnd places its frame again).
        internal static int Version => s_version;

        internal static int Count
        {
            get
            {
                Load();
                return s_entries.Count;
            }
        }

        static string FilePath => Path.Combine(BepInEx.Paths.CachePath, "RealPortal", "frames.json");

        internal static bool TryGet(int prefab, out Entry e)
        {
            Load();
            return s_entries.TryGetValue(prefab, out e);
        }

        // A real portal's opening, as just measured (PortalLink.UpdateFrame) with its root's pose: kept for its prefab, or
        // replaced when it differs from what was kept.
        internal static void Learn(int prefab, string name, Vector3 rootPos, Quaternion rootRot, PortalFrame f, string source)
        {
            if (prefab == 0 || f == null)
                return;
            Load();
            Vector3 local = Quaternion.Inverse(rootRot) * (f.Center - rootPos);
            if (s_entries.TryGetValue(prefab, out Entry old) && Same(old, local, f))
                return;
            var e = new Entry
            {
                Prefab = name ?? "",
                Hash = prefab,
                Local = local,
                Half = f.HalfSize,
                Round = f.Round,
                Shape = f.Shape != null ? (Vector2[])f.Shape.Clone() : null,
                Drop = f.BaseDrop,
                Source = source ?? "",
            };
            if (old != null)
            {
                LabRelearned++;
                Plugin.Debug($"FrameCache: {e.Prefab} measured differently ({source}): centre {old.Local:F3} -> {local:F3}, half size {old.Half:F3} -> {f.HalfSize:F3}");
            }
            else
                Plugin.Debug($"FrameCache: {e.Prefab} learnt ({source}): centre {local:F3} from its root, half size {f.HalfSize:F3}, {(f.Shape != null ? "outline" : f.Round ? "round" : "box")}");
            s_entries[prefab] = e;
            LabLearned++;
            s_version++;
            if (!s_dirty)
            {
                s_dirty = true;
                s_saveAt = Time.unscaledTime + SaveDelay;
            }
        }

        // The prefab's opening placed at a pose (a far ZDO's position and rotation), into `into`; false when the prefab has
        // no entry yet. As PortalLink.UpdateFrame sets a real one: the plane turned by the root's yaw only.
        internal static bool Place(int prefab, Vector3 pos, Quaternion rot, PortalFrame into)
        {
            if (into == null || !TryGet(prefab, out Entry e))
                return false;
            into.Rotation = Quaternion.Euler(0f, rot.eulerAngles.y, 0f);
            into.Center = pos + rot * e.Local;
            into.HalfSize = e.Half;
            into.Round = e.Round;
            into.Shape = e.Shape;
            into.BaseDrop = e.Drop;
            return true;
        }

        static bool Same(Entry e, Vector3 local, PortalFrame f)
        {
            if (e.Round != f.Round || (e.Local - local).sqrMagnitude > SameMetres * SameMetres || (e.Half - f.HalfSize).sqrMagnitude > SameMetres * SameMetres
                || Mathf.Abs(e.Drop - f.BaseDrop) > SameMetres)
                return false;
            if ((e.Shape == null) != (f.Shape == null))
                return false;
            if (e.Shape == null)
                return true;
            if (e.Shape.Length != f.Shape.Length)
                return false;
            for (int i = 0; i < e.Shape.Length; i++)
                if ((e.Shape[i] - f.Shape[i]).sqrMagnitude > SameShape * SameShape)
                    return false;
            return true;
        }

        // Once a frame (AreaPolicy, under Far.VirtualEnd): a change is written once it has stood for SaveDelay.
        internal static void Tick()
        {
            if (s_dirty && Time.unscaledTime >= s_saveAt)
                Save();
        }

        // Now, if anything changed (the scene goes, the game quits).
        internal static void Flush()
        {
            if (s_dirty)
                Save();
        }

        // ---- the file ------------------------------------------------------------------------------------

        static void Load()
        {
            if (s_loaded)
                return;
            s_loaded = true;
            string path = FilePath;
            try
            {
                if (!File.Exists(path))
                    return;
                if (!(Json.Parse(File.ReadAllText(path)) is Dictionary<string, object> root) || Num(root, "version") != FileVersion
                    || !(root.TryGetValue("frames", out object list) && list is List<object> frames))
                    return;
                foreach (object o in frames)
                {
                    if (!(o is Dictionary<string, object> d) || Str(d, "mod") != Plugin.Version)
                        continue;
                    string name = Str(d, "prefab");
                    int hash = (int)Num(d, "hash");
                    // The name's stable hash is the key the game uses: an entry whose two disagree is not trusted.
                    if (string.IsNullOrEmpty(name) || name.GetStableHashCode() != hash)
                        continue;
                    var e = new Entry
                    {
                        Prefab = name,
                        Hash = hash,
                        Local = Vec3(d, "center"),
                        Half = Vec2(d, "half"),
                        Round = d.TryGetValue("round", out object r) && r is bool b && b,
                        Drop = (float)Num(d, "drop"),
                        Source = Str(d, "source"),
                    };
                    if (d.TryGetValue("shape", out object s) && s is List<object> pts && pts.Count >= 6 && pts.Count % 2 == 0)
                    {
                        e.Shape = new Vector2[pts.Count / 2];
                        for (int i = 0; i < e.Shape.Length; i++)
                            e.Shape[i] = new Vector2(ToFloat(pts[i * 2]), ToFloat(pts[i * 2 + 1]));
                    }
                    if (e.Half.x <= 0f || e.Half.y <= 0f)
                        continue;
                    s_entries[hash] = e;
                    LabLoaded++;
                }
                if (s_entries.Count > 0)
                {
                    s_version++;
                    Plugin.Debug($"FrameCache: {s_entries.Count} portal frame(s) read from {path}");
                }
            }
            catch (Exception ex)
            {
                LabErrors++;
                Plugin.Log.LogWarning($"FrameCache: {path} not read ({ex.Message}); frames are measured again");
            }
        }

        static void Save()
        {
            s_dirty = false;
            string path = FilePath;
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\n  \"version\": ").Append(FileVersion).Append(",\n  \"frames\": [");
                bool first = true;
                foreach (Entry e in s_entries.Values)
                {
                    sb.Append(first ? "\n    {" : ",\n    {");
                    first = false;
                    sb.Append("\"prefab\": ").Append(Quote(e.Prefab)).Append(", \"hash\": ").Append(e.Hash.ToString(CultureInfo.InvariantCulture))
                      .Append(", \"mod\": ").Append(Quote(Plugin.Version)).Append(", \"source\": ").Append(Quote(e.Source))
                      .Append(", \"center\": [").Append(F(e.Local.x)).Append(", ").Append(F(e.Local.y)).Append(", ").Append(F(e.Local.z)).Append(']')
                      .Append(", \"half\": [").Append(F(e.Half.x)).Append(", ").Append(F(e.Half.y)).Append(']')
                      .Append(", \"round\": ").Append(e.Round ? "true" : "false")
                      .Append(", \"drop\": ").Append(F(e.Drop));
                    if (e.Shape != null)
                    {
                        sb.Append(", \"shape\": [");
                        for (int i = 0; i < e.Shape.Length; i++)
                            sb.Append(i == 0 ? "" : ", ").Append(F(e.Shape[i].x)).Append(", ").Append(F(e.Shape[i].y));
                        sb.Append(']');
                    }
                    sb.Append('}');
                }
                sb.Append("\n  ]\n}\n");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // Written beside it, then moved over it: a quit half way leaves the old file, not half a new one.
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmp, path);
                LabSaves++;
            }
            catch (Exception ex)
            {
                LabErrors++;
                Plugin.Log.LogWarning($"FrameCache: {path} not written ({ex.Message})");
            }
        }

        static string F(float v) => float.IsNaN(v) || float.IsInfinity(v) ? "0" : v.ToString("R", CultureInfo.InvariantCulture);

        static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\')
                    sb.Append('\\').Append(c);
                else if (c < ' ')
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else
                    sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        static double Num(Dictionary<string, object> d, string key) => d.TryGetValue(key, out object v) && v is double n ? n : double.NaN;
        static float ToFloat(object v) => v is double n ? (float)n : 0f;
        static string Str(Dictionary<string, object> d, string key) => d.TryGetValue(key, out object v) ? v as string : null;

        static Vector3 Vec3(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out object v) && v is List<object> l && l.Count == 3 ? new Vector3(ToFloat(l[0]), ToFloat(l[1]), ToFloat(l[2])) : Vector3.zero;

        static Vector2 Vec2(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out object v) && v is List<object> l && l.Count == 2 ? new Vector2(ToFloat(l[0]), ToFloat(l[1])) : Vector2.zero;

        // Lab: the cache in one line.
        internal static string Describe()
        {
            Load();
            return $"frame cache: {s_entries.Count} prefab(s) ({LabLoaded} read, {LabLearned} learnt, {LabRelearned} measured differently, {LabSaves} written" +
                   (LabErrors > 0 ? $", {LabErrors} file error(s)" : "") + (s_dirty ? ", unsaved" : "") + ")";
        }

        // The little JSON this file needs (objects, arrays, strings, numbers, true/false/null), read once at load.
        static class Json
        {
            internal static object Parse(string s)
            {
                int i = 0;
                object v = Value(s, ref i);
                Skip(s, ref i);
                if (i != s.Length)
                    throw new FormatException("text after the value at " + i);
                return v;
            }

            static void Skip(string s, ref int i)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i]))
                    i++;
            }

            static object Value(string s, ref int i)
            {
                Skip(s, ref i);
                if (i >= s.Length)
                    throw new FormatException("unexpected end");
                char c = s[i];
                if (c == '{')
                {
                    i++;
                    var d = new Dictionary<string, object>();
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == '}')
                    {
                        i++;
                        return d;
                    }
                    while (true)
                    {
                        Skip(s, ref i);
                        string key = Text(s, ref i);
                        Skip(s, ref i);
                        Expect(s, ref i, ':');
                        d[key] = Value(s, ref i);
                        Skip(s, ref i);
                        if (i < s.Length && s[i] == ',')
                        {
                            i++;
                            continue;
                        }
                        Expect(s, ref i, '}');
                        return d;
                    }
                }
                if (c == '[')
                {
                    i++;
                    var l = new List<object>();
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ']')
                    {
                        i++;
                        return l;
                    }
                    while (true)
                    {
                        l.Add(Value(s, ref i));
                        Skip(s, ref i);
                        if (i < s.Length && s[i] == ',')
                        {
                            i++;
                            continue;
                        }
                        Expect(s, ref i, ']');
                        return l;
                    }
                }
                if (c == '"')
                    return Text(s, ref i);
                if (Word(s, ref i, "true"))
                    return true;
                if (Word(s, ref i, "false"))
                    return false;
                if (Word(s, ref i, "null"))
                    return null;
                int start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
                    i++;
                if (i == start)
                    throw new FormatException("unexpected '" + c + "' at " + i);
                return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            static bool Word(string s, ref int i, string w)
            {
                if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0)
                    return false;
                i += w.Length;
                return true;
            }

            static void Expect(string s, ref int i, char c)
            {
                if (i >= s.Length || s[i] != c)
                    throw new FormatException("'" + c + "' expected at " + i);
                i++;
            }

            static string Text(string s, ref int i)
            {
                Expect(s, ref i, '"');
                var sb = new StringBuilder();
                while (i < s.Length && s[i] != '"')
                {
                    char c = s[i++];
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (i >= s.Length)
                        break;
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 > s.Length)
                                throw new FormatException("short \\u escape");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: sb.Append(e); break;
                    }
                }
                Expect(s, ref i, '"');
                return sb.ToString();
            }
        }
    }
}
