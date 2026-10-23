using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Room-with-a-door check for build plans (user, 2026-10-14: "do same for doors. condition: an agent is trying to make
	// blueprint a room with door (so it encloses them). if condition is found: scan surrounding (use the same vent logic)
	// to see if there is a room across said being-built room. if found: remidner fires "you have a room across the room
	// you are trying to build. really build room with door? can lead to suboptimal pathfinding and may cause issues.""),
	// the sibling of AgentWorkspotCheck: a NOTE, never a Fault; it makes the plan wait for confirm (Plan.askConfirm),
	// even under raw apply.
	//
	// When: the order puts down a door (a room form's door, "build door at", a build option with walls and a door ...).
	// The room is simulated from that door, the base read as it WILL be: a cell bounds a room when it holds a door, or
	// holds something that fills the cell and is not walkable (walls, rock, vents), built or as a blueprint / frame, or
	// gets one in this order; this order's mine cells (dig room) count as open. From each side of the door a flood fill
	// over cardinal neighbours (rooms are cardinal: districts of regions) finds the enclosed cells; a fill that reaches
	// the map edge or passes MaxCells is no room. An enclosure that is already a built room cell for cell (a door put
	// into an existing room) is not a room "being built" and is skipped.
	// Across (the vent logic of AgentRoomTempScan): an enclosure cell, a wall-like cell next to it (not a door), and past
	// that wall an open cell of a different, existing proper room (built now; rooms that are only blueprints are not
	// seen). A room the new walls cut out of (it holds enclosure cells today) is not "across".
	//
	// Game facts relied on (decompiled tree sources\RimWorldDecompiled\):
	//   What bounds a room: a door cell is a Portal region; a cell not walkable with a Fillage.Full thing is no region
	//       (RegionType.None); other impassable cells join a room           Verse\RegionTypeUtility.cs:17-44
	//   ProperRoom: not touching the map edge, has a Normal district          Verse\Room.cs:536-553
	internal static class AgentDoorCheck
	{
		private const int MaxCells = 600;
		private const int MaxNamed = 4;

		private static readonly IntVec3[] Cardinals = new IntVec3[] { IntVec3.North, IntVec3.East, IntVec3.South, IntVec3.West };

		// ---- entry points ---------------------------------------------------------------------------------

		internal static void Note(Plan plan, Map map, ThingDef def, IntVec3 cell, Rot4 rot)
		{
			Note(plan, map, new List<AgentWindCheck.Placed> { new AgentWindCheck.Placed(def, cell, rot) }, null);
		}

		internal static void Note(Plan plan, Map map, ThingDef def, IEnumerable<IntVec3> cells, Rot4 rot)
		{
			Note(plan, map, cells.Select((IntVec3 c) => new AgentWindCheck.Placed(def, c, rot)).ToList(), null);
		}

		internal static void Note(Plan plan, Map map, List<AgentWindCheck.Placed> placed)
		{
			Note(plan, map, placed, null);
		}

		// The room forms (build room new, dig room, furnished room): walls, the door, and the cells mined open.
		internal static void NoteShell(Plan plan, Map map, RoomShell shell)
		{
			if (shell == null || !shell.placeDoor || !shell.door.IsValid)
			{
				return;
			}
			List<AgentWindCheck.Placed> placed = new List<AgentWindCheck.Placed>();
			foreach (IntVec3 c in shell.wallCells)
			{
				placed.Add(new AgentWindCheck.Placed(ThingDefOf.Wall, c, ThingDefOf.Wall.defaultPlacingRot));
			}
			placed.Add(new AgentWindCheck.Placed(ThingDefOf.Door, shell.door, ThingDefOf.Door.defaultPlacingRot));
			HashSet<IntVec3> open = new HashSet<IntVec3>(shell.mineCells);
			// The fogged cells a dig room marks count as dug, not as whatever is under the fog (2026-10-21).
			open.UnionWith(shell.fogMineCells);
			if (shell.doorInRock)
			{
				open.Add(shell.door);
			}
			Note(plan, map, placed, open);
		}

		private static void Note(Plan plan, Map map, List<AgentWindCheck.Placed> placed, HashSet<IntVec3> open)
		{
			try
			{
				if (plan == null || map == null || placed == null)
				{
					return;
				}
				List<IntVec3> doors = new List<IntVec3>();
				HashSet<IntVec3> planDoors = new HashSet<IntVec3>();
				HashSet<IntVec3> planWalls = new HashSet<IntVec3>();
				foreach (AgentWindCheck.Placed p in placed)
				{
					if (p.def == null)
					{
						continue;
					}
					foreach (IntVec3 c in GenAdj.OccupiedRect(p.cell, p.rot, p.def.Size))
					{
						if (p.def.IsDoor)
						{
							planDoors.Add(c);
						}
						else if (FillsCell(p.def))
						{
							planWalls.Add(c);
						}
					}
					if (p.def.IsDoor)
					{
						doors.Add(p.cell);
					}
				}
				if (doors.Count == 0)
				{
					return;
				}
				Sim sim = new Sim(map, planWalls, planDoors, open ?? new HashSet<IntVec3>());

				// Each door: the enclosures on its sides that are rooms being built.
				List<HashSet<IntVec3>> rooms = new List<HashSet<IntVec3>>();
				foreach (IntVec3 door in doors)
				{
					foreach (IntVec3 d in Cardinals)
					{
						IntVec3 side = door + d;
						if (!side.InBounds(map) || sim.Bounds(side) || rooms.Any((HashSet<IntVec3> r) => r.Contains(side)))
						{
							continue;
						}
						HashSet<IntVec3> cells = sim.Enclosure(side);
						if (cells == null || AlreadyBuilt(map, cells))
						{
							continue;
						}
						rooms.Add(cells);
					}
				}
				if (rooms.Count == 0)
				{
					return;
				}

				// Existing rooms across a wall of a room being built.
				Dictionary<Room, IntVec3> across = new Dictionary<Room, IntVec3>();
				foreach (HashSet<IntVec3> cells in rooms)
				{
					HashSet<Room> cutFrom = new HashSet<Room>();
					foreach (IntVec3 c in cells)
					{
						Room now = c.GetRoom(map);
						if (now != null)
						{
							cutFrom.Add(now);
						}
					}
					foreach (IntVec3 c in cells)
					{
						foreach (IntVec3 d in Cardinals)
						{
							IntVec3 w = c + d;
							IntVec3 o = w + d;
							if (!o.InBounds(map) || cells.Contains(w) || !sim.Bounds(w) || sim.IsDoor(w) || cells.Contains(o) || sim.Bounds(o))
							{
								continue;
							}
							Room other = o.GetRoom(map);
							if (other == null || !other.ProperRoom || cutFrom.Contains(other) || across.ContainsKey(other))
							{
								continue;
							}
							across[other] = w;
						}
					}
				}
				if (across.Count == 0)
				{
					return;
				}

				List<string> parts = new List<string>();
				foreach (KeyValuePair<Room, IntVec3> kv in across.Take(MaxNamed))
				{
					string part = "the " + Name(kv.Key) + " across the wall at " + AgentWorkspotCheck.Xz(kv.Value);
					foreach (IntVec3 door in doors)
					{
						if (Cardinals.Any((IntVec3 d) => (door + d).InBounds(map) && (door + d).GetRoom(map) == kv.Key))
						{
							part += " (the door at " + AgentWorkspotCheck.Xz(door) + " opens into it)";
							break;
						}
					}
					parts.Add(part);
				}
				if (across.Count > MaxNamed)
				{
					parts.Add((across.Count - MaxNamed) + " more");
				}
				plan.notes.Add("DOOR: you have a room across the room you are trying to build (" + string.Join("; ", parts.ToArray())
					+ "). Really build the room with a door? It can lead to suboptimal pathfinding and may cause issues. Say apply to build it anyway, or cancel.");
				plan.askConfirm = true;
				plan.confirmBy = "Door check";   // the harness log (HarnessLog.cs, H4)
			}
			catch (Exception ex)
			{
				// A check that only informs must never stop a plan.
				Log.Warning("[VerbalCommands] door check skipped: " + ex.Message);
			}
		}

		// ---- the simulated base -------------------------------------------------------------------------------

		private sealed class Sim
		{
			private readonly Map map;
			private readonly HashSet<IntVec3> planWalls, planDoors, open;

			public Sim(Map map, HashSet<IntVec3> planWalls, HashSet<IntVec3> planDoors, HashSet<IntVec3> open)
			{
				this.map = map;
				this.planWalls = planWalls;
				this.planDoors = planDoors;
				this.open = open;
			}

			// A door now or once the plans are built.
			public bool IsDoor(IntVec3 c)
			{
				if (planDoors.Contains(c) || c.GetDoor(map) != null)
				{
					return true;
				}
				foreach (Thing t in c.GetThingList(map))
				{
					ThingDef built = Future(t);
					if (built != null && built.IsDoor)
					{
						return true;
					}
				}
				return false;
			}

			// The cell bounds a room once the plans are built (RegionTypeUtility.cs:17-44).
			public bool Bounds(IntVec3 c)
			{
				if (planWalls.Contains(c) || IsDoor(c))
				{
					return true;
				}
				if (open.Contains(c))
				{
					return false;
				}
				if (!c.WalkableByNormal(map))
				{
					foreach (Thing t in c.GetThingList(map))
					{
						if (t.def.Fillage == FillCategory.Full)
						{
							return true;
						}
					}
				}
				foreach (Thing t in c.GetThingList(map))
				{
					ThingDef built = Future(t);
					if (built != null && FillsCell(built))
					{
						return true;
					}
				}
				return false;
			}

			// The cells enclosed with START, or null when the fill reaches the map edge or passes MaxCells.
			public HashSet<IntVec3> Enclosure(IntVec3 start)
			{
				HashSet<IntVec3> seen = new HashSet<IntVec3> { start };
				Queue<IntVec3> queue = new Queue<IntVec3>();
				queue.Enqueue(start);
				while (queue.Count > 0)
				{
					IntVec3 c = queue.Dequeue();
					if (c.x <= 0 || c.z <= 0 || c.x >= map.Size.x - 1 || c.z >= map.Size.z - 1)
					{
						return null;   // touches the map edge: not a proper room (Room.cs:540)
					}
					foreach (IntVec3 d in Cardinals)
					{
						IntVec3 n = c + d;
						if (seen.Contains(n) || !n.InBounds(map) || Bounds(n))
						{
							continue;
						}
						seen.Add(n);
						if (seen.Count > MaxCells)
						{
							return null;
						}
						queue.Enqueue(n);
					}
				}
				return seen;
			}

			// What a blueprint or frame will become; null for anything else.
			private static ThingDef Future(Thing t)
			{
				if ((t.def.IsBlueprint || t.def.IsFrame) && t.Faction == Faction.OfPlayer)
				{
					return t.def.entityDefToBuild as ThingDef;
				}
				return null;
			}
		}

		// A building that bounds a room once it stands: fills the cell and cannot be walked (walls, vents ...).
		private static bool FillsCell(ThingDef def)
		{
			return def != null && def.Fillage == FillCategory.Full && def.passability == Traversability.Impassable;
		}

		// CELLS is a built room as it is: one existing proper room with exactly these cells.
		private static bool AlreadyBuilt(Map map, HashSet<IntVec3> cells)
		{
			Room room = cells.First().GetRoom(map);
			return room != null && room.ProperRoom && room.CellCount == cells.Count && cells.All((IntVec3 c) => c.GetRoom(map) == room);
		}

		private static string Name(Room room)
		{
			string name = VocabularyText.RoomLabel(room);
			return string.IsNullOrEmpty(name) ? "room" : name;
		}
	}
}
