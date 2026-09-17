using ChatBot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatBot.Data;

/// <summary>
/// EF Core DbContext for ChatBot.
///
/// PRODUCTION NOTE: One DbContext per bounded context.
/// If app grows, you might have separate contexts for:
/// - VectorDbContext (chunks, embeddings)
/// - ChatDbContext (conversations, messages)
/// - UserDbContext (auth, profiles)
///
/// For learning: single context is fine.
/// </summary>
public class ChatBotDbContext : DbContext
{
    public ChatBotDbContext(DbContextOptions<ChatBotDbContext> options) : base(options) { }

    public DbSet<DocumentChunkEntity> DocumentChunks => Set<DocumentChunkEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentChunkEntity>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Index on ChunkId for fast lookups and duplicate prevention
            entity.HasIndex(e => e.ChunkId).IsUnique();

            // Index on DocumentName for "delete all chunks from document X" operations
            entity.HasIndex(e => e.DocumentName);

            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.EmbeddingBlob).IsRequired();
        });
    }
}
