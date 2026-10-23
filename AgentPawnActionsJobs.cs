using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;
using Verse.AI;

namespace VerbalCommands
{
	// Agent 09, jobs: rescue, tend, capture, arrest. Each is the game's own right-click option for that pawn and
	// target (RightClick/Choose, AgentPawnActions.cs), refused with the option's own text when it is greyed out.
	public static partial class AgentPawnActions
	{
		// Null = the form is not one of these four.
		private static PlanReply DispatchJobs(FormContext c)
		{
			switch (c.form)
			{
				case "rescue":
					return PlanRescue(c);
				case "tend":
					return PlanTend(c);
				case "capture":
					return PlanCapture(c);
				case "arrest":
					return PlanArrest(c);
			}
			return null;
		}

		// ---- small helpers ------------------------------------------------------------------------------

		// True when the pawn has `def` as its current job or first in its queue (an ordered job is queued first and
		// started at once, Pawn_JobTracker.cs:924-935).
		private static bool HasJob(Pawn pawn, JobDef def)
		{
			if (pawn == null || pawn.jobs == null)
			{
				return false;
			}
			if (pawn.CurJobDef == def)
			{
				return true;
			}
			if (pawn.jobs.jobQueue != null)
			{
				for (int i = 0; i < pawn.jobs.jobQueue.Count; i++)
				{
					QueuedJob queued = pawn.jobs.jobQueue[i];
					if (queued != null && queued.job != null && queued.job.def == def)
					{
						return true;
					}
				}
			}
			return false;
		}

		// After Choose: the option's action can refuse by a screen message only (no bed, TryTakeOrderedJob false);
		// then no job exists. Say so instead of reporting a done order.
		private static void RequireJob(Pawn doer, JobDef def, string what)
		{
			if (!HasJob(doer, def))
			{
				throw new Fault(doer.LabelShort + " did not start to " + what + "; the game refused it.");
			}
		}

		private static void RequireSeen(FormContext c, Pawn pawn)
		{
			if (pawn.Dead || pawn.Destroyed || !pawn.Spawned)
			{
				throw new Fault(pawn.LabelShort + " is not on the map right now.");
			}
			if (!AgentPlace.Visible(pawn.Map, pawn.Position))
			{
				throw new Fault(TargetName(pawn) + " is out of view.");
			}
		}

		// Named doer = patient is refused for rescue, capture, arrest (the providers never offer a pawn itself).
		private static void RefuseSelfDoer(FormContext c, Pawn patient, string verb)
		{
			if (c.Has("DOER") && FormPlanner.ResolveOnePawn(c, "DOER") == patient)
			{
				throw new Fault(patient.LabelShort + " can't " + verb + " " + patient.LabelShort + ". Name another colonist.");
			}
		}

		private static string Plain(TaggedString text)
		{
			return text.Resolve().StripTags().Trim();
		}

		// ---- rescue -----------------------------------------------------------------------------------------

		// rescue {PAWN}[ with {DOER}]. FloatMenuOptionProvider_RescuePawn (RescuePawn.cs:16-70): the option exists
		// when HealthAIUtility.CanRescueNow(forced) holds (downed, not in a bed, ... HealthAIUtility.cs:81-145) and
		// the patient is not a prisoner, slave or hostile; its action finds the bed (RescuePawn.cs:48-52) and gives
		// JobDefOf.Rescue (RescuePawn.cs:60-62). No bed: FloatMenuUtility.ValidateTakeToBedOption greys it out
		// (FloatMenuUtility.cs:194-214).
		private static PlanReply PlanRescue(FormContext c)
		{
			Pawn patient = FormPlanner.ResolveOnePawn(c, "PAWN");
			RefuseSelfDoer(c, patient, "rescue");
			RequireSeen(c, patient);
			string whyNot = WhyNoRescue(patient);
			if (whyNot != null)
			{
				throw new Fault(whyNot);
			}
			Pawn doer = PickDoer(c, "DOER", (Pawn p) => RescueOption(p, patient), "rescue " + patient.LabelShort, patient.Position);

			Plan plan = FormPlanner.NewPlan("rescue");
			Write write = new Write();
			write.description = doer.LabelShort + " rescues " + patient.LabelShort;
			write.apply = delegate
			{
				RequireSeen(c, patient);
				Choose(RescueOption(doer, patient));
				RequireJob(doer, JobDefOf.Rescue, "rescue " + patient.LabelShort);
				FollowJob(doer, doer.LabelShort + " rescues " + patient.LabelShort);
			};
			plan.writes.Add(write.Reads(() => DoingLine(doer)));
			return FormPlanner.Reply(plan, doer.LabelShort + " carries " + patient.LabelShort + " to a bed.", new List<Pawn> { doer }, null);
		}

		// The game's rescue option for this doer; null = the game offers none to this pawn. Throws the bed refusal.
		private static FloatMenuOption RescueOption(Pawn doer, Pawn patient)
		{
			if (doer == patient)
			{
				return null;
			}
			FloatMenuOption option = Pick(RightClick(new FloatMenuOptionProvider_RescuePawn(), new List<Pawn> { doer }, patient, patient.Position), (FloatMenuOption o) => true);
			if (option == null)
			{
				return null;
			}
			// The same two tries the option makes (RescuePawn.cs:48-52, FloatMenuUtility.cs:196-201).
			Building_Bed bed = RestUtility.FindBedFor(patient, doer, checkSocialProperness: false);
			if (bed == null)
			{
				bed = RestUtility.FindBedFor(patient, doer, checkSocialProperness: false, ignoreOtherReservations: true);
			}
			if (bed == null)
			{
				throw new Fault(Plain("NoNonPrisonerBed".Translate()) + " Build a bed or sleeping spot first.");
			}
			return option;
		}

		// Why the game offers no rescue of this patient to anyone: the provider's own conditions
		// (RescuePawn.cs:18-40, HealthAIUtility.WantsToBeRescued, HealthAIUtility.cs:81-120). Null = looks rescuable.
		private static string WhyNoRescue(Pawn patient)
		{
			string name = patient.LabelShort;
			if (!patient.Downed)
			{
				return name + " is not down, so nobody needs to carry " + patient.gender.GetObjective() + ".";
			}
			if (patient.InBed())
			{
				return name + " is already in a bed.";
			}
			if (patient.InMentalState && !patient.health.hediffSet.HasHediff(HediffDefOf.Scaria))
			{
				return name + " is in a mental break, so the game does not offer a rescue.";
			}
			return null;
		}

		// ---- tend -------------------------------------------------------------------------------------------

		// tend {PAWN}[ with {DOER}]. A drafted doer: FloatMenuOptionProvider_DraftedTend (DraftedTend.cs:30-88, the
		// first option is the one with the doctor's own medicine, or "(Without medicine)" when carrying none; action
		// DraftedTend.cs:81-87 gives JobDefOf.TendPatient). An undrafted doer: the "Prioritize tending" option of
		// FloatMenuOptionProvider_WorkGivers (WorkGivers.cs:100-216) of a WorkGiver_Tend (WorkGiver_Tend.cs:23-58).
		private static PlanReply PlanTend(FormContext c)
		{
			Pawn patient = FormPlanner.ResolveOneFrom(c, "PAWN", FormPlanner.PawnsOfColony(c.map), "colonist, prisoner or animal");
			RequireSeen(c, patient);
			string what = "tend " + patient.LabelShort;
			Pawn doer;
			if (c.Has("DOER"))
			{
				doer = PickDoer(c, "DOER", (Pawn p) => TendOption(p, patient), what, patient.Position);
			}
			else
			{
				// Another colonist is tried first; the patient tends themselves only when nobody else can.
				try
				{
					doer = PickDoer(c, "DOER", (Pawn p) => p == patient ? null : TendOption(p, patient), what, patient.Position);
				}
				catch (Fault first)
				{
					try
					{
						doer = PickDoer(c, "DOER", (Pawn p) => TendOption(p, patient), what, patient.Position);
					}
					catch (Fault)
					{
						throw first;
					}
				}
			}

			Plan plan = FormPlanner.NewPlan("tend");
			FloatMenuOption chosen = TendOption(doer, patient);
			if (doer.Drafted && chosen.Label.StripTags().IndexOf(Plain("WithoutMedicine".Translate()), StringComparison.OrdinalIgnoreCase) >= 0)
			{
				plan.notes.Add(doer.LabelShort + " carries no medicine, so this tends without medicine.");
			}
			Write write = new Write();
			write.description = doer.LabelShort + " tends " + patient.LabelShort;
			write.apply = delegate
			{
				RequireSeen(c, patient);
				Choose(TendOption(doer, patient));
				RequireJob(doer, JobDefOf.TendPatient, "tend " + patient.LabelShort);
				FollowJob(doer, doer.LabelShort + " tends " + patient.LabelShort);
			};
			plan.writes.Add(write.Reads(() => DoingLine(doer)));
			string line = doer == patient ? doer.LabelShort + " tends " + doer.gender.GetObjective() + "self." : doer.LabelShort + " tends " + patient.LabelShort + ".";
			return FormPlanner.Reply(plan, line, new List<Pawn> { doer }, null);
		}

		// The game's tend option for this doer (the drafted provider or the work-giver one, by the doer's state
		// now). Never null: a doer the game gives nothing is a Fault with the reason.
		private static FloatMenuOption TendOption(Pawn doer, Pawn patient)
		{
			if (doer.Drafted)
			{
				FloatMenuOption o = Pick(RightClick(new FloatMenuOptionProvider_DraftedTend(), new List<Pawn> { doer }, patient, patient.Position), (FloatMenuOption x) => true);
				if (o != null)
				{
					return o;
				}
			}
			else
			{
				List<FloatMenuOption> options = RightClick(new FloatMenuOptionProvider_WorkGivers(), new List<Pawn> { doer }, patient, patient.Position);
				List<string> labels = TendLabels(patient);
				Func<FloatMenuOption, bool> isTend = delegate (FloatMenuOption x)
				{
					string text = Why(x);
					return labels.Any((string l) => text.StartsWith(l, StringComparison.OrdinalIgnoreCase));
				};
				FloatMenuOption o = Pick(options, (FloatMenuOption x) => isTend(x) && !x.Disabled) ?? Pick(options, isTend);
				if (o != null)
				{
					return o;
				}
			}
			throw new Fault(WhyNoTend(doer, patient));
		}

		// How the work-giver menu words a tend option for this patient, one start text per WorkGiverDef whose worker is
		// a WorkGiver_Tend (the doctor's tend work givers): the greyed form (WorkGivers.cs:124, then ": reason"),
		// the enabled form (WorkGivers.cs:171) and the "already doing it" form (WorkGivers.cs:147). The option has no
		// field that names its work giver, so it is told apart by this text, built from the same translation keys
		// and the work giver's own verb and gerund.
		private static List<string> TendLabels(Pawn patient)
		{
			List<string> labels = new List<string>();
			foreach (WorkGiverDef giver in DefDatabase<WorkGiverDef>.AllDefsListForReading)
			{
				if (giver.giverClass == null || !typeof(WorkGiver_Tend).IsAssignableFrom(giver.giverClass))
				{
					continue;
				}
				string verb = giver.verb ?? "";
				string gerund = giver.gerund ?? "";
				labels.Add(Plain("CannotGenericWork".Translate(verb, patient.Label)));
				labels.Add(Plain("PrioritizeGeneric".Translate(gerund, patient.Label).CapitalizeFirst()));
				labels.Add(Plain("CannotGenericAlreadyAm".Translate(gerund, patient.LabelShort, patient)));
			}
			return labels;
		}

		// Why the game gives this doer no tend option for this patient: DraftedTend.cs:90-113 (valid target) and
		// WorkGiver_Tend.HasJobOnThing (WorkGiver_Tend.cs:23-43), in that order.
		private static string WhyNoTend(Pawn doer, Pawn patient)
		{
			string name = patient.LabelShort;
			if (!patient.health.HasHediffsNeedingTend())
			{
				return Plain("TendingNotRequired".Translate(patient)) + ".";
			}
			if (!HealthAIUtility.ShouldBeTendedNowByPlayer(patient))
			{
				return name + " is not to be tended now (medical care is off for " + patient.gender.GetObjective() + ", or " + patient.gender.GetPronoun() + " is set to be executed or slaughtered).";
			}
			if (patient.InAggroMentalState && !patient.health.hediffSet.HasHediff(HediffDefOf.Scaria))
			{
				return name + " is in a violent mental state and can't be tended.";
			}
			if (!doer.Drafted && !WorkGiver_Tend.GoodLayingStatusForTend(patient, doer))
			{
				return name + (patient.RaceProps.Humanlike ? " is not in a bed" : " is standing") + ", and an undrafted colonist only tends a patient who " + (patient.RaceProps.Humanlike ? "is in a bed." : "is lying down.")
					+ " A drafted " + doer.LabelShort + " can tend " + patient.gender.GetObjective() + " where " + patient.gender.GetPronoun() + " is.";
			}
			if (doer.Drafted && !patient.Downed && !(patient.IsColonist || patient.IsPrisonerOfColony || patient.IsSlaveOfColony || (patient.Faction == Faction.OfPlayer && patient.IsAnimal)))
			{
				return name + " is not a colonist, prisoner or colony animal, so the game offers no tending.";
			}
			if (!doer.CanReserve(patient))
			{
				return "Someone else is already busy with " + name + ".";
			}
			return doer.LabelShort + " can't tend " + name + " right now.";
		}

		// ---- capture ----------------------------------------------------------------------------------------

		// capture {TARGET}[ with {DOER}]. FloatMenuOptionProvider_CapturePawn (CapturePawn.cs:16-61): the option
		// exists for a pawn GenAI.CanBeCaptured (GenAI.cs:39-71) that HealthAIUtility.CanRescueNow(forced) lets be
		// carried; its action finds a prisoner bed (CapturePawn.cs:37-41) and gives JobDefOf.Capture
		// (CapturePawn.cs:48-50). No bed: ValidateTakeToBedOption greys it out (CapturePawn.cs:58-59).
		private static PlanReply PlanCapture(FormContext c)
		{
			string targetText = c.Text("TARGET");
			if (string.IsNullOrWhiteSpace(targetText))
			{
				throw new Fault("The order named no target.");
			}
			Pawn target;
			string reason;
			if (!AgentTargets.TryFind(c.map, targetText, out target, out reason))
			{
				throw new Fault(string.IsNullOrWhiteSpace(reason) ? "'" + targetText + "' is not in view." : reason);
			}
			string targetName = TargetName(target);
			string whyNot = WhyNoCapture(target, targetName);
			if (whyNot != null)
			{
				throw new Fault(whyNot);
			}
			Pawn doer = PickDoer(c, "DOER", (Pawn p) => CaptureOption(p, target), "capture " + targetName, target.Position);

			Plan plan = FormPlanner.NewPlan("capture");
			Write write = new Write();
			write.description = doer.LabelShort + " captures " + targetName;
			write.apply = delegate
			{
				RequireSeen(c, target);
				if (!target.Downed)
				{
					throw new Fault(targetName + " is not down any more, so can't be captured.");
				}
				Choose(CaptureOption(doer, target));
				RequireJob(doer, JobDefOf.Capture, "capture " + targetName);
				int startTick = Find.TickManager.TicksGame;
				AgentFollow.Track("capture", doer.LabelShort + " captures " + targetName, delegate
				{
					if (target.IsPrisonerOfColony)
					{
						return "captured";
					}
					if (target.Dead || target.Destroyed)
					{
						return "ended: " + targetName + " is dead";
					}
					if (Find.TickManager.TicksGame > startTick + 120 && !HasJob(doer, JobDefOf.Capture))
					{
						return "ended";
					}
					return null;
				});
			};
			plan.writes.Add(write.Reads(() => DoingLine(doer)));

			// What the game itself warns about (CapturePawn.cs:27-33, 52-54), then the plain risk.
			string risk = "A badly hurt captive can die before reaching the bed.";
			if (target.guest != null && !target.guest.Recruitable)
			{
				risk += " The game marks " + targetName + " as \"" + Plain("Unrecruitable".Translate()) + "\".";
			}
			if (target.Faction != null && target.Faction != Faction.OfPlayer && !target.Faction.Hidden && !target.Faction.HostileTo(Faction.OfPlayer) && !target.IsPrisonerOfColony)
			{
				risk += " " + Plain("MessageCapturingWillAngerFaction".Translate(target.Named("PAWN")).AdjustedFor(target));
			}
			string line = "RISKY: " + doer.LabelShort + " captures " + targetName + " and carries " + target.gender.GetObjective() + " to a prisoner bed. " + risk;
			return FormPlanner.Reply(plan, line, new List<Pawn> { doer }, null);
		}

		private static FloatMenuOption CaptureOption(Pawn doer, Pawn target)
		{
			return Pick(RightClick(new FloatMenuOptionProvider_CapturePawn(), new List<Pawn> { doer }, target, target.Position), (FloatMenuOption o) => true);
		}

		// Why the game offers no capture of this pawn: CapturePawn.cs:18-25 (GenAI.CanBeCaptured, CanRescueNow).
		private static string WhyNoCapture(Pawn target, string name)
		{
			if (!target.RaceProps.Humanlike || target.IsSubhuman)
			{
				return name + " is not a person, so the game does not let you capture " + target.gender.GetObjective() + ".";
			}
			if (!target.Downed)
			{
				return name + " is not down, so can't be captured. Bring " + target.gender.GetObjective() + " down first.";
			}
			if (target.InMentalState && !target.health.hediffSet.HasHediff(HediffDefOf.Scaria))
			{
				return name + " is in a mental break, so the game offers no capture.";
			}
			if (target.IsPrisonerOfColony)
			{
				return name + " is already your prisoner.";
			}
			return null;
		}

		// ---- arrest -----------------------------------------------------------------------------------------

		// arrest {PAWN}[ with {DOER}]. FloatMenuOptionProvider_Arrest (Arrest.cs:17-62): for an undrafted arrester the
		// game offers it only for a wild man (Arrest.cs:27-30), so, like every order that sends a colonist to fight,
		// the doer is drafted first (as PlanAttack does) and the option is then asked for with the doer drafted.
		// Its action finds a prisoner bed and gives JobDefOf.Arrest (Arrest.cs:41-54).
		private static PlanReply PlanArrest(FormContext c)
		{
			Pawn patient = FormPlanner.ResolveOnePawn(c, "PAWN");
			RefuseSelfDoer(c, patient, "arrest");
			RequireSeen(c, patient);
			Pawn doer = PickDoer(c, "DOER", (Pawn p) => ArrestOptionPlan(p, patient), "arrest " + patient.LabelShort, patient.Position);

			Plan plan = FormPlanner.NewPlan("arrest");
			bool draftFirst = !doer.Drafted && !c.notes.plannedDrafts.Contains(doer);
			if (draftFirst)
			{
				plan.notes.Add(doer.LabelShort + " gets drafted first (the game only offers an arrest to a drafted colonist). " + doer.LabelShort + " stays drafted until you undraft " + doer.gender.GetObjective() + ".");
				c.notes.plannedDrafts.Add(doer);
			}
			Write write = new Write();
			write.description = doer.LabelShort + (draftFirst ? " is drafted and arrests " : " arrests ") + patient.LabelShort;
			write.apply = delegate
			{
				RequireSeen(c, patient);
				DraftNow(doer, "sent");
				List<FloatMenuOption> options = RightClick(new FloatMenuOptionProvider_Arrest(), new List<Pawn> { doer }, patient, patient.Position);
				FloatMenuOption option = Pick(options, (FloatMenuOption o) => true);
				if (option == null)
				{
					throw new Fault(WhyNoArrest(patient, doer) ?? doer.LabelShort + " can't arrest " + patient.LabelShort + " right now.");
				}
				Choose(option);
				// The option's action gives no job when the game finds no prisoner bed (Arrest.cs:41-48, a screen
				// message only); say that rather than "refused" (outbox 02465200_06133).
				if (!HasJob(doer, JobDefOf.Arrest) && RestUtility.FindBedFor(patient, doer, false, true, GuestStatus.Prisoner) == null)
				{
					throw new Fault("The game found no prisoner bed for " + patient.LabelShort + ", so " + doer.LabelShort + " did not start the arrest (" + doer.LabelShort
						+ " is drafted now). A prisoner bed counts only inside a prison cell: a closed room whose beds are set to prisoner ('set every bed in ROOM to prisoner.').");
				}
				RequireJob(doer, JobDefOf.Arrest, "arrest " + patient.LabelShort);
				int startTick = Find.TickManager.TicksGame;
				AgentFollow.Track("arrest", doer.LabelShort + " arrests " + patient.LabelShort, delegate
				{
					if (patient.IsPrisonerOfColony)
					{
						return "arrested";
					}
					if (patient.Dead || patient.Destroyed)
					{
						return "ended: " + patient.LabelShort + " is dead";
					}
					if (Find.TickManager.TicksGame > startTick + 120 && !HasJob(doer, JobDefOf.Arrest))
					{
						return "ended";
					}
					return null;
				});
			};
			plan.writes.Add(write.Reads(() => DoingLine(doer)));

			// Arrest.cs:61 shows the accept chance in its label; a refusal sends a pawn that is not the enemy of the
			// arrester berserk (Pawn.CheckAcceptArrest, Pawn.cs:4062-4065).
			string chance = patient.GetAcceptArrestChance(doer).ToStringPercent();
			string line = "RISKY: " + doer.LabelShort + (draftFirst ? " is drafted and tries" : " tries") + " to arrest " + patient.LabelShort + " (" + chance + " chance " + patient.gender.GetPronoun() + " submits). A refused arrest can start a fight: "
				+ patient.LabelShort + " can go berserk.";
			return FormPlanner.Reply(plan, line, new List<Pawn> { doer }, null);
		}

		// The arrest option as the game would give it to this doer once drafted. A drafted doer: the real option. An
		// undrafted one: the same conditions in the provider's order (Arrest.cs:19-38), as an enabled stand-in option
		// or a greyed one with the game's own wording; the apply drafts and asks the game for the real one.
		private static FloatMenuOption ArrestOptionPlan(Pawn doer, Pawn patient)
		{
			if (doer == patient || doer.drafter == null || doer.Deathresting)
			{
				return null;
			}
			if (doer.Drafted)
			{
				FloatMenuOption real = Pick(RightClick(new FloatMenuOptionProvider_Arrest(), new List<Pawn> { doer }, patient, patient.Position), (FloatMenuOption o) => true);
				if (real == null)
				{
					string why = WhyNoArrest(patient, doer);
					if (why != null)
					{
						throw new Fault(why);
					}
				}
				return real;
			}
			if (!patient.CanBeArrestedBy(doer) || (patient.Downed && patient.guilt.IsGuilty))
			{
				string why = WhyNoArrest(patient, doer);
				if (why != null)
				{
					throw new Fault(why);
				}
				return null;
			}
			if (doer.InSameExtraFaction(patient, ExtraFactionType.HomeFaction) || doer.InSameExtraFaction(patient, ExtraFactionType.MiniFaction))
			{
				return new FloatMenuOption(Plain("CannotArrest".Translate()) + ": " + Plain("SameFaction".Translate(patient)), null);
			}
			if (!doer.CanReach(patient, PathEndMode.OnCell, Danger.Deadly))
			{
				return new FloatMenuOption(Plain("CannotArrest".Translate()) + ": " + Plain("NoPath".Translate().CapitalizeFirst()), null);
			}
			return new FloatMenuOption("arrest", delegate { });
		}

		// Why the game offers no arrest of this pawn: GenAI.CanBeArrestedBy (GenAI.cs:73-100), Arrest.cs:23-26.
		private static string WhyNoArrest(Pawn patient, Pawn doer)
		{
			string name = patient.LabelShort;
			if (patient.DevelopmentalStage.Baby())
			{
				return name + " is a baby, so the game offers no arrest.";
			}
			if (patient.IsPrisonerOfColony && patient.Position.IsInPrisonCell(patient.MapHeld))
			{
				return name + " is already locked in a prison cell.";
			}
			if (patient.Downed && patient.guilt != null && patient.guilt.IsGuilty)
			{
				return name + " is down and already guilty, so the game offers no arrest.";
			}
			if (patient.HostileTo(Faction.OfPlayer) || (patient.InAggroMentalState && patient.HostileTo(doer)))
			{
				return name + " is hostile, so the game offers no arrest (a hostile pawn is captured, not arrested).";
			}
			return "The game offers no arrest of " + name + " right now.";
		}
	}
}
