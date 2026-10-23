using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	public enum OrderState
	{
		Idle,
		Sending,
		AwaitingConfirm,
		Applying,
		Done,
		Faulted
	}

	// Agent inbox (agent_player brief 01): how one order is run when it does not come from the order window.
	// null options (the window's orders) mean everything as before.
	public sealed class OrderOptions
	{
		public bool raw;        // the text is router sentences: no Haiku call (router.txt route_raw)
		public bool dry;        // plan and report, never apply
		public bool? autoApply; // null = the confirmBeforeApply setting; true = apply at once; false = wait
	}

	// Owns one order's lifecycle: snapshot -> request -> response -> resolve -> (confirm) -> apply.
	// Every public method here is expected to be called from the main thread (the dialog's GUI
	// code). The only thing that crosses to a background thread is AnthropicClient.SendAsync; its
	// continuation posts back through MainThread before touching anything here.
	public sealed class OrderController
	{
		public OrderState State = OrderState.Idle;
		public List<string> Log = new List<string>();
		public List<Plan> Plans = new List<Plan>();
		public List<string> ModelTexts = new List<string>();

		// Agent inbox (agent_player brief 01): what the current order was asked to do and what became of it.
		// Options is null for window orders. LastResult is one of answer, planned, applied, dry_run,
		// nothing_to_change, ask, cannot, failed, problem, cancelled (null while nothing is known yet).
		public OrderOptions Options;
		public string LastResult;
		public RouteOutcome LastOutcome;
		public List<string> LastPlanLines = new List<string>();
		public List<string> LastPlanNotes = new List<string>();
		public List<JObject> LastActions = new List<JObject>();

		private Snapshot snap;
		private string systemPrompt;
		private JArray tools;
		private string lastOrderText;
		private CancellationTokenSource cts;

		// Brief 15 Step C: the current order's route (Step D reads lastRoute.storageMatched), and the
		// rewrite/slice text re-sent by SendExplainRequest so its first user turn matches the original.
		public RouteResult lastRoute;
		private string lastRewrite;
		private string lastSliceText;
		// Brief 15b fix 10: the storage ids of this order's slice; set_storage accepts no other id.
		private HashSet<string> lastSliceStorageIds;
		// True between launching the router task and OnRouterDone accepting its result.
		private bool routerInFlight;
		// Brief 15b fix 14 (nit 8): the backend is read ONCE per order, in Submit; the router, the main
		// request and the explain request all use this value.
		// Model-agnostic calls (2026-10-05): the router (MAIN role) and the main/explain requests (FALLBACK role)
		// can now have different providers (LlmProviders.cs, .env). orderUseHeadless is now "the router call is the
		// Claude Code CLI"; orderFallback is the FALLBACK role, also read once per order in Submit.
		private bool orderUseHeadless;
		private LlmTarget orderFallback;
		private LlmDefaults orderLlmDefaults;

		// Brief 23: the script router path. ConfirmLines are the sentences the player reads at the confirm
		// step (what route() returned); the Plans behind them hold the changes. pendingQuestion is the ask
		// left by an earlier order (in memory only: it is gone when the game closes, which is fine).
		public List<string> ConfirmLines = new List<string>();
		private static PendingQuestion pendingQuestion;
		private bool clearOrderBox;
		// True from the moment a script run is launched until OnScriptDone takes its answer.
		private bool scriptInFlight;
		// The "can't do it" option (TryFallbackRun): the current order's map, log ids and game side, and whether the
		// run now going is the fallback model's second run.
		private Map scriptMap;
		private RouterLog.Ctx scriptRunCtx;
		private RouterGame scriptGame;
		private bool scriptFallbackRun;

		// Brief 29: the router log's order id and turn. Turn 1 = the player's order and the model's reply to it;
		// each later player message of the same order (the answer to an ASK) adds 1. A new order (one that is not
		// an answer to the pending question) and Clear conversation start again at turn 1 / no order. Static, like
		// pendingQuestion, because the pending question carries the order into the next message.
		private static int orderSeq;
		private static string currentOrderId = "";
		private static int currentTurn;

		private static RouterLog.Ctx CurrentCtx()
		{
			return new RouterLog.Ctx(currentOrderId, currentTurn);
		}

		// Agent inbox (agent_player brief 01): read-only views of the order id / turn, and of the one pending question.
		public static string CurrentOrderId { get { return currentOrderId; } }
		public static int CurrentTurn { get { return currentTurn; } }
		public static bool HasPendingQuestion { get { return pendingQuestion != null; } }
		public static string PendingQuestionText { get { return pendingQuestion != null ? pendingQuestion.question : null; } }

		// Agent inbox (agent_player brief 01): the inbox's "cancel" for a question left by an earlier order.
		public static void DropPendingQuestion()
		{
			DropQuestion(null);
		}

		private static void DropQuestion(string reason)
		{
			if (pendingQuestion == null)
			{
				return;
			}
			JObject fields = Fields("decision", "question dropped", "question", pendingQuestion.question);
			if (reason != null)
			{
				fields["reason"] = reason;
			}
			RouterLog.Write(CurrentCtx(), "decision", fields);
			pendingQuestion = null;
		}

		// Agent inbox: true when the inbox setting is on (AgentInbox.Enabled, AgentInbox.cs).
		private static bool InboxOn() { return AgentInbox.Enabled; }

		// Agent inbox (agent_player brief 01): the one place the route call is started, so the raw entry is a one-spot swap.
		private static Task<RouteOutcome> StartRoute(RouterBridge bridge, string order, PendingQuestion pending, TimeSpan limit, CancellationToken token, bool raw)
		{
			return bridge.RouteAsync(order, pending, limit, token, raw ? RouterBridge.RawEntryFunction : RouterBridge.EntryFunction);
		}

		private static void BeginTurn(bool answerToPending)
		{
			if (answerToPending && currentOrderId.Length > 0)
			{
				currentTurn++;
				return;
			}
			orderSeq++;
			currentOrderId = "o" + orderSeq;
			currentTurn = 1;
		}

		private static JObject Fields(params object[] pairs)
		{
			JObject o = new JObject();
			for (int i = 0; i + 1 < pairs.Length; i += 2)
			{
				object v = pairs[i + 1];
				o[(string)pairs[i]] = v == null ? JValue.CreateNull() : JToken.FromObject(v);
			}
			return o;
		}

		// One action record: the form and slots of the plan, the write's own description, and its result.
		private static JObject ActionFields(Plan plan, Write write, string result, string reason, string before, string after)
		{
			JObject o = new JObject();
			o["form"] = plan.toolName;
			o["before"] = before;
			o["after"] = after;
			JToken slots = null;
			if (plan.slotsJson != null)
			{
				try
				{
					slots = JToken.Parse(plan.slotsJson);
				}
				catch (Exception)
				{
					slots = plan.slotsJson;
				}
			}
			o["slots"] = slots;
			o["description"] = write.description;
			o["result"] = result;
			o["reason"] = reason;
			o["plan_notes"] = new JArray(plan.notes);
			return o;
		}

		// Main thread, at the start of every order.
		private static void ConfigureRouterLog(VerbalCommandsSettings settings)
		{
			try
			{
				// Agent inbox (agent_player brief 01): the log is written whenever the inbox is on.
				RouterLog.Configure(settings.saveRouterLog || InboxOn(), Path.Combine(VerbalCommandsMod.ModRootDir ?? "", "Router", "haiku_prompt.md"), Router.RouterModelFilePath());
			}
			catch (Exception ex)
			{
				Verse.Log.Warning("[VerbalCommands] router log: could not start: " + ex.Message);
			}
		}

		// Brief 23: true once after the router asked a question, so the order box can be emptied for the answer.
		public bool TakeClearOrderBox()
		{
			bool wanted = clearOrderBox;
			clearOrderBox = false;
			return wanted;
		}

		// Brief 15b fix 14: controllers with an order in flight (main thread only). The mod settings
		// grey out the backend toggle while any of them is still Sending.
		private static readonly List<OrderController> ordersInFlight = new List<OrderController>();

		// Main thread. Current.ProgramState (Verse\Current.cs:67): outside a game nothing can finish
		// (MainThread is drained by a GameComponent), so the toggle is never locked on the main menu.
		public static bool AnyOrderInFlight
		{
			get
			{
				ordersInFlight.RemoveAll((OrderController c) => c.State != OrderState.Sending);
				return ordersInFlight.Count > 0 && Current.ProgramState == ProgramState.Playing;
			}
		}

		// Agent inbox (agent_player brief 01): the order window makes its controller with showInboxLine true; the inbox's
		// own controller passes false.
		public OrderController() : this(true)
		{
		}

		public OrderController(bool showInboxLine)
		{
			if (showInboxLine && InboxOn())
			{
				AppendLog("Agent inbox is ON: text files in the inbox folder are run as orders (Options > Mod settings > Verbal Commands).");
			}
		}

		public void Submit(string orderText)
		{
			Submit(orderText, null);
		}

		public void Submit(string orderText, OrderOptions options)
		{
			if (State == OrderState.Sending || State == OrderState.Applying)
			{
				return;
			}

			// Agent inbox (agent_player brief 01): a new order starts with nothing known about its result.
			Options = options;
			LastResult = null;
			LastOutcome = null;
			LastPlanLines.Clear();
			LastPlanNotes.Clear();
			LastActions.Clear();

			VerbalCommandsSettings settings = VerbalCommandsMod.Settings;
			if (settings == null)
			{
				AppendLog("No API key set. Enter one in Options > Mod settings > Verbal Commands.");
				LastResult = "problem";
				return;
			}
			// Model-agnostic calls: the two roles as .env and the settings page say (no file read for the router's
			// model here: only its provider and key are needed now, and router_model.txt is read off the main thread).
			string routerModelPathForLlm = null;
			try
			{
				routerModelPathForLlm = Router.RouterModelFilePath();
			}
			catch (Exception)
			{
			}
			orderLlmDefaults = LlmDefaults.FromSettings(settings, routerModelPathForLlm);
			LlmTarget mainTarget = LlmTargets.Resolve(LlmRole.Main, orderLlmDefaults, false);
			orderFallback = LlmTargets.Resolve(LlmRole.Fallback, orderLlmDefaults);
			bool useHeadless = mainTarget.IsCli;
			// Agent inbox: raw orders never call a model, so they need no key.
			bool rawOrder = options != null && options.raw;
			if (!rawOrder)
			{
				// The sentence router calls only the main model; the older path calls both.
				string missing = mainTarget.MissingReason();
				if (missing == null && !settings.useScriptRouter)
				{
					missing = orderFallback.MissingReason();
				}
				if (missing != null)
				{
					AppendLog(missing);
					LastResult = "problem";
					return;
				}
			}

			Map map = Find.CurrentMap;
			if (map == null)
			{
				AppendLog("No current map.");
				LastResult = "problem";
				return;
			}

			ConfigureRouterLog(settings);
			// Agent inbox: the older path is not used by the inbox.
			if (options != null && !settings.useScriptRouter)
			{
				AppendLog("The agent inbox needs the sentence router (mod setting) to be on. Nothing was changed.");
				LastResult = "problem";
				State = OrderState.Faulted;
				return;
			}
			orderUseHeadless = useHeadless;
			if (settings.useScriptRouter)
			{
				SubmitScript(orderText, settings, map);
				return;
			}

			// Older path (useScriptRouter off): a new order every time; the router log follows what it sends.
			BeginTurn(false);
			RouterLog.Write(CurrentCtx(), "order", Fields("text", orderText, "path", "older router + main model"));
			Plans.Clear();
			ModelTexts.Clear();
			ConfirmLines.Clear();

			lastOrderText = orderText;
			orderUseHeadless = useHeadless;
			snap = Snapshot.Capture(map);
			AppendLog("snapshot: " + snap.pawnsById.Count + " colonists, " +
				((JArray)snap.json["rooms"]).Count + " rooms, " + ((JArray)snap.json["areas"]).Count + " areas");

			lastRoute = null;
			lastSliceStorageIds = null;

			// OrderState.Sending covers the router call AND the main call (brief 15 Step C).
			State = OrderState.Sending;
			if (!ordersInFlight.Contains(this))
			{
				ordersInFlight.Add(this);
			}
			cts = new CancellationTokenSource();
			CancellationToken token = cts.Token;

			// Brief 15 Step C, step 1 (main thread): vocabulary from live game data + regex. Any
			// failure here only skips routing; it never blocks the order.
			RouterVocabulary vocab = null;
			RouteMatch step1 = null;
			try
			{
				vocab = Router.BuildVocabulary(map);
				step1 = Router.Match(orderText, vocab);
			}
			catch (Exception ex)
			{
				Verse.Log.Warning("[VerbalCommands] router: vocabulary/regex failed, order sent without routing: " + ex);
				AppendLog("router: the colony's word list could not be built, so the order was sent without routing.");
				vocab = null;
				step1 = null;
			}

			string routerModelFilePath = null;
			try
			{
				routerModelFilePath = Router.RouterModelFilePath();
			}
			catch (Exception ex)
			{
				Verse.Log.Warning("[VerbalCommands] router: could not resolve router_model.txt path: " + ex.Message);
			}

			if (vocab == null || step1.combinations.Count > 0 || routerModelFilePath == null)
			{
				// Step 1 found a combination (or routing is unavailable): skip step 2. router_model.txt
				// is still created/read off the main thread so players can find and edit it.
				if (routerModelFilePath != null)
				{
					string pathForEnsure = routerModelFilePath;
					Task.Run(delegate
					{
						string ignoredNote;
						Router.ReadRouterModel(pathForEnsure, out ignoredNote);
					});
				}
				RouteResult route;
				try
				{
					route = Router.FinishRoute(map, vocab, step1, null);
				}
				catch (Exception ex)
				{
					// Never block the order: plain order, no scoped data.
					Verse.Log.Warning("[VerbalCommands] router: step 1 scope failed, order sent without routing: " + ex);
					route = new RouteResult();
					route.sliceNote = "the storage data could not be built";
				}
				LogRoute(route);
				SendMainRequest(route, token);
				return;
			}

			// Step 2 (background): router call. Step 3 + main request continue in OnRouterDone.
			Router.RouterCall call = new Router.RouterCall();
			call.defaults = orderLlmDefaults;
			// Brief 15b fix 6: the router's own cap, not the main timeout.
			call.capSeconds = useHeadless ? Router.RouterCapSecondsHeadless : Router.RouterCapSecondsApi;
			call.modelFilePath = routerModelFilePath;
			call.orderText = orderText;
			call.vocabularyJson = Router.VocabularyJson(vocab);

			routerInFlight = true;
			Task.Run(async delegate
			{
				RouterOutput output;
				try
				{
					output = await Router.RunRouterAsync(call, token).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					// RunRouterAsync is not expected to throw; if it does, route nothing and carry on.
					output = new RouterOutput();
					output.requestedModel = Router.DefaultRouterModel;
					output.windowNote = "The router ran into a problem inside the mod. Your order went to the main model without storage data.";
					output.failurePlayerLog = "router error (" + ex.GetType().Name + ")";
				}
				MainThread.Post(delegate { OnRouterDone(output, vocab, step1, token); });
			}, token);
		}

		// ---- Brief 23: the script router path ----------------------------------------------------------
		// Order -> router.txt (run by the interpreter, off the main thread; it calls the small model and plans each
		// sentence through RouterGame) -> one of: plans (confirm, then apply), a question (nothing planned, the
		// answer is the next order), an answer (brief 26: questions, shown at once), "cannot", or "failed" (nothing applied).

		private void SubmitScript(string orderText, VerbalCommandsSettings settings, Map map)
		{
			Plans.Clear();
			ModelTexts.Clear();
			ConfirmLines.Clear();
			lastOrderText = orderText;
			// orderUseHeadless: set in Submit (the MAIN role's backend).
			snap = null;
			lastRoute = null;
			lastSliceStorageIds = null;

			State = OrderState.Sending;
			if (!ordersInFlight.Contains(this))
			{
				ordersInFlight.Add(this);
			}
			cts = new CancellationTokenSource();
			CancellationToken token = cts.Token;

			// Agent inbox (agent_player brief 01): a raw order is never an answer; a question left waiting is dropped.
			bool raw = Options != null && Options.raw;
			PendingQuestion pending = raw ? null : pendingQuestion;
			if (raw && pendingQuestion != null)
			{
				DropQuestion("raw order");
			}
			scriptInFlight = true;
			BeginTurn(pending != null);
			RouterLog.Ctx runCtx = CurrentCtx();
			if (pending != null)
			{
				RouterLog.Write(runCtx, "player_answer", Fields("text", orderText, "question", pending.question, "order_so_far", pending.order));
			}
			else
			{
				JObject orderFields = Fields("text", orderText, "path", raw ? "raw sentences" : "sentence router");
				if (Options != null)
				{
					List<string> modeFlags = new List<string>();
					if (Options.raw)
					{
						modeFlags.Add("raw");
					}
					if (Options.dry)
					{
						modeFlags.Add("dry");
					}
					if (Options.autoApply.HasValue)
					{
						modeFlags.Add(Options.autoApply.Value ? "apply" : "wait");
					}
					orderFields["mode"] = new JArray(modeFlags);
				}
				RouterLog.Write(runCtx, "order", orderFields);
			}
			if (pending != null)
			{
				AppendLog("answering: " + pending.question);
				if (settings.logRouterAnswers)
				{
					Verse.Log.Message("[VerbalCommands] router: turn " + currentTurn + ", player's answer \""+ orderText + "\" to question \"" + pending.question + "\" (order \"" + pending.order + "\")");
				}
			}
			AppendLog("order sent to the sentence router");

			scriptMap = map;
			scriptRunCtx = runCtx;
			scriptFallbackRun = false;
			StartScriptRun(orderText, pending, token, raw, false);
		}

		// Main thread. Builds the game side and runs router.txt once (off the main thread); OnScriptDone takes the
		// answer. fallbackRun: every model call of this run goes to the fallback model (TryFallbackRun).
		private void StartScriptRun(string orderText, PendingQuestion pending, CancellationToken token, bool raw, bool fallbackRun)
		{
			VerbalCommandsSettings settings = VerbalCommandsMod.Settings;
			RouterLog.Ctx runCtx = scriptRunCtx ?? CurrentCtx();
			scriptInFlight = true;
			RouteOutcome early = null;
			RouterBridge bridge = null;
			scriptGame = null;
			try
			{
				// Main thread: the model-file path and the settings are read here, never in the background.
				scriptGame = new RouterGame(scriptMap, settings, Router.RouterModelFilePath(), runCtx, fallbackRun);
				string root = VerbalCommandsMod.ModRootDir;
				bridge = new RouterBridge(Path.Combine(root, "Router", "router.txt"), Path.Combine(root, "Router", "haiku_prompt.md"), scriptGame);
			}
			catch (Exception ex)
			{
				Verse.Log.Warning("[VerbalCommands] script router could not start: " + ex);
				RouterLog.Write(runCtx, "error", Fields("where", "script router start", "detail", ex.ToString()));
				early = RouteOutcome.Problem("The order could not be started because of a problem inside the mod.");
			}

			if (early != null)
			{
				OnScriptDone(early, orderText, pending, token);
				return;
			}
			// The whole run (the model call and every wait for the main thread included) is cut off after this.
			TimeSpan limit = TimeSpan.FromSeconds(Math.Max(60, settings.timeoutSeconds * 2 + 30));
			RouterBridge capturedBridge = bridge;
			Task.Run(async delegate
			{
				RouteOutcome outcome;
				try
				{
					outcome = await StartRoute(capturedBridge, orderText, pending, limit, token, raw).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					RouterLog.Write(runCtx, "error", Fields("where", "script router run", "detail", ex.ToString()));
					outcome = RouteOutcome.Problem("The order could not be understood because of a problem inside the mod.");
				}
				MainThread.Post(delegate { OnScriptDone(outcome, orderText, pending, token); });
			}, token);
		}

		// Main thread. Same guard as OnRouterDone: a Cancel or a newer order drops a late answer.
		private void OnScriptDone(RouteOutcome outcome, string orderText, PendingQuestion pending, CancellationToken token)
		{
			if (State != OrderState.Sending || token.IsCancellationRequested || cts == null || cts.Token != token)
			{
				return;
			}
			scriptInFlight = false;
			// Agent inbox (agent_player brief 01): what the run came back with.
			LastOutcome = outcome;
			// The main model's call failed and the fallback model answered (RouterGame.HaikuAsync): say so.
			if (scriptGame != null)
			{
				foreach (string note in scriptGame.TakeNotes())
				{
					AppendLog(note);
				}
			}

			RouterLog.Ctx ctx = CurrentCtx();
			RouterLog.Write(ctx, "route", Fields(
				"result", outcome.kind.ToString().ToLowerInvariant(),
				"lines", outcome.lines,
				"question", outcome.question,
				"plan_count", outcome.handles.Count));
			if (TryFallbackRun(outcome, orderText, pending, token))
			{
				return;
			}

			switch (outcome.kind)
			{
				case RouteKind.Problem:
					foreach (string line in outcome.lines)
					{
						AppendLog("The order could not be carried out: " + line);
					}
					AppendLog("Nothing was changed.");
					LastResult = "problem";
					State = OrderState.Faulted;
					return;

				case RouteKind.Ask:
					// Nothing is planned and nothing goes to the interpreter (user, 2026-10-02: "if haiku decides
					// ASK/CANNOT then router does not proceed with handing off to interpreter. at all.").
					// The player's next order is the answer; it is routed with this left in pending().
					PendingQuestion next = new PendingQuestion();
					next.order = pending != null ? pending.order + " " + orderText : orderText;
					next.question = outcome.question;
					pendingQuestion = next;
					clearOrderBox = true;
					AppendLog("question: " + outcome.question);
					AppendLog("Type your answer and send it. Nothing was changed.");
					LastResult = "ask";
					State = OrderState.Done;
					return;

				case RouteKind.Cannot:
					pendingQuestion = null;
					AppendLog("cannot: " + (outcome.lines.Count > 0 ? outcome.lines[0] : "that can't be done"));
					AppendLog("Nothing was changed.");
					LastResult = "cannot";
					State = OrderState.Done;
					return;

				case RouteKind.Answer:
					// Brief 26: every sentence was a question. Nothing is planned, nothing needs a confirm.
					pendingQuestion = null;
					foreach (string answerText in outcome.lines)
					{
						RouterLog.Write(ctx, "answer", Fields("text", answerText));
					}
					foreach (string answer in outcome.lines)
					{
						foreach (string answerLine in answer.Replace("\r", "").Split('\n'))
						{
							if (answerLine.Trim().Length > 0)
							{
								AppendLog("answer: " + answerLine);
							}
						}
					}
					AppendLog("Nothing was changed.");
					// Always logged (the "log planned changes" setting does not cover answers).
					Verse.Log.Message("[VerbalCommands] router: turn " + ctx.turn + ", answered order \""+ orderText + "\": " + string.Join(" | ", outcome.lines.ToArray()).Replace("\r", "").Replace("\n", " / "));
					LastResult = "answer";
					State = OrderState.Done;
					return;

				case RouteKind.Failed:
					pendingQuestion = null;
					foreach (string reason in outcome.lines)
					{
						AppendLog("not done: " + reason);
					}
					AppendLog("Nothing was changed.");
					LastResult = "failed";
					State = OrderState.Faulted;
					return;
			}

			// RouteKind.Plans
			pendingQuestion = null;
			foreach (object handle in outcome.handles)
			{
				Plan plan = handle as Plan;
				if (plan != null)
				{
					Plans.Add(plan);
				}
			}
			ConfirmLines.AddRange(outcome.lines);
			// Agent inbox (agent_player brief 01): the plan as the inbox reports it.
			LastPlanLines.AddRange(ConfirmLines);
			foreach (Plan plan in Plans)
			{
				LastPlanNotes.AddRange(plan.notes);
				foreach (string note in plan.notes)
				{
					AppendLog("note: " + note);
				}
			}
			if (Plans.Count == 0)
			{
				AppendLog("There was nothing to change.");
				LastResult = "nothing_to_change";
				State = OrderState.Done;
				return;
			}
			foreach (string line in ConfirmLines)
			{
				AppendLog("plan: " + line);
			}
			VerbalCommandsSettings settings = VerbalCommandsMod.Settings;
			List<string> riskyLines = new List<string>();
			List<string> planNotes = new List<string>();
			foreach (string line in ConfirmLines)
			{
				if (line.IndexOf("RISKY", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					riskyLines.Add(line);
				}
			}
			foreach (Plan plan in Plans)
			{
				planNotes.AddRange(plan.notes);
			}
			// Agent inbox (agent_player brief 01): a dry run never waits and never applies; otherwise the order's own
			// apply/wait choice (when it has one) beats the confirmBeforeApply setting.
			bool dryRun = Options != null && Options.dry;
			bool confirm = !dryRun && (Options != null && Options.autoApply.HasValue ? !Options.autoApply.Value : (settings == null || settings.confirmBeforeApply));
			// Wind check (AgentWindCheck, 2026-10-14): a WIND note makes the order wait for confirm even under raw apply.
			// So do a LIGHT note (AgentLightCheck), the WORKSPOT reminder (AgentWorkspotCheck) and a DOOR note (AgentDoorCheck).
			// Harness log (HarnessLog.cs, H4): one of those gates held an order that would have applied.
			if (!dryRun && !confirm)
			{
				foreach (string gate in Plans.Where((Plan p) => p.askConfirm && p.confirmBy != null).Select((Plan p) => p.confirmBy).Distinct())
				{
					HarnessLog.Add(gate, "plan held for confirm");
				}
			}
			if (!dryRun && Plans.Any((Plan p) => p.askConfirm))
			{
				confirm = true;
			}
			JObject planFields = Fields(
				"lines", ConfirmLines,
				"risky_lines", riskyLines,
				"notes", planNotes,
				"waits_for_confirm", confirm);
			if (dryRun)
			{
				planFields["dry_run"] = true;
			}
			RouterLog.Write(ctx, "plan", planFields);
			if (settings == null || settings.logRouterPlans)
			{
				Verse.Log.Message("[VerbalCommands] router: turn " + ctx.turn + ", planned for order \""+ orderText + "\": " + string.Join(" | ", ConfirmLines.ToArray()));
			}
			if (dryRun)
			{
				RouterLog.Write(ctx, "decision", Fields("decision", "dry run"));
				AppendLog("dry run: nothing was applied.");
				Plans.Clear();
				ConfirmLines.Clear();
				LastResult = "dry_run";
				State = OrderState.Done;
				return;
			}
			if (confirm)
			{
				LastResult = "planned";
				State = OrderState.AwaitingConfirm;
			}
			else
			{
				RouterLog.Write(ctx, "decision", Fields("decision", "apply", "auto", true));
				ApplyAll();
			}
		}

		// The order window's "can't do it" option (user, 2026-10-05: "for "cant do it" you make an option (inside
		// return-key-pressed-page for prompting, not on mod settings) and the setting is saved"): when the main model's
		// run comes back "cannot" or "not done", the order runs once more through router.txt with the fallback model in
		// place of the main model. Not for raw orders (they call no model), and never after the fallback's own run.
		// Main thread; true when the second run was started.
		private bool TryFallbackRun(RouteOutcome outcome, string orderText, PendingQuestion pending, CancellationToken token)
		{
			if (scriptFallbackRun || (outcome.kind != RouteKind.Cannot && outcome.kind != RouteKind.Failed))
			{
				return false;
			}
			if (Options != null && Options.raw)
			{
				return false;
			}
			VerbalCommandsSettings settings = VerbalCommandsMod.Settings;
			if (settings == null || !settings.fallbackOnCannot)
			{
				return false;
			}
			LlmDefaults d = orderLlmDefaults ?? LlmDefaults.FromSettings(settings, Router.RouterModelFilePath());
			LlmTarget fallback = LlmTargets.Resolve(LlmRole.Fallback, d);
			string missing = fallback.MissingReason();
			if (missing != null)
			{
				AppendLog("The fallback model was not asked: " + missing);
				return false;
			}
			LlmTarget main = LlmTargets.Resolve(LlmRole.Main, d);
			if (main.SameModelAs(fallback))
			{
				AppendLog("The fallback model is the same as the main model, so it was not asked again.");
				return false;
			}
			string what = outcome.kind == RouteKind.Cannot ? "cannot" : "not done";
			foreach (string line in outcome.lines)
			{
				AppendLog("main model (" + main.model + "), " + what + ": " + line);
			}
			AppendLog("asking the fallback model (" + fallback.model + ") instead");
			RouterLog.Write(CurrentCtx(), "decision", Fields("decision", "fallback run", "main_result", what, "main_lines", outcome.lines,
				"main_model", main.model, "fallback_model", fallback.model));
			scriptFallbackRun = true;
			StartScriptRun(orderText, pending, token, false, true);
			return true;
		}

		// Brief 15 Step C, step 3 (main thread). Same guard as OnResponse, plus the token: a Cancel or
		// dialog close during the router call must not fire the main call. Brief 15b fix 7: closing the
		// window on the headless path no longer cancels the token, so the token must also still be this
		// order's (a late result from an earlier order is dropped).
		private void OnRouterDone(RouterOutput output, RouterVocabulary vocab, RouteMatch step1, CancellationToken token)
		{
			if (State != OrderState.Sending || token.IsCancellationRequested || !routerInFlight || cts == null || cts.Token != token)
			{
				return;
			}
			routerInFlight = false;

			if (output.modelFileNote != null)
			{
				AppendLog("router: " + output.modelFileNote);
			}
			// Brief 15b nit 1: one plain sentence, "used claude-haiku-4-5" only when that worked.
			if (output.windowNote != null)
			{
				AppendLog("router: " + output.windowNote);
			}

			RouteResult route;
			try
			{
				route = Router.FinishRoute(snap.map, vocab, step1, output);
			}
			catch (Exception ex)
			{
				// Never block the order: fall back to the plain order with no scoped data.
				Verse.Log.Warning("[VerbalCommands] router: step 3 failed, order sent without routing: " + ex);
				route = new RouteResult();
				route.routerCalled = true;
				route.router = output;
				route.sliceNote = "the storage data could not be built";
			}
			LogRoute(route);
			SendMainRequest(route, token);
		}

		// One order-window line + one Player.log line per order (brief 15 Step C step 6).
		private void LogRoute(RouteResult route)
		{
			lastRoute = route;
			try
			{
				AppendLog(Router.DescribeForWindow(route));
				Router.LogToPlayerLog(route);
				RouterLog.Write(CurrentCtx(), "route", Fields("result", "older router", "lines", new List<string> { Router.DescribeForWindow(route) }, "rewritten", route.rewritten));
			}
			catch (Exception ex)
			{
				Verse.Log.Warning("[VerbalCommands] router: logging failed: " + ex.Message);
			}
		}

		// Step 4: the main request, unchanged apart from the rewrite/slice sections of the user turn.
		// Brief 15b fix 13 (nit 7): anything that throws while the request is BUILT (tools, system prompt,
		// request JSON) goes to OnSendFailed, so Send is not left disabled.
		private void SendMainRequest(RouteResult route, CancellationToken token)
		{
			int timeoutSeconds = 0;
			try
			{
				VerbalCommandsSettings settings = VerbalCommandsMod.Settings;
				timeoutSeconds = settings.timeoutSeconds;
				string orderText = lastOrderText;
				lastRewrite = route != null ? route.rewritten : null;
				lastSliceText = route != null ? route.sliceText : null;
				string rewriteText = lastRewrite;
				string scopedSliceText = lastSliceText;

				// Step D: offer set_storage only when a storage combination matched AND a slice with storage
				// is attached (Router.FinishRoute drops a slice without storage, brief 15b fix 9).
				// `tools` and `systemPrompt` are reused by SendExplainRequest, so a set_storage tool_use there
				// stays valid.
				bool offerStorage = route != null && route.storageMatched && !string.IsNullOrEmpty(route.sliceText);
				lastSliceStorageIds = offerStorage ? StorageIndex.SliceStorageIds(route.slice) : null;
				// Brief 21: the build tools are offered only when the order (step 1) or the router's rewrite
				// (step 3) said "build" or "construct" (Router.ComboBuild). The build data (anchors, the
				// player's habits, saved layouts, drawn options) then goes into the snapshot the model gets;
				// making it reads the map and updates layout_patterns.json. It never blocks the order.
				bool offerBuild = route != null && route.finalMatch != null && route.finalMatch.combinations.Contains(Router.ComboBuild);
				if (offerBuild)
				{
					BuildTools.AttachBuildData(snap);
				}
				tools = ToolDefinitions.BuildTools(snap, offerStorage, offerBuild);
				// Brief 15b fix 3: the storage rules are part of the prompt only when set_storage is offered.
				systemPrompt = ToolDefinitions.BuildSystemPrompt(settings, offerStorage, offerBuild);

				// Model-agnostic calls: the FALLBACK role (read in Submit).
				LlmTarget fallback = orderFallback ?? LlmTargets.Resolve(LlmRole.Fallback, LlmDefaults.FromSettings(settings, null));
				bool useHeadless = fallback.IsCli;
				string backendName = useHeadless ? "headless CLI" : (fallback.provider == LlmProvider.Anthropic && fallback.baseUrl.Length == 0 ? "API" : fallback.Backend);

				string effortDescription = useHeadless ? settings.effort : AnthropicClient.DescribeEffort(settings, fallback);
				AppendLog("request sent (" + backendName + ", model " + fallback.model + ", effort " + effortDescription +
					(offerStorage ? ", set_storage offered" : "") + (offerBuild ? ", build tools offered" : "") + ")");
				// Brief 29: the older path's main request, as it is built (never the key or headers).
				RouterLog.NotePrompt(CurrentCtx(), systemPrompt);
				RouterLog.Write(CurrentCtx(), "haiku_sent", Fields(
					"call", "main",
					"backend", backendName,
					"model", fallback.model,
					"effort", effortDescription,
					"system_prompt_sha256", RouterLog.Sha256(systemPrompt),
					"set_storage_offered", offerStorage,
					"build_tools_offered", offerBuild,
					"input", Fields("order", orderText, "rewrite", rewriteText, "slice", scopedSliceText, "snapshot", snap.json.ToString(Formatting.None))));

				int requestTimeout = timeoutSeconds;
				if (useHeadless)
				{
					string headlessExePath = fallback.claudeExePath;
					string headlessModel = fallback.model;
					string headlessEffort = settings.effort;
					string snapshotJsonText = snap.json.ToString(Formatting.None);
					string promptForRequest = systemPrompt;
					JArray toolsForRequest = tools;

					Task.Run(async delegate
					{
						try
						{
							LlmResponse response = await HeadlessClient.SendAsync(headlessExePath, headlessModel, headlessEffort,
								promptForRequest, toolsForRequest, orderText, snapshotJsonText, requestTimeout, token, rewriteText, scopedSliceText).ConfigureAwait(false);
							MainThread.Post(delegate { OnResponse(response); });
						}
						catch (Exception ex)
						{
							MainThread.Post(delegate { OnSendFailed(ex, requestTimeout); });
						}
					}, token);
				}
				else
				{
					JObject request = AnthropicClient.BuildInitialRequest(settings, systemPrompt, tools, orderText, snap.json, rewriteText, scopedSliceText);
					request["model"] = fallback.model;

					Task.Run(async delegate
					{
						try
						{
							LlmResponse response = await LlmClient.SendWithEffortFallbackAsync(fallback, request, requestTimeout, token).ConfigureAwait(false);
							MainThread.Post(delegate { OnResponse(response); });
						}
						catch (Exception ex)
						{
							MainThread.Post(delegate { OnSendFailed(ex, requestTimeout); });
						}
					}, token);
				}
			}
			catch (Exception ex)
			{
				Verse.Log.Warning("[VerbalCommands] building the request failed: " + ex);
				OnSendFailed(ex, timeoutSeconds);
			}
		}

		// Brief 15b general rule: plain words only (PlainFailure), never exception text.
		private void OnSendFailed(Exception ex, int timeoutSeconds)
		{
			if (State != OrderState.Sending)
			{
				return;
			}
			JObject failedFields = RouterLog.DescribeError(ex);
			failedFields["call"] = "main";
			failedFields["ok"] = false;
			RouterLog.Write(CurrentCtx(), "haiku_reply", failedFields);
			AppendLog("The order failed: " + PlainFailure.Describe(ex, timeoutSeconds) + ". Nothing was changed.");
			State = OrderState.Faulted;
		}

		private void OnResponse(LlmResponse r)
		{
			if (State != OrderState.Sending)
			{
				return;
			}

			RouterLog.Write(CurrentCtx(), "haiku_reply", Fields(
				"call", "main",
				"ok", true,
				"stop_reason", r.stopReason,
				"reply", r.texts,
				"tool_uses", r.toolUses.Select((JObject t) => t.ToString(Formatting.None)).ToList()));
			AppendLog("response: stop_reason " + r.stopReason);
			if (r.stopReason == "refusal" && !string.IsNullOrEmpty(r.refusalExplanation))
			{
				AppendLog("refused: " + r.refusalExplanation);
			}
			if (r.effortDropped)
			{
				AppendLog(EffortDroppedLine);
			}

			foreach (string text in r.texts)
			{
				ModelTexts.Add(text);
				AppendLog("model: " + text);
			}

			bool anyFault = false;
			foreach (JObject toolUse in r.toolUses)
			{
				string toolName = (string)toolUse["name"];
				AppendLog("response: tool_use " + toolName);
				try
				{
					Plan plan = Executor.Resolve(toolUse, snap, lastSliceStorageIds);
					Plans.Add(plan);
					AppendLog("plan: " + plan.writes.Count + " write(s), " + plan.notes.Count + " note(s)");
					RouterLog.Write(CurrentCtx(), "plan", Fields(
						"lines", new List<string> { toolName + ": " + plan.writes.Count + " write(s)" },
						"risky_lines", new List<string>(),
						"notes", plan.notes,
						"waits_for_confirm", VerbalCommandsMod.Settings == null || VerbalCommandsMod.Settings.confirmBeforeApply));
					foreach (string note in plan.notes)
					{
						AppendLog("note: " + note);
					}
				}
				catch (Fault fault)
				{
					anyFault = true;
					AppendLog("not done: " + fault.Message);
					RouterLog.Write(CurrentCtx(), "route", Fields("result", "failed", "lines", new List<string> { toolName + ": " + fault.Message }));
					VerbalCommandsSettings settings = VerbalCommandsMod.Settings;
					if (settings != null && settings.explainErrorsWithLlm)
					{
						SendExplainRequest(r.content, (string)toolUse["id"], fault.Message);
					}
				}
			}

			if (Plans.Count > 0)
			{
				VerbalCommandsSettings settings = VerbalCommandsMod.Settings;
				bool confirm = settings == null || settings.confirmBeforeApply;
				if (confirm)
				{
					State = OrderState.AwaitingConfirm;
				}
				else
				{
					RouterLog.Write(CurrentCtx(), "decision", Fields("decision", "apply", "auto", true));
					ApplyAll();
				}
			}
			else if (anyFault)
			{
				State = OrderState.Faulted;
			}
			else
			{
				State = OrderState.Done;
			}
		}

		// Brief 15b general rule: the API's own error text is not shown.
		private const string EffortDroppedLine =
			"effort: this model does not accept the effort setting, so the request was sent again without it. " +
			"Leave effort blank or 'high' for this model to skip the retry.";

		private void SendExplainRequest(JArray assistantContent, string toolUseId, string errorText)
		{
			VerbalCommandsSettings settings = VerbalCommandsMod.Settings;
			if (settings == null)
			{
				return;
			}

			int timeoutSeconds = settings.timeoutSeconds;
			// Model-agnostic calls: the explain request goes to the same FALLBACK role as the main request.
			LlmTarget fallback = orderFallback ?? LlmTargets.Resolve(LlmRole.Fallback, LlmDefaults.FromSettings(settings, null));

			if (fallback.IsCli)
			{
				string headlessExePath = fallback.claudeExePath;
				string headlessModel = fallback.model;
				string headlessEffort = settings.effort;
				string orderTextForExplain = lastOrderText;

				Task.Run(async delegate
				{
					try
					{
						LlmResponse response = await HeadlessClient.ExplainAsync(headlessExePath, headlessModel, headlessEffort,
							orderTextForExplain, errorText, timeoutSeconds, CancellationToken.None).ConfigureAwait(false);
						MainThread.Post(delegate { OnExplainResponse(response); });
					}
					catch (Exception ex)
					{
						MainThread.Post(delegate { AppendLog("The explanation could not be fetched: " + PlainFailure.Describe(ex, timeoutSeconds) + "."); });
					}
				});
				return;
			}

			JObject request = AnthropicClient.BuildErrorExplainRequest(
				settings, systemPrompt, tools, lastOrderText, snap.json, assistantContent, toolUseId, errorText, lastRewrite, lastSliceText);
			request["model"] = fallback.model;

			Task.Run(async delegate
			{
				try
				{
					LlmResponse response = await LlmClient.SendWithEffortFallbackAsync(fallback, request, timeoutSeconds, CancellationToken.None).ConfigureAwait(false);
					MainThread.Post(delegate { OnExplainResponse(response); });
				}
				catch (Exception ex)
				{
					MainThread.Post(delegate { AppendLog("The explanation could not be fetched: " + PlainFailure.Describe(ex, timeoutSeconds) + "."); });
				}
			});
		}

		private void OnExplainResponse(LlmResponse r)
		{
			if (r.effortDropped)
			{
				AppendLog(EffortDroppedLine);
			}
			RouterLog.Write(CurrentCtx(), "haiku_reply", Fields("call", "explain", "ok", true, "reply", r.texts));
			foreach (string text in r.texts)
			{
				AppendLog("explanation: " + text);
			}
		}

		public void ApplyAll()
		{
			// Agent inbox (agent_player brief 01): a guard; the dry branch of OnScriptDone never leaves a plan waiting.
			if (Options != null && Options.dry)
			{
				AppendLog("dry run: nothing is applied.");
				return;
			}
			if (Plans.Count == 0)
			{
				State = OrderState.Done;
				return;
			}

			RouterLog.Ctx applyCtx = CurrentCtx();
			if (State == OrderState.AwaitingConfirm)
			{
				RouterLog.Write(applyCtx, "decision", Fields("decision", "apply", "auto", false));
			}
			State = OrderState.Applying;
			// Brief 23: blueprints placed by this apply are what a later "those" sentence of the order means.
			FormPlanner.BeginApply();
			AgentFollow.SetApplyContext(applyCtx != null ? applyCtx.orderId : null);
			int totalWrites = 0;
			bool anyException = false;

			foreach (Plan plan in Plans)
			{
				foreach (Write write in plan.writes)
				{
					// Agent 02 (item 27): the value the write touches, read from the game before and after.
					string before = AgentReadback.Read(plan, write);
					try
					{
						write.apply();
						totalWrites++;
						// An Action has no result of its own: it returned without throwing, so it is "done".
						JObject doneAction = ActionFields(plan, write, "done", null, before, AgentReadback.Read(plan, write));
						LastActions.Add(doneAction);
						RouterLog.Write(applyCtx, "action", doneAction);
					}
					catch (Exception ex)
					{
						anyException = true;
						JObject refusedAction = ActionFields(plan, write, "refused", PlainFailure.Describe(ex) + " | " + ex.Message, before, AgentReadback.Read(plan, write));
						LastActions.Add(refusedAction);
						RouterLog.Write(applyCtx, "action", refusedAction);
						Verse.Log.Error("[VerbalCommands] Exception applying write '" + write.description + "': " + ex);
						AppendLog("Could not apply '" + write.description + "': " + PlainFailure.Describe(ex) + ".");
					}
				}
			}

			// Agent 02 (item 30): follow what the existing forms started (blueprints, research), then forget the order id.
			AgentFollow.AfterApply(Plans);
			AgentFollow.SetApplyContext(null);
			AppendLog("applied " + totalWrites + " write(s)");
			// Agent inbox (agent_player brief 01): the actions say which writes threw.
			LastResult = "applied";
			State = anyException ? OrderState.Faulted : OrderState.Done;
			Plans.Clear();
			ConfirmLines.Clear();

			// Brief 15b fix 15 (nit 11): closing the window during the main call still lets it finish and
			// apply; a RimWorld message says so. Messages.Message(string, MessageTypeDef, bool)
			// (Verse\Messages.cs:47); MessageTypeDefOf.TaskCompletion (RimWorld\MessageTypeDefOf.cs:20), sound
			// Message_TaskCompletion (Core Defs\Misc\MessageTypeDefs\MessageTypeDef.xml). RejectInput
			// (RimWorld\MessageTypeDefOf.cs:26) for the plain failure line, as VerbalCommandsMod uses it.
			if (totalWrites > 0)
			{
				Messages.Message("VerbalCommands_ChangesApplied".Translate(), MessageTypeDefOf.TaskCompletion, false);
			}
			if (anyException)
			{
				Messages.Message("VerbalCommands_SomeChangesFailed".Translate(), MessageTypeDefOf.RejectInput, false);
			}
		}

		public void CancelPending()
		{
			CancelPending("cancel");
		}

		// decisionName goes into the router log: "cancel" (the Cancel button) or "cleared" (Clear conversation).
		private void CancelPending(string decisionName)
		{
			if (State == OrderState.AwaitingConfirm || State == OrderState.Sending)
			{
				RouterLog.Write(CurrentCtx(), "decision", Fields("decision", decisionName, "state", State == OrderState.Sending ? "request in flight" : "plan waiting for confirm"));
			}
			if (cts != null)
			{
				cts.Cancel();
			}
			routerInFlight = false;
			scriptInFlight = false;
			if (State == OrderState.AwaitingConfirm)
			{
				Plans.Clear();
				ConfirmLines.Clear();
				AppendLog("cancelled: plan discarded");
				LastResult = "cancelled"; // Agent inbox (agent_player brief 01)
			}
			else if (State == OrderState.Sending)
			{
				AppendLog("cancelled: request in flight");
				LastResult = "cancelled"; // Agent inbox (agent_player brief 01)
			}
			State = OrderState.Idle;
		}

		// Clear conversation button: cancels a run in progress (as Cancel does), then drops everything one order
		// leaves for the next (the window log, plan, router question, last order's text and route). Files on disk
		// (transcripts.jsonl) are not touched.
		public void ClearConversation()
		{
			CancelPending("cleared");
			RouterLog.Write(CurrentCtx(), "cleared", Fields("last_order", lastOrderText, "had_pending_question", pendingQuestion != null));
			currentOrderId = "";
			currentTurn = 0;
			Log.Clear();
			Plans.Clear();
			ModelTexts.Clear();
			ConfirmLines.Clear();
			pendingQuestion = null;
			clearOrderBox = false;
			lastOrderText = null;
			lastRoute = null;
			lastRewrite = null;
			lastSliceText = null;
			lastSliceStorageIds = null;
			snap = null;
			// Agent inbox (agent_player brief 01)
			Options = null;
			LastResult = null;
			LastOutcome = null;
			LastPlanLines.Clear();
			LastPlanNotes.Clear();
			LastActions.Clear();
			AppendLog("Conversation cleared.");
			Verse.Log.Message("[VerbalCommands] conversation cleared");
		}

		// Brief 15 Step C: called from Dialog_VerbalCommand.PostClose. Closing the window while the
		// ROUTER call is in flight drops the order (the main call must not fire later). A main call
		// already in flight is left alone, exactly as before this brief.
		// Brief 15b fix 7: on the headless path the token is NOT cancelled, so no taskkill runs on close;
		// the claude process finishes (or hits the router cap, whose timeout kill stays as before) and
		// OnRouterDone drops the late result (State is no longer Sending). The API path still cancels its
		// HTTP request (no process involved).
		public void NotifyDialogClosed()
		{
			// Brief 23: a script run is dropped when the window closes (its answer would have nowhere to show).
			if (State == OrderState.Sending && (scriptInFlight || routerInFlight))
			{
				RouterLog.Write(CurrentCtx(), "decision", Fields("decision", "closed", "state", "window closed while the request was in flight"));
			}
			if (State == OrderState.Sending && scriptInFlight)
			{
				scriptInFlight = false;
				if (cts != null)
				{
					cts.Cancel();
				}
				State = OrderState.Idle;
				return;
			}
			if (State == OrderState.Sending && routerInFlight)
			{
				routerInFlight = false;
				if (!orderUseHeadless && cts != null)
				{
					cts.Cancel();
				}
				State = OrderState.Idle;
			}
		}

		private void AppendLog(string line)
		{
			int hour = 0;
			if (snap != null && snap.map != null)
			{
				hour = RimWorld.GenLocalDate.HourOfDay(snap.map);
			}
			else if (Find.CurrentMap != null)
			{
				hour = RimWorld.GenLocalDate.HourOfDay(Find.CurrentMap);
			}
			Log.Add("[" + hour + "h] " + line);
		}
	}
}
