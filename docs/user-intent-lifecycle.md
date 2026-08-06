# User-Intent × Lifecycle Coverage Map - Toki for Windows

_Read-only analysis. Maps every stage of the meeting lifecycle to the user's likely
intents, checks whether the code gives each intent (a) a visible affordance, (b) a
feedback loop, or (c) nothing, and proposes the missing prompts - each with a file-level
hook and a note on how it fits the restraint principle ("appear only with something worth
showing")._

Author: systems-analysis pass, 2026-08-04. Source of truth: `src/Toki`.

---

## 0. The lifecycle as a state sequence

The pill's state machine (`src/Toki/State/PillState.cs`, transitions in
`src/Toki/State/AppState.cs:9-31`) drives everything. The real user journey threads
through it as:

```
idle (Hidden/Collapsed)
  → [detector fires] MeetingPrompt        "Slack is in a call. Record it?"
  → RecordingBar  ⇄ RecordingOpen         upside-down Toki  /  timer + live line + Stop
  → [Stop, or auto-stop on mic release] Summarizing
  → Notes  |  NoSpeech                     keep/discard  /  "nothing was said"
  → Library (persisted)  → Ask (typed in hub, or push-to-talk on the notch)
Parallel tracks:
  Calendar (agenda / create / edit / delete)   PillState.Calendar
  PrepCard (~5 min before a calendared meeting) PillState.PrepCard → JoinAndRecord
  Home / Settings / About / ManualNote
```

Two facts frame every gap below:

1. **The mic on the notch runs ONLY while `Ctrl+Alt+Space` is held**
   (`App.xaml.cs:818-905`, `PttStart`/`PttStop`). There is no continuous listen and no
   auto re-open of the mic. Every conversational turn requires the user to physically
   press-and-hold again - the assistant can *ask* a follow-up but cannot *listen* for the
   answer on its own.
2. **The live-meeting "record?" prompt is one-shot per mic-session**
   (`MeetingDetector.cs:126,128` - `_prompted.Add(c.Id)`). Once offered (or dismissed),
   nothing re-surfaces while that same call continues.

---

## 1. Coverage table

Legend for **Affordance**: ✅ clear visual prompt · 🟡 present but weak/indirect · ❌ nothing.

### Stage A - Idle (Hidden / Collapsed)

| User intent | Affordance | Feedback loop | Gap / proposal |
|---|---|---|---|
| "Where is Toki / bring it up" | ✅ tray + `Ctrl+Alt+Space` peek (`App.xaml.cs:808-816` `ToggleHome`) | ✅ Collapsed → Home | - |
| "Start a note now without a detected call" | ✅ Home → Take Notes (`HomeView.xaml:42-49`, `StartQuickNote` `App.xaml.cs:412-418`) | ✅ morphs to RecordingBar | - |
| "Is a meeting happening right now that I should record?" | 🟡 only the one-shot detector prompt | - | **GAP #1 (owner-flagged).** See §2.1. |

### Stage B - Meeting detected → MeetingPrompt

| User intent | Affordance | Feedback loop | Gap / proposal |
|---|---|---|---|
| "Record it" | ✅ coral **Record** (`MeetingPromptView.xaml:9-10` → `AcceptPrompt` `App.xaml.cs:393-401`) | ✅ → RecordingBar; arms auto-stop | - |
| "Not now" | ✅ **Not now** (`MeetingPromptView.xaml:11-12` → `DismissPrompt` `App.xaml.cs:404-409`) | 🟡 snoozes 10 min silently - no toast confirming "I'll stay quiet for this call" | Minor: a 1.5s "Okay - I'll stay out of this one" on dismiss. |
| "Always record this app - stop asking" | ❌ **no such control** (only Record / Not now). CLAUDE.md Constraint #3 explicitly anticipates an "always record" mode; `grep` for `AlwaysRecord`/`always` finds no setting. | - | **GAP #7.** Add an "Always for {app}" affordance (secondary link on the prompt or a Settings per-app list) → persists to `AppSettings`, checked in `MeetingDetector.PollOnce` before raising the prompt so it auto-starts instead. |
| "Which app is this?" | ✅ friendly app name in text (`MeetingPromptView.xaml.cs:29-30`) | - | - |
| "I'm ignoring this - go away on its own" | ❌ MeetingPrompt has **no auto-dismiss timeout** | - | Minor: auto-collapse the prompt after ~20s of no action (treat as a soft "not now" without the 10-min snooze), so a missed prompt doesn't squat. |

### Stage C - Recording (RecordingBar ⇄ RecordingOpen)

| User intent | Affordance | Feedback loop | Gap / proposal |
|---|---|---|---|
| "Is it actually recording?" | ✅ upside-down peeking Toki = "I'm recording" (`RecordingBarView.xaml:5-7`); expanded shows coral dot + "Recording" (`RecordingOpenView.xaml:24-28`) | ✅ live transcript line proves audio is flowing (`RecordingOpenView.xaml:31-34`, fed by `App.LivePreview`) | Collapsed bar relies on the user *knowing* the upside-down-Toki convention - no text label. Minor. |
| "Stop" | ✅ **Stop** in expanded view (`RecordingOpenView.xaml:36-43` → `StopRecording` `App.xaml.cs:529`) | ✅ → Summarizing loader | Stop is only in the *expanded* view - from the collapsed bar the user must first hover/click to expand. Acceptable. |
| "How long have I been recording?" | ✅ timer (`RecordingOpenView.xaml:20-21`, `RecordingClock`) | ✅ ticks | Not visible on the collapsed bar. Minor. |
| "Take a quick note / mark this moment" | ❌ no in-recording bookmark or note action | - | **GAP #8.** A "mark moment" affordance (a small pin on the expanded bar) writing a timestamp into the session - high-value, low-restraint-cost. Currently the only capture is the passive transcript. |
| "Join the actual call (open the tab)" | ❌ not from here | - | Covered pre-meeting by PrepCard only; see §2.1. |
| "Am I capturing *them* or just me?" | 🟡 the live line shows *whoever* spoke, but there's no explicit "capturing Slack + mic" indicator | - | Minor: label the source once ("Capturing {app} + your mic") on entry to RecordingOpen. Reassurance for the core privacy promise. |
| "Mute/pause" | ❌ no pause | - | Deliberate omission (a paused recorder is a lie risk). Leave as-is. |

### Stage D - Summarizing

| User intent | Affordance | Feedback loop | Gap / proposal |
|---|---|---|---|
| "Is it working / how long?" | ✅ loader animation, min-show 1.2s (`App.xaml.cs:557,619`) | 🟡 indeterminate - no stage text | For a long meeting the LLM map-reduce can run up to 300s (`App.xaml.cs:633`). Proposal: cycle honest stage labels ("Transcribing…" → "Writing your notes…") so a 2-minute wait doesn't read as a hang. Hook: `SummarizingView` + a status setter called from the `StopRecording` background task. |
| "Cancel this / I changed my mind" | ❌ no cancel | - | Low priority - result auto-saves to Library regardless (`App.xaml.cs:664-674`). |

### Stage E - Notes (result) | NoSpeech (empty)

| User intent | Affordance | Feedback loop | Gap / proposal |
|---|---|---|---|
| "Keep it" | ✅ **Save to Library** (`NotesView.xaml:39-41`) | ✅ | - |
| "Discard it + its audio" | ✅ **Delete** (`NotesView.xaml:36-38` → `DiscardMeeting` `App.xaml.cs:94-113`) | ✅ removes DB row, markdown, audio | - |
| "Copy the notes" | ✅ Copy icon (`NotesView.xaml:21-22`) | ✅ | - |
| "Open the full note" | ✅ maximise to Library (`NotesView.xaml:23-24`) | ✅ | - |
| **"Ask a follow-up about this meeting"** | ❌ **no visible affordance** on the Notes screen | - | **GAP #3.** The engine is screen-aware - `Ctrl+Alt+Space` while Notes is up grounds "this meeting" (`App.xaml.cs:838-839`, `NotesQa.cs:230-236`) - but nothing tells the user. Add a quiet "Ask about this ⌥" chip → focuses the notch Ask. |
| **"Draft a follow-up email / share this"** | ❌ nothing | - | **GAP #6.** No email-draft or share action anywhere in the notes flow (`grep email\|draft\|share` in `NotesView.xaml.cs` → none). High-value post-meeting intent, entirely unserved. |
| "Rewrite in another template" | 🟡 exists via Library (`RewriteNotesAsync` `App.xaml.cs:142-155`) but not from the pill Notes view | - | Minor. |
| (NoSpeech) "Why nothing?" | ✅ honest empty state (`NoSpeechView.xaml:5-8`) | ✅ Okay dismisses | - |

### Stage F - Library / Ask (hub + notch)

| User intent | Affordance | Feedback loop | Gap / proposal |
|---|---|---|---|
| "Search / ask across meetings" | ✅ Ask hub page + notch push-to-talk (`AskView`, `NotesQa.AskStreamAsync`) | ✅ streaming answer, source chips | - |
| "Where did that answer come from?" | ✅ "About: {title}" scope line (`AskView.xaml.cs:88-100`) | ✅ | - |
| "See the full answer" | ✅ "See full answer in Library →" (`AskView.xaml.cs:200-206`) | ✅ | - |
| **"I asked by voice and Toki misheard - try again"** | 🟡 partial; static message, **no re-listen** | ❌ | **GAP #2 (owner-flagged).** See §2.2. |
| "Run a command (open library, new note, switch model…)" | ✅ `CommandRouter` (`App.xaml.cs:970-997`) | ✅ Toki confirms in one line | - |
| "New chat / clear context" | ✅ `NewChat` command + hub button | ✅ | - |

### Stage G - Calendar & voice-create

| User intent | Affordance | Feedback loop | Gap / proposal |
|---|---|---|---|
| "See my week" | ✅ `PillState.Calendar` week view | ✅ | - |
| "Create a meeting by voice" | 🟡 works for the happy path; **single-slot, no cross-turn correction** | 🟡 | **GAP #4 (owner-flagged).** See §2.3. |
| "Edit / delete an event" | ✅ Calendar view patch/delete with undo | ✅ | - |
| "Prep me before a meeting" | ✅ PrepCard auto-surfaces ~5 min out (`PrepWatcher.cs:20-21`) | ✅ LAST TIME / STILL OPEN / AGENDA + Join | - |
| "Join and record from the prep card" | ✅ **Join and record** (`PrepCardView.xaml:52-53` → `JoinAndRecord` `App.xaml.cs:430-438`) | ✅ opens link + records | Only fires in the ±5-min lead window and only for *calendared* events - not for a live ad-hoc call already in progress (see §2.1). |

---

## 2. The three owner-flagged gaps (deep dive)

### 2.1 GAP #1 - No "a meeting is ongoing - Join?" prompt during a live call

**Owner want:** while a meeting is actively happening, show an intent prompt offering to
join / record.

**Current behaviour.** Detection is a single edge event:
`MeetingDetector` raises `MeetingDetected` once per mic-session
(`MeetingDetector.cs:123-132`), the host flips to `MeetingPrompt`
(`App.xaml.cs:375-379`), and the app is added to `_prompted` so it is **never re-offered**
for the duration of that call (`MeetingDetector.cs:126,128`). If the user picks **Not now**,
that app is snoozed for 10 minutes (`Cooldown` `MeetingDetector.cs:19`, `Snooze`
`:70`, `DismissPrompt` `App.xaml.cs:404-409`). The consequence: **once the initial prompt is
gone, there is no standing "you're in a meeting - want me in on it?" affordance**, even
though the mic-in-use signal still says a call is live. The prep card's "Join and record"
(`App.xaml.cs:430`) is the *only* Join affordance and it is **calendar-driven and
pre-meeting** (`PrepWatcher` lead window, `PrepWatcher.cs:63-85`) - it cannot help an
ad-hoc call that is already underway.

**Also missing:** the prompt itself offers **Record / Not now** but never **Join** - even
when the detected app is a browser tab that hasn't opened the meeting URL. There is no
"a meeting is ongoing" *status* the user can act on later.

**Proposed prompt + hooks.**
- Add a low-key **ongoing-meeting affordance** on the idle/collapsed pill: when
  `MeetingDetector` still sees a non-denylisted app on the mic *and* the initial prompt was
  dismissed/ignored *and* we're not recording, show a small persistent "● In a call -
  record?" chip (not the full prompt again). Hook: a new `MeetingOngoing` event on
  `MeetingDetector` (raised from `PollOnce` when `liveIds` is non-empty, target isn't
  recording, and the app is past debounce), surfaced by `App` as a collapsed-pill badge
  rather than a state change - so it obeys restraint (a quiet dot, not a modal).
- Add a **Join** action to that chip (and optionally to `MeetingPromptView`) that opens the
  call. Reuse `Views.CalendarRows.OpenUrl` as `JoinAndRecord` does (`App.xaml.cs:433`); for
  an ad-hoc call with no known URL, Join simply focuses the detected app's window.
- **Restraint fit:** re-surface at most once (a second `_prompted`-style guard), and only as
  a dot/chip on the already-present pill - it is "something worth showing" (a live,
  un-recorded meeting) without re-nagging.

**Files to touch:** `Detection/MeetingDetector.cs` (new debounced ongoing signal, separate
from the one-shot `_prompted` set), `App.xaml.cs` (`StartMeetingDetection` wiring +
collapsed-pill badge), `Views/CollapsedView.xaml`(+`.cs`) or `RecordingBarView` idiom for
the badge.

### 2.2 GAP #2 - Misheard / unclear voice input: no clarifying follow-up + no re-listen

**Owner want:** when the assistant mis-hears or fails to understand, it should ask
("I didn't get that, can you repeat?") **and listen again**, not go silent.

**Current behaviour - two sub-cases:**

1. **Empty / no-speech transcription.** `PttStop` transcribes the held snippet; on empty it
   calls `Views.AskView.Instance?.Failed("Didn't catch that - hold and speak.")`
   (`App.xaml.cs:903`). Other failures show `"Couldn't transcribe that - try again."`
   (`:899`) or mic errors (`:867,875`). `Failed` just prints a dim line and arms auto-hide
   (`AskView.xaml.cs:126`). **The mic is not re-opened** - the user must physically
   press-and-hold `Ctrl+Alt+Space` again. This is the core miss: Toki *tells* the user to
   retry but does not *listen*.
2. **Understood audio but a misheard command.** A command-shaped utterance that matched no
   real command degrades to a targeted clarification via
   `CommandRouter.TryNearMiss` (`CommandRouter.cs:148-170`), invoked in the QA pipeline at
   `NotesQa.cs:208`. **But** `TryNearMiss` only covers **theme / ghost / notes-engine**
   switches (`:163-168`) - it requires a switch-verb lead (`:157`). A misheard **navigation**
   command ("open the laburry" mangled beyond the fuzzy `libr(ary|arry|ery)` set at
   `CommandRouter.cs:87`) is neither a command nor a near-miss, so it falls through to
   retrieval/QA and gets an unhelpful "not in your notes" style answer. And even when a
   clarification *is* produced, it is a **canned text line** (`NotesQa.cs:208` → `Canned`)
   - again **no re-listen**.

**Proposed prompt + hooks.**
- **Re-listen loop (the big one).** After a `Failed(...)` on the notch, or after any
  clarification reply that expects a spoken answer, **auto-arm the mic for a short window**
  so the user can just speak (VUI "reprompt" pattern). Since the product rule is
  *press-to-talk, no always-on mic* (CLAUDE.md, `mascot-and-voice` memory), scope this
  tightly: a **bounded ~6s "your turn" listen** that shows the waveform and a countdown, and
  closes itself if nothing is said. Hook: in `App.PttStop` (`App.xaml.cs:879-905`) replace
  the bare `Failed` return with a `RelistenAfter(prompt)` helper that re-enters the
  `PttStart` mic path once, driven by a `DispatcherTimer`, and have `AskView` show a
  "Listening for your reply…" strip (reuse `Listening()` `AskView.xaml.cs:50-61`).
- **Widen the clarifier.** Extend `CommandRouter.TryNearMiss` beyond theme/ghost/model to
  navigation near-misses (edit-distance against the known page nouns library/settings/
  templates/home), returning "Did you mean 'open the Library'? Say yes." This ties into the
  backlog's already-noted "did you mean?" idea (`docs/backlog.md:253`).
- **Make the empty-catch conversational.** Change `"Didn't catch that - hold and speak."`
  to also trigger the re-listen so the instruction is self-fulfilling.
- **Restraint fit:** the re-listen is bounded, self-closing, and only appears right after a
  genuine miss - it never becomes an ambient open mic.

**Files to touch:** `App.xaml.cs` (`PttStop`/`PttStart` - the re-listen loop + a shared
`_pttRelisten` guard), `Views/AskView.xaml.cs` (a "listening for reply" affordance +
countdown), `Llm/CommandRouter.cs` (`TryNearMiss` widening / a `DidYouMean` for nav),
`Llm/NotesQa.cs:205-208` (route the widened clarifier).

### 2.3 GAP #3 - Voice meeting-create is single-slot, not multi-turn slot-filling with corrections

**Owner want:** gather **all** details (title, date incl. today/tomorrow/day-after, exact
start→end time, people), let the user amend values across turns, and only then confirm with
a final "yes".

**Current behaviour.** `CalendarCommand.TryDetect` (`CalendarCommand.cs:51-71`) parses one
utterance into a `PendingCalendarCreate(Title, Start, End, Emails, PersonMentioned)`
(`:9-11`). The multi-turn logic lives in `NotesQa.AskStreamAsync`
(`NotesQa.cs:150-203`) on `ConversationContext.PendingCreate`
(`QueryUnderstanding.cs:61`):

- On first detection it stores the draft and asks **either** the confirm prompt **or**, if
  `Start is null`, the "when?" prompt (`NotesQa.cs:197-202`, `CalendarCommand.WhenPrompt`
  `:118-119`).
- The pending is **cleared up front on the very next turn** (`NotesQa.cs:156`,
  "Single-shot").

This yields **exactly one fillable slot - the start time** - and **no correction path**:

1. **Only `Start` is ever back-filled** (`NotesQa.cs:157-165`). If the user gave a title but
   no time, Toki asks "when?"; when they answer with a time it fills `Start`/`End` and moves
   to confirm. **Title, people/emails, and end-time are never solicited or amendable.** A
   person mentioned by name but with no email is only *noted in the description*, never
   turned into an "what's their email?" turn (`CalendarCommand.cs:143-144`,
   `ConfirmPrompt` `:114-115`).
2. **The draft dies on a non-time reply.** In the "when?" branch, if the follow-up doesn't
   parse to a time the draft is discarded and the turn falls through to normal QA
   (`NotesQa.cs:166` "the draft dies"). So "next week sometime" or "make it an hour" loses
   the whole pending create.
3. **Confirm accepts only literal yes/no.** At the confirm step the only recognized replies
   are `IsYes`/`IsNo` (`NotesQa.cs:168-171`, patterns `CalendarCommand.cs:43-44`). A
   **correction** like "yes but move it to 4" or "change the title to budget review" is not
   yes/no, so the pending is already-cleared (`:156`) and the correction is lost - the user
   must restart the whole create.
4. Positive: **date parsing itself is capable.** `QuickEventParser` wraps
   Microsoft.Recognizers (`QuickEventParser.cs:36`) and handles today/tomorrow/named days,
   explicit time-ranges (`datetimerange`/`timerange` `:109-114`), business-hours
   disambiguation of bare hours (`:78-82`), and rolling a past time forward (`:84-93`).
   Day-after-tomorrow is within the recognizer's grammar. So the **parser is not the
   blocker - the dialogue policy is**: it never runs a real slot-filling loop.

**Proposed prompt + hooks - a genuine slot-filler.**
- Replace the single-shot pending handling in `NotesQa.cs:150-203` with a **slot state
  machine** on `ConversationContext.PendingCreate` that:
  - Tracks which slots are still missing/unconfirmed (title, day, start, end, attendees) and
    asks for the **first missing required** one, one question per turn, until all required
    slots are filled - *without* clearing the pending on every turn (drop the eager
    `ctx.PendingCreate = null` at `:156`; clear only on final confirm/cancel).
  - Accepts **corrections at any turn**: re-run `QuickEventParser` / a person-extract on each
    reply and merge deltas into the draft (`pending with { ... }`) instead of only honoring a
    yes/no. Recognize amend phrases ("change the title to…", "make it 4pm", "add
    ana@x.com", "actually with Sam").
  - Keeps the **final confirm gate** - nothing is booked until an explicit yes
    (`CalendarCommand.ExecuteAsync` `:137-155` already refuses to create without confirmation
    and is provider-neutral).
  - Surfaces a **live draft summary** each turn (the current `ConfirmPrompt` `:101-116` is a
    good base - extend it to show all four fields and mark the one still needed).
- **Re-listen tie-in (compounds GAP #2):** each "when should it be?" / "who's coming?" turn
  should auto-arm the bounded re-listen from §2.2 - otherwise a multi-turn create means
  holding the hotkey for every single slot, which is exactly the friction the owner wants
  gone.
- **Restraint fit:** ask only for *required* missing slots (title + start are enough to
  book; end defaults to +30 min `QuickEventParser.cs:57`; attendees optional), so a fully
  specified one-shot ("schedule a sync with Ana tomorrow 3–4") still books in a single
  confirm - the loop only appears when detail is actually missing.

**Files to touch:** `Llm/CalendarCommand.cs` (slot model + amend detectors + a richer
`ConfirmPrompt`/`WhenPrompt` family), `Llm/QueryUnderstanding.cs:61` (`PendingCreate`
shape - add per-slot confirmed flags), `Llm/NotesQa.cs:150-203` (the loop itself), and the
re-listen hook in `App.xaml.cs` PTT path. Test seams already exist
(`CalendarCommand.CreateOverride`/`ConnectedOverride` `:22-23`, self-tests
`Dev/SelfTests.cs:1035-1069`).

---

## 3. Prioritized backlog of missing prompts & feedback loops

Ranked by user impact × how load-bearing the gap is to the core promise.

| # | Gap | Where it hooks | What it should do | Restraint note |
|---|---|---|---|---|
| P1 | **Voice re-listen after a miss / between slots** (GAP #2 core) | `App.xaml.cs:879-905` (`PttStop`/`PttStart`), `Views/AskView.xaml.cs:50-61,126` | After a failed catch or a clarifying/slot question, auto-arm a bounded ~6s "your turn" listen with waveform + countdown; self-closes | Bounded + self-closing; never an ambient mic |
| P2 | **Multi-turn calendar slot-filling + corrections** (GAP #4) | `Llm/NotesQa.cs:150-203`, `Llm/CalendarCommand.cs`, `Llm/QueryUnderstanding.cs:61` | Fill title/day/start/end/people over turns, accept amendments, single final confirm; live draft summary | Only asks for missing *required* slots; one-shot still one-shot |
| P3 | **Ongoing-meeting "record / Join?" affordance** (GAP #1) | `Detection/MeetingDetector.cs:123-132` (new ongoing signal), `App.xaml.cs:365-388`, `Views/CollapsedView.xaml` | A quiet "● In a call - record?" chip on the idle pill after the first prompt passed, with a Join action | A dot/chip on the existing pill, re-surfaced once - not a repeat modal |
| P4 | **"Ask about this meeting" affordance on the Notes screen** (GAP #3) | `Views/NotesView.xaml` (+`.cs`), grounding already at `App.xaml.cs:838-839`, `NotesQa.cs:230-236` | A quiet "Ask about this ⌥" chip that focuses the notch Ask; makes the invisible screen-grounding discoverable | One chip; the capability already exists, only unadvertised |
| P5 | **Draft follow-up email / share from a note** (GAP #6) | `Views/NotesView.xaml.cs` (new action), reuse `LlmService`/`NotesQa` | Generate a shareable recap / mailto draft from the note | Behind an explicit action, not proactive |
| P6 | **"Always record {app}" control** (GAP #7) | `MeetingPromptView.xaml` + `AppSettings` + `MeetingDetector.PollOnce` | Per-app auto-record so trusted meeting apps skip the prompt | Opt-in; honors human-in-the-loop by being an explicit user choice |
| P7 | **Widen the misheard-command clarifier to navigation + "did you mean?"** | `Llm/CommandRouter.cs:148-170`, routed at `NotesQa.cs:208` | Edit-distance clarify for library/settings/templates/home near-misses | Safe no-op reply, never a wrong action |
| P8 | **In-recording "mark this moment" / quick note** | `Views/RecordingOpenView.xaml`(+`.cs`), `Audio/RecordingSession.cs` | A pin that timestamps a highlight into the session | Small icon on the expanded bar only |
| P9 | **Staged Summarizing status text** | `Views/SummarizingView.xaml`(+`.cs`), setter from `App.xaml.cs:549-656` | "Transcribing…" → "Writing your notes…" so a long wait reads as progress | Text only; no new surface |
| P10 | **Dismiss/snooze confirmation toast + prompt auto-timeout** | `App.xaml.cs:404-409`, `MeetingPromptView` | 1.5s "I'll stay out of this one"; auto-collapse an ignored prompt after ~20s | Brief, self-dismissing |
| P11 | **"Capturing {app} + your mic" reassurance on record start** | `Views/RecordingOpenView.xaml.cs` | One line confirming both streams are captured | Once, on entry; supports the privacy promise |

---

## 4. What is already well-covered (so we don't regress it)

- **Recording integrity is visible and honest:** the upside-down Toki / coral-dot /
  live-transcript triad proves a live mic (`RecordingBarView.xaml`,
  `RecordingOpenView.xaml:24-34`); a live session can never silently vanish
  (`CloseOverlay` `App.xaml.cs:457-461`, session-truth auto-stop `:380-386`).
- **Empty/trivial results fail gracefully** with an honest empty state and audio cleanup
  (`NoSpeechView.xaml`, `App.xaml.cs:579-593`).
- **Answer grounding is shown, not hidden** ("About: {title}" `AskView.xaml.cs:88-100`),
  and misrouting to fabricated citations is actively prevented (`TurnRouter`,
  `NotesQa.cs:150-208` canned-reply tiers).
- **Commands confirm in one line** and never fire on question-shaped input
  (`CommandRouter.cs:112-124`, veto `:116`).
- **Prep is restrained and self-retiring** (`PrepWatcher.cs:11,55-58`).

The through-line of the real gaps: **Toki's *understanding* is strong but its *turn-taking
is not conversational* - it can ask, but (a) it can't listen back without a fresh
hold, (b) it only fills one calendar slot, and (c) it lets a live un-recorded meeting go
quiet after one prompt.** All three are turn-taking / feedback-loop gaps, not
comprehension gaps, and all three converge on the same fix primitive: a bounded,
restraint-respecting **re-listen** on the notch.
