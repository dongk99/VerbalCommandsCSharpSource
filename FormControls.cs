using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Brief 23, wave 2: building controls (contract 3b). Each change is made the way the gizmo makes it:
	//   switch on/off, vent open/close  CompFlickable: the gizmo's toggle action sets the WANTED state (a private
	//       field) and the flick designation, and a colonist walks over to flick it (CompFlickable.CompGetGizmosExtra,
	//       FlickUtility.UpdateFlickDesignation). The toggle is found among the comp's own gizmos and invoked.
	//   temperature  CompTempControl.TargetTemperature (stored in Celsius; the player speaks in Prefs.TemperatureMode,
	//       GenTemperature.CelsiusTo is the game's conversion that way); clamped to the building's declared range
	//       CompProperties_TempControl.minTargetTemperature / maxTargetTemperature (the gizmo itself only clamps to
	//       -273.15..1000).
	//   refuel on/off  CompRefuelable.allowAutoRefuel (the field the gizmo toggles; only buildings whose
	//       Props.showAllowAutoRefuelToggle is set have that gizmo).
	//   fuel level  CompRefuelable.TargetFuelLevel (clamped to 0..fuelCapacity by the setter; only buildings with
	//       Props.targetFuelLevelConfigurable).
	//   door hold open / let close  Building_Door's hold-open toggle gizmo (holdOpenInt is protected).
	//   forbid / unforbid  CompForbiddable.Forbidden (public setter, as the gizmo uses).
	//   bed medical / prisoner / colonist  Building_Bed.Medical / ForOwnerType (the setters the gizmos use; both
	//       clear the bed's owners).
	//   turret hold fire / fire  Building_TurretGun's hold-fire toggle gizmo (holdFire is private).
	public static partial class FormPlanner
	{
		private static PlanReply DispatchControls(FormContext c)
		{
			switch (c.form)
			{
				case "switch on":
				case "switch off":
					return PlanFlick(c, c.form == "switch on", false);
				case "vent open":
				case "vent close":
					return PlanFlick(c, c.form == "vent open", true);
				case "temperature":
					return PlanTemperature(c);
				case "refuel on":
				case "refuel off":
					return PlanRefuel(c, c.form == "refuel on");
				case "fuel level":
					return PlanFuelLevel(c);
				case "door hold open":
				case "door let close":
					return PlanDoorHold(c, c.form == "door hold open");
				case "forbid":
				case "unforbid":
					return PlanForbid(c, c.form == "forbid");
				case "bed medical":
				case "bed prisoner":
				case "bed colonist":
					return PlanBedType(c);
				case "turret hold fire":
				case "turret fire":
					return PlanTurret(c, c.form == "turret hold fire");
			}
			return null;
		}

		// ---- finding the buildings -------------------------------------------------------------------------

		// TARGETS (building names) inside ROOM when given, else on the whole map. A name with no match is refused.
		private static List<Building> ResolveTargets(FormContext c, Func<Building, bool> fits, string kindWord)
		{
			// "those": the buildings an earlier building-control sentence of this order changed.
			if (c.req.thoseObjects != null)
			{
				List<Building> earlier = c.req.thoseObjects.OfType<Building>().Where((Building b) => b.Spawned && fits(b)).ToList();
				if (earlier.Count == 0)
				{
					throw new Fault("None of the buildings from the earlier sentence can be changed that way.");
				}
				return earlier;
			}
			List<string> names = c.TextList("TARGETS");
			if (names.Count == 0)
			{
				throw new Fault("The order did not say which " + kindWord + ".");
			}
			// ROOM may be a room name or the game's room ID ("room412"; user, 2026-10-25, for forbid / unforbid).
			List<Room> rooms = c.Has("ROOM") ? RoomsByNameOrId(c.map, c.Text("ROOM")) : null;
			// "at SPOT" (user 2026-10-04: one power switch out of several cuts one grid): only the building on that cell.
			bool atSpot = c.Has("SPOT");
			IntVec3 spot = IntVec3.Invalid;
			if (atSpot)
			{
				string why;
				if (!AgentPlace.TryCell(c.map, c.Text("SPOT"), out spot, out why))
				{
					throw new Fault(string.IsNullOrEmpty(why) ? "I could not find the spot '" + c.Text("SPOT") + "'." : why);
				}
			}
			List<Building> result = new List<Building>();
			foreach (string name in names)
			{
				string n = Norm(name);
				List<Building> found = new List<Building>();
				// A building's game ID (Thing.ThingID, "door12345"; user, 2026-10-25: "if its buildings, print their id so its
				// easier for models to unforbid/forbid them"): that one building.
				string trimmed = name.Trim();
				if (IsGameId(trimmed))
				{
					Building byId = c.map.listerBuildings.allBuildingsColonist.FirstOrDefault((Building b) => b.Spawned
						&& b.Faction == Faction.OfPlayer && fits(b) && string.Equals(b.ThingID, trimmed, StringComparison.OrdinalIgnoreCase));
					if (byId == null)
					{
						throw new Fault("There is no building with the ID " + trimmed + " on the map that can be changed that way.");
					}
					if (!result.Contains(byId))
					{
						result.Add(byId);
					}
					continue;
				}
				// A door as the "doors" vocabulary line writes it: "storeroom to kitchen".
				int split = name.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
				// Doors that lead outside (user, 2026-10-25): "bedroom to outdoors" (that room's), "doors to outdoors" (every
				// one; "in ROOM" narrows it to that room's).
				if (split > 0 && IsOutdoorsWord(name.Substring(split + 4)))
				{
					string side = name.Substring(0, split).Trim();
					List<Room> sideRooms = IsDoorsWord(side) ? rooms : OneOutdoorSide(c, side, (string id) =>
						(c.form == "unforbid" ? "unforbid " : "forbid ") + id + " to outdoors" + (c.Has("ROOM") ? " in " + c.Text("ROOM") : "") + ".");
					foreach (Building_Door door in DoorsToOutdoors(c.map, sideRooms))
					{
						if (door.Faction == Faction.OfPlayer && fits(door) && (rooms == null || DoorSides(c.map, door).Any((Room r) => rooms.Contains(r))))
						{
							found.Add(door);
						}
					}
					if (found.Count == 0)
					{
						throw new Fault("There is no door from " + (IsDoorsWord(side) ? (c.Has("ROOM") ? "the " + c.Text("ROOM") : "the colony") : "the " + side) + " to the outdoors.");
					}
				}
				else if (split > 0)
				{
					List<Room> sideA = StorageIndex.FindRoomsByLabel(c.map, name.Substring(0, split).Trim());
					List<Room> sideB = StorageIndex.FindRoomsByLabel(c.map, name.Substring(split + 4).Trim());
					if (sideA.Count > 0 && sideB.Count > 0)
					{
						foreach (Building_Door door in DoorsBetween(c.map, sideA, sideB))
						{
							if (door.Faction == Faction.OfPlayer && fits(door))
							{
								found.Add(door);
							}
						}
					}
				}
				foreach (Building b in c.map.listerBuildings.allBuildingsColonist)
				{
					if (!b.Spawned || b.Faction != Faction.OfPlayer || !fits(b))
					{
						continue;
					}
					if (Norm(b.def.label) != n && Norm(BuildDefs.Label(b.def, b.Stuff)) != n)
					{
						continue;
					}
					if (rooms != null && !rooms.Contains(RoomOfThing(b)))
					{
						continue;
					}
					found.Add(b);
				}
				if (atSpot)
				{
					List<Building> all = found;
					found = all.Where((Building b) => b.OccupiedRect().Contains(spot)).ToList();
					if (found.Count == 0)
					{
						Building nearest = all.OrderBy((Building b) => b.Position.DistanceToSquared(spot)).FirstOrDefault();
						throw new Fault("There is no " + name + " at " + AgentPlace.Describe(c.map, spot)
							+ (nearest != null ? "; the nearest one is at " + AgentPlace.Describe(c.map, nearest.Position) : "") + ".");
					}
				}
				if (found.Count == 0)
				{
					throw new Fault("There is no " + name + (c.Has("ROOM") ? " in the " + c.Text("ROOM") : " on the map") + " that can be changed that way.");
				}
				foreach (Building b in found)
				{
					if (!result.Contains(b))
					{
						result.Add(b);
					}
				}
			}
			return result;
		}

		// A game ID is one word: letters (the defName) then its number ("door12345", "Bed4567"), as Thing.ThingID writes it.
		private static bool IsGameId(string text)
		{
			return System.Text.RegularExpressions.Regex.IsMatch(text ?? "", "^[A-Za-z_]+[0-9]+$");
		}

		// The ROOM of "ROOM to outdoors": a room name or ID. A name that several rooms have changes nothing (user, 2026-10-25:
		// "If it detects more than 1 room that can match the order, then list all of the roomID for the agent with [error]
		// tag instead, with proper sentence order."): every room ID, and the agent's own sentence for each.
		private static List<Room> OneOutdoorSide(FormContext c, string side, Func<string, string> sentenceFor)
		{
			List<Room> rooms = RoomsByNameOrId(c.map, side);
			if (rooms.Count > 1)
			{
				throw new Fault("[error] " + rooms.Count + " rooms are named " + side + ": "
					+ string.Join(", ", rooms.Select((Room r) => "Room" + r.ID).ToArray()) + ". Say which one: "
					+ string.Join(" / ", rooms.Select((Room r) => sentenceFor("room" + r.ID)).ToArray()));
			}
			return rooms;
		}

		private static bool IsOutdoorsWord(string text)
		{
			string t = (text ?? "").Trim();
			return string.Equals(t, "outdoors", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "the outdoors", StringComparison.OrdinalIgnoreCase);
		}

		private static bool IsDoorsWord(string text)
		{
			string t = (text ?? "").Trim();
			return string.Equals(t, "doors", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "door", StringComparison.OrdinalIgnoreCase);
		}

		// "standing lamp (3)" for several of one kind.
		private static string DescribeBuildings(List<Building> buildings)
		{
			List<string> parts = new List<string>();
			foreach (IGrouping<string, Building> group in buildings.GroupBy((Building b) => b.def.label))
			{
				parts.Add(group.Count() > 1 ? group.Key + " (" + group.Count() + ")" : group.Key);
			}
			return JoinNames(parts);
		}

		private static string WhereText(FormContext c)
		{
			return c.Has("ROOM") ? " in the " + c.Text("ROOM") : "";
		}

		private static Plan BuildingPlan(string name, List<Building> buildings, string verb, Action<Building> change)
		{
			Plan plan = NewPlan(name);
			foreach (Building b in buildings)
			{
				Write write = new Write();
				write.description = verb + " " + b.def.label;
				Building capturedBuilding = b;
				write.apply = delegate
				{
					if (!capturedBuilding.Spawned)
					{
						throw new Fault("The " + capturedBuilding.def.label + " is gone.");
					}
					change(capturedBuilding);
				};
				plan.writes.Add(write);
			}
			return plan;
		}

		// A Command_Toggle among the gizmos, found by its label (a translated key), and set to the wanted state.
		private static void SetByToggleGizmo(IEnumerable<Gizmo> gizmos, string labelKey, bool wanted, string what)
		{
			string label = labelKey != null ? labelKey.Translate().ToString() : null;
			foreach (Gizmo g in gizmos)
			{
				Command_Toggle toggle = g as Command_Toggle;
				if (toggle == null || toggle.isActive == null || toggle.toggleAction == null)
				{
					continue;
				}
				if (label != null && toggle.defaultLabel != label)
				{
					continue;
				}
				if (toggle.isActive() != wanted)
				{
					toggle.toggleAction();
				}
				return;
			}
			throw new Fault(what + " has no such switch to change.");
		}

		// ---- switch, vent ---------------------------------------------------------------------------------------

		private static PlanReply PlanFlick(FormContext c, bool on, bool vents)
		{
			List<Building> buildings = ResolveTargets(c,
				(Building b) => b.GetComp<CompFlickable>() != null && (b is Building_Vent) == vents,
				vents ? "vents" : "buildings with a power switch");
			Plan plan = BuildingPlan(c.form, buildings, vents ? (on ? "open" : "close") : (on ? "switch on" : "switch off"), delegate(Building b)
			{
				SetByToggleGizmo(b.GetComp<CompFlickable>().CompGetGizmosExtra(), null, on, "The " + b.def.label);
			});
			string what = DescribeBuildings(buildings) + WhereText(c);
			string line = (vents ? (on ? "Open " : "Close ") : (on ? "Switch on " : "Switch off ")) + what
				+ ". A colonist walks over to do it; it does not happen at once.";
			return Reply(plan, line, null, buildings.Cast<object>());
		}

		// ---- temperature ---------------------------------------------------------------------------------------

		private static float DisplayToCelsius(float value)
		{
			switch (Prefs.TemperatureMode)
			{
				case TemperatureDisplayMode.Fahrenheit:
					return (value - 32f) / 1.8f;
				case TemperatureDisplayMode.Kelvin:
					return value - 273.15f;
				default:
					return value;
			}
		}

		private static PlanReply PlanTemperature(FormContext c)
		{
			double? degrees = c.Number("DEGREES");
			if (degrees == null)
			{
				throw new Fault("The order did not give a temperature.");
			}
			float celsius = DisplayToCelsius((float)degrees.Value);
			// The limit the game enforces on what a player can set: CompTempControl.InterfaceChangeTargetTemperature
			// clamps to -273.15..1000 Celsius. CompProperties_TempControl.min/maxTargetTemperature are not used by
			// any game code. A value outside is refused, not clamped.
			const float LowestCelsius = -273.15f;
			const float HighestCelsius = 1000f;
			if (celsius < LowestCelsius || celsius > HighestCelsius)
			{
				throw new Fault("That temperature can't be set. The game accepts " + LowestCelsius.ToStringTemperature("F0") + " to " +
					HighestCelsius.ToStringTemperature("F0") + ". Nothing was changed.");
			}
			List<Building> buildings = ResolveTargets(c, (Building b) => b.GetComp<CompTempControl>() != null, "coolers or heaters");
			Plan plan = NewPlan("temperature");
			foreach (Building b in buildings)
			{
				float value = celsius;
				Write write = new Write();
				write.description = b.def.label + " to " + value.ToStringTemperature("F0");
				Building capturedBuilding = b;
				float capturedValue = value;
				write.apply = delegate
				{
					CompTempControl live = capturedBuilding.GetComp<CompTempControl>();
					if (live == null || !capturedBuilding.Spawned)
					{
						throw new Fault("The " + capturedBuilding.def.label + " is gone.");
					}
					live.TargetTemperature = capturedValue;
				};
				plan.writes.Add(write);
			}
			string line = "Set " + DescribeBuildings(buildings) + WhereText(c) + " to " + celsius.ToStringTemperature("F0") + ".";
			return Reply(plan, line, null, buildings.Cast<object>());
		}

		// ---- refuel, fuel level -------------------------------------------------------------------------------------

		private static PlanReply PlanRefuel(FormContext c, bool on)
		{
			List<Building> buildings = ResolveTargets(c,
				(Building b) => b.GetComp<CompRefuelable>() != null && b.GetComp<CompRefuelable>().Props.showAllowAutoRefuelToggle,
				"buildings that refuel");
			Plan plan = BuildingPlan("refuel", buildings, on ? "allow refuelling of" : "stop refuelling", delegate(Building b)
			{
				b.GetComp<CompRefuelable>().allowAutoRefuel = on;
			});
			string line = (on ? "Let " : "Stop ") + DescribeBuildings(buildings) + WhereText(c) + (on ? " be refuelled automatically." : " being refuelled automatically.");
			return Reply(plan, line, null, buildings.Cast<object>());
		}

		private static PlanReply PlanFuelLevel(FormContext c)
		{
			double? n = c.Number("N");
			if (n == null || n < 0)
			{
				throw new Fault("The fuel level must be a number, 0 or more.");
			}
			List<Building> buildings = ResolveTargets(c,
				(Building b) => b.GetComp<CompRefuelable>() != null && b.GetComp<CompRefuelable>().Props.targetFuelLevelConfigurable,
				"buildings with a fuel level to set");
			Plan plan = NewPlan("fuel level");
			foreach (Building b in buildings)
			{
				CompRefuelable comp = b.GetComp<CompRefuelable>();
				float capacity = comp.Props.fuelCapacity;
				float value = Mathf.Clamp((float)n.Value, 0f, capacity);
				if (value != (float)n.Value)
				{
					plan.notes.Add("The " + b.def.label + " holds at most " + capacity.ToStringDecimalIfSmall() + ", so that is used.");
				}
				Write write = new Write();
				write.description = b.def.label + " target fuel " + value.ToStringDecimalIfSmall();
				Building capturedBuilding = b;
				float capturedValue = value;
				write.apply = delegate
				{
					CompRefuelable live = capturedBuilding.GetComp<CompRefuelable>();
					if (live == null || !capturedBuilding.Spawned)
					{
						throw new Fault("The " + capturedBuilding.def.label + " is gone.");
					}
					live.TargetFuelLevel = capturedValue;
				};
				plan.writes.Add(write);
			}
			string line = "Set the target fuel level of " + DescribeBuildings(buildings) + WhereText(c) + " to " + VerbalCommands.RouterScript.Value.FormatNumber(n.Value) + ".";
			return Reply(plan, line, null, buildings.Cast<object>());
		}

		// ---- doors ---------------------------------------------------------------------------------------------------

		private static PlanReply PlanDoorHold(FormContext c, bool hold)
		{
			string text = c.Text("DOOR");
			if (text == null)
			{
				throw new Fault("The order did not say which door.");
			}
			List<Building_Door> doors;
			int split = text.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
			// "bedroom to outdoors" (the doors line, user 2026-10-25): that room's doors that lead outside.
			if (split > 0 && IsOutdoorsWord(text.Substring(split + 4)))
			{
				doors = DoorsToOutdoors(c.map, OneOutdoorSide(c, text.Substring(0, split).Trim(), (string id) =>
					hold ? "hold door " + id + " to outdoors open." : "let door " + id + " to outdoors close."));
				if (doors.Count == 0)
				{
					throw new Fault("There is no door from the " + text.Substring(0, split).Trim() + " to the outdoors.");
				}
			}
			else if (split > 0)
			{
				List<Room> a = ResolveRooms(c.map, text.Substring(0, split).Trim());
				List<Room> b = ResolveRooms(c.map, text.Substring(split + 4).Trim());
				doors = DoorsBetween(c.map, a, b);
				if (doors.Count == 0)
				{
					throw new Fault("There is no door between those two rooms ('" + text + "').");
				}
			}
			else
			{
				List<Room> rooms = ResolveRooms(c.map, text);
				doors = c.map.listerBuildings.allBuildingsColonist.OfType<Building_Door>()
					.Where((Building_Door d) => d.Spawned && DoorSides(c.map, d).Any((Room r) => rooms.Contains(r))).ToList();
				if (doors.Count == 0)
				{
					throw new Fault("There is no door to the " + text + ".");
				}
			}
			Plan plan = NewPlan(c.form);
			foreach (Building_Door door in doors)
			{
				Write write = new Write();
				write.description = (hold ? "hold open" : "let close") + " the door";
				Building_Door capturedDoor = door;
				write.apply = delegate
				{
					SetByToggleGizmo(capturedDoor.GetGizmos(), "CommandToggleDoorHoldOpen", hold, "That door");
				};
				plan.writes.Add(write);
			}
			string line = (hold ? "Hold open " : "Let close ") + (doors.Count > 1 ? doors.Count + " doors " : "the door ") + "'" + text + "'.";
			return Reply(plan, line, null, null);
		}

		// ---- forbid -------------------------------------------------------------------------------------------------

		private static PlanReply PlanForbid(FormContext c, bool forbid)
		{
			List<Building> buildings = ResolveTargets(c, (Building b) => b.GetComp<CompForbiddable>() != null, "buildings or doors");
			Plan plan = BuildingPlan(c.form, buildings, forbid ? "forbid" : "unforbid", delegate(Building b)
			{
				b.GetComp<CompForbiddable>().Forbidden = forbid;
			});
			string line = (forbid ? "Forbid " : "Allow ") + DescribeBuildings(buildings) + WhereText(c) + ".";
			return Reply(plan, line, null, buildings.Cast<object>());
		}

		// ---- beds ---------------------------------------------------------------------------------------------------

		private static PlanReply PlanBedType(FormContext c)
		{
			string roomLabel = c.Text("ROOM");
			List<Room> rooms = ResolveRooms(c.map, roomLabel);
			List<Building_Bed> beds = new List<Building_Bed>();
			foreach (Room room in rooms)
			{
				foreach (Building_Bed bed in room.ContainedBeds)
				{
					if (bed.Faction == Faction.OfPlayer && bed.def.building.bed_humanlike && !bed.ForHumanBabies && !beds.Contains(bed))
					{
						beds.Add(bed);
					}
				}
			}
			if (beds.Count == 0)
			{
				throw new Fault("There is no bed for colonists in the " + roomLabel + ".");
			}
			double? count = c.Number("COUNT");
			if (count != null)
			{
				if (count < 1 || count != Math.Floor(count.Value))
				{
					throw new Fault("The number of beds must be a whole number, 1 or more.");
				}
				if (count > beds.Count)
				{
					throw new Fault("The " + roomLabel + " has " + beds.Count + (beds.Count == 1 ? " bed" : " beds") + ", not " + (int)count.Value + ".");
				}
				// Beds nobody owns first: changing a bed's type takes its owners away.
				beds = beds.OrderBy((Building_Bed b) => b.OwnersForReading.Count).Take((int)count.Value).ToList();
			}

			Plan plan = NewPlan(c.form);
			string kind = c.form.Substring("bed ".Length);
			// A prison cell belongs to the room, not to a bed (user, 2026-10-14: a prisoner sleeping spot stood, yet the
			// arrest found no bed; outbox 02465200_06133 "the game refused it"). A prisoner bed counts only inside a prison
			// cell (RestUtility.CanUseBedNow, RestUtility.cs:125), and only Room.Notify_RoomShapeChanged sets that
			// (Verse\Room.cs:686-729; setting ForOwnerType just marks the role dirty, Room.cs:673-676). The game's own
			// toggle (Building_Bed.SetBedOwnerTypeByInterface, Building_Bed.cs:395-473) changes every bed of the room to
			// prisoner, or every prisoner bed of it back to colonist, then refreshes each room; so does this, the refresh
			// after the last bed (a refresh between them would turn a changed bed back while the room is still a cell).
			if (kind != "medical")
			{
				List<Building_Bed> rest = new List<Building_Bed>();
				foreach (Room room in rooms)
				{
					foreach (Building_Bed bed in room.ContainedBeds)
					{
						if (bed.Faction == Faction.OfPlayer && bed.def.building.bed_humanlike && !bed.ForHumanBabies && !beds.Contains(bed) && !rest.Contains(bed)
							&& (kind == "prisoner" ? !bed.ForPrisoners : bed.ForPrisoners))
						{
							rest.Add(bed);
						}
					}
				}
				if (rest.Count > 0)
				{
					beds.AddRange(rest);
					// Settings gate (user, 2026-10-26): Build advice tips/notes. The extra beds still change.
					if (VerbalCommandsMod.BuildAdviceOn)
					{
						plan.notes.Add(kind == "prisoner"
							? "A prison cell is the whole room: the game makes every bed in it a prisoner bed, so " + rest.Count + " more " + (rest.Count == 1 ? "bed" : "beds") + " in the " + roomLabel + " change too."
							: "A room stays a prison cell while any prisoner bed is in it, so " + rest.Count + " more prisoner " + (rest.Count == 1 ? "bed" : "beds") + " in the " + roomLabel + " change too.");
					}
				}
			}
			List<Building_Bed> changing = new List<Building_Bed>();
			foreach (Building_Bed bed in beds)
			{
				if (kind == "medical" && !bed.def.building.bed_canBeMedical)
				{
					// Settings gate (user, 2026-10-26): Build advice tips/notes. The bed is still left out.
					if (VerbalCommandsMod.BuildAdviceOn)
					{
						plan.notes.Add("A " + bed.def.label + " can't be a medical bed, so it was left out.");
					}
					continue;
				}
				if (kind == "prisoner" && !Building_Bed.RoomCanBePrisonCell(bed.GetRoom()))
				{
					// Settings gate (user, 2026-10-26): Build advice tips/notes. The bed is still left out.
					if (VerbalCommandsMod.BuildAdviceOn)
					{
						plan.notes.Add("A bed in the " + roomLabel + " can't be for prisoners: the room can't be a prison cell (it needs walls and a door, and not be outdoors).");
					}
					continue;
				}
				changing.Add(bed);
				Write write = new Write();
				write.description = "make a " + bed.def.label + " " + (kind == "medical" ? "a medical bed" : "a " + kind + " bed");
				Building_Bed capturedBed = bed;
				write.apply = delegate
				{
					if (!capturedBed.Spawned)
					{
						throw new Fault("That bed is gone.");
					}
					if (kind == "medical")
					{
						capturedBed.Medical = true;
					}
					else if (kind == "prisoner")
					{
						capturedBed.ForOwnerType = BedOwnerType.Prisoner;
					}
					else
					{
						capturedBed.ForOwnerType = BedOwnerType.Colonist;
						capturedBed.Medical = false;
					}
				};
				plan.writes.Add(write);
			}
			if (changing.Count == 0)
			{
				throw new Fault("No bed in the " + roomLabel + " could be changed that way.");
			}
			if (kind != "medical")
			{
				// The game's refresh (Building_Bed.cs:466-473): districts, then rooms. Sets Room.IsPrisonCell.
				List<Building_Bed> refreshed = changing.ToList();
				Write refresh = new Write();
				refresh.description = "update the " + roomLabel + " as " + (kind == "prisoner" ? "a prison cell" : "no prison cell");
				refresh.apply = delegate
				{
					List<District> districts = new List<District>();
					List<Room> changedRooms = new List<Room>();
					foreach (Building_Bed bed in refreshed)
					{
						District district = bed.Spawned ? bed.GetDistrict() : null;
						if (district == null)
						{
							continue;
						}
						if (!districts.Contains(district))
						{
							districts.Add(district);
						}
						if (district.Room != null && !changedRooms.Contains(district.Room))
						{
							changedRooms.Add(district.Room);
						}
					}
					foreach (District district in districts)
					{
						district.Notify_RoomShapeOrContainedBedsChanged();
					}
					foreach (Room room in changedRooms)
					{
						room.Notify_RoomShapeChanged();
					}
				};
				plan.writes.Add(refresh);
			}
			// Settings gate (user, 2026-10-26): Build advice tips/notes.
			if (VerbalCommandsMod.BuildAdviceOn && changing.Any((Building_Bed b) => b.OwnersForReading.Count > 0))
			{
				plan.notes.Add("A changed bed loses the colonists it was assigned to.");
			}
			string line = "Make " + (count != null || changing.Count < beds.Count ? changing.Count + " of the " : "every ") + (changing.Count == 1 && count == null ? "bed" : "beds")
				+ " in the " + roomLabel + " " + (kind == "medical" ? "medical beds" : kind + " beds") + ".";
			return Reply(plan, line, null, null);
		}

		// ---- turrets ------------------------------------------------------------------------------------------------

		private static PlanReply PlanTurret(FormContext c, bool holdFire)
		{
			List<Building> buildings = ResolveTargets(c, (Building b) => b is Building_TurretGun, "turrets");
			Plan plan = BuildingPlan(c.form, buildings, holdFire ? "hold fire of" : "open fire of", delegate(Building b)
			{
				SetByToggleGizmo(b.GetGizmos(), "CommandHoldFire", holdFire, "The " + b.def.label);
			});
			string line = (holdFire ? "Make " + DescribeBuildings(buildings) + WhereText(c) + " hold fire." : "Let " + DescribeBuildings(buildings) + WhereText(c) + " fire.");
			return Reply(plan, line, null, buildings.Cast<object>());
		}
	}
}
