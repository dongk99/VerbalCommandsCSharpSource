using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Brief 27, wave 1: pawn-care forms (contract 27), what the pawn's own tabs do.
	//   prisoner mode    ITab_Pawn_Visitor.DrawExclusiveInteractionRow / InteractionModeChanged (ITab_Pawn_Visitor.cs:371-418,
	//                    :437-444: Pawn_GuestTracker.SetExclusiveInteraction); which modes a pawn is offered is the
	//                    tab's local CanUsePrisonerInteractionMode (:407-440). Slaves: SlaveInteractionModeDef rows (:146-167).
	//   animal master    TrainableUtility.MasterSelectButton_GenerateMenu (TrainableUtility.cs:80-120): CanBeMaster, then
	//                    Pawn_PlayerSettings.Master; the card offers it only when Obedience is learned (TrainingCardUtility.cs:36)
	//   animal training  TrainingCardUtility.DoTrainableCheckbox (:160-180): Pawn_TrainingTracker.SetWantedRecursive
	//   operation        HealthCardUtility.DrawMedOperationsTab (:264-300) lists, GenerateSurgeryOption (:308-410) asks the
	//                    confirmations, CreateSurgeryBill (:405-440) queues; ITab_Pawn_Health.ShouldAllowOperations (:46-77)
	//   operation cancel BillStack.Delete (BillStack.cs:75-80)
	public static partial class FormPlanner
	{
		// ---- prisoners ----------------------------------------------------------------------------------------------

		private static string DlcFor(string modeWord)
		{
			switch (modeWord)
			{
				case "enslave":
				case "reduce will":
				case "reduce will only":
				case "convert":
					return ModsConfig.IdeologyActive ? null : "Ideology";
				case "bloodfeed":
				case "hemogen farm":
					return ModsConfig.BiotechActive ? null : "Biotech";
				case "study":
				case "interrogate":
					return ModsConfig.AnomalyActive ? null : "Anomaly";
			}
			return null;
		}

		// The words players use for the game's labels (maintain only, reduce resistance only, ...).
		private static string PrisonerModeAlias(string n)
		{
			switch (n)
			{
				case "no interaction":
				case "none":
				case "nothing":
				case "maintain":
					return "maintainonly";
				case "chat":
				case "reduce resistance":
				case "talk":
					return "reduceresistance";
				case "recruit":
				case "attempt recruit":
					return "attemptrecruit";
				case "free":
				case "let go":
					return "release";
				case "execute":
				case "kill":
					return "execution";
				case "reduce will":
					return "reducewill";
				case "hemogen":
					return "hemogenfarm";
			}
			return n.Replace(" ", "");
		}

		// Null when the tab would offer the mode for this pawn; else the reason it does not
		// (ITab_Pawn_Visitor.DoPrisonerTab, local CanUsePrisonerInteractionMode, :407-440).
		internal static string WhyPrisonerModeNotOffered(Pawn pawn, PrisonerInteractionModeDef mode)
		{
			if (!pawn.guest.Recruitable && mode.hideIfNotRecruitable)
			{
				return "can't be recruited";
			}
			if (pawn.IsWildMan() && !mode.allowOnWildMan)
			{
				return "is a wild man";
			}
			if (mode.hideOnHemogenicPawns && ModsConfig.BiotechActive && pawn.genes != null && pawn.genes.HasActiveGene(GeneDefOf.Hemogenic))
			{
				return "is hemogenic";
			}
			if (!mode.allowInClassicIdeoMode && Find.IdeoManager.classicMode)
			{
				return "isn't available in classic ideology mode";
			}
			if (ModsConfig.AnomalyActive)
			{
				bool studiable = false;
				CompStudiable comp;
				if (pawn.TryGetComp<CompStudiable>(out comp) && comp.EverStudiable() && pawn.kindDef.studiableAsPrisoner && !pawn.everLostEgo)
				{
					studiable = true;
				}
				if (mode.hideIfNotStudiableAsPrisoner && !studiable)
				{
					return "can't be studied";
				}
				if (mode.hideIfGrayFleshNotAppeared && !Find.Anomaly.hasSeenGrayFlesh)
				{
					return "isn't available yet";
				}
			}
			return null;
		}

		internal static List<PrisonerInteractionModeDef> ExclusivePrisonerModes()
		{
			return DefDatabase<PrisonerInteractionModeDef>.AllDefsListForReading.Where((PrisonerInteractionModeDef d) => !d.isNonExclusiveInteraction).OrderBy((PrisonerInteractionModeDef d) => d.listOrder).ToList();
		}

		private static PlanReply PlanPrisonerMode(FormContext c)
		{
			List<Pawn> pool = c.map.mapPawns.PrisonersOfColony.ToList();
			pool.AddRange(c.map.mapPawns.SlavesOfColonySpawned.Where((Pawn p) => !pool.Contains(p)));
			List<Pawn> pawns = ResolvePawnsFrom(c, "PAWNS", pool, "prisoner");
			string modeText = c.Text("MODE") ?? "";
			string n = Norm(modeText);
			if (n.Length == 0)
			{
				throw new Fault("The order did not say which interaction mode.");
			}
			string dlc = DlcFor(n);
			if (dlc != null)
			{
				throw new Fault("'" + modeText + "' needs the " + dlc + " expansion, which is not active.");
			}
			string alias = PrisonerModeAlias(n);

			Plan plan = NewPlan("prisoner mode");
			List<Pawn> done = new List<Pawn>();
			List<string> reasons = new List<string>();
			string chosenLabel = null;
			bool risky = false;
			string riskyText = null;
			foreach (Pawn pawn in pawns)
			{
				if (pawn.guest == null)
				{
					reasons.Add(pawn.LabelShort + " has no interaction setting.");
					continue;
				}
				Pawn capturedPawn = pawn;
				if (pawn.IsSlaveOfColony)
				{
					SlaveInteractionModeDef slaveMode = DefDatabase<SlaveInteractionModeDef>.AllDefsListForReading.FirstOrDefault((SlaveInteractionModeDef d) =>
						Norm(d.label) == n || Norm(d.defName) == alias || d.defName.ToLowerInvariant() == alias || Norm(d.label).Replace(" ", "") == alias);
					if (slaveMode == null)
					{
						reasons.Add(pawn.LabelShort + " is a slave; the slave modes are " + JoinNames(DefDatabase<SlaveInteractionModeDef>.AllDefsListForReading.OrderBy((SlaveInteractionModeDef d) => d.listOrder).Select((SlaveInteractionModeDef d) => d.label)) + ".");
						continue;
					}
					if (slaveMode == SlaveInteractionModeDefOf.Imprison && RestUtility.FindBedFor(pawn, pawn, checkSocialProperness: false, ignoreOtherReservations: false, GuestStatus.Prisoner) == null)
					{
						reasons.Add(pawn.LabelShort + " can't be imprisoned: there is no prisoner bed.");
						continue;
					}
					done.Add(pawn);
					chosenLabel = slaveMode.label;
					if (slaveMode == SlaveInteractionModeDefOf.Execute || slaveMode == SlaveInteractionModeDefOf.Emancipate)
					{
						risky = true;
						riskyText = slaveMode == SlaveInteractionModeDefOf.Execute ? "execute" : "emancipate";
					}
					Write slaveWrite = new Write();
					slaveWrite.description = pawn.LabelShort + ": " + slaveMode.label;
					slaveWrite.apply = delegate { capturedPawn.guest.slaveInteractionMode = slaveMode; };
					plan.writes.Add(slaveWrite);
					continue;
				}

				PrisonerInteractionModeDef mode = ExclusivePrisonerModes().FirstOrDefault((PrisonerInteractionModeDef d) =>
					Norm(d.label) == n || Norm(d.defName) == alias || d.defName.ToLowerInvariant() == alias || Norm(d.label).Replace(" ", "") == alias);
				if (mode == null)
				{
					if (DefDatabase<PrisonerInteractionModeDef>.AllDefsListForReading.Any((PrisonerInteractionModeDef d) => d.isNonExclusiveInteraction && (Norm(d.label) == n || d.defName.ToLowerInvariant() == alias)))
					{
						throw new Fault("'" + modeText + "' is a separate checkbox on the prisoner tab, not a mode; the mod does not set it yet.");
					}
					throw new Fault("'" + modeText + "' is not a prisoner mode. The modes are " + JoinNames(ExclusivePrisonerModes().Select((PrisonerInteractionModeDef d) => d.label)) + ".");
				}
				string why = WhyPrisonerModeNotOffered(pawn, mode);
				if (why != null)
				{
					reasons.Add(pawn.LabelShort + " " + why + ", so '" + mode.label + "' is not offered for them.");
					continue;
				}
				done.Add(pawn);
				chosenLabel = mode.label;
				if (mode == PrisonerInteractionModeDefOf.Execution || mode == PrisonerInteractionModeDefOf.Release)
				{
					risky = true;
					riskyText = mode == PrisonerInteractionModeDefOf.Execution ? "execute" : "release";
				}
				Write write = new Write();
				write.description = pawn.LabelShort + ": " + mode.label;
				write.apply = delegate
				{
					capturedPawn.guest.SetExclusiveInteraction(mode);
					// InteractionModeChanged (ITab_Pawn_Visitor.cs:437-444): the conversion target defaults to the primary ideo.
					if (mode == PrisonerInteractionModeDefOf.Convert && capturedPawn.guest.ideoForConversion == null && Faction.OfPlayer.ideos != null)
					{
						capturedPawn.guest.ideoForConversion = Faction.OfPlayer.ideos.PrimaryIdeo;
					}
				};
				plan.writes.Add(write);
			}
			if (done.Count == 0)
			{
				throw new Fault(string.Join(" ", reasons.ToArray()));
			}
			foreach (string reason in reasons)
			{
				plan.notes.Add(reason + " They were left out.");
			}
			string line = (risky ? "RISKY: " : "") + "Set " + JoinNames(Names(done)) + " to '" + chosenLabel + "'.";
			if (risky)
			{
				line += riskyText == "execute" ? " That means they will be executed." : riskyText == "release" ? " That means they will be let go." : " That frees them from slavery.";
			}
			return Reply(plan, line, done, null);
		}

		// ---- animals -------------------------------------------------------------------------------------------------

		private static List<Pawn> ResolveAnimals(FormContext c, string key)
		{
			List<Pawn> animals = ResolvePawnsFrom(c, key, c.map.mapPawns.SpawnedColonyAnimals, "tame animal");
			return animals;
		}

		private static PlanReply PlanAnimalMaster(FormContext c)
		{
			List<Pawn> animals = ResolveAnimals(c, "ANIMALS");
			string masterText = c.Text("PAWN") ?? "";
			string mn = Norm(masterText);
			Pawn master = null;
			bool clear = mn == "nobody" || mn == "no one" || mn == "none" || mn == "noone";
			if (!clear)
			{
				master = FindPawn(c.map, masterText);
				if (master == null)
				{
					throw new Fault("There is no colonist called '" + masterText + "'.");
				}
			}
			Plan plan = NewPlan("animal master");
			List<Pawn> done = new List<Pawn>();
			foreach (Pawn animal in animals)
			{
				if (animal.playerSettings == null || animal.training == null)
				{
					plan.notes.Add(animal.LabelShort + " can't have a master, so was left out.");
					continue;
				}
				if (!clear)
				{
					if (ModsConfig.IdeologyActive && !animal.RaceProps.playerCanChangeMaster)
					{
						plan.notes.Add(animal.LabelShort + " is bound to its own master and the game does not let you change it, so was left out.");
						continue;
					}
					if (!animal.training.HasLearned(TrainableDefOf.Obedience))
					{
						plan.notes.Add(animal.LabelShort + " has not learned obedience yet, so can't have a master; it was left out.");
						continue;
					}
					if (!TrainableUtility.CanBeMaster(master, animal))
					{
						int level = master.skills.GetSkill(SkillDefOf.Animals).Level;
						int needed = TrainableUtility.MinimumHandlingSkill(animal);
						plan.notes.Add(master.LabelShort + " can't be master of " + animal.LabelShort + (level < needed
							? ": animals skill " + level + " is below the " + needed + " it needs."
							: ".") + " It was left out.");
						continue;
					}
				}
				done.Add(animal);
				Write write = new Write();
				write.description = animal.LabelShort + ": master " + (clear ? "nobody" : master.LabelShort);
				Pawn capturedAnimal = animal;
				Pawn capturedMaster = master;
				write.apply = delegate { capturedAnimal.playerSettings.Master = capturedMaster; };
				plan.writes.Add(write);
			}
			if (done.Count == 0)
			{
				throw new Fault(plan.notes.Count > 0 ? string.Join(" ", plan.notes.ToArray()) : "None of them can have a master.");
			}
			return Reply(plan, clear
				? "Take the master away from " + JoinNames(Names(done)) + "."
				: "Make " + master.LabelShort + " the master of " + JoinNames(Names(done)) + ".", done, null);
		}

		private static TrainableDef FindTrainable(string text, IEnumerable<Pawn> animals)
		{
			string n = Norm(text);
			List<TrainableDef> all = DefDatabase<TrainableDef>.AllDefsListForReading.Where((TrainableDef d) => !d.label.NullOrEmpty()).ToList();
			List<TrainableDef> found = all.Where((TrainableDef d) => Norm(d.label) == n || Norm(d.defName) == n).ToList();
			if (found.Count == 0)
			{
				found = all.Where((TrainableDef d) => ContainsWord(Norm(d.label), n) || ContainsWord(n, Norm(d.label))).ToList();
			}
			if (found.Count == 1)
			{
				return found[0];
			}
			if (found.Count > 1)
			{
				throw new Fault("'" + text + "' could mean several trainings: " + JoinNames(found.Select((TrainableDef d) => d.label)) + ".");
			}
			throw new Fault("'" + text + "' is not a kind of training. The kinds are " + JoinNames(TrainingKinds(animals)) + ".");
		}

		// Trainings the card shows for at least one of these animals.
		internal static List<string> TrainingKinds(IEnumerable<Pawn> animals)
		{
			List<string> labels = new List<string>();
			foreach (TrainableDef td in TrainableUtility.TrainableDefsInListOrder)
			{
				foreach (Pawn animal in animals)
				{
					bool visible;
					if (animal.training != null && animal.RaceProps.showTrainables)
					{
						animal.training.CanAssignToTrain(td, out visible);
						if (visible)
						{
							labels.Add(td.label);
							break;
						}
					}
				}
			}
			return labels;
		}

		private static PlanReply PlanAnimalTraining(FormContext c)
		{
			List<Pawn> animals = ResolveAnimals(c, "ANIMALS");
			TrainableDef td = FindTrainable(c.Text("TRAINING") ?? "", c.map.mapPawns.SpawnedColonyAnimals);
			bool? onValue = ParseBool(c.Text("ON"));
			if (onValue == null)
			{
				throw new Fault("The order did not say whether to turn the training on or off.");
			}
			bool on = onValue.Value;
			Plan plan = NewPlan("animal training");
			List<Pawn> done = new List<Pawn>();
			foreach (Pawn animal in animals)
			{
				bool visible = false;
				AcceptanceReport can = false;
				if (animal.training != null && animal.RaceProps.showTrainables)
				{
					can = animal.training.CanAssignToTrain(td, out visible);
				}
				if (!visible)
				{
					plan.notes.Add(animal.LabelShort + " can't be trained in " + td.label + ", so was left out.");
					continue;
				}
				if (!can.Accepted)
				{
					plan.notes.Add(animal.LabelShort + " can't be trained in " + td.label + ": " + can.Reason + " It was left out.");
					continue;
				}
				done.Add(animal);
				Write write = new Write();
				write.description = animal.LabelShort + ": " + td.label + " training " + (on ? "on" : "off");
				Pawn capturedAnimal = animal;
				write.apply = delegate { capturedAnimal.training.SetWantedRecursive(td, on); };
				plan.writes.Add(write);
			}
			if (done.Count == 0)
			{
				throw new Fault(string.Join(" ", plan.notes.ToArray()));
			}
			if (on && !td.prerequisites.NullOrEmpty())
			{
				plan.notes.Add("It also turns on what it needs first: " + JoinNames(td.prerequisites.Select((TrainableDef p) => p.label)) + ".");
			}
			else if (!on)
			{
				List<TrainableDef> dependents = DefDatabase<TrainableDef>.AllDefsListForReading.Where((TrainableDef t) => t.prerequisites != null && t.prerequisites.Contains(td)).ToList();
				if (dependents.Count > 0)
				{
					plan.notes.Add("It also turns off what depends on it: " + JoinNames(dependents.Select((TrainableDef p) => p.label)) + ".");
				}
			}
			return Reply(plan, "Turn " + td.label + " training " + (on ? "on" : "off") + " for " + JoinNames(Names(done)) + ".", done, null);
		}

		// ---- operations ----------------------------------------------------------------------------------------------

		internal sealed class SurgeryOption
		{
			public RecipeDef recipe;
			public BodyPartRecord part;
			public string label;
			public string partLabel;
			public string full;
			// Null when the bill can be queued now; else why not (plain words).
			public string problem;
		}

		// ITab_Pawn_Health.ShouldAllowOperations (:46-77): null when the pawn can have operations queued.
		internal static string WhyNoOperations(Pawn pawn)
		{
			if (pawn.Dead)
			{
				return "is dead";
			}
			if (!pawn.def.AllRecipes.Any((RecipeDef x) => x.AvailableNow && x.AvailableOnNow(pawn)))
			{
				return "has no operations available";
			}
			if (pawn.IsMutant && !pawn.mutant.Def.entitledToMedicalCare)
			{
				return "can't be given medical care";
			}
			if (pawn.Faction == Faction.OfPlayer)
			{
				return null;
			}
			if (pawn.IsPrisonerOfColony || (pawn.HostFaction == Faction.OfPlayer && !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving)))
			{
				return null;
			}
			return "is not someone the colony can operate on";
		}

		// The options the health tab's operations menu would list (HealthCardUtility.cs:264-300), each marked workable
		// or with the reason it is greyed out; options the menu hides because a tech item is missing are kept here
		// with that reason so the refusal can say what is missing.
		internal static List<SurgeryOption> SurgeryOptions(Pawn pawn)
		{
			List<SurgeryOption> options = new List<SurgeryOption>();
			Map held = pawn.MapHeld;
			if (held == null)
			{
				return options;
			}
			foreach (RecipeDef recipe in pawn.def.AllRecipes)
			{
				if (!recipe.AvailableNow)
				{
					continue;
				}
				AcceptanceReport report = recipe.Worker.AvailableReport(pawn);
				if (!report.Accepted && report.Reason.NullOrEmpty())
				{
					continue;
				}
				List<ThingDef> missing = recipe.PotentiallyMissingIngredients(null, held).ToList();
				string problem = null;
				if (!report.Reason.NullOrEmpty())
				{
					problem = report.Reason;
				}
				else if (missing.Count > 0)
				{
					problem = "it needs " + JoinNames(missing.Select((ThingDef d) => d.label)) + ", and none is on the map";
				}
				else if (recipe.Worker is Recipe_ImplantXenogerm)
				{
					problem = "it needs a xenogerm chosen in the health tab";
				}
				if (recipe.targetsBodyPart)
				{
					foreach (BodyPartRecord part in recipe.Worker.GetPartsToApplyOn(pawn, recipe))
					{
						if (recipe.AvailableOnNow(pawn, part))
						{
							options.Add(MakeSurgeryOption(pawn, recipe, part, problem));
						}
					}
				}
				else if (!pawn.health.hediffSet.HasHediff(recipe.addsHediff))
				{
					options.Add(MakeSurgeryOption(pawn, recipe, null, problem));
				}
			}
			return options;
		}

		private static SurgeryOption MakeSurgeryOption(Pawn pawn, RecipeDef recipe, BodyPartRecord part, string problem)
		{
			SurgeryOption o = new SurgeryOption();
			o.recipe = recipe;
			o.part = part;
			o.label = PlainText(recipe.Worker.GetLabelWhenUsedOn(pawn, part)).ToLowerInvariant();
			o.partLabel = part != null && !recipe.hideBodyPartNames ? PlainText(part.Label).ToLowerInvariant() : null;
			o.full = o.partLabel != null ? o.label + " " + o.partLabel : o.label;
			o.problem = problem;
			return o;
		}

		private static string OperationList(List<SurgeryOption> options, int cap)
		{
			return CapList(options.Select((SurgeryOption o) => o.full).Distinct(), cap);
		}

		private static SurgeryOption PickSurgery(Pawn pawn, List<SurgeryOption> options, string recipeText, string partText)
		{
			string n = Norm(recipeText);
			List<SurgeryOption> found = options.Where((SurgeryOption o) => Norm(o.full) == n).ToList();
			if (found.Count == 0)
			{
				found = options.Where((SurgeryOption o) => Norm(o.label) == n).ToList();
			}
			if (found.Count == 0)
			{
				string[] words = n.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
				found = options.Where((SurgeryOption o) => words.Length > 0 && words.All((string w) => ContainsWord(Norm(o.full), w))).ToList();
			}
			if (found.Count == 0)
			{
				throw new Fault(pawn.LabelShort + " has no operation called '" + recipeText + "'." + (options.Count > 0 ? " The operations for " + pawn.LabelShort + " are: " + OperationList(options, 12) + "." : ""));
			}
			if (!string.IsNullOrWhiteSpace(partText))
			{
				string p = Norm(partText);
				List<SurgeryOption> byPart = found.Where((SurgeryOption o) => o.partLabel != null && (Norm(o.partLabel) == p || ContainsWord(Norm(o.partLabel), p))).ToList();
				if (byPart.Count == 0)
				{
					throw new Fault("'" + recipeText + "' can't be done on '" + partText + "' of " + pawn.LabelShort + "." + (found.Any((SurgeryOption o) => o.partLabel != null)
						? " It fits: " + CapList(found.Where((SurgeryOption o) => o.partLabel != null).Select((SurgeryOption o) => o.partLabel), 12) + "." : ""));
				}
				found = byPart;
			}
			if (found.Select((SurgeryOption o) => o.recipe).Distinct().Count() > 1)
			{
				throw new Fault("'" + recipeText + "' could mean several operations on " + pawn.LabelShort + ": " + OperationList(found, 8) + ".");
			}
			if (found.Count > 1)
			{
				throw new Fault("'" + recipeText + "' fits several parts of " + pawn.LabelShort + ": " + CapList(found.Select((SurgeryOption o) => o.partLabel ?? o.label), 12) + ". Say which part.");
			}
			return found[0];
		}

		private static PlanReply PlanOperation(FormContext c)
		{
			Pawn pawn = ResolveOneFrom(c, "PAWN", PawnsOfColony(c.map), "colonist, prisoner or animal");
			string why = WhyNoOperations(pawn);
			if (why != null)
			{
				throw new Fault(pawn.LabelShort + " " + why + ", so no operation can be queued.");
			}
			string recipeText = c.Text("RECIPE") ?? "";
			if (recipeText.Length == 0)
			{
				throw new Fault("The order did not say which operation.");
			}
			SurgeryOption option = PickSurgery(pawn, SurgeryOptions(pawn), recipeText, c.Text("PART"));
			if (option.problem != null)
			{
				throw new Fault(UpperFirst(option.full) + " can't be queued on " + pawn.LabelShort + ": " + PlainText(option.problem) + ".");
			}
			RecipeDef recipe = option.recipe;
			BodyPartRecord part = option.part;
			Plan plan = NewPlan("operation");

			// What the health tab asks before it queues (GenerateSurgeryOption :330-390).
			bool risky = recipe.Worker is Recipe_RemoveBodyPart || recipe.isViolation || recipe.defName.IndexOf("Euthan", StringComparison.OrdinalIgnoreCase) >= 0
				|| recipe.defName.IndexOf("Execute", StringComparison.OrdinalIgnoreCase) >= 0 || recipe.Worker.IsViolationOnPawn(pawn, part, Faction.OfPlayer);
			HediffDef hediffDef = recipe.addsHediff ?? recipe.changesHediffLevel;
			if (hediffDef != null)
			{
				TaggedString violation = CompRoyalImplant.CheckForViolations(pawn, hediffDef, recipe.hediffLevelOffset);
				if (!violation.NullOrEmpty())
				{
					risky = true;
					plan.notes.Add(PlainText(violation.Resolve()));
				}
			}
			TaggedString confirmation = recipe.Worker.GetConfirmation(pawn);
			if (!confirmation.NullOrEmpty())
			{
				risky = true;
				plan.notes.Add(PlainText(confirmation.Resolve()));
			}
			if (pawn.Faction != null && !pawn.Faction.Hidden && !pawn.Faction.HostileTo(Faction.OfPlayer) && recipe.Worker.IsViolationOnPawn(pawn, part, Faction.OfPlayer))
			{
				plan.notes.Add("This operation will anger " + pawn.HomeFaction + ".");
			}
			if (!pawn.MapHeld.mapPawns.FreeColonists.Any((Pawn p) => recipe.PawnSatisfiesSkillRequirements(p)))
			{
				plan.notes.Add("No colonist has the skill for this operation, so it waits until one does.");
			}

			Write write = new Write();
			write.description = "operation '" + option.full + "' on " + pawn.LabelShort;
			write.apply = delegate
			{
				if (pawn.Dead || pawn.BillStack == null)
				{
					throw new Fault(pawn.LabelShort + " can't be operated on any more, so nothing was queued.");
				}
				// The call the menu's action ends in (HealthCardUtility.cs:405).
				HealthCardUtility.CreateSurgeryBill(pawn, recipe, part);
			};
			plan.writes.Add(write);
			string line = (risky ? "RISKY: " : "") + "Queue the operation '" + option.full + "' on " + pawn.LabelShort + "." + (risky ? " It can't easily be undone." : "");
			return Reply(plan, line, new Pawn[] { pawn }, null);
		}

		private static PlanReply PlanOperationCancel(FormContext c)
		{
			Pawn pawn = ResolveOneFrom(c, "PAWN", PawnsOfColony(c.map), "colonist, prisoner or animal");
			string recipeText = c.Text("RECIPE") ?? "";
			string n = Norm(recipeText);
			List<Bill_Medical> bills = pawn.BillStack != null ? pawn.BillStack.Bills.OfType<Bill_Medical>().ToList() : new List<Bill_Medical>();
			if (bills.Count == 0)
			{
				throw new Fault(pawn.LabelShort + " has no operations queued.");
			}
			List<SurgeryOption> asOptions = bills.Select((Bill_Medical b) => MakeSurgeryOption(pawn, b.recipe, b.Part, null)).ToList();
			List<int> hits = new List<int>();
			for (int i = 0; i < asOptions.Count; i++)
			{
				if (Norm(asOptions[i].full) == n)
				{
					hits.Add(i);
				}
			}
			if (hits.Count == 0)
			{
				for (int i = 0; i < asOptions.Count; i++)
				{
					if (Norm(asOptions[i].label) == n)
					{
						hits.Add(i);
					}
				}
			}
			if (hits.Count == 0)
			{
				string[] words = n.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
				for (int i = 0; i < asOptions.Count; i++)
				{
					if (words.Length > 0 && words.All((string w) => ContainsWord(Norm(asOptions[i].full), w)))
					{
						hits.Add(i);
					}
				}
			}
			if (hits.Count == 0)
			{
				throw new Fault(pawn.LabelShort + " has no queued operation called '" + recipeText + "'. Queued: " + OperationList(asOptions, 12) + ".");
			}
			if (hits.Count > 1)
			{
				throw new Fault("'" + recipeText + "' fits several queued operations of " + pawn.LabelShort + ": " + OperationList(hits.Select((int i) => asOptions[i]).ToList(), 8) + ".");
			}
			Bill_Medical bill = bills[hits[0]];
			string label = asOptions[hits[0]].full;
			Plan plan = NewPlan("operation cancel");
			Write write = new Write();
			write.description = "remove operation '" + label + "' from " + pawn.LabelShort;
			write.apply = delegate
			{
				if (bill.deleted || !pawn.BillStack.Bills.Contains(bill))
				{
					throw new Fault("The operation '" + label + "' is already gone.");
				}
				pawn.BillStack.Delete(bill);
			};
			plan.writes.Add(write);
			return Reply(plan, "Remove the queued operation '" + label + "' from " + pawn.LabelShort + ".", new Pawn[] { pawn }, null);
		}
	}
}
