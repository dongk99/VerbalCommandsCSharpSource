using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// The mental break descriptions (agent_player\knowhow\mentalbreaks, copied by build.ps1 into Tips\mentalbreaks):
	// one .md per break, written from the RimWorld Wiki "Mental break" page (revision 184203) in the cloud session of
	// 2026-10-15 (Cloud_Session\claude_session_export). Two router questions read them:
	//   query mental break   "what does {BREAK} do."  the file of that break in full. User, 2026-10-15 (cloud
	//                        session): "At the end of mentalbreakwatch, add: [TIP] You can query each mental events and
	//                        what they do by saying "what does {mental_event} do"."
	//   query mental breaks  "list breaks."  every break's name by severity. User, 2026-10-15 (cloud session):
	//                        "Probably new verbalizer "list breaks"".
	// BREAK is found by, in this order: the file name ("targeted_tantrum"; other marks count as "_"); a defName on the "Game defs" column of mentalbreaks\INDEX.md ("Wander_OwnRoom");
	// the label the game gives that def, with underscores (sitting_alone = Wander_OwnRoom = hide_in_room). The game's
	// labels are not the wiki's names (Core MentalStates_Mood.xml: Wander_OwnRoom sitting_alone, Jailbreaker
	// inducing_prisoner_to_escape), and most MentalBreakDefs have no label of their own (only Catatonic, RunWild, FireTerror in the 1.6 XML),
	// so the state's label is used for those. Main thread only.
	public static class MentalBreakTips
	{
		private sealed class Entry
		{
			public string file;      // file name without .md
			public string section;   // the INDEX heading it is under ("Minor mood breaks")
			public string dlc;       // "none", "Biotech", "Anomaly and Ideology", ...
			public List<string> defs = new List<string>();
		}

		public static string Dir
		{
			get { return Path.Combine(TipInjector.TipsDir, "mentalbreaks"); }
		}

		// "what does BREAK do."
		public static QueryReply QueryBreak(string typed)
		{
			try
			{
				List<Entry> entries = ReadIndex();
				string n = Key(Strip(typed));
				if (n.Length == 0)
				{
					return QueryReply.Fail("Name the mental break; \"list breaks.\" lists them.");
				}
				List<string> files = Find(entries, n);
				if (files.Count == 0)
				{
					return QueryReply.Fail("No mental break named \"" + n + "\". The breaks are: " + string.Join(", ", Active(entries).Select((Entry e) => e.file).ToArray()) + ", general.");
				}
				if (files.Count > 1)
				{
					return QueryReply.Fail("\"" + n + "\" is the game's name for more than one break: " + string.Join(", ", files.ToArray())
						+ ". Ask for one: what does " + files[0] + " do.");
				}
				string text = File.ReadAllText(Path.Combine(Dir, files[0] + ".md"), Encoding.UTF8).Replace("\r", "");
				return QueryReply.Ok(string.Join("\n", text.Split('\n').Select((string l) => l.TrimEnd()).ToArray()).Trim());
			}
			catch (Exception ex)
			{
				return QueryReply.Fail("The mental break files could not be read: " + ex.Message);
			}
		}

		// "list breaks.": the names by severity, only the breaks of the DLCs in this game.
		public static QueryReply QueryList()
		{
			try
			{
				List<Entry> entries = Active(ReadIndex());
				StringBuilder sb = new StringBuilder();
				foreach (string section in entries.Select((Entry e) => e.section).Distinct())
				{
					sb.Append(section.ToLowerInvariant()).Append(": ")
						.Append(string.Join(", ", entries.Where((Entry e) => e.section == section).Select((Entry e) => e.file).ToArray())).Append('\n');
				}
				sb.Append("general: what every break shares (control, arrest, catharsis).\n");
				sb.Append("What one does and how it ends: what does BREAK do.");
				return QueryReply.Ok(sb.ToString());
			}
			catch (Exception ex)
			{
				return QueryReply.Fail("The mental break files could not be read: " + ex.Message);
			}
		}

		private static List<string> Find(List<Entry> entries, string n)
		{
			List<string> files = new List<string>();
			if (n == "general" || n == "index" || entries.Any((Entry e) => e.file == n))
			{
				files.Add(n == "index" ? "INDEX" : n);
				return files;
			}
			foreach (Entry e in entries)
			{
				if (e.defs.Any((string d) => Key(d) == n))
				{
					files.Add(e.file);
				}
			}
			if (files.Count > 0)
			{
				return files;
			}
			// The game's label of a def on the Game defs column (a break def without a label: its state's label).
			foreach (Entry e in entries)
			{
				foreach (string d in e.defs)
				{
					MentalStateDef state = DefDatabase<MentalStateDef>.GetNamedSilentFail(d);
					MentalBreakDef brk = DefDatabase<MentalBreakDef>.GetNamedSilentFail(d);
					string breakLabel = brk == null ? null : (brk.label.NullOrEmpty() ? brk.mentalState?.label : brk.label);
					if ((state != null && Key(state.label) == n) || (breakLabel != null && Key(breakLabel) == n))
					{
						if (!files.Contains(e.file))
						{
							files.Add(e.file);
						}
					}
				}
			}
			return files;
		}

		// The rows of mentalbreaks\INDEX.md: "| Event | `file.md` | DLC | Game defs |" under "## Section" headings.
		private static List<Entry> ReadIndex()
		{
			List<Entry> entries = new List<Entry>();
			string section = "";
			foreach (string raw in File.ReadAllLines(Path.Combine(Dir, "INDEX.md"), Encoding.UTF8))
			{
				string line = raw.Trim();
				if (line.StartsWith("## "))
				{
					section = line.Substring(3).Trim();
					continue;
				}
				string[] cells = line.Trim('|').Split('|').Select((string s) => s.Trim()).ToArray();
				if (!line.StartsWith("|") || cells.Length < 3 || !cells[1].StartsWith("`") || !cells[1].EndsWith(".md`"))
				{
					continue;
				}
				Entry e = new Entry();
				e.file = cells[1].Trim('`').Substring(0, cells[1].Length - 5);
				e.section = section;
				e.dlc = cells[2];
				if (cells.Length > 3)
				{
					e.defs = cells[3].Split(',').Select((string s) => s.Trim()).Where((string s) => s.Length > 0 && s != "?").ToList();
				}
				entries.Add(e);
			}
			return entries;
		}

		// Only the breaks whose DLC (every one named: "Anomaly and Ideology") is in this game.
		private static List<Entry> Active(List<Entry> entries)
		{
			return entries.Where((Entry e) => e.dlc.Split(new[] { " and " }, StringSplitOptions.None).All(DlcActive)).ToList();
		}

		private static bool DlcActive(string dlc)
		{
			switch (dlc.Trim().ToLowerInvariant())
			{
				case "royalty":
					return ModsConfig.RoyaltyActive;
				case "ideology":
					return ModsConfig.IdeologyActive;
				case "biotech":
					return ModsConfig.BiotechActive;
				case "anomaly":
					return ModsConfig.AnomalyActive;
				case "odyssey":
					return ModsConfig.OdysseyActive;
				default:
					return true;
			}
		}

		private static string Strip(string typed)
		{
			string s = (typed ?? "").Trim().TrimEnd('.').Trim();
			return s.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? s.Substring(0, s.Length - 3) : s;
		}

		// One word, the form of the file names: psychotic_wandering_daze.
		private static string Key(string label)
		{
			StringBuilder sb = new StringBuilder();
			foreach (char ch in (label ?? "").ToLowerInvariant())
			{
				if (char.IsLetterOrDigit(ch))
				{
					sb.Append(ch);
				}
				else if (sb.Length > 0 && sb[sb.Length - 1] != '_')
				{
					sb.Append('_');
				}
			}
			return sb.ToString().TrimEnd('_');
		}
	}
}
