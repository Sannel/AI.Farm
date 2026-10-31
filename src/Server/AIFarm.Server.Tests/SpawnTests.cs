using AIFarm.Contracts;
using AIFarm.Server.Game;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AIFarm.Server.Tests;

/// <summary>The default user Adam, and spawning animals with the spawn permission.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class SpawnTests(PostgresFixture postgres) : IAsyncLifetime
{
	private FarmFactory _factory = null!;

	public Task InitializeAsync()
	{
		_factory = new FarmFactory(postgres);
		_ = _factory.Server;
		return Task.CompletedTask;
	}

	public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

	private async Task<(HubConnection Hub, JoinResult Joined)> Join(string name)
	{
		var hub = new HubConnectionBuilder()
			.WithUrl(new Uri(_factory.Server.BaseAddress, GameHubProtocol.Path), options =>
			{
				options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
				options.Transports = HttpTransportType.LongPolling;
			})
			.Build();
		await hub.StartAsync();
		return (hub, await hub.InvokeAsync<JoinResult>(GameHubProtocol.Join, name));
	}

	[Fact]
	public async Task AdamExistsFromTheStartAndMaySpawnAnimals()
	{
		var adam = await _factory.WithDb(db => db.WorldEntities.AsNoTracking().SingleAsync(e => e.Kind == EntityKind.Player && e.Name == "Adam"));
		Assert.Contains(Permissions.SpawnAnimals, adam.Permissions);

		var (hub, joined) = await Join("Adam");
		await using var _ = hub;
		Assert.Equal(adam.Id, joined.PlayerId);
		Assert.Contains(Permissions.SpawnAnimals, joined.Permissions);
		var catalog = await hub.InvokeAsync<SpawnableAnimal[]>(GameHubProtocol.GetSpawnableAnimals);
		Assert.Equal(AnimalCatalog.All.Count, catalog.Length);
		Assert.Contains(catalog, a => a.Model == "Cow_Male" && a.Name == "Bull" && a.Species == "Cow" && a.Sex == "Male");
	}

	[Fact]
	public async Task SpawnedAnimalsAppearInFrontFacingThePlayerAndAreSaved()
	{
		var (hub, joined) = await Join("Adam");
		await using var _ = hub;
		var me = joined.Snapshot.Entities.Single(e => e.Id == joined.PlayerId);

		var bull = await hub.InvokeAsync<EntitySnapshot>(GameHubProtocol.SpawnAnimal, "Cow_Male");

		Assert.Equal(("Bull", "Cow_Male", EntityKind.Animal, true), (bull.Name, bull.Model, bull.Kind, bull.HasMemory));
		Assert.Equal(me.X + MathF.Sin(me.Yaw) * GameHub.SpawnDistance, bull.X, 3);
		Assert.Equal(me.Z + MathF.Cos(me.Yaw) * GameHub.SpawnDistance, bull.Z, 3);
		Assert.Equal(new EntityVitals(Mood.Content, 100f, 6 * 365), bull.Vitals! with { Energy = 100f, AgeDays = 6 * 365 });
		var saved = await _factory.WithDb(db => db.WorldEntities.AsNoTracking().SingleAsync(e => e.Id == bull.Id));
		Assert.Equal((bull.X, bull.Z, bull.X, bull.Z), (saved.X, saved.Z, saved.HomeX, saved.HomeZ));
	}

	[Fact]
	public async Task OtherPlayersMayNotSpawn()
	{
		var (hub, joined) = await Join("Visitor");
		await using var _ = hub;

		Assert.Empty(joined.Permissions);
		Assert.Empty(await hub.InvokeAsync<SpawnableAnimal[]>(GameHubProtocol.GetSpawnableAnimals));
		var refused = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<EntitySnapshot>(GameHubProtocol.SpawnAnimal, "Cow_Male"));
		Assert.Contains("isn't allowed", refused.Message);
	}

	[Fact]
	public async Task UnknownAnimalsAreRefused()
	{
		var (hub, _) = await Join("Adam");
		await using var __ = hub;

		var refused = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<EntitySnapshot>(GameHubProtocol.SpawnAnimal, "Dragon"));
		Assert.Contains("no animal called Dragon", refused.Message);
	}

	[Fact]
	public async Task SavingTheWorldKeepsAdamsPermission()
	{
		var (hub, joined) = await Join("Adam");
		await hub.InvokeAsync(GameHubProtocol.SendInput, new PlayerInput(0, 0, 1f, false));
		await Task.Delay(1000); // several saves (every quarter second here) while he's in the world
		await hub.DisposeAsync(); // and the save when he leaves

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (_factory.Services.GetRequiredService<GameWorld>().Find(joined.PlayerId) is not null)
		{
			await Task.Delay(50, timeout.Token);
		}
		var adam = await _factory.WithDb(db => db.WorldEntities.AsNoTracking().SingleAsync(e => e.Id == joined.PlayerId));
		Assert.Equal(1f, adam.Yaw);
		Assert.Contains(Permissions.SpawnAnimals, adam.Permissions);
	}
}
