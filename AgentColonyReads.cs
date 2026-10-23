using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using VerbalCommands.Bridge;

namespace VerbalCommands
{
	// Agent 04, brief S4: the dispatcher for the colony reads, `show status.`, `show power.`, `show temperature.`
	// and `show mood of PAWN.` Read-only; every fact is what a player's screen shows (inspect pane, health tab, mood bar,
	// alerts bar, time controls). Nothing here calls DebugSettings / Prefs.DevMode or a getter that fills in a default.
	//
	// Game facts (sources\RimWorldDecompiled\, file:line):
	//   power nets      PowerNetManager.AllNetsListForReading (RimWorld\PowerNetManager.cs:42); PowerNet.powerComps /
	//                   batteryComps (PowerNet.cs:18-20); the net's gain rate is the sum of EnergyOutputPerTick of the comps that
	//                   are PowerOn (PowerNet.cs:181-196; I sum PowerOutput myself because CurrentEnergyGainRate also reads
	//                   DebugSettings.unlimitedPower, :183); stored energy (:198-206, a battery stunned by EMP counts 0)
	//   watts           CompPower.WattsToWattDaysPerTick = 1.6666667E-05 = 1/60000 (CompPower.cs:20), so 1 W for one day is
	//                   1 Wd and the inspect text prints rate / that constant as W (CompPower.cs:241-250)
	//   made / used     CompPowerTrader.PowerOutput (CompPowerTrader.cs:36-45): positive makes, negative uses; PowerOn (:74-78)
	//   needs power     the game's "needs power" overlay: not broken down, FlickUtility.WantsToBeOn, not PowerOn,
	//                   Props.showPowerNeededIfOff (CompPowerTrader.cs:232-241; CompProperties_Power.cs:23; WantsToBeOn is
	//                   RimWorld\FlickUtility.cs:33-55); a consumer is Props.PowerConsumption > 0 (CompProperties_Power.cs:37)
	//   switched off    CompFlickable.SwitchIsOn (CompFlickable.cs:38-42)
	//   battery         StoredEnergy (CompPowerBattery.cs:28), capacity Props.storedEnergyMax (CompProperties_Battery.cs:5),
	//                   self-discharge 5 W per battery while it holds energy (CompPowerBattery.cs:13, 66-70, 113-116)
	//   per day / hour  60000 ticks a day, 2500 ticks an hour: 1 W lasts 1 Wd a day, so days = Wd / W, hours = days * 24
	//   heater / cooler CompTempControl.TargetTemperature (CompTempControl.cs:15-22), PowerTrader (:31), the room's things
	//                   incl. adjacent walls Room.ContainedAndAdjacentThings (Verse\Room.cs:481)
	//   room            Room.Temperature (Verse\Room.cs:87-96), UsesOutdoorTemperature (:99-107), OpenRoofCount (:293-303),
	//                   ProperRoom (:536-553; touching the map edge is not a proper room), CellCount (:203)
	//   pawn temps      GenTemperature.GetTemperatureForCell (Verse\GenTemperature.cs:121-125), ComfortableTemperatureRange
	//                   (:59-62), SafeTemperatureRange = comfortable -10 / +10 (:87-92); Alert_Hypothermia (RimWorld\
	//                   Alert_Hypothermia.cs:15-26) and Alert_Heatstroke (Alert_Heatstroke.cs:15-24) use HediffDefOf.Hypothermia /
	//                   Heatstroke through HediffSet.GetFirstHediffOfDef(def, mustBeVisible) (Verse\HediffSet.cs:531-540)
	//   hediff label    Hediff.Label = label + " (stage label)" (Verse\Hediff.cs:59-66)
	//   health words    bleeding HediffSet.BleedRateTotal (HediffSet.cs:69), Hediff.Bleeding threshold 1E-05 (Hediff.cs:195);
	//                   needs tending HediffSet.HasTendableHediff (HediffSet.cs:790-799); sick HediffSet.AnyHediffMakesSickThought
	//                   (HediffSet.cs:216-228, the rule that gives the "sick" thought); pain HealthCardUtility.GetPainLabel
	//                   (RimWorld\HealthCardUtility.cs:1064-1085) when PainTotal is not zero; life-threatening is the rule of
	//                   Alert_LifeThreateningHediff (:15-37: IsCurrentlyLifeThreatening and not FullyImmune, Deathless gene
	//                   needs mtbDeathDestroysBrain)
	//   mood            Need_Mood.CurInstantLevel (Need_Mood.cs:19-33), MoodString (:36-70), Need.CurInstantLevelPercentage
	//                   (Need.cs:31, read by the mood bar every frame, Need.cs:260); the bar arrow tolerance 0.05
	//                   (Need_Seeker.cs:8-28); CurLevel moves towards CurInstantLevel (Need_Seeker.cs:40-52)
	//   mental breaker  BreakThresholdExtreme / Major / Minor (Verse.AI\MentalBreaker.cs:40-44), CanDoRandomMentalBreaks
	//                   (:46-65), BreakExtreme / Major / MinorIsImminent (:67-104), Blocked (:115); the mood tooltip prints
	//                   the three thresholds (Need_Mood.cs:92-99); the mood bar draws them only when CanDoRandomMentalBreaks
	//                   (Need_Mood.cs:111-118)
	//   inspiration     Pawn.Inspiration (Verse\Pawn.cs:335-345), Inspiration.InspectLine (RimWorld\Inspiration.cs:19-25)
	//   game speed      TickManager.CurTimeSpeed (Verse\TickManager.cs:187); Paused also includes forced pauses by open
	//                   windows (:130-139), so the speed words use CurTimeSpeed. TimeSpeed.Ultrafast is the dev speed
	//                   (Verse\TimeSpeed.cs) and is never named: it is printed as superfast.
	public static partial class FormPlanner
	{
		private const int ReadsWidth = 100;
		private const float ReadsSelfDischargeWatts = 5f;

		// ---- the dispatcher ------------------------------------------------------------------------------------------

		// Agent 10 adds one call to this inside FormQueries.Query. Null for any other form.
		private static QueryReply ReadsQuery(FormContext c)
		{
			switch (c.form)
			{
				case "query status":
					return QueryReply.Ok(ReadsStatus(c.map));
				case "query threats":
					return QueryReply.Ok(ReadsThreats(c.map));
				case "query food":
					return QueryReply.Ok(ReadsFood(c.map));
				case "query power":
					return QueryReply.Ok(ReadsPower(c.map));
				case "query temperature":
					return QueryReply.Ok(ReadsTemperature(c));
				case "query mood":
					return QueryReply.Ok(ReadsMood(c));
			}
			return null;
		}

		// ---- small helpers ----------------------------------------------------------------------------------------------

		// Adds the text as one line, or as several when it is wider than 100 characters (cut at a space, the rest indented).
		private static void ReadsAddWrapped(List<string> lines, string text, string indent)
		{
			string rest = text;
			string prefix = "";
			while (rest.Length + prefix.Length > ReadsWidth)
			{
				int limit = ReadsWidth - prefix.Length;
				int cut = rest.LastIndexOf(' ', Math.Min(rest.Length - 1, limit));
				if (cut <= 0)
				{
					cut = limit;
				}
				lines.Add(prefix + rest.Substring(0, cut).TrimEnd());
				rest = rest.Substring(cut).TrimStart();
				prefix = indent;
			}
			lines.Add(prefix + rest);
		}

		private static string ReadsSigned(float v)
		{
			return (v > 0f ? "+" : "") + Mathf.Round(v).ToString("0", CultureInfo.InvariantCulture);
		}

		private static string ReadsTemp(float celsius)
		{
			return celsius.ToStringTemperature("F0");
		}

		private static string ReadsTempRange(FloatRange range)
		{
			return ReadsTemp(range.min) + " to " + ReadsTemp(range.max);
		}

		private static string ReadsPlace(Map map, Thing t)
		{
			return AgentPlace.Describe(map, t.PositionHeld);
		}

		// A thing the player's screen shows and owns: spawned, player faction, not under fog.
		private static bool ReadsMine(Map map, Thing t)
		{
			return t != null && t.Spawned && t.Faction == Faction.OfPlayer && AgentPlace.Visible(map, t.PositionHeld);
		}

		// "Electric stove x2, Standing lamp" out of a list of things.
		private static List<string> ReadsCounted(IEnumerable<Thing> things)
		{
			List<string> order = new List<string>();
			Dictionary<string, int> counts = new Dictionary<string, int>();
			foreach (Thing t in things)
			{
				string label = PlainText(t.LabelNoCount);
				if (label.Length == 0)
				{
					label = PlainText(t.def.label);
				}
				if (!counts.ContainsKey(label))
				{
					counts[label] = 0;
					order.Add(label);
				}
				counts[label]++;
			}
			return order.Select((string l) => counts[l] > 1 ? l + " x" + counts[l] : l).ToList();
		}

		// ---- show status. -----------------------------------------------------------------------------------------------

		private static string ReadsStatus(Map map)
		{
			List<string> lines = new List<string>();

			// 1-2: date / hour / season, then outdoor temperature and weather on one line when it fits.
			string[] weather = QueryWeather(map).Split('\n');
			lines.Add(weather[0]);
			if (weather.Length > 1)
			{
				string second = weather[1];
				if (weather.Length > 2)
				{
					string joined = second + " " + weather[2];
					if (joined.Length <= ReadsWidth)
					{
						second = joined;
					}
					else
					{
						lines.Add(second);
						second = weather[2];
					}
				}
				lines.Add(second);
			}

			// game speed
			TickManager tm = Find.TickManager;
			string speed;
			switch (tm.CurTimeSpeed)
			{
				case TimeSpeed.Paused:
					speed = "paused";
					break;
				case TimeSpeed.Normal:
					speed = "normal";
					break;
				case TimeSpeed.Fast:
					speed = "fast";
					break;
				default:
					speed = "superfast";
					break;
			}
			lines.Add("Game speed: " + speed + ".");

			// threats
			List<string> threats = ReadsThreatLines(map, 5);
			if (threats.Count == 0)
			{
				lines.Add("Threats: none in view.");
			}
			else
			{
				lines.Add("Threats:");
				foreach (string t in threats)
				{
					ReadsAddWrapped(lines, "- " + t, "    ");
				}
			}

			// colonists: the lines of QueryColonists, matched to pawns by order of FreeColonists, plus one detail line each
			List<Pawn> colonists = map.mapPawns.FreeColonists.ToList();
			string[] colLines = QueryColonists(map).Split('\n');
			bool matched = colonists.Count > 0 && colLines.Length == colonists.Count + 1;
			if (matched)
			{
				ReadsAddWrapped(lines, colLines[0], "    ");
				for (int i = 0; i < colonists.Count; i++)
				{
					ReadsAddWrapped(lines, colLines[i + 1], "    ");
					if (!colonists[i].Dead)
					{
						ReadsAddWrapped(lines, "    " + ReadsColonistDetail(map, colonists[i]), "      ");
					}
				}
			}
			else if (colonists.Count == 0)
			{
				lines.Add("There are no colonists.");
			}
			else
			{
				lines.Add(colonists.Count + (colonists.Count == 1 ? " colonist:" : " colonists:"));
				foreach (Pawn p in colonists)
				{
					lines.Add("- " + p.LabelShort);
					if (!p.Dead)
					{
						ReadsAddWrapped(lines, "    " + ReadsColonistDetail(map, p), "      ");
					}
				}
			}

			ReadsAddWrapped(lines, ReadsFoodLine(map), "    ");
			ReadsAddWrapped(lines, ReadsPowerLine(map), "    ");

			// research: only the "Researching" / "No research project" lines of QueryResearch
			foreach (string line in QueryResearch().Split('\n'))
			{
				if (line.StartsWith("Researching "))
				{
					ReadsAddWrapped(lines, "Research: " + line.Substring("Researching ".Length), "    ");
				}
				else if (line.StartsWith("No research project is set"))
				{
					lines.Add("Research: no project is set.");
				}
			}

			// alerts and letters
			try
			{
				List<Alert> alerts = ActiveAlerts();
				List<string> critical = alerts.Where((Alert a) => a.Priority == AlertPriority.Critical).Select((Alert a) => PlainText(a.Label)).ToList();
				string alertLine = alerts.Count == 0 ? "Alerts: none." : "Alerts: " + alerts.Count;
				if (alerts.Count > 0 && critical.Count > 0)
				{
					alertLine += " (" + critical.Count + " critical: " + CapList(critical, 3) + ")";
				}
				ReadsAddWrapped(lines, alertLine, "    ");
			}
			catch (Fault)
			{
				lines.Add("Alerts: can't be read right now.");
			}
			int letters = Find.LetterStack.LettersListForReading.Count((Letter l) => l.CanShowInLetterStack);
			lines.Add(letters == 0 ? "Letters: none waiting." : "Letters: " + letters + " waiting.");
			lines.Add("Ask show alerts, show letters, show threats, show food or show power for details.");
			return ReadsJoin(lines);
		}

		// Where, mood against the minor break line, health words, food and rest.
		private static string ReadsColonistDetail(Map map, Pawn p)
		{
			List<string> parts = new List<string>();
			if (p.Spawned)
			{
				parts.Add("at " + AgentPlace.Describe(map, p.Position));
			}
			Need_Mood mood = p.needs != null ? p.needs.mood : null;
			if (mood != null && p.mindState != null && p.mindState.mentalBreaker != null && p.mindState.mentalBreaker.CanDoRandomMentalBreaks)
			{
				float threshold = p.mindState.mentalBreaker.BreakThresholdMinor;
				int diff = Mathf.RoundToInt((mood.CurLevelPercentage - threshold) * 100f);
				string line = diff >= 0 ? diff + " points above" : (-diff) + " points below";
				parts.Add("mood " + line + " the " + Percent(threshold) + " minor break line");
			}
			parts.Add(string.Join(", ", ReadsHealthWords(p).ToArray()));
			List<string> needs = new List<string>();
			if (p.needs != null && p.needs.food != null)
			{
				needs.Add("food " + Percent(p.needs.food.CurLevelPercentage));
			}
			if (p.needs != null && p.needs.rest != null)
			{
				needs.Add("rest " + Percent(p.needs.rest.CurLevelPercentage));
			}
			if (needs.Count > 0)
			{
				parts.Add(string.Join(", ", needs.ToArray()));
			}
			return string.Join("; ", parts.ToArray());
		}

		// Only the words that are true; "healthy" when none is.
		private static List<string> ReadsHealthWords(Pawn p)
		{
			List<string> words = new List<string>();
			if (p.health == null)
			{
				words.Add("healthy");
				return words;
			}
			HediffSet set = p.health.hediffSet;
			if (set.BleedRateTotal > 1E-05f)
			{
				words.Add("bleeding");
			}
			if (p.Downed)
			{
				words.Add("downed");
			}
			if (set.HasTendableHediff())
			{
				words.Add("needs tending");
			}
			if (set.AnyHediffMakesSickThought)
			{
				words.Add("sick");
			}
			if (set.PainTotal > 0.0001f)
			{
				words.Add("in pain (" + PlainText(HealthCardUtility.GetPainLabel(p).First).ToLowerInvariant() + ")");
			}
			if (ReadsLifeThreatening(p))
			{
				words.Add("life-threatening");
			}
			if (set.GetFirstHediffOfDef(HediffDefOf.Hypothermia, true) != null)
			{
				words.Add("hypothermia");
			}
			if (set.GetFirstHediffOfDef(HediffDefOf.Heatstroke, true) != null)
			{
				words.Add("heatstroke");
			}
			if (words.Count == 0)
			{
				words.Add("healthy");
			}
			return words;
		}

		// The rule of Alert_LifeThreateningHediff.SickPawns (Alert_LifeThreateningHediff.cs:15-37).
		private static bool ReadsLifeThreatening(Pawn p)
		{
			bool deathless = ModsConfig.BiotechActive && p.genes != null && p.genes.HasActiveGene(GeneDefOf.Deathless);
			foreach (Hediff h in p.health.hediffSet.hediffs)
			{
				if (!h.IsCurrentlyLifeThreatening || h.FullyImmune())
				{
					continue;
				}
				if (deathless)
				{
					HediffStage stage = h.CurStage;
					if (stage == null || !stage.mtbDeathDestroysBrain)
					{
						continue;
					}
				}
				return true;
			}
			return false;
		}

		// ---- power ---------------------------------------------------------------------------------------------------------

		private sealed class ReadsPowerNet
		{
			public int Count;
			public float Made;
			public float Used;
			public float Stored;
			public float Capacity;
			public int Batteries;
			public int BatteriesWithEnergy;
			public Thing FirstGenerator;
			public Thing FirstThing;
			public List<Thing> SwitchedOff = new List<Thing>();
			public List<Thing> NoPower = new List<Thing>();
		}

		// The game's "needs power" overlay rule for something that uses power (CompPowerTrader.cs:232-241).
		private static bool ReadsNeedsPower(CompPowerTrader comp)
		{
			Thing t = comp.parent;
			return comp.Props.PowerConsumption > 0f && !comp.PowerOn && comp.Props.showPowerNeededIfOff
				&& FlickUtility.WantsToBeOn(t) && !t.IsBrokenDown();
		}

		private static List<ReadsPowerNet> ReadsPowerGather(Map map, out List<Thing> unconnected)
		{
			List<ReadsPowerNet> result = new List<ReadsPowerNet>();
			foreach (PowerNet net in map.powerNetManager.AllNetsListForReading)
			{
				ReadsPowerNet info = new ReadsPowerNet();
				foreach (CompPowerTrader comp in net.powerComps)
				{
					Thing t = comp.parent;
					if (!ReadsMine(map, t))
					{
						continue;
					}
					info.Count++;
					if (info.FirstThing == null)
					{
						info.FirstThing = t;
					}
					float watts = comp.PowerOutput;
					if (comp.PowerOn)
					{
						if (watts > 0f)
						{
							info.Made += watts;
							if (info.FirstGenerator == null)
							{
								info.FirstGenerator = t;
							}
						}
						else if (watts < 0f)
						{
							info.Used -= watts;
						}
					}
					CompFlickable flick = t.TryGetComp<CompFlickable>();
					if (flick != null && !flick.SwitchIsOn)
					{
						info.SwitchedOff.Add(t);
					}
					else if (ReadsNeedsPower(comp))
					{
						info.NoPower.Add(t);
					}
				}
				foreach (CompPowerBattery battery in net.batteryComps)
				{
					Thing t = battery.parent;
					if (!ReadsMine(map, t))
					{
						continue;
					}
					info.Count++;
					info.Batteries++;
					if (info.FirstThing == null)
					{
						info.FirstThing = t;
					}
					if (!battery.StunnedByEMP)
					{
						info.Stored += battery.StoredEnergy;
						if (battery.StoredEnergy > 0f)
						{
							info.BatteriesWithEnergy++;
						}
					}
					info.Capacity += battery.Props.storedEnergyMax;
				}
				if (info.Count > 0)
				{
					result.Add(info);
				}
			}
			unconnected = new List<Thing>();
			foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.PowerTrader))
			{
				CompPowerTrader comp = t.TryGetComp<CompPowerTrader>();
				if (comp != null && comp.PowerNet == null && ReadsMine(map, t) && ReadsNeedsPower(comp))
				{
					unconnected.Add(t);
				}
			}
			return result;
		}

		private static string ReadsWd(float v)
		{
			return Mathf.Round(v).ToString("0", CultureInfo.InvariantCulture);
		}

		internal static string ReadsPowerLine(Map map)
		{
			List<Thing> unconnected;
			List<ReadsPowerNet> nets = ReadsPowerGather(map, out unconnected);
			if (nets.Count == 0 && unconnected.Count == 0)
			{
				return "Power: none.";
			}
			int without = unconnected.Count + nets.Sum((ReadsPowerNet n) => n.NoPower.Count);
			string line;
			if (nets.Count == 0)
			{
				line = "Power: no net";
			}
			else
			{
				line = "Power: " + nets.Count + (nets.Count == 1 ? " net" : " nets") + ", made " + ReadsWd(nets.Sum((ReadsPowerNet n) => n.Made))
					+ " W, used " + ReadsWd(nets.Sum((ReadsPowerNet n) => n.Used)) + " W";
				float capacity = nets.Sum((ReadsPowerNet n) => n.Capacity);
				if (capacity > 0f)
				{
					line += ", stored " + ReadsWd(nets.Sum((ReadsPowerNet n) => n.Stored)) + " of " + ReadsWd(capacity) + " Wd";
				}
			}
			if (without > 0)
			{
				line += "; " + without + (without == 1 ? " building" : " buildings") + " without power";
			}
			return line;
		}

		private static string ReadsPower(Map map)
		{
			List<Thing> unconnected;
			List<ReadsPowerNet> nets = ReadsPowerGather(map, out unconnected);
			if (nets.Count == 0 && unconnected.Count == 0)
			{
				return "No power: nothing on the map makes, stores or uses electricity.";
			}
			List<string> lines = new List<string>();
			int number = 0;
			foreach (ReadsPowerNet net in nets)
			{
				number++;
				Thing near = net.FirstGenerator ?? net.FirstThing;
				lines.Add("Power net " + number + " (near " + ReadsPlace(map, near) + "):");
				float netW = net.Made - net.Used;
				ReadsAddWrapped(lines, "  Made " + ReadsWd(net.Made) + " W, used " + ReadsWd(net.Used) + " W: net " + ReadsSigned(netW)
					+ " W, which is " + ReadsSigned(netW) + " Wd per day.", "    ");
				if (net.Batteries > 0)
				{
					string stored = "  Stored " + ReadsWd(net.Stored) + " of " + ReadsWd(net.Capacity) + " Wd";
					if (net.Capacity > 0f)
					{
						stored += " (" + Percent(net.Stored / net.Capacity) + ")";
					}
					lines.Add(stored + ".");
					// Draining: the net's loss plus the 5 W each charged battery loses by itself (CompPowerBattery.cs:113-116).
					float drain = -netW + ReadsSelfDischargeWatts * net.BatteriesWithEnergy;
					if (net.Stored > 0f && drain > 0f)
					{
						float hours = net.Stored / drain * 24f;
						lines.Add("  Batteries last about " + (hours < 1f ? "under an hour" : hours.ToString("0", CultureInfo.InvariantCulture) + (Mathf.Round(hours) == 1f ? " hour" : " hours"))
							+ " at this draw.");
					}
					else if (net.Stored <= 0f && netW < 0f)
					{
						lines.Add("  Batteries are empty and the net is losing power.");
					}
				}
				else if (netW < 0f)
				{
					lines.Add("  No battery on this net and it is losing power.");
				}
				ReadsPowerThingLines(map, lines, "  Switched off", net.SwitchedOff);
				ReadsPowerThingLines(map, lines, "  Not getting power", net.NoPower);
			}
			if (unconnected.Count > 0)
			{
				List<string> labels = ReadsCounted(unconnected);
				ReadsAddWrapped(lines, "Not on any power net: " + CapList(labels, 8) + ", first at " + ReadsPlace(map, unconnected[0]) + ".", "    ");
			}
			return ReadsJoin(lines);
		}

		private static void ReadsPowerThingLines(Map map, List<string> lines, string header, List<Thing> things)
		{
			if (things.Count == 0)
			{
				return;
			}
			lines.Add(header + " (" + things.Count + "):");
			foreach (Thing t in things.Take(10))
			{
				ReadsAddWrapped(lines, "    - " + PlainText(t.LabelCap) + " at " + ReadsPlace(map, t), "        ");
			}
			if (things.Count > 10)
			{
				lines.Add("    and " + (things.Count - 10) + " more");
			}
		}

		// ---- temperature -----------------------------------------------------------------------------------------------

		// Room cell nearest the room's average cell: a spot the agent can name; null when the room has no cells.
		private static IntVec3? ReadsRoomCell(Room room)
		{
			List<IntVec3> cells = room.Cells.ToList();
			if (cells.Count == 0)
			{
				return null;
			}
			float x = cells.Average((IntVec3 cell) => (float)cell.x);
			float z = cells.Average((IntVec3 cell) => (float)cell.z);
			IntVec3 best = cells[0];
			float bestDist = float.MaxValue;
			foreach (IntVec3 cell in cells)
			{
				float d = (cell.x - x) * (cell.x - x) + (cell.z - z) * (cell.z - z);
				if (d < bestDist)
				{
					bestDist = d;
					best = cell;
				}
			}
			return best;
		}

		private static List<CompTempControl> ReadsTempControls(Room room)
		{
			List<CompTempControl> result = new List<CompTempControl>();
			foreach (Thing t in room.ContainedAndAdjacentThings)
			{
				CompTempControl ct = t.TryGetComp<CompTempControl>();
				if (ct != null && t.Faction == Faction.OfPlayer && !result.Contains(ct))
				{
					result.Add(ct);
				}
			}
			return result;
		}

		// "Heater 21C on", "Cooler 10C no power", "Heater 21C switched off".
		private static string ReadsTempControlText(CompTempControl ct)
		{
			string text = PlainText(ct.parent.LabelNoCount) + " " + ReadsTemp(ct.TargetTemperature);
			CompPowerTrader trader = ct.PowerTrader;
			if (trader != null)
			{
				if (trader.PowerOn)
				{
					text += " on";
				}
				else
				{
					text += FlickUtility.WantsToBeOn(ct.parent) ? " no power" : " switched off";
				}
			}
			return text;
		}

		private static string ReadsRoomName(Room room)
		{
			string name = VocabularyText.RoomLabel(room);
			return string.IsNullOrEmpty(name) ? "room" : PlainText(name);
		}

		private static string ReadsTemperature(FormContext c)
		{
			Map map = c.map;
			if (c.Has("ROOM"))
			{
				return ReadsTemperatureOfRooms(map, ResolveRooms(map, c.Text("ROOM")));
			}
			List<string> lines = new List<string>();
			lines.Add("Outdoor temperature " + ReadsTemp(map.mapTemperature.OutdoorTemp) + ".");

			// enclosed rooms
			List<Room> rooms = new List<Room>();
			int outdoorLike = 0;
			foreach (Room room in StorageIndex.AllProperRooms(map))
			{
				IntVec3? spot = ReadsRoomCell(room);
				if (spot == null || !AgentPlace.Visible(map, spot.Value))
				{
					continue;
				}
				if (room.UsesOutdoorTemperature)
				{
					outdoorLike++;
					continue;
				}
				rooms.Add(room);
			}
			if (rooms.Count == 0)
			{
				lines.Add("No enclosed rooms yet: everyone is at the outdoor temperature.");
			}
			else
			{
				lines.Add("Enclosed rooms:");
				foreach (Room room in rooms.Take(20))
				{
					string text = "- " + ReadsRoomName(room) + ": " + ReadsTemp(room.Temperature);
					if (StorageIndex.IsGenericRoomRole(room.Role))
					{
						text += " at " + AgentPlace.Describe(map, ReadsRoomCell(room).Value);
					}
					List<string> controls = ReadsTempControls(room).Select(ReadsTempControlText).ToList();
					if (controls.Count > 0)
					{
						text += "; " + string.Join(", ", controls.ToArray());
					}
					ReadsAddWrapped(lines, text, "    ");
				}
				if (rooms.Count > 20)
				{
					lines.Add("and " + (rooms.Count - 20) + " more rooms left out.");
				}
			}
			if (outdoorLike > 0)
			{
				lines.Add(outdoorLike + (outdoorLike == 1 ? " room has" : " rooms have") + " an open roof and follows the outdoor temperature.");
			}

			// who is at risk
			List<Pawn> people = new List<Pawn>();
			foreach (Pawn p in map.mapPawns.FreeColonists)
			{
				people.Add(p);
			}
			foreach (Pawn p in map.mapPawns.PrisonersOfColony)
			{
				if (!people.Contains(p))
				{
					people.Add(p);
				}
			}
			List<string> risk = new List<string>();
			List<string> uncomfortable = new List<string>();
			foreach (Pawn p in people)
			{
				if (!p.Spawned || p.Dead || p.Suspended || p.health == null || !AgentPlace.Visible(map, p.Position))
				{
					continue;
				}
				float temp = GenTemperature.GetTemperatureForCell(p.Position, map);
				FloatRange safe = p.SafeTemperatureRange();
				Hediff cold = p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Hypothermia, true);
				Hediff heat = p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Heatstroke, true);
				bool outside = !safe.Includes(temp);
				if (outside || cold != null || heat != null)
				{
					string text = "- " + p.LabelShort + " at " + AgentPlace.Describe(map, p.Position) + ": " + ReadsTemp(temp) + ", safe range " + ReadsTempRange(safe);
					if (cold != null)
					{
						text += ", " + PlainText(cold.LabelCap);
					}
					if (heat != null)
					{
						text += ", " + PlainText(heat.LabelCap);
					}
					risk.Add(text);
				}
				else
				{
					FloatRange comfy = p.ComfortableTemperatureRange();
					if (!comfy.Includes(temp))
					{
						uncomfortable.Add(p.LabelShort + " " + ReadsTemp(temp) + " (comfortable " + ReadsTempRange(comfy) + ")");
					}
				}
			}
			List<string> animalRisk = new List<string>();
			foreach (Pawn p in map.mapPawns.SpawnedColonyAnimals)
			{
				if (p.Dead || p.Suspended || !AgentPlace.Visible(map, p.Position))
				{
					continue;
				}
				float temp = GenTemperature.GetTemperatureForCell(p.Position, map);
				bool sick = p.health != null && (p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Hypothermia, true) != null
					|| p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Heatstroke, true) != null);
				if (!p.SafeTemperatureRange().Includes(temp) || sick)
				{
					animalRisk.Add(p.LabelShort + " " + ReadsTemp(temp));
				}
			}
			if (risk.Count == 0 && animalRisk.Count == 0)
			{
				lines.Add("Nobody is at risk from the temperature.");
			}
			else
			{
				lines.Add("At risk:");
				foreach (string r in risk)
				{
					ReadsAddWrapped(lines, r, "    ");
				}
				if (animalRisk.Count > 0)
				{
					ReadsAddWrapped(lines, "- Animals outside their safe range: " + CapList(animalRisk, 5) + ".", "    ");
				}
			}
			if (uncomfortable.Count > 0)
			{
				ReadsAddWrapped(lines, "Uncomfortable but safe (mood only): " + CapList(uncomfortable, 5) + ".", "    ");
			}
			return ReadsJoin(lines);
		}

		private static string ReadsTemperatureOfRooms(Map map, List<Room> rooms)
		{
			List<string> lines = new List<string>();
			foreach (Room room in rooms)
			{
				IntVec3? spot = ReadsRoomCell(room);
				string head = ReadsRoomName(room) + (spot != null ? " at " + AgentPlace.Describe(map, spot.Value) : "") + ": " + ReadsTemp(room.Temperature) + ".";
				ReadsAddWrapped(lines, head, "    ");
				if (room.OpenRoofCount == 0)
				{
					lines.Add("  Roofed.");
				}
				else
				{
					lines.Add("  Open roof on " + room.OpenRoofCount + " of " + room.CellCount + " cells"
						+ (room.UsesOutdoorTemperature ? "; its temperature follows the outdoors (" + ReadsTemp(map.mapTemperature.OutdoorTemp) + ")." : "."));
				}
				List<string> controls = ReadsTempControls(room).Select(ReadsTempControlText).ToList();
				lines.Add(controls.Count > 0 ? "  Heaters and coolers: " + string.Join(", ", controls.ToArray()) + "." : "  No heater or cooler.");
				List<string> inside = new List<string>();
				foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
				{
					if (!p.Dead && AgentPlace.Visible(map, p.Position) && p.Position.GetRoom(map) == room)
					{
						inside.Add(p.LabelShort);
					}
				}
				lines.Add(inside.Count > 0 ? "  In it now: " + CapList(inside, 10) + "." : "  Nobody is in it now.");
			}
			return ReadsJoin(lines);
		}

		// ---- show mood of PAWN. --------------------------------------------------------------------------------------------

		private static string ReadsMood(FormContext c)
		{
			Map map = c.map;
			List<Pawn> pool = new List<Pawn>();
			pool.AddRange(map.mapPawns.FreeColonists);
			foreach (Pawn p in map.mapPawns.PrisonersOfColony)
			{
				if (!pool.Contains(p))
				{
					pool.Add(p);
				}
			}
			foreach (Pawn p in map.mapPawns.SlavesOfColonySpawned)
			{
				if (!pool.Contains(p))
				{
					pool.Add(p);
				}
			}
			Pawn pawn = ResolveOneFrom(c, "PAWN", pool, "colonist or prisoner");
			Need_Mood mood = pawn.needs != null ? pawn.needs.mood : null;
			if (mood == null)
			{
				throw new Fault(pawn.LabelShort + " has no mood the game shows.");
			}
			List<string> lines = new List<string>();
			lines.Add(pawn.LabelShort + ": Mood " + Percent(mood.CurLevelPercentage) + " (" + PlainText(mood.MoodString) + ").");

			MentalBreaker breaker = pawn.mindState != null ? pawn.mindState.mentalBreaker : null;
			if (breaker != null)
			{
				if (!breaker.CanDoRandomMentalBreaks)
				{
					lines.Add("Can't have random breaks now.");
				}
				else
				{
					lines.Add("Breaks: minor below " + Percent(breaker.BreakThresholdMinor) + ", major below " + Percent(breaker.BreakThresholdMajor)
						+ ", extreme below " + Percent(breaker.BreakThresholdExtreme) + ".");
					if (breaker.BreakExtremeIsImminent)
					{
						lines.Add("An extreme break is imminent.");
					}
					else if (breaker.BreakMajorIsImminent)
					{
						lines.Add("A major break is imminent.");
					}
					else if (breaker.BreakMinorIsImminent)
					{
						lines.Add("A minor break is imminent.");
					}
					else if (pawn.MentalState == null && mood.CurLevel < breaker.BreakThresholdMinor && breaker.Blocked)
					{
						lines.Add("Mood is under the minor line but mental breaks are blocked right now.");
					}
					else if (pawn.MentalState == null)
					{
						lines.Add("No break is imminent.");
					}
				}
			}
			if (pawn.MentalState != null)
			{
				ReadsAddWrapped(lines, "In a mental break now: " + PlainText(pawn.MentalState.InspectLine) + ".", "    ");
			}
			Inspiration inspiration = pawn.Inspiration;
			if (inspiration != null)
			{
				ReadsAddWrapped(lines, "Inspired: " + PlainText(inspiration.InspectLine) + ".", "    ");
			}

			// where the mood is heading (the bar arrow's tolerance, Need_Seeker.cs:8-28)
			float now = mood.CurLevelPercentage;
			float target = mood.CurInstantLevelPercentage;
			if (target > now + 0.05f)
			{
				lines.Add("Mood is rising to " + Percent(target) + ".");
			}
			else if (target < now - 0.05f)
			{
				lines.Add("Mood is falling to " + Percent(target) + ".");
			}
			else
			{
				lines.Add("Mood is steady near " + Percent(target) + ".");
			}

			// every thought group with its offset, biggest first
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
				List<KeyValuePair<string, float>> sorted = offsets.OrderByDescending((KeyValuePair<string, float> kv) => Math.Abs(kv.Value)).ToList();
				if (sorted.Count == 0)
				{
					lines.Add("Thoughts: none.");
				}
				else
				{
					lines.Add("Thoughts:");
					foreach (KeyValuePair<string, float> kv in sorted.Take(20))
					{
						ReadsAddWrapped(lines, "- " + kv.Key + " " + ReadsSigned(kv.Value), "    ");
					}
					if (sorted.Count > 20)
					{
						lines.Add("and " + (sorted.Count - 20) + " smaller thoughts left out.");
					}
				}
			}
			return ReadsJoin(lines);
		}
	}
}
