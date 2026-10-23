using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RimWorld;
using UnityEngine;
using VerbalCommands.Bridge;
using Verse;

namespace VerbalCommands
{
	// A picture of the colony for an agent that plays with no screen (user, 2026-10-14: "i need to have a cheap way where
	// the orchestrator sees screenshot of the colony *WITHOUT* using computer-use." / "Or area. based on its center.").
	// Two questions:
	//   show picture around SPOT.          the game's own view, centred on SPOT, 41 by 41 cells like "show map around"
	//   show picture of AREA.              centred on the middle of AREA, zoomed so the whole area is in the frame
	// The mod moves the game camera there, then at the end of every rendered frame grabs the screen as the player would
	// see it (UI included), shrinks it to MaxWidth pixels wide and writes it as one PNG, again and again until the PNG is
	// in the folder (user, 2026-10-14: "do a try reloop until a .png with expected filename appears on expected folder
	// output", "no time limit"). Every failed try writes FailName next to it with the try count and the error, so a
	// missing picture is never silent; the note is removed when the picture lands. Then the camera is put back. The
	// answer names the file; the agent reads it with its own image reader. Nothing here uses the desktop, the mouse or
	// any OCR: it is the game's own frame, written by the game's own screenshot call.
	//
	// Game facts relied on (decompiled tree sources\RimWorldDecompiled\, line numbers of that tree):
	//   CameraDriver.SetRootPosAndSize(rootPos, rootSize): sets the camera's cell and zoom at once   Verse\CameraDriver.cs:560-568
	//   CameraDriver.RootSize is the orthographic half-height in cells; the view is RootSize*aspect wide to each side
	//       (CurrentViewRect: minX = x - RootSize*aspect - 1)                                        Verse\CameraDriver.cs:205-215
	//   CameraDriver.MapPosition: the cell under the camera now                                        Verse\CameraDriver.cs:180-187
	//   config.sizeRange: the zoom limits the player has                                               Verse\CameraDriver.cs:324
	//   The map is drawn only inside the camera's view rect, so a second camera elsewhere would see nothing: hence
	//       moving the real camera                                                                     Verse\MapDrawer.cs (DrawMapMesh)
	//   Find.Root is the game's MonoBehaviour (Current.Root), so it can host a coroutine                 Verse\Find.cs:14, Verse\Root.cs:14
	//   WaitForEndOfFrame yields after the frame is rendered, so the grab sees the moved camera           UnityEngine
	//   UnityEngine.ScreenCapture.CaptureScreenshotAsTexture: the frame as rendered (ScreenCaptureModule);
	//       ImageConversion.EncodeToPNG (ImageConversionModule). Both DLLs ship in RimWorldWin64_Data\Managed.
	internal static class AgentPicture
	{
		private const int AroundHalf = 20;        // same window as AgentMap "show map around": 41 by 41 cells
		private const int MaxWidth = 1024;        // pixels; about 1,200 image tokens for a 16:9 frame
		private const float Margin = 1.5f;        // cells of air round an AREA
		private const string FileName = "picture.png";
		private const string FailName = "failed to make picture_png.md";

		private static bool pending;
		private static Vector3 savedPos;
		private static float savedSize;
		private static string pendingPath;
		private static string pendingTitle;

		public static QueryReply Query(FormContext c)
		{
			if (c == null)
			{
				return null;
			}
			string form = c.form;
			if (form != "query picture around" && form != "query picture area")
			{
				return null;
			}
			Map map = c.map;
			if (map == null)
			{
				throw new Fault("there is no map to look at right now.");
			}
			if (Find.CurrentMap != map)
			{
				throw new Fault("that map is not the one on screen; a picture shows only the map the camera is on.");
			}
			if (pending)
			{
				throw new Fault("a picture is still being taken; ask again in a moment.");
			}
			CameraDriver cam = Find.CameraDriver;
			if (cam == null)
			{
				throw new Fault("the game camera is not available right now.");
			}
			if (Find.Root == null)
			{
				throw new Fault("the game root is not available right now.");
			}

			IntVec3 centre;
			CellRect rect;
			string title;
			if (form == "query picture around")
			{
				string spotText = c.Has("SPOT") ? c.Text("SPOT") : null;
				if (string.IsNullOrWhiteSpace(spotText))
				{
					throw new Fault("no place was given for the picture.");
				}
				string reason;
				if (!AgentPlace.TryCell(map, spotText, out centre, out reason))
				{
					throw new Fault(reason ?? "that place could not be read.");
				}
				rect = new CellRect(centre.x - AroundHalf, centre.z - AroundHalf, 2 * AroundHalf + 1, 2 * AroundHalf + 1).ClipInsideMap(map);
				title = "Picture around " + spotText.Trim() + " = " + AgentPlace.Describe(map, centre);
			}
			else
			{
				string areaText = c.Has("AREA") ? c.Text("AREA") : null;
				if (string.IsNullOrWhiteSpace(areaText))
				{
					throw new Fault("no area was given for the picture.");
				}
				string why;
				if (!AgentPlace.TryRect(map, areaText, out rect, out why))
				{
					throw new Fault(why ?? "that area could not be read.");
				}
				rect = rect.ClipInsideMap(map);
				centre = new IntVec3(rect.minX + (rect.maxX - rect.minX) / 2, 0, rect.minZ + (rect.maxZ - rect.minZ) / 2);
				title = "Picture of " + AgentPlace.DescribeRect(map, rect) + ", centred on " + AgentPlace.Describe(map, centre);
			}

			// Zoom so the whole rect fits: half-height = RootSize, half-width = RootSize * aspect (CameraDriver.cs:205-215).
			float aspect = (float)UI.screenWidth / Math.Max(1, UI.screenHeight);
			float halfH = rect.Height / 2f + Margin;
			float halfW = rect.Width / 2f + Margin;
			float size = Math.Max(halfH, halfW / aspect);
			FloatRange range = cam.config.sizeRange;
			bool clipped = size > range.max;
			size = Mathf.Clamp(size, range.min, range.max);

			string path = PicturePath();
			string failPath = Path.Combine(Path.GetDirectoryName(path), FailName);
			// An old picture or failure note must not pass for this one.
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			if (File.Exists(path))
			{
				File.Delete(path);
			}
			if (File.Exists(failPath))
			{
				File.Delete(failPath);
			}
			savedPos = cam.MapPosition.ToVector3Shifted();
			savedSize = cam.RootSize;
			Vector3 target = centre.ToVector3Shifted();
			cam.SetRootPosAndSize(new Vector3(target.x, savedPos.y, target.z), size);
			pending = true;
			pendingPath = path;
			pendingTitle = title;
			Find.Root.StartCoroutine(Take(path, failPath, title));

			string text = title + " (" + rect.Width + " by " + rect.Height + " cells; north is up): " + path
				+ "\nThe file appears when the game has drawn the frame; wait for it, then read it as an image. If a try fails, " + FailName
				+ " in the same folder says why, and the tries go on until the picture is there. The screen shows the game's view with its UI; cell numbers are not drawn."
				+ (clipped ? "\nThe area is wider than the camera can zoom out; the middle part is in the frame." : "");
			return QueryReply.Ok(text);
		}

		// Runs on the game's root MonoBehaviour. Each turn of the loop waits for the end of a rendered frame, grabs the
		// screen, writes the PNG and checks that it is there. It ends only when the PNG is in the folder; a failed try
		// writes the failure note and the next frame tries again. No frame count, no time limit (user, 2026-10-14).
		private static IEnumerator Take(string path, string failPath, string title)
		{
			int tries = 0;
			while (true)
			{
				yield return new WaitForEndOfFrame();
				tries++;
				string error = TryWrite(path);
				if (error == null && File.Exists(path) && new FileInfo(path).Length > 0)
				{
					break;
				}
				WriteFail(failPath, title, tries, error ?? "the write raised nothing, but " + FileName + " is not in the folder");
			}
			try
			{
				if (File.Exists(failPath))
				{
					File.Delete(failPath);
				}
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] picture: failure note not removed: " + ex.Message);
			}
			pending = false;
			try
			{
				CameraDriver cam = Find.CameraDriver;
				if (cam != null)
				{
					cam.SetRootPosAndSize(savedPos, savedSize);
				}
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] camera not put back: " + ex.Message);
			}
		}

		// One try: grab, shrink, encode, write. Returns null when every step ran, else the error text. Never throws.
		private static string TryWrite(string path)
		{
			Texture2D shot = null;
			Texture2D small = null;
			try
			{
				shot = ScreenCapture.CaptureScreenshotAsTexture();
				small = Shrink(shot, MaxWidth);
				byte[] png = ImageConversion.EncodeToPNG(small);
				if (png == null || png.Length == 0)
				{
					return "EncodeToPNG gave no bytes";
				}
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				File.WriteAllBytes(path, png);
				return null;
			}
			catch (Exception ex)
			{
				return ex.GetType().Name + ": " + ex.Message;
			}
			finally
			{
				if (shot != null)
				{
					UnityEngine.Object.Destroy(shot);
				}
				if (small != null && small != shot)
				{
					UnityEngine.Object.Destroy(small);
				}
			}
		}

		private static void WriteFail(string failPath, string title, int tries, string error)
		{
			try
			{
				string text = "# failed to make " + FileName + "\n\n" + title + "\n\ntries so far: " + tries
					+ "\nlast error: " + error + "\n\nThe mod keeps trying every frame until the picture is there; this note is removed then.\n";
				File.WriteAllText(failPath, text);
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] picture: failure note not written: " + ex.Message);
			}
			if (tries == 1)
			{
				Log.Warning("[VerbalCommands] picture try failed: " + error);
			}
		}

		// Nearest-neighbour shrink to at most maxWidth pixels wide (the frame is never enlarged).
		private static Texture2D Shrink(Texture2D src, int maxWidth)
		{
			if (src.width <= maxWidth)
			{
				return src;
			}
			float scale = (float)maxWidth / src.width;
			int w = maxWidth;
			int h = Math.Max(1, Mathf.RoundToInt(src.height * scale));
			Color32[] from = src.GetPixels32();
			Color32[] to = new Color32[w * h];
			for (int y = 0; y < h; y++)
			{
				int sy = Math.Min(src.height - 1, (int)(y / scale));
				for (int x = 0; x < w; x++)
				{
					int sx = Math.Min(src.width - 1, (int)(x / scale));
					to[y * w + x] = from[sy * src.width + sx];
				}
			}
			Texture2D dst = new Texture2D(w, h, TextureFormat.RGBA32, false);
			dst.SetPixels32(to);
			dst.Apply(false);
			return dst;
		}

		// The agent's outbox folder when the inbox is on (the agent already reads there), else the save-data folder.
		private static string PicturePath()
		{
			string dir = AgentInbox.Enabled ? AgentInbox.OutboxDir : null;
			if (string.IsNullOrEmpty(dir))
			{
				dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "VerbalCommands");
			}
			return Path.Combine(dir, FileName);
		}
	}
}
