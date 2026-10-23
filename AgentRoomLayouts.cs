using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Agent 08, brief S3: a furnished room at the place the player names. Form "build furnished room": TYPE
	// (bedroom, barracks, kitchen, dining room, research room), AREA (the outside, walls included), STUFF?, SIDE?,
	// COUNT? (barracks: how many beds). The walls and the door are AgentBuildShapes' room shell; this file adds the
	// furniture, laid out by the game's own placement rules and not copied from a room on the map (a fresh map has
	// none; user 2026-10-03, 00_AMENDMENT.md Addition 4). Blueprints only: colonists build. MAIN THREAD ONLY.
	// Fair play: nothing here reads or sets dev mode or DebugSettings; every cell goes through AgentBuild.CellProblem
	// (the build tool's own check, nothing under fog).
	//
	// Game facts relied on (decompiled tree sources\RimWorldDecompiled\):
	//   a bed's head and foot cells per rotation: BedUtility.GetSleepingSlotPos / GetFeetSlotPos  RimWorld\BedUtility.cs:13-40
	//   a facility (end table, dresser, multi-analyzer) links only where the game's own test says so: adjacency,
	//       "cardinal to the bed head", or maxDistance  RimWorld\CompAffectedByFacilities.cs:118-190
	//   where the worker stands: ThingUtility.InteractionCellWhenAt  Verse\ThingUtility.cs:83-88
	//   a seat counts for eating when a table (surfaceType Eat) is on a cardinal side of it  RimWorld\Toils_Ingest.cs:129, :168-169
	//   room roles: beds (humanlike) make a bedroom or a barracks  RimWorld\RoomRoleWorker_Bedroom.cs:14-43,
	//       RoomRoleWorker_Barracks.cs:10-37; an eat-surface table a dining room  RoomRoleWorker_DiningRoom.cs:8-20;
	//       a research bench a laboratory  RoomRoleWorker_Laboratory.cs:8-20; a meal stove a kitchen  RoomRoleWorker_Kitchen.cs:8-39
	// From the wiki (rimworldwiki End_table, Bedroom): one end table per bed, next to the head; a bedroom's inside of
	// 5 by 5 or 4 by 6 is a good size.
	// Not confirmed in the source: the defNames below are the game's own as written in mod\Layouts\example_rooms.txt
	// (Bed, EndTable, Dresser, TableButcher, ElectricStove, HiTechResearchBench, MultiAnalyzer, Stool) or the usual
	// ones (SleepingSpot, TorchLamp, FueledStove, Campfire, ButcherSpot, Table1x2c, Table2x2c, Table2x4c,
	// DiningChair, SimpleResearchBench); a name the game does not have is skipped.
	public static class AgentRoomLayouts
	{
		private const int PiecesNamed = 12;
		private const int TightBedroomCells = 20;
		private const int CellsPerLight = 30;

		private sealed class Piece
		{
			public ThingDef def;
			public ThingDef stuff;
			public IntVec3 cell;
			public Rot4 rot;
			public CellRect rect;
			public bool hasSpot;        // a worker spot (interaction cell)
			public IntVec3 spot;
			public bool isBed;
			public bool isSeat;
		}

		private sealed class Layout
		{
			public Map map;
			public RoomShell shell;
			public IntVec3 doorFront;
			public List<Piece> pieces = new List<Piece>();
			public HashSet<IntVec3> taken = new HashSet<IntVec3>();     // footprints
			public HashSet<IntVec3> keepFree = new HashSet<IntVec3>();  // worker spots and the cell inside the door
			public List<string> leftOut = new List<string>();
			public Dictionary<ThingDef, ThingDef> stuffOf = new Dictionary<ThingDef, ThingDef>();
		}

		private delegate bool Fits(Piece p);
		private delegate float Score(Piece p);

		public static PlanReply PlanFurnished(FormContext c)
		{
			Map map = c.map;
			string type = (c.Text("TYPE") ?? "").Trim().ToLowerInvariant();
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
			RoomShell shell = AgentBuildShapes.PlanShell(map, rect, c.Text("SIDE"), c.Text("STUFF"), false);
			rect = shell.rect;
			if (shell.inside.Width < 1 || shell.inside.Height < 1)
			{
				throw new Fault("A " + rect.Width + " by " + rect.Height + " room has no inside to furnish. Make it bigger.");
			}

			Layout layout = new Layout();
			layout.map = map;
			layout.shell = shell;
			// The inside cell in front of the door stays free.
			layout.doorFront = shell.door - shell.doorSide.FacingCell;
			layout.keepFree.Add(layout.doorFront);

			List<string> notes = new List<string>();
			string size = rect.Width + " by " + rect.Height;
			switch (type)
			{
				case "bedroom":
					FillBedroom(layout, size, notes);
					break;
				case "barracks":
					FillBarracks(layout, size, BedCount(c, map, notes), notes);
					break;
				case "kitchen":
					FillKitchen(layout, size, notes);
					break;
				case "dining room":
					FillDiningRoom(layout, size, notes);
					break;
				case "research room":
					FillResearchRoom(layout, size, notes);
					break;
				default:
					throw new Fault("A furnished room is a bedroom, barracks, kitchen, dining room or research room; '" + type + "' is none of them.");
			}

			// Material: the furniture's own stuff (the STUFF word when the piece can be made of it, else the game's pick).
			Dictionary<ThingDef, int> claimed = new Dictionary<ThingDef, int>();
			string stuffWord = c.Text("STUFF");
			foreach (IGrouping<ThingDef, Piece> group in layout.pieces.GroupBy((Piece p) => p.def))
			{
				ThingDef stuff = StuffFor(map, group.Key, stuffWord, group.Count(), claimed, notes);
				foreach (Piece p in group)
				{
					p.stuff = stuff;
				}
			}
			Dictionary<ThingDef, int> needed = new Dictionary<ThingDef, int>(shell.needed);
			foreach (Piece p in layout.pieces)
			{
				BuildDefs.AddCosts(needed, p.def, p.stuff, 1);
			}

			Plan plan = FormPlanner.NewPlan("agent build furnished room");
			// The shell's own material lines are replaced by lines for walls and furniture together.
			HashSet<string> shellMaterial = new HashSet<string>(AgentBuild.MaterialText(map, shell.needed).Split('\n'));
			plan.notes.AddRange(shell.notes.Where((string n) => !shellMaterial.Contains(n)));
			foreach (string part in AgentBuild.MaterialText(map, needed).Split('\n'))
			{
				if (part.Length > 0)
				{
					plan.notes.Add(part);
				}
			}
			plan.notes.AddRange(notes);
			if (layout.leftOut.Count > 0)
			{
				plan.notes.Add("Left out, no place for it: " + FormPlanner.JoinNames(layout.leftOut) + ".");
			}
			foreach (ThingDef powered in layout.pieces.Select((Piece p) => p.def).Distinct().Where((ThingDef d) => NeedsPower(d)))
			{
				plan.notes.Add(FormPlanner.UpperFirst(LabelOf(powered)) + " needs power; it stays off until a power line reaches it.");
			}

			string corners = Corners(rect);
			string label = type + " " + size + " at " + corners;
			AgentBuildShapes.AddShellWrites(map, plan, shell, label);

			List<Piece> pieces = new List<Piece>(layout.pieces);
			Write furniture = new Write();
			furniture.description = "Blueprints: " + pieces.Count + " furniture pieces of " + type + " " + corners;
			furniture.apply = delegate
			{
				List<KeyValuePair<IntVec3, BuildableDef>> placed = new List<KeyValuePair<IntVec3, BuildableDef>>();
				List<string> failed = new List<string>();
				foreach (Piece p in pieces)
				{
					Blueprint_Build blueprint = AgentBuild.PlaceNow(map, p.def, p.cell, p.rot, p.stuff);
					if (blueprint == null)
					{
						failed.Add(LabelOf(p.def) + " at " + Xz(p.cell));
					}
					else
					{
						placed.Add(new KeyValuePair<IntVec3, BuildableDef>(blueprint.Position, p.def));
					}
				}
				if (placed.Count > 0)
				{
					AgentBuild.TrackGroup(map, label + " (furniture)", placed);
				}
				if (failed.Count > 0)
				{
					throw new Fault(placed.Count + " of " + pieces.Count + " furniture blueprints were placed; not placed: "
						+ string.Join(", ", failed.Take(5).ToArray()) + (failed.Count > 5 ? " and " + (failed.Count - 5) + " more" : "") + ".");
				}
			};
			plan.writes.Add(furniture.Reads(() =>
			{
				int n = 0;
				foreach (Piece p in pieces)
				{
					if (HoldsOrPlans(map, p.cell, p.def))
					{
						n++;
					}
				}
				return type + " " + corners + ": " + n + " of " + pieces.Count + " furniture pieces built or planned";
			}));

			List<string> named = new List<string>();
			foreach (Piece p in pieces.Take(PiecesNamed))
			{
				string facing = AgentBuild.FacingText(map, p.def, p.cell, p.rot);
				named.Add(LabelOf(p.def) + " at " + AgentPlace.Describe(map, p.cell) + (facing.Length > 0 ? " (" + facing + ")" : ""));
			}
			if (pieces.Count > PiecesNamed)
			{
				named.Add("and " + (pieces.Count - PiecesNamed) + " more");
			}
			string line = shell.line.TrimEnd('.') + ", furnished as a " + type + ": " + string.Join("; ", named.ToArray()) + ".";
			return FormPlanner.Reply(plan, line, null, null);
		}

		// ---- the five room types ----------------------------------------------------------------------

		private static void FillBedroom(Layout layout, string size, List<string> notes)
		{
			ThingDef bedDef = FirstAvailable("Bed", "SleepingSpot");
			if (bedDef == null)
			{
				throw new Fault("The colony can't build a bed or a sleeping spot now.");
			}
			Piece bed = PlaceBed(layout, bedDef);
			if (bed == null)
			{
				throw new Fault("A " + size + " bedroom has no room for a " + LabelOf(bedDef) + " with its head at a wall. Make it bigger.");
			}
			PlaceFacility(layout, FirstAvailable("EndTable"), bed);
			PlaceFacility(layout, FirstAvailable("Dresser"), bed);
			PlaceLights(layout, 1);
			if (layout.shell.inside.Area < TightBedroomCells)
			{
				notes.Add("Rather tight: a 5 by 5 or 4 by 6 inside (7 by 7 or 6 by 8 outside) gives a better mood.");
			}
			notes.Add("One bed for people makes the room a bedroom.");
		}

		private static void FillBarracks(Layout layout, string size, int wanted, List<string> notes)
		{
			ThingDef bedDef = FirstAvailable("Bed", "SleepingSpot");
			if (bedDef == null)
			{
				throw new Fault("The colony can't build a bed or a sleeping spot now.");
			}
			List<Piece> beds = new List<Piece>();
			for (int i = 0; i < wanted; i++)
			{
				Piece bed = PlaceBed(layout, bedDef);
				if (bed == null)
				{
					break;
				}
				beds.Add(bed);
			}
			if (beds.Count < wanted)
			{
				throw new Fault("A " + size + " barracks has room for " + beds.Count + (beds.Count == 1 ? " bed" : " beds") + "; " + wanted
					+ (wanted == 1 ? " was" : " were") + " asked. Make it bigger.");
			}
			ThingDef endTable = FirstAvailable("EndTable");
			foreach (Piece bed in beds)
			{
				PlaceFacility(layout, endTable, bed);
			}
			PlaceLights(layout, Math.Max(1, layout.shell.inside.Area / CellsPerLight));
			notes.Add(beds.Count > 1 ? "Two or more beds for people make the room a barracks." : "One bed for people makes the room a bedroom, not a barracks.");
		}

		private static void FillKitchen(Layout layout, string size, List<string> notes)
		{
			ThingDef stove = FirstAvailable("FueledStove", "Campfire", "ElectricStove");
			if (stove == null)
			{
				throw new Fault("The colony can't build a stove or a campfire now.");
			}
			if (PlaceBench(layout, stove) == null)
			{
				throw new Fault("A " + size + " kitchen has no room for a " + LabelOf(stove) + " at a wall with its worker spot free. Make it bigger.");
			}
			ThingDef butcher = FirstAvailable("TableButcher", "ButcherSpot");
			if (butcher != null && PlaceBench(layout, butcher) == null)
			{
				layout.leftOut.Add(LabelOf(butcher));
			}
			PlaceLights(layout, 1);
			notes.Add("A stove that cooks meals makes the room a kitchen; butchering in the same room makes it dirty, which makes food poisoning more likely.");
		}

		private static void FillDiningRoom(Layout layout, string size, List<string> notes)
		{
			Piece table = null;
			foreach (string name in new[] { "Table2x4c", "Table2x2c", "Table1x2c" })
			{
				ThingDef def = FirstAvailable(name);
				if (def == null)
				{
					continue;
				}
				// With a ring of free cells round it first (seats on every side), then anywhere.
				table = PlaceTable(layout, def, true) ?? PlaceTable(layout, def, false);
				if (table != null)
				{
					break;
				}
			}
			if (table == null)
			{
				throw new Fault("A " + size + " dining room has no room for a table. Make it bigger.");
			}
			ThingDef seat = FirstAvailable("DiningChair", "Stool");
			int seats = 0;
			if (seat != null)
			{
				seats = PlaceSeats(layout, seat, table);
			}
			if (seats == 0)
			{
				layout.leftOut.Add("seats");
			}
			PlaceLights(layout, 1);
			notes.Add("A table to eat at makes the room a dining room; " + seats + (seats == 1 ? " seat stands" : " seats stand") + " at the table.");
		}

		private static void FillResearchRoom(Layout layout, string size, List<string> notes)
		{
			ThingDef benchDef = FirstAvailable("HiTechResearchBench", "SimpleResearchBench");
			if (benchDef == null)
			{
				throw new Fault("The colony can't build a research bench now.");
			}
			Piece bench = PlaceBench(layout, benchDef);
			if (bench == null)
			{
				throw new Fault("A " + size + " research room has no room for a " + LabelOf(benchDef) + " at a wall with its worker spot free. Make it bigger.");
			}
			if (benchDef.defName == "HiTechResearchBench")
			{
				PlaceFacility(layout, FirstAvailable("MultiAnalyzer"), bench);
			}
			PlaceLights(layout, 1);
			notes.Add("A research bench makes the room a laboratory; a clean room researches faster.");
		}

		// ---- placing one kind of piece ----------------------------------------------------------------

		// A bed: its head against a wall, as far from the door as it gets, not side by side with another bed (one cell
		// between, where an end table can stand).
		private static Piece PlaceBed(Layout layout, ThingDef def)
		{
			CellRect inside = layout.shell.inside;
			return Place(layout, def, delegate(Piece p)
			{
				IntVec3 head = BedUtility.GetSleepingSlotPos(0, p.cell, p.rot, def.Size);
				IntVec3 foot = BedUtility.GetFeetSlotPos(0, p.cell, p.rot, def.Size);
				IntVec3 away = head - foot;
				if (away == IntVec3.Zero)
				{
					// A one-cell sleeping spot: any cell at a wall.
					if (!AtWall(inside, p.rect))
					{
						return false;
					}
				}
				else if (inside.Contains(head + away))
				{
					return false;
				}
				foreach (Piece other in layout.pieces)
				{
					if (other.isBed && other.rect.ExpandedBy(1).Overlaps(p.rect))
					{
						return false;
					}
				}
				return true;
			}, (Piece p) => Dist(p.rect.CenterCell, layout.doorFront));
		}

		// A bench, stove or butcher place: a footprint cell at a wall, the worker spot inside the room and free.
		private static Piece PlaceBench(Layout layout, ThingDef def)
		{
			CellRect inside = layout.shell.inside;
			return Place(layout, def, (Piece p) => AtWall(inside, p.rect), (Piece p) => Dist(p.rect.CenterCell, layout.doorFront));
		}

		// A table: with ring = true only where every cell round it is a free inside cell; nearest the room's middle.
		private static Piece PlaceTable(Layout layout, ThingDef def, bool ring)
		{
			CellRect inside = layout.shell.inside;
			IntVec3 middle = inside.CenterCell;
			return Place(layout, def, delegate(Piece p)
			{
				if (!ring)
				{
					return true;
				}
				foreach (IntVec3 cell in p.rect.ExpandedBy(1).Cells)
				{
					if (!p.rect.Contains(cell) && (!inside.Contains(cell) || layout.taken.Contains(cell) || layout.keepFree.Contains(cell)))
					{
						return false;
					}
				}
				return true;
			}, (Piece p) => -Dist(p.rect.CenterCell, middle));
		}

		// Seats on the free cells on the four sides of the table, each turned to face it. Returns how many were placed.
		private static int PlaceSeats(Layout layout, ThingDef def, Piece table)
		{
			int placed = 0;
			List<IntVec3> spots = new List<IntVec3>();
			foreach (IntVec3 cell in table.rect.Cells)
			{
				for (int i = 0; i < 4; i++)
				{
					IntVec3 seat = cell + GenAdj.CardinalDirections[i];
					if (!table.rect.Contains(seat) && !spots.Contains(seat))
					{
						spots.Add(seat);
					}
				}
			}
			foreach (IntVec3 spot in spots.OrderBy((IntVec3 s) => s.z).ThenBy((IntVec3 s) => s.x))
			{
				IntVec3 wanted = spot;
				Piece piece = Place(layout, def, delegate(Piece p)
				{
					if (p.cell != wanted)
					{
						return false;
					}
					// Turned to the table when it can be turned.
					return !def.rotatable || table.rect.Contains(p.cell + p.rot.FacingCell);
				}, (Piece p) => 0f);
				if (piece != null)
				{
					piece.isSeat = true;
					placed++;
				}
			}
			return placed;
		}

		// An end table, dresser or multi-analyzer: only where the game's own link test accepts it for the target;
		// nearest the target. A piece with no place is left out.
		private static void PlaceFacility(Layout layout, ThingDef def, Piece target)
		{
			if (def == null)
			{
				return;
			}
			bool links = def.GetCompProperties<CompProperties_Facility>() != null;
			Piece piece = Place(layout, def, delegate(Piece p)
			{
				if (!links)
				{
					return p.rect.ExpandedBy(1).Overlaps(target.rect);
				}
				return CompAffectedByFacilities.CanPotentiallyLinkTo_Static(def, p.cell, p.rot, target.def, target.cell, target.rot, layout.map);
			}, (Piece p) => -Dist(p.rect.CenterCell, target.rect.CenterCell));
			if (piece == null)
			{
				layout.leftOut.Add(LabelOf(def) + " for the " + LabelOf(target.def) + " at " + Xz(target.cell));
			}
		}

		// Lights that need no power (a torch lamp), each at a wall and as far from the other lights as it gets.
		private static void PlaceLights(Layout layout, int count)
		{
			ThingDef def = FirstAvailable("TorchLamp");
			if (def == null || NeedsPower(def))
			{
				return;
			}
			CellRect inside = layout.shell.inside;
			IntVec3 middle = inside.CenterCell;
			List<Piece> lights = new List<Piece>();
			for (int i = 0; i < count; i++)
			{
				Piece light = Place(layout, def, (Piece p) => AtWall(inside, p.rect), delegate(Piece p)
				{
					if (lights.Count == 0)
					{
						return -Dist(p.cell, middle);
					}
					return lights.Min((Piece other) => Dist(p.cell, other.cell));
				});
				if (light == null)
				{
					layout.leftOut.Add(LabelOf(def));
					return;
				}
				lights.Add(light);
			}
		}

		// ---- the one search ---------------------------------------------------------------------------

		// Every cell and rotation the piece fits (inside the room, on free cells, accepted by the build tool, its worker
		// spot free), best score first (ties: lower z, then lower x, then rotation); the first that leaves every worker
		// spot, bedside and seat reachable from the door is placed. null when none.
		private static Piece Place(Layout layout, ThingDef def, Fits fits, Score score)
		{
			Map map = layout.map;
			CellRect inside = layout.shell.inside;
			List<Rot4> rots = new List<Rot4>();
			if (def.rotatable)
			{
				rots.Add(Rot4.North);
				rots.Add(Rot4.East);
				rots.Add(Rot4.South);
				rots.Add(Rot4.West);
			}
			else
			{
				rots.Add(def.defaultPlacingRot);
			}
			List<KeyValuePair<float, Piece>> candidates = new List<KeyValuePair<float, Piece>>();
			foreach (IntVec3 cell in inside.Cells)
			{
				foreach (Rot4 rot in rots)
				{
					Piece p = new Piece();
					p.def = def;
					p.cell = cell;
					p.rot = rot;
					p.rect = GenAdj.OccupiedRect(cell, rot, def.Size);
					p.isBed = def.IsBed;
					bool ok = true;
					foreach (IntVec3 f in p.rect.Cells)
					{
						if (!inside.Contains(f) || layout.taken.Contains(f) || layout.keepFree.Contains(f))
						{
							ok = false;
							break;
						}
					}
					if (!ok)
					{
						continue;
					}
					if (def.hasInteractionCell)
					{
						p.hasSpot = true;
						p.spot = ThingUtility.InteractionCellWhenAt(def, cell, rot, map);
						if (!inside.Contains(p.spot) || layout.taken.Contains(p.spot) || p.rect.Contains(p.spot))
						{
							continue;
						}
					}
					if (!fits(p))
					{
						continue;
					}
					if (AgentBuild.CellProblem(map, def, cell, rot, null) != null)
					{
						continue;
					}
					candidates.Add(new KeyValuePair<float, Piece>(score(p), p));
				}
			}
			foreach (KeyValuePair<float, Piece> kv in candidates.OrderByDescending((KeyValuePair<float, Piece> k) => k.Key)
				.ThenBy((KeyValuePair<float, Piece> k) => k.Value.cell.z).ThenBy((KeyValuePair<float, Piece> k) => k.Value.cell.x)
				.ThenBy((KeyValuePair<float, Piece> k) => k.Value.rot.AsInt))
			{
				Piece p = kv.Value;
				layout.pieces.Add(p);
				foreach (IntVec3 f in p.rect.Cells)
				{
					layout.taken.Add(f);
				}
				if (StillWalkable(layout))
				{
					if (p.hasSpot)
					{
						layout.keepFree.Add(p.spot);
					}
					return p;
				}
				layout.pieces.RemoveAt(layout.pieces.Count - 1);
				foreach (IntVec3 f in p.rect.Cells)
				{
					layout.taken.Remove(f);
				}
			}
			return null;
		}

		// A flood fill from the cell inside the door over the inside cells nothing stands on (seats can be walked
		// over). It must reach every worker spot, a cell beside every bed, and a cell beside every other piece.
		private static bool StillWalkable(Layout layout)
		{
			CellRect inside = layout.shell.inside;
			HashSet<IntVec3> blocked = new HashSet<IntVec3>();
			foreach (Piece p in layout.pieces)
			{
				if (!p.isSeat && p.def.passability != Traversability.Standable && !(p.def.building != null && p.def.building.isSittable))
				{
					foreach (IntVec3 f in p.rect.Cells)
					{
						blocked.Add(f);
					}
				}
			}
			if (blocked.Contains(layout.doorFront))
			{
				return false;
			}
			HashSet<IntVec3> reached = new HashSet<IntVec3>();
			Queue<IntVec3> open = new Queue<IntVec3>();
			reached.Add(layout.doorFront);
			open.Enqueue(layout.doorFront);
			while (open.Count > 0)
			{
				IntVec3 cell = open.Dequeue();
				for (int i = 0; i < 4; i++)
				{
					IntVec3 next = cell + GenAdj.CardinalDirections[i];
					if (inside.Contains(next) && !blocked.Contains(next) && reached.Add(next))
					{
						open.Enqueue(next);
					}
				}
			}
			foreach (Piece p in layout.pieces)
			{
				if (p.hasSpot)
				{
					if (!reached.Contains(p.spot))
					{
						return false;
					}
					continue;
				}
				bool beside = false;
				foreach (IntVec3 f in p.rect.Cells)
				{
					if (reached.Contains(f))
					{
						beside = true;
						break;
					}
					for (int i = 0; i < 4 && !beside; i++)
					{
						beside = reached.Contains(f + GenAdj.CardinalDirections[i]);
					}
					if (beside)
					{
						break;
					}
				}
				if (!beside)
				{
					return false;
				}
			}
			return true;
		}

		// ---- small helpers ----------------------------------------------------------------------------

		// COUNT of a barracks; left out = one bed for each free colonist on the map (a note says so).
		private static int BedCount(FormContext c, Map map, List<string> notes)
		{
			double? said = c.Number("COUNT");
			if (said.HasValue)
			{
				int n = (int)said.Value;
				if (n < 1 || n > 40)
				{
					throw new Fault("A barracks takes 1 to 40 beds.");
				}
				return n;
			}
			int colonists = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
			notes.Add("No number of beds was said: one for each of the " + colonists + (colonists == 1 ? " colonist" : " colonists") + " on the map.");
			return colonists;
		}

		// The first of the defNames the game has and the colony can build now (BuildDefs.IsAvailable); null when none.
		private static ThingDef FirstAvailable(params string[] defNames)
		{
			foreach (string name in defNames)
			{
				ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
				string ignored;
				if (def != null && BuildDefs.IsAvailable(def, out ignored))
				{
					return def;
				}
			}
			return null;
		}

		// The STUFF word when the piece can be made of it, else the material the game would pick (with a note).
		private static ThingDef StuffFor(Map map, ThingDef def, string word, int pieces, Dictionary<ThingDef, int> claimed, List<string> notes)
		{
			if (!def.MadeFromStuff)
			{
				return null;
			}
			if (!string.IsNullOrWhiteSpace(word))
			{
				try
				{
					return AgentBuild.ResolveStuffFor(map, def, word, pieces, claimed, notes);
				}
				catch (Fault)
				{
					notes.Add(FormPlanner.UpperFirst(LabelOf(def)) + " can't be made of " + word.Trim() + "; another material is used for it.");
				}
			}
			return AgentBuild.ResolveStuffFor(map, def, null, pieces, claimed, notes);
		}

		private static bool NeedsPower(ThingDef def)
		{
			CompProperties_Power power = def.GetCompProperties<CompProperties_Power>();
			return power != null && power.PowerConsumption > 0f;
		}

		// A footprint cell touches the wall ring (it is on the edge of the inside).
		private static bool AtWall(CellRect inside, CellRect rect)
		{
			return rect.minX == inside.minX || rect.maxX == inside.maxX || rect.minZ == inside.minZ || rect.maxZ == inside.maxZ;
		}

		private static float Dist(IntVec3 a, IntVec3 b)
		{
			return (a - b).LengthHorizontalSquared;
		}

		private static bool HoldsOrPlans(Map map, IntVec3 cell, ThingDef def)
		{
			if (!cell.InBounds(map))
			{
				return false;
			}
			List<Thing> things = cell.GetThingList(map);
			for (int i = 0; i < things.Count; i++)
			{
				ThingDef td = things[i].def;
				if (td == def || ((td.IsBlueprint || td.IsFrame) && td.entityDefToBuild == def))
				{
					return true;
				}
			}
			return false;
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
