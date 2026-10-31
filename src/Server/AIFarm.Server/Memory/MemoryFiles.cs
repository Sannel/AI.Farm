using AIFarm.Contracts;
using AIFarm.Server.Data;
using AIFarm.Server.Game;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AIFarm.Server.Memory;

/// <summary>A memory file's text and how full it is.</summary>
public sealed record MemoryFileView(Guid OwnerId, Guid? SubjectId, string? SubjectName, string Content, int Limit)
{
	public int Used => Content.Length;
}

/// <summary>
/// NPCs' memory files: a main notebook per NPC limited to <see cref="BrainOptions.MemoryFileSize"/>
/// characters, and one per player it knows, limited to a third of that. An NPC rewrites a file
/// whole, and anything over the limit is refused, so it has to keep its memory within size.
/// </summary>
public sealed class MemoryFiles(FarmDbContext db, IOptions<BrainOptions> options, TimeProvider time)
{
	public int MainLimit => options.Value.MemoryFileSize;

	public int PlayerLimit => options.Value.MemoryFileSize / 3;

	/// <summary>The NPC's main file, or what it knows about one player (empty if nothing yet).</summary>
	public async Task<MemoryFileView> ReadAsync(Guid npcId, Guid? playerId = null, CancellationToken cancellationToken = default)
	{
		await RequireAsync(npcId, playerId, cancellationToken);
		var file = await db.MemoryFiles.AsNoTracking()
			.FirstOrDefaultAsync(f => f.OwnerId == npcId && f.SubjectId == playerId, cancellationToken);
		return await ViewAsync(npcId, playerId, file?.Content ?? "", cancellationToken);
	}

	/// <summary>Replaces a file's text.</summary>
	/// <exception cref="ArgumentException">The text is longer than the file holds.</exception>
	public async Task<MemoryFileView> WriteAsync(Guid npcId, Guid? playerId, string content,
		CancellationToken cancellationToken = default)
	{
		await RequireAsync(npcId, playerId, cancellationToken);
		content = (content ?? "").Trim();
		var limit = playerId is null ? MainLimit : PlayerLimit;
		if (content.Length > limit)
		{
			throw new ArgumentException(
				$"That's {content.Length} characters, but this memory file holds at most {limit}. " +
				$"Shorten it by {content.Length - limit}: summarise, and drop what matters least.");
		}
		var file = await db.MemoryFiles.FirstOrDefaultAsync(f => f.OwnerId == npcId && f.SubjectId == playerId, cancellationToken);
		if (file is null)
		{
			file = new MemoryFile { Id = Guid.CreateVersion7(), OwnerId = npcId, SubjectId = playerId };
			db.MemoryFiles.Add(file);
		}
		file.Content = content;
		file.UpdatedAt = time.GetUtcNow();
		await db.SaveChangesAsync(cancellationToken);
		return await ViewAsync(npcId, playerId, content, cancellationToken);
	}

	/// <summary>What the NPC knows about each of these players (including ones it knows nothing about yet).</summary>
	public async Task<IReadOnlyList<MemoryFileView>> AboutPlayersAsync(Guid npcId, IEnumerable<Guid> playerIds,
		CancellationToken cancellationToken = default)
	{
		var ids = playerIds.Distinct().ToList();
		var names = await db.WorldEntities.AsNoTracking().Where(e => ids.Contains(e.Id))
			.ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken);
		var files = await db.MemoryFiles.AsNoTracking()
			.Where(f => f.OwnerId == npcId && f.SubjectId != null && ids.Contains(f.SubjectId.Value))
			.ToDictionaryAsync(f => f.SubjectId!.Value, f => f.Content, cancellationToken);
		return [.. ids.Where(names.ContainsKey).Select(id =>
			new MemoryFileView(npcId, id, names[id], files.GetValueOrDefault(id, ""), PlayerLimit))];
	}

	private async Task<MemoryFileView> ViewAsync(Guid npcId, Guid? playerId, string content, CancellationToken cancellationToken)
	{
		var name = playerId is null ? null
			: await db.WorldEntities.Where(e => e.Id == playerId).Select(e => e.Name).FirstOrDefaultAsync(cancellationToken);
		return new MemoryFileView(npcId, playerId, name, content, playerId is null ? MainLimit : PlayerLimit);
	}

	private async Task RequireAsync(Guid npcId, Guid? playerId, CancellationToken cancellationToken)
	{
		if (!await db.WorldEntities.AnyAsync(e => e.Id == npcId && e.Kind == EntityKind.Npc, cancellationToken))
		{
			throw new KeyNotFoundException($"There's no NPC {npcId}.");
		}
		if (playerId is { } id && !await db.WorldEntities.AnyAsync(e => e.Id == id && e.Kind == EntityKind.Player, cancellationToken))
		{
			throw new KeyNotFoundException($"There's no player {id}.");
		}
	}
}
