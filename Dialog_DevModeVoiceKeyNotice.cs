using UnityEngine;
using Verse;

namespace VerbalCommands
{
	// One-time notice (brief 08): shown when dev mode is on and VerbalCommands_VoiceToggle is still
	// bound to the same key as vanilla's Dev_ToggleDebugLog, so the player understands why ~ opens
	// the debug log instead of toggling voice, and that Backslash is the dev-mode voice key instead.
	// Gate and dialog-timing (LongEventHandler.ExecuteWhenFinished) are in
	// GameComponent_VerbalCommands.MaybeShowDevModeVoiceKeyNotice; this class only draws itself and
	// commits the "don't remind me" checkbox on close.
	public class Dialog_DevModeVoiceKeyNotice : Window
	{
		// Ticked by default (brief 08): closing the dialog without touching the checkbox suppresses
		// the notice from then on, same as an implicit "got it, don't show this again".
		private bool dontRemindMe = true;

		private readonly string bodyText;

		private readonly Vector2 initialSize;

		public Dialog_DevModeVoiceKeyNotice()
		{
			doCloseX = true;
			closeOnCancel = true;
			forcePause = true;
			absorbInputAroundWindow = true;
			optionalTitle = "VerbalCommands_DevModeNotice_Title".Translate();

			bodyText = "VerbalCommands_DevModeNotice_Body".Translate(VerbalCommandsDefs.VoiceToggleDevKey.MainKeyLabel);

			// Sized for the text (brief 08): fixed width, height derived from Text.CalcHeight of the
			// actual body string at that width (Verse/Text.cs:209) plus fixed chrome for the title,
			// checkbox row, close-button row and window margins (Window.Margin = 18f, Verse/Window.cs:104,
			// plus the extra Margin+25f the base class reserves for optionalTitle - Verse/Window.cs:250-253).
			const float width = 480f;
			const float chromeHeight = 18f * 2f + 25f + 30f + 30f + 24f;
			Text.Font = GameFont.Small;
			float bodyHeight = Text.CalcHeight(bodyText, width - 18f * 2f);
			initialSize = new Vector2(width, bodyHeight + chromeHeight);
		}

		public override Vector2 InitialSize => initialSize;

		public override void DoWindowContents(Rect inRect)
		{
			float curY = inRect.y;

			Widgets.Label(new Rect(inRect.x, curY, inRect.width, Text.CalcHeight(bodyText, inRect.width)), bodyText);
			curY = inRect.yMax - 30f - 6f - 30f;

			Rect checkboxRect = new Rect(inRect.x, curY, inRect.width, 30f);
			Widgets.CheckboxLabeled(checkboxRect, "VerbalCommands_DevModeNotice_DontRemind".Translate(), ref dontRemindMe);
			curY += 30f + 6f;

			float closeWidth = 120f;
			Rect closeRect = new Rect(inRect.xMax - closeWidth, curY, closeWidth, 30f);
			if (Widgets.ButtonText(closeRect, "VerbalCommands_Close".Translate()))
			{
				Close();
			}
			if (Mouse.IsOver(closeRect))
			{
				// Zero delay (brief 09): default TipSignal delay is 0.45f (Verse/TipSignal.cs:47) and
				// DrawActiveTips only draws once realtimeSinceStartup > firstTriggerTime + delay
				// (Verse/TooltipHandler.cs:121); players were closing the dialog before that elapsed.
				TooltipHandler.TipRegion(closeRect, new TipSignal("VerbalCommands_DevModeNotice_CloseTooltip".Translate(), 0f));
			}
		}

		// Commit only on close (brief 08), so every close path - the Close button, doCloseX, and
		// Escape (closeOnCancel routes OnCancelKeyPressed -> Close(), Verse/Window.cs:338-344) - all
		// funnel through PostClose and save the same way. Unticking then closing means it shows again
		// next entry; the checkbox never writes to settings while the dialog is merely open.
		public override void PostClose()
		{
			base.PostClose();
			VerbalCommandsMod.Settings.devModeVoiceNoticeSuppressed = dontRemindMe;
			VerbalCommandsMod.Settings.Write();
		}
	}
}
