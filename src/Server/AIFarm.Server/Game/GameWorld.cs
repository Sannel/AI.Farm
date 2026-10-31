using AIFarm.Contracts;
using AIFarm.Server.Data;

namespace AIFarm.Server.Game;

/// <summary>
/// The authoritative, in-memory game state and its rules. <see cref="GameLoop"/> steps it at a
/// fixed rate; hub calls feed player input in. Every entity here is also a row in the database
/// (<see cref="WorldEntity"/>): changed entities are handed back by <see cref="TakeChanged"/> to
/// be saved. Living entities (players and animals) have vitals (mood, energy and age) that
/// change as they live. Every public member is thread-safe.
/// </summary>
/// <param name="dayLength">How long a game day lasts in real time (ages advance by game days).</param>
public sealed class GameWorld(Random random, TimeSpan dayLength)
{
	public const float WalkSpeed = 6f; // matches the client's PlayerController.MoveSpeed
	public const float RunSpeed = 9f;
	public const float AnimalWalkSpeed = 0.9f;
	public const float AnimalWanderRadius = 4f;
	public const float NpcWalkSpeed = 2.5f; // an unhurried farmer's stroll

	/// <summary>How fast overlapping animals and NPCs are nudged apart, in metres per second.</summary>
	public const float SeparationSpeed = 1.2f;

	/// <summary>How long something that can't get past an obstacle keeps trying before it gives up and picks again.</summary>
	public const float GiveUpAfterBlocked = 1.2f;

	/// <summary>
	/// How much room each kind of body takes up on the ground (a circle's radius, in metres), by
	/// species, so animals and people keep clear of each other.
	/// </summary>
	public static float BodyRadius(string model, EntityKind kind)
	{
		if (kind is EntityKind.Player or EntityKind.Npc)
		{
			return 0.4f;
		}
		return model.Split('_')[0] switch
		{
			"Horse" => 0.75f,
			"Cow" => 0.7f,
			"Donkey" => 0.6f,
			"Pig" => 0.5f,
			"Sheep" => 0.45f,
			"Goat" => 0.4f,
			"Dog" => 0.35f,
			"Chicken" => 0.22f,
			_ => 0.5f,
		};
	}
	public const float NpcWanderRadius = 3f;
	public const string PlayerModel = "Farmer";

	public static readonly (float X, float Z) PlayerSpawn = (0f, 6f);

	/// <summary>New players face -Z, looking up the valley at the farmhouse.</summary>
	public const float PlayerSpawnYaw = MathF.PI;

	/// <summary>Energy rules, in percent per real minute.</summary>
	public static class Energy
	{
		public const float PlayerWalkCost = 1f;
		public const float PlayerRunCost = 4f;
		public const float PlayerRestGain = 2f;
		public const float AnimalWalkCost = 1.5f;
		public const float AnimalIdleGain = 0.3f;
		public const float AnimalRestGain = 4f;

		/// <summary>Below this a living entity is tired, and an animal lies down to rest.</summary>
		public const float Tired = 20f;

		/// <summary>A resting animal gets up again at this energy.</summary>
		public const float Rested = 90f;

		/// <summary>A tired entity cheers up to content at this energy.</summary>
		public const float Recovered = 60f;
	}

	public static readonly TimeSpan DefaultDayLength = TimeSpan.FromMinutes(20);

	/// <summary>How much faster than its speed a reported player position may move per tick before it's held back.</summary>
	public const float ReportedPositionSlack = 1.25f;

	private readonly Lock _lock = new();
	private readonly Dictionary<Guid, SimEntity> _entities = [];
	private readonly double _dayLengthSeconds = Math.Max(1, dayLength.TotalSeconds);
	private long _tick;
	private double _time; // simulated seconds since the world started

	public GameWorld(Random random) : this(random, DefaultDayLength)
	{
	}

	public long Tick
	{
		get
		{
			lock (_lock)
			{
				return _tick;
			}
		}
	}

	public static bool IsLiving(EntityKind kind) => kind is EntityKind.Player or EntityKind.Animal or EntityKind.Npc;

	/// <summary>Only animals can be removed from the world; people (players and NPCs) can't.</summary>
	public static bool IsDestroyable(EntityKind kind) => kind is EntityKind.Animal;

	/// <summary>Replaces the world's non-player entities (animals, props) with these saved ones.</summary>
	public void Load(IEnumerable<WorldEntity> saved)
	{
		lock (_lock)
		{
			foreach (var id in _entities.Where(e => e.Value.Kind != EntityKind.Player).Select(e => e.Key).ToList())
			{
				_entities.Remove(id);
			}
			foreach (var e in saved.Where(e => e.Kind != EntityKind.Player))
			{
				_entities[e.Id] = SimEntity.From(e, wait: (float)random.NextDouble() * 4f);
			}
		}
	}

	/// <summary>Adds a new non-player entity (e.g. a freshly spawned animal) to the world.</summary>
	public void AddEntity(WorldEntity entity)
	{
		if (entity.Kind == EntityKind.Player)
		{
			throw new ArgumentException("Players join with AddPlayer.", nameof(entity));
		}
		lock (_lock)
		{
			_entities[entity.Id] = SimEntity.From(entity, wait: 1f + (float)random.NextDouble() * 3f);
		}
	}

	/// <summary>Brings a player's saved entity into the world where they left off.</summary>
	/// <returns>False if that player is already in the world.</returns>
	public bool AddPlayer(WorldEntity player)
	{
		if (player.Kind != EntityKind.Player)
		{
			throw new ArgumentException("Not a player entity.", nameof(player));
		}
		lock (_lock)
		{
			return _entities.TryAdd(player.Id, SimEntity.From(player));
		}
	}

	/// <summary>Takes a player out of the world, returning their final state to save (null if absent).</summary>
	public WorldEntity? RemovePlayer(Guid id, DateTimeOffset now)
	{
		lock (_lock)
		{
			if (!_entities.TryGetValue(id, out var e) || e.Kind != EntityKind.Player)
			{
				return null;
			}
			_entities.Remove(id);
			return e.ToRecord(now);
		}
	}

	public bool SetInput(Guid playerId, PlayerInput input)
	{
		lock (_lock)
		{
			if (!_entities.TryGetValue(playerId, out var player) || player.Kind != EntityKind.Player)
			{
				return false;
			}
			player.Input = Sanitise(input);
			return true;
		}
	}

	/// <summary>Removes an animal from the world; false if it isn't here or can't be destroyed.</summary>
	public bool Remove(Guid id)
	{
		lock (_lock)
		{
			return _entities.TryGetValue(id, out var e) && IsDestroyable(e.Kind) && _entities.Remove(id);
		}
	}

	/// <summary>
	/// Sends an NPC walking to (x, z); once there, that spot becomes where it potters about.
	/// False if there's no such NPC in the world.
	/// </summary>
	public bool MoveTo(Guid npcId, float x, float z)
	{
		lock (_lock)
		{
			if (!_entities.TryGetValue(npcId, out var npc) || npc.Kind != EntityKind.Npc)
			{
				return false;
			}
			npc.TargetX = x;
			npc.TargetZ = z;
			npc.Directed = true;
			return true;
		}
	}

	/// <summary>
	/// Has an NPC give its attention to someone (usually a player it is talking with) for a while:
	/// once it stands still it turns to face them, follows them with its eyes if they move, and
	/// doesn't wander off meanwhile. False if either isn't in the world.
	/// </summary>
	public bool Engage(Guid npcId, Guid targetId, float seconds)
	{
		lock (_lock)
		{
			if (!_entities.TryGetValue(npcId, out var npc) || npc.Kind != EntityKind.Npc || !_entities.ContainsKey(targetId))
			{
				return false;
			}
			npc.FocusId = targetId;
			npc.FocusUntil = _time + seconds;
			return true;
		}
	}

	/// <summary>How far an animal can be from someone who is giving it water.</summary>
	public const float GiveWaterReach = 4f;

	/// <summary>How much energy a drink gives an animal, in percent.</summary>
	public const float DrinkEnergy = 15f;

	/// <summary>Dipping a bucket takes a moment: this long between dips.</summary>
	public static readonly TimeSpan DipTime = TimeSpan.FromMilliseconds(600);

	/// <summary>
	/// A player or NPC dips a bucket in the water they're standing at. Returns how many buckets they
	/// then carry, or why they couldn't (not by water, bucket full).
	/// </summary>
	public (bool Ok, string Message, int Water) CollectWater(Guid id, DateTimeOffset now)
	{
		lock (_lock)
		{
			if (!_entities.TryGetValue(id, out var e) || e.Kind is not (EntityKind.Player or EntityKind.Npc))
			{
				return (false, "You aren't in the world.", 0);
			}
			var distance = WaterMap.DistanceToWater(e.X, e.Z);
			if (distance > WaterMap.CollectReach)
			{
				return (false, FormattableString.Invariant($"The nearest water is {distance:0} m away."), e.Water);
			}
			if (e.Water >= WaterMap.BucketCapacity)
			{
				return (false, "Your bucket is already full.", e.Water);
			}
			if (now - e.LastDip < DipTime)
			{
				return (false, "Wait a moment.", e.Water);
			}
			e.LastDip = now;
			e.Water++;
			e.Changed = true;
			return (true, FormattableString.Invariant($"Collected water ({e.Water} of {WaterMap.BucketCapacity})."), e.Water);
		}
	}

	/// <summary>
	/// A person gives a bucket of water to an animal beside them: it drinks, which gives it some
	/// energy back.
	/// </summary>
	public (bool Ok, string Message) GiveWater(Guid giverId, Guid animalId)
	{
		lock (_lock)
		{
			if (!_entities.TryGetValue(giverId, out var giver) || giver.Kind is not (EntityKind.Player or EntityKind.Npc))
			{
				return (false, "You aren't in the world.");
			}
			if (!_entities.TryGetValue(animalId, out var animal) || animal.Kind != EntityKind.Animal)
			{
				return (false, "That isn't an animal on the farm.");
			}
			if (giver.Water <= 0)
			{
				return (false, "You have no water: fill your bucket at the river or lake first.");
			}
			var distance = Remaining(giver, animal.X, animal.Z);
			if (distance > GiveWaterReach)
			{
				return (false, FormattableString.Invariant($"{animal.Name} is {distance:0} m away; walk over first."));
			}
			giver.Water--;
			giver.Changed = true;
			ChangeEnergyBy(animal.Vitals!, DrinkEnergy);
			animal.Changed = true;
			return (true, $"{animal.Name} drank the water and perked up.");
		}
	}

	/// <summary>Sets a living entity's mood; false if it isn't in the world or isn't alive.</summary>
	public bool SetMood(Guid id, Mood mood)
	{
		lock (_lock)
		{
			if (!_entities.TryGetValue(id, out var e) || e.Vitals is null)
			{
				return false;
			}
			e.Vitals.Mood = mood;
			e.Changed = true;
			return true;
		}
	}

	/// <summary>Advances the simulation by dt seconds.</summary>
	public void Step(float dt)
	{
		lock (_lock)
		{
			_tick++;
			_time += dt;
			foreach (var entity in _entities.Values)
			{
				switch (entity.Kind)
				{
					case EntityKind.Player:
						StepPlayer(entity, dt);
						break;
					case EntityKind.Animal:
						StepAnimal(entity, dt);
						StepBody(entity, dt);
						if (entity.Swim.SwimmingAnimation && WaterMap.WaterDepth(entity.X, entity.Z) > entity.Swim.SwimDepth)
						{
							entity.Animation = "Swimming";
						}
						break;
					case EntityKind.Npc:
						StepNpc(entity, dt);
						StepBody(entity, dt);
						break;
				}
				if (entity.Vitals is { } living)
				{
					StepBreath(entity, living, dt);
				}
				if (entity.Vitals is { } vitals)
				{
					vitals.AgeDays += dt / _dayLengthSeconds;
					UpdateMood(vitals);
					entity.Changed = true;
				}
			}
			Separate(dt);
		}
	}

	public WorldSnapshot Snapshot()
	{
		lock (_lock)
		{
			return new WorldSnapshot(_tick, [.. _entities.Values.Select(e => e.ToSnapshot())]);
		}
	}

	public EntitySnapshot? Find(Guid id)
	{
		lock (_lock)
		{
			return _entities.TryGetValue(id, out var e) ? e.ToSnapshot() : null;
		}
	}

	/// <summary>Entities within radius metres of (x, z) on the ground plane, nearest first.</summary>
	public IReadOnlyList<EntitySnapshot> Near(float x, float z, float radius)
	{
		lock (_lock)
		{
			return [.. _entities.Values
				.Select(e => (Entity: e, Distance: MathF.Sqrt((e.X - x) * (e.X - x) + (e.Z - z) * (e.Z - z))))
				.Where(e => e.Distance <= radius)
				.OrderBy(e => e.Distance)
				.Select(e => e.Entity.ToSnapshot())];
		}
	}

	/// <summary>Every entity that moved, turned or lived a little since the last call, as rows to save.</summary>
	public IReadOnlyList<WorldEntity> TakeChanged(DateTimeOffset now)
	{
		lock (_lock)
		{
			var changed = _entities.Values.Where(e => e.Changed).ToList();
			foreach (var e in changed)
			{
				e.Changed = false;
			}
			return [.. changed.Select(e => e.ToRecord(now))];
		}
	}

	/// <summary>
	/// Moves a player towards where its client says it is (or along its input direction), never
	/// faster than it may go: running needs energy. Moving costs energy; standing still restores it.
	/// </summary>
	private static void StepPlayer(SimEntity player, float dt)
	{
		var input = player.Input;
		var vitals = player.Vitals!;
		if (player.Yaw != input.Yaw)
		{
			player.Yaw = input.Yaw;
			player.Changed = true;
		}
		var maxSpeed = input.Run && vitals.Energy > 0f ? RunSpeed : WalkSpeed; // too worn out to run
		float dx, dz;
		if (input.X is { } x && input.Z is { } z)
		{
			dx = x - player.X;
			dz = z - player.Z;
			// A little slack for packets arriving unevenly; anything more is held back.
			var allowed = maxSpeed * dt * ReportedPositionSlack + 0.02f;
			var distance = MathF.Sqrt(dx * dx + dz * dz);
			if (distance > allowed)
			{
				dx *= allowed / distance;
				dz *= allowed / distance;
			}
			if (input.Y is { } y)
			{
				player.Y = Math.Clamp(y, -10f, 100f);
			}
		}
		else
		{
			dx = input.MoveX * maxSpeed * dt;
			dz = input.MoveZ * maxSpeed * dt;
		}

		var speed = MathF.Sqrt(dx * dx + dz * dz) / dt;
		if (speed < 0.1f)
		{
			player.Animation = "Idle_A";
			ChangeEnergy(vitals, Energy.PlayerRestGain, dt);
			return;
		}
		player.X += dx;
		player.Z += dz;
		player.Changed = true;
		// Catching up after packets bunched up can briefly beat walking pace; that's still walking.
		var running = input.Run && speed > WalkSpeed * 1.05f;
		player.Animation = running ? "Running_A" : "Walking_A";
		var cost = running ? Energy.PlayerRunCost : Energy.PlayerWalkCost * Math.Min(speed / WalkSpeed, 1f);
		ChangeEnergy(vitals, -cost * (WaterMap.WaterDepth(player.X, player.Z) >= WaterMap.SwimDepth ? SwimEnergyFactor : 1f), dt);
	}

	/// <summary>
	/// Animals wander round their home spot: stand a few seconds, walk somewhere nearby, repeat,
	/// going around anything in the way. Walking tires them; once tired they lie down and rest
	/// until they're full of energy again.
	/// </summary>
	private void StepAnimal(SimEntity animal, float dt)
	{
		var vitals = animal.Vitals!;
		if (animal.Resting)
		{
			animal.Animation = "Lie_Idle";
			ChangeEnergy(vitals, Energy.AnimalRestGain, dt);
			if (vitals.Energy >= Energy.Rested)
			{
				animal.Resting = false;
				animal.Wait = 2f + (float)random.NextDouble() * 3f;
				animal.Animation = "Idle";
			}
			return;
		}

		if (animal.TargetX is not { } targetX || animal.TargetZ is not { } targetZ)
		{
			animal.Animation = "Idle";
			ChangeEnergy(vitals, Energy.AnimalIdleGain, dt);
			if (vitals.Energy < Energy.Tired)
			{
				animal.Resting = true;
				animal.Animation = "Lie_Idle";
				return;
			}
			animal.Wait -= dt;
			if (animal.Wait <= 0f)
			{
				// Somewhere nearby it's willing to stand: never in water it keeps out of.
				for (var attempt = 0; attempt < 8; attempt++)
				{
					var angle = random.NextDouble() * Math.Tau;
					var distance = AnimalWanderRadius * Math.Sqrt(random.NextDouble());
					var x = animal.HomeX + (float)(Math.Cos(angle) * distance);
					var z = animal.HomeZ + (float)(Math.Sin(angle) * distance);
					if (animal.Swim.Refuses(WaterMap.WaterDepth(x, z)))
					{
						continue;
					}
					animal.TargetX = x;
					animal.TargetZ = z;
					break;
				}
				if (animal.TargetX is null)
				{
					animal.TargetX = animal.HomeX; // nowhere dry nearby: stay home
					animal.TargetZ = animal.HomeZ;
				}
			}
			return;
		}

		if (!Walk(animal, targetX, targetZ, AnimalWalkSpeed, dt))
		{
			animal.Animation = "Idle";
			if ((animal.BlockedFor += dt) >= GiveUpAfterBlocked)
			{
				// Boxed in: stop trying, wait a moment and pick somewhere else.
				animal.TargetX = animal.TargetZ = null;
				animal.BlockedFor = 0f;
				animal.Wait = 1f + (float)random.NextDouble() * 2f;
			}
			return;
		}
		animal.BlockedFor = 0f;
		animal.Changed = true;
		ChangeEnergy(vitals, -Energy.AnimalWalkCost, dt);
		if (Remaining(animal, targetX, targetZ) < 0.05f)
		{
			Settle(animal, targetX, targetZ);
			animal.TargetX = animal.TargetZ = null;
			animal.Wait = 3f + (float)random.NextDouble() * 5f;
			animal.Animation = "Idle";
			return;
		}
		animal.Animation = animal.Model.StartsWith("Bat_", StringComparison.Ordinal) ? "Flying" : "Walking";
	}

	/// <summary>
	/// NPCs walk where their agent sends them; left to themselves they potter about near home
	/// every so often. Walking tires them like players; standing still rests them. Giving someone
	/// their attention (see <see cref="Engage"/>), they turn to face them and stay put.
	/// </summary>
	private void StepNpc(SimEntity npc, float dt)
	{
		var vitals = npc.Vitals!;
		if (npc.TargetX is not { } targetX || npc.TargetZ is not { } targetZ)
		{
			npc.Animation = "Idle_A";
			ChangeEnergy(vitals, Energy.PlayerRestGain, dt);
			if (FocusOf(npc) is { } focus)
			{
				var yaw = MathF.Atan2(focus.X - npc.X, focus.Z - npc.Z); // models face +Z at yaw 0
				if (MathF.Abs(yaw - npc.Yaw) > 0.005f)
				{
					npc.Yaw = yaw;
					npc.Changed = true;
				}
				npc.Wait = MathF.Max(npc.Wait, 5f); // don't wander off mid-conversation
				return;
			}
			npc.Wait -= dt;
			if (npc.Wait <= 0f && vitals.Energy >= Energy.Tired)
			{
				var angle = random.NextDouble() * Math.Tau;
				var distance = NpcWanderRadius * Math.Sqrt(random.NextDouble());
				npc.TargetX = npc.HomeX + (float)(Math.Cos(angle) * distance);
				npc.TargetZ = npc.HomeZ + (float)(Math.Sin(angle) * distance);
			}
			return;
		}

		if (!Walk(npc, targetX, targetZ, NpcWalkSpeed, dt))
		{
			npc.Animation = "Idle_A";
			if ((npc.BlockedFor += dt) >= GiveUpAfterBlocked)
			{
				npc.TargetX = npc.TargetZ = null;
				npc.Directed = false;
				npc.BlockedFor = 0f;
				npc.Wait = 1f + (float)random.NextDouble() * 2f;
			}
			return;
		}
		npc.BlockedFor = 0f;
		npc.Changed = true;
		ChangeEnergy(vitals, -Energy.PlayerWalkCost, dt);
		if (Remaining(npc, targetX, targetZ) < 0.05f)
		{
			Settle(npc, targetX, targetZ);
			npc.TargetX = npc.TargetZ = null;
			if (npc.Directed)
			{
				// Sent here on purpose: this is where it stays now.
				npc.HomeX = npc.X;
				npc.HomeZ = npc.Z;
				npc.Directed = false;
			}
			npc.Wait = 10f + (float)random.NextDouble() * 15f;
			npc.Animation = "Idle_A";
			return;
		}
		npc.Animation = "Walking_A";
	}

	private SimEntity? FocusOf(SimEntity npc) =>
		npc.FocusId is { } id && _time < npc.FocusUntil && _entities.TryGetValue(id, out var focus) ? focus : null;

	/// <summary>Arriving: lands exactly on the spot, unless someone is standing on it.</summary>
	private void Settle(SimEntity e, float x, float z)
	{
		if (!Blocked(e, x, z))
		{
			e.X = x;
			e.Z = z;
		}
	}

	private static float Remaining(SimEntity e, float x, float z) => MathF.Sqrt((x - e.X) * (x - e.X) + (z - e.Z) * (z - e.Z));

	/// <summary>
	/// Takes one step towards a spot at the given speed, going around anything in the way. False
	/// when every way forward is blocked, so it stays where it is.
	/// </summary>
	private bool Walk(SimEntity e, float targetX, float targetZ, float speed, float dt)
	{
		var dx = targetX - e.X;
		var dz = targetZ - e.Z;
		var remaining = MathF.Sqrt(dx * dx + dz * dz);
		if (remaining < 1e-4f)
		{
			return true;
		}
		// Wading is slower, for animals as for players.
		var step = MathF.Min(speed * (WaterMap.WaterDepth(e.X, e.Z) > 0.15f ? 0.7f : 1f) * dt, remaining);
		return TryMove(e, dx / remaining * step, dz / remaining * step);
	}

	/// <summary>Turns, in radians, tried in turn when something is in the way: straight on, then either side.</summary>
	private static readonly float[] AvoidanceTurns = [0f, 0.6f, -0.6f, 1.2f, -1.2f, 1.8f, -1.8f];

	/// <summary>
	/// Moves an entity by (dx, dz), or if something is in the way, by the same distance at the
	/// smallest turn that is clear, facing the way it actually went. False if it can't move at all.
	/// </summary>
	private bool TryMove(SimEntity e, float dx, float dz)
	{
		foreach (var turn in AvoidanceTurns)
		{
			var (sin, cos) = MathF.SinCos(turn);
			var mx = dx * cos - dz * sin;
			var mz = dx * sin + dz * cos;
			if (!Blocked(e, e.X + mx, e.Z + mz))
			{
				e.X += mx;
				e.Z += mz;
				e.Yaw = MathF.Atan2(mx, mz);
				return true;
			}
		}
		return false;
	}

	/// <summary>How far an animal's body floats below the water's surface when it swims (its back stays above).</summary>
	public const float FloatDrop = 0.3f;

	/// <summary>Where animals and the farmer stand: on the ground, afloat on the water, or diving under it.</summary>
	private void StepBody(SimEntity e, float dt)
	{
		var ground = WaterMap.Height(e.X, e.Z);
		var waterDepth = WaterMap.WaterDepth(e.X, e.Z);
		var swimming = e.Swim.CanSwim && waterDepth > e.Swim.SwimDepth;
		if (!swimming)
		{
			e.Depth = e.DiveGoal = 0f;
			e.Submerged = false;
			SetBodyY(e, ground);
			return;
		}

		// Divers sometimes duck under for a while, while their breath holds out.
		var deepestDive = MathF.Max(0f, waterDepth - 0.7f);
		if (e.Swim.CanDive)
		{
			e.DiveCooldown -= dt;
			var breath = e.Air / e.Swim.AirSeconds;
			if (e.DiveGoal > 0f && (breath < 0.35f || e.DiveCooldown <= 0f || e.DiveGoal > deepestDive))
			{
				e.DiveGoal = 0f; // come up for air
				e.DiveCooldown = 6f + (float)random.NextDouble() * 10f;
			}
			else if (e.DiveGoal <= 0f && e.Depth < 0.05f && e.DiveCooldown <= 0f && breath > 0.8f && deepestDive > 0.4f)
			{
				e.DiveGoal = Math.Min(deepestDive, 0.8f);
				e.DiveCooldown = 4f + (float)random.NextDouble() * 6f;
			}
		}
		else
		{
			e.DiveGoal = 0f;
		}
		const float DiveSpeed = 0.8f;
		e.Depth = e.Depth < e.DiveGoal ? MathF.Min(e.DiveGoal, e.Depth + DiveSpeed * dt) : MathF.Max(e.DiveGoal, e.Depth - DiveSpeed * dt);
		// A swimmer's back is at the surface; diving takes it below.
		e.Submerged = e.Depth > FloatDrop + 0.1f;
		SetBodyY(e, MathF.Max(ground, WaterMap.SurfaceY - FloatDrop - e.Depth));
	}

	private static void SetBodyY(SimEntity e, float y)
	{
		if (MathF.Abs(e.Y - y) > 1e-4f)
		{
			e.Y = y;
			e.Changed = true;
		}
	}

	/// <summary>Breath: it runs down while the head is under water and comes back at the surface; with none left, they drown.</summary>
	private void StepBreath(SimEntity e, VitalsState vitals, float dt)
	{
		if (e.Kind == EntityKind.Player)
		{
			var under = WaterMap.EyesUnderwater(e.X, e.Y, e.Z);
			if (under != e.Submerged)
			{
				e.Submerged = under;
				e.Changed = true;
			}
		}
		if (e.Swim.AirSeconds <= 0f)
		{
			return;
		}
		var before = e.Air;
		e.Air = e.Submerged ? MathF.Max(0f, e.Air - dt) : MathF.Min(e.Swim.AirSeconds, e.Air + dt * AirRecoveryRate);
		if (e.Air <= 0f)
		{
			// Out of breath: it wears them out fast, and an animal heads straight up.
			vitals.Energy = Math.Max(0f, vitals.Energy - DrowningEnergyLoss * dt);
			e.DiveGoal = 0f;
		}
		if (MathF.Abs(e.Air - before) > 1e-4f)
		{
			e.Changed = true;
		}
	}

	/// <summary>Breath comes back this many times faster than it goes.</summary>
	public const float AirRecoveryRate = 8f;

	/// <summary>Swimming wears a person out this many times faster than walking.</summary>
	public const float SwimEnergyFactor = 2.5f;

	/// <summary>Energy lost per second with no breath left, in percent.</summary>
	public const float DrowningEnergyLoss = 10f;

	/// <summary>
	/// Whether standing at (x, z) would put the entity into someone else's space further than it
	/// already is (moving apart from something it overlaps is always allowed).
	/// </summary>
	private bool Blocked(SimEntity e, float x, float z)
	{
		// Each keeps out of the water it won't go into (unless it is already in it).
		if (e.Kind != EntityKind.Player
			&& e.Swim.Refuses(WaterMap.WaterDepth(x, z)) && !e.Swim.Refuses(WaterMap.WaterDepth(e.X, e.Z)))
		{
			return true;
		}
		foreach (var other in _entities.Values)
		{
			if (other == e)
			{
				continue;
			}
			var gap = e.Radius + other.Radius;
			var after = (other.X - x) * (other.X - x) + (other.Z - z) * (other.Z - z);
			if (after < gap * gap && after < (other.X - e.X) * (other.X - e.X) + (other.Z - e.Z) * (other.Z - e.Z))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// Gently pushes apart animals and NPCs that overlap (spawned on top of each other, walked
	/// into by a player). Players are never moved by the server, so whoever overlaps one gives way.
	/// </summary>
	private void Separate(float dt)
	{
		var all = _entities.Values.ToArray();
		var maxPush = SeparationSpeed * dt;
		for (var i = 0; i < all.Length; i++)
		{
			for (var j = i + 1; j < all.Length; j++)
			{
				var (a, b) = (all[i], all[j]);
				if (a.Kind == EntityKind.Player && b.Kind == EntityKind.Player)
				{
					continue;
				}
				var dx = b.X - a.X;
				var dz = b.Z - a.Z;
				var distance = MathF.Sqrt(dx * dx + dz * dz);
				var overlap = a.Radius + b.Radius - distance;
				if (overlap <= 0f)
				{
					continue;
				}
				if (distance < 1e-3f)
				{
					// Exactly on top of each other: pick a direction that differs per pair.
					(dx, dz, distance) = (MathF.Cos(i * 2.4f + j), MathF.Sin(i * 2.4f + j), 1f);
				}
				var push = MathF.Min(overlap, maxPush);
				var (ux, uz) = (dx / distance, dz / distance); // from a towards b
				var aMoves = a.Kind != EntityKind.Player;
				var bMoves = b.Kind != EntityKind.Player;
				var share = aMoves && bMoves ? 0.5f : 1f;
				if (aMoves)
				{
					a.X -= ux * push * share;
					a.Z -= uz * push * share;
					a.Changed = true;
				}
				if (bMoves)
				{
					b.X += ux * push * share;
					b.Z += uz * push * share;
					b.Changed = true;
				}
			}
		}
	}

	private static void ChangeEnergyBy(VitalsState vitals, float percent) =>
		vitals.Energy = Math.Clamp(vitals.Energy + percent, 0f, 100f);

	private static void ChangeEnergy(VitalsState vitals, float percentPerMinute, float dt) =>
		vitals.Energy = Math.Clamp(vitals.Energy + percentPerMinute * dt / 60f, 0f, 100f);

	/// <summary>Low energy makes anyone tired; once recovered they're content again.</summary>
	private static void UpdateMood(VitalsState vitals)
	{
		if (vitals.Energy < Energy.Tired)
		{
			vitals.Mood = Mood.Tired;
		}
		else if (vitals.Mood == Mood.Tired && vitals.Energy >= Energy.Recovered)
		{
			vitals.Mood = Mood.Content;
		}
	}

	private static PlayerInput Sanitise(PlayerInput input)
	{
		var x = float.IsFinite(input.MoveX) ? input.MoveX : 0f;
		var z = float.IsFinite(input.MoveZ) ? input.MoveZ : 0f;
		var length = MathF.Sqrt(x * x + z * z);
		if (length > 1f)
		{
			x /= length;
			z /= length;
		}
		static float? Finite(float? value) => value is { } v && float.IsFinite(v) ? v : null;
		return new PlayerInput(x, z, float.IsFinite(input.Yaw) ? input.Yaw : 0f, input.Run,
			Finite(input.X), Finite(input.Y), Finite(input.Z));
	}

	private sealed class VitalsState
	{
		public Mood Mood { get; set; }
		public float Energy { get; set; }
		public double AgeDays { get; set; }
	}

	private sealed class SimEntity(Guid id, EntityKind kind, string name, string model, bool hasMemory)
	{
		public Guid Id { get; } = id;
		public EntityKind Kind { get; } = kind;
		public string Name { get; } = name;
		public string Model { get; } = model;
		public bool HasMemory { get; } = hasMemory;
		public VitalsState? Vitals { get; init; }
		public string[] Permissions { get; init; } = [];
		public float X { get; set; }
		public float Y { get; set; }
		public float Z { get; set; }
		public float Yaw { get; set; }
		public string Animation { get; set; } = "Idle";
		public PlayerInput Input { get; set; } = new(0, 0, 0, false);
		public float HomeX { get; set; }
		public float HomeZ { get; set; }
		public float? TargetX { get; set; }
		public float? TargetZ { get; set; }
		public float Wait { get; set; }
		public bool Resting { get; set; }
		public bool Directed { get; set; }
		public int Water { get; set; }
		public DateTimeOffset LastDip { get; set; }
		public float BlockedFor { get; set; }
		public Guid? FocusId { get; set; }
		public double FocusUntil { get; set; }
		public float Radius { get; init; }
		public SwimProfile Swim { get; init; } = Swimming.For("", EntityKind.Prop);

		/// <summary>Seconds of breath left; goes down under water, refills at the surface.</summary>
		public float Air { get; set; }
		public bool Submerged { get; set; }

		/// <summary>How far below its floating level a diving animal is, and how far it is heading.</summary>
		public float Depth { get; set; }
		public float DiveGoal { get; set; }
		public float DiveCooldown { get; set; }
		public string? Persona { get; init; }
		public bool Changed { get; set; }

		public static SimEntity From(WorldEntity e, float wait = 0f) =>
			new(e.Id, e.Kind, e.Name, e.Model, e.HasMemory)
			{
				X = e.X, Y = e.Y, Z = e.Z, Yaw = e.Yaw, HomeX = e.HomeX, HomeZ = e.HomeZ, Wait = wait,
				Input = new PlayerInput(0, 0, e.Yaw, false),
				Animation = e.Kind is EntityKind.Player or EntityKind.Npc ? "Idle_A" : "Idle", // people use the farmer rig
				Permissions = e.Permissions, // carried through so saving the row keeps them
				Persona = e.Persona,
				Radius = BodyRadius(e.Model, e.Kind),
				Swim = Swimming.For(e.Model, e.Kind),
				Air = Swimming.For(e.Model, e.Kind).AirSeconds,
				Water = e.Water,
				// Living things always have vitals, filled in with defaults if the row has none.
				Vitals = IsLiving(e.Kind)
					? new VitalsState { Mood = e.Mood ?? Mood.Content, Energy = e.Energy ?? 100f, AgeDays = e.AgeDays ?? 0 }
					: null,
			};

		public WorldEntity ToRecord(DateTimeOffset now) => new()
		{
			Id = Id, Kind = Kind, Name = Name, Model = Model, HasMemory = HasMemory, Permissions = Permissions, Persona = Persona,
			Water = Water,
			X = X, Y = Y, Z = Z, Yaw = Yaw, HomeX = HomeX, HomeZ = HomeZ,
			Mood = Vitals?.Mood, Energy = Vitals?.Energy, AgeDays = Vitals?.AgeDays,
			UpdatedAt = now,
		};

		public EntitySnapshot ToSnapshot() => new(Id, Kind, Name, Model, X, Y, Z, Yaw, Animation, HasMemory,
			Vitals is null ? null : new EntityVitals(Vitals.Mood, Vitals.Energy, Vitals.AgeDays, AirPercent, Submerged), Water);

		/// <summary>How much of its breath it has left, in percent (full for something that doesn't need air).</summary>
		public float AirPercent => Swim.AirSeconds > 0f ? Math.Clamp(Air / Swim.AirSeconds * 100f, 0f, 100f) : 100f;
	}
}
