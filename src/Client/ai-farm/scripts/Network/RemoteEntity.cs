using System.Collections.Generic;
using AIFarm.Characters;
using AIFarm.Contracts;
using Godot;

namespace AIFarm.Network;

/// <summary>
/// An entity from the server shown in the world: its model, animated by a
/// <see cref="CharacterAnimator"/>, a collider so the player bumps into it (and the debug
/// animation menu can find it), and a name tag with its mood. Snapshots arrive about ten
/// times a second; in between it glides towards the latest position and facing.
/// </summary>
public partial class RemoteEntity : AnimatableBody3D
{
	private const string FarmerModel = "res://assets/kaykit_farmers/Farmer_A.glb";
	private const string FarmerAnimations = "res://assets/kaykit_animations/Rig_Medium";
	private const float Smoothing = 12f; // how quickly it closes the gap to the server's position
	private const float SnapDistance = 5f; // further off than this (e.g. first sight) it jumps there
	private const float TagRange = 14f; // name tags show within this many metres

	/// <summary>Display scale per model, matching how they were placed in the scene by hand.</summary>
	private static readonly Dictionary<string, float> ModelScales = new()
	{
		["Farmer"] = 0.75f,
		["Sheep"] = 0.85f,
		["Sheep_Female"] = 0.85f,
		["Sheep_Male"] = 0.85f,
		["Dog"] = 0.8f,
		["Dog_Female"] = 0.8f,
		["Dog_Male"] = 0.8f,
		["Poodle_Female"] = 0.8f,
		["Poodle_Male"] = 0.8f,
		["GuideDog_Female"] = 0.8f, ["GuideDog_Male"] = 0.8f,
		["ServiceDog_Female"] = 0.8f, ["ServiceDog_Male"] = 0.8f,
		["Wolf_Female"] = 0.8f, ["Wolf_Male"] = 0.8f,
		["Fox_Female"] = 0.8f, ["Fox_Male"] = 0.8f,
		["Raccoon_Female"] = 0.8f, ["Raccoon_Male"] = 0.8f,
	};

	private CharacterAnimator _animator = null!;
	private Label3D _tag = null!;
	private Vector3 _target;
	private float _targetYaw;
	private string _animation = "";
	private bool _placed;

	public EntitySnapshot State { get; private set; } = null!;

	/// <summary>Height of the top of its model, for speech bubbles.</summary>
	public float HeadHeight { get; private set; } = 1.8f;

	/// <summary>The moving mouth, for characters that have one (the farmer).</summary>
	public TalkingMouth? Mouth { get; private set; }

	/// <summary>Where its voice plays from.</summary>
	public NpcVoice Voice { get; private set; } = null!;

	/// <summary>Builds the node for a snapshot entity (call <see cref="Apply"/> after adding it to the tree).</summary>
	public static RemoteEntity Create(EntitySnapshot entity)
	{
		var node = new RemoteEntity { Name = $"{entity.Name} ({entity.Id})", SyncToPhysics = false, State = entity };
		// A model this client doesn't have yet (the server is newer) shows as a farmer rather than nothing.
		var path = ResourceLoader.Exists(ModelPath(entity.Model)) ? ModelPath(entity.Model) : FarmerModel;
		var model = GD.Load<PackedScene>(path).Instantiate<Node3D>();
		model.Name = "Model";
		model.Scale = Vector3.One * ModelScales.GetValueOrDefault(entity.Model, 1f);
		node.AddChild(model);

		var person = entity.Kind is EntityKind.Player or EntityKind.Npc; // farmers, on the KayKit rig
		node._animator = new CharacterAnimator
		{
			Name = "Animator",
			// The farmer's animations come from the KayKit rig library; animals carry their own.
			AnimationFolder = person ? FarmerAnimations : "",
			DefaultAnimation = person ? "Idle_A" : "Idle",
		};
		node.AddChild(node._animator);

		node._tag = new Label3D
		{
			Name = "Tag",
			Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
			FontSize = 40,
			OutlineSize = 10,
			PixelSize = 0.003f,
			// Only nearby tags, fading out, so a crowd doesn't turn into a wall of text.
			VisibilityRangeEnd = TagRange,
			VisibilityRangeEndMargin = 3f,
			VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
		};
		node.AddChild(node._tag);
		return node;
	}

	public override void _Ready()
	{
		// The collider is sized from the model's meshes once it's in the tree, as they appear
		// (the model may be scaled within this node).
		var bounds = AnimalPen.ModelBounds(GetNode<Node3D>("Model"), this);
		AddChild(new CollisionShape3D
		{
			Name = "Collision",
			Shape = new BoxShape3D { Size = bounds.Size },
			Position = bounds.GetCenter(),
		});
		_tag.Position = new Vector3(0, bounds.End.Y + 0.35f, 0);
		HeadHeight = bounds.End.Y;
		// The voice comes from its face: at the mouth, a little below the top of the head.
		Voice = new NpcVoice { Name = "Voice", Position = new Vector3(0, bounds.End.Y * 0.9f, bounds.End.Z * 0.5f) };
		AddChild(Voice);

		// The farmer's mouth moves with his voice.
		if (State.Kind == EntityKind.Npc && State.Model == "Farmer")
		{
			var mouth = new TalkingMouth { Name = "TalkingMouth", Voice = Voice };
			AddChild(mouth);
			mouth.Attach(GetNode<Node3D>("Model"));
			Mouth = mouth;
		}
	}

	/// <summary>Takes in the entity's latest state from a snapshot.</summary>
	public void Apply(EntitySnapshot entity)
	{
		State = entity;
		// The server puts animals on the ground, afloat on the water or under it.
		var y = entity.Y;
		if (entity.Kind == EntityKind.Animal && entity.Model.StartsWith("Bat_") && entity.Animation == "Flying")
			y += 1.5f;
		_target = new Vector3(entity.X, y, entity.Z);
		_targetYaw = entity.Yaw;
		if (!_placed || Position.DistanceTo(_target) > SnapDistance)
		{
			Position = _target;
			Rotation = new Vector3(0, _targetYaw, 0);
			_placed = true;
		}

		_tag.Text = entity.Vitals is { } vitals ? $"{entity.Name}\n{vitals.Mood}" : entity.Name;
		if (entity.Animation != _animation)
		{
			_animator.Play(Transition(_animation, entity.Animation));
			_animation = entity.Animation;
		}
	}

	public override void _Process(double delta)
	{
		if (!_placed)
		{
			return;
		}
		var weight = 1f - Mathf.Exp(-Smoothing * (float)delta);
		Position = Position.Lerp(_target, weight);
		Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, _targetYaw, weight), 0);
	}

	/// <summary>
	/// The server reports states (lying, standing); getting between them plays the transition,
	/// which the animator then follows with the state's loop.
	/// </summary>
	private static string Transition(string from, string to) => (from, to) switch
	{
		(not "Lie_Idle", "Lie_Idle") when from != "" => "Lie_Down",
		("Lie_Idle", not "Lie_Idle") => "Lie_StandUp",
		_ => to,
	};

	/// <summary>Where a model's scene lives: "Cow_Female" is assets/Cow/Cow_Female.glb.</summary>
	public static string ModelPath(string model) =>
		model == "Farmer" ? FarmerModel : $"res://assets/{model.Split('_')[0]}/{model}.glb";
}
