using AIFarm.Contracts;
using AIFarm.Server.Data;
using AIFarm.Server.Game;

namespace AIFarm.Server.Tests;

/// <summary>Swimming and diving: who goes in, how long they can hold their breath, and what happens when it runs out.</summary>
public sealed class SwimmingTests
{
	private static float LakeX => WaterMap.Lake.X;

	private static float LakeZ => WaterMap.Lake.Z;

	private static WorldEntity Animal(string model, float x, float z) => new()
	{
		Id = Guid.NewGuid(), Kind = EntityKind.Animal, Name = model, Model = model, X = x, Z = z, HomeX = x, HomeZ = z,
		HasMemory = true, Mood = Mood.Content, Energy = 100, AgeDays = 365,
	};

	private static WorldEntity Player(float x, float y, float z) => new()
	{
		Id = Guid.NewGuid(), Kind = EntityKind.Player, Name = "Adam", Model = GameWorld.PlayerModel, X = x, Y = y, Z = z,
		Mood = Mood.Content, Energy = 100, AgeDays = 9125,
	};

	private static void Run(GameWorld world, float seconds)
	{
		for (var i = 0; i < seconds * 20; i++)
		{
			world.Step(0.05f);
		}
	}

	[Fact]
	public void SpeciesHaveTheirOwnWaterHabits()
	{
		Assert.Equal(SwimAbility.Avoids, Swimming.For("Cat_Female", EntityKind.Animal).Ability);
		Assert.Equal(SwimAbility.Avoids, Swimming.For("Chicken_Male", EntityKind.Animal).Ability);
		Assert.Equal(SwimAbility.Swimmer, Swimming.For("Horse_Female", EntityKind.Animal).Ability);
		Assert.True(Swimming.For("Hippo_Male", EntityKind.Animal).CanDive);
		Assert.True(Swimming.For("Hippo_Male", EntityKind.Animal).AirSeconds > Swimming.For("Dog_Male", EntityKind.Animal).AirSeconds);
		Assert.Equal(SwimAbility.Wader, Swimming.For("Farmer", EntityKind.Npc).Ability);
		Assert.True(Swimming.For("Farmer", EntityKind.Player).CanDive);
	}

	[Fact]
	public void AnimalsThatDontSwimNeverGoIntoTheWater()
	{
		var world = new GameWorld(new Random(4));
		// A cat and a chicken living right on the shore, whose wander range reaches into the lake.
		var shore = LakeX + WaterMap.Lake.EdgeRadius + 0.5f;
		var cat = Animal("Cat_Female", shore, LakeZ);
		var chicken = Animal("Chicken_Male", shore, LakeZ + 2f);
		world.Load([cat, chicken]);

		for (var i = 0; i < 20 * 600; i++) // ten minutes
		{
			world.Step(0.05f);
			foreach (var id in new[] { cat.Id, chicken.Id })
			{
				var at = world.Find(id)!;
				Assert.True(WaterMap.WaterDepth(at.X, at.Z) <= 0f, $"{at.Name} stood in water at ({at.X:0.0}, {at.Z:0.0})");
			}
		}
	}

	[Fact]
	public void SwimmersFloatAtTheSurfaceInDeepWater()
	{
		var world = new GameWorld(new Random(5));
		var horse = Animal("Horse_Female", LakeX, LakeZ);
		world.Load([horse]);

		Run(world, 5);

		var at = world.Find(horse.Id)!;
		Assert.True(at.Y > -1f && at.Y < WaterMap.SurfaceY, $"y = {at.Y}");
		Assert.False(at.Vitals!.Submerged); // a horse doesn't dive
		Assert.Equal(100f, at.Vitals.Air);
	}

	[Fact]
	public void DiversGoUnderAndTheirBreathRunsDownThenRefills()
	{
		var world = new GameWorld(new Random(6));
		var hippo = Animal("Hippo_Female", LakeX, LakeZ);
		world.Load([hippo]);

		var dived = false;
		var lowest = 100f;
		for (var i = 0; i < 20 * 240; i++)
		{
			world.Step(0.05f);
			var vitals = world.Find(hippo.Id)!.Vitals!;
			dived |= vitals.Submerged;
			lowest = MathF.Min(lowest, vitals.Air);
		}
		Assert.True(dived, "a hippo in deep water should dive sometimes");
		Assert.True(lowest < 100f);
		Assert.True(lowest > 0f, "it comes up before it drowns");
	}

	[Fact]
	public void APlayerUnderWaterRunsOutOfBreathAndTheDrowningDrainsEnergy()
	{
		var world = new GameWorld(new Random(7));
		var deepY = WaterMap.SurfaceY - 1f; // body well under, eyes under
		var player = Player(LakeX, deepY, LakeZ);
		world.AddPlayer(player);
		world.SetInput(player.Id, new PlayerInput(0, 0, 0, false, LakeX, deepY, LakeZ));

		Run(world, 10);
		var after10 = world.Find(player.Id)!.Vitals!;
		Assert.True(after10.Submerged);
		Assert.InRange(after10.Air, 60f, 70f); // a third of a 30-second breath gone

		Run(world, 25);
		var drowning = world.Find(player.Id)!.Vitals!;
		Assert.Equal(0f, drowning.Air);
		Assert.True(drowning.Energy < 60f, $"energy {drowning.Energy}");
	}

	[Fact]
	public void BreathComesBackAtTheSurface()
	{
		var world = new GameWorld(new Random(8));
		var below = WaterMap.SurfaceY - 1f;
		var player = Player(LakeX, below, LakeZ);
		world.AddPlayer(player);
		world.SetInput(player.Id, new PlayerInput(0, 0, 0, false, LakeX, below, LakeZ));
		Run(world, 15);
		Assert.True(world.Find(player.Id)!.Vitals!.Air < 60f);

		var above = WaterMap.SurfaceY - WaterMap.SwimEyeHeight + 0.2f; // head out
		world.SetInput(player.Id, new PlayerInput(0, 0, 0, false, LakeX, above, LakeZ));
		Run(world, 5);

		var vitals = world.Find(player.Id)!.Vitals!;
		Assert.False(vitals.Submerged);
		Assert.Equal(100f, vitals.Air);
	}

	[Fact]
	public void WadingNeverPutsAPlayersHeadUnderWater()
	{
		var shallow = WaterMap.Lake.X + WaterMap.Lake.EdgeRadius - 1f; // just inside the shoreline
		Assert.False(WaterMap.EyesUnderwater(shallow, -0.5f, LakeZ));
		Assert.False(WaterMap.EyesUnderwater(0f, 0f, 6f));
		Assert.True(WaterMap.EyesUnderwater(LakeX, -1.2f, LakeZ));
	}
}
