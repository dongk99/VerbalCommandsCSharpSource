using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Agent 05, brief A: read-back questions about one colonist or all of them (read-only; MAIN THREAD ONLY like every query).
	// Hook (added later by another agent, in FormQueries.cs Query, next to QueryGear):
	//     QueryReply rbPawns = QueryReadbackPawns(c); if (rbPawns != null) { return rbPawns; }
	//   query work of       the Work tab row of one colonist (RimWorld\MainTabWindow_Work.cs:8, one PawnColumnWorker_WorkPriority
	//                       cell per visible work type, RimWorld\PawnColumnWorker_WorkPriority.cs:16-30 -> WidgetsWork.DrawWorkBoxFor,
	//                       RimWorld\WidgetsWork.cs:66-98)
	//   query work          the same grid for every free colonist (MainTabWindow_PawnTable.Pawns = FreeColonists,
	//                       RimWorld\MainTabWindow_PawnTable.cs:32; babies left out, MainTabWindow_Work.cs:15)
	//   query skills        the Character tab (RimWorld\SkillUI.cs:51-128 skills, RimWorld\CharacterCardUtility.cs:1106-1169 traits,
	//                       :1174-1176 "Incapable of")
	//   query schedule of / query schedule   the Schedule tab (RimWorld\PawnColumnWorker_Timetable.cs:12-27, one cell per hour)
	//   query area of       the Assign tab allowed area (RimWorld\PawnColumnWorker_AllowedArea.cs:30-34, AreaAllowedGUI.cs:51)
	//   query bed of        the Assign tab bed (Pawn_Ownership.OwnedBed, RimWorld\Pawn_Ownership.cs:12-17)
	//   query settings of   drafted / fire at will / threat response / medical care / self-tend
	// No getter here writes anything into the game. Pawn_WorkSettings.GetPriority is only called after EverWork is true
	// (it calls ConfirmInitializedDebug, RimWorld\Pawn_WorkSettings.cs:131-137,160-168, which logs an error and
	// initializes the settings when they are missing). Pawn.GetDisabledWorkTypes (Verse\Pawn.cs:4192) and the
	// SkillRecord / Def label getters only fill caches the Work and Character tabs fill themselves.
	public static partial class FormPlanner
	{
		// A reply never runs past this many lines (brief rule 6).
		private const int RbPawnMaxLines = 60;
		// A line of the work grid never runs past this many characters (brief rule 6).
		private const int RbPawnMaxWidth = 100;

		// null when c.form is not one of these.
		internal static QueryReply QueryReadbackPawns(FormContext c)
		{
			switch (c.form)
			{
				case "query work of":
					return QueryReply.Ok(RbPawnWorkOf(c));
				case "query work":
					return QueryReply.Ok(RbPawnWorkAll(c));
				case "query skills":
					return QueryReply.Ok(RbPawnSkills(c));
				case "query schedule of":
					return QueryReply.Ok(RbPawnScheduleOf(c));
				case "query schedule":
					return QueryReply.Ok(RbPawnScheduleAll(c));
				case "query area of":
					return QueryReply.Ok(RbPawnAreaOf(c));
				case "query bed of":
					return QueryReply.Ok(RbPawnBedOf(c));
				case "query settings of":
					return QueryReply.Ok(RbPawnSettingsOf(c));
			}
			return null;
		}

		// ---- shared bits -----------------------------------------------------------------------------------------------

		// The PAWN slot: one free colonist of the map (FindPawn walks map.mapPawns.FreeColonists).
		private static Pawn RbPawnPick(FormContext c)
		{
			string name = c.Text("PAWN");
			if (string.IsNullOrEmpty(name))
			{
				throw new Fault("The question did not say which colonist.");
			}
			Pawn pawn = FindPawn(c.map, name);
			if (pawn == null)
			{
				throw new Fault("There is no colonist called '" + name + "'.");
			}
			return pawn;
		}

		// Every free colonist of the map, as "query colonists" has them (Verse MapPawns.FreeColonists).
		private static List<Pawn> RbPawnEveryone(FormContext c)
		{
			List<Pawn> list = c.map.mapPawns.FreeColonists.ToList();
			if (list.Count == 0)
			{
				throw new Fault("There are no colonists on this map.");
			}
			return list;
		}

		private static bool RbPawnIsBaby(Pawn p)
		{
			// Verse\DevelopmentalStageExtensions.cs:92-94
			return p.DevelopmentalStage.Baby();
		}

		// A map cell as the player may see it: nothing for a cell under fog (brief rule 3).
		private static string RbPawnCellText(Map map, IntVec3 cell)
		{
			return AgentPlace.Visible(map, cell) ? AgentPlace.Describe(map, cell) : "an unseen spot";
		}

		// Cuts a reply to RbPawnMaxLines lines; the last line says how to ask for less.
		private static string RbPawnJoinLines(List<string> lines, string howToAskForLess)
		{
			if (lines.Count > RbPawnMaxLines)
			{
				int keep = RbPawnMaxLines - 1;
				int cut = lines.Count - keep;
				lines = lines.Take(keep).ToList();
				lines.Add("... " + cut + " more; " + howToAskForLess);
			}
			return string.Join("\n", lines.ToArray());
		}

		// "prefix" + items joined by ", ", broken into lines of at most RbPawnMaxWidth characters (continuation lines
		// start with two spaces).
		private static void RbPawnWrapInto(List<string> lines, string prefix, List<string> items)
		{
			StringBuilder line = new StringBuilder(prefix);
			bool first = true;
			foreach (string item in items)
			{
				string piece = (first ? "" : ", ") + item;
				if (!first && line.Length + piece.Length + 1 > RbPawnMaxWidth)
				{
					line.Append(",");
					lines.Add(line.ToString());
					line = new StringBuilder("  " + item);
				}
				else
				{
					line.Append(piece);
				}
				first = false;
			}
			lines.Add(line.ToString());
		}

		// ---- work -------------------------------------------------------------------------------------------------------

		// The work types the Work tab has a column for, left to right. PawnColumnDefGenerator.ImpliedPawnColumnDefs
		// (RimWorld\PawnColumnDefGenerator.cs:30-43) goes through WorkTypeDefsInPriorityOrder (Verse\WorkTypeDefsUtility.cs:8:
		// naturalPriority, highest first) with visible work types (Verse\WorkTypeDef.cs:24), reversed, and inserts each one
		// right after the copy/paste column, so the final order is naturalPriority highest first. A column shows only while
		// WorkTypeDef.VisibleCurrently (PawnColumnWorker_WorkPriority.cs:14), the same test as VisibleNow
		// (Verse\WorkTypeDef.cs:47-85).
		private static List<WorkTypeDef> RbPawnWorkColumns()
		{
			List<WorkTypeDef> list = new List<WorkTypeDef>();
			foreach (WorkTypeDef def in WorkTypeDefsUtility.WorkTypeDefsInPriorityOrder)
			{
				if (def.visible && def.VisibleNow())
				{
					list.Add(def);
				}
			}
			return list;
		}

		private const int RbPawnNoSettings = -2;
		private const int RbPawnIncapable = -1;

		// What the Work tab cell of this pawn and work type shows: RbPawnNoSettings (empty cell, PawnColumnWorker_WorkPriority
		// .cs:20-23), RbPawnIncapable (no box, WidgetsWork.cs:68-81), or the priority 0-4 (WidgetsWork.cs:89-98).
		// With manual priorities off GetPriority gives 3 for every active work of a humanlike (Pawn_WorkSettings.cs:164-167).
		private static int RbPawnWorkState(Pawn p, WorkTypeDef w)
		{
			if (p.workSettings == null || !p.workSettings.EverWork)
			{
				return RbPawnNoSettings;
			}
			if (p.WorkTypeIsDisabled(w))
			{
				return RbPawnIncapable;
			}
			return p.workSettings.GetPriority(w);
		}

		private static string RbPawnWorkWord(string labelShort)
		{
			return UpperFirst(labelShort);
		}

		private static string RbPawnWorkOf(FormContext c)
		{
			Pawn pawn = RbPawnPick(c);
			if (RbPawnIsBaby(pawn))
			{
				throw new Fault(pawn.LabelShort + " is a baby and has no work settings.");
			}
			if (pawn.workSettings == null || !pawn.workSettings.EverWork)
			{
				throw new Fault(pawn.LabelShort + " has no work settings.");
			}
			// Find.PlaySettings.useWorkPriorities is the "Manual priorities" checkbox of the Work tab
			// (RimWorld\MainTabWindow_Work.cs:46-48).
			bool manual = Find.PlaySettings.useWorkPriorities;
			List<string> lines = new List<string>();
			lines.Add(pawn.LabelShort + ": work priorities (manual priorities " + (manual ? "on). 1 is highest." : "off: each work is just on or off)."));
			foreach (WorkTypeDef def in RbPawnWorkColumns())
			{
				int state = RbPawnWorkState(pawn, def);
				string value;
				if (state == RbPawnIncapable)
				{
					value = "cannot (incapable)";
				}
				else if (state <= 0)
				{
					value = "off";
				}
				else
				{
					value = manual ? state.ToString() : "on";
				}
				lines.Add(RbPawnWorkWord(def.labelShort) + ": " + value);
			}
			return RbPawnJoinLines(lines, "ask for fewer: show skills of " + pawn.LabelShort + ".");
		}

		private static string RbPawnWorkAll(FormContext c)
		{
			List<Pawn> pawns = RbPawnEveryone(c).Where((Pawn p) => !RbPawnIsBaby(p)).ToList();
			if (pawns.Count == 0)
			{
				throw new Fault("There are no colonists with work settings on this map.");
			}
			bool manual = Find.PlaySettings.useWorkPriorities;
			List<string> lines = new List<string>();
			lines.Add(manual
				? "Work priorities (manual priorities on). 1 is highest, - is off, x cannot."
				: "Work (manual priorities off). on is active, - is off, x cannot.");
			foreach (WorkTypeDef def in RbPawnWorkColumns())
			{
				List<string> cells = new List<string>();
				foreach (Pawn pawn in pawns)
				{
					int state = RbPawnWorkState(pawn, def);
					string value;
					if (state == RbPawnIncapable)
					{
						value = "x";
					}
					else if (state == RbPawnNoSettings)
					{
						value = "?";
					}
					else if (state <= 0)
					{
						value = "-";
					}
					else
					{
						value = manual ? state.ToString() : "on";
					}
					cells.Add(pawn.LabelShort + " " + value);
				}
				RbPawnWrapInto(lines, RbPawnWorkWord(def.labelShort) + ": ", cells);
			}
			return RbPawnJoinLines(lines, "ask about one colonist: show work of NAME.");
		}

		// ---- skills -----------------------------------------------------------------------------------------------------

		// Any pawn with skills in view, not only colonists (FormWave1Queries.PawnInView; user, 2026-10-16: "THE MOD BLOCKS
		// SKILL SHOWING FOR SOMEONE NOT COLONIST EVEN WHEN AGENT QUERIES"). The game shows a stranger's Character tab too.
		private static string RbPawnSkills(FormContext c)
		{
			Pawn pawn = PawnInView(c, (Pawn p) => p.skills != null);
			// SkillUI.DrawSkillsOf (RimWorld\SkillUI.cs:51-61): a baby has no skills yet.
			if (RbPawnIsBaby(pawn))
			{
				throw new Fault(pawn.LabelShort + " is a baby: skills develop later.");
			}
			List<string> lines = new List<string>();
			lines.Add(pawn.LabelShort + ": skills");
			if (pawn.skills != null)
			{
				// Skill order: SkillUI.Reset (RimWorld\SkillUI.cs:47), listOrder highest first (RimWorld\SkillDef.cs:21).
				foreach (SkillDef def in DefDatabase<SkillDef>.AllDefs.OrderByDescending((SkillDef sd) => sd.listOrder))
				{
					SkillRecord skill = RbPawnFindSkill(pawn, def);
					if (skill == null)
					{
						continue;
					}
					// Label SkillUI.cs:88, level SkillUI.cs:90 (GetLevel, RimWorld\SkillRecord.cs:334-346), passion icon only
					// when not TotallyDisabled (SkillUI.cs:92-97; SkillRecord.cs:85-98, Passion enum RimWorld\Passion.cs:3-8).
					string label = UpperFirst(def.skillLabel);
					if (skill.TotallyDisabled)
					{
						lines.Add(label + ": cannot (incapable)");
					}
					else
					{
						string passion = skill.passion == Passion.Major ? ", major passion" : (skill.passion == Passion.Minor ? ", minor passion" : "");
						lines.Add(label + ": " + skill.GetLevel() + passion);
					}
				}
			}
			// "Incapable of" list: pawn.CombinedDisabledWorkTags, one entry per flag (RimWorld\CharacterCardUtility.cs:1174-1176,
			// WorkTagsFrom :1450-1459, Gen.GetAllSelectedItems Verse\Gen.cs:52-64), label WorkTags.LabelTranslated
			// (Verse\WorkTypeDefsUtility.cs:10) capitalized (CharacterCardUtility.cs:1226).
			WorkTags disabled = pawn.CombinedDisabledWorkTags;
			List<string> cannot = new List<string>();
			int tagBits = (int)disabled;
			foreach (WorkTags tag in Enum.GetValues(typeof(WorkTags)))
			{
				int bit = (int)tag;
				if (bit != 0 && bit == (tagBits & bit))
				{
					cannot.Add(UpperFirst(tag.LabelTranslated()));
				}
			}
			lines.Add(cannot.Count == 0 ? "Cannot do: nothing." : "Cannot do: " + string.Join(", ", cannot.ToArray()));
			// Traits as the tab lists them: pawn.story.traits.TraitsSorted, Trait.LabelCap (CharacterCardUtility.cs:1114,1162;
			// RimWorld\Trait.cs:36; a suppressed one is drawn greyed, Trait.cs:40).
			List<string> traits = new List<string>();
			if (pawn.story != null && pawn.story.traits != null)
			{
				foreach (Trait trait in pawn.story.traits.TraitsSorted.ToList())
				{
					traits.Add(trait.LabelCap + (trait.Suppressed ? " (suppressed)" : ""));
				}
			}
			lines.Add(traits.Count == 0 ? "Traits: none." : "Traits: " + string.Join(", ", traits.ToArray()));
			return RbPawnJoinLines(lines, "ask for one thing: show work of " + pawn.LabelShort + ".");
		}

		// Pawn_SkillTracker.GetSkill (RimWorld\Pawn_SkillTracker.cs:86-97) logs an error when the def is missing; this does not.
		private static SkillRecord RbPawnFindSkill(Pawn pawn, SkillDef def)
		{
			// Pawn_SkillTracker.skills, RimWorld\Pawn_SkillTracker.cs:14
			List<SkillRecord> all = pawn.skills.skills;
			for (int i = 0; i < all.Count; i++)
			{
				if (all[i].def == def)
				{
					return all[i];
				}
			}
			return null;
		}

		// ---- schedule ---------------------------------------------------------------------------------------------------

		// "0-5 Sleep, 6-21 Anything, 22-23 Sleep": runs of the same assignment over hours 0-23. The cell colour of the
		// Schedule tab is Pawn_TimetableTracker.GetAssignment(hour) (RimWorld\PawnColumnWorker_Timetable.cs:79-82,
		// RimWorld\Pawn_TimetableTracker.cs:51-54); the name of an assignment is its LabelCap, as the tab's selector
		// shows it (RimWorld\TimeAssignmentSelector.cs:46).
		private static string RbPawnRuns(Pawn p)
		{
			if (p.timetable == null || p.timetable.times == null || p.timetable.times.Count < 24)
			{
				return null;
			}
			List<string> runs = new List<string>();
			int start = 0;
			for (int h = 1; h <= 24; h++)
			{
				if (h == 24 || p.timetable.GetAssignment(h) != p.timetable.GetAssignment(start))
				{
					int end = h - 1;
					string span = start == end ? start.ToString() : start + "-" + end;
					runs.Add(span + " " + RbPawnAssignmentLabel(p.timetable.GetAssignment(start)));
					start = h;
				}
			}
			return string.Join(", ", runs.ToArray());
		}

		private static string RbPawnAssignmentLabel(TimeAssignmentDef def)
		{
			return def == null ? "nothing" : def.LabelCap.ToString();
		}

		// Local hour of the map: RimWorld\GenLocalDate.cs:16-19 (a pure read of the game clock and the map's tile).
		private static int RbPawnHourNow(Map map)
		{
			return GenLocalDate.HourOfDay(map);
		}

		private static string RbPawnScheduleOf(FormContext c)
		{
			Pawn pawn = RbPawnPick(c);
			string runs = RbPawnRuns(pawn);
			if (runs == null)
			{
				throw new Fault(pawn.LabelShort + " has no schedule.");
			}
			List<string> lines = new List<string>();
			lines.Add(pawn.LabelShort + ": " + runs);
			int hour = RbPawnHourNow(c.map);
			if (hour >= 0 && hour < 24)
			{
				lines.Add("Now it is hour " + hour + ": " + RbPawnAssignmentLabel(pawn.timetable.GetAssignment(hour)) + ".");
			}
			return RbPawnJoinLines(lines, "ask about one colonist: show schedule of NAME.");
		}

		private static string RbPawnScheduleAll(FormContext c)
		{
			List<string> lines = new List<string>();
			int hour = RbPawnHourNow(c.map);
			if (hour >= 0 && hour < 24)
			{
				lines.Add("Schedule (hours 0-23; now it is hour " + hour + ").");
			}
			foreach (Pawn pawn in RbPawnEveryone(c))
			{
				string runs = RbPawnRuns(pawn);
				lines.Add(pawn.LabelShort + ": " + (runs ?? "no schedule"));
			}
			return RbPawnJoinLines(lines, "ask about one colonist: show schedule of NAME.");
		}

		// ---- area -------------------------------------------------------------------------------------------------------

		// The Assign tab and the inspect pane read Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap
		// (RimWorld\Pawn_PlayerSettings.cs:85-94, AreaAllowedGUI.cs:56, InspectPaneFiller.cs:148); null is the "Unrestricted"
		// button (AreaUtility.AreaAllowedLabel_Area, Verse\AreaUtility.cs:51-58). The cell count is Area.TrueCount
		// (Verse\Area.cs:28), the cells are Area.ActiveCells (Verse\Area.cs:84).
		private static string RbPawnAreaOf(FormContext c)
		{
			Pawn pawn = RbPawnPick(c);
			if (pawn.playerSettings == null)
			{
				throw new Fault(pawn.LabelShort + " has no allowed area setting.");
			}
			Area area = pawn.playerSettings.AreaRestrictionInPawnCurrentMap;
			if (area == null)
			{
				return pawn.LabelShort + ": allowed area Unrestricted (can go anywhere).";
			}
			int minX = int.MaxValue;
			int minZ = int.MaxValue;
			int maxX = int.MinValue;
			int maxZ = int.MinValue;
			foreach (IntVec3 cell in area.ActiveCells)
			{
				if (!AgentPlace.Visible(c.map, cell))
				{
					continue;
				}
				minX = Math.Min(minX, cell.x);
				minZ = Math.Min(minZ, cell.z);
				maxX = Math.Max(maxX, cell.x);
				maxZ = Math.Max(maxZ, cell.z);
			}
			string head = pawn.LabelShort + ": allowed area '" + area.Label + "': " + area.TrueCount + " cells";
			if (maxX < minX)
			{
				return head + ", none of them in view yet.";
			}
			return head + ", from " + RbPawnCellText(c.map, new IntVec3(minX, 0, minZ)) + " to " + RbPawnCellText(c.map, new IntVec3(maxX, 0, maxZ)) + ".";
		}

		// ---- bed --------------------------------------------------------------------------------------------------------

		// Pawn_Ownership.OwnedBed is a plain field read (RimWorld\Pawn_Ownership.cs:12-17). Kind: Building_Bed.ForOwnerType /
		// ForColonists / ForPrisoners / ForSlaves / Medical (RimWorld\Building_Bed.cs:44,76,78,92,110, all plain reads);
		// the owners are CompAssignableToPawn.AssignedPawnsForReading (Building_Bed.cs:40, CompAssignableToPawn.cs:49);
		// the room text is the mod's BuildDefs.RoomText(RoomOfThing(bed)) as FormBills.cs:60-64 uses it.
		private static string RbPawnBedOf(FormContext c)
		{
			Pawn pawn = RbPawnPick(c);
			Building_Bed bed = pawn.ownership != null ? pawn.ownership.OwnedBed : null;
			if (bed == null)
			{
				return pawn.LabelShort + " has no bed.";
			}
			if (!bed.Spawned || bed.Map != c.map)
			{
				return pawn.LabelShort + ": owns " + bed.LabelNoCount + " on another map.";
			}
			StringBuilder sb = new StringBuilder(pawn.LabelShort + ": owns " + bed.LabelNoCount);
			if (AgentPlace.Visible(c.map, bed.Position))
			{
				sb.Append(" at " + AgentPlace.Describe(c.map, bed.Position));
			}
			else
			{
				sb.Append(", location not in view");
			}
			sb.Append(", " + (bed.ForPrisoners ? "prisoner bed" : (bed.ForSlaves ? "slave bed" : "colonist bed")));
			if (bed.Medical)
			{
				sb.Append(", medical");
			}
			string room = BuildDefs.RoomText(RoomOfThing(bed));
			if (room == "outdoors")
			{
				sb.Append(", outdoors");
			}
			else if (room.Length > 0)
			{
				sb.Append(", in room " + room);
			}
			sb.Append(".");
			List<string> others = new List<string>();
			List<Pawn> owners = bed.OwnersForReading;
			for (int i = 0; i < owners.Count; i++)
			{
				if (owners[i] != pawn)
				{
					others.Add(owners[i].LabelShort);
				}
			}
			if (others.Count > 0)
			{
				sb.Append("\nShared with " + JoinNames(others) + ".");
			}
			return sb.ToString();
		}

		// ---- settings ---------------------------------------------------------------------------------------------------

		// drafted: Pawn.Drafted (Verse\Pawn.cs:600-610, a plain read); fire at will: Pawn_DraftController.FireAtWill getter
		// (RimWorld\Pawn_DraftController.cs:104-111, plain read; the Fire at will gizmo shows only while drafted, :190-211);
		// threat response: Pawn_PlayerSettings.hostilityResponse (RimWorld\Pawn_PlayerSettings.cs:29, enum
		// RimWorld\HostilityResponseMode.cs:3-8, the Assign tab column draws it for humanlikes,
		// RimWorld\PawnColumnWorker_HostilityResponse.cs:12-18); medical care: Pawn_PlayerSettings.medCare (:27) with
		// MedicalCareCategory.GetLabel (RimWorld\MedicalCareUtility.cs:68-71); self-tend: Pawn_PlayerSettings.selfTend (:31),
		// shown on the Health tab for non-baby colonists (RimWorld\HealthCardUtility.cs:508-524).
		private static string RbPawnSettingsOf(FormContext c)
		{
			Pawn pawn = RbPawnPick(c);
			if (pawn.playerSettings == null)
			{
				throw new Fault(pawn.LabelShort + " has no settings to show.");
			}
			List<string> lines = new List<string>();
			lines.Add(pawn.LabelShort + ": settings");
			lines.Add("Drafted: " + (pawn.Drafted ? "yes" : "no"));
			if (pawn.drafter != null)
			{
				lines.Add("Fire at will: " + (pawn.drafter.FireAtWill ? "on" : "off") + " (applies when drafted)");
			}
			if (pawn.RaceProps.Humanlike)
			{
				string response;
				switch (pawn.playerSettings.hostilityResponse)
				{
					case HostilityResponseMode.Flee:
						response = "flee";
						break;
					case HostilityResponseMode.Attack:
						response = "attack";
						break;
					default:
						response = "ignore";
						break;
				}
				lines.Add("Threat response: " + response);
			}
			lines.Add("Medical care: " + pawn.playerSettings.medCare.GetLabel());
			// The Assign tab's Carry column (FormGroups.PlanCarryMedicine).
			if (pawn.inventoryStock != null)
			{
				InventoryStockGroupDef carry = InventoryStockGroupDefOf.Medicine;
				ThingDef kind = pawn.inventoryStock.GetDesiredThingForGroup(carry);
				lines.Add("Carry medicine: " + pawn.inventoryStock.GetDesiredCountForGroup(carry) + " " + kind.label
					+ " (has " + (pawn.inventory != null ? pawn.inventory.Count(kind) : 0) + ")");
			}
			if (!RbPawnIsBaby(pawn))
			{
				lines.Add("Self-tend: " + (pawn.playerSettings.selfTend ? "yes" : "no"));
			}
			return RbPawnJoinLines(lines, "ask about one setting.");
		}
	}
}
