using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Packed (minified) items and installing them (user, 2026-10-17):
	//   "for Build. Fallback. since it is (I think) currently being used for other build, First, it will look for existing
	//   items sitting on map (i.e. uninstalled). It will then list items that it queried. THEN offer them to say Build
	//   {FullItemID} at x,z. Install also works." / "Install small sculpture will do exact same thing." / "List packed bed
	//   first. If there is non, then fallback." / "If the model says "build NEW bed", only then it will build a new one.
	//   After list, have it also say "If you want to build a NEW bed instead of installing it, say "build/install new bed at
	//   x,z"."" The ID is the game's Thing.ThingID ("MinifiedThing311669", the user's pick); a name finds an item by its
	//   base name ("small sculpture") or its full label ("sandstone small sculpture (normal)", MinifiedThing.LabelNoCount =
	//   the inner thing's, MinifiedThing.cs:73). The NEW line only when the thing can be built new (the user: "Whatever that
	//   would have less token going into model works"). Texts: the user's pick "As shown".
	// "install" is "build" (router VERBS). The router lets a name from the "packed" vocabulary line, or an ID, through.
	// Installing is the game's install tool: GenConstruct.PlaceBlueprintForInstall (Designator_Install.cs:118-121).
	internal static class AgentInstall
	{
		private const string InstallLine = "If you want to install, say Build {fullItemId} at x,z. Install also works.";

		private static readonly string[] BuildForms = { "build", "build at", "build near", "build anywhere", "build line" };

		// Packed items lying on the map in view.
		private static List<MinifiedThing> Packed(Map map)
		{
			return map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing).OfType<MinifiedThing>()
				.Where((MinifiedThing m) => m.Spawned && m.InnerThing != null && AgentPlace.Visible(map, m.Position)).ToList();
		}

		// The "packed" vocabulary line: each packed item's base name and full label.
		internal static IEnumerable<string> PackedNames(Map map)
		{
			foreach (MinifiedThing m in Packed(map))
			{
				yield return m.InnerThing.def.label;
				yield return m.LabelNoCount;
			}
		}

		// Before a build form is planned (FormPlanner.Dispatch): the packed items of that name, or the install of one by
		// its ID; null to plan the form as before.
		internal static PlanReply PackedFirst(FormContext c)
		{
			if (Array.IndexOf(BuildForms, c.form) < 0 || c.Text("NEW") == "yes")
			{
				return null;
			}
			string name = (c.Text("BUILDING") ?? "").Trim();
			if (name.Length == 0)
			{
				return null;
			}
			Map map = c.map;
			if (System.Text.RegularExpressions.Regex.IsMatch(name, "^minifiedthing[a-z]*[0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
			{
				if (c.form != "build at")
				{
					throw new Fault(InstallLine);
				}
				MinifiedThing item = map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing).OfType<MinifiedThing>()
					.FirstOrDefault((MinifiedThing m) => m.Spawned && m.InnerThing != null && string.Equals(m.ThingID, name, StringComparison.OrdinalIgnoreCase));
				if (item == null)
				{
					throw new Fault("Nothing on the map has the ID " + name + ".");
				}
				return PlanInstall(c, item);
			}
			List<MinifiedThing> found = Packed(map)
				.Where((MinifiedThing m) => FormPlanner.SameName(m.InnerThing.def.label, name) || FormPlanner.SameName(m.LabelNoCount, name))
				.OrderBy((MinifiedThing m) => m.thingIDNumber).ToList();
			if (found.Count == 0)
			{
				return null;
			}
			List<string> lines = new List<string>();
			foreach (MinifiedThing m in found)
			{
				Zone zone = map.zoneManager.ZoneAt(m.Position);
				lines.Add(m.ThingID + ": " + m.LabelNoCount + ", " + Xz(m.Position) + (zone is Zone_Stockpile ? ", in " + zone.label : ""));
			}
			lines.Add(InstallLine);
			string baseName = found[0].InnerThing.def.label;
			if (AgentBuild.BuildingsNow().Any((string b) => FormPlanner.SameName(b, baseName)))
			{
				lines.Add("If you want to build a NEW " + baseName + " instead of installing it, say \"build/install new " + baseName + " at x,z\".");
			}
			throw new Fault(string.Join("\n", lines.ToArray()));
		}

		// "build ID at SPOT[ facing SIDE]": the game's install blueprint for that item.
		private static PlanReply PlanInstall(FormContext c, MinifiedThing item)
		{
			Map map = c.map;
			Thing inner = item.InnerThing;
			ThingDef def = inner.def;
			string label = item.LabelNoCount;
			IntVec3 cell = AgentBuild.SpotCell(c);
			Plan plan = FormPlanner.NewPlan("agent install");
			Rot4 rot = AgentBuild.RotFor(def, c.Text("FACING"), plan.notes);
			string problem = AgentBuild.CellProblem(map, def, cell, rot, inner.Stuff);
			if (problem != null)
			{
				throw new Fault("Can't install " + label + " at " + Xz(cell) + ": " + problem);
			}
			string line = "Install " + label + " at " + AgentPlace.Describe(map, cell);
			string facing = AgentBuild.FacingText(map, def, cell, rot);
			if (facing.Length > 0)
			{
				line += ", " + facing;
			}
			line += ".";
			Write write = new Write();
			write.description = "Install: " + label + " at " + Xz(cell);
			write.apply = delegate
			{
				if (item.Destroyed || !item.Spawned || item.InnerThing == null)
				{
					throw new Fault(FormPlanner.UpperFirst(label) + " is gone, so it was not installed.");
				}
				// As the game's install tool (Designator_Install.cs:118-121).
				GenSpawn.WipeExistingThings(cell, rot, def.installBlueprintDef, map, DestroyMode.Deconstruct);
				GenConstruct.PlaceBlueprintForInstall(item, cell, map, rot, Faction.OfPlayer);
			};
			plan.writes.Add(write);
			return FormPlanner.Reply(plan, line, null, null);
		}

		private static string Xz(IntVec3 c)
		{
			return c.x.ToString(CultureInfo.InvariantCulture) + ", " + c.z.ToString(CultureInfo.InvariantCulture);
		}
	}
}
