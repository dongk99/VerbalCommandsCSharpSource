using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Brief 21: the example rooms shipped with the mod, as text the MOD reads (not the model).
	//
	// User, 2026-10-01: "you describe how benches are placed in text, save that as example, in respect to
	// room size, patterns, etc. the mod/interpreter does the actual building." and "mod should be reading
	// the text and then place furnitures/buildings by itself. the agent only does prompting."
	//
	// File: <mod folder>\Layouts\example_rooms.txt. One block per room:
	//
	//     room: barracks
	//     b = bed, head north
	//     e = end table
	//     grid:
	//     #####
	//     #be.+
	//     #b..#
	//     #####
	//     end
	//
	//   - The grid is the room seen from above, north at the top, one character per tile.
	//     '#' wall, '+' door, '.' free floor; every other character is a letter from the lines above it.
	//   - "letter = name" : the game's name for the building ("end table") or its defName.
	//   - ", head north" / ", worker south" : the side the bed's head, or the tile the colonist stands on,
	//     points to. Left out, the shape in the grid decides which way the building lies.
	//   - A building bigger than one tile fills all its tiles with its letter. Several of the same letter
	//     side by side are cut into buildings of that size, from the top left.
	//   - Lines starting with "//" are notes.
	//
	// Each room becomes a SavedLayout (so "build room like kitchen" works in a colony that has no kitchen
	// yet), and every pair of touching buildings of different kinds in it becomes an example habit, used
	// by place_blueprint only where the player's own colonies show no habit for that pair.
	// A room that can't be read (unknown name, a shape that doesn't fit the building) is left out with
	// one warning; the others still load. MAIN THREAD only (DefDatabase).
	public static class ExampleRooms
	{
		private const string LogPrefix = "[VerbalCommands] example rooms: ";
		public const string FileName = "example_rooms.txt";

		public static string FilePath()
		{
			string root = VerbalCommandsMod.ModRootDir;
			return root.NullOrEmpty() ? null : Path.Combine(root, "Layouts", FileName);
		}

		private sealed class LegendEntry
		{
			public ThingDef def;
			public bool hasSide;
			public Rot4 rot;
		}

		// Adds the example layouts and example habits to the view. Never throws.
		public static void AddTo(LayoutView view)
		{
			try
			{
				string path = FilePath();
				if (path == null || !File.Exists(path))
				{
					return;
				}
				string[] lines = File.ReadAllLines(path);
				int i = 0;
				int number = 0;
				while (i < lines.Length)
				{
					string line = lines[i].Trim();
					if (!line.StartsWith("room:", StringComparison.OrdinalIgnoreCase))
					{
						i++;
						continue;
					}
					string name = line.Substring(5).Trim();
					int start = i;
					i++;
					number++;
					string problem;
					SavedLayout layout = ReadRoom(lines, ref i, name, number, out problem);
					if (layout == null)
					{
						Log.WarningOnce(LogPrefix + "'" + name + "' (line " + (start + 1) + ") was left out: " + problem,
							("vc_example_room_" + number + "_" + name).GetHashCode());
						continue;
					}
					if (!view.layouts.Exists((SavedLayout have) => have.id == layout.id))
					{
						view.layouts.Add(layout);
					}
					AddHabits(view, layout);
				}
			}
			catch (Exception ex)
			{
				Log.WarningOnce(LogPrefix + FileName + " could not be read: " + ex.Message, 81200416);
			}
		}

		private static SavedLayout ReadRoom(string[] lines, ref int i, string name, int number, out string problem)
		{
			problem = null;
			Dictionary<char, LegendEntry> legend = new Dictionary<char, LegendEntry>();
			List<string> grid = null;
			bool closed = false;
			while (i < lines.Length)
			{
				string raw = lines[i];
				string line = raw.Trim();
				if (grid == null)
				{
					if (line.StartsWith("room:", StringComparison.OrdinalIgnoreCase))
					{
						break;
					}
					i++;
					if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
					{
						continue;
					}
					if (line.Equals("grid:", StringComparison.OrdinalIgnoreCase))
					{
						grid = new List<string>();
						continue;
					}
					if (problem == null)
					{
						problem = ReadLegendLine(line, legend);
					}
					continue;
				}
				i++;
				if (line.Equals("end", StringComparison.OrdinalIgnoreCase))
				{
					closed = true;
					break;
				}
				if (line.Length > 0)
				{
					grid.Add(line);
				}
			}
			if (problem != null)
			{
				return null;
			}
			if (name.Length == 0)
			{
				problem = "it has no name";
				return null;
			}
			if (grid == null || grid.Count == 0 || !closed)
			{
				problem = "it has no grid, or the grid has no \"end\" line";
				return null;
			}
			int height = grid.Count;
			int width = grid[0].Length;
			foreach (string row in grid)
			{
				if (row.Length != width)
				{
					problem = "its grid lines are not all the same length";
					return null;
				}
			}

			SavedLayout layout = new SavedLayout();
			layout.roleLabel = name;
			layout.roleDefName = RoleDefNameFor(name);
			layout.from = "example";
			layout.width = width;
			layout.height = height;
			layout.fromThisColony = false;
			layout.id = "example-" + number + "-" + name.Replace(' ', '-').ToLowerInvariant();

			// taken[x, z]: tiles already turned into a building. z counts from the bottom line (south).
			bool[,] taken = new bool[width, height];
			for (int row = 0; row < height; row++)
			{
				for (int x = 0; x < width; x++)
				{
					int z = height - 1 - row;
					if (taken[x, z])
					{
						continue;
					}
					char c = grid[row][x];
					if (c == '.')
					{
						continue;
					}
					if (c == '#' || c == '+')
					{
						// ThingDefOf.Wall / ThingDefOf.Door (RimWorld\ThingDefOf.cs).
						LayoutThing shell = new LayoutThing();
						shell.def = (c == '#' ? ThingDefOf.Wall : ThingDefOf.Door).defName;
						shell.stuff = "";
						shell.x = x;
						shell.z = z;
						shell.rot = 0;
						shell.structural = true;
						layout.things.Add(shell);
						taken[x, z] = true;
						continue;
					}
					LegendEntry entry;
					if (!legend.TryGetValue(c, out entry))
					{
						problem = "the grid uses '" + c + "', which no line above it explains";
						return null;
					}
					LayoutThing thing = TakeBuilding(grid, taken, entry, c, x, row, width, height);
					if (thing == null)
					{
						problem = "the tiles marked '" + c + "' don't fit a " + entry.def.label + " (" + entry.def.Size.x + " by " + entry.def.Size.z + ")";
						return null;
					}
					layout.things.Add(thing);
				}
			}
			return layout;
		}

		// "b = bed, head north"
		private static string ReadLegendLine(string line, Dictionary<char, LegendEntry> legend)
		{
			int eq = line.IndexOf('=');
			if (eq < 1 || line.Substring(0, eq).Trim().Length != 1)
			{
				return "the line \"" + line + "\" is not \"letter = building\"";
			}
			char letter = line.Substring(0, eq).Trim()[0];
			if (letter == '#' || letter == '+' || letter == '.')
			{
				return "'" + letter + "' already means wall, door or floor";
			}
			string rest = line.Substring(eq + 1).Trim();
			string name = rest;
			string side = null;
			int comma = rest.IndexOf(',');
			if (comma >= 0)
			{
				name = rest.Substring(0, comma).Trim();
				side = rest.Substring(comma + 1).Trim().ToLowerInvariant();
			}
			List<ThingDef> defs = BuildDefs.FindBuildables(name);
			if (defs.Count != 1)
			{
				return defs.Count == 0 ? "there is no building called '" + name + "'" : "'" + name + "' could mean several buildings";
			}
			LegendEntry entry = new LegendEntry();
			entry.def = defs[0];
			if (side != null)
			{
				// "head north" / "worker south": the last word is the direction.
				string[] words = side.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
				int dir = words.Length == 2 && (words[0] == "head" || words[0] == "worker") ? DirectionOf(words[1]) : -1;
				if (dir < 0)
				{
					return "\"" + side + "\" is not \"head\" or \"worker\" followed by north, east, south or west";
				}
				// The head of a bed and the standing tile of a bench are both on the side opposite to the
				// way the building is rotated: a bed rotated north has its head at the south end
				// (BedUtility.GetSlotPos, RimWorld\BedUtility.cs:22-29), and a bench's standing tile is
				// interactionCellOffset (0,0,-1) turned by its rotation (ThingUtility.InteractionCell,
				// Verse\ThingUtility.cs:55-58; Core Defs: every vanilla workbench uses (0,0,-1)).
				entry.hasSide = true;
				entry.rot = new Rot4((dir + 2) % 4);
			}
			legend[letter] = entry;
			return null;
		}

		private static int DirectionOf(string word)
		{
			switch (word)
			{
				case "north": return 0;
				case "east": return 1;
				case "south": return 2;
				case "west": return 3;
				default: return -1;
			}
		}

		// Cuts one building of the letter's size out of the grid, its top left tile at (x, row).
		private static LayoutThing TakeBuilding(List<string> grid, bool[,] taken, LegendEntry entry, char letter, int x, int row, int width, int height)
		{
			IntVec2 size = entry.def.Size;
			// (tiles wide, tiles tall, rotation) to try, in order.
			List<Rot4> rotations = new List<Rot4>();
			// A building that can't be turned is always saved with rotation 0, so a side given for it is ignored.
			if (entry.hasSide && entry.def.rotatable)
			{
				rotations.Add(entry.rot);
			}
			else
			{
				rotations.Add(Rot4.North);
				if (entry.def.rotatable && size.x != size.z)
				{
					rotations.Add(Rot4.East);
				}
			}
			foreach (Rot4 rot in rotations)
			{
				int w = rot.IsHorizontal ? size.z : size.x;
				int h = rot.IsHorizontal ? size.x : size.z;
				if (x + w > width || row + h > height)
				{
					continue;
				}
				bool fits = true;
				for (int dx = 0; dx < w && fits; dx++)
				{
					for (int dr = 0; dr < h; dr++)
					{
						if (grid[row + dr][x + dx] != letter || taken[x + dx, height - 1 - (row + dr)])
						{
							fits = false;
							break;
						}
					}
				}
				if (!fits)
				{
					continue;
				}
				int minZ = height - 1 - (row + h - 1);
				CellRect rect = new CellRect(x, minZ, w, h);
				// The building's own tile (Thing.Position) is the one for which the game's footprint
				// (GenAdj.OccupiedRect, Verse\GenAdj.cs:595-599) is exactly these tiles.
				foreach (IntVec3 c in rect)
				{
					CellRect made = GenAdj.OccupiedRect(c, rot, size);
					if (made.minX == rect.minX && made.minZ == rect.minZ && made.maxX == rect.maxX && made.maxZ == rect.maxZ)
					{
						foreach (IntVec3 t in rect)
						{
							taken[t.x, t.z] = true;
						}
						LayoutThing thing = new LayoutThing();
						thing.def = entry.def.defName;
						thing.stuff = "";
						thing.x = c.x;
						thing.z = c.z;
						thing.rot = entry.def.rotatable ? rot.AsInt : 0;
						thing.structural = BuildDefs.IsStructural(entry.def);
						return thing;
					}
				}
			}
			return null;
		}

		// The game's room type with that label, when there is one ("kitchen" -> Kitchen), so a layout is
		// found by either name.
		private static string RoleDefNameFor(string name)
		{
			foreach (RoomRoleDef role in DefDatabase<RoomRoleDef>.AllDefsListForReading)
			{
				if (role.label != null && string.Equals(role.label.Trim(), name, StringComparison.OrdinalIgnoreCase))
				{
					return role.defName;
				}
			}
			return name;
		}

		// Touching pairs of different kinds in the example room -> example habits, the same way
		// LayoutMemory learns the player's from the map.
		private static void AddHabits(LayoutView view, SavedLayout layout)
		{
			List<LayoutThing> pieces = new List<LayoutThing>();
			foreach (LayoutThing t in layout.things)
			{
				if (!t.structural)
				{
					pieces.Add(t);
				}
			}
			foreach (LayoutThing a in pieces)
			{
				ThingDef aDef = DefDatabase<ThingDef>.GetNamedSilentFail(a.def);
				if (aDef == null)
				{
					continue;
				}
				foreach (LayoutThing b in pieces)
				{
					if (b == a || b.def == a.def)
					{
						continue;
					}
					ThingDef bDef = DefDatabase<ThingDef>.GetNamedSilentFail(b.def);
					if (bDef == null)
					{
						continue;
					}
					LayoutRelation rel = LayoutRelation.Between(aDef, new IntVec3(a.x, 0, a.z), new Rot4(a.rot), bDef, new IntVec3(b.x, 0, b.z), new Rot4(b.rot));
					if (!rel.valid)
					{
						continue;
					}
					string pair = BuildDefs.KindOf(aDef) + ">" + BuildDefs.KindOf(bDef);
					Dictionary<string, int> keys;
					if (!view.exampleHabits.TryGetValue(pair, out keys))
					{
						keys = new Dictionary<string, int>();
						view.exampleHabits[pair] = keys;
					}
					int seen;
					keys.TryGetValue(rel.Key, out seen);
					keys[rel.Key] = seen + 1;
				}
			}
		}
	}
}
