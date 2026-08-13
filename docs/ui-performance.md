# Why the Library felt slow, and what virtualization would change

*2026-08-11/12. Owner: "the overall UI rendering has been very slow and clunky."*

Everything here is MEASURED on the owner's PC against the real database (54 meetings), via the
`--uitest` self-test, not estimated. Run it any time:

```
Toki.exe --uitest
```

---

> **DONE 2026-08-12.** The list is virtualized (`Views/MeetingList.cs`) and drag-reorder was removed at
> the owner's request ("it does not add much productivity as I thought it would"), which is what made
> virtualization safe - drag was the one feature it silently breaks. Measured after:
>
> ```
> build the whole list (create + lay out)   424.2 ms  ->  46.1 ms
> ```
>
> All three budgets now PASS. The analysis below is kept because it is the reasoning, and because the
> "when it costs anything" section still governs whether a future change reintroduces the problem.

## The numbers

```
[--uitest] 54 meetings in the library

  ...of which: query the store                 17.6 ms
  ...of which: create the cards                63.8 ms
build the whole list (create + lay out)       424.2 ms
move the selection highlight                    0.4 ms
render an open meeting note                   139.2 ms
```

Read that middle block carefully, because it is the whole story:

| Stage | Cost | Whose code |
|---|---|---|
| SQL query | 17.6 ms | SQLite |
| Creating 54 cards | 63.8 ms | ours (`Card(m)`) |
| **Laying them out** | **~343 ms** | **WPF** |

**81% of a list rebuild is WPF measuring and arranging cards.** Only about six are on screen at any
moment. The other forty-eight are measured, arranged and kept alive for nobody.

---

## What was actually making it feel clunky (already fixed)

Virtualization was never the first problem. Two lines were.

### 1. Clicking a note rebuilt the entire list

`Select()` called `LoadList()` with the comment *"refresh selection highlight"*. So every click on a
note re-ran the SQL query and re-created roughly 800 visual elements, in order to change **one
background brush**.

Fixed by walking the cards already on screen (each carries its meeting id in `Tag`) and setting two
brushes:

| | Before | After |
|---|---|---|
| Click a note | ~424 ms | **0.4 ms** |

That is the single biggest win in this whole area, and it is about a thousand times faster. It is also
the one the owner was feeling, because clicking notes is what you do constantly.

### 2. Search re-queried and rebuilt on every keystroke

`OnSearch` called `LoadList()` directly. Typing "pricing" ran seven full-text queries and seven complete
rebuilds, each throwing away the visual tree the previous letter had just built.

Fixed with a 180 ms debounce: under the ~200 ms that reads as instant, above a fast typist's inter-key
gap. Typing "pricing" now costs **one** rebuild instead of seven.

**Neither fix required virtualization.** Both are what "slow and clunky" actually meant day to day.

---

## Virtualization: what, who, when, how

### What it is

Today `Cards` is a plain `StackPanel`. A `StackPanel` realises **every** child: it creates the visual,
measures it, arranges it and keeps it in memory, whether or not it is anywhere near the viewport.

A `VirtualizingStackPanel` inside an `ItemsControl` only realises the items **currently in view** (plus
a small buffer). Scroll down and it creates the rows you are arriving at; with
`VirtualizationMode="Recycling"` it reuses the containers you just left rather than creating and
destroying them.

### Who it affects

Anyone whose library has grown. The cost is linear in the number of meetings, and the visible portion is
constant (about six rows), so the waste grows with every meeting recorded and none of it is ever seen.

| Meetings | Build without virtualization | Build with virtualization (projected) |
|---|---|---|
| 54 (today) | **424 ms** (measured) | ~50 ms |
| 200 | ~1.5 s | ~50 ms |
| 500 | ~3.9 s | ~50 ms |
| 1,000 | ~7.8 s | ~50 ms |

The projections are linear extrapolation of a measured per-card cost, which is honest for creation and
slightly optimistic for layout (WPF's arrange pass is not perfectly linear). The virtualized column is
roughly flat because it only ever touches what is visible.

At 54 meetings this is a **noticeable pause**. At 500 it is a **hang**.

### When it costs anything

Only on a **full rebuild**. After the two fixes above, that happens on:

- opening the Library page
- changing sort
- toggling the "my notes only" filter
- the search debounce firing (once per pause in typing, not per keystroke)
- finishing a drag-reorder

All of those are deliberate, user-initiated actions, once each. That is why the fixes above mattered
more: they were firing dozens of times a minute.

### How it would be done

The obstacle is that the cards are built in C# (`Card(MeetingSummary)`), with drag handlers, hover
states, chips and a date badge. A `DataTemplate` cannot call that.

The clean way is a small `ItemsControl` subclass, roughly twenty lines:

```csharp
sealed class MeetingCards : ItemsControl
{
    internal Func<MeetingSummary, FrameworkElement>? Build;
    protected override DependencyObject GetContainerForItemOverride() => new ContentPresenter();
    protected override bool IsItemItsOwnContainerOverride(object item) => false;
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is ContentPresenter cp && item is MeetingSummary m && Build is not null)
            cp.Content = Build(m);          // called only for rows that are actually realised
    }
}
```

with

```xml
<local:MeetingCards ItemsSource="{Binding}" ScrollViewer.CanContentScroll="True">
  <ItemsControl.ItemsPanel>
    <ItemsPanelTemplate>
      <VirtualizingStackPanel VirtualizationMode="Recycling"/>
    </ItemsPanelTemplate>
  </ItemsControl.ItemsPanel>
</local:MeetingCards>
```

`Card(m)` is reused untouched. `PrepareContainerForItemOverride` fires again on recycle, so a reused
container is rebuilt for its new meeting.

### What has to change with it, and the risk

Six places currently reach into `Cards.Children` directly. Each needs rethinking, and one is genuinely
risky:

| Site | What it does | With virtualization |
|---|---|---|
| `LoadList` | `Children.Clear()` then `Add(Card(m))` | becomes `ItemsSource = items` |
| `HighlightSelectedCard` | walks children, sets brushes | only realised rows exist; the selected row may be off-screen, so selection has to live in the item state, not the visual |
| `ShowPage` | `Children.Count == 0` as "not loaded yet" | needs a real flag |
| auto-select first | `Children[0]` | becomes `items[0]` |
| `Placeholder` | added as a child | becomes an empty-state overlay |
| **drag-reorder** | `Cards.UpdateLayout()` then walks `Children` **by index** to compute the drop target | **the hard one.** Indices are positions in the realised window, not in the list. Needs hit-testing against the panel or index arithmetic against `ItemContainerGenerator` |

Drag-reorder is the reason this was not done first: it is the one feature that can be silently broken by
virtualization and would not show up in a screenshot.

---

## The decision, and why in this order

1. **Fix the constant costs first.** Clicking a note went 424 ms to 0.4 ms; search went 7 rebuilds to 1.
   These fire dozens of times a minute and needed no structural change.
2. **Then measure.** `--uitest` exists so this stops being a matter of opinion. It prints budgets and
   passes or fails them.
3. **Then virtualize**, because the measurement says the remaining 424 ms is over budget and grows
   linearly with the library. It is now justified by a number rather than by "virtualization is good
   practice".

The budgets `--uitest` asserts:

| Check | Budget | Why |
|---|---|---|
| move the selection highlight | 16 ms | one frame at 60 Hz; a click must feel instant |
| full list build | 250 ms | the threshold where a deliberate action starts to feel like a wait |
| open a meeting note | 400 ms | a heavier action; some latency is acceptable |

Current state: selection **PASS** (0.4 ms), note render **PASS** (139 ms), list build **FAIL** (424 ms).

---

## Related: the transcript fix cut the note view too

`NoteRenderer.MeetingDocument` builds one `FlowDocument` paragraph per transcript segment. A real
8-minute meeting produced **182** segments, so 182 paragraphs plus the notes above them.

The turn-merging fix (T1, see `docs/real-meeting-findings.md`) joins each speaker's consecutive lines,
taking that same meeting to roughly 40. That is a ~4x reduction in the note document for free, from a
change made for transcript readability rather than for performance.

Worth remembering as a pattern: the cheapest performance win in this round came from fixing a
correctness problem upstream.
