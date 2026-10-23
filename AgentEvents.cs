using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Agent 02, item "events": the event lines of the game that an agent playing it needs to see, the automatic
	// pause triggers of a run, and the question `show events`. Main thread only (AgentTime.Tick calls Tick/Reset
	// from GameComponentTick; the form planner calls QueryEvents).
	//
	// What is checked (every 60 ticks, only what is NEW since the last check; the first check after Reset only
	// records the baseline), and what the game itself tells the player at the same moment:
	//   letters           new letter on Find.LetterStack (Letter.ID, Verse\Letter.cs:9). A letter pauses the run when
	//                     its def is ThreatBig / ThreatSmall / NegativeEvent / Death (RimWorld\LetterDefOf.cs:8-18) or
	//                     when it asks for a choice (AsksForChoice below). Raids: IncidentWorker_RaidEnemy.cs:145-147
	//                     GetLetterDef returns ThreatBig, used at IncidentWorker_Raid.cs:166. Manhunter packs:
	//                     IncidentWorker_AggressiveAnimals.cs:61 and :66 send ThreatBig. Prison breaks:
	//                     PrisonBreakUtility.cs:250 letterDef = ThreatBig (label "LetterLabelPrisonBreak",
	//                     PrisonBreakUtility.cs:249-250). Ambushes of manhunters (IncidentWorker_Ambush.cs:104-107) take the
	//                     letter def from the IncidentDef (an XML value, not checked here).
	//   downed / died     Pawn.Downed (Verse\Pawn.cs:231), Pawn.Dead (:261); a dead colonist leaves mapPawns.FreeColonists
	//                     (Verse\MapPawns.cs:144, :787-795 filters on Faction/RaceProps only, a corpse is not a pawn there),
	//                     so "was in the last check, is gone and Dead" = died; gone and not Dead (caravan) = dropped.
	//   mental break      Pawn.InMentalState (Verse\Pawn.cs:277), Pawn.MentalStateDef (:301)
	//   starving          Need_Food.Starving (RimWorld\Need_Food.cs:19); same test as Alert_StarvationColonists.cs:20
	//   bleeding          HealthUtility.TicksUntilDeathDueToBloodLoss (Verse\HealthUtility.cs:552-561): int.MaxValue when
	//                     BleedRateTotal (Verse\HediffSet.cs:69) < 0.0001; a tended wound does not bleed
	//                     (Hediff_Injury.cs:158 returns 0 for IsTended). The health tab says "won't bleed out soon" at
	//                     60000 ticks or more (RimWorld\HealthCardUtility.cs:203-205): the same one-day limit is used.
	//                     Deathless pawns (Biotech) are skipped as HealthCardUtility.cs:205 does.
	//   fire in the home  same test as Alert_FireInHomeArea.cs:15-19: ThingDefOf.Fire things on a cell of
	//                     map.areaManager.Home (Verse\AreaManager.cs:17) that is not fogged (FogGrid.IsFogged,
	//                     Verse\FogGrid.cs:71-78; GridsUtility.cs:91 is the same call).
	//   idle              same test as the game's "Colonist idle" alert (RimWorld\Alert_ColonistsIdle.cs:15-47): free
	//                     colonists spawned on a home map whose Pawn_MindState.IsIdle holds (Verse.AI\Pawn_MindState.cs:
	//                     220-232: not downed, spawned, lastJobTag == JobTag.Idle), not quest lodgers (RimWorld\
	//                     QuestUtility.cs:524), no royal title with suppressIdleAlert (RimWorld\RoyalTitleDef.cs:40). The
	//                     game holds its alert back until day 1; this does not (user, 2026-10-14: "earlygame and pawns
	//                     slacking? that's a no-no."). One line when a colonist turns idle, naming every idle colonist;
	//                     a colonist is named as new again only an in-game hour after it was last reported, since a
	//                     pawn with little work flips between idle and a short job many times an hour. Does not pause.
	public static class AgentEvents
	{
		public sealed class Entry
		{
			public int seq;
			public int tick;
			public string when; // "day 5, 14h"
			public string kind;
			public string text;
			public string orderId;
		}

		private const int Keep = 500;
		private const int CheckEvery = 60;
		private const int MaxShownLines = 59;
		// User 2026-10-04: "events should be fairly short in description".
		private const int MaxLetterLine = 100;
		// "won't bleed out soon" limit of the health tab, HealthCardUtility.cs:205 (60000 = GenDate.TicksPerDay)
		private const int BleedLimitTicks = GenDate.TicksPerDay;

		private static readonly List<Entry> entries = new List<Entry>();
		private static int seq;

		// Checks.
		private static readonly HashSet<int> seenLetters = new HashSet<int>();
		private static readonly Dictionary<int, PawnState> pawnStates = new Dictionary<int, PawnState>();
		private static bool haveBaseline;
		private static bool homeFire;
		private static int nextCheck;

		// `show events.` remembers the tick it was last asked (or the tick the game was loaded).
		private static int sinceTick;

		private static readonly HashSet<string> loggedFaults = new HashSet<string>();

		private sealed class PawnState
		{
			public Pawn pawn;
			public bool downed;
			public bool mental;
			public bool starving;
			public bool bleeding;
		}

		public static int LastSeq
		{
			get { return seq; }
		}

		// ---- entries ---------------------------------------------------------------------------------------------

		// Writes an `event` router-log record and keeps the entry in memory (last 500). Never throws.
		public static Entry Add(string kind, string text, string orderId = null)
		{
			Entry e = new Entry();
			try
			{
				e.seq = ++seq;
				e.tick = NowTick();
				e.kind = kind ?? "";
				e.text = text ?? "";
				e.orderId = orderId;
				e.when = AgentTime.When(e.tick);
				entries.Add(e);
				if (entries.Count > Keep)
				{
					entries.RemoveRange(0, entries.Count - Keep);
				}
			}
			catch (Exception ex)
			{
				LogOnce("Add (memory)", ex);
			}
			try
			{
				AgentTime.EnsureLog();
				JObject fields = new JObject();
				fields["event_kind"] = e.kind;
				fields["text"] = e.text;
				fields["seq"] = e.seq;
				RouterLog.Write(new RouterLog.Ctx(orderId ?? "", 0), "event", fields);
			}
			catch (Exception ex)
			{
				LogOnce("Add (log)", ex);
			}
			return e;
		}

		// Entries with seq greater than the given one (a copy).
		public static List<Entry> Since(int afterSeq)
		{
			try
			{
				return entries.Where((Entry e) => e.seq > afterSeq).ToList();
			}
			catch (Exception)
			{
				return new List<Entry>();
			}
		}

		// New game or a load (AgentTime.Tick): forget what was seen. seq keeps counting.
		public static void Reset()
		{
			entries.Clear();
			seenLetters.Clear();
			pawnStates.Clear();
			batteryLevels.Clear();
			idleNow.Clear();
			idleReportedAt.Clear();
			AgentReminderScan.Reset();
			AgentDoorPathScan.Reset();
			haveBaseline = false;
			homeFire = false;
			nextCheck = 0;
			sinceTick = NowTick();
		}

		// ---- tick ------------------------------------------------------------------------------------------------

		// AgentTime.Tick, once per game tick; the checks run every 60 ticks. Never throws.
		public static void Tick()
		{
			try
			{
				if (Current.Game == null || Find.TickManager == null || Find.LetterStack == null)
				{
					return;
				}
				int tick = Find.TickManager.TicksGame;
				// Every tick: the colonists' walks through doors for the daily door line (AgentDoorPathScan.cs).
				RunCheck("doorwalks", AgentDoorPathScan.Track);
				// nextCheck far ahead of now (time went backwards): check now.
				if (tick < nextCheck && nextCheck - tick <= CheckEvery)
				{
					return;
				}
				nextCheck = tick + CheckEvery;
				bool baseline = !haveBaseline;
				RunCheck("letters", delegate { CheckLetters(baseline); });
				RunCheck("colonists", delegate { CheckColonists(baseline); });
				RunCheck("fire", delegate { CheckFire(baseline); });
				RunCheck("batteries", delegate { CheckBatteries(baseline); });
				RunCheck("idle", delegate { CheckIdle(baseline); });
				// Not an event line: the 6-hour work spot scan for the orchestrator's reminder (AgentReminderScan.cs).
				RunCheck("workspots", AgentReminderScan.Tick);
				haveBaseline = true;
			}
			catch (Exception ex)
			{
				LogOnce("Tick", ex);
			}
		}

		private static void RunCheck(string name, Action check)
		{
			try
			{
				check();
			}
			catch (Exception ex)
			{
				LogOnce(name, ex);
			}
		}

		private static void LogOnce(string where, Exception ex)
		{
			try
			{
				if (loggedFaults.Add(where))
				{
					Log.Error("[VerbalCommands] AgentEvents " + where + ": " + ex);
				}
			}
			catch (Exception)
			{
			}
		}

		private static int NowTick()
		{
			try
			{
				return Current.Game != null && Find.TickManager != null ? Find.TickManager.TicksGame : 0;
			}
			catch (Exception)
			{
				return 0;
			}
		}

		// An event that pauses a run: the line is written first, then the pause is asked for.
		private static void Pausing(string kind, string text)
		{
			Add(kind, text);
			try
			{
				AgentTime.RequestPause(text);
			}
			catch (Exception ex)
			{
				LogOnce("RequestPause", ex);
			}
		}

		// ---- letters ---------------------------------------------------------------------------------------------

		private static void CheckLetters(bool baseline)
		{
			List<Letter> stack = new List<Letter>(Find.LetterStack.LettersListForReading);
			foreach (Letter letter in stack)
			{
				if (letter == null || !seenLetters.Add(letter.ID) || baseline || !letter.CanShowInLetterStack)
				{
					continue;
				}
				bool choice = AsksForChoice(letter);
				string label = Clean(letter.Label.Resolve());
				if (label.Length == 0)
				{
					label = "(no title)";
				}
				string line = label + " (" + LetterKindWord(letter.def) + (choice ? ", needs a choice" : "") + ")";
				string sentence = FirstSentence(letter);
				if (sentence.Length > 0)
				{
					line += ": " + sentence;
				}
				line = Cut(line, MaxLetterLine);
				if (PausesByDef(letter.def) || choice)
				{
					Pausing("letter", line);
				}
				else
				{
					Add("letter", line);
				}
			}
		}

		// Letter kinds that pause: RimWorld\LetterDefOf.cs:8 ThreatBig, :10 ThreatSmall, :12 NegativeEvent, :18 Death.
		private static bool PausesByDef(LetterDef def)
		{
			return def != null && (def == LetterDefOf.ThreatBig || def == LetterDefOf.ThreatSmall
				|| def == LetterDefOf.NegativeEvent || def == LetterDefOf.Death);
		}

		private static string LetterKindWord(LetterDef def)
		{
			if (def == null)
			{
				return "notice";
			}
			if (def == LetterDefOf.ThreatBig || def == LetterDefOf.ThreatSmall)
			{
				return "threat";
			}
			if (def == LetterDefOf.NegativeEvent || def == LetterDefOf.RitualOutcomeNegative)
			{
				return "bad event";
			}
			if (def == LetterDefOf.Death)
			{
				return "death";
			}
			if (def == LetterDefOf.PositiveEvent || def == LetterDefOf.RitualOutcomePositive)
			{
				return "good event";
			}
			if (def == LetterDefOf.NeutralEvent)
			{
				return "event";
			}
			if (def == LetterDefOf.GameEnded)
			{
				return "game over";
			}
			return "notice";
		}

		// A letter that really waits for a decision. `show letters` lists ChoiceLetter.Choices
		// (FormWave1.cs:860-881 ChoicesOf), but that list also has the Close button of a plain letter:
		// StandardLetter.Choices yields Option_Close first (Verse\StandardLetter.cs:11), and Option_Close has an
		// action (Verse\ChoiceLetter.cs:35-42), so "has a choice option" is true for every StandardLetter. Here a
		// letter counts as asking for a choice when it is a ChoiceLetter, not a StandardLetter / DeathLetter /
		// NewQuestLetter (their Choices are only Close, jump, quest, hyperlinks: StandardLetter.cs:7-36,
		// DeathLetter.cs, NewQuestLetter.cs), and its Choices contain the game's "Postpone" button
		// (ChoiceLetter.Option_Postpone, Verse\ChoiceLetter.cs:92-104), which the game offers only on letters that
		// wait for an answer: ChoiceLetter_AcceptJoiner (the Postpone yield), ChoiceLetter_AcceptVisitors,
		// ChoiceLetter_AcceptCreepJoiner, ChoiceLetter_ChoosePawn, ChoiceLetter_RansomDemand, ChoiceLetter_BabyBirth,
		// ChoiceLetter_BabyToChild (all in RimWorld\). ChoiceLetter_BetrayVisitors and ChoiceLetter_EntityDiscovered
		// have only Close/jump/quest/hyperlink buttons and do not count. ChoiceLetter_GameEnded (RimWorld\) counts: its
		// options are keep watching / new wanderers / main menu.
		private static bool AsksForChoice(Letter letter)
		{
			ChoiceLetter choice = letter as ChoiceLetter;
			if (choice == null)
			{
				return false;
			}
			if (choice is ChoiceLetter_GameEnded)
			{
				return true;
			}
			if (choice is StandardLetter || choice is DeathLetter || choice is NewQuestLetter)
			{
				return false;
			}
			try
			{
				string postpone = Clean("PostponeLetter".Translate().ToString());
				FieldInfo textField = typeof(DiaOption).GetField("text", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
				if (textField == null)
				{
					return false;
				}
				foreach (DiaOption option in choice.Choices)
				{
					string text = Clean(textField.GetValue(option) as string);
					if (text.Length > 0 && text == postpone)
					{
						return true;
					}
				}
			}
			catch (Exception ex)
			{
				LogOnce("AsksForChoice", ex);
			}
			return false;
		}

		// First sentence of the letter text (IArchivable.ArchivedTooltip is the ChoiceLetter text,
		// Verse\ChoiceLetter.cs:155-158), tags stripped.
		private static string FirstSentence(Letter letter)
		{
			try
			{
				string raw = ((IArchivable)letter).ArchivedTooltip;
				if (string.IsNullOrEmpty(raw))
				{
					return "";
				}
				raw = Regex.Replace(raw, "<.*?>", "").Replace("\r", "");
				foreach (string paragraph in raw.Split('\n'))
				{
					string p = Regex.Replace(paragraph, @"\s+", " ").Trim();
					if (p.Length == 0)
					{
						continue;
					}
					Match m = Regex.Match(p, @"^(.+?[.!?])(\s|$)");
					return Cut(m.Success ? m.Groups[1].Value : p, MaxLetterLine);
				}
			}
			catch (Exception ex)
			{
				LogOnce("FirstSentence", ex);
			}
			return "";
		}

		// ---- colonists -------------------------------------------------------------------------------------------

		private static void CheckColonists(bool baseline)
		{
			HashSet<int> present = new HashSet<int>();
			foreach (Map map in Find.Maps)
			{
				if (map == null || !map.IsPlayerHome)
				{
					continue;
				}
				List<Pawn> colonists = new List<Pawn>(map.mapPawns.FreeColonists);
				foreach (Pawn pawn in colonists)
				{
					if (pawn == null || pawn.Dead)
					{
						continue;
					}
					int key = pawn.thingIDNumber;
					present.Add(key);
					bool downed = pawn.Downed;
					bool mental = pawn.InMentalState;
					bool starving = pawn.needs != null && pawn.needs.food != null && pawn.needs.food.Starving;
					int bleedTicks = BleedTicks(pawn);
					bool bleeding = bleedTicks < BleedLimitTicks;
					PawnState state;
					if (!pawnStates.TryGetValue(key, out state))
					{
						// First time seen (first check, or the pawn joined): the state is taken as the baseline.
						state = new PawnState();
						state.pawn = pawn;
						pawnStates[key] = state;
						state.downed = downed;
						state.mental = mental;
						state.starving = starving;
						state.bleeding = bleeding;
						continue;
					}
					state.pawn = pawn;
					string name = pawn.LabelShort;
					if (downed && !state.downed)
					{
						IntVec3 at = pawn.PositionHeld;
						Pausing("downed", name + " is downed at " + at.x + ", " + at.z + ".");
					}
					if (mental && !state.mental)
					{
						MentalStateDef def = pawn.MentalStateDef;
						string label = def != null ? Clean(def.label.NullOrEmpty() ? def.defName : def.label) : "unknown";
						Pausing("mental break", name + ": mental break: " + label + ".");
					}
					if (starving && !state.starving)
					{
						Pausing("starving", name + " is starving.");
					}
					if (bleeding && !state.bleeding)
					{
						int hours = Math.Max(1, (int)Math.Round(bleedTicks / (double)GenDate.TicksPerHour));
						Pausing("bleeding", name + " is bleeding: about " + hours + " h to death untended.");
					}
					state.downed = downed;
					state.mental = mental;
					state.starving = starving;
					state.bleeding = bleeding;
				}
			}
			// In the last check, not in this one: dead = died, otherwise it left the map (caravan): dropped, no event.
			List<int> gone = pawnStates.Keys.Where((int k) => !present.Contains(k)).ToList();
			foreach (int key in gone)
			{
				Pawn pawn = pawnStates[key].pawn;
				pawnStates.Remove(key);
				if (!baseline && pawn != null && pawn.Dead)
				{
					Pausing("died", pawn.LabelShort + " died.");
				}
			}
		}

		// Ticks until death by blood loss with nobody tending (int.MaxValue: not bleeding, or tended, or Deathless).
		private static int BleedTicks(Pawn pawn)
		{
			try
			{
				if (pawn.health == null || pawn.health.hediffSet == null)
				{
					return int.MaxValue;
				}
				if (ModsConfig.BiotechActive && pawn.genes != null && pawn.genes.HasActiveGene(GeneDefOf.Deathless))
				{
					return int.MaxValue;
				}
				return HealthUtility.TicksUntilDeathDueToBloodLoss(pawn);
			}
			catch (Exception ex)
			{
				LogOnce("BleedTicks", ex);
				return int.MaxValue;
			}
		}

		// ---- fire ------------------------------------------------------------------------------------------------

		private static void CheckFire(bool baseline)
		{
			int count = 0;
			IntVec3 first = IntVec3.Invalid;
			foreach (Map map in Find.Maps)
			{
				if (map == null || !map.IsPlayerHome)
				{
					continue;
				}
				Area home = map.areaManager.Home;
				List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
				for (int i = 0; i < fires.Count; i++)
				{
					IntVec3 cell = fires[i].Position;
					if (home[cell] && !map.fogGrid.IsFogged(cell))
					{
						if (count == 0)
						{
							first = cell;
						}
						count++;
					}
				}
			}
			bool now = count > 0;
			if (!baseline && now && !homeFire)
			{
				Pausing("fire", "Fire in the home area at " + first.x + ", " + first.z + " (" + count + (count == 1 ? " fire)." : " fires)."));
			}
			homeFire = now;
		}

		// ---- batteries ---------------------------------------------------------------------------------------------
		// User 2026-10-04: an event line when the batteries of a power net fall to 90%, 75%, 50%, 35% and so on, for
		// each net by itself (a colony can have separate grids). One line for each mark passed on the way down; a net
		// that charged back up above a mark gives the line again the next time it falls through it. Not a pause.
		// A net is told apart by its battery with the lowest id (the PowerNet objects are rebuilt when a conduit
		// changes). Facts: PowerNet.batteryComps / powerComps (RimWorld\PowerNet.cs), CompPowerBattery.StoredEnergy
		// and Props.storedEnergyMax, the 5 W a charged battery loses by itself (RimWorld\CompPowerBattery.cs:113-116),
		// CompPowerTrader.PowerOutput / PowerOn (RimWorld\CompPowerTrader.cs:36-45, :74-78).
		private static readonly int[] BatteryMarks = new int[] { 90, 75, 50, 35, 20, 10 };
		private const float BatterySelfDischargeWatts = 5f;
		// Net key (lowest battery id) -> how many marks its charge is below.
		private static readonly Dictionary<int, int> batteryLevels = new Dictionary<int, int>();

		// ---- idle colonists --------------------------------------------------------------------------------------

		private const int IdleReportAgainTicks = GenDate.TicksPerHour;
		private static readonly HashSet<int> idleNow = new HashSet<int>();
		private static readonly Dictionary<int, int> idleReportedAt = new Dictionary<int, int>();

		// The idle colonists on MAP, or on every home map when MAP is null; the test of Alert_ColonistsIdle.cs:15-47
		// without its day-1 wait (see the header). Also used by the idle tip (TipInjector.cs, state colonists-idle).
		public static List<Pawn> IdleColonists(Map onlyMap)
		{
			List<Pawn> result = new List<Pawn>();
			foreach (Map map in Find.Maps)
			{
				if (map == null || !map.IsPlayerHome || (onlyMap != null && map != onlyMap))
				{
					continue;
				}
				foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
				{
					if (pawn == null || pawn.mindState == null || !pawn.mindState.IsIdle || pawn.IsQuestLodger())
					{
						continue;
					}
					RoyalTitle title = pawn.royalty != null ? pawn.royalty.MostSeniorTitle : null;
					if (title != null && title.def.suppressIdleAlert)
					{
						continue;
					}
					result.Add(pawn);
				}
			}
			return result;
		}

		private static void CheckIdle(bool baseline)
		{
			int now = NowTick();
			List<Pawn> idle = IdleColonists(null);
			bool anyNew = false;
			HashSet<int> keys = new HashSet<int>();
			foreach (Pawn pawn in idle)
			{
				int key = pawn.thingIDNumber;
				keys.Add(key);
				if (idleNow.Contains(key))
				{
					continue;
				}
				int last;
				if (idleReportedAt.TryGetValue(key, out last) && now - last < IdleReportAgainTicks)
				{
					continue;
				}
				if (!baseline)
				{
					anyNew = true;
					idleReportedAt[key] = now;
				}
			}
			idleNow.Clear();
			idleNow.UnionWith(keys);
			if (!anyNew)
			{
				return;
			}
			int colonists = 0;
			foreach (Map map in Find.Maps)
			{
				if (map != null && map.IsPlayerHome)
				{
					colonists += map.mapPawns.FreeColonistsSpawnedCount;
				}
			}
			string names = string.Join(", ", idle.Select((Pawn p) => p.LabelShort).ToArray());
			Add("idle", Cut(names + (idle.Count == 1 ? " is" : " are") + " idle: " + idle.Count + " of " + colonists
				+ " colonists have nothing to do.", MaxLetterLine + 60));
		}

		private static void CheckBatteries(bool baseline)
		{
			HashSet<int> seen = new HashSet<int>();
			foreach (Map map in Find.Maps)
			{
				if (map == null || !map.IsPlayerHome || map.powerNetManager == null)
				{
					continue;
				}
				foreach (PowerNet net in map.powerNetManager.AllNetsListForReading)
				{
					float stored = 0f;
					float capacity = 0f;
					int charged = 0;
					Thing first = null;
					foreach (CompPowerBattery battery in net.batteryComps)
					{
						Thing t = battery.parent;
						if (t == null || !t.Spawned || t.Faction != Faction.OfPlayer)
						{
							continue;
						}
						if (first == null || t.thingIDNumber < first.thingIDNumber)
						{
							first = t;
						}
						capacity += battery.Props.storedEnergyMax;
						stored += battery.StoredEnergy;
						if (battery.StoredEnergy > 0f)
						{
							charged++;
						}
					}
					if (first == null || capacity <= 0f)
					{
						continue;
					}
					int key = first.thingIDNumber;
					seen.Add(key);
					float percent = stored / capacity * 100f;
					int below = 0;
					foreach (int mark in BatteryMarks)
					{
						if (percent < mark)
						{
							below++;
						}
					}
					int before;
					bool known = batteryLevels.TryGetValue(key, out before);
					batteryLevels[key] = below;
					if (baseline || !known || below <= before)
					{
						continue;
					}
					float watts = 0f;
					foreach (CompPowerTrader comp in net.powerComps)
					{
						if (comp.PowerOn)
						{
							watts += comp.PowerOutput;
						}
					}
					string line = "Batteries of the power net with the battery at " + AgentPlace.Describe(map, first.Position) + " fell below "
						+ BatteryMarks[below - 1] + "%: " + UnityEngine.Mathf.RoundToInt(stored) + " of " + UnityEngine.Mathf.RoundToInt(capacity) + " Wd, net "
						+ (watts >= 0f ? "+" : "") + UnityEngine.Mathf.RoundToInt(watts) + " W";
					float drain = -watts + BatterySelfDischargeWatts * charged;
					if (stored > 0f && drain > 0f)
					{
						float hours = stored / drain * 24f;
						line += hours < 1f ? ", under an hour left" : ", about " + UnityEngine.Mathf.RoundToInt(hours) + " hours left";
					}
					Add("power", Cut(line + ".", MaxLetterLine + 60));
				}
			}
			// A net whose batteries are gone is forgotten.
			foreach (int key in batteryLevels.Keys.ToList())
			{
				if (!seen.Contains(key))
				{
					batteryLevels.Remove(key);
				}
			}
		}

		// ---- show events ------------------------------------------------------------------------------------------

		private sealed class Line
		{
			public int tick;
			public string when;
			public string text;
		}

		// Forms `show events.` (since this was last asked, first time since the game was loaded) and
		// `show events since day {DAY}.` Read-only. Lines: the letters and messages of Find.Archive
		// (RimWorld\Archive.cs:14, filled by LetterStack.cs:78 and Messages.cs:59-61) plus this file's entries that are not
		// letters (the archive already has the letters).
		public static QueryReply QueryEvents(FormContext c)
		{
			int now = NowTick();
			int from;
			bool remember;
			if (c.Has("DAY"))
			{
				double? d = c.Number("DAY");
				if (!d.HasValue || d.Value < 1 || Math.Floor(d.Value) != d.Value)
				{
					throw new Fault("DAY must be a whole number from 1.");
				}
				int today = GenDate.DaysPassedAt(now) + 1;
				if (d.Value > today)
				{
					throw new Fault("Day " + d.Value.ToString("0", CultureInfo.InvariantCulture) + " has not come yet; today is day " + today + ".");
				}
				from = ((int)d.Value - 1) * GenDate.TicksPerDay;
				remember = false;
			}
			else
			{
				from = sinceTick;
				remember = true;
			}

			List<Line> lines = new List<Line>();
			foreach (IArchivable item in Find.Archive.ArchivablesListForReading)
			{
				Letter letter = item as Letter;
				Message message = item as Message;
				if (letter != null)
				{
					int tick = item.CreatedTicksGame;
					if (tick >= from)
					{
						lines.Add(new Line { tick = tick, when = AgentTime.When(tick), text = "letter (" + LetterKindWord(letter.def) + "): " + Clean(letter.Label.Resolve()) });
					}
				}
				else if (message != null)
				{
					int tick = item.CreatedTicksGame;
					if (tick >= from)
					{
						lines.Add(new Line { tick = tick, when = AgentTime.When(tick), text = Cut("message: " + Clean(message.text), MaxLetterLine) });
					}
				}
			}
			foreach (Entry e in entries)
			{
				if (e.kind != "letter" && e.tick >= from)
				{
					lines.Add(new Line { tick = e.tick, when = e.when, text = e.kind + ": " + e.text });
				}
			}
			List<Line> ordered = lines.OrderBy((Line l) => l.tick).ToList();

			string since = AgentTime.When(from);
			List<string> output = new List<string>();
			if (ordered.Count == 0)
			{
				output.Add("No events since " + since + ".");
			}
			else
			{
				output.Add("Events since " + since + " (" + ordered.Count + "):");
				int skipped = Math.Max(0, ordered.Count - MaxShownLines);
				List<Line> shown = ordered.Skip(skipped).ToList();
				foreach (Line l in shown)
				{
					output.Add(l.when + "  " + l.text);
				}
				if (skipped > 0)
				{
					int day = GenDate.DaysPassedAt(shown[0].tick) + 1;
					output.Add(skipped + " earlier events not shown: ask 'show events since day " + day + "'.");
				}
			}
			if (remember)
			{
				// The events of this very tick were just shown: the next question starts at the next tick.
				sinceTick = now + 1;
			}
			return QueryReply.Ok(string.Join("\n", output.ToArray()));
		}

		// ---- text ------------------------------------------------------------------------------------------------

		// The game's colour / hyperlink tags out, line breaks and runs of blanks to one space. Commas stay.
		private static string Clean(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return "";
			}
			return Regex.Replace(Regex.Replace(text, "<.*?>", ""), @"\s+", " ").Trim();
		}

		private static string Cut(string text, int max)
		{
			if (text == null || text.Length <= max)
			{
				return text ?? "";
			}
			return text.Substring(0, max - 3).TrimEnd() + "...";
		}
	}
}
