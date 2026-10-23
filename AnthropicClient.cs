using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VerbalCommands
{
	public sealed class LlmResponse
	{
		public string rawJson;
		public JArray content;
		public string stopReason;
		public string refusalExplanation;
		public System.Collections.Generic.List<string> texts = new System.Collections.Generic.List<string>();
		public System.Collections.Generic.List<JObject> toolUses = new System.Collections.Generic.List<JObject>();
		public bool effortDropped;
		public string effortDropReason;
	}

	// Thrown by SendAsync on any non-2xx response. Carries the parsed error envelope described at
	// https://platform.claude.com/docs/en/api/errors: {"type":"error","error":{"type":"...","message":"..."},"request_id":"..."}.
	public sealed class AnthropicApiException : Exception
	{
		public readonly int statusCode;
		public readonly string errorType;
		public readonly string errorMessage;
		public readonly string requestId;
		public readonly string rawBody;
		// Model-agnostic calls (LlmProviders.cs): the server's host when it is not Anthropic's own; null for Anthropic.
		public string provider;

		public AnthropicApiException(int statusCode, string errorType, string errorMessage, string requestId, string rawBody)
			: base("HTTP " + statusCode + " " + (errorType ?? "") + ": " + errorMessage)
		{
			this.statusCode = statusCode;
			this.errorType = errorType;
			this.errorMessage = errorMessage;
			this.requestId = requestId;
			this.rawBody = rawBody;
		}
	}

	// Brief 15b general rule: every failure message the player sees says plainly what happened, in
	// words - no error codes, no exception text, no HTTP status numbers. Describe returns a short
	// lower-case clause ("no internet connection, ...") for the order window; any thread.
	public static class PlainFailure
	{
		public static string Describe(Exception ex, int timeoutSeconds = 0)
		{
			Exception e = ex;
			while (e is AggregateException && e.InnerException != null)
			{
				e = e.InnerException;
			}
			if (e is HeadlessFailure || e is Fault || e is LlmFailure)
			{
				// Our own plain-words text.
				return e.Message;
			}
			if (e is TimeoutException)
			{
				return timeoutSeconds > 0 ? "no answer within " + timeoutSeconds + " seconds" : "no answer in time";
			}
			AnthropicApiException api = e as AnthropicApiException;
			if (api != null)
			{
				return DescribeApi(api);
			}
			for (Exception inner = e; inner != null; inner = inner.InnerException)
			{
				if (inner is HttpRequestException || inner is WebException || inner is System.Net.Sockets.SocketException)
				{
					return "no internet connection, or Anthropic's servers could not be reached";
				}
			}
			if (e is OperationCanceledException)
			{
				return "the request was cancelled";
			}
			if (e is JsonException)
			{
				return "the answer could not be read";
			}
			return "something went wrong inside the mod";
		}

		// Error types: https://platform.claude.com/docs/en/api/errors (see AnthropicApiException).
		private static string DescribeApi(AnthropicApiException api)
		{
			string type = api.errorType ?? "";
			// Model-agnostic calls: another provider's servers are named by their host.
			string servers = api.provider == null ? "Anthropic's servers" : "the servers at " + api.provider;
			if (api.statusCode == 401 || type == "authentication_error")
			{
				return "the API key was not accepted";
			}
			if (api.statusCode == 402 || type == "billing_error")
			{
				return "there is a billing problem with the API account";
			}
			if (api.statusCode == 403 || type == "permission_error")
			{
				return "the API key is not allowed to do this";
			}
			if (api.statusCode == 404 || type == "not_found_error")
			{
				return api.provider == null ? "that model name does not exist" : "that model name (or the server address) does not exist";
			}
			if (api.statusCode == 413 || type == "request_too_large")
			{
				return "the order was too large to send";
			}
			if (api.statusCode == 429 || type == "rate_limit_error")
			{
				return "too many requests right now; wait a moment and try again";
			}
			if (api.statusCode == 529 || type == "overloaded_error")
			{
				return servers + " are overloaded right now";
			}
			if (api.statusCode >= 500)
			{
				return servers + " had a problem";
			}
			if (api.errorMessage != null && api.errorMessage.IndexOf("credit balance", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return "the API account has no credit left";
			}
			return "the request was not accepted";
		}
	}

	// Everything here runs on a background thread (invoked via Task.Run from OrderController) and
	// never touches any RimWorld game object. Results are handed back to the caller's continuation,
	// which is responsible for posting to MainThread before touching game state.
	public static class AnthropicClient
	{
		private const string Endpoint = "https://api.anthropic.com/v1/messages";
		private const string AnthropicVersion = "2023-06-01";

		private static readonly HttpClient httpClient;

		// Models that have already rejected top-level output_config.effort this session, memoized so
		// later requests skip straight to omitting it. Read on the main thread in BuildBody, written
		// on background threads in SendWithEffortFallbackAsync.
		private static readonly HashSet<string> modelsRejectingEffort = new HashSet<string>();
		private static readonly object modelsRejectingEffortLock = new object();

		static AnthropicClient()
		{
			ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
			httpClient = new HttpClient();
		}

		// Brief 15 Step C: labels for the two optional first-user-turn sections. Shared with
		// HeadlessClient.SendAsync so both backends send the same text.
		public const string RewriteLabel =
			"ROUTER REWRITE (the same order restated with the colony's exact names by a small routing model; " +
			"if it disagrees with the player's order above, the player's order wins):";
		// Brief 15b fix 4: the filter key (user's wording) follows the label, shared by both backends.
		public const string ScopedSliceLabel =
			"SCOPED STORAGE DATA (JSON: storage units and workbenches in the rooms/benches this order names; " +
			"ids are the storage ids of those units). " +
			"Read a filter from broad to narrow, and let the narrowest entry win. For example, " +
			"{\"all\":true,\"no_cats\":[\"foods\"],\"cats\":[\"meals\"]} means everything except food, but meals are " +
			"allowed. \"hp\" and \"quality\" are the allowed hit point and quality ranges, and \"no_special\" lists " +
			"blocked special filters such as rotten. The data:";

		// Brief 15 Step C: the first user turn, used by BuildInitialRequest AND BuildErrorExplainRequest
		// so the explain request re-sends exactly the same content. Order: original order, router
		// rewrite (optional), snapshot, scoped slice (optional) - brief step 5.
		private static JArray BuildFirstUserContent(string userText, JObject snapshotJson, string rewriteText, string scopedSliceText)
		{
			JArray content = new JArray();

			JObject orderTextBlock = new JObject();
			orderTextBlock["type"] = "text";
			orderTextBlock["text"] = userText;
			content.Add(orderTextBlock);

			if (!string.IsNullOrEmpty(rewriteText))
			{
				JObject rewriteBlock = new JObject();
				rewriteBlock["type"] = "text";
				rewriteBlock["text"] = RewriteLabel + "\n" + rewriteText;
				content.Add(rewriteBlock);
			}

			JObject snapshotBlock = new JObject();
			snapshotBlock["type"] = "text";
			snapshotBlock["text"] = snapshotJson.ToString(Formatting.None);
			content.Add(snapshotBlock);

			if (!string.IsNullOrEmpty(scopedSliceText))
			{
				JObject sliceBlock = new JObject();
				sliceBlock["type"] = "text";
				sliceBlock["text"] = ScopedSliceLabel + "\n" + scopedSliceText;
				content.Add(sliceBlock);
			}

			return content;
		}

		public static JObject BuildInitialRequest(VerbalCommandsSettings s, string systemPrompt, JArray tools, string userText, JObject snapshotJson,
			string rewriteText = null, string scopedSliceText = null)
		{
			JArray content = BuildFirstUserContent(userText, snapshotJson, rewriteText, scopedSliceText);

			JObject userMessage = new JObject();
			userMessage["role"] = "user";
			userMessage["content"] = content;

			JArray messages = new JArray();
			messages.Add(userMessage);

			return BuildBody(s, systemPrompt, tools, messages, toolChoiceAuto: true);
		}

		public static JObject BuildErrorExplainRequest(VerbalCommandsSettings s, string systemPrompt, JArray tools, string userText, JObject snapshotJson, JArray assistantContent, string toolUseId, string errorText,
			string rewriteText = null, string scopedSliceText = null)
		{
			// Re-send the original first user turn (order, router rewrite, snapshot, scoped slice -
			// same builder as BuildInitialRequest), echo the assistant's
			// original content array unmodified, then reply with the tool_result (is_error:true) as
			// the FIRST block of the next user turn, followed by a text block asking for a
			// one-sentence explanation. See sources/anthropic_docs/handle_tool_calls.txt: tool_result
			// blocks must come first in the content array.
			JArray firstUserContent = BuildFirstUserContent(userText, snapshotJson, rewriteText, scopedSliceText);

			JObject firstUserMessage = new JObject();
			firstUserMessage["role"] = "user";
			firstUserMessage["content"] = firstUserContent;

			JObject assistantMessage = new JObject();
			assistantMessage["role"] = "assistant";
			assistantMessage["content"] = assistantContent;

			JObject toolResultBlock = new JObject();
			toolResultBlock["type"] = "tool_result";
			toolResultBlock["tool_use_id"] = toolUseId;
			toolResultBlock["content"] = errorText;
			toolResultBlock["is_error"] = true;

			JObject explainTextBlock = new JObject();
			explainTextBlock["type"] = "text";
			explainTextBlock["text"] = "That tool call could not be applied. In one sentence, explain the problem to the player in plain language.";

			JArray secondUserContent = new JArray();
			secondUserContent.Add(toolResultBlock);
			secondUserContent.Add(explainTextBlock);

			JObject secondUserMessage = new JObject();
			secondUserMessage["role"] = "user";
			secondUserMessage["content"] = secondUserContent;

			JArray messages = new JArray();
			messages.Add(firstUserMessage);
			messages.Add(assistantMessage);
			messages.Add(secondUserMessage);

			return BuildBody(s, systemPrompt, tools, messages, toolChoiceAuto: false);
		}

		private static JObject BuildBody(VerbalCommandsSettings s, string systemPrompt, JArray tools, JArray messages, bool toolChoiceAuto)
		{
			JObject body = new JObject();
			body["model"] = s.model;
			body["max_tokens"] = s.maxTokens;
			body["system"] = systemPrompt;
			body["messages"] = messages;
			body["tools"] = tools;

			JObject toolChoice = new JObject();
			toolChoice["type"] = toolChoiceAuto ? "auto" : "none";
			body["tool_choice"] = toolChoice;

			if (ShouldSendEffort(s.model, s.effort))
			{
				JObject outputConfig = new JObject();
				outputConfig["effort"] = s.effort;
				body["output_config"] = outputConfig;
			}

			return body;
		}

		private static bool ShouldSendEffort(string model, string effort)
		{
			if (string.IsNullOrEmpty(effort))
			{
				return false;
			}
			string trimmed = effort.Trim();
			if (trimmed.Length == 0)
			{
				return false;
			}
			// platform.claude.com/docs/en/build-with-claude/effort: "Setting `effort` to `"high"`
			// produces exactly the same behavior as omitting the `effort` parameter entirely." So
			// sending it is pointless and, on models that reject the field outright, harmful.
			if (string.Equals(trimmed, "high", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			lock (modelsRejectingEffortLock)
			{
				if (modelsRejectingEffort.Contains(model))
				{
					return false;
				}
			}
			return true;
		}

		// For the settings-panel log line. Mirrors ShouldSendEffort's logic but returns a
		// human-readable description instead of a bool.
		public static string DescribeEffort(VerbalCommandsSettings s, LlmTarget target)
		{
			if (target != null && target.provider == LlmProvider.OpenAi)
			{
				return "(not sent: " + target.providerName + " model)";
			}
			return DescribeEffort(target != null ? target.model : s.model, s.effort);
		}

		public static string DescribeEffort(VerbalCommandsSettings s)
		{
			return DescribeEffort(s.model, s.effort);
		}

		private static string DescribeEffort(string model, string effort)
		{
			string trimmed = effort == null ? "" : effort.Trim();
			if (trimmed.Length == 0)
			{
				return "(blank: not sent)";
			}
			if (string.Equals(trimmed, "high", StringComparison.OrdinalIgnoreCase))
			{
				return trimmed + " (not sent: API default)";
			}
			lock (modelsRejectingEffortLock)
			{
				if (modelsRejectingEffort.Contains(model))
				{
					return trimmed + " (not sent: model rejected it earlier this session)";
				}
			}
			return trimmed;
		}

		// Model-agnostic calls: baseUrl is MAIN_/FALLBACK_BASE_URL from .env for a server that speaks the Messages API;
		// null or "" = Anthropic's own endpoint.
		public static string MessagesUrl(string baseUrl)
		{
			if (string.IsNullOrEmpty(baseUrl))
			{
				return Endpoint;
			}
			string b = baseUrl.Trim().TrimEnd('/');
			if (b.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
			{
				return b;
			}
			if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
			{
				return b + "/messages";
			}
			return b + "/v1/messages";
		}

		public static async Task<LlmResponse> SendAsync(JObject requestBody, string apiKey, int timeoutSeconds, CancellationToken ct, string baseUrl = null)
		{
			string json = JsonConvert.SerializeObject(requestBody);
			string endpoint = MessagesUrl(baseUrl);

			using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint))
			{
				request.Content = new StringContent(json, Encoding.UTF8, "application/json");
				request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
				request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

				using (CancellationTokenSource timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
				using (CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
				{
					HttpResponseMessage response;
					string body;
					try
					{
						response = await httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
						body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
					}
					catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
					{
						throw new TimeoutException("Request to Anthropic API timed out after " + timeoutSeconds + "s.");
					}

					if (!response.IsSuccessStatusCode)
					{
						int statusCode = (int)response.StatusCode;
						string errorType = null;
						string errorMessage = body;
						string requestId = null;
						try
						{
							JObject errRoot = JObject.Parse(body);
							JToken errObj = errRoot["error"];
							if (errObj != null)
							{
								errorType = (string)errObj["type"];
								errorMessage = (string)errObj["message"];
							}
							requestId = (string)errRoot["request_id"];
						}
						catch
						{
							errorType = null;
							errorMessage = body;
						}
						AnthropicApiException api = new AnthropicApiException(statusCode, errorType, errorMessage, requestId, body);
						if (!string.IsNullOrEmpty(baseUrl))
						{
							api.provider = LlmTarget.HostOf(endpoint);
						}
						throw api;
					}

					return ParseResponse(body);
				}
			}
		}

		// Sends the request; if the model rejects top-level output_config.effort, retries once
		// without it and reports the drop back to the caller via LlmResponse.effortDropped.
		public static async Task<LlmResponse> SendWithEffortFallbackAsync(JObject requestBody, string model, string apiKey, int timeoutSeconds, CancellationToken ct, string baseUrl = null)
		{
			try
			{
				return await SendAsync(requestBody, apiKey, timeoutSeconds, ct, baseUrl).ConfigureAwait(false);
			}
			catch (AnthropicApiException ex) when (IsEffortRejection(ex, requestBody))
			{
				lock (modelsRejectingEffortLock)
				{
					modelsRejectingEffort.Add(model);
				}
				JObject clone = (JObject)requestBody.DeepClone();
				clone.Remove("output_config");
				LlmResponse retryResult = await SendAsync(clone, apiKey, timeoutSeconds, ct, baseUrl).ConfigureAwait(false);
				retryResult.effortDropped = true;
				retryResult.effortDropReason = ex.errorMessage;
				return retryResult;
			}
		}

		// The exact message text returned when a non-supporting model receives top-level
		// output_config.effort is NOT documented anywhere (checked platform.claude.com/docs/en/api/errors
		// and /build-with-claude/effort, 2026-09-02); only the per-message (beta) variant is documented
		// ("output_config.effort requires a model that supports per-turn effort; this model does not").
		// This substring test is therefore the best available signal and is heuristic, not exact.
		// Only one retry, and only when we actually sent output_config in the first place.
		private static bool IsEffortRejection(AnthropicApiException ex, JObject requestBody)
		{
			return ex.statusCode == 400
				&& ex.errorType == "invalid_request_error"
				&& requestBody["output_config"] != null
				&& ex.errorMessage != null
				&& (ex.errorMessage.IndexOf("effort", StringComparison.OrdinalIgnoreCase) >= 0
					|| ex.errorMessage.IndexOf("output_config", StringComparison.OrdinalIgnoreCase) >= 0);
		}

		private static LlmResponse ParseResponse(string rawJson)
		{
			LlmResponse result = new LlmResponse();
			result.rawJson = rawJson;

			JObject root = JObject.Parse(rawJson);
			result.stopReason = (string)root["stop_reason"];

			JToken stopDetails = root["stop_details"];
			if (stopDetails != null && stopDetails.Type == JTokenType.Object)
			{
				result.refusalExplanation = (string)stopDetails["explanation"];
			}

			JArray content = root["content"] as JArray;
			result.content = content ?? new JArray();

			foreach (JToken block in result.content)
			{
				string type = (string)block["type"];
				if (type == "text")
				{
					result.texts.Add((string)block["text"]);
				}
				else if (type == "tool_use")
				{
					result.toolUses.Add((JObject)block);
				}
			}

			return result;
		}
	}
}
