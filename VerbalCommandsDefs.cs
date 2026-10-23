using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Static lookup for defs this mod defines in XML. KeyBindingDef.GetNamed does a dictionary
	// lookup each call (Verse/DefDatabase.cs:209), so cache it lazily rather than call it every GUI frame.
	public static class VerbalCommandsDefs
	{
		private static KeyBindingDef openKey;

		public static KeyBindingDef OpenKey
		{
			get
			{
				if (openKey == null)
				{
					openKey = DefDatabase<KeyBindingDef>.GetNamed("VerbalCommands_Open");
				}
				return openKey;
			}
		}

		private static KeyBindingDef voiceToggleKey;

		public static KeyBindingDef VoiceToggleKey
		{
			get
			{
				if (voiceToggleKey == null)
				{
					voiceToggleKey = DefDatabase<KeyBindingDef>.GetNamed("VerbalCommands_VoiceToggle");
				}
				return voiceToggleKey;
			}
		}

		private static KeyBindingDef voiceToggleDevKey;

		// Second toggle key (brief 08), active only while Prefs.DevMode is true; see the XML comment
		// for why dev mode needs a separate key from VoiceToggleKey.
		public static KeyBindingDef VoiceToggleDevKey
		{
			get
			{
				if (voiceToggleDevKey == null)
				{
					voiceToggleDevKey = DefDatabase<KeyBindingDef>.GetNamed("VerbalCommands_VoiceToggleDev");
				}
				return voiceToggleDevKey;
			}
		}

		// Brief 08: "evaluate Prefs.DevMode live on every event" (Verse/Prefs.cs:368 is a plain
		// property read, not cached state) - both the key-handling code and the icon's
		// tooltip/click-to-rebind target must agree on which def is "active" right now, so this is
		// the single source of truth for that choice.
		public static KeyBindingDef ActiveVoiceToggleKey => Prefs.DevMode ? VoiceToggleDevKey : VoiceToggleKey;

		// Index 1..9 = VerbalCommands_Group1.._Group9; index 0 is unused (null).
		private static KeyBindingDef[] groupKeys;

		public static KeyBindingDef[] GroupKeys
		{
			get
			{
				if (groupKeys == null)
				{
					groupKeys = new KeyBindingDef[10];
					for (int i = 1; i <= 9; i++)
					{
						groupKeys[i] = DefDatabase<KeyBindingDef>.GetNamed("VerbalCommands_Group" + i);
					}
				}
				return groupKeys;
			}
		}
	}
}
