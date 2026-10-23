using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Brief 27, wave 1 (contract 27): the change forms the player's own tabs, gizmos and dialogs have. Each one
	// does what that UI does:
	//   game speed          TickManager.CurTimeSpeed / Pause / TogglePaused (RimWorld\TimeControls.cs:67-80, 111-130)
	//   research start      ResearchManager.SetCurrentProject (MainTabWindow_Research.cs:696, DoBeginResearch); the
	//                       locked reasons are those of DrawStartButton (:508-570)
	//   fire at will        Pawn_DraftController.FireAtWill (Pawn_DraftController.cs:104, gizmo :197-212: drafted, ranged primary)
	//   bill repeat / pause when satisfied   Dialog_BillConfig.cs:126-137, 200-209; BillRepeatModeUtility.cs:8-20
	//   drug group entry    DrugPolicyEntry fields as Dialog_ManageDrugPolicies.DoEntryRow sets them (:176-208)
	//   policy new / rename / delete   Dialog_ManagePolicies.cs:132-157, 196-198; the databases' MakeNew... / TryDelete;
	//                       Dialog_RenamePolicy name rule (Verse\Dialog_RenamePolicy.cs:9-20, Dialog_Rename.cs:18, 59)
	//   letter choose       ChoiceLetter.Choices (DiaOption.action), what Dialog_NodeTree runs on a click (DiaOption.cs:75-95)
	// Pawn-care forms (prisoners, animals, operations) are in FormWave1Care.cs, the questions in FormWave1Queries.cs.
	public static partial class FormPlanner
	{
		private static PlanReply DispatchWave1(FormContext c)
		{
			switch (c.form)
			{
				case "game speed":
					return PlanGameSpeed(c);
				case "research start":
					return PlanResearchStart(c);
				case "bill repeat":
					return PlanBillRepeat(c);
				case "bill pause when satisfied":
					return PlanBillPauseWhenSatisfied(c, true);
				case "bill unpause when satisfied":
					return PlanBillPauseWhenSatisfied(c, false);
				case "fire at will on":
					return PlanFireAtWill(c, true);
				case "fire at will off":
					return PlanFireAtWill(c, false);
				case "prisoner mode":
					return PlanPrisonerMode(c);
				case "animal master":
					return PlanAnimalMaster(c);
				case "animal training":
					return PlanAnimalTraining(c);
				case "operation":
					return PlanOperation(c);
				case "operation cancel":
					return PlanOperationCancel(c);
				case "drug group entry":
					return PlanDrugGroupEntry(c);
				case "policy new":
				case "policy rename":
				case "policy delete":
					return PlanPolicyManage(c);
				case "letter choose":
					return PlanLetterChoose(c);
			}
			return null;
		}

		// ---- shared helpers --------------------------------------------------------------------------------------

		// A true/false slot that arrives as text ("true", "false", "on", "yes" ...). Null when it is neither.
		internal static bool? ParseBool(string text)
		{
			string n = Norm(text ?? "");
			switch (n)
			{
				case "true":
				case "yes":
				case "on":
				case "1":
				case "allow":
				case "enabled":
					return true;
				case "false":
				case "no":
				case "off":
				case "0":
				case "forbid":
				case "disabled":
					return false;
			}
			return null;
		}

		// Text with the game's colour / hyperlink tags taken out, commas out (vocabulary lines split at commas).
		internal static string PlainText(string text)
		{
			return string.IsNullOrEmpty(text) ? "" : Regex.Replace(text, "<.*?>", "").Replace(",", "").Trim();
		}

		internal static bool PawnNameMatches(Pawn p, string normalizedName)
		{
			if (Norm(p.LabelShort) == normalizedName)
			{
				return true;
			}
			NameTriple triple = p.Name as NameTriple;
			if (triple != null && (Norm(triple.Nick) == normalizedName || Norm(triple.First) == normalizedName))
			{
				return true;
			}
			if (p.Name != null && Norm(p.Name.ToStringFull) == normalizedName)
			{
				return true;
			}
			// An unnamed animal is its kind ("muffalo", "muffalos").
			if (p.kindDef != null && !p.kindDef.label.NullOrEmpty())
			{
				string kind = Norm(p.kindDef.label);
				if (kind == normalizedName || kind + "s" == normalizedName || kind + "es" == normalizedName)
				{
					return true;
				}
			}
			return false;
		}

		// PAWNS-like slot (names, "everyone", "them") out of a pool that is not only colonists.
		internal static List<Pawn> ResolvePawnsFrom(FormContext c, string key, IEnumerable<Pawn> pool, string noun)
		{
			List<Pawn> poolList = pool.ToList();
			List<Pawn> pawns = new List<Pawn>();
			if (c.req.themPawns != null)
			{
				foreach (object o in c.req.themPawns)
				{
					Pawn p = o as Pawn;
					if (p != null && !pawns.Contains(p))
					{
						pawns.Add(p);
					}
				}
			}
			foreach (string name in c.TextList(key))
			{
				string n = Norm(name);
				if (n == "everyone" || n == "everybody" || n == "all")
				{
					foreach (Pawn p in poolList)
					{
						if (!pawns.Contains(p))
						{
							pawns.Add(p);
						}
					}
				}
				else if (n == "them" || n == "they")
				{
					if (c.req.themPawns == null)
					{
						throw new Fault("'" + name + "' has no pawns selected earlier in this order.");
					}
				}
				else
				{
					List<Pawn> found = poolList.Where((Pawn p) => PawnNameMatches(p, n)).ToList();
					if (found.Count == 0)
					{
						throw new Fault("There is no " + noun + " called '" + name + "'.");
					}
					foreach (Pawn p in found)
					{
						if (!pawns.Contains(p))
						{
							pawns.Add(p);
						}
					}
				}
			}
			if (pawns.Count == 0)
			{
				throw new Fault("The order named nobody.");
			}
			return pawns;
		}

		internal static Pawn ResolveOneFrom(FormContext c, string key, IEnumerable<Pawn> pool, string noun)
		{
			List<Pawn> pawns = ResolvePawnsFrom(c, key, pool, noun);
			if (pawns.Count != 1)
			{
				throw new Fault("That needs exactly one " + noun + ", but '" + string.Join(", ", c.TextList(key).ToArray()) + "' means " + pawns.Count
					+ (pawns.Count > 1 ? " (" + CapList(pawns.Select((Pawn p) => p.LabelShort + (p.kindDef != null ? " the " + p.kindDef.label : "")), 6) + ")" : "") + ".");
			}
			return pawns[0];
		}

		// Colonists, prisoners, slaves and colony animals: whoever has a health tab and a needs tab the player can open.
		internal static List<Pawn> PawnsOfColony(Map map)
		{
			List<Pawn> all = new List<Pawn>();
			all.AddRange(map.mapPawns.FreeColonists);
			foreach (Pawn p in map.mapPawns.PrisonersOfColony)
			{
				if (!all.Contains(p))
				{
					all.Add(p);
				}
			}
			foreach (Pawn p in map.mapPawns.SlavesOfColonySpawned)
			{
				if (!all.Contains(p))
				{
					all.Add(p);
				}
			}
			foreach (Pawn p in map.mapPawns.SpawnedColonyAnimals)
			{
				if (!all.Contains(p))
				{
					all.Add(p);
				}
			}
			return all;
		}

		// ---- game speed ------------------------------------------------------------------------------------------

		private static PlanReply PlanGameSpeed(FormContext c)
		{
			string text = Norm(c.Text("SPEED") ?? "");
			TickManager tm = Find.TickManager;
			if (tm == null)
			{
				throw new Fault("No game is currently loaded.");
			}
			Action change;
			string line;
			// An open hourly check holds the pause until every agent has voted (AgentHourlyCheck.cs, 2026-10-16): any speed
			// but a pause is refused, with the batch in the refusal.
			bool pauseWord = text == "paused" || text == "pause" || text == "stop" || text == "freeze";
			if (!pauseWord && AgentHourlyCheck.IsOpen)
			{
				throw new Fault(AgentHourlyCheck.RefusalText());
			}
			switch (text)
			{
				case "paused":
				case "pause":
				case "stop":
				case "freeze":
					change = delegate { tm.Pause(); };
					line = "Pause the game.";
					break;
				case "unpause":
				case "unpaused":
				case "resume":
				case "play":
					// What the pause key does when paused: back to the speed before the pause (TickManager.TogglePaused).
					change = delegate
					{
						if (tm.CurTimeSpeed == TimeSpeed.Paused)
						{
							tm.TogglePaused();
						}
					};
					line = "Unpause the game.";
					break;
				case "normal":
				case "slow":
				case "1":
				case "1x":
					change = delegate { tm.CurTimeSpeed = TimeSpeed.Normal; };
					line = "Set the game speed to normal.";
					break;
				case "fast":
				case "2":
				case "2x":
					change = delegate { tm.CurTimeSpeed = TimeSpeed.Fast; };
					line = "Set the game speed to fast.";
					break;
				case "superfast":
				case "super fast":
				case "fastest":
				case "3":
				case "3x":
					change = delegate { tm.CurTimeSpeed = TimeSpeed.Superfast; };
					line = "Set the game speed to superfast.";
					break;
				default:
					throw new Fault("'" + text + "' is not a game speed. Use paused, normal, fast or superfast.");
			}
			Plan plan = NewPlan("game speed");
			plan.notes.Add("This window pauses the game while it is open; the speed takes hold when it closes.");
			// A window that holds the game paused keeps a speed change from moving the game (Verse\TickManager.cs:142-156).
			string holdNote = AgentWindows.HoldNote();
			if (holdNote != null)
			{
				plan.notes.Add(holdNote);
			}
			Write write = new Write();
			write.description = line.TrimEnd('.');
			// A plan made before the check opened and applied after it: refused at apply too.
			write.apply = pauseWord ? change : (Action)delegate
			{
				if (AgentHourlyCheck.IsOpen)
				{
					throw new Fault(AgentHourlyCheck.RefusalText());
				}
				change();
			};
			plan.writes.Add(write);
			return Reply(plan, line, null, null);
		}

		// ---- research --------------------------------------------------------------------------------------------

		// The reasons the research tab gives for a locked project (MainTabWindow_Research.cs:540-569).
		private static List<string> ResearchLockedReasons(ResearchProjectDef project)
		{
			List<string> reasons = new List<string>();
			if (!project.PrerequisitesCompleted)
			{
				List<string> missing = new List<string>();
				if (project.prerequisites != null)
				{
					missing.AddRange(project.prerequisites.Where((ResearchProjectDef p) => !p.IsFinished).Select((ResearchProjectDef p) => p.label));
				}
				if (project.hiddenPrerequisites != null)
				{
					missing.AddRange(project.hiddenPrerequisites.Where((ResearchProjectDef p) => !p.IsFinished).Select((ResearchProjectDef p) => p.label));
				}
				reasons.Add("it needs " + (missing.Count > 0 ? JoinNames(missing) : "other research") + " first");
			}
			if (!project.TechprintRequirementMet)
			{
				reasons.Add("it needs " + project.TechprintCount + " techprints applied (" + project.TechprintsApplied + " so far)");
			}
			if (project.requiredResearchBuilding != null && !project.PlayerHasAnyAppropriateResearchBench)
			{
				reasons.Add("it needs a " + project.requiredResearchBuilding.label + " (and its facilities)");
			}
			if (!project.PlayerMechanitorRequirementMet)
			{
				reasons.Add("it needs a mechanitor in the colony");
			}
			if (!project.InspectionRequirementsMet)
			{
				reasons.Add("the grav engine has to be inspected first");
			}
			if (!project.AnalyzedThingsRequirementsMet)
			{
				reasons.Add("something it needs has not been studied yet");
			}
			return reasons;
		}

		private static PlanReply PlanResearchStart(FormContext c)
		{
			string text = c.Text("PROJECT") ?? "";
			string n = Norm(text);
			if (n.Length == 0)
			{
				throw new Fault("The order did not say which research project.");
			}
			List<ResearchProjectDef> all = DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where((ResearchProjectDef p) => !p.label.NullOrEmpty() && !p.IsHidden).ToList();
			List<ResearchProjectDef> found = all.Where((ResearchProjectDef p) => Norm(p.label) == n).ToList();
			if (found.Count == 0)
			{
				string[] words = n.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
				found = all.Where((ResearchProjectDef p) => words.Length > 0 && words.All((string w) => ContainsWord(Norm(p.label), w))).ToList();
			}
			if (found.Count == 0)
			{
				throw new Fault("There is no research project called '" + text + "'.");
			}
			if (found.Count > 1)
			{
				throw new Fault("'" + text + "' could mean several research projects: " + CapList(found.Select((ResearchProjectDef p) => p.label), 8) + ".");
			}
			ResearchProjectDef project = found[0];
			if (project.IsFinished)
			{
				throw new Fault(UpperFirst(project.label) + " is already researched.");
			}
			Plan plan = NewPlan("research start");
			if (Find.ResearchManager.IsCurrentProject(project))
			{
				plan.notes.Add(UpperFirst(project.label) + " is already the current research project.");
				return Reply(plan, UpperFirst(project.label) + " is already being researched.", null, null);
			}
			if (!project.CanStartNow)
			{
				List<string> reasons = ResearchLockedReasons(project);
				throw new Fault(UpperFirst(project.label) + " can't be started yet" + (reasons.Count > 0 ? ": " + string.Join("; ", reasons.ToArray()) : "") + ".");
			}
			ResearchProjectDef current = Find.ResearchManager.GetProject();
			if (current != null && !current.IsFinished)
			{
				plan.notes.Add("This replaces the current project, " + current.label + " (its progress is kept).");
			}
			Write write = new Write();
			write.description = "research " + project.label;
			write.apply = delegate
			{
				// DoBeginResearch's call (MainTabWindow_Research.cs:696).
				Find.ResearchManager.SetCurrentProject(project);
			};
			plan.writes.Add(write);
			return Reply(plan, "Research " + project.label + ".", null, null);
		}

		// ---- fire at will ----------------------------------------------------------------------------------------

		private static PlanReply PlanFireAtWill(FormContext c, bool on)
		{
			List<Pawn> pawns = ResolvePawns(c, "PAWNS");
			Plan plan = NewPlan(c.form);
			List<Pawn> done = new List<Pawn>();
			foreach (Pawn pawn in pawns)
			{
				if (pawn.drafter == null)
				{
					plan.notes.Add(pawn.LabelShort + " can't be drafted, so was left out.");
					continue;
				}
				if (!pawn.Drafted && !c.notes.plannedDrafts.Contains(pawn))
				{
					plan.notes.Add(pawn.LabelShort + " is not drafted, so was left out (the toggle only shows for drafted pawns).");
					continue;
				}
				if (pawn.equipment == null || pawn.equipment.Primary == null || !pawn.equipment.Primary.def.IsRangedWeapon)
				{
					plan.notes.Add(pawn.LabelShort + " has no ranged weapon, so was left out (the toggle only shows with one).");
					continue;
				}
				done.Add(pawn);
				Write write = new Write();
				write.description = pawn.LabelShort + ": fire at will " + (on ? "on" : "off");
				Pawn capturedPawn = pawn;
				write.apply = delegate { capturedPawn.drafter.FireAtWill = on; };
				plan.writes.Add(write);
			}
			if (done.Count == 0)
			{
				throw new Fault("None of them is drafted with a ranged weapon.");
			}
			return Reply(plan, (on ? "Fire at will on for " : "Hold fire (fire at will off) for ") + JoinNames(Names(done)) + ".", done, null);
		}

		// ---- bills: repeat mode, pause when satisfied --------------------------------------------------------------

		// The bill the sentence means and a check that it is still there when the change is applied.
		private static Bill_Production ProductionBillFor(FormContext c, out Building_WorkTable bench, out string recipeLabel, out string where, out Action check)
		{
			bench = ResolveOneBench(c);
			string benchLabel = c.Text("BENCH");
			string recipeText = c.Text("RECIPE");
			if (recipeText == null)
			{
				throw new Fault("The order did not say which recipe.");
			}
			where = BenchPhrase(c, bench);
			bool planned;
			Bill bill = FindBill(c, bench, recipeText, benchLabel, out planned);
			Bill_Production production = bill as Bill_Production;
			RequireProduction(production, bill.recipe);
			recipeLabel = bill.recipe.label;
			Building_WorkTable capturedBench = bench;
			bool capturedPlanned = planned;
			check = delegate
			{
				if (!capturedPlanned && (production.deleted || !capturedBench.billStack.Bills.Contains(production)))
				{
					throw new Fault("The bill for " + production.recipe.label + " is gone, so it was not changed.");
				}
			};
			return production;
		}

		private static PlanReply PlanBillRepeat(FormContext c)
		{
			Building_WorkTable bench;
			string recipeLabel;
			string where;
			Action check;
			Bill_Production bill = ProductionBillFor(c, out bench, out recipeLabel, out where, out check);
			string mode = Norm(c.Text("MODE") ?? "");
			double? count = c.Number("COUNT");
			Plan plan = NewPlan("bill repeat");
			Action change;
			string line;
			switch (mode)
			{
				case "count":
				case "times":
				case "repeat":
					if (count == null || count < 1 || count != Math.Floor(count.Value))
					{
						throw new Fault("The number of times must be a whole number, 1 or more.");
					}
					int times = (int)count.Value;
					change = delegate
					{
						bill.repeatMode = BillRepeatModeDefOf.RepeatCount;
						bill.repeatCount = times;
					};
					line = "Make " + recipeLabel + " " + times + (times == 1 ? " time" : " times") + " (bill on " + where + ").";
					break;
				case "until":
				case "target":
				case "until you have":
					if (!bill.recipe.WorkerCounter.CanCountProducts(bill))
					{
						throw new Fault("The game can't count what " + bill.recipe.label + " makes, so a bill can't make it until you have some number of it.");
					}
					if (count != null && (count < 1 || count != Math.Floor(count.Value)))
					{
						throw new Fault("The number to have must be a whole number, 1 or more.");
					}
					int target = count != null ? (int)count.Value : -1;
					change = delegate
					{
						bill.repeatMode = BillRepeatModeDefOf.TargetCount;
						if (target > 0)
						{
							// Dialog_BillConfig.cs:160: the unpause number moves with the target.
							int before = bill.targetCount;
							bill.targetCount = target;
							bill.unpauseWhenYouHave = Math.Max(0, bill.unpauseWhenYouHave + (target - before));
							if (bill.pauseWhenSatisfied && bill.unpauseWhenYouHave >= bill.targetCount)
							{
								bill.unpauseWhenYouHave = Math.Max(0, bill.targetCount - 1);
							}
						}
					};
					line = "Make " + recipeLabel + " until you have " + (target > 0 ? target.ToString() : "the number already set (" + bill.targetCount + ")") + " (bill on " + where + ").";
					break;
				case "forever":
				case "always":
					change = delegate { bill.repeatMode = BillRepeatModeDefOf.Forever; };
					line = "Make " + recipeLabel + " forever (bill on " + where + ").";
					break;
				default:
					throw new Fault("'" + mode + "' is not a repeat mode. Use count, until or forever.");
			}
			Write write = new Write();
			write.description = line.TrimEnd('.');
			write.apply = delegate
			{
				check();
				change();
			};
			plan.writes.Add(write);
			return Reply(plan, line, null, null);
		}

		private static PlanReply PlanBillPauseWhenSatisfied(FormContext c, bool pause)
		{
			Building_WorkTable bench;
			string recipeLabel;
			string where;
			Action check;
			Bill_Production bill = ProductionBillFor(c, out bench, out recipeLabel, out where, out check);
			Plan plan = NewPlan(c.form);
			if (bill.repeatMode != BillRepeatModeDefOf.TargetCount)
			{
				plan.notes.Add("The bill for " + recipeLabel + " is not set to 'until you have' right now; pause when satisfied only matters in that mode.");
			}
			Write write = new Write();
			string line = (pause ? "Pause" : "Don't pause") + " the bill for " + recipeLabel + " on " + where + " when it is satisfied.";
			write.description = line.TrimEnd('.');
			write.apply = delegate
			{
				check();
				bill.pauseWhenSatisfied = pause;
				if (pause && bill.unpauseWhenYouHave >= bill.targetCount)
				{
					bill.unpauseWhenYouHave = Math.Max(0, bill.targetCount - 1);
				}
			};
			plan.writes.Add(write);
			return Reply(plan, line, null, null);
		}

		// ---- drug group entry --------------------------------------------------------------------------------------

		private static PlanReply PlanDrugGroupEntry(FormContext c)
		{
			DrugPolicy policy = (DrugPolicy)FindPolicy(Current.Game.drugPolicyDatabase.AllPolicies.Cast<Policy>().ToList(), c.Text("NAME") ?? "", "drug");
			string drugText = c.Text("DRUG") ?? "";
			string dn = Norm(drugText);
			List<DrugPolicyEntry> entries = new List<DrugPolicyEntry>();
			for (int i = 0; i < policy.Count; i++)
			{
				if (policy[i].drug != null)
				{
					entries.Add(policy[i]);
				}
			}
			List<DrugPolicyEntry> found = entries.Where((DrugPolicyEntry e) => LabelMatchesWord(Norm(e.drug.label), dn)).ToList();
			if (found.Count == 0)
			{
				found = entries.Where((DrugPolicyEntry e) => ContainsWord(Norm(e.drug.label), dn) || ContainsWord(dn, Norm(e.drug.label))).ToList();
			}
			if (found.Count == 0)
			{
				throw new Fault("The drug group '" + policy.label + "' has no drug called '" + drugText + "'. Its drugs are " + CapList(entries.Select((DrugPolicyEntry e) => e.drug.label), 20) + ".");
			}
			if (found.Count > 1)
			{
				throw new Fault("'" + drugText + "' could mean several drugs: " + CapList(found.Select((DrugPolicyEntry e) => e.drug.label), 8) + ".");
			}
			DrugPolicyEntry entry = found[0];
			string drug = entry.drug.label;
			string field = Norm(c.Text("FIELD") ?? "");
			Plan plan = NewPlan("drug group entry");
			Action change;
			string what;
			switch (field)
			{
				case "allow for joy":
				case "allow for recreation":
				{
					if (!entry.drug.IsPleasureDrug)
					{
						throw new Fault(UpperFirst(drug) + " is not a recreational drug, so it has no 'allow for joy' setting.");
					}
					bool value = RequireBool(c);
					change = delegate { entry.allowedForJoy = value; };
					what = (value ? "allow " : "don't allow ") + drug + " for joy";
					break;
				}
				case "allow scheduled":
				{
					bool value = RequireBool(c);
					change = delegate { entry.allowScheduled = value; };
					what = (value ? "take " : "don't take ") + drug + " on a schedule";
					break;
				}
				case "take every":
				{
					double? days = c.Number("VALUE");
					if (days == null || days < 0.1 || days > 25)
					{
						throw new Fault("'Take every' is a number of days from 0.1 to 25.");
					}
					float d = (float)days.Value;
					change = delegate { entry.daysFrequency = d; };
					what = "take " + drug + " every " + d.ToString("0.##", CultureInfo.InvariantCulture) + (d == 1f ? " day" : " days");
					break;
				}
				case "only if mood below":
				{
					float v = RequirePercent(c, "mood");
					change = delegate { entry.onlyIfMoodBelow = v; };
					what = v >= 1f ? "take " + drug + " whatever the mood" : "take " + drug + " only if mood is below " + Percent(v);
					break;
				}
				case "only if recreation below":
				case "only if joy below":
				{
					float v = RequirePercent(c, "recreation");
					change = delegate { entry.onlyIfJoyBelow = v; };
					what = v >= 1f ? "take " + drug + " whatever the recreation level" : "take " + drug + " only if recreation is below " + Percent(v);
					break;
				}
				case "take to inventory":
				{
					double? n = c.Number("VALUE");
					int max = PawnUtility.GetMaxAllowedToPickUp(entry.drug, null);
					if (n == null || n < 0 || n != Math.Floor(n.Value) || n > max)
					{
						throw new Fault("'Take to inventory' is a whole number from 0 to " + max + " for " + drug + ".");
					}
					int count = (int)n.Value;
					change = delegate
					{
						entry.takeToInventory = count;
						entry.takeToInventoryTempBuffer = null;
					};
					what = "carry " + count + " " + drug;
					break;
				}
				default:
					throw new Fault("'" + field + "' is not a drug group setting. Use allow for joy, allow scheduled, take every, only if mood below, only if recreation below or take to inventory.");
			}
			if (!entry.allowScheduled && (field == "take every" || field == "only if mood below" || field == "only if recreation below" || field == "only if joy below"))
			{
				plan.notes.Add("'Allow scheduled' is off for " + drug + " in this group, so this only matters once it is turned on.");
			}
			Write write = new Write();
			write.description = "drug group '" + policy.label + "': " + what;
			write.apply = change;
			plan.writes.Add(write);
			return Reply(plan, "In the drug group '" + policy.label + "': " + what + ".", null, null);
		}

		private static bool RequireBool(FormContext c)
		{
			bool? v = ParseBool(c.Text("VALUE"));
			if (v == null)
			{
				throw new Fault("The value has to be on or off.");
			}
			return v.Value;
		}

		// 1 to 100 percent as the slider's 0.01 to 1 (100 percent = no requirement).
		private static float RequirePercent(FormContext c, string what)
		{
			double? v = c.Number("VALUE");
			if (v == null || v < 1 || v > 100)
			{
				throw new Fault("The " + what + " limit is a percent from 1 to 100 (100 means no limit).");
			}
			return (float)(v.Value / 100.0);
		}

		// ---- policies: new, rename, delete -------------------------------------------------------------------------

		private sealed class PolicyKindInfo
		{
			public string kind;
			public List<Policy> all;
			public Func<Policy> make;
			public Func<Policy, AcceptanceReport> tryDelete;
			public Func<Pawn, Policy> of;
		}

		private static PolicyKindInfo PolicyKindFor(string kindText)
		{
			PolicyKindInfo info = new PolicyKindInfo();
			switch (Norm(kindText))
			{
				case "clothing":
				case "apparel":
				case "outfit":
					info.kind = "clothing";
					info.all = Current.Game.outfitDatabase.AllOutfits.Cast<Policy>().ToList();
					info.make = () => Current.Game.outfitDatabase.MakeNewOutfit();
					info.tryDelete = (Policy p) => Current.Game.outfitDatabase.TryDelete((ApparelPolicy)p);
					info.of = (Pawn p) => ApparelPolicyOf(p);
					break;
				case "food":
					info.kind = "food";
					info.all = Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Cast<Policy>().ToList();
					info.make = () => Current.Game.foodRestrictionDatabase.MakeNewFoodRestriction();
					info.tryDelete = (Policy p) => Current.Game.foodRestrictionDatabase.TryDelete((FoodPolicy)p);
					info.of = (Pawn p) => FoodPolicyOf(p);
					break;
				case "drug":
				case "drugs":
					info.kind = "drug";
					info.all = Current.Game.drugPolicyDatabase.AllPolicies.Cast<Policy>().ToList();
					info.make = () => Current.Game.drugPolicyDatabase.MakeNewDrugPolicy();
					info.tryDelete = (Policy p) => Current.Game.drugPolicyDatabase.TryDelete((DrugPolicy)p);
					info.of = (Pawn p) => DrugPolicyOf(p);
					break;
				case "reading":
					info.kind = "reading";
					info.all = Current.Game.readingPolicyDatabase.AllReadingPolicies.Cast<Policy>().ToList();
					info.make = () => Current.Game.readingPolicyDatabase.MakeNewReadingPolicy();
					info.tryDelete = (Policy p) => Current.Game.readingPolicyDatabase.TryDelete((ReadingPolicy)p);
					info.of = (Pawn p) => ReadingPolicyOf(p);
					break;
				default:
					throw new Fault("'" + kindText + "' is not a kind of group. The kinds are clothing, food, drug and reading.");
			}
			return info;
		}

		// Dialog_RenamePolicy: not empty, under 28 characters, letters, digits, spaces, ' and -.
		private static string CheckPolicyName(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
			{
				return "The order did not give a name.";
			}
			if (name.Length >= 28)
			{
				return "The game allows group names of at most 27 characters.";
			}
			if (!Regex.IsMatch(name, "^[\\p{L}0-9 '\\-]*$"))
			{
				return "The game allows only letters, digits, spaces, apostrophes and hyphens in a group name.";
			}
			return null;
		}

		private static PlanReply PlanPolicyManage(FormContext c)
		{
			PolicyKindInfo info = PolicyKindFor(c.Text("KIND") ?? "");
			string name = c.Text("NAME") ?? "";
			Plan plan = NewPlan(c.form);
			Write write = new Write();
			string line;
			if (c.form == "policy new")
			{
				string bad = CheckPolicyName(name);
				if (bad != null)
				{
					throw new Fault(bad);
				}
				if (info.all.Any((Policy p) => Norm(p.label) == Norm(name)))
				{
					throw new Fault("There is already a " + info.kind + " group called '" + name + "'.");
				}
				write.description = "new " + info.kind + " group '" + name + "'";
				write.apply = delegate
				{
					// Dialog_ManagePolicies.cs:196-198 (New policy), then the rename.
					Policy made = info.make();
					made.label = name;
				};
				line = "Make a new " + info.kind + " group called '" + name + "'.";
			}
			else if (c.form == "policy rename")
			{
				Policy policy = FindPolicy(info.all, name, info.kind);
				string newName = c.Text("NEW_NAME") ?? c.Text("NEW NAME") ?? "";
				string bad = CheckPolicyName(newName);
				if (bad != null)
				{
					throw new Fault(bad);
				}
				if (info.all.Any((Policy p) => p != policy && Norm(p.label) == Norm(newName)))
				{
					throw new Fault("There is already a " + info.kind + " group called '" + newName + "'.");
				}
				write.description = "rename " + info.kind + " group '" + policy.label + "' to '" + newName + "'";
				write.apply = delegate { policy.label = newName; };
				line = "Rename the " + info.kind + " group '" + policy.label + "' to '" + newName + "'.";
			}
			else
			{
				Policy policy = FindPolicy(info.all, name, info.kind);
				// The game's rule (the databases' TryDelete): refused while a pawn uses it.
				Pawn user = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive.FirstOrDefault((Pawn p) => info.of(p) == policy);
				if (user != null)
				{
					throw new Fault("The " + info.kind + " group '" + policy.label + "' can't be deleted: " + user.LabelShort + " still uses it. Give them another group first.");
				}
				write.description = "delete " + info.kind + " group '" + policy.label + "'";
				write.apply = delegate
				{
					AcceptanceReport report = info.tryDelete(policy);
					if (!report.Accepted)
					{
						throw new Fault(report.Reason.NullOrEmpty() ? "The group could not be deleted." : report.Reason);
					}
				};
				line = "RISKY: delete the " + info.kind + " group '" + policy.label + "'. It is gone for good.";
				if (info.all.IndexOf(policy) == 0)
				{
					plan.notes.Add("It is the default group; the next one becomes the default.");
				}
			}
			plan.writes.Add(write);
			return Reply(plan, line, null, null);
		}

		// ---- letters ------------------------------------------------------------------------------------------------

		// Choice letters that are in the letter stack now.
		internal static List<ChoiceLetter> OpenChoiceLetters()
		{
			return Find.LetterStack.LettersListForReading.OfType<ChoiceLetter>().Where((ChoiceLetter l) => l.CanShowInLetterStack).ToList();
		}

		internal static string LetterLabelOf(Letter letter)
		{
			return PlainText(letter.Label.Resolve());
		}

		internal sealed class LetterChoice
		{
			public DiaOption option;
			public string label;
		}

		// The options a player can click in the letter's dialog: not the camera jump, not the hyperlinks, not the ones
		// that do nothing (postpone).
		internal static List<LetterChoice> ChoicesOf(ChoiceLetter letter)
		{
			List<LetterChoice> result = new List<LetterChoice>();
			string jump = "JumpToLocation".Translate().ToString();
			foreach (DiaOption option in letter.Choices)
			{
				string label = PlainText(PrivateField(option, "text") as string);
				if (label.Length == 0 || option.hyperlink.def != null || label == PlainText(jump))
				{
					continue;
				}
				if (option.action == null && option.link == null && option.linkLateBind == null)
				{
					continue;
				}
				LetterChoice choice = new LetterChoice();
				choice.option = option;
				choice.label = label;
				result.Add(choice);
			}
			return result;
		}

		private static PlanReply PlanLetterChoose(FormContext c)
		{
			string letterText = c.Text("LETTER") ?? "";
			string optionText = c.Text("OPTION") ?? "";
			string ln = Norm(letterText);
			List<ChoiceLetter> letters = OpenChoiceLetters();
			List<ChoiceLetter> found = letters.Where((ChoiceLetter l) => Norm(LetterLabelOf(l)) == ln).ToList();
			if (found.Count == 0)
			{
				string[] words = ln.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
				found = letters.Where((ChoiceLetter l) => words.Length > 0 && words.All((string w) => ContainsWord(Norm(LetterLabelOf(l)), w))).ToList();
			}
			if (found.Count == 0)
			{
				throw new Fault("There is no open letter called '" + letterText + "'." + (letters.Count > 0 ? " The open letters are " + CapList(letters.Select((ChoiceLetter l) => LetterLabelOf(l)).Distinct(), 8) + "." : " No letter is waiting for an answer."));
			}
			if (found.Count > 1)
			{
				throw new Fault("More than one open letter fits '" + letterText + "': " + CapList(found.Select((ChoiceLetter l) => LetterLabelOf(l)), 6) + ". Answer one at a time, naming it more exactly.");
			}
			ChoiceLetter letter = found[0];
			List<LetterChoice> choices = ChoicesOf(letter);
			string on = Norm(optionText);
			List<LetterChoice> picked = choices.Where((LetterChoice ch) => Norm(ch.label) == on).ToList();
			if (picked.Count == 0)
			{
				picked = choices.Where((LetterChoice ch) => ContainsWord(Norm(ch.label), on)).ToList();
			}
			if (picked.Count == 0)
			{
				throw new Fault("The letter '" + LetterLabelOf(letter) + "' has no option '" + optionText + "'. Its options are " + JoinNames(choices.Select((LetterChoice ch) => ch.label)) + ".");
			}
			if (picked.Count > 1)
			{
				throw new Fault("'" + optionText + "' fits several options of '" + LetterLabelOf(letter) + "': " + JoinNames(picked.Select((LetterChoice ch) => ch.label)) + ".");
			}
			LetterChoice choice = picked[0];
			if (choice.option.disabled)
			{
				throw new Fault("'" + choice.label + "' can't be chosen for '" + LetterLabelOf(letter) + "' right now" + (string.IsNullOrEmpty(choice.option.disabledReason) ? "." : ": " + PlainText(choice.option.disabledReason) + "."));
			}
			string label = choice.label;
			string letterLabel = LetterLabelOf(letter);
			bool final = Regex.IsMatch(Norm(label), "reject|refuse|decline|betray|pay|capture|ransom");
			Plan plan = NewPlan("letter choose");
			Write write = new Write();
			write.description = "letter '" + letterLabel + "': " + label;
			write.apply = delegate
			{
				if (!Find.LetterStack.LettersListForReading.Contains(letter))
				{
					throw new Fault("The letter '" + letterLabel + "' is gone, so it was not answered.");
				}
				LetterChoice again = ChoicesOf(letter).FirstOrDefault((LetterChoice ch) => ch.label == label);
				if (again == null || again.option.disabled)
				{
					throw new Fault("'" + label + "' can no longer be chosen for '" + letterLabel + "'.");
				}
				// What a click in the letter's dialog runs (DiaOption.Activate): the action, then any follow-up node.
				if (again.option.action != null)
				{
					again.option.action();
				}
			};
			plan.writes.Add(write);
			string line = (final ? "RISKY: " : "") + "Answer the letter '" + letterLabel + "' with '" + label + "'." + (final ? " That can't be taken back." : "");
			return Reply(plan, line, null, null);
		}
	}
}
