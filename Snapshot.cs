using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Read-only capture of the colony state relevant to orders, taken on the main thread.
	// `json` is exactly what gets sent to the model; `pawnsById`/the name lists let the
	// Executor resolve the model's tool_use input against live objects later.
	public sealed class Snapshot
	{
		public Map map;
		public JObject json;
		public Dictionary<string, Pawn> pawnsById = new Dictionary<string, Pawn>();
		public List<string> timeAssignmentNames = new List<string>();
		public List<string> roomRoleNames = new List<string>();
		public List<string> workTypeNames = new List<string>();

		public static Snapshot Capture(Map map)
		{
			Snapshot snap = new Snapshot();
			snap.map = map;

			// Vocabulary lists: what the model is allowed to reference by name.
			List<TimeAssignmentDef> timeAssignments = DefDatabase<TimeAssignmentDef>.AllDefsListForReading;
			foreach (TimeAssignmentDef ta in timeAssignments)
			{
				snap.timeAssignmentNames.Add(ta.defName);
			}

			List<RoomRoleDef> roomRoles = DefDatabase<RoomRoleDef>.AllDefsListForReading;
			foreach (RoomRoleDef rr in roomRoles)
			{
				snap.roomRoleNames.Add(rr.defName);
			}

			List<WorkTypeDef> workTypes = DefDatabase<WorkTypeDef>.AllDefsListForReading;
			foreach (WorkTypeDef wt in workTypes)
			{
				snap.workTypeNames.Add(wt.defName);
			}

			int hour = GenLocalDate.HourOfDay(map);
			bool useWorkPriorities = Find.PlaySettings.useWorkPriorities;

			int allowedAreaCount = 0;
			JArray areasJson = new JArray();
			foreach (Area area in map.areaManager.AllAreas)
			{
				if (area is Area_Allowed allowedArea)
				{
					allowedAreaCount++;
					JObject areaObj = new JObject();
					areaObj["label"] = allowedArea.Label;
					areaObj["cells"] = allowedArea.TrueCount;
					areasJson.Add(areaObj);
				}
			}
			int areaSlotsRemaining = AreaManager.MaxAllowedAreas - allowedAreaCount;

			JArray roomsJson = new JArray();
			foreach (Room room in map.regionGrid.AllRooms)
			{
				if (!room.ProperRoom)
				{
					continue;
				}
				JObject roomObj = new JObject();
				roomObj["id"] = room.ID;
				roomObj["role"] = room.Role.defName;
				// Label as the game shows it: role label, ideo worship-room name, or
				// "SomeonesRoom"/"CouplesRoom" for owned rooms (Verse\Room.cs:881-897).
				roomObj["label"] = StorageIndex.RoomName(room);   // "growroom" for a room with a hydroponics basin (user, 2026-10-17)
				roomObj["cells"] = room.CellCount;
				JArray ownerIds = new JArray();
				foreach (Pawn owner in room.Owners)
				{
					ownerIds.Add(owner.ThingID);
				}
				roomObj["owner_ids"] = ownerIds;
				roomsJson.Add(roomObj);
			}

			HashSet<string> weaponsHeldSet = new HashSet<string>();

			JArray colonistsJson = new JArray();
			foreach (Pawn pawn in map.mapPawns.FreeColonists)
			{
				snap.pawnsById[pawn.ThingID] = pawn;

				JObject pawnObj = new JObject();
				pawnObj["id"] = pawn.ThingID;
				pawnObj["name"] = pawn.LabelShort;
				bool needsRest = pawn.needs.rest != null;
				pawnObj["needs_rest"] = needsRest;
				// Biological age in whole years (Verse\Pawn_AgeTracker.cs:99).
				pawnObj["age"] = pawn.ageTracker != null ? (int?)pawn.ageTracker.AgeBiologicalYears : null;

				// Held weapon (Verse\Pawn_EquipmentTracker.cs:16-27, Verse\ThingDef.cs:954,1115-1131).
				ThingWithComps primaryWeapon = pawn.equipment != null ? pawn.equipment.Primary : null;
				if (primaryWeapon != null)
				{
					JObject weaponObj = new JObject();
					weaponObj["def"] = primaryWeapon.def.defName;
					weaponObj["label"] = primaryWeapon.LabelCap.ToString();
					weaponObj["ranged"] = primaryWeapon.def.IsRangedWeapon;
					weaponObj["range"] = Math.Round((double)GroupRule.WeaponRange(primaryWeapon.def), 1);
					pawnObj["weapon"] = weaponObj;
					weaponsHeldSet.Add(primaryWeapon.def.defName);
				}
				else
				{
					pawnObj["weapon"] = JValue.CreateNull();
				}
				Room bedroom = pawn.ownership.Bedroom;
				pawnObj["bedroom_room_id"] = bedroom != null ? (int?)bedroom.ID : null;
				// Room the pawn stands in (Verse\RegionAndRoomQuery.cs:53-56); only proper rooms
				// (Verse\Room.cs:536) have an entry in rooms[], so anything else is null.
				Room currentRoom = pawn.GetRoom();
				pawnObj["current_room_id"] = currentRoom != null && currentRoom.ProperRoom ? (int?)currentRoom.ID : null;

				JArray timetableArr = new JArray();
				if (pawn.timetable != null)
				{
					for (int h = 0; h < 24; h++)
					{
						timetableArr.Add(pawn.timetable.GetAssignment(h).defName);
					}
				}
				pawnObj["timetable"] = timetableArr;

				JObject workObj = new JObject();
				if (pawn.workSettings != null && pawn.workSettings.EverWork)
				{
					foreach (WorkTypeDef wt in workTypes)
					{
						if (pawn.WorkTypeIsDisabled(wt))
						{
							continue;
						}
						workObj[wt.defName] = pawn.workSettings.GetPriority(wt);
					}
				}
				pawnObj["work"] = workObj;

				JObject skillsObj = new JObject();
				if (pawn.skills != null)
				{
					foreach (SkillRecord rec in pawn.skills.skills)
					{
						// GetLevelForUI() matches the number shown in the Character tab
						// (SkillRecord.cs:348-360), including Biotech/Anomaly aptitudes.
						skillsObj[rec.def.defName] = rec.GetLevelForUI();
					}
				}
				pawnObj["skills"] = skillsObj;

				string currentAreaLabel = null;
				if (pawn.playerSettings != null)
				{
					Area currentArea = pawn.playerSettings.AreaRestrictionInPawnCurrentMap;
					if (currentArea != null)
					{
						currentAreaLabel = currentArea.Label;
					}
				}
				pawnObj["current_area"] = currentAreaLabel;

				colonistsJson.Add(pawnObj);
			}

			List<string> weaponsHeldSorted = weaponsHeldSet.ToList();
			weaponsHeldSorted.Sort(StringComparer.Ordinal);

			JArray groupsJson = new JArray();
			GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
			if (gc != null)
			{
				foreach (KeyValuePair<int, GroupRule> kv in gc.AllGroups.OrderBy((KeyValuePair<int, GroupRule> p) => p.Key))
				{
					JObject groupObj = new JObject();
					groupObj["slot"] = kv.Key;
					groupObj["name"] = kv.Value.name;
					groupObj["rule"] = kv.Value.Summary();
					groupsJson.Add(groupObj);
				}
			}

			JObject root = new JObject();
			root["hour"] = hour;
			root["use_work_priorities"] = useWorkPriorities;
			root["area_slots_remaining"] = areaSlotsRemaining;
			root["time_assignments"] = new JArray(snap.timeAssignmentNames);
			root["room_roles"] = new JArray(snap.roomRoleNames);
			root["work_types"] = new JArray(snap.workTypeNames);
			root["colonists"] = colonistsJson;
			root["areas"] = areasJson;
			root["rooms"] = roomsJson;
			root["weapons_held"] = new JArray(weaponsHeldSorted);
			root["groups"] = groupsJson;

			snap.json = root;
			return snap;
		}
	}
}
