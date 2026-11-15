using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Data;
using WebApi.Identity;
using WebApi.Services;

namespace WebApi.Tests;

/// <summary>
/// ST-61: authz hardening — admin gates read the DATABASE (the is_admin claim is frozen into
/// a 30-day token), ?access_token= is hub-only, and registration never self-grants admin
/// (the DbSeeder promotion pass is the only granting path).
/// </summary>
public class AuthzTests
{
    private static ApplicationDbContext NewDb(string name)
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    private static HttpContext RequestFor(string? sub, ApplicationDbContext db)
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(sub is null ? [] : [new Claim("sub", sub)], authenticationType: "test"));
        var http = new DefaultHttpContext { User = principal };
        http.RequestServices = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        return http;
    }

    // ---------- IsAdminAsync (DB-backed, demotion takes effect immediately) ----------

    [Fact]
    public async Task IsAdminAsync_AdminRow_True()
    {
        using var db = NewDb(nameof(IsAdminAsync_AdminRow_True));
        db.Users.Add(new ApplicationUser { Id = "u1", UserName = "u1", IsAdmin = true });
        await db.SaveChangesAsync();

        Assert.True(await Authz.IsAdminAsync(RequestFor("u1", db)));
    }

    [Fact]
    public async Task IsAdminAsync_DemotedInDb_FalseImmediately()
    {
        // The bug: a token minted while admin carried is_admin=true for 30 days.
        using var db = NewDb(nameof(IsAdminAsync_DemotedInDb_FalseImmediately));
        db.Users.Add(new ApplicationUser { Id = "u1", UserName = "u1", IsAdmin = true });
        await db.SaveChangesAsync();
        Assert.True(await Authz.IsAdminAsync(RequestFor("u1", db)));

        var user = await db.Users.SingleAsync(u => u.Id == "u1");
        user.IsAdmin = false;
        await db.SaveChangesAsync();

        Assert.False(await Authz.IsAdminAsync(RequestFor("u1", db)));
    }

    [Fact]
    public async Task IsAdminAsync_UnknownUserOrNoClaim_False()
    {
        using var db = NewDb(nameof(IsAdminAsync_UnknownUserOrNoClaim_False));
        Assert.False(await Authz.IsAdminAsync(RequestFor("ghost", db)));
        Assert.False(await Authz.IsAdminAsync(RequestFor(null, db)));
    }

    [Fact]
    public async Task IsAdminAsync_NoDbContextRegistered_False()
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u1")], "test")),
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
        Assert.False(await Authz.IsAdminAsync(http));
    }

    // ---------- AllowsQueryStringToken (SignalR fallback is hub-only) ----------

    [Theory]
    [InlineData("/hubs/setlist", true)]
    [InlineData("/hubs", true)]
    [InlineData("/Hubs/Setlist/Negotiate", true)]   // PathString comparison is case-insensitive
    [InlineData("/api/songs", false)]
    [InlineData("/hangfire", false)]
    [InlineData("/hubx", false)]                    // prefix must be a whole segment
    [InlineData("/", false)]
    [InlineData(null, false)]
    public void AllowsQueryStringToken_PathGated(string? path, bool expected)
        => Assert.Equal(expected, Authz.AllowsQueryStringToken(path));

    // ---------- Registration never self-grants; DbSeeder owns promotion ----------

    [Fact]
    public void SeedFirstUserIsAdmin_NoAdmin_PromotesOldest()
    {
        using var db = NewDb(nameof(SeedFirstUserIsAdmin_NoAdmin_PromotesOldest));
        var older = new ApplicationUser { Id = "u1", UserName = "u1", IsAdmin = false, CreatedAt = DateTime.UtcNow.AddDays(-1) };
        var newer = new ApplicationUser { Id = "u2", UserName = "u2", IsAdmin = false, CreatedAt = DateTime.UtcNow };
        db.Users.AddRange(older, newer); // two concurrent first registrations: NOBODY self-granted
        db.SaveChanges();

        DbSeeder.SeedFirstUserIsAdmin(db);

        Assert.True(db.Users.Single(u => u.Id == "u1").IsAdmin);
        Assert.False(db.Users.Single(u => u.Id == "u2").IsAdmin);

        // Idempotent: a second pass (restart) keeps exactly one admin.
        DbSeeder.SeedFirstUserIsAdmin(db);
        Assert.Equal(1, db.Users.Count(u => u.IsAdmin));
    }

    [Fact]
    public void SeedFirstUserIsAdmin_AdminExists_NoChange()
    {
        using var db = NewDb(nameof(SeedFirstUserIsAdmin_AdminExists_NoChange));
        db.Users.Add(new ApplicationUser { Id = "u1", UserName = "u1", IsAdmin = true });
        db.SaveChanges();

        DbSeeder.SeedFirstUserIsAdmin(db);

        Assert.True(db.Users.Single(u => u.Id == "u1").IsAdmin);
        Assert.Equal(1, db.Users.Count(u => u.IsAdmin));
    }
}
