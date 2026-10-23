using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using RimWorld;
using UnityEngine;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// Agent 02: the game clock for the router log and the agent inbox, run-then-pause, and `show speed`.
	//
	// Game facts (all from sources\RimWorldDecompiled, cited file:line):
	// - GameComponent.GameComponentTick is called once per game tick from GameComponentUtility.GameComponentTick
	//   (Verse\GameComponentUtility.cs:24-38), which DoSingleTick calls (Verse\TickManager.cs:460), after
	//   ticksGameInt++ (TickManager.cs:366): during the hook TicksGame is already the new tick.
	// - The update loop runs several ticks per frame but stops after the tick in which the game became paused:
	//   `if (Paused || ... > 45.45 ms) break;` (TickManager.cs:346), so a Pause() from inside a tick hook loses no
	//   ticks past the pause and the remaining ticks of that frame are not run.
	// - TickManager.Pause() = TogglePaused() when not paused (TickManager.cs:237-243); TogglePaused remembers the
	//   speed in prePauseTimeSpeed (:224) and resumes to it (:227-230), the same as the space bar.
	// - CurTimeSpeed set = what the speed buttons do (:187-207); it assigns curTimeSpeed, so it leaves pause.
	// - Paused = curTimeSpeed == Paused || ForcePaused (:130-140); ForcePaused = a window that pauses, a long
	//   event, the tile picker, a cutscene, landing confirmation (:142-156).
	// - TickRateMultiplier: Normal 1, Fast 3, Superfast 6 (12 when nothing is happening), Paused 0 (:74-114).
	// - GenDate.TicksPerHour = 2500 (RimWorld\GenDate.cs:33); DaysPassedAt(tick) = floor(tick / 60000) (:74-77);
	//   HourOfDay(abs, longitude) (:94-97), Year(abs, longitude) (:129-133), DateFullStringAt (:175-180);
	//   abs = TicksGame + TickManager.gameStartAbsTick (TickManager.cs:17, as GenDate.TickGameToAbs :64-67).
	// - Longitude of a map: Find.WorldGrid.LongLatOf(map.Tile) (RimWorld.Planet\WorldGrid.cs:174), as GenLocalDate
	//   does (RimWorld\GenLocalDate.cs:162-165) and FormWave1Queries.QueryWeather.
	// - UnityData.IsInMainThread (Verse\UnityData.cs:28); Current.Game is a plain static field read (Verse\Current.cs:43-48).
	public static class AgentTime
	{
		private const string LogPrefix = "[VerbalCommands] ";
		// "run until event": the longest it runs (mod constant, so the router never makes up a number).
		public const int UntilEventHours = 24;
		private const int RefreshEvery = 250;

		// ---- game-time cache ------------------------------------------------------------------------------

		private static readonly object cacheLock = new object();
		private static Game cacheGame;
		private static bool cacheValid;
		private static int cacheTick;
		private static int cacheDay;
		private static int cacheYear;
		private static int cacheHour;
		private static int cacheAbsOffset;
		private static float cacheLongitude;
		private static string cacheDate;

		// Main thread. Reads the clock into the cache. withDate also builds the translated date text.
		private static void Refresh(bool withDate)
		{
			try
			{
				Game game = Current.Game;
				if (game == null || game.tickManager == null)
				{
					return;
				}
				TickManager tm = game.tickManager;
				int tick = tm.TicksGame;
				Map map = Find.CurrentMap;
				if (map == null)
				{
					List<Map> maps = Find.Maps;
					if (maps != null)
					{
						for (int i = 0; i < maps.Count; i++)
						{
							if (maps[i].IsPlayerHome)
							{
								map = maps[i];
								break;
							}
						}
					}
				}
				Vector2 location = Vector2.zero;
				if (map != null)
				{
					location = Find.WorldGrid.LongLatOf(map.Tile);
				}
				int offset = tm.gameStartAbsTick;
				long abs = (long)tick + offset;
				int day = GenDate.DaysPassedAt(tick) + 1;
				int hour = GenDate.HourOfDay(abs, location.x);
				int year = GenDate.Year(abs, location.x);
				string date = withDate ? GenDate.DateFullStringAt(abs, location) : null;
				lock (cacheLock)
				{
					bool sameGame = cacheValid && ReferenceEquals(cacheGame, game);
					cacheGame = game;
					cacheValid = true;
					cacheTick = tick;
					cacheDay = day;
					cacheYear = year;
					cacheHour = hour;
					cacheAbsOffset = offset;
					cacheLongitude = location.x;
					cacheDate = withDate ? date : (sameGame ? cacheDate : null);
				}
				if (withDate)
				{
					lastRefreshTick = tick;
				}
			}
			catch (Exception ex)
			{
				LogOnce("clock refresh", ex);
			}
		}

		// Any thread, never throws. Adds game_tick, game_day, game_year, game_hour when the cache belongs to the
		// loaded game; otherwise nothing.
		public static void FillGameTime(JObject o)
		{
			try
			{
				if (o == null)
				{
					return;
				}
				if (Current.Game != null && UnityData.IsInMainThread)
				{
					Refresh(false);
				}
				lock (cacheLock)
				{
					if (cacheValid && cacheGame != null && ReferenceEquals(cacheGame, Current.Game))
					{
						o["game_tick"] = cacheTick;
						o["game_day"] = cacheDay;
						o["game_year"] = cacheYear;
						o["game_hour"] = cacheHour;
					}
				}
			}
			catch (Exception)
			{
			}
		}

		// "day 5 (10th of Aprimay 5500), 14h, tick 312345". Main thread (uses the cache elsewhere).
		public static string Now()
		{
			try
			{
				if (Current.Game != null && UnityData.IsInMainThread)
				{
					Refresh(true);
				}
				lock (cacheLock)
				{
					if (!cacheValid || !ReferenceEquals(cacheGame, Current.Game))
					{
						return "time unknown";
					}
					string date = string.IsNullOrEmpty(cacheDate) ? "" : " (" + cacheDate + ")";
					return "day " + cacheDay + date + ", " + cacheHour + "h, tick " + cacheTick;
				}
			}
			catch (Exception)
			{
				return "time unknown";
			}
		}

		// "day 5, 14h" for any tick of this game.
		public static string When(int ticksGame)
		{
			try
			{
				lock (cacheLock)
				{
					if (!cacheValid || !ReferenceEquals(cacheGame, Current.Game))
					{
						return "tick " + ticksGame;
					}
					long abs = (long)ticksGame + cacheAbsOffset;
					return "day " + (GenDate.DaysPassedAt(ticksGame) + 1) + ", " + GenDate.HourOfDay(abs, cacheLongitude) + "h";
				}
			}
			catch (Exception)
			{
				return "tick " + ticksGame;
			}
		}

		// ---- tick hook ------------------------------------------------------------------------------------

		private static Game lastGame;
		private static int lastRefreshTick;
		private static bool loggedOnce;

		// GameComponent_VerbalCommands.GameComponentTick, once per game tick. Never throws.
		public static void Tick()
		{
			try
			{
				Game game = Current.Game;
				if (game == null || game.tickManager == null)
				{
					return;
				}
				if (!ReferenceEquals(game, lastGame))
				{
					// A new game or a load: nothing of the old one carries over.
					lastGame = game;
					ResetRun();
					try
					{
						AgentEvents.Reset();
					}
					catch (Exception ex)
					{
						LogOnce("AgentEvents.Reset", ex);
					}
					try
					{
						AgentFollow.Reset();
					}
					catch (Exception ex)
					{
						LogOnce("AgentFollow.Reset", ex);
					}
					try
					{
						AgentPawnWatch.Reset();
					}
					catch (Exception ex)
					{
						LogOnce("AgentPawnWatch.Reset", ex);
					}
					Refresh(true);
					EnsureLog();
				}
				int tick = game.tickManager.TicksGame;
				lock (cacheLock)
				{
					if (cacheValid && ReferenceEquals(cacheGame, game))
					{
						cacheTick = tick;
					}
				}
				if (tick - lastRefreshTick >= RefreshEvery || tick < lastRefreshTick)
				{
					Refresh(true);
				}
				CheckRunEnd(game.tickManager, tick);
				try
				{
					AgentEvents.Tick();
				}
				catch (Exception ex)
				{
					LogOnce("AgentEvents.Tick", ex);
				}
				try
				{
					AgentFollow.Tick();
				}
				catch (Exception ex)
				{
					LogOnce("AgentFollow.Tick", ex);
				}
				// pawn_watch.json for pawns_watch.ps1 / hostiles_watch.ps1 (AgentPawnWatch.cs, user 2026-10-17).
				try
				{
					AgentPawnWatch.Tick();
				}
				catch (Exception ex)
				{
					LogOnce("AgentPawnWatch.Tick", ex);
				}
				try
				{
					HumanPlayLog.Tick();
				}
				catch (Exception ex)
				{
					LogOnce("HumanPlayLog.Tick", ex);
				}
			}
			catch (Exception ex)
			{
				LogOnce("Tick", ex);
			}
		}

		private static void LogOnce(string where, Exception ex)
		{
			if (loggedOnce)
			{
				return;
			}
			loggedOnce = true;
			try
			{
				Verse.Log.Error(LogPrefix + "AgentTime " + where + " failed (logged once): " + ex);
			}
			catch (Exception)
			{
			}
		}

		// ---- run, then pause ------------------------------------------------------------------------------

		private static volatile bool runActive;
		private static int runStartTick;
		private static int runEndTick;
		private static int runHours;
		private static TimeSpeed runSpeed;
		private static bool runUntilEvent;
		private static string runOrderId;
		private static string lastRunEnd;

		public static bool RunActive
		{
			get { return runActive; }
		}

		private static void ResetRun()
		{
			runActive = false;
			lastRunEnd = null;
			runOrderId = null;
		}

		private static string SpeedWord(TimeSpeed speed)
		{
			switch (speed)
			{
				case TimeSpeed.Paused:
					return "paused";
				case TimeSpeed.Normal:
					return "normal";
				case TimeSpeed.Fast:
					return "fast";
				case TimeSpeed.Superfast:
					return "superfast";
				case TimeSpeed.Ultrafast:
					return "ultrafast";
				default:
					return speed.ToString().ToLowerInvariant();
			}
		}

		private static string Hours1(double hours)
		{
			return hours.ToString("F1", CultureInfo.InvariantCulture);
		}

		private static string GameHours(int hours)
		{
			return hours + (hours == 1 ? " game hour" : " game hours");
		}

		// The game's speed to go back to when un-paused: TogglePaused (TickManager.cs:222-234).
		private static TimeSpeed ResumeSpeed(TickManager tm)
		{
			return tm.prePauseTimeSpeed != TimeSpeed.Paused ? tm.prePauseTimeSpeed : TimeSpeed.Normal;
		}

		// What the space bar does: TickManager.Pause() (TickManager.cs:237-243), which keeps the speed to resume to.
		private static void PauseGame(TickManager tm)
		{
			tm.Pause();
		}

		// Main thread, inside Write.apply of form "run".
		private static void StartRun(int hours, TimeSpeed speed, bool untilEvent)
		{
			TickManager tm = Find.TickManager;
			if (tm == null)
			{
				throw new Fault("No game is currently loaded.");
			}
			EnsureLog();
			// The same as the speed buttons; it also leaves pause (TickManager.cs:187-207). It refuses while the
			// player has no control (a gravship landing being confirmed).
			tm.CurTimeSpeed = speed;
			if (tm.CurTimeSpeed != speed)
			{
				throw new Fault("The game does not accept a speed change right now.");
			}
			if (runActive)
			{
				EndRun("Run stopped early: replaced by a new run.");
			}
			runStartTick = tm.TicksGame;
			runEndTick = runStartTick + hours * GenDate.TicksPerHour;
			runHours = hours;
			runSpeed = speed;
			runUntilEvent = untilEvent;
			string order = null;
			try
			{
				order = OrderController.CurrentOrderId;
			}
			catch (Exception)
			{
			}
			runOrderId = string.IsNullOrEmpty(order) ? null : order;
			runActive = true;
			Refresh(true);
			string text = untilEvent
				? "Run started: until something happens (at most " + GameHours(hours) + ") at " + SpeedWord(speed) + "."
				: "Run started: " + GameHours(hours) + " at " + SpeedWord(speed) + ".";
			AgentEvents.Add("run start", text, runOrderId);
		}

		// Records the end of the run (state first, then the event line).
		private static void EndRun(string text)
		{
			string order = runOrderId;
			runActive = false;
			lastRunEnd = text;
			runOrderId = null;
			try
			{
				AgentEvents.Add("run end", text, order);
			}
			catch (Exception ex)
			{
				LogOnce("run end event", ex);
			}
		}

		private static void CheckRunEnd(TickManager tm, int tick)
		{
			if (!runActive || tick < runEndTick)
			{
				return;
			}
			PauseGame(tm);
			string text = runUntilEvent
				? "Run until something happens: nothing paused it in " + GameHours(runHours) + "; paused at " + When(tick) + "."
				: "Run of " + GameHours(runHours) + " finished at " + When(tick) + ".";
			EndRun(text);
		}

		// Pauses the game now if a run is in progress (the run ends, the reason is recorded); no effect otherwise.
		// Main thread (called from AgentEvents.Tick inside the game tick).
		public static void RequestPause(string reason)
		{
			try
			{
				if (!runActive)
				{
					return;
				}
				TickManager tm = Find.TickManager;
				if (tm == null)
				{
					return;
				}
				int tick = tm.TicksGame;
				PauseGame(tm);
				double done = Math.Max(0, tick - runStartTick) / (double)GenDate.TicksPerHour;
				string text = "Run stopped early after " + Hours1(done) + " of " + runHours + " hours, at " + When(tick) + ": " + reason;
				EndRun(text);
			}
			catch (Exception ex)
			{
				LogOnce("RequestPause", ex);
			}
		}

		private static double HoursLeft(TickManager tm)
		{
			return Math.Max(0, runEndTick - tm.TicksGame) / (double)GenDate.TicksPerHour;
		}

		// "speed: superfast; run: 6.0 h left" / "speed: paused; no run". The read-back line of the run write.
		public static string StateLine()
		{
			TickManager tm = Current.Game != null ? Find.TickManager : null;
			if (tm == null)
			{
				return "no game loaded";
			}
			string run = runActive ? "run: " + Hours1(HoursLeft(tm)) + " h left" : "no run";
			return "speed: " + SpeedWord(tm.CurTimeSpeed) + "; " + run;
		}

		// ---- form "run" -----------------------------------------------------------------------------------

		private static bool TryWhole(FormContext c, string key, int min, int max, string noun, out int value)
		{
			value = 0;
			if (!c.Has(key))
			{
				return false;
			}
			double? n = c.Number(key);
			if (!n.HasValue || Math.Floor(n.Value) != n.Value || n.Value < min || n.Value > max)
			{
				throw new Fault(noun + " must be a whole number from " + min + " to " + max + ".");
			}
			value = (int)n.Value;
			return true;
		}

		public static PlanReply PlanRun(FormContext c)
		{
			if (Current.Game == null || Find.TickManager == null)
			{
				throw new Fault("No game is currently loaded.");
			}
			// An open hourly check holds the pause until every agent has voted (AgentHourlyCheck.cs, 2026-10-16).
			if (AgentHourlyCheck.IsOpen)
			{
				throw new Fault(AgentHourlyCheck.RefusalText());
			}
			int hoursSlot;
			int daysSlot;
			bool hasHours = TryWhole(c, "HOURS", 1, 120, "Hours", out hoursSlot);
			bool hasDays = TryWhole(c, "DAYS", 1, 5, "Days", out daysSlot);
			if (hasHours && hasDays)
			{
				throw new Fault("Give the length of a run in hours or in days, not both.");
			}
			int hours;
			bool untilEvent = false;
			if (hasHours)
			{
				hours = hoursSlot;
			}
			else if (hasDays)
			{
				hours = daysSlot * 24;
			}
			else
			{
				hours = UntilEventHours;
				untilEvent = true;
			}

			string speedText = FormPlanner.Norm(c.Text("SPEED") ?? "");
			TimeSpeed speed;
			switch (speedText)
			{
				case "":
				case "superfast":
				case "super fast":
				case "fastest":
				case "3":
				case "3x":
					speed = TimeSpeed.Superfast;
					break;
				case "fast":
				case "2":
				case "2x":
					speed = TimeSpeed.Fast;
					break;
				case "normal":
				case "slow":
				case "1":
				case "1x":
					speed = TimeSpeed.Normal;
					break;
				default:
					throw new Fault("A run needs a speed: normal, fast or superfast.");
			}

			string line = untilEvent
				? "Run the game at " + SpeedWord(speed) + " until something happens (at most " + GameHours(hours) + "), then pause."
				: "Run the game at " + SpeedWord(speed) + " for " + GameHours(hours) + ", then pause. It pauses earlier on a threat or emergency.";
			Plan plan = FormPlanner.NewPlan("run");
			plan.notes.Add("This window pauses the game while it is open; the speed takes hold when it closes.");
			// A window that holds the game paused (a research-complete box) keeps the run standing still: Verse\TickManager.cs:142-156.
			string holdNote = AgentWindows.HoldNote();
			if (holdNote != null)
			{
				plan.notes.Add(holdNote);
			}
			int runHoursNow = hours;
			TimeSpeed runSpeedNow = speed;
			bool runUntilNow = untilEvent;
			Write write = new Write();
			write.description = line.TrimEnd('.');
			write.apply = delegate
			{
				if (AgentHourlyCheck.IsOpen)
				{
					throw new Fault(AgentHourlyCheck.RefusalText());
				}
				StartRun(runHoursNow, runSpeedNow, runUntilNow);
			};
			write.Reads(StateLine);
			plan.writes.Add(write);
			return FormPlanner.Reply(plan, line, null, null);
		}

		// ---- form "query speed" ---------------------------------------------------------------------------

		public static QueryReply QuerySpeed(FormContext c)
		{
			if (Current.Game == null || Find.TickManager == null)
			{
				return QueryReply.Fail("No game is currently loaded.");
			}
			TickManager tm = Find.TickManager;
			List<string> lines = new List<string>();
			TimeSpeed cur = tm.CurTimeSpeed;
			if (cur == TimeSpeed.Paused)
			{
				lines.Add("Speed: paused (resumes at " + SpeedWord(ResumeSpeed(tm)) + ").");
			}
			else if (tm.ForcePaused)
			{
				lines.Add("Speed: paused while a window is open (resumes at " + SpeedWord(cur) + ").");
			}
			else
			{
				lines.Add("Speed: " + SpeedWord(cur) + ".");
			}
			lines.Add("Time: " + Now() + ".");
			if (!tm.Paused)
			{
				float mult = tm.TickRateMultiplier;
				if (mult > 0f)
				{
					double seconds = GenDate.TicksPerHour / (60.0 * mult);
					lines.Add("1 game hour takes about " + seconds.ToString(seconds >= 10 ? "F0" : "F1", CultureInfo.InvariantCulture) + " real seconds at this speed.");
				}
			}
			if (runActive)
			{
				string left = Hours1(HoursLeft(tm));
				if (tm.Paused)
				{
					lines.Add("Run: on hold while paused, " + left + " of " + runHours + " game hours left.");
				}
				else
				{
					lines.Add("Run: " + left + " of " + runHours + " game hours left (ends about " + When(runEndTick) + ").");
				}
			}
			else
			{
				lines.Add("Run: none.");
			}
			// Which window (or what else) holds the pause: TickManager.ForcePaused, Verse\TickManager.cs:142-156.
			if (tm.ForcePaused)
			{
				lines.Add(AgentWindows.HoldLine());
			}
			// The open hourly check that holds the pause (AgentHourlyCheck.cs).
			if (AgentHourlyCheck.IsOpen)
			{
				lines.Add(AgentHourlyCheck.PauseLine());
			}
			if (!string.IsNullOrEmpty(lastRunEnd))
			{
				lines.Add("Last run: " + lastRunEnd);
			}
			return QueryReply.Ok(string.Join("\n", lines.ToArray()));
		}

		// ---- router log -----------------------------------------------------------------------------------

		private static bool loggedLogFailure;

		// Main thread. Configures the router log once if it is not on yet, exactly as OrderController.ConfigureRouterLog
		// does (OrderController.cs:189-200): saveRouterLog setting, haiku prompt, router model file.
		public static void EnsureLog()
		{
			try
			{
				if (RouterLog.Configured)
				{
					return;
				}
				VerbalCommandsSettings settings = VerbalCommandsMod.Settings;
				bool save = settings != null && settings.saveRouterLog;
				RouterLog.Configure(save || RouterLog.ForceOn, Path.Combine(VerbalCommandsMod.ModRootDir ?? "", "Router", "haiku_prompt.md"), Router.RouterModelFilePath());
			}
			catch (Exception ex)
			{
				if (!loggedLogFailure)
				{
					loggedLogFailure = true;
					Verse.Log.Warning(LogPrefix + "router log: could not start: " + ex.Message);
				}
			}
		}
	}
}
