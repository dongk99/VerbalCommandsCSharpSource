using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace VerbalCommands
{
	// Vent options for heater and cooler blueprints (user, 2026-10-21: "code a reminder (whenever agent places blueprint) so
	// the game does the math for it, so whenever they place a heater/cooler the game can scan for different rooms that they
	// can place vent to (one vent per room) and show resulting temperature."), the sibling of AgentCoolerCheck: a NOTE on
	// the plan, never a Fault, no confirm.
	//
	// Wording (user, 2026-10-21: "looks fine"): "Vent instead: Barracks 22C, Storage 18C." Only options that settle within
	// 10C..30C are listed (user, 2026-10-21: "the gap needs to be between 10~30s instead of user's 10~40c"). No option:
	// no note.
	//
	// Game facts relied on (decompiled tree sources\RimWorldDecompiled\):
	//   heater heats its own cell's room                                                 RimWorld\Building_Heater.cs:22
	//   cooler cools the room at Position + South.RotatedBy(Rotation)                     RimWorld\Building_Cooler.cs:18, :40
	//   a vent moves the same heat each way, divided by each room's cell count, so two closed rooms settle at
	//   (cellsA x tempA + cellsB x tempB) / (cellsA + cellsB); other heat sources left out Verse\GenTemperature.cs:368-457
	//   walls a vent may replace: as AgentRoomTempScan.Vent (GenConstruct.cs:571, PlaceWorker_Vent.cs:38-46)
	internal static class AgentVentCheck
	{
		private const float LowestC = 10f;    // user: "between 10~30"
		private const float HighestC = 30f;

		private static readonly IntVec3[] Cardinals = new IntVec3[] { IntVec3.North, IntVec3.East, IntVec3.South, IntVec3.West };

		// ---- entry points ---------------------------------------------------------------------------------

		internal static void Note(Plan plan, Map map, ThingDef def, IntVec3 cell, Rot4 rot)
		{
			Note(plan, map, new List<AgentWindCheck.Placed> { new AgentWindCheck.Placed(def, cell, rot) });
		}

		internal static void Note(Plan plan, Map map, ThingDef def, IEnumerable<IntVec3> cells, Rot4 rot)
		{
			Note(plan, map, cells.Select((IntVec3 c) => new AgentWindCheck.Placed(def, c, rot)).ToList());
		}

		internal static void Note(Plan plan, Map map, List<AgentWindCheck.Placed> placed)
		{
			try
			{
				if (plan == null || map == null || placed == null)
				{
					return;
				}
				HashSet<Room> done = new HashSet<Room>();
				foreach (AgentWindCheck.Placed p in placed)
				{
					IntVec3 at;
					if (Is<Building_Heater>(p.def))
					{
						at = p.cell;
					}
					else if (Is<Building_Cooler>(p.def))
					{
						at = p.cell + IntVec3.South.RotatedBy(p.rot);
					}
					else
					{
						continue;
					}
					if (!at.InBounds(map))
					{
						continue;
					}
					Room room = at.GetRoom(map);
					if (room == null || !room.ProperRoom || room.UsesOutdoorTemperature || !done.Add(room))
					{
						continue;
					}
					List<string> parts = new List<string>();
					foreach (KeyValuePair<Room, float> o in Options(map, room))
					{
						if (o.Value >= LowestC && o.Value <= HighestC)
						{
							parts.Add(Name(o.Key) + " " + Temp(o.Value));
						}
					}
					if (parts.Count > 0)
					{
						plan.notes.Add("Vent instead: " + string.Join(", ", parts) + ".");
					}
				}
			}
			catch (Exception ex)
			{
				// A check that only informs must never stop a plan.
				Log.Warning("[VerbalCommands] vent check skipped: " + ex.Message);
			}
		}

		// ---- the parts ------------------------------------------------------------------------------------

		// Every other visible closed room behind a wall a vent may replace, once each, with the temperature both would
		// settle at. Rooms already joined to ROOM by a vent are left out.
		private static List<KeyValuePair<Room, float>> Options(Map map, Room room)
		{
			List<KeyValuePair<Room, float>> options = new List<KeyValuePair<Room, float>>();
			HashSet<Room> seen = new HashSet<Room>();
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
					if (!ventable || c.Impassable(map) || o.Impassable(map) || !AgentPlace.Visible(map, o))
					{
						continue;
					}
					if (seen.Add(other))
					{
						options.Add(new KeyValuePair<Room, float>(other, Settle(room, other)));
					}
				}
			}
			options.RemoveAll((KeyValuePair<Room, float> kv) => vented.Contains(kv.Key));
			return options;
		}

		private static float Settle(Room a, Room b)
		{
			float cells = a.CellCount + b.CellCount;
			return cells <= 0f ? a.Temperature : (a.CellCount * a.Temperature + b.CellCount * b.Temperature) / cells;
		}

		private static bool Is<T>(ThingDef def)
		{
			return def != null && def.thingClass != null && typeof(T).IsAssignableFrom(def.thingClass);
		}

		private static string Name(Room room)
		{
			string name = VocabularyText.RoomLabel(room);
			return FormPlanner.UpperFirst(string.IsNullOrEmpty(name) ? "room" : name);
		}

		private static string Temp(float celsius)
		{
			return Mathf.RoundToInt(celsius).ToString(CultureInfo.InvariantCulture) + "C";
		}
	}
}
