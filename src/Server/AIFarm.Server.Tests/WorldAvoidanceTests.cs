using AIFarm.Contracts;
using AIFarm.Server.Data;
using AIFarm.Server.Game;

namespace AIFarm.Server.Tests;

/// <summary>Animals, the farmer and players keep out of each other's way; the farmer faces who he's talking to.</summary>
public sealed class WorldAvoidanceTests
{
	private static WorldEntity Animal(string model, float x, float z, string name = "Animal") => new()
	{
		Id = Guid.NewGuid(), Kind = EntityKind.Animal, Name = name, Model = model, X = x, Z = z, HomeX = x, HomeZ = z,
		HasMemory = true, Mood = Mood.Content, Energy = 100, AgeDays = 365,
	};

	private static WorldEntity Farmer(float x, float z) => new()
	{
		Id = Guid.NewGuid(), Kind = EntityKind.Npc, Name = "Claude", Model = "Farmer", X = x, Z = z, HomeX = x, HomeZ = z,
		HasMemory = true, Mood = Mood.Content, Energy = 100, AgeDays = 40 * 365,
	};

	private static WorldEntity Player(float x, float z) => new()
	{
		Id = Guid.NewGuid(), Kind = EntityKind.Player, Name = "Adam", Model = GameWorld.PlayerModel, X = x, Z = z,
		Mood = Mood.Content, Energy = 100, AgeDays = 9125,
	};

	private static float Gap(EntitySnapshot a, EntitySnapshot b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

	private static float Needed(EntitySnapshot a, EntitySnapshot b) =>
		GameWorld.BodyRadius(a.Model, a.Kind) + GameWorld.BodyRadius(b.Model, b.Kind);

	[Fact]
	public void ACrowdedPenNeverHasAnimalsWalkIntoEachOther()
	{
		var world = new GameWorld(new Random(7));
		// Fourteen animals packed close, each wandering around its own spot.
		var herd = new[] { "Horse_Female", "Horse_Male", "Cow_Female", "Cow_Male", "Donkey_Female", "Pig_Female", "Pig_Male", "Goat_Female",
			"Goat_Male", "Sheep_Female", "Sheep_Male", "Dog_Male", "Chicken_Female", "Chicken_Male" }
			.Select((model, i) => Animal(model, i % 5 * 2.2f, i / 5 * 2.2f)).ToList();
		var farmer = Farmer(-3f, 1f); // beside the herd, not inside it
		world.Load([.. herd, farmer]);

		var tightest = float.MaxValue;
		for (var tick = 0; tick < 20 * 240; tick++) // four minutes
		{
			world.Step(0.05f);
			var all = world.Snapshot().Entities;
			for (var i = 0; i < all.Count; i++)
			{
				for (var j = i + 1; j < all.Count; j++)
				{
					tightest = MathF.Min(tightest, Gap(all[i], all[j]) - Needed(all[i], all[j]));
				}
			}
		}

		Assert.True(tightest > -0.06f, $"two bodies overlapped by {-tightest:0.00} m");
	}

	[Fact]
	public void SomethingWalkingStraightAtAnotherGoesAroundIt()
	{
		var world = new GameWorld(new Random(1));
		var sheep = Animal("Sheep_Female", 0, 0, "Sheep");
		var farmer = Farmer(-4f, 0f);
		world.Load([sheep, farmer]);
		world.MoveTo(farmer.Id, 4f, 0f); // straight through the sheep

		var closest = float.MaxValue;
		for (var tick = 0; tick < 20 * 12; tick++)
		{
			world.Step(0.05f);
			var me = world.Find(farmer.Id)!;
			closest = MathF.Min(closest, Gap(me, world.Find(sheep.Id)!));
		}

		Assert.True(closest > 0.4f + 0.45f - 0.06f, $"came within {closest:0.00} m of the sheep");
		var end = world.Find(farmer.Id)!;
		Assert.True(end.X > 0, "got past"); // went around rather than giving up
	}

	[Fact]
	public void OverlappingAnimalsAreNudgedApart()
	{
		var world = new GameWorld(new Random(3));
		var a = Animal("Cow_Female", 0, 0);
		var b = Animal("Cow_Male", 0.1f, 0); // spawned right on top of the first
		world.Load([a, b]);

		for (var tick = 0; tick < 20 * 5; tick++)
		{
			world.Step(0.05f);
		}

		Assert.True(Gap(world.Find(a.Id)!, world.Find(b.Id)!) >= 1.35f, "still overlapping after five seconds");
	}

	[Fact]
	public void AnimalsGiveWayToAPlayerAndNeverMoveThem()
	{
		var world = new GameWorld(new Random(3));
		var cow = Animal("Cow_Female", 0.3f, 0);
		var adam = Player(0, 0); // stands where the cow is
		world.Load([cow]);
		world.AddPlayer(adam);

		for (var tick = 0; tick < 20 * 4; tick++)
		{
			world.Step(0.05f);
		}

		var player = world.Find(adam.Id)!;
		Assert.Equal((0f, 0f), (player.X, player.Z));
		Assert.True(Gap(world.Find(cow.Id)!, player) >= 1.05f);
	}

	[Fact]
	public void TheFarmerFacesThePlayerHeIsTalkingToAndStaysPut()
	{
		var world = new GameWorld(new Random(5));
		var farmer = Farmer(0, 0);
		var adam = Player(3, 4); // up and to one side
		world.Load([farmer]);
		world.AddPlayer(adam);

		Assert.True(world.Engage(farmer.Id, adam.Id, 30f));
		for (var tick = 0; tick < 20 * 29; tick++) // all but the last second
		{
			world.Step(0.05f);
		}

		var me = world.Find(farmer.Id)!;
		Assert.Equal(MathF.Atan2(3, 4), me.Yaw, 0.01f);
		Assert.Equal((0f, 0f), (me.X, me.Z)); // didn't wander off, even though he would have by now

		// He follows them with his eyes when they walk round him.
		world.SetInput(adam.Id, new PlayerInput(0, 0, 0, false, X: -3f, Z: 4f));
		for (var tick = 0; tick < 20; tick++)
		{
			world.Step(0.05f);
		}
		Assert.Equal(MathF.Atan2(world.Find(adam.Id)!.X, world.Find(adam.Id)!.Z), world.Find(farmer.Id)!.Yaw, 0.01f);
	}

	[Fact]
	public void HisAttentionEndsAfterTheEngagementAndEngagingNeedsBothToBeThere()
	{
		var world = new GameWorld(new Random(5));
		var farmer = Farmer(0, 0);
		var adam = Player(3, 4);
		world.Load([farmer]);
		world.AddPlayer(adam);

		Assert.False(world.Engage(farmer.Id, Guid.NewGuid(), 10f)); // nobody there
		Assert.False(world.Engage(adam.Id, farmer.Id, 10f)); // only NPCs pay attention
		world.Engage(farmer.Id, adam.Id, 2f);
		for (var tick = 0; tick < 20 * 120; tick++)
		{
			world.Step(0.05f);
		}

		var me = world.Find(farmer.Id)!;
		Assert.True(MathF.Sqrt(me.X * me.X + me.Z * me.Z) <= GameWorld.NpcWanderRadius + 0.01f); // back to pottering about
	}
}
