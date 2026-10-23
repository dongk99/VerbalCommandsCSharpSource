using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Cooler side check for build plans (user, 2026-10-21: "1. sees cooler placement. 2. tells where coldside is facing
	// (the room, or outside, or another room). 3. asks agent for CONFIRMATION, or needs it flipped."; after an agent
	// built a cooler facing west and read it as cooling west, so it heated the colonists' room), the sibling of
	// AgentDoorCheck: a NOTE, never a Fault; it makes the plan wait for confirm (Plan.askConfirm), even under raw apply.
	// Fires on the plan only (user, 2026-10-21: "on the plan").
	//
	// Wording (user, 2026-10-21, picked): "Cooler at X, Z facing DIR: cold side blows into PLACE, hot side into PLACE.
	// Confirm, or flip it?" PLACE (user, 2026-10-21, picked): "ROLE at X, Z" for a room, "outside at X, Z",
	// "a wall at X, Z (the cooler won't run)", "unseen at X, Z" under fog. Rooms are read as built now: walls that are
	// only blueprints make no room yet (as in AgentDoorCheck).
	//
	// Game facts relied on (decompiled tree sources\RimWorldDecompiled\):
	//   cold side Position + South.RotatedBy(Rotation) (its room is cooled), hot side Position + North.RotatedBy(Rotation)
	//       (heat pushed there); either side Impassable and the cooler does nothing   RimWorld\Building_Cooler.cs:18-21, :40-41
	//   RotatedBy(Rot4): East (z, -x), South (-x, -z), West (-z, x)                     Verse\IntVec3Utility.cs:35-45
	internal static class AgentCoolerCheck
	{
		// ---- entry points ---------------------------------------------------------------------------------

		internal static void Note(Plan plan, Map map, ThingDef def, IntVec3 cell, Rot4 rot)
		{
			Note(plan, map, new List<AgentWindCheck.Placed> { new AgentWindCheck.Placed(def, cell, rot) });
		}

		internal static void Note(Plan plan, Map map, ThingDef def, IEnumerable<IntVec3> cells, Rot4 rot)
		{
			Note(plan, map, cells.Select((IntVec3 c) => new AgentWindCheck.Placed(def, c, rot)).ToList());
		}

		internal static void Note(Plan plan, Map map, List<AgentWindCheck.Placed> placed)
		{
			try
			{
				if (plan == null || map == null || placed == null)
				{
					return;
				}
				bool any = false;
				foreach (AgentWindCheck.Placed p in placed)
				{
					if (!IsCooler(p.def))
					{
						continue;
					}
					IntVec3 cold = p.cell + IntVec3.South.RotatedBy(p.rot);
					IntVec3 hot = p.cell + IntVec3.North.RotatedBy(p.rot);
					plan.notes.Add(FormPlanner.UpperFirst(Label(p.def)) + " at " + Xz(p.cell) + " facing " + Word(p.rot)
						+ ": cold side blows into " + Place(map, cold) + ", hot side into " + Place(map, hot) + ". Confirm, or flip it?");
					any = true;
				}
				if (any)
				{
					plan.askConfirm = true;
					plan.confirmBy = "Cooler check";   // the harness log (HarnessLog.cs, H4)
				}
			}
			catch (Exception ex)
			{
				// A check that only informs must never stop a plan.
				Log.Warning("[VerbalCommands] cooler check skipped: " + ex.Message);
			}
		}

		// ---- the parts ------------------------------------------------------------------------------------

		private static bool IsCooler(ThingDef def)
		{
			return def != null && def.thingClass != null && typeof(Building_Cooler).IsAssignableFrom(def.thingClass);
		}

		// What the side cell blows into, read as the map is now.
		private static string Place(Map map, IntVec3 c)
		{
			string at = " at " + Xz(c);
			if (!c.InBounds(map))
			{
				return "outside" + at;
			}
			if (!AgentPlace.Visible(map, c))
			{
				return "unseen" + at;
			}
			if (c.Impassable(map))
			{
				return "a wall" + at + " (the cooler won't run)";
			}
			Room room = c.GetRoom(map);
			if (room == null || !room.ProperRoom || room.UsesOutdoorTemperature)
			{
				return "outside" + at;
			}
			string name = VocabularyText.RoomLabel(room);
			return (string.IsNullOrEmpty(name) ? "room" : name) + at;
		}

		private static string Word(Rot4 rot)
		{
			switch (rot.AsInt)
			{
				case 1:
					return "east";
				case 2:
					return "south";
				case 3:
					return "west";
				default:
					return "north";
			}
		}

		private static string Label(ThingDef def)
		{
			return def.label.NullOrEmpty() ? def.defName : def.label;
		}

		private static string Xz(IntVec3 c)
		{
			return c.x.ToString(CultureInfo.InvariantCulture) + ", " + c.z.ToString(CultureInfo.InvariantCulture);
		}
	}
}
