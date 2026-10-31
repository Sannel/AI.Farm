using AIFarm.Server.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace AIFarm.Server.Tests;

/// <summary>One PostgreSQL + pgvector container shared by every test that needs a database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
	private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("docker.io/pgvector/pgvector:0.8.7-pg18").Build();

	public Task InitializeAsync() => _container.StartAsync();

	public Task DisposeAsync() => _container.DisposeAsync().AsTask();

	/// <summary>A connection string to a fresh, empty database (created by the server's migrations).</summary>
	public string NewDatabase() =>
		new Npgsql.NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = $"test_{Guid.NewGuid():N}" }
			.ConnectionString;
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
	public const string Name = "Database";
}

/// <summary>
/// The real server against its own test database, with a stand-in embedding model so no
/// Ollama is needed.
/// </summary>
public sealed class FarmFactory(PostgresFixture postgres, string? apiKey = null) : WebApplicationFactory<Program>
{
	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseSetting("ConnectionStrings:Farm", postgres.NewDatabase());
		builder.UseSetting("Security:ApiKey", apiKey ?? "");
		builder.UseSetting("Game:SaveInterval", "00:00:00.25"); // save positions often so tests can see them
		builder.UseSetting("Brain:Enabled", "false"); // no language model here; BrainTests drive turns themselves
		builder.ConfigureTestServices(services =>
			services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, WordHashEmbeddings>());
	}

	/// <summary>Runs something against this server's database.</summary>
	public async Task<T> WithDb<T>(Func<FarmDbContext, Task<T>> action)
	{
		await using var scope = Services.CreateAsyncScope();
		return await action(scope.ServiceProvider.GetRequiredService<FarmDbContext>());
	}
}

/// <summary>
/// A deterministic stand-in for an embedding model: each word adds weight to a few hashed
/// dimensions, so texts sharing words come out similar. Enough to test search ordering.
/// </summary>
public sealed class WordHashEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
{
	public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
		EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
		Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(v => new Embedding<float>(Embed(v)))));

	public static float[] Embed(string text)
	{
		var vector = new float[FarmDbContext.EmbeddingDimensions];
		foreach (var word in text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var hash = (uint)StableHash(word.Trim('.', ',', '!', '?'));
			for (var k = 0; k < 3; k++)
			{
				vector[(hash >> (k * 9)) % (uint)vector.Length] += 1f;
			}
		}
		var length = MathF.Sqrt(vector.Sum(v => v * v));
		return length == 0 ? vector : [.. vector.Select(v => v / length)];
	}

	private static int StableHash(string word)
	{
		unchecked
		{
			var hash = (int)2166136261;
			foreach (var c in word)
			{
				hash = (hash ^ c) * 16777619;
			}
			return hash;
		}
	}

	public object? GetService(Type serviceType, object? serviceKey = null) => null;

	public void Dispose()
	{
	}
}
