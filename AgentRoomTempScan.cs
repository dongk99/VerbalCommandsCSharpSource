using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Room temperature lines of the orchestrator's 6-hour reminder (AgentReminderScan.cs: same scan, batch and delivery).
	// User, 2026-10-14: "1 more order. same trigger mechanism, but condition: labeled room temperatures compared to
	// outdoor. If room temp is too high relative to outdoors (causing hyperthermia): tip fires: "consider making air
	// conditioners to keep room cool". if causing hypothermia: "consider making heaters to room temperature more warm."
	// if another room adjacent to that room is found and it's across a wall: "consider making a vent connecting two rooms
	// to equalize temperature"." and "assume hypothermia threshhold as 10c, hyperthermia threshhold as 30c."
	//
	// Rooms: the proper rooms (StorageIndex.AllProperRooms) with a labeled role (not the generic "room" or none,
	// StorageIndex.IsGenericRoomRole), not following the outdoor temperature (Room.UsesOutdoorTemperature: an open roof,
	// a heater would not help), visible. Below 10C: the heater line; above 30C: the air conditioner (cooler) line; both
	// say the outdoor temperature. A room kept cold on purpose (a cooler in or beside it set below 10C, a freezer) gets
	// no heater line; one kept hot on purpose (a heater set above 30C) no cooler line.
	// Vent: a wall cell between this room and another room that a vent may replace (a constructed or smoothed wall,
	// GenConstruct.cs:571 isPlaceOverableWall / IsSmoothed with Vent's canPlaceOverWall, Buildings_Temperature.xml) with
	// a passable cell on both sides (PlaceWorker_Vent.cs:38-46), where the other room is at least 5C on the helpful side
	// (warmer for a cold room, cooler for a hot one; 5C is a default, not the user's) and no vent joins the two yet.
	// One line per pair of rooms.
	// > CHANGED 2026-10-16: the hot threshold is 35C (was 30C), and the room line's advice is the user's three sentences,
	// > all of them, cold or hot, no gating (they replace "Consider making heaters ..." / "Consider making air conditioners
	// > ..." and the "cold enough for hypothermia" / "hot enough for heatstroke" words). User's words: "change it to 35c. And
	// > Add ALL 3 sentence into one output" / "if the outdoor is cold, and your inside is hot, Make more room or make room
	// > bigger so heat spreads out." "If your outdoor is hot, and your inside is also hot, THEN add cooler." "If your outdoor
	// > is cold, and your inside is cold, THEN build heater." (after the orchestrator spent steel on a cooler for a
	// > geyser-heated barracks at -15C outdoors). The vent line is unchanged.
	// > CHANGED again 2026-10-16: gated, one sentence per line (user: "lets see how smart you are. logic."). Hot room (above
	// > 35C): outdoors 35C or below -> spread the heat; outdoors above 35C -> cooler. Cold room (below 10C): outdoors below
	// > 10C -> heater; outdoors 10C or above -> nothing at all, no advice and no vent line: the room is a freezer, and the
	// > user's logic has no case for it (user: "MY VENT LOGIC DOESNT TRIGGER FOR 4TH CONDITION." / "I wouldnt write a
	// > sentence for something I know my logic doesnt have, NO?").
	internal static class AgentRoomTempScan
	{
		private const float ColdBelow = 10f;   // user: hypothermia threshold
		private const float HotAbove = 35f;    // user: hyperthermia threshold (30C until 2026-10-16: "change it to 35c.")

		// The user's three sentences, verbatim (2026-10-16); Advice picks one.
		private const string SpreadHeat = "if the outdoor is cold, and your inside is hot, Make more room or make room bigger so heat spreads out.";
		private const string AddCooler = "If your outdoor is hot, and your inside is also hot, THEN add cooler.";
		private const string BuildHeater = "If your outdoor is cold, and your inside is cold, THEN build heater.";

		// Hot room: outdoors at or below the hot line can take the heat (spread it), above it a cooler. Cold room (only with
		// outdoors below the cold line; Lines skips the freezer case): a heater.
		private static string Advice(bool cold, float outdoor)
		{
			if (cold)
			{
				return BuildHeater;
			}
			return outdoor > HotAbove ? AddCooler : SpreadHeat;
		}
		private const float VentGain = 5f;     // default: the other room must be this much better
		private const int MaxRooms = 20;

		private static readonly IntVec3[] Cardinals = new IntVec3[] { IntVec3.North, IntVec3.East, IntVec3.South, IntVec3.West };

		internal static List<string> Lines(Map map)
		{
			List<string> lines = new List<string>();
			if (map == null)
			{
				return lines;
			}
			float outdoor = map.mapTemperature.OutdoorTemp;
			HashSet<string> ventPairs = new HashSet<string>();
			int rooms = 0;
			foreach (Room room in StorageIndex.AllProperRooms(map))
			{
				if (StorageIndex.IsGenericRoomRole(room.Role) || room.UsesOutdoorTemperature)
				{
					continue;
				}
				IntVec3? spot = Centre(room);
				if (spot == null || !AgentPlace.Visible(map, spot.Value))
				{
					continue;
				}
				float temp = room.Temperature;
				bool cold = temp < ColdBelow && !KeptAt(room, true);
				bool hot = temp > HotAbove && !KeptAt(room, false);
				if (!cold && !hot)
				{
					continue;
				}
				// The freezer case (cold room, outdoors 10C or warmer): no line at all, no advice and no vent (user,
				// 2026-10-16: "MY VENT LOGIC DOESNT TRIGGER FOR 4TH CONDITION.").
				if (cold && outdoor >= ColdBelow)
				{
					continue;
				}
				if (rooms >= MaxRooms)
				{
					break;
				}
				rooms++;
				string name = Name(room);
				string head = "[REMINDER] The " + name + " (" + AgentWorkspotCheck.Xz(spot.Value) + ") is " + Temp(temp) + " while outdoors is " + Temp(outdoor);
				lines.Add(head + ": " + (cold ? "below " + Temp(ColdBelow) : "above " + Temp(HotAbove)) + ". " + Advice(cold, outdoor));
				HarnessLog.Note("room temperature", name + " (" + AgentWorkspotCheck.Xz(spot.Value) + ")");
				string vent = Vent(map, room, cold, ventPairs);
				if (vent != null)
				{
					lines.Add(vent);
				}
			}
			return lines;
		}

		// A wall between ROOM and a room on the helpful side that a vent could replace; the best such room. Null if none.
		private static string Vent(Map map, Room room, bool cold, HashSet<string> ventPairs)
		{
			Room best = null;
			IntVec3 bestWall = IntVec3.Invalid;
			HashSet<Room> vented = new HashSet<Room>();
			foreach (IntVec3 c in room.Cells)
			{
				foreach (IntVec3 d in Cardinals)
				{
					IntVec3 w = c + d;
					IntVec3 o = w + d;
					if (!w.InBounds(map) || !o.InBounds(map))
					{
						continue;
					}
					Building edifice = w.GetEdifice(map);
					if (edifice == null || edifice.def.building == null)
					{
						continue;
					}
					Room other = o.GetRoom(map);
					if (other == null || other == room || !other.ProperRoom || other.UsesOutdoorTemperature)
					{
						continue;
					}
					if (edifice is Building_Vent)
					{
						vented.Add(other);
						continue;
					}
					bool ventable = edifice.def.building.isPlaceOverableWall || edifice.def.IsSmoothed;
					if (!ventable || c.Impassable(map) || o.Impassable(map))
					{
						continue;
					}
					float gain = cold ? other.Temperature - room.Temperature : room.Temperature - other.Temperature;
					if (gain < VentGain)
					{
						continue;
					}
					if (best == null || (cold ? other.Temperature > best.Temperature : other.Temperature < best.Temperature))
					{
						best = other;
						bestWall = w;
					}
				}
			}
			if (best == null || vented.Contains(best))
			{
				return null;
			}
			string key = room.ID < best.ID ? room.ID + "-" + best.ID : best.ID + "-" + room.ID;
			if (!ventPairs.Add(key))
			{
				return null;
			}
			HarnessLog.Note("vent", Name(room) + " / " + Name(best) + " (" + AgentWorkspotCheck.Xz(bestWall) + ")");
			return "[REMINDER] The " + Name(room) + " (" + Temp(room.Temperature) + ") and the " + Name(best) + " (" + Temp(best.Temperature) + ") share a wall at "
				+ AgentWorkspotCheck.Xz(bestWall) + ": consider making a vent there connecting the two rooms to equalize temperature.";
		}

		// True when a cooler in or beside ROOM is set below 10C (COLD) or a heater above 30C (not COLD): kept so on purpose.
		private static bool KeptAt(Room room, bool cold)
		{
			foreach (Thing t in room.ContainedAndAdjacentThings)
			{
				if (t.Faction != Faction.OfPlayer)
				{
					continue;
				}
				CompTempControl ct = t.TryGetComp<CompTempControl>();
				if (ct == null)
				{
					continue;
				}
				if (cold && t is Building_Cooler && ct.TargetTemperature < ColdBelow)
				{
					return true;
				}
				if (!cold && t is Building_Heater && ct.TargetTemperature > HotAbove)
				{
					return true;
				}
			}
			return false;
		}

		private static string Name(Room room)
		{
			string name = VocabularyText.RoomLabel(room);
			return string.IsNullOrEmpty(name) ? "room" : name;
		}

		private static string Temp(float celsius)
		{
			return celsius.ToStringTemperature("F0");
		}

		// The room cell nearest its middle (as FormPlanner.ReadsRoomCell).
		private static IntVec3? Centre(Room room)
		{
			List<IntVec3> cells = room.Cells.ToList();
			if (cells.Count == 0)
			{
				return null;
			}
			float x = cells.Average((IntVec3 c) => (float)c.x);
			float z = cells.Average((IntVec3 c) => (float)c.z);
			return cells.OrderBy((IntVec3 c) => (c.x - x) * (c.x - x) + (c.z - z) * (c.z - z)).First();
		}
	}
}
