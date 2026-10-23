using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;
using Verse.AI;

namespace VerbalCommands
{
	// Agent 05, brief C: read-only questions about buildings, blueprints and the research path (MAIN THREAD ONLY like
	// every query; nothing here changes the game). Every name starts with RbBuild or RbResearch.
	//   query building       "show building TARGETS[ in ROOM | at SPOT]": the state of each matching player building.
	//   query blueprints     "show blueprints": the player's blueprints and frames, what they lack, what blocks them.
	//   query research path  "show research path to PROJECT": the unfinished prerequisites in doing order.
	// Game facts (decompiled source under sources\RimWorldDecompiled\, line numbers of that tree):
	//   ListerBuildings.allBuildingsColonist (Verse\ListerBuildings.cs:68; Add :103-111: player faction, no natural rock)
	//   Thing.HitPoints (Verse\Thing.cs:93), Thing.MaxHitPoints (:105), CellRect.Contains (Verse\CellRect.cs:936),
	//   CellRect.ClosestCellTo (:1042), GenAdj.OccupiedRect(Thing) (Verse\GenAdj.cs:572)
	//   CompFlickable.SwitchIsOn (RimWorld\CompFlickable.cs:38)
	//   CompPowerTrader.PowerOn (RimWorld\CompPowerTrader.cs:74), .PowerOutput (:36; negative = uses, positive = makes,
	//     the sign the inspect string reads at :279-295); CompPowerPlant is a CompPowerTrader (RimWorld\CompPowerPlant.cs:6)
	//   CompPowerBattery.StoredEnergy (RimWorld\CompPowerBattery.cs:28), CompProperties_Battery.storedEnergyMax
	//     (RimWorld\CompProperties_Battery.cs:5), shown in Wd at CompPowerBattery.cs:114-118
	//   CompRefuelable.Fuel (RimWorld\CompRefuelable.cs:57), FuelPercentOfMax (:61), TargetFuelLevel (:35-46),
	//     allowAutoRefuel (:15), Props.fuelCapacity / targetFuelLevelConfigurable / showAllowAutoRefuelToggle (gizmo rules :347-374)
	//   CompTempControl.TargetTemperature (RimWorld\CompTempControl.cs:19-24; backing field targetTemperature :17)
	//   Building_Door.Open (RimWorld\Building_Door.cs:51), Building_Door.HoldOpen (:53; the toggle gizmo flips holdOpenInt :580)
	//   CompForbiddable.Forbidden (read as FormControls.cs:381 writes it)
	//   Building_TurretGun.holdFire (private field, RimWorld\Building_TurretGun.cs:21; the Hold fire toggle :647-659;
	//     CanToggleHoldFire = PlayerControlled :101), read by reflection, never toggled
	//   Building_Bed.ForOwnerType (RimWorld\Building_Bed.cs:92), Medical (:110), OwnersForReading (:40),
	//     CompAssignableToPawn.AssignedPawnsForReading (RimWorld\CompAssignableToPawn.cs:49)
	//   Building_WorkTable.billStack (RimWorld\Building_WorkTable.cs:8), BillStack.Count (RimWorld\BillStack.cs:27)
	//   StorageSettings.Priority (RimWorld\StorageSettings.cs:23-26; getter only reads), Building_Storage.GetStoreSettings
	//     (RimWorld\Building_Storage.cs:100)
	//   Blueprint.EntityToBuild (RimWorld\Blueprint.cs:150), Blueprint.TotalMaterialCost (:126; Blueprint_Build.cs:65),
	//     Blueprint.ThingCountNeeded (:133), IConstructible (RimWorld\IConstructible.cs), Blueprint_Install.EntityToBuild
	//     (RimWorld\Blueprint_Install.cs:54; its TotalMaterialCost logs an error, :69, so it is never called)
	//   Frame.workDone (RimWorld\Frame.cs:15), Frame.WorkToBuild (:39), Frame.ThingCountNeeded (:228), Frame.TotalMaterialCost
	//     (:211; CostListAdjusted, RimWorld\CostListCalculator.cs:85 only fills its own cost cache)
	//   ResourceCounter.GetCount (Verse\Map.cs:81 resourceCounter; RimWorld\ResourceCounter.cs:75: storage only)
	//   ForbidUtility.IsForbidden(Thing, Faction) (RimWorld\ForbidUtility.cs:191-206: reads CompForbiddable.Forbidden)
	//   Pawn_WorkSettings.WorkIsActive (RimWorld\Pawn_WorkSettings.cs:171; its ConfirmInitializedDebug :131 would initialise
	//     a pawn that has no settings, so Initialized (:56) is tested first)
	//   ReachabilityUtility.CanReach(Pawn, LocalTargetInfo, PathEndMode, Danger) (Verse\ReachabilityUtility.cs:8; Reachability
	//     .CanReach, Verse\Reachability.cs:102, only fills the reachability scratch/cache)
	//   Research tab: projects drawn = difficulty.AllowedBy(hideWhen) or current (RimWorld\MainTabWindow_Research.cs:212),
	//     tab content only when ResearchManager.TabInfoVisible (:788; RimWorld\ResearchManager.cs:55), IsHidden ones are drawn as
	//     "(unknown research)" (:915-929; Verse\ResearchProjectDef.cs:152); prerequisites + hiddenPrerequisites listed together
	//     (:1108-1135); bench present = any map's allBuildingsColonist has the def (:1127-1137); facilities (:1151-1170);
	//     techprints TechprintCount / TechprintsApplied (Verse\ResearchProjectDef.cs:204-216; tab :1418);
	//     CostApparent (:103), ProgressApparentString (:109), IsFinished (:120), CanStartNow (:164),
	//     PlayerHasAnyAppropriateResearchBench (:351); current project ResearchManager.IsCurrentProject (:171)
	public static partial class FormPlanner
	{
		// One answer never runs past this many buildings / group lines / lines (rule 6: about 60 lines).
		private const int RbBuildMaxBuildings = 20;
		private const int RbBuildMaxGroupLines = 40;
		private const int RbBuildMaxLines = 60;
		private const int RbResearchMaxSteps = 38;

		internal static QueryReply QueryReadbackBuildings(FormContext c)
		{
			switch (c.form)
			{
				case "query building":
					return QueryReply.Ok(RbBuildQueryBuilding(c));
				case "query blueprints":
					return QueryReply.Ok(RbBuildQueryBlueprints(c));
				case "query research path":
					return QueryReply.Ok(RbResearchQueryPath(c));
			}
			return null;
		}

		// =============================================================================================================
		// query building
		// =============================================================================================================

		private static string RbBuildQueryBuilding(FormContext c)
		{
			List<string> words = c.TextList("TARGETS");
			if (words.Count == 0)
			{
				throw new Fault("The question did not say which building.");
			}
			List<Room> rooms = c.Has("ROOM") ? ResolveRooms(c.map, c.Text("ROOM")) : null;
			IntVec3 spot = IntVec3.Invalid;
			bool hasSpot = false;
			if (c.Has("SPOT"))
			{
				string reason;
				if (!AgentPlace.TryCell(c.map, c.Text("SPOT"), out spot, out reason))
				{
					throw new Fault(reason);
				}
				hasSpot = true;
			}

			// The player's own spawned buildings, not frames (those are on "show blueprints."), not under fog.
			List<Building> all = new List<Building>();
			foreach (Building b in c.map.listerBuildings.allBuildingsColonist)
			{
				if (b.Spawned && b.Faction == Faction.OfPlayer && !(b is Frame) && !c.map.fogGrid.IsFogged(b.Position))
				{
					all.Add(b);
				}
			}

			List<Building> matched = new List<Building>();
			List<string> notes = new List<string>();
			Dictionary<ThingDef, string> plurals = new Dictionary<ThingDef, string>();
			foreach (string word in words)
			{
				List<Building> found = RbBuildMatchWord(all, word, plurals);
				if (found.Count == 0)
				{
					notes.Add("There is no " + word + " built on the map." + RbBuildBlueprintHint(c.map, word));
					continue;
				}
				if (rooms != null)
				{
					found = found.Where((Building b) => rooms.Contains(RoomOfThing(b))).ToList();
					if (found.Count == 0)
					{
						notes.Add("There is no " + word + " built in the " + c.Text("ROOM") + ".");
						continue;
					}
				}
				foreach (Building b in found)
				{
					if (!matched.Contains(b))
					{
						matched.Add(b);
					}
				}
			}

			if (hasSpot && matched.Count > 0)
			{
				// The matching building whose occupied rect holds the cell, else the nearest matching one.
				List<Building> at = matched.Where((Building b) => b.OccupiedRect().Contains(spot)).ToList();
				if (at.Count > 0)
				{
					matched = at;
				}
				else
				{
					IntVec3 s = spot;
					Building nearest = matched.OrderBy((Building b) => RbBuildDistSq(b, s)).First();
					matched = new List<Building> { nearest };
				}
			}
			else
			{
				matched = matched.OrderBy((Building b) => b.def.label ?? b.def.defName).ThenBy((Building b) => b.Position.x).ThenBy((Building b) => b.Position.z).ToList();
			}

			List<string> lines = new List<string>();
			int shown = 0;
			foreach (Building b in matched)
			{
				if (shown >= RbBuildMaxBuildings)
				{
					break;
				}
				List<string> block = new List<string>();
				block.Add(RbBuildHeader(c.map, b));
				foreach (string d in RbBuildDetails(c.map, b))
				{
					block.Add("  " + d);
				}
				if (shown > 0 && lines.Count + block.Count > RbBuildMaxLines)
				{
					break;
				}
				lines.AddRange(block);
				shown++;
			}
			if (matched.Count > shown)
			{
				lines.Add("... " + (matched.Count - shown) + " more; narrow it down with: show building NAME at SPOT, or: show building NAME in ROOM.");
			}
			lines.AddRange(notes);
			if (lines.Count == 0)
			{
				throw new Fault("No building fits that.");
			}
			return string.Join("\n", lines.ToArray());
		}

		private static int RbBuildDistSq(Building b, IntVec3 spot)
		{
			IntVec3 near = b.OccupiedRect().ClosestCellTo(spot);
			int dx = near.x - spot.x;
			int dz = near.z - spot.z;
			return dx * dx + dz * dz;
		}

		// A blueprint or frame of that name, not under fog: say where to look.
		private static string RbBuildBlueprintHint(Map map, string word)
		{
			string n = Norm(word);
			foreach (ThingRequestGroup group in new ThingRequestGroup[] { ThingRequestGroup.Blueprint, ThingRequestGroup.BuildingFrame })
			{
				foreach (Thing t in map.listerThings.ThingsInGroup(group).ToList())
				{
					if (t.Faction != Faction.OfPlayer || map.fogGrid.IsFogged(t.Position))
					{
						continue;
					}
					BuildableDef entity = RbBuildEntityOf(t);
					if (entity != null && !entity.label.NullOrEmpty() && (Norm(entity.label) == n || ContainsWord(Norm(entity.label), n)))
					{
						return " A blueprint or frame for it is waiting: see show blueprints.";
					}
				}
			}
			return "";
		}

		// The word as the player says it: a class word, else def label / plural, else a whole word inside a label.
		private static List<Building> RbBuildMatchWord(List<Building> all, string word, Dictionary<ThingDef, string> plurals)
		{
			string n = Norm(word);
			switch (n)
			{
				case "door":
				case "doors":
					return all.Where((Building b) => b is Building_Door).ToList();
				case "bed":
				case "beds":
					return all.Where((Building b) => b is Building_Bed).ToList();
				case "turret":
				case "turrets":
					return all.Where((Building b) => b is Building_Turret).ToList();
				case "bench":
				case "benches":
				case "workbench":
				case "workbenches":
					return all.Where((Building b) => b is Building_WorkTable).ToList();
			}
			List<Building> exact = all.Where((Building b) =>
			{
				string label = Norm(b.def.label);
				return label == n || Norm(BuildDefs.Label(b.def, b.Stuff)) == n || Norm(RbBuildPlural(b.def, plurals)) == n;
			}).ToList();
			if (exact.Count > 0)
			{
				return exact;
			}
			return all.Where((Building b) =>
			{
				string label = Norm(b.def.label);
				return ContainsWord(label, n) || ContainsWord(Norm(BuildDefs.Label(b.def, b.Stuff)), n) || ContainsWord(Norm(RbBuildPlural(b.def, plurals)), n);
			}).ToList();
		}

		// LanguageWorker.Pluralize(string, int) (Verse\LanguageWorker.cs:103-106), as Placement.cs:270 uses it.
		private static string RbBuildPlural(ThingDef def, Dictionary<ThingDef, string> cache)
		{
			string p;
			if (cache.TryGetValue(def, out p))
			{
				return p;
			}
			p = "";
			try
			{
				if (!def.label.NullOrEmpty() && Find.ActiveLanguageWorker != null)
				{
					p = Find.ActiveLanguageWorker.Pluralize(def.label) ?? "";
				}
			}
			catch (Exception)
			{
				p = "";
			}
			cache[def] = p;
			return p;
		}

		private static string RbBuildRoomText(Building b)
		{
			try
			{
				Room room = RoomOfThing(b);
				if (room != null && room.ProperRoom && (!StorageIndex.IsGenericRoomRole(room.Role) || StorageIndex.IsGrowRoom(room)))
				{
					return StorageIndex.RoomName(room);
				}
			}
			catch (Exception)
			{
			}
			return null;
		}

		private static string RbBuildHeader(Map map, Building b)
		{
			string room = RbBuildRoomText(b);
			return BuildDefs.Label(b.def, b.Stuff) + " at " + AgentPlace.Describe(map, b.Position) + (room != null ? " in " + room : "");
		}

		private static string RbBuildNum(float v)
		{
			return Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
		}

		private static FieldInfo rbBuildHoldFireField;
		private static bool rbBuildHoldFireLooked;

		// Building_TurretGun.holdFire is private (Building_TurretGun.cs:21): read, never written.
		private static bool? RbBuildHoldFire(Building b)
		{
			if (!(b is Building_TurretGun))
			{
				return null;
			}
			if (!rbBuildHoldFireLooked)
			{
				rbBuildHoldFireLooked = true;
				rbBuildHoldFireField = typeof(Building_TurretGun).GetField("holdFire", BindingFlags.Instance | BindingFlags.NonPublic);
			}
			if (rbBuildHoldFireField == null || rbBuildHoldFireField.FieldType != typeof(bool))
			{
				return null;
			}
			return (bool)rbBuildHoldFireField.GetValue(b);
		}

		// The lines that apply to this building, one fact each.
		private static List<string> RbBuildDetails(Map map, Building b)
		{
			List<string> lines = new List<string>();

			// on / off
			CompFlickable flick = b.GetComp<CompFlickable>();
			bool switchedOff = false;
			if (flick != null)
			{
				switchedOff = !flick.SwitchIsOn;
				lines.Add(switchedOff ? "off" : "on");
			}

			// power
			CompPowerTrader trader = b.GetComp<CompPowerTrader>();
			if (trader != null && !switchedOff)
			{
				bool generator = b.GetComp<CompPowerPlant>() != null;
				if (generator)
				{
					float made = trader.PowerOutput;
					lines.Add(made > 0.5f ? "making " + Math.Round(made).ToString("0", CultureInfo.InvariantCulture) + " W" : "not making power");
				}
				else if (!trader.PowerOn)
				{
					lines.Add("no power");
				}
				else
				{
					float draw = -trader.PowerOutput;
					lines.Add(draw > 0.5f ? "powered, using " + Math.Round(draw).ToString("0", CultureInfo.InvariantCulture) + " W" : "powered");
				}
			}
			CompPowerBattery battery = b.GetComp<CompPowerBattery>();
			if (battery != null && battery.Props.storedEnergyMax > 0f)
			{
				lines.Add("stored " + battery.StoredEnergy.ToString("F0", CultureInfo.InvariantCulture) + " of " + battery.Props.storedEnergyMax.ToString("F0", CultureInfo.InvariantCulture)
					+ " Wd (" + Percent(battery.StoredEnergy / battery.Props.storedEnergyMax) + ")");
			}

			// fuel
			CompRefuelable fuel = b.GetComp<CompRefuelable>();
			if (fuel != null && fuel.Props.fuelCapacity > 0f)
			{
				string text = "fuel " + RbBuildNum(fuel.Fuel) + " of " + RbBuildNum(fuel.Props.fuelCapacity) + " (" + Percent(fuel.FuelPercentOfMax) + ")";
				if (fuel.Props.targetFuelLevelConfigurable)
				{
					text += ", refuel to " + RbBuildNum(fuel.TargetFuelLevel);
				}
				if (fuel.Props.showAllowAutoRefuelToggle)
				{
					text += ", auto refuel " + (fuel.allowAutoRefuel ? "on" : "off");
				}
				lines.Add(text);
			}

			// target temperature
			CompTempControl temp = b.GetComp<CompTempControl>();
			if (temp != null && temp.TargetTemperature > -2000f)
			{
				lines.Add("target temperature " + Math.Round(temp.TargetTemperature).ToString("0", CultureInfo.InvariantCulture) + "°C");
			}

			// door
			Building_Door door = b as Building_Door;
			if (door != null)
			{
				lines.Add(door.Open ? "open" : "closed");
				if (door.HoldOpen)
				{
					lines.Add("held open");
				}
			}

			// forbidden
			CompForbiddable forbiddable = b.GetComp<CompForbiddable>();
			if (forbiddable != null && forbiddable.Forbidden)
			{
				lines.Add("forbidden");
			}

			// turret
			bool? hold = RbBuildHoldFire(b);
			if (hold.HasValue)
			{
				lines.Add("hold fire " + (hold.Value ? "on" : "off"));
			}

			// bed
			Building_Bed bed = b as Building_Bed;
			if (bed != null)
			{
				string kind = bed.ForOwnerType == BedOwnerType.Prisoner ? "prisoner bed" : bed.ForOwnerType == BedOwnerType.Slave ? "slave bed" : "colonist bed";
				lines.Add(kind + (bed.Medical ? ", medical" : ""));
				if (!bed.Medical && bed.CompAssignableToPawn != null)
				{
					List<Pawn> owners = bed.OwnersForReading;
					lines.Add(owners != null && owners.Count > 0 ? "owners: " + JoinNames(owners.Select((Pawn p) => p.LabelShort)) : "no owner");
				}
			}

			// bench
			Building_WorkTable bench = b as Building_WorkTable;
			if (bench != null && bench.billStack != null)
			{
				int bills = bench.billStack.Count;
				lines.Add(bills == 0 ? "no bills" : bills + (bills == 1 ? " bill" : " bills") + "; see them with: show bills at " + (b.def.label ?? b.def.defName));
			}

			// storage
			Building_Storage storage = b as Building_Storage;
			if (storage != null)
			{
				StorageSettings settings = storage.GetStoreSettings();
				if (settings != null)
				{
					lines.Add("storage priority " + settings.Priority.ToString().ToLowerInvariant());
				}
			}

			// hit points
			if (b.def.useHitPoints && b.HitPoints < b.MaxHitPoints)
			{
				lines.Add("hit points " + b.HitPoints + "/" + b.MaxHitPoints);
			}
			return lines;
		}

		// =============================================================================================================
		// query blueprints
		// =============================================================================================================

		private sealed class RbBuildGroup
		{
			public BuildableDef entity;
			public ThingDef stuff;
			public bool frame;
			public bool install;
			public List<Thing> things = new List<Thing>();
		}

		// What a blueprint or frame is going to be.
		private static BuildableDef RbBuildEntityOf(Thing t)
		{
			Blueprint bp = t as Blueprint;
			if (bp != null)
			{
				return bp.EntityToBuild() ?? bp.def.entityDefToBuild;
			}
			return t.def.entityDefToBuild;
		}

		private static string RbBuildGroupLabel(RbBuildGroup g)
		{
			string label = g.entity != null && !g.entity.label.NullOrEmpty() ? g.entity.label : "building";
			if (g.stuff != null)
			{
				label += " (" + g.stuff.label + ")";
			}
			if (g.install)
			{
				label += " (reinstall)";
			}
			return label;
		}

		private static string RbBuildQueryBlueprints(FormContext c)
		{
			Map map = c.map;
			List<RbBuildGroup> groups = new List<RbBuildGroup>();
			Dictionary<string, RbBuildGroup> byKey = new Dictionary<string, RbBuildGroup>();
			foreach (ThingRequestGroup requestGroup in new ThingRequestGroup[] { ThingRequestGroup.Blueprint, ThingRequestGroup.BuildingFrame })
			{
				foreach (Thing t in map.listerThings.ThingsInGroup(requestGroup).ToList())
				{
					if (t.Faction != Faction.OfPlayer || !t.Spawned || map.fogGrid.IsFogged(t.Position))
					{
						continue;
					}
					IConstructible constructible = t as IConstructible;
					BuildableDef entity = RbBuildEntityOf(t);
					if (constructible == null || entity == null)
					{
						continue;
					}
					bool frame = t is Frame;
					bool install = t is Blueprint_Install;
					ThingDef stuff = constructible.EntityToBuildStuff();
					string key = entity.defName + "|" + (stuff != null ? stuff.defName : "") + "|" + (frame ? "F" : install ? "I" : "B");
					RbBuildGroup g;
					if (!byKey.TryGetValue(key, out g))
					{
						g = new RbBuildGroup();
						g.entity = entity;
						g.stuff = stuff;
						g.frame = frame;
						g.install = install;
						byKey[key] = g;
						groups.Add(g);
					}
					g.things.Add(t);
				}
			}
			if (groups.Count == 0)
			{
				return "Nothing is waiting to be built.";
			}
			groups = groups.OrderByDescending((RbBuildGroup g) => g.things.Count).ThenBy((RbBuildGroup g) => RbBuildGroupLabel(g)).ToList();

			List<string> lines = new List<string>();
			int shown = 0;
			foreach (RbBuildGroup g in groups)
			{
				if (shown >= RbBuildMaxGroupLines)
				{
					break;
				}
				shown++;
				lines.Add(RbBuildGroupLine(map, g));
				foreach (string m in RbBuildMissing(map, g))
				{
					lines.Add("  " + m);
				}
			}
			if (groups.Count > shown)
			{
				lines.Add("... " + (groups.Count - shown) + " more kinds of blueprint or frame not listed.");
			}

			// What blocks the work.
			List<Pawn> free = map.mapPawns.FreeColonistsSpawned.ToList();
			bool anyBuilder = false;
			foreach (Pawn p in free)
			{
				if (p.workSettings != null && p.workSettings.Initialized && p.workSettings.WorkIsActive(WorkTypeDefOf.Construction))
				{
					anyBuilder = true;
					break;
				}
			}
			if (!anyBuilder)
			{
				lines.Add("Nobody does Construction.");
			}
			Pawn tester = free.FirstOrDefault((Pawn p) => !p.Downed);
			foreach (RbBuildGroup g in groups.Take(shown))
			{
				string label = RbBuildGroupLabel(g);
				int forbidden = g.things.Count((Thing t) => t.IsForbidden(Faction.OfPlayer));
				if (forbidden > 0)
				{
					lines.Add(label + ": " + forbidden + " of " + g.things.Count + " forbidden.");
				}
				if (tester != null)
				{
					Thing rep = g.things.FirstOrDefault((Thing t) => !t.IsForbidden(Faction.OfPlayer));
					if (rep != null)
					{
						bool reach = true;
						try
						{
							reach = tester.CanReach(rep, PathEndMode.Touch, Danger.Deadly);
						}
						catch (Exception)
						{
							reach = true;
						}
						if (!reach)
						{
							lines.Add(label + ": " + tester.LabelShort + " can't reach the one at " + AgentPlace.Describe(map, rep.Position) + ".");
						}
					}
				}
				foreach (ThingDefCountClass need in RbBuildNeeded(g))
				{
					if (map.resourceCounter.GetCount(need.thingDef) <= 0)
					{
						lines.Add(label + ": no " + need.thingDef.label + " in storage.");
					}
				}
			}
			return string.Join("\n", lines.ToArray());
		}

		private static string RbBuildGroupLine(Map map, RbBuildGroup g)
		{
			string label = RbBuildGroupLabel(g);
			Thing first = g.things[0];
			string near = "near " + AgentPlace.Describe(map, first.Position);
			int n = g.things.Count;
			if (!g.frame)
			{
				return label + ": " + n + (n == 1 ? " blueprint " : " blueprints ") + near;
			}
			float sum = 0f;
			foreach (Thing t in g.things)
			{
				Frame f = t as Frame;
				float work = f != null ? f.WorkToBuild : 0f;
				sum += work > 0f ? Math.Min(1f, f.workDone / work) : 0f;
			}
			string pct = Percent(sum / n);
			return label + ": " + (n == 1 ? "frame, " + pct + " built, " : n + " frames, " + pct + " built on average, ") + near;
		}

		// The materials the group still lacks: for a frame the cost minus what it holds, for a blueprint the whole cost.
		private static List<ThingDefCountClass> RbBuildNeeded(RbBuildGroup g)
		{
			List<ThingDefCountClass> result = new List<ThingDefCountClass>();
			if (g.install)
			{
				return result;
			}
			IConstructible first = g.things[0] as IConstructible;
			List<ThingDefCountClass> cost = null;
			try
			{
				cost = first != null ? first.TotalMaterialCost() : null;
			}
			catch (Exception)
			{
				cost = null;
			}
			if (cost == null)
			{
				return result;
			}
			foreach (ThingDefCountClass item in cost)
			{
				int total = 0;
				foreach (Thing t in g.things)
				{
					IConstructible ic = t as IConstructible;
					if (ic != null)
					{
						total += Math.Max(0, ic.ThingCountNeeded(item.thingDef));
					}
				}
				if (total > 0)
				{
					ThingDefCountClass need = new ThingDefCountClass(item.thingDef, total);
					result.Add(need);
				}
			}
			return result;
		}

		private static List<string> RbBuildMissing(Map map, RbBuildGroup g)
		{
			List<string> lines = new List<string>();
			foreach (ThingDefCountClass need in RbBuildNeeded(g))
			{
				lines.Add("needs " + need.thingDef.label + " " + need.count + " more, " + map.resourceCounter.GetCount(need.thingDef) + " in storage");
			}
			return lines;
		}

		// =============================================================================================================
		// query research path
		// =============================================================================================================

		// A project the Research tab shows the player (see the header comment for the lines).
		private static bool RbResearchShown(ResearchProjectDef p)
		{
			if (p == null || p.label.NullOrEmpty())
			{
				return false;
			}
			if (!(Find.Storyteller.difficulty.AllowedBy(p.hideWhen) || Find.ResearchManager.IsCurrentProject(p)))
			{
				return false;
			}
			if (p.tab != null && !Find.ResearchManager.TabInfoVisible(p.tab))
			{
				return false;
			}
			return !p.IsHidden;
		}

		private static List<ResearchProjectDef> RbResearchPrereqs(ResearchProjectDef p)
		{
			List<ResearchProjectDef> list = new List<ResearchProjectDef>();
			if (p.prerequisites != null)
			{
				list.AddRange(p.prerequisites.Where((ResearchProjectDef x) => x != null));
			}
			if (p.hiddenPrerequisites != null)
			{
				list.AddRange(p.hiddenPrerequisites.Where((ResearchProjectDef x) => x != null));
			}
			return list;
		}

		// Post-order walk: every project comes after its own unfinished prerequisites.
		private static void RbResearchCollect(ResearchProjectDef p, List<ResearchProjectDef> order, HashSet<ResearchProjectDef> seen)
		{
			if (p == null || p.IsFinished || !seen.Add(p))
			{
				return;
			}
			foreach (ResearchProjectDef pre in RbResearchPrereqs(p))
			{
				RbResearchCollect(pre, order, seen);
			}
			order.Add(p);
		}

		private static string RbResearchLabel(ResearchProjectDef p)
		{
			return UpperFirst(p.label);
		}

		private static bool RbResearchHaveBench(ThingDef bench)
		{
			// MainTabWindow_Research.cs:1127-1137: any map's allBuildingsColonist holds the def.
			foreach (Map map in Find.Maps)
			{
				foreach (Building b in map.listerBuildings.allBuildingsColonist)
				{
					if (b.def == bench)
					{
						return true;
					}
				}
			}
			return false;
		}

		private static string RbResearchPoints(ResearchProjectDef p)
		{
			return p.ProgressApparentString + " of " + p.CostApparent.ToString("F0", CultureInfo.InvariantCulture) + " points";
		}

		private static string RbResearchQueryPath(FormContext c)
		{
			string text = c.Text("PROJECT") ?? "";
			string n = Norm(text);
			if (n.Length == 0)
			{
				throw new Fault("The question did not say which research project.");
			}
			List<ResearchProjectDef> shown = DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(RbResearchShown).ToList();
			List<ResearchProjectDef> found = shown.Where((ResearchProjectDef p) => Norm(p.label) == n).ToList();
			if (found.Count == 0)
			{
				found = shown.Where((ResearchProjectDef p) => string.Equals(p.defName, text, StringComparison.OrdinalIgnoreCase)).ToList();
			}
			if (found.Count == 0)
			{
				string[] words = n.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
				found = shown.Where((ResearchProjectDef p) => words.Length > 0 && words.All((string w) => ContainsWord(Norm(p.label), w))).ToList();
			}
			if (found.Count == 0)
			{
				throw new Fault("There is no research project called '" + text + "'.");
			}
			if (found.Count > 1)
			{
				throw new Fault("'" + text + "' fits: " + CapList(found.Select((ResearchProjectDef p) => p.label), 8) + "; say which.");
			}
			ResearchProjectDef goal = found[0];
			if (goal.IsFinished)
			{
				List<string> gave = RbResearchGives(goal);
				return RbResearchLabel(goal) + " is already researched" + (gave.Count > 0 ? "; " + string.Join("; ", gave.ToArray()) : "") + ".";
			}

			List<ResearchProjectDef> order = new List<ResearchProjectDef>();
			RbResearchCollect(goal, order, new HashSet<ResearchProjectDef>());
			int before = order.Count - 1;
			List<string> lines = new List<string>();
			string state = goal.ProgressReal > 0f ? "in progress" : "not started";
			string need = before == 0 ? "no project needed first" : "needs " + before + (before == 1 ? " project" : " projects") + " first";
			lines.Add(RbResearchLabel(goal) + ": " + state + ", " + RbResearchPoints(goal) + " (" + need + ").");
			for (int i = 0; i < order.Count; i++)
			{
				bool isGoal = i == order.Count - 1;
				if (!isGoal && i >= RbResearchMaxSteps)
				{
					if (i == RbResearchMaxSteps)
					{
						lines.Add("... " + (order.Count - 1 - RbResearchMaxSteps) + " more steps not listed; ask about one project: show research path to NAME.");
					}
					continue;
				}
				lines.Add((i + 1) + ". " + RbResearchStepText(order[i]));
			}
			return string.Join("\n", lines.ToArray());
		}

		private static string RbResearchStepText(ResearchProjectDef p)
		{
			List<string> parts = new List<string>();
			parts.Add(RbResearchLabel(p) + (Find.ResearchManager.IsCurrentProject(p) ? " (current)" : "") + ": " + RbResearchPoints(p));
			if (p.requiredResearchBuilding != null)
			{
				parts.Add("bench: " + p.requiredResearchBuilding.label + (RbResearchHaveBench(p.requiredResearchBuilding) ? " (you have one)" : " (none built)"));
			}
			if (!p.requiredResearchFacilities.NullOrEmpty())
			{
				string facilities = JoinNames(p.requiredResearchFacilities.Where((ThingDef f) => f != null).Select((ThingDef f) => f.label));
				bool benchOk = p.PlayerHasAnyAppropriateResearchBench;
				parts.Add("facilities: " + facilities + (benchOk ? " (in place)" : " (no bench has them yet)"));
			}
			if (p.TechprintCount > 0)
			{
				parts.Add("techprints: " + p.TechprintsApplied + " of " + p.TechprintCount);
			}
			List<string> after = RbResearchPrereqs(p).Where((ResearchProjectDef x) => !x.IsFinished).Select((ResearchProjectDef x) => x.label).Distinct().ToList();
			if (after.Count > 0)
			{
				parts.Add("after: " + JoinNames(after));
			}
			if (p.CanStartNow)
			{
				parts.Add("can start now");
			}
			parts.AddRange(RbResearchGives(p));
			return string.Join("; ", parts.ToArray()) + ".";
		}

		// User 2026-10-04: what a project unlocks, as the research tab shows it. "unlocks: ..." = the buildings,
		// recipes' products, plants, floors and operations that list it (ResearchProjectDef.UnlockedDefs,
		// Verse\ResearchProjectDef.cs:301-329); "opens: ..." = the projects that have it as a prerequisite.
		private static List<string> RbResearchGives(ResearchProjectDef p)
		{
			List<string> parts = new List<string>();
			try
			{
				List<string> unlocked = p.UnlockedDefs.Where((Def d) => d != null && !d.label.NullOrEmpty()).Select((Def d) => d.label).Distinct().ToList();
				if (unlocked.Count > 0)
				{
					parts.Add("unlocks: " + CapList(unlocked, 12));
				}
			}
			catch (Exception)
			{
			}
			List<string> opens = DefDatabase<ResearchProjectDef>.AllDefsListForReading
				.Where((ResearchProjectDef q) => q != p && RbResearchShown(q) && RbResearchPrereqs(q).Contains(p))
				.Select((ResearchProjectDef q) => q.label).Distinct().ToList();
			if (opens.Count > 0)
			{
				parts.Add("opens: " + CapList(opens, 8));
			}
			return parts;
		}
	}
}
