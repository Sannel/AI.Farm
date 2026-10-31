using System.Net;
using System.Net.Http.Json;
using AIFarm.Server.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIFarm.Server.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class MemoryTests(PostgresFixture postgres) : IAsyncLifetime
{
	private FarmFactory _factory = null!;

	public Task InitializeAsync()
	{
		_factory = new FarmFactory(postgres);
		_ = _factory.Server; // start the server (and run migrations)
		return Task.CompletedTask;
	}

	public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

	private async Task<T> WithStore<T>(Func<MemoryStore, Task<T>> action)
	{
		await using var scope = _factory.Services.CreateAsyncScope();
		return await action(scope.ServiceProvider.GetRequiredService<MemoryStore>());
	}

	[Fact]
	public async Task RecallFindsTheClosestMemoryFirst()
	{
		await WithStore(async store =>
		{
			await store.RememberAsync("farmhand", "The cow likes to graze by the old oak tree.");
			await store.RememberAsync("farmhand", "Sam prefers carrots over apples.");
			return await store.RememberAsync("farmhand", "The barn door squeaks when it rains.");
		});

		var results = await WithStore(store => store.SearchAsync("farmhand", "what does Sam prefer, carrots or apples?", limit: 3));

		Assert.Equal(3, results.Count);
		Assert.Equal("Sam prefers carrots over apples.", results[0].Content);
		Assert.True(results[0].Score > results[1].Score);
	}

	[Fact]
	public async Task AgentsOnlySeeTheirOwnMemories()
	{
		await WithStore(store => store.RememberAsync("alice", "Secret: the key is under the mat."));

		var bobs = await WithStore(store => store.SearchAsync("bob", "where is the key?"));

		Assert.Empty(bobs);
	}

	[Fact]
	public async Task TagsNarrowTheSearch()
	{
		await WithStore(async store =>
		{
			await store.RememberAsync("farmhand", "Feed the chickens at dawn.", ["Chores", "morning"]);
			return await store.RememberAsync("farmhand", "Feed the pigs at dusk.", ["chores", "evening"]);
		});

		var morning = await WithStore(store => store.SearchAsync("farmhand", "feed", tags: ["chores", "MORNING"]));

		Assert.Equal("Feed the chickens at dawn.", Assert.Single(morning).Content);
	}

	[Fact]
	public async Task ForgetDeletesOnlyTheOwnersMemory()
	{
		var memory = await WithStore(store => store.RememberAsync("farmhand", "Temporary note."));

		Assert.False(await WithStore(store => store.ForgetAsync("someone-else", memory.Id)));
		Assert.True(await WithStore(store => store.ForgetAsync("farmhand", memory.Id)));
		Assert.Empty(await WithStore(store => store.RecentAsync("farmhand")));
	}

	[Fact]
	public async Task HttpApiStoresSearchesAndRejectsWrongSizedEmbeddings()
	{
		var client = _factory.CreateClient();

		var created = await client.PostAsJsonAsync("/api/agents/scout/memories",
			new MemoryEndpoints.RememberRequest("The fox den is past the river.", ["places"]));
		Assert.Equal(HttpStatusCode.Created, created.StatusCode);

		var found = await client.PostAsJsonAsync("/api/agents/scout/memories/search", new MemoryEndpoints.SearchRequest("fox den"));
		var results = await found.Content.ReadFromJsonAsync<MemoryItem[]>();
		Assert.Equal("The fox den is past the river.", Assert.Single(results!).Content);

		var bad = await client.PostAsJsonAsync("/api/agents/scout/memories",
			new MemoryEndpoints.RememberRequest("Wrong size", Embedding: [1f, 2f, 3f]));
		Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
	}
}
