using WebApi.Endpoints;

namespace WebApi.Tests;

/// <summary>ST-40: bug-report marker parsing/writing — the file is hand-editable markdown, so the round-trip must be exact.</summary>
public class BugReportEndpointsTests
{
    private const string Sample = """
        # Bug reports
        ## [2026-10-01 17:46 UTC] James Taylor

        The bug reports don't need the drop down.

        ---
        Page: /settings
        """;

    // ---------- ParseEntries ----------

    [Fact]
    public void Parse_BasicEntry_ExtractsIdTimestampUserText()
    {
        var entries = BugReportEndpoints.ParseEntries(Sample);

        var e = Assert.Single(entries);
        Assert.Equal("bug-1", e.id);
        Assert.Equal("2026-10-01 17:46 UTC", e.timestamp);
        Assert.Equal("James Taylor", e.user);
        Assert.Contains("don't need the drop down", e.text);
        Assert.Equal("new", e.status);
    }

    [Fact]
    public void Parse_StatusMarker_ResolvedInVersion()
    {
        var content = Sample + "\n<!-- status: resolved in 0.3.1 -->\n";
        var e = Assert.Single(BugReportEndpoints.ParseEntries(content));

        Assert.Equal("resolved", e.status);
        Assert.Equal("0.3.1", e.resolvedIn);
    }

    [Fact]
    public void Parse_LegacyResolvedMarker_StillParses()
    {
        var content = Sample + "\n<!-- resolved -->\n";
        var e = Assert.Single(BugReportEndpoints.ParseEntries(content));

        Assert.Equal("resolved", e.status);
        Assert.Null(e.resolvedIn);
    }

    [Fact]
    public void Parse_WaitingMarker()
    {
        var content = Sample + "\n<!-- status: waiting -->\n";
        var e = Assert.Single(BugReportEndpoints.ParseEntries(content));

        Assert.Equal("waiting", e.status);
        Assert.Null(e.resolvedIn);
    }

    [Fact]
    public void Parse_TitleMarker_IsExposedAndKeptOutOfText()
    {
        var content = Sample + "\n<!-- title: Reopen button -->\n<!-- status: resolved in 0.3.1 -->\n";
        var e = Assert.Single(BugReportEndpoints.ParseEntries(content));

        Assert.Equal("Reopen button", e.title);
        Assert.DoesNotContain("title:", e.text);
    }

    [Fact]
    public void Parse_MultipleEntries_AreOneBasedAndOrdered()
    {
        var content = Sample + "\n## [2026-10-01 18:00 UTC] Agent\n\nSecond report.\n";
        var entries = BugReportEndpoints.ParseEntries(content);

        Assert.Equal(2, entries.Count);
        Assert.Equal("bug-1", entries[0].id);
        Assert.Equal("bug-2", entries[1].id);
    }

    // ---------- SetFields ----------

    [Fact]
    public void Set_StatusAndTitle_WritesBothMarkers()
    {
        var (found, updated) = BugReportEndpoints.SetFields(Sample, 1, "resolved", "0.3.1", "Reopen button");

        Assert.True(found);
        Assert.NotNull(updated);
        var e = Assert.Single(BugReportEndpoints.ParseEntries(updated!));
        Assert.Equal("resolved", e.status);
        Assert.Equal("0.3.1", e.resolvedIn);
        Assert.Equal("Reopen button", e.title);
    }

    [Fact]
    public void Set_StatusOnly_PreservesExistingTitle()
    {
        var withTitle = BugReportEndpoints.SetFields(Sample, 1, null, null, "Keep me")!.Updated!;
        var (found, updated) = BugReportEndpoints.SetFields(withTitle, 1, "resolved", "0.3.1", title: null);

        Assert.True(found);
        var e = Assert.Single(BugReportEndpoints.ParseEntries(updated!));
        Assert.Equal("resolved", e.status);
        Assert.Equal("Keep me", e.title);
    }

    [Fact]
    public void Set_TitleOnly_PreservesResolvedInVersion()
    {
        var resolved = BugReportEndpoints.SetFields(Sample, 1, "resolved", "0.3.0", title: null)!.Updated!;
        var (found, updated) = BugReportEndpoints.SetFields(resolved, 1, status: null, resolvedIn: null, title: "New title");

        Assert.True(found);
        var e = Assert.Single(BugReportEndpoints.ParseEntries(updated!));
        Assert.Equal("resolved", e.status);
        Assert.Equal("0.3.0", e.resolvedIn); // not wiped by a title-only update
        Assert.Equal("New title", e.title);
    }

    [Fact]
    public void Set_EmptyTitle_ClearsIt()
    {
        var withTitle = BugReportEndpoints.SetFields(Sample, 1, null, null, "To be cleared")!.Updated!;
        var (found, updated) = BugReportEndpoints.SetFields(withTitle, 1, status: null, resolvedIn: null, title: "");

        Assert.True(found);
        var e = Assert.Single(BugReportEndpoints.ParseEntries(updated!));
        Assert.Null(e.title);
    }

    [Fact]
    public void Set_SameValues_NoChange()
    {
        var resolved = BugReportEndpoints.SetFields(Sample, 1, "resolved", "0.3.0", title: null)!.Updated!;
        var (found, updated) = BugReportEndpoints.SetFields(resolved, 1, "resolved", "0.3.0", title: null);

        Assert.True(found); // the entry exists — a no-op is not a 404
        Assert.Null(updated); // nothing to write
    }

    [Fact]
    public void Set_OutOfRange_NotFound()
    {
        var (found, updated) = BugReportEndpoints.SetFields(Sample, 2, "resolved", "0.3.1", null);

        Assert.False(found);
        Assert.Null(updated);
    }

    [Fact]
    public void Set_TitleWithClosingSequence_IsSanitized()
    {
        var (found, updated) = BugReportEndpoints.SetFields(Sample, 1, null, null, "bad --> title");

        Assert.True(found);
        // The marker must stay on one line and parse back cleanly.
        var e = Assert.Single(BugReportEndpoints.ParseEntries(updated!));
        Assert.Equal("bad  title", e.title);
    }

    [Fact]
    public void Set_MultiLineTitle_IsFlattenedToOneLine()
    {
        var (found, updated) = BugReportEndpoints.SetFields(Sample, 1, null, null, "line one\nline two");

        Assert.True(found);
        var e = Assert.Single(BugReportEndpoints.ParseEntries(updated!));
        Assert.Equal("line one line two", e.title);
    }

    [Fact]
    public void Set_StatusNew_RemovesMarker()
    {
        var resolved = BugReportEndpoints.SetFields(Sample, 1, "resolved", "0.3.0", title: null)!.Updated!;
        var (found, updated) = BugReportEndpoints.SetFields(resolved, 1, "new", null, title: null);

        Assert.True(found);
        var e = Assert.Single(BugReportEndpoints.ParseEntries(updated!));
        Assert.Equal("new", e.status);
        Assert.Null(e.resolvedIn);
    }

    [Fact]
    public void RoundTrip_SetThenParse_IsStable()
    {
        var (found, updated) = BugReportEndpoints.SetFields(Sample, 1, "resolved", "0.3.1", "Title here");
        Assert.True(found);

        var e = Assert.Single(BugReportEndpoints.ParseEntries(updated!));
        Assert.Equal("resolved", e.status);
        Assert.Equal("0.3.1", e.resolvedIn);
        Assert.Equal("Title here", e.title);

        // Setting the same values again is a no-op (idempotent).
        var (_, second) = BugReportEndpoints.SetFields(updated!, 1, "resolved", "0.3.1", "Title here");
        Assert.Null(second);
    }

    // ---------- ST-63: sanitization + stable ids ----------

    [Fact]
    public void SanitizeMessage_EntryBoundaryLines_AreNeutralized()
    {
        // A report that starts a line with "##" used to create a phantom entry — shifting every
        // later positional id, so "resolve #7" edited the wrong report.
        var s = BugReportEndpoints.SanitizeMessage("line one\n## [2030-01-01 00:00 UTC] Attacker\n##nested\nplain");

        Assert.Equal("line one\n ## [2030-01-01 00:00 UTC] Attacker\n ##nested\nplain", s);
    }

    [Fact]
    public void SanitizeMessage_MarkerOpens_AreNeutralized()
    {
        // Embedded "<!-- status: ... -->" used to self-resolve the report (it parsed as a marker).
        var s = BugReportEndpoints.SanitizeMessage("fix me <!-- status: resolved in 9.9.9 --> thanks\n<!-- id: forged -->");

        Assert.DoesNotContain("<!--", s);
        Assert.Contains("< !-- status: resolved in 9.9.9 -->", s);
        Assert.Contains("< !-- id: forged -->", s);
    }

    [Fact]
    public void Append_Sanitized_RoundTripsAsLiteralText()
    {
        // Simulates the POST append: header + app-written id marker + sanitized message.
        var message = BugReportEndpoints.SanitizeMessage(
            "## [2030-01-01 00:00 UTC] Attacker\nself-resolving <!-- status: resolved in 9.9.9 -->");
        var content = "# Bug reports\n## [2026-11-01 12:00 UTC] James\n\n<!-- id:deadbeef -->\n\n" + message + "\n";

        var e = Assert.Single(BugReportEndpoints.ParseEntries(content));

        Assert.Equal("deadbeef", e.id);           // app marker wins over positional
        Assert.Equal("new", e.status);            // the embedded marker did NOT self-resolve
        Assert.Contains("## [2030-01-01 00:00 UTC] Attacker", e.text); // no phantom entry: it is text
        Assert.Contains("< !-- status: resolved in 9.9.9 -->", e.text);
    }

    [Fact]
    public void StableIds_ArePositionalOnlyForLegacy()
    {
        var content = """
            # Bug reports
            ## [2026-11-01 10:00 UTC] James

            <!-- id:cafe01 -->

            First (app-written)

            ## [2026-11-01 11:00 UTC] James

            Old (no marker)
            """;

        var entries = BugReportEndpoints.ParseEntries(content);
        Assert.Equal("cafe01", entries[0].id);  // stable id from the app marker
        Assert.Equal("bug-2", entries[1].id);   // pre-ST-63 entry keeps its positional id
    }

    [Fact]
    public void ResolveEntryIndex_GuidGuidPositional_AndMisses()
    {
        var content = """
            # Bug reports
            ## [2026-11-01 10:00 UTC] James

            <!-- id:cafe01 -->

            First

            ## [2026-11-01 11:00 UTC] James

            Second
            """;

        Assert.Equal(1, BugReportEndpoints.ResolveEntryIndex("cafe01", content));
        Assert.Equal(2, BugReportEndpoints.ResolveEntryIndex("bug-2", content));
        // Legacy positional addressing still works for app-written entries (position, not name).
        Assert.Equal(1, BugReportEndpoints.ResolveEntryIndex("bug-1", content));
        Assert.Equal(0, BugReportEndpoints.ResolveEntryIndex("bug-99", content));
        Assert.Equal(0, BugReportEndpoints.ResolveEntryIndex("nope", content));
    }

    [Fact]
    public void SetFields_ViaResolvedIndex_KeepsIdsStable()
    {
        var content = """
            # Bug reports
            ## [2026-11-01 10:00 UTC] James

            <!-- id:cafe01 -->

            First

            ## [2026-11-01 11:00 UTC] James

            Second
            """;

        var index = BugReportEndpoints.ResolveEntryIndex("cafe01", content);
        var (found, updated) = BugReportEndpoints.SetFields(content, index, "resolved", "0.4.0", null);

        Assert.True(found);
        var entries = BugReportEndpoints.ParseEntries(updated!);
        Assert.Equal(2, entries.Count);
        Assert.Equal("cafe01", entries[0].id);   // id unchanged by the write
        Assert.Equal("resolved", entries[0].status);
        Assert.Equal("0.4.0", entries[0].resolvedIn);
        Assert.Equal("new", entries[1].status);  // neighbor untouched
    }
}
