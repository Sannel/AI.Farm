using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFarm.Contracts;
using Godot;
using Microsoft.AspNetCore.SignalR.Client;

namespace AIFarm.Network;

/// <summary>
/// Autoload holding the connection to the game server (SignalR, see <see cref="GameHubProtocol"/>).
/// The main menu connects and joins; the game scene reads <see cref="Latest"/> snapshots and
/// sends the player's input. SignalR calls back on worker threads, so everything handed to
/// the game is picked up on the main thread in <see cref="_Process"/>.
/// </summary>
public partial class ServerConnection : Node
{
	/// <summary>Raised on the main thread when the connection drops unexpectedly.</summary>
	[Signal]
	public delegate void ConnectionLostEventHandler(string reason);

	public static ServerConnection Instance { get; private set; } = null!;

	/// <summary>Raised on the main thread when someone says something in the world.</summary>
	public event Action<SpeechEvent>? SpeechHeard;

	private HubConnection? _hub;
	private WorldSnapshot? _incoming;
	private readonly System.Collections.Concurrent.ConcurrentQueue<SpeechEvent> _speech = new();
	private string? _lostReason;

	/// <summary>True between a successful <see cref="ConnectAsync"/> and <see cref="Disconnect"/>.</summary>
	public bool IsOnline => _hub?.State == HubConnectionState.Connected && PlayerId != Guid.Empty;

	/// <summary>The entity the server gave us to play.</summary>
	public Guid PlayerId { get; private set; }

	public string PlayerName { get; private set; } = "";

	/// <summary>Why the last game ended by losing the connection, for the main menu to show; cleared once shown.</summary>
	public string? LostReason { get; set; }

	/// <summary>What the server lets this player do (see <see cref="Permissions"/>).</summary>
	public IReadOnlyList<string> Permissions { get; private set; } = [];

	/// <summary>The animals this player may spawn; empty without the spawn permission.</summary>
	public IReadOnlyList<SpawnableAnimal> SpawnableAnimals { get; private set; } = [];

	public bool Can(string permission) => Permissions.Contains(permission);

	/// <summary>The newest world snapshot received (on the main thread); null until the first.</summary>
	public WorldSnapshot? Latest { get; private set; }

	public override void _EnterTree()
	{
		Instance = this;
		ProcessMode = ProcessModeEnum.Always;
	}

	public override void _Process(double delta)
	{
		if (Interlocked.Exchange(ref _incoming, null) is { } snapshot)
		{
			Latest = snapshot;
		}
		while (_speech.TryDequeue(out var said))
		{
			SpeechHeard?.Invoke(said);
		}
		if (Interlocked.Exchange(ref _lostReason, null) is { } reason)
		{
			EmitSignal(SignalName.ConnectionLost, reason);
		}
	}

	/// <summary>Connects to the server at <paramref name="address"/> and joins the world as <paramref name="name"/>.</summary>
	/// <exception cref="Exception">Couldn't reach the server, or it turned us away.</exception>
	public async Task ConnectAsync(string address, string name, CancellationToken cancellationToken = default)
	{
		await DisconnectAsync();
		var hub = new HubConnectionBuilder()
			.WithUrl(new Uri(new Uri(NormaliseAddress(address)), GameHubProtocol.Path))
			.Build();
		hub.On<WorldSnapshot>(GameHubProtocol.Snapshot, snapshot => Volatile.Write(ref _incoming, snapshot));
		hub.On<SpeechEvent>(GameHubProtocol.Speech, said => _speech.Enqueue(said));
		hub.Closed += error =>
		{
			if (ReferenceEquals(_hub, hub))
			{
				Volatile.Write(ref _lostReason, error?.Message ?? "The server closed the connection.");
			}
			return Task.CompletedTask;
		};

		try
		{
			await hub.StartAsync(cancellationToken);
			var joined = await hub.InvokeAsync<JoinResult>(GameHubProtocol.Join, name, cancellationToken);
			SpawnableAnimals = joined.Permissions.Contains(Contracts.Permissions.SpawnAnimals)
				? await hub.InvokeAsync<SpawnableAnimal[]>(GameHubProtocol.GetSpawnableAnimals, cancellationToken)
				: [];
			_hub = hub;
			PlayerId = joined.PlayerId;
			PlayerName = name;
			Permissions = joined.Permissions;
			Latest = joined.Snapshot;
		}
		catch
		{
			await hub.DisposeAsync();
			throw;
		}
	}

	/// <summary>Sends the local player's movement; quietly does nothing when not connected.</summary>
	public void SendInput(PlayerInput input)
	{
		if (IsOnline)
		{
			// Fire and forget: input is sent many times a second, so a lost one doesn't matter.
			_ = _hub!.SendAsync(GameHubProtocol.SendInput, input);
		}
	}

	/// <summary>Asks the server to spawn an animal in front of the player; returns the new animal.</summary>
	/// <exception cref="HubException">Not allowed, or no such animal.</exception>
	public Task<EntitySnapshot> SpawnAnimalAsync(string model) =>
		IsOnline
			? _hub!.InvokeAsync<EntitySnapshot>(GameHubProtocol.SpawnAnimal, model)
			: Task.FromException<EntitySnapshot>(new InvalidOperationException("Not connected to a server."));

	/// <summary>Asks the server to remove an animal for good.</summary>
	/// <exception cref="HubException">Not allowed, or it can't be destroyed.</exception>
	/// <summary>Says something to an NPC nearby (it answers on its next turn).</summary>
	/// <exception cref="HubException">Too far away, or no such NPC.</exception>
	public Task SayToNpcAsync(Guid npcId, string text) =>
		IsOnline
			? _hub!.InvokeAsync(GameHubProtocol.SayToNpc, npcId, text)
			: Task.FromException(new InvalidOperationException("Not connected to a server."));

	/// <summary>Dips a bucket in the water the player is at; returns how many buckets they now carry.</summary>
	/// <exception cref="HubException">Not near water, or the bucket is full.</exception>
	public Task<int> CollectWaterAsync() =>
		IsOnline
			? _hub!.InvokeAsync<int>(GameHubProtocol.CollectWater)
			: Task.FromException<int>(new InvalidOperationException("Not connected to a server."));

	/// <summary>A line's voice from the server, piece by piece while it's being made.</summary>
	public IAsyncEnumerable<SpeechAudioChunk> StreamSpeech(Guid speechId, CancellationToken cancellationToken) =>
		IsOnline
			? _hub!.StreamAsync<SpeechAudioChunk>(GameHubProtocol.StreamSpeech, speechId, cancellationToken)
			: AsyncEnumerable.Empty<SpeechAudioChunk>();

	public Task DestroyEntityAsync(Guid entityId) =>
		IsOnline
			? _hub!.InvokeAsync(GameHubProtocol.DestroyEntity, entityId)
			: Task.FromException(new InvalidOperationException("Not connected to a server."));

	/// <summary>Leaves the world (the server saves where we stopped).</summary>
	public void Disconnect() => _ = DisconnectAsync();

	private async Task DisconnectAsync()
	{
		var hub = _hub;
		_hub = null;
		PlayerId = Guid.Empty;
		Permissions = [];
		SpawnableAnimals = [];
		Latest = null;
		if (hub is not null)
		{
			await hub.DisposeAsync();
		}
	}

	public override void _ExitTree() => Disconnect();

	/// <summary>"myserver" or "myserver:8080" become http URLs; full URLs are kept.</summary>
	public static string NormaliseAddress(string address)
	{
		address = address.Trim();
		if (!address.Contains("://"))
		{
			address = "http://" + address;
		}
		var uri = new Uri(address);
		return uri.IsDefaultPort && uri.Scheme == "http" && !address[(address.IndexOf("://") + 3)..].Contains(':')
			? new UriBuilder(uri) { Port = DefaultPort }.Uri.ToString()
			: uri.ToString();
	}

	public const int DefaultPort = 8080;
}
