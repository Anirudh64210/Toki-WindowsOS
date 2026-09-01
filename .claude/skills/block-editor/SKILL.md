# The note editor - block model, keys, and the traps

Research-derived (Notion's help centre, block-model blog and production CSS; the Notion API;
ProseMirror source and forum; Tiptap; BlockNote; Editor.js test suites; WordPress Gutenberg;
CKEditor 5; Lexical; prosemirror-flat-list; dotnet/wpf issues). Apply this skill on EVERY change to
NoteEditor, NoteRenderer, NoteMarkdown, the sticky note, or the prep card.

When a rule here conflicts with older code, the rule wins. When it conflicts with the owner, the
owner wins.

Toki's editor is a WPF RichTextBox over a flat FlowDocument of Paragraphs, each carrying a marker.
It is NOT a full block editor and does not need to be. These rules say which of Notion's behaviours
are load-bearing and which are optional.

## 0. The one idea

Notion has ONE primitive. Every gesture is a one-field edit on it: turn-into sets the type, indent
moves a child pointer, check sets a flag. Nothing is modal, nothing loses content. If a change needs
a special case per block kind, the feel is already gone.

## 1. The block model

A block is { Id, Type, Props, InlineContent, Children }.

- **Type is explicit state, never inferred from the text.** It is set by exactly five paths: a
  markdown input rule, the slash menu, turn-into, a direct shortcut, paste.
- **Turn-into preserves the whole props bag.** Notion changes only the type; unused props are
  retained and ignored, so a to-do's checked state survives a trip through Heading and back. Never
  build per-type classes that drop fields on conversion.
- **Lists are RUNS, not containers.** No ul/ol wrapper, no WPF List. prosemirror-flat-list exists
  because the container model makes indent and dedent imprecise. Toki already learned this as
  "bullets create themselves"; do not go back.
- **Toggle open/closed is view state, not document state.** Never serialise it.
- **Markdown is a lossy EXPORT, never the source of truth.** BlockNote documents that non-list
  children get un-nested and styles dropped. The DB holds structure; a user-edited .md file is a
  PASTE of new content, not a merge, because the ids are gone.

## 2. Block identity (the rule that protects evidence links)

Toki's evidence links, sticky-note links and task extractor point at note locations. An id that
changes silently retargets all of them.

| Operation | Who keeps the id |
|---|---|
| Split at offset > 0 | Original keeps id and the LEFT half; right half is new |
| Split at offset 0 | Original id stays with the TEXT; the new empty block above is new |
| Merge | The EARLIER block's id survives |
| Type change | Id survives. This is what makes turn-into non-destructive |
| Indent / outdent | Id survives, only the parent changes |
| Paste | Every pasted block gets a FRESH id, even if the clipboard carries one |

## 3. Keys

### Enter, in chain order (the order IS the mechanism)

1. Selection non-empty: delete it first.
2. Empty list item, nested: outdent one level.
3. Empty list item, top level: convert to paragraph.
4. Otherwise split.

Put split first and you ship infinite bullets. ProseMirror's chain is newlineInCode ->
createParagraphNear -> liftEmptyBlock -> splitBlock, and liftEmptyBlock sits BEFORE splitBlock for
exactly this reason.

On split: the new block inherits type and depth for CONTINUATION types (bullet, numbered, to-do,
toggle) and falls back to paragraph for TERMINAL types (heading, quote, code, divider). Caret goes to
offset 0 of the new block.

Shift+Enter is a soft line break inside the same block.

### Backspace at the start, in stages (each stage is one keypress and one undo unit)

1. Selection non-empty: delete it, stop.
2. Depth > 0: outdent one level, stop.
3. Type is not paragraph: convert to paragraph, stop.
4. Merge into the previous block. The PREVIOUS block's type, depth and id survive. Caret lands at
   the join point.

Three sub-rules people miss:
- **Unmergeable previous block** (image, divider, transcript card): do NOT delete and do NOT refuse
  silently. Move the caret to the end of the previous block and stop. If it is atomic, SELECT it;
  a second Backspace deletes it.
- **"At the start" means at the start of VISIBLE content.** A block whose text is one space, a
  zero-width character, or an empty tag is functionally empty.
- **A block may own several inputs** (a to-do is a checkbox plus text). Backspace at offset 0 of
  input N moves to input N-1 WITHIN the block, and only crosses the block boundary from input 0.

### Tab and Shift+Tab

- Tab reparents the block under its PREVIOUS SIBLING.
- **Tab is a no-op when the block is the first child of its parent.** ProseMirror's sinkListItem
  returns false at startIndex 0. There is nothing to nest into.
- Outdent must carry the whole subtree, and the outdented block's following siblings become its
  children, or content teleports.
- Handle multi-block selection explicitly or disable it. Tab on a multi-selection silently doing
  nothing is a known gap in both BlockNote and Gutenberg.
- In WPF, Tab must be handled in PreviewKeyDown with e.Handled = true or focus leaves the editor.

### Arrows

- Left at offset 0 goes to the END of the previous block in document order; Right at the end goes to
  offset 0 of the next.
- Up and Down preserve the GOAL COLUMN across block boundaries. See section 6.
- Esc selects the whole block. In block-selection mode, arrows move the selection.

### Selection across blocks, then typing

Delete fully covered blocks, truncate first and last, append the last's remainder to the first,
delete the last. **The FIRST block's type, depth and id survive.** Guard: if the selection ends at
offset 0 of the trailing block, do not include that block at all (CKEditor 5 issue 6680: otherwise
deleting between two headings demotes the second one).

Build BLOCK-selection mode before cross-block TEXT selection. Notion shipped block selection years
before partial text selection across blocks, and still not on Firefox. That is the clearest evidence
available of how hard the text case is.

## 4. Markdown input rules

Anchored to the start of the block. The commit character is CONSUMED, not inserted.

    bullet     ^\s*([-+*])\s$
    ordered    ^(\d+)\.\s$
    task       ^\s*(\[([( |x])?\])\s$
    heading    ^(#{1,N})\s$
    quote      ^\s*>\s$
    divider    ^(?:---|___\s|\*\*\*\s)$      fires on the third hyphen, no space
    code       ^```([a-z]+)?[\s\n]$

Notion's own set deviates from markdown twice: ">" plus space makes a TOGGLE, and a double quote
plus space makes the quote block. Inline while typing: double-asterisk bold, single-asterisk italic,
backtick code, SINGLE tilde strikethrough. Link and image syntax and tables are PASTE-only.

Rules:
- Evaluate on text insertion only. NEVER on paste, programmatic insertion, undo/redo, or a click
  landing next to an existing marker.
- The ordered-list rule carries a join predicate: childCount + start equals the typed number. Typing
  "3. " after a two-item list joins it; "7. " starts a new list at 7.
- Look back a bounded distance (ProseMirror uses 500 characters) so long paragraphs do not degrade.
- **Backspace immediately after a rule fires REVERTS the rule** and restores the literal trigger
  text, caret at the end of it. Capture the pre-rule text and offset BEFORE mutating.

## 5. The slash menu

- Opens on TYPING the trigger. Not on paste, not on undo, not on a click beside an existing slash.
- **Only at the start of a line, or after a space.** Tiptap defaults to allowedPrefixes of a single
  space. This is what stops "and/or" opening a menu. Toki already does this; keep it.
- Notion's query TOLERATES SPACES and does not close on space, which is why a browser extension
  exists to add that. Toki closes on space deliberately. That is an owner choice, not a bug.
- No match: keep the typed text as literal text. Convert nothing, delete nothing.
- Dismiss on: item chosen, Esc, caret moves away, or backspacing past the trigger.
- **Empty block: CONVERT it. Non-empty block: INSERT below.** Either way delete the trigger and the
  whole query from the document BEFORE running the command.
- The menu owns the keyboard while open. Arrows, Enter and Esc must not also reach the document.
- **REFINED 2026-08-24: the cleanup belongs to the MENU, not to a way of choosing.** The line above
  ("delete the trigger and the whole query BEFORE running the command") was implemented in the Return
  case of the key handler, and the row's own Click handler just closed and ran. So Enter came out
  clean and every command picked with a POINTER left its "/" (or a whole "/standup template") sitting
  in the writing - and clicking is how a person picks a template. Two commit routes, one cleanup.
  Give the menu the cleanup as a callback it owns and route every route through one `Run`. A
  self-test that only presses Enter cannot see this: drive a real row Click.

## 6. WPF specifics that will bite

- **SetCaret is the ONLY way to move the caret in code.** WPF caches the x a vertical arrow aims for
  and never clears it when CaretPosition is set programmatically (dotnet/wpf#3757, open, triaged
  "very unlikely" to be fixed). MEASURED in Toki: caret 67.3px in, Down landed at 5.0px.
  SetCaret nudges the caret one character with the real editing commands and back, which makes WPF
  recompute the column from where the caret actually is, then re-asserts the position if the round
  trip drifted. Guarded by --notetest.
  SUPERSEDED, same day, and this is the useful half: the first version cleared the private
  TextEditor._suggestedX by reflection. It passed every check in the dev build and did NOTHING in
  the shipped one, because the app publishes SELF-CONTAINED and carries its own
  PresentationFramework where that field was not found, and the try/catch swallowed it. Running the
  harness on the PACKAGED exe is what caught it (39 checks in dev, 38 and a failure in dist,
  deterministically). Do not reintroduce reflection into WPF internals here.
- CaretIndex is not a DependencyProperty and cannot be bound. Setting Text through a binding resets
  the caret.
- **Positions are (BlockId, Offset)**, never a control reference or a screen point. Capture before
  every mutation, restore AFTER layout, and clamp the offset to the new length (a type change that
  strips a marker shortens the text).
- **Do not virtualise blocks in the editable region.** A block that does not exist cannot hold focus,
  be a selection endpoint, be scrolled to, or be seen by UI Automation. Gutenberg's answer instead:
  render only the FOCUSED block synchronously, everything else at a lower priority. Virtualise
  DECORATIONS (search highlights, evidence links), not content.
- No Effect inside a block template. Same jank triad as the notch.
- **NEVER give a marker's host an explicit Height. An `InlineUIContainer` CLIPS its child to the
  host's layout box.** (ADDED 2026-08-24, paid for in a shipped bug.) Toki's bullet, number and tick
  box live in an `InlineUIContainer` so they can never be read back as text. The host `Border` was
  given the body text's cap height, to sit the marker on the same optical band as the capitals - right
  intent, and it turns out to be a knife. The Border draws no clip and `ClipToBounds` is false; the
  clip comes from the line's hosting of the element. At a 16px body the host was 11px and a tick box
  is 24px, so 13 of every 24 pixels were cut away and every digit lost its top ("1." rendered as
  "l."). Sizing any container inside a FlowDocument to less than its child DELETES ink.
  - To land the marker on the band instead: size the host to its child, use
    `BaselineAlignment.Baseline` (host bottom on the text baseline), and sink it with a
    `TranslateTransform` by `(hostHeight - capHeight) / 2`. A RenderTransform moves INK without moving
    the layout box, which is the only correction that does not re-create the clip. The LINE does not
    clip the transformed ink; only the host ever did.
  - A dot and a tick box are SHAPES (centring the box centres the ink). A NUMBER is not - digits stand
    on a baseline with the font's descent as empty space below - so align the number by baseline and
    ride it back up against the sink.
  - The row height must come from the PARAGRAPH, not the marker: pin marked lines with
    `LineStackingStrategy.BlockLineHeight`, or a taller marker stretches its own row and a mixed list
    comes out ragged (measured: 28px for a to-do against 21.3px for a bullet).
- **`LineStackingStrategy.MaxHeight` is a FlowDocument's default and it IGNORES `LineHeight`.** It
  sizes each line to its own content. So `Document.LineHeight` is inert unless you also set
  BlockLineHeight, and any comment claiming an em-based leading is in force is probably describing an
  intention rather than the screen. MEASURED in Toki 2026-08-24: the documented 1.5em leading had never
  applied; the real leading was the typeface's own ~1.33em.

## 7. Undo

- Coalesce typing on **500ms AND adjacency**. Non-adjacent changes always start a new group.
- **Close the group around every structural change**: type change, indent, outdent, insert, delete,
  move, paste, programmatic rewrite.
- **Derived edits must NEVER get their own undo step.** Renumbering, trailing-block maintenance,
  auto-promotion and restyling are Toki's tidying, not the user's typing. If they record separately,
  Ctrl+Z appears to do nothing and the second press loses two things. OnTextChanged already wraps
  Normalize, AutoPromote and Renumber in one BeginChange/EndChange. Keep it that way, and note that
  HandleReturn and HandleBackspace currently do NOT.

## 8. Paste

- Split on one-or-more newlines. The "or more" matters: consecutive blank lines collapse and must
  NOT produce empty blocks.
- Do NOT try to be clever about one newline versus two. ProseMirror's author declined this: wrapped
  plain text (email, terminal output, an ASR transcript) uses single newlines as hard wraps, so
  either reading is wrong half the time. Offer an explicit "paste as one block" instead.
- Mid-block paste is a slice with OPEN ENDS: N lines into the middle of one block yields N blocks,
  not N+2. The first pasted line joins the left half, the last joins the right half.
- Structure that does not fit DEGRADES to the nearest accepted type. Never drop text silently.
- Run type inference on pasted markdown per line, on the LEADING MARKER only, and never re-type a
  block that already has content.

## 9. Numbering

The number is DERIVED, never stored. It is the 1-based index within the contiguous run of same
(type, depth) siblings. Any other block ends the run and the next restarts at 1, which is what Notion
does. Store an optional start index only on the first item of a run.

## 10. Atomic blocks and the gap cursor

Every position adjacent to a non-text block needs a selectable gap, or the block is unreachable and
undeletable: between two consecutive atomic blocks, before the first, and after the last. Selecting
an atomic block is a BLOCK selection with its own visual (a rim), not a text highlight.

Deleting a COLLAPSED toggle must expand it first. Silently deleting hidden children feels
unrecoverable even when undo exists.

## 11. IME (Chinese, Japanese, Korean)

- Enter during composition CONFIRMS a candidate. It is not a submit. Acting on it commits
  half-composed text and swallows the conversion.
- Guard every handler for Enter, Backspace, Tab, Esc and arrows while composing. Web editors test
  isComposing or keyCode 229; in WPF the equivalent is Key.ImeProcessed in PreviewKeyDown plus
  TextCompositionManager state.
- **No input rule, auto-promotion or repair pass may run during composition.** ProseMirror's rule
  runner literally opens by returning false if the view is composing.
- Never mutate the document from TextChanged while composing. Composition is ONE undo unit.
- This cannot be verified without an IME installed. Do NOT ship a blind fix; get a real test first.

## 12. Visual numbers, measured from Notion's production CSS

Notion's page shell is a grid: content width minmax(auto, 720px), margin width minmax(96px, 1fr).
The classic page is 900px wide with 96px padding each side, giving a **708px text measure**, which
Notion then hard-codes for its tablet layout. Full width sets margins to a fixed 96px.

Do NOT copy 708px into a 356px notch panel. Copy the RATIO: roughly 44 characters at a 16px body,
margins around 13 percent of the shell.

| Thing | Value |
|---|---|
| Body | 16px, line-height 1.5 |
| H1 / H2 / H3 | 1.875em / 1.5em / 1.25em, weight 600, line-height 1.3 |
| Block padding | 3px 2px, margin 1px 0. Paragraph gap falls out at 8px |
| Heading margin-top | H1 about 32px, H2 about 26px, H3 about 20px |
| Heading spacing rule | The HEADING owns the space above it; nothing owns space below; the first heading owns nothing |
| List marker column | 24px fixed, flex-shrink 0. Gap to text 2-4px |
| Indent per level | 24px |
| Bullet cycle by depth | dot, hollow dot, square |
| Drag handle (Notion's real inline style) | 18px wide, 24px tall, at left -20px, top 3px |
| Selection fill | rgba(35,131,226,0.14), NO border radius. Notion's block selection is a flat rectangle |

**Hanging indent is STRUCTURAL, not a text property.** The marker is a fixed-width sibling of the
text, so wrapped lines start at the text column automatically. In WPF that is a two-column Grid,
marker column fixed and right-aligned for numbers so multi-digit values do not shift the text.

**REFINED 2026-08-24: "right-aligned for numbers" means FOR NUMBERS ONLY.** Toki applied it to all
three markers, on the reasoning that a constant gap between marker and text is what makes a list look
even. It is not - what makes a list look even is a shared LEFT edge, because that is the edge the eye
scans down and the one the headings and paragraphs above it already use. MEASURED in Toki with all
three right-aligned: bullet ink at 24.0px, number at 19.6px, tick box at 8.0px, a 16px spread down the
left of every mixed list. Left-align the dot and the box; the text starts at a fixed column either way,
so nothing after the marker moves. Right-align a number only if you have multi-digit runs AND the text
column is not fixed.
- **Measure the INK, not the control.** A tick box is usually a hit pad around a smaller visible box
  (Toki's is 24px around 18px), so its control edge is 3px left of its ink. A check that measures the
  control reports three markers as aligned at the moment their visible edges are not - the same
  mistake as measuring a clipped marker's centre. Reach into the template for the visible element.

**ADDED 2026-08-24: the heading spacing rule in the table above is load-bearing.** "The HEADING owns
the space above it; nothing owns space below" is not a stylistic preference - a heading with space
underneath, followed by an empty line (which is exactly what a template's section is), opens a gap of
nearly seventy pixels that reads as a hole in the page rather than as a place to write. Toki had 0.62em
below and that was the whole reason its templates looked broken.

**ADDED 2026-08-24: a blank line is a FULL line.** Toki drew one at 9/13 of a row so Enter-Enter gave
"a beat, not an empty row". Superseded: it is a gap you cannot predict and a caret target you can barely
hit, and Notepad, Apple Notes, Notion and Word all give a full empty line. If a tighter beat is wanted,
take it from the paragraph gap, never from a line the caret has to live on.

## 13. Placeholders

Three tiers, and only three:

1. **Empty document**, one empty block: show the document hint, focused or not.
2. **Focused empty block**: show the default hint. Requires focus.
3. **Block-type placeholder**: an empty block of a given type shows its own name ALWAYS, focused or
   not. This is why an empty Heading in Notion shows a grey "Heading" when the caret is elsewhere.

Never in a code block, never while text is selected. Toki puts the hint UNDER the editor, not in the
document, so it can never land in the saved Markdown. Keep that.

## 14. What Toki already does right (do not "fix" these)

- Slash only at the start of a line, so "and/or" stays prose.
- Enter owned by Toki for every line kind, because WPF's split copies formatting but not markers.
- Backspace on an empty marked line drops the marker instead of eating the line above.
- Renumber derives from contiguous runs and resets on any non-numbered line.
- The repair pass is ONE undo unit.
- Markers are drawn geometry sized in ems, baseline-aligned to the cap band, measured at 0.05px by
  --notetest. Never go back to a font character at a hardcoded size.
- The hint lives outside the document.

## 15. Open, and owner-gated (do not decide these alone)

1. **Tab.** AcceptsTab is True, so Tab types an invisible tab into the saved Markdown. It is neither
   indenting nor nothing. Options: nest under the previous sibling, or swallow the key.
2. **Enter and Backspace over a selection.** Both currently bail to WPF, whose split copies
   formatting but not markers, the exact fault the editor was built to avoid.
3. **IME.** The repair pass runs on every text change including mid-composition. Cannot be verified
   on this machine.
