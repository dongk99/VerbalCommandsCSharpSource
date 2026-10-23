using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Brief 26: questions about the colony (host function query, contract 26). One dispatcher by form name
	// (Query); every answer is read from the game and nothing is written (MAIN THREAD ONLY, RouterGame moves
	// the call here). The text is plain words, several lines separated by a newline.
	//   query count         how much of a thing the colony has: ResourceCounter.GetCount (Verse\Map.cs:81,
	//                       RimWorld\ResourceCounter.cs:75). That counter only holds things in storage
	//                       (SlotGroup.HeldThings, ResourceCounter.cs:131-142), skips things that are not fresh or
	//                       are under fog (ShouldCount, :157-168) and gives 0 for defs with resourceReadoutPriority
	//                       Uncounted (ThingDef.CountAsResource, Verse\ThingDef.cs:657); for those the answer is
	//                       counted by hand: in storage, and on the floor of the home area.
	//   query relations     Pawn_RelationsTracker.RelatedPawns (Pawn_RelationsTracker.cs:258) and
	//                       PawnRelationUtility.GetRelations (PawnRelationUtility.cs:11), the labels the social tab
	//                       shows (SocialCardUtility.cs:1050: PawnRelationDef.GetGenderSpecificLabel of the other pawn)
	//   query group         the policy's own data: ApparelPolicy.filter, FoodPolicy.filter, DrugPolicy entries
	//                       (DrugPolicyEntry), ReadingPolicy.defFilter / effectFilter; medicine is
	//                       Pawn_PlayerSettings.medCare; weapon groups are the mod's GroupRule
	//   query pawn groups   what one pawn has of each of those, and its area (Pawn_PlayerSettings
	//                       .AreaRestrictionInPawnCurrentMap, null = anywhere)
	// The pawn trackers' CurrentXPolicy getters write the default policy into the pawn when it has none
	// (Pawn_OutfitTracker.cs:21-24 and the three like it), so a question reads the private field instead.
	public static partial class FormPlanner
	{
		// A list in a sentence never runs past this many names.
		private const int QueryListCap = 15;

		internal static QueryReply Query(Map map, string form, Dictionary<string, object> slots)
		{
			PlanRequest req = new PlanRequest();
			req.form = form;
			req.slots = slots ?? new Dictionary<string, object>();
			FormContext c = new FormContext(map, req);
			try
			{
				if (map == null || Find.Maps == null || !Find.Maps.Contains(map))
				{
					return QueryReply.Fail("The map this question was for is no longer loaded.");
				}
				switch (c.form)
				{
					case "query count":
						return QueryReply.Ok(QueryCount(c));
					case "query relations":
						return QueryReply.Ok(QueryRelations(c));
					case "query group":
						return QueryReply.Ok(QueryGroup(c));
					case "query pawn groups":
						return QueryReply.Ok(QueryPawnGroups(c));
					// User 2026-10-13: a control group (hotkey slot) by its number (FormControlGroups.cs).
					case "query control group":
						return QueryReply.Ok(ControlGroupText(c));
					// Agent 06.
					case "query zones":
						return QueryReply.Ok(AgentZones.QueryZones(map));
					case "query areas":
						return QueryReply.Ok(AgentAreas.QueryAreas(map));
					// Tip injection: "i need tips on NAME." (TipInjector.cs).
					case "query tips":
						return TipInjector.Query(c.Text("NAME"));
					// User 2026-10-15: "what does BREAK do." and "list breaks." (MentalBreakTips.cs).
					case "query mental break":
						return MentalBreakTips.QueryBreak(c.Text("BREAK"));
					case "query mental breaks":
						return MentalBreakTips.QueryList();
					// User 2026-10-17: "list buildings." and "list CATEGORY." (AgentBuild.cs).
					case "query buildings":
						return AgentBuild.QueryBuildings(c.Text("CATEGORY"));
					// User 2026-10-17: "list bill PAWN has." and "what can PAWN create." (FormBills.cs).
					case "query pawn bills":
						return QueryPawnBills(c);
					case "query pawn create":
						return QueryPawnCreate(c);
					// User 2026-10-17: "show ROOM stats." (RoomStats.cs).
					case "query room stats":
						return QueryRoomStats(c);
				}
				// Agent 05: read-back questions (AgentReadbackPawns.cs, AgentReadbackStorage.cs, AgentReadbackBuildings.cs).
				QueryReply readback = QueryReadbackPawns(c) ?? QueryReadbackStorage(c) ?? QueryReadbackBuildings(c);
				if (readback != null)
				{
					return readback;
				}
				// Brief 30: what pawns wear, hold and carry (FormGearQueries.cs).
					QueryReply gear = QueryGear(c);
					if (gear != null)
					{
						return gear;
					}
				// Agent 04: status, threats, food, power, temperature, mood (AgentColonyReads*.cs).
				QueryReply colonyReads = ReadsQuery(c);
				if (colonyReads != null)
				{
					return colonyReads;
				}
				// Agent 07: what is designated, and the wild animals in view (AgentDesignationQueries.cs).
				QueryReply designations = AgentDesignationQueries.Query(c);
				if (designations != null)
				{
					return designations;
				}
				// Who can be interacted with, and the open trade window (AgentInteract.cs).
				QueryReply interact = AgentInteract.Query(c);
				if (interact != null)
				{
					return interact;
				}
				// Agent 03: show map (AgentMap.cs); where is, where is nearest, where are / list ... on map (AgentFind.cs).
				// 2026-10-14: a picture of the colony (AgentPicture.cs).
				QueryReply place = AgentPicture.Query(c) ?? AgentMap.Query(c) ?? AgentFind.Query(c);
				if (place != null)
				{
					return place;
				}
				// Agent 02: the clock, event history, what orders started (AgentTime / AgentEvents / AgentFollow).
				switch (c.form)
				{
					case "query speed":
						return AgentTime.QuerySpeed(c);
					case "query events":
						return AgentEvents.QueryEvents(c);
					case "query pending":
						return AgentFollow.QueryPending(c);
				}
					// Brief 27: the wave 1 questions (FormWave1Queries.cs).
				QueryReply wave1 = QueryWave1(c);
				if (wave1 != null)
				{
					return wave1;
				}
				return QueryReply.Fail("The mod can't answer that yet: '" + c.form + "' is not built yet.");
			}
			catch (Fault fault)
			{
				return QueryReply.Fail(fault.Message);
			}
		}

		private static string CapList(IEnumerable<string> items, int cap)
		{
			List<string> list = items.ToList();
			if (list.Count <= cap)
			{
				return JoinNames(list);
			}
			return string.Join(", ", list.Take(cap).ToArray()) + " and " + (list.Count - cap) + " more";
		}

		private static string Percent(float v)
		{
			return Math.Round(v * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
		}

		// ---- query count -----------------------------------------------------------------------------------------

		private static string QueryCount(FormContext c)
		{
			List<string> things = c.TextList("THINGS");
			if (things.Count == 0)
			{
				throw new Fault("The question did not say which thing to count.");
			}
			List<string> lines = new List<string>();
			bool anyCounted = false;
			foreach (string text in things)
			{
				bool counted;
				lines.Add(CountLine(c.map, text, out counted));
				anyCounted = anyCounted || counted;
			}
			if (anyCounted)
			{
				lines.Add("(The game's resource count: things in storage that are not spoiled and not under fog.)");
			}
			return string.Join("\n", lines.ToArray());
		}

		private static string CountLine(Map map, string text, out bool counted)
		{
			counted = false;
			string n = Norm(text);
			if (n.Length == 0)
			{
				throw new Fault("The question did not say which thing to count.");
			}
			List<ThingDef> items = DefDatabase<ThingDef>.AllDefsListForReading
				.Where((ThingDef d) => d.category == ThingCategory.Item && !d.label.NullOrEmpty()).ToList();

			// A def whose label is the player's word, or its plural.
			List<ThingDef> exact = items.Where((ThingDef d) => LabelMatchesWord(Norm(d.label), n)).ToList();
			if (exact.Count > 1)
			{
				List<ThingDef> resources = exact.Where((ThingDef d) => d.CountAsResource).ToList();
				if (resources.Count == 1)
				{
					exact = resources;
				}
			}
			if (exact.Count == 1)
			{
				return CountOneDef(map, exact[0], out counted);
			}
			if (exact.Count > 1)
			{
				throw new Fault("'" + text + "' could mean several things: " + CapList(exact.Select((ThingDef d) => d.label).Distinct(), 8) + ". Say which.");
			}

			// A category of things ("meals", "raw food"), by the label.
			List<ThingCategoryDef> cats = new List<ThingCategoryDef>();
			foreach (ThingCategoryDef cat in DefDatabase<ThingCategoryDef>.AllDefsListForReading)
			{
				if (!cat.label.NullOrEmpty() && LabelMatchesWord(Norm(cat.label), n) && cat.DescendantThingDefs.Any((ThingDef d) => d.category == ThingCategory.Item))
				{
					cats.Add(cat);
				}
			}
			if (cats.Count == 1)
			{
				return CountCategory(map, cats[0], out counted);
			}
			if (cats.Count > 1)
			{
				throw new Fault("'" + text + "' could mean several kinds of things: " + CapList(cats.Select((ThingCategoryDef d) => d.label).Distinct(), 8) + ". Say which.");
			}

			// The player's word is part of a label ("wood" for "wood log") or the label is part of the words.
			List<ThingDef> partial = items.Where((ThingDef d) =>
			{
				string label = Norm(d.label);
				return ContainsWord(label, n) || (label.Length >= 3 && ContainsWord(n, label));
			}).ToList();
			if (partial.Count > 1)
			{
				List<ThingDef> resources = partial.Where((ThingDef d) => d.CountAsResource).ToList();
				if (resources.Count == 1)
				{
					partial = resources;
				}
			}
			if (partial.Count == 1)
			{
				return CountOneDef(map, partial[0], out counted);
			}
			if (partial.Count > 1)
			{
				throw new Fault("'" + text + "' could mean several things: " + CapList(partial.Select((ThingDef d) => d.label).Distinct(), 8) + ". Say which.");
			}
			throw new Fault("I can't find anything called '" + text + "' to count.");
		}

		private static bool LabelMatchesWord(string label, string word)
		{
			return label == word || label + "s" == word || label + "es" == word || word + "s" == label || word + "es" == label
				|| (word.EndsWith("s") && label == word.Substring(0, word.Length - 1))
				|| (word.EndsWith("es") && label == word.Substring(0, word.Length - 2));
		}

		private static string CountOneDef(Map map, ThingDef def, out bool counted)
		{
			string name = UpperFirst(def.label);
			if (def.CountAsResource)
			{
				counted = true;
				return name + ": " + map.resourceCounter.GetCount(def).ToString("N0", CultureInfo.InvariantCulture) + ForbiddenText(map, new[] { def }) + ".";
			}
			counted = false;
			int stored;
			int floor;
			CountByHand(map, new HashSet<ThingDef> { def }, out stored, out floor);
			return name + ": " + stored.ToString("N0", CultureInfo.InvariantCulture) + " in storage and " + floor.ToString("N0", CultureInfo.InvariantCulture)
				+ " on the floor in the home area (the game's resource count does not include these).";
		}

		private static string CountCategory(Map map, ThingCategoryDef cat, out bool counted)
		{
			List<ThingDef> defs = cat.DescendantThingDefs.Where((ThingDef d) => d.category == ThingCategory.Item).ToList();
			int total = 0;
			foreach (ThingDef d in defs)
			{
				if (d.CountAsResource)
				{
					total += map.resourceCounter.GetCount(d);
				}
			}
			counted = defs.Any((ThingDef d) => d.CountAsResource);
			string name = UpperFirst(cat.label);
			HashSet<ThingDef> uncounted = new HashSet<ThingDef>(defs.Where((ThingDef d) => !d.CountAsResource));
			int stored = 0;
			int floor = 0;
			if (uncounted.Count > 0)
			{
				CountByHand(map, uncounted, out stored, out floor);
			}
			string text = name + " (all kinds): ";
			if (counted)
			{
				text += total.ToString("N0", CultureInfo.InvariantCulture);
				text += ForbiddenText(map, defs.Where((ThingDef d) => d.CountAsResource));
				if (stored + floor > 0)
				{
					text += ", plus " + stored.ToString("N0", CultureInfo.InvariantCulture) + " in storage and " + floor.ToString("N0", CultureInfo.InvariantCulture)
						+ " on the floor in the home area that the resource count does not include";
				}
				return text + ".";
			}
			return text + stored.ToString("N0", CultureInfo.InvariantCulture) + " in storage and " + floor.ToString("N0", CultureInfo.InvariantCulture)
				+ " on the floor in the home area (the game's resource count does not include these).";
		}

		// " (plus 225 forbidden lying around, not counted until unforbidden)" or "": the game's count leaves forbidden
		// things out, so a fresh landing's pod cargo read as 0 (AgentBuild.ForbiddenLying).
		private static string ForbiddenText(Map map, IEnumerable<ThingDef> defs)
		{
			int n = 0;
			foreach (ThingDef d in defs)
			{
				n += AgentBuild.ForbiddenLying(map, d);
			}
			return n > 0 ? " (plus " + n.ToString("N0", CultureInfo.InvariantCulture) + " forbidden lying around, not counted until unforbidden)" : "";
		}

		// Things of these defs lying in a storage cell, and (not in storage) on the floor inside the home area.
		// ThingRequestGroup.HaulableEver: every item that can be carried (Verse\ListerThings.cs:37).
		private static void CountByHand(Map map, HashSet<ThingDef> defs, out int stored, out int floor)
		{
			stored = 0;
			floor = 0;
			Area home = map.areaManager.Home;
			foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
			{
				if (!t.Spawned || !defs.Contains(t.def))
				{
					continue;
				}
				if (t.GetSlotGroup() != null)
				{
					stored += t.stackCount;
				}
				else if (home != null && home[t.Position])
				{
					floor += t.stackCount;
				}
			}
		}

		// ---- query relations -------------------------------------------------------------------------------------

		// What "relations" lists: family and partners and bonds, not cousins and in-laws.
		private static bool IsCloseRelation(PawnRelationDef d)
		{
			return d == PawnRelationDefOf.Spouse || d == PawnRelationDefOf.Fiance || d == PawnRelationDefOf.Lover
				|| d == PawnRelationDefOf.ExSpouse || d == PawnRelationDefOf.ExLover || d == PawnRelationDefOf.Parent
				|| d == PawnRelationDefOf.ParentBirth || d == PawnRelationDefOf.Child || d == PawnRelationDefOf.Sibling
				|| d == PawnRelationDefOf.HalfSibling || d == PawnRelationDefOf.Bond || d == PawnRelationDefOf.Overseer;
		}

		private static PawnRelationDef FindRelationDef(string text)
		{
			string n = Norm(text);
			// The words players use, before the game's labels.
			switch (n)
			{
				case "married":
				case "marry":
				case "wed":
				case "spouse":
				case "husband":
				case "wife":
					return PawnRelationDefOf.Spouse;
				case "engaged":
				case "fiance":
				case "fiancee":
					return PawnRelationDefOf.Fiance;
				case "dating":
				case "together":
				case "partner":
				case "lover":
				case "in a relationship":
					return PawnRelationDefOf.Lover;
			}
			foreach (PawnRelationDef def in DefDatabase<PawnRelationDef>.AllDefsListForReading)
			{
				if (Norm(def.label) == n || Norm(def.labelFemale) == n || Norm(def.defName) == n
					|| Norm(def.label) + "s" == n || (!def.labelFemale.NullOrEmpty() && Norm(def.labelFemale) + "s" == n))
				{
					return def;
				}
			}
			throw new Fault("I don't know the relation '" + text + "'. Some are " + JoinNames(DefDatabase<PawnRelationDef>.AllDefsListForReading
				.Where(IsCloseRelation).Select((PawnRelationDef d) => d.label).Distinct().Take(8)) + ".");
		}

		private static string PawnNameForAnswer(Pawn p)
		{
			return p.LabelShort + (p.Dead ? " (dead)" : "");
		}

		private static string QueryRelations(FormContext c)
		{
			Pawn pawn = ResolveOnePawn(c, "PAWN");
			string relationText = c.Text("RELATION");
			if (pawn.relations == null || !pawn.RaceProps.IsFlesh)
			{
				throw new Fault(pawn.LabelShort + " has no relations in the game.");
			}
			List<KeyValuePair<Pawn, PawnRelationDef>> found = new List<KeyValuePair<Pawn, PawnRelationDef>>();
			foreach (Pawn other in pawn.relations.RelatedPawns.ToList())
			{
				foreach (PawnRelationDef def in pawn.GetRelations(other))
				{
					found.Add(new KeyValuePair<Pawn, PawnRelationDef>(other, def));
				}
			}

			if (!string.IsNullOrWhiteSpace(relationText))
			{
				PawnRelationDef wanted = FindRelationDef(relationText);
				List<KeyValuePair<Pawn, PawnRelationDef>> matches = found.Where((KeyValuePair<Pawn, PawnRelationDef> kv) => kv.Value == wanted).ToList();
				if (matches.Count == 0)
				{
					return wanted == PawnRelationDefOf.Spouse
						? "No. " + pawn.LabelShort + " is not married."
						: "No. " + pawn.LabelShort + " has no " + wanted.label + ".";
				}
				List<string> parts = new List<string>();
				foreach (KeyValuePair<Pawn, PawnRelationDef> kv in matches)
				{
					parts.Add(wanted == PawnRelationDefOf.Spouse
						? pawn.LabelShort + " is married to " + PawnNameForAnswer(kv.Key) + "."
						: PawnNameForAnswer(kv.Key) + " is " + pawn.LabelShort + "'s " + wanted.GetGenderSpecificLabel(kv.Key).ToLowerInvariant() + ".");
				}
				return "Yes. " + string.Join(" ", parts.ToArray());
			}

			List<Pawn> others = new List<Pawn>();
			foreach (KeyValuePair<Pawn, PawnRelationDef> kv in found)
			{
				if (IsCloseRelation(kv.Value) && !others.Contains(kv.Key))
				{
					others.Add(kv.Key);
				}
			}
			if (others.Count == 0)
			{
				return pawn.LabelShort + " has no family, partners or bonds the game lists.";
			}
			List<string> entries = new List<string>();
			foreach (Pawn other in others)
			{
				string labels = string.Join(", ", found.Where((KeyValuePair<Pawn, PawnRelationDef> kv) => kv.Key == other && IsCloseRelation(kv.Value))
					.Select((KeyValuePair<Pawn, PawnRelationDef> kv) => kv.Value.GetGenderSpecificLabel(other).ToLowerInvariant()).ToArray());
				entries.Add(PawnNameForAnswer(other) + " (" + labels + ")");
			}
			return pawn.LabelShort + "'s relations: " + CapList(entries, 30) + ".";
		}

		// ---- policies of a pawn (read without the getters that fill in a default) ---------------------------------

		private static object PrivateField(object owner, string field)
		{
			FieldInfo info = owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			return info != null ? info.GetValue(owner) : null;
		}

		// The pawn's own policy, or the database's first one (what the game would give it) when it has none yet.
		private static T PolicyOf<T>(object tracker, string field, IList<T> all) where T : Policy
		{
			if (tracker == null)
			{
				return null;
			}
			T own = PrivateField(tracker, field) as T;
			return own ?? (all.Count > 0 ? all[0] : null);
		}

		private static ApparelPolicy ApparelPolicyOf(Pawn p)
		{
			return PolicyOf(p.outfits, "curApparelPolicy", Current.Game.outfitDatabase.AllOutfits);
		}

		private static FoodPolicy FoodPolicyOf(Pawn p)
		{
			return PolicyOf(p.foodRestriction, "curPolicy", Current.Game.foodRestrictionDatabase.AllFoodRestrictions);
		}

		private static DrugPolicy DrugPolicyOf(Pawn p)
		{
			return PolicyOf(p.drugs, "curPolicy", Current.Game.drugPolicyDatabase.AllPolicies);
		}

		private static ReadingPolicy ReadingPolicyOf(Pawn p)
		{
			return PolicyOf(p.reading, "curPolicy", Current.Game.readingPolicyDatabase.AllReadingPolicies);
		}

		private static string PawnsLine(IEnumerable<Pawn> pawns, string none)
		{
			List<string> names = pawns.Select((Pawn p) => p.LabelShort).ToList();
			return names.Count == 0 ? none : "Pawns in it: " + CapList(names, QueryListCap) + ".";
		}

		// ---- query group -----------------------------------------------------------------------------------------

		private static string QueryGroup(FormContext c)
		{
			string kind = Norm(c.Text("KIND") ?? "");
			string name = c.Text("NAME") ?? "";
			List<Pawn> colonists = c.map.mapPawns.FreeColonists.ToList();
			switch (kind)
			{
				case "clothing":
				case "apparel":
				{
					ApparelPolicy policy = (ApparelPolicy)FindPolicy(Current.Game.outfitDatabase.AllOutfits.Cast<Policy>().ToList(), name, "clothing");
					return "Clothing group '" + policy.label + "' allows " + FilterText(policy.filter, (ThingDef d) => d.IsApparel, "clothing", true) + "\n"
						+ PawnsLine(colonists.Where((Pawn p) => ApparelPolicyOf(p) == policy), "Nobody is in it.");
				}
				case "food":
				{
					FoodPolicy policy = (FoodPolicy)FindPolicy(Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Cast<Policy>().ToList(), name, "food");
					return "Food group '" + policy.label + "' allows " + FilterText(policy.filter, (ThingDef d) => d.IsNutritionGivingIngestible, "food", false) + "\n"
						+ PawnsLine(colonists.Where((Pawn p) => FoodPolicyOf(p) == policy), "Nobody is in it.");
				}
				case "drug":
				{
					DrugPolicy policy = (DrugPolicy)FindPolicy(Current.Game.drugPolicyDatabase.AllPolicies.Cast<Policy>().ToList(), name, "drug");
					return DrugText(policy) + "\n" + PawnsLine(colonists.Where((Pawn p) => DrugPolicyOf(p) == policy), "Nobody is in it.");
				}
				case "reading":
				{
					ReadingPolicy policy = (ReadingPolicy)FindPolicy(Current.Game.readingPolicyDatabase.AllReadingPolicies.Cast<Policy>().ToList(), name, "reading");
					return ReadingText(policy) + "\n" + PawnsLine(colonists.Where((Pawn p) => ReadingPolicyOf(p) == policy), "Nobody is in it.");
				}
				case "medicine":
				case "medical":
				case "medical care":
					return MedicineText(c.map, ParseMedicalCare(name), colonists);
				case "weapon":
				case "weapons":
					return WeaponGroupText(name, colonists);
			}
			throw new Fault("'" + kind + "' is not a kind of group. The kinds are clothing, food, drug, reading, medicine and weapon.");
		}

		// What a clothing, food or reading filter lets through, short: all, none, a few names, or a count with
		// the biggest categories. withQuality: apparel also has hit points and quality ranges.
		private static string FilterText(ThingFilter filter, Func<ThingDef, bool> belongs, string noun, bool withQuality)
		{
			List<ThingDef> all = DefDatabase<ThingDef>.AllDefsListForReading
				.Where((ThingDef d) => belongs(d) && !d.label.NullOrEmpty() && !d.thingCategories.NullOrEmpty()).ToList();
			HashSet<ThingDef> allowed = new HashSet<ThingDef>(filter.AllowedThingDefs.Where((ThingDef d) => belongs(d) && all.Contains(d)));
			string text;
			if (allowed.Count == 0)
			{
				text = "no " + noun + " at all.";
			}
			else if (allowed.Count == all.Count)
			{
				text = "all " + noun + " (every one of the " + all.Count + " kinds).";
			}
			else if (allowed.Count <= 10)
			{
				text = "only " + JoinNames(allowed.Select((ThingDef d) => d.label).OrderBy((string s) => s)) + ".";
			}
			else if (all.Count - allowed.Count <= 10)
			{
				text = "all " + noun + " except " + JoinNames(all.Where((ThingDef d) => !allowed.Contains(d)).Select((ThingDef d) => d.label).OrderBy((string s) => s)) + ".";
			}
			else
			{
				List<string> top = allowed.GroupBy((ThingDef d) => d.thingCategories[0])
					.OrderByDescending((IGrouping<ThingCategoryDef, ThingDef> g) => g.Count())
					.Take(5)
					.Select((IGrouping<ThingCategoryDef, ThingDef> g) => g.Key.label + " (" + g.Count() + " of " + all.Count((ThingDef d) => d.thingCategories[0] == g.Key) + ")")
					.ToList();
				text = allowed.Count + " of " + all.Count + " kinds of " + noun + ". Most of them: " + JoinNames(top) + ".";
			}
			if (withQuality && allowed.Count > 0)
			{
				List<string> limits = new List<string>();
				FloatRange hp = filter.AllowedHitPointsPercents;
				if (hp.min > 0.001f || hp.max < 0.999f)
				{
					limits.Add("hit points " + Percent(hp.min) + " to " + Percent(hp.max));
				}
				QualityRange quality = filter.AllowedQualityLevels;
				if (quality.min != QualityCategory.Awful || quality.max != QualityCategory.Legendary)
				{
					limits.Add("quality " + quality.min.GetLabel() + " to " + quality.max.GetLabel());
				}
				if (limits.Count > 0)
				{
					text += " Only " + JoinNames(limits) + ".";
				}
			}
			return text;
		}

		private static string FrequencyText(float days)
		{
			if (Math.Abs(days - 1f) < 0.001f)
			{
				return "every day";
			}
			return days < 1f
				? (1f / days).ToString("0.##", CultureInfo.InvariantCulture) + " times a day"
				: "every " + days.ToString("0.##", CultureInfo.InvariantCulture) + " days";
		}

		// DrugPolicyEntry: allowScheduled + daysFrequency + the mood / recreation limits, allowedForAddiction,
		// allowedForJoy (RimWorld\Dialog_ManageDrugPolicies.cs:190-210).
		private static string DrugText(DrugPolicy policy)
		{
			List<string> scheduled = new List<string>();
			List<string> addicted = new List<string>();
			List<string> joy = new List<string>();
			for (int i = 0; i < policy.Count; i++)
			{
				DrugPolicyEntry e = policy[i];
				if (e.drug == null)
				{
					continue;
				}
				if (e.allowScheduled)
				{
					string s = e.drug.label + " " + FrequencyText(e.daysFrequency);
					if (e.onlyIfMoodBelow < 1f)
					{
						s += " when mood is below " + Percent(e.onlyIfMoodBelow);
					}
					if (e.onlyIfJoyBelow < 1f)
					{
						s += " when recreation is below " + Percent(e.onlyIfJoyBelow);
					}
					scheduled.Add(s);
				}
				if (e.drug.IsAddictiveDrug && e.allowedForAddiction)
				{
					addicted.Add(e.drug.label);
				}
				if (e.drug.IsPleasureDrug && e.allowedForJoy)
				{
					joy.Add(e.drug.label);
				}
			}
			return "Drug group '" + policy.label + "':\n"
				+ "Taken on a schedule: " + (scheduled.Count > 0 ? CapList(scheduled, 12) : "nothing") + ".\n"
				+ "Taken for recreation: " + (joy.Count > 0 ? CapList(joy, 12) : "nothing") + ".\n"
				+ "Taken when addicted: " + (addicted.Count > 0 ? CapList(addicted, 12) : "nothing") + ".";
		}

		// ReadingPolicy.defFilter (which books) and effectFilter (book effects, special filters under
		// ThingCategoryDefOf.BookEffects, RimWorld\ReadingPolicy.cs:21).
		private static string ReadingText(ReadingPolicy policy)
		{
			string text = "Reading group '" + policy.label + "' allows " + FilterText(policy.defFilter, (ThingDef d) => d.HasComp<CompBook>(), "books", false);
			List<string> blocked = DefDatabase<SpecialThingFilterDef>.AllDefsListForReading
				.Where((SpecialThingFilterDef s) => s.parentCategory == ThingCategoryDefOf.BookEffects && !policy.effectFilter.Allows(s))
				.Select((SpecialThingFilterDef s) => s.label).ToList();
			if (blocked.Count > 0)
			{
				text += "\nBooks with these effects are not allowed: " + CapList(blocked, 10) + ".";
			}
			FloatRange chance = policy.effectFilter.AllowedMentalBreakChance;
			if (chance.min > 0.001f || chance.max < 0.999f)
			{
				text += "\nOnly books with a mental break chance of " + Percent(chance.min) + " to " + Percent(chance.max) + ".";
			}
			return text;
		}

		// MedicalCareCategory (RimWorld\MedicalCareUtility.cs:68-84): which medicines the level lets a doctor use.
		private static string MedicineText(Map map, MedicalCareCategory level, List<Pawn> colonists)
		{
			string means;
			switch (level)
			{
				case MedicalCareCategory.NoCare:
					means = "no treatment at all";
					break;
				case MedicalCareCategory.NoMeds:
					means = "treatment by a doctor, without any medicine";
					break;
				default:
					means = "treatment with " + JoinNames(DefDatabase<ThingDef>.AllDefsListForReading
						.Where((ThingDef d) => d.IsMedicine && d.category == ThingCategory.Item && !d.label.NullOrEmpty() && level.AllowsMedicine(d))
						.Select((ThingDef d) => d.label));
					break;
			}
			List<Pawn> mine = colonists.Where((Pawn p) => p.playerSettings != null && p.playerSettings.medCare == level).ToList();
			List<Pawn> animals = map.mapPawns.SpawnedColonyAnimals.ToList()
				.Where((Pawn p) => p.playerSettings != null && p.playerSettings.medCare == level).ToList();
			string text = "Medical care '" + level.GetLabel() + "' means " + means + ".\n"
				+ (mine.Count > 0 ? "Colonists with it: " + CapList(mine.Select((Pawn p) => p.LabelShort), QueryListCap) + "." : "No colonist has it.");
			if (animals.Count > 0)
			{
				text += "\nAnimals with it: " + CapList(animals.Select((Pawn p) => p.LabelShort), QueryListCap) + ".";
			}
			return text;
		}

		private static string WeaponRuleText(GroupRule rule)
		{
			// User 2026-10-13: a names-only or skill control group has no weapon rule to tell.
			if (rule.membersOnly)
			{
				return "only the colonists named for it";
			}
			if (!string.IsNullOrEmpty(rule.skill))
			{
				return "everyone with " + rule.SkillText();
			}
			List<string> parts = new List<string>();
			parts.Add(rule.weaponClass == "melee" ? "melee weapons" : rule.weaponClass == "ranged" ? "ranged weapons" : "any weapon");
			if (rule.minRange > 0f)
			{
				parts.Add("range at least " + rule.minRange.ToString("0.#", CultureInfo.InvariantCulture));
			}
			if (rule.maxRange > 0f)
			{
				parts.Add("range at most " + rule.maxRange.ToString("0.#", CultureInfo.InvariantCulture));
			}
			if (rule.allowedWeapons != null && rule.allowedWeapons.Count > 0)
			{
				parts.Add("only " + CapList(rule.allowedWeapons.Select((string d) => DefDatabase<ThingDef>.GetNamedSilentFail(d) != null ? DefDatabase<ThingDef>.GetNamedSilentFail(d).label : d), 10));
			}
			if (rule.includeUnarmed)
			{
				parts.Add("also unarmed pawns");
			}
			return string.Join(", ", parts.ToArray());
		}

		private static string WeaponGroupText(string name, List<Pawn> colonists)
		{
			GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
			if (gc == null)
			{
				throw new Fault("No game is currently loaded.");
			}
			List<KeyValuePair<int, GroupRule>> groups = gc.AllGroups.Where((KeyValuePair<int, GroupRule> kv) => kv.Value != null).OrderBy((KeyValuePair<int, GroupRule> kv) => kv.Key).ToList();
			string n = Norm(name);
			List<KeyValuePair<int, GroupRule>> found = groups.Where((KeyValuePair<int, GroupRule> kv) => Norm(kv.Value.name) == n || kv.Key.ToString(CultureInfo.InvariantCulture) == n).ToList();
			if (found.Count == 0)
			{
				throw new Fault("There is no weapon group called '" + name + "'." + (groups.Count > 0
					? " The weapon groups are " + JoinNames(groups.Select((KeyValuePair<int, GroupRule> kv) => kv.Value.name)) + "."
					: " There are no weapon groups yet."));
			}
			if (found.Count > 1)
			{
				throw new Fault("More than one weapon group is called '" + name + "'.");
			}
			GroupRule rule = found[0].Value;
			return "Weapon group " + found[0].Key + " '" + rule.name + "' holds " + WeaponRuleText(rule) + ".\n"
				+ PawnsLine(colonists.Where((Pawn p) => rule.Matches(p)), "Nobody is in it right now.");
		}

		// ---- query pawn groups -----------------------------------------------------------------------------------

		private static string QueryPawnGroups(FormContext c)
		{
			Pawn pawn = ResolveOnePawn(c, "PAWN");
			List<string> parts = new List<string>();
			ApparelPolicy apparel = ApparelPolicyOf(pawn);
			if (apparel != null)
			{
				parts.Add("clothing group '" + apparel.label + "'");
			}
			FoodPolicy food = FoodPolicyOf(pawn);
			if (food != null)
			{
				parts.Add("food group '" + food.label + "'");
			}
			DrugPolicy drug = DrugPolicyOf(pawn);
			if (drug != null)
			{
				parts.Add("drug group '" + drug.label + "'");
			}
			ReadingPolicy reading = ReadingPolicyOf(pawn);
			if (reading != null)
			{
				parts.Add("reading group '" + reading.label + "'");
			}
			if (pawn.playerSettings != null)
			{
				parts.Add("medical care: " + pawn.playerSettings.medCare.GetLabel());
				Area area = pawn.playerSettings.AreaRestrictionInPawnCurrentMap;
				parts.Add("area: " + (area != null ? area.Label : "anywhere"));
			}
			GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
			if (gc != null)
			{
				List<string> weaponGroups = gc.AllGroups.Where((KeyValuePair<int, GroupRule> kv) => kv.Value != null && kv.Value.Matches(pawn))
					.OrderBy((KeyValuePair<int, GroupRule> kv) => kv.Key).Select((KeyValuePair<int, GroupRule> kv) => kv.Value.name).ToList();
				parts.Add(weaponGroups.Count > 0 ? "weapon group: " + JoinNames(weaponGroups) : "no weapon group");
			}
			if (parts.Count == 0)
			{
				throw new Fault(pawn.LabelShort + " has no group settings in the game.");
			}
			return pawn.LabelShort + ": " + string.Join("; ", parts.ToArray()) + ".";
		}
	}
}
