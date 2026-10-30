using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIFarm.World;

/// <summary>
/// Static layout of a world: every placed prop and the collision shapes around them.
/// The client renders it and the server can use the same data for collision and positions,
/// so this file deliberately has no Godot dependencies. Vectors are [x, y, z] arrays in
/// metres, rotations are yaw in radians around +Y.
/// </summary>
public sealed class WorldData
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;

	/// <summary>Seed the layout was generated from; informational only.</summary>
	public ulong Seed { get; set; }

	public List<WorldProp> Props { get; set; } = [];

	public List<WorldCollider> Colliders { get; set; } = [];

	private static readonly JsonSerializerOptions Options = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
	};

	public static WorldData FromJson(string json)
	{
		var data = JsonSerializer.Deserialize<WorldData>(json, Options)
			?? throw new InvalidDataException("World file is empty.");
		if (data.Version != CurrentVersion)
		{
			throw new InvalidDataException($"Unsupported world file version {data.Version} (expected {CurrentVersion}).");
		}
		return data;
	}

	/// <summary>
	/// Serialises with one prop or collider per line, so the file stays compact but
	/// regenerating it produces readable git diffs.
	/// </summary>
	public string ToJson()
	{
		var json = new StringBuilder();
		json.Append($"{{\"version\":{Version},\"seed\":{Seed},\n\"props\":[\n");
		AppendLines(json, Props);
		json.Append("],\n\"colliders\":[\n");
		AppendLines(json, Colliders);
		json.Append("]}\n");
		return json.ToString();
	}

	private static void AppendLines<T>(StringBuilder json, List<T> items)
	{
		for (var i = 0; i < items.Count; i++)
		{
			json.Append(JsonSerializer.Serialize(items[i], Options));
			json.Append(i < items.Count - 1 ? ",\n" : "\n");
		}
	}
}

/// <summary>One placed model instance.</summary>
public sealed class WorldProp
{
	/// <summary>Model name without colour suffix or extension, e.g. "Tree_1_A".</summary>
	public string Model { get; set; } = "";

	public float[] Position { get; set; } = [0, 0, 0];

	public float Yaw { get; set; }

	public float[] Scale { get; set; } = [1, 1, 1];

	/// <summary>Rendering hint: distant and ground-cover props skip shadows.</summary>
	public bool CastShadow { get; set; }
}

public enum ColliderShape
{
	Box,
	Cylinder,
}

/// <summary>
/// A static collision shape. Position is the centre of the shape's base; it extends
/// upward from there.
/// </summary>
public sealed class WorldCollider
{
	public ColliderShape Shape { get; set; }

	public float[] Position { get; set; } = [0, 0, 0];

	public float Yaw { get; set; }

	/// <summary>Box only: full width, height and depth.</summary>
	public float[]? Size { get; set; }

	/// <summary>Cylinder only.</summary>
	public float? Radius { get; set; }

	/// <summary>Cylinder only.</summary>
	public float? Height { get; set; }
}
