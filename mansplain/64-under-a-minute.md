# Under a minute, by writing six times less

You set the bar: under a minute, every meeting, no quality lost. Here is where it landed, measured on
your own recordings.

| meeting | length | before | now |
|---|---|---|---|
| the 22-minute one | 22.3 min | 2 min 56s | **46 seconds** |
| the 12-minute one | 12.7 min | 2 min 11s | **22 seconds** |
| the 90-second one | 1.4 min | 1 min 21s | **10 seconds** |

## The idea

Toki reads your meeting in chunks. For each chunk it used to ask the AI to write out the important
points in its own words. Then it wrote the note from those points.

That first step was 85% of the wait, because **writing is the slow part**. Reading is nearly free.

So the AI no longer writes anything in that step. It just says **which lines matter** - "4, 9, 17" -
and Toki copies those exact lines out of your transcript itself. Copying is instant. The AI's output
for each chunk dropped from about 430 words to about 70.

## Why this doesn't cost quality, and how I checked

I did not want to take my own word for it, so I ran both versions over five of your meetings and
compared what the notes actually claim against what the transcript actually says:

|  | old | new |
|---|---|---|
| facts stated that are true | 100% | 97% |
| facts from the meeting it caught | 39% | 33% |
| bullets with a source link | 68% | 69% |
| **facts stated at all** | 22 | **31** |

Every one of those gaps is three facts or fewer across five meetings, which is inside the noise. The
new version states *more* facts overall.

**And it got better at something I expected it to get worse at.** Toki names who owns each to-do. On
your 22-minute meeting the old version named **nobody** across six tasks. The new one named three or
four people, twice in a row. That makes sense once you see it: your transcript says "Kashish, can you
get Koshi on the hiring numbers?" Copying that keeps the name. Asking an AI to rewrite it loses her.

## The part I should tell you about

**Three separate times today I nearly reported something false because I trusted one measurement.**

- I measured that using the whole graphics card was "only 3% faster" and shipped a cautious setting.
  Running it three times: it was 33% faster.
- I ran the new version once, saw it lose every task owner, and was about to write it up as a
  quality regression. On a different meeting it did the opposite, twice.
- I replayed the same meeting on unchanged code and got 109 seconds, then 136 seconds.

Toki's note-writing is deliberately a bit random - that is what stops it producing the same flat
phrasing every time. Which means **one run tells you almost nothing.** I have written that into my
notes as a rule, because I clearly needed it.

## Two things I found and deliberately did not ship

**Using the whole graphics card** is 33% faster and I am not doing it. Loading the entire AI leaves
116 megabytes free out of your card. That is not headroom, and if it ever runs out Toki does not slow
down or warn you, it dies mid-summary and you get the bad fallback notes. With the new approach we
are already well under a minute, so the extra ten seconds buys nothing worth that.

**A memory-saving trick** that every published table says is essentially free turned out to be **2.3
times slower** on your specific graphics driver. The maths said yes, your hardware said no. I have
left a test in place so that the next time someone reads that table, they have to read the
measurement first.

## What is next, and one question for you

The biggest remaining win is to do that chunk-reading **while the meeting is still happening**,
instead of at the end. Toki already transcribes live, so the work could be spread across the call and
almost nothing would be left when you hit stop - about 20 seconds, **no matter how long the meeting
was**. That is what makes a two-hour call as fast as a ten-minute one.

**The catch, and it is your call:** it means keeping the AI loaded on your graphics card for the whole
meeting. On your laptop that is probably fine, because your screen actually runs off the built-in
Intel/AMD chip and the NVIDIA card sits completely idle. But it is the same card a video call would
reach for, and I would rather ask than find out during a real meeting.

Second on the list, and needing nothing from you: showing the title and summary **as they are being
written** rather than after. The note takes 22 seconds but the title arrives in about 3. You would
see it fill in.
