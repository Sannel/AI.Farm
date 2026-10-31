using System.Net;
using System.Net.Http.Json;
using AIFarm.Contracts;
using AIFarm.Server.Data;
using AIFarm.Server.Game;
using AIFarm.Server.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AIFarm.Server.Tests;

/// <summary>Memories kept by entities in the game world, stored in PostgreSQL with where they were made.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class EntityMemoryTests(PostgresFixture postgres) : IAsyncLifetime
{
	private FarmFactory _factory = null!;

	public Task InitializeAsync()
	{
		_factory = new FarmFactory(postgres);
		_ = _factory.Server; // start the server: migrations, then the game loop stocks the world
		return Task.CompletedTask;
	}

	public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

	private async Task<EntitySnapshot> Animal(string name)
	{
		var world = _factory.Services.GetRequiredService<GameWorld>();
		for (var i = 0; i < 100; i++) // the game loop loads the world just after startup
		{
			if (world.Snapshot().Entities.FirstOrDefault(e => e.Name == name) is { } animal)
			{
				return animal;
			}
			await Task.Delay(50);
		}
		throw new InvalidOperationException($"No {name} in the world.");
	}

	private async Task<T> WithStore<T>(Func<MemoryStore, Task<T>> action)
	{
		await using var scope = _factory.Services.CreateAsyncScope();
		return await action(scope.ServiceProvider.GetRequiredService<MemoryStore>());
	}

	[Fact]
	public async Task AnimalsRememberWithWhereTheyWereAndRecallByMeaning()
	{
		var cow = await Animal("Cow");
		Assert.True(cow.HasMemory);

		var memory = await WithStore(store => store.RememberForEntityAsync(cow.Id, "Sam gave me an apple by the fence."));
		await WithStore(store => store.RememberForEntityAsync(cow.Id, "The clover by the pond tastes sweet."));

		Assert.Equal(cow.Id, memory.EntityId);
		Assert.NotNull(memory.X);
		Assert.True(MathF.Abs(memory.X!.Value - cow.X) < GameWorld.AnimalWanderRadius * 2);
		var recalled = await WithStore(store => store.SearchForEntityAsync(cow.Id, "who gave me an apple?"));
		Assert.Equal("Sam gave me an apple by the fence.", recalled[0].Content);

		var stored = await _factory.WithDb(db => db.Memories.SingleAsync(m => m.Id == memory.Id));
		Assert.Equal((cow.Id, MemoryStore.EntityOwner(cow.Id)), (stored.EntityId!.Value, stored.AgentId));
	}

	[Fact]
	public async Task EachEntityOnlyRecallsItsOwnMemories()
	{
		var cow = await Animal("Cow");
		var pig = await Animal("Sow");
		await WithStore(store => store.RememberForEntityAsync(cow.Id, "The farmer hides treats in the barn."));

		Assert.Empty(await WithStore(store => store.SearchForEntityAsync(pig.Id, "where are the treats?")));
	}

	[Fact]
	public async Task OnlyEntitiesThatKeepMemoriesCanRemember()
	{
		var client = _factory.CreateClient();
		var player = await _factory.WithDb(async db =>
		{
			var sam = new WorldEntity { Id = Guid.NewGuid(), Kind = EntityKind.Player, Name = "NoMemory", Model = "Farmer" };
			db.WorldEntities.Add(sam);
			await db.SaveChangesAsync();
			return sam;
		});

		var forPlayer = await client.PostAsJsonAsync($"/api/entities/{player.Id}/memories", new MemoryEndpoints.RememberRequest("hello"));
		var forNobody = await client.PostAsJsonAsync($"/api/entities/{Guid.NewGuid()}/memories", new MemoryEndpoints.RememberRequest("hello"));
		var spoofed = await client.PostAsJsonAsync($"/api/agents/{MemoryStore.EntityOwner(player.Id)}/memories",
			new MemoryEndpoints.RememberRequest("sneaky"));

		Assert.Equal(HttpStatusCode.BadRequest, forPlayer.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, forNobody.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, spoofed.StatusCode);
	}

	[Fact]
	public async Task RemovingAnEntityRemovesItsMemories()
	{
		var goat = await _factory.WithDb(async db =>
		{
			var g = new WorldEntity { Id = Guid.NewGuid(), Kind = EntityKind.Animal, Name = "Visiting Goat", Model = "Goat_Male", HasMemory = true };
			db.WorldEntities.Add(g);
			await db.SaveChangesAsync();
			return g;
		});
		await WithStore(store => store.RememberForEntityAsync(goat.Id, "I was only visiting."));

		await _factory.WithDb(db => db.WorldEntities.Where(e => e.Id == goat.Id).ExecuteDeleteAsync());

		Assert.Equal(0, await _factory.WithDb(db => db.Memories.CountAsync(m => m.EntityId == goat.Id)));
	}

	[Fact]
	public async Task AnimalPositionsAreSavedAsTheyMove()
	{
		var cow = await Animal("Cow");
		var start = await _factory.WithDb(db => db.WorldEntities.AsNoTracking().SingleAsync(e => e.Id == cow.Id));

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		WorldEntity saved;
		do
		{
			await Task.Delay(250, timeout.Token);
			saved = await _factory.WithDb(db => db.WorldEntities.AsNoTracking().SingleAsync(e => e.Id == cow.Id));
		}
		while (saved.X == start.X && saved.Z == start.Z); // it wanders off within a few seconds

		var live = await Animal("Cow");
		var drift = MathF.Sqrt(MathF.Pow(live.X - saved.X, 2) + MathF.Pow(live.Z - saved.Z, 2));
		Assert.True(drift < 1f, $"saved position is {drift} m from the live one");
		Assert.True(saved.UpdatedAt > start.UpdatedAt);
	}
}
