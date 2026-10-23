using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Rebind popup for whichever voice-toggle def is passed in - VerbalCommands_VoiceToggle or
	// VerbalCommands_VoiceToggleDev (brief 08 adds the second def; this class already took the def as
	// a ctor parameter rather than hardcoding one, so nothing else here changes), opened by clicking
	// the mic status icon or a "Change" button in Mod settings.
	//
	// RimWorld.Dialog_DefineBinding(KeyPrefsData, KeyBindingDef, KeyPrefs.BindingSlot) writes the new
	// binding into whatever KeyPrefsData it is given (sources/RimWorldDecompiled/RimWorld/Dialog_DefineBinding.cs:22-30
	// for the constructor, :44-58 for DoWindowContents calling keyPrefsData.SetBinding on KeyDown) and
	// does NOT save to disk itself. We pass the LIVE KeyPrefs.KeyPrefsData (Verse/KeyPrefs.cs:18-28)
	// directly (not a clone, unlike RimWorld.Dialog_KeyBindings which clones it and only writes back on
	// its own OK button - RimWorld/Dialog_KeyBindings.cs:32/95-101) and slot A, so the binding takes
	// effect immediately; PostClose then persists it with KeyPrefs.Save() (Verse/KeyPrefs.cs:62-84).
	public class Dialog_DefineVoiceToggleBinding : Dialog_DefineBinding
	{
		public Dialog_DefineVoiceToggleBinding(KeyBindingDef keyDef)
			: base(KeyPrefs.KeyPrefsData, keyDef, KeyPrefs.BindingSlot.A)
		{
		}

		public override void PostClose()
		{
			base.PostClose();
			KeyPrefs.Save();
		}
	}
}
