using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace AIFarm.Server.Speech;

/// <summary>The "Speech" configuration section: the voice NPCs speak with.</summary>
public sealed class SpeechOptions
{
	public const string Section = "Speech";

	/// <summary>
	/// Folder of a Kokoro text-to-speech model for Sherpa-ONNX: Kokoro-82M v1.0
	/// (kokoro-multi-lang-v1_0, the default) or the older v0.19. It holds model*.onnx, voices.bin,
	/// tokens.txt, espeak-ng-data and, for v1.0, lexicon-*.txt and dict/. Empty, or missing, means
	/// speech is text only (bubbles).
	/// </summary>
	public string ModelDir { get; set; } = "";

	/// <summary>CPU threads the voice may use.</summary>
	public int Threads { get; set; } = 4;

	/// <summary>Speaking rate (1 = normal).</summary>
	public float Speed { get; set; } = 1f;

	/// <summary>
	/// The pause between sentences, in milliseconds. Kokoro pads every sentence with about 200 ms
	/// of silence (80 before, 120 after), which sounds like a long stop at each full stop; that
	/// padding is trimmed and this much is put back instead.
	/// </summary>
	public int SentencePauseMs { get; set; } = 90;
}

/// <summary>
/// Turns text into speech with Kokoro (via Sherpa-ONNX), handing audio back sentence by
/// sentence as it's made so it can be streamed before the whole line is done. One line at a
/// time: the model uses every thread it's given.
/// </summary>
public sealed class VoiceSynthesizer : IDisposable
{
	private readonly OfflineTts? _tts;
	private readonly SemaphoreSlim _oneAtATime = new(1, 1);
	private readonly float _speed;
	private readonly int _sentencePauseMs;

	public VoiceSynthesizer(IOptions<SpeechOptions> options, ILogger<VoiceSynthesizer> logger)
	{
		var settings = options.Value;
		_speed = settings.Speed;
		_sentencePauseMs = Math.Max(0, settings.SentencePauseMs);
		var dir = settings.ModelDir;
		var model = string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)
			? null
			: Directory.GetFiles(dir, "model*.onnx").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
		if (model is null)
		{
			logger.LogInformation("No voice model (Speech:ModelDir is '{Dir}'): NPCs speak in text only", dir);
			return;
		}
		var config = new OfflineTtsConfig();
		config.Model.Kokoro.Model = model;
		config.Model.Kokoro.Voices = Path.Combine(dir, "voices.bin");
		config.Model.Kokoro.Tokens = Path.Combine(dir, "tokens.txt");
		config.Model.Kokoro.DataDir = Path.Combine(dir, "espeak-ng-data");
		config.Model.Kokoro.Lang = "en-us";
		// Kokoro v1.0 reads English from lexicons (with espeak-ng for words they lack).
		var lexicons = new[] { "lexicon-us-en.txt", "lexicon-gb-en.txt" }.Select(f => Path.Combine(dir, f)).Where(File.Exists).ToList();
		if (lexicons.Count > 0)
		{
			config.Model.Kokoro.Lexicon = string.Join(",", lexicons);
		}
		if (Directory.Exists(Path.Combine(dir, "dict")))
		{
			config.Model.Kokoro.DictDir = Path.Combine(dir, "dict");
		}
		config.Model.NumThreads = settings.Threads;
		_tts = new OfflineTts(config);
		SampleRate = _tts.SampleRate;
		Speakers = _tts.NumSpeakers;
		logger.LogInformation("Voice model {Model}: {Speakers} voices at {Rate} Hz", model, Speakers, SampleRate);
	}

	public bool Available => _tts is not null;

	public int SampleRate { get; }

	/// <summary>How many voices the loaded model has (0 without one).</summary>
	public int Speakers { get; }

	/// <summary>A voice's speaker id from its name (e.g. "am_michael") or number; unknown ones give the first voice.</summary>
	public int Resolve(string? voice) => KokoroVoices.Resolve(voice, Speakers);

	/// <summary>
	/// Speaks text in a voice (a Kokoro speaker id), calling onAudio with each piece of 16-bit
	/// PCM as it's made. Runs on a worker thread.
	/// </summary>
	public async Task SpeakAsync(string text, int voice, Action<byte[]> onAudio, CancellationToken cancellationToken)
	{
		if (_tts is null)
		{
			return;
		}
		await _oneAtATime.WaitAsync(cancellationToken);
		try
		{
			await Task.Run(() =>
			{
				var first = true;
				_tts.GenerateWithCallback(text, _speed, voice, (samples, count) =>
				{
					var floats = new float[count];
					Marshal.Copy(samples, floats, 0, count);
					// Each callback is one sentence: take off Kokoro's padding, and put a short pause
					// between sentences instead (none before the first).
					var sentence = TrimSilence(floats, SampleRate);
					if (!first)
					{
						sentence = [.. new float[SampleRate * _sentencePauseMs / 1000], .. sentence];
					}
					first = false;
					onAudio(ToPcm16(sentence));
					return cancellationToken.IsCancellationRequested ? 0 : 1; // 0 stops it
				});
			}, cancellationToken);
		}
		finally
		{
			_oneAtATime.Release();
		}
	}

	/// <summary>Below this a sample counts as silence (about -48 dB).</summary>
	public const float SilenceThreshold = 0.004f;

	/// <summary>
	/// The audio without the near-silence at either end, leaving a few milliseconds of margin so
	/// soft starts and fades aren't clipped. All silence gives back the input.
	/// </summary>
	public static float[] TrimSilence(float[] samples, int sampleRate, int marginMs = 12)
	{
		var first = Array.FindIndex(samples, s => Math.Abs(s) > SilenceThreshold);
		if (first < 0)
		{
			return samples;
		}
		var last = Array.FindLastIndex(samples, s => Math.Abs(s) > SilenceThreshold);
		var margin = sampleRate * marginMs / 1000;
		var start = Math.Max(0, first - margin);
		var end = Math.Min(samples.Length, last + 1 + margin);
		return samples[start..end];
	}

	private static byte[] ToPcm16(float[] samples)
	{
		var bytes = new byte[samples.Length * 2];
		for (var i = 0; i < samples.Length; i++)
		{
			var value = (short)Math.Clamp((int)(samples[i] * short.MaxValue), short.MinValue, short.MaxValue);
			bytes[2 * i] = (byte)value;
			bytes[2 * i + 1] = (byte)(value >> 8);
		}
		return bytes;
	}

	public void Dispose()
	{
		// OfflineTts frees its native memory in a finalizer; nothing else to release.
		_oneAtATime.Dispose();
	}
}
