namespace AIFarm.Server.Memory;

/// <summary>
/// The memory store over plain HTTP, for callers that aren't MCP clients. Callers may pass
/// their own embeddings instead of having the server make them.
/// </summary>
public static class MemoryEndpoints
{
	public sealed record RememberRequest(string Content, string[]? Tags = null, string? Metadata = null, float[]? Embedding = null);

	public sealed record SearchRequest(string? Query, int Limit = 5, string[]? Tags = null, float[]? Embedding = null);

	public static IEndpointRouteBuilder MapMemoryEndpoints(this IEndpointRouteBuilder app)
	{
		var group = app.MapGroup("/api/agents/{agentId}/memories").WithTags("Memories");

		group.MapPost("/", async (string agentId, RememberRequest request, MemoryStore store, CancellationToken ct) =>
		{
			var memory = await store.RememberAsync(agentId, request.Content, request.Tags, request.Metadata, request.Embedding, ct);
			return Results.Created($"/api/agents/{agentId}/memories/{memory.Id}", memory);
		});

		group.MapPost("/search", (string agentId, SearchRequest request, MemoryStore store, CancellationToken ct) =>
			store.SearchAsync(agentId, request.Query, request.Limit, request.Tags, request.Embedding, ct));

		group.MapGet("/", (string agentId, int? limit, string[]? tag, MemoryStore store, CancellationToken ct) =>
			store.RecentAsync(agentId, limit ?? 20, tag, ct));

		group.MapDelete("/{id:guid}", async (string agentId, Guid id, MemoryStore store, CancellationToken ct) =>
			await store.ForgetAsync(agentId, id, ct) ? Results.NoContent() : Results.NotFound());

		// The same for entities in the game world that keep memories.
		var entities = app.MapGroup("/api/entities/{entityId:guid}/memories").WithTags("Entity memories");

		entities.MapPost("/", async (Guid entityId, RememberRequest request, MemoryStore store, CancellationToken ct) =>
		{
			var memory = await store.RememberForEntityAsync(entityId, request.Content, request.Tags, request.Metadata,
				request.Embedding, ct);
			return Results.Created($"/api/entities/{entityId}/memories/{memory.Id}", memory);
		});

		entities.MapPost("/search", (Guid entityId, SearchRequest request, MemoryStore store, CancellationToken ct) =>
			store.SearchForEntityAsync(entityId, request.Query, request.Limit, request.Tags, request.Embedding, ct));

		entities.MapGet("/", (Guid entityId, int? limit, string[]? tag, MemoryStore store, CancellationToken ct) =>
			store.RecentForEntityAsync(entityId, limit ?? 20, tag, ct));

		entities.MapDelete("/{id:guid}", async (Guid entityId, Guid id, MemoryStore store, CancellationToken ct) =>
			await store.ForgetForEntityAsync(entityId, id, ct) ? Results.NoContent() : Results.NotFound());

		return app;
	}
}
