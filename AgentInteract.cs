using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using UnityEngine;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// User 2026-10-04: the playing agent has to find out which pawn it can do something with (only one pawn of a
	// caravan trades; quest givers work the same way), so the question is broad: "show interactable pawns" lists the
	// people on the map who are not colonists with what a right-click on each offers, and "use option OPTION on
	// TARGET with PAWN" gives that right-click order. Trading then goes through the game's own trade window, which
	// opens when the colonist reaches the trader: "show trade", "trade buy / sell COUNT THING", "accept trade",
	// "cancel trade" read and set that window's deal. MAIN THREAD ONLY.
	// Fair play: nothing here reads or sets dev mode or DebugSettings. The options are the game's own float menu;
	// the goods and prices are the game's own trade deal, readable only while the trade window is open (that is,
	// after a colonist walked to the trader, as a player must); the deal is settled by the game's own
	// TradeDeal.TryExecute, which checks silver and moves the things.
	//
	// Game facts relied on (decompiled tree sources\RimWorldDecompiled\):
	//   the right-click menu for selected pawns at a click position: FloatMenuMakerMap.GetOptions  RimWorld\FloatMenuMakerMap.cs:27-71
	//   only a pawn with CanTradeNow gets the "trade with" option, whose label names the trader and its kind, and
	//       which gives the job TradeWithPawn  RimWorld\FloatMenuOptionProvider_Trade.cs:19, :46, :56; Verse\Pawn.cs:1227
	//   the trade window sets up the session (TradeSession.SetupWith) and pauses the game (forcePause)  RimWorld\Dialog_Trade.cs:235-240
	//   the deal: TradeSession.deal.AllTradeables, CurrencyTradeable, UpdateCurrencyCount, TryExecute,
	//       DoesTraderHaveEnoughSilver  RimWorld\TradeDeal.cs:15-30, :132-148, :150-221, :223-234
	//   one line of the deal: Tradeable.Label, TraderWillTrade, IsCurrency, GetPriceFor, CountHeldBy, ActionToDo;
	//       a positive count is the colony buying (PositiveCountDirection Source)  RimWorld\Tradeable.cs:80, :100, :153, :388, :443, :137, :167-177
	//   setting a count: Transferable.CanAdjustTo / AdjustTo  RimWorld\Transferable.cs:85, :107
	//   Accept button: TryExecute, then Close; Cancel button: Close; a trader with a quest gives it when the window
	//       closes  RimWorld\Dialog_Trade.cs:366-395, :403-406, :447-455
	//   trade ships in orbit: Map.passingShipManager.passingShips  RimWorld\PassingShipManager.cs:10
	public static class AgentInteract
	{
		private const int PawnCap = 30;
		private const int OptionCap = 8;
		private const int OptionWidth = 70;
		private const int TradeLineCap = 55;

		private static bool tradeWindowSeen;

		// ---- entry points ------------------------------------------------------------------------------

		// FormPlanner.Query: null for a form that is not one of these.
		public static QueryReply Query(FormContext c)
		{
			switch (c.form)
			{
				case "query interactable":
					return QueryReply.Ok(Interactable(c));
				case "query trade":
					return QueryReply.Ok(ShowTrade(c));
				// The windows that hold the game paused, the trade window among them (AgentWindows.cs).
				case "query windows":
					return QueryReply.Ok(AgentWindows.Show(c));
			}
			return null;
		}

		// FormPlanner.Dispatch: null for a form that is not one of these.
		public static PlanReply Plan(FormContext c)
		{
			try
			{
				switch (c.form)
				{
					case "interact":
						return PlanInteract(c);
					case "trade buy":
						return PlanAdjust(c, true);
					case "trade sell":
						return PlanAdjust(c, false);
					case "trade accept":
						return PlanAccept(c);
					case "trade cancel":
						return PlanCancel(c);
					// "close window.": the top window that holds the game paused, never the trade window (AgentWindows.cs).
					case "window close":
						return AgentWindows.Plan(c);
				}
			}
			catch (Fault fault)
			{
				return PlanReply.Fail(fault.Message);
			}
			return null;
		}

		// GameComponentUpdate, every frame: when the trade window opens (a colonist reached the trader) a run in
		// progress is ended with that reason, so "show speed." says why the game stands still. Never throws.
		public static void Update()
		{
			try
			{
				if (Current.ProgramState != ProgramState.Playing || Find.WindowStack == null)
				{
					return;
				}
				bool open = TradeWindow() != null && TradeSession.Active;
				if (open && !tradeWindowSeen)
				{
					AgentTime.RequestPause("the trade window with " + TraderName() + " is open: ask 'show trade.', then 'accept trade.' or 'cancel trade.'");
				}
				tradeWindowSeen = open;
			}
			catch (Exception)
			{
			}
		}

		// ---- show interactable pawns -------------------------------------------------------------------

		private static string Interactable(FormContext c)
		{
			Map map = c.map;
			Pawn doer = Doer(c);
			List<string> lines = new List<string>();
			if (TradeWindow() != null && TradeSession.Active)
			{
				lines.Add("A trade window is open with " + TraderName() + ": ask 'show trade.'");
			}
			List<Pawn> pawns = Candidates(map).OrderBy((Pawn p) => p.Position.DistanceToSquared(doer.Position)).ToList();
			if (pawns.Count == 0)
			{
				lines.Add("Nobody but your own colonists is in view.");
			}
			else
			{
				lines.Add("People in view who are not your colonists, with what " + doer.LabelShort + " can do with each (the game's right-click orders):");
			}
			foreach (Pawn p in pawns.Take(PawnCap))
			{
				lines.Add("- " + Describe(map, p));
				List<FloatMenuOption> options = OptionsAt(doer, p.DrawPos);
				if (options.Count == 0)
				{
					lines.Add("    nothing to do with them now");
					continue;
				}
				foreach (FloatMenuOption o in options.Take(OptionCap))
				{
					lines.Add("    " + (o.Disabled ? "not now: " : "option: ") + Cut(LabelOf(o), OptionWidth));
				}
				if (options.Count > OptionCap)
				{
					lines.Add("    and " + (options.Count - OptionCap) + " more options");
				}
			}
			if (pawns.Count > PawnCap)
			{
				lines.Add("and " + (pawns.Count - PawnCap) + " more people, farther away.");
			}
			List<PassingShip> ships = map.passingShipManager != null ? map.passingShipManager.passingShips : null;
			if (ships != null && ships.Count > 0)
			{
				lines.Add("In orbit: " + FormPlanner.JoinNames(ships.Select((PassingShip s) => s.FullTitle)) + ". A ship is called from a powered comms console: 'use option call on X, Z with PAWN.' with the console's cell.");
			}
			lines.Add("To do one: 'use option WORDS on NAME with PAWN.' (WORDS from the option, NAME as written here, or the cell).");
			return string.Join("\n", lines.ToArray());
		}

		// People (and mechanoids) in view who are not free colonists. Wild animals are in "show wildlife.".
		private static List<Pawn> Candidates(Map map)
		{
			List<Pawn> list = new List<Pawn>();
			foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
			{
				if (p == null || p.Dead || p.IsFreeColonist || !AgentPlace.Visible(map, p.Position))
				{
					continue;
				}
				if (p.RaceProps.Humanlike || p.RaceProps.IsMechanoid)
				{
					list.Add(p);
				}
			}
			return list;
		}

		private static string Describe(Map map, Pawn p)
		{
			List<string> bits = new List<string>();
			string kind = FormPlanner.PlainText(p.KindLabel);
			if (!string.IsNullOrEmpty(kind))
			{
				bits.Add(kind);
			}
			if (p.Faction != null && p.Faction != Faction.OfPlayer)
			{
				bits.Add(FormPlanner.PlainText(p.Faction.Name));
			}
			if (p.IsPrisonerOfColony)
			{
				bits.Add("your prisoner");
			}
			else if (p.HostileTo(Faction.OfPlayer))
			{
				bits.Add("hostile");
			}
			if (p.Downed)
			{
				bits.Add("downed");
			}
			try
			{
				if (((ITrader)p).CanTradeNow && p.TraderKind != null)
				{
					bits.Add("TRADER: " + p.TraderKind.label);
				}
			}
			catch (Exception)
			{
			}
			if (p.mindState != null && p.mindState.hasQuest)
			{
				bits.Add("has a quest to give");
			}
			return FormPlanner.PlainText(p.LabelShort) + " (" + string.Join(", ", bits.ToArray()) + ") at " + AgentPlace.Describe(map, p.Position);
		}

		// ---- use option OPTION on TARGET with PAWN ------------------------------------------------------

		private static PlanReply PlanInteract(FormContext c)
		{
			Map map = c.map;
			Pawn doer = FormPlanner.ResolveOnePawn(c, "PAWN");
			if (!doer.Spawned || doer.Map != map)
			{
				throw new Fault(doer.LabelShort + " is not on the map.");
			}
			string wanted = (c.Text("OPTION") ?? "").Trim();
			if (wanted.Length == 0)
			{
				throw new Fault("The order did not say which option.");
			}
			string targetText;
			Vector3 pos = TargetPos(c, doer, out targetText);
			FloatMenuOption option = FindOption(doer, pos, wanted, targetText);
			string label = LabelOf(option);

			Plan plan = FormPlanner.NewPlan("agent interact");
			plan.notes.Add("This is the game's own right-click order; what it does is what that order does in the game.");
			Write write = new Write();
			write.description = doer.LabelShort + ": " + label;
			write.apply = delegate
			{
				FloatMenuOption now = FindOption(doer, pos, wanted, targetText);
				AgentPawnActions.Choose(now);
				AgentPawnActions.FollowJob(doer, label);
			};
			plan.writes.Add(write.Reads(() => AgentPawnActions.DoingLine(doer)));
			return FormPlanner.Reply(plan, doer.LabelShort + ": " + label + ".", new[] { doer }, null);
		}

		// Where the click goes: a cell ("120, 88") or a person in view by name (their draw position, as the game
		// clicks a pawn).
		private static Vector3 TargetPos(FormContext c, Pawn doer, out string targetText)
		{
			Map map = c.map;
			string text = (c.Text("TARGET") ?? "").Trim();
			if (text.Length == 0)
			{
				throw new Fault("The order did not say on whom or where.");
			}
			targetText = text;
			if (char.IsDigit(text[0]))
			{
				IntVec3 cell;
				string reason;
				if (!AgentPlace.TryCell(map, text, out cell, out reason))
				{
					throw new Fault(string.IsNullOrEmpty(reason) ? "'" + text + "' is not a cell I can find." : reason);
				}
				return cell.ToVector3Shifted();
			}
			string norm = FormPlanner.Norm(text);
			List<Pawn> found = Candidates(map).Where((Pawn p) => FormPlanner.PawnNameMatches(p, norm)).ToList();
			if (found.Count == 0)
			{
				throw new Fault("Nobody in view is called '" + text + "'; ask 'show interactable pawns.' for the names.");
			}
			if (found.Count > 1)
			{
				throw new Fault("Several in view are called '" + text + "': at " + FormPlanner.JoinNames(found.Take(6).Select((Pawn p) => Xz(p.Position)))
					+ ". Name the cell instead: 'use option ... on X, Z with " + doer.LabelShort + ".'");
			}
			return found[0].DrawPos;
		}

		// The one enabled option whose label holds the player's words; a greyed-out one is refused with its own
		// text (the game's reason); none or several: a Fault that lists what there is.
		private static FloatMenuOption FindOption(Pawn doer, Vector3 pos, string wanted, string targetText)
		{
			List<FloatMenuOption> options = OptionsAt(doer, pos);
			if (options.Count == 0)
			{
				throw new Fault("The game offers " + doer.LabelShort + " nothing to do on " + targetText + " right now.");
			}
			string norm = FormPlanner.Norm(wanted);
			List<FloatMenuOption> hits = options.Where((FloatMenuOption o) => FormPlanner.Norm(LabelOf(o)).Contains(norm)).ToList();
			List<FloatMenuOption> enabled = hits.Where((FloatMenuOption o) => !o.Disabled).ToList();
			if (enabled.Count == 1)
			{
				return enabled[0];
			}
			if (enabled.Count > 1)
			{
				List<string> labels = enabled.Select((FloatMenuOption o) => LabelOf(o)).Distinct().ToList();
				if (labels.Count == 1)
				{
					return enabled[0];
				}
				throw new Fault("'" + wanted + "' fits several options: " + string.Join("; ", labels.Take(6).ToArray()) + ". Say more of the one you mean.");
			}
			if (hits.Count > 0)
			{
				throw new Fault(AgentPawnActions.Why(hits[0]));
			}
			throw new Fault("No option on " + targetText + " has '" + wanted + "' in it. The options for " + doer.LabelShort + " are: "
				+ string.Join("; ", options.Take(OptionCap).Select((FloatMenuOption o) => LabelOf(o)).ToArray()) + ".");
		}

		// The game's whole right-click menu for one selected pawn at a position (FloatMenuMakerMap.cs:27-71).
		private static List<FloatMenuOption> OptionsAt(Pawn doer, Vector3 pos)
		{
			if (doer.Map != Find.CurrentMap)
			{
				throw new Fault("Switch the view to that map first; the game only gives orders on the map you look at.");
			}
			FloatMenuContext context;
			List<FloatMenuOption> options = FloatMenuMakerMap.GetOptions(new List<Pawn> { doer }, pos, out context);
			return options ?? new List<FloatMenuOption>();
		}

		// PAWN when the sentence named one, else the first free colonist who is up.
		private static Pawn Doer(FormContext c)
		{
			if (c.Has("PAWN"))
			{
				return FormPlanner.ResolveOnePawn(c, "PAWN");
			}
			Pawn doer = c.map.mapPawns.FreeColonistsSpawned.FirstOrDefault((Pawn p) => !p.Downed);
			if (doer == null)
			{
				throw new Fault("No colonist is up to ask for.");
			}
			return doer;
		}

		// ---- the trade window --------------------------------------------------------------------------

		private static Dialog_Trade TradeWindow()
		{
			return Find.WindowStack != null ? Find.WindowStack.WindowOfType<Dialog_Trade>() : null;
		}

		private static string TraderName()
		{
			ITrader trader = TradeSession.trader;
			if (trader == null)
			{
				return "nobody";
			}
			string name = FormPlanner.PlainText(trader.TraderName);
			return trader.TraderKind != null ? name + " (" + trader.TraderKind.label + ")" : name;
		}

		private static TradeDeal Deal()
		{
			if (TradeWindow() == null || !TradeSession.Active || TradeSession.deal == null)
			{
				throw new Fault("No trade window is open. Send a colonist to a trader first ('show interactable pawns.', then 'use option trade on NAME with PAWN.'), "
					+ "run the game until they meet, and ask again.");
			}
			return TradeSession.deal;
		}

		private static string ShowTrade(FormContext c)
		{
			TradeDeal deal = Deal();
			string word = c.Has("WORD") ? FormPlanner.Norm(c.Text("WORD")) : "";
			List<string> lines = new List<string>();
			string negotiator = TradeSession.playerNegotiator != null ? TradeSession.playerNegotiator.LabelShort : "nobody";
			lines.Add("Trading with " + TraderName() + "; " + negotiator + " does the talking." + (TradeSession.giftMode ? " The window is in gift mode." : ""));
			Tradeable silver = deal.CurrencyTradeable;
			if (silver != null)
			{
				lines.Add("Silver: you " + silver.CountHeldBy(Transactor.Colony) + ", they " + silver.CountHeldBy(Transactor.Trader) + ".");
			}
			lines.AddRange(DealLines(deal));
			List<Tradeable> rows = deal.AllTradeables.Where((Tradeable t) => t != null && t.HasAnyThing && !t.IsCurrency
				&& (word.Length == 0 || FormPlanner.Norm(t.Label).Contains(word))).ToList();
			// What they sell first, then what only you hold; by name inside each.
			rows = rows.OrderByDescending((Tradeable t) => t.CountHeldBy(Transactor.Trader) > 0).ThenBy((Tradeable t) => t.Label).ToList();
			if (rows.Count == 0)
			{
				lines.Add(word.Length > 0 ? "Nothing in this trade has '" + c.Text("WORD") + "' in its name." : "Nothing to trade.");
			}
			foreach (Tradeable t in rows.Take(TradeLineCap))
			{
				string line = "- " + FormPlanner.PlainText(t.Label) + ": you " + t.CountHeldBy(Transactor.Colony) + ", they " + t.CountHeldBy(Transactor.Trader);
				if (!t.TraderWillTrade)
				{
					line += "; they do not trade this";
				}
				else
				{
					line += "; buy at " + Money(t.GetPriceFor(TradeAction.PlayerBuys)) + ", sell at " + Money(t.GetPriceFor(TradeAction.PlayerSells));
				}
				lines.Add(line);
			}
			if (rows.Count > TradeLineCap)
			{
				lines.Add((rows.Count - TradeLineCap) + " more lines: ask 'show trade for WORD.' with a word of the name.");
			}
			lines.Add("Then: 'trade buy COUNT THING.', 'trade sell COUNT THING.', 'accept trade.' or 'cancel trade.'");
			return string.Join("\n", lines.ToArray());
		}

		// What the deal holds now: one line per thing set, and the silver that changes hands.
		private static List<string> DealLines(TradeDeal deal)
		{
			List<string> lines = new List<string>();
			deal.UpdateCurrencyCount();
			foreach (Tradeable t in deal.AllTradeables)
			{
				if (t == null || !t.HasAnyThing || t.IsCurrency || t.CountToTransfer == 0)
				{
					continue;
				}
				bool buys = t.ActionToDo == TradeAction.PlayerBuys;
				int n = Math.Abs(t.CountToTransfer);
				lines.Add("In the deal: " + (buys ? "buy " : "sell ") + n + " " + FormPlanner.PlainText(t.Label) + " at "
					+ Money(t.GetPriceFor(t.ActionToDo)) + " each.");
			}
			Tradeable silver = deal.CurrencyTradeable;
			if (lines.Count > 0 && silver != null)
			{
				int toColony = silver.CountToTransferToSource;
				lines.Add(toColony >= 0 ? "In the deal: you get " + toColony + " silver." : "In the deal: you pay " + (-toColony) + " silver.");
				if (silver.CountPostDealFor(Transactor.Colony) < 0)
				{
					lines.Add("You do not have that much silver; the game will refuse the deal.");
				}
				if (!deal.DoesTraderHaveEnoughSilver())
				{
					lines.Add("They do not have that much silver; they pay only what they have.");
				}
			}
			if (lines.Count == 0)
			{
				lines.Add("Nothing is set in the deal yet.");
			}
			return lines;
		}

		// "trade buy COUNT THING." / "trade sell COUNT THING.": sets that line of the open deal (replacing what it
		// was set to), as dragging its slider does. Nothing changes hands until "accept trade.".
		private static PlanReply PlanAdjust(FormContext c, bool buy)
		{
			TradeDeal deal = Deal();
			if (TradeSession.giftMode)
			{
				throw new Fault("The trade window is in gift mode; the mod only trades.");
			}
			double? said = c.Number("COUNT");
			if (!said.HasValue || said.Value < 1 || Math.Floor(said.Value) != said.Value)
			{
				throw new Fault("COUNT must be a whole number from 1.");
			}
			int count = (int)said.Value;
			Tradeable t = FindTradeable(deal, c.Text("THING"));
			string label = FormPlanner.PlainText(t.Label);
			if (!t.TraderWillTrade)
			{
				throw new Fault(TraderName() + " does not trade " + label + ".");
			}
			int target = buy ? count : -count;
			AcceptanceReport report = t.CanAdjustTo(target);
			if (!report.Accepted)
			{
				int have = t.CountHeldBy(buy ? Transactor.Trader : Transactor.Colony);
				throw new Fault((buy ? "They have " : "You have ") + have + " " + label + (string.IsNullOrEmpty(report.Reason) ? "" : " (" + report.Reason.StripTags() + ")") + "; " + count + " is too many.");
			}
			TradeAction action = buy ? TradeAction.PlayerBuys : TradeAction.PlayerSells;
			float price = t.GetPriceFor(action);
			Plan plan = FormPlanner.NewPlan("agent trade set");
			plan.notes.Add("This only sets the deal. Nothing changes hands until 'accept trade.'");
			Write write = new Write();
			write.description = "Trade deal: " + (buy ? "buy " : "sell ") + count + " " + label;
			write.apply = delegate
			{
				TradeDeal now = Deal();
				if (!now.AllTradeables.Contains(t))
				{
					throw new Fault("The trade changed; ask 'show trade.' again.");
				}
				AcceptanceReport again = t.CanAdjustTo(target);
				if (!again.Accepted)
				{
					throw new Fault("That many " + label + " can't be traded any more.");
				}
				t.AdjustTo(target);
				now.UpdateCurrencyCount();
			};
			plan.writes.Add(write.Reads(() => string.Join(" ", DealLines(Deal()).ToArray())));
			string line = "Set the deal: " + (buy ? "buy " : "sell ") + count + " " + label + " at " + Money(price) + " each (" + Money(price * count) + " silver).";
			return FormPlanner.Reply(plan, line, null, null);
		}

		private static Tradeable FindTradeable(TradeDeal deal, string name)
		{
			string text = (name ?? "").Trim();
			if (text.Length == 0)
			{
				throw new Fault("The order did not say what to trade.");
			}
			string norm = FormPlanner.Norm(text);
			List<Tradeable> rows = deal.AllTradeables.Where((Tradeable t) => t != null && t.HasAnyThing && !t.IsCurrency).ToList();
			List<Tradeable> hits = rows.Where((Tradeable t) => FormPlanner.Norm(t.Label) == norm).ToList();
			if (hits.Count == 0)
			{
				hits = rows.Where((Tradeable t) => t.ThingDef != null && FormPlanner.SameName(t.ThingDef.label, text)).ToList();
			}
			if (hits.Count == 0)
			{
				hits = rows.Where((Tradeable t) => FormPlanner.Norm(t.Label).Contains(norm)).ToList();
			}
			if (hits.Count == 0)
			{
				throw new Fault("Nothing in this trade is called '" + text + "'; ask 'show trade.' for the names.");
			}
			if (hits.Count > 1)
			{
				throw new Fault("'" + text + "' fits several lines: " + string.Join("; ", hits.Take(6).Select((Tradeable t) => FormPlanner.PlainText(t.Label)).ToArray())
					+ ". Write the name as 'show trade.' prints it.");
			}
			return hits[0];
		}

		// "accept trade.": the Accept button (Dialog_Trade.cs:366-395): TradeDeal.TryExecute, then the window closes.
		private static PlanReply PlanAccept(FormContext c)
		{
			TradeDeal deal = Deal();
			List<string> summary = DealLines(deal);
			if (!deal.AllTradeables.Any((Tradeable t) => t != null && t.HasAnyThing && !t.IsCurrency && t.CountToTransfer != 0))
			{
				throw new Fault("Nothing is set in the deal; say 'trade buy COUNT THING.' or 'trade sell COUNT THING.' first, or 'cancel trade.'");
			}
			Tradeable silver = deal.CurrencyTradeable;
			if (silver == null || silver.CountPostDealFor(Transactor.Colony) < 0)
			{
				throw new Fault("You can't afford that deal: " + string.Join(" ", summary.ToArray()));
			}
			Plan plan = FormPlanner.NewPlan("agent trade accept");
			Write write = new Write();
			write.description = "Trade with " + TraderName() + ": accept the deal";
			write.apply = delegate
			{
				TradeDeal now = Deal();
				bool traded;
				if (!now.TryExecute(out traded))
				{
					throw new Fault("The game refused the deal (the colony can't afford it, or a colonist will not do it).");
				}
				Dialog_Trade window = TradeWindow();
				if (window != null)
				{
					window.Close(false);
				}
			};
			plan.writes.Add(write.Reads(() => TradeWindow() == null ? "the trade is closed" : "the trade window is still open"));
			return FormPlanner.Reply(plan, "Accept the trade with " + TraderName() + ": " + string.Join(" ", summary.ToArray()), null, null);
		}

		// "cancel trade.": the Cancel button: the window closes, nothing changes hands.
		private static PlanReply PlanCancel(FormContext c)
		{
			Deal();
			Plan plan = FormPlanner.NewPlan("agent trade cancel");
			Write write = new Write();
			write.description = "Trade with " + TraderName() + ": close the window";
			write.apply = delegate
			{
				Dialog_Trade window = TradeWindow();
				if (window != null)
				{
					window.Close(false);
				}
			};
			plan.writes.Add(write.Reads(() => TradeWindow() == null ? "the trade is closed" : "the trade window is still open"));
			return FormPlanner.Reply(plan, "Close the trade with " + TraderName() + " without trading.", null, null);
		}

		// ---- small helpers -----------------------------------------------------------------------------

		private static string LabelOf(FloatMenuOption o)
		{
			return o == null ? "" : (o.Label ?? "").StripTags().Trim();
		}

		private static string Money(float value)
		{
			return value.ToString("0.##", CultureInfo.InvariantCulture);
		}

		private static string Cut(string text, int max)
		{
			if (text == null || text.Length <= max)
			{
				return text ?? "";
			}
			return text.Substring(0, max - 3).TrimEnd() + "...";
		}

		private static string Xz(IntVec3 c)
		{
			return c.x.ToString(CultureInfo.InvariantCulture) + ", " + c.z.ToString(CultureInfo.InvariantCulture);
		}
	}
}
