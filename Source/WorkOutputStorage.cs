using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Lilly.CheatWork
{
    internal static class WorkOutputStorage
    {
        [ThreadStatic]
        private static Capture currentCapture;

        [ThreadStatic]
        private static bool storingOutputs;

        [ThreadStatic]
        private static Capture reusableRootCapture;

        [ThreadStatic]
        private static CaptureScope reusableRootScope;

        internal sealed class CaptureScope : IDisposable
        {
            private readonly Capture capture;
            private bool disposed;

            internal CaptureScope(Capture capture)
            {
                this.capture = capture;
            }

            internal void Activate()
            {
                disposed = false;
            }

            public void Dispose()
            {
                if (disposed || capture == null)
                    return;

                disposed = true;
                currentCapture = capture.Parent;
                try
                {
                    StoreCapturedOutputs(capture);
                }
                finally
                {
                    capture.Outputs.Clear();
                    capture.Seen.Clear();
                    if (capture == reusableRootCapture)
                        capture.Map = null;
                }
            }
        }

        internal sealed class Capture
        {
            internal Map Map;
            internal readonly Capture Parent;
            internal readonly List<Thing> Outputs = new List<Thing>();
            internal readonly HashSet<Thing> Seen = new HashSet<Thing>();

            internal Capture(Map map, Capture parent)
            {
                Map = map;
                Parent = parent;
            }
        }

        internal static CaptureScope Begin(Pawn pawn)
        {
            if (pawn == null || pawn.Faction != Faction.OfPlayer)
                return null;

            return Begin(pawn.Map);
        }

        internal static CaptureScope Begin(Map map)
        {
            if (!CheatWorkMod.AutoStoreWorkOutputs || map == null)
                return null;

            if (currentCapture != null && currentCapture.Map == map)
                return null;

            Capture capture;
            CaptureScope scope;
            if (currentCapture == null)
            {
                if (reusableRootCapture == null)
                {
                    reusableRootCapture = new Capture(map, null);
                    reusableRootScope = new CaptureScope(reusableRootCapture);
                }

                capture = reusableRootCapture;
                capture.Map = map;
                reusableRootScope.Activate();
                scope = reusableRootScope;
            }
            else
            {
                capture = new Capture(map, currentCapture);
                scope = new CaptureScope(capture);
            }

            currentCapture = capture;
            return scope;
        }

        internal static bool IsCapturing
        {
            get { return currentCapture != null && !storingOutputs; }
        }

        internal static void RecordSpawnedThing(Thing thing, Map map, bool respawningAfterLoad)
        {
            if (storingOutputs || respawningAfterLoad || currentCapture == null ||
                thing == null || map != currentCapture.Map ||
                thing.def.category != ThingCategory.Item || !thing.def.EverHaulable)
                return;

            if (currentCapture.Seen.Add(thing))
                currentCapture.Outputs.Add(thing);
        }

        private static void StoreCapturedOutputs(Capture capture)
        {
            if (capture.Outputs.Count == 0)
                return;

            bool wasStoringOutputs = storingOutputs;
            storingOutputs = true;
            try
            {
                for (int i = 0; i < capture.Outputs.Count; i++)
                    StoreOutput(capture.Outputs[i], capture.Map);
            }
            finally
            {
                storingOutputs = wasStoringOutputs;
            }
        }

        private static void StoreOutput(Thing thing, Map map)
        {
            if (thing == null || thing.Destroyed || !thing.Spawned || thing.Map != map)
                return;

            IntVec3 originalCell = thing.Position;
            bool despawned = false;
            try
            {
                if (StoreUtility.IsGoodStoreCell(
                        originalCell, map, thing, null, Faction.OfPlayer))
                    return;

                IntVec3 storageCell;
                if (!StoreUtility.TryFindBestBetterStoreCellFor(
                        thing,
                        null,
                        map,
                        StoragePriority.Unstored,
                        Faction.OfPlayer,
                        out storageCell,
                        true) ||
                    storageCell == originalCell)
                    return;

                thing.DeSpawn();
                despawned = true;
                Thing resultingThing;
                bool placed = GenPlace.TryPlaceThing(
                    thing,
                    storageCell,
                    map,
                    ThingPlaceMode.Direct,
                    out resultingThing);
                if (placed || thing.Destroyed || thing.Spawned)
                    return;

                GenSpawn.Spawn(thing, originalCell, map, WipeMode.Vanish);
                despawned = false;
            }
            catch (Exception exception)
            {
                if (despawned && !thing.Destroyed && !thing.Spawned)
                {
                    try
                    {
                        GenSpawn.Spawn(thing, originalCell, map, WipeMode.Vanish);
                    }
                    catch (Exception restoreException)
                    {
                        Log.Error("[Cheat Work] Could not restore work output " +
                            thing.def.defName + " to its original cell: " + restoreException);
                    }
                }

                Log.Error("[Cheat Work] Failed to store work output " +
                    thing.def.defName + ": " + exception);
            }
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.SpawnSetup), new[] { typeof(Map), typeof(bool) })]
    internal static class Thing_SpawnSetup_WorkOutputStorage_Patch
    {
        private static void Postfix(Thing __instance, Map map, bool respawningAfterLoad)
        {
            WorkOutputStorage.RecordSpawnedThing(__instance, map, respawningAfterLoad);
        }
    }

    [HarmonyPatch]
    internal static class GenPlace_TryPlaceThing_WorkOutputStorage_Patch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return AccessTools.GetDeclaredMethods(typeof(GenPlace))
                .Where(method =>
                {
                    if (method.Name != nameof(GenPlace.TryPlaceThing) ||
                        method.ReturnType != typeof(bool))
                        return false;

                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 9 &&
                        parameters[0].ParameterType == typeof(Thing) &&
                        parameters[2].ParameterType == typeof(Map) &&
                        parameters[4].ParameterType == typeof(Thing).MakeByRefType();
                });
        }

        private static void Postfix(object[] __args, bool __result)
        {
            if (!__result || !WorkOutputStorage.IsCapturing)
                return;

            Thing placedThing = __args[4] as Thing ?? __args[0] as Thing;
            WorkOutputStorage.RecordSpawnedThing(placedThing, __args[2] as Map, false);
        }
    }

    [HarmonyPatch]
    internal static class JobDriver_WorkOutputStorage_Patch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return AccessTools.GetDeclaredMethods(typeof(JobDriver))
                .Where(method =>
                    method.ReturnType == typeof(void) &&
                    (method.Name == "DriverTick" ||
                     (method.Name == "DriverTickInterval" &&
                      method.GetParameters().Length == 1 &&
                      method.GetParameters()[0].ParameterType == typeof(int))));
        }

        private static void Prefix(Pawn ___pawn, out WorkOutputStorage.CaptureScope __state)
        {
            __state = WorkOutputStorage.Begin(___pawn);
        }

        private static Exception Finalizer(
            Exception __exception,
            WorkOutputStorage.CaptureScope __state)
        {
            __state?.Dispose();
            return __exception;
        }
    }
}
