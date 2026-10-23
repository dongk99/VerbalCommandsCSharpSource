using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Agent 06, brief B: the home, build-roof, no-roof, snow-clear (and pollution-clear) areas and the allowed areas,
	// painted over a rectangle the way a player does with the Architect > Zone / Area designators and the
	// Manage areas dialog. Forms: "area paint" (NAME, AREA, ON), "area create" (NAME, AREA), "area delete" (NAME);
	// question "query areas".
	//   home            Designator_AreaHomeExpand / Designator_AreaHomeClear (Designator_AreaHome.cs:22-46)
	//   build roof      Designator_AreaBuildRoof (add only, .cs:25-42); remove = Designator_AreaIgnoreRoof (.cs:24-41)
	//   no roof         Designator_AreaNoRoof (add only, .cs:27-59); remove = Designator_AreaIgnoreRoof
	//   snow clear      Designator_AreaSnowClearExpand / Clear (Designator_AreaSnowClear.cs:23-47)
	//   pollution clear Designator_AreaPollutionClearExpand / Clear (Designator_AreaPollutionClear.cs:21-45), Biotech only
	//   allowed area    Designator_AreaAllowedExpand / Clear (.cs:21-29 / :21-29), Dialog_ManageAreas.cs:46-48 (new), :109-111 (delete)
	// Every cell goes through the matching designator's own CanDesignateCell rule, then its DesignateSingleCell, as
	// Designator.DesignateMultiCell does (Designator.cs:262-291). Sounds, the tutorial hooks and the
	// PlayerKnowledgeDatabase calls of FinalizeDesignationSucceeded are not copied (they change no game state a plan needs).
	// Fair play: a fogged cell is skipped in every form (the roof designators refuse it in CanDesignateCell, Designator_AreaBuildRoof.cs:31,
	// Designator_AreaNoRoof.cs:33, Designator_AreaIgnoreRoof.cs:30; the others don't, but a player can't click a cell
	// he can't see), and answers print only what is visible.
	// > CHANGED 2026-10-16: fog is now skipped only by the roof forms (their designators refuse it). Home, snow clear,
	// > pollution clear and allowed areas take every cell of the rectangle, fogged or not: their CanDesignateCell has no fog
	// > rule (Designator_AreaHome.cs:22-34, Designator_AreaSnowClear.cs:23-35, Designator_AreaAllowedExpand.cs:21-24) and the
	// > drag rectangle has none either (DesignationDragger.cs), so a player's drag fills it. User's words: "This should not
	// > be happening. when an agent sets up an area, it sets up a fucking rectangle." (area "camp" had left out the fogged
	// > mountain, and every mining trip into it was refused). Judge (:fog check) and PlanCreate (both loops) changed.
	// > CHANGED again 2026-10-16: no form checks fog at all, the roof forms included, and Tally counts every cell of an
	// > area (answers too). User's words: "No fog check."
	public static class AgentAreas
	{
		private enum Kind
		{
			Home,
			BuildRoof,
			NoRoof,
			SnowClear,
			PollutionClear,
			Allowed
		}

		private enum Op
		{
			Add,
			Remove
		}

		private enum Verdict
		{
			Accept,
			Outside,
			Fog,
			Thick,
			Already
		}

		// Dialog_Rename.cs:59 only takes the typed text while text.Length < MaxNameLength (28), so 27 is the longest name.
		private const int MaxNameChars = 27;
		private const int MaxAnswerLines = 60;

		// ---- entry points ---------------------------------------------------------------------------------------

		// FormPlanner.Dispatch calls this for "area paint", "area create", "area delete". Throws Fault for refusals.
		public static PlanReply Plan(FormContext c)
		{
			switch (c.form)
			{
				case "area paint":
					return PlanPaint(c);
				case "area create":
					return PlanCreate(c);
				case "area delete":
					return PlanDelete(c);
			}
			throw new Fault("The mod can't do that yet: '" + c.form + "' is not built yet.");
		}

		// FormQueries.Query: "query areas". Returns the answer text.
		public static string QueryAreas(Map map)
		{
			List<string> lines = new List<string>();
			AreaManager am = map.areaManager;
			lines.Add(DescribeBuiltIn("Home", am.Home, map));
			lines.Add(DescribeBuiltIn("Build roof", am.BuildRoof, map));
			lines.Add(DescribeBuiltIn("Remove roof", am.NoRoof, map));
			lines.Add(DescribeBuiltIn(am.SnowOrSandClear.Label, am.SnowOrSandClear, map));
			if (ModsConfig.BiotechActive && am.PollutionClear != null)
			{
				lines.Add(DescribeBuiltIn(am.PollutionClear.Label, am.PollutionClear, map));
			}
			List<Area_Allowed> allowed = am.AllAreas.OfType<Area_Allowed>().ToList();
			foreach (Area_Allowed a in allowed)
			{
				string line = DescribeBuiltIn("Area '" + a.Label + "'", a, map);
				List<Pawn> kept = KeptTo(map, a);
				line += " " + (kept.Count == 0 ? "No one is kept to it." : "Kept to it: " + FormPlanner.JoinNames(kept.Select((Pawn p) => p.LabelShort)) + ".");
				lines.Add(line);
			}
			// AreaManager.cs:13 MaxAllowedAreas, the same count CanMakeNewAllowed uses (AreaManager.cs:142-145).
			lines.Add("Allowed areas: " + allowed.Count + " of " + AreaManager.MaxAllowedAreas + " used.");
			if (lines.Count > MaxAnswerLines)
			{
				int more = lines.Count - (MaxAnswerLines - 1);
				lines = lines.Take(MaxAnswerLines - 1).ToList();
				lines.Add("... and " + more + " more lines.");
			}
			return string.Join("\n", lines.ToArray());
		}

		// "Home: 412 cells, from 100, 80 to 120, 95 (...)." or "Home: 0 cells."
		private static string DescribeBuiltIn(string label, Area area, Map map)
		{
			int count;
			CellRect bounds;
			Tally(map, area, out count, out bounds);
			if (count == 0)
			{
				return label + ": 0 cells.";
			}
			return label + ": " + Cells(count) + ", from " + AgentPlace.DescribeRect(map, bounds) + ".";
		}

		// ---- area paint -----------------------------------------------------------------------------------------

		private static PlanReply PlanPaint(FormContext c)
		{
			string name = c.Text("NAME");
			if (string.IsNullOrWhiteSpace(name))
			{
				throw new Fault("The order did not say which area.");
			}
			bool? addValue = FormPlanner.ParseBool(c.Text("ON"));
			if (addValue == null)
			{
				throw new Fault("The order did not say whether to add cells to the area or take them out.");
			}
			Op op = addValue.Value ? Op.Add : Op.Remove;
			Map map = c.map;
			Kind kind;
			Area area = Resolve(map, name, out kind);
			CellRect rect = RectOf(c, "AREA");

			List<IntVec3> accepted;
			Counts refused;
			Survey(map, kind, area, op, rect, out accepted, out refused);
			if (accepted.Count == 0)
			{
				throw new Fault(NothingToDo(kind, area, op, refused));
			}

			CellRect bounds = Bounds(accepted);
			string noun = Noun(kind, area);
			string left = refused.Text();
			string tail = left.Length > 0 ? " (" + left + " left out)" : "";
			string line;
			if (kind == Kind.BuildRoof || kind == Kind.NoRoof)
			{
				if (op == Op.Add)
				{
					line = "Add " + Cells(accepted.Count) + " to the " + noun + ", inside " + AgentPlace.DescribeRect(map, bounds) + tail + ".";
					line += kind == Kind.BuildRoof
						? " Colonists will build roof there."
						: " Colonists will remove roof there and none will be built.";
				}
				else
				{
					// Designator_AreaIgnoreRoof: both marks are cleared, whichever of the two the cell had.
					line = "Clear the roof marks (build roof / remove roof) on " + Cells(accepted.Count) + " inside " + AgentPlace.DescribeRect(map, bounds) + tail
						+ "; colonists will neither build nor remove roof there.";
				}
			}
			else if (op == Op.Add)
			{
				line = "Add " + Cells(accepted.Count) + " to the " + noun + ", inside " + AgentPlace.DescribeRect(map, bounds) + tail + ".";
			}
			else
			{
				line = "Take " + Cells(accepted.Count) + " out of the " + noun + ", inside " + AgentPlace.DescribeRect(map, bounds) + tail + ".";
			}

			Plan plan = FormPlanner.NewPlan("area paint");
			AddPaintNotes(plan, map, kind, area, op, accepted);

			Write write = new Write();
			write.description = (op == Op.Add ? "add " : "take ") + Cells(accepted.Count) + (op == Op.Add ? " to " : " out of ") + noun;
			Kind capturedKind = kind;
			Area capturedArea = area;
			Op capturedOp = op;
			List<IntVec3> capturedCells = accepted;
			write.apply = delegate
			{
				// Time passes between plan and confirm: fetch the area again and re-judge every cell on live state.
				Area live = Fetch(map, capturedKind, capturedArea);
				int changed = Paint(map, capturedKind, live, capturedOp, capturedCells);
				if (changed == 0)
				{
					throw new Fault("Nothing was changed: none of those cells could still be " + (capturedOp == Op.Add ? "added to" : "taken out of") + " the " + Noun(capturedKind, live) + ".");
				}
			};
			plan.writes.Add(write.Reads(() => ReadPaint(map, kind, area)));
			return FormPlanner.Reply(plan, line, null, null);
		}

		// The notes after the confirm line: what the game itself warns about, what the change will not do.
		private static void AddPaintNotes(Plan plan, Map map, Kind kind, Area area, Op op, List<IntVec3> accepted)
		{
			if (kind == Kind.BuildRoof && op == Op.Add)
			{
				// The game's own caution, Designator_AreaBuildRoof.cs:44-54 (ShowWarningForCell), same test as
				// RoofUtility.FirstBlockingThing (RoofUtility.cs:9-20) which JobDriver_BuildRoof.cs:27 also uses.
				int plants = 0;
				int tooFar = 0;
				int markedNoRoof = 0;
				foreach (IntVec3 cell in accepted)
				{
					if (RoofUtility.FirstBlockingThing(cell, map) != null)
					{
						plants++;
					}
					if (map.areaManager.NoRoof[cell])
					{
						markedNoRoof++;
					}
					// RoofCollapseUtility.WithinRangeOfRoofHolder (RoofCollapseUtility.cs:9-30): a roof can only be built
					// within RoofMaxSupportDistance (6.9 cells, .cs:5) of a wall or column that holds a roof. The same call with
					// assumeNonNoRoofCellsAreRoofed is what the game's own auto-roof setter asks (AutoBuildRoofAreaSetter.cs:118):
					// it assumes the cells between will get roofed, so it names only cells that can never be roofed.
					if (!cell.Roofed(map) && !RoofCollapseUtility.WithinRangeOfRoofHolder(cell, map, true))
					{
						tooFar++;
					}
				}
				if (plants > 0)
				{
					plan.notes.Add(plants + (plants == 1 ? " of the cells has" : " of the cells have") + " a plant that can't live under a roof; the game warns about these (Add roof area). Colonists cut such a plant before they roof the cell.");
				}
				if (tooFar > 0)
				{
					plan.notes.Add(tooFar + (tooFar == 1 ? " of the cells is" : " of the cells are") + " too far from a wall or column to hold a roof (more than "
						+ RoofCollapseUtility.RoofMaxSupportDistance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " cells), so no roof will be built there.");
				}
				if (markedNoRoof > 0)
				{
					plan.notes.Add(markedNoRoof + (markedNoRoof == 1 ? " of the cells was" : " of the cells were") + " marked remove roof; that mark is cleared, as the game does.");
				}
				AgentWindCheck.NoteRoof(plan, map, accepted);
				AgentLightCheck.NoteRoof(plan, map, accepted);
			}
			else if (kind == Kind.NoRoof && op == Op.Add)
			{
				int markedBuild = accepted.Count((IntVec3 cell) => map.areaManager.BuildRoof[cell]);
				if (markedBuild > 0)
				{
					plan.notes.Add(markedBuild + (markedBuild == 1 ? " of the cells was" : " of the cells were") + " marked build roof; that mark is cleared, as the game does.");
				}
			}
			else if (kind == Kind.Allowed && op == Op.Remove && area != null)
			{
				int left = area.TrueCount - accepted.Count;
				if (left <= 0)
				{
					plan.notes.Add("Area '" + area.Label + "' will have 0 cells; pawns kept to it can't go anywhere useful.");
				}
			}
		}

		private static string ReadPaint(Map map, Kind kind, Area area)
		{
			AreaManager am = map.areaManager;
			if (kind == Kind.BuildRoof || kind == Kind.NoRoof)
			{
				return "Build roof: " + Cells(CountVisible(map, am.BuildRoof)) + ", remove roof: " + Cells(CountVisible(map, am.NoRoof));
			}
			if (kind == Kind.Allowed)
			{
				if (area == null || !am.AllAreas.Contains(area))
				{
					return "no area '" + (area != null ? area.Label : "?") + "'";
				}
				return "area '" + area.Label + "': " + Cells(CountVisible(map, area));
			}
			Area live = Fetch(map, kind, area);
			return Noun(kind, live) + ": " + Cells(CountVisible(map, live));
		}

		// ---- area create ----------------------------------------------------------------------------------------

		private static PlanReply PlanCreate(FormContext c)
		{
			Map map = c.map;
			string name = (c.Text("NAME") ?? "").Trim();
			CheckNewName(map, name);
			CellRect rect = RectOf(c, "AREA");
			AreaManager am = map.areaManager;
			if (!am.CanMakeNewAllowed())
			{
				throw new Fault("No area was made: the colony already has as many areas as the game allows (" + AreaManager.MaxAllowedAreas + ").");
			}

			// Designator_AreaAllowedExpand.CanDesignateCell (.cs:21-24): in bounds and not yet in the area; the new area is empty.
			List<IntVec3> accepted = new List<IntVec3>();
			Counts refused = new Counts();
			foreach (IntVec3 cell in rect)
			{
				if (!cell.InBounds(map))
				{
					refused.outside++;
				}
				else
				{
					accepted.Add(cell);   // fogged too: the whole rectangle, as a player's drag (CHANGED 2026-10-16, top of file)
				}
			}
			if (accepted.Count == 0)
			{
				throw new Fault("No area was made: " + (refused.Text().Length > 0 ? refused.Text() : "that rectangle has no cell") + ".");
			}

			string left = refused.Text();
			string line = "Make area '" + name + "' with " + Cells(accepted.Count) + " inside " + AgentPlace.DescribeRect(map, Bounds(accepted))
				+ (left.Length > 0 ? " (" + left + " left out)" : "") + ".";
			Plan plan = FormPlanner.NewPlan("area create");
			plan.notes.Add("Use 'restrict PAWNS to area " + name + ".' to keep colonists in it.");

			Write write = new Write();
			write.description = "make area '" + name + "' with " + Cells(accepted.Count);
			List<IntVec3> capturedCells = accepted;
			write.apply = delegate
			{
				// Re-check: another area may have been made, or the name taken, since the plan.
				CheckNewName(map, name);
				Area_Allowed made;
				if (!map.areaManager.TryMakeNewAllowed(out made))
				{
					throw new Fault("Area '" + name + "' wasn't created: the colony already has as many areas as the game allows.");
				}
				// Dialog_ManageAreas.cs:48 makes the area, Executor.cs:300 / Area_Allowed.cs:81-84 names it.
				made.SetLabel(name);
				foreach (IntVec3 cell in capturedCells)
				{
					if (cell.InBounds(map) && !made[cell])
					{
						made[cell] = true;
					}
				}
			};
			plan.writes.Add(write.Reads(() =>
			{
				Area_Allowed found = map.areaManager.AllAreas.OfType<Area_Allowed>().FirstOrDefault((Area_Allowed a) => FormPlanner.SameName(a.Label, name));
				return found == null ? "no area '" + name + "'" : "area '" + found.Label + "': " + Cells(CountVisible(map, found));
			}));
			return FormPlanner.Reply(plan, line, null, null);
		}

		// The rename dialog's rules (Dialog_Rename.cs:38-41 non-empty, :59 length; Dialog_RenameArea.cs:17 not in use)
		// plus the names the router reads as one of the fixed areas.
		private static void CheckNewName(Map map, string name)
		{
			if (string.IsNullOrWhiteSpace(name))
			{
				throw new Fault("No area was made: the order gave no name.");
			}
			if (name.Length > MaxNameChars)
			{
				throw new Fault("No area was made: the name '" + name + "' is too long (the game takes at most " + MaxNameChars + " letters).");
			}
			if (IsFixedAreaName(name) || map.areaManager.AllAreas.Any((Area a) => FormPlanner.SameName(a.Label, name)))
			{
				throw new Fault("No area was made: there is already an area called '" + name + "'.");
			}
		}

		// ---- area delete ----------------------------------------------------------------------------------------

		private static PlanReply PlanDelete(FormContext c)
		{
			Map map = c.map;
			string name = (c.Text("NAME") ?? "").Trim();
			if (name.Length == 0)
			{
				throw new Fault("The order did not say which area to delete.");
			}
			Kind kind;
			Area found = Resolve(map, name, out kind);
			Area_Allowed area = found as Area_Allowed;
			if (kind != Kind.Allowed || area == null || !area.Mutable)
			{
				// Dialog_ManageAreas.cs:32 lists only Mutable areas; AreaManager.Remove (.cs:71-77) refuses the rest.
				throw new Fault("The " + Noun(kind, found) + " can't be deleted; take cells out of it instead ('area " + FormPlanner.Norm(name) + " remove ...').");
			}
			List<Pawn> kept = KeptTo(map, area);
			int cells = CountVisible(map, area);
			string who = kept.Count == 0
				? "No one is kept to it."
				: FormPlanner.JoinNames(kept.Select((Pawn p) => p.LabelShort)) + (kept.Count == 1 ? " is" : " are") + " kept to it and will be free to go anywhere.";
			string line = "RISKY: Delete area '" + area.Label + "' (" + Cells(cells) + "). " + who;

			Plan plan = FormPlanner.NewPlan("area delete");
			Write write = new Write();
			write.description = "delete area '" + area.Label + "'";
			Area_Allowed captured = area;
			write.apply = delegate
			{
				if (!map.areaManager.AllAreas.Contains(captured))
				{
					throw new Fault("Area '" + captured.Label + "' is already gone.");
				}
				// Dialog_ManageAreas.cs:109-111: area.Delete() (Area.cs:137-140 -> AreaManager.Remove, which
				// tells every pawn the area is gone, Pawn_PlayerSettings.cs:404-413).
				captured.Delete();
			};
			plan.writes.Add(write.Reads(() => map.areaManager.AllAreas.Contains(captured)
				? "area '" + captured.Label + "': " + Cells(CountVisible(map, captured))
				: "no area '" + captured.Label + "'"));
			return FormPlanner.Reply(plan, line, kept, null);
		}

		// ---- the cell rules -------------------------------------------------------------------------------------

		private sealed class Counts
		{
			public int outside;
			public int fog;
			public int thick;
			public int already;

			// The refusal part of a confirm line; "already" is not part of it (silent unless it is all of them).
			public string Text()
			{
				List<string> parts = new List<string>();
				if (outside > 0)
				{
					parts.Add(outside + " outside the map");
				}
				if (fog > 0)
				{
					parts.Add(fog + " under fog");
				}
				if (thick > 0)
				{
					parts.Add(thick + " under thick rock roof");
				}
				return string.Join(", ", parts.ToArray());
			}
		}

		// The matching designator's CanDesignateCell with c.map instead of Find.CurrentMap, plus the fog rule.
		private static Verdict Judge(Map map, Kind kind, Area area, Op op, IntVec3 cell)
		{
			// Every designator starts with c.InBounds (Designator_AreaHome.cs:24, AreaBuildRoof.cs:27, ...).
			if (!cell.InBounds(map))
			{
				return Verdict.Outside;
			}
			// No fog check in any form (user, 2026-10-16: "No fog check."; CHANGED note at the top of the file).
			AreaManager am = map.areaManager;
			if (kind == Kind.BuildRoof && op == Op.Add)
			{
				// Designator_AreaBuildRoof.cs:35
				return am.BuildRoof[cell] ? Verdict.Already : Verdict.Accept;
			}
			if (kind == Kind.NoRoof && op == Op.Add)
			{
				// Designator_AreaNoRoof.cs:37-42: thick (mountain) roof can't be removed.
				RoofDef roof = map.roofGrid.RoofAt(cell);
				if (roof != null && roof.isThickRoof)
				{
					return Verdict.Thick;
				}
				return am.NoRoof[cell] ? Verdict.Already : Verdict.Accept;
			}
			if (kind == Kind.BuildRoof || kind == Kind.NoRoof)
			{
				// Remove, either name: Designator_AreaIgnoreRoof.cs:34
				return am.BuildRoof[cell] || am.NoRoof[cell] ? Verdict.Accept : Verdict.Already;
			}
			// Home, snow clear, pollution clear, allowed: Add accepts a cell not yet in the area, Remove one that is
			// (Designator_AreaHome.cs:28-33, AreaSnowClear.cs:29-34, AreaPollutionClear.cs:27-32, AreaAllowedExpand.cs:23, AreaAllowedClear.cs:23).
			bool inside = area[cell];
			if (op == Op.Add)
			{
				return inside ? Verdict.Already : Verdict.Accept;
			}
			return inside ? Verdict.Accept : Verdict.Already;
		}

		private static void Survey(Map map, Kind kind, Area area, Op op, CellRect rect, out List<IntVec3> accepted, out Counts refused)
		{
			accepted = new List<IntVec3>();
			refused = new Counts();
			foreach (IntVec3 cell in rect)
			{
				switch (Judge(map, kind, area, op, cell))
				{
					case Verdict.Accept:
						accepted.Add(cell);
						break;
					case Verdict.Outside:
						refused.outside++;
						break;
					case Verdict.Fog:
						refused.fog++;
						break;
					case Verdict.Thick:
						refused.thick++;
						break;
					default:
						refused.already++;
						break;
				}
			}
		}

		// "No cell to change -> Fault saying why."
		private static string NothingToDo(Kind kind, Area area, Op op, Counts refused)
		{
			string noun = Noun(kind, area);
			string why = refused.Text();
			if (refused.already > 0 && why.Length == 0)
			{
				bool roofClear = (kind == Kind.BuildRoof || kind == Kind.NoRoof) && op == Op.Remove;
				if (roofClear)
				{
					return "Nothing to change: none of those cells has a build roof or remove roof mark.";
				}
				return "Nothing to change: " + (refused.already == 1 ? "that cell is" : "all " + refused.already + " cells are")
					+ (op == Op.Add ? " already in the " : " not in the ") + noun + ".";
			}
			if (why.Length == 0)
			{
				return "Nothing to change: that rectangle has no cell.";
			}
			return "Nothing to change: " + why + (refused.already > 0 ? ", " + refused.already + " already " + (op == Op.Add ? "in" : "out of") + " the " + noun : "") + ".";
		}

		// What each designator's DesignateSingleCell (and finalize step) does, cell by cell, after a fresh Judge.
		private static int Paint(Map map, Kind kind, Area area, Op op, List<IntVec3> cells)
		{
			AreaManager am = map.areaManager;
			int changed = 0;
			List<IntVec3> justAdded = new List<IntVec3>();
			foreach (IntVec3 cell in cells)
			{
				if (Judge(map, kind, area, op, cell) != Verdict.Accept)
				{
					continue;
				}
				changed++;
				if (kind == Kind.BuildRoof && op == Op.Add)
				{
					// Designator_AreaBuildRoof.cs:38-42
					am.BuildRoof[cell] = true;
					am.NoRoof[cell] = false;
				}
				else if (kind == Kind.NoRoof && op == Op.Add)
				{
					// Designator_AreaNoRoof.cs:45-49; the BuildRoof mark goes in the finalize step below.
					am.NoRoof[cell] = true;
					justAdded.Add(cell);
				}
				else if (kind == Kind.BuildRoof || kind == Kind.NoRoof)
				{
					// Designator_AreaIgnoreRoof.cs:37-41
					am.BuildRoof[cell] = false;
					am.NoRoof[cell] = false;
				}
				else
				{
					// Designator_AreaHome.cs:36-46, AreaSnowClear.cs:37-47, AreaPollutionClear.cs:35-45, AreaAllowedExpand.cs:26-29, AreaAllowedClear.cs:26-29
					area[cell] = op == Op.Add;
				}
			}
			// Designator_AreaNoRoof.FinalizeDesignationSucceeded (.cs:51-59): the cells just marked lose their BuildRoof mark.
			foreach (IntVec3 cell in justAdded)
			{
				am.BuildRoof[cell] = false;
			}
			return changed;
		}

		// ---- names and areas ------------------------------------------------------------------------------------

		private static bool IsFixedAreaName(string name)
		{
			string n = FormPlanner.Norm(name);
			return n == "home" || n == "roof" || n == "build roof" || n == "buildroof" || n == "no roof" || n == "noroof" || n == "remove roof"
				|| n == "snow clear" || n == "sand clear" || n == "snow and sand clear" || n == "snow or sand clear" || n == "pollution clear";
		}

		// NAME -> the area (AreaManager.cs:17-25 for the fixed ones; an Area_Allowed by label otherwise).
		private static Area Resolve(Map map, string name, out Kind kind)
		{
			string n = FormPlanner.Norm(name);
			AreaManager am = map.areaManager;
			if (n == "home")
			{
				kind = Kind.Home;
				return am.Home;
			}
			if (n == "roof" || n == "build roof" || n == "buildroof")
			{
				kind = Kind.BuildRoof;
				return am.BuildRoof;
			}
			if (n == "no roof" || n == "noroof" || n == "remove roof")
			{
				kind = Kind.NoRoof;
				return am.NoRoof;
			}
			if (n == "snow clear" || n == "sand clear" || n == "snow and sand clear" || n == "snow or sand clear")
			{
				kind = Kind.SnowClear;
				return am.SnowOrSandClear;
			}
			if (n == "pollution clear")
			{
				// AreaManager.cs:38-40 and :55: the area exists only with Biotech.
				if (!ModsConfig.BiotechActive || am.PollutionClear == null)
				{
					throw new Fault("There is no pollution clear area (the game makes it only with the Biotech expansion).");
				}
				kind = Kind.PollutionClear;
				return am.PollutionClear;
			}
			List<Area_Allowed> all = am.AllAreas.OfType<Area_Allowed>().ToList();
			Area_Allowed match = all.FirstOrDefault((Area_Allowed a) => FormPlanner.SameName(a.Label, name));
			if (match == null)
			{
				throw new Fault("There is no area called '" + name + "'." + (all.Count > 0 ? " The areas are " + FormPlanner.JoinNames(all.Select((Area_Allowed a) => a.Label)) + "." : ""));
			}
			kind = Kind.Allowed;
			return match;
		}

		// The same area again at apply time; an allowed area that was deleted meanwhile is a Fault.
		private static Area Fetch(Map map, Kind kind, Area planned)
		{
			AreaManager am = map.areaManager;
			switch (kind)
			{
				case Kind.Home:
					return am.Home;
				case Kind.BuildRoof:
					return am.BuildRoof;
				case Kind.NoRoof:
					return am.NoRoof;
				case Kind.SnowClear:
					return am.SnowOrSandClear;
				case Kind.PollutionClear:
					if (am.PollutionClear == null)
					{
						throw new Fault("The pollution clear area is gone.");
					}
					return am.PollutionClear;
			}
			if (planned == null || !am.AllAreas.Contains(planned))
			{
				throw new Fault("Area '" + (planned != null ? planned.Label : "?") + "' is gone, so nothing was changed.");
			}
			return planned;
		}

		private static string Noun(Kind kind, Area area)
		{
			switch (kind)
			{
				case Kind.Home:
					return "home area";
				case Kind.BuildRoof:
					return "build roof area";
				case Kind.NoRoof:
					return "remove roof area";
				case Kind.SnowClear:
					return (area != null ? area.Label : "snow clear").ToLowerInvariant() + " area";
				case Kind.PollutionClear:
					return "pollution clear area";
			}
			return "area '" + (area != null ? area.Label : "?") + "'";
		}

		// ---- reading areas --------------------------------------------------------------------------------------

		// The cells a player could see (nothing under fog, AgentPlace.Visible): count and bounding rectangle.
		private static void Tally(Map map, Area area, out int count, out CellRect bounds)
		{
			count = 0;
			int minX = int.MaxValue;
			int minZ = int.MaxValue;
			int maxX = int.MinValue;
			int maxZ = int.MinValue;
			foreach (IntVec3 cell in area.ActiveCells)
			{
				count++;   // every cell, fogged too (user, 2026-10-16: "No fog check.")
				minX = Math.Min(minX, cell.x);
				minZ = Math.Min(minZ, cell.z);
				maxX = Math.Max(maxX, cell.x);
				maxZ = Math.Max(maxZ, cell.z);
			}
			bounds = count == 0 ? CellRect.Empty : CellRect.FromLimits(minX, minZ, maxX, maxZ);
		}

		private static int CountVisible(Map map, Area area)
		{
			int count;
			CellRect bounds;
			Tally(map, area, out count, out bounds);
			return count;
		}

		private static CellRect Bounds(List<IntVec3> cells)
		{
			int minX = cells.Min((IntVec3 c) => c.x);
			int minZ = cells.Min((IntVec3 c) => c.z);
			int maxX = cells.Max((IntVec3 c) => c.x);
			int maxZ = cells.Max((IntVec3 c) => c.z);
			return CellRect.FromLimits(minX, minZ, maxX, maxZ);
		}

		// The pawns whose area restriction is this area (Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap, .cs:85-97;
		// FormGroups.cs PlanArea sets it the same way). A pawn the player can't see (fog) is left out.
		private static List<Pawn> KeptTo(Map map, Area area)
		{
			List<Pawn> kept = new List<Pawn>();
			foreach (Pawn p in map.mapPawns.AllPawns)
			{
				if (p.playerSettings == null || p.playerSettings.AreaRestrictionInPawnCurrentMap != area)
				{
					continue;
				}
				if (p.Spawned && !AgentPlace.Visible(map, p.Position))
				{
					continue;
				}
				kept.Add(p);
			}
			return kept;
		}

		private static CellRect RectOf(FormContext c, string slot)
		{
			CellRect rect;
			string reason;
			if (!AgentPlace.TryRect(c.map, c.Text(slot), out rect, out reason))
			{
				throw new Fault(reason);
			}
			return rect;
		}

		private static string Cells(int n)
		{
			return n + (n == 1 ? " cell" : " cells");
		}
	}
}
