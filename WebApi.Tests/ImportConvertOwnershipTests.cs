using WebApi.Endpoints;
using WebApi.Entities;

namespace WebApi.Tests;

/// <summary>
/// ST-56: the /api/import/convert/{id} routes (GET/DELETE/retry/save) must be owner-only —
/// they used to load drafts by id alone, letting any authenticated band member read,
/// delete, re-run, and save other users' conversion drafts.
/// </summary>
public class ImportConvertOwnershipTests
{
    [Fact]
    public void Owner_matches_own_draft()
    {
        var draft = new ImportDraft { UserId = "user-a" };
        Assert.True(ImportConvertEndpoints.IsOwner(draft, "user-a"));
    }

    [Fact]
    public void Foreign_user_does_not_own_the_draft()
    {
        var draft = new ImportDraft { UserId = "user-a" };
        Assert.False(ImportConvertEndpoints.IsOwner(draft, "user-b"));
    }

    [Fact]
    public void Anonymous_caller_owns_nothing()
    {
        var draft = new ImportDraft { UserId = "user-a" };
        Assert.False(ImportConvertEndpoints.IsOwner(draft, null));
    }

    [Fact]
    public void Draft_without_a_user_is_nobodys()
    {
        // Consistent with the list endpoint: rows with null UserId are never returned.
        var draft = new ImportDraft { UserId = null };
        Assert.False(ImportConvertEndpoints.IsOwner(draft, "user-a"));
        Assert.False(ImportConvertEndpoints.IsOwner(draft, null));
    }

    [Fact]
    public void Ownership_is_ordinal_not_case_insensitive()
    {
        var draft = new ImportDraft { UserId = "User-A" };
        Assert.False(ImportConvertEndpoints.IsOwner(draft, "user-a"));
    }
}
