using System.Collections.Concurrent;
using AIFarm.Contracts;
using AIFarm.Server.Data;
using AIFarm.Server.Speech;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AIFarm.Server.Game;

/// <summary>
/// The real-time connection for game clients (see <see cref="GameHubProtocol"/>). Each
/// connection is one player; snapshots are pushed to everyone by <see cref="GameLoop"/>.
/// Players are saved by name: joining again puts you back where you left off, with whatever
/// permissions you have (e.g. spawning animals).
/// </summary>
public sealed class GameHub(GameWorld world, PlayerConnections players, FarmDbContext db, TimeProvider time,
	IOptions<GameOptions> options, SpeechService speech, NpcBrain brain, ILogger<GameHub> logger) : Hub
{
	public const int MaxNameLength = 32;
	public const string DefaultName = "Farmer";

	/// <summary>How far in front of the player a spawned animal appears, in metres.</summary>
	public const float SpawnDistance = 3f;

	[HubMethodName(GameHubProtocol.Join)]
	public async Task<JoinResult> Join(string name)
	{
		name = string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();
		name = name[..Math.Min(name.Length, MaxNameLength)];
		if (players.TryGet(Context.ConnectionId, out var existing))
		{
			return new JoinResult(existing.PlayerId, world.Snapshot(), existing.Permissions);
		}

		var player = await db.WorldEntities.AsNoTracking()
			.FirstOrDefaultAsync(e => e.Kind == EntityKind.Player && e.Name == name, Context.ConnectionAborted);
		if (player is null)
		{
			player = new WorldEntity
			{
				Id = Guid.CreateVersion7(), Kind = EntityKind.Player, Name = name, Model = GameWorld.PlayerModel,
				X = GameWorld.PlayerSpawn.X, Z = GameWorld.PlayerSpawn.Z, Yaw = GameWorld.PlayerSpawnYaw,
				Mood = Mood.Content, Energy = 100f, AgeDays = options.Value.PlayerStartAgeDays, UpdatedAt = time.GetUtcNow(),
			};
			db.WorldEntities.Add(player);
			await db.SaveChangesAsync(Context.ConnectionAborted);
		}
		if (!world.AddPlayer(player))
		{
			throw new HubException($"{name} is already playing.");
		}
		players.Add(Context.ConnectionId, new PlayerSession(player.Id, player.Permissions));
		logger.LogInformation("{Name} joined as {PlayerId} at ({X}, {Z})", name, player.Id, player.X, player.Z);
		return new JoinResult(player.Id, world.Snapshot(), player.Permissions);
	}

	[HubMethodName(GameHubProtocol.SendInput)]
	public void SendInput(PlayerInput input)
	{
		if (players.TryGet(Context.ConnectionId, out var session))
		{
			world.SetInput(session.PlayerId, input);
		}
	}

	[HubMethodName(GameHubProtocol.GetSpawnableAnimals)]
	public IReadOnlyList<SpawnableAnimal> GetSpawnableAnimals() =>
		players.TryGet(Context.ConnectionId, out var session) && session.Permissions.Contains(Permissions.SpawnAnimals)
			? [.. AnimalCatalog.All.Select(a => a.ToContract())]
			: [];

	/// <summary>Spawns an animal a few metres in front of the player, facing them, and saves it.</summary>
	[HubMethodName(GameHubProtocol.SpawnAnimal)]
	public async Task<EntitySnapshot> SpawnAnimal(string model)
	{
		if (!players.TryGet(Context.ConnectionId, out var session) || world.Find(session.PlayerId) is not { } player)
		{
			throw new HubException("Join the world first.");
		}
		if (!session.Permissions.Contains(Permissions.SpawnAnimals))
		{
			throw new HubException($"{player.Name} isn't allowed to spawn animals.");
		}
		var kind = AnimalCatalog.Find(model) ?? throw new HubException($"There's no animal called {model}.");

		// Entities face +Z at yaw 0, so the player is looking along (sin yaw, cos yaw).
		var x = player.X + MathF.Sin(player.Yaw) * SpawnDistance;
		var z = player.Z + MathF.Cos(player.Yaw) * SpawnDistance;
		var animal = GameLoop.NewAnimal(kind, x, z, player.Yaw + MathF.PI, time.GetUtcNow());
		animal.Y = player.Y;
		db.WorldEntities.Add(animal);
		await db.SaveChangesAsync(Context.ConnectionAborted);
		world.AddEntity(animal);
		logger.LogInformation("{Player} spawned a {Animal} ({Model}) at ({X}, {Z})", player.Name, kind.Name, model, x, z);
		return world.Find(animal.Id)!;
	}

	/// <summary>Dips a bucket in the water the player is at.</summary>
	[HubMethodName(GameHubProtocol.CollectWater)]
	public int CollectWater()
	{
		if (!players.TryGet(Context.ConnectionId, out var session))
		{
			throw new HubException("Join the world first.");
		}
		var (ok, message, water) = world.CollectWater(session.PlayerId, time.GetUtcNow());
		return ok ? water : throw new HubException(message);
	}

	/// <summary>Permanently removes an animal (its memories go with it). People can't be destroyed.</summary>
	[HubMethodName(GameHubProtocol.DestroyEntity)]
	public async Task DestroyEntity(Guid entityId)
	{
		if (!players.TryGet(Context.ConnectionId, out var session))
		{
			throw new HubException("Join the world first.");
		}
		if (!session.Permissions.Contains(Permissions.DestroyAnimals))
		{
			throw new HubException("You aren't allowed to destroy animals.");
		}
		var target = world.Find(entityId) ?? throw new HubException("That isn't in the world any more.");
		if (!GameWorld.IsDestroyable(target.Kind))
		{
			throw new HubException($"{target.Name} can't be destroyed.");
		}
		world.Remove(entityId);
		await db.WorldEntities.Where(e => e.Id == entityId).ExecuteDeleteAsync(Context.ConnectionAborted);
		logger.LogInformation("Player {PlayerId} destroyed {Name} ({EntityId})", session.PlayerId, target.Name, entityId);
	}

	/// <summary>The player says something to an NPC standing nearby; it answers on its next turn.</summary>
	[HubMethodName(GameHubProtocol.SayToNpc)]
	public async Task SayToNpc(Guid npcId, string text)
	{
		if (!players.TryGet(Context.ConnectionId, out var session) || world.Find(session.PlayerId) is not { } player)
		{
			throw new HubException("Join the world first.");
		}
		if (world.Find(npcId) is not { Kind: EntityKind.Npc } npc)
		{
			throw new HubException("There's nobody like that here.");
		}
		if (MathF.Sqrt((npc.X - player.X) * (npc.X - player.X) + (npc.Z - player.Z) * (npc.Z - player.Z)) > NpcBrain.TalkDistance)
		{
			throw new HubException($"{npc.Name} is too far away to hear you.");
		}
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		var said = await speech.PlayerSaysAsync(player, npc, text, Context.ConnectionAborted);
		world.Engage(npcId, session.PlayerId, NpcBrain.EngageSeconds); // it turns to face whoever spoke
		brain.HeardFrom(npcId, player.Name, said.Text);
	}

	/// <summary>The voice for a line someone said, streamed as it's made.</summary>
	[HubMethodName(GameHubProtocol.StreamSpeech)]
	public IAsyncEnumerable<SpeechAudioChunk> StreamSpeech(Guid speechId, CancellationToken cancellationToken) =>
		speech.StreamAsync(speechId, cancellationToken);

	public override async Task OnDisconnectedAsync(Exception? exception)
	{
		if (players.Remove(Context.ConnectionId, out var session) && world.RemovePlayer(session.PlayerId, time.GetUtcNow()) is { } final)
		{
			// Save where they stopped, so they rejoin there.
			db.WorldEntities.Update(final);
			await db.SaveChangesAsync();
			logger.LogInformation("{Name} left at ({X}, {Z})", final.Name, final.X, final.Z);
		}
		await base.OnDisconnectedAsync(exception);
	}
}

/// <summary>A connected player: their entity and what they're allowed to do.</summary>
public sealed record PlayerSession(Guid PlayerId, string[] Permissions);

/// <summary>Which player each hub connection controls.</summary>
public sealed class PlayerConnections
{
	private readonly ConcurrentDictionary<string, PlayerSession> _players = new();

	public void Add(string connectionId, PlayerSession session) => _players[connectionId] = session;

	public bool TryGet(string connectionId, out PlayerSession session) => _players.TryGetValue(connectionId, out session!);

	public bool Remove(string connectionId, out PlayerSession session) => _players.TryRemove(connectionId, out session!);
}
