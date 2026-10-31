using System.Net;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AIFarm.Server.Tests;

/// <summary>Drives the MCP endpoint the way an AI agent's MCP client would.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class McpTests(PostgresFixture postgres)
{
	private const string Key = "test-key";

	private static async Task<McpClient> Connect(FarmFactory factory, string? key)
	{
		var http = factory.CreateClient();
		if (key is not null)
		{
			http.DefaultRequestHeaders.Authorization = new("Bearer", key);
		}
		var transport = new HttpClientTransport(
			new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
			http, ownsHttpClient: true);
		return await McpClient.CreateAsync(transport);
	}

	private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

	[Fact]
	public async Task AgentCanRememberAndRecallThroughMcp()
	{
		await using var factory = new FarmFactory(postgres, Key);
		await using var client = await Connect(factory, Key);

		var tools = await client.ListToolsAsync();
		Assert.Superset(new HashSet<string> { "remember", "recall", "recent_memories", "forget", "entity_remember", "entity_recall",
			"entity_recent_memories", "entity_forget", "world_snapshot", "find_nearby", "get_entity" },
			tools.Select(t => t.Name).ToHashSet());

		var remembered = await client.CallToolAsync("remember", new Dictionary<string, object?>
		{
			["agentId"] = "farmhand", ["content"] = "The goats escaped through the north fence.", ["tags"] = new[] { "incident" },
		});
		Assert.NotEqual(true, remembered.IsError);

		var recalled = await client.CallToolAsync("recall", new Dictionary<string, object?>
		{
			["agentId"] = "farmhand", ["query"] = "how did the goats escape?",
		});
		Assert.Contains("north fence", Text(recalled));
	}

	[Fact]
	public async Task AgentCanSeeTheWorld()
	{
		await using var factory = new FarmFactory(postgres);
		await using var client = await Connect(factory, key: null);

		var result = await client.CallToolAsync("world_snapshot");

		using var snapshot = JsonDocument.Parse(Text(result));
		var models = snapshot.RootElement.GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("model").GetString());
		Assert.Contains("Cow_Female", models);
	}

	[Fact]
	public async Task McpAndMemoryApiNeedTheKeyWhenOneIsSet()
	{
		await using var factory = new FarmFactory(postgres, Key);
		var http = factory.CreateClient();

		Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/agents/farmhand/memories")).StatusCode);
		await Assert.ThrowsAnyAsync<Exception>(() => Connect(factory, "wrong-key"));
		Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
	}
}
