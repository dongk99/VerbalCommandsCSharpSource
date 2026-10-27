using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Sun lamp / hydroponics check for build plans (user, 2026-10-14: "write a similar code for sun lamp/hydroponics as
	// well"), the twin of AgentWindCheck. NOTES only, never a Fault; a note makes the plan wait for confirm
	// (Plan.askConfirm), even under raw apply.
	//   1. A sun lamp is being planned: how many cells it lights brightly enough for crops, what cuts it off (walls,
	//      rock, doors of this order too), what grows there (basins, growing zones), whether it stands unroofed
	//      (rain short-circuit) and whether a conduit is within reach.
	//   2. A hydroponics basin is being planned: which of its cells are roofed with no sun lamp over them (crops will
	//      not grow), whether a conduit is within reach (plants die without power), whether the spot is at a growing
	//      temperature for its default plant.
	//   3. A light blocker (ThingDef.blockLight: walls, doors, rock ...) or a build-roof area is being planned: which
	//      existing sun lamps lose growing cells (basins, growing zones) to it, or which growing cells lose the sun.
	// Nothing is noted when none of that applies, so an ordinary wall order is untouched.
	//
	// Game facts relied on (decompiled tree sources\RimWorldDecompiled\, line numbers of that tree):
	//   Light flood: from the lamp cell (distance 1.00) over the 8 neighbours, +1.00 straight, +1.41 diagonal, never
	//       through a light blocker, no diagonal between two blockers, up to glowRadius
	//                                                            Verse.Glow\ComputeGlowGridsJob.cs:25-27, :68-152, :164
	//   A cell is OVERLIT (game glow 1.0) when floor(distance) < overlightRadius      ComputeGlowGridsJob.cs:188 (alpha =
	//       (int)distance), GlowGrid.cs:141-157 (AddColors: a = 1 when a < overlightRadius), GlowGrid.cs:244-263
	//       (GroundGlowAt: a == 1 -> 1f; any other lamp light is capped at 0.5)
	//   Unroofed cells get the sky glow first (1.0 by day)        Verse\GlowGrid.cs:247-255
	//   Light blockers: a spawned Building whose def.blockLight    Verse\Building.cs:140, :242; Verse\ThingDef.cs:318
	//   Crops need glow >= plant.growMinGlow (0.51 default)        RimWorld\PlantProperties.cs:75; PlantUtility.cs:361-368;
	//       Plant.cs:452 (HasEnoughLightToGrow), :832 (no light -> no growth), :214 (dies after 450000 unlit ticks)
	//   Sun lamp: CompProperties_Glower overlightRadius 7.0, glowRadius 14; 2900 W; shortCircuitInRain; on 06:00-19:12
	//       Data\Core\Defs\ThingDefs_Buildings\Buildings_Furniture.xml:1369-1416; Building_SunLamp.GrowableCells =
	//       RadialCellsAround(specialDisplayRadius 5.8)             RimWorld\Building_SunLamp.cs:10
	//   Hydroponics basin: Building_PlantGrower, 1x4, fertility 2.8, 70 W, default rice; unpowered -> plants take rot
	//       damage every rare tick                                  Buildings_Production.xml (HydroponicsBasin),
	//       RimWorld\Building_PlantGrower.cs:65-77, :115-122
	//   Growth needs the cell temperature inside the plant's range  RimWorld\PlantUtility.cs:21-31, Plant.cs:296
	//   A connector finds a transmitter within range (conduit)      RimWorld\PowerConnectionMaker.cs:101
	//   rimworldwiki Sun_lamp (rev 177585): "The inner gray area (5 tiles out) is 100% illumination, able to grow all
	//       crops"; "Rain or snow on a powered sun lamp will cause short circuits and fires. A roof will prevent this."
	//   rimworldwiki Hydroponics_basin (rev 179488): "when unpowered, its plants will wither very quickly"; "plants
	//       require at least 51% light, while standing lamps provide a maximum of 50% lighting and do not 'stack'".
	internal static class AgentLightCheck
	{
		private sealed class Lamp
		{
			public string label;
			public IntVec3 cell;
			public float overlight;
			public float radius;
		}

		private const int CardinalCost = 100;     // ComputeGlowGridsJob.cs:25
		private const int DiagonalCost = 141;     // ComputeGlowGridsJob.cs:27
		private const int StartDist = 100;        // ComputeGlowGridsJob.cs:164
		private const int MaxReasonsNamed = 6;

		private static readonly IntVec3[] Directions = new IntVec3[8]
		{
			new IntVec3(0, 0, -1), new IntVec3(1, 0, 0), new IntVec3(0, 0, 1), new IntVec3(-1, 0, 0),
			new IntVec3(1, 0, -1), new IntVec3(1, 0, 1), new IntVec3(-1, 0, 1), new IntVec3(-1, 0, -1)
		};

		// ---- what a def is -------------------------------------------------------------------------------

		private static CompProperties_Glower Glower(ThingDef def)
		{
			if (def == null || def.comps == null)
			{
				return null;
			}
			for (int i = 0; i < def.comps.Count; i++)
			{
				CompProperties_Glower g = def.comps[i] as CompProperties_Glower;
				if (g != null)
				{
					return g;
				}
			}
			return null;
		}

		// A lamp bright enough for crops: the only Core lamp with an overlight radius is the sun lamp.
		internal static bool IsSunLamp(ThingDef def)
		{
			CompProperties_Glower g = Glower(def);
			return g != null && g.overlightRadius > 0f;
		}

		internal static bool IsPlantGrower(ThingDef def)
		{
			return def != null && def.thingClass != null && typeof(Building_PlantGrower).IsAssignableFrom(def.thingClass);
		}

		private static bool BlocksLight(ThingDef def)
		{
			return def != null && def.blockLight;
		}

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

		private static string LabelOf(ThingDef def)
		{
			return def.label.NullOrEmpty() ? def.defName : def.label;
		}

		// ---- entry points ---------------------------------------------------------------------------------

		internal static void Note(Plan plan, Map map, ThingDef def, IntVec3 cell, Rot4 rot)
		{
			// Settings gate (user, 2026-10-26): Building placement checks.
			if (!VerbalCommandsMod.PlacementChecksOn)
			{
				return;
			}
			Note(plan, map, new List<AgentWindCheck.Placed> { new AgentWindCheck.Placed(def, cell, rot) });
		}

		internal static void Note(Plan plan, Map map, ThingDef def, IEnumerable<IntVec3> cells, Rot4 rot)
		{
			// Settings gate (user, 2026-10-26): Building placement checks.
			if (!VerbalCommandsMod.PlacementChecksOn)
			{
				return;
			}
			List<AgentWindCheck.Placed> list = new List<AgentWindCheck.Placed>();
			foreach (IntVec3 c in cells)
			{
				list.Add(new AgentWindCheck.Placed(def, c, rot));
			}
			Note(plan, map, list);
		}

		internal static void Note(Plan plan, Map map, List<AgentWindCheck.Placed> placed)
		{
			// Settings gate (user, 2026-10-26): Building placement checks.
			if (!VerbalCommandsMod.PlacementChecksOn)
			{
				return;
			}
			try
			{
				if (plan == null || map == null || placed == null || placed.Count == 0)
				{
					return;
				}
				bool any = placed.Any((AgentWindCheck.Placed p) => IsSunLamp(p.def) || IsPlantGrower(p.def) || BlocksLight(p.def));
				if (!any)
				{
					return;
				}
				int before = plan.notes.Count;

				// Blockers as the base WILL be: spawned blockers, blueprints and frames of blockers, and this order's.
				HashSet<IntVec3> planBlockers = new HashSet<IntVec3>();
				foreach (AgentWindCheck.Placed p in placed)
				{
					if (BlocksLight(p.def))
					{
						foreach (IntVec3 c in GenAdj.OccupiedRect(p.cell, p.rot, p.def.Size))
						{
							planBlockers.Add(c);
						}
					}
				}
				List<Lamp> existing = ExistingLamps(map);
				List<Lamp> planned = new List<Lamp>();
				foreach (AgentWindCheck.Placed p in placed)
				{
					if (IsSunLamp(p.def))
					{
						planned.Add(MakeLamp(LabelOf(p.def), p.cell, Glower(p.def)));
					}
				}
				// Cells any existing lamp lights for crops after this order (blockers of this order included).
				HashSet<IntVec3> litByExisting = new HashSet<IntVec3>();
				foreach (Lamp l in existing)
				{
					litByExisting.UnionWith(Overlit(map, l, planBlockers));
				}
				HashSet<IntVec3> litByPlanned = new HashSet<IntVec3>();
				foreach (Lamp l in planned)
				{
					litByPlanned.UnionWith(Overlit(map, l, planBlockers));
				}

				// 1. Each sun lamp of this order.
				foreach (Lamp l in planned)
				{
					HashSet<IntVec3> lit = Overlit(map, l, planBlockers);
					int possible = GenRadial.RadialCellsAround(l.cell, 5.8f, true).Count((IntVec3 c) => c.InBounds(map));
					int roofedLit = lit.Count((IntVec3 c) => map.roofGrid.Roofed(c));
					int cutOff = Math.Max(0, possible - lit.Count);
					int basinCells = 0;
					int zoneCells = 0;
					int alreadyLit = 0;
					foreach (IntVec3 c in lit)
					{
						if (GrowerCell(map, c))
						{
							basinCells++;
						}
						else if (map.zoneManager.ZoneAt(c) is Zone_Growing)
						{
							zoneCells++;
						}
						if (litByExisting.Contains(c))
						{
							alreadyLit++;
						}
					}
					string text = "LIGHT: the " + l.label + " at " + Xz(l.cell) + " would light " + lit.Count + " cells brightly enough for crops"
						+ (cutOff > 0 ? " (" + cutOff + " of the usual " + possible + " are cut off by walls or rock)" : "")
						+ "; " + roofedLit + " of them " + (roofedLit == 1 ? "is" : "are") + " roofed (where a lamp matters; unroofed cells get the sun by day)"
						+ "; under it now: " + basinCells + " hydroponics " + (basinCells == 1 ? "cell" : "cells") + ", " + zoneCells + " growing-zone " + (zoneCells == 1 ? "cell" : "cells")
						+ (alreadyLit > 0 ? "; " + alreadyLit + " already lit by another sun lamp" : "") + ".";
					if (!map.roofGrid.Roofed(l.cell))
					{
						text += " Its own cell is unroofed: a powered sun lamp short-circuits in rain or snow.";
					}
					CompPower tx = PowerConnectionMaker.BestTransmitterForConnector(l.cell, map, null);
					if (tx == null)
					{
						text += " No conduit or powered building is within reach of it: it will have no power (needs 2900 W from 06:00 to 19:12).";
					}
					text += " Say apply to build it anyway, or cancel.";
					plan.notes.Add(text);
				}

				// 2. Each hydroponics basin of this order.
				foreach (AgentWindCheck.Placed p in placed)
				{
					if (!IsPlantGrower(p.def))
					{
						continue;
					}
					List<IntVec3> cells = GenAdj.OccupiedRect(p.cell, p.rot, p.def.Size).Cells.Where((IntVec3 c) => c.InBounds(map)).ToList();
					int dark = 0;
					foreach (IntVec3 c in cells)
					{
						if (map.roofGrid.Roofed(c) && !litByExisting.Contains(c) && !litByPlanned.Contains(c))
						{
							dark++;
						}
					}
					List<string> parts = new List<string>();
					if (dark > 0)
					{
						parts.Add(dark + " of its " + cells.Count + " cells " + (dark == 1 ? "is" : "are") + " roofed with no sun lamp lighting " + (dark == 1 ? "it" : "them")
							+ ": crops there will not grow (they need 51% light; a sun lamp gives 100% within about 5.8 cells, a standing lamp only 50%)");
					}
					CompPower tx = PowerConnectionMaker.BestTransmitterForConnector(p.cell, map, null);
					if (tx == null)
					{
						parts.Add("no conduit or powered building is within reach: it will have no power (needs 70 W day and night) and plants in it die quickly without it");
					}
					ThingDef plant = p.def.building != null ? p.def.building.defaultPlantToGrow : null;
					if (plant != null && plant.plant != null && !PlantUtility.GrowthSeasonNow(p.cell, map, plant))
					{
						float temp = p.cell.GetTemperature(map);
						parts.Add("the spot is " + temp.ToString("0", CultureInfo.InvariantCulture) + "C now; " + LabelOf(plant) + " grows only between "
							+ plant.plant.minGrowthTemperature.ToString("0", CultureInfo.InvariantCulture) + "C and " + plant.plant.maxGrowthTemperature.ToString("0", CultureInfo.InvariantCulture) + "C");
					}
					if (parts.Count > 0)
					{
						plan.notes.Add("LIGHT: the " + LabelOf(p.def) + " at " + Xz(p.cell) + ": " + string.Join("; ", parts.ToArray()) + ". Say apply to build it anyway, or cancel.");
					}
				}

				// 3. Blockers of this order against existing sun lamps: growing cells they lose.
				if (planBlockers.Count > 0)
				{
					foreach (Lamp l in existing)
					{
						HashSet<IntVec3> beforeLit = Overlit(map, l, null);
						HashSet<IntVec3> afterLit = Overlit(map, l, planBlockers);
						int lostBasin = 0;
						int lostZone = 0;
						int lostOther = 0;
						foreach (IntVec3 c in beforeLit)
						{
							if (afterLit.Contains(c) || planBlockers.Contains(c) || !map.roofGrid.Roofed(c))
							{
								continue;   // still lit, or becomes the wall itself, or has the sun anyway
							}
							if (GrowerCell(map, c))
							{
								lostBasin++;
							}
							else if (map.zoneManager.ZoneAt(c) is Zone_Growing)
							{
								lostZone++;
							}
							else
							{
								lostOther++;
							}
						}
						if (lostBasin + lostZone == 0)
						{
							continue;
						}
						plan.notes.Add("LIGHT: this cuts the " + l.label + " at " + Xz(l.cell) + " off from " + (lostBasin + lostZone) + " roofed growing "
							+ (lostBasin + lostZone == 1 ? "cell" : "cells") + " (" + lostBasin + " hydroponics, " + lostZone + " growing zone"
							+ (lostOther > 0 ? ", plus " + lostOther + " other roofed cells" : "") + "): crops there will stop growing. Say apply to build anyway, or cancel.");
					}
				}

				if (plan.notes.Count > before)
				{
					plan.askConfirm = true;
					plan.confirmBy = "Sun lamp check";   // the harness log (HarnessLog.cs, H4)
				}

				// The layout picture, after the confirm test so it alone never makes the plan wait.
				if (placed.Any((AgentWindCheck.Placed p) => IsSunLamp(p.def) || IsPlantGrower(p.def)))
				{
					string picture = PictureNote();
					if (picture != null)
					{
						plan.notes.Add(picture);
					}
				}
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] light check skipped: " + ex.Message);
			}
		}

		// Example layout picture (user, 2026-10-14: "make a copy of this file and have it inject to context (as png file)
		// whenever agents are trying to build a sun lamp OR hydroponics bay."): the user's RimworldHydroponicSetup.png,
		// shipped as Tips\images\hydroponics_layout.png (master copy agent_player\knowhow\images\). The mod can not put an
		// image into an agent's context itself; the note gives the file's full path for the agent to open with its Read
		// tool, and send.ps1 prints PICTURE notes to the console like the tips. Every plan that places a sun lamp or a
		// hydroponics basin gets it; null when the file is missing.
		internal const string PictureFile = "hydroponics_layout.png";

		private static string PictureNote()
		{
			string path = System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.Combine(VerbalCommandsMod.ModRootDir ?? "", "Tips"), "images"), PictureFile);
			if (!System.IO.File.Exists(path))
			{
				return null;
			}
			return "PICTURE: an example sun lamp and hydroponics layout (the user's): one sun lamp in the middle, 1x4 hydroponics basins "
				+ "laid around it in a pinwheel so every basin cell is inside the lamp's lit circle, a few floor cells left to walk in, the "
				+ "whole room walled and roofed. Open the image with your Read tool before placing: " + path;
		}

		// Build-roof marks: a roofed growing cell with no sun lamp over it loses the sun.
		internal static void NoteRoof(Plan plan, Map map, List<IntVec3> cells)
		{
			// Settings gate (user, 2026-10-26): Building placement checks.
			if (!VerbalCommandsMod.PlacementChecksOn)
			{
				return;
			}
			try
			{
				if (plan == null || map == null || cells == null || cells.Count == 0)
				{
					return;
				}
				HashSet<IntVec3> lit = new HashSet<IntVec3>();
				foreach (Lamp l in ExistingLamps(map))
				{
					lit.UnionWith(Overlit(map, l, null));
				}
				int basin = 0;
				int zone = 0;
				foreach (IntVec3 c in cells)
				{
					if (map.roofGrid.Roofed(c) || lit.Contains(c))
					{
						continue;
					}
					if (GrowerCell(map, c))
					{
						basin++;
					}
					else if (map.zoneManager.ZoneAt(c) is Zone_Growing)
					{
						zone++;
					}
				}
				if (basin + zone == 0)
				{
					return;
				}
				plan.notes.Add("LIGHT: " + (basin + zone) + " of these cells " + (basin + zone == 1 ? "is a growing cell" : "are growing cells") + " (" + basin + " hydroponics, " + zone
					+ " growing zone) with no sun lamp over " + (basin + zone == 1 ? "it" : "them") + ": once roofed " + (basin + zone == 1 ? "it loses" : "they lose")
					+ " the sun and crops there stop growing. Say apply to roof anyway, or cancel.");
				plan.askConfirm = true;
				plan.confirmBy = "Sun lamp check";   // the harness log (HarnessLog.cs, H4)
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] light check (roof) skipped: " + ex.Message);
			}
		}

		// ---- helpers --------------------------------------------------------------------------------------

		private static List<Lamp> ExistingLamps(Map map)
		{
			List<Lamp> list = new List<Lamp>();
			foreach (Building b in map.listerBuildings.allBuildingsColonist)
			{
				if (IsSunLamp(b.def))
				{
					list.Add(MakeLamp(LabelOf(b.def), b.Position, Glower(b.def)));
				}
			}
			foreach (ThingRequestGroup group in new ThingRequestGroup[] { ThingRequestGroup.Blueprint, ThingRequestGroup.BuildingFrame })
			{
				foreach (Thing t in map.listerThings.ThingsInGroup(group))
				{
					ThingDef built = BuiltDef(t);
					if (!IsSunLamp(built) || t.Faction != Faction.OfPlayer)
					{
						continue;
					}
					list.Add(MakeLamp(LabelOf(built) + (t.def.IsFrame ? " frame" : " blueprint"), t.Position, Glower(built)));
				}
			}
			return list;
		}

		private static Lamp MakeLamp(string label, IntVec3 cell, CompProperties_Glower g)
		{
			Lamp l = new Lamp();
			l.label = label;
			l.cell = cell;
			l.overlight = g != null ? g.overlightRadius : 7f;
			l.radius = g != null ? g.glowRadius : 14f;
			return l;
		}

		// A cell of a hydroponics basin (or other plant grower), built, framed or blueprinted.
		private static bool GrowerCell(Map map, IntVec3 c)
		{
			List<Thing> things = c.GetThingList(map);
			for (int i = 0; i < things.Count; i++)
			{
				if (things[i] is Building_PlantGrower || IsPlantGrower(BuiltDef(things[i])))
				{
					return true;
				}
			}
			return false;
		}

		// A light blocker now or once the plans are built: spawned blocking building, blueprint / frame of one, or this order's.
		private static bool Blocked(Map map, IntVec3 c, HashSet<IntVec3> planBlockers)
		{
			if (planBlockers != null && planBlockers.Contains(c))
			{
				return true;
			}
			Building edifice = map.edificeGrid[c];
			if (edifice != null && edifice.def.blockLight)
			{
				return true;
			}
			List<Thing> things = c.GetThingList(map);
			for (int i = 0; i < things.Count; i++)
			{
				ThingDef built = BuiltDef(things[i]);
				if (built != null && built != things[i].def && built.blockLight)
				{
					return true;
				}
			}
			return false;
		}

		// The game's flood (ComputeGlowGridsJob.Flood), kept to the cells that end up overlit: floor(dist) < overlightRadius.
		private static HashSet<IntVec3> Overlit(Map map, Lamp lamp, HashSet<IntVec3> planBlockers)
		{
			HashSet<IntVec3> result = new HashSet<IntVec3>();
			int maxDist = (int)Math.Round(lamp.radius * 100f);
			Dictionary<IntVec3, int> dist = new Dictionary<IntVec3, int>();
			HashSet<IntVec3> done = new HashSet<IntVec3>();
			dist[lamp.cell] = StartDist;
			// Small area (radius 14): a plain "pop the smallest" is fine.
			while (true)
			{
				IntVec3 t = IntVec3.Invalid;
				int best = int.MaxValue;
				foreach (KeyValuePair<IntVec3, int> kv in dist)
				{
					if (!done.Contains(kv.Key) && kv.Value < best)
					{
						best = kv.Value;
						t = kv.Key;
					}
				}
				if (!t.IsValid)
				{
					break;
				}
				done.Add(t);
				if ((int)(best / 100f) < lamp.overlight)
				{
					result.Add(t);
				}
				bool[] blockers = new bool[8];
				for (int i = 0; i < Directions.Length; i++)
				{
					IntVec3 c = t + Directions[i];
					if (!c.InBounds(map))
					{
						continue;
					}
					bool flag = Blocked(map, c, planBlockers);
					blockers[i] = flag;
					if (flag || done.Contains(c))
					{
						continue;
					}
					int step = i < 4 ? CardinalCost : DiagonalCost;
					int nd = best + step;
					if (nd > maxDist)
					{
						continue;
					}
					// No squeezing diagonally between two blockers (ComputeGlowGridsJob.cs:110-136).
					if ((i == 4 && blockers[0] && blockers[1]) || (i == 5 && blockers[1] && blockers[2]) || (i == 6 && blockers[2] && blockers[3]) || (i == 7 && blockers[0] && blockers[3]))
					{
						continue;
					}
					int old;
					if (!dist.TryGetValue(c, out old) || nd < old)
					{
						dist[c] = nd;
					}
				}
			}
			return result;
		}

		private static string Xz(IntVec3 c)
		{
			return c.x.ToString(CultureInfo.InvariantCulture) + ", " + c.z.ToString(CultureInfo.InvariantCulture);
		}
	}
}
