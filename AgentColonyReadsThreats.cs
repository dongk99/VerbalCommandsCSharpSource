using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace VerbalCommands
{
	// Agent 04, S1: `show threats.` (form `query threats`). Read-only; everything printed is what a player's screen can
	// show: only things on cells where AgentPlace.Visible is true, no raid points, no timers, no faction plans, no Lord job
	// read for sieges. The dispatcher (`case "query threats"`) is written elsewhere (AgentColonyReads.cs).
	//
	// Game facts used (sources\RimWorldDecompiled\):
	//   hostile pawns        AgentTargets.Names(map) (agent 09; same names as `attack TARGET`), no list of our own
	//   humanlike / animal   RaceProperties.Humanlike (Verse\RaceProperties.cs:261), .Animal (:265-272), .Insect (:277),
	//                        .IsMechanoid (:338); Pawn.KindLabel (Verse\Pawn.cs:275)
	//   manhunting/berserk   MentalStateDefOf.Manhunter/ManhunterPermanent/ManhunterBloodRain (RimWorld\MentalStateDefOf.cs:33,35,45),
	//                        Pawn.InAggroMentalState (Verse\Pawn.cs:313-323: CurStateDef.IsAggro, covers Berserk :8,10,12,17)
	//   fleeing              what the inspect pane shows: Pawn.GetJobReport (Verse\Pawn.cs:3074-3079: the Lord job's report, else
	//                        the current driver's) and the colonist-bar "fleeing" icon, which is job FleeAndCower
	//                        (RimWorld\ColonistBarColonistDrawer.cs:327-330). A raider counts as fleeing when
	//                        - its mental state is PanicFlee (MentalStateDefOf.cs:31; LordToil_PanicFlee.Init starts it for a
	//                          faction that fled, RimWorld\LordToil_PanicFlee.cs:16-23, added by Verse.AI.Group\Lord.cs:306-313), or
	//                        - its current job is Flee / FleeAndCower (JobDefOf.cs:124,126), or
	//                        - its Lord's current toil (Lord.CurLordToil, Verse.AI.Group\Lord.cs:62) is an exit-map toil:
	//                          LordToil_ExitMap (Verse.AI.Group\LordToil_ExitMap.cs:5, duty ExitMapBest :11, :23-37; the raid
	//                          retreat toil, RimWorld\LordJob_AssaultColony.cs:105,119-125), its subclass LordToil_ExitMapFighting
	//                          (Verse.AI.Group\LordToil_ExitMapFighting.cs:5), LordToil_ExitMapAndDefendSelf
	//                          (RimWorld\LordToil_ExitMapAndDefendSelf.cs:6), LordToil_ExitMapRandom / LordToil_ExitMapNear
	//                          (Verse.AI.Group\LordToil_ExitMapRandom.cs:5, LordToil_ExitMapNear.cs:5), or LordToil_PanicFlee
	//   weapon               Pawn_EquipmentTracker.Primary (Verse\Pawn_EquipmentTracker.cs:16), Thing.LabelNoParenthesis (Verse\Thing.cs:442)
	//   predator hunts       JobDefOf.PredatorHunt (RimWorld\JobDefOf.cs:502); its prey is job target A (JobDriver_PredatorHunt.cs:14,
	//                        24-32, PreyInd = TargetIndex.A). A predator that recently attacked or is within 12 tiles of colony
	//                        prey is itself hostile (GenHostility.cs:186-197), so it may already be in AgentTargets: then its
	//                        line gets ", hunting <target>" instead of a second line.
	//   fires                ThingDefOf.Fire (RimWorld\ThingDefOf.cs:163), listerThings.ThingsOfDef and the game's own rule
	//                        areaManager.Home[position] && !Fogged (RimWorld\Alert_FireInHomeArea.cs:15-19); Fire.parent is the
	//                        thing the fire is attached to (Verse\AttachableThing.cs:7; Fire.Label "FireOn", RimWorld\Fire.cs:76-78)
	//   home area            Map.areaManager.Home (Verse\AreaManager.cs:17; Area_Home, RimWorld\Area_Home.cs:6), Area[IntVec3]
	//                        (Verse\Area.cs:60), Area.ActiveCells (Verse\Area.cs:84)
	//   drop pods            DropPodIncoming (RimWorld\DropPodIncoming.cs:6). A pod's faction is not stored on the pod
	//                        (RimWorld\DropPodUtility.cs:12-26, the faction only picks the def), and what is inside is not on
	//                        screen, so a hostile pod cannot be told from a friendly one: every visible incoming pod is printed
	//                        as "drop pods landing", with no contents
	//   hostile buildings    Faction.HostileTo(Faction.OfPlayer) (RimWorld\FactionUtility.cs:8) on things of ThingRequestGroup
	//                        BuildingArtificial (frames included: Verse\ThingDef.cs:1183-1195, Verse\ThingListGroupHelper.cs:76-79)
	//                        and Blueprint (Verse\ThingRequestGroupUtility.cs:20-22); a blueprint's faction is set by
	//                        GenConstruct.PlaceBlueprintForBuild (RimWorld\GenConstruct.cs:32); label of what it will become:
	//                        ThingDef.entityDefToBuild (Verse\ThingDef.cs:373; RimWorld\Blueprint.cs:11); the pawn spawn lists:
	//                        MapPawns.AllPawnsSpawned (Verse\MapPawns.cs:327), FreeColonistsSpawned (:329, :802-815)
	public static partial class FormPlanner
	{
		private const int ReadsPawnLineCap = 20;
		private const int ReadsHuntLineCap = 6;
		private const int ReadsPodLineCap = 4;
		private const int ReadsMaxWidth = 100;
		private const int ReadsMaxLines = 60;

		private sealed class ReadsHostile
		{
			public string Name;
			public Pawn Pawn;
			public bool Downed;
			public bool Fleeing;
			public int Group;
			public Pawn Near;
			public int Dist;
			public string Dir;
			public int HomeDist;
			public Pawn HuntTarget;
		}

		private sealed class ReadsHunter
		{
			public Pawn Predator;
			public Pawn Prey;
			public int Dist;
			public string Dir;
		}

		private sealed class ReadsFacts
		{
			public List<Pawn> Colonists = new List<Pawn>();
			public List<ReadsHostile> Hostiles = new List<ReadsHostile>();
			public List<ReadsHunter> Hunters = new List<ReadsHunter>();
			public int FireCells;
			public int FireInHome;
			public Thing NearestFire;
			public Pawn NearestFireColonist;
			public int NearestFireDist;
			public List<string> BurningPawns = new List<string>();
			public List<IntVec3> PodCells = new List<IntVec3>();
			public List<string> BuildingLabels = new List<string>();
			public List<int> BuildingCounts = new List<int>();
			public IntVec3 BuildingNear;
			public bool HasBuildings;
			public Area Home;
			public bool IsEmpty
			{
				get
				{
					return Hostiles.Count == 0 && Hunters.Count == 0 && FireCells == 0 && PodCells.Count == 0 && !HasBuildings;
				}
			}
		}

		private static readonly string[] ReadsGroupOne = new string[] { "raider", "mechanoid", "insect", "manhunting or berserk animal", "other hostile animal", "other hostile" };
		private static readonly string[] ReadsGroupMany = new string[] { "raiders", "mechanoids", "insects", "manhunting or berserk animals", "other hostile animals", "other hostiles" };

		// ---- public pieces other agent 04 files use --------------------------------------------------------------------

		// 8-point direction from `from` to `to`; x grows east, z grows north. Same cell: "here".
		internal static string ReadsCompass(IntVec3 from, IntVec3 to)
		{
			int dx = to.x - from.x;
			int dz = to.z - from.z;
			if (dx == 0 && dz == 0)
			{
				return "here";
			}
			// The direction word the orders read back ("N tiles DIRECTION of NAME", AgentPlace.cs DirectionWord), so a
			// place copied from this answer into an order means the same cell.
			return AgentPlace.DirectionWord(dx, dz);
		}

		// The nearest spawned free colonist (alive: MapPawns lists only live spawned pawns) to a cell; null when none.
		internal static Pawn ReadsNearestColonist(Map map, IntVec3 cell)
		{
			if (map == null || map.mapPawns == null)
			{
				return null;
			}
			Pawn best = null;
			int bestDist = int.MaxValue;
			List<Pawn> list = map.mapPawns.FreeColonistsSpawned;
			for (int i = 0; i < list.Count; i++)
			{
				Pawn p = list[i];
				if (p == null || p.Dead || !p.Spawned)
				{
					continue;
				}
				int d = ReadsDistSq(p.Position, cell);
				if (d < bestDist)
				{
					bestDist = d;
					best = p;
				}
			}
			return best;
		}

		// ---- the question ------------------------------------------------------------------------------------------------

		private static string ReadsThreats(Map map)
		{
			ReadsFacts facts = ReadsGather(map);
			if (facts.IsEmpty)
			{
				return "No threats in view.";
			}
			List<string> lines = new List<string>();

			// hostile pawns, by group
			int shown = 0;
			int skipped = 0;
			for (int g = 0; g < ReadsGroupOne.Length; g++)
			{
				int gg = g;
				List<ReadsHostile> members = facts.Hostiles.Where((ReadsHostile h) => h.Group == gg).OrderBy((ReadsHostile h) => h.Dist).ToList();
				if (members.Count == 0)
				{
					continue;
				}
				lines.Add(ReadsHeader(facts, g, members));
				// A bunch standing together is told as one group (nearest member, the box round it, what they hold);
				// one or two on their own keep a line each.
				foreach (List<ReadsHostile> cluster in ReadsClusters(members))
				{
					if (cluster.Count >= ReadsClusterMin)
					{
						lines.AddRange(ReadsClusterLines(map, cluster));
						continue;
					}
					foreach (ReadsHostile h in cluster)
					{
						if (shown >= ReadsPawnLineCap)
						{
							skipped++;
							continue;
						}
						shown++;
						lines.Add(ReadsPawnLine(map, h));
					}
				}
			}
			if (skipped > 0)
			{
				lines.Add("and " + skipped + " more");
			}

			// predators hunting (wild animals that are not hostile yet)
			int huntShown = 0;
			foreach (ReadsHunter hunter in facts.Hunters.OrderBy((ReadsHunter x) => x.Dist))
			{
				if (huntShown >= ReadsHuntLineCap)
				{
					lines.Add("and " + (facts.Hunters.Count - huntShown) + " more predators hunting");
					break;
				}
				huntShown++;
				lines.Add("- " + ReadsKind(hunter.Predator) + " hunting " + ReadsName(hunter.Prey) + ", " + ReadsTiles(hunter.Dist) + " "
					+ hunter.Dir + " of " + ReadsName(hunter.Prey) + ", at " + ReadsCell(map, hunter.Predator.PositionHeld));
			}

			// fires
			if (facts.FireCells > 0)
			{
				string fire = "fire: " + facts.FireCells + (facts.FireCells == 1 ? " burning cell" : " burning cells");
				if (facts.Home != null)
				{
					fire += ", " + facts.FireInHome + " in the home area";
				}
				lines.Add(fire);
				if (facts.NearestFire != null)
				{
					string near = "nearest fire: ";
					if (facts.NearestFireColonist != null)
					{
						near += ReadsFromColonist(facts.NearestFireColonist, facts.NearestFire.PositionHeld, facts.NearestFireDist) + ", at ";
					}
					else
					{
						near += "at ";
					}
					near += ReadsCell(map, facts.NearestFire.PositionHeld);
					Fire f = facts.NearestFire as Fire;
					Pawn on = f != null ? f.parent as Pawn : null;
					if (on != null)
					{
						near += " (on " + ReadsName(on) + ")";
					}
					lines.Add(near);
				}
				if (facts.BurningPawns.Count > 0)
				{
					lines.Add("on fire: " + CapList(facts.BurningPawns, 6));
				}
			}

			// drop pods
			for (int i = 0; i < facts.PodCells.Count && i < ReadsPodLineCap; i++)
			{
				lines.Add("drop pods landing at " + ReadsCell(map, facts.PodCells[i]));
			}
			if (facts.PodCells.Count > ReadsPodLineCap)
			{
				lines.Add("and " + (facts.PodCells.Count - ReadsPodLineCap) + " more drop pods landing");
			}

			// hostile buildings (siege gear is what the player sees as sandbags and mortars)
			if (facts.HasBuildings)
			{
				lines.Add("hostile buildings: " + ReadsBuildingList(facts) + " near " + ReadsCell(map, facts.BuildingNear));
			}

			return ReadsJoin(lines);
		}

		// A short summary for the status answer: at most `max` lines, one per group; empty list when nothing is in view.
		internal static List<string> ReadsThreatLines(Map map, int max)
		{
			List<string> result = new List<string>();
			if (max <= 0)
			{
				return result;
			}
			ReadsFacts facts = ReadsGather(map);
			if (facts.IsEmpty)
			{
				return result;
			}
			for (int g = 0; g < ReadsGroupOne.Length; g++)
			{
				int gg = g;
				List<ReadsHostile> members = facts.Hostiles.Where((ReadsHostile h) => h.Group == gg).OrderBy((ReadsHostile h) => h.Dist).ToList();
				if (members.Count == 0)
				{
					continue;
				}
				string line = members.Count + " " + (members.Count == 1 ? ReadsGroupOne[g] : ReadsGroupMany[g]);
				int downed = members.Count((ReadsHostile h) => h.Downed);
				int fleeing = members.Count((ReadsHostile h) => h.Fleeing);
				List<string> notes = new List<string>();
				if (downed > 0)
				{
					notes.Add(downed + " downed");
				}
				if (fleeing > 0)
				{
					notes.Add(fleeing + " fleeing");
				}
				if (notes.Count > 0)
				{
					line += " (" + string.Join(", ", notes.ToArray()) + ")";
				}
				ReadsHostile first = members[0];
				if (first.Near != null)
				{
					line += ", nearest " + ReadsFromColonist(first.Near, first.Pawn.PositionHeld, first.Dist);
				}
				result.Add(line);
			}
			if (facts.Hunters.Count > 0)
			{
				ReadsHunter h0 = facts.Hunters.OrderBy((ReadsHunter x) => x.Dist).First();
				string hunt = ReadsKind(h0.Predator) + " hunting " + ReadsName(h0.Prey) + ", " + ReadsTiles(h0.Dist) + " " + h0.Dir + " of " + ReadsName(h0.Prey);
				if (facts.Hunters.Count > 1)
				{
					hunt = facts.Hunters.Count + " predators hunting, nearest: " + hunt;
				}
				result.Add(hunt);
			}
			if (facts.FireCells > 0)
			{
				string fire = "fire: " + facts.FireCells + (facts.FireCells == 1 ? " cell" : " cells");
				if (facts.Home != null)
				{
					fire += ", " + facts.FireInHome + " in home area";
				}
				if (facts.NearestFire != null && facts.NearestFireColonist != null)
				{
					fire += ", nearest " + ReadsFromColonist(facts.NearestFireColonist, facts.NearestFire.PositionHeld, facts.NearestFireDist);
				}
				result.Add(fire);
			}
			if (facts.PodCells.Count > 0)
			{
				result.Add(facts.PodCells.Count + (facts.PodCells.Count == 1 ? " drop pod landing" : " drop pods landing"));
			}
			if (facts.HasBuildings)
			{
				result.Add("hostile buildings: " + ReadsBuildingList(facts));
			}
			if (result.Count > max)
			{
				result.RemoveRange(max, result.Count - max);
			}
			return result;
		}

		// ---- gathering ---------------------------------------------------------------------------------------------------

		private static ReadsFacts ReadsGather(Map map)
		{
			ReadsFacts facts = new ReadsFacts();
			if (map == null)
			{
				return facts;
			}
			List<Pawn> free = map.mapPawns.FreeColonistsSpawned;
			for (int i = 0; i < free.Count; i++)
			{
				if (free[i] != null && !free[i].Dead && free[i].Spawned)
				{
					facts.Colonists.Add(free[i]);
				}
			}
			Area home = map.areaManager != null ? map.areaManager.Home : null;
			List<IntVec3> homeCells = null;
			if (home != null && home.TrueCount > 0)
			{
				facts.Home = home;
				homeCells = home.ActiveCells.ToList();
			}

			// hostile pawns: the same names `attack TARGET` uses
			Dictionary<Pawn, ReadsHostile> byPawn = new Dictionary<Pawn, ReadsHostile>();
			List<AgentTarget> names = AgentTargets.Names(map);
			if (names != null)
			{
				foreach (AgentTarget t in names)
				{
					if (t == null || t.Pawn == null || t.Pawn.Dead || !t.Pawn.Spawned || !AgentPlace.Visible(map, t.Pawn.PositionHeld))
					{
						continue;
					}
					ReadsHostile h = new ReadsHostile();
					h.Name = t.Name;
					h.Pawn = t.Pawn;
					h.Downed = t.Downed || t.Pawn.Downed;
					h.Fleeing = ReadsIsFleeing(t.Pawn);
					h.Group = ReadsGroupOf(t.Pawn);
					IntVec3 cell = t.Pawn.PositionHeld;
					int d2;
					h.Near = ReadsNearestOf(facts.Colonists, cell, out d2);
					if (h.Near != null)
					{
						h.Dist = ReadsWhole(h.Near.Position, cell);
						h.Dir = ReadsCompass(h.Near.Position, cell);
					}
					else
					{
						h.Dist = int.MaxValue;
						h.Dir = "";
					}
					if (facts.Home != null)
					{
						if (facts.Home[cell])
						{
							h.HomeDist = 0;
						}
						else
						{
							h.HomeDist = ReadsNearestCellDist(homeCells, cell);
						}
					}
					else
					{
						h.HomeDist = -1;
					}
					facts.Hostiles.Add(h);
					byPawn[t.Pawn] = h;
				}
			}

			// predators hunting a colonist or a colony animal
			IReadOnlyList<Pawn> all = map.mapPawns.AllPawnsSpawned;
			for (int i = 0; i < all.Count; i++)
			{
				Pawn p = all[i];
				if (p == null || p.Dead || !p.Spawned || !p.RaceProps.Animal)
				{
					continue;
				}
				Job job = p.CurJob;
				if (job == null || job.def != JobDefOf.PredatorHunt)
				{
					continue;
				}
				Pawn prey = job.GetTarget(TargetIndex.A).Thing as Pawn;
				if (prey == null || prey.Dead || !prey.Spawned || prey.Faction != Faction.OfPlayer)
				{
					continue;
				}
				if (!AgentPlace.Visible(map, p.PositionHeld))
				{
					continue;
				}
				ReadsHostile already;
				if (byPawn.TryGetValue(p, out already))
				{
					already.HuntTarget = prey;
					continue;
				}
				ReadsHunter hunter = new ReadsHunter();
				hunter.Predator = p;
				hunter.Prey = prey;
				hunter.Dist = ReadsWhole(prey.Position, p.PositionHeld);
				hunter.Dir = ReadsCompass(prey.Position, p.PositionHeld);
				facts.Hunters.Add(hunter);
			}

			// fires (Alert_FireInHomeArea.cs:15-19 rule for "in the home area")
			List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
			int bestFire = int.MaxValue;
			for (int i = 0; i < fires.Count; i++)
			{
				Thing f = fires[i];
				if (f == null || f.Destroyed || !f.Spawned)
				{
					continue;
				}
				IntVec3 cell = f.PositionHeld;
				if (!AgentPlace.Visible(map, cell))
				{
					continue;
				}
				facts.FireCells++;
				if (home != null && home[cell])
				{
					facts.FireInHome++;
				}
				Fire fire = f as Fire;
				Pawn on = fire != null ? fire.parent as Pawn : null;
				if (on != null)
				{
					string n = ReadsName(on);
					if (!facts.BurningPawns.Contains(n))
					{
						facts.BurningPawns.Add(n);
					}
				}
				int d2;
				Pawn near = ReadsNearestOf(facts.Colonists, cell, out d2);
				if (near != null && d2 < bestFire)
				{
					bestFire = d2;
					facts.NearestFire = f;
					facts.NearestFireColonist = near;
					facts.NearestFireDist = ReadsWhole(near.Position, cell);
				}
				else if (facts.NearestFire == null && near == null)
				{
					facts.NearestFire = f;
				}
			}

			// incoming drop pods (visible ones only; contents are never read)
			List<Thing> things = map.listerThings.AllThings;
			for (int i = 0; i < things.Count; i++)
			{
				if (things[i] is DropPodIncoming && things[i].Spawned && AgentPlace.Visible(map, things[i].PositionHeld))
				{
					facts.PodCells.Add(things[i].PositionHeld);
				}
			}

			// hostile buildings and blueprints
			Faction player = Faction.OfPlayer;
			int bestB = int.MaxValue;
			ReadsScanBuildings(map, map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial), player, facts, ref bestB);
			ReadsScanBuildings(map, map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint), player, facts, ref bestB);
			return facts;
		}

		private static void ReadsScanBuildings(Map map, List<Thing> list, Faction player, ReadsFacts facts, ref int bestDist)
		{
			for (int i = 0; i < list.Count; i++)
			{
				Thing t = list[i];
				if (t == null || t.Destroyed || !t.Spawned || t.Faction == null || t.Faction == player || !t.Faction.HostileTo(player))
				{
					continue;
				}
				IntVec3 cell = t.PositionHeld;
				if (!AgentPlace.Visible(map, cell))
				{
					continue;
				}
				string label;
				if (t.def.IsBlueprint && t.def.entityDefToBuild != null)
				{
					label = PlainText(t.def.entityDefToBuild.label) + " (blueprint)";
				}
				else if (t.def.IsFrame && t.def.entityDefToBuild != null)
				{
					label = PlainText(t.def.entityDefToBuild.label) + " (frame)";
				}
				else
				{
					label = PlainText(t.def.label);
				}
				int idx = facts.BuildingLabels.IndexOf(label);
				if (idx < 0)
				{
					facts.BuildingLabels.Add(label);
					facts.BuildingCounts.Add(1);
				}
				else
				{
					facts.BuildingCounts[idx]++;
				}
				int d2;
				Pawn near = ReadsNearestOf(facts.Colonists, cell, out d2);
				if (!facts.HasBuildings || (near != null && d2 < bestDist))
				{
					facts.BuildingNear = cell;
					if (near != null)
					{
						bestDist = d2;
					}
				}
				facts.HasBuildings = true;
			}
		}

		// ---- classification ------------------------------------------------------------------------------------------------

		private static int ReadsGroupOf(Pawn p)
		{
			if (p.RaceProps.Humanlike)
			{
				return 0;
			}
			if (p.RaceProps.IsMechanoid)
			{
				return 1;
			}
			if (p.RaceProps.Insect)
			{
				return 2;
			}
			if (p.RaceProps.Animal)
			{
				MentalStateDef state = p.MentalStateDef;
				bool manhunting = state == MentalStateDefOf.Manhunter || state == MentalStateDefOf.ManhunterPermanent || state == MentalStateDefOf.ManhunterBloodRain;
				return (manhunting || p.InAggroMentalState) ? 3 : 4;
			}
			return 5;
		}

		private static bool ReadsIsFleeing(Pawn p)
		{
			if (p.MentalStateDef == MentalStateDefOf.PanicFlee)
			{
				return true;
			}
			Job job = p.CurJob;
			if (job != null && (job.def == JobDefOf.Flee || job.def == JobDefOf.FleeAndCower || job.def == JobDefOf.FleeAndCowerShort))
			{
				return true;
			}
			Lord lord = p.GetLord();
			LordToil toil = lord != null ? lord.CurLordToil : null;
			return toil is LordToil_ExitMap || toil is LordToil_ExitMapAndDefendSelf || toil is LordToil_ExitMapRandom
				|| toil is LordToil_ExitMapNear || toil is LordToil_PanicFlee;
		}

		// ---- text ------------------------------------------------------------------------------------------------------------

		private static string ReadsHeader(ReadsFacts facts, int group, List<ReadsHostile> members)
		{
			int downed = members.Count((ReadsHostile h) => h.Downed);
			int fleeing = members.Count((ReadsHostile h) => h.Fleeing);
			string line = members.Count + " " + (members.Count == 1 ? ReadsGroupOne[group] : ReadsGroupMany[group])
				+ " (" + downed + " downed, " + fleeing + " fleeing)";
			if (facts.Home != null)
			{
				int best = members.Min((ReadsHostile h) => h.HomeDist);
				line += best <= 0 ? ", nearest is inside the home area" : ", nearest is " + ReadsTiles(best) + " from the home area";
			}
			return line;
		}

		private static string ReadsPawnLine(Map map, ReadsHostile h)
		{
			string weapon = "no weapon";
			Pawn p = h.Pawn;
			if (p.equipment != null && p.equipment.Primary != null)
			{
				string label = PlainText(p.equipment.Primary.LabelNoParenthesis);
				if (!string.IsNullOrEmpty(label))
				{
					weapon = label;
				}
			}
			string cell = ReadsCell(map, p.PositionHeld);
			string status = "";
			if (h.Downed)
			{
				status += ", downed";
			}
			if (h.Fleeing)
			{
				status += ", fleeing";
			}
			if (h.HuntTarget != null)
			{
				status += ", hunting " + ReadsName(h.HuntTarget);
			}
			string where = "";
			if (h.Near != null)
			{
				where = ", " + ReadsFromColonist(h.Near, p.PositionHeld, h.Dist);
			}
			string line = "- " + h.Name + ": " + weapon + status + where + ", at " + cell;
			if (line.Length > ReadsMaxWidth && h.Near != null && cell.IndexOf(ReadsName(h.Near), StringComparison.Ordinal) >= 0)
			{
				// the cell text already says how far it is from this colonist
				line = "- " + h.Name + ": " + weapon + status + ", at " + cell;
			}
			if (line.Length > ReadsMaxWidth && weapon.Length > 12)
			{
				int over = line.Length - ReadsMaxWidth;
				int keep = Math.Max(8, weapon.Length - over - 3);
				weapon = weapon.Substring(0, keep) + "...";
				line = "- " + h.Name + ": " + weapon + status + where + ", at " + cell;
				if (line.Length > ReadsMaxWidth)
				{
					line = "- " + h.Name + ": " + weapon + status + ", at " + cell;
				}
			}
			return line;
		}

		// ---- groups standing together ------------------------------------------------------------------------------------
		// Members of one kind are split into groups: pawns under the same Lord (the game's own raid group,
		// Verse.AI.Group\Lord.cs) are one group; pawns with no Lord (manhunting animals) are joined when one stands
		// within ReadsClusterGap tiles of another. Groups come nearest first.

		private const int ReadsClusterMin = 3;
		private const int ReadsClusterGap = 8;
		private const int ReadsClusterNames = 6;
		private const int ReadsClusterWeapons = 4;

		private static List<List<ReadsHostile>> ReadsClusters(List<ReadsHostile> members)
		{
			List<List<ReadsHostile>> clusters = new List<List<ReadsHostile>>();
			Dictionary<Lord, List<ReadsHostile>> byLord = new Dictionary<Lord, List<ReadsHostile>>();
			List<ReadsHostile> loose = new List<ReadsHostile>();
			foreach (ReadsHostile h in members)
			{
				Lord lord = h.Pawn.GetLord();
				if (lord == null)
				{
					loose.Add(h);
					continue;
				}
				List<ReadsHostile> list;
				if (!byLord.TryGetValue(lord, out list))
				{
					list = new List<ReadsHostile>();
					byLord[lord] = list;
					clusters.Add(list);
				}
				list.Add(h);
			}
			// Loose pawns: joined while any member of one bunch is within the gap of any member of another.
			List<List<ReadsHostile>> bunches = new List<List<ReadsHostile>>();
			foreach (ReadsHostile h in loose)
			{
				List<ReadsHostile> home = null;
				for (int i = bunches.Count - 1; i >= 0; i--)
				{
					bool near = false;
					foreach (ReadsHostile other in bunches[i])
					{
						if (AgentPlace.Tiles(other.Pawn.PositionHeld, h.Pawn.PositionHeld) <= ReadsClusterGap)
						{
							near = true;
							break;
						}
					}
					if (!near)
					{
						continue;
					}
					if (home == null)
					{
						home = bunches[i];
					}
					else
					{
						home.AddRange(bunches[i]);
						bunches.RemoveAt(i);
					}
				}
				if (home == null)
				{
					home = new List<ReadsHostile>();
					bunches.Add(home);
				}
				home.Add(h);
			}
			clusters.AddRange(bunches);
			foreach (List<ReadsHostile> cluster in clusters)
			{
				cluster.Sort((ReadsHostile a, ReadsHostile b) => a.Dist.CompareTo(b.Dist));
			}
			return clusters.OrderBy((List<ReadsHostile> c) => c[0].Dist).ToList();
		}

		// Three lines for one group: its nearest member (the distance that matters is to the front, not the middle),
		// the box round it (an AREA the next order can use), and who and what is in it.
		private static List<string> ReadsClusterLines(Map map, List<ReadsHostile> cluster)
		{
			List<string> lines = new List<string>();
			ReadsHostile first = cluster[0];
			IntVec3 at = first.Pawn.PositionHeld;
			string head = "- group of " + cluster.Count + ": nearest is " + first.Name;
			string where = first.Near != null ? ", " + ReadsFromColonist(first.Near, at, first.Dist) : "";
			string line = head + where + ", at " + ReadsCell(map, at);
			if (line.Length > ReadsMaxWidth)
			{
				line = head + ", at " + ReadsCell(map, at);
			}
			lines.Add(line);

			int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
			int downed = 0;
			int fleeing = 0;
			List<string> weaponOrder = new List<string>();
			Dictionary<string, int> weapons = new Dictionary<string, int>();
			foreach (ReadsHostile h in cluster)
			{
				IntVec3 c = h.Pawn.PositionHeld;
				minX = Math.Min(minX, c.x);
				maxX = Math.Max(maxX, c.x);
				minZ = Math.Min(minZ, c.z);
				maxZ = Math.Max(maxZ, c.z);
				if (h.Downed)
				{
					downed++;
				}
				if (h.Fleeing)
				{
					fleeing++;
				}
				string weapon = "no weapon";
				if (h.Pawn.equipment != null && h.Pawn.equipment.Primary != null)
				{
					string label = PlainText(h.Pawn.equipment.Primary.LabelNoParenthesis);
					if (!string.IsNullOrEmpty(label))
					{
						weapon = label;
					}
				}
				if (!weapons.ContainsKey(weapon))
				{
					weapons[weapon] = 0;
					weaponOrder.Add(weapon);
				}
				weapons[weapon]++;
			}
			string box = "  all within " + minX + ", " + minZ + " to " + maxX + ", " + maxZ + " (" + (maxX - minX + 1) + " by " + (maxZ - minZ + 1) + ")";
			if (downed > 0)
			{
				box += "; " + downed + " downed";
			}
			if (fleeing > 0)
			{
				box += "; " + fleeing + " fleeing";
			}
			// Animals hold nothing: the weapon list is only said when someone holds one.
			if (!(weapons.Count == 1 && weapons.ContainsKey("no weapon")))
			{
				List<string> parts = weaponOrder.OrderByDescending((string w) => weapons[w]).Take(ReadsClusterWeapons)
					.Select((string w) => weapons[w] + " " + w).ToList();
				string held = "; " + string.Join(", ", parts.ToArray()) + (weaponOrder.Count > ReadsClusterWeapons ? " and others" : "");
				if ((box + held).Length <= ReadsMaxWidth)
				{
					box += held;
				}
				else
				{
					lines.Add(box);
					box = " " + held.Substring(1);
				}
			}
			lines.Add(box);
			lines.Add("  " + CapList(cluster.Select((ReadsHostile h) => h.Name), ReadsClusterNames));
			return lines;
		}

		private static string ReadsBuildingList(ReadsFacts facts)
		{
			List<string> parts = new List<string>();
			for (int i = 0; i < facts.BuildingLabels.Count; i++)
			{
				parts.Add(facts.BuildingCounts[i] + " " + facts.BuildingLabels[i]);
			}
			return CapList(parts, 6);
		}

		private static string ReadsFromColonist(Pawn colonist, IntVec3 cell, int dist)
		{
			string dir = ReadsCompass(colonist.Position, cell);
			if (dir == "here")
			{
				return "at " + ReadsName(colonist);
			}
			return ReadsTiles(dist) + " " + dir + " of " + ReadsName(colonist);
		}

		private static string ReadsTiles(int n)
		{
			return n + (n == 1 ? " tile" : " tiles");
		}

		private static string ReadsName(Pawn p)
		{
			string n = p.LabelShort;
			return string.IsNullOrEmpty(n) ? "someone" : PlainText(n);
		}

		private static string ReadsKind(Pawn p)
		{
			string k = PlainText(p.KindLabel);
			return string.IsNullOrEmpty(k) ? "animal" : k.ToLowerInvariant();
		}

		private static string ReadsCell(Map map, IntVec3 cell)
		{
			return AgentPlace.Describe(map, cell);
		}

		private static string ReadsJoin(List<string> lines)
		{
			if (lines.Count > ReadsMaxLines)
			{
				int left = lines.Count - (ReadsMaxLines - 1);
				lines.RemoveRange(ReadsMaxLines - 1, left);
				lines.Add("and " + left + " more lines left out.");
			}
			return string.Join("\n", lines.ToArray());
		}

		// ---- distances ---------------------------------------------------------------------------------------------------

		private static int ReadsDistSq(IntVec3 a, IntVec3 b)
		{
			int dx = a.x - b.x;
			int dz = a.z - b.z;
			return dx * dx + dz * dz;
		}

		// Tiles as the orders count them (the larger axis, AgentPlace.Tiles), not the straight-line length.
		private static int ReadsWhole(IntVec3 a, IntVec3 b)
		{
			return AgentPlace.Tiles(a, b);
		}

		private static Pawn ReadsNearestOf(List<Pawn> colonists, IntVec3 cell, out int distSq)
		{
			Pawn best = null;
			distSq = int.MaxValue;
			for (int i = 0; i < colonists.Count; i++)
			{
				int d = ReadsDistSq(colonists[i].Position, cell);
				if (d < distSq)
				{
					distSq = d;
					best = colonists[i];
				}
			}
			return best;
		}

		private static int ReadsNearestCellDist(List<IntVec3> cells, IntVec3 cell)
		{
			int best = int.MaxValue;
			for (int i = 0; i < cells.Count; i++)
			{
				int d = AgentPlace.Tiles(cells[i], cell);
				if (d < best)
				{
					best = d;
				}
			}
			return best == int.MaxValue ? 0 : best;
		}
	}
}
