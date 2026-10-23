using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;
using Verse.AI;

namespace VerbalCommands
{
	// Brief 23: "plan this form with these slots" (contract section 2, host function plan). One dispatcher by
	// form name (Dispatch); each form builds a Plan whose writes are applied only after the player confirms,
	// the same Plan / Write objects the older tools produce. Where an older tool exists the form builds the
	// arguments the JSON tool takes and goes through Executor.Resolve / BuildTools.Resolve*, so the checks,
	// confirm text and apply step stay the ones already written. Forms with no code yet answer with a plain
	// "not built yet". MAIN THREAD ONLY (RouterGame moves every call here).
	//
	// The files of this class: FormPlanner.cs (dispatch, helpers, pawn forms), FormStorage.cs (storage and build
	// forms), FormBills.cs, FormControls.cs, FormGroups.cs.

	// Notes kept for one run of route(): what earlier sentences of the same order planned.
	public sealed class RunNotes
	{
		public readonly HashSet<Pawn> plannedDrafts = new HashSet<Pawn>();
		public readonly List<PlannedBill> plannedBills = new List<PlannedBill>();
		// User 2026-10-13: the control group an earlier sentence of this order planned for a slot, so a later
		// "take Stumpy out of control group 9." works on it (nothing is written until the player confirms).
		public readonly Dictionary<int, GroupRule> plannedGroups = new Dictionary<int, GroupRule>();
	}

	// What "those" can point at: storage as it stands (re-found by id when used) or buildings a "build"
	// sentence of the same order will place (the blueprints exist only once the order is applied).
	public sealed class StorageRef
	{
		public string unitId;
	}

	public sealed class BuildRef
	{
		public ThingDef def;
		public string label;
		public bool isStorage;
	}

	public sealed class FormContext
	{
		public Map map;
		public PlanRequest req;
		public RunNotes notes;
		// Lower-case form name, without a trailing " (those)".
		public string form;

		public FormContext(Map map, PlanRequest req)
		{
			this.map = map;
			this.req = req;
			notes = req.runState as RunNotes ?? new RunNotes();
			string f = (req.form ?? "").Trim().ToLowerInvariant();
			if (f.EndsWith(" (those)"))
			{
				f = f.Substring(0, f.Length - " (those)".Length);
			}
			form = f;
		}

		public bool Has(string key)
		{
			return req.slots.ContainsKey(key) && req.slots[key] != null;
		}

		// A text slot (a number slot is turned into its text); null when absent.
		public string Text(string key)
		{
			object o;
			if (!req.slots.TryGetValue(key, out o) || o == null)
			{
				return null;
			}
			if (o is double)
			{
				return Value_Format((double)o);
			}
			string s = o as string;
			return s != null ? s.Trim() : null;
		}

		public List<string> TextList(string key)
		{
			List<string> result = new List<string>();
			object o;
			if (!req.slots.TryGetValue(key, out o) || o == null)
			{
				return result;
			}
			List<object> list = o as List<object>;
			if (list != null)
			{
				foreach (object item in list)
				{
					string s = item is double ? Value_Format((double)item) : item as string;
					if (!string.IsNullOrWhiteSpace(s))
					{
						result.Add(s.Trim());
					}
				}
			}
			else
			{
				string s = Text(key);
				if (!string.IsNullOrEmpty(s))
				{
					result.Add(s);
				}
			}
			return result;
		}

		// A number slot (or text that is a number); null when absent or not a number.
		public double? Number(string key)
		{
			object o;
			if (!req.slots.TryGetValue(key, out o) || o == null)
			{
				return null;
			}
			if (o is double)
			{
				return (double)o;
			}
			double d;
			string s = o as string;
			if (s != null && double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d))
			{
				return d;
			}
			return null;
		}

		private static string Value_Format(double d)
		{
			return Math.Floor(d) == d && Math.Abs(d) < 1e15
				? ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture)
				: d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
		}
	}

	public static partial class FormPlanner
	{
		// Called by OrderController.ApplyAll before the writes run.
		public static void BeginApply()
		{
			BuildTools.RecentBlueprints.Clear();
		}

		public static PlanReply Dispatch(Map map, PlanRequest req)
		{
			FormContext c = new FormContext(map, req);
			try
			{
				if (map == null || Find.Maps == null || !Find.Maps.Contains(map))
				{
					return PlanReply.Fail("The map this order was for is no longer loaded, so nothing was changed.");
				}
				// User 2026-10-17: a build form lists the packed items of that name first, or installs one by its ID
				// (AgentInstall.cs); null when there are none.
				PlanReply packed = AgentInstall.PackedFirst(c);
				if (packed != null)
				{
					return packed;
				}
				switch (c.form)
				{
					case "storage holds only":
					case "storage also holds":
					case "storage no longer holds":
					case "storage priority":
						return PlanStorage(c);
					case "build":
						return PlanBuild(c);
					// Agent 02: run the game for a time, then pause (AgentTime.cs).
					case "run":
						return AgentTime.PlanRun(c);
					case "build room":
						return PlanBuildRoom(c);
					case "build option":
						return PlanBuildOption(c);
					// Agent 08: building with no room (AgentBuild.cs, AgentBuildShapes.cs, AgentRoomLayouts.cs).
					case "build at":
						return AgentBuild.PlanAt(c);
					case "build near":
						return AgentBuild.PlanNear(c);
					case "build anywhere":
						return AgentBuild.PlanAnywhere(c);
					case "place at":
						return AgentBuild.PlanPlaceAt(c);
					case "place near":
						return AgentBuild.PlanPlaceNear(c);
					case "build line":
						return AgentBuildShapes.PlanLine(c);
					case "build floor":
						return AgentBuildShapes.PlanFloor(c);
					// User 2026-10-17: a whole room's floor, and cancelling it (AgentBuildShapes.cs).
					case "build floor room":
						return AgentBuildShapes.PlanFloorRoom(c);
					case "cancel floor room":
						return AgentBuildShapes.PlanCancelFloorRoom(c);
					case "build room new":
						return AgentBuildShapes.PlanRoomNew(c);
					case "dig room":
						return AgentBuildShapes.PlanDigRoom(c);
					case "build furnished room":
						return AgentRoomLayouts.PlanFurnished(c);
					case "cancel blueprint at":
						return AgentBuild.PlanCancelAt(c);
					case "cancel blueprint area":
						return AgentBuild.PlanCancelArea(c);
					// Agent 06: zones and areas (AgentZones.cs, AgentAreas.cs).
					case "zone growing":
					case "zone stockpile":
					case "zone dumping":
					case "zone work":
					case "zone crop":
					case "zone grow":
					case "zone shrink":
					case "zone delete":
					case "zone rename":
						return AgentZones.Plan(c);
					case "area paint":
					case "area create":
					case "area delete":
						return AgentAreas.Plan(c);
					case "bed":
						return PlanBedAssign(c);
					case "allowed area":
						return PlanAllowedArea(c);
					case "schedule":
						return PlanSchedule(c);
					case "work priority":
						return PlanWorkPriority(c, false);
					case "work off":
						return PlanWorkPriority(c, true);
					case "bills":
						return PlanBillsOnlyPawn(c);
					case "draft":
						return PlanDraft(c);
					case "select pawns":
						return PlanSelectPawns(c);
					case "move":
						return PlanMove(c);
					case "weapon group":
						return PlanWeaponGroup(c);
					case "weapon group cleared":
						return PlanGroupCleared(c, "weapon group");
					// User 2026-10-13: control groups by skill level or by name (FormControlGroups.cs).
					case "control group skill min":
					case "control group skill max":
					case "control group skill over":
					case "control group skill under":
					case "control group skill between":
						return PlanControlGroupSkill(c);
					case "control group members":
						return PlanControlGroupMembers(c, true);
					case "control group remove":
						return PlanControlGroupMembers(c, false);
					case "control group cleared":
						return PlanGroupCleared(c, "control group");
				}
				// A right-click order on a person or cell, and the trade window (AgentInteract.cs); null for any other form.
				PlanReply interact = AgentInteract.Plan(c);
				if (interact != null)
				{
					return interact;
				}
				// Agent 09: what colonists do right now (AgentPawnActions*.cs); null for any other form.
				PlanReply pawnAction = AgentPawnActions.Plan(c);
				if (pawnAction != null)
				{
					return pawnAction;
				}
				// Agent 07: designations (AgentDesignations.cs, AgentDesignationQueries.cs); null for any other form.
				PlanReply designation = AgentDesignations.Plan(c) ?? AgentDesignationQueries.Plan(c);
				if (designation != null)
				{
					return designation;
				}
				PlanReply more = DispatchMore(c);
				if (more != null)
				{
					return more;
				}
				return PlanReply.Fail(NotBuilt(c.form));
			}
			catch (Fault fault)
			{
				return PlanReply.Fail(fault.Message);
			}
		}

		// ---- plain words ------------------------------------------------------------------------

		// A form name the mod has no code for (every form of contract section 3 has some, so this is for a form
		// the language gains later): said plainly, nothing is changed.
		private static string NotBuilt(string form)
		{
			return "The mod can't do that yet: '" + form + "' is not built yet.";
		}

		internal static string JoinNames(IEnumerable<string> names)
		{
			List<string> list = names.ToList();
			if (list.Count == 0)
			{
				return "";
			}
			if (list.Count == 1)
			{
				return list[0];
			}
			return string.Join(", ", list.Take(list.Count - 1).ToArray()) + " and " + list[list.Count - 1];
		}

		internal static string UpperFirst(string s)
		{
			return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
		}

		internal static string Norm(string s)
		{
			return Router.Normalize(s);
		}

		internal static bool SameName(string a, string b)
		{
			return Norm(a) == Norm(b);
		}

		internal static IList<object> AsObjects<T>(IEnumerable<T> items)
		{
			List<object> list = new List<object>();
			foreach (T item in items)
			{
				list.Add(item);
			}
			return list;
		}

		internal static PlanReply Reply(Plan plan, string line, IEnumerable<Pawn> pawns, IEnumerable<object> objects)
		{
			return PlanReply.Ok(line, plan, pawns != null ? AsObjects(pawns) : null, objects != null ? objects.ToList() : null);
		}

		internal static Plan NewPlan(string name)
		{
			Plan plan = new Plan();
			plan.toolName = name;
			return plan;
		}

		// ---- pawns, rooms, benches -------------------------------------------------------------------

		internal static Snapshot LiteSnapshot(Map map)
		{
			Snapshot snap = new Snapshot();
			snap.map = map;
			foreach (Pawn p in map.mapPawns.FreeColonists)
			{
				snap.pawnsById[p.ThingID] = p;
			}
			return snap;
		}

		internal static JObject ToolUse(string name, JObject input)
		{
			JObject use = new JObject();
			use["name"] = name;
			use["input"] = input;
			return use;
		}

		internal static JObject TargetsFor(IEnumerable<Pawn> pawns)
		{
			JArray ids = new JArray();
			foreach (Pawn p in pawns)
			{
				ids.Add(p.ThingID);
			}
			JObject targets = new JObject();
			targets["mode"] = "ids";
			targets["ids"] = ids;
			return targets;
		}

		private static Pawn FindPawn(Map map, string name)
		{
			string wanted = Norm(name);
			foreach (Pawn p in map.mapPawns.FreeColonists)
			{
				if (Norm(p.LabelShort) == wanted)
				{
					return p;
				}
				NameTriple triple = p.Name as NameTriple;
				if (triple != null && (Norm(triple.Nick) == wanted || Norm(triple.First) == wanted))
				{
					return p;
				}
				if (p.Name != null && Norm(p.Name.ToStringFull) == wanted)
				{
					return p;
				}
			}
			return null;
		}

		// PAWNS (names, "everyone", "them") plus whatever THEM points at.
		internal static List<Pawn> ResolvePawns(FormContext c, string key)
		{
			List<Pawn> pawns = new List<Pawn>();
			if (c.req.themPawns != null)
			{
				foreach (object o in c.req.themPawns)
				{
					Pawn p = o as Pawn;
					if (p != null && !pawns.Contains(p))
					{
						pawns.Add(p);
					}
				}
			}
			foreach (string name in c.TextList(key))
			{
				string n = Norm(name);
				if (n == "everyone" || n == "everybody" || n == "all")
				{
					foreach (Pawn p in c.map.mapPawns.FreeColonists)
					{
						if (!pawns.Contains(p))
						{
							pawns.Add(p);
						}
					}
				}
				else if (n == "them" || n == "they")
				{
					if (c.req.themPawns == null)
					{
						throw new Fault("'" + name + "' has no pawns selected earlier in this order.");
					}
				}
				else
				{
					Pawn p = FindPawn(c.map, name);
					if (p == null)
					{
						throw new Fault("There is no colonist called '" + name + "'.");
					}
					if (!pawns.Contains(p))
					{
						pawns.Add(p);
					}
				}
			}
			if (pawns.Count == 0)
			{
				throw new Fault("The order named nobody.");
			}
			return pawns;
		}

		internal static Pawn ResolveOnePawn(FormContext c, string key)
		{
			List<Pawn> pawns = ResolvePawns(c, key);
			if (pawns.Count != 1)
			{
				throw new Fault("That needs exactly one colonist, but the order named " + pawns.Count + ".");
			}
			return pawns[0];
		}

		internal static List<string> Names(IEnumerable<Pawn> pawns)
		{
			return pawns.Select((Pawn p) => p.LabelShort).ToList();
		}

		// Rooms with this label, as the game shows it. Refuses none.
		internal static List<Room> ResolveRooms(Map map, string label)
		{
			List<Room> rooms = StorageIndex.FindRoomsByLabel(map, label);
			if (rooms.Count == 0)
			{
				throw new Fault("There is no room called '" + label + "'.");
			}
			return rooms;
		}

		internal static List<Room> ResolveRoomsOrNull(Map map, string label)
		{
			return label == null ? null : ResolveRooms(map, label);
		}

		internal static Room RoomOfThing(Thing thing)
		{
			return BuildDefs.RoomOf(thing);
		}

		// Player benches by label, inside the rooms when given.
		internal static List<Building_WorkTable> ResolveBenches(Map map, string benchLabel, List<Room> rooms, string roomLabel)
		{
			List<Building_WorkTable> benches = StorageIndex.FindBenchesByLabel(map, benchLabel);
			if (benches.Count == 0)
			{
				throw new Fault("There is no " + benchLabel + " on the map.");
			}
			if (rooms != null)
			{
				benches = benches.Where((Building_WorkTable b) => rooms.Contains(RoomOfThing(b))).ToList();
				if (benches.Count == 0)
				{
					throw new Fault("There is no " + benchLabel + " in the " + roomLabel + ".");
				}
			}
			return benches;
		}

		// ---- schedule, work priorities (through Executor) ------------------------------------------------

		private static PlanReply PlanSchedule(FormContext c)
		{
			List<Pawn> pawns = ResolvePawns(c, "PAWNS");
			string assignmentText = c.Text("ASSIGNMENT");
			TimeAssignmentDef assignment = null;
			foreach (TimeAssignmentDef def in DefDatabase<TimeAssignmentDef>.AllDefsListForReading)
			{
				if (SameName(def.label, assignmentText) || SameName(def.defName, assignmentText))
				{
					assignment = def;
				}
			}
			if (assignment == null)
			{
				throw new Fault("'" + assignmentText + "' is not a way to spend time. The choices are " +
					JoinNames(DefDatabase<TimeAssignmentDef>.AllDefsListForReading.Select((TimeAssignmentDef d) => d.label.ToLowerInvariant())) + ".");
			}
			double? from = c.Number("FROM");
			double? to = c.Number("TO");
			if (from == null || to == null || from < 0 || from > 23 || to < 0 || to > 23 || from != Math.Floor(from.Value) || to != Math.Floor(to.Value))
			{
				throw new Fault("The hours must be whole numbers from 0 to 23.");
			}
			int fromHour = (int)from.Value;
			int toHour = (int)to.Value;
			if (fromHour == toHour)
			{
				throw new Fault("The schedule starts and ends at the same hour, so it is not clear how long it lasts.");
			}

			Snapshot snap = LiteSnapshot(c.map);
			Plan plan = NewPlan("set_schedule");
			foreach (Pawn pawn in pawns)
			{
				if (pawn.timetable == null)
				{
					plan.notes.Add(pawn.LabelShort + " has no schedule, so it was left out.");
					continue;
				}
				JArray slots = new JArray();
				for (int h = 0; h < 24; h++)
				{
					slots.Add(pawn.timetable.GetAssignment(h).defName);
				}
				for (int h = fromHour; h != toHour; h = (h + 1) % 24)
				{
					slots[h] = assignment.defName;
				}
				JObject input = new JObject();
				input["targets"] = TargetsFor(new Pawn[] { pawn });
				input["slots"] = slots;
				Plan one = Executor.Resolve(ToolUse("set_schedule", input), snap);
				string text = pawn.LabelShort + ": " + assignment.label.ToLowerInvariant() + " from " + fromHour.ToString("00") + ":00 to " + toHour.ToString("00") + ":00";
				foreach (Write write in one.writes)
				{
					write.description = text;
					plan.writes.Add(write);
				}
				plan.notes.AddRange(one.notes);
			}
			if (plan.writes.Count == 0)
			{
				throw new Fault("Nobody's schedule could be changed.");
			}
			string line = JoinNames(Names(pawns)) + ": " + assignment.label.ToLowerInvariant() + " from " + fromHour + " to " + toHour + " o'clock.";
			return Reply(plan, line, pawns, null);
		}

		private static PlanReply PlanWorkPriority(FormContext c, bool off)
		{
			List<Pawn> pawns = ResolvePawns(c, "PAWNS");
			string workText = c.Text("WORKTYPE");
			WorkTypeDef work = null;
			foreach (WorkTypeDef def in DefDatabase<WorkTypeDef>.AllDefsListForReading)
			{
				if (SameName(def.labelShort, workText) || SameName(def.gerundLabel, workText) || SameName(def.pawnLabel, workText)
					|| SameName(def.label, workText) || SameName(def.defName, workText))
				{
					work = def;
					break;
				}
			}
			if (work == null)
			{
				throw new Fault("'" + workText + "' is not a kind of work.");
			}
			int priority = 0;
			List<string> blocked = new List<string>();
			if (!off)
			{
				double? n = c.Number("N");
				if (n == null || n < 1 || n > 4 || n != Math.Floor(n.Value))
				{
					throw new Fault("Work priority must be a whole number from 1 to 4.");
				}
				priority = (int)n.Value;
				// Red skills (SkillBlock.cs, 2026-10-16): a pawn with skill 0 to 2 for this work is left out, with the
				// user's line; when every pawn is, the order is refused with it.
				List<Pawn> keep = new List<Pawn>();
				foreach (Pawn p in pawns)
				{
					string why = SkillBlock.Check(p, work);
					if (why != null)
					{
						blocked.Add(why);
					}
					else
					{
						keep.Add(p);
					}
				}
				if (keep.Count == 0)
				{
					throw new Fault(string.Join(" ", blocked.ToArray()));
				}
				pawns = keep;
			}

			JObject entry = new JObject();
			entry["work_type"] = work.defName;
			entry["priority"] = priority;
			JObject input = new JObject();
			input["targets"] = TargetsFor(pawns);
			input["priorities"] = new JArray(entry);
			Plan plan = Executor.Resolve(ToolUse("set_work_priorities", input), LiteSnapshot(c.map));
			plan.notes.AddRange(blocked);
			string what = work.labelShort.ToLowerInvariant();
			foreach (Write write in plan.writes)
			{
				int colon = write.description.IndexOf(':');
				string who = colon > 0 ? write.description.Substring(0, colon) : "";
				write.description = who + ": " + what + (off ? " is turned off" : " priority " + priority);
			}
			string line = off
				? JoinNames(Names(pawns)) + " stop " + work.gerundLabel.ToLowerInvariant() + "."
				: what + " priority " + priority + " for " + JoinNames(Names(pawns)) + ".";
			return Reply(plan, line, pawns, null);
		}

		// ---- bills: "let only PAWN do bills on BENCH" is in FormBills.cs ------------------------------------

		// ---- draft, select, move ---------------------------------------------------------------------------

		private static PlanReply PlanDraft(FormContext c)
		{
			List<Pawn> pawns = ResolvePawns(c, "PAWNS");
			Plan plan = NewPlan("draft");
			List<Pawn> done = new List<Pawn>();
			foreach (Pawn pawn in pawns)
			{
				if (pawn.drafter == null || pawn.Downed || !pawn.Spawned || pawn.Deathresting)
				{
					plan.notes.Add(pawn.LabelShort + " can't be drafted right now, so was left out.");
					continue;
				}
				done.Add(pawn);
				c.notes.plannedDrafts.Add(pawn);
				Write write = new Write();
				write.description = "draft " + pawn.LabelShort;
				Pawn capturedPawn = pawn;
				write.apply = delegate
				{
					capturedPawn.drafter.Drafted = true;
				};
				plan.writes.Add(write);
			}
			if (done.Count == 0)
			{
				throw new Fault("None of them can be drafted right now.");
			}
			return Reply(plan, "Draft " + JoinNames(Names(done)) + ".", done, null);
		}

		private static PlanReply PlanSelectPawns(FormContext c)
		{
			List<Pawn> pawns = ResolvePawns(c, "PAWNS");
			string condition = c.Text("CONDITION") ?? "";
			string n = Norm(condition);
			Func<Pawn, bool> test;
			if (n.Contains("melee"))
			{
				test = (Pawn p) => p.equipment != null && p.equipment.Primary != null && p.equipment.Primary.def.IsMeleeWeapon;
			}
			else if (n.Contains("ranged") || n.Contains("shooter"))
			{
				test = (Pawn p) => p.equipment != null && p.equipment.Primary != null && p.equipment.Primary.def.IsRangedWeapon;
			}
			else if (n.Contains("no weapon") || n.Contains("unarmed"))
			{
				test = (Pawn p) => p.equipment == null || p.equipment.Primary == null;
			}
			else
			{
				List<ThingDef> weapons = FindWeaponDefs(n);
				if (weapons.Count == 0)
				{
					throw new Fault("I can't tell who '" + condition + "' means yet. Try melee weapons, ranged weapons or the name of a weapon.");
				}
				test = (Pawn p) => p.equipment != null && p.equipment.Primary != null && weapons.Contains(p.equipment.Primary.def);
			}
			List<Pawn> matching = pawns.Where(test).ToList();
			if (matching.Count == 0)
			{
				throw new Fault("Nobody has " + condition + ".");
			}
			Plan plan = NewPlan("select pawns");
			Write write = new Write();
			write.description = "select " + JoinNames(Names(matching));
			write.apply = delegate
			{
				Find.Selector.ClearSelection();
				foreach (Pawn p in matching)
				{
					if (p.Spawned)
					{
						Find.Selector.Select(p, false, true);
					}
				}
			};
			plan.writes.Add(write);
			return Reply(plan, "Select " + JoinNames(Names(matching)) + ".", matching, null);
		}

		// Weapons whose label (or plural) appears in the text.
		internal static List<ThingDef> FindWeaponDefs(string normalizedText)
		{
			List<ThingDef> found = new List<ThingDef>();
			foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if (!def.IsWeapon || def.label.NullOrEmpty())
				{
					continue;
				}
				string label = Norm(def.label);
				if (ContainsWord(normalizedText, label) || ContainsWord(normalizedText, label + "s") || ContainsWord(normalizedText, label + "es"))
				{
					found.Add(def);
				}
			}
			return found;
		}

		internal static bool ContainsWord(string text, string word)
		{
			if (word.Length == 0)
			{
				return false;
			}
			int at = 0;
			while ((at = text.IndexOf(word, at, StringComparison.Ordinal)) >= 0)
			{
				bool startOk = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
				int end = at + word.Length;
				bool endOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
				if (startOk && endOk)
				{
					return true;
				}
				at++;
			}
			return false;
		}

		private static PlanReply PlanMove(FormContext c)
		{
			List<Pawn> pawns = ResolvePawns(c, "PAWNS");
			string roomLabel = c.Text("NAME");
			List<Room> rooms = ResolveRooms(c.map, roomLabel);
			// Pawns that are not drafted are drafted as part of this change (a player has to draft to send them);
			// pawns that can't be drafted at all are left out and named in a note.
			List<Pawn> skipped = new List<Pawn>();
			List<Pawn> toDraft = new List<Pawn>();
			foreach (Pawn pawn in pawns.ToList())
			{
				if (pawn.Downed || !pawn.Spawned || pawn.drafter == null || pawn.Deathresting)
				{
					skipped.Add(pawn);
					pawns.Remove(pawn);
				}
				else if (!pawn.Drafted && !c.notes.plannedDrafts.Contains(pawn))
				{
					toDraft.Add(pawn);
				}
			}
			if (pawns.Count == 0)
			{
				throw new Fault(JoinNames(Names(skipped)) + (skipped.Count == 1 ? " can't" : " can't") + " be drafted or moved right now.");
			}

			// Free cells in the room, nearest its middle first, one cell for each pawn.
			List<IntVec3> cells = new List<IntVec3>();
			foreach (Room room in rooms)
			{
				foreach (IntVec3 cell in room.Cells)
				{
					if (cell.Standable(c.map) && cell.GetDoor(c.map) == null && cell.GetFirstPawn(c.map) == null)
					{
						cells.Add(cell);
					}
				}
			}
			if (cells.Count < pawns.Count)
			{
				throw new Fault("The " + roomLabel + " has free floor for " + cells.Count + (cells.Count == 1 ? " pawn" : " pawns") + ", not " + pawns.Count + ".");
			}
			double cx = cells.Average((IntVec3 v) => (double)v.x);
			double cz = cells.Average((IntVec3 v) => (double)v.z);
			cells = cells.OrderBy((IntVec3 v) => (v.x - cx) * (v.x - cx) + (v.z - cz) * (v.z - cz)).ToList();

			Plan plan = NewPlan("move");
			if (toDraft.Count > 0)
			{
				plan.notes.Add(toDraft.Count + (toDraft.Count == 1 ? " pawn gets" : " pawns get") + " drafted to move (" + JoinNames(Names(toDraft)) + "). They stay drafted until you undraft them.");
			}
			if (skipped.Count > 0)
			{
				plan.notes.Add(JoinNames(Names(skipped)) + " can't be drafted or moved right now, so " + (skipped.Count == 1 ? "was" : "were") + " left out.");
			}
			List<IntVec3> used = new List<IntVec3>();
			foreach (Pawn pawn in pawns)
			{
				IntVec3 target = IntVec3.Invalid;
				foreach (IntVec3 cell in cells)
				{
					if (!used.Contains(cell) && pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly))
					{
						target = cell;
						break;
					}
				}
				if (!target.IsValid)
				{
					throw new Fault(pawn.LabelShort + " can't get to the " + roomLabel + ".");
				}
				used.Add(target);
				Write write = new Write();
				write.description = pawn.LabelShort + (toDraft.Contains(pawn) ? " is drafted and walks to the " : " walks to the ") + roomLabel;
				Pawn capturedPawn = pawn;
				IntVec3 capturedCell = target;
				write.apply = delegate
				{
					if (!capturedPawn.Drafted && capturedPawn.drafter != null)
					{
						capturedPawn.drafter.Drafted = true;
					}
					if (!capturedPawn.Drafted)
					{
						throw new Fault(capturedPawn.LabelShort + " could not be drafted, so was not sent.");
					}
					FloatMenuOptionProvider_DraftedMove.PawnGotoAction(capturedCell, capturedPawn, capturedCell);
				};
				plan.writes.Add(write);
			}
			return Reply(plan, "Send " + JoinNames(Names(pawns)) + " to the " + roomLabel + (toDraft.Count > 0 ? " (" + toDraft.Count + (toDraft.Count == 1 ? " is" : " are") + " drafted first)." : "."), pawns, null);
		}

		// ---- bed assignment -----------------------------------------------------------------------------

		private static PlanReply PlanBedAssign(FormContext c)
		{
			Pawn pawn = ResolveOnePawn(c, "PAWN");
			string roomLabel = c.Text("ROOM");
			List<Room> rooms = ResolveRooms(c.map, roomLabel);

			Building_Bed chosen = null;
			bool alreadyThere = false;
			int bedsSeen = 0;
			foreach (Room room in rooms)
			{
				foreach (Building_Bed bed in room.ContainedBeds)
				{
					CompAssignableToPawn comp = bed.CompAssignableToPawn;
					if (comp == null || bed.Faction != Faction.OfPlayer || bed.Medical || bed.ForPrisoners || !bed.def.building.bed_humanlike)
					{
						continue;
					}
					bedsSeen++;
					if (comp.AssignedPawnsForReading.Contains(pawn))
					{
						alreadyThere = true;
						chosen = bed;
						break;
					}
					if (chosen == null && comp.HasFreeSlot && comp.CanAssignTo(pawn).Accepted)
					{
						chosen = bed;
					}
				}
				if (alreadyThere)
				{
					break;
				}
			}
			if (bedsSeen == 0)
			{
				throw new Fault("There is no bed for colonists in the " + roomLabel + ".");
			}
			if (chosen == null)
			{
				throw new Fault("No bed in the " + roomLabel + " is free for " + pawn.LabelShort + ".");
			}
			Plan plan = NewPlan("bed");
			string line;
			if (alreadyThere)
			{
				plan.notes.Add(pawn.LabelShort + " already has a bed in the " + roomLabel + ".");
				line = pawn.LabelShort + " already has a bed in the " + roomLabel + ".";
			}
			else
			{
				Write write = new Write();
				write.description = pawn.LabelShort + " gets a bed in the " + roomLabel;
				Pawn capturedPawn = pawn;
				Building_Bed capturedBed = chosen;
				write.apply = delegate
				{
					// The same call the bed's assign dialog makes (CompAssignableToPawn_Bed.TryAssignPawn).
					capturedBed.CompAssignableToPawn.TryAssignPawn(capturedPawn);
				};
				plan.writes.Add(write);
				line = "Give " + pawn.LabelShort + " a bed in the " + roomLabel + ".";
			}
			return Reply(plan, line, new Pawn[] { pawn }, null);
		}

		// ---- allowed area from rooms -----------------------------------------------------------------------

		private static PlanReply PlanAllowedArea(FormContext c)
		{
			List<Pawn> pawns = ResolvePawns(c, "PAWNS");
			List<string> labels = c.TextList("ROOMS");
			if (labels.Count == 0)
			{
				throw new Fault("The order named no room to keep them in.");
			}
			List<Room> rooms = new List<Room>();
			foreach (string label in labels)
			{
				foreach (Room r in ResolveRooms(c.map, label))
				{
					if (!rooms.Contains(r))
					{
						rooms.Add(r);
					}
				}
			}
			string areaName = UpperFirst(JoinNames(labels));
			Area_Allowed existing = null;
			int allowedCount = 0;
			foreach (Area area in c.map.areaManager.AllAreas)
			{
				Area_Allowed allowed = area as Area_Allowed;
				if (allowed == null)
				{
					continue;
				}
				allowedCount++;
				if (SameName(allowed.Label, areaName))
				{
					existing = allowed;
				}
			}
			if (existing == null && allowedCount >= AreaManager.MaxAllowedAreas)
			{
				throw new Fault("No area was made: the colony already has as many areas as the game allows (" + AreaManager.MaxAllowedAreas + ").");
			}
			Map map = c.map;
			Plan plan = NewPlan("create_allowed_area");
			Write write = new Write();
			write.description = "keep " + JoinNames(Names(pawns)) + " in " + JoinNames(labels);
			write.apply = delegate
			{
				Area_Allowed area = existing;
				if (area == null && !map.areaManager.TryMakeNewAllowed(out area))
				{
					throw new Fault("Area '" + areaName + "' wasn't created: the colony already has as many areas as the game allows.");
				}
				if (existing == null)
				{
					area.SetLabel(areaName);
				}
				foreach (Room r in rooms)
				{
					foreach (IntVec3 cell in r.Cells)
					{
						area[cell] = true;
					}
				}
				foreach (Pawn p in pawns)
				{
					p.playerSettings.AreaRestrictionInPawnCurrentMap = area;
				}
			};
			plan.writes.Add(write);
			if (existing != null)
			{
				plan.notes.Add("The area '" + areaName + "' already exists; the rooms are added to it.");
			}
			return Reply(plan, "Keep " + JoinNames(Names(pawns)) + " in " + JoinNames(labels) + " (area '" + areaName + "').", pawns, null);
		}

		// ---- weapon groups (hotkey groups) -------------------------------------------------------------------

		private static PlanReply PlanWeaponGroup(FormContext c)
		{
			string name = c.Text("NAME") ?? "";
			string rule = c.Text("RULE") ?? "";
			string n = Norm(rule);

			string weaponClass = "any";
			if (n.Contains("melee"))
			{
				weaponClass = "melee";
			}
			else if (n.Contains("ranged") || n.Contains("shooter") || n.Contains("gun"))
			{
				weaponClass = "ranged";
			}
			bool unarmed = n.Contains("unarmed") || n.Contains("no weapon");
			List<ThingDef> weapons = FindWeaponDefs(n);
			double minRange = 0;
			double maxRange = 0;
			System.Text.RegularExpressions.Match num = System.Text.RegularExpressions.Regex.Match(n, "[0-9]+(\\.[0-9]+)?");
			if (num.Success && n.Contains("range"))
			{
				double value = double.Parse(num.Value, System.Globalization.CultureInfo.InvariantCulture);
				if (n.Contains("under") || n.Contains("less") || n.Contains("below") || n.Contains("at most") || n.Contains("shorter"))
				{
					maxRange = value;
				}
				else
				{
					minRange = value;
				}
			}
			bool anyWeapon = n.Contains("any weapon") || n.Contains("everyone") || n.Contains("everything");
			if (weaponClass == "any" && weapons.Count == 0 && !unarmed && !anyWeapon)
			{
				throw new Fault("I can't tell which weapons '" + rule + "' means. Say melee weapons, ranged weapons or a weapon name.");
			}

			GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
			if (gc == null)
			{
				throw new Fault("No game is currently loaded.");
			}
			// The slot: the group of that name if there is one, else the first empty slot.
			int slot = 0;
			for (int s = 1; s <= 9 && slot == 0; s++)
			{
				GroupRule existing = gc.GetGroup(s);
				if (existing != null && SameName(existing.name, name))
				{
					slot = s;
				}
			}
			for (int s = 1; s <= 9 && slot == 0; s++)
			{
				if (gc.GetGroup(s) == null)
				{
					slot = s;
				}
			}
			if (slot == 0)
			{
				throw new Fault("All nine weapon group slots are taken. Clear one first.");
			}

			JObject ruleInput = new JObject();
			ruleInput["weapon_class"] = weaponClass;
			ruleInput["min_range"] = (int)Math.Round(minRange);
			ruleInput["max_range"] = (int)Math.Round(maxRange);
			ruleInput["allowed_weapons"] = new JArray(weapons.Select((ThingDef d) => d.defName).ToArray());
			ruleInput["include_unarmed"] = unarmed;
			JObject input = new JObject();
			input["slot"] = slot;
			input["name"] = name;
			input["rule"] = ruleInput;
			input["always_include_ids"] = new JArray();
			input["always_exclude_ids"] = new JArray();
			Plan plan = Executor.Resolve(ToolUse("define_group", input), LiteSnapshot(c.map));
			return Reply(plan, "Make weapon group " + slot + " '" + name + "' hold " + rule + ".", null, null);
		}

		// word: "weapon group" or "control group" (user 2026-10-13); both empty the same hotkey slot.
		private static PlanReply PlanGroupCleared(FormContext c, string word)
		{
			double? slot = c.Number("SLOT");
			if (slot == null)
			{
				throw new Fault("The order did not say which " + word + " slot.");
			}
			JObject input = new JObject();
			input["slot"] = (int)slot.Value;
			Plan plan = Executor.Resolve(ToolUse("clear_group", input), LiteSnapshot(c.map));
			return Reply(plan, "Clear " + word + " " + (int)slot.Value + ".", null, null);
		}
	}
}
