using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Verse;

namespace VerbalCommands
{
	// Brief 13 Phase B1 item 3 (+ user rule 4 "Hardware record" and user rule 3 "No device friendly
	// name anywhere"). Resolves a sanitized hardware ID + connection type + audio format for the
	// capture device Unity's Microphone is using, and maintains the "one device record per session,
	// new record only when it changes" rule, writing changed records to device_records.jsonl.
	//
	// This supersedes Probe_Brief13.cs's A2 probe (deleted in this phase): that probe read the
	// endpoint's PKEY_Device_InstanceId PROPERTY, which on the Phase A test machine came back
	// VT_EMPTY (brief 13 "Phase A results": "PKEY_Device_InstanceId on the endpoint property store
	// returned VT_EMPTY, so the cfgmgr32 walk never ran"). Brief 13 Phase B1 decisions' prescribed fix
	// - verified below - is to call IMMDevice::GetId() directly instead of reading that optional
	// property: learn.microsoft.com/en-us/windows/win32/coreaudio/device-properties (fetched
	// 2026-09-26) states, for PKEY_Device_InstanceId: "Stores the audio endpoint device instance
	// identifier. The value CAN ALSO BE ACQUIRED VIA IMMDevice::GetId method" - i.e. GetId() returns
	// the same device-instance-identifier string, but GetId() is a plain interface method every valid
	// IMMDevice must answer (it is how the enumerator itself identifies endpoints), unlike the
	// property store entry a driver may simply not populate. GetId()'s own page
	// (learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdevice-getid, fetched
	// 2026-09-26) separately warns the string's format is "undefined" and should be treated as
	// opaque - so it is only ever fed into CM_Locate_DevNodeW (never parsed/sanitized itself; only the
	// bus-enumerated device IDs found by walking UP from it, via CM_Get_Device_ID, are sanitized and
	// logged), and if CM_Locate_DevNodeW rejects it, that step is logged and the whole thing is
	// skipped for this session (brief 13 Phase B1 item 3: "If the docs do not support that route, try
	// the next documented one; if none works, log the failure step and continue").
	internal static class DeviceIdResolver
	{
		private const string LogPrefix = "[VerbalCommands] devicerec: ";
		private static volatile bool disabledForSession;
		private static volatile bool loggedDisableReason;
		private static readonly object Gate = new object();
		private static string currentKey; // sanitized "connection|hwid|format" used only to detect change
		private static volatile string currentRecordId; // GUID string embedded in voice_debug.jsonl rows

		public static string CurrentDeviceRecordId => currentRecordId;

		// Called from SttEngine.StartCaptureOnMainThread via Task.Run (never on the main thread - COM
		// and cfgmgr32 calls are not Unity API calls, but they are unbounded-latency P/Invoke, so they
		// must not block the frame that just called Microphone.Start()). Brief 13 user rule 4: "Checked
		// when the order box opens and when voice is toggled on" - both paths call
		// SttEngine.StartCaptureOnMainThread, so hooking it there covers both triggers.
		public static void EnsureCurrentAsync(string micDeviceNameInMemoryOnly)
		{
			if (disabledForSession || string.IsNullOrEmpty(micDeviceNameInMemoryOnly))
			{
				return;
			}
			Task.Run(delegate
			{
				try
				{
					Resolve(micDeviceNameInMemoryOnly);
				}
				catch (Exception ex)
				{
					// Guard rule (brief 13 Phase B1 item 6): any failure disables collection for the
					// session and logs exactly once; never affects voice input (this method never
					// touches the mic, recognizer or Send path).
					disabledForSession = true;
					if (!loggedDisableReason)
					{
						loggedDisableReason = true;
						Log.Error(LogPrefix + "disabled for this session: " + ex.GetType().FullName + ": " + ex.Message);
					}
				}
			});
		}

		private static void Resolve(string micDeviceName)
		{
			DeviceInfo info;
			if (!TryResolveComRoute(micDeviceName, out info))
			{
				Log.Message(LogPrefix + "no active capture endpoint matched (or every step failed); no device record this check");
				return;
			}

			string key = info.ConnectionType + "|" + info.HwId + "|" + info.Format;
			lock (Gate)
			{
				if (key == currentKey)
				{
					return; // Unchanged (brief 13 user rule 4: "a new one only when ... changes").
				}
				currentKey = key;
				currentRecordId = Guid.NewGuid().ToString("N");
				WriteRecordAsync(currentRecordId, info);
			}
		}

		private struct DeviceInfo
		{
			public string ConnectionType; // "USB" / "Bluetooth" / "other(ENUM)" / "unknown"
			public string HwId; // sanitized, per Phase A2 rules (see ReportSanitizedIfBusId below)
			public string Format; // "<ch>ch <rate>Hz <bits>bit" or "unknown"
		}

		private static void WriteRecordAsync(string id, DeviceInfo info)
		{
			Task.Run(delegate
			{
				try
				{
					// Same folder as TranscriptLogger.cs/voice_debug.jsonl (brief 13 user rule 5):
					// Path.Combine(GenFilePaths.SaveDataFolderPath, "VerbalCommands", "debug") -
					// sources/RimWorldDecompiled/Verse/GenFilePaths.cs:93-101 (SaveDataFolderPath) and
					// :179 (ConfigFolderPath is a sibling subfolder, never an ancestor - same citation
					// already used in TranscriptLogger.cs).
					string dir = Path.Combine(Path.Combine(GenFilePaths.SaveDataFolderPath, "VerbalCommands"), "debug");
					Directory.CreateDirectory(dir);
					string path = Path.Combine(dir, "device_records.jsonl");

					JObject o = new JObject
					{
						["id"] = id,
						["utc"] = DateTime.UtcNow.ToString("o"),
						["connection"] = info.ConnectionType,
						["hwid"] = info.HwId,
						["format"] = info.Format
					};
					File.AppendAllText(path, o.ToString(Newtonsoft.Json.Formatting.None) + "\n", Encoding.UTF8);
				}
				catch (Exception ex)
				{
					if (!loggedDisableReason)
					{
						loggedDisableReason = true;
						Log.Error(LogPrefix + "failed to write device_records.jsonl (device records disabled for this session): " + ex);
					}
					disabledForSession = true;
				}
			});
		}

		// ---- COM route: IMMDeviceEnumerator -> matching capture endpoint -> IMMDevice::GetId() +
		// PKEY_AudioEngine_DeviceFormat -> cfgmgr32 devnode walk. Interop shapes (GUIDs, vtable order,
		// PROPERTYKEY/PROPVARIANT layout) are the same ones Probe_Brief13.cs verified in Phase A
		// (learn.microsoft.com per-interface pages plus the mingw-w64 header mirror for raw GUID
		// literals and struct field orders not printed on the API reference pages - see that file's
		// original comments, carried over here since Phase A found this part of the COM route worked
		// ("COM: works (CoCreateInstance ok, 1 capture endpoint, matched to Unity's device...")). Only
		// the hardware-ID step changes (GetId() instead of the PKEY_Device_InstanceId property).
		// ---------------------------------------------------------------------------------------------

		private static readonly Guid ClsidMMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
		private const uint CLSCTX_INPROC_SERVER = 0x1;
		private const uint STGM_READ = 0x0;
		private const uint DEVICE_STATE_ACTIVE = 0x1;

		private enum EDataFlow : int { eRender = 0, eCapture = 1, eAll = 2 }

		[StructLayout(LayoutKind.Sequential)]
		private struct PROPERTYKEY
		{
			public Guid fmtid;
			public uint pid;
			public PROPERTYKEY(Guid fmtid, uint pid) { this.fmtid = fmtid; this.pid = pid; }
		}

		[StructLayout(LayoutKind.Explicit)]
		private struct PROPVARIANT
		{
			[FieldOffset(0)] public ushort vt;
			[FieldOffset(8)] public IntPtr pwszVal; // VT_LPWSTR (31)
			[FieldOffset(8)] public int blobCbSize; // VT_BLOB (65)
			[FieldOffset(16)] public IntPtr blobData; // VT_BLOB (65)
		}

		private const ushort VT_LPWSTR = 31;
		private const ushort VT_BLOB = 65;

		[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IMMDeviceEnumerator
		{
			int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IMMDeviceCollection devices);
		}

		[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IMMDeviceCollection
		{
			int GetCount(out uint count);
			int Item(uint index, out IMMDevice device);
		}

		// Vtable order (Activate, OpenPropertyStore, GetId, GetState - mingw-w64 mmdeviceapi.h mirror,
		// cross-checked against learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-
		// immdevice, fetched 2026-09-26): GetId is declared at its real slot 2 (Activate is an unused
		// placeholder kept only to hold slot 0).
		[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IMMDevice
		{
			int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
			int OpenPropertyStore(uint stgmAccess, out IPropertyStore properties);
			// learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdevice-getid
			// (fetched 2026-09-26): "retrieves an endpoint ID string that identifies the audio endpoint
			// device", out LPWSTR, CoTaskMemFree-owned by the callee's allocator - marshaled here as
			// [MarshalAs(UnmanagedType.LPWStr)] out string, which the CLR marshaler frees with
			// CoTaskMemFree automatically for an `out string` parameter of this attribute (standard
			// COM interop marshaling behavior for LPWStr-out parameters).
			int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
		}

		[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IPropertyStore
		{
			int GetCount(out uint cProps);
			int GetAt(uint iProp, out PROPERTYKEY pkey);
			int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
		}

		[DllImport("ole32.dll")]
		private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

		[DllImport("ole32.dll")]
		private static extern int PropVariantClear(ref PROPVARIANT pvar);

		private static readonly PROPERTYKEY PKEY_Device_FriendlyName =
			new PROPERTYKEY(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
		private static readonly PROPERTYKEY PKEY_AudioEngine_DeviceFormat =
			new PROPERTYKEY(new Guid("F19F064D-082C-4E27-BC73-6882A1BB8E4C"), 0);

		private static void ThrowIfFailed(int hr, string step)
		{
			if (hr < 0)
			{
				throw new COMException("HRESULT 0x" + hr.ToString("X8") + " from " + step, hr);
			}
		}

		private static bool TryResolveComRoute(string micDeviceName, out DeviceInfo info)
		{
			info = new DeviceInfo { ConnectionType = "unknown", HwId = "unknown", Format = "unknown" };

			Guid clsid = ClsidMMDeviceEnumerator;
			Guid iidEnumerator = typeof(IMMDeviceEnumerator).GUID;
			IntPtr pEnumUnk;
			int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iidEnumerator, out pEnumUnk);
			ThrowIfFailed(hr, "CoCreateInstance(MMDeviceEnumerator)");

			IMMDeviceEnumerator enumerator = null;
			IMMDeviceCollection collection = null;
			try
			{
				enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(pEnumUnk);
				Marshal.Release(pEnumUnk);

				hr = enumerator.EnumAudioEndpoints(EDataFlow.eCapture, DEVICE_STATE_ACTIVE, out collection);
				ThrowIfFailed(hr, "EnumAudioEndpoints(eCapture)");
				uint count;
				hr = collection.GetCount(out count);
				ThrowIfFailed(hr, "IMMDeviceCollection.GetCount");

				for (uint i = 0; i < count; i++)
				{
					IMMDevice device = null;
					IPropertyStore store = null;
					try
					{
						hr = collection.Item(i, out device);
						if (hr < 0) { continue; }

						hr = device.OpenPropertyStore(STGM_READ, out store);
						if (hr < 0) { continue; }

						string friendlyName = TryGetLpwstr(store, PKEY_Device_FriendlyName);
						// In-memory-only name compare (brief 13 A2 / user rule 3): neither side is ever
						// logged or stored.
						if (friendlyName == null || !string.Equals(friendlyName.Trim(), micDeviceName.Trim(), StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}

						string endpointId;
						hr = device.GetId(out endpointId);
						if (hr < 0 || string.IsNullOrEmpty(endpointId))
						{
							Log.Message(LogPrefix + "IMMDevice::GetId failed hr=0x" + hr.ToString("X8"));
							return false;
						}

						info.Format = TryGetAudioEngineFormat(store) ?? "unknown";

						string connectionType, hwid;
						if (WalkEndpointIdToBusNode(endpointId, out connectionType, out hwid))
						{
							info.ConnectionType = connectionType;
							info.HwId = hwid;
							return true;
						}
						return false; // Walk logged its own failure step already.
					}
					finally
					{
						ReleaseComObjectSafely(store);
						ReleaseComObjectSafely(device);
					}
				}
				Log.Message(LogPrefix + "no active capture endpoint's FriendlyName matched the Unity mic device");
				return false;
			}
			finally
			{
				ReleaseComObjectSafely(collection);
				ReleaseComObjectSafely(enumerator);
			}
		}

		private static string TryGetLpwstr(IPropertyStore store, PROPERTYKEY key)
		{
			PROPERTYKEY k = key;
			PROPVARIANT pv;
			int hr = store.GetValue(ref k, out pv);
			if (hr < 0)
			{
				return null;
			}
			try
			{
				if (pv.vt != VT_LPWSTR || pv.pwszVal == IntPtr.Zero)
				{
					return null;
				}
				return Marshal.PtrToStringUni(pv.pwszVal);
			}
			finally
			{
				try { PropVariantClear(ref pv); } catch (Exception) { }
			}
		}

		// PKEY_AudioEngine_DeviceFormat carries a WAVEFORMATEX blob (learn.microsoft.com/en-us/windows/
		// win32/api/mmeapi/ns-mmeapi-waveformatex): WORD wFormatTag; WORD nChannels; DWORD
		// nSamplesPerSec; DWORD nAvgBytesPerSec; WORD nBlockAlign; WORD wBitsPerSample; WORD cbSize.
		private static string TryGetAudioEngineFormat(IPropertyStore store)
		{
			PROPERTYKEY k = PKEY_AudioEngine_DeviceFormat;
			PROPVARIANT pv;
			int hr = store.GetValue(ref k, out pv);
			if (hr < 0)
			{
				return null;
			}
			try
			{
				if (pv.vt != VT_BLOB || pv.blobData == IntPtr.Zero || pv.blobCbSize < 18)
				{
					return null;
				}
				short nChannels = Marshal.ReadInt16(pv.blobData, 2);
				int nSamplesPerSec = Marshal.ReadInt32(pv.blobData, 4);
				short wBitsPerSample = Marshal.ReadInt16(pv.blobData, 14);
				return nChannels + "ch " + nSamplesPerSec + "Hz " + wBitsPerSample + "bit";
			}
			finally
			{
				try { PropVariantClear(ref pv); } catch (Exception) { }
			}
		}

		private static void ReleaseComObjectSafely(object o)
		{
			if (o == null) return;
			try { Marshal.ReleaseComObject(o); } catch (Exception) { }
		}

		// ---- cfgmgr32 devnode walk (Phase A2's route, docs re-confirmed 2026-09-26) --------------------
		// learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_locate_devnodew: takes a
		// "device instance ID" string. learn.microsoft.com/en-us/windows/win32/coreaudio/device-
		// properties documents PKEY_Device_InstanceId (== IMMDevice::GetId()'s value, per that page) as
		// exactly that kind of ID, so GetId()'s string is fed here directly - if CM_Locate_DevNodeW
		// rejects it (learn.microsoft.com/en-us/windows/win32/coreaudio/endpoint-id-strings separately
		// warns the endpoint-ID format is "undefined" and might not always resolve), that is logged and
		// treated as "this route failed" (brief 13 item 3: "log the failure step and continue").
		// learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_parent: walks up one
		// devnode at a time to its parent; CM_Get_Device_IDW reads each node's own device instance ID
		// (used here exactly as Probe_Brief13.cs's WalkToBusNode did in Phase A).
		[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
		private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

		[DllImport("cfgmgr32.dll")]
		private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

		[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
		private static extern int CM_Get_Device_IDW(uint dnDevInst, StringBuilder buffer, uint bufferLen, uint ulFlags);

		private const uint CM_LOCATE_DEVNODE_NORMAL = 0x0;
		private const int CR_SUCCESS = 0x0;
		private const uint MAX_DEVICE_ID_LEN = 200;

		private static bool WalkEndpointIdToBusNode(string endpointId, out string connectionType, out string hwid)
		{
			connectionType = null;
			hwid = null;

			uint devInst;
			int cr = CM_Locate_DevNodeW(out devInst, endpointId, CM_LOCATE_DEVNODE_NORMAL);
			if (cr != CR_SUCCESS)
			{
				Log.Message(LogPrefix + "CM_Locate_DevNodeW(GetId endpoint id) failed, CONFIGRET=0x" + cr.ToString("X8") +
					" (id withheld - brief 13 hard rule: never log the raw instance/endpoint id)");
				return false;
			}

			const int maxHops = 12;
			for (int hop = 0; hop < maxHops; hop++)
			{
				StringBuilder buf = new StringBuilder((int)MAX_DEVICE_ID_LEN);
				cr = CM_Get_Device_IDW(devInst, buf, MAX_DEVICE_ID_LEN, 0);
				if (cr != CR_SUCCESS)
				{
					Log.Message(LogPrefix + "CM_Get_Device_IDW failed at hop " + hop + ", CONFIGRET=0x" + cr.ToString("X8"));
					return false;
				}

				string sanitizedType, sanitizedId;
				if (ReportSanitizedIfBusId(buf.ToString(), out sanitizedType, out sanitizedId))
				{
					connectionType = sanitizedType;
					hwid = sanitizedId;
					return true;
				}

				uint parent;
				cr = CM_Get_Parent(out parent, devInst, 0);
				if (cr != CR_SUCCESS)
				{
					Log.Message(LogPrefix + "CM_Get_Parent failed at hop " + hop + " (CONFIGRET=0x" + cr.ToString("X8") +
						") - reached the top of the device tree without a recognized bus prefix");
					return false;
				}
				devInst = parent;
			}
			Log.Message(LogPrefix + "gave up after " + maxHops + " CM_Get_Parent hops without a recognized bus prefix");
			return false;
		}

		// Sanitization rules verbatim from brief 13 A2 (moved from Probe_Brief13.cs, which is deleted
		// in this phase): USB -> "USB\VID_xxxx&PID_yyyy[&REV_zzzz]" only; Bluetooth -> "BT" plus
		// VID/PID(/REV) only, never a MAC/12-hex address; any other recognized bus (HDAUDIO, SWD, ACPI,
		// PCI, ROOT) -> the enumerator name plus VEN_/DEV_ or FUNC_ only. Never the raw instance id,
		// friendly name or endpoint GUID string (brief 13 user rule 3 / hard constraints).
		private static bool ReportSanitizedIfBusId(string rawId, out string connectionType, out string sanitizedId)
		{
			connectionType = null;
			sanitizedId = null;
			if (string.IsNullOrEmpty(rawId))
			{
				return false;
			}
			string upper = rawId.ToUpperInvariant();

			if (upper.StartsWith("USB\\"))
			{
				Match m = Regex.Match(rawId, @"VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4}(&REV_[0-9A-Fa-f]{4})?", RegexOptions.IgnoreCase);
				connectionType = "USB";
				sanitizedId = m.Success ? "USB\\" + m.Value.ToUpperInvariant() : "USB (VID/PID pattern not found)";
				return true;
			}
			// BTHLEDEVICE = Bluetooth LE enumerator (added during Phase A review, 2026-09-26 - distinct
			// from classic Bluetooth's BTHENUM\/BTH\; see VerbalCommands.csproj-adjacent review notes in
			// COMPACT_2026-09-26.md item 2 under Phase A fixes).
			if (upper.StartsWith("BTHENUM\\") || upper.StartsWith("BLUETOOTH\\") || upper.StartsWith("BTH\\") || upper.StartsWith("BTHLEDEVICE\\"))
			{
				Match m = Regex.Match(rawId,
					@"VID[_&]?([0-9A-Fa-f]{4}).*?PID[_&]?([0-9A-Fa-f]{4})(?:.*?REV[_&]?([0-9A-Fa-f]{4}))?",
					RegexOptions.IgnoreCase);
				connectionType = "Bluetooth";
				if (m.Success)
				{
					sanitizedId = "BT VID_" + m.Groups[1].Value.ToUpperInvariant() + "&PID_" + m.Groups[2].Value.ToUpperInvariant();
					if (m.Groups[3].Success)
					{
						sanitizedId += "&REV_" + m.Groups[3].Value.ToUpperInvariant();
					}
				}
				else
				{
					sanitizedId = "BT";
				}
				return true;
			}

			int firstBackslash = rawId.IndexOf('\\');
			string enumerator = firstBackslash > 0 ? rawId.Substring(0, firstBackslash) : rawId;
			bool isKnownBusEnumerator = enumerator == "HDAUDIO" || enumerator == "SWD" || enumerator == "ACPI" ||
				enumerator == "PCI" || enumerator == "ROOT";
			if (!isKnownBusEnumerator)
			{
				return false;
			}
			Match venDev = Regex.Match(rawId, @"VEN_[0-9A-Fa-f]{4}(&DEV_[0-9A-Fa-f]{4})?", RegexOptions.IgnoreCase);
			Match func = Regex.Match(rawId, @"FUNC_[0-9A-Fa-f]+", RegexOptions.IgnoreCase);
			string detail = venDev.Success ? venDev.Value.ToUpperInvariant() : (func.Success ? func.Value.ToUpperInvariant() : "(no VEN/DEV/FUNC found)");
			connectionType = "other(" + enumerator + ")";
			sanitizedId = enumerator + "\\" + detail;
			return true;
		}
	}
}
