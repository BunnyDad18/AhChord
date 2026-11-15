using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using WebApi.Entities;
using WebApi.Identity;

namespace WebApi.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Song> Songs => Set<Song>();
    public DbSet<Playlist> Playlists => Set<Playlist>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<PlaylistSong> PlaylistSongs => Set<PlaylistSong>();
    public DbSet<PlayHistory> PlayHistory => Set<PlayHistory>();
    public DbSet<ImportDraft> ImportDrafts => Set<ImportDraft>();
    public DbSet<LlmSettings> LlmSettings => Set<LlmSettings>();
    public DbSet<MetadataProposal> MetadataProposals => Set<MetadataProposal>(); // ST-35 F3
    public DbSet<AppMeta> AppMeta => Set<AppMeta>(); // ST-43: one-shot markers (seeded flag)
    public DbSet<WishlistItem> WishlistItems => Set<WishlistItem>(); // ST-44
    public DbSet<SongSourceFile> SongSourceFiles => Set<SongSourceFile>(); // ST-46: source files (PDF/PNG/JPG) per song
    public DbSet<MetadataScanProgress> MetadataScanProgresses => Set<MetadataScanProgress>(); // ST-68: live scan progress (single row)
    public DbSet<Tag> Tags => Set<Tag>(); // ST-106: artists/genres/free tags (Kind)
    public DbSet<SongTagLink> SongTagLinks => Set<SongTagLink>();
    public DbSet<PlaylistTagLink> PlaylistTagLinks => Set<PlaylistTagLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Playlist n—1 Collection: deleting a collection unassigns (not deletes) its playlists.
        modelBuilder.Entity<Playlist>()
            .HasOne(p => p.Collection)
            .WithMany()
            .HasForeignKey(p => p.CollectionId)
            .OnDelete(DeleteBehavior.SetNull);

        // Playlist <-> Song: ordered many-to-many via the PlaylistSong join table.
        modelBuilder.Entity<PlaylistSong>()
            .HasKey(ps => new { ps.PlaylistId, ps.SongId });

        modelBuilder.Entity<PlaylistSong>()
            .HasOne<Playlist>()
            .WithMany()
            .HasForeignKey(ps => ps.PlaylistId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PlaylistSong>()
            .HasOne<Song>()
            .WithMany()
            .HasForeignKey(ps => ps.SongId)
            .OnDelete(DeleteBehavior.Cascade);

        // PlayHistory n—1 Song: deleting a song removes its history rows.
        modelBuilder.Entity<PlayHistory>()
            .HasOne<Song>()
            .WithMany()
            .HasForeignKey(h => h.SongId)
            .OnDelete(DeleteBehavior.Cascade);

        // ST-106: tags — one table, three kinds (Artist, Genre, free Tag). A song carries
        // any-kind tags; a playlist only ever Kind=Tag links (setlist grouping, the
        // collection retirement). Both joins cascade, mirroring PlaylistSong.
        modelBuilder.Entity<SongTagLink>()
            .HasKey(t => new { t.SongId, t.TagId });

        modelBuilder.Entity<SongTagLink>()
            .HasOne<Song>()
            .WithMany()
            .HasForeignKey(t => t.SongId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SongTagLink>()
            .HasOne<Tag>()
            .WithMany()
            .HasForeignKey(t => t.TagId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PlaylistTagLink>()
            .HasKey(t => new { t.PlaylistId, t.TagId });

        modelBuilder.Entity<PlaylistTagLink>()
            .HasOne<Playlist>()
            .WithMany()
            .HasForeignKey(t => t.PlaylistId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PlaylistTagLink>()
            .HasOne<Tag>()
            .WithMany()
            .HasForeignKey(t => t.TagId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes (TECHNICAL_ARCHITECTURE.md §4).
        modelBuilder.Entity<Song>().HasIndex(s => s.Title);
        modelBuilder.Entity<Song>().HasIndex(s => s.Artist);
        modelBuilder.Entity<PlaylistSong>().HasIndex(ps => new { ps.PlaylistId, ps.Position });
        modelBuilder.Entity<PlayHistory>().HasIndex(h => new { h.UserId, h.ViewedAt });

        // ST-30: at most one LLM-settings row per user. ST-63: the single global row
        // (UserId == null) now has a DB constraint too. A filtered index on a nullable column
        // is NOT enough — Postgres unique indexes treat NULL keys as distinct, so the index
        // also needs NULLS NOT DISTINCT (PG15+, via Npgsql AreNullsDistinct(false)); the
        // filter keeps the index tiny (only the global row is indexed).
        modelBuilder.Entity<LlmSettings>().HasIndex(s => s.UserId).IsUnique();
        modelBuilder.Entity<LlmSettings>()
            .HasIndex(s => s.UserId, "IX_LlmSettings_UserId_GlobalUnique")
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter("\"UserId\" IS NULL");

        // ST-43: key/value markers — the Key column is the primary key.
        modelBuilder.Entity<AppMeta>().HasKey(m => m.Key);

        // ST-68: single-row scan progress — Key is the primary key ("library").
        modelBuilder.Entity<MetadataScanProgress>().HasKey(m => m.Key);

        // ST-44: per-user wishlist — list newest first.
        modelBuilder.Entity<WishlistItem>().HasIndex(w => new { w.UserId, w.CreatedAt });

        // ST-46: source files n—1 Song; deleting a song removes its attached files.
        modelBuilder.Entity<SongSourceFile>()
            .HasOne<Song>()
            .WithMany()
            .HasForeignKey(f => f.SongId)
            .OnDelete(DeleteBehavior.Cascade);

        // ST-46: list per-song source files oldest first.
        modelBuilder.Entity<SongSourceFile>().HasIndex(f => new { f.SongId, f.CreatedAt });

        // ST-106: one tag row per (Kind, NormalizedName) — NormalizedName is the lower-cased
        // match key maintained by TagName, so case-insensitive uniqueness needs no provider
        // collation tricks and holds on the InMemory test store too (AHC-7).
        modelBuilder.Entity<Tag>().HasIndex(t => new { t.Kind, t.NormalizedName }).IsUnique();
    }
}
