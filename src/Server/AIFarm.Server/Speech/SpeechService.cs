using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AIFarm.Contracts;
using AIFarm.Server.Game;
using Microsoft.AspNetCore.SignalR;

namespace AIFarm.Server.Speech;

/// <summary>A line of a conversation, for an NPC's briefing.</summary>
public sealed record SpokenLine(string Speaker, string Text, DateTimeOffset At);

/// <summary>
/// Everything said out loud: shows it to every client as a bubble, keeps each NPC's recent
/// conversation (what it said and what was said to it), and makes and keeps the voice for
/// NPC lines so clients can stream it, even while it's still being made.
/// </summary>
public sealed class SpeechService(
	IHubContext<GameHub> hub,
	VoiceSynthesizer voice,
	TimeProvider time,
	ILogger<SpeechService> logger)
{
	public const int MaxLength = 300;
	public const int ConversationLength = 10;
	private static readonly TimeSpan KeepClips = TimeSpan.FromMinutes(2);

	private readonly ConcurrentDictionary<Guid, Clip> _clips = new();
	private readonly ConcurrentDictionary<Guid, LinkedList<SpokenLine>> _conversations = new();

	/// <summary>An NPC says something (to a player, if given), with reply options for that player and a voice.</summary>
	public async Task<SpeechEvent> NpcSaysAsync(EntitySnapshot npc, string text, string voiceName, EntitySnapshot? to,
		IReadOnlyList<string> replies, CancellationToken cancellationToken)
	{
		text = Trim(text);
		var speech = new SpeechEvent(Guid.CreateVersion7(), npc.Id, npc.Name, text, to?.Id, to is null ? [] : replies,
			voice.Available);
		Log(npc.Id, npc.Name, text);
		if (voice.Available)
		{
			StartVoice(speech.SpeechId, text, voice.Resolve(voiceName));
		}
		await hub.Clients.All.SendAsync(GameHubProtocol.Speech, speech, cancellationToken);
		return speech;
	}

	/// <summary>A player says something to an NPC (shown as a bubble over the player; no voice).</summary>
	public async Task<SpeechEvent> PlayerSaysAsync(EntitySnapshot player, EntitySnapshot npc, string text,
		CancellationToken cancellationToken)
	{
		text = Trim(text);
		var speech = new SpeechEvent(Guid.CreateVersion7(), player.Id, player.Name, text, null, [], false);
		Log(npc.Id, player.Name, text);
		await hub.Clients.All.SendAsync(GameHubProtocol.Speech, speech, cancellationToken);
		return speech;
	}

	/// <summary>The NPC's recent conversation, oldest first.</summary>
	public IReadOnlyList<SpokenLine> Conversation(Guid npcId)
	{
		if (!_conversations.TryGetValue(npcId, out var lines))
		{
			return [];
		}
		lock (lines)
		{
			return [.. lines];
		}
	}

	/// <summary>A line's voice, piece by piece as it's made; empty if there's no voice for it.</summary>
	public async IAsyncEnumerable<SpeechAudioChunk> StreamAsync(Guid speechId,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		if (!_clips.TryGetValue(speechId, out var clip))
		{
			yield break;
		}
		for (var next = 0; ; next++)
		{
			byte[]? piece;
			while (true)
			{
				// Take the "more has arrived" signal before looking, so a piece added in between
				// still wakes us.
				var more = clip.NextArrival();
				if ((piece = clip.Get(next)) is not null)
				{
					break;
				}
				if (clip.Finished)
				{
					yield break;
				}
				if (await Task.WhenAny(more, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken)) != more)
				{
					yield break; // the voice stalled (or the client went away)
				}
			}
			yield return new SpeechAudioChunk(voice.SampleRate, piece);
		}
	}

	private void StartVoice(Guid speechId, string text, int voiceId)
	{
		var clip = new Clip(time.GetUtcNow());
		_clips[speechId] = clip;
		foreach (var old in _clips.Where(c => time.GetUtcNow() - c.Value.Made > KeepClips).Select(c => c.Key).ToList())
		{
			_clips.TryRemove(old, out _);
		}
		_ = Task.Run(async () =>
		{
			try
			{
				await voice.SpeakAsync(text, voiceId, clip.Add, CancellationToken.None);
			}
			catch (Exception e)
			{
				logger.LogWarning("Couldn't voice \"{Text}\": {Error}", text, e.Message);
			}
			finally
			{
				clip.Finish();
			}
		});
	}

	private void Log(Guid npcId, string speaker, string text)
	{
		var lines = _conversations.GetOrAdd(npcId, _ => new LinkedList<SpokenLine>());
		lock (lines)
		{
			lines.AddLast(new SpokenLine(speaker, text, time.GetUtcNow()));
			while (lines.Count > ConversationLength)
			{
				lines.RemoveFirst();
			}
		}
	}

	private static string Trim(string text)
	{
		text = (text ?? "").Trim().Trim('"');
		return text.Length <= MaxLength ? text : text[..MaxLength].TrimEnd() + "…";
	}

	/// <summary>A voice clip being made: pieces arrive over time; readers wait for the next.</summary>
	private sealed class Clip(DateTimeOffset made)
	{
		private readonly List<byte[]> _pieces = [];
		private TaskCompletionSource _more = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public DateTimeOffset Made { get; } = made;

		public bool Finished { get; private set; }

		public byte[]? Get(int index)
		{
			lock (_pieces)
			{
				return index < _pieces.Count ? _pieces[index] : null;
			}
		}

		public void Add(byte[] piece)
		{
			lock (_pieces)
			{
				_pieces.Add(piece);
			}
			Wake();
		}

		public void Finish()
		{
			Finished = true;
			Wake();
		}

		/// <summary>Completes when the next piece arrives or the clip finishes.</summary>
		public Task NextArrival()
		{
			lock (_pieces)
			{
				return _more.Task;
			}
		}

		private void Wake()
		{
			TaskCompletionSource done;
			lock (_pieces)
			{
				done = _more;
				_more = new(TaskCreationOptions.RunContinuationsAsynchronously);
			}
			done.TrySetResult();
		}
	}
}
