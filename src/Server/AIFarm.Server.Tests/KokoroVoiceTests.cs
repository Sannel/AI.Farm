using AIFarm.Server.Speech;

namespace AIFarm.Server.Tests;

/// <summary>Kokoro voices are chosen by name; the ids differ between model versions.</summary>
public sealed class KokoroVoiceTests
{
	[Theory]
	[InlineData("am_michael", 54, 16)] // Kokoro-82M v1.0
	[InlineData("AF_Heart", 54, 3)]
	[InlineData("bm_george", 54, 26)]
	[InlineData("bm_george", 11, 9)] // the older v0.19 numbers them differently
	[InlineData("am_michael", 11, 6)]
	public void NamesBecomeTheModelsSpeakerIds(string name, int speakers, int expected) =>
		Assert.Equal(expected, KokoroVoices.Resolve(name, speakers));

	[Theory]
	[InlineData("7", 54, 7)]
	[InlineData("99", 54, 0)] // out of range
	[InlineData("nobody", 54, 0)] // unknown name
	[InlineData("", 54, 0)]
	[InlineData(null, 54, 0)]
	[InlineData("am_michael", 0, 0)] // no voice model loaded
	public void NumbersPassThroughAndAnythingElseFallsBack(string? voice, int speakers, int expected) =>
		Assert.Equal(expected, KokoroVoices.Resolve(voice, speakers));

	[Fact]
	public void EveryVoiceNameIsUniqueAndTheTablesHaveTheRightSizes()
	{
		Assert.Equal(54, KokoroVoices.Names(54).Count);
		Assert.Equal(11, KokoroVoices.Names(11).Count);
		Assert.Equal(54, KokoroVoices.Names(54).Distinct().Count());
		Assert.Empty(KokoroVoices.Names(3));
	}
}

/// <summary>The padding Kokoro puts round each sentence is trimmed, so full stops don't sound like long stops.</summary>
public sealed class SentencePauseTests
{
	private const int Rate = 24000;

	private static float[] Tone(int milliseconds) => [.. Enumerable.Range(0, Rate * milliseconds / 1000).Select(i => 0.3f * MathF.Sin(i * 0.2f))];

	private static float[] Silence(int milliseconds) => new float[Rate * milliseconds / 1000];

	[Fact]
	public void SilenceAtBothEndsIsTrimmedLeavingASmallMargin()
	{
		var padded = new[] { Silence(80), Tone(500), Silence(120) }.SelectMany(x => x).ToArray(); // like Kokoro's sentences

		var trimmed = Speech.VoiceSynthesizer.TrimSilence(padded, Rate);

		Assert.InRange(trimmed.Length * 1000 / Rate, 500 + 20, 500 + 28); // the sound, plus 12 ms either side
		Assert.Equal(padded.Sum(x => x * x), trimmed.Sum(x => x * x), 1); // none of the sound was cut off
	}

	[Fact]
	public void QuietSoundsInsideAreKeptAndAllSilenceIsLeftAlone()
	{
		var withGap = new[] { Tone(200), Silence(150), Tone(200) }.SelectMany(x => x).ToArray();
		var silent = Silence(300);

		Assert.Equal(withGap.Length, Speech.VoiceSynthesizer.TrimSilence(withGap, Rate).Length); // the middle gap stays
		Assert.Same(silent, Speech.VoiceSynthesizer.TrimSilence(silent, Rate));
	}
}
