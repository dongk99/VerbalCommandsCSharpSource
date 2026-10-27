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
	//   show picture around SPOT.          the game's own view of the 41 by 41 cells centred on SPOT, no cell numbers
	//   show picture of AREA.              the game's own view of exactly the cells of AREA, x and z drawn on every tile
	// The mod moves the game camera there and hides the game's UI (screenshot mode, as F11 does), then at the end of every
	// rendered frame grabs the screen, cuts it to exactly the asked cells, shrinks it to MaxWidth pixels wide if wider,
	// draws the cell numbers for AREA, and writes it as one PNG, again and again until the PNG is in the folder (user,
	// 2026-10-14: "do a try reloop until a .png with expected filename appears on expected folder output", "no time
	// limit"). Every failed try writes FailName next to it with the try count and the error, so a missing picture is never
	// silent; the note is removed when the picture lands. Then the camera and screenshot mode are put back. The answer
	// names the file; the agent reads it with its own image reader. Nothing here uses the desktop, the mouse or any OCR:
	// it is the game's own frame, written by the game's own screenshot call.
	// Crop and numbers (user, 2026-10-27: "crop, hell, redraw the map from a pawn's position, so it looks identical to
	// what the agents see, *WITH* coordinate on each tile. Every single one." / "If the order is *JUST* show area around
	// x, no coordinate outputs."). Before that the picture was the whole screen with UI, and "30 by 30" came out wider.
	// Open windows block the picture (user, 2026-10-27: "I would probably have it so that show picture gets blocked if
	// any window (caravan screen, research completed window) is open.").
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
	//   Find.UIRoot.screenshotMode.Active: settable; when true the UI skips its drawing      Verse\ScreenshotModeHandler.cs:10-38
	//       (the game's own debug action sets it the same way)                              Verse\DebugActionsMapManagement.cs:707
	//   Find.Camera: the Unity camera that draws the map (WorldToScreenPoint for the crop)   Verse\Find.cs:36, Verse\Current.cs:35
	//   Find.WindowStack.Windows: every open window; ImmediateWindow is the tooltip / alert box drawn every frame
	//                                                                     Verse\WindowStack.cs:41, Verse\ImmediateWindow.cs:6
	internal static class AgentPicture
	{
		private const int AroundHalf = 20;        // same window as AgentMap "show map around": 41 by 41 cells
		private const int MaxWidth = 1024;        // pixels; about 1,200 image tokens for a 16:9 frame
		private const float Margin = 1.5f;        // cells of air round an AREA
		private const string FileName = "picture.png";
		private const string FailName = "failed to make picture_png.md";

		// 3 by 5 pixel digits 0-9, rows top to bottom, 3 bits per row (left bit first).
		private static readonly int[][] Digits =
		{
			new[] { 7, 5, 5, 5, 7 }, new[] { 2, 6, 2, 2, 7 }, new[] { 7, 1, 7, 4, 7 }, new[] { 7, 1, 7, 1, 7 },
			new[] { 5, 5, 7, 1, 1 }, new[] { 7, 4, 7, 1, 7 }, new[] { 7, 4, 7, 5, 7 }, new[] { 7, 1, 1, 1, 1 },
			new[] { 7, 5, 7, 5, 7 }, new[] { 7, 5, 7, 1, 7 }
		};

		private static bool pending;
		private static Vector3 savedPos;
		private static float savedSize;
		private static bool savedScreenshotMode;
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
			Window open = OpenWindow();
			if (open != null)
			{
				throw new Fault("a window is open (" + open.GetType().Name + "); close it first (show windows. / close window.), then ask for the picture again.");
			}

			IntVec3 centre;
			CellRect rect;
			string title;
			bool labels = form == "query picture area";
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
			// Hide the UI for the shot, as F11 does (ScreenshotModeHandler.cs:10-20); put back in Take.
			if (Find.UIRoot != null && Find.UIRoot.screenshotMode != null)
			{
				savedScreenshotMode = Find.UIRoot.screenshotMode.Active;
				Find.UIRoot.screenshotMode.Active = true;
			}
			pending = true;
			pendingPath = path;
			pendingTitle = title;
			Find.Root.StartCoroutine(Take(path, failPath, title, rect, labels));

			string look = labels
				? "The picture is the game's view of exactly those cells, with the UI hidden; every tile has its x and z drawn (x on top, z below it)."
				: "The picture is the game's view of exactly those cells, with the UI hidden; cell numbers are not drawn.";
			string text = title + " (" + rect.Width + " by " + rect.Height + " cells; north is up): " + path
				+ "\nThe file appears when the game has drawn the frame; wait for it, then read it as an image. If a try fails, " + FailName
				+ " in the same folder says why, and the tries go on until the picture is there. " + look
				+ (clipped ? "\nThe area is wider than the camera can zoom out; the middle part is in the frame." : "");
			return QueryReply.Ok(text);
		}

		// The first open window that is not a tooltip / alert box (ImmediateWindow) and not one of the mod's own.
		private static Window OpenWindow()
		{
			if (Find.WindowStack == null)
			{
				return null;
			}
			foreach (Window w in Find.WindowStack.Windows)
			{
				if (w == null || w is ImmediateWindow || w.GetType().Assembly == typeof(AgentPicture).Assembly)
				{
					continue;
				}
				return w;
			}
			return null;
		}

		// Runs on the game's root MonoBehaviour. Each turn of the loop waits for the end of a rendered frame, grabs the
		// screen, writes the PNG and checks that it is there. It ends only when the PNG is in the folder; a failed try
		// writes the failure note and the next frame tries again. No frame count, no time limit (user, 2026-10-14).
		private static IEnumerator Take(string path, string failPath, string title, CellRect rect, bool labels)
		{
			int tries = 0;
			while (true)
			{
				yield return new WaitForEndOfFrame();
				tries++;
				string error = TryWrite(path, rect, labels);
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
				if (Find.UIRoot != null && Find.UIRoot.screenshotMode != null)
				{
					Find.UIRoot.screenshotMode.Active = savedScreenshotMode;
				}
			}
			catch (Exception ex)
			{
				Log.Warning("[VerbalCommands] screenshot mode not put back: " + ex.Message);
			}
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

		// One try: grab, crop, shrink, numbers, encode, write. Returns null when every step ran, else the error text.
		// Never throws.
		private static string TryWrite(string path, CellRect rect, bool labels)
		{
			Texture2D shot = null;
			Texture2D cut = null;
			Texture2D small = null;
			try
			{
				shot = ScreenCapture.CaptureScreenshotAsTexture();
				int ox, oy;
				float sx, sy;
				cut = Crop(shot, rect, out ox, out oy, out sx, out sy);
				small = Shrink(cut, MaxWidth);
				if (labels)
				{
					float k = (float)small.width / Math.Max(1, cut.width);
					DrawLabels(small, rect, ox, oy, sx, sy, k);
				}
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
				if (small != null && small != cut && small != shot)
				{
					UnityEngine.Object.Destroy(small);
				}
				if (cut != null && cut != shot)
				{
					UnityEngine.Object.Destroy(cut);
				}
				if (shot != null)
				{
					UnityEngine.Object.Destroy(shot);
				}
			}
		}

		// Cut the frame to the pixels of the rect's cells: its corners through Find.Camera.WorldToScreenPoint. ox, oy are
		// the cut's lower-left corner in frame pixels; sx, sy turn camera pixels into frame pixels. When the camera is not
		// there or the box is empty, the whole frame is kept.
		private static Texture2D Crop(Texture2D src, CellRect rect, out int ox, out int oy, out float sx, out float sy)
		{
			ox = 0;
			oy = 0;
			sx = 1f;
			sy = 1f;
			Camera camera = Find.Camera;
			if (camera == null)
			{
				return src;
			}
			sx = (float)src.width / Math.Max(1, camera.pixelWidth);
			sy = (float)src.height / Math.Max(1, camera.pixelHeight);
			Vector3 a = camera.WorldToScreenPoint(new Vector3(rect.minX, 0f, rect.minZ));
			Vector3 b = camera.WorldToScreenPoint(new Vector3(rect.maxX + 1, 0f, rect.maxZ + 1));
			int x0 = Mathf.Clamp(Mathf.FloorToInt(Math.Min(a.x, b.x) * sx), 0, src.width);
			int x1 = Mathf.Clamp(Mathf.CeilToInt(Math.Max(a.x, b.x) * sx), 0, src.width);
			int y0 = Mathf.Clamp(Mathf.FloorToInt(Math.Min(a.y, b.y) * sy), 0, src.height);
			int y1 = Mathf.Clamp(Mathf.CeilToInt(Math.Max(a.y, b.y) * sy), 0, src.height);
			if (x1 - x0 < 2 || y1 - y0 < 2)
			{
				return src;
			}
			ox = x0;
			oy = y0;
			Color[] pixels = src.GetPixels(x0, y0, x1 - x0, y1 - y0);
			Texture2D dst = new Texture2D(x1 - x0, y1 - y0, TextureFormat.RGBA32, false);
			dst.SetPixels(pixels);
			dst.Apply(false);
			return dst;
		}

		// x over z in the top-left corner of every tile, white with a black outline. Each tile's corner comes from the
		// camera, so a cut that the screen clipped still gets the right numbers. k is the shrink factor.
		private static void DrawLabels(Texture2D tex, CellRect rect, int ox, int oy, float sx, float sy, float k)
		{
			Camera camera = Find.Camera;
			if (camera == null)
			{
				return;
			}
			Vector3 a = camera.WorldToScreenPoint(new Vector3(rect.minX, 0f, rect.minZ));
			Vector3 b = camera.WorldToScreenPoint(new Vector3(rect.minX + 1, 0f, rect.minZ + 1));
			float cellPx = Math.Min(Math.Abs(b.x - a.x) * sx, Math.Abs(b.y - a.y) * sy) * k;
			// Half the size that would fill the tile, so the tile's things stay visible (user, 2026-10-27: "you need to
			// shrink coordinate print, id say factor of 50%").
			int s = Math.Max(1, (int)((cellPx - 4f) / 22f));
			int w = tex.width;
			int h = tex.height;
			Color32[] pix = tex.GetPixels32();
			Color32 black = new Color32(0, 0, 0, 255);
			Color32 white = new Color32(255, 255, 255, 255);
			for (int pass = 0; pass < 2; pass++)
			{
				Color32 colour = pass == 0 ? black : white;
				int grow = pass == 0 ? 1 : 0;
				for (int x = rect.minX; x <= rect.maxX; x++)
				{
					for (int z = rect.minZ; z <= rect.maxZ; z++)
					{
						Vector3 p = camera.WorldToScreenPoint(new Vector3(x, 0f, z + 1));
						int left = (int)((p.x * sx - ox) * k) + 2;
						int top = (int)((p.y * sy - oy) * k) - 2;
						DrawNumber(pix, w, h, x, left, top, s, colour, grow);
						DrawNumber(pix, w, h, z, left, top - 6 * s, s, colour, grow);
					}
				}
			}
			tex.SetPixels32(pix);
			tex.Apply(false);
		}

		// One number in the 3 by 5 digits, s pixels per digit pixel; (left, top) is its top-left in texture pixels
		// (texture rows count up from the bottom). grow widens every lit pixel, for the outline pass.
		private static void DrawNumber(Color32[] pix, int w, int h, int value, int left, int top, int s, Color32 colour, int grow)
		{
			string text = value.ToString(CultureInfo.InvariantCulture);
			for (int i = 0; i < text.Length; i++)
			{
				int d = text[i] - '0';
				if (d < 0 || d > 9)
				{
					continue;
				}
				int[] rows = Digits[d];
				int dx = left + i * 4 * s;
				for (int r = 0; r < 5; r++)
				{
					for (int col = 0; col < 3; col++)
					{
						if ((rows[r] & (4 >> col)) == 0)
						{
							continue;
						}
						int px0 = dx + col * s - grow;
						int px1 = dx + col * s + s - 1 + grow;
						int py1 = top - r * s + grow;
						int py0 = top - r * s - (s - 1) - grow;
						for (int y = Math.Max(0, py0); y <= Math.Min(h - 1, py1); y++)
						{
							for (int x = Math.Max(0, px0); x <= Math.Min(w - 1, px1); x++)
							{
								pix[y * w + x] = colour;
							}
						}
					}
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
