using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Brief 15 Step B: storage/bench data builder. MAIN THREAD ONLY - everything here reads live
	// game objects (zones, buildings, bills, rooms, thing grids).
	//
	// Public API:
	//   JObject Build(Map, IEnumerable<Room>, IEnumerable<Building_WorkTable>)
	//       Combined slice for matched rooms and/or benches; either argument may be null/empty.
	//       Returns an empty JObject when nothing is in scope (caller attaches nothing).
	//   JObject BuildForRooms(Map, IEnumerable<Room>)              = Build(map, rooms, null)
	//   JObject BuildForBenches(Map, IEnumerable<Building_WorkTable>) = Build(map, null, benches)
	//   List<Room> AllProperRooms(Map)                  rooms that have an entry in Snapshot rooms[]
	//   List<Room> FindRoomsByLabel(Map, string)        case-insensitive match on GetRoomRoleLabel()
	//   List<Building_WorkTable> AllPlayerBenches(Map)  spawned player-owned work tables
	//   List<Building_WorkTable> FindBenchesByLabel(Map, string)  match on thing label or def label
	//   string BenchLabel(Building_WorkTable)           label as the game shows it (LabelCap)
	//   List<StorageUnit> CollectUnits(Map)             every player storage unit on the map
	//   StorageUnit FindUnit(Map, string id)            resolve ONE emitted unit id to live objects +
	//                                                   StorageSettings (Step D decoder: call per id)
	//   string UnitIdFor(ISlotGroup)                    storage id for a bill's store target
	// Brief 15b additions: IsGenericRoomRole, BenchVocabLabel, UnitRoomText, SliceStorageIds,
	// CategoryName / SpecialFilterName / TrySplitUniqueName, BuildTabView, CanHold.
	//
	// Unit ids are the game's own load ids, distinguishable by prefix:
	//   "Zone_<n>"          stockpile zone             (Verse\Zone.cs:417-420)
	//   "StorageGroup_<n>"  linked storage buildings   (RimWorld\StorageGroup.cs:160-163)
	//   anything else       single storage building, Thing.ThingID = defName+number (Verse\Thing.cs:392-401)
	// FindUnit does not parse ids; it re-collects the live units and compares the ids they emit. A
	// storage group id (or the ThingID of one of its members) resolves to the group's shared settings.
	//
	// Grouping ("encoder"): storage[] has one ENTRY per set of units in the same room whose effective
	// settings are identical as the storage tab shows them (brief 15b fix 11: same priority, same listed
	// defs allowed, same listed special filters, same hit point / quality ranges where the tab has those
	// sliders, and equivalent parent filters so the compact filter text means the same for every member). Units in different rooms, or with no room, are never merged. A game storage
	// group (linked shelves) is already one unit and can merge further. Every id in "ids" is individually
	// resolvable with FindUnit; writes are applied per id.
	//
	// Emitted JSON (illustrative, hand-written):
	// {
	//   "rooms":   [ {"id": 7, "label": "kitchen"} ],
	//   "storage": [
	//     {"ids": ["Zone_12"], "count": 1, "names": {"Zone_12": "Stockpile zone 2"},
	//      "kinds": {"stockpile": 1}, "room_id": 7, "cells": 24, "priority": "Preferred",
	//      "filter": {"cats": ["foods"], "no_defs": ["pemmican"], "no_special": ["allow rotten"]},
	//      "contents": {"rice": 212, "raw potatoes": 90}},
	//     {"ids": ["StorageGroup_3", "Shelf88121", "Shelf88125"], "count": 3,
	//      "names": {"StorageGroup_3": "Storage group 1"}, "kinds": {"shelf": 5},
	//      "room_id": 7, "cells": 10, "priority": "Normal", "filter": {"all": true, "no_cats": ["corpses"]},
	//      "contents": {"steel": 450, "wood": 300}, "more_kinds": 4}
	//   ],
	//   "benches": [
	//     {"id": "ElectricStove41872", "label": "Electric stove", "room_id": 7,
	//      "bills": [{"recipe": "cook simple meal", "repeat": "until 10", "store": "SpecificStockpile",
	//                 "store_id": "Zone_12"},
	//                {"recipe": "cook fine meal", "repeat": "forever", "suspended": true, "store": "BestStockpile"}],
	//      "nearby": [{"id": "Zone_12", "dist": 2}, {"id": "Shelf88121", "dist": 5}]}
	//   ]
	// }
	// "count" = number of unit ids in the entry; "kinds" counts physical things (zones, each building of
	// a group); "names" only lists player-renamable units (zones, storage groups); "cells" and "contents"
	// (top 10 def labels by total stack count) are summed over the entry.
	// Filter semantics (compact, only defs the storage tab lists, so "everything this storage
	// can hold" collapses to {"all": true}): "all" = root category allowed; "cats"/"no_cats" = whole
	// category allowed/disallowed; "defs"/"no_defs" = single-thing exceptions. The most specific entry
	// wins (thing over category, deeper category over shallower). {"none": true} = nothing allowed.
	// "no_special" lists disallowed special filters the tab lists (only filters under an allowed category);
	// "hp" (percent) and "quality" appear only when not the full range and the tab has that slider.
	// Category and special-filter labels shared by several defs are written "label [defName]"
	// (CategoryName / SpecialFilterName, brief 15b fix 2).
	// Bench "nearby" (only for benches passed explicitly): one element per storage entry in the bench's
	// room, nearest first: "id" = the entry's unit nearest to the bench, "dist" = cells to it. A bill's
	// "store_id" always names the specific target unit, and that unit's entry is always in storage[].
	// "suspended" appears only when true.
	public static class StorageIndex
	{
		private const string LogPrefix = "[VerbalCommands] storage: ";

		// Top N contents per unit.
		private const int ContentsLimit = 10;

		// Bench scope in a non-proper room (outdoors / map-edge "room"): only this many nearest units,
		// otherwise every outdoor stockpile on the map would be "in the same room".
		private const int OutdoorNearbyLimit = 10;

		// ---------------------------------------------------------------------------------------
		// Lookup helpers (for Step C: matched label -> live Room / bench objects).
		// ---------------------------------------------------------------------------------------

		// Same set as Snapshot rooms[]: map.regionGrid.AllRooms (Verse\RegionGrid.cs:28) filtered to
		// ProperRoom (Verse\Room.cs:536-553).
		public static List<Room> AllProperRooms(Map map)
		{
			List<Room> result = new List<Room>();
			if (map == null)
			{
				return result;
			}
			foreach (Room room in map.regionGrid.AllRooms)
			{
				if (room != null && room.ProperRoom)
				{
					result.Add(room);
				}
			}
			return result;
		}

		// Brief 15b fix 1: the generic room types, by def, never by label text. RoomRoleDefOf.None
		// (RimWorld\RoomRoleDefOf.cs:8) is every room over 60 regions or not proper
		// (Verse\Room.cs:859-879; Core RoomRoles.xml label "none"); the generic "room" def has no DefOf
		// entry, so it is found by its worker class (RoomRoleDef.workerClass, Verse\RoomRoleDef.cs:8;
		// RoomRoleWorker_Room scores 0.99 for every room, RimWorld\RoomRoleWorker_Room.cs:5-11; Core
		// RoomRoles.xml defName Room, label "room"). Such rooms never have owners (Verse\Room.cs:406-412),
		// so no "Alice's room" label is lost.
		// User 2026-10-17: "any room with hydroponic bay would be "growroom". and they can say "growroom" which then can show
		// roomid for the growroom, and multiple if multiple present." A hydroponics basin is a plant grower whose sowTag is
		// "Hydroponic" (Core Buildings_Production.xml, HydroponicsBasin). Every room name the mod gives goes through RoomName.
		public const string GrowRoomLabel = "growroom";

		public static bool IsGrowRoom(Room room)
		{
			if (room == null || room.TouchesMapEdge)
			{
				return false;
			}
			foreach (Thing t in room.ContainedAndAdjacentThings)
			{
				if (t is Building_PlantGrower && t.def.building != null && t.def.building.sowTag == "Hydroponic" && t.GetRoom() == room)
				{
					return true;
				}
			}
			return false;
		}

		// The room's name: "growroom" for a room with a hydroponics basin, else the game's label (Room.GetRoomRoleLabel).
		public static string RoomName(Room room)
		{
			return IsGrowRoom(room) ? GrowRoomLabel : room.GetRoomRoleLabel();
		}

		public static bool IsGenericRoomRole(RoomRoleDef role)
		{
			return role == null || role == RoomRoleDefOf.None || role.workerClass == typeof(RoomRoleWorker_Room);
		}

		// Label as the game shows it (Verse\Room.cs:881-897), same value as Snapshot rooms[].label.
		// Rooms of a generic type are never matched (brief 15b fix 1).
		public static List<Room> FindRoomsByLabel(Map map, string label)
		{
			List<Room> result = new List<Room>();
			string wanted = label != null ? label.Trim() : null;
			if (wanted.NullOrEmpty())
			{
				return result;
			}
			foreach (Room room in AllProperRooms(map))
			{
				if (IsGenericRoomRole(room.Role) && !IsGrowRoom(room))
				{
					continue;
				}
				string roomLabel = SafeRoomLabel(room);
				if (roomLabel != null && string.Equals(roomLabel.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
				{
					result.Add(room);
				}
			}
			return result;
		}

		// listerBuildings.allBuildingsColonist only holds Faction.OfPlayer buildings
		// (Verse\ListerBuildings.cs:68,103-116); faction re-checked in case it changed since.
		public static List<Building_WorkTable> AllPlayerBenches(Map map)
		{
			List<Building_WorkTable> result = new List<Building_WorkTable>();
			if (map == null)
			{
				return result;
			}
			List<Building> buildings = map.listerBuildings.allBuildingsColonist;
			for (int i = 0; i < buildings.Count; i++)
			{
				if (buildings[i] is Building_WorkTable bench && IsPlayerBench(bench, map))
				{
					result.Add(bench);
				}
			}
			return result;
		}

		// Brief 15b fix 12: label with stuff but without hit points / quality ("wooden butcher table", not
		// "wooden butcher table (83%)"): GenLabel.ThingLabel(BuildableDef, ThingDef stuff)
		// (RimWorld\GenLabel.cs:114-141), where Thing.LabelNoCount adds LabelExtras (:144-221).
		public static string BenchVocabLabel(Building_WorkTable bench)
		{
			return GenLabel.ThingLabel(bench.def, bench.Stuff);
		}

		// Matches BenchVocabLabel or the def label ("electric stove"), case-insensitive.
		public static List<Building_WorkTable> FindBenchesByLabel(Map map, string label)
		{
			List<Building_WorkTable> result = new List<Building_WorkTable>();
			string wanted = label != null ? label.Trim() : null;
			if (wanted.NullOrEmpty())
			{
				return result;
			}
			foreach (Building_WorkTable bench in AllPlayerBenches(map))
			{
				try
				{
					if (string.Equals(BenchVocabLabel(bench), wanted, StringComparison.OrdinalIgnoreCase)
						|| string.Equals(bench.def.label, wanted, StringComparison.OrdinalIgnoreCase))
					{
						result.Add(bench);
					}
				}
				catch (Exception ex)
				{
					Log.WarningOnce(LogPrefix + "bench label failed: " + ex.Message, ("vc_bench_label_" + bench.ThingID).GetHashCode());
				}
			}
			return result;
		}

		// Building_WorkTable is not IRenameable, so the inspect pane shows Thing.LabelCap
		// (RimWorld\InspectPaneUtility.cs:126-128, Verse\Thing.cs:436).
		public static string BenchLabel(Building_WorkTable bench)
		{
			return bench.LabelCap;
		}

		// Brief 15c fix 3: where a storage unit is, for confirm lines. Empty ("") means say nothing
		// about the room at all (the caller then leaves out the "in ..." clause):
		//  - unit.room == null: the room lookup failed or was never resolved, i.e. unknown, not
		//    necessarily outdoors, so nothing is said.
		//  - unit.room.TouchesMapEdge (Verse\Room.cs:363-374): this is the single map-spanning room
		//    that represents the outside, i.e. actually outdoors (also what ProperRoom checks first,
		//    Verse\Room.cs:536-553) -> "outdoors".
		//  - IsGenericRoomRole(unit.room.Role) (RoomRoleDefOf.None or the generic "room" def, brief 15b
		//    fix 1): not a named room -> nothing is said (no "in \"none\"" / "in \"room\"").
		//  - otherwise: the room's own label (Room.GetRoomRoleLabel, Verse\Room.cs:881-897), quoted.
		public static string UnitRoomText(StorageUnit unit)
		{
			if (unit == null || unit.room == null)
			{
				return "";
			}
			if (unit.room.TouchesMapEdge)
			{
				return "outdoors";
			}
			if (IsGenericRoomRole(unit.room.Role))
			{
				return "";
			}
			string label = SafeRoomLabel(unit.room);
			return label.NullOrEmpty() ? "" : "\"" + label + "\"";
		}

		// Brief 15b fix 10: every storage id the slice offered (storage[].ids; bench nearby ids and bill
		// store_ids are always ids of emitted entries, see the header).
		public static HashSet<string> SliceStorageIds(JObject slice)
		{
			HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
			JArray storage = slice != null ? slice["storage"] as JArray : null;
			if (storage == null)
			{
				return ids;
			}
			foreach (JToken entry in storage)
			{
				JArray entryIds = entry["ids"] as JArray;
				if (entryIds == null)
				{
					continue;
				}
				foreach (JToken id in entryIds)
				{
					if (id.Type == JTokenType.String)
					{
						ids.Add((string)id);
					}
				}
			}
			return ids;
		}

		// ---------------------------------------------------------------------------------------
		// Unique names (brief 15b fix 2). Vanilla shares some labels between defs: "headgear" x3 and
		// "misc" x3 (Core ThingCategoryDefs\ThingCategories.xml), special filters "allow smeltable" /
		// "allow burnable" etc. for apparel and weapons. A shared label is written "label [defName]";
		// a unique label stays plain. Executor accepts both forms and the defName.
		// ---------------------------------------------------------------------------------------

		public static string CategoryName(ThingCategoryDef cat)
		{
			if (cat.label.NullOrEmpty())
			{
				return cat.defName;
			}
			string label = cat.label.Trim();
			int shared = 0;
			foreach (ThingCategoryDef other in DefDatabase<ThingCategoryDef>.AllDefsListForReading)
			{
				if (other.label != null && string.Equals(other.label.Trim(), label, StringComparison.OrdinalIgnoreCase))
				{
					shared++;
				}
			}
			return shared > 1 ? label + " [" + cat.defName + "]" : label;
		}

		public static string SpecialFilterName(SpecialThingFilterDef sf)
		{
			if (sf.label.NullOrEmpty())
			{
				return sf.defName;
			}
			string label = sf.label.Trim();
			int shared = 0;
			foreach (SpecialThingFilterDef other in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading)
			{
				if (other.label != null && string.Equals(other.label.Trim(), label, StringComparison.OrdinalIgnoreCase))
				{
					shared++;
				}
			}
			return shared > 1 ? label + " [" + sf.defName + "]" : label;
		}

		// "headgear [ArmorHeadgear]" -> label "headgear", defName "ArmorHeadgear". False for any other form.
		public static bool TrySplitUniqueName(string name, out string label, out string defName)
		{
			label = null;
			defName = null;
			if (name == null)
			{
				return false;
			}
			string t = name.Trim();
			int open = t.LastIndexOf('[');
			if (open <= 0 || !t.EndsWith("]", StringComparison.Ordinal))
			{
				return false;
			}
			defName = t.Substring(open + 1, t.Length - open - 2).Trim();
			label = t.Substring(0, open).Trim();
			return defName.Length > 0;
		}

		// ---------------------------------------------------------------------------------------
		// The storage tab's view of a filter (brief 15b fix 11 / nits 4+5): the compact filter, the
		// encoder's identical-settings test and set_storage only consider what the tab shows, so a
		// hand-set and a mod-set shelf that look the same in the tab also group together.
		// ---------------------------------------------------------------------------------------

		public sealed class TabView
		{
			// Defs the tab lists (Listing_TreeThingFilter.Visible(ThingDef), Verse\Listing_TreeThingFilter.cs:325-351).
			public HashSet<ThingDef> visibleDefs = new HashSet<ThingDef>();
			// Special filters the tab lists with a checkbox (see IsSpecialFilterShown).
			public List<SpecialThingFilterDef> visibleSpecials = new List<SpecialThingFilterDef>();
			// The tab draws the hit point / quality sliders only when the PARENT filter allows it
			// (Verse\ThingFilterUI.cs:48-56,64-71; fields Verse\ThingFilter.cs:34,38).
			public bool hitPointsConfigurable;
			public bool qualityConfigurable;
		}

		// parentFilter = the storage's parent filter (ITab_Storage passes GetParentStoreSettings().filter,
		// RimWorld\ITab_Storage.cs:132-141), or the generic fallback (RimWorld\StorageSettings.cs:136-146).
		public static TabView BuildTabView(ThingFilter parentFilter)
		{
			TabView view = new TabView();
			// allowedHitPointsConfigurable/allowedQualitiesConfigurable are only kept current by
			// RecalculateSpecialFilterConfigurability (Verse\ThingFilter.cs:595-620), which the
			// DisplayRootCategory getter triggers on demand (Verse\ThingFilter.cs:239-261 ->
			// RecalculateDisplayRootCategory :549-593). displayRootCategoryInt is [Unsaved(false)]
			// (ThingFilter.cs:14-15), so after a save/load it starts null and the two flags sit at
			// their field defaults, true (ThingFilter.cs:34,38), until something reads
			// DisplayRootCategory. The game reads it first for this same reason before checking the
			// flags (Verse\ThingFilterUI.cs:53-55); do the same here, main thread only.
			_ = parentFilter.DisplayRootCategory;
			view.hitPointsConfigurable = parentFilter.allowedHitPointsConfigurable;
			view.qualityConfigurable = parentFilter.allowedQualitiesConfigurable;
			// ThingFilter.AllowedThingDefs (Verse\ThingFilter.cs:176): Visible() requires parent.Allows anyway.
			foreach (ThingDef def in parentFilter.AllowedThingDefs)
			{
				if (IsDefShown(def, parentFilter))
				{
					view.visibleDefs.Add(def);
				}
			}
			foreach (SpecialThingFilterDef sf in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading)
			{
				if (IsSpecialFilterShown(sf, parentFilter, view.visibleDefs))
				{
					view.visibleSpecials.Add(sf);
				}
			}
			return view;
		}

		// Listing_TreeThingFilter.Visible(ThingDef) (Verse\Listing_TreeThingFilter.cs:325-351):
		// PlayerAcquirable (Verse\ThingDef.cs:506-523), not a virtual def (Verse\ThingDef.cs:37), allowed by
		// the parent and not always blocked by one of the parent's special filters
		// (ThingFilter.IsAlwaysDisallowedDueToSpecialFilters, Verse\ThingFilter.cs:622-632). The storage
		// tab passes no forceHiddenDefs (RimWorld\ITab_Storage.cs:141).
		private static bool IsDefShown(ThingDef def, ThingFilter parentFilter)
		{
			return def != null && def.PlayerAcquirable && def.virtualDefParent == null
				&& parentFilter.Allows(def) && !parentFilter.IsAlwaysDisallowedDueToSpecialFilters(def);
		}

		// A special filter has a checkbox in the storage tab when: configurable (DoSpecialFilter,
		// Verse\Listing_TreeThingFilter.cs:125-130); the parent allows it (Visible, :435-440); it is not in
		// the tab's forced hidden list (ITab_Storage.HiddenSpecialThingFilters, RimWorld\ITab_Storage.cs:178-187)
		// or the parent's hiddenSpecialFilters (:507-510); some def the parent allows can ever match it
		// (:487-516, SpecialThingFilterWorker.CanEverMatch Verse\SpecialThingFilterWorker.cs:12); and its
		// category is listed, i.e. has a shown def (Visible(TreeNode_ThingCategory), :417-424).
		private static bool IsSpecialFilterShown(SpecialThingFilterDef sf, ThingFilter parentFilter, HashSet<ThingDef> visibleDefs)
		{
			if (sf == null || !sf.configurable || sf.parentCategory == null || !parentFilter.Allows(sf) || HiddenByStorageTab(sf))
			{
				return false;
			}
			if (parentFilter.hiddenSpecialFilters != null && parentFilter.hiddenSpecialFilters.Contains(sf))
			{
				return false;
			}
			try
			{
				foreach (ThingDef def in sf.parentCategory.DescendantThingDefs)
				{
					if (visibleDefs.Contains(def) && sf.Worker.CanEverMatch(def))
					{
						return true;
					}
				}
			}
			catch (Exception ex)
			{
				Log.WarningOnce(LogPrefix + "special filter check failed for " + sf.defName + ": " + ex.Message, ("vc_sf_" + sf.defName).GetHashCode());
			}
			return false;
		}

		// RimWorld\ITab_Storage.cs:178-187 (private there): with Ideology, the meat-type filters are hidden.
		private static bool HiddenByStorageTab(SpecialThingFilterDef sf)
		{
			// ModsConfig.IdeologyActive (Verse\ModsConfig.cs:83); SpecialThingFilterDefOf fields are
			// [MayRequireIdeology] (RimWorld\SpecialThingFilterDefOf.cs:16-26).
			if (!ModsConfig.IdeologyActive)
			{
				return false;
			}
			return sf == SpecialThingFilterDefOf.AllowVegetarian || sf == SpecialThingFilterDefOf.AllowCarnivore
				|| sf == SpecialThingFilterDefOf.AllowCannibal || sf == SpecialThingFilterDefOf.AllowInsectMeat;
		}

		// Whether a storage with this parent filter can hold the def at all: the parent allows it and none
		// of the parent's special filters always blocks it (ThingFilter.Allows(ThingDef) Verse\ThingFilter.cs:908-911,
		// IsAlwaysDisallowedDueToSpecialFilters :622-632). Used for set_storage refusals (brief 15b fix 11).
		public static bool CanHold(ThingFilter parentFilter, ThingDef def)
		{
			return parentFilter.Allows(def) && !parentFilter.IsAlwaysDisallowedDueToSpecialFilters(def);
		}

		// ---------------------------------------------------------------------------------------
		// Storage units (for Step D: emitted id -> live objects + StorageSettings).
		// ---------------------------------------------------------------------------------------

		// Every player storage unit on the map: stockpile zones and storage buildings from
		// map.haulDestinationManager.AllGroupsListForReading (RimWorld\HaulDestinationManager.cs:28),
		// plus container storage that is a haul source instead of a slot group (bookcases, outfit
		// stands: RimWorld\Building_Bookcase.cs:8, RimWorld\Building_OutfitStand.cs:11) from
		// AllHaulSourcesListForReading (:30). Linked buildings collapse into one unit per StorageGroup.
		// Unknown slot-group parents that are neither a zone nor a Thing are skipped.
		public static List<StorageUnit> CollectUnits(Map map)
		{
			List<StorageUnit> units = new List<StorageUnit>();
			if (map == null || map.haulDestinationManager == null)
			{
				return units;
			}
			Dictionary<StorageGroup, StorageUnit> byGroup = new Dictionary<StorageGroup, StorageUnit>();
			HashSet<Thing> seen = new HashSet<Thing>();

			List<SlotGroup> slotGroups = map.haulDestinationManager.AllGroupsListForReading;
			for (int i = 0; i < slotGroups.Count; i++)
			{
				SlotGroup slotGroup = slotGroups[i];
				try
				{
					if (slotGroup == null || slotGroup.parent == null)
					{
						continue;
					}
					// SlotGroup.parent (RimWorld\SlotGroup.cs:8); CellsList = parent.AllSlotCellsList() (:60).
					if (slotGroup.parent is Zone_Stockpile zone)
					{
						StorageUnit unit = new StorageUnit();
						unit.id = zone.GetUniqueLoadID();
						// Zones are IRenameable; the inspect pane shows InspectLabel = RenamableLabel
						// (Verse\Zone.cs:40-54, RimWorld\InspectPaneUtility.cs:94-98).
						unit.label = zone.RenamableLabel;
						unit.kind = "stockpile";
						unit.zone = zone;
						unit.cells.AddRange(slotGroup.CellsList);
						units.Add(unit);
					}
					else if (slotGroup.parent is Thing thing)
					{
						AddStorageThing(thing, map, slotGroup.CellsList, units, byGroup, seen);
					}
				}
				catch (Exception ex)
				{
					Log.WarningOnce(LogPrefix + "skipped a slot group: " + ex.Message, ("vc_slotgroup_" + i).GetHashCode());
				}
			}

			List<IHaulSource> haulSources = map.haulDestinationManager.AllHaulSourcesListForReading;
			for (int i = 0; i < haulSources.Count; i++)
			{
				try
				{
					if (haulSources[i] is Thing thing && thing.Spawned)
					{
						// Cells the building occupies (Verse\GenAdj.cs:131).
						List<IntVec3> cells = GenAdj.CellsOccupiedBy(thing).ToList();
						AddStorageThing(thing, map, cells, units, byGroup, seen);
					}
				}
				catch (Exception ex)
				{
					Log.WarningOnce(LogPrefix + "skipped a haul source: " + ex.Message, ("vc_haulsource_" + i).GetHashCode());
				}
			}

			for (int i = 0; i < units.Count; i++)
			{
				try
				{
					units[i].room = RoomForCells(units[i].cells, map);
				}
				catch (Exception ex)
				{
					Log.WarningOnce(LogPrefix + "room lookup failed for " + units[i].id + ": " + ex.Message, ("vc_unitroom_" + units[i].id).GetHashCode());
				}
			}
			return units;
		}

		// Resolve ONE unit id exactly as emitted in storage[].ids (or a bill's store_id) back to the live
		// unit; null if gone. Also accepts the ThingID of a building inside a storage group and returns
		// the group's unit. Write through unit.Settings (group settings for linked buildings).
		public static StorageUnit FindUnit(Map map, string id)
		{
			if (id.NullOrEmpty())
			{
				return null;
			}
			string wanted = id.Trim();
			List<StorageUnit> units = CollectUnits(map);
			foreach (StorageUnit unit in units)
			{
				if (string.Equals(unit.id, wanted, StringComparison.Ordinal))
				{
					return unit;
				}
			}
			foreach (StorageUnit unit in units)
			{
				if (unit.group == null)
				{
					continue;
				}
				for (int i = 0; i < unit.buildings.Count; i++)
				{
					if (string.Equals(unit.buildings[i].ThingID, wanted, StringComparison.Ordinal))
					{
						return unit;
					}
				}
			}
			return null;
		}

		// Storage id of a bill's store target (Bill_Production.GetSlotGroup, RimWorld\Bill_Production.cs:182-185).
		// The target is either a StorageGroup (RimWorld\StorageGroup.cs:6 implements ISlotGroup) or a
		// zone's/building's own SlotGroup (RimWorld\Bill_Production.cs:163-175).
		public static string UnitIdFor(ISlotGroup slotGroup)
		{
			if (slotGroup is StorageGroup storageGroup)
			{
				return storageGroup.GetUniqueLoadID();
			}
			if (slotGroup is SlotGroup group && group.parent != null)
			{
				if (group.parent is Zone_Stockpile zone)
				{
					return zone.GetUniqueLoadID();
				}
				if (group.parent is Thing thing)
				{
					StorageGroup linked = thing is IStorageGroupMember member ? member.Group : null;
					return linked != null ? linked.GetUniqueLoadID() : thing.ThingID;
				}
			}
			return null;
		}

		// ---------------------------------------------------------------------------------------
		// Slice builders.
		// ---------------------------------------------------------------------------------------

		public static JObject BuildForRooms(Map map, IEnumerable<Room> rooms)
		{
			return Build(map, rooms, null);
		}

		public static JObject BuildForBenches(Map map, IEnumerable<Building_WorkTable> benches)
		{
			return Build(map, null, benches);
		}

		// Room scope: every storage unit and bench whose room is one of `rooms`.
		// Bench scope: each bench with its bills, plus "nearby" = storage units in the bench's room,
		// nearest first, with distance in cells from the bench position to the unit's nearest cell.
		// Both: a bill's SpecificStockpile target is always included in storage[], even from another room.
		public static JObject Build(Map map, IEnumerable<Room> rooms, IEnumerable<Building_WorkTable> benches)
		{
			JObject root = new JObject();
			if (map == null)
			{
				return root;
			}

			List<Room> roomList = new List<Room>();
			HashSet<Room> roomSet = new HashSet<Room>();
			if (rooms != null)
			{
				foreach (Room room in rooms)
				{
					if (room != null && roomSet.Add(room))
					{
						roomList.Add(room);
					}
				}
			}
			List<Building_WorkTable> scopedBenches = new List<Building_WorkTable>();
			HashSet<Building_WorkTable> benchSet = new HashSet<Building_WorkTable>();
			if (benches != null)
			{
				foreach (Building_WorkTable bench in benches)
				{
					if (bench != null && IsPlayerBench(bench, map) && benchSet.Add(bench))
					{
						scopedBenches.Add(bench);
					}
				}
			}
			if (roomList.Count == 0 && scopedBenches.Count == 0)
			{
				return root;
			}

			List<StorageUnit> units = CollectUnits(map);
			Dictionary<string, StorageUnit> unitsById = new Dictionary<string, StorageUnit>();
			foreach (StorageUnit unit in units)
			{
				unitsById[unit.id] = unit;
			}

			List<StorageUnit> emitUnits = new List<StorageUnit>();
			HashSet<string> emitIds = new HashSet<string>();

			// Room scope: units and benches in the rooms.
			JArray roomsJson = new JArray();
			List<Building_WorkTable> roomBenches = new List<Building_WorkTable>();
			if (roomList.Count > 0)
			{
				foreach (Room room in roomList)
				{
					JObject roomObj = new JObject();
					roomObj["id"] = room.ID;
					roomObj["label"] = SafeRoomLabel(room);
					roomsJson.Add(roomObj);
				}
				foreach (StorageUnit unit in units)
				{
					if (unit.room != null && roomSet.Contains(unit.room) && emitIds.Add(unit.id))
					{
						emitUnits.Add(unit);
					}
				}
				foreach (Building_WorkTable bench in AllPlayerBenches(map))
				{
					if (benchSet.Contains(bench))
					{
						continue;
					}
					Room benchRoom = SafeRoomOfThing(bench);
					if (benchRoom != null && roomSet.Contains(benchRoom))
					{
						roomBenches.Add(bench);
					}
				}
			}

			// Bench scope: explicit benches first ("nearby" is attached after grouping), then room benches.
			JArray benchesJson = new JArray();
			List<string> billTargetIds = new List<string>();
			List<KeyValuePair<JObject, List<KeyValuePair<StorageUnit, int>>>> nearbyByBench = new List<KeyValuePair<JObject, List<KeyValuePair<StorageUnit, int>>>>();
			foreach (Building_WorkTable bench in scopedBenches)
			{
				try
				{
					JObject benchObj = BenchJson(bench, billTargetIds);
					Room benchRoom = SafeRoomOfThing(bench);
					List<KeyValuePair<StorageUnit, int>> nearby = NearbyUnits(bench, benchRoom, units);
					foreach (KeyValuePair<StorageUnit, int> kv in nearby)
					{
						if (emitIds.Add(kv.Key.id))
						{
							emitUnits.Add(kv.Key);
						}
					}
					nearbyByBench.Add(new KeyValuePair<JObject, List<KeyValuePair<StorageUnit, int>>>(benchObj, nearby));
					benchesJson.Add(benchObj);
				}
				catch (Exception ex)
				{
					Log.WarningOnce(LogPrefix + "skipped bench " + bench.ThingID + ": " + ex.Message, ("vc_bench_" + bench.ThingID).GetHashCode());
				}
			}
			foreach (Building_WorkTable bench in roomBenches)
			{
				try
				{
					benchesJson.Add(BenchJson(bench, billTargetIds));
				}
				catch (Exception ex)
				{
					Log.WarningOnce(LogPrefix + "skipped bench " + bench.ThingID + ": " + ex.Message, ("vc_bench_" + bench.ThingID).GetHashCode());
				}
			}

			// Bill targets from any room.
			foreach (string targetId in billTargetIds)
			{
				if (unitsById.TryGetValue(targetId, out StorageUnit target) && emitIds.Add(targetId))
				{
					emitUnits.Add(target);
				}
			}

			// Encoder: merge units with identical settings in the same room into one entry.
			Dictionary<ThingFilter, TabView> viewCache = new Dictionary<ThingFilter, TabView>();
			List<StorageEntry> entries = GroupUnits(emitUnits, viewCache);
			Dictionary<StorageUnit, StorageEntry> entryOf = new Dictionary<StorageUnit, StorageEntry>();
			foreach (StorageEntry entry in entries)
			{
				foreach (StorageUnit unit in entry.units)
				{
					entryOf[unit] = entry;
				}
			}

			// Bench "nearby": one element per entry, its unit nearest to the bench. `nearby` is sorted
			// ascending, so the first unit seen of each entry is that entry's nearest.
			foreach (KeyValuePair<JObject, List<KeyValuePair<StorageUnit, int>>> pair in nearbyByBench)
			{
				JArray nearbyJson = new JArray();
				HashSet<StorageEntry> done = new HashSet<StorageEntry>();
				foreach (KeyValuePair<StorageUnit, int> kv in pair.Value)
				{
					if (entryOf.TryGetValue(kv.Key, out StorageEntry entry) && done.Add(entry))
					{
						JObject near = new JObject();
						near["id"] = kv.Key.id;
						near["dist"] = kv.Value;
						nearbyJson.Add(near);
					}
				}
				pair.Key["nearby"] = nearbyJson;
			}

			JArray storageJson = new JArray();
			foreach (StorageEntry entry in entries)
			{
				try
				{
					storageJson.Add(EntryJson(entry, viewCache));
				}
				catch (Exception ex)
				{
					string firstId = entry.units.Count > 0 ? entry.units[0].id : "?";
					Log.WarningOnce(LogPrefix + "skipped storage entry " + firstId + ": " + ex.Message, ("vc_entry_" + firstId).GetHashCode());
				}
			}

			if (roomsJson.Count > 0)
			{
				root["rooms"] = roomsJson;
			}
			root["storage"] = storageJson;
			root["benches"] = benchesJson;
			return root;
		}

		// ---------------------------------------------------------------------------------------
		// Internals.
		// ---------------------------------------------------------------------------------------

		private static bool IsPlayerBench(Building_WorkTable bench, Map map)
		{
			return bench.Spawned && bench.Map == map && bench.Faction == Faction.OfPlayer;
		}

		private static void AddStorageThing(Thing thing, Map map, List<IntVec3> cells, List<StorageUnit> units,
			Dictionary<StorageGroup, StorageUnit> byGroup, HashSet<Thing> seen)
		{
			if (thing == null || !thing.Spawned || thing.Map != map || thing.Faction != Faction.OfPlayer)
			{
				return;
			}
			if (!(thing is IStoreSettingsParent) || !seen.Add(thing))
			{
				return;
			}
			// Linked buildings share the group's StorageSettings (RimWorld\Building_Storage.cs:13,100-107;
			// IStorageGroupMember.Group, RimWorld\IStorageGroupMember.cs:7).
			StorageGroup group = thing is IStorageGroupMember member ? member.Group : null;
			if (group != null)
			{
				if (!byGroup.TryGetValue(group, out StorageUnit groupUnit))
				{
					groupUnit = new StorageUnit();
					groupUnit.id = group.GetUniqueLoadID();
					// Name the player sees in the storage tab / bill store menu (RimWorld\StorageGroup.cs:23-33;
					// SlotGroup.GetGroupLabel uses RenamableLabel for groups, RimWorld\SlotGroup.cs:143-154).
					groupUnit.label = group.RenamableLabel;
					groupUnit.kind = thing.def.label;
					groupUnit.group = group;
					byGroup[group] = groupUnit;
					units.Add(groupUnit);
				}
				groupUnit.buildings.Add(thing);
				groupUnit.cells.AddRange(cells);
				return;
			}
			StorageUnit unit = new StorageUnit();
			unit.id = thing.ThingID;
			// Storage buildings are not IRenameable: inspect pane label is Thing.LabelCap
			// (RimWorld\InspectPaneUtility.cs:126-128); Building_Storage.SlotYielderLabel() is also LabelCap
			// (RimWorld\Building_Storage.cs:127-130).
			unit.label = thing.LabelCap;
			unit.kind = thing.def.label;
			unit.buildings.Add(thing);
			unit.cells.AddRange(cells);
			units.Add(unit);
		}

		// Room of the unit's first cell (GridsUtility.GetRoom, Verse\GridsUtility.cs:57-60); cells under
		// impassable buildings have no region, so fall back to the next cell that has a room, then to a
		// room adjacent to the first cell (Verse\RegionAndRoomQuery.cs:73-89).
		private static Room RoomForCells(List<IntVec3> cells, Map map)
		{
			for (int i = 0; i < cells.Count; i++)
			{
				Room room = cells[i].GetRoom(map);
				if (room != null)
				{
					return room;
				}
			}
			if (cells.Count > 0)
			{
				return RegionAndRoomQuery.RoomAtOrAdjacent(cells[0], map, RegionType.Set_All);
			}
			return null;
		}

		// Thing.GetRoom (Verse\RegionAndRoomQuery.cs:53-56); impassable benches have no region under them,
		// so fall back to the interaction cell (Verse\ThingDef.cs:82, Verse\Thing.cs:504), then adjacency.
		private static Room SafeRoomOfThing(Thing thing)
		{
			try
			{
				Room room = thing.GetRoom();
				if (room != null)
				{
					return room;
				}
				if (thing.def.hasInteractionCell)
				{
					room = thing.InteractionCell.GetRoom(thing.Map);
					if (room != null)
					{
						return room;
					}
				}
				return RegionAndRoomQuery.RoomAtOrAdjacent(thing.Position, thing.Map, RegionType.Set_All);
			}
			catch (Exception ex)
			{
				Log.WarningOnce(LogPrefix + "room lookup failed for " + thing.ThingID + ": " + ex.Message, ("vc_thingroom_" + thing.ThingID).GetHashCode());
				return null;
			}
		}

		private static string SafeRoomLabel(Room room)
		{
			try
			{
				return RoomName(room);
			}
			catch (Exception)
			{
				return null;
			}
		}

		// Only proper rooms have an entry in Snapshot rooms[], so anything else is null (as Snapshot does).
		private static JToken RoomIdJson(Room room)
		{
			if (room != null && room.ProperRoom)
			{
				return room.ID;
			}
			return JValue.CreateNull();
		}

		private static List<KeyValuePair<StorageUnit, int>> NearbyUnits(Building_WorkTable bench, Room benchRoom, List<StorageUnit> units)
		{
			List<KeyValuePair<StorageUnit, int>> result = new List<KeyValuePair<StorageUnit, int>>();
			if (benchRoom == null)
			{
				return result;
			}
			IntVec3 pos = bench.Position;
			foreach (StorageUnit unit in units)
			{
				if (unit.room != benchRoom || unit.cells.Count == 0)
				{
					continue;
				}
				float best = float.MaxValue;
				for (int i = 0; i < unit.cells.Count; i++)
				{
					// Straight-line distance in cells (Verse\IntVec3.cs:50).
					float d = (unit.cells[i] - pos).LengthHorizontal;
					if (d < best)
					{
						best = d;
					}
				}
				result.Add(new KeyValuePair<StorageUnit, int>(unit, (int)Math.Round(best)));
			}
			result.Sort((KeyValuePair<StorageUnit, int> a, KeyValuePair<StorageUnit, int> b) => a.Value.CompareTo(b.Value));
			if (!benchRoom.ProperRoom && result.Count > OutdoorNearbyLimit)
			{
				result.RemoveRange(OutdoorNearbyLimit, result.Count - OutdoorNearbyLimit);
			}
			return result;
		}

		private static JObject BenchJson(Building_WorkTable bench, List<string> billTargetIds)
		{
			JObject benchObj = new JObject();
			benchObj["id"] = bench.ThingID;
			benchObj["label"] = BenchLabel(bench);
			benchObj["room_id"] = RoomIdJson(SafeRoomOfThing(bench));
			JArray billsJson = new JArray();
			// Building_WorkTable.billStack (RimWorld\Building_WorkTable.cs:6-8), BillStack.Bills (RimWorld\BillStack.cs:23).
			if (bench.billStack != null)
			{
				foreach (Bill bill in bench.billStack.Bills)
				{
					if (bill == null)
					{
						continue;
					}
					try
					{
						billsJson.Add(BillJson(bill, billTargetIds));
					}
					catch (Exception ex)
					{
						Log.WarningOnce(LogPrefix + "skipped a bill on " + bench.ThingID + ": " + ex.Message, ("vc_bill_" + bench.ThingID).GetHashCode());
					}
				}
			}
			benchObj["bills"] = billsJson;
			return benchObj;
		}

		private static JObject BillJson(Bill bill, List<string> billTargetIds)
		{
			JObject billObj = new JObject();
			// Recipe label, or the ideo-style "make X" label (RimWorld\Bill.cs:75-85).
			billObj["recipe"] = bill.Label;
			if (bill is Bill_Production production)
			{
				// Player-given bill name, if renamed (RimWorld\Bill_Production.cs:86-98).
				string name = production.RenamableLabel;
				if (!name.NullOrEmpty() && name != production.BaseLabel)
				{
					billObj["name"] = name;
				}
				// Repeat mode (RimWorld\Bill_Production.cs:12-22, RimWorld\BillRepeatModeDefOf.cs:6-10).
				if (production.repeatMode == BillRepeatModeDefOf.Forever)
				{
					billObj["repeat"] = "forever";
				}
				else if (production.repeatMode == BillRepeatModeDefOf.RepeatCount)
				{
					billObj["repeat"] = production.repeatCount + "x";
				}
				else if (production.repeatMode == BillRepeatModeDefOf.TargetCount)
				{
					billObj["repeat"] = "until " + production.targetCount;
				}
			}
			// Bill.suspended (RimWorld\Bill.cs:29).
			if (bill.suspended)
			{
				billObj["suspended"] = true;
			}
			// Only Bill_Production overrides the store mode; the base always says BestStockpile
			// (RimWorld\Bill.cs:442-450), so other bill types get no "store".
			if (bill is Bill_Production)
			{
				// GetStoreMode/GetSlotGroup (RimWorld\Bill_Production.cs:177-185), BillStoreModeDefOf (RimWorld\BillStoreModeDefOf.cs:6-10).
				BillStoreModeDef mode = bill.GetStoreMode();
				if (mode != null)
				{
					billObj["store"] = mode.defName;
					if (mode == BillStoreModeDefOf.SpecificStockpile)
					{
						string targetId = UnitIdFor(bill.GetSlotGroup());
						billObj["store_id"] = targetId;
						if (targetId != null)
						{
							billTargetIds.Add(targetId);
						}
					}
				}
			}
			return billObj;
		}

		// ---- Encoder: grouping identical settings within a room ----------------------------------

		private sealed class StorageEntry
		{
			public List<StorageUnit> units = new List<StorageUnit>();
			public Room room;
			// Settings of the first unit; every other unit's settings are equal (SettingsEqual).
			public StorageSettings settings;
			// Parent filter of the first unit (what the compact filter is expressed against) and the
			// representative of its equivalence class (parents whose storage tab shows the same thing).
			public ThingFilter parentFilter;
			public ThingFilter parentRep;
		}

		// First-appearance order is kept, so callers control entry order through `units`.
		private static List<StorageEntry> GroupUnits(List<StorageUnit> units, Dictionary<ThingFilter, TabView> viewCache)
		{
			List<StorageEntry> entries = new List<StorageEntry>();
			List<ThingFilter> parentReps = new List<ThingFilter>();
			foreach (StorageUnit unit in units)
			{
				StorageEntry match = null;
				StorageSettings settings = null;
				ThingFilter parentFilter = null;
				ThingFilter parentRep = null;
				try
				{
					settings = unit.Settings;
					StorageSettings parentSettings = unit.ParentSettings;
					parentFilter = parentSettings != null ? parentSettings.filter : null;
					if (parentFilter == null)
					{
						parentFilter = StorageSettings.EverStorableFixedSettings().filter;
					}
					// No room (unknown location) or no settings: never merged.
					if (settings != null && settings.filter != null && unit.room != null)
					{
						parentRep = ParentRepresentative(parentFilter, parentReps, viewCache);
						TabView view = ViewOf(parentRep, viewCache);
						foreach (StorageEntry entry in entries)
						{
							if (entry.room == unit.room && entry.parentRep == parentRep && entry.settings != null
								&& SettingsEqual(entry.settings, settings, view))
							{
								match = entry;
								break;
							}
						}
					}
				}
				catch (Exception ex)
				{
					match = null;
					parentRep = null;
					Log.WarningOnce(LogPrefix + "grouping failed for " + unit.id + ": " + ex.Message, ("vc_group_" + unit.id).GetHashCode());
				}
				if (match == null)
				{
					match = new StorageEntry();
					match.room = unit.room;
					match.settings = settings;
					match.parentFilter = parentFilter;
					match.parentRep = parentRep;
					entries.Add(match);
				}
				match.units.Add(unit);
			}
			return entries;
		}

		// Parent filters are equivalent when their storage tabs show the same thing: same listed defs, same
		// listed special filters, same hit point / quality sliders (e.g. two shelf defs with identical
		// fixedStorageSettings, RimWorld\Building_Storage.cs:109-117), so the compact filter text means the
		// same thing for every member of an entry.
		private static ThingFilter ParentRepresentative(ThingFilter parentFilter, List<ThingFilter> reps, Dictionary<ThingFilter, TabView> viewCache)
		{
			TabView view = ViewOf(parentFilter, viewCache);
			foreach (ThingFilter rep in reps)
			{
				if (rep == parentFilter || SameView(ViewOf(rep, viewCache), view))
				{
					return rep;
				}
			}
			reps.Add(parentFilter);
			return parentFilter;
		}

		private static TabView ViewOf(ThingFilter parentFilter, Dictionary<ThingFilter, TabView> viewCache)
		{
			if (!viewCache.TryGetValue(parentFilter, out TabView view))
			{
				view = BuildTabView(parentFilter);
				viewCache[parentFilter] = view;
			}
			return view;
		}

		private static bool SameView(TabView a, TabView b)
		{
			return a.hitPointsConfigurable == b.hitPointsConfigurable && a.qualityConfigurable == b.qualityConfigurable
				&& a.visibleSpecials.Count == b.visibleSpecials.Count && !a.visibleSpecials.Except(b.visibleSpecials).Any()
				&& a.visibleDefs.SetEquals(b.visibleDefs);
		}

		// The real settings, not the compact text, but only what the storage tab shows (brief 15b fix 11):
		// priority (RimWorld\StorageSettings.cs:23), allowance of every listed def (Verse\ThingFilter.cs:908-911;
		// the tab's category checkbox also allows defs the parent excludes, Verse\Listing_TreeThingFilter.cs:177,
		// which the tab never lists, :339-344), every listed special filter (:913-916), and the hit point /
		// quality ranges when the tab has those sliders (:182-237). The mental-break range is not in the
		// storage tab (ITab_Storage passes showMentalBreakChanceRange false, RimWorld\ITab_Storage.cs:141,
		// Verse\ThingFilterUI.cs:29,72-75) and is ignored.
		private static bool SettingsEqual(StorageSettings a, StorageSettings b, TabView view)
		{
			if (a == b)
			{
				return true;
			}
			if (a.Priority != b.Priority)
			{
				return false;
			}
			ThingFilter fa = a.filter;
			ThingFilter fb = b.filter;
			if (fa == null || fb == null)
			{
				return fa == fb;
			}
			if (view.hitPointsConfigurable && fa.AllowedHitPointsPercents != fb.AllowedHitPointsPercents)
			{
				return false;
			}
			if (view.qualityConfigurable && fa.AllowedQualityLevels != fb.AllowedQualityLevels)
			{
				return false;
			}
			for (int i = 0; i < view.visibleSpecials.Count; i++)
			{
				if (fa.Allows(view.visibleSpecials[i]) != fb.Allows(view.visibleSpecials[i]))
				{
					return false;
				}
			}
			foreach (ThingDef def in view.visibleDefs)
			{
				if (fa.Allows(def) != fb.Allows(def))
				{
					return false;
				}
			}
			return true;
		}

		private static JObject EntryJson(StorageEntry entry, Dictionary<ThingFilter, TabView> viewCache)
		{
			JObject entryObj = new JObject();
			JArray ids = new JArray();
			JObject names = new JObject();
			JObject kinds = new JObject();
			int cells = 0;
			Dictionary<string, int> counts = new Dictionary<string, int>();
			foreach (StorageUnit unit in entry.units)
			{
				ids.Add(unit.id);
				// Player-renamable names only (zones, storage groups); single buildings are covered by "kinds".
				if ((unit.zone != null || unit.group != null) && !unit.label.NullOrEmpty())
				{
					names[unit.id] = unit.label;
				}
				if (unit.zone != null)
				{
					AddKind(kinds, "stockpile");
				}
				foreach (Thing building in unit.buildings)
				{
					AddKind(kinds, building.def.label.NullOrEmpty() ? building.def.defName : building.def.label);
				}
				cells += unit.cells.Count;
				try
				{
					foreach (KeyValuePair<string, int> kv in unit.CountContents())
					{
						counts.TryGetValue(kv.Key, out int n);
						counts[kv.Key] = n + kv.Value;
					}
				}
				catch (Exception ex)
				{
					Log.WarningOnce(LogPrefix + "contents failed for " + unit.id + ": " + ex.Message, ("vc_contents_" + unit.id).GetHashCode());
				}
			}
			entryObj["ids"] = ids;
			entryObj["count"] = entry.units.Count;
			if (names.Count > 0)
			{
				entryObj["names"] = names;
			}
			entryObj["kinds"] = kinds;
			entryObj["room_id"] = RoomIdJson(entry.room);
			entryObj["cells"] = cells;
			StorageSettings settings = entry.settings;
			if (settings != null)
			{
				// StorageSettings.Priority (RimWorld\StorageSettings.cs:23), enum RimWorld\StoragePriority.cs:3-11.
				entryObj["priority"] = settings.Priority.ToString();
				if (settings.filter != null)
				{
					entryObj["filter"] = CompactFilter(settings.filter, entry.parentFilter, viewCache);
				}
			}

			List<KeyValuePair<string, int>> sorted = counts.ToList();
			sorted.Sort((KeyValuePair<string, int> a, KeyValuePair<string, int> b) =>
			{
				int c = b.Value.CompareTo(a.Value);
				return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
			});
			JObject contentsObj = new JObject();
			for (int i = 0; i < sorted.Count && i < ContentsLimit; i++)
			{
				contentsObj[sorted[i].Key] = sorted[i].Value;
			}
			entryObj["contents"] = contentsObj;
			if (sorted.Count > ContentsLimit)
			{
				entryObj["more_kinds"] = sorted.Count - ContentsLimit;
			}
			return entryObj;
		}

		private static void AddKind(JObject kinds, string kind)
		{
			JToken existing = kinds[kind];
			kinds[kind] = existing != null ? (int)existing + 1 : 1;
		}

		// ---- Compact filter -------------------------------------------------------------------

		private sealed class FilterNode
		{
			public int total;
			public int allowed;
			// Entries needed inside this category if it is in the allowed / disallowed state.
			public int costAllowed;
			public int costDisallowed;
		}

		private sealed class FilterWalk
		{
			public ThingFilter filter;
			public HashSet<ThingDef> relevant;
			public HashSet<ThingDef> reached = new HashSet<ThingDef>();
			public Dictionary<ThingCategoryDef, FilterNode> nodes = new Dictionary<ThingCategoryDef, FilterNode>();
			public List<string> cats = new List<string>();
			public List<string> noCats = new List<string>();
			public List<string> defs = new List<string>();
			public List<string> noDefs = new List<string>();
			public HashSet<string> defsSeen = new HashSet<string>();
		}

		// Expresses `filter` relative to what the parent filter allows (the defs the storage tab offers;
		// Building_Storage.GetParentStoreSettings RimWorld\Building_Storage.cs:109-117, Zone_Stockpile
		// RimWorld\Zone_Stockpile.cs:111-114, StorageGroup RimWorld\StorageGroup.cs:140-147), choosing per
		// category the fewest entries (category allow/disallow + single-thing exceptions). Categories come
		// from the ThingCategoryDef tree (Verse\ThingCategoryDef.cs:9-23, filled in
		// Verse\ThingCategoryNodeDatabase.cs:23-55), root = ThingFilter.RootNode (Verse\ThingFilter.cs:103-113).
		// Brief 15b fix 11: only what the storage tab shows (TabView): listed defs, listed special filters,
		// and the hit point / quality ranges only where the tab has those sliders.
		private static JObject CompactFilter(ThingFilter filter, ThingFilter parentFilter, Dictionary<ThingFilter, TabView> viewCache)
		{
			if (parentFilter == null)
			{
				// Same fallback the game uses for storage (RimWorld\StorageSettings.cs:136-146).
				parentFilter = StorageSettings.EverStorableFixedSettings().filter;
			}
			TabView view = ViewOf(parentFilter, viewCache);
			HashSet<ThingDef> relevant = view.visibleDefs;

			FilterWalk walk = new FilterWalk();
			walk.filter = filter;
			walk.relevant = relevant;

			TreeNode_ThingCategory rootNode = filter.RootNode;
			ThingCategoryDef rootCat = rootNode != null ? rootNode.catDef : ThingCategoryDefOf.Root;
			FilterNode root = Measure(rootCat, walk, 0);

			// Storable defs that are in no category can only be listed one by one.
			int unreachedAllowed = 0;
			foreach (ThingDef def in relevant)
			{
				if (!walk.reached.Contains(def) && filter.Allows(def))
				{
					unreachedAllowed++;
				}
			}

			JObject result = new JObject();
			if (root.allowed == 0 && unreachedAllowed == 0)
			{
				result["none"] = true;
				return result;
			}

			bool rootState = root.total > 0 && DecideState(root, false);
			if (rootState)
			{
				result["all"] = true;
			}
			if (root.total > 0 && !(rootState && root.allowed == root.total))
			{
				EmitChildren(rootCat, rootState, walk, 0);
			}
			foreach (ThingDef def in relevant)
			{
				if (walk.reached.Contains(def))
				{
					continue;
				}
				bool allowed = filter.Allows(def);
				if (allowed != rootState)
				{
					AddDef(walk, def, allowed);
				}
			}

			if (walk.cats.Count > 0)
			{
				result["cats"] = new JArray(walk.cats);
			}
			if (walk.noCats.Count > 0)
			{
				result["no_cats"] = new JArray(walk.noCats);
			}
			if (walk.defs.Count > 0)
			{
				result["defs"] = new JArray(walk.defs);
			}
			if (walk.noDefs.Count > 0)
			{
				result["no_defs"] = new JArray(walk.noDefs);
			}

			// Special filters: only a disallowed one restricts anything (ThingFilter.Allows(Thing) checks
			// disallowedSpecialFilters only, Verse\ThingFilter.cs:898-904; Allows(SpecialThingFilterDef) :913-916).
			// Listed only when the storage tab lists it (TabView.visibleSpecials) and its category has
			// something allowed. Shared labels get the unique form (SpecialFilterName).
			JArray noSpecial = new JArray();
			foreach (SpecialThingFilterDef sf in view.visibleSpecials)
			{
				if (filter.Allows(sf))
				{
					continue;
				}
				if (walk.nodes.TryGetValue(sf.parentCategory, out FilterNode node) && node.allowed > 0)
				{
					noSpecial.Add(SpecialFilterName(sf));
				}
			}
			if (noSpecial.Count > 0)
			{
				result["no_special"] = noSpecial;
			}

			// Hit points / quality ranges, only when narrowed and only where the storage tab has the
			// slider (Verse\ThingFilter.cs:182-237; defaults :30-36). The book mental-break range is not in
			// the storage tab and is not sent.
			FloatRange hp = filter.AllowedHitPointsPercents;
			if (view.hitPointsConfigurable && hp != FloatRange.ZeroToOne)
			{
				result["hp"] = new JArray((int)Math.Round(hp.min * 100f), (int)Math.Round(hp.max * 100f));
			}
			QualityRange quality = filter.AllowedQualityLevels;
			if (view.qualityConfigurable && quality != QualityRange.All)
			{
				// QualityCategory.GetLabel (RimWorld\QualityUtility.cs:39).
				result["quality"] = new JArray(quality.min.GetLabel(), quality.max.GetLabel());
			}
			return result;
		}

		// Post-order count of relevant defs under `cat` (defs listed in several categories are counted in
		// each; harmless for the all/none tests) and the minimal entry cost for either state.
		private static FilterNode Measure(ThingCategoryDef cat, FilterWalk walk, int depth)
		{
			FilterNode node = new FilterNode();
			walk.nodes[cat] = node;
			if (depth > 32)
			{
				return node;
			}
			List<ThingDef> childDefs = cat.childThingDefs;
			for (int i = 0; i < childDefs.Count; i++)
			{
				ThingDef def = childDefs[i];
				if (def == null || !walk.relevant.Contains(def))
				{
					continue;
				}
				walk.reached.Add(def);
				node.total++;
				// ThingFilter.Allows(ThingDef) (Verse\ThingFilter.cs:908-911).
				if (walk.filter.Allows(def))
				{
					node.allowed++;
					node.costDisallowed++;
				}
				else
				{
					node.costAllowed++;
				}
			}
			List<ThingCategoryDef> children = cat.childCategories;
			for (int i = 0; i < children.Count; i++)
			{
				if (children[i] == null)
				{
					continue;
				}
				FilterNode child = Measure(children[i], walk, depth + 1);
				if (child.total == 0)
				{
					continue;
				}
				node.total += child.total;
				node.allowed += child.allowed;
				node.costAllowed += Math.Min(child.costAllowed, 1 + child.costDisallowed);
				node.costDisallowed += Math.Min(child.costDisallowed, 1 + child.costAllowed);
			}
			return node;
		}

		// Uniform categories take their uniform state (so the highest fully allowed category is the one
		// named); mixed ones flip only if that is strictly cheaper than inheriting.
		private static bool DecideState(FilterNode node, bool inherited)
		{
			if (node.allowed == node.total)
			{
				return true;
			}
			if (node.allowed == 0)
			{
				return false;
			}
			int keep = inherited ? node.costAllowed : node.costDisallowed;
			int flip = 1 + (inherited ? node.costDisallowed : node.costAllowed);
			return flip < keep ? !inherited : inherited;
		}

		private static void Emit(ThingCategoryDef cat, bool inherited, FilterWalk walk, int depth)
		{
			if (!walk.nodes.TryGetValue(cat, out FilterNode node) || node.total == 0)
			{
				return;
			}
			bool state = DecideState(node, inherited);
			if (state != inherited)
			{
				(state ? walk.cats : walk.noCats).Add(CategoryName(cat));
			}
			if ((state && node.allowed == node.total) || (!state && node.allowed == 0))
			{
				return;
			}
			EmitChildren(cat, state, walk, depth);
		}

		private static void EmitChildren(ThingCategoryDef cat, bool state, FilterWalk walk, int depth)
		{
			if (depth > 32)
			{
				return;
			}
			List<ThingDef> childDefs = cat.childThingDefs;
			for (int i = 0; i < childDefs.Count; i++)
			{
				ThingDef def = childDefs[i];
				if (def == null || !walk.relevant.Contains(def))
				{
					continue;
				}
				bool allowed = walk.filter.Allows(def);
				if (allowed != state)
				{
					AddDef(walk, def, allowed);
				}
			}
			List<ThingCategoryDef> children = cat.childCategories;
			for (int i = 0; i < children.Count; i++)
			{
				if (children[i] != null)
				{
					Emit(children[i], state, walk, depth + 1);
				}
			}
		}

		private static void AddDef(FilterWalk walk, ThingDef def, bool allowed)
		{
			string label = def.label.NullOrEmpty() ? def.defName : def.label;
			if (walk.defsSeen.Add((allowed ? "+" : "-") + def.defName))
			{
				(allowed ? walk.defs : walk.noDefs).Add(label);
			}
		}
	}

	// One storage unit: a stockpile zone, a single storage building, or a linked storage group.
	// Built by StorageIndex.CollectUnits on the main thread; holds live references.
	public sealed class StorageUnit
	{
		public string id;
		public string label;
		public string kind;
		public Zone_Stockpile zone;
		public StorageGroup group;
		// Storage buildings in this unit (one, or every spawned member of the group).
		public List<Thing> buildings = new List<Thing>();
		public List<IntVec3> cells = new List<IntVec3>();
		public Room room;

		// The settings to read AND write: linked buildings return the group's shared settings
		// (RimWorld\Building_Storage.cs:100-107, RimWorld\StorageGroup.cs:135-138);
		// Zone_Stockpile.GetStoreSettings (RimWorld\Zone_Stockpile.cs:116-119).
		public StorageSettings Settings
		{
			get
			{
				if (group != null)
				{
					return group.GetStoreSettings();
				}
				if (zone != null)
				{
					return zone.GetStoreSettings();
				}
				if (buildings.Count > 0 && buildings[0] is IStoreSettingsParent parent)
				{
					return parent.GetStoreSettings();
				}
				return null;
			}
		}

		// The fixed filter bounding what this storage may hold (IStoreSettingsParent.GetParentStoreSettings,
		// RimWorld\IStoreSettingsParent.cs:9).
		public StorageSettings ParentSettings
		{
			get
			{
				if (group != null)
				{
					return group.GetParentStoreSettings();
				}
				if (zone != null)
				{
					return zone.GetParentStoreSettings();
				}
				if (buildings.Count > 0 && buildings[0] is IStoreSettingsParent parent)
				{
					return parent.GetParentStoreSettings();
				}
				return null;
			}
		}

		// Total stack count per def label of what is stored. Slot groups: SlotGroup.HeldThings
		// (RimWorld\SlotGroup.cs:18-36, storable things on the cells); container storage: its ThingOwner
		// (Verse\IThingHolder.cs:11). Minified things count as their inner thing (RimWorld\MinifyUtility.cs:42).
		public Dictionary<string, int> CountContents()
		{
			Dictionary<string, int> counts = new Dictionary<string, int>();
			if (zone != null && zone.slotGroup != null)
			{
				AddCounts(zone.slotGroup.HeldThings, counts);
			}
			for (int i = 0; i < buildings.Count; i++)
			{
				Thing building = buildings[i];
				if (building is ISlotGroupParent slotParent)
				{
					SlotGroup slotGroup = slotParent.GetSlotGroup();
					if (slotGroup != null)
					{
						AddCounts(slotGroup.HeldThings, counts);
					}
				}
				else if (building is IThingHolder holder)
				{
					ThingOwner owner = holder.GetDirectlyHeldThings();
					if (owner != null)
					{
						AddCounts(owner, counts);
					}
				}
			}
			return counts;
		}

		private static void AddCounts(IEnumerable<Thing> things, Dictionary<string, int> counts)
		{
			foreach (Thing thing in things)
			{
				if (thing == null)
				{
					continue;
				}
				Thing inner = thing.GetInnerIfMinified();
				ThingDef def = inner != null ? inner.def : thing.def;
				string label = def.label.NullOrEmpty() ? def.defName : def.label;
				counts.TryGetValue(label, out int n);
				counts[label] = n + thing.stackCount;
			}
		}
	}
}
