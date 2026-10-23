using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Outdoor battery rule (user, 2026-10-16: "Block agents from building outdoor battery. outputs: "Do you like having a
	// house fire? have you ever seen an EE put a battery outdoor? Sorry, our colony is OSHA-compliant. deleting
	// blueprint."" / "Maybe also have it scan for places they can build a battery INDOOR in ANY of the existing area,
	// THEN put a blueprint there instead.").
	//
	// Why outdoors is the danger: rain picks a random colony building that can catch an electrical fire, and when its
	// cell has no roof it short-circuits (SteadyEnvironmentEffects.RollForRainFire, RimWorld\SteadyEnvironmentEffects.cs:
	// 350-358); a battery holding more than 100 Wd explodes in flame (ShortCircuitUtility.TryShortCircuitInRain,
	// RimWorld\ShortCircuitUtility.cs:90-105).
	//
	// The agents' build paths only (AgentBuild.PlaceNow, BuildTools.PlaceOne), while the agent inbox is on. A battery
	// (a ThingDef with CompPowerBattery) is outdoors when a cell under it has no roof and is not marked "build roof"
	// (a marked cell gets its roof, as the game's auto-roof marks an enclosed room's cells). Then the blueprint goes to
	// the nearest indoor spot instead: a cell of a proper room (not a prison cell), every cell under the battery
	// roofed, the game's own placement check (GenConstruct.CanPlaceBlueprintAt), nothing built or planned there
	// (BuildDefs.FreeOfBuildings), not fogged, no cell next to a door; a spot against a wall first, the asked rotation
	// first. No such spot: nothing is placed. Either way the user's line goes to the agent with the next result; after
	// a move, a second line with the new spot.
	internal static class BatteryOutdoorRule
	{
		// The user's wording, verbatim.
		private const string Line = "Do you like having a house fire? have you ever seen an EE put a battery outdoor? Sorry, our colony is OSHA-compliant. deleting blueprint.";

		private static List<string> messages = new List<string>();

		public static List<string> TakeMessages()
		{
			if (messages.Count == 0)
			{
				return new List<string>();
			}
			List<string> lines = messages;
			messages = new List<string>();
			return lines;
		}

		// For the agents' build paths: each line of NOTE goes out with the next result, once per result.
		internal static void Tell(string note)
		{
			if (string.IsNullOrEmpty(note))
			{
				return;
			}
			foreach (string line in note.Split('\n'))
			{
				if (!messages.Contains(line))
				{
					messages.Add(line);
				}
			}
		}

		internal static bool IsBattery(BuildableDef def)
		{
			ThingDef td = def as ThingDef;
			return td != null && td.HasComp(typeof(CompPowerBattery));
		}

		// Main thread, before a blueprint is placed. False: place as asked. True: the battery was outdoors; NOTE is the
		// line for the agent, and MOVED is true with CELL and ROT changed to the indoor spot, or false (place nothing).
		internal static bool Check(Map map, BuildableDef def, ThingDef stuff, ref IntVec3 cell, ref Rot4 rot, out bool moved, out string note)
		{
			moved = false;
			note = null;
			if (!AgentInbox.Enabled || map == null || !IsBattery(def) || !Outdoors(map, def, cell, rot))
			{
				return false;
			}
			note = Line;
			IntVec3 asked = cell;
			IntVec3 spotCell;
			Rot4 spotRot;
			if (FindIndoor(map, def, stuff, cell, rot, out spotCell, out spotRot))
			{
				cell = spotCell;
				rot = spotRot;
				moved = true;
				// The second line (user, 2026-10-16: "Ill take latter.").
				note = Line + "\n" + "Battery blueprint placed at (" + AgentWorkspotCheck.Xz(cell) + ") instead.";
			}
			// Harness log (HarnessLog.cs, H1).
			HarnessLog.Add("Battery", AgentWorkspotCheck.Xz(asked) + " -> " + (moved ? AgentWorkspotCheck.Xz(cell) : "not placed"));
			return true;
		}

		private static bool Outdoors(Map map, BuildableDef def, IntVec3 cell, Rot4 rot)
		{
			foreach (IntVec3 c in GenAdj.OccupiedRect(cell, rot, def.Size))
			{
				if (c.InBounds(map) && !map.roofGrid.Roofed(c) && !map.areaManager.BuildRoof[c])
				{
					return true;
				}
			}
			return false;
		}

		// The nearest indoor spot to FROM (see the header), or false.
		private static bool FindIndoor(Map map, BuildableDef def, ThingDef stuff, IntVec3 from, Rot4 asked, out IntVec3 spot, out Rot4 spotRot)
		{
			spot = IntVec3.Invalid;
			spotRot = asked;
			List<IntVec3> cells = new List<IntVec3>();
			foreach (Room room in map.regionGrid.AllRooms)
			{
				if (room == null || !Indoor(room))
				{
					continue;
				}
				cells.AddRange(room.Cells);
			}
			cells = cells.OrderBy((IntVec3 c) => c.DistanceToSquared(from)).ToList();
			List<Rot4> rots = new List<Rot4> { asked };
			rots.AddRange(new Rot4[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West }.Where((Rot4 r) => r != asked));
			// First pass: against a wall; second: anywhere indoors.
			for (int pass = 0; pass < 2; pass++)
			{
				foreach (IntVec3 c in cells)
				{
					foreach (Rot4 r in rots)
					{
						if (Fits(map, def, stuff, c, r) && (pass == 1 || AgainstWall(map, def, c, r)))
						{
							spot = c;
							spotRot = r;
							return true;
						}
					}
				}
			}
			return false;
		}

		private static bool Indoor(Room room)
		{
			return room.ProperRoom && !room.IsPrisonCell && !room.IsDoorway && !room.PsychologicallyOutdoors;
		}

		private static bool Fits(Map map, BuildableDef def, ThingDef stuff, IntVec3 cell, Rot4 rot)
		{
			foreach (IntVec3 c in GenAdj.OccupiedRect(cell, rot, def.Size))
			{
				if (!c.InBounds(map) || !AgentPlace.Visible(map, c) || !map.roofGrid.Roofed(c) || !BuildDefs.FreeOfBuildings(c, map))
				{
					return false;
				}
				Room room = c.GetRoom(map);
				if (room == null || !Indoor(room))
				{
					return false;
				}
				foreach (IntVec3 d in GenAdj.CardinalDirections)
				{
					IntVec3 n = c + d;
					if (n.InBounds(map) && n.GetDoor(map) != null)
					{
						return false;
					}
				}
			}
			return GenConstruct.CanPlaceBlueprintAt(def, cell, rot, map, false, null, null, stuff).Accepted;
		}

		private static bool AgainstWall(Map map, BuildableDef def, IntVec3 cell, Rot4 rot)
		{
			CellRect rect = GenAdj.OccupiedRect(cell, rot, def.Size);
			foreach (IntVec3 c in rect)
			{
				foreach (IntVec3 d in GenAdj.CardinalDirections)
				{
					IntVec3 n = c + d;
					if (rect.Contains(n) || !n.InBounds(map))
					{
						continue;
					}
					Building edifice = n.GetEdifice(map);
					if (edifice != null && edifice.def.IsWall)
					{
						return true;
					}
				}
			}
			return false;
		}
	}
}
