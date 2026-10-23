using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// The 12-hour room list for the orchestrator (user, 2026-10-17): "have a 12hr reminder for room list (no building list,
	// but pawn bills for each room. Exception: Any room with hydropnic bays and/or sun lamp). with their stats, important
	// value, and say "You can build floor for these rooms by saying "build {floortype} for room" with expected costs on
	// each room, and expected beauty/cleanliness/impressiveness increase." The user's picks: the short "important:" line;
	// grow rooms are kept and show their buildings ("DO NOT leave out grow rooms."); "{room}" in the last line; every floor
	// the colony can lay, those it can afford first, an unaffordable one with "(you have N)" on the short material; and,
	// with no growing zone and no hydroponics basin on the map, "building floors this early without steady supply will risk
	// game over." after the list. Costs and estimates are the "build FLOOR for ROOM" plan's (AgentBuildShapes.FloorCells,
	// FloorCost, FloorGain); stats, important stats and bills are "show ROOM stats" (RoomStats.cs). Every second 6-hour
	// scan (AgentReminderScan.Tick), delivered with that batch.
	internal static class AgentRoomScan
	{
		// The whole block for the home maps, or null when there is no room.
		internal static string Text()
		{
			List<string> lines = new List<string>();
			foreach (Map map in Find.Maps.Where((Map m) => m != null && m.IsPlayerHome))
			{
				try
				{
					lines.AddRange(MapLines(map));
				}
				catch (Exception ex)
				{
					Log.Warning("[VerbalCommands] 12-hour room list failed: " + ex.Message);
				}
			}
			if (lines.Count == 0)
			{
				return null;
			}
			lines.Insert(0, "[REMINDER] 12-hour check (rooms):");
			lines.Add("You can build floor for these rooms by saying \"build {floortype} for {room}\"");
			return string.Join("\n", lines.ToArray());
		}

		private static List<string> MapLines(Map map)
		{
			List<string> lines = new List<string>();
			List<TerrainDef> floors = AgentBuild.FloorDefs().Where((TerrainDef d) => !d.MadeFromStuff).ToList();
			foreach (Room room in StorageIndex.AllProperRooms(map).OrderBy((Room r) => r.ID))
			{
				bool grow = StorageIndex.IsGrowRoom(room) || room.ContainedAndAdjacentThings.Any((Thing t) => t.def.defName == "SunLamp" && t.GetRoom() == room);
				lines.Add(grow ? FormPlanner.RoomHead(room) : "Room" + room.ID + ": " + (VocabularyText.RoomLabel(room) ?? "room"));
				lines.Add("  " + FormPlanner.RoomStatsLine(room));
				lines.Add("  important: " + string.Join(", ", FormPlanner.RoomImportantNames(room).ToArray()));
				lines.Add("  " + FormPlanner.RoomBillsLine(room));
				List<string> canAfford = new List<string>();
				List<string> cannotAfford = new List<string>();
				foreach (TerrainDef def in floors)
				{
					int already;
					int cannot;
					List<IntVec3> cells = AgentBuildShapes.FloorCells(map, room, def, out already, out cannot);
					if (cells.Count == 0)
					{
						continue;
					}
					Dictionary<ThingDef, int> cost = AgentBuildShapes.FloorCost(def, cells.Count);
					bool afford = true;
					List<string> parts = new List<string>();
					foreach (KeyValuePair<ThingDef, int> kv in cost)
					{
						int have = map.resourceCounter.GetCount(kv.Key);
						bool shortOf = have < kv.Value;
						afford = afford && !shortOf;
						parts.Add(kv.Value + " " + kv.Key.label + (shortOf ? " (you have " + have + ")" : ""));
					}
					string line = "  " + def.label + ": " + (parts.Count > 0 ? string.Join(", ", parts.ToArray()) + ", " : "")
						+ AgentBuildShapes.FloorGain(map, room, def, cells);
					(afford ? canAfford : cannotAfford).Add(line);
				}
				lines.AddRange(canAfford);
				lines.AddRange(cannotAfford);
			}
			if (lines.Count > 0 && !NoFoodSupply(map))
			{
				return lines;
			}
			if (lines.Count > 0)
			{
				lines.Add("building floors this early without steady supply will risk game over.");
			}
			return lines;
		}

		// True with no growing zone and no hydroponics basin on the map (the user's condition for the game-over line).
		private static bool NoFoodSupply(Map map)
		{
			if (map.zoneManager.AllZones.Any((Zone z) => z is Zone_Growing))
			{
				return false;
			}
			return !map.listerBuildings.allBuildingsColonist.Any((Building b) => b is Building_PlantGrower && b.def.building != null && b.def.building.sowTag == "Hydroponic");
		}
	}
}
