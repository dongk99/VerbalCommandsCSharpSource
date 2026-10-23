using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;

namespace VerbalCommands
{
	// The human play record (user, 2026-10-09: "I want to record my gameplay, but in text (like, tips style), based
	// on actions i do, so it reads like a diary where agents can use it as reference" / "make the .md files
	// unique-ish like REFERENCE_HUMAN_PLAY-[savefile-rws]. you may build and have that option shown under mod menu
	// (ticking option on generates logfile onto savefile folder). mod option: "Record human gameplay (log-based)
	// for training"").
	//
	// On while the mod setting recordHumanPlay is ticked. Every CheckEvery ticks the game state is compared with
	// the state at the last check and every difference the player can have made is written as one line, same-kind
	// actions grouped into one line per check (user: "you could bulk-group same action kinds/hauling kinds"). The
	// "why" is not here: a Claude session asks the user for it at the end of the day and writes the diary from this
	// file (agent_player\USER_REQUESTS.md, item 4).
	//
	// File: <Saves folder>\REFERENCE_HUMAN_PLAY-<save name>.md (GenFilePaths.SavedGamesFolderPath, Verse\
	// GenFilePaths.cs:181). The save name is, in this order: the permadeath name (GameInfo.permadeathModeUniqueName,
	// Verse\GameInfo.cs:12); the name the game was loaded from (Game.InitData.gameToLoad, Verse\GameInitData.cs:44,
	// which Game.LoadGame leaves in place: only InitNewGame clears InitData, Verse\Game.cs:553); the newest .rws in
	// the Saves folder right after a save (GameDataSaveLoader.CurrentGameStateIsValuable is false for 60 ticks after
	// SaveGame, Verse\GameDataSaveLoader.cs:50,137); else the colony's faction name. When the name changes (save as)
	// and the new file does not exist yet, the old file is copied to it: a "save as" forks the record.
	//
	// What is compared (all maps of the game; the "when" of a line is the game clock, AgentTime.When):
	//   designations   map.designationManager.AllDesignations (Verse\DesignationManager.cs:30): new and cancelled
	//                  ones, grouped by def, with the cell range and, for things, what they are.
	//   blueprints     ThingRequestGroup.Blueprint things of the player: new ones grouped by label (BuildDefs.Label,
	//                  Placement.cs:222); ones the mod placed itself are counted apart (GameComponent_VerbalCommands.
	//                  IsModPlaced).
	//   zones          map.zoneManager.AllZones (Verse\ZoneManager.cs:13): new, deleted, resized (Zone.cells, Zone.cs:
	//                  20), stockpile priority and allowed-def count (Zone_Stockpile.settings, RimWorld\Zone_Stockpile.
	//                  cs:9; StorageSettings.Priority, RimWorld\StorageSettings.cs:23; ThingFilter.AllowedDefCount,
	//                  Verse\ThingFilter.cs:180), growing zone plant (Zone_Growing.GetPlantDefToGrow, :184).
	//   bills          every colony building that is an IBillGiver (RimWorld\IBillGiver.cs:10): added, removed and
	//                  changed bills (Bill.LabelCap, RimWorld\Bill.cs:87; Bill_Production.repeatMode/repeatCount/
	//                  targetCount, RimWorld\Bill_Production.cs:12-22; Bill.suspended, Bill.cs:29).
	//   areas          map.areaManager.AllAreas (Verse\AreaManager.cs:15): cell count per area (Area.TrueCount, Verse\
	//                  Area.cs:28), which covers home, roof and allowed areas.
	//   colonists      work priorities (Pawn_WorkSettings.GetPriority, RimWorld\Pawn_WorkSettings.cs:160), drafting
	//                  (Pawn_DraftController.Drafted, RimWorld\Pawn_DraftController.cs:19), jobs the player ordered
	//                  (Job.playerForced, Verse.AI\Job.cs:37; JobDriver.GetReport, Verse.AI\JobDriver.cs:218), allowed
	//                  area, medicine, hostility response, self-tend (Pawn_PlayerSettings, RimWorld\Pawn_PlayerSettings.
	//                  cs:27-31,85), apparel and food policy, the schedule (Pawn_TimetableTracker.GetAssignment, :51).
	//   prisoners      the interaction mode (Pawn_GuestTracker.ExclusiveInteractionMode, RimWorld\Pawn_GuestTracker.
	//                  cs:83).
	//   forbidding     things of ThingRequestGroup.HaulableEver that went from allowed to forbidden or back
	//                  (ForbidUtility.IsForbidden(Thing, Faction), RimWorld\ForbidUtility.cs:191), counted per def.
	//   research       the current project (ResearchManager.GetProject, RimWorld\ResearchManager.cs:155).
	// Not seen: letter choices, trade, caravans, time speed. Orders given through the mod are in the router log;
	// their effects (blueprints, designations, bills, priorities) show up here like the player's own.
	//
	// Main thread only (AgentTime.Tick). Never throws out: every check runs in its own try, faults are logged once.
	public static class HumanPlayLog
	{
		private const int CheckEvery = 180;
		private const string FilePrefix = "REFERENCE_HUMAN_PLAY-";
		private const int MaxBreakdown = 6;

		private static Game lastGame;
		private static bool haveBaseline;
		private static int nextCheck;
		private static string saveName;
		private static string filePath;
		private static DateTime sessionStart;
		private static readonly List<string> pending = new List<string>();
		private static readonly HashSet<string> loggedFaults = new HashSet<string>();

		// Snapshots. Keys carry the map id so several maps do not collide.
		private static readonly Dictionary<string, Designation> designations = new Dictionary<string, Designation>();
		private static readonly HashSet<string> blueprints = new HashSet<string>();
		private static readonly Dictionary<Zone, ZoneState> zones = new Dictionary<Zone, ZoneState>();
		private static readonly Dictionary<string, string> bills = new Dictionary<string, string>();   // bill id -> "giver|text"
		private static readonly Dictionary<string, int> areas = new Dictionary<string, int>();
		private static readonly Dictionary<int, PawnState> pawns = new Dictionary<int, PawnState>();
		private static readonly Dictionary<int, bool> forbidden = new Dictionary<int, bool>();
		private static string research;

		private sealed class ZoneState
		{
			public string label;
			public int cells;
			public string box;
			public string settings;
		}

		private sealed class PawnState
		{
			public string name;
			public Dictionary<string, int> work = new Dictionary<string, int>();
			public bool drafted;
			public int forcedJob = -1;
			public string area, medCare, hostility, outfit, food, schedule, prisonerMode;
			public bool selfTend;
		}

		public static bool Enabled
		{
			get
			{
				try
				{
					return VerbalCommandsMod.Settings != null && VerbalCommandsMod.Settings.recordHumanPlay;
				}
				catch (Exception)
				{
					return false;
				}
			}
		}

		// ---- tick ------------------------------------------------------------------------------------------------

		// AgentTime.Tick, once per game tick. Never throws.
		public static void Tick()
		{
			try
			{
				Game game = Current.Game;
				if (game == null || game.tickManager == null)
				{
					return;
				}
				if (!Enabled)
				{
					if (lastGame != null)
					{
						Flush();
						lastGame = null;
					}
					return;
				}
				int tick = game.tickManager.TicksGame;
				if (!ReferenceEquals(game, lastGame))
				{
					lastGame = game;
					Reset();
					saveName = SaveName(game);
					filePath = PathFor(saveName);
					sessionStart = DateTime.Now;
					WriteHeader(game, tick);
					nextCheck = tick;
				}
				if (tick < nextCheck && nextCheck - tick <= CheckEvery)
				{
					return;
				}
				nextCheck = tick + CheckEvery;
				RunCheck("save name", delegate { CheckSaveName(game); });
				bool baseline = !haveBaseline;
				foreach (Map map in game.Maps)
				{
					Map m = map;
					if (m == null)
					{
						continue;
					}
					string where = game.Maps.Count > 1 ? MapName(m) + ": " : "";
					RunCheck("designations", delegate { CheckDesignations(m, where, baseline, tick); });
					RunCheck("blueprints", delegate { CheckBlueprints(m, where, baseline, tick); });
					RunCheck("zones", delegate { CheckZones(m, where, baseline, tick); });
					RunCheck("bills", delegate { CheckBills(m, where, baseline, tick); });
					RunCheck("areas", delegate { CheckAreas(m, where, baseline, tick); });
					RunCheck("pawns", delegate { CheckPawns(m, where, baseline, tick); });
					RunCheck("forbidden", delegate { CheckForbidden(m, where, baseline, tick); });
				}
				RunCheck("research", delegate { CheckResearch(baseline, tick); });
				RunCheck("forget", delegate { ForgetGone(game); });
				haveBaseline = true;
				Flush();
			}
			catch (Exception ex)
			{
				LogOnce("Tick", ex);
			}
		}

		private static void Reset()
		{
			haveBaseline = false;
			pending.Clear();
			designations.Clear();
			blueprints.Clear();
			zones.Clear();
			bills.Clear();
			areas.Clear();
			pawns.Clear();
			forbidden.Clear();
			research = null;
		}

		// ---- file ------------------------------------------------------------------------------------------------

		private static string SaveName(Game game)
		{
			try
			{
				if (game.Info != null && game.Info.permadeathMode && !game.Info.permadeathModeUniqueName.NullOrEmpty())
				{
					return game.Info.permadeathModeUniqueName;
				}
				if (game.InitData != null && !game.InitData.gameToLoad.NullOrEmpty())
				{
					return game.InitData.gameToLoad;
				}
				Faction f = Faction.OfPlayerSilentFail;
				if (f != null && !f.Name.NullOrEmpty())
				{
					return f.Name;
				}
			}
			catch (Exception ex)
			{
				LogOnce("SaveName", ex);
			}
			return "unnamed";
		}

		private static string PathFor(string name)
		{
			StringBuilder sb = new StringBuilder();
			foreach (char c in name ?? "")
			{
				sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
			}
			string clean = sb.Length == 0 ? "unnamed" : sb.ToString();
			return Path.Combine(GenFilePaths.SavedGamesFolderPath, FilePrefix + clean + ".md");
		}

		// Right after a save the newest .rws in the Saves folder is the save just made: its name becomes the file's.
		private static void CheckSaveName(Game game)
		{
			if (GameDataSaveLoader.CurrentGameStateIsValuable)
			{
				return;
			}
			DirectoryInfo dir = new DirectoryInfo(GenFilePaths.SavedGamesFolderPath);
			if (!dir.Exists)
			{
				return;
			}
			FileInfo newest = dir.GetFiles("*.rws").OrderByDescending((FileInfo f) => f.LastWriteTimeUtc).FirstOrDefault();
			if (newest == null || (DateTime.UtcNow - newest.LastWriteTimeUtc).TotalMinutes > 2)
			{
				return;
			}
			string name = Path.GetFileNameWithoutExtension(newest.Name);
			if (name == saveName)
			{
				return;
			}
			string newPath = PathFor(name);
			Flush();
			if (!File.Exists(newPath) && File.Exists(filePath))
			{
				File.Copy(filePath, newPath);
			}
			string old = saveName;
			saveName = name;
			filePath = newPath;
			Line(game.tickManager.TicksGame, "saved as \"" + name + "\"" + (old == name ? "" : " (record continued from " + old + ")"));
		}

		private static void WriteHeader(Game game, int tick)
		{
			StringBuilder sb = new StringBuilder();
			bool fresh = !File.Exists(filePath);
			if (fresh)
			{
				sb.AppendLine("# Human play record: " + saveName);
				sb.AppendLine();
				sb.AppendLine("Written by the VerbalCommands mod while \"Record human gameplay (log-based) for training\" is on.");
				sb.AppendLine("One line per change the player made, same kinds grouped per check (" + CheckEvery + " ticks). The why");
				sb.AppendLine("is not here: it is asked of the player afterwards and written into the diary.");
			}
			sb.AppendLine();
			sb.AppendLine("## Session " + sessionStart.ToString("yyyy-MM-dd HH:mm") + ", " + AgentTime.When(tick));
			try
			{
				Map map = game.CurrentMap;
				if (map != null)
				{
					List<string> names = map.mapPawns.FreeColonistsSpawned.Select((Pawn p) => p.LabelShort).ToList();
					sb.AppendLine("Colonists: " + (names.Count == 0 ? "none" : string.Join(", ", names.ToArray()))
						+ "; map " + MapName(map) + ", " + map.Size.x + "x" + map.Size.z
						+ ", " + Mathf(map.mapTemperature.OutdoorTemp) + "C outdoors.");
				}
			}
			catch (Exception ex)
			{
				LogOnce("header", ex);
			}
			Append(sb.ToString());
		}

		private static string Mathf(float v)
		{
			return ((int)Math.Round(v)).ToString();
		}

		private static void Line(int tick, string text)
		{
			pending.Add("- [" + AgentTime.When(tick) + "] " + text);
		}

		private static void Flush()
		{
			if (pending.Count == 0)
			{
				return;
			}
			string block = string.Join("\n", pending.ToArray()) + "\n";
			pending.Clear();
			Append(block);
		}

		private static void Append(string text)
		{
			try
			{
				if (filePath.NullOrEmpty())
				{
					return;
				}
				Directory.CreateDirectory(Path.GetDirectoryName(filePath));
				File.AppendAllText(filePath, text, Encoding.UTF8);
			}
			catch (Exception ex)
			{
				LogOnce("Append", ex);
			}
		}

		// ---- designations ----------------------------------------------------------------------------------------

		private static void CheckDesignations(Map map, string where, bool baseline, int tick)
		{
			string mapKey = map.uniqueID + "|";
			HashSet<string> seen = new HashSet<string>();
			Dictionary<DesignationDef, List<Designation>> added = new Dictionary<DesignationDef, List<Designation>>();
			foreach (Designation d in map.designationManager.AllDesignations)
			{
				if (d == null || d.def == null)
				{
					continue;
				}
				string key = mapKey + d.def.defName + "|" + (d.target.HasThing ? "t" + d.target.Thing.thingIDNumber : d.target.Cell.ToString());
				seen.Add(key);
				if (designations.ContainsKey(key))
				{
					continue;
				}
				designations[key] = d;
				if (baseline)
				{
					continue;
				}
				List<Designation> list;
				if (!added.TryGetValue(d.def, out list))
				{
					added[d.def] = list = new List<Designation>();
				}
				list.Add(d);
			}
			Dictionary<DesignationDef, List<Designation>> removed = new Dictionary<DesignationDef, List<Designation>>();
			foreach (string key in designations.Keys.Where((string k) => k.StartsWith(mapKey) && !seen.Contains(k)).ToList())
			{
				Designation d = designations[key];
				designations.Remove(key);
				if (baseline || d == null)
				{
					continue;
				}
				// A designation that went because its work was done is not a cancel: the target is gone or changed.
				if (d.target.HasThing && (d.target.Thing == null || d.target.Thing.Destroyed || !d.target.Thing.Spawned))
				{
					continue;
				}
				if (!d.target.HasThing && d.def == DesignationDefOf.Mine && !d.target.Cell.InBounds(map))
				{
					continue;
				}
				if (!d.target.HasThing && d.def == DesignationDefOf.Mine && d.target.Cell.GetFirstMineable(map) == null)
				{
					continue;   // mined out
				}
				List<Designation> list;
				if (!removed.TryGetValue(d.def, out list))
				{
					removed[d.def] = list = new List<Designation>();
				}
				list.Add(d);
			}
			foreach (KeyValuePair<DesignationDef, List<Designation>> kv in added)
			{
				Line(tick, where + "designated " + DefLabel(kv.Key) + ": " + DescribeTargets(kv.Value));
			}
			foreach (KeyValuePair<DesignationDef, List<Designation>> kv in removed)
			{
				Line(tick, where + "cancelled " + DefLabel(kv.Key) + ": " + DescribeTargets(kv.Value));
			}
		}

		private static string DescribeTargets(List<Designation> list)
		{
			List<IntVec3> cells = list.Select((Designation d) => d.target.HasThing ? d.target.Thing.Position : d.target.Cell).ToList();
			string text = list.Count + (list.Count == 1 ? " target" : " targets") + " at " + Box(cells);
			Dictionary<string, int> kinds = new Dictionary<string, int>();
			foreach (Designation d in list)
			{
				if (!d.target.HasThing || d.target.Thing == null)
				{
					continue;
				}
				string l = d.target.Thing.def.label.NullOrEmpty() ? d.target.Thing.def.defName : d.target.Thing.def.label;
				int n;
				kinds.TryGetValue(l, out n);
				kinds[l] = n + 1;
			}
			if (kinds.Count > 0)
			{
				text += " (" + Breakdown(kinds) + ")";
			}
			return text;
		}

		// ---- blueprints ------------------------------------------------------------------------------------------

		private static void CheckBlueprints(Map map, string where, bool baseline, int tick)
		{
			string mapKey = map.uniqueID + "|";
			HashSet<string> seen = new HashSet<string>();
			Dictionary<string, List<IntVec3>> added = new Dictionary<string, List<IntVec3>>();
			Dictionary<string, int> viaMod = new Dictionary<string, int>();
			GameComponent_VerbalCommands comp = GameComponent_VerbalCommands.Instance;
			foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint))
			{
				if (t == null || t.Destroyed || t.Faction != Faction.OfPlayer || t.def.entityDefToBuild == null)
				{
					continue;
				}
				string key = mapKey + t.thingIDNumber;
				seen.Add(key);
				if (!blueprints.Add(key) || baseline)
				{
					continue;
				}
				ThingDef thingDef = t.def.entityDefToBuild as ThingDef;
				IConstructible constructible = t as IConstructible;
				string label = thingDef != null
					? BuildDefs.Label(thingDef, constructible != null ? constructible.EntityToBuildStuff() : null)
					: (t.def.entityDefToBuild.label.NullOrEmpty() ? t.def.entityDefToBuild.defName : t.def.entityDefToBuild.label);
				List<IntVec3> cells;
				if (!added.TryGetValue(label, out cells))
				{
					added[label] = cells = new List<IntVec3>();
				}
				cells.Add(t.Position);
				if (thingDef != null && comp != null && comp.IsModPlaced(map, t.Position, thingDef))
				{
					int n;
					viaMod.TryGetValue(label, out n);
					viaMod[label] = n + 1;
				}
			}
			blueprints.RemoveWhere((string k) => k.StartsWith(mapKey) && !seen.Contains(k));
			foreach (KeyValuePair<string, List<IntVec3>> kv in added)
			{
				int mod;
				viaMod.TryGetValue(kv.Key, out mod);
				Line(tick, where + "placed " + kv.Key + " x " + kv.Value.Count + " at " + Box(kv.Value)
					+ (mod > 0 ? " (" + mod + " through the mod)" : ""));
			}
		}

		// ---- zones -----------------------------------------------------------------------------------------------

		private static void CheckZones(Map map, string where, bool baseline, int tick)
		{
			HashSet<Zone> seen = new HashSet<Zone>();
			foreach (Zone z in map.zoneManager.AllZones)
			{
				if (z == null)
				{
					continue;
				}
				seen.Add(z);
				ZoneState now = new ZoneState();
				now.label = z.label.NullOrEmpty() ? z.GetType().Name : z.label;
				now.cells = z.cells.Count;
				now.box = Box(z.cells);
				now.settings = ZoneSettings(z);
				ZoneState old;
				if (!zones.TryGetValue(z, out old))
				{
					zones[z] = now;
					if (!baseline)
					{
						Line(tick, where + "zoned " + now.label + ": " + now.cells + " cells at " + now.box
							+ (now.settings.Length > 0 ? ", " + now.settings : ""));
					}
					continue;
				}
				if (baseline)
				{
					zones[z] = now;
					continue;
				}
				if (old.label != now.label)
				{
					Line(tick, where + "renamed zone " + old.label + " to " + now.label);
				}
				if (old.cells != now.cells)
				{
					Line(tick, where + (now.cells > old.cells ? "expanded " : "shrank ") + now.label + ": " + old.cells + " to " + now.cells + " cells, now " + now.box);
				}
				if (old.settings != now.settings)
				{
					Line(tick, where + "changed " + now.label + ": " + now.settings);
				}
				zones[z] = now;
			}
			foreach (Zone z in zones.Keys.Where((Zone k) => k.Map == map && !seen.Contains(k)).ToList())
			{
				ZoneState old = zones[z];
				zones.Remove(z);
				if (!baseline)
				{
					Line(tick, where + "deleted zone " + old.label + " (" + old.cells + " cells at " + old.box + ")");
				}
			}
		}

		private static string ZoneSettings(Zone z)
		{
			Zone_Stockpile s = z as Zone_Stockpile;
			if (s != null && s.settings != null)
			{
				return "priority " + s.settings.Priority.ToString().ToLowerInvariant()
					+ ", " + (s.settings.filter != null ? s.settings.filter.AllowedDefCount : 0) + " kinds allowed";
			}
			Zone_Growing g = z as Zone_Growing;
			if (g != null)
			{
				ThingDef plant = g.GetPlantDefToGrow();
				return "grows " + (plant == null ? "nothing" : plant.label) + (g.allowSow ? "" : ", sowing off");
			}
			return "";
		}

		// ---- bills -----------------------------------------------------------------------------------------------

		private static void CheckBills(Map map, string where, bool baseline, int tick)
		{
			string mapKey = map.uniqueID + "|";
			HashSet<string> seen = new HashSet<string>();
			foreach (Building b in map.listerBuildings.allBuildingsColonist)
			{
				IBillGiver giver = b as IBillGiver;
				if (giver == null || giver.BillStack == null)
				{
					continue;
				}
				string at = b.def.label + " " + b.Position.x + "," + b.Position.z;
				for (int i = 0; i < giver.BillStack.Count; i++)
				{
					Bill bill = giver.BillStack[i];
					if (bill == null)
					{
						continue;
					}
					string key = mapKey + bill.GetUniqueLoadID();
					seen.Add(key);
					string text = BillText(bill);
					string old;
					if (!bills.TryGetValue(key, out old))
					{
						bills[key] = at + "|" + text;
						if (!baseline)
						{
							Line(tick, where + "added bill at " + at + ": " + text);
						}
						continue;
					}
					string oldText = old.Substring(old.IndexOf('|') + 1);
					if (oldText != text)
					{
						bills[key] = at + "|" + text;
						if (!baseline)
						{
							Line(tick, where + "changed bill at " + at + ": " + oldText + " -> " + text);
						}
					}
				}
			}
			foreach (string key in bills.Keys.Where((string k) => k.StartsWith(mapKey) && !seen.Contains(k)).ToList())
			{
				string old = bills[key];
				bills.Remove(key);
				if (!baseline)
				{
					int bar = old.IndexOf('|');
					Line(tick, where + "removed bill at " + old.Substring(0, bar) + ": " + old.Substring(bar + 1));
				}
			}
		}

		private static string BillText(Bill bill)
		{
			string text = bill.LabelCap;
			Bill_Production p = bill as Bill_Production;
			if (p != null && p.repeatMode != null)
			{
				if (p.repeatMode == BillRepeatModeDefOf.RepeatCount)
				{
					text += " x " + p.repeatCount;
				}
				else if (p.repeatMode == BillRepeatModeDefOf.TargetCount)
				{
					text += " until " + p.targetCount;
				}
				else if (p.repeatMode == BillRepeatModeDefOf.Forever)
				{
					text += " forever";
				}
			}
			if (bill.suspended)
			{
				text += " (suspended)";
			}
			return text;
		}

		// ---- areas -----------------------------------------------------------------------------------------------

		private static void CheckAreas(Map map, string where, bool baseline, int tick)
		{
			string mapKey = map.uniqueID + "|";
			HashSet<string> seen = new HashSet<string>();
			foreach (Area a in map.areaManager.AllAreas)
			{
				if (a == null)
				{
					continue;
				}
				string label = a.Label.NullOrEmpty() ? a.GetType().Name : a.Label;
				string key = mapKey + label;
				seen.Add(key);
				int count = a.TrueCount;
				int old;
				if (!areas.TryGetValue(key, out old))
				{
					areas[key] = count;
					if (!baseline && !(a is Area_Home) && !(a is Area_BuildRoof) && !(a is Area_NoRoof))
					{
						Line(tick, where + "made area " + label + ": " + count + " cells");
					}
					continue;
				}
				if (old != count)
				{
					areas[key] = count;
					if (!baseline)
					{
						Line(tick, where + "area " + label + ": " + old + " to " + count + " cells");
					}
				}
			}
			foreach (string key in areas.Keys.Where((string k) => k.StartsWith(mapKey) && !seen.Contains(k)).ToList())
			{
				areas.Remove(key);
				if (!baseline)
				{
					Line(tick, where + "deleted area " + key.Substring(mapKey.Length));
				}
			}
		}

		// ---- pawns -----------------------------------------------------------------------------------------------

		private static void CheckPawns(Map map, string where, bool baseline, int tick)
		{
			List<Pawn> list = new List<Pawn>();
			foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
			{
				if (p == null || p.Dead)
				{
					continue;
				}
				if (p.IsColonist || p.IsPrisonerOfColony || (p.Faction == Faction.OfPlayer && p.RaceProps != null && p.RaceProps.Animal))
				{
					list.Add(p);
				}
			}
			Dictionary<string, List<string>> grouped = new Dictionary<string, List<string>>();
			foreach (Pawn p in list)
			{
				PawnState now = Read(p);
				PawnState old;
				if (!pawns.TryGetValue(p.thingIDNumber, out old) || baseline)
				{
					pawns[p.thingIDNumber] = now;
					continue;
				}
				pawns[p.thingIDNumber] = now;
				foreach (KeyValuePair<string, int> kv in now.work)
				{
					int before;
					if (old.work.TryGetValue(kv.Key, out before) && before != kv.Value)
					{
						Group(grouped, "set work priorities", now.name + " " + kv.Key + " " + before + " to " + kv.Value);
					}
				}
				if (old.drafted != now.drafted)
				{
					Group(grouped, now.drafted ? "drafted" : "undrafted", now.name);
				}
				if (now.forcedJob >= 0 && now.forcedJob != old.forcedJob)
				{
					string report = "";
					try
					{
						report = p.jobs != null && p.jobs.curDriver != null ? p.jobs.curDriver.GetReport() : (p.CurJob != null ? p.CurJob.def.label : "");
					}
					catch (Exception)
					{
					}
					Group(grouped, "ordered", now.name + ": " + report.TrimEnd('.'));
				}
				Changed(grouped, "allowed area", now.name, old.area, now.area);
				Changed(grouped, "medicine", now.name, old.medCare, now.medCare);
				Changed(grouped, "hostility response", now.name, old.hostility, now.hostility);
				Changed(grouped, "self-tend", now.name, old.selfTend ? "on" : "off", now.selfTend ? "on" : "off");
				Changed(grouped, "apparel policy", now.name, old.outfit, now.outfit);
				Changed(grouped, "food policy", now.name, old.food, now.food);
				Changed(grouped, "schedule", now.name, old.schedule, now.schedule);
				Changed(grouped, "prisoner mode", now.name, old.prisonerMode, now.prisonerMode);
			}
			foreach (KeyValuePair<string, List<string>> kv in grouped)
			{
				Line(tick, where + kv.Key + ": " + string.Join("; ", kv.Value.ToArray()));
			}
		}

		private static PawnState Read(Pawn p)
		{
			PawnState s = new PawnState();
			s.name = p.LabelShort;
			try
			{
				if (p.workSettings != null && p.workSettings.EverWork)
				{
					foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefsListForReading)
					{
						s.work[w.labelShort.NullOrEmpty() ? w.defName : w.labelShort] = p.workSettings.GetPriority(w);
					}
				}
			}
			catch (Exception ex)
			{
				LogOnce("work", ex);
			}
			s.drafted = p.drafter != null && p.drafter.Drafted;
			Job job = p.CurJob;
			s.forcedJob = job != null && job.playerForced ? job.loadID : -1;
			if (p.playerSettings != null)
			{
				Area area = p.playerSettings.AreaRestrictionInPawnCurrentMap;
				s.area = area == null ? "unrestricted" : area.Label;
				s.medCare = p.playerSettings.medCare.ToString();
				s.hostility = p.playerSettings.hostilityResponse.ToString();
				s.selfTend = p.playerSettings.selfTend;
			}
			s.outfit = p.outfits != null && p.outfits.CurrentApparelPolicy != null ? p.outfits.CurrentApparelPolicy.label : null;
			s.food = p.foodRestriction != null && p.foodRestriction.CurrentFoodPolicy != null ? p.foodRestriction.CurrentFoodPolicy.label : null;
			s.schedule = p.timetable != null ? Schedule(p) : null;
			s.prisonerMode = p.IsPrisonerOfColony && p.guest != null && p.guest.ExclusiveInteractionMode != null
				? p.guest.ExclusiveInteractionMode.label : null;
			return s;
		}

		// "0-6 sleep, 6-22 anything, 22-24 sleep"
		private static string Schedule(Pawn p)
		{
			List<string> runs = new List<string>();
			string cur = null;
			int start = 0;
			for (int h = 0; h <= 24; h++)
			{
				string a = null;
				if (h < 24)
				{
					TimeAssignmentDef d = p.timetable.GetAssignment(h);
					a = d == null ? "?" : d.label;
				}
				if (a != cur)
				{
					if (cur != null)
					{
						runs.Add(start + "-" + h + " " + cur);
					}
					cur = a;
					start = h;
				}
			}
			return string.Join(", ", runs.ToArray());
		}

		private static void Changed(Dictionary<string, List<string>> grouped, string kind, string name, string before, string now)
		{
			if (before == now || now == null)
			{
				return;
			}
			Group(grouped, kind, name + ": " + (before ?? "-") + " to " + now);
		}

		private static void Group(Dictionary<string, List<string>> grouped, string kind, string item)
		{
			List<string> list;
			if (!grouped.TryGetValue(kind, out list))
			{
				grouped[kind] = list = new List<string>();
			}
			list.Add(item);
		}

		// ---- forbidding ------------------------------------------------------------------------------------------

		private static void CheckForbidden(Map map, string where, bool baseline, int tick)
		{
			Dictionary<string, int> nowForbidden = new Dictionary<string, int>();
			Dictionary<string, int> nowAllowed = new Dictionary<string, int>();
			HashSet<int> seen = new HashSet<int>();
			foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
			{
				if (t == null || t.Destroyed || t.def == null)
				{
					continue;
				}
				bool f = t.IsForbidden(Faction.OfPlayer);
				seen.Add(t.thingIDNumber);
				bool old;
				if (forbidden.TryGetValue(t.thingIDNumber, out old) && old != f && !baseline)
				{
					string l = t.def.label.NullOrEmpty() ? t.def.defName : t.def.label;
					Dictionary<string, int> into = f ? nowForbidden : nowAllowed;
					int n;
					into.TryGetValue(l, out n);
					into[l] = n + t.stackCount;
				}
				forbidden[t.thingIDNumber] = f;
			}
			// Things of other maps keep their entries; only this map's gone things are dropped.
			foreach (int id in forbidden.Keys.Where((int k) => !seen.Contains(k)).ToList())
			{
				if (!map.listerThings.AllThings.Any((Thing t) => t.thingIDNumber == id))
				{
					forbidden.Remove(id);
				}
			}
			if (nowForbidden.Count > 0)
			{
				Line(tick, where + "forbade " + nowForbidden.Values.Sum() + " items (" + Breakdown(nowForbidden) + ")");
			}
			if (nowAllowed.Count > 0)
			{
				Line(tick, where + "allowed " + nowAllowed.Values.Sum() + " items (" + Breakdown(nowAllowed) + ")");
			}
		}

		// ---- research --------------------------------------------------------------------------------------------

		private static void CheckResearch(bool baseline, int tick)
		{
			ResearchProjectDef proj = Find.ResearchManager != null ? Find.ResearchManager.GetProject() : null;
			string now = proj == null ? null : proj.label;
			if (!baseline && now != research && now != null)
			{
				Line(tick, "research: " + (research ?? "nothing") + " to " + now);
			}
			research = now;
		}

		// ---- housekeeping ----------------------------------------------------------------------------------------

		private static void ForgetGone(Game game)
		{
			HashSet<int> mapIds = new HashSet<int>(game.Maps.Select((Map m) => m.uniqueID));
			foreach (int id in pawns.Keys.ToList())
			{
				bool alive = game.Maps.Any((Map m) => m.mapPawns.AllPawnsSpawned.Any((Pawn p) => p.thingIDNumber == id));
				if (!alive)
				{
					pawns.Remove(id);
				}
			}
			foreach (Zone z in zones.Keys.Where((Zone k) => k.Map == null || !mapIds.Contains(k.Map.uniqueID)).ToList())
			{
				zones.Remove(z);
			}
		}

		private static void RunCheck(string name, Action check)
		{
			try
			{
				check();
			}
			catch (Exception ex)
			{
				LogOnce(name, ex);
			}
		}

		private static void LogOnce(string where, Exception ex)
		{
			try
			{
				if (loggedFaults.Add(where))
				{
					Log.Error("[VerbalCommands] HumanPlayLog " + where + ": " + ex);
				}
			}
			catch (Exception)
			{
			}
		}

		// ---- text helpers ----------------------------------------------------------------------------------------

		private static string MapName(Map map)
		{
			try
			{
				return map.Parent != null ? map.Parent.LabelCap.ToString() : "map " + map.uniqueID;
			}
			catch (Exception)
			{
				return "map " + map.uniqueID;
			}
		}

		private static string DefLabel(Def def)
		{
			return def.label.NullOrEmpty() ? def.defName : def.label;
		}

		// "128,130" for one cell, "x 126-130, z 128-131" for more.
		private static string Box(List<IntVec3> cells)
		{
			if (cells == null || cells.Count == 0)
			{
				return "?";
			}
			if (cells.Count == 1)
			{
				return cells[0].x + "," + cells[0].z;
			}
			int x0 = cells.Min((IntVec3 c) => c.x), x1 = cells.Max((IntVec3 c) => c.x);
			int z0 = cells.Min((IntVec3 c) => c.z), z1 = cells.Max((IntVec3 c) => c.z);
			return "x " + x0 + (x0 == x1 ? "" : "-" + x1) + ", z " + z0 + (z0 == z1 ? "" : "-" + z1);
		}

		// "steel x 10, wood x 4, ..." largest first, at most MaxBreakdown kinds.
		private static string Breakdown(Dictionary<string, int> counts)
		{
			List<KeyValuePair<string, int>> sorted = counts.OrderByDescending((KeyValuePair<string, int> kv) => kv.Value).ToList();
			List<string> parts = sorted.Take(MaxBreakdown).Select((KeyValuePair<string, int> kv) => kv.Key + " x " + kv.Value).ToList();
			if (sorted.Count > MaxBreakdown)
			{
				parts.Add("and " + (sorted.Count - MaxBreakdown) + " more kinds");
			}
			return string.Join(", ", parts.ToArray());
		}
	}
}
