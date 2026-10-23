using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VerbalCommands.RouterScript;

namespace VerbalCommands.Bridge
{
	// Brief 23: the part of the mod that talks to the router script (router.txt). It has no RimWorld types
	// in it: the game side sits behind IRouterGame, so the console harness (src\VerbalCommands.RouterHarness)
	// compiles this very file with a fake game side.
	//
	// One call, RouteAsync(order, pending, ...), does: read router.txt and haiku_prompt.md, register the host
	// functions of the contract (vocabulary, pending, haiku, plan, query, log), run route(order) OFF the calling
	// thread (RouterScript README thread notes: RunAsync runs on the calling thread until a host function
	// answers later, so the whole call is started inside Task.Run), and turn the returned dict into a
	// RouteOutcome. It never throws: every problem comes back as RouteKind.Problem with a plain message.
	// Host calls that need the game ask the game side, which moves itself onto the game's main thread; this
	// code never waits on the main thread.

	// The game side of the bridge. Every method may be called on any thread; each one that touches game
	// objects must hop to the main thread itself and hand the answer back through the Task.
	public interface IRouterGame
	{
		// A plain object the game side can keep per-run notes in (e.g. who an earlier sentence drafted).
		object NewRunState();

		// The VOCABULARY block, one "name: a, b, c" line per kind. orderText is the order being routed
		// (the recipes line only lists benches whose name occurs in it).
		Task<string> VocabularyAsync(string orderText, CancellationToken token);

		// Plans one sentence. Nothing is applied. Never throws for a refusal: that is PlanReply.Fail.
		Task<PlanReply> PlanAsync(PlanRequest request, CancellationToken token);

		// The model call: systemPrompt (haiku_prompt.md) plus input as the user message; the model's text.
		// Throws ScriptError with a plain sentence when the model cannot be reached.
		Task<string> HaikuAsync(string systemPrompt, string input, CancellationToken token);

		// Answers one question (brief 26). Reads the game and changes nothing. Never throws for a refusal: that is
		// QueryReply.Fail.
		Task<QueryReply> QueryAsync(string form, Dictionary<string, object> slots, CancellationToken token);

		// One [VerbalCommands] log line.
		void Log(string text);
	}

	public sealed class PendingQuestion
	{
		public string order;
		public string question;
	}

	public sealed class PlanRequest
	{
		public string form;
		// Slot values as plain data: string, double, List<object>, Dictionary<string, object>.
		public Dictionary<string, object> slots = new Dictionary<string, object>();
		// 0-based position of this call within the run (the n of THEM/THOSE refers to these positions).
		public int index;
		public object runState;
		// Resolved from slots "THEM": n / "THOSE": n by the bridge (null when the slot is absent).
		public IList<object> themPawns;
		public IList<object> thoseObjects;
	}

	public sealed class PlanReply
	{
		public bool ok;
		public string line;
		public string reason;
		// What the game side planned (kept by the bridge, handed back in RouteOutcome.handles on "plans").
		public object handle;
		// What this sentence selected or acted on, for a later THEM / THOSE.
		public IList<object> pawns;
		public IList<object> objects;

		public static PlanReply Ok(string line, object handle, IList<object> pawns, IList<object> objects)
		{
			PlanReply r = new PlanReply();
			r.ok = true;
			r.line = line;
			r.handle = handle;
			r.pawns = pawns;
			r.objects = objects;
			return r;
		}

		public static PlanReply Fail(string reason)
		{
			PlanReply r = new PlanReply();
			r.ok = false;
			r.reason = reason;
			return r;
		}
	}

	// The answer to one question: text (lines separated by a newline) or a plain reason.
	public sealed class QueryReply
	{
		public bool ok;
		public string text;
		public string reason;

		public static QueryReply Ok(string text)
		{
			QueryReply r = new QueryReply();
			r.ok = true;
			r.text = text;
			return r;
		}

		public static QueryReply Fail(string reason)
		{
			QueryReply r = new QueryReply();
			r.ok = false;
			r.reason = reason;
			return r;
		}
	}

	public enum RouteKind
	{
		Plans,
		Ask,
		Cannot,
		Failed,
		// Brief 26: every sentence was a question and was answered; lines holds the answers.
		Answer,
		// The script could not be loaded or stopped: problem holds its plain message(s).
		Problem
	}

	public sealed class RouteOutcome
	{
		public RouteKind kind;
		// Plans: the confirm lines. Answer: the answers. Failed: the reasons. Problem: the messages. Cannot: the reason.
		public List<string> lines = new List<string>();
		// Ask: the question.
		public string question;
		// Plans: what the game side planned, in sentence order. Empty for every other kind.
		public List<object> handles = new List<object>();

		public static RouteOutcome Problem(params string[] messages)
		{
			RouteOutcome o = new RouteOutcome();
			o.kind = RouteKind.Problem;
			o.lines.AddRange(messages);
			return o;
		}
	}

	public sealed class RouterBridge
	{
		public const string EntryFunction = "route";
		// The agent inbox: sentences already written, no model call (agent_player brief 01).
		public const string RawEntryFunction = "route_raw";

		private readonly string scriptPath;
		private readonly string promptPath;
		private readonly IRouterGame game;

		public RouterBridge(string scriptPath, string promptPath, IRouterGame game)
		{
			this.scriptPath = scriptPath;
			this.promptPath = promptPath;
			this.game = game;
		}

		// pending: what an earlier "ask" left (null = none). limit: wall-clock time for the whole run
		// (host calls included); TimeSpan.Zero = no limit. Never throws.
		public Task<RouteOutcome> RouteAsync(string order, PendingQuestion pending, TimeSpan limit, CancellationToken token)
		{
			return RouteAsync(order, pending, limit, token, EntryFunction);
		}

		// entry: the script function to run (EntryFunction or RawEntryFunction); null or empty means EntryFunction.
		public Task<RouteOutcome> RouteAsync(string order, PendingQuestion pending, TimeSpan limit, CancellationToken token, string entry)
		{
			return Task.Run(() => RouteCoreAsync(order, pending, limit, token, entry));
		}

		private async Task<RouteOutcome> RouteCoreAsync(string order, PendingQuestion pending, TimeSpan limit, CancellationToken token, string entry)
		{
			try
			{
				string source;
				string prompt;
				try
				{
					source = File.ReadAllText(scriptPath, Encoding.UTF8);
				}
				catch (Exception)
				{
					return RouteOutcome.Problem("The router file is missing from the mod folder, so the order was not understood.");
				}
				try
				{
					prompt = File.ReadAllText(promptPath, Encoding.UTF8);
				}
				catch (Exception)
				{
					return RouteOutcome.Problem("The router's instruction file is missing from the mod folder, so the order was not understood.");
				}

				Run run = new Run(game, prompt, order, pending);
				ScriptHost host = new ScriptHost();
				run.Register(host);

				Script script = Script.Load(Path.GetFileName(scriptPath), source, host);
				if (!script.Ok)
				{
					RouteOutcome bad = new RouteOutcome();
					bad.kind = RouteKind.Problem;
					bad.lines.AddRange(script.errors);
					return bad;
				}
				script.options.timeLimit = limit;

				ScriptResult result = await script.RunAsync(string.IsNullOrEmpty(entry) ? EntryFunction : entry, new Value[] { Value.FromText(order) }, token).ConfigureAwait(false);
				if (!result.ok)
				{
					RouteOutcome stopped = new RouteOutcome();
					stopped.kind = RouteKind.Problem;
					stopped.lines.AddRange(result.errors);
					if (stopped.lines.Count == 0)
					{
						stopped.lines.Add("The router stopped before it gave an answer.");
					}
					return stopped;
				}
				return run.ReadResult(result.value);
			}
			catch (OperationCanceledException)
			{
				return RouteOutcome.Problem("The order was cancelled.");
			}
			catch (Exception)
			{
				// Not a script problem and not the player's: nothing of the exception is shown.
				return RouteOutcome.Problem("The order could not be understood because of a problem inside the mod.");
			}
		}

		// The state of one run: its plans, and the host functions that close over it.
		private sealed class Run
		{
			private readonly IRouterGame game;
			private readonly string prompt;
			private readonly string order;
			private readonly PendingQuestion pending;
			private readonly object runState;
			private readonly List<PlanReply> planned = new List<PlanReply>();

			public Run(IRouterGame game, string prompt, string order, PendingQuestion pending)
			{
				this.game = game;
				this.prompt = prompt;
				this.order = order;
				this.pending = pending;
				runState = game.NewRunState();
			}

			public void Register(ScriptHost host)
			{
				host.RegisterAsync("vocabulary", 0, VocabularyAsync);
				host.Register("pending", 0, Pending);
				host.RegisterAsync("haiku", 1, HaikuAsync);
				host.RegisterAsync("plan", 2, PlanAsync);
				host.RegisterAsync("query", 2, QueryAsync);
				host.Register("log", 1, Log);
			}

			private async Task<Value> VocabularyAsync(Value[] args, CancellationToken token)
			{
				try
				{
					return Value.FromText(await game.VocabularyAsync(order, token).ConfigureAwait(false) ?? "");
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (ScriptError)
				{
					throw;
				}
				catch (Exception)
				{
					throw new ScriptError("The colony's word list could not be built.");
				}
			}

			private Value Pending(Value[] args)
			{
				if (pending == null)
				{
					return Value.None;
				}
				Value dict = Value.NewDict();
				dict.AsDict.Set("order", Value.FromText(pending.order ?? ""));
				dict.AsDict.Set("question", Value.FromText(pending.question ?? ""));
				return dict;
			}

			private async Task<Value> HaikuAsync(Value[] args, CancellationToken token)
			{
				string input = args[0].AsText;
				if (input == null)
				{
					throw new ScriptError("The router sent the model something that was not text.");
				}
				try
				{
					string text = await game.HaikuAsync(prompt, input, token).ConfigureAwait(false);
					return Value.FromText(text ?? "");
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (ScriptError)
				{
					throw;
				}
				catch (Exception)
				{
					throw new ScriptError("The model could not be reached.");
				}
			}

			private Value Log(Value[] args)
			{
				try
				{
					game.Log(args[0].ToText());
				}
				catch (Exception)
				{
				}
				return Value.None;
			}

			private async Task<Value> PlanAsync(Value[] args, CancellationToken token)
			{
				PlanReply reply = await PlanCoreAsync(args, token).ConfigureAwait(false);
				// Position n of THEM/THOSE counts every plan call of the run, the refused ones included.
				planned.Add(reply);
				Value dict = Value.NewDict();
				dict.AsDict.Set("ok", Value.FromBool(reply.ok));
				if (reply.ok)
				{
					dict.AsDict.Set("line", Value.FromText(reply.line ?? ""));
				}
				else
				{
					dict.AsDict.Set("reason", Value.FromText(reply.reason ?? "That could not be planned."));
				}
				return dict;
			}

			// Brief 26: query(form, slots) -> {"ok": true, "text": ...} or {"ok": false, "reason": ...}. Read-only.
			private async Task<Value> QueryAsync(Value[] args, CancellationToken token)
			{
				QueryReply reply;
				string form = args[0].AsText;
				ScriptDict slotDict = args[1].AsDict;
				if (form == null || slotDict == null)
				{
					reply = QueryReply.Fail("The router sent a question the mod could not read.");
				}
				else
				{
					Dictionary<string, object> slots = new Dictionary<string, object>();
					foreach (string key in slotDict.Keys)
					{
						slots[key] = slotDict.Get(key).ToObject();
					}
					try
					{
						reply = await game.QueryAsync(form, slots, token).ConfigureAwait(false) ?? QueryReply.Fail("That could not be answered.");
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch (Exception)
					{
						reply = QueryReply.Fail("That could not be answered because of a problem inside the mod.");
					}
				}
				Value dict = Value.NewDict();
				dict.AsDict.Set("ok", Value.FromBool(reply.ok));
				if (reply.ok)
				{
					dict.AsDict.Set("text", Value.FromText(reply.text ?? ""));
				}
				else
				{
					dict.AsDict.Set("reason", Value.FromText(reply.reason ?? "That could not be answered."));
				}
				return dict;
			}

			private async Task<PlanReply> PlanCoreAsync(Value[] args, CancellationToken token)
			{
				string form = args[0].AsText;
				ScriptDict slotDict = args[1].AsDict;
				if (form == null || slotDict == null)
				{
					return PlanReply.Fail("The router sent a sentence the mod could not read.");
				}

				PlanRequest request = new PlanRequest();
				request.form = form;
				request.index = planned.Count;
				request.runState = runState;
				foreach (string key in slotDict.Keys)
				{
					request.slots[key] = slotDict.Get(key).ToObject();
				}

				string problem;
				if (!ResolveBackReference(request, "THEM", "pawns", out request.themPawns, out problem)
					|| !ResolveBackReference(request, "THOSE", "storage or buildings", out request.thoseObjects, out problem))
				{
					return PlanReply.Fail(problem);
				}

				try
				{
					PlanReply reply = await game.PlanAsync(request, token).ConfigureAwait(false);
					return reply ?? PlanReply.Fail("That could not be planned.");
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception)
				{
					return PlanReply.Fail("That could not be planned because of a problem inside the mod.");
				}
			}

			// "THEM": n or "THOSE": n -> what the n-th earlier plan call selected (pawns) or acted on (objects).
			private bool ResolveBackReference(PlanRequest request, string slotName, string what, out IList<object> found, out string problem)
			{
				found = null;
				problem = null;
				object raw;
				if (!request.slots.TryGetValue(slotName, out raw))
				{
					return true;
				}
				string word = slotName == "THEM" ? "'them'" : "'those'";
				if (!(raw is double))
				{
					problem = word + " did not say which earlier sentence it means.";
					return false;
				}
				double n = (double)raw;
				if (n != Math.Floor(n) || n < 0 || n >= planned.Count)
				{
					problem = word + " does not point at an earlier sentence of this order.";
					return false;
				}
				PlanReply earlier = planned[(int)n];
				IList<object> items = slotName == "THEM" ? earlier.pawns : earlier.objects;
				if (!earlier.ok || items == null || items.Count == 0)
				{
					problem = word + " means " + what + " from sentence " + ((int)n + 1) + ", which did not give any.";
					return false;
				}
				found = items;
				return true;
			}

			public RouteOutcome ReadResult(Value value)
			{
				ScriptDict dict = value != null ? value.AsDict : null;
				Value kindValue = dict != null ? dict.Get("result") : null;
				string kind = kindValue != null ? kindValue.AsText : null;
				RouteOutcome o = new RouteOutcome();
				switch (kind)
				{
					case "plans":
						o.kind = RouteKind.Plans;
						o.lines = ListOf(dict, "lines");
						foreach (PlanReply reply in planned)
						{
							if (reply.ok && reply.handle != null)
							{
								o.handles.Add(reply.handle);
							}
						}
						return o;
					case "ask":
						o.kind = RouteKind.Ask;
						o.question = TextOf(dict, "question");
						if (string.IsNullOrEmpty(o.question))
						{
							return RouteOutcome.Problem("The router asked a question without saying what it was.");
						}
						return o;
					case "answer":
						o.kind = RouteKind.Answer;
						o.lines = ListOf(dict, "lines");
						return o;
					case "cannot":
						o.kind = RouteKind.Cannot;
						o.lines.Add(TextOf(dict, "reason"));
						return o;
					case "failed":
						o.kind = RouteKind.Failed;
						o.lines = ListOf(dict, "reasons");
						return o;
					default:
						return RouteOutcome.Problem("The router gave an answer the mod does not understand.");
				}
			}

			private static string TextOf(ScriptDict dict, string key)
			{
				Value v = dict.Get(key);
				return v != null && !v.IsNone ? v.ToText() : "";
			}

			private static List<string> ListOf(ScriptDict dict, string key)
			{
				Value v = dict.Get(key);
				return v != null ? v.ToStringList() : new List<string>();
			}
		}
	}
}
