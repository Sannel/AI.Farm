using System.Threading.Channels;
using AIFarm.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;

namespace AIFarm.Server.Tests;

/// <summary>Plays the game the way the Godot client will: join, move, watch the snapshots.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class GameHubTests(PostgresFixture postgres)
{
	private static HubConnection Connect(FarmFactory factory) =>
		new HubConnectionBuilder()
			.WithUrl(new Uri(factory.Server.BaseAddress, GameHubProtocol.Path), options =>
			{
				options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
				options.Transports = HttpTransportType.LongPolling; // the in-memory test server has no sockets
			})
			.Build();

	[Fact]
	public async Task PlayerJoinsMovesAndIsSeenByOthers()
	{
		await using var factory = new FarmFactory(postgres);
		await using var sam = Connect(factory);
		await using var watcher = Connect(factory);
		var snapshots = Channel.CreateUnbounded<WorldSnapshot>();
		watcher.On<WorldSnapshot>(GameHubProtocol.Snapshot, s => snapshots.Writer.TryWrite(s));
		await sam.StartAsync();
		await watcher.StartAsync();

		var joined = await sam.InvokeAsync<JoinResult>(GameHubProtocol.Join, "Sam");
		var start = Assert.Single(joined.Snapshot.Entities, e => e.Id == joined.PlayerId);
		Assert.Equal(EntityKind.Player, start.Kind);
		Assert.Contains(joined.Snapshot.Entities, e => e.Kind == EntityKind.Animal);

		await sam.InvokeAsync(GameHubProtocol.SendInput, new PlayerInput(0, -1, 0, Run: false));

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		EntitySnapshot? seen;
		do
		{
			// Snapshots from before Sam joined don't have him yet.
			var snapshot = await snapshots.Reader.ReadAsync(timeout.Token);
			seen = snapshot.Entities.SingleOrDefault(e => e.Id == joined.PlayerId);
		}
		while (seen is null || seen.Z > start.Z - 1f); // walked at least a metre towards -Z
		Assert.Equal("Walking_A", seen.Animation);

		await sam.StopAsync();
		do
		{
			var snapshot = await snapshots.Reader.ReadAsync(timeout.Token);
			if (snapshot.Entities.All(e => e.Id != joined.PlayerId))
			{
				break;
			}
		}
		while (true);
	}

	[Fact]
	public async Task PlayersComeBackWhereTheyLeftOff()
	{
		await using var factory = new FarmFactory(postgres);
		await using var first = Connect(factory);
		await first.StartAsync();
		var joined = await first.InvokeAsync<JoinResult>(GameHubProtocol.Join, "Rejoiner");
		var start = joined.Snapshot.Entities.Single(e => e.Id == joined.PlayerId);

		// A second connection can't be the same player at the same time.
		await using (var twin = Connect(factory))
		{
			await twin.StartAsync();
			await Assert.ThrowsAsync<HubException>(() => twin.InvokeAsync<JoinResult>(GameHubProtocol.Join, "Rejoiner"));
		}

		await first.InvokeAsync(GameHubProtocol.SendInput, new PlayerInput(1, 0, 1.5f, Run: false));
		await Task.Delay(600);
		await first.InvokeAsync(GameHubProtocol.SendInput, new PlayerInput(0, 0, 1.5f, Run: false));
		await first.StopAsync();

		var saved = await factory.WithDb(db => db.WorldEntities.AsNoTracking().SingleAsync(e => e.Id == joined.PlayerId));
		Assert.True(saved.X > start.X + 1f, $"saved X {saved.X} vs start {start.X}");
		Assert.Equal(1.5f, saved.Yaw);

		await using var again = Connect(factory);
		await again.StartAsync();
		var rejoined = await again.InvokeAsync<JoinResult>(GameHubProtocol.Join, "Rejoiner");
		var back = rejoined.Snapshot.Entities.Single(e => e.Id == rejoined.PlayerId);
		Assert.Equal(joined.PlayerId, rejoined.PlayerId);
		Assert.Equal((saved.X, saved.Z), (back.X, back.Z));
	}
}
