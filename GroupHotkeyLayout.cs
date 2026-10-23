using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace VerbalCommands
{
	public enum GroupKeyLayout
	{
		Unset,
		FKeys,
		NumberRow,
		Manual
	}

	// Owns the player-consent flow for binding group hotkeys and the rebinding itself. Never
	// touches a key without the player choosing to, in PromptIfUnset's Dialog_MessageBox chain or
	// the explicit settings buttons (VerbalCommandsMod.cs). Group defs default to KeyCode.None in
	// XML (mod\Defs\KeyBindingDefs\VerbalCommands_KeyBindings.xml) precisely so nothing is bound
	// until one of these paths runs.
	public static class GroupHotkeyLayout
	{
		// Generated KeyBindingDef names for the vanilla main tabs ("MainTab_" + MainButtonDef.defName,
		// Verse/KeyBindingDefGenerator.cs:29-42), in F1..F9 order per Data/Core/Defs/Misc/MainButtonDefs/MainButtons.xml.
		// Kept as full literals (not built with string concatenation) so DefDatabase lookups are
		// simple compile-time constants.
		private static readonly string[] MainTabKeyBindingDefNames =
		{
			"MainTab_Work", "MainTab_Schedule", "MainTab_Assign", "MainTab_Animals", "MainTab_Wildlife",
			"MainTab_Research", "MainTab_Quests", "MainTab_World", "MainTab_History"
		};

		private static readonly KeyCode[] FKeyCodes =
		{
			KeyCode.F1, KeyCode.F2, KeyCode.F3, KeyCode.F4, KeyCode.F5, KeyCode.F6, KeyCode.F7, KeyCode.F8, KeyCode.F9
		};

		// Group1..5 -> 5,6,7,8,9; Group6 -> 0; Group7..9 stay unbound (only 6 slots in this layout).
		private static readonly KeyCode[] NumberRowCodes =
		{
			KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9, KeyCode.Alpha0
		};

		public static GroupKeyLayout CurrentLayout()
		{
			VerbalCommandsSettings settings = VerbalCommandsMod.Settings;
			string raw = settings != null ? settings.groupKeyLayout : null;
			GroupKeyLayout layout;
			if (string.IsNullOrEmpty(raw) || !Enum.TryParse(raw, out layout))
			{
				return GroupKeyLayout.Unset;
			}
			return layout;
		}

		public static void Apply(GroupKeyLayout layout)
		{
			switch (layout)
			{
				case GroupKeyLayout.FKeys:
					ApplyFKeys();
					break;
				case GroupKeyLayout.NumberRow:
					ApplyNumberRow();
					break;
				case GroupKeyLayout.Manual:
					ApplyManual();
					break;
				default:
					break;
			}

			VerbalCommandsMod.Settings.groupKeyLayout = layout.ToString();
			VerbalCommandsMod.Settings.Write();
		}

		private static void ApplyFKeys()
		{
			KeyBindingDef[] groupKeys = VerbalCommandsDefs.GroupKeys;
			List<string> unboundTabs = new List<string>();

			for (int i = 1; i <= 9; i++)
			{
				string mainTabDefName = MainTabKeyBindingDefNames[i - 1];
				KeyBindingDef tabKey = DefDatabase<KeyBindingDef>.GetNamedSilentFail(mainTabDefName);
				KeyBindingData tabData;
				if (tabKey != null && KeyPrefs.KeyPrefsData.keyPrefs.TryGetValue(tabKey, out tabData))
				{
					bool changed = false;
					if (tabData.keyBindingA == FKeyCodes[i - 1])
					{
						tabData.keyBindingA = KeyCode.None;
						changed = true;
					}
					if (tabData.keyBindingB == FKeyCodes[i - 1])
					{
						tabData.keyBindingB = KeyCode.None;
						changed = true;
					}
					if (changed)
					{
						unboundTabs.Add(mainTabDefName);
					}
				}

				if (groupKeys[i] != null)
				{
					SetGroupKeyBinding(groupKeys[i], FKeyCodes[i - 1]);
				}
			}

			KeyPrefs.Save();
			Log.Message("[VerbalCommands] FKeys layout applied. Tab shortcuts unbound: " +
				(unboundTabs.Count > 0 ? string.Join(", ", unboundTabs) : "(none)") + ". Groups 1-9 bound to F1-F9.");
		}

		private static void ApplyNumberRow()
		{
			KeyBindingDef[] groupKeys = VerbalCommandsDefs.GroupKeys;
			for (int i = 1; i <= 6; i++)
			{
				if (groupKeys[i] != null)
				{
					SetGroupKeyBinding(groupKeys[i], NumberRowCodes[i - 1]);
				}
			}
			for (int i = 7; i <= 9; i++)
			{
				if (groupKeys[i] != null)
				{
					SetGroupKeyBinding(groupKeys[i], KeyCode.None);
				}
			}

			KeyPrefs.Save();
			Log.Message("[VerbalCommands] NumberRow layout applied: groups 1-5 -> 5,6,7,8,9; group 6 -> 0. Groups 7-9 unbound (only 6 slots in this layout).");
		}

		private static void ApplyManual()
		{
			Log.Message("[VerbalCommands] Manual layout selected: no group keys bound automatically. Bind them yourself in Options > Keyboard (VerbalCommands_Group1..9).");
		}

		// For each MainTab_* def, restore its own XML default; clear every group key to None;
		// reset the layout to Unset so PromptIfUnset asks again next time.
		public static void RestoreVanillaTabKeys()
		{
			foreach (string mainTabDefName in MainTabKeyBindingDefNames)
			{
				KeyBindingDef tabKey = DefDatabase<KeyBindingDef>.GetNamedSilentFail(mainTabDefName);
				if (tabKey == null)
				{
					continue;
				}
				KeyBindingData tabData;
				if (!KeyPrefs.KeyPrefsData.keyPrefs.TryGetValue(tabKey, out tabData))
				{
					tabData = new KeyBindingData(KeyCode.None, KeyCode.None);
					KeyPrefs.KeyPrefsData.keyPrefs[tabKey] = tabData;
				}
				tabData.keyBindingA = tabKey.defaultKeyCodeA;
				tabData.keyBindingB = tabKey.defaultKeyCodeB;
			}

			KeyBindingDef[] groupKeys = VerbalCommandsDefs.GroupKeys;
			for (int i = 1; i <= 9; i++)
			{
				if (groupKeys[i] != null)
				{
					SetGroupKeyBinding(groupKeys[i], KeyCode.None);
				}
			}

			VerbalCommandsMod.Settings.groupKeyLayout = GroupKeyLayout.Unset.ToString();
			VerbalCommandsMod.Settings.Write();
			KeyPrefs.Save();
			Log.Message("[VerbalCommands] Restored vanilla F1-F9 tab shortcuts; cleared all group key bindings.");
		}

		// Shown once, the first time a group is defined (from Executor.ResolveDefineGroup's apply
		// step) or from the settings "Choose group hotkeys..." button. No-op once a layout is set;
		// RestoreVanillaTabKeys / the settings button are the only ways back to Unset.
		public static void PromptIfUnset()
		{
			if (CurrentLayout() != GroupKeyLayout.Unset)
			{
				return;
			}

			Find.WindowStack.Add(new Dialog_MessageBox(
				"VerbalCommands_PromptFKeys".Translate(),
				"VerbalCommands_PromptFKeysYes".Translate(),
				delegate { Apply(GroupKeyLayout.FKeys); },
				"VerbalCommands_PromptFKeysNo".Translate(),
				delegate { PromptNumberRow(); },
				"VerbalCommands_PromptTitle".Translate()));
		}

		private static void PromptNumberRow()
		{
			Find.WindowStack.Add(new Dialog_MessageBox(
				"VerbalCommands_PromptNumberRow".Translate(),
				"VerbalCommands_PromptNumberRowYes".Translate(),
				delegate { Apply(GroupKeyLayout.NumberRow); },
				"VerbalCommands_PromptNumberRowNo".Translate(),
				delegate { Apply(GroupKeyLayout.Manual); },
				"VerbalCommands_PromptTitle".Translate()));
		}

		private static void SetGroupKeyBinding(KeyBindingDef def, KeyCode code)
		{
			KeyBindingData data;
			if (!KeyPrefs.KeyPrefsData.keyPrefs.TryGetValue(def, out data))
			{
				data = new KeyBindingData(KeyCode.None, KeyCode.None);
				KeyPrefs.KeyPrefsData.keyPrefs[def] = data;
			}
			data.keyBindingA = code;
			data.keyBindingB = KeyCode.None;
		}
	}
}
