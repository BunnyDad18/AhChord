namespace WebApi.Dtos;

/// <summary>List-level collection shape (with a live <c>playlistCount</c>).</summary>
public record CollectionDto(
    Guid Id,
    string Name,
    string? Description,
    string Category,
    int PlaylistCount,
    DateTime CreatedAt);

/// <summary>Full collection including its playlists ordered by name.</summary>
public record CollectionDetailDto(
    Guid Id,
    string Name,
    string? Description,
    string Category,
    int PlaylistCount,
    DateTime CreatedAt,
    List<PlaylistDto> Playlists);

/// <summary>POST /api/collections. <c>name</c> required; <c>category</c> defaults to "Other".</summary>
public record CreateCollectionRequest(
    string? Name,
    string? Description,
    string? Category);

/// <summary>
/// PUT /api/collections/{id}. All fields optional: present (non-null) values replace the stored
/// values, absent/null fields are left unchanged (same semantics as the song/playlist PUTs).
/// </summary>
public record UpdateCollectionRequest(
    string? Name,
    string? Description,
    string? Category);
