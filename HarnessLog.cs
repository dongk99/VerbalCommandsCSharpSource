using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Harness interventions (user, 2026-10-16: "add to Colony Analysys mod (only shows on when agent inbox is on): have a log
	// whenever reminderes like that (force-place, vent, etc) fire. consult user what is APPORPORAITE to go there, to discern
	// what is an *advice* and what is a *harness*."). The user's picks ("1,2,3,4 only."):
	//   H1 the battery outdoor rule (BatteryOutdoorRule.Check): "Battery   116, 130 -> 116, 128";
	//   H2 the research bench rule (ResearchBenchRule.Check);
	//   H3 every hourly check (AgentHourlyCheck.Open), one entry per check ("One row per check") with its problems counted
	//      and placed ("for 3, expand and say how many it fired per problem, with coordinate/room info"): each problem the
	//      hourly scan finds calls Note(problem, place) while the scan runs (BeginHourly .. TakeHourly);
	//   H4 the confirm gates (AgentDoorCheck, AgentWindCheck, AgentLightCheck, AgentWorkspotCheck: Plan.confirmBy) when they
	//      hold an order that said apply (OrderController): "Door check   plan held for confirm".
	// Not logged (user: "H5 does not count. H6 does not count."): the micro orders' replies, the Claude Code hooks.
	// Only while the agent inbox is on. Kept with the save (GameComponent_VerbalCommands "vcHarnessLog").
	// ColonyAnalysys draws it (section "Harness interventions", the user's title) through reflection: Rows(), Count and
	// AgentInbox.Enabled, so neither mod needs the other.
	public class HarnessEntry : IExposable
	{
		public int tick;
		public string when;
		public string what;
		public string detail;
		// H3 only: "problem\tcount\tplaces".
		public List<string> problems = new List<string>();

		public void ExposeData()
		{
			Scribe_Values.Look(ref tick, "tick", 0);
			Scribe_Values.Look(ref when, "when");
			Scribe_Values.Look(ref what, "what");
			Scribe_Values.Look(ref detail, "detail");
			Scribe_Collections.Look(ref problems, "problems", LookMode.Value);
			if (Scribe.mode == LoadSaveMode.PostLoadInit && problems == null)
			{
				problems = new List<string>();
			}
		}
	}

	// The history's files (user, 2026-10-16: "whenever a save loads, the history gets saved into .rws, so if the game
	// cannot find apporporaite file for that colony. it loads from .rws instead."):
	//   - every save gets a colony id (GameComponent_VerbalCommands "vcColonyId"; a save without one gets one at its first
	//     load), and every entry is also written at once to <SaveData>\VerbalCommands\harness\<colony id>.jsonl, so a
	//     crash or a quit without saving loses nothing;
	//   - when a save loads (OnLoaded): that colony's file is read and what the save lacks is merged in, so it goes into the
	//     .rws at the next save; entries newer than the loaded save are dropped (a branch the player reloaded over) and the
	//     file is written again from the merged list. No file: the history is the .rws's, and the file is made from it.
	//   - one-time backfill (user's pick: "This colony, one-time"): <SaveData>\VerbalCommands\harness\import.jsonl, made
	//     from the old launch logs of one colony, goes into the first save loaded with it present, then is renamed
	//     import_used_<colony id>.jsonl.
	public static class HarnessLog
	{
		private static string Dir()
		{
			return Path.Combine(GenFilePaths.SaveDataFolderPath, "VerbalCommands", "harness");
		}

		private static string FileFor(string id)
		{
			return Path.Combine(Dir(), id + ".jsonl");
		}

		internal static void OnNewGame()
		{
			GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
			if (gc != null)
			{
				gc.ColonyId = Guid.NewGuid().ToString("N");
			}
		}

		internal static void OnLoaded()
		{
			try
			{
				GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
				if (gc == null || Find.TickManager == null)
				{
					return;
				}
				if (string.IsNullOrEmpty(gc.ColonyId))
				{
					gc.ColonyId = Guid.NewGuid().ToString("N");
				}
				int now = Find.TickManager.TicksGame;
				Directory.CreateDirectory(Dir());
				List<HarnessEntry> merged = new List<HarnessEntry>(gc.HarnessEntries);
				string path = FileFor(gc.ColonyId);
				if (File.Exists(path))
				{
					merged = Merge(merged, ReadFile(path).Where((HarnessEntry e) => e.tick <= now));
				}
				string import = Path.Combine(Dir(), "import.jsonl");
				if (File.Exists(import))
				{
					merged = Merge(merged, ReadFile(import).Where((HarnessEntry e) => e.tick <= now));
					string used = Path.Combine(Dir(), "import_used_" + gc.ColonyId + ".jsonl");
					if (File.Exists(used))
					{
						File.Delete(used);
					}
					File.Move(import, used);
				}
				gc.HarnessEntries.Clear();
				gc.HarnessEntries.AddRange(merged.OrderBy((HarnessEntry e) => e.tick));
				File.WriteAllText(path, string.Concat(gc.HarnessEntries.Select((HarnessEntry e) => ToJson(e) + "\n").ToArray()), new UTF8Encoding(false));
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] harness log: reading the history failed: " + ex.Message);
			}
		}

		private static string Key(HarnessEntry e)
		{
			return e.tick + "|" + e.what + "|" + e.detail + "|" + string.Join("\u0001", (e.problems ?? new List<string>()).ToArray());
		}

		private static List<HarnessEntry> Merge(List<HarnessEntry> have, IEnumerable<HarnessEntry> more)
		{
			HashSet<string> keys = new HashSet<string>(have.Select(Key));
			List<HarnessEntry> result = new List<HarnessEntry>(have);
			foreach (HarnessEntry e in more)
			{
				if (keys.Add(Key(e)))
				{
					result.Add(e);
				}
			}
			return result;
		}

		private static string ToJson(HarnessEntry e)
		{
			JObject o = new JObject();
			o["tick"] = e.tick;
			o["when"] = e.when ?? "";
			o["what"] = e.what ?? "";
			o["detail"] = e.detail ?? "";
			o["problems"] = new JArray((e.problems ?? new List<string>()).ToArray());
			return o.ToString(Formatting.None);
		}

		private static List<HarnessEntry> ReadFile(string path)
		{
			List<HarnessEntry> list = new List<HarnessEntry>();
			foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
			{
				if (string.IsNullOrWhiteSpace(line))
				{
					continue;
				}
				try
				{
					JObject o = JObject.Parse(line);
					HarnessEntry e = new HarnessEntry
					{
						tick = (int?)o["tick"] ?? 0,
						when = (string)o["when"] ?? "",
						what = (string)o["what"] ?? "",
						detail = (string)o["detail"] ?? "",
						problems = o["problems"] is JArray a ? a.Select((JToken t) => (string)t ?? "").ToList() : new List<string>()
					};
					if (e.what.Length > 0)
					{
						list.Add(e);
					}
				}
				catch (Exception)
				{
				}
			}
			return list;
		}

		private static void AppendToFile(HarnessEntry e)
		{
			try
			{
				GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
				if (gc == null || string.IsNullOrEmpty(gc.ColonyId))
				{
					return;
				}
				Directory.CreateDirectory(Dir());
				File.AppendAllText(FileFor(gc.ColonyId), ToJson(e) + "\n", new UTF8Encoding(false));
			}
			catch (Exception)
			{
			}
		}

		// The hourly scan's problems, in the order found: problem, place.
		private static List<KeyValuePair<string, string>> pending;

		internal static void BeginHourly()
		{
			pending = new List<KeyValuePair<string, string>>();
		}

		// One problem the hourly scan found (does nothing outside the scan).
		internal static void Note(string problem, string place)
		{
			if (pending != null)
			{
				pending.Add(new KeyValuePair<string, string>(problem, place ?? ""));
			}
		}

		internal static List<KeyValuePair<string, string>> TakeHourly()
		{
			List<KeyValuePair<string, string>> found = pending ?? new List<KeyValuePair<string, string>>();
			pending = null;
			return found;
		}

		// H3: check N with what the scan found, each problem once with how many times and where.
		internal static void AddHourly(int seq, List<KeyValuePair<string, string>> found)
		{
			List<string> problems = new List<string>();
			foreach (IGrouping<string, KeyValuePair<string, string>> g in found.GroupBy((KeyValuePair<string, string> kv) => kv.Key))
			{
				List<string> places = g.Select((KeyValuePair<string, string> kv) => kv.Value).Where((string s) => s.Length > 0).ToList();
				problems.Add(g.Key + "\t" + g.Count() + "\t" + string.Join(", ", places.ToArray()));
			}
			Add("Hourly check " + seq, "", problems);
		}

		internal static void Add(string what, string detail, List<string> problems = null)
		{
			try
			{
				if (!AgentInbox.Enabled || Current.Game == null || Find.TickManager == null)
				{
					return;
				}
				GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
				if (gc == null)
				{
					return;
				}
				int tick = Find.TickManager.TicksGame;
				HarnessEntry entry = new HarnessEntry
				{
					tick = tick,
					when = When(tick),
					what = what,
					detail = detail ?? "",
					problems = problems ?? new List<string>()
				};
				gc.HarnessEntries.Add(entry);
				AppendToFile(entry);
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] harness log: " + ex.Message);
			}
		}

		// "Day 3, 9h": the game day counted from 1 (TicksGame, as the agents' day notes) and the hour at the first home map.
		private static string When(int tick)
		{
			int day = tick / GenDate.TicksPerDay + 1;
			Map map = Find.Maps.FirstOrDefault((Map m) => m != null && m.IsPlayerHome);
			int hour = map != null ? GenLocalDate.HourOfDay(map) : GenDate.HourOfDay(Find.TickManager.TicksAbs, 0f);
			return "Day " + day + ", " + hour + "h";
		}

		// ---- for ColonyAnalysys (reflection) ---------------------------------------------------------------------

		public static int Count
		{
			get
			{
				GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
				return gc == null ? 0 : gc.HarnessEntries.Count;
			}
		}

		// How many it counts for (user, 2026-10-16: "each entry on 1hr check counts as 1 individual count"): an hourly check
		// every item it found (two blueprints waiting are 2), anything else 1.
		private static int Items(HarnessEntry e)
		{
			if (e.problems == null || e.problems.Count == 0)
			{
				return 1;
			}
			int n = 0;
			foreach (string p in e.problems)
			{
				string[] parts = p.Split('\t');
				int c;
				n += parts.Length > 1 && int.TryParse(parts[1], out c) ? c : 1;
			}
			return n;
		}

		// Newest first. An entry: { "head", when, what, detail, tick, items }; each H3 problem under it:
		// { "sub", problem, count, places }.
		public static List<string[]> Rows()
		{
			List<string[]> rows = new List<string[]>();
			GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
			if (gc == null)
			{
				return rows;
			}
			for (int i = gc.HarnessEntries.Count - 1; i >= 0; i--)
			{
				HarnessEntry e = gc.HarnessEntries[i];
				rows.Add(new string[] { "head", e.when ?? "", e.what ?? "", e.detail ?? "", e.tick.ToString(System.Globalization.CultureInfo.InvariantCulture),
					Items(e).ToString(System.Globalization.CultureInfo.InvariantCulture) });
				foreach (string p in e.problems)
				{
					string[] parts = p.Split('\t');
					rows.Add(new string[] { "sub", parts.Length > 0 ? parts[0] : "", parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : "" });
				}
			}
			return rows;
		}
	}
}
