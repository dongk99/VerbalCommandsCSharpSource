using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// Brief 23: the VOCABULARY block the script's vocabulary() host function returns (contract section 4):
	// one "name: a, b, c" line per kind, lower-case line names, entries as the game labels them, each name
	// once, a line with nothing to list left out. The lines of router_lab\vocabulary.txt come first
	// (rooms, benches, colonists, work types, assignments, weapon group slots, taken weapon group slots,
	// buildings, doors, layouts), then the lines of contract section 4. MAIN THREAD ONLY.
	public static class VocabularyText
	{
		private const string LogPrefix = "[VerbalCommands] vocabulary: ";

		// A cap for the lists that can grow large (all the buildings the player could build).
		private const int BuildingCap = 250;

		public static string Build(Map map, string orderText)
		{
			StringBuilder sb = new StringBuilder();
			string order = FormPlanner.Norm(orderText ?? "");

			AddLine(sb, "rooms", () => Rooms(map));
			AddLine(sb, "benches", () => Benches(map));
			AddLine(sb, "colonists", () => map.mapPawns.FreeColonists.Select((Pawn p) => p.LabelShort));
			// Agent 03: what a player can point at on a map with no rooms (AgentPlace.cs).
			AddLine(sb, "landmarks", () => AgentPlace.LandmarkNames(map));
			// Agent 09: the enemies in view, by stable names (AgentTargets.cs). "hostiles: none" is sent too.
			try
			{
				sb.Append(AgentTargets.VocabularyLine(map)).Append('\n');
			}
			catch (Exception ex)
			{
				Log.Warning(LogPrefix + "the 'hostiles' line could not be made: " + ex.Message);
			}
			AddLine(sb, "work types", () => DefDatabase<WorkTypeDef>.AllDefsListForReading.Where((WorkTypeDef w) => w.visible).Select((WorkTypeDef w) => w.labelShort));
			AddLine(sb, "assignments", () => DefDatabase<TimeAssignmentDef>.AllDefsListForReading
				.Where((TimeAssignmentDef t) => t != TimeAssignmentDefOf.Meditate || ModsConfig.RoyaltyActive)
				.Select((TimeAssignmentDef t) => t.LabelCap.ToString()));
			// User 2026-10-13: the skills a control group can be made by (FormControlGroups.cs).
			AddLine(sb, "skills", () => FormPlanner.SkillNames());
			AddLine(sb, "weapon group slots", () => Enumerable.Range(1, 9).Select((int s) => s.ToString()));
			AddLine(sb, "taken weapon group slots", () => TakenWeaponGroupSlots());
			// Brief 26: the names of the weapon groups that exist, for questions about them.
			AddLine(sb, "weapon groups", () => WeaponGroupNames());
			// Agent 08: what the build menu shows now (Designator_Build.Visible: research, tech level,
			// prerequisite buildings, difficulty), not only finished research; and the floors line.
			AddLine(sb, "buildings", () => AgentBuild.BuildingsNow());
			AddLine(sb, "floors", () => AgentBuild.Floors());
			// User 2026-10-17: packed items on the map, by base name and full label ("build NAME" lists them, AgentInstall.cs).
			AddLine(sb, "packed", () => AgentInstall.PackedNames(map));
			AddLine(sb, "doors", () => Doors(map));
			AddLine(sb, "layouts", () => LayoutMemory.ViewFor(map).layouts.Select((SavedLayout l) => l.roleLabel));

			// Contract section 4: the new lines.
			AddLine(sb, "storage buildings", () => StorageBuildings(map));
			// User 2026-10-07: the game IDs a storage sentence may name one storage building by ("storage ID").
			AddLine(sb, "storage ids", () => StorageIds(map));
			string recipes = Recipes(map, order);
			if (recipes.Length > 0)
			{
				sb.Append("recipes: ").Append(recipes).Append('\n');
			}
			AddLine(sb, "switchable", () => OnMap<CompFlickable>(map, (Building b) => !(b is Building_Vent)));
			AddLine(sb, "vents", () => OnMapOfClass<Building_Vent>(map));
			AddLine(sb, "temperature controls", () => OnMap<CompTempControl>(map, null));
			AddLine(sb, "fuelled", () => OnMap<CompRefuelable>(map, null));
			AddLine(sb, "turrets", () => OnMapOfClass<Building_TurretGun>(map));
			AddLine(sb, "clothing groups", () => Current.Game.outfitDatabase.AllOutfits.Select((ApparelPolicy p) => p.label));
			AddLine(sb, "food groups", () => Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Select((FoodPolicy p) => p.label));
			AddLine(sb, "drug groups", () => Current.Game.drugPolicyDatabase.AllPolicies.Select((DrugPolicy p) => p.label));
			AddLine(sb, "reading groups", () => Current.Game.readingPolicyDatabase.AllReadingPolicies.Select((ReadingPolicy p) => p.label));
			AddLine(sb, "medical care", () => MedicalCareLevels().Select((MedicalCareCategory m) => m.GetLabel().ToString()));
			// The medicines the Assign tab's Carry column offers (InventoryStockGroupDefOf.Medicine.thingDefs; Core
			// InventoryStockGroups.xml: herbal, industrial, glitterworld), for "set carry medicine of PAWNS to N MEDICINE."
			AddLine(sb, "carry medicines", () => InventoryStockGroupDefOf.Medicine.thingDefs.Select((ThingDef d) => d.label));
			AddLine(sb, "areas", () => map.areaManager.AllAreas.Where((Area a) => a is Area_Allowed).Select((Area a) => a.Label));
			// Agent 06: zones by their game label, and the crops a growing zone can be set to now.
			AddLine(sb, "zones", () => AgentZones.ZoneNames(map));
			AddLine(sb, "crops", () => AgentZones.CropNames(map));
			// Tip injection: the know-how files "i need tips on NAME." can open (TipInjector.cs).
			AddLine(sb, "tips", () => TipInjector.Names());

			// Brief 27, wave 1: research projects, tame animals, prisoners, letters, operations, trainings, modes, drugs.
			AddLine(sb, "research", () => DefDatabase<ResearchProjectDef>.AllDefsListForReading
				.Where((ResearchProjectDef p) => !p.label.NullOrEmpty() && p.CanStartNow).Select((ResearchProjectDef p) => p.label).Take(ResearchCap));
			AddLine(sb, "animals", () => map.mapPawns.SpawnedColonyAnimals.Select((Pawn p) => p.LabelShort));
			AddLine(sb, "prisoners", () => map.mapPawns.PrisonersOfColony.Concat(map.mapPawns.SlavesOfColonySpawned).Select((Pawn p) => p.LabelShort));
			AddRaw(sb, "letters", Letters());
			AddRaw(sb, "operations", Operations(map, order));
			AddLine(sb, "trainings", () => FormPlanner.TrainingKinds(map.mapPawns.SpawnedColonyAnimals));
			AddLine(sb, "prisoner modes", () => PrisonerModes(map));
			AddLine(sb, "drugs", () => Current.Game.drugPolicyDatabase.AllPolicies.SelectMany((DrugPolicy p) =>
				Enumerable.Range(0, p.Count).Select((int i) => p[i].drug).Where((ThingDef d) => d != null).Select((ThingDef d) => d.label)));
			return sb.ToString();
		}

		private const int ResearchCap = 80;

		private static void AddRaw(StringBuilder sb, string name, Func<string> make)
		{
			try
			{
				string text = make();
				if (!string.IsNullOrEmpty(text))
				{
					sb.Append(name).Append(": ").Append(text).Append('\n');
				}
			}
			catch (Exception ex)
			{
				Log.Warning(LogPrefix + "the '" + name + "' line could not be made: " + ex.Message);
			}
		}

		// "wanderer joins (accept, reject); ransom for Bob (pay, refuse)": the open choice letters and the options a click offers.
		private static Func<string> Letters()
		{
			return delegate
			{
				List<string> parts = new List<string>();
				foreach (ChoiceLetter letter in FormPlanner.OpenChoiceLetters())
				{
					List<string> options = FormPlanner.ChoicesOf(letter).Where((FormPlanner.LetterChoice o) => !o.option.disabled).Select((FormPlanner.LetterChoice o) => o.label).ToList();
					string label = FormPlanner.LetterLabelOf(letter);
					if (label.Length > 0 && options.Count > 0)
					{
						parts.Add(label + " (" + string.Join(", ", options.ToArray()) + ")");
					}
				}
				return string.Join("; ", parts.Distinct().ToArray());
			};
		}

		// "Bob: install bionic arm, amputate left leg; Alice: ...": only for pawns named in the order, only operations that can be queued now.
		private static Func<string> Operations(Map map, string normalizedOrder)
		{
			return delegate
			{
				List<string> parts = new List<string>();
				foreach (Pawn pawn in FormPlanner.PawnsOfColony(map))
				{
					if (!FormPlanner.ContainsWord(normalizedOrder, FormPlanner.Norm(pawn.LabelShort)) || FormPlanner.WhyNoOperations(pawn) != null)
					{
						continue;
					}
					List<string> labels = Distinct(FormPlanner.SurgeryOptions(pawn).Where((FormPlanner.SurgeryOption o) => o.problem == null).Select((FormPlanner.SurgeryOption o) => o.full)).Take(OperationCap).ToList();
					if (labels.Count > 0)
					{
						parts.Add(pawn.LabelShort + ": " + string.Join(", ", labels.ToArray()));
					}
				}
				return string.Join("; ", parts.ToArray());
			};
		}

		private const int OperationCap = 40;

		// The exclusive interaction modes the prisoner tab offers at least one prisoner, and the slave modes when there are slaves.
		private static IEnumerable<string> PrisonerModes(Map map)
		{
			List<string> modes = new List<string>();
			foreach (PrisonerInteractionModeDef mode in FormPlanner.ExclusivePrisonerModes())
			{
				if (map.mapPawns.PrisonersOfColony.Any((Pawn p) => p.guest != null && FormPlanner.WhyPrisonerModeNotOffered(p, mode) == null))
				{
					modes.Add(mode.label);
				}
			}
			if (map.mapPawns.SlavesOfColonySpawned.Count > 0)
			{
				modes.AddRange(DefDatabase<SlaveInteractionModeDef>.AllDefsListForReading.OrderBy((SlaveInteractionModeDef d) => d.listOrder).Select((SlaveInteractionModeDef d) => d.label));
			}
			return modes;
		}

		internal static IEnumerable<MedicalCareCategory> MedicalCareLevels()
		{
			foreach (MedicalCareCategory m in Enum.GetValues(typeof(MedicalCareCategory)))
			{
				yield return m;
			}
		}

		// One line; an entry list that fails is logged and left out, never thrown.
		private static void AddLine(StringBuilder sb, string name, Func<IEnumerable<string>> entries)
		{
			try
			{
				List<string> list = Distinct(entries());
				if (list.Count > 0)
				{
					sb.Append(name).Append(": ").Append(string.Join(", ", list.ToArray())).Append('\n');
				}
			}
			catch (Exception ex)
			{
				Log.Warning(LogPrefix + "the '" + name + "' line could not be made: " + ex.Message);
			}
		}

		// Each name once (compared the way the mod compares names), in the order found.
		private static List<string> Distinct(IEnumerable<string> names)
		{
			List<string> result = new List<string>();
			HashSet<string> seen = new HashSet<string>();
			foreach (string name in names)
			{
				if (string.IsNullOrWhiteSpace(name))
				{
					continue;
				}
				// The script splits a line at commas, so an entry never holds one.
				string trimmed = name.Replace(",", "").Trim();
				if (seen.Add(FormPlanner.Norm(trimmed)))
				{
					result.Add(trimmed);
				}
			}
			return result;
		}

		internal static string RoomLabel(Room room)
		{
			try
			{
				return StorageIndex.RoomName(room);
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static IEnumerable<string> Rooms(Map map)
		{
			foreach (Room room in StorageIndex.AllProperRooms(map))
			{
				if (!StorageIndex.IsGenericRoomRole(room.Role) || StorageIndex.IsGrowRoom(room))
				{
					yield return RoomLabel(room);
				}
			}
		}

		private static IEnumerable<string> Benches(Map map)
		{
			foreach (Building_WorkTable bench in StorageIndex.AllPlayerBenches(map))
			{
				yield return bench.def.label;
			}
		}

		private static IEnumerable<string> TakenWeaponGroupSlots()
		{
			GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
			if (gc == null)
			{
				yield break;
			}
			foreach (KeyValuePair<int, GroupRule> kv in gc.AllGroups.OrderBy((KeyValuePair<int, GroupRule> p) => p.Key))
			{
				if (kv.Value != null)
				{
					yield return kv.Key + " (" + kv.Value.name + ")";
				}
			}
		}

		private static IEnumerable<string> WeaponGroupNames()
		{
			GameComponent_VerbalCommands gc = GameComponent_VerbalCommands.Instance;
			if (gc == null)
			{
				yield break;
			}
			foreach (KeyValuePair<int, GroupRule> kv in gc.AllGroups.OrderBy((KeyValuePair<int, GroupRule> p) => p.Key))
			{
				if (kv.Value != null && !string.IsNullOrWhiteSpace(kv.Value.name))
				{
					yield return kv.Value.name;
				}
			}
		}

		private static IEnumerable<string> Buildings()
		{
			int count = 0;
			foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if (count >= BuildingCap)
				{
					yield break;
				}
				if (!BuildDefs.IsPlayerBuildable(def) || def.label.NullOrEmpty() || !def.IsResearchFinished
					|| (def.building != null && def.building.isNaturalRock))
				{
					continue;
				}
				count++;
				yield return def.label;
			}
		}

		// "kitchen to dining room": one entry for each pair of named rooms a door joins.
		private static IEnumerable<string> Doors(Map map)
		{
			foreach (Building building in map.listerBuildings.allBuildingsColonist)
			{
				Building_Door door = building as Building_Door;
				if (door == null || !door.Spawned)
				{
					continue;
				}
				List<string> sides = FormPlanner.DoorSides(map, door)
					.Where((Room r) => !StorageIndex.IsGenericRoomRole(r.Role))
					.Select((Room r) => RoomLabel(r))
					.Where((string s) => !string.IsNullOrEmpty(s))
					.Distinct()
					.OrderBy((string s) => s, StringComparer.OrdinalIgnoreCase)
					.ToList();
				for (int i = 0; i < sides.Count; i++)
				{
					for (int j = i + 1; j < sides.Count; j++)
					{
						yield return sides[i] + " to " + sides[j];
					}
				}
				// A door that leads outside: "bedroom to outdoors" (user, 2026-10-25: "unforbid/forbid x (probably x being
				// doors) that goes to outdoors").
				if (FormPlanner.LeadsOutdoors(map, door))
				{
					foreach (string side in sides)
					{
						yield return side + " to outdoors";
					}
				}
			}
		}

		// Shelf, small shelf, hopper, bookcase, grave, sarcophagus, ...: what the player can build now and what
		// already stands on the map (anything with storage settings and a priority).
		private static IEnumerable<string> StorageBuildings(Map map)
		{
			foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if (BuildDefs.IsStorage(def) && !def.label.NullOrEmpty() && (def.IsResearchFinished || StandsOnMap(map, def)))
				{
					yield return def.label;
				}
			}
		}

		// The ThingID of every player storage building on the map that is not under fog (StorageIndex.CollectUnits).
		private static IEnumerable<string> StorageIds(Map map)
		{
			foreach (StorageUnit unit in StorageIndex.CollectUnits(map))
			{
				if (unit.zone != null)
				{
					continue;
				}
				foreach (Thing b in unit.buildings)
				{
					if (b.Spawned && !map.fogGrid.IsFogged(b.Position))
					{
						yield return b.ThingID;
					}
				}
			}
		}

		private static bool StandsOnMap(Map map, ThingDef def)
		{
			return map.listerBuildings.allBuildingsColonist.Any((Building b) => b.def == def);
		}

		// "electric stove: a, b; butcher table: c". Only for benches named in the order.
		private static string Recipes(Map map, string normalizedOrder)
		{
			try
			{
				List<string> parts = new List<string>();
				HashSet<ThingDef> doneDefs = new HashSet<ThingDef>();
				foreach (Building_WorkTable bench in StorageIndex.AllPlayerBenches(map))
				{
					if (!doneDefs.Add(bench.def) || !FormPlanner.ContainsWord(normalizedOrder, FormPlanner.Norm(bench.def.label)))
					{
						continue;
					}
					List<string> labels = Distinct(FormPlanner.AvailableRecipes(bench).Select((RecipeDef r) => r.label));
					if (labels.Count > 0)
					{
						parts.Add(bench.def.label + ": " + string.Join(", ", labels.ToArray()));
					}
				}
				return string.Join("; ", parts.ToArray());
			}
			catch (Exception ex)
			{
				Log.Warning(LogPrefix + "the 'recipes' line could not be made: " + ex.Message);
				return "";
			}
		}

		// Labels of the buildings on the map that have the comp, each kind once.
		private static IEnumerable<string> OnMap<T>(Map map, Func<Building, bool> filter) where T : ThingComp
		{
			foreach (Building b in map.listerBuildings.allBuildingsColonist)
			{
				if (b.Spawned && b.GetComp<T>() != null && (filter == null || filter(b)))
				{
					yield return b.def.label;
				}
			}
		}

		private static IEnumerable<string> OnMapOfClass<T>(Map map) where T : Building
		{
			foreach (Building b in map.listerBuildings.allBuildingsColonist)
			{
				if (b.Spawned && b is T)
				{
					yield return b.def.label;
				}
			}
		}
	}
}
