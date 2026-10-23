using System;
using Newtonsoft.Json.Linq;
using RimWorld;

namespace VerbalCommands
{
	// Builds the `tools` array and the fixed system prompt sent to the model. Enums are pulled
	// from the Snapshot's vocabulary lists so they always match what is actually in DefDatabase
	// for the running mod list (e.g. Meditate only appears with Royalty active).
	public static class ToolDefinitions
	{
		// Brief 15b fix 3: short rules only. Headless never sends tool descriptions
		// (HeadlessClient.BuildJsonSchema forwards input_schema only), so every fact the model needs is here
		// or in a schema property description.
		private const string SystemPromptBase =
			"You turn one RimWorld colony order into tool calls.\n" +
			"- Colonist ids come from the snapshot's colonists array.\n" +
			"- Hours are 0-23; the snapshot's \"hour\" is the current hour.\n" +
			"- A schedule is always 24 slots, one per hour (index 0 = hour 0), and replaces the colonist's whole " +
			"schedule. Expand patterns like \"recreation every 6 hours\" into all 24 slots yourself. \"Joy\" is recreation.\n" +
			"- Never schedule Sleep for a colonist whose needs_rest is false.\n" +
			"- Work priority 1 is highest, 4 lowest, 0 off. If use_work_priorities is false, only 0 and 3 work.\n" +
			"- Allowed areas are built from room roles. The colony can have 10; area_slots_remaining says how many are left.\n" +
			"- A pawn group is a hotkey slot 1-9 that stores a rule (weapon class, range, weapons), not a list of " +
			"colonists. The rule is checked against current equipment each time the player presses the key " +
			"(Alt+key also drafts). The groups array shows taken slots. Weapons are defNames from weapons_held " +
			"or any real weapon.\n" +
			"- If the order is unclear, or names a colonist or place that doesn't exist, ask in one or two plain " +
			"sentences and call no tool.";

		// Added only when set_storage is offered (a scoped storage slice is attached).
		private const string SystemPromptStorage =
			"Storage (set_storage):\n" +
			"- Storage ids come from the SCOPED STORAGE DATA. Send a whole ids array to change every storage in " +
			"that entry, or some ids to change only those. Changing one shelf of a linked group changes the whole group.\n" +
			"- Item, category and special filter names may be any game label or defName, not only ones in the data. " +
			"A name shown as \"label [defName]\" shares its label with another def; copy that form.\n" +
			// Brief 15c fix 5: matches ApplyStorageChange (Executor.cs): replace clears allowed defs
			// (ThingFilter.SetDisallowAll, Verse\ThingFilter.cs:816-829) but passes every
			// SpecialThingFilterDef as exceptedFilters, so disallowedSpecialFilters is untouched, and
			// it never touches allowedHitPointsPercents/allowedQualities; those three only change when
			// this same call also sets them.
			"- mode \"modify\" changes the current filter; \"replace\" clears the allowed items and categories " +
			"first. Special filters, hit points and quality stay unless set. The category " +
			"\"all\" is everything the storage can hold. The narrowest entry wins: item over category, deeper " +
			"category over wider.\n" +
			"- hit_points and quality set the allowed ranges; null leaves a range as it is.\n" +
			"- Things a storage can't hold are refused and the player is told; the rest still applies.";

		// The prompt depends on the tool set, so OrderController builds it together with the tools and
		// re-uses both for the explain request.
		// Brief 21: offerBuild adds the build rules (BuildTools.SystemPromptBuild), only when the build
		// tools are offered.
		public static string BuildSystemPrompt(VerbalCommandsSettings settings, bool offerStorage, bool offerBuild = false)
		{
			string prompt = offerStorage ? SystemPromptBase + "\n\n" + SystemPromptStorage : SystemPromptBase;
			if (offerBuild)
			{
				prompt += "\n\n" + VerbalCommands.BuildTools.SystemPromptBuild;
			}
			string extra = settings != null ? settings.systemPromptExtra : null;
			if (string.IsNullOrEmpty(extra))
			{
				return prompt;
			}
			return prompt + "\n\n" + extra;
		}

		// Brief 15 Step D: offerStorage adds set_storage; the caller passes true only when the router
		// matched a storage combination AND a scoped storage slice with at least one storage unit is
		// attached to the order (brief 15b fix 9, Router.FinishRoute).
		// Brief 21: offerBuild adds place_blueprint, draw_plan and build_option; the caller passes true only
		// when the order or the router's rewrite said "build" or "construct" (Router.ComboBuild).
		public static JArray BuildTools(Snapshot snap, bool offerStorage, bool offerBuild = false)
		{
			JArray tools = new JArray();
			tools.Add(BuildTargetsUsingTool("set_schedule", BuildSetScheduleSchema(snap), SetScheduleDescription));
			tools.Add(BuildTargetsUsingTool("create_allowed_area", BuildCreateAllowedAreaSchema(snap), CreateAllowedAreaDescription));
			tools.Add(BuildTargetsUsingTool("set_work_priorities", BuildSetWorkPrioritiesSchema(snap), SetWorkPrioritiesDescription));
			tools.Add(BuildTargetsUsingTool("define_group", BuildDefineGroupSchema(snap), DefineGroupDescription));
			tools.Add(BuildTargetsUsingTool("clear_group", BuildClearGroupSchema(snap), ClearGroupDescription));
			if (offerStorage)
			{
				tools.Add(BuildTargetsUsingTool("set_storage", BuildSetStorageSchema(), SetStorageDescription));
			}
			if (offerBuild)
			{
				// Written in full: inside this class, "BuildTools" alone is the method above.
				VerbalCommands.BuildTools.AddTools(tools);
			}
			return tools;
		}

		private const string SetScheduleDescription =
			"Sets the 24-hour schedule (timetable) for one or more colonists, one time-assignment per in-game " +
			"hour. Use this whenever the order asks to change when colonists sleep, work, or take recreation, " +
			"including recurring patterns like \"recreation every 6 hours\" which you must expand into the 24 " +
			"slots yourself. The executor writes every slot for every targeted pawn in one shot; it does not " +
			"merge with the pawn's existing schedule. This will refuse the whole call if any slot name is not " +
			"one of the time_assignments listed in the snapshot, or if Meditate is requested without the " +
			"Royalty DLC active, or if the target set resolves to zero pawns.";

		private static JObject BuildSetScheduleSchema(Snapshot snap)
		{
			JObject slotsItem = new JObject();
			slotsItem["type"] = "string";
			slotsItem["enum"] = new JArray(snap.timeAssignmentNames);

			JObject slots = new JObject();
			slots["type"] = "array";
			slots["items"] = slotsItem;
			slots["description"] = "Exactly 24 entries, index 0 = hour 0 through index 23 = hour 23. The executor rejects any other length.";

			JObject note = new JObject();
			note["type"] = "string";

			JObject properties = new JObject();
			properties["targets"] = BuildTargetsSchema(snap);
			properties["slots"] = slots;
			properties["note"] = note;

			JObject schema = new JObject();
			schema["type"] = "object";
			schema["properties"] = properties;
			schema["required"] = new JArray("targets", "slots", "note");
			schema["additionalProperties"] = false;
			return schema;
		}

		private const string CreateAllowedAreaDescription =
			"Creates a new player allowed-area from one or more room roles (for example the kitchen or a rec " +
			"room), optionally including each targeted pawn's own bedroom, and restricts the targeted pawns to " +
			"it. Use this when the order asks to make a zone or area and confine colonists to it. The colony " +
			"can hold at most 10 allowed areas total; if creating the areas this call needs would exceed that " +
			"cap, the executor refuses the entire call and creates nothing, so warn the user in text if the " +
			"snapshot's area_slots_remaining looks too low before calling this. If a requested room role matches " +
			"no room on the map, the call is refused and lists which role matched nothing.";

		private static JObject BuildCreateAllowedAreaSchema(Snapshot snap)
		{
			JObject name = new JObject();
			name["type"] = "string";

			JObject roomRoleItem = new JObject();
			roomRoleItem["type"] = "string";
			roomRoleItem["enum"] = new JArray(snap.roomRoleNames);

			JObject roomRoles = new JObject();
			roomRoles["type"] = "array";
			roomRoles["items"] = roomRoleItem;

			JObject includeOwnBedroom = new JObject();
			includeOwnBedroom["type"] = "boolean";

			JObject properties = new JObject();
			properties["name"] = name;
			properties["room_roles"] = roomRoles;
			properties["include_own_bedroom"] = includeOwnBedroom;
			properties["targets"] = BuildTargetsSchema(snap);

			JObject schema = new JObject();
			schema["type"] = "object";
			schema["properties"] = properties;
			schema["required"] = new JArray("name", "room_roles", "include_own_bedroom", "targets");
			schema["additionalProperties"] = false;
			return schema;
		}

		private const string SetWorkPrioritiesDescription =
			"Sets work-type priorities (0 disabled, 1 highest through 4 lowest) for one or more colonists. Use " +
			"this when the order asks to change who does what job, or its priority. Pawns incapable of a " +
			"requested work type (permanently disabled, e.g. by a trait or incapability) are silently skipped " +
			"for that work type and reported back as a note rather than causing a failure. If manual work " +
			"priorities are turned off in the Work tab, the executor refuses the whole call unless every " +
			"requested priority is either 0 or 3, since only those two values are meaningful in that mode.";

		private static JObject BuildSetWorkPrioritiesSchema(Snapshot snap)
		{
			JObject workTypeItem = new JObject();
			workTypeItem["type"] = "string";
			workTypeItem["enum"] = new JArray(snap.workTypeNames);

			JObject priorityValue = new JObject();
			priorityValue["type"] = "integer";
			priorityValue["description"] = "Integer 0 to 4: 0 = disabled, 1 = highest priority, 4 = lowest. Any other value is rejected by the executor.";

			JObject priorityEntryProperties = new JObject();
			priorityEntryProperties["work_type"] = workTypeItem;
			priorityEntryProperties["priority"] = priorityValue;

			JObject priorityEntry = new JObject();
			priorityEntry["type"] = "object";
			priorityEntry["properties"] = priorityEntryProperties;
			priorityEntry["required"] = new JArray("work_type", "priority");
			priorityEntry["additionalProperties"] = false;

			JObject priorities = new JObject();
			priorities["type"] = "array";
			priorities["items"] = priorityEntry;

			JObject properties = new JObject();
			properties["targets"] = BuildTargetsSchema(snap);
			properties["priorities"] = priorities;

			JObject schema = new JObject();
			schema["type"] = "object";
			schema["properties"] = properties;
			schema["required"] = new JArray("targets", "priorities");
			schema["additionalProperties"] = false;
			return schema;
		}

		private const string DefineGroupDescription =
			"Defines or overwrites a rule-based pawn group in one of 9 hotkey slots. The rule is stored, not a " +
			"member list: pressing that slot's key later selects whichever colonists match the rule at that " +
			"moment (their equipment may have changed since this call), and holding Alt while pressing it also " +
			"drafts the matches. No further LLM involvement happens at keypress. allowed_weapons entries must " +
			"be defNames that come from the snapshot's weapons_held list, or another real weapon defName; the " +
			"executor compares case-insensitively. Check the snapshot's groups array first so you don't " +
			"silently overwrite a slot the player still wants.";

		private static JObject BuildDefineGroupSchema(Snapshot snap)
		{
			JObject slot = new JObject();
			slot["type"] = "integer";
			slot["description"] = "1 to 9; overwrites that slot.";

			JObject name = new JObject();
			name["type"] = "string";
			name["description"] = "Short label, 1-30 chars.";

			JObject weaponClass = new JObject();
			weaponClass["type"] = "string";
			weaponClass["enum"] = new JArray("ranged", "melee", "any");

			JObject minRange = new JObject();
			minRange["type"] = "integer";
			minRange["description"] = "Cells; 0 = no minimum; only meaningful for ranged.";

			JObject maxRange = new JObject();
			maxRange["type"] = "integer";
			maxRange["description"] = "Cells; 0 = no maximum.";

			JObject allowedWeaponItem = new JObject();
			allowedWeaponItem["type"] = "string";

			JObject allowedWeapons = new JObject();
			allowedWeapons["type"] = "array";
			allowedWeapons["items"] = allowedWeaponItem;
			allowedWeapons["description"] = "Weapon defNames; empty = any weapon passing class/range.";

			JObject includeUnarmed = new JObject();
			includeUnarmed["type"] = "boolean";

			JObject ruleProperties = new JObject();
			ruleProperties["weapon_class"] = weaponClass;
			ruleProperties["min_range"] = minRange;
			ruleProperties["max_range"] = maxRange;
			ruleProperties["allowed_weapons"] = allowedWeapons;
			ruleProperties["include_unarmed"] = includeUnarmed;

			JObject rule = new JObject();
			rule["type"] = "object";
			rule["properties"] = ruleProperties;
			rule["required"] = new JArray("weapon_class", "min_range", "max_range", "allowed_weapons", "include_unarmed");
			rule["additionalProperties"] = false;

			JObject idItem = new JObject();
			idItem["type"] = "string";

			JObject alwaysIncludeIds = new JObject();
			alwaysIncludeIds["type"] = "array";
			alwaysIncludeIds["items"] = idItem;

			JObject alwaysExcludeIds = new JObject();
			alwaysExcludeIds["type"] = "array";
			alwaysExcludeIds["items"] = idItem;

			JObject properties = new JObject();
			properties["slot"] = slot;
			properties["name"] = name;
			properties["rule"] = rule;
			properties["always_include_ids"] = alwaysIncludeIds;
			properties["always_exclude_ids"] = alwaysExcludeIds;

			JObject schema = new JObject();
			schema["type"] = "object";
			schema["properties"] = properties;
			schema["required"] = new JArray("slot", "name", "rule", "always_include_ids", "always_exclude_ids");
			schema["additionalProperties"] = false;
			return schema;
		}

		private const string ClearGroupDescription =
			"Clears a previously defined pawn group hotkey slot so it selects nothing until redefined.";

		private static JObject BuildClearGroupSchema(Snapshot snap)
		{
			JObject slot = new JObject();
			slot["type"] = "integer";
			slot["description"] = "1 to 9.";

			JObject properties = new JObject();
			properties["slot"] = slot;

			JObject schema = new JObject();
			schema["type"] = "object";
			schema["properties"] = properties;
			schema["required"] = new JArray("slot");
			schema["additionalProperties"] = false;
			return schema;
		}

		// ---- set_storage (brief 15 Step D) -----------------------------------------------------
		// Every property is required (no optional parameters) like the other strict tools: "unchanged",
		// empty arrays and null (hit_points / quality, the only union types) stand for "leave as is".
		// Strict-tool limits:
		// sources\anthropic_docs\structured_outputs.txt:494-501. The schema is constant (no colony
		// names): offering it only on storage orders gives two tool-set variants, each cached as a
		// compiled grammar (:372-382). HeadlessClient.BuildJsonSchema only
		// forwards input_schema, not the tool description, so the property descriptions carry the rules.

		public const string SetStoragePriorityUnchanged = "unchanged";
		public const string SetStorageAllCategory = "all";

		private const string SetStorageDescription =
			"Changes the storage settings (priority, and what may be stored) of stockpile zones and storage " +
			"buildings listed in the SCOPED STORAGE DATA attached to this order. storage_ids must be copied " +
			"from that data (storage[].ids, a bench's nearby[].id or a bill's store_id); never invent one. " +
			"All ids inside one storage entry currently have identical settings: pass the whole ids array to " +
			"change all of them, or only some ids to change just those (they then show as separate entries). " +
			"Linked shelves form one storage group; changing any id of a group changes the whole group. " +
			"Category, item and special filter names are labels exactly as shown in that data (filter cats/no_cats " +
			"are categories; filter defs/no_defs and contents are items; no_special are special filters; a shared " +
			"label is shown as \"label [defName]\") or defNames, compared case-insensitively; any category, item " +
			"or special filter in the game may be named, not only ones already listed. mode \"modify\" applies " +
			"the lists on top of the current filter; \"replace\" first disallows everything, then applies them " +
			"(replace with all lists empty = store nothing). The category \"all\" means everything the storage " +
			"can hold. Categories are applied broad to narrow, then items, so the most specific name wins. " +
			"hit_points and quality set the allowed ranges (null = leave as is); special filters (e.g. allow " +
			"rotten) are switched on or off by name. Everything the storage tab can change can be changed. " +
			"Things a storage cannot hold, and settings its storage tab does not have, are refused for that " +
			"storage and the player is told; the rest of the call still applies. priority \"unchanged\" keeps " +
			"each storage's current priority. The whole call is refused if any id is not in the scoped storage " +
			"data, if any name is unknown or ambiguous, or if it would change nothing.";

		private static JObject BuildSetStorageSchema()
		{
			JObject idItem = new JObject();
			idItem["type"] = "string";

			JObject storageIds = new JObject();
			storageIds["type"] = "array";
			storageIds["items"] = idItem;
			storageIds["description"] = "Storage unit ids from the SCOPED STORAGE DATA (storage[].ids): a whole ids array or a subset of it.";

			JObject mode = new JObject();
			mode["type"] = "string";
			mode["enum"] = new JArray("modify", "replace");
			mode["description"] = "modify = change on top of the current filter; replace = start from nothing allowed, then apply the lists.";

			// StoragePriority (RimWorld\StoragePriority.cs:3-11) minus Unstored, which the storage tab
			// never offers (RimWorld\ITab_Storage.cs:117-126).
			JArray priorityNames = new JArray();
			priorityNames.Add(SetStoragePriorityUnchanged);
			foreach (StoragePriority p in Enum.GetValues(typeof(StoragePriority)))
			{
				if (p != StoragePriority.Unstored)
				{
					priorityNames.Add(p.ToString());
				}
			}
			JObject priority = new JObject();
			priority["type"] = "string";
			priority["enum"] = priorityNames;
			priority["description"] = "New storage priority, or \"unchanged\".";

			JObject properties = new JObject();
			properties["storage_ids"] = storageIds;
			properties["mode"] = mode;
			properties["priority"] = priority;
			properties["allow_categories"] = BuildNameListSchema("Category labels (as in filter cats/no_cats) or defNames to allow; \"all\" = everything this storage can hold.");
			properties["disallow_categories"] = BuildNameListSchema("Category labels or defNames to disallow; \"all\" = everything.");
			properties["allow_items"] = BuildNameListSchema("Item labels (as in filter defs/no_defs or contents) or defNames to allow.");
			properties["disallow_items"] = BuildNameListSchema("Item labels or defNames to disallow.");
			// Brief 15b fix 5: everything the storage tab can change (RimWorld\ITab_Storage.cs:104-141):
			// hit point range (Verse\ThingFilter.cs:182-199), quality range (:220-237) and special filters
			// (SetAllow(SpecialThingFilterDef, bool), :694-716). Not the mental-break range (:201-218), which
			// the storage tab does not show. Two nullable parameters (union types, limit 16 per request,
			// sources\anthropic_docs\structured_outputs.txt:499).
			properties["hit_points"] = BuildHitPointsSchema();
			properties["quality"] = BuildQualitySchema();
			properties["allow_special"] = BuildNameListSchema("Special filter labels (as in filter no_special, e.g. \"allow rotten\") or defNames to switch on.");
			properties["disallow_special"] = BuildNameListSchema("Special filter labels or defNames to switch off.");

			JObject schema = new JObject();
			schema["type"] = "object";
			schema["properties"] = properties;
			schema["required"] = new JArray("storage_ids", "mode", "priority", "allow_categories", "disallow_categories", "allow_items", "disallow_items",
				"hit_points", "quality", "allow_special", "disallow_special");
			schema["additionalProperties"] = false;
			return schema;
		}

		private static JObject BuildHitPointsSchema()
		{
			JObject min = new JObject();
			min["type"] = "integer";
			min["description"] = "Lowest allowed hit points, percent 0-100.";
			JObject max = new JObject();
			max["type"] = "integer";
			max["description"] = "Highest allowed hit points, percent 0-100.";
			JObject rangeProperties = new JObject();
			rangeProperties["min_percent"] = min;
			rangeProperties["max_percent"] = max;
			JObject range = new JObject();
			range["type"] = "object";
			range["properties"] = rangeProperties;
			range["required"] = new JArray("min_percent", "max_percent");
			range["additionalProperties"] = false;
			return BuildNullable(range, "Allowed hit point range in percent (filter \"hp\"), or null to leave it as it is.");
		}

		// QualityCategory (RimWorld\QualityCategory.cs:3-12), awful to legendary.
		private static JObject BuildQualitySchema()
		{
			JArray names = new JArray();
			foreach (QualityCategory q in Enum.GetValues(typeof(QualityCategory)))
			{
				names.Add(q.ToString());
			}
			JObject min = new JObject();
			min["type"] = "string";
			min["enum"] = names;
			JObject max = new JObject();
			max["type"] = "string";
			max["enum"] = names.DeepClone();
			JObject rangeProperties = new JObject();
			rangeProperties["min"] = min;
			rangeProperties["max"] = max;
			JObject range = new JObject();
			range["type"] = "object";
			range["properties"] = rangeProperties;
			range["required"] = new JArray("min", "max");
			range["additionalProperties"] = false;
			return BuildNullable(range, "Allowed quality range (filter \"quality\"), or null to leave it as it is.");
		}

		private static JObject BuildNullable(JObject objectSchema, string description)
		{
			JObject nullSchema = new JObject();
			nullSchema["type"] = "null";
			JObject nullable = new JObject();
			nullable["anyOf"] = new JArray(objectSchema, nullSchema);
			nullable["description"] = description;
			return nullable;
		}

		private static JObject BuildNameListSchema(string description)
		{
			JObject nameItem = new JObject();
			nameItem["type"] = "string";

			JObject list = new JObject();
			list["type"] = "array";
			list["items"] = nameItem;
			list["description"] = description + " Empty array = none.";
			return list;
		}

		private static JObject BuildTargetsSchema(Snapshot snap)
		{
			JObject modeField = new JObject();
			modeField["type"] = "string";
			modeField["enum"] = new JArray("all", "no_rest_need", "ids");

			JObject idsItem = new JObject();
			idsItem["type"] = "string";

			JObject idsField = new JObject();
			idsField["type"] = "array";
			idsField["items"] = idsItem;

			JObject properties = new JObject();
			properties["mode"] = modeField;
			properties["ids"] = idsField;

			JObject schema = new JObject();
			schema["type"] = "object";
			schema["properties"] = properties;
			schema["required"] = new JArray("mode", "ids");
			schema["additionalProperties"] = false;
			schema["description"] =
				"Which colonists to apply this to: \"all\" for every free colonist, \"no_rest_need\" for those " +
				"whose needs_rest is false in the snapshot, or \"ids\" for an explicit list of colonist ids from " +
				"the snapshot's colonists array (leave ids empty for the other two modes).";
			return schema;
		}

		private static JObject BuildTargetsUsingTool(string name, JObject inputSchema, string description)
		{
			JObject tool = new JObject();
			tool["name"] = name;
			tool["description"] = description;
			tool["input_schema"] = inputSchema;
			tool["strict"] = true;
			return tool;
		}
	}
}
