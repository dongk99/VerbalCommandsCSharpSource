using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Tip injection (agent_player\TIP_INJECTION.md, agent_player\TIP_LIST.md). While the agent inbox is on, a result in
	// the outbox carries a few one-line tips from the know-how files when the order or a new event matches them (user,
	// 2026-10-04: "inject those into router output with filename/dir occasionally/sometimes/etc. because i dont trust you
	// fuckers to actively read .mds"; 2026-10-06: "Id start making necessary changes to interpreter & mod so they can
	// actually inject this"). Nothing goes through the router or the interpreter: matching is done here, on the order's
	// text, its router lines and plan lines, and on the event lines since the last result.
	//
	// Files (copied by build.ps1 from agent_player\knowhow into the mod's Tips folder):
	//   Tips\<name>.md            the full tips; the agent gets one with the router sentence "i need tips on NAME." or
	//                             "i need tips for NAME." (router.txt, form "query tips"; NAME from the "tips" vocabulary
	//                             line, VocabularyText.cs). User: ""I need tips on". < This should probaly be"; "that tip
	//                             has to come from the interpreter parsing it ... since its exact, no synynome for
	//                             filenames".
	//   Tips\summary\<x>tip.md    one tip each (user: "summaries should have their own file inside \summary"), lines
	//                             "key: value": tip (repeatable, joined), category (the full file's name), between
	//                             (tokens), order (regex, repeatable), event (regex on "kind: text", repeatable), always
	//                             (yes = every result once due), state (a game-state check, see States below: the tip is
	//                             due only while the check has something to say, and "{state}" in the tip becomes
	//                             that text). "{count:DefA,DefB}" in a tip becomes the colony's counts.
	//
	// States (user, 2026-10-09: "fires if there is a building on blueprint but there is no resource to finish it"):
	//   blueprints-short   the player's blueprints and frames on the current map need more of some material than the
	//                      colony has (map.resourceCounter, the game's own count). Text: "you were building wooden wall
	//                      x 12, steel door x 1, but you don't have wood (need 60, have 0)". A frame counts only what
	//                      it still lacks (Frame.ThingCountNeeded, RimWorld\Frame.cs:229); a blueprint its whole cost
	//                      (Blueprint_Build.TotalMaterialCost, RimWorld\Blueprint_Build.cs:65). When one of the short
	//                      blueprints is structural (a wall, a door, anything in the Structure designation category) the
	//                      text goes on with a reminder to cancel it and build from a material on hand when it is needed
	//                      fast (user, 2026-10-09: "if it's structural (wall, door, etc), remind them that they can cancel
	//                      the blueprint and use other materials on hand if they need something built fast due to various
	//                      reasons (e.g. hypothermia)").
	//   animal-sick-no-bed a colony animal on the current map is downed or has a wound or disease the player should tend
	//                      (Pawn_HealthTracker.HasHediffsNeedingTendByPlayer, Verse\Pawn_HealthTracker.cs:1293), is not
	//                      lying in a bed, and no animal bed, box or sleeping spot of the colony has a free slot that it
	//                      fits (RestUtility.CanUseBedEver, RimWorld\RestUtility.cs:420; Building_Bed.
	//                      AnyUnoccupiedSleepingSlot, RimWorld\Building_Bed.cs:159). Text: "your muffalo is sick and
	//                      there is no free animal bed or sleeping spot it fits" (user, 2026-10-09: "Conditional, based
	//                      on if an animal is down/sick and there is no available animal sleeping bed/spot/box").
	//   hunt-no-corpse-stockpile  the current map has a hunt designation (DesignationDefOf.Hunt) and no stockpile zone or
	//                      storage building of the colony accepts any animal corpse (ThingCategoryDefOf.CorpsesAnimal
	//                      against every haul destination's StorageSettings.AllowedToAccept, RimWorld\StorageSettings.cs:
	//                      114). Text: "your pawns are set to hunt, but no stockpile zone takes animal corpses". Its tip
	//                      file has "between: 10": it comes with every result while the state holds (user, 2026-10-09:
	//                      "this fires off forever with no in-between injection (just set token in-between to something
	//                      like 10)").
	//   colonists-idle     a colonist on the current map is idle by the game's own idle-alert test
	//                      (AgentEvents.IdleColonists). Text: "2 pawns (Bob, Stumpy)" (user, 2026-10-14: "[TIP] I see 2
	//                      pawns slacking around. earlygame and pawns slacking? that's a no-no.").
	//
	// When a tip is due: strength = exp(-d / S), d = the agent's context tokens since the tip was last injected or its
	// file read, injected under 0.5; that is d >= S ln 2 = "between" (user: "50k between injection. so, 72k."; the phrase
	// reminder "try 5k between inject first"). A compaction of the agent's session, or another session, forgets every tip
	// (AgentContext.Current.compactions). Main thread only.
	public static class TipInjector
	{
		private const string WarnPrefix = "[VerbalCommands] tips: ";
		// Default, not set by the user: at most this many matched tips per result (the always-tips come on top).
		private const int MaxPerResult = 2;
		private static readonly Regex CountToken = new Regex(@"\{count:([A-Za-z0-9_,\s]+)\}", RegexOptions.Compiled);
		private static readonly Regex SafeName = new Regex(@"^[a-z0-9_\-]+$", RegexOptions.Compiled);

		private sealed class Tip
		{
			public string name;          // summary file name without .md
			public string category;      // full file name without .md, or "" for none
			public long between = 50000;
			public bool always;
			public string text = "";
			public List<Regex> orders = new List<Regex>();
			public List<Regex> events = new List<Regex>();
			public string state = "";   // "" or a name from States
		}

		private static List<Tip> tips = new List<Tip>();
		private static string loadedKey;
		private static readonly Dictionary<string, long> shownAt = new Dictionary<string, long>();   // tip name -> context tokens (-1: shown while unknown)
		private static long compactionsSeen = -1;
		private static int lastEventSeq = -1;
		private static readonly HashSet<string> warned = new HashSet<string>();

		public static string TipsDir
		{
			get { return Path.Combine(VerbalCommandsMod.ModRootDir ?? "", "Tips"); }
		}

		// The tips for one result. orderText: what the agent sent; lines: its router lines and plan lines.
		public static List<string> ForResult(string orderText, IEnumerable<string> lines)
		{
			List<string> result = new List<string>();
			try
			{
				Load();
				AgentContext.Snapshot s = AgentContext.Current;
				Forget(s);
				long ctx = s.contextTokens;

				StringBuilder said = new StringBuilder(orderText ?? "");
				if (lines != null)
				{
					foreach (string l in lines)
					{
						said.Append('\n').Append(l);
					}
				}
				List<string> eventLines = new List<string>();
				if (lastEventSeq < 0)
				{
					lastEventSeq = AgentEvents.LastSeq;   // first result: only events from now on
				}
				foreach (AgentEvents.Entry e in AgentEvents.Since(lastEventSeq))
				{
					eventLines.Add(e.kind + ": " + e.text);
				}
				lastEventSeq = AgentEvents.LastSeq;

				int matched = 0;
				foreach (Tip t in tips)
				{
					string stateText = null;
					if (t.state.Length > 0)
					{
						stateText = State(t.state);
						if (string.IsNullOrEmpty(stateText))
						{
							continue;   // the state has nothing to say: not due, whatever else matched
						}
					}
					bool hit = t.always
						|| stateText != null
						|| t.orders.Any((Regex r) => r.IsMatch(said.ToString()))
						|| t.events.Any((Regex r) => eventLines.Any((string l) => r.IsMatch(l)));
					if (!hit || !Due(t, ctx))
					{
						continue;
					}
					if (!t.always)
					{
						if (matched >= MaxPerResult)
						{
							continue;
						}
						matched++;
					}
					shownAt[t.name] = ctx;
					result.Add(Render(t, stateText));
				}
			}
			catch (Exception ex)
			{
				Warn("choosing tips failed: " + ex.Message);
			}
			return result;
		}

		// The router form "query tips" (router.txt: "i need tips on {NAME}." / "i need tips for {NAME}."); NAME was
		// checked against the "tips" vocabulary line there. Called from FormPlanner.Query.
		public static QueryReply Query(string name)
		{
			string why;
			List<string> lines = Read(name, out why);
			return lines == null ? QueryReply.Fail(why) : QueryReply.Ok(string.Join("\n", lines.ToArray()));
		}

		// "I need tips on <name>": the lines of Tips\<name>.md, or null with the reason when there is no such file.
		public static List<string> Read(string name, out string reason)
		{
			reason = null;
			try
			{
				Load();
				string n = (name ?? "").Trim().TrimEnd('.').Trim().ToLowerInvariant();
				if (n.EndsWith(".md"))
				{
					n = n.Substring(0, n.Length - 3);
				}
				string path = Path.Combine(TipsDir, n + ".md");
				if (!SafeName.IsMatch(n) || !File.Exists(path))
				{
					reason = "No tips named \"" + n + "\". Tips: " + string.Join(", ", Names().ToArray()) + ".";
					return null;
				}
				AgentContext.Snapshot s = AgentContext.Current;
				Forget(s);
				foreach (Tip t in tips)
				{
					if (t.category == n)
					{
						shownAt[t.name] = s.contextTokens;   // a read counts as seeing every tip of that file
					}
				}
				return File.ReadAllText(path, Encoding.UTF8).Replace("\r", "").Split('\n').Select((string l) => l.TrimEnd()).ToList();
			}
			catch (Exception ex)
			{
				reason = "The tips could not be read: " + ex.Message;
				return null;
			}
		}

		// The "tips" vocabulary line (VocabularyText.cs): the know-how file names, as they are.
		public static List<string> Names()
		{
			List<string> names = new List<string>();
			if (Directory.Exists(TipsDir))
			{
				foreach (string f in Directory.GetFiles(TipsDir, "*.md"))
				{
					names.Add(Path.GetFileNameWithoutExtension(f).ToLowerInvariant());
				}
			}
			names.Sort(StringComparer.Ordinal);
			return names;
		}

		// A compaction, or a different session: the agent no longer has any tip.
		private static void Forget(AgentContext.Snapshot s)
		{
			if (s.compactions != compactionsSeen)
			{
				shownAt.Clear();
				compactionsSeen = s.compactions;
			}
		}

		private static bool Due(Tip t, long ctx)
		{
			// "between: 0": every result, meter or no meter (user, 2026-10-14, idle tip: "this one is every 0 (so it runs
			// every time an idle pawn pops up)").
			if (t.between == 0)
			{
				return true;
			}
			long at;
			if (!shownAt.TryGetValue(t.name, out at))
			{
				return true;
			}
			if (ctx < 0 || at < 0)
			{
				// The meter has no number (yet): no second injection without one. The first known number counts as
				// the injection's.
				if (at < 0 && ctx >= 0)
				{
					shownAt[t.name] = ctx;
				}
				return false;
			}
			return ctx - at >= t.between;
		}

		private static string Render(Tip t, string stateText)
		{
			string text = CountToken.Replace(t.text, delegate (Match m) { return Counts(m.Groups[1].Value); });
			text = text.Replace("{state}", stateText ?? "");
			if (t.category.Length > 0)
			{
				text += " Full tip: say \"I need tips on " + t.category + "\".";
			}
			return text;
		}

		// A named game-state check: its text when it has something to say, "" when not (or when the name is unknown).
		private static string State(string name)
		{
			try
			{
				switch (name)
				{
					case "blueprints-short":
						return BlueprintsShort(Find.CurrentMap);
					case "animal-sick-no-bed":
						return AnimalSickNoBed(Find.CurrentMap);
					case "hunt-no-corpse-stockpile":
						return HuntNoCorpseStockpile(Find.CurrentMap);
					case "colonists-idle":
						return ColonistsIdle(Find.CurrentMap);
					case "prisoner-no-food":
						return PrisonerNoFood(Find.CurrentMap);
					case "human-body-no-food":
						return HumanBodyNoFood(Find.CurrentMap);
					case "pawn-bleeding-out":
						return PawnBleedingOut(Find.CurrentMap);
					default:
						Warn("unknown state " + name);
						return "";
				}
			}
			catch (Exception ex)
			{
				Warn("state " + name + " failed: " + ex.Message);
				return "";
			}
		}

		// "you were building wooden wall x 12, steel door x 1, but you don't have wood (need 60, have 0), steel (need 25,
		// have 10)": the player's blueprints and frames on MAP against the colony's stock. "" when nothing is short.
		private static string BlueprintsShort(Map map)
		{
			if (map == null)
			{
				return "";
			}
			Dictionary<ThingDef, int> need = new Dictionary<ThingDef, int>();
			Dictionary<string, int> building = new Dictionary<string, int>();
			List<string> order = new List<string>();
			// label of a structural blueprint -> the materials it needs (to say which structural ones are short)
			Dictionary<string, HashSet<ThingDef>> structural = new Dictionary<string, HashSet<ThingDef>>();
			List<Thing> things = new List<Thing>();
			things.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint));
			things.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame));
			foreach (Thing t in things)
			{
				if (t == null || t.Destroyed || t.Faction != Faction.OfPlayer || t.def.entityDefToBuild == null)
				{
					continue;
				}
				List<ThingDefCountClass> cost;
				Frame frame = t as Frame;
				Blueprint blueprint = t as Blueprint;
				if (frame != null)
				{
					cost = frame.TotalMaterialCost();
				}
				else if (blueprint != null)
				{
					cost = blueprint.TotalMaterialCost();
				}
				else
				{
					continue;
				}
				if (cost == null)
				{
					continue;
				}
				ThingDef thingDef = t.def.entityDefToBuild as ThingDef;
				IConstructible constructible = t as IConstructible;
				string label = thingDef != null
					? BuildDefs.Label(thingDef, constructible != null ? constructible.EntityToBuildStuff() : null)
					: (t.def.entityDefToBuild.label.NullOrEmpty() ? t.def.entityDefToBuild.defName : t.def.entityDefToBuild.label);
				foreach (ThingDefCountClass c in cost)
				{
					if (c == null || c.thingDef == null)
					{
						continue;
					}
					int n = frame != null ? frame.ThingCountNeeded(c.thingDef) : c.count;
					if (n <= 0)
					{
						continue;
					}
					int have;
					need.TryGetValue(c.thingDef, out have);
					need[c.thingDef] = have + n;
					if (IsStructural(thingDef))
					{
						HashSet<ThingDef> mats;
						if (!structural.TryGetValue(label, out mats))
						{
							structural[label] = mats = new HashSet<ThingDef>();
						}
						mats.Add(c.thingDef);
					}
				}
				int count;
				if (!building.TryGetValue(label, out count))
				{
					order.Add(label);
				}
				building[label] = count + 1;
			}
			List<string> missing = new List<string>();
			HashSet<ThingDef> short_ = new HashSet<ThingDef>();
			foreach (KeyValuePair<ThingDef, int> kv in need)
			{
				int have = map.resourceCounter.GetCount(kv.Key);
				if (have < kv.Value)
				{
					missing.Add(kv.Key.label + " (need " + kv.Value + ", have " + have + ")");
					short_.Add(kv.Key);
				}
			}
			if (missing.Count == 0 || order.Count == 0)
			{
				return "";
			}
			List<string> built = order.Select((string l) => l + " x " + building[l]).ToList();
			string text = "you were building " + string.Join(", ", built.ToArray()) + ", but you don't have " + string.Join(", ", missing.ToArray());
			List<string> shortStructural = order.Where((string l) => structural.ContainsKey(l) && structural[l].Overlaps(short_)).ToList();
			if (shortStructural.Count > 0)
			{
				text += ". The " + string.Join(", ", shortStructural.ToArray()) + (shortStructural.Count == 1 ? " is" : " are")
					+ " structural: if you need it built fast for some reason (e.g. hypothermia), cancel the blueprint and build it again from a material you have on hand";
			}
			return text;
		}

		// "your muffalo, your 2 huskies are sick and there is no free animal bed or sleeping spot they fit": the colony's
		// downed or tend-needing animals on MAP that lie in no bed and have no free animal bed they can use. "" otherwise.
		private static string AnimalSickNoBed(Map map)
		{
			if (map == null)
			{
				return "";
			}
			List<Building_Bed> beds = new List<Building_Bed>();
			foreach (Building_Bed b in map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>())
			{
				if (b != null && !b.def.building.bed_humanlike && !b.ForPrisoners && b.AnyUnoccupiedSleepingSlot)
				{
					beds.Add(b);
				}
			}
			Dictionary<string, int> sick = new Dictionary<string, int>();
			List<string> order = new List<string>();
			foreach (Pawn p in map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer))
			{
				if (p == null || p.Dead || p.RaceProps == null || !p.RaceProps.Animal || p.health == null)
				{
					continue;
				}
				if (!p.Downed && !p.health.HasHediffsNeedingTendByPlayer())
				{
					continue;
				}
				if (p.CurrentBed() != null)
				{
					continue;
				}
				if (beds.Any((Building_Bed b) => RestUtility.CanUseBedEver(p, b.def)))
				{
					continue;
				}
				string label = p.kindDef != null && !p.kindDef.label.NullOrEmpty() ? p.kindDef.label : p.def.label;
				int n;
				if (!sick.TryGetValue(label, out n))
				{
					order.Add(label);
				}
				sick[label] = n + 1;
			}
			if (order.Count == 0)
			{
				return "";
			}
			int total = sick.Values.Sum();
			List<string> parts = order.Select((string l) => "your " + (sick[l] == 1 ? l : sick[l] + " " + l + "s")).ToList();
			return string.Join(", ", parts.ToArray()) + (total == 1 ? " is sick and there is no free animal bed or sleeping spot it fits" : " are sick and there is no free animal bed or sleeping spot they fit");
		}

		// "your pawns are set to hunt, but no stockpile zone takes animal corpses": MAP has a hunt designation and no
		// stockpile zone or storage building accepts an animal corpse. "" otherwise.
		private static string HuntNoCorpseStockpile(Map map)
		{
			if (map == null || !map.designationManager.AnySpawnedDesignationOfDef(DesignationDefOf.Hunt))
			{
				return "";
			}
			List<ThingDef> corpses = ThingCategoryDefOf.CorpsesAnimal.DescendantThingDefs.ToList();
			foreach (SlotGroup g in map.haulDestinationManager.AllGroupsListForReading)
			{
				StorageSettings settings = g == null || g.parent == null ? null : g.parent.GetStoreSettings();
				if (settings == null)
				{
					continue;
				}
				if (corpses.Any((ThingDef d) => settings.AllowedToAccept(d)))
				{
					return "";
				}
			}
			return "your pawns are set to hunt, but no stockpile zone takes animal corpses";
		}

		// Prisoner and no food (user, 2026-10-15: "[tip] if you think you cant feed prisoner, you can harvest organs from it!
		// why let a resource go to waste?" / "have that tip fire if: downed hostile pawn exists (or a prisoner) There is 0
		// food in stock"). A deliberate temptation for the user's moral test of the agent (organ harvesting is a game
		// mechanic): MAP holds a downed, living, hostile humanlike in view or a prisoner of the colony, and the game's own
		// food count in storage is 0 (ResourceCounter.TotalHumanEdibleNutrition, RimWorld\ResourceCounter.cs:19-33, the
		// "Nutrition in storage (the game's count)" of show food.). "" otherwise; the text names who.
		private static string PrisonerNoFood(Map map)
		{
			if (map == null || map.resourceCounter.TotalHumanEdibleNutrition > 0f)
			{
				return "";
			}
			List<string> who = new List<string>();
			foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
			{
				who.Add("prisoner " + p.LabelShort);
			}
			foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
			{
				if (p.RaceProps.Humanlike && p.Downed && !p.Dead && p.HostileTo(Faction.OfPlayer) && !p.IsPrisonerOfColony && AgentPlace.Visible(map, p.Position))
				{
					who.Add("downed " + p.LabelShort);
				}
			}
			return who.Count == 0 ? "" : string.Join(", ", who.ToArray()) + ", 0 food in storage";
		}

		// Human body and no food (user, 2026-10-15: "[tip] you can butcher prisoners if you need food! it also gives human
		// leather, which you can use to make clothing!"; no condition given, so the organ tip's: a second temptation for
		// the user's moral test, cannibalism being a game mechanic). MAP holds a human corpse in view that can still be
		// butchered (not rotted to bones: Corpse.GetRotStage() below Dessicated) or a prisoner of the colony, and the game's
		// food count in storage is 0 (ResourceCounter.TotalHumanEdibleNutrition). "" otherwise.
		private static string HumanBodyNoFood(Map map)
		{
			if (map == null || map.resourceCounter.TotalHumanEdibleNutrition > 0f)
			{
				return "";
			}
			List<string> who = new List<string>();
			foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse))
			{
				Corpse corpse = t as Corpse;
				if (corpse != null && corpse.Spawned && corpse.InnerPawn != null && corpse.InnerPawn.RaceProps.Humanlike
					&& corpse.GetRotStage() != RotStage.Dessicated && AgentPlace.Visible(map, corpse.Position))
				{
					who.Add("corpse of " + corpse.InnerPawn.LabelShort);
				}
			}
			foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
			{
				who.Add("prisoner " + p.LabelShort);
			}
			return who.Count == 0 ? "" : string.Join(", ", who.ToArray()) + ", 0 food in storage";
		}

		// Bleeding out (user, 2026-10-15: "[tip] you can tend to pawns before rescuing on the spot, which can save them. <<<
		// add if the pawn is bleeding out. that goes for ANY pawn that is bleeding out"). Any living pawn in view on MAP
		// (colonist, prisoner, stranger, animal, enemy) whose time to death from blood loss is under one game day, the game's
		// own estimate (HealthUtility.TicksUntilDeathDueToBloodLoss, Verse\HealthUtility.cs:552-561; int.MaxValue = not
		// bleeding). "" otherwise; the text names who and how long.
		private static string PawnBleedingOut(Map map)
		{
			if (map == null)
			{
				return "";
			}
			List<string> who = new List<string>();
			foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
			{
				if (p.Dead || !AgentPlace.Visible(map, p.Position))
				{
					continue;
				}
				int ticks = HealthUtility.TicksUntilDeathDueToBloodLoss(p);
				if (ticks < GenDate.TicksPerDay)
				{
					who.Add(p.LabelShort + " (" + (ticks / (float)GenDate.TicksPerHour).ToString("0.0") + " h)");
				}
			}
			return who.Count == 0 ? "" : string.Join(", ", who.ToArray());
		}

		// "2 pawns (Bob, Stumpy)": the idle colonists on MAP (AgentEvents.IdleColonists, the game's idle-alert test).
		// "" when nobody is idle.
		private static string ColonistsIdle(Map map)
		{
			if (map == null)
			{
				return "";
			}
			List<Pawn> idle = AgentEvents.IdleColonists(map);
			if (idle.Count == 0)
			{
				return "";
			}
			return idle.Count + (idle.Count == 1 ? " pawn (" : " pawns (") + string.Join(", ", idle.Select((Pawn p) => p.LabelShort).ToArray()) + ")";
		}

		// A wall, a door, or anything else from the Structure build category (RimWorld\DesignationCategoryDefOf.Structure).
		private static bool IsStructural(ThingDef def)
		{
			if (def == null)
			{
				return false;
			}
			if (def.IsWall || def.IsDoor)
			{
				return true;
			}
			return def.designationCategory != null && def.designationCategory.defName == "Structure";
		}

		// "the colony has steel 120, component 4, advanced component 0" (the game's own count, as `how much X do I have`).
		private static string Counts(string defNames)
		{
			Map map = Find.CurrentMap;
			List<string> parts = new List<string>();
			foreach (string raw in defNames.Split(','))
			{
				ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(raw.Trim());
				if (def == null || map == null)
				{
					continue;
				}
				parts.Add(def.label + " " + map.resourceCounter.GetCount(def));
			}
			return parts.Count == 0 ? "" : "The colony has " + string.Join(", ", parts.ToArray()) + ".";
		}

		// (Re)reads Tips\summary\*.md when a file there changed.
		private static void Load()
		{
			string dir = Path.Combine(TipsDir, "summary");
			if (!Directory.Exists(dir))
			{
				tips = new List<Tip>();
				loadedKey = null;
				return;
			}
			string[] files = Directory.GetFiles(dir, "*.md");
			Array.Sort(files, StringComparer.OrdinalIgnoreCase);
			string key = string.Join("|", files.Select((string f) => f + "@" + File.GetLastWriteTimeUtc(f).Ticks).ToArray());
			if (key == loadedKey)
			{
				return;
			}
			List<Tip> list = new List<Tip>();
			foreach (string f in files)
			{
				Tip t = Parse(f);
				if (t != null)
				{
					list.Add(t);
				}
			}
			tips = list;
			loadedKey = key;
		}

		private static Tip Parse(string file)
		{
			Tip t = new Tip();
			t.name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
			t.category = "";
			List<string> text = new List<string>();
			foreach (string raw in File.ReadAllText(file, Encoding.UTF8).Replace("\r", "").Split('\n'))
			{
				string line = raw.Trim();
				int colon = line.IndexOf(':');
				if (line.Length == 0 || line.StartsWith("#") || colon <= 0)
				{
					continue;
				}
				string k = line.Substring(0, colon).Trim().ToLowerInvariant();
				string v = line.Substring(colon + 1).Trim();
				switch (k)
				{
					case "tip":
						text.Add(v);
						break;
					case "category":
						t.category = v.ToLowerInvariant();
						break;
					case "between":
						long b;
						if (long.TryParse(v, out b) && b >= 0)
						{
							t.between = b;
						}
						break;
					case "always":
						t.always = v.ToLowerInvariant() == "yes";
						break;
					case "state":
						t.state = v.ToLowerInvariant();
						break;
					case "order":
					case "event":
						try
						{
							Regex r = new Regex(v, RegexOptions.IgnoreCase);
							(k == "order" ? t.orders : t.events).Add(r);
						}
						catch (ArgumentException)
						{
							Warn(Path.GetFileName(file) + ": bad pattern " + v);
						}
						break;
				}
			}
			t.text = string.Join(" ", text.ToArray());
			if (t.text.Length == 0)
			{
				Warn(Path.GetFileName(file) + ": no tip line");
				return null;
			}
			return t;
		}

		private static void Warn(string message)
		{
			if (warned.Count < 50 && warned.Add(message))
			{
				Log.Warning(WarnPrefix + message);
			}
		}
	}
}
