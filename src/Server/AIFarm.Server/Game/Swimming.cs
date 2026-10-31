using AIFarm.Contracts;

namespace AIFarm.Server.Game;

/// <summary>How well something copes with water.</summary>
public enum SwimAbility
{
	/// <summary>Never goes near it: keeps out of any water, even the shallows.</summary>
	Avoids,

	/// <summary>Wades the shallows but keeps out of water too deep to stand in.</summary>
	Wader,

	/// <summary>Swims at the surface (and wades), but doesn't go under.</summary>
	Swimmer,

	/// <summary>Swims, and dives below the surface while its air lasts.</summary>
	Diver,
}

/// <summary>
/// What an entity does in water.
/// </summary>
/// <param name="AirSeconds">How long it can stay under water before it starts to drown; 0 if it doesn't need air.</param>
/// <param name="SwimDepth">How deep the water must be before it swims instead of wading.</param>
/// <param name="SwimmingAnimation">Whether it has a swimming animation to show (few animals do yet).</param>
public sealed record SwimProfile(SwimAbility Ability, float AirSeconds, float SwimDepth = 0.7f, bool SwimmingAnimation = false)
{
	public bool CanSwim => Ability >= SwimAbility.Swimmer;

	public bool CanDive => Ability == SwimAbility.Diver;

	/// <summary>Whether it keeps out of water this deep (before it is in it already).</summary>
	public bool Refuses(float waterDepth) => Ability switch
	{
		SwimAbility.Avoids => waterDepth > 0f,
		SwimAbility.Wader => waterDepth >= WaterMap.DeepWater,
		_ => false,
	};
}

/// <summary>Who swims, who dives and for how long, by species.</summary>
public static class Swimming
{
	/// <summary>How long a player can stay under before they start to drown.</summary>
	public const float PlayerAirSeconds = 30f;

	public static readonly SwimProfile Player = new(SwimAbility.Diver, PlayerAirSeconds, WaterMap.SwimDepth);

	/// <summary>The farmer wades but, like the rest of the farm's people, doesn't go out of his depth.</summary>
	public static readonly SwimProfile Person = new(SwimAbility.Wader, 0f, WaterMap.SwimDepth);

	private static readonly SwimProfile Default = new(SwimAbility.Swimmer, 20f);

	/// <summary>Light, climbing or desert things that would never choose to get wet.</summary>
	private static readonly HashSet<string> Avoiders =
	[
		"Cat", "BlackCat", "Chicken", "Giraffe", "Monkey", "Gorilla", "Orangutan", "Sloth", "Koala", "Hamster",
		"Mouse", "Rat", "Chipmunk", "Hedgehog", "Rabbit", "Bat", "Camel", "BactrianCamel",
	];

	private static readonly Dictionary<string, SwimProfile> Divers = new()
	{
		["Hippo"] = new(SwimAbility.Diver, 90f),
		["Beaver"] = new(SwimAbility.Diver, 60f, 0.3f, SwimmingAnimation: true),
		["Otter"] = new(SwimAbility.Diver, 60f, 0.3f, SwimmingAnimation: true),
		["PolarBear"] = new(SwimAbility.Diver, 40f, 0.3f, SwimmingAnimation: true),
		["Dog"] = new(SwimAbility.Diver, 15f),
		["GuideDog"] = new(SwimAbility.Diver, 15f),
		["ServiceDog"] = new(SwimAbility.Diver, 15f),
		["Poodle"] = new(SwimAbility.Diver, 15f),
	};

	public static SwimProfile For(string model, EntityKind kind)
	{
		switch (kind)
		{
			case EntityKind.Player:
				return Player;
			case EntityKind.Npc:
				return Person;
		}
		var species = model.Split('_')[0];
		if (Divers.TryGetValue(species, out var diver))
		{
			return diver;
		}
		return Avoiders.Contains(species) ? new SwimProfile(SwimAbility.Avoids, 0f) : Default;
	}
}
