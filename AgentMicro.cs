using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using VerbalCommands.Bridge;
using Verse;
using Verse.AI;

namespace VerbalCommands
{
	// Manual mining and hauling, "micros" (user, 2026-10-16: "manual hauls/mines (i.e. micros)."; "this will be core
	// component so agents can micromange pawns to haul manually"). The router forms "micro mine" (mine near SPOT [with
	// PAWNS]. / mine in area AREA [with PAWNS].) and "micro haul" (haul from SPOT [with PAWNS].), router.txt.
	//
	// The order (user's words, 2026-10-16):
	//   - PAWNS left out: "Asks which pawn to send. Lists pawns with skill, manipulation, consciousness, mining yield/speed,
	//     expected travel time to that coordinate". The question waits like a router question (the inbox pauses while it
	//     waits); the answer "name1, name2" is sent on as the "with PAWNS" sentence (AgentInbox.WorkMicroAnswer).
	//   - "Gate: Hostile pawns/animals/mechs/insects present": "Scope: Entire map", "Apply/cancel works" (Plan.askConfirm,
	//     like AgentDoorCheck). Hostiles are the names `attack` uses (AgentTargets.Names: hostile pawns in view), not downed.
	//   - Tiles: "nearest mineable tile EXCEPT ROCK. Anything that DOESNT drop chunks is MINEABLE tile." Per pawn "Level
	//     8~10: 2. below 8: 1. 11~: 3." Mining skill.
	//   - "Scanner: Mines 1 tile first. if a new vein shows ..., automines that vein as well" ("Upto count. Probably inject
	//     reminder if theres more vein."). Other resources near: "pause game, ask agents if they want to mine that resource
	//     as well. if "cancel" is said, then hauling loop begins. If they say "mine", then yes, deduct whatever tile pawns
	//     already mined, upto limit."
	//   - "when finished, auto-priority hauls (add some delay ...)": "0.3s, 3s check while the game runs, excl. while
	//     paused". Real seconds, counted only while the game is not paused (Update, from GameComponentUpdate).
	//   - The gate: "The pawn who has mined holds resources, and physically cannot hold more ... EVERY pawn who has undertaken
	//     in that mining order must have SOME resources to bring back, unless the area has no resources left to haul",
	//     "unless hauling is disabled/turned off for this pawn". "Cannot hold more": the game's own pick-up count,
	//     MassUtility.CountToPickUpUntilOverEncumbered (RimWorld\MassUtility.cs:32-40), is 0 for every resource left; Pick
	//     Up And Haul fills the inventory with that same count (JobDriver_HaulToInventory.cs:43). A stack carried in the
	//     hands (the vanilla haul): Pawn_CarryTracker.Full, or nothing left that stacks with it (CheckHauling).
	//   - Done: "task finished, pawns coming back, est. [x] hr with [inventory_list of those pawns]." ("Travel back to
	//     wherever pawns came from, so, reuse the time that was calculated from beginning steps."); a haul order: "hauling
	//     done, pawns coming back with [inventory_list of those pawns]." Given with the next result of any order (answer 9A).
	//   - Leftovers: "a reminder shell is created (every 6hr) saying "coordinate,coordinate has [resource_list] from mining.
	//     say "move resources ... from coordinate,coordinate"" ("haul from" is that sentence); in the 6-hour batch ("Yes.
	//     batch."); "Reminder stays if the area has leftover resources (but not chunks/slags)."
	//   - "Definition: Area scan. 8x8 tile near the (coordinate,coordinate) & edge spaces around (area/zone)."
	//
	// Not the user's (my choices, open to change): corpses are not resources; the 3 s check does not interrupt fleeing, a pawn on fire, bed rest until healed
	// or another forced order.
	//
	// Save: MicroData in GameComponent_VerbalCommands ("vcMicro"). The pawn question of an order is not saved.
	// Main thread only.
	public static class AgentMicro
	{
		internal const int PhaseMining = 0;
		internal const int PhaseDelay = 1;
		internal const int PhaseWaitAnswer = 2;
		internal const int PhaseHauling = 3;
		internal const int PhaseDone = 4;

		private const float HaulDelaySeconds = 0.3f;
		private const float CheckSeconds = 3f;
		private const int ScanSide = 8;
		private const int MaxVein = 60;
		private const int HaulCandidates = 8;
		private const int ArriveTiles = 6;

		// ---- text (every line an agent reads; see the list given to the user before the build) ----------------------

		private static string PawnQuestion(string place, List<string> lines)
		{
			return "Which pawns to send to " + place + "? Reply with names, e.g. name1, name2, name3.\n" + string.Join("\n", lines.ToArray());
		}

		private static string MinePawnLine(Pawn p, float hours, string place)
		{
			return p.LabelShort + ": mining " + p.skills.GetSkill(SkillDefOf.Mining).Level
				+ ", manipulation " + p.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation).ToStringPercent()
				+ ", consciousness " + p.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness).ToStringPercent()
				+ ", mining yield " + p.GetStatValue(StatDefOf.MiningYield).ToStringPercent()
				+ ", mining speed " + p.GetStatValue(StatDefOf.MiningSpeed).ToStringPercent()
				+ ", " + Hours(hours) + " hr to " + place + ".";
		}

		private static string HaulPawnLine(Pawn p, float hours, string place)
		{
			return p.LabelShort + ": " + Hours(hours) + " hr to " + place + ".";
		}

		private const string HostileNote = "hostile pawns/animals detected. Really send? reply with apply to send, cancel to cancel";

		private static string MinePlanLine(Pawn p, int tiles, string resource, IntVec3 tile, float hours)
		{
			return p.LabelShort + " mines " + tiles + (tiles == 1 ? " tile" : " tiles") + " of " + resource + " from " + Xz(tile) + " (" + Hours(hours) + " hr away), then hauls it";
		}

		private static string HaulPlanLine(Pawn p, string place, float hours)
		{
			return p.LabelShort + " hauls from " + place + " (" + Hours(hours) + " hr away)";
		}

		private static string MoreQuestion(string place, string others)
		{
			return place + " also has " + others + ". Reply mine to mine it too, or cancel to haul.";
		}

		private static string VeinLine(string resource, IntVec3 cell, int tiles)
		{
			return "[REMINDER] More " + resource + " is left at " + Xz(cell) + " (" + tiles + (tiles == 1 ? " tile" : " tiles") + "). Say \"mine near " + Xz(cell) + "\" to mine it.";
		}

		private static string MineDone(float hours, string inventory)
		{
			return "task finished, pawns coming back, est. " + Hours(hours) + " hr with " + inventory + ".";
		}

		private static string HaulDone(string inventory)
		{
			return "hauling done, pawns coming back with " + inventory + ".";
		}

		private static string LeftoverLine(string place, string resources)
		{
			return "[REMINDER] " + place + " has " + resources + " from mining. say \"haul from " + place + "\"";
		}

		// ---- question ---------------------------------------------------------------------------------------------

		private sealed class Question
		{
			public string kind;       // "pawns" (an order without PAWNS) or "more" (other resources near, mid-run)
			public string text;
			public string sentence;   // pawns: the order's sentence without its period
			public int jobId;         // more: the job
		}

		private static Question question;
		private static Game seenGame;
		private static float checkTimer;
		private static readonly HashSet<string> loggedFaults = new HashSet<string>();

		// Raised each time a question is asked, so the inbox can tell a refusal that asked from one that did not.
		public static int AskCount { get; private set; }
		public static bool HasQuestion { get { return question != null; } }
		public static string QuestionText { get { return question != null ? question.text : null; } }
		public static bool QuestionIsPawns { get { return question != null && question.kind == "pawns"; } }

		private static MicroData Data
		{
			get
			{
				GameComponent_VerbalCommands c = GameComponent_VerbalCommands.Instance;
				return c == null ? null : c.Micro;
			}
		}

		// The pawn question's answer as the order to send: "mine near 112, 87 with bob, ann."
		public static string PawnAnswerSentence(string answer)
		{
			string names = (answer ?? "").Trim().TrimEnd('.').Trim();
			string sentence = question.sentence + " with " + names + ".";
			question = null;
			return sentence;
		}

		// The pawn question's answer is names of free colonists ("bob, ann" / "bob and ann"), as FormPlanner.FindPawn
		// matches them; anything else is not taken as the answer.
		public static bool AreColonistNames(string text)
		{
			Map map = Find.CurrentMap;
			if (map == null)
			{
				return false;
			}
			string s = (text ?? "").Trim().TrimEnd('.').Replace(" and ", ",");
			List<string> names = s.Split(',').Select((string n) => n.Trim()).Where((string n) => n.Length > 0).ToList();
			if (names.Count == 0)
			{
				return false;
			}
			foreach (string n in names)
			{
				bool found = false;
				foreach (Pawn p in map.mapPawns.FreeColonists)
				{
					NameTriple triple = p.Name as NameTriple;
					if (FormPlanner.SameName(p.LabelShort, n) || (triple != null && (FormPlanner.SameName(triple.Nick, n) || FormPlanner.SameName(triple.First, n))))
					{
						found = true;
						break;
					}
				}
				if (!found)
				{
					return false;
				}
			}
			return true;
		}

		// "mine" to the other-resources question: the pawns waiting on it mine those too, up to their tiles.
		public static void AnswerMine()
		{
			Question q = question;
			question = null;
			MicroJob job = q != null ? JobById(q.jobId) : null;
			if (job == null)
			{
				return;
			}
			job.asking = false;
			job.askedMore = true;
			Map map = MapOf(job);
			if (map == null)
			{
				return;
			}
			foreach (ThingDef def in OtherYields(job, map).Keys)
			{
				if (!job.allowed.Contains(def.defName))
				{
					job.allowed.Add(def.defName);
				}
			}
			foreach (MicroPawn mp in job.pawns.Where((MicroPawn p) => p.phase == PhaseWaitAnswer).ToList())
			{
				AfterMined(job, mp, map);
			}
		}

		// "cancel": a pawn question is dropped; to the other-resources question, the waiting pawns start hauling.
		public static void CancelQuestion()
		{
			Question q = question;
			question = null;
			if (q == null || q.kind != "more")
			{
				return;
			}
			MicroJob job = JobById(q.jobId);
			if (job == null)
			{
				return;
			}
			job.asking = false;
			job.askedMore = true;
			Map map = MapOf(job);
			if (map == null)
			{
				return;
			}
			foreach (MicroPawn mp in job.pawns.Where((MicroPawn p) => p.phase == PhaseWaitAnswer).ToList())
			{
				StartHauling(job, mp, map);
			}
		}

		// What finished jobs said, given once with the next result of any order (AgentInbox.WriteOutbox).
		public static List<string> TakeMessages()
		{
			MicroData d = Data;
			if (d == null || d.messages.Count == 0)
			{
				return new List<string>();
			}
			List<string> lines = d.messages;
			d.messages = new List<string>();
			return lines;
		}

		// The 6-hour batch (AgentReminderScan.Scan): one line per finished job whose place still has resources lying there.
		// A place with none left is forgotten.
		public static List<string> LeftoverLines(Map map)
		{
			List<string> lines = new List<string>();
			MicroData d = Data;
			if (d == null)
			{
				return lines;
			}
			foreach (MicroJob job in d.jobs.Where((MicroJob j) => j.finished && j.mapId == map.uniqueID).ToList())
			{
				List<Thing> left = Resources(job, map);
				if (left.Count == 0)
				{
					d.jobs.Remove(job);
					continue;
				}
				lines.Add(LeftoverLine(job.place, CountList(left)));
			}
			return lines;
		}

		// ---- plan -------------------------------------------------------------------------------------------------

		private sealed class Site
		{
			public IntVec3 centre;
			public List<IntVec3> region;
			public string place;      // "112, 87"
			public string sentence;   // the order's sentence without its period
			public bool unforbid;     // a rotting food haul (AgentRotScan): forbidden items count and are unforbidden at apply
		}

		// Rotting food hauls (AgentRotScan.HaulPlan; user, 2026-10-17: "haul [itemname] ... move the item in that 10x10 area
		// ... if they are forbidden, they are automatically unforbidden"): the sentence the pawn answer is added to ("haul
		// rice near 115, 85") and its square, so the answer ("... with bob.", form "micro haul") gets the 10x10 square, not
		// the 8x8 around the cell. Key: map id and the sentence in lower case. Forgotten when another game is loaded.
		private static readonly Dictionary<string, CellRect> rotSites = new Dictionary<string, CellRect>();

		private static string RotKey(Map map, string sentence)
		{
			return map.uniqueID + "|" + (sentence ?? "").Trim().ToLowerInvariant();
		}

		// "haul THINGS." while THINGS is rotting outdoors (AgentRotScan.HaulPlan): a haul over the 10x10 square.
		internal static PlanReply PlanRotHaul(FormContext c, CellRect square, List<string> things)
		{
			Site site = new Site();
			site.centre = square.CenterCell;
			site.region = square.Cells.ToList();
			site.place = Xz(site.centre);
			site.sentence = "haul " + FormPlanner.JoinNames(things) + " near " + site.place;
			site.unforbid = true;
			rotSites[RotKey(c.map, site.sentence)] = square;
			return PlanSite(c, true, site);
		}

		// Forms "micro mine" and "micro haul" (AgentPawnActions.DispatchWork).
		public static PlanReply Plan(FormContext c)
		{
			bool haul = c.form == "micro haul";
			return PlanSite(c, haul, ReadSite(c, haul));
		}

		private static PlanReply PlanSite(FormContext c, bool haul, Site site)
		{
			Map map = c.map;
			// The older forms that run as micro orders now (user, 2026-10-16: "Merge all 3. new replace old"): THINGS limits
			// what is mined or picked up (matched as the designation forms match it, AgentDesignations.WordFilter); COUNT caps
			// each pawn's tiles (the smaller of COUNT and the Mining skill count).
			List<string> things = c.TextList("THINGS");
			Func<Thing, bool> filter = things.Count > 0 ? AgentDesignations.WordFilter(map, haul ? "haul" : "mine", things) : null;
			double? countSaid = c.Number("COUNT");
			int count = countSaid.HasValue ? Math.Max(1, (int)Math.Round(countSaid.Value)) : int.MaxValue;

			IntVec3 first = IntVec3.Invalid;
			ThingDef resource = null;
			List<ThingDef> kinds = new List<ThingDef>();
			if (!haul)
			{
				first = NearestOre(map, site, filter);
				if (!first.IsValid)
				{
					throw new Fault("There is no ore in view near " + site.place + ".");
				}
				resource = YieldOf(map, first);
				kinds.Add(resource);
				if (filter != null)
				{
					foreach (IntVec3 cell in site.region)
					{
						ThingDef y = YieldOf(map, cell);
						if (y != null && !kinds.Contains(y) && filter(cell.GetFirstMineable(map)))
						{
							kinds.Add(y);
						}
					}
				}
			}
			else if (Resources(site.region, map, filter, site.unforbid).Count == 0)
			{
				throw new Fault("No resources lie at " + site.place + ".");
			}
			IntVec3 goal = haul ? StandCell(map, site) : first;

			if (!c.Has("PAWNS") && c.req.themPawns == null)
			{
				List<KeyValuePair<Pawn, float>> able = new List<KeyValuePair<Pawn, float>>();
				foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
				{
					if (WhyNot(p, haul) == null && p.CanReach(goal, PathEndMode.Touch, Danger.Deadly))
					{
						able.Add(new KeyValuePair<Pawn, float>(p, TravelHours(p, goal)));
					}
				}
				if (able.Count == 0)
				{
					throw new Fault("Nobody can " + (haul ? "haul from " : "mine at ") + site.place + " right now.");
				}
				List<string> lines = able.OrderBy((KeyValuePair<Pawn, float> e) => e.Value)
					.Select((KeyValuePair<Pawn, float> e) => haul ? HaulPawnLine(e.Key, e.Value, site.place) : MinePawnLine(e.Key, e.Value, site.place)).ToList();
				string asked = PawnQuestion(site.place, lines);
				// Only the agent inbox takes the answer (AgentInbox.WorkMicroAnswer); the order window gets the list alone.
				if (AgentInbox.Enabled)
				{
					question = new Question { kind = "pawns", sentence = site.sentence, text = asked };
					AskCount++;
				}
				return PlanReply.Fail(asked);
			}

			List<Pawn> named = FormPlanner.ResolvePawns(c, "PAWNS");
			Plan plan = FormPlanner.NewPlan(c.form);
			List<MicroPawn> going = new List<MicroPawn>();
			List<string> parts = new List<string>();
			// Why a pawn was left out (the skill block line, "was left out"). Sent as plan notes only while Build advice is on
			// (Settings gate, user, 2026-10-26); the first one is still the Fault text when nobody goes.
			List<string> leftOut = new List<string>();
			HashSet<IntVec3> taken = new HashSet<IntVec3>();
			List<IntVec3> tiles = haul ? null : Candidates(map, site.region, first, kinds);
			foreach (Pawn p in named)
			{
				// Red skills (SkillBlock.cs, 2026-10-16): Mining 0 to 2 is left out with the user's line.
				string red = haul ? null : SkillBlock.Check(p, WorkTypeDefOf.Mining);
				if (red != null)
				{
					leftOut.Add(red);
					continue;
				}
				string why = p.Map != map ? p.LabelShort + " is not on this map." : WhyNot(p, haul);
				IntVec3 tile = IntVec3.Invalid;
				if (why == null && !haul)
				{
					List<IntVec3> reachable = tiles.Where((IntVec3 t) => !taken.Contains(t) && p.CanReach(t, PathEndMode.Touch, Danger.Deadly))
						.OrderBy((IntVec3 t) => t.DistanceToSquared(site.centre)).ToList();
					if (reachable.Count == 0)
					{
						why = p.LabelShort + " can't reach any " + resource.label + " there.";
					}
					else
					{
						tile = reachable[0];
					}
				}
				if (why == null && haul && !p.CanReach(goal, PathEndMode.Touch, Danger.Deadly))
				{
					why = p.LabelShort + " can't reach " + site.place + ".";
				}
				if (why != null)
				{
					leftOut.Add(why + " " + p.LabelShort + " was left out.");
					continue;
				}
				MicroPawn mp = new MicroPawn();
				mp.pawn = p;
				if (haul)
				{
					mp.travelHours = TravelHours(p, goal);
					mp.phase = PhaseHauling;
					parts.Add(HaulPlanLine(p, site.place, mp.travelHours));
				}
				else
				{
					taken.Add(tile);
					mp.quota = Math.Min(count, Quota(p));
					mp.target = tile;
					mp.travelHours = TravelHours(p, tile);
					mp.phase = PhaseMining;
					parts.Add(MinePlanLine(p, mp.quota, resource.label, tile, mp.travelHours));
				}
				going.Add(mp);
			}
			if (going.Count == 0)
			{
				throw new Fault(leftOut.Count > 0 ? leftOut[0] : "Nobody to send.");
			}
			// Settings gate (user, 2026-10-26): Build advice tips/notes. Off: the skill block line and the "was left out" lines
			// are not sent with the plan (the Fault above still gives the first one).
			if (VerbalCommandsMod.BuildAdviceOn)
			{
				plan.notes.AddRange(leftOut);
			}
			// Settings gate (user, 2026-10-26): Building placement checks. Off: no hostiles note and no confirm.
			if (VerbalCommandsMod.PlacementChecksOn && HostilesOnMap(map))
			{
				plan.notes.Add(HostileNote);
				plan.askConfirm = true;
			}

			Write write = new Write();
			write.description = site.sentence + ": " + FormPlanner.JoinNames(going.Select((MicroPawn m) => m.pawn.LabelShort));
			write.apply = delegate
			{
				if (site.unforbid)
				{
					Unforbid(Resources(site.region, map, filter, true));
				}
				Start(map, haul, site, kinds, things, going);
			};
			plan.writes.Add(write.Reads(() => string.Join("; ", going.Select((MicroPawn m) => AgentPawnActions.DoingLine(m.pawn)).ToArray())));
			return FormPlanner.Reply(plan, string.Join("; ", parts.ToArray()) + ".", going.Select((MicroPawn m) => m.pawn), null);
		}

		private static Site ReadSite(FormContext c, bool haul)
		{
			Map map = c.map;
			Site site = new Site();
			if (c.Has("AREA"))
			{
				CellRect rect;
				string reason;
				if (!AgentPlace.TryRect(map, c.Text("AREA"), out rect, out reason))
				{
					throw new Fault(string.IsNullOrWhiteSpace(reason) ? "'" + c.Text("AREA") + "' is not an area I can find." : reason);
				}
				site.centre = rect.CenterCell;
				site.region = rect.ExpandedBy(1).ClipInsideMap(map).Cells.ToList();
				site.sentence = "mine " + (c.Has("THINGS") ? FormPlanner.JoinNames(c.TextList("THINGS")) + " " : "") + "in area " + c.Text("AREA");
			}
			else
			{
				string text = c.Text("SPOT");
				site.centre = AgentPawnActions.Spot(c, "SPOT");
				Zone zone = map.zoneManager.AllZones.FirstOrDefault((Zone z) => FormPlanner.SameName(z.label, text));
				if (zone != null)
				{
					HashSet<IntVec3> cells = new HashSet<IntVec3>(zone.Cells);
					foreach (IntVec3 cell in zone.Cells)
					{
						foreach (IntVec3 n in GenAdjFast.AdjacentCells8Way(cell))
						{
							if (n.InBounds(map))
							{
								cells.Add(n);
							}
						}
					}
					site.region = cells.ToList();
				}
				else
				{
					site.region = Square(map, site.centre);
				}
				// The sentence the pawn answer is added to (" with NAMES"): the form the order came in.
				if (c.Has("THINGS"))
				{
					string named = FormPlanner.JoinNames(c.TextList("THINGS"));
					site.sentence = haul ? "haul " + named + " near " + text
						: "mine nearest " + (c.Has("COUNT") ? c.Text("COUNT") + " " : "") + named + " near " + text;
				}
				else
				{
					site.sentence = (haul ? "haul from " : "mine near ") + text;
				}
				// The pawn answer to a rotting food haul: its 10x10 square again, forbidden items included.
				CellRect square;
				if (haul && c.Has("THINGS") && rotSites.TryGetValue(RotKey(map, site.sentence), out square))
				{
					site.region = square.Cells.ToList();
					site.unforbid = true;
				}
			}
			site.place = Xz(site.centre);
			return site;
		}

		// The 8x8 cells around a cell (the area scan).
		private static List<IntVec3> Square(Map map, IntVec3 c)
		{
			return new CellRect(c.x - ScanSide / 2, c.z - ScanSide / 2, ScanSide, ScanSide).ClipInsideMap(map).Cells.ToList();
		}

		// Null when the pawn can do it, else why not (the game's rules for a prioritized job: a work type at priority 0 is
		// refused, FloatMenuOptionProvider_WorkGivers.cs:144-146; a missing capacity :131-134).
		private static string WhyNot(Pawn p, bool haul)
		{
			if (p == null || p.Dead || !p.Spawned || p.Downed)
			{
				return (p != null ? p.LabelShort : "That colonist") + " can't go right now.";
			}
			if (p.InMentalState)
			{
				return p.LabelShort + " is in a mental break.";
			}
			WorkTypeDef work = haul ? WorkTypeDefOf.Hauling : WorkTypeDefOf.Mining;
			if (p.WorkTypeIsDisabled(work))
			{
				return p.LabelShort + " can never do " + work.gerundLabel + ".";
			}
			if (p.workSettings == null || p.workSettings.GetPriority(work) == 0)
			{
				return p.LabelShort + "'s " + work.gerundLabel + " is off.";
			}
			if (!p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
			{
				return p.LabelShort + " can't use their hands.";
			}
			return null;
		}

		// Mining skill: below 8 one tile, 8 to 10 two, 11 and up three (user, 2026-10-16).
		private static int Quota(Pawn p)
		{
			int level = p.skills != null ? p.skills.GetSkill(SkillDefOf.Mining).Level : 0;
			return level < 8 ? 1 : level <= 10 ? 2 : 3;
		}

		// Walking time along the game's path, at the pawn's own speed: PawnPath.TotalCost counts the game's default
		// 13 ticks per straight cell (Verse\PathFinder.cs:256-258, as AgentDoorPathScan), scaled by Pawn.TicksPerMoveCardinal.
		private static float TravelHours(Pawn p, IntVec3 to)
		{
			try
			{
				using (PawnPath path = p.Map.pathFinder.FindPathNow(p.Position, to, p, null, PathEndMode.Touch))
				{
					if (path == null || !path.Found)
					{
						return 0f;
					}
					return path.TotalCost * (p.TicksPerMoveCardinal / 13f) / GenDate.TicksPerHour;
				}
			}
			catch (Exception)
			{
				return p.Position.DistanceTo(to) * p.TicksPerMoveCardinal / GenDate.TicksPerHour;
			}
		}

		private static bool HostilesOnMap(Map map)
		{
			return AgentTargets.Names(map).Any((AgentTarget t) => t != null && t.Pawn != null && !t.Downed && !t.Pawn.Downed && !t.Pawn.Dead);
		}

		// A cell of the region a pawn can stand on, nearest the centre (the goal of a haul order's walk).
		private static IntVec3 StandCell(Map map, Site site)
		{
			IntVec3 best = site.centre;
			float bestD = float.MaxValue;
			foreach (IntVec3 c in site.region)
			{
				if (c.Standable(map) && AgentPlace.Visible(map, c))
				{
					float d = c.DistanceToSquared(site.centre);
					if (d < bestD)
					{
						bestD = d;
						best = c;
					}
				}
			}
			return best;
		}

		// ---- ore --------------------------------------------------------------------------------------------------

		// What the rock at the cell drops, when it drops something that is not a chunk ("Anything that DOESNT drop chunks
		// is MINEABLE tile"): Mineable.TrySpawnYield spawns building.mineableThing (RimWorld\Mineable.cs:66-80).
		private static ThingDef YieldOf(Map map, IntVec3 c)
		{
			if (!c.InBounds(map) || !AgentPlace.Visible(map, c))
			{
				return null;
			}
			Mineable m = c.GetFirstMineable(map);
			if (m == null || m.def.building == null)
			{
				return null;
			}
			ThingDef y = m.def.building.mineableThing;
			if (y == null || y.IsWithinCategory(ThingCategoryDefOf.Chunks))
			{
				return null;
			}
			return y;
		}

		// The nearest ore inside the area scan (the 8x8 around a cell, or the area / zone and its edge); none there = the
		// order is refused. User, 2026-10-16, on the scan moving to the nearest ore anywhere: "....and what if that ore
		// happens to be across the map".
		private static IntVec3 NearestOre(Map map, Site site, Func<Thing, bool> filter)
		{
			IEnumerable<IntVec3> cells = site.region;
			IntVec3 best = IntVec3.Invalid;
			int bestD = int.MaxValue;
			foreach (IntVec3 c in cells)
			{
				if (YieldOf(map, c) == null || (filter != null && !filter(c.GetFirstMineable(map))))
				{
					continue;
				}
				int d = c.DistanceToSquared(site.centre);
				if (d < bestD)
				{
					bestD = d;
					best = c;
				}
			}
			return best;
		}

		// The ore cells of that yield connected to the cell (8-way), the cell included.
		private static List<IntVec3> Vein(Map map, IntVec3 from, ThingDef def)
		{
			List<IntVec3> found = new List<IntVec3>();
			HashSet<IntVec3> seen = new HashSet<IntVec3> { from };
			Queue<IntVec3> open = new Queue<IntVec3>();
			open.Enqueue(from);
			while (open.Count > 0 && found.Count < MaxVein)
			{
				IntVec3 c = open.Dequeue();
				if (YieldOf(map, c) != def)
				{
					continue;
				}
				found.Add(c);
				foreach (IntVec3 n in GenAdjFast.AdjacentCells8Way(c))
				{
					if (seen.Add(n))
					{
						open.Enqueue(n);
					}
				}
			}
			return found;
		}

		// The ore of that yield in the region, plus the veins touching the given cells (a vein a mined tile opened).
		private static List<IntVec3> Candidates(Map map, List<IntVec3> region, IntVec3 from, List<ThingDef> kinds)
		{
			HashSet<IntVec3> cells = new HashSet<IntVec3>(region.Where((IntVec3 c) => kinds.Contains(YieldOf(map, c))));
			if (from.IsValid)
			{
				foreach (IntVec3 c in Vein(map, from, YieldOf(map, from)))
				{
					cells.Add(c);
				}
			}
			return cells.ToList();
		}

		// Ore cells next to the region or the mined cells, by yield: what the pawns may go on to mine.
		private static Dictionary<ThingDef, List<IntVec3>> YieldsNear(MicroJob job, Map map)
		{
			Dictionary<ThingDef, List<IntVec3>> by = new Dictionary<ThingDef, List<IntVec3>>();
			HashSet<IntVec3> cells = new HashSet<IntVec3>(job.region);
			foreach (IntVec3 m in job.minedCells)
			{
				foreach (IntVec3 n in GenAdjFast.AdjacentCells8Way(m))
				{
					cells.Add(n);
				}
			}
			foreach (IntVec3 c in cells)
			{
				ThingDef y = YieldOf(map, c);
				if (y == null)
				{
					continue;
				}
				List<IntVec3> list;
				if (!by.TryGetValue(y, out list))
				{
					by[y] = list = new List<IntVec3>();
				}
				list.Add(c);
			}
			// A vein that a mined tile opened runs on past the region.
			foreach (ThingDef y in by.Keys.ToList())
			{
				foreach (IntVec3 start in by[y].ToList())
				{
					foreach (IntVec3 v in Vein(map, start, y))
					{
						if (!by[y].Contains(v))
						{
							by[y].Add(v);
						}
					}
				}
			}
			return by;
		}

		private static Dictionary<ThingDef, List<IntVec3>> OtherYields(MicroJob job, Map map)
		{
			Dictionary<ThingDef, List<IntVec3>> all = YieldsNear(job, map);
			return all.Where((KeyValuePair<ThingDef, List<IntVec3>> e) => !job.allowed.Contains(e.Key.defName)).ToDictionary((KeyValuePair<ThingDef, List<IntVec3>> e) => e.Key, (KeyValuePair<ThingDef, List<IntVec3>> e) => e.Value);
		}

		// ---- resources on the ground ------------------------------------------------------------------------------

		// Any item a colonist can haul (apparel and weapons too; user, 2026-10-16: ""apparel and weapons" do count. they
		// are something you can haul."), but not chunks ("but not chunks/slags") and not corpses.
		private static bool IsResource(Thing t)
		{
			if (t == null || t.def == null || t.def.category != ThingCategory.Item || !t.def.EverHaulable || t is Corpse)
			{
				return false;
			}
			return !t.def.IsWithinCategory(ThingCategoryDefOf.Chunks);
		}

		// Resources lying in those cells: in view, not forbidden, not already in a storage that takes them.
		private static List<Thing> Resources(IEnumerable<IntVec3> cells, Map map)
		{
			return Resources(cells, map, null);
		}

		// The same, only the things the filter takes (null = all).
		private static List<Thing> Resources(IEnumerable<IntVec3> cells, Map map, Func<Thing, bool> filter)
		{
			return Resources(cells, map, filter, false);
		}

		// The same; forbidden ones too when asked (a rotting food haul unforbids them).
		private static List<Thing> Resources(IEnumerable<IntVec3> cells, Map map, Func<Thing, bool> filter, bool forbiddenToo)
		{
			List<Thing> found = new List<Thing>();
			foreach (IntVec3 c in cells)
			{
				if (!c.InBounds(map) || !AgentPlace.Visible(map, c))
				{
					continue;
				}
				List<Thing> things = c.GetThingList(map);
				for (int i = 0; i < things.Count; i++)
				{
					Thing t = things[i];
					if (t.Spawned && IsResource(t) && (forbiddenToo || !t.IsForbidden(Faction.OfPlayer)) && !t.IsInValidStorage() && !found.Contains(t) && (filter == null || filter(t)))
					{
						found.Add(t);
					}
				}
			}
			return found;
		}

		private static List<Thing> Resources(MicroJob job, Map map)
		{
			return Resources(job.region, map, JobFilter(job, map));
		}

		// A haul order that named THINGS picks up only those (a mining order hauls whatever its tiles dropped). Built once
		// per order and game, as the designation forms match words (AgentDesignations.WordFilter).
		private static readonly Dictionary<MicroJob, Func<Thing, bool>> filters = new Dictionary<MicroJob, Func<Thing, bool>>();

		private static Func<Thing, bool> JobFilter(MicroJob job, Map map)
		{
			if (!job.haul || job.things.Count == 0)
			{
				return null;
			}
			Func<Thing, bool> f;
			if (!filters.TryGetValue(job, out f))
			{
				f = AgentDesignations.WordFilter(map, "haul", job.things);
				filters[job] = f;
			}
			return f;
		}

		// The game's own unforbid (ForbidUtility.SetForbidden, as Designator_Unforbid does).
		private static void Unforbid(List<Thing> things)
		{
			foreach (Thing t in things)
			{
				if (t.IsForbidden(Faction.OfPlayer))
				{
					t.SetForbidden(false, false);
				}
			}
		}

		// "45 steel, 3 component industrial": counts by kind, most first.
		private static string CountList(IEnumerable<Thing> things)
		{
			return string.Join(", ", things.GroupBy((Thing t) => t.def).Select((IGrouping<ThingDef, Thing> g) => new KeyValuePair<ThingDef, int>(g.Key, g.Sum((Thing t) => t.stackCount)))
				.OrderByDescending((KeyValuePair<ThingDef, int> e) => e.Value).Select((KeyValuePair<ThingDef, int> e) => e.Value + " " + e.Key.label).ToArray());
		}

		// What the pawn holds: its inventory and the stack in its hands.
		private static List<Thing> Held(Pawn p)
		{
			List<Thing> held = new List<Thing>();
			if (p.inventory != null)
			{
				held.AddRange(p.inventory.innerContainer.Where(IsResource));
			}
			if (p.carryTracker != null && p.carryTracker.CarriedThing != null && IsResource(p.carryTracker.CarriedThing))
			{
				held.Add(p.carryTracker.CarriedThing);
			}
			return held;
		}

		// "Bob: 45 steel; Ann: 30 gold".
		private static string InventoryList(MicroJob job)
		{
			List<string> parts = new List<string>();
			foreach (MicroPawn mp in job.pawns)
			{
				if (mp.pawn == null || mp.pawn.Dead)
				{
					continue;
				}
				List<Thing> held = Held(mp.pawn);
				parts.Add(mp.pawn.LabelShort + ": " + (held.Count > 0 ? CountList(held) : "nothing"));
			}
			return string.Join("; ", parts.ToArray());
		}

		// ---- running ----------------------------------------------------------------------------------------------

		private static void Start(Map map, bool haul, Site site, List<ThingDef> kinds, List<string> things, List<MicroPawn> going)
		{
			MicroData d = Data;
			if (d == null)
			{
				throw new Fault("The game has no place to keep the order.");
			}
			// A newer order on the same place replaces the leftover reminder of an older one.
			d.jobs.RemoveAll((MicroJob j) => j.finished && j.mapId == map.uniqueID && j.place == site.place);
			MicroJob job = new MicroJob();
			job.id = d.nextId++;
			job.haul = haul;
			job.mapId = map.uniqueID;
			job.place = site.place;
			job.region = site.region;
			job.things = new List<string>(things);
			if (kinds.Count > 0)
			{
				job.resource = kinds[0].defName;
				job.allowed.AddRange(kinds.Select((ThingDef k) => k.defName));
			}
			string firstWhy = null;
			foreach (MicroPawn mp in going)
			{
				bool started = haul ? TryHaul(job, mp, map) : StartMining(map, mp, mp.target, out firstWhy);
				if (haul || started)
				{
					job.pawns.Add(mp);
				}
			}
			if (job.pawns.Count == 0)
			{
				throw new Fault(firstWhy ?? "The game gave nobody that order.");
			}
			d.jobs.Add(job);
		}

		// Marks the tile (the game's Designator_Mine, RimWorld\Designator_Mine.cs:31-82) and gives the pawn the game's
		// "Prioritize mining" order on it (AgentPawnActions.FindWorkOption).
		private static bool StartMining(Map map, MicroPawn mp, IntVec3 tile, out string why)
		{
			why = null;
			if (map != Find.CurrentMap)
			{
				why = "Switch the view to that map first; the game only gives orders on the map you look at.";
				return false;
			}
			try
			{
				Designator_Mine designator = new Designator_Mine();
				if (designator.CanDesignateCell(tile).Accepted)
				{
					designator.DesignateSingleCell(tile);
				}
				AgentPawnActions.WorkHit hit = AgentPawnActions.FindWorkOption(mp.pawn, new List<IntVec3> { tile }, AgentPawnActions.WorkMatcher("mining"));
				if (hit == null || hit.option.Disabled)
				{
					why = hit != null ? AgentPawnActions.Why(hit.option) : mp.pawn.LabelShort + " can't mine " + Xz(tile) + " right now.";
					return false;
				}
				AgentPawnActions.Choose(hit.option);
				mp.target = tile;
				mp.phase = PhaseMining;
				return true;
			}
			catch (Fault fault)
			{
				why = fault.Message;
				return false;
			}
		}

		// The game's "Prioritize hauling" order on one of the resources, Pick Up And Haul's inventory haul first (its work
		// giver HaulToInventory, Defs\JobDefs\WorkGiver.xml); the ones that just dropped first, then the nearest. The cells
		// searched: the region and the 8x8 around the pawn.
		private static bool TryHaul(MicroJob job, MicroPawn mp, Map map)
		{
			Pawn p = mp.pawn;
			List<Thing> items = Resources(job, map);
			// The 8x8 around the pawn only once it is at the site: a pawn still at home would haul what lies there.
			if (NearSite(job, p))
			{
				foreach (Thing t in Resources(Square(map, p.Position), map, JobFilter(job, map)))
				{
					if (!items.Contains(t))
					{
						items.Add(t);
					}
				}
			}
			items = items.OrderBy((Thing t) => job.dropped.Contains(t) ? 0 : 1).ThenBy((Thing t) => t.Position.DistanceToSquared(p.Position)).Take(HaulCandidates).ToList();
			foreach (Thing t in items)
			{
				FloatMenuOption option = HaulOption(p, t);
				if (option != null)
				{
					try
					{
						AgentPawnActions.Choose(option);
						return true;
					}
					catch (Fault)
					{
					}
				}
			}
			return false;
		}

		private static FloatMenuOption HaulOption(Pawn p, Thing t)
		{
			List<FloatMenuOption> options;
			try
			{
				options = AgentPawnActions.RightClick(new FloatMenuOptionProvider_WorkGivers(), new List<Pawn> { p }, t, t.Position);
			}
			catch (Fault)
			{
				return null;
			}
			FloatMenuOption any = null;
			foreach (FloatMenuOption o in options)
			{
				if (o == null || o.Disabled)
				{
					continue;
				}
				WorkGiverDef giver = AgentPawnActions.GiverOf(o);
				if (giver == null || giver.workType != WorkTypeDefOf.Hauling)
				{
					continue;
				}
				if (giver.defName == "HaulToInventory")
				{
					return o;
				}
				any = any ?? o;
			}
			return any;
		}

		private static void StartHauling(MicroJob job, MicroPawn mp, Map map)
		{
			if (WhyNot(mp.pawn, true) != null)
			{
				mp.phase = PhaseDone;   // "unless hauling is disabled/turned off for this pawn"
				return;
			}
			mp.phase = PhaseHauling;
			TryHaul(job, mp, map);
		}

		// 0.3 s after a tile was mined: the next tile, the other-resources question, or hauling.
		private static void AfterMined(MicroJob job, MicroPawn mp, Map map)
		{
			if (mp.mined < mp.quota)
			{
				List<IntVec3> next = new List<IntVec3>();
				Dictionary<ThingDef, List<IntVec3>> near = YieldsNear(job, map);
				foreach (string defName in job.allowed)
				{
					ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
					if (def == null)
					{
						continue;
					}
					List<IntVec3> cells;
					if (near.TryGetValue(def, out cells))
					{
						next.AddRange(cells);
					}
				}
				HashSet<IntVec3> taken = new HashSet<IntVec3>(job.pawns.Where((MicroPawn o) => o != mp && o.phase == PhaseMining).Select((MicroPawn o) => o.target));
				foreach (IntVec3 tile in next.Where((IntVec3 t) => !taken.Contains(t) && mp.pawn.CanReach(t, PathEndMode.Touch, Danger.Deadly))
					.OrderBy((IntVec3 t) => t.DistanceToSquared(mp.pawn.Position)).ToList())
				{
					string why;
					if (StartMining(map, mp, tile, out why))
					{
						return;
					}
				}
				if (!job.askedMore)
				{
					Dictionary<ThingDef, List<IntVec3>> others = OtherYields(job, map);
					if (others.Count > 0)
					{
						mp.phase = PhaseWaitAnswer;
						if (!job.asking)
						{
							job.asking = true;
							job.question = MoreQuestion(job.place, string.Join(", ", others.Select((KeyValuePair<ThingDef, List<IntVec3>> e) => e.Key.label + " (" + e.Value.Count + (e.Value.Count == 1 ? " tile" : " tiles") + ")").ToArray()));
							AskMore(job);
						}
						return;
					}
				}
			}
			else if (!job.veinTold)
			{
				List<IntVec3> left = new List<IntVec3>();
				Dictionary<ThingDef, List<IntVec3>> near = YieldsNear(job, map);
				ThingDef def = job.resource != null ? DefDatabase<ThingDef>.GetNamedSilentFail(job.resource) : null;
				if (def != null && near.TryGetValue(def, out left) && left.Count > 0 && job.pawns.All((MicroPawn o) => o == mp || o.phase != PhaseMining))
				{
					job.veinTold = true;
					Data.messages.Add(VeinLine(def.label, left.OrderBy((IntVec3 c) => c.DistanceToSquared(mp.pawn.Position)).First(), left.Count));
				}
			}
			StartHauling(job, mp, map);
		}

		// The other-resources question: the game pauses (the inbox gives the speed back once nothing waits), and the
		// question goes out with the next result and in the inbox state file. One question at a time: a later one waits.
		private static void AskMore(MicroJob job)
		{
			if (question != null)
			{
				return;
			}
			question = new Question { kind = "more", text = job.question, jobId = job.id };
			AskCount++;
			Data.messages.Add(job.question);
			AgentInbox.PauseForQuestion();
		}

		// Mineable.DestroyMined (MineYieldPatch): a tile of a job was mined; what dropped is kept for the haul.
		internal static void Mined(Map map, IntVec3 cell, Pawn miner, List<Thing> dropped)
		{
			MicroData d = Data;
			if (d == null)
			{
				return;
			}
			foreach (MicroJob job in d.jobs)
			{
				if (job.finished || job.haul || job.mapId != map.uniqueID)
				{
					continue;
				}
				MicroPawn mp = job.pawns.FirstOrDefault((MicroPawn p) => p.phase == PhaseMining && p.target == cell)
					?? job.pawns.FirstOrDefault((MicroPawn p) => p.pawn == miner && p.phase == PhaseMining && job.RegionSet.Contains(cell));
				if (mp == null)
				{
					continue;
				}
				mp.mined++;
				mp.target = IntVec3.Invalid;
				mp.phase = PhaseDelay;
				mp.delay = HaulDelaySeconds;
				job.minedCells.Add(cell);
				job.dropped.AddRange(dropped.Where((Thing t) => !job.dropped.Contains(t)));
				return;
			}
		}

		internal static bool Watching(Map map)
		{
			MicroData d = Data;
			return d != null && d.jobs.Any((MicroJob j) => !j.finished && !j.haul && j.mapId == map.uniqueID);
		}

		// GameComponentUpdate, every frame. The 0.3 s step and the 3 s check count real seconds while the game runs.
		public static void Update()
		{
			try
			{
				if (Current.ProgramState != ProgramState.Playing || Current.Game == null || Find.TickManager == null)
				{
					return;
				}
				if (seenGame != Current.Game)
				{
					seenGame = Current.Game;
					question = null;
					filters.Clear();
					rotSites.Clear();
					checkTimer = 0f;
					MicroData loaded = Data;
					MicroJob asking = loaded != null ? loaded.jobs.FirstOrDefault((MicroJob j) => j.asking && !j.finished) : null;
					if (asking != null)
					{
						question = new Question { kind = "more", text = asking.question, jobId = asking.id };
					}
				}
				MicroData d = Data;
				if (d == null || d.jobs.Count == 0 || Find.TickManager.Paused)
				{
					return;
				}
				float dt = Mathf.Min(Time.unscaledDeltaTime, 1f);
				foreach (MicroJob job in d.jobs.Where((MicroJob j) => !j.finished).ToList())
				{
					foreach (MicroPawn mp in job.pawns.Where((MicroPawn p) => p.phase == PhaseDelay).ToList())
					{
						mp.delay -= dt;
						if (mp.delay <= 0f)
						{
							Map map = MapOf(job);
							if (map == null || mp.pawn == null || mp.pawn.Dead || !mp.pawn.Spawned)
							{
								mp.phase = PhaseDone;
								continue;
							}
							AfterMined(job, mp, map);
						}
					}
				}
				checkTimer += dt;
				if (checkTimer >= CheckSeconds)
				{
					checkTimer = 0f;
					Check(d);
				}
			}
			catch (Exception ex)
			{
				if (loggedFaults.Add("update"))
				{
					Log.Error("[VerbalCommands] micro orders: " + ex);
				}
			}
		}

		// Every 3 s while the game runs.
		private static void Check(MicroData d)
		{
			foreach (MicroJob job in d.jobs.Where((MicroJob j) => !j.finished).ToList())
			{
				Map map = MapOf(job);
				if (map == null)
				{
					d.jobs.Remove(job);
					continue;
				}
				if (job.asking && question == null)
				{
					AskMore(job);
				}
				foreach (MicroPawn mp in job.pawns)
				{
					Pawn p = mp.pawn;
					if (mp.phase == PhaseDone)
					{
						continue;
					}
					if (p == null || p.Dead || !p.Spawned || p.Map != map)
					{
						mp.phase = PhaseDone;
						continue;
					}
					if (mp.phase == PhaseMining)
					{
						CheckMining(job, mp, map);
					}
					else if (mp.phase == PhaseHauling)
					{
						CheckHauling(job, mp, map);
					}
				}
				if (job.pawns.All((MicroPawn mp) => mp.phase == PhaseDone))
				{
					Finish(d, job, map);
				}
			}
		}

		// What the 3 s check must not break. User, 2026-10-16: "Override eating and sleeping. Draft, mental break, stays."
		// Kept too (not ruled on): fleeing, putting out fire on itself, and bed rest until healed (a patient's LayDown with
		// Job.restUntilHealed is not sleep).
		private static bool Busy(Pawn p)
		{
			if (p.Drafted || p.InMentalState || p.Downed)
			{
				return true;
			}
			Job j = p.CurJob;
			if (j == null)
			{
				return false;
			}
			return j.def == JobDefOf.Flee || j.def == JobDefOf.FleeAndCower || j.def == JobDefOf.ExtinguishSelf
				|| (j.restUntilHealed && (j.def == JobDefOf.LayDown || j.def == JobDefOf.LayDownResting));
		}

		private static void CheckMining(MicroJob job, MicroPawn mp, Map map)
		{
			Pawn p = mp.pawn;
			if (!mp.target.IsValid || mp.target.GetFirstMineable(map) == null)
			{
				// Gone without the game's mining step (blown up, or mined by a step the patch did not see).
				mp.target = IntVec3.Invalid;
				mp.phase = PhaseDelay;
				mp.delay = 0f;
				return;
			}
			Job j = p.CurJob;
			if (j != null && j.def == JobDefOf.Mine && j.targetA.Cell == mp.target)
			{
				return;
			}
			if (Busy(p) || (j != null && j.playerForced))
			{
				return;
			}
			string why;
			StartMining(map, mp, mp.target, out why);
		}

		private static bool NearSite(MicroJob job, Pawn p)
		{
			return job.RegionSet.Contains(p.Position) || job.region.Any((IntVec3 c) => c.InHorDistOf(p.Position, ArriveTiles));
		}

		// On a haul job that picks up something lying in the region (the targets of the job, Pick Up And Haul's queue too).
		private static bool HaulingHere(MicroJob job, Pawn p)
		{
			Job j = p.CurJob;
			if (j == null)
			{
				return false;
			}
			if (j.def != JobDefOf.HaulToCell && j.def != JobDefOf.HaulToContainer && j.def != JobDefOf.TakeInventory && j.def != JobDefOf.TakeCountToInventory && j.def.defName != "HaulToInventory")
			{
				return false;
			}
			if (p.carryTracker != null && p.carryTracker.CarriedThing != null)
			{
				return false;   // on the way to storage
			}
			if (j.targetA.HasThing && job.RegionSet.Contains(j.targetA.Thing.Position))
			{
				return true;
			}
			return j.targetQueueA != null && j.targetQueueA.Any((LocalTargetInfo t) => t.HasThing && t.Thing.Spawned && job.RegionSet.Contains(t.Thing.Position));
		}

		private static void CheckHauling(MicroJob job, MicroPawn mp, Map map)
		{
			Pawn p = mp.pawn;
			if (job.haul && !mp.arrived)
			{
				// "reuse hauling gate part of mining AFTER the pawns have arrived near the area"
				if (NearSite(job, p))
				{
					mp.arrived = true;
				}
				else
				{
					if (!HaulingHere(job, p) && !Busy(p) && !(p.CurJob != null && p.CurJob.playerForced))
					{
						TryHaul(job, mp, map);
					}
					return;
				}
			}
			List<Thing> left = Resources(job, map);
			if (left.Count == 0)
			{
				mp.phase = PhaseDone;   // nothing left to haul
				return;
			}
			// A stack in the hands (the vanilla haul): full by the game's own rule, Pawn_CarryTracker.Full (the item's stack
			// limit or carrying capacity / volume per unit, Verse\Pawn_CarryTracker.cs:28, 53-67), or when nothing left
			// stacks with it (the hands hold one kind). Not full: the game's haul tops it up from same-kind stacks within
			// 8 tiles itself (Toils_Haul.CheckForGetOpportunityDuplicate, Verse.AI\Toils_Haul.cs:192-215), and a new order
			// would end the carry and drop the stack, so the check waits; once delivered the pawn is sent back for the rest.
			Thing carried = p.carryTracker != null ? p.carryTracker.CarriedThing : null;
			if (carried != null)
			{
				if (p.carryTracker.Full || !left.Any((Thing t) => t.CanStackWith(carried)))
				{
					mp.phase = PhaseDone;
				}
				return;
			}
			bool full = left.All((Thing t) => MassUtility.CountToPickUpUntilOverEncumbered(p, t) <= 0);
			if (full)
			{
				mp.phase = PhaseDone;
				return;
			}
			if (HaulingHere(job, p) || Busy(p))
			{
				return;
			}
			// "If theres resources inside that 8x8 box, haul until pawn cannot carry more." (user, 2026-10-16)
			TryHaul(job, mp, map);
		}

		private static void Finish(MicroData d, MicroJob job, Map map)
		{
			job.finished = true;
			job.dropped.Clear();
			float hours = job.pawns.Where((MicroPawn mp) => mp.pawn != null && !mp.pawn.Dead).Select((MicroPawn mp) => mp.travelHours).DefaultIfEmpty(0f).Max();
			d.messages.Add(job.haul ? HaulDone(InventoryList(job)) : MineDone(hours, InventoryList(job)));
			if (Resources(job, map).Count == 0)
			{
				d.jobs.Remove(job);
			}
		}

		// ---- small helpers ----------------------------------------------------------------------------------------

		private static MicroJob JobById(int id)
		{
			MicroData d = Data;
			return d != null ? d.jobs.FirstOrDefault((MicroJob j) => j.id == id) : null;
		}

		private static Map MapOf(MicroJob job)
		{
			return Find.Maps.FirstOrDefault((Map m) => m.uniqueID == job.mapId);
		}

		private static string Xz(IntVec3 c)
		{
			return AgentWorkspotCheck.Xz(c);
		}

		private static string Hours(float h)
		{
			return h.ToString("0.0", CultureInfo.InvariantCulture);
		}
	}

	// One colonist of a micro order.
	public class MicroPawn : IExposable
	{
		public Pawn pawn;
		public int quota;                          // tiles to mine (0 for a haul order)
		public int mined;
		public IntVec3 target = IntVec3.Invalid;   // the tile being mined
		public int phase;                          // AgentMicro.Phase*
		public float delay;                        // real seconds left before the step after a mined tile
		public float travelHours;                  // to the site when planned; the way back is taken as the same
		public bool arrived;                       // haul order: has been at the site

		public void ExposeData()
		{
			Scribe_References.Look(ref pawn, "pawn");
			Scribe_Values.Look(ref quota, "quota");
			Scribe_Values.Look(ref mined, "mined");
			Scribe_Values.Look(ref target, "target", IntVec3.Invalid);
			Scribe_Values.Look(ref phase, "phase");
			Scribe_Values.Look(ref delay, "delay");
			Scribe_Values.Look(ref travelHours, "travelHours");
			Scribe_Values.Look(ref arrived, "arrived");
		}
	}

	// One micro order: its place, the area scan, the pawns.
	public class MicroJob : IExposable
	{
		public int id;
		public bool haul;
		public int mapId;
		public string place;                                   // "112, 87"
		public List<IntVec3> region = new List<IntVec3>();     // the area scan
		public string resource;                                // the yield of the first tile (defName)
		public List<string> allowed = new List<string>();      // the yields the pawns may mine
		public List<string> things = new List<string>();       // the THINGS the order named (a haul order picks up only those)
		public bool askedMore;
		public bool asking;
		public string question;
		public bool veinTold;
		public bool finished;                                  // done; kept while resources lie in the region
		public List<IntVec3> minedCells = new List<IntVec3>();
		public List<Thing> dropped = new List<Thing>();        // what the mined tiles dropped
		public List<MicroPawn> pawns = new List<MicroPawn>();

		private HashSet<IntVec3> regionSet;

		public HashSet<IntVec3> RegionSet
		{
			get
			{
				if (regionSet == null || regionSet.Count != region.Count)
				{
					regionSet = new HashSet<IntVec3>(region);
				}
				return regionSet;
			}
		}

		public void ExposeData()
		{
			Scribe_Values.Look(ref id, "id");
			Scribe_Values.Look(ref haul, "haul");
			Scribe_Values.Look(ref mapId, "mapId");
			Scribe_Values.Look(ref place, "place");
			Scribe_Collections.Look(ref region, "region", LookMode.Value);
			Scribe_Values.Look(ref resource, "resource");
			Scribe_Collections.Look(ref allowed, "allowed", LookMode.Value);
			Scribe_Collections.Look(ref things, "things", LookMode.Value);
			Scribe_Values.Look(ref askedMore, "askedMore");
			Scribe_Values.Look(ref asking, "asking");
			Scribe_Values.Look(ref question, "question");
			Scribe_Values.Look(ref veinTold, "veinTold");
			Scribe_Values.Look(ref finished, "finished");
			Scribe_Collections.Look(ref minedCells, "minedCells", LookMode.Value);
			Scribe_Collections.Look(ref dropped, "dropped", LookMode.Reference);
			Scribe_Collections.Look(ref pawns, "pawns", LookMode.Deep);
			if (Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				region = region ?? new List<IntVec3>();
				allowed = allowed ?? new List<string>();
				things = things ?? new List<string>();
				minedCells = minedCells ?? new List<IntVec3>();
				dropped = (dropped ?? new List<Thing>()).Where((Thing t) => t != null).ToList();
				pawns = (pawns ?? new List<MicroPawn>()).Where((MicroPawn p) => p != null).ToList();
				regionSet = null;
			}
		}
	}

	// The micro orders of a game, saved with it (GameComponent_VerbalCommands, "vcMicro").
	public class MicroData : IExposable
	{
		public int nextId = 1;
		public List<MicroJob> jobs = new List<MicroJob>();
		public List<string> messages = new List<string>();

		public void ExposeData()
		{
			Scribe_Values.Look(ref nextId, "nextId", 1);
			Scribe_Collections.Look(ref jobs, "jobs", LookMode.Deep);
			Scribe_Collections.Look(ref messages, "messages", LookMode.Value);
			if (Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				jobs = (jobs ?? new List<MicroJob>()).Where((MicroJob j) => j != null).ToList();
				messages = messages ?? new List<string>();
			}
		}
	}

	// The game's mining step, Mineable.DestroyMined(Pawn) (RimWorld\Mineable.cs:39-47): it destroys the rock and spawns the
	// yield with GenPlace.TryPlaceThing Near the cell (:66-80), which may also add to a stack already lying there. The
	// prefix notes the items around the cell and their counts, the postfix the same again: new things, or stacks that grew,
	// are what dropped. Only while a micro mining order runs on that map. Patched by hand, like MerchantArrivalPatch.
	internal static class MineYieldPatch
	{
		private const float Radius = 4.9f;

		internal sealed class State
		{
			public Map map;
			public IntVec3 cell;
			public Dictionary<Thing, int> before;
		}

		public static void Apply()
		{
			try
			{
				MethodInfo original = typeof(Mineable).GetMethod("DestroyMined", BindingFlags.Public | BindingFlags.Instance);
				if (original == null)
				{
					Log.Warning("[VerbalCommands] Mineable.DestroyMined not found; micro mining can't see mined tiles.");
					return;
				}
				Harmony harmony = new Harmony("kopaz.verbalcommands.micromine");
				harmony.Patch(original,
					new HarmonyMethod(typeof(MineYieldPatch).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
					new HarmonyMethod(typeof(MineYieldPatch).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)));
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] Could not patch Mineable.DestroyMined (is Harmony loaded?): " + ex.Message);
			}
		}

		private static Dictionary<Thing, int> Around(Map map, IntVec3 cell)
		{
			Dictionary<Thing, int> d = new Dictionary<Thing, int>();
			foreach (Thing t in GenRadial.RadialDistinctThingsAround(cell, map, Radius, true))
			{
				if (t.def.category == ThingCategory.Item)
				{
					d[t] = t.stackCount;
				}
			}
			return d;
		}

		public static void Prefix(Mineable __instance, out State __state)
		{
			__state = null;
			try
			{
				Map map = __instance.Map;
				if (map == null || !AgentMicro.Watching(map))
				{
					return;
				}
				__state = new State { map = map, cell = __instance.Position, before = Around(map, __instance.Position) };
			}
			catch (Exception)
			{
				__state = null;
			}
		}

		public static void Postfix(Pawn pawn, State __state)
		{
			if (__state == null)
			{
				return;
			}
			try
			{
				List<Thing> dropped = new List<Thing>();
				foreach (KeyValuePair<Thing, int> e in Around(__state.map, __state.cell))
				{
					int was;
					if (!__state.before.TryGetValue(e.Key, out was) || e.Value > was)
					{
						dropped.Add(e.Key);
					}
				}
				AgentMicro.Mined(__state.map, __state.cell, pawn, dropped);
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] micro mining: " + ex.Message);
			}
		}
	}
}
