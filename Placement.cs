using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Brief 21 (briefs\21_blueprints_plans_patterns.md): where a new building goes.
	//
	// Three parts, all MAIN THREAD, none of them changes the game:
	//   LayoutRelation     how one building sits against another, written relative to the way the first
	//                      one faces, so the same habit is found on benches facing any direction. The same
	//                      function is used to LEARN (LayoutMemory) and to SCORE a free spot (PlacementSearch).
	//   BuildDefs          names -> buildable defs, stuff, what the colony may build, cost text.
	//   PlacementSearch    the spot search (user 2026-09-27T23:57:47Z: "anywhere in same room but weigh it
	//                      so that closest spot to workbench where it doesnt block door works").
	// Axes: +z is north, +x is east (Verse\IntVec3.cs:19,21; Verse\Rot4.cs:155-161). Directions are numbered
	// like Rot4: 0 north, 1 east, 2 south, 3 west (Verse\Rot4.cs:10-16).

	public struct LayoutRelation
	{
		// False when the two footprints overlap or don't touch (edge or corner).
		public bool valid;
		public bool corner;
		// Relative to the way the anchor faces. Side: 0 in front, 1 right, 2 behind, 3 left.
		// Corner: 0 front-right, 1 back-right, 2 back-left, 3 front-left.
		public int side;
		// How far the other building's middle is from the anchor's middle along the touching side, in half
		// tiles (so even and odd sizes need no fractions). 0 = centered. Always 0 for a corner.
		public int along2;
		// The other building's rotation relative to the anchor's: 0 same way, 1 turned right, 2 opposite,
		// 3 turned left. Always 0 when the other building can't be rotated.
		public int relRot;
		// The same side / corner on the map: side 0 north, 1 east, 2 south, 3 west; corner 0 north-east,
		// 1 south-east, 2 south-west, 3 north-west.
		public int worldSide;

		public string Key
		{
			get { return (corner ? "c" : "s") + side + "|" + along2 + "|" + relRot; }
		}

		public static bool TryParseKey(string key, out LayoutRelation rel)
		{
			rel = new LayoutRelation();
			if (string.IsNullOrEmpty(key) || key.Length < 6)
			{
				return false;
			}
			string[] parts = key.Split('|');
			int side;
			int along2;
			int relRot;
			if (parts.Length != 3 || parts[0].Length != 2 || (parts[0][0] != 'c' && parts[0][0] != 's')
				|| !int.TryParse(parts[0].Substring(1), out side) || !int.TryParse(parts[1], out along2)
				|| !int.TryParse(parts[2], out relRot) || side < 0 || side > 3 || relRot < 0 || relRot > 3)
			{
				return false;
			}
			rel.valid = true;
			rel.corner = parts[0][0] == 'c';
			rel.side = side;
			rel.along2 = along2;
			rel.relRot = relRot;
			return true;
		}

		private static readonly string[] WorldSideNames = { "north", "east", "south", "west" };
		private static readonly string[] WorldCornerNames = { "north-east", "south-east", "south-west", "north-west" };
		private static readonly string[] LocalSideNames = { "in front of", "to the right of", "behind", "to the left of" };
		private static readonly string[] LocalCornerNames =
		{
			"at the front-right corner of", "at the back-right corner of", "at the back-left corner of", "at the front-left corner of"
		};

		// "touching the east side of" / "at the north-east corner of" (for one real spot on the map).
		public string WorldPhrase()
		{
			return corner
				? "at the " + WorldCornerNames[worldSide] + " corner of"
				: "touching the " + WorldSideNames[worldSide] + " side of";
		}

		// "to the left of" (for a habit, which has no map direction).
		public string LocalPhrase()
		{
			return corner ? LocalCornerNames[side] : LocalSideNames[side];
		}

		// Footprints: GenAdj.OccupiedRect(center, rot, size) (Verse\GenAdj.cs:595-599), the same call
		// GenConstruct.CanPlaceBlueprintAt starts with (RimWorld\GenConstruct.cs:233).
		public static LayoutRelation Between(ThingDef anchorDef, IntVec3 anchorPos, Rot4 anchorRot, ThingDef otherDef, IntVec3 otherPos, Rot4 otherRot)
		{
			LayoutRelation rel = new LayoutRelation();
			CellRect ra = GenAdj.OccupiedRect(anchorPos, anchorRot, anchorDef.Size);
			CellRect rb = GenAdj.OccupiedRect(otherPos, otherRot, otherDef.Size);

			int ex = rb.minX > ra.maxX ? 1 : (rb.maxX < ra.minX ? -1 : 0);
			int ez = rb.minZ > ra.maxZ ? 1 : (rb.maxZ < ra.minZ ? -1 : 0);
			if (ex == 0 && ez == 0)
			{
				return rel;
			}
			int gapX = ex > 0 ? rb.minX - ra.maxX : (ex < 0 ? ra.minX - rb.maxX : 1);
			int gapZ = ez > 0 ? rb.minZ - ra.maxZ : (ez < 0 ? ra.minZ - rb.maxZ : 1);
			if (gapX != 1 || gapZ != 1)
			{
				return rel;
			}

			// A building that can't be rotated has no "front": its frame is the map's.
			int frame = anchorDef.rotatable ? anchorRot.AsInt : 0;

			if (ex != 0 && ez != 0)
			{
				int worldCorner = ex > 0 ? (ez > 0 ? 0 : 1) : (ez < 0 ? 2 : 3);
				rel.corner = true;
				rel.worldSide = worldCorner;
				rel.side = Mod4(worldCorner - frame);
				rel.along2 = 0;
			}
			else
			{
				int worldSide = ez > 0 ? 0 : (ex > 0 ? 1 : (ez < 0 ? 2 : 3));
				rel.worldSide = worldSide;
				rel.side = Mod4(worldSide - frame);
				// Middle-to-middle difference in half tiles, turned into the anchor's frame: one step
				// counter-clockwise is (x, z) -> (-z, x), the inverse of IntVec3Utility.RotatedBy(Rot4)
				// (Verse\IntVec3Utility.cs:35-45, clockwise (x, z) -> (z, -x)).
				int dx = (rb.minX + rb.maxX) - (ra.minX + ra.maxX);
				int dz = (rb.minZ + rb.maxZ) - (ra.minZ + ra.maxZ);
				for (int i = 0; i < frame; i++)
				{
					int oldX = dx;
					dx = -dz;
					dz = oldX;
				}
				rel.along2 = (rel.side == 0 || rel.side == 2) ? dx : dz;
			}
			rel.relRot = otherDef.rotatable ? Mod4(otherRot.AsInt - frame) : 0;
			rel.valid = true;
			return rel;
		}

		private static int Mod4(int v)
		{
			return ((v % 4) + 4) % 4;
		}
	}

	public static class BuildDefs
	{
		public const string KindWorkbench = "workbench";
		public const string KindBed = "bed";
		public const string KindStorage = "storage";

		// Habits are kept per kind of building, not per exact building (a shelf next to any workbench is
		// one habit). ThingDef.IsWorkTable / IsBed (Verse\ThingDef.cs:1020,878); storage = thingClass
		// Building_Storage or a subclass, the same test Router.BuildVocabulary uses. Anything else is its
		// own kind (its defName).
		public static string KindOf(ThingDef def)
		{
			if (def.IsWorkTable)
			{
				return KindWorkbench;
			}
			if (def.IsBed)
			{
				return KindBed;
			}
			if (IsStorage(def))
			{
				return KindStorage;
			}
			return def.defName;
		}

		public static string KindLabel(string kind)
		{
			if (kind == KindWorkbench || kind == KindBed)
			{
				return kind;
			}
			if (kind == KindStorage)
			{
				return "storage building";
			}
			ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(kind);
			return def != null && !def.label.NullOrEmpty() ? def.label : kind;
		}

		public static bool IsStorage(ThingDef def)
		{
			return def.thingClass != null && typeof(Building_Storage).IsAssignableFrom(def.thingClass);
		}

		// Walls, doors, fences, conduits and things hung on walls: the shell of a room, never furniture.
		// ThingDef.IsWall / IsDoor / IsFence / IsSmoothed (Verse\ThingDef.cs:880,912,914,1028);
		// BuildingProperties.isPowerConduit / isAttachment / isNaturalRock (RimWorld\BuildingProperties.cs:146,160,87).
		public static bool IsStructural(ThingDef def)
		{
			if (def == null)
			{
				return true;
			}
			if (def.IsWall || def.IsDoor || def.IsFence || def.IsSmoothed)
			{
				return true;
			}
			BuildingProperties b = def.building;
			return b != null && (b.isPowerConduit || b.isAttachment || b.isNaturalRock);
		}

		// A building the player's build menu could hold: BuildableDef.BuildableByPlayer
		// (Verse\BuildableDef.cs:144) and a blueprint def to place (BuildableDef.blueprintDef, :110).
		public static bool IsPlayerBuildable(ThingDef def)
		{
			return def != null && def.category == ThingCategory.Building && def.BuildableByPlayer && def.blueprintDef != null;
		}

		public static string Label(ThingDef def, ThingDef stuff)
		{
			// GenLabel.ThingLabel(BuildableDef, ThingDef stuff) (RimWorld\GenLabel.cs:114): "wooden shelf".
			try
			{
				return GenLabel.ThingLabel(def, stuff);
			}
			catch (Exception)
			{
				return def.label.NullOrEmpty() ? def.defName : def.label;
			}
		}

		// defName, then label, then the active language's plural of the label ("shelves"), all
		// case-insensitive. Several defs can share a label, so a list comes back.
		public static List<ThingDef> FindBuildables(string name)
		{
			List<ThingDef> byDefName = new List<ThingDef>();
			List<ThingDef> byLabel = new List<ThingDef>();
			List<ThingDef> byPlural = new List<ThingDef>();
			string wanted = (name ?? "").Trim();
			if (wanted.Length == 0)
			{
				return byDefName;
			}
			LanguageWorker worker = Find.ActiveLanguageWorker;
			foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if (!IsPlayerBuildable(def))
				{
					continue;
				}
				if (string.Equals(def.defName, wanted, StringComparison.OrdinalIgnoreCase))
				{
					byDefName.Add(def);
					continue;
				}
				if (def.label.NullOrEmpty())
				{
					continue;
				}
				if (string.Equals(def.label.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
				{
					byLabel.Add(def);
					continue;
				}
				try
				{
					// LanguageWorker.Pluralize(string, int) (Verse\LanguageWorker.cs:103-106).
					if (worker != null && string.Equals(worker.Pluralize(def.label), wanted, StringComparison.OrdinalIgnoreCase))
					{
						byPlural.Add(def);
					}
				}
				catch (Exception)
				{
				}
			}
			if (byDefName.Count > 0)
			{
				return byDefName;
			}
			return byLabel.Count > 0 ? byLabel : byPlural;
		}

		// What the build menu shows: Designator_Build.Visible (RimWorld\Designator_Build.cs:109-170: tech
		// level, research, Anomaly level, difficulty, place workers, prerequisite buildings). Dev god mode
		// shows everything there, and so here.
		public static bool IsAvailable(ThingDef def, out string reason)
		{
			reason = null;
			bool visible;
			try
			{
				visible = new Designator_Build(def).Visible;
			}
			catch (Exception)
			{
				visible = def.IsResearchFinished;
			}
			if (visible)
			{
				return true;
			}
			// BuildableDef.IsResearchFinished (Verse\BuildableDef.cs:172).
			reason = !def.IsResearchFinished ? "it hasn't been researched yet" : "the colony can't build it yet";
			return false;
		}

		// How an automatic material (none named) came out.
		public enum AutoStuff
		{
			NotAutomatic,   // the player named it, or the building takes no material
			Default,        // the game's default, enough in storage
			Replaced,       // steel (or plasteel for a wall) because the default is short or not allowed
			Short           // nothing allowed is in stock; the game's default is kept
		}

		// The user's rule, 2026-10-01: "use default. if it runs out of resource for that material than it
		// uses steel. plasteel only allowed for walls. precious metals (gold/silver/jade) only allowed with
		// player's prompt." Later the same day: "I forgot uranium is a thing. consider that as precious metal
		// as well." Correction the same day: "default, WOOD > STEEL > plasteel. mbad."
		// name empty (automatic), tried in this order (AutoOrder), each material once:
		//   1. GenStuff.DefaultStuffFor (RimWorld\GenStuff.cs:33), if storage still holds enough of it;
		//   2. else wood (ThingDefOf.WoodLog, RimWorld\ThingDefOf.cs:14), if GenStuff.AllowedStuffsFor
		//      (RimWorld\GenStuff.cs:121) lists it and storage holds enough;
		//   3. else steel (ThingDefOf.Steel, RimWorld\ThingDefOf.cs:12), same two conditions;
		//   4. else, for a wall only (ThingDef.IsWall, Verse\ThingDef.cs:880), plasteel
		//      (ThingDefOf.Plasteel, RimWorld\ThingDefOf.cs:24) under the same two conditions;
		//   5. else the game's default again: the blueprint is still placed and waits for material (how = Short).
		// Automatic never returns gold, silver (ThingDefOf.cs:10, :8), jade (ThingDefOf.cs:52) or uranium
		// (ThingDefOf.cs:50), and never plasteel for a non-wall. If the game's default is one of those, step 1
		// is skipped. If wood and steel can't make that building either there is nothing allowed left, so the
		// default is kept (how = Short).
		// "Enough" is ResourceCounter.GetCount (RimWorld\ResourceCounter.cs:75), the count in storage, minus
		// what earlier parts of the same order already claimed (claimed, updated here) so that 20 shelves or
		// 40 walls in one order are counted together. needed = def.CostStuffCount times the number of pieces.
		// name given: one of GenStuff.AllowedStuffsFor by defName, label or the word used in building names
		// ("wooden", ThingDef.LabelAsStuff, Verse\ThingDef.cs:1348). The player asked for it, so gold, silver,
		// jade, uranium and plasteel are allowed there, and no stock check is made.
		public static ThingDef ResolveStuff(ThingDef def, string name, Map map, int needed, Dictionary<ThingDef, int> claimed,
			out AutoStuff how, out string problem)
		{
			problem = null;
			how = AutoStuff.NotAutomatic;
			if (!def.MadeFromStuff)
			{
				return null;
			}
			string wanted = (name ?? "").Trim();
			if (wanted.Length == 0)
			{
				ThingDef dflt = GenStuff.DefaultStuffFor(def);
				ThingDef chosen = dflt;
				how = AutoStuff.Default;
				if (map != null)
				{
					chosen = dflt;
					how = AutoStuff.Short;
					foreach (ThingDef option in AutoOrder(def))
					{
						if (HasEnough(map, claimed, option, needed))
						{
							chosen = option;
							how = option == dflt ? AutoStuff.Default : AutoStuff.Replaced;
							break;
						}
					}
				}
				if (chosen != null && claimed != null)
				{
					int have;
					claimed.TryGetValue(chosen, out have);
					claimed[chosen] = have + needed;
				}
				return chosen;
			}
			foreach (ThingDef candidate in GenStuff.AllowedStuffsFor(def))
			{
				if (string.Equals(candidate.defName, wanted, StringComparison.OrdinalIgnoreCase)
					|| (candidate.label != null && string.Equals(candidate.label.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
					|| string.Equals(candidate.LabelAsStuff, wanted, StringComparison.OrdinalIgnoreCase))
				{
					if (claimed != null)
					{
						int have;
						claimed.TryGetValue(candidate, out have);
						claimed[candidate] = have + needed;
					}
					return candidate;
				}
			}
			problem = Label(def, null) + " can't be made of '" + wanted + "'";
			return null;
		}

		// ThingDefOf.Jade and ThingDefOf.Uranium exist in this game version (RimWorld\ThingDefOf.cs:52, :50);
		// the name lookup is the fallback for a version without them.
		private static bool IsJadeOrUranium(ThingDef stuff)
		{
			ThingDef jade = ThingDefOf.Jade ?? DefDatabase<ThingDef>.GetNamedSilentFail("Jade");
			ThingDef uranium = ThingDefOf.Uranium ?? DefDatabase<ThingDef>.GetNamedSilentFail("Uranium");
			return (jade != null && stuff == jade) || (uranium != null && stuff == uranium);
		}

		// May the automatic choice use this stuff for this building? Not gold, silver, jade or uranium; plasteel
		// only for a wall.
		public static bool AutoAllowed(ThingDef def, ThingDef stuff)
		{
			if (stuff == ThingDefOf.Gold || stuff == ThingDefOf.Silver || IsJadeOrUranium(stuff))
			{
				return false;
			}
			if (stuff == ThingDefOf.Plasteel && !def.IsWall)
			{
				return false;
			}
			return true;
		}

		// The materials the automatic choice tries, in order, each once: the game's default, wood, steel, then
		// plasteel for a wall. Each must be allowed for this building (AutoAllowed) and made by it
		// (GenStuff.AllowedStuffsFor), so a default that is gold or the like is left out.
		private static List<ThingDef> AutoOrder(ThingDef def)
		{
			List<ThingDef> order = new List<ThingDef>();
			ThingDef dflt = GenStuff.DefaultStuffFor(def);
			if (dflt != null && AutoAllowed(def, dflt))
			{
				order.Add(dflt);
			}
			ThingDef[] later = new ThingDef[] { ThingDefOf.WoodLog, ThingDefOf.Steel, def.IsWall ? ThingDefOf.Plasteel : null };
			foreach (ThingDef s in later)
			{
				if (s != null && !order.Contains(s) && AutoCandidate(def, s))
				{
					order.Add(s);
				}
			}
			return order;
		}

		private static bool AutoCandidate(ThingDef def, ThingDef stuff)
		{
			return stuff != null && AutoAllowed(def, stuff) && GenStuff.AllowedStuffsFor(def).Contains(stuff);
		}

		private static bool HasEnough(Map map, Dictionary<ThingDef, int> claimed, ThingDef stuff, int needed)
		{
			int taken = 0;
			if (claimed != null)
			{
				claimed.TryGetValue(stuff, out taken);
			}
			return map.resourceCounter.GetCount(stuff) - taken >= needed;
		}

		// The player-facing line for one building kind, or null when the default was used.
		public static string AutoStuffNote(ThingDef def, ThingDef chosen, AutoStuff how)
		{
			string kind = Label(def, null);
			ThingDef dflt = GenStuff.DefaultStuffFor(def);
			string dfltName = dflt != null ? StuffName(dflt) : "the usual material";
			if (how == AutoStuff.Replaced)
			{
				if (dflt != null && !AutoAllowed(def, dflt))
				{
					return dfltName + " is only used when you ask for it, so " + StuffName(chosen) + " is used for " + kind + ".";
				}
				return "Not enough " + dfltName + " in storage, so " + StuffName(chosen) + " is used for " + kind + ".";
			}
			if (how == AutoStuff.Short)
			{
				List<string> names = new List<string>();
				foreach (ThingDef option in AutoOrder(def))
				{
					names.Add(StuffName(option));
				}
				if (names.Count == 0)
				{
					return kind + " is only made of " + dfltName + ", which is only used when you ask for it; the blueprints are placed and wait for material.";
				}
				return "Not enough " + string.Join(" or ", names.ToArray()) + " in storage for " + kind +
					"; the blueprints are placed and wait for material.";
			}
			return null;
		}

		private static string StuffName(ThingDef stuff)
		{
			return stuff.label.NullOrEmpty() ? stuff.defName : stuff.label;
		}

		// "wood: 60 needed, 340 in storage". ResourceCounter.GetCount (RimWorld\ResourceCounter.cs:75) is the
		// number the game's own resource readout shows: only what lies in storage.
		public static void AddCosts(Dictionary<ThingDef, int> needed, ThingDef def, ThingDef stuff, int count)
		{
			if (stuff != null && def.CostStuffCount > 0)
			{
				AddCost(needed, stuff, def.CostStuffCount * count);
			}
			// BuildableDef.CostList (Verse\BuildableDef.cs:190).
			List<ThingDefCountClass> fixedCosts = def.CostList;
			if (fixedCosts != null)
			{
				foreach (ThingDefCountClass cost in fixedCosts)
				{
					if (cost != null && cost.thingDef != null)
					{
						AddCost(needed, cost.thingDef, cost.count * count);
					}
				}
			}
		}

		private static void AddCost(Dictionary<ThingDef, int> needed, ThingDef def, int amount)
		{
			int have;
			needed.TryGetValue(def, out have);
			needed[def] = have + amount;
		}

		public static string CostText(Map map, Dictionary<ThingDef, int> needed)
		{
			List<string> parts = new List<string>();
			foreach (KeyValuePair<ThingDef, int> kv in needed)
			{
				int have = map != null ? map.resourceCounter.GetCount(kv.Key) : 0;
				parts.Add((kv.Key.label.NullOrEmpty() ? kv.Key.defName : kv.Key.label) + ": " + kv.Value + " needed, " + have + " in storage");
			}
			return string.Join("; ", parts.ToArray());
		}

		// The room name for player text. "" = say nothing (no room, or a room the game only calls "room" /
		// "none": StorageIndex.IsGenericRoomRole). The outside is the one room touching the map edge
		// (Verse\Room.cs:363).
		public static string RoomText(Room room)
		{
			if (room == null)
			{
				return "";
			}
			if (room.TouchesMapEdge)
			{
				return "outdoors";
			}
			if (StorageIndex.IsGrowRoom(room))
			{
				return StorageIndex.GrowRoomLabel;
			}
			if (StorageIndex.IsGenericRoomRole(room.Role))
			{
				return "";
			}
			try
			{
				string label = room.GetRoomRoleLabel();
				return label.NullOrEmpty() ? "" : label;
			}
			catch (Exception)
			{
				return "";
			}
		}

		// The room a building stands in. A building that fills its cell has no room of its own
		// (RegionAndRoomQuery.GetRoom, Verse\RegionAndRoomQuery.cs:53), so the cells around it are tried.
		public static Room RoomOf(Thing thing)
		{
			if (thing == null || !thing.Spawned)
			{
				return null;
			}
			Room room = thing.GetRoom();
			if (room != null)
			{
				return room;
			}
			foreach (IntVec3 c in GenAdj.CellsAdjacentCardinal(thing))
			{
				if (c.InBounds(thing.Map))
				{
					room = c.GetRoom(thing.Map);
					if (room != null)
					{
						return room;
					}
				}
			}
			return null;
		}

		// True when nothing built, being built or planned as a blueprint stands on the cell. Power
		// conduits and floor blueprints don't count: furniture goes over them in the game too
		// (GenConstruct.CanPlaceBlueprintOver, RimWorld\GenConstruct.cs:500-591). Stricter than the game
		// on purpose: the mod never replaces or removes a building.
		public static bool FreeOfBuildings(IntVec3 c, Map map)
		{
			List<Thing> things = c.GetThingList(map);
			for (int i = 0; i < things.Count; i++)
			{
				ThingDef def = things[i].def;
				if (def.category != ThingCategory.Building && !def.IsBlueprint && !def.IsFrame)
				{
					continue;
				}
				if (def.entityDefToBuild is TerrainDef)
				{
					continue;
				}
				ThingDef built = (def.entityDefToBuild as ThingDef) ?? def;
				if (built.building != null && built.building.isPowerConduit)
				{
					continue;
				}
				return false;
			}
			return true;
		}

		private static readonly string[] DirectionNames = { "north", "east", "south", "west" };

		public static string DirectionName(int dir)
		{
			return DirectionNames[((dir % 4) + 4) % 4];
		}

		// "3 tiles north-east of": which way `to` lies from `from`, and how many tiles (the longer of the
		// two axis distances).
		public static string OffsetPhrase(IntVec3 from, IntVec3 to)
		{
			int dx = to.x - from.x;
			int dz = to.z - from.z;
			int ax = Math.Abs(dx);
			int az = Math.Abs(dz);
			int tiles = Math.Max(ax, az);
			if (tiles == 0)
			{
				return "at";
			}
			string ns = dz > 0 ? "north" : "south";
			string ew = dx > 0 ? "east" : "west";
			string dir;
			if (az >= 2 * ax)
			{
				dir = ns;
			}
			else if (ax >= 2 * az)
			{
				dir = ew;
			}
			else
			{
				dir = ns + "-" + ew;
			}
			return tiles + (tiles == 1 ? " tile " : " tiles ") + dir + " of";
		}
	}

	public sealed class PlacementQuery
	{
		public Map map;
		public ThingDef def;
		public ThingDef stuff;
		// The building the new one goes next to; null when only a room was named.
		public Thing anchor;
		public Room room;
		// "auto", "adjacent", or a direction ("north", "east", "south", "west").
		public string where = "auto";
		// null = try the default rotation first, then the other three.
		public Rot4? rotation;
		// Cells taken by earlier placements of the same order (not on the map yet).
		public HashSet<IntVec3> reserved = new HashSet<IntVec3>();
		// The part of `reserved` that will not be walkable.
		public HashSet<IntVec3> reservedBlocking = new HashSet<IntVec3>();
		// Learned habits for (anchor kind > new building kind): relation key -> times seen. May be null.
		public Dictionary<string, int> habits;
	}

	public sealed class PlacementSpot
	{
		public IntVec3 cell;
		public Rot4 rot;
		public LayoutRelation rel;
		// Times the player's own buildings sit this way (0 = no habit used).
		public int habitCount;
		public bool linksToAnchor;
	}

	public static class PlacementSearch
	{
		// Only used when the anchor is not inside a room (outdoors): how far from it a spot may be.
		// Inside a room there is no limit (user: "anywhere in same room"). Not a user value.
		public const int OutdoorSearchRadius = 8;

		private sealed class Candidate
		{
			public PlacementSpot spot;
			public int linkRank;
			public double distance;
			public double centerDistance;
			public int rotOrder;
		}

		// Returns null and a plain reason when nothing fits.
		public static PlacementSpot Find(PlacementQuery q, out string failReason)
		{
			failReason = null;
			Map map = q.map;
			bool properRoom = q.room != null && q.room.ProperRoom;

			HashSet<IntVec3> area = new HashSet<IntVec3>();
			if (properRoom)
			{
				foreach (IntVec3 c in q.room.Cells)
				{
					area.Add(c);
				}
			}
			else if (q.anchor != null)
			{
				CellRect around = q.anchor.OccupiedRect().ExpandedBy(OutdoorSearchRadius);
				around.ClipInsideMap(map);
				foreach (IntVec3 c in around)
				{
					Room cellRoom = c.GetRoom(map);
					if (cellRoom != null && !cellRoom.ProperRoom)
					{
						area.Add(c);
					}
				}
			}
			if (area.Count == 0)
			{
				failReason = "there is no free ground there";
				return null;
			}

			// "doesnt block door" (user): no part of the new building on a tile right next to a door.
			// Doors are found on the room's edge (Room.BorderCellsCardinal, Verse\Room.cs:327-347;
			// GridsUtility.GetDoor, Verse\GridsUtility.cs:424).
			HashSet<IntVec3> doorFronts = new HashSet<IntVec3>();
			List<IntVec3> doorCells = new List<IntVec3>();
			foreach (IntVec3 c in area)
			{
				for (int i = 0; i < 4; i++)
				{
					IntVec3 n = c + GenAdj.CardinalDirections[i];
					if (n.InBounds(map) && n.GetDoor(map) != null)
					{
						doorFronts.Add(c);
						if (!doorCells.Contains(n))
						{
							doorCells.Add(n);
						}
					}
				}
			}

			CellRect anchorRect = q.anchor != null ? q.anchor.OccupiedRect() : CellRect.Empty;
			CellRect extents = RectOf(area);

			// Facility link (end table, dresser and the like): the game's own check, so a spot where the
			// new piece would actually link to the anchor comes first
			// (CompAffectedByFacilities.CanPotentiallyLinkTo_Static, RimWorld\CompAffectedByFacilities.cs:118-190;
			// CompProperties_AffectedByFacilities.linkableFacilities, RimWorld\CompProperties_AffectedByFacilities.cs:8).
			bool facilityOfAnchor = false;
			if (q.anchor != null && q.def.GetCompProperties<CompProperties_Facility>() != null)
			{
				CompProperties_AffectedByFacilities affected = q.anchor.def.GetCompProperties<CompProperties_AffectedByFacilities>();
				facilityOfAnchor = affected != null && affected.linkableFacilities != null && affected.linkableFacilities.Contains(q.def);
			}

			List<Rot4> rotations = new List<Rot4>();
			if (q.rotation.HasValue)
			{
				rotations.Add(q.rotation.Value);
			}
			else if (!q.def.rotatable)
			{
				// BuildableDef.defaultPlacingRot (Verse\BuildableDef.cs:36), what the build button starts with.
				rotations.Add(q.def.defaultPlacingRot);
			}
			else
			{
				for (int i = 0; i < 4; i++)
				{
					rotations.Add(new Rot4((q.def.defaultPlacingRot.AsInt + i) % 4));
				}
			}

			int wantedDir = DirectionOf(q.where);
			bool wantAdjacent = string.Equals(q.where, "adjacent", StringComparison.OrdinalIgnoreCase);

			List<Candidate> candidates = new List<Candidate>();
			foreach (IntVec3 cell in area)
			{
				for (int r = 0; r < rotations.Count; r++)
				{
					Rot4 rot = rotations[r];
					CellRect rect = GenAdj.OccupiedRect(cell, rot, q.def.Size);
					if (!RectUsable(rect, area, q.reserved, doorFronts, map))
					{
						continue;
					}

					Candidate cand = new Candidate();
					cand.spot = new PlacementSpot();
					cand.spot.cell = cell;
					cand.spot.rot = rot;
					cand.rotOrder = r;

					if (q.anchor != null)
					{
						if (rect.Overlaps(anchorRect))
						{
							continue;
						}
						LayoutRelation rel = LayoutRelation.Between(q.anchor.def, q.anchor.Position, q.anchor.Rotation, q.def, cell, rot);
						cand.spot.rel = rel;
						if (wantAdjacent && (!rel.valid || rel.corner))
						{
							continue;
						}
						if (wantedDir >= 0 && !OnSide(rect, anchorRect, wantedDir))
						{
							continue;
						}
						if (rel.valid && q.habits != null)
						{
							int seen;
							if (q.habits.TryGetValue(rel.Key, out seen))
							{
								cand.spot.habitCount = seen;
							}
						}
						if (facilityOfAnchor)
						{
							bool links = false;
							try
							{
								links = CompAffectedByFacilities.CanPotentiallyLinkTo_Static(q.def, cell, rot, q.anchor.def, q.anchor.Position,
									q.anchor.Rotation, map);
							}
							catch (Exception)
							{
							}
							cand.spot.linksToAnchor = links;
							cand.linkRank = links ? 0 : 1;
						}
						cand.distance = GapDistance(rect, anchorRect);
						cand.centerDistance = CenterDistance(rect, anchorRect);
					}
					else
					{
						// Only a room was named. Not a user rule (see the brief): against a wall first, then
						// as far from the doors as the room allows, so walkways stay open. A direction word
						// means that side of the room.
						bool atWall = TouchesEdge(rect, area);
						double fromDoors = doorCells.Count > 0 ? NearestDoorDistance(rect, doorCells) : 0.0;
						if (wantedDir >= 0)
						{
							cand.distance = DistanceToRoomSide(rect, extents, wantedDir) * 1000.0 + (atWall ? 0.0 : 500.0) - fromDoors;
						}
						else
						{
							cand.distance = (atWall ? 0.0 : 1000.0) - fromDoors;
						}
						cand.centerDistance = 0.0;
					}
					candidates.Add(cand);
				}
			}

			if (candidates.Count == 0)
			{
				failReason = "there is no free spot for it there that keeps the doors clear";
				return null;
			}

			// Best first: a spot that links to the anchor, then the player's own habit (most often seen),
			// then the closest to the anchor (user: "closest spot to workbench"). The rest only makes the
			// order the same every time.
			candidates.Sort(delegate (Candidate a, Candidate b)
			{
				int c = a.linkRank.CompareTo(b.linkRank);
				if (c != 0)
				{
					return c;
				}
				c = b.spot.habitCount.CompareTo(a.spot.habitCount);
				if (c != 0)
				{
					return c;
				}
				c = a.distance.CompareTo(b.distance);
				if (c != 0)
				{
					return c;
				}
				c = a.centerDistance.CompareTo(b.centerDistance);
				if (c != 0)
				{
					return c;
				}
				c = a.rotOrder.CompareTo(b.rotOrder);
				if (c != 0)
				{
					return c;
				}
				c = a.spot.cell.z.CompareTo(b.spot.cell.z);
				return c != 0 ? c : a.spot.cell.x.CompareTo(b.spot.cell.x);
			});

			// The game's own rule for the build button (Designator_Build.CanDesignateCell,
			// RimWorld\Designator_Build.cs:446-453), never god mode, and then the walkway check. Only run
			// until the first spot passes.
			bool blocksWalking = q.def.passability == Traversability.Impassable;
			WalkwayCheck walkways = null;
			string gameReason = null;
			bool walkwayRefused = false;
			foreach (Candidate cand in candidates)
			{
				AcceptanceReport report = GenConstruct.CanPlaceBlueprintAt(q.def, cand.spot.cell, cand.spot.rot, map, false, null, null, q.stuff);
				if (!report.Accepted)
				{
					if (gameReason == null && !report.Reason.NullOrEmpty())
					{
						gameReason = report.Reason;
					}
					continue;
				}
				if (blocksWalking && properRoom)
				{
					if (walkways == null)
					{
						walkways = new WalkwayCheck(map, area, doorFronts, q.reservedBlocking);
					}
					CellRect rect = GenAdj.OccupiedRect(cand.spot.cell, cand.spot.rot, q.def.Size);
					if (!walkways.StaysOpen(rect, q.def, cand.spot.cell, cand.spot.rot))
					{
						walkwayRefused = true;
						continue;
					}
				}
				return cand.spot;
			}

			if (walkwayRefused)
			{
				failReason = "every free spot there would block the way to a door or a workbench";
			}
			else if (gameReason != null)
			{
				failReason = "the game doesn't allow it there (" + gameReason.TrimEnd('.') + ")";
			}
			else
			{
				failReason = "there is no free spot for it there";
			}
			return null;
		}

		private static int DirectionOf(string where)
		{
			string w = (where ?? "").Trim().ToLowerInvariant();
			switch (w)
			{
				case "north": return 0;
				case "east": return 1;
				case "south": return 2;
				case "west": return 3;
				default: return -1;
			}
		}

		private static bool RectUsable(CellRect rect, HashSet<IntVec3> area, HashSet<IntVec3> reserved, HashSet<IntVec3> doorFronts, Map map)
		{
			foreach (IntVec3 c in rect)
			{
				if (!area.Contains(c) || reserved.Contains(c) || doorFronts.Contains(c) || !BuildDefs.FreeOfBuildings(c, map))
				{
					return false;
				}
			}
			return true;
		}

		private static bool OnSide(CellRect rect, CellRect anchor, int dir)
		{
			switch (dir)
			{
				case 0: return rect.minZ > anchor.maxZ;
				case 1: return rect.minX > anchor.maxX;
				case 2: return rect.maxZ < anchor.minZ;
				default: return rect.maxX < anchor.minX;
			}
		}

		// Free tiles between the two footprints (0 = touching), as a straight-line length.
		private static double GapDistance(CellRect a, CellRect b)
		{
			int gx = Math.Max(0, Math.Max(a.minX - b.maxX, b.minX - a.maxX) - 1);
			int gz = Math.Max(0, Math.Max(a.minZ - b.maxZ, b.minZ - a.maxZ) - 1);
			return Math.Sqrt((double)(gx * gx + gz * gz));
		}

		private static double CenterDistance(CellRect a, CellRect b)
		{
			double dx = ((a.minX + a.maxX) - (b.minX + b.maxX)) / 2.0;
			double dz = ((a.minZ + a.maxZ) - (b.minZ + b.maxZ)) / 2.0;
			return Math.Sqrt(dx * dx + dz * dz);
		}

		private static CellRect RectOf(HashSet<IntVec3> cells)
		{
			int minX = int.MaxValue;
			int minZ = int.MaxValue;
			int maxX = int.MinValue;
			int maxZ = int.MinValue;
			foreach (IntVec3 c in cells)
			{
				minX = Math.Min(minX, c.x);
				minZ = Math.Min(minZ, c.z);
				maxX = Math.Max(maxX, c.x);
				maxZ = Math.Max(maxZ, c.z);
			}
			return CellRect.FromLimits(minX, minZ, maxX, maxZ);
		}

		// True when some tile of the footprint has a neighbour outside the room (a wall, usually).
		private static bool TouchesEdge(CellRect rect, HashSet<IntVec3> area)
		{
			foreach (IntVec3 c in rect)
			{
				for (int i = 0; i < 4; i++)
				{
					if (!area.Contains(c + GenAdj.CardinalDirections[i]))
					{
						return true;
					}
				}
			}
			return false;
		}

		private static double NearestDoorDistance(CellRect rect, List<IntVec3> doors)
		{
			double best = double.MaxValue;
			foreach (IntVec3 door in doors)
			{
				int dx = door.x < rect.minX ? rect.minX - door.x : (door.x > rect.maxX ? door.x - rect.maxX : 0);
				int dz = door.z < rect.minZ ? rect.minZ - door.z : (door.z > rect.maxZ ? door.z - rect.maxZ : 0);
				best = Math.Min(best, Math.Sqrt((double)(dx * dx + dz * dz)));
			}
			return best;
		}

		private static int DistanceToRoomSide(CellRect rect, CellRect extents, int dir)
		{
			switch (dir)
			{
				case 0: return extents.maxZ - rect.maxZ;
				case 1: return extents.maxX - rect.maxX;
				case 2: return rect.minZ - extents.minZ;
				default: return rect.minX - extents.minX;
			}
		}

		// Would a building that can't be walked through cut the room in two? The tiles that matter are
		// the ones in front of each door and the tiles colonists stand on to use a building
		// (ThingUtility.InteractionCellsWhenAt, Verse\ThingUtility.cs:60-82). Every pair of them that can
		// reach each other now must still reach each other afterwards. Steps are north/east/south/west
		// only, so a way that exists only diagonally counts as blocked (the careful side).
		private sealed class WalkwayCheck
		{
			private readonly Map map;
			private readonly HashSet<IntVec3> area;
			private readonly HashSet<IntVec3> alreadyBlocked;
			private readonly List<List<IntVec3>> groups = new List<List<IntVec3>>();

			public WalkwayCheck(Map map, HashSet<IntVec3> area, HashSet<IntVec3> doorFronts, HashSet<IntVec3> alreadyBlocked)
			{
				this.map = map;
				this.area = area;
				this.alreadyBlocked = alreadyBlocked ?? new HashSet<IntVec3>();

				HashSet<IntVec3> keys = new HashSet<IntVec3>();
				foreach (IntVec3 c in doorFronts)
				{
					if (Walkable(c, null))
					{
						keys.Add(c);
					}
				}
				HashSet<Thing> seen = new HashSet<Thing>();
				List<IntVec3> cells = new List<IntVec3>();
				foreach (IntVec3 c in area)
				{
					List<Thing> things = c.GetThingList(map);
					for (int i = 0; i < things.Count; i++)
					{
						Thing t = things[i];
						if (t.def.category != ThingCategory.Building || !t.def.HasSingleOrMultipleInteractionCells || !seen.Add(t))
						{
							continue;
						}
						ThingUtility.InteractionCellsWhenAt(cells, t.def, t.Position, t.Rotation, map);
						foreach (IntVec3 ic in cells)
						{
							if (area.Contains(ic) && Walkable(ic, null))
							{
								keys.Add(ic);
							}
						}
					}
				}

				// Group the key tiles by which of them reach each other today.
				HashSet<IntVec3> left = new HashSet<IntVec3>(keys);
				while (left.Count > 0)
				{
					IntVec3 start = left.First();
					HashSet<IntVec3> reached = Flood(start, null);
					List<IntVec3> group = new List<IntVec3>();
					foreach (IntVec3 k in keys)
					{
						if (reached.Contains(k) && left.Contains(k))
						{
							group.Add(k);
						}
					}
					foreach (IntVec3 k in group)
					{
						left.Remove(k);
					}
					if (group.Count == 0)
					{
						left.Remove(start);
						continue;
					}
					groups.Add(group);
				}
			}

			public bool StaysOpen(CellRect newRect, ThingDef def, IntVec3 cell, Rot4 rot)
			{
				HashSet<IntVec3> blocked = new HashSet<IntVec3>();
				foreach (IntVec3 c in newRect)
				{
					blocked.Add(c);
				}

				// The new building's own standing tiles must be reachable from a door too.
				List<IntVec3> own = new List<IntVec3>();
				if (def.HasSingleOrMultipleInteractionCells)
				{
					List<IntVec3> cells = new List<IntVec3>();
					ThingUtility.InteractionCellsWhenAt(cells, def, cell, rot, map);
					foreach (IntVec3 ic in cells)
					{
						if (area.Contains(ic))
						{
							own.Add(ic);
						}
					}
				}

				for (int g = 0; g < groups.Count; g++)
				{
					List<IntVec3> group = groups[g];
					IntVec3 start = IntVec3.Invalid;
					foreach (IntVec3 k in group)
					{
						if (blocked.Contains(k))
						{
							// A standing tile or door tile would be built over.
							return false;
						}
						if (!start.IsValid)
						{
							start = k;
						}
					}
					if (!start.IsValid)
					{
						continue;
					}
					HashSet<IntVec3> reached = Flood(start, blocked);
					foreach (IntVec3 k in group)
					{
						if (!reached.Contains(k))
						{
							return false;
						}
					}
					if (g == 0)
					{
						foreach (IntVec3 k in own)
						{
							if (!reached.Contains(k))
							{
								return false;
							}
						}
					}
				}
				return true;
			}

			// GenGrid.Walkable (Verse\GenGrid.cs:135-142).
			private bool Walkable(IntVec3 c, HashSet<IntVec3> blocked)
			{
				if (!area.Contains(c) || alreadyBlocked.Contains(c) || (blocked != null && blocked.Contains(c)))
				{
					return false;
				}
				return c.Walkable(map);
			}

			private HashSet<IntVec3> Flood(IntVec3 start, HashSet<IntVec3> blocked)
			{
				HashSet<IntVec3> reached = new HashSet<IntVec3>();
				if (!Walkable(start, blocked))
				{
					return reached;
				}
				Queue<IntVec3> open = new Queue<IntVec3>();
				open.Enqueue(start);
				reached.Add(start);
				while (open.Count > 0)
				{
					IntVec3 c = open.Dequeue();
					for (int i = 0; i < 4; i++)
					{
						IntVec3 n = c + GenAdj.CardinalDirections[i];
						if (!reached.Contains(n) && Walkable(n, blocked))
						{
							reached.Add(n);
							open.Enqueue(n);
						}
					}
				}
				return reached;
			}
		}
	}
}
