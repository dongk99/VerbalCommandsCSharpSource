using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Brief 30: questions about what pawns wear, hold and carry (read-only; MAIN THREAD ONLY like every query).
	//   query gear          one colonist's Gear tab: ITab_Pawn_Gear.FillTab (RimWorld\ITab_Pawn_Gear.cs:145-177): the equipment
	//                       list (Pawn_EquipmentTracker.AllEquipmentListForReading, Verse\Pawn_EquipmentTracker.cs:57), the
	//                       worn apparel (Pawn_ApparelTracker.WornApparel, RimWorld\Pawn_ApparelTracker.cs:40) and the
	//                       inventory (Pawn_InventoryTracker.innerContainer, Verse\Pawn_InventoryTracker.cs:15).
	//                       Marks as the tab draws them (DrawThingRow, :196-291): "forced" = OutfitForcedHandler.IsForced
	//                       (RimWorld\OutfitForcedHandler.cs:44, used at ITab_Pawn_Gear.cs:275), "locked" = the greyed drop
	//                       button (:206-212: Pawn_ApparelTracker.IsLocked, RimWorld\Pawn_ApparelTracker.cs:359; quest lodger
	//                       gear; PawnKindDef.destroyGearOnDrop). Quality is Thing.TryGetQuality (RimWorld\QualityUtility.cs:27),
	//                       hit points Thing.HitPoints / Thing.MaxHitPoints (Verse\Thing.cs:105).
	//   query wearing / not wearing     which colonists wear (none of) THINGS: only apparel defs
	//   query holding / not holding     which colonists have (none of) THINGS equipped: weapons and other equipment defs
	//   query carrying / not carrying   which colonists have (none of) THINGS in the inventory: any item def
	// The pawn set is the one "query colonists" uses (map.mapPawns.FreeColonists); animals and prisoners are not listed.
	// Nothing here calls a getter that fills in a default: the trackers are read, never the policy getters.
	public static partial class FormPlanner
	{
		private enum GearKind
		{
			Worn,
			Held,
			Carried
		}

		private class GearMatch
		{
			public bool any;
			public string label;
			public HashSet<ThingDef> defs = new HashSet<ThingDef>();

			public bool Matches(Thing t)
			{
				return any || defs.Contains(t.def);
			}
		}

		private static QueryReply QueryGear(FormContext c)
		{
			switch (c.form)
			{
				case "query gear":
					return QueryReply.Ok(QueryGearOf(c));
				case "query wearing":
					return QueryReply.Ok(WhoHas(c, GearKind.Worn, true));
				case "query not wearing":
					return QueryReply.Ok(WhoHas(c, GearKind.Worn, false));
				case "query holding":
					return QueryReply.Ok(WhoHas(c, GearKind.Held, true));
				case "query not holding":
					return QueryReply.Ok(WhoHas(c, GearKind.Held, false));
				case "query carrying":
					return QueryReply.Ok(WhoHas(c, GearKind.Carried, true));
				case "query not carrying":
					return QueryReply.Ok(WhoHas(c, GearKind.Carried, false));
			}
			return null;
		}

		// ---- the things a pawn has, per kind ---------------------------------------------------------------------------

		private static List<Thing> GearOf(Pawn p, GearKind kind)
		{
			List<Thing> list = new List<Thing>();
			switch (kind)
			{
				case GearKind.Worn:
					if (p.apparel != null)
					{
						list.AddRange(p.apparel.WornApparel.Cast<Thing>());
					}
					break;
				case GearKind.Held:
					if (p.equipment != null)
					{
						list.AddRange(p.equipment.AllEquipmentListForReading.Cast<Thing>());
					}
					break;
				default:
					if (p.inventory != null)
					{
						list.AddRange(p.inventory.innerContainer);
					}
					break;
			}
			return list;
		}

		// ---- how THINGS is matched -------------------------------------------------------------------------------------

		private static string GearNoun(GearKind kind)
		{
			return kind == GearKind.Worn ? "apparel" : kind == GearKind.Held ? "weapon or equipment" : "item";
		}

		private static bool InGearPool(ThingDef d, GearKind kind)
		{
			if (d.category != ThingCategory.Item || d.label.NullOrEmpty())
			{
				return false;
			}
			switch (kind)
			{
				case GearKind.Worn:
					return d.IsApparel;
				case GearKind.Held:
					return d.IsWeapon || d.equipmentType != EquipmentType.None;
				default:
					return true;
			}
		}

		// Words that mean "any of this kind" (who is unarmed = not holding any weapon). "a" / "the" never get here.
		private static GearMatch GenericGearWord(string n, GearKind kind, List<ThingDef> pool)
		{
			string word = n.StartsWith("any ") ? n.Substring(4) : n;
			bool anything = n == "anything" || n == "something" || n == "any" || n == "things" || n == "anything at all";
			GearMatch m = new GearMatch();
			switch (kind)
			{
				case GearKind.Worn:
					if (anything || word == "apparel" || word == "clothing" || word == "clothes" || word == "gear")
					{
						m.any = true;
						m.label = "any apparel";
						return m;
					}
					break;
				case GearKind.Held:
					if (anything || word == "weapon" || word == "weapons" || word == "equipment")
					{
						m.any = true;
						m.label = "any weapon";
						return m;
					}
					if (word == "ranged weapon" || word == "ranged weapons" || word == "melee weapon" || word == "melee weapons")
					{
						bool ranged = word.StartsWith("ranged");
						foreach (ThingDef d in pool)
						{
							if (ranged ? d.IsRangedWeapon : d.IsMeleeWeapon)
							{
								m.defs.Add(d);
							}
						}
						m.label = "any " + (ranged ? "ranged" : "melee") + " weapon";
						return m;
					}
					break;
				default:
					if (anything || word == "item" || word == "items")
					{
						m.any = true;
						m.label = "anything";
						return m;
					}
					break;
			}
			return null;
		}

		// One word of the player's: generic word, else (as CountLine does, FormQueries.cs:123-160) a def's label or its plural,
		// else a category label, else a word that is part of a label. Unlike "query count" every def that fits is taken
		// ("helmet" is every helmet), only defs of the kind asked about, and the first rule that finds any decides.
		private static GearMatch MatchGearWord(string text, GearKind kind, List<ThingDef> pool)
		{
			string n = Norm(text);
			if (n.Length == 0)
			{
				throw new Fault("The question did not say which " + GearNoun(kind) + " to look for.");
			}
			GearMatch generic = GenericGearWord(n, kind, pool);
			if (generic != null)
			{
				return generic;
			}
			GearMatch m = new GearMatch();
			m.label = text.Trim();
			foreach (ThingDef d in pool.Where((ThingDef d) => LabelMatchesWord(Norm(d.label), n)))
			{
				m.defs.Add(d);
			}
			if (m.defs.Count == 0)
			{
				foreach (ThingCategoryDef cat in DefDatabase<ThingCategoryDef>.AllDefsListForReading)
				{
					if (!cat.label.NullOrEmpty() && LabelMatchesWord(Norm(cat.label), n))
					{
						foreach (ThingDef d in cat.DescendantThingDefs.Where((ThingDef d) => pool.Contains(d)))
						{
							m.defs.Add(d);
						}
					}
				}
			}
			if (m.defs.Count == 0)
			{
				foreach (ThingDef d in pool.Where((ThingDef d) =>
				{
					string label = Norm(d.label);
					return ContainsWord(label, n) || (label.Length >= 3 && ContainsWord(n, label));
				}))
				{
					m.defs.Add(d);
				}
			}
			if (m.defs.Count == 0)
			{
				throw new Fault("I can't find any " + GearNoun(kind) + " called '" + text + "'.");
			}
			return m;
		}

		private static GearMatch MatchGear(FormContext c, GearKind kind)
		{
			List<string> things = c.TextList("THINGS");
			if (things.Count == 0)
			{
				throw new Fault("The question did not say which " + GearNoun(kind) + " to look for.");
			}
			List<ThingDef> pool = DefDatabase<ThingDef>.AllDefsListForReading.Where((ThingDef d) => InGearPool(d, kind)).ToList();
			GearMatch all = new GearMatch();
			List<string> labels = new List<string>();
			foreach (string text in things)
			{
				GearMatch one = MatchGearWord(text, kind, pool);
				all.any = all.any || one.any;
				all.defs.UnionWith(one.defs);
				labels.Add(one.label);
			}
			all.label = JoinNames(labels.Distinct());
			return all;
		}

		// ---- describing a thing ----------------------------------------------------------------------------------------

		// Marks as the Gear tab draws them (ITab_Pawn_Gear.cs:206-212, 275-281).
		private static List<string> GearMarks(Pawn p, Thing t, bool inventory)
		{
			List<string> marks = new List<string>();
			Apparel apparel = t as Apparel;
			if (apparel != null && p.outfits != null && p.outfits.forcedHandler.IsForced(apparel))
			{
				marks.Add("forced");
			}
			bool locked = apparel != null && p.apparel != null && p.apparel.IsLocked(apparel);
			bool lodger = p.IsQuestLodger() && (inventory || !EquipmentUtility.QuestLodgerCanUnequip(t, p));
			bool destroyOnDrop = !inventory && p.kindDef != null && p.kindDef.destroyGearOnDrop;
			if (locked || lodger || destroyOnDrop)
			{
				marks.Add("locked");
			}
			return marks;
		}

		private static string DescribeGear(Pawn p, Thing t, bool inventory, bool withDetails)
		{
			string text = PlainText(t.LabelNoParenthesis);
			if (t.stackCount > 1)
			{
				text += " x" + t.stackCount;
			}
			if (!withDetails)
			{
				return text;
			}
			List<string> bits = new List<string>();
			QualityCategory quality;
			if (t.TryGetQuality(out quality))
			{
				bits.Add(quality.GetLabel());
			}
			if (t.def.useHitPoints && t.MaxHitPoints > 0)
			{
				bits.Add(Percent((float)t.HitPoints / t.MaxHitPoints) + " hit points");
			}
			bits.AddRange(GearMarks(p, t, inventory));
			return bits.Count > 0 ? text + " (" + string.Join(", ", bits.ToArray()) + ")" : text;
		}

		// ": armor sharp 20%, blunt 8%, heat 10%; insulation cold 40, heat 2 C; worn by a corpse" for one piece of
		// apparel; "" for anything else. Stats: StatDefOf.ArmorRating_Sharp / _Blunt / _Heat, Insulation_Cold /
		// _Heat (RimWorld\StatDefOf.cs:396-404); Apparel.WornByCorpse (RimWorld\Apparel.cs:27) is the "tainted" mark.
		private static string ApparelStats(Thing t)
		{
			Apparel apparel = t as Apparel;
			if (apparel == null)
			{
				return "";
			}
			List<string> parts = new List<string>();
			float sharp = t.GetStatValue(StatDefOf.ArmorRating_Sharp);
			float blunt = t.GetStatValue(StatDefOf.ArmorRating_Blunt);
			float heat = t.GetStatValue(StatDefOf.ArmorRating_Heat);
			if (sharp > 0f || blunt > 0f || heat > 0f)
			{
				parts.Add("armor sharp " + Percent(sharp) + ", blunt " + Percent(blunt) + ", heat " + Percent(heat));
			}
			float cold = t.GetStatValue(StatDefOf.Insulation_Cold);
			float hot = t.GetStatValue(StatDefOf.Insulation_Heat);
			if (cold != 0f || hot != 0f)
			{
				parts.Add("insulation cold " + Degrees(cold) + ", heat " + Degrees(hot) + " C");
			}
			if (apparel.WornByCorpse)
			{
				parts.Add("worn by a corpse (a mood penalty)");
			}
			return parts.Count > 0 ? ": " + string.Join("; ", parts.ToArray()) : "";
		}

		private static string Degrees(float value)
		{
			return value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
		}

		// ---- query gear ------------------------------------------------------------------------------------------------

		private static string QueryGearOf(FormContext c)
		{
			Pawn pawn = ResolveOnePawn(c, "PAWN");
			List<string> lines = new List<string>();
			lines.Add(pawn.LabelShort + "'s gear:");
			if (pawn.equipment == null && pawn.apparel == null && pawn.inventory == null)
			{
				throw new Fault(pawn.LabelShort + " has no gear tab in the game.");
			}
			List<Thing> held = GearOf(pawn, GearKind.Held);
			lines.Add(held.Count > 0
				? "Equipped: " + CapList(held.Select((Thing t) => DescribeGear(pawn, t, false, true)), 10) + "."
				: "Equipped: nothing (unarmed).");
			List<Thing> worn = GearOf(pawn, GearKind.Worn);
			if (worn.Count == 0)
			{
				lines.Add("Wearing: nothing.");
			}
			else
			{
				// User 2026-10-04: the stats of each piece, not only its name (what the item's info card shows).
				lines.Add("Wearing:");
				foreach (Thing t in worn.Take(20))
				{
					lines.Add("- " + DescribeGear(pawn, t, false, true) + ApparelStats(t));
				}
				if (worn.Count > 20)
				{
					lines.Add("and " + (worn.Count - 20) + " more.");
				}
			}
			// What the clothes add up to: the temperatures this pawn is comfortable in (the stats the Gear tab shows).
			lines.Add("Comfortable from " + Degrees(pawn.GetStatValue(StatDefOf.ComfyTemperatureMin)) + " to "
				+ Degrees(pawn.GetStatValue(StatDefOf.ComfyTemperatureMax)) + " C.");
			List<Thing> inventory = GearOf(pawn, GearKind.Carried);
			lines.Add(inventory.Count > 0
				? "Inventory: " + CapList(inventory.Select((Thing t) => DescribeGear(pawn, t, true, true)), 20) + "."
				: "Inventory: nothing.");
			return string.Join("\n", lines.ToArray());
		}

		// ---- who wears / holds / carries -------------------------------------------------------------------------------

		private static string GearVerb(GearKind kind, bool positive, bool thirdPerson)
		{
			string verb = kind == GearKind.Worn ? "wear" : kind == GearKind.Held ? "hold" : "carry";
			if (positive)
			{
				return thirdPerson ? (kind == GearKind.Carried ? "carries" : verb + "s") : (kind == GearKind.Carried ? "carry" : verb);
			}
			return "do not " + verb;
		}

		private static string WhoHas(FormContext c, GearKind kind, bool positive)
		{
			GearMatch match = MatchGear(c, kind);
			List<Pawn> colonists = c.map.mapPawns.FreeColonists.ToList();
			if (colonists.Count == 0)
			{
				return "There are no colonists.";
			}
			List<KeyValuePair<Pawn, List<Thing>>> hits = new List<KeyValuePair<Pawn, List<Thing>>>();
			List<Pawn> misses = new List<Pawn>();
			foreach (Pawn p in colonists)
			{
				List<Thing> found = GearOf(p, kind).Where((Thing t) => match.Matches(t)).ToList();
				if (found.Count > 0)
				{
					hits.Add(new KeyValuePair<Pawn, List<Thing>>(p, found));
				}
				else
				{
					misses.Add(p);
				}
			}
			string total = colonists.Count == 1 ? "colonist" : "colonists";
			if (positive)
			{
				if (hits.Count == 0)
				{
					return "Nobody " + GearVerb(kind, true, true) + " " + match.label + " (of " + colonists.Count + " " + total + ").";
				}
				List<string> lines = new List<string>();
				lines.Add(hits.Count + " of " + colonists.Count + " " + total + " " + GearVerb(kind, true, hits.Count == 1) + " " + match.label + ":");
				foreach (KeyValuePair<Pawn, List<Thing>> hit in hits.Take(QueryListCap))
				{
					lines.Add("- " + hit.Key.LabelShort + ": " + CapList(hit.Value.Select((Thing t) => DescribeGear(hit.Key, t, kind == GearKind.Carried, kind != GearKind.Carried)), 8));
				}
				if (hits.Count > QueryListCap)
				{
					lines.Add("and " + (hits.Count - QueryListCap) + " more.");
				}
				return string.Join("\n", lines.ToArray());
			}
			if (misses.Count == 0)
			{
				return "Everyone (" + colonists.Count + " " + total + ") " + GearVerb(kind, true, true) + " " + match.label + ".";
			}
			return misses.Count + " of " + colonists.Count + " " + total + " " + (misses.Count == 1 ? GearVerb(kind, false, false).Replace("do not", "does not") : GearVerb(kind, false, false))
				+ " " + match.label + ": " + CapList(misses.Select((Pawn p) => p.LabelShort), QueryListCap) + ".";
		}
	}
}
