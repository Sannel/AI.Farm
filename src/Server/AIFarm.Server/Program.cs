using System.ClientModel;
using AIFarm.Contracts;
using AIFarm.Server;
using AIFarm.Server.Data;
using AIFarm.Server.Game;
using AIFarm.Server.Memory;
using AIFarm.Server.Speech;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<FarmDbContext>(options =>
	options.UseNpgsql(builder.Configuration.GetConnectionString("Farm"), npgsql => npgsql.UseVector()));
builder.Services.AddSingleton(TimeProvider.System);

// Memory: embeddings from an OpenAI-compatible endpoint (Ollama by default), stored in pgvector.
builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection(EmbeddingOptions.Section));
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(services =>
{
	var settings = services.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
	var client = new OpenAIClient(new ApiKeyCredential(settings.ApiKey), new OpenAIClientOptions { Endpoint = settings.Endpoint });
	return client.GetEmbeddingClient(settings.Model).AsIEmbeddingGenerator(settings.Dimensions);
});
builder.Services.AddScoped<MemoryStore>();
builder.Services.AddScoped<MemoryFiles>();

// Game: one authoritative world, stepped by the game loop and reached through the hub.
builder.Services.Configure<GameOptions>(builder.Configuration.GetSection(GameOptions.Section));
builder.Services.Configure<UsersOptions>(builder.Configuration.GetSection(UsersOptions.Section));
builder.Services.Configure<NpcsOptions>(builder.Configuration.GetSection(NpcsOptions.Section));
builder.Services.AddSingleton(services =>
	new GameWorld(Random.Shared, services.GetRequiredService<IOptions<GameOptions>>().Value.DayLength));
builder.Services.AddSingleton<PlayerConnections>();
builder.Services.AddHostedService<GameLoop>();

// NPC brains: a language model with tool calling plays the NPCs (Gemma 4 on Ollama by default, or Azure AI Foundry).
builder.Services.Configure<BrainOptions>(builder.Configuration.GetSection(BrainOptions.Section));
builder.Services.AddSingleton<IChatClient>(services =>
	BrainClient.Create(services.GetRequiredService<IOptions<BrainOptions>>().Value, services));
builder.Services.AddSingleton<NpcBrain>();
builder.Services.AddHostedService(services => services.GetRequiredService<NpcBrain>());

// Speech: bubbles for everyone, and NPC voices made with Kokoro and streamed to clients.
builder.Services.Configure<SpeechOptions>(builder.Configuration.GetSection(SpeechOptions.Section));
builder.Services.AddSingleton<VoiceSynthesizer>();
builder.Services.AddSingleton<SpeechService>();
builder.Services.AddSignalR();

builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "AI Farm", Version = "0.1.0" })
	.WithHttpTransport()
	.WithTools<MemoryTools>()
	.WithTools<EntityMemoryTools>()
	.WithTools<GameTools>()
	.WithTools<NpcTools>()
	.WithPrompts<NpcTools>();

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddDbContextCheck<FarmDbContext>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
	var db = scope.ServiceProvider.GetRequiredService<FarmDbContext>();
	if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
	{
		await db.Database.MigrateAsync();
	}
	// Default players (Adam, who may spawn and destroy animals) exist from the start.
	await DefaultUsers.EnsureAsync(db, app.Services.GetRequiredService<IOptions<UsersOptions>>().Value,
		app.Services.GetRequiredService<IOptions<GameOptions>>().Value.PlayerStartAgeDays, TimeProvider.System);
	// And the NPCs, like Claude the farmer, with their personas.
	await DefaultNpcs.EnsureAsync(db, app.Services.GetRequiredService<IOptions<NpcsOptions>>().Value, TimeProvider.System);
}

// Bad input (e.g. a wrong-sized embedding) is the caller's mistake: answer 400 (or 404 for an
// unknown entity), not 500.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
	StatusCodeSelector = exception => exception switch
	{
		KeyNotFoundException => StatusCodes.Status404NotFound,
		ArgumentException => StatusCodes.Status400BadRequest,
		_ => StatusCodes.Status500InternalServerError,
	},
});
app.UseWebSockets();
app.UseMiddleware<ApiKeyMiddleware>();

app.MapHealthChecks("/health");
app.MapMcp("/mcp");
app.MapMemoryEndpoints();
app.MapHub<GameHub>(GameHubProtocol.Path);
app.MapGet("/api/world", (GameWorld world) => world.Snapshot()).WithTags("World");
app.MapPut("/api/entities/{entityId:guid}/mood", async (Guid entityId, MoodRequest request, GameWorld world, FarmDbContext db,
		TimeProvider time, CancellationToken ct) =>
	await GameTools.SetMoodAsync(world, db, time, entityId, request.Mood, ct) ? Results.NoContent() : Results.NotFound())
	.WithTags("World");

app.Run();

/// <summary>Visible to the integration tests' WebApplicationFactory.</summary>
public partial class Program;

/// <summary>Body of PUT /api/entities/{entityId}/mood.</summary>
public sealed record MoodRequest(Mood Mood);
