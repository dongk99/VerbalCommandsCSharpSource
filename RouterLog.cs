using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Verse;

namespace VerbalCommands
{
	// Brief 29: the mod's own log of what was sent to the small model, what came back, and what was done in the
	// game. One file per game launch: <SaveDataFolderPath>\VerbalCommands\logs\router_<yyyy-MM-dd_HHmmss>.jsonl,
	// one JSON object per line, created on the first record; the newest 20 files are kept.
	//
	// Folder rule as TranscriptLogger.cs:41-46: GenFilePaths.SaveDataFolderPath (Verse\GenFilePaths.cs:93-101) is
	// the "RimWorld by Ludeon Studios" folder; GenFilePaths.ConfigFolderPath (:179) is a sibling of it, so nothing
	// here is ever under Config.
	//
	// Writing: every record goes onto one queue and ONE background thread writes them in order (no Task.Run per
	// line, so lines cannot reorder). A failure is logged once and never reaches an order. The API key, auth
	// headers and environment are never passed in; Redact also blanks anything that looks like sk-ant-....
	public static class RouterLog
	{
		private const string LogPrefix = "[VerbalCommands] router log: ";
		private const int KeepFiles = 20;
		private static readonly Regex KeyLike = new Regex(@"sk-ant-[A-Za-z0-9_\-]+", RegexOptions.Compiled);
		private static readonly Regex OtherKeyLike = new Regex(@"\bsk-(?!ant-\[redacted\])[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled);

		// Which order and which turn a record belongs to. Immutable: a late background result keeps the ids of the
		// order that started it.
		public sealed class Ctx
		{
			public readonly string orderId;
			public readonly int turn;

			public Ctx(string orderId, int turn)
			{
				this.orderId = orderId;
				this.turn = turn;
			}
		}

		private static volatile bool enabled;
		// Set by Configure on the main thread (GenFilePaths may touch UnityEngine.Application).
		private static string logDir;
		private static string promptPath;
		private static string modelFilePath;

		private static readonly object startLock = new object();
		private static BlockingCollection<Action> queue;
		private static Thread writer;

		// Writer thread only from here down.
		private static string filePath;
		private static bool fileTried;
		private static string lastPromptHash;
		private static bool loggedFailureOnce;

		// Main thread. Called at the start of every order (and by Clear conversation).
		public static void Configure(bool saveRouterLog, string promptFilePath, string routerModelFilePath)
		{
			configured = false;
			enabled = saveRouterLog || ForceOn;
			if (!enabled)
			{
				return;
			}
			try
			{
				logDir = Path.Combine(GenFilePaths.SaveDataFolderPath, "VerbalCommands", "logs");
				promptPath = promptFilePath;
				modelFilePath = routerModelFilePath;
				configured = !string.IsNullOrEmpty(logDir);
			}
			catch (Exception ex)
			{
				enabled = false;
				configured = false;
				LogFailureOnce(ex);
			}
		}

		// Agent 02: set while the agent inbox is on; agent 10 wires it to AgentInbox.Enabled. The log is then on
		// whatever the saveRouterLog setting says.
		public static volatile bool ForceOn;

		private static volatile bool configured;

		// Agent 02: true once Configure ran with the log on and a folder set; false after a failure (or when the
		// log is off).
		public static bool Configured
		{
			get { return configured && enabled; }
		}

		public static bool Enabled
		{
			get { return enabled; }
		}

		// Any thread. fields may be null.
		public static void Write(Ctx ctx, string kind, JObject fields)
		{
			if (!enabled)
			{
				return;
			}
			try
			{
				JObject o = new JObject();
				o["utc"] = DateTime.UtcNow.ToString("o");
				o["kind"] = kind;
				o["order_id"] = ctx != null ? ctx.orderId : "";
				o["turn"] = ctx != null ? ctx.turn : 0;
				AgentTime.FillGameTime(o);
				if (fields != null)
				{
					foreach (JProperty p in fields.Properties().ToList())
					{
						o[p.Name] = p.Value;
					}
				}
				Enqueue(delegate { AppendLine(ctx, o); });
			}
			catch (Exception ex)
			{
				LogFailureOnce(ex);
			}
		}

		// Any thread. Called before each model call: when the system prompt differs from the last one written
		// (the file was reloaded), a system_prompt record goes in first. Compared on the writer thread, so it
		// sits in order with the records around it.
		public static void NotePrompt(Ctx ctx, string systemPrompt)
		{
			if (!enabled)
			{
				return;
			}
			try
			{
				string hash = Sha256(systemPrompt);
				string text = systemPrompt;
				Enqueue(delegate
				{
					EnsureFile(ctx);
					if (filePath == null || hash == lastPromptHash)
					{
						return;
					}
					lastPromptHash = hash;
					JObject o = Header(ctx, "system_prompt");
					o["system_prompt_sha256"] = hash;
					o["system_prompt"] = text;
					AppendRaw(o);
				});
			}
			catch (Exception ex)
			{
				LogFailureOnce(ex);
			}
		}

		public static string Sha256(string text)
		{
			using (SHA256 sha = SHA256.Create())
			{
				byte[] hash = sha.ComputeHash(new UTF8Encoding(false).GetBytes(text ?? ""));
				StringBuilder sb = new StringBuilder();
				foreach (byte b in hash)
				{
					sb.Append(b.ToString("x2"));
				}
				return sb.ToString();
			}
		}

		public static string Redact(string text)
		{
			if (text == null)
			{
				return null;
			}
			// Model-agnostic calls: other providers' keys (sk-..., sk-or-..., sk-proj-...) as well as Anthropic's.
			return OtherKeyLike.Replace(KeyLike.Replace(text, "sk-ant-[redacted]"), "sk-[redacted]");
		}

		// What the mod knows about a failure: for the log only, never shown to the player.
		public static JObject DescribeError(Exception ex)
		{
			JObject o = new JObject();
			if (ex == null)
			{
				return o;
			}
			o["error_type"] = ex.GetType().Name;
			o["error"] = ex.Message;
			o["timed_out"] = ex is TimeoutException;
			o["cancelled"] = ex is OperationCanceledException;
			HeadlessFailure headless = ex as HeadlessFailure;
			if (headless != null)
			{
				if (headless.exitCode.HasValue)
				{
					o["exit_code"] = headless.exitCode.Value;
				}
				o["stderr"] = headless.stderr;
				o["cli_text"] = headless.cliText;
			}
			AnthropicApiException api = ex as AnthropicApiException;
			if (api != null)
			{
				o["http_status"] = api.statusCode;
				o["api_error_type"] = api.errorType;
				o["api_error_message"] = api.errorMessage;
				o["request_id"] = api.requestId;
				o["raw_body"] = api.rawBody;
			}
			return o;
		}

		// ---- writer ---------------------------------------------------------------------------------

		private static void Enqueue(Action work)
		{
			lock (startLock)
			{
				if (queue == null)
				{
					queue = new BlockingCollection<Action>();
					writer = new Thread(WriterLoop);
					writer.IsBackground = true;
					writer.Name = "VerbalCommands router log";
					writer.Start();
				}
			}
			queue.Add(work);
		}

		private static void WriterLoop()
		{
			foreach (Action work in queue.GetConsumingEnumerable())
			{
				try
				{
					work();
				}
				catch (Exception ex)
				{
					LogFailureOnce(ex);
				}
			}
		}

		private static JObject Header(Ctx ctx, string kind)
		{
			JObject o = new JObject();
			o["utc"] = DateTime.UtcNow.ToString("o");
			o["kind"] = kind;
			o["order_id"] = ctx != null ? ctx.orderId : "";
			o["turn"] = ctx != null ? ctx.turn : 0;
			AgentTime.FillGameTime(o);
			return o;
		}

		private static void AppendLine(Ctx ctx, JObject o)
		{
			EnsureFile(ctx);
			if (filePath == null)
			{
				return;
			}
			AppendRaw(o);
		}

		private static void AppendRaw(JObject o)
		{
			string line = Redact(o.ToString(Formatting.None));
			File.AppendAllText(filePath, line + "\n", new UTF8Encoding(false));
		}

		// Writer thread. Creates the file (and the session record) once, on the first record.
		private static void EnsureFile(Ctx ctx)
		{
			if (fileTried)
			{
				return;
			}
			fileTried = true;
			try
			{
				Directory.CreateDirectory(logDir);
				// Keep the newest KeepFiles including the one about to be made: names sort by time.
				string[] old = Directory.GetFiles(logDir, "router_*.jsonl");
				Array.Sort(old, StringComparer.OrdinalIgnoreCase);
				for (int i = 0; i < old.Length - (KeepFiles - 1); i++)
				{
					try
					{
						File.Delete(old[i]);
					}
					catch (Exception)
					{
					}
				}
				filePath = Path.Combine(logDir, "router_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".jsonl");

				string prompt = "";
				try
				{
					prompt = File.ReadAllText(promptPath, Encoding.UTF8);
				}
				catch (Exception)
				{
					prompt = null;
				}
				string note;
				string model = null;
				string provider = null;
				try
				{
					// Model-agnostic calls: MAIN_MODEL / MAIN_PROVIDER in .env win over router_model.txt.
					Dictionary<string, string> env = LlmEnv.Read();
					string envModel;
					string envProvider;
					if (env.TryGetValue("MAIN_MODEL", out envModel) && envModel.Trim().Length > 0)
					{
						model = envModel.Trim();
					}
					if (env.TryGetValue("MAIN_PROVIDER", out envProvider) && envProvider.Trim().Length > 0)
					{
						provider = envProvider.Trim();
					}
					if (model == null)
					{
						model = Router.ReadRouterModel(modelFilePath, out note);
					}
				}
				catch (Exception)
				{
				}

				JObject o = Header(ctx, "session");
				o["mod_version"] = AssemblyVersion();
				o["dll_build_utc"] = DllBuildTime();
				o["model"] = model;
				if (provider != null)
				{
					o["provider"] = provider;
				}
				o["system_prompt_sha256"] = prompt != null ? Sha256(prompt) : null;
				o["system_prompt"] = prompt;
				if (prompt != null)
				{
					lastPromptHash = Sha256(prompt);
				}
				AppendRaw(o);
			}
			catch (Exception ex)
			{
				filePath = null;
				enabled = false;
				LogFailureOnce(ex);
			}
		}

		private static string AssemblyVersion()
		{
			try
			{
				return typeof(RouterLog).Assembly.GetName().Version.ToString();
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static string DllBuildTime()
		{
			try
			{
				string location = typeof(RouterLog).Assembly.Location;
				if (!string.IsNullOrEmpty(location) && File.Exists(location))
				{
					return File.GetLastWriteTimeUtc(location).ToString("o");
				}
			}
			catch (Exception)
			{
			}
			return null;
		}

		private static void LogFailureOnce(Exception ex)
		{
			if (loggedFailureOnce)
			{
				return;
			}
			loggedFailureOnce = true;
			try
			{
				Verse.Log.Error(LogPrefix + "failed to write the router log: " + ex.Message);
			}
			catch (Exception)
			{
			}
		}
	}
}
