using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Room stats (user, 2026-10-17): "New order. Show {Roomname} stats. If there is duplicate (because fucking rimworld),
	// lists ALL of the room of that name with roomID/list of buildings in the room, AND their stats. It should show their
	// room stats, and say "This {room} needs to have {stats_type} important, though beauty/impressiveness in general also
	// affect indirectly via increasing pawn's modd." (yes, in full). Also shows bills present on that room." / "Mood* not
	// modd" / "If a room dont have anything, fallback to space/beauty/cleanliness. also says what buildings there are."
	// The room ID is the game's Room.ID ("Room412", the user's pick; it is a counter the game hands out when it rebuilds a
	// room, Room.MakeNew, Verse\Room.cs:595-600, so it changes after a load or a wall/door change). {stats_type}: the stats
	// the room's role marks related (RoomRoleDef.IsStatRelated; Core RoomRoles.xml relatedStats). Texts: the user's pick
	// "As shown". The same blocks are the list "build FLOOR for ROOM" / "cancel floor for ROOM" give for several rooms.
	public static partial class FormPlanner
	{
		// The answer's order.
		private static RoomStatDef[] ShownRoomStats()
		{
			return new RoomStatDef[] { RoomStatDefOf.Impressiveness, RoomStatDefOf.Beauty, RoomStatDefOf.Cleanliness, RoomStatDefOf.Space, RoomStatDefOf.Wealth };
		}

		// The "needs to have" line's order (as RoomRoles.xml lists them).
		private static RoomStatDef[] ImportantRoomStats()
		{
			return new RoomStatDef[] { RoomStatDefOf.Beauty, RoomStatDefOf.Cleanliness, RoomStatDefOf.Wealth, RoomStatDefOf.Space, RoomStatDefOf.Impressiveness };
		}

		// ROOM as a room ID ("room412") or a room name (every room of that name).
		internal static List<Room> RoomsByNameOrId(Map map, string text)
		{
			string t = (text ?? "").Trim();
			Match m = Regex.Match(t, "^room([0-9]+)$", RegexOptions.IgnoreCase);
			if (m.Success)
			{
				int id = int.Parse(m.Groups[1].Value);
				Room room = map.regionGrid.AllRooms.FirstOrDefault((Room r) => r != null && r.ID == id);
				if (room == null)
				{
					throw new Fault("Nothing on the map has the ID " + t + ".");
				}
				return new List<Room> { room };
			}
			return ResolveRooms(map, t).OrderBy((Room r) => r.ID).ToList();
		}

		// "Room412: barracks (fueled stove, 3 beds, small sculpture)".
		internal static string RoomHead(Room room)
		{
			List<string> parts = RoomBuildings(room);
			return "Room" + room.ID + ": " + (VocabularyText.RoomLabel(room) ?? "room") + (parts.Count > 0 ? " (" + string.Join(", ", parts.ToArray()) + ")" : "");
		}

		private static List<string> RoomBuildings(Room room)
		{
			Map map = room.Map;
			Dictionary<string, int> counts = new Dictionary<string, int>();
			HashSet<Thing> seen = new HashSet<Thing>();
			foreach (IntVec3 cell in room.Cells)
			{
				foreach (Thing t in cell.GetThingList(map))
				{
					if (t.def.category != ThingCategory.Building || t.def.IsBlueprint || t.def.IsFrame || !seen.Add(t))
					{
						continue;
					}
					int n;
					counts.TryGetValue(t.def.label, out n);
					counts[t.def.label] = n + 1;
				}
			}
			return counts.OrderBy((KeyValuePair<string, int> k) => k.Key)
				.Select((KeyValuePair<string, int> k) => k.Value == 1 ? k.Key : k.Value + " " + Find.ActiveLanguageWorker.Pluralize(k.Key, k.Value)).ToList();
		}

		// "impressiveness 12 (awful), beauty -1.20 (ugly), ..." in the game's own number format and stage words.
		internal static string RoomStatText(RoomStatDef stat, float value)
		{
			RoomStatScoreStage stage = stat.GetScoreStage(value);
			return stat.label + " " + stat.ScoreToString(value) + (stage != null && !stage.label.NullOrEmpty() ? " (" + stage.label + ")" : "");
		}

		// The role's related stats, or else space, beauty, cleanliness.
		internal static List<string> RoomImportantNames(Room room)
		{
			List<string> names = room.Role == null ? new List<string>()
				: ImportantRoomStats().Where((RoomStatDef s) => room.Role.IsStatRelated(s)).Select((RoomStatDef s) => s.label).ToList();
			if (names.Count == 0)
			{
				names = new List<string> { RoomStatDefOf.Space.label, RoomStatDefOf.Beauty.label, RoomStatDefOf.Cleanliness.label };
			}
			return names;
		}

		// "impressiveness 12 (awful), beauty -1.20 (ugly), cleanliness ..., space ..., wealth ...".
		internal static string RoomStatsLine(Room room)
		{
			return string.Join(", ", ShownRoomStats().Select((RoomStatDef s) => RoomStatText(s, room.GetStat(s))).ToArray());
		}

		// The user's line.
		private static string RoomImportantLine(Room room)
		{
			List<string> names = RoomImportantNames(room);
			return "This " + (VocabularyText.RoomLabel(room) ?? "room") + " needs to have " + string.Join(", ", names.ToArray())
				+ " important, though beauty/impressiveness in general also affect indirectly via increasing pawn's mood.";
		}

		// "bills: cook simple meal - forever (fueled stove, only Zimmerman); ..." or "bills: none".
		internal static string RoomBillsLine(Room room)
		{
			Map map = room.Map;
			List<string> bills = new List<string>();
			HashSet<Building_WorkTable> benches = new HashSet<Building_WorkTable>();
			foreach (IntVec3 cell in room.Cells)
			{
				foreach (Thing t in cell.GetThingList(map))
				{
					Building_WorkTable bench = t as Building_WorkTable;
					if (bench == null || bench.billStack == null || !benches.Add(bench))
					{
						continue;
					}
					foreach (Bill bill in bench.billStack.Bills)
					{
						Bill_Production production = bill as Bill_Production;
						bills.Add(bill.Label + (production != null ? " - " + RbBillRepeat(production) : "") + " (" + bench.def.label
							+ (bill.PawnRestriction != null ? ", only " + bill.PawnRestriction.LabelShort : "") + ")");
					}
				}
			}
			return "bills: " + (bills.Count > 0 ? string.Join("; ", bills.ToArray()) : "none");
		}

		// One room's block of "show ROOM stats.".
		internal static List<string> RoomBlock(Room room)
		{
			List<string> lines = new List<string>();
			lines.Add(RoomHead(room));
			lines.Add("  " + RoomStatsLine(room));
			lines.Add("  " + RoomImportantLine(room));
			lines.Add("  " + RoomBillsLine(room));
			return lines;
		}

		// The list a room form gives when ROOM names several rooms.
		internal static string RoomBlocks(IEnumerable<Room> rooms)
		{
			return string.Join("\n", rooms.SelectMany((Room r) => RoomBlock(r)).ToArray());
		}

		internal static QueryReply QueryRoomStats(FormContext c)
		{
			return QueryReply.Ok(RoomBlocks(RoomsByNameOrId(c.map, c.Text("ROOM"))));
		}
	}
}
