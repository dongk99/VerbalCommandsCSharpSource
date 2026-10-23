using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Brief 21: what the mod learns from the way the player builds, and the file it is kept in.
	//
	// User, 2026-09-27T23:57:47Z: "have it so that the placement tooling itself learns (saving pattern data,
	// treat one pattern in a set cell where furnitures are placed in specific order but does not have
	// repeating patterns, exception being bed+endtable+bed ... or bed+endtable (placed above bed, or below
	// bed) from how the person place shelving/workbenches".
	// User, 2026-09-28T00:01:54Z: "a single example is acceptable as a pattern"; a room order needs a room
	// that "must already exist", or example layouts.
	// User, 2026-09-28T00:07:59Z: "the router can be \"smart\" and save those layout, just like it would
	// save/read layout of existing rooms."
	//
	// Two things are learned, both by reading the map (nothing is changed on it):
	//   habits   pairs of touching buildings of different kinds, as a LayoutRelation (Placement.cs), counted
	//            per pair of kinds ("workbench>storage"). Two of the same building side by side are a row,
	//            not a habit, so they are skipped; bed + end table + bed is two bed>end table pairs.
	//   layouts  every named room: its walls, doors and everything built in it, tile by tile.
	// Both are stored per colony in one file next to router_model.txt, so habits and layouts from earlier
	// colonies are still known in a new one. A colony's entry is replaced each time its map is read, so
	// nothing is counted twice.
	//
	// MAIN THREAD only (game objects, and GenFilePaths.SaveDataFolderPath).

	public sealed class LayoutThing
	{
		public string def;
		public string stuff;     // "" = none / the default
		public int x;            // offset from the layout's south-west corner
		public int z;
		public int rot;
		public bool structural;  // wall, door, fence, conduit (BuildDefs.IsStructural)

		public JObject ToJson()
		{
			JObject o = new JObject();
			o["def"] = def;
			o["stuff"] = stuff ?? "";
			o["x"] = x;
			o["z"] = z;
			o["rot"] = rot;
			o["structural"] = structural;
			return o;
		}

		public static LayoutThing FromJson(JObject o)
		{
			LayoutThing t = new LayoutThing();
			t.def = (string)o["def"];
			t.stuff = (string)o["stuff"] ?? "";
			t.x = (int?)o["x"] ?? 0;
			t.z = (int?)o["z"] ?? 0;
			t.rot = (int?)o["rot"] ?? 0;
			t.structural = (bool?)o["structural"] ?? false;
			return t.def.NullOrEmpty() ? null : t;
		}
	}

	public sealed class SavedLayout
	{
		public string id;          // role defName + "-" + 6 hex digits of the layout's hash; stable
		public string roleDefName;
		public string roleLabel;   // "bedroom"
		public string from;        // the room it was read from, as the game labelled it then
		public int width;          // walls included
		public int height;
		public List<LayoutThing> things = new List<LayoutThing>();
		public bool fromThisColony;

		// "1x bed, 2x end table": what stands in the room, walls and doors left out.
		public string ContentsText()
		{
			Dictionary<string, int> counts = new Dictionary<string, int>();
			List<string> order = new List<string>();
			foreach (LayoutThing t in things)
			{
				if (t.structural)
				{
					continue;
				}
				ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(t.def);
				string label = def != null && !def.label.NullOrEmpty() ? def.label : t.def;
				int n;
				if (!counts.TryGetValue(label, out n))
				{
					order.Add(label);
				}
				counts[label] = n + 1;
			}
			if (order.Count == 0)
			{
				return "nothing but walls";
			}
			List<string> parts = new List<string>();
			foreach (string label in order)
			{
				parts.Add(counts[label] + "x " + label);
			}
			return string.Join(", ", parts.ToArray());
		}

		public JObject ToJson()
		{
			JObject o = new JObject();
			o["id"] = id;
			o["role"] = roleDefName;
			o["role_label"] = roleLabel;
			o["from"] = from;
			o["w"] = width;
			o["h"] = height;
			JArray arr = new JArray();
			foreach (LayoutThing t in things)
			{
				arr.Add(t.ToJson());
			}
			o["things"] = arr;
			return o;
		}

		public static SavedLayout FromJson(JObject o)
		{
			if (o == null)
			{
				return null;
			}
			SavedLayout l = new SavedLayout();
			l.id = (string)o["id"];
			l.roleDefName = (string)o["role"] ?? "";
			l.roleLabel = (string)o["role_label"] ?? "";
			l.from = (string)o["from"] ?? "";
			l.width = (int?)o["w"] ?? 0;
			l.height = (int?)o["h"] ?? 0;
			JArray arr = o["things"] as JArray;
			if (arr != null)
			{
				foreach (JToken tok in arr)
				{
					LayoutThing t = tok is JObject ? LayoutThing.FromJson((JObject)tok) : null;
					if (t != null)
					{
						l.things.Add(t);
					}
				}
			}
			return l.id.NullOrEmpty() || l.width <= 0 || l.height <= 0 || l.things.Count == 0 ? null : l;
		}
	}

	// Everything known right now: this colony's map plus every other colony in the file.
	public sealed class LayoutView
	{
		public int mapId = -1;
		// "workbench>storage" -> relation key -> times seen.
		public Dictionary<string, Dictionary<string, int>> habits = new Dictionary<string, Dictionary<string, int>>();
		// "workbench>storage|s3|0|0" -> "shelf next to electric stove".
		public Dictionary<string, string> examples = new Dictionary<string, string>();
		// This colony's layouts first.
		public List<SavedLayout> layouts = new List<SavedLayout>();

		// The same, from the example rooms shipped with the mod (ExampleRooms.cs). Only used for a pair
		// the player's own colonies show nothing for.
		public Dictionary<string, Dictionary<string, int>> exampleHabits = new Dictionary<string, Dictionary<string, int>>();

		// The player's own habits for the pair when there are any, else the example rooms'.
		public Dictionary<string, int> HabitsFor(string anchorKind, string otherKind, out bool fromExamples)
		{
			fromExamples = false;
			Dictionary<string, int> found;
			if (habits.TryGetValue(anchorKind + ">" + otherKind, out found) && found.Count > 0)
			{
				return found;
			}
			if (exampleHabits.TryGetValue(anchorKind + ">" + otherKind, out found) && found.Count > 0)
			{
				fromExamples = true;
				return found;
			}
			return null;
		}

		public SavedLayout FindLayout(string idOrRoomType)
		{
			string wanted = (idOrRoomType ?? "").Trim();
			if (wanted.Length == 0)
			{
				return null;
			}
			foreach (SavedLayout l in layouts)
			{
				if (string.Equals(l.id, wanted, StringComparison.OrdinalIgnoreCase))
				{
					return l;
				}
			}
			// A room type instead of an id: the first layout of that type (this colony's come first).
			foreach (SavedLayout l in layouts)
			{
				if (string.Equals(l.roleLabel, wanted, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(l.roleDefName, wanted, StringComparison.OrdinalIgnoreCase))
				{
					return l;
				}
			}
			return null;
		}
	}

	public static class LayoutMemory
	{
		private const string LogPrefix = "[VerbalCommands] layouts: ";
		public const string FileName = "layout_patterns.json";
		private const int FileVersion = 1;

		// A room larger than this is not saved as a layout (walls included). Not user values: they keep
		// the file and the plan drawing small.
		private const int MaxLayoutCells = 400;
		private const int MaxLayoutSide = 30;

		private static LayoutView current;

		// Same folder as router_model.txt (Router.RouterModelFilePath): a sibling of Config, never inside it.
		public static string FilePath()
		{
			return Path.Combine(GenFilePaths.SaveDataFolderPath, "VerbalCommands", FileName);
		}

		// The view made by the last Refresh for this map; reads the map again when there is none.
		public static LayoutView ViewFor(Map map)
		{
			if (current != null && map != null && current.mapId == map.uniqueID)
			{
				return current;
			}
			return Refresh(map);
		}

		// Reads the map, replaces this colony's entry in the file (written only when it changed), and
		// returns everything known. Never throws: a file problem only means "this map's own data".
		public static LayoutView Refresh(Map map)
		{
			LayoutView view = new LayoutView();
			if (map == null)
			{
				return view;
			}
			view.mapId = map.uniqueID;

			JObject mine = new JObject();
			try
			{
				mine = ScanColony(map);
			}
			catch (Exception ex)
			{
				Log.Warning(LogPrefix + "reading the map failed: " + ex);
			}

			JObject root = null;
			string path = null;
			try
			{
				path = FilePath();
				root = ReadFile(path);
			}
			catch (Exception ex)
			{
				Log.WarningOnce(LogPrefix + "the layout file could not be read: " + ex.Message, 81200411);
			}
			if (root == null)
			{
				root = new JObject();
			}
			JObject colonies = root["colonies"] as JObject;
			if (colonies == null)
			{
				colonies = new JObject();
				root["colonies"] = colonies;
			}
			root["version"] = FileVersion;

			string key = ColonyKey(map);
			JToken before = colonies[key];
			bool changed = before == null || !JToken.DeepEquals(before, mine);
			colonies[key] = mine;
			if (changed && path != null)
			{
				try
				{
					Directory.CreateDirectory(Path.GetDirectoryName(path));
					File.WriteAllText(path, root.ToString(Formatting.Indented), new UTF8Encoding(false));
				}
				catch (Exception ex)
				{
					Log.WarningOnce(LogPrefix + "the layout file could not be written: " + ex.Message, 81200412);
				}
			}

			// This colony first, so its layouts win a tie and come first in the list.
			MergeColony(view, mine, true);
			foreach (KeyValuePair<string, JToken> kv in colonies)
			{
				if (kv.Key != key && kv.Value is JObject)
				{
					MergeColony(view, (JObject)kv.Value, false);
				}
			}
			// The example rooms shipped with the mod, last: the player's own colonies come first.
			ExampleRooms.AddTo(view);
			current = view;
			return view;
		}

		private static JObject ReadFile(string path)
		{
			if (!File.Exists(path))
			{
				return null;
			}
			try
			{
				return JObject.Parse(File.ReadAllText(path));
			}
			catch (JsonException)
			{
				// A damaged file is started again; this colony's part is rebuilt from the map anyway.
				Log.WarningOnce(LogPrefix + FileName + " was not readable and is started again.", 81200413);
				return null;
			}
		}

		// World seed + the world's own random value + the map's id (RimWorld.Planet\WorldInfo.cs:12,14;
		// Verse\Map.cs:21): the same colony gives the same key in every save of it.
		private static string ColonyKey(Map map)
		{
			string world = "world";
			try
			{
				world = Find.World.info.seedString + "_" + Find.World.info.persistentRandomValue;
			}
			catch (Exception)
			{
			}
			return world + "_map" + map.uniqueID;
		}

		private static void MergeColony(LayoutView view, JObject colony, bool thisColony)
		{
			JObject habits = colony["habits"] as JObject;
			if (habits != null)
			{
				foreach (KeyValuePair<string, JToken> pair in habits)
				{
					JObject keys = pair.Value as JObject;
					if (keys == null)
					{
						continue;
					}
					Dictionary<string, int> into;
					if (!view.habits.TryGetValue(pair.Key, out into))
					{
						into = new Dictionary<string, int>();
						view.habits[pair.Key] = into;
					}
					foreach (KeyValuePair<string, JToken> kv in keys)
					{
						int count = kv.Value != null && kv.Value.Type == JTokenType.Integer ? (int)kv.Value : 0;
						if (count <= 0)
						{
							continue;
						}
						int have;
						into.TryGetValue(kv.Key, out have);
						into[kv.Key] = have + count;
					}
				}
			}
			JObject examples = colony["examples"] as JObject;
			if (examples != null)
			{
				foreach (KeyValuePair<string, JToken> kv in examples)
				{
					if (!view.examples.ContainsKey(kv.Key) && kv.Value != null && kv.Value.Type == JTokenType.String)
					{
						view.examples[kv.Key] = (string)kv.Value;
					}
				}
			}
			JArray layouts = colony["layouts"] as JArray;
			if (layouts != null)
			{
				foreach (JToken tok in layouts)
				{
					SavedLayout l = SavedLayout.FromJson(tok as JObject);
					if (l == null || view.layouts.Any((SavedLayout have) => have.id == l.id))
					{
						continue;
					}
					l.fromThisColony = thisColony;
					view.layouts.Add(l);
				}
			}
		}

		// ---- reading the map -----------------------------------------------------------------------

		private static JObject ScanColony(Map map)
		{
			GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
			if (gc != null)
			{
				gc.ForgetGoneModPlaced(map);
			}

			JObject colony = new JObject();
			JObject habits = new JObject();
			JObject examples = new JObject();

			// ListerBuildings.allBuildingsColonist: the player's buildings (Verse\ListerBuildings.cs:68).
			List<Building> buildings = map.listerBuildings.allBuildingsColonist;
			HashSet<Thing> neighbours = new HashSet<Thing>();
			for (int i = 0; i < buildings.Count; i++)
			{
				Building anchor = buildings[i];
				if (!Learnable(anchor, map, gc))
				{
					continue;
				}
				neighbours.Clear();
				CellRect around = anchor.OccupiedRect().ExpandedBy(1);
				around.ClipInsideMap(map);
				foreach (IntVec3 c in around)
				{
					List<Thing> things = c.GetThingList(map);
					for (int t = 0; t < things.Count; t++)
					{
						Thing other = things[t];
						// The same building twice in a row is a row, not a habit.
						if (other != anchor && other.def != anchor.def && Learnable(other, map, gc))
						{
							neighbours.Add(other);
						}
					}
				}
				foreach (Thing other in neighbours)
				{
					LayoutRelation rel = LayoutRelation.Between(anchor.def, anchor.Position, anchor.Rotation, other.def, other.Position, other.Rotation);
					if (!rel.valid)
					{
						continue;
					}
					string pair = BuildDefs.KindOf(anchor.def) + ">" + BuildDefs.KindOf(other.def);
					JObject keys = habits[pair] as JObject;
					if (keys == null)
					{
						keys = new JObject();
						habits[pair] = keys;
					}
					string key = rel.Key;
					keys[key] = ((int?)keys[key] ?? 0) + 1;
					string exampleKey = pair + "|" + key;
					if (examples[exampleKey] == null)
					{
						examples[exampleKey] = other.def.label + " next to " + anchor.def.label;
					}
				}
			}
			colony["habits"] = SortedObject(habits);
			colony["examples"] = SortedObject(examples);

			JArray layouts = new JArray();
			HashSet<string> seenIds = new HashSet<string>();
			foreach (Room room in StorageIndex.AllProperRooms(map))
			{
				try
				{
					SavedLayout layout = CaptureRoom(map, room);
					if (layout != null && seenIds.Add(layout.id))
					{
						layouts.Add(layout.ToJson());
					}
				}
				catch (Exception ex)
				{
					Log.WarningOnce(LogPrefix + "a room could not be read: " + ex.Message, ("vc_layout_room_" + room.ID).GetHashCode());
				}
			}
			colony["layouts"] = new JArray(layouts.OrderBy((JToken l) => (string)l["id"], StringComparer.Ordinal));
			return colony;
		}

		// Key order would otherwise follow the order buildings were made in, and the file would be
		// rewritten for no reason.
		private static JObject SortedObject(JObject o)
		{
			JObject sorted = new JObject();
			foreach (JProperty p in o.Properties().OrderBy((JProperty x) => x.Name, StringComparer.Ordinal))
			{
				sorted[p.Name] = p.Value is JObject ? SortedObject((JObject)p.Value) : p.Value;
			}
			return sorted;
		}

		// Furniture the player placed: a player building from the build menu, not part of a room's shell,
		// and not one the mod placed itself (so the mod never learns from its own choices; the reading of
		// the user's "unless they placed one" in brief 16, kept here).
		private static bool Learnable(Thing thing, Map map, GameComponent_VerbalCommands gc)
		{
			if (thing == null || !thing.Spawned || thing.def.category != ThingCategory.Building || thing.Faction != Faction.OfPlayer)
			{
				return false;
			}
			if (!BuildDefs.IsPlayerBuildable(thing.def) || BuildDefs.IsStructural(thing.def))
			{
				return false;
			}
			return gc == null || !gc.IsModPlaced(map, thing.Position, thing.def);
		}

		// One named room -> a layout. The shell is the ring of tiles around the room's tiles, corners
		// included (Room.BorderCells, Verse\Room.cs:305-325).
		private static SavedLayout CaptureRoom(Map map, Room room)
		{
			if (room == null || StorageIndex.IsGenericRoomRole(room.Role) || room.CellCount > MaxLayoutCells)
			{
				return null;
			}
			HashSet<IntVec3> inside = new HashSet<IntVec3>(room.Cells);
			HashSet<IntVec3> shell = new HashSet<IntVec3>();
			foreach (IntVec3 c in room.BorderCells)
			{
				if (c.InBounds(map))
				{
					shell.Add(c);
				}
			}
			int minX = int.MaxValue;
			int minZ = int.MaxValue;
			int maxX = int.MinValue;
			int maxZ = int.MinValue;
			foreach (IntVec3 c in inside.Concat(shell))
			{
				minX = Math.Min(minX, c.x);
				minZ = Math.Min(minZ, c.z);
				maxX = Math.Max(maxX, c.x);
				maxZ = Math.Max(maxZ, c.z);
			}
			int width = maxX - minX + 1;
			int height = maxZ - minZ + 1;
			if (width > MaxLayoutSide || height > MaxLayoutSide)
			{
				return null;
			}

			SavedLayout layout = new SavedLayout();
			layout.roleDefName = room.Role.defName;
			layout.roleLabel = room.Role.label.NullOrEmpty() ? room.Role.defName : room.Role.label;
			layout.from = BuildDefs.RoomText(room);
			layout.width = width;
			layout.height = height;

			HashSet<Thing> seen = new HashSet<Thing>();
			foreach (IntVec3 c in shell)
			{
				Building edifice = c.GetEdifice(map);
				if (edifice == null)
				{
					continue;
				}
				if (edifice.Faction == Faction.OfPlayer && BuildDefs.IsPlayerBuildable(edifice.def))
				{
					if (seen.Add(edifice))
					{
						layout.things.Add(ThingEntry(edifice, minX, minZ));
					}
				}
				else if (edifice.def.passability == Traversability.Impassable)
				{
					// Rock, or a wall nobody in the colony built: a plain wall stands in for it when the
					// layout is built somewhere else (ThingDefOf.Wall, its default stuff).
					LayoutThing wall = new LayoutThing();
					wall.def = ThingDefOf.Wall.defName;
					wall.stuff = "";
					wall.x = c.x - minX;
					wall.z = c.z - minZ;
					wall.rot = 0;
					wall.structural = true;
					layout.things.Add(wall);
				}
			}
			foreach (IntVec3 c in inside)
			{
				List<Thing> things = c.GetThingList(map);
				for (int i = 0; i < things.Count; i++)
				{
					Thing t = things[i];
					if (t.def.category == ThingCategory.Building && t.Faction == Faction.OfPlayer && BuildDefs.IsPlayerBuildable(t.def) && seen.Add(t))
					{
						layout.things.Add(ThingEntry(t, minX, minZ));
					}
				}
			}
			if (layout.things.Count == 0)
			{
				return null;
			}

			layout.things = layout.things
				.OrderBy((LayoutThing t) => t.z).ThenBy((LayoutThing t) => t.x)
				.ThenBy((LayoutThing t) => t.def, StringComparer.Ordinal).ToList();
			StringBuilder sb = new StringBuilder(layout.roleDefName);
			foreach (LayoutThing t in layout.things)
			{
				sb.Append(';').Append(t.def).Append(',').Append(t.stuff).Append(',').Append(t.x).Append(',').Append(t.z).Append(',').Append(t.rot);
			}
			layout.id = layout.roleDefName + "-" + StableHash(sb.ToString()).ToString("x8").Substring(0, 6);
			return layout;
		}

		private static LayoutThing ThingEntry(Thing t, int minX, int minZ)
		{
			LayoutThing e = new LayoutThing();
			e.def = t.def.defName;
			e.stuff = t.Stuff != null ? t.Stuff.defName : "";
			e.x = t.Position.x - minX;
			e.z = t.Position.z - minZ;
			e.rot = t.Rotation.AsInt;
			e.structural = BuildDefs.IsStructural(t.def);
			return e;
		}

		// FNV-1a, 32 bit: the same text gives the same number on every machine and run (string.GetHashCode
		// does not promise that).
		private static uint StableHash(string s)
		{
			uint hash = 2166136261;
			foreach (char c in s)
			{
				hash ^= c;
				hash *= 16777619;
			}
			return hash;
		}
	}
}
