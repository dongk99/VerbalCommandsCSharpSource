using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	public sealed class AgentTarget
	{
		public string Name;   // "raider 1", "mad boar 2", "scyther 1"
		public Pawn Pawn;
		public bool Downed;   // true: shown as "raider 2 (down)" in the vocabulary line
	}

	// Stable names ("raider 1", "mad boar 2") for the hostile pawns the player can see.
	// MAIN THREAD ONLY: reads the live map and keeps static tables; nothing here is called from a tick.
	public static class AgentTargets
	{
		private const int MaxListed = 30;
		private const string DownSuffix = " (down)";

		private sealed class Entry
		{
			public Pawn Pawn;
			public string Name;
			public string Kind;
			public int Number;
		}

		private sealed class MapTable
		{
			public readonly Dictionary<int, Entry> ByPawn = new Dictionary<int, Entry>();   // thingIDNumber -> entry
			public readonly Dictionary<string, int> Counters = new Dictionary<string, int>(); // kind word -> last number given
		}

		// Map.uniqueID -> table. Reset when the game object changes (a loaded save starts fresh).
		private static readonly Dictionary<int, MapTable> Tables = new Dictionary<int, MapTable>();
		private static Game tablesGame;

		// Every hostile pawn on this map that the player can see (downed ones included), ordered by kind, then number.
		public static List<AgentTarget> Names(Map map)
		{
			List<AgentTarget> result = new List<AgentTarget>();
			if (map == null)
			{
				return result;
			}
			MapTable table = TableFor(map);
			if (table == null)
			{
				return result;
			}
			Prune(map, table);

			List<Entry> inView = new List<Entry>();
			IReadOnlyList<Pawn> spawned = map.mapPawns.AllPawnsSpawned;
			for (int i = 0; i < spawned.Count; i++)
			{
				Pawn pawn = spawned[i];
				if (!IsEligible(map, pawn))
				{
					continue;
				}
				Entry entry;
				if (table.ByPawn.TryGetValue(pawn.thingIDNumber, out entry))
				{
					if (AgentPlace.Visible(map, pawn.Position))
					{
						inView.Add(entry);
					}
					continue;
				}
				if (!AgentPlace.Visible(map, pawn.Position))
				{
					continue;
				}
				string kind = KindWord(pawn);
				int number;
				table.Counters.TryGetValue(kind, out number);
				number++;
				table.Counters[kind] = number;
				entry = new Entry { Pawn = pawn, Kind = kind, Number = number, Name = kind + " " + number };
				table.ByPawn[pawn.thingIDNumber] = entry;
				inView.Add(entry);
			}

			inView.Sort(CompareEntries);
			for (int i = 0; i < inView.Count; i++)
			{
				Entry e = inView[i];
				result.Add(new AgentTarget { Name = e.Name, Pawn = e.Pawn, Downed = e.Pawn.Downed });
			}
			return result;
		}

		// "raider 2" -> that pawn. False + plain-words reason when no such name, gone, or out of view.
		public static bool TryFind(Map map, string name, out Pawn target, out string reason)
		{
			target = null;
			reason = null;
			string key = Normalize(name);
			if (map == null || key.Length == 0)
			{
				reason = "There is no enemy called '" + key + "' in view.";
				return false;
			}
			MapTable table = TableFor(map);
			if (table != null)
			{
				Prune(map, table);
				foreach (KeyValuePair<int, Entry> kv in table.ByPawn)
				{
					if (kv.Value.Name != key)
					{
						continue;
					}
					if (!AgentPlace.Visible(map, kv.Value.Pawn.Position))
					{
						reason = "'" + key + "' is out of view now.";
						return false;
					}
					target = kv.Value.Pawn;
					return true;
				}
				// Not in the table: was the name ever given out on this map? Then it is gone.
				string kind;
				int number;
				if (SplitName(key, out kind, out number))
				{
					int last;
					if (table.Counters.TryGetValue(kind, out last) && number >= 1 && number <= last)
					{
						reason = "'" + key + "' is no longer on the map.";
						return false;
					}
				}
			}
			reason = "There is no enemy called '" + key + "' in view.";
			return false;
		}

		// The name of a pawn if it has one (for log lines and answers), else null. Never gives out a new name.
		public static string NameOf(Pawn pawn)
		{
			if (pawn == null || pawn.Map == null)
			{
				return null;
			}
			MapTable table = TableFor(pawn.Map);
			if (table == null)
			{
				return null;
			}
			Entry entry;
			if (table.ByPawn.TryGetValue(pawn.thingIDNumber, out entry) && entry.Pawn == pawn)
			{
				return entry.Name;
			}
			return null;
		}

		// "hostiles: raider 1, raider 2 (down), mad boar 1" or "hostiles: none".
		public static string VocabularyLine(Map map)
		{
			List<AgentTarget> list = Names(map);
			if (list.Count == 0)
			{
				return "hostiles: none";
			}
			StringBuilder sb = new StringBuilder("hostiles: ");
			int shown = Math.Min(list.Count, MaxListed);
			for (int i = 0; i < shown; i++)
			{
				if (i > 0)
				{
					sb.Append(", ");
				}
				sb.Append(list[i].Name);
				if (list[i].Downed)
				{
					sb.Append(DownSuffix);
				}
			}
			if (list.Count > shown)
			{
				sb.Append(", and ").Append(list.Count - shown).Append(" more");
			}
			return sb.ToString();
		}

		// ---- internals ----

		// The table for this map, after dropping tables of a changed game or of maps that are gone.
		// Null when there is no game or the map is not one of the game's maps.
		private static MapTable TableFor(Map map)
		{
			Game game = Current.Game;
			if (game == null)
			{
				Tables.Clear();
				tablesGame = null;
				return null;
			}
			if (!ReferenceEquals(game, tablesGame))
			{
				Tables.Clear();
				tablesGame = game;
			}
			List<Map> maps = Find.Maps;
			if (Tables.Count > 0)
			{
				List<int> gone = null;
				foreach (int id in Tables.Keys)
				{
					bool alive = false;
					for (int i = 0; i < maps.Count; i++)
					{
						if (maps[i].uniqueID == id)
						{
							alive = true;
							break;
						}
					}
					if (!alive)
					{
						if (gone == null)
						{
							gone = new List<int>();
						}
						gone.Add(id);
					}
				}
				if (gone != null)
				{
					for (int i = 0; i < gone.Count; i++)
					{
						Tables.Remove(gone[i]);
					}
				}
			}
			if (!maps.Contains(map))
			{
				return null;
			}
			MapTable table;
			if (!Tables.TryGetValue(map.uniqueID, out table))
			{
				table = new MapTable();
				Tables[map.uniqueID] = table;
			}
			return table;
		}

		// Drop entries whose pawn is no longer spawned on this map, or is no longer a hostile of ours
		// (dead, left, captured). Out of view or downed pawns keep their name.
		private static void Prune(Map map, MapTable table)
		{
			if (table.ByPawn.Count == 0)
			{
				return;
			}
			List<int> drop = null;
			foreach (KeyValuePair<int, Entry> kv in table.ByPawn)
			{
				if (!IsEligible(map, kv.Value.Pawn))
				{
					if (drop == null)
					{
						drop = new List<int>();
					}
					drop.Add(kv.Key);
				}
			}
			if (drop != null)
			{
				for (int i = 0; i < drop.Count; i++)
				{
					table.ByPawn.Remove(drop[i]);
				}
			}
		}

		// Spawned on this map, alive, hostile to the colony, not the colony's own, not its prisoner or slave.
		// (Pawn.Dead: Verse/Pawn.cs:261; IsPrisonerOfColony: Verse/Pawn.cs:624; IsSlaveOfColony: Verse/Pawn.cs:648;
		// HostileTo(Faction): RimWorld/GenHostility.cs:118.)
		private static bool IsEligible(Map map, Pawn pawn)
		{
			if (pawn == null || pawn.Dead || !pawn.Spawned || pawn.Map != map)
			{
				return false;
			}
			if (pawn.Faction == Faction.OfPlayer || pawn.IsPrisonerOfColony || pawn.IsSlaveOfColony)
			{
				return false;
			}
			return pawn.HostileTo(Faction.OfPlayer);
		}

		// humanlike -> raider; animal in a manhunter or berserk state -> "mad <label>"; everything else -> "<label>".
		private static string KindWord(Pawn pawn)
		{
			if (pawn.RaceProps.Humanlike)
			{
				return "raider";
			}
			string label = (pawn.kindDef != null ? pawn.kindDef.label : null) ?? pawn.def.label ?? "enemy";
			label = label.Trim().ToLowerInvariant();
			if (pawn.RaceProps.Animal && IsMad(pawn))
			{
				return "mad " + label;
			}
			return label;
		}

		// Manhunter, ManhunterPermanent (RimWorld/MentalStateDefOf.cs:33,35) and the berserk states
		// (MentalStateDefOf.cs:8,10,17). ManhunterBloodRain (line 45) is Anomaly-only, so it may be null here.
		private static bool IsMad(Pawn pawn)
		{
			MentalStateDef cur = pawn.MentalStateDef;
			if (cur == null)
			{
				return false;
			}
			return cur == MentalStateDefOf.Manhunter
				|| cur == MentalStateDefOf.ManhunterPermanent
				|| cur == MentalStateDefOf.ManhunterBloodRain
				|| cur == MentalStateDefOf.Berserk
				|| cur == MentalStateDefOf.BerserkPermanent
				|| cur == MentalStateDefOf.BerserkWarcall;
		}

		private static int CompareEntries(Entry a, Entry b)
		{
			int c = string.CompareOrdinal(a.Kind, b.Kind);
			return c != 0 ? c : a.Number.CompareTo(b.Number);
		}

		// Lower case, trimmed, single spaces, without a trailing " (down)".
		private static string Normalize(string name)
		{
			if (name == null)
			{
				return "";
			}
			string s = name.Trim().ToLowerInvariant();
			if (s.EndsWith(DownSuffix, StringComparison.Ordinal))
			{
				s = s.Substring(0, s.Length - DownSuffix.Length).TrimEnd();
			}
			StringBuilder sb = new StringBuilder(s.Length);
			bool space = false;
			for (int i = 0; i < s.Length; i++)
			{
				if (char.IsWhiteSpace(s[i]))
				{
					space = true;
					continue;
				}
				if (space && sb.Length > 0)
				{
					sb.Append(' ');
				}
				space = false;
				sb.Append(s[i]);
			}
			return sb.ToString();
		}

		// "mad boar 3" -> ("mad boar", 3).
		private static bool SplitName(string key, out string kind, out int number)
		{
			kind = null;
			number = 0;
			int sp = key.LastIndexOf(' ');
			if (sp <= 0 || sp >= key.Length - 1)
			{
				return false;
			}
			if (!int.TryParse(key.Substring(sp + 1), out number))
			{
				return false;
			}
			kind = key.Substring(0, sp);
			return true;
		}
	}
}
