using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;
using VerbalCommands.Bridge;

namespace VerbalCommands
{
	// Agent 09, file 3 of AgentPawnActions: extinguish, prioritize, rest, self tend on/off.
	//   extinguish   FloatMenuOptionProvider_ExtinguishFires (RimWorld\FloatMenuOptionProvider_ExtinguishFires.cs:16-52)
	//   prioritize   FloatMenuOptionProvider_WorkGivers (RimWorld\FloatMenuOptionProvider_WorkGivers.cs:29-216)
	//   rest         RestUtility.FindBedFor(Pawn) (RestUtility.cs:232-235) + the bed's option
	//                Building_Bed.GetBedRestFloatMenuOption (Building_Bed.cs:594-630) asked the way the game asks it,
	//                FloatMenuOptionProvider_FromThing.GetOptionsFor (FloatMenuOptionProvider_FromThing.cs:16-30)
	//   self tend    the health tab's box, HealthCardUtility.cs:508-536
	public static partial class AgentPawnActions
	{
		// Null = not one of this file's forms.
		private static PlanReply DispatchWork(FormContext c)
		{
			switch (c.form)
			{
				case "extinguish":
					return PlanExtinguish(c);
				case "prioritize":
					return PlanPrioritize(c);
				case "rest":
					return PlanRest(c);
				case "self tend on":
					return PlanSelfTend(c, true);
				case "self tend off":
					return PlanSelfTend(c, false);
				// User 2026-10-16: manual mining and hauling (AgentMicro.cs).
				case "micro mine":
				case "micro haul":
					return AgentMicro.Plan(c);
				// User 2026-10-16: a pawn that builds only roofs (AgentRoofOnly.cs).
				case "roof only":
					return AgentRoofOnly.Plan(c);
				// User 2026-10-16: every stone chunk indoors marked "haul things" (AgentChunks.cs).
				case "move chunks":
					return AgentChunks.Plan(c);
			}
			return null;
		}

		// A PAWNS-type slot that names somebody (a name, "everyone", or "them" after an earlier sentence).
		private static bool PawnsGiven(FormContext c, string key)
		{
			return c.req.themPawns != null || c.TextList(key).Count > 0;
		}

		private static bool SpotGiven(FormContext c, string key)
		{
			return !string.IsNullOrWhiteSpace(c.Text(key));
		}

		// Passed to PickDoer as the slot key when the slot is absent: PickDoer's `c.Has(key)` is then false and it
		// takes the nearest colonist the game lets do it (a real key could hold an empty list and make it fault).
		private const string NoSlot = "_no_slot_";

		private static List<Pawn> FreeAble(FormContext c)
		{
			return c.map.mapPawns.FreeColonistsSpawned.Where((Pawn p) => !p.Downed).ToList();
		}

		// ---- extinguish ---------------------------------------------------------------------------------------

		private static FloatMenuOption ExtinguishOption(Pawn pawn, IntVec3 cell)
		{
			// The game's click on a burning cell with this pawn selected (FloatMenuMakerMap.cs:27-131, one provider).
			return Pick(RightClick(new FloatMenuOptionProvider_ExtinguishFires(), new List<Pawn> { pawn }, null, cell), (FloatMenuOption o) => true);
		}

		// extinguish fire[ at {SPOT}][ with {PAWNS}]. The option is the game's "Extinguish fires nearby"
		// (ExtinguishFires.cs:37, label key ExtinguishFiresNearby): job ExtinguishFiresNearby queued on every fire
		// near the clicked cell (ExtinguishFires.cs:43-48), given with TryTakeOrderedJob (ExtinguishFires.cs:48).
		private static PlanReply PlanExtinguish(FormContext c)
		{
			Map map = c.map;
			List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Where((Thing t) => t.Spawned && AgentPlace.Visible(map, t.Position)).ToList();
			if (fires.Count == 0)
			{
				throw new Fault("No fire is in view.");
			}
			List<Pawn> named = PawnsGiven(c, "PAWNS") ? FormPlanner.ResolvePawns(c, "PAWNS") : null;

			Thing fire;
			if (SpotGiven(c, "SPOT"))
			{
				IntVec3 spot = Spot(c, "SPOT");
				fire = fires.Where((Thing f) => f.Position.DistanceTo(spot) <= 8f).OrderBy((Thing f) => f.Position.DistanceToSquared(spot)).FirstOrDefault();
				if (fire == null)
				{
					throw new Fault("No fire in view within 8 tiles of " + AgentPlace.Describe(map, spot) + ".");
				}
			}
			else
			{
				List<Pawn> refs = named ?? FreeAble(c);
				if (refs.Count == 0)
				{
					fire = fires[0];
				}
				else
				{
					fire = fires.OrderBy((Thing f) => refs.Min((Pawn p) => p.Position.DistanceToSquared(f.Position))).First();
				}
			}
			IntVec3 fireCell = fire.Position;
			string fireText = AgentPlace.Describe(map, fireCell);

			List<Pawn> doers = new List<Pawn>();
			Plan plan = FormPlanner.NewPlan("extinguish");
			if (named != null)
			{
				string firstWhy = null;
				foreach (Pawn p in named)
				{
					string why = null;
					try
					{
						FloatMenuOption o = ExtinguishOption(p, fireCell);
						if (o == null)
						{
							why = p.LabelShort + " can't put out that fire right now.";
						}
						else if (o.Disabled)
						{
							why = Why(o);
						}
					}
					catch (Fault fault)
					{
						why = fault.Message;
					}
					if (why == null)
					{
						doers.Add(p);
					}
					else
					{
						firstWhy = firstWhy ?? why;
						plan.notes.Add(why + " " + p.LabelShort + " was left out.");
					}
				}
				if (doers.Count == 0)
				{
					throw new Fault(firstWhy ?? "Nobody can put out that fire right now.");
				}
			}
			else
			{
				doers.Add(PickDoer(c, NoSlot, (Pawn p) => ExtinguishOption(p, fireCell), "put out fires", fireCell));
			}

			foreach (Pawn doer in doers)
			{
				Pawn capturedDoer = doer;
				Write write = new Write();
				write.description = doer.LabelShort + " puts out the fire at " + fireText;
				write.apply = delegate
				{
					FloatMenuOption option = ExtinguishOption(capturedDoer, fireCell);
					if (option == null)
					{
						throw new Fault("There is no fire at " + fireText + " any more.");
					}
					Choose(option);
					FollowJob(capturedDoer, capturedDoer.LabelShort + " puts out the fire at " + fireText);
				};
				plan.writes.Add(write.Reads(() => DoingLine(capturedDoer)));
			}
			string line = FormPlanner.JoinNames(FormPlanner.Names(doers)) + (doers.Count == 1 ? " puts" : " put") + " out the fire at " + fireText + ".";
			return FormPlanner.Reply(plan, line, doers, null);
		}

		// ---- prioritize ---------------------------------------------------------------------------------------

		// WORK word -> the work givers it means, taken from the defs (never from translated text):
		//   hauling      every WorkGiverDef whose workType is WorkTypeDefOf.Hauling
		//   constructing the construction work givers for frames and blueprints, by giver class: WorkGiver_ConstructFinishFrames,
		//                WorkGiver_ConstructDeliverResourcesToFrames, WorkGiver_ConstructDeliverResourcesToBlueprints (both the
		//                Construction and the Hauling copies of the two deliver givers use those classes)
		//   repairing    giver class WorkGiver_Repair
		//   mining       workType WorkTypeDefOf.Mining
		//   sowing       giver class WorkGiver_GrowerSow
		//   harvesting   giver class WorkGiver_GrowerHarvest
		//   cutting      workType WorkTypeDefOf.PlantCutting
		//   cleaning     workType WorkTypeDefOf.Cleaning
		internal static Func<WorkGiverDef, bool> WorkMatcher(string work)
		{
			switch (FormPlanner.Norm(work))
			{
				case "hauling":
				case "haul":
					return (WorkGiverDef d) => d.workType == WorkTypeDefOf.Hauling;
				case "constructing":
				case "construction":
				case "construct":
				case "building":
					return (WorkGiverDef d) => IsGiver(d, typeof(WorkGiver_ConstructFinishFrames)) || IsGiver(d, typeof(WorkGiver_ConstructDeliverResourcesToFrames)) || IsGiver(d, typeof(WorkGiver_ConstructDeliverResourcesToBlueprints));
				case "repairing":
				case "repair":
					return (WorkGiverDef d) => IsGiver(d, typeof(WorkGiver_Repair));
				case "mining":
				case "mine":
					return (WorkGiverDef d) => d.workType == WorkTypeDefOf.Mining;
				case "sowing":
				case "sow":
					return (WorkGiverDef d) => IsGiver(d, typeof(WorkGiver_GrowerSow));
				case "harvesting":
				case "harvest":
					return (WorkGiverDef d) => IsGiver(d, typeof(WorkGiver_GrowerHarvest));
				case "cutting":
				case "cut":
					return (WorkGiverDef d) => d.workType == WorkTypeDefOf.PlantCutting;
				case "cleaning":
				case "clean":
					return (WorkGiverDef d) => d.workType == WorkTypeDefOf.Cleaning;
			}
			throw new Fault("'" + work + "' is not a kind of work I can prioritize (hauling, constructing, repairing, mining, sowing, harvesting, cutting, cleaning).");
		}

		private static bool IsGiver(WorkGiverDef d, Type giverClass)
		{
			return d.giverClass != null && giverClass.IsAssignableFrom(d.giverClass);
		}

		// The work giver an ENABLED work-giver option was made from. FloatMenuOptionProvider_WorkGivers.GetWorkGiverOption
		// makes the option's action as a closure over `giver` and `localScanner` (WorkGivers.cs:172-189), and the
		// option carries nothing else that names the giver (its label is translated text: WorkGivers.cs:171). So the
		// closure's fields are read (a read only; nothing is run). Null when the option is greyed (no action) or the
		// closure holds neither.
		internal static WorkGiverDef GiverOf(FloatMenuOption option)
		{
			if (option == null || option.action == null)
			{
				return null;
			}
			return GiverIn(option.action.Target, 0);
		}

		private static WorkGiverDef GiverIn(object holder, int depth)
		{
			if (holder == null || depth > 2)
			{
				return null;
			}
			FieldInfo[] fields = holder.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (FieldInfo f in fields)
			{
				object value = f.GetValue(holder);
				WorkGiverDef def = value as WorkGiverDef;
				if (def != null)
				{
					return def;
				}
				WorkGiver_Scanner scanner = value as WorkGiver_Scanner;
				if (scanner != null && scanner.def != null)
				{
					return scanner.def;
				}
			}
			foreach (FieldInfo f in fields)
			{
				// The compiler's nested closure classes ("<>c__DisplayClass...") hold the outer one in a field.
				if (f.FieldType.IsClass && f.FieldType.Name.Contains("DisplayClass"))
				{
					WorkGiverDef def = GiverIn(f.GetValue(holder), depth + 1);
					if (def != null)
					{
						return def;
					}
				}
			}
			return null;
		}

		// A GREYED work-giver option has no action, so no closure. The game builds its label as
		// "CannotGenericWork"(workGiver.verb, thing label) + ": " + reason (WorkGivers.cs:124,131,139-167), so the giver's
		// own `verb` (def text, the same string the game put in) is looked for in the label. Only used for greyed ones.
		private static bool GreyedBelongs(FloatMenuOption option, List<WorkGiverDef> givers)
		{
			string label = option.Label ?? "";
			foreach (WorkGiverDef d in givers)
			{
				if (!string.IsNullOrEmpty(d.verb) && label.IndexOf(d.verb, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return true;
				}
			}
			return false;
		}

		internal sealed class WorkHit
		{
			public FloatMenuOption option;
			public IntVec3 cell;
		}

		// What the game's right-click gives `doer` at each cell (nearest first) from the work-giver provider; the
		// first enabled option that belongs to the work, else the first greyed one that belongs, else null.
		internal static WorkHit FindWorkOption(Pawn doer, List<IntVec3> cells, Func<WorkGiverDef, bool> belongs)
		{
			List<WorkGiverDef> givers = DefDatabase<WorkGiverDef>.AllDefsListForReading.Where(belongs).ToList();
			WorkHit grey = null;
			foreach (IntVec3 cell in cells)
			{
				List<FloatMenuOption> options = RightClick(new FloatMenuOptionProvider_WorkGivers(), new List<Pawn> { doer }, null, cell);
				foreach (FloatMenuOption o in options)
				{
					if (o == null)
					{
						continue;
					}
					if (!o.Disabled)
					{
						WorkGiverDef def = GiverOf(o);
						if (def != null && belongs(def))
						{
							WorkHit hit = new WorkHit();
							hit.option = o;
							hit.cell = cell;
							return hit;
						}
					}
					else if (grey == null && GreyedBelongs(o, givers))
					{
						grey = new WorkHit();
						grey.option = o;
						grey.cell = cell;
					}
				}
			}
			return grey;
		}

		// prioritize {WORK} at {SPOT}[ with {DOER}]. The game's "Prioritize ..." options at SPOT and the cells within
		// radius 2 (GenRadial.RadialCellsAround is nearest first), given by FloatMenuOptionProvider_WorkGivers
		// (TryTakeOrderedJobPrioritizedWork, WorkGivers.cs:178).
		private static PlanReply PlanPrioritize(FormContext c)
		{
			string work = c.Text("WORK");
			if (string.IsNullOrWhiteSpace(work))
			{
				throw new Fault("The order named no kind of work.");
			}
			Func<WorkGiverDef, bool> belongs = WorkMatcher(work);
			IntVec3 spot = Spot(c, "SPOT");
			Map map = c.map;
			string spotText = AgentPlace.Describe(map, spot);
			string workWord = FormPlanner.Norm(work);

			List<IntVec3> cells = new List<IntVec3>();
			foreach (IntVec3 cell in GenRadial.RadialCellsAround(spot, 2f, true))
			{
				if (cell.InBounds(map) && AgentPlace.Visible(map, cell))
				{
					cells.Add(cell);
				}
			}

			bool anyOption = false;
			Func<Pawn, FloatMenuOption> optionFor = delegate (Pawn p)
			{
				WorkHit h = FindWorkOption(p, cells, belongs);
				if (h == null)
				{
					return null;
				}
				anyOption = true;
				return h.option;
			};
			Pawn doer;
			try
			{
				doer = PickDoer(c, "DOER", optionFor, "prioritize " + workWord, spot);
			}
			catch (Fault)
			{
				if (!anyOption)
				{
					throw new Fault("There is no " + workWord + " to do at " + spotText + ".");
				}
				throw;
			}
			WorkHit planned = FindWorkOption(doer, cells, belongs);
			// Red skills (SkillBlock.cs, 2026-10-16): the work type of the option the game offers; skill 0 to 2 is refused.
			WorkGiverDef plannedGiver = planned != null ? GiverOf(planned.option) : null;
			string red = plannedGiver != null ? SkillBlock.Check(doer, plannedGiver.workType) : null;
			if (red != null)
			{
				throw new Fault(red);
			}
			string optionText = planned != null ? Why(planned.option) : workWord;

			Pawn capturedDoer = doer;
			Plan plan = FormPlanner.NewPlan("prioritize");
			Write write = new Write();
			write.description = doer.LabelShort + " prioritizes " + workWord + " at " + spotText;
			write.apply = delegate
			{
				WorkHit hit = FindWorkOption(capturedDoer, cells, belongs);
				if (hit == null)
				{
					throw new Fault("There is no " + workWord + " to do at " + spotText + " any more.");
				}
				Choose(hit.option);
				FollowJob(capturedDoer, capturedDoer.LabelShort + " does " + workWord + " at " + spotText);
			};
			plan.writes.Add(write.Reads(() => DoingLine(capturedDoer)));
			return FormPlanner.Reply(plan, doer.LabelShort + ": " + optionText + " (at " + spotText + ").", new List<Pawn> { doer }, null);
		}

		// ---- rest ---------------------------------------------------------------------------------------------

		// The option Building_Bed.GetBedRestFloatMenuOption gives (Building_Bed.cs:594-630), asked through
		// FloatMenuOptionProvider_FromThing for the bed RestUtility.FindBedFor(pawn) picks. Null bed = no free bed.
		private static FloatMenuOption RestOption(Pawn pawn, out Building_Bed bed)
		{
			bed = RestUtility.FindBedFor(pawn);
			if (bed == null)
			{
				throw new Fault("There is no free bed for " + pawn.LabelShort + ".");
			}
			return Pick(RightClick(new FloatMenuOptionProvider_FromThing(), new List<Pawn> { pawn }, bed, bed.Position), (FloatMenuOption o) => true);
		}

		// have {PAWN} rest. The bed's "Use medical bed" option: job LayDown with restUntilHealed (Building_Bed.cs:620-622).
		private static PlanReply PlanRest(FormContext c)
		{
			Pawn pawn = FormPlanner.ResolveOnePawn(c, "PAWN");
			Building_Bed bed;
			FloatMenuOption option = RestOption(pawn, out bed);
			if (option == null)
			{
				// The game offers the option only for an undrafted pawn and a medical bed (Building_Bed.cs:596).
				if (pawn.Drafted)
				{
					throw new Fault(pawn.LabelShort + " is drafted; the bed's rest option is only offered to undrafted colonists.");
				}
				if (!bed.Medical)
				{
					if (HealthAIUtility.ShouldSeekMedicalRest(pawn))
					{
						throw new Fault("There is no free medical bed for " + pawn.LabelShort + ".");
					}
					throw new Fault(pawn.LabelShort + " is not hurt or sick enough to rest until healed.");
				}
				throw new Fault(pawn.LabelShort + " can't rest in " + bed.LabelShort + " right now.");
			}
			if (option.Disabled)
			{
				throw new Fault(Why(option));
			}
			string bedText = bed.LabelShort + " at " + AgentPlace.Describe(c.map, bed.Position);
			Pawn capturedPawn = pawn;
			Plan plan = FormPlanner.NewPlan("rest");
			Write write = new Write();
			write.description = pawn.LabelShort + " rests in " + bedText + " until healed";
			write.apply = delegate
			{
				Building_Bed nowBed;
				FloatMenuOption nowOption = RestOption(capturedPawn, out nowBed);
				Choose(nowOption);
				FollowJob(capturedPawn, capturedPawn.LabelShort + " rests in " + nowBed.LabelShort);
			};
			plan.writes.Add(write.Reads(() => DoingLine(capturedPawn)));
			return FormPlanner.Reply(plan, pawn.LabelShort + " goes to " + bedText + " and rests until healed.", new List<Pawn> { pawn }, null);
		}

		// ---- self tend ----------------------------------------------------------------------------------------

		private static string Pronoun(Pawn pawn, string male, string female, string other)
		{
			return pawn.gender == Gender.Male ? male : (pawn.gender == Gender.Female ? female : other);
		}

		// turn on/off self tend for {PAWNS}: the health tab's box (HealthCardUtility.cs:508-536), which is shown for
		// a colonist who is not dead, not a baby and has playerSettings (:508). Turning it on is undone for a pawn
		// who can never doctor (:527-531); a note when the Doctor priority is 0 (:532-535). Same shape as
		// FormWave1.PlanFireAtWill.
		private static PlanReply PlanSelfTend(FormContext c, bool on)
		{
			List<Pawn> pawns = FormPlanner.ResolvePawns(c, "PAWNS");
			Plan plan = FormPlanner.NewPlan(c.form);
			List<Pawn> done = new List<Pawn>();
			string firstProblem = null;
			foreach (Pawn pawn in pawns)
			{
				string problem = null;
				if (!pawn.IsColonist || pawn.Dead || pawn.DevelopmentalStage.Baby() || pawn.playerSettings == null)
				{
					problem = pawn.LabelShort + " has no self tend setting.";
				}
				else if (on && pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor))
				{
					problem = pawn.LabelShort + " can never do doctor work, so can't tend " + Pronoun(pawn, "himself", "herself", "themselves") + ".";
				}
				else if (pawn.playerSettings.selfTend == on)
				{
					problem = pawn.LabelShort + " already has self tend " + (on ? "on" : "off") + ".";
				}
				if (problem != null)
				{
					firstProblem = firstProblem ?? problem;
					plan.notes.Add(problem + (pawns.Count > 1 ? " Left out." : ""));
					continue;
				}
				if (on && pawn.workSettings != null && pawn.workSettings.GetPriority(WorkTypeDefOf.Doctor) == 0)
				{
					plan.notes.Add(pawn.LabelShort + "'s doctor work is off, so " + Pronoun(pawn, "he", "she", "they") + " will only self-tend after you turn it on.");
				}
				done.Add(pawn);
				Pawn capturedPawn = pawn;
				Write write = new Write();
				write.description = pawn.LabelShort + ": self tend " + (on ? "on" : "off");
				write.apply = delegate
				{
					if (on && capturedPawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor))
					{
						throw new Fault(capturedPawn.LabelShort + " can never do doctor work, so can't tend " + Pronoun(capturedPawn, "himself", "herself", "themselves") + ".");
					}
					capturedPawn.playerSettings.selfTend = on;
				};
				plan.writes.Add(write.Reads(() => capturedPawn.LabelShort + ": self tend " + (capturedPawn.playerSettings != null && capturedPawn.playerSettings.selfTend ? "on" : "off")));
			}
			if (done.Count == 0)
			{
				throw new Fault(firstProblem ?? "Nobody to change self tend for.");
			}
			return FormPlanner.Reply(plan, (on ? "Self tend on for " : "Self tend off for ") + FormPlanner.JoinNames(FormPlanner.Names(done)) + ".", done, null);
		}
	}
}
