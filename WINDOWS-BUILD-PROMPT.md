# Build Toki for Windows — full project brief

You are a professional software engineer building **Toki for Windows** from scratch in this empty repository. Toki already exists as a native macOS app (separate repo, not available to you); this document is the complete transfer of everything that project learned, decided, and shipped. Treat it as the source of truth. Read all of it before writing any code.

Your first task after reading: create this repo's `CLAUDE.md` from this brief (condensed, same rules), then start Milestone 0.

---

## 1. What Toki is

Toki is a meeting assistant that lives quietly at the edge of your screen. It notices when you're in a meeting, offers to record it, transcribes it, writes clean structured notes, and remembers every meeting in a searchable local Library — **entirely on the user's machine**. One-time **$20**, no subscription, nothing ever leaves the laptop.

The pitch against Granola / Otter / Fathom: they cost $20–30 **every month**, they ship your private conversations to a cloud, and they're heavy apps. Toki is a lightweight native utility, bought once, fully private.

**Who it's for:** people in back-to-back calls who care about owning their data — founders, freelancers, consultants, the privacy-conscious.

## 2. Hard constraints — every decision filters through these

1. **$0 monthly upkeep, forever.** No servers, no AI credits, no cloud costs Toki absorbs. Transcription and summarization are on-device. Optional "bring your own API key" lets users pay their own model provider for sharper summaries — Toki never touches that cost. If a feature needs a recurring cost, it does not belong in the product.
2. **All data is local.** SQLite + files under `%APPDATA%\Toki\`. No account, no backend, no telemetry, no cloud sync.
3. **Toki only activates for meetings.** It does NOT tap system audio globally, ever — no recording YouTube, no "record everything" mode. It detects that a call is happening, asks the user, and taps **only that app's process tree**. A human is always in the loop unless the user explicitly chose "always record". This was an explicit owner decision on macOS — do not relax it.
4. **Minimal permissions are part of the pitch.** Windows requires no permission prompt for loopback audio and only the standard mic consent — keep it that way. Never add anything that triggers a scary system prompt.
5. **Restraint is the design.** Toki appears when it has something worth showing — a detected meeting, finished notes — and stays invisible otherwise.

## 3. The owner and how to work with them

The owner (Sid, they/them) is **non-technical**. They describe desired behavior and outcomes; you write the code.

- Explain plans in plain language before big changes.
- **Verify features by actually running the app and watching the real output appear — never by describing what the code should do.** (Unlike the macOS project, you are building on the OS you target. Run everything, every time. There is no excuse for unverified code in this repo.)
- Ask before making product decisions; recommend, don't assume.
- Never run `git` commands unless asked.
- Build in small milestones, each verified working before the next.
- Keep docs clean and current: `README.md` build log per milestone, `ARCHITECTURE.md` for decisions + rationale, and a `mansplain/` folder that tells the story in plain non-technical language (what we set out to do, what broke, how it was fixed).

## 4. Decided tech stack (confirmed by the owner — do not re-litigate)

| Concern | Decision |
|---|---|
| App framework | **C# / .NET 8 + WPF** — borderless transparent topmost windows are mature in WPF; every library below has C# bindings |
| UI shape | **Pill overlay** (Wispr Flow style), bottom-center of screen — see §6 |
| System ("them") audio | **WASAPI Application Loopback** — per-process-tree capture, Windows 10 2004+ (build 19041). No permission prompt |
| Mic ("you") audio | **WASAPI capture** via NAudio |
| Meeting detection | **Registry mic-in-use signal** (`CapabilityAccessManager\ConsentStore\microphone`), polled — see §7 |
| Final transcription | **NVIDIA Parakeet TDT 0.6b v2 (ONNX, int8) via sherpa-onnx** — the same model the Mac app uses on the Neural Engine; runs faster than real-time on CPU |
| Live preview transcript | **sherpa-onnx streaming model** (small streaming Zipformer) — preview only, never what gets saved |
| Transcription fallback | **whisper.cpp `small.en`** — fires **only when Parakeet errors, never on silence** (see lesson 4) |
| Notes LLM | **Qwen3-4B-Instruct-2507, Q4_K_M GGUF via LLamaSharp (llama.cpp)** — Vulkan backend (works on NVIDIA/AMD/Intel GPUs), CPU fallback |
| Notes floor | Deterministic heuristic extraction — always available, instant, free |
| Optional sharper notes | Bring-your-own-API-key (OpenAI/Anthropic-compatible), user's cost |
| Storage | **SQLite** (`Microsoft.Data.Sqlite`) at `%APPDATA%\Toki\toki.db` + Markdown mirror in `%APPDATA%\Toki\Notes\` |
| Calendar | **Google Calendar OAuth** (loopback redirect to `127.0.0.1`, no backend; tokens encrypted with DPAPI). Outlook/Graph is a fast-follow, not v1 |
| Hotkey | `RegisterHotKey`, default **Ctrl+Alt+Space** (plain Alt+Space is reserved by Windows), user-configurable |
| Tray | `H.NotifyIcon.Wpf` status icon (open library, settings, quit) |
| Distribution | Code-signed installer + **Velopack** auto-updates served from GitHub Releases (free). Licensing via Gumroad or Lemon Squeezy key — zero backend |
| Model downloads | From Hugging Face CDN, **all downloaded during onboarding with a progress UI, and re-warmed at every launch** — a meeting must never stall on a download (see lesson 2) |

Verify exact package names/APIs against current docs when you start (sherpa-onnx NuGet, LLamaSharp + `LLamaSharp.Backend.Vulkan`, NAudio, Velopack). Microsoft's `ApplicationLoopback` sample is the reference for process-tree loopback interop.

## 5. Feature spec (v1)

### Capture
- Invisible until needed. Surfaces when: a meeting is detected, the hotkey is pressed, or the tray icon is clicked.
- Records any meeting — detected app (Meet in a browser, Zoom, Teams, Discord, anything) or mic-only "Quick note" if no call app is found (with an on-screen "mic-only" notice, never a silent global tap).
- Two streams: `you` = mic, `them` = the meeting app's process tree via loopback. This gives free two-speaker attribution.
- Live streaming transcript in the expanded recording view so the user can see it's working.
- Auto-record setting: **ask** (default) / **always** / **never**.
- Flag a moment mid-meeting (star button → marker in the transcript).

### Understand
- On Stop: one whole-file Parakeet pass per stream → merge by timestamp → cross-stream echo de-dup → notes.
- Notes shape (matches what Granola/Circleback produce): **TL;DR → What was discussed (topic blocks with descriptive headings) → Decisions → To do (owner + due date) → Open questions.** Three focused LLM passes, not one; the TL;DR is written **last, from the other sections**.
- Notes are editable; action items check off; owner/due-date assignable.
- **Model selection** in Settings: Qwen3-4B (best) / Qwen3-1.7B (low-spec machines) / heuristic-only / BYO API key. Download-on-select with progress.

### Remember & act
- **Library**: searchable list of all meetings; transcript view with `you:`/`them:` speaker lens; Markdown export; delete with confirmation.
- **Manual notes (NEW — not in the Mac app yet):** a "New note" button in the Library creates a note with no recording — title + freeform Markdown body, fully editable, searchable alongside meeting notes, optionally linked to an upcoming calendar event (so pre-meeting prep lives where the meeting's notes will land). Schema: same notes table with a `kind` column (`meeting` / `manual`).
- Pre-meeting prep card: ~2 minutes before a calendar event, surface last time's notes, still-open action items, and any linked manual note.
- Notes reconnect to their calendar event.

### Onboarding & fit
- First-run: 3-step intro → **model download screen** (Parakeet ~700MB-class + Qwen3-4B ~2.4GB, with sizes and progress shown, skippable to heuristic mode) → optional Google Calendar connect → mic consent.
- Settings: auto-record mode, model selection, hotkey, echo-cancel toggle (see lesson 6), **"Built with" attribution screen** (see lesson 11).

### Not in v1 (deliberately)
Team/shared workspaces · CRM/Notion/Slack integrations · "Ask Toki" chat across meetings · mobile · per-remote-speaker diarization · Outlook calendar. Keep the product tight and single-user.

## 6. The pill UI and the character

Toki's identity is a small character with personality, but **it only appears when it has something worth showing**. The working surfaces (notes, library) stay calm and fast; personality lives in transitions and arrivals.

- **Idle:** nothing visible at all (or a sliver of pill if the user pins it — setting).
- **Meeting detected:** the pill slides in at bottom-center: *"Zoom is in a call — record it?"* with Record / Not now. Dismiss = 10-minute snooze for that app.
- **Hover on the pill:** the Toki character peeks out of the pill (the signature moment — like Wispr Flow's pill, but alive). Click expands to the Home surface.
- **Recording:** compact pill with timer + live-transcript toggle + star + Stop.
- **Notes ready:** pill expands and the character "sets the notes down"; the notes panel opens.
- **No speech captured:** a friendly empty state ("Nothing was said — nothing saved"), and **nothing is written to the Library**.
- WPF specifics: `WindowStyle=None`, `AllowsTransparency=True`, `Topmost=True`, `ShowInTaskbar=False`, per-monitor-DPI aware, animated width/height morphs between states. A single window that morphs, not multiple windows.
- The Library opens as a separate resizable window (from Home and from the tray).

State machine (port this exactly): `hidden → meetingPrompt → recordingBar ⇄ recordingOpen → summarizing → notes | noSpeech`, plus `home`, `library`, `settings`. One `AppState` class owns it; views only read it.

## 7. Meeting detection — how it must work

The signal is the microphone: a call is essentially the only everyday reason another app captures the mic.

- Poll `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone` every **2.5s**: subkeys are packaged apps; classic apps live under `NonPackaged` with `#`-encoded exe paths. `LastUsedTimeStop == 0` (QWORD) → that app is using the mic **right now**. This needs no permission and gives you the app's identity. (Cross-check with `IAudioSessionManager2` session enumeration on capture endpoints if the registry proves unreliable on some SKUs.)
- **Denylist, not allowlist.** Any app can host a meeting, so treat every mic user as a candidate and exclude known non-meetings: dictation tools (Wispr Flow, superwhisper-likes), screen recorders (OBS, Loom, ShareX, Snagit), voice recorders, audio production (Audacity, FL Studio), AI IDEs/desktop assistants (VS Code, Cursor, ChatGPT, Claude), Toki itself. Substring match, case-insensitive, conservative — a spurious prompt is dismissible; a missed meeting is lost forever.
- **Debounce:** a candidate must persist 2 consecutive polls (~5s) before prompting. **Cooldown:** a dismissed app isn't re-suggested for 10 minutes. **Auto-stop:** while recording, watch the target; if it releases the mic for ~3 polls (~7.5s), the call ended — stop and summarize automatically.
- Log every change in the mic-using-app set. "It didn't prompt" is only diagnosable if the log shows what detection saw.
- Never prompt while Toki is busy (already recording, or user is reading notes).

## 8. Audio capture — how it must work

- **"Them":** WASAPI Application Loopback via `ActivateAudioInterfaceAsync` + `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK` with `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE`, targeting the detected app's root PID. The include-tree flag is what captures browser meetings (Meet's audio lives in a renderer child process) — Windows solves natively what took the Mac app weeks. Rebuild the capture if the target's process tree changes materially; handle device changes (AirPods/headset swap mid-meeting) by rebuilding on default-device-change notifications (`IMMNotificationClient`).
- **"You":** WASAPI mic capture (NAudio), default device, shared mode.
- Write each stream to its own WAV; timestamps are meeting-relative.
- **Echo:** the mic physically hears the far end through laptop speakers, so remote speech can appear on both streams. Primary fix is the **cross-stream text de-dup** after transcription: within a ±4s window, drop a `you` line whose normalized text matches a `them` line (for lines under 3 significant words, require an exact normalized match — fuzzy matching on short interjections like "yeah" wrongly deletes real speech). Windows AEC (voice-capture DMO / audio effects) is an **opt-in Settings toggle, off by default** — on macOS the OS echo canceller was fragile across engine restarts and caused intermittent silent-mic failures; assume the same risk here until proven otherwise.

## 9. Lessons the macOS build paid for — do NOT relearn these

1. **Garbage in, garbage out.** Months were lost blaming mediocre summaries on prompt engineering while the real cause was Whisper `base.en` getting ~1 word in 4 wrong. **Before tuning how the model thinks, check what it's being fed.** That's why the ASR is Parakeet and why `--asrtest` exists.
2. **A meeting must never wait on a download.** The Mac app's "Parakeet is slow" bug was actually a first-run model download + engine compile happening *inside* the first meeting. Download everything at onboarding, warm (load) engines at every launch, and if a model isn't ready, degrade gracefully (heuristic notes) — never block.
3. **Do not chunk audio yourself.** The Mac app hand-chunked long recordings into 10-minute pieces and lost words at every seam. Modern ASR toolkits chunk internally with overlapping windows. One whole-file call per stream. If sherpa-onnx needs help on very long files, use *its* documented long-audio path, not hand-cut segments.
4. **Silence is not failure.** An empty transcription result from Parakeet means nobody spoke — trust it (Parakeet can't hallucinate on silence). Fall back to Whisper **only on an actual error**. On the Mac, treating silence as failure triggered a surprise 500MB Whisper download after every quiet meeting.
5. **Reset decoder/recognizer state between meetings.** Leaked state garbled every second meeting on the Mac. Fresh state per transcription call, always.
6. **OS echo cancellation is fragile; keep it opt-in** (§8). And never manually re-bind audio devices on a voice-processing-enabled node — the Mac version of that mistake made the mic silently fail *intermittently*, the worst kind of bug.
7. **A silent meeting saves nothing.** No hollow "No speech was captured" notes in the Library. Return null, show the friendly empty state.
8. **LLM sampler params matter more than prompts.** Default samplers (topP 1.0 / topK 0) made 4-bit Qwen drift into Chinese and collapse into `!!!!!!!!`. Use Qwen3's official params: temperature 0.7, topP 0.8, topK 20, presence penalty ~0.5, **never greedy/temp-0** (Qwen's own card says greedy causes endless repeats). Prefix prompts with "Respond in English only." Validate output: reject anything containing CJK/kana/Hangul codepoints or 6+ repeats of one character; retry once; then fall back to the heuristic. Never show the user degenerate output.
9. **Titles come from the summary, heuristically** — no separate LLM title pass. Validate with the same garbage checks (≥3 letters, mostly ASCII letters, no `!!!` runs); fall back to "`<app name>` — `<date>`".
10. **Meeting duration is wall-clock** (`end - start`), not the last UI timer tick.
11. **Attribution is a licence obligation, not decoration.** Parakeet (and any diarization models) are **CC-BY-4.0**: selling Toki is explicitly allowed *on the condition that the authors are credited*. The "Built with" screen in Settings is what discharges that. Qwen3 is Apache-2.0 (include the notice). Whisper models are MIT. Ship the acknowledgements screen or you are distributing these models in breach of their licences.
12. **A self-test that has never produced its real artifact is not a test.** The Mac app's model-download test printed a confident success message for a download that had never happened; a finished-looking audio capture file existed for months without ever being called. **Code that exists is not code that runs. Verify by watching the real output appear** — a WAV you can play, a transcript that matches what was said, a note you can read.
13. **`kind`-of-build gotchas are real** — on the Mac, the release script silently produced broken binaries for weeks. Whatever your packaging path is, make the packaged artifact part of verification: test the installed app, not just `dotnet run`.

## 10. Self-test CLI flags (build these as you go)

The exe must support headless self-tests that exit with pass/fail — they're how every milestone is verified and how the owner tests without understanding the code:

- `--detecttest` — 30s: print every app currently using the mic and how the denylist/cooldown filters it. (Verify: start a Meet in the browser, see it listed as a candidate.)
- `--loopbacktest` — detect the current call, tap its process tree for 15s, write `them.wav`, print peak levels. (Verify: play the file, hear the other side.)
- `--mictest` — meter + record the mic for 10s, write `you.wav`. (Verify: hear yourself.)
- `--asrtest <file>` — transcribe a long recording in one pass; print the transcript head **and tail** plus timing. (Verify: the tail matches the end of the audio — proves no mid-file truncation.)
- `--notestest` — canned transcript with a planted echo pair, planted decisions/actions/questions → run echo de-dup, notes, and title; flag CJK/repetition garbage; check the planted items were extracted.
- `--llmtest` — load the selected GGUF, generate against a fixed prompt, run the degeneration validator.
- `--dbtest` — SQLite round-trip: insert, search, update, delete a meeting and a manual note.

## 11. Build order — small milestones, each verified before the next

Ship infrastructure first; polish and the full character system come after the core works. **De-risk the hardest unknown early.**

- **M0 — Scaffold.** Repo layout, `CLAUDE.md` (from this brief), .NET 8 WPF project, CI (build + unit tests; note CI can never verify GPU/model paths — those are run-on-a-real-PC jobs forever), `README.md` build-log format.
- **M1 — Pill shell.** Borderless topmost pill window, the full state machine with placeholder content, morph animations, tray icon, hotkey. Verify: click through every state on screen.
- **M2 — Audio spike (the de-risk milestone).** WASAPI process-tree loopback + mic capture → two WAVs. `--loopbacktest`, `--mictest`. Verify by ear against a real browser Meet call. *Nothing else proceeds until this works.*
- **M3 — Meeting detection.** Registry poll, denylist, debounce/cooldown, prompt flow wired into the pill, auto-stop. `--detecttest`. Verify: join a real call, get prompted; dismiss, confirm the snooze; record, hang up, watch auto-stop.
- **M4 — Transcription.** sherpa-onnx Parakeet whole-file per stream, streaming preview model, whisper.cpp failure-only fallback, echo de-dup, merge. `--asrtest`. Verify with a scripted 2-person call: `you`/`them` attributed correctly, echo lines appear once, tail of transcript present.
- **M5 — Storage + heuristic notes + Library.** SQLite schema (meetings, notes, action items, manual notes with `kind`), heuristic extraction, Library window with search/edit/export/delete-confirm, **manual "New note"**, Markdown mirror. `--dbtest`, `--notestest`.
- **M6 — LLM notes.** LLamaSharp + Qwen3-4B, three-pass notes (TL;DR last), sampler params + validators from lesson 8, model-selection setting, BYO-key path. `--llmtest`. Verify against the same scripted call: decisions/to-dos/questions match what was actually said.
- **M7 — Calendar.** Google OAuth (loopback), event list on Home, join-and-record, prep card, auto-record rules, manual-note↔event linking.
- **M8 — Ship shape.** Onboarding with model downloads, settings complete, attribution screen, Velopack packaging + signed installer, license-key check. Verify the *installed* app end-to-end (lesson 13).
- **M9 — Character & polish.** Hover-peek animation, arrival moments, time-of-day moods — only the restrained set (§6); nothing that appears without a reason.

After every milestone: update `README.md` (what was built, how it was verified, what broke), `ARCHITECTURE.md` (any decision made), and `mansplain/` (the plain-language story).

## 12. What "working" looks like

A user joins a real Google Meet in their browser. Toki's pill slides in and asks. They say yes. They talk for 20 minutes. They hang up. Toki stops itself, and 30–60 seconds later hands them notes where the TL;DR is true, the decisions are the ones they made, the to-dos have the right owners, `you`/`them` labels are right, nothing is duplicated by echo, the title is a sensible English phrase, and the duration is correct. Next morning they search the Library and find it — next to the prep note they wrote themselves before the call. Nothing ever left their machine, and nobody bills them next month.
