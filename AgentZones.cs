using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;
using ZPlan = VerbalCommands.Plan;

namespace VerbalCommands
{
	// Agent 06, brief A: zones (growing, stockpile, dumping stockpile) the way the Architect > Zone designators
	// and the zone gizmos make and change them. Entry points are wired in by agent 10:
	//   AgentZones.Plan(c)         the 9 change forms ("zone growing", "zone stockpile", "zone dumping", "zone work",
	//                              "zone crop", "zone grow", "zone shrink", "zone delete", "zone rename")
	//   AgentZones.QueryZones(map) "query zones"
	//   AgentZones.ZoneNames / CropNames   the "zones" and "crops" vocabulary lines.
	public static class AgentZones
	{
		private const int MaxAnswerLines = 60;

		// Returned by CellCheck.Refusal for a cell that already belongs to the zone being grown: skipped silently.
		private const string OwnCell = "<own>";

		// ======================================================================================================
		// Entry points
		// ======================================================================================================

		public static PlanReply Plan(FormContext c)
		{
			switch (c.form)
			{
				case "zone growing":
					return PlanCreate(c, typeof(Zone_Growing), StorageSettingsPreset.DefaultStockpile);
				case "zone stockpile":
					return PlanCreate(c, typeof(Zone_Stockpile), StorageSettingsPreset.DefaultStockpile);
				case "zone dumping":
					return PlanCreate(c, typeof(Zone_Stockpile), StorageSettingsPreset.DumpingStockpile);
				case "zone work":
					return PlanWork(c);
				case "zone crop":
					return PlanCrop(c);
				case "zone grow":
					return PlanGrow(c);
				case "zone shrink":
					return PlanShrink(c);
				case "zone delete":
					return PlanDelete(c);
				case "zone rename":
					return PlanRename(c);
			}
			throw new Fault("The mod can't do that yet: '" + c.form + "' is not a zone form.");
		}

		public static string QueryZones(Map map)
		{
			List<Zone> zones = map.zoneManager.AllZones;
			if (zones.Count == 0)
			{
				return "No zones.";
			}
			Dictionary<ThingCategoryDef, int> totals = new Dictionary<ThingCategoryDef, int>();
			List<string> lines = new List<string>();
			foreach (Zone zone in zones)
			{
				lines.Add(QueryLine(map, zone, totals));
			}
			if (lines.Count > MaxAnswerLines)
			{
				int more = lines.Count - MaxAnswerLines;
				lines = lines.Take(MaxAnswerLines).ToList();
				lines.Add("and " + more + " more " + (more == 1 ? "zone." : "zones."));
			}
			return string.Join("\n", lines.ToArray());
		}

		// Labels of every zone on the map, game order (Verse\ZoneManager.cs:13 AllZones).
		public static IEnumerable<string> ZoneNames(Map map)
		{
			return map.zoneManager.AllZones.Select((Zone z) => z.label).ToList();
		}

		// Every plant a growing zone's menu can offer on this map, in the menu's order
		// (Verse\Command_SetPlantToGrow.cs:60-67: ValidPlantTypesForGrowers + IsPlantAvailable, then SortBy
		// priority descending, label). For a zone not yet made, CanSowOnGrower is the "Ground" sowTag test
		// (RimWorld\PlantUtility.cs:26-29); the pollution part needs the zone's cells, so it is not applied here.
		public static IEnumerable<string> CropNames(Map map)
		{
			List<ThingDef> plants = DefDatabase<ThingDef>.AllDefs
				.Where((ThingDef d) => d.category == ThingCategory.Plant && d.plant != null && d.plant.sowTags != null
					&& d.plant.sowTags.Contains("Ground") && Command_SetPlantToGrow.IsPlantAvailable(d, map))
				.OrderByDescending((ThingDef d) => PlantListPriority(d))
				.ThenBy((ThingDef d) => d.label, StringComparer.Ordinal)
				.ToList();
			List<string> names = new List<string>();
			foreach (ThingDef d in plants)
			{
				string name = CropLabel(d);
				if (!names.Contains(name))
				{
					names.Add(name);
				}
			}
			return names;
		}

		// ======================================================================================================
		// Create: zone growing / zone stockpile / zone dumping
		// ======================================================================================================

		private static PlanReply PlanCreate(FormContext c, Type zoneType, StorageSettingsPreset preset)
		{
			Map map = c.map;
			bool growing = zoneType == typeof(Zone_Growing);
			CellRect rect = ReadRect(c);
			ThingDef crop = growing ? ResolveCrop(c.Text("CROP")) : null;

			// THINGS (stockpile only): same words and refusals as "storage holds only" (FormStorage.cs:243-269, 455-483).
			List<string> thingWords = growing || preset != StorageSettingsPreset.DefaultStockpile ? new List<string>() : c.TextList("THINGS");
			bool allowAll = thingWords.Any((string w) => IsAllWord(w));
			List<ThingCategoryDef> categoryDefs = new List<ThingCategoryDef>();
			List<ThingDef> itemDefs = new List<ThingDef>();
			if (thingWords.Count > 0 && !allowAll)
			{
				ResolveThings(thingWords, categoryDefs, itemDefs);
			}
			bool filterSet = thingWords.Count > 0;

			CellCheck check = new CellCheck(map, zoneType, null);
			Tally left = new Tally();
			List<IntVec3> cells = Evaluate(check, rect, left);
			string kindWord = KindWord(zoneType, preset);
			if (cells.Count == 0)
			{
				throw new Fault("No cell there can take a " + kindWord + ": " + left.Phrase() + ".");
			}

			ZPlan plan = FormPlanner.NewPlan(c.form);
			if (growing)
			{
				string refusal = CropRefusal(map, crop, cells, null);
				if (refusal != null)
				{
					throw new Fault(refusal);
				}
				AddCropNotes(map, crop, cells, plan.notes);
			}

			int parts = CountComponents(cells);
			string forWhat = growing ? " of " + CropLabel(crop) : filterSet ? " for " + (allowAll ? "everything" : FormPlanner.JoinNames(thingWords)) : "";
			string line = "Make a " + kindWord + forWhat + " on " + CellsWord(cells.Count) + " " + Corners(map, cells) + ".";
			if (left.Total > 0)
			{
				line += " " + left.Sentence();
			}
			if (parts > 1)
			{
				line += " (" + parts + " zones, the cells are not connected)";
			}

			Write write = new Write();
			write.description = "make a " + kindWord + forWhat;
			write.apply = delegate
			{
				// Re-check: time passed between plan and confirm.
				Tally leftNow = new Tally();
				List<IntVec3> now = Evaluate(new CellCheck(map, zoneType, null), rect, leftNow);
				if (now.Count == 0)
				{
					throw new Fault("Those cells can no longer take a " + kindWord + ", so no zone was made: " + leftNow.Phrase() + ".");
				}
				if (growing)
				{
					string again = CropRefusal(map, crop, now, null);
					if (again != null)
					{
						throw new Fault(again);
					}
				}
				List<Zone> made = MakeZones(map, now, growing, preset);
				foreach (Zone zone in made)
				{
					Zone_Growing gz = zone as Zone_Growing;
					if (gz != null)
					{
						gz.SetPlantDefToGrow(crop);
					}
				}
				// The designator removes haul designations at the end of DesignateMultiCell
				// (RimWorld\Designator_ZoneAdd.cs:190-193), before anybody edits the filter.
				foreach (Zone zone in made)
				{
					Zone_Stockpile sp = zone as Zone_Stockpile;
					if (sp != null)
					{
						sp.slotGroup.RemoveHaulDesignationOnStoredThings();
					}
				}
				if (filterSet)
				{
					List<SpecialThingFilterDef> keepSpecial = DefDatabase<SpecialThingFilterDef>.AllDefsListForReading;
					foreach (Zone zone in made)
					{
						Zone_Stockpile sp = zone as Zone_Stockpile;
						if (sp == null)
						{
							continue;
						}
						if (allowAll)
						{
							// "Allow everything" button: SetAllowAll with the parent filter (Verse\ThingFilter.cs:842-872,
							// RimWorld\Zone_Stockpile.cs:111-114 GetParentStoreSettings).
							sp.settings.filter.SetAllowAll(sp.GetParentStoreSettings().filter);
							continue;
						}
						sp.settings.filter.SetDisallowAll(null, keepSpecial);
						foreach (ThingCategoryDef cat in categoryDefs)
						{
							sp.settings.filter.SetAllow(cat, true, null, keepSpecial);
						}
						foreach (ThingDef item in itemDefs)
						{
							sp.settings.filter.SetAllow(item, true);
						}
					}
				}
			};
			List<IntVec3> readCells = cells;
			plan.writes.Add(write.Reads(() => ZonesAtLine(map, readCells, zoneType)));
			return PlanReply.Ok(line, plan, null, null);
		}

		// The designator's own loop (RimWorld\Designator_ZoneAdd.cs:147-193), without the selector: the first cell
		// starts a zone, cells touching it (4-neighbour) join it, and when nothing more joins, the next left-over cell
		// starts another zone of the same kind. cells must already be accepted (no zone on them).
		private static List<Zone> MakeZones(Map map, List<IntVec3> cells, bool growing, StorageSettingsPreset preset)
		{
			List<IntVec3> unset = new List<IntVec3>(cells);
			List<Zone> made = new List<Zone>();
			Zone current = NewZone(map, growing, preset);
			map.zoneManager.RegisterZone(current);
			made.Add(current);
			current.AddCell(unset[0]);
			unset.RemoveAt(0);
			while (true)
			{
				int count = unset.Count;
				for (int i = unset.Count - 1; i >= 0; i--)
				{
					bool touches = false;
					for (int d = 0; d < 4; d++)
					{
						IntVec3 n = unset[i] + GenAdj.CardinalDirections[d];
						if (n.InBounds(map) && map.zoneManager.ZoneAt(n) == current)
						{
							touches = true;
							break;
						}
					}
					if (touches)
					{
						current.AddCell(unset[i]);
						unset.RemoveAt(i);
					}
				}
				if (unset.Count == 0)
				{
					break;
				}
				if (unset.Count == count)
				{
					current = NewZone(map, growing, preset);
					map.zoneManager.RegisterZone(current);
					made.Add(current);
					current.AddCell(unset[0]);
					unset.RemoveAt(0);
				}
			}
			foreach (Zone zone in made)
			{
				zone.CheckContiguous();
			}
			return made;
		}

		// MakeNewZone of the two designators (RimWorld\Designator_ZoneAdd_Growing.cs:66-70,
		// Designator_ZoneAddStockpile.cs:226-229), with the order's map instead of Find.CurrentMap.
		private static Zone NewZone(Map map, bool growing, StorageSettingsPreset preset)
		{
			if (growing)
			{
				return new Zone_Growing(map.zoneManager);
			}
			return new Zone_Stockpile(preset, map.zoneManager);
		}

		// ======================================================================================================
		// zone work (the two gizmo toggles), zone crop
		// ======================================================================================================

		private static PlanReply PlanWork(FormContext c)
		{
			Map map = c.map;
			Zone zone = FindZone(c);
			Zone_Growing growing = zone as Zone_Growing;
			if (growing == null)
			{
				throw new Fault(zone.label + (zone is Zone_Stockpile ? " is a stockpile" : " is not a growing zone") + "; only a growing zone has sowing and cutting.");
			}
			string workText = c.Text("WORK");
			string work = workText == null ? "" : FormPlanner.Norm(workText);
			bool sowing;
			if (work == "sowing" || work == "sow")
			{
				sowing = true;
			}
			else if (work == "cutting" || work == "cut")
			{
				sowing = false;
			}
			else
			{
				throw new Fault("'" + workText + "' is not sowing or cutting.");
			}
			string onText = c.Text("ON");
			if (onText == null)
			{
				throw new Fault("Say whether to allow or stop " + (sowing ? "sowing" : "cutting") + ".");
			}
			bool on = string.Equals(onText, "true", StringComparison.OrdinalIgnoreCase);
			string word = sowing ? "sowing" : "cutting";

			ZPlan plan = FormPlanner.NewPlan(c.form);
			bool already = (sowing ? growing.allowSow : growing.allowCut) == on;
			if (already)
			{
				plan.notes.Add(UpperFirst(word) + " is already " + (on ? "on" : "off") + " in " + growing.label + ".");
			}
			string line = (on ? "Allow " : "Stop ") + word + " in " + growing.label + ".";
			Write write = new Write();
			write.description = (on ? "allow " : "stop ") + word + " in " + growing.label;
			write.apply = delegate
			{
				Live(map, growing);
				// The gizmos only flip the field (RimWorld\Zone_Growing.cs:226-248).
				if (sowing)
				{
					growing.allowSow = on;
				}
				else
				{
					growing.allowCut = on;
				}
			};
			plan.writes.Add(write.Reads(() => ZoneLine(map, growing)));
			return PlanReply.Ok(line, plan, null, null);
		}

		private static PlanReply PlanCrop(FormContext c)
		{
			Map map = c.map;
			Zone zone = FindZone(c);
			Zone_Growing growing = zone as Zone_Growing;
			if (growing == null)
			{
				throw new Fault(zone.label + (zone is Zone_Stockpile ? " is a stockpile" : " is not a growing zone") + "; only a growing zone has a crop.");
			}
			ThingDef crop = ResolveCrop(c.Text("CROP"));
			List<IntVec3> cells = growing.cells.ToList();
			string refusal = CropRefusal(map, crop, cells, growing);
			if (refusal != null)
			{
				throw new Fault(refusal);
			}
			ThingDef was = growing.GetPlantDefToGrow();
			ZPlan plan = FormPlanner.NewPlan(c.form);
			AddCropNotes(map, crop, cells, plan.notes);
			string line = "Grow " + CropLabel(crop) + " in " + growing.label + " (was " + (was != null ? CropLabel(was) : "nothing") + ").";
			Write write = new Write();
			write.description = "grow " + CropLabel(crop) + " in " + growing.label;
			write.apply = delegate
			{
				Live(map, growing);
				string again = CropRefusal(map, crop, growing.cells.ToList(), growing);
				if (again != null)
				{
					throw new Fault(again);
				}
				// The menu entry's action (Verse\Command_SetPlantToGrow.cs:84).
				growing.SetPlantDefToGrow(crop);
			};
			plan.writes.Add(write.Reads(() => ZoneLine(map, growing)));
			return PlanReply.Ok(line, plan, null, null);
		}

		// ======================================================================================================
		// zone grow / shrink / delete / rename
		// ======================================================================================================

		private static PlanReply PlanGrow(FormContext c)
		{
			Map map = c.map;
			Zone zone = FindZone(c);
			Type zoneType = zone.GetType();
			if (zoneType != typeof(Zone_Growing) && zoneType != typeof(Zone_Stockpile))
			{
				throw new Fault(zone.label + " is not a growing zone or a stockpile, so cells can't be added to it.");
			}
			CellRect rect = ReadRect(c);
			Tally left = new Tally();
			List<IntVec3> added = GrowSelection(map, zone, rect, left);
			if (added.Count == 0)
			{
				throw new Fault("No cell there can be added to " + zone.label + (left.Total > 0 ? ": " + left.Phrase() + "." : ": they are all in it already."));
			}
			ZPlan plan = FormPlanner.NewPlan(c.form);
			Zone_Growing gz = zone as Zone_Growing;
			if (gz != null && gz.GetPlantDefToGrow() != null)
			{
				AddGrowFertilityNote(map, gz.GetPlantDefToGrow(), added, plan.notes);
			}
			string line = "Add " + CellsWord(added.Count) + " to " + zone.label + " over " + AgentPlace.DescribeRect(map, rect) + ".";
			if (left.Total > 0)
			{
				line += " " + left.Sentence();
			}
			Write write = new Write();
			write.description = "add " + CellsWord(added.Count) + " to " + zone.label;
			write.apply = delegate
			{
				Live(map, zone);
				Tally leftNow = new Tally();
				List<IntVec3> now = GrowSelection(map, zone, rect, leftNow);
				if (now.Count == 0)
				{
					throw new Fault("No cell there can be added to " + zone.label + " any more, so nothing was changed.");
				}
				foreach (IntVec3 cell in now)
				{
					zone.AddCell(cell);
				}
				// Designator_ZoneAdd.DesignateMultiCell :188-193.
				zone.CheckContiguous();
				Zone_Stockpile sp = zone as Zone_Stockpile;
				if (sp != null)
				{
					sp.slotGroup.RemoveHaulDesignationOnStoredThings();
				}
			};
			plan.writes.Add(write.Reads(() => ZoneLine(map, zone)));
			return PlanReply.Ok(line, plan, null, null);
		}

		// Cells of the rectangle the zone's own designator would accept, minus those that do not touch the zone
		// (4-neighbour, through other added cells): those are left out and counted as "not touching <label>".
		private static List<IntVec3> GrowSelection(Map map, Zone zone, CellRect rect, Tally left)
		{
			List<IntVec3> accepted = Evaluate(new CellCheck(map, zone.GetType(), zone), rect, left);
			HashSet<IntVec3> pool = new HashSet<IntVec3>(accepted);
			HashSet<IntVec3> seen = new HashSet<IntVec3>();
			List<IntVec3> order = new List<IntVec3>();
			Queue<IntVec3> queue = new Queue<IntVec3>();
			foreach (IntVec3 cell in accepted)
			{
				for (int d = 0; d < 4; d++)
				{
					IntVec3 n = cell + GenAdj.CardinalDirections[d];
					if (n.InBounds(map) && map.zoneManager.ZoneAt(n) == zone)
					{
						if (seen.Add(cell))
						{
							queue.Enqueue(cell);
							order.Add(cell);
						}
						break;
					}
				}
			}
			while (queue.Count > 0)
			{
				IntVec3 cell = queue.Dequeue();
				for (int d = 0; d < 4; d++)
				{
					IntVec3 n = cell + GenAdj.CardinalDirections[d];
					if (pool.Contains(n) && seen.Add(n))
					{
						queue.Enqueue(n);
						order.Add(n);
					}
				}
			}
			for (int i = 0; i < accepted.Count - order.Count; i++)
			{
				left.Add("not touching " + zone.label);
			}
			return order;
		}

		private static PlanReply PlanShrink(FormContext c)
		{
			Map map = c.map;
			Zone zone = FindZone(c);
			CellRect rect = ReadRect(c);
			Tally left = new Tally();
			List<IntVec3> remove = ShrinkSelection(map, zone, rect, left);
			if (remove.Count == 0)
			{
				throw new Fault(left.Total > 0
					? "No cell of " + zone.label + " in that area can be taken out: " + left.Phrase() + "."
					: "No cell of " + zone.label + " is inside that area.");
			}
			// Zone.RemoveCell deletes the zone at 0 cells (Verse\Zone.cs:112-128); the designator then runs
			// CheckContiguous on it (RimWorld\Designator_ZoneDelete.cs:167-175), which drops every cell that is not
			// connected to the zone's first cell (Verse\Zone.cs:221-273, FloodFill is 4-neighbour).
			HashSet<IntVec3> gone = new HashSet<IntVec3>(remove);
			List<IntVec3> remaining = zone.cells.Where((IntVec3 x) => !gone.Contains(x)).ToList();
			int total = zone.CellCount;
			string line;
			int cutOff = 0;
			if (remaining.Count > 0)
			{
				cutOff = remaining.Count - ReachableCount(remaining, remaining[0]);
			}
			string area = AgentPlace.DescribeRect(map, rect);
			if (remaining.Count == 0)
			{
				line = "RISKY: Take every cell out of " + zone.label + " over " + area + ": this removes all " + total + " cells, so "
					+ zone.label + " and its settings are deleted; " + (zone is Zone_Growing ? "plants and items" : "items") + " on the ground stay.";
			}
			else if (cutOff > 0)
			{
				line = "RISKY: Take " + CellsWord(remove.Count) + " out of " + zone.label + " over " + area + ". That leaves " + CellsWord(cutOff)
					+ " cut off from the rest, and the game drops those too, so " + CellsWord(remove.Count + cutOff) + " are lost in all; things on the ground stay.";
			}
			else
			{
				line = "Take " + CellsWord(remove.Count) + " out of " + zone.label + " over " + area + ".";
			}
			if (left.Total > 0)
			{
				line += " " + left.Sentence();
			}
			ZPlan plan = FormPlanner.NewPlan(c.form);
			Write write = new Write();
			write.description = "take " + CellsWord(remove.Count) + " out of " + zone.label;
			write.apply = delegate
			{
				Live(map, zone);
				List<IntVec3> now = ShrinkSelection(map, zone, rect, new Tally());
				if (now.Count == 0)
				{
					throw new Fault("No cell of " + zone.label + " is in that area any more, so nothing was changed.");
				}
				foreach (IntVec3 cell in now)
				{
					if (zone.ContainsCell(cell))
					{
						zone.RemoveCell(cell);
					}
				}
				zone.CheckContiguous();
			};
			plan.writes.Add(write.Reads(() => ZoneLine(map, zone)));
			return PlanReply.Ok(line, plan, null, null);
		}

		// Designator_ZoneDelete.CanDesignateCell (RimWorld\Designator_ZoneDelete.cs:140-155): in bounds, not fogged,
		// has a zone; here only cells of THIS zone count.
		private static List<IntVec3> ShrinkSelection(Map map, Zone zone, CellRect rect, Tally left)
		{
			List<IntVec3> result = new List<IntVec3>();
			foreach (IntVec3 cell in rect.Cells)
			{
				if (!cell.InBounds(map) || map.zoneManager.ZoneAt(cell) != zone)
				{
					continue;
				}
				if (!AgentPlace.Visible(map, cell))
				{
					left.Add("under fog");
					continue;
				}
				result.Add(cell);
			}
			return result;
		}

		private static PlanReply PlanDelete(FormContext c)
		{
			Map map = c.map;
			Zone zone = FindZone(c);
			ZPlan plan = FormPlanner.NewPlan(c.form);
			string what = zone is Zone_Growing ? "its crop setting; plants and items on the ground stay" : zone is Zone_Stockpile ? "its settings; items on the ground stay" : "items on the ground stay";
			string line = "RISKY: Delete " + zone.label + " (" + zone.CellCount + " cells) and " + what + ".";
			Write write = new Write();
			write.description = "delete " + zone.label;
			write.apply = delegate
			{
				Live(map, zone);
				// Zone.Delete(bool playSound) (Verse\Zone.cs:135-153): false = no sound; it removes every cell,
				// which deregisters the zone, then deselects it. The gizmo calls Delete() = Delete(true) (:130-133, :206-213).
				zone.Delete(false);
			};
			plan.writes.Add(write.Reads(() => ZoneLine(map, zone)));
			return PlanReply.Ok(line, plan, null, null);
		}

		private static PlanReply PlanRename(FormContext c)
		{
			Map map = c.map;
			Zone zone = FindZone(c);
			string name = c.Text("NEW_NAME");
			RenameRefusal(map, zone, name);
			ZPlan plan = FormPlanner.NewPlan(c.form);
			string line = "Rename " + zone.label + " to " + name + ".";
			Write write = new Write();
			write.description = "rename " + zone.label + " to " + name;
			write.apply = delegate
			{
				Live(map, zone);
				RenameRefusal(map, zone, name);
				// Dialog_Rename.DoWindowContents: renaming.RenamableLabel = curName (Verse\Dialog_Rename.cs:89-92).
				zone.RenamableLabel = name;
			};
			plan.writes.Add(write.Reads(() => ZoneLine(map, zone)));
			return PlanReply.Ok(line, plan, null, null);
		}

		// Dialog_Rename.NameIsValid (Verse\Dialog_Rename.cs:38-41: not empty), the text field's length limit
		// (:18 MaxNameLength 28, :59 text.Length < MaxNameLength, so at most 27 characters) and
		// Dialog_RenameZone.NameIsValid (Verse\Dialog_RenameZone.cs:163-175: another zone has the label, or a storage
		// group has the name). The dialog checks Current.Game.CurrentMap's storage groups; this uses the zone's map.
		private static void RenameRefusal(Map map, Zone zone, string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				throw new Fault("No new name was given.");
			}
			if (name.Length >= 28)
			{
				throw new Fault("A zone name can be at most 27 letters long; '" + name + "' has " + name.Length + ".");
			}
			if (map.zoneManager.AllZones.Any((Zone z) => z != zone && z.label == name))
			{
				throw new Fault("Another zone is already called '" + name + "'.");
			}
			if (map.storageGroups != null && map.storageGroups.HasGroupWithName(name))
			{
				throw new Fault("A linked storage group is already called '" + name + "'.");
			}
		}

		// ======================================================================================================
		// Cell rules
		// ======================================================================================================

		// Designator_ZoneAdd.CanDesignateCell (RimWorld\Designator_ZoneAdd.cs:66-78) + IsZoneableCell (:80-102)
		// + the type's own override: stockpile (Designator_ZoneAddStockpile.cs:237-257), growing
		// (Designator_ZoneAdd_Growing.cs:48-64). The instance CanDesignateCell uses base.Map (= Find.CurrentMap), so its
		// conditions are copied here and run against the order's map.
		private sealed class CellCheck
		{
			private readonly Map map;
			private readonly Type zoneType;
			private readonly Zone own;
			private readonly float minFertility;

			public CellCheck(Map map, Type zoneType, Zone own)
			{
				this.map = map;
				this.zoneType = zoneType;
				this.own = own;
				if (zoneType == typeof(Zone_Growing))
				{
					// Designator_ZoneAdd_Growing.cs:54-58.
					float min = ModsConfig.BiotechActive ? 0.5f : ThingDefOf.Plant_Potato.plant.fertilityMin;
					if (ModsConfig.IdeologyActive && BuildCopyCommandUtility.FindAllowedDesignator(TerrainDefOf.FungalGravel) != null)
					{
						min = Math.Min(min, ThingDefOf.Plant_Nutrifungus.plant.fertilityMin);
					}
					minFertility = min;
				}
			}

			// null = accepted; OwnCell = already in the zone being grown; else the reason, worded to follow a number
			// ("3 under fog", "2 in another zone (Growing zone 1)").
			public string Refusal(IntVec3 c)
			{
				if (!c.InBounds(map))
				{
					return "outside the map";
				}
				if (!AgentPlace.Visible(map, c))
				{
					return "under fog";
				}
				Zone there = map.zoneManager.ZoneAt(c);
				if (there != null)
				{
					if (there == own)
					{
						return OwnCell;
					}
					if (there.GetType() != zoneType)
					{
						return "in another zone (" + there.label + ")";
					}
					return "already in " + there.label;
				}
				if (c.InNoZoneEdgeArea(map))
				{
					return "too close to the map edge";
				}
				List<Thing> things = map.thingGrid.ThingsListAt(c);
				for (int i = 0; i < things.Count; i++)
				{
					if (!things[i].def.CanOverlapZones)
					{
						return "with something standing on them";
					}
				}
				if (zoneType == typeof(Zone_Stockpile))
				{
					if (c.GetTerrain(map).passability == Traversability.Impassable)
					{
						return "on impassable ground";
					}
				}
				else if (zoneType == typeof(Zone_Growing))
				{
					if (c.GetFertility(map) < minFertility)
					{
						return "not fertile enough";
					}
				}
				return null;
			}
		}

		private static List<IntVec3> Evaluate(CellCheck check, CellRect rect, Tally left)
		{
			List<IntVec3> accepted = new List<IntVec3>();
			foreach (IntVec3 cell in rect.Cells)
			{
				string why = check.Refusal(cell);
				if (why == null)
				{
					accepted.Add(cell);
				}
				else if (why != OwnCell)
				{
					left.Add(why);
				}
			}
			return accepted;
		}

		private sealed class Tally
		{
			private readonly List<string> order = new List<string>();
			private readonly Dictionary<string, int> counts = new Dictionary<string, int>();
			public int Total;

			public void Add(string reason)
			{
				int n;
				if (!counts.TryGetValue(reason, out n))
				{
					order.Add(reason);
				}
				counts[reason] = n + 1;
				Total++;
			}

			// "3 not fertile enough, 1 under fog"
			public string Phrase()
			{
				return string.Join(", ", order.Select((string r) => counts[r] + " " + r).ToArray());
			}

			// "4 cells left out: 3 not fertile enough, 1 under fog."
			public string Sentence()
			{
				return Total == 0 ? "" : CellsWord(Total) + " left out: " + Phrase() + ".";
			}
		}

		// ======================================================================================================
		// Crops
		// ======================================================================================================

		// CROP matches the plant's label, the label without a trailing " plant", or the defName (SameName).
		private static ThingDef ResolveCrop(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				throw new Fault("No crop was named.");
			}
			List<ThingDef> matches = DefDatabase<ThingDef>.AllDefs
				.Where((ThingDef d) => d.category == ThingCategory.Plant && d.plant != null && CropNameMatches(d, text))
				.ToList();
			if (matches.Count == 0)
			{
				throw new Fault("'" + text + "' is not a crop.");
			}
			List<ThingDef> sowable = matches.Where((ThingDef d) => d.plant.sowTags != null && d.plant.sowTags.Contains("Ground")).ToList();
			if (sowable.Count > 1)
			{
				throw new Fault("'" + text + "' could mean several crops (" + FormPlanner.JoinNames(sowable.Select((ThingDef d) => d.defName)) + "); name it more exactly.");
			}
			// None sown on the ground: the first match, so CropRefusal can say why it can't go in a zone.
			return sowable.Count == 1 ? sowable[0] : matches[0];
		}

		private static bool CropNameMatches(ThingDef d, string text)
		{
			if (d.label != null)
			{
				if (FormPlanner.SameName(d.label, text) || FormPlanner.SameName(CropLabel(d), text))
				{
					return true;
				}
			}
			return d.defName != null && FormPlanner.SameName(d.defName, text);
		}

		private static string CropLabel(ThingDef d)
		{
			string label = d.label ?? d.defName;
			if (label.EndsWith(" plant", StringComparison.OrdinalIgnoreCase) && label.Length > " plant".Length)
			{
				label = label.Substring(0, label.Length - " plant".Length);
			}
			return label;
		}

		// The settable test of the "set plant to grow" menu: ValidPlantTypesForGrowers -> CanSowOnGrower
		// (RimWorld\PlantUtility.cs:270-286: pollution, then the "Ground" sowTag for a zone), then IsPlantAvailable
		// (Verse\Command_SetPlantToGrow.cs:62, :166-189). zone is null while the zone does not exist yet (cells then
		// stand in for its cells). Returns the refusal in plain words, or null.
		private static string CropRefusal(Map map, ThingDef crop, List<IntVec3> cells, Zone_Growing zone)
		{
			string name = UpperFirst(CropLabel(crop));
			if (crop.plant == null || crop.plant.sowTags == null || !crop.plant.sowTags.Contains("Ground"))
			{
				return name + " can't be sown in a growing zone.";
			}
			bool pollutionOk = zone != null ? PollutionUtility.CanPlantAt(crop, zone) : CanPlantAtCells(crop, cells, map);
			if (!pollutionOk)
			{
				return crop.plant.RequiresNoPollution
					? name + " needs clean ground, and all of those cells are polluted."
					: name + " needs polluted ground, and none of those cells are polluted.";
			}
			if (zone != null && !PlantUtility.CanSowOnGrower(crop, zone))
			{
				return name + " can't be sown in " + zone.label + ".";
			}
			if (!Command_SetPlantToGrow.IsPlantAvailable(crop, map))
			{
				List<ResearchProjectDef> need = crop.plant.sowResearchPrerequisites;
				if (need != null)
				{
					for (int i = 0; i < need.Count; i++)
					{
						if (!need[i].IsFinished)
						{
							return name + " can't be sown yet: it needs the research '" + need[i].label + "'.";
						}
					}
					if (crop.plant.mustBePermanentDarknessToSow && !map.gameConditionManager.IsAlwaysDarkOutside)
					{
						return name + " can only be sown in permanent darkness, and it is not dark here.";
					}
					if (crop.plant.mustBeWildToSow && !map.wildPlantSpawner.AllWildPlants.Contains(crop))
					{
						return name + " can only be sown if it grows wild on this map, and it does not.";
					}
				}
				return name + " can't be sown on this map.";
			}
			return null;
		}

		// Verse\PollutionUtility.cs:129-153 (CanPlantAt) for cells that are not yet a zone.
		private static bool CanPlantAtCells(ThingDef crop, List<IntVec3> cells, Map map)
		{
			if (crop.plant.RequiresNoPollution)
			{
				return cells.Any((IntVec3 x) => !x.IsPolluted(map));
			}
			if (crop.plant.RequiresPollution)
			{
				return cells.Any((IntVec3 x) => x.IsPolluted(map));
			}
			return true;
		}

		// The warnings the menu gives after a click (Verse\Command_SetPlantToGrow.cs:120-164) as plan notes, plus the
		// fertility note (the game lets you, the plant just won't grow there).
		private static void AddCropNotes(Map map, ThingDef crop, List<IntVec3> cells, List<string> notes)
		{
			string name = CropLabel(crop);
			int minSkill = crop.plant.sowMinSkill;
			if (minSkill > 0 && !AnyoneCanSow(map, minSkill))
			{
				notes.Add("No colonist can sow " + name + " yet: it needs Plants skill " + minSkill + " and growing work turned on, and nobody has both.");
			}
			AddGrowFertilityNote(map, crop, cells, notes);
			if (crop.plant.interferesWithRoof && cells.Any((IntVec3 x) => x.Roofed(map)))
			{
				notes.Add(UpperFirst(name) + " does not grow under a roof, and some of these cells are roofed.");
			}
			if (crop.plant.diesToLight || crop.plant.cavePlant)
			{
				bool dark = map.gameConditionManager.IsAlwaysDarkOutside;
				foreach (IntVec3 cell in cells)
				{
					bool covered = dark || cell.Roofed(map);
					bool unlit = map.glowGrid.GroundGlowAt(cell, ignoreCavePlants: true) <= 0f;
					if (!covered || !unlit)
					{
						notes.Add(UpperFirst(name) + " needs darkness, and some of these cells are lit or open to the sky.");
						break;
					}
				}
			}
		}

		private static void AddGrowFertilityNote(Map map, ThingDef crop, List<IntVec3> cells, List<string> notes)
		{
			float need = crop.plant.fertilityMin;
			int low = cells.Count((IntVec3 x) => x.GetFertility(map) < need);
			if (low > 0)
			{
				notes.Add(low + " of the " + cells.Count + " cells are below the " + (int)Math.Round(need * 100f) + "% fertility " + CropLabel(crop) + " needs, so it won't grow there.");
			}
		}

		// Command_SetPlantToGrow.cs:124-134.
		private static bool AnyoneCanSow(Map map, int minSkill)
		{
			foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
			{
				if (pawn.skills != null && pawn.workSettings != null
					&& pawn.skills.GetSkill(SkillDefOf.Plants).Level >= minSkill && !pawn.Downed
					&& pawn.workSettings.WorkIsActive(WorkTypeDefOf.Growing))
				{
					return true;
				}
			}
			if (ModsConfig.BiotechActive)
			{
				Pawn mech;
				if (MechanitorUtility.AnyPlayerMechCanDoWork(WorkTypeDefOf.Growing, minSkill, out mech))
				{
					return true;
				}
			}
			return false;
		}

		// Verse\Command_SetPlantToGrow.cs:191-205.
		private static float PlantListPriority(ThingDef d)
		{
			if (d.plant.IsTree)
			{
				return 1f;
			}
			switch (d.plant.purpose)
			{
				case PlantPurpose.Food:
					return 4f;
				case PlantPurpose.Health:
					return 3f;
				case PlantPurpose.Beauty:
					return 2f;
				default:
					return 0f;
			}
		}

		// ======================================================================================================
		// Stockpile contents words
		// ======================================================================================================

		private static bool IsAllWord(string word)
		{
			string n = FormPlanner.Norm(word);
			return n == "everything" || n == "anything" || n == "all";
		}

		// Classifies the words with FormPlanner.ClassifyThings (FormStorage.cs:243, made internal by agent 10, see
		// agent_player\out\06\register.md), then the def lookups of AddBlueprintStorageWrite (:455-483), with the
		// parent filter of a stockpile zone (RimWorld\Zone_Stockpile.cs:111-114: StorageSettings.EverStorableFixedSettings()).
		private static void ResolveThings(List<string> words, List<ThingCategoryDef> categoryDefs, List<ThingDef> itemDefs)
		{
			ThingFilter parent = StorageSettings.EverStorableFixedSettings().filter;
			List<string> catNames = new List<string>();
			List<string> itemNames = new List<string>();
			FormPlanner.ClassifyThings(words, catNames, itemNames);
			foreach (string name in itemNames)
			{
				List<ThingDef> found = Executor.FindItemDefs(name);
				if (found.Count != 1)
				{
					throw new Fault("'" + name + "' could mean several items; name it more exactly.");
				}
				if (!StorageIndex.CanHold(parent, found[0]))
				{
					throw new Fault(UpperFirst(found[0].label) + " can't be stored in a stockpile.");
				}
				itemDefs.Add(found[0]);
			}
			foreach (string name in catNames)
			{
				List<ThingCategoryDef> found = Executor.FindCategoryDefs(name);
				if (found.Count != 1)
				{
					throw new Fault("'" + name + "' could mean several kinds of things; name it more exactly.");
				}
				categoryDefs.Add(found[0]);
			}
		}

		// "foods (all), manufactured (all), raw resources (part)": the top-level categories under
		// ThingCategoryDefOf.Root (RimWorld\ThingCategoryDefOf.cs:12) that are all or partly allowed, at most 8.
		// "All" counts only the defs the parent filter (everything a stockpile can ever store) allows.
		private static string FilterSummary(StorageSettings settings, Dictionary<ThingCategoryDef, int> totals)
		{
			ThingFilter parent = StorageSettings.EverStorableFixedSettings().filter;
			List<string> parts = new List<string>();
			foreach (ThingCategoryDef cat in ThingCategoryDefOf.Root.childCategories)
			{
				int total;
				if (!totals.TryGetValue(cat, out total))
				{
					total = cat.DescendantThingDefs.Count((ThingDef d) => parent.Allows(d));
					totals[cat] = total;
				}
				if (total == 0)
				{
					continue;
				}
				int allowed = cat.DescendantThingDefs.Count((ThingDef d) => parent.Allows(d) && settings.filter.Allows(d));
				if (allowed == 0)
				{
					continue;
				}
				parts.Add(cat.label + (allowed >= total ? " (all)" : " (part)"));
			}
			if (parts.Count == 0)
			{
				return "nothing";
			}
			if (parts.Count > 8)
			{
				int more = parts.Count - 8;
				return string.Join(", ", parts.Take(8).ToArray()) + ", and " + more + " more";
			}
			return string.Join(", ", parts.ToArray());
		}

		// ======================================================================================================
		// Reading back (Reads lines) and the query
		// ======================================================================================================

		// "no zone at those cells" before a create; after it, the zone(s) now on those cells.
		private static string ZonesAtLine(Map map, List<IntVec3> cells, Type zoneType)
		{
			List<Zone> zones = new List<Zone>();
			foreach (IntVec3 cell in cells)
			{
				if (!cell.InBounds(map))
				{
					continue;
				}
				Zone z = map.zoneManager.ZoneAt(cell);
				if (z != null && z.GetType() == zoneType && !zones.Contains(z))
				{
					zones.Add(z);
				}
			}
			if (zones.Count == 0)
			{
				return "no zone at those cells";
			}
			return string.Join("; ", zones.Select((Zone z) => ZoneLine(map, z)).ToArray());
		}

		// One short line about a zone: "Growing zone 1: 96 cells, rice, sowing on, cutting on".
		private static string ZoneLine(Map map, Zone zone)
		{
			if (!map.zoneManager.AllZones.Contains(zone))
			{
				return zone.label + ": gone";
			}
			Zone_Growing gz = zone as Zone_Growing;
			if (gz != null)
			{
				ThingDef crop = gz.GetPlantDefToGrow();
				return zone.label + ": " + CellsWord(zone.CellCount) + ", " + (crop != null ? CropLabel(crop) : "no crop")
					+ ", sowing " + (gz.allowSow ? "on" : "off") + ", cutting " + (gz.allowCut ? "on" : "off");
			}
			Zone_Stockpile sp = zone as Zone_Stockpile;
			if (sp != null)
			{
				return zone.label + ": " + CellsWord(zone.CellCount) + ", priority " + sp.settings.Priority.Label().ToLowerInvariant()
					+ ", holds " + FilterSummary(sp.settings, new Dictionary<ThingCategoryDef, int>());
			}
			return zone.label + ": " + CellsWord(zone.CellCount);
		}

		private static string QueryLine(Map map, Zone zone, Dictionary<ThingCategoryDef, int> totals)
		{
			StringBuilder sb = new StringBuilder();
			sb.Append(zone.label).Append(": ");
			Zone_Growing gz = zone as Zone_Growing;
			Zone_Stockpile sp = zone as Zone_Stockpile;
			sb.Append(gz != null ? "growing" : sp != null ? "stockpile" : "zone");
			sb.Append(", ").Append(CellsWord(zone.CellCount));
			if (zone.CellCount > 0)
			{
				int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
				foreach (IntVec3 cell in zone.cells)
				{
					minX = Math.Min(minX, cell.x);
					maxX = Math.Max(maxX, cell.x);
					minZ = Math.Min(minZ, cell.z);
					maxZ = Math.Max(maxZ, cell.z);
				}
				// First corner plain "x, z", the second with Describe so the text says where it is.
				sb.Append(", ").Append(minX).Append(", ").Append(minZ).Append(" to ").Append(AgentPlace.Describe(map, new IntVec3(maxX, 0, maxZ)));
			}
			if (gz != null)
			{
				ThingDef crop = gz.GetPlantDefToGrow();
				sb.Append("; ").Append(crop != null ? CropLabel(crop) : "no crop");
				sb.Append("; sowing ").Append(gz.allowSow ? "on" : "off").Append(", cutting ").Append(gz.allowCut ? "on" : "off");
				// Same count as Zone_Growing.ContentsStatistics (RimWorld\Zone_Growing.cs:133-156), without its
				// division by zero when there are no plants.
				int plants = 0;
				float growth = 0f;
				foreach (IntVec3 cell in zone.cells)
				{
					List<Thing> things = cell.GetThingList(map);
					for (int i = 0; i < things.Count; i++)
					{
						Plant plant = things[i] as Plant;
						if (plant != null && plant.def == crop)
						{
							plants++;
							growth += plant.Growth;
						}
					}
				}
				if (plants == 0)
				{
					sb.Append("; no plants yet");
				}
				else
				{
					sb.Append("; ").Append(plants).Append(plants == 1 ? " plant" : " plants").Append(", average growth ").Append((int)Math.Round(growth / plants * 100f)).Append("%");
				}
			}
			else if (sp != null)
			{
				sb.Append("; priority ").Append(sp.settings.Priority.Label().ToLowerInvariant());
				sb.Append("; holds: ").Append(FilterSummary(sp.settings, totals));
				int stacks = sp.slotGroup.HeldThingsCount;
				sb.Append("; ").Append(stacks).Append(stacks == 1 ? " stack stored" : " stacks stored");
			}
			return sb.ToString();
		}

		// ======================================================================================================
		// Small helpers
		// ======================================================================================================

		private static CellRect ReadRect(FormContext c)
		{
			string text = c.Text("AREA");
			if (string.IsNullOrWhiteSpace(text))
			{
				throw new Fault("No area was given.");
			}
			CellRect rect;
			string reason;
			if (!AgentPlace.TryRect(c.map, text, out rect, out reason))
			{
				throw new Fault(reason);
			}
			return rect;
		}

		// The zone named in slot ZONE: label compared with SameName.
		private static Zone FindZone(FormContext c)
		{
			string name = c.Text("ZONE");
			List<Zone> all = c.map.zoneManager.AllZones;
			if (string.IsNullOrWhiteSpace(name))
			{
				throw new Fault("No zone was named.");
			}
			List<Zone> matches = all.Where((Zone z) => FormPlanner.SameName(z.label, name)).ToList();
			if (matches.Count == 1)
			{
				return matches[0];
			}
			if (matches.Count > 1)
			{
				throw new Fault("Several zones are called '" + name + "'; rename one first.");
			}
			if (all.Count == 0)
			{
				throw new Fault("There is no zone called '" + name + "'; there are no zones on the map.");
			}
			List<string> labels = all.Select((Zone z) => z.label).ToList();
			string list = labels.Count > 10
				? FormPlanner.JoinNames(labels.Take(10)) + " and " + (labels.Count - 10) + " more"
				: FormPlanner.JoinNames(labels);
			throw new Fault("There is no zone called '" + name + "'. Zones: " + list + ".");
		}

		// The apply delegate runs after time has passed: the zone may be gone.
		private static void Live(Map map, Zone zone)
		{
			if (!map.zoneManager.AllZones.Contains(zone))
			{
				throw new Fault(zone.label + " is gone, so nothing was changed.");
			}
		}

		private static string KindWord(Type zoneType, StorageSettingsPreset preset)
		{
			if (zoneType == typeof(Zone_Growing))
			{
				return "growing zone";
			}
			return preset == StorageSettingsPreset.DumpingStockpile ? "dumping stockpile" : "stockpile";
		}

		private static string CellsWord(int n)
		{
			return n + (n == 1 ? " cell" : " cells");
		}

		private static string UpperFirst(string s)
		{
			return FormPlanner.UpperFirst(s);
		}

		// "from 100, 80 (...) to 109, 89 (...)": the bounding rectangle of the cells, both corners with Describe.
		private static string Corners(Map map, List<IntVec3> cells)
		{
			int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
			foreach (IntVec3 cell in cells)
			{
				minX = Math.Min(minX, cell.x);
				maxX = Math.Max(maxX, cell.x);
				minZ = Math.Min(minZ, cell.z);
				maxZ = Math.Max(maxZ, cell.z);
			}
			IntVec3 a = new IntVec3(minX, 0, minZ);
			IntVec3 b = new IntVec3(maxX, 0, maxZ);
			if (a == b)
			{
				return "at " + AgentPlace.Describe(map, a);
			}
			return "from " + AgentPlace.Describe(map, a) + " to " + AgentPlace.Describe(map, b);
		}

		// Number of 4-neighbour connected parts of a cell set.
		private static int CountComponents(List<IntVec3> cells)
		{
			HashSet<IntVec3> rest = new HashSet<IntVec3>(cells);
			int parts = 0;
			foreach (IntVec3 start in cells)
			{
				if (!rest.Contains(start))
				{
					continue;
				}
				parts++;
				FloodWithin(rest, start, true);
			}
			return parts;
		}

		// How many of the cells connect (4-neighbour) to start, as Zone.CheckContiguous's flood fill does.
		private static int ReachableCount(List<IntVec3> cells, IntVec3 start)
		{
			return FloodWithin(new HashSet<IntVec3>(cells), start, false);
		}

		private static int FloodWithin(HashSet<IntVec3> set, IntVec3 start, bool consume)
		{
			HashSet<IntVec3> seen = new HashSet<IntVec3>();
			Queue<IntVec3> queue = new Queue<IntVec3>();
			seen.Add(start);
			queue.Enqueue(start);
			while (queue.Count > 0)
			{
				IntVec3 cell = queue.Dequeue();
				for (int d = 0; d < 4; d++)
				{
					IntVec3 n = cell + GenAdj.CardinalDirections[d];
					if (set.Contains(n) && seen.Add(n))
					{
						queue.Enqueue(n);
					}
				}
			}
			if (consume)
			{
				foreach (IntVec3 cell in seen)
				{
					set.Remove(cell);
				}
			}
			return seen.Count;
		}
	}
}
