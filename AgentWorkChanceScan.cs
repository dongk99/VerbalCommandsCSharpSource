using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace VerbalCommands
{
	// Colonists likely to botch construction or harvests, in the orchestrator's 6-hour reminder batch
	// (AgentReminderScan). User, 2026-10-14: "see if any of the logs have been recording botched harvested/construction.
	// if not, make a recorder that says who failed, at what skill level & health condition, and set up a reminder
	// telling orchestrator to assign better pawns. fires every 6hr." / "game should already calculate construction
	// fail/botched harvest chance independent of harmony. Not sure how well polling would detect it."
	//   No log had them (router logs, outbox and Player.log searched 2026-10-14): a failed build is only a screen
	//   message above 1400 work (Frame.cs:433-436), a failed harvest only a text mote (JobDriver_PlantWork.cs:72). So,
	//   as the user said, the game's own chances, read per colonist, no event needed:
	//   construction: JobDriver_ConstructFinishFrame.cs:61-62 fails a tick with 1 - s^(work/total), s = the stat
	//   ConstructSuccessChance; over one whole build that is a fail chance of 1 - s.
	//   harvest: JobDriver_PlantWork.cs:68-73 wastes the plant's whole yield when Rand.Value > PlantHarvestYield (crops
	//   with harvestFailable, not blighted; drugs use DrugHarvestYield and are left out). Harvesting is Growing work for
	//   zones and Plant cutting for marked wild plants, so both count.
	//   Both stats (Stats_Pawns_WorkGeneral.xml) come from the skill (construction 75% at 0 to 100% at 8; harvest 60% at
	//   0 to 100% at 8) times Manipulation (weight 0.3) and Sight (0.2); GetStatValue holds all of it.
	// Fires when a colonist with the work on fails 10% or more; names the skill, the stat's capacities under 100% and
	// the colonist's visible bad health conditions, and the colonist who does it best, with the router's own orders
	// "set WORKTYPE priority N for PAWNS." and "stop PAWNS doing WORKTYPE." (WORKTYPE = labelShort, VocabularyText).
	internal static class AgentWorkChanceScan
	{
		private const float FailAt = 0.10f;
		private const float BetterBy = 0.05f;
		private const int MaxConditions = 3;

		private class Check
		{
			public string title;
			public string verb;           // "fails about N% of builds"
			public string loss;
			public StatDef stat;
			public SkillDef skill;
			public WorkTypeDef[] works;   // the first is the one to assign
		}

		private static Check ConstructionCheck()
		{
			return new Check { title = "Construction", verb = "of builds", loss = "A failed build loses about half its materials and all the work done.",
				stat = StatDefOf.ConstructSuccessChance, skill = SkillDefOf.Construction, works = new[] { WorkTypeDefOf.Construction } };
		}

		public static List<string> Lines(Map map)
		{
			List<Check> checks = new List<Check>
			{
				ConstructionCheck(),
				new Check { title = "Harvesting", verb = "of crop harvests", loss = "A failed harvest loses that plant's whole yield.",
					stat = StatDefOf.PlantHarvestYield, skill = SkillDefOf.Plants, works = new[] { WorkTypeDefOf.Growing, WorkTypeDefOf.PlantCutting } }
			};
			List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned.Where((Pawn p) => p.skills != null && p.workSettings != null && p.workSettings.EverWork).ToList();
			List<string> lines = new List<string>();
			string failed = FailureLine(map, pawns);
			if (failed != null)
			{
				lines.Add(failed);
			}
			foreach (Check check in checks)
			{
				string line = CheckLine(pawns, check);
				if (line != null)
				{
					lines.Add(line);
				}
			}
			return lines;
		}

		// ---- builds that actually failed, from the game's own messages ------------------------------------------------
		// User, 2026-10-15: "that oughta save onto .rws. then you can have a scanner read autosave, and use the result as
		// polling. and we already made a reminder for it." The game's "construction failed" message (ConstructionFailPatch
		// makes it fire for every build over 1 work) is a historical message: it sits in Find.Archive, which is exactly what
		// the save writes (History.cs:71, Archive.cs:18). Reading Find.Archive gives the same entries without waiting for an
		// autosave or parsing the file. Each 6-hour scan takes the messages made after the last scan (or the load): a
		// NegativeEvent Message whose text is the game's MessageConstructionFailed line for one of the colonists (the line
		// is rebuilt with each colonist's name and a marker for the building, Frame.cs:435), on this map. Named with the
		// colonist's skill and health now (Condition), and the colonist who builds best, as in CheckLine.
		private static int failSince = -1;

		public static void MarkScanned(int tick)
		{
			failSince = tick;
		}

		private static string FailureLine(Map map, List<Pawn> pawns)
		{
			if (failSince < 0 || Find.Archive == null || pawns.Count == 0)
			{
				return null;
			}
			Dictionary<Pawn, List<string>> byPawn = new Dictionary<Pawn, List<string>>();
			foreach (Message m in Find.Archive.ArchivablesListForReading.OfType<Message>())
			{
				if (m.startingTick <= failSince || m.def != MessageTypeDefOf.NegativeEvent || string.IsNullOrEmpty(m.text))
				{
					continue;
				}
				if (m.lookTargets != null && m.lookTargets.IsValid() && m.lookTargets.PrimaryTarget.Map != null && m.lookTargets.PrimaryTarget.Map != map)
				{
					continue;
				}
				string text = m.text.StripTags();
				foreach (Pawn p in pawns)
				{
					string building = BuildingIn(text, p);
					if (building == null)
					{
						continue;
					}
					List<string> list;
					if (!byPawn.TryGetValue(p, out list))
					{
						byPawn[p] = list = new List<string>();
					}
					list.Add(building);
					break;
				}
			}
			if (byPawn.Count == 0)
			{
				return null;
			}
			Check check = ConstructionCheck();
			List<KeyValuePair<Pawn, List<string>>> sorted = byPawn.OrderByDescending((KeyValuePair<Pawn, List<string>> kv) => kv.Value.Count).ToList();
			foreach (KeyValuePair<Pawn, List<string>> kv in sorted)
			{
				HarnessLog.Note("botched builds", kv.Key.LabelShort + " " + kv.Value.Count + "x (" + string.Join(", ", kv.Value.Distinct().ToArray()) + ")");
			}
			string who = string.Join("; ", sorted.Select((KeyValuePair<Pawn, List<string>> kv) => kv.Key.LabelShort + " " + kv.Value.Count + "x ("
				+ string.Join(", ", kv.Value.GroupBy((string b) => b).Select((IGrouping<string, string> g) => g.Key + (g.Count() > 1 ? " x" + g.Count() : "")).ToArray())
				+ "; now " + Condition(kv.Key, check) + ", fails about " + Fail(kv.Key, check.stat).ToStringPercent() + ")").ToArray());
			string text2 = "[REMINDER] Botched builds since the last check (the game's own messages): " + who + ". Each failure lost about half its materials.";
			Pawn best = pawns.Where((Pawn p) => !byPawn.ContainsKey(p) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)).OrderBy((Pawn p) => Fail(p, check.stat)).FirstOrDefault();
			Pawn worst = sorted[0].Key;
			if (best != null && Fail(best, check.stat) <= Fail(worst, check.stat) - BetterBy)
			{
				text2 += " " + best.LabelShort + " builds better (fails about " + Fail(best, check.stat).ToStringPercent() + "): 'set " + WorkTypeDefOf.Construction.labelShort
					+ " priority 1 for " + best.LabelShort + ".' and 'stop " + worst.LabelShort + " doing " + WorkTypeDefOf.Construction.labelShort + ".'";
			}
			return text2;
		}

		// The building of a "construction failed" line for this worker, or null when the line is something else.
		private static string BuildingIn(string text, Pawn p)
		{
			const string Marker = "\u0001";
			string line = ((string)"MessageConstructionFailed".Translate(Marker, p.LabelShort, p.Named("WORKER"))).StripTags().CapitalizeFirst();
			int at = line.IndexOf(Marker, StringComparison.Ordinal);
			if (at < 0)
			{
				return null;
			}
			string before = line.Substring(0, at);
			string after = line.Substring(at + Marker.Length);
			if (text.Length <= before.Length + after.Length || !text.StartsWith(before, StringComparison.Ordinal) || !text.EndsWith(after, StringComparison.Ordinal))
			{
				return null;
			}
			return text.Substring(before.Length, text.Length - before.Length - after.Length);
		}

		private static float Fail(Pawn p, StatDef stat)
		{
			return 1f - Mathf.Clamp01(p.GetStatValue(stat));
		}

		private static string CheckLine(List<Pawn> pawns, Check check)
		{
			WorkTypeDef main = check.works[0];
			List<Pawn> bad = pawns.Where((Pawn p) => check.works.Any((WorkTypeDef w) => !p.WorkTypeIsDisabled(w) && p.workSettings.WorkIsActive(w))
				&& Fail(p, check.stat) >= FailAt - 0.0005f).OrderByDescending((Pawn p) => Fail(p, check.stat)).ToList();
			if (bad.Count == 0)
			{
				return null;
			}
			foreach (Pawn p in bad)
			{
				HarnessLog.Note("botched builds", check.title + ": " + p.LabelShort + " fails about " + Fail(p, check.stat).ToStringPercent());
			}
			string text = "[REMINDER] " + check.title + ": " + string.Join("; ", bad.Select((Pawn p) => p.LabelShort + " fails about " + Fail(p, check.stat).ToStringPercent()
				+ " " + check.verb + " (" + Condition(p, check) + ")").ToArray()) + ". " + check.loss;

			Pawn best = pawns.Where((Pawn p) => !bad.Contains(p) && !p.WorkTypeIsDisabled(main)).OrderBy((Pawn p) => Fail(p, check.stat)).FirstOrDefault();
			if (best == null || Fail(best, check.stat) > Fail(bad[bad.Count - 1], check.stat) - BetterBy)
			{
				return text + " No colonist does it clearly better now; the skill rises with practice, a builder or grower can be recruited, and healing"
					+ " lifts manipulation and sight.";
			}
			bool bestOn = best.workSettings.WorkIsActive(main);
			text += " " + best.LabelShort + " does better (fails about " + Fail(best, check.stat).ToStringPercent() + ", " + check.skill.label + " skill "
				+ best.skills.GetSkill(check.skill).Level + (bestOn ? ", " + main.labelShort + " priority " + best.workSettings.GetPriority(main) : ", " + main.labelShort + " off") + ").";
			string worst = bad[0].LabelShort;
			return text + " Consider assigning better pawns: 'set " + main.labelShort + " priority 1 for " + best.LabelShort + ".'"
				+ " and 'stop " + worst + " doing " + main.labelShort + ".' (or a lower priority for " + worst + ").";
		}

		// "Construction skill 1; manipulation 80%; health: scratch (left hand), cataract (right eye)".
		private static string Condition(Pawn p, Check check)
		{
			List<string> parts = new List<string>();
			SkillRecord skill = p.skills.GetSkill(check.skill);
			parts.Add(check.skill.LabelCap + " skill " + skill.Level + (skill.passion != Passion.None ? " (" + skill.passion.ToString().ToLowerInvariant() + " passion)" : ""));
			if (check.stat.capacityFactors != null)
			{
				List<string> caps = new List<string>();
				foreach (PawnCapacityFactor f in check.stat.capacityFactors)
				{
					float level = p.health.capacities.GetLevel(f.capacity);
					if (level < 0.995f)
					{
						caps.Add(f.capacity.label + " " + level.ToStringPercent());
					}
				}
				if (caps.Count > 0)
				{
					parts.Add(string.Join(", ", caps.ToArray()));
				}
			}
			List<string> conditions = p.health.hediffSet.hediffs.Where((Hediff h) => h.Visible && h.def.isBad)
				.Select((Hediff h) => h.LabelCap + (h.Part != null ? " (" + h.Part.Label + ")" : "")).Distinct().ToList();
			if (conditions.Count > 0)
			{
				parts.Add("health: " + string.Join(", ", conditions.Take(MaxConditions).ToArray()) + (conditions.Count > MaxConditions ? " and " + (conditions.Count - MaxConditions) + " more" : ""));
			}
			else
			{
				parts.Add("healthy");
			}
			return string.Join("; ", parts.ToArray());
		}
	}
}
