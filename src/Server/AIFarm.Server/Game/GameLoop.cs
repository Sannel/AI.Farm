using System.Diagnostics;
using AIFarm.Contracts;
using AIFarm.Server.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AIFarm.Server.Game;

/// <summary>
/// Runs the game: loads the saved world (stocking a new one with the starter animals, which
/// all keep memories), steps <see cref="GameWorld"/> at a fixed rate, pushes snapshots to every
/// client, and saves everything that changed (positions and the vitals of living things,
/// players included) periodically and on shutdown.
/// </summary>
public sealed class GameLoop(
	GameWorld world,
	IHubContext<GameHub> hub,
	IServiceScopeFactory scopes,
	IOptions<GameOptions> options,
	TimeProvider time,
	ILogger<GameLoop> logger) : BackgroundService
{
	/// <summary>The animals a brand-new world starts with (one of each), by model, on the pasture by spawn.</summary>
	public static readonly (string Model, float X, float Z)[] StarterAnimals =
	[
		("Sheep_Female", -4.5f, 2.5f),
		("Sheep_Male", -6.5f, 2.5f),
		("Dog_Male", -1.8f, 1.4f),
		("Dog_Female", -3.8f, 1.4f),
		("Monkey_Female", -8f, -12f),
		("Monkey_Male", -11f, -12f),
		("Gorilla_Female", -14f, -12f),
		("Gorilla_Male", -17f, -12f),
		("Orangutan_Female", -20f, -12f),
		("Orangutan_Male", -23f, -12f),
		("Poodle_Female", -26f, -12f),
		("Poodle_Male", -29f, -12f),
		("GuideDog_Female", -8f, -16f),
		("GuideDog_Male", -11f, -16f),
		("ServiceDog_Female", -14f, -16f),
		("ServiceDog_Male", -17f, -16f),
		("Wolf_Female", -20f, -16f),
		("Wolf_Male", -23f, -16f),
		("Fox_Female", -26f, -16f),
		("Fox_Male", -29f, -16f),
		("Raccoon_Female", -8f, -20f),
		("Raccoon_Male", -11f, -20f),
		("Cat_Female", -14f, -20f),
		("Cat_Male", -17f, -20f),
		("BlackCat_Female", -20f, -20f),
		("BlackCat_Male", -23f, -20f),
		("Lion_Female", -26f, -20f),
		("Lion_Male", -29f, -20f),
		("Tiger_Female", -8f, -24f),
		("Tiger_Male", -11f, -24f),
		("Leopard_Female", -14f, -24f),
		("Leopard_Male", -17f, -24f),
		("Unicorn_Female", -20f, -24f),
		("Unicorn_Male", -23f, -24f),
		("Zebra_Female", -26f, -24f),
		("Zebra_Male", -29f, -24f),
		("Deer_Female", -8f, -28f),
		("Deer_Male", -11f, -28f),
		("Moose_Female", -14f, -28f),
		("Moose_Male", -17f, -28f),
		("Bison_Female", -20f, -28f),
		("Bison_Male", -23f, -28f),
		("WaterBuffalo_Female", -26f, -28f),
		("WaterBuffalo_Male", -29f, -28f),
		("Boar_Female", -8f, -32f),
		("Boar_Male", -11f, -32f),
		("Camel_Female", -14f, -32f),
		("Camel_Male", -17f, -32f),
		("BactrianCamel_Female", -20f, -32f),
		("BactrianCamel_Male", -23f, -32f),
		("Llama_Female", -26f, -32f),
		("Llama_Male", -29f, -32f),
		("Giraffe_Female", -8f, -36f),
		("Giraffe_Male", -11f, -36f),
		("Elephant_Female", -14f, -36f),
		("Elephant_Male", -17f, -36f),
		("Mammoth_Female", -20f, -36f),
		("Mammoth_Male", -23f, -36f),
		("Rhino_Female", -26f, -36f),
		("Rhino_Male", -29f, -36f),
		("Hippo_Female", -8f, -40f),
		("Hippo_Male", -11f, -40f),
		("Mouse_Female", -14f, -40f),
		("Mouse_Male", -17f, -40f),
		("Rat_Female", -20f, -40f),
		("Rat_Male", -23f, -40f),
		("Hamster_Female", -26f, -40f),
		("Hamster_Male", -29f, -40f),
		("Rabbit_Female", -8f, -44f),
		("Rabbit_Male", -11f, -44f),
		("Chipmunk_Female", -14f, -44f),
		("Chipmunk_Male", -17f, -44f),
		("Beaver_Female", -20f, 14f),
		("Beaver_Male", -21f, 17f),
		("Hedgehog_Female", -26f, -44f),
		("Hedgehog_Male", -29f, -44f),
		("Bat_Female", -8f, -48f),
		("Bat_Male", -11f, -48f),
		("Bear_Female", -14f, -48f),
		("Bear_Male", -17f, -48f),
		("PolarBear_Female", -20f, -48f),
		("PolarBear_Male", -23f, -48f),
		("Koala_Female", -26f, -48f),
		("Koala_Male", -29f, -48f),
		("Panda_Female", -8f, -52f),
		("Panda_Male", -11f, -52f),
		("Sloth_Female", -14f, -52f),
		("Sloth_Male", -17f, -52f),
		("Otter_Female", -40f, 14f),
		("Otter_Male", -39f, 17f),
		("Skunk_Female", -20f, -52f),
		("Skunk_Male", -23f, -52f),
		("Kangaroo_Female", -26f, -52f),
		("Kangaroo_Male", -29f, -52f),
		("Badger_Female", -8f, -56f),
		("Badger_Male", -11f, -56f),
		("Turkey_Female", -14f, -56f),
		("Turkey_Male", -17f, -56f),
		("Cow_Female", 7f, -4f),
		("Cow_Male", 10f, -4f),
		("Pig_Female", 13f, -4f),
		("Pig_Male", 15f, -4f),
		("Chicken_Female", 7f, 0f),
		("Chicken_Male", 8.5f, 0f),
		("Goat_Female", 11f, 0f),
		("Goat_Male", 13f, 0f),
		("Horse_Female", 8f, 5f),
		("Horse_Male", 11f, 5f),
		("Donkey_Female", 14f, 5f),
		("Donkey_Male", 16f, 5f),
	];

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await LoadAsync(stoppingToken);

		var settings = options.Value;
		var period = TimeSpan.FromSeconds(1.0 / Math.Max(1, settings.TickRate));
		using var timer = new PeriodicTimer(period, time);
		var clock = Stopwatch.StartNew();
		var lastStep = clock.Elapsed;
		var lastSave = time.GetUtcNow();
		try
		{
			while (await timer.WaitForNextTickAsync(stoppingToken))
			{
				var now = clock.Elapsed;
				// Real elapsed time (capped, so a stall doesn't teleport everything).
				world.Step((float)Math.Min((now - lastStep).TotalSeconds, 0.25));
				lastStep = now;

				if (world.Tick % Math.Max(1, settings.BroadcastEvery) == 0)
				{
					await hub.Clients.All.SendAsync(GameHubProtocol.Snapshot, world.Snapshot(), stoppingToken);
				}
				if (time.GetUtcNow() - lastSave >= settings.SaveInterval)
				{
					lastSave = time.GetUtcNow();
					await SaveAsync(stoppingToken);
				}
			}
		}
		catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
		{
		}
	}

	public override async Task StopAsync(CancellationToken cancellationToken)
	{
		await base.StopAsync(cancellationToken);
		await SaveAsync(cancellationToken);
	}

	private async Task LoadAsync(CancellationToken cancellationToken)
	{
		await using var scope = scopes.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<FarmDbContext>();
		if (!await db.WorldEntities.AnyAsync(e => e.Kind == EntityKind.Animal, cancellationToken))
		{
			var now = time.GetUtcNow();
			db.WorldEntities.AddRange(StarterAnimals.Select(a => NewAnimal(AnimalCatalog.Find(a.Model)!, a.X, a.Z, 0f, now)));
			await db.SaveChangesAsync(cancellationToken);
			logger.LogInformation("Stocked a new world with {Count} animals", StarterAnimals.Length);
		}
		// Players come in when they join; everything else lives in the world all the time.
		var saved = await db.WorldEntities.AsNoTracking().Where(e => e.Kind != EntityKind.Player).ToListAsync(cancellationToken);
		world.Load(saved);
		logger.LogInformation("Loaded {Count} world entities", saved.Count);
	}

	/// <summary>A new, rested, content animal of a catalog kind, at home where it stands.</summary>
	public static WorldEntity NewAnimal(AnimalCatalog.Entry kind, float x, float z, float yaw, DateTimeOffset now) => new()
	{
		Id = Guid.CreateVersion7(), Kind = EntityKind.Animal, Name = kind.Name, Model = kind.Model,
		X = x, Z = z, Yaw = yaw, HomeX = x, HomeZ = z, HasMemory = true,
		Mood = Mood.Content, Energy = 100f, AgeDays = kind.AgeDays, UpdatedAt = now,
	};

	private async Task SaveAsync(CancellationToken cancellationToken)
	{
		var changed = world.TakeChanged(time.GetUtcNow());
		if (changed.Count == 0)
		{
			return;
		}
		try
		{
			await using var scope = scopes.CreateAsyncScope();
			var db = scope.ServiceProvider.GetRequiredService<FarmDbContext>();
			db.WorldEntities.UpdateRange(changed);
			await db.SaveChangesAsync(cancellationToken);
		}
		catch (Exception e) when (e is not OperationCanceledException)
		{
			logger.LogError(e, "Saving {Count} world entities failed", changed.Count);
		}
	}
}
