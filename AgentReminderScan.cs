using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace VerbalCommands
{
	// The orchestrator's 6-hour reminders, one batch per scan: work spots / benches (below), light at their interaction
	// spots (LightLine below), room temperatures (AgentRoomTempScan.cs; user, 2026-10-14: "1 more order. same trigger
	// mechanism"), stone chunks indoors (AgentChunks.cs; replaced ChunkLine, user 2026-10-16) and steel / component /
	// plasteel sources on the map while the count is 0 or under what the bills need (AgentResourceScan.cs), a stove
	// while only a campfire cooks (StoveLine below), a simple meal x1 bill with no x4 bill (BulkMealLine below), and too few benches for the colonists working at the same hours or
	// stockpiled chunks with no stonecutter's table (AgentBenchScan.cs), two or more blueprints waiting (BlueprintLine
	// below) and colonists likely to botch builds or harvests (AgentWorkChanceScan.cs), in that order. Growing zones / plant growers with cells growing nothing (AgentGrowScan.cs) are scanned every
	// 3 game hours instead (GrowTick below) and follow the 6-hour lines, or go out alone as a "3-hour check". In the scan the work spot lines leave darkness out: the light line covers it.
	// Once a game day (the first scan of a new day) the door placement lines from the colonists' walks follow
	// (AgentDoorPathScan.cs); they wait apart from the 6-hour lines until delivered or replaced by the next day's.
	// A colonist's mental break with no prisoner bed to arrest them into is checked every 60 ticks instead (BreakLine
	// below) and goes out with the next batch, or alone as a "Mental break check".
	//
	// Work spot / bench reminder for the orchestrator (user, 2026-10-14: "find out if the dll can already scan for
	// furnitures/spots being placed in suboptimal area and design a reminder inject for orchestrator as well. prbably in
	// line of [reminder] - You have [building] placed in [room_label, coordinate] which has work speed penalty of [rate].
	// consider re-placing them. have it scan every 6H of ingame days. injection gets sent out in a batch, so if 2
	// buildings are placed in suboptimal area it fires off once with 2 lines."; delivery option B, user: "B.").
	//
	// Scan: every time the game clock enters a new 6-hour slot (TicksGame / 15000), not at load; AgentEvents.Tick calls
	// Tick. Every colony work table or research bench on a home map (built, frame or blueprint) whose place costs work
	// speed (AgentWorkspotCheck.Penalties: in the open, bad temperature, wrong room role, dark; power is left out, it
	// is not where the bench stands) gives one line. One scan = one batch; a newer scan replaces a batch not yet given.
	//
	// Delivery (option B): the mod cannot tell the orchestrator from a specialist (they share the session id, so the
	// inbox file names are the same). The batch goes out once, on the first result whose actions include an order only
	// the orchestrator sends (agent_player\delegates\ORCHESTRATOR.md: speed, letters and windows stay the
	// orchestrator's (section 4, section 1 last line); trade is orchestrator only (section 2); a build the orchestrator
	// sends itself (section 3)). Bills, attacks, draft and move are left out: a specialist sends those itself.
	// It goes into the outbox field "reminders" as "[REMINDER] ..." lines (only there since 2026-10-19, user: "no
	// duplicates."; it was in answers and reasons too); it is not a tip
	// (no token gap, no cap, not turned off by the "Agent tips" setting). No event line: the label of a campfire would
	// wake the drafter's wait on "fire".
	//
	// CHANGED 2026-10-16 (hourly check): the work spot and light lines, the room temperature and vent lines, the stove,
	// bulk meal, bench, blueprint, botched build and doctor medicine lines, and the 3-hour deterioration alert moved to
	// the hourly check (HourlyLines below; AgentHourlyCheck.cs: batched, pauses the game, goes to every agent, unpaused
	// by every agent's vote). User's words: "1,2,3,4,7,9,10,12,13,14, and 2 fires EVERY ONE HOUR. BATCHED. AND PAUSES THE
	// GAME. GOES TO EVERY SINGLE AGENT ON THE GODDAMN LIST. BOTH SUBAGENT AND ORCHESTRATOR." The 6-hour batch keeps the
	// stone chunk, resource, heater, stonecutter, weapon and micro leftover lines and the draft tip; the 3-hour one the
	// grow lines. The botched-build window (AgentWorkChanceScan.MarkScanned) now runs from one hourly scan to the next.
	public static class AgentReminderScan
	{
		private const int SlotTicks = GenDate.TicksPerHour * 6;   // 15000
		private const int MaxLines = 20;

		// Forms only the orchestrator sends (plan.toolName, the "form" of an outbox action).
		private static readonly HashSet<string> OrchestratorForms = new HashSet<string>
		{
			"game speed", "run", "letter choose", "agent window close", "agent trade accept", "agent trade set",
			BuildTools.PlaceBlueprintTool, BuildTools.DrawPlanTool, BuildTools.BuildOptionTool
		};
		private static readonly string[] OrchestratorFormStarts = new string[] { "agent build ", "agent place " };

		private const int SlotsPerDay = GenDate.TicksPerDay / SlotTicks;   // 4

		private static int lastSlot = int.MinValue;
		private static List<GatedLine> pending = new List<GatedLine>();
		// The daily door lines (AgentDoorPathScan.cs; user: "reminder fires every day"): kept apart so a 6-hour scan
		// does not replace them; the next day's lines do.
		private static List<string> doorPending = new List<string>();
		// The 12-hour room list (AgentRoomScan.cs; user, 2026-10-17): its own block, made at every second 6-hour scan and
		// given with the next batch; a newer one replaces it.
		private static string roomPending;

		// Settings gates (user, 2026-10-26): a 6-hour line is queued with the gate of its category (Base and building,
		// Pawn and work, Resource, Know-how tips), and TakeFor drops it if that gate is off by the time it is handed out.
		// The door and room lines are Base, the grow lines Food, the break / bleed / merchant lines Urgent: one gate for
		// the whole queue, checked in TakeFor.
		private sealed class GatedLine
		{
			public readonly string Line;
			public readonly Func<bool> Gate;

			public GatedLine(string line, Func<bool> gate)
			{
				Line = line;
				Gate = gate;
			}
		}

		private static readonly Func<bool> BaseGate = () => VerbalCommandsMod.BaseRemindersOn;
		private static readonly Func<bool> PawnGate = () => VerbalCommandsMod.PawnRemindersOn;
		private static readonly Func<bool> ResourceGate = () => VerbalCommandsMod.ResourceRemindersOn;
		private static readonly Func<bool> TipsGate = () => VerbalCommandsMod.TipsOn;

		public static void Reset()
		{
			lastSlot = int.MinValue;
			pending = new List<GatedLine>();
			doorPending = new List<string>();
			roomPending = null;
			scannedAt = "";
			breakSeen.Clear();
			breakPending = new List<Pawn>();
			bleedSeen.Clear();
			bleedPending = new List<Pawn>();
			merchantSeen.Clear();
			merchantPending = new List<Pawn>();
			lastDraftSpot = new Dictionary<Pawn, IntVec3>();
			lastGrowSlot = int.MinValue;
			growPending = new List<string>();
			growScannedAt = "";
			AgentDeteriorationScan.Reset();
			AgentWorkChanceScan.MarkScanned(-1);
			AgentResourceScan.Reset();
			AgentHourlyCheck.Reset();
		}

		// The growing zone / grower lines (AgentGrowScan.cs) every 3 game hours, not 6 (user, 2026-10-14: "this one should
		// be every 3 ingame hour and not 6 (so, half)"): their own slot, kept apart like the door lines; a newer 3-hour
		// scan replaces lines not yet given. A 6-hour mark is also a 3-hour mark, so then they join that batch.
		private const int GrowSlotTicks = GenDate.TicksPerHour * 3;   // 7500
		private static int lastGrowSlot = int.MinValue;
		private static List<string> growPending = new List<string>();
		private static string growScannedAt = "";

		private static void GrowTick()
		{
			int slot = Find.TickManager.TicksGame / GrowSlotTicks;
			if (lastGrowSlot == int.MinValue)
			{
				lastGrowSlot = slot;   // a load: the first scan is at the next slot
				return;
			}
			if (slot == lastGrowSlot)
			{
				return;
			}
			lastGrowSlot = slot;
			List<string> lines = new List<string>();
			foreach (Map map in Find.Maps)
			{
				if (map != null && map.IsPlayerHome)
				{
					// Settings gate (user, 2026-10-26): Food and Farming reminders. Off: no scan; an empty result still replaces the old lines, as a newer scan does.
					if (VerbalCommandsMod.FoodRemindersOn)
					{
						lines.AddRange(AgentGrowScan.Lines(map));
					}
					// The deterioration alert moved to the hourly check (AgentHourlyCheck.cs, 2026-10-16).
				}
			}
			growPending = lines;
			growScannedAt = ScanTime();
			AgentReminderLog.Scan("3-hour check", growScannedAt, lines);
		}

		// AgentEvents.Tick (every 60 ticks). Main thread.
		public static void Tick()
		{
			BreakTick();
			BleedTick();
			GrowTick();
			AgentHourlyCheck.Tick();
			int slot = Find.TickManager.TicksGame / SlotTicks;
			if (lastSlot == int.MinValue)
			{
				lastSlot = slot;   // a load: the first scan is at the next slot
				return;
			}
			if (slot == lastSlot)
			{
				return;
			}
			bool newDay = slot / SlotsPerDay != lastSlot / SlotsPerDay;
			lastSlot = slot;
			pending = Scan();
			scannedAt = ScanTime();
			AgentReminderLog.Scan("6-hour check", scannedAt, pending.Select((GatedLine g) => g.Line).ToList());
			if (slot % 2 == 0)
			{
				// Settings gate (user, 2026-10-26): Base and building reminders (12-hour room list). Off: no scan, as if there was nothing to say.
				string rooms = VerbalCommandsMod.BaseRemindersOn ? AgentRoomScan.Text() : null;
				if (rooms != null)
				{
					roomPending = rooms;
					AgentReminderLog.Scan("12-hour check", scannedAt, rooms.Split('\n').ToList());
				}
			}
			if (newDay)
			{
				// Settings gate (user, 2026-10-26): Base and building reminders (daily door lines; the pathfindCost check inside DailyLines stays). Off: no scan (it records suggestions), and the old lines are replaced by none.
				doorPending = VerbalCommandsMod.BaseRemindersOn ? AgentDoorPathScan.DailyLines() : new List<string>();
				AgentReminderLog.Scan("Daily door check", scannedAt, doorPending);
			}
		}

		// The batch for this result, or an empty list: only when an action of it is an orchestrator-only form. Taken once.
		public static List<string> TakeFor(JArray actions)
		{
			if ((pending.Count == 0 && doorPending.Count == 0 && growPending.Count == 0 && breakPending.Count == 0 && bleedPending.Count == 0 && merchantPending.Count == 0 && roomPending == null) || actions == null)
			{
				return new List<string>();
			}
			bool orchestrator = false;
			foreach (JToken a in actions)
			{
				string form = a is JObject ? (string)a["form"] : null;
				if (form != null && (OrchestratorForms.Contains(form) || OrchestratorFormStarts.Any((string s) => form.StartsWith(s, StringComparison.Ordinal))))
				{
					orchestrator = true;
					break;
				}
			}
			if (!orchestrator)
			{
				return new List<string>();
			}
			// Settings gates (user, 2026-10-26): a line queued while its gate was on is dropped here if the gate is off by now.
			// The 6-hour lines each keep their own gate (GatedLine); the door and room lines are Base, the grow lines Food, the
			// break / bleed / merchant lines Urgent. The titles below follow what is left.
			bool baseOn = VerbalCommandsMod.BaseRemindersOn;
			bool foodOn = VerbalCommandsMod.FoodRemindersOn;
			List<string> batch = pending.Where((GatedLine g) => g.Gate()).Select((GatedLine g) => g.Line).ToList();
			bool scanned = batch.Count > 0 || (baseOn && doorPending.Count > 0);
			bool grown = foodOn && growPending.Count > 0;
			if (baseOn)
			{
				batch.AddRange(doorPending);
			}
			if (foodOn)
			{
				batch.AddRange(growPending);
			}
			if (!VerbalCommandsMod.UrgentRemindersOn)
			{
				// Urgent lines dropped here are also forgotten (the seen-sets), so turning the gate on during the same event reports it.
				breakSeen.ExceptWith(breakPending);
				bleedSeen.ExceptWith(bleedPending);
				merchantSeen.ExceptWith(merchantPending);
				breakPending.Clear();
				bleedPending.Clear();
				merchantPending.Clear();
			}
			// Mental breaks are checked again now: one that ended, or got a prisoner bed since, is dropped.
			bool breaks = false;
			foreach (Pawn p in breakPending)
			{
				string line = BreakLine(p);
				if (line != null)
				{
					batch.Add(line);
					breaks = true;
				}
			}
			// Bleeding strangers too: one rescued, tended or dead since is dropped.
			bool bleeds = false;
			foreach (Pawn p in bleedPending)
			{
				string line = BleedLine(p);
				if (line != null)
				{
					batch.Add(line);
					bleeds = true;
				}
			}
			// Merchants in the home zone; one gone since is dropped and forgotten (a later visit fires again).
			bool merchants = false;
			foreach (Pawn p in merchantPending)
			{
				string line = MerchantLine(p);
				if (line != null)
				{
					batch.Add(line);
					merchants = true;
				}
			}
			merchantSeen.RemoveWhere((Pawn p) => p == null || !p.Spawned || p.Dead);
			pending = new List<GatedLine>();
			doorPending = new List<string>();
			growPending = new List<string>();
			breakPending = new List<Pawn>();
			bleedPending = new List<Pawn>();
			merchantPending = new List<Pawn>();
			List<string> result = new List<string>();
			string forms = string.Join(", ", actions.OfType<JObject>().Select((JObject a) => (string)a["form"]).Where((string f) => f != null).Distinct().ToArray());
			if (roomPending != null)
			{
				if (baseOn)
				{
					result.Add(roomPending);
					AgentReminderLog.Delivered(roomPending.Split('\n')[0], forms);
				}
				roomPending = null;
			}
			if (batch.Count == 0)
			{
				return result;
			}
			int urgentKinds = (breaks ? 1 : 0) + (bleeds ? 1 : 0) + (merchants ? 1 : 0);
			string title = scanned ? "6-hour check" : grown ? "3-hour check" : urgentKinds > 1 ? "Urgent check"
				: bleeds ? "Bleeding check" : merchants ? "Merchant check" : "Mental break check";
			string when = scanned ? scannedAt : grown ? growScannedAt : ScanTime();
			string block = Block(batch, title, when);
			AgentReminderLog.Delivered(block.Split('\n')[0], forms);
			result.Insert(0, block);
			return result;
		}

		// Mental break with no prisoner bed (user, 2026-10-14: "[reminder] I see that your prisoner is on a mental break,
		// but has no prisoner bed. maybe make a bed spot, turn it into a prisoner bed, then arrest your pawn?" / "should be
		// a fairly self explanatory reminder tip with condition for you to add"). "Prisoner" read as the colonist to be
		// arrested. Checked every 60 ticks, not at the 6-hour scan (a break can be over by then); once per break.
		//   Condition: a free colonist on a home map is in a mental state; another colonist could arrest them (not downed,
		//   not in a mental state, can manipulate; the game's CanBeArrestedBy, Verse.AI\GenAI.cs:73-100, which leaves out
		//   an aggro break hostile to the arrester, e.g. berserk); and the game's own arrest bed search finds no bed
		//   (RestUtility.FindBedFor with GuestStatus.Prisoner, as FloatMenuOptionProvider_Arrest.cs:41-48, which shows
		//   "NoPrisonerBed" then).
		//   The orders named are the router's: "place BUILDING at SPOT facing SIDE.", "set every bed in ROOM to prisoner."
		//   (a prison cell is the whole room; a prisoner bed needs a room that can be one, FormControls.PlanBedType) and
		//   "arrest PAWN with DOER.".
		private static readonly HashSet<Pawn> breakSeen = new HashSet<Pawn>();
		private static List<Pawn> breakPending = new List<Pawn>();

		// Bleeding stranger (user, 2026-10-15: "[REMINDER] you have a pod survivor Yuuma that is about to bleed out in 5 hour.
		// if you want to rescue them, do it now!" / "Make that happen. Bleed out based on health condition"). A downed, living
		// humanlike in view that is not the colony's (not a colonist, slave or prisoner of the colony) and not hostile, not
		// in a bed, and bleeding: the game's own time to death from blood loss, HealthUtility.TicksUntilDeathDueToBloodLoss
		// (Verse\HealthUtility.cs:552-561: blood left / bleed rate; int.MaxValue = not bleeding). Checked every 60 ticks like
		// the mental break line; once per downed stranger; checked again at delivery (rescued, tended, dead: dropped). The
		// order for a stranger is the right-click one ("rescue PAWN" takes colonists only): use option rescue on X, Z with DOER.
		private static readonly HashSet<Pawn> bleedSeen = new HashSet<Pawn>();
		private static List<Pawn> bleedPending = new List<Pawn>();

		private static void BleedTick()
		{
			List<Pawn> bleeding = new List<Pawn>();
			foreach (Map map in Find.Maps)
			{
				if (map == null || !map.IsPlayerHome)
				{
					continue;
				}
				foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
				{
					if (BleedingStranger(p))
					{
						bleeding.Add(p);
					}
				}
			}
			bleedSeen.RemoveWhere((Pawn p) => !bleeding.Contains(p));
			bleedPending.RemoveAll((Pawn p) => !bleeding.Contains(p));
			// Settings gate (user, 2026-10-26): Urgent reminders. Off: no line is made and the pawn is not marked seen, so turning it on during the event reports it.
			if (!VerbalCommandsMod.UrgentRemindersOn)
			{
				return;
			}
			foreach (Pawn p in bleeding)
			{
				string line = bleedSeen.Add(p) ? BleedLine(p) : null;
				if (line != null)
				{
					bleedPending.Add(p);
					AgentReminderLog.Scan("Bleeding check", ScanTime(), new List<string> { line });
				}
			}
		}

		private static bool BleedingStranger(Pawn p)
		{
			return p != null && p.Spawned && !p.Dead && p.Downed && p.RaceProps.Humanlike && p.Faction != Faction.OfPlayer && !p.IsPrisonerOfColony
				&& !p.IsSlaveOfColony && !p.HostileTo(Faction.OfPlayer) && !p.InBed() && AgentPlace.Visible(p.Map, p.Position)
				&& HealthUtility.TicksUntilDeathDueToBloodLoss(p) < int.MaxValue;
		}

		private static string BleedLine(Pawn p)
		{
			if (!BleedingStranger(p))
			{
				return null;
			}
			float hours = HealthUtility.TicksUntilDeathDueToBloodLoss(p) / (float)GenDate.TicksPerHour;
			string when = hours < 2f ? hours.ToString("0.0") + " hours" : Math.Round(hours).ToString("0") + " hours";
			string who = p.Faction == null ? "stranger" : "visitor from " + p.Faction.Name;
			Pawn doer = p.Map.mapPawns.FreeColonistsSpawned.Where((Pawn c) => !c.Downed && !c.InMentalState && c.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
				.OrderBy((Pawn c) => c.Position.DistanceToSquared(p.Position)).FirstOrDefault();
			return "[REMINDER] You have a downed " + who + " " + p.LabelShort + " at " + AgentWorkspotCheck.Xz(p.Position) + " that is about to bleed out in " + when
				+ ". If you want to rescue them, do it now! (e.g. 'use option rescue on " + AgentWorkspotCheck.Xz(p.Position) + " with " + (doer != null ? doer.LabelShort : "PAWN") + ".')"
				// User, 2026-10-15: "add "you need to DRAFT one of your own pawns BEFORE tend options become available!" at the
				// end of reminder sentence".
				+ " You need to DRAFT one of your own pawns BEFORE tend options become available!"
				// User, 2026-10-15: "And then "Make sure to rescue/arrest them after tending, since colonists dont do that on
				// their own" at the end".
				+ " Make sure to rescue/arrest them after tending, since colonists don't do that on their own.";
		}

		// Merchant in the home zone (user, 2026-10-15: "see if you can have [reminder]: a merchant is nearby if that merchant
		// steps into home zone, WITHOUT a tick based timer."). Called by MerchantArrivalPatch from the game's own step
		// (Pawn_PathFollower.TryEnterNextPathCell), not from a timer. Once per merchant per visit (the set forgets a merchant
		// once it is gone at delivery); checked again at delivery. The trade is opened with the game's right-click order:
		// "use option trade on NAME with PAWN." (the interact form; show interactable pawns lists traders).
		private static readonly HashSet<Pawn> merchantSeen = new HashSet<Pawn>();
		private static List<Pawn> merchantPending = new List<Pawn>();

		internal static void MerchantEntered(Pawn p)
		{
			// Settings gate (user, 2026-10-26): Urgent reminders. Off: no line, and the merchant is not marked seen, so turning it on while they stand in the home zone reports them at their next step.
			if (!VerbalCommandsMod.UrgentRemindersOn)
			{
				return;
			}
			if (merchantSeen.Add(p))
			{
				merchantPending.Add(p);
				string line = MerchantLine(p);
				AgentReminderLog.Scan("Merchant check", ScanTime(), new List<string> { line ?? p.LabelShort });
			}
		}

		private static string MerchantLine(Pawn p)
		{
			if (p == null || !p.Spawned || p.Dead || p.trader == null || p.trader.traderKind == null || p.Map == null)
			{
				return null;
			}
			Pawn negotiator = p.Map.mapPawns.FreeColonistsSpawned.Where((Pawn c) => !c.Downed && !c.InMentalState && c.skills != null
				&& c.health.capacities.CapableOf(PawnCapacityDefOf.Talking)).OrderByDescending((Pawn c) => c.skills.GetSkill(SkillDefOf.Social).Level).FirstOrDefault();
			string kind = p.trader.traderKind.label.NullOrEmpty() ? "merchant" : p.trader.traderKind.label;
			return "[REMINDER] A merchant is nearby: " + p.LabelShort + " (" + kind + (p.Faction != null ? ", " + p.Faction.Name : "") + ") has stepped into your home zone at "
				+ AgentWorkspotCheck.Xz(p.Position) + (p.CanTradeNow ? "" : "; they can't trade just yet") + ". To trade: 'use option trade on " + p.LabelShort
				+ " with " + (negotiator != null ? negotiator.LabelShort : "PAWN") + ".'" + (negotiator != null ? " (" + negotiator.LabelShort + " has the best Social skill, "
				+ negotiator.skills.GetSkill(SkillDefOf.Social).Level + ")" : "");
		}

		private static void BreakTick()
		{
			List<Pawn> breaking = new List<Pawn>();
			foreach (Map map in Find.Maps)
			{
				if (map == null || !map.IsPlayerHome)
				{
					continue;
				}
				foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
				{
					if (p.InMentalState)
					{
						breaking.Add(p);
					}
				}
			}
			breakSeen.RemoveWhere((Pawn p) => !breaking.Contains(p));
			breakPending.RemoveAll((Pawn p) => !breaking.Contains(p));
			// Settings gate (user, 2026-10-26): Urgent reminders. Off: no line is made and the pawn is not marked seen, so turning it on during the break reports it.
			if (!VerbalCommandsMod.UrgentRemindersOn)
			{
				return;
			}
			foreach (Pawn p in breaking)
			{
				string breakLine = breakSeen.Add(p) ? BreakLine(p) : null;
				if (breakLine != null)
				{
					breakPending.Add(p);
					AgentReminderLog.Scan("Mental break check", ScanTime(), new List<string> { breakLine });
				}
			}
		}

		private static string BreakLine(Pawn p)
		{
			if (p == null || !p.Spawned || !p.InMentalState || p.Map == null || p.MentalStateDef == null)
			{
				return null;
			}
			Pawn arrester = p.Map.mapPawns.FreeColonistsSpawned.FirstOrDefault((Pawn c) => c != p && !c.Downed && !c.InMentalState
				&& c.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && p.CanBeArrestedBy(c));
			if (arrester == null)
			{
				return null;
			}
			if (RestUtility.FindBedFor(p, arrester, false, true, GuestStatus.Prisoner) != null)
			{
				return null;
			}
			string name = p.LabelShort;
			return "[REMINDER] I see that your pawn " + name + " is on a mental break (" + p.MentalStateDef.label + ", at " + AgentWorkspotCheck.Xz(p.Position)
				+ "), but you have no prisoner bed. Maybe make a bed spot, turn it into a prisoner bed, then arrest your pawn? A prisoner bed has to stand in a "
				+ "closed room (walls and a door). (e.g. 'place sleeping spot at X, Z facing SIDE.', then 'set every bed in ROOM to prisoner.', then 'arrest "
				+ name + " with " + arrester.LabelShort + ".')";
		}

		// One message for the whole batch (user, 2026-10-14: "group every reminder that gets sent out on every 6hr so they
		// batch send in one prompt"): a head line with when it was scanned and how many, then the reminders numbered, one
		// per line, each without its own "[REMINDER]". So the reminders field holds one entry.
		private static string scannedAt = "";

		// TITLE: "6-hour check"; "3-hour check" when only grow lines (and maybe break lines) are in it; "Mental break check"
		// when only break lines (BreakLine) are (then WHEN is now).
		internal static string Block(List<string> batch, string title, string when)
		{
			List<string> rows = new List<string>();
			rows.Add("[REMINDER] " + title + (string.IsNullOrEmpty(when) ? "" : " (" + when + ")") + ", " + batch.Count + (batch.Count == 1 ? " reminder:" : " reminders:"));
			for (int i = 0; i < batch.Count; i++)
			{
				string line = batch[i] ?? "";
				if (line.StartsWith("[REMINDER] ", StringComparison.Ordinal))
				{
					line = line.Substring("[REMINDER] ".Length);
				}
				rows.Add((i + 1) + ". " + line);
			}
			return string.Join("\n", rows.ToArray());
		}

		// The game date and hour of the scan, at the first home map's place on the planet (as the game's date readout).
		internal static string ScanTime()
		{
			try
			{
				Map map = Find.Maps.FirstOrDefault((Map m) => m != null && m.IsPlayerHome);
				return map == null ? "" : GenDate.DateFullStringWithHourAt(Find.TickManager.TicksAbs, Find.WorldGrid.LongLatOf(map.Tile));
			}
			catch (Exception)
			{
				return "";
			}
		}

		// ---- the scan ---------------------------------------------------------------------------------------------

		// The lines of one producer, each queued with the gate of its category (user, 2026-10-26).
		private static void AddGated(List<GatedLine> into, IEnumerable<string> lines, Func<bool> gate)
		{
			foreach (string line in lines)
			{
				into.Add(new GatedLine(line, gate));
			}
		}

		// The 6-hour batch: what did not move to the hourly check (AgentHourlyCheck.cs, 2026-10-16).
		private static List<GatedLine> Scan()
		{
			List<GatedLine> lines = new List<GatedLine>();
			foreach (Map map in Find.Maps)
			{
				if (map != null && map.IsPlayerHome)
				{
					// Settings gate (user, 2026-10-26): Base and building reminders (stone chunks).
					if (VerbalCommandsMod.BaseRemindersOn)
					{
						string chunks = AgentChunks.ReminderLine(map);
						if (chunks != null)
						{
							lines.Add(new GatedLine(chunks, BaseGate));
						}
					}
					// Settings gate (user, 2026-10-26): Resource reminders (steel / component / plasteel sources).
					if (VerbalCommandsMod.ResourceRemindersOn)
					{
						AddGated(lines, AgentResourceScan.Lines(map), ResourceGate);
					}
					// Settings gate (user, 2026-10-26): Base and building reminders (heater, stonecutter).
					if (VerbalCommandsMod.BaseRemindersOn)
					{
						string heaterLine = HeaterLine(map);
						if (heaterLine != null)
						{
							lines.Add(new GatedLine(heaterLine, BaseGate));
						}
						AddGated(lines, AgentBenchScan.StonecutterLines(map), BaseGate);
					}
					// Settings gate (user, 2026-10-26): Pawn and work reminders (weapons).
					if (VerbalCommandsMod.PawnRemindersOn)
					{
						AddGated(lines, WeaponLines(map), PawnGate);
					}
					// Micro orders' leftovers (AgentMicro.cs; user, 2026-10-16: "a reminder shell is created (every 6hr)" / "Yes. batch.").
					// Settings gate (user, 2026-10-26): Resource reminders.
					if (VerbalCommandsMod.ResourceRemindersOn)
					{
						AddGated(lines, AgentMicro.LeftoverLines(map), ResourceGate);
					}
				}
			}
			// Drafting tip, every 6-hour check, no condition (user, 2026-10-15: "have a tip fire every 6hr saying [TIP] if
			// youre expecting an option from pawn menu, but dont see, maybe its because you havent drafted the pawn! try
			// drafting them, look for option, and when finished, undraft them."). Token-gap tips (TipInjector) can't keep game
			// hours, so it rides in this batch; "[TIP]" stays (Block strips only "[REMINDER] ").
			// Settings gate (user, 2026-10-26): Know-how tips.
			if (VerbalCommandsMod.TipsOn)
			{
				lines.Add(new GatedLine("[TIP] If you're expecting an option from the pawn menu but don't see it, maybe it's because you haven't drafted the pawn! Try drafting them, look"
					+ " for the option, and when finished, undraft them.", TipsGate));
			}
			return lines;
		}

		// The hourly batch (AgentHourlyCheck.cs), in the user's numbering of the 6-hour and 3-hour lists: 1 work spots,
		// 2 light, 3 room temperature, 4 vent (both from AgentRoomTempScan), 7 stove, 9 bulk meals, 10 benches, 12
		// blueprints waiting, 13 botched builds, 14 doctors' medicine, and the 3-hour list's 2, deterioration.
		internal static List<string> HourlyLines()
		{
			HarnessLog.BeginHourly();   // each problem below also goes to the harness log (HarnessLog.cs, H3)
			List<string> lines = new List<string>();
			List<string> lightLines = new List<string>();
			int more = 0;
			int moreDark = 0;
			foreach (Map map in Find.Maps)
			{
				// Settings gate (user, 2026-10-26): Base and building reminders (work spot penalty and light lines, with their "And N more").
				if (!VerbalCommandsMod.BaseRemindersOn || map == null || !map.IsPlayerHome)
				{
					continue;
				}
				foreach (Thing t in Workspots(map))
				{
					ThingDef def = Built(t);
					string what = AgentWorkspotCheck.LabelOf(def) + (t.def.IsBlueprint ? " (blueprint)" : t.def.IsFrame ? " (frame)" : "");

					// Light at the interaction spot (see LightAt).
					string light = LightLine(map, t, def, what);
					if (light != null)
					{
						if (lightLines.Count >= MaxLines)
						{
							moreDark++;
						}
						else
						{
							lightLines.Add(light);
						}
					}

					// Darkness has the light line above, power is not where the bench stands.
					List<AgentWorkspotCheck.Penalty> found = AgentWorkspotCheck.Penalties(map, def, t.Position, t is Building ? t : null)
						.Where((AgentWorkspotCheck.Penalty p) => !p.power && !p.dark).ToList();
					if (found.Count == 0)
					{
						continue;
					}
					HarnessLog.Note("work spot penalty", what + ", " + RoomLabel(map, def, t.Position) + " (" + AgentWorkspotCheck.Xz(t.Position) + ")");
					if (lines.Count >= MaxLines)
					{
						more++;
						continue;
					}
					float rate = found.Aggregate(1f, (float r, AgentWorkspotCheck.Penalty p) => r * p.factor);
					string speed = AgentWorkspotCheck.IsResearchBench(def) ? "research speed" : "work speed";
					lines.Add("[REMINDER] You have " + what + " placed in " + RoomLabel(map, def, t.Position) + " (" + AgentWorkspotCheck.Xz(t.Position) + "), which has a "
						+ speed + " penalty of x" + AgentWorkspotCheck.Pct(rate) + " ("
						+ string.Join(", ", found.Select((AgentWorkspotCheck.Penalty p) => p.what + " x" + AgentWorkspotCheck.Pct(p.factor)).ToArray())
						+ "). Consider re-placing it.");
				}
			}
			if (more > 0)
			{
				lines.Add("[REMINDER] And " + more + " more work spots or benches with a work speed penalty.");
			}
			lines.AddRange(lightLines);
			if (moreDark > 0)
			{
				lines.Add("[REMINDER] And " + moreDark + " more work spots or benches with less than 50% light at their interaction spot.");
			}
			Dictionary<Pawn, IntVec3> draftSpots = new Dictionary<Pawn, IntVec3>();
			foreach (Map map in Find.Maps)
			{
				if (map != null && map.IsPlayerHome)
				{
					// Settings gate (user, 2026-10-26): Base and building reminders (room temperature and vents, stove).
					if (VerbalCommandsMod.BaseRemindersOn)
					{
						lines.AddRange(AgentRoomTempScan.Lines(map));
						string stove = StoveLine(map);
						if (stove != null)
						{
							lines.Add(stove);
						}
					}
					// Settings gate (user, 2026-10-26): Food and Farming reminders (bulk meals).
					if (VerbalCommandsMod.FoodRemindersOn)
					{
						string bulkMeal = BulkMealLine(map);
						if (bulkMeal != null)
						{
							lines.Add(bulkMeal);
						}
					}
					// Settings gate (user, 2026-10-26): Pawn and work reminders (benches).
					if (VerbalCommandsMod.PawnRemindersOn)
					{
						lines.AddRange(AgentBenchScan.WorkLines(map));
					}
					// Settings gate (user, 2026-10-26): Base and building reminders (blueprints waiting).
					if (VerbalCommandsMod.BaseRemindersOn)
					{
						string blueprints = BlueprintLine(map);
						if (blueprints != null)
						{
							lines.Add(blueprints);
						}
					}
					// Settings gate (user, 2026-10-26): Pawn and work reminders (botched builds, doctors' medicine).
					if (VerbalCommandsMod.PawnRemindersOn)
					{
						lines.AddRange(AgentWorkChanceScan.Lines(map));
						string carry = CarryMedicineLine(map);
						if (carry != null)
						{
							lines.Add(carry);
						}
					}
					// Settings gate (user, 2026-10-26): Food and Farming reminders (deterioration, rotting food).
					if (VerbalCommandsMod.FoodRemindersOn)
					{
						lines.AddRange(AgentDeteriorationScan.Lines(map));
						// Rotting food, indoors by room and outdoors by 10x10 square (AgentRotScan.cs; user, 2026-10-17).
						lines.AddRange(AgentRotScan.Lines(map));
					}
					// Drafted pawns and forbidden buildings / items (user, 2026-10-25; DraftLines, ForbiddenLines below).
					// Settings gate (user, 2026-10-26): Pawn and work reminders. DraftLines always runs: it records where the
					// drafted pawns stand (draftSpots, then lastDraftSpot), which the next hourly scan compares; only its lines are held back.
					List<string> drafted = DraftLines(map, draftSpots);
					if (VerbalCommandsMod.PawnRemindersOn)
					{
						lines.AddRange(drafted);
					}
					// Settings gate (user, 2026-10-26): Base and building reminders (forbidden buildings and items).
					if (VerbalCommandsMod.BaseRemindersOn)
					{
						lines.AddRange(ForbiddenLines(map));
					}
				}
			}
			lastDraftSpot = draftSpots;
			// Settings gate (user, 2026-10-26): Pawn and work reminders.
			string hypothermia = VerbalCommandsMod.PawnRemindersOn ? HypothermiaLine() : null;
			if (hypothermia != null)
			{
				lines.Add(hypothermia);
			}
			AgentWorkChanceScan.MarkScanned(Find.TickManager.TicksGame);   // botched builds count from this scan on
			return lines;
		}

		// ---- drafted pawns (user, 2026-10-25) ------------------------------------------------------------------------
		// "set up some sort of draft-watch so it goes into agent's 1hr reminder if a pawn is drafted & sitting there"; the
		// user's picks: any drafted pawn at every hourly scan, hostiles on the map or not, pawns that have not moved said
		// together without cells ("bob and alice hasnt moved from last spot"), the short line + why. Indoors, the user's
		// own text (2026-10-25: "the agents may have intentionally drafted pawns so they can put pawns in safe places (e.g.
		// indoors)"), one line per room.
		private static Dictionary<Pawn, IntVec3> lastDraftSpot = new Dictionary<Pawn, IntVec3>();

		private static List<string> DraftLines(Map map, Dictionary<Pawn, IntVec3> now)
		{
			List<string> lines = new List<string>();
			List<Pawn> drafted = map.mapPawns.FreeColonistsSpawned.Where((Pawn p) => p.Drafted).ToList();
			foreach (IGrouping<Room, Pawn> group in drafted.Where((Pawn p) => IsIndoors(p.GetRoom())).GroupBy((Pawn p) => p.GetRoom()))
			{
				List<Pawn> pawns = group.ToList();
				lines.Add("[REMINDER] " + PawnNames(pawns) + (pawns.Count == 1 ? " is" : " are") + " inside " + (VocabularyText.RoomLabel(group.Key) ?? "a room")
					+ ". However pawns wont do anything while drafted, so if you are drafting to get them cooled down on hypothermia/hyperthermia,"
					+ " simply disallow/forbid them from using doors inside the room so they can interact with whatever is in the room but not go out.");
			}
			List<Pawn> still = new List<Pawn>();
			List<Pawn> moved = new List<Pawn>();
			foreach (Pawn p in drafted)
			{
				now[p] = p.Position;
				if (IsIndoors(p.GetRoom()))
				{
					continue;
				}
				IntVec3 before;
				if (lastDraftSpot.TryGetValue(p, out before) && before == p.Position)
				{
					still.Add(p);
				}
				else
				{
					moved.Add(p);
				}
			}
			if (still.Count > 0)
			{
				lines.Add("[REMINDER] " + PawnNames(still) + (still.Count == 1 ? " is drafted and hasn't" : " are drafted and haven't")
					+ " moved from their last spot. Drafted pawns don't eat, sleep or work.");
			}
			if (moved.Count > 0)
			{
				lines.Add("[REMINDER] " + PawnNames(moved) + (moved.Count == 1 ? " is" : " are") + " drafted. Drafted pawns don't eat, sleep or work.");
			}
			return lines;
		}

		// ---- forbidden buildings and items (user, 2026-10-25) ----------------------------------------------------------
		// "I would write a general code that scans for forbidden buildings/items indoors and say what they are, and if its
		// buildings, print their id so its easier for models to unforbid/forbid them" / "so they dont forget to unforbid
		// them and wonder why their pawns arent doing anything". The user's picks: every forbidden door (doors sit between
		// rooms) and the other buildings inside a room; items inside a room, per room, kinds and counts.
		private static List<string> ForbiddenLines(Map map)
		{
			List<string> lines = new List<string>();
			List<string> buildings = new List<string>();
			int moreBuildings = 0;
			foreach (Building b in map.listerBuildings.allBuildingsColonist)
			{
				CompForbiddable comp = b.Spawned ? b.GetComp<CompForbiddable>() : null;
				if (comp == null || !comp.Forbidden)
				{
					continue;
				}
				Building_Door door = b as Building_Door;
				string where;
				if (door != null)
				{
					where = DoorName(map, door);
				}
				else
				{
					Room room = FormPlanner.RoomOfThing(b);
					if (!IsIndoors(room))
					{
						continue;
					}
					where = VocabularyText.RoomLabel(room) ?? "";
				}
				if (buildings.Count >= MaxLines)
				{
					moreBuildings++;
					continue;
				}
				buildings.Add(b.ThingID + " (" + b.def.label + (where != "" ? ", " + where : "") + ")");
			}
			if (buildings.Count > 0)
			{
				lines.Add("[REMINDER] Forbidden buildings: " + string.Join(", ", buildings.ToArray()) + (moreBuildings > 0 ? " and " + moreBuildings + " more" : "")
					+ ". Colonists won't use or pass them; unforbid ID. allows one again.");
			}

			List<Room> roomOrder = new List<Room>();
			Dictionary<Room, Dictionary<string, int>> byRoom = new Dictionary<Room, Dictionary<string, int>>();
			foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
			{
				if (!t.Spawned || !t.IsForbidden(Faction.OfPlayer))
				{
					continue;
				}
				Room room = t.GetRoom();
				if (!IsIndoors(room))
				{
					continue;
				}
				Dictionary<string, int> kinds;
				if (!byRoom.TryGetValue(room, out kinds))
				{
					kinds = new Dictionary<string, int>();
					byRoom[room] = kinds;
					roomOrder.Add(room);
				}
				int had;
				kinds.TryGetValue(t.def.label, out had);
				kinds[t.def.label] = had + t.stackCount;
			}
			if (roomOrder.Count > 0)
			{
				List<string> rooms = new List<string>();
				foreach (Room room in roomOrder.Take(MaxLines))
				{
					rooms.Add((VocabularyText.RoomLabel(room) ?? "a room") + ": "
						+ string.Join(", ", byRoom[room].Select((KeyValuePair<string, int> k) => k.Value > 1 ? k.Key + " x" + k.Value : k.Key).ToArray()));
				}
				int moreRooms = roomOrder.Count - rooms.Count;
				lines.Add("[REMINDER] Forbidden items indoors: " + string.Join("; ", rooms.ToArray()) + (moreRooms > 0 ? " and " + moreRooms + " more rooms" : "")
					+ ". unforbid items of THINGS in area AREA. allows them again.");
			}
			return lines;
		}

		// A proper room that is not open air.
		private static bool IsIndoors(Room room)
		{
			return room != null && room.ProperRoom && !room.PsychologicallyOutdoors;
		}

		// "bedroom to outdoors", "kitchen to dining room": the door as the "doors" vocabulary line names it.
		private static string DoorName(Map map, Building_Door door)
		{
			List<string> sides = FormPlanner.DoorSides(map, door)
				.Select((Room r) => VocabularyText.RoomLabel(r))
				.Where((string s) => !string.IsNullOrEmpty(s))
				.Distinct()
				.ToList();
			if (FormPlanner.LeadsOutdoors(map, door))
			{
				sides.Add("outdoors");
			}
			return string.Join(" to ", sides.ToArray());
		}

		// "Bob", "Bob and Alice", "Bob, Alice and Carol".
		private static string PawnNames(List<Pawn> pawns)
		{
			List<string> names = pawns.Select((Pawn p) => p.LabelShort).ToList();
			if (names.Count <= 1)
			{
				return names.Count == 1 ? names[0] : "";
			}
			return string.Join(", ", names.Take(names.Count - 1).ToArray()) + " and " + names[names.Count - 1];
		}

		// Hypothermia (user, 2026-10-16, notes\hypothermia_practical.md: "maybe just list of pawns with hypothermia + severity."
		// / "Lines change as hypothermia progresses." / "the line fires only once, applied to pawn with highest hypothermia
		// severity, but must list every pawn with its own severity."). One line for all home maps while a colonist has
		// hypothermia at 0.04 or more (the first stage the health tab shows, Data\Core\Defs\HediffDefs\
		// Hediffs_Global_Temperature.xml): every such colonist with its severity, highest first, then the user's text for the
		// highest one's tier (0.35 covers extreme, 0.62, too: the user gave no text for it).
		private static string HypothermiaLine()
		{
			List<KeyValuePair<Pawn, float>> cold = new List<KeyValuePair<Pawn, float>>();
			foreach (Map map in Find.Maps)
			{
				if (map == null || !map.IsPlayerHome)
				{
					continue;
				}
				foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
				{
					Hediff h = p.health != null ? p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Hypothermia, false) : null;
					if (h != null && h.Severity >= 0.04f)
					{
						cold.Add(new KeyValuePair<Pawn, float>(p, h.Severity));
					}
				}
			}
			if (cold.Count == 0)
			{
				return null;
			}
			cold = cold.OrderByDescending((KeyValuePair<Pawn, float> kv) => kv.Value).ToList();
			foreach (KeyValuePair<Pawn, float> kv in cold)
			{
				HarnessLog.Note("hypothermia", kv.Key.LabelShort + " " + kv.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
			}
			float top = cold[0].Value;
			string text = top >= 0.35f
				? "0.35: Major penalty, and risk of pawns collapsing/having frostbite starts here. seek indoor shelter ASAP."
				: top >= 0.2f
					? "0.20: Moderate penalty to pawn capacity. seek indoor shelter very soon."
					: "0.04: the pawn is starting to feel cold. minor penalty to manipulation and consciousness. practically speaking: jobs success chance (that can fail) increased. not ideal, but not game-ending unless resource-starved scenario.";
			return "[REMINDER] Hypothermia: " + string.Join(", ", cold.Select((KeyValuePair<Pawn, float> kv) => kv.Key.LabelShort + " "
				+ kv.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).ToArray()) + ". " + text;
		}

		// The stone chunk line (user, 2026-10-14: "[reminder] you dont seem to have stockpile for stone chunks and you have stonk
		// chunks indoors.") was replaced by AgentChunks.ReminderLine (user, 2026-10-16: "Sure.", to replacing it).

		// Stove (user, 2026-10-14: "make a reminder for stove. condition: campfire exists, but no cooking stove. OR; no
		// tree throughout map, campfire exist, no stove. if former: [Having a stove can enable better meal which can lead
		// to better mood.] if latter: [having a stove will help you save wood, because i see you dont have any tree
		// currently on map]"). The colony has a built campfire (ThingDefOf.Campfire) and no cooking stove: a colony work
		// table other than the campfire with a recipe that makes a meal (IngestibleProperties.IsMeal), built, framed or
		// blueprinted (one on its way counts, so the line stops once it is ordered). The second text when no tree stands
		// in an unfogged cell of the map (Plant with def.plant.IsTree), else the first.
		private static string StoveLine(Map map)
		{
			if (ThingDefOf.Campfire == null || !map.listerBuildings.ColonistsHaveBuilding(ThingDefOf.Campfire))
			{
				return null;
			}
			foreach (Building b in map.listerBuildings.allBuildingsColonist)
			{
				if (IsStove(b.def))
				{
					return null;
				}
			}
			foreach (ThingRequestGroup group in new ThingRequestGroup[] { ThingRequestGroup.BuildingFrame, ThingRequestGroup.Blueprint })
			{
				foreach (Thing t in map.listerThings.ThingsInGroup(group))
				{
					if (t.Faction == Faction.OfPlayer && IsStove(Built(t)))
					{
						return null;
					}
				}
			}
			Building campfire = map.listerBuildings.allBuildingsColonist.FirstOrDefault((Building b) => b.def == ThingDefOf.Campfire && b.Spawned);
			HarnessLog.Note("stove", campfire == null ? "" : campfire.def.label + ", " + RoomLabel(map, campfire.def, campfire.Position) + " (" + AgentWorkspotCheck.Xz(campfire.Position) + ")");
			return AnyTreeInView(map)
				? "[REMINDER] Having a stove can enable better meal which can lead to better mood."
				: "[REMINDER] Having a stove will help you save wood, because I see you don't have any tree currently on map.";
		}

		// A tree standing in an unfogged cell (the stove line's test; also the heater line's).
		private static bool AnyTreeInView(Map map)
		{
			return map.listerThings.ThingsInGroup(ThingRequestGroup.Plant)
				.Any((Thing t) => t.Spawned && t.def.plant != null && t.def.plant.IsTree && AgentPlace.Visible(map, t.Position));
		}

		// Heater (user, 2026-10-15: "Im going to guess there is no "build heater you dumbass, theres no tree on this map and
		// your only heat source is a campfire". if so, theres a tree scanner logic in place for stove. borrow that."). The
		// colony has a built campfire (ThingDefOf.Campfire), no heater of any kind (thingClass Building_Heater, built, framed
		// or blueprinted: one on its way counts), and no tree in view (AnyTreeInView, as the stove line). The example uses the
		// router's "build BUILDING near SPOT." at the campfire.
		private static string HeaterLine(Map map)
		{
			if (ThingDefOf.Campfire == null)
			{
				return null;
			}
			Building campfire = map.listerBuildings.allBuildingsColonist.FirstOrDefault((Building b) => b.def == ThingDefOf.Campfire && b.Spawned);
			if (campfire == null)
			{
				return null;
			}
			if (map.listerBuildings.allBuildingsColonist.Any((Building b) => b is Building_Heater))
			{
				return null;
			}
			foreach (ThingRequestGroup group in new ThingRequestGroup[] { ThingRequestGroup.BuildingFrame, ThingRequestGroup.Blueprint })
			{
				foreach (Thing t in map.listerThings.ThingsInGroup(group))
				{
					ThingDef built = Built(t);
					if (t.Faction == Faction.OfPlayer && built != null && built.thingClass != null && typeof(Building_Heater).IsAssignableFrom(built.thingClass))
					{
						return null;
					}
				}
			}
			if (AnyTreeInView(map))
			{
				return null;
			}
			string heater = ThingDefOf.Heater != null ? ThingDefOf.Heater.label : "heater";
			return "[REMINDER] Build a " + heater + ": I see no tree on this map, and your only heat source is a campfire, which burns wood you can't"
				+ " replace here. A " + heater + " runs on power instead. (e.g. 'build " + heater + " near " + AgentWorkspotCheck.Xz(campfire.Position) + ".')";
		}

		// Bulk meals (user, 2026-10-14: "add reminder. condition: stove makes simple meal in x1, but not x4. "[reminder]:
		// Bulk-making meal can save pawns trips.""). A colony bench that offers the x4 recipe and has an active (not
		// suspended) bill for one simple meal but none for x4. Recipes_Meals.xml:32-87: CookMealSimple makes 1 meal from
		// 0.5 nutrition, CookMealSimpleBulk 4 from 2.0 (work 1200); both are on the fueled and the electric stove
		// (Buildings_Production.xml:713-721, :806-814) and on the campfire (Buildings_Temperature.xml:106-107), so the
		// campfire counts too. The example order copies the x1 bill's repeat mode (count / until you have / forever) with
		// the router's "add bill to make ... at BENCH[ in ROOM]." (router.txt help "bill add ...").
		private static string BulkMealLine(Map map)
		{
			RecipeDef single = DefDatabase<RecipeDef>.GetNamedSilentFail("CookMealSimple");
			RecipeDef bulk = DefDatabase<RecipeDef>.GetNamedSilentFail("CookMealSimpleBulk");
			if (single == null || bulk == null)
			{
				return null;
			}
			List<string> where = new List<string>();
			string example = null;
			foreach (Building b in map.listerBuildings.allBuildingsColonist)
			{
				Building_WorkTable table = b as Building_WorkTable;
				if (table == null || !b.Spawned || table.billStack == null || !b.def.AllRecipes.Contains(bulk))
				{
					continue;
				}
				Bill_Production one = table.billStack.Bills.OfType<Bill_Production>().FirstOrDefault((Bill_Production x) => x.recipe == single && !x.suspended);
				if (one == null || table.billStack.Bills.Any((Bill x) => x.recipe == bulk && !x.suspended))
				{
					continue;
				}
				where.Add(b.def.label + " in " + RoomLabel(map, b.def, b.Position) + " (" + AgentWorkspotCheck.Xz(b.Position) + ")");
				HarnessLog.Note("bulk meals", b.def.label + ", " + RoomLabel(map, b.def, b.Position) + " (" + AgentWorkspotCheck.Xz(b.Position) + ")");
				if (example == null)
				{
					Room room = b.GetRoom();
					string at = " at " + b.def.label + (room != null && !room.PsychologicallyOutdoors && !string.IsNullOrEmpty(VocabularyText.RoomLabel(room)) ? " in " + VocabularyText.RoomLabel(room) : "") + ".";
					if (one.repeatMode == BillRepeatModeDefOf.TargetCount)
					{
						example = "add bill to make " + bulk.label + " until you have " + one.targetCount + at;
					}
					else if (one.repeatMode == BillRepeatModeDefOf.Forever)
					{
						example = "add bill to make " + bulk.label + " forever" + at;
					}
					else
					{
						example = "add bill to make " + Math.Max(1, (one.repeatCount + 3) / 4) + " " + bulk.label + at;
					}
				}
			}
			if (where.Count == 0)
			{
				return null;
			}
			return "[REMINDER] Bulk-making meal can save pawns trips. " + string.Join(", ", where.ToArray()) + (where.Count == 1 ? " has" : " have")
				+ " a '" + single.label + "' bill (1 meal each) but no '" + bulk.label + "' bill (4 meals each). (e.g. '" + example + "')";
		}

		// Weapon in the wrong hands (user, 2026-10-15: "see if you can have a reminder for pawns holding weapons, but theres a
		// pawn with higher skill than any of the pawns holding weapons (either melee or ranged) and if there is one,
		// [reminder] I see {pawn_name} with {either shooting or melee, dep on condition} of {skill_level}, but doesn't have a
		// weapon. maybe have a colonist drop a weapon by using {sentence here} and have the higher skill leveled colonist pick
		// it up?"). For ranged weapons (ThingDef.IsRangedWeapon, Shooting) and melee weapons (IsMeleeWeapon, Melee) apart:
		// among free colonists who can fight (not downed, violence not disabled), the unarmed one with the most skill against
		// the holder of that kind with the least; a line when the unarmed one is higher, so the swap helps. Orders: the
		// router's "have PAWN drop ITEM." (risky) and "equip PAWN with WEAPON.". A colonist named for ranged is not named
		// again for melee.
		private static List<string> WeaponLines(Map map)
		{
			List<string> lines = new List<string>();
			List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned.Where((Pawn p) => p.skills != null && p.equipment != null && !p.Downed
				&& !p.WorkTagIsDisabled(WorkTags.Violent)).ToList();
			HashSet<Pawn> named = new HashSet<Pawn>();
			foreach (bool ranged in new bool[] { true, false })
			{
				SkillDef skill = ranged ? SkillDefOf.Shooting : SkillDefOf.Melee;
				List<Pawn> holders = pawns.Where((Pawn p) => p.equipment.Primary != null
					&& (ranged ? p.equipment.Primary.def.IsRangedWeapon : p.equipment.Primary.def.IsMeleeWeapon)).ToList();
				if (holders.Count == 0)
				{
					continue;
				}
				Pawn weakest = holders.OrderBy((Pawn p) => p.skills.GetSkill(skill).Level).First();
				Pawn best = pawns.Where((Pawn p) => p.equipment.Primary == null && !named.Contains(p)).OrderByDescending((Pawn p) => p.skills.GetSkill(skill).Level).FirstOrDefault();
				if (best == null || best.skills.GetSkill(skill).Level <= weakest.skills.GetSkill(skill).Level)
				{
					continue;
				}
				named.Add(best);
				string weapon = weakest.equipment.Primary.def.label;
				string skillWord = ranged ? "shooting" : "melee";
				lines.Add("[REMINDER] I see " + best.LabelShort + " with " + skillWord + " of " + best.skills.GetSkill(skill).Level + ", but doesn't have a weapon. Maybe have a"
					+ " colonist drop a weapon by using 'have " + weakest.LabelShort + " drop " + weapon + ".' (" + weakest.LabelShort + " holds it with " + skillWord + " "
					+ weakest.skills.GetSkill(skill).Level + ") and have the higher skill leveled colonist pick it up: 'equip " + best.LabelShort + " with " + weapon + ".'");
			}
			return lines;
		}

		// Doctors not carrying medicine (user, 2026-10-14: "and a reminder for doctors that should be carrying but arent.
		// every 6hr."). A doctor: a free colonist with Doctor work on (not disabled for them). "Not carrying": the Assign
		// tab's Carry setting (Pawn_InventoryStockTracker, group InventoryStockGroupDefOf.Medicine) is 0, or it is set but
		// the colony has none of that kind in storage to refill from (the pawn fetches it itself,
		// JobGiver_TakeForInventoryStock.cs:19-27, so a set count with stock on hand needs nothing). Only while some
		// medicine of the group is in storage (ResourceCounter: stored items); the example order uses the kind with the
		// most in storage and the router's "set carry medicine of PAWNS to N MEDICINE." (FormGroups.PlanCarryMedicine).
		private static string CarryMedicineLine(Map map)
		{
			InventoryStockGroupDef group = InventoryStockGroupDefOf.Medicine;
			if (group == null || group.thingDefs.NullOrEmpty())
			{
				return null;
			}
			Dictionary<ThingDef, int> stored = group.thingDefs.ToDictionary((ThingDef d) => d, (ThingDef d) => map.resourceCounter.GetCount(d));
			ThingDef most = stored.OrderByDescending((KeyValuePair<ThingDef, int> kv) => kv.Value).First().Key;
			if (stored[most] <= 0)
			{
				return null;
			}
			List<string> notSet = new List<string>();
			List<string> noStock = new List<string>();
			List<string> names = new List<string>();
			foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
			{
				if (p.inventoryStock == null || p.workSettings == null || !p.workSettings.EverWork || p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)
					|| !p.workSettings.WorkIsActive(WorkTypeDefOf.Doctor))
				{
					continue;
				}
				int want = p.inventoryStock.GetDesiredCountForGroup(group);
				ThingDef kind = p.inventoryStock.GetDesiredThingForGroup(group);
				int has = p.inventory != null ? p.inventory.Count(kind) : 0;
				if (want <= 0)
				{
					notSet.Add(p.LabelShort);
					names.Add(p.LabelShort);
				}
				else if (has < want && stored[kind] <= 0)
				{
					noStock.Add(p.LabelShort + " (set to " + want + " " + kind.label + ", has " + has + ", none in storage)");
					names.Add(p.LabelShort);
				}
			}
			if (names.Count == 0)
			{
				return null;
			}
			foreach (string n in names)
			{
				HarnessLog.Note("doctor's medicine", n);
			}
			List<string> parts = new List<string>();
			if (notSet.Count > 0)
			{
				parts.Add(FormPlanner.JoinNames(notSet) + (notSet.Count == 1 ? " does Doctor work but carries no medicine" : " do Doctor work but carry no medicine"));
			}
			if (noStock.Count > 0)
			{
				parts.Add(string.Join(", ", noStock.ToArray()) + " can't refill");
			}
			string have = string.Join(", ", stored.Where((KeyValuePair<ThingDef, int> kv) => kv.Value > 0).Select((KeyValuePair<ThingDef, int> kv) => kv.Value + " " + kv.Key.label).ToArray());
			return "[REMINDER] " + string.Join("; ", parts.ToArray()) + ". A doctor who carries medicine can tend at once where someone falls, without a trip to"
				+ " storage (in storage: " + have + "). (e.g. 'set carry medicine of " + FormPlanner.JoinNames(names) + " to 1 " + most.label + ".'; up to 3)";
		}

		// Several blueprints (user, 2026-10-14: "addn. reminder, if multile blueprints are on map (1+): orchestrator gets
		// reminder that it can assign pawns to work on specific building/constructing it to prioritize it over others").
		// "multiple (1+)" read as more than one: 2 or more of the colony's build blueprints and frames (Blueprint_Build,
		// Frame; an install blueprint is hauling, not construction). The order to name is the mod's own
		// "prioritize constructing at SPOT with DOER." (router.txt, form "prioritize"; AgentPawnActionsWork.cs:191-195:
		// delivering to blueprints and frames and finishing frames). The example uses the first one and a colonist with
		// Construction on.
		private const int BlueprintKindsNamed = 5;

		private static string BlueprintLine(Map map)
		{
			List<Thing> waiting = new List<Thing>();
			foreach (ThingRequestGroup group in new ThingRequestGroup[] { ThingRequestGroup.Blueprint, ThingRequestGroup.BuildingFrame })
			{
				foreach (Thing t in map.listerThings.ThingsInGroup(group))
				{
					if (t.Spawned && t.Faction == Faction.OfPlayer && (t is Blueprint_Build || t is Frame) && t.def.entityDefToBuild != null)
					{
						waiting.Add(t);
					}
				}
			}
			if (waiting.Count < 2)
			{
				return null;
			}
			foreach (Thing t in waiting)
			{
				HarnessLog.Note("blueprints waiting", (t.def.entityDefToBuild.label ?? t.def.entityDefToBuild.defName) + " (" + AgentWorkspotCheck.Xz(t.Position) + ")");
			}
			List<KeyValuePair<string, int>> kinds = waiting.GroupBy((Thing t) => t.def.entityDefToBuild.label ?? t.def.entityDefToBuild.defName)
				.Select((IGrouping<string, Thing> g) => new KeyValuePair<string, int>(g.Key, g.Count()))
				.OrderByDescending((KeyValuePair<string, int> kv) => kv.Value).ToList();
			string named = string.Join(", ", kinds.Take(BlueprintKindsNamed).Select((KeyValuePair<string, int> kv) => kv.Value + " " + kv.Key).ToArray())
				+ (kinds.Count > BlueprintKindsNamed ? " and " + (kinds.Count - BlueprintKindsNamed) + " more kinds" : "");
			Pawn builder = map.mapPawns.FreeColonistsSpawned.FirstOrDefault((Pawn p) => p.workSettings != null && p.workSettings.EverWork
				&& !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && p.workSettings.WorkIsActive(WorkTypeDefOf.Construction));
			string example = "prioritize constructing at " + AgentWorkspotCheck.Xz(waiting[0].Position) + (builder != null ? " with " + builder.LabelShort : "") + ".";
			return "[REMINDER] You have " + waiting.Count + " blueprints and frames waiting to be built (" + named + "). You can assign a pawn to work on a "
				+ "specific one first, over the others: prioritize constructing at X, Z with NAME. (e.g. '" + example + "')";
		}

		private static bool IsStove(ThingDef def)
		{
			if (def == null || def == ThingDefOf.Campfire || !def.IsWorkTable)
			{
				return false;
			}
			return def.AllRecipes.Any((RecipeDef r) => r.products != null
				&& r.products.Any((ThingDefCountClass p) => p.thingDef != null && p.thingDef.ingestible != null && p.thingDef.ingestible.IsMeal));
		}

		// The colony's work tables and research benches on MAP: built ones, then frames and blueprints of them.
		private static List<Thing> Workspots(Map map)
		{
			List<Thing> list = new List<Thing>();
			foreach (Building b in map.listerBuildings.allBuildingsColonist)
			{
				if (b.Spawned && AgentWorkspotCheck.IsWorkspot(b.def))
				{
					list.Add(b);
				}
			}
			foreach (ThingRequestGroup group in new ThingRequestGroup[] { ThingRequestGroup.BuildingFrame, ThingRequestGroup.Blueprint })
			{
				foreach (Thing t in map.listerThings.ThingsInGroup(group))
				{
					if (t.Spawned && t.Faction == Faction.OfPlayer && AgentWorkspotCheck.IsWorkspot(Built(t)))
					{
						list.Add(t);
					}
				}
			}
			return list;
		}

		// Light (user, 2026-10-14: "do same for lighting. check if a bench's interaction spot has light source below 50%.
		// if it's below 50%, tip fires off to build light source."). The light the light sources give the interaction
		// cell, the sun left out (GroundGlowAt ignoreSky, Verse\GlowGrid.cs:244-263), so a scan at noon and at midnight
		// read the same. Lamp light counts up to 50% at most (GlowGrid.cs:260-261: min(0.5)); only a sun lamp's inner
		// area gives 100% (:256-259). So "under 50%" = no lamp reaches the spot fully. Built benches use their own
		// interaction cell; a frame or blueprint the cell it will have (ThingUtility.InteractionCellWhenAt).
		private const float LightNeeded = 0.5f;

		private static string LightLine(Map map, Thing t, ThingDef def, string what)
		{
			if (!def.hasInteractionCell)
			{
				return null;
			}
			IntVec3 cell = t is Building ? t.InteractionCell : ThingUtility.InteractionCellWhenAt(def, t.Position, t.Rotation, map);
			if (!cell.InBounds(map))
			{
				return null;
			}
			float glow = map.glowGrid.GroundGlowAt(cell, false, true);
			if (glow >= LightNeeded - 0.005f)
			{
				return null;
			}
			HarnessLog.Note("dark work spot", what + ", " + RoomLabel(map, def, t.Position) + " (" + AgentWorkspotCheck.Xz(cell) + ")");
			return "[REMINDER] The interaction spot of the " + what + " in " + RoomLabel(map, def, t.Position) + " (" + AgentWorkspotCheck.Xz(cell) + ") has "
				+ AgentWorkspotCheck.Pct(glow) + " light from light sources (below 50%). Consider building a light source near it (a standing lamp lights up to 50%).";
		}

		// What a thing is or will be once built (a blueprint / frame: the building it becomes).
		private static ThingDef Built(Thing t)
		{
			if (t.def.IsBlueprint || t.def.IsFrame)
			{
				return t.def.entityDefToBuild as ThingDef;
			}
			return t.def;
		}

		// "outdoors", "barracks", "workshop" ...: the room at CELL as the player sees it.
		private static string RoomLabel(Map map, ThingDef def, IntVec3 cell)
		{
			Room room = cell.GetRoom(map);
			if (room == null || room.PsychologicallyOutdoors || (AgentWorkspotCheck.IsResearchBench(def) && room.OutdoorsForWork))
			{
				return "outdoors";
			}
			if (StorageIndex.IsGrowRoom(room))
			{
				return StorageIndex.GrowRoomLabel;
			}
			if (room.Role == null || room.Role == RoomRoleDefOf.None)
			{
				return "a room with no role";
			}
			return room.Role.label;
		}
	}
}
