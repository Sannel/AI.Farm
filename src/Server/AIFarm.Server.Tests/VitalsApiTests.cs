using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AIFarm.Contracts;
using AIFarm.Server.Data;
using AIFarm.Server.Game;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace AIFarm.Server.Tests;

/// <summary>Vitals saved in PostgreSQL and changed through the MCP tools and HTTP API.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class VitalsApiTests(PostgresFixture postgres) : IAsyncLifetime
{
	private FarmFactory _factory = null!;

	public Task InitializeAsync()
	{
		_factory = new FarmFactory(postgres);
		_ = _factory.Server;
		return Task.CompletedTask;
	}

	public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

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

	private Task<WorldEntity> Saved(Guid id) => _factory.WithDb(db => db.WorldEntities.AsNoTracking().SingleAsync(e => e.Id == id));

	[Fact]
	public async Task NewWorldsStartWithVitalsAndTheyKeepGettingSaved()
	{
		var horse = await Live("Mare");
		var first = await Saved(horse.Id);
		Assert.Equal((Mood.Content, 100f, 8 * 365.0), (first.Mood, first.Energy, first.AgeDays));

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		WorldEntity later;
		do
		{
			await Task.Delay(250, timeout.Token);
			later = await Saved(horse.Id);
		}
		while (later.AgeDays == first.AgeDays); // she ages every tick and is saved every quarter second here

		Assert.True(later.AgeDays > first.AgeDays);
		Assert.InRange(later.Energy!.Value, 0f, 100f);
	}

	[Fact]
	public async Task AgentsSetMoodsThroughMcpAndItIsSaved()
	{
		var dog = await Live("Male Dog");
		var http = _factory.CreateClient();
		await using var client = await McpClient.CreateAsync(new HttpClientTransport(
			new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http, ownsHttpClient: true));

		await client.CallToolAsync("set_mood", new Dictionary<string, object?> { ["entityId"] = dog.Id, ["mood"] = "Playful" });

		Assert.Equal(Mood.Playful, (await Live("Male Dog")).Vitals!.Mood);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while ((await Saved(dog.Id)).Mood != Mood.Playful)
		{
			await Task.Delay(100, timeout.Token);
		}
	}

	[Fact]
	public async Task OfflinePlayersMoodIsSetInTheDatabaseAndJsonUsesNames()
	{
		var offline = await _factory.WithDb(async db =>
		{
			var sam = new WorldEntity
			{
				Id = Guid.NewGuid(), Kind = EntityKind.Player, Name = "Offline Sam", Model = GameWorld.PlayerModel,
				Mood = Mood.Content, Energy = 80, AgeDays = 9000,
			};
			db.WorldEntities.Add(sam);
			await db.SaveChangesAsync();
			return sam;
		});
		var http = _factory.CreateClient();

		var set = await http.PutAsJsonAsync($"/api/entities/{offline.Id}/mood", new { mood = "Sad" });
		var unknown = await http.PutAsJsonAsync($"/api/entities/{Guid.NewGuid()}/mood", new { mood = "Sad" });

		Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
		Assert.Equal(Mood.Sad, (await Saved(offline.Id)).Mood);

		using var world = JsonDocument.Parse(await http.GetStringAsync("/api/world"));
		var animal = world.RootElement.GetProperty("entities").EnumerateArray().First(e => e.GetProperty("kind").GetString() == "Animal");
		Assert.Equal(JsonValueKind.String, animal.GetProperty("vitals").GetProperty("mood").ValueKind);
	}
}
