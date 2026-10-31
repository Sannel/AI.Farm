namespace AIFarm.Server.Speech;

/// <summary>
/// Kokoro voice names and the ids Sherpa-ONNX wants, for the two models we know (read from the
/// models' own speaker tables). Voice names are
/// <c>&lt;language&gt;&lt;f|m&gt;_&lt;name&gt;</c>: af_ is American female, am_ American male, bf_ / bm_
/// British; the rest are other languages.
/// </summary>
public static class KokoroVoices
{
	/// <summary>Kokoro-82M v1.0 (kokoro-multi-lang-v1_0): 54 voices.</summary>
	private static readonly string[] V1 =
	[
		"af_alloy", "af_aoede", "af_bella", "af_heart", "af_jessica", "af_kore", "af_nicole", "af_nova", "af_river",
		"af_sarah", "af_sky", "am_adam", "am_echo", "am_eric", "am_fenrir", "am_liam", "am_michael", "am_onyx",
		"am_puck", "am_santa", "bf_alice", "bf_emma", "bf_isabella", "bf_lily", "bm_daniel", "bm_fable", "bm_george",
		"bm_lewis", "ef_dora", "em_alex", "ff_siwis", "hf_alpha", "hf_beta", "hm_omega", "hm_psi", "if_sara",
		"im_nicola", "jf_alpha", "jf_gongitsune", "jf_nezumi", "jf_tebukuro", "jm_kumo", "pf_dora", "pm_alex",
		"pm_santa", "zf_xiaobei", "zf_xiaoni", "zf_xiaoxiao", "zf_xiaoyi", "zm_yunjian", "zm_yunxi", "zm_yunxia",
		"zm_yunyang", "em_santa",
	];

	/// <summary>Kokoro v0.19 (kokoro-en-v0_19): 11 English voices.</summary>
	private static readonly string[] Legacy =
	[
		"af", "af_bella", "af_nicole", "af_sarah", "af_sky", "am_adam", "am_michael", "bf_emma", "bf_isabella",
		"bm_george", "bm_lewis",
	];

	/// <summary>The voice names a model with this many speakers has, in id order (empty if unknown).</summary>
	public static IReadOnlyList<string> Names(int speakers) => speakers switch
	{
		54 => V1,
		11 => Legacy,
		_ => [],
	};

	/// <summary>
	/// A voice's speaker id from its name ("am_michael") or a plain number. Unknown names, and
	/// numbers out of range, give <paramref name="fallback"/>.
	/// </summary>
	public static int Resolve(string? voice, int speakers, int fallback = 0)
	{
		voice = voice?.Trim();
		if (string.IsNullOrEmpty(voice))
		{
			return fallback;
		}
		if (int.TryParse(voice, out var id))
		{
			return id >= 0 && id < Math.Max(speakers, 1) ? id : fallback;
		}
		var index = Names(speakers).ToList().FindIndex(n => n.Equals(voice, StringComparison.OrdinalIgnoreCase));
		return index >= 0 ? index : fallback;
	}
}
