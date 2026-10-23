using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// User 2026-10-13: control groups. The hotkey slots 1-9 (GroupRule, GameComponent_VerbalCommands.ActivateGroup:
	// the key selects every free colonist the slot's rule matches, Alt+key also drafts) filled in two more ways:
	//   by skill level   "put everyone with SKILL at least MIN / at most MAX / over MIN / under MAX / from MIN to MAX in
	//                    control group SLOT." stores a skill rule (GroupRule.skill, skillMin, skillMax), checked live at
	//                    keypress like a weapon rule, so a colonist who levels up joins by itself;
	//   by name          "put PAWNS in control group SLOT." / "take PAWNS out of control group SLOT." keep the names in
	//                    the slot (GroupRule.alwaysIncludeIds / alwaysExcludeIds; a slot made this way is membersOnly,
	//                    so nobody else is selected). A part of a name is enough when exactly one colonist fits
	//                    (the router already does this against the "colonists" line; ResolvePawnsLoose does it again
	//                    here for an order that came without a vocabulary).
	// Every plan line names the colonists the group will hold ("...: it will hold Alice and Bob."), and the player
	// confirms that line before anything is saved (the user's "confirmation ... with list of pawns"). A skill rule
	// replaces what the slot held; names are added to / taken from what it holds.
	// "show control group SLOT." (query control group) prints the rule and who is in it now.
	public static partial class FormPlanner
	{
		// The slot of a control group line: 1 to 9 (the router checked it; this is the mod's own guard).
		private static int ControlSlot(FormContext c)
		{
			double? slot = c.Number("SLOT");
			if (slot == null || slot.Value < 1 || slot.Value > 9 || slot.Value != Math.Floor(slot.Value))
			{
				throw new Fault("A control group is a hotkey slot, 1 to 9.");
			}
			return (int)slot.Value;
		}

		// The skill names as the vocabulary "skills" line and the Character tab show them (SkillDef.skillLabel,
		// listOrder highest first as in RimWorld\SkillUI.cs:47).
		internal static IEnumerable<string> SkillNames()
		{
			return DefDatabase<SkillDef>.AllDefsListForReading.OrderByDescending((SkillDef d) => d.listOrder).Select((SkillDef d) => UpperFirst(d.skillLabel));
		}

		private static SkillDef FindSkill(string text)
		{
			string n = Norm(text ?? "");
			foreach (SkillDef def in DefDatabase<SkillDef>.AllDefsListForReading)
			{
				if (Norm(def.skillLabel) == n || Norm(def.label) == n || Norm(def.defName) == n)
				{
					return def;
				}
			}
			throw new Fault("'" + text + "' is not a skill. The skills are " + JoinNames(SkillNames()) + ".");
		}

		private static int SkillLevel(FormContext c, string key)
		{
			double? v = c.Number(key);
			if (v == null)
			{
				throw new Fault("The order did not say the skill level.");
			}
			if (v.Value < 0 || v.Value > 20)
			{
				throw new Fault("A skill level is 0 to 20.");
			}
			return (int)v.Value;
		}

		private static PlanReply PlanControlGroupSkill(FormContext c)
		{
			int slot = ControlSlot(c);
			SkillDef skill = FindSkill(c.Text("SKILL"));
			int min = 0;
			int max = 20;
			string said;
			switch (c.form)
			{
				case "control group skill min":
					min = SkillLevel(c, "MIN");
					said = "at least " + min;
					break;
				case "control group skill max":
					max = SkillLevel(c, "MAX");
					said = "at most " + max;
					break;
				case "control group skill over":
					min = SkillLevel(c, "MIN") + 1;
					said = "over " + (min - 1);
					break;
				case "control group skill under":
					max = SkillLevel(c, "MAX") - 1;
					said = "under " + (max + 1);
					break;
				default:
					min = SkillLevel(c, "MIN");
					max = SkillLevel(c, "MAX");
					said = "from " + min + " to " + max;
					break;
			}
			if (min > max)
			{
				throw new Fault("No skill level is " + said + ": levels go from 0 to 20.");
			}
			string label = UpperFirst(skill.skillLabel);
			GroupRule rule = new GroupRule();
			rule.name = Cap30(label + " " + said);
			rule.skill = skill.defName;
			rule.skillMin = min;
			rule.skillMax = max;
			rule.weaponClass = "any";
			return ControlGroupPlan(c, slot, rule, "Put everyone with " + label + " " + said + " in control group " + slot, true);
		}

		private static PlanReply PlanControlGroupMembers(FormContext c, bool add)
		{
			int slot = ControlSlot(c);
			List<Pawn> named = ResolvePawnsLoose(c, "PAWNS");
			GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
			if (gc == null)
			{
				throw new Fault("No game is currently loaded.");
			}
			GroupRule existing = PlannedOrStored(c, gc, slot);
			GroupRule rule;
			if (existing == null)
			{
				if (!add)
				{
					throw new Fault("Control group " + slot + " is empty.");
				}
				rule = new GroupRule();
				rule.name = "Group " + slot;
				rule.membersOnly = true;
			}
			else
			{
				rule = existing.Copy();
			}
			foreach (Pawn p in named)
			{
				string id = p.ThingID;
				if (add)
				{
					rule.alwaysExcludeIds.Remove(id);
					if (!rule.alwaysIncludeIds.Contains(id))
					{
						rule.alwaysIncludeIds.Add(id);
					}
				}
				else
				{
					rule.alwaysIncludeIds.Remove(id);
					if (!rule.alwaysExcludeIds.Contains(id))
					{
						rule.alwaysExcludeIds.Add(id);
					}
				}
			}
			string lead = add
				? "Put " + JoinNames(Names(named)) + " in control group " + slot
				: "Take " + JoinNames(Names(named)) + " out of control group " + slot;
			return ControlGroupPlan(c, slot, rule, lead, false);
		}

		// The plan: one write that stores the rule in the slot (the same write Executor.ResolveDefineGroup makes); the
		// line names everyone the group will hold, and says when it replaces a differently named group.
		private static PlanReply ControlGroupPlan(FormContext c, int slot, GroupRule rule, string lead, bool replaces)
		{
			GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
			if (gc == null)
			{
				throw new Fault("No game is currently loaded.");
			}
			GroupRule existing = PlannedOrStored(c, gc, slot);
			List<Pawn> members = c.map.mapPawns.FreeColonists.Where((Pawn p) => rule.Matches(p)).ToList();
			Plan plan = NewPlan("define_group");
			Write write = new Write();
			write.description = "control group " + slot + " \"" + rule.name + "\": " + rule.Summary()
				+ (existing != null && replaces ? " (replaces \"" + existing.name + "\")" : "");
			GroupRule captured = rule;
			write.apply = delegate
			{
				GameComponent_VerbalCommands g = GameComponent_VerbalCommands.Instance;
				if (g == null)
				{
					throw new Fault("The group wasn't saved: no game is currently loaded.");
				}
				g.SetGroup(slot, captured);
				GroupHotkeyLayout.PromptIfUnset();
			};
			plan.writes.Add(write);
			c.notes.plannedGroups[slot] = rule;
			plan.notes.Add(members.Count > 0 ? "currently matches: " + string.Join(", ", Names(members).ToArray()) : "currently matches: nobody");
			string line = lead + ": it will hold " + (members.Count > 0 ? JoinNames(Names(members)) : "nobody right now");
			if (existing != null && replaces && !SameName(existing.name, rule.name))
			{
				line += "; this replaces '" + existing.name + "' in slot " + slot;
			}
			return Reply(plan, line + ".", members, null);
		}

		// What the slot holds for this order: the rule an earlier sentence of the same order planned, else the stored one.
		private static GroupRule PlannedOrStored(FormContext c, GameComponent_VerbalCommands gc, int slot)
		{
			GroupRule planned;
			if (c.notes.plannedGroups.TryGetValue(slot, out planned))
			{
				return planned;
			}
			return gc.GetGroup(slot);
		}

		private static string Cap30(string s)
		{
			return s.Length <= 30 ? s : s.Substring(0, 30);
		}

		// PAWNS like ResolvePawns, but a name that is no colonist's exact name is taken as part of one when exactly one
		// free colonist's short name, nickname, first name or full name contains it (user 2026-10-13: "full/partial name").
		internal static List<Pawn> ResolvePawnsLoose(FormContext c, string key)
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
					Pawn p = FindPawn(c.map, name) ?? FindPawnLoose(c.map, name);
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

		private static Pawn FindPawnLoose(Map map, string name)
		{
			string wanted = Norm(name);
			if (wanted.Length == 0)
			{
				throw new Fault("The order named nobody.");
			}
			List<Pawn> found = new List<Pawn>();
			foreach (Pawn p in map.mapPawns.FreeColonists)
			{
				NameTriple triple = p.Name as NameTriple;
				bool fits = Norm(p.LabelShort).Contains(wanted)
					|| (triple != null && (Norm(triple.Nick).Contains(wanted) || Norm(triple.First).Contains(wanted) || Norm(triple.Last).Contains(wanted)))
					|| (p.Name != null && Norm(p.Name.ToStringFull).Contains(wanted));
				if (fits)
				{
					found.Add(p);
				}
			}
			if (found.Count == 0)
			{
				throw new Fault("There is no colonist called '" + name + "'.");
			}
			if (found.Count > 1)
			{
				throw new Fault("'" + name + "' fits " + JoinNames(Names(found)) + "; say which.");
			}
			return found[0];
		}

		// ---- query control group -------------------------------------------------------------------------------

		private static string ControlGroupText(FormContext c)
		{
			int slot = ControlSlot(c);
			GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
			if (gc == null)
			{
				throw new Fault("No game is currently loaded.");
			}
			GroupRule rule = gc.GetGroup(slot);
			if (rule == null)
			{
				List<string> taken = gc.AllGroups.Where((KeyValuePair<int, GroupRule> kv) => kv.Value != null)
					.OrderBy((KeyValuePair<int, GroupRule> kv) => kv.Key)
					.Select((KeyValuePair<int, GroupRule> kv) => kv.Key + " (" + kv.Value.name + ")").ToList();
				return "Control group " + slot + " is empty." + (taken.Count > 0 ? " The groups in use are " + JoinNames(taken) + "." : " No control group is set yet.");
			}
			List<Pawn> colonists = c.map.mapPawns.FreeColonists.ToList();
			return "Control group " + slot + " '" + rule.name + "' holds " + WeaponRuleText(rule) + ".\n"
				+ PawnsLine(colonists.Where((Pawn p) => rule.Matches(p)), "Nobody is in it right now.");
		}
	}
}
