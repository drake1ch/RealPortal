using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace RealPortal.Engine
{
    // Rank 10 of Docs\general\critic.md (SaveSnapshot step 1 + ProfileSaveAsync). Measured on the lab world (326k ZDOs,
    // savetest mode=auto): the autosave frame is 260-310 ms; Game.SavePlayerProfile takes 192-203 ms of it, 190 ms of
    // which is Minimap.SaveMapData, and ZDOMan.PrepareSave 51-84 ms the next frame. Everything here is off by default.
    //
    // ---- SaveSnapshot (world) ----------------------------------------------------------------------------------------
    // What the game does (ZNet.SaveWorld, main thread): ZDOMan.PrepareSave clones the ZDOs of the dirty chunks only
    // (GetSaveClonePerChunk: MemberwiseClone of each ZDO, all value fields), then ZDOExtraData.PrepareSave runs
    // RegenerateConnectionHashData and clones the extra data of EVERY ZDO in the world (7 type maps + connection hashes:
    // ToDictionary with a MemberwiseClone of each BinarySearchDictionary). The save thread then writes each clone with
    // ZDO.Save, which asks ZDOExtraData.GetSaveData for the clone's data (session-only keys filtered out against the LIVE
    // s_sessionOnly set).
    // Vanilla race: BinarySearchDictionary.Clone is a MemberwiseClone, so the "clone" shares m_keys/m_values with the
    // live map. SetValue/Remove shift those arrays in place, and released maps go back to Pool<> and are reused by new
    // ZDOs, so a ZDO changed or destroyed during the 0.5-10 s the save thread runs can be written torn or with another
    // ZDO's keys. s_sessionOnly is read by the save thread while the main thread may add to it.
    //
    // What this does instead (same instant, same output):
    //  - ZDOMan.PrepareSave runs as in the game (dirty chunks, clones, dirty-set switch).
    //  - Its ZDOExtraData.PrepareSave is replaced: RegenerateConnectionHashData runs exactly as in the game (it is part
    //    of what gets saved: fresh unique hashes per save), then the extra data of the cloned ZDOs ONLY (D, not the whole
    //    world N) is copied into pooled flat arrays: per type the keys and values in the map's own (sorted) order, each
    //    byte array cloned once (no reference to a live array survives; the save thread hands these clones to ZDO.Save,
    //    which only reads them), strings by reference (immutable), connection hashes by reference
    //    (ZDOConnectionHashData is immutable), and a copy of s_sessionOnly. Nothing the save thread reads is shared with
    //    live state any more, which fixes the race.
    //  - ZDOExtraData.GetSaveData (asked by the game's own ZDO.Save on the save thread) is answered from that snapshot:
    //    the same lists in the same order, session-only keys dropped with the frozen set, the same connection object.
    //    Only while ZDOMan.m_saveData is still the save data the snapshot was taken for: a snapshot can never answer a
    //    later save. ZDO.Save, SaveChunks, the chunk mapping, file names, backups, cloud handling: all the game's code.
    //  So each chunk file is byte-identical to what the game writes when nothing changes between the save point and
    //  the moment its save thread reaches that ZDO (the only case where the game's output is defined). The one place
    //  the game is not deterministic is gone: data changed during the save window no longer leaks into the files.
    //  Documented difference: a key added to s_sessionOnly AFTER the save point (ZSyncAnimation.AddSessionHash) is
    //  filtered by the game only if its save thread has not passed that ZDO yet; here it is never filtered in this save
    //  (the set is frozen at the save point like the data). Both are "the value at the save point".
    //  How the copy is made: per type one lookup per captured ZDO. The 8 type/connection copies run in parallel on pool
    //  threads while the main thread waits and takes part: at that moment nothing writes ZDO data (the main thread is
    //  inside PrepareSave and the game writes ZDO data nowhere else), so concurrent reads of the maps are safe. The split
    //  is by type, so the largest map's task (usually ints or floats) bounds the parallel time (and a preempted pool
    //  thread holding it makes the main thread wait). On a machine with one CPU (no helper thread) and many ZDOs dirty
    //  (D >= OneCpuDirtyShare x the largest map), the one-thread copy costs as much as the game's full clone or more, so
    //  that save is left to the game (counted as a fallback, reason given). Harness, realistic data, chunks scattered
    //  against the dictionary order: one-thread copy 24/112/358 ms at 5/25/100 % of 326k ZDOs dirty, 8 threads 4/32/106
    //  ms, the game's full clone 126-164 ms; one thread breaks even near 37 % of the ZDOs, about half the largest map.
    //  Those are a correctness rig's numbers, not a prediction: there the game's full clone of 326k ZDOs takes 126-180
    //  ms, while player.log on the real 326k-ZDO lab world says "ZDOExtraData.PrepareSave done [34-67ms]". In game the
    //  cost is the game's own log line "PrepareSave: ZDOExtraData.PrepareSave done [X ms]" (it times this replacement
    //  when on, the game's clone when off) and LastCaptureMs, compared in one session.
    //  Pooled across saves (until the feature is switched off): about (4 + value size) bytes per saved entry and ~40
    //  bytes per dirty ZDO. Per-save garbage: the byte-array clones (the byte-array volume of the dirty ZDOs).
    //  Not done: spreading the copy over frames. Copying before the save point would need proof that nothing changed
    //  since (the data revision is not bumped by direct ZDOExtraData writes, which are public API); copying after it
    //  needs copy-on-write hooks on every ZDOExtraData mutator, and those one-line methods are inlined by Mono into
    //  ZDO.Set & co., so Harmony cannot intercept them reliably (that needs NoInlining from the preloader: netsave.md
    //  "Step 2"). The copy is therefore one frame, but only over the dirty ZDOs and parallel.
    // Stands down (the game's own path, counted in WorldFallbacks with the reason) when: another mod patches ZNet.SaveWorld,
    // ZNet.SaveWorldThread, ZDOMan.PrepareSave/SaveChunks/SaveCleanup, ZDOExtraData.PrepareSave/GetSaveData/ClearSave or
    // ZDO.Save, with Harmony or with MonoMod (On./IL. hooks through MMHOOK, or Hook/ILHook/Detour made directly; checked
    // at every save); the world is not a local save (cloud / Steam cloud / legacy source, or local storage not allowed);
    // a save thread is still alive; the game version is not one this was verified on (VerifiedGameVersions); or an
    // earlier world save of the module failed this session. Not visible: a MonoMod NativeDetour or any other raw patch
    // of these methods' code (no registry holds them), and hooks added between the check and the save thread's end.
    // Failures: if the capture throws (also on a null map, on which the game's Clone would throw), the game's clones are
    // made instead (without running RegenerateConnectionHashData a second time) and the feature stays off for the
    // session. If the save thread asks for a ZDO the snapshot does not hold (cannot happen with the game's code),
    // GetSaveData throws. If a save served from the snapshot fails in SaveChunks for any reason (that throw, an I/O
    // error, SaveChunks returning false), the game's save thread reports it, deletes the new chunk files and keeps the
    // old save and its dirty chunks (its normal failure path), and the rest of the session is the game's. Anyone else
    // asking on the main thread during the save window for a ZDO outside the snapshot gets the live data, which is
    // what the game's shared-array clones would give. A snapshot left behind by a save that never reached SaveCleanup
    // (its thread could not mount the save depot, or ZNet.SaveWorld threw after PrepareSave) is dropped at the next
    // ZDOMan.PrepareSave or when the feature is switched off.
    // The save thread's lookups follow the snapshot order (a cursor, no hashing); only an out-of-order request builds an
    // index, whose hash reads ZDOID.UserID (a static list the main thread may append to: the game's own save thread
    // reads it the same way through its dictionary lookups).
    //
    // ---- ProfileSaveAsync -------------------------------------------------------------------------------------------
    // Game.SavePlayerProfile runs on the main thread: Player.Save into the profile, Minimap.SaveMapData (2 x size^2
    // BinaryWriter.Write(bool) calls, then GZip of ~8 MB: the 190 ms), the logout point, then PlayerProfile.Save: the
    // whole profile package, SHA512, .fch.new written and flushed to disk, ReplaceOldFile (.fch -> .old, .new -> .fch),
    // auto-backup. Every client does it at once when the server saves (RPC SavePlayerProfile).
    // With the feature on, the same method runs (a prefix replica of its body, with the game's own calls) except:
    //  - Minimap.SaveMapData: the explored/exploredOthers bits are copied (BitArray.CopyTo, 2 x 512 KB at 2048^2), with
    //    the saved pins (name, pos, type, checked, owner, author string) and IsReferencePositionPublic: the minimap state
    //    at the save point. The profile's world entry is created then as SetMapData would (GetWorldData), so the world
    //    dictionary has the same order.
    //  - PlayerProfile.Save: its main-thread part runs here (SavingStarted, PreSaveCloudChecksAndOperations, paths,
    //    directory) and the profile package is serialized here, at the save point, with the game's ZPackage writes in the
    //    game's order, split in two at the current world's map bytes; m_lastSaveLoad and the knownWorlds/knownWorldKeys
    //    increments happen here too, after the stats were written, exactly as in the game.
    //  - A writer thread then makes the map bytes exactly as Minimap.GetMapData: the inner package is built in one
    //    pooled array of its exact size (texture size, the bits as 0/1 bytes = N x BinaryWriter.Write(bool), the pins
    //    through a ZPackage, the public flag) and compressed with one GZipStream(Fastest).Write of it, which is what
    //    Utils.Compress does with the inner package's array; then [8][length][bytes] as ZPackage.Write(8) +
    //    WriteCompressed. Garbage per save is the ~9 KB result, not the ~38 MB of the game's growing MemoryStreams (a
    //    Boehm collection stops every thread, whichever one allocated). It assembles head + map + tail, hashes it
    //    (SHA512, as ZPackage.GenerateHash), writes .fch.new with the game's FileWriter (local file, flushed to disk) and
    //    calls the game's FileHelpers.ReplaceOldFile (the same swap).
    //  - Back on the main thread (Game.Update once the writer is done AND no world save thread is running, or any
    //    flush): the map bytes are set into the profile's world entry (SetMapData), SaveSystem caches invalidated,
    //    ZNet.ConsiderAutoBackup, SavingFinished. The game finishes a profile save before its autosave's world save
    //    starts; waiting for the world save thread keeps it that way (both would mount/unmount the save depot through
    //    FileHelpers' unsynchronized counter on Steam, and both reload SaveSystem caches).
    // Flushes (wait for the writer and finish on the main thread) before: the next SavePlayerProfile, any
    // PlayerProfile.SavePlayerToDisk or LoadPlayerFromDisk (any profile), PlayerProfile.GetMapData, Game.Shutdown (quit,
    // logout, disconnect) and Game.OnDestroy. During shutdown Game.SavePlayerProfile is the game's, synchronous. So two
    // writes of a profile never overlap, a later save never races an earlier one, and the profile file is complete and
    // renamed before the game leaves the world. A flush never throws: an exception while finishing (ConsiderAutoBackup,
    // a SavingFinished handler) is logged, counted in ProfileErrors and turns the feature off for the session; it never
    // reaches Game.Shutdown/OnDestroy or the flushing profile method. A crash while the writer runs leaves what a crash
    // in the game's own save leaves: the old .fch and possibly a partial .new.
    // Stands down (the game's path) when: another mod patches Game.SavePlayerProfile, PlayerProfile.Save/SavePlayerToDisk/
    // SetMapData, Minimap.SaveMapData/GetMapData, ZPackage.WriteCompressed/GenerateHash, Utils.Compress,
    // FileHelpers.ReplaceOldFile or FileWriter.Finish (Harmony or MonoMod, as above); PlayerProfile.SavingStarted or
    // SavingFinished has a handler other than the game's Hud and Menu (here SavingStarted comes before the new map bytes
    // are in the profile, and a SetMapData made during the writer window would be replaced by the writer's bytes: the
    // game itself never calls it there, its only caller is SaveMapData inside SavePlayerProfile, which flushes first);
    // the profile is not local (cloud / Steam cloud), local storage is not allowed, there is no local player, minimap
    // or world UID, the game is shutting down, the version is unverified, or a write failed earlier this session.
    // If the writer fails, cannot be started, or SaveVerify finds a difference, the error is logged and the game's
    // synchronous save runs at once on the main thread (the profile is not left unsaved). If ReplaceOldFile fails after
    // moving .fch to .old, the complete .new is kept (as the game would leave it); only a .new whose own write failed is
    // deleted.
    // Differences (all timing, none in the file): the profile's in-memory map bytes for this world, the "saving" HUD icon
    // and ConsiderAutoBackup follow the writer (~0.1-0.3 s, or the end of the world save) instead of the same frame;
    // vanilla's mount of the save depot (Steam) ends before the local file is written (it is not used for local files);
    // an exception while writing no longer cancels the world save of the same autosave (the game's would throw out of
    // UpdateSaving). A save redone the game's way after a failure first undoes what the module's serialization changed
    // in the profile (the knownWorlds/knownWorldKeys increments, m_lastSaveLoad), so its file is the game's.
    //
    // ---- SaveVerify (lab) -------------------------------------------------------------------------------------------
    // On a verified game version, with SaveSnapshot / ProfileSaveAsync:
    //  World: at the save point, after the snapshot, the game's own clones are made too and the game's ZDOMan.SaveChunks is
    //  run on the main thread (the world is frozen there) into <save data>\rp_saveverify\world\; when the real save thread
    //  has written its chunks (from the snapshot), every chunk file and the chunk mapping are byte-compared. A difference
    //  fails that save (SaveChunks returns false: the game deletes the new chunk files and keeps the previous save and its
    //  dirty chunks) and the rest of the session is the game's.
    //  Profile: the game's Minimap.GetMapData runs, and the game's PlayerProfile.SavePlayerToDisk writes a field-by-field
    //  copy of the profile (same dictionary order) into <save data>\rp_saveverify\profile\; the writer thread compares the
    //  map bytes and the whole file BEFORE writing: on a difference the module's file is not written, the game's save runs
    //  at once with the game's map bytes, and the rest of the session is the game's.
    // On an unverified game version (shadow): the game saves as it always does, and the module's files are only made and
    // compared: the world snapshot is taken right after the game's own ZDOExtraData.PrepareSave (its clones, its hashes)
    // and written with SaveChunks on the main thread into rp_saveverify\world\, then compared with what the game's save
    // thread wrote (that thread reads the game's shared clones, so a busy world can differ by the vanilla race; judge in a
    // quiet scene); the profile replica (minimap snapshot taken as SavePlayerProfile starts, package of a copy of the
    // profile right before the game's SavePlayerToDisk) is compared with the file the game just wrote.
    // Mismatches are logged with the first differing offset, the files kept, and counted (VerifyWorldMismatches,
    // VerifyProfileMismatches, VerifyMapMismatches); on a match the reference files are deleted. The reference work is
    // not in the timings (VerifyWorldRefMs, VerifyProfileRefMs). Expected noise: "Failed to retrieve save with name
    // ../rp_saveverify/..." (ConsiderAutoBackup of the copy).
    //
    // Counters: public statics below and Describe(); ResetCounters() clears the cumulative and Max ones (Max ones also on
    // any switch of these features). LastPrepareMs is the whole ZDOMan.PrepareSave on the main thread, LastCaptureMs the
    // snapshot part of it, LastProfileMainMs the whole Game.SavePlayerProfile on the main thread, LastProfileFinishMs the
    // main-thread end of the save a frame or more later; the profile's main-thread cost is their sum.
    //
    // Checked outside the game (scratch harness: Unity 6000.0.75's Mono, the game's HarmonyX and assembly_valheim 1.0.20,
    // Unity natives stubbed): ZDO.Save over the snapshot == ZDO.Save over the game's clones for every dirty ZDO (all
    // seven types, session keys, >127-entry maps, byte arrays, connections, duplicated and out-of-order ZDOs, one thread
    // and parallel); after live changes past the save point (byte arrays changed in place included) the snapshot still
    // writes the save-point bytes while the game's own clones write most ZDOs differently; a snapshot that missed its
    // SaveCleanup never answers a later save; the real ZDOMan.PrepareSave arms it and makes no full clone; SaveVerify
    // passes with the main thread changing ZDOs during the save thread's SaveChunks, and a difference fails the save; the
    // shadow mode on an "unverified" version; MonoMod hooks and foreign SavingStarted handlers make it stand down; the
    // map bytes == Minimap.GetMapData (64, 37 and 2048 texels); two consecutive Game.SavePlayerProfile files == the
    // game's (stats increments included); stand-down, flush, deferred finish, writer-start, replace and write failures,
    // and the file of a save redone the game's way after a failure == the game's own save of a twin profile.
    [EngineModule]
    public static class SaveSnapshotModule
    {
        // On by default since the lab's savetest (24.10): byte-identical files (auto, burst, rpc, verify=yes), stand-down on a
        // foreign patch, the autosave frame 262-267 ms -> 27-32 ms on 308k ZDOs.
        static readonly Feature s_world = Features.Register("SaveSnapshot", true,
            "World save: only the dirty ZDOs' extra data is copied at the save point (into pooled flat arrays, deep), and the save thread writes from that copy: same files, no full-world clone, no data race.");
        static readonly Feature s_profile = Features.Register("ProfileSaveAsync", true,
            "Player profile save: the profile and minimap are snapshotted on the main thread; the map bytes, hash and file write (.new + replace, as the game) run on a writer thread.");
        static readonly Feature s_verify = Features.Register("SaveVerify", false,
            "Lab: with SaveSnapshot / ProfileSaveAsync, also runs the game's save path into <save data>/rp_saveverify and byte-compares (a difference: that save is the game's, and the rest of the session). On an unverified game version the game saves and the module's files are only made and compared.");

        // ---- lab counters ---------------------------------------------------------------------------------------------
        // World.
        public static long WorldSnapshots, WorldFallbacks, WorldErrors, ServeCalls, ServeMisses, WorldShadows;
        public static int LastZdos, LastChunks, LastThreads;
        public static long LastBytes;
        public static double LastCaptureMs, LastPrepareMs, MaxPrepareMs, VerifyWorldRefMs;
        public static string LastWorldFallback = "";
        // Profile.
        public static long ProfileAsyncSaves, ProfileFallbacks, ProfileErrors, ProfileFlushWaits, ProfileShadows;
        public static double LastProfileMainMs, MaxProfileMainMs, LastProfileWorkerMs, MaxFlushWaitMs, LastProfileFinishMs, MaxProfileFinishMs, VerifyProfileRefMs;
        public static long LastProfileBytes;
        public static string LastProfileFallback = "";
        // Verify.
        public static long VerifyWorldRuns, VerifyWorldFiles, VerifyWorldMismatches, VerifyWorldBytes;
        public static long VerifyProfileRuns, VerifyProfileMismatches, VerifyMapMismatches;
        public static string LastVerify = "";
        // Lab knobs: parallel copy on/off, the smallest copy (map entries or ZDO lookups) worth the pool threads, and the
        // dirty share above which a one-CPU machine leaves the save to the game's full clone.
        public static bool Parallel = true;
        public static int ParallelMinWork = 20000;
        public static double OneCpuDirtyShare = 0.5;

        // A snapshot serving a running save thread / a profile save not finished yet (the lab waits for both).
        public static bool WorldSnapshotActive => s_active != null && SaveThreadAlive();
        public static bool ProfileSaving => s_job != null;

        const string LabOwners = "valheim.realportal.lab";
        // Versions whose save code was read and checked against this module (decompile + IL of the methods named above).
        static readonly string[] VerifiedGameVersions = { "1.0.20" };

        static int s_mainThreadId;
        static int s_cpus = Environment.ProcessorCount;
        static bool s_versionOk;
        static string s_gameVersion = "?";
        static bool s_worldReady, s_profileReady;
        static bool s_worldFailed, s_profileFailed;
        static string s_worldState = "", s_profileState = "";
        static AccessTools.FieldRef<ZNet, Thread> s_zSaveThread;

        static void Install(Harmony harmony)
        {
            s_mainThreadId = Thread.CurrentThread.ManagedThreadId;
            try
            {
                s_gameVersion = global::Version.CurrentVersion.ToString();
                s_versionOk = Array.IndexOf(VerifiedGameVersions, s_gameVersion) >= 0;
            }
            catch (Exception e)
            {
                s_versionOk = false;
                s_gameVersion = "? (" + e.Message + ")";
            }
            if (!s_versionOk)
                EnginePlugin.Log.LogInfo($"SaveSnapshot/ProfileSaveAsync: verified on game {string.Join(", ", VerifiedGameVersions)}, running {s_gameVersion}: the game saves as it is; with SaveVerify on the module's files are only made and compared (rp_saveverify).");
            try
            {
                s_zSaveThread = AccessTools.FieldRefAccess<ZNet, Thread>("m_saveThread");
            }
            catch (Exception e)
            {
                s_zSaveThread = null;
                EnginePlugin.Log.LogWarning("SaveSnapshot/ProfileSaveAsync: ZNet.m_saveThread not found: " + e.Message);
            }
            try
            {
                InstallWorld(harmony);
                s_worldReady = true;
            }
            catch (Exception e)
            {
                EnginePlugin.Log.LogWarning("SaveSnapshot: left out (the game saves the world as it is): " + e.Message);
            }
            try
            {
                InstallProfile(harmony);
                s_profileReady = true;
            }
            catch (Exception e)
            {
                EnginePlugin.Log.LogWarning("ProfileSaveAsync: left out (the game saves the profile as it is): " + e.Message);
            }
            s_world.Switched += on =>
            {
                MaxPrepareMs = 0;
                if (!on && !SaveThreadAlive())
                {
                    // No save reads a snapshot now: drop any (also one left by a save that never reached SaveCleanup)
                    // and the pooled arrays. A save in flight keeps its own; SaveCleanup drops the pool after it.
                    ReleaseActive();
                    s_pool = null;
                }
            };
            s_profile.Switched += on =>
            {
                MaxProfileMainMs = MaxFlushWaitMs = MaxProfileFinishMs = 0;
                if (!on && s_job == null)
                    DropProfileBuffers();
            };
            s_verify.Switched += on =>
            {
                MaxPrepareMs = MaxProfileMainMs = MaxProfileFinishMs = 0;
            };
        }

        public static void ResetCounters()
        {
            WorldSnapshots = WorldFallbacks = WorldErrors = ServeCalls = ServeMisses = WorldShadows = 0;
            MaxPrepareMs = VerifyWorldRefMs = 0;
            ProfileAsyncSaves = ProfileFallbacks = ProfileErrors = ProfileFlushWaits = ProfileShadows = 0;
            MaxProfileMainMs = MaxFlushWaitMs = MaxProfileFinishMs = VerifyProfileRefMs = 0;
            VerifyWorldRuns = VerifyWorldFiles = VerifyWorldMismatches = VerifyWorldBytes = 0;
            VerifyProfileRuns = VerifyProfileMismatches = VerifyMapMismatches = 0;
        }

        static bool SaveThreadAlive()
        {
            ZNet net = ZNet.instance;
            if ((object)net == null || s_zSaveThread == null)
                return false;
            Thread th = s_zSaveThread(net);
            return th != null && th.IsAlive;
        }

        // =================================================================================================================
        // World
        // =================================================================================================================

        static Dictionary<ZDOID, BinarySearchDictionary<int, float>> s_floats;
        static Dictionary<ZDOID, BinarySearchDictionary<int, Vector3>> s_vec3;
        static Dictionary<ZDOID, BinarySearchDictionary<int, Quaternion>> s_quats;
        static Dictionary<ZDOID, BinarySearchDictionary<int, int>> s_ints;
        static Dictionary<ZDOID, BinarySearchDictionary<int, long>> s_longs;
        static Dictionary<ZDOID, BinarySearchDictionary<int, string>> s_strings;
        static Dictionary<ZDOID, BinarySearchDictionary<int, byte[]>> s_byteArrays;
        static Dictionary<ZDOID, ZDOConnectionHashData> s_connHash;
        static HashSet<int> s_sessionOnly;
        static Action s_regenerate;
        static FieldInfo s_saveDataField, s_objectsByChunkField, s_mappingCurrentField;
        static Func<ZDOMan, object> s_saveDataOf;
        static FieldInfo[] s_vanillaSaveFields;   // s_saveFloats .. s_saveConnections
        static MethodBase[] s_worldMethods;

        static volatile WorldSnapshot s_active;
        static WorldSnapshot s_pool;
        static bool s_armed, s_armedShadow;
        static long s_prepareStart;
        static double s_prepareExcludedMs;
        // The thread running the game's reference SaveChunks (SaveVerify), whose GetSaveData calls go to the game.
        static volatile Thread s_referenceThread;
        // The thread writing the module's copy in shadow mode, and the snapshot it is answered from.
        static volatile Thread s_shadowThread;
        static volatile WorldSnapshot s_shadowSnap;
        // A compare due after the real save thread's SaveChunks.
        static VerifyPending s_verifyWorld;

        sealed class VerifyPending
        {
            internal readonly string Dir;
            internal readonly bool Shadow;

            internal VerifyPending(string dir, bool shadow)
            {
                Dir = dir;
                Shadow = shadow;
            }
        }

        [ThreadStatic] static WorldSnapshot t_cursorOwner;
        [ThreadStatic] static int t_cursor;
        [ThreadStatic] static ServeLists t_lists;

        static T StaticField<T>(Type type, string name) where T : class
        {
            FieldInfo f = AccessTools.Field(type, name) ?? throw new MissingFieldException(type.Name, name);
            return f.GetValue(null) as T ?? throw new MissingFieldException(type.Name, name + " (null or other type)");
        }

        static MethodInfo Need(MethodInfo m, string what) => m ?? throw new MissingMethodException(what + " not found (game update?)");

        static void InstallWorld(Harmony harmony)
        {
            Type x = typeof(ZDOExtraData);
            s_floats = StaticField<Dictionary<ZDOID, BinarySearchDictionary<int, float>>>(x, "s_floats");
            s_vec3 = StaticField<Dictionary<ZDOID, BinarySearchDictionary<int, Vector3>>>(x, "s_vec3");
            s_quats = StaticField<Dictionary<ZDOID, BinarySearchDictionary<int, Quaternion>>>(x, "s_quats");
            s_ints = StaticField<Dictionary<ZDOID, BinarySearchDictionary<int, int>>>(x, "s_ints");
            s_longs = StaticField<Dictionary<ZDOID, BinarySearchDictionary<int, long>>>(x, "s_longs");
            s_strings = StaticField<Dictionary<ZDOID, BinarySearchDictionary<int, string>>>(x, "s_strings");
            s_byteArrays = StaticField<Dictionary<ZDOID, BinarySearchDictionary<int, byte[]>>>(x, "s_byteArrays");
            s_connHash = StaticField<Dictionary<ZDOID, ZDOConnectionHashData>>(x, "s_connectionsHashData");
            s_sessionOnly = StaticField<HashSet<int>>(x, "s_sessionOnly");
            s_regenerate = (Action)Delegate.CreateDelegate(typeof(Action),
                Need(AccessTools.DeclaredMethod(x, "RegenerateConnectionHashData", Type.EmptyTypes), "ZDOExtraData.RegenerateConnectionHashData"));
            string[] saveNames = { "s_saveFloats", "s_saveVec3s", "s_saveQuats", "s_saveInts", "s_saveLongs", "s_saveStrings", "s_saveByteArrays", "s_saveConnections" };
            s_vanillaSaveFields = new FieldInfo[saveNames.Length];
            for (int i = 0; i < saveNames.Length; i++)
                s_vanillaSaveFields[i] = AccessTools.Field(x, saveNames[i]) ?? throw new MissingFieldException("ZDOExtraData", saveNames[i]);
            s_saveDataField = AccessTools.Field(typeof(ZDOMan), "m_saveData") ?? throw new MissingFieldException("ZDOMan", "m_saveData");
            s_objectsByChunkField = AccessTools.Field(s_saveDataField.FieldType, "m_objectsByChunk");
            if (s_objectsByChunkField == null || s_objectsByChunkField.FieldType != typeof(List<Tuple<ZoneSystem.ChunkIndex, List<ZDO>>>))
                throw new MissingFieldException("ZDOMan.SaveData", "m_objectsByChunk");
            try
            {
                AccessTools.FieldRef<ZDOMan, object> saveDataRef = AccessTools.FieldRefAccess<ZDOMan, object>(s_saveDataField);
                s_saveDataOf = man => saveDataRef(man);
            }
            catch
            {
                FieldInfo f = s_saveDataField;
                s_saveDataOf = man => f.GetValue(man);
            }
            s_mappingCurrentField = AccessTools.Field(typeof(ZDOMan), "m_chunkSaveMappingCurrent") ?? throw new MissingFieldException("ZDOMan", "m_chunkSaveMappingCurrent");
            if (s_zSaveThread == null)
                throw new MissingFieldException("ZNet", "m_saveThread");
            // The BinarySearchDictionary internals the copy reads (throws here, not at the first save, if they moved).
            Bsd<float>.Check(); Bsd<Vector3>.Check(); Bsd<Quaternion>.Check(); Bsd<int>.Check(); Bsd<long>.Check(); Bsd<string>.Check(); Bsd<byte[]>.Check();

            MethodInfo zdoPrepare = Need(AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.PrepareSave), Type.EmptyTypes), "ZDOMan.PrepareSave");
            MethodInfo saveChunks = Need(AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.SaveChunks), new[] { typeof(string), typeof(FileHelpers.FileSource) }), "ZDOMan.SaveChunks");
            MethodInfo cleanup = Need(AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.SaveCleanup), Type.EmptyTypes), "ZDOMan.SaveCleanup");
            MethodInfo extraPrepare = Need(AccessTools.DeclaredMethod(x, nameof(ZDOExtraData.PrepareSave), Type.EmptyTypes), "ZDOExtraData.PrepareSave");
            MethodInfo getSaveData = Need(AccessTools.DeclaredMethod(x, nameof(ZDOExtraData.GetSaveData)), "ZDOExtraData.GetSaveData");
            MethodInfo clearSave = Need(AccessTools.DeclaredMethod(x, nameof(ZDOExtraData.ClearSave), Type.EmptyTypes), "ZDOExtraData.ClearSave");
            MethodInfo zdoSave = Need(AccessTools.DeclaredMethod(typeof(ZDO), nameof(ZDO.Save), new[] { typeof(ZPackage) }), "ZDO.Save");
            MethodInfo saveWorld = Need(AccessTools.DeclaredMethod(typeof(ZNet), "SaveWorld", new[] { typeof(bool) }), "ZNet.SaveWorld");
            MethodInfo saveThread = Need(AccessTools.DeclaredMethod(typeof(ZNet), "SaveWorldThread", Type.EmptyTypes), "ZNet.SaveWorldThread");
            s_worldMethods = new MethodBase[] { saveWorld, saveThread, zdoPrepare, saveChunks, cleanup, extraPrepare, getSaveData, clearSave, zdoSave };

            Type t = typeof(SaveSnapshotModule);
            // Inert ones first; arming (ZDOMan.PrepareSave) last, so a half-installed module never arms.
            harmony.Patch(cleanup, postfix: new HarmonyMethod(t, nameof(AfterSaveCleanup)) { priority = Priority.Last });
            harmony.Patch(getSaveData, prefix: new HarmonyMethod(t, nameof(BeforeGetSaveData)) { priority = Priority.Last });
            harmony.Patch(saveChunks, postfix: new HarmonyMethod(t, nameof(AfterSaveChunks)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(t, nameof(SaveChunksFinalizer)) { priority = Priority.Last });
            harmony.Patch(extraPrepare, prefix: new HarmonyMethod(t, nameof(BeforeExtraPrepareSave)) { priority = Priority.Last },
                postfix: new HarmonyMethod(t, nameof(AfterExtraPrepareSave)) { priority = Priority.Last });
            harmony.Patch(zdoPrepare, prefix: new HarmonyMethod(t, nameof(BeforeZdoPrepareSave)) { priority = Priority.First },
                finalizer: new HarmonyMethod(t, nameof(ZdoPrepareSaveFinalizer)) { priority = Priority.Last });
        }

        static void ReleaseActive()
        {
            WorldSnapshot s = Interlocked.Exchange(ref s_active, null);
            s?.Release();
        }

        // ---- arming: ZDOMan.PrepareSave ---------------------------------------------------------------------------------

        static void BeforeZdoPrepareSave()
        {
            s_prepareStart = Stopwatch.GetTimestamp();
            s_prepareExcludedMs = 0;
            s_armed = s_armedShadow = false;
            s_verifyWorld = null;   // a compare left by a save whose thread never reached SaveChunks
            // A snapshot left by a save that never reached SaveCleanup (its thread could not mount the save depot, or
            // ZNet.SaveWorld threw after PrepareSave): ZNet.SaveWorld has joined the last save thread before calling this,
            // so nothing reads it any more. Dropped whether or not this save is the module's.
            if (!SaveThreadAlive())
                ReleaseActive();
            if (!s_worldReady || !s_world.On)
                return;
            string why = WorldBlocker();
            if (why != null)
            {
                SetState(ref s_worldState, "standing down: " + why, "SaveSnapshot");
                WorldFallbacks++;
                LastWorldFallback = why;
                return;
            }
            s_armed = true;
            s_armedShadow = !s_versionOk;
            SetState(ref s_worldState, s_armedShadow ? $"shadow (game {s_gameVersion} not verified: the game saves, the module's copy is compared)" : "active", "SaveSnapshot");
        }

        static Exception ZdoPrepareSaveFinalizer(Exception __exception)
        {
            s_armed = s_armedShadow = false;
            LastPrepareMs = Math.Max(0, Ms(s_prepareStart) - s_prepareExcludedMs);
            if (LastPrepareMs > MaxPrepareMs)
                MaxPrepareMs = LastPrepareMs;
            return __exception;
        }

        static string WorldBlocker()
        {
            if (s_worldFailed)
                return "an earlier world save of the module failed this session";
            if (!s_versionOk && !s_verify.On)
                return "game " + s_gameVersion + " not verified";
            ZNet net = ZNet.instance;
            if (net == null || ZNet.World == null || ZDOMan.instance == null)
                return "no world";
            if (ZNet.World.m_fileSource != FileHelpers.FileSource.Local || !FileHelpers.LocalStorageSupportedAndAllowed)
                return "world save is not local (" + ZNet.World.m_fileSource + ")";
            Thread th = s_zSaveThread(net);
            if (th != null && th.IsAlive)
                return "a save thread is still running";
            string foreign = ForeignPatch(s_worldMethods) ?? ForeignDetour(s_worldMethods);
            if (foreign != null)
                return "patched by " + foreign;
            return null;
        }

        // ---- capture: ZDOExtraData.PrepareSave ---------------------------------------------------------------------------

        // The dirty ZDOs' list of this save (null when ZDOMan has not prepared one).
        static List<Tuple<ZoneSystem.ChunkIndex, List<ZDO>>> SaveChunksOf(out object saveData)
        {
            saveData = null;
            try
            {
                ZDOMan man = ZDOMan.instance;
                saveData = (object)man != null ? s_saveDataOf(man) : null;
                return saveData != null ? (List<Tuple<ZoneSystem.ChunkIndex, List<ZDO>>>)s_objectsByChunkField.GetValue(saveData) : null;
            }
            catch
            {
                return null;
            }
        }

        static int LargestMap() => Math.Max(Math.Max(Math.Max(s_floats.Count, s_vec3.Count), Math.Max(s_quats.Count, s_ints.Count)),
            Math.Max(Math.Max(s_longs.Count, s_strings.Count), s_byteArrays.Count));

        static bool BeforeExtraPrepareSave(bool __runOriginal)
        {
            if (!__runOriginal || !s_armed || s_armedShadow)
                return true;   // the shadow copy is taken after the game's own PrepareSave (postfix)
            s_armed = false;
            List<Tuple<ZoneSystem.ChunkIndex, List<ZDO>>> chunks = SaveChunksOf(out object saveData);
            if (chunks == null)
            {
                s_worldFailed = true;
                WorldErrors++;
                Report(2, "SaveSnapshot: ZDOMan.m_saveData is not prepared; this save and the rest of the session use the game's path.");
                return true;
            }
            int d = 0;
            foreach (Tuple<ZoneSystem.ChunkIndex, List<ZDO>> t in chunks)
                d += t.Item2.Count;
            if (!s_verify.On && s_cpus <= 1 && d > 0)
            {
                int n = LargestMap();
                if (d >= OneCpuDirtyShare * n)
                {
                    // One thread copying most of the world costs what the game's clone costs: leave this save to the game.
                    WorldFallbacks++;
                    LastWorldFallback = $"one CPU and {d} dirty ZDOs of ~{n}: the game's full clone is as cheap";
                    return true;
                }
            }
            // The game's first step, as the game runs it: it is part of what gets saved.
            s_regenerate();
            WorldSnapshot snap = TakeSnapshot(chunks, saveData);
            if (snap == null)
            {
                // The rest of the game's PrepareSave (RegenerateConnectionHashData already ran), and the game's path
                // from now on.
                VanillaClones();
                return false;
            }
            WorldSnapshots++;
            s_active = snap;
            if (s_verify.On)
            {
                long v0 = Stopwatch.GetTimestamp();
                VerifyWorldReference();
                VerifyWorldRefMs = Ms(v0);
                s_prepareExcludedMs += VerifyWorldRefMs;
            }
            return false;
        }

        // Shadow (SaveVerify on an unverified game version): the game's own PrepareSave has just made its clones and
        // hashes; the module's copy of the same instant is written beside the save, never in it.
        static void AfterExtraPrepareSave()
        {
            if (!s_armed || !s_armedShadow)
                return;
            s_armed = s_armedShadow = false;
            List<Tuple<ZoneSystem.ChunkIndex, List<ZDO>>> chunks = SaveChunksOf(out object saveData);
            if (chunks == null)
            {
                Report(1, "SaveVerify (shadow): ZDOMan.m_saveData is not prepared, nothing to compare.");
                return;
            }
            long v0 = Stopwatch.GetTimestamp();
            WorldSnapshot snap = TakeSnapshot(chunks, saveData);
            if (snap == null)
                return;
            WorldShadows++;
            try
            {
                ShadowWorld(snap);
            }
            finally
            {
                snap.Release();
                VerifyWorldRefMs = Ms(v0);
                s_prepareExcludedMs += VerifyWorldRefMs;
            }
        }

        // Main thread, at the save point. Null (the feature off for the session, reported) if the copy failed.
        static WorldSnapshot TakeSnapshot(List<Tuple<ZoneSystem.ChunkIndex, List<ZDO>>> chunks, object saveData)
        {
            long t0 = Stopwatch.GetTimestamp();
            WorldSnapshot snap;
            try
            {
                snap = s_pool ?? (s_pool = new WorldSnapshot());
                snap.Capture(chunks, Parallel);
            }
            catch (Exception e)
            {
                s_worldFailed = true;
                WorldErrors++;
                s_pool?.Release();
                s_pool = null;
                Report(2, "SaveSnapshot: snapshot failed, this save and the rest of the session use the game's path: " + e);
                return null;
            }
            snap.SaveData = saveData;
            LastCaptureMs = Ms(t0);
            LastZdos = snap.Count;
            LastChunks = snap.Chunks;
            LastBytes = snap.Bytes;
            LastThreads = snap.Threads;
            return snap;
        }

        // What the game's ZDOExtraData.PrepareSave does after RegenerateConnectionHashData.
        static void VanillaClones()
        {
            s_vanillaSaveFields[0].SetValue(null, s_floats.Clone());
            s_vanillaSaveFields[1].SetValue(null, s_vec3.Clone());
            s_vanillaSaveFields[2].SetValue(null, s_quats.Clone());
            s_vanillaSaveFields[3].SetValue(null, s_ints.Clone());
            s_vanillaSaveFields[4].SetValue(null, s_longs.Clone());
            s_vanillaSaveFields[5].SetValue(null, s_strings.Clone());
            s_vanillaSaveFields[6].SetValue(null, s_byteArrays.Clone());
            s_vanillaSaveFields[7].SetValue(null, s_connHash.Clone());
        }

        static void ClearVanillaClones()
        {
            foreach (FieldInfo f in s_vanillaSaveFields)
                f.SetValue(null, null);
        }

        // ---- serving: ZDOExtraData.GetSaveData (save thread) ---------------------------------------------------------

        static bool BeforeGetSaveData(bool __runOriginal, ZDOID zid, ref List<KeyValuePair<int, float>> floats, ref List<KeyValuePair<int, Vector3>> vec3s,
            ref List<KeyValuePair<int, Quaternion>> quats, ref List<KeyValuePair<int, int>> ints, ref List<KeyValuePair<int, long>> longs,
            ref List<KeyValuePair<int, string>> strings, ref List<KeyValuePair<int, byte[]>> byteArray, ref ZDOConnectionHashData connectionData)
        {
            if (!__runOriginal)
                return true;
            Thread cur = Thread.CurrentThread;
            bool shadow = ReferenceEquals(cur, s_shadowThread);
            WorldSnapshot s;
            if (shadow)
            {
                s = s_shadowSnap;
                if (s == null)
                    return true;
            }
            else
            {
                s = s_active;
                if (s == null || ReferenceEquals(cur, s_referenceThread))
                    return true;
                // Only for the save the snapshot was taken for: one that outlived its save (no SaveCleanup) must never
                // answer a later save's ZDO.Save with older data.
                ZDOMan man = ZDOMan.instance;
                if ((object)man == null || !ReferenceEquals(s_saveDataOf(man), s.SaveData))
                    return true;
            }
            int i = s.Locate(zid);
            ServeLists l = t_lists ?? (t_lists = new ServeLists());
            if (i < 0)
            {
                if (!shadow)
                {
                    // Not in the snapshot. The game's clones exist only when something else made them: let it answer.
                    if (s_vanillaSaveFields[0].GetValue(null) != null)
                        return true;
                    ZNet net = ZNet.instance;
                    Thread saveThread = (object)net != null ? s_zSaveThread(net) : null;
                    if (cur.ManagedThreadId == s_mainThreadId && !ReferenceEquals(cur, saveThread))
                    {
                        // Someone else asking on the main thread during a save (the game never does): the game's clones
                        // share the live arrays, so it would get the live data; give that.
                        HashSet<int> live = s_sessionOnly;
                        FillLive(s_floats, zid, live, l.F);
                        FillLive(s_vec3, zid, live, l.V);
                        FillLive(s_quats, zid, live, l.Q);
                        FillLive(s_ints, zid, live, l.I);
                        FillLive(s_longs, zid, live, l.L);
                        FillLive(s_strings, zid, live, l.S);
                        FillLive(s_byteArrays, zid, live, l.B);
                        floats = l.F;
                        vec3s = l.V;
                        quats = l.Q;
                        ints = l.I;
                        longs = l.L;
                        strings = l.S;
                        byteArray = l.B;
                        connectionData = s_connHash.TryGetValue(zid, out ZDOConnectionHashData c) ? c : null;
                        return false;
                    }
                }
                s_worldFailed = true;
                WorldErrors++;
                throw new InvalidOperationException($"SaveSnapshot: ZDO {zid} is not in the save snapshot; this save fails (old save kept) and the next one is the game's.");
            }
            Interlocked.Increment(ref ServeCalls);
            HashSet<int> session = s.Session;
            s.F.Fill(i, session, l.F);
            s.V.Fill(i, session, l.V);
            s.Q.Fill(i, session, l.Q);
            s.I.Fill(i, session, l.I);
            s.L.Fill(i, session, l.L);
            s.S.Fill(i, session, l.S);
            s.B.Fill(i, session, l.B);
            floats = l.F;
            vec3s = l.V;
            quats = l.Q;
            ints = l.I;
            longs = l.L;
            strings = l.S;
            byteArray = l.B;
            connectionData = s.Conns[i];
            return false;
        }

        static void FillLive<T>(Dictionary<ZDOID, BinarySearchDictionary<int, T>> map, ZDOID zid, HashSet<int> session, List<KeyValuePair<int, T>> list)
        {
            list.Clear();
            if (!map.TryGetValue(zid, out BinarySearchDictionary<int, T> bsd))
                return;
            foreach (KeyValuePair<int, T> kv in bsd)
                if (!session.Contains(kv.Key))
                    list.Add(kv);
        }

        static void AfterSaveCleanup()
        {
            ReleaseActive();
            if (!s_world.On)
                s_pool = null;
        }

        // A save served from the snapshot that fails in SaveChunks (an exception, or false: the game then deletes the new
        // chunk files and keeps the previous save): the rest of the session is the game's.
        static void SaveChunksFinalizer(bool __result, Exception __exception)
        {
            Thread cur = Thread.CurrentThread;
            if (ReferenceEquals(cur, s_referenceThread) || ReferenceEquals(cur, s_shadowThread) || s_active == null)
                return;
            if (__exception == null && __result)
                return;
            if (s_worldFailed)
                return;   // already counted and said (snapshot miss, SaveVerify difference)
            s_worldFailed = true;
            WorldErrors++;
            Report(2, "SaveSnapshot: a world save served from the snapshot failed (" + (__exception != null ? __exception.Message : "SaveChunks returned false")
                + "); the game keeps the previous save, and the rest of the session uses the game's path.");
        }

        sealed class ServeLists
        {
            internal readonly List<KeyValuePair<int, float>> F = new List<KeyValuePair<int, float>>();
            internal readonly List<KeyValuePair<int, Vector3>> V = new List<KeyValuePair<int, Vector3>>();
            internal readonly List<KeyValuePair<int, Quaternion>> Q = new List<KeyValuePair<int, Quaternion>>();
            internal readonly List<KeyValuePair<int, int>> I = new List<KeyValuePair<int, int>>();
            internal readonly List<KeyValuePair<int, long>> L = new List<KeyValuePair<int, long>>();
            internal readonly List<KeyValuePair<int, string>> S = new List<KeyValuePair<int, string>>();
            internal readonly List<KeyValuePair<int, byte[]>> B = new List<KeyValuePair<int, byte[]>>();
        }

        // The BinarySearchDictionary internals: sorted keys and values, the first m_length of them used (its enumerator
        // yields exactly these, in this order).
        static class Bsd<T>
        {
            internal static readonly AccessTools.FieldRef<BinarySearchDictionary<int, T>, int[]> Keys =
                AccessTools.FieldRefAccess<BinarySearchDictionary<int, T>, int[]>("m_keys");
            internal static readonly AccessTools.FieldRef<BinarySearchDictionary<int, T>, T[]> Values =
                AccessTools.FieldRefAccess<BinarySearchDictionary<int, T>, T[]>("m_values");
            internal static readonly AccessTools.FieldRef<BinarySearchDictionary<int, T>, ushort> Length =
                AccessTools.FieldRefAccess<BinarySearchDictionary<int, T>, ushort>("m_length");

            internal static void Check()
            {
                var d = new BinarySearchDictionary<int, T>();
                d[3] = default;
                d[1] = default;
                if (Length(d) != 2 || Keys(d) == null || Keys(d)[0] != 1 || Keys(d)[1] != 3 || Values(d) == null)
                    throw new InvalidOperationException("BinarySearchDictionary<int, " + typeof(T).Name + "> layout changed");
            }
        }

        static void Grow<T>(ref T[] a, int n)
        {
            if (a.Length < n)
                a = new T[Math.Max(n, a.Length + (a.Length >> 1))];
        }

        // One type's extra data of the captured ZDOs: Start/Count per captured ZDO into the flat Keys/Values.
        class Store<T>
        {
            internal readonly Dictionary<ZDOID, BinarySearchDictionary<int, T>> Map;
            internal readonly string Name;
            internal int[] Start = Array.Empty<int>();
            internal ushort[] Count = Array.Empty<ushort>();
            internal int[] Keys = Array.Empty<int>();
            internal T[] Values = Array.Empty<T>();
            internal int Total;

            internal Store(string name, Dictionary<ZDOID, BinarySearchDictionary<int, T>> map)
            {
                Name = name;
                Map = map;
            }

            internal void Prepare(int d)
            {
                Grow(ref Start, d);
                Grow(ref Count, d);
                Total = 0;
            }

            internal void Capture(WorldSnapshot s)
            {
                int d = s.Count;
                ZDOID[] ids = s.Ids;
                for (int i = 0; i < d; i++)
                {
                    if (Map.TryGetValue(ids[i], out BinarySearchDictionary<int, T> bsd))
                        Append(i, bsd);
                    else
                        Count[i] = 0;
                }
                AfterCapture();
            }

            void Append(int i, BinarySearchDictionary<int, T> bsd)
            {
                // The game never keeps a null map (Release and Remove take the entry out); its Clone would throw on one,
                // so the capture does too and the game's path runs (and throws the same way).
                if (bsd == null)
                    throw new InvalidOperationException(Name + ": null map for the dirty ZDO at " + i);
                int n = Bsd<T>.Length(bsd);
                Start[i] = Total;
                Count[i] = (ushort)n;
                if (n == 0)
                    return;
                int need = Total + n;
                if (Keys.Length < need)
                {
                    int cap = Math.Max(need, Keys.Length + (Keys.Length >> 1) + 64);
                    Array.Resize(ref Keys, cap);
                    Array.Resize(ref Values, cap);
                }
                Array.Copy(Bsd<T>.Keys(bsd), 0, Keys, Total, n);
                Array.Copy(Bsd<T>.Values(bsd), 0, Values, Total, n);
                Total = need;
            }

            protected virtual void AfterCapture()
            {
            }

            internal virtual void Release()
            {
                if (!typeof(T).IsValueType)
                    Array.Clear(Values, 0, Total);
            }

            // ZDOHelper.GetValuesOrEmptyFilterSession over the copy.
            internal void Fill(int i, HashSet<int> session, List<KeyValuePair<int, T>> list)
            {
                list.Clear();
                int n = Count[i];
                if (n == 0)
                    return;
                for (int k = Start[i], end = Start[i] + n; k < end; k++)
                {
                    int key = Keys[k];
                    if (!session.Contains(key))
                        list.Add(new KeyValuePair<int, T>(key, Values[k]));
                }
            }

            internal virtual long Bytes() => Total * (4L + (typeof(T).IsValueType ? System.Runtime.InteropServices.Marshal.SizeOf(typeof(T)) : IntPtr.Size));
        }

        // Byte arrays: each one cloned at the save point (the game's code changes some in place, e.g. ArcheryTarget's
        // s_data); the save thread hands these clones to ZDO.Save, which only reads them. One copy, nothing retained
        // after SaveCleanup.
        sealed class ByteStore : Store<byte[]>
        {
            long _data;

            internal ByteStore(Dictionary<ZDOID, BinarySearchDictionary<int, byte[]>> map) : base("bytes", map)
            {
            }

            protected override void AfterCapture()
            {
                long b = 0;
                for (int j = 0; j < Total; j++)
                {
                    byte[] a = Values[j];
                    if (a == null)
                        continue;
                    Values[j] = (byte[])a.Clone();
                    b += a.Length;
                }
                _data = b;
            }

            internal override long Bytes() => Total * (4L + IntPtr.Size) + _data;
        }

        sealed class StringStore : Store<string>
        {
            internal StringStore(Dictionary<ZDOID, BinarySearchDictionary<int, string>> map) : base("strings", map)
            {
            }

            internal override long Bytes()
            {
                long b = Total * (4L + IntPtr.Size);
                for (int j = 0; j < Total; j++)
                    b += (Values[j]?.Length ?? 0) * 2L;
                return b;
            }
        }

        // Open-addressing index ZDOID -> position in the snapshot (first position for a ZDOID listed twice).
        sealed class ZdoIndex
        {
            ZDOID[] _keys = Array.Empty<ZDOID>();
            int[] _slots = Array.Empty<int>();
            int _mask;

            internal void Reset(int n)
            {
                int cap = 16;
                while (cap < n * 2)
                    cap <<= 1;
                if (_slots.Length < cap)
                {
                    _slots = new int[cap];
                    _keys = new ZDOID[cap];
                }
                else
                {
                    Array.Clear(_slots, 0, cap);
                }
                _mask = cap - 1;
            }

            static int Hash(ZDOID id)
            {
                ulong h = (ulong)id.UserID * 0x9E3779B97F4A7C15UL + id.ID * 0xC2B2AE3D27D4EB4FUL;
                h ^= h >> 31;
                return (int)h;
            }

            internal int AddOrGet(ZDOID id, int index)
            {
                int s = Hash(id) & _mask;
                while (true)
                {
                    int v = _slots[s];
                    if (v == 0)
                    {
                        _slots[s] = index + 1;
                        _keys[s] = id;
                        return index;
                    }
                    if (_keys[s] == id)
                        return v - 1;
                    s = (s + 1) & _mask;
                }
            }

            internal int Find(ZDOID id)
            {
                int s = Hash(id) & _mask;
                while (true)
                {
                    int v = _slots[s];
                    if (v == 0)
                        return -1;
                    if (_keys[s] == id)
                        return v - 1;
                    s = (s + 1) & _mask;
                }
            }
        }

        sealed class WorldSnapshot
        {
            internal ZDOID[] Ids = Array.Empty<ZDOID>();
            internal ZDOConnectionHashData[] Conns = Array.Empty<ZDOConnectionHashData>();
            internal int Count, Chunks, Threads;
            internal long Bytes;
            // ZDOMan.m_saveData of the save this snapshot was taken for.
            internal object SaveData;
            internal readonly HashSet<int> Session = new HashSet<int>();
            internal readonly Store<float> F = new Store<float>("floats", s_floats);
            internal readonly Store<Vector3> V = new Store<Vector3>("vec3", s_vec3);
            internal readonly Store<Quaternion> Q = new Store<Quaternion>("quats", s_quats);
            internal readonly Store<int> I = new Store<int>("ints", s_ints);
            internal readonly Store<long> L = new Store<long>("longs", s_longs);
            internal readonly StringStore S = new StringStore(s_strings);
            internal readonly ByteStore B = new ByteStore(s_byteArrays);
            internal readonly ZdoIndex Index = new ZdoIndex();
            volatile bool _indexReady;
            readonly object _indexLock = new object();
            readonly Action[] _tasks;

            internal WorldSnapshot()
            {
                _tasks = new Action[] { () => F.Capture(this), () => V.Capture(this), () => Q.Capture(this), () => I.Capture(this),
                    () => L.Capture(this), () => S.Capture(this), () => B.Capture(this), CaptureConnections };
            }

            // Main thread, inside ZDOMan.PrepareSave right after the game cloned the dirty ZDOs.
            internal void Capture(List<Tuple<ZoneSystem.ChunkIndex, List<ZDO>>> chunks, bool parallel)
            {
                int d = 0;
                foreach (Tuple<ZoneSystem.ChunkIndex, List<ZDO>> t in chunks)
                    d += t.Item2.Count;
                Grow(ref Ids, d);
                Grow(ref Conns, d);
                int k = 0;
                foreach (Tuple<ZoneSystem.ChunkIndex, List<ZDO>> t in chunks)
                    foreach (ZDO z in t.Item2)
                        Ids[k++] = z.m_uid;
                Count = d;
                Chunks = chunks.Count;
                SaveData = null;
                _indexReady = false;
                F.Prepare(d);
                V.Prepare(d);
                Q.Prepare(d);
                I.Prepare(d);
                L.Prepare(d);
                S.Prepare(d);
                B.Prepare(d);
                Session.Clear();
                Session.UnionWith(s_sessionOnly);
                // Eight tasks, one per type map (each one lookup per captured ZDO in its own map).
                Threads = RunAll(_tasks, parallel && 8L * d >= ParallelMinWork);
                Bytes = F.Bytes() + V.Bytes() + Q.Bytes() + I.Bytes() + L.Bytes() + S.Bytes() + B.Bytes() + d * (8L + IntPtr.Size);
            }

            // ZDOExtraData.s_saveConnections.GetValueOrDefaultPiktiv(zid, null) for each captured ZDO.
            void CaptureConnections()
            {
                int d = Count;
                for (int i = 0; i < d; i++)
                    Conns[i] = s_connHash.TryGetValue(Ids[i], out ZDOConnectionHashData c) ? c : null;
            }

            // Save thread, only when a ZDO is asked out of snapshot order. A ZDOID listed twice has its data twice (each
            // position was looked up on its own), so the first position is as good as any.
            void BuildIndex()
            {
                Index.Reset(Count);
                for (int i = 0; i < Count; i++)
                    Index.AddOrGet(Ids[i], i);
                _indexReady = true;
            }

            // Position of zid's data; the save thread asks in snapshot order, so this is usually the next one.
            internal int Locate(ZDOID zid)
            {
                int c = ReferenceEquals(t_cursorOwner, this) ? t_cursor : 0;
                int i;
                if (c < Count && Ids[c] == zid)
                {
                    i = c;
                }
                else
                {
                    Interlocked.Increment(ref ServeMisses);
                    if (!_indexReady)
                        lock (_indexLock)
                            if (!_indexReady)
                                BuildIndex();
                    i = Index.Find(zid);
                    if (i < 0)
                        return -1;
                }
                t_cursorOwner = this;
                t_cursor = i + 1;
                return i;
            }

            internal void Release()
            {
                F.Release();
                V.Release();
                Q.Release();
                I.Release();
                L.Release();
                S.Release();
                B.Release();
                Array.Clear(Conns, 0, Count);
                SaveData = null;
                // A new capture into this pooled object rebuilds the index; a cursor left on another thread starts over.
                _indexReady = false;
            }
        }

        // Runs the tasks, on pool threads and this one when parallel. Returns the threads used.
        static int RunAll(Action[] tasks, bool parallel)
        {
            int n = tasks.Length;
            int helpers = parallel ? Math.Min(n - 1, Math.Max(0, s_cpus - 1)) : 0;
            if (helpers <= 0)
            {
                for (int j = 0; j < n; j++)
                    tasks[j]();
                return 1;
            }
            int next = -1, pending = n, used = 1;
            Exception error = null;
            using (var done = new ManualResetEventSlim(false))
            {
                void Work(bool pool)
                {
                    bool counted = false;
                    int k;
                    while ((k = Interlocked.Increment(ref next)) < n)
                    {
                        if (pool && !counted)
                        {
                            counted = true;
                            Interlocked.Increment(ref used);
                        }
                        try
                        {
                            tasks[k]();
                        }
                        catch (Exception e)
                        {
                            Interlocked.CompareExchange(ref error, e, null);
                        }
                        if (Interlocked.Decrement(ref pending) == 0)
                            done.Set();
                    }
                }
                WaitCallback poolWork = _ => Work(true);
                for (int h = 0; h < helpers; h++)
                {
                    try
                    {
                        ThreadPool.UnsafeQueueUserWorkItem(poolWork, null);
                    }
                    catch
                    {
                        break;   // this thread takes what no pool thread does
                    }
                }
                Work(false);
                // Returns once every task has finished, including those pool threads took; a pool thread that starts
                // later finds nothing left and never touches the event.
                done.Wait();
            }
            if (error != null)
                throw new InvalidOperationException("snapshot copy failed: " + error.Message, error);
            return used;
        }

        // ---- SaveVerify: a second world save at the save point, compared after the real one ---------------------------

        static string VerifyRoot() => Utils.GetSaveDataPath(FileHelpers.FileSource.Local) + "/rp_saveverify";

        static void ResetDir(string dir)
        {
            if (Directory.Exists(dir))
                foreach (string f in Directory.GetFiles(dir))
                    File.Delete(f);
            Directory.CreateDirectory(dir);
        }

        // The game's SaveChunks over the game's clones, on the frozen world (the real save is the snapshot's).
        static void VerifyWorldReference()
        {
            s_verifyWorld = null;
            string dir = VerifyRoot() + "/world/";
            ZDOMan man = ZDOMan.instance;
            try
            {
                ResetDir(dir);
                VanillaClones();
                s_referenceThread = Thread.CurrentThread;
                if (man.SaveChunks(dir, FileHelpers.FileSource.Local))
                    s_verifyWorld = new VerifyPending(dir, false);
                else
                    Report(1, "SaveVerify: the game's reference SaveChunks reported a failure, nothing to compare.");
            }
            catch (Exception e)
            {
                Report(1, "SaveVerify: reference world save failed: " + e.Message);
            }
            finally
            {
                s_referenceThread = null;
                ClearVanillaClones();
                s_mappingCurrentField.SetValue(man, null);
            }
        }

        // Shadow: the game's SaveChunks answered from the module's snapshot, on the frozen world (the real save is the
        // game's own, from its clones).
        static void ShadowWorld(WorldSnapshot snap)
        {
            s_verifyWorld = null;
            string dir = VerifyRoot() + "/world/";
            ZDOMan man = ZDOMan.instance;
            try
            {
                ResetDir(dir);
                s_shadowSnap = snap;
                s_shadowThread = Thread.CurrentThread;
                if (man.SaveChunks(dir, FileHelpers.FileSource.Local))
                    s_verifyWorld = new VerifyPending(dir, true);
                else
                    Report(1, "SaveVerify (shadow): the module's world copy reported a failure, nothing to compare.");
            }
            catch (Exception e)
            {
                Report(1, "SaveVerify (shadow): the module's world copy failed: " + e.Message);
            }
            finally
            {
                s_shadowThread = null;
                s_shadowSnap = null;
                s_mappingCurrentField.SetValue(man, null);
            }
        }

        // The real save thread's SaveChunks is done: compare. A difference while the real files came from the snapshot
        // fails this save (the game's failure path keeps the previous save and the dirty chunks).
        static void AfterSaveChunks(string path, ref bool __result)
        {
            Thread cur = Thread.CurrentThread;
            if (ReferenceEquals(cur, s_referenceThread) || ReferenceEquals(cur, s_shadowThread))
                return;
            VerifyPending v = Interlocked.Exchange(ref s_verifyWorld, null);
            if (v == null)
                return;
            string mode = v.Shadow ? "SaveVerify (shadow)" : "SaveVerify";
            if (!__result)
            {
                Report(1, mode + ": the world save failed, nothing compared.");
                return;
            }
            try
            {
                int files = 0, bad = 0;
                long bytes = 0;
                var notes = new List<string>();
                foreach (string f in Directory.GetFiles(v.Dir))
                {
                    string name = Path.GetFileName(f);
                    string real = path + (name.EndsWith(".chunks", StringComparison.Ordinal) ? SaveSystem.MainChunksFileName() : name);
                    byte[] a = File.ReadAllBytes(f);
                    byte[] b = File.Exists(real) ? File.ReadAllBytes(real) : null;
                    files++;
                    bytes += a.Length;
                    int at = FirstDifference(a, 0, a.Length, b);
                    if (at >= 0)
                    {
                        bad++;
                        if (notes.Count < 8)
                            notes.Add(b == null ? $"{name}: missing in the real save" : $"{name}: {a.Length} vs {b.Length} bytes, first difference at {at}");
                    }
                }
                Interlocked.Increment(ref VerifyWorldRuns);
                Interlocked.Add(ref VerifyWorldFiles, files);
                Interlocked.Add(ref VerifyWorldMismatches, bad);
                Interlocked.Add(ref VerifyWorldBytes, bytes);
                LastVerify = $"world{(v.Shadow ? " (shadow)" : "")}: {files} files, {bytes} bytes, {bad} different";
                if (bad == 0)
                {
                    Directory.Delete(v.Dir, true);
                    Report(0, mode + ": " + LastVerify + " (byte-identical to the game's).");
                    return;
                }
                s_worldFailed = true;
                if (!v.Shadow)
                    __result = false;
                Report(2, $"{mode}: {LastVerify}; the {(v.Shadow ? "module's" : "game's")} files are kept in {v.Dir}: " + string.Join("; ", notes)
                    + (v.Shadow ? ". The save itself is the game's; the shadow stops for the session."
                        : ". This world save is discarded (the game keeps the previous save and the dirty chunks); the rest of the session is the game's."));
            }
            catch (Exception e)
            {
                Report(1, mode + ": world compare failed: " + e.Message);
            }
        }

        // Offset of the first byte where b differs from a[from, from+count) (b compared from bFrom), or -1; a shorter or
        // null b differs at its end.
        static int FirstDifference(byte[] a, int from, int count, byte[] b, int bFrom = 0)
        {
            if (b == null)
                return 0;
            int n = Math.Max(0, Math.Min(count, b.Length - bFrom));
            for (int k = 0; k < n; k++)
                if (a[from + k] != b[bFrom + k])
                    return k;
            return n < count ? n : -1;
        }

        // =================================================================================================================
        // Profile
        // =================================================================================================================

        static AccessTools.FieldRef<Game, PlayerProfile> s_gameProfile;
        static AccessTools.FieldRef<PlayerProfile, bool> s_createBackup;
        static AccessTools.FieldRef<PlayerProfile, DateTime> s_lastSaveLoad;
        static AccessTools.FieldRef<PlayerProfile, string> s_playerName, s_startSeed;
        static AccessTools.FieldRef<PlayerProfile, long> s_playerID;
        static AccessTools.FieldRef<PlayerProfile, byte[]> s_playerData;
        static FieldInfo s_worldData;
        static MethodInfo s_getWorldData;
        static FieldInfo s_wHaveCustomSpawn, s_wSpawn, s_wHaveLogout, s_wLogout, s_wHaveDeath, s_wDeath, s_wHome, s_wMapData;
        static AccessTools.FieldRef<Minimap, BitArray> s_mmExplored, s_mmOthers;
        static AccessTools.FieldRef<Minimap, List<Minimap.PinData>> s_mmPins;
        static FieldInfo s_pinAuthor;
        static MethodInfo s_mmGetMapData;
        static MethodInfo s_memberwiseClone;
        // Splatform-typed calls (the engine does not reference Splatform.dll), bound once.
        static MethodInfo s_mount, s_unmount;
        static object s_readWrite, s_unmountDefault;
        static ConstructorInfo s_fileWriterCtor;
        static MethodInfo s_replaceOldFile;
        static object s_sameFileEnding;
        static PropertyInfo s_distributionPlatform;
        static MethodBase[] s_profileMethods;

        static volatile ProfileJob s_job;
        static double s_profileExcludedMs;
        // Map buffers (one map built at a time: the writer, or the main thread while no writer runs).
        static readonly object s_mapLock = new object();
        static int[] s_wordsA = Array.Empty<int>(), s_wordsB = Array.Empty<int>();
        static byte[] s_mapBuf = Array.Empty<byte>();
        static MemoryStream s_mapZ;
        // Shadow mode: the replica of the profile save in progress (main thread only).
        static ProfileShadow s_shadow;

        static void DropProfileBuffers()
        {
            lock (s_mapLock)
            {
                s_wordsA = s_wordsB = Array.Empty<int>();
                s_mapBuf = Array.Empty<byte>();
                s_mapZ = null;
            }
        }

        static void InstallProfile(Harmony harmony)
        {
            Type p = typeof(PlayerProfile);
            s_gameProfile = AccessTools.FieldRefAccess<Game, PlayerProfile>("m_playerProfile");
            s_createBackup = AccessTools.FieldRefAccess<PlayerProfile, bool>("m_createBackupBeforeSaving");
            s_lastSaveLoad = AccessTools.FieldRefAccess<PlayerProfile, DateTime>("m_lastSaveLoad");
            s_playerName = AccessTools.FieldRefAccess<PlayerProfile, string>("m_playerName");
            s_startSeed = AccessTools.FieldRefAccess<PlayerProfile, string>("m_startSeed");
            s_playerID = AccessTools.FieldRefAccess<PlayerProfile, long>("m_playerID");
            s_playerData = AccessTools.FieldRefAccess<PlayerProfile, byte[]>("m_playerData");
            s_worldData = AccessTools.Field(p, "m_worldData") ?? throw new MissingFieldException("PlayerProfile", "m_worldData");
            if (!typeof(IDictionary).IsAssignableFrom(s_worldData.FieldType))
                throw new MissingFieldException("PlayerProfile", "m_worldData (not a dictionary)");
            s_getWorldData = Need(AccessTools.DeclaredMethod(p, "GetWorldData", new[] { typeof(long) }), "PlayerProfile.GetWorldData");
            Type w = s_getWorldData.ReturnType;
            FieldInfo WF(string n) => AccessTools.Field(w, n) ?? throw new MissingFieldException("WorldPlayerData", n);
            s_wHaveCustomSpawn = WF("m_haveCustomSpawnPoint");
            s_wSpawn = WF("m_spawnPoint");
            s_wHaveLogout = WF("m_haveLogoutPoint");
            s_wLogout = WF("m_logoutPoint");
            s_wHaveDeath = WF("m_haveDeathPoint");
            s_wDeath = WF("m_deathPoint");
            s_wHome = WF("m_homePoint");
            s_wMapData = WF("m_mapData");
            s_mmExplored = AccessTools.FieldRefAccess<Minimap, BitArray>("m_explored");
            s_mmOthers = AccessTools.FieldRefAccess<Minimap, BitArray>("m_exploredOthers");
            s_mmPins = AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");
            s_pinAuthor = AccessTools.Field(typeof(Minimap.PinData), "m_author") ?? throw new MissingFieldException("Minimap.PinData", "m_author");
            s_mmGetMapData = Need(AccessTools.DeclaredMethod(typeof(Minimap), "GetMapData", Type.EmptyTypes), "Minimap.GetMapData");
            s_memberwiseClone = Need(AccessTools.Method(typeof(object), "MemberwiseClone"), "object.MemberwiseClone");

            s_mount = Need(AccessTools.DeclaredMethod(typeof(FileHelpers), "Mount"), "FileHelpers.Mount");
            s_readWrite = Enum.Parse(s_mount.GetParameters()[0].ParameterType, "ReadWrite");
            s_unmount = Need(AccessTools.DeclaredMethod(typeof(FileHelpers), "Unmount"), "FileHelpers.Unmount");
            ParameterInfo um = s_unmount.GetParameters()[0];
            s_unmountDefault = um.HasDefaultValue ? Enum.ToObject(um.ParameterType, um.RawDefaultValue) : Enum.Parse(um.ParameterType, "Commit");
            s_replaceOldFile = Need(AccessTools.DeclaredMethod(typeof(FileHelpers), "ReplaceOldFile"), "FileHelpers.ReplaceOldFile");
            Type grouping = s_replaceOldFile.GetParameters()[3].ParameterType;
            s_sameFileEnding = Enum.Parse(grouping, "SameFileEnding");
            s_fileWriterCtor = AccessTools.DeclaredConstructor(typeof(FileWriter), new[] { typeof(string), grouping, typeof(FileHelpers.FileHelperType), typeof(FileHelpers.FileSource) })
                ?? throw new MissingMethodException("FileWriter(string, CloudStorageFileGrouping, FileHelperType, FileSource) not found");
            Type pm = s_mount.GetParameters()[0].ParameterType.Assembly.GetType("Splatform.PlatformManager");
            s_distributionPlatform = pm != null ? AccessTools.Property(pm, "DistributionPlatform") : null;
            if (s_distributionPlatform == null)
                throw new MissingMemberException("Splatform.PlatformManager", "DistributionPlatform");

            MethodInfo savePlayerProfile = Need(AccessTools.DeclaredMethod(typeof(Game), nameof(Game.SavePlayerProfile), new[] { typeof(bool), typeof(bool) }), "Game.SavePlayerProfile");
            MethodInfo update = Need(AccessTools.DeclaredMethod(typeof(Game), "Update", Type.EmptyTypes), "Game.Update");
            MethodInfo onDestroy = Need(AccessTools.DeclaredMethod(typeof(Game), "OnDestroy", Type.EmptyTypes), "Game.OnDestroy");
            MethodInfo shutdown = Need(AccessTools.DeclaredMethod(typeof(Game), "Shutdown", new[] { typeof(bool) }), "Game.Shutdown");
            MethodInfo toDisk = Need(AccessTools.DeclaredMethod(p, "SavePlayerToDisk", Type.EmptyTypes), "PlayerProfile.SavePlayerToDisk");
            MethodInfo fromDisk = Need(AccessTools.DeclaredMethod(p, "LoadPlayerFromDisk", Type.EmptyTypes), "PlayerProfile.LoadPlayerFromDisk");
            MethodInfo getMap = Need(AccessTools.DeclaredMethod(p, nameof(PlayerProfile.GetMapData), Type.EmptyTypes), "PlayerProfile.GetMapData");
            MethodInfo save = Need(AccessTools.DeclaredMethod(p, nameof(PlayerProfile.Save), Type.EmptyTypes), "PlayerProfile.Save");
            MethodInfo setMap = Need(AccessTools.DeclaredMethod(p, nameof(PlayerProfile.SetMapData), new[] { typeof(byte[]) }), "PlayerProfile.SetMapData");
            MethodInfo saveMap = Need(AccessTools.DeclaredMethod(typeof(Minimap), nameof(Minimap.SaveMapData), Type.EmptyTypes), "Minimap.SaveMapData");
            MethodInfo writeCompressed = Need(AccessTools.DeclaredMethod(typeof(ZPackage), nameof(ZPackage.WriteCompressed), new[] { typeof(ZPackage) }), "ZPackage.WriteCompressed");
            MethodInfo generateHash = Need(AccessTools.DeclaredMethod(typeof(ZPackage), nameof(ZPackage.GenerateHash), Type.EmptyTypes), "ZPackage.GenerateHash");
            MethodInfo compress = Need(AccessTools.DeclaredMethod(typeof(Utils), nameof(Utils.Compress), new[] { typeof(byte[]) }), "Utils.Compress");
            MethodInfo finish = Need(AccessTools.DeclaredMethod(typeof(FileWriter), nameof(FileWriter.Finish), Type.EmptyTypes), "FileWriter.Finish");
            s_profileMethods = new MethodBase[] { savePlayerProfile, save, toDisk, setMap, saveMap, s_mmGetMapData, writeCompressed, generateHash, compress, s_replaceOldFile, finish };

            Type t = typeof(SaveSnapshotModule);
            // Flush points first (inert while nothing is queued); the replacement last.
            harmony.Patch(update, postfix: new HarmonyMethod(t, nameof(AfterGameUpdate)));
            harmony.Patch(onDestroy, prefix: new HarmonyMethod(t, nameof(FlushPrefix)) { priority = Priority.First });
            harmony.Patch(shutdown, prefix: new HarmonyMethod(t, nameof(FlushPrefix)) { priority = Priority.First });
            harmony.Patch(toDisk, prefix: new HarmonyMethod(t, nameof(BeforeSavePlayerToDisk)) { priority = Priority.First },
                postfix: new HarmonyMethod(t, nameof(AfterSavePlayerToDisk)) { priority = Priority.Last });
            harmony.Patch(fromDisk, prefix: new HarmonyMethod(t, nameof(FlushPrefix)) { priority = Priority.First });
            // PlayerProfile.GetMapData is small enough to be inlined into its one caller (Minimap.LoadMapData, at world
            // start, after the flush of the previous Game's shutdown); this is a second line, not the only one.
            harmony.Patch(getMap, prefix: new HarmonyMethod(t, nameof(FlushPrefix)) { priority = Priority.First });
            harmony.Patch(savePlayerProfile, prefix: new HarmonyMethod(t, nameof(BeforeSavePlayerProfile)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(t, nameof(SavePlayerProfileFinalizer)) { priority = Priority.Last });
        }

        sealed class MapSnap
        {
            internal int TextureSize, Length;
            internal int[] Explored, Others;
            internal readonly List<PinSnap> Pins = new List<PinSnap>();
            internal bool ReferencePublic;
        }

        struct PinSnap
        {
            internal string Name, Author;
            internal Vector3 Pos;
            internal int Type;
            internal bool Checked;
            internal long Owner;
        }

        sealed class ProfileJob
        {
            internal PlayerProfile Profile;
            internal object Entry;
            internal MapSnap Map;
            internal byte[] Head, Tail;
            internal readonly StatsUndo Undo = new StatsUndo();
            internal string Filename, Path, NewPath, OldPath;
            internal FileHelpers.FileSource Source;
            internal DateTime Now;
            internal byte[] MapBytes;
            internal Exception Error;
            internal volatile bool Done;
            internal readonly ManualResetEventSlim Finished = new ManualResetEventSlim(false);
            internal long FileBytes;
            internal double WorkerMs;
            // SaveVerify.
            internal byte[] VanillaMap;
            internal string VanillaFile, VerifyDir, VerifyResult;
            internal bool VerifyBad;
        }

        // Shadow mode: the module's replica of one Game.SavePlayerProfile, compared with the file the game writes.
        sealed class ProfileShadow
        {
            internal PlayerProfile Profile;
            internal MapSnap Map;
            internal long WorldUID;
            internal byte[] Pkg, Hash, OurMap, GameMap;
        }

        // ---- Game.SavePlayerProfile -------------------------------------------------------------------------------------

        static bool BeforeSavePlayerProfile(Game __instance, bool __runOriginal, bool setLogoutPoint, bool isFromRpc)
        {
            s_shadow = null;
            if (!__runOriginal || !s_profileReady)
                return true;
            // Whatever runs now, an earlier write is finished first.
            Flush();
            if (!s_profile.On)
                return true;
            string why = ProfileBlocker(__instance, isFromRpc, out PlayerProfile profile);
            if (why != null)
            {
                if (why.Length > 0)
                {
                    ProfileFallbacks++;
                    LastProfileFallback = why;
                    SetState(ref s_profileState, "standing down: " + why, "ProfileSaveAsync");
                }
                return true;
            }
            if (!s_versionOk)
            {
                // SaveVerify on an unverified game version: the game saves; the module's file is only made and compared.
                SetState(ref s_profileState, $"shadow (game {s_gameVersion} not verified: the game saves, the module's file is compared)", "ProfileSaveAsync");
                ArmProfileShadow(profile);
                return true;
            }
            SetState(ref s_profileState, "active", "ProfileSaveAsync");
            long t0 = Stopwatch.GetTimestamp();
            s_profileExcludedMs = 0;
            try
            {
                SaveProfileAsync(__instance, profile, setLogoutPoint);
            }
            finally
            {
                LastProfileMainMs = Math.Max(0, Ms(t0) - s_profileExcludedMs);
                if (LastProfileMainMs > MaxProfileMainMs)
                    MaxProfileMainMs = LastProfileMainMs;
            }
            return false;
        }

        static void SavePlayerProfileFinalizer()
        {
            s_shadow = null;
        }

        // Null: go async (or shadow on an unverified version). "": the game's path, which returns early by itself (not a
        // fallback). Else the reason.
        static string ProfileBlocker(Game game, bool isFromRpc, out PlayerProfile profile)
        {
            profile = null;
            if (SaveSystem.HasSessionFlag(SaveSystemSessionFlags.DontSaveCharacter))
                return "";
            if (isFromRpc && ZNet.instance != null && ZNet.instance.HardSaveBlock())
                return "";
            if (s_profileFailed)
                return "a profile save of the module failed earlier this session";
            if (!s_versionOk && !s_verify.On)
                return "game " + s_gameVersion + " not verified";
            if (game.IsShuttingDown())
                return "shutting down";
            profile = s_gameProfile(game);
            if (profile == null || profile.m_filename == null)
                return "no profile";
            if (profile.m_fileSource != FileHelpers.FileSource.Local || !FileHelpers.LocalStorageSupportedAndAllowed)
                return "profile is not local (" + profile.m_fileSource + ")";
            if (!(bool)Player.m_localPlayer)
                return "no local player";
            Minimap mm = Minimap.instance;
            if (!(bool)mm)
                return "no minimap";
            if (!(bool)ZNet.instance || ZNet.instance.GetWorldUID() == 0L)
                return "no world";
            BitArray e = s_mmExplored(mm), o = s_mmOthers(mm);
            if (e == null || o == null || e.Length != o.Length || s_mmPins(mm) == null)
                return "minimap layout";
            string foreign = ForeignPatch(s_profileMethods) ?? ForeignDetour(s_profileMethods);
            if (foreign != null)
                return "patched by " + foreign;
            string handler = ForeignHandler(PlayerProfile.SavingStarted, "SavingStarted") ?? ForeignHandler(PlayerProfile.SavingFinished, "SavingFinished");
            if (handler != null)
                return handler;
            return null;
        }

        // A handler of the profile's save events other than the game's own (Hud, Menu): it may read the profile's map
        // bytes at SavingStarted or set them during the writer window.
        static string ForeignHandler(Action a, string name)
        {
            if (a == null)
                return null;
            foreach (Delegate d in a.GetInvocationList())
            {
                Type t = d.Method.DeclaringType, top = t;
                while (top?.DeclaringType != null)
                    top = top.DeclaringType;
                if (top == typeof(Hud) || top == typeof(Menu))
                    continue;
                return $"a PlayerProfile.{name} handler ({t?.FullName ?? "?"}.{d.Method.Name})";
            }
            return null;
        }

        // Game.SavePlayerProfile after its guards, with SaveMapData and PlayerProfile.Save split as described above.
        static void SaveProfileAsync(Game game, PlayerProfile profile, bool setLogoutPoint)
        {
            game.m_saveTimer = 0f;
            bool mounted = false;
            SystemResourceManager.PushIsInLoadingScreen();
            if (FileHelpers.CloudStorageSupported)
            {
                mounted = Mount();
                if (!mounted)
                    throw new InvalidOperationException("Failed to mount!");
            }
            try
            {
                MapSnap map = null;
                object entry = null;
                long worldUID = 0L;
                if ((bool)Player.m_localPlayer)
                {
                    profile.SavePlayerData(Player.m_localPlayer);
                    // Minimap.instance.SaveMapData() = profile.SetMapData(GetMapData()): the map is read now, its bytes
                    // are made by the writer; SetMapData's GetWorldData (which may add this world's entry) runs now.
                    map = CaptureMap(Minimap.instance);
                    worldUID = ZNet.instance.GetWorldUID();
                    if (worldUID != 0L)
                        entry = s_getWorldData.Invoke(profile, new object[] { worldUID });
                    if (setLogoutPoint)
                        profile.SaveLogoutPoint();
                }
                if (FileHelpers.OperationExceedsCloudCapacity(profile.m_fileSource, 1048576uL, 1, profile.GetPath().ToSingleArray()))
                {
                    string path = profile.GetPath();
                    profile.m_fileSource = FileHelpers.FileSource.Local;
                    string path2 = profile.GetPath();
                    if (FileHelpers.FileExistsCloud(path))
                        FileHelpers.FileCopyOutFromCloud(path, path2, deleteOnCloud: true);
                    SaveSystem.InvalidateCache(SaveDataType.Character);
                    ZLog.LogWarning("The character save operation may exceed the cloud save quota and it has therefore been moved to local storage!");
                }
                SynchronizeAchievements();
                if (map == null || entry == null)
                {
                    // Not reachable through ProfileBlocker; kept equal to the game anyway.
                    if (map != null && worldUID != 0L)
                        s_wMapData.SetValue(entry ?? s_getWorldData.Invoke(profile, new object[] { worldUID }), BuildMapData(map));
                    profile.Save();
                    return;
                }
                BeginWrite(profile, map, entry, worldUID);
            }
            finally
            {
                if (mounted)
                    Unmount();
                SystemResourceManager.PopIsInLoadingScreen();
            }
        }

        static MapSnap CaptureMap(Minimap mm)
        {
            BitArray explored = s_mmExplored(mm), others = s_mmOthers(mm);
            var m = new MapSnap { TextureSize = mm.m_textureSize, Length = explored.Length };
            int words = (m.Length + 31) >> 5;
            lock (s_mapLock)
            {
                Grow(ref s_wordsA, words);
                Grow(ref s_wordsB, words);
                explored.CopyTo(s_wordsA, 0);
                others.CopyTo(s_wordsB, 0);
                m.Explored = s_wordsA;
                m.Others = s_wordsB;
            }
            foreach (Minimap.PinData pin in s_mmPins(mm))
                if (pin.m_save)
                    m.Pins.Add(new PinSnap
                    {
                        Name = pin.m_name, Pos = pin.m_pos, Type = (int)pin.m_type, Checked = pin.m_checked, Owner = pin.m_ownerID,
                        Author = s_pinAuthor.GetValue(pin).ToString()
                    });
            m.ReferencePublic = ZNet.instance.IsReferencePositionPublic();
            return m;
        }

        // PlayerProfile.SavePlayerToDisk up to the file write (main thread).
        static void BeginWrite(PlayerProfile p, MapSnap map, object entry, long worldUID)
        {
            PlayerProfile.SavingStarted?.Invoke();
            DateTime now = DateTime.Now;
            bool mounted = false;
            if (FileHelpers.CloudStorageSupported)
            {
                mounted = Mount();
                if (!mounted)
                    throw new InvalidOperationException("Failed to mount!");
            }
            var job = new ProfileJob { Profile = p, Entry = entry, Map = map, Filename = p.m_filename, Now = now };
            Exception ours = null;
            try
            {
                bool saveExceededCloudQuota = false;
                SaveSystem.PreSaveCloudChecksAndOperations(p.m_filename, SaveDataType.Character, ref p.m_fileSource, ref s_createBackup(p), ref saveExceededCloudQuota, 1);
                if (p.m_fileSource != FileHelpers.FileSource.Local)
                {
                    ours = new NotSupportedException("the profile moved to " + p.m_fileSource);
                }
                else
                {
                    string folder = SaveSystem.GetCharacterFolderPath(p.m_fileSource);
                    job.Path = folder + p.m_filename + ".fch";
                    job.OldPath = job.Path + ".old";
                    job.NewPath = job.Path + ".new";
                    job.Source = p.m_fileSource;
                    if (!Directory.Exists(folder) && p.m_fileSource.IsNotCloud())
                        Directory.CreateDirectory(folder);
                    if (s_verify.On)
                    {
                        long v0 = Stopwatch.GetTimestamp();
                        VerifyProfileReference(job, p, worldUID);
                        VerifyProfileRefMs = Ms(v0);
                        s_profileExcludedMs += VerifyProfileRefMs;
                    }
                    try
                    {
                        Serialize(p, entry, out job.Head, out job.Tail, job.Undo);
                    }
                    catch (Exception e)
                    {
                        ours = e;
                    }
                }
            }
            finally
            {
                if (mounted)
                    Unmount();
            }
            if (ours != null)
            {
                // Finish this save the game's way, synchronously, and stay on the game's path.
                s_profileFailed = true;
                ProfileErrors++;
                Report(2, "ProfileSaveAsync: " + ours.Message + "; this save and the rest of the session use the game's path.");
                s_wMapData.SetValue(entry, job.VanillaMap ?? MapBytesAnyWay(map));
                RaiseSavingFinished();
                UndoStats(p, job.Undo);
                p.Save();
                return;
            }
            // Published before the thread exists (a flush must find it); taken back if the thread cannot be started.
            s_job = job;
            try
            {
                StartWriter(job);
            }
            catch (Exception e)
            {
                s_job = null;
                job.Error = e;
                job.Done = true;
                job.Finished.Set();
                FinishSafe(job);   // the error path: the game's save, now
                return;
            }
            ProfileAsyncSaves++;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void StartWriter(ProfileJob job)
        {
            new Thread(() => WriteJob(job)) { IsBackground = true, Name = "RP profile save" }.Start();
        }

        // What Serialize changed in the profile (the stats increments and m_lastSaveLoad), so a save redone the game's way
        // after a failure starts from the state the game's own save would have started from (its file then carries the
        // stats as the game's would, the increments land after them, as in the game).
        sealed class StatsUndo
        {
            internal readonly List<(Dictionary<string, float> Dict, string Key, bool Existed, float Old)> Entries = new List<(Dictionary<string, float>, string, bool, float)>();
            internal bool HasLastSaveLoad;
            internal DateTime LastSaveLoad;
        }

        // The game's IncrementOrSet, the first touch of each (dictionary, key) recorded.
        static void Inc(StatsUndo undo, Dictionary<string, float> d, string key, int num)
        {
            if (undo != null)
            {
                bool seen = false;
                foreach ((Dictionary<string, float> Dict, string Key, bool Existed, float Old) e in undo.Entries)
                    if (ReferenceEquals(e.Dict, d) && e.Key == key)
                    {
                        seen = true;
                        break;
                    }
                if (!seen)
                    undo.Entries.Add((d, key, d.TryGetValue(key, out float old), old));
            }
            d.IncrementOrSet(key, num);
        }

        // Back to the state before Serialize: old values restored, added keys removed newest first (Dictionary's free
        // list is LIFO, so the game's re-adds land in the same slots: same enumeration order, same file).
        static void UndoStats(PlayerProfile p, StatsUndo undo)
        {
            if (undo == null)
                return;
            for (int i = undo.Entries.Count - 1; i >= 0; i--)
            {
                (Dictionary<string, float> d, string key, bool existed, float old) = undo.Entries[i];
                if (existed)
                    d[key] = old;
                else
                    d.Remove(key);
            }
            undo.Entries.Clear();
            if (undo.HasLastSaveLoad)
                s_lastSaveLoad(p) = undo.LastSaveLoad;
            undo.HasLastSaveLoad = false;
        }

        static void WriteDict(ZPackage pkg, Dictionary<string, float> d)
        {
            pkg.Write(d.Count);
            foreach (KeyValuePair<string, float> kv in d)
            {
                pkg.Write(kv.Key);
                pkg.Write(kv.Value);
            }
        }

        // The profile package of PlayerProfile.SavePlayerToDisk, write for write, in two parts: up to the current world's
        // "map data present" flag, and from after its map bytes. Also its two state changes, at the same points.
        static void Serialize(PlayerProfile p, object entry, out byte[] head, out byte[] tail, StatsUndo undo)
        {
            var a = new ZPackage();
            ZPackage cur = a, b = null;
            cur.Write(47);
            cur.Write(205);
            cur.Write(10);
            for (int i = 0; i < 10; i++)
            {
                PlayerProfile.PlayerStats st = p.m_playerStats[i];
                for (int j = 0; j < 205; j++)
                    cur.Write(st.m_stats[(PlayerStatType)j]);
                WriteDict(cur, st.m_knownWorlds);
                WriteDict(cur, st.m_knownWorldKeys);
                WriteDict(cur, st.m_knownCommands);
                cur.Write(5);
                for (int k = 0; k < 5; k++)
                    WriteDict(cur, st.m_enemyStats[k]);
                WriteDict(cur, st.m_itemPickupStats);
                WriteDict(cur, st.m_itemCraftStats);
                WriteDict(cur, st.m_pickableStats);
                WriteDict(cur, st.m_foodEatenStats);
                WriteDict(cur, st.m_piecesPlacedStats);
            }
            cur.Write(p.m_firstSpawn);
            var worlds = (IDictionary)s_worldData.GetValue(p);
            cur.Write(worlds.Count);
            foreach (DictionaryEntry de in worlds)
            {
                object w = de.Value;
                cur.Write((long)de.Key);
                cur.Write((bool)s_wHaveCustomSpawn.GetValue(w));
                cur.Write((Vector3)s_wSpawn.GetValue(w));
                cur.Write((bool)s_wHaveLogout.GetValue(w));
                cur.Write((Vector3)s_wLogout.GetValue(w));
                cur.Write((bool)s_wHaveDeath.GetValue(w));
                cur.Write((Vector3)s_wDeath.GetValue(w));
                cur.Write((Vector3)s_wHome.GetValue(w));
                if (ReferenceEquals(w, entry) && b == null)
                {
                    // SetMapData put the new (non-null) bytes here: flag, then Write(byte[]) = length + bytes (writer).
                    cur.Write(true);
                    b = new ZPackage();
                    cur = b;
                    continue;
                }
                byte[] md = (byte[])s_wMapData.GetValue(w);
                cur.Write(md != null);
                if (md != null)
                    cur.Write(md);
            }
            if (b == null)
                throw new InvalidOperationException("the current world's entry is not in the profile");
            cur.Write(s_playerName(p));
            cur.Write(s_playerID(p));
            cur.Write(s_startSeed(p));
            int num = (int)(DateTime.Now - s_lastSaveLoad(p)).TotalSeconds;
            if (undo != null)
            {
                undo.LastSaveLoad = s_lastSaveLoad(p);
                undo.HasLastSaveLoad = true;
            }
            s_lastSaveLoad(p) = DateTime.Now;
            cur.Write(p.m_usedCheats);
            cur.Write(new DateTimeOffset(p.m_dateCreated).ToUnixTimeSeconds());
            if ((bool)ZNet.instance && (bool)ZoneSystem.instance && ZNet.World != null)
            {
                int idx = Achievements.GetCurrentAchievementDifficultyIndex();
                Inc(undo, p.m_playerStats[0].m_knownWorlds, ZNet.instance.GetWorldName(), num);
                Inc(undo, p.m_playerStats[idx].m_knownWorlds, ZNet.instance.GetWorldName(), num);
                for (int l = 0; l < 53; l++)
                {
                    GlobalKeys g = (GlobalKeys)l;
                    if (ZoneSystem.instance.GetGlobalKey(g, out string value))
                    {
                        Inc(undo, p.m_playerStats[0].m_knownWorldKeys, g.ToString().ToLower() + " " + value, num);
                        Inc(undo, p.m_playerStats[idx].m_knownWorldKeys, g.ToString().ToLower() + " " + value, num);
                    }
                    else
                    {
                        Inc(undo, p.m_playerStats[0].m_knownWorldKeys, g.ToString().ToLower() + " default", num);
                        Inc(undo, p.m_playerStats[idx].m_knownWorldKeys, g.ToString().ToLower() + " default", num);
                    }
                }
            }
            byte[] playerData = s_playerData(p);
            if (playerData != null)
            {
                cur.Write(true);
                cur.Write(playerData);
            }
            else
            {
                cur.Write(false);
            }
            head = a.GetArray();
            tail = b.GetArray();
        }

        // head + Write(byte[] map) (int32 length + bytes) + tail: the profile package.
        static byte[] Assemble(byte[] head, byte[] map, byte[] tail)
        {
            var pkg = new byte[head.Length + 4 + map.Length + tail.Length];
            Buffer.BlockCopy(head, 0, pkg, 0, head.Length);
            int o = head.Length;
            PutInt(pkg, o, map.Length);
            Buffer.BlockCopy(map, 0, pkg, o + 4, map.Length);
            Buffer.BlockCopy(tail, 0, pkg, o + 4 + map.Length, tail.Length);
            return pkg;
        }

        static void PutInt(byte[] a, int at, int v)
        {
            a[at] = (byte)v;
            a[at + 1] = (byte)(v >> 8);
            a[at + 2] = (byte)(v >> 16);
            a[at + 3] = (byte)(v >> 24);
        }

        // Minimap.GetMapData from the snapshot (any thread; one at a time). The game: inner = new ZPackage(); Write(size);
        // N x Write(explored[i]); N x Write(others[i]); Write(pin count); pins; Write(public); outer.Write(8);
        // outer.WriteCompressed(inner) = Utils.Compress(inner.GetArray()) (one GZipStream(Fastest).Write of the whole
        // array), then Write(int length) + the bytes; outer.GetArray(). Here the inner array is built in place, in a pooled
        // buffer of its exact size.
        static byte[] BuildMapData(MapSnap m)
        {
            var pins = new ZPackage();
            pins.Write(m.Pins.Count);
            foreach (PinSnap pin in m.Pins)
            {
                pins.Write(pin.Name);
                pins.Write(pin.Pos);
                pins.Write(pin.Type);
                pins.Write(pin.Checked);
                pins.Write(pin.Owner);
                pins.Write(pin.Author);
            }
            pins.Write(m.ReferencePublic);
            byte[] pinBytes = pins.GetArray();
            int n = m.Length;
            long size = 4L + 2L * n + pinBytes.Length;
            if (size > int.MaxValue)
                throw new InvalidOperationException("map data over 2 GB");
            int len = (int)size;
            byte[] result;
            lock (s_mapLock)
            {
                byte[] buf = s_mapBuf.Length >= len ? s_mapBuf : (s_mapBuf = new byte[len]);
                PutInt(buf, 0, m.TextureSize);
                Expand(m.Explored, n, buf, 4);
                Expand(m.Others, n, buf, 4 + n);
                Buffer.BlockCopy(pinBytes, 0, buf, 4 + 2 * n, pinBytes.Length);
                MemoryStream z = s_mapZ ?? (s_mapZ = new MemoryStream());
                z.SetLength(0);
                using (var gz = new GZipStream(z, System.IO.Compression.CompressionLevel.Fastest, true))
                    gz.Write(buf, 0, len);
                int c = checked((int)z.Length);
                result = new byte[8 + c];
                PutInt(result, 0, 8);
                PutInt(result, 4, c);
                Buffer.BlockCopy(z.GetBuffer(), 0, result, 8, c);
            }
            ZLog.Log("Minimap: compressed mapData " + len + " => " + result.Length + " bytes");
            return result;
        }

        // Error paths (main thread): the snapshot's bytes, or if even that fails the game's own GetMapData of the minimap
        // as it is now, so the game's save that follows still carries a map.
        static byte[] MapBytesAnyWay(MapSnap m)
        {
            try
            {
                return BuildMapData(m);
            }
            catch (Exception e)
            {
                Report(2, "ProfileSaveAsync: map bytes from the snapshot failed (" + e.Message + "), taking the game's.");
                return (byte[])s_mmGetMapData.Invoke(Minimap.instance, null);
            }
        }

        // BitArray bit i is bit (i % 32) of word i / 32; BinaryWriter.Write(bool) writes 1 or 0.
        static void Expand(int[] words, int n, byte[] bits, int at)
        {
            int full = n >> 5;
            for (int k = 0; k < full; k++)
            {
                int v = words[k], o = at + (k << 5);
                for (int b = 0; b < 32; b++)
                    bits[o + b] = (byte)((v >> b) & 1);
            }
            for (int i = full << 5; i < n; i++)
                bits[at + i] = (byte)((words[i >> 5] >> (i & 31)) & 1);
        }

        // ---- writer thread --------------------------------------------------------------------------------------------

        static void WriteJob(ProfileJob j)
        {
            long t0 = Stopwatch.GetTimestamp();
            bool created = false;
            try
            {
                byte[] map = BuildMapData(j.Map);
                j.MapBytes = map;
                byte[] pkg = Assemble(j.Head, map, j.Tail);
                byte[] hash;
                using (SHA512 sha = SHA512.Create())
                    hash = sha.ComputeHash(pkg);
                if (j.VanillaFile != null || j.VanillaMap != null)
                {
                    // SaveVerify: compared before anything is written; a difference leaves the save to the game.
                    byte[] game = j.VanillaFile != null && File.Exists(j.VanillaFile) ? File.ReadAllBytes(j.VanillaFile) : null;
                    j.VerifyBad = CompareProfile(j.Filename, pkg, hash, map, j.VanillaMap, j.VanillaFile != null, game, j.VerifyDir, false, out j.VerifyResult);
                    if (j.VerifyBad)
                        throw new InvalidDataException("SaveVerify: the module's profile differs from the game's (not written)");
                }
                created = true;
                var fw = (FileWriter)s_fileWriterCtor.Invoke(new object[] { j.NewPath, s_sameFileEnding, FileHelpers.FileHelperType.Binary, j.Source });
                fw.m_binary.Write(pkg.Length);
                fw.m_binary.Write(pkg);
                fw.m_binary.Write(hash.Length);
                fw.m_binary.Write(hash);
                fw.Finish();
                if (fw.Status != FileWriter.WriterStatus.CloseSucceeded)
                    throw new IOException("writing " + j.NewPath + ": " + fw.Status);
                // A complete .new from here on: if the swap fails half way (.fch already moved to .old), it is the newest
                // save on disk and stays, as the game would leave it.
                created = false;
                s_replaceOldFile.Invoke(null, new object[] { j.Path, j.NewPath, j.OldPath, s_sameFileEnding, j.Source });
                j.FileBytes = 8L + pkg.Length + hash.Length;
            }
            catch (Exception e)
            {
                j.Error = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                if (created)
                    try
                    {
                        if (File.Exists(j.NewPath))
                            File.Delete(j.NewPath);
                    }
                    catch
                    {
                        // The game leaves a partial .new too; the next save overwrites it.
                    }
            }
            finally
            {
                j.WorkerMs = Ms(t0);
                j.Done = true;
                j.Finished.Set();
            }
        }

        // ---- finishing on the main thread ---------------------------------------------------------------------------

        static void AfterGameUpdate()
        {
            ProfileJob j = s_job;
            if (j == null || !j.Done)
                return;
            // The game ends a profile save before its autosave's world save starts; keep it that way (both mount and
            // unmount the save depot through FileHelpers' unsynchronized counter, and both reload SaveSystem caches).
            if (SaveThreadAlive())
                return;
            s_job = null;
            FinishSafe(j);
        }

        static void FlushPrefix() => Flush();

        // Lab / other modules: finish a pending profile write now.
        public static void FlushProfileSave() => Flush();

        static void Flush()
        {
            ProfileJob j = s_job;
            if (j == null)
                return;
            if (Thread.CurrentThread.ManagedThreadId != s_mainThreadId)
            {
                // Not the main thread: only wait for the file; the main thread finishes it.
                j.Finished.Wait();
                return;
            }
            if (!j.Done)
            {
                long t0 = Stopwatch.GetTimestamp();
                j.Finished.Wait();
                ProfileFlushWaits++;
                double ms = Ms(t0);
                if (ms > MaxFlushWaitMs)
                    MaxFlushWaitMs = ms;
            }
            s_job = null;
            FinishSafe(j);
        }

        // Finish, timed, never throwing: it runs inside flush points (Game.Shutdown, Game.OnDestroy, profile methods)
        // whose own work must go on.
        static void FinishSafe(ProfileJob j)
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                Finish(j);
            }
            catch (Exception e)
            {
                s_profileFailed = true;
                ProfileErrors++;
                Report(2, "ProfileSaveAsync: finishing the profile save failed (" + e + "); the rest of the session uses the game's path.");
            }
            finally
            {
                LastProfileFinishMs = Ms(t0);
                if (LastProfileFinishMs > MaxProfileFinishMs)
                    MaxProfileFinishMs = LastProfileFinishMs;
            }
        }

        // The rest of SavePlayerToDisk after the write, and SaveMapData's effect on the profile.
        static void Finish(ProfileJob j)
        {
            LastProfileWorkerMs = j.WorkerMs;
            if (j.VerifyResult != null)
            {
                LastVerify = j.VerifyResult;
                Report(j.VerifyBad ? 2 : 0, "SaveVerify: " + j.VerifyResult);
            }
            if (j.Error == null)
            {
                s_wMapData.SetValue(j.Entry, j.MapBytes);
                LastProfileBytes = j.FileBytes;
                try
                {
                    SaveSystem.InvalidateCache(SaveDataType.Character);
                    ZNet.ConsiderAutoBackup(j.Filename, SaveDataType.Character, j.Now);
                }
                finally
                {
                    RaiseSavingFinished();
                }
                return;
            }
            s_profileFailed = true;
            if (!j.VerifyBad)
                ProfileErrors++;
            Report(2, (j.VerifyBad ? "ProfileSaveAsync: SaveVerify found a difference, the module's file was not written"
                : $"ProfileSaveAsync: writing {j.Path} failed ({j.Error.Message})") + "; saving it the game's way now, and the rest of the session too.");
            RaiseSavingFinished();
            try
            {
                // The game's map bytes of the save point when SaveVerify made them, else the module's.
                s_wMapData.SetValue(j.Entry, j.VanillaMap ?? j.MapBytes ?? MapBytesAnyWay(j.Map));
                UndoStats(j.Profile, j.Undo);
                j.Profile.Save();
            }
            catch (Exception e)
            {
                Report(2, "ProfileSaveAsync: the game's save failed too: " + e);
            }
        }

        static void RaiseSavingFinished()
        {
            try
            {
                PlayerProfile.SavingFinished?.Invoke();
            }
            catch (Exception e)
            {
                Report(2, "ProfileSaveAsync: a PlayerProfile.SavingFinished handler threw: " + e.Message);
            }
        }

        // ---- SaveVerify: profile ---------------------------------------------------------------------------------------

        // The game's GetMapData now (main thread, same frame as the snapshot), and the game's SavePlayerToDisk on a copy
        // of the profile into <save data>/rp_saveverify/profile/.
        static void VerifyProfileReference(ProfileJob j, PlayerProfile p, long worldUID)
        {
            try
            {
                j.VanillaMap = (byte[])s_mmGetMapData.Invoke(Minimap.instance, null);
                j.VerifyDir = VerifyRoot() + "/profile/";
                ResetDir(j.VerifyDir);
                PlayerProfile copy = CopyProfile(p, "../rp_saveverify/profile/" + p.m_filename, worldUID, j.VanillaMap);
                copy.Save();
                j.VanillaFile = j.VerifyDir + p.m_filename + ".fch";
            }
            catch (Exception e)
            {
                j.VanillaFile = null;
                Report(1, "SaveVerify: reference profile save failed: " + (e.InnerException ?? e).Message);
            }
        }

        // Shadow: the minimap as SavePlayerProfile starts (its SaveMapData reads the same state a few calls later).
        static void ArmProfileShadow(PlayerProfile profile)
        {
            try
            {
                s_shadow = new ProfileShadow { Profile = profile, Map = CaptureMap(Minimap.instance), WorldUID = ZNet.instance.GetWorldUID() };
                ProfileShadows++;
            }
            catch (Exception e)
            {
                s_shadow = null;
                Report(1, "SaveVerify (shadow): minimap snapshot failed: " + e.Message);
            }
        }

        static void BeforeSavePlayerToDisk(PlayerProfile __instance)
        {
            Flush();
            ProfileShadow sh = s_shadow;
            if (sh == null || !ReferenceEquals(sh.Profile, __instance) || sh.Pkg != null)
                return;
            // The game has run Player.Save, SaveMapData and SaveLogoutPoint; its SavePlayerToDisk is next. The module's
            // package of the same state, from a copy of the profile (its stats increments land on the copy).
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                object entry = s_getWorldData.Invoke(__instance, new object[] { sh.WorldUID });
                sh.GameMap = (byte[])s_wMapData.GetValue(entry);
                sh.OurMap = BuildMapData(sh.Map);
                PlayerProfile copy = CopyProfile(__instance, __instance.m_filename, sh.WorldUID, sh.GameMap);
                object copyEntry = s_getWorldData.Invoke(copy, new object[] { sh.WorldUID });
                Serialize(copy, copyEntry, out byte[] head, out byte[] tail, null);
                sh.Pkg = Assemble(head, sh.OurMap, tail);
                using (SHA512 sha = SHA512.Create())
                    sh.Hash = sha.ComputeHash(sh.Pkg);
            }
            catch (Exception e)
            {
                s_shadow = null;
                Report(1, "SaveVerify (shadow): the module's profile copy failed: " + (e.InnerException ?? e).Message);
            }
            finally
            {
                VerifyProfileRefMs = Ms(t0);
            }
        }

        static void AfterSavePlayerToDisk(PlayerProfile __instance, bool __result)
        {
            ProfileShadow sh = s_shadow;
            if (sh == null || !ReferenceEquals(sh.Profile, __instance) || sh.Pkg == null)
                return;
            s_shadow = null;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                string path = SaveSystem.GetCharacterFolderPath(__instance.m_fileSource) + __instance.m_filename + ".fch";
                byte[] game = __result && File.Exists(path) ? File.ReadAllBytes(path) : null;
                bool bad = CompareProfile(__instance.m_filename, sh.Pkg, sh.Hash, sh.OurMap, sh.GameMap, true, game, VerifyRoot() + "/profile/", true, out string result);
                LastVerify = result;
                if (bad)
                {
                    s_profileFailed = true;
                    Report(2, "SaveVerify (shadow): " + result + ". The save itself is the game's; the shadow stops for the session.");
                }
                else
                {
                    Report(0, "SaveVerify (shadow): " + result);
                }
            }
            catch (Exception e)
            {
                Report(1, "SaveVerify (shadow): profile compare failed: " + e.Message);
            }
            finally
            {
                VerifyProfileRefMs += Ms(t0);
            }
        }

        static PlayerProfile CopyProfile(PlayerProfile p, string filename, long worldUID, byte[] map)
        {
            var c = new PlayerProfile(filename, FileHelpers.FileSource.Local);
            foreach (FieldInfo f in typeof(PlayerProfile).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                switch (f.Name)
                {
                    case "m_filename":
                    case "m_fileSource":
                        continue;
                    case "m_createBackupBeforeSaving":
                        f.SetValue(c, false);
                        continue;
                    case "m_playerStats":
                        for (int i = 0; i < p.m_playerStats.Length; i++)
                            c.m_playerStats[i] = (PlayerProfile.PlayerStats)CopyObject(p.m_playerStats[i]);
                        continue;
                    case "m_worldData":
                    {
                        var src = (IDictionary)f.GetValue(p);
                        var dst = (IDictionary)f.GetValue(c);
                        dst.Clear();
                        foreach (DictionaryEntry de in src)
                        {
                            object w = s_memberwiseClone.Invoke(de.Value, null);
                            if ((long)de.Key == worldUID)
                                s_wMapData.SetValue(w, map);
                            dst.Add(de.Key, w);
                        }
                        continue;
                    }
                    default:
                        f.SetValue(c, f.GetValue(p));
                        continue;
                }
            }
            return c;
        }

        // A PlayerStats with every dictionary copied in enumeration order (so the copy enumerates the same way).
        static object CopyObject(object o)
        {
            object c = s_memberwiseClone.Invoke(o, null);
            foreach (FieldInfo f in o.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                f.SetValue(c, CopyValue(f.GetValue(o)));
            return c;
        }

        static object CopyValue(object v)
        {
            if (v is IDictionary d)
            {
                var n = (IDictionary)Activator.CreateInstance(v.GetType());
                foreach (DictionaryEntry e in d)
                    n.Add(e.Key, e.Value);
                return n;
            }
            if (v is Array arr && !(v is byte[]))
            {
                var n = (Array)arr.Clone();
                for (int i = 0; i < n.Length; i++)
                    n.SetValue(CopyValue(arr.GetValue(i)), i);
                return n;
            }
            return v;
        }

        // The module's profile file ([pkg.Length][pkg][hash.Length][hash]) and map bytes against the game's. Counts, and on
        // a difference keeps the module's files (and the game's map) in keepDir; on a match of a reference save
        // (not shadow) deletes keepDir. True when different.
        static bool CompareProfile(string name, byte[] pkg, byte[] hash, byte[] map, byte[] gameMap, bool fileExpected, byte[] gameFile, string keepDir, bool shadow,
            out string result)
        {
            var notes = new List<string>();
            bool bad = false;
            if (gameMap != null)
            {
                int at = FirstDifference(map, 0, map.Length, gameMap);
                if (at >= 0 || gameMap.Length != map.Length)
                {
                    bad = true;
                    Interlocked.Increment(ref VerifyMapMismatches);
                    notes.Add($"map bytes {map.Length} vs game {gameMap.Length}, first difference at {Math.Max(at, 0)}");
                }
            }
            long ours = 8L + pkg.Length + hash.Length;
            if (fileExpected)
            {
                int at = FileDifference(gameFile, pkg, hash);
                if (at >= 0)
                {
                    bad = true;
                    notes.Add(gameFile == null ? "the game's file is missing" : $"profile file {ours} vs game {gameFile.Length} bytes, first difference at {at}");
                }
                Interlocked.Increment(ref VerifyProfileRuns);
            }
            string label = shadow ? "profile " + name + " (shadow)" : "profile " + name;
            if (bad)
            {
                Interlocked.Increment(ref VerifyProfileMismatches);
                try
                {
                    Directory.CreateDirectory(keepDir);
                    using (var f = new FileStream(keepDir + name + ".ours.fch", FileMode.Create, FileAccess.Write))
                    using (var w = new BinaryWriter(f))
                    {
                        w.Write(pkg.Length);
                        w.Write(pkg);
                        w.Write(hash.Length);
                        w.Write(hash);
                    }
                    File.WriteAllBytes(keepDir + name + ".ours.map", map);
                    if (gameMap != null)
                        File.WriteAllBytes(keepDir + name + ".game.map", gameMap);
                }
                catch (Exception e)
                {
                    notes.Add("keeping files failed: " + e.Message);
                }
                result = $"{label}: DIFFERENT ({string.Join("; ", notes)}); files kept in {keepDir}";
                return true;
            }
            result = $"{label}: {ours} bytes, map {map.Length} bytes, byte-identical to the game's";
            if (!shadow)
                try
                {
                    Directory.Delete(keepDir, true);
                }
                catch
                {
                    // Left for the next run's ResetDir.
                }
            return false;
        }

        // Offset of the first byte where the game's file differs from [pkg.Length][pkg][hash.Length][hash], or -1.
        static int FileDifference(byte[] v, byte[] pkg, byte[] hash)
        {
            if (v == null)
                return 0;
            for (int k = 0; k < 4; k++)
                if (k >= v.Length || v[k] != (byte)(pkg.Length >> (8 * k)))
                    return k;
            int d = FirstDifference(pkg, 0, pkg.Length, v, 4);
            if (d >= 0)
                return 4 + d;
            int h = 4 + pkg.Length;
            for (int k = 0; k < 4; k++)
                if (h + k >= v.Length || v[h + k] != (byte)(hash.Length >> (8 * k)))
                    return h + k;
            d = FirstDifference(hash, 0, hash.Length, v, h + 4);
            if (d >= 0)
                return h + 4 + d;
            long ours = 8L + pkg.Length + hash.Length;
            return v.Length != ours ? (int)Math.Min(v.Length, ours) : -1;
        }

        // ---- Splatform-typed calls by reflection ------------------------------------------------------------------------

        static bool Mount() => (bool)s_mount.Invoke(null, new[] { s_readWrite });

        static void Unmount() => s_unmount.Invoke(null, new[] { s_unmountDefault });

        // PlatformManager.DistributionPlatform?.AchievementManager?.Synchronize()
        static void SynchronizeAchievements()
        {
            object platform = s_distributionPlatform.GetValue(null);
            if (platform == null)
                return;
            PropertyInfo am = FindProperty(platform.GetType(), "AchievementManager");
            object manager = am?.GetValue(platform);
            if (manager == null)
                return;
            MethodInfo sync = FindMethod(manager.GetType(), "Synchronize");
            sync?.Invoke(manager, null);
        }

        static PropertyInfo FindProperty(Type t, string name)
        {
            PropertyInfo p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (p != null)
                return p;
            foreach (Type i in t.GetInterfaces())
                if ((p = i.GetProperty(name)) != null)
                    return p;
            return null;
        }

        static MethodInfo FindMethod(Type t, string name)
        {
            MethodInfo m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (m != null)
                return m;
            foreach (Type i in t.GetInterfaces())
                if ((m = i.GetMethod(name, Type.EmptyTypes)) != null)
                    return m;
            return null;
        }

        // =================================================================================================================
        // Shared
        // =================================================================================================================

        // "owner on Type.Method" of the first Harmony patch on these methods that is neither the engine's nor the lab's,
        // or null.
        static string ForeignPatch(MethodBase[] methods)
        {
            try
            {
                foreach (MethodBase m in methods)
                {
                    HarmonyLib.Patches info = Harmony.GetPatchInfo(m);
                    if (info == null)
                        continue;
                    string owner = Foreign(info.Prefixes) ?? Foreign(info.Postfixes) ?? Foreign(info.Transpilers) ?? Foreign(info.Finalizers) ?? Foreign(info.ILManipulators);
                    if (owner != null)
                        return owner + " on " + m.DeclaringType?.Name + "." + m.Name;
                }
                return null;
            }
            catch (Exception e)
            {
                return "unreadable patch info (" + e.Message + ")";
            }
        }

        static string Foreign(IEnumerable<HarmonyLib.Patch> patches)
        {
            if (patches == null)
                return null;
            foreach (HarmonyLib.Patch p in patches)
            {
                if (p.owner == EnginePlugin.Guid || p.owner != null && p.owner.StartsWith(LabOwners, StringComparison.Ordinal))
                    continue;
                return p.owner ?? "?";
            }
            return null;
        }

        // ---- MonoMod hooks -----------------------------------------------------------------------------------------------
        // Harmony.GetPatchInfo sees Harmony patches only. MonoMod's own hooks (MMHOOK's On./IL. through HookEndpointManager,
        // or Hook / ILHook / Detour made directly) are registered in MonoMod.RuntimeDetour's static maps: Detour._DetourMap
        // (method -> its detours; an On. hook is a Hook, whose Detour is there) and ILHook._Map (method -> context with the
        // IL hook chain and the one Detour that applies it). HarmonyX applies its patches as one ILHook per method whose
        // manipulator is in its own assembly (the 0Harmony the engine uses); that hook and its context's Detour are
        // Harmony's (judged by owner above). Anything else applied on these methods is another mod's (a second Harmony
        // copy included). A MonoMod build whose maps are not found counts as foreign (stand down). Not visible: NativeDetour (no registry) and other raw patches of the methods' code.
        sealed class DetourRegistry
        {
            internal IDictionary DetourMap, IlMap;
            internal FieldInfo CtxChain, CtxDetour, IlManipulator;
            internal PropertyInfo IlApplied, DetourApplied;
            internal FieldInfo DetourTarget;
        }

        static readonly Dictionary<Assembly, DetourRegistry> s_registries = new Dictionary<Assembly, DetourRegistry>();

        static DetourRegistry Registry(Assembly a)
        {
            if (s_registries.TryGetValue(a, out DetourRegistry r))
                return r;
            r = null;
            try
            {
                Type detour = a.GetType("MonoMod.RuntimeDetour.Detour"), il = a.GetType("MonoMod.RuntimeDetour.ILHook");
                Type ctx = il?.GetNestedType("Context", BindingFlags.NonPublic | BindingFlags.Public);
                const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                const BindingFlags I = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var x = new DetourRegistry
                {
                    DetourMap = detour?.GetField("_DetourMap", S)?.GetValue(null) as IDictionary,
                    IlMap = il?.GetField("_Map", S)?.GetValue(null) as IDictionary,
                    CtxChain = ctx?.GetField("Chain", I),
                    CtxDetour = ctx?.GetField("Detour", I),
                    IlManipulator = il?.GetField("Manipulator", I),
                    IlApplied = il?.GetProperty("IsApplied", I),
                    DetourApplied = detour?.GetProperty("IsApplied", I),
                    DetourTarget = detour?.GetField("Target", I)
                };
                if (x.DetourMap != null && x.IlMap != null && x.CtxChain != null && x.CtxDetour != null && x.IlManipulator != null && x.IlApplied != null
                    && x.DetourApplied != null && x.DetourTarget != null)
                    r = x;
            }
            catch
            {
                r = null;
            }
            s_registries[a] = r;
            return r;
        }

        // "a MonoMod ... on Type.Method" for the first non-Harmony MonoMod hook applied on these methods, or null.
        static string ForeignDetour(MethodBase[] methods)
        {
            try
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!a.FullName.StartsWith("MonoMod.RuntimeDetour,", StringComparison.Ordinal))
                        continue;
                    DetourRegistry r = Registry(a);
                    if (r == null)
                        return "MonoMod hooks (unreadable hook registry of " + a.GetName().Name + " " + a.GetName().Version + ")";
                    string f = ForeignDetour(r, methods);
                    if (f != null)
                        return f;
                }
                return null;
            }
            catch (Exception e)
            {
                return "MonoMod hooks (unreadable: " + e.Message + ")";
            }
        }

        static string ForeignDetour(DetourRegistry r, MethodBase[] methods)
        {
            var ilDetours = new List<object>();
            foreach (KeyValuePair<MethodBase, object> e in Entries(r.IlMap))
            {
                MethodBase m = Match(e.Key, methods);
                if (m == null)
                    continue;
                object detour = r.CtxDetour.GetValue(e.Value);
                if (detour != null)
                    ilDetours.Add(detour);
                foreach (object hook in Items(r.CtxChain.GetValue(e.Value) as IList))
                {
                    if (hook == null || !(bool)r.IlApplied.GetValue(hook))
                        continue;
                    var manipulator = r.IlManipulator.GetValue(hook) as Delegate;
                    Type owner = manipulator?.Method.DeclaringType;
                    if (owner != null && owner.Assembly == typeof(Harmony).Assembly)
                        continue;   // HarmonyX's own patcher: its patches were judged by owner
                    return $"a MonoMod IL hook ({owner?.FullName ?? "?"}.{manipulator?.Method.Name}, {owner?.Assembly.GetName().Name}) on {m.DeclaringType?.Name}.{m.Name}";
                }
            }
            foreach (KeyValuePair<MethodBase, object> e in Entries(r.DetourMap))
            {
                MethodBase m = Match(e.Key, methods);
                if (m == null)
                    continue;
                foreach (object d in Items(e.Value as IList))
                {
                    if (d == null || ilDetours.Contains(d) || !(bool)r.DetourApplied.GetValue(d))
                        continue;
                    var target = r.DetourTarget.GetValue(d) as MethodBase;
                    return $"a MonoMod detour / On. hook (to {target?.DeclaringType?.FullName ?? "?"}.{target?.Name}) on {m.DeclaringType?.Name}.{m.Name}";
                }
            }
            return null;
        }

        // A copy of a MonoMod map, taken under its own lock (MonoMod locks the map, then separately each list: one lock
        // at a time here too).
        static List<KeyValuePair<MethodBase, object>> Entries(IDictionary map)
        {
            var list = new List<KeyValuePair<MethodBase, object>>();
            lock (map)
            {
                foreach (DictionaryEntry e in map)
                    if (e.Key is MethodBase k)
                        list.Add(new KeyValuePair<MethodBase, object>(k, e.Value));
            }
            return list;
        }

        static object[] Items(IList list)
        {
            if (list == null)
                return Array.Empty<object>();
            lock (list)
            {
                var a = new object[list.Count];
                list.CopyTo(a, 0);
                return a;
            }
        }

        static MethodBase Match(MethodBase key, MethodBase[] methods)
        {
            foreach (MethodBase m in methods)
            {
                if (ReferenceEquals(key, m))
                    return m;
                try
                {
                    if (key.Name == m.Name && key.DeclaringType == m.DeclaringType && key.MetadataToken == m.MetadataToken && key.Module == m.Module)
                        return m;
                }
                catch
                {
                    // A dynamic method key: not one of the game's.
                }
            }
            return null;
        }

        static void SetState(ref string state, string now, string feature)
        {
            if (state == now)
                return;
            state = now;
            Report(0, feature + ": " + now);
        }

        // 0 info, 1 warning, 2 error. BepInEx's log on the main thread; the game's (thread-safe) ZLog elsewhere.
        static void Report(int level, string text)
        {
            if (Thread.CurrentThread.ManagedThreadId == s_mainThreadId && EnginePlugin.Log != null)
            {
                if (level == 0)
                    EnginePlugin.Log.LogInfo(text);
                else if (level == 1)
                    EnginePlugin.Log.LogWarning(text);
                else
                    EnginePlugin.Log.LogError(text);
                return;
            }
            text = "[Real Portal Engine] " + text;
            if (level == 0)
                ZLog.Log(text);
            else if (level == 1)
                ZLog.LogWarning(text);
            else
                ZLog.LogError(text);
        }

        static double Ms(long since) => (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;

        public static string Describe() =>
            $"SaveSnapshot {(s_worldReady ? s_worldState : "not installed")}: {WorldSnapshots} snapshots, {WorldShadows} shadow copies, {WorldFallbacks} fallbacks ({LastWorldFallback}), {WorldErrors} errors; " +
            $"last: {LastZdos} ZDOs in {LastChunks} chunks, {LastBytes} bytes, copy {LastCaptureMs:0.0} ms on {LastThreads} threads, PrepareSave {LastPrepareMs:0.0} ms (max {MaxPrepareMs:0.0}, verify work {VerifyWorldRefMs:0.0} ms not in it); " +
            $"served {ServeCalls}, cursor misses {ServeMisses}. " +
            $"ProfileSaveAsync {(s_profileReady ? s_profileState : "not installed")}: {ProfileAsyncSaves} async, {ProfileShadows} shadow, {ProfileFallbacks} fallbacks ({LastProfileFallback}), {ProfileErrors} errors; " +
            $"last main {LastProfileMainMs:0.0} + finish {LastProfileFinishMs:0.0} = {LastProfileMainMs + LastProfileFinishMs:0.0} ms (max {MaxProfileMainMs:0.0} + {MaxProfileFinishMs:0.0}; verify work {VerifyProfileRefMs:0.0} ms not in it), writer {LastProfileWorkerMs:0.0} ms, {LastProfileBytes} bytes; flush waits {ProfileFlushWaits} (max {MaxFlushWaitMs:0.0} ms). " +
            $"SaveVerify: world {VerifyWorldRuns} runs, {VerifyWorldFiles} files, {VerifyWorldMismatches} different; profile {VerifyProfileRuns} runs, {VerifyProfileMismatches} different, map {VerifyMapMismatches} different; last: {LastVerify}";
    }
}
