using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Agent 03 (agent_player\out\03\forms.md, sections 1, 3 and 5; brief sonnet_briefs\AgentFind.md plus its
	// "mountain bases" addition): where is NAME / where is nearest THING [to SPOT] / list THINGS on map. A Claude
	// agent plays RimWorld through text orders only; these answers tell it where things are so its next order can
	// name the place. They change nothing.
	//
	// MAIN THREAD ONLY (it reads Map state; RouterGame moves every call onto the main thread first).
	// Read only. Fair play: only things and cells that AgentPlace.Visible accepts are looked at, printed or counted;
	// the one exception the brief asks for is the mountain answer's count of fogged cells next to a mountain (a
	// number only; nothing under the fog is read). No dev mode, no Debug*, no god mode.
	// Answers are plain words, every line under 100 characters. Query returns null for any other form and may
	// throw Fault (a refusal in plain words); any other failure becomes a plain "could not be looked up" reply.
	//
	// Game members used (sources\RimWorldDecompiled\):
	//   Verse\MapPawns.cs:329 FreeColonistsSpawned, :144 FreeColonists, :347 PrisonersOfColonySpawned,
	//     :349 SlavesOfColonySpawned, :471 SpawnedColonyAnimals, :327 AllPawnsSpawned
	//   Verse\Pawn.cs:956 LabelShort, :213 Name, :231 Downed, :261 Dead, :600 Drafted, :225 RaceProps, :46 jobs,
	//     :16 (Pawn.cs field kindDef is PawnKindDef kindDef); Verse\Name.cs:5 ToStringFull
	//   Verse.AI\JobDriver.cs:218 GetReport (via Pawn_JobTracker.curDriver, what the inspect pane shows)
	//   Verse\Thing.cs:17 def, :19 thingIDNumber, :27 stackCount, :194 Spawned, :390 Faction;
	//     Verse\Thing.cs:261 Position; RimWorld\StoreUtility.cs:79 GetSlotGroup (Thing)
	//   RimWorld\GenHostility.cs:118 HostileTo(Thing, Faction); RimWorld\Faction.cs:214 OfPlayer
	//   Verse\RaceProperties.cs:265 Animal; Verse\ThingDef.cs:15 category, :387 building, :389 race, :395 plant;
	//     Verse\Def.cs:12 defName, :17 label; Verse\DefDatabase.cs:18 AllDefsListForReading, :227 GetNamedSilentFail
	//   Verse\ThingCategory.cs:7 Item; Verse\ListerThings.cs:25 AllThings, :37 ThingsInGroup, :52 ThingsOfDef;
	//     Verse\ThingRequestGroup.cs:32 Plant
	//   RimWorld\BuildingProperties.cs:87 isNaturalRock, :89 isResourceRock; Verse\GridsUtility.cs:443 GetEdifice;
	//     Verse\GenGrid.cs:135 Walkable
	//   RimWorld\PlantProperties.cs:203 Harvestable, :207 IsTree
	//   RimWorld\ThingDefOf.cs:171 ShipChunk, :167 SteamGeyser
	//   Verse\BuildableDef.cs:19 fertility, :13 passability; Verse\Traversability.cs:7 Impassable;
	//     Verse\TerrainDef.cs:320 IsRiver, :324 IsWater; Verse\TerrainGrid.cs:89 TerrainAt;
	//     RimWorld\TerrainDefOf.cs:8 Sand, :26 Gravel, :28 WaterDeep, :32 WaterMovingChestDeep,
	//     :36 WaterOceanDeep, :48 Marsh, :50 Mud
	//   Verse\ZoneManager.cs:13 AllZones, :78 ZoneAt; Verse\Zone.cs:16 label, :98 Cells
	//   Verse\GridsUtility.cs:57 GetRoom; Verse\Room.cs:509 Role (StorageIndex.IsGenericRoomRole,
	//     VocabularyText.RoomLabel -> Room.GetRoomRoleLabel, Room.cs:881)
	//   Verse\FogGrid.cs:71 IsFogged (only through AgentPlace.Visible, and in the mountain fog count)
	//   Verse\Map.cs:262 Size, :43 listerThings, :47 mapPawns, :77 zoneManager, :107 fogGrid, :113 terrainGrid,
	//     :117 roofGrid; Verse\RoofGrid.cs:80 RoofAt; Verse\RoofDef.cs:5 isNatural, :7 isThickRoof
	//   Verse\CellRect.cs:352 FromLimits
	//   mod members: FormPlanner.PawnNameMatches (FormWave1.cs:98), Executor.FindItemDefs (Executor.cs:1509)
	public static class AgentFind
	{
		private const int ListLineCap = 10;
		private const int MaxLines = 60;
		private const int MaxCols = 99;
		private const int VeinCap = 500;
		private const int MassCap = 20000;
		private const int MinMass = 20;
		private const int MountainListCap = 8;
		private const string CutLine = "Cut at 60 lines; ask for fewer things at once.";
		private const string Allowed = "Try: steel, compacted machinery, ore, rock, ship chunk, geyser, trees, berries, rich soil, "
			+ "water, animals, hostiles, items.";

		private static readonly object CellMatch = new object();
		private static readonly int[] DX = { 1, -1, 0, 0 };
		private static readonly int[] DZ = { 0, 0, 1, -1 };

		private enum Mode
		{
			Things,
			Ore,
			Patch,
			Mountain
		}

		private sealed class Kind
		{
			public string label;
			public Mode mode;
			public Func<Map, IEnumerable<Thing>> things;
			public Func<Map, IntVec3, object> cellKey;
			public ThingDef numberedDef;
			public string numberedPrefix;
			public bool showLabel;
		}

		private sealed class Hit
		{
			public IntVec3 cell;
			public string extra;
			public int id;
		}

		private sealed class Comp
		{
			public object key;
			public int size;
			public IntVec3 nearest;
		}

		private sealed class Mass
		{
			public List<int> cells = new List<int>();
			public bool hasFace;
			public IntVec3 rank;
		}

		// ------------------------------------------------------------------------------------------------
		// Entry
		// ------------------------------------------------------------------------------------------------

		public static QueryReply Query(FormContext c)
		{
			if (c == null)
			{
				return null;
			}
			string form = c.form;
			if (form != "query where" && form != "query nearest" && form != "query list")
			{
				return null;
			}
			try
			{
				if (c.map == null)
				{
					throw new Fault("there is no map to look at right now.");
				}
				if (form == "query where")
				{
					return QueryReply.Ok(AnswerWhere(c));
				}
				if (form == "query nearest")
				{
					return QueryReply.Ok(AnswerNearest(c));
				}
				return QueryReply.Ok(AnswerList(c));
			}
			catch (Fault)
			{
				throw;
			}
			catch (Exception)
			{
				return QueryReply.Fail("that could not be looked up right now.");
			}
		}

		// ------------------------------------------------------------------------------------------------
		// where is NAME
		// ------------------------------------------------------------------------------------------------

		private static string AnswerWhere(FormContext c)
		{
			Map map = c.map;
			string name = Tidy(c.Text("NAME"));
			if (name.Length == 0)
			{
				throw new Fault("no name was given.");
			}

			string pawns = WherePawns(map, name);
			if (pawns != null)
			{
				return JoinLines(new List<string> { pawns });
			}

			IntVec3 cell;
			if (AgentPlace.TryLandmark(map, name, out cell) && AgentPlace.Visible(map, cell))
			{
				return JoinLines(new List<string> { name + ": " + AgentPlace.Describe(map, cell) + "." });
			}

			foreach (Zone zone in map.zoneManager.AllZones)
			{
				if (zone == null || zone.label == null || !FormPlanner.SameName(zone.label, name))
				{
					continue;
				}
				int count = 0;
				int minX = int.MaxValue;
				int minZ = int.MaxValue;
				int maxX = int.MinValue;
				int maxZ = int.MinValue;
				foreach (IntVec3 zc in zone.Cells)
				{
					if (!AgentPlace.Visible(map, zc))
					{
						continue;
					}
					count++;
					minX = Math.Min(minX, zc.x);
					maxX = Math.Max(maxX, zc.x);
					minZ = Math.Min(minZ, zc.z);
					maxZ = Math.Max(maxZ, zc.z);
				}
				if (count > 0)
				{
					CellRect rect = CellRect.FromLimits(minX, minZ, maxX, maxZ);
					return JoinLines(new List<string>
					{
						zone.label + ": " + Num(count) + (count == 1 ? " cell, " : " cells, ") + AgentPlace.DescribeRect(map, rect) + "."
					});
				}
			}

			List<Kind> kinds = Resolve(name);
			if (kinds.Count > 0)
			{
				string text = Blocks(map, kinds, false, AgentPlace.ColonyCentre(map));
				if (text != null)
				{
					return text;
				}
			}
			return JoinLines(new List<string> { "Nothing called '" + name + "' is in view on this map." });
		}

		private static string WherePawns(Map map, string name)
		{
			MapPawns mp = map.mapPawns;
			List<Pawn> hits = SpawnedMatches(mp.FreeColonistsSpawned, name, map);
			if (hits.Count == 0)
			{
				foreach (Pawn p in mp.FreeColonists)
				{
					if (p != null && !p.Spawned && PawnNameIs(p, name))
					{
						return (p.LabelShort ?? name) + " is not on this map right now.";
					}
				}
				hits = SpawnedMatches(mp.PrisonersOfColonySpawned, name, map);
			}
			if (hits.Count == 0)
			{
				hits = SpawnedMatches(mp.SlavesOfColonySpawned, name, map);
			}
			if (hits.Count == 0)
			{
				hits = SpawnedMatches(mp.SpawnedColonyAnimals, name, map);
			}
			if (hits.Count == 0)
			{
				return null;
			}
			List<string> lines = new List<string>();
			foreach (Pawn p in hits.Take(ListLineCap))
			{
				AddPawnLines(map, p, lines);
			}
			if (hits.Count > ListLineCap)
			{
				lines.Add("and " + (hits.Count - ListLineCap) + " more with that name.");
			}
			return string.Join("\n", lines.ToArray());
		}

		private static List<Pawn> SpawnedMatches(IEnumerable<Pawn> pawns, string name, Map map)
		{
			List<Pawn> result = new List<Pawn>();
			if (pawns == null)
			{
				return result;
			}
			foreach (Pawn p in pawns)
			{
				if (p != null && p.Spawned && !p.Dead && AgentPlace.Visible(map, p.Position) && PawnNameIs(p, name))
				{
					result.Add(p);
				}
			}
			return result;
		}

		private static bool PawnNameIs(Pawn p, string name)
		{
			return FormPlanner.PawnNameMatches(p, FormPlanner.Norm(name));
		}

		private static void AddPawnLines(Map map, Pawn p, List<string> lines)
		{
			IntVec3 pos = p.Position;
			string line = (p.LabelShort ?? "pawn") + ": " + AgentPlace.Describe(map, pos);
			Zone zone = map.zoneManager.ZoneAt(pos);
			if (zone != null && !string.IsNullOrEmpty(zone.label))
			{
				line += ", in " + zone.label;
			}
			Room room = pos.GetRoom(map);
			if (room != null && !StorageIndex.IsGenericRoomRole(room.Role))
			{
				string roomLabel = VocabularyText.RoomLabel(room);
				if (!string.IsNullOrEmpty(roomLabel))
				{
					line += ", in " + roomLabel;
				}
			}
			lines.Add(line + ".");
			string job = p.jobs != null && p.jobs.curDriver != null ? FormPlanner.PlainText(p.jobs.curDriver.GetReport()) : "";
			job = job.TrimEnd('.', ' ');
			if (job.Length > 0)
			{
				AddSentence(lines, "Doing: " + job + ".");
			}
			if (p.Downed)
			{
				AddSentence(lines, "Downed.");
			}
			if (p.Drafted)
			{
				AddSentence(lines, "Drafted.");
			}
		}

		private static void AddSentence(List<string> lines, string sentence)
		{
			string last = lines[lines.Count - 1];
			if (last.Length + 1 + sentence.Length <= MaxCols)
			{
				lines[lines.Count - 1] = last + " " + sentence;
			}
			else
			{
				lines.Add("  " + sentence);
			}
		}

		// ------------------------------------------------------------------------------------------------
		// where is nearest THING [to SPOT]
		// ------------------------------------------------------------------------------------------------

		private static string AnswerNearest(FormContext c)
		{
			Map map = c.map;
			string word = Tidy(c.Text("THING"));
			if (word.Length == 0)
			{
				throw new Fault("no thing was named to look for.");
			}
			List<Kind> kinds = ResolveOrFault(word);

			IntVec3 from;
			string fromName;
			string spot = Tidy(c.Text("SPOT"));
			if (spot.Length > 0)
			{
				string reason;
				if (!AgentPlace.TryCell(map, spot, out from, out reason))
				{
					throw new Fault(reason);
				}
				fromName = spot;
			}
			else
			{
				from = AgentPlace.ColonyCentre(map);
				fromName = "the colonists";
			}

			List<string> lines = new List<string>();
			foreach (Kind k in kinds)
			{
				lines.AddRange(NearestLines(map, k, from, fromName));
			}
			return JoinLines(lines);
		}

		private static List<string> NearestLines(Map map, Kind k, IntVec3 from, string fromName)
		{
			List<string> lines = new List<string>();
			string head = "Nearest " + k.label + " to " + fromName + ": ";
			if (k.mode == Mode.Things)
			{
				Hit best = null;
				foreach (Hit h in CollectHits(map, k))
				{
					if (best == null || Closer(h.cell, best.cell, from) || (h.cell == best.cell && h.id < best.id))
					{
						best = h;
					}
				}
				if (best == null)
				{
					lines.Add("No " + k.label + " in view.");
					return lines;
				}
				lines.Add(head + AgentPlace.Describe(map, best.cell) + ", " + Tiles(AgentPlace.Tiles(best.cell, from)) + " away.");
				return lines;
			}
			if (k.mode == Mode.Mountain)
			{
				List<Mass> masses = FindMasses(map, from);
				if (masses.Count == 0)
				{
					lines.Add("No " + k.label + " in view.");
					return lines;
				}
				Mass m = masses[0];
				lines.Add(head + (m.hasFace ? "rock face " : "rock (no open face yet) ") + AgentPlace.Describe(map, m.rank) + ", "
					+ Tiles(AgentPlace.Tiles(m.rank, from)) + " away.");
				lines.AddRange(MountainDetail(map, m, new bool[map.Size.x * map.Size.z]));
				return lines;
			}

			IntVec3 found;
			object key;
			if (!RingSearch(map, k, from, out found, out key))
			{
				lines.Add("No " + k.label + " in view.");
				return lines;
			}
			string text = head + AgentPlace.Describe(map, found) + ", " + Tiles(AgentPlace.Tiles(found, from)) + " away";
			if (k.mode == Mode.Ore)
			{
				ThingDef def = key as ThingDef;
				int size = def != null ? VeinSize(map, found, def, VeinCap) : 1;
				text += "; that vein has " + (size > VeinCap ? VeinCap + "+" : Num(size)) + " cells";
			}
			lines.Add(text + ".");
			return lines;
		}

		// Square rings outward from `from`; the first ring with a visible match wins (best of the ring by
		// straight-line distance). At most the whole map.
		private static bool RingSearch(Map map, Kind k, IntVec3 from, out IntVec3 found, out object key)
		{
			found = IntVec3.Invalid;
			key = null;
			int w = map.Size.x;
			int h = map.Size.z;
			int maxR = Math.Max(w, h);
			for (int r = 0; r <= maxR; r++)
			{
				bool any = false;
				long bestD = long.MaxValue;
				for (int dx = -r; dx <= r; dx++)
				{
					int x = from.x + dx;
					if (x < 0 || x >= w)
					{
						continue;
					}
					int step = (Math.Abs(dx) == r || r == 0) ? 1 : 2 * r;
					for (int dz = -r; dz <= r; dz += step)
					{
						int z = from.z + dz;
						if (z < 0 || z >= h)
						{
							continue;
						}
						IntVec3 cell = new IntVec3(x, 0, z);
						if (!AgentPlace.Visible(map, cell))
						{
							continue;
						}
						object kk = k.cellKey(map, cell);
						if (kk == null)
						{
							continue;
						}
						long d = (long)dx * dx + (long)dz * dz;
						if (d < bestD)
						{
							bestD = d;
							found = cell;
							key = kk;
							any = true;
						}
					}
				}
				if (any)
				{
					return true;
				}
			}
			return false;
		}

		// Cells of the same mineable def 4-connected to `start` (all Visible), counted up to cap + 1.
		private static int VeinSize(Map map, IntVec3 start, ThingDef def, int cap)
		{
			int w = map.Size.x;
			HashSet<int> seen = new HashSet<int>();
			Stack<IntVec3> todo = new Stack<IntVec3>();
			seen.Add(start.z * w + start.x);
			todo.Push(start);
			int count = 0;
			while (todo.Count > 0)
			{
				IntVec3 cur = todo.Pop();
				count++;
				if (count > cap)
				{
					return cap + 1;
				}
				for (int i = 0; i < 4; i++)
				{
					IntVec3 n = new IntVec3(cur.x + DX[i], 0, cur.z + DZ[i]);
					if (!AgentPlace.Visible(map, n) || !seen.Add(n.z * w + n.x))
					{
						continue;
					}
					Building b = n.GetEdifice(map);
					if (b != null && b.def == def)
					{
						todo.Push(n);
					}
				}
			}
			return count;
		}

		// ------------------------------------------------------------------------------------------------
		// list THINGS
		// ------------------------------------------------------------------------------------------------

		private static string AnswerList(FormContext c)
		{
			List<string> words = c.TextList("THINGS");
			if (words.Count == 0)
			{
				throw new Fault("no thing was named to look for.");
			}
			List<Kind> kinds = new List<Kind>();
			HashSet<string> labels = new HashSet<string>();
			foreach (string w in words)
			{
				foreach (Kind k in ResolveOrFault(Tidy(w)))
				{
					if (labels.Add(k.label))
					{
						kinds.Add(k);
					}
				}
			}
			// "list mountains on map." alone: the map picture's answer as it is, not cut at MaxLines (its list of tags
			// was cut at P49 of 57 colonists in a test, 2026-10-19); AgentMapPicture keeps its lines under 100 characters.
			if (kinds.Count == 1 && kinds[0].mode == Mode.Mountain)
			{
				return AgentMapPicture.Make(c.map);
			}
			string text = Blocks(c.map, kinds, true, AgentPlace.ColonyCentre(c.map));
			return text ?? "Nothing to list.";
		}

		private static string Blocks(Map map, List<Kind> kinds, bool includeZero, IntVec3 centre)
		{
			List<string> all = new List<string>();
			foreach (Kind k in kinds)
			{
				int total;
				List<string> block = Block(map, k, centre, out total);
				if (total == 0 && !includeZero)
				{
					continue;
				}
				all.AddRange(block);
			}
			if (all.Count == 0)
			{
				return null;
			}
			return JoinLines(all);
		}

		private static List<string> Block(Map map, Kind k, IntVec3 centre, out int total)
		{
			List<string> lines = new List<string>();
			if (k.mode == Mode.Things)
			{
				List<Hit> hits = CollectHits(map, k)
					.OrderBy(h => AgentPlace.Tiles(h.cell, centre)).ThenBy(h => Dist2(h.cell, centre)).ThenBy(h => h.id).ToList();
				total = hits.Count;
				lines.Add(FormPlanner.UpperFirst(k.label) + ": " + Num(hits.Count) + " in view.");
				foreach (Hit h in hits.Take(ListLineCap))
				{
					lines.Add("  " + AgentPlace.Describe(map, h.cell) + (h.extra != null ? " " + h.extra : ""));
				}
				if (hits.Count > ListLineCap)
				{
					lines.Add("  and " + Num(hits.Count - ListLineCap) + " more");
				}
				return lines;
			}
			if (k.mode == Mode.Mountain)
			{
				// "list mountains on map." answers with a drawn picture of the whole map (AgentMapPicture.cs; user,
				// 2026-10-19: "if they say "list mountains on map", then the game just takes picture of the entire map
				// instead."). It was a text list of each mountain's spans, roof and ore before.
				total = 1;
				lines.Add(AgentMapPicture.Make(map));
				return lines;
			}

			List<Comp> comps = Components(map, k, centre)
				.OrderBy(x => AgentPlace.Tiles(x.nearest, centre)).ThenBy(x => Dist2(x.nearest, centre)).ToList();
			int cells = 0;
			foreach (Comp x in comps)
			{
				cells += x.size;
			}
			total = cells;
			if (k.mode == Mode.Ore)
			{
				lines.Add(FormPlanner.UpperFirst(k.label) + ": " + Num(comps.Count) + (comps.Count == 1 ? " vein, " : " veins, ")
					+ Num(cells) + (cells == 1 ? " cell" : " cells") + " in view.");
			}
			else
			{
				lines.Add(FormPlanner.UpperFirst(k.label) + ": " + Num(cells) + (cells == 1 ? " cell" : " cells") + " in view.");
			}
			foreach (Comp x in comps.Take(ListLineCap))
			{
				string line = "  " + AgentPlace.Describe(map, x.nearest);
				if (k.mode == Mode.Ore)
				{
					ThingDef def = x.key as ThingDef;
					if (k.showLabel && def != null && def.label != null)
					{
						line += " " + def.label;
					}
					line += " vein of " + Num(x.size) + (x.size == 1 ? " cell" : " cells");
				}
				else
				{
					line += " patch of " + Num(x.size) + (x.size == 1 ? " cell" : " cells");
				}
				lines.Add(line);
			}
			if (comps.Count > ListLineCap)
			{
				lines.Add("  and " + Num(comps.Count - ListLineCap) + " more");
			}
			return lines;
		}

		// ------------------------------------------------------------------------------------------------
		// Collecting
		// ------------------------------------------------------------------------------------------------

		private static List<Hit> CollectHits(Map map, Kind k)
		{
			List<Hit> hits = new List<Hit>();
			IEnumerable<Thing> things = k.things != null ? k.things(map) : null;
			if (things == null)
			{
				return hits;
			}
			Dictionary<int, string> names = k.numberedDef != null ? Numbered(map, k.numberedDef, k.numberedPrefix) : null;
			foreach (Thing t in things)
			{
				if (t == null || !t.Spawned || !AgentPlace.Visible(map, t.Position))
				{
					continue;
				}
				Hit h = new Hit();
				h.cell = t.Position;
				h.id = t.thingIDNumber;
				Pawn p = t as Pawn;
				if (p != null)
				{
					h.extra = p.LabelShort;
				}
				else
				{
					string extra = k.showLabel ? t.def.label : null;
					if (t.def.category == ThingCategory.Item && t.stackCount > 1)
					{
						extra = (extra == null ? "" : extra + " ") + "x" + t.stackCount.ToString(CultureInfo.InvariantCulture);
					}
					h.extra = extra;
				}
				string landmark;
				if (names != null && names.TryGetValue(t.thingIDNumber, out landmark))
				{
					h.extra = landmark;
				}
				hits.Add(h);
			}
			return hits;
		}

		// "ship chunk 1" ...: the Visible things of the def by thingIDNumber, at most 9 (as AgentPlace names them).
		private static Dictionary<int, string> Numbered(Map map, ThingDef def, string prefix)
		{
			Dictionary<int, string> result = new Dictionary<int, string>();
			if (def == null)
			{
				return result;
			}
			int n = 0;
			foreach (Thing t in map.listerThings.ThingsOfDef(def).Where(x => x != null && x.Spawned && AgentPlace.Visible(map, x.Position))
				.OrderBy(x => x.thingIDNumber))
			{
				if (n >= 9)
				{
					break;
				}
				n++;
				result[t.thingIDNumber] = prefix + " " + n.ToString(CultureInfo.InvariantCulture);
			}
			return result;
		}

		// 4-connected groups of Visible cells with the same non-null key (one scan of the map).
		private static List<Comp> Components(Map map, Kind k, IntVec3 centre)
		{
			int w = map.Size.x;
			int h = map.Size.z;
			object[] keys = new object[w * h];
			for (int z = 0; z < h; z++)
			{
				for (int x = 0; x < w; x++)
				{
					IntVec3 c = new IntVec3(x, 0, z);
					if (AgentPlace.Visible(map, c))
					{
						keys[z * w + x] = k.cellKey(map, c);
					}
				}
			}
			bool[] seen = new bool[w * h];
			List<Comp> result = new List<Comp>();
			Stack<int> todo = new Stack<int>();
			for (int i = 0; i < keys.Length; i++)
			{
				if (keys[i] == null || seen[i])
				{
					continue;
				}
				Comp comp = new Comp();
				comp.key = keys[i];
				comp.nearest = new IntVec3(i % w, 0, i / w);
				seen[i] = true;
				todo.Push(i);
				while (todo.Count > 0)
				{
					int cur = todo.Pop();
					int cx = cur % w;
					int cz = cur / w;
					comp.size++;
					IntVec3 cc = new IntVec3(cx, 0, cz);
					if (Closer(cc, comp.nearest, centre))
					{
						comp.nearest = cc;
					}
					for (int d = 0; d < 4; d++)
					{
						int nx = cx + DX[d];
						int nz = cz + DZ[d];
						if (nx < 0 || nz < 0 || nx >= w || nz >= h)
						{
							continue;
						}
						int ni = nz * w + nx;
						if (!seen[ni] && keys[ni] != null && ReferenceEquals(keys[ni], comp.key))
						{
							seen[ni] = true;
							todo.Push(ni);
						}
					}
				}
				result.Add(comp);
			}
			return result;
		}

		// ------------------------------------------------------------------------------------------------
		// Mountains (the "mountain bases" addition)
		// ------------------------------------------------------------------------------------------------

		private static bool IsRock(Map map, IntVec3 c)
		{
			Building b = c.GetEdifice(map);
			return b != null && b.def != null && b.def.building != null && b.def.building.isNaturalRock;
		}

		// Masses of Visible natural rock (4-neighbour), at least MinMass cells, nearest first (by the cell that is
		// the nearest rock face; a mass with no open face is ranked by its nearest rock cell).
		private static List<Mass> FindMasses(Map map, IntVec3 from)
		{
			int w = map.Size.x;
			int h = map.Size.z;
			bool[] rock = new bool[w * h];
			for (int z = 0; z < h; z++)
			{
				for (int x = 0; x < w; x++)
				{
					IntVec3 c = new IntVec3(x, 0, z);
					rock[z * w + x] = AgentPlace.Visible(map, c) && IsRock(map, c);
				}
			}
			bool[] seen = new bool[w * h];
			List<Mass> masses = new List<Mass>();
			Stack<int> todo = new Stack<int>();
			for (int i = 0; i < rock.Length; i++)
			{
				if (!rock[i] || seen[i])
				{
					continue;
				}
				Mass m = new Mass();
				seen[i] = true;
				todo.Push(i);
				while (todo.Count > 0)
				{
					int cur = todo.Pop();
					m.cells.Add(cur);
					for (int d = 0; d < 4; d++)
					{
						int nx = cur % w + DX[d];
						int nz = cur / w + DZ[d];
						if (nx < 0 || nz < 0 || nx >= w || nz >= h)
						{
							continue;
						}
						int ni = nz * w + nx;
						if (rock[ni] && !seen[ni])
						{
							seen[ni] = true;
							todo.Push(ni);
						}
					}
				}
				if (m.cells.Count < MinMass)
				{
					continue;
				}
				bool haveFace = false;
				IntVec3 bestFace = IntVec3.Invalid;
				IntVec3 bestAny = new IntVec3(m.cells[0] % w, 0, m.cells[0] / w);
				foreach (int ci in m.cells)
				{
					IntVec3 cell = new IntVec3(ci % w, 0, ci / w);
					if (Closer(cell, bestAny, from))
					{
						bestAny = cell;
					}
					if (haveFace && !Closer(cell, bestFace, from))
					{
						continue;
					}
					if (HasOpenSide(map, rock, cell))
					{
						bestFace = cell;
						haveFace = true;
					}
				}
				m.hasFace = haveFace;
				m.rank = haveFace ? bestFace : bestAny;
				masses.Add(m);
			}
			return masses.OrderBy(x => AgentPlace.Tiles(x.rank, from)).ThenBy(x => Dist2(x.rank, from)).ThenBy(x => x.cells[0]).ToList();
		}

		// A 4-neighbour that is Visible, not natural rock and walkable: a place to stand and mine from.
		private static bool HasOpenSide(Map map, bool[] rock, IntVec3 cell)
		{
			int w = map.Size.x;
			for (int d = 0; d < 4; d++)
			{
				IntVec3 n = new IntVec3(cell.x + DX[d], 0, cell.z + DZ[d]);
				if (!AgentPlace.Visible(map, n) || rock[n.z * w + n.x])
				{
					continue;
				}
				if (n.Walkable(map))
				{
					return true;
				}
			}
			return false;
		}

		// The lines after a mountain's first line: spans, counts, roof, ore.
		private static List<string> MountainDetail(Map map, Mass m, bool[] fogSeen)
		{
			List<string> lines = new List<string>();
			int w = map.Size.x;
			int h = map.Size.z;
			int minX = int.MaxValue;
			int minZ = int.MaxValue;
			int maxX = int.MinValue;
			int maxZ = int.MinValue;
			int thick = 0;
			int thin = 0;
			Dictionary<int, ThingDef> oreAt = new Dictionary<int, ThingDef>();
			foreach (int ci in m.cells)
			{
				int x = ci % w;
				int z = ci / w;
				minX = Math.Min(minX, x);
				maxX = Math.Max(maxX, x);
				minZ = Math.Min(minZ, z);
				maxZ = Math.Max(maxZ, z);
				IntVec3 cell = new IntVec3(x, 0, z);
				RoofDef roof = map.roofGrid.RoofAt(cell);
				if (roof != null)
				{
					if (roof.isThickRoof)
					{
						thick++;
					}
					else if (roof.isNatural)
					{
						thin++;
					}
				}
				Building b = cell.GetEdifice(map);
				if (b != null && b.def != null && b.def.building != null && b.def.building.isResourceRock)
				{
					oreAt[ci] = b.def;
				}
			}

			// Fogged cells 4-connected to the rock: counted only, nothing under them is looked at.
			int fog = 0;
			bool capped = false;
			Queue<int> queue = new Queue<int>();
			foreach (int ci in m.cells)
			{
				for (int d = 0; d < 4; d++)
				{
					TryFog(map, fogSeen, queue, ci % w + DX[d], ci / w + DZ[d], ref fog, ref minX, ref maxX, ref minZ, ref maxZ);
				}
			}
			while (queue.Count > 0 && !capped)
			{
				int cur = queue.Dequeue();
				for (int d = 0; d < 4; d++)
				{
					TryFog(map, fogSeen, queue, cur % w + DX[d], cur / w + DZ[d], ref fog, ref minX, ref maxX, ref minZ, ref maxZ);
				}
				if (fog >= MassCap)
				{
					capped = true;
				}
			}
			if (fog >= MassCap)
			{
				capped = true;
			}
			if (minX > maxX)
			{
				minX = maxX = 0;
				minZ = maxZ = 0;
			}

			lines.Add("  spans " + AgentPlace.DescribeRect(map, CellRect.FromLimits(minX, minZ, maxX, maxZ)) + ".");
			string rockText = m.cells.Count > MassCap ? Num(MassCap) + "+" : Num(m.cells.Count);
			lines.Add("  " + rockText + " rock cells in view, " + (capped ? Num(MassCap) + "+" : Num(fog))
				+ " unexplored cells behind them.");
			if (thick > 0 || thin > 0)
			{
				List<string> parts = new List<string>();
				if (thick > 0)
				{
					parts.Add(Num(thick) + " overhead mountain");
				}
				if (thin > 0)
				{
					parts.Add(Num(thin) + " thin rock");
				}
				lines.Add("  roof over the rock in view: " + string.Join(", ", parts.ToArray()) + ".");
			}
			lines.Add("  " + OreText(oreAt, w, h));
			return lines;
		}

		private static void TryFog(Map map, bool[] fogSeen, Queue<int> queue, int x, int z, ref int fog,
			ref int minX, ref int maxX, ref int minZ, ref int maxZ)
		{
			int w = map.Size.x;
			if (x < 0 || z < 0 || x >= w || z >= map.Size.z)
			{
				return;
			}
			int i = z * w + x;
			if (fogSeen[i] || !map.fogGrid.IsFogged(new IntVec3(x, 0, z)))
			{
				return;
			}
			fogSeen[i] = true;
			queue.Enqueue(i);
			fog++;
			minX = Math.Min(minX, x);
			maxX = Math.Max(maxX, x);
			minZ = Math.Min(minZ, z);
			maxZ = Math.Max(maxZ, z);
		}

		private static string OreText(Dictionary<int, ThingDef> oreAt, int w, int h)
		{
			if (oreAt.Count == 0)
			{
				return "no ore in view.";
			}
			Dictionary<ThingDef, int[]> perDef = new Dictionary<ThingDef, int[]>(); // [cells, veins]
			HashSet<int> seen = new HashSet<int>();
			Stack<int> todo = new Stack<int>();
			foreach (KeyValuePair<int, ThingDef> e in oreAt)
			{
				if (seen.Contains(e.Key))
				{
					continue;
				}
				int[] tally;
				if (!perDef.TryGetValue(e.Value, out tally))
				{
					tally = new int[2];
					perDef[e.Value] = tally;
				}
				tally[1]++;
				seen.Add(e.Key);
				todo.Push(e.Key);
				while (todo.Count > 0)
				{
					int cur = todo.Pop();
					tally[0]++;
					for (int d = 0; d < 4; d++)
					{
						int nx = cur % w + DX[d];
						int nz = cur / w + DZ[d];
						if (nx < 0 || nz < 0 || nx >= w || nz >= h)
						{
							continue;
						}
						int ni = nz * w + nx;
						ThingDef other;
						if (oreAt.TryGetValue(ni, out other) && other == e.Value && seen.Add(ni))
						{
							todo.Push(ni);
						}
					}
				}
			}
			List<string> parts = new List<string>();
			foreach (KeyValuePair<ThingDef, int[]> e in perDef.OrderByDescending(x => x.Value[0]).ThenBy(x => x.Key.label))
			{
				parts.Add(e.Key.label + " " + Num(e.Value[0]) + (e.Value[0] == 1 ? " cell (" : " cells (") + Num(e.Value[1])
					+ (e.Value[1] == 1 ? " vein)" : " veins)"));
			}
			return "ore in view: " + string.Join(", ", parts.ToArray()) + ".";
		}

		// ------------------------------------------------------------------------------------------------
		// Kinds: what the player's words match
		// ------------------------------------------------------------------------------------------------

		private static List<Kind> ResolveOrFault(string word)
		{
			List<Kind> kinds = Resolve(word);
			if (kinds.Count == 0)
			{
				throw new Fault(JoinLines(new List<string> { "'" + word + "' is not something the mod can look for. " + Allowed }));
			}
			return kinds;
		}

		private static List<Kind> Resolve(string typed)
		{
			string n = FormPlanner.Norm(typed);
			List<Kind> r = Candidates(n, typed);
			if (r.Count == 0 && n.EndsWith("es") && n.Length > 3)
			{
				r = Candidates(n.Substring(0, n.Length - 2), typed);
			}
			if (r.Count == 0 && n.EndsWith("s") && n.Length > 2)
			{
				r = Candidates(n.Substring(0, n.Length - 1), typed);
			}
			return r;
		}

		private static List<Kind> Candidates(string n, string typed)
		{
			List<Kind> r = new List<Kind>();
			if (n.Length == 0)
			{
				return r;
			}
			string[] metals = { "plasteel", "silver", "gold", "uranium", "jade" };
			foreach (string m in metals)
			{
				if (n == m)
				{
					r.Add(OreKind(typed + " ore", "Mineable" + FormPlanner.UpperFirst(m)));
					r.Add(ItemKind(typed + " lying around", FormPlanner.UpperFirst(m)));
					return r;
				}
				if (n == m + " ore")
				{
					r.Add(OreKind(typed, "Mineable" + FormPlanner.UpperFirst(m)));
					return r;
				}
			}
			switch (n)
			{
				case "steel":
					r.Add(OreKind(typed + " ore", "MineableSteel"));
					r.Add(ItemKind(typed + " lying around", "Steel"));
					return r;
				case "steel ore":
				case "compacted steel":
					r.Add(OreKind(typed, "MineableSteel"));
					return r;
				case "compacted machinery":
				case "machinery":
				case "component":
				case "components":
					r.Add(OreKind(typed, "MineableComponentsIndustrial"));
					return r;
				case "ore":
				case "ores":
				case "minerals":
				case "mineral":
					{
						Kind k = new Kind();
						k.label = typed;
						k.mode = Mode.Ore;
						k.showLabel = true;
						k.cellKey = (map, c) =>
						{
							Building b = c.GetEdifice(map);
							return b != null && b.def != null && b.def.building != null && b.def.building.isResourceRock ? (object)b.def : null;
						};
						r.Add(k);
						return r;
					}
				case "rock":
				case "rocks":
				case "stone":
				case "stones":
				case "rock wall":
				case "rock walls":
					{
						Kind k = new Kind();
						k.label = typed;
						k.mode = Mode.Patch;
						k.cellKey = (map, c) =>
						{
							Building b = c.GetEdifice(map);
							return b != null && b.def != null && b.def.building != null && b.def.building.isNaturalRock
								&& !b.def.building.isResourceRock ? CellMatch : null;
						};
						r.Add(k);
						return r;
					}
				case "mountain":
				case "mountains":
				case "rock face":
				case "rock faces":
				case "cliff":
				case "cliffs":
					{
						Kind k = new Kind();
						k.label = typed;
						k.mode = Mode.Mountain;
						r.Add(k);
						return r;
					}
				case "ship chunk":
				case "ship chunks":
					r.Add(NumberedKind(typed, ThingDefOf.ShipChunk, "ship chunk"));
					return r;
				case "geyser":
				case "geysers":
				case "steam geyser":
				case "steam geysers":
					r.Add(NumberedKind(typed, ThingDefOf.SteamGeyser, "steam geyser"));
					return r;
				case "tree":
				case "trees":
					r.Add(ThingsKind(typed, true, m => m.listerThings.ThingsInGroup(ThingRequestGroup.Plant)
						.Where(t => t.def.plant != null && t.def.plant.IsTree)));
					return r;
				case "berries":
				case "berry":
				case "berry bush":
				case "berry bushes":
					{
						ThingDef berry = DefDatabase<ThingDef>.GetNamedSilentFail("Plant_Berry");
						r.Add(ThingsKind(typed, false, m => berry == null ? new List<Thing>() : m.listerThings.ThingsOfDef(berry)));
						return r;
					}
				case "harvestable plants":
				case "harvestable plant":
				case "wild plants":
				case "wild plant":
				case "food plants":
				case "food plant":
					r.Add(ThingsKind(typed, true, m => m.listerThings.ThingsInGroup(ThingRequestGroup.Plant)
						.Where(t => t.def.plant != null && t.def.plant.Harvestable && !t.def.plant.IsTree)));
					return r;
				case "rich soil":
					r.Add(TerrainKind(typed, t => t.fertility >= 1.4f));
					return r;
				case "soil":
				case "fertile soil":
					r.Add(TerrainKind(typed, t => t.fertility >= 1.0f));
					return r;
				case "water":
					r.Add(TerrainKind(typed, t => t.IsWater));
					return r;
				case "deep water":
					r.Add(TerrainKind(typed, IsDeepWater));
					return r;
				case "shallow water":
					r.Add(TerrainKind(typed, t => t.IsWater && !IsDeepWater(t)));
					return r;
				case "river":
					r.Add(TerrainKind(typed, t => t.IsRiver));
					return r;
				case "marsh":
					r.Add(TerrainKind(typed, t => t == TerrainDefOf.Marsh));
					return r;
				case "mud":
					r.Add(TerrainKind(typed, t => t == TerrainDefOf.Mud));
					return r;
				case "sand":
					r.Add(TerrainKind(typed, t => t == TerrainDefOf.Sand));
					return r;
				case "gravel":
					r.Add(TerrainKind(typed, t => t == TerrainDefOf.Gravel));
					return r;
				case "animals":
				case "animal":
				case "wild animals":
				case "wild animal":
					r.Add(PawnsKind(typed, p => p.RaceProps != null && p.RaceProps.Animal && p.Faction != Faction.OfPlayer
						&& !p.HostileTo(Faction.OfPlayer)));
					return r;
				case "hostiles":
				case "hostile":
				case "enemies":
				case "enemy":
				case "raiders":
				case "raider":
					r.Add(PawnsKind(typed, p => p.HostileTo(Faction.OfPlayer)));
					return r;
				case "colonists":
				case "colonist":
					r.Add(ThingsKind(typed, false, m => m.mapPawns.FreeColonistsSpawned.Cast<Thing>()));
					return r;
				case "prisoners":
				case "prisoner":
					r.Add(ThingsKind(typed, false, m => m.mapPawns.PrisonersOfColonySpawned.Cast<Thing>()));
					return r;
				case "tame animals":
				case "tame animal":
					r.Add(ThingsKind(typed, false, m => m.mapPawns.SpawnedColonyAnimals.Cast<Thing>()));
					return r;
				case "items":
				case "item":
					r.Add(ThingsKind(typed, true, m => m.listerThings.AllThings
						.Where(t => t.def.category == ThingCategory.Item && t.Spawned && t.GetSlotGroup() == null)));
					return r;
			}
			return DefMatches(n);
		}

		// By game label, in this order: a pawn kind / race, a plant def, a building def, an item def, a terrain def.
		private static List<Kind> DefMatches(string n)
		{
			List<Kind> r = new List<Kind>();
			List<ThingDef> thingDefs = DefDatabase<ThingDef>.AllDefsListForReading;

			HashSet<PawnKindDef> pawnKinds = new HashSet<PawnKindDef>();
			HashSet<ThingDef> races = new HashSet<ThingDef>();
			string pawnLabel = null;
			foreach (PawnKindDef d in DefDatabase<PawnKindDef>.AllDefsListForReading)
			{
				if (d != null && d.label != null && FormPlanner.SameName(d.label, n))
				{
					pawnKinds.Add(d);
					pawnLabel = pawnLabel ?? d.label;
				}
			}
			foreach (ThingDef d in thingDefs)
			{
				if (d != null && d.race != null && d.label != null && FormPlanner.SameName(d.label, n))
				{
					races.Add(d);
					pawnLabel = pawnLabel ?? d.label;
				}
			}
			if (pawnKinds.Count + races.Count > 0)
			{
				r.Add(PawnsKind(pawnLabel, p => !p.Dead && (pawnKinds.Contains(p.kindDef) || races.Contains(p.def))));
				return r;
			}

			List<ThingDef> plants = thingDefs.Where(d => d != null && d.plant != null && d.label != null && FormPlanner.SameName(d.label, n)).ToList();
			if (plants.Count > 0)
			{
				r.Add(ThingsKind(plants[0].label, plants.Count > 1, m => plants.SelectMany(d => m.listerThings.ThingsOfDef(d))));
				return r;
			}

			List<ThingDef> buildings = thingDefs.Where(d => d != null && d.category == ThingCategory.Building && d.label != null
				&& FormPlanner.SameName(d.label, n)).ToList();
			if (buildings.Count > 0)
			{
				ThingDef first = buildings[0];
				if (first.building != null && first.building.isNaturalRock)
				{
					HashSet<ThingDef> rockDefs = new HashSet<ThingDef>(buildings.Where(d => d.building != null && d.building.isNaturalRock));
					Kind k = new Kind();
					k.label = first.label;
					k.mode = first.building.isResourceRock ? Mode.Ore : Mode.Patch;
					k.showLabel = rockDefs.Count > 1;
					k.cellKey = (map, c) =>
					{
						Building b = c.GetEdifice(map);
						return b != null && rockDefs.Contains(b.def) ? (k.mode == Mode.Ore ? (object)b.def : CellMatch) : null;
					};
					r.Add(k);
					return r;
				}
				r.Add(ThingsKind(first.label, buildings.Count > 1, m => buildings.SelectMany(d => m.listerThings.ThingsOfDef(d))));
				return r;
			}

			List<ThingDef> items = ItemDefsByName(n);
			if (items.Count > 0)
			{
				r.Add(ThingsKind(items[0].label, items.Count > 1, m => items.SelectMany(d => m.listerThings.ThingsOfDef(d))));
				return r;
			}

			List<TerrainDef> terrains = DefDatabase<TerrainDef>.AllDefsListForReading
				.Where(d => d != null && d.label != null && FormPlanner.SameName(d.label, n)).ToList();
			if (terrains.Count > 0)
			{
				HashSet<TerrainDef> set = new HashSet<TerrainDef>(terrains);
				r.Add(TerrainKind(terrains[0].label, t => set.Contains(t)));
			}
			return r;
		}

		private static bool IsDeepWater(TerrainDef t)
		{
			return t.IsWater && (t.passability == Traversability.Impassable || t == TerrainDefOf.WaterDeep
				|| t == TerrainDefOf.WaterOceanDeep || t == TerrainDefOf.WaterMovingChestDeep);
		}

		private static Kind OreKind(string label, string defName)
		{
			ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
			Kind k = new Kind();
			k.label = label;
			k.mode = Mode.Ore;
			k.cellKey = (map, c) =>
			{
				Building b = c.GetEdifice(map);
				return def != null && b != null && b.def == def ? (object)def : null;
			};
			return k;
		}

		// Item defs by name: the mod's own matcher first (Executor.FindItemDefs), then a plain label search.
		private static List<ThingDef> ItemDefsByName(string name)
		{
			List<ThingDef> defs = Executor.FindItemDefs(name).Where(d => d != null && d.category == ThingCategory.Item).ToList();
			if (defs.Count > 0)
			{
				return defs;
			}
			return DefDatabase<ThingDef>.AllDefsListForReading
				.Where(d => d != null && d.category == ThingCategory.Item && d.label != null && FormPlanner.SameName(d.label, name)).ToList();
		}

		// The item lying around: spawned, Visible, not in a storage.
		private static Kind ItemKind(string label, string name)
		{
			List<ThingDef> defs = ItemDefsByName(name);
			Kind k = new Kind();
			k.label = label;
			k.mode = Mode.Things;
			k.things = m => defs.SelectMany(d => m.listerThings.ThingsOfDef(d)).Where(t => t.Spawned && t.GetSlotGroup() == null);
			return k;
		}

		private static Kind NumberedKind(string label, ThingDef def, string prefix)
		{
			Kind k = new Kind();
			k.label = label;
			k.mode = Mode.Things;
			k.numberedDef = def;
			k.numberedPrefix = prefix;
			k.things = m => def == null ? new List<Thing>() : m.listerThings.ThingsOfDef(def);
			return k;
		}

		private static Kind ThingsKind(string label, bool showLabel, Func<Map, IEnumerable<Thing>> things)
		{
			Kind k = new Kind();
			k.label = label;
			k.mode = Mode.Things;
			k.showLabel = showLabel;
			k.things = things;
			return k;
		}

		private static Kind PawnsKind(string label, Func<Pawn, bool> test)
		{
			return ThingsKind(label, false, m => m.mapPawns.AllPawnsSpawned.Where(p => p != null && !p.Dead && test(p)).Cast<Thing>());
		}

		private static Kind TerrainKind(string label, Func<TerrainDef, bool> test)
		{
			Kind k = new Kind();
			k.label = label;
			k.mode = Mode.Patch;
			k.cellKey = (map, c) =>
			{
				TerrainDef t = map.terrainGrid.TerrainAt(c);
				return t != null && test(t) ? CellMatch : null;
			};
			return k;
		}

		// ------------------------------------------------------------------------------------------------
		// Text helpers
		// ------------------------------------------------------------------------------------------------

		private static string Tidy(string s)
		{
			return (s ?? "").Trim().TrimEnd('.').Trim();
		}

		private static string Num(int n)
		{
			return n.ToString("N0", CultureInfo.InvariantCulture);
		}

		private static string Tiles(int n)
		{
			return n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " tile" : " tiles");
		}

		private static long Dist2(IntVec3 a, IntVec3 b)
		{
			long dx = a.x - b.x;
			long dz = a.z - b.z;
			return dx * dx + dz * dz;
		}

		// a is nearer to `from` than b: fewer tiles, then the shorter straight line.
		private static bool Closer(IntVec3 a, IntVec3 b, IntVec3 from)
		{
			int ta = AgentPlace.Tiles(a, from);
			int tb = AgentPlace.Tiles(b, from);
			if (ta != tb)
			{
				return ta < tb;
			}
			return Dist2(a, from) < Dist2(b, from);
		}

		// Every line under 100 characters (wrapped at a space, the rest indented), the whole answer at most
		// MaxLines lines with a last line saying it was cut.
		private static string JoinLines(List<string> lines)
		{
			List<string> wrapped = new List<string>();
			foreach (string raw in lines)
			{
				foreach (string line in raw.Split('\n'))
				{
					WrapOne(line, wrapped);
				}
			}
			if (wrapped.Count > MaxLines)
			{
				wrapped = wrapped.Take(MaxLines - 1).ToList();
				wrapped.Add(CutLine);
			}
			return string.Join("\n", wrapped.ToArray());
		}

		private static void WrapOne(string line, List<string> result)
		{
			// A line that is a file path stays whole: "...Ludeon Studios\..." split at its space can not be opened
			// (the map picture's path, AgentMapPicture.cs, 2026-10-19).
			if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^[A-Za-z]:[\\/]"))
			{
				result.Add(line);
				return;
			}
			int indent = 0;
			while (indent < line.Length && line[indent] == ' ')
			{
				indent++;
			}
			string pad = new string(' ', indent + 2);
			string rest = line;
			while (rest.Length > MaxCols)
			{
				int cut = rest.LastIndexOf(' ', MaxCols - 1);
				if (cut <= pad.Length)
				{
					cut = MaxCols - 1;
				}
				result.Add(rest.Substring(0, cut).TrimEnd());
				rest = pad + rest.Substring(cut).TrimStart();
			}
			result.Add(rest);
		}
	}
}
