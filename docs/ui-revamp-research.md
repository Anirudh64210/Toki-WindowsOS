# UI Revamp Research - Glass teardown + industry "smooth UI" standards, mapped to Toki's WPF stack

**Date:** 2026-08-04 · **Status:** research spec (no code changed) · **Scope:** drives the full UI revamp pass.
**Method:** studied `pickle-com/glass` (repo + DeepWiki + source files), plus published UX patterns from
Granola, Superhuman, Linear, Raycast, Notion, Fantastical, ChatGPT/Claude chat UIs, and the Material /
Fluent / Carbon / NN-g motion literature. Every recommendation names the WPF mechanism and, where
possible, the Toki file it lands in.

---

## 1. Executive summary - highest-leverage changes, ranked (impact ÷ effort)

| # | Change | Why it wins | Effort |
|---|--------|-------------|--------|
| 1 | **Central motion-token system** - one static `Motion` class (durations + easings), every animation in the app consumes it. Today durations are scattered literals (160/200/210/240/280ms across `PillWindow.xaml.cs`, `SmoothScroll.cs`, views). | Consistent timing is the single biggest "premium feel" lever; it also makes every later change one-line. | S |
| 2 | **Measure-before-reveal for every notch state change** - measure the incoming view at target width (`view.Measure(new Size(w, double.PositiveInfinity))`) and animate to the *measured* height, instead of animating to hard-coded state sizes and hoping text fits. Kills text clipping as a class of bug. | Text clipping is the #1 "cheap-feeling" defect in a resizing container. | M |
| 3 | **Ask thread: industry-grade stick-to-bottom** - upgrade the existing 8 px anchor in `AskView.xaml.cs` to the three-state model (anchored / drifted-up / re-anchored) with a ~100–150 px threshold and a floating "jump to latest" pill. Never scroll a user who scrolled up. | This is the pattern ChatGPT, Claude, Discord all converge on; the 8 px threshold makes Toki's anchor hair-trigger fragile. | S |
| 4 | **Token batching for streaming text** - coalesce LLM tokens on a ~33–50 ms `DispatcherTimer` tick and append to a `Run`, instead of per-token layout invalidation. Add a staggered 3-dot "thinking" indicator with reserved height. | Smoothest visible win in the assistant; removes stutter and layout jump at once. | S |
| 5 | **Interruptible, single-authority notch animator** - port Glass's `smoothMovementManager` idea: one coordinator owns window bounds animation, ease-out-cubic, any new target cancels the in-flight one cleanly (`HandoffBehavior.SnapshotAndReplace` from *current animated value*, never from the stale layout value). Open/close must look identical on the 1st and 50th run. | Repetition-stable morphing is what makes the overlay feel native, not webby. | M |
| 6 | **100 ms feedback rule everywhere** - every click/press shows visual state change within 100 ms (WPF `Trigger`-based pressed states, optimistic UI for saves/deletes), all real work off the dispatcher. Superhuman's whole brand is this one rule. | Perceived speed > actual speed; cheap to audit. | S |
| 7 | **Calm event form = natural-language-first with live parse echo** - Toki already parses "call with Ana tomorrow at 3" (`Llm/CalendarCommand.cs`); make the *typed* create-flow the same: one text field, parsed chips (date/time/duration/people) render live underneath as confirmation, structured fields only as fallback. Fantastical's proven pattern. | Turns the heaviest form in the app into one input. | M |
| 8 | **Empty states with one CTA + the mascot** - every list/panel (Library empty, Ask first-run, Calendar disconnected, no-speech) gets: what this space will hold, one primary action, `TokiBlob` as the warm monochrome-adjacent illustration. Linear/Notion pattern. | Empty states are the first impression of half the app's views. | S |
| 9 | **Onboarding as a header state machine** - port Glass's `welcome → apikey → permission → main` progression into `PillState` onboarding states rendered *in the notch itself* (small header-sized steps, not a wizard window). Name capture is one friendly question; the privacy notice is a selling point ("everything stays on this laptop"), not legalese; the flow ends inside the product with a first action, never a blank home. | M8 needs onboarding anyway; the notch-native version is differentiating and Glass proves it works. | M |
| 10 | **Library list virtualization + pixel scrolling** - `VirtualizingStackPanel` (`VirtualizationMode=Recycling`, `ScrollUnit=Pixel`) so a 200-meeting library scrolls at 60 fps and still works with `SmoothScroll`. | Future-proofs the one unbounded list in the app. | S |
| 11 | **Entry animation vocabulary for new content** - one standard "arrive" motion (opacity 0→1 + 8–12 px translate-up, 180 ms ease-out) applied to: new chat message, new library card, prep card reveal, parse chips. Exit = 120–160 ms ease-in fade. Nothing else. | A single arrive/leave grammar reads as designed; five different ones read as accidental. | S |
| 12 | **Layout-stability audit pass** - `TextTrimming=CharacterEllipsis` on every single-line text, `MinHeight` reservations where content appears (thinking indicator, parse chips, status lines), no `Auto`-sized rows that pop, `UseLayoutRounding=True` at window roots. | Eliminates the 1-px jiggles and reflows that read as "unfinished." | M |

---

## 2. Glass (pickle-com/glass) - teardown and what to adapt

Sources: [github.com/pickle-com/glass](https://github.com/pickle-com/glass) ·
[deepwiki.com/pickle-com/glass](https://deepwiki.com/pickle-com/glass) ·
[deepwiki architecture page](https://deepwiki.com/pickle-com/glass/2-architecture-overview) ·
[`src/window/smoothMovementManager.js` (raw)](https://raw.githubusercontent.com/pickle-com/glass/main/src/window/smoothMovementManager.js) ·
[pickle.com/glass](https://pickle.com/glass) ·
[background on its origin as a Cheating-Daddy fork](https://quasa.io/media/the-ai-cheating-assistant-drama-cluely-s-meteoric-rise-pickle-s-open-source-clone-and-a-code-theft-scandal)

### 2.1 What Glass is

Electron app (LitElement web components in renderers, Node main process, IPC bridge), GPL-3.0, ~7.6k stars.
"Digital mind extension": an always-on-top, capture-invisible overlay with three surfaces - a persistent
**Header** bar, an **Ask** window (AI Q&A over screen+audio context), and a **Listen** window (live meeting
transcription/summary). Multi-provider LLM (OpenAI/Gemini/Claude/Ollama local), Whisper/local STT,
Rust-based AEC. Directory shape: `src/window/` (windowManager, windowLayoutManager, smoothMovementManager),
`src/ui/` (Lit components incl. the header state machine), `src/features/` (ask/listen/settings),
`src/bridge/` (IPC), `src/index.js`.

Philosophically it is Toki's closest open-source cousin - overlay-first, meeting-aware, local-capable,
invisible-to-capture (Toki's ghost mode / `WDA_EXCLUDEFROMCAPTURE` is the same trick Glass uses via
`SetWindowDisplayAffinity`). The differences: Glass records continuously/contextually and is
multi-window; Toki is consent-gated per meeting and single-morphing-window (both deliberate Toki
constraints - keep them).

### 2.2 Concrete, adaptable findings

**(a) `smoothMovementManager.js` - the window animation authority.** Observed implementation:

- Default duration **300 ms**, per-call override.
- Easing: **ease-out-cubic**, literally `eased = 1 - Math.pow(1 - p, 3)` - enter/move motion decelerates, never ease-in-out for movement.
- A `Map` of per-window animation timers; starting a new animation **cancels the in-flight one and retargets from the current position** - animations are always interruptible mid-flight without snapping.
- Destroyed-window guard (`_isWindowValid`) cleans timers and still fires completion callbacks.
- Same easing reused for `fade()` opacity - one motion grammar for bounds *and* opacity.

**Adaptation for Toki:** Toki's `PillWindow.xaml.cs` already animates `Width`/`Height` with `CubicEase`,
but timings vary per call-site and there is no single owner. Build a `NotchMotion` (or extend the
existing morph code) that is the *only* code allowed to animate window/clip bounds: takes a target rect,
uses `HandoffBehavior.SnapshotAndReplace` so retargeting mid-morph continues from the animated value,
exposes a `Completed` continuation, and reads durations from the motion tokens (§4e). WPF advantage over
Glass: Glass steps at 8 ms `setTimeout` (~125 fps attempted, on the CPU); WPF `DoubleAnimation` is
composition-clocked - keep it, just centralize it.

**(b) Separation: layout math vs. window plumbing vs. motion.** Glass splits `windowLayoutManager`
(pure "where should each window be" computation) from `windowManager` (create/show/hide) from
`smoothMovementManager` (how it gets there). **Adaptation:** give Toki a pure function
`NotchLayout.For(PillState state, Size measuredContent, Rect workArea) → Rect` - the design-spec state
sizes become *minimums*, measured content can grow them (see §4d), and the animator just tweens to
whatever the function returns. Testable without a window.

**(c) Header state machine for onboarding.** Glass's header renders one compact bar whose controller
(`src/ui/assets/header/controller.js`) walks `welcome → apikey → permission → main`; only in `main`
do the other windows exist. Onboarding is therefore *inside the persistent surface*, tiny, and each
step is one decision. **Adaptation:** add `PillState.Welcome / OnboardName / OnboardPrivacy /
OnboardModels / OnboardDone` rendered in the notch at prompt-card size (~300×74 to ~356×220), one
question per state, morphing between steps with the standard notch morph. No separate wizard window.
Model download progress lives in `OnboardModels` with a real progress bar (M8 requirement: a meeting
never waits on a download).

**(d) Keyboard-centric overlay control.** Glass: `Ctrl/Cmd+\` toggles visibility; `Ctrl/Cmd+arrows`
nudges window position; Ask summoned by shortcut from anywhere. **Adaptation:** Toki has
`Ctrl+Alt+Space` push-to-talk; consider (owner decision) a second chord to open/focus the Ask text bar,
and `Esc` consistently collapsing any expanded state back to the pill. Do not add position-nudging -
Toki's notch is deliberately top-center-fixed.

**(e) Provider modularity surfaced in UI.** Glass's onboarding first choice is "your API key or hosted."
Toki's equivalent lever (BYO key = "sharp brain", backlog #13) should be surfaced the same way: a single
calm choice card, on-device as the default, the API option framed as an upgrade - not buried in Settings.

**(f) What NOT to copy.** Glass's always-listening ambient capture (violates Toki Hard Constraint #3),
its multi-window architecture (Toki is one morphing window by design), Firebase/web components, and its
per-frame `setTimeout` animation loop (WPF's animation clock is strictly better). Also note Glass is
GPL-3.0 and had a code-provenance controversy - **adapt patterns, never port code**.

---

## 3. Industry findings - per topic, with Toki/WPF mappings

### 3a. Streaming-chat UX (Ask view, answer-on-the-notch)

Pattern consensus from ChatGPT/Claude/Discord teardowns
([The Prompt Bench](https://thepromptbench.com/ai-product-ux/streaming-ui-patterns-that-dont-break/),
[shadcn Message Scroller spec](https://ui.shadcn.com/docs/components/base/message-scroller),
[Setproduct AI-chat anatomy](https://www.setproduct.com/blog/ai-chat-interface-ui-design),
[intuitive chatbot scrolling](https://tuffstuff9.hashnode.dev/intuitive-scrolling-for-chatbot-message-streaming),
[Chrome team: rendering streamed LLM responses](https://developer.chrome.com/docs/ai/render-llm-responses),
[performant AI markdown renderer](https://tigerabrodi.blog/how-to-build-a-performant-ai-markdown-renderer)):

1. **Three-state auto-scroll** - *anchored* (auto-follow), *drifted-up* (user scrolled: never move them),
   *re-anchored* (user returned to bottom or tapped the pill). Auto-scroll engages only when the viewport
   is within ~100–150 px of the bottom when tokens arrive; 8 px (Toki today, `AskView.xaml.cs:55`) is too
   strict - any sub-pixel extent change un-anchors the user.
2. **"Jump to latest" affordance** - the moment the user drifts, show a small floating pill
   (bottom-center of the thread, above the input); click = smooth-scroll to end + re-anchor. Disappears
   when anchored.
3. **Wheel-up instantly un-anchors** - don't wait for the offset check; a `PreviewMouseWheel` with
   negative delta while streaming should set `_stick = false` immediately (fixes the "fighting the user"
   failure mode every issue tracker complains about).
4. **Batch token rendering** - render on a cadence, not per token. 30–50 ms coalescing is invisible to
   the eye and removes per-token layout passes.
5. **Thinking indicator with reserved space** - 3 dots, staggered opacity pulse; the indicator row
   occupies the same `MinHeight` the first text line will, so the transition thinking→streaming doesn't
   jump the layout.
6. **Message entry motion** - only the *new* message animates in (fade + small rise); history never moves.
7. **Markdown-safe streaming** - never re-parse/rebuild the whole document per chunk; append to the last
   block, and don't style a markdown token (e.g. `**bold`) until it's closed, otherwise text flashes.

**WPF implementation spec (`src/Toki/Views/AskView.xaml(.cs)`):**

- **Anchor state:** keep `_stick`, but: threshold `>= ScrollableHeight - 120`; set `_stick=false` in
  `PreviewMouseWheel` when `e.Delta > 0` (scrolling up) or on scrollbar thumb drag
  (`Thumb.DragStarted` via routed event); re-anchor only via the pill or the user reaching bottom.
  Keep the existing `DispatcherPriority.Background` deferred `ScrollToEnd` (correct - it runs after
  the appended text has been measured; `AskView.xaml.cs:60-64` comment already documents why).
- **Jump-to-latest pill:** a small `Border` overlaying the ScrollViewer (Grid layering), `Opacity`
  animated 0→1 over 150 ms when un-anchored during streaming; on click animate the SmoothScroll target
  to `ScrollableHeight`, then set `_stick = true`.
- **Token batching:** the streaming callback appends to a `StringBuilder`; a `DispatcherTimer`
  (33–50 ms, `DispatcherPriority.Render`) flushes into the bubble. Append into a trailing
  `Run` inside the `TextBlock`'s `Inlines` (or the last paragraph container) rather than resetting
  `.Text`, so WPF only re-measures the tail line, not the whole bubble.
- **Fixed wrap width:** answer bubbles get an explicit `MaxWidth` (thread width − padding) and
  `TextWrapping="Wrap"`; never let streaming text change the bubble's width - width reflow mid-stream
  is the most visible jitter.
- **Thinking indicator:** three 5 px `Ellipse`s; one `Storyboard` with `RepeatBehavior="Forever"`,
  each dot's `Opacity` keyframed 0.25→1→0.25 over ~900 ms with 150 ms `BeginTime` stagger. Row
  `MinHeight` = one line-height of the answer style.
- **Caret/cursor during stream (optional polish):** a 2 px `Rectangle` after the last Run with a soft
  500 ms opacity blink - Claude/ChatGPT both use a trailing marker to signal "still writing"; remove
  it on completion.

### 3b. Multi-field forms done calmly (event creation, settings)

Sources: [Fantastical NL input](https://flexibits.com/fantastical/help/adding-events-and-tasks),
[Fantastical NL guide](https://thesweetsetup.com/natural-language-guide-for-fantastical/),
[NN/g date-input guidelines](https://www.nngroup.com/articles/date-input/),
[Linear's delightful patterns](https://gunpowderlabs.com/2024/12/22/linear-delightful-patterns).

- **Natural-language-first (Fantastical's proven pattern):** one text field parses "Lunch with John
  tomorrow at 1pm for 2 hours" - title, people (`with`), place (`at`), date/time/duration extracted
  live. The critical UX detail: the parse is **echoed back as it happens** (highlighted tokens /
  a live-updating preview card), so the user trusts it before committing.
- **NN/g on dates:** free-text beats dropdown gymnastics for near-future dates; always show the
  *resolved* interpretation ("Thu, Aug 6 · 3:00–3:30 PM") because relative phrases are ambiguous.
- **Linear's modal form calm:** few visible fields; everything else behind small chip-buttons that
  open on demand; instant keyboard flow; the form never scrolls.

**WPF implementation spec (`CalendarView` create/edit):**

- Reuse `Llm/CalendarCommand.cs` / the deterministic NL parse already built for voice-create as the
  typed path: one `TextBox`, and beneath it a **chip row** (WrapPanel of small rounded `Border`s) that
  renders the parsed fields live: `📅 Thu Aug 6` `🕒 3:00 PM` `⏱ 30 min` `👤 Ana`. Chips animate in
  with the standard arrive motion; clicking a chip opens the tiny structured editor for just that field
  (the existing `SearchableDropdown` for people; a compact time list for time).
- **Debounce the parse** ~250 ms after last keystroke; reserve the chip row's height
  (`MinHeight` = chip height + margin) so the form doesn't grow when chips appear.
- Structured fallback stays for edit-existing, but follows dividers-not-boxes: labels above values,
  no field borders until focus (underline or subtle fill on focus only), one accent (coral) reserved
  for the primary action.
- **Confirm affordance** = the primary button restates the resolution: "Create · Thu 3:00 PM". Toki's
  voice-create is already confirm-gated; make typed create match.

### 3c. Onboarding / first-run

Sources: Glass header flow (§2.2c), [Appcues onboarding practices](https://www.appcues.com/blog/user-onboarding-best-practices),
[Formbricks 2026 practices](https://formbricks.com/blog/user-onboarding-best-practices),
[Toptal onboarding guide](https://www.toptal.com/designers/ux/user-onboarding-best-practices).

Distilled rules that fit Toki: first value fast; every question must visibly shape the experience;
explain *why* before asking for anything; end in the product, never on a dead "done" screen.

**Toki onboarding spec (notch-native, 4 short states):**

1. **Welcome** - mascot `DropIn()`, "Hi, I'm Toki. I take your meeting notes - everything stays on
   this laptop." One button.
2. **Name** - "What should I call you?" One `TextBox`, skippable ("later" link). Feeds the existing
   user profile (P10) → the greeting immediately uses it on the next screen: proof the answer mattered.
3. **Privacy = pitch, not legalese** - three short lines with checkmarks: *recorded only when you say
   yes · transcribed and summarized on this device · nothing ever uploaded*. This is Toki's core
   differentiator; say it here, once, warmly. (No scary OS permission prompts exist to explain -
   Hard Constraint #4 - so this screen is pure reassurance.)
4. **Models** - "Setting up my brain" with a real determinate `ProgressBar` per model download and the
   on-device-vs-API choice card (§2.2e). On completion, morph straight into **Home** with the calendar
   connect card and a first-run empty state that shows what will appear - never a blank pill.

Each step is one notch morph; back is always available; total time-to-home under a minute plus download.

### 3d. Preventing text clipping / measure-before-reveal / layout stability

This is pure WPF discipline; the industry principle is "the container adapts to content, content is
never truncated silently mid-animation" (visible in Raycast/Linear panels which never clip mid-resize).

**Spec:**

- **Measure before reveal:** before morphing to a state, instantiate/populate the view, call
  `view.Measure(new Size(stateWidth, double.PositiveInfinity))`, take
  `max(designMinHeight, ceil(view.DesiredSize.Height) + chrome)` as the morph target. The design-spec
  sizes (CLAUDE.md table) become minimums, not fixed truths. This single change eliminates the
  "prompt text clipped in German/long-title" class of bug.
- **Never `SizeToContent` on the overlay window** - it recalculates during animation and fights the
  morph; explicit measured targets only.
- **Single-line text:** every `TextBlock` that must stay one line gets `TextTrimming="CharacterEllipsis"`
  + `ToolTip` with the full text. Multi-line body text gets `TextWrapping="Wrap"` + fixed `MaxWidth`.
  A bare `TextBlock` in a star column with neither is a defect (auditable by grep).
- **Reserve space for late content:** anything that appears after load (status line, chips, thinking
  dots, "connected" badges) sits in a row whose `MinHeight` equals its rendered height - content
  changes opacity, not layout.
- **Grid discipline:** columns that hold text = `*` with `MinWidth`, never `Auto` (Auto + long text =
  neighbor push-out); use `SharedSizeGroup` for label columns that must align across sections.
- **Crisp pixels:** `UseLayoutRounding="True"` + `SnapsToDevicePixels="True"` at both window roots;
  `TextOptions.TextFormattingMode="Display"` for UI-size Segoe UI Variable (13–14 px) - `Ideal` blurs
  small text; keep `Ideal` only for large display type if kerning matters.
- **Scroll inside, not overflow outside:** any state whose measured height exceeds a max (e.g. notes at
  438, library at 440) scrolls internally (`ScrollViewer` + `SmoothScroll`), the window never grows past
  its max - matches the existing design system.
- **During the morph, content fades - it doesn't reflow:** cross-fade outgoing/incoming views
  (outgoing 120 ms ease-in fade-out → morph → incoming 180 ms ease-out fade-in, slight overlap ok)
  so text is never visibly re-wrapping while the width animates. Toki's clip-reveal approach is right;
  formalize the choreography.

### 3e. Motion standards - durations, easing, the "premium" numbers

Sources: [Material duration & easing](https://m1.material.io/motion/duration-easing.html),
[Fluent timing & easing](https://learn.microsoft.com/en-us/windows/apps/design/motion/timing-and-easing),
[Carbon motion](https://carbondesignsystem.com/elements/motion/overview/),
[NN/g animation duration](https://www.nngroup.com/articles/animation-duration/),
[Superhuman 100 ms rule](https://blog.superhuman.com/superhuman-is-built-for-speed/),
Glass `smoothMovementManager` (§2.2a).

Consensus: micro-feedback 100–150 ms; small component transitions 150–250 ms; view/container
transitions 250–400 ms; **enter/move = ease-out** (fast start, gentle stop), **exit = ease-in**,
ease-in-out only for an element transforming in place; anything over ~500 ms reads as sluggish;
motion must be a *system* (same values everywhere), and fast products (Superhuman) keep animation
minimal and never let it block input.

**Toki motion tokens (proposed `src/Toki/Theme/Motion.cs`, consumed by all XAML+code animations):**

| Token | Duration | Easing | Used for |
|---|---|---|---|
| `Instant` | 90 ms | ease-out (Cubic) | pressed states, hover fills, toggles |
| `Micro` | 150 ms | ease-out | chips, badges, jump-pill fade, icon swaps |
| `Enter` | 180 ms | ease-out cubic + 8–12 px translate-up | message/card/list-item arrival |
| `Exit` | 130 ms | ease-in cubic, fade only | dismissals, outgoing view |
| `Morph` | 280 ms | `CubicEase EaseOut` (match Glass's 1−(1−p)³) | notch bounds/clip morphs |
| `Grand` | 340 ms | ease-out cubic | first open, onboarding step morphs, prep-card drop |

Notes: Toki's current 200–280 ms values are already in range - the fix is *consistency*, not speed.
Switch the notch morph from `EaseInOut` (current `PillWindow.xaml.cs:460`) to **EaseOut** for
expansions (Glass, Material and Fluent all agree: arriving motion decelerates); keep a soft ease-in
tail only on collapse. Mascot personality animations (`TokiBlob.React()`) are exempt - character
motion may be springy/longer; UI motion may not.

**The 100 ms rule (Superhuman):** every input gets sub-100 ms visual acknowledgment. In WPF that means:
pressed/hover via `Trigger`/VSM (never code-behind async), all I/O off the dispatcher, and
**optimistic UI** ([Linear's pattern](https://gunpowderlabs.com/2024/12/22/linear-delightful-patterns),
[how Linear is fast](https://performance.dev/how-is-linear-so-fast-a-technical-breakdown)): rename/
delete/pin in the Library updates the UI immediately and reconciles with SQLite in the background -
Toki is local-first, so optimism is nearly always truth.

### 3f. Empty states & feedback loops

Sources: [Pencil & Paper empty states](https://www.pencilandpaper.io/articles/empty-states),
[Eleken empty-state UX](https://www.eleken.co/blog-posts/empty-state-ux),
[Carbon empty-state pattern](https://carbondesignsystem.com/patterns/empty-states-pattern/),
[Setproduct empty states](https://www.setproduct.com/blog/empty-state-ui-design),
Granola reviews ([BlueDot](https://www.bluedothq.com/blog/granola-review),
[Over the Anthill](https://overtheanthill.substack.com/p/granola),
[design bootcamp writeup](https://medium.com/design-bootcamp/how-granola-ai-helped-me-stop-taking-notes-and-start-listening-during-meetings-and-interviews-ff72215b6553)).

Rules: say what will live here; give exactly one primary action (+ optional quiet secondary); write
like a person; illustration warm but quiet (Linear/Notion use near-monochrome marks - Toki has
something better: the mascot); search-empty ≠ first-use-empty (different copy); errors always pair
what-happened with what-to-do-next.

**Toki spec:** a shared `EmptyState` control (mascot pose + title + one-line body + optional button):

- Library first-use: "Your meetings will live here. Join a call and I'll offer to take notes." (no
  button - the trigger is a meeting; instead a quiet "or write a manual note").
- Library search-miss: "Nothing matched '<query>'. Try fewer words." (mascot smaller; no CTA).
- Ask first-run: 2–3 tappable example prompts ("What did we decide yesterday?") - examples are the
  chat-UI-standard empty state and teach the retrieval boundary.
- Calendar disconnected: one sentence + "Connect Google Calendar" primary.
- No-speech already follows lesson #7 ("a silent meeting saves nothing") - fold it into the same control.

**Feedback loops (Granola's lesson):** Granola wins by staying a calm notepad during the meeting and
doing its magic at the edges. Toki's equivalents: recording state always honestly visible (session-truth,
H1); post-meeting "notes ready" pill is the payoff moment - it deserves the `Grand` entrance + a subtle
mascot reaction; every long operation (summarizing, model warm-up) shows determinate progress when
knowable, and the summarizing state already does.

---

## 4. The smooth-UI checklist - every Toki view must pass

**Layout & text**
- [ ] No text clipping in any state, any content length, any theme - every single-line `TextBlock` has `TextTrimming="CharacterEllipsis"`, every body text wraps within an explicit width.
- [ ] Notch morph target = measured content height (≥ design minimum); nothing is cut off on reveal.
- [ ] Nothing shifts layout after first render - late-arriving content has reserved `MinHeight`/`MinWidth`.
- [ ] `UseLayoutRounding` + `SnapsToDevicePixels` at the root; no half-pixel blurry borders.
- [ ] Both themes verified (`--render --theme`); no hard-coded hex left inline.

**Motion**
- [ ] Every animation uses a Motion token - zero literal durations in views.
- [ ] Enter = ease-out; exit = ease-in; notch morph ease-out cubic ~280 ms.
- [ ] Open/close/morph is identical on the 1st and 50th repetition - interrupting a morph retargets smoothly from the current animated value (`SnapshotAndReplace`), never snaps or drifts.
- [ ] Only new content animates in; existing content never re-animates or reflows.
- [ ] Animations never block input; `Esc` always works mid-animation.

**Feedback**
- [ ] Every click/press acknowledges within 100 ms (trigger-based visual state).
- [ ] Local mutations are optimistic; failures reconcile with a quiet toast/inline note.
- [ ] Every async operation > 400 ms shows state (dots/skeleton/progress); determinate when possible.
- [ ] Every empty view uses the shared EmptyState pattern: purpose + one action, human copy.

**Chat (Ask)**
- [ ] Auto-scroll only when anchored (≤ ~120 px from bottom); scrolling up instantly un-anchors.
- [ ] Jump-to-latest pill appears when drifted during streaming.
- [ ] Tokens batched ≥ 30 ms; bubble width fixed; thinking indicator height-reserved.

**Scrolling**
- [ ] All scrollers use `SmoothScroll`; unbounded lists virtualize (`Recycling`, `ScrollUnit=Pixel`).
- [ ] Scroll position preserved across state re-entry (Library, Calendar).

---

## 5. Implementation seams in today's codebase (where the work lands)

- `src/Toki/PillWindow.xaml.cs` - morph authority + measure-before-reveal + easing change (lines ~391, 460–465, 626–669 currently hold the scattered animation literals).
- `src/Toki/Theme/Motion.cs` (new) + `Theme/Theme.xaml` - motion tokens; Theme.xaml already hosts 22 animation-related usages to migrate.
- `src/Toki/Views/AskView.xaml.cs` - anchor threshold (line 55), wheel un-anchor, jump pill, token batching (existing deferred `ScrollToEnd` at 60–64 is correct and stays).
- `src/Toki/Controls/SmoothScroll.cs` - already excellent (280 ms ease-out glide, retargeting, extent-change target invalidation); expose its glide for the jump-pill; verify interop with `CanContentScroll` when Library virtualizes.
- `src/Toki/Views/CalendarView.xaml.cs` - NL-first create field + chip echo (reuse `Llm/CalendarCommand.cs` parsing); the Today-button fade at line 412–424 already follows the drift/return pattern - generalize it.
- `src/Toki/Controls/TokiBlob.xaml.cs` - mascot poses for EmptyState + onboarding `DropIn()` (already exists).
- New: `Controls/EmptyState.xaml`, onboarding `PillState`s + views, `NotchLayout` pure layout function.

---

## 6. Sources

**Glass:** [repo](https://github.com/pickle-com/glass) · [DeepWiki overview](https://deepwiki.com/pickle-com/glass) · [DeepWiki architecture](https://deepwiki.com/pickle-com/glass/2-architecture-overview) · [smoothMovementManager.js](https://raw.githubusercontent.com/pickle-com/glass/main/src/window/smoothMovementManager.js) · [pickle.com/glass](https://pickle.com/glass) · [origin/controversy context](https://quasa.io/media/the-ai-cheating-assistant-drama-cluely-s-meteoric-rise-pickle-s-open-source-clone-and-a-code-theft-scandal)

**Streaming chat:** [The Prompt Bench - streaming UI patterns](https://thepromptbench.com/ai-product-ux/streaming-ui-patterns-that-dont-break/) · [shadcn Message Scroller](https://ui.shadcn.com/docs/components/base/message-scroller) · [Setproduct AI chat anatomy](https://www.setproduct.com/blog/ai-chat-interface-ui-design) · [Intuitive chatbot scrolling](https://tuffstuff9.hashnode.dev/intuitive-scrolling-for-chatbot-message-streaming) · [Chrome: render LLM responses](https://developer.chrome.com/docs/ai/render-llm-responses) · [Performant AI markdown renderer](https://tigerabrodi.blog/how-to-build-a-performant-ai-markdown-renderer) · [Streamdown](https://streamdown.ai/)

**Motion:** [Material duration & easing](https://m1.material.io/motion/duration-easing.html) · [Fluent timing & easing](https://learn.microsoft.com/en-us/windows/apps/design/motion/timing-and-easing) · [Carbon motion](https://carbondesignsystem.com/elements/motion/overview/) · [NN/g animation duration](https://www.nngroup.com/articles/animation-duration/)

**Speed/product feel:** [Superhuman 100 ms](https://blog.superhuman.com/superhuman-is-built-for-speed/) · [Linear delightful patterns](https://gunpowderlabs.com/2024/12/22/linear-delightful-patterns) · [Linear performance breakdown](https://performance.dev/how-is-linear-so-fast-a-technical-breakdown)

**Forms/dates:** [Fantastical NL input](https://flexibits.com/fantastical/help/adding-events-and-tasks) · [Fantastical NL guide](https://thesweetsetup.com/natural-language-guide-for-fantastical/) · [NN/g date input](https://www.nngroup.com/articles/date-input/)

**Onboarding:** [Appcues](https://www.appcues.com/blog/user-onboarding-best-practices) · [Formbricks](https://formbricks.com/blog/user-onboarding-best-practices) · [Toptal](https://www.toptal.com/designers/ux/user-onboarding-best-practices)

**Empty states:** [Pencil & Paper](https://www.pencilandpaper.io/articles/empty-states) · [Eleken](https://www.eleken.co/blog-posts/empty-state-ux) · [Carbon pattern](https://carbondesignsystem.com/patterns/empty-states-pattern/) · [Setproduct](https://www.setproduct.com/blog/empty-state-ui-design)

**Granola:** [BlueDot review](https://www.bluedothq.com/blog/granola-review) · [Over the Anthill](https://overtheanthill.substack.com/p/granola) · [Design bootcamp writeup](https://medium.com/design-bootcamp/how-granola-ai-helped-me-stop-taking-notes-and-start-listening-during-meetings-and-interviews-ff72215b6553)
