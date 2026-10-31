using System.ComponentModel;
using AIFarm.Contracts;
using AIFarm.Server.Data;
using AIFarm.Server.Memory;
using AIFarm.Server.Speech;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace AIFarm.Server.Game;

/// <summary>Where the NPCs' language model runs.</summary>
public enum BrainProvider
{
	/// <summary>Any OpenAI-compatible chat endpoint: Ollama (the default), OpenAI, vLLM, LM Studio...</summary>
	OpenAiCompatible,

	/// <summary>Azure AI Foundry (or Azure OpenAI): models you've deployed there.</summary>
	AzureFoundry,
}

/// <summary>Which of the model API's two request styles to use.</summary>
public enum BrainApi
{
	/// <summary>The classic /chat/completions API, which every provider speaks (Ollama too).</summary>
	ChatCompletions,

	/// <summary>The newer /responses API (OpenAI and Azure AI Foundry; some models only work with it).</summary>
	Responses,
}

/// <summary>
/// The "Brain" configuration section: the language model that plays the NPCs. It needs tool
/// calling. By default that's Ollama through its OpenAI-compatible API, as in compose.yaml; or
/// set <see cref="Provider"/> to <see cref="BrainProvider.AzureFoundry"/> to use a model deployed
/// in Azure AI Foundry.
/// </summary>
public sealed class BrainOptions
{
	public const string Section = "Brain";

	public bool Enabled { get; set; } = true;

	public BrainProvider Provider { get; set; } = BrainProvider.OpenAiCompatible;

	/// <summary>
	/// OpenAI-compatible: the API's base URL (Ollama: http://host:11434/v1). Azure Foundry: your
	/// resource's endpoint (https://NAME.openai.azure.com or https://NAME.services.ai.azure.com);
	/// "/openai/v1/" is added if it isn't there. A full request URL pasted from a portal works too:
	/// ending in "/responses" selects the Responses API (see <see cref="Api"/>), and the ending is
	/// dropped from the base URL.
	/// </summary>
	public Uri Endpoint { get; set; } = new("http://localhost:11434/v1");

	/// <summary>
	/// Which request style to use. Left empty it follows the endpoint: one ending in "/responses"
	/// uses the Responses API, anything else chat completions.
	/// </summary>
	public BrainApi? Api { get; set; }

	/// <summary>
	/// The model. For Ollama, its name (it must support tool calling, e.g. gemma4:e4b, qwen3:4b);
	/// for Azure Foundry, the name of your deployment.
	/// </summary>
	public string Model { get; set; } = "gemma4:e4b";

	/// <summary>
	/// The API key. Ollama ignores it. For Azure Foundry, leave it empty to sign in with Entra ID
	/// instead (az login, a managed identity, or the AZURE_CLIENT_ID/TENANT_ID/SECRET variables).
	/// </summary>
	public string ApiKey { get; set; } = "ollama";

	/// <summary>
	/// How creative the NPC is (0-2). Leave it empty to use the model's own default, which
	/// reasoning models (gpt-5, o-series) require: they refuse any other value.
	/// </summary>
	public float? Temperature { get; set; } = 0.6f;

	/// <summary>How often each NPC takes a turn on its own.</summary>
	public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

	/// <summary>A player walking up gets a turn sooner, but never closer together than this.</summary>
	public TimeSpan MinimumGap { get; set; } = TimeSpan.FromSeconds(15);

	/// <summary>Most tool calls the NPC may make in one turn.</summary>
	public int MaxSteps { get; set; } = 6;

	/// <summary>A turn taking longer than this (a slow model on a CPU) is abandoned.</summary>
	public TimeSpan TurnTimeout { get; set; } = TimeSpan.FromMinutes(3);

	/// <summary>
	/// Characters an NPC's main memory file may hold. What it knows about each player gets a
	/// third of this.
	/// </summary>
	public int MemoryFileSize { get; set; } = 4000;

	/// <summary>Players within this many metres have their memory files in the NPC's briefing.</summary>
	public float PlayerMemoryRange { get; set; } = 15f;

	/// <summary>Wait this long after startup before the first turns (lets the model finish downloading).</summary>
	public TimeSpan StartDelay { get; set; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Gives every NPC with a persona (Claude the farmer, for a start) a mind: every so often, or
/// when a player comes up to them, the language model gets the NPC's persona, a briefing on how
/// things stand, its memory files (its own, and what it knows about each player nearby), and
/// tools bound to that NPC (look around, walk, comfort an animal it's with, rewrite its memory).
/// It acts, then sums up what it did, which is logged.
/// </summary>
public sealed class NpcBrain(
	GameWorld world,
	IChatClient chat,
	IServiceScopeFactory scopes,
	SpeechService speech,
	IOptions<BrainOptions> options,
	IOptions<NpcsOptions> npcs,
	TimeProvider time,
	ILogger<NpcBrain> logger) : BackgroundService
{
	/// <summary>A player this close to an NPC has "come over" and gets the NPC's attention.</summary>
	public const float NoticeDistance = 8f;

	/// <summary>An NPC can only comfort an animal it's standing with.</summary>
	public const float ComfortDistance = 4f;

	/// <summary>How far an NPC's voice carries to the person it's talking to.</summary>
	public const float TalkDistance = 12f;

	/// <summary>How long an NPC keeps its attention on someone it spoke to (or that spoke to it).</summary>
	public const float EngageSeconds = 30f;

	/// <summary>How many things the player is offered to say back.</summary>
	public const int ReplyCount = 5;

	/// <summary>
	/// Plain replies to fill in any the model doesn't come up with, in the same order as the roles
	/// it's asked for: friendly, curious, practical, negative, and "talk to you later".
	/// </summary>
	public static readonly string[] FallbackReplies =
	[
		"Good to see you!", "How are the animals doing?", "Can I give you a hand?", "I don't have time for this.", "I'll talk to you later.",
	];

	/// <summary>Kokoro voice used when an NPC has none configured.</summary>
	public const string DefaultVoice = "am_michael";

	private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(1);

	/// <summary>Why a turn is due; lower comes first (Ollama answers one request at a time).</summary>
	public enum Urgency
	{
		SpokenTo = 0,
		PlayerArrived = 1,
		NearPlayers = 2,
		Routine = 3,
	}

	private readonly Dictionary<Guid, DateTimeOffset> _lastTurn = [];
	private readonly Dictionary<Guid, HashSet<Guid>> _playersNearby = [];
	private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, List<string>> _spokenTo = new();
	private readonly SemaphoreSlim _wake = new(0);

	/// <summary>
	/// Someone said something to an NPC: it answers on its next turn, which goes ahead of
	/// every other turn.
	/// </summary>
	public void HeardFrom(Guid npcId, string speaker, string text)
	{
		var said = _spokenTo.GetOrAdd(npcId, _ => []);
		lock (said)
		{
			said.Add($"{speaker} said to you: \"{text}\"");
		}
		_wake.Release();
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var settings = options.Value;
		if (!settings.Enabled)
		{
			logger.LogInformation("NPC brain is off (Brain:Enabled is false)");
			return;
		}
		logger.LogInformation("NPC brain using {Model} at {Endpoint}", settings.Model, settings.Endpoint);
		try
		{
			await Task.Delay(settings.StartDelay, time, stoppingToken);
			while (!stoppingToken.IsCancellationRequested)
			{
				if (NextTurn(settings) is { } turn)
				{
					await TakeTurnAsync(turn.NpcId, turn.Reason, stoppingToken);
					continue; // straight on to whatever is most urgent now
				}
				await _wake.WaitAsync(CheckEvery, stoppingToken); // something said, or a second passes
			}
		}
		catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
		{
		}
	}

	/// <summary>
	/// The most urgent NPC turn due, if any: being spoken to, then a player coming over, then
	/// routine turns of NPCs near players (who think twice as often), then everyone else's;
	/// between equals, the NPC nearest a player first.
	/// </summary>
	internal (Guid NpcId, string Reason)? NextTurn(BrainOptions settings)
	{
		var due = new List<(Urgency Urgency, float Distance, Guid NpcId, string Reason)>();
		foreach (var npc in world.Snapshot().Entities.Where(e => e.Kind == EntityKind.Npc))
		{
			var players = world.Near(npc.X, npc.Z, settings.PlayerMemoryRange).Where(e => e.Kind == EntityKind.Player).ToList();
			var nearest = players.Count == 0 ? float.MaxValue : players.Min(p => Distance(p, npc));
			if (_spokenTo.TryRemove(npc.Id, out var said))
			{
				string[] lines;
				lock (said)
				{
					lines = [.. said];
				}
				due.Add((Urgency.SpokenTo, nearest, npc.Id, string.Join("\n", lines)));
			}
			else if (WhyNow(npc, players, settings) is { } routine)
			{
				due.Add((routine.Urgency, nearest, npc.Id, routine.Reason));
			}
		}
		return due.Count == 0 ? null
			: due.OrderBy(d => d.Urgency).ThenBy(d => d.Distance).Select(d => (d.NpcId, d.Reason)).First();
	}

	/// <summary>Why this NPC should act now without being spoken to (a player came over, or it's been a while), or null.</summary>
	private (Urgency Urgency, string Reason)? WhyNow(EntitySnapshot npc, List<EntitySnapshot> playersInRange, BrainOptions settings)
	{
		var now = time.GetUtcNow();
		var since = _lastTurn.TryGetValue(npc.Id, out var last) ? now - last : TimeSpan.MaxValue;

		var players = playersInRange.Where(p => Distance(p, npc) <= NoticeDistance).ToList();
		var known = _playersNearby.GetValueOrDefault(npc.Id) ?? [];
		var arrived = players.Where(p => !known.Contains(p.Id)).ToList();
		if (arrived.Count > 0 && since < settings.MinimumGap)
		{
			return null; // notice them on the next check, once the gap has passed
		}
		_playersNearby[npc.Id] = [.. players.Select(p => p.Id)];

		if (arrived.Count > 0)
		{
			return (Urgency.PlayerArrived, $"{string.Join(" and ", arrived.Select(p => p.Name))} just came over to you.");
		}
		var withPlayers = playersInRange.Count > 0;
		var interval = withPlayers ? settings.Interval / 2 : settings.Interval;
		return since >= interval
			? (withPlayers ? Urgency.NearPlayers : Urgency.Routine, withPlayers ? "Time to see how things are going." : "Time to see to the farm.")
			: null;
	}

	/// <summary>One turn for one NPC: brief the model, let it act through its tools, log what it did.</summary>
	public async Task<string?> TakeTurnAsync(Guid npcId, string reason, CancellationToken cancellationToken)
	{
		var settings = options.Value;
		_lastTurn[npcId] = time.GetUtcNow();
		await using var scope = scopes.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<FarmDbContext>();
		var files = scope.ServiceProvider.GetRequiredService<MemoryFiles>();
		var npc = await db.WorldEntities.AsNoTracking().FirstOrDefaultAsync(e => e.Id == npcId, cancellationToken);
		if (npc?.Persona is null)
		{
			return null;
		}
		var voice = npcs.Value.Defaults.FirstOrDefault(n => n.Name == npc.Name)?.Voice ?? DefaultVoice;

		var conversation = speech.Conversation(npcId);
		var briefing = NpcBriefing.Describe(world, npc) + "\n" +
			await NpcBriefing.MemoryAsync(world, files, npc, options.Value.PlayerMemoryRange, cancellationToken) +
			(conversation.Count == 0 ? "" : "\n## Recent conversation (oldest first)\n\n" +
				string.Join("", conversation.Select(l => $"- {l.Speaker}: \"{l.Text}\"\n"))) +
			$"\n## Why you're thinking now\n\n{reason}\n";

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(settings.TurnTimeout);
		var tools = new FarmerToolbox(world, files, npcId,
			(text, to) => SayAsync(npc, voice, text, to, timeout.Token));
		var chatOptions = new ChatOptions
		{
			Tools = tools.All(),
			Temperature = settings.Temperature,
		};
		List<ChatMessage> messages =
		[
			new(ChatRole.System, npc.Persona + "\n\n" + TurnInstructions(settings.MaxSteps)),
			new(ChatRole.User, briefing),
		];

		try
		{
			var started = time.GetTimestamp();
			var response = await chat.GetResponseAsync(messages, chatOptions, timeout.Token);
			var actions = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
				.Select(c => $"{c.Name}({string.Join(", ", c.Arguments?.Select(a => $"{a.Key}={a.Value}") ?? [])})").ToList();
			logger.LogInformation("{Npc} ({Reason}) in {Seconds:0.0}s did [{Actions}] and says: {Reply}",
				npc.Name, reason, time.GetElapsedTime(started).TotalSeconds, string.Join("; ", actions), response.Text.Trim());
			return response.Text;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			logger.LogWarning("{Npc}'s turn took longer than {Timeout} and was abandoned", npc.Name, settings.TurnTimeout);
		}
		catch (Exception e) when (e is not OperationCanceledException)
		{
			// The model may still be downloading, or the endpoint may be down: try again next time.
			logger.LogWarning("{Npc}'s turn failed: {Error}", npc.Name, BrainClient.Describe(e, settings));
		}
		return null;
	}

	/// <summary>
	/// The NPC says something out loud, to a player nearby if given; that player gets three
	/// things they might say back, written by the model in a short separate request.
	/// </summary>
	private async Task<string> SayAsync(WorldEntity npc, string voice, string text, Guid? to, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return "You didn't say anything.";
		}
		var me = world.Find(npc.Id);
		if (me is null)
		{
			return "You aren't on the farm.";
		}
		EntitySnapshot? player = null;
		if (to is null)
		{
			// Said to nobody in particular: if someone is within earshot, it's to the nearest.
			player = world.Near(me.X, me.Z, TalkDistance).FirstOrDefault(e => e.Kind == EntityKind.Player);
		}
		else if (to is { } id)
		{
			player = world.Find(id);
			if (player?.Kind != EntityKind.Player)
			{
				return "There's nobody here with that id. Use the player id from your briefing.";
			}
			if (Distance(player, me) > TalkDistance)
			{
				return $"{player.Name} is too far away to hear you; walk over first.";
			}
		}
		if (player is not null)
		{
			world.Engage(npc.Id, player.Id, EngageSeconds); // turn to them, and stay put while they answer
		}
		var replies = player is null ? [] : await RepliesAsync(npc, player, text, cancellationToken);
		await speech.NpcSaysAsync(me, text, voice, player, replies, cancellationToken);
		return player is null ? "You said it." : $"You said it to {player.Name}.";
	}

	public sealed record ReplyOptions(List<string> Replies);

	/// <summary>
	/// Five short, different things the player might say back, from the model: friendly, curious,
	/// practical, negative, and a goodbye ("I'll talk to you later"). Any it fails to write are filled in
	/// with plain ones.
	/// </summary>
	public async Task<IReadOnlyList<string>> RepliesAsync(WorldEntity npc, EntitySnapshot player, string line,
		CancellationToken cancellationToken)
	{
		var conversation = string.Join("\n", speech.Conversation(npc.Id).TakeLast(6).Select(l => $"{l.Speaker}: {l.Text}"));
		var prompt = $$"""
			{{player.Name}}, a human visiting the farm, is talking with {{npc.Name}}, the farmer.
			Conversation so far:
			{{(conversation.Length == 0 ? "(nothing yet)" : conversation)}}
			{{npc.Name}}: {{line}}

			Write exactly 5 different things {{player.Name}} might say back, each under 12 words, in
			the first person, in this order:
			1. friendly
			2. curious (a question)
			3. practical or about the animals
			4. negative (curt, doubtful, or disagreeing, but not abusive)
			5. ending the chat for now, like "I'll talk to you later"
			Answer with JSON only: {"replies": ["...", "...", "...", "...", "..."]}
			""";
		var replies = new List<string>();
		try
		{
			var response = await chat.GetResponseAsync(prompt,
				new ChatOptions { Temperature = options.Value.Temperature is null ? null : 0.8f }, cancellationToken);
			replies = ParseReplies(response.Text);
		}
		catch (Exception e) when (e is not OperationCanceledException)
		{
			logger.LogWarning("Couldn't think of replies for {Player}: {Error}", player.Name, BrainClient.Describe(e, options.Value));
		}
		return [.. replies.Concat(FallbackReplies.Skip(replies.Count)).Take(ReplyCount)];
	}

	/// <summary>
	/// The reply options in a model's answer: the JSON {"replies": [...]} wherever it sits in the
	/// text (models add code fences and chatter), or failing that, one option per line.
	/// </summary>
	public static List<string> ParseReplies(string text)
	{
		static string Clean(string r) => r.Trim().Trim('"', '\'', '`').Trim();
		var start = text.IndexOf('{');
		var end = text.LastIndexOf('}');
		IEnumerable<string> found = [];
		if (start >= 0 && end > start)
		{
			try
			{
				var options = System.Text.Json.JsonSerializer.Deserialize<ReplyOptions>(text[start..(end + 1)],
					new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
				found = options?.Replies ?? [];
			}
			catch (System.Text.Json.JsonException)
			{
				// Fall through to lines.
			}
		}
		if (!found.Any())
		{
			found = text.Split('\n').Select(l => l.TrimStart(' ', '-', '*', '•', '1', '2', '3', '4', '5', '.', ')')).Where(l => l.Length > 3);
		}
		return [.. found.Select(Clean).Where(r => r.Length > 0).Select(r => r.Length <= 120 ? r : r[..120]).Distinct().Take(ReplyCount)];
	}

	private static float Distance(EntitySnapshot a, EntitySnapshot b) =>
		MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

	private static string TurnInstructions(int maxSteps) => $"""
		## How your turns work

		Every so often you get a briefing on how things stand. Act on it with your tools: look
		around, walk to an animal or person, comfort an animal you're with, and talk (`say`).
		Take at most {maxSteps} actions, then finish with one plain sentence, in your own voice,
		of what you did and why. Use the ids your briefing and tools give you; never make one up.

		## Talking

		When someone speaks to you, answer them with `say`, passing their player id so they can
		reply. Greet people who come over. Keep each line short and spoken: one or two sentences,
		no lists or markdown. Don't talk to yourself when nobody is near.

		## Your memory

		Your memory is the memory file in your briefing, plus a smaller file for each person you
		know, shown when they're nearby. That is all you remember between turns. Each file has a
		fixed size: when you learn something worth keeping, rewrite the file (`rewrite_memory`, or
		`rewrite_memory_about` for a person) with the new thing worked in, and keep it within its
		size by summarising and dropping what matters least. Only rewrite when something has
		changed or is new; don't write the same thing again.
		""";
}

/// <summary>The farmer's tools for one turn, already tied to the NPC playing them.</summary>
public sealed class FarmerToolbox(GameWorld world, MemoryFiles files, Guid self,
	Func<string, Guid?, Task<string>>? say = null)
{
	public IList<AITool> All() =>
	[
		.. say is null ? Array.Empty<AITool>() : [AIFunctionFactory.Create(Say, "say")],
		AIFunctionFactory.Create(LookAround, "look_around"),
		AIFunctionFactory.Create(FarmOverview, "farm_overview"),
		AIFunctionFactory.Create(WalkTo, "walk_to"),
		AIFunctionFactory.Create(WalkOver, "walk_over_to"),
		AIFunctionFactory.Create(Comfort, "comfort"),
		AIFunctionFactory.Create(FindWater, "find_water"),
		AIFunctionFactory.Create(CollectWater, "collect_water"),
		AIFunctionFactory.Create(GiveWater, "give_water"),
		AIFunctionFactory.Create(RewriteMemory, "rewrite_memory"),
		AIFunctionFactory.Create(RewriteMemoryAbout, "rewrite_memory_about"),
	];

	/// <summary>How far from someone the NPC stops when it walks over to them (talking distance).</summary>
	public const float StandOff = 2.8f;

	public sealed record Seen(Guid Id, string Name, string Kind, string? Mood, int? Energy, string Doing, float X, float Z, float Distance);

	[Description("Say something out loud. Pass a person's player id to speak to them, so they can answer you.")]
	public Task<string> Say([Description("What you say: one or two short spoken sentences.")] string text,
		[Description("The player id of the person you're talking to. Leave it out to speak to whoever is nearest.")] Guid? playerId = null) =>
		say!(text, playerId);

	[Description("Who and what is near you: animals and people within the radius, nearest first.")]
	public IReadOnlyList<Seen> LookAround([Description("How far to look, in metres.")] float radius = 15f)
	{
		var me = Me();
		return [.. world.Near(me.X, me.Z, Math.Clamp(radius, 1f, 60f)).Where(e => e.Id != self).Select(e => See(e, me))];
	}

	[Description("Every animal on the farm: where each is, how it feels and what it's doing.")]
	public IReadOnlyList<Seen> FarmOverview()
	{
		var me = Me();
		return [.. world.Snapshot().Entities.Where(e => e.Kind == EntityKind.Animal).Select(e => See(e, me)).OrderBy(s => s.Distance)];
	}

	[Description("Walk to a spot on the ground (X and Z, in metres). You stay there afterwards.")]
	public string WalkTo(float x, float z) =>
		world.MoveTo(self, x, z) ? FormattableString.Invariant($"Walking to ({x:0.#}, {z:0.#}).") : "You can't walk right now.";

	[Description("Walk over to an animal or person, stopping just beside them.")]
	public string WalkOver([Description("Their id, from look_around or farm_overview.")] Guid id)
	{
		if (world.Find(id) is not { } target)
		{
			return "They aren't on the farm.";
		}
		var me = Me();
		var dx = me.X - target.X;
		var dz = me.Z - target.Z;
		var length = MathF.Max(MathF.Sqrt(dx * dx + dz * dz), 0.001f);
		// Stop a comfortable talking distance short, on our side of them.
		var x = target.X + dx / length * StandOff;
		var z = target.Z + dz / length * StandOff;
		return world.MoveTo(self, x, z) ? $"Walking over to {target.Name}." : "You can't walk right now.";
	}

	[Description("Replace your whole memory file with a new version (keep what still matters, work in what's new). " +
		"It must fit within the file's size; if it's too long you'll be told by how much.")]
	public async Task<string> RewriteMemory([Description("The complete new text of your memory file.")] string content)
	{
		try
		{
			var file = await files.WriteAsync(self, null, content);
			return $"Memory file saved ({file.Used} of {file.Limit} characters).";
		}
		catch (ArgumentException e)
		{
			return e.Message;
		}
	}

	[Description("Replace what you know about a person with a new version. It has a smaller size than your main " +
		"memory file; if it's too long you'll be told by how much.")]
	public async Task<string> RewriteMemoryAbout([Description("The person's player id, from your briefing or look_around.")] Guid playerId,
		[Description("The complete new text of what you know about them.")] string content)
	{
		try
		{
			var file = await files.WriteAsync(self, playerId, content);
			return $"Saved what you know about {file.SubjectName} ({file.Used} of {file.Limit} characters).";
		}
		catch (Exception e) when (e is ArgumentException or KeyNotFoundException)
		{
			return e.Message;
		}
	}

	[Description("Comfort an animal you're standing with (within a few metres), changing how it feels.")]
	public string Comfort([Description("The animal's id.")] Guid id,
		[Description("How it feels after your visit: Content, Happy or Playful.")] Mood mood)
	{
		if (world.Find(id) is not { Kind: EntityKind.Animal } animal)
		{
			return "That isn't an animal on the farm.";
		}
		var me = Me();
		var distance = MathF.Sqrt((animal.X - me.X) * (animal.X - me.X) + (animal.Z - me.Z) * (animal.Z - me.Z));
		if (distance > NpcBrain.ComfortDistance)
		{
			return FormattableString.Invariant($"{animal.Name} is {distance:0} m away; walk over first.");
		}
		if (mood is not (Mood.Content or Mood.Happy or Mood.Playful))
		{
			return "A visit can only make them content, happy or playful.";
		}
		world.SetMood(id, mood);
		return $"{animal.Name} seems {mood.ToString().ToLowerInvariant()} now.";
	}

	[Description("Walk to the nearest water (the river or the lake) so you can fill your bucket there.")]
	public string FindWater()
	{
		var me = Me();
		var distance = WaterMap.DistanceToWater(me.X, me.Z);
		if (distance <= WaterMap.CollectReach)
		{
			return "You're already at the water; collect_water.";
		}
		var (x, z) = WaterMap.NearestWaterPoint(me.X, me.Z);
		return world.MoveTo(self, x, z)
			? FormattableString.Invariant($"Walking to the water, {distance:0} m away, at ({x:0.#}, {z:0.#}).")
			: "You can't walk right now.";
	}

	[Description("Dip your bucket in the water you're standing at. Your bucket holds a few buckets' worth.")]
	public string CollectWater() => world.CollectWater(self, DateTimeOffset.UtcNow).Message;

	[Description("Give a bucket of water to an animal beside you: it drinks and perks up.")]
	public string GiveWater([Description("The animal's id.")] Guid id) => world.GiveWater(self, id).Message;

	private EntitySnapshot Me() => world.Find(self) ?? throw new InvalidOperationException("You aren't on the farm.");

	private static Seen See(EntitySnapshot e, EntitySnapshot me) => new(e.Id, e.Name, e.Kind.ToString(),
		e.Vitals?.Mood.ToString(), e.Vitals is { } v ? (int)MathF.Round(v.Energy) : null,
		e.Animation switch { "Lie_Idle" => "resting", "Walking" or "Walking_A" => "walking", "Running_A" => "running", _ => "standing" },
		MathF.Round(e.X, 1), MathF.Round(e.Z, 1), MathF.Round(MathF.Sqrt((e.X - me.X) * (e.X - me.X) + (e.Z - me.Z) * (e.Z - me.Z)), 1));
}
