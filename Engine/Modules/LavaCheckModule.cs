using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // Character.UpdateLava runs for every owned character every physics step and starts with
    // ZoneSystem.GetGroundData: a 10 km raycast down from 5000 m above, GetComponent<Heightmap> on the hit and the
    // heightmap's biome blend. Only then does it check WorldGenerator.IsAshlands and return outside the Ashlands. Far
    // areas multiply the owned characters, and most of them stand nowhere near lava or deep snow.
    //
    // What the call writes, and who reads it (the whole game assembly; the fields are private, and no installed mod
    // names them):
    //  - m_lastGroundHeight, m_lastHeightmap: lava proximity and height in the Ashlands branch of this same call, and
    //    deep snow (UpdateWalking, the landing check in UpdateGroundContact) only under m_lastBiome == DeepNorth;
    //  - m_lastBiome: the Ashlands branch (day heat), and the same two DeepNorth checks;
    //  - the normal and biome area: discarded.
    // So where IsAshlands(x, z) is false (the game's own early return, same function and arguments) and the biome the
    // call would report cannot be DeepNorth, nothing the game does depends on the result. That biome is
    // Heightmap.GetBiome of the terrain heightmap under (x, z): one of the four corner biomes the HeightmapBuilder
    // took from the biome map (WorldGenerator.GetBiomeSector, 12 m cells), or None when all four are None (TerrainLod
    // and RealPortal's FarTerrain heightmaps have no collider, so the ray never hits those). The skip therefore
    // needs, besides !IsAshlands, no DeepNorth cell in the biome map within 128 m of the character's zone (the
    // corners of the zone's heightmap, or of a neighbour's when the point is on their shared edge, lie within 96 m of
    // the zone's centre along each axis), checked once per zone and cached, and also !IsDeepnorth(x, z) as a guard
    // for a distant-LOD heightmap that would have a collider (its GetBiome asks WorldGenerator directly). While the
    // biome map is not ready (the game then hands out placeholder sectors) nothing is skipped. The readers in
    // UpdateWalking and UpdateGroundContact use what the previous step wrote, at that step's position, which is the
    // position the skip was decided for.
    //
    // A skipped call leaves m_lastGroundHeight at the character's position (as a ray that hits nothing does),
    // m_lastHeightmap null, and m_lastBiome the biome-map biome under the character (never DeepNorth there; near a
    // border it can differ from the heightmap's corner blend, which nothing reads outside the branches above), so the
    // fields stay meaningful for anyone reading them.
    //
    // The call site stays: the skip is a GetGroundData prefix (Priority.First) that answers only UpdateLava's own
    // call, recognised by its first argument being that very character's m_lastGroundHeight (UpdateLava's prefix
    // names the character). Every other caller, SnowRoller and Vagon with their own fields included, gets the
    // original. Other mods' prefixes and postfixes on GetGroundData still run for a skipped call (HarmonyX runs every
    // prefix) and see the answer above, much as after a ray that hit no heightmap; one that writes its own results
    // runs after this one and wins as it would in the game. Only a prefix that moves p before the raycast is not
    // followed: the skip is decided on the character's own position.
    [EngineModule]
    internal static class LavaCheckModule
    {
        static readonly Feature s_feature = Features.Register("LavaCheck", true,
            "Character.UpdateLava skips its 10 km ground raycast where neither Ashlands heat nor Deep North snow can apply.");

        static readonly AccessTools.FieldRef<Character, Vector3> s_lastGroundHeight = AccessTools.FieldRefAccess<Character, Vector3>("m_lastGroundHeight");

        // The character whose UpdateLava is about to ask; taken by the next GetGroundData, whoever calls it.
        static Character s_asking;

        // Zones whose surroundings hold no DeepNorth in the biome map (true), and those that do (false).
        static readonly Dictionary<Vector2s, bool> s_zones = new Dictionary<Vector2s, bool>();
        // The map those answers came from (its sector at the world origin; a new map has new sectors).
        static BiomeSector s_map;

        const float Reach = 128f;

        // Lab: UpdateLava ground queries answered without the raycast, and those that cast it.
        internal static long Skipped, Cast;

        static void Install(Harmony harmony)
        {
            if (s_feature == null)
                return;
            MethodInfo updateLava = AccessTools.DeclaredMethod(typeof(Character), "UpdateLava");
            MethodInfo ground = AccessTools.DeclaredMethod(typeof(ZoneSystem), nameof(ZoneSystem.GetGroundData));
            if (updateLava == null || ground == null)
            {
                EnginePlugin.Log.LogWarning("LavaCheck: Character.UpdateLava or ZoneSystem.GetGroundData not found, left as it is.");
                return;
            }
            // Either patch alone does nothing: without the UpdateLava one nobody is asking, without the GetGroundData
            // one nobody answers.
            harmony.Patch(ground, prefix: new HarmonyMethod(typeof(LavaCheckModule), nameof(BeforeGroundData)) { priority = Priority.First });
            // Last, so another mod's UpdateLava prefix that asks for ground data of its own has asked already.
            harmony.Patch(updateLava, prefix: new HarmonyMethod(typeof(LavaCheckModule), nameof(BeforeUpdateLava)) { priority = Priority.Last });
            s_feature.Switched += on =>
            {
                s_zones.Clear();
                s_asking = null;
            };
        }

        static void BeforeUpdateLava(Character __instance)
        {
            s_asking = s_feature.On ? __instance : null;
        }

        static bool BeforeGroundData(ref Vector3 p, ref Vector3 normal, ref Heightmap.Biome biome, ref Heightmap.BiomeArea biomeArea, ref Heightmap hmap)
        {
            Character c = s_asking;
            if ((object)c == null)
                return true;
            // One answer per UpdateLava. A call that is not UpdateLava's own (another mod's patch in between, or an
            // UpdateLava another prefix skipped) just gets the original.
            s_asking = null;
            if (!s_feature.On || !SameField(ref p, ref s_lastGroundHeight(c)))
                return true;
            if (!Unneeded(p, out Heightmap.Biome here))
            {
                Cast++;
                return true;
            }
            normal = Vector3.up;
            biomeArea = Heightmap.BiomeArea.Everything;
            hmap = null;
            biome = here;
            Skipped++;
            return false;
        }

        // Whether two references are one variable (the C# 9 stand-in for Unsafe.AreSame, which is not in netstandard2.1).
        static unsafe bool SameField(ref Vector3 a, ref Vector3 b)
        {
            fixed (Vector3* pa = &a)
            fixed (Vector3* pb = &b)
                return pa == pb;
        }

        static bool Unneeded(Vector3 p, out Heightmap.Biome here)
        {
            here = Heightmap.Biome.None;
            WorldGenerator world = WorldGenerator.instance;
            if (world == null || WorldGenerator.IsAshlands(p.x, p.z) || WorldGenerator.IsDeepnorth(p.x, p.z))
                return false;
            BiomeSector origin = world.GetBiomeSector(0f, 0f);
            if (Placeholder(origin))
                return false;
            if (!ReferenceEquals(origin, s_map))
            {
                s_zones.Clear();
                s_map = origin;
            }
            Vector2s zone = ZoneSystem.GetZone(p);
            if (!s_zones.TryGetValue(zone, out bool clear))
                s_zones.Add(zone, clear = NoDeepNorthAround(world, zone));
            if (!clear)
                return false;
            here = world.GetBiomeSector(p.x, p.z).Biome;
            return true;
        }

        // Every biome-map cell a heightmap corner near this zone can fall into: the cells under the zone and Reach
        // around it, looked up through the game's own GetBiomeSector (as HeightmapBuilder does, edges clamped the same way).
        static bool NoDeepNorthAround(WorldGenerator world, Vector2s zone)
        {
            Vector3 centre = ZoneSystem.GetZonePos(zone);
            int x0 = AltBiomeWorldData.WorldSpaceToMapSpace(centre.x - Reach);
            int x1 = AltBiomeWorldData.WorldSpaceToMapSpace(centre.x + Reach);
            int y0 = AltBiomeWorldData.WorldSpaceToMapSpace(centre.z - Reach);
            int y1 = AltBiomeWorldData.WorldSpaceToMapSpace(centre.z + Reach);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    BiomeSector s = world.GetBiomeSector(AltBiomeWorldData.MapSpaceToWorldSpace(x), AltBiomeWorldData.MapSpaceToWorldSpace(y));
                    if (Placeholder(s) || s.Biome == Heightmap.Biome.DeepNorth)
                        return false;
                }
            return true;
        }

        // What GetBiomeSector hands out without a ready biome map (or for a hole in it).
        static bool Placeholder(BiomeSector s) =>
            s == null || ReferenceEquals(s, BiomeSector.Empty) || ReferenceEquals(s, BiomeSector.EmptyMeadows)
            || ReferenceEquals(s, BiomeSector.EmptyBlackForest) || ReferenceEquals(s, BiomeSector.EmptyEdge);
    }
}
