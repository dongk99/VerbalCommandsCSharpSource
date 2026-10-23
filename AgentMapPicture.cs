﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace VerbalCommands
{
	// The whole map as one drawn picture, the answer to "list mountains on map." (user, 2026-10-19: "if they say "list
	// mountains on map", then the game just takes picture of the entire map instead." / "you dont need entire goddamn
	// cell by cell of mountain. humans dont play like this"). Not a screenshot: the game camera zooms out to 60 cells
	// from its centre at most (Verse\CameraMapConfig.cs:20, sizeRange 11 to 60), so a 250 by 250 map does not fit
	// (user's pick, option B: the mod draws its own map image). Asked whether it shows geysers and pawns: colonists and
	// hostiles are drawn, steam geysers and ship chunks too.
	// Snow (user, 2026-10-19: "that vastly undersells what the map actually is, because it's fucking arctic"): open
	// ground is drawn whiter the deeper its snow (Verse\SnowGrid.cs:148 GetDepth, read live), as the save renderer of
	// 2026-09-26 (base-map\render_png.py) did from the save's snow layer.
	// Labels (user: "try to fix that name overlap issue because that looks like a great way to models to hallucinate"):
	// every mark carries a short tag (P1.. colonists, H1.. hostiles, G1.. steam geysers, C1.. ship chunks; G and C are the
	// numbers of the landmark names, AgentPlace.Numbered); the text answer names each tag with its cell. A tag goes in the
	// first free place next to its mark, never over another tag or mark.
	// One cell = scale pixels; north is up. Fogged cells are black: nothing under fog is shown. Main thread (Texture2D).
	internal static class AgentMapPicture
	{
		private const int MaxPixels = 1024;   // the longer side; about 1,200 image tokens at most, as AgentPicture
		private const int CharW = 6;          // a 5 by 7 letter and one pixel of space (smaller tags, user 2026-10-19:
		private const int LineH = 9;          // "make labels smaller than what currently are"; 3 by 5 drawn double before)
		private const int DotSize = 7;        // pixels; a mark was 3 by 3 cells (12 pixels) before
		private const string FileName = "map.png";

		private static readonly Color32 Fog = new Color32(0, 0, 0, 255);
		private static readonly Color32 Rock = new Color32(125, 125, 125, 255);
		private static readonly Color32 RockUnderMountain = new Color32(85, 85, 85, 255);
		private static readonly Color32 OtherOre = new Color32(200, 60, 200, 255);
		private static readonly Color32 Ours = new Color32(110, 70, 40, 255);
		private static readonly Color32 OtherBuilding = new Color32(150, 110, 150, 255);
		private static readonly Color32 Water = new Color32(40, 80, 170, 255);
		private static readonly Color32 Ice = new Color32(165, 195, 215, 255);
		private static readonly Color32 Floor = new Color32(190, 160, 110, 255);
		private static readonly Color32 Soil = new Color32(95, 100, 60, 255);
		private static readonly Color32 Barren = new Color32(120, 112, 100, 255);
		private static readonly Color32 Snow = new Color32(238, 241, 245, 255);
		private static readonly Color32 Colonist = new Color32(0, 200, 0, 255);
		private static readonly Color32 Hostile = new Color32(230, 0, 0, 255);
		private static readonly Color32 Geyser = new Color32(255, 140, 0, 255);
		private static readonly Color32 Chunk = new Color32(230, 200, 0, 255);
		private static readonly Color32 LabelText = new Color32(255, 255, 255, 255);
		private static readonly Color32 LabelBack = new Color32(0, 0, 0, 255);
		private static readonly Color32 WalkableBuilding = new Color32(230, 90, 150, 255);   // rose
		private static readonly Color32 Stockpile = new Color32(110, 70, 220, 255);          // indigo
		private static readonly Color32 OurAnimal = new Color32(0, 150, 255, 255);           // azure, tagged A
		private static readonly Color32 WildAnimal = new Color32(25, 25, 100, 255);          // navy, 3 pixels, no tag

		// Ore by the thing it is mined into (BuildingProperties.mineableThing).
		private static readonly Dictionary<string, Color32> OreColour = new Dictionary<string, Color32>
		{
			{ "Steel", new Color32(230, 120, 40, 255) },
			{ "ComponentIndustrial", new Color32(240, 220, 0, 255) },
			{ "Silver", new Color32(250, 250, 250, 255) },
			{ "Gold", new Color32(255, 200, 60, 255) },
			{ "Plasteel", new Color32(0, 220, 230, 255) },
			{ "Uranium", new Color32(120, 255, 120, 255) },
			{ "Jade", new Color32(0, 160, 110, 255) },
		};

		private sealed class Box
		{
			public int x, y, w, h;
			public Box(int x, int y, int w, int h) { this.x = x; this.y = y; this.w = w; this.h = h; }
			// 2 pixels of air count as part of the box: two tags must not touch ("P52" and "S352" read as "P52S352", test 4).
			public bool Hits(Box o) { return x - 2 < o.x + o.w && o.x < x + w + 2 && y - 2 < o.y + o.h && o.y < y + h + 2; }
		}

		private sealed class Mark
		{
			public IntVec3 cell;
			public Color32 colour;
			public string tag;
			public string legend;
			public Box box;
		}

		public static string Make(Map map)
		{
			Stopwatch watch = Stopwatch.StartNew();
			int w = map.Size.x;
			int h = map.Size.z;
			int scale = Math.Max(1, Math.Min(4, MaxPixels / Math.Max(w, h)));
			int pw = w * scale;
			int ph = h * scale;
			Color32[] px = new Color32[pw * ph];

			for (int z = 0; z < h; z++)
			{
				for (int x = 0; x < w; x++)
				{
					Fill(px, pw, ph, x * scale, z * scale, scale, scale, CellColour(map, new IntVec3(x, 0, z)));
				}
			}

			List<Mark> marks = new List<Mark>();
			int n = 0;
			foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.ToList())
			{
				n++;
				marks.Add(NewMark(p.Position, Colonist, "P" + n, p.LabelShort));
			}
			n = 0;
			foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
			{
				if (p == null || p.Dead || p.Faction == Faction.OfPlayer || !AgentPlace.Visible(map, p.Position) || !p.HostileTo(Faction.OfPlayer))
				{
					continue;
				}
				n++;
				marks.Add(NewMark(p.Position, Hostile, "H" + n, p.LabelShort + (p.Downed ? " (down)" : "")));
			}
			// Animals (user, 2026-10-19: "Yes" to "your animals tagged, wild ones as untagged dots"): your animals are marks
			// A1.. with name and kind in the text; wild ones (not hostile: a hostile one is an H) are small untagged dots,
			// counted in the text.
			n = 0;
			int wild = 0;
			foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
			{
				if (p == null || p.Dead || p.RaceProps == null || !p.RaceProps.Animal || !AgentPlace.Visible(map, p.Position))
				{
					continue;
				}
				if (p.Faction == Faction.OfPlayer)
				{
					n++;
					marks.Add(NewMark(p.Position, OurAnimal, "A" + n, p.LabelShort + (p.Name != null ? " (" + p.KindLabel + ")" : "")));
				}
				else if (p.Faction == null && !p.HostileTo(Faction.OfPlayer))
				{
					wild++;
					Fill(px, pw, ph, p.Position.x * scale + scale / 2 - 1, p.Position.z * scale + scale / 2 - 1, 3, 3, WildAnimal);
				}
			}
			AddNumbered(map, marks, ThingDefOf.SteamGeyser, "steam geyser", "G", Geyser);
			AddNumbered(map, marks, ThingDefOf.ShipChunk, "ship chunk", "C", Chunk);

			// The marks, then one tag per mark, or per group: marks within 2 cells of each other are a group (user,
			// 2026-10-19: "whenever labels start overlapping, make the software print on some other area (incl. coordinate
			// of overlap) on what individual labels have."). A group gets the tag K1.. on the map, and the text
			// says which marks it holds and where.
			List<Box> taken = new List<Box>();
			foreach (Mark m in marks)
			{
				m.box = new Box(m.cell.x * scale + scale / 2 - DotSize / 2, m.cell.z * scale + scale / 2 - DotSize / 2, DotSize, DotSize);
				Fill(px, pw, ph, m.box.x, m.box.y, m.box.w, m.box.h, m.colour);
				taken.Add(m.box);
			}
			List<List<Mark>> groups = Groups(marks);
			List<string> groupText = new List<string>();
			int k = 0;
			foreach (List<Mark> g in groups)
			{
				if (g.Count == 1)
				{
					Box tag = PlaceTag(g[0].box, g[0].tag, pw, ph, taken);
					taken.Add(tag);
					DrawText(px, pw, ph, tag.x, tag.y, g[0].tag, true, 1);
					continue;
				}
				k++;
				int x0 = g.Min((Mark m) => m.box.x) - 1;
				int y0 = g.Min((Mark m) => m.box.y) - 1;
				int x1 = g.Max((Mark m) => m.box.x + m.box.w) + 1;
				int y1 = g.Max((Mark m) => m.box.y + m.box.h) + 1;
				Box around = new Box(x0, y0, x1 - x0, y1 - y0);
				Outline(px, pw, ph, around, LabelText);
				taken.Add(around);
				string name = "K" + k;
				Box gtag = PlaceTag(around, name, pw, ph, taken);
				taken.Add(gtag);
				DrawText(px, pw, ph, gtag.x, gtag.y, name, true, 1);
				int cx = (int)Math.Round(g.Average((Mark m) => (double)m.cell.x));
				int cz = (int)Math.Round(g.Average((Mark m) => (double)m.cell.z));
				string members = string.Join(" ", g.Select((Mark m) => m.tag).ToArray());
				groupText.Add(name + " at " + cx.ToString(CultureInfo.InvariantCulture) + "," + cz.ToString(CultureInfo.InvariantCulture)
					+ " holds " + members);
			}

			// Stockpile zones: the tag S + the zone's ID on its middle cell (the zone cell nearest its centre), or next to it
			// when that place is taken; the text names each one with its cell and size.
			List<string> zoneText = new List<string>();
			foreach (Zone_Stockpile zone in map.zoneManager.AllZones.OfType<Zone_Stockpile>().OrderBy((Zone_Stockpile z) => z.ID).ToList())
			{
				List<IntVec3> cells = zone.Cells.Where((IntVec3 c) => AgentPlace.Visible(map, c)).ToList();
				if (cells.Count == 0)
				{
					continue;
				}
				double ax = cells.Average((IntVec3 c) => (double)c.x);
				double az = cells.Average((IntVec3 c) => (double)c.z);
				IntVec3 mid = cells.OrderBy((IntVec3 c) => (c.x - ax) * (c.x - ax) + (c.z - az) * (c.z - az)).First();
				string ztag = "S" + zone.ID.ToString(CultureInfo.InvariantCulture);
				int tw = ztag.Length * CharW + 1;
				Box zb = new Box(mid.x * scale + scale / 2 - tw / 2, mid.z * scale + scale / 2 - LineH / 2, tw, LineH);
				if (taken.Any((Box o) => o.Hits(zb)) || zb.x < 0 || zb.y < 0 || zb.x + zb.w > pw || zb.y + zb.h > ph)
				{
					zb = PlaceTag(new Box(mid.x * scale, mid.z * scale, scale, scale), ztag, pw, ph, taken);
				}
				taken.Add(zb);
				DrawText(px, pw, ph, zb.x, zb.y, ztag, true, 1);
				zoneText.Add(ztag + " " + zone.label + " " + mid.x.ToString(CultureInfo.InvariantCulture) + ","
					+ mid.z.ToString(CultureInfo.InvariantCulture) + " (" + cells.Count.ToString(CultureInfo.InvariantCulture) + " cells)");
			}

			// No strip under the map any more (user, 2026-10-19: "you might as well get rid of that overlap image on bottom.");
			// the text answer names every group's members.
			int fullH = ph;
			Color32[] full = px;

			string path = PicturePath();
			Texture2D tex = new Texture2D(pw, fullH, TextureFormat.RGBA32, false);
			try
			{
				tex.SetPixels32(full);
				tex.Apply(false);
				byte[] png = ImageConversion.EncodeToPNG(tex);
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				File.WriteAllBytes(path, png);
			}
			finally
			{
				UnityEngine.Object.Destroy(tex);
			}
			watch.Stop();
			Verse.Log.Message("[VerbalCommands] map picture: " + w + " by " + h + " cells, " + pw + " by " + fullH + " pixels, "
				+ marks.Count + " marks, " + k + " groups, " + watch.ElapsedMilliseconds + " ms");

			// The game pauses, with the user's words (2026-10-19: "whenever models run this command, the game autopauses by
			// saying "I see you ran this command, so read the {filename, filename} (add filename there on {}) that comes with
			// it, and unpause when done reading.""); the file name goes where the braces were.
			string readLine = "I see you ran this command, so read the " + Path.GetFileName(path)
				+ " that comes with it, and unpause when done reading.";
			AgentTime.RequestPause(readLine);
			TickManager tm = Find.TickManager;
			if (tm != null && tm.CurTimeSpeed != TimeSpeed.Paused)
			{
				tm.Pause();
			}

			List<string> lines = new List<string>();
			lines.Add(readLine);
			lines.Add("Map of the whole map (" + w + " by " + h + " cells, " + scale + " pixels per cell, north is up):");
			lines.Add(path);
			Pack(lines, "Read it as an image. Ground: white-grey snow (whiter = deeper), olive soil, grey-brown gravel or sand, pale"
				+ " blue ice, blue water, tan floors. Rock: grey, dark grey under overhead mountain. Ore: orange steel, yellow"
				+ " compacted machinery, white silver, gold gold, cyan plasteel, light green uranium, dark green-blue jade, purple"
				+ " other. Dark brown: your walls and anything else nobody can walk through; violet: others'. Rose: buildings"
				+ " you can walk through (doors, beds, benches, shelves, turrets, ...). Indigo: stockpile zones, tagged S and"
				+ " the zone's ID. Black: fog.", " ");
			Pack(lines, "Marks, each with its tag next to it: green P = colonist, azure A = your animal, red H = hostile, orange G ="
				+ " steam geyser, yellow C = ship chunk. Small navy dots without a tag: wild animals ("
				+ wild.ToString(CultureInfo.InvariantCulture) + "). Marks within 2 cells of each other share one tag K1.. in a"
				+ " white box; the list below says what each K holds. Tags:", " ");
			Pack(lines, string.Join(" | ", marks.Select((Mark m) => m.legend).ToArray()), " | ");
			if (groupText.Count > 0)
			{
				Pack(lines, string.Join(" | ", groupText.ToArray()), " | ");
			}
			if (zoneText.Count > 0)
			{
				Pack(lines, "Stockpiles: " + string.Join(" | ", zoneText.ToArray()), " | ");
			}
			string text = string.Join("\n", lines.ToArray());
			return text;
		}

		// Marks within 2 cells of each other (either way), joined through chains.
		private static List<List<Mark>> Groups(List<Mark> marks)
		{
			int[] parent = Enumerable.Range(0, marks.Count).ToArray();
			Func<int, int> find = null;
			find = (int i) => parent[i] == i ? i : (parent[i] = find(parent[i]));
			for (int i = 0; i < marks.Count; i++)
			{
				for (int j = i + 1; j < marks.Count; j++)
				{
					if (Math.Abs(marks[i].cell.x - marks[j].cell.x) <= 2 && Math.Abs(marks[i].cell.z - marks[j].cell.z) <= 2)
					{
						parent[find(i)] = find(j);
					}
				}
			}
			List<List<Mark>> groups = new List<List<Mark>>();
			Dictionary<int, List<Mark>> byRoot = new Dictionary<int, List<Mark>>();
			for (int i = 0; i < marks.Count; i++)
			{
				int r = find(i);
				List<Mark> g;
				if (!byRoot.TryGetValue(r, out g))
				{
					g = new List<Mark>();
					byRoot[r] = g;
					groups.Add(g);
				}
				g.Add(marks[i]);
			}
			return groups;
		}

		// Lines of at most 100 characters, broken only at SEP.
		private static void Pack(List<string> lines, string text, string sep)
		{
			string line = "";
			foreach (string piece in text.Split(new[] { sep }, StringSplitOptions.None))
			{
				if (line.Length > 0 && line.Length + sep.Length + piece.Length > 100)
				{
					lines.Add(line);
					line = "";
				}
				line = line.Length == 0 ? piece : line + sep + piece;
			}
			if (line.Length > 0)
			{
				lines.Add(line);
			}
		}

		private static Mark NewMark(IntVec3 cell, Color32 colour, string tag, string name)
		{
			Mark m = new Mark();
			m.cell = cell;
			m.colour = colour;
			m.tag = tag;
			m.legend = tag + " " + name + " " + cell.x.ToString(CultureInfo.InvariantCulture) + ","
				+ cell.z.ToString(CultureInfo.InvariantCulture);
			return m;
		}

		private static void AddNumbered(Map map, List<Mark> marks, ThingDef def, string prefix, string tag, Color32 colour)
		{
			foreach (KeyValuePair<string, IntVec3> e in AgentPlace.Numbered(map, def, prefix))
			{
				marks.Add(NewMark(e.Value, colour, tag + e.Key.Substring(prefix.Length).Trim(), e.Key));
			}
		}

		private static Color32 CellColour(Map map, IntVec3 c)
		{
			if (map.fogGrid.IsFogged(c))
			{
				return Fog;
			}
			Building b = c.GetEdifice(map);
			if (b != null && b.def != null && b.def.building != null && b.def.building.isNaturalRock)
			{
				if (b.def.building.isResourceRock)
				{
					ThingDef mined = b.def.building.mineableThing;
					Color32 ore;
					return mined != null && OreColour.TryGetValue(mined.defName, out ore) ? ore : OtherOre;
				}
				RoofDef roof = map.roofGrid.RoofAt(c);
				return roof != null && roof.isThickRoof ? RockUnderMountain : Rock;
			}
			// Only what nobody can walk through is drawn as a building (walls, ...); a door, bed, chair, turret or anything
			// else walkable shows the floor or ground under it (user, 2026-10-19: "it fucking blobs interior space that could
			// be technically walkable." / "Doors are passable, fix it.").
			if (b != null && b.def.passability == Traversability.Impassable)
			{
				return b.Faction == Faction.OfPlayer ? Ours : OtherBuilding;
			}
			// Walkable buildings and stockpile zones each in a colour of their own, apart from the rest of the palette
			// (user, 2026-10-19: "Stockpile zones get their own color & label (based on ingame id). Buildings get their own
			// color, but not label. both must contract from current color palette"): rose and indigo, the two hues
			// nothing else here uses. Snow is not drawn over a stockpile, so it stays visible. A stockpile is drawn over the
			// shelves or anything else on it, so the whole zone shows (it was only its empty cells, 2026-10-19 test 4). A door
			// is drawn as the floor under it, a gap in the wall (user: "you just gave doors same color as... ....buildings.").
			if (map.zoneManager.ZoneAt(c) is Zone_Stockpile)
			{
				return Stockpile;
			}
			if (b != null && !(b is Building_Door))
			{
				return WalkableBuilding;
			}
			TerrainDef t = c.GetTerrain(map);
			Color32 ground;
			if (t == null)
			{
				ground = Barren;
			}
			else if (t.IsWater)
			{
				return Water;
			}
			else if (t.layerable)
			{
				ground = Floor;
			}
			else if (t.defName.IndexOf("Ice", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				ground = Ice;
			}
			else
			{
				ground = t.fertility >= 0.7f ? Soil : Barren;
			}
			// Snow lies on open ground and floors; whiter the deeper (any snow at all is already plainly white-ish).
			float depth = map.snowGrid.GetDepth(c);
			if (depth > 0.03f)
			{
				return Color32.Lerp(ground, Snow, 0.45f + 0.55f * Mathf.Clamp01(depth));
			}
			return ground;
		}

		// The first free place for the tag: right, left, above, below the mark, then the corners, then the same one step
		// further out. Free = inside the picture and over no other mark or tag. If none is free, the right side.
		private static Box PlaceTag(Box mark, string tag, int pw, int ph, List<Box> taken)
		{
			int tw = tag.Length * CharW + 1;
			int th = LineH;
			int midY = mark.y + mark.h / 2 - th / 2;
			int midX = mark.x + mark.w / 2 - tw / 2;
			for (int ring = 0; ring < 3; ring++)
			{
				int gap = 1 + ring * (th + 1);
				int[][] spots =
				{
					new[] { mark.x + mark.w + gap, midY },
					new[] { mark.x - gap - tw, midY },
					new[] { midX, mark.y + mark.h + gap },
					new[] { midX, mark.y - gap - th },
					new[] { mark.x + mark.w + gap, mark.y + mark.h + gap },
					new[] { mark.x - gap - tw, mark.y + mark.h + gap },
					new[] { mark.x + mark.w + gap, mark.y - gap - th },
					new[] { mark.x - gap - tw, mark.y - gap - th },
				};
				foreach (int[] s in spots)
				{
					Box b = new Box(s[0], s[1], tw, th);
					if (b.x < 0 || b.y < 0 || b.x + b.w > pw || b.y + b.h > ph)
					{
						continue;
					}
					if (!taken.Any((Box o) => o.Hits(b)))
					{
						return b;
					}
				}
			}
			return new Box(mark.x + mark.w + 1, midY, tw, th);
		}

		// White 5 by 7 letters, one pixel each, on black (background) or with no background.
		private static void DrawText(Color32[] px, int pw, int ph, int x, int y, string text, bool background, int size)
		{
			string s = (text ?? "").ToUpperInvariant();
			if (background)
			{
				Fill(px, pw, ph, x, y, s.Length * CharW + 1, LineH, LabelBack);
			}
			for (int i = 0; i < s.Length; i++)
			{
				string[] g;
				if (!Glyphs.TryGetValue(s[i], out g))
				{
					continue;
				}
				for (int row = 0; row < 7; row++)
				{
					for (int col = 0; col < 5; col++)
					{
						if (g[row][col] == '#')
						{
							Fill(px, pw, ph, x + size * (1 + i * CharW + col), y + size * (1 + 6 - row), size, size, LabelText);
						}
					}
				}
			}
		}

		private static void Outline(Color32[] px, int pw, int ph, Box b, Color32 colour)
		{
			Fill(px, pw, ph, b.x, b.y, b.w, 1, colour);
			Fill(px, pw, ph, b.x, b.y + b.h - 1, b.w, 1, colour);
			Fill(px, pw, ph, b.x, b.y, 1, b.h, colour);
			Fill(px, pw, ph, b.x + b.w - 1, b.y, 1, b.h, colour);
		}

		private static void Fill(Color32[] px, int pw, int ph, int x0, int y0, int fw, int fh, Color32 colour)
		{
			int xa = Math.Max(0, x0);
			int ya = Math.Max(0, y0);
			int xb = Math.Min(pw, x0 + fw);
			int yb = Math.Min(ph, y0 + fh);
			for (int y = ya; y < yb; y++)
			{
				int row = y * pw;
				for (int x = xa; x < xb; x++)
				{
					px[row + x] = colour;
				}
			}
		}

		private static string PicturePath()
		{
			string dir = AgentInbox.Enabled ? AgentInbox.OutboxDir : null;
			if (string.IsNullOrEmpty(dir))
			{
				dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "VerbalCommands");
			}
			return Path.Combine(dir, FileName);
		}

		// 5 by 7 dots per character, top row first: the tags' letters and digits.
		private static readonly Dictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
		{
			{ 'A', new[] { ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" } },
			{ 'C', new[] { ".###.", "#...#", "#....", "#....", "#....", "#...#", ".###." } },
			{ 'G', new[] { ".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".###." } },
			{ 'H', new[] { "#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" } },
			{ 'K', new[] { "#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#" } },
			{ 'P', new[] { "####.", "#...#", "#...#", "####.", "#....", "#....", "#...." } },
			{ 'T', new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.." } },
			{ '0', new[] { ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###." } },
			{ '1', new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." } },
			{ '2', new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" } },
			{ '3', new[] { "####.", "....#", "....#", ".###.", "....#", "....#", "####." } },
			{ '4', new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." } },
			{ '5', new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###." } },
			{ '6', new[] { "..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###." } },
			{ '7', new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." } },
			{ '8', new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." } },
			{ '9', new[] { ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.." } },
			{ ',', new[] { ".....", ".....", ".....", ".....", "..##.", "..#..", ".#..." } },
			{ ':', new[] { ".....", "..#..", "..#..", ".....", "..#..", "..#..", "....." } },
			{ 'S', new[] { ".####", "#....", "#....", ".###.", "....#", "....#", "####." } },
		};
	}
}
