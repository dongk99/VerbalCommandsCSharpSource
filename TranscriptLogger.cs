using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Verse;

namespace VerbalCommands
{
	// Opt-in per-Send transcript log (brief 11 section C). The mod never persisted what the player
	// sends until now; this is off by default (VerbalCommandsSettings.saveTranscripts).
	public static class TranscriptLogger
	{
		private const string LogPrefix = "[VerbalCommands] transcript: ";
		private static bool loggedFailureOnce;

		// File I/O goes off the main thread (brief 11 section C) and is also wrapped in try/catch, so
		// a failure can never block Send.
		public static void AppendAsync(bool voice, string voiceRaw, string sentText, string sttModel)
		{
			Task.Run(delegate
			{
				try
				{
					Append(voice, voiceRaw, sentText, sttModel);
				}
				catch (Exception ex)
				{
					if (!loggedFailureOnce)
					{
						loggedFailureOnce = true;
						Log.Error(LogPrefix + "failed to write transcripts.jsonl: " + ex);
					}
				}
			});
		}

		private static void Append(bool voice, string voiceRaw, string sentText, string sttModel)
		{
			// GenFilePaths.SaveDataFolderPath (sources/RimWorldDecompiled/Verse/GenFilePaths.cs:93-101)
			// is the "RimWorld by Ludeon Studios" folder itself. GenFilePaths.ConfigFolderPath (:179)
			// is FolderUnderSaveData("Config") - a SIBLING subfolder of SaveDataFolderPath, not an
			// ancestor of it - so Path.Combine(SaveDataFolderPath, "VerbalCommands", ...) never lands
			// inside Config and this method never reads or writes anything under Config.
			string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "VerbalCommands");
			Directory.CreateDirectory(dir);
			string path = Path.Combine(dir, "transcripts.jsonl");

			string voiceRawSafe = voiceRaw ?? "";
			bool edited = voice && sentText.Trim() != voiceRawSafe.Trim();

			JObject o = new JObject
			{
				["utc"] = DateTime.UtcNow.ToString("o"),
				["voice"] = voice,
				["voice_raw"] = voiceRawSafe,
				["sent_text"] = sentText,
				["edited"] = edited,
				["stt_model"] = voice ? sttModel : null
			};

			File.AppendAllText(path, o.ToString(Formatting.None) + "\n", Encoding.UTF8);
		}
	}
}
