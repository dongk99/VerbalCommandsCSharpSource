// Agent 02 (agent_player brief C): before/after values of a write (AgentReadback) and following an order
// to its end (AgentFollow). Signatures: agent_player\out\02\forms.md sections 1, 2 and 3b.
//
// Persistence: none. The followed list and the readers live in memory only; AgentTime.Tick calls
// AgentFollow.Reset when a different game is loaded or started, so a save/load ends following.
//
// Everything here runs on the main thread. Nothing here changes the game: the readers and the checks only
// look (fair play: a player could see the same on screen; nothing under fog is read, nothing is spawned,
// finished or moved).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;
using Verse.AI;

namespace VerbalCommands
{
	public static class AgentReadback
	{
		public const string NotReported = "not reported";

		private const int MaxLine = 200;

		private static readonly ConditionalWeakTable<Write, Func<string>> readers = new ConditionalWeakTable<Write, Func<string>>();

		// Form files: attach a reader to a Write (see forms.md section 1). Returns the same Write.
		public static Write Reads(this Write write, Func<string> read)
		{
			if (write == null || read == null)
			{
				return write;
			}
			readers.Remove(write);
			readers.Add(write, read);
			return write;
		}

		// OrderController.ApplyAll: the write's own reader, else the built-in reader of plan.toolName, else
		// NotReported. Never throws; one line of at most 200 characters.
		public static string Read(Plan plan, Write write)
		{
			try
			{
				Func<string> own;
				if (write != null && readers.TryGetValue(write, out own))
				{
					return OneLine(own());
				}
				if (plan == null)
				{
					return NotReported;
				}
				return OneLine(BuiltIn(plan));
			}
			catch (Exception ex)
			{
				return OneLine("read failed: " + ex.Message);
			}
		}

		private static string OneLine(string s)
		{
			if (string.IsNullOrWhiteSpace(s))
			{
				return NotReported;
			}
			s = s.Replace("\r", " ").Replace("\n", " ").Trim();
			if (s.Length > MaxLine)
			{
				s = s.Substring(0, MaxLine - 3) + "...";
			}
			return s;
		}

		// plan.toolName strings (all checked in the code, see the report):
		//   "game speed"            FormWave1.cs:283 (NewPlan)
		//   "research start"        FormWave1.cs:362
		//   "draft"                 FormPlanner.cs:563
		//   "undraft"               FormGroups.cs:218
		//   "set_work_priorities"   Executor.cs:432 (forms "work priority" and "work off" return that Resolve plan,
		//                           FormPlanner.cs:542-553)
		//   "set_schedule"          FormPlanner.cs:470 (form "schedule" returns a plan of that name)
		//   "set_storage"           Executor.cs:851 / FormStorage.cs:375 (storage priority: not tied here, see report)
		// The short form names are accepted too, in case a plan is ever named after its form.
		private static string BuiltIn(Plan plan)
		{
			switch (plan.toolName)
			{
				case "game speed":
					return ReadGameSpeed();
				case "research start":
					return ReadResearch();
				case "draft":
				case "undraft":
					return ReadDrafted();
				case "set_work_priorities":
				case "work priority":
				case "work off":
					return ReadWork(Slots(plan));
				case "set_schedule":
				case "schedule":
					return ReadSchedule(Slots(plan));
				default:
					return NotReported;
			}
		}

		private static JObject Slots(Plan plan)
		{
			if (string.IsNullOrEmpty(plan.slotsJson))
			{
				return null;
			}
			return JToken.Parse(plan.slotsJson) as JObject;
		}

		private static string SlotText(JObject slots, string key)
		{
			if (slots == null)
			{
				return null;
			}
			JToken t = slots[key];
			if (t == null)
			{
				return null;
			}
			JArray arr = t as JArray;
			if (arr != null)
			{
				t = arr.Count > 0 ? arr[0] : null;
				if (t == null)
				{
					return null;
				}
			}
			string s = t.Type == JTokenType.String ? (string)t : t.ToString(Newtonsoft.Json.Formatting.None);
			return s == null ? null : s.Trim();
		}

		private static int? SlotInt(JObject slots, string key)
		{
			string s = SlotText(slots, key);
			double d;
			if (s != null && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d))
			{
				return (int)Math.Floor(d);
			}
			return null;
		}

		private static List<Pawn> Colonists()
		{
			Map map = Find.CurrentMap;
			if (map == null || map.mapPawns == null)
			{
				return new List<Pawn>();
			}
			return map.mapPawns.FreeColonists.ToList();
		}

		// "speed: fast" / "speed: paused (resumes at fast)". TickManager.CurTimeSpeed, prePauseTimeSpeed
		// (Verse\TickManager.cs:23, 187; TogglePaused 213-230 sets curTimeSpeed back to prePauseTimeSpeed).
		private static string ReadGameSpeed()
		{
			TickManager tm = Find.TickManager;
			if (tm == null)
			{
				return NotReported;
			}
			if (tm.CurTimeSpeed == TimeSpeed.Paused)
			{
				TimeSpeed back = tm.prePauseTimeSpeed;
				return back == TimeSpeed.Paused ? "speed: paused" : "speed: paused (resumes at " + back.ToString().ToLowerInvariant() + ")";
			}
			return "speed: " + tm.CurTimeSpeed.ToString().ToLowerInvariant();
		}

		// "research: Electricity (120 / 1600)" / "research: none". ResearchManager.GetProject
		// (RimWorld\ResearchManager.cs:155), ProgressApparentString / CostApparent as the research tab prints them
		// (Verse\ResearchProjectDef.cs:103-114, 462-464).
		private static string ReadResearch()
		{
			ResearchManager rm = Find.ResearchManager;
			if (rm == null)
			{
				return NotReported;
			}
			ResearchProjectDef p = rm.GetProject();
			if (p == null)
			{
				return "research: none";
			}
			return "research: " + p.label + " (" + p.ProgressApparentString + " / " + p.CostApparent.ToString("F0") + ")";
		}

		// "drafted: Bob, Ann; not drafted: Cid". Pawn_DraftController.Drafted (RimWorld\Pawn_DraftController.cs:19).
		private static string ReadDrafted()
		{
			List<Pawn> all = Colonists();
			if (all.Count == 0)
			{
				return NotReported;
			}
			List<string> yes = new List<string>();
			List<string> no = new List<string>();
			foreach (Pawn p in all)
			{
				if (p.drafter != null && p.drafter.Drafted)
				{
					yes.Add(p.LabelShort);
				}
				else
				{
					no.Add(p.LabelShort);
				}
			}
			return "drafted: " + (yes.Count > 0 ? string.Join(", ", yes.ToArray()) : "none") + "; not drafted: " + (no.Count > 0 ? string.Join(", ", no.ToArray()) : "none");
		}

		// "Cooking: Bob 3, Ann off, Cid cannot". The work type is matched like FormPlanner.PlanWorkPriority does
		// (labelShort, gerundLabel, pawnLabel, label, defName; ignoring case). Pawn_WorkSettings.GetPriority /
		// WorkIsActive (RimWorld\Pawn_WorkSettings.cs:160-175), Pawn.WorkTypeIsDisabled (Verse\Pawn.cs:4476).
		private static string ReadWork(JObject slots)
		{
			string text = SlotText(slots, "WORKTYPE");
			if (string.IsNullOrEmpty(text))
			{
				return NotReported;
			}
			WorkTypeDef work = null;
			foreach (WorkTypeDef def in DefDatabase<WorkTypeDef>.AllDefsListForReading)
			{
				if (SameWord(def.labelShort, text) || SameWord(def.gerundLabel, text) || SameWord(def.pawnLabel, text)
					|| SameWord(def.label, text) || SameWord(def.defName, text))
				{
					work = def;
					break;
				}
			}
			if (work == null)
			{
				return NotReported;
			}
			List<string> parts = new List<string>();
			foreach (Pawn p in Colonists())
			{
				if (p.workSettings == null)
				{
					continue;
				}
				string value;
				if (p.WorkTypeIsDisabled(work))
				{
					value = "cannot";
				}
				else if (!p.workSettings.WorkIsActive(work))
				{
					value = "off";
				}
				else
				{
					value = p.workSettings.GetPriority(work).ToString();
				}
				parts.Add(p.LabelShort + " " + value);
			}
			if (parts.Count == 0)
			{
				return NotReported;
			}
			string name = !string.IsNullOrEmpty(work.gerundLabel) ? work.gerundLabel : (!string.IsNullOrEmpty(work.labelShort) ? work.labelShort : work.defName);
			return char.ToUpperInvariant(name[0]) + name.Substring(1) + ": " + string.Join(", ", parts.ToArray());
		}

		private static bool SameWord(string a, string b)
		{
			return !string.IsNullOrEmpty(a) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
		}

		// "Bob 22-5 sleep; Ann 22-2 sleep, 2-5 anything": per colonist the assignments of hours FROM..TO (TO is
		// the first hour NOT included, as FormPlanner.PlanSchedule loops: FormPlanner.cs:483), wrapping past 23.
		// A run "a-b" is the hours a:00 up to b:00. Pawn_TimetableTracker.GetAssignment (RimWorld\Pawn_TimetableTracker.cs:51).
		private static string ReadSchedule(JObject slots)
		{
			int? from = SlotInt(slots, "FROM");
			int? to = SlotInt(slots, "TO");
			if (from == null || to == null || from < 0 || from > 23 || to < 0 || to > 23)
			{
				return NotReported;
			}
			List<string> perPawn = new List<string>();
			foreach (Pawn p in Colonists())
			{
				if (p.timetable == null)
				{
					continue;
				}
				List<string> runs = new List<string>();
				string runLabel = null;
				int runStart = 0;
				int last = 0;
				int h = from.Value;
				int count = 0;
				do
				{
					string label = p.timetable.GetAssignment(h).label.ToLowerInvariant();
					if (label != runLabel)
					{
						if (runLabel != null)
						{
							runs.Add(runStart + "-" + ((last + 1) % 24) + " " + runLabel);
						}
						runLabel = label;
						runStart = h;
					}
					last = h;
					h = (h + 1) % 24;
					count++;
				}
				while (h != to && count < 24);
				if (runLabel != null)
				{
					runs.Add(runStart + "-" + ((last + 1) % 24) + " " + runLabel);
				}
				perPawn.Add(p.LabelShort + " " + string.Join(", ", runs.ToArray()));
			}
			return perPawn.Count == 0 ? NotReported : string.Join("; ", perPawn.ToArray());
		}
	}

	public static class AgentFollow
	{
		private const int MaxItems = 200;
		private const int CheckEveryTicks = 250;
		private const int MaxAnswerLines = 60;

		private sealed class Item
		{
			public int id;
			public string kind;
			public string label;
			public string orderId;
			public int startTick;
			public Func<string> check;
			public Func<string> status;
			public object subject;
			public readonly HashSet<int> thingIds = new HashSet<int>();
			public bool failedLogged;
		}

		private static readonly List<Item> items = new List<Item>();
		private static readonly HashSet<int> followedThings = new HashSet<int>();
		private static int nextId = 1;
		private static int lastCheckTick = -1;
		private static string currentOrder;
		private static bool loggedFailure;

		// ---- core ------------------------------------------------------------------------------------------

		public static void Track(string kind, string label, Func<string> check, Func<string> status = null)
		{
			try
			{
				if (check == null)
				{
					return;
				}
				Item it = MakeItem(kind, label, status);
				it.check = check;
				AddItem(it);
			}
			catch (Exception ex)
			{
				LogOnce("Track", ex);
			}
		}

		private static Item MakeItem(string kind, string label, Func<string> status)
		{
			Item it = new Item();
			it.id = nextId++;
			it.kind = kind ?? "";
			it.label = string.IsNullOrEmpty(label) ? (string.IsNullOrEmpty(kind) ? "order" : kind) : label;
			it.orderId = currentOrder;
			it.startTick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
			it.status = status;
			return it;
		}

		private static void AddItem(Item it)
		{
			while (items.Count >= MaxItems)
			{
				Item oldest = items[0];
				items.RemoveAt(0);
				Emit(oldest, "no longer followed (list full)");
			}
			items.Add(it);
			RebuildThingSet();
		}

		private static void RebuildThingSet()
		{
			followedThings.Clear();
			foreach (Item it in items)
			{
				foreach (int id in it.thingIds)
				{
					followedThings.Add(id);
				}
			}
		}

		private static void Remember(Item it, Thing t)
		{
			if (t != null && it.thingIds.Add(t.thingIDNumber))
			{
				followedThings.Add(t.thingIDNumber);
			}
		}

		private static void Emit(Item it, string text)
		{
			Emit(it.label, text, it.orderId);
		}

		private static void Emit(string label, string text, string orderId)
		{
			try
			{
				AgentEvents.Add("followed", label + ": " + text, orderId);
			}
			catch (Exception ex)
			{
				LogOnce("AgentEvents.Add", ex);
			}
		}

		private static void LogOnce(string where, Exception ex)
		{
			if (loggedFailure)
			{
				return;
			}
			loggedFailure = true;
			Log.Error("[VerbalCommands] AgentFollow " + where + " failed (logged once): " + ex);
		}

		// AgentTime.Tick, once per game tick: every 250 ticks run each item's check.
		public static void Tick()
		{
			try
			{
				if (items.Count == 0)
				{
					return;
				}
				TickManager tm = Find.TickManager;
				if (tm == null)
				{
					return;
				}
				int now = tm.TicksGame;
				if (lastCheckTick >= 0 && now >= lastCheckTick && now - lastCheckTick < CheckEveryTicks)
				{
					return;
				}
				lastCheckTick = now;
				bool removedAny = false;
				foreach (Item it in items.ToList())
				{
					string result;
					try
					{
						result = it.check();
					}
					catch (Exception ex)
					{
						result = "could not be followed: " + ex.Message;
						if (!it.failedLogged)
						{
							it.failedLogged = true;
							Log.Warning("[VerbalCommands] following '" + it.label + "' failed: " + ex);
						}
					}
					if (result != null)
					{
						items.Remove(it);
						removedAny = true;
						Emit(it, result);
					}
				}
				if (removedAny)
				{
					RebuildThingSet();
				}
			}
			catch (Exception ex)
			{
				LogOnce("Tick", ex);
			}
		}

		public static void Reset()
		{
			items.Clear();
			followedThings.Clear();
			lastCheckTick = -1;
			currentOrder = null;
		}

		// ---- building: blueprint -> frame -> building ------------------------------------------------------

		// What one blueprint/frame is now: state 0 blueprint, 1 frame, 2 built, 3 cancelled or destroyed.
		private sealed class Piece
		{
			public Map map;
			public IntVec3 cell;
			public BuildableDef def;
			public ThingDef stuff;
			public int state;
			public Thing current;
			public bool forbiddenReported;
		}

		private static Piece MakePiece(Thing t)
		{
			Piece p = new Piece();
			Blueprint bp = t as Blueprint;
			Frame fr = t as Frame;
			if (bp != null)
			{
				// Blueprint.EntityToBuild / EntityToBuildStuff: RimWorld\Blueprint.cs:150-154.
				p.def = bp.EntityToBuild();
				p.stuff = bp.EntityToBuildStuff();
				p.state = 0;
			}
			else if (fr != null)
			{
				// Frame: def.entityDefToBuild (Verse\ThingDef.cs:373), Frame.EntityToBuildStuff (RimWorld\Frame.cs:206).
				p.def = fr.def.entityDefToBuild;
				p.stuff = fr.EntityToBuildStuff();
				p.state = 1;
			}
			else
			{
				return null;
			}
			p.map = t.Map;
			p.cell = t.Position;
			p.current = t;
			return p;
		}

		private static bool StuffMatches(ThingDef wanted, ThingDef actual)
		{
			return wanted == null || actual == null || wanted == actual;
		}

		// Looks at the cell: a blueprint of the def (Blueprint_Build.EntityToBuild, RimWorld\Blueprint_Build.cs:55),
		// a frame of the def (a blueprint becomes Def.frameDef, Blueprint_Build.cs:72), the finished building (a
		// Frame destroys itself and spawns the building, RimWorld\Frame.cs:262-285), or for a floor the terrain
		// (GridsUtility.GetTerrain, Verse\GridsUtility.cs:116).
		private static void Scan(Piece p)
		{
			if (p.state >= 2)
			{
				return;
			}
			if (p.map == null || Find.Maps == null || !Find.Maps.Contains(p.map))
			{
				p.state = 3;
				p.current = null;
				return;
			}
			ThingDef thingDef = p.def as ThingDef;
			int state = 3;
			Thing found = null;
			List<Thing> things = p.cell.GetThingList(p.map);
			for (int i = 0; i < things.Count; i++)
			{
				Thing t = things[i];
				Blueprint bp = t as Blueprint;
				if (bp != null)
				{
					if (bp.EntityToBuild() == p.def && StuffMatches(p.stuff, bp.EntityToBuildStuff()))
					{
						state = 0;
						found = t;
						break;
					}
					continue;
				}
				Frame fr = t as Frame;
				if (fr != null)
				{
					if (fr.def.entityDefToBuild == p.def && StuffMatches(p.stuff, fr.EntityToBuildStuff()))
					{
						state = 1;
						found = t;
						break;
					}
					continue;
				}
				if (thingDef != null && t.def == thingDef && StuffMatches(p.stuff, t.Stuff))
				{
					state = 2;
					found = t;
					break;
				}
			}
			if (state == 3)
			{
				TerrainDef terrain = p.def as TerrainDef;
				if (terrain != null && p.cell.GetTerrain(p.map) == terrain)
				{
					state = 2;
				}
			}
			p.state = state;
			p.current = state < 2 ? found : null;
		}

		private static string CellText(IntVec3 c)
		{
			return c.x + ", " + c.z;
		}

		// A Blueprint_Build or Frame: follows blueprint -> frame -> building at the same cell and def.
		public static void Track(Thing blueprintOrFrame, string label = null)
		{
			try
			{
				if (blueprintOrFrame == null || followedThings.Contains(blueprintOrFrame.thingIDNumber))
				{
					return;
				}
				Piece piece = MakePiece(blueprintOrFrame);
				if (piece == null)
				{
					return;
				}
				string orderId = currentOrder;
				Item it = MakeItem("build", string.IsNullOrEmpty(label) ? "build " + piece.def.label + " at " + CellText(piece.cell) : label, null);
				it.thingIds.Add(blueprintOrFrame.thingIDNumber);
				it.check = delegate
				{
					Scan(piece);
					if (piece.state == 2)
					{
						return "built";
					}
					if (piece.state == 3)
					{
						return "cancelled or destroyed";
					}
					Remember(it, piece.current);
					if (!piece.forbiddenReported && piece.current != null && piece.current.IsForbidden(Faction.OfPlayer))
					{
						// ForbidUtility.IsForbidden(Thing, Faction): RimWorld\ForbidUtility.cs:191.
						piece.forbiddenReported = true;
						Emit(it.label, "forbidden, colonists will not build it", orderId);
					}
					return null;
				};
				it.status = delegate { return piece.state == 1 ? "frame" : "blueprint"; };
				AddItem(it);
			}
			catch (Exception ex)
			{
				LogOnce("Track(Thing)", ex);
			}
		}

		// Many blueprints/frames as ONE item. Already-tracked things are skipped.
		public static void Track(IEnumerable<Thing> blueprintsOrFrames, string label)
		{
			try
			{
				if (blueprintsOrFrames == null)
				{
					return;
				}
				List<Piece> pieces = new List<Piece>();
				List<Thing> things = new List<Thing>();
				foreach (Thing t in blueprintsOrFrames)
				{
					if (t == null || followedThings.Contains(t.thingIDNumber) || things.Contains(t))
					{
						continue;
					}
					Piece p = MakePiece(t);
					if (p == null)
					{
						continue;
					}
					things.Add(t);
					pieces.Add(p);
				}
				if (pieces.Count == 0)
				{
					return;
				}
				string orderId = currentOrder;
				Item it = MakeItem("build", string.IsNullOrEmpty(label) ? "build: " + pieces.Count + " blueprints" : label, null);
				foreach (Thing t in things)
				{
					it.thingIds.Add(t.thingIDNumber);
				}
				it.check = delegate
				{
					int newlyForbidden = 0;
					foreach (Piece p in pieces)
					{
						if (p.state >= 2)
						{
							continue;
						}
						Scan(p);
						if (p.state >= 2)
						{
							continue;
						}
						Remember(it, p.current);
						if (!p.forbiddenReported && p.current != null && p.current.IsForbidden(Faction.OfPlayer))
						{
							p.forbiddenReported = true;
							newlyForbidden++;
						}
					}
					if (newlyForbidden > 0)
					{
						Emit(it.label, newlyForbidden + " forbidden, colonists will not build " + (newlyForbidden == 1 ? "it" : "them"), orderId);
					}
					if (pieces.Any((Piece p) => p.state < 2))
					{
						return null;
					}
					int built = pieces.Count((Piece p) => p.state == 2);
					int gone = pieces.Count((Piece p) => p.state == 3);
					return "built " + built + " of " + pieces.Count + (gone > 0 ? ", " + gone + " cancelled" : "");
				};
				it.status = delegate
				{
					int built = pieces.Count((Piece p) => p.state == 2);
					int frames = pieces.Count((Piece p) => p.state == 1);
					int blueprints = pieces.Count((Piece p) => p.state == 0);
					int gone = pieces.Count((Piece p) => p.state == 3);
					List<string> parts = new List<string>();
					if (built > 0)
					{
						parts.Add(built + " built");
					}
					if (frames > 0)
					{
						parts.Add(frames + (frames == 1 ? " frame" : " frames"));
					}
					if (blueprints > 0)
					{
						parts.Add(blueprints + (blueprints == 1 ? " blueprint" : " blueprints"));
					}
					if (gone > 0)
					{
						parts.Add(gone + " cancelled");
					}
					return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "none left";
				};
				AddItem(it);
			}
			catch (Exception ex)
			{
				LogOnce("Track(things)", ex);
			}
		}

		// ---- bill ------------------------------------------------------------------------------------------

		// A Bill_Production: "bill done" (repeat count 0 / target count reached) or "bill deleted"; Forever ends
		// only when deleted. Bill.DeletedOrDereferenced (RimWorld\Bill.cs:111), Bill_Production.repeatMode /
		// repeatCount / targetCount (RimWorld\Bill_Production.cs:12-22), modes BillRepeatModeDefOf
		// (RimWorld\BillRepeatModeDefOf.cs), RecipeWorkerCounter.CanCountProducts / CountProducts
		// (Verse\RecipeWorkerCounter.cs:10-19; the bill itself shows "count/target" with it, Bill_Production.cs:80).
		public static void Track(Bill bill, string label = null)
		{
			try
			{
				if (bill == null)
				{
					return;
				}
				Bill_Production prod = bill as Bill_Production;
				string recipe = bill.recipe != null ? bill.recipe.label : "bill";
				string bench = "";
				if (bill.billStack != null && bill.billStack.billGiver != null)
				{
					bench = bill.billStack.billGiver.LabelShort;
				}
				Item it = MakeItem("bill", string.IsNullOrEmpty(label) ? "bill " + recipe + (bench.Length > 0 ? " at " + bench : "") : label, null);
				it.subject = bill;
				it.check = delegate
				{
					if (bill.DeletedOrDereferenced)
					{
						return "bill deleted";
					}
					if (prod == null)
					{
						return null;
					}
					if (prod.repeatMode == BillRepeatModeDefOf.RepeatCount)
					{
						return prod.repeatCount <= 0 ? "bill done" : null;
					}
					if (prod.repeatMode == BillRepeatModeDefOf.TargetCount)
					{
						if (CanCount(prod) && prod.recipe.WorkerCounter.CountProducts(prod) >= prod.targetCount)
						{
							return "target count reached";
						}
					}
					return null;
				};
				it.status = delegate
				{
					if (prod == null)
					{
						return null;
					}
					if (prod.repeatMode == BillRepeatModeDefOf.Forever)
					{
						return "repeats forever";
					}
					if (prod.repeatMode == BillRepeatModeDefOf.RepeatCount)
					{
						return prod.repeatCount + " left";
					}
					if (prod.repeatMode == BillRepeatModeDefOf.TargetCount && CanCount(prod))
					{
						return prod.recipe.WorkerCounter.CountProducts(prod) + " / " + prod.targetCount;
					}
					return null;
				};
				AddItem(it);
			}
			catch (Exception ex)
			{
				LogOnce("Track(Bill)", ex);
			}
		}

		private static bool CanCount(Bill_Production prod)
		{
			return prod.recipe != null && prod.recipe.WorkerCounter != null && prod.recipe.WorkerCounter.CanCountProducts(prod) && prod.Map != null;
		}

		// ---- research --------------------------------------------------------------------------------------

		// "research complete" (ResearchProjectDef.IsFinished, Verse\ResearchProjectDef.cs:120), "research switched to X"
		// / "research stopped" (ResearchManager.IsCurrentProject / GetProject, RimWorld\ResearchManager.cs:155-171).
		public static void Track(ResearchProjectDef project, string label = null)
		{
			try
			{
				if (project == null)
				{
					return;
				}
				Item it = MakeItem("research", string.IsNullOrEmpty(label) ? "research " + project.label : label, null);
				it.subject = project;
				it.check = delegate
				{
					if (project.IsFinished)
					{
						return "research complete";
					}
					ResearchManager rm = Find.ResearchManager;
					if (rm.IsCurrentProject(project))
					{
						return null;
					}
					ResearchProjectDef now = rm.GetProject();
					return now == null ? "research stopped" : "research switched to " + now.label;
				};
				it.status = delegate { return project.ProgressApparentString + " / " + project.CostApparent.ToString("F0"); };
				AddItem(it);
			}
			catch (Exception ex)
			{
				LogOnce("Track(research)", ex);
			}
		}

		// ---- designations ----------------------------------------------------------------------------------

		// Pending while any target still carries def (DesignationManager.DesignationOn / DesignationAt,
		// Verse\DesignationManager.cs:253, 275; DesignationDef.targetType, Verse\DesignationDef.cs:10).
		// A target counts DONE when its thing is destroyed (also: a hunted/slaughtered pawn that died, a harvested
		// plant that is no longer harvestable), or, for a cell target, when the cell no longer has the thing the
		// designation was for: mining = no Mineable at the cell (GridsUtility.GetFirstMineable, Verse\GridsUtility.cs:362).
		// Other cell designations (smoothing and the like) are removed by the game when the work is done and are
		// counted done: a cancel cannot be told from a finish for them. Anything else that lost its designation
		// counts as "removed".
		public static void Track(Map map, IEnumerable<LocalTargetInfo> targets, DesignationDef def, string label)
		{
			try
			{
				if (map == null || targets == null || def == null)
				{
					return;
				}
				List<LocalTargetInfo> list = targets.Where((LocalTargetInfo t) => t.IsValid).ToList();
				if (list.Count == 0)
				{
					return;
				}
				int[] state = new int[list.Count]; // 0 pending, 1 done, 2 removed
				Item it = MakeItem("designation", string.IsNullOrEmpty(label) ? def.defName + ": " + list.Count + " targets" : label, null);
				it.check = delegate
				{
					if (Find.Maps == null || !Find.Maps.Contains(map))
					{
						return "map no longer loaded";
					}
					for (int i = 0; i < list.Count; i++)
					{
						if (state[i] != 0 || HasDesignation(map, list[i], def))
						{
							continue;
						}
						state[i] = IsDone(map, list[i], def) ? 1 : 2;
					}
					if (state.Any((int s) => s == 0))
					{
						return null;
					}
					int done = state.Count((int s) => s == 1);
					int removed = state.Count((int s) => s == 2);
					return "done: " + done + " of " + list.Count + (removed > 0 ? ", " + removed + " removed" : "");
				};
				it.status = delegate { return state.Count((int s) => s == 0) + " of " + list.Count + " left"; };
				AddItem(it);
			}
			catch (Exception ex)
			{
				LogOnce("Track(designations)", ex);
			}
		}

		private static bool HasDesignation(Map map, LocalTargetInfo target, DesignationDef def)
		{
			if (def.targetType == TargetType.Cell)
			{
				return map.designationManager.DesignationAt(target.Cell, def) != null;
			}
			return target.HasThing && map.designationManager.DesignationOn(target.Thing, def) != null;
		}

		private static bool IsDone(Map map, LocalTargetInfo target, DesignationDef def)
		{
			if (def.targetType == TargetType.Cell)
			{
				if (def == DesignationDefOf.Mine)
				{
					return target.Cell.GetFirstMineable(map) == null;
				}
				return true;
			}
			Thing thing = target.Thing;
			if (thing == null || thing.Destroyed || thing.Discarded)
			{
				return true;
			}
			Pawn pawn = thing as Pawn;
			if (pawn != null && pawn.Dead)
			{
				return true;
			}
			Plant plant = thing as Plant;
			if (plant != null && def == DesignationDefOf.HarvestPlant && !plant.HarvestableNow)
			{
				return true;
			}
			return false;
		}

		// ---- job -------------------------------------------------------------------------------------------

		// Pending while the pawn's current job is that job or it is in the pawn's job queue
		// (Pawn_JobTracker.curJob / jobQueue, Verse.AI\Pawn_JobTracker.cs:14, 18; JobQueue.Contains, Verse.AI\JobQueue.cs:61).
		public static void Track(Pawn pawn, Job job, string label)
		{
			try
			{
				if (pawn == null || job == null)
				{
					return;
				}
				Item it = MakeItem("job", string.IsNullOrEmpty(label) ? pawn.LabelShort + ": " + job.def.defName : label, null);
				it.check = delegate
				{
					string name = pawn.LabelShort;
					bool pending = !pawn.Dead && !pawn.Discarded && pawn.jobs != null
						&& (pawn.jobs.curJob == job || (pawn.jobs.jobQueue != null && pawn.jobs.jobQueue.Contains(job)));
					if (pending)
					{
						return null;
					}
					if (pawn.Dead)
					{
						return "ended (" + name + " is dead)";
					}
					if (pawn.Discarded)
					{
						return "ended (" + name + " is gone)";
					}
					if (pawn.Downed)
					{
						return "ended (" + name + " is downed)";
					}
					return "ended";
				};
				AddItem(it);
			}
			catch (Exception ex)
			{
				LogOnce("Track(job)", ex);
			}
		}

		// ---- OrderController hooks -------------------------------------------------------------------------

		public static void SetApplyContext(string orderId)
		{
			currentOrder = orderId;
		}

		// After all writes of an apply: follow what the existing forms started.
		public static void AfterApply(List<Plan> plans)
		{
			try
			{
				List<Blueprint_Build> fresh = BuildTools.RecentBlueprints
					.Where((Blueprint_Build b) => b != null && !b.Destroyed && b.Spawned && !followedThings.Contains(b.thingIDNumber))
					.ToList();
				if (fresh.Count == 1)
				{
					Track(fresh[0]);
				}
				else if (fresh.Count > 1)
				{
					Track(fresh.Cast<Thing>(), GroupLabel(fresh));
				}
				if (plans != null && Find.ResearchManager != null)
				{
					foreach (Plan plan in plans)
					{
						if (plan == null || plan.toolName != "research start")
						{
							continue;
						}
						ResearchProjectDef current = Find.ResearchManager.GetProject();
						if (current != null && !current.IsFinished && !items.Any((Item i) => i.subject == current))
						{
							Track(current);
						}
					}
				}
			}
			catch (Exception ex)
			{
				LogOnce("AfterApply", ex);
			}
		}

		// "build: 12 blueprints (wall x10, door x2)"
		private static string GroupLabel(List<Blueprint_Build> blueprints)
		{
			List<string> names = new List<string>();
			Dictionary<string, int> counts = new Dictionary<string, int>();
			foreach (Blueprint_Build b in blueprints)
			{
				string name = b.EntityToBuild().label;
				int n;
				if (!counts.TryGetValue(name, out n))
				{
					names.Add(name);
				}
				counts[name] = n + 1;
			}
			List<string> parts = names.OrderByDescending((string s) => counts[s]).Take(5).Select((string s) => s + " x" + counts[s]).ToList();
			if (names.Count > 5)
			{
				parts.Add("other kinds x" + names.Skip(5).Sum((string s) => counts[s]));
			}
			return "build: " + blueprints.Count + " blueprints (" + string.Join(", ", parts.ToArray()) + ")";
		}

		// ---- show pending ----------------------------------------------------------------------------------

		// "- build: 12 blueprints (wall x10, door x2), since day 5, 14h: 4 built, 6 frames, 2 blueprints"
		public static List<string> PendingLines()
		{
			List<string> lines = new List<string>();
			foreach (Item it in items)
			{
				string line = "- " + it.label + ", since " + AgentTime.When(it.startTick);
				string status = null;
				try
				{
					status = it.status != null ? it.status() : null;
				}
				catch (Exception)
				{
				}
				if (!string.IsNullOrEmpty(status))
				{
					line += ": " + status;
				}
				lines.Add(line);
			}
			return lines;
		}

		// Form "query pending" (read-only).
		public static QueryReply QueryPending(FormContext c)
		{
			List<string> lines = PendingLines();
			if (lines.Count == 0)
			{
				return QueryReply.Ok("Nothing is being followed. (The list starts empty when a game is loaded or started.)");
			}
			List<string> output = new List<string>();
			output.Add("Followed (" + lines.Count + "):");
			if (lines.Count + 1 > MaxAnswerLines)
			{
				int shown = MaxAnswerLines - 2;
				output.AddRange(lines.Take(shown));
				output.Add("... and " + (lines.Count - shown) + " more not shown (the list holds at most " + MaxItems + ").");
			}
			else
			{
				output.AddRange(lines);
			}
			return QueryReply.Ok(string.Join("\n", output.ToArray()));
		}
	}
}
