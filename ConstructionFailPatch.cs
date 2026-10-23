using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace VerbalCommands
{
	// The game's "construction failed" message for every failed build over 1 work, not only over 1400 (user,
	// 2026-10-15: "Im going to guess if the work is over 1400. so that means you can reduce that and fire it for small
	// shit too. make that change so it fires if the work is more than 1. that oughta save onto .rws").
	// Frame.FailConstruction (RimWorld\Frame.cs:408-437) posts
	//   Messages.Message("MessageConstructionFailed".Translate(LabelEntityToBuild, worker.LabelShort, worker.Named("WORKER")),
	//     new TargetInfo(base.Position, map), MessageTypeDefOf.NegativeEvent);
	// only when base.Faction == Faction.OfPlayer && WorkToBuild > 1400f (:433-436). The prefix notes a player frame of
	// more than 1 and at most 1400 work (above 1400 the game posts it itself); the postfix posts that same line. It is a
	// historical message, so it goes into Find.Archive and the save (Messages.cs:55-62, Archive.cs:18), where
	// AgentWorkChanceScan reads it.
	// Patched by hand (no Harmony attributes on types), so a missing Harmony only fails Apply, not the whole DLL.
	internal static class ConstructionFailPatch
	{
		public static void Apply()
		{
			try
			{
				MethodInfo original = typeof(Frame).GetMethod("FailConstruction", BindingFlags.Public | BindingFlags.Instance, null, new Type[] { typeof(Pawn) }, null);
				if (original == null)
				{
					Log.Warning("[VerbalCommands] Frame.FailConstruction not found; the small-build failure message is off.");
					return;
				}
				Harmony harmony = new Harmony("kopaz.verbalcommands");
				harmony.Patch(original,
					new HarmonyMethod(typeof(ConstructionFailPatch).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
					new HarmonyMethod(typeof(ConstructionFailPatch).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)));
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] Could not patch Frame.FailConstruction (is Harmony loaded?): " + ex.Message);
			}
		}

		// Before the frame is destroyed: what the message needs, when the game itself will not post it.
		public static void Prefix(Frame __instance, out object[] __state)
		{
			__state = null;
			try
			{
				if (__instance != null && __instance.Spawned && __instance.Faction == Faction.OfPlayer)
				{
					float work = __instance.WorkToBuild;
					if (work > 1f && work <= 1400f)
					{
						__state = new object[] { __instance.LabelEntityToBuild, __instance.Position, __instance.Map };
					}
				}
			}
			catch (Exception)
			{
				__state = null;
			}
		}

		// The game's own line (Frame.cs:435), for the builds it leaves silent.
		public static void Postfix(Pawn worker, object[] __state)
		{
			if (__state == null || worker == null)
			{
				return;
			}
			try
			{
				Messages.Message("MessageConstructionFailed".Translate((string)__state[0], worker.LabelShort, worker.Named("WORKER")),
					new TargetInfo((IntVec3)__state[1], (Map)__state[2]), MessageTypeDefOf.NegativeEvent);
			}
			catch (Exception)
			{
			}
		}
	}
}
