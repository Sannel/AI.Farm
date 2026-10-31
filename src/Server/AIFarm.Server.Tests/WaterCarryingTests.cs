using AIFarm.Contracts;
using AIFarm.Server.Data;
using AIFarm.Server.Game;

namespace AIFarm.Server.Tests;

/// <summary>Filling a bucket at the river or lake, giving a drink to animals, and keeping out of deep water.</summary>
public sealed class WaterCarryingTests
{
	private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static WorldEntity Person(float x, float z) => new()
	{
		Id = Guid.NewGuid(), Kind = EntityKind.Player, Name = "Adam", Model = GameWorld.PlayerModel, X = x, Z = z,
		Mood = Mood.Content, Energy = 100, AgeDays = 9125,
	};

	private static WorldEntity Animal(float x, float z, float energy) => new()
	{
		Id = Guid.NewGuid(), Kind = EntityKind.Animal, Name = "Jenny", Model = "Donkey_Female", X = x, Z = z, HomeX = x, HomeZ = z,
		HasMemory = true, Mood = Mood.Content, Energy = energy, AgeDays = 365,
	};

	private static float LakeShore => WaterMap.Lake.X + WaterMap.Lake.EdgeRadius + 1f;

	[Fact]
	public void ABucketFillsAtTheWaterUpToItsCapacity()
	{
		var world = new GameWorld(new Random(1));
		var person = Person(LakeShore, WaterMap.Lake.Z);
		world.AddPlayer(person);

		for (var i = 1; i <= WaterMap.BucketCapacity; i++)
		{
			var (ok, _, water) = world.CollectWater(person.Id, Start + i * GameWorld.DipTime * 2);
			Assert.True(ok);
			Assert.Equal(i, water);
		}
		var full = world.CollectWater(person.Id, Start + TimeSpan.FromMinutes(1));
		Assert.False(full.Ok);
		Assert.Equal(WaterMap.BucketCapacity, world.Find(person.Id)!.Water);
	}

	[Fact]
	public void CollectingNeedsTheWaterToBeNear()
	{
		var world = new GameWorld(new Random(1));
		var person = Person(0f, 6f); // the spawn point, far from water
		world.AddPlayer(person);

		var (ok, message, water) = world.CollectWater(person.Id, Start);
		Assert.False(ok);
		Assert.Equal(0, water);
		Assert.Contains("away", message);
	}

	[Fact]
	public void DippingTooQuicklyIsRefused()
	{
		var world = new GameWorld(new Random(1));
		var person = Person(LakeShore, WaterMap.Lake.Z);
		world.AddPlayer(person);

		Assert.True(world.CollectWater(person.Id, Start).Ok);
		Assert.False(world.CollectWater(person.Id, Start + TimeSpan.FromMilliseconds(100)).Ok);
	}

	[Fact]
	public void GivingWaterToAnAnimalRestoresEnergyAndUsesABucket()
	{
		var world = new GameWorld(new Random(1));
		var person = Person(LakeShore, WaterMap.Lake.Z);
		var animal = Animal(LakeShore + 1.5f, WaterMap.Lake.Z, energy: 20);
		world.Load([animal]);
		world.AddPlayer(person);
		world.CollectWater(person.Id, Start);

		var (ok, _) = world.GiveWater(person.Id, animal.Id);

		Assert.True(ok);
		Assert.Equal(0, world.Find(person.Id)!.Water);
		Assert.Equal(20 + GameWorld.DrinkEnergy, world.Find(animal.Id)!.Vitals!.Energy, 1);
	}

	[Fact]
	public void YouNeedWaterAndToBeNextToTheAnimalToGiveIt()
	{
		var world = new GameWorld(new Random(1));
		var person = Person(LakeShore, WaterMap.Lake.Z);
		var near = Animal(LakeShore + 1.5f, WaterMap.Lake.Z, energy: 20);
		var far = Animal(LakeShore + 30f, WaterMap.Lake.Z, energy: 20);
		world.Load([near, far]);
		world.AddPlayer(person);

		Assert.False(world.GiveWater(person.Id, near.Id).Ok); // empty bucket
		world.CollectWater(person.Id, Start);
		Assert.False(world.GiveWater(person.Id, far.Id).Ok); // too far
		Assert.Equal(1, world.Find(person.Id)!.Water);
	}

	[Fact]
	public void AnimalsKeepOutOfDeepWater()
	{
		var world = new GameWorld(new Random(2));
		// A donkey on the shore tries to walk to the middle of the lake.
		var farmer = new WorldEntity
		{
			Id = Guid.NewGuid(), Kind = EntityKind.Npc, Name = "Claude", Model = "Farmer", X = LakeShore, Z = WaterMap.Lake.Z,
			HomeX = LakeShore, HomeZ = WaterMap.Lake.Z, HasMemory = true, Mood = Mood.Content, Energy = 100, AgeDays = 14600,
		};
		world.Load([farmer]);
		world.MoveTo(farmer.Id, WaterMap.Lake.X, WaterMap.Lake.Z);

		for (var tick = 0; tick < 20 * 90; tick++)
		{
			world.Step(0.05f);
			var at = world.Find(farmer.Id)!;
			Assert.False(WaterMap.IsDeep(at.X, at.Z), $"walked into deep water at ({at.X:0.0}, {at.Z:0.0})");
		}
	}
}
