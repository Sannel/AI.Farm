using System.Collections.Generic;
using Godot;

namespace AIFarm.Characters;

/// <summary>
/// Builds one AnimationLibrary per KayKit rig by merging every animation file in the rig's folder
/// (General, MovementBasic, Tools, ...). The merged library is cached and shared by every
/// character on that rig, since the animations themselves are read-only.
/// </summary>
public static class RigAnimations
{
	/// <summary>Animations whose names contain one of these play continuously until replaced.</summary>
	private static readonly string[] LoopingKeywords =
	[
		"Idle", "Walking", "Running", "Crawling", "Crouching", "Sneaking", "Blocking", "Aiming",
		"Shooting", "Spellcasting", "Reeling", "Struggling", "Chopping", "Digging", "Hammering",
		"Sawing", "Pickaxing", "Lockpicking", "Working", "Holding", "Push_Ups", "Sit_Ups",
		"Cheering", "Waving", "Eating", "Sleeping", "Trotting", "Stalking",
	];

	private static readonly Dictionary<string, AnimationLibrary> Cache = [];
	private static readonly Dictionary<string, List<(string Group, string[] Names)>> GroupCache = [];

	public static AnimationLibrary Get(string folder)
	{
		if (Cache.TryGetValue(folder, out var cached))
		{
			return cached;
		}

		var merged = new AnimationLibrary();
		var groups = new List<(string Group, string[] Names)>();
		foreach (var file in ResourceLoader.ListDirectory(folder))
		{
			if (!file.EndsWith(".glb"))
			{
				continue;
			}

			var source = GD.Load<PackedScene>(folder.PathJoin(file)).Instantiate();
			var library = source.GetNodeOrNull<AnimationPlayer>("AnimationPlayer")?.GetAnimationLibrary("");
			if (library is not null)
			{
				var added = new List<string>();
				foreach (var name in library.GetAnimationList())
				{
					// Every file repeats T-Pose; keep the first copy of any duplicate.
					if (!merged.HasAnimation(name))
					{
						merged.AddAnimation(name, library.GetAnimation(name));
						added.Add(name);
					}
				}
				groups.Add((GroupName(folder, file), added.ToArray()));
			}
			source.Free();
		}

		ApplyLoopModes(merged);
		Cache[folder] = merged;
		GroupCache[folder] = groups;
		return merged;
	}

	/// <summary>The rig's animations grouped by the file they came from, e.g. "Movement Basic".</summary>
	public static IReadOnlyList<(string Group, string[] Names)> GetGroups(string folder)
	{
		Get(folder);
		return GroupCache[folder];
	}

	/// <summary>"Rig_Medium_MovementBasic.glb" in folder "Rig_Medium" becomes "Movement Basic".</summary>
	private static string GroupName(string folder, string file)
	{
		var name = file.GetBaseName().TrimPrefix(folder.GetFile() + "_");
		return System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
	}

	/// <summary>glTF imports never loop, so mark the continuous animations in a library.</summary>
	public static void ApplyLoopModes(AnimationLibrary library)
	{
		foreach (var name in library.GetAnimationList())
		{
			library.GetAnimation(name).LoopMode = IsLooping(name)
				? Animation.LoopModeEnum.Linear
				: Animation.LoopModeEnum.None;
		}
	}

	public static bool IsLooping(string name)
	{
		foreach (var keyword in LoopingKeywords)
		{
			if (name.Contains(keyword))
			{
				return true;
			}
		}
		return false;
	}
}
