# Toki gets a design twin, and learns there can only be one of him

Three rounds in one evening.

## The design twin

You asked to rebuild the design using Claude Design. The catch: claude.ai/design builds with web
components, and Toki is a Windows app with none. So Toki's design system got a hand-built web
twin - fifteen pieces (the glass card, every button shape, the chips, the ticks, the meeting
cards, the cloud himself, the whole notch arrangement) using the exact same colours and rules as
the real app, each one screenshot-verified before upload. It lives in your claude.ai/design
account as "Toki Design System", along with a written guide distilled from the Figma file so the
design agent knows the rules before it draws anything.

How to use it: open the project, ask the design agent for a screen. It builds with Toki's real
parts. When you like something, I rebuild it in the actual app - from the Figma frame, in the
app's own language. That's exactly what happened next.

## Ask and Settings, from the frames

You picked the direction (the frames, not the agent's button-obsessed takes), so I pulled the two
frames and rebuilt in the app: the Ask page got its blue-teal glow rising from the bottom, the
four suggestion chips are back (real buttons now), and the prompt bar became one soft capsule
ending in a round send button. Settings became one centred column with the Appearance card -
rounded surface, rows split by hairlines, switches on the right. The frame's "Recording" and
"Permissions" sections were left OUT on purpose: those features don't exist yet, and a switch
wired to nothing is a lie.

## There can only be one Toki

Your check caught it: hold-to-speak showed TWO Tokis - the panel's own built-in one plus the new
floating bubble one. Turns out four panels had leftover built-in Tokis, and the calendar's empty
state had two more. Rule enforced everywhere now: on any surface, the floating bubble Toki is THE
Toki. Also fixed from your list: the bubble no longer overlaps the card (clean 10px gap), the
tiny recording-bar Toki finally scribbles with his pen while transcribing, and the hover-summon
peek sits flush with the top edge again instead of floating below it.

## The cloud seams

The web twin's cloud looked "kinda weird" because each puff of the cloud was shaded separately -
four little light sources instead of one. One shared gradient across the whole shape fixed it;
the app's own Toki always had the smooth version.
