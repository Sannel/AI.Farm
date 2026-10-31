using AIFarm.Contracts;
using AIFarm.Server.Data;
using AIFarm.Server.Game;
using AIFarm.Server.Memory;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AIFarm.Server.Tests;

/// <summary>Claude the farmer (an NPC with a persona), and destroying animals.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class NpcTests(PostgresFixture postgres) : IAsyncLifetime
{
	private FarmFactory _factory = null!;

	public Task InitializeAsync()
	{
		_factory = new FarmFactory(postgres);
		_ = _factory.Server;
		return Task.CompletedTask;
	}

	public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

	private GameWorld World => _factory.Services.GetRequiredService<GameWorld>();

	private async Task<EntitySnapshot> Live(string name)
	{
		for (var i = 0; i < 100; i++)
		{
			if (World.Snapshot().Entities.FirstOrDefault(e => e.Name == name) is { } entity)
			{
				return entity;
			}
			await Task.Delay(50);
		}
		throw new InvalidOperationException($"No {name} in the world.");
	}

	private async Task<McpClient> Mcp()
	{
		var http = _factory.CreateClient();
		return await McpClient.CreateAsync(new HttpClientTransport(
			new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http, ownsHttpClient: true));
	}

	private async Task<HubConnection> Join(string name)
	{
		var hub = new HubConnectionBuilder()
			.WithUrl(new Uri(_factory.Server.BaseAddress, GameHubProtocol.Path), options =>
			{
				options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
				options.Transports = HttpTransportType.LongPolling;
			})
			.Build();
		await hub.StartAsync();
		await hub.InvokeAsync<JoinResult>(GameHubProtocol.Join, name);
		return hub;
	}

	[Fact]
	public async Task TheFarmerLivesOnTheFarmWithHisPersonaAndMemory()
	{
		var farmer = await Live("Claude");

		Assert.Equal((EntityKind.Npc, "Farmer", true), (farmer.Kind, farmer.Model, farmer.HasMemory));
		Assert.NotNull(farmer.Vitals);
		var saved = await _factory.WithDb(db => db.WorldEntities.AsNoTracking().SingleAsync(e => e.Id == farmer.Id));
		Assert.StartsWith("# Claude, the farmer", saved.Persona);
		Assert.Contains("gifted to you by your human drivers", saved.Persona);

		await using (var scope = _factory.Services.CreateAsyncScope())
		{
			var store = scope.ServiceProvider.GetRequiredService<MemoryStore>();
			await store.RememberForEntityAsync(farmer.Id, "Adam brought me a new bull today.");
			Assert.Equal("Adam brought me a new bull today.", (await store.SearchForEntityAsync(farmer.Id, "who brought the bull?"))[0].Content);
		}
	}

	[Fact]
	public async Task AnAgentCanTakeOnThePersonaAndWalkTheFarmer()
	{
		var farmer = await Live("Claude");
		await using var client = await Mcp();

		var prompts = await client.ListPromptsAsync();
		Assert.Contains(prompts, p => p.Name == "play_npc");
		var prompt = await client.GetPromptAsync("play_npc", new Dictionary<string, object?> { ["name"] = "Claude" });
		var text = string.Concat(prompt.Messages.Select(m => m.Content).OfType<TextContentBlock>().Select(c => c.Text));
		Assert.Contains("You are Claude, the farmer", text);
		Assert.Contains(farmer.Id.ToString(), text);
		Assert.Contains("Near you:", text);

		var listed = await client.CallToolAsync("list_npcs");
		Assert.Contains(farmer.Id.ToString(), string.Concat(listed.Content.OfType<TextContentBlock>().Select(c => c.Text)));

		await client.CallToolAsync("npc_move_to", new Dictionary<string, object?> { ["entityId"] = farmer.Id, ["x"] = 4f, ["z"] = -2f });
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		EntitySnapshot now;
		do
		{
			await Task.Delay(100, timeout.Token);
			now = World.Find(farmer.Id)!;
		}
		while (now.X != 4f || now.Z != -2f);
		var home = await _factory.WithDb(async db =>
		{
			// Saved soon after (every quarter second here), with the new spot as his home.
			for (var i = 0; i < 50; i++)
			{
				var row = await db.WorldEntities.AsNoTracking().SingleAsync(e => e.Id == farmer.Id);
				if (row.HomeX == 4f)
				{
					return row;
				}
				await Task.Delay(100);
			}
			throw new TimeoutException("Not saved.");
		});
		Assert.Equal((4f, -2f), (home.HomeX, home.HomeZ));
		Assert.NotNull(home.Persona); // saving his position doesn't lose his persona
	}

	[Fact]
	public async Task AdamCanDestroyAnimalsButNotPeople()
	{
		var hen = await Live("Hen");
		var farmer = await Live("Claude");
		await using (var scope = _factory.Services.CreateAsyncScope())
		{
			await scope.ServiceProvider.GetRequiredService<MemoryStore>().RememberForEntityAsync(hen.Id, "I laid an egg.");
		}
		await using var adam = await Join("Adam");

		await adam.InvokeAsync(GameHubProtocol.DestroyEntity, hen.Id);

		Assert.Null(World.Find(hen.Id));
		Assert.False(await _factory.WithDb(db => db.WorldEntities.AnyAsync(e => e.Id == hen.Id)));
		Assert.Equal(0, await _factory.WithDb(db => db.Memories.CountAsync(m => m.EntityId == hen.Id)));

		var refused = await Assert.ThrowsAsync<HubException>(() => adam.InvokeAsync(GameHubProtocol.DestroyEntity, farmer.Id));
		Assert.Contains("Claude can't be destroyed", refused.Message);
		Assert.NotNull(World.Find(farmer.Id));
	}

	[Fact]
	public async Task OthersCannotDestroyAnything()
	{
		var cow = await Live("Cow");
		await using var visitor = await Join("Visitor");

		var refused = await Assert.ThrowsAsync<HubException>(() => visitor.InvokeAsync(GameHubProtocol.DestroyEntity, cow.Id));

		Assert.Contains("aren't allowed", refused.Message);
		Assert.NotNull(World.Find(cow.Id));
	}
}
