using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Wind-path check for build plans (user, 2026-10-14: "make a code that runs whenever agent tries to order turbine
	// bp, sees if there are tiles that would block placement. it does *NOT* reject, it merely informs the agent for
	// confirmation. ... probably goes same for building walls and other things that are high & can block. does not
	// trigger unless a wall is being put on those area.").
	//
	// Two questions, both answered as plan NOTES (never a Fault); a note also makes the plan wait for confirm
	// (Plan.askConfirm), even when the order said raw apply:
	//   1. A wind turbine is being planned: what already stands (or is planned) in ITS wind path, and what else in
	//      this same order lands there.
	//   2. Something that blocks wind (ThingDef.blockWind: walls, doors, batteries, heaters, trees ..., or a roof) is
	//      being planned: which EXISTING turbines (built, frame or blueprint) have it in their wind path.
	// Nothing is noted when no turbine is involved, so an ordinary wall order is untouched.
	//
	// Game facts relied on (decompiled tree sources\RimWorldDecompiled\, line numbers of that tree):
	//   WindTurbineUtility.CalculateWindCells(center, rot, size): the two strips, 7 wide, 10 in front and 6 behind
	//       for north/east, 6 in front and 10 behind for south/west        RimWorld\WindTurbineUtility.cs:8-58
	//   CompPowerPlantWind.RecalculateBlockages: a path cell counts once when it is roofed, else once when any thing
	//       on it has def.blockWind                                        RimWorld\CompPowerPlantWind.cs:208-239
	//   CompPowerPlantWind.CompTick: output minus 20% per blocked cell, floor 0  RimWorld\CompPowerPlantWind.cs:114-125
	//       (PowerReductionPercentPerObstacle = 0.2f                          RimWorld\CompPowerPlantWind.cs:46)
	//   ThingDef.blockWind                                                  Verse\ThingDef.cs:320
	//   ThingDef.entityDefToBuild (what a blueprint / frame becomes)        Verse\ThingDef.cs:373
	//   GenAdj.OccupiedRect(center, rot, size)                              Verse\GenAdj.cs:595
	//   ListerBuildings.allBuildingsColonist                                Verse\ListerBuildings.cs:68
	//   rimworldwiki Wind_turbine (rev 179675): "The exclusion zone of several turbines may overlap, so long as the
	//       turbine building itself isn't in any of the zones."
	internal static class AgentWindCheck
	{
		internal struct Placed
		{
			public ThingDef def;
			public IntVec3 cell;
			public Rot4 rot;
			public Placed(ThingDef def, IntVec3 cell, Rot4 rot)
			{
				this.def = def;
				this.cell = cell;
				this.rot = rot;
			}
		}

		private sealed class Turbine
		{
			public string label;        // "wind turbine", "wind turbine blueprint", "wind turbine frame"
			public IntVec3 cell;
			public HashSet<IntVec3> path = new HashSet<IntVec3>();
			public HashSet<IntVec3> body = new HashSet<IntVec3>();
		}

		private const float ReductionPerCell = 0.2f;   // CompPowerPlantWind.cs:46
		private const int MaxReasonsNamed = 6;

		internal static bool IsTurbine(ThingDef def)
		{
			if (def == null || def.comps == null)
			{
				return false;
			}
			for (int i = 0; i < def.comps.Count; i++)
			{
				Type cls = def.comps[i].compClass;
				if (cls != null && typeof(CompPowerPlantWind).IsAssignableFrom(cls))
				{
					return true;
				}
			}
			return false;
		}

		// What a thing on the map will be once built: the def itself, or for a blueprint / frame the building it becomes.
		private static ThingDef BuiltDef(Thing t)
		{
			if (t == null)
			{
				return null;
			}
			if (t.def.IsBlueprint || t.def.IsFrame)
			{
				return t.def.entityDefToBuild as ThingDef;
			}
			return t.def;
		}

		private static bool BlocksWind(ThingDef def)
		{
			return def != null && def.blockWind;
		}

		private static string LabelOf(ThingDef def)
		{
			return def.label.NullOrEmpty() ? def.defName : def.label;
		}

		// ---- entry points ---------------------------------------------------------------------------------

		internal static void Note(Plan plan, Map map, ThingDef def, IntVec3 cell, Rot4 rot)
		{
			Note(plan, map, new List<Placed> { new Placed(def, cell, rot) });
		}

		internal static void Note(Plan plan, Map map, ThingDef def, IEnumerable<IntVec3> cells, Rot4 rot)
		{
			List<Placed> list = new List<Placed>();
			foreach (IntVec3 c in cells)
			{
				list.Add(new Placed(def, c, rot));
			}
			Note(plan, map, list);
		}

		internal static void Note(Plan plan, Map map, List<Placed> placed)
		{
			try
			{
				if (plan == null || map == null || placed == null || placed.Count == 0)
				{
					return;
				}
				bool anyBlocker = placed.Any((Placed p) => BlocksWind(p.def) || IsTurbine(p.def));
				if (!anyBlocker)
				{
					return;
				}
				List<Turbine> existing = ExistingTurbines(map);
				List<Turbine> planned = new List<Turbine>();
				foreach (Placed p in placed)
				{
					if (IsTurbine(p.def))
					{
						planned.Add(MakeTurbine(map, LabelOf(p.def), p.cell, p.rot, p.def.Size));
					}
				}
				if (existing.Count == 0 && planned.Count == 0)
				{
					return;
				}
				int before = plan.notes.Count;

				// 1. Each turbine of this order: what blocks its path, on the map and in this same order.
				foreach (Turbine t in planned)
				{
					Dictionary<string, int> reasons = new Dictionary<string, int>();
					List<string> order = new List<string>();
					int blocked = 0;
					foreach (IntVec3 c in t.path)
					{
						string why = MapBlocker(map, c);
						if (why == null)
						{
							why = PlanBlocker(placed, t, c);
						}
						if (why == null)
						{
							continue;
						}
						blocked++;
						Bump(reasons, order, why);
					}
					if (blocked > 0)
					{
						plan.notes.Add("WIND: the " + t.label + " at " + Xz(t.cell) + " would have " + blocked + " of its " + t.path.Count
							+ " wind-path cells blocked (" + ReasonText(reasons, order) + "): about " + Percent(blocked) + " of its output. "
							+ "Each blocked cell takes 20% off; 5 cells and it makes nothing. Say apply to build it anyway, or cancel and pick open ground.");
					}
					string geyser = GeyserNote(map, t);
					if (geyser != null)
					{
						plan.notes.Add(geyser);
					}
				}

				// 2. Existing turbines: what this order puts into their paths.
				foreach (Turbine u in existing)
				{
					Dictionary<string, int> reasons = new Dictionary<string, int>();
					List<string> order = new List<string>();
					HashSet<IntVec3> counted = new HashSet<IntVec3>();
					int added = 0;
					foreach (Placed p in placed)
					{
						if (!BlocksWind(p.def))
						{
							continue;
						}
						string label = LabelOf(p.def);
						foreach (IntVec3 c in GenAdj.OccupiedRect(p.cell, p.rot, p.def.Size))
						{
							if (!u.path.Contains(c) || counted.Contains(c))
							{
								continue;
							}
							// Already blocked on the map: this order changes nothing for that cell.
							if (MapBlocker(map, c) != null)
							{
								continue;
							}
							counted.Add(c);
							added++;
							Bump(reasons, order, label);
						}
					}
					if (added == 0)
					{
						continue;
					}
					int already = u.path.Count((IntVec3 c) => MapBlocker(map, c) != null);
					int total = already + added;
					plan.notes.Add("WIND: this puts " + added + (added == 1 ? " cell" : " cells") + " (" + ReasonText(reasons, order) + ") into the wind path of the "
						+ u.label + " at " + Xz(u.cell) + (already > 0 ? ", which already has " + already + " blocked" : "")
						+ ": it would go to about " + Percent(total) + " of its output. Each blocked cell takes 20% off; 5 cells and it makes nothing. "
						+ "Say apply to build anyway, or cancel and keep the 7 by 18 box around the turbine clear.");
				}

				if (plan.notes.Count > before)
				{
					plan.askConfirm = true;
					plan.confirmBy = "Wind check";   // the harness log (HarnessLog.cs, H4)
				}
			}
			catch (Exception ex)
			{
				// A check that only informs must never stop a plan.
				Log.Warning("[VerbalCommands] wind check skipped: " + ex.Message);
			}
		}

		// Roof marks (build roof area): a roofed path cell counts the same as a wall (CompPowerPlantWind.cs:221-225).
		internal static void NoteRoof(Plan plan, Map map, List<IntVec3> cells)
		{
			try
			{
				if (plan == null || map == null || cells == null || cells.Count == 0)
				{
					return;
				}
				List<Turbine> existing = ExistingTurbines(map);
				if (existing.Count == 0)
				{
					return;
				}
				int before = plan.notes.Count;
				foreach (Turbine u in existing)
				{
					int added = 0;
					foreach (IntVec3 c in cells)
					{
						if (u.path.Contains(c) && MapBlocker(map, c) == null)
						{
							added++;
						}
					}
					if (added == 0)
					{
						continue;
					}
					int already = u.path.Count((IntVec3 c) => MapBlocker(map, c) != null);
					plan.notes.Add("WIND: " + added + " of these roof cells " + (added == 1 ? "lies" : "lie") + " in the wind path of the " + u.label + " at " + Xz(u.cell)
						+ (already > 0 ? ", which already has " + already + " blocked" : "") + ": once roofed it would go to about " + Percent(already + added)
						+ " of its output. Say apply to roof anyway, or cancel.");
				}
				if (plan.notes.Count > before)
				{
					plan.askConfirm = true;
					plan.confirmBy = "Wind check";   // the harness log (HarnessLog.cs, H4)
				}
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] wind check (roof) skipped: " + ex.Message);
			}
		}

		// A planned turbine on a steam geyser's spot (user, 2026-10-15: a screenshot of "[reminder] Your turbine seems to
		// block potential placement for hydrothermal vent." / "this also needs to be a logic." / "Is that logic alongside
		// turbine blocking wall or separate. and gated behind agent building a turbine i hope?"). The hydrothermal vent is
		// the game's steam geyser (Buildings_Natural.xml:260); a geothermal generator (6x6, Buildings_Power.xml:566-627)
		// goes only on one, its Position on the geyser's (PlaceWorker_OnSteamGeyser). A note when the turbine's own body
		// (not its wind path, which does not stop placing) overlaps that 6x6 footprint of a geyser in view that has no
		// geothermal generator on it yet (built, framed or blueprinted). Only on a turbine order, like the wind notes.
		private static string GeyserNote(Map map, Turbine t)
		{
			ThingDef geo = DefDatabase<ThingDef>.GetNamedSilentFail("GeothermalGenerator");
			if (geo == null || ThingDefOf.SteamGeyser == null)
			{
				return null;
			}
			foreach (Thing g in map.listerThings.ThingsOfDef(ThingDefOf.SteamGeyser))
			{
				if (!g.Spawned || !AgentPlace.Visible(map, g.Position))
				{
					continue;
				}
				if (g.Position.GetThingList(map).Any((Thing x) => x.def == geo || BuiltDef(x) == geo))
				{
					continue;
				}
				CellRect footprint = GenAdj.OccupiedRect(g.Position, Rot4.North, geo.Size);
				int overlap = t.body.Count((IntVec3 c) => footprint.Contains(c));
				if (overlap == 0)
				{
					continue;
				}
				return "GEYSER: the " + t.label + " at " + Xz(t.cell) + " would stand on " + overlap + (overlap == 1 ? " cell" : " cells")
					+ " of the spot a geothermal generator needs on the steam geyser at " + Xz(g.Position) + " (6 by 6, centred on the geyser)"
					+ (geo.IsResearchFinished ? "" : "; geothermal power is not researched yet")
					+ ". A geothermal generator gives steady power day and night. Say apply to build the turbine anyway, or cancel and place it clear of the geyser.";
			}
			return null;
		}

		// ---- helpers --------------------------------------------------------------------------------------

		private static List<Turbine> ExistingTurbines(Map map)
		{
			List<Turbine> list = new List<Turbine>();
			foreach (Building b in map.listerBuildings.allBuildingsColonist)
			{
				if (IsTurbine(b.def))
				{
					list.Add(MakeTurbine(map, LabelOf(b.def), b.Position, b.Rotation, b.def.Size));
				}
			}
			foreach (ThingRequestGroup group in new ThingRequestGroup[] { ThingRequestGroup.Blueprint, ThingRequestGroup.BuildingFrame })
			{
				foreach (Thing t in map.listerThings.ThingsInGroup(group))
				{
					ThingDef built = BuiltDef(t);
					if (!IsTurbine(built) || t.Faction != Faction.OfPlayer)
					{
						continue;
					}
					list.Add(MakeTurbine(map, LabelOf(built) + (t.def.IsFrame ? " frame" : " blueprint"), t.Position, t.Rotation, built.Size));
				}
			}
			return list;
		}

		private static Turbine MakeTurbine(Map map, string label, IntVec3 cell, Rot4 rot, IntVec2 size)
		{
			Turbine t = new Turbine();
			t.label = label;
			t.cell = cell;
			foreach (IntVec3 c in WindTurbineUtility.CalculateWindCells(cell, rot, size))
			{
				if (c.InBounds(map))
				{
					t.path.Add(c);
				}
			}
			foreach (IntVec3 c in GenAdj.OccupiedRect(cell, rot, size))
			{
				t.body.Add(c);
			}
			return t;
		}

		// Why a map cell blocks wind now, as the game counts it (roof first, then the first blocking thing), or null.
		// A blueprint or frame of a blocking building counts too: the agent reads plans as what the base WILL be.
		private static string MapBlocker(Map map, IntVec3 c)
		{
			if (!c.InBounds(map))
			{
				return null;
			}
			if (map.roofGrid.Roofed(c))
			{
				return "roof";
			}
			List<Thing> things = c.GetThingList(map);
			for (int i = 0; i < things.Count; i++)
			{
				Thing t = things[i];
				if (t.def.blockWind)
				{
					return LabelOf(t.def);
				}
				ThingDef built = BuiltDef(t);
				if (built != null && built != t.def && built.blockWind)
				{
					return LabelOf(built) + (t.def.IsFrame ? " frame" : " blueprint");
				}
			}
			return null;
		}

		// Why a cell will be blocked by ANOTHER placement of this same order (not the turbine's own body), or null.
		private static string PlanBlocker(List<Placed> placed, Turbine self, IntVec3 c)
		{
			foreach (Placed p in placed)
			{
				if (!BlocksWind(p.def))
				{
					continue;
				}
				if (IsTurbine(p.def) && p.cell == self.cell)
				{
					continue;
				}
				foreach (IntVec3 cell in GenAdj.OccupiedRect(p.cell, p.rot, p.def.Size))
				{
					if (cell == c)
					{
						return LabelOf(p.def) + " of this order";
					}
				}
			}
			return null;
		}

		private static void Bump(Dictionary<string, int> counts, List<string> order, string key)
		{
			int n;
			if (!counts.TryGetValue(key, out n))
			{
				order.Add(key);
			}
			counts[key] = n + 1;
		}

		private static string ReasonText(Dictionary<string, int> counts, List<string> order)
		{
			List<string> parts = new List<string>();
			int rest = 0;
			for (int i = 0; i < order.Count; i++)
			{
				if (i < MaxReasonsNamed)
				{
					parts.Add(counts[order[i]] + " " + order[i]);
				}
				else
				{
					rest += counts[order[i]];
				}
			}
			if (rest > 0)
			{
				parts.Add(rest + " other");
			}
			return string.Join(", ", parts.ToArray());
		}

		private static string Percent(int blocked)
		{
			float left = Math.Max(0f, 1f - ReductionPerCell * blocked);
			return ((int)Math.Round(left * 100f)).ToString(CultureInfo.InvariantCulture) + "%";
		}

		private static string Xz(IntVec3 c)
		{
			return c.x.ToString(CultureInfo.InvariantCulture) + ", " + c.z.ToString(CultureInfo.InvariantCulture);
		}
	}
}
