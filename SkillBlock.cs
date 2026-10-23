using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Red skills (user, 2026-10-16: "Any pawn that has [RED] skill level gets BLOCKED from doing it. I.E. Skill level 0~2.
	// Mod outputs to agents (when they try): "they are good at [2 passion skill list, 1 passion skill list, highest
	// skill], Assign them to do those instead?"" / scope: "Exclusion for BUILD ROOF stuff. Everything else will be
	// blocked."; rendering: the user's pick "Lists, labeled", empty lists left out).
	// The skill of a work type is the game's own number for it, Pawn_SkillTracker.AverageOfRelevantSkillsFor
	// (RimWorld\Pawn_SkillTracker.cs:136); a work type with no relevant skill (hauling, cleaning ...) is never blocked.
	// Used by the agents' orders that put a pawn on work: work priority (FormPlanner.PlanWorkPriority), prioritize
	// (AgentPawnActionsWork.PlanPrioritize), micro mine (AgentMicro), bills for pawns (FormBills). Not by
	// "build roof at AREA using PAWN" (user's exclusion).
	internal static class SkillBlock
	{
		public const int RedMax = 2;

		// The line for the agent when PAWN's skill for WORK is 0 to 2, else null.
		internal static string Check(Pawn pawn, WorkTypeDef work)
		{
			if (pawn == null || pawn.skills == null || work == null || work.relevantSkills.NullOrEmpty())
			{
				return null;
			}
			return pawn.skills.AverageOfRelevantSkillsFor(work) <= RedMax ? Line(pawn) : null;
		}

		// The same for one skill (a bill's RecipeDef.workSkill).
		internal static string Check(Pawn pawn, SkillDef skill)
		{
			if (pawn == null || pawn.skills == null || skill == null)
			{
				return null;
			}
			SkillRecord r = pawn.skills.GetSkill(skill);
			return r != null && !r.TotallyDisabled && r.Level <= RedMax ? Line(pawn) : null;
		}

		// "Stumpy: they are good at Plants, Mining (2 passion), Construction (1 passion), Mining 11 (highest skill), Assign
		// them to do those instead?" (the user's wording; the lists filled in, an empty one left out).
		internal static string Line(Pawn pawn)
		{
			List<SkillRecord> usable = pawn.skills.skills.Where((SkillRecord s) => s != null && !s.TotallyDisabled).ToList();
			List<string> parts = new List<string>();
			List<string> major = usable.Where((SkillRecord s) => s.passion == Passion.Major).Select((SkillRecord s) => (string)s.def.LabelCap).ToList();
			if (major.Count > 0)
			{
				parts.Add(string.Join(", ", major.ToArray()) + " (2 passion)");
			}
			List<string> minor = usable.Where((SkillRecord s) => s.passion == Passion.Minor).Select((SkillRecord s) => (string)s.def.LabelCap).ToList();
			if (minor.Count > 0)
			{
				parts.Add(string.Join(", ", minor.ToArray()) + " (1 passion)");
			}
			SkillRecord best = usable.OrderByDescending((SkillRecord s) => s.Level).FirstOrDefault();
			if (best != null)
			{
				parts.Add(best.def.LabelCap + " " + best.Level + " (highest skill)");
			}
			// The pawn's name in front (user, 2026-10-16: "well, add a pawnname in the format.").
			return pawn.LabelShort + ": they are good at " + string.Join(", ", parts.ToArray()) + ", Assign them to do those instead?";
		}
	}
}
