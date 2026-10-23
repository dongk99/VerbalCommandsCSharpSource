using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace VerbalCommands
{
	// Agent 04, brief S2: `show food.` (form `query food`). Read-only; every fact is what a player sees on screen.
	// Game facts used (all under sources\RimWorldDecompiled\):
	//   The game's food counter     RimWorld\ResourceCounter.cs:19-33 (TotalHumanEdibleNutrition: human-edible, nutrition-giving
	//                               defs in countedAmounts), :128-143 (only SlotGroup.HeldThings = storage), :157-168 (ShouldCount:
	//                               not IsNotFresh, not fogged); so food lying on the ground is NOT in it.
	//   Low food alert              RimWorld\Alert_LowFood.cs:32 (never before tick 150000), :45-48 (home map, colonist spawned,
	//                               TotalHumanEdibleNutrition < 4 * FreeColonistsSpawnedCount), :24-26 (days = nutrition /
	//                               (FreeColonistsSpawnedCount + PrisonersOfColonyCount))
	//   Food list the game keeps    Verse\ThingRequestGroup.cs:12 FoodSourceNotPlantOrTree, its def test Verse\ThingListGroupHelper.cs:58-63
	//                               (nutrition-giving ingestible whose foodType is more than Plant/Tree; the group also holds corpses
	//                               and nutrient paste dispensers, so this code keeps only ThingCategory.Item and handles corpses apart);
	//                               the game scans the same group in RimWorld\FoodUtility.cs:683, 700
	//   Human edible / nutrition    Verse\ThingDef.cs:930-940 (IsNutritionGivingIngestible), RimWorld\IngestibleProperties.cs:88 (HumanEdible),
	//                               :90-99 (IsMeal, preferability MealAwful..MealLavish)
	//   Spoiled                     RimWorld\RottableUtility.cs:7-15 (IsNotFresh), :17-25 (IsDessicated)
	//   Forbidden                   RimWorld\ForbidUtility.cs:191-202 (Thing.IsForbidden(Faction))
	//   In storage                  RimWorld\StoreUtility.cs:14, 38-41 (IsInAnyStorage, SlotGroupParentAt)
	//   Real hunger rate            RimWorld\Need_Food.cs:119-122 (FoodFallPerTickAssumingCategory: life stage, race, hediffs, traits, bed),
	//                               :51 (the game's own use of the Fed rate), :162 (the need falls by the rate x 150 every 150 ticks),
	//                               RimWorld\HungerLevelUtility.cs:24-34 (hunger category multiplier); one day = 60000 ticks
	//   Rot                         RimWorld\CompRottable.cs:51-59 (TicksUntilRotAtCurrentTemp), :178-185 (the inspect pane's own "frozen"
	//                               test: GenTemperature.RotRateAtTemperature(rounded ambient) < 0.001, Verse\GenTemperature.cs:459-470),
	//                               :208-225 (TicksUntilRotAtTemp)
	//   Unroofed deterioration      RimWorld\SteadyEnvironmentEffects.cs:84 (roofGrid.Roofed), :268-300 (FinalDeteriorationRate, "DeterioratingUnroofed")
	//   Growing zones               RimWorld\Zone_Growing.cs:61-84 (ContentsStatistics: plants of the zone's def in its cells),
	//                               :86-114 (inspect string: plant count, average growth, GrowthSeasonNow of the first cell),
	//                               :24-42 (PlantDefToGrow; its getter writes the potato default into the zone, so this code
	//                               reads the private field plantDefToGrow and falls back to the potato without writing it)
	//   Plant growth                RimWorld\Plant.cs:83 (Growth), :109-119 (HarvestableNow: growth > harvestMinGrowth), :289-303 (GrowthRate),
	//                               :334-347 (GrowthPerTick = 1 / (60000 * growDays) * GrowthRate, 0 while Resting), :277-286 (Resting:
	//                               day percent < 0.25 or > 0.8, so growth runs 55% of the day), :413-428 (TicksUntilFullyGrown, the game's
	//                               own formula; it is protected and not shown in the inspect pane, Plant.cs:1069-1141 shows percent and
	//                               growth rate only), :815-826 (growth is added every 2000 ticks only when PlantUtility.GrowthSeasonNow),
	//                               :350-395 (the four growth factors), :1076-1077 (inspect pane shows growth percent and rate)
	//   Growing season              RimWorld\PlantUtility.cs:21-33 (GrowthSeasonNow(cell, map, plantDef))
	//   Wild plants                 Verse\ThingRequestGroup.cs:29 HarvestablePlant (Verse\ThingListGroupHelper.cs:44-49),
	//                               RimWorld\PlantProperties.cs:50 (harvestedThingDef)
	//   Mouths                      Verse\MapPawns.cs:592 (FreeColonistsSpawnedCount), :242 (PrisonersOfColonyCount), :471 (SpawnedColonyAnimals)
	public static partial class FormPlanner
	{
		private const float ReadsFoodTicksPerDay = 60000f;
		private const float ReadsFoodAlertTick = 150000f;
		private const float ReadsFoodAlertBarsPerColonist = 4f;
		private const float ReadsFoodWildRadius = 30f;
		private const float ReadsFoodGrowingDayFraction = 0.55f;
		private const int ReadsFoodMaxLineWidth = 100;
		private const int ReadsFoodMaxLines = 60;
		private const int ReadsFoodKindCap = 8;
		private const int ReadsFoodRotCap = 8;
		private const int ReadsFoodZoneCap = 5;

		private static FieldInfo readsFoodPlantField;

		private sealed class ReadsFoodKind
		{
			public ThingDef def;
			public bool isMeal;
			public int total;
			public int storage;
			public int loose;
			public int carried;
			public int forbidden;
			public float nutrition;
		}

		private sealed class ReadsFoodRotEntry
		{
			public ThingDef def;
			public bool unroofed;
			public int count;
			public int minTicks;
		}

		private sealed class ReadsFoodScan
		{
			public readonly Dictionary<ThingDef, ReadsFoodKind> kinds = new Dictionary<ThingDef, ReadsFoodKind>();
			public readonly Dictionary<ThingDef, int> animalOnly = new Dictionary<ThingDef, int>();
			public readonly List<ReadsFoodRotEntry> rot = new List<ReadsFoodRotEntry>();
			public int animalCorpses;
			public int spoiled;
			public int forbidden;
			public int mealCount;
			public int rawCount;
			public float nutrition;
		}

		// ---- the scan --------------------------------------------------------------------------------------------------

		private static ReadsFoodScan ReadsFoodScanMap(Map map)
		{
			ReadsFoodScan scan = new ReadsFoodScan();
			// Spawned things only: a thing a pawn carries is not in listerThings, so food in a raider's or trader's
			// inventory never shows up here. Colonists' own carried food is added below.
			List<Thing> group = map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree);
			for (int i = 0; i < group.Count; i++)
			{
				Thing t = group[i];
				if (t == null || !t.Spawned || !AgentPlace.Visible(map, t.PositionHeld))
				{
					continue;
				}
				Corpse corpse = t as Corpse;
				if (corpse != null)
				{
					if (corpse.InnerPawn != null && corpse.InnerPawn.RaceProps.Animal && !t.IsDessicated())
					{
						scan.animalCorpses++;
					}
					continue;
				}
				ReadsFoodAddThing(map, scan, t, false);
			}
			foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
			{
				if (!AgentPlace.Visible(map, pawn.Position))
				{
					continue;
				}
				Thing carriedNow = pawn.carryTracker != null ? pawn.carryTracker.CarriedThing : null;
				if (carriedNow != null)
				{
					ReadsFoodAddThing(map, scan, carriedNow, true);
				}
				if (pawn.inventory != null && pawn.inventory.innerContainer != null)
				{
					foreach (Thing t in pawn.inventory.innerContainer)
					{
						ReadsFoodAddThing(map, scan, t, true);
					}
				}
			}
			scan.rot.Sort((ReadsFoodRotEntry a, ReadsFoodRotEntry b) => a.minTicks.CompareTo(b.minTicks));
			return scan;
		}

		private static void ReadsFoodAddThing(Map map, ReadsFoodScan scan, Thing t, bool carried)
		{
			ThingDef d = t.def;
			if (d.category != ThingCategory.Item || !d.IsNutritionGivingIngestible)
			{
				return;
			}
			bool human = d.ingestible.HumanEdible;
			if (t.IsNotFresh())
			{
				if (human)
				{
					scan.spoiled += t.stackCount;
				}
				return;
			}
			if (!human)
			{
				int had;
				scan.animalOnly.TryGetValue(d, out had);
				scan.animalOnly[d] = had + t.stackCount;
				return;
			}
			ReadsFoodKind kind;
			if (!scan.kinds.TryGetValue(d, out kind))
			{
				kind = new ReadsFoodKind();
				kind.def = d;
				kind.isMeal = d.ingestible.IsMeal;
				scan.kinds[d] = kind;
			}
			int n = t.stackCount;
			kind.total += n;
			if (carried)
			{
				kind.carried += n;
			}
			else
			{
				if (t.IsInAnyStorage())
				{
					kind.storage += n;
				}
				else
				{
					kind.loose += n;
				}
				if (t.IsForbidden(Faction.OfPlayer))
				{
					kind.forbidden += n;
					scan.forbidden += n;
				}
			}
			float nutrition = t.GetStatValue(StatDefOf.Nutrition) * n;
			kind.nutrition += nutrition;
			scan.nutrition += nutrition;
			if (kind.isMeal)
			{
				scan.mealCount += n;
			}
			else
			{
				scan.rawCount += n;
			}

			CompRottable rot = t.TryGetComp<CompRottable>();
			if (rot != null && rot.Active)
			{
				// The inspect pane calls an item frozen when the rot rate at the rounded temperature is under 0.001 (CompRottable.cs:182).
				float rate = GenTemperature.RotRateAtTemperature(Mathf.RoundToInt(t.AmbientTemperature));
				if (rate >= 0.001f)
				{
					int ticks = rot.TicksUntilRotAtCurrentTemp;
					bool unroofed = !carried && !map.roofGrid.Roofed(t.PositionHeld);
					ReadsFoodRotEntry entry = null;
					for (int i = 0; i < scan.rot.Count; i++)
					{
						if (scan.rot[i].def == d && scan.rot[i].unroofed == unroofed)
						{
							entry = scan.rot[i];
							break;
						}
					}
					if (entry == null)
					{
						entry = new ReadsFoodRotEntry();
						entry.def = d;
						entry.unroofed = unroofed;
						entry.minTicks = ticks;
						scan.rot.Add(entry);
					}
					entry.count += n;
					entry.minTicks = Math.Min(entry.minTicks, ticks);
				}
			}
		}

		// ---- the shared numbers ------------------------------------------------------------------------------------------

		// Sum of the colonists' and prisoners' Need_Food fall per day, ticks per day 60000. FoodFallPerTick (Need_Food.cs:49) is scaled
		// down while a pawn is Hungry (x0.5), UrgentlyHungry (x0.25) or Starving (x0) (HungerLevelUtility.cs:24-34), so it would read
		// low for a pawn that has not eaten yet; the rate while Fed (the one the game uses for TicksUntilHungryWhenFed, Need_Food.cs:51)
		// is the steady burn rate and still includes life stage, traits, hediffs and bed (Need_Food.cs:119-122).
		private static float ReadsFoodEatingPerDay(Map map)
		{
			float perDay = 0f;
			foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
			{
				if (pawn.needs != null && pawn.needs.food != null)
				{
					perDay += pawn.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed) * ReadsFoodTicksPerDay;
				}
			}
			List<Pawn> prisoners = new List<Pawn>(map.mapPawns.PrisonersOfColony);
			foreach (Pawn pawn in prisoners)
			{
				if (pawn.needs != null && pawn.needs.food != null)
				{
					perDay += pawn.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed) * ReadsFoodTicksPerDay;
				}
			}
			return perDay;
		}

		private static string ReadsFoodF1(float v)
		{
			return v.ToString("F1", CultureInfo.InvariantCulture);
		}

		private static string ReadsFoodGameDays(Map map, int mouths)
		{
			if (mouths <= 0)
			{
				return "n/a (no colonists)";
			}
			return ReadsFoodF1(map.resourceCounter.TotalHumanEdibleNutrition / mouths) + " days";
		}

		private static string ReadsFoodRealDays(float nutrition, float perDay)
		{
			if (perDay <= 0.0001f)
			{
				return "n/a (nobody eating)";
			}
			return ReadsFoodF1(nutrition / perDay) + " days";
		}

		// ---- one line for the status answer ------------------------------------------------------------------------------

		internal static string ReadsFoodLine(Map map)
		{
			ReadsFoodScan scan = ReadsFoodScanMap(map);
			int mouths = map.mapPawns.FreeColonistsSpawnedCount + map.mapPawns.PrisonersOfColonyCount;
			float perDay = ReadsFoodEatingPerDay(map);
			return "Food: " + scan.mealCount + " meals, " + scan.rawCount + " raw; " + ReadsFoodF1(scan.nutrition) + " nutrition visible = "
				+ ReadsFoodRealDays(scan.nutrition, perDay) + " at real eating (game's storage count: " + ReadsFoodGameDays(map, mouths) + ")";
		}

		// ---- the answer to `show food.` -----------------------------------------------------------------------------------

		private static string ReadsFood(Map map)
		{
			ReadsFoodScan scan = ReadsFoodScanMap(map);
			List<string> lines = new List<string>();

			// Meals and raw food, per kind: count and where it lies.
			ReadsFoodKindLines(lines, "Meals:", scan.kinds.Values.Where((ReadsFoodKind k) => k.isMeal));
			ReadsFoodKindLines(lines, "Raw food:", scan.kinds.Values.Where((ReadsFoodKind k) => !k.isMeal));
			if (scan.animalOnly.Count > 0)
			{
				List<string> animal = scan.animalOnly.OrderByDescending((KeyValuePair<ThingDef, int> p) => p.Value)
					.Select((KeyValuePair<ThingDef, int> p) => PlainText(p.Key.label) + " x" + p.Value).ToList();
				ReadsFoodAdd(lines, "Food only animals eat: " + CapList(animal, 6) + ".");
			}
			if (scan.animalCorpses > 0)
			{
				ReadsFoodAdd(lines, scan.animalCorpses + " animal " + (scan.animalCorpses == 1 ? "corpse" : "corpses") + " to butcher.");
			}
			ReadsFoodAdd(lines, "Counted: food you can see on the map, not spoiled, including food the colonists carry.");
			if (scan.forbidden > 0)
			{
				ReadsFoodAdd(lines, "Forbidden items are counted: " + scan.forbidden + " of the items above are forbidden.");
			}
			if (scan.spoiled > 0)
			{
				ReadsFoodAdd(lines, "Spoiled and not counted: " + scan.spoiled + ".");
			}

			// Nutrition: the game's number and ours.
			float storageNutrition = map.resourceCounter.TotalHumanEdibleNutrition;
			ReadsFoodAdd(lines, "Nutrition in storage (the game's count): " + ReadsFoodF1(storageNutrition));
			ReadsFoodAdd(lines, "Nutrition, all visible human food: " + ReadsFoodF1(scan.nutrition));

			// Mouths and days.
			int colonists = map.mapPawns.FreeColonistsSpawnedCount;
			int prisoners = map.mapPawns.PrisonersOfColonyCount;
			int mouths = colonists + prisoners;
			ReadsFoodAdd(lines, "Mouths: " + colonists + " colonists + " + prisoners + " prisoners = " + mouths + " (the game's rule). Colony animals: "
				+ map.mapPawns.SpawnedColonyAnimals.Count + " (not in the days).");
			float perDay = ReadsFoodEatingPerDay(map);
			ReadsFoodAdd(lines, "Real eating rate: " + ReadsFoodF1(perDay) + " nutrition a day for all of them.");
			ReadsFoodAdd(lines, "Days of food, the game's way (1 full bar per person a day, storage only): " + ReadsFoodGameDays(map, mouths));
			ReadsFoodAdd(lines, "Days of food, all visible food at the colonists' real eating rate: " + ReadsFoodRealDays(scan.nutrition, perDay));

			// The alert, from the same numbers (Alert_LowFood.cs:32, 45-48); the alert itself is not called.
			bool below = map.IsPlayerHome && map.mapPawns.AnyColonistSpawned
				&& storageNutrition < ReadsFoodAlertBarsPerColonist * colonists;
			bool late = Find.TickManager.TicksGame >= ReadsFoodAlertTick;
			string rule = "(shows below 4 bars per colonist in storage, after day 2.5)";
			if (below && late)
			{
				ReadsFoodAdd(lines, "Low food alert: showing " + rule);
			}
			else if (below)
			{
				ReadsFoodAdd(lines, "Low food alert: not showing " + rule + "; storage is below that now, so it shows from day 2.5.");
			}
			else
			{
				ReadsFoodAdd(lines, "Low food alert: not showing " + rule);
			}

			ReadsFoodGrowingLines(map, lines);
			ReadsFoodRotLines(lines, scan);

			if (lines.Count > ReadsFoodMaxLines)
			{
				int left = lines.Count - (ReadsFoodMaxLines - 1);
				lines.RemoveRange(ReadsFoodMaxLines - 1, left);
				lines.Add("(" + left + " more lines left out)");
			}
			return string.Join("\n", lines.ToArray());
		}

		private static void ReadsFoodKindLines(List<string> lines, string header, IEnumerable<ReadsFoodKind> source)
		{
			List<ReadsFoodKind> kinds = source.OrderByDescending((ReadsFoodKind k) => k.nutrition).ToList();
			if (kinds.Count == 0)
			{
				ReadsFoodAdd(lines, header + " none.");
				return;
			}
			ReadsFoodAdd(lines, header);
			for (int i = 0; i < kinds.Count && i < ReadsFoodKindCap; i++)
			{
				ReadsFoodKind k = kinds[i];
				string where = k.storage + " in storage";
				if (k.loose > 0)
				{
					where += ", " + k.loose + " loose";
				}
				if (k.carried > 0)
				{
					where += ", " + k.carried + " carried";
				}
				if (k.forbidden > 0)
				{
					where += ", " + k.forbidden + " forbidden";
				}
				ReadsFoodAdd(lines, "- " + PlainText(k.def.label) + " x" + k.total + " (" + where + ")");
			}
			if (kinds.Count > ReadsFoodKindCap)
			{
				lines.Add("- and " + (kinds.Count - ReadsFoodKindCap) + " more kinds");
			}
		}

		// ---- growing ---------------------------------------------------------------------------------------------------------

		private static ThingDef ReadsFoodZonePlant(Zone_Growing zone)
		{
			if (readsFoodPlantField == null)
			{
				readsFoodPlantField = typeof(Zone_Growing).GetField("plantDefToGrow", BindingFlags.Instance | BindingFlags.NonPublic);
			}
			ThingDef def = readsFoodPlantField != null ? readsFoodPlantField.GetValue(zone) as ThingDef : null;
			return def ?? ThingDefOf.Plant_Potato;
		}

		// Days until the plant reaches 'target' growth, by the game's own formula (Plant.GrowthPerTick, Plant.cs:346): the factors
		// as they are now, the night rest counted as 45% of the day (Plant.Resting, Plant.cs:277-286). -1 when it is not growing now.
		private static float ReadsFoodDaysToGrowth(Plant p, float target)
		{
			if (p.Growth >= target)
			{
				return 0f;
			}
			if (p.Blighted || !PlantUtility.GrowthSeasonNow(p.Position, p.Map, p.def))
			{
				return -1f;
			}
			float rate = p.GrowthRateFactor_Fertility * p.GrowthRateFactor_Temperature * p.GrowthRateFactor_NoxiousHaze * p.GrowthRateFactor_Drought;
			if (rate <= 0.001f)
			{
				return -1f;
			}
			return (target - p.Growth) * p.def.plant.growDays / (rate * ReadsFoodGrowingDayFraction);
		}

		private static void ReadsFoodGrowingLines(Map map, List<string> lines)
		{
			List<Zone_Growing> zones = map.zoneManager.AllZones.OfType<Zone_Growing>().ToList();
			if (zones.Count == 0)
			{
				lines.Add("No growing zones.");
			}
			else
			{
				lines.Add("Growing:");
			}
			for (int zi = 0; zi < zones.Count && zi < ReadsFoodZoneCap; zi++)
			{
				Zone_Growing zone = zones[zi];
				ThingDef plantDef = ReadsFoodZonePlant(zone);
				List<IntVec3> cells = zone.Cells;
				if (cells.Count == 0)
				{
					continue;
				}
				ReadsFoodAdd(lines, "- " + PlainText(zone.label) + ": " + PlainText(plantDef.label) + ", " + cells.Count + " cells, at "
					+ AgentPlace.Describe(map, cells[0]));
				int sown = 0;
				int harvestable = 0;
				float growthSum = 0f;
				float soonestHarvest = -1f;
				float soonestFull = -1f;
				foreach (IntVec3 cell in cells)
				{
					if (!AgentPlace.Visible(map, cell))
					{
						continue;
					}
					List<Thing> things = cell.GetThingList(map);
					for (int i = 0; i < things.Count; i++)
					{
						Plant plant = things[i] as Plant;
						if (plant == null || plant.def != plantDef)
						{
							continue;
						}
						sown++;
						growthSum += plant.Growth;
						if (plant.HarvestableNow)
						{
							harvestable++;
						}
						else if (plantDef.plant.Harvestable)
						{
							float h = ReadsFoodDaysToGrowth(plant, plantDef.plant.harvestMinGrowth);
							if (h >= 0f && (soonestHarvest < 0f || h < soonestHarvest))
							{
								soonestHarvest = h;
							}
						}
						float f = ReadsFoodDaysToGrowth(plant, 0.9999f);
						if (f > 0f && (soonestFull < 0f || f < soonestFull))
						{
							soonestFull = f;
						}
					}
				}
				bool season = PlantUtility.GrowthSeasonNow(cells[0], map, plantDef);
				if (sown == 0)
				{
					ReadsFoodAdd(lines, "  nothing sown yet; growing season now: " + (season ? "yes" : "no"));
					continue;
				}
				ReadsFoodAdd(lines, "  " + sown + " sown, average growth " + Percent(growthSum / sown) + ", " + harvestable + " harvestable now; growing season now: "
					+ (season ? "yes" : "no"));
				List<string> soon = new List<string>();
				if (harvestable == 0 && soonestHarvest >= 0f)
				{
					soon.Add("first harvest in " + ReadsFoodF1(soonestHarvest) + " days");
				}
				if (soonestFull > 0f)
				{
					soon.Add("soonest fully grown in " + ReadsFoodF1(soonestFull) + " days");
				}
				if (soon.Count > 0)
				{
					ReadsFoodAdd(lines, "  " + string.Join("; ", soon.ToArray()) + " (at today's conditions, night rest counted)");
				}
				else if (harvestable < sown)
				{
					ReadsFoodAdd(lines, "  not growing right now (out of season, blight or too little fertility).");
				}
			}
			if (zones.Count > ReadsFoodZoneCap)
			{
				lines.Add("- and " + (zones.Count - ReadsFoodZoneCap) + " more growing zones");
			}
			ReadsFoodWildLine(map, lines);
		}

		// Harvestable wild plants that give human food, visible, within 30 tiles of a colonist; plants inside a growing zone are not wild.
		private static void ReadsFoodWildLine(Map map, List<string> lines)
		{
			List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
			if (colonists.Count == 0)
			{
				return;
			}
			Dictionary<ThingDef, int> counts = new Dictionary<ThingDef, int>();
			float nearestDist = float.MaxValue;
			IntVec3 nearestCell = IntVec3.Invalid;
			List<Thing> plants = map.listerThings.ThingsInGroup(ThingRequestGroup.HarvestablePlant);
			for (int i = 0; i < plants.Count; i++)
			{
				Plant plant = plants[i] as Plant;
				if (plant == null || !plant.Spawned || !plant.HarvestableNow)
				{
					continue;
				}
				ThingDef yield = plant.def.plant.harvestedThingDef;
				if (yield == null || !yield.IsNutritionGivingIngestible || !yield.ingestible.HumanEdible)
				{
					continue;
				}
				if (!AgentPlace.Visible(map, plant.Position) || map.zoneManager.ZoneAt(plant.Position) is Zone_Growing)
				{
					continue;
				}
				float best = float.MaxValue;
				for (int c = 0; c < colonists.Count; c++)
				{
					best = Math.Min(best, (plant.Position - colonists[c].Position).LengthHorizontal);
				}
				if (best > ReadsFoodWildRadius)
				{
					continue;
				}
				int had;
				counts.TryGetValue(plant.def, out had);
				counts[plant.def] = had + 1;
				if (best < nearestDist)
				{
					nearestDist = best;
					nearestCell = plant.Position;
				}
			}
			if (counts.Count == 0)
			{
				ReadsFoodAdd(lines, "Wild plants: no harvestable wild food plants within 30 tiles of a colonist.");
				return;
			}
			List<string> parts = counts.OrderByDescending((KeyValuePair<ThingDef, int> p) => p.Value)
				.Select((KeyValuePair<ThingDef, int> p) => PlainText(p.Key.label) + " x" + p.Value).ToList();
			ReadsFoodAdd(lines, "Wild plants harvestable now, within 30 tiles of a colonist: " + CapList(parts, 5) + "; nearest at "
				+ AgentPlace.Describe(map, nearestCell) + ".");
		}

		// ---- rotting -------------------------------------------------------------------------------------------------------------

		private static void ReadsFoodRotLines(List<string> lines, ReadsFoodScan scan)
		{
			if (scan.rot.Count == 0)
			{
				lines.Add("Nothing is rotting (all frozen or nothing perishable).");
				return;
			}
			lines.Add("Will rot (soonest first, not frozen):");
			for (int i = 0; i < scan.rot.Count && i < ReadsFoodRotCap; i++)
			{
				ReadsFoodRotEntry e = scan.rot[i];
				ReadsFoodAdd(lines, "- " + PlainText(e.def.label) + " x" + e.count + ": rots in " + ReadsFoodF1(e.minTicks / ReadsFoodTicksPerDay) + " days"
					+ (e.unroofed ? " (unroofed, also deteriorates)" : ""));
			}
			if (scan.rot.Count > ReadsFoodRotCap)
			{
				lines.Add("- and " + (scan.rot.Count - ReadsFoodRotCap) + " more");
			}
		}

		// ---- line width ----------------------------------------------------------------------------------------------------------

		// Adds a line; one wider than 100 characters is split at a space and the rest indented.
		private static void ReadsFoodAdd(List<string> lines, string text)
		{
			string rest = text;
			bool first = true;
			while (rest.Length > (first ? ReadsFoodMaxLineWidth : ReadsFoodMaxLineWidth - 2))
			{
				int width = first ? ReadsFoodMaxLineWidth : ReadsFoodMaxLineWidth - 2;
				int cut = rest.LastIndexOf(' ', width - 1);
				if (cut < 20)
				{
					cut = width;
				}
				lines.Add((first ? "" : "  ") + rest.Substring(0, cut).TrimEnd());
				rest = rest.Substring(cut).TrimStart();
				first = false;
			}
			lines.Add((first ? "" : "  ") + rest);
		}
	}
}
