using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Brief 27, wave 1: questions (contract 27). Read-only; the sources are what the player sees on screen:
	//   query alerts      AlertsReadout.activeAlerts (the list AlertsReadoutOnGUI draws, AlertsReadout.cs:253-276, 278-);
	//                     the label is Alert.Label (Alert.cs:50-58), the culprits Alert.GetReport().AllCulprits (Alert.cs:100, AlertReport.cs)
	//   query letters     LetterStack.LettersListForReading (Verse\LetterStack.cs:28), ChoiceLetter.Choices; the last
	//                     messages are the Message entries of Find.Archive (RimWorld\Archive.cs:14)
	//   query needs       Pawn_NeedsTracker.AllNeeds / Need.ShowOnNeedList (NeedsCardUtility.cs:94), Need_Mood.thoughts
	//                     GetDistinctMoodThoughtGroups / MoodOffsetOfGroup (NeedsCardUtility.cs:130, 230)
	//   query health      HealthCardUtility.VisibleHediffs rule (:231-262: missing parts first, then Visible hediffs),
	//                     GetEfficiencyLabel (:1028), HediffSet.PainTotal, BillStack of Bill_Medical
	//   query research    ResearchManager.GetProject / IsCurrentProject, ResearchProjectDef.ProgressApparent / CostApparent / CanStartNow
	//   query weather     GenDate.DateFullStringAt (GenDate.cs:175), GenLocalDate.HourOfDay / Season, MapTemperature.OutdoorTemp
	//                     (shown with ToStringTemperature, the unit the game is set to), WeatherManager.CurWeatherPerceived,
	//                     GameConditionManager.ActiveConditions (the map's and the world's)
	//   query colonists   Pawn_JobTracker.curDriver.GetReport (what the inspect pane shows), Need_Mood, Pawn.MentalState
	//   query prisoners   Pawn_GuestTracker.ExclusiveInteractionMode / Recruitable / resistance / will (ITab_Pawn_Visitor.cs:228-260)
	//   query animals     Pawn_PlayerSettings.Master, Pawn_TrainingTracker.GetWanted / HasLearned, area
	// Nothing here calls a getter that fills in a default (the policy trackers) or changes a pawn.
	public static partial class FormPlanner
	{
		private static QueryReply QueryWave1(FormContext c)
		{
			switch (c.form)
			{
				case "query alerts":
					return QueryReply.Ok(QueryAlerts());
				case "query letters":
					return QueryReply.Ok(QueryLetters());
				case "query letter":
					return QueryReply.Ok(QueryLetter(c));
				case "query needs":
					return QueryReply.Ok(QueryNeeds(c));
				case "query health":
					return QueryReply.Ok(QueryHealth(c));
				case "query research":
					return QueryReply.Ok(QueryResearch());
				case "query weather":
					return QueryReply.Ok(QueryWeather(c.map));
				case "query colonists":
					return QueryReply.Ok(QueryColonists(c.map));
				case "query prisoners":
					return QueryReply.Ok(QueryPrisoners(c.map));
				case "query animals":
					return QueryReply.Ok(QueryAnimals(c.map));
			}
			return null;
		}

		// ---- alerts ---------------------------------------------------------------------------------------------------

		private static List<Alert> ActiveAlerts()
		{
			UIRoot_Play root = Find.UIRoot as UIRoot_Play;
			if (root == null || root.alerts == null)
			{
				throw new Fault("The alerts can't be read right now.");
			}
			List<Alert> result = new List<Alert>();
			List<Alert> main = PrivateField(root.alerts, "activeAlerts") as List<Alert>;
			if (main != null)
			{
				result.AddRange(main);
			}
			foreach (string field in new string[] { "activeScenarioAlerts", "activeSignalActionAlerts" })
			{
				List<Alert> more = PrivateField(root.alerts, field) as List<Alert>;
				if (more != null)
				{
					result.AddRange(more.Where((Alert a) => !result.Contains(a)));
				}
			}
			IDictionary precepts = PrivateField(root.alerts, "activePreceptAlerts") as IDictionary;
			if (precepts != null)
			{
				foreach (object list in precepts.Values)
				{
					List<Alert> more = list as List<Alert>;
					if (more != null)
					{
						result.AddRange(more.Where((Alert a) => !result.Contains(a)));
					}
				}
			}
			return result.Where((Alert a) => a.Active && !string.IsNullOrEmpty(a.Label)).ToList();
		}

		private static string TargetName(GlobalTargetInfo target)
		{
			Pawn pawn = target.Thing as Pawn;
			if (pawn != null)
			{
				return pawn.LabelShort;
			}
			if (target.Thing != null)
			{
				return PlainText(target.Thing.LabelCap);
			}
			return null;
		}

		private static string QueryAlerts()
		{
			List<Alert> alerts = ActiveAlerts();
			if (alerts.Count == 0)
			{
				return "No alerts are showing.";
			}
			List<string> lines = new List<string>();
			lines.Add(alerts.Count + (alerts.Count == 1 ? " alert is showing:" : " alerts are showing:"));
			foreach (Alert alert in alerts.OrderByDescending((Alert a) => (int)a.Priority))
			{
				string text = "- " + PlainText(alert.Label) + (alert.Priority == AlertPriority.Critical ? " (critical)" : "");
				try
				{
					List<string> names = alert.GetReport().AllCulprits.Select(TargetName).Where((string s) => !string.IsNullOrEmpty(s)).Distinct().ToList();
					if (names.Count > 0)
					{
						text += ": " + CapList(names, 5);
					}
				}
				catch (Exception)
				{
				}
				lines.Add(text);
			}
			return string.Join("\n", lines.ToArray());
		}

		// ---- letters ---------------------------------------------------------------------------------------------------

		private static string QueryLetters()
		{
			List<string> lines = new List<string>();
			List<Letter> letters = Find.LetterStack.LettersListForReading.Where((Letter l) => l.CanShowInLetterStack).ToList();
			if (letters.Count == 0)
			{
				lines.Add("No letters are waiting.");
			}
			else
			{
				lines.Add(letters.Count + (letters.Count == 1 ? " letter is waiting:" : " letters are waiting:"));
				foreach (Letter letter in letters)
				{
					string text = "- " + LetterLabelOf(letter);
					ChoiceLetter choice = letter as ChoiceLetter;
					if (choice != null)
					{
						List<LetterChoice> options = ChoicesOf(choice);
						if (options.Count > 0)
						{
							text += " (choices: " + string.Join(", ", options.Select((LetterChoice o) => o.label + (o.option.disabled ? " [not possible now]" : "")).ToArray()) + ")";
						}
					}
					lines.Add(text);
				}
			}
			List<string> messages = Find.Archive.ArchivablesListForReading.OfType<Message>().Reverse().Take(6).Select((Message m) => PlainText(m.text)).ToList();
			if (messages.Count > 0)
			{
				lines.Add("Last messages, newest first:");
				foreach (string m in messages)
				{
					lines.Add("- " + m);
				}
			}
			return string.Join("\n", lines.ToArray());
		}

		// show letter LETTER. (user, 2026-10-15: the orchestrator's "show letter New lovers." got "I do not know how to do
		// that", so it closed the letter without knowing which two colonists it was about; "dll needs taping"). One letter's
		// text, as the letter window shows it: the open letters first (LetterStack.LettersListForReading,
		// Verse\LetterStack.cs:28), then closed ones still in the archive, newest first (Find.Archive; a Letter is an
		// IArchivable, Verse\Letter.cs:7). LETTER is matched on the label as the letter list writes it (LetterLabelOf):
		// the same label first, then a label that holds the words. The text is ChoiceLetter.Text (Verse\ChoiceLetter.cs:21),
		// for any other letter its tooltip (Letter.cs:47). Also who or what it points at (Letter.lookTargets) and, when open,
		// its choices.
		private static string QueryLetter(FormContext c)
		{
			string want = Norm(c.Text("LETTER") ?? "");
			List<Letter> open = Find.LetterStack.LettersListForReading.Where((Letter l) => l.CanShowInLetterStack).ToList();
			List<Letter> closed = Find.Archive.ArchivablesListForReading.OfType<Letter>().Reverse().Where((Letter l) => !open.Contains(l)).ToList();
			Letter found = null;
			foreach (List<Letter> list in new List<Letter>[] { open, closed })
			{
				found = list.FirstOrDefault((Letter l) => Norm(LetterLabelOf(l)) == want)
					?? list.FirstOrDefault((Letter l) => want.Length > 0 && Norm(LetterLabelOf(l)).Contains(want));
				if (found != null)
				{
					break;
				}
			}
			if (found == null)
			{
				string names = string.Join(", ", open.Concat(closed).Take(8).Select((Letter l) => LetterLabelOf(l)).ToArray());
				throw new Fault("There is no letter called '" + c.Text("LETTER") + "'." + (names.Length > 0 ? " Letters: " + names + "." : ""));
			}
			bool isOpen = open.Contains(found);
			List<string> lines = new List<string>();
			lines.Add(LetterLabelOf(found) + " (" + (isOpen ? "open" : "closed") + ", " + Math.Max(0, Find.TickManager.TicksGame - found.arrivalTick).ToStringTicksToPeriod() + " ago)");
			ChoiceLetter choice = found as ChoiceLetter;
			string body = choice != null ? PlainText(choice.Text) : PlainText(((IArchivable)found).ArchivedTooltip);
			if (!string.IsNullOrEmpty(body))
			{
				lines.Add(body);
			}
			if (found.lookTargets != null && found.lookTargets.Any)
			{
				List<string> about = found.lookTargets.targets.Select((GlobalTargetInfo t) => TargetName(t)).Where((string s) => !string.IsNullOrEmpty(s)).Distinct().ToList();
				if (about.Count > 0)
				{
					lines.Add("About: " + CapList(about, 6) + ".");
				}
			}
			if (isOpen && choice != null)
			{
				List<LetterChoice> options = ChoicesOf(choice);
				if (options.Count > 0)
				{
					lines.Add("Choices: " + string.Join(", ", options.Select((LetterChoice o) => o.label + (o.option.disabled ? " [not possible now]" : "")).ToArray()) + ".");
				}
			}
			return string.Join("\n", lines.ToArray());
		}

		// ---- needs ----------------------------------------------------------------------------------------------------

		private static string QueryNeeds(FormContext c)
		{
			Pawn pawn = ResolveOneFrom(c, "PAWN", PawnsOfColony(c.map), "colonist, prisoner or animal");
			if (pawn.needs == null || pawn.needs.AllNeeds.Count == 0)
			{
				throw new Fault(pawn.LabelShort + " has no needs the game shows.");
			}
			List<string> lines = new List<string>();
			Need_Mood mood = pawn.needs.mood;
			if (mood != null)
			{
				string head = pawn.LabelShort + "'s mood is " + Percent(mood.CurLevelPercentage) + " (" + PlainText(mood.MoodString) + ").";
				if (pawn.MentalState != null)
				{
					head += " In a mental break: " + PlainText(pawn.MentalState.InspectLine) + ".";
				}
				lines.Add(head);
				if (mood.thoughts != null)
				{
					List<Thought> groups = new List<Thought>();
					mood.thoughts.GetDistinctMoodThoughtGroups(groups);
					List<KeyValuePair<string, float>> offsets = new List<KeyValuePair<string, float>>();
					foreach (Thought group in groups)
					{
						float offset = mood.thoughts.MoodOffsetOfGroup(group);
						if (Math.Abs(offset) >= 0.5f)
						{
							offsets.Add(new KeyValuePair<string, float>(PlainText(group.LabelCap), offset));
						}
					}
					List<string> top = offsets.OrderByDescending((KeyValuePair<string, float> kv) => Math.Abs(kv.Value)).Take(6)
						.Select((KeyValuePair<string, float> kv) => kv.Key + " " + (kv.Value > 0 ? "+" : "") + kv.Value.ToString("0", CultureInfo.InvariantCulture)).ToList();
					if (top.Count > 0)
					{
						lines.Add("Biggest thoughts: " + string.Join(", ", top.ToArray()) + ".");
					}
				}
			}
			List<string> needs = new List<string>();
			foreach (Need need in pawn.needs.AllNeeds)
			{
				if (need.ShowOnNeedList && need != mood)
				{
					needs.Add(need.LabelCap + " " + Percent(need.CurLevelPercentage));
				}
			}
			if (needs.Count > 0)
			{
				lines.Add("Needs: " + string.Join(", ", needs.ToArray()) + ".");
			}
			return string.Join("\n", lines.ToArray());
		}

		// ---- health -----------------------------------------------------------------------------------------------------

		// The PAWN slot read from every pawn in view (QueryHealth, below; also RbPawnSkills, user 2026-10-16: "THE MOD BLOCKS
		// SKILL SHOWING FOR SOMEONE NOT COLONIST EVEN WHEN AGENT QUERIES", after "show skills of Zojerath." was refused for a
		// downed hostile). KEEP, when given, leaves out pawns it does not take.
		internal static Pawn PawnInView(FormContext c, Func<Pawn, bool> keep)
		{
			Map map = c.map;
			List<Pawn> inView = map.mapPawns.AllPawns.Where((Pawn p) => p != null && !p.Dead && AgentPlace.Visible(map, p.PositionHeld) && (keep == null || keep(p))).ToList();
			Pawn target;
			string ignored;
			string said = c.Text("PAWN") ?? "";
			if (c.req.themPawns == null && !inView.Any((Pawn p) => PawnNameMatches(p, Norm(said))) && AgentTargets.TryFind(map, said, out target, out ignored) && target != null
				&& (keep == null || keep(target)))
			{
				return target;
			}
			return ResolveOneFrom(c, "PAWN", inView, "pawn in view");
		}

		// Every pawn in view, not only the colony's (user, 2026-10-16, after "show health of Igor." was refused for a guest:
		// "it's a non faction member. EVERY pawn should have show health of [x] available."): guests, visitors, traders,
		// downed strangers, wild animals and enemies, by name or kind as PawnNameMatches reads them, and an enemy also by
		// its "hostiles" name ("raider 1", AgentTargets). In view only (AgentPlace.Visible at PositionHeld: a pawn carried
		// or in a bed counts too).
		private static string QueryHealth(FormContext c)
		{
			Pawn pawn = PawnInView(c, null);
			if (pawn.health == null)
			{
				throw new Fault(pawn.LabelShort + " has no health in the game.");
			}
			List<string> lines = new List<string>();
			lines.Add(pawn.LabelShort + "'s health is " + Percent(pawn.health.summaryHealth.SummaryHealthPercent) + ", pain " + Percent(pawn.health.hediffSet.PainTotal) + ".");

			List<string> missing = pawn.health.hediffSet.GetMissingPartsCommonAncestors().Select((Hediff_MissingPart h) => PlainText(h.Part.Label)).ToList();
			if (missing.Count > 0)
			{
				lines.Add("Missing: " + CapList(missing, 12) + ".");
			}
			List<string> implants = new List<string>();
			List<string> injuries = new List<string>();
			List<string> sicknesses = new List<string>();
			foreach (Hediff h in pawn.health.hediffSet.hediffs)
			{
				if (h is Hediff_MissingPart || !h.Visible)
				{
					continue;
				}
				string where = h.Part != null ? " (" + PlainText(h.Part.Label) + ")" : "";
				if (h.def.countsAsAddedPartOrImplant)
				{
					implants.Add(PlainText(h.LabelCap) + where);
				}
				else if (h is Hediff_Injury)
				{
					string state = h.IsPermanent() ? ", permanent" : h.Bleeding ? ", bleeding" : "";
					state += h.IsTended() ? ", tended" : "";
					injuries.Add(PlainText(h.LabelCap) + where + state);
				}
				else
				{
					string state = h.TendableNow() ? ", needs tending" : h.IsTended() ? ", tended" : "";
					sicknesses.Add(PlainText(h.LabelCap) + where + (h.Severity > 0f && h.def.lethalSeverity > 0f ? " " + Percent(h.Severity / h.def.lethalSeverity) + " of lethal" : "") + state);
				}
			}
			if (sicknesses.Count > 0)
			{
				lines.Add("Illnesses and conditions: " + CapList(sicknesses, 12) + ".");
			}
			if (injuries.Count > 0)
			{
				lines.Add("Injuries: " + CapList(injuries, 12) + ".");
			}
			if (implants.Count > 0)
			{
				lines.Add("Implants and added parts: " + CapList(implants, 12) + ".");
			}
			if (missing.Count == 0 && injuries.Count == 0 && sicknesses.Count == 0 && implants.Count == 0)
			{
				lines.Add("No injuries, illnesses, missing parts or implants.");
			}
			float bleed = pawn.health.hediffSet.BleedRateTotal;
			if (bleed > 0.01f)
			{
				lines.Add("Bleeding " + Percent(bleed) + " of blood per day.");
			}
			List<string> capacities = new List<string>();
			IEnumerable<PawnCapacityDef> defs = pawn.def.race.Humanlike ? DefDatabase<PawnCapacityDef>.AllDefs.Where((PawnCapacityDef x) => x.showOnHumanlikes)
				: pawn.def.race.Animal ? DefDatabase<PawnCapacityDef>.AllDefs.Where((PawnCapacityDef x) => x.showOnAnimals)
				: DefDatabase<PawnCapacityDef>.AllDefs.Where((PawnCapacityDef x) => x.showOnMechanoids);
			foreach (PawnCapacityDef def in defs.OrderBy((PawnCapacityDef d) => d.listOrder))
			{
				capacities.Add(PlainText(def.GetLabelFor(pawn)) + " " + PlainText(HealthCardUtility.GetEfficiencyLabel(pawn, def).First));
			}
			if (capacities.Count > 0)
			{
				lines.Add("Capacities: " + string.Join(", ", capacities.ToArray()) + ".");
			}
			if (pawn.BillStack != null)
			{
				List<string> queued = pawn.BillStack.Bills.OfType<Bill_Medical>().Select((Bill_Medical b) => MakeSurgeryOption(pawn, b.recipe, b.Part, null).full).ToList();
				lines.Add(queued.Count > 0 ? "Operations queued: " + CapList(queued, 10) + "." : "No operations queued.");
			}
			return string.Join("\n", lines.ToArray());
		}

		// ---- research ---------------------------------------------------------------------------------------------------

		private static string QueryResearch()
		{
			List<string> lines = new List<string>();
			ResearchManager rm = Find.ResearchManager;
			List<ResearchProjectDef> current = new List<ResearchProjectDef>();
			ResearchProjectDef main = rm.GetProject();
			if (main != null)
			{
				current.Add(main);
			}
			if (ModsConfig.AnomalyActive)
			{
				foreach (ResearchProjectDef p in DefDatabase<ResearchProjectDef>.AllDefsListForReading)
				{
					if (p.knowledgeCategory != null && rm.IsCurrentProject(p) && !current.Contains(p))
					{
						current.Add(p);
					}
				}
			}
			if (current.Count == 0)
			{
				lines.Add("No research project is set.");
			}
			foreach (ResearchProjectDef p in current)
			{
				lines.Add("Researching " + p.label + ": " + p.ProgressApparentString + " of " + p.CostApparent.ToString("F0", CultureInfo.InvariantCulture) + " (" + Percent(p.ProgressPercent) + ").");
				// What it gives when done (AgentReadbackBuildings.RbResearchGives).
				List<string> gives = RbResearchGives(p);
				if (gives.Count > 0)
				{
					lines.Add("  " + string.Join("; ", gives.ToArray()) + ".");
				}
			}
			List<string> available = DefDatabase<ResearchProjectDef>.AllDefsListForReading
				.Where((ResearchProjectDef p) => !p.label.NullOrEmpty() && p.CanStartNow && !current.Contains(p)).Select((ResearchProjectDef p) => p.label).ToList();
			lines.Add(available.Count > 0 ? "Available to start: " + CapList(available, 30) + "." : "No other project can be started right now.");
			return string.Join("\n", lines.ToArray());
		}

		// ---- weather ----------------------------------------------------------------------------------------------------

		private static string QueryWeather(Map map)
		{
			Vector2 location = Find.WorldGrid.LongLatOf(map.Tile);
			List<string> lines = new List<string>();
			lines.Add(GenDate.DateFullStringAt(Find.TickManager.TicksAbs, location) + ", " + GenLocalDate.HourOfDay(map).ToString("00", CultureInfo.InvariantCulture) + ":00, "
				+ GenLocalDate.Season(map).Label() + ".");
			lines.Add("Outdoor temperature " + map.mapTemperature.OutdoorTemp.ToStringTemperature("F0") + ".");
			WeatherDef weather = map.weatherManager.CurWeatherPerceived;
			lines.Add("Weather: " + (weather != null ? PlainText(weather.LabelCap) : "unknown") + ".");
			List<string> conditions = new List<string>();
			foreach (GameCondition condition in map.gameConditionManager.ActiveConditions)
			{
				conditions.Add(PlainText(condition.LabelCap));
			}
			foreach (GameCondition condition in Find.World.gameConditionManager.ActiveConditions)
			{
				conditions.Add(PlainText(condition.LabelCap));
			}
			lines.Add(conditions.Count > 0 ? "Active conditions: " + CapList(conditions.Distinct(), 10) + "." : "No special conditions are active.");
			return string.Join("\n", lines.ToArray());
		}

		// ---- colonists, prisoners, animals ---------------------------------------------------------------------------

		private static string QueryColonists(Map map)
		{
			List<Pawn> colonists = map.mapPawns.FreeColonists.ToList();
			if (colonists.Count == 0)
			{
				return "There are no colonists.";
			}
			List<string> lines = new List<string>();
			lines.Add(colonists.Count + (colonists.Count == 1 ? " colonist:" : " colonists:"));
			foreach (Pawn p in colonists)
			{
				List<string> state = new List<string>();
				if (p.Dead)
				{
					state.Add("dead");
				}
				else
				{
					string job = p.jobs != null && p.jobs.curDriver != null ? PlainText(p.jobs.curDriver.GetReport()) : "";
					state.Add(job.Length > 0 ? job.TrimEnd('.').ToLowerInvariant() : "idle");
					if (p.needs != null && p.needs.mood != null)
					{
						state.Add("mood " + Percent(p.needs.mood.CurLevelPercentage));
					}
					if (p.Drafted)
					{
						state.Add("drafted");
					}
					if (p.Downed)
					{
						state.Add("downed");
					}
					if (p.MentalState != null)
					{
						state.Add("mental break: " + PlainText(p.MentalState.InspectLine));
					}
				}
				lines.Add("- " + p.LabelShort + ": " + string.Join(", ", state.ToArray()));
			}
			return string.Join("\n", lines.ToArray());
		}

		private static string QueryPrisoners(Map map)
		{
			List<Pawn> prisoners = map.mapPawns.PrisonersOfColony.ToList();
			if (prisoners.Count == 0)
			{
				return "There are no prisoners.";
			}
			List<string> lines = new List<string>();
			lines.Add(prisoners.Count + (prisoners.Count == 1 ? " prisoner:" : " prisoners:"));
			foreach (Pawn p in prisoners)
			{
				List<string> info = new List<string>();
				info.Add("mode " + (p.guest != null ? p.guest.ExclusiveInteractionMode.label : "unknown"));
				if (p.guest != null)
				{
					if (p.guest.Recruitable)
					{
						float resistance = p.guest.resistance > 0f ? Mathf.Max(0.1f, p.guest.resistance) : 0f;
						info.Add("recruitment resistance " + resistance.ToString("F1", CultureInfo.InvariantCulture));
					}
					else
					{
						info.Add("can't be recruited");
					}
					if (ModsConfig.IdeologyActive)
					{
						info.Add("will " + p.guest.will.ToString("F1", CultureInfo.InvariantCulture));
					}
				}
				if (p.needs != null && p.needs.mood != null)
				{
					info.Add("mood " + Percent(p.needs.mood.CurLevelPercentage));
				}
				lines.Add("- " + p.LabelShort + ": " + string.Join(", ", info.ToArray()));
			}
			return string.Join("\n", lines.ToArray());
		}

		private static string QueryAnimals(Map map)
		{
			List<Pawn> animals = map.mapPawns.SpawnedColonyAnimals.ToList();
			if (animals.Count == 0)
			{
				return "There are no tame animals.";
			}
			List<string> lines = new List<string>();
			lines.Add(animals.Count + (animals.Count == 1 ? " tame animal:" : " tame animals:"));
			foreach (Pawn p in animals)
			{
				List<string> info = new List<string>();
				if (p.kindDef != null && PlainText(p.kindDef.label) != p.LabelShort.ToLowerInvariant())
				{
					info.Add(p.kindDef.label);
				}
				if (p.playerSettings != null)
				{
					info.Add("master " + (p.playerSettings.Master != null ? p.playerSettings.Master.LabelShort : "nobody"));
					Area area = p.playerSettings.AreaRestrictionInPawnCurrentMap;
					info.Add("area " + (area != null ? area.Label : "anywhere"));
				}
				if (p.training != null && p.RaceProps.showTrainables)
				{
					List<string> learned = new List<string>();
					List<string> wanted = new List<string>();
					foreach (TrainableDef td in TrainableUtility.TrainableDefsInListOrder)
					{
						bool visible;
						p.training.CanAssignToTrain(td, out visible);
						if (!visible)
						{
							continue;
						}
						if (p.training.HasLearned(td))
						{
							learned.Add(td.label);
						}
						else if (p.training.GetWanted(td))
						{
							wanted.Add(td.label);
						}
					}
					info.Add(learned.Count > 0 ? "trained: " + string.Join(", ", learned.ToArray()) : "no training learned");
					if (wanted.Count > 0)
					{
						info.Add("training wanted: " + string.Join(", ", wanted.ToArray()));
					}
				}
				lines.Add("- " + p.LabelShort + ": " + string.Join("; ", info.ToArray()));
			}
			return string.Join("\n", lines.ToArray());
		}
	}
}
