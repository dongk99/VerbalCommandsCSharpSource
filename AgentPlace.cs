using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Agent 03 (agent_player\out\03\forms.md, sections 1 and 2): places on a map with no rooms. A Claude agent
	// names a place as text: a SPOT (one cell) or an AREA (a rectangle). The router script has already checked
	// the shape and rewritten it to one written form; this class turns that text into game cells (TryCell,
	// TryRect) and prints cells back (Describe, DescribeRect) so a later order can type them again.
	//
	// MAIN THREAD ONLY (it reads Map state; RouterGame moves every call onto the main thread first).
	// Read-only: nothing here changes the game. Fair play: nothing under fog is printed, counted or accepted
	// as a target (Visible is the single test). TryCell, TryRect, Describe, DescribeRect and LandmarkNames never
	// throw: they return false with a plain-words reason (Describe: just "x, z").
	//
	// Game members relied on (sources\RimWorldDecompiled\):
	//   Verse\MapPawns.cs:329 FreeColonistsSpawned, :144 FreeColonists, :347 PrisonersOfColonySpawned,
	//     :349 SlavesOfColonySpawned, :471 SpawnedColonyAnimals
	//   Verse\Pawn.cs:956 LabelShort, :213 Name; Verse\Name.cs:5 ToStringFull; Verse\Thing.cs:194 Spawned,
	//     :261 Position, :17 def, :19 thingIDNumber
	//   Verse\ZoneManager.cs:13 AllZones; Verse\Zone.cs:16 label, :98 Cells
	//   Verse\Room.cs:189 Cells; Verse\Room.cs:509 Role (via VocabularyText.RoomLabel)
	//   Verse\FogGrid.cs:71-78 IsFogged; Verse\GenGrid.cs:75 InBounds
	//   Verse\CellRect.cs:352 FromLimits, :753 ClipInsideMap, :58-64 minX/maxX/minZ/maxZ, :84 Width, :100 Height,
	//     :176 Cells
	//   Verse\Rot4.cs:155-158 FacingCell (north = +z, east = +x)
	//   RimWorld\ThingDefOf.cs:171 ShipChunk, :167 SteamGeyser; Verse\ListerThings.cs:52 ThingsOfDef
	//   Verse\TerrainDef.cs:320 IsRiver, :322 IsOcean; Verse\TerrainGrid.cs:89 TerrainAt
	//   Verse\GridsUtility.cs:443 GetEdifice; RimWorld\BuildingProperties.cs:87 isNaturalRock;
	//     Verse\ThingDef.cs:387 building
	//   Verse\Map.cs:262 Size, :43 listerThings, :47 mapPawns, :77 zoneManager, :107 fogGrid, :113 terrainGrid
	public static class AgentPlace
	{
		private const int NearRefTiles = 60;
		private const int MaxAreaSide = 250;
		private const int MaxNumbered = 9;

		private static readonly Regex CoordsRx = new Regex(@"^(-?\d+)\s*,\s*(-?\d+)$", RegexOptions.CultureInvariant);
		private static readonly Regex CoordsOnlyRx = new Regex(@"^\s*-?\d+\s*,\s*-?\d+\s*$", RegexOptions.CultureInvariant);
		private static readonly Regex GroupRx = new Regex(@"\(([^()]*)\)", RegexOptions.CultureInvariant);
		private static readonly Regex SpacesRx = new Regex(@"\s+", RegexOptions.CultureInvariant);
		private static readonly Regex OffsetRx = new Regex(@"^(\d+)\s+tiles?\s+([a-z]+(?:[ -][a-z]+)?)\s+of\s+(.+)$",
			RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
		private static readonly Regex CornersRx = new Regex(@"^(-?\d+)\s*,\s*(-?\d+)\s+to\s+(-?\d+)\s*,\s*(-?\d+)$",
			RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
		private static readonly Regex AroundRx = new Regex(@"^(\d+)\s*(?:by|x)\s*(\d+)\s+(?:around|at|near)\s+(.+)$",
			RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
		private static readonly Regex NumberedRx = new Regex(@"^(ship chunk|steam geyser) ([1-9])$",
			RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

		// ------------------------------------------------------------------------------------------------
		// Public API
		// ------------------------------------------------------------------------------------------------

		public static bool TryCell(Map map, string spot, out IntVec3 cell, out string reason)
		{
			cell = IntVec3.Invalid;
			reason = null;
			try
			{
				if (map == null)
				{
					reason = "there is no map to look at right now.";
					return false;
				}
				string text = Clean(spot);
				if (text.Length == 0)
				{
					reason = "no place was given.";
					return false;
				}
				IntVec3 found;
				if (!ResolveSpot(map, text, out found, out reason))
				{
					return false;
				}
				if (!found.InBounds(map))
				{
					reason = OffMapReason(map, found);
					return false;
				}
				if (!Visible(map, found))
				{
					reason = Coords(found) + " is not explored yet (under fog).";
					return false;
				}
				cell = found;
				reason = null;
				return true;
			}
			catch (Exception)
			{
				cell = IntVec3.Invalid;
				reason = "that place could not be read.";
				return false;
			}
		}

		public static bool TryRect(Map map, string area, out CellRect rect, out string reason)
		{
			rect = CellRect.Empty;
			reason = null;
			try
			{
				if (map == null)
				{
					reason = "there is no map to look at right now.";
					return false;
				}
				string text = Clean(area);
				CellRect found;
				Match m = CornersRx.Match(text);
				if (m.Success)
				{
					int x1, z1, x2, z2;
					if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out x1)
						|| !int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out z1)
						|| !int.TryParse(m.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out x2)
						|| !int.TryParse(m.Groups[4].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out z2))
					{
						reason = NotAnAreaReason(text);
						return false;
					}
					IntVec3 a = new IntVec3(x1, 0, z1);
					IntVec3 b = new IntVec3(x2, 0, z2);
					if (!a.InBounds(map))
					{
						reason = OffMapReason(map, a);
						return false;
					}
					if (!b.InBounds(map))
					{
						reason = OffMapReason(map, b);
						return false;
					}
					found = CellRect.FromLimits(Math.Min(x1, x2), Math.Min(z1, z2), Math.Max(x1, x2), Math.Max(z1, z2));
				}
				else
				{
					m = AroundRx.Match(text);
					if (!m.Success)
					{
						reason = NotAnAreaReason(text);
						return false;
					}
					int w, h;
					if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out w)
						|| !int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out h)
						|| w < 1 || h < 1 || w > MaxAreaSide || h > MaxAreaSide)
					{
						reason = "an area's size must be 1 to 250 cells each way.";
						return false;
					}
					IntVec3 centre;
					if (!TryCell(map, m.Groups[3].Value, out centre, out reason))
					{
						return false;
					}
					int minX = centre.x - (w - 1) / 2;
					int minZ = centre.z - (h - 1) / 2;
					found = new CellRect(minX, minZ, w, h).ClipInsideMap(map);
				}
				bool anyVisible = false;
				for (int x = found.minX; x <= found.maxX && !anyVisible; x++)
				{
					for (int z = found.minZ; z <= found.maxZ; z++)
					{
						if (Visible(map, new IntVec3(x, 0, z)))
						{
							anyVisible = true;
							break;
						}
					}
				}
				if (!anyVisible)
				{
					reason = "that area is not explored yet (all of it is under fog).";
					return false;
				}
				rect = found;
				reason = null;
				return true;
			}
			catch (Exception)
			{
				rect = CellRect.Empty;
				reason = "that area could not be read.";
				return false;
			}
		}

		public static string Describe(Map map, IntVec3 cell)
		{
			try
			{
				if (map == null)
				{
					return Coords(cell);
				}
				if (!cell.InBounds(map))
				{
					return Coords(cell) + " (off the map)";
				}
				return Coords(cell) + " (" + Relative(map, cell) + ")";
			}
			catch (Exception)
			{
				return Coords(cell);
			}
		}

		public static string DescribeRect(Map map, CellRect rect)
		{
			string corners = Coords(new IntVec3(rect.minX, 0, rect.minZ)) + " to " + Coords(new IntVec3(rect.maxX, 0, rect.maxZ));
			try
			{
				int w = rect.maxX - rect.minX + 1;
				int h = rect.maxZ - rect.minZ + 1;
				string size = w.ToString(CultureInfo.InvariantCulture) + " by " + h.ToString(CultureInfo.InvariantCulture);
				if (map == null)
				{
					return corners + " (" + size + ")";
				}
				IntVec3 centre = new IntVec3(rect.minX + (rect.maxX - rect.minX) / 2, 0, rect.minZ + (rect.maxZ - rect.minZ) / 2);
				return corners + " (" + size + ", centre " + Relative(map, centre) + ")";
			}
			catch (Exception)
			{
				return corners;
			}
		}

		public static bool Visible(Map map, IntVec3 cell)
		{
			try
			{
				return map != null && cell.InBounds(map) && !map.fogGrid.IsFogged(cell);
			}
			catch (Exception)
			{
				return false;
			}
		}

		public static IEnumerable<string> LandmarkNames(Map map)
		{
			List<string> names = new List<string>();
			try
			{
				if (map == null)
				{
					return names;
				}
				names.Add("map centre");
				names.Add("north edge");
				names.Add("south edge");
				names.Add("east edge");
				names.Add("west edge");
				foreach (KeyValuePair<string, IntVec3> e in Numbered(map, ThingDefOf.ShipChunk, "ship chunk"))
				{
					names.Add(e.Key);
				}
				foreach (KeyValuePair<string, IntVec3> e in Numbered(map, ThingDefOf.SteamGeyser, "steam geyser"))
				{
					names.Add(e.Key);
				}
				IntVec3 c;
				if (TryLandmark(map, "river", out c))
				{
					names.Add("river");
				}
				if (TryLandmark(map, "coast", out c))
				{
					names.Add("coast");
				}
				if (TryLandmark(map, "rock face", out c))
				{
					names.Add("rock face");
				}
			}
			catch (Exception)
			{
			}
			return names;
		}

		public static bool TryLandmark(Map map, string name, out IntVec3 cell)
		{
			cell = IntVec3.Invalid;
			try
			{
				if (map == null || string.IsNullOrWhiteSpace(name))
				{
					return false;
				}
				string n = SpacesRx.Replace(name.Trim(), " ").ToLowerInvariant();
				IntVec3 size = map.Size;
				switch (n)
				{
					case "map centre":
					case "map center":
						cell = new IntVec3(size.x / 2, 0, size.z / 2);
						return true;
					case "north edge":
						cell = new IntVec3(size.x / 2, 0, size.z - 1);
						return true;
					case "south edge":
						cell = new IntVec3(size.x / 2, 0, 0);
						return true;
					case "east edge":
						cell = new IntVec3(size.x - 1, 0, size.z / 2);
						return true;
					case "west edge":
						cell = new IntVec3(0, 0, size.z / 2);
						return true;
				}
				Match m = NumberedRx.Match(n);
				if (m.Success)
				{
					bool chunk = m.Groups[1].Value == "ship chunk";
					foreach (KeyValuePair<string, IntVec3> e in Numbered(map, chunk ? ThingDefOf.ShipChunk : ThingDefOf.SteamGeyser, m.Groups[1].Value))
					{
						if (e.Key == n)
						{
							cell = e.Value;
							return true;
						}
					}
					return false;
				}
				if (n == "river")
				{
					return NearestVisible(map, c => map.terrainGrid.TerrainAt(c).IsRiver, out cell);
				}
				if (n == "coast")
				{
					return NearestVisible(map, c => map.terrainGrid.TerrainAt(c).IsOcean, out cell);
				}
				if (n == "rock face")
				{
					return NearestVisible(map, c =>
					{
						Building b = c.GetEdifice(map);
						return b != null && b.def != null && b.def.building != null && b.def.building.isNaturalRock;
					}, out cell);
				}
				return false;
			}
			catch (Exception)
			{
				cell = IntVec3.Invalid;
				return false;
			}
		}

		public static IntVec3 ColonyCentre(Map map)
		{
			try
			{
				if (map == null)
				{
					return IntVec3.Zero;
				}
				List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned;
				if (pawns != null && pawns.Count > 0)
				{
					double sx = 0;
					double sz = 0;
					foreach (Pawn p in pawns)
					{
						sx += p.Position.x;
						sz += p.Position.z;
					}
					int x = (int)Math.Round(sx / pawns.Count, MidpointRounding.AwayFromZero);
					int z = (int)Math.Round(sz / pawns.Count, MidpointRounding.AwayFromZero);
					return new IntVec3(x, 0, z);
				}
				return new IntVec3(map.Size.x / 2, 0, map.Size.z / 2);
			}
			catch (Exception)
			{
				return map != null ? new IntVec3(map.Size.x / 2, 0, map.Size.z / 2) : IntVec3.Zero;
			}
		}

		public static int Tiles(IntVec3 a, IntVec3 b)
		{
			return Math.Max(Math.Abs(a.x - b.x), Math.Abs(a.z - b.z));
		}

		// dx = cell.x - ref.x, dz = cell.z - ref.z. The lab's rule (router_lab\locations.txt:23-25): when the
		// smaller offset is at least half the larger one it is a corner, else the straight one of the larger axis.
		public static string DirectionWord(int dx, int dz)
		{
			if (dx == 0 && dz == 0)
			{
				return "";
			}
			int ax = Math.Abs(dx);
			int az = Math.Abs(dz);
			int min = Math.Min(ax, az);
			int max = Math.Max(ax, az);
			if (min > 0 && 2 * min >= max)
			{
				return (dz > 0 ? "north" : "south") + (dx > 0 ? "east" : "west");
			}
			if (ax > az)
			{
				return dx > 0 ? "east" : "west";
			}
			return dz > 0 ? "north" : "south";
		}

		// ------------------------------------------------------------------------------------------------
		// Text cleaning
		// ------------------------------------------------------------------------------------------------

		// Trim, drop a trailing ".", drop bracketed tails (a bracket holding only "x, z" is unwrapped), collapse
		// spaces. Case is kept (names are compared case-insensitively later; reasons quote the player's text).
		private static string Clean(string s)
		{
			string t = (s ?? "").Replace(' ', ' ');
			for (int i = 0; i < 6; i++)
			{
				string before = t;
				t = SpacesRx.Replace(t, " ").Trim();
				while (t.EndsWith("."))
				{
					t = t.Substring(0, t.Length - 1).TrimEnd();
				}
				t = GroupRx.Replace(t, g => CoordsOnlyRx.IsMatch(g.Groups[1].Value) ? g.Groups[1].Value.Trim() : "");
				t = SpacesRx.Replace(t, " ").Trim();
				while (t.EndsWith("."))
				{
					t = t.Substring(0, t.Length - 1).TrimEnd();
				}
				if (t == before)
				{
					break;
				}
			}
			return t;
		}

		// ------------------------------------------------------------------------------------------------
		// SPOT resolution
		// ------------------------------------------------------------------------------------------------

		private static bool ResolveSpot(Map map, string text, out IntVec3 cell, out string reason)
		{
			cell = IntVec3.Invalid;
			reason = null;
			string t = StripNear(text);
			if (TryCoords(t, out cell))
			{
				return true;
			}
			Match m = OffsetRx.Match(t);
			if (m.Success)
			{
				int n;
				int sx, sz;
				if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
					&& n <= 100000
					&& ParseDirection(m.Groups[2].Value, out sx, out sz))
				{
					IntVec3 baseCell;
					if (!ResolveBase(map, m.Groups[3].Value.Trim(), out baseCell, out reason))
					{
						return false;
					}
					cell = new IntVec3(baseCell.x + n * sx, 0, baseCell.z + n * sz);
					return true;
				}
			}
			return ResolveName(map, t, out cell, out reason);
		}

		// The NAME of "N tiles DIRECTION of NAME": "x, z" or a name.
		private static bool ResolveBase(Map map, string text, out IntVec3 cell, out string reason)
		{
			cell = IntVec3.Invalid;
			reason = null;
			string t = StripNear(text);
			if (TryCoords(t, out cell))
			{
				return true;
			}
			return ResolveName(map, t, out cell, out reason);
		}

		private static string StripNear(string text)
		{
			string t = text.Trim();
			if (t.StartsWith("near ", StringComparison.OrdinalIgnoreCase))
			{
				t = t.Substring(5).Trim();
			}
			return t;
		}

		private static bool TryCoords(string t, out IntVec3 cell)
		{
			cell = IntVec3.Invalid;
			Match m = CoordsRx.Match(t);
			if (!m.Success)
			{
				return false;
			}
			int x, z;
			if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
				|| !int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out z))
			{
				return false;
			}
			cell = new IntVec3(x, 0, z);
			return true;
		}

		private static bool ResolveName(Map map, string name, out IntVec3 cell, out string reason)
		{
			cell = IntVec3.Invalid;
			reason = null;
			if (name.Length == 0)
			{
				reason = "no place was given.";
				return false;
			}

			Pawn pawn = FindPawn(map.mapPawns.FreeColonistsSpawned, name);
			if (pawn != null)
			{
				cell = pawn.Position;
				return true;
			}
			// A colonist of that name who is not standing on this map.
			Pawn away = FindPawn(map.mapPawns.FreeColonists, name);
			if (away != null)
			{
				reason = (away.LabelShort ?? name) + " is not on this map right now.";
				return false;
			}
			pawn = FindPawn(map.mapPawns.PrisonersOfColonySpawned, name) ?? FindPawn(map.mapPawns.SlavesOfColonySpawned, name);
			if (pawn != null)
			{
				cell = pawn.Position;
				return true;
			}
			foreach (Pawn animal in map.mapPawns.SpawnedColonyAnimals)
			{
				if (animal != null && FormPlanner.PawnNameMatches(animal, FormPlanner.Norm(name)))
				{
					cell = animal.Position;
					return true;
				}
			}
			if (TryLandmark(map, name, out cell))
			{
				return true;
			}
			foreach (Zone zone in map.zoneManager.AllZones)
			{
				if (zone != null && zone.label != null && FormPlanner.SameName(zone.label, name) && zone.Cells.Count > 0)
				{
					cell = NearestToAverage(zone.Cells);
					return true;
				}
			}
			foreach (Room room in StorageIndex.AllProperRooms(map))
			{
				string label = VocabularyText.RoomLabel(room);
				if (label != null && FormPlanner.SameName(label, name))
				{
					List<IntVec3> cells = room.Cells.ToList();
					if (cells.Count > 0)
					{
						cell = NearestToAverage(cells);
						return true;
					}
				}
			}
			cell = IntVec3.Invalid;
			reason = "'" + name + "' is not a colonist, landmark or zone on this map.";
			return false;
		}

		private static Pawn FindPawn(List<Pawn> pawns, string name)
		{
			if (pawns == null)
			{
				return null;
			}
			string normalized = FormPlanner.Norm(name);
			foreach (Pawn p in pawns)
			{
				if (p != null && FormPlanner.PawnNameMatches(p, normalized))
				{
					return p;
				}
			}
			return null;
		}

		private static IntVec3 NearestToAverage(IList<IntVec3> cells)
		{
			double ax = 0;
			double az = 0;
			foreach (IntVec3 c in cells)
			{
				ax += c.x;
				az += c.z;
			}
			ax /= cells.Count;
			az /= cells.Count;
			IntVec3 best = cells[0];
			double bestD = double.MaxValue;
			foreach (IntVec3 c in cells)
			{
				double d = (c.x - ax) * (c.x - ax) + (c.z - az) * (c.z - az);
				if (d < bestD)
				{
					bestD = d;
					best = c;
				}
			}
			return best;
		}

		// north/up/top = +z, south/down/bottom = -z, east/right = +x, west/left = -x. Corners: both axes.
		private static bool ParseDirection(string word, out int sx, out int sz)
		{
			sx = 0;
			sz = 0;
			string w = SpacesRx.Replace(word.Replace('-', ' ').Trim().ToLowerInvariant(), " ");
			List<string> tokens = new List<string>();
			if (w.Contains(" "))
			{
				tokens.AddRange(w.Split(' '));
			}
			else if (w == "northeast")
			{
				tokens.Add("north");
				tokens.Add("east");
			}
			else if (w == "northwest")
			{
				tokens.Add("north");
				tokens.Add("west");
			}
			else if (w == "southeast")
			{
				tokens.Add("south");
				tokens.Add("east");
			}
			else if (w == "southwest")
			{
				tokens.Add("south");
				tokens.Add("west");
			}
			else
			{
				tokens.Add(w);
			}
			if (tokens.Count < 1 || tokens.Count > 2)
			{
				return false;
			}
			bool haveX = false;
			bool haveZ = false;
			foreach (string tok in tokens)
			{
				switch (tok)
				{
					case "north":
					case "up":
					case "top":
						if (haveZ) { return false; }
						haveZ = true;
						sz = 1;
						break;
					case "south":
					case "down":
					case "bottom":
						if (haveZ) { return false; }
						haveZ = true;
						sz = -1;
						break;
					case "east":
					case "right":
						if (haveX) { return false; }
						haveX = true;
						sx = 1;
						break;
					case "west":
					case "left":
						if (haveX) { return false; }
						haveX = true;
						sx = -1;
						break;
					default:
						return false;
				}
			}
			return haveX || haveZ;
		}

		// ------------------------------------------------------------------------------------------------
		// Describe helpers
		// ------------------------------------------------------------------------------------------------

		private static string Coords(IntVec3 c)
		{
			return c.x.ToString(CultureInfo.InvariantCulture) + ", " + c.z.ToString(CultureInfo.InvariantCulture);
		}

		private static string OffMapReason(Map map, IntVec3 c)
		{
			int w = map.Size.x;
			int h = map.Size.z;
			return Coords(c) + " is off the map: the map is " + w.ToString(CultureInfo.InvariantCulture) + " by "
				+ h.ToString(CultureInfo.InvariantCulture) + " (x 0 to " + (w - 1).ToString(CultureInfo.InvariantCulture)
				+ ", z 0 to " + (h - 1).ToString(CultureInfo.InvariantCulture) + ").";
		}

		private static string NotAnAreaReason(string text)
		{
			return "'" + text + "' is not an area: write 'x, z to x, z' or 'W by H around a place'.";
		}

		// "6 tiles north of Bob", "1 tile east of ship chunk 1", "at map centre".
		private static string Relative(Map map, IntVec3 cell)
		{
			string refName = null;
			IntVec3 refCell = IntVec3.Invalid;
			int bestDist = int.MaxValue;
			int bestKind = 0; // 0 = colonist, 1 = landmark
			foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
			{
				if (p == null)
				{
					continue;
				}
				Consider(p.LabelShort ?? "", 0, p.Position, cell, ref refName, ref refCell, ref bestDist, ref bestKind);
			}
			foreach (KeyValuePair<string, IntVec3> e in Numbered(map, ThingDefOf.ShipChunk, "ship chunk"))
			{
				Consider(e.Key, 1, e.Value, cell, ref refName, ref refCell, ref bestDist, ref bestKind);
			}
			foreach (KeyValuePair<string, IntVec3> e in Numbered(map, ThingDefOf.SteamGeyser, "steam geyser"))
			{
				Consider(e.Key, 1, e.Value, cell, ref refName, ref refCell, ref bestDist, ref bestKind);
			}
			if (refName == null)
			{
				refName = "map centre";
				refCell = new IntVec3(map.Size.x / 2, 0, map.Size.z / 2);
			}
			int n = Tiles(cell, refCell);
			if (n == 0)
			{
				return "at " + refName;
			}
			string dir = DirectionWord(cell.x - refCell.x, cell.z - refCell.z);
			return n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " tile " : " tiles ") + dir + " of " + refName;
		}

		private static void Consider(string name, int kind, IntVec3 at, IntVec3 cell, ref string refName, ref IntVec3 refCell,
			ref int bestDist, ref int bestKind)
		{
			int d = Tiles(cell, at);
			if (d == 0 || d > NearRefTiles)
			{
				return;
			}
			bool better = refName == null || d < bestDist
				|| (d == bestDist && (kind < bestKind || (kind == bestKind && string.CompareOrdinal(name, refName) < 0)));
			if (better)
			{
				refName = name;
				refCell = at;
				bestDist = d;
				bestKind = kind;
			}
		}

		// ------------------------------------------------------------------------------------------------
		// Landmark helpers
		// ------------------------------------------------------------------------------------------------

		// "<prefix> N" for each spawned thing of def whose cell is Visible, by thingIDNumber, N from 1, at most 9.
		internal static List<KeyValuePair<string, IntVec3>> Numbered(Map map, ThingDef def, string prefix)   // internal: AgentMapPicture labels
		{
			List<KeyValuePair<string, IntVec3>> result = new List<KeyValuePair<string, IntVec3>>();
			if (map == null || def == null)
			{
				return result;
			}
			List<Thing> things = map.listerThings.ThingsOfDef(def);
			if (things == null)
			{
				return result;
			}
			int n = 0;
			foreach (Thing t in things.Where(x => x != null && x.Spawned && Visible(map, x.Position)).OrderBy(x => x.thingIDNumber))
			{
				if (n >= MaxNumbered)
				{
					break;
				}
				n++;
				result.Add(new KeyValuePair<string, IntVec3>(prefix + " " + n.ToString(CultureInfo.InvariantCulture), t.Position));
			}
			return result;
		}

		// First Visible cell matching the test, scanning square rings outward from the colony centre (the best of
		// the first ring that has a hit, by straight-line distance). Stops at the first hit; at most the whole map.
		private static bool NearestVisible(Map map, Func<IntVec3, bool> test, out IntVec3 cell)
		{
			cell = IntVec3.Invalid;
			IntVec3 centre = ColonyCentre(map);
			int maxR = Math.Max(map.Size.x, map.Size.z);
			for (int r = 0; r <= maxR; r++)
			{
				bool found = false;
				double bestD = double.MaxValue;
				IntVec3 best = IntVec3.Invalid;
				for (int dx = -r; dx <= r; dx++)
				{
					// edge columns walk every z; the columns between visit only the top and bottom of the ring
					int step = (Math.Abs(dx) == r || r == 0) ? 1 : 2 * r;
					for (int dz = -r; dz <= r; dz += step)
					{
						IntVec3 c = new IntVec3(centre.x + dx, 0, centre.z + dz);
						if (!Visible(map, c) || !test(c))
						{
							continue;
						}
						double d = (double)dx * dx + (double)dz * dz;
						if (d < bestD)
						{
							bestD = d;
							best = c;
							found = true;
						}
					}
				}
				if (found)
				{
					cell = best;
					return true;
				}
			}
			return false;
		}
	}
}
