using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace VerbalCommands
{
	public class Dialog_VerbalCommand : Window
	{
		private readonly OrderController controller = new OrderController();
		private string orderText = "";
		private Vector2 logScrollPosition = Vector2.zero;
		private readonly SttEngine stt;

		// Recognizer finals appended since the last Send or since the box opened (brief 11 section C
		// "voice_raw"), space-joined via the same rule as AppendRecognizedText below. Reset on
		// PostOpen and after every Send.
		private string voiceRawSinceSend = "";
		private bool anyVoiceFinalSinceSend;

		public Dialog_VerbalCommand()
		{
			doCloseX = true;
			closeOnAccept = false;
			closeOnCancel = true;
			absorbInputAroundWindow = true;
			draggable = true;
			preventCameraMotion = false;
			// Brief 11 section A.2: "Dialog_VerbalCommand sets forcePause = true (see Verse/Window.cs;
			// the dev-mode notice already uses it)." Verse/Window.cs:28 declares `public bool
			// forcePause;`; Dialog_DevModeVoiceKeyNotice.cs sets it the same way.
			forcePause = true;

			stt = new SttEngine(AppendRecognizedText);
		}

		public override Vector2 InitialSize => new Vector2(700f, 560f);

		// Auto-start (brief 07, carried into brief 11): voice is ON by default; starts automatically
		// whenever the dialog opens with the setting ON, including when opened by the "toggle
		// everywhere" key path (GameComponent_VerbalCommands sets voiceEnabled=true before opening).
		public override void PostOpen()
		{
			base.PostOpen();
			voiceRawSinceSend = "";
			anyVoiceFinalSinceSend = false;
			if (VerbalCommandsMod.Settings.voiceEnabled)
			{
				stt.Start();
			}
		}

		public override void PostClose()
		{
			// Brief 15 Step C: a close during the router call must not fire the main call later.
			controller.NotifyDialogClosed();
			stt.Dispose();
			base.PostClose();
			UI.UnfocusCurrentControl();
		}

		// Verse/Window.cs:125 WindowUpdate() runs once per real Update() frame for every open window -
		// called from Verse/WindowStack.cs:171-177 WindowsUpdate(), from Verse/UIRoot.cs:73-77
		// UIRootUpdate(), from Verse/Root.cs:138 - unconditionally on game pause, the same per-frame
		// path already relied on for GameComponentUpdate draining MainThread while paused (see
		// GameComponent_VerbalCommands). This is the one main-thread place mic samples are pulled each
		// frame (brief 11 section B).
		public override void WindowUpdate()
		{
			base.WindowUpdate();
			stt.Poll();
		}

		// Called by GameComponent_VerbalCommands when the player presses the voice toggle key while
		// this dialog is open: it already flipped and saved VerbalCommandsMod.Settings.voiceEnabled;
		// this starts/stops the engine to match immediately.
		public void SetVoiceEnabled(bool enabled)
		{
			if (enabled)
			{
				stt.Start();
			}
			else
			{
				stt.Stop("toggle");
			}
		}

		// The dialog owns orderText and decides the append rule; SttEngine only knows to call this
		// with each final recognized phrase. Also tracks voiceRawSinceSend/anyVoiceFinalSinceSend for
		// the transcript log (brief 11 section C).
		private void AppendRecognizedText(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}
			AppendWithSpace(ref orderText, text);
			AppendWithSpace(ref voiceRawSinceSend, text);
			anyVoiceFinalSinceSend = true;
		}

		private static void AppendWithSpace(ref string target, string text)
		{
			if (!string.IsNullOrEmpty(target) && !char.IsWhiteSpace(target[target.Length - 1]))
			{
				target += " ";
			}
			target += text;
		}

		public override void DoWindowContents(Rect inRect)
		{
			float curY = inRect.y;

			// Brief 23: after a question from the router the box is emptied, so the player types only the answer.
			if (controller.TakeClearOrderBox())
			{
				orderText = "";
			}

			float titleHeight = 30f;
			Rect titleRect = new Rect(inRect.x, curY, inRect.width, titleHeight);
			Widgets.Label(titleRect, "Verbal Commands");
			DrawVoiceIcon(inRect.x + Text.CalcSize("Verbal Commands").x + 6f, curY, titleHeight);
			curY += 34f;

			float orderHeight = Text.LineHeight * 3f + 8f;
			Rect orderRect = new Rect(inRect.x, curY, inRect.width, orderHeight);
			orderText = Widgets.TextArea(orderRect, orderText);
			curY += orderHeight + 6f;

			if (!string.IsNullOrEmpty(stt.Hypothesis))
			{
				GUI.color = Color.grey;
				Widgets.Label(new Rect(inRect.x, curY, inRect.width, Text.LineHeight), stt.Hypothesis);
				GUI.color = Color.white;
				curY += Text.LineHeight + 4f;
			}

			float buttonHeight = 30f;
			float buttonWidth = (inRect.width - 16f) / 5f;
			Rect sendRect = new Rect(inRect.x, curY, buttonWidth, buttonHeight);
			Rect applyRect = new Rect(sendRect.xMax + 4f, curY, buttonWidth, buttonHeight);
			Rect cancelRect = new Rect(applyRect.xMax + 4f, curY, buttonWidth, buttonHeight);
			Rect clearRect = new Rect(cancelRect.xMax + 4f, curY, buttonWidth, buttonHeight);
			Rect closeRect = new Rect(clearRect.xMax + 4f, curY, buttonWidth, buttonHeight);

			bool canSend = (controller.State == OrderState.Idle || controller.State == OrderState.Done ||
				controller.State == OrderState.Faulted) && !string.IsNullOrEmpty(orderText);
			bool canApplyOrCancel = controller.State == OrderState.AwaitingConfirm;

			if (Widgets.ButtonText(sendRect, "Send", true, true, canSend))
			{
				// Brief 11 section A.4: Send stops voice for THIS box only (voiceEnabled setting is
				// not touched, so the next open/toggle-on still starts listening). Capture the
				// transcript fields before anything is reset.
				bool voiceForTranscript = anyVoiceFinalSinceSend;
				string voiceRawForTranscript = voiceRawSinceSend;
				string sentTextForTranscript = orderText;

				controller.Submit(orderText);
				stt.Stop("send");

				if (VerbalCommandsMod.Settings.saveTranscripts)
				{
					string sttModel = voiceForTranscript ? SttEngine.ModelFolderName : null;
					TranscriptLogger.AppendAsync(voiceForTranscript, voiceRawForTranscript, sentTextForTranscript, sttModel);
				}

				voiceRawSinceSend = "";
				anyVoiceFinalSinceSend = false;
			}
			if (Widgets.ButtonText(applyRect, "Apply", true, true, canApplyOrCancel))
			{
				controller.ApplyAll();
			}
			if (Widgets.ButtonText(cancelRect, "Cancel", true, true, canApplyOrCancel))
			{
				controller.CancelPending();
			}
			// Clears the whole conversation (log, pending question, plan); a run in progress is cancelled first.
			if (Widgets.ButtonText(clearRect, "VerbalCommands_ClearConversation".Translate(), true, true,
				controller.State != OrderState.Applying))
			{
				controller.ClearConversation();
				logScrollPosition = Vector2.zero;
			}
			if (Widgets.ButtonText(closeRect, "Close"))
			{
				Close();
			}
			curY += buttonHeight + 6f;

			// The "can't do it" option (user, 2026-10-05: in this window, not the mod settings; saved). Same save as the
			// voice toggle key (GameComponent_VerbalCommands: Settings.Write()).
			VerbalCommandsSettings settings = VerbalCommandsMod.Settings;
			if (settings != null)
			{
				Rect fallbackRect = new Rect(inRect.x, curY, inRect.width, Text.LineHeight + 2f);
				bool fallbackOnCannot = settings.fallbackOnCannot;
				Widgets.CheckboxLabeled(fallbackRect, "VerbalCommands_FallbackOnCannot".Translate(), ref fallbackOnCannot);
				TooltipHandler.TipRegion(fallbackRect, "VerbalCommands_FallbackOnCannotTip".Translate());
				if (fallbackOnCannot != settings.fallbackOnCannot)
				{
					settings.fallbackOnCannot = fallbackOnCannot;
					settings.Write();
				}
				curY += fallbackRect.height + 4f;
			}

			if (controller.State == OrderState.AwaitingConfirm && controller.Plans.Count > 0)
			{
				// Brief 23: the sentence router's confirm lines are drawn instead of the writes' descriptions, and the
				// box grows to fit them (up to a limit).
				bool sentenceLines = controller.ConfirmLines.Count > 0;
				float previewHeight = 130f;
				if (sentenceLines)
				{
					int lineCount = controller.ConfirmLines.Count;
					foreach (Plan countPlan in controller.Plans)
					{
						lineCount += countPlan.notes.Count;
					}
					previewHeight = Mathf.Clamp(Text.LineHeight * lineCount + 8f, 130f, 260f);
				}
				Rect previewOuter = new Rect(inRect.x, curY, inRect.width, previewHeight);
				Widgets.DrawBox(previewOuter);
				float lineY = previewOuter.y + 2f;
				if (sentenceLines)
				{
					foreach (string confirmLine in controller.ConfirmLines)
					{
						if (lineY + Text.LineHeight > previewOuter.yMax)
						{
							break;
						}
						Widgets.Label(new Rect(previewOuter.x + 4f, lineY, previewOuter.width - 8f, Text.LineHeight), confirmLine);
						lineY += Text.LineHeight;
					}
				}
				// Brief 15c fix 4: notes (including refusals) drawn before the write lines, so they are
				// not the first thing cut off in this fixed-height box.
				foreach (Plan plan in controller.Plans)
				{
					foreach (string note in plan.notes)
					{
						if (lineY + Text.LineHeight > previewOuter.yMax)
						{
							break;
						}
						Widgets.Label(new Rect(previewOuter.x + 4f, lineY, previewOuter.width - 8f, Text.LineHeight), "note: " + note);
						lineY += Text.LineHeight;
					}
					foreach (Write write in plan.writes)
					{
						if (sentenceLines)
						{
							break;
						}
						if (lineY + Text.LineHeight > previewOuter.yMax)
						{
							break;
						}
						Widgets.Label(new Rect(previewOuter.x + 4f, lineY, previewOuter.width - 8f, Text.LineHeight), write.description);
						lineY += Text.LineHeight;
					}
				}
				curY += previewHeight + 6f;
			}

			Rect logOuter = new Rect(inRect.x, curY, inRect.width, inRect.yMax - curY);
			string logText = string.Join("\n", controller.Log);
			float logTextHeight = Mathf.Max(logOuter.height, Text.LineHeight * (controller.Log.Count + 1));
			Rect logView = new Rect(0f, 0f, logOuter.width - 16f, logTextHeight);
			Widgets.BeginScrollView(logOuter, ref logScrollPosition, logView);
			Widgets.TextArea(new Rect(0f, 0f, logView.width, logView.height), logText, true);
			Widgets.EndScrollView();
		}

		// Status icon (brief 07, updated by brief 11): immediately right of the title, vertically
		// centered with it. Green = mic capturing AND recognizer ready (stt.IsListening); grey slash =
		// otherwise (off, loading model, or error). Click opens the vanilla rebind popup for whichever
		// voice-toggle def is currently active.
		private void DrawVoiceIcon(float x, float rowY, float rowHeight)
		{
			float iconSize = 24f;
			Rect iconRect = new Rect(x, rowY + (rowHeight - iconSize) / 2f, iconSize, iconSize);
			Texture2D tex = stt.IsListening ? VerbalCommandsTex.MicOn : VerbalCommandsTex.MicOff;
			if (Widgets.ButtonImage(iconRect, tex))
			{
				Find.WindowStack.Add(new Dialog_DefineVoiceToggleBinding(VerbalCommandsDefs.ActiveVoiceToggleKey));
			}
			if (Mouse.IsOver(iconRect))
			{
				TooltipHandler.TipRegion(iconRect, BuildVoiceTooltip());
			}
		}

		// Brief 11 section A.8: tooltip state is one of loading model / listening / off / error+reason.
		private string BuildVoiceTooltip()
		{
			string state;
			switch (stt.State)
			{
				case SttState.LoadingModel:
					state = "VerbalCommands_VoiceStateLoading".Translate();
					break;
				case SttState.Listening:
					state = "VerbalCommands_VoiceStateOn".Translate();
					break;
				case SttState.Error:
					state = "VerbalCommands_VoiceStateError".Translate(stt.ErrorReason);
					break;
				default:
					state = "VerbalCommands_VoiceStateOff".Translate();
					break;
			}
			string keyLine = "VerbalCommands_VoiceToggleKeyLabel".Translate(VerbalCommandsDefs.ActiveVoiceToggleKey.MainKeyLabel);
			string clickLine = "VerbalCommands_VoiceClickToChangeKey".Translate();
			return state + "\n" + keyLine + "\n" + clickLine;
		}
	}
}
