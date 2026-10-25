using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace RealPortal
{
    // Reaching through a live portal: what you look at past the opening (a chest, an item, a lever, a creature) is
    // what you hover and use, and building puts the piece where you aim on the far side. The game casts its ray from
    // the camera; when that ray goes into an opening before it meets anything on this side, it is cast on from the
    // matching point of the exit, and distances count the way through the portal.
    [HarmonyPatch]
    internal static class PortalReach
    {
        // The ray from `from` along `dir` enters a live portal's opening within `max` (and nothing on this side in the
        // `mask` stops it first): where it comes out over there, which way it goes on, and how far it went here.
        internal static bool Through(Vector3 from, Vector3 dir, float max, int mask, out PortalLink link, out Vector3 farFrom, out Vector3 farDir, out float before)
        {
            link = null;
            farFrom = farDir = default;
            before = 0f;
            PortalSystem sys = PortalSystem.Instance;
            if (sys == null || !Plugin.Reach.Value)
                return false;
            Vector3 to = from + dir * max;
            float bestT = float.MaxValue;
            Vector3 bestHit = default;
            foreach (PortalLink l in sys.Active)
                if (l.Frame.Crosses(from, to, out Vector3 hit, out float t) && t < bestT)
                {
                    bestT = t;
                    bestHit = hit;
                    link = l;
                }
            if (link == null)
                return false;
            before = bestT * max;
            // Something on this side is in the way: the game's own ray is right.
            if (Physics.Raycast(from, dir, out RaycastHit near, before, mask, QueryTriggerInteraction.Ignore) && !IsOwnPortal(near.collider, link))
            {
                link = null;
                return false;
            }
            Matrix4x4 m = PortalMath.Pair(link.Frame, link.Partner.Frame);
            farDir = PortalMath.Rotation(m) * dir;
            // Just past the exit plane, so the exit's own frame and wall are behind the ray.
            farFrom = m.MultiplyPoint3x4(bestHit) + farDir * 0.02f;
            return true;
        }

        static bool IsOwnPortal(Collider c, PortalLink link) => c != null && (c == link.Wall || c.transform.IsChildOf(link.transform));

        static readonly AccessTools.FieldRef<Player, int> s_interactMask = AccessTools.FieldRefAccess<Player, int>("m_interactMask");
        static readonly AccessTools.FieldRef<Player, int> s_placeRayMask = AccessTools.FieldRefAccess<Player, int>("m_placeRayMask");
        static readonly AccessTools.FieldRef<Player, int> s_placeWaterRayMask = AccessTools.FieldRefAccess<Player, int>("m_placeWaterRayMask");
        static readonly AccessTools.FieldRef<Player, GameObject> s_placementGhost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
        static readonly RaycastHit[] s_hits = new RaycastHit[64];
        static readonly System.Collections.Generic.IComparer<RaycastHit> s_byDistance =
            System.Collections.Generic.Comparer<RaycastHit>.Create((x, y) => x.distance.CompareTo(y.distance));

        // Lab: hovers and placements found through a portal.
        internal static int LabHovers, LabPlacements;
        internal static string LabLast = "";

        static readonly System.Reflection.MethodInfo s_raycastNonAlloc = AccessTools.Method(typeof(Physics), nameof(Physics.RaycastNonAlloc),
            new[] { typeof(Vector3), typeof(Vector3), typeof(RaycastHit[]), typeof(float), typeof(int) });
        static readonly System.Reflection.MethodInfo s_distance = AccessTools.Method(typeof(Vector3), nameof(Vector3.Distance));
        static readonly System.Reflection.MethodInfo s_euler = AccessTools.Method(typeof(Quaternion), nameof(Quaternion.Euler), new[] { typeof(float), typeof(float), typeof(float) });

        // Swaps one called method for another (same arguments) everywhere in a method's code.
        static System.Collections.Generic.IEnumerable<CodeInstruction> Swap(System.Collections.Generic.IEnumerable<CodeInstruction> code, System.Reflection.MethodInfo from, System.Reflection.MethodInfo to, string where)
        {
            int n = 0;
            foreach (CodeInstruction ci in code)
            {
                if (ci.Calls(from))
                {
                    ci.opcode = System.Reflection.Emit.OpCodes.Call;
                    ci.operand = to;
                    n++;
                }
                yield return ci;
            }
            if (n == 0)
                Plugin.Log.LogWarning($"{where}: {from.Name} not found; reaching through portals is off there.");
        }

        // What you hover: the game's own ray, going on through openings (PortalPhysics); the game's checks of how
        // far it is then count the way through the portal.
        [HarmonyTranspiler, HarmonyPatch(typeof(Player), "FindHoverObject")]
        static System.Collections.Generic.IEnumerable<CodeInstruction> FindHoverObject(System.Collections.Generic.IEnumerable<CodeInstruction> code) =>
            Swap(code, s_raycastNonAlloc, AccessTools.Method(typeof(PortalPhysics), nameof(PortalPhysics.RaycastNonAlloc)), "Player.FindHoverObject");

        // A chest open on the far side stays open while you stand at the portal (the game closes it beyond its
        // reach, measured straight).
        [HarmonyTranspiler, HarmonyPatch(typeof(InventoryGui), "UpdateContainer")]
        static System.Collections.Generic.IEnumerable<CodeInstruction> UpdateContainer(System.Collections.Generic.IEnumerable<CodeInstruction> code) =>
            Swap(code, s_distance, AccessTools.Method(typeof(PortalPhysics), nameof(PortalPhysics.Distance)), "InventoryGui.UpdateContainer");

        // A workbench over there is in reach when it is by the way through the portal.
        [HarmonyPostfix, HarmonyPatch(typeof(CraftingStation), "InUseDistance")]
        static void InUseDistance(CraftingStation __instance, Humanoid human, ref bool __result)
        {
            if (!__result && human != null && Plugin.Reach.Value)
                __result = PortalPhysics.Distance(human.transform.position, __instance.transform.position) < __instance.m_useDistance;
        }

        // The piece being placed over there turns as the pair turns the view (it is rotated in the far world's frame).
        static Quaternion s_placeTurn = Quaternion.identity;
        static int s_placeTurnFrame = -1;

        [HarmonyTranspiler, HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        static System.Collections.Generic.IEnumerable<CodeInstruction> UpdatePlacementGhost(System.Collections.Generic.IEnumerable<CodeInstruction> code)
        {
            bool done = false;
            foreach (CodeInstruction ci in code)
            {
                yield return ci;
                if (!done && ci.Calls(s_euler))
                {
                    done = true;
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(PortalReach), nameof(PlaceRotation)));
                }
            }
            if (!done)
                Plugin.Log.LogWarning("Player.UpdatePlacementGhost: rotation not found; pieces placed through portals keep this side's rotation.");
        }

        static Quaternion PlaceRotation(Quaternion r) => s_placeTurnFrame == Time.frameCount ? s_placeTurn * r : r;

        // ---- the vanilla rules across a portal -----------------------------------------------------------

        // A thing over there that you reach through a portal (far by the straight way, near by the way through).
        // The portal it goes through: the nearest live one by you.
        internal static bool Across(Vector3 you, Vector3 thing, out PortalLink via)
        {
            via = null;
            PortalSystem sys = PortalSystem.Instance;
            if (sys == null || Vector3.Distance(you, thing) < 20f)
                return false;
            float best = 12f;
            foreach (PortalLink l in sys.Active)
            {
                float d = Vector3.Distance(you, l.Frame.Center);
                if (d < best && Vector3.Distance(l.Partner.Frame.Center, thing) < 15f)
                {
                    best = d;
                    via = l;
                }
            }
            return via != null;
        }

        // An item that may not be carried through that portal, as the vanilla teleport rules have it.
        internal static bool Forbidden(ItemDrop.ItemData item, PortalLink via) =>
            item != null && Plugin.RespectItemRestrictions.Value && !item.m_shared.m_teleportable
            && !(via.Portal != null && via.Portal.m_allowAllItems) && !(via.Partner?.Portal != null && via.Partner.Portal.m_allowAllItems)
            && !(ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.TeleportAll));

        static float s_saidAt = -10f;

        static void SayNo(Player p)
        {
            if (Time.time - s_saidAt < 1f)
                return;
            s_saidAt = Time.time;
            p.Message(MessageHud.MessageType.Center, "$msg_noteleport");
        }

        static readonly AccessTools.FieldRef<InventoryGui, Container> s_currentContainer = AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");

        // Moving between your bag and a chest that is open across a portal.
        static bool AcrossChest(Inventory a, Inventory b, out PortalLink via)
        {
            via = null;
            Player p = Player.m_localPlayer;
            InventoryGui gui = InventoryGui.instance;
            Container c = gui != null ? s_currentContainer(gui) : null;
            if (p == null || c == null)
                return false;
            Inventory mine = p.GetInventory(), theirs = c.GetInventory();
            if (!((a == mine && b == theirs) || (a == theirs && b == mine)))
                return false;
            return Across(p.transform.position, c.transform.position, out via);
        }

        [HarmonyPrefix, HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveItemToThis), new[] { typeof(Inventory), typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int) })]
        static bool MoveItemTo(Inventory __instance, Inventory fromInventory, ItemDrop.ItemData item, ref bool __result)
        {
            if (!AcrossChest(__instance, fromInventory, out PortalLink via) || !Forbidden(item, via))
                return true;
            SayNo(Player.m_localPlayer);
            __result = false;
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveItemToThis), new[] { typeof(Inventory), typeof(ItemDrop.ItemData) })]
        static bool MoveItemToSimple(Inventory __instance, Inventory fromInventory, ItemDrop.ItemData item)
        {
            if (!AcrossChest(__instance, fromInventory, out PortalLink via) || !Forbidden(item, via))
                return true;
            SayNo(Player.m_localPlayer);
            return false;
        }

        // "Take all" across a portal leaves what may not go through.
        [HarmonyPrefix, HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveAll))]
        static bool MoveAll(Inventory __instance, Inventory fromInventory)
        {
            if (!AcrossChest(__instance, fromInventory, out PortalLink via))
                return true;
            bool any = false;
            foreach (ItemDrop.ItemData item in fromInventory.GetAllItems().ToArray())
            {
                if (Forbidden(item, via))
                {
                    any = true;
                    continue;
                }
                __instance.MoveItemToThis(fromInventory, item);
            }
            if (any)
                SayNo(Player.m_localPlayer);
            return false;
        }

        // Picking up across a portal what may not go through it.
        [HarmonyPrefix, HarmonyPatch(typeof(Humanoid), nameof(Humanoid.Pickup))]
        static bool Pickup(Humanoid __instance, GameObject go, ref bool __result)
        {
            if (__instance != Player.m_localPlayer || go == null || !go.TryGetComponent(out ItemDrop drop)
                || !Across(__instance.transform.position, go.transform.position, out PortalLink via) || !Forbidden(drop.m_itemData, via))
                return true;
            SayNo(Player.m_localPlayer);
            __result = false;
            return false;
        }

        // Building over there with what may not go through.
        internal static bool BuildForbidden(Player p, PortalLink via)
        {
            GameObject ghost = s_placementGhost(p);
            Piece piece = ghost != null ? ghost.GetComponent<Piece>() : null;
            if (piece == null)
                return false;
            foreach (Piece.Requirement r in piece.m_resources)
                if (r.m_resItem != null && Forbidden(r.m_resItem.m_itemData, via))
                    return true;
            return false;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Player), "PieceRayTest")]
        static void PieceRayTest(Player __instance, ref bool __result, ref Vector3 point, ref Vector3 normal, ref Piece piece, ref Heightmap heightmap,
                                 ref Collider waterSurface, bool water)
        {
            GameCamera cam = GameCamera.instance;
            if (cam == null || __instance != Player.m_localPlayer)
                return;
            int mask = water ? s_placeWaterRayMask(__instance) : s_placeRayMask(__instance);
            Vector3 from = cam.transform.position, dir = cam.transform.forward;
            if (!Through(from, dir, 50f, mask, out PortalLink link, out Vector3 farFrom, out Vector3 farDir, out float before))
                return;
            // Nothing over there built of what may not be carried through.
            if (BuildForbidden(__instance, link))
            {
                __result = false;
                point = normal = Vector3.zero;
                piece = null;
                heightmap = null;
                waterSurface = null;
                return;
            }
            float reach = __instance.m_maxPlaceDistance;
            GameObject ghost = s_placementGhost(__instance);
            if (ghost != null && ghost.TryGetComponent(out Piece ghostPiece))
                reach += ghostPiece.m_extraPlacementDistance;
            float toOpening = Vector3.Distance(__instance.m_eye.position, from + dir * before);
            __result = false;
            point = normal = Vector3.zero;
            piece = null;
            heightmap = null;
            waterSurface = null;
            if (!Physics.Raycast(farFrom, farDir, out RaycastHit h, 50f - before, mask) || !h.collider || h.collider.attachedRigidbody
                || toOpening + h.distance >= reach || IsOwnPortal(h.collider, link.Partner))
                return;
            point = h.point;
            normal = h.normal;
            piece = h.collider.GetComponentInParent<Piece>();
            heightmap = h.collider.GetComponent<Heightmap>();
            waterSurface = h.collider.gameObject.layer == LayerMask.NameToLayer("Water") ? h.collider : null;
            __result = true;
            // The pair's turn about the vertical, for the piece's rotation (UpdatePlacementGhost).
            Quaternion turn = PortalMath.Rotation(PortalMath.Pair(link.Frame, link.Partner.Frame));
            s_placeTurn = Quaternion.Euler(0f, turn.eulerAngles.y, 0f);
            s_placeTurnFrame = Time.frameCount;
            LabPlacements++;
        }
    }
}
