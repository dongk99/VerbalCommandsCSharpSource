using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Agent 03 (agent_player\out\03\forms.md, section 4 and sonnet_briefs\AgentMap.md): the text map questions.
	// A Claude agent plays only through text orders and has no screen; these three questions print the map as
	// text so it can see the land. They change nothing.
	//   query map          LAYER?          coarse map of the whole map, 1 character per 10x10 block
	//   query map around   LAYER?, SPOT    1 character per cell, 41x41 centred on the SPOT cell, clipped to the map
	//   query map area     LAYER?, AREA    1 character per cell, at most 60 wide and 50 tall (the south-west part)
	// The router script has already parsed the sentence and checked the slots; Query gets the form name and slots.
	//
	// MAIN THREAD ONLY (it reads Map state; RouterGame moves the call onto the main thread first). Read-only.
	// Fair play: a cell under fog is printed as '?' and nothing else about it is looked at or counted
	// (AgentPlace.Visible is the single test). No dev mode, no Debug* classes.
	//
	// Game members relied on (sources\RimWorldDecompiled\):
	//   Verse\Map.cs:77 zoneManager, :87 areaManager, :107 fogGrid, :113 terrainGrid, :117 roofGrid, :262 Size
	//   Verse\TerrainGrid.cs:89 TerrainAt(IntVec3); Verse\TerrainDef.cs:153 natural, :320 IsRiver, :324 IsWater,
	//     :336 IsFloor; Verse\BuildableDef.cs:13 passability, :19 fertility
	//   RimWorld\TerrainDefOf.cs:28 WaterDeep, :32 WaterMovingChestDeep, :36 WaterOceanDeep, :48 Marsh, :50 Mud
	//   Verse\GridsUtility.cs:443 GetEdifice, :67 GetThingList, :158 GetPlant
	//   RimWorld\BuildingProperties.cs:87 isNaturalRock, :89 isResourceRock, :267 mineableThing, :55 isWall
	//   Verse\ThingDef.cs:880 IsWall (the game's own wall test: building.isWall), :387 building, :395 plant, :15 category
	//   Verse\Def.cs:12 defName; Verse\ThingCategory.cs:7 Item; RimWorld\ThingDefOf.cs:171 ShipChunk, :167 SteamGeyser
	//   RimWorld\Building_Door.cs:9 Building_Door; RimWorld\Blueprint.cs:9 Blueprint; RimWorld\Frame.cs:11 Frame
	//   RimWorld\PlantProperties.cs:203 Harvestable, :207 IsTree; Verse\Thing.cs:17 def, :261 Position, :390 Faction
	//   Verse\ZoneManager.cs:78 ZoneAt; RimWorld\Zone_Stockpile.cs:7; RimWorld\Zone_Growing.cs:10
	//   Verse\RoofGrid.cs:80 RoofAt(IntVec3); Verse\RoofDef.cs:5 isNatural, :7 isThickRoof
	//   Verse\AreaManager.cs:19 BuildRoof, :21 NoRoof (the player's roof marks); Verse\Area.cs:60 this[IntVec3]
	//   RimWorld\GenHostility.cs:118 HostileTo(Thing, Faction); RimWorld\Faction.cs:214 OfPlayer
	//   Verse\Pawn.cs:518 IsColonist, :624 IsPrisonerOfColony, :648 IsSlaveOfColony, :275 KindLabel,
	//     :956 LabelShort, :213 Name, :225 RaceProps; Verse\RaceProperties.cs:265 Animal; Verse\Name.cs:26 Numerical,
	//     :7 ToStringShort
	//   Verse\CellRect.cs:84 Width, :100 Height, :339 constructor, :352 FromLimits, :753 ClipInsideMap
	public static class AgentMap
	{
		private const int AroundHalf = 20;     // 41 by 41 around a SPOT
		private const int MaxAreaW = 60;
		private const int MaxAreaH = 50;
		private const int Block = 10;
		private const int MaxLine = 99;        // every printed line is under 100 characters
		private const int MaxEntries = 10;

		private const int NTerr = 1;
		private const int NEdi = 2;
		private const int NThings = 4;
		private const int NPlant = 8;
		private const int NZone = 16;
		private const int NRoof = 32;

		// Characters in the order the legend lists them.
		private const string LegendOrder = "?#smp$gujo~-=,;\":._+TbC^iWDBxzfd@PahMXtrR";

		private enum Layer
		{
			Overview,
			Terrain,
			Resources,
			Buildings,
			Roof,
			Creatures
		}

		// What one visible cell holds (every field is 0 / false until Gather fills it).
		private sealed class Facts
		{
			public bool fog;
			public char terr;      // terrain class
			public bool water;     // terrain class is = ~ -
			public bool rock;      // natural rock edifice
			public char edi;       // # s m p $ g u j o (rock), W D B C (other edifice or ship chunk), else 0
			public bool geyser;
			public bool blueprint;
			public bool item;
			public char plant;     // T, b or 0
			public char zone;      // z, f, d or 0
			public bool hostile;
			public bool colonist;
			public bool prisoner;
			public bool animal;
			public char roof;      // M X t r R o .
			public char roofBase;  // M t r o .

			public void Reset()
			{
				fog = false;
				terr = '_';
				water = false;
				rock = false;
				edi = '\0';
				geyser = false;
				blueprint = false;
				item = false;
				plant = '\0';
				zone = '\0';
				hostile = false;
				colonist = false;
				prisoner = false;
				animal = false;
				roof = '.';
				roofBase = '.';
			}
		}

		private sealed class PawnEntry
		{
			public Pawn pawn;
			public char kind; // h @ P a
		}

		private sealed class Entry
		{
			public long dist;
			public int x;
			public int z;
			public string text;
		}

		private sealed class Env
		{
			public Map map;
			public Area noRoof;
			public Area buildRoof;
		}

		// null when c.form is not one of the three forms; else the answer. May throw Fault (the caller catches it).
		public static QueryReply Query(FormContext c)
		{
			if (c == null)
			{
				return null;
			}
			string form = c.form;
			if (form != "query map" && form != "query map around" && form != "query map area")
			{
				return null;
			}
			Map map = c.map;
			if (map == null)
			{
				throw new Fault("there is no map to look at right now.");
			}
			Layer layer;
			string layerName;
			ParseLayer(c.Has("LAYER") ? c.Text("LAYER") : null, out layer, out layerName);
			if (form == "query map")
			{
				return QueryReply.Ok(Coarse(map, layer, layerName));
			}
			if (form == "query map around")
			{
				string spotText = c.Has("SPOT") ? c.Text("SPOT") : null;
				if (string.IsNullOrWhiteSpace(spotText))
				{
					throw new Fault("no place was given for the map.");
				}
				IntVec3 cell;
				string reason;
				if (!AgentPlace.TryCell(map, spotText, out cell, out reason))
				{
					throw new Fault(reason ?? "that place could not be read.");
				}
				CellRect win = new CellRect(cell.x - AroundHalf, cell.z - AroundHalf, 2 * AroundHalf + 1, 2 * AroundHalf + 1).ClipInsideMap(map);
				string title = "Map around " + spotText.Trim() + " = " + AgentPlace.Describe(map, cell) + " (" + layerName + "), ";
				return QueryReply.Ok(Detail(map, layer, win, cell, title, null));
			}
			string areaText = c.Has("AREA") ? c.Text("AREA") : null;
			if (string.IsNullOrWhiteSpace(areaText))
			{
				throw new Fault("no area was given for the map.");
			}
			CellRect rect;
			string why;
			if (!AgentPlace.TryRect(map, areaText, out rect, out why))
			{
				throw new Fault(why ?? "that area could not be read.");
			}
			CellRect part = rect;
			string[] note = null;
			if (rect.Width > MaxAreaW || rect.Height > MaxAreaH)
			{
				part = CellRect.FromLimits(rect.minX, rect.minZ,
					rect.minX + Math.Min(rect.Width, MaxAreaW) - 1, rect.minZ + Math.Min(rect.Height, MaxAreaH) - 1);
				note = new string[]
				{
					"Only " + AgentPlace.DescribeRect(map, part) + " is shown;",
					"ask \"show map of X, Z to X, Z.\" for the rest."
				};
			}
			IntVec3 centre = new IntVec3(part.minX + (part.maxX - part.minX) / 2, 0, part.minZ + (part.maxZ - part.minZ) / 2);
			string areaTitle = "Map of " + AgentPlace.DescribeRect(map, part) + " (" + layerName + "), ";
			return QueryReply.Ok(Detail(map, layer, part, centre, areaTitle, note));
		}

		// ------------------------------------------------------------------------------------------------
		// Layer
		// ------------------------------------------------------------------------------------------------

		private static void ParseLayer(string text, out Layer layer, out string name)
		{
			string t = (text ?? "").Trim().ToLowerInvariant();
			switch (t)
			{
				case "":
					layer = Layer.Overview;
					name = "all layers";
					return;
				case "terrain":
				case "land":
				case "ground":
				case "soil":
					layer = Layer.Terrain;
					name = "terrain";
					return;
				case "resources":
				case "ore":
					layer = Layer.Resources;
					name = "resources";
					return;
				case "buildings":
				case "zones":
					layer = Layer.Buildings;
					name = "buildings";
					return;
				case "roof":
					layer = Layer.Roof;
					name = "roof";
					return;
				case "creatures":
				case "pawns":
					layer = Layer.Creatures;
					name = "creatures";
					return;
			}
			throw new Fault("'" + t + "' is not a map layer: use terrain, resources, buildings, roof or creatures.");
		}

		private static int NeedFor(Layer layer)
		{
			switch (layer)
			{
				case Layer.Overview:
					return NTerr | NEdi | NThings | NPlant | NZone;
				case Layer.Terrain:
					return NTerr | NEdi;
				case Layer.Resources:
					return NTerr | NEdi | NThings | NPlant;
				case Layer.Buildings:
					return NTerr | NEdi | NThings | NZone;
				case Layer.Roof:
					return NRoof;
				default:
					return NTerr | NEdi | NThings;
			}
		}

		// ------------------------------------------------------------------------------------------------
		// One cell: read what is there (Visible cells only)
		// ------------------------------------------------------------------------------------------------

		private static Env MakeEnv(Map map)
		{
			Env e = new Env();
			e.map = map;
			e.noRoof = map.areaManager.NoRoof;
			e.buildRoof = map.areaManager.BuildRoof;
			return e;
		}

		private static void Gather(Env env, IntVec3 c, int need, Facts f, List<PawnEntry> pawnSink)
		{
			f.Reset();
			Map map = env.map;
			if (!AgentPlace.Visible(map, c))
			{
				f.fog = true;
				return;
			}
			if ((need & NTerr) != 0)
			{
				f.terr = TerrainClass(map.terrainGrid.TerrainAt(c));
				f.water = f.terr == '=' || f.terr == '~' || f.terr == '-';
			}
			if ((need & (NEdi | NThings)) != 0)
			{
				ClassifyEdifice(c.GetEdifice(map), f);
			}
			if ((need & NThings) != 0)
			{
				List<Thing> list = c.GetThingList(map);
				for (int i = 0; i < list.Count; i++)
				{
					Thing t = list[i];
					if (t == null || t.def == null)
					{
						continue;
					}
					Pawn p = t as Pawn;
					if (p != null)
					{
						char kind = PawnKind(p);
						if (kind == '\0')
						{
							continue;
						}
						if (kind == 'h')
						{
							f.hostile = true;
						}
						else if (kind == '@')
						{
							f.colonist = true;
						}
						else if (kind == 'P')
						{
							f.prisoner = true;
						}
						else
						{
							f.animal = true;
						}
						if (pawnSink != null)
						{
							PawnEntry pe = new PawnEntry();
							pe.pawn = p;
							pe.kind = kind;
							pawnSink.Add(pe);
						}
						continue;
					}
					if (t is Blueprint || t is Frame)
					{
						f.blueprint = true;
					}
					else if (t.def == ThingDefOf.SteamGeyser)
					{
						f.geyser = true;
					}
					else if (t.def == ThingDefOf.ShipChunk)
					{
						if (f.edi == '\0')
						{
							f.edi = 'C';
						}
					}
					else if (t.def.category == ThingCategory.Item)
					{
						f.item = true;
					}
				}
			}
			if ((need & NPlant) != 0)
			{
				Plant plant = c.GetPlant(map);
				if (plant != null && plant.def != null && plant.def.plant != null)
				{
					if (plant.def.plant.IsTree)
					{
						f.plant = 'T';
					}
					else if (plant.def.plant.Harvestable)
					{
						f.plant = 'b';
					}
				}
			}
			if ((need & NZone) != 0)
			{
				Zone zone = map.zoneManager.ZoneAt(c);
				if (zone != null)
				{
					f.zone = zone is Zone_Stockpile ? 'z' : (zone is Zone_Growing ? 'f' : 'd');
				}
			}
			if ((need & NRoof) != 0)
			{
				RoofDef roof = map.roofGrid.RoofAt(c);
				bool noMark = env.noRoof != null && env.noRoof[c];
				bool buildMark = env.buildRoof != null && env.buildRoof[c];
				if (roof != null && roof.isThickRoof)
				{
					f.roof = 'M';
					f.roofBase = 'M';
				}
				else if (roof != null)
				{
					f.roofBase = roof.isNatural ? 't' : 'r';
					f.roof = noMark ? 'X' : f.roofBase;
				}
				else if (buildMark)
				{
					f.roof = 'R';
					f.roofBase = '.';
				}
				else if (noMark)
				{
					f.roof = 'o';
					f.roofBase = 'o';
				}
				else
				{
					f.roof = '.';
					f.roofBase = '.';
				}
			}
		}

		// h hostile, @ colonist, P prisoner or slave of the colony, a other animal; 0 for anything else.
		private static char PawnKind(Pawn p)
		{
			if (p.HostileTo(Faction.OfPlayer))
			{
				return 'h';
			}
			if (p.IsPrisonerOfColony || p.IsSlaveOfColony)
			{
				return 'P';
			}
			if (p.IsColonist)
			{
				return '@';
			}
			if (p.RaceProps != null && p.RaceProps.Animal)
			{
				return 'a';
			}
			return '\0';
		}

		private static void ClassifyEdifice(Building b, Facts f)
		{
			if (b == null || b.def == null)
			{
				return;
			}
			ThingDef d = b.def;
			if (d.building != null && d.building.isNaturalRock)
			{
				f.rock = true;
				f.edi = d.building.isResourceRock ? OreChar(d) : '#';
				return;
			}
			if (b is Frame)
			{
				return; // a frame is a blueprint-or-frame: the thing list sets f.blueprint
			}
			if (b is Building_Door)
			{
				f.edi = 'D';
			}
			else if (d == ThingDefOf.ShipChunk)
			{
				f.edi = 'C';
			}
			else if (d.IsWall)
			{
				f.edi = 'W';
			}
			else
			{
				f.edi = 'B';
			}
		}

		private static char OreChar(ThingDef d)
		{
			switch (d.defName)
			{
				case "MineableSteel":
					return 's';
				case "MineableComponentsIndustrial":
					return 'm';
				case "MineablePlasteel":
					return 'p';
				case "MineableSilver":
					return '$';
				case "MineableGold":
					return 'g';
				case "MineableUranium":
					return 'u';
				case "MineableJade":
					return 'j';
			}
			string item = d.building != null && d.building.mineableThing != null ? d.building.mineableThing.defName : null;
			switch (item)
			{
				case "Steel":
					return 's';
				case "ComponentIndustrial":
					return 'm';
				case "Plasteel":
					return 'p';
				case "Silver":
					return '$';
				case "Gold":
					return 'g';
				case "Uranium":
					return 'u';
				case "Jade":
					return 'j';
			}
			return 'o';
		}

		private static char TerrainClass(TerrainDef t)
		{
			if (t == null)
			{
				return '_';
			}
			if (t.IsRiver)
			{
				return '=';
			}
			if (t.IsWater)
			{
				if (t.passability == Traversability.Impassable || t == TerrainDefOf.WaterDeep
					|| t == TerrainDefOf.WaterOceanDeep || t == TerrainDefOf.WaterMovingChestDeep)
				{
					return '~';
				}
				return '-';
			}
			if (t == TerrainDefOf.Marsh)
			{
				return ',';
			}
			if (t == TerrainDefOf.Mud)
			{
				return ';';
			}
			if (t.IsFloor && !t.natural)
			{
				return '+';
			}
			if (t.fertility >= 1.4f)
			{
				return '"';
			}
			if (t.fertility >= 1.0f)
			{
				return '.';
			}
			if (t.fertility > 0f)
			{
				return ':';
			}
			return '_';
		}

		// ------------------------------------------------------------------------------------------------
		// Detail map: one character per cell
		// ------------------------------------------------------------------------------------------------

		private static char PawnChar(Facts f)
		{
			if (f.hostile)
			{
				return 'h';
			}
			if (f.colonist)
			{
				return '@';
			}
			if (f.prisoner)
			{
				return 'P';
			}
			if (f.animal)
			{
				return 'a';
			}
			return '\0';
		}

		private static bool IsOre(char c)
		{
			return c == 's' || c == 'm' || c == 'p' || c == '$' || c == 'g' || c == 'u' || c == 'j' || c == 'o';
		}

		private static char Background(Facts f)
		{
			if (f.rock)
			{
				return '#';
			}
			return f.water ? '~' : '.';
		}

		private static char DetailChar(Layer layer, Facts f)
		{
			if (f.fog)
			{
				return '?';
			}
			char ch;
			switch (layer)
			{
				case Layer.Overview:
					ch = PawnChar(f);
					if (ch != '\0')
					{
						return ch;
					}
					if (f.edi != '\0')
					{
						return f.edi;
					}
					if (f.geyser)
					{
						return '^';
					}
					if (f.blueprint)
					{
						return 'x';
					}
					if (f.item)
					{
						return 'i';
					}
					if (f.plant != '\0')
					{
						return f.plant;
					}
					if (f.zone != '\0')
					{
						return f.zone;
					}
					return f.terr;
				case Layer.Terrain:
					return f.rock ? '#' : f.terr;
				case Layer.Resources:
					if (IsOre(f.edi) || f.edi == 'C')
					{
						return f.edi;
					}
					if (f.geyser)
					{
						return '^';
					}
					if (f.plant == 'T' || f.plant == 'b')
					{
						return f.plant;
					}
					if (f.item)
					{
						return 'i';
					}
					return Background(f);
				case Layer.Buildings:
					if (f.edi == 'W' || f.edi == 'D' || f.edi == 'B')
					{
						return f.edi;
					}
					if (f.edi == 'C')
					{
						return 'B'; // a ship chunk is a building: it blocks the way
					}
					if (f.blueprint)
					{
						return 'x';
					}
					if (f.zone != '\0')
					{
						return f.zone;
					}
					if (f.rock)
					{
						return '#';
					}
					if (f.water)
					{
						return '~';
					}
					return f.terr == '+' ? '+' : '.';
				case Layer.Roof:
					return f.roof;
				default:
					ch = PawnChar(f);
					return ch != '\0' ? ch : Background(f);
			}
		}

		private static string Detail(Map map, Layer layer, CellRect win, IntVec3 centre, string titleHead, string[] note)
		{
			int w = win.Width;
			int h = win.Height;
			int need = NeedFor(layer) | NThings | NEdi;
			Env env = MakeEnv(map);
			Facts f = new Facts();
			List<PawnEntry> pawns = new List<PawnEntry>();
			bool[] seen = new bool[128];
			List<string> rows = new List<string>();
			for (int z = win.maxZ; z >= win.minZ; z--)
			{
				char[] row = new char[w];
				for (int x = win.minX; x <= win.maxX; x++)
				{
					Gather(env, new IntVec3(x, 0, z), need, f, pawns);
					char ch = DetailChar(layer, f);
					row[x - win.minX] = ch;
					seen[ch] = true;
				}
				rows.Add(z.ToString(CultureInfo.InvariantCulture).PadLeft(3) + " " + new string(row));
			}

			List<string> lines = new List<string>();
			AddWrapped(lines, "", new string[]
			{
				titleHead.TrimEnd(),
				w.ToString(CultureInfo.InvariantCulture) + " by " + h.ToString(CultureInfo.InvariantCulture) + " cells,",
				"x " + win.minX.ToString(CultureInfo.InvariantCulture) + " to " + win.maxX.ToString(CultureInfo.InvariantCulture) + ",",
				"z " + win.minZ.ToString(CultureInfo.InvariantCulture) + " to " + win.maxZ.ToString(CultureInfo.InvariantCulture) + ";",
				"north is up."
			}, "");
			AddRuler(lines, w, win.minX, 1);
			lines.AddRange(rows);
			AddLegend(lines, seen, layer);

			AddEntryLine(lines, "Colonists", CollectPawns(map, pawns, '@', centre));
			AddEntryLine(lines, "Hostiles", CollectPawns(map, pawns, 'h', centre));
			AddEntryLine(lines, "Prisoners", CollectPawns(map, pawns, 'P', centre));
			AddEntryLine(lines, "Animals", CollectPawns(map, pawns, 'a', centre));
			AddEntryLine(lines, "Landmarks", CollectLandmarks(map, win, centre));
			if (note != null)
			{
				AddWrapped(lines, "", note, "");
			}
			return string.Join("\n", lines.ToArray());
		}

		private static List<Entry> CollectPawns(Map map, List<PawnEntry> pawns, char kind, IntVec3 centre)
		{
			List<Entry> list = new List<Entry>();
			for (int i = 0; i < pawns.Count; i++)
			{
				PawnEntry pe = pawns[i];
				if (pe.kind != kind)
				{
					continue;
				}
				Pawn p = pe.pawn;
				IntVec3 pos = p.Position;
				string text;
				if (kind == 'h')
				{
					text = p.KindLabel;
				}
				else if (kind == 'a')
				{
					bool tame = p.Faction != null && p.Faction == Faction.OfPlayer;
					string name = p.Name != null && !p.Name.Numerical ? p.Name.ToStringShort : null;
					text = p.KindLabel + (tame ? (name != null ? " (" + name + ", tame)" : " (tame)") : " (wild)");
				}
				else
				{
					text = p.LabelShort;
				}
				list.Add(MakeEntry(pos, centre, text));
			}
			return list;
		}

		private static List<Entry> CollectLandmarks(Map map, CellRect win, IntVec3 centre)
		{
			List<Entry> list = new List<Entry>();
			foreach (string name in AgentPlace.LandmarkNames(map))
			{
				if (!name.StartsWith("ship chunk ", StringComparison.Ordinal) && !name.StartsWith("steam geyser ", StringComparison.Ordinal))
				{
					continue;
				}
				IntVec3 cell;
				if (AgentPlace.TryLandmark(map, name, out cell) && win.Contains(cell) && AgentPlace.Visible(map, cell))
				{
					list.Add(MakeEntry(cell, centre, name));
				}
			}
			return list;
		}

		private static Entry MakeEntry(IntVec3 pos, IntVec3 centre, string text)
		{
			Entry e = new Entry();
			long dx = pos.x - centre.x;
			long dz = pos.z - centre.z;
			e.dist = dx * dx + dz * dz;
			e.x = pos.x;
			e.z = pos.z;
			e.text = pos.x.ToString(CultureInfo.InvariantCulture) + ", " + pos.z.ToString(CultureInfo.InvariantCulture) + " " + text;
			return e;
		}

		private static void AddEntryLine(List<string> lines, string label, List<Entry> entries)
		{
			if (entries.Count == 0)
			{
				return;
			}
			entries.Sort(delegate (Entry a, Entry b)
			{
				if (a.dist != b.dist)
				{
					return a.dist < b.dist ? -1 : 1;
				}
				if (a.x != b.x)
				{
					return a.x < b.x ? -1 : 1;
				}
				if (a.z != b.z)
				{
					return a.z < b.z ? -1 : 1;
				}
				return string.CompareOrdinal(a.text, b.text);
			});
			int shown = Math.Min(entries.Count, MaxEntries);
			int more = entries.Count - shown;
			AddWrappedEntries(lines, label + ":", entries, shown, more);
		}

		// Wraps at entry boundaries (an entry is never split): "Label: e1; e2; ..." under MaxLine characters.
		private static void AddWrappedEntries(List<string> lines, string label, List<Entry> entries, int shown, int more)
		{
			List<string> pieces = new List<string>();
			for (int i = 0; i < shown; i++)
			{
				bool last = i == shown - 1 && more == 0;
				pieces.Add(entries[i].text + (last ? "." : ";"));
			}
			if (more > 0)
			{
				pieces.Add("and " + more.ToString(CultureInfo.InvariantCulture) + " more.");
			}
			AddWrapped(lines, label, pieces.ToArray(), "  ");
		}

		// ------------------------------------------------------------------------------------------------
		// Coarse map: one character per 10 by 10 block
		// ------------------------------------------------------------------------------------------------

		private static string PriorityFor(Layer layer)
		{
			switch (layer)
			{
				case Layer.Overview:
					return "h@PWDBxC^mpgu$jsozfdabi";
				case Layer.Resources:
					return "C^mpgu$jsobi";
				case Layer.Buildings:
					return "WDBxzfd";
				case Layer.Roof:
					return "XR";
				case Layer.Creatures:
					return "h@Pa";
				default:
					return "";
			}
		}

		private static string ClassesFor(Layer layer)
		{
			switch (layer)
			{
				case Layer.Overview:
					return "#T=~-,;+\".:_";
				case Layer.Terrain:
					return "#=~-,;+\".:_";
				case Layer.Resources:
					return "T#~.";
				case Layer.Buildings:
					return "#~+.";
				case Layer.Roof:
					return "Mtro.";
				default:
					return "#~.";
			}
		}

		// The per-cell class counted for "the most common" (see the brief, "One character per 10x10 block").
		private static char ClassChar(Layer layer, Facts f)
		{
			switch (layer)
			{
				case Layer.Overview:
					if (f.rock)
					{
						return '#';
					}
					return f.plant == 'T' ? 'T' : f.terr;
				case Layer.Terrain:
					return f.rock ? '#' : f.terr;
				case Layer.Resources:
					if (f.plant == 'T')
					{
						return 'T';
					}
					return Background(f);
				case Layer.Buildings:
					if (f.rock)
					{
						return '#';
					}
					if (f.water)
					{
						return '~';
					}
					return f.terr == '+' ? '+' : '.';
				case Layer.Roof:
					return f.roofBase;
				default:
					return Background(f);
			}
		}

		// Is this priority character present in the cell (any layer's priority string)?
		private static bool Has(char ch, Layer layer, Facts f)
		{
			switch (ch)
			{
				case 'h':
					return f.hostile;
				case '@':
					return f.colonist;
				case 'P':
					return f.prisoner;
				case 'a':
					return f.animal;
				case 'W':
				case 'D':
				case 'm':
				case 'p':
				case 'g':
				case 'u':
				case '$':
				case 'j':
				case 's':
				case 'o':
					return f.edi == ch;
				case 'B':
					return f.edi == 'B' || (layer == Layer.Buildings && f.edi == 'C');
				case 'C':
					return f.edi == 'C';
				case 'x':
					return f.blueprint;
				case '^':
					return f.geyser;
				case 'i':
					return f.item;
				case 'b':
					return f.plant == 'b';
				case 'z':
				case 'f':
				case 'd':
					return f.zone == ch;
				case 'X':
				case 'R':
					return f.roof == ch;
			}
			return false;
		}

		private static string Coarse(Map map, Layer layer, string layerName)
		{
			int W = map.Size.x;
			int H = map.Size.z;
			int nx = (W + Block - 1) / Block;
			int nz = (H + Block - 1) / Block;
			int need = NeedFor(layer);
			string priority = PriorityFor(layer);
			string classes = ClassesFor(layer);
			Env env = MakeEnv(map);
			Facts f = new Facts();
			bool[] seen = new bool[128];
			string[] rows = new string[nz];
			int[] counts = new int[classes.Length];
			for (int j = 0; j < nz; j++)
			{
				char[] row = new char[nx];
				for (int i = 0; i < nx; i++)
				{
					Array.Clear(counts, 0, counts.Length);
					long mask = 0;
					int visible = 0;
					int x1 = Math.Min(W, (i + 1) * Block);
					int z1 = Math.Min(H, (j + 1) * Block);
					for (int z = j * Block; z < z1; z++)
					{
						for (int x = i * Block; x < x1; x++)
						{
							Gather(env, new IntVec3(x, 0, z), need, f, null);
							if (f.fog)
							{
								continue;
							}
							visible++;
							for (int k = 0; k < priority.Length; k++)
							{
								if (Has(priority[k], layer, f))
								{
									mask |= 1L << k;
								}
							}
							int ci = classes.IndexOf(ClassChar(layer, f));
							if (ci >= 0)
							{
								counts[ci]++;
							}
						}
					}
					char ch;
					if (visible == 0)
					{
						ch = '?';
					}
					else if (mask != 0)
					{
						int k = 0;
						while ((mask & (1L << k)) == 0)
						{
							k++;
						}
						ch = priority[k];
					}
					else
					{
						int best = 0;
						for (int k = 1; k < counts.Length; k++)
						{
							if (counts[k] > counts[best])
							{
								best = k;
							}
						}
						ch = classes[best];
					}
					row[i] = ch;
					seen[ch] = true;
				}
				rows[j] = new string(row);
			}

			List<string> lines = new List<string>();
			AddWrapped(lines, "", new string[]
			{
				"Map overview (" + layerName + "), " + W.ToString(CultureInfo.InvariantCulture) + " by " + H.ToString(CultureInfo.InvariantCulture) + " cells,",
				"1 character = 10 by 10 cells;",
				"north is up, row label = z of the block's south edge."
			}, "");
			AddRuler(lines, nx, 0, Block);
			for (int j = nz - 1; j >= 0; j--)
			{
				lines.Add((j * Block).ToString(CultureInfo.InvariantCulture).PadLeft(3) + " " + rows[j]);
			}
			AddLegend(lines, seen, layer);
			AddWrapped(lines, "", new string[]
			{
				"Ask \"show map around SPOT.\"",
				"or \"show map of X, Z to X, Z.\" for full detail."
			}, "");
			return string.Join("\n", lines.ToArray());
		}

		// ------------------------------------------------------------------------------------------------
		// Layout helpers
		// ------------------------------------------------------------------------------------------------

		// Two lines over the columns, indented past the 4-character row labels: the x value at every 5th column
		// (column k is x = firstX + k * step) and a '|' under it.
		private static void AddRuler(List<string> lines, int cols, int firstX, int step)
		{
			char[] top = new char[cols + 8];
			char[] bar = new char[cols + 8];
			for (int i = 0; i < top.Length; i++)
			{
				top[i] = ' ';
				bar[i] = ' ';
			}
			for (int k = 0; k < cols; k += 5)
			{
				string label = (firstX + k * step).ToString(CultureInfo.InvariantCulture);
				for (int q = 0; q < label.Length; q++)
				{
					top[k + q] = label[q];
				}
				bar[k] = '|';
			}
			lines.Add("    " + new string(top).TrimEnd());
			lines.Add("    " + new string(bar).TrimEnd());
		}

		private static void AddLegend(List<string> lines, bool[] seen, Layer layer)
		{
			List<string> pieces = new List<string>();
			for (int i = 0; i < LegendOrder.Length; i++)
			{
				char ch = LegendOrder[i];
				if (!seen[ch])
				{
					continue;
				}
				pieces.Add(ch.ToString() + " " + Meaning(ch, layer));
			}
			for (int i = 0; i < pieces.Count; i++)
			{
				pieces[i] = pieces[i] + (i == pieces.Count - 1 ? "." : ",");
			}
			// A piece holds spaces inside it ("# rock wall,"): wrap at piece boundaries, never inside one.
			AddWrapped(lines, "Legend:", pieces.ToArray(), "");
		}

		// Greedy wrap: pieces are joined by one space; a piece is never split; each line is under 100 characters.
		private static void AddWrapped(List<string> lines, string first, string[] pieces, string indent)
		{
			List<string> all = new List<string>();
			for (int i = 0; i < pieces.Length; i++)
			{
				// a piece that cannot fit on a line by itself is split at its spaces
				if (indent.Length + pieces[i].Length > MaxLine)
				{
					all.AddRange(pieces[i].Split(' '));
				}
				else
				{
					all.Add(pieces[i]);
				}
			}
			StringBuilder line = new StringBuilder(first);
			bool any = first.Length > 0;
			for (int i = 0; i < all.Count; i++)
			{
				string p = all[i];
				if (p.Length == 0)
				{
					continue;
				}
				if (!any)
				{
					line.Append(p);
					any = true;
				}
				else if (line.Length + 1 + p.Length <= MaxLine)
				{
					line.Append(' ').Append(p);
				}
				else
				{
					lines.Add(line.ToString());
					line = new StringBuilder(indent);
					line.Append(p);
				}
			}
			if (line.Length > 0)
			{
				lines.Add(line.ToString());
			}
		}

		private static string Meaning(char ch, Layer layer)
		{
			switch (ch)
			{
				case '?': return "under fog";
				case '#': return "rock wall";
				case 's': return "steel ore (compacted steel)";
				case 'm': return "compacted machinery";
				case 'p': return "plasteel ore";
				case '$': return "silver ore";
				case 'g': return "gold ore";
				case 'u': return "uranium ore";
				case 'j': return "jade ore";
				case 'o': return layer == Layer.Roof ? "marked no-roof, open" : "other ore";
				case '~': return layer == Layer.Overview || layer == Layer.Terrain ? "deep water" : "water";
				case '-': return "shallow water";
				case '=': return "moving water (river)";
				case ',': return "marsh";
				case ';': return "mud";
				case '"': return "rich soil";
				case '.':
					if (layer == Layer.Roof)
					{
						return "open sky";
					}
					return layer == Layer.Overview || layer == Layer.Terrain ? "soil" : "open ground";
				case ':': return "poor soil";
				case '_': return "nothing grows";
				case '+': return "built floor";
				case 'T': return "tree";
				case 'b': return "harvestable plant";
				case 'C': return "ship chunk";
				case '^': return "steam geyser";
				case 'i': return "items";
				case 'W': return "wall";
				case 'D': return "door";
				case 'B': return "building";
				case 'x': return "blueprint or frame";
				case 'z': return "stockpile zone";
				case 'f': return "growing zone";
				case 'd': return "other zone";
				case '@': return "colonist";
				case 'P': return "prisoner or slave";
				case 'a': return "animal";
				case 'h': return "hostile";
				case 'M': return "overhead mountain (cannot be removed)";
				case 'X': return "roofed, marked no-roof (to be removed)";
				case 't': return "thin rock roof";
				case 'r': return "built roof";
				case 'R': return "marked build-roof, not roofed yet";
			}
			return "?";
		}
	}
}
