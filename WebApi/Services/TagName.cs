using System.Globalization;
using WebApi.Entities;

namespace WebApi.Services;

/// <summary>
/// ST-106: the one normalisation rule for tag names, shared by endpoints, the backfill,
/// and autocomplete. Display name = trimmed input; genres are title-cased per word so
/// pop/Pop/POP are one "Pop" and "pop rock" lands as "Pop Rock". Artists and free tags
/// keep the spelling first entered. The match key (<see cref="Normalized"/>) is the
/// lower-cased display name — the unique index is per (Kind, Normalized).
/// </summary>
public static class TagName
{
    /// <summary>Trimmed display form; genres title-cased. Null/whitespace input → null (no tag).</summary>
    public static string? Display(TagKind kind, string? raw)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return kind == TagKind.Genre
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(trimmed)
            : trimmed;
    }

    /// <summary>Match key for the unique index; null when there is no name.</summary>
    public static string? Normalized(TagKind kind, string? raw) => Display(kind, raw)?.ToLowerInvariant();
}
