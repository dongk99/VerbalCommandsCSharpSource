using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VerbalCommands
{
	// Token meter (user, 2026-10-05: "first i need a token counter that will open whenever an llm agent is detected
	// playing the game (there is a tickbox function so gating function can use this)"; "I just need context size of that
	// session. input/output/cache is useful but not necessary"; "have one for claude code and codex"; "Turns on whenever
	// agent inbox is on. (no, do not open a VISIBLE window. change.)"; "just write it whenever functions are done?
	// instead of writing every 5s?"). Two numbers, written into inbox_state.json by AgentInbox; no window:
	// - the playing agent's context: the input size of its session's latest model call (Claude Code or Codex);
	// - the router model's context on its latest call (RouterGame, from the API reply's usage).
	//
	// Read when an order is done (AgentInbox wrote its outbox file), off the main thread; nothing runs on a timer.
	// Which session plays: send.ps1 names each file <model>-<harness>-<mm-hh-dd-mm-yyyy>-<session id, 8>_<number>.txt
	// (user, 2026-10-05: "make it so that new sessions have different outbox file name. probably
	// modelname-harness-mm-hh-dd-mm-yyyy format", session id added on the user's choice), so the session's transcript is
	// opened directly. Any other name is looked for in the recent transcripts (the agent reads its result, so the name
	// lands in its own transcript; it may only be there by the next order). Only reads files.
	//
	// Where each application keeps its sessions:
	// - Claude Code: %USERPROFILE%\.claude\projects\<project>\<session>.jsonl (CLAUDE_CONFIG_DIR moves .claude). Context =
	//   usage.input_tokens + cache_read_input_tokens + cache_creation_input_tokens of the last assistant line. Checked
	//   in this machine's transcripts, 2026-10-05.
	// - Codex: %USERPROFILE%\.codex\sessions\YYYY\MM\DD\rollout-*.jsonl (CODEX_HOME moves .codex). Context =
	//   payload.info.last_token_usage.input_tokens of the last token_count event (OpenAI's input_tokens include the
	//   cached part); payload.info.model_context_window when given. NOT checked: Codex is not installed on this machine
	//   and this format is from memory.
	public static class AgentContext
	{
		public enum App
		{
			None,
			ClaudeCode,
			Codex
		}

		public sealed class Snapshot
		{
			public App app;
			public string sessionPath;
			public long contextTokens = -1;
			public long contextWindow = -1;   // Codex only, when its transcript gives it
			public string routerModel;
			public long routerTokens = -1;
			public long compactions;          // tip injection: goes up when the agent forgot (compaction, other session)
		}

		private const string WarnPrefix = "[VerbalCommands] token meter: ";
		private static readonly TimeSpan RecentTranscript = TimeSpan.FromMinutes(15);
		private const int MatchTailBytes = 512 * 1024;
		private const int UsageTailBytes = 2 * 1024 * 1024;
		private static readonly Regex SessionName = new Regex(@"-(claudecode|codex)-\d{2}-\d{2}-\d{2}-\d{2}-\d{4}-([0-9a-f]{8})_\d+", RegexOptions.Compiled);

		private static readonly object gate = new object();
		private static readonly object readLock = new object();
		private static readonly HashSet<string> warned = new HashSet<string>();
		private static string unmatchedName;   // an earlier name not found yet; tried again with the next order
		private static App lockedApp;
		private static string lockedPath;
		private static long contextTokens = -1, contextWindow = -1;
		private static string routerModel;
		private static long routerTokens = -1;
		// Tip injection (TipInjector.cs): counts the times the agent lost its context. Claude Code writes a
		// "compact_boundary" line at each compaction (seen in this machine's transcripts, 2026-10-06: "subtype":
		// "compact_boundary" with compactMetadata preTokens/postTokens); Codex: not known, so a drop of the context
		// to under half counts too. A different session counts as well.
		private static long compactions;
		private static string lastCompactMark;

		public static Snapshot Current
		{
			get
			{
				lock (gate)
				{
					Snapshot s = new Snapshot();
					s.app = lockedApp;
					s.sessionPath = lockedPath;
					s.contextTokens = contextTokens;
					s.contextWindow = contextWindow;
					s.routerModel = routerModel;
					s.routerTokens = routerTokens;
					s.compactions = compactions;
					return s;
				}
			}
		}

		// Main thread: AgentInbox wrote outbox\<name minus .txt>.json for the inbox file `name`. The agent's numbers are
		// read in the background; AgentInbox writes them with the next state.
		public static void NoteOutbox(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return;
			}
			Task.Run(delegate { ReadAfterOrder(name); });
		}

		// Main thread: AgentInbox took inbox\<name>. Read now as well, so the tips chosen for this order's result see the
		// agent's previous call (finished and written by then) and a compaction before it.
		public static void NoteTake(string name)
		{
			NoteOutbox(name);
		}

		// Any thread: the router model answered. usage is the reply's "usage" object (Anthropic Messages, the Claude Code
		// CLI's JSON envelope, or OpenAI chat completions).
		public static void NoteRouterReply(string model, JToken usage)
		{
			long tokens = UsageContext(usage);
			if (tokens < 0)
			{
				return;
			}
			lock (gate)
			{
				routerModel = model;
				routerTokens = tokens;
			}
		}

		// Anthropic: input_tokens + cache_read_input_tokens + cache_creation_input_tokens (input_tokens alone is only
		// the part after the last cache breakpoint). OpenAI chat completions: prompt_tokens, which includes the cached part.
		public static long UsageContext(JToken usage)
		{
			JObject u = usage as JObject;
			if (u == null)
			{
				return -1;
			}
			if (u["input_tokens"] != null)
			{
				return Num(u, "input_tokens") + Num(u, "cache_read_input_tokens") + Num(u, "cache_creation_input_tokens");
			}
			if (u["prompt_tokens"] != null)
			{
				return Num(u, "prompt_tokens");
			}
			return -1;
		}

		// The numbers for inbox_state.json (null where not known yet).
		public static JObject StateFields()
		{
			Snapshot s = Current;
			JObject o = new JObject();
			o["agent"] = s.app == App.ClaudeCode ? "claude-code" : (s.app == App.Codex ? "codex" : null);
			o["agent_session"] = s.sessionPath != null ? SessionId(s.sessionPath) : null;
			o["agent_context_tokens"] = s.contextTokens >= 0 ? (JToken)s.contextTokens : JValue.CreateNull();
			o["agent_context_window"] = s.contextWindow > 0 ? (JToken)s.contextWindow : JValue.CreateNull();
			o["router_model"] = s.routerModel;
			o["router_context_tokens"] = s.routerTokens >= 0 ? (JToken)s.routerTokens : JValue.CreateNull();
			return o;
		}

		// ---- background ------------------------------------------------------------------------------------------

		private static void ReadAfterOrder(string name)
		{
			try
			{
				lock (readLock)
				{
					App app;
					string path;
					string earlier;
					lock (gate)
					{
						app = lockedApp;
						path = lockedPath;
						earlier = unmatchedName;
					}
					App foundApp;
					string foundPath;
					bool found = FindSession(name, app, path, out foundApp, out foundPath);
					if (!found && earlier != null && earlier != name)
					{
						found = FindSession(earlier, app, path, out foundApp, out foundPath);
					}
					lock (gate)
					{
						unmatchedName = found ? null : name;
						if (found)
						{
							if (lockedPath != null && lockedPath != foundPath)
							{
								compactions++;   // another session plays now
								lastCompactMark = null;
							}
							lockedApp = foundApp;
							lockedPath = foundPath;
						}
						app = lockedApp;
						path = lockedPath;
					}
					if (path == null || !File.Exists(path))
					{
						return;
					}
					long tokens, window;
					string mark = null;
					bool read = app == App.Codex ? ReadCodex(path, out tokens, out window) : ReadClaude(path, out tokens, out window, out mark);
					if (read)
					{
						lock (gate)
						{
							if (lockedPath == path)
							{
								// A new compact_boundary line, or the context fell to under half (Codex has no known mark).
								bool compacted = mark != null && lastCompactMark != null && mark != lastCompactMark;
								bool dropped = contextTokens > 0 && tokens < contextTokens / 2;
								if (compacted || dropped)
								{
									compactions++;
								}
								if (mark != null)
								{
									lastCompactMark = mark;
								}
								contextTokens = tokens;
								contextWindow = window;
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				Warn("reading the agent's session failed: " + ex.Message);
			}
		}

		// The session whose transcript holds `name`: the locked one first, then the newest recent Claude Code or Codex
		// transcript (a folder that does not exist is skipped).
		private static bool FindSession(string name, App app, string path, out App foundApp, out string foundPath)
		{
			foundApp = App.None;
			foundPath = null;
			Match named = SessionName.Match(name);
			if (named.Success)
			{
				App want = named.Groups[1].Value == "codex" ? App.Codex : App.ClaudeCode;
				string file = TranscriptOf(want, named.Groups[2].Value);
				if (file != null)
				{
					foundApp = want;
					foundPath = file;
					return true;
				}
			}
			if (path != null && TailContains(path, name))
			{
				foundApp = app;
				foundPath = path;
				return true;
			}
			List<KeyValuePair<App, FileInfo>> candidates = new List<KeyValuePair<App, FileInfo>>();
			DateTime cutoff = DateTime.UtcNow - RecentTranscript;
			{
				string projects = Path.Combine(ConfigRoot("CLAUDE_CONFIG_DIR", ".claude"), "projects");
				if (Directory.Exists(projects))
				{
					foreach (DirectoryInfo dir in new DirectoryInfo(projects).GetDirectories())
					{
						foreach (FileInfo f in dir.GetFiles("*.jsonl"))
						{
							if (f.LastWriteTimeUtc >= cutoff)
							{
								candidates.Add(new KeyValuePair<App, FileInfo>(App.ClaudeCode, f));
							}
						}
					}
				}
			}
			{
				string sessions = Path.Combine(ConfigRoot("CODEX_HOME", ".codex"), "sessions");
				if (Directory.Exists(sessions))
				{
					foreach (FileInfo f in new DirectoryInfo(sessions).GetFiles("rollout-*.jsonl", SearchOption.AllDirectories))
					{
						if (f.LastWriteTimeUtc >= cutoff)
						{
							candidates.Add(new KeyValuePair<App, FileInfo>(App.Codex, f));
						}
					}
				}
			}
			candidates.Sort((a, b) => b.Value.LastWriteTimeUtc.CompareTo(a.Value.LastWriteTimeUtc));
			foreach (KeyValuePair<App, FileInfo> c in candidates)
			{
				if (c.Value.FullName == path)
				{
					continue;
				}
				if (TailContains(c.Value.FullName, name))
				{
					foundApp = c.Key;
					foundPath = c.Value.FullName;
					return true;
				}
			}
			return false;
		}

		// The newest transcript whose session id starts with sid8: Claude Code <id>.jsonl, Codex rollout-<time>-<id>.jsonl.
		private static string TranscriptOf(App app, string sid8)
		{
			FileInfo best = null;
			if (app == App.ClaudeCode)
			{
				string projects = Path.Combine(ConfigRoot("CLAUDE_CONFIG_DIR", ".claude"), "projects");
				if (Directory.Exists(projects))
				{
					foreach (DirectoryInfo dir in new DirectoryInfo(projects).GetDirectories())
					{
						foreach (FileInfo f in dir.GetFiles(sid8 + "*.jsonl"))
						{
							if (best == null || f.LastWriteTimeUtc > best.LastWriteTimeUtc)
							{
								best = f;
							}
						}
					}
				}
			}
			else
			{
				string sessions = Path.Combine(ConfigRoot("CODEX_HOME", ".codex"), "sessions");
				if (Directory.Exists(sessions))
				{
					foreach (FileInfo f in new DirectoryInfo(sessions).GetFiles("rollout-*-" + sid8 + "*.jsonl", SearchOption.AllDirectories))
					{
						if (best == null || f.LastWriteTimeUtc > best.LastWriteTimeUtc)
						{
							best = f;
						}
					}
				}
			}
			return best != null ? best.FullName : null;
		}

		// mark: the uuid of the last compact_boundary line in the tail (null when none is in it).
		private static bool ReadClaude(string path, out long tokens, out long window, out string mark)
		{
			tokens = -1;
			window = -1;
			mark = null;
			string[] lines = ReadTail(path, UsageTailBytes).Split('\n');
			for (int i = lines.Length - 1; i >= 0; i--)
			{
				if (lines[i].IndexOf("\"compact_boundary\"", StringComparison.Ordinal) >= 0)
				{
					JObject c = TryParse(lines[i]);
					if (c != null && (string)c["subtype"] == "compact_boundary")
					{
						mark = (string)c["uuid"] ?? (string)c["timestamp"] ?? lines[i].GetHashCode().ToString();
						break;
					}
				}
			}
			for (int i = lines.Length - 1; i >= 0; i--)
			{
				string line = lines[i];
				if (line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0 || line.IndexOf("\"assistant\"", StringComparison.Ordinal) < 0)
				{
					continue;
				}
				JObject o = TryParse(line);
				if (o == null || (string)o["type"] != "assistant" || (bool?)o["isSidechain"] == true)
				{
					continue;
				}
				long sum = UsageContext(o["message"] != null ? o["message"]["usage"] : null);
				if (sum > 0)   // an error placeholder line carries a usage of all zeros
				{
					tokens = sum;
					return true;
				}
			}
			return false;
		}

		private static bool ReadCodex(string path, out long tokens, out long window)
		{
			tokens = -1;
			window = -1;
			string[] lines = ReadTail(path, UsageTailBytes).Split('\n');
			for (int i = lines.Length - 1; i >= 0; i--)
			{
				string line = lines[i];
				if (line.IndexOf("token_count", StringComparison.Ordinal) < 0)
				{
					continue;
				}
				JObject o = TryParse(line);
				JObject payload = o != null ? o["payload"] as JObject : null;
				if (payload == null || (string)payload["type"] != "token_count")
				{
					continue;
				}
				JObject info = payload["info"] as JObject;
				JObject last = info != null ? info["last_token_usage"] as JObject : null;
				long input = Num(last ?? payload, "input_tokens");
				if (input > 0)
				{
					tokens = input;
					window = info != null ? Num(info, "model_context_window") : -1;
					if (window == 0)
					{
						window = -1;
					}
					return true;
				}
			}
			return false;
		}

		// ---- helpers ---------------------------------------------------------------------------------------------

		private static string ConfigRoot(string envName, string folderName)
		{
			string root = Environment.GetEnvironmentVariable(envName);
			if (string.IsNullOrEmpty(root))
			{
				root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), folderName);
			}
			return root;
		}

		private static bool TailContains(string path, string text)
		{
			try
			{
				return ReadTail(path, MatchTailBytes).IndexOf(text, StringComparison.Ordinal) >= 0;
			}
			catch (IOException)
			{
				return false;
			}
			catch (UnauthorizedAccessException)
			{
				return false;
			}
		}

		// The last maxBytes of the file, from the first whole line. The application keeps writing it, so it is shared.
		private static string ReadTail(string path, int maxBytes)
		{
			using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
			{
				long start = Math.Max(0, fs.Length - maxBytes);
				fs.Seek(start, SeekOrigin.Begin);
				byte[] buf = new byte[fs.Length - start];
				int read = 0;
				while (read < buf.Length)
				{
					int n = fs.Read(buf, read, buf.Length - read);
					if (n <= 0)
					{
						break;
					}
					read += n;
				}
				string text = Encoding.UTF8.GetString(buf, 0, read);
				if (start > 0)
				{
					int nl = text.IndexOf('\n');
					text = nl >= 0 ? text.Substring(nl + 1) : "";
				}
				return text;
			}
		}

		private static JObject TryParse(string line)
		{
			try
			{
				return JObject.Parse(line);
			}
			catch (JsonException)
			{
				return null;   // a line cut by the tail start, or one still being written
			}
		}

		private static long Num(JObject o, string key)
		{
			JToken t = o != null ? o[key] : null;
			if (t == null || (t.Type != JTokenType.Integer && t.Type != JTokenType.Float))
			{
				return 0;
			}
			return (long)t;
		}

		// Claude Code names the file <session uuid>.jsonl, Codex rollout-<time>-<uuid>.jsonl.
		public static string SessionId(string path)
		{
			string name = Path.GetFileNameWithoutExtension(path ?? "");
			if (name.Length >= 36 && name.StartsWith("rollout-", StringComparison.Ordinal))
			{
				name = name.Substring(name.Length - 36);
			}
			return name;
		}

		// Plain Player.log warning; each distinct message once. Any thread.
		private static void Warn(string message)
		{
			bool first;
			lock (gate)
			{
				first = warned.Count < 50 && warned.Add(message);
			}
			if (first)
			{
				MainThread.Post(delegate { Verse.Log.Warning(WarnPrefix + message); });
			}
		}
	}
}
