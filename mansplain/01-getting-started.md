# Step 1 — Getting started

*July 15, 2026*

## What we set out to do

Before building anything visible, we needed a working workshop: the tools to build a Windows app, an empty app that actually opens, and a way to automatically check our work as the project grows.

## What we did

1. **Installed the toolbox.** This PC didn't have Microsoft's app-building toolkit (called ".NET") yet. We installed it. Free, official, from Microsoft.

2. **Created the empty app.** Think of it as pouring the foundation of a house — there are no rooms yet, but it stands. The computer confirmed it builds with zero errors.

3. **Created the testing rig.** A second small program whose only job is to test the first one. Every time we add a feature, we also add automatic checks here, so old features can't silently break while we build new ones. It runs, and its first (trivial) check passes.

4. **Wrote the project's memory.** Three documents so nothing gets lost between sessions: the build log (what was done and how it was proven to work), the decisions log (what we chose and why), and this folder (the story in plain words).

## What broke

One small thing: the testing rig came out of the box set up for "any computer," but Toki is a Windows-only app, so the two couldn't connect. We set the testing rig to "Windows-only" too. Thirty-second fix.

## The design arrived

Sid shared the product design — pictures of every screen from the Mac version, where Toki hangs from the notch at the top of the screen. Windows laptops don't have a notch, so on Windows Toki will be a **small pill floating at the bottom-center of the screen** instead. Same personality, same dark look, same pink character — it just grows upward from the bottom instead of downward from the top.

## What's next

**The pill itself.** A little rounded window that floats above everything, can't be accidentally dragged into a corner, and knows how to smoothly change shape: tiny while idle, a question ("Record this meeting?") when a call starts, a timer bar while recording, a notes panel when done. No real recording yet — first we make the shell feel right.
