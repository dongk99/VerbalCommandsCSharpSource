using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Agent 07 / S1: the designation change forms (agent_player\out\07\forms.md).
	//   designate VERB      VERB: chop cut harvest mine deconstruct uninstall haul hunt tame slaughter strip claim smooth
	//   items forbid / items unforbid
	// Plan() builds the plan now (what, where, the confirm line); the write runs only after the player confirms and does
	// what a click of the game's own designator does: CanDesignateThing / CanDesignateCell, then DesignateThing /
	// DesignateSingleCell. Colonists then do the work. "designate cancel" is another file: Plan() returns null for it.
	// CHANGED 2026-10-16 (user: "Merge all 3. new replace old. delete old logic as needed"): "mine nearest [COUNT] THINGS
	// near SPOT", "mine THINGS in area AREA" and "haul THINGS near SPOT" are the micro orders now (AgentMicro.cs, router
	// forms "micro mine" / "micro haul"); the vein of "mine nearest" and the rock columns and report of "mine in area" are
	// gone from here. WordFilter gives AgentMicro the same THINGS matching.
	//
	// Fair play: nothing here reads or sets dev mode or god mode; every target and every cell must pass
	// AgentPlace.Visible (nothing under fog is counted, printed or marked). The mod itself spawns, destroys, finishes
	// or moves nothing. No game message is shown at plan time (the game's warnings are recomputed from the same
	// conditions and put into the confirm line). Main thread only (plan and apply already are).
	public static class AgentDesignations
	{
		private static readonly string[] DesignateVerbs =
		{
			"chop", "cut", "harvest", "mine", "deconstruct", "uninstall", "haul", "hunt", "tame", "slaughter", "strip", "claim", "smooth"
		};

		private const int NearTiles = 10;
		private const int CapAll = 300;
		// Area / near / nearest.
		private const int CapAny = 500;

		private static readonly string[] LeadDrops = { "all ", "every ", "the ", "my " };
		private static readonly string[] TrailDrops = { " ore", " ores", " vein", " veins", " deposit", " deposits" };

		// Words that name a whole group of things; the confirm line then reads "23 trees (14 oak tree, 9 poplar tree)".
		private static readonly HashSet<string> GroupKeys = new HashSet<string>
		{
			"tree", "plant", "animal", "item", "wall", "floor", "building", "corpse", "rock", "stone", "ore"
		};

		private static readonly HashSet<string> WallNames = new HashSet<string> { "wall", "surface" };
		private static readonly HashSet<string> FloorNames = new HashSet<string> { "floor", "ground", "surface" };

		// ---- context ------------------------------------------------------------------------------------------------

		private sealed class Word
		{
			public string raw;
			public string key;
			public List<string> tokens;
			public bool universal;
			public bool exact;
		}

		private sealed class Cand
		{
			public Thing thing;
			public IntVec3 cell;
			public string label;
			public int id;
			public HashSet<string> names;
			public bool checkedAccept;
			public AcceptanceReport report;

			public IntVec3 Pos
			{
				get { return thing != null ? thing.Position : cell; }
			}

			public bool Accepted
			{
				get { return report.Accepted; }
			}
		}

		private sealed class Ctx
		{
			public Map map;
			public string verb;
			public bool items;
			public string mode;
			public CellRect rect;
			public IntVec3 spot;
			public bool hasCount;
			public int count;
			public List<Word> words = new List<Word>();
			public Dictionary<ThingDef, HashSet<string>> defNames = new Dictionary<ThingDef, HashSet<string>>();
			public string smoothKind = "surface";
			// MODE room (user, 2026-10-15): the cells of the rooms called ROOM, and how to name them.
			public HashSet<IntVec3> roomCells;
			public string roomLabel;

			public bool CellVerb
			{
				get { return verb == "mine" || verb == "smooth"; }
			}
		}

		// ---- entry point --------------------------------------------------------------------------------------------

		// null when c.form is not one of this file's forms (so the caller tries the next dispatcher).
		public static PlanReply Plan(FormContext c)
		{
			string verb;
			bool items;
			if (!ParseForm(c.form, out verb, out items))
			{
				return null;
			}
			Map map = c.map;
			if (map != Find.CurrentMap)
			{
				return PlanReply.Fail("That map is not the one on screen; designations can only be placed on the map being shown.");
			}
			// "haul THINGS." while THINGS is rotting outdoors: a micro haul over its 10x10 square (AgentRotScan.HaulPlan; user,
			// 2026-10-17, "C# redirect"). Otherwise the mark below, as before.
			if (verb == "haul" && !items && (c.Text("MODE") ?? "").Trim().ToLowerInvariant() == "all")
			{
				PlanReply rotHaul = AgentRotScan.HaulPlan(c);
				if (rotHaul != null)
				{
					return rotHaul;
				}
			}

			Ctx x = new Ctx();
			x.map = map;
			x.verb = verb;
			x.items = items;

			foreach (string raw in c.TextList("THINGS"))
			{
				string key = Key(raw);
				if (key.Length == 0)
				{
					continue;
				}
				Word w = new Word();
				w.raw = raw;
				w.key = key;
				w.tokens = Tokens(key);
				w.universal = IsUniversal(x, key);
				x.words.Add(w);
			}
			if (x.words.Count == 0 && !items)
			{
				return PlanReply.Fail("Say what to " + verb + ", for example '" + verb + " " + ExampleNoun(verb) + ".'");
			}
			if (verb == "smooth")
			{
				x.smoothKind = SmoothKindFor(x.words);
			}

			// MODE is fixed by the template; infer it only if it is missing.
			string mode = (c.Text("MODE") ?? "").Trim().ToLowerInvariant();
			if (mode != "all" && mode != "area" && mode != "nearest" && mode != "near" && mode != "room")
			{
				mode = c.Has("AREA") ? "area" : (c.Has("SPOT") ? (c.Has("COUNT") ? "nearest" : "near") : (c.Has("ROOM") ? "room" : "all"));
			}
			x.mode = mode;
			string reason;
			// "deconstruct butcher spot in kitchen." (user, 2026-10-15: agents' "deconstruct butcher spot in kitchen / in the
			// barracks" read the room words as part of the name and found nothing, outbox dbd8523c_02236, 0d4b1b3a_01324). The
			// rooms with that label (FormPlanner.ResolveRooms, as the bed and forbid orders use); a thing counts when any of
			// its cells is in them. The marking itself stays the game's own click: Designator.CanDesignateThing /
			// DesignateThing (a spot, WorkToBuild 0, is removed at once, Designator_Deconstruct.cs:69-76).
			if (mode == "room")
			{
				string label = c.Text("ROOM");
				List<Room> rooms = StorageIndex.FindRoomsByLabel(map, label ?? "");
				if (rooms.Count == 0)
				{
					return PlanReply.Fail("There is no room called '" + label + "'.");
				}
				x.roomLabel = label;
				x.roomCells = new HashSet<IntVec3>(rooms.SelectMany((Room r) => r.Cells));
			}
			if (mode == "area")
			{
				if (!c.Has("AREA"))
				{
					return PlanReply.Fail("Say which area, for example '" + verb + " " + ExampleNoun(verb) + " in area 100, 80 to 120, 95.'");
				}
				if (!AgentPlace.TryRect(map, c.Text("AREA"), out x.rect, out reason))
				{
					return PlanReply.Fail(reason);
				}
			}
			else if (mode == "nearest" || mode == "near")
			{
				if (!c.Has("SPOT"))
				{
					return PlanReply.Fail("Say where, for example '" + verb + " " + ExampleNoun(verb) + " near bob.'");
				}
				if (!AgentPlace.TryCell(map, c.Text("SPOT"), out x.spot, out reason))
				{
					return PlanReply.Fail(reason);
				}
			}
			if (mode == "nearest")
			{
				double? n = c.Number("COUNT");
				if (n.HasValue)
				{
					x.hasCount = true;
					x.count = (int)Math.Round(n.Value);
					if (x.count < 1)
					{
						return PlanReply.Fail("The count must be at least 1.");
					}
				}
			}

			Designator d = MakeDesignator(x);

			// Everything the verb could mark that the player can see, then the ones the words name.
			List<Cand> visible = Enumerate(x).ToList();
			// User 2026-10-17: "Uninstall needs similar logic." "uninstall ID." names one thing by its game ID (Thing.ThingID,
			// as the list below gives it).
			if (verb == "uninstall" && x.words.Count == 1
				&& System.Text.RegularExpressions.Regex.IsMatch(x.words[0].raw.Trim(), "^[a-z_][a-z0-9_]*[0-9]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
			{
				string id = x.words[0].raw.Trim();
				Cand one = visible.FirstOrDefault((Cand cd) => cd.thing != null && string.Equals(cd.thing.ThingID, id, StringComparison.OrdinalIgnoreCase));
				if (one == null)
				{
					return PlanReply.Fail("Nothing on the map has the ID " + id + ".");
				}
				EnsureAccept(x, d, one);
				return one.Accepted ? Build(c, x, new List<Cand> { one }) : Refused(x, new List<Cand> { one });
			}
			foreach (Word w in x.words)
			{
				if (!w.universal)
				{
					w.exact = visible.Any((Cand cd) => cd.names.Contains(w.key));
				}
			}
			List<Cand> matched = visible.Where((Cand cd) => MatchesWords(x, cd)).ToList();
			foreach (Cand cd in matched)
			{
				EnsureAccept(x, d, cd);
			}
			List<Cand> accepted = matched.Where((Cand cd) => cd.Accepted).ToList();
			List<Cand> inScope = accepted.Where((Cand cd) => InScope(x, cd)).ToList();

			List<Cand> chosen;
			if (mode == "nearest")
			{
				List<Cand> sorted = accepted.OrderBy((Cand cd) => Dist2(cd.Pos, x.spot)).ThenBy((Cand cd) => cd.id).ToList();
				if (sorted.Count == 0)
				{
					chosen = sorted;
				}
				else
				{
					chosen = sorted.Take(x.hasCount ? x.count : 1).ToList();
				}
			}
			else
			{
				chosen = inScope;
			}

			if (chosen.Count == 0)
			{
				return Zero(x, d, visible, matched, accepted);
			}
			// User 2026-10-17: an uninstall that matches several things lists each with its ID, cell and room (the user's
			// pick, "Like the build list"); one thing is uninstalled as before.
			if (verb == "uninstall" && chosen.Count > 1)
			{
				List<string> lines = new List<string>();
				foreach (Cand cd in chosen.Where((Cand cd) => cd.thing != null).OrderBy((Cand cd) => cd.thing.thingIDNumber))
				{
					string room = BuildDefs.RoomText(FormPlanner.RoomOfThing(cd.thing));
					lines.Add(cd.thing.ThingID + ": " + cd.thing.LabelNoCount + ", " + cd.thing.Position.x.ToString(CultureInfo.InvariantCulture)
						+ ", " + cd.thing.Position.z.ToString(CultureInfo.InvariantCulture) + (room.Length > 0 ? ", in " + room : ""));
				}
				lines.Add("If you want to uninstall, say Uninstall {fullItemId}.");
				return PlanReply.Fail(string.Join("\n", lines.ToArray()));
			}

			int cap = mode == "all" ? CapAll : CapAny;
			if (chosen.Count > cap)
			{
				return Capped(x, chosen);
			}

			return Build(c, x, chosen);
		}

		private static bool ParseForm(string form, out string verb, out bool items)
		{
			verb = null;
			items = false;
			if (form == null)
			{
				return false;
			}
			if (form == "items forbid" || form == "items unforbid")
			{
				verb = form.Substring("items ".Length);
				items = true;
				return true;
			}
			if (form.StartsWith("designate "))
			{
				string v = form.Substring("designate ".Length).Trim();
				if (Array.IndexOf(DesignateVerbs, v) >= 0)
				{
					verb = v;
					return true;
				}
			}
			return false;
		}

		// ---- the game's designators ---------------------------------------------------------------------------------

		// Designator_PlantsHarvestWood.cs:7, Designator_PlantsCut.cs:7, Designator_PlantsHarvest.cs:6, Designator_Mine.cs:7,
		// Designator_Deconstruct.cs:7, Designator_Uninstall.cs:7, Designator_Claim.cs:7, Designator_Haul.cs:6,
		// Designator_Hunt.cs:8, Designator_Tame.cs:8, Designator_Slaughter.cs:8, Designator_Strip.cs:8,
		// Designator_SmoothWalls.cs:6 / SmoothFloors.cs:6 / SmoothSurface.cs:6, Designator_Forbid.cs:7, Designator_Unforbid.cs:7.
		private static Designator MakeDesignator(Ctx x)
		{
			switch (x.verb)
			{
				case "chop": return new Designator_PlantsHarvestWood();
				case "cut": return new Designator_PlantsCut();
				case "harvest": return new Designator_PlantsHarvest();
				case "mine": return new Designator_Mine();
				case "deconstruct": return new Designator_Deconstruct();
				case "uninstall": return new Designator_Uninstall();
				case "claim": return new Designator_Claim();
				case "haul": return new Designator_Haul();
				case "hunt": return new Designator_Hunt();
				case "tame": return new Designator_Tame();
				case "slaughter": return new Designator_Slaughter();
				case "strip": return new Designator_Strip();
				case "forbid": return new Designator_Forbid();
				case "unforbid": return new Designator_Unforbid();
				case "smooth":
					if (x.smoothKind == "walls")
					{
						return new Designator_SmoothWalls();
					}
					if (x.smoothKind == "floors")
					{
						return new Designator_SmoothFloors();
					}
					return new Designator_SmoothSurface();
			}
			throw new Fault("The form 'designate " + x.verb + "' is not built.");
		}

		// The designation the designator adds (null for claim, forbid, unforbid and smooth, which has two).
		private static DesignationDef DesignationFor(string verb)
		{
			switch (verb)
			{
				case "chop":
				case "harvest": return DesignationDefOf.HarvestPlant;
				case "cut": return DesignationDefOf.CutPlant;
				case "mine": return DesignationDefOf.Mine;
				case "deconstruct": return DesignationDefOf.Deconstruct;
				case "uninstall": return DesignationDefOf.Uninstall;
				case "haul": return DesignationDefOf.Haul;
				case "hunt": return DesignationDefOf.Hunt;
				case "tame": return DesignationDefOf.Tame;
				case "slaughter": return DesignationDefOf.Slaughter;
				case "strip": return DesignationDefOf.Strip;
			}
			return null;
		}

		private static AcceptanceReport Accept(Ctx x, Designator d, Cand cd)
		{
			if (x.CellVerb)
			{
				return d.CanDesignateCell(cd.cell);
			}
			return d.CanDesignateThing(cd.thing);
		}

		private static void EnsureAccept(Ctx x, Designator d, Cand cd)
		{
			if (!cd.checkedAccept)
			{
				cd.report = Accept(x, d, cd);
				cd.checkedAccept = true;
			}
		}

		// ---- candidates ---------------------------------------------------------------------------------------------

		private static IEnumerable<Thing> Sources(Ctx x)
		{
			Map map = x.map;
			switch (x.verb)
			{
				case "chop":
				case "cut":
				case "harvest":
					return map.listerThings.ThingsInGroup(ThingRequestGroup.Plant);
				case "mine":
					return map.listerThings.AllThings.Where((Thing t) => t.def.mineable);
				case "deconstruct":
				case "uninstall":
				case "claim":
					return map.listerThings.AllThings.Where((Thing t) => t.def.category == ThingCategory.Building && !t.def.mineable);
				case "haul":
					return map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);
				case "hunt":
				case "tame":
				case "slaughter":
					return map.mapPawns.AllPawnsSpawned.Cast<Thing>();
				case "strip":
					return map.mapPawns.AllPawnsSpawned.Cast<Thing>().Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse));
				case "forbid":
				case "unforbid":
					return map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Where((Thing t) => t.def.category == ThingCategory.Item);
			}
			return new List<Thing>();
		}

		// Spawned, in view (AgentPlace.Visible) candidates only.
		private static IEnumerable<Cand> Enumerate(Ctx x)
		{
			Map map = x.map;
			if (x.verb == "smooth")
			{
				foreach (IntVec3 cell in map.AllCells)
				{
					if (!AgentPlace.Visible(map, cell))
					{
						continue;
					}
					Building edifice = cell.GetEdifice(map);
					bool wall = edifice != null && edifice.def.IsSmoothable;
					Cand cd = new Cand();
					cd.cell = cell;
					cd.id = map.cellIndices.CellToIndex(cell);
					cd.label = wall ? "wall" : "floor";
					cd.names = wall ? WallNames : FloorNames;
					yield return cd;
				}
				yield break;
			}
			foreach (Thing t in Sources(x))
			{
				if (t == null || !t.Spawned || t.Destroyed || t.Map != map)
				{
					continue;
				}
				if (!AgentPlace.Visible(map, t.Position))
				{
					continue;
				}
				Cand cd = new Cand();
				cd.thing = t;
				cd.cell = t.Position;
				cd.id = t.thingIDNumber;
				cd.label = LabelOf(t);
				cd.names = NamesFor(x, t);
				yield return cd;
			}
		}

		private static string LabelOf(Thing t)
		{
			Pawn p = t as Pawn;
			if (p != null)
			{
				return p.kindDef != null && p.kindDef.label != null ? p.kindDef.label : p.def.label;
			}
			Corpse corpse = t as Corpse;
			if (corpse != null)
			{
				Pawn inner = corpse.InnerPawn;
				return (inner != null && inner.kindDef != null ? inner.kindDef.label : "body") + " corpse";
			}
			return t.def.label ?? t.def.defName;
		}

		// ---- names and matching (brief 3.3) -------------------------------------------------------------------------

		private static string Norm(string s)
		{
			if (s == null)
			{
				return "";
			}
			s = string.Join(" ", s.ToLowerInvariant().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
			bool changed = true;
			while (changed)
			{
				changed = false;
				foreach (string p in LeadDrops)
				{
					if (s.StartsWith(p))
					{
						s = s.Substring(p.Length).TrimStart();
						changed = true;
					}
				}
				foreach (string q in TrailDrops)
				{
					if (s.EndsWith(q))
					{
						s = s.Substring(0, s.Length - q.Length).TrimEnd();
						changed = true;
					}
				}
			}
			return s;
		}

		private static string Sing(string s)
		{
			if (s == null || s.Length <= 2)
			{
				return s ?? "";
			}
			if (s.EndsWith("ies"))
			{
				return s.Substring(0, s.Length - 3) + "y";
			}
			if (s.EndsWith("es"))
			{
				string stem = s.Substring(0, s.Length - 2);
				if (stem.EndsWith("s") || stem.EndsWith("x") || stem.EndsWith("ch") || stem.EndsWith("sh"))
				{
					return stem;
				}
			}
			if (s.EndsWith("s") && !s.EndsWith("ss"))
			{
				return s.Substring(0, s.Length - 1);
			}
			return s;
		}

		private static string Key(string s)
		{
			return Sing(Norm(s));
		}

		private static List<string> Tokens(string key)
		{
			List<string> list = new List<string>();
			foreach (string t in key.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
			{
				list.Add(Sing(t));
			}
			return list;
		}

		private static bool IsUniversal(Ctx x, string key)
		{
			if (key == "everything" || key == "anything" || key == "all" || key == "thing")
			{
				return true;
			}
			return key == "item" && (x.items || x.verb == "haul");
		}

		private static void AddName(HashSet<string> set, string raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
			{
				return;
			}
			string key = Key(raw);
			if (key.Length > 0)
			{
				set.Add(key);
			}
		}

		// Names that depend on the def only (cached per def).
		private static HashSet<string> DefNames(ThingDef def)
		{
			HashSet<string> s = new HashSet<string>();
			AddName(s, def.label);
			AddName(s, def.defName);
			if (def.plant != null)
			{
				AddName(s, "plant");
				if (def.plant.IsTree)
				{
					AddName(s, "tree");
				}
				if (def.plant.harvestedThingDef != null)
				{
					AddName(s, def.plant.harvestedThingDef.label);
				}
				if (def.label != null && def.label.ToLowerInvariant().Contains("bush"))
				{
					AddName(s, "bush");
				}
			}
			if (def.mineable && def.building != null)
			{
				if (def.building.isNaturalRock && !def.building.isResourceRock)
				{
					AddName(s, "rock");
					AddName(s, "stone");
				}
				if (def.building.isResourceRock)
				{
					AddName(s, "ore");
				}
				if (def.building.mineableThing != null)
				{
					AddName(s, def.building.mineableThing.label);
				}
			}
			if (def.race != null)
			{
				if (def.race.Animal)
				{
					AddName(s, "animal");
				}
				if (def.race.predator)
				{
					AddName(s, "predator");
				}
			}
			if (def.category == ThingCategory.Item)
			{
				if (def.thingCategories != null)
				{
					foreach (ThingCategoryDef cat in def.thingCategories)
					{
						for (ThingCategoryDef k = cat; k != null; k = k.parent)
						{
							AddName(s, k.label);
						}
					}
				}
				if (def.IsWeapon)
				{
					AddName(s, "weapon");
				}
				if (def.IsApparel)
				{
					AddName(s, "apparel");
					AddName(s, "clothes");
					AddName(s, "clothing");
				}
				if (def.IsNutritionGivingIngestible)
				{
					AddName(s, "food");
				}
				if (def.IsMedicine)
				{
					AddName(s, "medicine");
				}
				if (def.defName != null && def.defName.Contains("Chunk"))
				{
					AddName(s, "chunk");
				}
			}
			if (def.category == ThingCategory.Building && !def.mineable)
			{
				AddName(s, "building");
			}
			return s;
		}

		private static string StripParens(string s)
		{
			if (s == null)
			{
				return "";
			}
			int open;
			while ((open = s.IndexOf('(')) >= 0)
			{
				int close = s.IndexOf(')', open);
				if (close < 0)
				{
					s = s.Substring(0, open);
					break;
				}
				s = s.Remove(open, close - open + 1);
			}
			return s.Trim();
		}

		private static HashSet<string> NamesFor(Ctx x, Thing t)
		{
			HashSet<string> baseSet;
			if (!x.defNames.TryGetValue(t.def, out baseSet))
			{
				baseSet = DefNames(t.def);
				x.defNames[t.def] = baseSet;
			}
			List<string> extra = null;
			Pawn p = t as Pawn;
			Corpse corpse = t as Corpse;
			if (p != null)
			{
				extra = new List<string>();
				if (p.kindDef != null)
				{
					extra.Add(p.kindDef.label);
				}
				extra.Add(p.def.label);
				extra.Add(p.LabelShort);
			}
			else if (corpse != null)
			{
				extra = new List<string> { "corpse", "body" };
				if (corpse.InnerPawn != null)
				{
					extra.Add(corpse.InnerPawn.def.label + " corpse");
				}
			}
			else if (!t.def.mineable && t.def.plant == null)
			{
				extra = new List<string> { StripParens(t.LabelNoCount) };
				if (t.def.category == ThingCategory.Building && t.Faction == null)
				{
					extra.Add("ruin");
					extra.Add("ruins");
					extra.Add("ancient");
					extra.Add("ruin " + t.def.label);
				}
			}
			if (extra == null)
			{
				return baseSet;
			}
			HashSet<string> result = null;
			foreach (string e in extra)
			{
				if (string.IsNullOrWhiteSpace(e))
				{
					continue;
				}
				string key = Key(e);
				if (key.Length == 0 || baseSet.Contains(key))
				{
					continue;
				}
				if (result == null)
				{
					result = new HashSet<string>(baseSet);
				}
				result.Add(key);
			}
			return result ?? baseSet;
		}

		// No words (forbid / unforbid of every item) matches everything.
		private static bool MatchesWords(Ctx x, Cand cd)
		{
			if (x.words.Count == 0)
			{
				return true;
			}
			foreach (Word w in x.words)
			{
				if (w.universal)
				{
					return true;
				}
				if (w.exact)
				{
					if (cd.names.Contains(w.key))
					{
						return true;
					}
				}
				else if (LooseMatch(cd, w))
				{
					return true;
				}
			}
			return false;
		}

		// The THINGS of a micro order (AgentMicro.cs) matched as this file matches them: null when there are no words or one
		// of them means everything, else a test for one thing (a mineable for "mine", an item for "haul"). The exact /
		// loose choice of each word is made against what is in view now, as Plan does.
		internal static Func<Thing, bool> WordFilter(Map map, string verb, List<string> things)
		{
			Ctx x = new Ctx();
			x.map = map;
			x.verb = verb;
			foreach (string raw in things ?? new List<string>())
			{
				string key = Key(raw);
				if (key.Length == 0)
				{
					continue;
				}
				Word w = new Word();
				w.raw = raw;
				w.key = key;
				w.tokens = Tokens(key);
				w.universal = IsUniversal(x, key);
				if (w.universal)
				{
					return null;
				}
				x.words.Add(w);
			}
			if (x.words.Count == 0)
			{
				return null;
			}
			List<Cand> visible = Enumerate(x).ToList();
			foreach (Word w in x.words)
			{
				w.exact = visible.Any((Cand cd) => cd.names.Contains(w.key));
			}
			return delegate (Thing t)
			{
				if (t == null)
				{
					return false;
				}
				Cand cd = new Cand();
				cd.thing = t;
				cd.cell = t.Position;
				cd.names = NamesFor(x, t);
				return MatchesWords(x, cd);
			};
		}

		// A word with no exact match anywhere: a candidate with ONE name that holds every word of the player's word as a
		// whole word ("machinery" finds "compacted machinery").
		private static bool LooseMatch(Cand cd, Word w)
		{
			if (w.tokens.Count == 0)
			{
				return false;
			}
			foreach (string name in cd.names)
			{
				List<string> nameTokens = Tokens(name);
				bool all = true;
				foreach (string t in w.tokens)
				{
					if (!nameTokens.Contains(t))
					{
						all = false;
						break;
					}
				}
				if (all)
				{
					return true;
				}
			}
			return false;
		}

		private static string SmoothKindFor(List<Word> words)
		{
			bool allWall = words.Count > 0;
			bool allFloor = words.Count > 0;
			foreach (Word w in words)
			{
				if (w.key != "wall")
				{
					allWall = false;
				}
				if (w.key != "floor" && w.key != "ground")
				{
					allFloor = false;
				}
			}
			return allWall ? "walls" : (allFloor ? "floors" : "surface");
		}

		// ---- choosing the targets (brief 3.4) -----------------------------------------------------------------------

		private static int Dist2(IntVec3 a, IntVec3 b)
		{
			int dx = a.x - b.x;
			int dz = a.z - b.z;
			return dx * dx + dz * dz;
		}

		private static bool InScope(Ctx x, Cand cd)
		{
			switch (x.mode)
			{
				case "area":
					if (cd.thing != null && !(cd.thing is Pawn))
					{
						return cd.thing.OccupiedRect().Overlaps(x.rect);
					}
					return x.rect.Contains(cd.Pos);
				case "near":
					return AgentPlace.Tiles(cd.Pos, x.spot) <= NearTiles; // the larger of |dx|, |dz|, as Describe counts
				case "room":
					if (cd.thing != null && !(cd.thing is Pawn))
					{
						return cd.thing.OccupiedRect().Cells.Any((IntVec3 c) => x.roomCells.Contains(c));
					}
					return x.roomCells.Contains(cd.Pos);
			}
			return true;
		}

		// ---- refusals ----------------------------------------------------------------------------------------------

		private static string Past(string verb)
		{
			switch (verb)
			{
				case "chop": return "chopped";
				case "cut": return "cut";
				case "harvest": return "harvested";
				case "mine": return "mined";
				case "deconstruct": return "deconstructed";
				case "uninstall": return "uninstalled";
				case "haul": return "hauled";
				case "hunt": return "hunted";
				case "tame": return "tamed";
				case "slaughter": return "slaughtered";
				case "strip": return "stripped";
				case "claim": return "claimed";
				case "smooth": return "smoothed";
				case "forbid": return "forbidden";
				case "unforbid": return "unforbidden";
			}
			return verb + "ed";
		}

		private static string ExampleNoun(string verb)
		{
			switch (verb)
			{
				case "chop": return "trees";
				case "cut": return "plants";
				case "harvest": return "plants";
				case "mine": return "steel";
				case "deconstruct": return "ruins";
				case "uninstall": return "furniture";
				case "haul": return "items";
				case "hunt": return "animals";
				case "tame": return "animals";
				case "slaughter": return "animals";
				case "strip": return "corpses";
				case "claim": return "ruins";
				case "smooth": return "floors";
			}
			return "items";
		}

		private static string Upper1(string s)
		{
			return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
		}

		private static string Lower1(string s)
		{
			return string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);
		}

		// The game's own reason, made to read after a colon.
		private static string Soften(string reason)
		{
			reason = (reason ?? "").Trim().TrimEnd('.');
			if (reason.Length > 1 && char.IsUpper(reason[0]) && char.IsLower(reason[1]))
			{
				reason = Lower1(reason);
			}
			return reason;
		}

		private static string WordsText(Ctx x)
		{
			if (x.words.Count == 0)
			{
				return "items";
			}
			return string.Join(" and ", x.words.Select((Word w) => Norm(w.raw)).ToArray());
		}

		private static string NameOf(Cand cd)
		{
			Pawn p = cd.thing as Pawn;
			if (p != null)
			{
				string kind = cd.label;
				return string.Equals(p.LabelShort, kind, StringComparison.OrdinalIgnoreCase) ? kind : p.LabelShort + " the " + kind;
			}
			return cd.label;
		}

		// Why the game says no. AcceptanceReport.Reason when it has one, else the facts the designator's own test checks
		// (read from the decompiled designators), else a plain sentence.
		private static string RefusalWhy(Ctx x, Cand cd, AcceptanceReport r)
		{
			if (!string.IsNullOrEmpty(r.Reason))
			{
				return Soften(r.Reason);
			}
			Map map = x.map;
			if (x.verb == "smooth")
			{
				if (map.designationManager.DesignationAt(cd.cell, DesignationDefOf.SmoothWall) != null
					|| map.designationManager.DesignationAt(cd.cell, DesignationDefOf.SmoothFloor) != null)
				{
					return "it is already marked to be smoothed";
				}
				return "it is not a surface the game lets you smooth";
			}
			DesignationDef def = DesignationFor(x.verb);
			if (def != null)
			{
				bool marked = x.CellVerb
					? map.designationManager.DesignationAt(cd.cell, def) != null
					: (cd.thing != null && map.designationManager.DesignationOn(cd.thing, def) != null);
				if (marked)
				{
					return "it is already marked to " + x.verb;
				}
				// Designator_Mine.CanDesignateThing rejects a cell that carries MineVein too (Designator_Mine.cs:68-71).
				if (x.verb == "mine" && map.designationManager.DesignationAt(cd.cell, DesignationDefOf.MineVein) != null)
				{
					return "it is already marked to mine (as part of a vein)";
				}
			}
			Pawn p = cd.thing as Pawn;
			switch (x.verb)
			{
				case "hunt":
					if (p != null && p.Faction == Faction.OfPlayer)
					{
						return "it belongs to the colony";
					}
					if (p != null && p.Faction != null && p.Faction.def.humanlikeFaction)
					{
						return "it belongs to another people's faction";
					}
					return "it is not a wild animal that can be hunted";
				case "tame":
					if (p != null && p.Faction == Faction.OfPlayer)
					{
						return "it already belongs to the colony";
					}
					return "the game says it cannot be tamed";
				case "slaughter":
					if (p != null && p.Faction != Faction.OfPlayer)
					{
						return "it is not one of the colony's animals";
					}
					if (p != null && p.InAggroMentalState)
					{
						return "it is in a rage right now";
					}
					return "it cannot be slaughtered right now";
				case "chop":
				case "harvest":
					{
						Plant plant = cd.thing as Plant;
						if (plant != null && x.verb == "chop" && !plant.def.plant.IsTree)
						{
							return "it is not a tree";
						}
						if (plant != null && !plant.HarvestableNow)
						{
							return "it is not ready to be harvested yet";
						}
						return "the game does not let you " + x.verb + " it right now";
					}
				case "cut":
					return "the game does not let you cut it right now";
				case "forbid":
					return "it is already forbidden";
				case "unforbid":
					return "it is not forbidden";
				case "claim":
					return "it already belongs to the colony or cannot be claimed";
				case "strip":
					return "it has nothing the colony can strip";
				case "deconstruct":
					return "the colony cannot take it apart";
				case "uninstall":
					return "it cannot be uninstalled";
				case "haul":
					return "it cannot be hauled";
			}
			return "the game does not allow it right now";
		}

		private static string PlaceText(Ctx x)
		{
			switch (x.mode)
			{
				case "area": return "in " + AgentPlace.DescribeRect(x.map, x.rect);
				case "near": return "within " + NearTiles + " tiles of " + AgentPlace.Describe(x.map, x.spot);
				case "nearest": return "near " + AgentPlace.Describe(x.map, x.spot);
				case "room": return "in the " + x.roomLabel;
			}
			return "in view";
		}

		private static PlanReply Zero(Ctx x, Designator d, List<Cand> visible, List<Cand> matched, List<Cand> accepted)
		{
			string what = WordsText(x);
			List<Cand> refusedHere = matched.Where((Cand cd) => !cd.Accepted && InScope(x, cd)).ToList();
			List<Cand> refusedAnywhere = matched.Where((Cand cd) => !cd.Accepted).ToList();

			// 1. matched the words but the game refused it (here first, anywhere in view only if nothing else is true).
			if (refusedHere.Count > 0)
			{
				return Refused(x, refusedHere);
			}
			// 2. matching targets exist in view but none at that place.
			if (accepted.Count > 0 && (x.mode == "area" || x.mode == "near" || x.mode == "room"))
			{
				IntVec3 centre = x.mode == "area" ? x.rect.CenterCell : x.mode == "room" ? x.roomCells.First() : x.spot;
				Cand nearest = accepted.OrderBy((Cand cd) => Dist2(cd.Pos, centre)).ThenBy((Cand cd) => cd.id).First();
				return PlanReply.Fail("No " + what + " to " + x.verb + " " + PlaceText(x) + ". Nearest in view: " + AgentPlace.Describe(x.map, nearest.Pos) + ".");
			}
			if (refusedAnywhere.Count > 0)
			{
				return Refused(x, refusedAnywhere);
			}
			// 3. the words match nothing the verb can mark in view: say what it can.
			Dictionary<string, int> counts = new Dictionary<string, int>();
			foreach (Cand cd in visible)
			{
				EnsureAccept(x, d, cd);
				if (cd.Accepted)
				{
					int n;
					counts.TryGetValue(cd.label, out n);
					counts[cd.label] = n + 1;
				}
			}
			string past = Past(x.verb);
			if (counts.Count == 0)
			{
				return PlanReply.Fail("Nothing in view can be " + past + " right now.");
			}
			string list = string.Join(", ", counts.OrderByDescending((KeyValuePair<string, int> kv) => kv.Value).ThenBy((KeyValuePair<string, int> kv) => kv.Key)
				.Take(6).Select((KeyValuePair<string, int> kv) => kv.Key + " (" + kv.Value + ")").ToArray());
			return PlanReply.Fail("Nothing called '" + what + "' can be " + past + " in view. In view: " + list + ".");
		}

		private static PlanReply Refused(Ctx x, List<Cand> refused)
		{
			Cand first = refused[0];
			string why = RefusalWhy(x, first, first.report);
			string text = "The game won't let you " + x.verb + " " + NameOf(first) + ": " + why + ".";
			if (refused.Count > 1)
			{
				text += " (" + (refused.Count - 1) + " more matching were refused too.)";
			}
			return PlanReply.Fail(text);
		}

		private static PlanReply Capped(Ctx x, List<Cand> chosen)
		{
			string noun = OuterNoun(x) ?? WordsText(x);
			string where = x.mode == "all" ? "in view" : PlaceText(x);
			string text = "That would mark " + chosen.Count + " " + noun + " " + where + ".";
			if (x.items)
			{
				text += " Name an area (" + x.verb + " items in area X, Z to X, Z.).";
			}
			else
			{
				text += " Name an area (" + x.verb + " " + noun + " in area X, Z to X, Z.) or a count (" + x.verb + " nearest N " + noun + " near SPOT.).";
			}
			return PlanReply.Fail(text);
		}

		// ---- the confirm line (brief 3.5) ---------------------------------------------------------------------------

		private static string OuterNoun(Ctx x)
		{
			if (x.words.Count == 0)
			{
				return "items";
			}
			Word w = x.words[0];
			return GroupKeys.Contains(w.key) ? Norm(w.raw) : null;
		}

		private static string JoinAnd(List<string> parts)
		{
			if (parts.Count <= 1)
			{
				return parts.Count == 1 ? parts[0] : "";
			}
			return string.Join(", ", parts.Take(parts.Count - 1).ToArray()) + " and " + parts[parts.Count - 1];
		}

		private static string What(Ctx x, List<Cand> list)
		{
			List<KeyValuePair<string, int>> groups = list.GroupBy((Cand cd) => cd.label)
				.Select((IGrouping<string, Cand> g) => new KeyValuePair<string, int>(g.Key, g.Count()))
				.OrderByDescending((KeyValuePair<string, int> kv) => kv.Value).ThenBy((KeyValuePair<string, int> kv) => kv.Key).ToList();
			List<string> parts = groups.Take(4).Select((KeyValuePair<string, int> kv) => kv.Value + " " + kv.Key).ToList();
			string inner;
			if (groups.Count > 4)
			{
				inner = string.Join(", ", parts.ToArray()) + " and " + (groups.Count - 4) + " more kinds";
			}
			else
			{
				inner = JoinAnd(parts);
			}
			string outer = OuterNoun(x);
			if (outer != null && groups.Count > 1)
			{
				return list.Count + " " + outer + " (" + inner + ")";
			}
			return inner;
		}

		private static string Where(Ctx x, List<Cand> chosen)
		{
			string w;
			switch (x.mode)
			{
				case "area": w = "in " + AgentPlace.DescribeRect(x.map, x.rect); break;
				case "nearest": w = "nearest to " + AgentPlace.Describe(x.map, x.spot); break;
				case "near": w = "within " + NearTiles + " tiles of " + AgentPlace.Describe(x.map, x.spot); break;
				case "room": w = "in the " + x.roomLabel; break;
				default: w = "in view"; break;
			}
			if (x.mode != "all" && chosen.Count > 1)
			{
				long sx = 0;
				long sz = 0;
				foreach (Cand cd in chosen)
				{
					sx += cd.Pos.x;
					sz += cd.Pos.z;
				}
				IntVec3 centre = new IntVec3((int)(sx / chosen.Count), 0, (int)(sz / chosen.Count));
				w += ", around " + AgentPlace.Describe(x.map, centre);
			}
			return w;
		}

		// The game's slaughter warnings (SlaughterDesignatorUtility.cs:7-43), same conditions, nothing shown on screen.
		private static void SlaughterWarnings(Ctx x, List<Cand> chosen, List<string> into)
		{
			HashSet<PawnKindDef> venerated = new HashSet<PawnKindDef>();
			foreach (Cand cd in chosen)
			{
				Pawn p = cd.thing as Pawn;
				if (p == null)
				{
					continue;
				}
				if (p.RaceProps.IsFlesh)
				{
					Pawn bonded = p.relations.GetFirstDirectRelationPawn(PawnRelationDefOf.Bond, (Pawn o) => !o.Dead);
					if (bonded != null)
					{
						AddOnce(into, "MessageSlaughteringBondedAnimal".Translate(p.LabelShort, bonded.LabelShort, p.Named("DESIGNATED"), bonded.Named("BONDED")).ToString());
					}
				}
				if (venerated.Add(p.kindDef))
				{
					string v = VeneratedWarning(p);
					if (v != null)
					{
						AddOnce(into, v);
					}
				}
			}
		}

		// SlaughterDesignatorUtility.cs:19-43.
		private static string VeneratedWarning(Pawn pawn)
		{
			if (!ModsConfig.IdeologyActive || !pawn.SpawnedOrAnyParentSpawned || pawn.MapHeld.mapPawns.FreeColonistsSpawned.Count == 0)
			{
				return null;
			}
			bool all = true;
			bool any = false;
			foreach (Pawn item in pawn.MapHeld.mapPawns.FreeColonistsSpawned)
			{
				if (!item.WorkTypeIsDisabled(WorkTypeDefOf.Hunting))
				{
					any = true;
					if (item.Ideo == null || !item.Ideo.IsVeneratedAnimal(pawn))
					{
						all = false;
						break;
					}
				}
			}
			if (any && all)
			{
				return "MessageAnimalIsVeneratedForAllColonists".Translate(pawn.GetKindLabelPlural().CapitalizeFirst().Named("PAWNKINDLABELPLURAL"), Faction.OfPlayer.def.pawnsPlural.Named("PAWNS")).ToString();
			}
			return null;
		}

		private static void AddOnce(List<string> list, string s)
		{
			if (!string.IsNullOrEmpty(s) && !list.Contains(s))
			{
				list.Add(s);
			}
		}

		// Designator_Hunt.cs:91-109 and CheckHunters 111-128. The "psycho" test is the game's own: the race's raw
		// manhunterOnDamageChance (line 102 and 104); the message prints PawnUtility.GetManhunterOnDamageChance(pawn).
		private static void HuntWarnings(Ctx x, List<Cand> chosen, List<string> risk, List<string> notes)
		{
			bool anyAssigned = false;
			bool anyProper = false;
			foreach (Pawn colonist in x.map.mapPawns.FreeColonistsSpawned)
			{
				bool active = colonist.workSettings != null && colonist.workSettings.Initialized && colonist.workSettings.WorkIsActive(WorkTypeDefOf.Hunting);
				if (active)
				{
					anyAssigned = true;
				}
				if (!colonist.Downed && active && WorkGiver_HunterHunt.HasHuntingWeapon(colonist))
				{
					anyProper = true;
					break;
				}
			}
			if (!anyAssigned)
			{
				AddOnce(notes, "MessageNoHuntersAvailable".Translate().ToString());
			}
			else if (!anyProper)
			{
				AddOnce(notes, "MessageNoHuntersWithProperWeapon".Translate().ToString());
			}
			HashSet<PawnKindDef> seen = new HashSet<PawnKindDef>();
			foreach (Cand cd in chosen)
			{
				Pawn pawn = cd.thing as Pawn;
				if (pawn == null || !seen.Add(pawn.kindDef))
				{
					continue;
				}
				if (pawn.RaceProps.manhunterOnDamageChance >= 0.015f)
				{
					float chance = PawnUtility.GetManhunterOnDamageChance(pawn);
					AddOnce(risk, "MessageAnimalsGoPsychoHunted".Translate(pawn.kindDef.GetLabelPlural().CapitalizeFirst(), chance.ToStringPercent(), pawn.Named("ANIMAL")).CapitalizeFirst().ToString());
				}
				string v = VeneratedWarning(pawn);
				if (v != null)
				{
					AddOnce(notes, v);
				}
			}
		}

		// TameUtility.cs:11-45, per kind (the game warns for the first pawn of each kind).
		private static void TameWarnings(Ctx x, List<Cand> chosen, List<string> notes)
		{
			HashSet<PawnKindDef> seen = new HashSet<PawnKindDef>();
			foreach (Cand cd in chosen)
			{
				Pawn pawn = cd.thing as Pawn;
				if (pawn == null || !seen.Add(pawn.kindDef))
				{
					continue;
				}
				float failChance = PawnUtility.GetManhunterOnTameFailChance(pawn);
				if (failChance >= 0.015f)
				{
					AddOnce(notes, "MessageAnimalManhuntsOnTameFailed".Translate(pawn.kindDef.GetLabelPlural().CapitalizeFirst(), failChance.ToStringPercent(), pawn.Named("ANIMAL")).ToString());
				}
				if (pawn.RaceProps.Insect && pawn.HostileTo(Faction.OfPlayer))
				{
					AddOnce(notes, "MessageInsectsHostileOnTaming".Translate(pawn.Named("PAWN")).ToString());
				}
				List<Pawn> handlers = x.map.mapPawns.FreeColonistsSpawned.Where((Pawn p) => p.workSettings != null && p.workSettings.Initialized && p.workSettings.WorkIsActive(WorkTypeDefOf.Handling)).ToList();
				if (handlers.Count == 0)
				{
					handlers = x.map.mapPawns.FreeColonistsSpawned.ToList();
				}
				if (handlers.Count == 0)
				{
					continue;
				}
				Pawn best = null;
				int bestLevel = -1;
				foreach (Pawn h in handlers)
				{
					if (!h.health.capacities.CapableOf(PawnCapacityDefOf.Talking) || !h.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
					{
						continue;
					}
					int lvl = h.skills.GetSkill(SkillDefOf.Animals).Level;
					if (lvl > bestLevel)
					{
						bestLevel = lvl;
						best = h;
					}
				}
				if (best != null)
				{
					int need = TrainableUtility.MinimumHandlingSkill(pawn);
					if (need > bestLevel)
					{
						AddOnce(notes, "MessageNoHandlerSkilledEnough".Translate(pawn.kindDef.label, need.ToStringCached(), SkillDefOf.Animals.LabelCap, best.LabelShort, bestLevel, pawn.Named("ANIMAL"), best.Named("HANDLER")).ToString());
					}
				}
			}
		}

		// ---- the plan -----------------------------------------------------------------------------------------------

		private static PlanReply Build(FormContext c, Ctx x, List<Cand> chosen)
		{
			string what = What(x, chosen);
			string where = Where(x, chosen);
			string verbCap = Upper1(x.verb);

			bool risky = false;
			List<string> riskText = new List<string>();
			List<string> notes = new List<string>();
			string core;
			switch (x.verb)
			{
				case "slaughter":
				{
					risky = true;
					List<string> names = chosen.Select((Cand cd) => ((Pawn)cd.thing).LabelShort).ToList();
					string nameList = names.Count <= 6 ? string.Join(", ", names.ToArray()) : string.Join(", ", names.Take(6).ToArray()) + " and " + (names.Count - 6) + " more";
					core = verbCap + " " + what + " (" + nameList + ") " + where + ".";
					riskText.Add(chosen.Count == 1 ? "It will be killed." : "They will be killed.");
					SlaughterWarnings(x, chosen, riskText);
					break;
				}
				case "hunt":
				{
					core = verbCap + " " + what + " " + where + ".";
					List<string> psycho = new List<string>();
					HuntWarnings(x, chosen, psycho, notes);
					if (psycho.Count > 0)
					{
						risky = true;
						riskText.AddRange(psycho);
					}
					break;
				}
				case "tame":
					core = verbCap + " " + what + " " + where + ".";
					TameWarnings(x, chosen, notes);
					break;
				case "deconstruct":
				{
					core = verbCap + " " + what + " " + where + ".";
					int own = chosen.Count((Cand cd) => cd.thing != null && cd.thing.Faction == Faction.OfPlayer);
					if (own > 0)
					{
						risky = true;
						riskText.Add(own == chosen.Count
							? (own == 1 ? "It is one of the colony's own buildings." : "All " + own + " are the colony's own buildings.")
							: own + " of them " + (own == 1 ? "is one of" : "are") + " the colony's own buildings.");
					}
					break;
				}
				default:
					core = verbCap + " " + what + " " + where + ".";
					break;
			}

			string body = core;
			if (riskText.Count > 0)
			{
				body += " " + string.Join(" ", riskText.ToArray());
			}
			if (notes.Count > 0)
			{
				body += " Note: " + string.Join(" ", notes.ToArray());
			}
			string line = risky ? "RISKY: " + Lower1(body) : body;

			string label = x.verb + " " + what;
			List<Cand> targets = chosen.ToList();
			Plan plan = FormPlanner.NewPlan(c.form);
			Write write = new Write();
			write.description = body;
			write.apply = delegate
			{
				ApplyAll(x, targets, what, label);
			};
			plan.writes.Add(write.Reads(() => ReadLine(x, targets, what)));
			string unripe = UnripeNote(x, chosen);
			if (unripe != null)
			{
				plan.notes.Add(unripe);
			}
			return FormPlanner.Reply(plan, line, null, null);
		}

		// Harvesting before full growth (user, 2026-10-14: "[reminder] note harvesting plants now may not return anyhing
		// because it's at 65% grown." / "add this whenever agents try to manually harvest. let them harvest, and just send
		// as reminder. doesnt fire if all of the plants on harvest is 100%. if its not, it gives [plant, coordinate,
		// coordinate] as plants not ready to harvest because its not 100%"). A note only: the order goes through as
		// before and does not wait for confirm (only Plan.askConfirm does, OrderController.cs:715).
		//   harvest and chop (both the HarvestPlant designation). Not fully grown = the game's Mature stage not reached
		//   (growth > 0.999, RimWorld\Plant.cs:462). The game only lets a plant be marked past its harvestMinGrowth
		//   (Designator_PlantsHarvest.cs:29, Plant.HarvestableNow :109-119), so a marked plant always yields something:
		//   harvestYield x (0.5 + 0.5 x how far it is from harvestMinGrowth to full), x its health (Plant.YieldNow,
		//   :932-948). The note says that rather than "nothing".
		private const int UnripeListed = 10;

		private static string UnripeNote(Ctx x, List<Cand> chosen)
		{
			if (x.verb != "harvest" && x.verb != "chop")
			{
				return null;
			}
			List<Plant> unripe = chosen.Select((Cand cd) => cd.thing as Plant)
				.Where((Plant p) => p != null && p.Growth <= 0.999f).OrderBy((Plant p) => p.Growth).ToList();
			if (unripe.Count == 0)
			{
				return null;
			}
			List<string> listed = unripe.Take(UnripeListed)
				.Select((Plant p) => "[" + p.def.label + ", " + AgentWorkspotCheck.Xz(p.Position) + "] " + (int)(p.Growth * 100f) + "%").ToList();
			if (unripe.Count > UnripeListed)
			{
				listed.Add("and " + (unripe.Count - UnripeListed) + " more");
			}
			return "[REMINDER] Note: harvesting plants now may return less, because " + unripe.Count + " of the " + chosen.Count
				+ (chosen.Count == 1 ? " plant is" : " plants are") + " not 100% grown and not ready to harvest: " + string.Join(", ", listed.ToArray())
				+ ". A plant cut before it is fully grown gives only 50% to 100% of its full yield (less the less it has grown); waiting gives the full yield.";
		}

		// What the playing agent sees before and after: how many of the planned targets carry the mark now.
		private static string ReadLine(Ctx x, List<Cand> targets, string what)
		{
			int n = 0;
			Map map = x.map;
			foreach (Cand cd in targets)
			{
				if (Carries(x, map, cd))
				{
					n++;
				}
			}
			switch (x.verb)
			{
				case "forbid":
				case "unforbid":
					return what + ": " + n + " forbidden";
				case "claim":
					return what + ": " + n + " belong to the colony";
			}
			return what + ": " + n + " marked to " + x.verb;
		}

		private static bool Carries(Ctx x, Map map, Cand cd)
		{
			DesignationManager dm = map.designationManager;
			switch (x.verb)
			{
				case "forbid":
				case "unforbid":
				{
					if (cd.thing == null)
					{
						return false;
					}
					CompForbiddable comp = cd.thing.TryGetComp<CompForbiddable>();
					return comp != null && comp.Forbidden;
				}
				case "claim":
					return cd.thing != null && cd.thing.Faction == Faction.OfPlayer;
				case "smooth":
					return dm.DesignationAt(cd.cell, DesignationDefOf.SmoothWall) != null || dm.DesignationAt(cd.cell, DesignationDefOf.SmoothFloor) != null;
				case "mine":
					return dm.DesignationAt(cd.cell, DesignationDefOf.Mine) != null;
			}
			DesignationDef def = DesignationFor(x.verb);
			return def != null && cd.thing != null && dm.DesignationOn(cd.thing, def) != null;
		}

		// ---- the write (brief 3.6) ----------------------------------------------------------------------------------

		private static void ApplyAll(Ctx x, List<Cand> targets, string what, string label)
		{
			if (Find.CurrentMap != x.map)
			{
				throw new Fault("That map is no longer the one on screen, so nothing was marked.");
			}
			Designator d = MakeDesignator(x);
			List<Cand> done = new List<Cand>();
			string firstReason = null;
			foreach (Cand cd in targets)
			{
				string why = null;
				if (cd.thing != null && (!cd.thing.Spawned || cd.thing.Destroyed))
				{
					why = "it is gone";
				}
				else if (!AgentPlace.Visible(x.map, cd.Pos))
				{
					why = "it is no longer in view";
				}
				else
				{
					AcceptanceReport r = Accept(x, d, cd);
					if (!r.Accepted)
					{
						why = RefusalWhy(x, cd, r);
					}
				}
				if (why != null)
				{
					if (firstReason == null)
					{
						firstReason = why;
					}
					continue;
				}
				if (x.CellVerb)
				{
					d.DesignateSingleCell(cd.cell);
				}
				else
				{
					d.DesignateThing(cd.thing);
				}
				done.Add(cd);
			}
			if (done.Count == 0)
			{
				string action = x.CellVerb || DesignationFor(x.verb) != null ? "marked" : Past(x.verb);
				throw new Fault("None of the " + targets.Count + " " + what + " can still be " + action + ": " + (firstReason ?? "the game no longer allows it") + ".");
			}
			TrackDone(x, done, label);
		}

		// Agent 02: follow the batch to its end. claim, forbid and unforbid have no designation (they act at once).
		private static void TrackDone(Ctx x, List<Cand> done, string label)
		{
			Map map = x.map;
			if (x.verb == "claim" || x.items)
			{
				return;
			}
			if (x.verb == "smooth")
			{
				List<LocalTargetInfo> walls = new List<LocalTargetInfo>();
				List<LocalTargetInfo> floors = new List<LocalTargetInfo>();
				foreach (Cand cd in done)
				{
					if (map.designationManager.DesignationAt(cd.cell, DesignationDefOf.SmoothWall) != null)
					{
						walls.Add(new LocalTargetInfo(cd.cell));
					}
					else if (map.designationManager.DesignationAt(cd.cell, DesignationDefOf.SmoothFloor) != null)
					{
						floors.Add(new LocalTargetInfo(cd.cell));
					}
				}
				if (walls.Count > 0)
				{
					AgentFollow.Track(map, walls, DesignationDefOf.SmoothWall, label);
				}
				if (floors.Count > 0)
				{
					AgentFollow.Track(map, floors, DesignationDefOf.SmoothFloor, label);
				}
				return;
			}
			DesignationDef def = DesignationFor(x.verb);
			if (def == null)
			{
				return;
			}
			List<LocalTargetInfo> targets = new List<LocalTargetInfo>();
			foreach (Cand cd in done)
			{
				targets.Add(x.CellVerb ? new LocalTargetInfo(cd.cell) : new LocalTargetInfo(cd.thing));
			}
			AgentFollow.Track(map, targets, def, label);
		}
	}
}
