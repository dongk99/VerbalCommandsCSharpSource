using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Brief 15 Step C: the order router (briefs\15_router_storage_rooms.md, "Step C: router").
	//
	// Flow (driven by OrderController.Submit / OnRouterDone):
	//   1. MAIN THREAD: BuildVocabulary(map) from live game data, Match(orderText). Any required
	//      combination found -> FinishRoute(...) with no router call.
	//   2. BACKGROUND: RunRouterAsync(...) - one small structured-output request (router model from
	//      router_model.txt, same backend as the main model, no effort) that rewrites the order with
	//      exact vocabulary strings. Typo guard retries once with claude-haiku-4-5, only within the
	//      router's own time cap (brief 15b fix 6). Never throws; a failure only means "no routing".
	//   3. MAIN THREAD: FinishRoute(...) re-runs Match on the rewrite, keeps the router's rooms/benches
	//      only where they equal vocabulary entries, and builds the StorageIndex slice when a storage
	//      combination matched.
	// There is no confidence score anywhere: a combination is found or it is not.
	public enum VocabKind
	{
		StorageWord,
		Room,
		Bench,
		Pawn,
		GroupWord,
		GroupName,
		GroupSlot,
		WorkType,
		TimeAssignment,
		// Brief 21: "build" / "construct" (Router.BuildWords).
		BuildWord
	}

	public sealed class VocabEntry
	{
		public string text;     // as the game shows it (sent to the router, used for lookups)
		public string norm;     // Router.Normalize(text)
		public VocabKind kind;
		public bool wordMode;   // Latin/Cyrillic only -> word-boundary matching; else substring
	}

	// Built per order on the main thread; afterwards read-only plain data (safe to read from the
	// background router task).
	public sealed class RouterVocabulary
	{
		public List<VocabEntry> entries = new List<VocabEntry>();
		private readonly HashSet<string> seen = new HashSet<string>();

		public void Add(VocabKind kind, string text)
		{
			if (text == null)
			{
				return;
			}
			string trimmed = text.Trim();
			string norm = Router.Normalize(trimmed);
			if (norm.Length == 0)
			{
				return;
			}
			if (!seen.Add((int)kind + "|" + norm))
			{
				return;
			}
			VocabEntry e = new VocabEntry();
			e.text = trimmed;
			e.norm = norm;
			e.kind = kind;
			e.wordMode = Router.IsLatinOrCyrillicOnly(norm);
			entries.Add(e);
		}

		public List<string> TextsOf(VocabKind kind)
		{
			List<string> result = new List<string>();
			foreach (VocabEntry e in entries)
			{
				if (e.kind == kind)
				{
					result.Add(e.text);
				}
			}
			return result;
		}

		// Exact (normalized, so case-insensitive) lookup of a router-returned string.
		public VocabEntry Find(VocabKind kind, string s)
		{
			string norm = Router.Normalize(s);
			if (norm.Length == 0)
			{
				return null;
			}
			foreach (VocabEntry e in entries)
			{
				if (e.kind == kind && e.norm == norm)
				{
					return e;
				}
			}
			return null;
		}
	}

	// Matched vocabulary strings per kind (the entry's display text), plus the combinations found.
	public sealed class RouteMatch
	{
		public Dictionary<VocabKind, List<string>> found = new Dictionary<VocabKind, List<string>>();
		public List<string> combinations = new List<string>();

		public bool Has(VocabKind kind)
		{
			List<string> list;
			return found.TryGetValue(kind, out list) && list.Count > 0;
		}

		public List<string> Get(VocabKind kind)
		{
			List<string> list;
			return found.TryGetValue(kind, out list) ? list : new List<string>();
		}

		public void AddFound(VocabKind kind, string text)
		{
			List<string> list;
			if (!found.TryGetValue(kind, out list))
			{
				list = new List<string>();
				found[kind] = list;
			}
			foreach (string existing in list)
			{
				if (string.Equals(existing, text, StringComparison.OrdinalIgnoreCase))
				{
					return;
				}
			}
			list.Add(text);
		}
	}

	// Result of the background router call. Strings only; filled on the background thread, read on
	// the main thread after MainThread.Post.
	public sealed class RouterOutput
	{
		public string requestedModel;       // first non-empty line of router_model.txt (or default)
		public string usedModel;            // model whose reply was used; null when routing failed
		public string modelFileNote;        // router_model.txt problem, if any (window only)
		// Brief 15b nit 1: one plain sentence for the order window whenever a router model failed
		// (typo guard worked, both failed, or the default failed). Null when the first call worked.
		public string windowNote;
		public string failurePlayerLog;     // routing failed entirely: plain reasons only (never model text) for Player.log
		public bool cancelled;
		public string rewritten;
		public List<string> rooms = new List<string>();
		public List<string> benches = new List<string>();
		public bool storage;

		public bool Succeeded
		{
			get { return usedModel != null && !cancelled; }
		}
	}

	// What the main request (step 4) gets. Built on the main thread.
	public sealed class RouteResult
	{
		public List<string> step1Combinations = new List<string>();
		public bool routerCalled;
		public RouterOutput router;           // null when the router was not called
		public List<string> step3Combinations = new List<string>();
		public RouteMatch finalMatch;         // matches the scope was built from
		public string rewritten;              // router rewrite sent to the main model (null = none)

		// Step D reads this to decide whether to offer the set_storage tool: true only when a storage
		// combination (storage word + room or bench label) matched in step 1 or step 3.
		public bool storageMatched;

		public JObject slice;                 // null when nothing is attached
		public string sliceText;              // slice.ToString(Formatting.None), null when not attached
		public string sliceNote;              // why no slice (window/Player.log), null when attached
	}

	public static class Router
	{
		public const string DefaultRouterModel = "claude-haiku-4-5";
		private const string LogPrefix = "[VerbalCommands] router: ";

		// Router output is a short rewrite plus two short lists.
		private const int RouterMaxTokens = 1024;

		// Brief 15b fix 6: the router call has its own time cap (whole call, typo-guard retry included)
		// instead of the main request's timeout. No retry after a timeout.
		public const int RouterCapSecondsApi = 20;
		public const int RouterCapSecondsHeadless = 40;

		// Word-start (prefix) matches are only accepted for entries at least this long; shorter entries
		// ("Al", "1", "rec") must match a whole word, so pawn "Al" never matches "all" and slot "1"
		// never matches "12".
		private const int MinPrefixMatchLength = 4;

		public const string ComboStorage = "storage";
		public const string ComboSchedule = "schedule";
		public const string ComboAllowedArea = "allowed area";
		public const string ComboWorkPriorities = "work priorities";
		public const string ComboGroups = "groups";
		// Brief 21: the build tools are offered when this is found (OrderController.SendMainRequest).
		public const string ComboBuild = "build";

		// User, 2026-09-28T00:49:09Z: "placement tool only offered when user (or rather, haiku-filtered user
		// prompt) \"build/construct\". which means haiku can only use the word \"build/construct\"."
		// Word-start matching (MinPrefixMatchLength) also finds "building", "builds", "constructing".
		private static readonly string[] BuildWords = { "build", "construct" };

		// Brief: fixed English storage words.
		private static readonly string[] EnglishStorageWords =
		{
			"shelf", "shelves", "shelving", "storage", "stockpile", "stock pile", "zone"
		};

		// Brief: group word (English "group").
		private const string GroupWord = "group";

		// CONSTANT schema (brief: compiled grammars are cached 24 h and any schema change recompiles,
		// sources\anthropic_docs\structured_outputs.txt:372-382). Vocabulary goes in the message text,
		// never into enums here. All properties required, additionalProperties false (:134, :286).
		public const string RouterSchemaJson =
			"{\"type\":\"object\",\"properties\":{" +
			"\"rewritten\":{\"type\":\"string\"}," +
			"\"rooms\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
			"\"benches\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
			"\"storage\":{\"type\":\"boolean\"}}," +
			"\"required\":[\"rewritten\",\"rooms\",\"benches\",\"storage\"]," +
			"\"additionalProperties\":false}";

		public const string RouterInstruction =
			"You are the order router for a RimWorld colony-management mod. You receive one player order and the colony's " +
			"VOCABULARY: the exact strings the game uses for storage words, room labels, workbench labels, colonist names, " +
			"the pawn-group word, group names and group slot numbers, work types and schedule time assignments. " +
			"Rewrite the order using only the exact vocabulary strings, keeping its meaning: replace every word or phrase " +
			"that refers to one of those things with the matching vocabulary string, copied exactly, and keep everything " +
			"else (numbers, hours, item names, what the player wants done) unchanged. Do not add, drop or carry out any " +
			"instruction and do not answer the player. When nothing in the vocabulary fits a word, keep the word as written. " +
			"Put every room_labels string the order refers to in \"rooms\" and every bench_labels string it refers to in " +
			"\"benches\", copied exactly; use empty arrays when there are none. Set \"storage\" to true only when the order " +
			"is about storage: stockpiles, shelves or other storage, what they may hold, or their priority. " +
			// Brief 21 (user 2026-09-28T00:49:09Z): the only two words that mean build intent.
			"When the player wants something built, placed, set up or made as a building, furniture or a room, say so " +
			"with the word \"build\" or \"construct\" and no other word; never use those two words for anything else.";

		private const string RouterHeadlessPrompt =
			"The player's order and the colony vocabulary follow on stdin. Respond with the required JSON.";

		// ---------------------------------------------------------------------------------------
		// Router model file (brief "User-decided behaviour").
		// ---------------------------------------------------------------------------------------

		// MAIN THREAD (resolves GenFilePaths.SaveDataFolderPath, Verse\GenFilePaths.cs:93-135, which
		// may touch UnityEngine.Application on first use). Same folder as transcripts.jsonl
		// (TranscriptLogger.cs:46); a sibling of Config, never inside it (TranscriptLogger.cs:41-45).
		public static string RouterModelFilePath()
		{
			return Path.Combine(GenFilePaths.SaveDataFolderPath, "VerbalCommands", "router_model.txt");
		}

		// Any thread (plain file I/O). Creates the file containing the default when missing; returns
		// the first non-empty trimmed line, or the default when the file is empty/unreadable.
		public static string ReadRouterModel(string path, out string note)
		{
			note = null;
			try
			{
				if (!File.Exists(path))
				{
					Directory.CreateDirectory(Path.GetDirectoryName(path));
					File.WriteAllText(path, DefaultRouterModel + "\n", new UTF8Encoding(false));
					return DefaultRouterModel;
				}
				foreach (string line in File.ReadAllLines(path))
				{
					string trimmed = line.Trim().Trim('﻿').Trim();
					if (trimmed.Length > 0)
					{
						return trimmed;
					}
				}
				return DefaultRouterModel;
			}
			catch (Exception)
			{
				note = "router_model.txt could not be read or created, so " + DefaultRouterModel + " is used.";
				return DefaultRouterModel;
			}
		}

		// ---------------------------------------------------------------------------------------
		// Step 1: vocabulary (MAIN THREAD).
		// ---------------------------------------------------------------------------------------

		public static RouterVocabulary BuildVocabulary(Map map)
		{
			RouterVocabulary v = new RouterVocabulary();

			// Storage words: fixed English list + the active translation's storage tab label
			// ("TabStorage", RimWorld\ITab_Storage.cs:94; English Keyed ITabs.xml:13 "Storage") and
			// default stockpile name ("Stockpile", RimWorld\ZonePresetNames.cs:12; English Keyed
			// Misc_Gameplay.xml:639 "Stockpile zone"). Translate() falls back to English when the key
			// is missing (Verse\Translator.cs:53-65); TaggedString -> string strips tags
			// (Verse\TaggedString.cs:120-123).
			foreach (string w in EnglishStorageWords)
			{
				v.Add(VocabKind.StorageWord, w);
			}
			v.Add(VocabKind.StorageWord, "TabStorage".Translate());
			v.Add(VocabKind.StorageWord, "Stockpile".Translate());

			// Brief 21: the two build words.
			foreach (string w in BuildWords)
			{
				v.Add(VocabKind.BuildWord, w);
			}

			// Labels of every ThingDef whose thingClass is Building_Storage or a subclass
			// (ThingDef.thingClass, Verse\ThingDef.cs:13; Building_Storage, RimWorld\Building_Storage.cs:9;
			// vanilla: shelf, small shelf, hopper), each plus its plural from the active language
			// (Find.ActiveLanguageWorker, Verse\Find.cs:34; LanguageWorker.Pluralize(string, int),
			// Verse\LanguageWorker.cs:103-106).
			LanguageWorker languageWorker = Find.ActiveLanguageWorker;
			foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if (def.thingClass == null || !typeof(Building_Storage).IsAssignableFrom(def.thingClass) || def.label.NullOrEmpty())
				{
					continue;
				}
				v.Add(VocabKind.StorageWord, def.label);
				try
				{
					v.Add(VocabKind.StorageWord, languageWorker.Pluralize(def.label));
				}
				catch (Exception ex)
				{
					Log.WarningOnce(LogPrefix + "pluralize failed for '" + def.defName + "': " + ex.Message, ("vc_router_plural_" + def.defName).GetHashCode());
				}
			}

			// Room labels as the game shows them (Room.GetRoomRoleLabel, Verse\Room.cs:881-897),
			// for the same proper rooms Snapshot rooms[] lists (StorageIndex.AllProperRooms), minus
			// the generic "room"/"none" roles (brief 15b fix 1, StorageIndex.IsGenericRoomRole).
			foreach (Room room in StorageIndex.AllProperRooms(map))
			{
				try
				{
					if (StorageIndex.IsGenericRoomRole(room.Role) && !StorageIndex.IsGrowRoom(room))
					{
						continue;
					}
					v.Add(VocabKind.Room, StorageIndex.RoomName(room));
				}
				catch (Exception ex)
				{
					Log.WarningOnce(LogPrefix + "room label failed: " + ex.Message, ("vc_router_room_" + room.ID).GetHashCode());
				}
			}

			// Bench labels: the two strings StorageIndex.FindBenchesByLabel resolves (stuff label
			// without hit points / quality, StorageIndex.BenchVocabLabel, and def label Verse\Def.cs:17).
			foreach (Building_WorkTable bench in StorageIndex.AllPlayerBenches(map))
			{
				try
				{
					v.Add(VocabKind.Bench, StorageIndex.BenchVocabLabel(bench));
					v.Add(VocabKind.Bench, bench.def.label);
				}
				catch (Exception ex)
				{
					Log.WarningOnce(LogPrefix + "bench label failed: " + ex.Message, ("vc_router_bench_" + bench.ThingID).GetHashCode());
				}
			}

			// Pawn names: same colonists as Snapshot colonists[] (MapPawns.FreeColonists,
			// Verse\MapPawns.cs:144). LabelShort (Verse\Pawn.cs:956-966) = mutant prefix + nickname
			// (NameTriple.ToStringShort => Nick, Verse\NameTriple.cs:22-43,58); the bare Nick is added
			// too when it differs.
			foreach (Pawn pawn in map.mapPawns.FreeColonists)
			{
				v.Add(VocabKind.Pawn, pawn.LabelShort);
				NameTriple triple = pawn.Name as NameTriple;
				if (triple != null && !string.Equals(triple.Nick, pawn.LabelShort, StringComparison.Ordinal))
				{
					v.Add(VocabKind.Pawn, triple.Nick);
				}
			}

			// Groups: English group word, stored group names, slot numbers 1-9 (every valid slot,
			// Executor.cs:448, so "group 4" in a define_group order matches even while slot 4 is empty).
			v.Add(VocabKind.GroupWord, GroupWord);
			GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
			if (gc != null)
			{
				foreach (KeyValuePair<int, GroupRule> kv in gc.AllGroups)
				{
					if (kv.Value != null)
					{
						v.Add(VocabKind.GroupName, kv.Value.name);
					}
				}
			}
			for (int slot = 1; slot <= 9; slot++)
			{
				v.Add(VocabKind.GroupSlot, slot.ToString());
			}

			// Work type labels: labelShort (the Work tab header, RimWorld\PawnColumnWorker_WorkPriority.cs:48),
			// gerundLabel, pawnLabel (Verse\WorkTypeDef.cs:13-19) and the Def label if set.
			foreach (WorkTypeDef wt in DefDatabase<WorkTypeDef>.AllDefsListForReading)
			{
				v.Add(VocabKind.WorkType, wt.labelShort);
				v.Add(VocabKind.WorkType, wt.gerundLabel);
				v.Add(VocabKind.WorkType, wt.pawnLabel);
				v.Add(VocabKind.WorkType, wt.label);
			}

			// Time assignment labels (Def.label, Verse\Def.cs:17; RimWorld\TimeAssignmentDef.cs:6).
			foreach (TimeAssignmentDef ta in DefDatabase<TimeAssignmentDef>.AllDefsListForReading)
			{
				v.Add(VocabKind.TimeAssignment, ta.label);
			}

			return v;
		}

		// ---------------------------------------------------------------------------------------
		// Matching (pure string code).
		// ---------------------------------------------------------------------------------------

		// Case-insensitive comparison form: NFC, lower-invariant (1:1 per UTF-16 char), curly
		// apostrophes -> ', whitespace runs -> one space, trimmed.
		public static string Normalize(string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return "";
			}
			string n;
			try
			{
				n = s.Normalize(NormalizationForm.FormC);
			}
			catch (ArgumentException)
			{
				n = s;
			}
			n = n.ToLowerInvariant().Replace('’', '\'').Replace('‘', '\'');
			StringBuilder sb = new StringBuilder(n.Length);
			bool lastSpace = false;
			foreach (char c in n)
			{
				if (char.IsWhiteSpace(c))
				{
					if (!lastSpace)
					{
						sb.Append(' ');
					}
					lastSpace = true;
				}
				else
				{
					sb.Append(c);
					lastSpace = false;
				}
			}
			return sb.ToString().Trim();
		}

		// True when every letter is Latin or Cyrillic (digits/punctuation/spaces allowed). Those
		// scripts separate words with spaces, so word-boundary matching applies; anything else
		// (CJK, Hangul, Thai, Greek, ...) uses plain substring matching (brief step 1).
		public static bool IsLatinOrCyrillicOnly(string s)
		{
			foreach (char c in s)
			{
				if (!char.IsLetter(c))
				{
					continue;
				}
				bool latin = c <= 'ɏ' || (c >= 'Ḁ' && c <= 'ỿ') || (c >= 'Ⱡ' && c <= 'Ɀ') || (c >= '꜠' && c <= 'ꟿ');
				bool cyrillic = (c >= 'Ѐ' && c <= 'ԯ') || (c >= 'ᲀ' && c <= '᲏') || (c >= 'ⷠ' && c <= 'ⷿ') || (c >= 'Ꙁ' && c <= 'ꚟ');
				if (!latin && !cyrillic)
				{
					return false;
				}
			}
			return true;
		}

		private sealed class Hit
		{
			public VocabEntry entry;
			public int start;
			public int end;
		}

		// Finds every vocabulary entry in `text`. Word-mode entries must start at a word start and
		// either end at a word end (whole word) or, when at least MinPrefixMatchLength long, be a
		// word start ("kitchen" in "kitchens"). Overlapping hits: the longest wins ("Alice's bedroom"
		// beats pawn "Alice" and time assignment "work" loses to room "workshop"); hits with the exact
		// same span in different kinds are all kept.
		public static RouteMatch Match(string text, RouterVocabulary vocab)
		{
			RouteMatch m = new RouteMatch();
			string t = Normalize(text);
			if (t.Length == 0 || vocab == null)
			{
				return m;
			}

			List<Hit> hits = new List<Hit>();
			foreach (VocabEntry e in vocab.entries)
			{
				int idx = 0;
				while (idx <= t.Length - e.norm.Length)
				{
					int at = t.IndexOf(e.norm, idx, StringComparison.Ordinal);
					if (at < 0)
					{
						break;
					}
					int end = at + e.norm.Length;
					if (!e.wordMode || IsWordMatch(t, at, end, e.norm))
					{
						Hit h = new Hit();
						h.entry = e;
						h.start = at;
						h.end = end;
						hits.Add(h);
					}
					idx = at + 1;
				}
			}

			hits.Sort(delegate (Hit a, Hit b)
			{
				int byLength = (b.end - b.start).CompareTo(a.end - a.start);
				return byLength != 0 ? byLength : a.start.CompareTo(b.start);
			});

			List<Hit> accepted = new List<Hit>();
			foreach (Hit h in hits)
			{
				bool blocked = false;
				foreach (Hit a in accepted)
				{
					bool overlaps = h.start < a.end && a.start < h.end;
					bool sameSpan = h.start == a.start && h.end == a.end;
					if (overlaps && !sameSpan)
					{
						blocked = true;
						break;
					}
				}
				if (!blocked)
				{
					accepted.Add(h);
					m.AddFound(h.entry.kind, h.entry.text);
				}
			}

			m.combinations = Combinations(m, false);
			return m;
		}

		private static bool IsWordMatch(string t, int start, int end, string entryNorm)
		{
			if (start > 0 && char.IsLetterOrDigit(t[start - 1]))
			{
				return false;
			}
			if (end >= t.Length || !char.IsLetterOrDigit(t[end]))
			{
				return true;
			}
			// Word-start match (the text word continues past the entry).
			return entryNorm.Length >= MinPrefixMatchLength && char.IsLetter(entryNorm[entryNorm.Length - 1]);
		}

		// Brief step 2 table. storageHint = the router's "storage": true (step 3 only).
		public static List<string> Combinations(RouteMatch m, bool storageHint)
		{
			List<string> combos = new List<string>();
			bool roomOrBench = m.Has(VocabKind.Room) || m.Has(VocabKind.Bench);
			bool groupRef = m.Has(VocabKind.GroupName) || (m.Has(VocabKind.GroupWord) && m.Has(VocabKind.GroupSlot));
			bool pawnOrGroup = m.Has(VocabKind.Pawn) || groupRef;

			if ((m.Has(VocabKind.StorageWord) || storageHint) && roomOrBench)
			{
				combos.Add(ComboStorage);
			}
			if (pawnOrGroup && m.Has(VocabKind.TimeAssignment))
			{
				combos.Add(ComboSchedule);
			}
			if (pawnOrGroup && m.Has(VocabKind.Room))
			{
				combos.Add(ComboAllowedArea);
			}
			if (pawnOrGroup && m.Has(VocabKind.WorkType))
			{
				combos.Add(ComboWorkPriorities);
			}
			if (m.Has(VocabKind.GroupWord) && (m.Has(VocabKind.GroupSlot) || m.Has(VocabKind.GroupName)))
			{
				combos.Add(ComboGroups);
			}
			// Brief 21: a build word alone is enough (the room or anchor is the main model's to resolve).
			if (m.Has(VocabKind.BuildWord))
			{
				combos.Add(ComboBuild);
			}
			return combos;
		}

		// ---------------------------------------------------------------------------------------
		// Step 2: router call (BACKGROUND THREAD; no game objects).
		// ---------------------------------------------------------------------------------------

		public sealed class RouterCall
		{
			// Model-agnostic calls (2026-10-05): the MAIN role of LlmProviders.cs (provider, model, key, server),
			// resolved in RunRouterAsync from the settings copy and .env. Without .env: the settings key or the
			// Claude Code CLI, and the model in router_model.txt, as before.
			public LlmDefaults defaults;
			public int capSeconds;          // RouterCapSecondsApi / RouterCapSecondsHeadless
			public string modelFilePath;
			public string orderText;
			public string vocabularyJson;
		}

		// MAIN THREAD: vocabulary as JSON text for the router's user message.
		public static string VocabularyJson(RouterVocabulary v)
		{
			JObject o = new JObject();
			o["storage_words"] = new JArray(v.TextsOf(VocabKind.StorageWord));
			o["room_labels"] = new JArray(v.TextsOf(VocabKind.Room));
			o["bench_labels"] = new JArray(v.TextsOf(VocabKind.Bench));
			o["colonist_names"] = new JArray(v.TextsOf(VocabKind.Pawn));
			o["group_word"] = new JArray(v.TextsOf(VocabKind.GroupWord));
			o["group_names"] = new JArray(v.TextsOf(VocabKind.GroupName));
			o["group_slots"] = new JArray(v.TextsOf(VocabKind.GroupSlot));
			o["work_types"] = new JArray(v.TextsOf(VocabKind.WorkType));
			o["time_assignments"] = new JArray(v.TextsOf(VocabKind.TimeAssignment));
			o["build_words"] = new JArray(v.TextsOf(VocabKind.BuildWord));
			return o.ToString(Formatting.None);
		}

		// Never throws. Cancellation -> output.cancelled (the caller's continuation checks the token too).
		public static async Task<RouterOutput> RunRouterAsync(RouterCall call, CancellationToken ct)
		{
			RouterOutput output = new RouterOutput();
			LlmTarget target;
			try
			{
				target = LlmTargets.Resolve(LlmRole.Main, call.defaults ?? new LlmDefaults { routerModelFilePath = call.modelFilePath });
			}
			catch (Exception ex)
			{
				output.requestedModel = DefaultRouterModel;
				output.windowNote = "The router model could not be worked out (" + ex.Message + "). " + WentWithoutRouting;
				output.failurePlayerLog = "router target: " + ex.GetType().Name;
				return output;
			}
			string model = target.model;
			output.requestedModel = model;
			output.modelFileNote = target.modelNote;

			string userText = "ORDER:\n" + call.orderText + "\n\nVOCABULARY (JSON; copy strings exactly):\n" + call.vocabularyJson;

			// Brief 15b fix 6: one cap for the whole router step. The first call gets all of it; the
			// typo-guard retry only gets what is left, and never runs after a timeout.
			int cap = Math.Max(1, call.capSeconds);
			System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

			Exception firstError;
			try
			{
				JObject reply = await CallOnceAsync(call, target, model, userText, cap, ct).ConfigureAwait(false);
				FillFromReply(output, reply, model);
				return output;
			}
			catch (Exception ex)
			{
				if (ct.IsCancellationRequested)
				{
					output.cancelled = true;
					return output;
				}
				firstError = ex;
			}
			string firstReason = ReasonText(firstError, cap);

			// The typo guard below is for a name typed into router_model.txt and an Anthropic/CLI backend: a model set
			// in .env, or another provider, gets no retry with claude-haiku-4-5.
			if (string.Equals(model, DefaultRouterModel, StringComparison.OrdinalIgnoreCase) || target.modelFromEnv || target.provider == LlmProvider.OpenAi)
			{
				output.windowNote = model + " didn't give a usable answer: " + firstReason + ". " + WentWithoutRouting;
				output.failurePlayerLog = model + ": " + firstReason;
				return output;
			}

			// Typo guard (brief): a non-default model failed -> retry once with the default, if time is left.
			string fileModel = "The model in router_model.txt ('" + model + "')";
			int remaining = cap - (int)Math.Ceiling(watch.Elapsed.TotalSeconds);
			if (firstError is TimeoutException || remaining < 1)
			{
				output.windowNote = fileModel + " didn't give a usable answer: " + firstReason + ". There was no time left to try " +
					DefaultRouterModel + ". " + WentWithoutRouting;
				output.failurePlayerLog = "router_model.txt model: " + firstReason + "; no time left for " + DefaultRouterModel;
				return output;
			}
			try
			{
				JObject reply = await CallOnceAsync(call, target, DefaultRouterModel, userText, remaining, ct).ConfigureAwait(false);
				FillFromReply(output, reply, DefaultRouterModel);
				output.windowNote = fileModel + " didn't give a usable answer: " + firstReason + ". " + DefaultRouterModel + " was used instead.";
				return output;
			}
			catch (Exception ex)
			{
				if (ct.IsCancellationRequested)
				{
					output.cancelled = true;
					return output;
				}
				string secondReason = ReasonText(ex, remaining);
				output.windowNote = string.Equals(firstReason, secondReason, StringComparison.Ordinal)
					? fileModel + " didn't give a usable answer, and " + DefaultRouterModel + " didn't either: " + firstReason + ". " + WentWithoutRouting
					: fileModel + " didn't give a usable answer (" + firstReason + "), and " + DefaultRouterModel + " didn't either (" +
						secondReason + "). " + WentWithoutRouting;
				output.failurePlayerLog = "router_model.txt model: " + firstReason + "; " + DefaultRouterModel + ": " + secondReason;
				return output;
			}
		}

		// The user's wording (COMPACT_2026-09-26_brief15.md 2b, nit 1).
		private const string WentWithoutRouting = "Your order went to the main model without storage data.";

		private static async Task<JObject> CallOnceAsync(RouterCall call, LlmTarget target, string model, string userText, int timeoutSeconds, CancellationToken ct)
		{
			string missing = target.MissingReason();
			if (missing != null)
			{
				throw new RouterReplyException(missing);
			}
			if (target.IsCli)
			{
				// Same `claude -p` backend as the main model (HeadlessClient.SendStructuredAsync:
				// --json-schema, --model, no --effort). On timeout the process tree is killed as before.
				return await HeadlessClient.SendStructuredAsync(target.claudeExePath, model, RouterHeadlessPrompt,
					RouterInstruction, RouterSchemaJson, userText, timeoutSeconds, ct).ConfigureAwait(false);
			}

			// Messages API with the player's key: output_config.format = {type: json_schema, schema}
			// (sources\anthropic_docs\structured_outputs.txt:65,122-136,160-161;
			// messages_create.txt:401-424 OutputConfig { effort, format }). No effort, no tools.
			JObject textBlock = new JObject();
			textBlock["type"] = "text";
			textBlock["text"] = userText;
			JObject userMessage = new JObject();
			userMessage["role"] = "user";
			userMessage["content"] = new JArray(textBlock);

			JObject format = new JObject();
			format["type"] = "json_schema";
			format["schema"] = JObject.Parse(RouterSchemaJson);
			JObject outputConfig = new JObject();
			outputConfig["format"] = format;

			JObject body = new JObject();
			body["model"] = model;
			body["max_tokens"] = RouterMaxTokens;
			body["system"] = RouterInstruction;
			body["messages"] = new JArray(userMessage);
			body["output_config"] = outputConfig;

			// Model-agnostic calls: another provider gets the same body converted (OpenAiClient: response_format
			// json_schema, and the schema in the system text for servers that ignore it).
			LlmResponse r = await LlmClient.SendAsync(target, body, timeoutSeconds, ct).ConfigureAwait(false);

			// Refusal / max_tokens replies may not match the schema (structured_outputs.txt:453-473).
			if (r.stopReason == "refusal")
			{
				throw new RouterReplyException("the model refused to answer");
			}
			if (r.stopReason == "max_tokens")
			{
				throw new RouterReplyException("the answer was cut off");
			}
			// "Valid JSON matching your schema in the response's text content block" (:141).
			string json = string.Concat(r.texts.ToArray());
			if (string.IsNullOrEmpty(json))
			{
				throw new RouterReplyException("the answer was empty");
			}
			if (target.provider == LlmProvider.OpenAi)
			{
				// Without a schema-enforcing server the JSON may come in ``` fences or with a line around it.
				json = OpenAiClient.JsonObjectText(json);
			}
			try
			{
				return JObject.Parse(json);
			}
			catch (JsonException)
			{
				throw new RouterReplyException("the answer was not in the expected format");
			}
		}

		private static void FillFromReply(RouterOutput output, JObject reply, string model)
		{
			JValue rewritten = reply["rewritten"] as JValue;
			JArray rooms = reply["rooms"] as JArray;
			JArray benches = reply["benches"] as JArray;
			JValue storage = reply["storage"] as JValue;
			if (rewritten == null || rewritten.Type != JTokenType.String || rooms == null || benches == null
				|| storage == null || storage.Type != JTokenType.Boolean)
			{
				throw new RouterReplyException("the answer was not in the expected format");
			}
			output.rewritten = ((string)rewritten).Trim();
			foreach (JToken tok in rooms)
			{
				if (tok.Type == JTokenType.String)
				{
					output.rooms.Add((string)tok);
				}
			}
			foreach (JToken tok in benches)
			{
				if (tok.Type == JTokenType.String)
				{
					output.benches.Add((string)tok);
				}
			}
			output.storage = (bool)storage;
			output.usedModel = model;
		}

		private sealed class RouterReplyException : Exception
		{
			public RouterReplyException(string message) : base(message)
			{
			}
		}

		// Brief 15b general rule: plain words only (no HTTP numbers, no exception text). The same text
		// goes to the order window and Player.log; it never echoes the order or the rewrite (step 6).
		private static string ReasonText(Exception ex, int timeoutSeconds)
		{
			if (ex is RouterReplyException)
			{
				return ex.Message;
			}
			return PlainFailure.Describe(ex, timeoutSeconds);
		}

		// ---------------------------------------------------------------------------------------
		// Step 3: scope + slice (MAIN THREAD).
		// ---------------------------------------------------------------------------------------

		// step1 = the original order's matches. router = null when step 1 found a combination (or the
		// vocabulary could not be built); otherwise the router output (failed or not).
		public static RouteResult FinishRoute(Map map, RouterVocabulary vocab, RouteMatch step1, RouterOutput router)
		{
			RouteResult result = new RouteResult();
			result.step1Combinations = step1 != null ? step1.combinations : new List<string>();
			result.router = router;
			result.routerCalled = router != null;

			RouteMatch final = step1 ?? new RouteMatch();
			if (router != null && router.Succeeded && vocab != null)
			{
				// Re-run the regex on the rewrite, merged with what step 1 already found in the
				// original, plus the router's lists where they equal vocabulary entries (anything else
				// is dropped - the regex is the validator).
				RouteMatch step3 = Match(router.rewritten, vocab);
				if (step1 != null)
				{
					foreach (KeyValuePair<VocabKind, List<string>> kv in step1.found)
					{
						foreach (string s in kv.Value)
						{
							step3.AddFound(kv.Key, s);
						}
					}
				}
				foreach (string s in router.rooms)
				{
					VocabEntry e = vocab.Find(VocabKind.Room, s);
					if (e != null)
					{
						step3.AddFound(VocabKind.Room, e.text);
					}
				}
				foreach (string s in router.benches)
				{
					VocabEntry e = vocab.Find(VocabKind.Bench, s);
					if (e != null)
					{
						step3.AddFound(VocabKind.Bench, e.text);
					}
				}
				step3.combinations = Combinations(step3, router.storage);
				result.step3Combinations = step3.combinations;
				final = step3;
				if (router.rewritten.Length > 0)
				{
					result.rewritten = router.rewritten;
				}
			}
			result.finalMatch = final;
			result.storageMatched = final.combinations.Contains(ComboStorage);

			// Brief step 4: scoped data only when a storage combination matched.
			if (!result.storageMatched)
			{
				result.sliceNote = "no storage combination";
				return result;
			}
			if (map == null || Find.Maps == null || !Find.Maps.Contains(map))
			{
				result.sliceNote = "map no longer loaded";
				return result;
			}
			try
			{
				List<Room> rooms = new List<Room>();
				foreach (string label in final.Get(VocabKind.Room))
				{
					foreach (Room room in StorageIndex.FindRoomsByLabel(map, label))
					{
						if (!rooms.Contains(room))
						{
							rooms.Add(room);
						}
					}
				}
				List<Building_WorkTable> benches = new List<Building_WorkTable>();
				foreach (string label in final.Get(VocabKind.Bench))
				{
					foreach (Building_WorkTable bench in StorageIndex.FindBenchesByLabel(map, label))
					{
						if (!benches.Contains(bench))
						{
							benches.Add(bench);
						}
					}
				}
				JObject slice = StorageIndex.Build(map, rooms, benches);
				if (slice == null || !slice.HasValues)
				{
					result.sliceNote = "nothing in scope";
					return result;
				}
				// Brief 15b fix 9: Build always emits a "storage" key, so check it has units. No storage
				// in scope -> no slice, and OrderController then does not offer set_storage.
				JArray storageInScope = slice["storage"] as JArray;
				if (storageInScope == null || storageInScope.Count == 0)
				{
					result.sliceNote = "no storage in the named rooms or near the named benches";
					return result;
				}
				result.slice = slice;
				result.sliceText = slice.ToString(Formatting.None);
			}
			catch (Exception ex)
			{
				// Never block the order on the slice.
				Log.Warning(LogPrefix + "building the scoped storage slice failed: " + ex);
				result.sliceNote = "the storage data could not be built";
			}
			return result;
		}

		// ---------------------------------------------------------------------------------------
		// Step 6: logging (one order-window line + one Player.log line per order).
		// ---------------------------------------------------------------------------------------

		// Player.log: no order text, no rewrite, no error free text.
		public static void LogToPlayerLog(RouteResult r)
		{
			Log.Message(LogPrefix + Describe(r, false));
		}

		// Order window: may include the rewrite.
		public static string DescribeForWindow(RouteResult r)
		{
			return "router: " + Describe(r, true);
		}

		private static string Describe(RouteResult r, bool window)
		{
			StringBuilder sb = new StringBuilder();
			sb.Append("step1 ");
			sb.Append(r.step1Combinations.Count > 0 ? "matched [" + string.Join(", ", r.step1Combinations.ToArray()) + "]" : "no combination");

			if (!r.routerCalled)
			{
				sb.Append("; router not called");
			}
			else if (r.router.Succeeded)
			{
				sb.Append("; router called (model ").Append(r.router.usedModel);
				if (!string.Equals(r.router.usedModel, r.router.requestedModel, StringComparison.Ordinal))
				{
					sb.Append(", router_model.txt had '").Append(r.router.requestedModel).Append("'");
				}
				sb.Append(")");
				sb.Append("; step3 ");
				sb.Append(r.step3Combinations.Count > 0 ? "matched [" + string.Join(", ", r.step3Combinations.ToArray()) + "]" : "no combination");
				sb.Append(r.router.storage ? " (router: storage)" : "");
			}
			else
			{
				// The order window already has the plain sentence (RouterOutput.windowNote).
				sb.Append("; router gave no usable answer");
				if (!window && r.router.failurePlayerLog != null)
				{
					sb.Append(" (").Append(r.router.failurePlayerLog).Append(")");
				}
				sb.Append("; original order sent without routing");
			}

			string labels = MatchedLabels(r.finalMatch);
			sb.Append("; labels ").Append(labels.Length > 0 ? labels : "none");

			if (r.sliceText != null)
			{
				sb.Append("; storage slice attached (").Append(r.sliceText.Length).Append(" chars)");
			}
			else
			{
				sb.Append("; no storage slice (").Append(r.sliceNote ?? "none").Append(")");
			}

			if (window && r.rewritten != null)
			{
				sb.Append("; rewrite: \"").Append(r.rewritten).Append("\"");
			}
			return sb.ToString();
		}

		private static string MatchedLabels(RouteMatch m)
		{
			if (m == null)
			{
				return "";
			}
			List<string> parts = new List<string>();
			AppendKind(parts, m, VocabKind.StorageWord, "storage");
			AppendKind(parts, m, VocabKind.Room, "rooms");
			AppendKind(parts, m, VocabKind.Bench, "benches");
			AppendKind(parts, m, VocabKind.Pawn, "pawns");
			AppendKind(parts, m, VocabKind.GroupWord, "group word");
			AppendKind(parts, m, VocabKind.GroupName, "group names");
			AppendKind(parts, m, VocabKind.GroupSlot, "slots");
			AppendKind(parts, m, VocabKind.WorkType, "work");
			AppendKind(parts, m, VocabKind.TimeAssignment, "time");
			AppendKind(parts, m, VocabKind.BuildWord, "build");
			return string.Join(" ", parts.ToArray());
		}

		private static void AppendKind(List<string> parts, RouteMatch m, VocabKind kind, string name)
		{
			if (m.Has(kind))
			{
				parts.Add(name + "=[" + string.Join(", ", m.Get(kind).ToArray()) + "]");
			}
		}
	}
}
