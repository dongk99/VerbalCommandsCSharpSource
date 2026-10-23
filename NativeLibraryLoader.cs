using System;
using System.IO;
using System.Runtime.InteropServices;
using Verse;

namespace VerbalCommands
{
	// Preloads the native sherpa-onnx/onnxruntime DLLs from mod\1.6\Native\ by full path via
	// kernel32!LoadLibraryW, in dependency order (onnxruntime.dll first), before any P/Invoke into
	// sherpa-onnx-c-api.dll runs (brief 11 section B "Native DLL placement").
	//
	// Why this exists (verified):
	// 1. sources/RimWorldDecompiled/Verse/ModAssemblyHandler.cs:31-49 (ReloadAll) walks every *.dll
	//    directly under the mod's Assemblies/ folder (ModContentPack.GetAllFilesForModPreserveOrder,
	//    :31) and calls Assembly.LoadFrom on each one (:37), treating it as a managed .NET assembly.
	//    A native DLL there would throw inside that try (BadImageFormatException), and the catch
	//    (:39-43) does `break`, which stops loading every assembly still queued after it in that
	//    folder - so native DLLs must live outside Assemblies/ entirely. They are placed in
	//    mod\1.6\Native\ instead, which ModAssemblyHandler never looks at.
	// 2. Because mod\1.6\Native\ is not on the game's DLL search path, the sherpa-onnx.dll wrapper's
	//    plain `[DllImport("sherpa-onnx-c-api")]` P/Invoke calls (decompiled from
	//    org.k2fsa.sherpa.onnx 1.13.8's lib/net45/sherpa-onnx.dll - see SttEngine.cs header comment)
	//    would fail with DllNotFoundException under the CLR's standard native-library search order.
	//    Loading each DLL once by its own full path with LoadLibraryW registers it in the process's
	//    already-loaded-module table under its base file name; Win32's loader resolves any later
	//    reference to that same base file name (a later P/Invoke call, or another DLL's own static
	//    import table) against the already-loaded module instead of re-searching disk (documented
	//    kernel32 LoadLibrary/GetProcAddress module-resolution behavior - this is OS loader behavior,
	//    not a RimWorld/Unity/sherpa API, so it has no decompiled-source citation). This is also how
	//    sherpa-onnx-c-api.dll's own static dependency on onnxruntime.dll gets resolved, provided
	//    onnxruntime.dll is loaded first - hence the fixed order below.
	internal static class NativeLibraryLoader
	{
		private const string LogPrefix = "[VerbalCommands] stt: ";

		[DllImport("kernel32.dll", EntryPoint = "LoadLibraryW", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr LoadLibraryW(string lpLibFileName);

		private static bool attempted;
		private static bool succeeded;
		private static string lastFailureReason;

		// Idempotent and safe to call from every SttEngine session: LoadLibraryW on an
		// already-loaded module just increments its reference count and returns the existing handle,
		// so repeated preload attempts across dialog open/close cycles never double-load or leak.
		public static bool EnsureLoaded(string nativeDir, out string failureReason)
		{
			if (attempted)
			{
				failureReason = succeeded ? null : lastFailureReason;
				return succeeded;
			}
			attempted = true;

			string[] names = { "onnxruntime.dll", "sherpa-onnx-c-api.dll" };
			foreach (string name in names)
			{
				string path = Path.Combine(nativeDir, name);
				if (!File.Exists(path))
				{
					lastFailureReason = "missing " + name + " at " + path;
					Log.Error(LogPrefix + lastFailureReason);
					succeeded = false;
					failureReason = lastFailureReason;
					return false;
				}

				IntPtr handle = LoadLibraryW(path);
				if (handle == IntPtr.Zero)
				{
					int err = Marshal.GetLastWin32Error();
					lastFailureReason = "LoadLibraryW(\"" + path + "\") failed, Win32 error " + err;
					Log.Error(LogPrefix + lastFailureReason);
					succeeded = false;
					failureReason = lastFailureReason;
					return false;
				}

				Log.Message(LogPrefix + "preloaded " + name + " from " + path);
			}

			succeeded = true;
			failureReason = null;
			return true;
		}
	}
}
