using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NWaves.Features;
using NWaves.Filters.Base;
using NWaves.Filters.Fda;
using NWaves.Transforms;
using NWaves.Utils;
using NWaves.Windows;
using SherpaOnnx;
using Verse;

namespace VerbalCommands
{
	// Brief 13 Phase B1 items 1-2 (+ Phase B1 decisions "Open-source parts chosen"). Owned by
	// SttEngine's worker thread for the lifetime of one box session, created only when
	// VerbalCommandsSettings.collectVoiceDebug is on. Feeds a mono 16 kHz copy of the same samples
	// already going to sherpa's recognizer into a sherpa-onnx Silero VAD, accumulates per-utterance
	// noise/speech measurements between recognizer endpoints, and appends one JSON line per utterance
	// to voice_debug.jsonl. Every public method is wrapped so a failure anywhere (model load, NWaves
	// type-init, VAD native call, file I/O) logs exactly once and permanently disables this instance -
	// it must never affect voice input/recognition (brief 13 "Hard constraints").
	//
	// Measurement-artifact rewrite (this file, 2026-09-26): the per-chunk independent NWaves
	// Resampler.Resample() call used to build a *fresh* FIR filter (fresh delay line) every single
	// ProcessChunk invocation - NWaves.Operations.Resampler.Resample, ilspycmd -t
	// NWaves.Operations.Resampler against NWaves 0.9.6's netstandard2.0 lib, 2026-09-26, shows this
	// method has no persisted state at all: it takes a DiscreteSignal and returns a new one, with no
	// field on Resampler carrying anything across calls. That produced a transient click/glitch at
	// every chunk boundary, and ALL measurements below used to be computed on that glitchy resampled
	// copy. This rewrite (a) keeps a genuinely STREAMING resampler (stateful FIR anti-alias filter +
	// fractional-phase decimation, both carried across ProcessChunk calls) feeding ONLY the VAD, and
	// (b) computes every measurement from a separate, untouched, ORIGINAL-source-rate copy of the
	// samples instead of the VAD's 16 kHz stream.
	internal sealed class VoiceDebugRecorder : IDisposable
	{
		private const string LogPrefix = "[VerbalCommands] voicedebug: ";
		private const int VadSampleRate = 16000;
		// sherpa-onnx c-api.h (raw.githubusercontent.com/k2-fsa/sherpa-onnx/master/sherpa-onnx/c-api/
		// c-api.h, fetched 2026-09-26): "buffer_size_in_seconds: Internal buffering capacity in
		// seconds." SttEngine's own OnlineRecognizerConfig leaves sherpa's default endpoint rules in
		// place (Rule2MinTrailingSilence=2.4f, Rule3MinUtteranceLength=20f - SttEngine.cs BuildConfig),
		// so a bound utterance span is at most ~20s of speech plus a few seconds of trailing silence;
		// 30s gives headroom without holding an unbounded amount of native buffer.
		private const float VadBufferSeconds = 30f;

		// --- Streaming VAD resampler (design item A) ---------------------------------------------
		// Anti-alias lowpass cutoff, well below the 16 kHz VAD stream's own Nyquist (8000 Hz), so the
		// decimation below doesn't fold energy above 8 kHz back into the VAD's passband. This filter
		// (and the decimation phase below) exists ONLY to feed Silero the mono 16 kHz stream it
		// requires; it is never used for any reported measurement - those come from the untouched
		// source-rate buffer (spanSourceSamples, see design item B).
		private const double VadAntiAliasCutoffHz = 7000.0;
		// Odd order -> DesignFilter.FirWinLp's underlying FirWinFdLp centers the kernel exactly at
		// index (order-1)/2 with zero fractional delay (ilspycmd -t NWaves.Filters.Fda.DesignFilter,
		// 2026-09-26: FirWinLp(order,...) calls FirWinFdLp(order, frequency, (order+1)%2 * 0.5, ...) -
		// for odd order, (order+1)%2==0, so the fractional-delay argument is exactly 0), so this
		// filter's group delay is an EXACT INTEGER number of input-rate samples: (order-1)/2 = 63.
		// That integer is what VadGroupDelaySamples() below folds into the VAD-index -> source-rate-
		// index mapping used by FinalizeUtterance (design item A "document how the filter's group
		// delay is handled").
		private const int VadAntiAliasFilterOrder = 127;

		private readonly VoiceActivityDetector vad;

		// NWaves.Filters.Base.FirFilter.Process(float) (ilspycmd -t NWaves.Filters.Base.FirFilter,
		// 2026-09-26) reads/writes the filter's own _delayLine/_delayLineOffset fields on every call,
		// with no reset in between - i.e. it is a genuinely stateful, streaming filter, unlike
		// Resampler.Resample above. Rebuilt (via ResetVadResampler) only if sourceSampleRate changes,
		// which SttEngine's actualSampleRate never does mid-session in practice (negotiated once in
		// StartCaptureOnMainThread - SttEngine.cs) but is handled below regardless.
		private FirFilter vadAntiAliasFilter;
		private int vadAntiAliasFilterRate = -1;
		// Fractional position, in filtered-sample index units (i.e. input-rate sample units, since the
		// anti-alias filter runs at the input rate before decimation), of the NEXT 16 kHz output sample
		// this resampler will emit. Carried across ProcessChunk calls for the recorder's lifetime (or
		// since the last rate change) so a continuous input stream yields a continuous, deterministic
		// output stream with no per-chunk restart (design item A).
		private double vadResamplePhase;
		private long vadResamplePrevIndex;
		private float vadResamplePrevFiltered;
		private bool vadResampleHavePrev;
		private double vadResampleRatio;

		private volatile bool disabled;
		private volatile bool loggedDisableReason;

		// Running count of 16 kHz samples fed to the VAD since the previous recognizer endpoint (or
		// since this recorder was created). Only the COUNT is needed now (used to clamp VAD speech
		// segments into the current span, and for utterance_duration_sec) - the previous rewrite kept
		// the actual 16 kHz float values in a List<float> here, but nothing reads them anymore now that
		// measurements are computed from spanSourceSamples (design item B) instead, so keeping only the
		// count avoids doubling this span's memory footprint for no reason.
		private int spanVadSampleCount;
		// Completed VAD speech segments for the current span, in the VAD's own running sample-index
		// space (SherpaOnnx.SpeechSegment.Start, ilspycmd: "public int Start => _start;"), so they can
		// be mapped back into spanVadSampleCount's index space once the span's absolute start index is
		// known, and from there into spanSourceSamples' index space (design item B).
		private readonly List<(long start, int length)> speechRangesGlobal = new List<(long, int)>();
		private long samplesFedToVadTotal;
		private long spanStartVadSampleIndex;

		// --- Original-source-rate measurement buffer (design item B) -----------------------------
		// A copy of each ProcessChunk's samples at the ORIGINAL capture rate (never resampled,
		// never touched by the VAD path above). All noise/speech level, band, flatness and tonal-peak
		// measurements below are computed from this buffer, not from the VAD's 16 kHz stream. Bounded
		// by the same sherpa endpoint-rule reasoning as the VAD path (~20s speech + headroom): a worst
		// case ~23s span at 48 kHz (the highest rate SttEngine's Microphone.GetDeviceCaps clamp is
		// likely to produce - SttEngine.cs StartCaptureOnMainThread) is ~1.1M floats (~4.4 MB),
		// replaced (not summed) every FinalizeUtterance call - acceptable for a debug-only feature.
		private readonly List<float> spanSourceSamples = new List<float>(48000 * 2);
		private int spanSourceSampleRate = -1;
		// Set if a later chunk in the current span reports a different sourceSampleRate than the
		// span's first chunk (design item B: "If sourceSampleRate changes mid-span (shouldn't happen),
		// handle it safely"). SttEngine negotiates actualSampleRate exactly once per capture session
		// (StartCaptureOnMainThread) so this is not expected to ever become true; if it does, this
		// span's measurement fields are safely reported as unavailable (see FinalizeUtterance) rather
		// than mixing samples from two different rates into one FFT, and rather than disabling the
		// whole recorder over what SttEngine itself calls out as unexpected.
		private bool spanSourceRateMismatch;

		// Per-source-rate FFT/window state (design item C), cached because a recorder normally only
		// ever sees one sourceSampleRate for its whole lifetime, but is keyed by rate in case
		// spanSourceSampleRate legitimately differs across separate utterances/spans.
		private readonly Dictionary<int, SpectralContext> spectralContexts = new Dictionary<int, SpectralContext>();

		private VoiceDebugRecorder(VoiceActivityDetector vad)
		{
			this.vad = vad;
		}

		// Factory instead of a public ctor: every failure path (missing model file, native call
		// throwing, NWaves/sherpa type-init failure) must return null rather than let a partially
		// constructed recorder escape (brief 13 item 6: "any failure ... disables debug collection for
		// the session, and never affects voice input").
		public static VoiceDebugRecorder TryCreate()
		{
			try
			{
				// Resolved the same way SttEngine.WorkerLoop resolves the STT model directory
				// (Path.Combine(VerbalCommandsMod.ModRootDir, "Models", ModelFolderName) in
				// SttEngine.cs) - Models\silero-vad\silero_vad.onnx, placed by the earlier B1 step
				// (COMPACT_2026-09-26.md: "mod\Models\silero-vad\silero_vad.onnx 643,854 B").
				string modelPath = Path.Combine(Path.Combine(VerbalCommandsMod.ModRootDir, "Models"), Path.Combine("silero-vad", "silero_vad.onnx"));
				if (!File.Exists(modelPath))
				{
					Log.Error(LogPrefix + "silero_vad.onnx not found at " + modelPath + "; voice debug data disabled for this session");
					return null;
				}

				// Field names/defaults verified via ilspycmd -t SherpaOnnx.VadModelConfig / -t
				// SherpaOnnx.SileroVadModelConfig against ThirdParty/sherpa-onnx/sherpa-onnx.dll,
				// 2026-09-26 (brief 13 Phase B1 decisions block quotes the same members). Threshold/
				// MinSilenceDuration/MinSpeechDuration/WindowSize/MaxSpeechDuration are left at the
				// struct's own default field initializers (0.5f, 0.5f, 0.25f, 512, 5f) - not sherpa's
				// STT endpoint rules, a separate tuning surface this brief does not ask to change. This
				// WindowSize=512 default is purely an internal VAD decision-window size and is
				// unrelated to the measurement FFT frame sizes computed per source rate below (design
				// item C) - the two used to be tied together (both hardcoded to 512 @ 16 kHz) before
				// this rewrite, which is no longer correct once measurement runs at the original
				// capture rate instead of 16 kHz.
				VadModelConfig config = new VadModelConfig
				{
					SileroVad = new SileroVadModelConfig { Model = modelPath },
					SampleRate = VadSampleRate,
					NumThreads = 1,
					Provider = "cpu",
					Debug = 0
				};
				VoiceActivityDetector vad = new VoiceActivityDetector(config, VadBufferSeconds);
				return new VoiceDebugRecorder(vad);
			}
			catch (Exception ex)
			{
				// Catches the sherpa-onnx native call throwing AND a NWaves/SherpaOnnx type-init
				// failure surfacing here as a TypeInitializationException the first time either
				// namespace's types are actually touched by this method (brief 13 item 6 explicitly
				// calls out "NWaves/zz7 load or type-init failure" as a case this must guard).
				Log.Error(LogPrefix + "init failed, voice debug data disabled for this session: " + ex.GetType().FullName + ": " + ex.Message);
				return null;
			}
		}

		// Called from SttEngine.WorkerLoop for every mono chunk already being fed to the sherpa
		// recognizer (brief 13 item 6: "fed a mono 16 kHz copy of the captured samples (NWaves
		// resample) on the existing worker thread"). sourceSampleRate is SttEngine's actualSampleRate.
		// `chunk` itself is NEVER mutated anywhere below (SttEngine.WorkerLoop feeds the same array to
		// stream.AcceptWaveform(actualSampleRate, chunk) right after this call - SttEngine.cs - so
		// recognition must see byte-for-byte the same data regardless of what this recorder does).
		public void ProcessChunk(float[] chunk, int sourceSampleRate)
		{
			if (disabled || chunk == null || chunk.Length == 0)
			{
				return;
			}
			try
			{
				// --- Design item B: keep an untouched, original-rate copy for measurement ---------
				if (spanSourceSampleRate == -1)
				{
					spanSourceSampleRate = sourceSampleRate;
				}
				else if (spanSourceSampleRate != sourceSampleRate)
				{
					spanSourceRateMismatch = true;
				}
				if (!spanSourceRateMismatch)
				{
					// List<float>.AddRange(float[]) copies each element by value - `chunk` is read,
					// never referenced or written.
					spanSourceSamples.AddRange(chunk);
				}

				// --- Design item A: streaming (stateful) resample to 16 kHz for the VAD only ------
				float[] mono16k;
				if (sourceSampleRate == VadSampleRate)
				{
					// Still copy rather than alias `chunk` (even though nothing here would mutate it)
					// so this path can never accidentally become an aliasing hazard if this method is
					// ever extended later.
					mono16k = (float[])chunk.Clone();
				}
				else
				{
					if (vadAntiAliasFilterRate != sourceSampleRate)
					{
						ResetVadResampler(sourceSampleRate);
					}
					mono16k = StreamingResampleToVad(chunk);
				}

				vad.AcceptWaveform(mono16k);
				spanVadSampleCount += mono16k.Length;
				samplesFedToVadTotal += mono16k.Length;
				DrainCompletedSegments();
			}
			catch (Exception ex)
			{
				Disable(ex);
			}
		}

		// Builds the streaming anti-alias filter + resets the fractional-phase decimation state for
		// `sourceSampleRate`. Cutoff is normalized against sourceSampleRate and capped well under 0.5
		// (DesignFilter.FirWinFdLp's Guard.AgainstInvalidRange requires (0,0.5] - ilspycmd -t
		// NWaves.Filters.Fda.DesignFilter, 2026-09-26) so this can never throw for any plausible mic
		// rate (8 kHz+).
		private void ResetVadResampler(int sourceSampleRate)
		{
			double cutoffHz = Math.Min(VadAntiAliasCutoffHz, 0.45 * Math.Min(sourceSampleRate, VadSampleRate));
			double cutoffNormalized = cutoffHz / sourceSampleRate;
			// DesignFilter.FirWinLp(int order, double frequency, WindowType window = Blackman) -
			// ilspycmd -t NWaves.Filters.Fda.DesignFilter, 2026-09-26. FirFilter(IEnumerable<double>)
			// casts the kernel to float (ilspycmd -t NWaves.Filters.Base.FirFilter).
			vadAntiAliasFilter = new FirFilter(DesignFilter.FirWinLp(VadAntiAliasFilterOrder, cutoffNormalized));
			vadAntiAliasFilterRate = sourceSampleRate;
			vadResamplePhase = 0.0;
			vadResamplePrevIndex = 0;
			vadResampleHavePrev = false;
			vadResampleRatio = (double)sourceSampleRate / VadSampleRate;
		}

		// Filters `chunk` through the persistent anti-alias FIR (state carried across calls, see
		// vadAntiAliasFilter comment) and decimates via a fractional-phase accumulator that is ALSO
		// carried across calls, so output-sample timing is a continuous function of total input
		// samples seen - not reset at chunk boundaries. Handles both integer ratios (48000->16000) and
		// non-integer ones (44100->16000) identically since the phase accumulator is a double, not an
		// integer counter.
		//
		// One-time startup note: the very first filtered sample of this resampler's lifetime is held
		// back (not emitted) because linear interpolation needs two consecutive filtered samples; this
		// adds one input-rate sample (<=1/44100 s = ~0.023 ms) of additional fixed latency on top of
		// the FIR's own group delay, folded into VadGroupDelaySamples' rounding tolerance rather than
		// compensated separately - judged negligible at that magnitude.
		private float[] StreamingResampleToVad(float[] chunk)
		{
			List<float> outSamples = new List<float>(chunk.Length * VadSampleRate / vadAntiAliasFilterRate + 2);
			for (int i = 0; i < chunk.Length; i++)
			{
				// NWaves.Filters.Base.FirFilter.Process(float) - stateful delay line, ilspycmd -t
				// NWaves.Filters.Base.FirFilter, 2026-09-26 (see class-level field comment above).
				float filtered = vadAntiAliasFilter.Process(chunk[i]);
				if (!vadResampleHavePrev)
				{
					vadResamplePrevFiltered = filtered;
					vadResampleHavePrev = true;
					vadResamplePrevIndex = 0;
					continue;
				}
				long curIndex = vadResamplePrevIndex + 1;
				while (vadResamplePhase <= curIndex)
				{
					double frac = vadResamplePhase - vadResamplePrevIndex;
					outSamples.Add(vadResamplePrevFiltered + (float)(frac * (filtered - vadResamplePrevFiltered)));
					vadResamplePhase += vadResampleRatio;
				}
				vadResamplePrevFiltered = filtered;
				vadResamplePrevIndex = curIndex;
			}
			return outSamples.ToArray();
		}

		// Input-rate sample count between a raw input sample and this recorder's reported 16 kHz VAD
		// output for it (design item A: "document how the filter's group delay is handled"). Zero in
		// the passthrough case (sourceSampleRate == VadSampleRate: no filter is used at all).
		private static int VadGroupDelaySamples(int sourceSampleRate)
		{
			return (sourceSampleRate == VadSampleRate) ? 0 : (VadAntiAliasFilterOrder - 1) / 2;
		}

		// Maps a VAD-index-space sample index (16 kHz) back to the source-rate index space of
		// spanSourceSamples (design item B), compensating for the anti-alias filter's group delay.
		private static int MapVadIndexToSource(long vadIndex, int sourceSampleRate, int groupDelaySamples)
		{
			double srcIndex = (double)vadIndex * sourceSampleRate / VadSampleRate;
			long rounded = (long)Math.Round(srcIndex) - groupDelaySamples;
			if (rounded < 0)
			{
				rounded = 0;
			}
			if (rounded > int.MaxValue)
			{
				rounded = int.MaxValue;
			}
			return (int)rounded;
		}

		private void DrainCompletedSegments()
		{
			while (!vad.IsEmpty())
			{
				SpeechSegment seg = vad.Front();
				// SpeechSegment.Start (ilspycmd -t SherpaOnnx.SpeechSegment: "public int Start =>
				// _start;") is the sample index, in the VAD's own continuous internal timeline, of the
				// first sample of this completed speech segment. This recorder never calls
				// vad.Reset(), so that timeline's origin is fixed at this recorder's creation and stays
				// comparable to samplesFedToVadTotal across the whole box session.
				speechRangesGlobal.Add((seg.Start, seg.Samples.Length));
				vad.Pop();
			}
		}

		// Called from SttEngine.WorkerLoop at every sherpa recognizer endpoint (brief 13 item 2: "one
		// record per recognizer endpoint"). finalText may be empty (a silence-only endpoint still gets
		// a noise-only record). Returns immediately (measurement is in-memory/CPU-only); the JSON-line
		// write itself is dispatched to a background task (brief 13 item 2: "off the main thread").
		public void FinalizeUtterance(string finalText, string sttModel, string deviceRecordId)
		{
			if (disabled)
			{
				return;
			}
			try
			{
				// Flush() forces out any speech segment still open at the utterance boundary without
				// resetting VAD state (SherpaOnnx.VoiceActivityDetector.Flush(), ilspycmd; distinct
				// from Reset(), which this recorder never calls - see DrainCompletedSegments comment).
				vad.Flush();
				DrainCompletedSegments();

				// "utterance duration" (brief 13 item 2): defined here as the total duration of the
				// VAD-detected speech samples in this span, not the whole noise+speech span (which
				// also includes the leading silence sherpa waited through before the utterance
				// started) - unchanged semantics from before this rewrite. Computed directly as the sum
				// of each (clamped) speech range's length in the 16 kHz VAD-index space: sherpa/Silero
				// emit speech segments in increasing, non-overlapping start order (DrainCompletedSegments
				// comment above - "the VAD's own continuous internal timeline"), so summing clamped
				// range lengths is equivalent to counting marked samples in that space one at a time.
				long speechSamples16k = 0;
				foreach ((long start, int length) seg in speechRangesGlobal)
				{
					long localStart = seg.start - spanStartVadSampleIndex;
					long localEndExclusive = localStart + seg.length;
					long from = Math.Max(0L, localStart);
					long to = Math.Min((long)spanVadSampleCount, localEndExclusive);
					if (to > from)
					{
						speechSamples16k += to - from;
					}
				}

				int nSrc = spanSourceRateMismatch ? 0 : spanSourceSamples.Count;
				int? measureRate = (nSrc > 0) ? spanSourceSampleRate : (int?)null;
				SpectralContext ctx = measureRate.HasValue ? GetSpectralContext(measureRate.Value) : null;

				// --- Design item B: map VAD speech segments into spanSourceSamples' index space ---
				bool[] isSpeechSrc = (nSrc > 0) ? new bool[nSrc] : Array.Empty<bool>();
				if (nSrc > 0)
				{
					int groupDelay = VadGroupDelaySamples(measureRate.Value);
					foreach ((long start, int length) seg in speechRangesGlobal)
					{
						long localStart = seg.start - spanStartVadSampleIndex;
						long localEndExclusive = localStart + seg.length;
						long from16k = Math.Max(0L, localStart);
						long to16k = Math.Min((long)spanVadSampleCount, localEndExclusive);
						if (to16k <= from16k)
						{
							continue;
						}
						int srcFrom = MapVadIndexToSource(from16k, measureRate.Value, groupDelay);
						int srcTo = MapVadIndexToSource(to16k, measureRate.Value, groupDelay);
						srcFrom = Math.Max(0, Math.Min(nSrc, srcFrom));
						srcTo = Math.Max(0, Math.Min(nSrc, srcTo));
						for (int k = srcFrom; k < srcTo; k++)
						{
							isSpeechSrc[k] = true;
						}
					}
				}

				JObject o = new JObject
				{
					["utc"] = DateTime.UtcNow.ToString("o"),
					["recognized_text"] = finalText ?? "",
					["stt_model"] = sttModel,
					["device_record_id"] = deviceRecordId,
					["utterance_duration_sec"] = speechSamples16k / (double)VadSampleRate
				};

				// Design item G: self-describing measurement fields. Null when this span has no usable
				// source-rate samples (empty span or a mid-span rate mismatch - see spanSourceRateMismatch).
				o["measure_sample_rate"] = measureRate.HasValue ? (JToken)measureRate.Value : JValue.CreateNull();
				o["fft_frame_size"] = (ctx != null) ? (JToken)ctx.FrameSize : JValue.CreateNull();
				o["fft_bin_hz"] = (ctx != null) ? (JToken)ctx.BinHz : JValue.CreateNull();

				if (nSrc == 0)
				{
					string fallbackNote = spanSourceRateMismatch
						? "source sample rate changed mid-span; measurement skipped"
						: null;
					AddLevelFields(o, "noise", 0, null, fallbackNote ?? "no non-speech (VAD) samples in this span");
					AddLevelFields(o, "speech", 0, null, fallbackNote ?? "no VAD-detected speech samples in this span");
					o["snr_db"] = JValue.CreateNull();
					AddBandFields(o, "noise", null, null);
					AddBandFields(o, "speech", null, null);
					AddNoiseSpectralFields(o, null, null, fallbackNote ?? "no non-speech (VAD) samples in this span");
				}
				else
				{
					int noiseCount, speechCount;
					double noiseSumSquares, speechSumSquares;
					AccumulateRms(spanSourceSamples, isSpeechSrc, false, out noiseCount, out noiseSumSquares);
					AccumulateRms(spanSourceSamples, isSpeechSrc, true, out speechCount, out speechSumSquares);
					double? noiseDb = (noiseCount > 0) ? RmsDbfsFrom(noiseCount, noiseSumSquares) : (double?)null;
					double? speechDb = (speechCount > 0) ? RmsDbfsFrom(speechCount, speechSumSquares) : (double?)null;
					AddLevelFields(o, "noise", noiseCount, noiseDb, "no non-speech (VAD) samples in this span");
					AddLevelFields(o, "speech", speechCount, speechDb, "no VAD-detected speech samples in this span");
					o["snr_db"] = (noiseDb.HasValue && speechDb.HasValue) ? (JToken)(speechDb.Value - noiseDb.Value) : JValue.CreateNull();

					// Design item D: contiguous runs only, frames never straddle a noise/speech boundary.
					int noiseFrameCount, speechFrameCount;
					float[] noiseSpectrum = AverageFramedPowerSpectrumForRuns(spanSourceSamples, isSpeechSrc, false, ctx, out noiseFrameCount);
					float[] speechSpectrum = AverageFramedPowerSpectrumForRuns(spanSourceSamples, isSpeechSrc, true, ctx, out speechFrameCount);

					AddBandFields(o, "noise", noiseSpectrum, ctx);
					AddBandFields(o, "speech", speechSpectrum, ctx);

					double frameMs = ctx.FrameSize * 1000.0 / measureRate.Value;
					string insufficientNote = "insufficient samples (<" + ctx.FrameSize + " at " + measureRate.Value +
						"Hz, " + frameMs.ToString("0.#") + "ms) for spectral analysis";
					AddNoiseSpectralFields(o, noiseSpectrum, ctx, insufficientNote);
				}

				// Prefs.VolumeMaster / VolumeGame / VolumeMusic (sources/RimWorldDecompiled/Verse/
				// Prefs.cs:16-30, :32-46, :48-62) - speaker-leakage context for the judge (brief 13
				// Phase B section "RimWorld's master / game / music volume settings at that moment").
				o["volume_master"] = Prefs.VolumeMaster;
				o["volume_game"] = Prefs.VolumeGame;
				o["volume_music"] = Prefs.VolumeMusic;

				WriteLineAsync(o);
			}
			catch (Exception ex)
			{
				Disable(ex);
			}
			finally
			{
				// Reset the span regardless of success, so one bad utterance cannot poison the next.
				spanVadSampleCount = 0;
				speechRangesGlobal.Clear();
				spanStartVadSampleIndex = samplesFedToVadTotal;
				spanSourceSamples.Clear();
				spanSourceSampleRate = -1;
				spanSourceRateMismatch = false;
			}
		}

		private static void AddLevelFields(JObject o, string prefix, int count, double? rmsDbfs, string emptyNote)
		{
			if (count == 0)
			{
				o[prefix + "_level_dbfs"] = JValue.CreateNull();
				o[prefix + "_note"] = emptyNote;
				return;
			}
			o[prefix + "_level_dbfs"] = rmsDbfs.Value;
			o[prefix + "_note"] = JValue.CreateNull();
		}

		// dBFS = 20*log10(rms) against full scale 1.0f: Unity AudioClip/Microphone samples are
		// documented to be normalized floats in [-1, 1] (UnityEngine.AudioClip.GetData - the same
		// samples SttEngine.Poll already reads this way, per SttEngine.cs), so 1.0 is full scale by
		// construction; a small epsilon avoids log10(0) for a true silence buffer.
		private const double DbfsEpsilon = 1e-10;

		private static void AccumulateRms(List<float> samples, bool[] isSpeech, bool wantSpeech, out int count, out double sumSquares)
		{
			count = 0;
			sumSquares = 0.0;
			int n = samples.Count;
			for (int i = 0; i < n; i++)
			{
				if (isSpeech[i] != wantSpeech)
				{
					continue;
				}
				double v = samples[i];
				sumSquares += v * v;
				count++;
			}
		}

		private static double RmsDbfsFrom(int count, double sumSquares)
		{
			double rms = Math.Sqrt(sumSquares / count);
			return 20.0 * Math.Log10(Math.Max(rms, DbfsEpsilon));
		}

		// Band-energy rule (brief 13 item 2 "band energies (dB) for <300, 300-3400, 3400-8000 Hz"),
		// restated per this rewrite's design item E: bins are averaged (mean power, not summed - does
		// not scale with window length) and converted to dB (10*log10(power)); the low band additionally
		// excludes bin 0 (DC) via the `hz > 0.0` guard below (design item E: mic DC offset previously
		// inflated band_low_db by including the DC bin). Returns null (not NaN) when the band has zero
		// usable bins, per design item E's "Guard BandMeanDb's count==0 path so it can't emit NaN".
		private static double? BandMeanDb(float[] avgSpectrum, SpectralContext ctx, double loHz, double hiHz)
		{
			double sum = 0.0;
			int count = 0;
			for (int i = 0; i < avgSpectrum.Length; i++)
			{
				double hz = i * ctx.BinHz;
				if (hz > 0.0 && hz >= loHz && hz < hiHz)
				{
					sum += avgSpectrum[i];
					count++;
				}
			}
			if (count == 0)
			{
				return null;
			}
			return 10.0 * Math.Log10(Math.Max(sum / count, DbfsEpsilon));
		}

		private static void AddBandFields(JObject o, string prefix, float[] avgSpectrum, SpectralContext ctx)
		{
			if (avgSpectrum == null)
			{
				o[prefix + "_band_low_db"] = JValue.CreateNull();
				o[prefix + "_band_mid_db"] = JValue.CreateNull();
				o[prefix + "_band_high_db"] = JValue.CreateNull();
				return;
			}
			double? low = BandMeanDb(avgSpectrum, ctx, 0.0, 300.0);
			double? mid = BandMeanDb(avgSpectrum, ctx, 300.0, 3400.0);
			double? high = BandMeanDb(avgSpectrum, ctx, 3400.0, 8000.0);
			o[prefix + "_band_low_db"] = low.HasValue ? (JToken)low.Value : JValue.CreateNull();
			o[prefix + "_band_mid_db"] = mid.HasValue ? (JToken)mid.Value : JValue.CreateNull();
			o[prefix + "_band_high_db"] = high.HasValue ? (JToken)high.Value : JValue.CreateNull();
		}

		// Noise-only spectral fields (flatness + tonal peaks) - unchanged field scope from before this
		// rewrite (speech never got these two fields either, only band fields; that asymmetry is
		// preserved here rather than added to, per "do not add features beyond the list").
		private static void AddNoiseSpectralFields(JObject o, float[] avgSpectrum, SpectralContext ctx, string insufficientNote)
		{
			if (avgSpectrum == null)
			{
				o["noise_spectral_flatness"] = JValue.CreateNull();
				o["noise_tonal_peaks"] = new JArray();
				// AddLevelFields already set noise_note to a real string if there were zero noise
				// samples at all; only overwrite the still-null case, where there were SOME noise
				// samples but fewer than one FFT frame's worth (or no source-rate samples at all).
				if (o["noise_note"] == null || o["noise_note"].Type == JTokenType.Null)
				{
					o["noise_note"] = insufficientNote;
				}
				return;
			}

			int cutoff = ctx.CutoffBinExclusive; // bins [0, cutoff) all have hz < 8000.
			// Slice INCLUDING bin 0 (DC), not starting from bin 1: NWaves.Features.Spectral.Flatness's
			// loop starts at i=1 (skips index 0) and divides by spectrum.Length (ilspycmd -t
			// NWaves.Features.Spectral, 2026-09-26). Slicing from bin 0 lets that internal skip do
			// exactly the DC exclusion design item E asks for; slicing from bin 1 instead would have
			// additionally dropped the first real (lowest-frequency, non-DC) bin.
			float[] flatnessSlice = new float[cutoff];
			Array.Copy(avgSpectrum, flatnessSlice, cutoff);
			o["noise_spectral_flatness"] = Spectral.Flatness(flatnessSlice);

			// Design item F: top-3 local maxima by power, greedy min-separation of 3 bins (Hann main
			// lobe half-width is 2 bins, so a separation of 3 guarantees selected peaks are outside
			// each other's main lobe), restricted to 0 < f < 8000 Hz.
			List<(int bin, float power)> localMaxima = new List<(int, float)>();
			for (int i = 1; i < cutoff - 1; i++)
			{
				if (avgSpectrum[i] > avgSpectrum[i - 1] && avgSpectrum[i] > avgSpectrum[i + 1])
				{
					localMaxima.Add((i, avgSpectrum[i]));
				}
			}
			localMaxima.Sort((a, b) => b.power.CompareTo(a.power));
			List<int> selectedBins = new List<int>();
			JArray peaks = new JArray();
			foreach ((int bin, float power) cand in localMaxima)
			{
				if (selectedBins.Count >= 3)
				{
					break;
				}
				bool tooClose = false;
				foreach (int selected in selectedBins)
				{
					if (Math.Abs(cand.bin - selected) < 3)
					{
						tooClose = true;
						break;
					}
				}
				if (tooClose)
				{
					continue;
				}
				selectedBins.Add(cand.bin);
				double peakDb = 10.0 * Math.Log10(Math.Max((double)cand.power, DbfsEpsilon));
				double? prominenceDb = ProminenceDb(avgSpectrum, cand.bin, cutoff, peakDb);
				peaks.Add(new JObject
				{
					["hz"] = cand.bin * ctx.BinHz,
					["db"] = peakDb,
					["prominence_db"] = prominenceDb.HasValue ? (JToken)prominenceDb.Value : JValue.CreateNull()
				});
			}
			o["noise_tonal_peaks"] = peaks;
		}

		// Design item F: prominence_db = peak dB minus the median dB of bins within +/-8 bins of the
		// peak, EXCLUDING the +/-2 bins immediately around it. Not thresholded - always reported when
		// computable. Null only in the degenerate case where every candidate context bin falls outside
		// [0, cutoffExclusive) (not expected for any real frame size used here).
		private static double? ProminenceDb(float[] avgSpectrum, int peakBin, int cutoffExclusive, double peakDb)
		{
			List<double> contextDb = new List<double>();
			for (int offset = -8; offset <= 8; offset++)
			{
				if (offset >= -2 && offset <= 2)
				{
					continue;
				}
				int bin = peakBin + offset;
				if (bin < 0 || bin >= cutoffExclusive)
				{
					continue;
				}
				contextDb.Add(10.0 * Math.Log10(Math.Max((double)avgSpectrum[bin], DbfsEpsilon)));
			}
			if (contextDb.Count == 0)
			{
				return null;
			}
			contextDb.Sort();
			int mid = contextDb.Count / 2;
			double median = (contextDb.Count % 2 == 0)
				? (contextDb[mid - 1] + contextDb[mid]) / 2.0
				: contextDb[mid];
			return peakDb - median;
		}

		// Per-source-rate FFT frame size, Hann window, and window-power correction (design item C).
		private sealed class SpectralContext
		{
			public int FrameSize;
			public int HopSize;
			public RealFft Fft;
			public float[] Window;
			public float WindowPowerCorrection;
			public double BinHz;
			public int CutoffBinExclusive;
		}

		private SpectralContext GetSpectralContext(int rate)
		{
			SpectralContext ctx;
			if (spectralContexts.TryGetValue(rate, out ctx))
			{
				return ctx;
			}
			int frameSize = NearestPowerOfTwoFrameSize(rate);
			int specLen = frameSize / 2 + 1;
			double binHz = (double)rate / frameSize;
			int cutoffBinExclusive = specLen;
			for (int i = 0; i < specLen; i++)
			{
				if (i * binHz >= 8000.0)
				{
					cutoffBinExclusive = i;
					break;
				}
			}
			// NWaves.Windows.Window.OfType(WindowType.Hann, length) / Window.Hann(length) - ilspycmd -t
			// NWaves.Windows.Window, 2026-09-26: "0.5 * (1.0 - Math.Cos(i * (2*PI/(length-1))))".
			float[] window = NWaves.Windows.Window.OfType(WindowType.Hann, frameSize);
			double windowPowerSum = 0.0;
			for (int i = 0; i < window.Length; i++)
			{
				windowPowerSum += (double)window[i] * window[i];
			}
			// Window-power correction (design item C "state the normalization ... you applied"):
			// windowing a frame attenuates its average power by mean(window^2) relative to a
			// rectangular window; RealFft.PowerSpectrum(..., normalize:true) already divides by
			// _fftSize=frameSize/2 (ilspycmd -t NWaves.Transforms.RealFft, 2026-09-26 - unchanged from
			// before this rewrite), so multiplying each windowed frame's power spectrum by
			// frameSize/sum(window^2) (= 1/mean(window^2)) on top of that restores the same absolute
			// power scale a rectangular-window frame would have produced, keeping band/flatness/peak dB
			// values comparable across differently-sized frames (different source rates) and consistent
			// with the pre-rewrite (rectangular-window) dB scale.
			float windowPowerCorrection = (float)(frameSize / Math.Max(windowPowerSum, 1e-12));
			ctx = new SpectralContext
			{
				FrameSize = frameSize,
				HopSize = frameSize / 2,
				// NWaves.Transforms.RealFft(int size) requires size to be a power of two
				// (Guard.AgainstNotPowerOfTwo - ilspycmd -t NWaves.Transforms.RealFft, 2026-09-26).
				Fft = new RealFft(frameSize),
				Window = window,
				WindowPowerCorrection = windowPowerCorrection,
				BinHz = binHz,
				CutoffBinExclusive = cutoffBinExclusive
			};
			spectralContexts[rate] = ctx;
			return ctx;
		}

		// Design item C: FFT frame length at the source rate should be ~32 ms, rounded to the NEAREST
		// power of two (RealFft requires a power of two - see GetSpectralContext). NWaves.Utils.
		// MathUtils.NextPowerOfTwo(int) only rounds UP ("Returns next power of 2 closest to the given
		// number" - but its body, ilspycmd -t NWaves.Utils.MathUtils, 2026-09-26, is
		// "(int)Math.Pow(2.0, Math.Ceiling(Math.Log(n,2.0)))", i.e. a ceiling, not a true nearest), so
		// "nearest" is picked here by comparing that ceiling power against its half. At 48000 Hz,
		// 48000*0.032=1536.0 is exactly the midpoint between 1024 and 2048 (ties broken toward the
		// larger size, 2048); at 44100 Hz, 44100*0.032=1411.2 is nearer to 1024; at 16000 Hz,
		// 16000*0.032=512.0 is itself already a power of two, reproducing the pre-rewrite 512 frame
		// size at 16 kHz.
		private static int NearestPowerOfTwoFrameSize(int rate)
		{
			double target = rate * 0.032;
			int ceilPow = MathUtils.NextPowerOfTwo(Math.Max(1, (int)Math.Ceiling(target)));
			int floorPow = (ceilPow > 1) ? ceilPow / 2 : 1;
			double distCeil = Math.Abs(ceilPow - target);
			double distFloor = Math.Abs(floorPow - target);
			return (distFloor < distCeil) ? floorPow : ceilPow;
		}

		// Design item D: contiguous runs only. Scans isSpeech for maximal runs matching `wantSpeech`;
		// frames are extracted with 50% overlap (ctx.HopSize = ctx.FrameSize/2) entirely within a single
		// run, so no frame ever straddles a noise/speech (or run/run) boundary; runs shorter than one
		// frame contribute nothing. Each frame's mean is subtracted before windowing (design item C),
		// then Hann-windowed, then power-spectrum'd with the window-power correction applied, then
		// averaged across all frames from all runs of that class.
		private static float[] AverageFramedPowerSpectrumForRuns(List<float> src, bool[] isSpeech, bool wantSpeech, SpectralContext ctx, out int frameCount)
		{
			int n = src.Count;
			int frameSize = ctx.FrameSize;
			int hop = ctx.HopSize;
			int specLen = frameSize / 2 + 1;
			float[] sum = new float[specLen];
			float[] frame = new float[frameSize];
			float[] spectrum = new float[specLen];
			frameCount = 0;
			int i = 0;
			while (i < n)
			{
				if (isSpeech[i] != wantSpeech)
				{
					i++;
					continue;
				}
				int runStart = i;
				while (i < n && isSpeech[i] == wantSpeech)
				{
					i++;
				}
				int runLength = i - runStart;
				if (runLength < frameSize)
				{
					continue;
				}
				int framesInRun = 1 + (runLength - frameSize) / hop;
				for (int f = 0; f < framesInRun; f++)
				{
					int frameStart = runStart + f * hop;
					// List<T>.CopyTo(int index, T[] array, int arrayIndex, int count) - same member the
					// pre-rewrite code already used here.
					src.CopyTo(frameStart, frame, 0, frameSize);
					float mean = 0f;
					for (int k = 0; k < frameSize; k++)
					{
						mean += frame[k];
					}
					mean /= frameSize;
					for (int k = 0; k < frameSize; k++)
					{
						frame[k] -= mean;
					}
					// NWaves.Windows.WindowExtensions.ApplyWindow(float[], float[]) multiplies in place
					// (ilspycmd -t NWaves.Windows.WindowExtensions, 2026-09-26) - applied only to this
					// method's own local `frame` buffer, never to `src` or the caller's chunk arrays.
					frame.ApplyWindow(ctx.Window);
					// NWaves.Transforms.RealFft.PowerSpectrum(float[], float[], bool normalize = true) -
					// same member the pre-rewrite code already used here.
					ctx.Fft.PowerSpectrum(frame, spectrum, true);
					for (int k = 0; k < specLen; k++)
					{
						sum[k] += spectrum[k] * ctx.WindowPowerCorrection;
					}
					frameCount++;
				}
			}
			if (frameCount == 0)
			{
				return null;
			}
			for (int k = 0; k < specLen; k++)
			{
				sum[k] /= frameCount;
			}
			return sum;
		}

		private void WriteLineAsync(JObject o)
		{
			string line = o.ToString(Formatting.None);
			Task.Run(delegate
			{
				try
				{
					// Same debug folder as Probe_Brief13.cs used for probe.opus and DeviceIdResolver.cs
					// uses for device_records.jsonl (brief 13 user rule 5).
					string dir = Path.Combine(Path.Combine(GenFilePaths.SaveDataFolderPath, "VerbalCommands"), "debug");
					Directory.CreateDirectory(dir);
					string path = Path.Combine(dir, "voice_debug.jsonl");
					File.AppendAllText(path, line + "\n", Encoding.UTF8);
				}
				catch (Exception ex)
				{
					Disable(ex, "failed to write voice_debug.jsonl");
				}
			});
		}

		private void Disable(Exception ex, string what = "unhandled exception")
		{
			disabled = true;
			if (!loggedDisableReason)
			{
				loggedDisableReason = true;
				Log.Error(LogPrefix + what + ", voice debug data disabled for this session: " + ex.GetType().FullName + ": " + ex.Message);
			}
		}

		// Called from SttEngine.WorkerLoop alongside stream.Dispose()/rec.Dispose() (brief 13 item 6:
		// "Dispose the VAD properly"). SherpaOnnx.VoiceActivityDetector.Dispose() (ilspycmd) calls the
		// native destroy function and suppresses the finalizer.
		public void Dispose()
		{
			try { vad.Dispose(); } catch (Exception ex) { Log.Error(LogPrefix + "exception disposing VAD: " + ex); }
		}
	}
}
