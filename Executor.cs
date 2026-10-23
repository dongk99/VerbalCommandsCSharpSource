using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	public sealed class Write
	{
		public string description;
		public Action apply;
	}

	public sealed class Plan
	{
		public string toolName;
		// Brief 29: the slots the router sent for this plan (JSON text), for the router log only.
		public string slotsJson;
		public List<Write> writes = new List<Write>();
		public List<string> notes = new List<string>();
		// Wind check (AgentWindCheck, 2026-10-14): true when a WIND note asks the player to confirm; the order then
		// waits for apply / cancel even when it said raw apply. Informs, never refuses. Also set by a LIGHT note
		// (AgentLightCheck), by the WORKSPOT reminder on every work spot / bench (AgentWorkspotCheck, 2026-10-14) and by a
		// DOOR note, a room built with a door next to an existing room (AgentDoorCheck, 2026-10-14).
		public bool askConfirm;
		// Which of those gates asked ("Door check", "Wind check", "Sun lamp check", "Work spot check"): the harness log
		// (HarnessLog.cs, H4) records it when the order said apply and was held. Null for the other askConfirm plans.
		public string confirmBy;
	}

	public sealed class Fault : Exception
	{
		public Fault(string msg) : base(msg)
		{
		}
	}

	// Resolves a validated tool_use into a Plan (or throws Fault), and applies a Plan's writes.
	// Both entry points run on the main thread. Resolution re-checks against LIVE state (not the
	// snapshot the model saw), because time passes between the request going out and the response
	// coming back.
	public static class Executor
	{
		// sliceStorageIds: the storage ids of this order's scoped storage slice (StorageIndex.SliceStorageIds);
		// set_storage accepts no other id (brief 15b fix 10). Null = no slice attached.
		public static Plan Resolve(JObject toolUse, Snapshot snap, HashSet<string> sliceStorageIds = null)
		{
			string toolName = (string)toolUse["name"];
			JObject input = (JObject)toolUse["input"];

			switch (toolName)
			{
				case "set_schedule":
					return ResolveSetSchedule(input, snap);
				case "create_allowed_area":
					return ResolveCreateAllowedArea(input, snap);
				case "set_work_priorities":
					return ResolveSetWorkPriorities(input, snap);
				case "define_group":
					return ResolveDefineGroup(input, snap);
				case "clear_group":
					return ResolveClearGroup(input, snap);
				case "set_storage":
					return ResolveSetStorage(input, snap, sliceStorageIds);
				// Brief 21: the build tools live in BuildTools.cs.
				case BuildTools.PlaceBlueprintTool:
					return BuildTools.ResolvePlaceBlueprint(input, snap);
				case BuildTools.DrawPlanTool:
					return BuildTools.ResolveDrawPlan(input, snap);
				case BuildTools.BuildOptionTool:
					return BuildTools.ResolveBuildOption(input, snap);
				default:
					// Brief 15c fix 7: plain words, no tool name.
					throw new Fault("That request wasn't understood, so nothing was changed.");
			}
		}

		public static void Apply(Plan plan)
		{
			foreach (Write write in plan.writes)
			{
				write.apply();
			}
		}

		// ---- targets ----------------------------------------------------------------------

		private static List<Pawn> ResolveTargets(JObject input, Snapshot snap)
		{
			JObject targets = (JObject)input["targets"];
			string mode = (string)targets["mode"];

			List<Pawn> result = new List<Pawn>();
			if (mode == "all")
			{
				result.AddRange(snap.map.mapPawns.FreeColonists);
			}
			else if (mode == "no_rest_need")
			{
				foreach (Pawn p in snap.map.mapPawns.FreeColonists)
				{
					if (p.needs.rest == null)
					{
						result.Add(p);
					}
				}
			}
			else if (mode == "ids")
			{
				JArray ids = (JArray)targets["ids"];
				foreach (JToken idTok in ids)
				{
					string id = (string)idTok;
					Pawn pawn;
					// Brief 15c fix 7: plain words, and name the colonist when we have one.
					if (!snap.pawnsById.TryGetValue(id, out pawn))
					{
						throw new Fault("That colonist no longer exists, so nothing was changed.");
					}
					if (!pawn.Spawned || pawn.Dead || pawn.Map != snap.map)
					{
						throw new Fault(pawn.LabelShort + " is no longer here, so nothing was changed.");
					}
					result.Add(pawn);
				}
			}
			else
			{
				throw new Fault("That order's colonist selection wasn't understood, so nothing was changed.");
			}

			if (result.Count == 0)
			{
				throw new Fault("No colonists matched that order, so nothing was changed.");
			}
			return result;
		}

		// ---- set_schedule -------------------------------------------------------------------

		private static Plan ResolveSetSchedule(JObject input, Snapshot snap)
		{
			List<Pawn> targets = ResolveTargets(input, snap);

			// Brief 15c fix 7: plain words.
			JArray slotsArr = (JArray)input["slots"];
			if (slotsArr.Count != 24)
			{
				throw new Fault("The schedule wasn't changed: it didn't cover all 24 hours.");
			}

			List<TimeAssignmentDef> slots = new List<TimeAssignmentDef>(24);
			foreach (JToken slotTok in slotsArr)
			{
				string slotName = (string)slotTok;
				TimeAssignmentDef def = ResolveTimeAssignment(slotName);
				if (def == null)
				{
					throw new Fault("The schedule wasn't changed: one of the hours named something that isn't a way to spend time.");
				}
				if (def == TimeAssignmentDefOf.Meditate && !ModsConfig.RoyaltyActive)
				{
					throw new Fault("The schedule wasn't changed: meditating needs the Royalty expansion, which isn't active.");
				}
				slots.Add(def);
			}

			Plan plan = new Plan();
			plan.toolName = "set_schedule";

			foreach (Pawn pawn in targets)
			{
				StringBuilder letters = new StringBuilder();
				for (int h = 0; h < 24; h++)
				{
					if (h > 0)
					{
						letters.Append(' ');
					}
					letters.Append(AbbreviateAssignment(slots[h]));
				}

				Write write = new Write();
				write.description = pawn.LabelShort + ": schedule -> " + letters + " (24 slots)";
				Pawn capturedPawn = pawn;
				List<TimeAssignmentDef> capturedSlots = slots;
				write.apply = delegate
				{
					for (int h = 0; h < 24; h++)
					{
						capturedPawn.timetable.SetAssignment(h, capturedSlots[h]);
					}
				};
				plan.writes.Add(write);
			}

			return plan;
		}

		private static string AbbreviateAssignment(TimeAssignmentDef def)
		{
			if (string.IsNullOrEmpty(def.defName))
			{
				return "?";
			}
			return char.ToUpperInvariant(def.defName[0]).ToString();
		}

		private static TimeAssignmentDef ResolveTimeAssignment(string name)
		{
			foreach (TimeAssignmentDef def in DefDatabase<TimeAssignmentDef>.AllDefsListForReading)
			{
				if (string.Equals(def.defName, name, StringComparison.OrdinalIgnoreCase))
				{
					return def;
				}
			}
			return null;
		}

		// ---- create_allowed_area -------------------------------------------------------------

		private static Plan ResolveCreateAllowedArea(JObject input, Snapshot snap)
		{
			List<Pawn> targets = ResolveTargets(input, snap);

			string name = (string)input["name"];
			bool includeOwnBedroom = (bool)input["include_own_bedroom"];

			JArray roomRolesArr = (JArray)input["room_roles"];
			List<RoomRoleDef> requestedRoles = new List<RoomRoleDef>();
			foreach (JToken roleTok in roomRolesArr)
			{
				string roleName = (string)roleTok;
				RoomRoleDef role = ResolveRoomRole(roleName);
				if (role == null)
				{
					// Brief 15c fix 7: plain words; roleName has no def to give it a label.
					throw new Fault("No area was made: one of the named room types doesn't exist.");
				}
				requestedRoles.Add(role);
			}

			// Rooms currently on the map for each requested role, live (not from the snapshot).
			List<Room> allRooms = new List<Room>();
			foreach (Room r in snap.map.regionGrid.AllRooms)
			{
				if (r.ProperRoom)
				{
					allRooms.Add(r);
				}
			}

			Dictionary<RoomRoleDef, List<Room>> matchedByRole = new Dictionary<RoomRoleDef, List<Room>>();
			List<string> unmatchedRoles = new List<string>();
			foreach (RoomRoleDef role in requestedRoles)
			{
				List<Room> matches = allRooms.Where((Room r) => r.Role == role).ToList();
				if (matches.Count == 0)
				{
					// RoomRoleDef.label (Verse\Def.cs), the game's own name for the room type.
					unmatchedRoles.Add(role.label);
				}
				matchedByRole[role] = matches;
			}
			if (unmatchedRoles.Count > 0)
			{
				// Brief 15c fix 7: plain words, labels not defNames.
				throw new Fault("No area was made: there is no " + string.Join(" or ", unmatchedRoles) + " on the map.");
			}

			int neededAreas = includeOwnBedroom ? targets.Count : 1;
			int existingAllowedAreas = snap.map.areaManager.AllAreas.Count((Area a) => a is Area_Allowed);
			int slotsRemaining = AreaManager.MaxAllowedAreas - existingAllowedAreas;
			if (neededAreas > slotsRemaining)
			{
				// Brief 15c fix 7: plain words, matches the brief's own example.
				string fitWord = slotsRemaining == 1 ? "fits" : "fit";
				throw new Fault("No areas were made: this needs " + neededAreas + ", but only " + slotsRemaining +
					" more " + fitWord + " (the game allows " + AreaManager.MaxAllowedAreas + ").");
			}

			Plan plan = new Plan();
			plan.toolName = "create_allowed_area";

			if (!includeOwnBedroom)
			{
				// One shared area for all targets, built from the matched rooms only.
				List<Room> roomsForArea = matchedByRole.Values.SelectMany((List<Room> l) => l).ToList();
				string cellsDesc = string.Join(", ", requestedRoles.Select((RoomRoleDef role) =>
					role.defName + " " + matchedByRole[role].Sum((Room r) => r.CellCount) + " cells"));
				string targetNames = string.Join(", ", targets.Select((Pawn p) => p.LabelShort));

				Write write = new Write();
				write.description = "create area '" + name + "' with " + roomsForArea.Count + " room(s) (" + cellsDesc +
					") and restrict " + targetNames + " to it";
				write.apply = delegate
				{
					Area_Allowed area;
					if (!snap.map.areaManager.TryMakeNewAllowed(out area))
					{
						// Brief 15c fix 7: plain words (this can race with another area made meanwhile).
						throw new Fault("Area '" + name + "' wasn't created: the colony already has as many areas as the game allows.");
					}
					area.SetLabel(name);
					foreach (Room r in roomsForArea)
					{
						foreach (IntVec3 c in r.Cells)
						{
							area[c] = true;
						}
					}
					foreach (Pawn p in targets)
					{
						p.playerSettings.AreaRestrictionInPawnCurrentMap = area;
					}
				};
				plan.writes.Add(write);
			}
			else
			{
				// One area per target pawn, each including the matched rooms plus that pawn's bedroom.
				foreach (Pawn pawn in targets)
				{
					List<Room> roomsForArea = matchedByRole.Values.SelectMany((List<Room> l) => l).ToList();
					Room bedroom = pawn.ownership != null ? pawn.ownership.Bedroom : null;

					List<string> cellsParts = requestedRoles
						.Select((RoomRoleDef role) => role.defName + " " + matchedByRole[role].Sum((Room r) => r.CellCount) + " cells")
						.ToList();
					if (bedroom != null)
					{
						cellsParts.Add("Bedroom " + bedroom.CellCount + " cells");
					}
					string cellsDesc = string.Join(", ", cellsParts);
					int roomCount = roomsForArea.Count + (bedroom != null ? 1 : 0);

					string areaName = name + " (" + pawn.LabelShort + ")";
					Write write = new Write();
					write.description = "create area '" + areaName + "' with " + roomCount + " room(s) (" + cellsDesc +
						") and restrict " + pawn.LabelShort + " to it";

					Pawn capturedPawn = pawn;
					Room capturedBedroom = bedroom;
					write.apply = delegate
					{
						Area_Allowed area;
						if (!snap.map.areaManager.TryMakeNewAllowed(out area))
						{
							// Brief 15c fix 7: plain words (this can race with another area made meanwhile).
							throw new Fault("Area '" + areaName + "' wasn't created: the colony already has as many areas as the game allows.");
						}
						area.SetLabel(areaName);
						foreach (Room r in roomsForArea)
						{
							foreach (IntVec3 c in r.Cells)
							{
								area[c] = true;
							}
						}
						if (capturedBedroom != null)
						{
							foreach (IntVec3 c in capturedBedroom.Cells)
							{
								area[c] = true;
							}
						}
						capturedPawn.playerSettings.AreaRestrictionInPawnCurrentMap = area;
					};
					plan.writes.Add(write);

					if (bedroom == null)
					{
						plan.notes.Add(pawn.LabelShort + " has no assigned bed; bedroom skipped");
					}
				}
			}

			return plan;
		}

		private static RoomRoleDef ResolveRoomRole(string name)
		{
			foreach (RoomRoleDef def in DefDatabase<RoomRoleDef>.AllDefsListForReading)
			{
				if (string.Equals(def.defName, name, StringComparison.OrdinalIgnoreCase))
				{
					return def;
				}
			}
			return null;
		}

		// ---- set_work_priorities -------------------------------------------------------------

		private static Plan ResolveSetWorkPriorities(JObject input, Snapshot snap)
		{
			List<Pawn> targets = ResolveTargets(input, snap);

			JArray prioritiesArr = (JArray)input["priorities"];
			List<Tuple<WorkTypeDef, int>> entries = new List<Tuple<WorkTypeDef, int>>();
			foreach (JToken entryTok in prioritiesArr)
			{
				JObject entryObj = (JObject)entryTok;
				string workTypeName = (string)entryObj["work_type"];
				int priority = (int)entryObj["priority"];

				WorkTypeDef workType = ResolveWorkType(workTypeName);
				if (workType == null)
				{
					// Brief 15c fix 7: plain words; workTypeName has no def to give it a label.
					throw new Fault("Work priorities weren't changed: one of the named work types doesn't exist.");
				}
				if (priority < 0 || priority > 4)
				{
					// Brief 15c fix 7: matches the brief's own example, no "0..4" notation.
					throw new Fault(UpperFirst(workType.labelShort) + " wasn't changed: work priority must be 1 to 4, or off.");
				}
				entries.Add(new Tuple<WorkTypeDef, int>(workType, priority));
			}

			bool manualPrioritiesOff = !Find.PlaySettings.useWorkPriorities;
			if (manualPrioritiesOff)
			{
				foreach (Tuple<WorkTypeDef, int> entry in entries)
				{
					if (entry.Item2 != 0 && entry.Item2 != 3)
					{
						// Brief 15c fix 7: plain words, no numeric priority notation.
						throw new Fault(UpperFirst(entry.Item1.labelShort) +
							" wasn't changed: with manual work priorities off in the Work tab, priorities can only be set to on or off.");
					}
				}
			}

			Plan plan = new Plan();
			plan.toolName = "set_work_priorities";

			foreach (Pawn pawn in targets)
			{
				foreach (Tuple<WorkTypeDef, int> entry in entries)
				{
					WorkTypeDef workType = entry.Item1;
					int priority = entry.Item2;

					if (pawn.WorkTypeIsDisabled(workType))
					{
						plan.notes.Add(pawn.LabelShort + " skipped: incapable of " + workType.defName);
						continue;
					}

					Write write = new Write();
					write.description = pawn.LabelShort + ": " + workType.defName + " -> " + priority;
					Pawn capturedPawn = pawn;
					WorkTypeDef capturedWorkType = workType;
					int capturedPriority = priority;
					write.apply = delegate
					{
						capturedPawn.workSettings.SetPriority(capturedWorkType, capturedPriority);
					};
					plan.writes.Add(write);
				}
			}

			return plan;
		}

		private static WorkTypeDef ResolveWorkType(string name)
		{
			foreach (WorkTypeDef def in DefDatabase<WorkTypeDef>.AllDefsListForReading)
			{
				if (string.Equals(def.defName, name, StringComparison.OrdinalIgnoreCase))
				{
					return def;
				}
			}
			return null;
		}

		// ---- define_group / clear_group -------------------------------------------------------

		private static Plan ResolveDefineGroup(JObject input, Snapshot snap)
		{
			// Brief 15c fix 7: plain words for every Fault below.
			int slot = (int)input["slot"];
			if (slot < 1 || slot > 9)
			{
				throw new Fault("The group wasn't saved: hotkey slots are numbered 1 to 9.");
			}

			string name = (string)input["name"];
			if (name != null)
			{
				name = name.Trim();
			}
			if (string.IsNullOrEmpty(name) || name.Length > 30)
			{
				throw new Fault("The group wasn't saved: give it a name, up to 30 characters long.");
			}

			JObject ruleInput = (JObject)input["rule"];
			string weaponClass = ((string)ruleInput["weapon_class"]).ToLowerInvariant();
			if (weaponClass != "ranged" && weaponClass != "melee" && weaponClass != "any")
			{
				throw new Fault("The group wasn't saved: its weapon type must be ranged, melee or any.");
			}

			int minRange = (int)ruleInput["min_range"];
			int maxRange = (int)ruleInput["max_range"];
			if (minRange < 0 || maxRange < 0)
			{
				throw new Fault("The group wasn't saved: its range can't be negative.");
			}
			if (maxRange != 0 && maxRange < minRange)
			{
				throw new Fault("The group wasn't saved: its longest range is shorter than its shortest range.");
			}

			List<string> allowedWeapons = new List<string>();
			List<string> unheldWeapons = new List<string>();
			JArray allowedWeaponsArr = (JArray)ruleInput["allowed_weapons"];
			foreach (JToken tok in allowedWeaponsArr)
			{
				string weaponName = (string)tok;
				ThingDef weaponDef = DefDatabase<ThingDef>.GetNamedSilentFail(weaponName);
				if (weaponDef == null)
				{
					// Strict enum-free string; try again case-insensitively before faulting.
					weaponDef = DefDatabase<ThingDef>.AllDefsListForReading
						.FirstOrDefault((ThingDef d) => string.Equals(d.defName, weaponName, StringComparison.OrdinalIgnoreCase));
				}
				if (weaponDef == null || !weaponDef.IsWeapon)
				{
					throw new Fault("The group wasn't saved: one of the named weapons doesn't exist.");
				}
				allowedWeapons.Add(weaponDef.defName);

				bool held = false;
				foreach (Pawn p in snap.pawnsById.Values)
				{
					ThingWithComps primary = p.equipment != null ? p.equipment.Primary : null;
					if (primary != null && primary.def == weaponDef)
					{
						held = true;
						break;
					}
				}
				if (!held)
				{
					unheldWeapons.Add(weaponDef.defName);
				}
			}

			bool includeUnarmed = (bool)ruleInput["include_unarmed"];

			// Brief 15c fix 7: ResolveGroupIds takes a plain-word phrase instead of the field name, for
			// its Fault message.
			List<string> alwaysIncludeIds = ResolveGroupIds((JArray)input["always_include_ids"], snap, "always include");
			List<string> alwaysExcludeIds = ResolveGroupIds((JArray)input["always_exclude_ids"], snap, "always leave out");

			GroupRule rule = new GroupRule();
			rule.name = name;
			rule.weaponClass = weaponClass;
			rule.minRange = minRange;
			rule.maxRange = maxRange;
			rule.allowedWeapons = allowedWeapons;
			rule.includeUnarmed = includeUnarmed;
			rule.alwaysIncludeIds = alwaysIncludeIds;
			rule.alwaysExcludeIds = alwaysExcludeIds;

			List<string> currentlyMatching = new List<string>();
			foreach (Pawn p in snap.pawnsById.Values)
			{
				if (rule.Matches(p))
				{
					currentlyMatching.Add(p.LabelShort);
				}
			}

			Plan plan = new Plan();
			plan.toolName = "define_group";

			Write write = new Write();
			write.description = "group " + slot + " \"" + name + "\": " + rule.Summary();
			write.apply = delegate
			{
				GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
				if (gc == null)
				{
					throw new Fault("The group wasn't saved: no game is currently loaded.");
				}
				gc.SetGroup(slot, rule);
				GroupHotkeyLayout.PromptIfUnset();
			};
			plan.writes.Add(write);

			plan.notes.Add(currentlyMatching.Count > 0
				? "currently matches: " + string.Join(", ", currentlyMatching)
				: "currently matches: nobody");
			if (unheldWeapons.Count > 0)
			{
				plan.notes.Add("not currently held by any colonist: " + string.Join(", ", unheldWeapons));
			}

			return plan;
		}

		// plainReason: e.g. "always include" / "always leave out" (brief 15c fix 7).
		private static List<string> ResolveGroupIds(JArray idsArr, Snapshot snap, string plainReason)
		{
			List<string> result = new List<string>();
			foreach (JToken tok in idsArr)
			{
				string id = (string)tok;
				if (!snap.pawnsById.ContainsKey(id))
				{
					throw new Fault("The group wasn't saved: a colonist it must " + plainReason + " no longer exists.");
				}
				result.Add(id);
			}
			return result;
		}

		private static Plan ResolveClearGroup(JObject input, Snapshot snap)
		{
			int slot = (int)input["slot"];
			if (slot < 1 || slot > 9)
			{
				// Brief 15c fix 7: plain words.
				throw new Fault("Nothing was cleared: hotkey slots are numbered 1 to 9.");
			}

			GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
			GroupRule existing = gc != null ? gc.GetGroup(slot) : null;
			if (existing == null)
			{
				throw new Fault("Nothing was cleared: hotkey slot " + slot + " is already empty.");
			}

			Plan plan = new Plan();
			plan.toolName = "clear_group";

			Write write = new Write();
			write.description = "clear group " + slot + " (\"" + existing.name + "\")";
			write.apply = delegate
			{
				GameComponent_VerbalCommands gcApply = GameComponent_VerbalCommands.Instance;
				if (gcApply == null)
				{
					throw new Fault("Nothing was cleared: no game is currently loaded.");
				}
				gcApply.ClearGroup(slot);
			};
			plan.writes.Add(write);

			return plan;
		}

		// ---- set_storage (brief 15 Step D, extended by brief 15b) ----------------------------------
		//
		// Decoder for the scoped storage slice (see the StorageIndex.cs header): every id in
		// storage_ids is resolved on its own with StorageIndex.FindUnit, so the model may send an
		// entry's whole "ids" array or a subset. Only ids that were in THIS order's slice are accepted
		// (brief 15b fix 10). Ids that land on the same StorageSettings object (a storage group id plus a
		// member shelf's ThingID, or two members of one group) are written once.
		//
		// Writes use the same calls as the storage tab, so the same change notifications fire:
		//   priority: StorageSettings.Priority setter (RimWorld\StorageSettings.cs:23-57; the tab sets it
		//     at RimWorld\ITab_Storage.cs:122-125) notifies haulDestinationManager and listerHaulables.
		//   filter: ThingFilter.SetAllow / SetDisallowAll (Verse\ThingFilter.cs:669-743,816-829; the tab
		//     calls them at Verse\Listing_TreeThingFilter.cs:148,177,247 and Verse\ThingFilterUI.cs:36),
		//     AllowedHitPointsPercents / AllowedQualityLevels setters (Verse\ThingFilter.cs:182-199,220-237;
		//     the tab's sliders, Verse\ThingFilterUI.cs:91-111) all invoke settingsChangedCallback =
		//     StorageSettings.TryNotifyChanged (RimWorld\StorageSettings.cs:59-62,131-134) ->
		//     owner.Notify_SettingsChanged (RimWorld\Zone_Stockpile.cs:121-127,
		//     RimWorld\Building_Storage.cs:119-125, RimWorld\StorageGroup.cs:149-158) ->
		//     listerHaulables.Notify_SlotGroupChanged (RimWorld\ListerHaulables.cs:69-80).
		// Brief 15b fix 5: set_storage can change everything the storage tab can (priority, items and
		// categories, special filters the tab lists, hit point and quality ranges where the tab has those
		// sliders; StorageIndex.TabView). The mental-break range is not in the storage tab and is left alone.
		// Brief 15b fix 11: things a storage cannot hold (StorageIndex.CanHold) and settings its tab does not
		// have are refused for that storage with a plain note; the rest still applies. A category is only
		// refused when nothing in it fits; otherwise the parts that fit are allowed (the tab only lists
		// those, Verse\Listing_TreeThingFilter.cs:325-351). Category allow/disallow never touches special
		// filters (every SpecialThingFilterDef is passed as exceptedFilters, Verse\ThingFilter.cs:731-737,819);
		// special filters change only when named.

		private sealed class StorageChange
		{
			public bool replace;
			public bool hasPriority;
			public StoragePriority priority;
			// Broad to narrow (fewest ancestor categories first) so a deeper category wins; on a tie
			// allows come before disallows.
			public List<KeyValuePair<ThingCategoryDef, bool>> categories = new List<KeyValuePair<ThingCategoryDef, bool>>();
			// Applied after the categories, so a single item overrides its category.
			public List<KeyValuePair<ThingDef, bool>> items = new List<KeyValuePair<ThingDef, bool>>();
			// Brief 15b fix 5.
			public List<KeyValuePair<SpecialThingFilterDef, bool>> specials = new List<KeyValuePair<SpecialThingFilterDef, bool>>();
			public bool hasHitPoints;
			public FloatRange hitPoints;
			public bool hasQuality;
			public QualityRange quality;
		}

		// What one storage unit actually gets: the shared change minus what that unit refuses.
		private sealed class UnitChange
		{
			public StorageChange change;
			public HashSet<ThingCategoryDef> refusedCategories = new HashSet<ThingCategoryDef>();
			public HashSet<ThingDef> refusedItems = new HashSet<ThingDef>();
			public HashSet<SpecialThingFilterDef> refusedSpecials = new HashSet<SpecialThingFilterDef>();
			public bool refuseHitPoints;
			public bool refuseQuality;

			public bool ChangesAnything
			{
				get
				{
					// Brief 15c fix 2: a `replace` that named at least one allow, all refused for this
					// storage, must not touch it at all (existing rule: a refused "only X" leaves the
					// storage as it was). Without this, SetDisallowAll (Verse\ThingFilter.cs:816-829)
					// would still clear the filter and nothing refused would be added back, so the
					// storage would end up accepting nothing.
					if (change.replace && ReplaceRefusesAllAllows())
					{
						return false;
					}
					return change.replace || change.hasPriority
						|| change.categories.Any((KeyValuePair<ThingCategoryDef, bool> kv) => !refusedCategories.Contains(kv.Key))
						|| change.items.Any((KeyValuePair<ThingDef, bool> kv) => !refusedItems.Contains(kv.Key))
						|| change.specials.Any((KeyValuePair<SpecialThingFilterDef, bool> kv) => !refusedSpecials.Contains(kv.Key))
						|| (change.hasHitPoints && !refuseHitPoints) || (change.hasQuality && !refuseQuality);
				}
			}

			// True when `change` is a replace that named at least one category or item to allow, and
			// every one of them is refused for this storage. A replace naming zero allows (disallow-only,
			// or nothing at all) is a deliberate "accept nothing" and is not covered here (brief 15c
			// followup, fix 2).
			private bool ReplaceRefusesAllAllows()
			{
				List<KeyValuePair<ThingCategoryDef, bool>> catAllows = change.categories
					.Where((KeyValuePair<ThingCategoryDef, bool> kv) => kv.Value).ToList();
				List<KeyValuePair<ThingDef, bool>> itemAllows = change.items
					.Where((KeyValuePair<ThingDef, bool> kv) => kv.Value).ToList();
				if (catAllows.Count == 0 && itemAllows.Count == 0)
				{
					return false;
				}
				return catAllows.All((KeyValuePair<ThingCategoryDef, bool> kv) => refusedCategories.Contains(kv.Key))
					&& itemAllows.All((KeyValuePair<ThingDef, bool> kv) => refusedItems.Contains(kv.Key));
			}
		}

		private static Plan ResolveSetStorage(JObject input, Snapshot snap, HashSet<string> sliceStorageIds)
		{
			Map map = snap.map;
			if (map == null || Find.Maps == null || !Find.Maps.Contains(map))
			{
				throw new Fault("The map this order was for is no longer loaded, so nothing was changed.");
			}

			StorageChange change = new StorageChange();

			string mode = ((string)input["mode"] ?? "").Trim();
			if (string.Equals(mode, "replace", StringComparison.OrdinalIgnoreCase))
			{
				change.replace = true;
			}
			else if (!string.Equals(mode, "modify", StringComparison.OrdinalIgnoreCase))
			{
				throw new Fault("The storage change had an unknown mode ('" + mode + "'), so nothing was changed.");
			}

			string priorityName = ((string)input["priority"] ?? "").Trim();
			if (priorityName.Length > 0 && !string.Equals(priorityName, ToolDefinitions.SetStoragePriorityUnchanged, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryParseStoragePriority(priorityName, out change.priority))
				{
					throw new Fault("'" + priorityName + "' is not a storage priority, so nothing was changed.");
				}
				change.hasPriority = true;
			}

			List<string> errors = new List<string>();
			List<ThingCategoryDef> allowCats = ResolveCategoryNames(input["allow_categories"] as JArray, errors);
			List<ThingCategoryDef> disallowCats = ResolveCategoryNames(input["disallow_categories"] as JArray, errors);
			List<ThingDef> allowItems = ResolveItemNames(input["allow_items"] as JArray, errors);
			List<ThingDef> disallowItems = ResolveItemNames(input["disallow_items"] as JArray, errors);
			List<SpecialThingFilterDef> allowSpecials = ResolveSpecialFilterNames(input["allow_special"] as JArray, errors);
			List<SpecialThingFilterDef> disallowSpecials = ResolveSpecialFilterNames(input["disallow_special"] as JArray, errors);
			foreach (ThingCategoryDef cat in allowCats)
			{
				if (disallowCats.Contains(cat))
				{
					errors.Add("category '" + StorageCategoryName(cat) + "' was both allowed and disallowed");
				}
			}
			foreach (ThingDef def in allowItems)
			{
				if (disallowItems.Contains(def))
				{
					errors.Add("'" + StorageItemName(def) + "' was both allowed and disallowed");
				}
			}
			foreach (SpecialThingFilterDef sf in allowSpecials)
			{
				if (disallowSpecials.Contains(sf))
				{
					errors.Add("the '" + StorageIndex.SpecialFilterName(sf) + "' setting was both switched on and off");
				}
			}
			ReadHitPoints(input["hit_points"], change, errors);
			ReadQuality(input["quality"], change, errors);
			if (errors.Count > 0)
			{
				throw new Fault("Nothing was changed: " + string.Join("; ", errors.ToArray()) + ".");
			}

			List<KeyValuePair<ThingCategoryDef, bool>> catOps = new List<KeyValuePair<ThingCategoryDef, bool>>();
			foreach (ThingCategoryDef cat in allowCats)
			{
				catOps.Add(new KeyValuePair<ThingCategoryDef, bool>(cat, true));
			}
			foreach (ThingCategoryDef cat in disallowCats)
			{
				catOps.Add(new KeyValuePair<ThingCategoryDef, bool>(cat, false));
			}
			// OrderBy is stable, so equal depths keep allows before disallows.
			change.categories = catOps.OrderBy((KeyValuePair<ThingCategoryDef, bool> kv) => kv.Key.Parents.Count()).ToList();
			foreach (ThingDef def in allowItems)
			{
				change.items.Add(new KeyValuePair<ThingDef, bool>(def, true));
			}
			foreach (ThingDef def in disallowItems)
			{
				change.items.Add(new KeyValuePair<ThingDef, bool>(def, false));
			}
			foreach (SpecialThingFilterDef sf in allowSpecials)
			{
				change.specials.Add(new KeyValuePair<SpecialThingFilterDef, bool>(sf, true));
			}
			foreach (SpecialThingFilterDef sf in disallowSpecials)
			{
				change.specials.Add(new KeyValuePair<SpecialThingFilterDef, bool>(sf, false));
			}

			if (!change.replace && !change.hasPriority && change.categories.Count == 0 && change.items.Count == 0
				&& change.specials.Count == 0 && !change.hasHitPoints && !change.hasQuality)
			{
				throw new Fault("The storage change would not change anything, so nothing was done.");
			}

			Plan plan = new Plan();
			plan.toolName = "set_storage";

			// Resolve EACH id on its own (StorageIndex.FindUnit, live objects, not the slice text), and only
			// ids this order's slice offered (brief 15b fix 10).
			JArray idsArr = input["storage_ids"] as JArray;
			if (idsArr == null || idsArr.Count == 0)
			{
				throw new Fault("The storage change named no storage, so nothing was changed.");
			}
			List<string> notInSlice = new List<string>();
			List<string> goneIds = new List<string>();
			List<string> targetIds = new List<string>();
			List<StorageUnit> targetUnits = new List<StorageUnit>();
			Dictionary<StorageSettings, string> firstIdBySettings = new Dictionary<StorageSettings, string>();
			HashSet<string> seenIds = new HashSet<string>(StringComparer.Ordinal);
			foreach (JToken tok in idsArr)
			{
				string id = ((string)tok ?? "").Trim();
				if (!seenIds.Add(id))
				{
					continue;
				}
				if (sliceStorageIds == null || !sliceStorageIds.Contains(id))
				{
					notInSlice.Add(id.Length > 0 ? id : "(empty)");
					continue;
				}
				StorageUnit unit = StorageIndex.FindUnit(map, id);
				StorageSettings settings = unit != null ? unit.Settings : null;
				if (settings == null || settings.filter == null)
				{
					goneIds.Add(id);
					continue;
				}
				if (unit.group != null && id != unit.id)
				{
					plan.notes.Add(id + " is linked in storage group '" + unit.label + "' (" + unit.id + ", " + unit.buildings.Count +
						" buildings); the change applies to the whole group");
				}
				string firstId;
				if (firstIdBySettings.TryGetValue(settings, out firstId))
				{
					plan.notes.Add(id + " and " + firstId + " are the same storage group (" + unit.id + "); changed once");
					continue;
				}
				firstIdBySettings[settings] = id;
				targetIds.Add(id);
				targetUnits.Add(unit);
			}
			if (notInSlice.Count > 0)
			{
				throw new Fault("The model picked storage that was not in this order's storage data (" + string.Join(", ", notInSlice.ToArray()) +
					"), so nothing was changed.");
			}
			if (goneIds.Count > 0)
			{
				throw new Fault("Some of the storage no longer exists (" + string.Join(", ", goneIds.ToArray()) + "), so nothing was changed.");
			}

			// Per-unit refusals (brief 15b fix 11), collected as sentence -> storage kinds that refuse it.
			List<string> refusalOrder = new List<string>();
			Dictionary<string, List<string>> refusedKinds = new Dictionary<string, List<string>>();
			Dictionary<ThingFilter, StorageIndex.TabView> views = new Dictionary<ThingFilter, StorageIndex.TabView>();
			int writesWithChanges = 0;
			for (int i = 0; i < targetUnits.Count; i++)
			{
				StorageUnit unit = targetUnits[i];
				UnitChange uc = RefusalsFor(unit, change, views, refusalOrder, refusedKinds);
				if (!uc.ChangesAnything)
				{
					continue;
				}
				writesWithChanges++;

				Write write = new Write();
				write.description = DescribeStorageWrite(unit, uc);
				string capturedId = targetIds[i];
				write.apply = delegate
				{
					// Re-resolve at apply time: the unit may have been removed, unlinked or relinked
					// while the plan waited for confirmation.
					if (Find.Maps == null || !Find.Maps.Contains(map))
					{
						throw new Fault("the map is no longer loaded");
					}
					StorageUnit live = StorageIndex.FindUnit(map, capturedId);
					if (live == null || live.Settings == null || live.Settings.filter == null)
					{
						throw new Fault("storage " + capturedId + " no longer exists");
					}
					ApplyStorageChange(live, uc);
				};
				plan.writes.Add(write.Reads(() => { StorageUnit u = StorageIndex.FindUnit(map, capturedId); return u == null || u.Settings == null ? "storage " + capturedId + ": gone" : u.label + ": " + u.Settings.Priority.ToString().ToLowerInvariant() + " priority"; }));
			}
			List<string> refusalNotes = new List<string>();
			foreach (string sentence in refusalOrder)
			{
				refusalNotes.Add(FillKinds(sentence, refusedKinds[sentence]));
			}
			if (writesWithChanges == 0)
			{
				throw new Fault("Nothing was changed. " + string.Join(" ", refusalNotes.ToArray()));
			}
			plan.notes.AddRange(refusalNotes);
			// Brief 15c fix 2: writesWithChanges already excludes any storage whose allows were all
			// refused (UnitChange.ChangesAnything / ReplaceRefusesAllAllows above skips its write
			// entirely), so this count is what is left after refusals, per storage. What remains here
			// is a deliberate "accept nothing" order (replace naming no allows at all).
			if (change.replace && change.categories.All((KeyValuePair<ThingCategoryDef, bool> kv) => !kv.Value)
				&& change.items.All((KeyValuePair<ThingDef, bool> kv) => !kv.Value))
			{
				plan.notes.Add("After this change, " + writesWithChanges + " storage(s) will accept nothing.");
			}

			return plan;
		}

		// Placeholder for the storage kinds in a refusal sentence (FillKinds).
		private const string KindsSlot = "{kinds}";
		private const string KindsSlotStart = "{Kinds}";

		// Works out what `unit` refuses from `change` and records one plain sentence per refused thing.
		private static UnitChange RefusalsFor(StorageUnit unit, StorageChange change, Dictionary<ThingFilter, StorageIndex.TabView> views,
			List<string> refusalOrder, Dictionary<string, List<string>> refusedKinds)
		{
			UnitChange uc = new UnitChange();
			uc.change = change;
			ThingFilter parentFilter = StorageParentFilter(unit);
			StorageIndex.TabView view;
			if (!views.TryGetValue(parentFilter, out view))
			{
				view = StorageIndex.BuildTabView(parentFilter);
				views[parentFilter] = view;
			}
			string kind = unit.kind.NullOrEmpty() ? "storage" : unit.kind;

			foreach (KeyValuePair<ThingCategoryDef, bool> op in change.categories)
			{
				// ThingCategoryDef.DescendantThingDefs (Verse\ThingCategoryDef.cs:70).
				if (op.Value && !op.Key.DescendantThingDefs.Any((ThingDef d) => StorageIndex.CanHold(parentFilter, d)))
				{
					uc.refusedCategories.Add(op.Key);
					AddRefusal(refusalOrder, refusedKinds, UpperFirst(StorageCategoryName(op.Key)) + " can't be stored in " + KindsSlot + ".", kind);
				}
			}
			foreach (KeyValuePair<ThingDef, bool> op in change.items)
			{
				if (op.Value && !StorageIndex.CanHold(parentFilter, op.Key))
				{
					uc.refusedItems.Add(op.Key);
					AddRefusal(refusalOrder, refusedKinds, UpperFirst(StorageItemName(op.Key)) + " can't be stored in " + KindsSlot + ".", kind);
				}
			}
			foreach (KeyValuePair<SpecialThingFilterDef, bool> op in change.specials)
			{
				if (!view.visibleSpecials.Contains(op.Key))
				{
					uc.refusedSpecials.Add(op.Key);
					AddRefusal(refusalOrder, refusedKinds, KindsSlotStart + " has no \"" + StorageIndex.SpecialFilterName(op.Key) +
						"\" setting, so that part was left out.", kind);
				}
			}
			if (change.hasHitPoints && !view.hitPointsConfigurable)
			{
				uc.refuseHitPoints = true;
				AddRefusal(refusalOrder, refusedKinds, KindsSlotStart + " has no hit point setting, so that part was left out.", kind);
			}
			if (change.hasQuality && !view.qualityConfigurable)
			{
				uc.refuseQuality = true;
				AddRefusal(refusalOrder, refusedKinds, KindsSlotStart + " has no quality setting, so that part was left out.", kind);
			}
			return uc;
		}

		private static void AddRefusal(List<string> order, Dictionary<string, List<string>> kinds, string sentence, string kind)
		{
			List<string> list;
			if (!kinds.TryGetValue(sentence, out list))
			{
				list = new List<string>();
				kinds[sentence] = list;
				order.Add(sentence);
			}
			if (!list.Contains(kind))
			{
				list.Add(kind);
			}
		}

		// "a shelf", "a shelf or a small shelf" (LanguageWorker.WithIndefiniteArticle(string, bool, bool),
		// Verse\LanguageWorker.cs:32; Find.ActiveLanguageWorker, Verse\Find.cs:34). Not Pluralize: English
		// gives "shelfs" (Verse\LanguageWorker_English.cs:130-153).
		private static string FillKinds(string sentence, List<string> kinds)
		{
			LanguageWorker worker = Find.ActiveLanguageWorker;
			List<string> parts = new List<string>();
			foreach (string kind in kinds)
			{
				parts.Add(worker != null ? worker.WithIndefiniteArticle(kind) : kind);
			}
			string phrase = string.Join(" or ", parts.ToArray());
			return sentence.Replace(KindsSlotStart, UpperFirst(phrase)).Replace(KindsSlot, phrase);
		}

		private static string UpperFirst(string s)
		{
			return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
		}

		// hit_points: null = unchanged, else {min_percent, max_percent} 0-100 (the tab's slider is a 0..1
		// FloatRange shown as percent, Verse\ThingFilterUI.cs:91-100).
		private static void ReadHitPoints(JToken token, StorageChange change, List<string> errors)
		{
			JObject range = token as JObject;
			if (range == null)
			{
				return;
			}
			int min;
			int max;
			if (!TryReadInt(range["min_percent"], out min) || !TryReadInt(range["max_percent"], out max)
				|| min < 0 || max > 100 || min > max)
			{
				errors.Add("the hit point range must be two whole percents from 0 to 100, lowest first");
				return;
			}
			change.hasHitPoints = true;
			change.hitPoints = new FloatRange(min / 100f, max / 100f);
		}

		// quality: null = unchanged, else {min, max} QualityCategory names (RimWorld\QualityCategory.cs:3-12)
		// or their labels (QualityUtility.GetLabel, RimWorld\QualityUtility.cs:39).
		private static void ReadQuality(JToken token, StorageChange change, List<string> errors)
		{
			JObject range = token as JObject;
			if (range == null)
			{
				return;
			}
			QualityCategory min;
			QualityCategory max;
			if (!TryParseQuality((string)range["min"], out min) || !TryParseQuality((string)range["max"], out max) || min > max)
			{
				errors.Add("the quality range must be two quality levels from awful to legendary, lowest first");
				return;
			}
			change.hasQuality = true;
			change.quality = new QualityRange(min, max);
		}

		private static bool TryReadInt(JToken token, out int value)
		{
			value = 0;
			if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
			{
				return false;
			}
			value = (int)Math.Round((double)token);
			return true;
		}

		private static bool TryParseQuality(string name, out QualityCategory quality)
		{
			string wanted = (name ?? "").Trim();
			foreach (QualityCategory q in Enum.GetValues(typeof(QualityCategory)))
			{
				if (string.Equals(q.ToString(), wanted, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(q.GetLabel(), wanted, StringComparison.OrdinalIgnoreCase))
				{
					quality = q;
					return true;
				}
			}
			quality = QualityCategory.Normal;
			return false;
		}

		private static void ApplyStorageChange(StorageUnit unit, UnitChange uc)
		{
			StorageChange change = uc.change;
			StorageSettings settings = unit.Settings;
			ThingFilter filter = settings.filter;
			ThingFilter parentFilter = StorageParentFilter(unit);
			List<SpecialThingFilterDef> keepSpecialFilters = DefDatabase<SpecialThingFilterDef>.AllDefsListForReading;

			if (change.hasPriority && settings.Priority != change.priority)
			{
				settings.Priority = change.priority;
			}
			if (change.replace)
			{
				filter.SetDisallowAll(null, keepSpecialFilters);
			}
			foreach (KeyValuePair<ThingCategoryDef, bool> op in change.categories)
			{
				if (uc.refusedCategories.Contains(op.Key))
				{
					continue;
				}
				if (op.Value)
				{
					// Defs this storage can't hold are excepted (the tab never lists them).
					HashSet<ThingDef> notHoldable = new HashSet<ThingDef>();
					foreach (ThingDef def in op.Key.DescendantThingDefs)
					{
						if (!StorageIndex.CanHold(parentFilter, def))
						{
							notHoldable.Add(def);
						}
					}
					filter.SetAllow(op.Key, true, notHoldable, keepSpecialFilters);
				}
				else
				{
					filter.SetAllow(op.Key, false, null, keepSpecialFilters);
				}
			}
			foreach (KeyValuePair<ThingDef, bool> op in change.items)
			{
				if (uc.refusedItems.Contains(op.Key) || (op.Value && !StorageIndex.CanHold(parentFilter, op.Key)))
				{
					continue;
				}
				filter.SetAllow(op.Key, op.Value);
			}
			// ThingFilter.SetAllow(SpecialThingFilterDef, bool) (Verse\ThingFilter.cs:694-716), as the tab's
			// special filter checkbox does (Verse\Listing_TreeThingFilter.cs:143-149).
			foreach (KeyValuePair<SpecialThingFilterDef, bool> op in change.specials)
			{
				if (!uc.refusedSpecials.Contains(op.Key))
				{
					filter.SetAllow(op.Key, op.Value);
				}
			}
			if (change.hasHitPoints && !uc.refuseHitPoints)
			{
				filter.AllowedHitPointsPercents = change.hitPoints;
			}
			if (change.hasQuality && !uc.refuseQuality)
			{
				filter.AllowedQualityLevels = change.quality;
			}
		}

		// Same fallback as StorageIndex / the game's generic storage (RimWorld\StorageSettings.cs:136-146).
		private static ThingFilter StorageParentFilter(StorageUnit unit)
		{
			StorageSettings parentSettings = unit.ParentSettings;
			if (parentSettings != null && parentSettings.filter != null)
			{
				return parentSettings.filter;
			}
			return StorageSettings.EverStorableFixedSettings().filter;
		}

		// Confirm line. Brief 15b fix 1: says where the storage is (StorageIndex.UnitRoomText). Refused
		// parts are left out (they are in the notes).
		private static string DescribeStorageWrite(StorageUnit unit, UnitChange uc)
		{
			StorageChange change = uc.change;
			List<string> parts = new List<string>();
			if (change.hasPriority)
			{
				StoragePriority current = unit.Settings.Priority;
				parts.Add(current == change.priority
					? "priority stays " + current
					: "priority " + current + " -> " + change.priority);
			}
			if (change.replace)
			{
				parts.Add("filter cleared first");
			}
			List<string> allowCats = new List<string>();
			List<string> disallowCats = new List<string>();
			foreach (KeyValuePair<ThingCategoryDef, bool> kv in change.categories)
			{
				if (!uc.refusedCategories.Contains(kv.Key))
				{
					(kv.Value ? allowCats : disallowCats).Add(StorageCategoryName(kv.Key));
				}
			}
			List<string> allowItems = new List<string>();
			List<string> disallowItems = new List<string>();
			foreach (KeyValuePair<ThingDef, bool> kv in change.items)
			{
				if (!uc.refusedItems.Contains(kv.Key))
				{
					(kv.Value ? allowItems : disallowItems).Add(StorageItemName(kv.Key));
				}
			}
			List<string> specialsOn = new List<string>();
			List<string> specialsOff = new List<string>();
			foreach (KeyValuePair<SpecialThingFilterDef, bool> kv in change.specials)
			{
				if (!uc.refusedSpecials.Contains(kv.Key))
				{
					(kv.Value ? specialsOn : specialsOff).Add(StorageIndex.SpecialFilterName(kv.Key));
				}
			}
			if (allowCats.Count > 0)
			{
				parts.Add("allow categories: " + string.Join(", ", allowCats.ToArray()));
			}
			if (disallowCats.Count > 0)
			{
				parts.Add("disallow categories: " + string.Join(", ", disallowCats.ToArray()));
			}
			if (allowItems.Count > 0)
			{
				parts.Add("allow items: " + string.Join(", ", allowItems.ToArray()));
			}
			if (disallowItems.Count > 0)
			{
				parts.Add("disallow items: " + string.Join(", ", disallowItems.ToArray()));
			}
			if (specialsOn.Count > 0)
			{
				parts.Add("switch on: " + string.Join(", ", specialsOn.ToArray()));
			}
			if (specialsOff.Count > 0)
			{
				parts.Add("switch off: " + string.Join(", ", specialsOff.ToArray()));
			}
			if (change.hasHitPoints && !uc.refuseHitPoints)
			{
				parts.Add("hit points " + Math.Round(change.hitPoints.min * 100f) + "-" + Math.Round(change.hitPoints.max * 100f) + "%");
			}
			if (change.hasQuality && !uc.refuseQuality)
			{
				parts.Add("quality " + change.quality.min.GetLabel() + " to " + change.quality.max.GetLabel());
			}
			// Brief 15c fix 3: UnitRoomText returns "" when nothing should be said about the room
			// (unknown room, or a room with no name); leave the "in ..." clause out entirely then.
			string roomText = StorageIndex.UnitRoomText(unit);
			string where = roomText.NullOrEmpty() ? "" : " in " + roomText;
			return StorageUnitName(unit) + where + ": " + string.Join("; ", parts.ToArray());
		}

		private static string StorageUnitName(StorageUnit unit)
		{
			return unit.label + " (" + unit.id + ")";
		}

		// The slice writes the root category as "all" (StorageIndex.CompactFilter) and shared labels in
		// their unique form (StorageIndex.CategoryName).
		private static string StorageCategoryName(ThingCategoryDef cat)
		{
			if (cat == ThingCategoryDefOf.Root)
			{
				return ToolDefinitions.SetStorageAllCategory;
			}
			return StorageIndex.CategoryName(cat);
		}

		// Same label the slice uses (StorageIndex.AddDef / CountContents).
		private static string StorageItemName(ThingDef def)
		{
			return def.label.NullOrEmpty() ? def.defName : def.label;
		}

		// StoragePriority names minus Unstored (the storage tab never offers it, RimWorld\ITab_Storage.cs:117-126).
		private static bool TryParseStoragePriority(string name, out StoragePriority priority)
		{
			foreach (StoragePriority p in Enum.GetValues(typeof(StoragePriority)))
			{
				if (p != StoragePriority.Unstored && string.Equals(p.ToString(), name, StringComparison.OrdinalIgnoreCase))
				{
					priority = p;
					return true;
				}
			}
			priority = StoragePriority.Normal;
			return false;
		}

		private static List<ThingCategoryDef> ResolveCategoryNames(JArray names, List<string> errors)
		{
			List<ThingCategoryDef> result = new List<ThingCategoryDef>();
			if (names == null)
			{
				return result;
			}
			foreach (JToken tok in names)
			{
				string name = ((string)tok ?? "").Trim();
				if (name.Length == 0)
				{
					continue;
				}
				ThingCategoryDef found;
				if (string.Equals(name, ToolDefinitions.SetStorageAllCategory, StringComparison.OrdinalIgnoreCase))
				{
					// ThingCategoryDefOf.Root (RimWorld\ThingCategoryDefOf.cs:12), the storage filter root
					// (Verse\ThingFilter.cs:103-113, Verse\ThingCategoryNodeDatabase.cs:15,25).
					found = ThingCategoryDefOf.Root;
				}
				else
				{
					List<ThingCategoryDef> matches = FindCategoryDefs(name);
					if (matches.Count > 1)
					{
						errors.Add("'" + name + "' could mean several categories (" +
							string.Join(", ", matches.Select((ThingCategoryDef d) => StorageIndex.CategoryName(d)).ToArray()) + ")");
						continue;
					}
					if (matches.Count == 0)
					{
						string hint = FindItemDefs(name).Count > 0 ? " ('" + name + "' is an item, not a category)" : "";
						errors.Add("there is no category called '" + name + "'" + hint);
						continue;
					}
					found = matches[0];
				}
				if (!result.Contains(found))
				{
					result.Add(found);
				}
			}
			return result;
		}

		private static List<ThingDef> ResolveItemNames(JArray names, List<string> errors)
		{
			List<ThingDef> result = new List<ThingDef>();
			if (names == null)
			{
				return result;
			}
			foreach (JToken tok in names)
			{
				string name = ((string)tok ?? "").Trim();
				if (name.Length == 0)
				{
					continue;
				}
				List<ThingDef> matches = FindItemDefs(name);
				if (matches.Count > 1)
				{
					errors.Add("'" + name + "' could mean several items (" +
						string.Join(", ", matches.Select((ThingDef d) => d.defName).ToArray()) + ")");
					continue;
				}
				if (matches.Count == 0)
				{
					string hint = FindCategoryDefs(name).Count > 0 || string.Equals(name, ToolDefinitions.SetStorageAllCategory, StringComparison.OrdinalIgnoreCase)
						? " ('" + name + "' is a category, not an item)" : "";
					errors.Add("there is no item called '" + name + "'" + hint);
					continue;
				}
				if (!result.Contains(matches[0]))
				{
					result.Add(matches[0]);
				}
			}
			return result;
		}

		private static List<SpecialThingFilterDef> ResolveSpecialFilterNames(JArray names, List<string> errors)
		{
			List<SpecialThingFilterDef> result = new List<SpecialThingFilterDef>();
			if (names == null)
			{
				return result;
			}
			foreach (JToken tok in names)
			{
				string name = ((string)tok ?? "").Trim();
				if (name.Length == 0)
				{
					continue;
				}
				List<SpecialThingFilterDef> matches = FindSpecialFilterDefs(name);
				if (matches.Count > 1)
				{
					errors.Add("'" + name + "' could mean several special filters (" +
						string.Join(", ", matches.Select((SpecialThingFilterDef d) => StorageIndex.SpecialFilterName(d)).ToArray()) + ")");
					continue;
				}
				if (matches.Count == 0)
				{
					errors.Add("there is no special filter called '" + name + "'");
					continue;
				}
				if (!result.Contains(matches[0]))
				{
					result.Add(matches[0]);
				}
			}
			return result;
		}

		// Unique form "label [defName]" (StorageIndex.CategoryName, brief 15b fix 2) -> that defName;
		// else defName match (case-insensitive), then label match.
		internal static List<ThingCategoryDef> FindCategoryDefs(string name)
		{
			string splitLabel;
			string splitDefName;
			if (StorageIndex.TrySplitUniqueName(name, out splitLabel, out splitDefName))
			{
				name = splitDefName;
			}
			List<ThingCategoryDef> byDefName = new List<ThingCategoryDef>();
			List<ThingCategoryDef> byLabel = new List<ThingCategoryDef>();
			foreach (ThingCategoryDef def in DefDatabase<ThingCategoryDef>.AllDefsListForReading)
			{
				if (string.Equals(def.defName, name, StringComparison.OrdinalIgnoreCase))
				{
					byDefName.Add(def);
				}
				else if (def.label != null && string.Equals(def.label.Trim(), name, StringComparison.OrdinalIgnoreCase))
				{
					byLabel.Add(def);
				}
			}
			return byDefName.Count > 0 ? byDefName : byLabel;
		}

		// Same rules as FindCategoryDefs; a label may also be named without its leading "allow "
		// ("rotten" for "allow rotten"; the filter key in AnthropicClient.ScopedSliceLabel says "such as rotten").
		internal static List<SpecialThingFilterDef> FindSpecialFilterDefs(string name)
		{
			string splitLabel;
			string splitDefName;
			if (StorageIndex.TrySplitUniqueName(name, out splitLabel, out splitDefName))
			{
				name = splitDefName;
			}
			List<SpecialThingFilterDef> byDefName = new List<SpecialThingFilterDef>();
			List<SpecialThingFilterDef> byLabel = new List<SpecialThingFilterDef>();
			List<SpecialThingFilterDef> byShortLabel = new List<SpecialThingFilterDef>();
			foreach (SpecialThingFilterDef def in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading)
			{
				string label = def.label != null ? def.label.Trim() : null;
				if (string.Equals(def.defName, name, StringComparison.OrdinalIgnoreCase))
				{
					byDefName.Add(def);
				}
				else if (label != null && string.Equals(label, name, StringComparison.OrdinalIgnoreCase))
				{
					byLabel.Add(def);
				}
				else if (label != null && string.Equals(label, "allow " + name, StringComparison.OrdinalIgnoreCase))
				{
					byShortLabel.Add(def);
				}
			}
			if (byDefName.Count > 0)
			{
				return byDefName;
			}
			return byLabel.Count > 0 ? byLabel : byShortLabel;
		}

		// Exact defName, then case-insensitive defName, then label. Several defs sharing a label are
		// narrowed to the ones a storage tab can list at all: storable (ThingFilter.AllStorableThingDefs,
		// Verse\ThingFilter.cs:178; ThingDef.EverStorable Verse\ThingDef.cs:1394), player-acquirable and
		// not virtual (Verse\Listing_TreeThingFilter.cs:325-334; Verse\ThingDef.cs:37,506).
		internal static List<ThingDef> FindItemDefs(string name)
		{
			List<ThingDef> result = new List<ThingDef>();
			ThingDef exact = DefDatabase<ThingDef>.GetNamedSilentFail(name);
			if (exact != null)
			{
				result.Add(exact);
				return result;
			}
			List<ThingDef> byLabel = new List<ThingDef>();
			foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if (string.Equals(def.defName, name, StringComparison.OrdinalIgnoreCase))
				{
					result.Add(def);
				}
				else if (def.label != null && string.Equals(def.label.Trim(), name, StringComparison.OrdinalIgnoreCase))
				{
					byLabel.Add(def);
				}
			}
			if (result.Count > 0)
			{
				return result;
			}
			if (byLabel.Count > 1)
			{
				List<ThingDef> listable = byLabel.Where((ThingDef d) => d.virtualDefParent == null && d.EverStorable(willMinifyIfPossible: true) && d.PlayerAcquirable).ToList();
				if (listable.Count > 0)
				{
					return listable;
				}
			}
			return byLabel;
		}
	}
}
