namespace AIFarm.Contracts;

/// <summary>
/// Where the water is in the valley, and the shape of the ground it sits in: a river that starts
/// in a spring pool at the foot of the north-west mountains, winds across the plain and runs into
/// a lake. The game client builds its terrain from this and the server uses it too (to keep
/// animals out of deep water and to know who is standing by the water), so the two can't disagree.
/// Positions are metres on the ground (X, Z); the plain is flat at height 0 and water is carved
/// below it.
/// </summary>
public static class WaterMap
{
	/// <summary>The height of every water surface, a little below the surrounding ground.</summary>
	public const float SurfaceY = -0.12f;

	/// <summary>Water this deep (metres) is too deep to wade: animals and players keep out of it.</summary>
	public const float DeepWater = 0.85f;

	/// <summary>Water at least this deep (metres) is swum in rather than waded: the player's body is afloat.</summary>
	public const float SwimDepth = 1.2f;

	/// <summary>How high a standing person's eyes are above their feet, and a swimming person's above their body's origin.</summary>
	public const float WalkEyeHeight = 1.6f;

	public const float SwimEyeHeight = 0.4f;

	/// <summary>
	/// Whether someone whose body is at (x, y, z) has their head under the water. Only swimmers can:
	/// anyone wading has their head well clear.
	/// </summary>
	public static bool EyesUnderwater(float x, float y, float z) =>
		WaterDepth(x, z) >= SwimDepth && y + SwimEyeHeight < SurfaceY - 0.04f;

	/// <summary>How far from the water's edge you can still dip a bucket.</summary>
	public const float CollectReach = 3.5f;

	/// <summary>How many buckets of water someone can carry.</summary>
	public const int BucketCapacity = 5;

	/// <summary>A round pool or lake: a flat floor, then a bank that rises smoothly to the ground.</summary>
	public sealed record Basin(string Name, float X, float Z, float FloorRadius, float OuterRadius, float Depth)
	{
		/// <summary>How deep the ground is dug at this distance from the centre.</summary>
		public float DepthAt(float distance) => Depth * (1f - Smooth(FloorRadius, OuterRadius, distance));

		/// <summary>The radius where the water's edge is (the ground has risen to the water's surface).</summary>
		public float EdgeRadius { get; } = Solve(d => Depth * (1f - Smooth(FloorRadius, OuterRadius, d)), FloorRadius, OuterRadius);

		/// <summary>The radius inside which the water is too deep to wade (ground dug at least this deep).</summary>
		public float DeepRadius { get; } = Solve(d => Depth * (1f - Smooth(FloorRadius, OuterRadius, d)), FloorRadius, OuterRadius,
			DeepWater - SurfaceY);
	}

	public static readonly Basin Lake = new("Lake", -30f, 14f, 4f, 16f, 1.7f);

	/// <summary>The pool the river rises from, at the foot of the mountains.</summary>
	public static readonly Basin Spring = new("Spring", -60f, -76f, 1.5f, 6f, 0.7f);

	public static readonly IReadOnlyList<Basin> Basins = [Lake, Spring];

	/// <summary>The river's course, from the spring to the lake (a smooth curve is drawn through these).</summary>
	private static readonly (float X, float Z)[] RiverControl =
	[
		(-60f, -74f), (-55f, -62f), (-62f, -50f), (-52f, -39f), (-44f, -30f), (-50f, -18f), (-40f, -8f), (-34f, 0f), (-31f, 8f),
	];

	/// <summary>The river's flat bed is this wide either side of its centre line...</summary>
	public const float RiverCoreHalfWidth = 1.2f;

	/// <summary>...and its banks slope up over this much more.</summary>
	public const float RiverBankWidth = 2.4f;

	public const float RiverDepth = 0.5f;

	/// <summary>The river's centre line, a point about every metre, from the spring to the lake.</summary>
	public static readonly IReadOnlyList<(float X, float Z)> Centerline = SampleCenterline();

	/// <summary>Half the river's width where the water meets its banks.</summary>
	public static readonly float RiverEdgeHalfWidth = Solve(d => RiverDepthAt(d), RiverCoreHalfWidth, RiverCoreHalfWidth + RiverBankWidth);

	private static readonly float MinX, MaxX, MinZ, MaxZ;

	static WaterMap()
	{
		var margin = RiverCoreHalfWidth + RiverBankWidth;
		MinX = Centerline.Min(p => p.X) - margin;
		MaxX = Centerline.Max(p => p.X) + margin;
		MinZ = Centerline.Min(p => p.Z) - margin;
		MaxZ = Centerline.Max(p => p.Z) + margin;
		foreach (var basin in Basins)
		{
			MinX = MathF.Min(MinX, basin.X - basin.OuterRadius);
			MaxX = MathF.Max(MaxX, basin.X + basin.OuterRadius);
			MinZ = MathF.Min(MinZ, basin.Z - basin.OuterRadius);
			MaxZ = MathF.Max(MaxZ, basin.Z + basin.OuterRadius);
		}
	}

	/// <summary>A rectangle (min X, min Z, max X, max Z) that holds all the carved ground.</summary>
	public static (float MinX, float MinZ, float MaxX, float MaxZ) Bounds => (MinX, MinZ, MaxX, MaxZ);

	public static float RiverDepthAt(float distanceFromCenterline) =>
		RiverDepth * (1f - Smooth(RiverCoreHalfWidth, RiverCoreHalfWidth + RiverBankWidth, distanceFromCenterline));

	/// <summary>The height of the ground at (x, z): 0 on the plain, below that where the river and lake are dug.</summary>
	public static float Height(float x, float z)
	{
		if (x < MinX || x > MaxX || z < MinZ || z > MaxZ)
		{
			return 0f;
		}
		var depth = RiverDepthAt(DistanceToCenterline(x, z));
		foreach (var basin in Basins)
		{
			var distance = MathF.Sqrt((x - basin.X) * (x - basin.X) + (z - basin.Z) * (z - basin.Z));
			if (distance < basin.OuterRadius)
			{
				depth = MathF.Max(depth, basin.DepthAt(distance));
			}
		}
		return -depth;
	}

	/// <summary>How deep the water is at (x, z), 0 on dry ground.</summary>
	public static float WaterDepth(float x, float z) => MathF.Max(0f, SurfaceY - Height(x, z));

	public static bool IsWater(float x, float z) => WaterDepth(x, z) > 0f;

	/// <summary>Whether the water at (x, z) is too deep to wade.</summary>
	public static bool IsDeep(float x, float z) => WaterDepth(x, z) >= DeepWater;

	/// <summary>How far (x, z) is from the nearest water's edge; 0 in the water.</summary>
	public static float DistanceToWater(float x, float z)
	{
		if (IsWater(x, z))
		{
			return 0f;
		}
		var best = DistanceToCenterline(x, z) - RiverEdgeHalfWidth;
		foreach (var basin in Basins)
		{
			best = MathF.Min(best, MathF.Sqrt((x - basin.X) * (x - basin.X) + (z - basin.Z) * (z - basin.Z)) - basin.EdgeRadius);
		}
		return MathF.Max(0f, best);
	}

	/// <summary>A spot in the water close to (x, z): the best place to walk to for a drink.</summary>
	public static (float X, float Z) NearestWaterPoint(float x, float z)
	{
		const float inside = 0.25f; // a little way in from the edge
		var best = (X: x, Z: z, Distance: float.MaxValue);

		// The river: the nearest point on its centre line, then towards (x, z) to the water's edge.
		var (cx, cz, distance) = NearestOnCenterline(x, z);
		if (distance > 1e-3f)
		{
			var reach = MathF.Max(0f, MathF.Min(distance, RiverEdgeHalfWidth - inside));
			var point = (X: cx + (x - cx) / distance * reach, Z: cz + (z - cz) / distance * reach);
			best = (point.X, point.Z, Dist(x, z, point.X, point.Z));
		}
		else
		{
			best = (cx, cz, 0f);
		}

		foreach (var basin in Basins)
		{
			var d = Dist(x, z, basin.X, basin.Z);
			var reach = MathF.Min(d, basin.EdgeRadius - inside);
			var point = d < 1e-3f ? (X: basin.X, Z: basin.Z) : (X: basin.X + (x - basin.X) / d * reach, Z: basin.Z + (z - basin.Z) / d * reach);
			var gap = Dist(x, z, point.X, point.Z);
			if (gap < best.Distance)
			{
				best = (point.X, point.Z, gap);
			}
		}
		return (best.X, best.Z);
	}

	/// <summary>Distance from (x, z) to the river's centre line.</summary>
	public static float DistanceToCenterline(float x, float z) => NearestOnCenterline(x, z).Distance;

	private static (float X, float Z, float Distance) NearestOnCenterline(float x, float z)
	{
		var best = (X: 0f, Z: 0f, Distance: float.MaxValue);
		for (var i = 0; i + 1 < Centerline.Count; i++)
		{
			var (ax, az) = Centerline[i];
			var (bx, bz) = Centerline[i + 1];
			var (dx, dz) = (bx - ax, bz - az);
			var length2 = dx * dx + dz * dz;
			var t = length2 < 1e-9f ? 0f : Math.Clamp(((x - ax) * dx + (z - az) * dz) / length2, 0f, 1f);
			var (px, pz) = (ax + dx * t, az + dz * t);
			var distance = Dist(x, z, px, pz);
			if (distance < best.Distance)
			{
				best = (px, pz, distance);
			}
		}
		return best;
	}

	private static float Dist(float ax, float az, float bx, float bz) => MathF.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

	/// <summary>0 below edge0, 1 above edge1, a smooth S between.</summary>
	public static float Smooth(float edge0, float edge1, float x)
	{
		var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
		return t * t * (3f - 2f * t);
	}

	/// <summary>Finds where a falling depth profile reaches the depth that makes water (the surface height below ground).</summary>
	private static float Solve(Func<float, float> depthAt, float low, float high, float target = -SurfaceY)
	{
		for (var i = 0; i < 40; i++)
		{
			var middle = (low + high) / 2f;
			if (depthAt(middle) > target)
			{
				low = middle;
			}
			else
			{
				high = middle;
			}
		}
		return (low + high) / 2f;
	}

	/// <summary>A Catmull-Rom curve through the control points, a point about every metre.</summary>
	private static (float X, float Z)[] SampleCenterline()
	{
		var points = new List<(float X, float Z)>();
		var control = RiverControl;
		for (var i = 0; i + 1 < control.Length; i++)
		{
			var p0 = control[Math.Max(i - 1, 0)];
			var p1 = control[i];
			var p2 = control[i + 1];
			var p3 = control[Math.Min(i + 2, control.Length - 1)];
			var length = Dist(p1.X, p1.Z, p2.X, p2.Z);
			var steps = Math.Max(2, (int)MathF.Ceiling(length));
			for (var s = 0; s < steps; s++)
			{
				var t = s / (float)steps;
				float Curve(float a, float b, float c, float d) =>
					0.5f * (2f * b + (-a + c) * t + (2f * a - 5f * b + 4f * c - d) * t * t + (-a + 3f * b - 3f * c + d) * t * t * t);
				points.Add((Curve(p0.X, p1.X, p2.X, p3.X), Curve(p0.Z, p1.Z, p2.Z, p3.Z)));
			}
		}
		points.Add(control[^1]);
		return [.. points];
	}
}
