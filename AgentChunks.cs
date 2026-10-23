using System.Collections.Generic;
using System.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Stone chunks indoors (user, 2026-10-16: "scans INTERIOR area for any stone chunks." / "fires reminders that there are
	// stonechunks present interior, which can affect beauty." / "Offers: "move chunks" command. agent can say "apply" or
	// "cancel". when apply: every chunk indoor is marked for "haul things"."). It replaces the 2026-10-14 line "You don't
	// seem to have a stockpile for stone chunks ..." (user: "Sure.", to replacing it). Wording given to the user and
	// approved before the build; the replies to "move chunks." with nothing to mark are the user's own words.
	//   Indoors: a visible stone chunk (ThingCategoryDefOf.StoneChunks) on a roofed cell of a room that is not open air
	//   (Room.PsychologicallyOutdoors, Verse\Room.cs:378-392), as the old line had it, and not in storage that takes it
	//   (it was put there; the haul mark refuses it, RimWorld\Designator_Haul.cs CanDesignateThing). A chunk is -8 beauty,
	//   a rock chunk 0 outdoors (Data\Core\Defs\ThingDefs_Misc\Various_Stone.xml:15, :32).
	//   The mark is the game's "haul things" (Designator_Haul): its CanDesignateThing / DesignateThing done on the chunk's
	//   own map, since the designator works on the map on screen (user: "Get rid of other map on screen part"). Chunks are
	//   not hauled unless marked or stored (RimWorld\ListerHaulables.cs:206), and a marked one only to storage that takes
	//   it (Verse.AI\HaulAIUtility.HaulToStorageJob); hence the "No stockpile" sentence.
	// Form "move chunks" (AgentPawnActions.DispatchWork); the plan always waits for apply / cancel (Plan.askConfirm).
	internal static class AgentChunks
	{
		private const int RoomsNamed = 3;

		// ---- text (every line an agent reads) ---------------------------------------------------------------------

		private static string ReminderText(int total, string where, bool noStockpile)
		{
			return "[REMINDER] You have " + total + " stone " + (total == 1 ? "chunk" : "chunks") + " indoors (" + where
				+ "), which lowers the rooms' beauty. Say \"move chunks.\" to haul them out."
				+ (noStockpile ? " No stockpile takes stone chunks yet; make one first, or the chunks stay." : "");
		}

		private static string ConfirmText(int total, string where)
		{
			return "Mark " + total + " stone " + (total == 1 ? "chunk" : "chunks") + " indoors for hauling (" + where + ")? Say apply to mark them, or cancel.";
		}

		private const string NoneIndoors = "No stone chunks indoors.";
		private const string AllMarked = "Every stone chunk indoors marked.";

		// ---- scan -------------------------------------------------------------------------------------------------

		// The indoor chunks of MAP by room, the room with the most first.
		internal static List<KeyValuePair<Room, List<Thing>>> Indoors(Map map)
		{
			Dictionary<Room, List<Thing>> byRoom = new Dictionary<Room, List<Thing>>();
			foreach (ThingDef d in ThingCategoryDefOf.StoneChunks.DescendantThingDefs)
			{
				foreach (Thing t in map.listerThings.ThingsOfDef(d))
				{
					if (!t.Spawned || !AgentPlace.Visible(map, t.Position) || !map.roofGrid.Roofed(t.Position) || t.IsInValidStorage())
					{
						continue;
					}
					Room room = t.GetRoom();
					if (room == null || room.PsychologicallyOutdoors)
					{
						continue;
					}
					List<Thing> list;
					if (!byRoom.TryGetValue(room, out list))
					{
						byRoom[room] = list = new List<Thing>();
					}
					list.Add(t);
				}
			}
			return byRoom.OrderByDescending((KeyValuePair<Room, List<Thing>> kv) => kv.Value.Count).ToList();
		}

		// A stockpile zone or storage building on MAP takes some stone chunk (StorageSettings.AllowedToAccept,
		// RimWorld\StorageSettings.cs:114).
		private static bool StockpileTakes(Map map)
		{
			List<ThingDef> chunkDefs = ThingCategoryDefOf.StoneChunks.DescendantThingDefs.ToList();
			foreach (SlotGroup g in map.haulDestinationManager.AllGroupsListForReading)
			{
				StorageSettings settings = g == null || g.parent == null ? null : g.parent.GetStoreSettings();
				if (settings != null && chunkDefs.Any((ThingDef d) => settings.AllowedToAccept(d)))
				{
					return true;
				}
			}
			return false;
		}

		// "5 in the bedroom (first at 12, 40); 2 in the kitchen (first at 20, 33)", the first RoomsNamed rooms.
		private static string Where(List<KeyValuePair<Room, List<Thing>>> rooms, bool firstAt)
		{
			List<string> parts = rooms.Take(RoomsNamed).Select((KeyValuePair<Room, List<Thing>> kv) => kv.Value.Count + " in the " + RoomName(kv.Key)
				+ (firstAt ? " (first at " + AgentWorkspotCheck.Xz(kv.Value[0].Position) + ")" : "")).ToList();
			if (rooms.Count > RoomsNamed)
			{
				parts.Add("more in " + (rooms.Count - RoomsNamed) + " other rooms");
			}
			return string.Join("; ", parts.ToArray());
		}

		private static string RoomName(Room room)
		{
			string name = VocabularyText.RoomLabel(room);
			return string.IsNullOrEmpty(name) ? "room" : name;
		}

		// The 6-hour line (AgentReminderScan.Scan), or null when no chunk lies indoors.
		internal static string ReminderLine(Map map)
		{
			List<KeyValuePair<Room, List<Thing>>> rooms = Indoors(map);
			if (rooms.Count == 0)
			{
				return null;
			}
			return ReminderText(rooms.Sum((KeyValuePair<Room, List<Thing>> kv) => kv.Value.Count), Where(rooms, true), !StockpileTakes(map));
		}

		// ---- the order --------------------------------------------------------------------------------------------

		// Designator_Haul.CanDesignateThing (RimWorld\Designator_Haul.cs) on the chunk's own map.
		private static bool CanMark(Map map, Thing t)
		{
			return t.Spawned && !t.Destroyed && t.def.designateHaulable && map.designationManager.DesignationOn(t, DesignationDefOf.Haul) == null && !t.IsInValidStorage();
		}

		public static PlanReply Plan(FormContext c)
		{
			Map map = c.map;
			List<KeyValuePair<Room, List<Thing>>> rooms = Indoors(map);
			if (rooms.Count == 0)
			{
				throw new Fault(NoneIndoors);
			}
			List<KeyValuePair<Room, List<Thing>>> unmarked = rooms
				.Select((KeyValuePair<Room, List<Thing>> kv) => new KeyValuePair<Room, List<Thing>>(kv.Key, kv.Value.Where((Thing t) => CanMark(map, t)).ToList()))
				.Where((KeyValuePair<Room, List<Thing>> kv) => kv.Value.Count > 0)
				.OrderByDescending((KeyValuePair<Room, List<Thing>> kv) => kv.Value.Count).ToList();
			if (unmarked.Count == 0)
			{
				throw new Fault(AllMarked);
			}
			List<Thing> chunks = unmarked.SelectMany((KeyValuePair<Room, List<Thing>> kv) => kv.Value).ToList();

			Plan plan = FormPlanner.NewPlan(c.form);
			plan.askConfirm = true;
			Write write = new Write();
			write.description = "move chunks";
			write.apply = delegate
			{
				// Designator_Haul.DesignateThing.
				foreach (Thing t in chunks)
				{
					if (CanMark(map, t))
					{
						map.designationManager.AddDesignation(new Designation(t, DesignationDefOf.Haul));
					}
				}
			};
			plan.writes.Add(write);
			return FormPlanner.Reply(plan, ConfirmText(chunks.Count, Where(unmarked, false)), null, null);
		}
	}
}
