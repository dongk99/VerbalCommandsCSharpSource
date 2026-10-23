using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Verse;

namespace VerbalCommands
{
	// Model-agnostic calls (user, 2026-10-05: "i need this to be model agnostic so any models can theoretically run
	// this, including initial haiku (router) part and fallback part"). Two roles, each with its own provider:
	//   Main     - the first (router) model that reads every order (router.txt's haiku(), the older path's router
	//              call). Model: MAIN_MODEL in .env, else router_model.txt (claude-haiku-4-5).
	//   Fallback - the bigger model the older path (sentence router off) sends the whole order to, with tools.
	//              Model: FALLBACK_MODEL in .env, else the settings page's "Fallback model" (settings.model).
	// Providers: anthropic (Messages API), openai (any OpenAI-compatible /chat/completions server: OpenAI, Ollama,
	// LM Studio, OpenRouter, Gemini's compatible endpoint, vLLM, ...), claude_cli (HeadlessClient, `claude -p`).
	// A role with nothing in .env behaves exactly as before: anthropic with the settings key, or claude_cli when
	// "Use Claude Code CLI" is ticked.
	public enum LlmRole
	{
		Main,
		Fallback
	}

	public enum LlmProvider
	{
		Anthropic,
		OpenAi,
		ClaudeCli
	}

	// Plain-words failure of a non-Anthropic provider (PlainFailure.Describe shows Message as is).
	public sealed class LlmFailure : Exception
	{
		public LlmFailure(string plainReason) : base(plainReason)
		{
		}
	}

	// What the settings page says, copied on the main thread (settings are never read in the background).
	public sealed class LlmDefaults
	{
		public string apiKey = "";
		public bool useHeadless;
		public string claudeExePath = "";
		public string fallbackModel = "";
		public string routerModelFilePath;

		// Main thread.
		public static LlmDefaults FromSettings(VerbalCommandsSettings s, string routerModelFilePath)
		{
			LlmDefaults d = new LlmDefaults();
			if (s != null)
			{
				d.apiKey = s.apiKey ?? "";
				d.useHeadless = s.useHeadless;
				d.claudeExePath = s.claudeExePath ?? "";
				d.fallbackModel = s.model ?? "";
			}
			d.routerModelFilePath = routerModelFilePath;
			LlmEnv.FilePath(); // resolve the folder here, on the main thread
			return d;
		}
	}

	// One role, resolved: which provider, model, key and server address a call uses.
	public sealed class LlmTarget
	{
		public LlmRole role;
		public LlmProvider provider;
		public string providerName = "anthropic"; // as written in .env (a preset name stays as written)
		public string model = "";
		public string apiKey = "";
		public string baseUrl = "";                // "" = the provider's own default
		public string claudeExePath = "";
		public string modelNote;                   // router_model.txt could not be read (Main only)
		public bool providerFromEnv;
		public bool modelFromEnv;
		public bool keyFromEnv;

		public bool IsCli { get { return provider == LlmProvider.ClaudeCli; } }

		public string RolePrefix { get { return role == LlmRole.Main ? "MAIN" : "FALLBACK"; } }

		// For log lines: "anthropic", "openai at localhost:11434", "claude_cli".
		public string Backend
		{
			get
			{
				if (provider == LlmProvider.ClaudeCli)
				{
					return "claude_cli";
				}
				if (baseUrl.Length == 0)
				{
					return providerName;
				}
				return providerName + " at " + HostOf(baseUrl);
			}
		}

		// Null when the role can be called; otherwise one plain sentence for the order window.
		public string MissingReason()
		{
			if (provider != LlmProvider.Anthropic || apiKey.Length > 0)
			{
				return null;
			}
			if (!providerFromEnv && !keyFromEnv)
			{
				// The wording the mod always used.
				return "No API key set. Enter one in Options > Mod settings > Verbal Commands.";
			}
			return "No API key for the " + (role == LlmRole.Main ? "main (router)" : "fallback") + " model: set " + RolePrefix +
				"_API_KEY in " + LlmEnv.FilePath() + ", or the key in Options > Mod settings > Verbal Commands.";
		}

		// Same provider, server and model: asking it again is no fallback.
		public bool SameModelAs(LlmTarget other)
		{
			return other != null && provider == other.provider && string.Equals(baseUrl, other.baseUrl, StringComparison.OrdinalIgnoreCase) &&
				string.Equals(model, other.model, StringComparison.OrdinalIgnoreCase);
		}

		public static string HostOf(string url)
		{
			try
			{
				return new Uri(url).Authority;
			}
			catch (Exception)
			{
				return url;
			}
		}
	}

	public static class LlmTargets
	{
		// Any thread (file reads only). readRouterModelFile: Main reads router_model.txt for its model when .env has
		// no MAIN_MODEL; pass false on the main thread when only the provider and key are wanted.
		public static LlmTarget Resolve(LlmRole role, LlmDefaults d, bool readRouterModelFile = true)
		{
			LlmTarget t = new LlmTarget();
			t.role = role;
			string prefix = t.RolePrefix;
			Dictionary<string, string> env = LlmEnv.Read();

			string providerText = Value(env, prefix + "_PROVIDER");
			if (providerText != null)
			{
				t.providerFromEnv = true;
				t.providerName = providerText.ToLowerInvariant();
				t.provider = ProviderOf(t.providerName, out string presetUrl);
				t.baseUrl = presetUrl ?? "";
			}
			else
			{
				t.provider = d.useHeadless ? LlmProvider.ClaudeCli : LlmProvider.Anthropic;
				t.providerName = d.useHeadless ? "claude_cli" : "anthropic";
			}
			string baseText = Value(env, prefix + "_BASE_URL");
			if (baseText != null)
			{
				t.baseUrl = baseText;
			}

			string keyText = Value(env, prefix + "_API_KEY");
			if (keyText != null)
			{
				t.keyFromEnv = true;
				t.apiKey = keyText;
			}
			else if (t.provider == LlmProvider.Anthropic)
			{
				// The settings key is an Anthropic key: never sent to another provider.
				t.apiKey = d.apiKey ?? "";
			}

			t.claudeExePath = Value(env, prefix + "_CLAUDE_PATH") ?? d.claudeExePath ?? "";

			string modelText = Value(env, prefix + "_MODEL");
			if (modelText != null)
			{
				t.modelFromEnv = true;
				t.model = modelText;
			}
			else if (role == LlmRole.Fallback)
			{
				t.model = d.fallbackModel ?? "";
			}
			else if (readRouterModelFile && d.routerModelFilePath != null)
			{
				t.model = Router.ReadRouterModel(d.routerModelFilePath, out t.modelNote);
			}
			else
			{
				t.model = Router.DefaultRouterModel;
			}
			return t;
		}

		// "openai" and the presets are the OpenAI format; a preset also gives the server's usual address.
		// Anything not known is read as openai (the common format of other servers), never as anthropic.
		private static LlmProvider ProviderOf(string name, out string presetUrl)
		{
			presetUrl = null;
			switch (name.Replace("-", "_").Replace(" ", "_"))
			{
				case "anthropic":
				case "claude":
					return LlmProvider.Anthropic;
				case "claude_cli":
				case "claude_code":
				case "headless":
				case "cli":
					return LlmProvider.ClaudeCli;
				case "ollama":
					presetUrl = "http://localhost:11434/v1";
					return LlmProvider.OpenAi;
				case "lmstudio":
				case "lm_studio":
					presetUrl = "http://localhost:1234/v1";
					return LlmProvider.OpenAi;
				case "openrouter":
					presetUrl = "https://openrouter.ai/api/v1";
					return LlmProvider.OpenAi;
				case "gemini":
					presetUrl = "https://generativelanguage.googleapis.com/v1beta/openai";
					return LlmProvider.OpenAi;
				default:
					return LlmProvider.OpenAi;
			}
		}

		private static string Value(Dictionary<string, string> env, string key)
		{
			string v;
			if (env != null && env.TryGetValue(key, out v) && v != null && v.Trim().Length > 0)
			{
				return v.Trim();
			}
			return null;
		}
	}

	// <save data>\VerbalCommands\.env: KEY=VALUE lines, # comments, optional quotes and "export ". Read again only
	// when the file's write time changes. Made (all lines commented out, so nothing changes) when missing.
	public static class LlmEnv
	{
		public const string FileName = ".env";

		private static readonly object gate = new object();
		private static string path;
		private static bool pathTried;
		private static DateTime cachedWrite = DateTime.MinValue;
		private static bool cachedExists;
		private static Dictionary<string, string> cached = new Dictionary<string, string>();
		private static bool templateTried;

		// Main thread on first use (GenFilePaths.SaveDataFolderPath, as Router.RouterModelFilePath); cached after.
		public static string FilePath()
		{
			lock (gate)
			{
				if (!pathTried)
				{
					pathTried = true;
					try
					{
						path = Path.Combine(GenFilePaths.SaveDataFolderPath, "VerbalCommands", FileName);
					}
					catch (Exception ex)
					{
						Verse.Log.Warning("[VerbalCommands] .env: the folder path could not be worked out: " + ex.Message);
						path = null;
					}
				}
				return path ?? "";
			}
		}

		public static bool Exists
		{
			get
			{
				string p = FilePath();
				return p.Length > 0 && File.Exists(p);
			}
		}

		// Any thread. A copy; empty when there is no file.
		public static Dictionary<string, string> Read()
		{
			string p = FilePath();
			lock (gate)
			{
				if (p.Length == 0)
				{
					return new Dictionary<string, string>();
				}
				try
				{
					if (!File.Exists(p))
					{
						EnsureTemplate(p);
						cachedExists = false;
						cached = new Dictionary<string, string>();
						return new Dictionary<string, string>();
					}
					DateTime write = File.GetLastWriteTimeUtc(p);
					if (!cachedExists || write != cachedWrite)
					{
						cached = Parse(File.ReadAllLines(p, Encoding.UTF8));
						cachedWrite = write;
						cachedExists = true;
					}
				}
				catch (Exception ex)
				{
					Warn(".env could not be read: " + ex.Message);
				}
				return new Dictionary<string, string>(cached, StringComparer.OrdinalIgnoreCase);
			}
		}

		public static Dictionary<string, string> Parse(IEnumerable<string> lines)
		{
			Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (string raw in lines)
			{
				string line = raw.Trim().TrimStart('﻿');
				if (line.Length == 0 || line.StartsWith("#"))
				{
					continue;
				}
				if (line.StartsWith("export ", StringComparison.Ordinal))
				{
					line = line.Substring(7).TrimStart();
				}
				int eq = line.IndexOf('=');
				if (eq <= 0)
				{
					continue;
				}
				string key = line.Substring(0, eq).Trim();
				string value = line.Substring(eq + 1).Trim();
				if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[value.Length - 1] == value[0])
				{
					value = value.Substring(1, value.Length - 2);
				}
				else
				{
					// An unquoted value ends at " #" (a comment after it).
					int hash = value.IndexOf(" #", StringComparison.Ordinal);
					if (hash >= 0)
					{
						value = value.Substring(0, hash).TrimEnd();
					}
				}
				d[key] = value;
			}
			return d;
		}

		// Once per game start. Every line is a comment, so the file changes nothing until a line is uncommented.
		private static void EnsureTemplate(string p)
		{
			if (templateTried)
			{
				return;
			}
			templateTried = true;
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(p));
				File.WriteAllText(p, Template, new UTF8Encoding(false));
			}
			catch (Exception ex)
			{
				Warn(".env could not be made: " + ex.Message);
			}
		}

		// Any thread: the warning is written on the main thread.
		private static void Warn(string text)
		{
			string line = "[VerbalCommands] " + text;
			MainThread.Post(delegate { Verse.Log.Warning(line); });
		}

		public const string Template =
			"# Verbal Commands: which model does what. A line that starts with # is ignored; remove the # to use it.\n" +
			"# A key that is missing or empty falls back to Options > Mod settings > Verbal Commands.\n" +
			"#\n" +
			"# MAIN     = the first (router) model. It reads every order and turns it into short sentences.\n" +
			"#            No MAIN_MODEL: the model in router_model.txt (claude-haiku-4-5).\n" +
			"# FALLBACK = the bigger model. With the sentence router off, it plans the whole order.\n" +
			"#            No FALLBACK_MODEL: the \"Fallback model\" on the settings page.\n" +
			"#\n" +
			"# <ROLE>_PROVIDER   anthropic | openai | claude_cli, or a preset: ollama, lmstudio, openrouter, gemini.\n" +
			"#                   openai means any server that speaks the OpenAI chat completions format.\n" +
			"#                   No provider: anthropic, or claude_cli when \"Use Claude Code CLI\" is ticked.\n" +
			"# <ROLE>_MODEL      the model name as that server spells it.\n" +
			"# <ROLE>_API_KEY    blank: anthropic uses the settings-page key; other servers get no key\n" +
			"#                   (a local server usually needs none). The settings-page key is never sent elsewhere.\n" +
			"# <ROLE>_BASE_URL   the server address, e.g. http://localhost:11434/v1. Blank: the provider's own.\n" +
			"# <ROLE>_CLAUDE_PATH  claude.exe for claude_cli. Blank: the settings page's path.\n" +
			"#\n" +
			"#MAIN_PROVIDER=anthropic\n" +
			"#MAIN_MODEL=claude-haiku-4-5\n" +
			"#MAIN_API_KEY=\n" +
			"#MAIN_BASE_URL=\n" +
			"#\n" +
			"#FALLBACK_PROVIDER=anthropic\n" +
			"#FALLBACK_MODEL=claude-sonnet-5\n" +
			"#FALLBACK_API_KEY=\n" +
			"#FALLBACK_BASE_URL=\n";
	}

	// The one entry point for a model call that is not claude_cli. The request is always built in the Anthropic
	// Messages shape (as the mod always did); OpenAiClient converts it there and back, so callers keep one shape.
	public static class LlmClient
	{
		public static Task<LlmResponse> SendAsync(LlmTarget t, JObject anthropicBody, int timeoutSeconds, CancellationToken ct)
		{
			if (t.provider == LlmProvider.OpenAi)
			{
				return OpenAiClient.SendAsync(t, anthropicBody, timeoutSeconds, ct);
			}
			return AnthropicClient.SendAsync(anthropicBody, t.apiKey, timeoutSeconds, ct, t.baseUrl);
		}

		// The main/explain requests: Anthropic keeps its effort retry; other providers never get effort.
		public static Task<LlmResponse> SendWithEffortFallbackAsync(LlmTarget t, JObject anthropicBody, int timeoutSeconds, CancellationToken ct)
		{
			if (t.provider == LlmProvider.OpenAi)
			{
				return OpenAiClient.SendAsync(t, anthropicBody, timeoutSeconds, ct);
			}
			return AnthropicClient.SendWithEffortFallbackAsync(anthropicBody, t.model, t.apiKey, timeoutSeconds, ct, t.baseUrl);
		}
	}

	// OpenAI-compatible chat completions. Background threads only; never touches the game.
	public static class OpenAiClient
	{
		public const string DefaultBaseUrl = "https://api.openai.com/v1";

		private static readonly HttpClient httpClient;

		// Servers (model + address) that rejected max_tokens or response_format this session: later requests skip them.
		private static readonly HashSet<string> wantsMaxCompletionTokens = new HashSet<string>();
		private static readonly HashSet<string> rejectsResponseFormat = new HashSet<string>();
		private static readonly object memoLock = new object();

		static OpenAiClient()
		{
			ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
			httpClient = new HttpClient();
			// The caller's own timeout cancels the request; the client's 100 s default would cut slow local models.
			httpClient.Timeout = Timeout.InfiniteTimeSpan;
		}

		public static string ChatUrl(string baseUrl)
		{
			string b = string.IsNullOrEmpty(baseUrl) ? DefaultBaseUrl : baseUrl.Trim().TrimEnd('/');
			if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
			{
				return b;
			}
			return b + "/chat/completions";
		}

		public static async Task<LlmResponse> SendAsync(LlmTarget t, JObject anthropicBody, int timeoutSeconds, CancellationToken ct)
		{
			string memoKey = ChatUrl(t.baseUrl) + "|" + t.model;
			bool maxCompletion;
			bool noFormat;
			lock (memoLock)
			{
				maxCompletion = wantsMaxCompletionTokens.Contains(memoKey);
				noFormat = rejectsResponseFormat.Contains(memoKey);
			}
			// At most two retries: one for the token-limit field name, one without response_format.
			for (int attempt = 0; ; attempt++)
			{
				JObject body = ToChatBody(anthropicBody, t.model, maxCompletion, noFormat);
				try
				{
					return await PostAsync(t, body, timeoutSeconds, ct).ConfigureAwait(false);
				}
				catch (AnthropicApiException ex) when (attempt < 2 && ex.statusCode == 400)
				{
					string m = (ex.errorMessage ?? "") + " " + (ex.errorType ?? "");
					if (!maxCompletion && m.IndexOf("max_completion_tokens", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						maxCompletion = true;
						lock (memoLock)
						{
							wantsMaxCompletionTokens.Add(memoKey);
						}
						continue;
					}
					if (!noFormat && body["response_format"] != null &&
						(m.IndexOf("response_format", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("json_schema", StringComparison.OrdinalIgnoreCase) >= 0))
					{
						noFormat = true;
						lock (memoLock)
						{
							rejectsResponseFormat.Add(memoKey);
						}
						continue;
					}
					throw;
				}
			}
		}

		// Anthropic Messages body -> chat completions body. Handles what the mod sends: system text, text blocks,
		// tool_use / tool_result blocks, tools, tool_choice, max_tokens and output_config.format (json_schema).
		// output_config.effort is Anthropic's own and is dropped.
		public static JObject ToChatBody(JObject a, string model, bool maxCompletionTokens, bool noResponseFormat)
		{
			JObject body = new JObject();
			body["model"] = model;

			JArray messages = new JArray();
			string system = SystemText(a["system"]);
			JObject schema = null;
			JObject format = a["output_config"] != null ? a["output_config"]["format"] as JObject : null;
			if (format != null && (string)format["type"] == "json_schema")
			{
				schema = format["schema"] as JObject;
			}
			if (schema != null)
			{
				// Also in words, for servers that ignore response_format.
				system = (system ?? "") + "\n\nAnswer with only one JSON object (no other text) that matches this JSON schema: " +
					schema.ToString(Formatting.None);
			}
			if (!string.IsNullOrEmpty(system))
			{
				messages.Add(new JObject { ["role"] = "system", ["content"] = system });
			}

			List<string> openToolCalls = new List<string>();
			JArray inMessages = a["messages"] as JArray ?? new JArray();
			foreach (JToken m in inMessages)
			{
				string role = (string)m["role"];
				JToken content = m["content"];
				if (role == "assistant")
				{
					openToolCalls.Clear();
					StringBuilder text = new StringBuilder();
					JArray calls = new JArray();
					foreach (JToken block in Blocks(content))
					{
						string type = (string)block["type"];
						if (type == "text")
						{
							AppendText(text, (string)block["text"]);
						}
						else if (type == "tool_use")
						{
							string id = (string)block["id"] ?? ("call_" + calls.Count);
							openToolCalls.Add(id);
							calls.Add(new JObject
							{
								["id"] = id,
								["type"] = "function",
								["function"] = new JObject
								{
									["name"] = (string)block["name"],
									["arguments"] = (block["input"] ?? new JObject()).ToString(Formatting.None)
								}
							});
						}
					}
					JObject msg = new JObject();
					msg["role"] = "assistant";
					msg["content"] = text.Length > 0 ? (JToken)text.ToString() : JValue.CreateNull();
					if (calls.Count > 0)
					{
						msg["tool_calls"] = calls;
					}
					messages.Add(msg);
					continue;
				}

				// user: tool results first (each its own "tool" message), then the text.
				StringBuilder userText = new StringBuilder();
				HashSet<string> answered = new HashSet<string>();
				foreach (JToken block in Blocks(content))
				{
					string type = (string)block["type"];
					if (type == "tool_result")
					{
						string id = (string)block["tool_use_id"] ?? "";
						answered.Add(id);
						string resultText = ResultText(block["content"]);
						if ((bool?)block["is_error"] == true)
						{
							resultText = "Error: " + resultText;
						}
						messages.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = resultText });
					}
					else if (type == "text")
					{
						AppendText(userText, (string)block["text"]);
					}
				}
				// Every tool call needs an answer in this format; the mod answers only the one that failed.
				foreach (string id in openToolCalls)
				{
					if (!answered.Contains(id))
					{
						messages.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = "Not run." });
					}
				}
				openToolCalls.Clear();
				if (userText.Length > 0)
				{
					messages.Add(new JObject { ["role"] = "user", ["content"] = userText.ToString() });
				}
			}
			body["messages"] = messages;

			JToken maxTokens = a["max_tokens"];
			if (maxTokens != null)
			{
				body[maxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = maxTokens.DeepClone();
			}

			JArray tools = a["tools"] as JArray;
			if (tools != null && tools.Count > 0)
			{
				JArray outTools = new JArray();
				foreach (JToken tool in tools)
				{
					JObject fn = new JObject();
					fn["name"] = (string)tool["name"];
					if (tool["description"] != null)
					{
						fn["description"] = (string)tool["description"];
					}
					fn["parameters"] = (tool["input_schema"] ?? new JObject { ["type"] = "object" }).DeepClone();
					outTools.Add(new JObject { ["type"] = "function", ["function"] = fn });
				}
				body["tools"] = outTools;
				JObject choice = a["tool_choice"] as JObject;
				if (choice != null)
				{
					string ct = (string)choice["type"];
					if (ct == "none")
					{
						body["tool_choice"] = "none";
					}
					else if (ct == "any")
					{
						body["tool_choice"] = "required";
					}
					else if (ct == "tool")
					{
						body["tool_choice"] = new JObject { ["type"] = "function", ["function"] = new JObject { ["name"] = (string)choice["name"] } };
					}
					else
					{
						body["tool_choice"] = "auto";
					}
				}
			}

			if (schema != null && !noResponseFormat)
			{
				body["response_format"] = new JObject
				{
					["type"] = "json_schema",
					["json_schema"] = new JObject { ["name"] = "answer", ["schema"] = schema.DeepClone(), ["strict"] = false }
				};
			}
			return body;
		}

		private static async Task<LlmResponse> PostAsync(LlmTarget t, JObject body, int timeoutSeconds, CancellationToken ct)
		{
			string url = ChatUrl(t.baseUrl);
			string json = JsonConvert.SerializeObject(body);
			using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url))
			{
				request.Content = new StringContent(json, Encoding.UTF8, "application/json");
				if (!string.IsNullOrEmpty(t.apiKey))
				{
					request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + t.apiKey);
				}
				using (CancellationTokenSource timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))))
				using (CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
				{
					HttpResponseMessage response;
					string text;
					try
					{
						response = await httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
						text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
					}
					catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
					{
						throw new TimeoutException("Request to " + LlmTarget.HostOf(url) + " timed out after " + timeoutSeconds + "s.");
					}
					catch (HttpRequestException)
					{
						throw new LlmFailure("the model server at " + LlmTarget.HostOf(url) + " could not be reached");
					}

					if (!response.IsSuccessStatusCode)
					{
						string errorType = null;
						string errorMessage = text;
						try
						{
							JObject root = JObject.Parse(text);
							JToken err = root["error"];
							if (err is JObject)
							{
								errorType = (string)err["type"] ?? (string)err["code"];
								errorMessage = (string)err["message"] ?? text;
							}
							else if (err != null && err.Type == JTokenType.String)
							{
								errorMessage = (string)err;
							}
						}
						catch (Exception)
						{
						}
						string requestId = null;
						IEnumerable<string> ids;
						if (response.Headers.TryGetValues("x-request-id", out ids))
						{
							requestId = ids.FirstOrDefault();
						}
						AnthropicApiException api = new AnthropicApiException((int)response.StatusCode, errorType, errorMessage, requestId, text);
						api.provider = LlmTarget.HostOf(url);
						throw api;
					}
					return ParseChat(text);
				}
			}
		}

		// chat completions reply -> LlmResponse in the Anthropic shape (content blocks, stop_reason names).
		public static LlmResponse ParseChat(string rawJson)
		{
			LlmResponse r = new LlmResponse();
			r.rawJson = rawJson;
			r.content = new JArray();
			JObject root = JObject.Parse(rawJson);
			JArray choices = root["choices"] as JArray;
			if (choices == null || choices.Count == 0)
			{
				throw new LlmFailure("the model server sent an answer with no choices");
			}
			JObject choice = (JObject)choices[0];
			JObject message = choice["message"] as JObject ?? new JObject();

			string text = ContentText(message["content"]);
			if (!string.IsNullOrEmpty(text))
			{
				r.content.Add(new JObject { ["type"] = "text", ["text"] = text });
				r.texts.Add(text);
			}

			JArray calls = message["tool_calls"] as JArray;
			if (calls != null)
			{
				int i = 0;
				foreach (JToken call in calls)
				{
					JObject fn = call["function"] as JObject;
					if (fn == null)
					{
						continue;
					}
					JToken args = fn["arguments"];
					JObject input;
					if (args is JObject)
					{
						input = (JObject)args; // some servers (Ollama) send an object, not a string
					}
					else
					{
						string argText = args != null ? (string)args : null;
						try
						{
							input = string.IsNullOrWhiteSpace(argText) ? new JObject() : JObject.Parse(argText);
						}
						catch (JsonException)
						{
							throw new LlmFailure("the model's tool call was not valid JSON");
						}
					}
					JObject use = new JObject();
					use["type"] = "tool_use";
					use["id"] = (string)call["id"] ?? ("call_" + i);
					use["name"] = (string)fn["name"];
					use["input"] = input;
					r.content.Add(use);
					r.toolUses.Add(use);
					i++;
				}
			}

			string refusal = (string)message["refusal"];
			string finish = (string)choice["finish_reason"];
			if (!string.IsNullOrEmpty(refusal))
			{
				r.stopReason = "refusal";
				r.refusalExplanation = refusal;
			}
			else if (finish == "length")
			{
				r.stopReason = "max_tokens";
			}
			else if (finish == "content_filter")
			{
				r.stopReason = "refusal";
			}
			else if (finish == "tool_calls" || finish == "function_call" || r.toolUses.Count > 0)
			{
				r.stopReason = "tool_use";
			}
			else
			{
				r.stopReason = "end_turn";
			}
			return r;
		}

		// A model asked for JSON may still wrap it in ``` fences or add a line around it: the outermost { ... }.
		public static string JsonObjectText(string text)
		{
			if (text == null)
			{
				return null;
			}
			int start = text.IndexOf('{');
			int end = text.LastIndexOf('}');
			return start >= 0 && end > start ? text.Substring(start, end - start + 1) : text;
		}

		private static IEnumerable<JToken> Blocks(JToken content)
		{
			if (content == null)
			{
				yield break;
			}
			if (content.Type == JTokenType.String)
			{
				yield return new JObject { ["type"] = "text", ["text"] = (string)content };
				yield break;
			}
			JArray arr = content as JArray;
			if (arr == null)
			{
				yield break;
			}
			foreach (JToken b in arr)
			{
				yield return b;
			}
		}

		private static string SystemText(JToken system)
		{
			if (system == null || system.Type == JTokenType.Null)
			{
				return null;
			}
			if (system.Type == JTokenType.String)
			{
				return (string)system;
			}
			StringBuilder sb = new StringBuilder();
			foreach (JToken b in Blocks(system))
			{
				AppendText(sb, (string)b["text"]);
			}
			return sb.ToString();
		}

		private static string ResultText(JToken content)
		{
			if (content == null)
			{
				return "";
			}
			if (content.Type == JTokenType.String)
			{
				return (string)content;
			}
			StringBuilder sb = new StringBuilder();
			foreach (JToken b in Blocks(content))
			{
				AppendText(sb, (string)b["text"]);
			}
			return sb.ToString();
		}

		// message.content: a string, null, or (some servers) an array of {type:"text", text} parts.
		private static string ContentText(JToken content)
		{
			if (content == null || content.Type == JTokenType.Null)
			{
				return null;
			}
			if (content.Type == JTokenType.String)
			{
				return (string)content;
			}
			StringBuilder sb = new StringBuilder();
			JArray parts = content as JArray;
			if (parts != null)
			{
				foreach (JToken p in parts)
				{
					string s = p.Type == JTokenType.String ? (string)p : (string)p["text"];
					if (s != null)
					{
						sb.Append(s);
					}
				}
			}
			return sb.ToString();
		}

		private static void AppendText(StringBuilder sb, string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return;
			}
			if (sb.Length > 0)
			{
				sb.Append("\n\n");
			}
			sb.Append(s);
		}
	}
}
