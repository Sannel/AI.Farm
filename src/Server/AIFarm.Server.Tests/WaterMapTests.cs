using AIFarm.Contracts;

namespace AIFarm.Server.Tests;

/// <summary>The shape of the river and lake, and that they stay clear of the farm.</summary>
public sealed class WaterMapTests
{
	[Fact]
	public void TheLakeAndTheSpringAreDugDeepAndTheRestOfThePlainIsFlat()
	{
		Assert.Equal(-1.7f, WaterMap.Height(WaterMap.Lake.X, WaterMap.Lake.Z), 3);
		Assert.Equal(-0.7f, WaterMap.Height(WaterMap.Spring.X, WaterMap.Spring.Z), 3);
		Assert.Equal(0f, WaterMap.Height(0f, 0f));
		Assert.Equal(0f, WaterMap.Height(60f, 60f));
		Assert.True(WaterMap.IsDeep(WaterMap.Lake.X, WaterMap.Lake.Z));
		Assert.False(WaterMap.IsDeep(WaterMap.Spring.X, WaterMap.Spring.Z)); // the spring pool can be waded
	}

	[Fact]
	public void TheRiverRunsFromTheSpringToTheLakeAndCanBeWaded()
	{
		var line = WaterMap.Centerline;
		var start = line[0];
		var end = line[^1];
		Assert.True(MathF.Sqrt(MathF.Pow(start.X - WaterMap.Spring.X, 2) + MathF.Pow(start.Z - WaterMap.Spring.Z, 2)) < WaterMap.Spring.EdgeRadius);
		Assert.True(MathF.Sqrt(MathF.Pow(end.X - WaterMap.Lake.X, 2) + MathF.Pow(end.Z - WaterMap.Lake.Z, 2)) < WaterMap.Lake.EdgeRadius);
		// It's water along its whole length, no deeper than wading, about 5-6 metres wide.
		foreach (var (x, z) in line)
		{
			Assert.True(WaterMap.IsWater(x, z), $"dry on the river at {x},{z}");
		}
		var inLake = (WaterMap.Lake.X, WaterMap.Lake.Z);
		foreach (var (x, z) in line.Where(p => MathF.Sqrt(MathF.Pow(p.X - inLake.X, 2) + MathF.Pow(p.Z - inLake.Z, 2)) > WaterMap.Lake.EdgeRadius + 1))
		{
			Assert.False(WaterMap.IsDeep(x, z));
		}
		Assert.InRange(WaterMap.RiverEdgeHalfWidth * 2, 4.5f, 7f);
		Assert.True(line.Count > 100); // a long way
	}

	[Theory]
	[InlineData(0f, -14f)] // the farmhouse
	[InlineData(0f, 6f)] // where players start
	[InlineData(-4.5f, 2.5f)] // the sheep
	[InlineData(7f, -4f)] // the pens
	[InlineData(16f, 5f)]
	[InlineData(-1.8f, 1.4f)] // the dog
	public void TheFarmAndItsAnimalsStayDry(float x, float z)
	{
		Assert.False(WaterMap.IsWater(x, z));
		Assert.True(WaterMap.DistanceToWater(x, z) > 8f, $"water is only {WaterMap.DistanceToWater(x, z):0.0} m from {x},{z}");
	}

	[Fact]
	public void TheNearestWaterPointIsInTheWaterAndAsCloseAsTheDistanceSays()
	{
		foreach (var (x, z) in new[] { (-4.5f, 2.5f), (0f, 6f), (-60f, -60f), (-38f, -10f), (-25f, 30f) })
		{
			var (wx, wz) = WaterMap.NearestWaterPoint(x, z);

			Assert.True(WaterMap.IsWater(wx, wz), $"nearest point to {x},{z} is dry");
			var away = MathF.Sqrt((wx - x) * (wx - x) + (wz - z) * (wz - z));
			Assert.InRange(away, WaterMap.DistanceToWater(x, z) - 0.2f, WaterMap.DistanceToWater(x, z) + 1.5f);
		}
		Assert.Equal(0f, WaterMap.DistanceToWater(WaterMap.Lake.X, WaterMap.Lake.Z));
	}

	[Fact]
	public void TheBanksAreGentleEnoughToWalk()
	{
		// Steepest slope anywhere on the carved ground, measured over half a metre.
		var (minX, minZ, maxX, maxZ) = WaterMap.Bounds;
		var steepest = 0f;
		for (var x = minX; x < maxX; x += 0.5f)
		{
			for (var z = minZ; z < maxZ; z += 0.5f)
			{
				var h = WaterMap.Height(x, z);
				steepest = MathF.Max(steepest, MathF.Abs(WaterMap.Height(x + 0.5f, z) - h) / 0.5f);
				steepest = MathF.Max(steepest, MathF.Abs(WaterMap.Height(x, z + 0.5f) - h) / 0.5f);
			}
		}
		Assert.True(steepest < MathF.Tan(30f * MathF.PI / 180f), $"a bank climbs {MathF.Atan(steepest) * 180 / MathF.PI:0} degrees"); // players walk up to 45
	}
}
