using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace VerbalCommands
{
	// Door placement from the colonists' own walks, once a game day in the orchestrator's reminder batch (user,
	// 2026-10-14: "a pathfinder optimizer that takes in data from how many tiles pawns moved across room via existing
	// door to another room between 1 task, and do place door in different configuration on either room a or room b to
	// see if it would result in shorter path." / "then reminder fires every day with "[reminder] I noticed your pawns
	// spend [amnt] moving across {room_a} to {room_b/area_b}. placing doors on {coordinate, coordinate} should save
	// [amnt_diff] tiles"").
	//
	// Recording (Track, every tick from AgentEvents.Tick): each free colonist's walk, the cells it steps on and the
	// tick it entered each, from when it starts moving (Pawn_PathFollower.Moving) until it stops. A walk that goes
	// through a player door between two rooms, at least one of them a proper room (Verse\Room.cs:536), gives one trip
	// for that door: the part of the walk inside the two rooms, from where it came into the first room (its start, or
	// the cell inside the door it came in by) to where it left the second. A side that is not a proper room (outdoors)
	// counts only within 12 cells of the proper room's bounds. Trips are kept for one game day.
	//
	// The daily lines (DailyLines, from AgentReminderScan on the first 6-hour scan of a new day): for each door with at
	// least 5 trips, every wall cell between the same two rooms where a door could go (a constructed or smoothed wall,
	// as the vent line in AgentRoomTempScan; a free standable cell with no building on both sides) is tried as the
	// door, the old door counted as wall. A shortest-path search over the two rooms from the tried door (the game's
	// move ticks, 13 a straight step and 18 a diagonal one, plus the path grid cost of the cell, Verse.AI\PathGrid.cs:55;
	// no corner cutting past an impassable cell) gives each trip's way start -> door -> end. The tried door with the
	// lowest total cost is compared with the same search through the existing door; the line fires when it saves at
	// least 50 tiles and at least 20% of them (both are defaults, not the user's numbers). "spend" is the tiles the
	// colonists really walked in the two rooms; "save" is the search's difference. The colonists chose their targets
	// with today's door, so it is an estimate.
	//
	// Check after a move (user: "record tick when colonist exits that room > record tick when colonist enters room B >
	// diff tick. If new door is placed, record same door to door cost using same logic but with different door ...
	// and a reminder fires (new pathfinder shows x% difference. maybe you have things slowing pawns down?)"). A trip's
	// ticks are taken only from a walk at the usual pace (the job's locomotionUrgency Jog, Verse.AI\Job.cs:55, not
	// paced to another pawn, not carrying a pawn): amble, walk and sprint scale every step (Pawn_PathFollower.cs:784-806).
	// They are divided by the colonist's own ticks per cell (60 / MoveSpeed, Verse\Pawn.cs:1950-1983) and times 13, so
	// a slow colonist and a fast one count alike; what is left is floors, things in the way and doors (the game adds
	// them per step, Pawn_PathFollower.cs:746-770, and waits for a door, :487-495). Colonists only block each other
	// with hostiles near (RimWorld\PawnUtility.cs:472-494), so traffic is not in it outside fights. Rain or snow under
	// open sky slows a colonist too (Pawn.cs:1974-1977) and is left in: on an outdoor side it reads as "slowing down".
	// For a door the
	// ratio "measured / what the search predicts for the same trips" is kept with each suggestion. When a later day
	// finds the old door gone and a door between the same two rooms with at least 5 timed trips, its ratio is
	// compared: 25% or more above the old door's (a default, not the user's number) gives the "things slowing pawns
	// down" line. Comparing ratios, not totals, takes out that the trips after the move go to other places. A moved
	// door is not suggested again for 3 days.
	//
	// Gate: the "Pathfind cost" mod setting (user: "add to mod option setting (pathfind cost) gate."); off, nothing is
	// recorded and no line is made. The trips and suggestions live in the save (DoorPathData in
	// GameComponent_VerbalCommands; user: "file doesnt flush/reset, and remains with savefile"): a load goes on with
	// them, a new game starts empty. Only the walks in progress are dropped at a load.
	internal static class AgentDoorPathScan
	{
		private const int OutdoorMargin = 12;
		private const int MinTrips = 5;
		private const int MinSaveTiles = 50;
		private const float MinSaveShare = 0.2f;
		private const float SlowerShare = 0.25f;
		private const int MaxLines = 5;
		private const int MaxWalk = 3000;
		private const int MaxTrips = 5000;
		private const int CooldownTicks = GenDate.TicksPerDay * 3;
		private const int SuggestionDays = 10;
		// The game's default move ticks for one cell (Verse\PathFinder.cs:256-258).
		private const int StraightCost = 13;
		private const int DiagonalCost = 18;

		private class Walk
		{
			public Map map;
			public List<IntVec3> cells = new List<IntVec3>();
			public List<int> ticks = new List<int>();
			// The colonist's own ticks per straight cell when the walk began; 0 = this walk is not timed.
			public float perCell;
		}

		private static readonly Dictionary<Pawn, Walk> walks = new Dictionary<Pawn, Walk>();

		private static DoorPathData Data
		{
			get
			{
				GameComponent_VerbalCommands c = GameComponent_VerbalCommands.Instance;
				return c == null ? null : c.DoorPath;
			}
		}

		private static bool On
		{
			get { return VerbalCommandsMod.Settings != null && VerbalCommandsMod.Settings.pathfindCost; }
		}

		// A new game or a load (AgentEvents.Reset): the walks in progress belong to the old game. The trips stay (save).
		public static void Reset()
		{
			walks.Clear();
		}

		// ---- recording ----------------------------------------------------------------------------------------------

		// AgentEvents.Tick, every game tick. Main thread.
		public static void Track()
		{
			DoorPathData data = Data;
			if (!On || data == null)
			{
				walks.Clear();
				return;
			}
			int now = Find.TickManager.TicksGame;
			if (data.trackingSince < 0)
			{
				data.trackingSince = now;
			}
			foreach (Map map in Find.Maps)
			{
				if (map == null || !map.IsPlayerHome)
				{
					continue;
				}
				foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
				{
					Walk walk;
					walks.TryGetValue(p, out walk);
					if (p.pather != null && p.pather.Moving)
					{
						if (walk == null || walk.map != map)
						{
							walk = new Walk { map = map, perCell = PerCell(p) };
							walk.cells.Add(p.Position);
							walk.ticks.Add(now);
							walks[p] = walk;
							continue;
						}
						if (walk.cells[walk.cells.Count - 1] != p.Position)
						{
							walk.cells.Add(p.Position);
							walk.ticks.Add(now);
							if (walk.cells.Count >= MaxWalk)
							{
								Finish(data, walk, now);
								walk.cells.RemoveRange(0, walk.cells.Count - 2);
								walk.ticks.RemoveRange(0, walk.ticks.Count - 2);
							}
						}
					}
					else if (walk != null)
					{
						if (walk.map == map && walk.cells[walk.cells.Count - 1] != p.Position)
						{
							walk.cells.Add(p.Position);
							walk.ticks.Add(now);
						}
						Finish(data, walk, now);
						walks.Remove(p);
					}
				}
			}
		}

		// Ticks per straight cell of P at the usual pace, or 0 when its walk is paced otherwise (see the header).
		private static float PerCell(Pawn p)
		{
			Job job = p.CurJob;
			if (job == null || job.locomotionUrgency != LocomotionUrgency.Jog || p.jobs.curDriver == null || p.jobs.curDriver.locomotionUrgencySameAs != null)
			{
				return 0f;
			}
			if (p.carryTracker != null && p.carryTracker.CarriedThing != null && p.carryTracker.CarriedThing.def.category == ThingCategory.Pawn)
			{
				return 0f;
			}
			float speed = p.GetStatValue(StatDefOf.MoveSpeed);
			return speed <= 0f ? 0f : Math.Min(450f, Math.Max(1f, 60f / speed));
		}

		// The trips of one finished walk: one for each player door it went through between two rooms.
		private static void Finish(DoorPathData data, Walk walk, int now)
		{
			Map map = walk.map;
			List<IntVec3> cells = walk.cells;
			for (int i = 1; i < cells.Count - 1; i++)
			{
				Building_Door door = cells[i].GetDoor(map);
				if (door == null || door.Faction != Faction.OfPlayer
					|| !cells[i - 1].AdjacentTo8Way(cells[i]) || !cells[i + 1].AdjacentTo8Way(cells[i]))
				{
					continue;
				}
				Room before = cells[i - 1].GetRoom(map);
				Room after = cells[i + 1].GetRoom(map);
				CellRect box;
				if (!Pair(before, after, map, out box))
				{
					continue;
				}
				int s = i - 1;
				while (s > 0 && In(map, cells[s - 1], before, box) && cells[s - 1].AdjacentTo8Way(cells[s]))
				{
					s--;
				}
				int e = i + 1;
				while (e < cells.Count - 1 && In(map, cells[e + 1], after, box) && cells[e + 1].AdjacentTo8Way(cells[e]))
				{
					e++;
				}
				int norm = walk.perCell > 0f ? (int)Math.Round((walk.ticks[e] - walk.ticks[s]) * StraightCost / walk.perCell) : -1;
				data.trips.Add(new DoorTrip
				{
					mapId = map.uniqueID, door = cells[i], start = cells[s], end = cells[e], tiles = e - s, norm = norm, tick = now
				});
			}
			if (data.trips.Count > MaxTrips)
			{
				data.trips.RemoveRange(0, data.trips.Count - MaxTrips);
			}
		}

		// Two different rooms, neither a doorway, at least one proper. BOX: where a side that is not a proper room counts
		// (the proper room's bounds and 12 cells around; the first proper one when both are).
		private static bool Pair(Room a, Room b, Map map, out CellRect box)
		{
			box = CellRect.Empty;
			if (a == null || b == null || a == b || a.IsDoorway || b.IsDoorway || (!a.ProperRoom && !b.ProperRoom))
			{
				return false;
			}
			box = (a.ProperRoom ? a : b).ExtentsClose.ExpandedBy(OutdoorMargin).ClipInsideMap(map);
			return true;
		}

		private static bool In(Map map, IntVec3 c, Room room, CellRect box)
		{
			return c.InBounds(map) && c.GetRoom(map) == room && (room.ProperRoom || box.Contains(c));
		}

		// ---- the daily lines ----------------------------------------------------------------------------------------

		// AgentReminderScan, on the first scan of a new game day. The "slowing down" lines first, then the move lines,
		// best savings first, at most 5 of each.
		public static List<string> DailyLines()
		{
			DoorPathData data = Data;
			if (!On || data == null)
			{
				return new List<string>();
			}
			int now = Find.TickManager.TicksGame;
			data.trips.RemoveAll((DoorTrip t) => now - t.tick > GenDate.TicksPerDay || MapOf(t.mapId) == null);
			data.suggestions.RemoveAll((DoorSuggestion s) => MapOf(s.mapId) == null
				|| (s.doneTick < 0 && now - s.tick > GenDate.TicksPerDay * SuggestionDays)
				|| (s.doneTick >= 0 && now - s.doneTick > CooldownTicks));
			foreach (Pawn gone in walks.Keys.Where((Pawn p) => p == null || !p.Spawned).ToList())
			{
				walks.Remove(gone);
			}
			int window = data.trackingSince < 0 ? 0 : Math.Min(GenDate.TicksPerDay, now - data.trackingSince);
			string period = window >= GenDate.TicksPerDay - GenDate.TicksPerHour ? "a day"
				: "in the last " + Math.Max(1, window / GenDate.TicksPerHour) + " hours";
			List<string> slower = new List<string>();
			List<KeyValuePair<int, string>> moves = new List<KeyValuePair<int, string>>();
			foreach (IGrouping<string, DoorTrip> g in data.trips.GroupBy((DoorTrip t) => t.mapId + ":" + t.door.x + "," + t.door.z))
			{
				List<DoorTrip> list = g.ToList();
				Map map = MapOf(list[0].mapId);
				Area area = Build(map, list[0].door, list);
				if (area == null)
				{
					continue;
				}
				// A door that replaced a suggested one: the check after the move, then no move line for 3 days.
				DoorSuggestion moved = data.suggestions.FirstOrDefault((DoorSuggestion s) => s.mapId == map.uniqueID && s.doneTick < 0
					&& s.oldDoor != area.door && s.oldDoor.GetDoor(map) == null && area.Joins(s.cellA, s.cellB));
				if (moved != null)
				{
					string line = CheckMoved(area, moved, now);
					if (line != null)
					{
						slower.Add(line);
					}
					continue;
				}
				if (data.suggestions.Any((DoorSuggestion s) => s.mapId == map.uniqueID && s.doneTick >= 0 && s.newDoor == area.door))
				{
					continue;
				}
				int save;
				string move = MoveLine(data, area, period, now, out save);
				if (move != null)
				{
					moves.Add(new KeyValuePair<int, string>(save, move));
				}
			}
			List<string> lines = slower.Take(MaxLines).ToList();
			lines.AddRange(moves.OrderByDescending((KeyValuePair<int, string> kv) => kv.Key).Take(MaxLines)
				.Select((KeyValuePair<int, string> kv) => kv.Value));
			return lines;
		}

		private static Map MapOf(int id)
		{
			return Find.Maps.FirstOrDefault((Map m) => m != null && m.uniqueID == id);
		}

		// What one door's trips run through: the two rooms, the search area and its grids, and the trips that still run
		// between the two rooms (same start and end counted together).
		private class Area
		{
			public Map map;
			public IntVec3 door;
			public Room rA;
			public Room rB;
			public IntVec3 cellA;
			public IntVec3 cellB;
			public CellRect box;
			public CellRect rect;
			public int size;
			// side: 1 room A, 2 room B (or the outdoors within the box), 0 anything else; walk: walkable on the path grid.
			public byte[] side;
			public bool[] walk;
			public PathGrid pg;
			public Dictionary<long, int> pairs = new Dictionary<long, int>();
			// The timed trips only (DoorTrip.norm >= 0) and the sum of their measured ticks.
			public Dictionary<long, int> timedPairs = new Dictionary<long, int>();
			public long measured;
			public int timedCount;
			public int actual;
			public int count;
			public int fromA;
			public Dictionary<Zone, int> zonesB = new Dictionary<Zone, int>();

			// Do the rooms at C1 and C2 (now) form this door's pair, either way round?
			public bool Joins(IntVec3 c1, IntVec3 c2)
			{
				Room r1 = c1.InBounds(map) ? c1.GetRoom(map) : null;
				Room r2 = c2.InBounds(map) ? c2.GetRoom(map) : null;
				return (r1 == rA && r2 == rB) || (r1 == rB && r2 == rA);
			}
		}

		private static Area Build(Map map, IntVec3 doorCell, List<DoorTrip> list)
		{
			if (map == null || doorCell.GetDoor(map) == null)
			{
				return null;
			}
			Area a = new Area { map = map, door = doorCell };
			if (!Sides(map, doorCell, out a.rA, out a.rB, out a.cellA, out a.cellB))
			{
				return null;
			}
			if (!a.rA.ProperRoom)
			{
				Room r = a.rA;
				a.rA = a.rB;
				a.rB = r;
				IntVec3 c = a.cellA;
				a.cellA = a.cellB;
				a.cellB = c;
			}
			a.box = a.rA.ExtentsClose.ExpandedBy(OutdoorMargin).ClipInsideMap(map);
			a.rect = a.box;
			if (a.rB.ProperRoom)
			{
				CellRect ea = a.rA.ExtentsClose;
				CellRect eb = a.rB.ExtentsClose;
				a.rect = CellRect.FromLimits(Math.Min(ea.minX, eb.minX), Math.Min(ea.minZ, eb.minZ), Math.Max(ea.maxX, eb.maxX), Math.Max(ea.maxZ, eb.maxZ))
					.ExpandedBy(1).ClipInsideMap(map);
			}
			a.size = a.rect.Width * a.rect.Height;
			a.side = new byte[a.size];
			a.walk = new bool[a.size];
			a.pg = map.pathing.Normal.pathGrid;
			foreach (IntVec3 c in a.rect)
			{
				int i = Idx(a.rect, c);
				Room r = c.GetRoom(map);
				a.side[i] = r == a.rA ? (byte)1 : (r == a.rB && (a.rB.ProperRoom || a.box.Contains(c))) ? (byte)2 : (byte)0;
				a.walk[i] = a.side[i] != 0 && a.pg.WalkableFast(c);
			}
			foreach (DoorTrip t in list)
			{
				if (!a.rect.Contains(t.start) || !a.rect.Contains(t.end))
				{
					continue;
				}
				int si = Idx(a.rect, t.start);
				int ei = Idx(a.rect, t.end);
				if (a.side[si] == 0 || a.side[ei] == 0 || a.side[si] == a.side[ei] || !a.walk[si] || !a.walk[ei])
				{
					continue;
				}
				long key = (long)si * a.size + ei;
				Add(a.pairs, key);
				a.actual += t.tiles;
				a.count++;
				if (t.norm >= 0)
				{
					Add(a.timedPairs, key);
					a.measured += t.norm;
					a.timedCount++;
				}
				if (a.side[si] == 1)
				{
					a.fromA++;
				}
				Zone z = map.zoneManager.ZoneAt(a.side[si] == 2 ? t.start : t.end);
				if (z != null)
				{
					int zn;
					a.zonesB.TryGetValue(z, out zn);
					a.zonesB[z] = zn + 1;
				}
			}
			return a;
		}

		private static void Add(Dictionary<long, int> d, long key)
		{
			int n;
			d.TryGetValue(key, out n);
			d[key] = n + 1;
		}

		// The move line for one door, or null; records the suggestion (with the door's measured / predicted ratio).
		private static string MoveLine(DoorPathData data, Area a, string period, int now, out int save)
		{
			save = 0;
			if (a.count < MinTrips)
			{
				return null;
			}
			// Where a door could go: a wall cell with room A on one side and room B on the other, straight through.
			HashSet<IntVec3> candidates = new HashSet<IntVec3>();
			foreach (IntVec3 c0 in a.rect)
			{
				if (a.side[Idx(a.rect, c0)] != 1)
				{
					continue;
				}
				foreach (IntVec3 d in GenAdj.CardinalDirections)
				{
					IntVec3 c = c0 + d;
					IntVec3 o = c + d;
					if (c == a.door || !a.rect.Contains(o) || a.side[Idx(a.rect, c)] != 0 || a.side[Idx(a.rect, o)] != 2)
					{
						continue;
					}
					Building edifice = c.GetEdifice(a.map);
					if (edifice == null || edifice.def.building == null || !(edifice.def.building.isPlaceOverableWall || edifice.def.IsSmoothed))
					{
						continue;
					}
					if (!c0.Standable(a.map) || !o.Standable(a.map) || c0.GetFirstBuilding(a.map) != null || o.GetFirstBuilding(a.map) != null)
					{
						continue;
					}
					candidates.Add(c);
				}
			}
			if (candidates.Count == 0)
			{
				return null;
			}

			Field here = Search(a, a.door);
			Result current = Totals(a, here, a.pairs);
			if (!current.ok)
			{
				return null;
			}
			IntVec3 bestCell = IntVec3.Invalid;
			Result best = default(Result);
			foreach (IntVec3 c in candidates)
			{
				Result r = Totals(a, Search(a, c), a.pairs);
				if (r.ok && (!best.ok || r.cost < best.cost))
				{
					best = r;
					bestCell = c;
				}
			}
			if (!best.ok || best.cost >= current.cost)
			{
				return null;
			}
			save = current.tiles - best.tiles;
			if (save < MinSaveTiles || save < current.tiles * MinSaveShare)
			{
				return null;
			}

			// Remembered for the check after the move; a newer suggestion for the same door replaces it.
			data.suggestions.RemoveAll((DoorSuggestion s) => s.mapId == a.map.uniqueID && s.oldDoor == a.door && s.doneTick < 0);
			data.suggestions.Add(new DoorSuggestion
			{
				mapId = a.map.uniqueID, oldDoor = a.door, cellA = a.cellA, cellB = a.cellB, cell = bestCell, tick = now,
				ratio = Ratio(a, here), newDoor = IntVec3.Invalid, doneTick = -1
			});

			// Names: "across A to B" in the way most trips went when both are rooms.
			string nameA = "the " + RoomName(a.rA);
			string nameB;
			if (a.rB.ProperRoom)
			{
				nameB = "the " + RoomName(a.rB);
				if (a.fromA * 2 < a.count)
				{
					string t = nameA;
					nameA = nameB;
					nameB = t;
				}
			}
			else
			{
				nameB = NameOutdoors(a);
			}
			return "[REMINDER] I noticed your pawns spend " + a.actual + " tiles " + period + " moving across " + nameA + " to " + nameB
				+ " (" + a.count + " trips through the door at (" + AgentWorkspotCheck.Xz(a.door) + ")). Placing the door on ("
				+ AgentWorkspotCheck.Xz(bestCell) + ") instead should save about " + save + " tiles " + period + ".";
		}

		// The check after a move: the new door's measured / predicted ratio against the old door's. Null while there are
		// fewer than 5 timed trips through it (the suggestion stays open), or when it is not slower.
		private static string CheckMoved(Area a, DoorSuggestion s, int now)
		{
			if (a.timedCount < MinTrips)
			{
				return null;
			}
			float ratio = Ratio(a, Search(a, a.door));
			s.newDoor = a.door;
			s.doneTick = now;
			if (ratio <= 0f || s.ratio <= 0f || ratio < s.ratio * (1f + SlowerShare))
			{
				return null;
			}
			int pct = (int)Math.Round((ratio / s.ratio - 1f) * 100f);
			string nameB = a.rB.ProperRoom ? "the " + RoomName(a.rB) : NameOutdoors(a);
			return "[REMINDER] The new door at (" + AgentWorkspotCheck.Xz(a.door) + ") between the " + RoomName(a.rA) + " and " + nameB
				+ ": the new path shows " + pct + "% difference (trips through it take " + pct + "% longer than the path should, "
				+ a.timedCount + " trips timed). Maybe you have things slowing pawns down (furniture, rough floor, a slow door)?";
		}

		// Measured ticks / predicted cost over the timed trips through the door FIELD was searched from; -1 if too few.
		private static float Ratio(Area a, Field field)
		{
			if (a.timedCount < MinTrips)
			{
				return -1f;
			}
			Result predicted = Totals(a, field, a.timedPairs);
			return !predicted.ok || predicted.cost <= 0 ? -1f : (float)a.measured / predicted.cost;
		}

		// An outdoor side: the zone most trip ends lie in when it holds more than half of them, else "outdoors".
		private static string NameOutdoors(Area a)
		{
			KeyValuePair<Zone, int> top = a.zonesB.OrderByDescending((KeyValuePair<Zone, int> kv) => kv.Value).FirstOrDefault();
			return top.Key != null && top.Value * 2 > a.count ? top.Key.label + " (outdoors)" : "outdoors";
		}

		// The two rooms a door joins, and a cell in each: the cells north and south of it, else east and west.
		private static bool Sides(Map map, IntVec3 door, out Room a, out Room b, out IntVec3 cellA, out IntVec3 cellB)
		{
			foreach (IntVec3 d in new IntVec3[] { IntVec3.North, IntVec3.East })
			{
				cellA = door + d;
				cellB = door - d;
				a = cellA.InBounds(map) ? cellA.GetRoom(map) : null;
				b = cellB.InBounds(map) ? cellB.GetRoom(map) : null;
				CellRect unused;
				if (Pair(a, b, map, out unused))
				{
					return true;
				}
			}
			a = null;
			b = null;
			cellA = IntVec3.Invalid;
			cellB = IntVec3.Invalid;
			return false;
		}

		// ---- the search -------------------------------------------------------------------------------------------

		private struct Result
		{
			public long cost;
			public int tiles;
			public bool ok;
		}

		// Cheapest cost and its steps from one door cell to every cell of the area.
		private struct Field
		{
			public int[] cost;
			public int[] steps;
		}

		// Dijkstra from DOOR, the only cell open between the two rooms (the old door is side 0, a doorway room, so it is
		// closed unless it is DOOR).
		private static Field Search(Area a, IntVec3 door)
		{
			int w = a.rect.Width;
			int src = Idx(a.rect, door);
			Field f = new Field { cost = new int[a.size], steps = new int[a.size] };
			for (int i = 0; i < a.size; i++)
			{
				f.cost[i] = int.MaxValue;
			}
			f.cost[src] = 0;
			FastPriorityQueue<KeyValuePair<int, int>> queue = new FastPriorityQueue<KeyValuePair<int, int>>(new ByCost());
			queue.Push(new KeyValuePair<int, int>(0, src));
			while (queue.Count > 0)
			{
				KeyValuePair<int, int> top = queue.Pop();
				int i = top.Value;
				if (top.Key > f.cost[i])
				{
					continue;
				}
				int x = a.rect.minX + i % w;
				int z = a.rect.minZ + i / w;
				for (int dx = -1; dx <= 1; dx++)
				{
					for (int dz = -1; dz <= 1; dz++)
					{
						if (dx == 0 && dz == 0)
						{
							continue;
						}
						IntVec3 n = new IntVec3(x + dx, 0, z + dz);
						if (!a.rect.Contains(n))
						{
							continue;
						}
						int ni = Idx(a.rect, n);
						if (!a.walk[ni])
						{
							continue;
						}
						bool diagonal = dx != 0 && dz != 0;
						if (diagonal && (!Open(a, new IntVec3(x + dx, 0, z)) || !Open(a, new IntVec3(x, 0, z + dz))))
						{
							continue;
						}
						int c = top.Key + (diagonal ? DiagonalCost : StraightCost) + Math.Min(a.pg.Cost(n), 1000);
						if (c < f.cost[ni])
						{
							f.cost[ni] = c;
							f.steps[ni] = f.steps[i] + 1;
							queue.Push(new KeyValuePair<int, int>(c, ni));
						}
					}
				}
			}
			return f;
		}

		// Every trip's way start -> door -> end over FIELD, times how many trips had it. Not ok if one can not be routed.
		private static Result Totals(Area a, Field f, Dictionary<long, int> pairs)
		{
			Result r = new Result { ok = true };
			foreach (KeyValuePair<long, int> kv in pairs)
			{
				int si = (int)(kv.Key / a.size);
				int ei = (int)(kv.Key % a.size);
				if (f.cost[si] == int.MaxValue || f.cost[ei] == int.MaxValue)
				{
					r.ok = false;
					return r;
				}
				r.cost += (long)(f.cost[si] + f.cost[ei]) * kv.Value;
				r.tiles += (f.steps[si] + f.steps[ei]) * kv.Value;
			}
			return r;
		}

		private static bool Open(Area a, IntVec3 c)
		{
			return a.rect.Contains(c) && a.walk[Idx(a.rect, c)];
		}

		private static int Idx(CellRect rect, IntVec3 c)
		{
			return (c.z - rect.minZ) * rect.Width + (c.x - rect.minX);
		}

		private class ByCost : IComparer<KeyValuePair<int, int>>
		{
			public int Compare(KeyValuePair<int, int> a, KeyValuePair<int, int> b)
			{
				return a.Key.CompareTo(b.Key);
			}
		}

		private static string RoomName(Room room)
		{
			string name = VocabularyText.RoomLabel(room);
			return string.IsNullOrEmpty(name) ? "room" : name;
		}
	}

	// One crossing (AgentDoorPathScan): the door, where the walk came into the first room and left the second, the
	// steps, the measured ticks scaled to a 13-tick colonist (-1 = not timed), and when.
	public class DoorTrip
	{
		public int mapId;
		public IntVec3 door;
		public IntVec3 start;
		public IntVec3 end;
		public int tiles;
		public int norm;
		public int tick;

		public string Pack()
		{
			return string.Join(" ", new int[] { mapId, door.x, door.z, start.x, start.z, end.x, end.z, tiles, norm, tick }
				.Select((int v) => v.ToString(CultureInfo.InvariantCulture)).ToArray());
		}

		public static DoorTrip Unpack(string s)
		{
			int[] v = DoorPathData.Ints(s, 10);
			if (v == null)
			{
				return null;
			}
			return new DoorTrip
			{
				mapId = v[0], door = new IntVec3(v[1], 0, v[2]), start = new IntVec3(v[3], 0, v[4]), end = new IntVec3(v[5], 0, v[6]),
				tiles = v[7], norm = v[8], tick = v[9]
			};
		}
	}

	// A door move the reminder suggested: the old door, a cell in each of its two rooms, the suggested cell, when, the
	// old door's measured / predicted ratio (-1 = too few timed trips), and once a door between the same rooms is
	// found with the old one gone: that door and when it was checked.
	public class DoorSuggestion
	{
		public int mapId;
		public IntVec3 oldDoor;
		public IntVec3 cellA;
		public IntVec3 cellB;
		public IntVec3 cell;
		public int tick;
		public float ratio;
		public IntVec3 newDoor;
		public int doneTick;

		public string Pack()
		{
			return string.Join(" ", new int[] { mapId, oldDoor.x, oldDoor.z, cellA.x, cellA.z, cellB.x, cellB.z, cell.x, cell.z, tick,
				newDoor.x, newDoor.z, doneTick }.Select((int v) => v.ToString(CultureInfo.InvariantCulture)).ToArray())
				+ " " + ratio.ToString("R", CultureInfo.InvariantCulture);
		}

		public static DoorSuggestion Unpack(string s)
		{
			string[] parts = (s ?? "").Split(' ');
			float ratio;
			if (parts.Length != 14 || !float.TryParse(parts[13], NumberStyles.Float, CultureInfo.InvariantCulture, out ratio))
			{
				return null;
			}
			int[] v = DoorPathData.Ints(string.Join(" ", parts, 0, 13), 13);
			if (v == null)
			{
				return null;
			}
			return new DoorSuggestion
			{
				mapId = v[0], oldDoor = new IntVec3(v[1], 0, v[2]), cellA = new IntVec3(v[3], 0, v[4]), cellB = new IntVec3(v[5], 0, v[6]),
				cell = new IntVec3(v[7], 0, v[8]), tick = v[9], newDoor = new IntVec3(v[10], 0, v[11]), doneTick = v[12], ratio = ratio
			};
		}
	}

	// The door path record, kept in the save (GameComponent_VerbalCommands, node "vcDoorPath"). Each trip and
	// suggestion is one line of numbers, so a day of trips stays small in the save file.
	public class DoorPathData : IExposable
	{
		public List<DoorTrip> trips = new List<DoorTrip>();
		public List<DoorSuggestion> suggestions = new List<DoorSuggestion>();
		public int trackingSince = -1;

		private List<string> tripLines;
		private List<string> suggestionLines;

		public void ExposeData()
		{
			if (Scribe.mode == LoadSaveMode.Saving)
			{
				tripLines = trips.Select((DoorTrip t) => t.Pack()).ToList();
				suggestionLines = suggestions.Select((DoorSuggestion s) => s.Pack()).ToList();
			}
			Scribe_Values.Look(ref trackingSince, "trackingSince", -1);
			Scribe_Collections.Look(ref tripLines, "trips", LookMode.Value);
			Scribe_Collections.Look(ref suggestionLines, "suggestions", LookMode.Value);
			if (Scribe.mode == LoadSaveMode.LoadingVars)
			{
				trips = (tripLines ?? new List<string>()).Select(DoorTrip.Unpack).Where((DoorTrip t) => t != null).ToList();
				suggestions = (suggestionLines ?? new List<string>()).Select(DoorSuggestion.Unpack).Where((DoorSuggestion s) => s != null).ToList();
			}
			if (Scribe.mode == LoadSaveMode.Saving || Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				tripLines = null;
				suggestionLines = null;
			}
		}

		// COUNT whole numbers separated by spaces, or null.
		internal static int[] Ints(string s, int count)
		{
			string[] parts = (s ?? "").Split(' ');
			if (parts.Length != count)
			{
				return null;
			}
			int[] v = new int[count];
			for (int i = 0; i < count; i++)
			{
				if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]))
				{
					return null;
				}
			}
			return v;
		}
	}
}
