using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Lilly.CheatWork
{
    [StaticConstructorOnStartup]
    internal static class CheatWorkStartup
    {
        static CheatWorkStartup()
        {
            new Harmony("lilly.cheatwork").PatchAll(Assembly.GetExecutingAssembly());
        }
    }

    [HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.AddDesignation))]
    internal static class DesignationManager_AddDesignation_Patch
    {
        [HarmonyPostfix]
        private static void Postfix(DesignationManager __instance, Designation __0)
        {
            ImmediateWork.ProcessDesignation(__instance, __0);
        }
    }

    [HarmonyPatch(typeof(GameComponentUtility), nameof(GameComponentUtility.GameComponentTick))]
    internal static class GameComponentUtility_GameComponentTick_Patch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            ImmediateWork.ProcessPendingMines();
        }
    }

    [HarmonyPatch(typeof(Area), "Set", new Type[] { typeof(IntVec3), typeof(bool) })]
    internal static class Area_Set_Patch
    {
        [HarmonyPostfix]
        private static void Postfix(Area __instance, IntVec3 __0, bool __1)
        {
            ImmediateWork.ProcessRoofRemoval(__instance, __0, __1);
        }
    }

    [HarmonyPatch(typeof(Zone_Growing), nameof(Zone_Growing.SetPlantDefToGrow))]
    internal static class Zone_Growing_SetPlantDefToGrow_Patch
    {
        [HarmonyPostfix]
        private static void Postfix(Zone_Growing __instance)
        {
            ImmediateWork.ProcessGrowingZone(__instance);
        }
    }

    [HarmonyPatch(typeof(Zone_Growing), nameof(Zone_Growing.AddCell))]
    internal static class Zone_Growing_AddCell_Patch
    {
        [HarmonyPostfix]
        private static void Postfix(Zone_Growing __instance, IntVec3 __0)
        {
            ImmediateWork.ProcessGrowingZoneCell(__instance, __0);
        }
    }

    internal static class ImmediateWork
    {
        private static bool processing;
        private static readonly Queue<PendingMine> pendingMines = new Queue<PendingMine>();
        private static readonly HashSet<Designation> queuedMineDesignations = new HashSet<Designation>();

        private readonly struct PendingMine
        {
            internal readonly Map Map;
            internal readonly DesignationManager Manager;
            internal readonly Designation Designation;

            internal PendingMine(Map map, DesignationManager manager, Designation designation)
            {
                Map = map;
                Manager = manager;
                Designation = designation;
            }
        }

        internal static void ProcessDesignation(DesignationManager manager, Designation designation)
        {
            if (!CheatWorkMod.Enabled || processing || !InGame || designation == null || designation.def == null)
                return;

            string defName = designation.def.defName;
            if (defName != "Mine" && defName != "MineVein" && defName != "Deconstruct" &&
                defName != "RemoveFloor" && defName != "RemoveFoundation" &&
                defName != "CutPlant" && defName != "HarvestPlant")
                return;

            Map map = manager.map;
            if (map == null)
                return;

            if (defName == "Mine" || defName == "MineVein")
            {
                if (queuedMineDesignations.Add(designation))
                    pendingMines.Enqueue(new PendingMine(map, manager, designation));
                return;
            }

            Plant plant = null;
            processing = true;
            try
            {
                switch (defName)
                {
                    case "Deconstruct":
                        Deconstruct(designation);
                        break;
                    case "RemoveFloor":
                        RemoveFloor(map, designation.target.Cell);
                        break;
                    case "RemoveFoundation":
                        RemoveFoundation(map, designation.target.Cell);
                        break;
                    case "CutPlant":
                        plant = designation.target.Thing as Plant;
                        if (plant != null)
                            CutPlant(map, plant);
                        break;
                    case "HarvestPlant":
                        plant = designation.target.Thing as Plant;
                        if (plant != null)
                            HarvestPlant(map, plant);
                        break;
                }
            }
            finally
            {
                processing = false;
                if (plant != null)
                    map.designationManager.TryRemoveDesignationOn(plant, DesignationDefOf.CutPlant);
                if (manager.AllDesignations.Contains(designation))
                    manager.RemoveDesignation(designation);
            }
        }

        internal static void ProcessPendingMines()
        {
            if (processing || pendingMines.Count == 0)
                return;

            if (!CheatWorkMod.Enabled || !InGame)
            {
                pendingMines.Clear();
                queuedMineDesignations.Clear();
                return;
            }

            processing = true;
            try
            {
                int queuedCount = pendingMines.Count;
                for (int i = 0; i < queuedCount; i++)
                {
                    PendingMine pending = pendingMines.Dequeue();
                    queuedMineDesignations.Remove(pending.Designation);

                    if (pending.Map == null || pending.Manager == null ||
                        pending.Manager.map != pending.Map ||
                        !pending.Manager.AllDesignations.Contains(pending.Designation))
                        continue;

                    try
                    {
                        Mine(pending.Map, pending.Designation);
                        pending.Manager.RemoveDesignation(pending.Designation);
                    }
                    catch (Exception exception)
                    {
                        Log.Error("[Cheat Work] Failed to process a queued mine designation: " + exception);
                    }
                }
            }
            finally
            {
                processing = false;
            }
        }

        internal static void ProcessRoofRemoval(Area area, IntVec3 cell, bool value)
        {
            if (!CheatWorkMod.Enabled || processing || !InGame || !value || !(area is Area_NoRoof))
                return;

            Map map = area.Map;
            if (map == null || !cell.InBounds(map) || !map.roofGrid.Roofed(cell))
                return;

            processing = true;
            try
            {
                map.roofGrid.SetRoof(cell, null);
                RoofCollapseCellsFinder.CheckCollapseFlyingRoofs(
                    new List<IntVec3> { cell }, map, true, false);
            }
            finally
            {
                processing = false;
            }
        }

        internal static void ProcessGrowingZone(Zone_Growing zone)
        {
            if (zone == null || !CheatWorkMod.Enabled || processing || !InGame)
                return;

            List<IntVec3> cells = zone.Cells;
            for (int i = 0; i < cells.Count; i++)
                ProcessGrowingZoneCell(zone, cells[i]);
        }

        internal static void ProcessGrowingZoneCell(Zone_Growing zone, IntVec3 cell)
        {
            if (zone == null || !CheatWorkMod.Enabled || processing || !InGame ||
                !zone.allowSow || !zone.CanAcceptSowNow())
                return;

            Map map = zone.Map;
            ThingDef plantDef = zone.GetPlantDefToGrow();
            if (map == null || plantDef == null || plantDef.plant == null ||
                !plantDef.plant.Sowable || !cell.InBounds(map) ||
                !PlantUtility.CanNowPlantAt(plantDef, cell, map, false) ||
                !PlantUtility.GrowthSeasonNow(cell, map, plantDef) ||
                PlantUtility.AdjacentSowBlocker(plantDef, cell, map) != null)
                return;

            List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Plant || things[i].def.BlocksPlanting(false))
                    return;
            }

            processing = true;
            try
            {
                Plant plant = GenSpawn.Spawn(plantDef, cell, map, WipeMode.Vanish) as Plant;
                if (plant == null)
                {
                    Log.Error("[Cheat Work] Failed to spawn sown plant " + plantDef.defName + ".");
                    return;
                }

                plant.Growth = 0f;
                plant.sown = true;
                plant.Growth = 0.0001f;
                map.mapDrawer.MapMeshDirty(cell, MapMeshFlagDefOf.Things);

                Pawn worker = FindWorker(map);
                if (worker == null)
                    return;

                worker.records.Increment(RecordDefOf.PlantsSown);
                Find.HistoryEventsManager.RecordEvent(
                    new HistoryEvent(HistoryEventDefOf.SowedPlant,
                        NamedArgumentUtility.Named(worker, HistoryEventArgsNames.Doer)));
                if (plantDef.plant.humanFoodPlant)
                {
                    Find.HistoryEventsManager.RecordEvent(
                        new HistoryEvent(HistoryEventDefOf.SowedHumanFoodPlant,
                            NamedArgumentUtility.Named(worker, HistoryEventArgsNames.Doer)));
                }
            }
            finally
            {
                processing = false;
            }
        }

        private static Pawn FindWorker(Map map)
        {
            List<Pawn> colonists = map.mapPawns.FreeColonists;
            for (int i = 0; i < colonists.Count; i++)
            {
                if (colonists[i].Faction == Faction.OfPlayer)
                    return colonists[i];
            }

            return null;
        }

        private static bool InGame
        {
            get { return Scribe.mode == LoadSaveMode.Inactive && Current.ProgramState == ProgramState.Playing; }
        }

        private static void Mine(Map map, Designation designation)
        {
            Mineable mineable = designation.target.Thing as Mineable;
            if (mineable == null && designation.target.Cell.InBounds(map))
            {
                List<Thing> things = map.thingGrid.ThingsListAtFast(designation.target.Cell);
                for (int i = 0; i < things.Count; i++)
                {
                    mineable = things[i] as Mineable;
                    if (mineable != null)
                        break;
                }
            }

            if (mineable != null && mineable.Spawned && !mineable.Destroyed)
                mineable.DestroyMined(null);
        }

        private static void Deconstruct(Designation designation)
        {
            Building building = designation.target.Thing as Building;
            if (building != null && building.Spawned && !building.Destroyed &&
                building.DeconstructibleBy(Faction.OfPlayer))
                building.Destroy(DestroyMode.Deconstruct);
        }

        private static void RemoveFloor(Map map, IntVec3 cell)
        {
            if (!cell.InBounds(map) || !map.terrainGrid.CanRemoveTopLayerAt(cell))
                return;

            TerrainDef temporaryTerrain = map.terrainGrid.TempTerrainAt(cell);
            if (temporaryTerrain != null && temporaryTerrain.Removable)
                map.terrainGrid.RemoveTempTerrain(cell, false, false);
            else
                map.terrainGrid.RemoveTopLayer(cell, true);

            FilthMaker.RemoveAllFilth(cell, map);
        }

        private static void RemoveFoundation(Map map, IntVec3 cell)
        {
            if (!cell.InBounds(map) || !map.terrainGrid.CanRemoveFoundationAt(cell))
                return;

            map.terrainGrid.RemoveFoundation(cell, true);
            FilthMaker.RemoveAllFilth(cell, map);
        }

        private static void CutPlant(Map map, Plant plant)
        {
            CompletePlantWork(map, plant, PlantDestructionMode.Cut, false);
        }

        private static void HarvestPlant(Map map, Plant plant)
        {
            CompletePlantWork(map, plant, PlantDestructionMode.Chop, true);
        }

        private static void CompletePlantWork(
            Map map,
            Plant plant,
            PlantDestructionMode destructionMode,
            bool requireHarvestable)
        {
            if (!plant.Spawned || plant.Map != map || (requireHarvestable && !plant.HarvestableNow))
                return;

            Pawn worker = FindActorPawn(map);
            if (worker == null)
            {
                Log.Warning("[Cheat Work] Cannot process plant work because no player pawn exists.");
                return;
            }

            ThingDef harvestedThingDef = plant.def.plant.harvestedThingDef;
            if (harvestedThingDef != null)
            {
                StatDef yieldStat = harvestedThingDef.IsDrug || plant.def.plant.drugForHarvestPurposes
                    ? StatDefOf.DrugHarvestYield
                    : StatDefOf.PlantHarvestYield;
                float yieldFactor = StatExtension.GetStatValue(worker, yieldStat, true, -1);
                bool harvestFailed = worker.RaceProps.Humanlike &&
                    plant.def.plant.harvestFailable &&
                    !plant.Blighted &&
                    Rand.Value > yieldFactor;

                if (!harvestFailed)
                {
                    int yield = plant.YieldNow();
                    if (yieldFactor > 1f)
                        yield = GenMath.RoundRandom(yield * yieldFactor);

                    if (yield > 0)
                    {
                        Thing harvested = ThingMaker.MakeThing(harvestedThingDef);
                        harvested.stackCount = yield;
                        if (worker.Faction != Faction.OfPlayer)
                            ForbidUtility.SetForbidden(harvested, true, true);
                        Find.QuestManager.Notify_PlantHarvested(worker, harvested);
                        PlaceHarvestedThing(harvested, plant, map);
                        worker.records.Increment(RecordDefOf.PlantsHarvested);
                    }

                    if (plant.HarvestableNow)
                    {
                        for (int i = 0; i < plant.AllComps.Count; i++)
                        {
                            IEnumerable<ThingDefCountClass> additionalYields =
                                plant.AllComps[i].GetAdditionalHarvestYield();
                            foreach (ThingDefCountClass additionalYield in additionalYields)
                            {
                                Thing additional = ThingMaker.MakeThing(additionalYield.thingDef);
                                additional.stackCount = additionalYield.count;
                                PlaceHarvestedThing(additional, plant, map);
                            }
                        }
                    }
                }
            }

            plant.PlantCollected(worker, destructionMode);
        }

        private static Pawn FindActorPawn(Map map)
        {
            List<Pawn> colonists = map.mapPawns.FreeColonists;
            for (int i = 0; i < colonists.Count; i++)
            {
                if (colonists[i].Faction == Faction.OfPlayer)
                    return colonists[i];
            }

            List<Pawn> worldPawns = Find.WorldPawns.AllPawnsAlive;
            for (int i = 0; i < worldPawns.Count; i++)
            {
                if (worldPawns[i].Faction == Faction.OfPlayer)
                    return worldPawns[i];
            }

            return null;
        }

        private static void PlaceHarvestedThing(Thing harvested, Plant plant, Map map)
        {
            Thing placedThing;
            if (!GenPlace.TryPlaceThing(harvested, plant.Position, map, ThingPlaceMode.Near, out placedThing))
                Log.Error("[Cheat Work] Could not place harvested product " + harvested.def.defName + ".");
        }
    }
}
