using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Toki.Llm;
using Toki.Storage;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// PROVENANCE: THE HALF THAT WAS BUILT AND NEVER CONNECTED.
///
/// ValidateDigest has been returning verified, narrowed transcript ranges for a while, and the
/// action_items table has carried src_first/src_last columns for longer than that. Nothing ever
/// assigned them. Every evidence link in the product was therefore recovered afterwards by matching
/// bullet text against the WHOLE transcript, and the gain from searching inside a recorded window
/// was never actually in the product. Re-measured 2026-09-03 (--linktest 300, 16 real meetings):
/// 85.6% to 88.5% precision-at-1 on the HARD condition, McNemar 4.17, significant at p<0.05; on the
/// EASY condition the difference is noise.
///
/// These tests pin the two joins that were missing: the writer attributing a finished bullet to the
/// digest window it was rewritten from, and ReplaceNotes persisting what Save already persisted.
///
/// MUTATION PROBES: every test here was run against the unfixed code first. Before the fix,
/// Attributes_a_bullet_to_the_window_it_came_from and the two round-trip tests fail; the silence
/// tests pass trivially (nothing was ever assigned, so nothing could be wrong) and exist to stop a
/// later tuning pass from buying recall by inventing links.
/// </summary>
public sealed class ProvenanceWiringTests : IDisposable
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"toki-prov-{Guid.NewGuid():N}.db");

    // Two windows with deliberately disjoint vocabulary, so an attribution can only come from
    // content overlap and never from luck.
    static List<LlmNoteWriter.Window> Windows() =>
    [
        new("Pricing was raised to nineteen pounds for the annual subscription tier", 0, 4),
        new("The onboarding tutorial video needs re-recording before the launch", 5, 9),
    ];

    static NoteSet NoteWith(params string[] bullets) => new()
    {
        Sections = [new NoteSection { Heading = "Notes", Bullets = [.. bullets] }],
    };

    [Fact]
    public void Attributes_a_bullet_to_the_window_it_came_from()
    {
        var note = NoteWith("Pricing raised to nineteen pounds annual");
        LlmNoteWriter.AttachProvenance(note, Windows());

        var span = note.Sections[0].SourceFor(0);
        Assert.NotNull(span);
        Assert.Equal(0, span!.First);
        Assert.Equal(4, span.Last);
    }

    [Fact]
    public void Attributes_the_second_window_when_that_is_the_source()
    {
        var note = NoteWith("Re-record the onboarding tutorial video before launch");
        LlmNoteWriter.AttachProvenance(note, Windows());

        var span = note.Sections[0].SourceFor(0);
        Assert.NotNull(span);
        Assert.Equal(5, span!.First);
        Assert.Equal(9, span.Last);
    }

    [Fact]
    public void Sources_stay_index_aligned_with_bullets()
    {
        // The middle bullet matches nothing. Its slot must still exist, or every source after it
        // shifts left and points the reader at the wrong moment - a silent, plausible-looking fault.
        var note = NoteWith(
            "Pricing raised to nineteen pounds annual",
            "Xylophone quokka zeppelin",
            "Re-record the onboarding tutorial video before launch");
        LlmNoteWriter.AttachProvenance(note, Windows());

        var s = note.Sections[0];
        Assert.Equal(3, s.Sources!.Count);
        Assert.Equal(0, s.SourceFor(0)!.First);
        Assert.Null(s.SourceFor(1));
        Assert.Equal(5, s.SourceFor(2)!.First);
    }

    [Fact]
    public void A_bullet_two_windows_explain_equally_gets_no_source()
    {
        // Both windows are about the same thing. Picking one would be a coin flip presented as a
        // fact, and CLAUDE.md's rule is that a wrong evidence link is worse than none.
        var tied = new List<LlmNoteWriter.Window>
        {
            new("Budget approval deadline moved to Friday", 0, 3),
            new("Budget approval deadline moved to Friday", 8, 11),
        };
        var note = NoteWith("Budget approval deadline moved to Friday");
        LlmNoteWriter.AttachProvenance(note, tied);

        Assert.Null(note.Sections[0].SourceFor(0));
    }

    [Fact]
    public void A_bullet_too_generic_to_attribute_gets_no_source()
    {
        var note = NoteWith("It was");
        LlmNoteWriter.AttachProvenance(note, Windows());
        Assert.Null(note.Sections[0].Sources);
    }

    [Fact]
    public void A_section_that_matched_nothing_stores_no_list_at_all()
    {
        // A list of nothing but nulls says the same as no list and only bloats the stored JSON.
        var note = NoteWith("Xylophone quokka zeppelin", "Marzipan bicycle thunder");
        LlmNoteWriter.AttachProvenance(note, Windows());
        Assert.Null(note.Sections[0].Sources);
    }

    [Fact]
    public void No_windows_means_no_attribution_attempted()
    {
        var note = NoteWith("Pricing raised to nineteen pounds annual");
        LlmNoteWriter.AttachProvenance(note, []);
        Assert.Null(note.Sections[0].Sources);
    }

    [Fact]
    public void Action_items_are_attributed_too()
    {
        var note = new NoteSet
        {
            Actions = [new ActionItem { Text = "Re-record the onboarding tutorial video" }],
        };
        LlmNoteWriter.AttachProvenance(note, Windows());

        Assert.NotNull(note.Actions[0].Source);
        Assert.Equal(5, note.Actions[0].Source!.First);
    }

    [Fact]
    public void An_existing_action_source_is_never_overwritten()
    {
        // A span set upstream was OBSERVED; this one is inferred. Observation wins.
        var note = new NoteSet
        {
            Actions =
            [
                new ActionItem
                {
                    Text = "Re-record the onboarding tutorial video",
                    Source = new SourceSpan { First = 41, Last = 42 },
                },
            ],
        };
        LlmNoteWriter.AttachProvenance(note, Windows());

        Assert.Equal(41, note.Actions[0].Source!.First);
    }

    [Fact]
    public void ReplaceNotes_persists_action_provenance()
    {
        // THE BUG THIS EXISTS FOR: ReplaceNotes' INSERT omitted src_first/src_last entirely, so
        // regenerating a note silently stripped provenance off every action item.
        var store = new MeetingStore(_dbPath);
        var id = store.Save(new MeetingRecord
        {
            Title = "Regen test",
            StartedAt = DateTimeOffset.Now,
            Notes = new NoteSet { Tldr = "first" },
        });

        store.ReplaceNotes(id, new NoteSet
        {
            Tldr = "second",
            Actions =
            [
                new ActionItem { Text = "Send the deck", Source = new SourceSpan { First = 7, Last = 9 } },
            ],
        });

        var back = store.Get(id);
        Assert.Single(back!.Notes.Actions);
        Assert.NotNull(back.Notes.Actions[0].Source);
        Assert.Equal(7, back.Notes.Actions[0].Source!.First);
        Assert.Equal(9, back.Notes.Actions[0].Source!.Last);
    }

    [Fact]
    public void ReplaceNotes_persists_section_provenance()
    {
        var store = new MeetingStore(_dbPath);
        var id = store.Save(new MeetingRecord
        {
            Title = "Regen sections",
            StartedAt = DateTimeOffset.Now,
            Notes = new NoteSet { Tldr = "first" },
        });

        store.ReplaceNotes(id, new NoteSet
        {
            Tldr = "second",
            Sections =
            [
                new NoteSection
                {
                    Heading = "Notes",
                    Bullets = ["Pricing raised"],
                    Sources = [new SourceSpan { First = 2, Last = 3 }],
                },
            ],
        });

        var back = store.Get(id);
        var span = back!.Notes.Sections[0].SourceFor(0);
        Assert.NotNull(span);
        Assert.Equal(2, span!.First);
        Assert.Equal(3, span.Last);
    }

    [Fact]
    public void An_action_with_no_source_round_trips_as_null()
    {
        // Absence must survive too: a null source is "not recorded", and writing 0 would send the
        // evidence link to the first line of the meeting with full confidence.
        var store = new MeetingStore(_dbPath);
        var id = store.Save(new MeetingRecord
        {
            Title = "No provenance",
            StartedAt = DateTimeOffset.Now,
            Notes = new NoteSet { Tldr = "x" },
        });

        store.ReplaceNotes(id, new NoteSet
        {
            Tldr = "x",
            Actions = [new ActionItem { Text = "Something nobody attributed" }],
        });

        Assert.Null(store.Get(id)!.Notes.Actions[0].Source);
    }

    /// <summary>
    /// THE WIRING ITSELF, through the real WriteAsync.
    ///
    /// Every test above exercises AttachProvenance directly, which would keep passing if the call
    /// site were deleted. This one drives the whole map-reduce with a scripted model and asserts the
    /// finished note carries sources - the thing that was actually missing for weeks.
    ///
    /// The model echoes the first lines of each window straight back as a cited digest, so the
    /// digest is verifiable by construction and the test measures the PLUMBING rather than a
    /// model's behaviour. Every segment uses vocabulary unique to itself, so a bullet can only match
    /// the window it truly came from and a false attribution cannot pass by luck.
    /// </summary>
    [Fact]
    public async Task WriteAsync_records_provenance_on_the_finished_note()
    {
        var transcript = LongTranscript(120);
        var llm = new EchoingLlm();

        var notes = await new LlmNoteWriter(llm).WriteAsync(transcript);

        Assert.True(llm.Windows >= 2, $"needed a map-reduce run to have provenance at all, got {llm.Windows} window(s)");
        var attributed = notes.Sections
            .SelectMany(s => s.Bullets.Select((_, i) => s.SourceFor(i)))
            .Where(x => x is not null)
            .ToList();
        Assert.NotEmpty(attributed);
        // Distinct windows, not one span stamped onto everything.
        Assert.True(attributed.Select(a => a!.First).Distinct().Count() > 1,
            "every bullet was attributed to the same window, which means the match is not discriminating");
        foreach (var span in attributed)
        {
            Assert.InRange(span!.First, 0, transcript.Count - 1);
            Assert.InRange(span.Last, span.First, transcript.Count - 1);
        }
    }

    /// <summary>A transcript long enough to force several map windows, where every segment's words
    /// are unique to it, so attribution cannot succeed by shared boilerplate.</summary>
    static List<TranscriptSegment> LongTranscript(int count)
    {
        var list = new List<TranscriptSegment>(count);
        for (var i = 0; i < count; i++)
        {
            var w = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel",
                            "india", "juliet", "kilo", "lima", "mike", "november", "oscar", "papa" };
            var text = string.Join(" ", w.Select(x => $"{x}{i}"));
            list.Add(new TranscriptSegment(i % 2 == 0 ? Speaker.You : Speaker.Them,
                i * 5000, i * 5000 + 4000, text));
        }
        return list;
    }

    /// <summary>A model that answers the map step by echoing the window's own first lines back with
    /// their real line numbers, so the citation always verifies, then writes a final note whose
    /// bullets are those echoed lines.</summary>
    sealed class EchoingLlm : ILlm
    {
        readonly List<string> _digests = [];
        public int Windows { get; private set; }
        public bool Ready => true;

        public Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct = default)
            => Task.FromResult(Reply(user));

        public async IAsyncEnumerable<string> StreamAsync(string system, string user, int maxTokens,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
            IReadOnlyList<string>? stop = null)
        { await Task.Yield(); yield return Reply(user); }

        string Reply(string user)
        {
            const string marker = "Transcript part:";
            if (user.Contains(marker))
            {
                Windows++;
                var body = user[(user.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
                var outLines = new List<string>();
                foreach (var raw in body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(3))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(raw, @"^\s*\[(\d+)\]\s*[^:]*:\s*(.+)$");
                    if (!m.Success) continue;
                    var text = m.Groups[2].Value.Trim();
                    outLines.Add($"[{m.Groups[1].Value}] {text}");
                    _digests.Add(text);
                }
                return string.Join("\n", outLines);
            }
            if (user.Contains("Write a title")) return "Provenance run";
            if (user.Contains("KEY POINTS"))
            {
                // One bullet per window: the first digest line each window produced.
                var picks = _digests.Where((_, i) => i % 3 == 0).Take(4).ToList();
                return "KEY POINTS\n" + string.Join("\n", picks.Select(p => "- " + p))
                     + "\n\nACTION ITEMS\n\nSUMMARY\nA run to check provenance plumbing.";
            }
            return "NONE";
        }
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* temp file */ }
    }
}
