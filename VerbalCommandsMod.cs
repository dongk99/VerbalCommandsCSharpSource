using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace VerbalCommands
{
	public class VerbalCommandsSettings : ModSettings
	{
		public string apiKey = "";
		public string model = "claude-sonnet-5";
		public string effort = "high";
		public int maxTokens = 2048;
		public int timeoutSeconds = 60;
		public bool confirmBeforeApply = true;
		public bool explainErrorsWithLlm = true;
		public bool logRouterAnswers = true;
		public bool logHaikuReplies = true;
		public bool logRouterPlans = true;
		// Brief 29: the mod's own file log of what was sent to the small model, its replies and what was done.
		public bool saveRouterLog = true;
		public string systemPromptExtra = "";
		public bool useHeadless = false;
		public string claudeExePath = "";
		public string groupKeyLayout = "Unset";
		public bool voiceEnabled = true;
		// Brief 11 section A.1: now defaults to true for fresh installs (this field initializer is
		// what applies when LoadedModManager.ReadModSettings<T> finds no settings file at all -
		// sources/RimWorldDecompiled/Verse/LoadedModManager.cs:521-550, `if (File.Exists(...))` is
		// false and it returns `new T()` directly, never calling ExposeData). Existing installs that
		// already have an explicit `false` saved are migrated by the settingsVersion check below.
		public bool voiceToggleEverywhere = true;
		public bool devModeVoiceNoticeSuppressed = false;
		// Brief 11 section A.1: bumped to 1 the first time ExposeData runs on a save with
		// settingsVersion < 1, forcing voiceToggleEverywhere to true even for installs that already
		// have an explicit saved `false` from before this brief.
		public int settingsVersion = 0;
		// Brief 11 section C: opt-in transcript log, off by default (the mod never persisted what the
		// player sends before this).
		public bool saveTranscripts = false;
		// Brief 13 Phase B1 item 1 (user rule 1): "an opt-in mod setting that players turn on when they
		// get poor transcription." Default false. Gates the whole B1 debug-data path (VAD, per-utterance
		// measurements, device records) - see SttEngine.cs WorkerLoop.
		public bool collectVoiceDebug = false;
		// Brief 23: orders go to the script router (Router\router.txt run by the interpreter) instead of
		// the older router + main model path. Default on; off keeps the older path as it was.
		public bool useScriptRouter = true;
		// Agent inbox: off by default (agent_player brief 01, items 1/33). When on, text files in the inbox folder are run as orders.
		public bool agentInbox = false;
		// Tip injection into agent inbox results (TipInjector.cs). On by default; off and no result carries tips
		// (the "tips" field and the "TIP:" lines in answers/reasons). "i need tips on NAME." still answers
		// (user, 2026-10-12: "mod option to turn them off for an agent").
		public bool agentTips = true;
		// The order window's "can't do it" checkbox (OrderController.TryFallbackRun): a "cannot" / "not done" from the
		// main model sends the order once more with the fallback model. Off by default; drawn only in the order window.
		public bool fallbackOnCannot = false;
		// User 2026-10-09: "Record human gameplay (log-based) for training". HumanPlayLog.cs writes
		// REFERENCE_HUMAN_PLAY-<save>.md into the Saves folder while this is on. Default off.
		public bool recordHumanPlay = false;
		// The colonists' walks through doors and the daily door reminders (AgentDoorPathScan.cs; user, 2026-10-14: "add
		// to mod option setting (pathfind cost) gate."). On by default; off and nothing is recorded or reminded.
		public bool pathfindCost = true;
		// Settings gate (user, 2026-10-26): per-category on/off for the text the playing agent gets. Every one is on by default;
		// off and the agent does not get that category's text. The Scribe key is the field name. Read through the
		// VerbalCommandsMod.*On properties (Know-how tips is agentTips above).
		public bool remindBase = true;
		public bool remindFood = true;
		public bool remindPawn = true;
		public bool remindResource = true;
		public bool remindUrgent = true;
		public bool buildPlacementChecks = true;
		public bool buildAdviceNotes = true;

		// Scribe string buffers for the numeric fields drawn with Widgets.TextFieldNumeric.
		[Unsaved(false)]
		private string maxTokensBuffer;

		[Unsaved(false)]
		private string timeoutSecondsBuffer;

		public override void ExposeData()
		{
			base.ExposeData();
			Scribe_Values.Look(ref apiKey, "apiKey", "");
			Scribe_Values.Look(ref model, "model", "claude-sonnet-5");
			Scribe_Values.Look(ref effort, "effort", "high");
			Scribe_Values.Look(ref maxTokens, "maxTokens", 2048);
			Scribe_Values.Look(ref timeoutSeconds, "timeoutSeconds", 60);
			Scribe_Values.Look(ref confirmBeforeApply, "confirmBeforeApply", true);
			Scribe_Values.Look(ref explainErrorsWithLlm, "explainErrorsWithLlm", true);
			Scribe_Values.Look(ref logRouterAnswers, "logRouterAnswers", true);
			Scribe_Values.Look(ref logHaikuReplies, "logHaikuReplies", true);
			Scribe_Values.Look(ref logRouterPlans, "logRouterPlans", true);
			Scribe_Values.Look(ref saveRouterLog, "saveRouterLog", true);
			Scribe_Values.Look(ref systemPromptExtra, "systemPromptExtra", "");
			Scribe_Values.Look(ref useHeadless, "useHeadless", false);
			Scribe_Values.Look(ref claudeExePath, "claudeExePath", "");
			Scribe_Values.Look(ref groupKeyLayout, "groupKeyLayout", "Unset");
			Scribe_Values.Look(ref voiceEnabled, "voiceEnabled", true);
			Scribe_Values.Look(ref voiceToggleEverywhere, "voiceToggleEverywhere", true);
			Scribe_Values.Look(ref devModeVoiceNoticeSuppressed, "devModeVoiceNoticeSuppressed", false);
			Scribe_Values.Look(ref settingsVersion, "settingsVersion", 0);
			Scribe_Values.Look(ref saveTranscripts, "saveTranscripts", false);
			Scribe_Values.Look(ref collectVoiceDebug, "collectVoiceDebug", false);
			Scribe_Values.Look(ref useScriptRouter, "useScriptRouter", true);
			Scribe_Values.Look(ref agentInbox, "agentInbox", false);
			Scribe_Values.Look(ref agentTips, "agentTips", true);
			Scribe_Values.Look(ref fallbackOnCannot, "fallbackOnCannot", false);
			Scribe_Values.Look(ref recordHumanPlay, "recordHumanPlay", false);
			Scribe_Values.Look(ref pathfindCost, "pathfindCost", true);
			// Settings gate (user, 2026-10-26): per-category agent text gates, all default on.
			Scribe_Values.Look(ref remindBase, "remindBase", true);
			Scribe_Values.Look(ref remindFood, "remindFood", true);
			Scribe_Values.Look(ref remindPawn, "remindPawn", true);
			Scribe_Values.Look(ref remindResource, "remindResource", true);
			Scribe_Values.Look(ref remindUrgent, "remindUrgent", true);
			Scribe_Values.Look(ref buildPlacementChecks, "buildPlacementChecks", true);
			Scribe_Values.Look(ref buildAdviceNotes, "buildAdviceNotes", true);

			// Brief 11 section A.1 migration: existing installs saved voiceToggleEverywhere=false
			// (its old default) before this brief; settingsVersion is missing from their file too, so
			// it Looks in as 0. Same Scribe.mode == LoadSaveMode.PostLoadInit pattern already used by
			// GameComponent_VerbalCommands.ExposeData for its own post-load fixup.
			if (Scribe.mode == LoadSaveMode.PostLoadInit && settingsVersion < 1)
			{
				voiceToggleEverywhere = true;
				settingsVersion = 1;
			}
		}

		public string MaxTokensBuffer
		{
			get
			{
				if (maxTokensBuffer == null)
				{
					maxTokensBuffer = maxTokens.ToString();
				}
				return maxTokensBuffer;
			}
			set { maxTokensBuffer = value; }
		}

		public string TimeoutSecondsBuffer
		{
			get
			{
				if (timeoutSecondsBuffer == null)
				{
					timeoutSecondsBuffer = timeoutSeconds.ToString();
				}
				return timeoutSecondsBuffer;
			}
			set { timeoutSecondsBuffer = value; }
		}
	}

	public class VerbalCommandsMod : Mod
	{
		public static VerbalCommandsSettings Settings;

		// Settings gate (user, 2026-10-26): one property per category of text the playing agent gets. Each is true while
		// Settings is null (nothing loaded yet: behaviour as before) and otherwise follows the settings window's checkbox.
		// Know-how tips.
		public static bool TipsOn { get { return Settings == null || Settings.agentTips; } }
		// Base and building reminders (includes 6hr, 12hr and daily).
		public static bool BaseRemindersOn { get { return Settings == null || Settings.remindBase; } }
		// Food and Farming reminders.
		public static bool FoodRemindersOn { get { return Settings == null || Settings.remindFood; } }
		// Pawn and work reminders.
		public static bool PawnRemindersOn { get { return Settings == null || Settings.remindPawn; } }
		// Resource reminders.
		public static bool ResourceRemindersOn { get { return Settings == null || Settings.remindResource; } }
		// Urgent reminders.
		public static bool UrgentRemindersOn { get { return Settings == null || Settings.remindUrgent; } }
		// Building placement checks.
		public static bool PlacementChecksOn { get { return Settings == null || Settings.buildPlacementChecks; } }
		// Build advice tips/notes.
		public static bool BuildAdviceOn { get { return Settings == null || Settings.buildAdviceNotes; } }

		// Verse/ModContentPack.cs:66 `public string RootDir => rootDirInt.FullName;` - the mod's root
		// folder (contains About\, 1.6\, Defs\, and the Models\ this brief adds), NOT the
		// version-specific 1.6\ subfolder. SttEngine resolves the model and native-DLL directories
		// from this at runtime (brief 11 section B).
		public static string ModRootDir;

		// Scroll fix: the settings page has more rows than fit in the mod-options window, so it needs
		// a scroll view. scrollPosition must persist across frames (Widgets.BeginScrollView takes it
		// `ref` - sources/RimWorldDecompiled/Verse/Widgets.cs:2863). viewHeight remembers the previous
		// frame's Listing_Standard.CurHeight (Verse/Listing.cs:30, `public float CurHeight => curY;`)
		// so the view rect passed to BeginScrollView this frame is tall enough for the content just
		// measured last frame; seeded with a generous initial guess so nothing is clipped on frame 1
		// before a real measurement exists.
		private Vector2 scrollPosition = Vector2.zero;
		private float viewHeight = 750f;

		// Model-agnostic calls (2026-10-05): the main (router) model's field edits router_model.txt (Router.ReadRouterModel
		// reads it for every order). Loaded when the page is first drawn, written in WriteSettings (the window's PreClose,
		// RimWorld\Dialog_ModSettings.cs:28-32), then dropped so the next opening reads the file again.
		private string routerModelBuffer;
		private string routerModelLoaded;

		public VerbalCommandsMod(ModContentPack content)
			: base(content)
		{
			Settings = GetSettings<VerbalCommandsSettings>();
			ModRootDir = content.RootDir;
			PatchConstructionFail();
		}

		// Its own method, so a missing 0Harmony.dll fails here (caught) and not the constructor (ConstructionFailPatch.cs).
		private static void PatchConstructionFail()
		{
			try
			{
				ConstructionFailPatch.Apply();
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] The construction-fail patch did not load: " + ex.Message);
			}
			try
			{
				MerchantArrivalPatch.Apply();
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] The merchant-arrival patch did not load: " + ex.Message);
			}
			try
			{
				PawnHarmPatch.Apply();
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] The pawn-harm patch did not load: " + ex.Message);
			}
			try
			{
				ResearchBenchRule.Apply();
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] The research bench patch did not load: " + ex.Message);
			}
			try
			{
				MineYieldPatch.Apply();
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] The micro mining patch did not load: " + ex.Message);
			}
			try
			{
				RoofOnlyPatch.Apply();
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] The roof only patch did not load: " + ex.Message);
			}
		}

		// The main (router) model and the fallback model. What .env does, and every other setting's description, is in
		// the mod's README.md (user, 2026-10-05: "get rid of every single description like that on modsettings and shove
		// it onto readme").
		private void DrawModelRows(Listing_Standard listing)
		{
			if (routerModelBuffer == null)
			{
				try
				{
					string note;
					routerModelBuffer = Router.ReadRouterModel(Router.RouterModelFilePath(), out note);
				}
				catch (Exception)
				{
					routerModelBuffer = Router.DefaultRouterModel;
				}
				routerModelLoaded = routerModelBuffer;
			}

			routerModelBuffer = listing.TextEntryLabeled("VerbalCommands_Settings_RouterModel".Translate(), routerModelBuffer, 1);
			Settings.model = TextEntryWithTip(listing, "VerbalCommands_Settings_Model".Translate(), Settings.model,
				"VerbalCommands_Settings_ModelTip".Translate());
		}

		// Instant tooltips (user, 2026-10-05: "dont make any delay seconds in showing it"): TipSignal's default delay is
		// 0.45 s (Verse\TipSignal.cs:47) and TooltipHandler draws a tip only after it (Verse\TooltipHandler.cs:121);
		// delay 0 shows it at once, as Dialog_DevModeVoiceKeyNotice's Close button does (brief 09). The tooltip texts
		// are the README's, for the settings the user picked (2026-10-05).
		private static void InstantTip(Rect rect, string text)
		{
			TooltipHandler.TipRegion(rect, new TipSignal(text, 0f));
		}

		// Listing_Standard.CheckboxLabeled (Verse\Listing_Standard.cs:215-233) with an instant tooltip.
		private static void CheckboxWithTip(Listing_Standard listing, string label, ref bool checkOn, string tip)
		{
			Rect rect = listing.GetRect(Text.CalcHeight(label, listing.ColumnWidth));
			rect.width = Math.Min(rect.width + 24f, listing.ColumnWidth);
			if (Mouse.IsOver(rect))
			{
				Widgets.DrawHighlight(rect);
			}
			InstantTip(rect, tip);
			Widgets.CheckboxLabeled(rect, label, ref checkOn);
			listing.Gap(listing.verticalSpacing);
		}

		// Listing_Standard.TextEntryLabeled (Verse\Listing_Standard.cs:334-339) with an instant tooltip.
		private static string TextEntryWithTip(Listing_Standard listing, string label, string text, string tip)
		{
			Rect rect = listing.GetRect(Text.LineHeight);
			string result = Widgets.TextEntryLabeled(rect, label, text);
			InstantTip(rect, tip);
			listing.Gap(listing.verticalSpacing);
			return result;
		}

		public override void WriteSettings()
		{
			base.WriteSettings();
			if (routerModelBuffer != null)
			{
				string typed = routerModelBuffer.Trim();
				if (typed != (routerModelLoaded ?? "").Trim())
				{
					try
					{
						string path = Router.RouterModelFilePath();
						Directory.CreateDirectory(Path.GetDirectoryName(path));
						File.WriteAllText(path, (typed.Length > 0 ? typed : Router.DefaultRouterModel) + "\n", new UTF8Encoding(false));
					}
					catch (Exception ex)
					{
						Verse.Log.Warning("[VerbalCommands] router_model.txt could not be written: " + ex.Message);
					}
				}
			}
			routerModelBuffer = null;
			routerModelLoaded = null;
		}

		public override string SettingsCategory()
		{
			return "Verbal Commands";
		}

		public override void DoSettingsWindowContents(Rect inRect)
		{
			// Scroll fix: rows past "Save sent orders to transcripts.jsonl" were unreachable because
			// the listing was drawn straight into inRect with no scroll view. Standard RimWorld
			// pattern (e.g. Verse/Dialog_ModSettings.cs, Verse/Listing_Standard.cs:445 BeginSection):
			// a Rect 16f narrower than inRect (room for the vertical scrollbar - Widgets.cs:2889
			// `viewRect.width -= 20f;` inside AdjustRectsForScrollView is the analogous vanilla
			// adjustment) sized to the content height, scrolled via Widgets.BeginScrollView /
			// EndScrollView (Verse/Widgets.cs:2863, 2896) with a Vector2 position that must persist
			// across frames, hence the `ref scrollPosition` field above.
			Rect viewRect = new Rect(0f, 0f, inRect.width - 16f, viewHeight);
			Widgets.BeginScrollView(inRect, ref scrollPosition, viewRect);

			Listing_Standard listing = new Listing_Standard();
			// Column-collapse fix: GetRect -> NewColumnIfNeeded (Verse/Listing.cs:54-60) calls
			// NewColumn() (47-52, resets curY to 0 and shifts curX right by a column) whenever
			// curY + neededHeight > listingRect.height. Because viewHeight below is fed back in
			// from last frame's CurHeight (curY, Listing.cs:30), any frame that adds a row (e.g.
			// ticking "Use Claude Code CLI") pushes content past the current viewHeight, rows spill
			// into a second column off-screen inside the scroll view, CurHeight shrinks to that
			// column's height, and it collapses further every subsequent frame. maxOneColumn forces
			// NewColumnIfNeeded to never call NewColumn(), so content always grows straight down.
			listing.maxOneColumn = true;
			listing.Begin(viewRect);

			listing.Label("VerbalCommands_Settings_ApiKey".Translate());
			// Brief 14: never show the key's characters, in any state (no "show" toggle, no
			// last-4 suffix) - a settings screenshot leaked the full key in plaintext 2026-09-26.
			listing.Label(Settings.apiKey.NullOrEmpty()
				? "VerbalCommands_Settings_ApiKeyNotSet".Translate()
				: "VerbalCommands_Settings_ApiKeySet".Translate());
			// Brief 14 Revision 2 ("that box shouldnt even be there"): no entry field of any kind
			// for the key (the prior GUI.PasswordField row is removed). The row is now a status
			// label above (drawn already) plus two buttons: paste from the OS clipboard, and clear.
			// Rect layout: GetRect(Text.LineHeight) is Verse/Listing.cs:62; RightHalf/LeftHalf are
			// the same GenUI split this file already uses elsewhere for two-widget rows.
			Rect apiKeyRowRect = listing.GetRect(Text.LineHeight);
			Rect apiKeyPasteRect = apiKeyRowRect.LeftHalf();
			Rect apiKeyClearRect = apiKeyRowRect.RightHalf();
			// GUIUtility.systemCopyBuffer: `public static extern string systemCopyBuffer { get;
			// set; }` (get FreeFunction "GetCopyBuffer", set FreeFunction "SetCopyBuffer") -
			// verified via ilspycmd against RimWorld's shipped
			// RimWorldWin64_Data\Managed\UnityEngine.IMGUIModule.dll, decompiled type
			// UnityEngine.GUIUtility, systemCopyBuffer property.
			if (Widgets.ButtonText(apiKeyPasteRect, "VerbalCommands_Settings_ApiKeyPaste".Translate()))
			{
				string clipboard = GUIUtility.systemCopyBuffer;
				string trimmed = clipboard?.Trim();
				if (trimmed.NullOrEmpty())
				{
					// Messages.Message(string, MessageTypeDef, bool) - sources/RimWorldDecompiled/
					// Verse/Messages.cs:47. MessageTypeDefOf.RejectInput -
					// sources/RimWorldDecompiled/RimWorld/MessageTypeDefOf.cs:26.
					Messages.Message("VerbalCommands_Settings_ApiKeyClipboardEmpty".Translate(), MessageTypeDefOf.RejectInput, false);
				}
				else
				{
					Settings.apiKey = trimmed;
				}
			}
			if (Widgets.ButtonText(apiKeyClearRect, "VerbalCommands_Settings_ApiKeyClear".Translate()))
			{
				Settings.apiKey = "";
			}
			listing.Gap(listing.verticalSpacing);

			listing.Gap();
			if (OrderController.AnyOrderInFlight)
			{
				// Brief 15b fix 14 (nit 8): greyed out while an order is in flight. Same row layout as
				// Listing_Standard.CheckboxLabeled (Verse\Listing_Standard.cs:215-233), but through
				// Widgets.CheckboxLabeled(..., disabled: true) (Verse\Widgets.cs:1215-1231), which draws the
				// box without the toggle.
				string headlessLabel = "VerbalCommands_Settings_Headless".Translate();
				Rect headlessRect = listing.GetRect(Text.CalcHeight(headlessLabel, listing.ColumnWidth));
				headlessRect.width = Mathf.Min(headlessRect.width + 24f, listing.ColumnWidth);
				GUI.color = Color.grey;
				Widgets.CheckboxLabeled(headlessRect, headlessLabel, ref Settings.useHeadless, true);
				GUI.color = Color.white;
				TooltipHandler.TipRegion(headlessRect, "VerbalCommands_Settings_HeadlessLocked".Translate());
				listing.Gap(listing.verticalSpacing);
			}
			else
			{
				listing.CheckboxLabeled("VerbalCommands_Settings_Headless".Translate(), ref Settings.useHeadless);
			}
			if (Settings.useHeadless)
			{
				Settings.claudeExePath = TextEntryWithTip(listing, "VerbalCommands_Settings_ClaudePath".Translate(), Settings.claudeExePath,
					"VerbalCommands_Settings_ClaudePathTip".Translate());
			}

			listing.Gap();
			DrawModelRows(listing);
			Settings.effort = listing.TextEntryLabeled("VerbalCommands_Settings_Effort".Translate(), Settings.effort, 1);

			listing.Gap();
			Rect maxTokensRect = listing.GetRect(30f);
			Rect maxTokensLabelRect = maxTokensRect.LeftHalf();
			Rect maxTokensFieldRect = maxTokensRect.RightHalf();
			Widgets.Label(maxTokensLabelRect, "VerbalCommands_Settings_MaxTokens".Translate());
			string maxTokensBuffer = Settings.MaxTokensBuffer;
			Widgets.TextFieldNumeric(maxTokensFieldRect, ref Settings.maxTokens, ref maxTokensBuffer, 1f, 1000000f);
			Settings.MaxTokensBuffer = maxTokensBuffer;

			Rect timeoutRect = listing.GetRect(30f);
			Rect timeoutLabelRect = timeoutRect.LeftHalf();
			Rect timeoutFieldRect = timeoutRect.RightHalf();
			Widgets.Label(timeoutLabelRect, "VerbalCommands_Settings_Timeout".Translate());
			string timeoutBuffer = Settings.TimeoutSecondsBuffer;
			Widgets.TextFieldNumeric(timeoutFieldRect, ref Settings.timeoutSeconds, ref timeoutBuffer, 1f, 600f);
			Settings.TimeoutSecondsBuffer = timeoutBuffer;

			listing.Gap();
			listing.CheckboxLabeled("VerbalCommands_Settings_ScriptRouter".Translate(), ref Settings.useScriptRouter);
			listing.CheckboxLabeled("VerbalCommands_Settings_Confirm".Translate(), ref Settings.confirmBeforeApply);
			listing.CheckboxLabeled("VerbalCommands_Settings_ExplainErrors".Translate(), ref Settings.explainErrorsWithLlm);
			listing.CheckboxLabeled("VerbalCommands_Settings_LogAnswers".Translate(), ref Settings.logRouterAnswers);
			listing.CheckboxLabeled("VerbalCommands_Settings_LogHaiku".Translate(), ref Settings.logHaikuReplies);
			listing.CheckboxLabeled("VerbalCommands_Settings_LogPlans".Translate(), ref Settings.logRouterPlans);
			CheckboxWithTip(listing, "VerbalCommands_Settings_SaveRouterLog".Translate(), ref Settings.saveRouterLog,
				"VerbalCommands_Settings_SaveRouterLogTip".Translate());
			listing.CheckboxLabeled("VerbalCommands_Settings_AgentInbox".Translate(), ref Settings.agentInbox);
			// Settings gate (user, 2026-10-26): the Know-how tips row moved to the last section below.
			listing.CheckboxLabeled("VerbalCommands_Settings_PathfindCost".Translate(), ref Settings.pathfindCost);
			listing.CheckboxLabeled("VerbalCommands_Settings_RecordHumanPlay".Translate(), ref Settings.recordHumanPlay);

			listing.Gap();
			listing.Label("VerbalCommands_Settings_Extra".Translate());
			Settings.systemPromptExtra = listing.TextEntry(Settings.systemPromptExtra, 4);

			listing.Gap();
			listing.Label("VerbalCommands_Settings_GroupLayout".Translate() + ": " + GroupHotkeyLayout.CurrentLayout());
			if (listing.ButtonText("VerbalCommands_Settings_ChooseGroupKeys".Translate()))
			{
				Settings.groupKeyLayout = GroupKeyLayout.Unset.ToString();
				GroupHotkeyLayout.PromptIfUnset();
			}
			if (listing.ButtonText("VerbalCommands_Settings_RestoreVanillaKeys".Translate()))
			{
				GroupHotkeyLayout.RestoreVanillaTabKeys();
			}

			listing.Gap();
			listing.CheckboxLabeled("VerbalCommands_Settings_VoiceEnabled".Translate(), ref Settings.voiceEnabled);
			listing.CheckboxLabeled("VerbalCommands_Settings_VoiceToggleEverywhere".Translate(), ref Settings.voiceToggleEverywhere);
			CheckboxWithTip(listing, "VerbalCommands_Settings_SaveTranscripts".Translate(), ref Settings.saveTranscripts,
				"VerbalCommands_Settings_SaveTranscriptsTip".Translate());
			// Brief 13 Phase B1 item 1 (user rule 1): opt-in debug-data collection, default off. Label
			// and description text are the brief's exact wording ("User-decided behaviour" section).
			// CHANGED 2026-10-05: the description row moved to README.md, word for word (user: "get rid of every single
			// description like that on modsettings and shove it onto readme").
			listing.CheckboxLabeled("VerbalCommands_Settings_CollectVoiceDebug".Translate(), ref Settings.collectVoiceDebug);

			// Brief 08: one row per def, each a label showing the currently bound key plus a button
			// that opens the same rebind popup the mic icon uses. Listing_Standard.ButtonTextLabeled
			// draws a left-hand label and a right-hand button in one row (Verse/Listing_Standard.cs:264-267).
			if (listing.ButtonTextLabeled(
				"VerbalCommands_Settings_VoiceToggleKey".Translate() + ": " + VerbalCommandsDefs.VoiceToggleKey.MainKeyLabel,
				"VerbalCommands_Settings_ChangeKey".Translate()))
			{
				Find.WindowStack.Add(new Dialog_DefineVoiceToggleBinding(VerbalCommandsDefs.VoiceToggleKey));
			}
			if (listing.ButtonTextLabeled(
				"VerbalCommands_Settings_VoiceToggleKeyDev".Translate() + ": " + VerbalCommandsDefs.VoiceToggleDevKey.MainKeyLabel,
				"VerbalCommands_Settings_ChangeKey".Translate()))
			{
				Find.WindowStack.Add(new Dialog_DefineVoiceToggleBinding(VerbalCommandsDefs.VoiceToggleDevKey));
			}

			// Bound to the inverse of the suppression setting, per brief 08, so the player can turn the
			// one-time notice back on from here without needing to trigger the conflict again.
			bool remindDevModeNotice = !Settings.devModeVoiceNoticeSuppressed;
			listing.CheckboxLabeled("VerbalCommands_Settings_RemindDevModeNotice".Translate(), ref remindDevModeNotice);
			Settings.devModeVoiceNoticeSuppressed = !remindDevModeNotice;

			// Settings gate (user, 2026-10-26): per-category on/off for the text the playing agent gets; no tooltips, no extra rows.
			listing.Gap();
			listing.Label("VerbalCommands_Settings_DevSection".Translate());
			listing.CheckboxLabeled("VerbalCommands_Settings_AgentTips".Translate(), ref Settings.agentTips);
			listing.CheckboxLabeled("VerbalCommands_Settings_RemindBase".Translate(), ref Settings.remindBase);
			listing.CheckboxLabeled("VerbalCommands_Settings_RemindFood".Translate(), ref Settings.remindFood);
			listing.CheckboxLabeled("VerbalCommands_Settings_RemindPawn".Translate(), ref Settings.remindPawn);
			listing.CheckboxLabeled("VerbalCommands_Settings_RemindResource".Translate(), ref Settings.remindResource);
			listing.CheckboxLabeled("VerbalCommands_Settings_RemindUrgent".Translate(), ref Settings.remindUrgent);
			listing.CheckboxLabeled("VerbalCommands_Settings_BuildChecks".Translate(), ref Settings.buildPlacementChecks);
			listing.CheckboxLabeled("VerbalCommands_Settings_BuildAdvice".Translate(), ref Settings.buildAdviceNotes);

			listing.End();
			// Remember this frame's real content height (Listing.CurHeight, Verse/Listing.cs:30) so
			// next frame's viewRect is sized to fit it exactly - never clipping the last row, never
			// leaving a giant empty scroll area either.
			viewHeight = listing.CurHeight;
			Widgets.EndScrollView();
		}
	}
}
