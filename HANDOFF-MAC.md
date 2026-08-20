# HANDOFF: Toki for macOS

Written 2026-08-19 at the end of the Windows build's polish phase, by the Claude that built the
Windows version with the owner. You are the Claude that will build the Mac version. This file is
everything we learned, decided, and paid for. Read it fully before writing any code. Where a
lesson is Windows-specific I say so and give you the transferable principle underneath it; assume
everything else applies to the Mac verbatim.

The Windows repo this comes from has three living documents you should mine when you need depth:
`ARCHITECTURE.md` (every decision with rationale, append-only), `README.md` (build log, newest
first), and `mansplain/` (the plain-language story per milestone, written for the owner). The
constitution was `CLAUDE.md`. Copy that structure on day one; it is half the reason the project
stayed sane.

---

## 1. What Toki is

A meeting assistant that lives at the TOP edge of the screen as a hovering glass card (a "virtual
notch") plus one Library window. It detects when you are in a meeting, offers to record,
transcribes and summarizes entirely on-device, and keeps every meeting in a searchable local
Library. One-time $20, no subscription, nothing leaves the laptop. Restraint is the design.

On the Mac the virtual notch concept gets stronger, not weaker: MacBooks HAVE a notch, and the
design language (Dynamic-Island-style morphing, glass, a mascot) was born on Apple hardware. Toki
should live beside/below the real notch as a floating panel.

## 2. Hard constraints - every decision filters through these (unchanged on Mac)

1. **$0 monthly upkeep, forever.** No servers, no cloud costs. On-device ASR + LLM. Optional BYO
   API key = the user's cost, never ours.
2. **All data local.** SQLite + files under `~/Library/Application Support/Toki/`. No account, no
   backend, no telemetry, no sync.
3. **Toki only activates for meetings.** Never taps system audio globally; detects a call, asks,
   taps only that app's audio. Human in the loop. Owner decision, do not relax it.
4. **Minimal permissions.** Nothing that triggers a scary system prompt beyond what audio capture
   genuinely requires. On macOS this is harder than Windows (see section 8) - design the
   onboarding around the permission moments, never spring them mid-meeting.
5. **Restraint is the design.** Appear only with something worth showing.

## 3. Working with the owner (Sid, they/them)

The owner is **non-technical**. Most friction in the Windows build was alignment friction, not
technical friction. These rules are the accumulated fix:

- **Explain plans in plain language before big changes.** Recommend, do not assume. Ask before
  product decisions. Do exactly what was asked; never spread a change to other components "for
  consistency" without approval.
- **Verify by RUNNING the app and watching real output appear**, never by describing what code
  should do. A green test suite repeatedly missed bugs the owner saw in one glance. For anything
  visual, the owner's eyes are the final gate. When a test and the owner disagree, the owner is
  right - the test is measuring a model of the product.
- **Never run git commands.** The owner does ALL git, including staging. Your job ends at "safe
  to commit" plus a list of what changed. (One agent ran a read-only `git status` once and we
  flagged it as a violation. The rule is absolute.)
- **Never touch the real settings/token store** (on Windows it was a DPAPI-protected
  settings.json; on Mac it will be Keychain items plus a settings file). Corruption is
  irrecoverable. Harnesses and tests must never touch the real note store either.
- **No em dashes, anywhere** - code, comments, docs, UI, prompts. Hyphen, comma, or a new
  sentence. We guarded this with a unit test (`NoEmDashTests`) with a tiny named-exception list.
  En dashes in ranges stay. Build the same guard on day one.
- **A clickable thing is a real control.** On Windows: "a Button, never a Border with a mouse
  handler" - that bug shipped three times. On Mac: a real `NSButton`/SwiftUI `Button`, never a
  view with a tap gesture pretending. The box IS the button: hit target = hover highlight =
  visible box, one geometry.
- **A control wired to nothing is worse than no control.** Never ship UI for a feature that does
  not exist yet, even if the design frame draws it.
- **One Toki per surface.** On any panel or window there is exactly one mascot. Peeks carry their
  own tiny one only because the main bubble hides while peeking.
- **Small milestones**, each verified before the next. After every milestone update the build
  log, the architecture doc, and the plain-language story.
- The owner sends screenshots and screen recordings when something is wrong. TREASURE THESE.
  A screen recording, scrubbed frame by frame, solved in one evening what three rounds of
  reasoning circled (see section 15). Ask for a recording early when a visual bug resists you.
- The owner's taste question that catches most design misses: **"Would Apple ship this?"** (on
  Windows it was "would Outlook ship this" - open the household-name equivalent BEFORE building).

## 4. Tech stack mapping (Windows decision -> Mac recommendation)

| Concern | Windows (settled) | Mac recommendation |
|---|---|---|
| Framework | C# / .NET 8 + WPF | Swift + SwiftUI over AppKit (NSPanel for the notch window). AppKit escape hatches will be needed for window styling; do not fight SwiftUI where AppKit is one line. |
| Overlay | ONE morphing borderless window, top-center; state machine owns everything, views only read it | Same. One `NSPanel` (nonactivating, `.statusBar`+1 level), one state machine, views render state. Never a second overlay window per feature. |
| "Them" audio | WASAPI Application Loopback per-process-tree (Win 10 2004+) | macOS 13+: CoreAudio process taps (`AudioHardwareCreateProcessTap` / `CATapDescription`, macOS 14.2+) or ScreenCaptureKit audio-only capture of the app. Research which gives per-app audio with the mildest permission (SCK wants Screen Recording permission; the CoreAudio tap wants audio-capture entitlement). This is THE platform-risk item - prototype it first, before any UI. |
| "You" audio | WASAPI mic via NAudio | AVAudioEngine input node. |
| Detection | Registry mic-consent-store poll every 2.5s; DENYLIST not allowlist; debounce 2 consecutive polls | Mac equivalent research needed: poll CoreAudio for "some process is using the microphone" (`kAudioDevicePropertyDeviceIsRunningSomewhere` or the modern attribution APIs). Same shape: detect that a KNOWN-CALL-APP is using the mic, then ASK. Denylist (exclude browsers-with-music etc.), never allowlist; debounce two polls; human in the loop always. |
| Final ASR | Parakeet TDT 0.6b v2 (ONNX int8) via sherpa-onnx, whole-file per stream | Same model. sherpa-onnx has macOS builds (arm64). Alternatively evaluate whisper.cpp with Metal as primary on Apple Silicon, but Parakeet won on quality/speed for meeting audio - keep it unless measured otherwise. |
| ASR fallback | whisper.cpp small.en - only on ERROR, never on silence | Same, with Metal. |
| Name repair | Post-ASR correction, corpus-wide, trusted names; never decode-time hotwords | Identical (pure algorithm, port it - see section 10). |
| Diarisation | ASR-guided, one embedding per utterance, AVERAGE linkage, threshold 0.80, attendee-count prior, NO persistent voiceprints | Identical (owner decision, privacy-driven). |
| Notes LLM | Qwen3-4B-Instruct Q4_K_M via LLamaSharp (llama.cpp), Vulkan, VRAM-auto offload | Same model via llama.cpp with Metal. Apple Silicon unified memory makes the offload-tier logic simpler; keep a low-memory mode for 8GB machines. |
| Storage | SQLite + Markdown mirror in Notes/ | Identical. |
| Calendars | Google AND Outlook, both connectable, loopback+PKCE, DPAPI tokens, merged agenda | Identical flows; tokens in Keychain instead of DPAPI. |
| Voice | Push-to-talk ONLY (hold a hotkey). NO wake word, NO always-on mic | Identical (owner decision). Global hotkey via Carbon/`CGEventTap` or the modern `KeyboardShortcuts` approach. |
| Echo fix | Cross-stream text de-dup post-ASR (±4s window); OS AEC opt-in only | Identical. macOS voice-processing AudioUnit is the AEC equivalent - still opt-in only, it is fragile. |
| Screen-capture invisibility | WDA_EXCLUDEFROMCAPTURE ("ghost mode") | `NSWindow.sharingType = .none`. Same owner requirement: the notch must not appear in screen shares. On Windows every composition write dropped the flag and it had to be re-asserted; verify whether macOS has any equivalent trap (probably not, but TEST it with an actual screen share). |
| Distribution | Self-contained single exe now; signed installer + auto-update parked as M8 | .app bundle, Developer ID signing + notarization (cannot be skipped on modern macOS), Sparkle for updates later. Parked until product-final, same as Windows. |

**One hard Windows-specific rule that dies on Mac:** "never add Microsoft.ML.OnnxRuntime"
(it conflicted with sherpa-onnx's bundled native runtime). The transferable principle: two
packages must never both bundle the same native inference runtime. Whatever you pick, ONE onnx
runtime in the process.

## 5. The design system (the Figma file is still the boss)

- **Reference**: Figma file `Udtb5KHB2iTLIVP5FhKaSl`, node `288:4` ("Redesign by SKz"). When
  building any screen, fetch the frame and replicate it; approximations get rejected. Implement
  from the FRAME, never port generated HTML. A web mirror of the design system lives in the
  Windows repo under `design-web/` (React) - useful as a token reference, it drifts.
- **The material** (this took a full research + build cycle to get right; steal it wholesale):
  warm dark glass. Card plate is a vertical gradient ~95% opaque (#F2343130 top to #F52A2726
  bottom in dark; light mode #F2FBF9F8 to #F5F2EFEE), radius 24, with: a radial top sheen
  (#14FFFFFF to transparent), a fixed-seed noise grain at 2.5% (2.0% light) - invisible itself
  but the single biggest "material, not paint" trick - a 2px inner top highlight (#40FFFFFF
  fading), a 2px inner bottom shade (#33000000 fading), and a 1-device-pixel RIM that is a
  gradient: brightest at the crown (#33FFFFFF) fading down the sides (#1AFFFFFF) - the cheap 2D
  form of a specular edge. In light mode the rim ends on a dark bottom hairline or a white card
  has no edge against a white app. Two stacked shadows (key: blur 28, y 8, 22%; ambient: blur 8,
  y 2, 12%; light theme at 70% strength).
- **On the Mac you may get REAL glass.** `NSVisualEffectView` blur-behind is native and cheap,
  and it composes with per-pixel-alpha windows (the thing Windows could not do). Try real
  vibrancy FIRST. But keep the layered recipe in your pocket: Apple's own plates are 82-95%
  opaque paint - the blur is the last few percent - and the layered fake is what makes it read
  premium even where blur is unavailable. CHECK: does `sharingType = .none` (screen-share
  invisibility) coexist with NSVisualEffectView? If they conflict, the owner chose invisibility
  over true translucency on Windows and will again.
- **Glass on glass**: controls resting on the card carry an inner step (a lighter fill + its own
  1px rim + soft ring shadows). That stacking is where the Control Center depth comes from.
- **Text as white opacities**: 90% / 50% / 25%. Font: SF Pro on Mac (it was Segoe UI Variable on
  Windows purely as the platform equivalent).
- **Radii are concentric**: card 24 > inner 16 > capsule 12 > pill 999. Inner radius = outer
  radius minus padding, floor of 4. Every violation of this reads as amateur instantly.
- **One meaning one colour**: amber = time/counts, teal = actions, blue = people, green =
  decisions, pink = questions, coral = record CTA, and STOP is Apple system red #FF453A dark /
  #FF3B30 light, stop only. (We shipped a wrong "lipstick" pink-red for months; the owner
  called it immediately once the glass made everything else clean. #FF2D55 is Apple's PINK, not
  red - do not repeat that.) Purple = manual/user-authored (used for manual-note accent bars).
- **Accent bars and lists**: any per-item colour must MEAN something. We shipped Library note
  accent bars coloured by `id % 6` - random roulette - and the owner hated it on sight. Now:
  blue = recorded meeting, purple = manual note. Never hash-coloured decoration.
- **Speaker avatar tints** are derived from the name (stable hash), never fetched. Initials are
  honest; photos are not available on-device.
- **Hover grammar**: hovering BRIGHTENS via a fading overlay (130ms in, 90ms out, ease-out) that
  is a SIBLING of the content, so labels never dim and translucent fills never grey. Never an
  instant brush swap. On the base card a 22% white overlay was the measured-visible value
  (fancier glass tokens measured 1.49:1 contrast = invisible; 2.0:1 was the floor).
- **Press = compress**: every button scales to ~0.97 on press.
- **Dividers, not boxes; selection is a brighter fill, not an outline; smaller reads premium.**
- **No suggestion/example chips in the notch; notch = glance + toggles only** (owner verdicts
  from a design-review round; they vetoed tappable suggestion chips explicitly).
- **The mascot**: TokiBlob - a small blue blob with black eyes, gradient body, sparkles. It has
  a motion vocabulary (DropIn, React, eye-tracking toward the pointer, idle glances, writing
  wiggles). The app icon is GENERATED from the mascot at 7 sizes; a single large frame scaled
  down looks wrong. Voice/personality: warm, brief, no emoji in its own UI text.

## 6. Motion and feel (apply on EVERY visual change)

The Windows repo has `.claude/skills/motion-and-feel/SKILL.md`; recreate it for the Mac repo.
The rules that earned their place:

- **Animate only transforms, opacity, and clip.** Never animate layout properties per-frame.
  Never put a shadow/blur effect INSIDE an animated subtree (on Windows this forced software
  rendering per frame; on Mac it forces offscreen rendering - same class of jank).
- **Curves live in ONE file** (we had `Motion.cs`: ArriveStrong for entrances, Depart for exits,
  Drawer for the Dynamic-Island settle, Move for in-place transforms). Exits run ~70% of the
  enter duration. UI motion caps at ~350ms; CHARACTER motion (the mascot) is the one named
  exemption and may run 500-600ms.
- **The notch window is bigger than the card** (mascot gutter on one side, shadow padding on the
  others). The card hovers a few px below the top edge in most states, sits flush (gap 2) while
  recording and while transcribing, and the collapsed summon peek is flush. Mascot placement is
  per-state via one function.
- **Transitions measure BEFORE they mutate.** We shipped a visible stutter because the transition
  painted the new surface and THEN spent ~100ms measuring the incoming view - a bare card with
  neither state's content. Do all measurement first; the old state owns every pixel through any
  stall; pack the visual mutations to land in one frame.
- **Never record a "current size" at the start of an animation** you might overlap with another -
  overlapping animations snap. Read live values or gate grows on the state actually shown (we
  had a grow-to-content race because the state machine notified content handlers before the
  window; a refresh-triggered grow morphed the box under the OLD state).
- **Warm the first show.** WPF animation clocks are time-based and so are Core Animation's: a
  50-150ms stall building views or rasterising the mascot on FIRST display eats the head of the
  entrance animation. Build first-open views and render the mascot once, off-screen, at idle
  priority during launch. (Same philosophy as models: a meeting never waits on a download, an
  animation never waits on a rasteriser.)
- **The hide must compose before the window hides.** THE hardest-won lesson of the polish phase:
  on Windows, a layered window retains its last composed frame and REPLAYS it on the next show -
  we hid one frame before the fully-closed clip composed, so every summon started with a stale
  corner stub, in both themes, for weeks, surviving two wrong fixes. Waiting on dispatcher/run
  -loop priority was NOT enough (priorities order UI-thread work, not the render thread's
  presentation); we had to count two actual rendered frames (`CompositionTarget.Rendering`; on
  Mac use `CADisplayLink`/`CVDisplayLink` or transaction completion) before hiding. Whatever the
  Mac's equivalent behaviour is, write the entrance self-test (below) before you write the
  entrance.
- **Transient surfaces open on DWELL and close on LEAVE, never on timers.** The transcribing
  loader: a 360ms hover dwell opens it (a drive-by graze does nothing), it re-tucks ~540ms after
  the cursor leaves; an idle timer exists only as a backstop. Timer-driven UI ping-pongs.
- **Character beats for state hand-offs.** When notes are ready: the mascot pops up with a
  "Notes ready" pill for 2s; clicking opens the notes; unclicked, Toki hops out of his bubble,
  somersaults once, shrinks, and flies to the top edge to become the peeking Toki (560ms). The
  hand-off rule: never two mascots visible, never a one-frame swap - sequence it and FILM it.
- **Hand-off/fade rules**: when a surface with the mascot collapses to a peek that has its own
  tiny mascot, fade the big one out over ~180ms BEFORE the swap; a deferred one-frame removal
  reads as a glitch. Anything faded out that can come back must be restored explicitly on
  re-entry (we left Toki permanently invisible once by fading without a restore path).

## 7. The notch window (platform mechanics)

- One borderless, non-activating, always-on-top panel. It must never steal focus - on Windows we
  used ShowActivated=False plus a global mouse hook for scroll; on Mac `NSPanel` with
  `.nonactivatingPanel` gives you this almost for free, and scroll events over a non-key window
  work natively. Position top-center in PHYSICAL pixels on the screen with the cursor.
- Screen-share invisibility (`sharingType = .none`) is an owner requirement (they present Toki
  while using Toki). Verify with a real Zoom/Meet share, not documentation.
- **The DPI/scale trap** (Mac version): Windows bugs came from logical-vs-physical pixel
  confusion at 125% scaling (hairlines smearing, glyphs snapping off-center by one device
  pixel). Retina is a clean 2x so most of that vanishes, BUT: draw hairlines at whole device
  pixels, and let tiny glyphs center at sub-pixel positions rather than snapping. Non-retina
  externals still exist; test at 1x once.
- **Small glyphs are authored vector paths, never font characters.** "▶" / "■" / "✕" text in a
  small disc can never be reliably centred: font glyphs carry baseline and side-bearing offsets
  that shift with fallback and scale. Draw the triangle/square/cross as paths, optically centred
  (a right-pointing triangle sits a hair RIGHT of box centre because its centroid is left of
  it). On Mac, SF Symbols are a legitimate alternative BECAUSE they are alignment-designed
  vectors with baked-in optical centring - prefer SF Symbols, fall back to paths, never raw
  string glyphs.
- **Letters in discs**: text line-boxes inherit line spacing from their surroundings (on Windows,
  Paragraph.LineHeight INHERITED into the avatar's TextBlock and pinned the initial to the top
  of the disc). Pin the line height to the font size inside any disc/chip and add a small
  optical lift. One shared "disc label" helper, used everywhere.
- **A control must fit its layout slot.** Layout systems silently clip an oversized child (our
  26px puck in a 24px row shipped with a flat bottom edge). Our hit-target self-test now fails
  any clickable control being layout-clipped. Do the same.

## 8. Audio capture and levels (the highest-risk area - prototype FIRST)

- **Two streams, always**: "you" (mic) and "them" (per-app output). Never system-wide loopback -
  hard constraint #3.
- **Permissions choreography (Mac-specific danger)**: mic permission AND (likely) screen-
  recording-or-audio-capture permission for the app tap. Request at ONBOARDING with plain
  explanations, never mid-meeting. If the per-app tap needs the Screen Recording permission, be
  honest in the UI about why (audio only, nothing leaves the machine).
- **THE level lesson (top-3 most important in this file)**: the owner's mic records ~38dB below
  the meeting stream, and a recogniser handed near-silence does not return nothing - IT RETURNS
  CONFIDENT NONSENSE. Half of every transcript was invented before we caught it. The fix stack,
  in order: measure per-buffer RMS; a Silero VAD gate BEFORE ASR (sherpa-onnx bundles it) so
  silence never reaches the model; LEVELLING before the gate (lift the quiet stream; true peak
  must NOT set the gain - one loud clap would duck the whole meeting); and only then transcribe.
  When transcripts look wrong, CHECK THE INPUT'S LEVEL before blaming any model, prompt, or the
  note writer. 99% peak can still be healthy, not clipped.
- **The gate must be on the path the product actually uses.** Ours was wired into a path real
  meetings never took for a while. Test the LIVE path, not the harness path.
- **A silent meeting saves nothing.** Empty transcript = no Library entry, no hollow notes.
- **Echo**: cross-stream text de-dup after ASR (±4s window) fixed it; OS echo-cancel is fragile
  and opt-in only. When comparing streams remember the asymmetry: a word wrongly DELETED is
  unrecoverable, a duplicate is cosmetic - thresholds must not be symmetric.
- **Recording session truth**: duration is wall-clock, not UI ticks. Recording state is owned by
  the session object, not the UI. Recovery on crash: on launch, find orphaned WAV/session files
  and offer to rebuild (claim inputs atomically by RENAME, not check-then-act; park a
  permanently-unparseable input rather than retrying it forever).
- **Levels drive the waveform** (section 13) via a tiny static meter: per-buffer RMS on the audio
  thread, no allocation, self-decays to zero when reports stop. Two meters: Recording =
  max(mic, loopback); Voice = push-to-talk only, so the Ask wave does not dance to the call.

## 9. ASR

- **Parakeet TDT 0.6b v2, int8, via sherpa-onnx. Whole-file decode per stream. NEVER hand-chunk
  audio** - chunking was an early disaster (boundary words died). One call per stream per
  meeting.
- **Silence is not failure.** An empty Parakeet result means nobody spoke. Whisper fallback runs
  ONLY on actual error, never on empty.
- **Reset recognizer state between meetings.** State bleed produced ghost words.
- **The WER harness is the quality gate**: a self-test flag that runs a known WAV + reference
  text through the REAL pipeline and prints word error rate plus a PER-NAME recall table (names
  are what users notice). Run it after ANY audio/ASR change. Build it early; synthesize eval
  audio with TTS if the owner has not recorded the script yet.
- **Readability pass LAST**: a rules-only repair pass after everything else (dates/times/percent
  to digits, uh/um removed, stutter doubles collapsed). Cosmetics never feed earlier passes.
  Rules only - it must be unable to invent. "Two questions" stays words; "Mm-hmm" stays an
  answer.
- **Parked for the owner**: a multilingual Whisper option in Settings (~500MB) for Hinglish
  meetings. Carry that option to the Mac backlog.

## 10. Name repair (post-ASR correction - port the algorithm)

Decode-time hotwords LOST to post-ASR correction in real testing; keep the architecture:

- Build the name corpus from the calendar attendees + names the user typed + trusted overrides.
- Corpus-wide correction pass over the transcript with CONSERVATIVE guards. The guards exist
  because the first version turned "day" into "Toki" and "David" into a product name:
  length-scaled distance budgets (short words get almost none), detection-gated replacement
  (only rewrite when a name was plausibly SAID), a ~180-word common-word list that is never
  rewritten, vowel agreement for very short names, and sentence-initial capitals treated as
  grammar, not names, unless the same word was heard mid-sentence too.
- Trusted names (user-vouched) get extra reach; everything else stays timid.
- The one-question audit for any corrector change: what is the worst word it could eat?

## 11. Diarisation and speakers (owner decisions, settled)

- ASR-guided: one speaker embedding per ASR utterance (never fixed-window), AVERAGE linkage
  clustering, threshold 0.80, attendee-count prior (the prior EXTENDS evidence, never overrides
  it).
- **NO persistent voiceprints across meetings** - owner privacy decision, do not relitigate.
  Within-meeting only.
- Speaker naming guards (they exist because "Please" and "Otherwise" became meeting
  participants): a name must be validated against the attendee corpus / plausibility checks
  before a cluster is named; unnamed clusters stay "Speaker 2"; the user's rename always wins.
- Diarisation runs AFTER the notes, not before (owner decision - notes speed matters more).
- Embedding model: CampPlus (Apache-2.0) - attribution row required.

## 12. Notes, summaries, and the LLM

- **Qwen3-4B-Instruct Q4_K_M via llama.cpp** (Metal on Mac). Sampler params matter more than
  prompts: temp 0.7, topP 0.8, topK 20, presence ~0.5, NEVER greedy. Validate output, retry
  once, fall back to heuristic.
- **Heuristic extraction is the floor** and is always available - the product must produce notes
  with no model at all.
- **Garbage in, garbage out**: check what the LLM is actually FED before tuning any prompt. Our
  worst summaries were perfect prompts over hallucinated transcripts (see the level lesson).
- **The summary is written from the transcript ALONE.** Never from the model's memory of the
  meeting, never from its own earlier outputs. Never validate model claims against model output.
- **A template is a claim, not a layout**: the LLM may PROPOSE a structure (decisions, actions,
  questions), but the transcript must CONFIRM each section or the section is dropped - an
  unconfirmed template gets filled with invention. This one rule fixed the worst note defect.
- **Typed-notes steering**: what the user typed during the meeting is merged in and steers
  emphasis; user text is sacred - re-attach it on every failure path, and the user's edits
  always win over regeneration.
- **Evidence links**: every bullet can jump to the transcript line that produced it - text jump,
  highlighted. STAY SILENT below the match floor: a wrong evidence link is worse than none.
- **Titles** come from the summary heuristically, validated, with a plain fallback. Never let a
  placeholder title become matchable state (a carry-forward feature once matched placeholder
  titles by substring and dragged strangers' to-dos in).
- **To-dos**: owner/due are validated in CODE, not trusted from the model. Task queries scope by
  DUE date, with provenance kept. A roll-call of attendees is not a to-do list (early bug).
- **Ask (the chatbot)**: FTS5/BM25 retrieval over the local store, streaming tokens, a task
  router, a general-knowledge override, and a persona that answers privacy questions accurately
  ($0, local, no telemetry). Blank-answer guard, emoji/self-name/date-echo sanitizer on output.
  Semantic embeddings search stayed BLOCKED on Windows by the native-runtime conflict; on Mac
  you may be able to run bge-small cleanly - it is on the wishlist.
- **BYO API key**: provider-agnostic OpenAI-format client; Claude via the Anthropic compat
  endpoint with sk-ant- auto-detect; a positive "verified" UX state. Tokens/keys in Keychain.

## 13. Voice and the waveform

- Push-to-talk ONLY (hold a hotkey, e.g. the Windows build used Ctrl+Alt+Space). No wake word,
  no always-on mic. Owner decision.
- **ONE waveform control, used everywhere** (recording bar and Ask). We shipped two different
  fake ones; the owner caught it. Spec that survived: ~3px bars, 3px gaps, full-round caps;
  recording 14 bars / Ask 9 bars are the only two knobs; cosine dome envelope with fixed
  per-bar jitter (integer hash, never Random - determinism keeps tests honest), normalised so
  max scale is exactly 1.0.
- **It must actually HEAR**: level mapping sqrt of a normalised RMS where BOTH ends track
  (noise floor = 10th percentile of ~3s history, reference = 95th percentile) because of the
  38dB mic gap; history ring starts FULL of silence (an empty ring normalises the first loud
  buffer against itself and flatlines the first second). Fast attack (~0.55/tick), slow decay
  (~0.14/tick, ~650ms settle), 30Hz timer, transform-only, zero per-tick allocation. Silence =
  resting dots at 15% with a slow breath, never a fake dance.
- Build the "--wavetest" style feel gate (prints bar heights through silence/attack/talk/decay
  phases) - it caught two defects pure reasoning had approved.

## 14. Storage, calendar, security

- SQLite (WAL) + a human-readable Markdown mirror of every note. Atomic saves (temp + rename)
  everywhere. One FTS query builder, one external-URL-launch path (allowlisted schemes), single-
  instance enforcement per user.
- Sticky notes are real DB rows linked to meetings by EXPLICIT user choice, never by title match.
- Deletion: tombstones; background jobs claim work by rename; "still open from last time"
  carry-forward matches by real identity, never by substring.
- Calendar: Google + Outlook simultaneously, merged agenda. OAuth on a localhost loopback with
  PKCE; refresh tokens in Keychain. The three Microsoft Graph gotchas that break silently are
  documented in the Windows repo (`evidence-audio-and-outlook` memory): timezone format
  (Prefer header), the /me/calendarView vs /events distinction, and delta-vs-poll behaviour -
  reread before wiring Outlook. Outlook needs an Azure app registration (owner does the portal
  part, guided).
- Meeting prep card: editable, hover-surfaced only (never auto-pops), typed content preserved
  through every failure path.

## 15. Verification culture (this is the actual product-quality engine)

- **Self-test flags on the shipped binary**, headless, exit pass/fail. The Windows set to
  recreate: hit targets + label centring + layout-clip detection (`--hittest`), Library perf
  budgets against the REAL db shape (`--uitest`), note editor checks (`--notetest`), arranged
  heights (`--measuretest`), the waveform feel (`--wavetest`), the ENTRANCE invariant
  (`--revealtest`: nothing that animates in may already be at its final value, and - added late,
  after the ghost frame - the hide must compose an empty final frame), frame budget attribution
  (`--fpstest`), both-themes contrast walk (`--themetest`), PNG renders of every view
  (`--render`), ASR/WER/notes/db/calendar/diarisation tests, and a dev seam to walk state paths
  (`--state a,b`) for filming transitions. Tests can never verify GPU/model/audio paths - those
  are run-on-a-real-Mac jobs forever.
- **Every fix ships with a test watched to FAIL against the unfixed code** (revert, run, restore
  - one combined cycle per wave is fine). The two bugs that ever escaped a "green" audit were
  both same-day fixes shipped without tests. Where a fix is untestable (pure visual), say so
  explicitly in the report.
- **Test the product, not a model of it**: our note-editor test passed 20/20 while the sticky
  note was unusable, because the editor was fine and the HOSTING was broken. Ask "which layer
  has no test?" first. Self-tests must produce their REAL artifact at least once; pin ambient
  settings; grep build output for "error", not the compiler-specific "error CS".
- **Instrument the live app early.** Four offline harnesses cleared a hover bug the owner could
  see (it was a contrast bug, the input layer was innocent). A log line plus a burst capture
  beats a day of reasoning.
- **Film it.** The notch CAN be photographed when screen-share-invisibility is off. Burst
  capture at 30-60fps into a contact sheet found, in minutes, bugs that survived three rounds of
  reasoning: the stale-frame corner, the two-Toki overlap, the mid-air animation stalls. The
  owner's own screen recordings, scrubbed frame by frame, are gold - no ffmpeg was even needed
  (we scrubbed with the platform media player). Build the capture script in week one.
- **Beware diagnostics that cry wolf**: our first live-log told the owner three false failures on
  a healthy app (rectangle hit-tests on rounded controls, too-narrow "clickable" definition)
  while missing the real bug (a page auto-closing, unlogged). A wrong diagnostic costs owner
  trust - validate the instrument before trusting its readings.
- **The honest-count rule**: count real independent assertions, not parameterised multiplications
  through case-insensitive code paths. The owner personally audited our test count once.

## 16. Performance lessons

- Warm everything at launch, at idle priority: models (download at onboarding, warm at launch,
  degrade to heuristic), first-open views, the mascot's raster cache. A meeting never waits on
  a download.
- GPU offload is the speed lever for the LLM; auto-detect (on Apple Silicon, Metal + unified
  memory simplifies this). Keep a low-memory mode (one model resident at a time) for 8GB
  machines; the dev machine being beefy must not hide the floor.
- Frame budget attribution BEFORE optimising anything visual (our `--fpstest` split frame cost
  into transparency vs content vs window-morph - optimising the wrong one is the default
  failure).
- Shadow/blur effects inside animated subtrees are the jank king on both platforms.
- BitmapCache-style rasterisation of the mascot (it is vector-heavy) - but pay the raster cost
  off-screen at launch, not on first show.

## 17. Licences and attribution (a legal obligation, not polish)

The "Built with" screen ships or we are in breach of CC-BY: Parakeet TDT (CC-BY-4.0, attribution
REQUIRED), Qwen3-4B (Apache-2.0), whisper.cpp/small.en (MIT), Silero VAD (MIT), CampPlus
speaker embeddings (Apache-2.0), sherpa-onnx (Apache-2.0), llama.cpp (MIT), plus whatever Mac
packages you add. Build the screen early; retrofitting attribution is how it gets forgotten.

## 18. Parked / undecided (carry to the Mac backlog, do not silently build)

- M8 ship path (signed + notarized installer, Sparkle auto-update, license-key gate, onboarding
  download UI) - parked until the product is final.
- Multilingual Whisper opt-in for Hinglish (~500MB model choice in Settings).
- Persistent voiceprints - owner said NO; within-meeting only.
- Wake word / always-on voice - owner said NO; push-to-talk only.
- Per-app "always record" - owner said no.
- Topic trackers, follow-up email drafts, audio-file import, local MCP server, evidence AUDIO
  playback (text jump shipped; audio player deliberately deferred), RAM-tier adaptive scaling -
  all discussed, all parked with reasons in the Windows `docs/backlog.md`.
- Suggestion chips in the notch - VETOED by the owner.
- Drag-to-reorder in the Library - built once, removed by the owner ("does not add much
  productivity"); do not rebuild.

## 19. The bug hall of fame (fastest way to not repeat us)

1. The mic stream was 80x quieter than the meeting stream and the ASR invented half of every
   transcript. Check input LEVELS before blaming models.
2. The name corrector ate the transcript ("day" -> "Toki"). Guards before reach.
3. "Please" and "Otherwise" attended a meeting. Validate names before creating people.
4. The summary was raw transcript; the template was filled with invention. Transcript confirms
   or the section dies.
5. A green suite of 1,800 tests missed every real-meeting failure. The owner's first real
   meeting is the actual test suite.
6. The stop button was pink for months (#FF2D55 is Apple pink; real red is #FF453A/#FF3B30).
7. Font characters can never be centred in discs; inherited line spacing reaches inside discs.
8. Layout systems silently clip oversized children - a 26px button in a 24px row ships with a
   flat bottom.
9. A layered window replays its RETAINED last frame on the next show: never hide before the
   final frame has provably composed.
10. Captured/frozen brushes broke theme switching four times; the fix was making the helper's
    signature take a token key, not a brush. Make theme-wrongness unrepresentable.
11. A refresh handler raced the state transition and animated the wrong state's box. Order of
    notification is architecture, not detail.
12. Instant hide of the mascot next to its replacement = "a corner glitched". Sequence
    hand-offs; fade, then swap; restore on re-entry.
13. Hover that "did not work" was a 1.15:1 contrast wash. Measure contrast, do not assume input
    bugs.
14. The loader that "never minimised" was being un-clipped by a settle that measured the WINDOW
    instead of the card. When two sizes can disagree, decide which one is truth.
15. `GetHashCode` is randomised per process - anything keyed on it recolours on every launch.
    Stable hashes for anything user-visible.
16. Timer-driven transient UI ping-pongs; dwell-and-leave is the grammar.
17. The packaged build is the product: test the published artifact, not just the dev build, and
    remember a stale binary once cost half a day ("dist is stale" - version-stamp the log line).

## 20. Process for the Mac build (recommended order)

1. Repo skeleton + the constitution file + the three living docs + the no-em-dash test.
2. **Prototype the per-app audio tap** (the platform-risk item) with a 20-line CLI: capture Zoom
   audio + mic to two WAVs, confirm permissions UX. If this fails, everything else changes.
3. ASR spike: sherpa-onnx + Parakeet on arm64, whole-file, WER harness with a synthetic eval.
4. The notch window shell: panel, state machine, entrance/exit with the reveal + composed-hide
   self-tests, ghost mode verified against a real screen share, burst-capture script.
5. The design system: tokens, material, one button family with the hover/press grammar,
   `--render` + `--themetest` equivalents.
6. Then the product loop in the Windows order: detection -> record -> transcribe -> notes ->
   Library -> calendar -> Ask -> polish. Small milestones, owner's eyes at every one.

Give the owner the same deal we had: plain language before big changes, real runs as proof,
their eyes as the final gate, their git as the only git. It worked.
