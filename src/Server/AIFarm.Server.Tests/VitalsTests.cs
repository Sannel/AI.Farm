using AIFarm.Contracts;
using AIFarm.Server.Data;
using AIFarm.Server.Game;

namespace AIFarm.Server.Tests;

/// <summary>Mood, energy and age of living entities, as the game rules change them.</summary>
public sealed class VitalsTests
{
	private static WorldEntity Animal(float energy = 100, Mood mood = Mood.Content, double age = 365) => new()
	{
		Id = Guid.NewGuid(), Kind = EntityKind.Animal, Name = "Cow", Model = "Cow_Female", HasMemory = true,
		Mood = mood, Energy = energy, AgeDays = age,
	};

	private static WorldEntity Player(float energy = 100) => new()
	{
		Id = Guid.NewGuid(), Kind = EntityKind.Player, Name = "Sam", Model = GameWorld.PlayerModel,
		Mood = Mood.Content, Energy = energy, AgeDays = 9125,
	};

	private static void Run(GameWorld world, double seconds, float dt = 0.05f)
	{
		for (var t = 0.0; t < seconds; t += dt)
		{
			world.Step(dt);
		}
	}

	[Fact]
	public void LivingThingsAgeWithGameTime()
	{
		var world = new GameWorld(new Random(1), dayLength: TimeSpan.FromSeconds(10));
		var cow = Animal(age: 100);
		world.Load([cow]);

		Run(world, 5);

		Assert.Equal(100.5, world.Find(cow.Id)!.Vitals!.AgeDays, 2);
	}

	[Fact]
	public void TiredAnimalsLieDownAndRestUntilTheyAreFullOfEnergy()
	{
		var world = new GameWorld(new Random(1));
		var cow = Animal(energy: GameWorld.Energy.Tired - 1);
		world.Load([cow]);

		Run(world, 1);
		var resting = world.Find(cow.Id)!;
		Assert.Equal("Lie_Idle", resting.Animation);
		Assert.Equal(Mood.Tired, resting.Vitals!.Mood);
		Assert.Equal((cow.X, cow.Z), (resting.X, resting.Z)); // stays put while resting

		// Resting brings back AnimalRestGain percent a minute, until Rested.
		var minutes = (GameWorld.Energy.Rested - cow.Energy!.Value) / GameWorld.Energy.AnimalRestGain;
		Run(world, minutes * 60 + 5);
		var rested = world.Find(cow.Id)!;
		Assert.NotEqual("Lie_Idle", rested.Animation);
		Assert.True(rested.Vitals!.Energy >= GameWorld.Energy.Rested - 1);
		Assert.Equal(Mood.Content, rested.Vitals.Mood);
	}

	[Fact]
	public void WalkingUsesAnimalEnergy()
	{
		var world = new GameWorld(new Random(1));
		var cow = Animal(energy: 50);
		world.Load([cow]);

		var walked = 0f;
		for (var i = 0; i < 20 * 120; i++)
		{
			world.Step(0.05f);
			if (world.Find(cow.Id)!.Animation == "Walking")
			{
				walked += 0.05f;
			}
		}

		var energy = world.Find(cow.Id)!.Vitals!.Energy;
		var idle = 120 - walked;
		var expected = 50 + (idle * GameWorld.Energy.AnimalIdleGain - walked * GameWorld.Energy.AnimalWalkCost) / 60;
		Assert.True(walked > 0);
		Assert.Equal(expected, energy, 1);
	}

	[Fact]
	public void RunningTiresPlayersAndExhaustedPlayersCanOnlyWalk()
	{
		var world = new GameWorld(new Random(1));
		var sam = Player(energy: 1);
		world.AddPlayer(sam);
		world.SetInput(sam.Id, new PlayerInput(1, 0, 0, Run: true));

		Run(world, 10); // 4% a minute: 1% lasts 15 seconds
		Assert.Equal("Running_A", world.Find(sam.Id)!.Animation);
		Run(world, 10);

		var player = world.Find(sam.Id)!;
		Assert.Equal(0f, player.Vitals!.Energy);
		Assert.Equal(Mood.Tired, player.Vitals.Mood);
		Assert.Equal("Walking_A", player.Animation);
	}

	[Fact]
	public void MoodCanBeSetButOnlyOnLivingThings()
	{
		var world = new GameWorld(new Random(1));
		var cow = Animal();
		var fence = new WorldEntity { Id = Guid.NewGuid(), Kind = EntityKind.Prop, Name = "Fence", Model = "Fence" };
		world.Load([cow, fence]);

		Assert.True(world.SetMood(cow.Id, Mood.Playful));
		Assert.False(world.SetMood(fence.Id, Mood.Happy));
		Assert.Null(world.Find(fence.Id)!.Vitals);

		world.Step(0.05f);
		var saved = world.TakeChanged(DateTimeOffset.UnixEpoch).Single(e => e.Id == cow.Id);
		Assert.Equal(Mood.Playful, saved.Mood);
		Assert.Equal(cow.AgeDays!.Value, saved.AgeDays!.Value, 3);
		Assert.NotNull(saved.Energy);
	}

	[Fact]
	public void SavedRowsWithoutVitalsGetDefaults()
	{
		var world = new GameWorld(new Random(1));
		var old = new WorldEntity { Id = Guid.NewGuid(), Kind = EntityKind.Animal, Name = "Old", Model = "Sheep" };
		world.Load([old]);

		Assert.Equal(new EntityVitals(Mood.Content, 100f, 0), world.Find(old.Id)!.Vitals! with { AgeDays = 0 });
	}
}
