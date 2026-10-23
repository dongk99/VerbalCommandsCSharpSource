using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Agent 07: cancel designations of one kind, and the two questions "show designations." / "show wildlife."
	// (agent_player\out\07\forms.md). Fair play: nothing here reads or sets dev mode or god mode; nothing under fog is
	// counted, printed or cancelled (AgentPlace.Visible); the questions change nothing and use no Designator (a
	// Designator acts on the map on screen).
	//
	// Game calls (sources\RimWorldDecompiled):
	//   DesignationManager.SpawnedDesignationsOfDef (Verse\DesignationManager.cs:333), DesignationOn(Thing, def) (:253),
	//   DesignationAt(cell, def) (:275), RemoveDesignation (:380); DesignationDef.designateCancelable (Verse\DesignationDef.cs:14).
	//   The game's cancel tool (RimWorld\Designator_Cancel.cs:48-69 DesignateSingleCell, :98-126 DesignateThing) removes every
	//   cancelable designation on the cell or thing it is dragged over, and also cancels the player's blueprints and frames,
	//   and for a mine-vein mark the whole touching vein (Designator_MineVein.RemoveContiguousDesignations). Here only the
	//   KIND asked is removed, only in view, blueprints and frames are never touched (agent 08's "cancel blueprint ..."), and
	//   a vein mark is removed cell by cell for the cells in view and in the area.
	//   Pawn_WorkSettings.Initialized (RimWorld\Pawn_WorkSettings.cs:56) is tested before WorkIsActive (:171), whose
	//   ConfirmInitializedDebug (:131) would otherwise initialise the settings.
	//   Hunting: the game warns when RaceProps.manhunterOnDamageChance >= 0.015 (RimWorld\Designator_Hunt.cs:102-104) and
	//   prints PawnUtility.GetManhunterOnDamageChance(pawn) (RimWorld\PawnUtility.cs:1232); the same test is used here.
	//   Taming: TameUtility.CanTame (RimWorld\TameUtility.cs:47-54, which already refuses Scaria, as Designator_Tame.cs:51-66);
	//   the game warns when PawnUtility.GetManhunterOnTameFailChance(pawn) (PawnUtility.cs:1291) >= 0.015 (TameUtility.cs:15-16).
	public static class AgentDesignationQueries
	{
		private const int ClusterTiles = 12;
		private const int MaxClusters = 5;
		private const int MaxWildKinds = 30;

		private sealed class KindRow
		{
			public string kind;      // the KIND word of "cancel KIND ..."
			public string work;      // what the colonists do, for "nobody does ..."
			public DesignationDef[] defs;
			public Func<Designation, bool> extra; // null = every designation of those defs
			public WorkTypeDef workType;
		}

		private static List<KindRow> rows;

		// Built on first use (DefOf fields are filled once the game has loaded its defs).
		private static List<KindRow> Rows()
		{
			if (rows != null)
			{
				return rows;
			}
			List<KindRow> r = new List<KindRow>();
			r.Add(Row("mining", WorkTypeDefOf.Mining, null, DesignationDefOf.Mine, DesignationDefOf.MineVein));
			r.Add(Row("chopping", WorkTypeDefOf.PlantCutting, (Designation d) => IsTree(d), DesignationDefOf.HarvestPlant));
			r.Add(Row("harvesting", WorkTypeDefOf.PlantCutting, (Designation d) => !IsTree(d), DesignationDefOf.HarvestPlant));
			r.Add(Row("cutting", WorkTypeDefOf.PlantCutting, null, DesignationDefOf.CutPlant));
			r.Add(Row("deconstructing", WorkTypeDefOf.Construction, null, DesignationDefOf.Deconstruct));
			r.Add(Row("uninstalling", WorkTypeDefOf.Construction, null, DesignationDefOf.Uninstall));
			r.Add(Row("hauling", WorkTypeDefOf.Hauling, null, DesignationDefOf.Haul));
			r.Add(Row("hunting", WorkTypeDefOf.Hunting, null, DesignationDefOf.Hunt));
			r.Add(Row("taming", WorkTypeDefOf.Handling, null, DesignationDefOf.Tame));
			r.Add(Row("slaughtering", WorkTypeDefOf.Handling, null, DesignationDefOf.Slaughter));
			r.Add(Row("stripping", WorkTypeDefOf.Hauling, null, DesignationDefOf.Strip));
			r.Add(Row("smoothing", WorkTypeDefOf.Construction, null, DesignationDefOf.SmoothWall, DesignationDefOf.SmoothFloor));
			rows = r;
			return rows;
		}

		private static KindRow Row(string kind, WorkTypeDef work, Func<Designation, bool> extra, params DesignationDef[] defs)
		{
			KindRow k = new KindRow();
			k.kind = kind;
			k.workType = work;
			k.work = work != null ? (work.labelShort ?? work.defName) : kind;
			k.defs = defs.Where((DesignationDef d) => d != null).ToArray();
			k.extra = extra;
			return k;
		}

		private static bool IsTree(Designation d)
		{
			Plant plant = d.target.HasThing ? d.target.Thing as Plant : null;
			return plant != null && plant.def.plant != null && plant.def.plant.IsTree;
		}

		// The visible designations of one row (optionally inside rect), sorted by x then z.
		private static List<Designation> Marks(Map map, KindRow row, CellRect? rect)
		{
			List<Designation> list = new List<Designation>();
			foreach (DesignationDef def in row.defs)
			{
				foreach (Designation des in map.designationManager.SpawnedDesignationsOfDef(def))
				{
					if (!des.target.IsValid)
					{
						continue;
					}
					IntVec3 cell = des.target.Cell;
					if (!AgentPlace.Visible(map, cell))
					{
						continue;
					}
					if (rect.HasValue && !rect.Value.Contains(cell))
					{
						continue;
					}
					if (row.extra != null && !row.extra(des))
					{
						continue;
					}
					list.Add(des);
				}
			}
			return list.OrderBy((Designation d) => d.target.Cell.x).ThenBy((Designation d) => d.target.Cell.z).ToList();
		}

		// "9 around 88, 140 (6 tiles north of Bob); 5 around ..." A mark joins the first cluster whose first cell is within
		// 12 tiles (the larger of |dx|, |dz|), else starts a new one. At most 5 clusters, then "and N more places".
		private static string Clusters(Map map, List<Designation> marks)
		{
			List<List<IntVec3>> clusters = new List<List<IntVec3>>();
			foreach (Designation des in marks)
			{
				IntVec3 cell = des.target.Cell;
				List<IntVec3> home = null;
				foreach (List<IntVec3> cl in clusters)
				{
					if (AgentPlace.Tiles(cl[0], cell) <= ClusterTiles)
					{
						home = cl;
						break;
					}
				}
				if (home == null)
				{
					home = new List<IntVec3>();
					clusters.Add(home);
				}
				home.Add(cell);
			}
			List<string> parts = new List<string>();
			foreach (List<IntVec3> cl in clusters.OrderByDescending((List<IntVec3> l) => l.Count).Take(MaxClusters))
			{
				long sx = 0;
				long sz = 0;
				foreach (IntVec3 c in cl)
				{
					sx += c.x;
					sz += c.z;
				}
				IntVec3 centre = new IntVec3((int)Math.Round((double)sx / cl.Count), 0, (int)Math.Round((double)sz / cl.Count));
				parts.Add(cl.Count + " around " + AgentPlace.Describe(map, centre));
			}
			string text = string.Join("; ", parts.ToArray());
			if (clusters.Count > MaxClusters)
			{
				text += "; and " + (clusters.Count - MaxClusters) + " more places";
			}
			return text;
		}

		private static bool AnyoneDoes(Map map, WorkTypeDef work)
		{
			if (work == null)
			{
				return true;
			}
			foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
			{
				if (p.workSettings != null && p.workSettings.Initialized && p.workSettings.WorkIsActive(work))
				{
					return true;
				}
			}
			return false;
		}

		// ---- designate cancel ---------------------------------------------------------------------------------------

		// "designate cancel" -> a plan; any other form -> null (the caller tries the next dispatcher).
		public static PlanReply Plan(FormContext c)
		{
			if (c == null || c.form != "designate cancel")
			{
				return null;
			}
			Map map = c.map;
			if (map == null)
			{
				return PlanReply.Fail("There is no map to look at right now.");
			}
			string kind = (c.Text("KIND") ?? "").Trim().ToLowerInvariant();
			List<KindRow> chosenRows;
			if (kind == "designations")
			{
				chosenRows = Rows();
			}
			else
			{
				chosenRows = Rows().Where((KindRow r) => r.kind == kind).ToList();
				if (chosenRows.Count == 0)
				{
					return PlanReply.Fail("'" + kind + "' is not a kind of mark; say one of " + string.Join(", ", Rows().Select((KindRow r) => r.kind).ToArray()) + " or designations.");
				}
			}
			string mode = (c.Text("MODE") ?? "").Trim().ToLowerInvariant();
			if (mode != "area" && mode != "all")
			{
				mode = c.Has("AREA") ? "area" : "all";
			}
			CellRect? rect = null;
			if (mode == "area")
			{
				CellRect r;
				string reason;
				if (!AgentPlace.TryRect(map, c.Text("AREA"), out r, out reason))
				{
					return PlanReply.Fail(Cap1(reason));
				}
				rect = r;
			}

			List<Designation> marks = new List<Designation>();
			foreach (KindRow row in chosenRows)
			{
				// Designator_Cancel removes only designations whose def is designateCancelable (Designator_Cancel.cs:57-60).
				marks.AddRange(Marks(map, row, rect).Where((Designation d) => d.def.designateCancelable));
			}
			string kindWord = kind == "designations" ? "" : kind + " ";
			string where = rect.HasValue ? " in " + AgentPlace.DescribeRect(map, rect.Value) : "";
			if (marks.Count == 0)
			{
				return PlanReply.Fail("No " + (kind == "designations" ? "marks" : kind) + " " + (kind == "designations" ? "are" : "is") + " marked in view" + where + ".");
			}
			marks = marks.OrderBy((Designation d) => d.target.Cell.x).ThenBy((Designation d) => d.target.Cell.z).ToList();

			string line = "Cancel " + marks.Count + " " + kindWord + (marks.Count == 1 ? "mark" : "marks") + " in view" + where
				+ " (" + Clusters(map, marks) + ").";
			List<Designation> planned = marks.ToList();
			int total = planned.Count;
			Plan plan = FormPlanner.NewPlan("designate cancel");
			Write write = new Write();
			write.description = line;
			write.apply = delegate
			{
				int removed = 0;
				foreach (Designation des in planned)
				{
					if (!StillThere(map, des))
					{
						continue;
					}
					map.designationManager.RemoveDesignation(des);
					removed++;
				}
				if (removed == 0)
				{
					throw new Fault("None of the " + total + " " + kindWord + "marks is still there, so nothing was cancelled.");
				}
			};
			plan.writes.Add(write.Reads(() => planned.Count((Designation d) => StillThere(map, d)) + " " + kindWord + "marks left of the " + total + " planned"));
			return FormPlanner.Reply(plan, line, null, null);
		}

		private static bool StillThere(Map map, Designation des)
		{
			if (map == null || Find.Maps == null || !Find.Maps.Contains(map))
			{
				return false;
			}
			if (des.def.targetType == TargetType.Cell)
			{
				return map.designationManager.DesignationAt(des.target.Cell, des.def) == des;
			}
			Thing t = des.target.Thing;
			return t != null && t.Spawned && map.designationManager.DesignationOn(t, des.def) == des;
		}

		private static string Cap1(string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return "That area could not be read.";
			}
			return char.ToUpperInvariant(s[0]) + s.Substring(1);
		}

		// ---- questions ----------------------------------------------------------------------------------------------

		// "query designations" / "query wildlife" -> the answer; any other form -> null.
		public static QueryReply Query(FormContext c)
		{
			if (c == null)
			{
				return null;
			}
			if (c.form == "query designations")
			{
				return c.map == null ? QueryReply.Fail("There is no map to look at right now.") : QueryReply.Ok(ShowDesignations(c.map));
			}
			if (c.form == "query wildlife")
			{
				return c.map == null ? QueryReply.Fail("There is no map to look at right now.") : QueryReply.Ok(ShowWildlife(c.map));
			}
			return null;
		}

		// One line per kind that has marks in view: "mining: 14 marked (9 around ...; 5 around ...); nobody does Mining".
		private static string ShowDesignations(Map map)
		{
			List<string> lines = new List<string>();
			foreach (KindRow row in Rows())
			{
				List<Designation> marks = Marks(map, row, null);
				if (marks.Count == 0)
				{
					continue;
				}
				string line = row.kind + ": " + marks.Count + " marked (" + Clusters(map, marks) + ")";
				if (!AnyoneDoes(map, row.workType))
				{
					line += "; nobody does " + row.work;
				}
				lines.Add(line);
			}
			if (lines.Count == 0)
			{
				return "Nothing is marked: no mining, chopping, cutting, harvesting, deconstructing, uninstalling, hauling, hunting, taming, slaughtering, stripping or smoothing in view.";
			}
			return string.Join("\n", lines.ToArray());
		}

		// Wild animals in view by kind: "deer: 4, nearest 120, 88 (30 tiles east of Bob); hunting: safe; taming: yes; 1 marked to hunt".
		private static string ShowWildlife(Map map)
		{
			IntVec3 home = AgentPlace.ColonyCentre(map);
			Dictionary<string, List<Pawn>> byKind = new Dictionary<string, List<Pawn>>();
			int angry = 0;
			foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
			{
				if (p == null || p.Dead || p.RaceProps == null || !p.RaceProps.Animal || p.Faction != null)
				{
					continue;
				}
				if (!AgentPlace.Visible(map, p.Position))
				{
					continue;
				}
				if (p.InAggroMentalState)
				{
					angry++;
					continue;
				}
				string label = p.kindDef != null && p.kindDef.label != null ? p.kindDef.label : p.def.label;
				List<Pawn> list;
				if (!byKind.TryGetValue(label, out list))
				{
					list = new List<Pawn>();
					byKind[label] = list;
				}
				list.Add(p);
			}
			List<string> lines = new List<string>();
			List<KeyValuePair<string, List<Pawn>>> kinds = byKind.OrderByDescending((KeyValuePair<string, List<Pawn>> kv) => kv.Value.Count)
				.ThenBy((KeyValuePair<string, List<Pawn>> kv) => kv.Key).ToList();
			foreach (KeyValuePair<string, List<Pawn>> kv in kinds.Take(MaxWildKinds))
			{
				List<Pawn> group = kv.Value;
				Pawn nearest = group.OrderBy((Pawn p) => (p.Position - home).LengthHorizontalSquared).ThenBy((Pawn p) => p.thingIDNumber).First();
				Pawn sample = nearest;
				string hunting;
				if (sample.RaceProps.manhunterOnDamageChance >= 0.015f)
				{
					hunting = "may attack the hunter (" + Pct(PawnUtility.GetManhunterOnDamageChance(sample)) + ")";
				}
				else
				{
					hunting = "safe";
				}
				if (sample.RaceProps.predator)
				{
					hunting += ", predator";
				}
				string taming;
				if (TameUtility.CanTame(sample))
				{
					taming = "yes";
					float fail = PawnUtility.GetManhunterOnTameFailChance(sample);
					if (fail >= 0.015f)
					{
						taming += " (turns on the tamer if taming fails, " + Pct(fail) + ")";
					}
				}
				else
				{
					taming = "no";
				}
				string line = kv.Key + ": " + group.Count + ", nearest " + AgentPlace.Describe(map, nearest.Position) + "; hunting: " + hunting + "; taming: " + taming;
				int hunt = group.Count((Pawn p) => map.designationManager.DesignationOn(p, DesignationDefOf.Hunt) != null);
				int tame = group.Count((Pawn p) => map.designationManager.DesignationOn(p, DesignationDefOf.Tame) != null);
				if (hunt > 0)
				{
					line += "; " + hunt + " marked to hunt";
				}
				if (tame > 0)
				{
					line += "; " + tame + " marked to tame";
				}
				lines.Add(line);
			}
			if (kinds.Count > MaxWildKinds)
			{
				lines.Add("... and " + (kinds.Count - MaxWildKinds) + " more kinds not listed.");
			}
			if (lines.Count == 0)
			{
				lines.Add("No wild animals in view.");
			}
			if (angry > 0)
			{
				lines.Add(angry + (angry == 1 ? " angry animal is" : " angry animals are") + " not listed here: ask 'show threats.'");
			}
			return string.Join("\n", lines.ToArray());
		}

		private static string Pct(float v)
		{
			return Math.Round(v * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
		}
	}
}
