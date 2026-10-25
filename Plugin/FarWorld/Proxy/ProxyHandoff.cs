using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // FarWorld proxies, the handoff to a real object (ARCHITECTURE-v2.md §3.12), whoever makes it (RemoteAreas, the game's
    // CreateObjects after a crossing, other mods). Acts only for a ZDO whose proxy some window drew in view within the last
    // two frames (ProxyDraw.HasVisibleProxy); the proxy goes in the frame its Created bit is set.
    //  - LodFadeInOut.Awake: its hide (the LODGroup's reference point pushed away for 0.1-0.3 s, then a fade in) would show
    //    nothing where the proxy just was; it is skipped, the game's Random.Range draw kept (same conditions as Awake), so
    //    global RNG use is unchanged.
    //  - ZNetScene.CreateObject: MaterialVariation and RandomMaterialValues check their ZDO at once (cached delegates)
    //    instead of at their first InvokeRepeating call, so the object shows the proxy's material state in its first
    //    frame. WearNTear applies health and snow in its own Awake.
    [HarmonyPatch]
    internal static class ProxyHandoff
    {
        static readonly Action<MaterialVariation> s_mvCheck =
            AccessTools.MethodDelegate<Action<MaterialVariation>>(AccessTools.Method(typeof(MaterialVariation), "CheckMaterial"));
        static readonly Action<RandomMaterialValues> s_rmvCheck =
            AccessTools.MethodDelegate<Action<RandomMaterialValues>>(AccessTools.Method(typeof(RandomMaterialValues), "CheckMaterial"));
        static readonly AccessTools.FieldRef<RandomMaterialValues, ZNetView> s_rmvView =
            AccessTools.FieldRefAccess<RandomMaterialValues, ZNetView>("m_nview");
        static readonly AccessTools.FieldRef<RandomMaterialValues, Piece> s_rmvPiece =
            AccessTools.FieldRefAccess<RandomMaterialValues, Piece>("m_piece");
        static readonly List<MaterialVariation> s_mvs = new List<MaterialVariation>();
        static readonly List<RandomMaterialValues> s_rmvs = new List<RandomMaterialValues>();
        static float s_errorAt;

        [HarmonyPrefix, HarmonyPatch(typeof(LodFadeInOut), "Awake")]
        static bool LodFadeAwake(LodFadeInOut __instance)
        {
            if (!FarProxies.Drawing)
                return true;
            try
            {
                // ZNetScene.CreateObject sets m_initZDO before Instantiate; the object's ZNetView takes it in its own Awake,
                // which may have run already.
                ZDO zdo = ZNetView.m_initZDO;
                if (zdo == null)
                {
                    ZNetView view = __instance.GetComponentInParent<ZNetView>();
                    zdo = view != null ? view.GetZDO() : null;
                }
                if (zdo == null || !ProxyDraw.HasVisibleProxy(zdo))
                    return true;
                Camera main = Utils.GetMainCamera();
                if (main != null && Vector3.Distance(main.transform.position, __instance.transform.position) > 20f && __instance.GetComponent<LODGroup>() != null)
                    UnityEngine.Random.Range(0.1f, 0.3f);
                ProxyDraw.LabHandoffSkips++;
                return false;
            }
            catch (Exception e)
            {
                Warn("LodFadeInOut skip", e);
                return true;
            }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(ZNetScene), "CreateObject")]
        static void ObjectCreated(ZDO zdo, GameObject __result)
        {
            if (__result == null || zdo == null || !FarProxies.Drawing)
                return;
            try
            {
                if (!ProxyDraw.HasVisibleProxy(zdo))
                    return;
                // MaterialVariation's Awake ran on every active object (its CheckMaterial runs on a disabled one too).
                __result.GetComponentsInChildren(true, s_mvs);
                for (int i = 0; i < s_mvs.Count; i++)
                    if (s_mvs[i] != null && s_mvs[i].gameObject.activeInHierarchy)
                        s_mvCheck(s_mvs[i]);
                // RandomMaterialValues starts in Start (not run yet): its references as Start sets them, for an enabled one.
                __result.GetComponentsInChildren(true, s_rmvs);
                for (int i = 0; i < s_rmvs.Count; i++)
                {
                    RandomMaterialValues r = s_rmvs[i];
                    if (r == null || !r.isActiveAndEnabled)
                        continue;
                    if (s_rmvView(r) == null)
                    {
                        s_rmvView(r) = r.GetComponentInParent<ZNetView>();
                        s_rmvPiece(r) = r.GetComponentInParent<Piece>();
                    }
                    if (s_rmvView(r) != null)
                        s_rmvCheck(r);
                }
                ProxyDraw.LabFirstFrameChecks++;
            }
            catch (Exception e)
            {
                Warn("first-frame material check", e);
            }
            finally
            {
                s_mvs.Clear();
                s_rmvs.Clear();
            }
        }

        static void Warn(string what, Exception e)
        {
            if (Time.unscaledTime < s_errorAt)
                return;
            s_errorAt = Time.unscaledTime + 10f;
            Plugin.Log.LogWarning($"FarProxies handoff: {what} failed: {e}");
        }
    }
}
