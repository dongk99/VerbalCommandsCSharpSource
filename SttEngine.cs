using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using RimWorld;
using SherpaOnnx;
using UnityEngine;
using Verse;

namespace VerbalCommands
{
	public enum SttState
	{
		// Nothing loaded, worker thread not running. Icon/tooltip: off.
		NotStarted,
		// Worker thread is loading the model / creating the recognizer. Icon/tooltip: loading model.
		LoadingModel,
		// Recognizer loaded and idle (worker thread alive, mic not capturing) - reached after Stop()
		// while the model stays warm for a same-box restart. Icon/tooltip: off.
		Ready,
		// Recognizer loaded AND mic capturing. Icon: green. Tooltip: listening.
		Listening,
		// Native preload, model load, mic acquisition or a worker exception failed. Icon/tooltip: off + reason.
		Error
	}

	// Replaces DictationSession (brief 11): bundled offline sherpa-onnx streaming zipformer instead of
	// UnityEngine.Windows.Speech.DictationRecognizer, which produced nothing on the user's machine.
	//
	// API facts below are decompiled (ilspycmd) from org.k2fsa.sherpa.onnx 1.13.8's managed
	// lib/net45/sherpa-onnx.dll (chosen over the netstandard2.0 build in the same package: this
	// project's own obj/project.assets.json shows NuGet already resolves Newtonsoft.Json 13.0.3 to its
	// net45 asset for our net472 TargetFramework - "compile": {"lib/net45/Newtonsoft.Json.dll": ...} -
	// a pattern already proven to run under RimWorld's Mono in this exact project; ilspycmd diff of
	// SherpaOnnx.OnlineStream/.OnlineRecognizer between the net45 and netstandard2.0 builds is
	// byte-for-byte identical, so this is a like-for-like choice, not a compatibility gamble). The
	// native runtime package used is org.k2fsa.sherpa.onnx.runtime.win-x64 1.13.8
	// (runtimes/win-x64/native/onnxruntime.dll + sherpa-onnx-c-api.dll).
	//
	// Verified members (namespace SherpaOnnx):
	//  - OnlineRecognizer(OnlineRecognizerConfig) ctor; CreateStream() -> OnlineStream; IsReady(OnlineStream) -> bool;
	//    Decode(OnlineStream); GetResult(OnlineStream) -> OnlineRecognizerResult; IsEndpoint(OnlineStream) -> bool;
	//    Reset(OnlineStream); Dispose(). Every native call is [DllImport("sherpa-onnx-c-api")].
	//  - OnlineStream.AcceptWaveform(int sampleRate, float[] samples); Dispose().
	//  - OnlineRecognizerResult.Text (string, the only field used here).
	//  - OnlineRecognizerConfig: FeatConfig (FeatureConfig{SampleRate=16000,FeatureDim=80} - the
	//    model's own expected feature rate, independent of the mic's actual capture rate),
	//    ModelConfig (OnlineModelConfig{Transducer,Tokens,NumThreads=1 default,Provider="cpu" default}),
	//    DecodingMethod="greedy_search" default, EnableEndpoint=0 default (we set 1 to enable),
	//    Rule1MinTrailingSilence=1.2f / Rule2MinTrailingSilence=2.4f / Rule3MinUtteranceLength=20f -
	//    these three are sherpa's own default endpoint rule values (struct field initializers), left
	//    untouched per brief 11 section B "sherpa's default rules".
	//  - OnlineTransducerModelConfig: Encoder/Decoder/Joiner (string paths).
	// AcceptWaveform takes the caller's actual sampleRate every call, which implies sherpa resamples
	// internally to FeatConfig.SampleRate; this is an inference from the C# API signature (why else
	// would every chunk carry a sample rate?), not something that could be behaviorally verified here
	// since the brief forbids launching RimWorld. Flagged as unverified in the report.
	public sealed class SttEngine
	{
		private const string LogPrefix = "[VerbalCommands] stt: ";
		public const string ModelFolderName = "sherpa-onnx-streaming-zipformer-en-2023-06-21";
		private const int MicLoopLengthSeconds = 10;
		private const int DesiredSampleRate = 16000;

		private readonly Action<string> onFinalText;

		private volatile SttState state = SttState.NotStarted;
		private volatile string errorReason;
		private volatile string hypothesis = "";

		private Thread workerThread;
		private BlockingCollection<float[]> sampleQueue;
		private volatile bool shutdownRequested;
		private volatile bool autoCaptureWhenReady;

		// Only ever touched on the worker thread once created.
		private OnlineRecognizer recognizer;
		private OnlineStream onlineStream;
		// Brief 13 Phase B1: created (and disposed) alongside recognizer/onlineStream, only when
		// VerbalCommandsSettings.collectVoiceDebug is on; null otherwise or if its own creation failed
		// (VoiceDebugRecorder.TryCreate already logs the reason once and never throws).
		private VoiceDebugRecorder voiceDebugRecorder;

		// Only ever touched on the main thread.
		private AudioClip micClip;
		private string micDeviceName;
		private int lastMicPosition;
		private int actualSampleRate;
		private volatile bool capturing;

		public SttState State => state;
		public string ErrorReason => errorReason;
		public string Hypothesis => hypothesis;
		public bool IsListening => state == SttState.Listening;

		public SttEngine(Action<string> onFinalText)
		{
			this.onFinalText = onFinalText;
		}

		// Deliberate start: Dialog_VerbalCommand.PostOpen (voice setting ON) or the player's
		// toggle-on (brief 11 section A: mic acquired only while the box is open with voice on, or on
		// toggle-on). The first call this box session spins up the worker thread (model load); a later
		// call while the model is already Ready just re-acquires the mic without reloading it, so
		// toggling off then back on in the same box is instant.
		public void Start()
		{
			if (state == SttState.Listening)
			{
				return;
			}

			if (state == SttState.Error)
			{
				state = SttState.NotStarted;
				errorReason = null;
			}

			if (workerThread == null)
			{
				shutdownRequested = false;
				autoCaptureWhenReady = true;
				sampleQueue = new BlockingCollection<float[]>();
				state = SttState.LoadingModel;
				workerThread = new Thread(WorkerLoop);
				workerThread.IsBackground = true;
				workerThread.Name = "VerbalCommands-Stt";
				workerThread.Start();
				return;
			}

			if (state == SttState.LoadingModel)
			{
				autoCaptureWhenReady = true;
				return;
			}

			// state == Ready: recognizer already loaded from an earlier Start() in this same box
			// session; just resume capturing.
			StartCaptureOnMainThread();
		}

		// Deliberate stop: Send, toggle-off, or an error path. Releases the mic
		// (Microphone.End must run on every one of these paths - brief 11 resource rule) but leaves
		// the worker thread and recognizer alive so a same-box restart is instant. Idempotent.
		public void Stop(string reason)
		{
			autoCaptureWhenReady = false;

			if (micClip != null)
			{
				Microphone.End(micDeviceName);
				micClip = null;
				Log.Message(LogPrefix + "mic released (" + reason + ")");
			}
			capturing = false;
			hypothesis = "";

			if (state == SttState.Listening)
			{
				state = SttState.Ready;
			}
		}

		// Dialog_VerbalCommand.PostClose: stops capture/releases the mic (via Stop), then tells the
		// worker thread to exit, which disposes the OnlineStream and OnlineRecognizer (brief 11
		// section A.5-6: "dispose the recognizer and model" on close).
		public void Dispose()
		{
			Stop("close");

			if (workerThread == null)
			{
				return;
			}

			shutdownRequested = true;
			try
			{
				if (sampleQueue != null && !sampleQueue.IsAddingCompleted)
				{
					sampleQueue.Add(null); // poison pill: wakes a blocked TryTake immediately
				}
			}
			catch (Exception)
			{
				// Queue already completed/disposed by a race with the worker thread finishing on its
				// own; nothing to do.
			}

			if (!workerThread.Join(2000))
			{
				Log.Warning(LogPrefix + "worker thread did not exit within 2s of Dispose(); abandoning it " +
					"(background thread, process is not blocked, but native handles may leak until process exit)");
			}

			workerThread = null;
			if (sampleQueue != null)
			{
				sampleQueue.Dispose();
				sampleQueue = null;
			}
			state = SttState.NotStarted;
		}

		// Main thread, once per real frame while the dialog is open (Dialog_VerbalCommand.WindowUpdate).
		// Reads only the mic samples captured since the last call and hands them to the worker thread;
		// never touches the recognizer/stream itself.
		public void Poll()
		{
			if (!capturing || micClip == null)
			{
				return;
			}

			int currentPos = Microphone.GetPosition(micDeviceName);
			if (currentPos == lastMicPosition)
			{
				return;
			}

			int clipSamples = micClip.samples;
			int channels = micClip.channels;
			int newFrameCount = (currentPos > lastMicPosition)
				? (currentPos - lastMicPosition)
				: ((clipSamples - lastMicPosition) + currentPos);
			if (newFrameCount <= 0)
			{
				lastMicPosition = currentPos;
				return;
			}

			float[] raw = new float[newFrameCount * channels];
			if (currentPos > lastMicPosition)
			{
				micClip.GetData(raw, lastMicPosition);
			}
			else
			{
				int tailFrames = clipSamples - lastMicPosition;
				if (tailFrames > 0)
				{
					float[] tail = new float[tailFrames * channels];
					micClip.GetData(tail, lastMicPosition);
					Array.Copy(tail, 0, raw, 0, tail.Length);
				}
				if (currentPos > 0)
				{
					float[] head = new float[currentPos * channels];
					micClip.GetData(head, 0);
					Array.Copy(head, 0, raw, tailFrames * channels, head.Length);
				}
			}
			lastMicPosition = currentPos;

			float[] mono = channels > 1 ? DownmixToMono(raw, channels) : raw;

			try
			{
				if (sampleQueue != null && !sampleQueue.IsAddingCompleted)
				{
					sampleQueue.Add(mono);
				}
			}
			catch (Exception)
			{
				// Queue completed (Dispose racing us) - drop this chunk, we're shutting down anyway.
			}
		}

		private static float[] DownmixToMono(float[] interleaved, int channels)
		{
			int frames = interleaved.Length / channels;
			float[] mono = new float[frames];
			for (int i = 0; i < frames; i++)
			{
				float sum = 0f;
				int baseIdx = i * channels;
				for (int c = 0; c < channels; c++)
				{
					sum += interleaved[baseIdx + c];
				}
				mono[i] = sum / channels;
			}
			return mono;
		}

		private void StartCaptureOnMainThread()
		{
			string[] devices = Microphone.devices;
			if (devices == null || devices.Length == 0)
			{
				ReportError("no microphone device found");
				return;
			}

			// "Use the default device" (brief 11 section B): UnityEngine.Microphone has no explicit
			// "default device" member (verified by ilspycmd -t UnityEngine.Microphone against
			// UnityEngine.AudioModule.dll - only `devices`, `Start`, `End`, `IsRecording`,
			// `GetPosition`, `GetDeviceCaps`, all keyed by a caller-supplied device name string), so
			// the first entry of Microphone.devices is used and logged by name for diagnostics.
			micDeviceName = devices[0];

			int minFreq, maxFreq;
			Microphone.GetDeviceCaps(micDeviceName, out minFreq, out maxFreq);
			// Per Unity's documented Microphone.GetDeviceCaps behavior (not decompilable further -
			// GetDeviceCaps is a native InternalCall with no managed-side logic beyond the P/Invoke
			// shim already shown by ilspycmd): minFreq==0 && maxFreq==0 means the device accepts any
			// sampling frequency, otherwise the requested rate is clamped into [minFreq, maxFreq].
			if (minFreq == 0 && maxFreq == 0)
			{
				actualSampleRate = DesiredSampleRate;
			}
			else
			{
				actualSampleRate = Mathf.Clamp(DesiredSampleRate, minFreq, maxFreq);
			}

			AudioClip clip;
			try
			{
				clip = Microphone.Start(micDeviceName, true, MicLoopLengthSeconds, actualSampleRate);
			}
			catch (Exception ex)
			{
				ReportError("Microphone.Start threw: " + ex.Message);
				return;
			}

			if (clip == null)
			{
				ReportError("Microphone.Start returned null for device \"" + micDeviceName + "\"");
				return;
			}

			micClip = clip;
			lastMicPosition = 0;
			capturing = true;
			state = SttState.Listening;
			// Brief 13 user rule 4 ("Checked when the order box opens and when voice is toggled on"):
			// this method runs on exactly those two triggers (Dialog_VerbalCommand.PostOpen -> Start(),
			// and GameComponent_VerbalCommands' voice-toggle-key handler -> SetVoiceEnabled(true) ->
			// Start()). Dispatched async (DeviceIdResolver.EnsureCurrentAsync does the actual COM/
			// cfgmgr32 work via Task.Run) so it never adds latency to this main-thread call.
			if (VerbalCommandsMod.Settings.collectVoiceDebug)
			{
				DeviceIdResolver.EnsureCurrentAsync(micDeviceName);
			}
			// Brief 13 user rule 3 ("No device friendly name anywhere ... log, file, bundle"): the
			// device name can be edited by the player and can contain a person's name, so it is never
			// logged, only the negotiated capture parameters (brief 11's original line included
			// `device="` + micDeviceName + `"` here; removed).
			Log.Message(LogPrefix + "mic capture started: rate=" + actualSampleRate +
				"Hz (requested " + DesiredSampleRate + "Hz, device caps " + minFreq + "-" + maxFreq + "Hz)");
		}

		// Callable from either thread (WorkerLoop failures, or StartCaptureOnMainThread failures).
		// Only sets plain volatile fields directly (thread-safe); the actual Microphone.End call is
		// always deferred through MainThread.Post, since UnityEngine.Microphone.Start/End/IsRecording/
		// GetDeviceCaps are not marked IsThreadSafe in the decompiled UnityEngine.AudioModule.dll
		// (ilspycmd -t UnityEngine.Microphone; only GetMicrophoneDeviceIDFromName and GetRecordPosition
		// carry [NativeMethod(IsThreadSafe = true)]), so they must only ever be called from the main
		// thread.
		private void ReportError(string reason)
		{
			Log.Error(LogPrefix + reason);
			errorReason = reason;
			state = SttState.Error;
			capturing = false;
			MainThread.Post(delegate
			{
				if (micClip != null)
				{
					Microphone.End(micDeviceName);
					micClip = null;
				}
				Messages.Message("VerbalCommands_SttError".Translate(reason), MessageTypeDefOf.RejectInput, false);
			});
		}

		// Runs entirely on the dedicated worker thread for the lifetime of one box session: model
		// load, then every AcceptWaveform/Decode/GetResult/IsEndpoint/Reset call, then final disposal.
		// Never touches game or dialog state directly - every result crosses back through
		// MainThread.Post (VerbalCommands/MainThread.cs), same pattern as OrderController's HTTP calls.
		private void WorkerLoop()
		{
			try
			{
				string nativeDir = Path.Combine(VerbalCommandsMod.ModRootDir, "1.6", "Native");
				string failReason;
				if (!NativeLibraryLoader.EnsureLoaded(nativeDir, out failReason))
				{
					ReportError("native DLL preload failed: " + failReason);
					return;
				}

				string modelDir = Path.Combine(Path.Combine(VerbalCommandsMod.ModRootDir, "Models"), ModelFolderName);
				OnlineRecognizerConfig config = BuildConfig(modelDir);

				Stopwatch sw = Stopwatch.StartNew();
				OnlineRecognizer rec;
				try
				{
					rec = new OnlineRecognizer(config);
				}
				catch (Exception ex)
				{
					ReportError("model load failed: " + ex.Message);
					return;
				}

				OnlineStream stream;
				try
				{
					stream = rec.CreateStream();
				}
				catch (Exception ex)
				{
					try { rec.Dispose(); } catch (Exception) { }
					ReportError("CreateStream failed: " + ex.Message);
					return;
				}
				sw.Stop();

				recognizer = rec;
				onlineStream = stream;
				Log.Message(LogPrefix + "model loaded in " + sw.ElapsedMilliseconds + " ms (" + modelDir + ")");

				// Brief 13 Phase B1 item 1: "Only when on: create the VAD ... on the existing worker
				// thread." TryCreate() never throws (guards internally); a null result just means no
				// voice debug data this session, decode/recognition below is unaffected either way.
				if (VerbalCommandsMod.Settings.collectVoiceDebug)
				{
					voiceDebugRecorder = VoiceDebugRecorder.TryCreate();
				}

				MainThread.Post(delegate
				{
					if (shutdownRequested)
					{
						return; // Dispose() raced us; worker will still exit and clean up below.
					}
					state = SttState.Ready;
					if (autoCaptureWhenReady)
					{
						StartCaptureOnMainThread();
					}
				});

				while (true)
				{
					float[] chunk;
					bool got = sampleQueue.TryTake(out chunk, 100);
					if (!got)
					{
						if (shutdownRequested)
						{
							break;
						}
						continue;
					}
					if (chunk == null)
					{
						break; // poison pill from Dispose()
					}

					try
					{
						// Brief 13 Phase B1 item 6: fed the same mono chunk (before sherpa's own
						// internal resampling - SttEngine.cs header comment) as a 16 kHz copy, on this
						// same worker thread. No-op (and cannot throw into this try) when the recorder
						// is null - guard is inside the call site, not just VoiceDebugRecorder itself.
						if (voiceDebugRecorder != null)
						{
							voiceDebugRecorder.ProcessChunk(chunk, actualSampleRate);
						}

						stream.AcceptWaveform(actualSampleRate, chunk);
						while (rec.IsReady(stream))
						{
							rec.Decode(stream);
						}
						OnlineRecognizerResult result = rec.GetResult(stream);
						string text = result.Text ?? "";
						bool isEndpoint = rec.IsEndpoint(stream);

						if (isEndpoint)
						{
							rec.Reset(stream);
							string finalText = text.Trim();
							// Brief 13 Phase B1 item 2: "one record per recognizer endpoint" - including
							// a silence-only endpoint with empty finalText (still useful noise data).
							if (voiceDebugRecorder != null)
							{
								voiceDebugRecorder.FinalizeUtterance(finalText, ModelFolderName, DeviceIdResolver.CurrentDeviceRecordId);
							}
							MainThread.Post(delegate
							{
								hypothesis = "";
								if (!string.IsNullOrEmpty(finalText))
								{
									onFinalText?.Invoke(finalText);
								}
							});
						}
						else
						{
							MainThread.Post(delegate
							{
								hypothesis = text;
							});
						}
					}
					catch (Exception ex)
					{
						Log.Error(LogPrefix + "worker exception during decode: " + ex);
					}
				}

				try { stream.Dispose(); } catch (Exception ex) { Log.Error(LogPrefix + "exception disposing stream: " + ex); }
				try { rec.Dispose(); } catch (Exception ex) { Log.Error(LogPrefix + "exception disposing recognizer: " + ex); }
				// Brief 13 Phase B1 item 6: "Dispose the VAD properly."
				if (voiceDebugRecorder != null)
				{
					voiceDebugRecorder.Dispose();
					voiceDebugRecorder = null;
				}
				recognizer = null;
				onlineStream = null;
				Log.Message(LogPrefix + "worker thread exiting, recognizer disposed");
			}
			catch (Exception ex)
			{
				ReportError("worker thread fatal exception: " + ex);
			}
		}

		private static OnlineRecognizerConfig BuildConfig(string modelDir)
		{
			OnlineRecognizerConfig config = new OnlineRecognizerConfig
			{
				FeatConfig = new FeatureConfig
				{
					SampleRate = 16000,
					FeatureDim = 80
				},
				ModelConfig = new OnlineModelConfig
				{
					Transducer = new OnlineTransducerModelConfig
					{
						Encoder = Path.Combine(modelDir, "encoder-epoch-99-avg-1.int8.onnx"),
						Decoder = Path.Combine(modelDir, "decoder-epoch-99-avg-1.int8.onnx"),
						Joiner = Path.Combine(modelDir, "joiner-epoch-99-avg-1.int8.onnx")
					},
					Tokens = Path.Combine(modelDir, "tokens.txt"),
					NumThreads = 2, // brief 11 section B
					Provider = "cpu" // brief 11 section B; also SherpaOnnx.OnlineModelConfig's own default
				},
				DecodingMethod = "greedy_search", // brief 11 section B; also the struct's own default
				EnableEndpoint = 1 // enable endpoint detection; Rule1/2/3 below are left at sherpa's own
				// default values (SherpaOnnx.OnlineRecognizerConfig field initializers, decompiled from
				// lib/net45/sherpa-onnx.dll): Rule1MinTrailingSilence=1.2f, Rule2MinTrailingSilence=2.4f,
				// Rule3MinUtteranceLength=20f - simply not set here, so the struct's own defaults apply.
			};
			return config;
		}
	}
}
