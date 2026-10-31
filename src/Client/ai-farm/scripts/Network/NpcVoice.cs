using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using AIFarm.Contracts;
using Godot;

namespace AIFarm.Network;

/// <summary>
/// Plays a character's voice from where they stand: streams a line's audio from the server and
/// feeds it to an <see cref="AudioStreamGenerator"/> as it arrives, so speech starts while the
/// server is still making the rest of it. A new line cuts off the old one. While it plays,
/// <see cref="Level"/> says how loud the voice is right now, for lip-sync.
/// </summary>
public partial class NpcVoice : AudioStreamPlayer3D
{
	private const int SampleRate = 24000; // Kokoro's rate; chunks say theirs and we follow it

	private readonly ConcurrentQueue<Vector2[]> _pending = new();
	private AudioStreamGeneratorPlayback? _playback;
	private CancellationTokenSource? _streaming;
	private Vector2[]? _partial;
	private int _partialOffset;

	// Loudness of the audio, in 10 ms steps, in the order it was queued, and how much of it has
	// been handed to the sound buffer: together they tell what is being heard at this moment.
	private const int WindowsPerSecond = 100;
	private const float FullLevelRms = 0.11f; // an RMS this loud (a strong vowel) opens the mouth fully
	private readonly List<float> _levels = [];
	private long _queuedFrames;
	private double _windowEnergy;
	private int _windowCount;

	/// <summary>How loud the voice is at this moment, 0 (silent) to 1 (loud), as it is being heard.</summary>
	public float Level { get; private set; }

	public override void _Ready()
	{
		Stream = new AudioStreamGenerator { MixRate = SampleRate, BufferLength = 1.0f };
		// Heard from where they stand: louder close up, fading with distance, and clearly to the
		// left or right when they are off to one side.
		AttenuationModel = AttenuationModelEnum.InverseDistance;
		UnitSize = 5f;
		MaxDistance = 45f;
		PanningStrength = 1.4f;
		MaxDb = 3f;
	}

	/// <summary>Starts streaming and playing a line's voice.</summary>
	public async void Speak(Guid speechId)
	{
		_streaming?.Cancel();
		var streaming = new CancellationTokenSource();
		_streaming = streaming;
		_pending.Clear();
		_partial = null;
		_levels.Clear();
		_queuedFrames = 0;
		_windowEnergy = 0;
		_windowCount = 0;
		Stop();
		Play();
		_playback = (AudioStreamGeneratorPlayback)GetStreamPlayback();
		try
		{
			await foreach (var chunk in ServerConnection.Instance.StreamSpeech(speechId, streaming.Token))
			{
				if (chunk.SampleRate != SampleRate && Stream is AudioStreamGenerator generator)
				{
					generator.MixRate = chunk.SampleRate;
				}
				_pending.Enqueue(ToFrames(chunk.Samples));
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception e)
		{
			GD.PushWarning($"Voice for {GetParent().Name} stopped: {e.Message}");
		}
	}

	public override void _Process(double delta)
	{
		if (_playback is null)
		{
			Level = 0f;
			return;
		}
		// Top up the playback buffer with as much of what's arrived as fits.
		var room = _playback.GetFramesAvailable();
		while (room > 0)
		{
			if (_partial is null)
			{
				if (!_pending.TryDequeue(out _partial))
				{
					break;
				}
				_partialOffset = 0;
			}
			var count = Math.Min(room, _partial.Length - _partialOffset);
			var frames = _partial.AsSpan(_partialOffset, count).ToArray();
			_playback.PushBuffer(frames);
			Measure(frames);
			_partialOffset += count;
			room -= count;
			if (_partialOffset >= _partial.Length)
			{
				_partial = null;
			}
		}
		Level = HeardLevel();
	}

	/// <summary>Adds queued audio to the loudness record, a 10 ms window at a time.</summary>
	private void Measure(Vector2[] frames)
	{
		var framesPerWindow = (int)((Stream as AudioStreamGenerator)!.MixRate / WindowsPerSecond);
		foreach (var frame in frames)
		{
			_windowEnergy += frame.X * frame.X;
			if (++_windowCount >= framesPerWindow)
			{
				_levels.Add(MathF.Sqrt((float)(_windowEnergy / _windowCount)));
				_windowEnergy = 0;
				_windowCount = 0;
			}
		}
		_queuedFrames += frames.Length;
	}

	/// <summary>The loudness of what is coming out of the speakers right now (the buffer is ahead of it).</summary>
	private float HeardLevel()
	{
		var generator = (AudioStreamGenerator)Stream!;
		var buffered = (int)(generator.BufferLength * generator.MixRate) - _playback!.GetFramesAvailable();
		var heard = _queuedFrames - buffered; // frames played so far
		if (heard < 0)
		{
			return 0f;
		}
		var window = (int)(heard / (generator.MixRate / WindowsPerSecond));
		return window < _levels.Count ? Mathf.Clamp(_levels[window] / FullLevelRms, 0f, 1f) : 0f;
	}

	public override void _ExitTree() => _streaming?.Cancel();

	private static Vector2[] ToFrames(byte[] pcm16)
	{
		var frames = new Vector2[pcm16.Length / 2];
		for (var i = 0; i < frames.Length; i++)
		{
			var sample = (short)(pcm16[2 * i] | (pcm16[2 * i + 1] << 8)) / 32768f;
			frames[i] = new Vector2(sample, sample);
		}
		return frames;
	}
}
