using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// A stored RULE for one hotkey group slot (1-9), not a member list. Evaluated against live
	// equipment by GameComponent_VerbalCommands.ActivateGroup at keypress time; no LLM involvement
	// there. Persisted per-save via GameComponent_VerbalCommands.groups.
	public sealed class GroupRule : IExposable
	{
		public string name = "";
		public string weaponClass = "any";
		public float minRange;
		public float maxRange;
		public List<string> allowedWeapons = new List<string>();
		public bool includeUnarmed;
		public List<string> alwaysIncludeIds = new List<string>();
		public List<string> alwaysExcludeIds = new List<string>();
		// User 2026-10-13 (control groups, FormControlGroups.cs): a skill rule (SkillDef defName, "" = none) with its
		// level range, checked instead of the weapon rule; and membersOnly = only alwaysIncludeIds are in.
		public string skill = "";
		public int skillMin;
		public int skillMax = 20;
		public bool membersOnly;

		public void ExposeData()
		{
			Scribe_Values.Look(ref name, "name", "");
			Scribe_Values.Look(ref weaponClass, "weaponClass", "any");
			Scribe_Values.Look(ref minRange, "minRange", 0f);
			Scribe_Values.Look(ref maxRange, "maxRange", 0f);
			Scribe_Collections.Look(ref allowedWeapons, "allowedWeapons", LookMode.Value);
			Scribe_Values.Look(ref includeUnarmed, "includeUnarmed", false);
			Scribe_Collections.Look(ref alwaysIncludeIds, "alwaysIncludeIds", LookMode.Value);
			Scribe_Collections.Look(ref alwaysExcludeIds, "alwaysExcludeIds", LookMode.Value);
			Scribe_Values.Look(ref skill, "skill", "");
			Scribe_Values.Look(ref skillMin, "skillMin", 0);
			Scribe_Values.Look(ref skillMax, "skillMax", 20);
			Scribe_Values.Look(ref membersOnly, "membersOnly", false);

			if (Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				if (allowedWeapons == null)
				{
					allowedWeapons = new List<string>();
				}
				if (alwaysIncludeIds == null)
				{
					alwaysIncludeIds = new List<string>();
				}
				if (alwaysExcludeIds == null)
				{
					alwaysExcludeIds = new List<string>();
				}
				if (skill == null)
				{
					skill = "";
				}
			}
		}

		// Live evaluation against one pawn's current equipment. Forced include/exclude by
		// ThingID win over everything else; exclude wins over include.
		public bool Matches(Pawn p)
		{
			string id = p.ThingID;
			if (alwaysExcludeIds != null && alwaysExcludeIds.Contains(id))
			{
				return false;
			}
			if (alwaysIncludeIds != null && alwaysIncludeIds.Contains(id))
			{
				return true;
			}
			// User 2026-10-13: a names-only group holds nobody else; a skill group is the skill rule alone.
			if (membersOnly)
			{
				return false;
			}
			if (!string.IsNullOrEmpty(skill))
			{
				return SkillMatches(p);
			}

			ThingWithComps primary = p.equipment != null ? p.equipment.Primary : null;
			if (primary == null)
			{
				return includeUnarmed;
			}

			ThingDef def = primary.def;
			if (!def.IsWeapon)
			{
				return false;
			}

			if (weaponClass == "ranged" && !def.IsRangedWeapon)
			{
				return false;
			}
			if (weaponClass == "melee" && !def.IsMeleeWeapon)
			{
				return false;
			}

			if (def.IsRangedWeapon)
			{
				float range = WeaponRange(def);
				if (minRange > 0f && range < minRange)
				{
					return false;
				}
				if (maxRange > 0f && range > maxRange)
				{
					return false;
				}
			}

			if (allowedWeapons != null && allowedWeapons.Count > 0)
			{
				bool found = false;
				for (int i = 0; i < allowedWeapons.Count; i++)
				{
					if (string.Equals(allowedWeapons[i], def.defName, StringComparison.Ordinal))
					{
						found = true;
						break;
					}
				}
				if (!found)
				{
					return false;
				}
			}

			return true;
		}

		// User 2026-10-13: the pawn's level in the skill (SkillRecord.Level, RimWorld\SkillRecord.cs) within skillMin..skillMax;
		// a skill the pawn can not have (TotallyDisabled) never matches.
		public bool SkillMatches(Pawn p)
		{
			SkillDef def = DefDatabase<SkillDef>.GetNamedSilentFail(skill);
			if (def == null || p.skills == null)
			{
				return false;
			}
			SkillRecord record = p.skills.GetSkill(def);
			if (record == null || record.TotallyDisabled)
			{
				return false;
			}
			int level = record.Level;
			return level >= skillMin && level <= skillMax;
		}

		// "Mining 8 to 20", for the summary and the show line.
		public string SkillText()
		{
			SkillDef def = DefDatabase<SkillDef>.GetNamedSilentFail(skill);
			string label = def != null ? def.skillLabel : skill;
			if (!string.IsNullOrEmpty(label))
			{
				label = char.ToUpperInvariant(label[0]) + label.Substring(1);
			}
			return label + " " + skillMin + " to " + skillMax;
		}

		// A copy to change and store in place of this one (the lists are copied too).
		public GroupRule Copy()
		{
			GroupRule r = new GroupRule();
			r.name = name;
			r.weaponClass = weaponClass;
			r.minRange = minRange;
			r.maxRange = maxRange;
			r.allowedWeapons = new List<string>(allowedWeapons ?? new List<string>());
			r.includeUnarmed = includeUnarmed;
			r.alwaysIncludeIds = new List<string>(alwaysIncludeIds ?? new List<string>());
			r.alwaysExcludeIds = new List<string>(alwaysExcludeIds ?? new List<string>());
			r.skill = skill ?? "";
			r.skillMin = skillMin;
			r.skillMax = skillMax;
			r.membersOnly = membersOnly;
			return r;
		}

		public string Summary()
		{
			StringBuilder sb = new StringBuilder();
			if (membersOnly)
			{
				sb.Append("named colonists only");
			}
			else if (!string.IsNullOrEmpty(skill))
			{
				sb.Append("skill " + SkillText());
			}
			else
			{
				sb.Append(weaponClass);
			}
			if (minRange > 0f)
			{
				sb.Append(", range>=" + minRange.ToString("0.#"));
			}
			if (maxRange > 0f)
			{
				sb.Append(", range<=" + maxRange.ToString("0.#"));
			}
			if (allowedWeapons != null && allowedWeapons.Count > 0)
			{
				sb.Append(", weapons [" + string.Join(", ", allowedWeapons) + "]");
			}
			if (includeUnarmed)
			{
				sb.Append(", incl. unarmed");
			}
			if (alwaysIncludeIds != null && alwaysIncludeIds.Count > 0)
			{
				sb.Append(", +" + alwaysIncludeIds.Count + " forced");
			}
			if (alwaysExcludeIds != null && alwaysExcludeIds.Count > 0)
			{
				sb.Append(", -" + alwaysExcludeIds.Count + " excluded");
			}
			return sb.ToString();
		}

		// Range of the first non-melee verb, else 0. Verse/ThingDef.cs:659-668 (Verbs), Verse/VerbProperties.cs:29,282.
		public static float WeaponRange(ThingDef def)
		{
			if (def == null)
			{
				return 0f;
			}
			foreach (VerbProperties vp in def.Verbs)
			{
				if (!vp.IsMeleeAttack)
				{
					return vp.range;
				}
			}
			return 0f;
		}
	}
}
