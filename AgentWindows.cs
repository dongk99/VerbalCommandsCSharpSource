using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Found 2026-10-04 in a live game played only through the inbox: a research project finished, the game opened its
	// dialog, and from then on every "run 2 hours." was applied while the tick never moved ("show speed." said the run
	// was on hold while paused). No sentence could show which window it was or close it. "show windows." lists the
	// windows that hold the game paused, "close window." presses the only button of the top one, "close window with
	// option OPTION." names the button or option when there are several. MAIN THREAD ONLY.
	// Fair play: nothing here reads or sets dev mode. A button is pressed the way a click on it would (its own action,
	// then the window closes), so what happens is what the game does when a player clicks it.
	//
	// Game facts relied on (decompiled tree sources\RimWorldDecompiled\):
	//   a window that pauses is one with forcePause set; WindowStack.WindowsForcePause is true when any window has it  Verse\Window.cs:28; Verse\WindowStack.cs:45-58
	//   the game then counts as paused: TickManager.Paused = speed Paused or ForcePaused; ForcePaused = WindowsForcePause, a
	//       long event, the tile picker, a gravship cutscene or its landing confirmation (or map-generator debug)  Verse\TickManager.cs:130-156
	//       (setting the speed does not lift it: a run's CurTimeSpeed is set, the game still stands still)
	//   WindowStack.Windows is the draw order, a new window goes after every window of the same or a lower layer, so
	//       the last window with forcePause is the one on top  Verse\WindowStack.cs:358-367, :539-549
	//   Window.Close removes the window from the stack  Verse\Window.cs:321-324
	//   message box: text, title, three buttons (A is always there, "OK" when it has no text; B and C only when their text
	//       is set); a click runs the button's action, then Close (C only when buttonCClose); forcePause is set in the
	//       constructor  Verse\Dialog_MessageBox.cs:8-34, :62-88, :115-146
	//   node tree dialog: forcePause in the constructor; the title and the current node (text and options) are protected
	//       fields; Dialog_Negotiation and Dialog_NodeTreeWithFactionInfo are subclasses  Verse\Dialog_NodeTree.cs:13-15, :59, :147-155;
	//       RimWorld\Dialog_Negotiation.cs:6; RimWorld\Dialog_NodeTreeWithFactionInfo.cs:6
	//   a node option: its text is a protected field; a click is the protected Activate (close the window when resolveTree,
	//       run the action, go to the link)  Verse\DiaOption.cs:12, :18-24, :100-120
	//   the research-complete box is a Dialog_NodeTree with TWO options, "OK" and "Research screen", both closing the window;
	//       it has no title  RimWorld\ResearchManager.cs:461-474
	//   Dialog_Trade pauses too (it has its own sentences in AgentInteract.cs)  RimWorld\Dialog_Trade.cs:240
	public static class AgentWindows
	{
		private const int WindowCap = 8;
		private const int BodyWidth = 600;
		private const int TitleWidth = 60;

		// What a choice that can lose something looks like in its own words (the letter choices use the same idea,
		// FormWave1.PlanLetterChoose): a sure risky mark for these, none for the rest.
		private static readonly Regex RiskyWords = new Regex(@"\b(reject|refuse|decline|betray|pay|capture|ransom|abandon|destroy|delete|kill|execute|sacrifice|overwrite|discard|release|slaughter|banish|surrender)\b", RegexOptions.IgnoreCase);

		private sealed class Choice
		{
			public string label;
			public bool disabled;
			public string why;
			public bool risky;
			public Action press;
		}

		// ---- the windows --------------------------------------------------------------------------------------

		// The windows that hold the game paused, top one first.
		private static List<Window> Pausing()
		{
			List<Window> list = new List<Window>();
			if (Find.WindowStack == null)
			{
				return list;
			}
			IList<Window> all = Find.WindowStack.Windows;
			for (int i = all.Count - 1; i >= 0; i--)
			{
				if (all[i] != null && all[i].forcePause)
				{
					list.Add(all[i]);
				}
			}
			return list;
		}

		private static Window Top()
		{
			List<Window> list = Pausing();
			return list.Count > 0 ? list[0] : null;
		}

		private static bool IsTrade(Window w)
		{
			return w is Dialog_Trade;
		}

		// The mod's own dialogs (the command window, the key notices): the player's, never closed from here.
		private static bool IsOwn(Window w)
		{
			return w.GetType().Assembly == typeof(AgentWindows).Assembly;
		}

		private static bool IsClosable(Window w)
		{
			return !IsTrade(w) && !IsOwn(w) && (w is Dialog_MessageBox || w is Dialog_NodeTree);
		}

		private static string Kind(Window w)
		{
			if (IsTrade(w))
			{
				return "trade window";
			}
			if (IsOwn(w))
			{
				return "mod's own window (" + w.GetType().Name + ")";
			}
			if (w is Dialog_MessageBox)
			{
				return "message box";
			}
			if (w is Dialog_NodeTree)
			{
				return "dialog (" + w.GetType().Name + ")";
			}
			return "window (" + w.GetType().Name + ")";
		}

		private static string Clean(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return "";
			}
			return Regex.Replace(text.StripTags(), @"\s+", " ").Trim();
		}

		private static string Cut(string text, int max)
		{
			if (text == null || text.Length <= max)
			{
				return text ?? "";
			}
			return text.Substring(0, max - 3).TrimEnd() + "...";
		}

		private static object Field(object owner, string name)
		{
			for (Type t = owner.GetType(); t != null; t = t.BaseType)
			{
				FieldInfo info = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
				if (info != null)
				{
					return info.GetValue(owner);
				}
			}
			return null;
		}

		private static DiaNode NodeOf(Window w)
		{
			return Field(w, "curNode") as DiaNode;
		}

		// The title where the game exposes one (a message box's title, a node tree's title); else the window's own
		// optional title; "" when none.
		private static string TitleOf(Window w)
		{
			Dialog_MessageBox box = w as Dialog_MessageBox;
			string title = box != null ? box.title : (w is Dialog_NodeTree ? Field(w, "title") as string : null);
			if (string.IsNullOrEmpty(title))
			{
				title = w.optionalTitle;
			}
			return Clean(title);
		}

		// The text where the game exposes it (a message box's text, a node tree's current node); "" when none.
		private static string BodyOf(Window w)
		{
			Dialog_MessageBox box = w as Dialog_MessageBox;
			if (box != null)
			{
				return Clean(box.text.Resolve());
			}
			DiaNode node = w is Dialog_NodeTree ? NodeOf(w) : null;
			return node != null ? Clean(node.text.Resolve()) : "";
		}

		// How the window is named in a sentence: its kind, and its title or else how its text starts (the
		// research-complete box has no title).
		private static string Label(Window w)
		{
			string title = TitleOf(w);
			if (title.Length > 0)
			{
				return Kind(w) + " \"" + Cut(title, TitleWidth) + "\"";
			}
			string body = BodyOf(w);
			return body.Length > 0 ? Kind(w) + " that starts \"" + Cut(body, TitleWidth) + "\"" : Kind(w);
		}

		// ---- the buttons and options --------------------------------------------------------------------------

		private static List<Choice> ChoicesOf(Window w)
		{
			List<Choice> list = new List<Choice>();
			Dialog_MessageBox box = w as Dialog_MessageBox;
			if (box != null)
			{
				AddButton(list, box.buttonAText, box.buttonADestructive, delegate
				{
					if (box.buttonAAction != null)
					{
						box.buttonAAction();
					}
					box.Close();
				});
				if (box.buttonBText != null)
				{
					AddButton(list, box.buttonBText, false, delegate
					{
						if (box.buttonBAction != null)
						{
							box.buttonBAction();
						}
						box.Close();
					});
				}
				if (box.buttonCText != null)
				{
					AddButton(list, box.buttonCText, false, delegate
					{
						if (box.buttonCAction != null)
						{
							box.buttonCAction();
						}
						if (box.buttonCClose)
						{
							box.Close();
						}
					});
				}
				return list;
			}
			DiaNode node = w is Dialog_NodeTree ? NodeOf(w) : null;
			if (node == null)
			{
				return list;
			}
			foreach (DiaOption option in node.options)
			{
				// A hyperlink opens an info card, it answers nothing.
				if (option == null || option.hyperlink.def != null)
				{
					continue;
				}
				DiaOption o = option;
				AddButton(list, Field(o, "text") as string, false, delegate { Activate(o); });
				Choice last = list.LastOrDefault();
				if (last != null && o.disabled)
				{
					last.disabled = true;
					last.why = Clean(o.disabledReason);
				}
			}
			return list;
		}

		private static void AddButton(List<Choice> list, string text, bool destructive, Action press)
		{
			string label = Clean(text);
			if (label.Length == 0)
			{
				return;
			}
			Choice choice = new Choice();
			choice.label = label;
			choice.risky = destructive || RiskyWords.IsMatch(label);
			choice.press = press;
			list.Add(choice);
		}

		// What a click on a node option runs: DiaOption.Activate (protected; Verse\DiaOption.cs:100).
		private static void Activate(DiaOption option)
		{
			MethodInfo method = typeof(DiaOption).GetMethod("Activate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (method == null)
			{
				throw new Fault("This version of the game has no way I know to press that option; nothing was pressed.");
			}
			method.Invoke(option, null);
		}

		private static string ChoiceText(Choice ch)
		{
			return "'" + ch.label + "'" + (ch.disabled ? " (not now" + (string.IsNullOrEmpty(ch.why) ? "" : ": " + ch.why) + ")" : "");
		}

		private static string ChoicesText(List<Choice> choices)
		{
			return string.Join("; ", choices.Select((Choice ch) => ChoiceText(ch)).ToArray());
		}

		// ---- what closes it ---------------------------------------------------------------------------------

		// One sentence on how the window can be closed, for the answer and the notes.
		private static string CloseHint(Window w)
		{
			if (IsTrade(w))
			{
				return "It has its own sentences: 'show trade.', then 'accept trade.' or 'cancel trade.'.";
			}
			if (IsOwn(w))
			{
				return "It is the mod's own window; the player closes it, 'close window.' never does.";
			}
			if (!IsClosable(w))
			{
				return "The mod does not know how to close this kind of window; 'close window.' will refuse it.";
			}
			List<Choice> choices = ChoicesOf(w);
			if (choices.Count == 0)
			{
				return "It shows no button the mod can press.";
			}
			if (choices.Count == 1)
			{
				return "'close window.' presses its only button, " + ChoiceText(choices[0]) + ".";
			}
			return "It has several choices, so 'close window.' refuses it: say 'close window with option WORDS.' with WORDS from one of " + ChoicesText(choices) + ".";
		}

		// ---- the sentences ----------------------------------------------------------------------------------

		// "show windows."
		public static string Show(FormContext c)
		{
			List<Window> windows = Pausing();
			if (windows.Count == 0)
			{
				return "No window is open. " + HoldLine();
			}
			List<string> lines = new List<string>();
			lines.Add(windows.Count == 1
				? "One window holds the game paused:"
				: windows.Count + " windows hold the game paused, the top one first (it is the one 'close window.' acts on):");
			int n = 0;
			foreach (Window w in windows.Take(WindowCap))
			{
				n++;
				lines.Add(n + ". " + Kind(w) + (TitleOf(w).Length > 0 ? " \"" + Cut(TitleOf(w), TitleWidth) + "\"" : ""));
				string body = BodyOf(w);
				if (body.Length > 0)
				{
					lines.Add("    text: " + Cut(body, BodyWidth));
				}
				if (IsClosable(w))
				{
					List<Choice> choices = ChoicesOf(w);
					lines.Add("    " + (w is Dialog_MessageBox ? "buttons: " : "options: ") + (choices.Count > 0 ? ChoicesText(choices) : "none the mod can press"));
				}
				lines.Add("    " + CloseHint(w));
			}
			if (windows.Count > WindowCap)
			{
				lines.Add("and " + (windows.Count - WindowCap) + " more windows below.");
			}
			return string.Join("\n", lines.ToArray());
		}

		// "close window." / "close window with option OPTION."
		public static PlanReply Plan(FormContext c)
		{
			try
			{
				return PlanClose(c);
			}
			catch (Fault fault)
			{
				return PlanReply.Fail(fault.Message);
			}
		}

		private static PlanReply PlanClose(FormContext c)
		{
			Window window = Top();
			if (window == null)
			{
				throw new Fault("No window is open that holds the game paused, so there is nothing to close. " + HoldLine());
			}
			string name = Label(window);
			if (IsTrade(window))
			{
				throw new Fault("The top window is the " + name + ". This sentence never closes it. " + CloseHint(window));
			}
			if (IsOwn(window))
			{
				throw new Fault("The top window is the " + name + ". " + CloseHint(window));
			}
			if (!IsClosable(window))
			{
				throw new Fault("The top window is the " + name + ". " + CloseHint(window));
			}
			List<Choice> choices = ChoicesOf(window);
			if (choices.Count == 0)
			{
				throw new Fault("The top window is the " + name + " and it shows no button the mod can press.");
			}
			string wanted = c.Has("OPTION") ? (c.Text("OPTION") ?? "").Trim() : "";
			Choice pick;
			if (wanted.Length == 0)
			{
				if (choices.Count > 1)
				{
					throw new Fault("The top window is the " + name + " and it has " + choices.Count + " choices, so I will not pick one for you: " + ChoicesText(choices)
						+ ". Say 'close window with option WORDS.' with words of the one you want.");
				}
				pick = choices[0];
			}
			else
			{
				string on = FormPlanner.Norm(wanted);
				List<Choice> hits = choices.Where((Choice ch) => FormPlanner.Norm(ch.label) == on).ToList();
				if (hits.Count == 0)
				{
					hits = choices.Where((Choice ch) => FormPlanner.ContainsWord(FormPlanner.Norm(ch.label), on)).ToList();
				}
				if (hits.Count == 0)
				{
					throw new Fault("The top window, the " + name + ", has no choice '" + wanted + "'. Its choices are " + ChoicesText(choices) + ".");
				}
				if (hits.Count > 1)
				{
					throw new Fault("'" + wanted + "' fits several choices of the " + name + ": " + ChoicesText(hits) + ". Say more of the one you mean.");
				}
				pick = hits[0];
			}
			if (pick.disabled)
			{
				throw new Fault("'" + pick.label + "' can't be pressed in the " + name + " right now" + (string.IsNullOrEmpty(pick.why) ? "." : ": " + pick.why + "."));
			}
			string label = pick.label;
			Plan plan = FormPlanner.NewPlan("agent window close");
			// Settings gate (user, 2026-10-26): Build advice tips/notes. The RISKY line below stays.
			if (pick.risky && VerbalCommandsMod.BuildAdviceOn)
			{
				plan.notes.Add("The words of this choice say it gives something up or can not be taken back; read them before you apply.");
			}
			Write write = new Write();
			write.description = "Window " + name + ": press '" + label + "'";
			write.apply = delegate
			{
				if (!ReferenceEquals(Top(), window))
				{
					throw new Fault("The top window changed; ask 'show windows.' again.");
				}
				Choice again = ChoicesOf(window).FirstOrDefault((Choice ch) => ch.label == label);
				if (again == null || again.disabled)
				{
					throw new Fault("'" + label + "' can no longer be pressed.");
				}
				again.press();
			};
			plan.writes.Add(write.Reads(() => AfterPress(window)));
			string line = (pick.risky ? "RISKY: " : "") + "Press '" + label + "' in the " + name + "." + (pick.risky ? " That may not be taken back." : "");
			return FormPlanner.Reply(plan, line, null, null);
		}

		// The read-back of the press: is the window gone, and does another one still hold the pause.
		private static string AfterPress(Window window)
		{
			bool open = Find.WindowStack != null && Find.WindowStack.IsOpen(window);
			string text = open ? "the window is still open: " + Label(window) : "the window is closed";
			Window top = Top();
			if (top != null && !ReferenceEquals(top, window))
			{
				text += "; another window holds the game paused: " + Label(top);
			}
			return text;
		}

		// ---- for the run path ---------------------------------------------------------------------------------

		// A plain note for the plan of a run or a speed change when a window holds the game paused now; null when none.
		// The run starts either way, it just stands still until the window is closed.
		public static string HoldNote()
		{
			Window top = Top();
			if (top == null)
			{
				return null;
			}
			return "A window holds the game paused: the " + Label(top) + ". The run starts, but the game stands still until it is closed. " + CloseHint(top);
		}

		// One line for "show speed." when the game is force-paused: which window (or what else) holds it.
		public static string HoldLine()
		{
			List<Window> windows = Pausing();
			if (windows.Count > 0)
			{
				Window top = windows[0];
				return "The pause is held by the " + Label(top) + (windows.Count > 1 ? " and " + (windows.Count - 1) + " more window" + (windows.Count > 2 ? "s" : "") + " below it" : "") + ". " + CloseHint(top);
			}
			TickManager tm = Find.TickManager;
			if (tm != null && tm.ForcePaused)
			{
				return "No window holds the game paused, but the game says it is paused by force: a loading event, the world-map tile picker, a gravship cutscene or its landing confirmation.";
			}
			return "Nothing holds the game paused.";
		}
	}
}
