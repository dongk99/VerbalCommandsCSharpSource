using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// A bill an earlier sentence of the same order will add: made now (not yet on the bench), put on the bench
	// by its add write, and changed by later sentences' writes (which run after it).
	public sealed class PlannedBill
	{
		public Building_WorkTable bench;
		public RecipeDef recipe;
		public Bill bill;
	}

	// Brief 23, wave 1: bills (contract 3b) and the older "bills" form. Game members used, all from the
	// decompiled source: Building_WorkTable.billStack (BillStack.Count / MaxCount 15 / AddBill / Delete),
	// ThingDef.AllRecipes, RecipeDef.AvailableNow / AvailableOnNow / MakeNewBill (BillUtility) /
	// WorkerCounter.CanCountProducts / CanPossiblyStore / workSkill / fixedIngredientFilter /
	// PawnSatisfiesSkillRequirements, Bill.suspended / SetPawnRestriction / SetAnyPawnRestriction /
	// allowedSkillRange / ingredientSearchRadius / ingredientFilter / SetStoreMode, Bill_Production.repeatMode /
	// repeatCount / targetCount, BillRepeatModeDefOf, BillStoreModeDefOf, IBillGiver.GetWorkgiver.
	public static partial class FormPlanner
	{
		private static PlanReply DispatchMore(FormContext c)
		{
			// Brief 27: wave 1 forms (FormWave1.cs); "bill repeat" and "bill (un)pause when satisfied" are among them.
			PlanReply wave1 = DispatchWave1(c);
			if (wave1 != null)
			{
				return wave1;
			}
			// User 2026-10-17: "delete {billid}" (no bench or recipe in the sentence).
			if (c.form == "bill delete")
			{
				return PlanBillDelete(c);
			}
			if (c.form.StartsWith("bill "))
			{
				return PlanBillForm(c);
			}
			PlanReply reply = DispatchControls(c);
			if (reply != null)
			{
				return reply;
			}
			return DispatchGroups(c);
		}

		// Recipes the bench's bill menu offers now (ITab_Bills: AvailableNow and AvailableOnNow).
		internal static IEnumerable<RecipeDef> AvailableRecipes(Building_WorkTable bench)
		{
			foreach (RecipeDef recipe in bench.def.AllRecipes)
			{
				if (recipe.AvailableNow && recipe.AvailableOnNow(bench))
				{
					yield return recipe;
				}
			}
		}

		private static string RoomPhrase(Thing thing)
		{
			string text = BuildDefs.RoomText(RoomOfThing(thing));
			return text.Length > 0 ? " in the " + text : "";
		}

		// One bench from BENCH [ROOM]. Several of the same name is refused: a bill is made for one bench.
		private static Building_WorkTable ResolveOneBench(FormContext c)
		{
			string benchLabel = c.Text("BENCH");
			if (benchLabel == null)
			{
				throw new Fault("The order did not say which workbench.");
			}
			string roomLabel = c.Text("ROOM");
			List<Room> rooms = roomLabel != null ? ResolveRooms(c.map, roomLabel) : null;
			List<Building_WorkTable> benches = ResolveBenches(c.map, benchLabel, rooms, roomLabel);
			if (benches.Count > 1)
			{
				List<string> where = benches.Select((Building_WorkTable b) => BuildDefs.RoomText(RoomOfThing(b))).Where((string s) => s.Length > 0).Distinct().ToList();
				throw new Fault("There is more than one " + benchLabel + (where.Count > 0 ? " (in the " + JoinNames(where) + ")" : "") + ". Say which room it is in.");
			}
			return benches[0];
		}

		// Recipes whose label is the text, else whose label holds every word of it. Refuses none and several.
		private static RecipeDef MatchRecipe(IEnumerable<RecipeDef> pool, string text, string benchLabel)
		{
			List<RecipeDef> all = pool.ToList();
			string n = Norm(text);
			List<RecipeDef> found = all.Where((RecipeDef r) => Norm(r.label) == n).ToList();
			if (found.Count == 0)
			{
				string[] words = n.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
				found = all.Where((RecipeDef r) =>
				{
					string label = Norm(r.label);
					return words.Length > 0 && words.All((string w) => ContainsWord(label, w));
				}).ToList();
			}
			if (found.Count == 1)
			{
				return found[0];
			}
			if (found.Count == 0)
			{
				List<string> names = all.Select((RecipeDef r) => r.label).Distinct().Take(12).ToList();
				throw new Fault("The " + benchLabel + " has no recipe called '" + text + "'." + (names.Count > 0 ? " It can make: " + JoinNames(names) + "." : ""));
			}
			throw new Fault("'" + text + "' could mean several recipes on the " + benchLabel + ": " + JoinNames(found.Select((RecipeDef r) => r.label).Distinct()) + ".");
		}

		private static string WhyRecipeNotAvailable(RecipeDef recipe)
		{
			if (recipe.researchPrerequisite != null && !recipe.researchPrerequisite.IsFinished)
			{
				return "it needs the " + recipe.researchPrerequisite.label + " research first";
			}
			if (recipe.researchPrerequisites != null)
			{
				foreach (ResearchProjectDef project in recipe.researchPrerequisites)
				{
					if (!project.IsFinished)
					{
						return "it needs the " + project.label + " research first";
					}
				}
			}
			return "it is not available to the colony yet";
		}

		// The bill an operation means: existing bills on the bench and bills planned earlier in this order.
		private static Bill FindBill(FormContext c, Building_WorkTable bench, string recipeText, string benchLabel, out bool planned)
		{
			List<Bill> existing = bench.billStack.Bills.ToList();
			List<PlannedBill> pending = c.notes.plannedBills.Where((PlannedBill p) => p.bench == bench).ToList();
			List<RecipeDef> recipes = existing.Select((Bill b) => b.recipe).Concat(pending.Select((PlannedBill p) => p.recipe)).Distinct().ToList();
			if (recipes.Count == 0)
			{
				throw new Fault("There are no bills on the " + benchLabel + ".");
			}
			RecipeDef recipe = MatchRecipe(recipes, recipeText, benchLabel);
			List<Bill> bills = existing.Where((Bill b) => b.recipe == recipe).ToList();
			List<PlannedBill> plannedBills = pending.Where((PlannedBill p) => p.recipe == recipe).ToList();
			if (bills.Count + plannedBills.Count > 1)
			{
				throw new Fault("There is more than one bill for " + recipe.label + " on the " + benchLabel + ", so it is not clear which one is meant.");
			}
			planned = bills.Count == 0;
			return planned ? plannedBills[0].bill : bills[0];
		}

		private static string BenchPhrase(FormContext c, Building_WorkTable bench)
		{
			return "the " + c.Text("BENCH") + (c.Has("ROOM") ? " in the " + c.Text("ROOM") : "");
		}

		private static PlanReply PlanBillForm(FormContext c)
		{
			Building_WorkTable bench = ResolveOneBench(c);
			string benchLabel = c.Text("BENCH");
			string recipeText = c.Text("RECIPE");
			if (recipeText == null)
			{
				throw new Fault("The order did not say which recipe.");
			}
			string where = BenchPhrase(c, bench);
			Plan plan = NewPlan(c.form);

			if (c.form.StartsWith("bill add "))
			{
				return PlanBillAdd(c, bench, benchLabel, recipeText, where, plan);
			}

			bool planned;
			Bill bill = FindBill(c, bench, recipeText, benchLabel, out planned);
			string recipeLabel = bill.recipe.label;
			Building_WorkTable capturedBench = bench;
			Bill capturedBill = bill;
			bool capturedPlanned = planned;
			Action check = delegate
			{
				if (!capturedPlanned && (capturedBill.deleted || !capturedBench.billStack.Bills.Contains(capturedBill)))
				{
					throw new Fault("The bill for " + capturedBill.recipe.label + " is gone, so it was not changed.");
				}
			};
			string line;
			Action change;

			switch (c.form)
			{
				case "bill remove":
					if (planned)
					{
						throw new Fault("That bill is only being added in this same order, so it can't be removed too.");
					}
					change = delegate { capturedBench.billStack.Delete(capturedBill); };
					line = "Delete the bill for " + recipeLabel + " on " + where + ".";
					break;
				case "bill pause":
					change = delegate { capturedBill.suspended = true; };
					line = "Pause the bill for " + recipeLabel + " on " + where + ".";
					break;
				case "bill resume":
					change = delegate { capturedBill.suspended = false; };
					line = "Resume the bill for " + recipeLabel + " on " + where + ".";
					break;
				case "bill pawn":
				{
					// User 2026-10-17: "let anyone do bill", "let WHO do bill" and "set skill range" are removed (no
					// sentence takes a bill off its pawn).
					Pawn pawn = ResolveOneBillPawn(c, bench, bill.recipe, plan);
					string red = RedSkillLine(pawn, bench, bill.recipe);
					if (red != null)
					{
						throw new Fault(red);
					}
					change = delegate { capturedBill.SetPawnRestriction(pawn); };
					line = "Only " + pawn.LabelShort + " does the bill for " + recipeLabel + " on " + where + ".";
					break;
				}
				case "bill radius":
				{
					double? n = c.Number("N");
					if (n == null || n < 1)
					{
						throw new Fault("The search radius must be a number of tiles.");
					}
					float radius = (float)n.Value;
					string how = (int)Math.Round(n.Value) + " tiles";
					if (radius < 3f)
					{
						radius = 3f;
						how = "3 tiles";
						plan.notes.Add("The game's smallest ingredient search radius is 3 tiles, so 3 is used.");
					}
					else if (radius >= 100f)
					{
						radius = Bill.MaxIngredientSearchRadius;
						how = "unlimited";
						plan.notes.Add("The game's largest set radius is 100 tiles; 100 or more means no limit, so no limit is used.");
					}
					change = delegate { capturedBill.ingredientSearchRadius = radius; };
					line ="Look for ingredients for " + recipeLabel + " on " + where + (how == "unlimited" ? " with no distance limit." : " within " + how + ".");
					break;
				}
				case "bill store":
					change = PlanBillStore(c, bill, recipeLabel, where, plan, out line);
					break;
				case "bill ingredients only":
				case "bill ingredients allow":
				case "bill ingredients forbid":
					change = PlanBillIngredients(c, bill, recipeLabel, where, plan, out line);
					break;
				default:
					return null;
			}

			Write write = new Write();
			write.description = line.TrimEnd('.');
			write.apply = delegate
			{
				check();
				change();
			};
			plan.writes.Add(write);
			return Reply(plan, line, null, null);
		}

		// Red skills (SkillBlock.cs, 2026-10-16): the recipe's skill, else the bench's work type; 0 to 2 is refused.
		private static string RedSkillLine(Pawn pawn, Building_WorkTable bench, RecipeDef recipe)
		{
			WorkGiverDef billGiver = bench.GetWorkgiver();
			return recipe.workSkill != null ? SkillBlock.Check(pawn, recipe.workSkill)
				: billGiver != null ? SkillBlock.Check(pawn, billGiver.workType) : null;
		}

		// User 2026-10-17: "bills that the pawn CANNOT create will bounce with "{pawnname} cannot create {itemname}. required
		// skill for {itemname} creation is {skillevel_req}"" (itemname: the recipe's name, the user's pick). Null when the
		// pawn meets every skill requirement (RecipeDef.PawnSatisfiesSkillRequirements).
		private static string CannotCreateLine(Pawn pawn, RecipeDef recipe)
		{
			if (recipe.PawnSatisfiesSkillRequirements(pawn))
			{
				return null;
			}
			string needed = string.Join(", ", recipe.skillRequirements.Where((SkillRequirement r) => r.skill != null)
				.Select((SkillRequirement r) => r.skill.LabelCap + " " + r.minLevel).ToArray());
			return pawn.LabelShort + " cannot create " + recipe.label + ". required skill for " + recipe.label + " creation is " + needed;
		}

		// The pawn for "bill pawn": one colonist who can do the bench's work (the dialog lists the same).
		private static Pawn ResolveOneBillPawn(FormContext c, Building_WorkTable bench, RecipeDef recipe, Plan plan)
		{
			Pawn pawn = ResolveOneBillPawnByKey(c, "PAWN");
			WorkGiverDef giver = bench.GetWorkgiver();
			if (giver != null)
			{
				if (pawn.WorkTypeIsDisabled(giver.workType))
				{
					throw new Fault(pawn.LabelShort + " will never do " + giver.workType.gerundLabel + ", so can't be given this bill.");
				}
				if (!pawn.workSettings.WorkIsActive(giver.workType))
				{
					plan.notes.Add(pawn.LabelShort + " is not assigned to " + giver.workType.gerundLabel + ", so the bill waits until they are.");
				}
			}
			return pawn;
		}

		private static Pawn ResolveOneBillPawnByKey(FormContext c, string key)
		{
			List<Pawn> pawns = ResolvePawns(c, key);
			if (pawns.Count > 1)
			{
				throw new Fault("The game lets a bill belong to only one pawn, but the order named " + pawns.Count + ".");
			}
			return pawns[0];
		}

		private static PlanReply PlanBillAdd(FormContext c, Building_WorkTable bench, string benchLabel, string recipeText, string where, Plan plan)
		{
			RecipeDef recipe = MatchRecipe(bench.def.AllRecipes, recipeText, benchLabel);
			if (!recipe.AvailableNow || !recipe.AvailableOnNow(bench))
			{
				throw new Fault("The " + benchLabel + " can't make " + recipe.label + " yet: " + WhyRecipeNotAvailable(recipe) + ".");
			}
			int already = bench.billStack.Count + c.notes.plannedBills.Count((PlannedBill p) => p.bench == bench);
			if (already >= BillStack.MaxCount)
			{
				throw new Fault("The " + benchLabel + " already has " + BillStack.MaxCount + " bills, the most the game allows.");
			}

			Bill made = recipe.MakeNewBill();
			Bill_Production production = made as Bill_Production;
			string line;
			switch (c.form)
			{
				case "bill add count":
				{
					double? count = c.Number("COUNT");
					RequireProduction(production, recipe);
					if (count == null || count < 1 || count != Math.Floor(count.Value))
					{
						throw new Fault("The number of times must be a whole number, 1 or more.");
					}
					production.repeatMode = BillRepeatModeDefOf.RepeatCount;
					production.repeatCount = (int)count.Value;
					line = "Add a bill on " + where + ": make " + recipe.label + " " + (int)count.Value + (count.Value == 1 ? " time." : " times.");
					break;
				}
				case "bill add until":
				{
					double? count = c.Number("COUNT");
					RequireProduction(production, recipe);
					if (count == null || count < 1 || count != Math.Floor(count.Value))
					{
						throw new Fault("The number to have must be a whole number, 1 or more.");
					}
					production.repeatMode = BillRepeatModeDefOf.TargetCount;
					production.targetCount = (int)count.Value;
					production.unpauseWhenYouHave = Math.Max(0, production.targetCount - 1);
					if (!recipe.WorkerCounter.CanCountProducts(production))
					{
						throw new Fault("The game can't count what " + recipe.label + " makes, so a bill can't make it until you have some number of it.");
					}
					line = "Add a bill on " + where + ": make " + recipe.label + " until you have " + (int)count.Value + ".";
					break;
				}
				case "bill add forever":
					RequireProduction(production, recipe);
					production.repeatMode = BillRepeatModeDefOf.Forever;
					line = "Add a bill on " + where + ": make " + recipe.label + " forever.";
					break;
				default:
					return null;
			}

			if (!bench.Map.mapPawns.FreeColonists.Any((Pawn p) => recipe.PawnSatisfiesSkillRequirements(p)))
			{
				plan.notes.Add("No colonist has the skill to make " + recipe.label + ", so nothing will happen until one does.");
			}
			// User 2026-10-17: every new bill belongs to the pawn named with "using PAWN".
			Pawn worker = ResolveOneBillPawn(c, bench, recipe, plan);
			string cannot = CannotCreateLine(worker, recipe);
			if (cannot != null)
			{
				throw new Fault(cannot);
			}
			string red = RedSkillLine(worker, bench, recipe);
			if (red != null)
			{
				throw new Fault(red);
			}
			line = line.TrimEnd('.') + ", only " + worker.LabelShort + ".";
			PlannedBill pending = new PlannedBill();
			pending.bench = bench;
			pending.recipe = recipe;
			pending.bill = made;
			c.notes.plannedBills.Add(pending);

			Write write = new Write();
			write.description = line.TrimEnd('.');
			Building_WorkTable capturedBench = bench;
			write.apply = delegate
			{
				if (capturedBench.billStack.Count >= BillStack.MaxCount)
				{
					throw new Fault("The " + capturedBench.def.label + " already has as many bills as the game allows.");
				}
				made.SetPawnRestriction(worker);
				capturedBench.billStack.AddBill(made);
			};
			plan.writes.Add(write);
			return Reply(plan, line, null, null);
		}

		private static void RequireProduction(Bill_Production production, RecipeDef recipe)
		{
			if (production == null)
			{
				throw new Fault("A bill for " + recipe.label + " is not an ordinary production bill, so it can't be set up by number yet.");
			}
		}

		// "floor", "best storage" or a storage PLACE.
		private static Action PlanBillStore(FormContext c, Bill bill, string recipeLabel, string where, Plan plan, out string line)
		{
			string whereText = c.Text("WHERE") ?? "";
			string n = Norm(whereText);
			Bill capturedBill = bill;
			if (n == "floor" || n == "the floor" || n == "drop on floor" || n == "drop on the floor")
			{
				line = "Drop what " + recipeLabel + " makes on the floor (bill on " + where + ").";
				return delegate { capturedBill.SetStoreMode(BillStoreModeDefOf.DropOnFloor); };
			}
			if (n == "best storage" || n == "best stockpile" || n == "best")
			{
				line = "Store what " + recipeLabel + " makes in the best storage (bill on " + where + ").";
				return delegate { capturedBill.SetStoreMode(BillStoreModeDefOf.BestStockpile); };
			}

			FormContext placeContext = PlaceContext(c, whereText);
			List<StorageUnit> units = ResolveStorageUnits(placeContext);
			if (units.Count > 1)
			{
				throw new Fault("'" + whereText + "' means " + units.Count + " different storage places. Name one.");
			}
			StorageUnit unit = units[0];
			ISlotGroup group = SlotGroupOf(unit);
			if (group == null)
			{
				throw new Fault("That storage can't be a bill's output.");
			}
			Bill_Production production = bill as Bill_Production;
			if (production != null && !bill.recipe.WorkerCounter.CanPossiblyStore(production, group))
			{
				throw new Fault("What " + recipeLabel + " makes can't be kept in that storage.");
			}
			line = "Store what " + recipeLabel + " makes in " + whereText + " (bill on " + where + ").";
			return delegate { capturedBill.SetStoreMode(BillStoreModeDefOf.SpecificStockpile, group); };
		}

		// The slot group a bill can store into: the linked group, the zone's, or the building's own.
		private static ISlotGroup SlotGroupOf(StorageUnit unit)
		{
			if (unit.group != null)
			{
				return unit.group;
			}
			if (unit.zone != null)
			{
				return unit.zone.slotGroup;
			}
			if (unit.buildings.Count > 0)
			{
				ISlotGroupParent parent = unit.buildings[0] as ISlotGroupParent;
				return parent != null ? parent.GetSlotGroup() : null;
			}
			return null;
		}

		// A storage PLACE written out ("storage near electric stove in kitchen", "storage in storeroom", "storage
		// north of door to kitchen in storeroom") turned back into slots, so the storage forms' lookup is reused.
		private static FormContext PlaceContext(FormContext c, string place)
		{
			PlanRequest request = new PlanRequest();
			request.form = "storage place";
			request.index = c.req.index;
			request.runState = c.req.runState;
			string text = Norm(place);
			System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(text,
				"^storage (?:(\\d+) tiles? )?([a-z]+(?: ?[a-z]+)?) of door to (.+?)(?: in (.+))?$");
			if (m.Success)
			{
				if (m.Groups[1].Success)
				{
					request.slots["DISTANCE"] = m.Groups[1].Value;
				}
				request.slots["DIRECTION"] = m.Groups[2].Value.Replace(" ", "");
				request.slots["DOOR"] = m.Groups[3].Value;
				if (m.Groups[4].Success)
				{
					request.slots["ROOM"] = m.Groups[4].Value;
				}
			}
			else
			{
				m = System.Text.RegularExpressions.Regex.Match(text, "^storage(?: near (.+?))?(?: in (.+))?$");
				if (!m.Success || (!m.Groups[1].Success && !m.Groups[2].Success))
				{
					throw new Fault("I can't tell which storage '" + place + "' means.");
				}
				if (m.Groups[1].Success)
				{
					request.slots["BENCH"] = m.Groups[1].Value;
				}
				if (m.Groups[2].Success)
				{
					request.slots["ROOM"] = m.Groups[2].Value;
				}
			}
			return new FormContext(c.map, request);
		}

		private static Action PlanBillIngredients(FormContext c, Bill bill, string recipeLabel, string where, Plan plan, out string line)
		{
			List<string> things = c.TextList("THINGS");
			if (things.Count == 0)
			{
				throw new Fault("The order did not say which ingredients.");
			}
			ThingFilter allowedByRecipe = bill.recipe.fixedIngredientFilter;
			// Special filters by name (user, 2026-10-15: a screenshot of the butcher bill's "Allow stranger corpses" / "Allow
			// slave corpses" and "Pretty sure it neds to have "butcher stranger" on to chop enemy faction corpses. read
			// decompiled dll to make sure that is a thing"). It is: AllowCorpsesStranger is off by default (Core
			// SpecialThingFilters.xml:15-22, allowedByDefault false), matches every humanlike corpse not of the player's
			// faction (SpecialThingFilterWorker_CorpsesStranger.cs:7-22), and a disallowed special filter rejects a matching
			// thing even when its def is allowed (ThingFilter.Allows, Verse\ThingFilter.cs:898-900). The apply below keeps
			// every special filter as it was, so "allow human corpses" alone never let a raider's body be butchered. Now a
			// name that is a special filter ("stranger corpses", "allow slave corpses", Executor.FindSpecialFilterDefs, as the
			// storage orders) switches it, if its category holds something the recipe can use.
			List<SpecialThingFilterDef> specials = new List<SpecialThingFilterDef>();
			List<string> plainThings = new List<string>();
			foreach (string name in things)
			{
				List<SpecialThingFilterDef> sf = Executor.FindSpecialFilterDefs(name);
				if (sf.Count == 1 && sf[0].configurable && sf[0].parentCategory != null
					&& sf[0].parentCategory.DescendantThingDefs.Any((ThingDef d) => allowedByRecipe.Allows(d)))
				{
					specials.Add(sf[0]);
				}
				else
				{
					plainThings.Add(name);
				}
			}
			List<string> catNames = new List<string>();
			List<string> itemNames = new List<string>();
			ClassifyThings(plainThings, catNames, itemNames);
			List<ThingCategoryDef> cats = new List<ThingCategoryDef>();
			List<ThingDef> items = new List<ThingDef>();
			foreach (string name in catNames)
			{
				List<ThingCategoryDef> found = Executor.FindCategoryDefs(name);
				if (found.Count != 1)
				{
					throw new Fault("'" + name + "' could mean several kinds of things; name it more exactly.");
				}
				if (!found[0].DescendantThingDefs.Any((ThingDef d) => allowedByRecipe.Allows(d)))
				{
					throw new Fault("Nothing in '" + name + "' can be used for " + recipeLabel + ".");
				}
				cats.Add(found[0]);
			}
			foreach (string name in itemNames)
			{
				List<ThingDef> found = Executor.FindItemDefs(name);
				if (found.Count != 1)
				{
					throw new Fault("'" + name + "' could mean several items; name it more exactly.");
				}
				if (c.form != "bill ingredients forbid" && !allowedByRecipe.Allows(found[0]))
				{
					throw new Fault(UpperFirst(found[0].label) + " can't be used for " + recipeLabel + ".");
				}
				items.Add(found[0]);
			}
			string form = c.form;
			Bill capturedBill = bill;
			string list = JoinNames(things);
			// Human corpses allowed while "allow stranger corpses" stays off: say so, with the order that switches it.
			ThingCategoryDef humanCorpses = DefDatabase<ThingCategoryDef>.GetNamedSilentFail("CorpsesHumanlike");
			SpecialThingFilterDef strangers = DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail("AllowCorpsesStranger");
			if (form != "bill ingredients forbid" && humanCorpses != null && strangers != null && !specials.Contains(strangers)
				&& (cats.Any((ThingCategoryDef cd) => cd == humanCorpses || humanCorpses.ThisAndChildCategoryDefs.Contains(cd) || cd.ThisAndChildCategoryDefs.Contains(humanCorpses))
					|| items.Any((ThingDef d) => d.thingCategories != null && d.thingCategories.Contains(humanCorpses)))
				&& (form == "bill ingredients only" || !bill.ingredientFilter.Allows(strangers)))
			{
				plan.notes.Add("Stranger corpses (raiders, prisoners, visitors: anyone not of your faction) stay off on this bill: the game's \"allow stranger corpses\""
					+ " is a separate switch, off by default. To use them too, add 'allow stranger corpses in bill for " + recipeLabel + " at BENCH.'");
			}
			switch (form)
			{
				case "bill ingredients only":
					line = "Use only " + list + " for " + recipeLabel + " (bill on " + where + ").";
					break;
				case "bill ingredients allow":
					line = "Also allow " + list + " for " + recipeLabel + " (bill on " + where + ").";
					break;
				default:
					line = "Don't use " + list + " for " + recipeLabel + " (bill on " + where + ").";
					break;
			}
			return delegate
			{
				ThingFilter filter = capturedBill.ingredientFilter;
				List<SpecialThingFilterDef> keepSpecial = DefDatabase<SpecialThingFilterDef>.AllDefsListForReading;
				if (form == "bill ingredients only")
				{
					filter.SetDisallowAll(null, keepSpecial);
				}
				bool allow = form != "bill ingredients forbid";
				foreach (ThingCategoryDef cat in cats)
				{
					HashSet<ThingDef> notUsable = new HashSet<ThingDef>();
					if (allow)
					{
						foreach (ThingDef d in cat.DescendantThingDefs)
						{
							if (!allowedByRecipe.Allows(d))
							{
								notUsable.Add(d);
							}
						}
					}
					filter.SetAllow(cat, allow, allow ? notUsable : null, keepSpecial);
				}
				foreach (ThingDef item in items)
				{
					filter.SetAllow(item, allow);
				}
				// The named special filters (ThingFilter.SetAllow(SpecialThingFilterDef, bool), Verse\ThingFilter.cs:694-716).
				foreach (SpecialThingFilterDef sf in specials)
				{
					filter.SetAllow(sf, allow);
				}
			};
		}

		// User 2026-10-17: a bench in the bill lists: "art bench in workshop", or "art bench at 116, 120" when its room has no
		// name (the user's pick).
		private static string BillBenchPlace(Building_WorkTable bench)
		{
			string room = BuildDefs.RoomText(RoomOfThing(bench));
			return bench.def.label + (room.Length > 0 ? " in " + room
				: " at " + bench.Position.x.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", " + bench.Position.z.ToString(System.Globalization.CultureInfo.InvariantCulture));
		}

		// User 2026-10-17: "list bill {pawnname} has": the bills only that pawn does, with the game's ID
		// (Bill.GetUniqueLoadID, "Bill_" + recipe defName + "_" + loadID).
		internal static QueryReply QueryPawnBills(FormContext c)
		{
			Pawn pawn = ResolveOneBillPawnByKey(c, "PAWN");
			List<string> lines = new List<string>();
			foreach (Building_WorkTable bench in StorageIndex.AllPlayerBenches(c.map))
			{
				if (bench.billStack == null)
				{
					continue;
				}
				foreach (Bill bill in bench.billStack.Bills)
				{
					if (bill.PawnRestriction != pawn)
					{
						continue;
					}
					Bill_Production production = bill as Bill_Production;
					lines.Add(bill.GetUniqueLoadID() + ": " + bill.Label + (production != null ? " - " + RbBillRepeat(production) : "") + " (" + BillBenchPlace(bench) + ")");
				}
			}
			if (lines.Count == 0)
			{
				return QueryReply.Ok(pawn.LabelShort + " has no bills.");
			}
			return QueryReply.Ok(string.Join("\n", lines.ToArray()));
		}

		// User 2026-10-17: "have play session just print out .md and update on its own on what bill pawns have" / "all it
		// needs to do is scan the game itself and just print out list of bills colonist has without garbage syntax". Every
		// game hour (AgentHourlyCheck.Tick), <SaveData>\VerbalCommands\bills.md, in the user's pick:
		//   Zimmerman
		//   - make small sculpture, 1 more time, art bench in workshop, Bill_Make_SculptureSmall_37
		//   Hicklin
		//   - no bills
		// Written only when the text changed; the skill's bills_hook.ps1 shows it to the orchestrator.
		private static string lastBillsFile;

		internal static void WriteBillsFile()
		{
			try
			{
				List<string> lines = new List<string>();
				foreach (Map map in Find.Maps.Where((Map m) => m != null && m.IsPlayerHome))
				{
					List<Building_WorkTable> benches = StorageIndex.AllPlayerBenches(map);
					foreach (Pawn pawn in map.mapPawns.FreeColonists)
					{
						lines.Add(pawn.LabelShort);
						int count = 0;
						foreach (Building_WorkTable bench in benches)
						{
							if (bench.billStack == null)
							{
								continue;
							}
							foreach (Bill bill in bench.billStack.Bills)
							{
								if (bill.PawnRestriction != pawn)
								{
									continue;
								}
								string repeat = BillsFileRepeat(bill as Bill_Production);
								lines.Add("- " + bill.Label + (repeat != null ? ", " + repeat : "") + ", " + BillBenchPlace(bench) + ", " + bill.GetUniqueLoadID());
								count++;
							}
						}
						if (count == 0)
						{
							lines.Add("- no bills");
						}
					}
				}
				string text = string.Join("\n", lines.ToArray()) + "\n";
				if (text == lastBillsFile)
				{
					return;
				}
				string root = System.IO.Path.GetDirectoryName(AgentInbox.StatePath);
				if (string.IsNullOrEmpty(root))
				{
					return;
				}
				System.IO.File.WriteAllText(System.IO.Path.Combine(root, "bills.md"), text);
				lastBillsFile = text;
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] bills.md: writing failed: " + ex.Message);
			}
		}

		// "1 more time", "forever", "until you have 10"; null for a bill that is not a production bill.
		private static string BillsFileRepeat(Bill_Production bill)
		{
			if (bill == null)
			{
				return null;
			}
			if (bill.repeatMode == BillRepeatModeDefOf.Forever)
			{
				return "forever";
			}
			if (bill.repeatMode == BillRepeatModeDefOf.TargetCount)
			{
				return "until you have " + bill.targetCount;
			}
			return bill.repeatCount + " more " + (bill.repeatCount == 1 ? "time" : "times");
		}

		// User 2026-10-17: "What can {pawnname} create" (based on current assigned jobs): every bench whose work the pawn is
		// assigned to, the recipes it offers now that the pawn's skills meet (red skills listed too, the user's pick).
		internal static QueryReply QueryPawnCreate(FormContext c)
		{
			Pawn pawn = ResolveOneBillPawnByKey(c, "PAWN");
			List<string> lines = new List<string>();
			// The user's words, verbatim; first (the user's pick).
			lines.Add("This only includes work that pawn is assigned to, so someone not assigned to tailoring will not show bills the pawn can make on a bench, even if it has skill requirement for it.");
			foreach (Building_WorkTable bench in StorageIndex.AllPlayerBenches(c.map))
			{
				WorkGiverDef giver = bench.GetWorkgiver();
				if (giver == null || pawn.workSettings == null || pawn.WorkTypeIsDisabled(giver.workType) || !pawn.workSettings.WorkIsActive(giver.workType))
				{
					continue;
				}
				List<string> recipes = AvailableRecipes(bench).Where((RecipeDef r) => r.PawnSatisfiesSkillRequirements(pawn))
					.Select((RecipeDef r) => r.label).Distinct().ToList();
				if (recipes.Count == 0)
				{
					continue;
				}
				string line = BillBenchPlace(bench) + ": " + string.Join(", ", recipes.ToArray());
				if (!lines.Contains(line))
				{
					lines.Add(line);
				}
			}
			if (lines.Count == 1)
			{
				lines.Add(pawn.LabelShort + " can't create anything here.");
			}
			return QueryReply.Ok(string.Join("\n", lines.ToArray()));
		}

		// User 2026-10-17: "delete {billid}": the bill with that game ID (as "list bill PAWN has" gives it), on any bench.
		private static PlanReply PlanBillDelete(FormContext c)
		{
			string id = (c.Text("BILLID") ?? "").Trim();
			Building_WorkTable bench = null;
			Bill bill = null;
			foreach (Building_WorkTable b in StorageIndex.AllPlayerBenches(c.map))
			{
				if (b.billStack == null)
				{
					continue;
				}
				bill = b.billStack.Bills.FirstOrDefault((Bill x) => string.Equals(x.GetUniqueLoadID(), id, StringComparison.OrdinalIgnoreCase));
				if (bill != null)
				{
					bench = b;
					break;
				}
			}
			if (bill == null)
			{
				throw new Fault("No bill has the ID " + id + ".");
			}
			Plan plan = NewPlan(c.form);
			string line = "Delete the bill for " + bill.recipe.label + " on the " + bench.def.label + RoomPhrase(bench) + ".";
			Building_WorkTable capturedBench = bench;
			Bill capturedBill = bill;
			Write write = new Write();
			write.description = line.TrimEnd('.');
			write.apply = delegate
			{
				if (capturedBill.deleted || !capturedBench.billStack.Bills.Contains(capturedBill))
				{
					throw new Fault("The bill for " + capturedBill.recipe.label + " is gone, so it was not changed.");
				}
				capturedBench.billStack.Delete(capturedBill);
			};
			plan.writes.Add(write);
			return Reply(plan, line, null, null);
		}

		// The older form "let only PAWNS do bills on BENCH": every bill on that bench belongs to that one pawn.
		private static PlanReply PlanBillsOnlyPawn(FormContext c)
		{
			string benchLabel = c.Text("BENCH");
			List<Building_WorkTable> benches = ResolveBenches(c.map, benchLabel, null, null);
			Pawn pawn = ResolveOneBillPawnByKey(c, "PAWNS");
			Plan plan = NewPlan("bills");
			int billCount = 0;
			foreach (Building_WorkTable bench in benches)
			{
				WorkGiverDef giver = bench.GetWorkgiver();
				if (giver != null && pawn.WorkTypeIsDisabled(giver.workType))
				{
					throw new Fault(pawn.LabelShort + " will never do " + giver.workType.gerundLabel + ", so can't be given the bills on the " + benchLabel + ".");
				}
				// Red skills (SkillBlock.cs, 2026-10-16): the bench's work type, skill 0 to 2 is refused.
				string red = giver != null ? SkillBlock.Check(pawn, giver.workType) : null;
				if (red != null)
				{
					throw new Fault(red);
				}
				List<Bill> bills = bench.billStack.Bills.ToList();
				bills.AddRange(c.notes.plannedBills.Where((PlannedBill p) => p.bench == bench).Select((PlannedBill p) => p.bill));
				foreach (Bill bill in bills)
				{
					billCount++;
					Write write = new Write();
					write.description = pawn.LabelShort + " does the bill for " + bill.recipe.label;
					Bill capturedBill = bill;
					Pawn capturedPawn = pawn;
					write.apply = delegate { capturedBill.SetPawnRestriction(capturedPawn); };
					plan.writes.Add(write);
				}
			}
			if (billCount == 0)
			{
				throw new Fault("There are no bills on the " + benchLabel + ".");
			}
			if (benches.Count > 1)
			{
				plan.notes.Add("There are " + benches.Count + " of " + benchLabel + "; the bills on all of them are changed.");
			}
			return Reply(plan, "Only " + pawn.LabelShort + " does bills on the " + benchLabel + " (" + billCount + (billCount == 1 ? " bill)." : " bills)."), new Pawn[] { pawn }, null);
		}
	}
}
