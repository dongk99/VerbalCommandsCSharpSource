using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Growing zones and plant growers (hydroponics basins, plant pots) with cells that grow nothing, for the
	// orchestrator's reminder batch, scanned every 3 game hours (AgentReminderScan.GrowTick). User, 2026-10-14: "make a
	// reminder for hydroponic/growing zone to see if there are empty areas not growing anything actively" / "this one
	// should be every 3 ingame hour and not 6 (so, half)".
	//
	// One line per growing zone, and per group of growers of the same kind, crop, room and power state. It counts:
	//   empty cells: no plant of the crop set for it (another plant there, e.g. grass, is cut by the sower first). Why,
	//   in the sowing work giver's own order (WorkGiver_GrowerSow.cs): sowing turned off for the zone (:30, allowSow),
	//   no power (:23, Building_PlantGrower.CanAcceptSowNow), not a growing temperature at the cell (:63, :205,
	//   PlantUtility.GrowthSeasonNow), crop research not done (sowResearchPrerequisites), the crop can't go on the
	//   cell (:205, PlantUtility.CanEverPlantAt with other plants wiped, the map temperature left out: the blocking
	//   thing or the game's reason), no colonist with Growing on and the crop's Plants skill (:159); else "waiting to
	//   be sown".
	//   plants not growing: blighted, not a growing temperature (Plant.GrowthRate is 0 then, Plant.cs:289-303), dying,
	//   on an unpowered grower (they rot, Building_PlantGrower.TickRare), too dark at day (the light factor is 0:
	//   below the crop's growMinGlow; checked only from 7h to 18h, plants rest outside 6h-19h, Plant.cs:277-287), or
	//   mature and waiting for harvest.
	// The orders named are the router's: "zone ZONE allow sowing." and "prioritize sowing at SPOT with DOER.".
	internal static class AgentGrowScan
	{
		private const int MaxGroups = 8;
		private const int MaxReasons = 4;

		private class Bed
		{
			public string what;
			public string zoneName;   // null for growers
			public ThingDef plant;
			public List<IntVec3> cells;
			public bool sowOff;
			public bool noPower;
			public bool plural;   // "2 hydroponics basins ... have"
		}

		public static List<string> Lines(Map map)
		{
			List<Bed> beds = new List<Bed>();
			foreach (Zone z in map.zoneManager.AllZones)
			{
				Zone_Growing g = z as Zone_Growing;
				if (g == null || g.Cells.Count == 0)
				{
					continue;
				}
				beds.Add(new Bed { what = "growing zone '" + g.label + "'", zoneName = g.label, plant = g.GetPlantDefToGrow(), cells = g.Cells.ToList(), sowOff = !g.allowSow });
			}
			List<Building_PlantGrower> growers = map.listerBuildings.allBuildingsColonist.OfType<Building_PlantGrower>().Where((Building_PlantGrower b) => b.Spawned).ToList();
			foreach (var group in growers.GroupBy((Building_PlantGrower b) => new { b.def, plant = b.GetPlantDefToGrow(), room = b.GetRoom(), power = b.CanAcceptSowNow() }))
			{
				List<Building_PlantGrower> list = group.ToList();
				string where = group.Key.room == null || group.Key.room.PsychologicallyOutdoors ? "outdoors" : "in the " + RoomName(group.Key.room);
				string label = list.Count == 1 ? group.Key.def.label : list.Count + " " + Find.ActiveLanguageWorker.Pluralize(group.Key.def.label, list.Count);
				beds.Add(new Bed { what = label + " " + where, plant = group.Key.plant, cells = list.SelectMany((Building_PlantGrower b) => b.OccupiedRect().Cells).Distinct().ToList(), noPower = !group.Key.power, plural = list.Count > 1 });
			}

			List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned.ToList();
			float day = GenLocalDate.DayPercent(map);
			bool daytime = day >= 0.29f && day <= 0.75f;
			List<string> lines = new List<string>();
			int more = 0;
			foreach (Bed bed in beds)
			{
				if (bed.plant == null || bed.plant.plant == null)
				{
					continue;
				}
				string line = BedLine(map, bed, colonists, daytime);
				if (line == null)
				{
					continue;
				}
				if (lines.Count >= MaxGroups)
				{
					more++;
					continue;
				}
				lines.Add(line);
			}
			if (more > 0)
			{
				lines.Add("[REMINDER] And " + more + " more growing zones or plant growers with cells not growing anything.");
			}
			return lines;
		}

		private static string BedLine(Map map, Bed bed, List<Pawn> colonists, bool daytime)
		{
			ThingDef crop = bed.plant;
			Pawn sower = colonists.FirstOrDefault((Pawn p) => p.workSettings != null && p.workSettings.EverWork && !p.WorkTypeIsDisabled(WorkTypeDefOf.Growing)
				&& p.workSettings.WorkIsActive(WorkTypeDefOf.Growing) && (crop.plant.sowMinSkill <= 0 || (p.skills != null && p.skills.GetSkill(SkillDefOf.Plants).Level >= crop.plant.sowMinSkill)));
			ResearchProjectDef research = crop.plant.sowResearchPrerequisites == null ? null : crop.plant.sowResearchPrerequisites.FirstOrDefault((ResearchProjectDef r) => r != null && !r.IsFinished);

			Dictionary<string, int> emptyWhy = new Dictionary<string, int>();
			Dictionary<string, int> stalledWhy = new Dictionary<string, int>();
			int empty = 0;
			int stalled = 0;
			IntVec3 firstWaiting = IntVec3.Invalid;
			foreach (IntVec3 c in bed.cells)
			{
				if (!c.InBounds(map))
				{
					continue;
				}
				Plant plant = c.GetThingList(map).OfType<Plant>().FirstOrDefault((Plant p) => p.def == crop);
				if (plant != null)
				{
					string why = Stalled(map, c, plant, bed, daytime);
					if (why != null)
					{
						stalled++;
						Count(stalledWhy, why);
					}
					continue;
				}
				empty++;
				string reason = Empty(map, c, crop, bed, sower, research);
				if (reason == "waiting to be sown" && !firstWaiting.IsValid)
				{
					firstWaiting = c;
				}
				Count(emptyWhy, reason);
			}
			if (empty == 0 && stalled == 0)
			{
				return null;
			}

			string text = "[REMINDER] Your " + bed.what + " (" + crop.label + ", " + bed.cells.Count + (bed.cells.Count == 1 ? " cell" : " cells") + ", at "
				+ AgentWorkspotCheck.Xz(bed.cells[0]) + ") " + (bed.plural ? "have " : "has ");
			List<string> parts = new List<string>();
			if (empty > 0)
			{
				parts.Add(empty + (empty == 1 ? " cell" : " cells") + " growing nothing (" + Reasons(emptyWhy) + ")");
			}
			if (stalled > 0)
			{
				parts.Add(stalled + (stalled == 1 ? " plant" : " plants") + " not growing (" + Reasons(stalledWhy) + ")");
			}
			text += string.Join(" and ", parts.ToArray()) + ".";
			if (bed.sowOff && bed.zoneName != null)
			{
				text += " To sow it: 'zone " + bed.zoneName + " allow sowing.'";
			}
			else if (firstWaiting.IsValid && sower != null)
			{
				text += " To sow it first: 'prioritize sowing at " + AgentWorkspotCheck.Xz(firstWaiting) + " with " + sower.LabelShort + ".'";
			}
			return text;
		}

		// Why an empty cell is not sown, in WorkGiver_GrowerSow's order.
		private static string Empty(Map map, IntVec3 c, ThingDef crop, Bed bed, Pawn sower, ResearchProjectDef research)
		{
			if (bed.sowOff)
			{
				return "sowing is turned off for the zone";
			}
			if (bed.noPower)
			{
				return "no power";
			}
			if (!PlantUtility.GrowthSeasonNow(c, map, crop))
			{
				return TemperatureText(map, c, crop);
			}
			if (research != null)
			{
				return "needs the research " + research.label;
			}
			Thing blocker;
			AcceptanceReport can = crop.CanEverPlantAt(c, map, out blocker, true, false);
			if (!can.Accepted)
			{
				if (blocker != null)
				{
					return "blocked by " + blocker.LabelShort;
				}
				return can.Reason.NullOrEmpty() ? "the crop can't go there" : can.Reason.TrimEnd('.').ToLowerInvariant();
			}
			if (sower == null)
			{
				return crop.plant.sowMinSkill > 0
					? "no colonist with Growing on and Plants skill " + crop.plant.sowMinSkill + " or more"
					: "no colonist with Growing on";
			}
			return "waiting to be sown";
		}

		// Why a planted crop does not grow now, or null.
		private static string Stalled(Map map, IntVec3 c, Plant plant, Bed bed, bool daytime)
		{
			if (plant.LifeStage == PlantLifeStage.Mature)
			{
				return "mature, waiting for harvest";
			}
			if (plant.Blighted)
			{
				return "blighted";
			}
			if (bed.noPower)
			{
				return "no power, the plants rot";
			}
			if (!PlantUtility.GrowthSeasonNow(c, map, plant.def))
			{
				return TemperatureText(map, c, plant.def);
			}
			if (plant.Dying)
			{
				return "dying";
			}
			if (daytime && plant.GrowthRateFactor_Light <= 0f)
			{
				return "too dark: under " + plant.def.plant.growMinGlow.ToStringPercent() + " light";
			}
			return null;
		}

		private static string TemperatureText(Map map, IntVec3 c, ThingDef crop)
		{
			float t = c.GetTemperature(map);
			return (t <= crop.plant.minGrowthTemperature ? "too cold" : "too hot") + " (" + t.ToString("0") + "C; " + crop.label + " grows between "
				+ crop.plant.minGrowthTemperature.ToString("0") + "C and " + crop.plant.maxGrowthTemperature.ToString("0") + "C)";
		}

		private static void Count(Dictionary<string, int> d, string key)
		{
			int n;
			d.TryGetValue(key, out n);
			d[key] = n + 1;
		}

		private static string Reasons(Dictionary<string, int> d)
		{
			List<KeyValuePair<string, int>> sorted = d.OrderByDescending((KeyValuePair<string, int> kv) => kv.Value).ToList();
			List<string> rows = sorted.Take(MaxReasons).Select((KeyValuePair<string, int> kv) => kv.Value + " " + kv.Key).ToList();
			if (sorted.Count > MaxReasons)
			{
				rows.Add(sorted.Skip(MaxReasons).Sum((KeyValuePair<string, int> kv) => kv.Value) + " other");
			}
			return string.Join(", ", rows.ToArray());
		}

		private static string RoomName(Room room)
		{
			string name = VocabularyText.RoomLabel(room);
			return string.IsNullOrEmpty(name) ? "room" : name;
		}
	}
}
