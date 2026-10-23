using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Brief 23: the storage forms and the build forms of FormPlanner (see FormPlanner.cs).
	public static partial class FormPlanner
	{
		// ---- finding the storage a sentence means ----------------------------------------------------------

		// PLACE = "near BENCH", "in ROOM", "near BENCH in ROOM", or a direction from a door.
		private static List<StorageUnit> ResolveStorageUnits(FormContext c)
		{
			Map map = c.map;
			List<string> roomLabels = new List<string>();
			List<Room> rooms = null;
			if (c.Has("ROOM"))
			{
				rooms = ResolveRooms(map, c.Text("ROOM"));
			}
			List<StorageUnit> all = StorageIndex.CollectUnits(map);
			// Agent 06: "zone ZONE" names one stockpile zone by its game label (a zone outdoors has no room).
			if (c.Has("ZONE"))
			{
				string zoneName = c.Text("ZONE");
				List<StorageUnit> byZone = all.Where((StorageUnit u) => u.zone != null && SameName(u.zone.label, zoneName)).ToList();
				if (byZone.Count == 0)
				{
					Zone other = map.zoneManager.AllZones.FirstOrDefault((Zone z) => SameName(z.label, zoneName));
					throw new Fault(other != null
						? UpperFirst(other.label) + " is not a stockpile, so it stores nothing."
						: "There is no stockpile zone called '" + zoneName + "'.");
				}
				return byZone;
			}
			// User 2026-10-07: one storage building by its game ID or by its cell (router.txt PLACES).
			if (c.Has("STORAGE_ID"))
			{
				string id = c.Text("STORAGE_ID");
				StorageUnit byId = UnitByThingId(all, id);
				if (byId == null)
				{
					throw new Fault("There is no storage building with the ID '" + id + "' on the map.");
				}
				return new List<StorageUnit> { byId };
			}
			if (c.Has("SPOT"))
			{
				IntVec3 cell;
				string reason;
				if (!AgentPlace.TryCell(map, c.Text("SPOT"), out cell, out reason))
				{
					throw new Fault("Storage at " + c.Text("SPOT") + ": " + reason);
				}
				List<StorageUnit> atCell = all.Where((StorageUnit u) => u.zone == null
					&& u.buildings.Any((Thing b) => b.Spawned && b.OccupiedRect().Contains(cell))).ToList();
				if (atCell.Count == 0)
				{
					StorageUnit zoneThere = all.FirstOrDefault((StorageUnit u) => u.zone != null && u.cells.Contains(cell));
					throw new Fault("There is no storage building at " + cell.x + ", " + cell.z + "."
						+ (zoneThere != null ? " That cell is in " + zoneThere.label + ": say zone " + zoneThere.label + "." : ""));
				}
				return atCell;
			}
			List<StorageUnit> units;

			if (c.Has("DIRECTION"))
			{
				units = UnitsByDoor(c, rooms, all);
			}
			else if (c.Has("BENCH"))
			{
				List<Building_WorkTable> benches = ResolveBenches(map, c.Text("BENCH"), rooms, c.Text("ROOM"));
				units = new List<StorageUnit>();
				foreach (Building_WorkTable bench in benches)
				{
					Room benchRoom = RoomOfThing(bench);
					StorageUnit nearest = null;
					int best = int.MaxValue;
					foreach (StorageUnit unit in all)
					{
						if (unit.room == null || unit.room != benchRoom || unit.cells.Count == 0)
						{
							continue;
						}
						int d = unit.cells.Min((IntVec3 cell) => cell.DistanceToSquared(bench.Position));
						if (d < best)
						{
							best = d;
							nearest = unit;
						}
					}
					if (nearest != null && !units.Contains(nearest))
					{
						units.Add(nearest);
					}
				}
				if (units.Count == 0)
				{
					throw new Fault("There is no storage in the same room as the " + c.Text("BENCH") + ".");
				}
			}
			else if (rooms != null)
			{
				units = all.Where((StorageUnit u) => u.room != null && rooms.Contains(u.room)).ToList();
				if (units.Count == 0)
				{
					throw new Fault("There is no storage in the " + c.Text("ROOM") + ".");
				}
			}
			else
			{
				throw new Fault("Storage needs a room or a bench to say which one is meant.");
			}
			return units;
		}

		// The building unit whose id or one of whose buildings' ThingID (Verse\Thing.cs:392-400: defName + number) is
		// id; the router hands the ID lowercased, so the case is ignored. Linked buildings give their group's unit.
		internal static StorageUnit UnitByThingId(List<StorageUnit> all, string id)
		{
			string wanted = (id ?? "").Trim();
			foreach (StorageUnit unit in all)
			{
				if (unit.zone != null)
				{
					continue;
				}
				if (string.Equals(unit.id, wanted, StringComparison.OrdinalIgnoreCase)
					|| unit.buildings.Any((Thing b) => string.Equals(b.ThingID, wanted, StringComparison.OrdinalIgnoreCase)))
				{
					return unit;
				}
			}
			return null;
		}

		private static IntVec3 DirectionVector(string direction)
		{
			switch (direction)
			{
				case "north": return new IntVec3(0, 0, 1);
				case "south": return new IntVec3(0, 0, -1);
				case "east": return new IntVec3(1, 0, 0);
				case "west": return new IntVec3(-1, 0, 0);
				case "northeast": return new IntVec3(1, 0, 1);
				case "northwest": return new IntVec3(-1, 0, 1);
				case "southeast": return new IntVec3(1, 0, -1);
				case "southwest": return new IntVec3(-1, 0, -1);
			}
			return IntVec3.Zero;
		}

		// North is +z and east is +x (Verse\Rot4.cs). A cardinal direction means that side is the larger part of
		// the offset; a corner means both parts point that way.
		private static bool OffsetIsToward(IntVec3 offset, IntVec3 dir)
		{
			if (dir.x != 0 && dir.z != 0)
			{
				return Math.Sign(offset.x) == dir.x && Math.Sign(offset.z) == dir.z;
			}
			if (dir.z != 0)
			{
				return Math.Sign(offset.z) == dir.z && Math.Abs(offset.z) >= Math.Abs(offset.x);
			}
			return Math.Sign(offset.x) == dir.x && Math.Abs(offset.x) >= Math.Abs(offset.z);
		}

		// "storage [DISTANCE tiles] DIRECTION of door to DOOR in ROOM": the door of ROOM that leads to DOOR, and
		// the storage of ROOM on that side of it.
		private static List<StorageUnit> UnitsByDoor(FormContext c, List<Room> rooms, List<StorageUnit> all)
		{
			Map map = c.map;
			string direction = c.Text("DIRECTION");
			IntVec3 dir = DirectionVector(direction);
			if (dir == IntVec3.Zero)
			{
				throw new Fault("'" + direction + "' is not a direction.");
			}
			if (rooms == null)
			{
				throw new Fault("Storage by a door needs the room it is in.");
			}
			string leadsTo = c.Text("DOOR");
			List<Room> otherRooms = ResolveRooms(map, leadsTo);
			List<Building_Door> doors = DoorsBetween(map, rooms, otherRooms);
			if (doors.Count == 0)
			{
				throw new Fault("There is no door from the " + c.Text("ROOM") + " to the " + leadsTo + ".");
			}
			double? distance = c.Number("DISTANCE");

			List<StorageUnit> found = new List<StorageUnit>();
			foreach (Building_Door door in doors)
			{
				List<StorageUnit> here = new List<StorageUnit>();
				if (distance != null)
				{
					IntVec3 target = door.Position + new IntVec3(dir.x * (int)distance.Value, 0, dir.z * (int)distance.Value);
					foreach (StorageUnit unit in all)
					{
						if (unit.room != null && rooms.Contains(unit.room) && unit.cells.Count > 0
							&& unit.cells.Min((IntVec3 cell) => Math.Max(Math.Abs(cell.x - target.x), Math.Abs(cell.z - target.z))) <= 1)
						{
							here.Add(unit);
						}
					}
				}
				else
				{
					StorageUnit nearest = null;
					int best = int.MaxValue;
					foreach (StorageUnit unit in all)
					{
						if (unit.room == null || !rooms.Contains(unit.room) || unit.cells.Count == 0)
						{
							continue;
						}
						IntVec3 closest = unit.cells.OrderBy((IntVec3 cell) => cell.DistanceToSquared(door.Position)).First();
						if (!OffsetIsToward(closest - door.Position, dir))
						{
							continue;
						}
						int d = closest.DistanceToSquared(door.Position);
						if (d < best)
						{
							best = d;
							nearest = unit;
						}
					}
					if (nearest != null)
					{
						here.Add(nearest);
					}
				}
				foreach (StorageUnit unit in here)
				{
					if (!found.Contains(unit))
					{
						found.Add(unit);
					}
				}
			}
			if (found.Count == 0)
			{
				throw new Fault("There is no storage " + (distance != null ? Value_Number(distance.Value) + " tiles " : "") + direction + " of the door from the "
					+ c.Text("ROOM") + " to the " + leadsTo + ".");
			}
			return found;
		}

		private static string Value_Number(double d)
		{
			return VerbalCommands.RouterScript.Value.FormatNumber(d);
		}

		// Doors that have a room of `a` on one side and a room of `b` on the other.
		internal static List<Building_Door> DoorsBetween(Map map, List<Room> a, List<Room> b)
		{
			List<Building_Door> result = new List<Building_Door>();
			foreach (Building building in map.listerBuildings.allBuildingsColonist)
			{
				Building_Door door = building as Building_Door;
				if (door == null || !door.Spawned)
				{
					continue;
				}
				List<Room> sides = DoorSides(map, door);
				bool touchesA = sides.Any((Room r) => a.Contains(r));
				bool touchesB = sides.Any((Room r) => b.Contains(r));
				if (touchesA && touchesB)
				{
					result.Add(door);
				}
			}
			return result;
		}

		// The proper rooms on the sides of a door (the cells next to it).
		internal static List<Room> DoorSides(Map map, Building_Door door)
		{
			List<Room> sides = new List<Room>();
			foreach (IntVec3 offset in GenAdj.CardinalDirections)
			{
				IntVec3 cell = door.Position + offset;
				if (!cell.InBounds(map))
				{
					continue;
				}
				Room room = cell.GetRoom(map);
				if (room != null && room.ProperRoom && !sides.Contains(room))
				{
					sides.Add(room);
				}
			}
			return sides;
		}

		// ---- ThingFilter names: items first, then categories ------------------------------------------------

		internal static void ClassifyThings(List<string> names, List<string> categories, List<string> items)
		{
			foreach (string name in names)
			{
				string chosen = null;
				bool isItem = false;
				foreach (string candidate in NameVariants(name))
				{
					if (Executor.FindItemDefs(candidate).Count > 0)
					{
						chosen = candidate;
						isItem = true;
						break;
					}
					if (Executor.FindCategoryDefs(candidate).Count > 0)
					{
						chosen = candidate;
						break;
					}
				}
				if (chosen == null)
				{
					throw new Fault("I don't know what '" + name + "' is as something to store.");
				}
				(isItem ? items : categories).Add(chosen);
			}
		}

		// "steel", then "steels", "steel" without a final s / es.
		private static IEnumerable<string> NameVariants(string name)
		{
			string n = name.Trim();
			yield return n;
			yield return n + "s";
			if (n.EndsWith("es") && n.Length > 3)
			{
				yield return n.Substring(0, n.Length - 2);
			}
			if (n.EndsWith("s") && n.Length > 2)
			{
				yield return n.Substring(0, n.Length - 1);
			}
		}

		// ---- storage forms -----------------------------------------------------------------------------------

		private static string PlaceText(FormContext c, int unitCount)
		{
			string text;
			if (c.req.thoseObjects != null)
			{
				text = "those";
			}
			else if (c.Has("DIRECTION"))
			{
				text = "storage " + (c.Has("DISTANCE") ? c.Text("DISTANCE") + " tiles " : "") + c.Text("DIRECTION") + " of the door to the " + c.Text("DOOR")
					+ (c.Has("ROOM") ? " in the " + c.Text("ROOM") : "");
			}
			else if (c.Has("ZONE"))
			{
				text = "zone " + c.Text("ZONE");
			}
			else if (c.Has("STORAGE_ID"))
			{
				text = "storage " + c.Text("STORAGE_ID");
			}
			else if (c.Has("SPOT"))
			{
				text = "storage at " + c.Text("SPOT");
			}
			else
			{
				text = "storage" + (c.Has("BENCH") ? " near the " + c.Text("BENCH") : "") + (c.Has("ROOM") ? " in the " + c.Text("ROOM") : "");
			}
			return unitCount > 1 ? text + " (" + unitCount + " places)" : text;
		}

		private static PlanReply PlanStorage(FormContext c)
		{
			Map map = c.map;
			bool priorityForm = c.form == "storage priority";

			List<StorageUnit> units = new List<StorageUnit>();
			List<BuildRef> builds = new List<BuildRef>();
			if (c.req.thoseObjects != null)
			{
				foreach (object o in c.req.thoseObjects)
				{
					StorageRef sref = o as StorageRef;
					BuildRef bref = o as BuildRef;
					if (sref != null)
					{
						StorageUnit unit = StorageIndex.FindUnit(map, sref.unitId);
						if (unit == null)
						{
							throw new Fault("The storage an earlier sentence named is no longer there.");
						}
						units.Add(unit);
					}
					else if (bref != null)
					{
						builds.Add(bref);
					}
				}
				foreach (BuildRef b in builds)
				{
					// Contract section 5, checked again here against the real thing: only storage has a priority
					// or a list of what it holds.
					if (!b.isStorage)
					{
						throw new Fault("A " + b.label + " is not storage, so it has no " + (priorityForm ? "priority." : "list of what it holds."));
					}
				}
				if (units.Count == 0 && builds.Count == 0)
				{
					throw new Fault("'those' has no storage from an earlier sentence in this order.");
				}
			}
			else
			{
				units = ResolveStorageUnits(c);
			}

			List<string> things = c.TextList("THINGS");
			List<string> cats = new List<string>();
			List<string> items = new List<string>();
			if (!priorityForm)
			{
				if (things.Count == 0)
				{
					throw new Fault("The order did not say what to store.");
				}
				ClassifyThings(things, cats, items);
			}
			string priority = priorityForm ? (c.Text("PRIORITY") ?? "").ToLowerInvariant() : null;
			if (priorityForm)
			{
				StoragePriority parsed;
				if (!TryParsePriority(priority, out parsed))
				{
					throw new Fault("'" + priority + "' is not a storage priority. Use low, normal, preferred, important or critical.");
				}
			}

			Plan plan = NewPlan("set_storage");
			if (units.Count > 0)
			{
				JObject input = new JObject();
				input["mode"] = c.form == "storage holds only" ? "replace" : "modify";
				input["priority"] = priorityForm ? priority : ToolDefinitions.SetStoragePriorityUnchanged;
				bool disallow = c.form == "storage no longer holds";
				input["allow_categories"] = new JArray(disallow ? new string[0] : cats.ToArray());
				input["disallow_categories"] = new JArray(disallow ? cats.ToArray() : new string[0]);
				input["allow_items"] = new JArray(disallow ? new string[0] : items.ToArray());
				input["disallow_items"] = new JArray(disallow ? items.ToArray() : new string[0]);
				input["allow_special"] = new JArray();
				input["disallow_special"] = new JArray();
				input["hit_points"] = JValue.CreateNull();
				input["quality"] = JValue.CreateNull();
				List<string> ids = units.Select((StorageUnit u) => u.id).ToList();
				input["storage_ids"] = new JArray(ids.ToArray());
				Plan fromExecutor = Executor.Resolve(ToolUse("set_storage", input), LiteSnapshot(map), new HashSet<string>(ids, StringComparer.Ordinal));
				plan.writes.AddRange(fromExecutor.writes);
				plan.notes.AddRange(fromExecutor.notes);
			}
			if (builds.Count > 0)
			{
				foreach (BuildRef b in builds)
				{
					AddBlueprintStorageWrite(c, plan, b, priorityForm ? (StoragePriority?)ParsePriority(priority) : null, c.form, cats, items);
				}
			}

			string place = PlaceText(c, units.Count + builds.Count);
			string line;
			switch (c.form)
			{
				case "storage holds only":
					line = "Only " + JoinNames(things) + " in " + place + ".";
					break;
				case "storage also holds":
					line = "Also allow " + JoinNames(things) + " in " + place + ".";
					break;
				case "storage no longer holds":
					line = "Keep " + JoinNames(things) + " out of " + place + ".";
					break;
				default:
					line = "Set " + place + " to " + priority + " priority.";
					break;
			}
			// Linked storage buildings share one setting (RimWorld\Building_Storage.cs:100-107): one named by ID or
			// cell changes them all, so the player reads that.
			if ((c.Has("STORAGE_ID") || c.Has("SPOT")) && units.Count == 1 && units[0].group != null && units[0].buildings.Count > 1)
			{
				line += " It is linked with " + (units[0].buildings.Count - 1) + " other storage buildings; they share this setting.";
			}
			List<object> objects = new List<object>();
			foreach (StorageUnit u in units)
			{
				StorageRef r = new StorageRef();
				r.unitId = u.id;
				objects.Add(r);
			}
			objects.AddRange(builds.Cast<object>());
			return Reply(plan, line, null, objects);
		}

		private static bool TryParsePriority(string name, out StoragePriority priority)
		{
			foreach (StoragePriority p in Enum.GetValues(typeof(StoragePriority)))
			{
				if (p != StoragePriority.Unstored && string.Equals(p.ToString(), name, StringComparison.OrdinalIgnoreCase))
				{
					priority = p;
					return true;
				}
			}
			priority = StoragePriority.Normal;
			return false;
		}

		private static StoragePriority ParsePriority(string name)
		{
			StoragePriority p;
			TryParsePriority(name, out p);
			return p;
		}

		// "Those" after a build sentence: the blueprints that sentence places are changed when the order is applied
		// (BuildTools.RecentBlueprints is filled by the build writes, which run first).
		private static void AddBlueprintStorageWrite(FormContext c, Plan plan, BuildRef b, StoragePriority? priority, string form, List<string> cats, List<string> items)
		{
			ThingFilter parent = b.def.building != null && b.def.building.fixedStorageSettings != null && b.def.building.fixedStorageSettings.filter != null
				? b.def.building.fixedStorageSettings.filter
				: StorageSettings.EverStorableFixedSettings().filter;
			List<ThingCategoryDef> categoryDefs = new List<ThingCategoryDef>();
			List<ThingDef> itemDefs = new List<ThingDef>();
			foreach (string name in cats)
			{
				List<ThingCategoryDef> found = Executor.FindCategoryDefs(name);
				if (found.Count != 1)
				{
					throw new Fault("'" + name + "' could mean several kinds of things; name it more exactly.");
				}
				categoryDefs.Add(found[0]);
			}
			foreach (string name in items)
			{
				List<ThingDef> found = Executor.FindItemDefs(name);
				if (found.Count != 1)
				{
					throw new Fault("'" + name + "' could mean several items; name it more exactly.");
				}
				if (form != "storage no longer holds" && !StorageIndex.CanHold(parent, found[0]))
				{
					throw new Fault(UpperFirst(found[0].label) + " can't be stored in a " + b.label + ".");
				}
				itemDefs.Add(found[0]);
			}
			Write write = new Write();
			write.description = (priority != null ? "set the new " + b.label + " to " + priority + " priority" : "set what the new " + b.label + " holds");
			ThingDef buildingDef = b.def;
			write.apply = delegate
			{
				List<Blueprint_Storage> placed = BuildTools.RecentBlueprints.OfType<Blueprint_Storage>()
					.Where((Blueprint_Storage bp) => bp.EntityToBuild() == buildingDef).ToList();
				if (placed.Count == 0)
				{
					throw new Fault("The " + b.label + " was not placed, so its settings were not changed.");
				}
				List<SpecialThingFilterDef> keepSpecial = DefDatabase<SpecialThingFilterDef>.AllDefsListForReading;
				foreach (Blueprint_Storage bp in placed)
				{
					StorageSettings settings = bp.settings;
					if (settings == null)
					{
						continue;
					}
					if (priority != null)
					{
						settings.Priority = priority.Value;
						continue;
					}
					if (form == "storage holds only")
					{
						settings.filter.SetDisallowAll(null, keepSpecial);
					}
					bool allow = form != "storage no longer holds";
					foreach (ThingCategoryDef cat in categoryDefs)
					{
						settings.filter.SetAllow(cat, allow, null, keepSpecial);
					}
					foreach (ThingDef item in itemDefs)
					{
						settings.filter.SetAllow(item, allow);
					}
				}
			};
			plan.writes.Add(write);
		}

		// ---- build ---------------------------------------------------------------------------------------------

		private static PlanReply PlanBuild(FormContext c)
		{
			Map map = c.map;
			string building = c.Text("BUILDING");
			if (building == null)
			{
				throw new Fault("The order did not say what to build.");
			}
			double? count = c.Number("COUNT");
			string where = "auto";
			if (c.Has("DIRECTION"))
			{
				where = c.Text("DIRECTION").ToLowerInvariant();
				if (where != "north" && where != "south" && where != "east" && where != "west")
				{
					throw new Fault("A building goes on the north, south, east or west side of another building.");
				}
			}

			List<Room> rooms = c.Has("ROOM") ? ResolveRooms(map, c.Text("ROOM")) : null;
			string anchorId;
			string anchorText;
			Plan extraNotes = NewPlan("note");
			if (c.Has("ANCHOR"))
			{
				string anchorName = c.Text("ANCHOR");
				List<Building> candidates = new List<Building>();
				foreach (Building b in map.listerBuildings.allBuildingsColonist)
				{
					if (!b.Spawned || b.Faction != Faction.OfPlayer)
					{
						continue;
					}
					if (!(SameName(b.def.label, anchorName) || SameName(BuildDefs.Label(b.def, b.Stuff), anchorName)))
					{
						continue;
					}
					if (rooms != null && !rooms.Contains(RoomOfThing(b)))
					{
						continue;
					}
					candidates.Add(b);
				}
				if (candidates.Count == 0)
				{
					throw new Fault("There is no " + anchorName + (c.Has("ROOM") ? " in the " + c.Text("ROOM") : " on the map") + " to build next to.");
				}
				if (candidates.Count > 1)
				{
					extraNotes.notes.Add("There are " + candidates.Count + " of " + anchorName + "; the first one found is used.");
				}
				anchorId = candidates[0].ThingID;
				anchorText = (where == "auto" ? "next to the " : where + " of the ") + anchorName;
			}
			else
			{
				if (rooms == null)
				{
					throw new Fault("Building needs a room, or a building to put it next to.");
				}
				if (rooms.Count > 1)
				{
					throw new Fault("Several rooms are called '" + c.Text("ROOM") + "'. Say which one is meant.");
				}
				anchorId = rooms[0].ID.ToString();
				anchorText = "";
			}

			JObject placement = new JObject();
			placement["building"] = building;
			placement["stuff"] = c.Text("STUFF") ?? "";
			placement["count"] = count != null ? (int)Math.Round(count.Value) : 1;
			placement["anchor_id"] = anchorId;
			placement["where"] = c.Has("ANCHOR") ? where : "auto";
			placement["rotation"] = "auto";
			placement["storage_priority"] = BuildTools.StoragePriorityDefault;
			placement["storage_only"] = new JArray();
			JObject input = new JObject();
			input["placements"] = new JArray(placement);
			Plan plan = BuildTools.ResolvePlaceBlueprint(input, LiteSnapshot(map));
			plan.notes.AddRange(extraNotes.notes);

			ThingDef def = ResolveBuildDef(building);
			List<object> objects = new List<object>();
			string label = def != null ? BuildDefs.Label(def, null) : building;
			if (def != null)
			{
				BuildRef r = new BuildRef();
				r.def = def;
				r.label = label;
				r.isStorage = BuildDefs.IsStorage(def);
				objects.Add(r);
			}
			string line = "Build " + (count != null ? Value_Number(count.Value) + " " : "") + building
				+ (anchorText.Length > 0 ? " " + anchorText : "") + (c.Has("ROOM") ? " in the " + c.Text("ROOM") : "") + ".";
			return Reply(plan, line, null, objects);
		}

		// The same choice BuildTools.ResolveBuilding makes (the colony's buildable one), or null.
		private static ThingDef ResolveBuildDef(string name)
		{
			List<ThingDef> found = BuildDefs.FindBuildables(name);
			if (found.Count > 1)
			{
				string ignored;
				List<ThingDef> available = found.Where((ThingDef d) => BuildDefs.IsAvailable(d, out ignored)).ToList();
				if (available.Count > 0)
				{
					found = available;
				}
			}
			return found.Count == 1 ? found[0] : null;
		}

		private static PlanReply PlanBuildRoom(FormContext c)
		{
			string roomText = c.Text("ROOM");
			if (roomText == null)
			{
				throw new Fault("The order did not say which room to copy.");
			}
			JObject input = new JObject();
			input["layout"] = roomText;
			input["options"] = PlanOptions.MaxOptions;
			Plan plan;
			try
			{
				plan = BuildTools.ResolveDrawPlan(input, LiteSnapshot(c.map));
			}
			catch (Fault first)
			{
				// An existing room: its room type is what a saved layout is filed under.
				List<Room> rooms = StorageIndex.FindRoomsByLabel(c.map, roomText);
				if (rooms.Count == 0 || rooms[0].Role == null || SameName(rooms[0].Role.label, roomText))
				{
					throw first;
				}
				input["layout"] = rooms[0].Role.label;
				plan = BuildTools.ResolveDrawPlan(input, LiteSnapshot(c.map));
			}
			return Reply(plan, "Draw plans for a room like the " + roomText + ".", null, null);
		}

		private static PlanReply PlanBuildOption(FormContext c)
		{
			double? n = c.Number("OPTION");
			if (n == null)
			{
				throw new Fault("The order did not say which option.");
			}
			JObject input = new JObject();
			input["option"] = (int)n.Value;
			Plan plan = BuildTools.ResolveBuildOption(input, LiteSnapshot(c.map));
			return Reply(plan, "Build option " + (int)n.Value + ".", null, null);
		}
	}
}
