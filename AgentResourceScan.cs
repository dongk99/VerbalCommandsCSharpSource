using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Steel, component and plasteel sources on the map, in the orchestrator's 6-hour reminder batch (AgentReminderScan).
	// User, 2026-10-14: "[reminder] I see forbidden steel and compacted steel throughout map, and your steel count is
	// currently 0." / "make sure mod actually scans for steel throughout map (incl. forbidden one, and compacted steel
	// one) before giving reminder." / "add [ship chunk] as well. shooting at ancient pod car & frame can give steel
	// indirectly by giving steel slag chunk. do same for compacted machinery (whenever component runs out), and same for
	// plasteel (whenever plasteel runs out). logic should also fire (all steel,machinery,plasteel) whenever expected
	// build cost on bill (exception of forever, but include until) is over stockpile."
	//
	// One line per resource when (a) the colony's count is 0 (the game's resource readout, ResourceCounter.GetCount:
	// storage only, RimWorld\ResourceCounter.cs:75-143) or (b) what the bills and blueprints still need is more than
	// that count, and the map holds a source of it in a cell that is not fogged. With no source on the map: the
	// fallback "Trade, strip-mine into mountains, or research long range scanner/deep drill/ground penetrating
	// scanner." (user), when something waits for it or the colony had some since the load (see Line).
	//   Sources: the resource lying outside storage (forbidden and not forbidden counted apart); its compacted rock
	//   (MineableSteel, MineableComponentsIndustrial "compacted machinery", MineablePlasteel); buildings of no player
	//   faction the colony may deconstruct (Building.DeconstructibleBy, Verse\Building.cs:488-513) whose cost holds it
	//   (a ship chunk: 40 steel, 11 components, Buildings_Exotic.xml:163-166), with what deconstructing gives back
	//   (cost x resourcesFractionWhenDeconstructed, 0.5 by default, Verse\BuildableDef.cs:38; GenLeaving.cs:320);
	//   buildings that can not be deconstructed but leave it, or for steel steel slag chunks, when destroyed
	//   (ThingDef.killedLeavings: ancient car, truck, troop carrier, car frame 2 slag chunks, ancient pod car 6,
	//   Buildings_Ancient_Outdoors.xml:527-602, 897-924); and for steel the steel slag chunks lying anywhere (an
	//   electric smelter smelts them, "smelt metal from slag", Recipes_Production.xml:45).
	//   Bill need: every bill of a colony bench that is not suspended. "Do X times" counts its runs left
	//   (Bill_Production.repeatCount, counted down per run); "do until you have X" counts (target - what the bill window
	//   shows, RecipeWorkerCounter.CountProducts) / products per run, rounded up, 0 while paused as satisfied or for an
	//   infinite target (999999 or more); "do forever" is left out. Per run: an ingredient that can only be this
	//   resource (a fixed ingredient, or the only one of its allowed things the bill's filter lets through), in the
	//   game's count (IngredientCount.CountRequiredOfFor, Verse\IngredientCount.cs:52-56).
	internal static class AgentResourceScan
	{
		private const int InfiniteTarget = 999999;
		private const int MaxBillsNamed = 3;

		private class Spec
		{
			public ThingDef def;
			public string plural;
			public ThingDef rock;
			public string rockLabel;
		}

		private class Group
		{
			public int count;
			public IntVec3 at = IntVec3.Invalid;
			public int each;
		}

		private static List<Spec> Specs()
		{
			List<Spec> list = new List<Spec>();
			if (ThingDefOf.Steel != null)
			{
				list.Add(new Spec { def = ThingDefOf.Steel, plural = "steel", rock = ThingDefOf.MineableSteel, rockLabel = "compacted steel" });
			}
			if (ThingDefOf.ComponentIndustrial != null)
			{
				list.Add(new Spec { def = ThingDefOf.ComponentIndustrial, plural = "components", rock = ThingDefOf.MineableComponentsIndustrial, rockLabel = "compacted machinery" });
			}
			if (ThingDefOf.Plasteel != null)
			{
				list.Add(new Spec { def = ThingDefOf.Plasteel, plural = "plasteel", rock = DefDatabase<ThingDef>.GetNamedSilentFail("MineablePlasteel"), rockLabel = "compacted plasteel" });
			}
			return list;
		}

		public static List<string> Lines(Map map)
		{
			List<string> lines = new List<string>();
			List<Spec> specs = Specs();
			Dictionary<ThingDef, int> need = new Dictionary<ThingDef, int>();
			Dictionary<ThingDef, List<KeyValuePair<int, string>>> billsOf = new Dictionary<ThingDef, List<KeyValuePair<int, string>>>();
			BillNeeds(map, specs, need, billsOf);
			BlueprintNeeds(map, specs, need, billsOf);
			foreach (Spec spec in specs)
			{
				int n;
				need.TryGetValue(spec.def, out n);
				List<KeyValuePair<int, string>> bills;
				billsOf.TryGetValue(spec.def, out bills);
				string line = Line(map, spec, n, bills);
				if (line != null)
				{
					lines.Add(line);
				}
			}
			return lines;
		}

		// ---- what the bills still need ------------------------------------------------------------------------------

		private static void BillNeeds(Map map, List<Spec> specs, Dictionary<ThingDef, int> need, Dictionary<ThingDef, List<KeyValuePair<int, string>>> billsOf)
		{
			foreach (Building b in map.listerBuildings.allBuildingsColonist)
			{
				IBillGiver giver = b as IBillGiver;
				if (giver == null || giver.BillStack == null)
				{
					continue;
				}
				foreach (Bill bill in giver.BillStack.Bills)
				{
					Bill_Production bp = bill as Bill_Production;
					if (bp == null || bp.suspended || bp.recipe == null || bp.recipe.ingredients == null)
					{
						continue;
					}
					int runs;
					try
					{
						runs = Runs(bp);
					}
					catch (Exception)
					{
						continue;
					}
					if (runs <= 0)
					{
						continue;
					}
					foreach (Spec spec in specs)
					{
						int per = PerRun(bp, spec.def);
						if (per <= 0)
						{
							continue;
						}
						int amount = per * runs;
						int had;
						need.TryGetValue(spec.def, out had);
						need[spec.def] = had + amount;
						List<KeyValuePair<int, string>> list;
						if (!billsOf.TryGetValue(spec.def, out list))
						{
							billsOf[spec.def] = list = new List<KeyValuePair<int, string>>();
						}
						list.Add(new KeyValuePair<int, string>(amount, bp.recipe.label + " x" + runs + " (" + amount + ")"));
					}
				}
			}
		}

		// What the colony's blueprints and frames still need (user: "blueprint should also count."): a blueprint its whole
		// cost (Blueprint_Build.TotalMaterialCost, RimWorld\Blueprint_Build.cs:65-68; an install blueprint costs nothing
		// and its TotalMaterialCost logs an error, so only Blueprint_Build), a frame what is not delivered yet
		// (Frame.ThingCountNeeded, RimWorld\Frame.cs:228), as TipInjector's short-of-material state counts them. Named
		// per building: "wall blueprints x12 (60)".
		private static void BlueprintNeeds(Map map, List<Spec> specs, Dictionary<ThingDef, int> need, Dictionary<ThingDef, List<KeyValuePair<int, string>>> billsOf)
		{
			Dictionary<ThingDef, Dictionary<string, int[]>> byLabel = new Dictionary<ThingDef, Dictionary<string, int[]>>();
			List<Thing> things = new List<Thing>();
			things.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint));
			things.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame));
			foreach (Thing t in things)
			{
				if (t == null || t.Destroyed || t.Faction != Faction.OfPlayer || t.def.entityDefToBuild == null)
				{
					continue;
				}
				Frame frame = t as Frame;
				Blueprint_Build blueprint = t as Blueprint_Build;
				List<ThingDefCountClass> cost;
				try
				{
					cost = frame != null ? frame.TotalMaterialCost() : blueprint != null ? blueprint.TotalMaterialCost() : null;
				}
				catch (Exception)
				{
					continue;
				}
				if (cost == null)
				{
					continue;
				}
				string label = (t.def.entityDefToBuild.label ?? t.def.entityDefToBuild.defName) + (frame != null ? " frames" : " blueprints");
				foreach (Spec spec in specs)
				{
					ThingDefCountClass c = cost.FirstOrDefault((ThingDefCountClass x) => x != null && x.thingDef == spec.def);
					if (c == null)
					{
						continue;
					}
					int n = frame != null ? frame.ThingCountNeeded(spec.def) : c.count;
					if (n <= 0)
					{
						continue;
					}
					int had;
					need.TryGetValue(spec.def, out had);
					need[spec.def] = had + n;
					Dictionary<string, int[]> labels;
					if (!byLabel.TryGetValue(spec.def, out labels))
					{
						byLabel[spec.def] = labels = new Dictionary<string, int[]>();
					}
					int[] v;
					if (!labels.TryGetValue(label, out v))
					{
						labels[label] = v = new int[2];
					}
					v[0]++;
					v[1] += n;
				}
			}
			foreach (KeyValuePair<ThingDef, Dictionary<string, int[]>> kv in byLabel)
			{
				List<KeyValuePair<int, string>> list;
				if (!billsOf.TryGetValue(kv.Key, out list))
				{
					billsOf[kv.Key] = list = new List<KeyValuePair<int, string>>();
				}
				foreach (KeyValuePair<string, int[]> l in kv.Value)
				{
					list.Add(new KeyValuePair<int, string>(l.Value[1], l.Key + " x" + l.Value[0] + " (" + l.Value[1] + ")"));
				}
			}
		}

		// Runs a bill still has to do: "do X times" its runs left, "do until you have X" what is missing / products per
		// run, "forever" none (see the header).
		private static int Runs(Bill_Production bp)
		{
			if (bp.repeatMode == BillRepeatModeDefOf.RepeatCount)
			{
				return bp.repeatCount;
			}
			if (bp.repeatMode == BillRepeatModeDefOf.TargetCount)
			{
				if (bp.targetCount >= InfiniteTarget || bp.paused || bp.recipe.products == null || bp.recipe.products.Count == 0
					|| !bp.recipe.WorkerCounter.CanCountProducts(bp))
				{
					return 0;
				}
				int missing = bp.targetCount - bp.recipe.WorkerCounter.CountProducts(bp);
				int perRun = Math.Max(1, bp.recipe.products[0].count);
				return missing <= 0 ? 0 : (missing + perRun - 1) / perRun;
			}
			return 0;
		}

		// How much of DEF one run takes: the ingredients that can only be DEF under this bill.
		private static int PerRun(Bill_Production bp, ThingDef def)
		{
			int n = 0;
			foreach (IngredientCount ing in bp.recipe.ingredients)
			{
				if (ing == null || ing.filter == null || !ing.filter.Allows(def))
				{
					continue;
				}
				bool only = ing.IsFixedIngredient;
				if (!only && bp.ingredientFilter != null && bp.ingredientFilter.Allows(def))
				{
					only = ing.filter.AllowedThingDefs.All((ThingDef o) => o == def || !bp.ingredientFilter.Allows(o));
				}
				if (only)
				{
					n += ing.CountRequiredOfFor(def, bp.recipe, bp);
				}
			}
			return n;
		}

		// ---- the sources on the map and the line ---------------------------------------------------------------------

		private static string Line(Map map, Spec spec, int need, List<KeyValuePair<int, string>> bills)
		{
			ThingDef def = spec.def;
			int stock = map.resourceCounter.GetCount(def);
			if (stock > 0)
			{
				hadSome.Add(def);
			}
			if (stock > 0 && need <= stock)
			{
				return null;
			}

			// The resource itself, outside storage.
			Group forbidden = new Group();
			Group loose = new Group();
			int forbiddenAmount = 0;
			int looseAmount = 0;
			foreach (Thing t in map.listerThings.ThingsOfDef(def))
			{
				if (!t.Spawned || !AgentPlace.Visible(map, t.Position) || t.GetSlotGroup() != null)
				{
					continue;
				}
				if (t.IsForbidden(Faction.OfPlayer))
				{
					Count(forbidden, t.Position);
					forbiddenAmount += t.stackCount;
				}
				else
				{
					Count(loose, t.Position);
					looseAmount += t.stackCount;
				}
			}

			// Its compacted rock.
			Group rock = new Group();
			int marked = 0;
			if (spec.rock != null)
			{
				foreach (Thing t in map.listerThings.ThingsOfDef(spec.rock))
				{
					if (!t.Spawned || !AgentPlace.Visible(map, t.Position))
					{
						continue;
					}
					Count(rock, t.Position);
					if (map.designationManager.DesignationAt(t.Position, DesignationDefOf.Mine) != null)
					{
						marked++;
					}
				}
			}

			// Steel slag chunks lying anywhere (steel only).
			Group slag = new Group();
			bool isSteel = def == ThingDefOf.Steel && ThingDefOf.ChunkSlagSteel != null;
			if (isSteel)
			{
				foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.ChunkSlagSteel))
				{
					if (t.Spawned && AgentPlace.Visible(map, t.Position))
					{
						Count(slag, t.Position);
					}
				}
			}

			// Buildings: deconstruct for it, or destroy for it / for steel slag chunks.
			Dictionary<ThingDef, Group> deconstruct = new Dictionary<ThingDef, Group>();
			Dictionary<ThingDef, Group> destroy = new Dictionary<ThingDef, Group>();
			Dictionary<ThingDef, bool> destroyGivesSlag = new Dictionary<ThingDef, bool>();
			foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
			{
				Building b = t as Building;
				if (b == null || !b.Spawned || b.Faction == Faction.OfPlayer || !AgentPlace.Visible(map, b.Position))
				{
					continue;
				}
				if (b.DeconstructibleBy(Faction.OfPlayer).Accepted && b.def.resourcesFractionWhenDeconstructed > 0f)
				{
					int back = Back(b, def);
					if (back > 0)
					{
						Count(Of(deconstruct, b.def), b.Position).each = back;
					}
					continue;
				}
				if (b.def.killedLeavings == null)
				{
					continue;
				}
				ThingDefCountClass leave = b.def.killedLeavings.FirstOrDefault((ThingDefCountClass l) => l.thingDef == def)
					?? (isSteel ? b.def.killedLeavings.FirstOrDefault((ThingDefCountClass l) => l.thingDef == ThingDefOf.ChunkSlagSteel) : null);
				if (leave != null && leave.count > 0)
				{
					Count(Of(destroy, b.def), b.Position).each = leave.count;
					destroyGivesSlag[b.def] = leave.thingDef != def;
				}
			}

			List<string> seen = new List<string>();
			List<string> detail = new List<string>();
			if (forbidden.count > 0)
			{
				seen.Add("forbidden " + spec.plural);
				detail.Add("forbidden: " + forbiddenAmount + " " + def.label + " in " + Stacks(forbidden.count) + ", one at (" + AgentWorkspotCheck.Xz(forbidden.at) + ")");
			}
			if (loose.count > 0)
			{
				seen.Add(spec.plural + " lying outside storage");
				detail.Add("outside storage: " + looseAmount + " " + def.label + " in " + Stacks(loose.count) + ", one at (" + AgentWorkspotCheck.Xz(loose.at) + ")");
			}
			if (rock.count > 0)
			{
				seen.Add(spec.rockLabel);
				detail.Add(spec.rockLabel + ": " + rock.count + (rock.count == 1 ? " cell" : " cells") + ", one at (" + AgentWorkspotCheck.Xz(rock.at) + "), " + marked + " marked for mining");
			}
			if (slag.count > 0)
			{
				seen.Add("steel slag chunks");
				detail.Add("steel slag chunks: " + slag.count + ", one at (" + AgentWorkspotCheck.Xz(slag.at) + "), an electric smelter smelts them into steel");
			}
			foreach (KeyValuePair<ThingDef, Group> kv in deconstruct)
			{
				seen.Add(Plural(kv.Key, kv.Value.count));
				detail.Add(kv.Key.label + ": " + kv.Value.count + ", one at (" + AgentWorkspotCheck.Xz(kv.Value.at) + "), deconstructing gives about " + kv.Value.each + " " + def.label + " each");
			}
			foreach (KeyValuePair<ThingDef, Group> kv in destroy)
			{
				seen.Add(Plural(kv.Key, kv.Value.count));
				string leaves = destroyGivesSlag[kv.Key] ? kv.Value.each + " steel slag " + (kv.Value.each == 1 ? "chunk" : "chunks") : kv.Value.each + " " + def.label;
				detail.Add(kv.Key.label + ": " + kv.Value.count + ", one at (" + AgentWorkspotCheck.Xz(kv.Value.at) + "), can not be deconstructed; destroying it (shoot it) leaves " + leaves + " each");
			}
			string needs = need > stock ? ", while your bills and blueprints need about " + need + " (" + Named(bills) + ")" : "";
			if (seen.Count == 0)
			{
				// Nothing on the map (user: "if there is nothing on the map, then fallback to "Trade, strip-mine into
				// mountains, or research long range scanner/deep drill/ground penetrating scanner""). Only when something
				// waits for it, or the colony had some since the game was loaded ("whenever plasteel runs out"): a colony
				// that never had plasteel and needs none is not told every 6 hours.
				if (need <= stock && !hadSome.Contains(def))
				{
					return null;
				}
				return "[REMINDER] Your " + def.label + " count is currently " + stock + needs + ", and I see no " + def.label
					+ " source on the map. Trade, strip-mine into mountains, or research long range scanner/deep drill/ground penetrating scanner.";
			}
			return "[REMINDER] I see " + Join(seen) + " throughout the map, and your " + def.label + " count is currently " + stock + needs
				+ " (" + string.Join("; ", detail.ToArray()) + ").";
		}

		// Resources the colony had in storage at some scan since the game was loaded (Lines; cleared by Reset).
		private static readonly HashSet<ThingDef> hadSome = new HashSet<ThingDef>();

		public static void Reset()
		{
			hadSome.Clear();
		}

		// The biggest three bills / blueprint groups that need it, then "N more".
		private static string Named(List<KeyValuePair<int, string>> bills)
		{
			List<string> named = (bills ?? new List<KeyValuePair<int, string>>()).OrderByDescending((KeyValuePair<int, string> kv) => kv.Key)
				.Take(MaxBillsNamed).Select((KeyValuePair<int, string> kv) => kv.Value).ToList();
			int others = (bills == null ? 0 : bills.Count) - named.Count;
			if (others > 0)
			{
				named.Add(others + " more");
			}
			return string.Join(", ", named.ToArray());
		}

		// What deconstructing B gives back of DEF (Verse\BuildableDef.cs:38, RimWorld\GenLeaving.cs:320).
		private static int Back(Building b, ThingDef def)
		{
			List<ThingDefCountClass> cost;
			try
			{
				cost = b.def.CostListAdjusted(b.Stuff, false);
			}
			catch (Exception)
			{
				return 0;
			}
			if (cost == null)
			{
				return 0;
			}
			int n = cost.Where((ThingDefCountClass c) => c.thingDef == def).Sum((ThingDefCountClass c) => c.count);
			return n <= 0 ? 0 : Math.Max(1, (int)Math.Round(n * b.def.resourcesFractionWhenDeconstructed, MidpointRounding.AwayFromZero));
		}

		private static Group Count(Group g, IntVec3 at)
		{
			g.count++;
			if (!g.at.IsValid)
			{
				g.at = at;
			}
			return g;
		}

		private static Group Of(Dictionary<ThingDef, Group> d, ThingDef def)
		{
			Group g;
			if (!d.TryGetValue(def, out g))
			{
				d[def] = g = new Group();
			}
			return g;
		}

		private static string Stacks(int n)
		{
			return n + (n == 1 ? " stack" : " stacks");
		}

		private static string Plural(ThingDef def, int n)
		{
			return n == 1 ? def.label : Find.ActiveLanguageWorker.Pluralize(def.label, n);
		}

		// "a", "a and b", "a, b and c".
		private static string Join(List<string> parts)
		{
			if (parts.Count <= 2)
			{
				return string.Join(" and ", parts.ToArray());
			}
			return string.Join(", ", parts.Take(parts.Count - 1).ToArray()) + " and " + parts[parts.Count - 1];
		}
	}
}
