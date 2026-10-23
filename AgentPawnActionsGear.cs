using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;
using Verse.AI;

namespace VerbalCommands
{
	// Agent 09, brief s5: equip, wear, pick up, drop. Every job is given the way the game gives it:
	//   equip    FloatMenuOptionProvider_Equip   (RimWorld\FloatMenuOptionProvider_Equip.cs:17-92)
	//   wear     FloatMenuOptionProvider_Wear    (RimWorld\FloatMenuOptionProvider_Wear.cs:15-74)
	//   pick up  FloatMenuOptionProvider_PickUpItem (RimWorld\FloatMenuOptionProvider_PickUpItem.cs:16-94)
	//   drop     the gear tab's drop button (RimWorld\ITab_Pawn_Gear.cs:196-262 DrawThingRow, :347-362 InterfaceDrop)
	// The matching of the player's word to a def copies FormGearQueries.cs:173-220 (MatchGearWord: label or plural,
	// else category label, else word part of a label), because those helpers are private to FormPlanner.
	public static partial class AgentPawnActions
	{
		private enum GearThing
		{
			Weapon,
			Apparel,
			Item
		}

		private enum GearHolder
		{
			Held,
			Worn,
			Carried
		}

		private static PlanReply DispatchGear(FormContext c)
		{
			switch (c.form)
			{
				case "equip":
					return PlanEquipWear(c, true);
				case "wear":
					return PlanEquipWear(c, false);
				case "pick up":
					return PlanPickUp(c);
				case "drop":
					return PlanDrop(c);
			}
			return null;
		}

		// ---- matching the player's word ---------------------------------------------------------------------

		private static string GearNounOf(GearThing kind)
		{
			return kind == GearThing.Weapon ? "weapon" : kind == GearThing.Apparel ? "apparel" : "item";
		}

		private static bool GearLabelMatchesWord(string label, string word)
		{
			return label == word || label + "s" == word || label + "es" == word || word + "s" == label || word + "es" == label
				|| (word.EndsWith("s") && label == word.Substring(0, word.Length - 1))
				|| (word.EndsWith("es") && label == word.Substring(0, word.Length - 2));
		}

		private static List<ThingDef> GearPoolOf(GearThing kind)
		{
			return DefDatabase<ThingDef>.AllDefsListForReading.Where(delegate (ThingDef d)
			{
				if (d.category != ThingCategory.Item || d.label.NullOrEmpty())
				{
					return false;
				}
				switch (kind)
				{
					case GearThing.Weapon:
						return d.IsWeapon || d.equipmentType != EquipmentType.None;
					case GearThing.Apparel:
						return d.IsApparel;
					default:
						return true;
				}
			}).ToList();
		}

		// The word without a leading "a", "an", "the", "some", normalised like every router word.
		private static string GearWordNorm(string text)
		{
			string n = FormPlanner.Norm(text ?? "");
			foreach (string article in new[] { "a ", "an ", "the ", "some " })
			{
				if (n.StartsWith(article))
				{
					n = n.Substring(article.Length).Trim();
					break;
				}
			}
			return n;
		}

		// Sets of defs the word can mean, most exact first: a generic word, a def's label or its plural, a category
		// label, a word that is part of a label (FormGearQueries.cs:122-220). The caller takes the first set that
		// has a matching thing, so "medicine" finds the plain medicine first and herbal medicine next.
		private static List<HashSet<ThingDef>> GearWordTiers(string text, GearThing kind, List<ThingDef> pool)
		{
			string n = GearWordNorm(text);
			if (n.Length == 0)
			{
				throw new Fault("The order did not say which " + GearNounOf(kind) + ".");
			}
			List<HashSet<ThingDef>> tiers = new List<HashSet<ThingDef>>();
			string word = n.StartsWith("any ") ? n.Substring(4) : n;
			bool anything = n == "anything" || n == "something" || n == "any" || n == "things" || n == "anything at all";
			if (anything
				|| (kind == GearThing.Weapon && (word == "weapon" || word == "weapons" || word == "equipment"))
				|| (kind == GearThing.Apparel && (word == "apparel" || word == "clothing" || word == "clothes" || word == "gear"))
				|| (kind == GearThing.Item && (word == "item" || word == "items")))
			{
				tiers.Add(new HashSet<ThingDef>(pool));
				return tiers;
			}
			if (kind == GearThing.Weapon && (word == "ranged weapon" || word == "ranged weapons" || word == "melee weapon" || word == "melee weapons"))
			{
				bool ranged = word.StartsWith("ranged");
				tiers.Add(new HashSet<ThingDef>(pool.Where((ThingDef d) => ranged ? d.IsRangedWeapon : d.IsMeleeWeapon)));
				return tiers;
			}
			tiers.Add(new HashSet<ThingDef>(pool.Where((ThingDef d) => GearLabelMatchesWord(FormPlanner.Norm(d.label), n))));
			HashSet<ThingDef> byCategory = new HashSet<ThingDef>();
			foreach (ThingCategoryDef cat in DefDatabase<ThingCategoryDef>.AllDefsListForReading)
			{
				if (!cat.label.NullOrEmpty() && GearLabelMatchesWord(FormPlanner.Norm(cat.label), n))
				{
					foreach (ThingDef d in cat.DescendantThingDefs.Where((ThingDef d) => pool.Contains(d)))
					{
						byCategory.Add(d);
					}
				}
			}
			tiers.Add(byCategory);
			tiers.Add(new HashSet<ThingDef>(pool.Where(delegate (ThingDef d)
			{
				string label = FormPlanner.Norm(d.label);
				return FormPlanner.ContainsWord(label, n) || (label.Length >= 3 && FormPlanner.ContainsWord(n, label));
			})));
			tiers.RemoveAll((HashSet<ThingDef> t) => t.Count == 0);
			if (tiers.Count == 0)
			{
				throw new Fault("I can't find any " + GearNounOf(kind) + " called '" + text.Trim() + "'.");
			}
			return tiers;
		}

		// ---- finding the thing on the ground -----------------------------------------------------------------

		// Things of the map that the word means: spawned (so not in anybody's hands), in view, and for the kind
		// asked (a weapon has CompEquippable, as the Equip option needs; apparel is an Apparel; an item is
		// EverHaulable, as the pick-up option needs). SPOT given: the match nearest that cell within 8 tiles; else
		// the match nearest the pawn. Forbidden things (IsForbidden(pawn)) are skipped; when only forbidden ones
		// match the order is refused.
		private static Thing FindOnGround(FormContext c, Pawn pawn, string word, GearThing kind, IntVec3? spot)
		{
			List<ThingDef> pool = GearPoolOf(kind);
			List<HashSet<ThingDef>> tiers = GearWordTiers(word, kind, pool);
			string shown = word.Trim();
			List<Thing> all = c.map.listerThings.AllThings.Where(delegate (Thing t)
			{
				if (!t.Spawned || t.Destroyed || t.def.category != ThingCategory.Item || !AgentPlace.Visible(c.map, t.Position))
				{
					return false;
				}
				switch (kind)
				{
					case GearThing.Weapon:
						return t.HasComp<CompEquippable>();
					case GearThing.Apparel:
						return t is Apparel;
					default:
						return t.def.EverHaulable;
				}
			}).ToList();
			IntVec3 from = spot ?? pawn.Position;
			Thing forbiddenNearest = null;
			foreach (HashSet<ThingDef> tier in tiers)
			{
				List<Thing> hits = all.Where((Thing t) => tier.Contains(t.def)).ToList();
				if (spot.HasValue)
				{
					hits = hits.Where((Thing t) => t.Position.DistanceToSquared(spot.Value) <= 64).ToList();
				}
				if (hits.Count == 0)
				{
					continue;
				}
				List<Thing> free = hits.Where((Thing t) => !t.IsForbidden(pawn)).ToList();
				if (free.Count > 0)
				{
					return free.OrderBy((Thing t) => t.Position.DistanceToSquared(from)).First();
				}
				if (forbiddenNearest == null)
				{
					forbiddenNearest = hits.OrderBy((Thing t) => t.Position.DistanceToSquared(from)).First();
				}
			}
			if (forbiddenNearest != null)
			{
				throw new Fault("The nearest " + shown + " (at " + AgentPlace.Describe(c.map, forbiddenNearest.Position) + ") is forbidden; unforbid it first.");
			}
			throw new Fault("No " + shown + " is in view" + (spot.HasValue ? " within 8 tiles of " + AgentPlace.Describe(c.map, spot.Value) : "") + ".");
		}

		private static string ThingName(Thing t)
		{
			return FormPlanner.PlainText(t.Label);
		}

		// For AgentReadback.Reads: "Dana: holds assault rifle; wears flak vest, pants; carries 2 herbal medicine".
		private static string GearLine(Pawn pawn)
		{
			Func<Thing, string> one = delegate (Thing t)
			{
				string name = FormPlanner.PlainText(t.LabelNoCount);
				return t.stackCount > 1 ? t.stackCount + " " + name : name;
			};
			List<string> held = pawn.equipment != null ? pawn.equipment.AllEquipmentListForReading.Cast<Thing>().Select(one).ToList() : new List<string>();
			List<string> worn = pawn.apparel != null ? pawn.apparel.WornApparel.Cast<Thing>().Select(one).ToList() : new List<string>();
			List<string> carried = pawn.inventory != null ? pawn.inventory.innerContainer.Select(one).ToList() : new List<string>();
			return pawn.LabelShort + ": holds " + (held.Count > 0 ? string.Join(", ", held.ToArray()) : "nothing")
				+ "; wears " + (worn.Count > 0 ? string.Join(", ", worn.ToArray()) : "nothing")
				+ "; carries " + (carried.Count > 0 ? string.Join(", ", carried.ToArray()) : "nothing");
		}

		private static void CheckGround(Thing thing, Pawn pawn)
		{
			string name = ThingName(thing);
			if (thing.Destroyed || !thing.Spawned)
			{
				throw new Fault("The " + name + " is gone.");
			}
			if (!AgentPlace.Visible(thing.Map, thing.Position))
			{
				throw new Fault("The " + name + " is out of view now.");
			}
			if (thing.IsForbidden(pawn))
			{
				throw new Fault("The " + name + " (at " + AgentPlace.Describe(thing.Map, thing.Position) + ") is forbidden now; unforbid it first.");
			}
		}

		// ---- equip, wear -------------------------------------------------------------------------------------

		private static PlanReply PlanEquipWear(FormContext c, bool equip)
		{
			Pawn pawn = FormPlanner.ResolveOnePawn(c, "PAWN");
			string word = c.Text(equip ? "WEAPON" : "APPAREL");
			if (string.IsNullOrWhiteSpace(word))
			{
				throw new Fault("The order did not say which " + (equip ? "weapon" : "apparel") + ".");
			}
			IntVec3? spot = c.Has("SPOT") ? Spot(c, "SPOT") : (IntVec3?)null;
			Thing thing = FindOnGround(c, pawn, word, equip ? GearThing.Weapon : GearThing.Apparel, spot);
			string name = ThingName(thing);
			string where = AgentPlace.Describe(c.map, thing.Position);
			Func<FloatMenuOptionProvider> provider = delegate
			{
				return equip ? (FloatMenuOptionProvider)new FloatMenuOptionProvider_Equip() : new FloatMenuOptionProvider_Wear();
			};
			Func<Pawn, FloatMenuOption> optionFor = (Pawn p) => Pick(RightClick(provider(), new List<Pawn> { p }, thing, thing.Position), (FloatMenuOption o) => true);
			PickDoer(c, "PAWN", optionFor, equip ? "equip that" : "wear that", thing.Position);

			string extra = "";
			if (equip)
			{
				// The option's action opens a window instead of a job in these two cases
				// (FloatMenuOptionProvider_Equip.cs:55-77); nothing is equipped without the player's answer there.
				if (EquipmentUtility.AlreadyBondedToWeapon(thing, pawn))
				{
					throw new Fault(pawn.LabelShort + " is already bonded to another bladelink weapon; the game asks you to confirm in a window, so do this one in the game.");
				}
				if (!EquipmentUtility.GetPersonaWeaponConfirmationText(thing, pawn).NullOrEmpty())
				{
					throw new Fault("The " + name + " is a bladelink weapon that bonds to whoever equips it; the game asks you to confirm in a window, so do this one in the game.");
				}
				ThingWithComps held = pawn.equipment.Primary;
				if (held != null && held != thing)
				{
					extra = "; the " + FormPlanner.PlainText(held.LabelNoParenthesis) + " it holds is dropped";
				}
			}
			else
			{
				// Pawn_ApparelTracker.Wear drops what the new apparel replaces (RimWorld\Pawn_ApparelTracker.cs:454-472).
				Apparel replaced = ApparelUtility.GetApparelReplacedByNewApparel(pawn, (Apparel)thing);
				if (replaced != null)
				{
					extra = "; the " + FormPlanner.PlainText(replaced.LabelNoParenthesis) + " it wears comes off and is dropped";
				}
			}

			Pawn capturedPawn = pawn;
			Thing capturedThing = thing;
			string doing = pawn.LabelShort + (equip ? " equips " : " wears ") + name;
			Plan plan = FormPlanner.NewPlan(equip ? "equip" : "wear");
			Write write = new Write();
			write.description = pawn.LabelShort + " picks up and " + (equip ? "equips " : "wears ") + name + " at " + where;
			write.apply = delegate
			{
				CheckGround(capturedThing, capturedPawn);
				FloatMenuOption option = optionFor(capturedPawn);
				if (option == null)
				{
					throw new Fault(capturedPawn.LabelShort + " can't " + (equip ? "equip " : "wear ") + name + " right now.");
				}
				Choose(option);
				FollowJob(capturedPawn, doing);
			};
			plan.writes.Add(write.Reads(() => GearLine(capturedPawn)));
			string line = pawn.LabelShort + " picks up and " + (equip ? "equips" : "wears") + " the " + name + " at " + where + extra + ".";
			return FormPlanner.Reply(plan, line, new List<Pawn> { pawn }, null);
		}

		// ---- pick up -----------------------------------------------------------------------------------------

		// The job the "pick up some" slider makes when the player confirms a count
		// (FloatMenuOptionProvider_PickUpItem.cs:73-85); the slider is a window, so the count the order gave is
		// passed here instead of the slider's.
		private static void TakeInventoryNow(Pawn pawn, Thing thing, int count)
		{
			thing.SetForbidden(false, false);
			Job job = JobMaker.MakeJob(JobDefOf.TakeInventory, thing);
			job.count = count;
			job.checkEncumbrance = true;
			job.takeInventoryDelay = 120;
			pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
		}

		private static PlanReply PlanPickUp(FormContext c)
		{
			Pawn pawn = FormPlanner.ResolveOnePawn(c, "PAWN");
			string word = c.Text("ITEM");
			if (string.IsNullOrWhiteSpace(word))
			{
				throw new Fault("The order did not say which item.");
			}
			int want = 1;
			if (c.Has("COUNT"))
			{
				double number;
				if (!double.TryParse(c.Text("COUNT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) || number < 1 || number != Math.Floor(number))
				{
					throw new Fault("The count must be a whole number, at least 1.");
				}
				want = (int)number;
			}
			IntVec3? spot = c.Has("SPOT") ? Spot(c, "SPOT") : (IntVec3?)null;
			Thing thing = FindOnGround(c, pawn, word, GearThing.Item, spot);
			string name = FormPlanner.PlainText(thing.LabelNoCount);
			string where = AgentPlace.Describe(c.map, thing.Position);
			Func<Pawn, List<FloatMenuOption>> optionsFor = (Pawn p) => RightClick(new FloatMenuOptionProvider_PickUpItem(), new List<Pawn> { p }, thing, thing.Position);
			List<FloatMenuOption> options = optionsFor(pawn);
			if (options.Count == 0)
			{
				// The provider yields nothing when the item can't be picked up, or at home for anything but drugs the
				// colonist's policy keeps (FloatMenuOptionProvider_PickUpItem.cs:24-27).
				throw new Fault(pawn.LabelShort + " can't pick up the " + name + " right now (the game offers 'pick up' only for items a colonist can carry, and at home only for drugs the drug policy keeps).");
			}
			FloatMenuOption enabled = Pick(options, (FloatMenuOption o) => !o.Disabled);
			if (enabled == null)
			{
				throw new Fault(Why(options[0]));
			}
			// A stack of one, or a limit of one, has the single "pick up one" option (PickUpItem.cs:40-49).
			int maxAllowed = PawnUtility.GetMaxAllowedToPickUp(pawn, thing.def);
			bool single = thing.stackCount == 1 || maxAllowed == 1;
			int n = 1;
			Plan plan = FormPlanner.NewPlan("pick up");
			if (single)
			{
				if (want > 1)
				{
					plan.notes.Add("Only 1 " + name + " can be picked up there; picking up 1.");
				}
			}
			else
			{
				int limit = Math.Min(maxAllowed, Math.Min(MassUtility.CountToPickUpUntilOverEncumbered(pawn, thing), thing.stackCount));
				n = Math.Min(want, limit);
				if (n < 1)
				{
					throw new Fault(pawn.LabelShort + " can't carry any more " + name + ".");
				}
				if (want > thing.stackCount)
				{
					plan.notes.Add("Only " + thing.stackCount + " " + name + " " + (thing.stackCount == 1 ? "is" : "are") + " there; picking up " + n + ".");
				}
				else if (want > n)
				{
					plan.notes.Add(pawn.LabelShort + " can carry only " + n + " of them" + (maxAllowed < want ? " (the game's limit for this item)" : "") + "; picking up " + n + ".");
				}
			}

			Pawn capturedPawn = pawn;
			Thing capturedThing = thing;
			int capturedCount = n;
			string doing = pawn.LabelShort + " picks up " + n + " " + name;
			Write write = new Write();
			write.description = pawn.LabelShort + " picks up " + n + " " + name + " at " + where;
			write.apply = delegate
			{
				CheckGround(capturedThing, capturedPawn);
				List<FloatMenuOption> fresh = optionsFor(capturedPawn);
				FloatMenuOption freshOption = Pick(fresh, (FloatMenuOption o) => !o.Disabled);
				if (freshOption == null)
				{
					throw new Fault(fresh.Count > 0 ? Why(fresh[0]) : capturedPawn.LabelShort + " can't pick up the " + name + " right now.");
				}
				if (single)
				{
					Choose(freshOption);
				}
				else
				{
					TakeInventoryNow(capturedPawn, capturedThing, Math.Min(capturedCount, capturedThing.stackCount));
				}
				FollowJob(capturedPawn, doing);
			};
			plan.writes.Add(write.Reads(() => GearLine(capturedPawn)));
			string line = pawn.LabelShort + " picks up " + n + " " + name + " at " + where + ".";
			return FormPlanner.Reply(plan, line, new List<Pawn> { pawn }, null);
		}

		// ---- drop --------------------------------------------------------------------------------------------

		private static List<Thing> HeldBy(Pawn pawn, GearHolder holder)
		{
			List<Thing> list = new List<Thing>();
			switch (holder)
			{
				case GearHolder.Held:
					if (pawn.equipment != null)
					{
						list.AddRange(pawn.equipment.AllEquipmentListForReading.Cast<Thing>());
					}
					break;
				case GearHolder.Worn:
					if (pawn.apparel != null)
					{
						list.AddRange(pawn.apparel.WornApparel.Cast<Thing>());
					}
					break;
				default:
					if (pawn.inventory != null)
					{
						list.AddRange(pawn.inventory.innerContainer);
					}
					break;
			}
			return list;
		}

		private static PlanReply PlanDrop(FormContext c)
		{
			Pawn pawn = FormPlanner.ResolveOnePawn(c, "PAWN");
			string word = c.Text("ITEM");
			if (string.IsNullOrWhiteSpace(word))
			{
				throw new Fault("The order did not say which item.");
			}
			List<HashSet<ThingDef>> tiers = GearWordTiers(word, GearThing.Item, GearPoolOf(GearThing.Item));
			Thing thing = null;
			GearHolder holder = GearHolder.Held;
			// Equipment first, then worn apparel, then the inventory (the gear tab's order, ITab_Pawn_Gear.cs:145-177).
			foreach (GearHolder h in new[] { GearHolder.Held, GearHolder.Worn, GearHolder.Carried })
			{
				List<Thing> things = HeldBy(pawn, h);
				foreach (HashSet<ThingDef> tier in tiers)
				{
					Thing found = things.FirstOrDefault((Thing t) => tier.Contains(t.def));
					if (found != null)
					{
						thing = found;
						break;
					}
				}
				if (thing != null)
				{
					holder = h;
					break;
				}
			}
			if (thing == null)
			{
				throw new Fault(pawn.LabelShort + " has no " + word.Trim() + ".");
			}
			if (!pawn.Spawned || pawn.Downed || pawn.Map == null)
			{
				throw new Fault(pawn.LabelShort + " can't drop anything right now.");
			}
			string name = FormPlanner.PlainText(thing.LabelNoCount);
			if (thing.stackCount > 1)
			{
				name = thing.stackCount + " " + name;
			}
			// The drop button is greyed out for these (ITab_Pawn_Gear.cs:206-212); InterfaceDrop does nothing for
			// an inventory item that is destroyed on drop (:358).
			Apparel apparel = thing as Apparel;
			if (apparel != null && holder == GearHolder.Worn && pawn.apparel.IsLocked(apparel))
			{
				throw new Fault(pawn.LabelShort + "'s " + name + " is locked on and can't be dropped.");
			}
			if (pawn.IsQuestLodger() && (holder == GearHolder.Carried || !EquipmentUtility.QuestLodgerCanUnequip(thing, pawn)))
			{
				throw new Fault(pawn.LabelShort + " is a quest lodger and can't drop that.");
			}
			if (holder != GearHolder.Carried && pawn.kindDef != null && pawn.kindDef.destroyGearOnDrop)
			{
				throw new Fault(pawn.LabelShort + "'s " + name + " can't be dropped.");
			}
			if (holder == GearHolder.Carried && thing.def.destroyOnDrop)
			{
				throw new Fault(pawn.LabelShort + "'s " + name + " can't be dropped.");
			}

			Pawn capturedPawn = pawn;
			Thing capturedThing = thing;
			GearHolder capturedHolder = holder;
			string at = AgentPlace.Describe(c.map, pawn.Position);
			string doing = pawn.LabelShort + " drops " + name;
			Plan plan = FormPlanner.NewPlan("drop");
			Write write = new Write();
			write.description = pawn.LabelShort + " drops " + name + " (" + (holder == GearHolder.Held ? "held" : holder == GearHolder.Worn ? "worn" : "carried") + ")";
			write.apply = delegate
			{
				if (!capturedPawn.Spawned || capturedPawn.Downed)
				{
					throw new Fault(capturedPawn.LabelShort + " can't drop anything right now.");
				}
				if (!HeldBy(capturedPawn, capturedHolder).Contains(capturedThing))
				{
					throw new Fault(capturedPawn.LabelShort + " no longer has the " + name + ".");
				}
				// ITab_Pawn_Gear.InterfaceDrop (:347-362), the same three branches.
				Action drop = delegate
				{
					ThingWithComps withComps = capturedThing as ThingWithComps;
					if (capturedThing is Apparel && capturedHolder == GearHolder.Worn)
					{
						capturedPawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.RemoveApparel, (Apparel)capturedThing), JobTag.Misc);
					}
					else if (withComps != null && capturedHolder == GearHolder.Held)
					{
						capturedPawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.DropEquipment, withComps), JobTag.Misc);
					}
					else
					{
						Thing dropped;
						if (!capturedPawn.inventory.innerContainer.TryDrop(capturedThing, capturedPawn.Position, capturedPawn.Map, ThingPlaceMode.Near, out dropped))
						{
							throw new Fault(capturedPawn.LabelShort + " could not drop the " + name + " here.");
						}
					}
				};
				// The tab asks first when a mechanitor would lose mechs (ITab_Pawn_Gear.cs:226-232): a window.
				if (ModsConfig.BiotechActive && MechanitorUtility.TryConfirmBandwidthLossFromDroppingThing(capturedPawn, capturedThing, drop))
				{
					throw new Fault("Dropping that would cost " + capturedPawn.LabelShort + " control of mechs; the game is asking you to confirm in a window, answer it there.");
				}
				drop();
				FollowJob(capturedPawn, doing);
			};
			plan.writes.Add(write.Reads(() => GearLine(capturedPawn)));
			string risk = holder == GearHolder.Worn
				? "It is left on the ground (a raider or animal may take it), and without it " + pawn.LabelShort + " is exposed to cold or heat and damage."
				: "It is left on the ground (a raider or animal may take it)" + (holder == GearHolder.Held ? ", and " + pawn.LabelShort + " is then unarmed." : ".");
			string line = "RISKY: " + pawn.LabelShort + " drops the " + name + " (" + (holder == GearHolder.Held ? "held" : holder == GearHolder.Worn ? "worn" : "carried") + ") at " + at + ". " + risk;
			return FormPlanner.Reply(plan, line, new List<Pawn> { pawn }, null);
		}
	}
}
