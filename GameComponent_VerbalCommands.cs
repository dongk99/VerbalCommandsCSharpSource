using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace VerbalCommands
{
	public class GameComponent_VerbalCommands : GameComponent
	{
		private static GameComponent_VerbalCommands instance;

		// Fallback accessor: prefer the instance captured at construction, else look it up on
		// Current.Game the way any other GameComponent consumer would (Verse/Game.cs:475-481
		// instantiates one GameComponent per registered type per Game).
		public static GameComponent_VerbalCommands Instance
		{
			get
			{
				if (instance != null)
				{
					return instance;
				}
				if (Current.Game != null)
				{
					instance = Current.Game.GetComponent<GameComponent_VerbalCommands>();
				}
				return instance;
			}
		}

		private Dictionary<int, GroupRule> groups = new Dictionary<int, GroupRule>();

		// Working lists required by the dictionary Scribe_Collections.Look overload (Verse\Scribe_Collections.cs:433).
		private List<int> groupKeysWorking;
		private List<GroupRule> groupValuesWorking;

		public GameComponent_VerbalCommands(Game game)
		{
			instance = this;
		}

		public override void ExposeData()
		{
			base.ExposeData();
			Scribe_Collections.Look(ref groups, "vcGroups", LookMode.Value, LookMode.Deep, ref groupKeysWorking, ref groupValuesWorking);
			if (Scribe.mode == LoadSaveMode.PostLoadInit && groups == null)
			{
				groups = new Dictionary<int, GroupRule>();
			}
			// Brief 21. Saves made before it have neither node; both lists then load as null.
			Scribe_Collections.Look(ref modPlaced, "vcModPlaced", LookMode.Value);
			Scribe_Collections.Look(ref pendingOptions, "vcPendingOptions", LookMode.Deep);
			if (Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				if (modPlaced == null)
				{
					modPlaced = new List<string>();
				}
				if (pendingOptions == null)
				{
					pendingOptions = new List<PendingOption>();
				}
				pendingOptions.RemoveAll((PendingOption o) => o == null);
				modPlacedSet = null;
			}
			// The door path record (AgentDoorPathScan.cs; user: "file doesnt flush/reset, and remains with savefile").
			// Saves made before it have no node; it then starts empty.
			Scribe_Deep.Look(ref doorPath, "vcDoorPath");
			if (Scribe.mode == LoadSaveMode.PostLoadInit && doorPath == null)
			{
				doorPath = new DoorPathData();
			}
			// Micro orders (AgentMicro.cs): the running mining / hauling orders and their leftover places. Saves made
			// before it have no node; it then starts empty.
			Scribe_Deep.Look(ref micro, "vcMicro");
			if (Scribe.mode == LoadSaveMode.PostLoadInit && micro == null)
			{
				micro = new MicroData();
			}
			// Roof only orders (AgentRoofOnly.cs). Saves made before it have no node; it then starts empty.
			Scribe_Deep.Look(ref roofOnly, "vcRoofOnly");
			if (Scribe.mode == LoadSaveMode.PostLoadInit && roofOnly == null)
			{
				roofOnly = new RoofOnlyData();
			}
			// Harness interventions (HarnessLog.cs). Saves made before it have no node; it then starts empty, and the colony id
			// is made at the first load (HarnessLog.OnLoaded).
			Scribe_Values.Look(ref colonyId, "vcColonyId");
			Scribe_Collections.Look(ref harnessEntries, "vcHarnessLog", LookMode.Deep);
			if (Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				if (harnessEntries == null)
				{
					harnessEntries = new List<HarnessEntry>();
				}
				harnessEntries.RemoveAll((HarnessEntry e) => e == null);
			}
		}

		private List<HarnessEntry> harnessEntries = new List<HarnessEntry>();		private string colonyId;

		public string ColonyId
		{
			get { return colonyId; }
			set { colonyId = value; }
		}

		public List<HarnessEntry> HarnessEntries
		{
			get
			{
				if (harnessEntries == null)
				{
					harnessEntries = new List<HarnessEntry>();
				}
				return harnessEntries;
			}
		}

		private RoofOnlyData roofOnly = new RoofOnlyData();

		public RoofOnlyData RoofOnly
		{
			get
			{
				if (roofOnly == null)
				{
					roofOnly = new RoofOnlyData();
				}
				return roofOnly;
			}
		}

		private MicroData micro = new MicroData();

		public MicroData Micro
		{
			get
			{
				if (micro == null)
				{
					micro = new MicroData();
				}
				return micro;
			}
		}

		private DoorPathData doorPath = new DoorPathData();

		public DoorPathData DoorPath
		{
			get
			{
				if (doorPath == null)
				{
					doorPath = new DoorPathData();
				}
				return doorPath;
			}
		}

		// ---- Brief 21: what the mod itself placed, and the drawn layout options ----------------------

		// "mapId|x,z|defName" for every blueprint the mod placed. LayoutMemory leaves these out when it
		// learns the player's habits, so the mod never learns from its own choices. A blueprint becomes a
		// frame and then a building, each a new Thing with a new id (RimWorld\Blueprint_Storage.cs:92-100,
		// RimWorld\Frame.cs:329), so the tile and the def are recorded, not the id.
		private List<string> modPlaced = new List<string>();
		private HashSet<string> modPlacedSet;

		private List<PendingOption> pendingOptions = new List<PendingOption>();

		private static string ModPlacedKey(Map map, IntVec3 cell, ThingDef def)
		{
			return map.uniqueID + "|" + cell.x + "," + cell.z + "|" + def.defName;
		}

		private HashSet<string> ModPlacedSet
		{
			get
			{
				if (modPlacedSet == null)
				{
					modPlacedSet = new HashSet<string>(modPlaced);
				}
				return modPlacedSet;
			}
		}

		public void RecordModPlaced(Map map, IntVec3 cell, ThingDef def)
		{
			string key = ModPlacedKey(map, cell, def);
			if (ModPlacedSet.Add(key))
			{
				modPlaced.Add(key);
			}
		}

		public bool IsModPlaced(Map map, IntVec3 cell, ThingDef def)
		{
			return modPlaced.Count > 0 && ModPlacedSet.Contains(ModPlacedKey(map, cell, def));
		}

		// Drops records of this map whose blueprint, frame or building is gone (cancelled, torn down), so
		// a later building the player puts on that tile is learned from again.
		public void ForgetGoneModPlaced(Map map)
		{
			if (modPlaced.Count == 0)
			{
				return;
			}
			string prefix = map.uniqueID + "|";
			int removed = modPlaced.RemoveAll(delegate (string key)
			{
				if (!key.StartsWith(prefix, System.StringComparison.Ordinal))
				{
					return false;
				}
				string[] parts = key.Split('|');
				string[] xz = parts.Length == 3 ? parts[1].Split(',') : null;
				int x;
				int z;
				if (xz == null || xz.Length != 2 || !int.TryParse(xz[0], out x) || !int.TryParse(xz[1], out z))
				{
					return true;
				}
				IntVec3 cell = new IntVec3(x, 0, z);
				if (!cell.InBounds(map))
				{
					return true;
				}
				List<Thing> things = cell.GetThingList(map);
				for (int i = 0; i < things.Count; i++)
				{
					// Blueprints and frames name what they will become in ThingDef.entityDefToBuild.
					ThingDef built = (things[i].def.entityDefToBuild as ThingDef) ?? things[i].def;
					if (built.defName == parts[2] && things[i].Position == cell)
					{
						return false;
					}
				}
				return true;
			});
			if (removed > 0)
			{
				modPlacedSet = null;
			}
		}

		public List<PendingOption> PendingOptionsFor(Map map)
		{
			List<PendingOption> result = new List<PendingOption>();
			foreach (PendingOption o in pendingOptions)
			{
				if (map != null && o.mapId == map.uniqueID)
				{
					result.Add(o);
				}
			}
			return result;
		}

		// Removes the plans of this map's options and forgets the options.
		public void ClearPendingOptions(Map map)
		{
			List<PendingOption> mine = PendingOptionsFor(map);
			PlanOptions.RemovePlans(mine);
			pendingOptions.RemoveAll((PendingOption o) => mine.Contains(o));
		}

		public void AddPendingOption(PendingOption option)
		{
			pendingOptions.Add(option);
		}

		public void SetGroup(int slot, GroupRule rule)
		{
			groups[slot] = rule;
		}

		public void ClearGroup(int slot)
		{
			groups.Remove(slot);
		}

		public GroupRule GetGroup(int slot)
		{
			GroupRule rule;
			return groups.TryGetValue(slot, out rule) ? rule : null;
		}

		public IEnumerable<KeyValuePair<int, GroupRule>> AllGroups
		{
			get { return groups; }
		}

		// One-time dev-mode voice-key notice (brief 08). StartedNewGame() and LoadedGame() each run
		// exactly once per game entry (Verse/Game.cs:552 inside InitNewGame, :646 inside LoadGame;
		// GameComponent_VerbalCommands is constructed fresh per Game - Verse/Game.cs GetComponent
		// pattern, same as the class-level Instance comment above), so no extra "already shown" guard
		// is needed for "at most once per game entry": a fresh instance's showed-flag always starts
		// false. Both call sites run from inside the new-game/load-game long event, before the
		// loading screen has necessarily finished, so the dialog itself must not be added directly
		// here - see MaybeShowDevModeVoiceKeyNotice.
		private bool devModeVoiceNoticeConsidered;

		public override void StartedNewGame()
		{
			base.StartedNewGame();
			HarnessLog.OnNewGame();   // a new colony id (HarnessLog.cs)
			MaybeShowDevModeVoiceKeyNotice();
		}

		public override void LoadedGame()
		{
			base.LoadedGame();
			HarnessLog.OnLoaded();   // the history: this colony's file, the .rws, a one-time import (HarnessLog.cs)			MaybeShowDevModeVoiceKeyNotice();
		}

		// Dialog timing (verified): Verse/Game.cs:541-548 (InitNewGame) and :638-645 (LoadGame) already
		// wrap their own Prefs.PauseOnLoad follow-up work in LongEventHandler.ExecuteWhenFinished right
		// next to the StartedNewGame()/LoadedGame() calls we hook - that is vanilla's own answer to
		// "is the WindowStack usable yet here", and it is "not yet, defer it". RimWorld/Page_CreateWorldParams.cs:204-208
		// confirms ExecuteWhenFinished is also the mechanism vanilla itself uses to open a Window once
		// deferred: `LongEventHandler.ExecuteWhenFinished(delegate { ... Find.WindowStack.Add(next); });`.
		// Verse/LongEventHandler.cs:298-305 ExecuteWhenFinished runs the action immediately only if there
		// is no current event or it has already been displayed; otherwise it queues until the current
		// long event (map generation / save loading, in our case) finishes - exactly what we want, so the
		// notice never appears under the loading-screen overlay and never gets torn down by scene/window
		// teardown that happens while that event is still running.
		private void MaybeShowDevModeVoiceKeyNotice()
		{
			if (devModeVoiceNoticeConsidered)
			{
				return;
			}
			devModeVoiceNoticeConsidered = true;

			if (!Prefs.DevMode)
			{
				return;
			}
			if (VerbalCommandsMod.Settings.devModeVoiceNoticeSuppressed)
			{
				return;
			}
			// No conflict (player already rebound one of the two keys away from BackQuote/whatever
			// Dev_ToggleDebugLog is bound to) -> nothing to warn about. KeyBindingDef.MainKey resolves
			// slot A falling back to slot B (Verse/KeyBindingDef.cs:22-39).
			if (VerbalCommandsDefs.VoiceToggleKey.MainKey != KeyBindingDefOf.Dev_ToggleDebugLog.MainKey)
			{
				return;
			}

			LongEventHandler.ExecuteWhenFinished(delegate
			{
				Find.WindowStack.Add(new Dialog_DevModeVoiceKeyNotice());
			});
		}

		public override void GameComponentUpdate()
		{
			try
			{
				MainThread.Drain();
			}
			catch (System.Exception ex)
			{
				Log.Error("[VerbalCommands] Exception in GameComponentUpdate: " + ex);
			}
			// Agent inbox (agent_player brief 01): polls the inbox folder; does nothing unless the setting is on, and catches its own errors.
			AgentInbox.Update();
			// Ends a run when the game's trade window opens (AgentInteract.cs); catches its own errors.
			AgentInteract.Update();
			// Micro orders: the 0.3 s step and the 3 s check (AgentMicro.cs); catches its own errors.
			AgentMicro.Update();
			// Roof only orders: the 3 s check (AgentRoofOnly.cs); catches its own errors.
			AgentRoofOnly.Update();
		}

		// Agent 02: game clock, run-then-pause, event lines and order follow-up (AgentTime.cs). One call;
		// AgentTime.Tick catches its own exceptions. The game calls this once per tick:
		// TickManager.DoSingleTick -> GameComponentUtility.GameComponentTick (Verse/TickManager.cs:460,
		// Verse/GameComponentUtility.cs:24-38).
		public override void GameComponentTick()
		{
			AgentTime.Tick();
		}

		public override void GameComponentOnGUI()
		{
			try
			{
				if (Current.ProgramState != ProgramState.Playing)
				{
					return;
				}

				// Rebind-popup guard (brief 07): GameComponentOnGUI (Verse/UIRoot.cs:66, called from the
				// base UIRootOnGUI()) runs before the window stack processes events
				// (RimWorld/UIRoot_Play.cs:43, windows.WindowStackOnGUI(), reached later in the override).
				// So while Dialog_DefineBinding (or our Dialog_DefineVoiceToggleBinding subclass) is open
				// waiting for the player's next keypress, our own key handlers below would run first and
				// could steal that key. IsOpen<T> uses `windows[i] is WindowType` (Verse/WindowStack.cs:295-303),
				// which matches subclasses via `is`, so this one check covers both vanilla's own
				// Dialog_DefineBinding (opened from RimWorld.Dialog_KeyBindings) and our subclass.
				if (Find.WindowStack.IsOpen<Dialog_DefineBinding>())
				{
					return;
				}

				// Brief 08: which def is "active" is re-evaluated every call via Prefs.DevMode
				// (VerbalCommandsDefs.ActiveVoiceToggleKey), never cached, so a mid-session dev-mode
				// toggle switches keys immediately. KeyBindingDef.KeyDownEvent only matches this
				// specific def's own bound key (Verse/KeyBindingDef.cs:43-65), so checking only the
				// active def already means the inactive def's key is never inspected here, let alone
				// consumed - the "don't consume the inactive key" requirement holds by construction.
				KeyBindingDef activeVoiceKey = VerbalCommandsDefs.ActiveVoiceToggleKey;
				if (activeVoiceKey != null && activeVoiceKey.KeyDownEvent)
				{
					if (Find.WindowStack.IsOpen<Dialog_VerbalCommand>())
					{
						VerbalCommandsMod.Settings.voiceEnabled = !VerbalCommandsMod.Settings.voiceEnabled;
						VerbalCommandsMod.Settings.Write();
						Dialog_VerbalCommand openDialog = Find.WindowStack.WindowOfType<Dialog_VerbalCommand>();
						openDialog?.SetVoiceEnabled(VerbalCommandsMod.Settings.voiceEnabled);
						Event.current.Use();
						return;
					}
					if (VerbalCommandsMod.Settings.voiceToggleEverywhere && !IsBlockedByModalDialog())
					{
						VerbalCommandsMod.Settings.voiceEnabled = true;
						VerbalCommandsMod.Settings.Write();
						Find.WindowStack.Add(new Dialog_VerbalCommand());
						Event.current.Use();
						return;
					}
					// voiceToggleEverywhere is off (or another modal blocks us): do NOT consume the key.
					// With dev mode off, vanilla binds BackQuote to Dev_ToggleDebugLog (KeyBindings.xml:395-399);
					// that handler runs earlier in the frame regardless (Verse/DebugWindowsOpener.DevToolStarterOnGUI()
					// is called from UIRoot.UIRootOnGUI():50, before GameComponentUtility.GameComponentOnGUI():66),
					// so leaving the event unused here only matters when dev mode is off for VoiceToggleKey, or
					// always for VoiceToggleDevKey (nothing of vanilla's binds Backslash by default) - in
					// either case any other mod/vanilla consumer of the key still sees an unconsumed event
					// this frame.
				}

				if (VerbalCommandsDefs.OpenKey.KeyDownEvent)
				{
					if (Find.WindowStack.IsOpen<Dialog_VerbalCommand>())
					{
						return;
					}

					if (IsBlockedByModalDialog())
					{
						return;
					}

					Find.WindowStack.Add(new Dialog_VerbalCommand());
					Event.current.Use();
					return;
				}

				// Group hotkeys (Alt = draft). This runs before the window stack (Verse/UIRoot.cs:66 inside
				// base.UIRootOnGUI, RimWorld/UIRoot_Play.cs:26; windows draw at :43), so Use()ing a key here
				// means no text field ever receives it. We therefore only need to hold back keys that would
				// otherwise have been typed as characters.
				if (Find.WindowStack.IsOpen<Dialog_VerbalCommand>())
				{
					return;
				}
				if (Find.WindowStack.AnySearchWidgetFocused)
				{
					return;
				}

				// Derived state, recomputed every call - unlike GUIUtility.keyboardControl it cannot latch on
				// and disable group keys for the rest of the session (Verse/WindowStack.cs:110).
				bool textEntryPossible = Find.WindowStack.NonImmediateDialogWindowOpen;

				KeyBindingDef[] groupKeys = VerbalCommandsDefs.GroupKeys;
				for (int slot = 1; slot <= 9; slot++)
				{
					KeyBindingDef keyDef = groupKeys[slot];
					if (keyDef == null || !keyDef.KeyDownEvent)
					{
						continue;
					}
					// F-keys and other non-printing binds are safe even with a dialog open; number-row binds
					// (the NumberRow layout, or anything the player picked under Manual) are not.
					if (textEntryPossible && IsTextInputKey(keyDef.MainKey))
					{
						continue;
					}
					ActivateGroup(slot, Event.current.alt);
					Event.current.Use();
					return;
				}
			}
			catch (System.Exception ex)
			{
				Log.Error("[VerbalCommands] Exception in GameComponentOnGUI: " + ex);
			}
		}

		// Same check the OpenKey path already used before brief 07: true if a non-MainTabWindow,
		// closeOnAccept window is on the stack (e.g. a confirmation dialog), meaning we should not pop
		// another window open on top of it. Shared by the Return-key open path and the voice-toggle
		// "everywhere" open path.
		private static bool IsBlockedByModalDialog()
		{
			foreach (Window w in Find.WindowStack.Windows)
			{
				if (!(w is MainTabWindow) && w.closeOnAccept)
				{
					return true;
				}
			}
			return false;
		}

		// True for keys that produce a character in a text field. UnityEngine.KeyCode is ordered so
		// that Space(32)..Tilde(126) covers printable ASCII (Alpha0-9, A-Z and punctuation, including
		// LeftCurlyBracket/Pipe/RightCurlyBracket/Tilde at 123-126, just past Z=122), and
		// Keypad0(256)..KeypadEquals(272) covers the numeric keypad; KeypadEnter is not a character.
		// Delete(127) is deliberately excluded - it edits a text field but inserts no character.
		// Everything else - function keys (F1=282+), arrows, Insert/Home/End/PageUp/PageDown,
		// modifiers - is safe to consume while a text field has focus.
		private static bool IsTextInputKey(KeyCode key)
		{
			int v = (int)key;
			if (v >= (int)KeyCode.Space && v <= (int)KeyCode.Tilde)
			{
				return true;
			}
			if (v >= (int)KeyCode.Keypad0 && v <= (int)KeyCode.KeypadEquals && key != KeyCode.KeypadEnter)
			{
				return true;
			}
			return false;
		}

		// Selects (and, with draft, drafts) every free colonist matching slot's rule right now.
		// No LLM call here: the rule was already resolved when it was defined (Executor.ResolveDefineGroup).
		private void ActivateGroup(int slot, bool draft)
		{
			GroupRule rule = GetGroup(slot);
			if (rule == null)
			{
				Messages.Message("VerbalCommands_GroupEmptySlot".Translate(slot), MessageTypeDefOf.RejectInput, false);
				return;
			}

			Map map = Find.CurrentMap;
			if (map == null)
			{
				return;
			}

			List<Pawn> members = new List<Pawn>();
			foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
			{
				if (rule.Matches(p))
				{
					members.Add(p);
				}
			}

			Find.Selector.ClearSelection();
			foreach (Pawn p in members)
			{
				Find.Selector.Select(p, false);
			}

			int draftedCount = 0;
			if (draft)
			{
				foreach (Pawn p in members)
				{
					if (p.drafter != null && !p.Downed && !p.drafter.Drafted)
					{
						p.drafter.Drafted = true;
						draftedCount++;
					}
				}
			}

			string msg = rule.name + ": " + members.Count + " selected";
			if (draft)
			{
				msg += ", " + draftedCount + " drafted";
			}
			Messages.Message(msg, MessageTypeDefOf.SilentInput, false);
		}
	}
}
