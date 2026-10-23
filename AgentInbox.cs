using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Agent inbox (agent_player brief 01, contract: agent_player\out\01\inbox_format.md).
	//
	// A Claude agent that plays with no screen and no keyboard writes orders as .txt files into the inbox folder.
	// Each file is run through the same order path as the order window (OrderController) and its result is written
	// to the outbox folder as a .json file. Off by default (mod setting agentInbox). No network code anywhere: the
	// only things read are *.txt files directly inside the inbox folder, and the only things written are files in
	// the three folders below. Nothing from the settings except the two flags is read, and the API key never
	// reaches any file (every JSON text also goes through RouterLog.Redact).
	//
	// Folder rule as RouterLog.cs:19-24 and :72: GenFilePaths.SaveDataFolderPath (Verse\GenFilePaths.cs:93-101); the
	// Config folder is a sibling of it and is never used.
	//
	// Threads: Update runs on the main thread (GameComponentUpdate). NoteHaikuReply may be called from any thread.
	public static class AgentInbox
	{
		private const string WarnPrefix = "[VerbalCommands] agent inbox: ";
		private const int MaxFileBytes = 16 * 1024;
		private const float PollSeconds = 0.5f;
		private const int KeepHaikuReplies = 20;

		private static readonly Regex LogPrefix = new Regex(@"^\[\d+h\] ", RegexOptions.Compiled);
		private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
		private static readonly UTF8Encoding Utf8Strict = new UTF8Encoding(false, true);
		private static readonly string[] KnownFlags = new string[] { "raw", "dry", "apply", "wait" };

		// The one order being worked (null when none).
		private sealed class Job
		{
			public string file;
			public string kind;               // "order" or "reply"
			public List<string> flags = new List<string>();
			public bool raw;
			public string text;
			public JObject gameStart;
			public int logStart;              // the controller's Log.Count when the job began
			public bool pausedByInbox;
			public string orderId = "";
			public int turn;
			public bool usedController;       // the order/apply/cancel went through the controller
			public List<string> routerLinesOverride; // reply files: the lines of the order that made the plan
			public int microAsks;             // AgentMicro.AskCount when the file was taken
		}

		private sealed class HaikuNote
		{
			public string orderId;
			public int turn;
			public string reply;
		}

		// Main thread only.
		private static string inboxDir, takenDir, outboxDir, statePath;
		private static bool pathsTried;
		private static bool foldersReady;
		private static float nextPoll;
		private static string lastStateKey;
		private static Game controllerGame;
		private static OrderController controller;
		private static Job job;
		private static List<string> lastOrderRouterLines = new List<string>();
		private static readonly HashSet<string> warned = new HashSet<string>();

		// Any thread.
		private static readonly object noteLock = new object();
		private static readonly List<HaikuNote> haikuNotes = new List<HaikuNote>();

		public static bool Enabled
		{
			get
			{
				VerbalCommandsSettings s = VerbalCommandsMod.Settings;
				return s != null && s.agentInbox;
			}
		}

		public static string InboxDir { get { EnsurePaths(); return inboxDir ?? ""; } }
		public static string OutboxDir { get { EnsurePaths(); return outboxDir ?? ""; } }
		public static string TakenDir { get { EnsurePaths(); return takenDir ?? ""; } }
		public static string StatePath { get { EnsurePaths(); return statePath ?? ""; } }

		// Main thread (GenFilePaths may touch UnityEngine.Application, as RouterLog.Configure notes).
		private static void EnsurePaths()
		{
			if (pathsTried)
			{
				return;
			}
			try
			{
				string root = Path.Combine(GenFilePaths.SaveDataFolderPath, "VerbalCommands");
				inboxDir = Path.Combine(root, "inbox");
				takenDir = Path.Combine(inboxDir, "taken");
				outboxDir = Path.Combine(root, "outbox");
				statePath = Path.Combine(root, "inbox_state.json");
				pathsTried = true;
			}
			catch (Exception ex)
			{
				Warn("the folder path could not be worked out: " + ex.Message);
			}
		}

		// Any thread. Called by RouterGame.HaikuAsync with each reply of the small model.
		public static void NoteHaikuReply(string orderId, int turn, string reply)
		{
			lock (noteLock)
			{
				for (int i = 0; i < haikuNotes.Count; i++)
				{
					if (haikuNotes[i].turn == turn && haikuNotes[i].orderId == orderId)
					{
						haikuNotes.RemoveAt(i);
						break;
					}
				}
				HaikuNote note = new HaikuNote();
				note.orderId = orderId ?? "";
				note.turn = turn;
				note.reply = reply ?? "";
				haikuNotes.Add(note);
				while (haikuNotes.Count > KeepHaikuReplies)
				{
					haikuNotes.RemoveAt(0);
				}
			}
		}

		// Main thread, every frame (GameComponentUpdate runs while the game is paused too).
		public static void Update()
		{
			RouterLog.ForceOn = Enabled;   // agent 02, item 3: the log is on while the inbox is on
			try
			{
				if (!Enabled)
				{
					foldersReady = false;
					return;
				}
				if (Current.ProgramState != ProgramState.Playing || Current.Game == null || Find.CurrentMap == null)
				{
					return;
				}
				float now = UnityEngine.Time.realtimeSinceStartup;
				if (now < nextPoll)
				{
					return;
				}
				nextPoll = now + PollSeconds;
				Poll(now);
			}
			catch (Exception ex)
			{
				Warn("update failed: " + ex);
			}
		}

		private static void Poll(float now)
		{
			// A different game (load, new game): the old job and controller belong to the old one.
			if (!object.ReferenceEquals(Current.Game, controllerGame) || controller == null)
			{
				if (controller != null && controller.State == OrderState.Sending)
				{
					try
					{
						controller.CancelPending();
					}
					catch (Exception ex)
					{
						Warn("the old game's order could not be cancelled: " + ex.Message);
					}
				}
				job = null;
				controller = new OrderController(false);
				controllerGame = Current.Game;
				lastOrderRouterLines = new List<string>();
				lastStateKey = null;
				inboxPausedSpeed = null;   // a speed saved in the old game is not given back in this one
			}

			if (!EnsureFolders())
			{
				return;
			}

			if (job != null)
			{
				try
				{
					if (controller.State != OrderState.Sending && controller.State != OrderState.Applying)
					{
						Job done = job;
						job = null;
						FinishJob(done);
					}
				}
				catch (Exception ex)
				{
					job = null;
					Warn("a result could not be written: " + ex.Message);
				}
			}

			if (job == null && !OrderController.AnyOrderInFlight)
			{
				try
				{
					TakeNext();
				}
				catch (Exception ex)
				{
					job = null;
					Warn("a file could not be taken: " + ex.Message);
				}
			}

			// The hourly check's votes (AgentHourlyCheck.cs): counted here, since game ticks stop while it holds the pause.
			AgentHourlyCheck.Update();

			WriteStateIfChanged();
		}

		// Makes the folders once per enable. False when the inbox must not be used (a folder is a link).
		private static bool EnsureFolders()
		{
			if (foldersReady)
			{
				return true;
			}
			EnsurePaths();
			if (inboxDir == null)
			{
				return false;
			}
			try
			{
				string configRoot = Path.Combine(GenFilePaths.SaveDataFolderPath, "Config"); // only compared with, never opened
				if (!string.IsNullOrEmpty(configRoot) &&
					Path.GetFullPath(inboxDir).StartsWith(Path.GetFullPath(configRoot), StringComparison.OrdinalIgnoreCase))
				{
					Warn("the inbox folder would be under the game's Config folder, so it is not used");
					return false;
				}
				string root = Path.GetDirectoryName(statePath);
				Directory.CreateDirectory(inboxDir);
				Directory.CreateDirectory(takenDir);
				Directory.CreateDirectory(outboxDir);
				foreach (string dir in new string[] { root, inboxDir, takenDir, outboxDir })
				{
					if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0)
					{
						Warn("the folder " + dir + " is a link, so the inbox is not used");
						return false;
					}
				}
				foldersReady = true;
				return true;
			}
			catch (Exception ex)
			{
				Warn("the folders could not be made: " + ex.Message);
				return false;
			}
		}

		// Step 3 of a poll: take the oldest *.txt and work it.
		private static void TakeNext()
		{
			List<FileInfo> candidates = new List<FileInfo>();
			foreach (string path in Directory.GetFiles(inboxDir, "*.txt", SearchOption.TopDirectoryOnly))
			{
				// The *.txt pattern also matches longer extensions on this framework (a.txtx): only .txt itself is taken.
				if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
				{
					candidates.Add(new FileInfo(path));
				}
			}
			if (candidates.Count == 0)
			{
				return;
			}
			candidates.Sort(delegate (FileInfo a, FileInfo b)
			{
				int byTime = a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc);
				return byTime != 0 ? byTime : string.CompareOrdinal(a.Name, b.Name);
			});
			FileInfo fi = candidates[0];
			string name = fi.Name;
			string reject = null;
			byte[] bytes = null;

			if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
			{
				reject = "The file is a link (shortcut); the inbox does not read links.";
			}
			else if (fi.Length > MaxFileBytes)
			{
				reject = "The file is bigger than 16 KB.";
			}
			else
			{
				try
				{
					bytes = File.ReadAllBytes(fi.FullName);
				}
				catch (UnauthorizedAccessException)
				{
					reject = "The file could not be read.";
				}
				catch (IOException)
				{
					// Still being written (or it just went away): leave it and try again next poll.
					return;
				}
			}

			string text = null;
			if (reject == null)
			{
				if (bytes.Length > MaxFileBytes)
				{
					reject = "The file is bigger than 16 KB.";
				}
				else if (bytes.Length == 0)
				{
					reject = "The file is empty.";
				}
				else
				{
					try
					{
						text = Utf8Strict.GetString(bytes);
					}
					catch (Exception)
					{
						reject = "The file is not valid UTF-8 text.";
					}
				}
			}

			// Taken files are moved, never deleted.
			if (!MoveToTaken(fi.FullName, name))
			{
				return;
			}

			Job j = new Job();
			j.file = name;
			j.gameStart = GameInfo();
			j.logStart = controller.Log.Count;
			j.microAsks = AgentMicro.AskCount;
			// Token meter: read at take too, so this order's tips see a compaction before it (TipInjector.cs).
			AgentContext.NoteTake(name);

			if (reject != null)
			{
				j.kind = "order";
				j.text = "";
				WriteOutbox(j, "rejected", new List<string> { reject });
				return;
			}

			text = text.TrimStart('﻿');
			string trimmed = text.Trim();
			string lower = trimmed.ToLowerInvariant();

			// A reply: the whole text is apply or cancel; it has no header.
			if (lower == "apply" || lower == "cancel")
			{
				j.kind = "reply";
				j.text = lower;
				WorkReply(j, lower);
				return;
			}

			j.kind = "order";
			string body = text;
			string firstLine = text;
			string rest = "";
			int nl = text.IndexOf('\n');
			if (nl >= 0)
			{
				firstLine = text.Substring(0, nl);
				rest = text.Substring(nl + 1);
			}
			Match header = Regex.Match(firstLine, @"^\s*mode\s*:(.*)$", RegexOptions.IgnoreCase);
			if (header.Success)
			{
				body = rest;
				string[] given = header.Groups[1].Value.Split(new char[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
				foreach (string g in given)
				{
					string flag = g.ToLowerInvariant();
					if (!j.flags.Contains(flag))
					{
						j.flags.Add(flag);
					}
				}
				j.text = body.Trim();
				List<string> unknown = j.flags.Where((string f) => Array.IndexOf(KnownFlags, f) < 0).ToList();
				if (unknown.Count > 0)
				{
					WriteOutbox(j, "rejected", new List<string> { "Unknown flag in the mode line: " + string.Join(" ", unknown.ToArray()) + ". The flags are raw, dry, apply, wait." });
					return;
				}
				if (j.flags.Contains("dry") && j.flags.Contains("apply"))
				{
					WriteOutbox(j, "rejected", new List<string> { "The mode line has dry and apply together; a dry run never applies." });
					return;
				}
				if (j.flags.Contains("apply") && j.flags.Contains("wait"))
				{
					WriteOutbox(j, "rejected", new List<string> { "The mode line has apply and wait together." });
					return;
				}
				if (j.text.Length == 0)
				{
					WriteOutbox(j, "rejected", new List<string> { "The file has a mode line but no order text." });
					return;
				}
			}
			else
			{
				j.text = body.Trim();
			}
			j.raw = j.flags.Contains("raw");

			WorkOrder(j);
		}

		// A reply file (apply / cancel) against what is waiting.
		private static void WorkReply(Job j, string word)
		{
			bool planWaits = controller.State == OrderState.AwaitingConfirm;
			bool questionWaits = OrderController.HasPendingQuestion;

			if (planWaits)
			{
				j.usedController = true;
				RememberOrderIdAndTurn(j);
				j.routerLinesOverride = new List<string>(lastOrderRouterLines);
				if (word == "apply")
				{
					controller.ApplyAll();
				}
				else
				{
					controller.CancelPending();
				}
				FinishOrWait(j);
				return;
			}
			if (questionWaits)
			{
				RememberOrderIdAndTurn(j);
				if (word == "cancel")
				{
					OrderController.DropPendingQuestion();
					WriteOutbox(j, "cancelled", null);
				}
				else
				{
					WriteOutbox(j, "rejected", new List<string> { "A question is waiting: send the answer or cancel." });
				}
				return;
			}
			// A micro order's question (AgentMicro.cs): cancel answers it; apply does not.
			if (AgentMicro.HasQuestion)
			{
				if (word == "cancel")
				{
					AgentMicro.CancelQuestion();
					WriteOutbox(j, "cancelled", null);
				}
				else
				{
					WriteOutbox(j, "rejected", new List<string> { "A question is waiting: send the answer or cancel." });
				}
				return;
			}
			WriteOutbox(j, "rejected", new List<string> { "Nothing is waiting, so there is nothing to " + word + "." });
		}

		// An order file while a micro order's question waits (AgentMicro.cs). The pawn question: the text is the names,
		// sent on as the order's "with PAWNS" sentence (raw: it is a router sentence now); false = go on with that order.
		// The other-resources question: "mine" or "cancel". True = the file is answered.
		private static bool WorkMicroAnswer(Job j)
		{
			string answer = (j.text ?? "").Trim().TrimEnd('.').Trim().ToLowerInvariant();
			if (answer == "cancel")
			{
				AgentMicro.CancelQuestion();
				WriteOutbox(j, "cancelled", null);
				return true;
			}
			if (AgentMicro.QuestionIsPawns && AgentMicro.AreColonistNames(j.text))
			{
				j.text = AgentMicro.PawnAnswerSentence(j.text);
				j.raw = true;
				return false;
			}
			if (answer == "mine")
			{
				AgentMicro.AnswerMine();
				WriteOutbox(j, "applied", null);
				return true;
			}
			WriteOutbox(j, "rejected", new List<string> { "A question is waiting: send the answer or cancel." });
			return true;
		}

		// An order file against what is waiting.
		private static void WorkOrder(Job j)
		{
			if (controller.State == OrderState.AwaitingConfirm)
			{
				RememberOrderIdAndTurn(j);
				WriteOutbox(j, "rejected", new List<string> { "A plan is waiting: send apply or cancel." });
				return;
			}
			if (AgentMicro.HasQuestion && WorkMicroAnswer(j))
			{
				return;
			}

			OrderOptions options = new OrderOptions();
			options.raw = j.raw;
			options.dry = j.flags.Contains("dry");
			options.autoApply = j.flags.Contains("apply");   // no flag = wait

			string idBefore = OrderController.CurrentOrderId;
			int turnBefore = OrderController.CurrentTurn;
			lastOrderRouterLines = new List<string>();
			j.usedController = true;
			job = j;
			controller.Submit(j.text, options);
			// Read right after Submit. An early problem (no key, no map) never starts a turn: then the ids are unchanged.
			if (OrderController.CurrentOrderId != idBefore || OrderController.CurrentTurn != turnBefore)
			{
				j.orderId = OrderController.CurrentOrderId;
				j.turn = OrderController.CurrentTurn;
			}
			if (controller.State != OrderState.Sending && controller.State != OrderState.Applying)
			{
				job = null;
				FinishJob(j);
			}
		}

		private static void FinishOrWait(Job j)
		{
			if (controller.State != OrderState.Sending && controller.State != OrderState.Applying)
			{
				FinishJob(j);
			}
			else
			{
				job = j;
			}
		}

		private static void RememberOrderIdAndTurn(Job j)
		{
			j.orderId = OrderController.CurrentOrderId;
			j.turn = OrderController.CurrentTurn;
		}

		// Pause rule (user, 2026-10-13: "certain actions shouldnt pause the game. adding a bill or making a query
		// shouldnt stop the game"). Until this build every taken file paused the game and nothing unpaused it. Now the
		// inbox pauses only while it waits on a decision: a plan that needs apply/cancel, or a question that needs an
		// answer. A question file, a raw apply order (bill, schedule, priority, speed...) and a reply that ends the
		// wait never pause. When the wait ends (apply, cancel, a new order after the plan was dropped) the speed the
		// inbox took away comes back, if the game is still paused; a pause the player or a letter made since is kept.
		private static TimeSpeed? inboxPausedSpeed;

		// Pauses the way the space bar does (Verse\TickManager.cs:237-243) and remembers what to resume to.
		private static void PauseForWait(Job j)
		{
			TickManager tm = Find.TickManager;
			if (tm != null && tm.CurTimeSpeed != TimeSpeed.Paused)
			{
				inboxPausedSpeed = tm.CurTimeSpeed;
				tm.Pause();
				j.pausedByInbox = true;
			}
		}

		// A micro order's question that comes up while the game runs (AgentMicro.AskMore; user, 2026-10-16: "pause game,
		// ask agents"): paused as for a wait, and given back the same way once nothing waits.
		public static void PauseForQuestion()
		{
			TickManager tm = Find.TickManager;
			if (tm != null && tm.CurTimeSpeed != TimeSpeed.Paused)
			{
				inboxPausedSpeed = tm.CurTimeSpeed;
				tm.Pause();
			}
		}

		// Gives the speed back after a wait the inbox paused for; does nothing when the game runs already or when the
		// inbox never paused it.
		private static void RestoreAfterWait()
		{
			if (inboxPausedSpeed == null)
			{
				return;
			}
			TimeSpeed back = inboxPausedSpeed.Value;
			inboxPausedSpeed = null;
			// An open hourly check holds the pause until every agent has voted (AgentHourlyCheck.cs, 2026-10-16).
			if (AgentHourlyCheck.IsOpen)
			{
				return;
			}
			TickManager tm = Find.TickManager;
			if (tm != null && tm.CurTimeSpeed == TimeSpeed.Paused && back != TimeSpeed.Paused)
			{
				tm.CurTimeSpeed = back;   // what the speed buttons do (TickManager.cs:187-207); leaves pause
			}
		}

		// Called before every outbox file: pause while something waits, resume when nothing does.
		private static void PauseOrResumeForWaiting(Job j)
		{
			if (CurrentWaiting() != "none")
			{
				PauseForWait(j);
			}
			else
			{
				RestoreAfterWait();
			}
		}

		// The job's controller work is over: write its outbox file.
		private static void FinishJob(Job j)
		{
			string result = controller.LastResult;
			if (string.IsNullOrEmpty(result))
			{
				result = "problem";
			}
			List<string> routerLines = RouterLinesOf(j);
			if (j.kind == "order")
			{
				lastOrderRouterLines = routerLines;
			}
			WriteOutbox(j, result, null);
		}

		private static List<string> RouterLinesOf(Job j)
		{
			if (j.routerLinesOverride != null)
			{
				return j.routerLinesOverride;
			}
			if (j.raw)
			{
				return SplitSentences(j.text);
			}
			string reply = null;
			lock (noteLock)
			{
				foreach (HaikuNote n in haikuNotes)
				{
					if (n.turn == j.turn && n.orderId == j.orderId)
					{
						reply = n.reply;
					}
				}
			}
			return reply == null ? new List<string>() : SplitSentences(reply);
		}

		// Lines trimmed; blank lines and ``` lines dropped.
		private static List<string> SplitSentences(string text)
		{
			List<string> lines = new List<string>();
			if (text == null)
			{
				return lines;
			}
			foreach (string raw in text.Replace("\r", "").Split('\n'))
			{
				string line = raw.Trim();
				if (line.Length == 0 || line.StartsWith("```"))
				{
					continue;
				}
				lines.Add(line);
			}
			return lines;
		}

		// The window log without the lines another outbox field already holds ("[12h] answer: X" when X is in answers,
		// "plan: X", "note: X", "not done: X", ...) and without a line it had already (user, 2026-10-19: "no duplicates.").
		private static readonly Regex LogLabel = new Regex(@"^(answer|answering|question|cannot|not done|plan|note|refused): ", RegexOptions.Compiled);

		private static List<string> WithoutRepeats(List<string> log, IEnumerable<string> elsewhere)
		{
			HashSet<string> seen = new HashSet<string>(elsewhere.Where((string s) => s != null).Select((string s) => s.TrimEnd()), StringComparer.Ordinal);
			List<string> kept = new List<string>();
			foreach (string line in log)
			{
				string bare = LogPrefix.Replace(line ?? "", "").TrimEnd();
				string body = LogLabel.Replace(bare, "");
				if (seen.Contains(bare) || seen.Contains(body))
				{
					continue;
				}
				seen.Add(bare);
				kept.Add(line);
			}
			return kept;
		}

		private static JArray Arr(IEnumerable<string> items)
		{
			JArray a = new JArray();
			if (items != null)
			{
				foreach (string s in items)
				{
					a.Add(s);
				}
			}
			return a;
		}

		private static string CurrentWaiting()
		{
			if (controller != null && controller.State == OrderState.AwaitingConfirm)
			{
				return "confirm";
			}
			return OrderController.HasPendingQuestion || AgentMicro.HasQuestion ? "ask" : "none";
		}

		// reasonsOverride: the plain reason(s) of a file the inbox itself rejected; null for everything else.
		private static void WriteOutbox(Job j, string result, List<string> reasonsOverride)
		{
			// A micro order asked which pawns go (AgentMicro.Plan): the router saw a refusal, the agent gets the question.
			bool microAsked = result == "failed" && AgentMicro.HasQuestion && AgentMicro.AskCount != j.microAsks;
			if (microAsked)
			{
				result = "ask";
			}
			PauseOrResumeForWaiting(j);
			string waiting = CurrentWaiting();
			bool fromController = j.usedController || waiting == "confirm";
			RouteOutcome outcome = j.usedController ? controller.LastOutcome : null;

			List<string> answers = new List<string>();
			List<string> reasons = new List<string>();
			string question = null;
			if (reasonsOverride != null)
			{
				reasons.AddRange(reasonsOverride);
			}
			else if (outcome != null)
			{
				if (result == "answer")
				{
					foreach (string a in outcome.lines)
					{
						foreach (string line in a.Replace("\r", "").Split('\n'))
						{
							if (line.Trim().Length > 0)
							{
								answers.Add(line.TrimEnd());
							}
						}
					}
				}
				else if (result == "failed" || result == "problem" || result == "cannot")
				{
					foreach (string r in outcome.lines)
					{
						if (r != null && r.Trim().Length > 0)
						{
							reasons.Add(r);
						}
					}
				}
				else if (result == "ask")
				{
					question = outcome.question;
				}
			}
			if (result == "problem" && outcome == null && reasonsOverride == null && j.usedController)
			{
				for (int i = j.logStart; i < controller.Log.Count; i++)
				{
					reasons.Add(LogPrefix.Replace(controller.Log[i], ""));
				}
			}
			if (waiting == "ask" && question == null)
			{
				question = OrderController.PendingQuestionText ?? AgentMicro.QuestionText;
			}
			// The question stays in the question field only (user, 2026-10-19: "fix it. no duplicates."); it used to go into
			// answers and reasons too, when the agent printed only those (the play hook has the whole result read since).

			List<string> planLines = fromController ? new List<string>(controller.LastPlanLines) : new List<string>();
			List<string> planNotes = fromController ? new List<string>(controller.LastPlanNotes) : new List<string>();
			bool risky = planLines.Any((string l) => l != null && l.IndexOf("RISKY", StringComparison.OrdinalIgnoreCase) >= 0);

			JArray actions = new JArray();
			if (j.usedController)
			{
				foreach (JObject action in controller.LastActions)
				{
					actions.Add(action.DeepClone());
				}
			}

			List<string> windowLog = new List<string>();
			if (j.usedController)
			{
				for (int i = j.logStart; i < controller.Log.Count; i++)
				{
					windowLog.Add(controller.Log[i]);
				}
			}

			// Nothing went to the parser for a rejected file.
			List<string> routerLines = result != "rejected" && (j.kind == "order" || j.routerLinesOverride != null) ? RouterLinesOf(j) : new List<string>();
			// Tip injection (TipInjector.cs): matched on what was sent, its router and plan lines, and new events.
			// The answer to "i need tips on X" is a whole know-how file: it is not matched, or every tip it mentions would come.
			bool readTips = (j.text ?? "").IndexOf("need tips", StringComparison.OrdinalIgnoreCase) >= 0;
			// Settings "Agent tips" off (user, 2026-10-12): no tips at all on results; the explicit "i need tips on X" form is untouched.
			VerbalCommandsSettings tipSettings = VerbalCommandsMod.Settings;
			List<string> tips = tipSettings != null && !tipSettings.agentTips
				? new List<string>()
				: TipInjector.ForResult(j.text, readTips ? routerLines.Concat(planLines) : routerLines.Concat(planLines).Concat(answers));
			// Tips and reminders are in their own fields only, each line once in a result (user, 2026-10-19: "fix it. no
			// duplicates."). They also went into answers and reasons while the agent printed chosen fields only (user,
			// 2026-10-06: "bake it into answers/result"), and send.ps1 printed them a fourth time; the play hook
			// (rimworld_play_scripts.py) has the whole result read since 2026-10-16, so one copy reaches the agent.
			// The orchestrator's 6-hour work spot reminder (AgentReminderScan.cs, option B): a batch goes out once, on the
			// first result with an orchestrator-only action. Not a tip: no gap, no cap, not under the "Agent tips" setting.
			List<string> reminders = AgentReminderScan.TakeFor(actions);
			// Micro orders (AgentMicro.cs): what a finished order said, and a question that came up, with the next result
			// of any order (user, 2026-10-16, answer "9 A").
			reminders.AddRange(AgentMicro.TakeMessages());
			// Roof only orders (AgentRoofOnly.cs): the same, for a finished order.
			reminders.AddRange(AgentRoofOnly.TakeMessages());
			// The research bench rule (ResearchBenchRule.cs): a bench moved instead of a second one, or not built.
			reminders.AddRange(ResearchBenchRule.TakeMessages());
			// The outdoor battery rule (BatteryOutdoorRule.cs): moved indoors, or not built.
			reminders.AddRange(BatteryOutdoorRule.TakeMessages());
			// The open hourly check is not on results (user, 2026-10-19: "it would be REMINDER list that would get spammed until
			// they vote, which autopause game, so it shouldnt even be spamming to them."): watch.ps1 and wait_round.ps1 hand
			// it out once per check (AgentHourlyCheck.cs).

			JObject o = new JObject();
			o["file"] = j.file;
			o["kind"] = j.kind;
			o["mode"] = Arr(j.flags);
			o["text"] = j.text ?? "";
			o["order_id"] = j.orderId ?? "";
			o["turn"] = j.turn;
			// No line twice (user, 2026-10-19: "fix it. no duplicates."): router_lines only when they differ from the text
			// sent (in raw mode they are the text, split), game_start only when the game moved, and window_log without the
			// lines another field already holds.
			if (!routerLines.SequenceEqual(SplitSentences(j.text ?? "")))
			{
				o["router_lines"] = Arr(routerLines);
			}
			o["result"] = result;
			o["answers"] = Arr(answers);
			o["plan_lines"] = Arr(planLines);
			o["notes"] = Arr(planNotes);
			o["risky"] = risky;
			o["actions"] = actions;
			o["question"] = question;
			o["reasons"] = Arr(reasons);
			o["waiting"] = waiting;
			o["paused_by_inbox"] = j.pausedByInbox;
			JToken gameEnd = GameInfo();
			if (j.gameStart == null || !JToken.DeepEquals(j.gameStart, gameEnd))
			{
				o["game_start"] = j.gameStart ?? new JObject();
			}
			o["game_end"] = gameEnd;
			o["window_log"] = Arr(WithoutRepeats(windowLog, answers.Concat(reasons).Concat(planLines).Concat(planNotes)
				.Concat(tips).Concat(reminders).Concat(new string[] { question })));
			o["tips"] = Arr(tips);
			o["reminders"] = Arr(reminders);
			o["utc"] = DateTime.UtcNow.ToString("o");

			string baseName = Path.GetFileNameWithoutExtension(j.file);
			WriteJsonAtomic(Path.Combine(outboxDir, baseName + ".json"), o);
			// Token meter: the agent's numbers are read now that the order is done (AgentContext.cs).
			AgentContext.NoteOutbox(j.file);
		}

		// Written when something in it changes (an order starts or is done, a plan or question waits, the game pauses
		// or runs, the token meter's numbers), not on a timer (user, 2026-10-05: "just write it whenever functions are
		// done? instead of writing every 5s?" / "get rid of that useless "every 5s" detect"). Before: also every 5 s
		// (StateSeconds), which send.ps1 read as "the game is running"; send.ps1 now checks the game's process instead.
		private static void WriteStateIfChanged()
		{
			string waiting = CurrentWaiting();
			string question = waiting == "ask" ? OrderController.PendingQuestionText ?? AgentMicro.QuestionText : null;
			TickManager tm = Find.TickManager;
			bool paused = tm != null && tm.Paused;
			string speed = tm != null ? tm.CurTimeSpeed.ToString() : null;
			string currentFile = job != null ? job.file : null;
			JObject meter = AgentContext.StateFields();
			// User 2026-10-12 ("inbox_state.json's new messages get injected as prompt onto agent, not based on -run or
			// based on whether they paused/changed speed"): a new event (AgentEvents: letters, messages, pawn states)
			// rewrites the file too, and the file carries the last events, so a watcher outside the game
			// (the rimworld-play skill's watch.ps1, armed with the agent's Monitor tool) can hand each new one to the agent.
			int lastEventSeq = AgentEvents.LastSeq;
			string key = string.Join("|", new string[] { job != null ? "busy" : "idle", currentFile ?? "", waiting, question ?? "", paused ? "p" : "r", speed ?? "", meter.ToString(Formatting.None), lastEventSeq.ToString(), AgentHourlyCheck.StateKey() });
			if (key == lastStateKey)
			{
				return;
			}
			lastStateKey = key;

			JObject g = GameInfo();
			g.Remove("speed");
			JObject o = new JObject();
			o["enabled"] = true;
			o["map_loaded"] = true;
			o["busy"] = job != null;
			o["current_file"] = currentFile;
			o["waiting"] = waiting;
			o["question"] = question;
			o["paused"] = paused;
			o["speed"] = speed;
			o["game"] = g;
			o["token_meter"] = meter;
			o["last_event_seq"] = lastEventSeq;
			o["events"] = RecentEvents(lastEventSeq);
			// The open hourly check, for watch.ps1 (orchestrator) and wait_round.ps1 (specialists); null when none.
			o["hourly"] = AgentHourlyCheck.StateField();
			o["utc"] = DateTime.UtcNow.ToString("o");
			WriteJsonAtomic(statePath, o);
		}

		// The newest events (at most StateEvents), oldest first: seq, when, kind, text. A watcher keeps the last seq it
		// handed on and reports only entries above it. Never throws.
		private const int StateEvents = 20;
		private static JArray RecentEvents(int lastSeq)
		{
			JArray a = new JArray();
			try
			{
				List<AgentEvents.Entry> list = AgentEvents.Since(lastSeq - StateEvents);
				for (int i = 0; i < list.Count; i++)
				{
					AgentEvents.Entry e = list[i];
					JObject o = new JObject();
					o["seq"] = e.seq;
					o["tick"] = e.tick;
					o["when"] = e.when;
					o["kind"] = e.kind;
					o["text"] = e.text;
					a.Add(o);
				}
			}
			catch (Exception)
			{
			}
			return a;
		}

		private static JObject GameInfo()
		{
			JObject o = new JObject();
			try
			{
				TickManager tm = Find.TickManager;
				Map map = Find.CurrentMap;
				o["tick"] = tm.TicksGame;                                   // Verse\TickManager.cs:53
				o["day"] = GenDate.DaysPassed;                              // RimWorld\GenDate.cs:43
				if (map != null)
				{
					o["hour"] = GenLocalDate.HourOfDay(map);                // RimWorld\GenLocalDate.cs:16
					o["date"] = GenDate.DateFullStringAt(GenTicks.TicksAbs, Find.WorldGrid.LongLatOf(map.Tile)); // GenDate.cs:175, WorldGrid.cs:174
				}
				else
				{
					o["hour"] = null;
					o["date"] = null;
				}
				o["speed"] = tm.CurTimeSpeed.ToString();                    // Verse\TickManager.cs:187
			}
			catch (Exception ex)
			{
				Warn("the game's date could not be read: " + ex.Message);
			}
			return o;
		}

		// <name>.json.tmp, delete an old <name>.json, then move: a reader never sees half a file.
		private static void WriteJsonAtomic(string path, JObject o)
		{
			try
			{
				string text = RouterLog.Redact(o.ToString(Formatting.Indented));
				string tmp = path + ".tmp";
				if (File.Exists(tmp))
				{
					File.Delete(tmp);
				}
				File.WriteAllBytes(tmp, Utf8NoBom.GetBytes(text));
				if (File.Exists(path))
				{
					File.Delete(path);
				}
				File.Move(tmp, path);
			}
			catch (Exception ex)
			{
				Warn("could not write " + Path.GetFileName(path) + ": " + ex.Message);
			}
		}

		// Moves the file into taken\ (adding _<utc ticks> before .txt when that name is taken). False if it could not move.
		private static bool MoveToTaken(string fullPath, string name)
		{
			try
			{
				string dest = Path.Combine(takenDir, name);
				if (File.Exists(dest))
				{
					dest = Path.Combine(takenDir, Path.GetFileNameWithoutExtension(name) + "_" + DateTime.UtcNow.Ticks + ".txt");
				}
				File.Move(fullPath, dest);
				return true;
			}
			catch (Exception ex)
			{
				Warn("could not move " + name + " to the taken folder: " + ex.Message);
				return false;
			}
		}

		// Plain Player.log warning; each distinct message once.
		private static void Warn(string message)
		{
			if (warned.Count < 200 && warned.Add(message))
			{
				Verse.Log.Warning(WarnPrefix + message);
			}
		}
	}
}
