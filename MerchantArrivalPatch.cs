using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace VerbalCommands
{
	// "[reminder]: a merchant is nearby" when a merchant steps into the home zone, with no tick timer (user, 2026-10-15:
	// "see if you can have [reminder]: a merchant is nearby if that merchant steps into home zone, WITHOUT a tick based
	// timer."). Every step a pawn takes runs Pawn_PathFollower.TryEnterNextPathCell, which sets its new cell
	// (Verse.AI\Pawn_PathFollower.cs:637-638, "this.pawn.Position = nextCell"); a Harmony postfix there sees the step as
	// it happens. A merchant: a pawn with a trader tracker that has a trader kind (Pawn.trader, Verse\Pawn.cs:90;
	// Pawn_TraderTracker.traderKind, RimWorld\Pawn_TraderTracker.cs:13), not the player's. The first check is
	// "trader == null", so a step of any other pawn costs one field read. Patched by hand, like ConstructionFailPatch.
	internal static class MerchantArrivalPatch
	{
		public static void Apply()
		{
			try
			{
				MethodInfo original = typeof(Pawn_PathFollower).GetMethod("TryEnterNextPathCell", BindingFlags.NonPublic | BindingFlags.Instance);
				if (original == null)
				{
					Log.Warning("[VerbalCommands] Pawn_PathFollower.TryEnterNextPathCell not found; the merchant reminder is off.");
					return;
				}
				Harmony harmony = new Harmony("kopaz.verbalcommands.merchant");
				harmony.Patch(original, null, new HarmonyMethod(typeof(MerchantArrivalPatch).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)));
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] Could not patch Pawn_PathFollower.TryEnterNextPathCell (is Harmony loaded?): " + ex.Message);
			}
		}

		// ___pawn: Pawn_PathFollower's own "protected Pawn pawn" (Pawn_PathFollower.cs:9).
		public static void Postfix(Pawn ___pawn)
		{
			if (___pawn == null || ___pawn.trader == null)
			{
				return;
			}
			try
			{
				if (___pawn.trader.traderKind == null || ___pawn.Faction == Faction.OfPlayer || !___pawn.Spawned || ___pawn.Map == null || !___pawn.Map.IsPlayerHome)
				{
					return;
				}
				if (___pawn.Map.areaManager.Home[___pawn.Position])
				{
					AgentReminderScan.MerchantEntered(___pawn);
				}
			}
			catch (Exception)
			{
			}
		}
	}
}
