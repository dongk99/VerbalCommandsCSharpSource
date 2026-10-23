using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Brief 21: showing a saved room layout on the map as plans, so the player can pick where it goes.
	//
	// User, 2026-09-28T00:01:54Z: a room order needs a room that "must already exist, or shows them with
	// example layouts by using plan feature". User, 2026-09-28T00:07:59Z: "the mod asks player where the
	// room would go, by placing them example layouts."
	//
	// The game's plan (1.6): a labelled, coloured set of tiles that costs nothing and builds nothing
	// (Verse\Plan.cs; one plan per tile, PlanManager.PlanAt, Verse\PlanManager.cs:76). One option is a few
	// plans: walls, doors, and one per kind of furniture, each its own colour, labelled
	// "Option 2: walls". The player picks with a later order ("build option 2"); there is no question
	// step yet. Plans the mod drew are removed when an option is built or new options are drawn.
	//
	// Note: this namespace has its own class called Plan (Executor.cs), so the game's is always written
	// Verse.Plan here.
	// MAIN THREAD only.

	// One drawn option, kept in the save (GameComponent_VerbalCommands) so "build option 2" still works
	// after saving and loading.
	public sealed class PendingOption : IExposable
	{
		public int number;
		public int mapId = -1;
		public string layoutJson;
		public int originX;
		public int originZ;
		// Verse.Plan.GetUniqueLoadID() of every plan drawn for this option (Verse\Plan.cs:396-399).
		public List<string> planIds = new List<string>();

		public void ExposeData()
		{
			Scribe_Values.Look(ref number, "number", 0);
			Scribe_Values.Look(ref mapId, "mapId", -1);
			Scribe_Values.Look(ref layoutJson, "layoutJson");
			Scribe_Values.Look(ref originX, "originX", 0);
			Scribe_Values.Look(ref originZ, "originZ", 0);
			Scribe_Collections.Look(ref planIds, "planIds", LookMode.Value);
			if (Scribe.mode == LoadSaveMode.PostLoadInit && planIds == null)
			{
				planIds = new List<string>();
			}
		}

		public IntVec3 Origin
		{
			get { return new IntVec3(originX, 0, originZ); }
		}

		public SavedLayout Layout()
		{
			try
			{
				return layoutJson.NullOrEmpty() ? null : SavedLayout.FromJson(JObject.Parse(layoutJson));
			}
			catch (Exception)
			{
				return null;
			}
		}
	}

	public static class PlanOptions
	{
		public const int MaxOptions = 3;

		// How far from the middle of the colony's buildings a spot is looked for, and the free tiles kept
		// between two options. Not user values.
		private const int SearchRadius = 60;
		private const int GapBetweenOptions = 2;

		// Up to `wanted` spots (south-west corner of the layout) where every tile of the layout is free
		// ground, nearest to the colony first. A layout is put down as it was saved, never turned.
		public static List<IntVec3> FindSpots(Map map, SavedLayout layout, int wanted)
		{
			List<IntVec3> found = new List<IntVec3>();
			IntVec3 center = ColonyCenter(map);

			int minX = Math.Max(0, center.x - SearchRadius);
			int minZ = Math.Max(0, center.z - SearchRadius);
			int maxX = Math.Min(map.Size.x - 1, center.x + SearchRadius);
			int maxZ = Math.Min(map.Size.z - 1, center.z + SearchRadius);
			int spanX = maxX - minX + 1;
			int spanZ = maxZ - minZ + 1;
			if (spanX < layout.width || spanZ < layout.height)
			{
				return found;
			}

			// Free tiles, worked out once.
			bool[,] free = new bool[spanX, spanZ];
			for (int x = 0; x < spanX; x++)
			{
				for (int z = 0; z < spanZ; z++)
				{
					free[x, z] = TileFree(map, new IntVec3(minX + x, 0, minZ + z));
				}
			}

			List<IntVec3> origins = new List<IntVec3>();
			for (int x = 0; x + layout.width <= spanX; x++)
			{
				for (int z = 0; z + layout.height <= spanZ; z++)
				{
					origins.Add(new IntVec3(minX + x, 0, minZ + z));
				}
			}
			int halfW = layout.width / 2;
			int halfH = layout.height / 2;
			origins.Sort(delegate (IntVec3 a, IntVec3 b)
			{
				int da = Sq(a.x + halfW - center.x) + Sq(a.z + halfH - center.z);
				int db = Sq(b.x + halfW - center.x) + Sq(b.z + halfH - center.z);
				int c = da.CompareTo(db);
				if (c != 0)
				{
					return c;
				}
				c = a.z.CompareTo(b.z);
				return c != 0 ? c : a.x.CompareTo(b.x);
			});

			List<CellRect> taken = new List<CellRect>();
			foreach (IntVec3 origin in origins)
			{
				CellRect rect = new CellRect(origin.x, origin.z, layout.width, layout.height);
				bool clash = false;
				foreach (CellRect t in taken)
				{
					if (rect.Overlaps(t))
					{
						clash = true;
						break;
					}
				}
				if (clash)
				{
					continue;
				}
				bool ok = true;
				for (int x = 0; x < layout.width && ok; x++)
				{
					for (int z = 0; z < layout.height; z++)
					{
						if (!free[origin.x - minX + x, origin.z - minZ + z])
						{
							ok = false;
							break;
						}
					}
				}
				if (!ok)
				{
					continue;
				}
				found.Add(origin);
				taken.Add(rect.ExpandedBy(GapBetweenOptions));
				if (found.Count >= wanted)
				{
					break;
				}
			}
			return found;
		}

		private static int Sq(int v)
		{
			return v * v;
		}

		// Free ground for a new room: inside the buildable part of the map and seen
		// (GenGrid.InNoBuildEdgeArea, Verse\GenGrid.cs:14; GridsUtility.Fogged, Verse\GridsUtility.cs:91, the
		// same two checks GenConstruct.CanPlaceBlueprintAt starts with, RimWorld\GenConstruct.cs:238-252),
		// nothing built or planned on it, not inside an existing room, not in one of the player's zones
		// (ZoneManager.ZoneAt, Verse\ZoneManager.cs:78), no plan of the player's on it, and ground a wall
		// can stand on (GenConstruct.CanBuildOnTerrain, RimWorld\GenConstruct.cs:69).
		private static bool TileFree(Map map, IntVec3 c)
		{
			if (!c.InBounds(map) || c.InNoBuildEdgeArea(map) || c.Fogged(map))
			{
				return false;
			}
			if (c.GetEdifice(map) != null || !BuildDefs.FreeOfBuildings(c, map))
			{
				return false;
			}
			Room room = c.GetRoom(map);
			if (room == null || room.ProperRoom)
			{
				return false;
			}
			if (map.zoneManager.ZoneAt(c) != null || map.planManager.PlanAt(c) != null)
			{
				return false;
			}
			return GenConstruct.CanBuildOnTerrain(ThingDefOf.Wall, c, map, Rot4.North);
		}

		private static IntVec3 ColonyCenter(Map map)
		{
			List<Building> buildings = map.listerBuildings.allBuildingsColonist;
			if (buildings.Count == 0)
			{
				return map.Center;
			}
			long x = 0;
			long z = 0;
			for (int i = 0; i < buildings.Count; i++)
			{
				x += buildings[i].Position.x;
				z += buildings[i].Position.z;
			}
			return new IntVec3((int)(x / buildings.Count), 0, (int)(z / buildings.Count));
		}

		// "9 tiles east of kitchen": the middle of the option, from the middle of the nearest named room.
		public static string DescribeSpot(Map map, SavedLayout layout, IntVec3 origin)
		{
			IntVec3 middle = new IntVec3(origin.x + layout.width / 2, 0, origin.z + layout.height / 2);
			Room nearest = null;
			IntVec3 nearestMiddle = IntVec3.Invalid;
			int best = int.MaxValue;
			foreach (Room room in StorageIndex.AllProperRooms(map))
			{
				if (BuildDefs.RoomText(room).Length == 0)
				{
					continue;
				}
				IntVec3 roomMiddle = room.ExtentsClose.CenterCell;
				int d = Sq(roomMiddle.x - middle.x) + Sq(roomMiddle.z - middle.z);
				if (d < best)
				{
					best = d;
					nearest = room;
					nearestMiddle = roomMiddle;
				}
			}
			if (nearest == null)
			{
				return BuildDefs.OffsetPhrase(ColonyCenter(map), middle) + " the middle of the colony";
			}
			return BuildDefs.OffsetPhrase(nearestMiddle, middle) + " " + BuildDefs.RoomText(nearest);
		}

		// Draws one option and fills option.planIds. Colours: the game's planning colours
		// (Designator_Plan_Add.Colors, RimWorld\Designator_Plan_Add.cs:22-25). new Verse.Plan(color, manager)
		// registers itself (Verse\Plan.cs:143-151).
		public static void Draw(Map map, SavedLayout layout, PendingOption option)
		{
			List<ColorDef> colors = Designator_Plan_Add.Colors.ToList();
			if (colors.Count == 0)
			{
				throw new Fault("No plans were drawn: the game has no plan colours to draw with.");
			}

			// Group name -> tiles. Walls and doors first so they get the first two colours every time.
			List<string> groupOrder = new List<string>();
			Dictionary<string, List<IntVec3>> groups = new Dictionary<string, List<IntVec3>>();
			AddGroup(groupOrder, groups, "walls");
			AddGroup(groupOrder, groups, "doors");
			foreach (LayoutThing t in layout.things)
			{
				ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(t.def);
				if (def == null)
				{
					continue;
				}
				string group;
				if (def.IsDoor)
				{
					group = "doors";
				}
				else if (t.structural)
				{
					group = "walls";
				}
				else
				{
					group = def.label.NullOrEmpty() ? def.defName : def.label;
				}
				AddGroup(groupOrder, groups, group);
				IntVec3 pos = new IntVec3(option.originX + t.x, 0, option.originZ + t.z);
				foreach (IntVec3 c in GenAdj.OccupiedRect(pos, new Rot4(t.rot), def.Size))
				{
					groups[group].Add(c);
				}
			}

			option.planIds.Clear();
			int colorIndex = 0;
			foreach (string group in groupOrder)
			{
				List<IntVec3> cells = groups[group];
				if (cells.Count == 0)
				{
					continue;
				}
				Verse.Plan plan = new Verse.Plan(colors[colorIndex % colors.Count], map.planManager);
				colorIndex++;
				plan.label = "Option " + option.number + ": " + group;
				foreach (IntVec3 c in cells)
				{
					// One plan per tile; the first group to claim a tile keeps it.
					if (c.InBounds(map) && map.planManager.PlanAt(c) == null)
					{
						plan.AddCell(c);
					}
				}
				if (plan.CellCount == 0)
				{
					plan.Deregister();
					continue;
				}
				option.planIds.Add(plan.GetUniqueLoadID());
			}
		}

		private static void AddGroup(List<string> order, Dictionary<string, List<IntVec3>> groups, string name)
		{
			if (!groups.ContainsKey(name))
			{
				groups[name] = new List<IntVec3>();
				order.Add(name);
			}
		}

		// Removes every plan the mod drew for the given options (Verse.Plan.Delete(bool), Verse\Plan.cs:207-225;
		// no sound). Plans the player made or relabelled are never touched: only the recorded ids.
		public static void RemovePlans(IEnumerable<PendingOption> options)
		{
			if (options == null || Find.Maps == null)
			{
				return;
			}
			foreach (PendingOption option in options)
			{
				Map map = Find.Maps.FirstOrDefault((Map m) => m.uniqueID == option.mapId);
				if (map == null || option.planIds == null || option.planIds.Count == 0)
				{
					continue;
				}
				HashSet<string> ids = new HashSet<string>(option.planIds);
				foreach (Verse.Plan plan in map.planManager.AllPlans.ToList())
				{
					if (ids.Contains(plan.GetUniqueLoadID()))
					{
						plan.Delete(false);
					}
				}
				option.planIds.Clear();
			}
		}
	}
}
