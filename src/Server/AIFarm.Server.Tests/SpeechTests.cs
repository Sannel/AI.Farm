using System.Threading.Channels;
using AIFarm.Contracts;
using AIFarm.Server.Game;
using AIFarm.Server.Speech;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIFarm.Server.Tests;

/// <summary>NPCs talking: speech bubbles, reply options, answering players first, and streamed voices.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class SpeechTests(PostgresFixture postgres) : IAsyncLifetime
{
	/// <summary>A Kokoro model folder for the voice test (set AIFARM_TTS_MODEL); without one it's skipped.</summary>
	private static readonly string? VoiceModel = Environment.GetEnvironmentVariable("AIFARM_TTS_MODEL");

	private FarmFactory _factory = null!;

	public Task InitializeAsync()
	{
		_factory = new FarmFactory(postgres);
		_ = _factory.Server;
		return Task.CompletedTask;
	}

	public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

	private GameWorld World => _factory.Services.GetRequiredService<GameWorld>();

	private sealed class ScriptedModel(params Func<IReadOnlyList<ChatMessage>, ChatResponse>[] replies) : IChatClient
	{
		private int _next;
		public List<List<ChatMessage>> Requests { get; } = [];

		public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			var sent = messages.ToList();
			Requests.Add(sent);
			return Task.FromResult(replies[Math.Min(_next++, replies.Length - 1)](sent));
		}

		public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
			ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public object? GetService(Type serviceType, object? serviceKey = null) => null;

		public void Dispose()
		{
		}
	}

	private static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

	private static ChatResponse Call(string name, Dictionary<string, object?> arguments) =>
		new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call0", name, arguments)]));

	private NpcBrain Brain(FarmFactory factory, IChatClient model) => new(
		factory.Services.GetRequiredService<GameWorld>(),
		new ChatClientBuilder(model).UseFunctionInvocation().Build(),
		factory.Services.GetRequiredService<IServiceScopeFactory>(),
		factory.Services.GetRequiredService<SpeechService>(),
		Options.Create(new BrainOptions()),
		Options.Create(new NpcsOptions()),
		TimeProvider.System,
		NullLogger<NpcBrain>.Instance);

	private static async Task<(HubConnection Hub, JoinResult Joined, Channel<SpeechEvent> Heard)> Join(FarmFactory factory, string name)
	{
		var hub = new HubConnectionBuilder()
			.WithUrl(new Uri(factory.Server.BaseAddress, GameHubProtocol.Path), options =>
			{
				options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
				options.Transports = HttpTransportType.LongPolling;
			})
			.Build();
		var heard = Channel.CreateUnbounded<SpeechEvent>();
		hub.On<SpeechEvent>(GameHubProtocol.Speech, s => heard.Writer.TryWrite(s));
		await hub.StartAsync();
		return (hub, await hub.InvokeAsync<JoinResult>(GameHubProtocol.Join, name), heard);
	}

	/// <summary>Brings the farmer over to stand a couple of metres from where players spawn.</summary>
	private static async Task<EntitySnapshot> FarmerAtSpawn(FarmFactory factory)
	{
		var world = factory.Services.GetRequiredService<GameWorld>();
		EntitySnapshot? farmer = null;
		for (var i = 0; i < 100 && farmer is null; i++)
		{
			farmer = world.Snapshot().Entities.FirstOrDefault(e => e.Kind == EntityKind.Npc);
			await Task.Delay(50);
		}
		world.MoveTo(farmer!.Id, GameWorld.PlayerSpawn.X, GameWorld.PlayerSpawn.Z - 2f);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		while (world.Find(farmer.Id) is { } now && (now.X != GameWorld.PlayerSpawn.X || now.Z != GameWorld.PlayerSpawn.Z - 2f))
		{
			await Task.Delay(100, timeout.Token);
		}
		return world.Find(farmer.Id)!;
	}

	[Fact]
	public async Task TheFarmerTalksToANearbyPlayerWhoGetsThreeRepliesToChooseFrom()
	{
		var farmer = await FarmerAtSpawn(_factory);
		var (hub, joined, heard) = await Join(_factory, "Adam");
		await using var _ = hub;
		var model = new ScriptedModel(
			_ => Call("say", new() { ["text"] = "Morning, Adam! The cows are in fine form today.", ["playerId"] = joined.PlayerId }),
			_ => Text("""{"replies": ["Good to hear!", "Which cow is your favourite?", "Need a hand with feeding?", "Not interested, honestly.", "I'll talk to you later."]}"""),
			_ => Text("Greeted Adam."));

		await Brain(_factory, model).TakeTurnAsync(farmer.Id, "Adam just came over to you.", CancellationToken.None);

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var speech = await heard.Reader.ReadAsync(timeout.Token);
		Assert.Equal(("Claude", "Morning, Adam! The cows are in fine form today.", joined.PlayerId),
			(speech.SpeakerName, speech.Text, speech.ToPlayerId));
		// Friendly, curious, practical, negative, and "talk to you later".
		Assert.Equal(["Good to hear!", "Which cow is your favourite?", "Need a hand with feeding?", "Not interested, honestly.",
			"I'll talk to you later."], speech.Replies);
		Assert.False(speech.HasAudio); // no voice model in tests
		// The reply request was its own short prompt about what Adam might say back.
		Assert.Contains("Write exactly 5 different things Adam might say back", model.Requests[1].Last().Text);
		Assert.Contains("negative", model.Requests[1].Last().Text);
		Assert.Contains("I'll talk to you later", model.Requests[1].Last().Text);
	}

	[Fact]
	public async Task RepliesTheModelDoesntWriteAreFilledInSoThereAreAlwaysFive()
	{
		var farmer = await FarmerAtSpawn(_factory);
		var (hub, joined, heard) = await Join(_factory, "Adam");
		await using var _ = hub;
		var model = new ScriptedModel(
			_ => Call("say", new() { ["text"] = "Morning, Adam." }), // no player id: it's to the nearest person
			_ => Text("""{"replies": ["Morning!", "How is the farm?"]}"""), // only two
			_ => Text("Done."));

		await Brain(_factory, model).TakeTurnAsync(farmer.Id, "Adam just came over to you.", CancellationToken.None);

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var speech = await heard.Reader.ReadAsync(timeout.Token);
		Assert.Equal(joined.PlayerId, speech.ToPlayerId);
		Assert.Equal(["Morning!", "How is the farm?", NpcBrain.FallbackReplies[2], NpcBrain.FallbackReplies[3], NpcBrain.FallbackReplies[4]],
			speech.Replies);
		Assert.Equal("I'll talk to you later.", speech.Replies[^1]);
	}

	[Fact]
	public async Task TheFarmerTurnsToFaceWhoeverHeSpeaksToAndWhoeverSpeaksToHim()
	{
		var farmer = await FarmerAtSpawn(_factory);
		var (hub, joined, _) = await Join(_factory, "Adam");
		await using var __ = hub;
		var model = new ScriptedModel(_ => Call("say", new() { ["text"] = "Morning, Adam.", ["playerId"] = joined.PlayerId }), _ => Text("{}"), _ => Text("Done."));

		await Brain(_factory, model).TakeTurnAsync(farmer.Id, "Adam just came over to you.", CancellationToken.None);

		var me = World.Find(farmer.Id)!;
		var player = World.Find(joined.PlayerId)!;
		var towardsPlayer = MathF.Atan2(player.X - me.X, player.Z - me.Z);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (MathF.Abs(World.Find(farmer.Id)!.Yaw - towardsPlayer) > 0.05f)
		{
			await Task.Delay(50, timeout.Token); // turns on the next game tick
		}
		Assert.Equal(towardsPlayer, World.Find(farmer.Id)!.Yaw, 0.05f);
	}

	[Fact]
	public async Task HeCantBeHeardFromAcrossTheFarm()
	{
		var farmer = World.Snapshot().Entities.First(e => e.Kind == EntityKind.Npc); // by the farmhouse, ~13 m from spawn
		var (hub, joined, _) = await Join(_factory, "Faraway");
		await using var __ = hub;
		var model = new ScriptedModel(
			_ => Call("say", new() { ["text"] = "Hello over there!", ["playerId"] = joined.PlayerId }),
			_ => Text("Too far."));

		await Brain(_factory, model).TakeTurnAsync(farmer.Id, "Time to see to the farm.", CancellationToken.None);

		var result = model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result?.ToString();
		Assert.Contains("too far away to hear you", result);
		var refused = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync(GameHubProtocol.SayToNpc, farmer.Id, "Hi!"));
		Assert.Contains("too far away", refused.Message);
	}

	[Fact]
	public async Task WhatAPlayerSaysIsShownAndTheNpcAnswersBeforeAnythingElse()
	{
		var farmer = await FarmerAtSpawn(_factory);
		var (hub, joined, heard) = await Join(_factory, "Adam");
		await using var _ = hub;

		await hub.InvokeAsync(GameHubProtocol.SayToNpc, farmer.Id, "Which cow is your favourite?");

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var bubble = await heard.Reader.ReadAsync(timeout.Token);
		Assert.Equal((joined.PlayerId, "Which cow is your favourite?"), (bubble.SpeakerId, bubble.Text));
		var brain = _factory.Services.GetRequiredService<NpcBrain>();
		var next = brain.NextTurn(new BrainOptions());
		Assert.Equal(farmer.Id, next!.Value.NpcId);
		Assert.Equal("Adam said to you: \"Which cow is your favourite?\"", next.Value.Reason);
		Assert.Contains(_factory.Services.GetRequiredService<SpeechService>().Conversation(farmer.Id),
			l => l.Speaker == "Adam" && l.Text == "Which cow is your favourite?");
	}

	[Fact]
	public async Task NpcVoicesStreamWhileTheyAreMade()
	{
		if (VoiceModel is null || !Directory.Exists(VoiceModel))
		{
			return; // needs a Kokoro model: set AIFARM_TTS_MODEL to its folder
		}
		await using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Speech:ModelDir", VoiceModel));
		var world = factory.Services.GetRequiredService<GameWorld>();
		EntitySnapshot? farmer = null;
		for (var i = 0; i < 100 && farmer is null; i++)
		{
			farmer = world.Snapshot().Entities.FirstOrDefault(e => e.Kind == EntityKind.Npc);
			await Task.Delay(50);
		}
		var speech = factory.Services.GetRequiredService<SpeechService>();

		var said = await speech.NpcSaysAsync(farmer!, "Morning! Mind the sheep by the fence.", NpcBrain.DefaultVoice, null, [],
			CancellationToken.None);

		Assert.True(said.HasAudio);
		var samples = 0;
		var rate = 0;
		await foreach (var chunk in speech.StreamAsync(said.SpeechId, CancellationToken.None))
		{
			rate = chunk.SampleRate;
			samples += chunk.Samples.Length / 2;
		}
		Assert.Equal(24000, rate);
		Assert.InRange(samples / (double)rate, 1.0, 10.0); // a couple of seconds of speech
	}

	[Fact]
	public async Task TheStopAtAFullStopIsShort()
	{
		if (VoiceModel is null || !Directory.Exists(VoiceModel))
		{
			return; // needs a Kokoro model: set AIFARM_TTS_MODEL to its folder
		}
		await using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Speech:ModelDir", VoiceModel));
		var world = factory.Services.GetRequiredService<GameWorld>();
		EntitySnapshot? farmer = null;
		for (var i = 0; i < 100 && farmer is null; i++)
		{
			farmer = world.Snapshot().Entities.FirstOrDefault(e => e.Kind == EntityKind.Npc);
			await Task.Delay(50);
		}
		var speech = factory.Services.GetRequiredService<SpeechService>();
		var said = await speech.NpcSaysAsync(farmer!, "Morning. The animals are all content. Anything you want me to watch?",
			NpcBrain.DefaultVoice, null, [], CancellationToken.None);

		var samples = new List<short>();
		var rate = 0;
		await foreach (var chunk in speech.StreamAsync(said.SpeechId, CancellationToken.None))
		{
			rate = chunk.SampleRate;
			for (var i = 0; i < chunk.Samples.Length; i += 2)
			{
				samples.Add((short)(chunk.Samples[i] | (chunk.Samples[i + 1] << 8)));
			}
		}

		// The longest run of near-silence between the first sound and the last.
		var threshold = (int)(Speech.VoiceSynthesizer.SilenceThreshold * short.MaxValue);
		var first = samples.FindIndex(x => Math.Abs(x) > threshold);
		var last = samples.FindLastIndex(x => Math.Abs(x) > threshold);
		int longest = 0, run = 0;
		for (var i = first; i <= last; i++)
		{
			run = Math.Abs(samples[i]) <= threshold ? run + 1 : 0;
			longest = Math.Max(longest, run);
		}
		var longestMs = longest * 1000 / rate;
		Assert.True(longestMs <= 180, $"the longest silence was {longestMs} ms"); // it was ~200 ms at each full stop, plus
	}
}
