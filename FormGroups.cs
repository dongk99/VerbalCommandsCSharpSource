using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Brief 23, wave 3: groups and pawn settings (contract 3b). "Group" is the player's word for a policy:
	//   clothing group  OutfitDatabase.AllOutfits (ApparelPolicy), Pawn_OutfitTracker.CurrentApparelPolicy
	//   food group      FoodRestrictionDatabase.AllFoodRestrictions (FoodPolicy), Pawn_FoodRestrictionTracker.CurrentFoodPolicy
	//   drug group      DrugPolicyDatabase.AllPolicies (DrugPolicy), Pawn_DrugPolicyTracker.CurrentPolicy
	//   reading group   ReadingPolicyDatabase.AllReadingPolicies (ReadingPolicy), Pawn_ReadingTracker.CurrentPolicy
	//   medical care    Pawn_PlayerSettings.medCare (MedicalCareCategory; the game's labels in Keyed Enums.xml)
	//   threat response Pawn_PlayerSettings.hostilityResponse (HostilityResponseMode Ignore / Attack / Flee)
	//   undraft         Pawn_DraftController.Drafted = false
	//   area            Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap (null = anywhere)
	//   group things    ApparelPolicy.filter / FoodPolicy.filter (ThingFilter.SetAllow / SetDisallowAll)
	// Per the user (2026-10-02) the "medicine group" is the per-pawn medical care level, not a policy.
	public static partial class FormPlanner
	{
		private static PlanReply DispatchGroups(FormContext c)
		{
			switch (c.form)
			{
				case "clothing group":
					return PlanPolicyGroup(c, "clothing", "clothing groups", Current.Game.outfitDatabase.AllOutfits.Cast<Policy>().ToList(),
						(Pawn p) => p.outfits != null && p.outfits.CurrentApparelPolicy != null,
						(Pawn p, Policy policy) => p.outfits.CurrentApparelPolicy = (ApparelPolicy)policy);
				case "food group":
					return PlanPolicyGroup(c, "food", "food groups", Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Cast<Policy>().ToList(),
						(Pawn p) => p.foodRestriction != null && p.foodRestriction.CurrentFoodPolicy != null,
						(Pawn p, Policy policy) => p.foodRestriction.CurrentFoodPolicy = (FoodPolicy)policy);
				case "drug group":
					return PlanPolicyGroup(c, "drug", "drug groups", Current.Game.drugPolicyDatabase.AllPolicies.Cast<Policy>().ToList(),
						(Pawn p) => p.drugs != null && p.drugs.CurrentPolicy != null,
						(Pawn p, Policy policy) => p.drugs.CurrentPolicy = (DrugPolicy)policy);
				case "reading group":
					return PlanPolicyGroup(c, "reading", "reading groups", Current.Game.readingPolicyDatabase.AllReadingPolicies.Cast<Policy>().ToList(),
						(Pawn p) => p.reading != null && p.reading.CurrentPolicy != null,
						(Pawn p, Policy policy) => p.reading.CurrentPolicy = (ReadingPolicy)policy);
				case "medical care":
					return PlanMedicalCare(c);
				case "carry medicine":
					return PlanCarryMedicine(c);
				case "threat response":
					return PlanThreatResponse(c);
				case "undraft":
					return PlanUndraft(c);
				case "area":
					return PlanArea(c);
				case "group things only":
				case "group things allow":
				case "group things forbid":
					return PlanGroupThings(c);
			}
			return null;
		}

		private static PlanReply PlanPolicyGroup(FormContext c, string kind, string listName, List<Policy> policies,
			Func<Pawn, bool> hasTracker, Action<Pawn, Policy> assign)
		{
			List<Pawn> pawns = ResolvePawns(c, "PAWNS");
			string name = c.Text("NAME") ?? "";
			Policy policy = FindPolicy(policies, name, kind);
			Plan plan = NewPlan(c.form);
			List<Pawn> done = new List<Pawn>();
			foreach (Pawn pawn in pawns)
			{
				if (!hasTracker(pawn))
				{
					plan.notes.Add(pawn.LabelShort + " can't be given a " + kind + " group, so was left out.");
					continue;
				}
				done.Add(pawn);
				Write write = new Write();
				write.description = pawn.LabelShort + ": " + kind + " group " + policy.label;
				Pawn capturedPawn = pawn;
				write.apply = delegate { assign(capturedPawn, policy); };
				plan.writes.Add(write);
			}
			if (done.Count == 0)
			{
				throw new Fault("None of them can be given a " + kind + " group.");
			}
			return Reply(plan, "Put " + JoinNames(Names(done)) + " in the " + kind + " group '" + policy.label + "'.", done, null);
		}

		private static Policy FindPolicy(List<Policy> policies, string name, string kind)
		{
			List<Policy> found = policies.Where((Policy p) => Norm(p.label) == Norm(name)).ToList();
			if (found.Count == 1)
			{
				return found[0];
			}
			if (found.Count > 1)
			{
				throw new Fault("More than one " + kind + " group is called '" + name + "'.");
			}
			throw new Fault("There is no " + kind + " group called '" + name + "'." +
				(policies.Count > 0 ? " The " + kind + " groups are " + JoinNames(policies.Select((Policy p) => p.label)) + "." : ""));
		}

		// ---- medical care -----------------------------------------------------------------------------------------

		private static PlanReply PlanMedicalCare(FormContext c)
		{
			// Prisoners and colony animals too (user, 2026-10-15: the orchestrator's "assign Ben to medicine group ..." was
			// refused with "'ben' is not a colonist"; "Make that happen so you can change prisoner medicine group"). The
			// same Pawn_PlayerSettings.medCare the Health tab sets for any of them (HealthCardUtility's medical care
			// selector); "everyone" stays the colonists, as before.
			bool everyone = c.TextList("PAWNS").Any((string n) => Norm(n) == "everyone" || Norm(n) == "everybody" || Norm(n) == "all");
			List<Pawn> pawns = everyone ? ResolvePawns(c, "PAWNS") : ResolvePawnsFrom(c, "PAWNS", PawnsOfColony(c.map), "colonist, prisoner or animal");
			string text = c.Text("LEVEL") ?? "";
			MedicalCareCategory level = ParseMedicalCare(text);

			Plan plan = NewPlan("medical care");
			List<Pawn> done = new List<Pawn>();
			foreach (Pawn pawn in pawns)
			{
				if (pawn.playerSettings == null)
				{
					plan.notes.Add(pawn.LabelShort + " has no medical care setting, so was left out.");
					continue;
				}
				done.Add(pawn);
				Write write = new Write();
				write.description = pawn.LabelShort + ": medical care " + level.GetLabel();
				Pawn capturedPawn = pawn;
				write.apply = delegate { capturedPawn.playerSettings.medCare = level; };
				plan.writes.Add(write);
			}
			if (done.Count == 0)
			{
				throw new Fault("None of them has a medical care setting.");
			}
			return Reply(plan, "Medical care for " + JoinNames(Names(done)) + ": " + level.GetLabel() + ".", done, null);
		}

		// ---- carry medicine -----------------------------------------------------------------------------------------

		// set carry medicine of {PAWNS} to {N}[ {MEDICINE}]. (user, 2026-10-14: "see if mod has sentence so that doctors
		// can carry medicine manually" / "add sentence, make new vocabs to router as needed, and change dll as
		// necessary"). The Assign tab's Carry column (PawnColumnWorker_Carry.cs:11-53): Pawn_InventoryStockTracker
		// SetThingForGroup / SetCountForGroup for InventoryStockGroupDefOf.Medicine (Pawn_InventoryStockTracker.cs:45-62;
		// the count is clamped to the group's min..max, 0..3 in InventoryStockGroupDef.cs:10-12). The pawn then fetches it
		// itself (JobGiver_TakeForInventoryStock.cs:19-27). MEDICINE from the vocabulary line "carry medicines"; left out,
		// the kind the pawn has stays.
		private static PlanReply PlanCarryMedicine(FormContext c)
		{
			List<Pawn> pawns = ResolvePawns(c, "PAWNS");
			InventoryStockGroupDef group = InventoryStockGroupDefOf.Medicine;
			double? number = c.Number("N");
			if (number == null || number != Math.Floor(number.Value) || number < group.min || number > group.max)
			{
				throw new Fault("The number of medicine to carry must be a whole number from " + group.min + " to " + group.max + ".");
			}
			int count = (int)number.Value;
			ThingDef medicine = null;
			if (c.Has("MEDICINE"))
			{
				string word = Norm(c.Text("MEDICINE"));
				medicine = group.thingDefs.FirstOrDefault((ThingDef d) => Norm(d.label) == word || Norm(d.LabelCap) == word || Norm(d.defName) == word);
				if (medicine == null)
				{
					throw new Fault("'" + c.Text("MEDICINE") + "' is not a medicine a pawn can carry. They are " + JoinNames(group.thingDefs.Select((ThingDef d) => d.label)) + ".");
				}
			}

			Plan plan = NewPlan("carry medicine");
			List<Pawn> done = new List<Pawn>();
			foreach (Pawn pawn in pawns)
			{
				if (pawn.inventoryStock == null)
				{
					plan.notes.Add(pawn.LabelShort + " has no carry setting, so was left out.");
					continue;
				}
				done.Add(pawn);
				ThingDef kind = medicine ?? pawn.inventoryStock.GetDesiredThingForGroup(group);
				if (count > 0 && pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor))
				{
					plan.notes.Add(pawn.LabelShort + " can never do doctor work, so won't tend anyone with it.");
				}
				Write write = new Write();
				write.description = pawn.LabelShort + ": carry " + count + " " + kind.label;
				Pawn capturedPawn = pawn;
				write.apply = delegate
				{
					if (medicine != null)
					{
						capturedPawn.inventoryStock.SetThingForGroup(group, medicine);
					}
					capturedPawn.inventoryStock.SetCountForGroup(group, count);
				};
				plan.writes.Add(write.Reads(() => CarryLine(capturedPawn)));
			}
			if (done.Count == 0)
			{
				throw new Fault("None of them has a carry setting.");
			}
			if (count > 0)
			{
				plan.notes.Add("Each fetches the medicine from storage by itself when it has less than that.");
			}
			string what = count == 0 ? "no medicine" : count + " " + (medicine != null ? medicine.label : "of the medicine already set");
			return Reply(plan, "Carry " + what + ": " + JoinNames(Names(done)) + ".", done, null);
		}

		// "Bob: carry 2 herbal medicine (has 1)". Shared with the settings readback.
		internal static string CarryLine(Pawn pawn)
		{
			if (pawn.inventoryStock == null)
			{
				return pawn.LabelShort + ": no carry setting";
			}
			InventoryStockGroupDef group = InventoryStockGroupDefOf.Medicine;
			ThingDef kind = pawn.inventoryStock.GetDesiredThingForGroup(group);
			int want = pawn.inventoryStock.GetDesiredCountForGroup(group);
			int has = pawn.inventory != null ? pawn.inventory.Count(kind) : 0;
			return pawn.LabelShort + ": carry " + want + " " + kind.label + " (has " + has + ")";
		}

		// The game's own labels, and the plain words the player uses (no medicine, herbal, industrial, best, no care).
		internal static MedicalCareCategory ParseMedicalCare(string text)
		{
			string n = Norm(text);
			foreach (MedicalCareCategory m in VocabularyText.MedicalCareLevels())
			{
				if (Norm(m.GetLabel()) == n || Norm(m.ToString()) == n)
				{
					return m;
				}
			}
			if (n == "no care" || n == "none" || n == "no doctor" || n.Contains("no medical care"))
			{
				return MedicalCareCategory.NoCare;
			}
			if (n.Contains("no med") || n.Contains("doctor"))
			{
				return MedicalCareCategory.NoMeds;
			}
			if (n.Contains("herbal"))
			{
				return MedicalCareCategory.HerbalOrWorse;
			}
			if (n.Contains("industrial") || n.Contains("normal"))
			{
				return MedicalCareCategory.NormalOrWorse;
			}
			if (n.Contains("best") || n.Contains("glitterworld") || n.Contains("whatever"))
			{
				return MedicalCareCategory.Best;
			}
			throw new Fault("'" + text + "' is not a medical care level. The levels are: " +
				JoinNames(VocabularyText.MedicalCareLevels().Select((MedicalCareCategory m) => m.GetLabel().ToString())) + ".");
		}

		// ---- threat response ------------------------------------------------------------------------------------------

		private static PlanReply PlanThreatResponse(FormContext c)
		{
			List<Pawn> pawns = ResolvePawns(c, "PAWNS");
			string text = Norm(c.Text("RESPONSE") ?? "");
			HostilityResponseMode mode;
			switch (text)
			{
				case "flee":
					mode = HostilityResponseMode.Flee;
					break;
				case "attack":
				// `set combatmode RESPONSE for PAWNS.` (user, 2026-10-17: "switch combat mode to auto-engage, ignore, flee" /
				// "combatmode, not combat mode (no spacebar)"): auto-engage is the game's Attack.
				case "auto-engage":
				case "auto engage":
					mode = HostilityResponseMode.Attack;
					break;
				case "ignore":
					mode = HostilityResponseMode.Ignore;
					break;
				default:
					throw new Fault("'" + text + "' is not a combatmode. Use auto-engage, flee or ignore.");
			}
			Plan plan = NewPlan("threat response");
			List<Pawn> done = new List<Pawn>();
			foreach (Pawn pawn in pawns)
			{
				if (pawn.playerSettings == null)
				{
					plan.notes.Add(pawn.LabelShort + " has no threat response setting, so was left out.");
					continue;
				}
				done.Add(pawn);
				Write write = new Write();
				write.description = pawn.LabelShort + ": combatmode " + AgentPawnWatch.CombatModeWord(mode) + " (" + mode.GetLabel().ToString().ToLowerInvariant() + " when threatened)";
				Pawn capturedPawn = pawn;
				write.apply = delegate { capturedPawn.playerSettings.hostilityResponse = mode; };
				plan.writes.Add(write);
			}
			if (done.Count == 0)
			{
				throw new Fault("None of them has a threat response setting.");
			}
			return Reply(plan, JoinNames(Names(done)) + ": combatmode " + AgentPawnWatch.CombatModeWord(mode) + " (" + mode.GetLabel().ToString().ToLowerInvariant() + " when threatened).", done, null);
		}

		// ---- undraft --------------------------------------------------------------------------------------------------

		private static PlanReply PlanUndraft(FormContext c)
		{
			List<Pawn> pawns = ResolvePawns(c, "PAWNS");
			Plan plan = NewPlan("undraft");
			List<Pawn> done = new List<Pawn>();
			foreach (Pawn pawn in pawns)
			{
				if (pawn.drafter == null || (!pawn.Drafted && !c.notes.plannedDrafts.Contains(pawn)))
				{
					plan.notes.Add(pawn.LabelShort + " is not drafted, so was left out.");
					continue;
				}
				done.Add(pawn);
				c.notes.plannedDrafts.Remove(pawn);
				Write write = new Write();
				write.description = "undraft " + pawn.LabelShort;
				Pawn capturedPawn = pawn;
				write.apply = delegate { capturedPawn.drafter.Drafted = false; };
				plan.writes.Add(write);
			}
			if (done.Count == 0)
			{
				throw new Fault("None of them is drafted.");
			}
			return Reply(plan, "Undraft " + JoinNames(Names(done)) + ".", done, null);
		}

		// ---- area ------------------------------------------------------------------------------------------------------

		private static PlanReply PlanArea(FormContext c)
		{
			List<Pawn> pawns = ResolvePawns(c, "PAWNS");
			string name = c.Text("NAME") ?? "";
			Area_Allowed area = null;
			bool anywhere = Norm(name) == "anywhere";
			if (!anywhere)
			{
				List<Area_Allowed> areas = c.map.areaManager.AllAreas.OfType<Area_Allowed>().Where((Area_Allowed a) => Norm(a.Label) == Norm(name)).ToList();
				if (areas.Count == 0)
				{
					List<string> existing = c.map.areaManager.AllAreas.OfType<Area_Allowed>().Select((Area_Allowed a) => a.Label).ToList();
					throw new Fault("There is no area called '" + name + "'." + (existing.Count > 0 ? " The areas are " + JoinNames(existing) + "." : ""));
				}
				area = areas[0];
			}
			Plan plan = NewPlan("area");
			List<Pawn> done = new List<Pawn>();
			foreach (Pawn pawn in pawns)
			{
				if (pawn.playerSettings == null)
				{
					plan.notes.Add(pawn.LabelShort + " can't be kept to an area, so was left out.");
					continue;
				}
				done.Add(pawn);
				Write write = new Write();
				write.description = pawn.LabelShort + ": " + (anywhere ? "go anywhere" : "stay in " + area.Label);
				Pawn capturedPawn = pawn;
				Area_Allowed capturedArea = area;
				write.apply = delegate { capturedPawn.playerSettings.AreaRestrictionInPawnCurrentMap = capturedArea; };
				plan.writes.Add(write);
			}
			if (done.Count == 0)
			{
				throw new Fault("None of them can be kept to an area.");
			}
			string line = anywhere
				? "Let " + JoinNames(Names(done)) + " go anywhere."
				: "Keep " + JoinNames(Names(done)) + " in the area '" + area.Label + "'.";
			return Reply(plan, line, done, null);
		}

		// ---- what a clothing or food group allows --------------------------------------------------------------------

		private static PlanReply PlanGroupThings(FormContext c)
		{
			string kind = Norm(c.Text("KIND") ?? "");
			string name = c.Text("NAME") ?? "";
			ThingFilter filter;
			Func<ThingDef, bool> belongs;
			string groupLabel;
			if (kind == "clothing" || kind == "apparel")
			{
				ApparelPolicy policy = (ApparelPolicy)FindPolicy(Current.Game.outfitDatabase.AllOutfits.Cast<Policy>().ToList(), name, "clothing");
				filter = policy.filter;
				groupLabel = "clothing group '" + policy.label + "'";
				belongs = (ThingDef d) => d.IsApparel;
			}
			else if (kind == "food")
			{
				FoodPolicy policy = (FoodPolicy)FindPolicy(Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Cast<Policy>().ToList(), name, "food");
				filter = policy.filter;
				groupLabel = "food group '" + policy.label + "'";
				belongs = (ThingDef d) => d.IsNutritionGivingIngestible;
			}
			else
			{
				throw new Fault("Changing what a " + kind + " group allows is not built yet. Only clothing and food groups have a list of things.");
			}

			List<string> things = c.TextList("THINGS");
			if (things.Count == 0)
			{
				throw new Fault("The order did not say which things.");
			}
			List<string> catNames = new List<string>();
			List<string> itemNames = new List<string>();
			ClassifyThings(things, catNames, itemNames);
			string form = c.form;
			bool allow = form != "group things forbid";
			List<ThingCategoryDef> cats = new List<ThingCategoryDef>();
			List<ThingDef> items = new List<ThingDef>();
			foreach (string n in catNames)
			{
				List<ThingCategoryDef> found = Executor.FindCategoryDefs(n);
				if (found.Count != 1)
				{
					throw new Fault("'" + n + "' could mean several kinds of things; name it more exactly.");
				}
				if (!found[0].DescendantThingDefs.Any(belongs))
				{
					throw new Fault("'" + n + "' has nothing a " + kind + " group can hold.");
				}
				cats.Add(found[0]);
			}
			foreach (string n in itemNames)
			{
				List<ThingDef> found = Executor.FindItemDefs(n);
				if (found.Count != 1)
				{
					throw new Fault("'" + n + "' could mean several items; name it more exactly.");
				}
				if (!belongs(found[0]))
				{
					throw new Fault(UpperFirst(found[0].label) + " can't be in a " + kind + " group.");
				}
				items.Add(found[0]);
			}

			Plan plan = NewPlan(form);
			Write write = new Write();
			write.description = form == "group things only" ? "the " + groupLabel + " allows only " + JoinNames(things)
				: allow ? "the " + groupLabel + " also allows " + JoinNames(things) : "the " + groupLabel + " no longer allows " + JoinNames(things);
			write.apply = delegate
			{
				List<SpecialThingFilterDef> keepSpecial = DefDatabase<SpecialThingFilterDef>.AllDefsListForReading;
				if (form == "group things only")
				{
					filter.SetDisallowAll(null, keepSpecial);
				}
				foreach (ThingCategoryDef cat in cats)
				{
					HashSet<ThingDef> notThese = new HashSet<ThingDef>(cat.DescendantThingDefs.Where((ThingDef d) => !belongs(d)));
					filter.SetAllow(cat, allow, allow ? notThese : null, keepSpecial);
				}
				foreach (ThingDef item in items)
				{
					filter.SetAllow(item, allow);
				}
			};
			plan.writes.Add(write);
			string line = form == "group things only" ? "The " + groupLabel + " allows only " + JoinNames(things) + "."
				: allow ? "The " + groupLabel + " also allows " + JoinNames(things) + "."
				: "The " + groupLabel + " no longer allows " + JoinNames(things) + ".";
			return Reply(plan, line, null, null);
		}
	}
}
