using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// The hourly check (user, 2026-10-16: "1,2,3,4,7,9,10,12,13,14, and 2 fires EVERY ONE HOUR. BATCHED. AND PAUSES THE
	// GAME. GOES TO EVERY SINGLE AGENT ON THE GODDAMN LIST. BOTH SUBAGENT AND ORCHESTRATOR." / "Every goddamn subagent AND
	// ORCHESTRAOR votes to UNPAUSE."). The lines are AgentReminderScan.HourlyLines.
	//
	// Every game hour (TicksGame / 2500, as the 6-hour slot), a scan. An empty scan does nothing. A scan with lines opens
	// check N: the game pauses, and until the orchestrator has voted for N:
	//   - inbox_state.json carries it in "hourly" (StateField), which watch.ps1 hands to the orchestrator and
	//     wait_round.ps1 to every specialist, once per check. Outbox results do not carry it, and the refusal below is one
	//     line (user, 2026-10-19: "it would be REMINDER list that would get spammed until they vote, which autopause game,
	//     so it shouldnt even be spamming to them."; every result and every refusal carried the whole batch before);
	//   - a speed order other than a pause, and a run, are refused (FormWave1.PlanGameSpeed, AgentTime.PlanRun), and the
	//     inbox gives no speed back after a wait (AgentInbox.RestoreAfterWait);
	//   - "show speed." says why the game stands still (PauseLine).
	// A vote is a file <SaveData>\VerbalCommands\votes\<N>\<name>.txt, written by the skill's vote.ps1, which needs N: N is
	// only in the batch, so an agent can not vote without having been handed it (user: "Yes, build that").
	// The voter is the orchestrator alone (user, 2026-10-19: "the 1-hour reminder does not need specialist voting. I dont
	// trust haikus to vote intelligently."; it was the orchestrator and every specialist with a fresh wait_round.ps1
	// heartbeat before, user's pick "Orchestrator + live specialists", 2026-10-16). When it has voted, the game goes back to
	// normal speed (1x: the speed cap of rimworld_play_scripts.py). A player who unpauses by hand is not fought: N stays
	// open until the vote is in, and a newer check replaces it.
	// Main thread: Tick from AgentReminderScan.Tick (game ticks), Update from AgentInbox.Poll (frames, paused too).
	internal static class AgentHourlyCheck
	{
		public const string OrchestratorName = "orchestrator";
		private const int SlotTicks = GenDate.TicksPerHour;   // 2500

		private static int lastSlot = int.MinValue;
		private static int seq;
		private static bool open;
		private static List<string> lines = new List<string>();
		private static string scannedAt = "";
		private static List<string> voters = new List<string>();
		private static HashSet<string> voted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		public static bool IsOpen
		{
			get { return open; }
		}

		// A new game or a load: the open check belongs to the old game. Its votes folder stays; the next N is above it.
		public static void Reset()
		{
			lastSlot = int.MinValue;
			open = false;
			lines = new List<string>();
			voters = new List<string>();
			voted.Clear();
		}

		public static void Tick()
		{
			int slot = Find.TickManager.TicksGame / SlotTicks;
			if (lastSlot == int.MinValue)
			{
				lastSlot = slot;   // a load: the first scan is at the next game hour
				AgentWorkChanceScan.MarkScanned(Find.TickManager.TicksGame);   // botched builds count from here
				return;
			}
			if (slot == lastSlot)
			{
				return;
			}
			lastSlot = slot;
			FormPlanner.WriteBillsFile();   // bills.md, every game hour (user, 2026-10-17; FormBills.cs)
			List<string> batch = AgentReminderScan.HourlyLines();
			List<KeyValuePair<string, string>> found = HarnessLog.TakeHourly();
			string when = AgentReminderScan.ScanTime();
			if (batch.Count == 0)
			{
				return;
			}
			Open(batch, when);
			HarnessLog.AddHourly(seq, found);   // the harness log (HarnessLog.cs, H3)
		}

		private static void Open(List<string> batch, string when)
		{
			seq = NextSeq();
			lines = batch;
			scannedAt = when;
			voters = new List<string> { OrchestratorName };
			voted.Clear();
			open = true;
			AgentReminderLog.Scan("Hourly check " + seq, when, batch);
			// A run in progress ends with the reason; then the pause itself (a run or not).
			AgentTime.RequestPause(PauseLine());
			TickManager tm = Find.TickManager;
			if (tm != null && tm.CurTimeSpeed != TimeSpeed.Paused)
			{
				tm.Pause();
			}
		}

		// Frames (AgentInbox.Poll): count the votes; when every voter has voted, back to normal speed.
		public static void Update()
		{
			if (!open)
			{
				return;
			}
			try
			{
				string dir = Path.Combine(VotesDir(), seq.ToString());
				if (Directory.Exists(dir))
				{
					foreach (string f in Directory.GetFiles(dir, "*.txt"))
					{
						voted.Add(Path.GetFileNameWithoutExtension(f).Trim());
					}
				}
				if (voters.All((string v) => voted.Contains(v)))
				{
					open = false;
					TickManager tm = Find.TickManager;
					if (tm != null && tm.CurTimeSpeed == TimeSpeed.Paused)
					{
						tm.CurTimeSpeed = TimeSpeed.Normal;   // what the speed buttons do (TickManager.cs:187-207); leaves pause
					}
				}
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] hourly check: counting the votes failed: " + ex.Message);
			}
		}

		// "[REMINDER] Hourly check 17 (2nd of Jugust, 5500, 13h), 3 reminders:" and the numbered lines (user's pick).
		public static string BatchText()
		{
			return AgentReminderScan.Block(new List<string>(lines), "Hourly check " + seq, scannedAt);
		}

		// "Hourly check 17: 3 reminders. Read them, then vote: vote.ps1 -Name NAME -Check 17" (user's pick, "show speed.").
		public static string PauseLine()
		{
			return "Hourly check " + seq + ": " + lines.Count + (lines.Count == 1 ? " reminder" : " reminders") + ". Read them, then vote: vote.ps1 -Name NAME -Check " + seq;
		}

		// "Hourly check 17 is waiting for the orchestrator's vote." The refusal of a speed order while N is open; one line,
		// without the batch (user, 2026-10-19, see the head of this file; it was "... waiting for votes from: orchestrator.
		// <the batch>").
		public static string RefusalText()
		{
			return "Hourly check " + seq + " is waiting for the orchestrator's vote.";
		}

		// The "hourly" field of inbox_state.json: null when no check is open.
		public static JToken StateField()
		{
			if (!open)
			{
				return JValue.CreateNull();
			}
			JObject o = new JObject();
			o["seq"] = seq;
			o["text"] = BatchText();
			o["pause_line"] = PauseLine();
			o["voters"] = new JArray(voters.ToArray());
			o["waiting_for"] = new JArray(Missing().ToArray());
			return o;
		}

		// For AgentInbox.WriteStateIfChanged: changes when N opens, closes, or a vote comes in.
		public static string StateKey()
		{
			return open ? seq + ":" + string.Join(",", Missing().ToArray()) : "-";
		}

		private static List<string> Missing()
		{
			return voters.Where((string v) => !voted.Contains(v)).ToList();
		}

		// Above every N used so far, this launch and earlier ones (the votes folder keeps them).
		private static int NextSeq()
		{
			int n = seq;
			try
			{
				string dir = VotesDir();
				if (Directory.Exists(dir))
				{
					foreach (string d in Directory.GetDirectories(dir))
					{
						int v;
						if (int.TryParse(Path.GetFileName(d), out v) && v > n)
						{
							n = v;
						}
					}
				}
			}
			catch (Exception)
			{
			}
			return n + 1;
		}

		private static string Root()
		{
			return Path.GetDirectoryName(AgentInbox.StatePath) ?? "";
		}

		private static string VotesDir()
		{
			return Path.Combine(Root(), "votes");
		}
	}
}
