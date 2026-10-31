using Godot;

namespace AIFarm.Network;

/// <summary>
/// Makes the farmer's mouth move while his voice plays. His model has no mouth (the face is a
/// painted texture, and a beard covers where it would be), so a small dark opening is put on
/// his face, on his head bone so it moves with him, and opened and closed with how loud the
/// voice is at that instant: shut in the gaps between words, wide on loud vowels.
/// </summary>
public partial class TalkingMouth : Node3D
{
	// Where the mouth goes, in the head bone's space (the model's own units): just under the
	// moustache, flush with the front of the beard (which is at z = 0.46 there).
	private static readonly Vector3 Place = new(0f, 0.062f, 0.462f);
	private const float Depth = 0.045f; // front to back, enough to stand clear of the beard's curve
	private const float ClosedWidth = 0.18f;
	private const float OpenWidth = 0.15f; // rounder when it's wide open
	private const float MaxHeight = 0.095f;
	private const float Attack = 36f; // how fast it opens (per second)
	private const float Release = 16f; // and closes
	private static readonly Color Inside = new(0.16f, 0.04f, 0.05f);

	private NpcVoice _voice = null!;
	private MeshInstance3D _mouth = null!;
	private float _open;

	/// <summary>The voice whose loudness moves the mouth.</summary>
	public NpcVoice Voice
	{
		set => _voice = value;
	}

	/// <summary>How open the mouth is, 0 (shut, hidden) to 1.</summary>
	public float Openness => _open;

	/// <summary>Puts the mouth on the head of the model under <paramref name="model"/>; false if it has no skeleton.</summary>
	public bool Attach(Node3D model)
	{
		if (model.FindChildren("*", nameof(Skeleton3D), true, false).Count == 0 || model.FindChildren("*", nameof(Skeleton3D), true, false)[0] is not Skeleton3D skeleton
			|| skeleton.FindBone("head") < 0)
		{
			return false;
		}
		var attachment = new BoneAttachment3D { Name = "HeadAttachment", BoneName = "head" };
		skeleton.AddChild(attachment);
		_mouth = new MeshInstance3D
		{
			Name = "Mouth",
			Mesh = new SphereMesh { Radius = 0.5f, Height = 1f },
			MaterialOverride = new StandardMaterial3D { AlbedoColor = Inside, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
			Position = Place,
			Visible = false,
		};
		attachment.AddChild(_mouth);
		return true;
	}

	public override void _Process(double delta)
	{
		if (_mouth is null || !IsInstanceValid(_voice))
		{
			return;
		}
		// Speech is louder than it needs to be to read as open: ease the level so quiet syllables still show.
		var target = Mathf.Pow(_voice.Level, 0.7f);
		var rate = target > _open ? Attack : Release;
		_open = Mathf.Lerp(_open, target, 1f - Mathf.Exp(-rate * (float)delta));
		_mouth.Visible = _open > 0.06f;
		if (_mouth.Visible)
		{
			var height = Mathf.Lerp(0.02f, MaxHeight, _open);
			var width = Mathf.Lerp(ClosedWidth, OpenWidth, _open);
			_mouth.Scale = new Vector3(width, height, Depth);
		}
	}
}
