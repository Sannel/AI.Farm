using System;
using System.Collections.Generic;
using AIFarm.Contracts;
using Godot;

namespace AIFarm.World;

/// <summary>
/// The valley floor: flat ground, with the river and lake from <see cref="WaterMap"/> dug into it
/// and water on top. The shape comes from the shared map, so the server and the client agree on
/// where the water is. Flat land around the carved patch is drawn and collided as plain slabs.
/// </summary>
public partial class Terrain : StaticBody3D
{
	private const string TexturePath = "res://assets/kaykit_forest/forest_texture.png";
	private const string WaterShaderPath = "res://shaders/water.gdshader";
	private const string WaterfallShaderPath = "res://shaders/waterfall.gdshader";

	/// <summary>How far the (flat) ground reaches in every direction.</summary>
	private const float Reach = 200f;

	/// <summary>Metres between height samples on the carved patch.</summary>
	private const float Cell = 1f;

	private static readonly Color Mud = new(0.42f, 0.34f, 0.24f);
	private static readonly Color Sand = new(0.72f, 0.66f, 0.46f);

	private Color _grass = new(0.42f, 0.66f, 0.27f);

	public override void _Ready()
	{
		_grass = SampleGrass();
		var (minX, minZ, maxX, maxZ) = WaterMap.Bounds;
		var x0 = MathF.Floor(minX - 4f);
		var z0 = MathF.Floor(minZ - 4f);
		var x1 = MathF.Ceiling(maxX + 4f);
		var z1 = MathF.Ceiling(maxZ + 4f);

		BuildPatch(x0, z0, x1, z1);
		BuildFlatGround(x0, z0, x1, z1);
		BuildWater();
		BuildWaterfall();
	}

	/// <summary>The one flat green the plain has always been, read off the forest palette.</summary>
	private static Color SampleGrass()
	{
		var texture = GD.Load<Texture2D>(TexturePath);
		var image = texture?.GetImage();
		if (image is null)
		{
			return new Color(0.42f, 0.66f, 0.27f);
		}
		if (image.IsCompressed())
		{
			image.Decompress();
		}
		var size = image.GetSize();
		return image.GetPixel((int)(0.035f * size.X), (int)(0.089f * size.Y));
	}

	private Color GroundColor(float height)
	{
		if (height >= 0f)
		{
			return _grass;
		}
		var bank = -height / -WaterMap.SurfaceY; // 0 at ground level, 1 at the waterline
		if (bank < 1f)
		{
			return _grass.Lerp(Sand, Mathf.Clamp(bank * 1.3f, 0f, 1f));
		}
		return Sand.Lerp(Mud, Mathf.Clamp((bank - 1f) / 3f, 0f, 1f));
	}

	/// <summary>Patchy variation (gravel, silt, weed) so the bed has something to see.</summary>
	private static float BedNoise(float x, float z) =>
		0.5f + 0.25f * MathF.Sin(x * 0.9f + MathF.Sin(z * 0.7f) * 1.7f) + 0.25f * MathF.Sin(z * 1.3f - x * 0.45f + 2f);

	/// <summary>The carved ground: a heightfield mesh with a matching height-map collider.</summary>
	private void BuildPatch(float x0, float z0, float x1, float z1)
	{
		var columns = (int)((x1 - x0) / Cell) + 1;
		var rows = (int)((z1 - z0) / Cell) + 1;
		var heights = new float[columns * rows];
		var mesh = new SurfaceTool();
		mesh.Begin(Mesh.PrimitiveType.Triangles);

		for (var r = 0; r < rows; r++)
		{
			for (var c = 0; c < columns; c++)
			{
				heights[r * columns + c] = WaterMap.Height(x0 + c * Cell, z0 + r * Cell);
			}
		}
		Vector3 Vertex(int c, int r) => new(x0 + c * Cell, heights[r * columns + c], z0 + r * Cell);
		void Add(int c, int r)
		{
			var x = x0 + c * Cell;
			var z = z0 + r * Cell;
			// Slope from neighbouring heights.
			var dx = WaterMap.Height(x + 0.5f, z) - WaterMap.Height(x - 0.5f, z);
			var dz = WaterMap.Height(x, z + 0.5f) - WaterMap.Height(x, z - 0.5f);
			mesh.SetNormal(new Vector3(-dx, 1f, -dz).Normalized());
			var colour = GroundColor(heights[r * columns + c]);
			if (heights[r * columns + c] < 0f)
			{
				// Under the banks the bed is patchy: lighter gravel, darker silt, a hint of green weed.
				var n = BedNoise(x, z);
				colour = colour.Lerp(n > 0.6f ? new Color(0.62f, 0.58f, 0.48f) : new Color(0.25f, 0.33f, 0.2f), Mathf.Clamp(Mathf.Abs(n - 0.5f) * 2.4f, 0f, 0.85f));
			}
			mesh.SetColor(colour.SrgbToLinear()); // vertex colours are linear
			mesh.AddVertex(Vertex(c, r));
		}
		for (var r = 0; r + 1 < rows; r++)
		{
			for (var c = 0; c + 1 < columns; c++)
			{
				Add(c, r); Add(c + 1, r); Add(c, r + 1);
				Add(c + 1, r); Add(c + 1, r + 1); Add(c, r + 1);
			}
		}

		var material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 1f };
		AddChild(new MeshInstance3D { Name = "Patch", Mesh = mesh.Commit(), MaterialOverride = material });

		var shape = new HeightMapShape3D { MapWidth = columns, MapDepth = rows, MapData = heights };
		AddChild(new CollisionShape3D
		{
			Name = "PatchCollision",
			Shape = shape,
			Position = new Vector3(x0 + (columns - 1) * Cell / 2f, 0f, z0 + (rows - 1) * Cell / 2f),
		});
	}

	/// <summary>Plain ground (height 0) around the carved patch: four rectangles, drawn and collided.</summary>
	private void BuildFlatGround(float x0, float z0, float x1, float z1)
	{
		(float MinX, float MinZ, float MaxX, float MaxZ)[] slabs =
		[
			(-Reach, -Reach, x0, Reach),
			(x1, -Reach, Reach, Reach),
			(x0, -Reach, x1, z0),
			(x0, z1, x1, Reach),
		];
		var material = new StandardMaterial3D { AlbedoColor = _grass, Roughness = 1f };
		foreach (var (minX, minZ, maxX, maxZ) in slabs)
		{
			var size = new Vector2(maxX - minX, maxZ - minZ);
			var centre = new Vector3((minX + maxX) / 2f, 0f, (minZ + maxZ) / 2f);
			AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = size, Material = material }, Position = centre });
			AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(size.X, 4f, size.Y) }, Position = centre - new Vector3(0f, 2f, 0f) });
		}
	}

	private void BuildWater()
	{
		var material = new ShaderMaterial { Shader = GD.Load<Shader>(WaterShaderPath) };
		AddChild(new MeshInstance3D { Name = "River", Mesh = RiverMesh(), MaterialOverride = material });
		var lakeMaterial = (ShaderMaterial)material.Duplicate();
		lakeMaterial.SetShaderParameter("flow_speed", 0.04f);
		foreach (var basin in WaterMap.Basins)
		{
			AddChild(new MeshInstance3D { Name = basin.Name + "Water", Mesh = BasinMesh(basin), MaterialOverride = lakeMaterial });
		}
	}

	/// <summary>
	/// Where the river starts: water pours over the cliff above the spring pool. The cliff blocks are
	/// ragged, so once the physics world exists we feel for the face and the ledge below the high cliff
	/// and hang the fall there.
	/// </summary>
	private async void BuildWaterfall()
	{
		await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
		await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
		var space = GetWorld3D().DirectSpaceState;
		var spring = WaterMap.Spring;
		var outward = new Vector3(spring.X, 0f, spring.Z).Normalized();
		var from = new Vector3(spring.X, 1f, spring.Z) - outward * 8f;
		var face = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, from + outward * 40f));
		if (face.Count == 0)
		{
			return;
		}
		var hit = (Vector3)face["position"];
		var top = space.IntersectRay(PhysicsRayQueryParameters3D.Create(hit + outward * 0.6f + Vector3.Up * 40f, hit + outward * 0.6f));
		if (top.Count == 0)
		{
			return;
		}
		var ledge = ((Vector3)top["position"]).Y;
		var toward = -outward;                                // out of the cliff, towards the plain
		var along = new Vector3(-toward.Z, 0f, toward.X);     // across the fall
		const float halfWidth = 1.8f;
		const float stream = 11.5f;                           // runs back into the rock above
		var lip = new Vector3(hit.X, 0f, hit.Z);
		var surface = WaterMap.SurfaceY;

		// The water's profile, from the ledge out over the grassy lip and down: how far out from the
		// cliff face, how high, and how much of the way along it is. It stays clear of the grass
		// overhang by leaving the face before the lip does.
		(float Out, float Y)[] profile =
		[
			(-stream, ledge + 0.05f), (-0.4f, ledge + 0.05f), (0.25f, ledge + 0.02f), (0.65f, ledge - 0.3f),
			(0.8f, ledge - 1.0f), (0.8f, (ledge + surface) / 2f), (0.95f, surface + 0.5f), (1.25f, surface),
		];
		var mesh = new SurfaceTool();
		mesh.Begin(Mesh.PrimitiveType.Triangles);
		var length = 0f;
		var lengths = new float[profile.Length];
		for (var i = 1; i < profile.Length; i++)
		{
			length += new Vector2(profile[i].Out - profile[i - 1].Out, profile[i].Y - profile[i - 1].Y).Length();
			lengths[i] = length;
		}
		Vector3 At(int i, float side) => lip + toward * profile[i].Out + Vector3.Up * profile[i].Y + along * side;
		void Vertex(int i, float side, float v)
		{
			var nearBottom = Mathf.Clamp((lengths[i] - (length - 2.2f)) / 2.2f, 0f, 1f);
			mesh.SetNormal(Vector3.Up.Lerp(toward, Mathf.Clamp(1f - nearBottom, 0f, 1f)).Normalized());
			mesh.SetColor(new Color(Mathf.Abs(v - 0.5f) * 2f, nearBottom, 0f));
			mesh.SetUV(new Vector2(lengths[i] - lengths[profile.Length - 1] + length, v));
			mesh.AddVertex(At(i, side));
		}
		// The run across the ledge is fed in from the rock; time it so U flows toward the lip and down.
		float[] sides = [-halfWidth, 0f, halfWidth];
		for (var i = 0; i + 1 < profile.Length; i++)
		{
			for (var k = 0; k < 2; k++)
			{
				var v0 = k * 0.5f;
				var v1 = v0 + 0.5f;
				Vertex(i, sides[k], v0); Vertex(i + 1, sides[k], v0); Vertex(i, sides[k + 1], v1);
				Vertex(i + 1, sides[k], v0); Vertex(i + 1, sides[k + 1], v1); Vertex(i, sides[k + 1], v1);
			}
		}

		var material = new ShaderMaterial { Shader = GD.Load<Shader>(WaterfallShaderPath) };
		AddChild(new MeshInstance3D { Name = "Waterfall", Mesh = mesh.Commit(), MaterialOverride = material });

		// Spray rising where it hits the pool.
		var spray = new CpuParticles3D
		{
			Name = "Spray",
			Position = lip + toward * 1.4f + Vector3.Up * (surface + 0.3f),
			Amount = 48,
			Lifetime = 1.8f,
			EmissionShape = CpuParticles3D.EmissionShapeEnum.Box,
			EmissionBoxExtents = new Vector3(halfWidth, 0.1f, 0.5f),
			Direction = Vector3.Up + toward * 0.3f,
			Spread = 25f,
			InitialVelocityMin = 0.5f,
			InitialVelocityMax = 1.3f,
			Gravity = new Vector3(0f, 0.15f, 0f),
			ScaleAmountMin = 0.6f,
			ScaleAmountMax = 1.4f,
			Mesh = new SphereMesh
			{
				Radius = 0.3f,
				Height = 0.6f,
				RadialSegments = 8,
				Rings = 4,
				Material = new StandardMaterial3D
				{
					ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
					Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
					VertexColorUseAsAlbedo = true,
					AlbedoColor = new Color(1f, 1f, 1f, 0.3f),
				},
			},
			ColorRamp = new Gradient { Colors = [new Color(1f, 1f, 1f, 0.35f), new Color(1f, 1f, 1f, 0f)] },
		};
		// A particle's box is aligned to the world, but the fall faces any way: turn it to match.
		spray.EmissionBoxExtents = new Vector3(MathF.Abs(along.X) * halfWidth + MathF.Abs(toward.X) * 0.5f, 0.1f,
			MathF.Abs(along.Z) * halfWidth + MathF.Abs(toward.Z) * 0.5f);
		AddChild(spray);
	}

	/// <summary>A ribbon along the river's centre line; U runs along it in metres and R of the colour is closeness to a bank.</summary>
	private static ArrayMesh RiverMesh()
	{
		var line = WaterMap.Centerline;
		var half = WaterMap.RiverEdgeHalfWidth + 0.35f;
		var mesh = new SurfaceTool();
		mesh.Begin(Mesh.PrimitiveType.Triangles);
		var along = 0f;
		var rows = new List<(Vector3 Left, Vector3 Middle, Vector3 Right, float U)>();
		for (var i = 0; i < line.Count; i++)
		{
			var (px, pz) = line[i];
			var (ax, az) = line[Math.Max(i - 1, 0)];
			var (bx, bz) = line[Math.Min(i + 1, line.Count - 1)];
			var tangent = new Vector2(bx - ax, bz - az).Normalized();
			var side = new Vector2(-tangent.Y, tangent.X);
			if (i > 0)
			{
				along += new Vector2(px - ax, pz - az).Length();
			}
			var y = WaterMap.SurfaceY;
			rows.Add((new Vector3(px + side.X * half, y, pz + side.Y * half), new Vector3(px, y, pz),
				new Vector3(px - side.X * half, y, pz - side.Y * half), along));
		}

		void Vertex(Vector3 position, float u, float v, float edge)
		{
			mesh.SetNormal(Vector3.Up);
			mesh.SetColor(new Color(edge, 0f, 0f));
			mesh.SetUV(new Vector2(u, v));
			mesh.AddVertex(position);
		}
		for (var i = 0; i + 1 < rows.Count; i++)
		{
			var a = rows[i];
			var b = rows[i + 1];
			// Two strips (left half and right half), each two triangles.
			Vertex(a.Left, a.U, 1f, 1f); Vertex(b.Left, b.U, 1f, 1f); Vertex(a.Middle, a.U, 0.5f, 0f);
			Vertex(b.Left, b.U, 1f, 1f); Vertex(b.Middle, b.U, 0.5f, 0f); Vertex(a.Middle, a.U, 0.5f, 0f);
			Vertex(a.Middle, a.U, 0.5f, 0f); Vertex(b.Middle, b.U, 0.5f, 0f); Vertex(a.Right, a.U, 0f, 1f);
			Vertex(b.Middle, b.U, 0.5f, 0f); Vertex(b.Right, b.U, 0f, 1f); Vertex(a.Right, a.U, 0f, 1f);
		}
		return mesh.Commit();
	}

	/// <summary>A disc of water for a lake or pool, a little wider than its shoreline so it tucks under the bank.</summary>
	private static ArrayMesh BasinMesh(WaterMap.Basin basin)
	{
		const int segments = 56;
		var radius = basin.EdgeRadius + 0.35f;
		var mesh = new SurfaceTool();
		mesh.Begin(Mesh.PrimitiveType.Triangles);
		void Vertex(float angle, float distance)
		{
			var x = basin.X + MathF.Cos(angle) * distance;
			var z = basin.Z + MathF.Sin(angle) * distance;
			mesh.SetNormal(Vector3.Up);
			mesh.SetColor(new Color(Mathf.Clamp((distance - (basin.EdgeRadius - 1.2f)) / 1.55f, 0f, 1f), 0f, 0f));
			mesh.SetUV(new Vector2(x, z) * 0.25f);
			mesh.AddVertex(new Vector3(x, WaterMap.SurfaceY, z));
		}
		float[] rings = [0f, radius * 0.5f, radius * 0.8f, radius];
		for (var s = 0; s < segments; s++)
		{
			var a0 = s * Mathf.Tau / segments;
			var a1 = (s + 1) * Mathf.Tau / segments;
			// Centre fan (CCW seen from above gives an up-facing surface).
			Vertex(0f, 0f); Vertex(a1, rings[1]); Vertex(a0, rings[1]);
			for (var k = 1; k + 1 < rings.Length; k++)
			{
				Vertex(a0, rings[k]); Vertex(a1, rings[k]); Vertex(a0, rings[k + 1]);
				Vertex(a1, rings[k]); Vertex(a1, rings[k + 1]); Vertex(a0, rings[k + 1]);
			}
		}
		return mesh.Commit();
	}
}
