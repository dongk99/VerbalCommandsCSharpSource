using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Verse;

namespace VerbalCommands
{
	// Every timed reminder scan and every delivery, in a markdown file (user, 2026-10-14: "scan reminders and tip firing
	// and open open an .md for every 6hr/3hr tips being tossed into there."). One file per game launch, next to the
	// router logs: <SaveDataFolderPath>\VerbalCommands\logs\reminders_<yyyy-MM-dd_HHmmss>.md (RouterLog.cs:75, :311 name
	// theirs the same way); the newest KeepFiles are kept. Written by AgentReminderScan on the main thread: a scan
	// (6-hour, 3-hour grow, daily door, mental break) as it is made, empty ones too, and a delivery when TakeFor hands a
	// batch to a result. Never throws: a failed write is dropped.
	internal static class AgentReminderLog
	{
		private const int KeepFiles = 20;
		private static string filePath;
		private static bool tried;

		public static void Scan(string title, string when, List<string> lines)
		{
			StringBuilder sb = new StringBuilder();
			sb.Append("\n## ").Append(title).Append(string.IsNullOrEmpty(when) ? "" : " - " + when)
				.Append(" (made ").Append(DateTime.Now.ToString("HH:mm:ss")).Append(")\n");
			if (lines == null || lines.Count == 0)
			{
				sb.Append("No reminders.\n");
			}
			else
			{
				for (int i = 0; i < lines.Count; i++)
				{
					string line = (lines[i] ?? "").Replace("\n", " ");
					if (line.StartsWith("[REMINDER] ", StringComparison.Ordinal))
					{
						line = line.Substring("[REMINDER] ".Length);
					}
					sb.Append(i + 1).Append(". ").Append(line).Append('\n');
				}
			}
			Append(sb.ToString());
		}

		public static void Delivered(string head, string forms)
		{
			Append("\n**Delivered** " + DateTime.Now.ToString("HH:mm:ss") + " with the result of: " + forms + ". " + (head ?? "").Replace("\n", " ") + "\n");
		}

		private static void Append(string text)
		{
			try
			{
				if (!tried)
				{
					tried = true;
					string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "VerbalCommands", "logs");
					Directory.CreateDirectory(dir);
					string[] old = Directory.GetFiles(dir, "reminders_*.md");
					Array.Sort(old, StringComparer.OrdinalIgnoreCase);
					for (int i = 0; i < old.Length - (KeepFiles - 1); i++)
					{
						try
						{
							File.Delete(old[i]);
						}
						catch (Exception)
						{
						}
					}
					filePath = Path.Combine(dir, "reminders_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".md");
					File.WriteAllText(filePath, "# Orchestrator reminders, game launch " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\n\n"
						+ "Every timed scan (6-hour check, 3-hour grow check, daily door lines, mental breaks) as it is made, and each"
						+ " delivery to the orchestrator.\n", new UTF8Encoding(false));
				}
				if (filePath != null)
				{
					File.AppendAllText(filePath, text, new UTF8Encoding(false));
				}
			}
			catch (Exception)
			{
				filePath = null;
			}
		}
	}
}
