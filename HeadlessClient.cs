using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VerbalCommands
{
	// Brief 15b general rule: a claude -p failure in plain words (never exception text, exit codes or
	// Claude Code's own output). PlainFailure.Describe shows Message as is.
	public sealed class HeadlessFailure : Exception
	{
		// Brief 29: what the mod has about a failed claude -p run, for the router log only (never shown to the
		// player). Null when the failure happened before the process ran.
		public int? exitCode;
		public string stderr;
		public string cliText;

		public HeadlessFailure(string plainReason) : base(plainReason)
		{
		}
	}

	// Alternative backend to AnthropicClient: shells out to the locally installed Claude Code
	// CLI ("claude -p") instead of calling the Anthropic Messages API directly. Never uses
	// --bare, so it rides the user's existing Claude Code subscription login and needs no API
	// key. Returns the same LlmResponse shape AnthropicClient does, with toolUses populated as
	// synthetic tool_use blocks, so OrderController.OnResponse and Executor.Resolve need no
	// changes.
	//
	// Everything here runs on a background thread (invoked via Task.Run from OrderController)
	// and never touches any RimWorld game object. Results are handed back to the caller's
	// continuation, which is responsible for posting to MainThread before touching game state.
	public static class HeadlessClient
	{
		private const string SendFixedInstruction =
			"The colony snapshot and the player's order follow on stdin. Respond with the required JSON.";

		private const string ExplainFixedInstruction =
			"The order and the fault follow on stdin. In one sentence, explain the problem to the player in plain language.";

		private const string MessageFieldDescription =
			"One or two sentences of plain-language reply to the player, used for a clarification question or " +
			"a refusal when no tool should be called. Send an empty string when tool arrays are used instead.";

		// Claude Code's own install folder for whoever is logged in (%USERPROFILE%\.local\bin); never a fixed user name.
		private static readonly string FallbackExePath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");

		private static string cachedExePath;

		// Brief 15 Step C: rewriteText / scopedSliceText are the router's rewrite and the scoped
		// storage slice (both optional, null = section omitted); same content and labels as the API
		// path's user turn (AnthropicClient.BuildFirstUserContent).
		public static async Task<LlmResponse> SendAsync(string claudeExePathSetting, string model, string effort,
			string systemPrompt, JArray tools, string orderText, string snapshotJsonText, int timeoutSeconds, CancellationToken ct,
			string rewriteText = null, string scopedSliceText = null)
		{
			string claudeExe = ResolveClaudeExePath(claudeExePathSetting);
			JObject schema = BuildJsonSchema(tools);
			string schemaJson = schema.ToString(Formatting.None);

			string systemPromptFile = WriteTempSystemPromptFile(systemPrompt);
			try
			{
				List<string> args = new List<string>();
				args.Add("-p");
				args.Add(SendFixedInstruction);
				args.Add("--output-format");
				args.Add("json");
				args.Add("--json-schema");
				args.Add(schemaJson);
				args.Add("--model");
				args.Add(model);
				args.Add("--effort");
				args.Add(effort);
				args.Add("--system-prompt-file");
				args.Add(systemPromptFile);
				args.Add("--permission-mode");
				args.Add("dontAsk");

				string stdinText = "ORDER:\n" + orderText;
				if (!string.IsNullOrEmpty(rewriteText))
				{
					stdinText += "\n\n" + AnthropicClient.RewriteLabel + "\n" + rewriteText;
				}
				stdinText += "\n\nSNAPSHOT:\n" + snapshotJsonText;
				if (!string.IsNullOrEmpty(scopedSliceText))
				{
					stdinText += "\n\n" + AnthropicClient.ScopedSliceLabel + "\n" + scopedSliceText;
				}

				ProcessResult procResult = await RunProcessAsync(claudeExe, args, stdinText, timeoutSeconds, ct).ConfigureAwait(false);
				JObject envelope = ParseEnvelopeOrThrow("order", procResult);
				string resultText = (string)envelope["result"];

				JObject structured = envelope["structured_output"] as JObject;
				if (structured == null)
				{
					structured = ExtractOutermostJsonObject(resultText);
				}
				if (structured == null)
				{
					throw new HeadlessFailure("Claude Code's answer was not in the expected format");
				}

				return BuildLlmResponseFromStructured(structured, tools, procResult.stdout);
			}
			finally
			{
				try
				{
					File.Delete(systemPromptFile);
				}
				catch
				{
				}
			}
		}

		// Brief 15 Step C: the router call. Same process handling as SendAsync (RunProcessAsync,
		// timeout/cancel, envelope parsing, structured_output with result-text fallback) but with a
		// caller-supplied constant schema and NO --effort (brief: "No output_config.effort /
		// --effort for the router"). Returns the structured JSON object; throws on any failure.
		public static async Task<JObject> SendStructuredAsync(string claudeExePathSetting, string model, string promptArg,
			string systemPrompt, string schemaJson, string stdinText, int timeoutSeconds, CancellationToken ct)
		{
			string claudeExe = ResolveClaudeExePath(claudeExePathSetting);

			string systemPromptFile = WriteTempSystemPromptFile(systemPrompt);
			try
			{
				List<string> args = new List<string>();
				args.Add("-p");
				args.Add(promptArg);
				args.Add("--output-format");
				args.Add("json");
				args.Add("--json-schema");
				args.Add(schemaJson);
				args.Add("--model");
				args.Add(model);
				args.Add("--system-prompt-file");
				args.Add(systemPromptFile);
				args.Add("--permission-mode");
				args.Add("dontAsk");

				ProcessResult procResult = await RunProcessAsync(claudeExe, args, stdinText, timeoutSeconds, ct).ConfigureAwait(false);
				JObject envelope = ParseEnvelopeOrThrow("router", procResult);

				JObject structured = envelope["structured_output"] as JObject;
				if (structured == null)
				{
					structured = ExtractOutermostJsonObject((string)envelope["result"]);
				}
				if (structured == null)
				{
					throw new HeadlessFailure("the answer was not in the expected format");
				}
				return structured;
			}
			finally
			{
				try
				{
					File.Delete(systemPromptFile);
				}
				catch
				{
				}
			}
		}

		// Brief 23: the script router's model call (host function haiku). Same process handling as
		// SendStructuredAsync, but plain text: the system prompt file is haiku_prompt.md, stdin is the
		// user message, no --json-schema and no --effort. Returns the answer text; throws on any failure.
		// envelopeSeen (token counter, 2026-10-05): given the CLI's JSON envelope, for its "usage".
		public static async Task<string> SendTextAsync(string claudeExePathSetting, string model, string promptArg,
			string systemPrompt, string stdinText, int timeoutSeconds, CancellationToken ct, Action<JObject> envelopeSeen = null)
		{
			string claudeExe = ResolveClaudeExePath(claudeExePathSetting);

			string systemPromptFile = WriteTempSystemPromptFile(systemPrompt);
			try
			{
				List<string> args = new List<string>();
				args.Add("-p");
				args.Add(promptArg);
				args.Add("--output-format");
				args.Add("json");
				args.Add("--model");
				args.Add(model);
				args.Add("--system-prompt-file");
				args.Add(systemPromptFile);
				args.Add("--permission-mode");
				args.Add("dontAsk");

				ProcessResult procResult = await RunProcessAsync(claudeExe, args, stdinText, timeoutSeconds, ct).ConfigureAwait(false);
				JObject envelope = ParseEnvelopeOrThrow("router", procResult);
				if (envelopeSeen != null)
				{
					envelopeSeen(envelope);
				}
				string resultText = (string)envelope["result"];
				if (string.IsNullOrEmpty(resultText))
				{
					throw new HeadlessFailure("the answer was empty");
				}
				return resultText;
			}
			finally
			{
				try
				{
					File.Delete(systemPromptFile);
				}
				catch
				{
				}
			}
		}

		public static async Task<LlmResponse> ExplainAsync(string claudeExePathSetting, string model, string effort,
			string orderText, string errorText, int timeoutSeconds, CancellationToken ct)
		{
			string claudeExe = ResolveClaudeExePath(claudeExePathSetting);

			List<string> args = new List<string>();
			args.Add("-p");
			args.Add(ExplainFixedInstruction);
			args.Add("--output-format");
			args.Add("json");
			args.Add("--model");
			args.Add(model);
			args.Add("--effort");
			args.Add(effort);
			args.Add("--permission-mode");
			args.Add("dontAsk");

			string stdinText = "ORIGINAL ORDER:\n" + orderText + "\n\nFAULT:\n" + errorText;

			ProcessResult procResult = await RunProcessAsync(claudeExe, args, stdinText, timeoutSeconds, ct).ConfigureAwait(false);
			JObject envelope = ParseEnvelopeOrThrow("explain", procResult);
			string resultText = (string)envelope["result"];

			LlmResponse response = new LlmResponse();
			response.rawJson = procResult.stdout;
			response.stopReason = "end_turn";
			response.content = new JArray();
			if (!string.IsNullOrEmpty(resultText))
			{
				response.texts.Add(resultText);
				JObject textBlock = new JObject();
				textBlock["type"] = "text";
				textBlock["text"] = resultText;
				response.content.Add(textBlock);
			}
			return response;
		}

		// ---- schema -----------------------------------------------------------------------

		// Flat object schema, one array property per tool, reusing that tool's existing
		// input_schema (deep-cloned so the originals from ToolDefinitions.BuildTools are never
		// mutated). This wrapper itself has no oneOf/anyOf: an empty array means "no calls of this
		// kind". The cloned per-tool schemas can still contain anyOf (brief 15c fix 6): set_storage's
		// nullable hit_points/quality fields are {"anyOf": [object schema, {"type": "null"}]}
		// (ToolDefinitions.cs BuildHitPointsSchema/BuildQualitySchema, ~line 441).
		private static JObject BuildJsonSchema(JArray tools)
		{
			JObject message = new JObject();
			message["type"] = "string";
			message["description"] = MessageFieldDescription;

			JObject properties = new JObject();
			properties["message"] = message;

			JArray required = new JArray();
			required.Add("message");

			foreach (JToken toolTok in tools)
			{
				JObject tool = (JObject)toolTok;
				string name = (string)tool["name"];
				JObject inputSchema = (JObject)tool["input_schema"];
				JObject clonedSchema = (JObject)inputSchema.DeepClone();

				JObject arraySchema = new JObject();
				arraySchema["type"] = "array";
				arraySchema["items"] = clonedSchema;

				properties[name] = arraySchema;
				required.Add(name);
			}

			JObject schema = new JObject();
			schema["type"] = "object";
			schema["properties"] = properties;
			schema["required"] = required;
			schema["additionalProperties"] = false;
			return schema;
		}

		// Flattens the structured_output payload into LlmResponse.texts/toolUses, walking the
		// tools array in its declaration order (not the JSON's own property order, which is not
		// guaranteed) so the result is deterministic.
		private static LlmResponse BuildLlmResponseFromStructured(JObject structured, JArray tools, string rawJson)
		{
			LlmResponse response = new LlmResponse();
			response.rawJson = rawJson;
			response.stopReason = "end_turn";
			response.content = new JArray();

			string message = (string)structured["message"];
			if (!string.IsNullOrEmpty(message))
			{
				response.texts.Add(message);
				JObject textBlock = new JObject();
				textBlock["type"] = "text";
				textBlock["text"] = message;
				response.content.Add(textBlock);
			}

			int index = 0;
			foreach (JToken toolTok in tools)
			{
				string toolName = (string)((JObject)toolTok)["name"];
				JArray calls = structured[toolName] as JArray;
				if (calls == null)
				{
					continue;
				}
				foreach (JToken callInput in calls)
				{
					JObject toolUse = new JObject();
					toolUse["type"] = "tool_use";
					toolUse["id"] = "headless_" + index;
					toolUse["name"] = toolName;
					toolUse["input"] = callInput;
					response.toolUses.Add(toolUse);
					response.content.Add(toolUse);
					index++;
				}
			}

			return response;
		}

		// ---- executable resolution ---------------------------------------------------------

		private static string ResolveClaudeExePath(string claudeExePathSetting)
		{
			if (cachedExePath != null)
			{
				return cachedExePath;
			}

			if (!string.IsNullOrEmpty(claudeExePathSetting) && File.Exists(claudeExePathSetting))
			{
				cachedExePath = claudeExePathSetting;
				return cachedExePath;
			}

			string fromWhere = TryWhereClaude();
			if (fromWhere != null)
			{
				cachedExePath = fromWhere;
				return cachedExePath;
			}

			if (File.Exists(FallbackExePath))
			{
				cachedExePath = FallbackExePath;
				return cachedExePath;
			}

			throw new HeadlessFailure(
				"claude.exe could not be found; set its full path in Options > Mod settings > Verbal Commands, " +
				"or make sure 'claude' is on PATH");
		}

		private static string TryWhereClaude()
		{
			try
			{
				ProcessStartInfo psi = new ProcessStartInfo();
				psi.FileName = "where.exe";
				psi.Arguments = "claude";
				psi.UseShellExecute = false;
				psi.RedirectStandardOutput = true;
				psi.RedirectStandardError = true;
				psi.CreateNoWindow = true;
				psi.StandardOutputEncoding = new UTF8Encoding(false);
				psi.StandardErrorEncoding = new UTF8Encoding(false);

				using (Process p = Process.Start(psi))
				{
					string output = p.StandardOutput.ReadToEnd();
					p.StandardError.ReadToEnd();
					p.WaitForExit(5000);
					if (p.ExitCode != 0)
					{
						return null;
					}
					string[] lines = output.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
					foreach (string line in lines)
					{
						string trimmed = line.Trim();
						if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(trimmed))
						{
							return trimmed;
						}
					}
				}
			}
			catch
			{
				return null;
			}
			return null;
		}

		// ---- process invocation --------------------------------------------------------------

		private sealed class ProcessResult
		{
			public string stdout;
			public string stderr;
			public int exitCode;
		}

		// Runs claude.exe directly (never through cmd.exe), writing stdinText to its stdin and
		// reading stdout/stderr asynchronously so a full pipe buffer can never deadlock the wait.
		// On timeout or external cancellation, kills the whole process tree and throws.
		private static async Task<ProcessResult> RunProcessAsync(string exePath, List<string> args, string stdinText, int timeoutSeconds, CancellationToken ct)
		{
			ProcessStartInfo psi = new ProcessStartInfo();
			psi.FileName = exePath;
			psi.Arguments = BuildArgumentString(args);
			psi.UseShellExecute = false;
			psi.CreateNoWindow = true;
			psi.RedirectStandardInput = true;
			psi.RedirectStandardOutput = true;
			psi.RedirectStandardError = true;
			psi.StandardOutputEncoding = new UTF8Encoding(false);
			psi.StandardErrorEncoding = new UTF8Encoding(false);
			psi.WorkingDirectory = Path.GetTempPath();

			Process process = new Process();
			process.StartInfo = psi;
			process.EnableRaisingEvents = true;

			TaskCompletionSource<bool> exitedTcs = new TaskCompletionSource<bool>();
			process.Exited += delegate { exitedTcs.TrySetResult(true); };

			try
			{
				process.Start();
			}
			catch (Exception)
			{
				process.Dispose();
				throw new HeadlessFailure("claude.exe could not be started");
			}

			try
			{
				Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
				Task<string> stderrTask = process.StandardError.ReadToEndAsync();
				Task stdinTask = WriteStdinAsync(process, stdinText);

				using (CancellationTokenSource timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
				using (CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
				using (linkedCts.Token.Register(delegate { exitedTcs.TrySetResult(false); }, useSynchronizationContext: false))
				{
					bool exited = await exitedTcs.Task.ConfigureAwait(false);
					if (!exited)
					{
						KillProcessTree(process);
						if (timeoutCts.IsCancellationRequested)
						{
							throw new TimeoutException("claude -p timed out after " + timeoutSeconds + "s.");
						}
						throw new OperationCanceledException(ct);
					}
				}

				string stdout = await stdoutTask.ConfigureAwait(false);
				string stderr = await stderrTask.ConfigureAwait(false);
				await stdinTask.ConfigureAwait(false);
				process.WaitForExit();

				ProcessResult result = new ProcessResult();
				result.stdout = stdout;
				result.stderr = stderr;
				result.exitCode = process.ExitCode;
				return result;
			}
			finally
			{
				process.Dispose();
			}
		}

		private static async Task WriteStdinAsync(Process process, string stdinText)
		{
			try
			{
				byte[] bytes = new UTF8Encoding(false).GetBytes(stdinText ?? string.Empty);
				using (Stream stream = process.StandardInput.BaseStream)
				{
					await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
					await stream.FlushAsync().ConfigureAwait(false);
				}
			}
			catch (Exception)
			{
				// The process may already have exited (e.g. an immediate auth failure) and closed
				// its stdin pipe; that surfaces via exit code / stderr instead, not here.
			}
		}

		private static void KillProcessTree(Process process)
		{
			try
			{
				ProcessStartInfo killPsi = new ProcessStartInfo();
				killPsi.FileName = "taskkill.exe";
				killPsi.Arguments = "/PID " + process.Id + " /T /F";
				killPsi.UseShellExecute = false;
				killPsi.CreateNoWindow = true;
				killPsi.RedirectStandardOutput = true;
				killPsi.RedirectStandardError = true;
				using (Process killer = Process.Start(killPsi))
				{
					killer.WaitForExit(5000);
				}
			}
			catch (Exception ex)
			{
				Verse.Log.Warning("[VerbalCommands] Failed to kill claude.exe process tree: " + ex.Message);
			}
			try
			{
				if (!process.HasExited)
				{
					process.Kill();
				}
			}
			catch
			{
			}
		}

		// Standard Windows CRT argv quoting: wrap in ", escape embedded " as \", and double any
		// run of backslashes that immediately precedes a " or the end of the argument.
		private static string BuildArgumentString(List<string> args)
		{
			StringBuilder sb = new StringBuilder();
			for (int i = 0; i < args.Count; i++)
			{
				if (i > 0)
				{
					sb.Append(' ');
				}
				sb.Append(QuoteArgument(args[i]));
			}
			return sb.ToString();
		}

		private static string QuoteArgument(string arg)
		{
			if (arg.Length != 0 && arg.IndexOfAny(new char[] { ' ', '\t', '\n', '\v', '"' }) < 0)
			{
				return arg;
			}

			StringBuilder sb = new StringBuilder();
			sb.Append('"');
			int i = 0;
			while (i < arg.Length)
			{
				int backslashCount = 0;
				while (i < arg.Length && arg[i] == '\\')
				{
					backslashCount++;
					i++;
				}

				if (i == arg.Length)
				{
					sb.Append('\\', backslashCount * 2);
				}
				else if (arg[i] == '"')
				{
					sb.Append('\\', backslashCount * 2 + 1);
					sb.Append('"');
					i++;
				}
				else
				{
					sb.Append('\\', backslashCount);
					sb.Append(arg[i]);
					i++;
				}
			}
			sb.Append('"');
			return sb.ToString();
		}

		// ---- response parsing -----------------------------------------------------------------

		private static string WriteTempSystemPromptFile(string systemPrompt)
		{
			string path = Path.Combine(Path.GetTempPath(), "verbalcommands_sysprompt_" + Guid.NewGuid().ToString("N") + ".txt");
			File.WriteAllText(path, systemPrompt, new UTF8Encoding(false));
			return path;
		}

		private static JObject ParseEnvelopeOrThrow(string label, ProcessResult procResult)
		{
			JObject envelope;
			try
			{
				envelope = JObject.Parse(procResult.stdout);
			}
			catch (Exception)
			{
				HeadlessFailure unreadable = new HeadlessFailure("Claude Code sent an answer that could not be read");
				unreadable.exitCode = procResult.exitCode;
				unreadable.stderr = procResult.stderr;
				unreadable.cliText = procResult.stdout;
				throw unreadable;
			}

			bool isError = envelope["is_error"] != null && (bool)envelope["is_error"];
			string resultText = (string)envelope["result"];

			if (isError || procResult.exitCode != 0)
			{
				HeadlessFailure failed = new HeadlessFailure(DescribeCliFailure(resultText ?? procResult.stderr));
				failed.exitCode = procResult.exitCode;
				failed.stderr = procResult.stderr;
				failed.cliText = resultText;
				throw failed;
			}

			return envelope;
		}

		// Plain-words reason for a failed claude -p run. Claude Code's own text is only searched for a few
		// well-known cases, never shown.
		private static string DescribeCliFailure(string cliText)
		{
			string t = (cliText ?? "").ToLowerInvariant();
			if (t.Contains("/login") || t.Contains("not logged in") || t.Contains("invalid api key"))
			{
				return "Claude Code is not logged in (run claude once and log in)";
			}
			if (t.Contains("model") && (t.Contains("not found") || t.Contains("not exist") || t.Contains("invalid model") || t.Contains("selected model")))
			{
				return "that model name does not exist";
			}
			if (t.Contains("usage limit") || t.Contains("rate limit") || t.Contains("limit reached"))
			{
				return "Claude Code's usage limit has been reached";
			}
			return "Claude Code reported that the request failed";
		}

		// Falls back to the outermost {...} object in `result` when structured_output is absent.
		private static JObject ExtractOutermostJsonObject(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}
			int start = text.IndexOf('{');
			if (start < 0)
			{
				return null;
			}

			int depth = 0;
			bool inString = false;
			bool escape = false;
			for (int i = start; i < text.Length; i++)
			{
				char c = text[i];
				if (inString)
				{
					if (escape)
					{
						escape = false;
					}
					else if (c == '\\')
					{
						escape = true;
					}
					else if (c == '"')
					{
						inString = false;
					}
					continue;
				}

				if (c == '"')
				{
					inString = true;
				}
				else if (c == '{')
				{
					depth++;
				}
				else if (c == '}')
				{
					depth--;
					if (depth == 0)
					{
						string candidate = text.Substring(start, i - start + 1);
						try
						{
							return JObject.Parse(candidate);
						}
						catch
						{
							return null;
						}
					}
				}
			}
			return null;
		}
	}
}
