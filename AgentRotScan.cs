using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Food rotting on the map, in the hourly check (AgentReminderScan.HourlyLines). User, 2026-10-17: "have it bake into
	// 1-hour reminder. scans entire map, including forbidden. Anything outdoor gets grouped into 10x10 tile (any one item
	// gets picked that can rot, anything 10x10 area can be parsed as one group of item in an area). Anything INDOORS will
	// say [Itemname, Qty] is being rotten in [roomname]. Build a fridge room, or if temperature outdoor is low, a room with
	// vent for cheaper cooling cost." / "agent can say haul [itemname] and it will automatically manual-priority pawns to
	// move the item in that 10x10 area with a shell that opens with estimated time/duration (borrow code, theres something
	// that already does similar stuff). if they are forbidden, they are automatically unforbidden".
	// Answers to the questions asked (2026-10-17): outdoor line "Mirror indoor text"; "haul THINGS." reaches the square in
	// C# ("C# redirect"); "Rotting now" (rot progressing, not frozen, plus already rotten); the fridge sentence "Always, as
	// written".
	//
	// Game facts (sources\RimWorldDecompiled\):
	//   RimWorld\CompRottable.cs: Stage Fresh / Rotting / Dessicated by RotProgress (:36-49); Active (:61-75); rot grows by
	//   GenTemperature.RotRateAtTemperature(ambient) per tick (:92-100); the inspect pane's "frozen" is that rate at the
	//   rounded ambient temperature under 0.001 (:178-185).
	//   Outdoors: no room, or Room.PsychologicallyOutdoors (as AgentReminderScan.RoomLabel and AgentDeteriorationScan).
	//   Room name: VocabularyText.RoomLabel (Room.GetRoomRoleLabel), the label "in ROOM" orders look rooms up by.
	//
	// My choices (not the user's): corpses left out; only cells in view (AgentPlace.Visible, the mod's fair-play rule);
	// dessicated items left out (they no longer rot); the 10x10 squares are a fixed grid on map coordinates (x 0-9, 10-19,
	// ...); indoor lines are one per item kind per room label; the haul goes to the square with the most of that item.
	internal static class AgentRotScan
	{
		private const int Side = 10;

		private static string IndoorLine(string item, int qty, string room)
		{
			return "[REMINDER] [" + item + ", " + qty + "] is being rotten in " + room + ". Build a fridge room, or if temperature outdoor is low, a room with vent for cheaper cooling cost.";
		}

		private static string OutdoorLine(string item, int qty, CellRect square)
		{
			return "[REMINDER] [" + item + ", " + qty + "] is being rotten outdoors in (" + AgentWorkspotCheck.Xz(square.Min) + ") to (" + AgentWorkspotCheck.Xz(square.Max)
				+ "). Say \"haul " + item + "\" to move it.";
		}

		// Rot is going on now: rotting already, or fresh and not frozen.
		internal static bool RottingNow(Thing t)
		{
			CompRottable rot = t.TryGetComp<CompRottable>();
			if (rot == null || !rot.Active)
			{
				return false;
			}
			RotStage stage = rot.Stage;
			if (stage == RotStage.Rotting)
			{
				return true;
			}
			return stage == RotStage.Fresh && GenTemperature.RotRateAtTemperature(Mathf.RoundToInt(t.AmbientTemperature)) >= 0.001f;
		}

		private static bool Outdoors(Map map, IntVec3 cell)
		{
			Room room = cell.GetRoom(map);
			return room == null || room.PsychologicallyOutdoors;
		}

		// Every rotting item in view, forbidden ones too.
		private static IEnumerable<Thing> Rotting(Map map)
		{
			List<Thing> items = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);
			for (int i = 0; i < items.Count; i++)
			{
				Thing t = items[i];
				if (t == null || !t.Spawned || t.def.category != ThingCategory.Item || t is Corpse || !AgentPlace.Visible(map, t.Position))
				{
					continue;
				}
				if (RottingNow(t))
				{
					yield return t;
				}
			}
		}

		internal static CellRect SquareOf(Map map, IntVec3 c)
		{
			return new CellRect(c.x / Side * Side, c.z / Side * Side, Side, Side).ClipInsideMap(map);
		}

		// The hourly lines of one map: indoor ones first (by room, then most first), then the outdoor squares.
		internal static List<string> Lines(Map map)
		{
			Dictionary<string, Dictionary<ThingDef, int>> indoor = new Dictionary<string, Dictionary<ThingDef, int>>();
			Dictionary<IntVec3, Dictionary<ThingDef, int>> outdoor = new Dictionary<IntVec3, Dictionary<ThingDef, int>>();
			foreach (Thing t in Rotting(map))
			{
				Dictionary<ThingDef, int> counts;
				if (Outdoors(map, t.Position))
				{
					IntVec3 key = SquareOf(map, t.Position).Min;
					if (!outdoor.TryGetValue(key, out counts))
					{
						outdoor[key] = counts = new Dictionary<ThingDef, int>();
					}
				}
				else
				{
					Room where = t.Position.GetRoom(map);
					string room = VocabularyText.RoomLabel(where);
					if (string.IsNullOrEmpty(room))
					{
						room = "room";
					}
					// The room's ID too (user, 2026-10-17: the agents could not tell which "room" it was; "With "growroom" and
					// an ID for each one, I could look it up directly"): one line per room, "growroom (Room420)".
					if (where != null)
					{
						room += " (Room" + where.ID + ")";
					}
					if (!indoor.TryGetValue(room, out counts))
					{
						indoor[room] = counts = new Dictionary<ThingDef, int>();
					}
				}
				int had;
				counts.TryGetValue(t.def, out had);
				counts[t.def] = had + t.stackCount;
			}
			List<string> lines = new List<string>();
			foreach (KeyValuePair<string, Dictionary<ThingDef, int>> room in indoor.OrderBy((KeyValuePair<string, Dictionary<ThingDef, int>> r) => r.Key, StringComparer.Ordinal))
			{
				foreach (KeyValuePair<ThingDef, int> e in room.Value.OrderByDescending((KeyValuePair<ThingDef, int> e) => e.Value))
				{
					HarnessLog.Note("rotting food", e.Key.label + " x" + e.Value + ", " + room.Key);
					lines.Add(IndoorLine(e.Key.label, e.Value, room.Key));
				}
			}
			foreach (KeyValuePair<IntVec3, Dictionary<ThingDef, int>> sq in outdoor.OrderBy((KeyValuePair<IntVec3, Dictionary<ThingDef, int>> s) => s.Key.z).ThenBy((KeyValuePair<IntVec3, Dictionary<ThingDef, int>> s) => s.Key.x))
			{
				CellRect square = SquareOf(map, sq.Key);
				foreach (KeyValuePair<ThingDef, int> e in sq.Value.OrderByDescending((KeyValuePair<ThingDef, int> e) => e.Value))
				{
					HarnessLog.Note("rotting food", e.Key.label + " x" + e.Value + ", outdoors (" + AgentWorkspotCheck.Xz(square.Min) + ") to (" + AgentWorkspotCheck.Xz(square.Max) + ")");
					lines.Add(OutdoorLine(e.Key.label, e.Value, square));
				}
			}
			return lines;
		}

		// "haul THINGS." (form "designate haul", MODE all; AgentDesignations.Plan): when an item the words name is rotting
		// outdoors now and lies outside storage, the order becomes a micro haul (AgentMicro.PlanRotHaul) over the 10x10 square
		// holding the most of it; forbidden ones are unforbidden when it is applied. Else null: the order marks items as before.
		// Live, not the last hourly scan's squares, so a square already hauled clear is not sent to again.
		internal static PlanReply HaulPlan(FormContext c)
		{
			Map map = c.map;
			List<string> things = c.TextList("THINGS");
			if (things.Count == 0)
			{
				return null;
			}
			Func<Thing, bool> filter = AgentDesignations.WordFilter(map, "haul", things);
			if (filter == null)
			{
				return null;   // "everything" and the like: the old map-wide mark
			}
			Dictionary<IntVec3, int> bySquare = new Dictionary<IntVec3, int>();
			foreach (Thing t in Rotting(map))
			{
				if (!Outdoors(map, t.Position) || t.IsInValidStorage() || !filter(t))
				{
					continue;
				}
				IntVec3 key = SquareOf(map, t.Position).Min;
				int had;
				bySquare.TryGetValue(key, out had);
				bySquare[key] = had + t.stackCount;
			}
			if (bySquare.Count == 0)
			{
				return null;
			}
			IntVec3 best = bySquare.OrderByDescending((KeyValuePair<IntVec3, int> e) => e.Value).ThenBy((KeyValuePair<IntVec3, int> e) => e.Key.z).ThenBy((KeyValuePair<IntVec3, int> e) => e.Key.x).First().Key;
			return AgentMicro.PlanRotHaul(c, SquareOf(map, best), things);
		}
	}
}
