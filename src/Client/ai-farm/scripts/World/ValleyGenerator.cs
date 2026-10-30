using System.Collections.Generic;
using Godot;

namespace AIFarm.World;

/// <summary>
/// Procedurally lays out the starting valley: a grassy plain with sparse trees, ringed by
/// forested cliff terraces whose height rolls around the valley like a wave. Only used by the
/// generator tool; the game loads the saved <see cref="WorldData"/> instead of calling this.
/// </summary>
public sealed class ValleyGenerator
{
	private static readonly string[] PlainTrees = ["Tree_1_A", "Tree_2_A", "Tree_3_A", "Tree_4_A", "Tree_5_A", "Tree_6_A", "Tree_7_A"];
	private static readonly string[] ForestTrees = ["Tree_1_B", "Tree_2_B", "Tree_3_B", "Tree_4_B", "Tree_5_A", "Tree_6_A", "Tree_7_A"];
	private static readonly string[] GrassClumps = ["Grass_1_A", "Grass_1_B", "Grass_1_C", "Grass_2_A", "Grass_2_B", "Grass_2_C"];
	private static readonly string[] PlainExtras = ["Bush_2_B", "Bush_1_D", "Rock_1_D", "Rock_3_G", "Rock_5_D"];

	/// <summary>One ring of cliff blocks around the valley.</summary>
	private readonly record struct Terrace(float Radius, string Model, float Height, float BaseY);

	private static readonly Terrace[] Terraces =
	[
		new(100f, "Hill_12x12x4", 4f, 0f),
		new(111f, "Hill_12x12x8", 8f, 0f),
		new(122f, "Hill_12x12x8", 8f, 6f),
		new(133f, "Hill_12x12x8", 8f, 12f),
		new(144f, "Hill_12x12x8", 8f, 18f),
		new(155f, "Hill_12x12x8", 8f, 24f),
		new(166f, "Hill_12x12x8", 8f, 30f),
	];

	private const float HillSize = 12f;
	private const float TrunkColliderHeight = 4f;

	public ulong Seed { get; init; } = 20261029;

	/// <summary>Radius of the open, walkable plain.</summary>
	public float PlainRadius { get; init; } = 92f;

	public int PlainTreeCount { get; init; } = 45;

	public int PlainExtraCount { get; init; } = 30;

	public int GrassCount { get; init; } = 3000;

	/// <summary>Keeps the spawn point clear of trees.</summary>
	public float SpawnClearRadius { get; init; } = 10f;

	private readonly RandomNumberGenerator _rng = new();
	private readonly float[] _wavePhases = new float[5];
	private WorldData _data = new();

	public WorldData Generate()
	{
		_rng.Seed = Seed;
		_data = new WorldData { Seed = Seed };

		BuildTerraces();
		var trees = ScatterPlainTrees();
		ScatterPlainExtras(trees);
		ScatterGrass();

		return _data;
	}

	private void BuildTerraces()
	{
		// Random phases (from the seed) so the ridge line is unique to this valley.
		for (var i = 0; i < _wavePhases.Length; i++)
		{
			_wavePhases[i] = _rng.Randf() * Mathf.Tau;
		}

		for (var ring = 0; ring < Terraces.Length; ring++)
		{
			var terrace = Terraces[ring];
			// Outer rings sit beyond the shadow distance; skipping them saves a big shadow pass on handhelds.
			var castShadow = ring == 0;
			var count = Mathf.CeilToInt(Mathf.Tau * terrace.Radius / (HillSize * 0.88f));
			for (var i = 0; i < count; i++)
			{
				var angle = Mathf.Tau * i / count;
				var radius = terrace.Radius + EdgeWobble(angle) + _rng.RandfRange(-1f, 1f);
				var stretch = RingStretch(ring, angle);
				var position = new Vector3(Mathf.Sin(angle) * radius, terrace.BaseY * stretch, Mathf.Cos(angle) * radius);
				// Face the valley centre, with a random quarter turn to vary the cliff pattern.
				var yaw = angle + _rng.RandiRange(0, 3) * Mathf.Pi / 2f;
				AddProp(terrace.Model, position, yaw, new Vector3(1f, stretch, 1f), castShadow);

				// Only the innermost ring is reachable, so only it needs collision.
				if (ring == 0)
				{
					AddBoxCollider(position, yaw, new Vector3(HillSize, terrace.Height, HillSize));
				}
			}

			ScatterTerraceForest(ring, castShadow);
		}
	}

	private void ScatterTerraceForest(int ring, bool castShadow)
	{
		var terrace = Terraces[ring];
		// The visible strip of each terrace top runs from its inner edge to where the next ring starts.
		var inner = terrace.Radius - HillSize / 2f + 1f;
		var outer = terrace.Radius + HillSize / 2f - 2f;
		var area = Mathf.Pi * (outer * outer - inner * inner);
		var treeCount = (int)(area / 11f);

		for (var i = 0; i < treeCount; i++)
		{
			var angle = _rng.Randf() * Mathf.Tau;
			var radius = _rng.RandfRange(inner, outer) + EdgeWobble(angle);
			var stretch = RingStretch(ring, angle);
			var topY = (terrace.BaseY + terrace.Height) * stretch;
			var position = new Vector3(Mathf.Sin(angle) * radius, topY, Mathf.Cos(angle) * radius);
			var model = ForestTrees[_rng.RandiRange(0, ForestTrees.Length - 1)];
			// Taller terraces get bigger trees so the forest still covers the stretched cliffs.
			var size = Mathf.Sqrt(stretch);
			AddRandomUpright(model, position, 1.1f * size, 1.8f * size, castShadow);
		}
	}

	/// <summary>
	/// Vertical stretch for a terrace ring at an angle around the valley. Stretching every ring by the
	/// same wave keeps them stacked without gaps while the skyline rises into peaks and dips into saddles.
	/// The innermost ring stays flat so the walkable edge is a consistent height.
	/// </summary>
	private float RingStretch(int ring, float angle)
	{
		if (ring == 0)
		{
			return 1f;
		}

		// Integer frequencies keep the wave seamless all the way around; a small per-ring twist
		// bends the ridges so they don't line up like spokes.
		var a = angle + ring * 0.04f;
		var wave = 1f
			+ 0.55f * Mathf.Sin(2f * a + _wavePhases[0])
			+ 0.35f * Mathf.Sin(3f * a + _wavePhases[1])
			+ 0.2f * Mathf.Sin(5f * a + _wavePhases[2]);
		return Mathf.Max(wave, 0.35f);
	}

	/// <summary>Pushes the valley edge in and out so it isn't a perfect circle.</summary>
	private float EdgeWobble(float angle)
	{
		return 3.5f * Mathf.Sin(3f * angle + _wavePhases[3]) + 1.5f * Mathf.Sin(7f * angle + _wavePhases[4]);
	}

	private List<Vector3> ScatterPlainTrees()
	{
		const float minSpacing = 9f;
		var placed = new List<Vector3>();
		var attempts = 0;

		while (placed.Count < PlainTreeCount && attempts++ < PlainTreeCount * 50)
		{
			var position = RandomPointOnPlain(SpawnClearRadius, PlainRadius - 6f);
			if (placed.Exists(p => p.DistanceTo(position) < minSpacing))
			{
				continue;
			}

			placed.Add(position);
			var model = PlainTrees[_rng.RandiRange(0, PlainTrees.Length - 1)];
			var scale = AddRandomUpright(model, position, 0.9f, 1.3f, castShadow: true);
			AddTrunkCollider(position, 0.45f * scale);
		}

		return placed;
	}

	private void ScatterPlainExtras(List<Vector3> trees)
	{
		for (var i = 0; i < PlainExtraCount; i++)
		{
			// Mostly tuck bushes and rocks near trees so the plain stays open.
			var position = trees.Count > 0 && _rng.Randf() < 0.7f
				? trees[_rng.RandiRange(0, trees.Count - 1)] + RandomFlatOffset(1.5f, 3.5f)
				: RandomPointOnPlain(SpawnClearRadius, PlainRadius - 4f);
			var model = PlainExtras[_rng.RandiRange(0, PlainExtras.Length - 1)];
			AddRandomUpright(model, position, 0.8f, 1.2f, castShadow: true);
		}
	}

	private void ScatterGrass()
	{
		for (var i = 0; i < GrassCount; i++)
		{
			var position = RandomPointOnPlain(0f, PlainRadius);
			var model = GrassClumps[_rng.RandiRange(0, GrassClumps.Length - 1)];
			AddRandomUpright(model, position, 0.5f, 1.0f, castShadow: false);
		}
	}

	private Vector3 RandomPointOnPlain(float minRadius, float maxRadius)
	{
		// sqrt keeps the distribution even across the disc instead of bunching at the centre.
		var t = Mathf.Sqrt(_rng.Randf());
		var radius = Mathf.Lerp(minRadius, maxRadius, t);
		var angle = _rng.Randf() * Mathf.Tau;
		return new Vector3(Mathf.Sin(angle) * radius, 0f, Mathf.Cos(angle) * radius);
	}

	private Vector3 RandomFlatOffset(float minDistance, float maxDistance)
	{
		var angle = _rng.Randf() * Mathf.Tau;
		var distance = _rng.RandfRange(minDistance, maxDistance);
		return new Vector3(Mathf.Sin(angle) * distance, 0f, Mathf.Cos(angle) * distance);
	}

	/// <summary>Adds a prop with a random yaw and uniform scale; returns the scale used.</summary>
	private float AddRandomUpright(string model, Vector3 position, float minScale, float maxScale, bool castShadow)
	{
		var yaw = _rng.Randf() * Mathf.Tau;
		var scale = _rng.RandfRange(minScale, maxScale);
		AddProp(model, position, yaw, Vector3.One * scale, castShadow);
		return scale;
	}

	private void AddProp(string model, Vector3 position, float yaw, Vector3 scale, bool castShadow)
	{
		_data.Props.Add(new WorldProp
		{
			Model = model,
			Position = ToArray(position),
			Yaw = Round(Mathf.Wrap(yaw, 0f, Mathf.Tau), 4),
			Scale = ToArray(scale),
			CastShadow = castShadow,
		});
	}

	private void AddBoxCollider(Vector3 position, float yaw, Vector3 size)
	{
		_data.Colliders.Add(new WorldCollider
		{
			Shape = ColliderShape.Box,
			Position = ToArray(position),
			Yaw = Round(Mathf.Wrap(yaw, 0f, Mathf.Tau), 4),
			Size = ToArray(size),
		});
	}

	private void AddTrunkCollider(Vector3 position, float radius)
	{
		_data.Colliders.Add(new WorldCollider
		{
			Shape = ColliderShape.Cylinder,
			Position = ToArray(position),
			Radius = Round(radius, 3),
			Height = TrunkColliderHeight,
		});
	}

	// Millimetre precision is plenty and keeps the saved file small.
	private static float[] ToArray(Vector3 v) => [Round(v.X, 3), Round(v.Y, 3), Round(v.Z, 3)];

	private static float Round(float value, int digits) => (float)System.Math.Round(value, digits);
}
