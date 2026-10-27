using System;
using System.Collections.Generic;
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
	// A pawn that builds only roofs (user, 2026-10-16: "new feature because agents are fucking dumb."). Router form
	// "roof only": build roof at AREA using PAWN.
	//
	// The order (user's words, 2026-10-16):
	//   - "makes that pawn ONLY build roof (a construction task). BLOCKS THAT PAWN FROM DOING ANYTHING that is NOT building
	//     roofing, construction wise." Every Construction work giver but WorkGiver_BuildRoof is taken out of the pawn's work
	//     lists (Pawn_WorkSettings.WorkGiversInOrderNormal / Emergency, RimWorld\Pawn_WorkSettings.cs:30-52), and its roof
	//     jobs are kept inside AREA (WorkGiver_BuildRoof.HasJobOnCell, RimWorld\WorkGiver_BuildRoof.cs). Other work types
	//     stay as they are.
	//   - Construction off in the work tab: "mod should auto assign them said work, and un-do assignment once finisshed."
	//     A pawn that can never do it (Pawn.WorkTypeIsDisabled, Verse\Pawn.cs) is refused.
	//   - AREA's cells without a roof get the game's build roof mark (Designator_AreaBuildRoof.cs:38-42, as AgentAreas.Paint).
	//
	// Not the user's (my choices, open to change): the order ends when the game would give the pawn no roof cell in AREA any
	// more (WorkGiver_BuildRoof.HasJobOnCell false for every cell), checked every 3 real seconds while the game runs; a new
	// order for the same pawn replaces the old one; Construction turned on is set to 3, the work tab's tick.
	//
	// Save: RoofOnlyData in GameComponent_VerbalCommands ("vcRoofOnly"). Main thread only.
	public static class AgentRoofOnly
	{
		private const float CheckSeconds = 3f;
		private const int OnPriority = 3;

		// ---- text (every line an agent reads; wording agreed with the user, 2026-10-16) ----------------------------

		private static string PlanLine(Pawn p, string place, int toRoof, int roofed)
		{
			return p.LabelShort + " builds only roofs in " + place + ": " + toRoof + " cells to roof, " + roofed + " already roofed. "
				+ p.LabelShort + " does no other construction work until it is done.";
		}

		private static string TurnedOnNote(Pawn p)
		{
			return "Construction is turned on for " + p.LabelShort + " until it is done.";
		}

		private static string UnsupportedNote(int n)
		{
			return n + " of those cells have no wall or column within 6 tiles yet; they get roofed once one stands.";
		}

		private static string Incapable(Pawn p)
		{
			return p.LabelShort + " is incapable of construction work.";
		}

		private static string NothingToRoof(string place)
		{
			return "Nothing to roof: every cell of " + place + " already has a roof.";
		}

		private static string Done(string place, string pawn, int roofed, int left)
		{
			return "roofing done at " + place + " by " + pawn + ": " + roofed + " cells roofed"
				+ (left > 0 ? ", " + left + " can't be roofed yet (no support)" : "") + ".";
		}

		// ---- state ------------------------------------------------------------------------------------------------

		private static float checkTimer;
		private static readonly HashSet<string> loggedFaults = new HashSet<string>();

		private static RoofOnlyData Data
		{
			get
			{
				GameComponent_VerbalCommands c = GameComponent_VerbalCommands.Instance;
				return c == null ? null : c.RoofOnly;
			}
		}

		internal static RoofOnlyJob JobOf(Pawn p)
		{
			RoofOnlyData d = Data;
			if (d == null || p == null)
			{
				return null;
			}
			for (int i = 0; i < d.jobs.Count; i++)
			{
				if (d.jobs[i].pawn == p)
				{
					return d.jobs[i];
				}
			}
			return null;
		}

		// What finished orders said, given once with the next result of any order (AgentInbox.WriteOutbox).
		public static List<string> TakeMessages()
		{
			RoofOnlyData d = Data;
			if (d == null || d.messages.Count == 0)
			{
				return new List<string>();
			}
			List<string> lines = d.messages;
			d.messages = new List<string>();
			return lines;
		}

		// ---- plan -------------------------------------------------------------------------------------------------

		// Form "roof only" (AgentPawnActions.DispatchWork).
		public static PlanReply Plan(FormContext c)
		{
			Map map = c.map;
			Pawn pawn = FormPlanner.ResolveOnePawn(c, "PAWN");
			if (pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction))
			{
				throw new Fault(Incapable(pawn));
			}
			string place = c.Text("AREA");
			CellRect rect;
			string reason;
			if (!AgentPlace.TryRect(map, place, out rect, out reason))
			{
				throw new Fault(string.IsNullOrWhiteSpace(reason) ? "'" + place + "' is not an area I can find." : reason);
			}
			List<IntVec3> cells = rect.ClipInsideMap(map).Cells.Where((IntVec3 cell) => AgentPlace.Visible(map, cell)).ToList();
			List<IntVec3> unroofed = cells.Where((IntVec3 cell) => !cell.Roofed(map)).ToList();
			if (unroofed.Count == 0)
			{
				throw new Fault(NothingToRoof(place));
			}
			int roofed = cells.Count - unroofed.Count;
			bool turnOn = pawn.workSettings != null && pawn.workSettings.GetPriority(WorkTypeDefOf.Construction) == 0;

			Plan plan = FormPlanner.NewPlan(c.form);
			int unsupported = unroofed.Count((IntVec3 cell) => !RoofCollapseUtility.WithinRangeOfRoofHolder(cell, map));
			// Settings gate (user, 2026-10-26): Build advice tips/notes.
			if (VerbalCommandsMod.BuildAdviceOn && unsupported > 0)
			{
				plan.notes.Add(UnsupportedNote(unsupported));
			}
			// Settings gate (user, 2026-10-26): Build advice tips/notes. turnOn still drives the write.
			if (VerbalCommandsMod.BuildAdviceOn && turnOn)
			{
				plan.notes.Add(TurnedOnNote(pawn));
			}

			Write write = new Write();
			write.description = "build roof at " + place + " using " + pawn.LabelShort;
			write.apply = delegate
			{
				Start(map, pawn, place, unroofed, turnOn);
			};
			plan.writes.Add(write.Reads(() => AgentPawnActions.DoingLine(pawn)));
			return FormPlanner.Reply(plan, PlanLine(pawn, place, unroofed.Count, roofed), new[] { pawn }, null);
		}

		private static void Start(Map map, Pawn pawn, string place, List<IntVec3> unroofed, bool turnOn)
		{
			RoofOnlyData d = Data;
			if (d == null)
			{
				return;
			}
			AreaManager am = map.areaManager;
			foreach (IntVec3 cell in unroofed)
			{
				// Designator_AreaBuildRoof.cs:38-42 (AgentAreas.Paint).
				if (!cell.Roofed(map))
				{
					am.BuildRoof[cell] = true;
					am.NoRoof[cell] = false;
				}
			}
			RoofOnlyJob old = JobOf(pawn);
			bool wasTurnedOn = false;
			if (old != null)
			{
				wasTurnedOn = old.turnedOn;
				d.jobs.Remove(old);
			}
			if (turnOn && pawn.workSettings != null)
			{
				pawn.workSettings.SetPriority(WorkTypeDefOf.Construction, OnPriority);
			}
			RoofOnlyJob job = new RoofOnlyJob();
			job.pawn = pawn;
			job.mapId = map.uniqueID;
			job.place = place;
			job.cells = unroofed.ToList();
			job.turnedOn = turnOn || wasTurnedOn;
			d.jobs.Add(job);
		}

		// ---- watch ------------------------------------------------------------------------------------------------

		// GameComponentUpdate, every frame. The 3 s check counts real seconds while the game runs.
		public static void Update()
		{
			try
			{
				if (Current.ProgramState != ProgramState.Playing || Current.Game == null || Find.TickManager == null)
				{
					return;
				}
				RoofOnlyData d = Data;
				if (d == null || d.jobs.Count == 0 || Find.TickManager.Paused)
				{
					return;
				}
				checkTimer += Mathf.Min(Time.unscaledDeltaTime, 1f);
				if (checkTimer < CheckSeconds)
				{
					return;
				}
				checkTimer = 0f;
				foreach (RoofOnlyJob job in d.jobs.ToList())
				{
					Check(d, job);
				}
			}
			catch (Exception ex)
			{
				if (loggedFaults.Add("update"))
				{
					Log.Error("[VerbalCommands] roof only orders: " + ex);
				}
			}
		}

		private static void Check(RoofOnlyData d, RoofOnlyJob job)
		{
			Map map = Find.Maps.FirstOrDefault((Map m) => m.uniqueID == job.mapId);
			Pawn p = job.pawn;
			if (map == null || p == null || p.Dead || p.Destroyed)
			{
				End(d, job, null);
				return;
			}
			if (!p.Spawned || p.Map != map || p.Downed || p.Drafted || p.InMentalState)
			{
				return;
			}
			if (p.CurJobDef == JobDefOf.BuildRoof)
			{
				return;
			}
			WorkGiver_BuildRoof roof = RoofWorker();
			if (roof != null && job.cells.Any((IntVec3 cell) => roof.HasJobOnCell(p, cell)))
			{
				return;
			}
			End(d, job, map);
		}

		private static void End(RoofOnlyData d, RoofOnlyJob job, Map map)
		{
			d.jobs.Remove(job);
			Pawn p = job.pawn;
			if (job.turnedOn && p != null && !p.Dead && p.workSettings != null)
			{
				p.workSettings.SetPriority(WorkTypeDefOf.Construction, 0);
			}
			if (map != null && p != null)
			{
				int left = job.cells.Count((IntVec3 cell) => !cell.Roofed(map));
				d.messages.Add(Done(job.place, p.LabelShort, job.cells.Count - left, left));
			}
		}

		private static WorkGiver_BuildRoof RoofWorker()
		{
			WorkGiverDef def = DefDatabase<WorkGiverDef>.GetNamedSilentFail("BuildRoofs");
			return def != null ? def.Worker as WorkGiver_BuildRoof : null;
		}
	}

	// One pawn that builds only roofs.
	public class RoofOnlyJob : IExposable
	{
		public Pawn pawn;
		public int mapId;
		public string place;                                // "100, 120 to 110, 128"
		public List<IntVec3> cells = new List<IntVec3>();   // AREA's cells that had no roof when the order was given
		public bool turnedOn;                               // Construction was off and the order turned it on

		private HashSet<IntVec3> cellSet;

		public HashSet<IntVec3> CellSet
		{
			get
			{
				if (cellSet == null || cellSet.Count != cells.Count)
				{
					cellSet = new HashSet<IntVec3>(cells);
				}
				return cellSet;
			}
		}

		public void ExposeData()
		{
			Scribe_References.Look(ref pawn, "pawn");
			Scribe_Values.Look(ref mapId, "mapId");
			Scribe_Values.Look(ref place, "place");
			Scribe_Collections.Look(ref cells, "cells", LookMode.Value);
			Scribe_Values.Look(ref turnedOn, "turnedOn");
			if (Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				cells = cells ?? new List<IntVec3>();
				cellSet = null;
			}
		}
	}

	// The roof only orders of a game, saved with it (GameComponent_VerbalCommands, "vcRoofOnly").
	public class RoofOnlyData : IExposable
	{
		public List<RoofOnlyJob> jobs = new List<RoofOnlyJob>();
		public List<string> messages = new List<string>();

		public void ExposeData()
		{
			Scribe_Collections.Look(ref jobs, "jobs", LookMode.Deep);
			Scribe_Collections.Look(ref messages, "messages", LookMode.Value);
			if (Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				jobs = (jobs ?? new List<RoofOnlyJob>()).Where((RoofOnlyJob j) => j != null && j.pawn != null).ToList();
				messages = messages ?? new List<string>();
			}
		}
	}

	// The two game methods the order changes, for its pawns only. Patched by hand, like MineYieldPatch.
	internal static class RoofOnlyPatch
	{
		private static readonly AccessTools.FieldRef<Pawn_WorkSettings, Pawn> PawnOf = AccessTools.FieldRefAccess<Pawn_WorkSettings, Pawn>("pawn");

		public static void Apply()
		{
			try
			{
				Harmony harmony = new Harmony("kopaz.verbalcommands.roofonly");
				HarmonyMethod lists = new HarmonyMethod(typeof(RoofOnlyPatch).GetMethod("ListsPostfix", BindingFlags.Public | BindingFlags.Static));
				foreach (string name in new[] { "WorkGiversInOrderNormal", "WorkGiversInOrderEmergency" })
				{
					MethodInfo getter = AccessTools.PropertyGetter(typeof(Pawn_WorkSettings), name);
					if (getter == null)
					{
						Log.Warning("[VerbalCommands] Pawn_WorkSettings." + name + " not found; roof only orders can't block other construction.");
						continue;
					}
					harmony.Patch(getter, null, lists);
				}
				MethodInfo hasJob = AccessTools.Method(typeof(WorkGiver_BuildRoof), "HasJobOnCell");
				if (hasJob == null)
				{
					Log.Warning("[VerbalCommands] WorkGiver_BuildRoof.HasJobOnCell not found; roof only orders can't keep roofing inside AREA.");
				}
				else
				{
					harmony.Patch(hasJob, null, new HarmonyMethod(typeof(RoofOnlyPatch).GetMethod("CellPostfix", BindingFlags.Public | BindingFlags.Static)));
				}
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] Could not patch for roof only orders (is Harmony loaded?): " + ex.Message);
			}
		}

		// Construction work givers other than roofing are taken out for a pawn with a roof only order.
		public static void ListsPostfix(Pawn_WorkSettings __instance, ref List<WorkGiver> __result)
		{
			try
			{
				if (__result == null || AgentRoofOnly.JobOf(PawnOf(__instance)) == null)
				{
					return;
				}
				__result = __result.Where((WorkGiver w) => w.def.workType != WorkTypeDefOf.Construction || w is WorkGiver_BuildRoof).ToList();
			}
			catch (Exception)
			{
			}
		}

		// A roof only pawn builds roofs inside its AREA only.
		public static void CellPostfix(Pawn pawn, IntVec3 c, ref bool __result)
		{
			if (!__result)
			{
				return;
			}
			try
			{
				RoofOnlyJob job = AgentRoofOnly.JobOf(pawn);
				if (job != null && !job.CellSet.Contains(c))
				{
					__result = false;
				}
			}
			catch (Exception)
			{
			}
		}
	}
}
