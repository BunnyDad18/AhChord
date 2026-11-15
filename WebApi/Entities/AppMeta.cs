namespace WebApi.Entities;

/// <summary>
/// ST-43: tiny key/value store for app-level one-shot markers. Currently holds the
/// "seeded" flag so demo content is inserted exactly once per database — re-seeding by
/// title/name match used to resurrect songs/collections/playlists the user had deleted
/// on every app restart (bug report 2026-10-04: "deleted things come back after a reload").
/// </summary>
public class AppMeta
{
    public string Key { get; set; } = "";

    /// <summary>Free-form value (e.g. the UTC timestamp seeding completed).</summary>
    public string Value { get; set; } = "";
}
