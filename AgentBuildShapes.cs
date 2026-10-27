using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using UnityEngine;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// The shell the room forms share. PlanShell makes every check of "build room new" (dig = false) or "dig room"
	// (dig = true) and throws Fault; AddShellWrites adds the writes. S3 (furnishing rooms) uses both.
	internal sealed class RoomShell
	{
		public CellRect rect;            // outside, walls included
		public CellRect inside;          // rect.ContractedBy(1)
		public IntVec3 door;             // the door cell
		public Rot4 doorSide;            // which edge the door is on
		public List<IntVec3> wallCells = new List<IntVec3>();   // edge cells that get a wall blueprint
		public List<IntVec3> mineCells = new List<IntVec3>();   // dig only
		public List<IntVec3> columns = new List<IntVec3>();     // dig only: rock cells inside left standing to hold the roof
		// dig only (user, 2026-10-21: "players can do this just fine. ... an agent needs to be able to do same."): fogged
		// cells inside, marked for mining as a player's drag marks them (Designator_Mine.cs:41-43 accepts a fogged cell);
		// what is under them is never looked at.
		public List<IntVec3> fogMineCells = new List<IntVec3>();
		// dig only: fogged edge cells, the door cell included; nothing is planned there (user, 2026-10-21: "the agent
		// figures out if it's open ground or not.").
		public List<IntVec3> fogEdge = new List<IntVec3>();
		public ThingDef wallStuff, doorStuff;
		public Dictionary<ThingDef, int> needed = new Dictionary<ThingDef, int>();
		public List<string> notes = new List<string>();
		public string line;              // the confirm sentence of 3 / 4
		// Extras (not in the brief's shape): the door cell gets a door blueprint; the door cell holds rock that is
		// mined (dig); rock cells inside that are mined / were already marked.
		public bool placeDoor;
		public bool doorInRock;
		// Room wall logic (AgentRoomFit, user 2026-10-16): door blueprints in walls shared with existing rooms.
		public List<IntVec3> sharedDoors = new List<IntVec3>();
		public int mineInside;
		public int alreadyMarked;
	}

	// Agent 08, brief S2: a straight line of one-cell buildings, a floor over an area, a box of walls with a door,
	// a room dug into rock. FormPlanner.Dispatch calls PlanLine / PlanFloor / PlanRoomNew / PlanDigRoom (agent 10
	// adds the case lines); each returns a Plan whose Writes run only after the player confirms. Blueprints and
	// mine designations only: colonists build and dig. MAIN THREAD ONLY (Dispatch and Write.apply).
	// Fair play: nothing here reads or sets dev mode or DebugSettings; every question and change goes through the
	// game calls a player's click makes, and nothing under fog is printed, counted or accepted (AgentPlace.Visible).
	// CHANGED 2026-10-21: "dig room" marks fogged inside cells for mining, as the game's own drag does
	// (Designator_Mine.cs:41-43); what is under them is still never read, and fogged edge cells get nothing.
	//
	// Game facts relied on (decompiled tree sources\RimWorldDecompiled\, line numbers of that tree):
	//   GenConstruct.CanPlaceBlueprintAt (placement check, via AgentBuild.CellProblem)  RimWorld\GenConstruct.cs:231
	//   GenConstruct.CanPlaceBlueprintOver; natural rock (a Building edifice that is not a placeable-over wall)
	//       gets no blueprint over it: the Building/edifice branch ends in "return false"  RimWorld\GenConstruct.cs:500, :543-589
	//   CanPlaceBlueprintAt answers "SpaceAlreadyOccupied" when CanPlaceBlueprintOver is false  RimWorld\GenConstruct.cs:359-362
	//   ThingDefOf.Door / ThingDefOf.Wall                      RimWorld\ThingDefOf.cs:630 / :632
	//   ThingDef.IsWall (building.isWall) / IsDoor / IsBlueprint / IsFrame  Verse\ThingDef.cs:880 / :912 / :896 / :894
	//   BuildingProperties.isNaturalRock / isWall / allowAutoroof  RimWorld\BuildingProperties.cs:87 / :55 / :79
	//   BuildableDef.MadeFromStuff / CostList / Size           Verse\BuildableDef.cs:142 / :190 / :140
	//   TerrainGrid.TerrainAt                                  Verse\TerrainGrid.cs:89
	//   PlantProperties.IsTree                                 RimWorld\PlantProperties.cs:207
	//   CellRect.EdgeCells / ContractedBy / Width / Height     Verse\CellRect.cs:204 / :1370 / :84 / :100
	//   Designator_Mine.CanDesignateCell / CanDesignateThing   RimWorld\Designator_Mine.cs:31-56 / :58-73
	//   Designator_Mine.DesignateSingleCell (AddDesignation, drop SmoothWall)  RimWorld\Designator_Mine.cs:75-78
	//   Designator.Map is Find.CurrentMap (so a Designator_Mine only answers for the current map)  Verse\Designator.cs:48
	//   DesignationManager.DesignationAt / AddDesignation / TryRemoveDesignation  Verse\DesignationManager.cs:275 / :167 / :419
	//   IntVec3.Fogged(map)                                    Verse\GridsUtility.cs:91
	//   The game's own roof rules:
	//   Room.Notify_RoomShapeChanged queues the room for the auto roof area (only while playing, room not fogged)
	//       Verse\Room.cs:699-701
	//   AutoBuildRoofAreaSetter.TryGenerateAreaNow gives up when room.CellCount > 320 (or RegionCount > 26, touches the
	//       map edge, is a doorway)                            Verse\AutoBuildRoofAreaSetter.cs:45
	//   ... gives up when a border holder/impassable thing belongs to another faction or has allowAutoroof = false
	//       Verse\AutoBuildRoofAreaSetter.cs:50-64 (test at :55)
	//   ... roofs a cell (BuildRoof area) only if RoofAt == null, not in the NoRoof area and WithinRangeOfRoofHolder
	//       Verse\AutoBuildRoofAreaSetter.cs:116-122 (:118, :120)
	//   RoofCollapseUtility.RoofMaxSupportDistance = 6.9f; WithinRangeOfRoofHolder needs a roof-holding building within
	//       6.9 (Euclid, squared compare, IntVec3.InHorDistOf Verse\IntVec3.cs:173) of the cell  Verse\RoofCollapseUtility.cs:5, :9-30
	//   Colonists' roof job fails without that support         RimWorld\JobDriver_BuildRoof.cs:14
	//   RoofDef.isThickRoof / canCollapse                      Verse\RoofDef.cs:7 / :15
	//   Mining a rock cell despawns a roof holder: Building.DeSpawn -> RoofCollapseCellsFinder.Notify_RoofHolderDespawned
	//       Verse\Building.cs:212-214; Mineable.DestroyMined  RimWorld\Mineable.cs:39-42
	//   ... roofed cells within the support radius that are then farther than 6.9 from every roof holder, with a roof
	//       that canCollapse, are marked to collapse: so a roof stays only while a holder is in range
	//       Verse\RoofCollapseCellsFinder.cs:11-42 (test at :27, mark at :35)
	public static class AgentBuildShapes
	{
		private const int LineCap = 250;
		private const int FloorCap = 400;
		private const int RoomMin = 3;
		private const int RoomMax = 30;
		private const int NamedCells = 5;
		private const int FloorReasonsNamed = 4;
		// AutoBuildRoofAreaSetter.cs:45 (room.CellCount > 320).
		private const int AutoRoofCellLimit = 320;
		// RoofCollapseUtility.RoofMaxSupportDistance = 6.9 (RoofCollapseUtility.cs:5): a holder within 6.9 means
		// at most 6 whole cells in a straight line (6 * 6 = 36 <= 47.61; 7 * 7 = 49 > 47.61).
		private const int SupportCells = 6;
		private const string AlreadyMarked = "already marked";

		// ---- the four forms -------------------------------------------------------------------------

		// Form "build line": BUILDING, STUFF?, AREA.
		public static PlanReply PlanLine(FormContext c)
		{
			Map map = c.map;
			ThingDef def = AgentBuild.ResolveThing(c.Text("BUILDING"));
			AgentBuild.RefuseSpot(def);
			string label = LabelOf(def);
			if (def.Size.x != 1 || def.Size.z != 1)
			{
				throw new Fault("A line is for one-cell buildings such as wall, fence, sandbags or conduit; " + label + " is "
					+ def.Size.x + " by " + def.Size.z + ".");
			}
			CellRect rect = AreaOf(c);
			if (rect.Width != 1 && rect.Height != 1)
			{
				throw new Fault("'from AREA' is a straight line one cell wide; " + Corners(rect) + " is " + rect.Width + " by "
					+ rect.Height + ". For walls round an area say 'build room AREA'.");
			}
			if (rect.Area > LineCap)
			{
				throw new Fault("A line is at most " + LineCap + " cells; " + Corners(rect) + " is " + rect.Area + ".");
			}

			// Pass 1: what is already there. Fogged cells are not looked at (they fail below with "not explored").
			List<IntVec3> toPlace = new List<IntVec3>();
			int already = 0;
			foreach (IntVec3 cell in rect.Cells)
			{
				if (AgentPlace.Visible(map, cell) && HoldsOrPlans(map, cell, def))
				{
					already++;
				}
				else
				{
					toPlace.Add(cell);
				}
			}
			if (toPlace.Count == 0)
			{
				throw new Fault("Every cell from " + Corners(rect) + " already has a " + label + " or a " + label + " blueprint.");
			}

			Plan plan = FormPlanner.NewPlan("agent build line");
			Dictionary<ThingDef, int> claimed = new Dictionary<ThingDef, int>();
			ThingDef stuff = AgentBuild.ResolveStuffFor(map, def, c.Text("STUFF"), toPlace.Count, claimed, plan.notes);
			Rot4 rot = def.defaultPlacingRot;

			// Pass 2: every other cell must take a blueprint, or the whole line is refused.
			List<string> failures = new List<string>();
			foreach (IntVec3 cell in toPlace)
			{
				string problem = AgentBuild.CellProblem(map, def, cell, rot, stuff);
				if (problem != null)
				{
					failures.Add(AgentPlace.Describe(map, cell) + ": " + Trim(problem));
				}
			}
			if (failures.Count > 0)
			{
				throw new Fault("Can't build the line of " + Plural(label, 2) + " from " + Corners(rect) + ": " + FailureText(failures)
					+ ". Nothing was planned.");
			}

			Dictionary<ThingDef, int> needed = new Dictionary<ThingDef, int>();
			BuildDefs.AddCosts(needed, def, stuff, toPlace.Count);
			AddMaterialNotes(plan, map, needed);
			if (already > 0)
			{
				plan.notes.Add(already + (already == 1 ? " cell already has a " : " cells already have a ") + label + " (built or planned) and is skipped.");
			}

			string fullLabel = BuildDefs.Label(def, stuff);
			string line = "Build " + toPlace.Count + " " + Plural(fullLabel, toPlace.Count) + " from " + AgentPlace.DescribeRect(map, rect)
				+ (already > 0 ? ", skipping " + already + " that already " + (already == 1 ? "has one" : "have one") : "") + ".";

			AgentWindCheck.Note(plan, map, def, toPlace, rot);
			AgentLightCheck.Note(plan, map, def, toPlace, rot);
			AgentWorkspotCheck.Note(plan, map, def, toPlace, rot);
			AgentDoorCheck.Note(plan, map, def, toPlace, rot);
			AgentCoolerCheck.Note(plan, map, def, toPlace, rot);
			AgentVentCheck.Note(plan, map, def, toPlace, rot);
			string tag = label + " line " + Corners(rect);
			Write write = BlueprintsWrite(map, def, toPlace, rot, stuff, tag,
				"Blueprints: " + toPlace.Count + " " + Plural(fullLabel, toPlace.Count) + " from " + Corners(rect), fullLabel);
			plan.writes.Add(write.Reads(() => CountReport(map, def, toPlace, tag)));
			return FormPlanner.Reply(plan, line, null, null);
		}

		// Form "build floor": FLOOR, AREA.
		public static PlanReply PlanFloor(FormContext c)
		{
			Map map = c.map;
			string name = c.Text("FLOOR");
			if (string.IsNullOrWhiteSpace(name))
			{
				throw new Fault("The order did not say which floor.");
			}
			TerrainDef def = ResolveFloor(name);
			string label = LabelOf(def);
			if (def.MadeFromStuff)
			{
				throw new Fault(FormPlanner.UpperFirst(label) + " is made from a material the mod can't choose for floors yet.");
			}
			CellRect rect = AreaOf(c);
			if (rect.Area > FloorCap)
			{
				throw new Fault("A floor order covers at most " + FloorCap + " cells; " + Corners(rect) + " is " + rect.Area + ".");
			}

			List<IntVec3> toPlace = new List<IntVec3>();
			List<string> order = new List<string>();
			Dictionary<string, int> counts = new Dictionary<string, int>();
			Dictionary<string, IntVec3> example = new Dictionary<string, IntVec3>();
			Rot4 rot = Rot4.North;
			foreach (IntVec3 cell in rect.Cells)
			{
				string why = null;
				if (!AgentPlace.Visible(map, cell))
				{
					why = "not explored yet";
				}
				else if (map.terrainGrid.TerrainAt(cell) == def)
				{
					why = "already " + label;
				}
				else if (HoldsOrPlans(map, cell, def))
				{
					why = "a " + label + " blueprint is already there";
				}
				else
				{
					string problem = AgentBuild.CellProblem(map, def, cell, rot, null);
					if (problem != null)
					{
						why = Trim(problem);
					}
				}
				if (why == null)
				{
					toPlace.Add(cell);
					continue;
				}
				if (!counts.ContainsKey(why))
				{
					counts[why] = 0;
					example[why] = cell;
					order.Add(why);
				}
				counts[why]++;
			}

			int skipped = rect.Area - toPlace.Count;
			List<string> reasons = new List<string>();
			for (int i = 0; i < order.Count && i < FloorReasonsNamed; i++)
			{
				string why = order[i];
				reasons.Add(counts[why] + " " + (counts[why] == 1 ? "cell" : "cells") + ": " + why + " (e.g. " + Xz(example[why]) + ")");
			}
			if (order.Count > FloorReasonsNamed)
			{
				int rest = 0;
				for (int i = FloorReasonsNamed; i < order.Count; i++)
				{
					rest += counts[order[i]];
				}
				reasons.Add(rest + " more " + (rest == 1 ? "cell" : "cells") + " for other reasons");
			}
			if (toPlace.Count == 0)
			{
				throw new Fault("No cell from " + Corners(rect) + " can take " + label + ": " + string.Join("; ", reasons.ToArray()) + ".");
			}

			Plan plan = FormPlanner.NewPlan("agent build floor");
			Dictionary<ThingDef, int> needed = new Dictionary<ThingDef, int>();
			AddFloorCosts(needed, def, toPlace.Count);
			AddMaterialNotes(plan, map, needed);
			if (skipped > 0)
			{
				plan.notes.Add(skipped + " " + (skipped == 1 ? "cell is" : "cells are") + " skipped, the rest is placed: " + string.Join("; ", reasons.ToArray()) + ".");
			}

			string line = "Build " + label + " on " + toPlace.Count + " of the " + rect.Area + " cells of " + AgentPlace.DescribeRect(map, rect) + ".";
			string tag = label + " floor " + Corners(rect);
			Write write = BlueprintsWrite(map, def, toPlace, rot, null, tag, "Blueprints: " + label + " on " + toPlace.Count + " cells of " + Corners(rect), label);
			plan.writes.Add(write.Reads(() => CountReport(map, def, toPlace, tag)));
			return FormPlanner.Reply(plan, line, null, null);
		}

		// User 2026-10-17: "build {floortype} for {roomid/roomname}. roomname lists all rooms with id, just like previous query.
		// Calculates expected cost for that room (should be easy). and asks agent for confirmation before they say
		// "apply/cancel". Bonus point if it calculates estimated cleanliness/impressiveness/beauty for them, alongside
		// expected cost." also shows bills present on that room." Every cell of the room that can take the floor; the
		// estimate puts the new floor's stats into the game's own room stat formulas (RoomStatWorker_Beauty / _Cleanliness /
		// _Wealth / _Impressiveness, BeautyUtility.CellBeauty). Texts: the user's pick "As shown".
		public static PlanReply PlanFloorRoom(FormContext c)
		{
			Map map = c.map;
			List<Room> rooms = FormPlanner.RoomsByNameOrId(map, c.Text("ROOM"));
			if (rooms.Count > 1)
			{
				throw new Fault(FormPlanner.RoomBlocks(rooms));
			}
			Room room = rooms[0];
			string name = c.Text("FLOOR");
			if (string.IsNullOrWhiteSpace(name))
			{
				throw new Fault("The order did not say which floor.");
			}
			TerrainDef def = ResolveFloor(name);
			string label = LabelOf(def);
			if (def.MadeFromStuff)
			{
				throw new Fault(FormPlanner.UpperFirst(label) + " is made from a material the mod can't choose for floors yet.");
			}
			if (room.CellCount > FloorCap)
			{
				throw new Fault("A floor order covers at most " + FloorCap + " cells; Room" + room.ID + " has " + room.CellCount + ".");
			}
			Rot4 rot = Rot4.North;
			int already;
			int cannot;
			List<IntVec3> toPlace = FloorCells(map, room, def, out already, out cannot);
			string head = FormPlanner.RoomHead(room);
			if (toPlace.Count == 0)
			{
				throw new Fault(head + "\nNo cell of Room" + room.ID + " can take " + label + " (" + already + " already " + label + ", " + cannot + " can't take it).");
			}
			List<string> skipped = new List<string>();
			if (already > 0)
			{
				skipped.Add(already + " already " + label);
			}
			if (cannot > 0)
			{
				skipped.Add(cannot + " can't take it");
			}
			List<string> lines = new List<string>();
			lines.Add(head);
			lines.Add("Lay " + label + " on " + toPlace.Count + " cells" + (skipped.Count > 0 ? " (" + string.Join(", ", skipped.ToArray()) + ", skipped)" : "") + ".");
			Dictionary<ThingDef, int> needed = new Dictionary<ThingDef, int>();
			AddFloorCosts(needed, def, toPlace.Count);
			if (needed.Count > 0)
			{
				lines.Add("Cost: " + string.Join(", ", needed.Select((KeyValuePair<ThingDef, int> kv) => kv.Value + " " + kv.Key.label
					+ " (you have " + map.resourceCounter.GetCount(kv.Key) + ")").ToArray()) + ".");
			}
			lines.Add(FloorEstimate(map, room, def, toPlace));
			lines.Add(FormPlanner.RoomBillsLine(room));

			Plan plan = FormPlanner.NewPlan("agent build floor");
			string tag = label + " floor Room" + room.ID;
			Write write = BlueprintsWrite(map, def, toPlace, rot, null, tag, "Blueprints: " + label + " on " + toPlace.Count + " cells of Room" + room.ID, label);
			plan.writes.Add(write.Reads(() => CountReport(map, def, toPlace, tag)));
			return FormPlanner.Reply(plan, string.Join("\n", lines.ToArray()), null, null);
		}

		// The cells of the room that can take the floor now; already = that floor or its plan is there, cannot = the game
		// refuses it (or not explored).
		internal static List<IntVec3> FloorCells(Map map, Room room, TerrainDef def, out int already, out int cannot)
		{
			List<IntVec3> toPlace = new List<IntVec3>();
			already = 0;
			cannot = 0;
			foreach (IntVec3 cell in room.Cells)
			{
				if (map.terrainGrid.TerrainAt(cell) == def || HoldsOrPlans(map, cell, def))
				{
					already++;
				}
				else if (!AgentPlace.Visible(map, cell) || AgentBuild.CellProblem(map, def, cell, Rot4.North, null) != null)
				{
					cannot++;
				}
				else
				{
					toPlace.Add(cell);
				}
			}
			return toPlace;
		}

		// "After (estimate): beauty -1.20 -> 0.90, cleanliness ..., wealth ..., impressiveness ...".
		private static string FloorEstimate(Map map, Room room, TerrainDef def, List<IntVec3> cells)
		{
			float[] e = EstimateFloor(map, room, def, cells);
			return "After (estimate): " + Change(RoomStatDefOf.Beauty, e[0], e[1]) + ", " + Change(RoomStatDefOf.Cleanliness, e[2], e[3])
				+ ", " + Change(RoomStatDefOf.Wealth, e[4], e[5]) + ", " + Change(RoomStatDefOf.Impressiveness, e[6], e[7]) + ".";
		}

		// "beauty -1.20 -> 0.90, cleanliness ..., impressiveness ..." (the 12-hour room list, AgentRoomScan.cs).
		internal static string FloorGain(Map map, Room room, TerrainDef def, List<IntVec3> cells)
		{
			float[] e = EstimateFloor(map, room, def, cells);
			return Change(RoomStatDefOf.Beauty, e[0], e[1]) + ", " + Change(RoomStatDefOf.Cleanliness, e[2], e[3]) + ", " + Change(RoomStatDefOf.Impressiveness, e[6], e[7]);
		}

		// The room's stats now and with the new floor on those cells, by the game's formulas (only the terrain part
		// changes): beauty, new beauty, cleanliness, new, wealth, new, impressiveness, new.
		private static float[] EstimateFloor(Map map, Room room, TerrainDef def, List<IntVec3> cells)
		{
			bool outdoors = room.PsychologicallyOutdoors;
			float beautySum = 0f;
			float cleanSum = 0f;
			float wealthSum = 0f;
			foreach (IntVec3 cell in cells)
			{
				TerrainDef old = map.terrainGrid.TerrainAt(cell);
				// BeautyUtility.CellBeauty: the terrain counts only when no full-fillage thing stands there.
				bool full = cell.GetThingList(map).Any((Thing t) => BeautyUtility.BeautyRelevant(t.def.category) && t.def.Fillage == FillCategory.Full);
				if (!full)
				{
					beautySum += TerrainBeauty(def, outdoors) - TerrainBeauty(old, outdoors);
				}
				cleanSum += def.GetStatValueAbstract(StatDefOf.Cleanliness) - old.GetStatValueAbstract(StatDefOf.Cleanliness);
				wealthSum += def.GetStatValueAbstract(StatDefOf.MarketValue) - old.GetStatValueAbstract(StatDefOf.MarketValue);
			}
			int n = room.CellCount;
			// RoomStatWorker_Beauty.CellCountCurve: (0, 20), (40, 40), then the cell count.
			float beautyDiv = n <= 40 ? 20f + n * 0.5f : n;
			float beauty = room.GetStat(RoomStatDefOf.Beauty);
			float clean = room.GetStat(RoomStatDefOf.Cleanliness);
			float wealth = room.GetStat(RoomStatDefOf.Wealth);
			float space = room.GetStat(RoomStatDefOf.Space);
			float newBeauty = beauty + (n > 0 ? beautySum / beautyDiv : 0f);
			float newClean = clean + (n > 0 ? cleanSum / n : 0f);
			float newWealth = wealth + wealthSum;
			float impressive = room.GetStat(RoomStatDefOf.Impressiveness);
			float newImpressive = Impressiveness(newWealth, newBeauty, space, newClean);
			return new float[] { beauty, newBeauty, clean, newClean, wealth, newWealth, impressive, newImpressive };
		}

		// The materials of def for that many cells (the 12-hour room list).
		internal static Dictionary<ThingDef, int> FloorCost(TerrainDef def, int cells)
		{
			Dictionary<ThingDef, int> needed = new Dictionary<ThingDef, int>();
			AddFloorCosts(needed, def, cells);
			return needed;
		}

		private static string Change(RoomStatDef stat, float from, float to)
		{
			return stat.label + " " + stat.ScoreToString(from) + " -> " + stat.ScoreToString(to);
		}

		private static float TerrainBeauty(TerrainDef t, bool outdoors)
		{
			return outdoors && t.StatBaseDefined(StatDefOf.BeautyOutdoors) ? t.GetStatValueAbstract(StatDefOf.BeautyOutdoors) : t.GetStatValueAbstract(StatDefOf.Beauty);
		}

		// RoomStatWorker_Impressiveness.GetScore, with the stats given.
		private static float Impressiveness(float wealth, float beauty, float space, float clean)
		{
			float f1 = ImpressFactor(wealth / 1500f);
			float f2 = ImpressFactor(beauty / 3f);
			float f3 = ImpressFactor(space / 125f);
			float f4 = ImpressFactor(1f + Mathf.Min(clean, 0f) / 2.5f);
			float a = (f1 + f2 + f3 + f4) / 4f;
			float b = Mathf.Min(f1, Mathf.Min(f2, Mathf.Min(f3, f4)));
			float num = Mathf.Lerp(a, b, 0.35f);
			float cap = f3 * 5f;
			if (num > cap)
			{
				num = Mathf.Lerp(num, cap, 0.75f);
			}
			return num * 100f;
		}

		private static float ImpressFactor(float baseFactor)
		{
			if (Mathf.Abs(baseFactor) < 1f)
			{
				return baseFactor;
			}
			return baseFactor > 0f ? 1f + Mathf.Log(baseFactor) : -1f - Mathf.Log(-baseFactor);
		}

		// User 2026-10-17: "cancel floor for {roomid/roomname}. shows current blueprints in progress for all of the room (also
		// says what buildings there are, including any bills present for the pawns) including completed tiles. they can say
		// "cancel floor" to deconstruct AND cancel floor for that roomid/roomname. tells them expected return in resources
		// (including for deconstruction)." Returns as the game gives them: a cancelled frame drops all it holds
		// (GenLeaving.GetBuildingResourcesLeaveCalculator, DestroyMode.Cancel = count x 1, RimWorld\GenLeaving.cs:321); a
		// removed floor drops its cost x resourcesFractionWhenDeconstructed, rounded at random per tile (GenLeaving.cs:249-278);
		// an unstarted blueprint holds nothing. Built floor = what the game's remove-floor tool takes
		// (Designator_RemoveFloor.CanDesignateCell). Texts: the user's pick "As shown".
		public static PlanReply PlanCancelFloorRoom(FormContext c)
		{
			Map map = c.map;
			List<Room> rooms = FormPlanner.RoomsByNameOrId(map, c.Text("ROOM"));
			if (rooms.Count > 1)
			{
				throw new Fault(FormPlanner.RoomBlocks(rooms));
			}
			Room room = rooms[0];
			List<Thing> plans = new List<Thing>();
			List<IntVec3> built = new List<IntVec3>();
			HashSet<Thing> seen = new HashSet<Thing>();
			foreach (IntVec3 cell in room.Cells)
			{
				foreach (Thing t in cell.GetThingList(map))
				{
					if ((t.def.IsBlueprint || t.def.IsFrame) && t.def.entityDefToBuild is TerrainDef && t.Faction == Faction.OfPlayer && seen.Add(t))
					{
						plans.Add(t);
					}
				}
				if (map.designationManager.DesignationAt(cell, DesignationDefOf.RemoveFloor) == null && map.terrainGrid.CanRemoveTopLayerAt(cell)
					&& !WorkGiver_ConstructRemoveFloor.AnyBuildingBlockingFloorRemoval(cell, map))
				{
					Building edifice = cell.GetEdifice(map);
					if (edifice == null || edifice.def.Fillage != FillCategory.Full || edifice.def.passability != Traversability.Impassable)
					{
						built.Add(cell);
					}
				}
			}
			string head = FormPlanner.RoomHead(room);
			if (plans.Count == 0 && built.Count == 0)
			{
				throw new Fault(head + "\nRoom" + room.ID + " has no floor plans and no built floor.");
			}

			// "wood floor x12 (not started)", "wood floor x4 (started, 12 wood delivered)".
			List<string> planParts = new List<string>();
			Dictionary<ThingDef, int> fromFrames = new Dictionary<ThingDef, int>();
			foreach (IGrouping<string, Thing> g in plans.Where((Thing t) => t.def.IsBlueprint).GroupBy((Thing t) => t.def.entityDefToBuild.label))
			{
				planParts.Add(g.Key + " x" + g.Count() + " (not started)");
			}
			foreach (IGrouping<string, Thing> g in plans.Where((Thing t) => t.def.IsFrame).GroupBy((Thing t) => t.def.entityDefToBuild.label))
			{
				Dictionary<ThingDef, int> held = new Dictionary<ThingDef, int>();
				foreach (Frame f in g.OfType<Frame>())
				{
					foreach (Thing r in f.resourceContainer)
					{
						int n;
						held.TryGetValue(r.def, out n);
						held[r.def] = n + r.stackCount;
						fromFrames.TryGetValue(r.def, out n);
						fromFrames[r.def] = n + r.stackCount;
					}
				}
				planParts.Add(g.Key + " x" + g.Count() + " (started" + (held.Count > 0 ? ", " + Amounts(held) + " delivered" : "") + ")");
			}
			Dictionary<ThingDef, float> fromFloor = new Dictionary<ThingDef, float>();
			List<string> builtParts = new List<string>();
			foreach (IGrouping<TerrainDef, IntVec3> g in built.GroupBy((IntVec3 cell) => map.terrainGrid.TerrainAt(cell)))
			{
				builtParts.Add(g.Key.label + " x" + g.Count());
				foreach (ThingDefCountClass cost in g.Key.CostListAdjusted(null))
				{
					float n;
					fromFloor.TryGetValue(cost.thingDef, out n);
					fromFloor[cost.thingDef] = n + cost.count * g.Key.resourcesFractionWhenDeconstructed * g.Count();
				}
			}

			List<string> lines = new List<string>();
			lines.Add(head);
			if (planParts.Count > 0)
			{
				lines.Add("  floor plans: " + string.Join(", ", planParts.ToArray()));
			}
			if (builtParts.Count > 0)
			{
				lines.Add("  built floor: " + string.Join(", ", builtParts.ToArray()));
			}
			lines.Add("  " + FormPlanner.RoomBillsLine(room));
			List<string> what = new List<string>();
			if (plans.Count > 0)
			{
				what.Add("cancel " + plans.Count + " floor " + (plans.Count == 1 ? "plan" : "plans"));
			}
			if (built.Count > 0)
			{
				what.Add("remove " + built.Count + " built floor " + (built.Count == 1 ? "tile" : "tiles"));
			}
			lines.Add(FormPlanner.UpperFirst(string.Join(" and ", what.ToArray())) + ".");
			List<string> returns = new List<string>();
			if (fromFrames.Count > 0)
			{
				returns.Add(Amounts(fromFrames) + " from the started plans");
			}
			Dictionary<ThingDef, int> floorRounded = fromFloor.Where((KeyValuePair<ThingDef, float> kv) => Mathf.RoundToInt(kv.Value) > 0)
				.ToDictionary((KeyValuePair<ThingDef, float> kv) => kv.Key, (KeyValuePair<ThingDef, float> kv) => Mathf.RoundToInt(kv.Value));
			if (floorRounded.Count > 0)
			{
				returns.Add("about " + Amounts(floorRounded) + " from the built floor");
			}
			if (returns.Count > 0)
			{
				lines.Add("Returns: " + string.Join(", ", returns.ToArray()) + ".");
			}

			Plan plan = FormPlanner.NewPlan("agent cancel floor");
			List<Thing> planList = new List<Thing>(plans);
			List<IntVec3> builtList = new List<IntVec3>(built);
			Write write = new Write();
			write.description = "Cancel floor: Room" + room.ID;
			write.apply = delegate
			{
				foreach (Thing t in planList)
				{
					if (!t.Destroyed)
					{
						t.Destroy(DestroyMode.Cancel);
					}
				}
				foreach (IntVec3 cell in builtList)
				{
					if (map.designationManager.DesignationAt(cell, DesignationDefOf.RemoveFloor) == null && map.terrainGrid.CanRemoveTopLayerAt(cell))
					{
						map.designationManager.AddDesignation(new Designation(cell, DesignationDefOf.RemoveFloor));
					}
				}
			};
			plan.writes.Add(write);
			return FormPlanner.Reply(plan, string.Join("\n", lines.ToArray()), null, null);
		}

		// "12 wood, 4 steel".
		private static string Amounts(Dictionary<ThingDef, int> amounts)
		{
			return string.Join(", ", amounts.Select((KeyValuePair<ThingDef, int> kv) => kv.Value + " " + kv.Key.label).ToArray());
		}

		// Form "build room new": AREA, STUFF?, SIDE?.
		public static PlanReply PlanRoomNew(FormContext c)
		{
			Map map = c.map;
			CellRect rect = AreaOf(c);
			RoomShell shell = PlanShell(map, rect, c.Text("SIDE"), c.Text("STUFF"), false);
			rect = shell.rect;
			Plan plan = FormPlanner.NewPlan("agent build room");
			plan.notes.AddRange(shell.notes);
			AddShellWrites(map, plan, shell, "room " + rect.Width + " by " + rect.Height + " at " + Corners(rect));
			return FormPlanner.Reply(plan, shell.line, null, null);
		}

		// Form "dig room": AREA, STUFF?, SIDE?.
		public static PlanReply PlanDigRoom(FormContext c)
		{
			Map map = c.map;
			CellRect rect = AreaOf(c);
			RoomShell shell = PlanShell(map, rect, c.Text("SIDE"), c.Text("STUFF"), true);
			rect = shell.rect;
			Plan plan = FormPlanner.NewPlan("agent dig room");
			plan.notes.AddRange(shell.notes);
			AddShellWrites(map, plan, shell, "dug room " + rect.Width + " by " + rect.Height + " at " + Corners(rect));
			return FormPlanner.Reply(plan, shell.line, null, null);
		}

		// ---- the shared shell ------------------------------------------------------------------------

		// Every check of "build room new" (dig = false) or "dig room" (dig = true); throws Fault. side null/empty = south.
		internal static RoomShell PlanShell(Map map, CellRect rect, string side, string stuff, bool dig)
		{
			if (rect.Width < RoomMin || rect.Width > RoomMax || rect.Height < RoomMin || rect.Height > RoomMax)
			{
				throw new Fault("A room is " + RoomMin + " to " + RoomMax + " cells each way, walls included; " + Corners(rect) + " is "
					+ rect.Width + " by " + rect.Height + ".");
			}
			// Room wall logic (AgentRoomFit, user 2026-10-16): no new room while one is still being built; the room is moved one
			// cell onto the wall of an existing room it would stand beside, and gets a door in each wall it shares.
			AgentRoomFit.RefuseWhileUnfinished(map);
			RoomFit fit = null;
			if (!dig)
			{
				fit = AgentRoomFit.Fit(map, rect);
				rect = fit.rect;
			}
			RoomShell shell = new RoomShell();
			shell.rect = rect;
			shell.inside = rect.ContractedBy(1);
			bool sideDefaulted;
			shell.doorSide = ParseSide(side, out sideDefaulted);
			shell.door = DoorCell(rect, shell.doorSide);
			if (fit != null)
			{
				shell.door = fit.DoorFor(shell.door);
			}
			ThingDef wall = ThingDefOf.Wall;
			ThingDef doorDef = ThingDefOf.Door;
			string sideName = SideName(shell.doorSide);
			string rectText = Corners(rect);

			// ---- inside: nothing under fog, nothing impassable, rock only when digging ----
			List<IntVec3> unexplored = new List<IntVec3>();
			List<IntVec3> rockInside = new List<IntVec3>();
			int trees = 0;
			int otherPlants = 0;
			foreach (IntVec3 cell in shell.inside.Cells)
			{
				if (!AgentPlace.Visible(map, cell))
				{
					unexplored.Add(cell);
					continue;
				}
				List<Thing> things = cell.GetThingList(map);
				for (int i = 0; i < things.Count; i++)
				{
					Thing t = things[i];
					ThingDef d = t.def;
					if (d.category == ThingCategory.Building && d.building != null && d.building.isNaturalRock)
					{
						if (!dig)
						{
							throw new Fault("The inside is blocked at " + Xz(cell) + ": " + RockLabel(d) + ". For a room dug into rock say 'dig room AREA'.");
						}
						rockInside.Add(cell);
					}
					else if (d.category == ThingCategory.Building && d.passability == Traversability.Impassable)
					{
						throw new Fault("The inside is blocked at " + Xz(cell) + ": " + LabelOf(d) + ".");
					}
					else if ((d.IsBlueprint || d.IsFrame) && d.entityDefToBuild is ThingDef planned
						&& planned.category == ThingCategory.Building && planned.passability == Traversability.Impassable)
					{
						throw new Fault("The inside is blocked at " + Xz(cell) + ": a planned " + LabelOf(planned) + ".");
					}
					else if (d.category == ThingCategory.Plant)
					{
						if (d.plant != null && d.plant.IsTree)
						{
							trees++;
						}
						else
						{
							otherPlants++;
						}
					}
				}
			}
			// dig room: fogged inside cells are marked for mining like a player's drag (Designator_Mine.cs:41-43); build room
			// still needs every inside cell seen.
			if (unexplored.Count > 0 && !dig)
			{
				throw new Fault("Part of the inside has not been explored yet (" + Xz(unexplored[0]) + (unexplored.Count > 1 ? " and " + (unexplored.Count - 1) + " more" : "")
					+ "); a room can only be planned on cells the colonists have seen.");
			}
			if (dig && rockInside.Count == 0 && unexplored.Count == 0)
			{
				throw new Fault("Nothing to dig there; for a room on open ground say 'build room AREA'.");
			}
			if (dig)
			{
				// A big dug room keeps columns of rock, or its roof falls in (ColumnCells). A fogged cell on the grid is
				// left unmarked: a column stays there if it is rock.
				HashSet<IntVec3> grid = ColumnCells(shell.inside);
				shell.columns = rockInside.Where((IntVec3 cell) => grid.Contains(cell)).ToList();
				rockInside.RemoveAll((IntVec3 cell) => grid.Contains(cell));
				unexplored.RemoveAll((IntVec3 cell) => grid.Contains(cell));
			}

			// ---- the door cell ----
			bool keepDoor = false;
			List<string> failures = new List<string>();
			Thing found;
			if (!AgentPlace.Visible(map, shell.door))
			{
				if (dig)
				{
					shell.fogEdge.Add(shell.door);
				}
				else
				{
					failures.Add(AgentPlace.Describe(map, shell.door) + ": not explored yet");
				}
			}
			else
			{
				EdgeKind kind = Classify(map, shell.door, out found);
				switch (kind)
				{
					case EdgeKind.Open:
						shell.placeDoor = true;
						break;
					case EdgeKind.Door:
					case EdgeKind.DoorPlanned:
						keepDoor = true;
						break;
					case EdgeKind.Wall:
						if (fit == null || !fit.IsSharedDoor(shell.door))
						{
							throw new Fault("A wall already stands where the door would go, " + Xz(shell.door) + "; choose another side with 'door ...'.");
						}
						shell.placeDoor = true;
						break;
					case EdgeKind.Rock:
						if (!dig)
						{
							throw new Fault("A wall already stands where the door would go, " + Xz(shell.door) + "; choose another side with 'door ...'.");
						}
						shell.doorInRock = true;
						// A blueprint over rock is refused by the game (GenConstruct.cs:359-362, :589), so this is
						// normally false; asked anyway so the game's own answer decides.
						shell.placeDoor = AgentBuild.CellProblem(map, doorDef, shell.door, doorDef.defaultPlacingRot, null) == null;
						break;
					default:
						throw new Fault("A wall already stands where the door would go, " + Xz(shell.door) + "; choose another side with 'door ...'.");
				}
			}

			// ---- the other edge cells ----
			int closed = 0;
			bool ringBlocked = false;
			foreach (IntVec3 cell in rect.EdgeCells)
			{
				if (cell == shell.door)
				{
					continue;
				}
				if (!AgentPlace.Visible(map, cell))
				{
					if (dig)
					{
						shell.fogEdge.Add(cell);
					}
					else
					{
						failures.Add(AgentPlace.Describe(map, cell) + ": not explored yet");
					}
					continue;
				}
				EdgeKind kind = Classify(map, cell, out found);
				if (kind == EdgeKind.Open)
				{
					shell.wallCells.Add(cell);
					continue;
				}
				closed++;
				// AutoBuildRoofAreaSetter.cs:55: another faction's holder, or allowAutoroof = false, stops the auto roof.
				if (found != null && (kind == EdgeKind.Wall || kind == EdgeKind.Door)
					&& ((found.Faction != null && found.Faction != Faction.OfPlayer)
						|| (found.def.building != null && !found.def.building.allowAutoroof)))
				{
					ringBlocked = true;
				}
			}

			// ---- mine designations (dig) ----
			List<IntVec3> toMark = new List<IntVec3>();
			if (dig)
			{
				List<IntVec3> rock = new List<IntVec3>(rockInside);
				if (shell.doorInRock)
				{
					rock.Add(shell.door);
				}
				foreach (IntVec3 cell in rock)
				{
					string problem = MineProblem(map, cell);
					if (problem == AlreadyMarked)
					{
						shell.alreadyMarked++;
					}
					else if (problem != null)
					{
						failures.Add(AgentPlace.Describe(map, cell) + ": can't be marked for mining, " + Trim(problem));
					}
					else
					{
						toMark.Add(cell);
						if (cell != shell.door)
						{
							shell.mineInside++;
						}
					}
				}
				// Fogged inside cells: the game's mine check accepts them without looking under the fog.
				foreach (IntVec3 cell in unexplored)
				{
					string problem = MineProblem(map, cell);
					if (problem == AlreadyMarked)
					{
						continue;
					}
					if (problem != null)
					{
						failures.Add(AgentPlace.Describe(map, cell) + ": can't be marked for mining, " + Trim(problem));
					}
					else
					{
						shell.fogMineCells.Add(cell);
					}
				}
			}

			// ---- material ----
			Dictionary<ThingDef, int> claimed = new Dictionary<ThingDef, int>();
			string stuffWord = stuff;
			if (shell.wallCells.Count > 0)
			{
				shell.wallStuff = AgentBuild.ResolveStuffFor(map, wall, stuffWord, shell.wallCells.Count, claimed, shell.notes);
			}
			List<SharedWall> newDoors = fit == null ? new List<SharedWall>()
				: fit.walls.Where((SharedWall w) => !w.used && w.door.IsValid && !w.existingDoor.IsValid).ToList();
			int doorCount = (shell.placeDoor ? 1 : 0) + newDoors.Count;
			if (doorCount > 0)
			{
				shell.doorStuff = AgentBuild.ResolveStuffFor(map, doorDef, stuffWord, doorCount, claimed, shell.notes);
			}
			foreach (SharedWall w in newDoors)
			{
				string problem = AgentBuild.CellProblem(map, doorDef, w.door, doorDef.defaultPlacingRot, shell.doorStuff);
				if (problem == null)
				{
					shell.sharedDoors.Add(w.door);
					// Settings gate (user, 2026-10-26): Build advice tips/notes.
					if (VerbalCommandsMod.BuildAdviceOn)
					{
						shell.notes.Add("A door goes in the wall shared with the room at " + Corners(w.room) + ", at " + Xz(w.door) + ".");
					}
				}
				else
				{
					// Settings gate (user, 2026-10-26): Build advice tips/notes.
					if (VerbalCommandsMod.BuildAdviceOn)
					{
						shell.notes.Add("No door in the wall shared with the room at " + Corners(w.room) + ": " + Trim(problem) + ".");
					}
				}
			}

			// Every other edge cell must take its blueprint, or the room has a gap.
			foreach (IntVec3 cell in shell.wallCells)
			{
				string problem = AgentBuild.CellProblem(map, wall, cell, wall.defaultPlacingRot, shell.wallStuff);
				if (problem != null)
				{
					failures.Add(AgentPlace.Describe(map, cell) + ": " + Trim(problem));
				}
			}
			if (shell.placeDoor && !shell.doorInRock)
			{
				string problem = AgentBuild.CellProblem(map, doorDef, shell.door, doorDef.defaultPlacingRot, shell.doorStuff);
				if (problem != null)
				{
					failures.Add(AgentPlace.Describe(map, shell.door) + " (door): " + Trim(problem));
				}
			}
			if (failures.Count > 0)
			{
				throw new Fault("Can't " + (dig ? "dig" : "build") + " the room at " + rectText + ": " + FailureText(failures)
					+ ". A room with a gap is no room; nothing was planned.");
			}

			shell.mineCells = toMark;
			if (shell.wallCells.Count > 0)
			{
				BuildDefs.AddCosts(shell.needed, wall, shell.wallStuff, shell.wallCells.Count);
			}
			if (shell.placeDoor || shell.sharedDoors.Count > 0)
			{
				BuildDefs.AddCosts(shell.needed, doorDef, shell.doorStuff, (shell.placeDoor ? 1 : 0) + shell.sharedDoors.Count);
			}
			if (shell.wallCells.Count == 0 && !shell.placeDoor && toMark.Count == 0 && shell.fogMineCells.Count == 0)
			{
				throw new Fault("Nothing to " + (dig ? "dig or " : "") + "build there: the walls, the door and the digging are already built, planned or marked.");
			}

			// ---- notes ----
			AddMaterialNotes(shell.notes, map, shell.needed);
			// Settings gate (user, 2026-10-26): Build advice tips/notes.
			if (VerbalCommandsMod.BuildAdviceOn && fit != null && (fit.dx != 0 || fit.dz != 0))
			{
				List<string> moves = new List<string>();
				if (fit.dx != 0)
				{
					moves.Add(fit.dx > 0 ? "east" : "west");
				}
				if (fit.dz != 0)
				{
					moves.Add(fit.dz > 0 ? "north" : "south");
				}
				shell.notes.Add("The room is moved 1 cell " + string.Join(" and ", moves.ToArray()) + ", to " + rectText
					+ ", onto the wall of the room at " + Corners(fit.shiftedTo) + ": no double wall.");
			}
			// Settings gate (user, 2026-10-26): Build advice tips/notes.
			if (VerbalCommandsMod.BuildAdviceOn && sideDefaulted)
			{
				shell.notes.Add("The door is on the south side; say 'door north' (or east, west) to choose.");
			}
			// Settings gate (user, 2026-10-26): Build advice tips/notes.
			if (VerbalCommandsMod.BuildAdviceOn && closed > 0)
			{
				shell.notes.Add(closed + (closed == 1 ? " edge cell already holds" : " edge cells already hold") + " a wall, a door or rock and "
					+ (closed == 1 ? "closes" : "close") + " the room there; " + (closed == 1 ? "it is" : "they are") + " kept as " + (closed == 1 ? "it is" : "they are") + ".");
			}
			List<IntVec3> fogWalls = shell.fogEdge.Where((IntVec3 cell) => cell != shell.door).ToList();
			// Settings gate (user, 2026-10-26): Build advice tips/notes.
			if (VerbalCommandsMod.BuildAdviceOn && fogWalls.Count > 0)
			{
				shell.notes.Add(fogWalls.Count + (fogWalls.Count == 1 ? " edge cell is" : " edge cells are") + " under fog ("
					+ string.Join("; ", fogWalls.Take(NamedCells).Select((IntVec3 c) => Xz(c)).ToArray())
					+ (fogWalls.Count > NamedCells ? "; and " + (fogWalls.Count - NamedCells) + " more" : "")
					+ "); nothing is planned there. Once " + (fogWalls.Count == 1 ? "it is" : "they are") + " explored, rock stays as wall, open ground needs a wall.");
			}
			// Settings gate (user, 2026-10-26): Build advice tips/notes.
			if (VerbalCommandsMod.BuildAdviceOn && keepDoor)
			{
				shell.notes.Add("A door already stands or is planned at " + Xz(shell.door) + "; it is kept.");
			}
			// Settings gate (user, 2026-10-26): Build advice tips/notes.
			if (VerbalCommandsMod.BuildAdviceOn && dig && shell.alreadyMarked > 0)
			{
				shell.notes.Add(shell.alreadyMarked + (shell.alreadyMarked == 1 ? " rock cell is" : " rock cells are") + " already marked for mining.");
			}
			// Settings gate (user, 2026-10-26): Build advice tips/notes.
			if (VerbalCommandsMod.BuildAdviceOn && dig && shell.doorInRock && !shell.placeDoor)
			{
				shell.notes.Add("The door cell is mined with the rest; once it is open, say 'build door at " + Xz(shell.door) + "'.");
			}
			// Settings gate (user, 2026-10-26): Build advice tips/notes.
			if (VerbalCommandsMod.BuildAdviceOn && (trees > 0 || otherPlants > 0))
			{
				List<string> parts = new List<string>();
				if (trees > 0)
				{
					parts.Add(trees + (trees == 1 ? " tree" : " trees"));
				}
				if (otherPlants > 0)
				{
					parts.Add(otherPlants + (otherPlants == 1 ? " other plant" : " other plants"));
				}
				shell.notes.Add(string.Join(" and ", parts.ToArray()) + " stand inside; they do not stop the room.");
			}
			// Settings gate (user, 2026-10-26): Build advice tips/notes. AddRoofNotes and AddAutoRoofRules only add notes.
			if (VerbalCommandsMod.BuildAdviceOn)
			{
				AddRoofNotes(map, shell, dig, ringBlocked);
			}

			// ---- the confirm sentence ----
			string doorWhere = " on the " + sideName + " side at " + AgentPlace.Describe(map, shell.door);
			string wallsText = shell.wallCells.Count > 0
				? shell.wallCells.Count + " " + Plural(LabelOf(wall), shell.wallCells.Count)
				: "no new walls";
			string doorText;
			if (shell.fogEdge.Contains(shell.door))
			{
				doorText = "the door cell" + doorWhere + " is under fog, nothing planned there";
			}
			else if (keepDoor)
			{
				doorText = "the existing door" + doorWhere;
			}
			else if (shell.placeDoor)
			{
				doorText = "a door" + doorWhere;
			}
			else
			{
				doorText = "the door cell" + doorWhere + " is mined too";
			}
			string stuffText = StuffSummary(shell);
			if (!dig)
			{
				shell.line = "Build a room " + rect.Width + " by " + rect.Height + " (inside " + shell.inside.Width + " by " + shell.inside.Height
					+ ") at " + AgentPlace.DescribeRect(map, rect) + ": " + wallsText + " and " + doorText + stuffText + ".";
			}
			else
			{
				string mine = shell.mineInside > 0
					? "mine " + shell.mineInside + (shell.mineInside == 1 ? " rock cell" : " rock cells") + " inside"
					: "no new rock cells to mine inside";
				if (shell.fogMineCells.Count > 0)
				{
					mine += ", mark " + shell.fogMineCells.Count + (shell.fogMineCells.Count == 1 ? " fogged cell" : " fogged cells") + " inside for mining";
				}
				shell.line = "Dig a room " + rect.Width + " by " + rect.Height + " (inside " + shell.inside.Width + " by " + shell.inside.Height
					+ ") at " + AgentPlace.DescribeRect(map, rect) + ": " + mine + ", " + wallsText + ", " + doorText + stuffText + ".";
			}
			return shell;
		}

		// Adds the writes of 3 / 4 (mine, walls, door, with readers and tracking) to plan, in that order.
		internal static void AddShellWrites(Map map, Plan plan, RoomShell shell, string label)
		{
			string roomText = "room " + Corners(shell.rect);
			AddMineWrite(map, plan, shell.mineCells, roomText, label, "rock cells", false);
			AddMineWrite(map, plan, shell.fogMineCells, roomText, label, "fogged cells", true);
			{
				// Wind check (AgentWindCheck, 2026-10-14): walls and the door are high; note any turbine path they cross.
				List<AgentWindCheck.Placed> high = new List<AgentWindCheck.Placed>();
				foreach (IntVec3 cell in shell.wallCells)
				{
					high.Add(new AgentWindCheck.Placed(ThingDefOf.Wall, cell, ThingDefOf.Wall.defaultPlacingRot));
				}
				if (shell.placeDoor && shell.door.IsValid)
				{
					high.Add(new AgentWindCheck.Placed(ThingDefOf.Door, shell.door, ThingDefOf.Door.defaultPlacingRot));
				}
				foreach (IntVec3 cell in shell.sharedDoors)
				{
					high.Add(new AgentWindCheck.Placed(ThingDefOf.Door, cell, ThingDefOf.Door.defaultPlacingRot));
				}
				AgentWindCheck.Note(plan, map, high);
				AgentLightCheck.Note(plan, map, high);
				// Door check (AgentDoorCheck, 2026-10-14): an existing room across a wall of this one.
				AgentDoorCheck.NoteShell(plan, map, shell);
			}
			AddWallDoorWrites(map, plan, shell, label, roomText);
		}

		// One mine write of the room (cells: the rock cells, or the fogged cells of a dig room). fogged: the readback counts
		// a cell only by its designation, so nothing under the fog is read.
		private static void AddMineWrite(Map map, Plan plan, List<IntVec3> mineCells, string roomText, string label, string noun, bool fogged)
		{
			if (mineCells.Count > 0)
			{
				List<IntVec3> cells = new List<IntVec3>(mineCells);
				Write mine = new Write();
				mine.description = "Mine designations: " + cells.Count + " " + noun + " of " + roomText;
				mine.apply = delegate
				{
					List<LocalTargetInfo> targets = new List<LocalTargetInfo>();
					List<IntVec3> refused = new List<IntVec3>();
					foreach (IntVec3 cell in cells)
					{
						string problem = MineProblem(map, cell);
						if (problem != null)
						{
							if (problem != AlreadyMarked)
							{
								refused.Add(cell);
							}
							continue;
						}
						// Designator_Mine.DesignateSingleCell (Designator_Mine.cs:77-78), without the warning popup.
						map.designationManager.AddDesignation(new Designation(cell, DesignationDefOf.Mine));
						map.designationManager.TryRemoveDesignation(cell, DesignationDefOf.SmoothWall);
						targets.Add(new LocalTargetInfo(cell));
					}
					if (targets.Count > 0)
					{
						AgentFollow.Track(map, targets, DesignationDefOf.Mine, label + " (mining)");
					}
					if (refused.Count > 0)
					{
						throw new Fault(refused.Count + " of " + cells.Count + " " + noun + " can't be marked for mining any more (first " + Xz(refused[0]) + ").");
					}
				};
				plan.writes.Add(mine.Reads(() =>
				{
					int marked = 0;
					foreach (IntVec3 cell in cells)
					{
						// Marked, or already mined out (no Mineable left, GridsUtility.cs:362); a cell still under fog only by
						// its designation.
						if (map.designationManager.DesignationAt(cell, DesignationDefOf.Mine) != null
							|| ((!fogged || AgentPlace.Visible(map, cell)) && cell.GetFirstMineable(map) == null))
						{
							marked++;
						}
					}
					return roomText + ": " + marked + " of " + cells.Count + " " + noun + " marked for mining or mined";
				}));
			}
		}

		// The wall, door and shared-door writes of the room, after the mine writes and the checks.
		private static void AddWallDoorWrites(Map map, Plan plan, RoomShell shell, string label, string roomText)
		{
			if (shell.wallCells.Count > 0)
			{
				List<IntVec3> cells = new List<IntVec3>(shell.wallCells);
				ThingDef wall = ThingDefOf.Wall;
				Write walls = BlueprintsWrite(map, wall, cells, wall.defaultPlacingRot, shell.wallStuff, label,
					"Blueprints: " + cells.Count + " wall cells of " + roomText, BuildDefs.Label(wall, shell.wallStuff));
				plan.writes.Add(walls.Reads(() =>
				{
					int n = 0;
					foreach (IntVec3 cell in cells)
					{
						if (HoldsOrPlans(map, cell, wall))
						{
							n++;
						}
					}
					return roomText + ": " + n + " of " + cells.Count + " wall cells built or planned";
				}));
			}
			if (shell.placeDoor)
			{
				List<IntVec3> cells = new List<IntVec3> { shell.door };
				ThingDef doorDef = ThingDefOf.Door;
				Write door = BlueprintsWrite(map, doorDef, cells, doorDef.defaultPlacingRot, shell.doorStuff, label + " (door)",
					"Blueprint: door at " + Xz(shell.door), BuildDefs.Label(doorDef, shell.doorStuff));
				IntVec3 doorCell = shell.door;
				plan.writes.Add(door.Reads(() => roomText + ": door at " + Xz(doorCell) + " "
					+ (HoldsOrPlans(map, doorCell, doorDef) ? "built or planned" : "not built or planned")));
			}
			if (shell.sharedDoors.Count > 0)
			{
				List<IntVec3> cells = new List<IntVec3>(shell.sharedDoors);
				ThingDef doorDef = ThingDefOf.Door;
				Write doors = BlueprintsWrite(map, doorDef, cells, doorDef.defaultPlacingRot, shell.doorStuff, label + " (shared door)",
					"Blueprints: " + cells.Count + " doors in shared walls of " + roomText, BuildDefs.Label(doorDef, shell.doorStuff));
				plan.writes.Add(doors.Reads(() => CountReport(map, doorDef, cells, roomText + " shared doors")));
			}
		}

		// ---- roof notes -------------------------------------------------------------------------------

		// The game's own rules for when a closed box roofs itself (cites in the header). Says only what the rules say.
		private static void AddRoofNotes(Map map, RoomShell shell, bool dig, bool ringBlocked)
		{
			List<IntVec3> inside = shell.inside.Cells.ToList();
			int roofed = 0;
			int thick = 0;
			int sky = 0;
			int unsupportedSky = 0;
			int unsupportedAll = 0;
			foreach (IntVec3 cell in inside)
			{
				bool far = Unsupported(shell, cell);
				// A fogged cell (dig room): its roof is not read, it counts only for the support distance.
				if (!AgentPlace.Visible(map, cell))
				{
					if (far)
					{
						unsupportedAll++;
					}
					continue;
				}
				RoofDef roof = map.roofGrid.RoofAt(cell);
				if (roof != null)
				{
					roofed++;
					if (roof.isThickRoof)
					{
						thick++;
					}
				}
				else
				{
					sky++;
					if (far)
					{
						unsupportedSky++;
					}
				}
				if (far)
				{
					unsupportedAll++;
				}
			}

			if (dig)
			{
				// Mining removes a roof holder (Building.cs:212-214): a roofed cell keeps its roof only while a roof holder
				// (wall or rock) stays within 6.9 of it, otherwise the game marks it to collapse if that roof can collapse
				// (RoofCollapseCellsFinder.cs:19-42, :27, :35).
				if (shell.columns.Count > 0)
				{
					shell.notes.Add(shell.columns.Count + (shell.columns.Count == 1 ? " column of rock is" : " columns of rock are")
						+ " left standing inside (" + string.Join("; ", shell.columns.Take(NamedCells).Select((IntVec3 c) => Xz(c)).ToArray())
						+ (shell.columns.Count > NamedCells ? "; and " + (shell.columns.Count - NamedCells) + " more" : "")
						+ ") so the roof holds: a roof more than " + SupportCells + " cells from any wall or rock falls in.");
				}
				if (unsupportedAll > 0)
				{
					shell.notes.Add(unsupportedAll + (unsupportedAll == 1 ? " inside cell is" : " inside cells are") + " more than " + SupportCells
						+ " cells from the nearest wall or rock of the room; the game lets a roof that far from any support collapse (support distance "
						+ "6.9), so leave a column of rock there or dig a smaller room.");
				}
				if (roofed > 0 && unsupportedAll == 0)
				{
					shell.notes.Add("The rock roof over the inside stays while a wall or rock stands within " + SupportCells + " cells of each cell.");
				}
				if (sky > 0)
				{
					shell.notes.Add(sky + " of " + inside.Count + " inside cells are not under a roof.");
					AddAutoRoofRules(shell, ringBlocked, sky, unsupportedSky);
				}
				return;
			}

			if (roofed == inside.Count)
			{
				shell.notes.Add("Every inside cell is already under a roof" + (thick == inside.Count ? " (overhead mountain)" : "") + "; no roof is needed.");
				return;
			}
			AddAutoRoofRules(shell, ringBlocked, sky, unsupportedSky);
		}

		private static void AddAutoRoofRules(RoomShell shell, bool ringBlocked, int skyCells, int unsupportedSky)
		{
			bool bad = false;
			int area = shell.inside.Area;
			if (area > AutoRoofCellLimit)
			{
				bad = true;
				shell.notes.Add("The inside is " + area + " cells; the game roofs a closed room by itself only up to " + AutoRoofCellLimit
					+ " cells, so the colonists will not roof this one by themselves.");
			}
			if (unsupportedSky > 0)
			{
				bad = true;
				shell.notes.Add(unsupportedSky + (unsupportedSky == 1 ? " inside cell is" : " inside cells are") + " more than " + SupportCells
					+ " cells from any wall (the roof support distance is 6.9) and will not be roofed by themselves; a wall or column inside fixes it.");
			}
			if (ringBlocked)
			{
				bad = true;
				shell.notes.Add("A wall of another faction, or a building that stops auto roofing, is part of the room's ring; the game then does not roof the room by itself.");
			}
			if (!bad && skyCells > 0)
			{
				shell.notes.Add("The colonists roof it themselves once the walls and door stand.");
			}
		}

		// The rock cells left standing as columns when a big area is dug out (user 2026-10-04: "you have to put column
		// for very large areas"). A roof falls in where no roof holder stands within 6.9 (RoofCollapseUtility.cs:5), so
		// an area more than 12 cells both ways has cells too far from its edge. Columns stand on a grid 9 apart, the
		// first one 7 cells in from the edge: every dug cell is then within 6 cells of the edge or within 6.4
		// (half the diagonal of a 9 by 9 square) of a column. `dug` is the rectangle that is mined (a room's inside,
		// or the AREA of a mine order). Empty when the area is narrow enough to need none.
		internal const int ColumnSpacing = 9;
		internal const int ColumnFirst = 6;

		internal static HashSet<IntVec3> ColumnCells(CellRect dug)
		{
			HashSet<IntVec3> cells = new HashSet<IntVec3>();
			if (dug.Width <= 2 * SupportCells || dug.Height <= 2 * SupportCells)
			{
				return cells;
			}
			for (int x = dug.minX + ColumnFirst; x <= dug.maxX; x += ColumnSpacing)
			{
				for (int z = dug.minZ + ColumnFirst; z <= dug.maxZ; z += ColumnSpacing)
				{
					cells.Add(new IntVec3(x, 0, z));
				}
			}
			return cells;
		}

		// True when the inside cell is too far from the room's edge and from every column left standing.
		private static bool Unsupported(RoomShell shell, IntVec3 cell)
		{
			if (WallDistance(shell.rect, cell) <= SupportCells)
			{
				return false;
			}
			foreach (IntVec3 column in shell.columns)
			{
				// 6.9 squared = 47.61 (RoofCollapseUtility.cs:5, IntVec3.InHorDistOf).
				if ((column - cell).LengthHorizontalSquared <= 47)
				{
					return false;
				}
			}
			return true;
		}

		// Whole cells from the cell to the nearest ring cell in a straight line (the ring is rect's edge).
		// Euclid to the ring is never shorter than this along an axis, so it decides the support test.
		private static int WallDistance(CellRect rect, IntVec3 cell)
		{
			return Math.Min(Math.Min(cell.x - rect.minX, rect.maxX - cell.x), Math.Min(cell.z - rect.minZ, rect.maxZ - cell.z));
		}

		// ---- classification and small helpers --------------------------------------------------------

		private enum EdgeKind
		{
			Open,
			Rock,
			Wall,
			Door,
			WallPlanned,
			DoorPlanned
		}

		// What closes (or does not close) an edge cell: natural rock, a built wall or door, or a player blueprint or
		// frame of a wall or door (ThingDef.IsWall / IsDoor, ThingDef.cs:880, :912).
		private static EdgeKind Classify(Map map, IntVec3 cell, out Thing found)
		{
			found = null;
			List<Thing> things = cell.GetThingList(map);
			for (int i = 0; i < things.Count; i++)
			{
				Thing t = things[i];
				ThingDef d = t.def;
				EdgeKind kind = EdgeKind.Open;
				if (d.category == ThingCategory.Building)
				{
					if (d.building != null && d.building.isNaturalRock)
					{
						kind = EdgeKind.Rock;
					}
					else if (d.IsDoor)
					{
						kind = EdgeKind.Door;
					}
					else if (d.IsWall || (d.building != null && d.building.canPlaceOverWall))
					{
						// A vent or cooler stands in a wall line (canPlaceOverWall, GenConstruct.cs:571; user 2026-10-16).
						kind = EdgeKind.Wall;
					}
				}
				else if ((d.IsBlueprint || d.IsFrame) && t.Faction == Faction.OfPlayer)
				{
					ThingDef built = d.entityDefToBuild as ThingDef;
					if (built != null && built.IsDoor)
					{
						kind = EdgeKind.DoorPlanned;
					}
					else if (built != null && built.IsWall)
					{
						kind = EdgeKind.WallPlanned;
					}
				}
				if (kind != EdgeKind.Open)
				{
					found = t;
					return kind;
				}
			}
			return EdgeKind.Open;
		}

		// A built thing of def, or a player blueprint or frame of it, stands on the cell.
		private static bool HoldsOrPlans(Map map, IntVec3 cell, BuildableDef def)
		{
			if (!cell.InBounds(map))
			{
				return false;
			}
			List<Thing> things = cell.GetThingList(map);
			for (int i = 0; i < things.Count; i++)
			{
				ThingDef td = things[i].def;
				if (td.IsBlueprint || td.IsFrame)
				{
					if (td.entityDefToBuild == def && things[i].Faction == Faction.OfPlayer)
					{
						return true;
					}
				}
				else if (td == def)
				{
					return true;
				}
			}
			TerrainDef terrain = def as TerrainDef;
			return terrain != null && map.terrainGrid.TerrainAt(cell) == terrain;
		}

		// null = a mine designation may go there now; AlreadyMarked = it has one (Mine or MineVein); else the reason.
		// Designator_Mine.CanDesignateCell when the map is the current one (Designator.Map = Find.CurrentMap,
		// Designator.cs:48), otherwise the same tests copied from Designator_Mine.cs:33-73.
		private static string MineProblem(Map map, IntVec3 cell)
		{
			if (!cell.InBounds(map))
			{
				return "That spot is off the map.";
			}
			if (map.designationManager.DesignationAt(cell, DesignationDefOf.Mine) != null
				|| map.designationManager.DesignationAt(cell, DesignationDefOf.MineVein) != null)
			{
				return AlreadyMarked;
			}
			if (Find.CurrentMap == map)
			{
				try
				{
					AcceptanceReport report = new Designator_Mine().CanDesignateCell(cell);
					if (report.Accepted)
					{
						return null;
					}
					return report.Reason.NullOrEmpty() ? "The game does not allow mining there." : report.Reason;
				}
				catch (Exception)
				{
				}
			}
			// Designator_Mine.cs:41-43: a fogged cell is accepted without looking under it.
			if (cell.Fogged(map))
			{
				return null;
			}
			Mineable mineable = cell.GetFirstMineable(map);
			if (mineable == null)
			{
				return "There is nothing there to mine.";
			}
			if (!mineable.def.mineable)
			{
				return "That can't be mined.";
			}
			return null;
		}

		private static TerrainDef ResolveFloor(string name)
		{
			List<TerrainDef> found = new List<TerrainDef>();
			foreach (TerrainDef def in DefDatabase<TerrainDef>.AllDefsListForReading)
			{
				if (def.BuildableByPlayer && !def.label.NullOrEmpty()
					&& (FormPlanner.SameName(def.label, name) || FormPlanner.SameName(def.defName, name)))
				{
					found.Add(def);
				}
			}
			if (found.Count == 0)
			{
				List<string> some = AgentBuild.Floors().Take(12).ToList();
				throw new Fault("No floor called '" + name.Trim() + "' can be built" + (some.Count > 0 ? "; floors you can build include " + FormPlanner.JoinNames(some) : "") + ".");
			}
			List<TerrainDef> available = found.Where((TerrainDef d) => FloorAvailable(d)).ToList();
			if (available.Count == 0)
			{
				throw new Fault("The colony can't build " + LabelOf(found[0]) + " yet.");
			}
			if (available.Count > 1)
			{
				throw new Fault("'" + name.Trim() + "' could be " + FormPlanner.JoinNames(available.Select((TerrainDef d) => d.defName)) + "; say which.");
			}
			return available[0];
		}

		// Designator_Build.Visible = what the build menu shows now (as AgentBuild.Floors does); fallback research done.
		private static bool FloorAvailable(TerrainDef def)
		{
			try
			{
				return new Designator_Build(def).Visible;
			}
			catch (Exception)
			{
				return def.IsResearchFinished;
			}
		}

		// BuildableDef.CostList (BuildableDef.cs:190) times the cells; BuildDefs.AddCosts takes a ThingDef only.
		private static void AddFloorCosts(Dictionary<ThingDef, int> needed, TerrainDef def, int cells)
		{
			List<ThingDefCountClass> costs = def.CostList;
			if (costs == null)
			{
				return;
			}
			foreach (ThingDefCountClass cost in costs)
			{
				if (cost != null && cost.thingDef != null)
				{
					int have;
					needed.TryGetValue(cost.thingDef, out have);
					needed[cost.thingDef] = have + cost.count * cells;
				}
			}
		}

		// One Write that places every cell with AgentBuild.PlaceNow and follows what it placed as one group.
		private static Write BlueprintsWrite(Map map, BuildableDef def, List<IntVec3> cells, Rot4 rot, ThingDef stuff,
			string trackLabel, string description, string whatText)
		{
			List<IntVec3> list = new List<IntVec3>(cells);
			Write write = new Write();
			write.description = description;
			write.apply = delegate
			{
				List<KeyValuePair<IntVec3, BuildableDef>> placed = new List<KeyValuePair<IntVec3, BuildableDef>>();
				List<IntVec3> failed = new List<IntVec3>();
				foreach (IntVec3 cell in list)
				{
					Blueprint_Build blueprint = AgentBuild.PlaceNow(map, def, cell, rot, stuff);
					if (blueprint == null)
					{
						failed.Add(cell);
					}
					else
					{
						placed.Add(new KeyValuePair<IntVec3, BuildableDef>(blueprint.Position, def));
					}
				}
				if (placed.Count > 0)
				{
					AgentBuild.TrackGroup(map, trackLabel, placed);
				}
				if (failed.Count > 0)
				{
					throw new Fault(placed.Count + " of " + list.Count + " " + whatText + " blueprints were placed; " + failed.Count
						+ (failed.Count == 1 ? " spot" : " spots") + " can't be built on any more (first " + Xz(failed[0]) + ").");
				}
			};
			return write;
		}

		private static string CountReport(Map map, BuildableDef def, List<IntVec3> cells, string tag)
		{
			int n = 0;
			foreach (IntVec3 cell in cells)
			{
				if (HoldsOrPlans(map, cell, def))
				{
					n++;
				}
			}
			return tag + ": " + n + " of " + cells.Count + " cells built or planned";
		}

		private static CellRect AreaOf(FormContext c)
		{
			string areaText = c.Text("AREA");
			if (areaText == null)
			{
				throw new Fault("The order did not say which area.");
			}
			CellRect rect;
			string reason;
			if (!AgentPlace.TryRect(c.map, areaText, out rect, out reason))
			{
				throw new Fault(string.IsNullOrEmpty(reason) ? "I could not find the area '" + areaText + "'." : reason);
			}
			return rect;
		}

		// "north" etc.; null/empty = south (defaulted = true). Anything else is a Fault.
		private static Rot4 ParseSide(string side, out bool defaulted)
		{
			defaulted = false;
			string s = (side ?? "").Trim().ToLowerInvariant();
			switch (s)
			{
				case "":
					defaulted = true;
					return Rot4.South;
				case "north":
					return Rot4.North;
				case "south":
					return Rot4.South;
				case "east":
					return Rot4.East;
				case "west":
					return Rot4.West;
				default:
					throw new Fault("A door side is north, south, east or west.");
			}
		}

		private static string SideName(Rot4 side)
		{
			if (side == Rot4.North)
			{
				return "north";
			}
			if (side == Rot4.East)
			{
				return "east";
			}
			if (side == Rot4.West)
			{
				return "west";
			}
			return "south";
		}

		// The middle cell of the side's edge, corners excluded: south x = minX + (Width-1)/2, z = minZ; north z = maxZ;
		// west x = minX, z = minZ + (Height-1)/2; east x = maxX.
		private static IntVec3 DoorCell(CellRect rect, Rot4 side)
		{
			int midX = rect.minX + (rect.Width - 1) / 2;
			int midZ = rect.minZ + (rect.Height - 1) / 2;
			if (side == Rot4.North)
			{
				return new IntVec3(midX, 0, rect.maxZ);
			}
			if (side == Rot4.East)
			{
				return new IntVec3(rect.maxX, 0, midZ);
			}
			if (side == Rot4.West)
			{
				return new IntVec3(rect.minX, 0, midZ);
			}
			return new IntVec3(midX, 0, rect.minZ);
		}

		// ", wood" / ", wood walls and steel door" / "" when nothing that takes a material is placed.
		private static string StuffSummary(RoomShell shell)
		{
			string wallName = shell.wallStuff != null && shell.wallCells.Count > 0 ? LabelOf(shell.wallStuff) : null;
			string doorName = shell.doorStuff != null && shell.placeDoor ? LabelOf(shell.doorStuff) : null;
			if (wallName != null && doorName != null && wallName != doorName)
			{
				return ", " + wallName + " walls and " + doorName + " door";
			}
			string one = wallName ?? doorName;
			return one != null ? ", " + one : "";
		}

		private static string RockLabel(ThingDef d)
		{
			string label = LabelOf(d);
			return label.IndexOf("rock", StringComparison.OrdinalIgnoreCase) >= 0 ? label : label + " rock";
		}

		private static string FailureText(List<string> failures)
		{
			string text = string.Join("; ", failures.Take(NamedCells).ToArray());
			if (failures.Count > NamedCells)
			{
				text += "; and " + (failures.Count - NamedCells) + " more";
			}
			return text;
		}

		private static string Trim(string reason)
		{
			return (reason ?? "").Trim().TrimEnd('.');
		}

		private static string Plural(string label, int count)
		{
			if (count == 1 || label.NullOrEmpty())
			{
				return label;
			}
			try
			{
				LanguageWorker worker = Find.ActiveLanguageWorker;
				if (worker != null)
				{
					return worker.Pluralize(label);
				}
			}
			catch (Exception)
			{
			}
			return label + "s";
		}

		private static void AddMaterialNotes(Plan plan, Map map, Dictionary<ThingDef, int> needed)
		{
			AddMaterialNotes(plan.notes, map, needed);
		}

		// MaterialText joins its lines with a newline: one note per line.
		private static void AddMaterialNotes(List<string> notes, Map map, Dictionary<ThingDef, int> needed)
		{
			string text = AgentBuild.MaterialText(map, needed);
			if (text.Length == 0)
			{
				return;
			}
			foreach (string part in text.Split('\n'))
			{
				if (part.Length > 0)
				{
					notes.Add(part);
				}
			}
		}

		private static string Xz(IntVec3 c)
		{
			return c.x.ToString(CultureInfo.InvariantCulture) + ", " + c.z.ToString(CultureInfo.InvariantCulture);
		}

		private static string Corners(CellRect rect)
		{
			return Xz(new IntVec3(rect.minX, 0, rect.minZ)) + " to " + Xz(new IntVec3(rect.maxX, 0, rect.maxZ));
		}

		private static string LabelOf(Def def)
		{
			return def.label.NullOrEmpty() ? def.defName : def.label;
		}
	}
}
