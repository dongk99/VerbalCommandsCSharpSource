using System.Collections.Generic;
using System.Globalization;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Room wall logic (user, 2026-10-16), run by AgentBuildShapes.PlanShell for every room an agent builds:
	//   "draws bounding box around an already placed room, 1 tile away from each wall, whenever blueprint is placed. If
	//   there is wall overlapping it, Force-fit blueprint by moving it so wall blueprint would overlap onto already placed
	//   room. Exception: corners, so, max 2 tiles allowed."
	//   "placing new room blueprint is blocked if there is 1 room that is unfinished (walls, doors, anything interior
	//   being built). a room is considered a room (in this script) of anything INSIDE tile of walls/vents/hvac/doors."
	//   "After blueprint is overlapped, a door blueprint is created between two corner."
	//   "you can overlap an hvac, vent, door, another wall into existing walls."
	// A wall here is anything that stands in a wall line: building.isWall, a door, or a building that can be placed over
	// a wall (building.canPlaceOverWall: doors, vent, cooler; GenConstruct.cs:571), built or a player blueprint/frame.
	// Natural rock also closes a room. A room is a set of cells closed by those, not touching fog or the map edge, at most
	// RoomMax-2 cells each way of inside. MAIN THREAD ONLY.
	internal sealed class SharedWall
	{
		public CellRect room;                       // the existing room, walls included
		public List<IntVec3> cells = new List<IntVec3>();   // the shared wall, its two corners included
		public IntVec3 door = IntVec3.Invalid;      // where the new door goes (between the corners), or Invalid
		public IntVec3 existingDoor = IntVec3.Invalid;
		public bool used;                           // the new room's own door was put there
	}

	internal sealed class RoomFit
	{
		public CellRect rect;                       // the room after the force-fit
		public int dx, dz;                          // the shift
		public CellRect shiftedTo;                  // the room it was moved onto (valid when dx or dz != 0)
		public List<SharedWall> walls = new List<SharedWall>();

		public bool IsSharedDoor(IntVec3 cell)
		{
			foreach (SharedWall w in walls)
			{
				if (w.used && w.door == cell)
				{
					return true;
				}
			}
			return false;
		}

		// The room's own door cell when it falls on a shared wall: that wall's existing door, or its new door.
		public IntVec3 DoorFor(IntVec3 door)
		{
			foreach (SharedWall w in walls)
			{
				if (!w.cells.Contains(door))
				{
					continue;
				}
				if (w.existingDoor.IsValid)
				{
					return w.existingDoor;
				}
				if (w.door.IsValid)
				{
					w.used = true;
					return w.door;
				}
			}
			return door;
		}
	}

	internal static class AgentRoomFit
	{
		private const int MaxInside = 28;           // AgentBuildShapes.RoomMax 30, walls excluded
		private const int CornerCells = 2;          // "Exception: corners, so, max 2 tiles allowed."

		private sealed class FoundRoom
		{
			public HashSet<IntVec3> cells = new HashSet<IntVec3>();
			public CellRect outer;
			public int planned;
		}

		// ---- the block -------------------------------------------------------------------------------

		// Fault when a room on the map is still being built: a player blueprint or frame in its walls or inside.
		internal static void RefuseWhileUnfinished(Map map)
		{
			HashSet<IntVec3> outside = new HashSet<IntVec3>();
			HashSet<IntVec3> seen = new HashSet<IntVec3>();
			List<Thing> planned = new List<Thing>();
			planned.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint));
			planned.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame));
			foreach (Thing t in planned)
			{
				if (t.Faction != Faction.OfPlayer || !t.Spawned)
				{
					continue;
				}
				List<IntVec3> seeds = new List<IntVec3>();
				bool plannedWall;
				if (WallAt(map, t.Position, out plannedWall))
				{
					for (int i = 0; i < 4; i++)
					{
						seeds.Add(t.Position + GenAdj.CardinalDirections[i]);
					}
				}
				else
				{
					seeds.Add(t.Position);
				}
				foreach (IntVec3 seed in seeds)
				{
					FoundRoom room = Flood(map, seed, outside, seen);
					if (room != null && room.planned > 0)
					{
						throw new Fault("A room is still being built at " + Corners(room.outer) + " (" + room.planned
							+ (room.planned == 1 ? " blueprint or frame" : " blueprints or frames") + " left); finish it before planning another room.");
					}
				}
			}
		}

		// ---- the force-fit ---------------------------------------------------------------------------

		internal static RoomFit Fit(Map map, CellRect rect)
		{
			List<FoundRoom> rooms = RoomsNear(map, rect);
			RoomFit fit = new RoomFit();
			int bestX = 0, bestZ = 0;
			foreach (FoundRoom r in rooms)
			{
				CellRect ring = r.outer.ExpandedBy(1);
				int touching = 0;
				foreach (IntVec3 c in rect.EdgeCells)
				{
					if (OnEdge(ring, c))
					{
						touching++;
					}
				}
				if (touching <= CornerCells)
				{
					continue;
				}
				// The new room's wall lies along one side of the ring, parallel to the room's wall: move it one cell onto it.
				int zOver = Overlap(rect.minZ, rect.maxZ, ring.minZ, ring.maxZ);
				int xOver = Overlap(rect.minX, rect.maxX, ring.minX, ring.maxX);
				if (rect.maxX == ring.minX && zOver > bestX)
				{
					bestX = zOver;
					fit.dx = 1;
					fit.shiftedTo = r.outer;
				}
				else if (rect.minX == ring.maxX && zOver > bestX)
				{
					bestX = zOver;
					fit.dx = -1;
					fit.shiftedTo = r.outer;
				}
				if (rect.maxZ == ring.minZ && xOver > bestZ)
				{
					bestZ = xOver;
					fit.dz = 1;
					fit.shiftedTo = r.outer;
				}
				else if (rect.minZ == ring.maxZ && xOver > bestZ)
				{
					bestZ = xOver;
					fit.dz = -1;
					fit.shiftedTo = r.outer;
				}
			}
			fit.rect = rect.MovedBy(new IntVec2(fit.dx, fit.dz));
			if (fit.dx != 0 || fit.dz != 0)
			{
				rooms = RoomsNear(map, fit.rect);
			}
			foreach (FoundRoom r in rooms)
			{
				SharedWall w = Shared(map, fit.rect, r);
				if (w != null)
				{
					fit.walls.Add(w);
				}
			}
			return fit;
		}

		// The wall the new room shares with an existing room, with the door cell between its two corners.
		private static SharedWall Shared(Map map, CellRect n, FoundRoom r)
		{
			CellRect o = r.outer;
			SharedWall w = new SharedWall();
			w.room = o;
			bool alongZ;
			int fixedAt;
			int lo, hi;
			if (n.maxX == o.minX || n.minX == o.maxX)
			{
				alongZ = true;
				fixedAt = n.maxX == o.minX ? o.minX : o.maxX;
				lo = System.Math.Max(n.minZ, o.minZ);
				hi = System.Math.Min(n.maxZ, o.maxZ);
			}
			else if (n.maxZ == o.minZ || n.minZ == o.maxZ)
			{
				alongZ = false;
				fixedAt = n.maxZ == o.minZ ? o.minZ : o.maxZ;
				lo = System.Math.Max(n.minX, o.minX);
				hi = System.Math.Min(n.maxX, o.maxX);
			}
			else
			{
				return null;
			}
			if (hi < lo)
			{
				return null;
			}
			for (int i = lo; i <= hi; i++)
			{
				IntVec3 c = alongZ ? new IntVec3(fixedAt, 0, i) : new IntVec3(i, 0, fixedAt);
				w.cells.Add(c);
				if (!w.existingDoor.IsValid && DoorAt(map, c))
				{
					w.existingDoor = c;
				}
			}
			if (w.existingDoor.IsValid || hi - lo < 2)
			{
				return w;
			}
			// Between the two corners, nearest the middle first; a cell holding a plain wall, open on both sides.
			int mid = lo + (hi - lo) / 2;
			for (int d = 0; d <= hi - lo; d++)
			{
				foreach (int i in new[] { mid - d, mid + d })
				{
					if (i <= lo || i >= hi || w.door.IsValid)
					{
						continue;
					}
					IntVec3 c = alongZ ? new IntVec3(fixedAt, 0, i) : new IntVec3(i, 0, fixedAt);
					IntVec3 a = alongZ ? c + IntVec3.East : c + IntVec3.North;
					IntVec3 b = alongZ ? c + IntVec3.West : c + IntVec3.South;
					IntVec3 inOld = r.cells.Contains(a) ? a : b;
					IntVec3 inNew = inOld == a ? b : a;
					if (PlainWallAt(map, c) && r.cells.Contains(inOld) && inOld.Standable(map) && n.ContractedBy(1).Contains(inNew))
					{
						w.door = c;
					}
				}
			}
			return w;
		}

		private static List<FoundRoom> RoomsNear(Map map, CellRect rect)
		{
			List<FoundRoom> rooms = new List<FoundRoom>();
			HashSet<IntVec3> outside = new HashSet<IntVec3>();
			HashSet<IntVec3> seen = new HashSet<IntVec3>();
			CellRect inside = rect.ContractedBy(1);
			foreach (IntVec3 c in rect.ExpandedBy(2).ClipInsideMap(map).Cells)
			{
				bool planned;
				if (!WallAt(map, c, out planned))
				{
					continue;
				}
				for (int i = 0; i < 4; i++)
				{
					FoundRoom r = Flood(map, c + GenAdj.CardinalDirections[i], outside, seen);
					if (r != null && !r.outer.Overlaps(inside))
					{
						rooms.Add(r);
					}
				}
			}
			return rooms;
		}

		// ---- what closes a room ----------------------------------------------------------------------

		// The cells closed in from start; null when it is no room (reaches fog, the map edge or is too big) or was seen.
		private static FoundRoom Flood(Map map, IntVec3 start, HashSet<IntVec3> outside, HashSet<IntVec3> seen)
		{
			if (!start.InBounds(map) || outside.Contains(start) || seen.Contains(start) || Closes(map, start))
			{
				return null;
			}
			FoundRoom room = new FoundRoom();
			room.cells.Add(start);
			Queue<IntVec3> queue = new Queue<IntVec3>();
			queue.Enqueue(start);
			int minX = start.x, maxX = start.x, minZ = start.z, maxZ = start.z;
			bool open = false;
			while (queue.Count > 0 && !open)
			{
				IntVec3 c = queue.Dequeue();
				if (!AgentPlace.Visible(map, c) || outside.Contains(c) || c.x == 0 || c.z == 0 || c.x == map.Size.x - 1 || c.z == map.Size.z - 1)
				{
					open = true;
					break;
				}
				minX = System.Math.Min(minX, c.x);
				maxX = System.Math.Max(maxX, c.x);
				minZ = System.Math.Min(minZ, c.z);
				maxZ = System.Math.Max(maxZ, c.z);
				if (maxX - minX + 1 > MaxInside || maxZ - minZ + 1 > MaxInside)
				{
					open = true;
					break;
				}
				for (int i = 0; i < 4; i++)
				{
					IntVec3 n = c + GenAdj.CardinalDirections[i];
					if (room.cells.Contains(n) || !n.InBounds(map) || Closes(map, n))
					{
						continue;
					}
					room.cells.Add(n);
					queue.Enqueue(n);
				}
			}
			if (open)
			{
				outside.UnionWith(room.cells);
				return null;
			}
			seen.UnionWith(room.cells);
			room.outer = CellRect.FromLimits(minX, minZ, maxX, maxZ).ExpandedBy(1);
			HashSet<IntVec3> ring = new HashSet<IntVec3>();
			foreach (IntVec3 c in room.cells)
			{
				if (PlannedHere(map, c))
				{
					room.planned++;
				}
				for (int i = 0; i < 8; i++)
				{
					IntVec3 n = c + GenAdj.AdjacentCells[i];
					bool planned;
					if (!room.cells.Contains(n) && n.InBounds(map) && ring.Add(n) && WallAt(map, n, out planned) && planned)
					{
						room.planned++;
					}
				}
			}
			return room;
		}

		private static bool Closes(Map map, IntVec3 c)
		{
			bool planned;
			if (WallAt(map, c, out planned))
			{
				return true;
			}
			Building edifice = c.GetEdifice(map);
			return edifice != null && edifice.def.building != null && edifice.def.building.isNaturalRock;
		}

		private static bool WallDef(ThingDef d)
		{
			return d != null && d.building != null && !d.building.isNaturalRock
				&& (d.IsWall || d.IsDoor || d.building.canPlaceOverWall);
		}

		// A wall, door, vent or cooler stands on the cell, built (any faction) or as a player blueprint or frame.
		private static bool WallAt(Map map, IntVec3 c, out bool planned)
		{
			planned = false;
			List<Thing> things = c.GetThingList(map);
			bool built = false;
			for (int i = 0; i < things.Count; i++)
			{
				Thing t = things[i];
				ThingDef d = t.def;
				if (d.IsBlueprint || d.IsFrame)
				{
					if (t.Faction == Faction.OfPlayer && WallDef(d.entityDefToBuild as ThingDef))
					{
						planned = true;
					}
				}
				else if (d.category == ThingCategory.Building && WallDef(d))
				{
					built = true;
				}
			}
			return built || planned;
		}

		// A player blueprint or frame of anything that is not part of the walls.
		private static bool PlannedHere(Map map, IntVec3 c)
		{
			List<Thing> things = c.GetThingList(map);
			for (int i = 0; i < things.Count; i++)
			{
				if ((things[i].def.IsBlueprint || things[i].def.IsFrame) && things[i].Faction == Faction.OfPlayer)
				{
					return true;
				}
			}
			return false;
		}

		private static bool DoorAt(Map map, IntVec3 c)
		{
			List<Thing> things = c.GetThingList(map);
			for (int i = 0; i < things.Count; i++)
			{
				ThingDef d = things[i].def;
				if (d.IsBlueprint || d.IsFrame)
				{
					if (things[i].Faction == Faction.OfPlayer && d.entityDefToBuild is ThingDef b && b.IsDoor)
					{
						return true;
					}
				}
				else if (d.IsDoor)
				{
					return true;
				}
			}
			return false;
		}

		private static bool PlainWallAt(Map map, IntVec3 c)
		{
			Building edifice = c.GetEdifice(map);
			return edifice != null && edifice.def.IsWall && !edifice.def.IsDoor && !PlannedHere(map, c);
		}

		// ---- small helpers ---------------------------------------------------------------------------

		private static bool OnEdge(CellRect r, IntVec3 c)
		{
			return r.Contains(c) && (c.x == r.minX || c.x == r.maxX || c.z == r.minZ || c.z == r.maxZ);
		}

		private static int Overlap(int aLo, int aHi, int bLo, int bHi)
		{
			return System.Math.Max(0, System.Math.Min(aHi, bHi) - System.Math.Max(aLo, bLo) + 1);
		}

		internal static string Xz(IntVec3 c)
		{
			return c.x.ToString(CultureInfo.InvariantCulture) + ", " + c.z.ToString(CultureInfo.InvariantCulture);
		}

		internal static string Corners(CellRect rect)
		{
			return Xz(new IntVec3(rect.minX, 0, rect.minZ)) + " to " + Xz(new IntVec3(rect.maxX, 0, rect.maxZ));
		}
	}
}
