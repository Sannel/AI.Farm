using AIFarm.Contracts;
using AIFarm.Server.Data;
using AIFarm.Server.Game;

namespace AIFarm.Server.Tests;

public sealed class GameWorldTests
{
	private static GameWorld NewWorld() => new(new Random(1234));

	private static WorldEntity Animal(float x, float z) => new()
	{
		Id = Guid.NewGuid(), Kind = EntityKind.Animal, Name = "Cow", Model = "Cow_Female", X = x, Z = z, HomeX = x, HomeZ = z,
		HasMemory = true,
	};

	private static WorldEntity Player(float x = 0, float z = 6) => new()
	{
		Id = Guid.NewGuid(), Kind = EntityKind.Player, Name = "Sam", Model = GameWorld.PlayerModel, X = x, Z = z,
	};

	[Theory]
	[InlineData("Beaver_Female")]
	[InlineData("PolarBear_Female")]
	[InlineData("Otter_Female")]
	public void SwimmingAnimalsCanWanderIntoDeepWaterAndUseTheirSwimmingAnimation(string model)
	{
		var world = NewWorld();
		var beaver = Animal(-20, 14);
		beaver.Model = model;
		world.Load([beaver]);
		var reachedDeep = false;
		for (var i = 0; i < 12000; i++)
		{
			world.Step(0.05f);
			var current = world.Find(beaver.Id)!;
			if (WaterMap.WaterDepth(current.X, current.Z) > 0.3f)
				Assert.Equal("Swimming", current.Animation);
			reachedDeep |= WaterMap.IsDeep(current.X, current.Z);
		}
		Assert.True(reachedDeep);
	}

	[Fact]
	public void BatsUseTheFlyingAnimationWhenTheyMove()
	{
		var world = NewWorld();
		var bat = Animal(10, -4);
		bat.Model = "Bat_Female";
		world.Load([bat]);
		var flew = false;
		for (var i = 0; i < 400; i++)
		{
			world.Step(0.05f);
			flew |= world.Find(bat.Id)!.Animation == "Flying";
		}
		Assert.True(flew);
	}

	[Fact]
	public void PlayerMovesInTheDirectionOfItsInput()
	{
		var world = NewWorld();
		var sam = Player(2, 3);
		world.AddPlayer(sam);
		world.SetInput(sam.Id, new PlayerInput(1, 0, 0.5f, Run: false));

		world.Step(0.5f);

		var player = world.Find(sam.Id)!;
		Assert.Equal(2 + GameWorld.WalkSpeed * 0.5f, player.X, 3);
		Assert.Equal(3, player.Z, 3);
		Assert.Equal(0.5f, player.Yaw);
		Assert.Equal("Walking_A", player.Animation);
		Assert.False(player.HasMemory);
	}

	[Fact]
	public void OverlongOrBrokenInputIsClamped()
	{
		var world = NewWorld();
		var sam = Player();
		world.AddPlayer(sam);
		world.SetInput(sam.Id, new PlayerInput(30, 40, float.NaN, Run: true));

		world.Step(1f);

		var player = world.Find(sam.Id)!;
		var moved = MathF.Sqrt(MathF.Pow(player.X - sam.X, 2) + MathF.Pow(player.Z - sam.Z, 2));
		Assert.Equal(GameWorld.RunSpeed, moved, 3);
		Assert.Equal(0f, player.Yaw);
	}

	[Fact]
	public void AnimalsWanderButStayNearHome()
	{
		var world = NewWorld();
		var cow = Animal(10, -4);
		world.Load([cow]);

		var moved = false;
		for (var i = 0; i < 20 * 120; i++) // two minutes at 20 ticks a second
		{
			world.Step(0.05f);
			var now = world.Find(cow.Id)!;
			moved |= now.X != cow.X || now.Z != cow.Z;
			var distance = MathF.Sqrt(MathF.Pow(now.X - cow.HomeX, 2) + MathF.Pow(now.Z - cow.HomeZ, 2));
			Assert.True(distance <= GameWorld.AnimalWanderRadius + 0.01f, $"wandered {distance} m from home");
		}
		Assert.True(moved);
		var saved = Assert.Single(world.TakeChanged(DateTimeOffset.UnixEpoch));
		Assert.Equal((cow.HomeX, cow.HomeZ, true), (saved.HomeX, saved.HomeZ, saved.HasMemory));
		Assert.Empty(world.TakeChanged(DateTimeOffset.UnixEpoch));
	}

	[Fact]
	public void MovedPlayersAreSavedAndLeaveWithTheirFinalPosition()
	{
		var world = NewWorld();
		var sam = Player();
		world.AddPlayer(sam);
		Assert.False(world.AddPlayer(sam)); // can't be in the world twice
		world.Step(1f);
		var still = Assert.Single(world.TakeChanged(DateTimeOffset.UnixEpoch)); // standing still, but a second older
		Assert.Equal((sam.X, sam.Z), (still.X, still.Z));

		world.SetInput(sam.Id, new PlayerInput(0, 1, 0, false));
		world.Step(1f);
		var saved = Assert.Single(world.TakeChanged(DateTimeOffset.UnixEpoch));
		Assert.Equal(sam.Z + GameWorld.WalkSpeed, saved.Z, 3);

		var final = world.RemovePlayer(sam.Id, DateTimeOffset.UnixEpoch);
		Assert.Equal(saved.Z, final!.Z);
		Assert.Null(world.Find(sam.Id));
		Assert.Null(world.RemovePlayer(sam.Id, DateTimeOffset.UnixEpoch));
		Assert.False(world.SetInput(sam.Id, new PlayerInput(1, 0, 0, false)));
	}

	[Fact]
	public void NearListsEntitiesInRangeNearestFirst()
	{
		var world = NewWorld();
		var near = Animal(1, 0);
		var nearer = Animal(0.5f, 0);
		world.Load([near, nearer, Animal(50, 50)]);

		var found = world.Near(0, 0, 5);

		Assert.Equal([nearer.Id, near.Id], found.Select(e => e.Id));
	}

	[Fact]
	public void PlayersFollowTheirReportedPositionButNoFasterThanTheyCanGo()
	{
		var world = NewWorld();
		var sam = Player(0, 0);
		world.AddPlayer(sam);

		// A normal step: one tick's walk ahead.
		world.SetInput(sam.Id, new PlayerInput(0, 0, 0, false, X: 0.3f, Y: 0.2f, Z: 0));
		world.Step(0.05f);
		var walked = world.Find(sam.Id)!;
		Assert.Equal((0.3f, 0.2f), (walked.X, walked.Y));
		Assert.Equal("Walking_A", walked.Animation);

		// A cheat (or a lag spike): 50 m in one tick is held to walking pace, and it catches up later.
		world.SetInput(sam.Id, new PlayerInput(0, 0, 0, false, X: 50.3f, Z: 0));
		world.Step(0.05f);
		var held = world.Find(sam.Id)!;
		Assert.Equal(0.3f + GameWorld.WalkSpeed * 0.05f * GameWorld.ReportedPositionSlack + 0.02f, held.X, 3);
		Assert.Equal("Walking_A", held.Animation); // a touch over walking speed, but not running
		for (var i = 0; i < 400; i++)
		{
			world.Step(0.05f);
		}
		Assert.Equal(50.3f, world.Find(sam.Id)!.X, 3);
	}

	[Fact]
	public void NpcsPotterNearHomeWalkWhereSentAndCantBeRemoved()
	{
		var world = NewWorld();
		var farmer = new WorldEntity
		{
			Id = Guid.NewGuid(), Kind = EntityKind.Npc, Name = "Claude", Model = GameWorld.PlayerModel, X = 0, Z = 0,
			HasMemory = true, Mood = Mood.Content, Energy = 100, AgeDays = 40 * 365,
		};
		world.Load([farmer]);

		for (var i = 0; i < 20 * 60; i++)
		{
			world.Step(0.05f);
			var now = world.Find(farmer.Id)!;
			Assert.True(MathF.Sqrt(now.X * now.X + now.Z * now.Z) <= GameWorld.NpcWanderRadius + 0.01f);
		}

		Assert.True(world.MoveTo(farmer.Id, 10, 0));
		for (var i = 0; i < 20 * 10; i++)
		{
			world.Step(0.05f);
		}
		var there = world.Find(farmer.Id)!;
		Assert.Equal((10f, 0f), (there.X, there.Z));
		Assert.False(world.Remove(farmer.Id));
		Assert.NotNull(world.Find(farmer.Id));
	}
}
