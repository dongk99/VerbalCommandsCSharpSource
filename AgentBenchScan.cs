using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Benches against the colonists who work at them, and a stonecutter's table for stockpiled chunks, in the
	// orchestrator's 6-hour reminder batch (AgentReminderScan). User, 2026-10-14: "have a reminder inject for
	// workbenches. if pawns are assigned on doing a work, and their schedules overlap, [reminder]: you have [n_pawn]
	// assigned to [work_category], but [production_bench_related_to_jobs] arent enough to cover it. art: art bench.
	// research: research bench + scanners + drug lab + dlc component. cooking: stove (both type), butcher spot,
	// campfire, stoves (both type). excluding unfueld ones. tailor: tailor bench (both kinds). crafting: all smithy
	// benches (smithy, fabrication bench, machining table), stonecutter's table. If colony has chunks with stockpilezone,
	// but has no stonecutter table: reminder on stonecutter table if there is a stockpilezone containing CHUNKS, but no
	// stonecutter table present."
	//
	// Which bench belongs to which work is read from the game's work givers, not a name list, so DLC benches come in by
	// themselves: every WorkGiverDef of the work type with fixedBillGiverDefs (the bill benches) or scannerDef (the
	// long-range mineral and ground-penetrating scanners), RimWorld\WorkGiverDef.cs:52, :72; for research also every
	// research bench (WorkGiver_Researcher takes any Building_ResearchBench). In the game's data (WorkGivers.xml) the
	// smithy, machining table and fabrication bench are Smithing work and the drug lab is Crafting work, not Research,
	// so Smithing gets its own line and the drug lab counts for Crafting; Cooking also has the brewery, Crafting the
	// crafting spot, biofuel refinery and electric smelter.
	//   Pawns: free colonists with the work type on (Pawn_WorkSettings.WorkIsActive, not disabled for them). "At the
	//   same hours": for each hour of the day, those whose schedule (Pawn_TimetableTracker.GetAssignment) is Work or
	//   Anything; the line fires when the busiest hour has more of them than usable benches.
	//   Usable bench: built, of the colony; one that burns fuel has fuel (CompRefuelable.HasFuel), one that takes power
	//   has it (CompPowerTrader.PowerOn). The ones left out are named.
	//   Stonecutter: a stockpile zone holds a stone chunk (ThingCategoryDefOf.StoneChunks) and the colony has no
	//   stonecutter's table (the DoBillsStonecut benches), built, framed or blueprinted.
	internal static class AgentBenchScan
	{
		private static readonly string[] WorkTypeNames = { "Cooking", "Research", "Art", "Tailoring", "Crafting", "Smithing" };
		private const int MaxNames = 6;

		private static List<KeyValuePair<WorkTypeDef, HashSet<ThingDef>>> benchesByWork;
		private static HashSet<ThingDef> stonecutters;

		private static List<KeyValuePair<WorkTypeDef, HashSet<ThingDef>>> BenchesByWork()
		{
			if (benchesByWork != null)
			{
				return benchesByWork;
			}
			List<KeyValuePair<WorkTypeDef, HashSet<ThingDef>>> list = new List<KeyValuePair<WorkTypeDef, HashSet<ThingDef>>>();
			foreach (string name in WorkTypeNames)
			{
				WorkTypeDef wt = DefDatabase<WorkTypeDef>.GetNamedSilentFail(name);
				if (wt == null)
				{
					continue;
				}
				HashSet<ThingDef> defs = new HashSet<ThingDef>();
				foreach (WorkGiverDef g in DefDatabase<WorkGiverDef>.AllDefsListForReading)
				{
					if (g.workType != wt)
					{
						continue;
					}
					if (g.fixedBillGiverDefs != null)
					{
						defs.UnionWith(g.fixedBillGiverDefs.Where((ThingDef d) => d != null));
					}
					if (g.scannerDef != null)
					{
						defs.Add(g.scannerDef);
					}
				}
				if (name == "Research")
				{
					defs.UnionWith(DefDatabase<ThingDef>.AllDefsListForReading.Where((ThingDef d) => d.thingClass != null && typeof(Building_ResearchBench).IsAssignableFrom(d.thingClass)));
				}
				list.Add(new KeyValuePair<WorkTypeDef, HashSet<ThingDef>>(wt, defs));
			}
			benchesByWork = list;
			return list;
		}

		private static HashSet<ThingDef> Stonecutters()
		{
			if (stonecutters == null)
			{
				WorkGiverDef g = DefDatabase<WorkGiverDef>.GetNamedSilentFail("DoBillsStonecut");
				stonecutters = new HashSet<ThingDef>(g != null && g.fixedBillGiverDefs != null ? g.fixedBillGiverDefs.Where((ThingDef d) => d != null) : Enumerable.Empty<ThingDef>());
			}
			return stonecutters;
		}

		public static List<string> Lines(Map map)
		{
			List<string> lines = WorkLines(map);
			lines.AddRange(StonecutterLines(map));
			return lines;
		}

		// The bench lines alone: the hourly check takes them (AgentHourlyCheck.cs), the stonecutter line stays in the 6-hour one.
		public static List<string> WorkLines(Map map)
		{
			List<string> lines = new List<string>();
			List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned.ToList();
			foreach (KeyValuePair<WorkTypeDef, HashSet<ThingDef>> kv in BenchesByWork())
			{
				string line = WorkLine(map, pawns, kv.Key, kv.Value);
				if (line != null)
				{
					lines.Add(line);
				}
			}
			return lines;
		}

		public static List<string> StonecutterLines(Map map)
		{
			List<string> lines = new List<string>();
			string stone = StonecutterLine(map);
			if (stone != null)
			{
				lines.Add(stone);
			}
			return lines;
		}

		private static string WorkLine(Map map, List<Pawn> pawns, WorkTypeDef wt, HashSet<ThingDef> benchDefs)
		{
			if (benchDefs.Count == 0)
			{
				return null;
			}
			List<Pawn> assigned = pawns.Where((Pawn p) => p.workSettings != null && p.workSettings.EverWork && !p.WorkTypeIsDisabled(wt)
				&& p.workSettings.WorkIsActive(wt)).ToList();
			if (assigned.Count < 2)
			{
				return null;
			}
			// The busiest hour: how many of them may work then.
			int most = 0;
			List<Pawn> mostPawns = null;
			for (int hour = 0; hour < GenDate.HoursPerDay; hour++)
			{
				List<Pawn> now = assigned.Where((Pawn p) => MayWork(p, hour)).ToList();
				if (now.Count > most)
				{
					most = now.Count;
					mostPawns = now;
				}
			}
			if (most < 2)
			{
				return null;
			}
			Dictionary<ThingDef, int> usable = new Dictionary<ThingDef, int>();
			Dictionary<string, int> leftOut = new Dictionary<string, int>();
			int usableCount = 0;
			List<string> usableAt = new List<string>();   // for the harness log (HarnessLog.cs, H3)
			foreach (Building b in map.listerBuildings.allBuildingsColonist)
			{
				if (!b.Spawned || !benchDefs.Contains(b.def))
				{
					continue;
				}
				string why = Unusable(b);
				if (why != null)
				{
					string key = b.def.label + " (" + why + ")";
					int n;
					leftOut.TryGetValue(key, out n);
					leftOut[key] = n + 1;
					continue;
				}
				int u;
				usable.TryGetValue(b.def, out u);
				usable[b.def] = u + 1;
				usableCount++;
				usableAt.Add(b.def.label + " (" + AgentWorkspotCheck.Xz(b.Position) + ")");
			}
			if (most <= usableCount)
			{
				return null;
			}
			string work = wt.gerundLabel.NullOrEmpty() ? wt.label : wt.gerundLabel;
			HarnessLog.Note("benches", work + ": " + most + " pawns, " + (usableAt.Count == 0 ? "no usable bench" : string.Join(", ", usableAt.ToArray())));
			string names = string.Join(", ", mostPawns.Take(MaxNames).Select((Pawn p) => p.LabelShort).ToArray()) + (mostPawns.Count > MaxNames ? " and " + (mostPawns.Count - MaxNames) + " more" : "");
			string benches;
			if (usableCount == 0)
			{
				benches = "no usable bench for it (" + string.Join(", ", benchDefs.Select((ThingDef d) => d.label).Distinct().OrderBy((string s) => s).ToArray()) + ")";
			}
			else
			{
				benches = usableCount + (usableCount == 1 ? " bench" : " benches") + " ("
					+ string.Join(", ", usable.Select((KeyValuePair<ThingDef, int> u) => u.Value + " " + u.Key.label).ToArray()) + ")";
			}
			string left = leftOut.Count > 0
				? " Left out: " + string.Join(", ", leftOut.Select((KeyValuePair<string, int> l) => l.Value + " " + l.Key).ToArray()) + "."
				: "";
			return "[REMINDER] You have " + most + " pawns assigned to " + work + " at the same hours (" + names + "), but " + benches
				+ (usableCount == 0 ? " to cover it." : (usableCount == 1 ? " isn't" : " aren't") + " enough to cover it.") + left;
		}

		// Work or Anything at that hour of the schedule.
		private static bool MayWork(Pawn p, int hour)
		{
			if (p.timetable == null)
			{
				return true;
			}
			TimeAssignmentDef a = p.timetable.GetAssignment(hour);
			return a == TimeAssignmentDefOf.Work || a == TimeAssignmentDefOf.Anything;
		}

		// Why a bench can not be worked now, or null.
		private static string Unusable(Building b)
		{
			CompRefuelable fuel = b.TryGetComp<CompRefuelable>();
			if (fuel != null && !fuel.HasFuel)
			{
				return "no fuel";
			}
			CompPowerTrader power = b.TryGetComp<CompPowerTrader>();
			if (power != null && !power.PowerOn)
			{
				return "no power";
			}
			return null;
		}

		private static string StonecutterLine(Map map)
		{
			HashSet<ThingDef> cutters = Stonecutters();
			if (cutters.Count == 0)
			{
				return null;
			}
			foreach (Building b in map.listerBuildings.allBuildingsColonist)
			{
				if (cutters.Contains(b.def))
				{
					return null;
				}
			}
			foreach (ThingRequestGroup group in new ThingRequestGroup[] { ThingRequestGroup.BuildingFrame, ThingRequestGroup.Blueprint })
			{
				foreach (Thing t in map.listerThings.ThingsInGroup(group))
				{
					if (t.Faction == Faction.OfPlayer && cutters.Contains(t.def.entityDefToBuild as ThingDef))
					{
						return null;
					}
				}
			}
			ThingCategoryDef chunks = ThingCategoryDefOf.StoneChunks;
			List<string> zones = new List<string>();
			int total = 0;
			foreach (Zone z in map.zoneManager.AllZones)
			{
				Zone_Stockpile stockpile = z as Zone_Stockpile;
				if (stockpile == null)
				{
					continue;
				}
				int n = stockpile.AllContainedThings.Count((Thing t) => t.def.thingCategories != null && t.def.thingCategories.Any((ThingCategoryDef c) => c == chunks || c.Parents.Contains(chunks)));
				if (n > 0)
				{
					zones.Add(n + " in " + stockpile.label);
					total += n;
				}
			}
			if (total == 0)
			{
				return null;
			}
			return "[REMINDER] You have stone chunks in a stockpile zone (" + string.Join(", ", zones.ToArray()) + "), but no stonecutter's table. "
				+ "A stonecutter's table cuts chunks into stone blocks, for walls and floors that do not burn.";
		}
	}
}
