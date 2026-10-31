using System.Collections.Generic;
using Godot;

namespace AIFarm.Characters;

/// <summary>
/// Animates a character with every animation available to it. KayKit ships character models
/// without animations and the animations separately per rig, so by default this attaches the
/// rig's merged library (see <see cref="RigAnimations"/>) to the model's matching skeleton.
/// Models that carry their own animations (like the sheep) leave <see cref="AnimationFolder"/>
/// empty and use the AnimationPlayer from their import instead.
///
/// Every change crossfades over <see cref="BlendTime"/>. Looping animations become the character's
/// base state; one-shots play once and then blend into their natural follow-up (e.g. Sit_Floor_Down
/// into Sit_Floor_Idle) or back to the base loop.
/// </summary>
public partial class CharacterAnimator : Node
{
	/// <summary>One-shots that lead into a specific animation instead of the base loop.</summary>
	private static readonly Dictionary<string, string> FollowUps = new()
	{
		["Sit_Chair_Down"] = "Sit_Chair_Idle",
		["Sit_Floor_Down"] = "Sit_Floor_Idle",
		["Sit_Down"] = "Sit_Idle",
		["Lie_Down"] = "Lie_Idle",
		["Death_A"] = "Death_A_Pose",
		["Death_B"] = "Death_B_Pose",
		["Jump_Start"] = "Jump_Idle",
		["Fishing_Cast"] = "Fishing_Idle",
		["Fishing_Bite"] = "Fishing_Idle",
		["Fishing_Tug"] = "Fishing_Idle",
		["Ranged_Bow_Draw"] = "Ranged_Bow_Aiming_Idle",
		["Ranged_Bow_Draw_Up"] = "Ranged_Bow_Aiming_Idle",
		["Ranged_Magic_Raise"] = "Ranged_Magic_Spellcasting",
		["Skeletons_Death"] = "Skeletons_Death_Pose",
		["Skeletons_Awaken_Floor"] = "Skeletons_Idle",
		["Skeletons_Awaken_Floor_Long"] = "Skeletons_Idle",
		["Skeletons_Awaken_Standing"] = "Skeletons_Idle",
		["Skeletons_Death_Resurrect"] = "Skeletons_Idle",
	};

	/// <summary>One-shots that end a sitting/lying state, so they return to the default animation.</summary>
	private static readonly HashSet<string> ReturnToDefault =
		["Sit_Chair_StandUp", "Sit_Floor_StandUp", "Sit_StandUp", "Lie_StandUp"];

	/// <summary>The imported character model; its skeleton must match the animation rig.</summary>
	[Export]
	public NodePath ModelPath { get; set; } = "../Model";

	/// <summary>Folder holding the rig's animation .glb files; empty to use the model's own animations.</summary>
	[Export(PropertyHint.Dir)]
	public string AnimationFolder { get; set; } = "res://assets/kaykit_animations/Rig_Medium";

	/// <summary>Looping animation the character starts in and returns to after standing up.</summary>
	[Export]
	public string DefaultAnimation { get; set; } = "Idle_A";

	/// <summary>Crossfade time in seconds between any two animations.</summary>
	[Export]
	public float BlendTime { get; set; } = 0.25f;

	private AnimationPlayer? _player;
	private string _baseAnimation = "";
	private bool _droppingsDeposited;
	private IReadOnlyList<(string Group, string[] Names)> _groups = [];

	/// <summary>Every animation this character can play.</summary>
	public string[] Animations => _player?.GetAnimationList() ?? [];

	public string CurrentAnimation => _player?.CurrentAnimation ?? "";

	public override void _PhysicsProcess(double delta)
	{
		if (_player is null || _player.CurrentAnimation != "Pooping" || _droppingsDeposited)
			return;
		if (_player.CurrentAnimationPosition >= _player.CurrentAnimationLength * 0.5)
		{
			_droppingsDeposited = true;
			AnimalDroppings.Deposit(GetNode<Node3D>(ModelPath), GetParent<Node3D>());
		}
	}

	/// <summary>Animations grouped by the rig file they came from, for menus.</summary>
	public IReadOnlyList<(string Group, string[] Names)> AnimationGroups => _groups;

	public override void _Ready()
	{
		var model = GetNode<Node3D>(ModelPath);

		if (string.IsNullOrEmpty(AnimationFolder))
		{
			_player = model.GetNodeOrNull<AnimationPlayer>("AnimationPlayer");
			if (_player is null)
			{
				GD.PushError($"{model.Name} has no embedded animations and no AnimationFolder is set");
				return;
			}
			RigAnimations.ApplyLoopModes(_player.GetAnimationLibrary(""));
			_groups = [(GetParent().Name, _player.GetAnimationList())];
		}
		else
		{
			// Track paths are relative to the model root (e.g. "Rig_Medium/Skeleton3D:hips"),
			// so the player lives directly under the model.
			_player = new AnimationPlayer { Name = "AnimationPlayer" };
			model.AddChild(_player);
			_player.AddAnimationLibrary("", RigAnimations.Get(AnimationFolder));
			_groups = RigAnimations.GetGroups(AnimationFolder);
		}

		_player.PlaybackDefaultBlendTime = BlendTime;
		_player.AnimationFinished += OnAnimationFinished;

		Play(DefaultAnimation);
	}

	/// <summary>
	/// Crossfades to an animation. Looping animations replace the base state; one-shots play
	/// once and then continue with their follow-up or the base loop.
	/// </summary>
	public void Play(string name)
	{
		if (_player is null)
		{
			return;
		}
		if (!_player.HasAnimation(name))
		{
			GD.PushWarning($"{GetParent().Name} has no animation '{name}'");
			return;
		}

		var alreadyPlaying = _player.CurrentAnimation == name;
		if (name == "Pooping") _droppingsDeposited = false;
		if (RigAnimations.IsLooping(name))
		{
			_baseAnimation = name;
			if (alreadyPlaying)
			{
				return;
			}
		}
		else if (alreadyPlaying)
		{
			// Replaying the same one-shot restarts it rather than being ignored.
			_player.Seek(0, true);
			return;
		}

		_player.Play(name);
	}

	private void OnAnimationFinished(StringName finished)
	{
		var name = finished.ToString();

		if (FollowUps.TryGetValue(name, out var next))
		{
			Play(next);
		}
		else if (ReturnToDefault.Contains(name))
		{
			Play(DefaultAnimation);
		}
		else if (!name.EndsWith("Pose") && _baseAnimation != "")
		{
			// Poses (Death_A_Pose, T-Pose, ...) hold; everything else returns to the base loop.
			Play(_baseAnimation);
		}
	}
}
