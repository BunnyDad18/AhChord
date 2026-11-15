using WebApi.Entities;
using WebApi.Services;

namespace WebApi.Tests;

public class MetadataReviewJobTests
{
    [Fact]
    public void ParseProposals_ValidJson_ExtractsEntries()
    {
        var proposals = MetadataReviewJob.ParseProposals(
            @"{""proposals"": [{""field"": ""genre"", ""value"": ""folk""}, {""field"": ""year"", ""value"": ""1969""}]}");

        Assert.Equal(2, proposals.Count);
        Assert.Equal(("genre", "folk"), proposals[0]);
        Assert.Equal(("year", "1969"), proposals[1]);
    }

    [Fact]
    public void ParseProposals_CodeFencesAndCommentary_Stripped()
    {
        var input = "Here you go:\n```json\n" +
                    @"{""proposals"": [{""field"": ""tempo"", ""value"": ""120""}]}" +
                    "\n```";

        var proposals = MetadataReviewJob.ParseProposals(input);

        Assert.Single(proposals);
        Assert.Equal(("tempo", "120"), proposals[0]);
    }

    [Fact]
    public void ParseProposals_UnknownFieldsAndBadValues_Dropped()
    {
        var input = @"{""proposals"": [
            {""field"": ""bpm"", ""value"": ""120""},
            {""field"": ""year"", ""value"": ""999""},
            {""field"": ""tempo"", ""value"": ""fast""},
            {""field"": ""key"", ""value"": """"}
        ]}";

        var proposals = MetadataReviewJob.ParseProposals(input);

        Assert.Empty(proposals);
    }

    [Fact]
    public void ParseProposals_EmptyListAndGarbage_ReturnsEmpty()
    {
        Assert.Empty(MetadataReviewJob.ParseProposals(@"{""proposals"": []}"));
        Assert.Empty(MetadataReviewJob.ParseProposals("no json here at all"));
        Assert.Empty(MetadataReviewJob.ParseProposals(""));
    }

    [Fact]
    public void ParseProposals_ObjectAndArrayValueNodes_DroppedNotFatal()
    {
        // ST-58: GetString() throws InvalidOperationException on non-string JSON nodes —
        // the LLM sometimes answers {"value": {"genre": "rock"}} or {"value": [1986]}.
        // The job must drop those entries, not crash into Hangfire's retry storm.
        var input = @"{""proposals"": [
            {""field"": ""genre"", ""value"": {""v"": ""rock""}},
            {""field"": ""year"", ""value"": [1986]},
            {""field"": {""f"": ""genre""}, ""value"": ""weird field""},
            {""field"": ""genre"", ""value"": ""rock""}
        ]}";

        var proposals = MetadataReviewJob.ParseProposals(input);

        Assert.Single(proposals);
        Assert.Equal(("genre", "rock"), proposals[0]);
    }

    [Theory]
    [InlineData("year", "1969", "1969")]
    [InlineData("year", "999", null)]       // out of range
    [InlineData("tempo", "120", "120")]
    [InlineData("tempo", "5", null)]        // implausible BPM
    [InlineData("genre", "  folk  ", "folk")]
    [InlineData("album", "  Thriller  ", "Thriller")]   // ST-53: album passes through like other text fields
    public void NormalizeValue_ValidatesPerField(string field, string raw, string? expected)
    {
        Assert.Equal(expected, MetadataReviewJob.NormalizeValue(field, raw));
    }

    // ST-72 (in-app report 2026-11-01 15:50): the church-music family consolidates to one
    // "worship" genre — code, not the prompt, is the source of truth.
    [Theory]
    [InlineData("gospel", "worship")]
    [InlineData("Gospel", "worship")]           // case-insensitive
    [InlineData("hymn", "worship")]
    [InlineData("spiritual", "worship")]
    [InlineData("CCM", "worship")]
    [InlineData("contemporary christian", "worship")]
    [InlineData("Praise & Worship", "worship")]
    [InlineData(" worship ", "worship")]        // already the canonical label
    [InlineData("rock", "rock")]                // everything else passes through…
    [InlineData("Folk Rock", "Folk Rock")]      // …with its case untouched
    [InlineData("gospel blues", "gospel blues")] // only exact family labels consolidate
    public void NormalizeValue_Genre_WorshipFamilyConsolidates(string raw, string expected)
    {
        Assert.Equal(expected, MetadataReviewJob.NormalizeValue("genre", raw));
    }

    [Fact]
    public void ParseProposals_GospelGenre_EndToEndAsWorship()
    {
        var proposals = MetadataReviewJob.ParseProposals(
            @"{""proposals"": [{""field"": ""genre"", ""value"": ""hymn""}]}");

        var p = Assert.Single(proposals);
        Assert.Equal(("genre", "worship"), p);
    }

    [Fact]
    public void TryApply_LegacyGospelProposal_LandsAsWorship()
    {
        // ST-72: proposals made before the mapping apply into the same single bucket.
        var song = new Song { Title = "T", ChordProText = "" };

        Assert.True(MetadataReviewJob.TryApply(song, "genre", "Gospel"));
        Assert.Equal("worship", song.Genre);
    }

    [Fact]
    public void TryApply_ValidValues_SetsFields()
    {
        var song = new Song { Title = "T", ChordProText = "" };

        Assert.True(MetadataReviewJob.TryApply(song, "genre", "folk"));
        Assert.Equal("folk", song.Genre);
        Assert.True(MetadataReviewJob.TryApply(song, "year", "1969"));
        Assert.Equal(1969, song.Year);
        Assert.True(MetadataReviewJob.TryApply(song, "tempo", "120"));
        Assert.Equal(120, song.Tempo);
    }

    [Fact]
    public void TryApply_InvalidValues_Rejected()
    {
        var song = new Song { Title = "T", ChordProText = "" };

        Assert.False(MetadataReviewJob.TryApply(song, "year", "999"));   // out of range
        Assert.Null(song.Year);
        Assert.False(MetadataReviewJob.TryApply(song, "tempo", "5"));    // implausible BPM
        Assert.Null(song.Tempo);
        Assert.False(MetadataReviewJob.TryApply(song, "title", "  "));   // blank title
        Assert.Equal("T", song.Title);
        Assert.True(MetadataReviewJob.TryApply(song, "album", "X"));     // ST-53: album is a valid field now
        Assert.Equal("X", song.Album);
    }
}
