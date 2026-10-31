using Godot;

namespace AIFarm.Characters;

/// <summary>
/// Lines up animal models in rows so they can be looked at and tried out in the valley. Each
/// becomes a static body (so the player can't walk through it and the debug animation menu can
/// find it) holding the model, a <see cref="CharacterAnimator"/> playing its own animations, and
/// a box collider fitted to the model. Animals face +Z; rows run along +X and stack along +Z.
/// </summary>
public partial class AnimalPen : Node3D
{
	/// <summary>The animal models (.glb with their own animations), in the order to place them.</summary>
	[Export(PropertyHint.ArrayType)]
	public string[] Models { get; set; } = [];

	/// <summary>How many animals stand side by side before starting the next row.</summary>
	[Export]
	public int PerRow { get; set; } = 8;

	/// <summary>Space left between neighbouring animals and between rows, in metres.</summary>
	[Export]
	public float Gap { get; set; } = 2.5f;

	public override void _Ready()
	{
		var x = 0f;
		var z = 0f;
		var rowDepth = 0f;
		for (var i = 0; i < Models.Length; i++)
		{
			if (i > 0 && i % PerRow == 0)
			{
				x = 0f;
				z += rowDepth + Gap;
				rowDepth = 0f;
			}

			var scene = GD.Load<PackedScene>(Models[i]);
			if (scene is null)
			{
				GD.PushError($"AnimalPen could not load {Models[i]}");
				continue;
			}

			var body = new StaticBody3D { Name = Models[i].GetFile().GetBaseName() };
			AddChild(body);
			var model = scene.Instantiate<Node3D>();
			model.Name = "Model";
			body.AddChild(model);

			var bounds = ModelBounds(model);
			body.Position = new Vector3(x - bounds.Position.X, 0f, z - bounds.Position.Z);
			x += bounds.Size.X + Gap;
			rowDepth = Mathf.Max(rowDepth, bounds.Size.Z);

			body.AddChild(new CollisionShape3D
			{
				Name = "Collision",
				Shape = new BoxShape3D { Size = bounds.Size },
				Position = bounds.GetCenter(),
			});
			body.AddChild(new CharacterAnimator { Name = "Animator", AnimationFolder = "", DefaultAnimation = "Idle" });
		}
	}

	/// <summary>The model's bounding box in its own space (rest pose), from all its meshes.</summary>
	public static Aabb ModelBounds(Node3D model) => ModelBounds(model, model);

	/// <summary>
	/// The model's bounding box as seen from <paramref name="frame"/>: pass the node the model is
	/// scaled and placed within, to get sizes as they actually appear (a model scaled to 75% is
	/// measured at 75%).
	/// </summary>
	public static Aabb ModelBounds(Node3D model, Node3D frame)
	{
		var bounds = new Aabb();
		var first = true;
		var toModel = frame.GlobalTransform.AffineInverse();
		foreach (var node in model.FindChildren("*", nameof(MeshInstance3D), true, false))
		{
			var mesh = (MeshInstance3D)node;
			var box = toModel * mesh.GlobalTransform * mesh.GetAabb();
			bounds = first ? box : bounds.Merge(box);
			first = false;
		}
		return bounds;
	}
}
