using UnityEngine;
using Verse;

namespace VerbalCommands
{
	// Loads the mic status icons once at startup. Path convention (no extension, relative to the
	// Textures/ folder) confirmed against sources/RimWorldDecompiled/Verse/GenFilePaths.cs:391-399
	// (ContentPath<Texture2D>() returns "Textures/") and the vanilla usage pattern in
	// sources/RimWorldDecompiled/Verse/TexButton.cs (e.g. ContentFinder<Texture2D>.Get("UI/Widgets/CloseX")
	// for mod\Textures\UI\Widgets\CloseX.png), which is also a [StaticConstructorOnStartup] class.
	[StaticConstructorOnStartup]
	public static class VerbalCommandsTex
	{
		public static readonly Texture2D MicOn = ContentFinder<Texture2D>.Get("VerbalCommands/MicOn");

		public static readonly Texture2D MicOff = ContentFinder<Texture2D>.Get("VerbalCommands/MicOff");
	}
}
