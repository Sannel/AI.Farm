using System.Collections.Generic;
using Godot;

namespace AIFarm.World;

/// <summary>
/// Loads a saved <see cref="WorldData"/> file and builds it: repeated models are drawn with
/// MultiMesh so thousands of props stay cheap, and colliders become static bodies.
/// </summary>
public partial class WorldBuilder : Node3D
{
	private const string ModelPath = "res://assets/kaykit_forest/{0}_Color1.gltf";

	[Export(PropertyHint.File, "*.json")]
	public string WorldFile { get; set; } = "res://world/valley.json";

	private readonly Dictionary<string, Mesh> _meshes = [];
	private readonly Dictionary<(Mesh Mesh, bool CastShadow), List<Transform3D>> _batches = [];

	public override void _Ready()
	{
		var json = FileAccess.GetFileAsString(WorldFile);
		if (string.IsNullOrEmpty(json))
		{
			GD.PushError($"Could not read world file {WorldFile}: {FileAccess.GetOpenError()}");
			return;
		}

		var data = WorldData.FromJson(json);
		foreach (var prop in data.Props)
		{
			AddInstance(prop);
		}
		foreach (var collider in data.Colliders)
		{
			AddCollider(collider);
		}
		FlushBatches();
	}

	private void AddInstance(WorldProp prop)
	{
		var basis = new Basis(Vector3.Up, prop.Yaw).Scaled(ToVector3(prop.Scale));
		var key = (GetMesh(prop.Model), prop.CastShadow);
		if (!_batches.TryGetValue(key, out var list))
		{
			list = [];
			_batches[key] = list;
		}
		list.Add(new Transform3D(basis, ToVector3(prop.Position)));
	}

	private void AddCollider(WorldCollider collider)
	{
		Shape3D shape;
		float height;
		switch (collider.Shape)
		{
			case ColliderShape.Box when collider.Size is { } size:
				shape = new BoxShape3D { Size = ToVector3(size) };
				height = size[1];
				break;
			case ColliderShape.Cylinder when collider.Radius is { } radius && collider.Height is { } cylinderHeight:
				shape = new CylinderShape3D { Radius = radius, Height = cylinderHeight };
				height = cylinderHeight;
				break;
			default:
				GD.PushWarning($"Skipping malformed {collider.Shape} collider in {WorldFile}");
				return;
		}

		var body = new StaticBody3D
		{
			Transform = new Transform3D(new Basis(Vector3.Up, collider.Yaw), ToVector3(collider.Position)),
		};
		// Godot shapes are centred; the file stores the base, so lift by half the height.
		body.AddChild(new CollisionShape3D { Shape = shape, Position = new Vector3(0f, height / 2f, 0f) });
		AddChild(body);
	}

	private Mesh GetMesh(string model)
	{
		if (_meshes.TryGetValue(model, out var cached))
		{
			return cached;
		}

		var scene = GD.Load<PackedScene>(string.Format(ModelPath, model));
		var root = scene.Instantiate();
		var mesh = FindMesh(root) ?? throw new System.InvalidOperationException($"No mesh found in {model}");
		root.Free();

		_meshes[model] = mesh;
		return mesh;
	}

	private static Mesh? FindMesh(Node node)
	{
		if (node is MeshInstance3D { Mesh: not null } instance)
		{
			return instance.Mesh;
		}
		foreach (var child in node.GetChildren())
		{
			if (FindMesh(child) is { } mesh)
			{
				return mesh;
			}
		}
		return null;
	}

	private void FlushBatches()
	{
		foreach (var ((mesh, castShadow), transforms) in _batches)
		{
			var multiMesh = new MultiMesh
			{
				TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
				Mesh = mesh,
				InstanceCount = transforms.Count,
			};
			for (var i = 0; i < transforms.Count; i++)
			{
				multiMesh.SetInstanceTransform(i, transforms[i]);
			}

			AddChild(new MultiMeshInstance3D
			{
				Name = mesh.ResourceName,
				Multimesh = multiMesh,
				CastShadow = castShadow
					? GeometryInstance3D.ShadowCastingSetting.On
					: GeometryInstance3D.ShadowCastingSetting.Off,
			});
		}
		_batches.Clear();
	}

	private static Vector3 ToVector3(float[] v) => new(v[0], v[1], v[2]);
}
