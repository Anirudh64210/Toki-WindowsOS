# Bring your own AI key - cloud-speed notes, if you want them

## What we set out to do

On-device notes keep every meeting private, but on a modest PC they're slow - a long meeting can take a while to write up. The last chapter explained why, and the honest fix: if you already pay for a cloud AI (Claude, OpenAI, and so on), let Toki borrow it. Your notes come back in seconds.

The rule never changes: **it's your key and your cost.** Toki doesn't run a server, doesn't pay for anything, and doesn't take a cut. This is a door you can open if you want speed, and ignore completely if you'd rather keep everything on your own machine.

## How it works now (the simple version)

Open **Settings → "My API key"** and you do three things, top to bottom:

1. **Pick your provider** from a dropdown - Claude, OpenAI, Groq, OpenRouter, or "Custom" for anything else.
2. **Paste your key.**
3. **Pick a model** from a list.

That's it. You never type a web address, and you never have to remember the exact model name - Toki fills the address in for you and fetches the current list of models straight from your provider, so it's always up to date.

## The thing I got wrong about addresses (and you caught it)

You asked a sharp question: *"isn't `https://` the same for everyone?"* Yes - and I'd been sloppy explaining it. `https://` just means "this is a secure, encrypted connection." It's the same prefix on every website. What's different is the **address** after it - the same way every site starts with `https://` but `google.com` and `amazon.com` are different places. Each AI provider has its own address, and it's fixed - it never changes between users. So the right design was exactly what you said: nobody should ever type an address. You choose the provider; Toki knows where to knock.

## Your key is guarded, seriously

An API key is like a credit card - anyone who has it can spend your money. So Toki treats it that way:

- It's **encrypted on your PC**, locked to your Windows account. Even someone who copied the file couldn't read it on another machine.
- It's stored **only** in Toki's private folder - never inside the project, never in a log, never written down in plain text anywhere.
- It only ever travels over an encrypted connection, and it's scrubbed out of any error message Toki might show.

We double-checked all of this before saving the project to the cloud: not a single trace of a real key anywhere it could leak.

## A green light that means something

There are two "connected" messages you might expect - but showing both would be clutter, and you called that out. So there's now **one** line with a small dot next to it. The dot only turns **green and gently pulses** after you tap **Test connection** and Toki actually reaches your provider and gets a reply. It never pretends to be connected just because a key is sitting in the box. If you want out, one tap on **Remove key** wipes it and puts you back on the free on-device model.

## Two false starts worth being honest about

- I first quietly built the whole thing around Claude - defaulting to it, pre-picking a Claude model - because that's what happened to be saved from an earlier test. That's not right; no provider should be the favourite. It's now completely even-handed: you pick, and nothing is assumed.
- I first tried a spinning "wheel" picker (like the time-picker on a phone). It looked clunky and made the panel too tall. Replaced with clean, compact dropdowns that stay small no matter how long the model list is.

And one real mistake: while taking screenshots to check my work, my tool was poking Toki's live settings and accidentally **erased your saved key**. You had to paste it back in. That one stung - I've since made the screenshot tool completely hands-off (it can't touch your settings or the internet), and written the lesson down so it can't happen again.

## Where it stands

Pick a provider, paste a key, choose a model, hit Test - green dot, and your notes come from the cloud in seconds. Or don't, and Toki keeps doing everything privately on your PC. Your choice, every time.
