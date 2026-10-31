using AIFarm.Contracts;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace AIFarm.Server.Data;

/// <summary>
/// The server's PostgreSQL database: agent memories (with pgvector embeddings for similarity
/// search) and the persisted state of the game world.
/// </summary>
public sealed class FarmDbContext(DbContextOptions<FarmDbContext> options) : DbContext(options)
{
	/// <summary>
	/// Length of every stored embedding. Fixed by the database column (vector(768)), so the
	/// embedding model must produce this many dimensions (nomic-embed-text does; OpenAI's
	/// text-embedding-3 models can be asked to). Changing it needs a migration.
	/// </summary>
	public const int EmbeddingDimensions = 768;

	public DbSet<MemoryRecord> Memories => Set<MemoryRecord>();

	public DbSet<WorldEntity> WorldEntities => Set<WorldEntity>();

	public DbSet<MemoryFile> MemoryFiles => Set<MemoryFile>();

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		modelBuilder.HasPostgresExtension("vector");

		modelBuilder.Entity<MemoryRecord>(memory =>
		{
			memory.ToTable("memories");
			memory.Property(m => m.AgentId).HasMaxLength(200);
			memory.Property(m => m.Embedding).HasColumnType($"vector({EmbeddingDimensions})");
			memory.Property(m => m.Metadata).HasColumnType("jsonb");
			memory.HasIndex(m => new { m.AgentId, m.CreatedAt });
			memory.HasIndex(m => m.Tags).HasMethod("gin");
			// Approximate nearest-neighbour index for cosine-distance searches.
			memory.HasIndex(m => m.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
			// An entity's memories go with it.
			memory.HasOne<WorldEntity>().WithMany().HasForeignKey(m => m.EntityId).OnDelete(DeleteBehavior.Cascade);
		});

		modelBuilder.Entity<MemoryFile>(file =>
		{
			file.ToTable("memory_files");
			// One main file per NPC (no subject) and one per player it knows.
			file.HasIndex(f => new { f.OwnerId, f.SubjectId }).IsUnique().AreNullsDistinct(false);
			file.HasOne<WorldEntity>().WithMany().HasForeignKey(f => f.OwnerId).OnDelete(DeleteBehavior.Cascade);
			file.HasOne<WorldEntity>().WithMany().HasForeignKey(f => f.SubjectId).OnDelete(DeleteBehavior.Cascade);
		});

		modelBuilder.Entity<WorldEntity>(entity =>
		{
			entity.ToTable("world_entities");
			entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(32);
			entity.Property(e => e.Name).HasMaxLength(200);
			entity.Property(e => e.Model).HasMaxLength(200);
			entity.Property(e => e.Mood).HasConversion<string>().HasMaxLength(32);
			// Players are found by name when they join, so a name belongs to one player.
			entity.HasIndex(e => e.Name).IsUnique().HasFilter("\"Kind\" = 'Player'");
		});
	}
}

/// <summary>
/// An NPC's memory file: a size-limited notebook it rewrites as it goes. With no subject it's
/// the NPC's main file; with a subject (a player), what it knows about that person.
/// </summary>
public sealed class MemoryFile
{
	public Guid Id { get; set; }

	public Guid OwnerId { get; set; }

	public Guid? SubjectId { get; set; }

	public string Content { get; set; } = "";

	public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Something an AI agent asked to remember. The owner is either an outside agent (AgentId is
/// theirs) or an entity in the world (EntityId is set and AgentId is "entity:&lt;id&gt;").
/// </summary>
public sealed class MemoryRecord
{
	public Guid Id { get; set; }

	/// <summary>Whose memory this is; searches only look within one owner's memories.</summary>
	public required string AgentId { get; set; }

	/// <summary>The world entity this memory belongs to, for entity memories.</summary>
	public Guid? EntityId { get; set; }

	/// <summary>Where the entity was when it formed the memory (entity memories only).</summary>
	public float? X { get; set; }

	public float? Y { get; set; }

	public float? Z { get; set; }

	public required string Content { get; set; }

	/// <summary>Free-form labels for filtering (e.g. "player:adam", "fact", "plan").</summary>
	public string[] Tags { get; set; } = [];

	/// <summary>Optional JSON the agent wants kept alongside (stored as jsonb).</summary>
	public string? Metadata { get; set; }

	public required Vector Embedding { get; set; }

	public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A game entity's saved state: animals, props, and players (kept between sessions by name).
/// The game loop writes positions back here as things move.
/// </summary>
public sealed class WorldEntity
{
	public Guid Id { get; set; }

	public EntityKind Kind { get; set; }

	public required string Name { get; set; }

	public required string Model { get; set; }

	public float X { get; set; }

	public float Y { get; set; }

	public float Z { get; set; }

	public float Yaw { get; set; }

	/// <summary>Where an animal wanders around (its pen or favourite spot).</summary>
	public float HomeX { get; set; }

	public float HomeZ { get; set; }

	/// <summary>Whether this entity keeps memories (it has an AI that remembers things).</summary>
	public bool HasMemory { get; set; }

	/// <summary>Buckets of water carried (players and NPCs).</summary>
	public int Water { get; set; }

	/// <summary>An NPC's persona: who it is and how it behaves, for the AI agent playing it (markdown).</summary>
	public string? Persona { get; set; }

	/// <summary>What a player is allowed to do (see <see cref="AIFarm.Contracts.Permissions"/>); empty for everyone else.</summary>
	public string[] Permissions { get; set; } = [];

	// Vital signs, for living entities (players and animals); null for props.

	public Mood? Mood { get; set; }

	/// <summary>0-100 percent.</summary>
	public float? Energy { get; set; }

	/// <summary>Age in game days.</summary>
	public double? AgeDays { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }
}
