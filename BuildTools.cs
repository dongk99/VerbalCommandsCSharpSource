using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Brief 21 (briefs\21_blueprints_plans_patterns.md): the three build tools.
	//   place_blueprint  blueprints for buildings next to an existing building or in a room
	//                    (user 2026-09-27T23:50:42Z: "give the mod tooling so that it can place blueprints
	//                    for buildings").
	//   draw_plan        a saved room layout drawn as plans at up to three free spots ("Option 1..3").
	//   build_option     blueprints for one drawn option; the mod's plans are then removed.
	// Offered only when the order (or the router's rewrite) says "build" or "construct"
	// (Router.ComboBuild; user 2026-09-28T00:49:09Z).
	//
	// Same shape as the other tools: Resolve* checks everything against the live map and returns a Plan
	// whose writes are plain sentences the player confirms; nothing on the map changes before Apply.
	// The build data for the model (AttachBuildData) is sentences and ids, never map coordinates.
	// MAIN THREAD only.
	public static class BuildTools
	{
		public const string PlaceBlueprintTool = "place_blueprint";
		public const string DrawPlanTool = "draw_plan";
		public const string BuildOptionTool = "build_option";

		private const string LogPrefix = "[VerbalCommands] build: ";

		// One call may place this many of one building (a typo guard, not a user value).
		private const int MaxCount = 20;
		// Build data caps, so a large colony doesn't bloat the request.
		private const int MaxAnchors = 80;
		private const int MaxHabitLines = 30;
		private const int MaxLayouts = 30;

		public const string StoragePriorityDefault = "default";

		// Brief 23: the blueprints placed since the last FormPlanner.BeginApply (main thread only).
		public static readonly List<Blueprint_Build> RecentBlueprints = new List<Blueprint_Build>();

		// Added to the system prompt only when the build tools are offered. Headless sends no tool
		// descriptions (HeadlessClient.BuildJsonSchema), so the rules are here and in the schema.
		public const string SystemPromptBuild =
			"Building (place_blueprint, draw_plan, build_option):\n" +
			"- Blueprints only: colonists do the building. Nothing existing is ever removed or replaced.\n" +
			"- place_blueprint puts one kind of building next to an anchor. anchor_id is an id from build.anchors " +
			"(a workbench, bed or storage building) or a room id from rooms. The mod picks the exact tile: inside the " +
			"anchor's room, the way the player usually arranges those two things (build.habits), else the closest " +
			"free spot, never in front of a door. Use \"where\" only when the player said where.\n" +
			"- building and stuff are game names (\"shelf\", \"wood\"); leave stuff empty unless the player named a material.\n" +
			"- count is how many the player asked for. If the player gave no number for a plural, ask instead.\n" +
			"- A storage building gets priority Preferred unless the player named one; storage_only lists what it may hold " +
			"(empty = the building's normal settings).\n" +
			"- A whole room (\"build a bedroom\") is built from a saved layout in build.layouts: call draw_plan with its " +
			"id (or the room type). That draws the options on the map; the player then picks with an order like " +
			"\"build option 2\", which is build_option. Options already drawn are in build.options.\n" +
			"- If no layout of that room type exists, say so and call no tool.";

		// ---- tool definitions ---------------------------------------------------------------------

		public static void AddTools(JArray tools)
		{
			tools.Add(Tool(PlaceBlueprintTool, PlaceBlueprintDescription, PlaceBlueprintSchema()));
			tools.Add(Tool(DrawPlanTool, DrawPlanDescription, DrawPlanSchema()));
			tools.Add(Tool(BuildOptionTool, BuildOptionDescription, BuildOptionSchema()));
		}

		private static JObject Tool(string name, string description, JObject schema)
		{
			JObject tool = new JObject();
			tool["name"] = name;
			tool["description"] = description;
			tool["input_schema"] = schema;
			tool["strict"] = true;
			return tool;
		}

		private const string PlaceBlueprintDescription =
			"Places blueprints for buildings (furniture, workbenches, storage, anything in the build menu). Each " +
			"placement names one kind of building, how many, and an anchor: a building id from build.anchors or a " +
			"room id from rooms. The mod chooses the tiles itself, inside the anchor's room: where the player " +
			"usually puts that kind of building relative to the anchor (build.habits), otherwise the closest free " +
			"spot to the anchor that leaves doors and walkways clear. Nothing is removed or replaced. The call is " +
			"refused when the building name is unknown or not yet buildable, the anchor id is not in the data, or " +
			"no free spot exists.";

		private const string DrawPlanDescription =
			"Shows a saved room layout (build.layouts) on the map as plans, at up to three free spots near the " +
			"colony, labelled Option 1, 2, 3. Plans cost nothing and build nothing. Use this when the player asks " +
			"for a whole room. The player then picks one with a later order.";

		private const string BuildOptionDescription =
			"Places the blueprints (walls, doors, furniture) for one option drawn earlier by draw_plan " +
			"(build.options), then removes the option plans.";

		private static JObject Str(string description)
		{
			JObject o = new JObject();
			o["type"] = "string";
			if (description != null)
			{
				o["description"] = description;
			}
			return o;
		}

		private static JObject Int(string description)
		{
			JObject o = new JObject();
			o["type"] = "integer";
			o["description"] = description;
			return o;
		}

		private static JObject Enum(string description, params string[] values)
		{
			JObject o = new JObject();
			o["type"] = "string";
			o["enum"] = new JArray(values);
			o["description"] = description;
			return o;
		}

		private static JObject Obj(JObject properties, params string[] required)
		{
			JObject o = new JObject();
			o["type"] = "object";
			o["properties"] = properties;
			o["required"] = new JArray(required);
			o["additionalProperties"] = false;
			return o;
		}

		// Every property is required, like the other strict tools: "", "auto", "default" and empty arrays
		// stand for "not said".
		private static JObject PlaceBlueprintSchema()
		{
			JObject p = new JObject();
			p["building"] = Str("What to build: the game's name for it (\"shelf\", \"end table\") or its defName.");
			p["stuff"] = Str("Material name (\"wood\", \"steel\"), or \"\" for the one the build menu would pick.");
			p["count"] = Int("How many to place, 1 to " + MaxCount + ". Only a number the player said.");
			p["anchor_id"] = Str("A building id from build.anchors, or a room id from rooms (as text).");
			p["where"] = Enum("auto = the player's usual arrangement, else the closest free spot. adjacent = must touch the anchor. " +
				"north/east/south/west = on that side of the anchor (or that side of the room).",
				"auto", "adjacent", "north", "east", "south", "west");
			p["rotation"] = Enum("Which way it faces; auto unless the player said.", "auto", "north", "east", "south", "west");

			// StoragePriority minus Unstored (RimWorld\StoragePriority.cs:3-11), as set_storage offers.
			List<string> priorities = new List<string>();
			priorities.Add(StoragePriorityDefault);
			foreach (StoragePriority sp in System.Enum.GetValues(typeof(StoragePriority)))
			{
				if (sp != StoragePriority.Unstored)
				{
					priorities.Add(sp.ToString());
				}
			}
			p["storage_priority"] = Enum("For storage buildings only: default = Preferred. Otherwise the priority the player named.",
				priorities.ToArray());
			JObject only = new JObject();
			only["type"] = "array";
			only["items"] = Str(null);
			only["description"] = "For storage buildings only: item or category names it may hold and nothing else. Empty array = its normal settings.";
			p["storage_only"] = only;

			JObject placement = Obj(p, "building", "stuff", "count", "anchor_id", "where", "rotation", "storage_priority", "storage_only");
			JObject placements = new JObject();
			placements["type"] = "array";
			placements["items"] = placement;
			placements["description"] = "One entry per kind of building.";

			JObject props = new JObject();
			props["placements"] = placements;
			return Obj(props, "placements");
		}

		private static JObject DrawPlanSchema()
		{
			JObject props = new JObject();
			props["layout"] = Str("A layout id from build.layouts, or a room type (\"bedroom\") to use the first layout of that type.");
			props["options"] = Int("How many spots to show, 1 to " + PlanOptions.MaxOptions + ".");
			return Obj(props, "layout", "options");
		}

		private static JObject BuildOptionSchema()
		{
			JObject props = new JObject();
			props["option"] = Int("The option number the player picked (build.options).");
			return Obj(props, "option");
		}

		// ---- build data for the model ----------------------------------------------------------------

		// Adds snap.json["build"]: anchors, habits, layouts and drawn options. Reads the map and updates
		// the layout file (LayoutMemory.Refresh). Never throws.
		public static void AttachBuildData(Snapshot snap)
		{
			try
			{
				Map map = snap.map;
				LayoutView view = LayoutMemory.Refresh(map);
				JObject build = new JObject();

				JArray anchors = new JArray();
				List<Building> buildings = map.listerBuildings.allBuildingsColonist;
				for (int i = 0; i < buildings.Count && anchors.Count < MaxAnchors; i++)
				{
					Building b = buildings[i];
					string kind = BuildDefs.KindOf(b.def);
					if (b.Faction != Faction.OfPlayer || !b.Spawned
						|| (kind != BuildDefs.KindWorkbench && kind != BuildDefs.KindBed && kind != BuildDefs.KindStorage))
					{
						continue;
					}
					JObject a = new JObject();
					a["id"] = b.ThingID;
					a["name"] = BuildDefs.Label(b.def, b.Stuff);
					a["kind"] = kind;
					Room room = BuildDefs.RoomOf(b);
					string roomText = BuildDefs.RoomText(room);
					if (roomText.Length > 0)
					{
						a["room"] = roomText;
					}
					anchors.Add(a);
				}
				build["anchors"] = anchors;

				JArray habits = new JArray();
				foreach (string line in HabitSentences(view))
				{
					habits.Add(line);
				}
				build["habits"] = habits;

				JArray layouts = new JArray();
				foreach (SavedLayout l in view.layouts.Take(MaxLayouts))
				{
					JObject o = new JObject();
					o["id"] = l.id;
					o["room_type"] = l.roleLabel;
					o["size"] = l.width + " by " + l.height + " tiles with walls";
					o["contains"] = l.ContentsText();
					if (l.fromThisColony && !l.from.NullOrEmpty())
					{
						o["copied_from"] = l.from;
					}
					layouts.Add(o);
				}
				build["layouts"] = layouts;

				JArray options = new JArray();
				GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
				if (gc != null)
				{
					foreach (PendingOption opt in gc.PendingOptionsFor(map))
					{
						SavedLayout l = opt.Layout();
						JObject o = new JObject();
						o["option"] = opt.number;
						o["room_type"] = l != null ? l.roleLabel : "";
						o["where"] = l != null ? PlanOptions.DescribeSpot(map, l, opt.Origin) : "";
						options.Add(o);
					}
				}
				build["options"] = options;

				snap.json["build"] = build;
			}
			catch (Exception ex)
			{
				Log.Warning(LogPrefix + "the build data could not be made: " + ex);
			}
		}

		// "The player puts a storage building to the left of a workbench (seen 3 times, e.g. shelf next to
		// electric stove)." One line per pair of kinds: the arrangement seen most often.
		private static List<string> HabitSentences(LayoutView view)
		{
			List<string> lines = new List<string>();
			foreach (KeyValuePair<string, Dictionary<string, int>> pair in view.habits.OrderBy((KeyValuePair<string, Dictionary<string, int>> p) => p.Key, StringComparer.Ordinal))
			{
				string[] kinds = pair.Key.Split('>');
				if (kinds.Length != 2 || pair.Value.Count == 0)
				{
					continue;
				}
				KeyValuePair<string, int> best = BestHabit(pair.Value);
				LayoutRelation rel;
				if (!LayoutRelation.TryParseKey(best.Key, out rel))
				{
					continue;
				}
				string example;
				view.examples.TryGetValue(pair.Key + "|" + best.Key, out example);
				string line = "The player puts a " + BuildDefs.KindLabel(kinds[1]) + " " + rel.LocalPhrase() + " a " + BuildDefs.KindLabel(kinds[0]) +
					" (seen " + best.Value + (best.Value == 1 ? " time" : " times") + (example != null ? ", e.g. " + example : "") + ").";
				lines.Add(line);
				if (lines.Count >= MaxHabitLines)
				{
					break;
				}
			}
			if (lines.Count > 0)
			{
				lines.Insert(0, "Sides are relative to the way the second-named building faces.");
			}
			return lines;
		}

		private static KeyValuePair<string, int> BestHabit(Dictionary<string, int> habits)
		{
			KeyValuePair<string, int> best = new KeyValuePair<string, int>(null, 0);
			foreach (KeyValuePair<string, int> kv in habits.OrderBy((KeyValuePair<string, int> h) => h.Key, StringComparer.Ordinal))
			{
				if (kv.Value > best.Value)
				{
					best = kv;
				}
			}
			return best;
		}

		// ---- place_blueprint -----------------------------------------------------------------------

		private sealed class StorageSetup
		{
			public bool isStorage;
			public StoragePriority priority;
			public bool hasOnly;
			public List<ThingCategoryDef> categories = new List<ThingCategoryDef>();
			public List<ThingDef> items = new List<ThingDef>();
		}

		public static Plan ResolvePlaceBlueprint(JObject input, Snapshot snap)
		{
			Map map = LiveMap(snap);
			JArray placements = input["placements"] as JArray;
			if (placements == null || placements.Count == 0)
			{
				throw new Fault("The order named nothing to build, so nothing was placed.");
			}

			LayoutView view = LayoutMemory.ViewFor(map);
			Plan plan = new Plan();
			plan.toolName = PlaceBlueprintTool;

			// Tiles taken by earlier placements of this same call (they are not on the map yet).
			HashSet<IntVec3> reserved = new HashSet<IntVec3>();
			HashSet<IntVec3> reservedBlocking = new HashSet<IntVec3>();
			List<AgentWindCheck.Placed> windPlaced = new List<AgentWindCheck.Placed>();
			Dictionary<ThingDef, int> costs = new Dictionary<ThingDef, int>();
			// Stuff already claimed by earlier entries of this order (not yet taken from storage).
			Dictionary<ThingDef, int> claimed = new Dictionary<ThingDef, int>();

			foreach (JToken tok in placements)
			{
				JObject p = tok as JObject;
				if (p == null)
				{
					continue;
				}
				string buildingName = ((string)p["building"] ?? "").Trim();
				ThingDef def = ResolveBuilding(buildingName);
				string label = BuildDefs.Label(def, null);

				string unavailable;
				if (!BuildDefs.IsAvailable(def, out unavailable))
				{
					throw new Fault("Nothing was placed: " + label + " can't be built, because " + unavailable + ".");
				}

				// The count comes first: the material choice needs the amount this entry will use.
				int count = p["count"] != null && p["count"].Type == JTokenType.Integer ? (int)p["count"] : 1;
				if (count < 1 || count > MaxCount)
				{
					throw new Fault("Nothing was placed: the number of " + label + " must be 1 to " + MaxCount + ".");
				}

				string stuffProblem;
				BuildDefs.AutoStuff how;
				ThingDef stuff = BuildDefs.ResolveStuff(def, (string)p["stuff"], map, def.CostStuffCount * count, claimed, out how, out stuffProblem);
				if (stuffProblem != null)
				{
					throw new Fault("Nothing was placed: " + stuffProblem + ".");
				}
				label = BuildDefs.Label(def, stuff);
				string stuffNote = BuildDefs.AutoStuffNote(def, stuff, how);
				if (stuffNote != null)
				{
					plan.notes.Add(stuffNote);
				}

				Thing anchor;
				Room room;
				ResolveAnchor(map, ((string)p["anchor_id"] ?? "").Trim(), out anchor, out room);

				string where = ((string)p["where"] ?? "auto").Trim().ToLowerInvariant();
				Rot4? rotation = ParseRotation((string)p["rotation"]);
				if (!def.rotatable)
				{
					rotation = null;
				}

				StorageSetup storage = ResolveStorageSetup(def, p, label, plan.notes);

				string anchorKind = anchor != null ? BuildDefs.KindOf(anchor.def) : null;
				bool habitsFromExamples = false;
				Dictionary<string, int> habits = anchor != null ? view.HabitsFor(anchorKind, BuildDefs.KindOf(def), out habitsFromExamples) : null;
				bool habitNoteAdded = false;

				for (int i = 0; i < count; i++)
				{
					PlacementQuery q = new PlacementQuery();
					q.map = map;
					q.def = def;
					q.stuff = stuff;
					q.anchor = anchor;
					q.room = room;
					q.where = where;
					q.rotation = rotation;
					q.reserved = reserved;
					q.reservedBlocking = reservedBlocking;
					q.habits = habits;

					string failReason;
					PlacementSpot spot = PlacementSearch.Find(q, out failReason);
					if (spot == null)
					{
						if (i == 0)
						{
							throw new Fault("Nothing was placed: " + label + " doesn't fit" + WhereText(anchor, room) + ", because " + failReason + ".");
						}
						plan.notes.Add("Only " + i + " of " + count + " " + label + " fit" + WhereText(anchor, room) + ": " + failReason + ".");
						break;
					}

					foreach (IntVec3 c in GenAdj.OccupiedRect(spot.cell, spot.rot, def.Size))
					{
						reserved.Add(c);
						if (def.passability == Traversability.Impassable)
						{
							reservedBlocking.Add(c);
						}
					}
					BuildDefs.AddCosts(costs, def, stuff, 1);

					if (spot.habitCount > 0 && !habitsFromExamples && !habitNoteAdded && HabitIsTied(habits))
					{
						habitNoteAdded = true;
						plan.notes.Add("You arrange " + BuildDefs.KindLabel(BuildDefs.KindOf(def)) + " and " + BuildDefs.KindLabel(anchorKind) +
							" in more than one way equally often. Say which side you want if this one is wrong.");
					}

					Write write = new Write();
					write.description = DescribePlacement(map, def, label, spot, anchor, room, storage, habitsFromExamples);
					ThingDef capturedDef = def;
					ThingDef capturedStuff = stuff;
					IntVec3 capturedCell = spot.cell;
					Rot4 capturedRot = spot.rot;
					string capturedLabel = label;
					StorageSetup capturedStorage = storage;
					write.apply = delegate
					{
						PlaceOne(map, capturedDef, capturedStuff, capturedCell, capturedRot, capturedLabel, capturedStorage, true);
					};
					plan.writes.Add(write);
					windPlaced.Add(new AgentWindCheck.Placed(def, spot.cell, spot.rot));
				}
			}

			if (plan.writes.Count == 0)
			{
				throw new Fault("The order named nothing to build, so nothing was placed.");
			}
			if (costs.Count > 0)
			{
				plan.notes.Add("Materials: " + BuildDefs.CostText(map, costs) + ".");
			}
			AgentWindCheck.Note(plan, map, windPlaced);
			AgentLightCheck.Note(plan, map, windPlaced);
			AgentWorkspotCheck.Note(plan, map, windPlaced);
			AgentDoorCheck.Note(plan, map, windPlaced);
			AgentCoolerCheck.Note(plan, map, windPlaced);
			AgentVentCheck.Note(plan, map, windPlaced);
			return plan;
		}

		private static Map LiveMap(Snapshot snap)
		{
			Map map = snap != null ? snap.map : null;
			if (map == null || Find.Maps == null || !Find.Maps.Contains(map))
			{
				throw new Fault("The map this order was for is no longer loaded, so nothing was changed.");
			}
			return map;
		}

		private static ThingDef ResolveBuilding(string name)
		{
			if (name.Length == 0)
			{
				throw new Fault("Nothing was placed: the order didn't say what to build.");
			}
			List<ThingDef> found = BuildDefs.FindBuildables(name);
			if (found.Count > 1)
			{
				// Several buildings share the name: keep the ones the colony can build now.
				string ignored;
				List<ThingDef> available = found.Where((ThingDef d) => BuildDefs.IsAvailable(d, out ignored)).ToList();
				if (available.Count > 0)
				{
					found = available;
				}
			}
			if (found.Count == 0)
			{
				throw new Fault("Nothing was placed: there is no building called '" + name + "'.");
			}
			if (found.Count > 1)
			{
				throw new Fault("Nothing was placed: '" + name + "' could mean several buildings (" +
					string.Join(", ", found.Select((ThingDef d) => d.label + " [" + d.defName + "]").ToArray()) + ").");
			}
			AgentBuild.RefuseSpot(found[0]);
			return found[0];
		}

		// A building id (ThingID of a player building on this map) or a room id (the number in rooms[]).
		private static void ResolveAnchor(Map map, string id, out Thing anchor, out Room room)
		{
			anchor = null;
			room = null;
			if (id.Length == 0)
			{
				throw new Fault("Nothing was placed: the order didn't say where (a room, or what to put it next to).");
			}
			int roomId;
			if (int.TryParse(id, out roomId))
			{
				foreach (Room r in StorageIndex.AllProperRooms(map))
				{
					if (r.ID == roomId)
					{
						room = r;
						return;
					}
				}
				throw new Fault("Nothing was placed: that room no longer exists.");
			}
			List<Building> buildings = map.listerBuildings.allBuildingsColonist;
			for (int i = 0; i < buildings.Count; i++)
			{
				if (buildings[i].ThingID == id && buildings[i].Spawned)
				{
					anchor = buildings[i];
					room = BuildDefs.RoomOf(anchor);
					return;
				}
			}
			throw new Fault("Nothing was placed: the building it should go next to no longer exists.");
		}

		private static Rot4? ParseRotation(string name)
		{
			switch ((name ?? "").Trim().ToLowerInvariant())
			{
				case "north": return Rot4.North;
				case "east": return Rot4.East;
				case "south": return Rot4.South;
				case "west": return Rot4.West;
				default: return null;
			}
		}

		// User, 2026-09-28T00:22:52Z: "\"Preferred\" is the default unless players specify shelves (low,
		// normal, preferred, important, critical). no other word is allowed."
		private static StorageSetup ResolveStorageSetup(ThingDef def, JObject p, string label, List<string> notes)
		{
			StorageSetup setup = new StorageSetup();
			setup.isStorage = BuildDefs.IsStorage(def);
			string priorityName = ((string)p["storage_priority"] ?? StoragePriorityDefault).Trim();
			JArray only = p["storage_only"] as JArray;
			bool priorityNamed = priorityName.Length > 0 && !string.Equals(priorityName, StoragePriorityDefault, StringComparison.OrdinalIgnoreCase);
			bool onlyNamed = only != null && only.Count > 0;

			if (!setup.isStorage)
			{
				if (priorityNamed || onlyNamed)
				{
					notes.Add(UpperFirst(label) + " is not storage, so the storage settings were left out.");
				}
				return setup;
			}

			setup.priority = StoragePriority.Preferred;
			if (priorityNamed)
			{
				bool found = false;
				foreach (StoragePriority sp in System.Enum.GetValues(typeof(StoragePriority)))
				{
					if (sp != StoragePriority.Unstored && string.Equals(sp.ToString(), priorityName, StringComparison.OrdinalIgnoreCase))
					{
						setup.priority = sp;
						found = true;
						break;
					}
				}
				if (!found)
				{
					throw new Fault("Nothing was placed: '" + priorityName + "' is not a storage priority.");
				}
			}

			if (!onlyNamed)
			{
				return setup;
			}
			// What this storage can hold at all: the building's fixed settings
			// (Blueprint_Storage.GetParentStoreSettings, RimWorld\Blueprint_Storage.cs:51-54;
			// StorageIndex.CanHold), else anything storable (RimWorld\StorageSettings.cs:136-146).
			ThingFilter parent = def.building != null && def.building.fixedStorageSettings != null && def.building.fixedStorageSettings.filter != null
				? def.building.fixedStorageSettings.filter
				: StorageSettings.EverStorableFixedSettings().filter;
			List<string> refused = new List<string>();
			foreach (JToken tok in only)
			{
				string name = ((string)tok ?? "").Trim();
				if (name.Length == 0)
				{
					continue;
				}
				ThingDef item = FindItem(name);
				if (item != null)
				{
					if (StorageIndex.CanHold(parent, item))
					{
						setup.items.Add(item);
					}
					else
					{
						refused.Add(name);
					}
					continue;
				}
				ThingCategoryDef cat = FindCategory(name);
				if (cat != null)
				{
					// ThingCategoryDef.DescendantThingDefs (Verse\ThingCategoryDef.cs:70).
					if (cat.DescendantThingDefs.Any((ThingDef d) => StorageIndex.CanHold(parent, d)))
					{
						setup.categories.Add(cat);
					}
					else
					{
						refused.Add(name);
					}
					continue;
				}
				throw new Fault("Nothing was placed: there is no item or category called '" + name + "'.");
			}
			if (setup.items.Count == 0 && setup.categories.Count == 0)
			{
				throw new Fault("Nothing was placed: " + string.Join(", ", refused.ToArray()) + " can't be stored in " + label + ".");
			}
			if (refused.Count > 0)
			{
				notes.Add(UpperFirst(string.Join(", ", refused.ToArray())) + " can't be stored in " + label + ", so that part was left out.");
			}
			setup.hasOnly = true;
			return setup;
		}

		// defName, then label; several defs sharing a label -> the one a storage tab can list (the same
		// narrowing as Executor.FindItemDefs).
		private static ThingDef FindItem(string name)
		{
			ThingDef exact = DefDatabase<ThingDef>.GetNamedSilentFail(name);
			if (exact != null && exact.category == ThingCategory.Item)
			{
				return exact;
			}
			List<ThingDef> byLabel = new List<ThingDef>();
			foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if (def.category != ThingCategory.Item)
				{
					continue;
				}
				if (string.Equals(def.defName, name, StringComparison.OrdinalIgnoreCase))
				{
					return def;
				}
				if (def.label != null && string.Equals(def.label.Trim(), name, StringComparison.OrdinalIgnoreCase))
				{
					byLabel.Add(def);
				}
			}
			if (byLabel.Count > 1)
			{
				ThingDef listable = byLabel.FirstOrDefault((ThingDef d) => d.virtualDefParent == null && d.EverStorable(willMinifyIfPossible: true) && d.PlayerAcquirable);
				if (listable != null)
				{
					return listable;
				}
			}
			return byLabel.Count > 0 ? byLabel[0] : null;
		}

		private static ThingCategoryDef FindCategory(string name)
		{
			ThingCategoryDef byLabel = null;
			foreach (ThingCategoryDef def in DefDatabase<ThingCategoryDef>.AllDefsListForReading)
			{
				if (string.Equals(def.defName, name, StringComparison.OrdinalIgnoreCase))
				{
					return def;
				}
				if (byLabel == null && def.label != null && string.Equals(def.label.Trim(), name, StringComparison.OrdinalIgnoreCase))
				{
					byLabel = def;
				}
			}
			return byLabel;
		}

		private static bool HabitIsTied(Dictionary<string, int> habits)
		{
			if (habits == null || habits.Count < 2)
			{
				return false;
			}
			List<int> counts = habits.Values.OrderByDescending((int v) => v).ToList();
			return counts[0] == counts[1];
		}

		private static string WhereText(Thing anchor, Room room)
		{
			string roomText = BuildDefs.RoomText(room);
			if (anchor != null)
			{
				return " near " + BuildDefs.Label(anchor.def, anchor.Stuff) + (roomText.Length > 0 ? " in " + roomText : "");
			}
			return roomText.Length > 0 ? " in " + roomText : " there";
		}

		// "Place wooden shelf blueprint touching the east side of electric stove in kitchen, the way you
		// placed 3 others (storage priority Preferred; holds only meals)."
		private static string DescribePlacement(Map map, ThingDef def, string label, PlacementSpot spot, Thing anchor, Room room, StorageSetup storage, bool habitFromExamples)
		{
			string roomText = BuildDefs.RoomText(room);
			string text = "Place " + label + " blueprint ";
			if (anchor != null)
			{
				string anchorLabel = BuildDefs.Label(anchor.def, anchor.Stuff);
				if (spot.rel.valid)
				{
					text += spot.rel.WorldPhrase() + " " + anchorLabel;
				}
				else
				{
					CellRect rect = GenAdj.OccupiedRect(spot.cell, spot.rot, def.Size);
					text += BuildDefs.OffsetPhrase(anchor.OccupiedRect().CenterCell, rect.CenterCell) + " " + anchorLabel;
				}
				if (roomText.Length > 0)
				{
					text += " in " + roomText;
				}
				if (spot.habitCount > 0)
				{
					text += habitFromExamples
						? ", as in the example rooms"
						: ", the way you placed " + spot.habitCount + (spot.habitCount == 1 ? " other" : " others");
				}
			}
			else
			{
				text += roomText.Length > 0 ? "in " + roomText : "there";
				IntVec3 door = NearestDoor(map, room, spot.cell);
				if (door.IsValid)
				{
					text += ", " + BuildDefs.OffsetPhrase(door, spot.cell) + " the nearest door";
				}
			}
			if (storage != null && storage.isStorage)
			{
				text += " (storage priority " + storage.priority;
				if (storage.hasOnly)
				{
					List<string> names = new List<string>();
					foreach (ThingCategoryDef cat in storage.categories)
					{
						names.Add(StorageIndex.CategoryName(cat));
					}
					foreach (ThingDef item in storage.items)
					{
						names.Add(item.label.NullOrEmpty() ? item.defName : item.label);
					}
					text += "; holds only " + string.Join(", ", names.ToArray());
				}
				text += ")";
			}
			return text + ".";
		}

		private static IntVec3 NearestDoor(Map map, Room room, IntVec3 from)
		{
			IntVec3 best = IntVec3.Invalid;
			if (room == null || !room.ProperRoom)
			{
				return best;
			}
			int bestDist = int.MaxValue;
			foreach (IntVec3 c in room.BorderCellsCardinal)
			{
				if (c.GetDoor(map) == null)
				{
					continue;
				}
				int d = (c - from).LengthHorizontalSquared;
				if (d < bestDist)
				{
					bestDist = d;
					best = c;
				}
			}
			return best;
		}

		private static string UpperFirst(string s)
		{
			return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
		}

		// The build button's own steps (Designator_Build.DesignateSingleCell, RimWorld\Designator_Build.cs:455-546),
		// with two differences: always a blueprint (never the god-mode / zero-work instant build, :474-518),
		// and nothing is wiped first (GenSpawn.WipeExistingThings, :521), because the mod only places on
		// tiles with nothing built on them. Checked again here: the map may have changed while the player
		// was reading the plan.
		// throwIfRefused: one blueprint -> a Fault the order window shows; a whole layout -> false, and the
		// caller counts it.
		private static bool PlaceOne(Map map, ThingDef def, ThingDef stuff, IntVec3 cell, Rot4 rot, string label, StorageSetup storage, bool throwIfRefused)
		{
			if (Find.Maps == null || !Find.Maps.Contains(map))
			{
				throw new Fault("the map is no longer loaded");
			}
			AcceptanceReport report = GenConstruct.CanPlaceBlueprintAt(def, cell, rot, map, false, null, null, stuff);
			bool free = true;
			foreach (IntVec3 c in GenAdj.OccupiedRect(cell, rot, def.Size))
			{
				if (!c.InBounds(map) || !BuildDefs.FreeOfBuildings(c, map))
				{
					free = false;
					break;
				}
			}
			if (!report.Accepted || !free)
			{
				if (!throwIfRefused)
				{
					return false;
				}
				string reason = !report.Accepted && !report.Reason.NullOrEmpty() ? " (" + report.Reason.TrimEnd('.') + ")" : "";
				throw new Fault(label + " wasn't placed: its spot is no longer free" + reason);
			}

			// The research bench rule (ResearchBenchRule.cs, 2026-10-16): no bench without a researcher; one researcher's
			// bench moves here instead of a second one. A refusal of a single blueprint is the order's Fault.
			string benchNote;
			ResearchBenchRule.Outcome benchRule = ResearchBenchRule.Check(map, def, cell, rot, out benchNote);
			if (benchRule == ResearchBenchRule.Outcome.Blocked && throwIfRefused)
			{
				throw new Fault(benchNote);
			}
			ResearchBenchRule.Tell(benchNote);
			if (benchRule != ResearchBenchRule.Outcome.Place)
			{
				return benchRule == ResearchBenchRule.Outcome.Moved;
			}
			// The outdoor battery rule (BatteryOutdoorRule.cs, 2026-10-16): an outdoor battery goes to the nearest indoor
			// spot instead, or is not placed. Not placed for a single blueprint is the order's Fault.
			bool batteryMoved;
			string batteryNote;
			if (BatteryOutdoorRule.Check(map, def, stuff, ref cell, ref rot, out batteryMoved, out batteryNote))
			{
				if (!batteryMoved && throwIfRefused)
				{
					throw new Fault(batteryNote);
				}
				BatteryOutdoorRule.Tell(batteryNote);
				if (!batteryMoved)
				{
					return false;
				}
			}
			// GenConstruct.PlaceBlueprintForBuild (RimWorld\GenConstruct.cs:29-41).
			Blueprint_Build blueprint = GenConstruct.PlaceBlueprintForBuild(def, cell, map, rot, Faction.OfPlayer, stuff);
			// Brief 23: a later "those" sentence of the same order changes what was just placed.
			RecentBlueprints.Add(blueprint);

			// A storage blueprint carries its settings to the frame and the finished building
			// (RimWorld\Blueprint_Storage.cs:10,60-68,92-100; RimWorld\Frame.cs:329). Its Priority setter
			// notifies nothing for a blueprint owner (RimWorld\StorageSettings.cs:23-57).
			Blueprint_Storage storageBlueprint = blueprint as Blueprint_Storage;
			if (storage != null && storage.isStorage && storageBlueprint != null && storageBlueprint.settings != null)
			{
				StorageSettings settings = storageBlueprint.settings;
				settings.Priority = storage.priority;
				if (storage.hasOnly && settings.filter != null)
				{
					ThingFilter parent = def.building != null && def.building.fixedStorageSettings != null && def.building.fixedStorageSettings.filter != null
						? def.building.fixedStorageSettings.filter
						: StorageSettings.EverStorableFixedSettings().filter;
					// Same calls as set_storage's "replace" (Executor.ApplyStorageChange): special filters stay.
					List<SpecialThingFilterDef> keepSpecial = DefDatabase<SpecialThingFilterDef>.AllDefsListForReading;
					settings.filter.SetDisallowAll(null, keepSpecial);
					foreach (ThingCategoryDef cat in storage.categories)
					{
						HashSet<ThingDef> notHoldable = new HashSet<ThingDef>();
						foreach (ThingDef d in cat.DescendantThingDefs)
						{
							if (!StorageIndex.CanHold(parent, d))
							{
								notHoldable.Add(d);
							}
						}
						settings.filter.SetAllow(cat, true, notHoldable, keepSpecial);
					}
					foreach (ThingDef item in storage.items)
					{
						settings.filter.SetAllow(item, true);
					}
				}
			}

			// PlaceWorker.PostPlace for each place worker (RimWorld\Designator_Build.cs:539-545).
			if (def.PlaceWorkers != null)
			{
				for (int i = 0; i < def.PlaceWorkers.Count; i++)
				{
					def.PlaceWorkers[i].PostPlace(map, def, cell, rot);
				}
			}

			GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
			if (gc != null)
			{
				gc.RecordModPlaced(map, cell, def);
			}
			return true;
		}

		// ---- draw_plan ------------------------------------------------------------------------------

		public static Plan ResolveDrawPlan(JObject input, Snapshot snap)
		{
			Map map = LiveMap(snap);
			LayoutView view = LayoutMemory.ViewFor(map);
			string wanted = ((string)input["layout"] ?? "").Trim();
			SavedLayout layout = view.FindLayout(wanted);
			if (layout == null)
			{
				throw new Fault(view.layouts.Count == 0
					? "No plans were drawn: no room layout is known yet. Layouts are copied from rooms you have built."
					: "No plans were drawn: there is no saved layout for '" + wanted + "'.");
			}

			int wantedOptions = input["options"] != null && input["options"].Type == JTokenType.Integer ? (int)input["options"] : PlanOptions.MaxOptions;
			wantedOptions = Math.Max(1, Math.Min(PlanOptions.MaxOptions, wantedOptions));

			List<IntVec3> spots = PlanOptions.FindSpots(map, layout, wantedOptions);
			if (spots.Count == 0)
			{
				throw new Fault("No plans were drawn: there is no free ground near the colony big enough for that " + layout.roleLabel +
					" (" + layout.width + " by " + layout.height + " tiles with its walls).");
			}

			List<string> lines = new List<string>();
			for (int i = 0; i < spots.Count; i++)
			{
				lines.Add("Option " + (i + 1) + " is " + PlanOptions.DescribeSpot(map, layout, spots[i]));
			}

			Plan plan = new Plan();
			plan.toolName = DrawPlanTool;
			Write write = new Write();
			write.description = "Draw " + spots.Count + (spots.Count == 1 ? " plan option" : " plan options") + " for a " + layout.roleLabel +
				" (" + layout.ContentsText() + "; " + layout.width + " by " + layout.height + " tiles with walls). " +
				string.Join(". ", lines.ToArray()) + ".";
			string layoutJson = layout.ToJson().ToString(Newtonsoft.Json.Formatting.None);
			write.apply = delegate
			{
				if (Find.Maps == null || !Find.Maps.Contains(map))
				{
					throw new Fault("the map is no longer loaded");
				}
				GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
				if (gc == null)
				{
					throw new Fault("no game is currently loaded");
				}
				// Earlier options and their plans go first: only one set is open at a time.
				gc.ClearPendingOptions(map);
				for (int i = 0; i < spots.Count; i++)
				{
					PendingOption option = new PendingOption();
					option.number = i + 1;
					option.mapId = map.uniqueID;
					option.layoutJson = layoutJson;
					option.originX = spots[i].x;
					option.originZ = spots[i].z;
					PlanOptions.Draw(map, layout, option);
					gc.AddPendingOption(option);
				}
			};
			plan.writes.Add(write);
			plan.notes.Add("Nothing is built yet. Pick one with an order like \"build option 1\"; the plans are removed then.");
			if (spots.Count < wantedOptions)
			{
				plan.notes.Add("Only " + spots.Count + " free " + (spots.Count == 1 ? "spot was" : "spots were") + " found.");
			}
			return plan;
		}

		// ---- build_option ---------------------------------------------------------------------------

		private sealed class OptionPiece
		{
			public ThingDef def;
			public ThingDef stuff;
			public IntVec3 cell;
			public Rot4 rot;
			public int order;   // 0 walls and the like, 1 doors, 2 everything else, 3 things hung on walls
		}

		public static Plan ResolveBuildOption(JObject input, Snapshot snap)
		{
			Map map = LiveMap(snap);
			GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
			int number = input["option"] != null && input["option"].Type == JTokenType.Integer ? (int)input["option"] : 0;
			PendingOption option = null;
			List<PendingOption> open = gc != null ? gc.PendingOptionsFor(map) : new List<PendingOption>();
			foreach (PendingOption o in open)
			{
				if (o.number == number)
				{
					option = o;
				}
			}
			if (option == null)
			{
				throw new Fault(open.Count == 0
					? "Nothing was placed: no options are drawn on this map. Ask for the room first, then pick an option."
					: "Nothing was placed: there is no option " + number + ". The drawn options are " +
						string.Join(", ", open.Select((PendingOption o) => o.number.ToString()).ToArray()) + ".");
			}
			SavedLayout layout = option.Layout();
			if (layout == null)
			{
				throw new Fault("Nothing was placed: option " + number + " could not be read. Ask for the room again.");
			}

			Plan plan = new Plan();
			plan.toolName = BuildOptionTool;

			List<OptionPiece> pieces = new List<OptionPiece>();
			Dictionary<string, int> counts = new Dictionary<string, int>();
			List<string> countOrder = new List<string>();
			Dictionary<string, int> skipped = new Dictionary<string, int>();
			Dictionary<ThingDef, int> costs = new Dictionary<ThingDef, int>();
			// Pieces that still need an automatic material: how many per kind, in first-seen order.
			Dictionary<ThingDef, int> autoCount = new Dictionary<ThingDef, int>();
			List<ThingDef> autoOrder = new List<ThingDef>();
			// Stuff claimed so far by this option (saved materials and, later, the automatic choices).
			Dictionary<ThingDef, int> claimed = new Dictionary<ThingDef, int>();
			// def -> why it can't be built (null = it can).
			Dictionary<ThingDef, string> availability = new Dictionary<ThingDef, string>();
			foreach (LayoutThing t in layout.things)
			{
				ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(t.def);
				string unavailable;
				if (def == null || !BuildDefs.IsPlayerBuildable(def))
				{
					Bump(skipped, t.def + " (not in this game)");
					continue;
				}
				// Asked once per kind of building (a layout has dozens of the same wall).
				if (!availability.TryGetValue(def, out unavailable))
				{
					BuildDefs.IsAvailable(def, out unavailable);
					availability[def] = unavailable;
				}
				if (unavailable != null)
				{
					Bump(skipped, def.label + " (" + unavailable + ")");
					continue;
				}
				// A saved material that is still allowed is kept: the player built it that way (the example
				// rooms save no material, so they follow the automatic rule below). Pieces left without a
				// material get one per kind of building, once every piece is known.
				ThingDef stuff = null;
				if (def.MadeFromStuff)
				{
					ThingDef saved = t.stuff.NullOrEmpty() ? null : DefDatabase<ThingDef>.GetNamedSilentFail(t.stuff);
					if (saved != null && GenStuff.AllowedStuffsFor(def).Contains(saved))
					{
						stuff = saved;
						int already;
						claimed.TryGetValue(saved, out already);
						claimed[saved] = already + def.CostStuffCount;
					}
					else
					{
						int seen;
						autoCount.TryGetValue(def, out seen);
						autoCount[def] = seen + 1;
						if (seen == 0)
						{
							autoOrder.Add(def);
						}
					}
				}
				OptionPiece piece = new OptionPiece();
				piece.def = def;
				piece.stuff = stuff;
				piece.cell = new IntVec3(option.originX + t.x, 0, option.originZ + t.z);
				piece.rot = new Rot4(t.rot);
				// Things hung on a wall go last: they need their wall's blueprint to be there
				// (BuildingProperties.isAttachment, RimWorld\BuildingProperties.cs:160).
				bool attachment = def.building != null && def.building.isAttachment;
				piece.order = attachment ? 3 : (def.IsDoor ? 1 : (t.structural ? 0 : 2));
				pieces.Add(piece);
			}
			if (pieces.Count == 0)
			{
				throw new Fault("Nothing was placed: none of that layout's buildings can be built in this colony.");
			}
			// One material per kind of building for all its automatic pieces, so a room is not half wood and
			// half steel. The amount is what the whole layout needs of that kind.
			Dictionary<ThingDef, ThingDef> autoStuff = new Dictionary<ThingDef, ThingDef>();
			foreach (ThingDef autoDef in autoOrder)
			{
				BuildDefs.AutoStuff how;
				string stuffProblem;
				ThingDef chosen = BuildDefs.ResolveStuff(autoDef, "", map, autoDef.CostStuffCount * autoCount[autoDef], claimed, out how, out stuffProblem);
				autoStuff[autoDef] = chosen;
				string stuffNote = BuildDefs.AutoStuffNote(autoDef, chosen, how);
				if (stuffNote != null)
				{
					plan.notes.Add(stuffNote);
				}
			}
			foreach (OptionPiece piece in pieces)
			{
				if (piece.def.MadeFromStuff && piece.stuff == null)
				{
					autoStuff.TryGetValue(piece.def, out piece.stuff);
				}
				string label = BuildDefs.Label(piece.def, piece.stuff);
				if (!counts.ContainsKey(label))
				{
					countOrder.Add(label);
				}
				Bump(counts, label);
				BuildDefs.AddCosts(costs, piece.def, piece.stuff, 1);
			}
			// Walls first, then doors, then the furniture, then wall fittings, so each piece is checked against
			// what is already there.
			pieces = pieces.OrderBy((OptionPiece p) => p.order).ToList();
			AgentWindCheck.Note(plan, map, pieces.Select((OptionPiece p) => new AgentWindCheck.Placed(p.def, p.cell, p.rot)).ToList());
			AgentLightCheck.Note(plan, map, pieces.Select((OptionPiece p) => new AgentWindCheck.Placed(p.def, p.cell, p.rot)).ToList());
			AgentWorkspotCheck.Note(plan, map, pieces.Select((OptionPiece p) => new AgentWindCheck.Placed(p.def, p.cell, p.rot)).ToList());
			AgentDoorCheck.Note(plan, map, pieces.Select((OptionPiece p) => new AgentWindCheck.Placed(p.def, p.cell, p.rot)).ToList());
			AgentCoolerCheck.Note(plan, map, pieces.Select((OptionPiece p) => new AgentWindCheck.Placed(p.def, p.cell, p.rot)).ToList());
			AgentVentCheck.Note(plan, map, pieces.Select((OptionPiece p) => new AgentWindCheck.Placed(p.def, p.cell, p.rot)).ToList());

			List<string> parts = new List<string>();
			foreach (string label in countOrder)
			{
				parts.Add(counts[label] + "x " + label);
			}
			Write write = new Write();
			write.description = "Place blueprints for option " + number + ", a " + layout.roleLabel + " " +
				PlanOptions.DescribeSpot(map, layout, option.Origin) + ": " + string.Join(", ", parts.ToArray()) + ". The option plans are then removed.";
			write.apply = delegate
			{
				if (Find.Maps == null || !Find.Maps.Contains(map))
				{
					throw new Fault("the map is no longer loaded");
				}
				int placed = 0;
				foreach (OptionPiece piece in pieces)
				{
					try
					{
						if (PlaceOne(map, piece.def, piece.stuff, piece.cell, piece.rot, null, null, false))
						{
							placed++;
						}
					}
					catch (Exception ex)
					{
						Log.Warning(LogPrefix + "a blueprint of option " + number + " could not be placed: " + ex.Message);
					}
				}
				GameComponent_VerbalCommands gcApply = GameComponent_VerbalCommands.Instance;
				if (placed == 0)
				{
					throw new Fault("option " + number + " was not built: its ground is no longer free");
				}
				if (gcApply != null)
				{
					gcApply.ClearPendingOptions(map);
				}
				if (placed < pieces.Count)
				{
					// Messages.Message(string, MessageTypeDef, bool) (Verse\Messages.cs:47).
					Messages.Message("VerbalCommands_BuildSomeSkipped".Translate(pieces.Count - placed, pieces.Count), MessageTypeDefOf.RejectInput, false);
				}
			};
			plan.writes.Add(write);

			if (costs.Count > 0)
			{
				plan.notes.Add("Materials: " + BuildDefs.CostText(map, costs) + ".");
			}
			foreach (KeyValuePair<string, int> kv in skipped)
			{
				plan.notes.Add("Left out: " + kv.Value + "x " + kv.Key + ".");
			}
			return plan;
		}

		private static void Bump(Dictionary<string, int> counts, string key)
		{
			int n;
			counts.TryGetValue(key, out n);
			counts[key] = n + 1;
		}
	}
}
