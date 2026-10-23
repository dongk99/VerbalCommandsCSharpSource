using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace VerbalCommands
{
	// pawn_watch.json: every pawn the colony looks after or meets, and every hostile in view, for the rimworld-play skill's
	// pawns_watch.ps1 (orchestrator, Monitor tool) and hostiles_watch.ps1 (the hostile_watch specialist). User, 2026-10-17:
	// "The agent orchestrator doesnt know if pawns are being actively attacked/chased. Solution: A perpetually opened shell
	// that only updates/notifies agent if the values change." / "even 1% change should do it" / "3 cells, not 5" / "Be as
	// descriptive as possible and do not try to write "short". should be as descriptive as what game shows." / "Scan entire
	// map. let agents filter out by category" / "colonists, visitors, traders, allies". Main thread only (AgentTime.Tick,
	// every 60 ticks); the file is rewritten only when something in it changed. The scripts decide what is new: a pawn's
	// "text" changed (1% steps: every number is a whole percent), it moved 3 cells or more, an attack line came or went.
	//
	// Lists (sources\RimWorldDecompiled\):
	//   pawns     every spawned pawn on a map that is not hostile to the colony (Pawn.HostileTo, RimWorld\GenHostility.cs),
	//             plus a pawn carried by one (Pawn_CarryTracker.CarriedThing): colonists, slaves, prisoners, colony mechs
	//             and animals, guests, visitors / traders / allies / travellers by their Lord job (LordJob_VisitColony,
	//             LordJob_TradeWithColony, LordJob_AssistColony, Verse.AI.Group\LordJob_TravelAndExit.cs), strangers, wild
	//             animals. Pawns of other factions only on cells that are not fogged (AgentPlace.Visible); ours always.
	//   hostiles  AgentTargets.Names (the names `attack TARGET` takes: "raider 2", "mad snowhare 1"), cells in view only.
	// Per pawn, what the health tab and gear tab show:
	//   health       SummaryHealthHandler.SummaryHealthPercent (Verse\SummaryHealthHandler.cs:14); pain HediffSet.PainTotal
	//                (Verse\HediffSet.cs:57); bleeding HediffSet.BleedRateTotal (:69), per day as the health tab writes it,
	//                and HealthUtility.TicksUntilDeathDueToBloodLoss (Verse\HealthUtility.cs:552)
	//   capacities   every PawnCapacityDef the tab shows for this pawn (PawnCapacityDef.CanShowOnPawn, Verse\PawnCapacityDef.cs:90)
	//   conditions   every visible hediff (Hediff.Visible, Verse\Hediff.cs:179) with its part, tended quality
	//                (HediffComp_TendDuration.tendQuality), bleeding (Hediff.BleedRate :191), permanent (HediffUtility.IsPermanent)
	//   wound ETA    Pawn_HealthTracker.HealthTickInterval (Verse\Pawn_HealthTracker.cs:1123-1159): every 600 ticks one
	//                random non-permanent injury heals 8 (+4 lying down, + the bed's bed_healPerDay) x each hediff stage's
	//                naturalHealingFactor x HealthScale x 0.01 x InjuryHealingFactor, and one random tended injury heals
	//                8 x lerp(tendQuality, 0.5..1.5) x the same; 100 such steps a day. Not while starving. An estimate: the
	//                game picks the injury at random, and resting or a bed changes the rate.
	//   disease ETA  immunity per day ImmunityRecord.ImmunityChangePerTick x 60000 (Verse\ImmunityRecord.cs:27, the game's own
	//                debug line in HediffComp_Immunizable.cs:135); severity per day the sum of the hediff's
	//                HediffComp_SeverityModifierBase.SeverityChangePerDay (Verse\HediffComp_SeverityModifierBase.cs:9,
	//                Immunizable :115, TendDuration :157); lethal at HediffDef.lethalSeverity
	//   gear         Pawn_EquipmentTracker.AllEquipmentListForReading (held), Pawn_InventoryTracker.innerContainer and
	//                Pawn_CarryTracker.CarriedThing (carried); Thing.LabelCap is the game's own label (quality, hit points)
	//   medicine     Pawn_PlayerSettings.medCare (RimWorld\Pawn_PlayerSettings.cs:27); a pawn without player settings gets
	//                the default the game would give it (the same choice as Pawn_PlayerSettings.cs:350-399)
	//   combatmode   Pawn_PlayerSettings.hostilityResponse (HostilityResponseMode; router: `set combatmode RESPONSE for PAWNS.`)
	// Attacks on a listed pawn (user: "Notified agent if they are being attacked, and by what source"):
	//   harmed       Pawn.PostApplyDamage (Verse\Pawn.cs:1914), Harmony postfix below: who did it (DamageInfo.Instigator)
	//                and when; counted for HarmWindow ticks. Only external violence (DamageDef.ExternalViolenceFor, as the
	//                game's own Pawn_MindState.Notify_DamageTaken, Verse.AI\Pawn_MindState.cs:655).
	//   aimed at     a pawn whose stance is Stance_Busy (warm-up or cool-down) with focusTarg = the listed pawn
	//                (Verse\Stance_Busy.cs:11)
	//   chased       a hostile whose job is AttackMelee / AttackStatic / PredatorHunt (RimWorld\JobDefOf.cs:38,40,502) on
	//                the listed pawn, not yet next to it
	//   melee        the user's words: "ranged attacks dont work when the source of attack is too close. That should change
	//                "pawn being engaged in melee on x,z from shooting, being attacked by {attack_source}"". The listed pawn
	//                holds a ranged weapon and was hit in melee: Verb_MeleeAttack sets mindState.meleeThreat and
	//                lastMeleeThreatHarmTick (RimWorld\Verb_MeleeAttack.cs:36-37); the game itself counts it for 400 ticks
	//                (Pawn_MindState.cs:240)
	public static class AgentPawnWatch
	{
		private const int CheckEvery = 60;
		private const int HarmWindow = 600;      // 10 game seconds after the last hit
		private const int MeleeWindow = 400;     // Pawn_MindState.cs:240
		private const int KeepLeft = 40;

		public const string FileName = "pawn_watch.json";

		private sealed class Hit
		{
			public Thing source;
			public int tick;
			public bool melee;
		}

		private sealed class Seen
		{
			public Pawn pawn;
			public string name;
			public string category;
		}

		private sealed class LeftEntry
		{
			public int seq;
			public string category;
			public string text;
		}

		// victim thingIDNumber -> instigator thingIDNumber -> last hit. Written by the Harmony postfix (main thread: damage
		// is applied in the game tick), read by Tick.
		private static readonly Dictionary<int, Dictionary<int, Hit>> hits = new Dictionary<int, Dictionary<int, Hit>>();
		private static readonly Dictionary<int, Seen> seen = new Dictionary<int, Seen>();
		private static readonly List<LeftEntry> left = new List<LeftEntry>();
		private static int leftSeq;
		// The same for the hostile list.
		private static readonly Dictionary<int, Seen> hostileSeen = new Dictionary<int, Seen>();
		private static readonly List<LeftEntry> hostilesLeft = new List<LeftEntry>();
		private static int hostilesLeftSeq;
		private static int nextCheck;
		private static bool haveBaseline;
		private static string lastKey;
		private static string path;
		private static bool loggedOnce;

		public static void Reset()
		{
			hits.Clear();
			seen.Clear();
			left.Clear();
			leftSeq = 0;
			hostileSeen.Clear();
			hostilesLeft.Clear();
			hostilesLeftSeq = 0;
			nextCheck = 0;
			haveBaseline = false;
			lastKey = null;
		}

		// From PawnHarmPatch. Never throws.
		public static void NoteDamage(Pawn victim, DamageInfo dinfo)
		{
			try
			{
				if (victim == null || dinfo.Instigator == null || dinfo.Def == null || !dinfo.Def.ExternalViolenceFor(victim))
				{
					return;
				}
				Dictionary<int, Hit> mine;
				if (!hits.TryGetValue(victim.thingIDNumber, out mine))
				{
					mine = new Dictionary<int, Hit>();
					hits[victim.thingIDNumber] = mine;
				}
				Hit h = new Hit();
				h.source = dinfo.Instigator;
				h.tick = Find.TickManager.TicksGame;
				h.melee = !dinfo.Def.isRanged && dinfo.Instigator.Spawned && victim.Spawned
					&& dinfo.Instigator.Position.AdjacentTo8WayOrInside(victim.Position);
				mine[dinfo.Instigator.thingIDNumber] = h;
			}
			catch (Exception)
			{
			}
		}

		// AgentTime.Tick, every game tick. Never throws.
		public static void Tick()
		{
			try
			{
				if (!AgentInbox.Enabled || Current.Game == null || Find.TickManager == null)
				{
					return;
				}
				int now = Find.TickManager.TicksGame;
				if (now < nextCheck && nextCheck - now <= CheckEvery)
				{
					return;
				}
				nextCheck = now + CheckEvery;
				Check(now);
			}
			catch (Exception ex)
			{
				if (!loggedOnce)
				{
					loggedOnce = true;
					Verse.Log.Warning("[VerbalCommands] pawn watch failed (logged once): " + ex);
				}
			}
		}

		// ---- the check ---------------------------------------------------------------------------------------------

		private static void Check(int now)
		{
			PruneHits(now);
			JArray pawns = new JArray();
			JArray hostiles = new JArray();
			Dictionary<int, Seen> nowSeen = new Dictionary<int, Seen>();
			Dictionary<int, Seen> nowHostiles = new Dictionary<int, Seen>();
			bool manyMaps = Find.Maps.Count > 1;
			int hostileCount = 0;

			foreach (Map map in Find.Maps)
			{
				List<Pawn> hostileList = new List<Pawn>();
				Dictionary<int, string> hostileNames = new Dictionary<int, string>();
				foreach (AgentTarget t in AgentTargets.Names(map))
				{
					hostileList.Add(t.Pawn);
					hostileNames[t.Pawn.thingIDNumber] = t.Name;
				}
				List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned.ToList();

				foreach (Pawn p in ListedPawns(map))
				{
					string category = Category(p);
					if (category == null)
					{
						continue;
					}
					Seen s = new Seen { pawn = p, name = p.LabelShort ?? p.LabelCap, category = category };
					nowSeen[p.thingIDNumber] = s;
					pawns.Add(PawnEntry(map, p, category, manyMaps, hostileList, hostileNames, now));
				}
				for (int i = 0; i < hostileList.Count; i++)
				{
					Pawn hp = hostileList[i];
					hostiles.Add(HostileEntry(map, hp, hostileNames[hp.thingIDNumber], manyMaps, colonists));
					nowHostiles[hp.thingIDNumber] = new Seen { pawn = hp, name = hostileNames[hp.thingIDNumber], category = HostileCategory(hp) };
					if (!hostileList[i].Downed)
					{
						hostileCount++;
					}
				}
			}

			NoteLeft(nowSeen);
			NoteHostilesLeft(nowHostiles);

			JObject o = new JObject();
			o["pawns"] = pawns;
			o["hostiles"] = hostiles;
			o["hostiles_standing"] = hostileCount;
			JArray l = new JArray();
			foreach (LeftEntry e in left)
			{
				JObject j = new JObject();
				j["seq"] = e.seq;
				j["category"] = e.category;
				j["text"] = e.text;
				l.Add(j);
			}
			o["left"] = l;
			o["last_left_seq"] = leftSeq;
			JArray hl = new JArray();
			foreach (LeftEntry e in hostilesLeft)
			{
				JObject j = new JObject();
				j["seq"] = e.seq;
				j["category"] = e.category;
				j["text"] = e.text;
				hl.Add(j);
			}
			o["hostiles_left"] = hl;
			o["last_hostiles_left_seq"] = hostilesLeftSeq;
			string key = o.ToString(Formatting.None);
			if (key == lastKey)
			{
				return;
			}
			lastKey = key;
			o["tick"] = now;
			o["when"] = AgentTime.When(now);
			o["utc"] = DateTime.UtcNow.ToString("o");
			Write(o);
		}

		// Spawned pawns of the map that are not hostile, and pawns they carry.
		private static IEnumerable<Pawn> ListedPawns(Map map)
		{
			IReadOnlyList<Pawn> spawned = map.mapPawns.AllPawnsSpawned;
			for (int i = 0; i < spawned.Count; i++)
			{
				Pawn p = spawned[i];
				if (p == null || p.Dead)
				{
					continue;
				}
				if (!IsOurs(p) && (p.HostileTo(Faction.OfPlayer) || !AgentPlace.Visible(map, p.Position)))
				{
					continue;
				}
				yield return p;
				Pawn carried = p.carryTracker != null ? p.carryTracker.CarriedThing as Pawn : null;
				if (carried != null && !carried.Dead && (IsOurs(carried) || !carried.HostileTo(Faction.OfPlayer)))
				{
					yield return carried;
				}
			}
		}

		private static bool IsOurs(Pawn p)
		{
			return p.Faction == Faction.OfPlayer || p.IsPrisonerOfColony || p.IsSlaveOfColony
				|| (p.guest != null && p.guest.HostFaction == Faction.OfPlayer);
		}

		// The category word the scripts filter on. Null: not listed.
		private static string Category(Pawn p)
		{
			RaceProperties race = p.RaceProps;
			if (p.IsPrisonerOfColony)
			{
				return "prisoner";
			}
			if (p.IsSlaveOfColony)
			{
				return "slave";
			}
			if (p.Faction == Faction.OfPlayer)
			{
				if (race.Humanlike)
				{
					return "colonist";
				}
				if (race.IsMechanoid)
				{
					return "colony mech";
				}
				if (race.Animal)
				{
					return "colony animal";
				}
				return "colony other";
			}
			if (p.guest != null && p.guest.HostFaction == Faction.OfPlayer)
			{
				return "guest";
			}
			if (race.Animal)
			{
				return p.Faction == null ? "wild animal" : "animal of " + FactionWord(p.Faction);
			}
			if (race.IsMechanoid)
			{
				return "mech";
			}
			if (race.IsAnomalyEntity)
			{
				return "entity";
			}
			if (!race.Humanlike)
			{
				return "other";
			}
			if (p.Faction == null)
			{
				return "stranger";
			}
			Lord lord = p.GetLord();
			LordJob job = lord != null ? lord.LordJob : null;
			if (p.trader != null && p.trader.traderKind != null || job is LordJob_TradeWithColony)
			{
				return "trader";
			}
			if (job is LordJob_AssistColony)
			{
				return "ally";
			}
			if (job is LordJob_VisitColony)
			{
				return "visitor";
			}
			if (job is LordJob_TravelAndExit)
			{
				return "traveller";
			}
			return "stranger";
		}

		private static string FactionWord(Faction f)
		{
			if (f == null)
			{
				return "no faction";
			}
			string kind = f == Faction.OfPlayer ? "your colony" : f.RelationKindWith(Faction.OfPlayer).ToString().ToLowerInvariant();
			return Plain(f.Name) + " (" + kind + ")";
		}

		// ---- one listed pawn ---------------------------------------------------------------------------------------

		private static JObject PawnEntry(Map map, Pawn p, string category, bool manyMaps, List<Pawn> hostileList,
			Dictionary<int, string> hostileNames, int now)
		{
			Pawn carrier = p.Spawned ? null : (p.ParentHolder as Pawn_CarryTracker)?.pawn;
			IntVec3 cell = carrier != null ? carrier.Position : p.Position;
			JObject o = new JObject();
			o["id"] = p.thingIDNumber;
			o["name"] = p.LabelShort;
			o["category"] = category;
			o["x"] = cell.x;
			o["z"] = cell.z;
			o["where"] = Where(map, cell, manyMaps, IsOurs(p));
			o["text"] = PawnText(map, p, category, carrier);
			o["attacks"] = Attacks(map, p, cell, hostileList, hostileNames, now);
			return o;
		}

		private static string PawnText(Map map, Pawn p, string category, Pawn carrier)
		{
			List<string> parts = new List<string>();
			string who = category;
			if (!IsOurs(p) || category == "guest")
			{
				if (p.Faction != null && p.Faction != Faction.OfPlayer)
				{
					who += ", " + FactionWord(p.Faction);
				}
				string kind = p.KindLabel;
				if (!string.IsNullOrEmpty(kind) && p.RaceProps.Humanlike)
				{
					who += ", " + Plain(kind);
				}
				Lord lord = p.GetLord();
				if (lord != null && lord.CurLordToil != null && p.Spawned)
				{
					string report = Plain(p.GetJobReport());
					if (report.Length > 0 && !p.RaceProps.Animal)
					{
						who += ", " + report.ToLowerInvariant();
					}
				}
			}
			else if (p.RaceProps.Animal || p.RaceProps.IsMechanoid)
			{
				who += ", " + Plain(p.KindLabel);
			}
			parts.Add("[" + who + "]");

			List<string> state = new List<string>();
			if (p.Downed)
			{
				state.Add("DOWNED");
			}
			if (p.Drafted)
			{
				state.Add("drafted");
			}
			if (p.InMentalState && p.MentalStateDef != null)
			{
				state.Add("mental state: " + Plain(p.MentalStateDef.label));
			}
			if (carrier != null)
			{
				state.Add("being carried by " + carrier.LabelShort + Doing(carrier));
			}
			foreach (Pawn c in map.mapPawns.FreeColonistsSpawned)
			{
				Job j = c.CurJob;
				if (j == null || j.targetA.Thing != p)
				{
					continue;
				}
				if (j.def == JobDefOf.Rescue)
				{
					state.Add("being rescued by " + c.LabelShort);
				}
				else if (j.def == JobDefOf.Arrest)
				{
					state.Add("being arrested by " + c.LabelShort);
				}
				else if (j.def == JobDefOf.Capture)
				{
					state.Add("being captured by " + c.LabelShort);
				}
				else if (j.def == JobDefOf.TendPatient)
				{
					state.Add("being tended by " + c.LabelShort);
				}
			}
			if (p.Spawned && p.InBed())
			{
				Building_Bed bed = p.CurrentBed();
				state.Add("lying in " + (bed != null ? Plain(bed.LabelNoCount) : "bed"));
			}
			if (state.Count > 0)
			{
				parts.Add(string.Join(", ", state));
			}

			parts.Add(HealthText(p, true));
			string conditions = ConditionsText(p);
			if (conditions.Length > 0)
			{
				parts.Add(conditions);
			}
			parts.Add(GearText(p));
			parts.Add("medicine: " + MedicineText(p));
			if (p.Faction == Faction.OfPlayer && p.RaceProps.Humanlike && p.playerSettings != null)
			{
				parts.Add("combatmode: " + CombatModeWord(p.playerSettings.hostilityResponse));
			}
			return string.Join(" | ", parts);
		}

		// " (rescuing)" style note from the carrier's job report, or "".
		private static string Doing(Pawn carrier)
		{
			try
			{
				string r = Plain(carrier.GetJobReport());
				return r.Length > 0 ? " (" + r.ToLowerInvariant() + ")" : "";
			}
			catch (Exception)
			{
				return "";
			}
		}

		internal static string CombatModeWord(HostilityResponseMode mode)
		{
			switch (mode)
			{
				case HostilityResponseMode.Attack:
					return "auto-engage";
				case HostilityResponseMode.Flee:
					return "flee";
				default:
					return "ignore";
			}
		}

		// "health 72%, pain 15%, bleeding 12%/day (bleeds out in 9 hours), consciousness 90%, moving 70%, ..."
		private static string HealthText(Pawn p, bool allCapacities)
		{
			List<string> h = new List<string>();
			h.Add("health " + Pct(p.health.summaryHealth.SummaryHealthPercent));
			float pain = p.health.hediffSet.PainTotal;
			if (pain > 0.005f)
			{
				h.Add("pain " + Pct(pain));
			}
			float bleed = p.health.hediffSet.BleedRateTotal;
			if (bleed > 0.0001f)
			{
				string line = "bleeding " + Pct(bleed) + "/day";
				int ticks = HealthUtility.TicksUntilDeathDueToBloodLoss(p);
				if (ticks < int.MaxValue)
				{
					line += " (bleeds out in " + Duration(ticks) + ")";
				}
				h.Add(line);
			}
			if (p.health.capacities != null)
			{
				foreach (PawnCapacityDef def in DefDatabase<PawnCapacityDef>.AllDefsListForReading)
				{
					if (!def.CanShowOnPawn(p))
					{
						continue;
					}
					if (!allCapacities && def != PawnCapacityDefOf.Moving && def != PawnCapacityDefOf.Manipulation && def != PawnCapacityDefOf.Consciousness)
					{
						continue;
					}
					h.Add(Plain(def.GetLabelFor()) + " " + Pct(p.health.capacities.GetLevel(def)));
				}
			}
			return string.Join(", ", h);
		}

		// Every visible condition, then the wound estimate.
		private static string ConditionsText(Pawn p)
		{
			List<string> c = new List<string>();
			float healable = 0f;
			float tendQualitySum = 0f;
			int tended = 0;
			foreach (Hediff hd in p.health.hediffSet.hediffs)
			{
				if (hd == null || !hd.Visible)
				{
					continue;
				}
				string text = Plain(hd.LabelCap);
				// Under one game hour left, the game's countdown in the label counts in seconds (HediffComp_Disappears,
				// showRemainingTime: cryptosleep sickness, anesthetic, ...), so the line changed on every check. It is
				// written in 0.1 hour steps instead (user, 2026-10-19: "0.1hr ~ 1hr steps are fine, so it's only shit that
				// counts in seconds, like cryptosleep sickness"; "Block them from spamming into poor agent's context.").
				HediffComp_Disappears gone = hd.TryGetComp<HediffComp_Disappears>();
				if (gone != null && gone.Props.showRemainingTime && gone.EffectiveTicksToDisappear < GenDate.TicksPerHour)
				{
					string seconds = Plain(gone.CompLabelInBracketsExtra);
					if (!string.IsNullOrEmpty(seconds))
					{
						text = text.Replace(seconds, Duration(gone.EffectiveTicksToDisappear));
					}
				}
				if (hd.Part != null)
				{
					text += " on " + Plain(hd.Part.Label);
				}
				List<string> d = new List<string>();
				Hediff_Injury inj = hd as Hediff_Injury;
				if (inj != null)
				{
					d.Add(Mathf.RoundToInt(inj.Severity).ToString(CultureInfo.InvariantCulture) + " damage");
					if (inj.IsPermanent())
					{
						d.Add("permanent");
					}
					else
					{
						healable += inj.Severity;
					}
				}
				if (hd.Bleeding)
				{
					d.Add("bleeding " + Pct(hd.BleedRate) + "/day");
				}
				HediffComp_TendDuration tend = hd.TryGetComp<HediffComp_TendDuration>();
				if (hd.IsTended() && tend != null)
				{
					d.Add("tended " + Pct(tend.tendQuality));
					if (inj != null && !inj.IsPermanent())
					{
						tendQualitySum += tend.tendQuality;
						tended++;
					}
				}
				else if (hd.TendableNow())
				{
					d.Add("needs tending");
				}
				string disease = DiseaseText(p, hd);
				if (disease.Length > 0)
				{
					d.Add(disease);
				}
				if (d.Count > 0)
				{
					text += " (" + string.Join(", ", d) + ")";
				}
				c.Add(text);
			}
			if (c.Count == 0)
			{
				return "";
			}
			string all = "conditions: " + string.Join("; ", c);
			if (healable > 0.001f)
			{
				all += "; wounds healed in " + WoundEstimate(p, healable, tended, tended > 0 ? tendQualitySum / tended : 0f);
			}
			return all;
		}

		// Pawn_HealthTracker.cs:1123-1159 (see the header).
		private static string WoundEstimate(Pawn p, float healable, int tended, float tendQuality)
		{
			if (!(p.RaceProps.IsFlesh || p.RaceProps.IsAnomalyEntity))
			{
				return "never (does not heal on its own)";
			}
			if (p.needs != null && p.needs.food != null && p.needs.food.Starving)
			{
				return "never while starving (no healing while starving)";
			}
			float natural = 8f;
			bool lying = p.GetPosture() != PawnPosture.Standing;
			Building_Bed bed = p.CurrentBed();
			if (lying)
			{
				natural += 4f;
				if (bed != null)
				{
					natural += bed.def.building.bed_healPerDay;
				}
			}
			foreach (Hediff hd in p.health.hediffSet.hediffs)
			{
				HediffStage st = hd.CurStage;
				if (st != null && st.naturalHealingFactor != -1f)
				{
					natural *= st.naturalHealingFactor;
				}
			}
			float factor = p.HealthScale * p.GetStatValue(StatDefOf.InjuryHealingFactor);
			float perDay = natural * factor;
			if (tended > 0)
			{
				perDay += 8f * GenMath.LerpDouble(0f, 1f, 0.5f, 1.5f, Mathf.Clamp01(tendQuality)) * factor;
			}
			if (perDay <= 0.0001f)
			{
				return "never at the current rate";
			}
			float days = healable / perDay;
			string how = lying ? (bed != null ? "resting in " + Plain(bed.LabelNoCount) : "lying down") : "standing (lying in a bed heals faster)";
			return "about " + Days(days) + " at the current rate (estimate; " + how + (tended > 0 ? ", " + tended + " tended" : ", none tended") + ")";
		}

		// "severity 40% (+12%/day), immunity 61% (+25%/day): immune in about 1.6 days, lethal in about 4.1 days if not immune first"
		private static string DiseaseText(Pawn p, Hediff hd)
		{
			HediffComp_Immunizable imm = hd.TryGetComp<HediffComp_Immunizable>();
			if (imm == null)
			{
				return "";
			}
			float sevPerDay = 0f;
			HediffWithComps wc = hd as HediffWithComps;
			if (wc != null)
			{
				foreach (HediffComp comp in wc.comps)
				{
					HediffComp_SeverityModifierBase m = comp as HediffComp_SeverityModifierBase;
					if (m != null)
					{
						sevPerDay += m.SeverityChangePerDay();
					}
				}
			}
			List<string> d = new List<string>();
			d.Add("severity " + Pct(hd.Severity) + " (" + SignedPct(sevPerDay) + "/day)");
			float immunity = imm.Immunity;
			if (imm.FullyImmune)
			{
				d.Add("immune: recovering");
				if (sevPerDay < 0f)
				{
					d.Add("gone in about " + Days(hd.Severity / -sevPerDay));
				}
				return string.Join(", ", d);
			}
			ImmunityRecord rec = p.health.immunity.GetImmunityRecord(hd.def);
			float immPerDay = rec != null ? rec.ImmunityChangePerTick(p, true, hd) * GenDate.TicksPerDay : 0f;
			string immText = "immunity " + Pct(immunity) + " (" + SignedPct(immPerDay) + "/day)";
			if (immPerDay > 0f)
			{
				immText += ": immune in about " + Days((1f - immunity) / immPerDay);
			}
			d.Add(immText);
			float lethal = hd.def.lethalSeverity;
			if (lethal > 0f && sevPerDay > 0f)
			{
				d.Add("lethal in about " + Days((lethal - hd.Severity) / sevPerDay) + " if not immune first");
			}
			return string.Join(", ", d);
		}

		// "holding bolt-action rifle (good 87%); carrying herbal medicine x3; hauling steel x75"
		private static string GearText(Pawn p)
		{
			List<string> g = new List<string>();
			List<string> held = new List<string>();
			if (p.equipment != null)
			{
				foreach (ThingWithComps t in p.equipment.AllEquipmentListForReading)
				{
					held.Add(Plain(t.LabelCap) + (t.def.IsRangedWeapon ? " (ranged)" : t.def.IsMeleeWeapon ? " (melee)" : ""));
				}
			}
			g.Add(held.Count > 0 ? "holding " + string.Join(", ", held) : "holding nothing");
			List<string> carried = new List<string>();
			if (p.inventory != null)
			{
				foreach (Thing t in p.inventory.innerContainer)
				{
					carried.Add(Plain(t.LabelCap));
				}
			}
			g.Add(carried.Count > 0 ? "carrying " + string.Join(", ", carried) : "carrying nothing");
			Thing hauled = p.carryTracker != null ? p.carryTracker.CarriedThing : null;
			if (hauled != null)
			{
				g.Add("hauling " + Plain(hauled.LabelCap));
			}
			return string.Join("; ", g);
		}

		private static string MedicineText(Pawn p)
		{
			if (p.playerSettings != null)
			{
				return Plain(p.playerSettings.medCare.GetLabel());
			}
			PlaySettings ps = Find.PlaySettings;
			MedicalCareCategory care;
			string why;
			if (p.Faction == null && p.RaceProps.Animal)
			{
				care = ps.defaultCareForWildlife;
				why = "wildlife";
			}
			else if (p.Faction == null)
			{
				care = ps.defaultCareForNoFaction;
				why = "no faction";
			}
			else
			{
				switch (p.Faction.RelationKindWith(Faction.OfPlayer))
				{
					case FactionRelationKind.Ally:
						care = ps.defaultCareForFriendlyFaction;
						why = "friendly faction";
						break;
					case FactionRelationKind.Hostile:
						care = ps.defaultCareForHostileFaction;
						why = "hostile faction";
						break;
					default:
						care = ps.defaultCareForNeutralFaction;
						why = "neutral faction";
						break;
				}
			}
			return Plain(care.GetLabel()) + " (the game's default for " + why + " pawns)";
		}

		// ---- attacks on a listed pawn --------------------------------------------------------------------------

		private static JArray Attacks(Map map, Pawn p, IntVec3 cell, List<Pawn> hostileList, Dictionary<int, string> hostileNames, int now)
		{
			Dictionary<int, string> attackers = new Dictionary<int, string>();   // source id -> description
			Dictionary<int, string> chasers = new Dictionary<int, string>();
			string meleeBy = null;
			int meleeId = 0;

			Dictionary<int, Hit> mine;
			if (hits.TryGetValue(p.thingIDNumber, out mine))
			{
				foreach (KeyValuePair<int, Hit> kv in mine)
				{
					if (now - kv.Value.tick > HarmWindow || kv.Value.source == null || kv.Value.source.Destroyed)
					{
						continue;
					}
					Pawn sp = kv.Value.source as Pawn;
					if (sp != null && (sp.Dead || sp.Downed))
					{
						continue;
					}
					attackers[kv.Key] = Source(kv.Value.source, hostileNames);
				}
			}
			// Aimed at (stance) and chased (job), from every hostile in view.
			for (int i = 0; i < hostileList.Count; i++)
			{
				Pawn h = hostileList[i];
				if (h.Downed || h.Dead)
				{
					continue;
				}
				Stance_Busy busy = h.stances != null ? h.stances.curStance as Stance_Busy : null;
				if (busy != null && busy.focusTarg.Thing == p)
				{
					attackers[h.thingIDNumber] = Source(h, hostileNames);
					continue;
				}
				Job j = h.CurJob;
				if (j != null && j.targetA.Thing == p && (j.def == JobDefOf.AttackMelee || j.def == JobDefOf.AttackStatic || j.def == JobDefOf.PredatorHunt))
				{
					if (h.Position.AdjacentTo8WayOrInside(cell))
					{
						attackers[h.thingIDNumber] = Source(h, hostileNames);
					}
					else if (!attackers.ContainsKey(h.thingIDNumber))
					{
						chasers[h.thingIDNumber] = Source(h, hostileNames) + ", " + h.Position.DistanceTo(cell).ToString("0", CultureInfo.InvariantCulture) + " cells away";
					}
				}
			}
			// Melee on a pawn that holds a ranged weapon.
			ThingWithComps primary = p.equipment != null ? p.equipment.Primary : null;
			if (primary != null && primary.def.IsRangedWeapon && p.mindState != null)
			{
				Pawn threat = p.mindState.meleeThreat;
				if (threat != null && !threat.Dead && !threat.Downed && now - p.mindState.lastMeleeThreatHarmTick <= MeleeWindow)
				{
					meleeId = threat.thingIDNumber;
					meleeBy = Source(threat, hostileNames);
				}
				else if (mine != null)
				{
					foreach (KeyValuePair<int, Hit> kv in mine)
					{
						if (kv.Value.melee && now - kv.Value.tick <= MeleeWindow && attackers.ContainsKey(kv.Key))
						{
							meleeId = kv.Key;
							meleeBy = attackers[kv.Key];
							break;
						}
					}
				}
			}

			JArray a = new JArray();
			string name = p.LabelShort;
			string at = cell.x.ToString(CultureInfo.InvariantCulture) + ", " + cell.z.ToString(CultureInfo.InvariantCulture);
			if (meleeBy != null)
			{
				a.Add(AttackLine("melee:" + meleeId,
					name + " being engaged in melee on " + at + " from shooting, being attacked by " + meleeBy,
					name + " is no longer engaged in melee by " + meleeBy));
			}
			foreach (KeyValuePair<int, string> kv in attackers)
			{
				if (kv.Key == meleeId && meleeBy != null)
				{
					continue;
				}
				a.Add(AttackLine("attack:" + kv.Key, name + " being attacked by " + kv.Value,
					name + " is no longer being attacked by " + kv.Value + " (no hit or aim for 10 game seconds)"));
			}
			foreach (KeyValuePair<int, string> kv in chasers)
			{
				string src = kv.Value;
				int comma = src.LastIndexOf(", ", StringComparison.Ordinal);
				string bare = comma > 0 ? src.Substring(0, comma) : src;
				a.Add(AttackLine("chase:" + kv.Key, name + " being chased by " + src, name + " is no longer being chased by " + bare));
			}
			return a;
		}

		private static JObject AttackLine(string key, string on, string off)
		{
			JObject o = new JObject();
			o["key"] = key;
			o["on"] = on;
			o["off"] = off;
			return o;
		}

		// "raider 2 (Kowalski, pirate, holding knife (melee))", "mad snowhare 1 (manhunter snowhare)", "mini-turret", "Bob (one of yours)".
		private static string Source(Thing t, Dictionary<int, string> hostileNames)
		{
			Pawn sp = t as Pawn;
			if (sp == null)
			{
				string what = Plain(t.LabelShort);
				if (t.Faction != null && t.Faction != Faction.OfPlayer)
				{
					what += " (" + FactionWord(t.Faction) + ")";
				}
				else if (t.Faction == Faction.OfPlayer)
				{
					what += " (one of yours)";
				}
				return what;
			}
			string name;
			List<string> d = new List<string>();
			if (!hostileNames.TryGetValue(sp.thingIDNumber, out name))
			{
				name = AgentTargets.NameOf(sp) ?? sp.LabelShort;
			}
			if (sp.RaceProps.Humanlike && sp.LabelShort != name)
			{
				d.Add(sp.LabelShort);
			}
			d.Add(sp.InMentalState && sp.MentalStateDef != null
				? Plain(sp.MentalStateDef.label) + " " + Plain(sp.KindLabel)
				: Plain(sp.KindLabel));
			if (sp.Faction == Faction.OfPlayer || sp.IsSlaveOfColony)
			{
				d.Add("one of yours");
			}
			ThingWithComps w = sp.equipment != null ? sp.equipment.Primary : null;
			if (w != null)
			{
				d.Add("holding " + Plain(w.LabelCap) + (w.def.IsRangedWeapon ? " (ranged)" : " (melee)"));
			}
			return name + " (" + string.Join(", ", d.Distinct()) + ")";
		}

		// ---- one hostile ---------------------------------------------------------------------------------------

		private static JObject HostileEntry(Map map, Pawn h, string name, bool manyMaps, List<Pawn> colonists)
		{
			JObject o = new JObject();
			o["id"] = h.thingIDNumber;
			o["name"] = name;
			o["category"] = HostileCategory(h);
			o["x"] = h.Position.x;
			o["z"] = h.Position.z;
			o["where"] = Where(map, h.Position, manyMaps, false);
			o["text"] = HostileText(map, h, name);
			Pawn nearest = null;
			float best = float.MaxValue;
			foreach (Pawn c in colonists)
			{
				float dist = c.Position.DistanceTo(h.Position);
				if (dist < best)
				{
					best = dist;
					nearest = c;
				}
			}
			o["nearest"] = nearest != null
				? "nearest colonist " + nearest.LabelShort + " " + best.ToString("0", CultureInfo.InvariantCulture) + " cells away"
				: "no colonist on this map";
			return o;
		}

		private static string HostileCategory(Pawn h)
		{
			RaceProperties r = h.RaceProps;
			if (r.Humanlike)
			{
				return "humanlike";
			}
			if (r.IsMechanoid)
			{
				return "mech";
			}
			if (r.Insect)
			{
				return "insect";
			}
			if (r.IsAnomalyEntity)
			{
				return "entity";
			}
			if (r.Animal)
			{
				return "animal";
			}
			return "other";
		}

		// "raider 2 (Kowalski, pirate, Pirate Gang (hostile); humanlike) holding bolt-action rifle (good) (ranged) | health 64%,
		//  pain 30%, bleeding 20%/day, moving 80%, manipulation 100%, consciousness 90% | wounds: 3 | attacking Alice"
		private static string HostileText(Map map, Pawn h, string name)
		{
			List<string> parts = new List<string>();
			List<string> who = new List<string>();
			if (h.RaceProps.Humanlike && h.LabelShort != name)
			{
				who.Add(h.LabelShort);
			}
			if (h.InMentalState && h.MentalStateDef != null)
			{
				who.Add(Plain(h.MentalStateDef.label) + " " + Plain(h.KindLabel));
			}
			else
			{
				who.Add(Plain(h.KindLabel));
			}
			who.Add(h.Faction != null ? FactionWord(h.Faction) : "no faction");
			who.Add(HostileCategory(h));
			string head = name + " (" + string.Join(", ", who) + ")";
			ThingWithComps w = h.equipment != null ? h.equipment.Primary : null;
			if (w != null)
			{
				head += " holding " + Plain(w.LabelCap) + (w.def.IsRangedWeapon ? " (ranged)" : " (melee)");
			}
			else if (h.RaceProps.Humanlike)
			{
				head += " holding nothing";
			}
			parts.Add(head);
			if (h.Downed)
			{
				parts.Add("DOWNED");
			}
			parts.Add(HealthText(h, false));
			int wounds = 0;
			foreach (Hediff hd in h.health.hediffSet.hediffs)
			{
				if (hd is Hediff_Injury && hd.Visible)
				{
					wounds++;
				}
			}
			parts.Add(wounds == 0 ? "not wounded" : wounds + " wound" + (wounds == 1 ? "" : "s"));
			if (!h.Downed)
			{
				parts.Add(Activity(h));
			}
			return string.Join(" | ", parts);
		}

		// What the hostile is doing: attacking / chasing one of ours, fleeing, else the game's own job report.
		private static string Activity(Pawn h)
		{
			Stance_Busy busy = h.stances != null ? h.stances.curStance as Stance_Busy : null;
			Thing focus = busy != null ? busy.focusTarg.Thing : null;
			if (focus != null && (focus.Faction == Faction.OfPlayer || (focus is Pawn && (((Pawn)focus).IsPrisonerOfColony || ((Pawn)focus).IsSlaveOfColony))))
			{
				return "attacking " + Plain(focus.LabelShort) + " at " + focus.Position.x + ", " + focus.Position.z;
			}
			Job j = h.CurJob;
			if (j != null)
			{
				Thing t = j.targetA.Thing;
				if (t != null && (j.def == JobDefOf.AttackMelee || j.def == JobDefOf.AttackStatic || j.def == JobDefOf.PredatorHunt))
				{
					string verb = t.Position.AdjacentTo8WayOrInside(h.Position) || j.def == JobDefOf.AttackStatic ? "attacking " : "chasing ";
					return verb + Plain(t.LabelShort) + " at " + t.Position.x + ", " + t.Position.z;
				}
				if (j.def == JobDefOf.Flee || j.def == JobDefOf.FleeAndCower)
				{
					return "fleeing";
				}
			}
			string report = "";
			try
			{
				report = Plain(h.GetJobReport());
			}
			catch (Exception)
			{
			}
			return report.Length > 0 ? "doing: " + report.ToLowerInvariant() : "doing: nothing the game shows";
		}

		// ---- who left the list ------------------------------------------------------------------------------------

		private static void NoteLeft(Dictionary<int, Seen> nowSeen)
		{
			if (!haveBaseline)
			{
				haveBaseline = true;
				seen.Clear();
				foreach (KeyValuePair<int, Seen> kv in nowSeen)
				{
					seen[kv.Key] = kv.Value;
				}
				return;
			}
			foreach (KeyValuePair<int, Seen> kv in seen)
			{
				Seen old = kv.Value;
				Seen cur;
				if (nowSeen.TryGetValue(kv.Key, out cur))
				{
					if (cur.category != old.category)
					{
						AddLeft(old.category, old.name + " changed from " + old.category + " to " + cur.category + Because(old.category, cur.category));
					}
					continue;
				}
				AddLeft(old.category, old.name + " [" + old.category + "] left the list: " + Reason(old.pawn));
			}
			seen.Clear();
			foreach (KeyValuePair<int, Seen> kv in nowSeen)
			{
				seen[kv.Key] = kv.Value;
			}
		}

		// A hostile that is no longer on the hostile list, and why. No baseline step: the list starts empty.
		private static void NoteHostilesLeft(Dictionary<int, Seen> nowHostiles)
		{
			foreach (KeyValuePair<int, Seen> kv in hostileSeen)
			{
				if (nowHostiles.ContainsKey(kv.Key))
				{
					continue;
				}
				LeftEntry e = new LeftEntry { seq = ++hostilesLeftSeq, category = kv.Value.category, text = kv.Value.name + " left the hostile list: " + HostileReason(kv.Value.pawn) };
				hostilesLeft.Add(e);
				if (hostilesLeft.Count > KeepLeft)
				{
					hostilesLeft.RemoveRange(0, hostilesLeft.Count - KeepLeft);
				}
			}
			hostileSeen.Clear();
			foreach (KeyValuePair<int, Seen> kv in nowHostiles)
			{
				hostileSeen[kv.Key] = kv.Value;
			}
		}

		private static string HostileReason(Pawn p)
		{
			try
			{
				if (p == null)
				{
					return "gone";
				}
				if (p.Dead)
				{
					return "killed";
				}
				if (p.IsPrisonerOfColony)
				{
					return "captured (now a prisoner, on the pawn list)";
				}
				if (p.Spawned && !p.HostileTo(Faction.OfPlayer))
				{
					return "no longer hostile" + (p.InMentalState ? "" : " (its mental state ended or its faction stopped the fight)") + "; now on the pawn list";
				}
				if (p.Spawned && p.Map != null && !AgentPlace.Visible(p.Map, p.Position))
				{
					return "out of view (fogged)";
				}
				if (!p.Spawned && p.ParentHolder is Pawn_CarryTracker)
				{
					Pawn carrier = ((Pawn_CarryTracker)p.ParentHolder).pawn;
					return "being carried by " + (carrier != null ? carrier.LabelShort : "someone");
				}
				if (!p.Spawned)
				{
					return "left the map";
				}
				return "no longer listed";
			}
			catch (Exception)
			{
				return "gone";
			}
		}

		private static string Because(string from, string to)
		{
			if (from == "prisoner" && to == "colonist")
			{
				return " (recruited)";
			}
			if (from == "prisoner" && to == "slave")
			{
				return " (enslaved)";
			}
			if (from == "slave" && to == "colonist")
			{
				return " (freed into the colony)";
			}
			if (to == "prisoner")
			{
				return " (taken prisoner)";
			}
			if (to == "colony animal")
			{
				return " (tamed)";
			}
			if (to == "colonist")
			{
				return " (joined the colony)";
			}
			return "";
		}

		private static string Reason(Pawn p)
		{
			try
			{
				if (p == null)
				{
					return "gone";
				}
				if (p.Dead)
				{
					return "died";
				}
				if (p.GetCaravan() != null)
				{
					return "left with a caravan";
				}
				if (p.Spawned && p.HostileTo(Faction.OfPlayer))
				{
					return "turned hostile (now on the hostile list)";
				}
				if (p.Spawned && p.Map != null && !AgentPlace.Visible(p.Map, p.Position))
				{
					return "out of view (fogged)";
				}
				if (!p.Spawned && p.ParentHolder != null && !(p.ParentHolder is Pawn_CarryTracker))
				{
					return "inside " + Plain(p.ParentHolder.ToString());
				}
				if (!p.Spawned)
				{
					return "left the map";
				}
				return "no longer listed";
			}
			catch (Exception)
			{
				return "gone";
			}
		}

		private static void AddLeft(string category, string text)
		{
			LeftEntry e = new LeftEntry { seq = ++leftSeq, category = category, text = text };
			left.Add(e);
			if (left.Count > KeepLeft)
			{
				left.RemoveRange(0, left.Count - KeepLeft);
			}
		}

		// ---- helpers ----------------------------------------------------------------------------------------------

		private static void PruneHits(int now)
		{
			List<int> emptyVictims = null;
			foreach (KeyValuePair<int, Dictionary<int, Hit>> kv in hits)
			{
				List<int> old = null;
				foreach (KeyValuePair<int, Hit> h in kv.Value)
				{
					if (now - h.Value.tick > HarmWindow)
					{
						(old = old ?? new List<int>()).Add(h.Key);
					}
				}
				if (old != null)
				{
					foreach (int k in old)
					{
						kv.Value.Remove(k);
					}
				}
				if (kv.Value.Count == 0)
				{
					(emptyVictims = emptyVictims ?? new List<int>()).Add(kv.Key);
				}
			}
			if (emptyVictims != null)
			{
				foreach (int k in emptyVictims)
				{
					hits.Remove(k);
				}
			}
		}

		// "120, 85 (in bedroom)" for our own; "158, 105 (in outdoors; 20 tiles east of Bob)" for others.
		private static string Where(Map map, IntVec3 cell, bool manyMaps, bool ours)
		{
			string room = "";
			try
			{
				Room r = cell.GetRoom(map);
				if (r != null)
				{
					room = r.PsychologicallyOutdoors ? "outdoors" : "in " + Plain(r.GetRoomRoleLabel());
				}
			}
			catch (Exception)
			{
			}
			string text = ours
				? cell.x.ToString(CultureInfo.InvariantCulture) + ", " + cell.z.ToString(CultureInfo.InvariantCulture) + (room.Length > 0 ? " (" + room + ")" : "")
				: AgentPlace.Describe(map, cell) + (room.Length > 0 ? " (" + room + ")" : "");
			if (manyMaps)
			{
				text += " on map " + Plain(map.Parent != null ? map.Parent.LabelCap : map.ToString());
			}
			return text;
		}

		private static string Pct(float f)
		{
			return Mathf.RoundToInt(f * 100f).ToString(CultureInfo.InvariantCulture) + "%";
		}

		private static string SignedPct(float f)
		{
			int v = Mathf.RoundToInt(f * 100f);
			return (v > 0 ? "+" : "") + v.ToString(CultureInfo.InvariantCulture) + "%";
		}

		private static string Days(float days)
		{
			if (float.IsNaN(days) || float.IsInfinity(days) || days < 0f)
			{
				return "never at the current rate";
			}
			if (days < 1f)
			{
				return (days * 24f).ToString("0.#", CultureInfo.InvariantCulture) + " hours";
			}
			return days.ToString("0.#", CultureInfo.InvariantCulture) + " days";
		}

		private static string Duration(int ticks)
		{
			return Days(ticks / (float)GenDate.TicksPerDay);
		}

		private static string Plain(string s)
		{
			return string.IsNullOrEmpty(s) ? "" : System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", "").Trim();
		}

		private static void Write(JObject o)
		{
			try
			{
				if (path == null)
				{
					string state = AgentInbox.StatePath;
					if (string.IsNullOrEmpty(state))
					{
						return;
					}
					path = Path.Combine(Path.GetDirectoryName(state), FileName);
				}
				string dir = Path.GetDirectoryName(path);
				if (!Directory.Exists(dir))
				{
					return;
				}
				string text = o.ToString(Formatting.Indented);
				string tmp = path + ".tmp";
				if (File.Exists(tmp))
				{
					File.Delete(tmp);
				}
				File.WriteAllBytes(tmp, new UTF8Encoding(false).GetBytes(text));
				if (File.Exists(path))
				{
					File.Delete(path);
				}
				File.Move(tmp, path);
			}
			catch (Exception ex)
			{
				if (!loggedOnce)
				{
					loggedOnce = true;
					Verse.Log.Warning("[VerbalCommands] could not write " + FileName + ": " + ex.Message);
				}
			}
		}
	}

	// Who hit whom: a Harmony postfix on Pawn.PostApplyDamage (Verse\Pawn.cs:1914). The game keeps the melee attacker
	// (Pawn_MindState.meleeThreat) but not a shooter, so the pawn watch records both here. Patched by hand, like
	// MerchantArrivalPatch.
	internal static class PawnHarmPatch
	{
		public static void Apply()
		{
			try
			{
				MethodInfo original = typeof(Pawn).GetMethod("PostApplyDamage", BindingFlags.Public | BindingFlags.Instance, null,
					new Type[] { typeof(DamageInfo), typeof(float) }, null);
				if (original == null)
				{
					Log.Warning("[VerbalCommands] Pawn.PostApplyDamage not found; the pawn watch will not name shooters.");
					return;
				}
				Harmony harmony = new Harmony("kopaz.verbalcommands.pawnharm");
				harmony.Patch(original, null, new HarmonyMethod(typeof(PawnHarmPatch).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)));
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] Could not patch Pawn.PostApplyDamage (is Harmony loaded?): " + ex.Message);
			}
		}

		public static void Postfix(Pawn __instance, DamageInfo dinfo)
		{
			AgentPawnWatch.NoteDamage(__instance, dinfo);
		}
	}
}
