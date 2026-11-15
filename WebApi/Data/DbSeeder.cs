using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebApi.Identity;

namespace WebApi.Data;

/// <summary>
/// Startup seeding — accounts only, and safe to run on every startup. Admin promotion, the
/// config-seeded admin and the test user are all idempotent.
///
/// There is no demo content. A fresh database starts EMPTY: no songs, no collection, no
/// setlist. ChordPro lyrics are copyrighted works, and this repo is published under MIT, so
/// bundling well-known charts as seed data is a takedown target that the licence does not
/// cover (ST-91 — it shipped for two years as "five songs so a fresh install has something to
/// play"). The empty library is a supported state: the Songs page says "No songs yet." and the
/// import flow is the way to fill it. Existing installs keep their rows untouched; the old
/// AppMeta "seeded" sentinel row is simply no longer written.
/// </summary>
public static class DbSeeder
{
    public static void Seed(ApplicationDbContext db) => SeedFirstUserIsAdmin(db);

    /// <summary>
    /// ST-35 A3: optional fixed test account for unattended agent testing. Only created when a
    /// password is configured (TEST_USER_PASSWORD); deployments that leave it unset get no extra user.
    /// Idempotent — skipped if the username or email already exists. Uses the DI-registered
    /// UserManager so it rides on whatever store implementation the framework provides
    /// (.NET 10 removed the concrete UserStore&lt;T&gt;, so we don't construct one directly).
    /// </summary>
    public static async Task SeedTestUserAsync(
        UserManager<ApplicationUser> userManager, string username, string email, string password)
    {
        if (await userManager.FindByNameAsync(username) is not null
            || await userManager.FindByEmailAsync(email) is not null)
        {
            return;
        }

        var user = new ApplicationUser
        {
            UserName = username,
            Email = email,
            EmailConfirmed = true, // no email flow by design (same as registration)
            DisplayName = "Agent",
            CreatedAt = DateTime.UtcNow
        };
        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Test user seeding failed: " + string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }

    /// <summary>ST-30: pre-existing installs have no admin — promote the oldest user (idempotent).
    /// ST-61: this is the ONLY admin-granting path (registration never self-grants — closes the
    /// first-registration race). Public for the authz tests.</summary>
    public static void SeedFirstUserIsAdmin(ApplicationDbContext db)
    {
        if (db.Users.Any(u => u.IsAdmin))
            return;
        var oldest = db.Users.OrderBy(u => u.CreatedAt).ThenBy(u => u.Id).FirstOrDefault();
        if (oldest is not null)
        {
            oldest.IsAdmin = true;
            db.SaveChanges();
        }
    }

    /// <summary>
    /// Public-demo gate 1: seed an admin account from config (SEED_ADMIN_USERNAME /
    /// SEED_ADMIN_PASSWORD / SEED_ADMIN_EMAIL), so an instance that closes registration is not
    /// a locked door. The promotion rule (<see cref="SeedFirstUserIsAdmin"/>) promotes the
    /// oldest user at the next restart, which on a fresh internet-facing database means "whoever
    /// registers first" — with registration closed and no seeded admin, nobody gets in at all.
    ///
    /// Idempotent: nothing happens once any admin exists, and an existing username is promoted
    /// rather than duplicated. Returns a line for the log, or null when there was nothing to do.
    /// </summary>
    public static async Task<string?> SeedAdminAsync(
        UserManager<ApplicationUser> userManager, string username, string? email, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return null;

        if (await userManager.Users.AnyAsync(u => u.IsAdmin))
            return null;

        var normalized = username.Trim();

        var existing = await userManager.FindByNameAsync(normalized);
        if (existing is not null)
        {
            existing.IsAdmin = true;
            var promoted = await userManager.UpdateAsync(existing);
            return promoted.Succeeded
                ? $"Seeded admin: promoted the existing account \"{normalized}\" (no admin existed)."
                : $"Seeded admin failed for \"{normalized}\": " + string.Join("; ", promoted.Errors.Select(e => e.Description));
        }

        var user = new ApplicationUser
        {
            UserName = normalized,
            Email = string.IsNullOrWhiteSpace(email) ? $"{normalized}@ahchord.local" : email.Trim(),
            EmailConfirmed = true, // no email flow by design (same as registration)
            DisplayName = "Admin",
            IsAdmin = true,
            CreatedAt = DateTime.UtcNow
        };
        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
            return "Seeded admin failed: " + string.Join("; ", created.Errors.Select(e => e.Description));

        return $"Seeded admin account \"{normalized}\" (SEED_ADMIN_USERNAME).";
    }
}
