using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Agent 08, brief S1: build one thing at / near a place, cancel blueprints and frames, two vocabulary lines.
	// FormPlanner.Dispatch calls PlanAt / PlanNear / PlanCancelAt / PlanCancelArea (agent 10 adds the case lines);
	// each returns a Plan whose Writes run only after the player confirms. Blueprints only: colonists build.
	// Fair play: nothing here reads or sets dev mode or DebugSettings; every question and every change goes
	// through the same game calls a player's click makes, and nothing under fog is printed, counted or accepted
	// (AgentPlace.Visible). MAIN THREAD ONLY (Dispatch and Write.apply).
	//
	// Game facts relied on (decompiled tree sources\RimWorldDecompiled\, line numbers of that tree):
	//   GenConstruct.PlaceBlueprintForBuild                    RimWorld\GenConstruct.cs:29
	//   GenConstruct.CanPlaceBlueprintAt (the placement check) RimWorld\GenConstruct.cs:231
	//   GenAdj.OccupiedRect(center, rot, size)                 Verse\GenAdj.cs:595
	//   GenRadial.RadialCellsAround(center, radius, useCenter) Verse\GenRadial.cs:204
	//   BuildableDef.Size / BuildableByPlayer / defaultPlacingRot  Verse\BuildableDef.cs:140 / :144 / :36
	//   ThingDef.rotatable; the build tool only offers turning for such a def  Verse\ThingDef.cs:25,
	//       RimWorld\Designator_Place.cs:109,124
	//   ThingDef.hasInteractionCell                            Verse\ThingDef.cs:82
	//   ThingUtility.InteractionCellWhenAt                     Verse\ThingUtility.cs:83
	//   BedUtility.GetSleepingSlotPos (head end; N/E/S/W cases) RimWorld\BedUtility.cs:37, :24-27
	//   Designator_Build.Visible (build menu) / constructor    RimWorld\Designator_Build.cs:109 / :263
	//   PlaceWorker.PostPlace after a blueprint is placed      RimWorld\Designator_Build.cs:539-545
	//   Designator_Cancel: player's Frame/Blueprint only       RimWorld\Designator_Cancel.cs:95
	//   Designator_Cancel.DesignateThing: Destroy(DestroyMode.Cancel)  RimWorld\Designator_Cancel.cs:100-102
	//   Thing.Destroy runs GenLeaving.DoLeavingsFor            Verse\Thing.cs:1079
	//   A Blueprint is not a Building, so it leaves nothing    RimWorld\GenLeaving.cs:280-285
	//   Cancel on a Frame: resourceContainer stacks go to the ground, fraction 1 (everything)
	//       RimWorld\GenLeaving.cs:13 (LeaveFraction_Cancel = 1f), :114-127 (Frame branch),
	//       :296 (Cancel => leaves resources), :321 (Cancel => RoundRandom(count * 1f) = count)
	//   ResourceCounter.GetCount (only what lies in storage)   RimWorld\ResourceCounter.cs:75
	//   Thing.IsInAnyStorage                                   RimWorld\StoreUtility.cs:38
	//   Thing.IsForbidden(Faction)                             RimWorld\ForbidUtility.cs:191
	//   ListerThings.ThingsOfDef                               Verse\ListerThings.cs:52
	//   IntVec3.GetThingList(map)                              Verse\GridsUtility.cs:67
	//   TerrainGrid.TerrainAt(IntVec3)                         Verse\TerrainGrid.cs:89
	//   IConstructible.EntityToBuildStuff                      RimWorld\IConstructible.cs:14
	//   ThingDef.entityDefToBuild / IsBlueprint / IsFrame      Verse\ThingDef.cs:373 / :896 / :894
	//   Rot4.IsHorizontal                                      Verse\Rot4.cs:118
	//   CellRect.Width / Height                                Verse\CellRect.cs:84 / :100
	public static class AgentBuild
	{
		private const int MaxCount = 50;
		private const float NearRadius = 15f;
		// Same cap as VocabularyText.BuildingCap (a private const there, VocabularyText.cs:20).
		private const int BuildingCap = 250;
		private const int FloorCap = 30;
		private const int MaxKindsNamed = 8;

		// ---- the four forms -------------------------------------------------------------------------

		// Spots are placed, not built (user 2026-10-04: "you cant "BUILD" something like animal sleeping spots or bed
		// spots. you have to PLACE them."; 2026-10-06: "build sleeping spots need to be blocked with "you can only place
		// them". same for other SPOTS"). The game's build tool puts a thing with WorkToBuild 0 down at once instead of
		// a blueprint (Designator_Build.DesignateSingleCell, RimWorld\Designator_Build.cs:474-500); those are the ones
		// "place" takes and "build" refuses.
		internal static bool PlacedNotBuilt(ThingDef def, ThingDef stuff)
		{
			return def != null && def.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff) == 0f;
		}

		// Every build form calls this after it knows the building: "build" with a spot is refused.
		internal static void RefuseSpot(ThingDef def)
		{
			if (PlacedNotBuilt(def, null))
			{
				string label = LabelOf(def);
				throw new Fault("You can only place " + label + ", not build it: say 'place " + label + " at SPOT.' or 'place " + label + " near SPOT.'");
			}
		}

		private static void CheckPlaceOrBuild(ThingDef def, ThingDef stuff, bool place)
		{
			if (!place)
			{
				RefuseSpot(def);
			}
			else if (!PlacedNotBuilt(def, stuff))
			{
				string label = LabelOf(def);
				throw new Fault(FormPlanner.UpperFirst(label) + " is built, not placed (only spots are placed): say 'build " + label + " at SPOT.' or 'build " + label + " near SPOT.'");
			}
		}

		// Form "build at": BUILDING, STUFF?, SPOT, FACING?.
		public static PlanReply PlanAt(FormContext c)
		{
			return PlanAt(c, false);
		}

		// Form "place at": BUILDING, SPOT, FACING? (a spot, put down at once).
		public static PlanReply PlanPlaceAt(FormContext c)
		{
			return PlanAt(c, true);
		}

		private static PlanReply PlanAt(FormContext c, bool place)
		{
			Map map = c.map;
			ThingDef def = ResolveThing(c.Text("BUILDING"));
			if (!place)
			{
				RefuseSpot(def);
			}
			IntVec3 cell = SpotCell(c);
			string label = LabelOf(def);
			Plan plan = FormPlanner.NewPlan(place ? "agent place at" : "agent build at");
			ThingDef stuff = ResolveStuffFor(map, def, c.Text("STUFF"), 1, new Dictionary<ThingDef, int>(), plan.notes);
			Rot4 rot = RotFor(def, c.Text("FACING"), plan.notes);
			CheckPlaceOrBuild(def, stuff, place);

			string problem = CellProblem(map, def, cell, rot, stuff);
			if (problem != null)
			{
				throw new Fault("Can't " + (place ? "place " : "build ") + label + " at " + Xz(cell) + ": " + problem);
			}

			Dictionary<ThingDef, int> needed = new Dictionary<ThingDef, int>();
			BuildDefs.AddCosts(needed, def, stuff, 1);
			AddMaterialNotes(plan, map, needed);

			string line = (place ? "Place " : "Build ") + label + StuffPart(stuff) + " at " + AgentPlace.Describe(map, cell);
			string facing = FacingText(map, def, cell, rot);
			if (facing.Length > 0)
			{
				line += ", " + facing;
			}
			line += ".";

			AgentWindCheck.Note(plan, map, def, cell, rot);
			AgentLightCheck.Note(plan, map, def, cell, rot);
			AgentWorkspotCheck.Note(plan, map, def, cell, rot);
			AgentDoorCheck.Note(plan, map, def, cell, rot);
			AgentCoolerCheck.Note(plan, map, def, cell, rot);
			AgentVentCheck.Note(plan, map, def, cell, rot);
			plan.writes.Add(place ? PlaceWrite(map, def, cell, rot, stuff, label) : BlueprintWrite(map, def, cell, rot, stuff, label));
			return FormPlanner.Reply(plan, line, null, null);
		}

		// Form "build anywhere": BUILDING, STUFF? (user 2026-10-04: "anywhere where it can be placed should be a thing
		// for geothermal"). Only for a building the game allows on one kind of spot: a geothermal generator must
		// stand on a steam geyser (PlaceWorker_OnSteamGeyser.AllowsPlacing, RimWorld\PlaceWorker_OnSteamGeyser.cs:8-16:
		// a geyser whose own position is the cell). The mod takes the free geyser in view nearest the colonists.
		// Anything else can stand in many places, and the player says where.
		public static PlanReply PlanAnywhere(FormContext c)
		{
			Map map = c.map;
			ThingDef def = ResolveThing(c.Text("BUILDING"));
			RefuseSpot(def);
			string label = LabelOf(def);
			bool onGeyser = def.PlaceWorkers != null && def.PlaceWorkers.Any((PlaceWorker w) => w is PlaceWorker_OnSteamGeyser);
			if (!onGeyser)
			{
				throw new Fault(FormPlanner.UpperFirst(label) + " can stand in many places, so say where: 'build " + label + " at SPOT.' or 'build " + label + " near SPOT.'");
			}
			Plan plan = FormPlanner.NewPlan("agent build anywhere");
			ThingDef stuff = ResolveStuffFor(map, def, c.Text("STUFF"), 1, new Dictionary<ThingDef, int>(), plan.notes);
			Rot4 rot = def.defaultPlacingRot;
			IntVec3 origin = AgentPlace.ColonyCentre(map);
			List<Thing> geysers = map.listerThings.ThingsOfDef(ThingDefOf.SteamGeyser)
				.Where((Thing g) => g.Spawned && AgentPlace.Visible(map, g.Position))
				.OrderBy((Thing g) => g.Position.DistanceToSquared(origin)).ToList();
			if (geysers.Count == 0)
			{
				throw new Fault("No steam geyser is in view, and a " + label + " can only stand on one.");
			}
			IntVec3 cell = IntVec3.Invalid;
			List<string> taken = new List<string>();
			foreach (Thing g in geysers)
			{
				string problem = CellProblem(map, def, g.Position, rot, stuff);
				if (problem == null)
				{
					cell = g.Position;
					break;
				}
				taken.Add(Xz(g.Position) + ": " + problem.Trim().TrimEnd('.'));
			}
			if (!cell.IsValid)
			{
				throw new Fault("No steam geyser in view can take a " + label + " now: " + string.Join("; ", taken.Take(5).ToArray()) + ".");
			}
			Dictionary<ThingDef, int> needed = new Dictionary<ThingDef, int>();
			BuildDefs.AddCosts(needed, def, stuff, 1);
			AddMaterialNotes(plan, map, needed);
			plan.notes.Add("Chosen: the free steam geyser nearest the colonists, of " + geysers.Count + " in view. For another one say 'build " + label + " at X, Z.' with its cell ('where are steam geysers.').");
			string line = "Build " + label + StuffPart(stuff) + " on the steam geyser at " + AgentPlace.Describe(map, cell) + ".";
			AgentWindCheck.Note(plan, map, def, cell, rot);
			AgentLightCheck.Note(plan, map, def, cell, rot);
			plan.writes.Add(BlueprintWrite(map, def, cell, rot, stuff, label));
			return FormPlanner.Reply(plan, line, null, null);
		}

		// Form "build near": COUNT?, BUILDING, STUFF?, SPOT, FACING?.
		public static PlanReply PlanNear(FormContext c)
		{
			return PlanNear(c, false);
		}

		// Form "place near": COUNT?, BUILDING, SPOT, FACING? (spots, put down at once).
		public static PlanReply PlanPlaceNear(FormContext c)
		{
			return PlanNear(c, true);
		}

		private static PlanReply PlanNear(FormContext c, bool place)
		{
			Map map = c.map;
			ThingDef def = ResolveThing(c.Text("BUILDING"));
			if (!place)
			{
				RefuseSpot(def);
			}
			IntVec3 centre = SpotCell(c);
			string label = LabelOf(def);

			int count = 1;
			double? asked = c.Number("COUNT");
			if (asked != null)
			{
				count = (int)Math.Round(asked.Value);
				if (count < 1)
				{
					throw new Fault("The count must be at least 1.");
				}
				if (count > MaxCount)
				{
					throw new Fault("At most " + MaxCount + " " + label + " can be built in one order; " + count + " were asked.");
				}
			}

			Plan plan = FormPlanner.NewPlan(place ? "agent place near" : "agent build near");
			ThingDef stuff = ResolveStuffFor(map, def, c.Text("STUFF"), count, new Dictionary<ThingDef, int>(), plan.notes);
			Rot4 rot = RotFor(def, c.Text("FACING"), plan.notes);
			CheckPlaceOrBuild(def, stuff, place);

			// Nearest valid cells first (RadialCellsAround is ordered by distance, GenRadial.cs:204). A pick may not
			// touch a cell an earlier pick reserved, and its interaction cell may not lie in an earlier footprint.
			HashSet<IntVec3> footprints = new HashSet<IntVec3>();
			HashSet<IntVec3> interactions = new HashSet<IntVec3>();
			List<IntVec3> picks = new List<IntVec3>();
			foreach (IntVec3 candidate in GenRadial.RadialCellsAround(centre, NearRadius, true))
			{
				if (picks.Count >= count)
				{
					break;
				}
				if (CellProblem(map, def, candidate, rot, stuff) != null)
				{
					continue;
				}
				List<IntVec3> rect = GenAdj.OccupiedRect(candidate, rot, def.Size).Cells.ToList();
				bool clash = false;
				foreach (IntVec3 cell in rect)
				{
					if (footprints.Contains(cell) || interactions.Contains(cell))
					{
						clash = true;
						break;
					}
				}
				if (clash)
				{
					continue;
				}
				IntVec3? work = null;
				if (def.hasInteractionCell)
				{
					work = ThingUtility.InteractionCellWhenAt(def, candidate, rot, map);
					if (footprints.Contains(work.Value))
					{
						continue;
					}
				}
				foreach (IntVec3 cell in rect)
				{
					footprints.Add(cell);
				}
				if (work != null)
				{
					interactions.Add(work.Value);
				}
				picks.Add(candidate);
			}

			string centreText = AgentPlace.Describe(map, centre);
			if (picks.Count == 0)
			{
				throw new Fault("There is no free place for " + label + " within " + (int)NearRadius + " tiles of " + centreText + ".");
			}
			if (picks.Count < count)
			{
				throw new Fault("Only " + picks.Count + " free " + (picks.Count == 1 ? "place" : "places") + " for " + label
					+ " within " + (int)NearRadius + " tiles of " + centreText + "; " + count + " " + (count == 1 ? "was" : "were") + " asked.");
			}

			Dictionary<ThingDef, int> needed = new Dictionary<ThingDef, int>();
			BuildDefs.AddCosts(needed, def, stuff, picks.Count);
			AddMaterialNotes(plan, map, needed);

			string line = (place ? "Place " : "Build ") + (picks.Count > 1 ? picks.Count + " " : "") + label + StuffPart(stuff) + " near " + centreText + ": "
				+ string.Join(", ", picks.Select((IntVec3 p) => AgentPlace.Describe(map, p)).ToArray());
			List<string> clauses = new List<string>();
			string head = BedHeadText(def, picks[0], rot);
			if (head.Length > 0)
			{
				clauses.Add(head);
			}
			if (def.hasInteractionCell)
			{
				List<string> workers = new List<string>();
				foreach (IntVec3 p in picks)
				{
					IntVec3 spot = ThingUtility.InteractionCellWhenAt(def, p, rot, map);
					if (AgentPlace.Visible(map, spot))
					{
						workers.Add(AgentPlace.Describe(map, spot));
					}
				}
				if (workers.Count > 0)
				{
					clauses.Add("worker stands at " + string.Join(", ", workers.ToArray()));
				}
			}
			if (clauses.Count > 0)
			{
				line += "; " + string.Join("; ", clauses.ToArray());
			}
			line += ".";

			AgentWindCheck.Note(plan, map, def, picks, rot);
			AgentLightCheck.Note(plan, map, def, picks, rot);
			AgentWorkspotCheck.Note(plan, map, def, picks, rot);
			AgentDoorCheck.Note(plan, map, def, picks, rot);
			AgentCoolerCheck.Note(plan, map, def, picks, rot);
			AgentVentCheck.Note(plan, map, def, picks, rot);
			foreach (IntVec3 p in picks)
			{
				plan.writes.Add(place ? PlaceWrite(map, def, p, rot, stuff, label) : BlueprintWrite(map, def, p, rot, stuff, label));
			}
			return FormPlanner.Reply(plan, line, null, null);
		}

		// Form "cancel blueprint at": BUILDING?, SPOT.
		public static PlanReply PlanCancelAt(FormContext c)
		{
			Map map = c.map;
			IntVec3 cell = SpotCell(c);
			string building = c.Text("BUILDING");
			List<Thing> found = BlueprintsAt(map, cell, building);
			if (found.Count == 0)
			{
				throw new Fault("There is no " + (building != null ? building + " " : "") + "blueprint at " + Xz(cell) + ".");
			}

			Plan plan = FormPlanner.NewPlan("agent cancel blueprint");
			string line;
			bool frames = found.Any((Thing t) => t.def.IsFrame);
			if (found.Count == 1)
			{
				line = "Cancel the " + BlueprintLabel(found[0]) + " " + Noun(found, false) + " at " + AgentPlace.Describe(map, cell);
			}
			else
			{
				line = "Cancel " + found.Count + " " + Noun(found, true) + " at " + AgentPlace.Describe(map, cell) + ": " + KindSummary(found);
			}
			line += FrameRefundText(frames) + ".";
			AddFrameNote(plan, frames);

			foreach (Thing t in found)
			{
				plan.writes.Add(CancelWrite(t));
			}
			return FormPlanner.Reply(plan, line, null, null);
		}

		// Form "cancel blueprint area": BUILDING?, AREA.
		public static PlanReply PlanCancelArea(FormContext c)
		{
			Map map = c.map;
			string areaText = c.Text("AREA");
			if (areaText == null)
			{
				throw new Fault("The order did not say which area.");
			}
			CellRect rect;
			string reason;
			if (!AgentPlace.TryRect(map, areaText, out rect, out reason))
			{
				throw new Fault(string.IsNullOrEmpty(reason) ? "I could not find the area '" + areaText + "'." : reason);
			}
			string building = c.Text("BUILDING");
			string corners = rect.minX.ToString(CultureInfo.InvariantCulture) + ", " + rect.minZ.ToString(CultureInfo.InvariantCulture)
				+ " to " + rect.maxX.ToString(CultureInfo.InvariantCulture) + ", " + rect.maxZ.ToString(CultureInfo.InvariantCulture);

			// A multi-cell blueprint is counted once, at its own Position, and only when that cell is in view.
			List<Thing> found = new List<Thing>();
			HashSet<Thing> seen = new HashSet<Thing>();
			foreach (IntVec3 cell in rect.Cells)
			{
				if (!AgentPlace.Visible(map, cell))
				{
					continue;
				}
				foreach (Thing t in cell.GetThingList(map).ToList())
				{
					if (t.Position == cell && IsPlayerBlueprint(t) && Matches(t, building) && seen.Add(t))
					{
						found.Add(t);
					}
				}
			}
			if (found.Count == 0)
			{
				throw new Fault("There is no " + (building != null ? building + " " : "") + "blueprint from " + corners + ".");
			}

			Plan plan = FormPlanner.NewPlan("agent cancel blueprint");
			bool frames = found.Any((Thing t) => t.def.IsFrame);
			string line = "Cancel " + found.Count + " " + Noun(found, found.Count != 1) + " from " + AgentPlace.DescribeRect(map, rect)
				+ ": " + KindSummary(found) + FrameRefundText(frames) + ".";
			AddFrameNote(plan, frames);
			foreach (Thing t in found)
			{
				plan.writes.Add(CancelWrite(t));
			}
			return FormPlanner.Reply(plan, line, null, null);
		}

		// ---- vocabulary -----------------------------------------------------------------------------

		// VocabularyText.Buildings() (same cap, same natural-rock rule), but a def is listed only when the build
		// menu would show it (BuildDefs.IsAvailable = Designator_Build.Visible: research, tech level, prerequisite
		// buildings, difficulty), not merely when its research is finished. Each label once.
		public static IEnumerable<string> BuildingsNow()
		{
			return BuildableNow().Select((ThingDef def) => def.label);
		}

		// The defs behind BuildingsNow, so "list buildings." lists exactly the buildings line.
		private static IEnumerable<ThingDef> BuildableNow()
		{
			HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			int count = 0;
			foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if (count >= BuildingCap)
				{
					yield break;
				}
				if (!BuildDefs.IsPlayerBuildable(def) || def.label.NullOrEmpty()
					|| (def.building != null && def.building.isNaturalRock))
				{
					continue;
				}
				string ignored;
				if (!BuildDefs.IsAvailable(def, out ignored))
				{
					continue;
				}
				if (!seen.Add(def.label))
				{
					continue;
				}
				count++;
				yield return def;
			}
		}

		// "list buildings." / "list CATEGORY." (user 2026-10-17: "list buildings will show every building the agent can
		// currently build. the list can be sorted based on categories" / "(ignore vehicles and ship for now)"). One line per
		// build menu category (DesignationCategoryDef, in the menu's order), the buildings line's entries in it; floors are the
		// floors line. A category with nothing in it is left out of "list buildings."; asked by name, it gets the user's line
		// (user 2026-10-17). A name that is no category: the user picked "Name + category list".
		private static readonly string[] SkippedCategories = { "ship", "vehicles" };

		public static QueryReply QueryBuildings(string category)
		{
			List<DesignationCategoryDef> cats = DefDatabase<DesignationCategoryDef>.AllDefsListForReading
				.Where((DesignationCategoryDef d) => !d.label.NullOrEmpty() && !SkippedCategories.Contains(d.label.ToLowerInvariant()))
				.OrderBy((DesignationCategoryDef d) => d.order).ToList();
			List<ThingDef> defs = BuildableNow().ToList();
			List<string> floors = Floors().ToList();
			Func<DesignationCategoryDef, List<string>> entries = (DesignationCategoryDef cat) => cat == DesignationCategoryDefOf.Floors
				? floors
				: defs.Where((ThingDef d) => d.designationCategory == cat).Select((ThingDef d) => d.label).ToList();
			// Orders and Zone hold no buildings, so they are never a line; asked by name they are no category either.
			cats = cats.Where((DesignationCategoryDef cat) => cat == DesignationCategoryDefOf.Floors
				|| DefDatabase<ThingDef>.AllDefsListForReading.Any((ThingDef d) => d.designationCategory == cat && BuildDefs.IsPlayerBuildable(d))).ToList();
			string label(DesignationCategoryDef cat) => cat.label.ToLowerInvariant();

			if (category != null)
			{
				string want = category.Trim().ToLowerInvariant();
				DesignationCategoryDef cat = cats.FirstOrDefault((DesignationCategoryDef c) => label(c) == want);
				if (cat == null)
				{
					return QueryReply.Fail(category.Trim() + " is not a category. Categories: " + string.Join(", ", cats.Select(label).ToArray()) + ".");
				}
				List<string> one = entries(cat);
				if (one.Count == 0)
				{
					return QueryReply.Ok("Nothing cannot be built here, Likely due to research/ideology. Consider researching first.");
				}
				return QueryReply.Ok(label(cat) + ": " + string.Join(", ", one.ToArray()));
			}
			List<string> lines = new List<string>();
			foreach (DesignationCategoryDef cat in cats)
			{
				List<string> some = entries(cat);
				if (some.Count > 0)
				{
					lines.Add(label(cat) + ": " + string.Join(", ", some.ToArray()));
				}
			}
			return QueryReply.Ok(string.Join("\n", lines.ToArray()));
		}

		// Labels of the floors the build menu offers: BuildableByPlayer (BuildableDef.cs:144) and
		// Designator_Build.Visible (Designator_Build.cs:109, constructor :263). Each label once, at most 30.
		public static IEnumerable<string> Floors()
		{
			return FloorDefs().Select((TerrainDef def) => def.label);
		}

		// The defs behind Floors (the 12-hour room list prices each, AgentRoomScan.cs).
		internal static IEnumerable<TerrainDef> FloorDefs()
		{
			HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			int count = 0;
			foreach (TerrainDef def in DefDatabase<TerrainDef>.AllDefsListForReading)
			{
				if (count >= FloorCap)
				{
					yield break;
				}
				if (!def.BuildableByPlayer || def.label.NullOrEmpty())
				{
					continue;
				}
				bool visible;
				try
				{
					visible = new Designator_Build(def).Visible;
				}
				catch (Exception)
				{
					visible = def.IsResearchFinished;
				}
				if (!visible || !seen.Add(def.label))
				{
					continue;
				}
				count++;
				yield return def;
			}
		}

		// ---- shared helpers (S2 calls these) --------------------------------------------------------

		// One buildable ThingDef for the player's word, or a Fault in plain words.
		internal static ThingDef ResolveThing(string name)
		{
			string wanted = (name ?? "").Trim();
			if (wanted.Length == 0)
			{
				throw new Fault("The order did not say what to build.");
			}
			List<ThingDef> found = BuildDefs.FindBuildables(wanted);
			if (found.Count == 0)
			{
				throw new Fault("Nothing the colony can build is called '" + wanted + "'.");
			}
			if (found.Count > 1)
			{
				List<ThingDef> available = found.Where((ThingDef d) =>
				{
					string ignored;
					return BuildDefs.IsAvailable(d, out ignored);
				}).ToList();
				if (available.Count > 0)
				{
					found = available;
				}
			}
			if (found.Count > 1)
			{
				List<string> names = found.Select((ThingDef d) => LabelOf(d)).ToList();
				if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() < names.Count)
				{
					names = found.Select((ThingDef d) => d.defName).ToList();
				}
				throw new Fault("'" + wanted + "' could be " + FormPlanner.JoinNames(names) + "; say which.");
			}
			ThingDef def = found[0];
			string reason;
			if (!BuildDefs.IsAvailable(def, out reason))
			{
				throw new Fault("The colony can't build " + LabelOf(def) + ": " + (reason ?? "it can't be built yet") + ".");
			}
			return def;
		}

		// BuildDefs.ResolveStuff for `pieces` pieces (needed = CostStuffCount * pieces); a problem -> Fault; adds
		// BuildDefs.AutoStuffNote to notes when it gives one. null when the def takes no material.
		internal static ThingDef ResolveStuffFor(Map map, ThingDef def, string stuffName, int pieces,
			Dictionary<ThingDef, int> claimed, List<string> notes)
		{
			if (!def.MadeFromStuff)
			{
				if (!string.IsNullOrWhiteSpace(stuffName) && notes != null)
				{
					notes.Add(FormPlanner.UpperFirst(LabelOf(def)) + " is not made of a material, so '" + stuffName.Trim() + "' is ignored.");
				}
				return null;
			}
			int needed = def.CostStuffCount * Math.Max(1, pieces);
			BuildDefs.AutoStuff how;
			string problem;
			ThingDef stuff = BuildDefs.ResolveStuff(def, stuffName, map, needed, claimed, out how, out problem);
			if (problem != null)
			{
				List<string> allowed = GenStuff.AllowedStuffsFor(def).Select((ThingDef s) => s.label.NullOrEmpty() ? s.defName : s.label).ToList();
				throw new Fault(FormPlanner.UpperFirst(problem) + (allowed.Count > 0 ? "; it can be made of " + FormPlanner.JoinNames(allowed) : "") + ".");
			}
			string note = BuildDefs.AutoStuffNote(def, stuff, how);
			if (note != null && notes != null)
			{
				notes.Add(note);
			}
			return stuff;
		}

		// FACING -> Rot4; null/empty -> def.defaultPlacingRot. A def that is not rotatable keeps its default and a
		// note says the facing was ignored (the build tool offers turning only for ThingDef.rotatable,
		// Designator_Place.cs:109,124).
		internal static Rot4 RotFor(ThingDef def, string facing, List<string> notes)
		{
			string f = (facing ?? "").Trim().ToLowerInvariant();
			if (f.Length == 0)
			{
				return def.defaultPlacingRot;
			}
			Rot4 rot;
			switch (f)
			{
				case "north":
					rot = Rot4.North;
					break;
				case "south":
					rot = Rot4.South;
					break;
				case "east":
					rot = Rot4.East;
					break;
				case "west":
					rot = Rot4.West;
					break;
				default:
					throw new Fault("A facing is north, south, east or west.");
			}
			if (!def.rotatable)
			{
				if (notes != null)
				{
					notes.Add("The " + f + " facing is ignored: " + LabelOf(def) + " can't be turned.");
				}
				return def.defaultPlacingRot;
			}
			return rot;
		}

		// "" or one short clause: a bed's head end; a def with an interaction cell: where the worker stands.
		internal static string FacingText(Map map, ThingDef def, IntVec3 cell, Rot4 rot)
		{
			List<string> parts = new List<string>();
			string head = BedHeadText(def, cell, rot);
			if (head.Length > 0)
			{
				parts.Add(head);
			}
			if (def.hasInteractionCell)
			{
				IntVec3 spot = ThingUtility.InteractionCellWhenAt(def, cell, rot, map);
				if (AgentPlace.Visible(map, spot))
				{
					parts.Add("worker stands at " + AgentPlace.Describe(map, spot));
				}
			}
			return string.Join("; ", parts.ToArray());
		}

		// null when a blueprint of def can go there now, else the reason in plain words (a capitalised sentence).
		internal static string CellProblem(Map map, BuildableDef def, IntVec3 cell, Rot4 rot, ThingDef stuff)
		{
			if (map == null || def == null)
			{
				return "The map is not available.";
			}
			CellRect rect = GenAdj.OccupiedRect(cell, rot, def.Size);
			foreach (IntVec3 c in rect)
			{
				if (!c.InBounds(map))
				{
					return "That spot is off the map.";
				}
				if (!AgentPlace.Visible(map, c))
				{
					return "That spot has not been explored yet.";
				}
			}
			// The game's own check (GenConstruct.cs:231), as the build tool runs it (godMode false).
			AcceptanceReport report = GenConstruct.CanPlaceBlueprintAt(def, cell, rot, map, false, null, null, stuff);
			if (!report.Accepted)
			{
				return report.Reason.NullOrEmpty() ? "The game does not allow it there." : report.Reason;
			}
			// The mod never replaces a building (BuildDefs.FreeOfBuildings); floors are not buildings.
			// A power conduit replaces nothing: the game lays it under walls, doors and most buildings
			// (CanPlaceBlueprintAt above already accepted the cell), so the extra check is skipped for it.
			// Without this every conduit onto a wall cell was refused ("A building or blueprint is already
			// there") and no room could be powered from outside (user, 2026-10-12, "fix that").
			ThingDef placedDef = def as ThingDef;
			bool conduit = placedDef != null && placedDef.building != null && placedDef.building.isPowerConduit;
			if (def is ThingDef && !conduit)
			{
				foreach (IntVec3 c in rect)
				{
					if (!BuildDefs.FreeOfBuildings(c, map) && !OnlyAllowedUnderneath(def, c, map) && !OverPlaceableWall(placedDef, stuff, c, map))
					{
						return "A building or blueprint is already there.";
					}
				}
			}
			return null;
		}

		// True when every building on the cell is a wall the game lets this def go over, the builder taking it down itself:
		// - a door, vent or cooler (canPlaceOverWall, Buildings_Structure.xml:39, Buildings_Temperature.xml:311, :373) on a
		//   wall (isPlaceOverableWall, :295), as GenConstruct.cs:571 allows;
		// - a built wall of another material (same replaceTags, GenConstruct.CanReplace, :380-395).
		// Without this a door in a wall shared with another room was always refused ("A building or blueprint is already
		// there", 2026-10-16).
		private static bool OverPlaceableWall(ThingDef def, ThingDef stuff, IntVec3 c, Map map)
		{
			if (def == null || def.building == null)
			{
				return false;
			}
			List<Thing> things = c.GetThingList(map);
			for (int i = 0; i < things.Count; i++)
			{
				Thing t = things[i];
				ThingDef td = t.def;
				if (td.category != ThingCategory.Building && !td.IsBlueprint && !td.IsFrame)
				{
					continue;
				}
				ThingDef built = (td.entityDefToBuild as ThingDef) ?? td;
				if (built.building != null && built.building.isPowerConduit)
				{
					continue;
				}
				if (def.building.canPlaceOverWall && built.building != null && (built.building.isPlaceOverableWall || built.IsSmoothed))
				{
					continue;
				}
				if (td.category == ThingCategory.Building && GenConstruct.CanReplace(def, td, stuff, t.Stuff))
				{
					continue;
				}
				return false;
			}
			return true;
		}

		// True when every building on the cell is one the def's own place workers put it over on purpose: a steam
		// geyser under a geothermal generator (PlaceWorker.ForceAllowPlaceOver, PlaceWorker_OnSteamGeyser.cs:18-21).
		// Without this the "never replaces a building" check refused every geothermal generator.
		private static bool OnlyAllowedUnderneath(BuildableDef def, IntVec3 c, Map map)
		{
			if (def.PlaceWorkers == null || def.PlaceWorkers.Count == 0)
			{
				return false;
			}
			List<Thing> things = c.GetThingList(map);
			for (int i = 0; i < things.Count; i++)
			{
				ThingDef td = things[i].def;
				if (td.category != ThingCategory.Building && !td.IsBlueprint && !td.IsFrame)
				{
					continue;
				}
				if (td.building != null && td.building.isPowerConduit)
				{
					continue;
				}
				if (!def.PlaceWorkers.Any((PlaceWorker w) => w.ForceAllowPlaceOver(td)))
				{
					return false;
				}
			}
			return true;
		}

		// One line per material (joined with a newline): "wood: 140 needed; 0 in storage, 310 lying around" and,
		// when storage + lying around is less than needed, "; short by 30, the blueprints wait for more".
		internal static string MaterialText(Map map, Dictionary<ThingDef, int> needed)
		{
			List<string> lines = new List<string>();
			foreach (KeyValuePair<ThingDef, int> kv in needed)
			{
				// ResourceCounter.GetCount: only what lies in stockpiles (ResourceCounter.cs:75).
				int inStorage = map != null ? map.resourceCounter.GetCount(kv.Key) : 0;
				int lying = 0;
				if (map != null)
				{
					// Spawned, in view, not forbidden to the player, not in storage (StoreUtility.cs:38, ForbidUtility.cs:191).
					List<Thing> things = map.listerThings.ThingsOfDef(kv.Key);
					for (int i = 0; i < things.Count; i++)
					{
						Thing t = things[i];
						if (t.Spawned && AgentPlace.Visible(map, t.Position) && !t.IsForbidden(Faction.OfPlayer) && !t.IsInAnyStorage())
						{
							lying += t.stackCount;
						}
					}
				}
				int forbidden = ForbiddenLying(map, kv.Key);
				string text = (kv.Key.label.NullOrEmpty() ? kv.Key.defName : kv.Key.label) + ": " + kv.Value + " needed; "
					+ inStorage + " in storage, " + lying + " lying around";
				if (forbidden > 0)
				{
					text += ", " + forbidden + " forbidden lying around";
				}
				int shortBy = kv.Value - inStorage - lying;
				if (shortBy > 0)
				{
					text += "; short by " + shortBy + ", the blueprints wait for more";
					if (forbidden > 0)
					{
						text += " (" + (forbidden >= shortBy ? "enough" : "not enough") + " once the forbidden ones are unforbidden)";
					}
				}
				lines.Add(text);
			}
			return string.Join("\n", lines.ToArray());
		}

		// Things of this def that are spawned, in view, forbidden to the player and not in storage: a fresh landing's
		// pod cargo. Neither ResourceCounter.GetCount (storage only) nor "lying around" counts them, so without this
		// the colony reads as having none (98981080, 2026-10-06: "Wood: 0.", "Steel: 0." with 225 of each forbidden).
		internal static int ForbiddenLying(Map map, ThingDef def)
		{
			int n = 0;
			if (map == null || def == null)
			{
				return 0;
			}
			List<Thing> things = map.listerThings.ThingsOfDef(def);
			for (int i = 0; i < things.Count; i++)
			{
				Thing t = things[i];
				if (t.Spawned && AgentPlace.Visible(map, t.Position) && t.IsForbidden(Faction.OfPlayer) && !t.IsInAnyStorage())
				{
					n += t.stackCount;
				}
			}
			return n;
		}

		// Places one blueprint now (main thread, inside a Write.apply): re-checks CellProblem first and returns null
		// (and logs one "[VerbalCommands] build: ..." line) when the cell is no longer usable.
		internal static Blueprint_Build PlaceNow(Map map, BuildableDef def, IntVec3 cell, Rot4 rot, ThingDef stuff)
		{
			string what = (def.label.NullOrEmpty() ? def.defName : def.label) + " at " + Xz(cell);
			if (map == null || Find.Maps == null || !Find.Maps.Contains(map))
			{
				Log.Message("[VerbalCommands] build: the map is no longer loaded; " + what + " not placed.");
				return null;
			}
			string problem = CellProblem(map, def, cell, rot, stuff);
			if (problem != null)
			{
				Log.Message("[VerbalCommands] build: " + what + " not placed: " + problem);
				return null;
			}
			// The research bench rule (ResearchBenchRule.cs, 2026-10-16): no bench without a researcher; one researcher's
			// bench moves here instead of a second one.
			string benchNote;
			ResearchBenchRule.Outcome benchRule = ResearchBenchRule.Check(map, def, cell, rot, out benchNote);
			ResearchBenchRule.Tell(benchNote);
			if (benchRule != ResearchBenchRule.Outcome.Place)
			{
				Log.Message("[VerbalCommands] build: " + what + " not placed: " + benchNote);
				return null;
			}
			// The outdoor battery rule (BatteryOutdoorRule.cs, 2026-10-16): an outdoor battery goes to the nearest indoor
			// spot instead, or is not placed.
			bool batteryMoved;
			string batteryNote;
			if (BatteryOutdoorRule.Check(map, def, stuff, ref cell, ref rot, out batteryMoved, out batteryNote))
			{
				BatteryOutdoorRule.Tell(batteryNote);
				if (!batteryMoved)
				{
					Log.Message("[VerbalCommands] build: " + what + " not placed: outdoors, and no indoor spot.");
					return null;
				}
				Log.Message("[VerbalCommands] build: " + what + " outdoors; placed at " + Xz(cell) + " instead.");
			}
			// GenConstruct.PlaceBlueprintForBuild (GenConstruct.cs:29-41), as BuildTools.PlaceOne does.
			Blueprint_Build blueprint = GenConstruct.PlaceBlueprintForBuild(def, cell, map, rot, Faction.OfPlayer, stuff);
			BuildTools.RecentBlueprints.Add(blueprint);
			// PlaceWorker.PostPlace for each place worker (Designator_Build.cs:539-545).
			if (def.PlaceWorkers != null)
			{
				for (int i = 0; i < def.PlaceWorkers.Count; i++)
				{
					def.PlaceWorkers[i].PostPlace(map, def, cell, rot);
				}
			}
			ThingDef thingDef = def as ThingDef;
			GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
			if (gc != null && thingDef != null)
			{
				gc.RecordModPlaced(map, cell, thingDef);
			}
			return blueprint;
		}

		private enum PlacedState
		{
			Pending,
			Built,
			Gone
		}

		// Follows a group of placements as ONE item of AgentFollow (kind "build"). Done when no cell has a blueprint
		// or frame of its def any more: a cell holding the built thing (a TerrainDef: TerrainAt(cell) == def,
		// TerrainGrid.cs:89) is built, one holding nothing of it is counted cancelled.
		internal static void TrackGroup(Map map, string label, List<KeyValuePair<IntVec3, BuildableDef>> placed)
		{
			List<KeyValuePair<IntVec3, BuildableDef>> items = new List<KeyValuePair<IntVec3, BuildableDef>>(placed);
			AgentFollow.Track("build", label, delegate
			{
				int pending = 0;
				int built = 0;
				int gone = 0;
				foreach (KeyValuePair<IntVec3, BuildableDef> kv in items)
				{
					switch (StateOf(map, kv.Key, kv.Value))
					{
						case PlacedState.Pending:
							pending++;
							break;
						case PlacedState.Built:
							built++;
							break;
						default:
							gone++;
							break;
					}
				}
				if (pending > 0)
				{
					return null;
				}
				return "built " + built + " of " + items.Count + (gone > 0 ? ", " + gone + " cancelled" : "");
			}, delegate
			{
				int left = 0;
				foreach (KeyValuePair<IntVec3, BuildableDef> kv in items)
				{
					if (StateOf(map, kv.Key, kv.Value) == PlacedState.Pending)
					{
						left++;
					}
				}
				return left + " of " + items.Count + " left";
			});
		}

		private static PlacedState StateOf(Map map, IntVec3 cell, BuildableDef def)
		{
			if (!cell.InBounds(map))
			{
				return PlacedState.Gone;
			}
			List<Thing> things = cell.GetThingList(map);
			for (int i = 0; i < things.Count; i++)
			{
				ThingDef td = things[i].def;
				if ((td.IsBlueprint || td.IsFrame) && td.entityDefToBuild == def)
				{
					return PlacedState.Pending;
				}
			}
			TerrainDef terrain = def as TerrainDef;
			if (terrain != null)
			{
				return map.terrainGrid.TerrainAt(cell) == terrain ? PlacedState.Built : PlacedState.Gone;
			}
			ThingDef thingDef = def as ThingDef;
			if (thingDef != null)
			{
				for (int i = 0; i < things.Count; i++)
				{
					if (things[i].def == thingDef)
					{
						return PlacedState.Built;
					}
				}
			}
			return PlacedState.Gone;
		}

		// ---- private pieces -------------------------------------------------------------------------

		private static string Xz(IntVec3 c)
		{
			return c.x.ToString(CultureInfo.InvariantCulture) + ", " + c.z.ToString(CultureInfo.InvariantCulture);
		}

		private static string LabelOf(BuildableDef def)
		{
			return def.label.NullOrEmpty() ? def.defName : def.label;
		}

		private static string StuffPart(ThingDef stuff)
		{
			return stuff != null ? " (" + (stuff.label.NullOrEmpty() ? stuff.defName : stuff.label) + ")" : "";
		}

		internal static IntVec3 SpotCell(FormContext c)
		{
			string spot = c.Text("SPOT");
			if (spot == null)
			{
				throw new Fault("The order did not say where.");
			}
			IntVec3 cell;
			string reason;
			if (!AgentPlace.TryCell(c.map, spot, out cell, out reason))
			{
				throw new Fault(string.IsNullOrEmpty(reason) ? "I could not find where '" + spot + "' is." : reason);
			}
			return cell;
		}

		private static void AddMaterialNotes(Plan plan, Map map, Dictionary<ThingDef, int> needed)
		{
			string text = MaterialText(map, needed);
			if (text.Length == 0)
			{
				return;
			}
			foreach (string line in text.Split('\n'))
			{
				plan.notes.Add(line);
			}
		}

		// "head at the north end": which end of the footprint BedUtility puts the sleeper's head (BedUtility.cs:37,
		// cases :24-27: facing north puts the head at the south end, east at the west end, south at the north end,
		// west at the east end). "" for anything that is not a bed or has no distinct ends.
		private static string BedHeadText(ThingDef def, IntVec3 cell, Rot4 rot)
		{
			if (def.thingClass == null || !typeof(Building_Bed).IsAssignableFrom(def.thingClass))
			{
				return "";
			}
			try
			{
				CellRect rect = GenAdj.OccupiedRect(cell, rot, def.Size);
				IntVec3 head = BedUtility.GetSleepingSlotPos(0, cell, rot, def.Size);
				if (rot.IsHorizontal)
				{
					if (rect.Width < 2)
					{
						return "";
					}
					return "head at the " + (head.x == rect.maxX ? "east" : "west") + " end";
				}
				if (rect.Height < 2)
				{
					return "";
				}
				return "head at the " + (head.z == rect.maxZ ? "north" : "south") + " end";
			}
			catch (Exception)
			{
				return "";
			}
		}

		// A blueprint's label with its material: "wooden wall"; a floor: its own label.
		private static string BlueprintLabel(Thing t)
		{
			BuildableDef built = t.def.entityDefToBuild;
			if (built == null)
			{
				return LabelOf(t.def);
			}
			ThingDef thingDef = built as ThingDef;
			if (thingDef != null)
			{
				IConstructible constructible = t as IConstructible;
				return BuildDefs.Label(thingDef, constructible != null ? constructible.EntityToBuildStuff() : null);
			}
			return LabelOf(built);
		}

		private static string KindLabel(Thing t)
		{
			BuildableDef built = t.def.entityDefToBuild;
			return built != null ? LabelOf(built) : LabelOf(t.def);
		}

		private static bool IsPlayerBlueprint(Thing t)
		{
			return t != null && (t.def.IsBlueprint || t.def.IsFrame) && t.def.entityDefToBuild != null && t.Faction == Faction.OfPlayer;
		}

		// BUILDING matches the label, the defName, the label with the material, or the plural of the label.
		private static bool Matches(Thing t, string building)
		{
			if (string.IsNullOrWhiteSpace(building))
			{
				return true;
			}
			BuildableDef built = t.def.entityDefToBuild;
			if (built == null)
			{
				return false;
			}
			if (FormPlanner.SameName(built.label, building) || FormPlanner.SameName(built.defName, building))
			{
				return true;
			}
			if (FormPlanner.SameName(BlueprintLabel(t), building))
			{
				return true;
			}
			try
			{
				LanguageWorker worker = Find.ActiveLanguageWorker;
				if (worker != null && !built.label.NullOrEmpty() && FormPlanner.SameName(worker.Pluralize(built.label), building))
				{
					return true;
				}
			}
			catch (Exception)
			{
			}
			return false;
		}

		private static List<Thing> BlueprintsAt(Map map, IntVec3 cell, string building)
		{
			List<Thing> result = new List<Thing>();
			if (!AgentPlace.Visible(map, cell))
			{
				return result;
			}
			// A multi-cell blueprint is in the list of every cell it covers; listed once.
			foreach (Thing t in cell.GetThingList(map).ToList())
			{
				if (IsPlayerBlueprint(t) && Matches(t, building) && !result.Contains(t))
				{
					result.Add(t);
				}
			}
			return result;
		}

		private static string Noun(List<Thing> things, bool plural)
		{
			bool blueprints = things.Any((Thing t) => t.def.IsBlueprint);
			bool frames = things.Any((Thing t) => t.def.IsFrame);
			if (blueprints && frames)
			{
				return plural ? "blueprints and frames" : "blueprint or frame";
			}
			if (frames)
			{
				return plural ? "frames" : "frame";
			}
			return plural ? "blueprints" : "blueprint";
		}

		// "wall x20, door x1"; at most 8 kinds named, then "and N more kinds".
		private static string KindSummary(List<Thing> things)
		{
			Dictionary<string, int> counts = new Dictionary<string, int>();
			List<string> order = new List<string>();
			foreach (Thing t in things)
			{
				string kind = KindLabel(t);
				if (!counts.ContainsKey(kind))
				{
					counts[kind] = 0;
					order.Add(kind);
				}
				counts[kind]++;
			}
			List<string> sorted = order.OrderByDescending((string k) => counts[k]).ToList();
			List<string> named = sorted.Take(MaxKindsNamed).Select((string k) => k + " x" + counts[k]).ToList();
			string text = string.Join(", ", named.ToArray());
			if (sorted.Count > MaxKindsNamed)
			{
				text += ", and " + (sorted.Count - MaxKindsNamed) + " more kinds";
			}
			return text;
		}

		// Cancelling a Frame gives back ALL material already delivered to it: Destroy(Cancel) runs
		// GenLeaving.DoLeavingsFor, the Frame branch moves each resourceContainer stack out with the Cancel
		// fraction RoundRandom(count * 1f) = count (GenLeaving.cs:114-127, :321). So no RISKY prefix; the confirm line
		// says so, and a note says the work already done on the frame is lost (Frame.workDone, Frame.cs:15).
		private static string FrameRefundText(bool frames)
		{
			return frames ? " (material already delivered to a frame comes back in full)" : "";
		}

		private static void AddFrameNote(Plan plan, bool frames)
		{
			if (frames)
			{
				plan.notes.Add("The building work already done on a frame is lost.");
			}
		}

		// What stands on a cell now, for the before/after readers.
		private static string CellReport(Map map, IntVec3 cell)
		{
			string where = Xz(cell);
			if (!AgentPlace.Visible(map, cell))
			{
				return where + ": not explored";
			}
			List<string> parts = new List<string>();
			foreach (Thing t in cell.GetThingList(map).ToList())
			{
				if (IsPlayerBlueprint(t))
				{
					parts.Add(BlueprintLabel(t) + (t.def.IsFrame ? " frame" : " blueprint"));
				}
				else if (t is Building && t.Faction == Faction.OfPlayer)
				{
					parts.Add(BuildDefs.Label(t.def, t.Stuff));
				}
			}
			return where + ": " + (parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "nothing built or planned");
		}

		private static string BlueprintReport(Thing t, IntVec3 at)
		{
			bool alive = t != null && !t.Destroyed && t.Spawned;
			return Xz(at) + ": " + (alive ? BlueprintLabel(t) + (t.def.IsFrame ? " frame" : " blueprint") : "no blueprint");
		}

		private static Write BlueprintWrite(Map map, ThingDef def, IntVec3 cell, Rot4 rot, ThingDef stuff, string label)
		{
			Write write = new Write();
			write.description = "Blueprint: " + label + " at " + Xz(cell);
			write.apply = delegate
			{
				Blueprint_Build blueprint = PlaceNow(map, def, cell, rot, stuff);
				if (blueprint == null)
				{
					throw new Fault("The " + label + " blueprint at " + Xz(cell) + " was not placed: the spot can't be built on any more.");
				}
				AgentFollow.Track(blueprint, label);
			};
			return write.Reads(() => CellReport(map, cell));
		}

		// A spot put down at once, as the game's build tool does for a thing with WorkToBuild 0: MakeThing, player
		// faction, Spawn (Designator_Build.cs:498-500), then PlaceWorker.PostPlace (:539-545). Re-checks the cell first.
		private static Write PlaceWrite(Map map, ThingDef def, IntVec3 cell, Rot4 rot, ThingDef stuff, string label)
		{
			Write write = new Write();
			write.description = "Place: " + label + " at " + Xz(cell);
			write.apply = delegate
			{
				if (map == null || Find.Maps == null || !Find.Maps.Contains(map))
				{
					throw new Fault("The map is no longer loaded; the " + label + " at " + Xz(cell) + " was not placed.");
				}
				string problem = CellProblem(map, def, cell, rot, stuff);
				if (problem != null)
				{
					throw new Fault("The " + label + " at " + Xz(cell) + " was not placed: " + problem);
				}
				Thing thing = ThingMaker.MakeThing(def, stuff);
				thing.SetFactionDirect(Faction.OfPlayer);
				GenSpawn.Spawn(thing, cell, map, rot);
				if (def.PlaceWorkers != null)
				{
					for (int i = 0; i < def.PlaceWorkers.Count; i++)
					{
						def.PlaceWorkers[i].PostPlace(map, def, cell, rot);
					}
				}
			};
			return write.Reads(() => CellReport(map, cell));
		}

		// Destroy(DestroyMode.Cancel), as the game's cancel tool does (Designator_Cancel.cs:100-102).
		private static Write CancelWrite(Thing target)
		{
			IntVec3 at = target.Position;
			string label = BlueprintLabel(target);
			Write write = new Write();
			write.description = "Cancel " + label + (target.def.IsFrame ? " frame" : " blueprint") + " at " + Xz(at);
			write.apply = delegate
			{
				if (target.Destroyed || !target.Spawned)
				{
					throw new Fault("The " + label + " at " + Xz(at) + " is already gone.");
				}
				target.Destroy(DestroyMode.Cancel);
			};
			return write.Reads(() => BlueprintReport(target, at));
		}
	}
}
