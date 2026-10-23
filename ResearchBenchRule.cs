using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace VerbalCommands
{
	// Research bench rule (user, 2026-10-16: "BLOCK any research benches from being built unless there is a pawn assigned on
	// research that can do research. If there is one, and there is one pawn, blocks new from being built. instead, *CANCELS*
	// the blueprint, FORCE-moves the existing blueprint (i.e. move order) to that spot." / "2 pawns assigned to research and
	// having 1 bench is NOT blockabke, if agent wants to build new one." / "If there is a pawn already WORKING on research
	// (hopefully this can be scanned easily when it happens), MAKE THAT PAWN DELIVER THE BENCH with manual priority.").
	// The user's picks: every state of the existing bench moves; a better bench is allowed as an upgrade; it covers everyone
	// (the agents' build orders and the player's own build designator) while the agent inbox is on; the wording below.
	//
	// When a research bench (thingClass Building_ResearchBench) is about to be placed on a map:
	//   - no free colonist there has Research on and can do it (WorkIsActive, not WorkTypeIsDisabled): not placed;
	//   - two or more such colonists: placed as asked;
	//   - one: the colony's research bench there that is as good or better (ResearchSpeedFactor at least the new one's;
	//     simple 0.75, hi-tech 1.0, Buildings_Production.xml) moves to the spot instead. A blueprint first, then a frame,
	//     then a built one, the nearest first. A blueprint or frame is cancelled (DestroyMode.Cancel: a frame drops what
	//     was delivered) and the new blueprint is placed. A built bench gets the game's reinstall blueprint at the spot
	//     (GenConstruct.PlaceBlueprintForReinstall, as Designator_Install.cs:125 does; a bench that can not be minified, the
	//     hi-tech one, can not move: nothing is placed), and a colonist researching at it right now (JobDefOf.Research on
	//     that bench) is ordered to carry it there (the game's install job, WorkGiver_ConstructDeliverResources.InstallJob
	//     :321-326, given as an ordered job like the game's "Prioritize" option).
	//   - none as good or better: placed as asked (the hi-tech bench over a simple one).
	// The line for the agent goes out with the next outbox result (TakeMessages); the player sees it as a game message.
	internal static class ResearchBenchRule
	{
		internal enum Outcome
		{
			Place,
			Blocked,
			Moved
		}

		private static List<string> messages = new List<string>();

		public static List<string> TakeMessages()
		{
			if (messages.Count == 0)
			{
				return new List<string>();
			}
			List<string> lines = messages;
			messages = new List<string>();
			return lines;
		}

		// For the agents' build paths (AgentBuild.PlaceNow, BuildTools.PlaceOne): the line goes out with the next result.
		internal static void Tell(string note)
		{
			if (!string.IsNullOrEmpty(note))
			{
				messages.Add(note);
			}
		}

		internal static bool IsBench(BuildableDef def)
		{
			ThingDef td = def as ThingDef;
			return td != null && td.thingClass != null && typeof(Building_ResearchBench).IsAssignableFrom(td.thingClass);
		}

		// Main thread, before a research bench blueprint is placed. Place: go on (an old blueprint or frame may have been
		// cancelled for it). Blocked / Moved: place nothing. NOTE is the line for the agent (null when nothing to say).
		internal static Outcome Check(Map map, BuildableDef def, IntVec3 cell, Rot4 rot, out string note)
		{
			note = null;
			if (!AgentInbox.Enabled || map == null || !IsBench(def))
			{
				return Outcome.Place;
			}
			ThingDef bench = (ThingDef)def;
			List<Pawn> researchers = map.mapPawns.FreeColonistsSpawned.Where((Pawn p) => p.workSettings != null && p.workSettings.EverWork
				&& !p.WorkTypeIsDisabled(WorkTypeDefOf.Research) && p.workSettings.WorkIsActive(WorkTypeDefOf.Research)).ToList();
			if (researchers.Count == 0)
			{
				// User's wording, with "stop wasting resources, idiot." added at the end by the user.
				note = "No colonist who can research has Research on: no research bench is built. stop wasting resources, idiot.";
				HarnessLog.Add("Research bench", "new bench at " + AgentWorkspotCheck.Xz(cell) + " cancelled: no researcher");   // HarnessLog.cs, H2
				return Outcome.Blocked;
			}
			if (researchers.Count > 1)
			{
				return Outcome.Place;
			}
			Thing old = Existing(map, bench, cell);
			if (old == null)
			{
				return Outcome.Place;
			}
			string head = researchers[0].LabelShort + " is the only researcher and has the research bench at " + AgentWorkspotCheck.Xz(old.Position);
			if (old is Blueprint || old is Frame)
			{
				string oldAt = AgentWorkspotCheck.Xz(old.Position);
				old.Destroy(DestroyMode.Cancel);
				note = head + ": it moves to " + AgentWorkspotCheck.Xz(cell) + " instead of a second bench.";
				HarnessLog.Add("Research bench", "bench blueprint at " + oldAt + " cancelled; new bench at " + AgentWorkspotCheck.Xz(cell));   // H2
				return Outcome.Place;
			}
			Building built = old as Building;
			if (built == null || built.def.minifiedDef == null)
			{
				note = head + ".";
				HarnessLog.Add("Research bench", "new bench at " + AgentWorkspotCheck.Xz(cell) + " cancelled; bench stays at " + AgentWorkspotCheck.Xz(old.Position));   // H2
				return Outcome.Blocked;
			}
			AcceptanceReport report = GenConstruct.CanPlaceBlueprintAt(built.def, cell, rot, map, false, built);
			if (!report.Accepted)
			{
				note = head + "." + (report.Reason.NullOrEmpty() ? "" : " " + report.Reason);
				HarnessLog.Add("Research bench", "new bench at " + AgentWorkspotCheck.Xz(cell) + " cancelled; bench stays at " + AgentWorkspotCheck.Xz(old.Position));   // H2
				return Outcome.Blocked;
			}
			InstallBlueprintUtility.CancelBlueprintsFor(built);
			Blueprint_Install install = GenConstruct.PlaceBlueprintForReinstall(built, cell, map, rot, Faction.OfPlayer);
			note = head + ": it moves to " + AgentWorkspotCheck.Xz(cell) + " instead of a second bench.";
			HarnessLog.Add("Research bench", "new bench at " + AgentWorkspotCheck.Xz(cell) + " cancelled; bench at " + AgentWorkspotCheck.Xz(built.Position) + " moved there");   // H2
			foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
			{
				if (p.CurJobDef == JobDefOf.Research && p.CurJob != null && p.CurJob.targetA.Thing == built && !p.Downed)
				{
					Job job = JobMaker.MakeJob(JobDefOf.HaulToContainer);
					job.targetA = built;
					job.targetB = install;
					job.count = 1;
					job.haulMode = HaulMode.ToContainer;
					p.jobs.TryTakeOrderedJob(job, JobTag.Misc);
					break;
				}
			}
			return Outcome.Moved;
		}

		// The colony's research bench on MAP as good as BENCH or better: a blueprint first, then a frame, then a built one;
		// the nearest to CELL first. Null if none.
		private static Thing Existing(Map map, ThingDef bench, IntVec3 cell)
		{
			float need = bench.GetStatValueAbstract(StatDefOf.ResearchSpeedFactor);
			List<Thing> found = new List<Thing>();
			foreach (ThingRequestGroup g in new ThingRequestGroup[] { ThingRequestGroup.Blueprint, ThingRequestGroup.BuildingFrame })
			{
				foreach (Thing t in map.listerThings.ThingsInGroup(g))
				{
					if (!t.Spawned || t.Faction != Faction.OfPlayer || !(t is Blueprint_Build || t is Frame))
					{
						continue;
					}
					ThingDef d = t.def.entityDefToBuild as ThingDef;
					if (IsBench(d) && d.GetStatValueAbstract(StatDefOf.ResearchSpeedFactor) >= need)
					{
						found.Add(t);
					}
				}
			}
			foreach (Building b in map.listerBuildings.allBuildingsColonist)
			{
				if (b.Spawned && IsBench(b.def) && b.def.GetStatValueAbstract(StatDefOf.ResearchSpeedFactor) >= need)
				{
					found.Add(b);
				}
			}
			return found.OrderBy((Thing t) => t is Blueprint ? 0 : t is Frame ? 1 : 2)
				.ThenBy((Thing t) => t.Position.DistanceToSquared(cell)).FirstOrDefault();
		}

		// ---- the player's build designator -----------------------------------------------------------------------

		// Designator_Build.DesignateSingleCell (RimWorld\Designator_Build.cs; entDef :10, placingRot Designator_Place.cs:10):
		// the player's own placement goes through the same rule; the line shows as a game message.
		public static void Apply()
		{
			MethodInfo original = typeof(Designator_Build).GetMethod("DesignateSingleCell", BindingFlags.Public | BindingFlags.Instance);
			if (original == null)
			{
				Log.Warning("[VerbalCommands] Designator_Build.DesignateSingleCell not found; the research bench rule covers the agents only.");
				return;
			}
			Harmony harmony = new Harmony("kopaz.verbalcommands.researchbench");
			harmony.Patch(original, new HarmonyMethod(typeof(ResearchBenchRule).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)));
		}

		public static bool Prefix(Designator_Build __instance, IntVec3 c, BuildableDef ___entDef, Rot4 ___placingRot)
		{
			try
			{
				if (!IsBench(___entDef))
				{
					return true;
				}
				string note;
				Outcome o = Check(__instance.Map, ___entDef, c, ___placingRot, out note);
				if (note != null)
				{
					Messages.Message(note, o == Outcome.Blocked ? MessageTypeDefOf.RejectInput : MessageTypeDefOf.NeutralEvent, false);
				}
				return o == Outcome.Place;
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] research bench rule failed: " + ex.Message);
				return true;
			}
		}
	}
}
