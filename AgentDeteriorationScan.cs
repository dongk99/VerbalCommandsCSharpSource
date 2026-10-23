using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Items losing durability in stockpile zones, every 3 game hours with the grow lines (AgentReminderScan.GrowTick).
	// User, 2026-10-14: "[alert] I scanned your stockpiles. last time, I saw [item_durability] at [n_1] hr before, I see
	// that you have an indoor shelter (scans for ANY indoor room), and your items have deteoriated by [item_durability diff
	// *100%]. Maybe move them outside stockpile zone? Fires every 3hr." / "Just dont even BOTHER looking at logic, look at
	// the durability, diff them."
	//   Each scan keeps every item's hit points in every stockpile zone of the map (by thing ID); the next scan diffs:
	//   an item still in a stockpile zone with fewer hit points than last time is listed with its old and new
	//   durability (HitPoints / MaxHitPoints) and the drop. Fires only when the map has an indoor room (a proper room,
	//   not open air: Room.ProperRoom and not PsychologicallyOutdoors). The first scan after a load only takes the
	//   snapshot.
	internal static class AgentDeteriorationScan
	{
		private const int MaxItems = 6;

		private class Seen
		{
			public int hp;
			public int tick;
		}

		private static readonly Dictionary<int, Dictionary<int, Seen>> lastByMap = new Dictionary<int, Dictionary<int, Seen>>();

		public static void Reset()
		{
			lastByMap.Clear();
		}

		public static List<string> Lines(Map map)
		{
			List<string> lines = new List<string>();
			int now = Find.TickManager.TicksGame;
			Dictionary<int, Seen> last;
			lastByMap.TryGetValue(map.uniqueID, out last);
			Dictionary<int, Seen> snap = new Dictionary<int, Seen>();
			// zone label -> (thing, old hp, hours ago)
			Dictionary<string, List<KeyValuePair<Thing, Seen>>> dropped = new Dictionary<string, List<KeyValuePair<Thing, Seen>>>();
			foreach (Zone z in map.zoneManager.AllZones)
			{
				Zone_Stockpile stockpile = z as Zone_Stockpile;
				if (stockpile == null)
				{
					continue;
				}
				foreach (Thing t in stockpile.AllContainedThings)
				{
					if (t.def.category != ThingCategory.Item || !t.def.useHitPoints || t.MaxHitPoints <= 0 || snap.ContainsKey(t.thingIDNumber))
					{
						continue;
					}
					snap[t.thingIDNumber] = new Seen { hp = t.HitPoints, tick = now };
					Seen before;
					if (last != null && last.TryGetValue(t.thingIDNumber, out before) && t.HitPoints < before.hp)
					{
						List<KeyValuePair<Thing, Seen>> list;
						if (!dropped.TryGetValue(stockpile.label, out list))
						{
							dropped[stockpile.label] = list = new List<KeyValuePair<Thing, Seen>>();
						}
						list.Add(new KeyValuePair<Thing, Seen>(t, before));
					}
				}
			}
			lastByMap[map.uniqueID] = snap;
			if (dropped.Count == 0)
			{
				return lines;
			}
			Room shelter = map.regionGrid.AllRooms.FirstOrDefault((Room r) => r != null && r.ProperRoom && !r.PsychologicallyOutdoors && r.CellCount > 0);
			if (shelter == null)
			{
				return lines;
			}
			string shelterText = VocabularyText.RoomLabel(shelter);
			if (string.IsNullOrEmpty(shelterText))
			{
				shelterText = "room";
			}
			shelterText += " at " + AgentWorkspotCheck.Xz(shelter.Cells.First());
			foreach (KeyValuePair<string, List<KeyValuePair<Thing, Seen>>> kv in dropped)
			{
				List<KeyValuePair<Thing, Seen>> items = kv.Value.OrderByDescending((KeyValuePair<Thing, Seen> x) => x.Value.hp - x.Key.HitPoints).ToList();
				HarnessLog.Note("deterioration", kv.Key + ", " + items.Count + (items.Count == 1 ? " item" : " items"));
				float hours = (now - items[0].Value.tick) / (float)GenDate.TicksPerHour;
				float oldPct = items.Sum((KeyValuePair<Thing, Seen> x) => (float)x.Value.hp / x.Key.MaxHitPoints) / items.Count;
				float newPct = items.Sum((KeyValuePair<Thing, Seen> x) => (float)x.Key.HitPoints / x.Key.MaxHitPoints) / items.Count;
				string named = string.Join(", ", items.Take(MaxItems).Select((KeyValuePair<Thing, Seen> x) => x.Key.LabelNoCount + (x.Key.stackCount > 1 ? " x" + x.Key.stackCount : "")
					+ " " + ((float)x.Value.hp / x.Key.MaxHitPoints).ToStringPercent() + " -> " + ((float)x.Key.HitPoints / x.Key.MaxHitPoints).ToStringPercent()).ToArray())
					+ (items.Count > MaxItems ? " and " + (items.Count - MaxItems) + " more" : "");
				lines.Add("[ALERT] I scanned your stockpiles. Last time, " + hours.ToString("0") + (hours >= 1.5f ? " hours" : " hour") + " before, I saw "
					+ items.Count + (items.Count == 1 ? " item" : " items") + " in " + kv.Key + " at " + oldPct.ToStringPercent() + " durability. I see that you have an"
					+ " indoor shelter (" + shelterText + "), and your items have deteriorated by " + (oldPct - newPct).ToStringPercent() + " (" + named + ")."
					+ " Maybe move them out of " + kv.Key + ", into the indoor shelter?");
			}
			return lines;
		}
	}
}
