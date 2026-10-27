using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Work spot / bench placement reminder for build plans (user, 2026-10-14: "make it so that whenever spots are being
	// built, add a reminder (needs confirmation from agent) that building spots/benches in wrong places (existing benches,
	// outdoors, etc) have work speed penalty. this is a REMINDER that fires irregardless of context length gap. read wiki
	// for details."), the sibling of AgentWindCheck / AgentLightCheck. A NOTE only, never a Fault; the note makes the plan
	// wait for confirm (Plan.askConfirm), even under raw apply.
	//   Unlike the tips (TipInjector.cs) it has no token gap: every plan that builds or places a work table (crafting
	//   spot, butcher spot, campfire, benches, stoves, smithies ...) or a research bench gets it. The note always carries
	//   the reminder, then what applies at each placed cell as the map stands now.
	//
	// Game facts relied on (decompiled tree sources\RimWorldDecompiled\, line numbers of that tree; Data = the game's
	// Data folder):
	//   Work tables: WorkTableWorkSpeedFactor = unpowered, room role, temperature, outdoors
	//                                                       Data\Core\Defs\Stats\Stats_Building_Special.xml:146-160
	//     Outdoors x0.8 when the room is PsychologicallyOutdoors (300+ unroofed cells, or on the map edge and half
	//       unroofed)                                       RimWorld\StatPart_WorkTableOutdoors.cs:7, :31-42; Verse\Room.cs:378-392
	//     Below 9C or above 35C x0.7                        RimWorld\StatPart_WorkTableTemperature.cs:7-11, :39-55
	//     In an indoor room whose role is not the bench's own x workTableNotInRoomRoleFactor (0.8 on the Core benches;
	//       a room's role follows the benches already in it)  RimWorld\StatPart_WorkTableRoomRole.cs:39-55;
	//                                                       Data\Core\Defs\ThingDefs_Buildings\Buildings_Production.xml
	//     Unpowered x unpoweredWorkTableWorkSpeedFactor (0.5 electric tailor bench; 0 = no slow mode, it stops)
	//                                                       RimWorld\StatPart_WorkTableUnpowered.cs:25-37;
	//                                                       RimWorld\BuildingProperties.cs:312-316; Buildings_Production.xml:406
	//   Research benches: ResearchSpeedFactor = outdoors x0.75, room role, temperature, room cleanliness
	//                                                       Stats_Building_Special.xml:87-100
	//     outdoors there = room OutdoorsForWork (over 100 or over 25% unroofed) or its own cell unroofed
	//                                                       RimWorld\StatPart_Outdoors.cs:35-54; Verse\Room.cs:394-404
	//   Darkness: the worker's global work speed x0.80 at no light up to x1.00 at 30% light
	//                                                       Data\Core\Defs\Stats\Stats_Pawns_WorkGeneral.xml:13-21;
	//                                                       Verse\GlowGrid.cs:244-263 (GroundGlowAt)
	//   A connector finds a transmitter within range (conduit)  RimWorld\PowerConnectionMaker.cs:101
	//   rimworldwiki Basics: "When outdoors, items will deteriorate, and workshops like the hand tailor bench will
	//       functions at 90% speed" (the wiki's older number; the code says 80%); Simple_research_bench: "A x0.7 work
	//       multiplier for being in "Bad Temperature" - below 10 C (50 F) or above 35 C"; Colony_Building_Guide: a
	//       butcher spot in the freezer is viable since "the work speed penalty is not very impactful", "do not put your
	//       kitchen stove in this room".
	internal static class AgentWorkspotCheck
	{
		private const float DarkGlow = 0.3f;           // Stats_Pawns_WorkGeneral.xml:17-18
		private const float DarkFactor = 0.8f;         // Stats_Pawns_WorkGeneral.xml:17
		private const int MaxNamed = 6;

		private const string Reminder = "WORKSPOT: reminder, where a work spot or bench stands sets how fast work goes there (game factors): "
			+ "in the open (no closed room) x80%, research benches x75% (also when their own cell is unroofed); "
			+ "below 9C or above 35C x70%; "
			+ "in a room whose role is set by other benches (a workbench in the lab, a stove outside the kitchen, a research bench in the workshop) x80%; "
			+ "a powered bench without power slows (electric tailor bench x50%) or stops; "
			+ "a worker in the dark down to x80%.";

		internal static bool IsWorkspot(ThingDef def)
		{
			return def != null && def.building != null && def.thingClass != null
				&& (def.IsWorkTable || typeof(Building_ResearchBench).IsAssignableFrom(def.thingClass));
		}

		internal static bool IsResearchBench(ThingDef def)
		{
			return def.thingClass != null && typeof(Building_ResearchBench).IsAssignableFrom(def.thingClass);
		}

		internal static string LabelOf(ThingDef def)
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
				List<AgentWindCheck.Placed> spots = placed.Where((AgentWindCheck.Placed p) => IsWorkspot(p.def)).ToList();
				if (spots.Count == 0)
				{
					return;
				}

				List<string> parts = new List<string>();
				foreach (AgentWindCheck.Placed p in spots.Take(MaxNamed))
				{
					List<string> found = Found(map, p.def, p.cell);
					parts.Add("the " + LabelOf(p.def) + " at " + Xz(p.cell) + ": " + (found.Count == 0 ? "none of these" : string.Join(", ", found.ToArray())));
				}
				string text = Reminder + " As the map stands now, " + string.Join("; ", parts.ToArray());
				if (spots.Count > MaxNamed)
				{
					text += "; and " + (spots.Count - MaxNamed) + " more not checked";
				}
				text += ".";
				// Walls and doors of this same order do not make a room until they stand: the cells read as they are now.
				if (placed.Any((AgentWindCheck.Placed p) => p.def != null && (p.def.IsWall || p.def.IsDoor)))
				{
					text += " This order's own walls and doors are not counted: once they and a roof stand, the room and its role change.";
				}
				text += " Say apply to go ahead, or cancel and pick a better place.";
				plan.notes.Add(text);
				plan.askConfirm = true;
				plan.confirmBy = "Work spot check";   // the harness log (HarnessLog.cs, H4)
			}
			catch (Exception ex)
			{
				// A check that only informs must never stop a plan.
				Log.Warning("[VerbalCommands] workspot check skipped: " + ex.Message);
			}
		}

		// ---- what applies at one cell ---------------------------------------------------------------------

		// One work speed penalty: what causes it and its factor (0 = the bench does not work at all).
		internal struct Penalty
		{
			public string what;
			public float factor;
			public bool power;   // a power penalty (not where the bench stands, so the 6-hour scan leaves it out)
			public bool dark;    // the darkness penalty (the 6-hour scan has its own light line for it instead)
			public Penalty(string what, float factor, bool power, bool dark = false)
			{
				this.what = what;
				this.factor = factor;
				this.power = power;
				this.dark = dark;
			}
		}

		private static List<string> Found(Map map, ThingDef def, IntVec3 cell)
		{
			return Penalties(map, def, cell, null).Select((Penalty p) => p.what + " (" + (p.power && p.factor <= 0f ? "it will not work until powered" : "x" + Pct(p.factor) + (p.power ? " until powered" : "")) + ")").ToList();
		}

		// The penalties of DEF at CELL. BUILT: the spawned building itself (AgentReminderScan), which uses the game's
		// tests on the thing (room role as it is, power as it is now); null for a placement still to be made.
		internal static List<Penalty> Penalties(Map map, ThingDef def, IntVec3 cell, Thing built)
		{
			List<Penalty> found = new List<Penalty>();
			if (map == null || def == null || def.building == null || !cell.InBounds(map))
			{
				return found;
			}
			bool roofed = map.roofGrid.Roofed(cell);
			if (IsResearchBench(def))
			{
				Room room = cell.GetRoom(map);
				if (room != null && (room.OutdoorsForWork || !roofed))
				{
					found.Add(new Penalty(!roofed ? "unroofed" : "in the open", 0.75f, false));   // Stats_Building_Special.xml:88-91
				}
			}
			else if (StatPart_WorkTableOutdoors.Applies(def, map, cell))
			{
				found.Add(new Penalty("in the open", StatPart_WorkTableOutdoors.WorkRateFactor, false));
			}
			if (StatPart_WorkTableTemperature.Applies(def, map, cell))
			{
				float temp = GenTemperature.GetTemperatureForCell(cell, map);
				found.Add(new Penalty(temp.ToString("0", CultureInfo.InvariantCulture) + "C now", StatPart_WorkTableTemperature.WorkRateFactor, false));
			}
			if (built != null ? StatPart_WorkTableRoomRole.Applies(built) : StatPart_WorkTableRoomRole.WouldApplyToBuildingIfPlaced(def, map, cell))
			{
				Room room = cell.GetRoom(map);
				string role = room != null && room.Role != null && room.Role != RoomRoleDefOf.None ? "a " + room.Role.label : "another room";
				found.Add(new Penalty("in " + role + ", not a " + def.building.workTableRoomRole.label, def.building.workTableNotInRoomRoleFactor, false));
			}
			if (built != null)
			{
				if (StatPart_WorkTableUnpowered.Applies(built))
				{
					found.Add(new Penalty("no power", def.building.unpoweredWorkTableWorkSpeedFactor, true));
				}
			}
			else if (def.ConnectToPower && PowerConnectionMaker.BestTransmitterForConnector(cell, map, null) == null)
			{
				found.Add(new Penalty("no conduit within reach", def.building.unpoweredWorkTableWorkSpeedFactor, true));
			}
			// Dark: only under a roof, where the sky does not light it by day.
			if (roofed)
			{
				float glow = map.glowGrid.GroundGlowAt(cell);
				if (glow < DarkGlow)
				{
					float factor = DarkFactor + (1f - DarkFactor) * (glow / DarkGlow);
					found.Add(new Penalty("dark, light " + Pct(glow) + " (the worker)", factor, false, true));
				}
			}
			return found;
		}

		internal static string Pct(float f)
		{
			return ((int)Math.Round(f * 100f)).ToString(CultureInfo.InvariantCulture) + "%";
		}

		internal static string Xz(IntVec3 c)
		{
			return c.x.ToString(CultureInfo.InvariantCulture) + ", " + c.z.ToString(CultureInfo.InvariantCulture);
		}
	}
}
