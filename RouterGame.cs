using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Brief 23: the game side of the script bridge (Bridge.IRouterGame). The script runs off the main thread;
	// every call that touches the game is posted to MainThread and answered through a Task, so the main thread
	// never waits on the script (WHY D8). The model call (haiku) reuses the mod's own clients and settings:
	// AnthropicClient (API key) or HeadlessClient (Claude Code CLI), the model named in router_model.txt
	// (Router.ReadRouterModel; claude-haiku-4-5 unless the player changed it), haiku_prompt.md as system prompt.
	// Model-agnostic calls (2026-10-05): the model is the MAIN role of LlmProviders.cs, so .env can point it at any
	// provider (MAIN_PROVIDER / MAIN_MODEL / MAIN_API_KEY / MAIN_BASE_URL); without .env it is as described above.
	// Fallback (user, 2026-10-05: "Call fail only, and for "cant do it" you make an option"): when the main model's call
	// fails (error, timeout, refusal, cut-off or empty answer), the same prompt goes once to the FALLBACK role. A run
	// made with fallbackRun (the order window's "can't do it" option, OrderController.TryFallbackRun) sends every call
	// to the FALLBACK role from the start.
	public sealed class RouterGame : IRouterGame
	{
		// The model's lines are short; this is generous.
		private const int HaikuMaxTokens = 1024;

		private const string HeadlessInstruction = "The player's order and the colony vocabulary follow on stdin. Answer as the instructions say.";

		private readonly Map map;
		// Model-agnostic calls: the settings page as copied on the main thread; the MAIN role is resolved per call.
		private readonly LlmDefaults llmDefaults;
		private readonly int timeoutSeconds;
		private readonly bool logHaikuReplies;
		// Brief 29: which order and turn this run belongs to (router log records and the Player.log turn number).
		private readonly RouterLog.Ctx ctx;
		private int haikuCalls;
		private readonly bool fallbackRun;
		// The fallback model may be a bigger (reasoning) model: it gets the settings' max output tokens when that is more.
		private readonly int fallbackMaxTokens;
		// Lines for the order window (and the inbox's window_log), taken by OrderController when the run is done.
		private readonly ConcurrentQueue<string> notes = new ConcurrentQueue<string>();

		// Main thread: settings and the model-file path are copied here, never read in the background.
		public RouterGame(Map map, VerbalCommandsSettings settings, string modelFilePath, RouterLog.Ctx ctx, bool fallbackRun = false)
		{
			this.ctx = ctx ?? new RouterLog.Ctx("", 0);
			this.map = map;
			llmDefaults = LlmDefaults.FromSettings(settings, modelFilePath);
			timeoutSeconds = Math.Max(5, settings.timeoutSeconds);
			logHaikuReplies = settings.logHaikuReplies;
			this.fallbackRun = fallbackRun;
			fallbackMaxTokens = Math.Max(HaikuMaxTokens, settings.maxTokens);
		}

		// Main thread, after the run.
		public List<string> TakeNotes()
		{
			List<string> taken = new List<string>();
			string line;
			while (notes.TryDequeue(out line))
			{
				taken.Add(line);
			}
			return taken;
		}

		private void Note(string line)
		{
			notes.Enqueue(line);
			Log(line);
		}


		public object NewRunState()
		{
			return new RunNotes();
		}

		public Task<string> VocabularyAsync(string orderText, CancellationToken token)
		{
			return OnMainThread(() => VocabularyText.Build(map, orderText), token);
		}

		public Task<PlanReply> PlanAsync(PlanRequest request, CancellationToken token)
		{
			return OnMainThread(delegate
			{
				try
				{
					PlanReply reply = FormPlanner.Dispatch(map, request);
					Plan planned = reply != null ? reply.handle as Plan : null;
					if (planned != null)
					{
						try
						{
							planned.slotsJson = JsonConvert.SerializeObject(request.slots);
						}
						catch (Exception)
						{
						}
					}
					return reply;
				}
				catch (Exception ex)
				{
					// A bug in a form: logged for the developer, plain words for the player.
					Verse.Log.Warning("[VerbalCommands] planning '" + request.form + "' failed: " + ex);
					return PlanReply.Fail("That could not be planned because of a problem inside the mod.");
				}
			}, token);
		}

		// Brief 26: a question is read-only and runs on the main thread, as a plan does.
		public Task<QueryReply> QueryAsync(string form, Dictionary<string, object> slots, CancellationToken token)
		{
			return OnMainThread(delegate
			{
				try
				{
					return FormPlanner.Query(map, form, slots);
				}
				catch (Exception ex)
				{
					Verse.Log.Warning("[VerbalCommands] answering '" + form + "' failed: " + ex);
					return QueryReply.Fail("That could not be answered because of a problem inside the mod.");
				}
			}, token);
		}

		public async Task<string> HaikuAsync(string systemPrompt, string input, CancellationToken token)
		{
			LlmTarget first = LlmTargets.Resolve(fallbackRun ? LlmRole.Fallback : LlmRole.Main, llmDefaults);
			try
			{
				return await CallLoggedAsync(first, systemPrompt, input, null, token).ConfigureAwait(false);
			}
			catch (Exception ex) when (!fallbackRun && !token.IsCancellationRequested)
			{
				LlmTarget fallback = LlmTargets.Resolve(LlmRole.Fallback, llmDefaults);
				if (fallback.MissingReason() != null || first.SameModelAs(fallback))
				{
					throw;
				}
				string why = ex.Message;
				Note("The main model (" + first.model + ") could not be used: " + why + " The fallback model (" + fallback.model + ") was asked instead.");
				try
				{
					return await CallLoggedAsync(fallback, systemPrompt, input, why, token).ConfigureAwait(false);
				}
				catch (Exception ex2) when (!token.IsCancellationRequested)
				{
					throw new VerbalCommands.RouterScript.ScriptError("Neither the main model nor the fallback model could be used. Main model: " +
						why + " Fallback model: " + ex2.Message);
				}
			}
		}

		// One model call with its router log records. afterFailure: why the main model was not used (fallback calls).
		private async Task<string> CallLoggedAsync(LlmTarget target, string systemPrompt, string input, string afterFailure, CancellationToken token)
		{
			int call = Interlocked.Increment(ref haikuCalls);
			string model = target.model;
			RouterLog.NotePrompt(ctx, systemPrompt);
			JObject sent = new JObject();
			sent["call"] = call;
			sent["role"] = target.role == LlmRole.Main ? "main" : "fallback";
			if (afterFailure != null)
			{
				sent["after_main_failure"] = afterFailure;
			}
			// "headless" / "api" as before for the two old backends; another provider is named with its server.
			sent["backend"] = target.IsCli ? "headless" : (target.provider == LlmProvider.Anthropic && target.baseUrl.Length == 0 ? "api" : target.Backend);
			sent["model"] = model;
			sent["system_prompt_sha256"] = RouterLog.Sha256(systemPrompt);
			sent["input"] = input;
			RouterLog.Write(ctx, "haiku_sent", sent);

			Stopwatch watch = Stopwatch.StartNew();
			string reply;
			try
			{
				reply = await AskModelAsync(systemPrompt, input, target, token).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				JObject failed = new JObject();
				failed["call"] = call;
				failed["ok"] = false;
				failed["elapsed_ms"] = watch.ElapsedMilliseconds;
				failed["player_message"] = ex.Message;
				Exception raw = lastRawFailure;
				failed.Merge(RouterLog.DescribeError(raw ?? ex));
				RouterLog.Write(ctx, "haiku_reply", failed);
				throw;
			}
			JObject replied = new JObject();
			replied["call"] = call;
			replied["ok"] = true;
			replied["elapsed_ms"] = watch.ElapsedMilliseconds;
			replied["reply"] = reply;
			RouterLog.Write(ctx, "haiku_reply", replied);
			// Agent inbox (agent_player brief 01): the outbox's router_lines.
			AgentInbox.NoteHaikuReply(ctx.orderId, ctx.turn, reply);
			if (logHaikuReplies)
			{
				// Only the reply: never the key, the headers or the system prompt. Several lines go on one entry.
				string[] replyLines = reply.Replace((char)13 + "", "").Trim().Split((char)10);
				Log("turn " + ctx.turn + ", haiku replied: " + string.Join(" | ", replyLines));
			}
			return reply;
		}

		// The exception the model call really threw (AskModelAsync wraps it in a plain-words ScriptError); read by
		// HaikuAsync for the router log. One run makes its calls one after another.
		private Exception lastRawFailure;

		private async Task<string> AskModelAsync(string systemPrompt, string input, LlmTarget target, CancellationToken token)
		{
			lastRawFailure = null;
			string model = target.model;
			try
			{
				string missing = target.MissingReason();
				if (missing != null)
				{
					throw new VerbalCommands.RouterScript.ScriptError(missing);
				}
				if (target.IsCli)
				{
					// Token meter: the envelope's usage (AgentContext.NoteRouterReply).
					return await HeadlessClient.SendTextAsync(target.claudeExePath, model, HeadlessInstruction, systemPrompt, input, timeoutSeconds, token,
						delegate (JObject envelope) { AgentContext.NoteRouterReply(model, envelope["usage"]); }).ConfigureAwait(false);
				}

				JObject textBlock = new JObject();
				textBlock["type"] = "text";
				textBlock["text"] = input;
				JObject userMessage = new JObject();
				userMessage["role"] = "user";
				userMessage["content"] = new JArray(textBlock);
				JObject body = new JObject();
				body["model"] = model;
				body["max_tokens"] = target.role == LlmRole.Fallback ? fallbackMaxTokens : HaikuMaxTokens;
				body["system"] = systemPrompt;
				body["messages"] = new JArray(userMessage);

				LlmResponse r = await LlmClient.SendAsync(target, body, timeoutSeconds, token).ConfigureAwait(false);
				NoteUsage(model, r);
				if (r.stopReason == "refusal")
				{
					throw new VerbalCommands.RouterScript.ScriptError("The model refused to answer.");
				}
				if (r.stopReason == "max_tokens")
				{
					throw new VerbalCommands.RouterScript.ScriptError("The model's answer was cut off.");
				}
				string text = string.Concat(r.texts.ToArray());
				if (string.IsNullOrWhiteSpace(text))
				{
					throw new VerbalCommands.RouterScript.ScriptError("The model's answer was empty.");
				}
				return text;
			}
			catch (OperationCanceledException ex)
			{
				lastRawFailure = ex;
				throw;
			}
			catch (VerbalCommands.RouterScript.ScriptError)
			{
				throw;
			}
			catch (Exception ex)
			{
				lastRawFailure = ex;
				throw new VerbalCommands.RouterScript.ScriptError("The model could not be reached: " + PlainFailure.Describe(ex, timeoutSeconds) + ".");
			}
		}

		// Token meter: the router model's context on this call, from the reply's usage (Anthropic and OpenAI shapes).
		private static void NoteUsage(string model, LlmResponse r)
		{
			try
			{
				if (!string.IsNullOrEmpty(r.rawJson))
				{
					AgentContext.NoteRouterReply(model, JObject.Parse(r.rawJson)["usage"]);
				}
			}
			catch (JsonException)
			{
			}
		}

		public void Log(string text)
		{
			string line = "[VerbalCommands] router: " + text;
			MainThread.Post(delegate { Verse.Log.Message(line); });
		}

		// Runs work on the main thread (drained by GameComponent_VerbalCommands each frame) and returns its
		// answer. The continuation does not run on the main thread (RunContinuationsAsynchronously), so the
		// script's next step never executes inside the main-thread drain.
		private static Task<T> OnMainThread<T>(Func<T> work, CancellationToken token)
		{
			TaskCompletionSource<T> done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			CancellationTokenRegistration registration = token.Register(delegate { done.TrySetCanceled(); });
			MainThread.Post(delegate
			{
				if (done.Task.IsCompleted)
				{
					return;
				}
				try
				{
					done.TrySetResult(work());
				}
				catch (Exception ex)
				{
					done.TrySetException(ex);
				}
			});
			done.Task.ContinueWith(delegate { registration.Dispose(); }, TaskContinuationOptions.ExecuteSynchronously);
			return done.Task;
		}
	}
}
