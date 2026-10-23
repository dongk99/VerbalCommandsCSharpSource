using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using VerbalCommands.Bridge;
using Verse;
using Verse.AI;

namespace VerbalCommands
{
	// Agent 09: orders that make colonists act now (move, attack, rescue, tend, ...), every one given the
	// way the game's right-click menu gives it: ask the game's own FloatMenuOptionProvider for that pawn and
	// target, refuse with the option's own text when the game shows it greyed out, run the option's action.
	// The files of this class: AgentPawnActions.cs (this one: entry, right-click helper, move/attack),
	// AgentPawnActionsJobs.cs (rescue, tend, capture, arrest), AgentPawnActionsWork.cs (extinguish, prioritize,
	// rest, self tend), AgentPawnActionsGear.cs (equip, wear, drop, pick up).
	public static partial class AgentPawnActions
	{
		// Called by agent 10's wiring (FormPlanner.DispatchMore). Null = this form is not one of ours.
		// Runs on the game's main thread, like every plan.
		public static PlanReply Plan(FormContext c)
		{
			try
			{
				switch (c.form)
				{
					case "move to spot":
						return PlanGo(c);
					case "attack":
						return PlanAttack(c);
				}
				return DispatchJobs(c) ?? DispatchWork(c) ?? DispatchGear(c);
			}
			catch (Fault fault)
			{
				return PlanReply.Fail(fault.Message);
			}
		}

		// ---- helpers shared by the four files of this class -------------------------------------------

		// The cell the SPOT-type slot `key` names (AgentPlace.TryCell); a plain-words Fault when it names nothing,
		// is off the map or is under fog.
		internal static IntVec3 Spot(FormContext c, string key)
		{
			string text = c.Text(key);
			if (string.IsNullOrWhiteSpace(text))
			{
				throw new Fault("The order named no spot.");
			}
			IntVec3 cell;
			string reason;
			if (!AgentPlace.TryCell(c.map, text, out cell, out reason))
			{
				throw new Fault(string.IsNullOrWhiteSpace(reason) ? "'" + text + "' is not a spot I can find." : reason);
			}
			return cell;
		}

		// What the game does for ONE provider when `doers` are selected and the player right-clicks `target` (or
		// the cell `cell` when target is null): FloatMenuMakerMap.GetOptions (FloatMenuMakerMap.cs:27-71) then
		// GetProviderOptions (FloatMenuMakerMap.cs:73-131) for this provider only, same steps, same order.
		// The click position is the target's DrawPos for a pawn, else the cell's centre. With a target given, only
		// that thing (and the pawn itself for a pawn) is asked for options, not the other things near the click
		// (the game offers options for every thing under the cursor; here the order named one). The option list
		// can be empty (the provider does not apply) and can hold greyed-out options (the game's reasons).
		// Providers: create them with `new`. FloatMenuMakerMap.Init (FloatMenuMakerMap.cs:18-25) makes every one
		// with Activator.CreateInstance, no arguments, and the provider classes keep no per-click state
		// (FloatMenuOptionProvider.cs holds none; FloatMenuOptionProvider_DraftedAttack.cs:9 has only a scratch
		// static list, DraftedMove.cs:10 the same), so a fresh instance gives the same options as the game's own.
		internal static List<FloatMenuOption> RightClick(FloatMenuOptionProvider provider, List<Pawn> doers, Thing target, IntVec3 cell)
		{
			List<FloatMenuOption> options = new List<FloatMenuOption>();
			if (provider == null || doers == null || doers.Count == 0)
			{
				throw new Fault("The order named nobody to give it to.");
			}
			Map map = doers[0].Map;
			if (map == null && target != null)
			{
				map = target.Map;
			}
			if (map == null)
			{
				throw new Fault(doers[0].LabelShort + " is not on a map right now.");
			}
			// The game uses Find.CurrentMap for every click (FloatMenuMakerMap.cs:31,35,135).
			if (map != Find.CurrentMap)
			{
				throw new Fault("Switch the view to that map first; the game only gives orders on the map you look at.");
			}
			IntVec3 clickCell = cell.IsValid ? cell : (target != null ? target.Position : IntVec3.Invalid);
			if (!clickCell.IsValid)
			{
				throw new Fault("The order named no place to click.");
			}
			Vector3 clickPos = (target is Pawn) ? target.DrawPos : clickCell.ToVector3Shifted();
			if (!clickPos.InBounds(map))
			{
				return options;
			}
			string firstName = doers[0].LabelShort;
			// A NEW list: the constructor removes the pawns that can't take orders (FloatMenuContext.cs:77).
			FloatMenuContext context = new FloatMenuContext(new List<Pawn>(doers), clickPos, map);
			if (!context.allSelectedPawns.Any())
			{
				throw new Fault(firstName + " can't take orders right now.");
			}
			if (!context.ClickedCell.IsValid || !context.ClickedCell.InBounds(map))
			{
				return options;
			}
			if (!context.IsMultiselect)
			{
				AcceptanceReport report = FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn(context.FirstSelectedPawn);
				if (!report.Accepted)
				{
					string reason = report.Reason.StripTags();
					throw new Fault(string.IsNullOrWhiteSpace(reason) ? context.FirstSelectedPawn.LabelShort + " can't take orders right now." : reason);
				}
			}
			else
			{
				string firstReason = null;
				context.allSelectedPawns.RemoveAll(delegate (Pawn p)
				{
					AcceptanceReport r = FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn(p);
					if (!r.Accepted && firstReason == null)
					{
						firstReason = string.IsNullOrWhiteSpace(r.Reason) ? p.LabelShort + " can't take orders right now." : r.Reason.StripTags();
					}
					return !r.Accepted;
				});
				if (!context.allSelectedPawns.Any())
				{
					throw new Fault(firstReason ?? "Nobody can take orders right now.");
				}
			}
			Pawn making = context.IsMultiselect ? null : context.FirstSelectedPawn;
			try
			{
				FloatMenuMakerMap.makingFor = making;
				FloatMenuMakerMap.currentProvider = provider;
				if (context.ValidSelectedPawns.Any() && provider.Applies(context))
				{
					options.AddRange(provider.GetOptions(context));
					foreach (Thing clickedThing in context.ClickedThings)
					{
						if (target != null && clickedThing != target)
						{
							continue;
						}
						if (!provider.TargetThingValid(clickedThing, context))
						{
							continue;
						}
						Thing thing = clickedThing;
						CompSelectProxy proxy;
						if (thing.TryGetComp(out proxy) && proxy.thingToSelect != null)
						{
							thing = proxy.thingToSelect;
						}
						foreach (FloatMenuOption item in provider.GetOptionsFor(thing, context))
						{
							if (item.iconThing == null)
							{
								item.iconThing = thing;
							}
							item.targetsDespawned = !thing.Spawned;
							options.Add(item);
						}
					}
					foreach (Pawn clickedPawn in context.ClickedPawns)
					{
						if (target != null && clickedPawn != target)
						{
							continue;
						}
						if (!provider.TargetPawnValid(clickedPawn, context))
						{
							continue;
						}
						foreach (FloatMenuOption item2 in provider.GetOptionsFor(clickedPawn, context))
						{
							if (item2.iconThing == null)
							{
								item2.iconThing = clickedPawn;
							}
							item2.targetsDespawned = !clickedPawn.Spawned;
							options.Add(item2);
						}
					}
				}
			}
			catch (Exception e)
			{
				// The game logs and carries on (FloatMenuMakerMap.cs:125-128); so do we.
				Log.Error("Error in FloatMenuWorker " + provider.GetType().Name + ": " + e);
			}
			finally
			{
				FloatMenuMakerMap.currentProvider = null;
				FloatMenuMakerMap.makingFor = null;
			}
			return options;
		}

		// The first option the match accepts, or null.
		internal static FloatMenuOption Pick(List<FloatMenuOption> options, Func<FloatMenuOption, bool> match)
		{
			if (options == null)
			{
				return null;
			}
			foreach (FloatMenuOption o in options)
			{
				if (o != null && match(o))
				{
					return o;
				}
			}
			return null;
		}

		// For a greyed-out option (FloatMenuOption.Disabled: its action is null, FloatMenuOption.cs:182-187): its
		// label with rich-text tags stripped, which is the game's own reason ("Fire at raider 1: Out of range").
		internal static string Why(FloatMenuOption o)
		{
			if (o == null)
			{
				return "The game does not allow that right now.";
			}
			string label = (o.Label ?? "").StripTags().Trim();
			return label.Length == 0 ? "The game does not allow that right now." : label;
		}

		// Run the option as a click on the menu would: FloatMenuOption.Chosen (FloatMenuOption.cs:294-312) calls
		// action() when not Disabled; its colonistOrdering flag only plays a sound and a null floatMenu skips
		// PreOptionChosen (an empty virtual, FloatMenu.cs:316), so no window and no sound are needed.
		internal static void Choose(FloatMenuOption o)
		{
			if (o == null)
			{
				throw new Fault("The game offered no such order.");
			}
			if (o.Disabled)
			{
				throw new Fault(Why(o));
			}
			o.Chosen(false, null);
		}

		// The doer a form needs. When the slot `key` names a colonist (FormPlanner.ResolveOnePawn) that one is
		// used and a greyed-out option is refused with its own text. When the slot is absent: the free colonists
		// that are spawned and not downed, nearest `near` first (when given), and the first whose option is
		// enabled is taken. None: a Fault with the nearest one's Why, or "Nobody can <what> right now.".
		// optionFor returns the game's option for that pawn (null = the game offers none to that pawn).
		// (`near` is an added optional last parameter; the four-argument call of the brief works unchanged.)
		internal static Pawn PickDoer(FormContext c, string key, Func<Pawn, FloatMenuOption> optionFor, string what, IntVec3? near = null)
		{
			if (c.Has(key))
			{
				Pawn named = FormPlanner.ResolveOnePawn(c, key);
				FloatMenuOption o = optionFor(named);
				if (o == null)
				{
					throw new Fault(named.LabelShort + " can't " + what + " right now.");
				}
				if (o.Disabled)
				{
					throw new Fault(Why(o));
				}
				return named;
			}
			List<Pawn> candidates = c.map.mapPawns.FreeColonistsSpawned.Where((Pawn p) => !p.Downed).ToList();
			if (near.HasValue)
			{
				IntVec3 spot = near.Value;
				candidates = candidates.OrderBy((Pawn p) => p.Position.DistanceToSquared(spot)).ToList();
			}
			string nearestWhy = null;
			foreach (Pawn p in candidates)
			{
				FloatMenuOption o;
				try
				{
					o = optionFor(p);
				}
				catch (Fault fault)
				{
					if (nearestWhy == null)
					{
						nearestWhy = fault.Message;
					}
					continue;
				}
				if (o == null)
				{
					continue;
				}
				if (!o.Disabled)
				{
					return p;
				}
				if (nearestWhy == null)
				{
					nearestWhy = Why(o);
				}
			}
			throw new Fault(nearestWhy ?? "Nobody can " + what + " right now.");
		}

		// Called inside an apply, after Choose: the job the option gave (the pawn's current job, else the first
		// queued one) is followed with AgentFollow.Track; nothing happens when no job was given.
		internal static void FollowJob(Pawn pawn, string label)
		{
			if (pawn == null || pawn.jobs == null)
			{
				return;
			}
			Job job = pawn.CurJob;
			if (job == null && pawn.jobs.jobQueue != null && pawn.jobs.jobQueue.Count > 0)
			{
				QueuedJob queued = pawn.jobs.jobQueue[0];
				job = queued != null ? queued.job : null;
			}
			if (job != null)
			{
				AgentFollow.Track(pawn, job, label);
			}
		}

		// For AgentReadback.Reads: "Bob: drafted, <job report>" or "Bob: not drafted, <job report>".
		internal static string DoingLine(Pawn pawn)
		{
			string report = null;
			try
			{
				if (pawn.jobs != null && pawn.jobs.curDriver != null)
				{
					report = pawn.jobs.curDriver.GetReport();
				}
			}
			catch (Exception)
			{
				report = null;
			}
			if (string.IsNullOrWhiteSpace(report))
			{
				report = "no job";
			}
			return pawn.LabelShort + ": " + (pawn.Drafted ? "drafted" : "not drafted") + ", " + report.StripTags().Trim();
		}

		// ---- shared by move and attack ----------------------------------------------------------------

		// As FormPlanner.PlanMove (FormPlanner.cs:685-704): pawns that can't be drafted at all are taken out of
		// `pawns` and named in `skipped`; pawns not drafted (and not drafted by an earlier sentence of this order)
		// go to `toDraft`, which the apply drafts. Faults when nobody is left.
		private static void SplitDrafting(FormContext c, List<Pawn> pawns, string what, out List<Pawn> toDraft, out List<Pawn> skipped)
		{
			toDraft = new List<Pawn>();
			skipped = new List<Pawn>();
			foreach (Pawn pawn in pawns.ToList())
			{
				if (pawn.Downed || !pawn.Spawned || pawn.drafter == null || pawn.Deathresting)
				{
					skipped.Add(pawn);
					pawns.Remove(pawn);
				}
				else if (!pawn.Drafted && !c.notes.plannedDrafts.Contains(pawn))
				{
					toDraft.Add(pawn);
				}
			}
			if (pawns.Count == 0)
			{
				throw new Fault(FormPlanner.JoinNames(FormPlanner.Names(skipped)) + " can't be drafted or " + what + " right now.");
			}
		}

		private static void AddDraftNotes(Plan plan, FormContext c, List<Pawn> toDraft, List<Pawn> skipped, string what)
		{
			if (toDraft.Count > 0)
			{
				plan.notes.Add(toDraft.Count + (toDraft.Count == 1 ? " pawn gets" : " pawns get") + " drafted (" + FormPlanner.JoinNames(FormPlanner.Names(toDraft)) + "). They stay drafted until you undraft them.");
				foreach (Pawn p in toDraft)
				{
					c.notes.plannedDrafts.Add(p);
				}
			}
			if (skipped.Count > 0)
			{
				plan.notes.Add(FormPlanner.JoinNames(FormPlanner.Names(skipped)) + " can't be drafted or " + what + " right now, so " + (skipped.Count == 1 ? "was" : "were") + " left out.");
			}
		}

		private static void DraftNow(Pawn pawn, string doing)
		{
			if (!pawn.Drafted && pawn.drafter != null)
			{
				pawn.drafter.Drafted = true;
			}
			if (!pawn.Drafted)
			{
				throw new Fault(pawn.LabelShort + " could not be drafted, so was not " + doing + ".");
			}
		}

		private static string DraftedFirst(List<Pawn> toDraft)
		{
			if (toDraft.Count == 0)
			{
				return "";
			}
			return " (" + FormPlanner.JoinNames(FormPlanner.Names(toDraft)) + (toDraft.Count == 1 ? " is" : " are") + " drafted first)";
		}

		// ---- move to spot ----------------------------------------------------------------------

		// Like FormPlanner.PlanMove (FormPlanner.cs:680-771) but to a SPOT: the spot's cell and the nearest free
		// standable cells around it (GenRadial), one for each pawn, each reachable by that pawn
		// (FloatMenuOptionProvider_DraftedMove.PawnCanGoto, DraftedMove.cs:76-87). The go-to is
		// FloatMenuOptionProvider_DraftedMove.PawnGotoAction (DraftedMove.cs:89-121), the same call the menu's
		// "Go here" makes for one pawn (DraftedMove.cs:43).
		private static PlanReply PlanGo(FormContext c)
		{
			List<Pawn> pawns = FormPlanner.ResolvePawns(c, "PAWNS");
			IntVec3 spot = Spot(c, "SPOT");
			List<Pawn> toDraft;
			List<Pawn> skipped;
			SplitDrafting(c, pawns, "moved", out toDraft, out skipped);

			List<IntVec3> cells = new List<IntVec3>();
			foreach (IntVec3 cell in GenRadial.RadialCellsAround(spot, 15.9f, true))
			{
				if (cell.InBounds(c.map) && cell.Standable(c.map) && cell.GetDoor(c.map) == null && cell.GetFirstPawn(c.map) == null && AgentPlace.Visible(c.map, cell))
				{
					cells.Add(cell);
				}
			}
			string spotText = AgentPlace.Describe(c.map, spot);
			if (cells.Count < pawns.Count)
			{
				throw new Fault("There is free standing room for " + cells.Count + (cells.Count == 1 ? " pawn" : " pawns") + " around " + spotText + ", not " + pawns.Count + ".");
			}

			Plan plan = FormPlanner.NewPlan("move to spot");
			AddDraftNotes(plan, c, toDraft, skipped, "moved");
			List<IntVec3> used = new List<IntVec3>();
			foreach (Pawn pawn in pawns)
			{
				IntVec3 target = IntVec3.Invalid;
				foreach (IntVec3 cell in cells)
				{
					if (!used.Contains(cell) && FloatMenuOptionProvider_DraftedMove.PawnCanGoto(pawn, cell).Accepted)
					{
						target = cell;
						break;
					}
				}
				if (!target.IsValid)
				{
					throw new Fault(pawn.LabelShort + " can't get to " + spotText + ".");
				}
				used.Add(target);
				Pawn capturedPawn = pawn;
				IntVec3 capturedCell = target;
				string label = pawn.LabelShort + " goes to " + AgentPlace.Describe(c.map, target);
				Write write = new Write();
				write.description = pawn.LabelShort + (toDraft.Contains(pawn) ? " is drafted and " : " ") + "walks to " + spotText;
				write.apply = delegate
				{
					DraftNow(capturedPawn, "sent");
					FloatMenuOptionProvider_DraftedMove.PawnGotoAction(capturedCell, capturedPawn, capturedCell);
					int startTick = Find.TickManager.TicksGame;
					AgentFollow.Track("move", label, delegate
					{
						string name = capturedPawn.LabelShort;
						if (capturedPawn.Dead || capturedPawn.Destroyed)
						{
							return "stopped: " + name + " is dead";
						}
						if (!capturedPawn.Spawned)
						{
							return "stopped: " + name + " is no longer on the map";
						}
						if (capturedPawn.Downed)
						{
							return "stopped: " + name + " is downed";
						}
						if (!capturedPawn.Drafted)
						{
							return "stopped: " + name + " is no longer drafted";
						}
						if (capturedPawn.Position.AdjacentTo8WayOrInside(capturedCell))
						{
							return "arrived";
						}
						// Not walking there any more (another order, or the path closed). The first ticks are
						// left alone: the job may not have started yet.
						if (Find.TickManager.TicksGame > startTick + 120 && capturedPawn.CurJobDef != JobDefOf.Goto)
						{
							return "stopped: " + name + " is doing something else";
						}
						return null;
					});
				};
				plan.writes.Add(write.Reads(() => DoingLine(capturedPawn)));
			}
			string line = "Send " + FormPlanner.JoinNames(FormPlanner.Names(pawns)) + " to " + spotText + DraftedFirst(toDraft) + ".";
			return FormPlanner.Reply(plan, line, pawns, null);
		}

		// ---- attack ---------------------------------------------------------------------------------------

		// attack {TARGET} with {PAWNS}. The game's drafted attack: FloatMenuOptionProvider_DraftedAttack
		// (DraftedAttack.cs:66-113, the single-select options). With a ranged primary weapon
		// (FloatMenuUtility.UseRangedAttack, FloatMenuUtility.cs:150-157) the provider yields the ranged option
		// first (autoTakeablePriority 40, DraftedAttack.cs:88) and then the melee one (30, DraftedAttack.cs:106);
		// without one it yields only the melee option (the ranged branch returns null with an empty failStr,
		// DraftedAttack.cs:164-174). The right one is found by that priority, not by its translated text; a
		// greyed-out option has no priority and is the one the game explains (a refusal with its own text).
		private static PlanReply PlanAttack(FormContext c)
		{
			List<Pawn> pawns = FormPlanner.ResolvePawns(c, "PAWNS");
			string targetText = c.Text("TARGET");
			if (string.IsNullOrWhiteSpace(targetText))
			{
				throw new Fault("The order named no target.");
			}
			List<Pawn> toDraft;
			List<Pawn> skipped;
			SplitDrafting(c, pawns, "sent to fight", out toDraft, out skipped);

			Dictionary<Pawn, Pawn> targetOf = new Dictionary<Pawn, Pawn>();
			if (FormPlanner.Norm(targetText) == "nearest enemy")
			{
				List<AgentTarget> enemies = AgentTargets.Names(c.map).Where((AgentTarget t) => !t.Downed && t.Pawn != null && t.Pawn.Spawned).ToList();
				if (enemies.Count == 0)
				{
					throw new Fault("No enemy is in view.");
				}
				foreach (Pawn pawn in pawns)
				{
					Pawn nearestEnemy = enemies.OrderBy((AgentTarget t) => t.Pawn.Position.DistanceToSquared(pawn.Position)).First().Pawn;
					targetOf[pawn] = nearestEnemy;
				}
			}
			else
			{
				Pawn found;
				string reason;
				if (!AgentTargets.TryFind(c.map, targetText, out found, out reason))
				{
					throw new Fault(string.IsNullOrWhiteSpace(reason) ? "'" + targetText + "' is not in view." : reason);
				}
				foreach (Pawn pawn in pawns)
				{
					targetOf[pawn] = found;
				}
			}

			Plan plan = FormPlanner.NewPlan("attack");
			AddDraftNotes(plan, c, toDraft, skipped, "sent to fight");
			foreach (Pawn pawn in pawns)
			{
				Pawn capturedPawn = pawn;
				Pawn capturedTarget = targetOf[pawn];
				string targetName = TargetName(capturedTarget);
				Write write = new Write();
				write.description = pawn.LabelShort + (toDraft.Contains(pawn) ? " is drafted and attacks " : " attacks ") + targetName;
				write.apply = delegate
				{
					if (capturedTarget.Dead || capturedTarget.Destroyed || !capturedTarget.Spawned)
					{
						throw new Fault(targetName + " is gone.");
					}
					if (!AgentPlace.Visible(capturedTarget.Map, capturedTarget.Position))
					{
						throw new Fault(targetName + " is out of view now.");
					}
					DraftNow(capturedPawn, "sent");
					List<FloatMenuOption> options = RightClick(new FloatMenuOptionProvider_DraftedAttack(), new List<Pawn> { capturedPawn }, capturedTarget, capturedTarget.Position);
					float wanted = FloatMenuUtility.UseRangedAttack(capturedPawn) ? 40f : 30f;
					FloatMenuOption option = Pick(options, (FloatMenuOption o) => o.Disabled || o.autoTakeablePriority == wanted);
					if (option == null)
					{
						throw new Fault(capturedPawn.LabelShort + " can't attack " + targetName + " right now.");
					}
					Choose(option);
					FollowJob(capturedPawn, capturedPawn.LabelShort + " attacks " + targetName);
				};
				plan.writes.Add(write.Reads(() => DoingLine(capturedPawn)));
			}

			// Who attacks what: grouped by target, "Bob and Dana attack raider 1, Eve attacks raider 2".
			List<string> parts = new List<string>();
			foreach (IGrouping<Pawn, Pawn> group in pawns.GroupBy((Pawn p) => targetOf[p]))
			{
				List<string> names = FormPlanner.Names(group);
				parts.Add(FormPlanner.JoinNames(names) + (names.Count == 1 ? " attacks " : " attack ") + TargetName(group.Key));
			}
			string line = "RISKY: " + string.Join(", ", parts.ToArray()) + ". The colonists will fight and can be hurt or killed" + DraftedFirst(toDraft) + ".";
			return FormPlanner.Reply(plan, line, pawns, null);
		}

		private static string TargetName(Pawn target)
		{
			return AgentTargets.NameOf(target) ?? target.LabelShort;
		}
	}
}
