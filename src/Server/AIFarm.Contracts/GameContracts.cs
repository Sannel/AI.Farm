using System.Text.Json.Serialization;

namespace AIFarm.Contracts;

/// <summary>
/// The real-time game protocol between the Godot client and the server, over SignalR. The
/// server is authoritative: clients send input, the server simulates and broadcasts snapshots.
/// Positions are metres in the client's world space (Y up), yaw is radians around +Y.
/// </summary>
public static class GameHubProtocol
{
	/// <summary>Where the hub is mapped on the server.</summary>
	public const string Path = "/hubs/game";

	/// <summary>Client → server: join the world as a player. Returns a <see cref="JoinResult"/>.</summary>
	public const string Join = nameof(Join);

	/// <summary>Client → server: the player's current movement input (<see cref="PlayerInput"/>).</summary>
	public const string SendInput = nameof(SendInput);

	/// <summary>Server → client: the latest <see cref="WorldSnapshot"/>, sent every broadcast tick.</summary>
	public const string Snapshot = nameof(Snapshot);

	/// <summary>
	/// Client → server: the animals this player may spawn (<see cref="SpawnableAnimal"/>s; empty
	/// without <see cref="Permissions.SpawnAnimals"/>).
	/// </summary>
	public const string GetSpawnableAnimals = nameof(GetSpawnableAnimals);

	/// <summary>
	/// Client → server: spawn an animal by model a little way in front of the player. Needs
	/// <see cref="Permissions.SpawnAnimals"/>. Returns the new animal's <see cref="EntitySnapshot"/>.
	/// </summary>
	public const string SpawnAnimal = nameof(SpawnAnimal);

	/// <summary>
	/// Client → server: permanently remove an animal (and its memories) by id. Needs
	/// <see cref="Permissions.DestroyAnimals"/>; players and NPCs can't be destroyed.
	/// </summary>
	public const string DestroyEntity = nameof(DestroyEntity);

	/// <summary>
	/// Client → server: dip a bucket in the water the player is standing at (within
	/// <see cref="WaterMap.CollectReach"/> of it). Returns how many buckets they now carry.
	/// </summary>
	public const string CollectWater = nameof(CollectWater);

	/// <summary>Server → client: someone said something (a <see cref="SpeechEvent"/>), to show as a bubble.</summary>
	public const string Speech = nameof(Speech);

	/// <summary>
	/// Client → server: the player says something to an NPC (usually one of the replies the NPC's
	/// speech offered). The NPC answers on its next turn, which goes ahead of everything else.
	/// </summary>
	public const string SayToNpc = nameof(SayToNpc);

	/// <summary>
	/// Client → server stream: the voice for a <see cref="SpeechEvent"/> with audio, as
	/// <see cref="SpeechAudioChunk"/>s while it's being generated.
	/// </summary>
	public const string StreamSpeech = nameof(StreamSpeech);
}

/// <summary>Something said out loud in the world.</summary>
/// <param name="ToPlayerId">The player being spoken to, if anyone in particular.</param>
/// <param name="Replies">For the player spoken to: things they might say back (usually three).</param>
/// <param name="HasAudio">Whether there's a voice to stream (<see cref="GameHubProtocol.StreamSpeech"/>).</param>
public sealed record SpeechEvent(
	Guid SpeechId,
	Guid SpeakerId,
	string SpeakerName,
	string Text,
	Guid? ToPlayerId,
	IReadOnlyList<string> Replies,
	bool HasAudio);

/// <summary>A piece of a voice clip: mono 16-bit PCM samples at SampleRate.</summary>
public sealed record SpeechAudioChunk(int SampleRate, byte[] Samples);

/// <summary>Things a player may be allowed to do (granted on the server, per player).</summary>
public static class Permissions
{
	/// <summary>Spawn any animal from the server's catalog.</summary>
	public const string SpawnAnimals = "spawn_animals";

	/// <summary>Remove animals from the world for good.</summary>
	public const string DestroyAnimals = "destroy_animals";
}

/// <summary>One animal the server can spawn.</summary>
/// <param name="Model">The model (and what to ask for), e.g. "Cow_Male".</param>
/// <param name="Species">What it is, for grouping, e.g. "Cow".</param>
/// <param name="Name">What one is called, e.g. "Bull".</param>
/// <param name="Sex">"Female", "Male", or "" for models with no separate sexes yet.</param>
public sealed record SpawnableAnimal(string Model, string Species, string Name, string Sex);

/// <summary>What kind of thing an entity is; decides how the server simulates it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EntityKind>))]
public enum EntityKind
{
	Player,
	Animal,
	Prop,

	/// <summary>A non-player person (like the farmer), played by an AI agent through its persona.</summary>
	Npc,
}

/// <summary>How a living entity feels.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Mood>))]
public enum Mood
{
	Content,
	Happy,
	Playful,
	Tired,
	Sad,
	Scared,
	Angry,
}

/// <summary>
/// A living entity's (player's or animal's) vital signs.
/// </summary>
/// <param name="Energy">0-100 percent. Moving uses it up; resting brings it back.</param>
/// <param name="AgeDays">Age in game days (see the server's Game:DayLength).</param>
/// <param name="Air">0-100 percent of the breath left; drains while Submerged and refills at the surface.</param>
/// <param name="Submerged">Whether its head is under water.</param>
public sealed record EntityVitals(Mood Mood, float Energy, double AgeDays, float Air = 100f, bool Submerged = false);

/// <summary>One entity's state as the client should show it.</summary>
/// <param name="Model">The model to show, e.g. "Cow_Female" or "Farmer".</param>
/// <param name="Animation">The animation it is playing, e.g. "Idle" or "Walking".</param>
/// <param name="HasMemory">Whether it keeps memories (its AI can remember and recall things).</param>
/// <param name="Vitals">Mood, energy and age for living entities; null for props.</param>
/// <param name="Water">Buckets of water it is carrying (see <see cref="WaterMap.BucketCapacity"/>).</param>
public sealed record EntitySnapshot(
	Guid Id,
	EntityKind Kind,
	string Name,
	string Model,
	float X,
	float Y,
	float Z,
	float Yaw,
	string Animation,
	bool HasMemory,
	EntityVitals? Vitals,
	int Water = 0);

/// <summary>Every entity in the world at one simulation tick.</summary>
public sealed record WorldSnapshot(long Tick, IReadOnlyList<EntitySnapshot> Entities);

/// <summary>
/// A player's movement. The game client reports where its player is (X, Y, Z, after its own
/// collisions) and the server follows, no faster than the player can walk or run; without a
/// position, MoveX/MoveZ is a direction in world space (length up to 1; longer is clamped).
/// Yaw is where the player faces; Run asks to run (only possible with energy left).
/// </summary>
public sealed record PlayerInput(float MoveX, float MoveZ, float Yaw, bool Run, float? X = null, float? Y = null, float? Z = null);

/// <summary>
/// The reply to <see cref="GameHubProtocol.Join"/>: which entity is you, the world now, and
/// what you're allowed to do (see <see cref="Permissions"/>).
/// </summary>
public sealed record JoinResult(Guid PlayerId, WorldSnapshot Snapshot, IReadOnlyList<string> Permissions);
