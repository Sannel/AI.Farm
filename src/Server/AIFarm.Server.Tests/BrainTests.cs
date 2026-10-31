using System.Text.Json;
using AIFarm.Contracts;
using AIFarm.Server.Data;
using AIFarm.Server.Game;
using AIFarm.Server.Memory;
using AIFarm.Server.Speech;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIFarm.Server.Tests;

/// <summary>A farmer's turn, with a scripted stand-in for the language model.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class BrainTests(PostgresFixture postgres) : IAsyncLifetime
{
	private FarmFactory _factory = null!;

	public Task InitializeAsync()
	{
		_factory = new FarmFactory(postgres);
		_ = _factory.Server;
		return Task.CompletedTask;
	}

	public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

	/// <summary>Answers each request with the next scripted reply, recording what it was sent.</summary>
	private sealed class ScriptedModel(params Func<IReadOnlyList<ChatMessage>, ChatResponse>[] replies) : IChatClient
	{
		private int _next;
		public List<List<ChatMessage>> Requests { get; } = [];
		public ChatOptions? LastOptions { get; private set; }

		public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			var sent = messages.ToList();
			Requests.Add(sent);
			LastOptions = options;
			return Task.FromResult(replies[Math.Min(_next++, replies.Length - 1)](sent));
		}

		public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
			ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public object? GetService(Type serviceType, object? serviceKey = null) => null;

		public void Dispose()
		{
		}
	}

	private async Task<EntitySnapshot> Live(string name)
	{
		var world = _factory.Services.GetRequiredService<GameWorld>();
		for (var i = 0; i < 100; i++)
		{
			if (world.Snapshot().Entities.FirstOrDefault(e => e.Name == name) is { } entity)
			{
				return entity;
			}
			await Task.Delay(50);
		}
		throw new InvalidOperationException($"No {name} in the world.");
	}

	private NpcBrain Brain(IChatClient model) => new(
		_factory.Services.GetRequiredService<GameWorld>(),
		new ChatClientBuilder(model).UseFunctionInvocation().Build(),
		_factory.Services.GetRequiredService<IServiceScopeFactory>(),
		_factory.Services.GetRequiredService<SpeechService>(),
		Options.Create(new BrainOptions { MaxSteps = 6 }),
		Options.Create(new NpcsOptions()),
		TimeProvider.System,
		NullLogger<NpcBrain>.Instance);

	private static ChatResponse Calls(params (string Name, Dictionary<string, object?> Arguments)[] calls) =>
		new(new ChatMessage(ChatRole.Assistant, [.. calls.Select((c, i) => (AIContent)new FunctionCallContent($"call{i}", c.Name, c.Arguments))]));

	[Fact]
	public async Task TheFarmerActsOnHisBriefingWithHisTools()
	{
		var farmer = await Live("Claude");
		var cow = await Live("Cow");
		var model = new ScriptedModel(
			_ => Calls(("walk_over_to", new() { ["id"] = cow.Id }), ("rewrite_memory", new() { ["content"] = "Cow looked fine this morning." })),
			_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Walked over to Cow and noted she's well.")));

		var reply = await Brain(model).TakeTurnAsync(farmer.Id, "Time to see to the farm.", CancellationToken.None);

		Assert.Equal("Walked over to Cow and noted she's well.", reply);
		// He was briefed with his persona, where he is, and why he's thinking.
		var first = model.Requests[0];
		Assert.StartsWith("# Claude, the farmer", first[0].Text);
		Assert.Contains(farmer.Id.ToString(), first[1].Text);
		Assert.Contains("Time to see to the farm.", first[1].Text);
		Assert.Equal(["say", "look_around", "farm_overview", "walk_to", "walk_over_to", "comfort", "find_water", "collect_water", "give_water", "rewrite_memory", "rewrite_memory_about"],
			model.LastOptions!.Tools!.Select(t => t.Name));
		Assert.Contains("## Your memory file (0 of 4000 characters used)", first[1].Text);
		// The tool calls really happened: he's walking to the cow and it's in his memory.
		using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
		{
			while (_factory.Services.GetRequiredService<GameWorld>().Find(farmer.Id)!.Animation != "Walking_A")
			{
				await Task.Delay(50, timeout.Token); // set off on the next game tick
			}
		}
		await using var scope = _factory.Services.CreateAsyncScope();
		var file = await scope.ServiceProvider.GetRequiredService<MemoryFiles>().ReadAsync(farmer.Id);
		Assert.Equal("Cow looked fine this morning.", file.Content);
		// And the results went back to the model.
		var results = model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString()).ToList();
		Assert.Contains("Walking over to Cow.", results);
		Assert.Contains("Memory file saved (29 of 4000 characters).", results);
	}

	[Fact]
	public async Task HeCanOnlyComfortAnimalsHesWith()
	{
		var farmer = await Live("Claude");
		var jack = await Live("Jack"); // the donkey, well away from the farmhouse
		var model = new ScriptedModel(
			_ => Calls(("comfort", new() { ["id"] = jack.Id, ["mood"] = "Happy" })),
			_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Too far to reach Jack.")));

		await Brain(model).TakeTurnAsync(farmer.Id, "Time to see to the farm.", CancellationToken.None);

		var result = model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result?.ToString();
		Assert.Contains("walk over first", result);
		Assert.NotEqual(Mood.Happy, _factory.Services.GetRequiredService<GameWorld>().Find(jack.Id)!.Vitals!.Mood);
	}

	[Fact]
	public async Task AnUnreachableModelJustSkipsTheTurn()
	{
		var farmer = await Live("Claude");
		var model = new ScriptedModel(_ => throw new HttpRequestException("Connection refused"));

		Assert.Null(await Brain(model).TakeTurnAsync(farmer.Id, "Time to see to the farm.", CancellationToken.None));
	}

	private async Task<WorldEntity> PlayerBesideFarmer(string name, float offset)
	{
		var farmer = await Live("Claude");
		var player = new WorldEntity
		{
			Id = Guid.NewGuid(), Kind = EntityKind.Player, Name = name, Model = GameWorld.PlayerModel,
			X = farmer.X + offset, Z = farmer.Z, Mood = Mood.Content, Energy = 100, AgeDays = 9125,
		};
		await _factory.WithDb(async db => { db.WorldEntities.Add(player); return await db.SaveChangesAsync(); });
		_factory.Services.GetRequiredService<GameWorld>().AddPlayer(player);
		return player;
	}

	[Fact]
	public async Task HisMemoryHasToStayWithinItsSize()
	{
		var farmer = await Live("Claude");
		var tooLong = new string('x', 4001);
		var model = new ScriptedModel(
			_ => Calls(("rewrite_memory", new() { ["content"] = tooLong })),
			_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Too long; I'll trim it.")));

		await Brain(model).TakeTurnAsync(farmer.Id, "Time to see to the farm.", CancellationToken.None);

		var result = model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result?.ToString();
		Assert.Contains("4001 characters, but this memory file holds at most 4000. Shorten it by 1", result);
		await using var scope = _factory.Services.CreateAsyncScope();
		Assert.Equal("", (await scope.ServiceProvider.GetRequiredService<MemoryFiles>().ReadAsync(farmer.Id)).Content);
	}

	[Fact]
	public async Task PlayersNearbyHaveTheirOwnSmallerMemoryFilesInHisBriefing()
	{
		var farmer = await Live("Claude");
		var near = await PlayerBesideFarmer("Nearby Nell", 3f);
		var far = await PlayerBesideFarmer("Faraway Fred", 40f);
		await using (var scope = _factory.Services.CreateAsyncScope())
		{
			var files = scope.ServiceProvider.GetRequiredService<MemoryFiles>();
			await files.WriteAsync(farmer.Id, null, "The bull is grumpy before noon.");
			await files.WriteAsync(farmer.Id, far.Id, "Fred likes the goats.");
			Assert.Equal(1333, files.PlayerLimit); // a third of the main file
			await Assert.ThrowsAsync<ArgumentException>(() => files.WriteAsync(farmer.Id, near.Id, new string('x', 1334)));
		}
		var model = new ScriptedModel(
			_ => Calls(("rewrite_memory_about", new() { ["playerId"] = near.Id, ["content"] = "Nell visits in the mornings." })),
			_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Said hello to Nell.")));

		await Brain(model).TakeTurnAsync(farmer.Id, "Nearby Nell just came over to you.", CancellationToken.None);

		var briefing = model.Requests[0][1].Text;
		Assert.Contains("The bull is grumpy before noon.", briefing);
		Assert.Contains($"## What you know about Nearby Nell (player id `{near.Id}`; 0 of 1333 characters used)", briefing);
		Assert.DoesNotContain("Faraway Fred", briefing); // out of range
		Assert.DoesNotContain("Fred likes the goats.", briefing);
		await using var after = _factory.Services.CreateAsyncScope();
		var nell = await after.ServiceProvider.GetRequiredService<MemoryFiles>().ReadAsync(farmer.Id, near.Id);
		Assert.Equal(("Nell visits in the mornings.", 1333), (nell.Content, nell.Limit));
	}
}
