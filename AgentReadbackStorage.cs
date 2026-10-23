using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Agent 05 brief B: read-back questions about bills and storage (read-only; MAIN THREAD ONLY like every query).
	//   query bills     every bill on a bench, in the bench's order (that order is the bill priority).
	//                   Slots: BENCH, ROOM (optional), SPOT (optional: picks the bench at that spot).
	//   query storage   each storage a PLACE (BENCH / ROOM / DOOR / DIRECTION / DISTANCE) or a ZONE names:
	//                   priority, what it allows in short, what is in it.
	// Game facts (decompiled source, sources\RimWorldDecompiled\):
	//   bills of a bench      Building_WorkTable.billStack (FormBills.cs:134), BillStack.Bills (RimWorld\BillStack.cs:23),
	//                         at most BillStack.MaxCount = 15 (:15); the list order is the order the bench works them in.
	//   recipe label          Bill.Label (RimWorld\Bill.cs:75-85); custom name Bill_Production.RenamableLabel / BaseLabel
	//                         (RimWorld\Bill_Production.cs:86, 98)
	//   repeat mode           Bill_Production.repeatMode (:12), repeatCount (:14), targetCount (:22); the modes are the three
	//                         BillRepeatModeDefOf entries (RimWorld\BillRepeatModeDefOf.cs:7-11). repeatCount is the number of
	//                         runs LEFT: Notify_IterationCompleted counts it down (Bill_Production.cs:245-258, :251), the game keeps no
	//                         "done" number, so the answer says "N more times".
	//   "you have N"          the bill window's number: recipe.WorkerCounter.CountProducts(bill) (RimWorld\Dialog_BillConfig.cs:139-141,
	//                         Verse\RecipeWorkerCounter.cs:17-79); reads the resource counter, the lister and the pawns' hands, writes
	//                         nothing. A target of 999999 or more is shown as "infinite" (Dialog_BillConfig.cs:142)
	//   pause when satisfied  pauseWhenSatisfied (Bill_Production.cs:24), unpauseWhenYouHave (:26): the bill restarts when the count is
	//                         at or below it (ShouldDoNow, :207-243). Window text: Dialog_BillConfig.cs:202-205
	//   paused                Bill_Production.paused (:40), set by ShouldDoNow (:226-238) only in target-count mode (the same method
	//                         clears it in the other modes, :209-212), so it is read only in that mode. The window's "Suspended"
	//                         button is Bill.suspended (Bill.cs:29, Dialog_BillConfig.cs:261-272)
	//   who may do it         Bill.PawnRestriction (Bill.cs:127), SlavesOnly (:129), MechsOnly (:131), NonMechsOnly (:133): the fields
	//                         Bill.SetPawnRestriction / SetAnySlaveRestriction / SetAnyMechRestriction / SetAnyNonMechRestriction write
	//                         (:165-203); the window shows the slave / mech words only while Ideology / Biotech is active
	//                         (Dialog_BillConfig.cs:249)
	//   skill range           Bill.allowedSkillRange (Bill.cs:35), shown only for a recipe with a work skill, no named pawn and not
	//                         mechs only (Dialog_BillConfig.cs:250-254); default 0-20 (Bill.cs:35)
	//   ingredient radius     Bill.ingredientSearchRadius (Bill.cs:33); 999 = "Unlimited" (Bill.cs:49, Dialog_BillConfig.cs:513)
	//   where products go     Bill.GetStoreMode() / GetSlotGroup() (Bill.cs:442-450, Bill_Production.cs:177-185); the modes are the
	//                         BillStoreModeDefOf entries (RimWorld\BillStoreModeDefOf.cs:7-11); the label of a chosen storage is
	//                         SlotGroup.GetGroupLabel (RimWorld\SlotGroup.cs:143-154, Dialog_BillConfig.cs:217); "can't hold them" is
	//                         RecipeWorkerCounter.CanPossiblyStore (Verse\RecipeWorkerCounter.cs:157-164, Dialog_BillConfig.cs:218-222)
	//   ingredients           Bill.ingredientFilter starts as a copy of recipe.defaultIngredientFilter (Bill.cs:151-158) and is bounded
	//                         by recipe.fixedIngredientFilter (Verse\RecipeDef.cs:33-35; Bill.ExposeData, Bill.cs:221-231). The window
	//                         shows the ingredient pane only when some ingredient is not fixed (Dialog_BillConfig.cs:486-496,
	//                         IngredientCount.IsFixedIngredient, Verse\IngredientCount.cs:14)
	//   filter tree           ThingCategoryDefOf.Root (RimWorld\ThingCategoryDefOf.cs:12) -> ThingCategoryDef.childCategories
	//                         (Verse\ThingCategoryDef.cs:20), DescendantThingDefs (:70-80), childThingDefs; ThingFilter.Allows(ThingDef)
	//                         (Verse\ThingFilter.cs:908). The tree is not read through ThingFilter.DisplayRootCategory: that getter
	//                         recalculates and caches (Verse\ThingFilter.cs:239-262, 549-596)
	//   storage settings      StorageUnit.Settings / ParentSettings (StorageIndex.cs:1559-1599); StorageSettings.Priority (RimWorld\
	//                         StorageSettings.cs:23-28, a plain read), filter (:12); the tab's label StoragePriority.Label
	//                         (RimWorld\StoragePriorityHelper.cs:7-18, ITab_Storage.cs:114); the tab bounds the filter by
	//                         GetParentStoreSettings().filter (ITab_Storage.cs:132-135); a stockpile's parent is
	//                         StorageSettings.EverStorableFixedSettings (RimWorld\Zone_Stockpile.cs:112-115)
	//   what is stored        SlotGroup.HeldThings (RimWorld\SlotGroup.cs:18-36: the storable things lying on the cells, a read of
	//                         the thing grid), Zone_Stockpile.slotGroup (RimWorld\Zone_Stockpile.cs:10), ISlotGroupParent.GetSlotGroup()
	//                         for buildings, IThingHolder.GetDirectlyHeldThings (Verse\IThingHolder.cs:11) for container storage;
	//                         minified things count as their inner thing (the same as StorageUnit.CountContents, StorageIndex.cs:1634-1648)
	//   fog                   FogGrid.IsFogged (Verse\FogGrid.cs:71): fogged cells and things are skipped
	//   zone name             Zone.label / Zone.RenamableLabel (Verse\Zone.cs:18, 40-47)
	public static partial class FormPlanner
	{
		// A reply never runs past this many lines (rule 6).
		private const int RbStoreMaxLines = 60;
		private const int RbBillMaxBenches = 5;
		private const int RbStoreMaxUnits = 10;
		private const int RbStoreTopHeld = 8;
		// About this many names in a filter summary, and at most this many in its "not:" line.
		private const int RbStoreNameBudget = 12;
		private const int RbStoreNotCap = 6;
		// The bill window shows this radius as "Unlimited" (Bill.cs:33, Dialog_BillConfig.cs:513).
		private const float RbBillUnlimitedRadius = 999f;
		// Dialog_BillConfig.cs:142
		private const int RbBillInfiniteTarget = 999999;

		internal static QueryReply QueryReadbackStorage(FormContext c)
		{
			switch (c.form)
			{
				case "query bills":
					return QueryReply.Ok(RbBillQuery(c));
				case "query storage":
					return QueryReply.Ok(RbStoreQuery(c));
				case "query stores":
					return QueryReply.Ok(RbStoresQuery(c));
			}
			return null;
		}

		// ---- shared -----------------------------------------------------------------------------------------------------

		private static string RbStoreCut(List<string> lines, string howToAskLess)
		{
			if (lines.Count > RbStoreMaxLines)
			{
				int hidden = lines.Count - (RbStoreMaxLines - 1);
				lines = lines.Take(RbStoreMaxLines - 1).ToList();
				lines.Add("... " + hidden + " more lines; " + howToAskLess);
			}
			return string.Join("\n", lines.ToArray());
		}

		private static string RbStoreName(ThingDef def)
		{
			return def.label.NullOrEmpty() ? def.defName : def.label;
		}

		// ---- the filter summary (bills and storage) --------------------------------------------------------------------

		private sealed class RbStoreCatState
		{
			public ThingCategoryDef cat;
			public int total;
			public int allowed;
		}

		// How many of the defs under a category the parent lets through (total) and the filter allows (allowed).
		private static RbStoreCatState RbStoreState(ThingCategoryDef cat, ThingFilter filter, ThingFilter parent)
		{
			RbStoreCatState s = new RbStoreCatState();
			s.cat = cat;
			HashSet<ThingDef> seen = new HashSet<ThingDef>();
			foreach (ThingDef def in cat.DescendantThingDefs)
			{
				if (!seen.Add(def))
				{
					continue;
				}
				if (parent != null && !parent.Allows(def))
				{
					continue;
				}
				s.total++;
				if (filter.Allows(def))
				{
					s.allowed++;
				}
			}
			return s;
		}

		// What is inside a category that is only partly allowed: one level down, the allowed sub-categories and loose things,
		// or "all but ..." when the few that are not allowed are shorter to say. Uses up the name budget.
		private static string RbStoreDetail(ThingCategoryDef cat, ThingFilter filter, ThingFilter parent, ref int budget)
		{
			List<string> yes = new List<string>();
			List<string> no = new List<string>();
			foreach (ThingCategoryDef child in cat.childCategories)
			{
				RbStoreCatState st = RbStoreState(child, filter, parent);
				if (st.total == 0)
				{
					continue;
				}
				(st.allowed > 0 ? yes : no).Add(UpperFirst(child.label));
			}
			foreach (ThingDef def in cat.childThingDefs)
			{
				if (parent != null && !parent.Allows(def))
				{
					continue;
				}
				(filter.Allows(def) ? yes : no).Add(RbStoreName(def));
			}
			if (budget <= 0 || yes.Count == 0)
			{
				return "";
			}
			bool allBut = no.Count > 0 && no.Count < yes.Count && no.Count <= 3;
			List<string> shown = allBut ? no : yes;
			int take = Math.Min(shown.Count, budget);
			budget -= take;
			return (allBut ? "all but " : "some: ") + string.Join(", ", shown.Take(take).ToArray())
				+ (take < shown.Count ? " and " + (shown.Count - take) + " more" : "");
		}

		// A ThingFilter in short, as the storage tab / bill window shows its tree: each top category as all, some or none,
		// one level down only into the "some" ones. At most two lines ("allows: ..." and "not: ..."), about 12 names. The
		// parent bounds what counts (bill: recipe.fixedIngredientFilter; storage: the settings' parent filter); null = no bound.
		private static List<string> RbStoreFilterSummary(ThingFilter filter, ThingFilter parent)
		{
			List<string> lines = new List<string>();
			ThingCategoryDef root = ThingCategoryDefOf.Root;
			if (filter == null || root == null)
			{
				lines.Add("allows: (not known)");
				return lines;
			}
			List<RbStoreCatState> top = root.childCategories
				.Select((ThingCategoryDef cat) => RbStoreState(cat, filter, parent))
				.Where((RbStoreCatState s) => s.total > 0)
				.ToList();
			List<RbStoreCatState> allowedTop = top.Where((RbStoreCatState s) => s.allowed > 0).ToList();
			List<RbStoreCatState> noneTop = top.Where((RbStoreCatState s) => s.allowed == 0).ToList();
			if (allowedTop.Count == 0)
			{
				lines.Add("allows: nothing");
				return lines;
			}
			if (noneTop.Count == 0 && allowedTop.All((RbStoreCatState s) => s.allowed == s.total))
			{
				lines.Add("allows: everything");
				return lines;
			}
			int budget = RbStoreNameBudget - allowedTop.Count - Math.Min(noneTop.Count, RbStoreNotCap);
			List<string> allowParts = new List<string>();
			foreach (RbStoreCatState s in allowedTop)
			{
				string name = UpperFirst(s.cat.label);
				if (s.allowed == s.total)
				{
					allowParts.Add(name);
					continue;
				}
				string detail = RbStoreDetail(s.cat, filter, parent, ref budget);
				allowParts.Add(detail.Length > 0 ? name + " (" + detail + ")" : name + " (some)");
			}
			lines.Add("allows: " + string.Join(", ", allowParts.ToArray()));
			if (noneTop.Count > 0)
			{
				string notText = string.Join(", ", noneTop.Take(RbStoreNotCap).Select((RbStoreCatState s) => UpperFirst(s.cat.label)).ToArray());
				if (noneTop.Count > RbStoreNotCap)
				{
					notText += " and " + (noneTop.Count - RbStoreNotCap) + " more";
				}
				lines.Add("not: " + notText);
			}
			return lines;
		}

		// ---- query bills -----------------------------------------------------------------------------------------------

		private static string RbBillQuery(FormContext c)
		{
			Map map = c.map;
			string benchLabel = c.Text("BENCH");
			if (benchLabel == null)
			{
				throw new Fault("The question did not say which workbench.");
			}
			string roomLabel = c.Text("ROOM");
			List<Room> rooms = roomLabel != null ? ResolveRooms(map, roomLabel) : null;
			List<Building_WorkTable> benches = ResolveBenches(map, benchLabel, rooms, roomLabel)
				.Where((Building_WorkTable b) => !map.fogGrid.IsFogged(b.Position))
				.ToList();
			if (benches.Count == 0)
			{
				throw new Fault("There is no " + benchLabel + " on the map.");
			}

			string spot = c.Text("SPOT");
			if (spot != null)
			{
				IntVec3 cell;
				string reason;
				if (!AgentPlace.TryCell(map, spot, out cell, out reason))
				{
					throw new Fault(string.IsNullOrEmpty(reason) ? "'" + spot + "' is not a place the mod can find." : reason);
				}
				Building_WorkTable pick = benches.FirstOrDefault((Building_WorkTable b) => b.OccupiedRect().Contains(cell));
				if (pick == null)
				{
					pick = benches.OrderBy((Building_WorkTable b) => b.Position.DistanceToSquared(cell)).First();
				}
				benches = new List<Building_WorkTable> { pick };
			}

			bool several = benches.Count > 1;
			List<string> lines = new List<string>();
			foreach (Building_WorkTable bench in benches.Take(RbBillMaxBenches))
			{
				string label = StorageIndex.BenchVocabLabel(bench);
				string room = BuildDefs.RoomText(RoomOfThing(bench));
				string at = " at " + AgentPlace.Describe(map, bench.Position) + (room.Length > 0 ? " in the " + room : "");
				List<Bill> bills = bench.billStack != null ? bench.billStack.Bills : null;
				if (bills == null || bills.Count == 0)
				{
					lines.Add("The " + label + (several ? at : "") + " has no bills.");
					continue;
				}
				lines.Add(label + at + ":");
				for (int i = 0; i < bills.Count; i++)
				{
					try
					{
						RbBillLines(bills[i], i + 1, lines);
					}
					catch (Exception)
					{
						lines.Add((i + 1) + ". " + bills[i].Label + " - could not be read");
					}
				}
			}
			if (benches.Count > RbBillMaxBenches)
			{
				lines.Add("... " + (benches.Count - RbBillMaxBenches) + " more " + benchLabel + " benches; ask about one: show bills at "
					+ benchLabel + " in ROOM.");
			}
			return RbStoreCut(lines, "ask about one bench: show bills at " + benchLabel + " in ROOM.");
		}

		// One bill: a one-line summary, then indented lines only for what differs from a new bill's defaults.
		private static void RbBillLines(Bill bill, int number, List<string> lines)
		{
			Bill_Production production = bill as Bill_Production;
			string head = number + ". " + bill.Label;
			if (production == null)
			{
				head += " - not an ordinary production bill";
				if (bill.suspended)
				{
					head += " - paused by you";
				}
				lines.Add(head);
				return;
			}
			if (!string.Equals(production.RenamableLabel, production.BaseLabel, StringComparison.OrdinalIgnoreCase))
			{
				head += " (named '" + production.RenamableLabel + "')";
			}
			head += " - " + RbBillRepeat(production);
			if (bill.suspended)
			{
				head += " - paused by you";
			}
			else if (production.repeatMode == BillRepeatModeDefOf.TargetCount && production.paused)
			{
				head += " - paused (satisfied)";
			}
			lines.Add(head);

			if (production.repeatMode == BillRepeatModeDefOf.TargetCount && production.pauseWhenSatisfied)
			{
				lines.Add("    pauses when satisfied; starts again at " + production.unpauseWhenYouHave + " or fewer");
			}

			string who = RbBillWho(bill);
			if (who != null)
			{
				lines.Add("    who: " + who);
			}
			if (bill.PawnRestriction == null && bill.recipe.workSkill != null && !bill.MechsOnly
				&& (bill.allowedSkillRange.min != 0 || bill.allowedSkillRange.max != 20))
			{
				lines.Add("    skill: " + bill.recipe.workSkill.label + " " + bill.allowedSkillRange.min + " to " + bill.allowedSkillRange.max);
			}
			if (bill.ingredientSearchRadius != RbBillUnlimitedRadius)
			{
				lines.Add("    ingredient radius: " + Math.Round(bill.ingredientSearchRadius).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " tiles");
			}
			string store = RbBillStore(production);
			if (store != null)
			{
				lines.Add("    products: " + store);
			}
			List<string> ingredients = RbBillIngredients(bill);
			if (ingredients != null)
			{
				lines.Add("    ingredients changed - " + ingredients[0]);
				for (int i = 1; i < ingredients.Count; i++)
				{
					lines.Add("    " + ingredients[i]);
				}
			}
		}

		private static string RbBillRepeat(Bill_Production bill)
		{
			if (bill.repeatMode == BillRepeatModeDefOf.Forever)
			{
				return "forever";
			}
			if (bill.repeatMode == BillRepeatModeDefOf.RepeatCount)
			{
				return bill.repeatCount > 0 ? "do " + bill.repeatCount + " more " + (bill.repeatCount == 1 ? "time" : "times") : "do 0 more times (finished)";
			}
			if (bill.repeatMode == BillRepeatModeDefOf.TargetCount)
			{
				string target = bill.targetCount < RbBillInfiniteTarget ? bill.targetCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : "infinite";
				string have = "";
				try
				{
					have = " (you have " + bill.recipe.WorkerCounter.CountProducts(bill) + ")";
				}
				catch (Exception)
				{
					have = "";
				}
				return "until you have " + target + have;
			}
			return "repeat mode unknown";
		}

		// null = anyone (the default).
		private static string RbBillWho(Bill bill)
		{
			if (bill.PawnRestriction != null)
			{
				return "only " + bill.PawnRestriction.LabelShort;
			}
			if (ModsConfig.IdeologyActive && bill.SlavesOnly)
			{
				return "slaves only";
			}
			if (ModsConfig.BiotechActive && bill.MechsOnly)
			{
				return "mechs only";
			}
			if (ModsConfig.BiotechActive && bill.NonMechsOnly)
			{
				return "non-mechs only";
			}
			return null;
		}

		// null = best storage (the default).
		private static string RbBillStore(Bill_Production bill)
		{
			BillStoreModeDef mode = bill.GetStoreMode();
			if (mode == BillStoreModeDefOf.DropOnFloor)
			{
				return "dropped on the floor";
			}
			if (mode == BillStoreModeDefOf.SpecificStockpile)
			{
				ISlotGroup group = bill.GetSlotGroup();
				if (group == null)
				{
					return "to a storage that is gone";
				}
				string label = SlotGroup.GetGroupLabel(group);
				if (label.NullOrEmpty() || label.StartsWith("UNABLE TO GET LABEL"))
				{
					label = "a chosen storage";
				}
				string text = "to " + label;
				try
				{
					if (!bill.recipe.WorkerCounter.CanPossiblyStore(bill, group))
					{
						text += " (it can't hold these)";
					}
				}
				catch (Exception)
				{
				}
				return text;
			}
			return null;
		}

		// null = the ingredients are the recipe's default (or the recipe has nothing to choose); else the filter summary lines.
		private static List<string> RbBillIngredients(Bill bill)
		{
			RecipeDef recipe = bill.recipe;
			if (bill.ingredientFilter == null || recipe.fixedIngredientFilter == null)
			{
				return null;
			}
			if (recipe.ingredients.All((IngredientCount i) => i.IsFixedIngredient))
			{
				return null;
			}
			ThingFilter standard = recipe.defaultIngredientFilter;
			if (standard != null)
			{
				bool same = true;
				foreach (ThingDef def in recipe.fixedIngredientFilter.AllowedThingDefs)
				{
					if (bill.ingredientFilter.Allows(def) != standard.Allows(def))
					{
						same = false;
						break;
					}
				}
				if (same)
				{
					return null;
				}
			}
			return RbStoreFilterSummary(bill.ingredientFilter, recipe.fixedIngredientFilter);
		}

		// ---- query storage ---------------------------------------------------------------------------------------------

		private static string RbStoreQuery(FormContext c)
		{
			Map map = c.map;
			List<StorageUnit> units;
			string askLess;
			if (c.Has("ZONE"))
			{
				string zone = c.Text("ZONE");
				units = StorageIndex.CollectUnits(map)
					.Where((StorageUnit u) => u.zone != null
						&& (string.Equals((u.zone.label ?? "").Trim(), zone, StringComparison.OrdinalIgnoreCase)
							|| string.Equals((u.label ?? "").Trim(), zone, StringComparison.OrdinalIgnoreCase)))
					.ToList();
				if (units.Count == 0)
				{
					throw new Fault("There is no stockpile zone called '" + zone + "'.");
				}
				askLess = "ask about one: show storage of zone ZONE.";
			}
			else
			{
				units = ResolveStorageUnits(c);
				askLess = "ask about one place: show storage near BENCH in ROOM.";
			}

			List<string> lines = new List<string>();
			foreach (StorageUnit unit in units.Take(RbStoreMaxUnits))
			{
				try
				{
					RbStoreUnitLines(map, unit, lines);
				}
				catch (Exception)
				{
					lines.Add((unit.label ?? "A storage") + ": could not be read");
				}
			}
			if (units.Count > RbStoreMaxUnits)
			{
				lines.Add("... " + (units.Count - RbStoreMaxUnits) + " more storage places; " + askLess);
			}
			return RbStoreCut(lines, askLess);
		}

		// ---- query stores ----------------------------------------------------------------------------------------------
		// User 2026-10-07: "Show places that only store X" (X = vocabulary of item, or category, also takes in game ID).
		// Output would be "Here are places that store X" (stockpile zone 1....n, Shelf (gameID, coordinate)."; every
		// storage that accepts X, the ones that take only X marked ("Both, marked"). X is an item or category name
		// (ClassifyThings' name rules) or the ThingID of a thing on the map (its def; a minified thing's inner def).
		// "Accepts" = the storage's filter allows a def of X that its parent filter lets in (ThingFilter.Allows,
		// Verse\ThingFilter.cs:908; the parent as in RbStoreUnitLines); "only" = every def the filter allows
		// (ThingFilter.AllowedThingDefs, :176) is a def of X.

		private static string RbStoresQuery(FormContext c)
		{
			Map map = c.map;
			List<string> things = c.TextList("THINGS");
			if (things.Count == 0)
			{
				throw new Fault("The question did not say what is stored.");
			}
			HashSet<ThingDef> wanted = new HashSet<ThingDef>();
			List<string> names = new List<string>();
			foreach (string text in things)
			{
				names.Add(RbStoresDefsOf(map, text, wanted));
			}
			string x = JoinNames(names);

			List<KeyValuePair<int, string>> found = new List<KeyValuePair<int, string>>();
			foreach (StorageUnit unit in StorageIndex.CollectUnits(map))
			{
				try
				{
					StorageSettings settings = unit.Settings;
					if (settings == null || !RbStoreMiddle(map, unit).IsValid)
					{
						continue;
					}
					ThingFilter parent = unit.ParentSettings != null ? unit.ParentSettings.filter : null;
					List<ThingDef> could = wanted.Where((ThingDef d) => parent == null || parent.Allows(d)).ToList();
					int accepted = could.Count((ThingDef d) => settings.filter.Allows(d));
					if (accepted == 0)
					{
						continue;
					}
					bool only = settings.filter.AllowedThingDefs.All((ThingDef d) => wanted.Contains(d) || (parent != null && !parent.Allows(d)));
					string line = "- " + RbStoresPlace(unit) + ", " + settings.Priority.Label() + " priority";
					if (accepted < could.Count)
					{
						line += ", " + accepted + " of the " + could.Count + " kinds";
					}
					if (only)
					{
						line += " (only " + x + ")";
					}
					found.Add(new KeyValuePair<int, string>((only ? 1000 : 0) + (int)settings.Priority, line));
				}
				catch (Exception)
				{
				}
			}
			if (found.Count == 0)
			{
				return "No storage on the map accepts " + x + ".";
			}
			List<string> lines = new List<string>();
			lines.Add("Here are places that store " + x + ":");
			lines.AddRange(found.OrderByDescending((KeyValuePair<int, string> kv) => kv.Key).Select((KeyValuePair<int, string> kv) => kv.Value));
			return RbStoreCut(lines, "ask about fewer things.");
		}

		// Adds the defs a THINGS entry names to wanted; gives the name to show.
		private static string RbStoresDefsOf(Map map, string text, HashSet<ThingDef> wanted)
		{
			string t = (text ?? "").Trim();
			// A game ID ends in its number (Verse\Thing.cs:392-400).
			if (t.Length > 0 && char.IsDigit(t[t.Length - 1]))
			{
				Thing thing = map.listerThings.AllThings.FirstOrDefault((Thing th) => string.Equals(th.ThingID, t, StringComparison.OrdinalIgnoreCase));
				if (thing != null)
				{
					Thing inner = thing.GetInnerIfMinified();
					ThingDef def = inner != null ? inner.def : thing.def;
					wanted.Add(def);
					return thing.ThingID + " (" + RbStoreName(def) + ")";
				}
			}
			List<string> cats = new List<string>();
			List<string> items = new List<string>();
			ClassifyThings(new List<string> { t }, cats, items);
			foreach (string item in items)
			{
				foreach (ThingDef def in Executor.FindItemDefs(item))
				{
					wanted.Add(def);
				}
			}
			foreach (string cat in cats)
			{
				foreach (ThingCategoryDef catDef in Executor.FindCategoryDefs(cat))
				{
					foreach (ThingDef def in catDef.DescendantThingDefs)
					{
						wanted.Add(def);
					}
				}
			}
			return t;
		}

		// "Stockpile zone 1 (stockpile)", "Shelf (ID Shelf12345, at 112, 87)"; linked buildings: the group's name, then each.
		private static string RbStoresPlace(StorageUnit unit)
		{
			if (unit.zone != null)
			{
				return (unit.label ?? "Stockpile zone") + " (stockpile)";
			}
			List<string> parts = unit.buildings.Where((Thing b) => b.Spawned)
				.Select((Thing b) => b.LabelCap.ToString() + " (ID " + b.ThingID + ", at " + b.Position.x + ", " + b.Position.z + ")").ToList();
			if (unit.group != null)
			{
				return (unit.label ?? "Storage group") + ", linked, one setting: " + string.Join(", ", parts.ToArray());
			}
			return parts.Count > 0 ? parts[0] : (unit.label ?? "Storage");
		}

		private static void RbStoreUnitLines(Map map, StorageUnit unit, List<string> lines)
		{
			string head = (unit.label ?? "Storage") + " (" + unit.kind + ", " + unit.cells.Count + (unit.cells.Count == 1 ? " cell" : " cells") + ")";
			IntVec3 middle = RbStoreMiddle(map, unit);
			if (middle.IsValid)
			{
				head += " near " + AgentPlace.Describe(map, middle);
			}
			lines.Add(head);

			StorageSettings settings = unit.Settings;
			if (settings != null)
			{
				lines.Add("  priority: " + settings.Priority.Label());
				ThingFilter parent = null;
				StorageSettings parentSettings = unit.ParentSettings;
				if (parentSettings != null)
				{
					parent = parentSettings.filter;
				}
				foreach (string line in RbStoreFilterSummary(settings.filter, parent))
				{
					lines.Add("  " + line);
				}
			}
			lines.Add("  " + RbStoreHolds(map, unit));
		}

		// The unit's cell nearest the middle of its visible cells; IntVec3.Invalid when all are under fog.
		private static IntVec3 RbStoreMiddle(Map map, StorageUnit unit)
		{
			List<IntVec3> cells = unit.cells.Where((IntVec3 cell) => !map.fogGrid.IsFogged(cell)).ToList();
			if (cells.Count == 0)
			{
				return IntVec3.Invalid;
			}
			double mx = cells.Average((IntVec3 cell) => (double)cell.x);
			double mz = cells.Average((IntVec3 cell) => (double)cell.z);
			IntVec3 best = cells[0];
			double bestD = double.MaxValue;
			foreach (IntVec3 cell in cells)
			{
				double d = (cell.x - mx) * (cell.x - mx) + (cell.z - mz) * (cell.z - mz);
				if (d < bestD)
				{
					bestD = d;
					best = cell;
				}
			}
			return best;
		}

		// Adds the visible things to the per-def counts. Things on slot-group cells also mark the cell as used.
		private static void RbStoreAdd(Map map, IEnumerable<Thing> things, IntVec3 holderCell, Dictionary<string, int> counts,
			HashSet<IntVec3> used, ref int stacks)
		{
			foreach (Thing thing in things)
			{
				if (thing == null)
				{
					continue;
				}
				IntVec3 where = thing.Spawned ? thing.Position : holderCell;
				if (where.IsValid && map.fogGrid.IsFogged(where))
				{
					continue;
				}
				Thing inner = thing.GetInnerIfMinified();
				ThingDef def = inner != null ? inner.def : thing.def;
				string label = RbStoreName(def);
				int n;
				counts.TryGetValue(label, out n);
				counts[label] = n + thing.stackCount;
				stacks++;
				if (thing.Spawned)
				{
					used.Add(thing.Position);
				}
			}
		}

		// "holds: steel 150, wood 300 ... (N of M cells used)".
		private static string RbStoreHolds(Map map, StorageUnit unit)
		{
			Dictionary<string, int> counts = new Dictionary<string, int>();
			HashSet<IntVec3> used = new HashSet<IntVec3>();
			int stacks = 0;
			bool onCells = false;
			if (unit.zone != null && unit.zone.slotGroup != null)
			{
				onCells = true;
				RbStoreAdd(map, unit.zone.slotGroup.HeldThings, IntVec3.Invalid, counts, used, ref stacks);
			}
			foreach (Thing building in unit.buildings)
			{
				if (map.fogGrid.IsFogged(building.Position))
				{
					continue;
				}
				ISlotGroupParent slotParent = building as ISlotGroupParent;
				if (slotParent != null)
				{
					SlotGroup slotGroup = slotParent.GetSlotGroup();
					if (slotGroup != null)
					{
						onCells = true;
						RbStoreAdd(map, slotGroup.HeldThings, building.Position, counts, used, ref stacks);
					}
					continue;
				}
				IThingHolder holder = building as IThingHolder;
				if (holder != null)
				{
					ThingOwner owner = holder.GetDirectlyHeldThings();
					if (owner != null)
					{
						RbStoreAdd(map, owner, building.Position, counts, used, ref stacks);
					}
				}
			}

			string text;
			if (counts.Count == 0)
			{
				text = "holds: nothing";
			}
			else
			{
				List<KeyValuePair<string, int>> sorted = counts.OrderByDescending((KeyValuePair<string, int> kv) => kv.Value)
					.ThenBy((KeyValuePair<string, int> kv) => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
				text = "holds: " + string.Join(", ", sorted.Take(RbStoreTopHeld).Select((KeyValuePair<string, int> kv) => kv.Key + " " + kv.Value).ToArray());
				if (sorted.Count > RbStoreTopHeld)
				{
					text += " and " + (sorted.Count - RbStoreTopHeld) + " more kinds";
				}
			}
			if (onCells)
			{
				int visible = unit.cells.Count((IntVec3 cell) => !map.fogGrid.IsFogged(cell));
				text += " (" + used.Count + " of " + visible + " cells used)";
			}
			else if (stacks > 0)
			{
				text += " (" + stacks + (stacks == 1 ? " stack)" : " stacks)");
			}
			return text;
		}
	}
}
