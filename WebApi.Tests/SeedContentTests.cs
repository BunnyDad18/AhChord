using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Identity;

namespace WebApi.Tests;

/// <summary>
/// ST-91: the demo seed is gone. ChordPro lyrics are copyrighted works and the repo is MIT, so
/// a fresh database starts EMPTY — no songs, no collection, no setlist, no "seeded" sentinel.
/// Accounts are the only thing startup writes (promotion, the config-seeded admin, the test
/// user). Existing installs keep their rows; nothing here deletes user data.
/// </summary>
public class SeedContentTests
{
    private static ApplicationDbContext NewDb(string name)
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    [Fact]
    public void Seed_On_A_Fresh_Database_Writes_Nothing()
    {
        using var db = NewDb(nameof(Seed_On_A_Fresh_Database_Writes_Nothing));

        DbSeeder.Seed(db);

        Assert.Empty(db.Users);
        Assert.Empty(db.Songs);
        Assert.Empty(db.Collections);
        Assert.Empty(db.Playlists);
        Assert.DoesNotContain(db.AppMeta, m => m.Key == "seeded");
    }

    [Fact]
    public void Seed_Promotes_The_Oldest_User_And_Writes_No_Content()
    {
        using var db = NewDb(nameof(Seed_Promotes_The_Oldest_User_And_Writes_No_Content));
        db.Users.Add(new ApplicationUser { Id = "u1", UserName = "u1", CreatedAt = DateTime.UtcNow.AddDays(-2) });
        db.Users.Add(new ApplicationUser { Id = "u2", UserName = "u2", CreatedAt = DateTime.UtcNow.AddDays(-1) });
        db.SaveChanges();

        DbSeeder.Seed(db);

        Assert.True(db.Users.Single(u => u.Id == "u1").IsAdmin);
        Assert.False(db.Users.Single(u => u.Id == "u2").IsAdmin);
        Assert.Empty(db.Songs);
        Assert.Empty(db.Playlists);
    }

    [Fact]
    public void Seed_Runs_Idempotently_On_Every_Startup()
    {
        using var db = NewDb(nameof(Seed_Runs_Idempotently_On_Every_Startup));

        DbSeeder.Seed(db);
        DbSeeder.Seed(db);

        Assert.Empty(db.Songs);
        Assert.Empty(db.AppMeta);
    }
}
