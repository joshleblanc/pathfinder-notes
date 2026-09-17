# Pathfinder 2e Session Recap

> Edit this template to shape the recap. The summarizer fills in every cell,
> row, and section below from the speaker-labeled transcript. Rules:
> no blank cells; every heading must appear in the output even if its body is a
> short "Not mentioned in this session." line; do not invent details.

---

# Session Recap

**Date:** the session date, taken from the transcript; "unknown" if the players never said it
**Captured:** the time the recording was made (wall clock)
**Audio duration:** total seconds of audio

## At a Glance

A two-sentence elevator pitch. What was the party's goal, and what hook do they leave on?

## Summary

Two to four paragraphs of past-tense narrative covering the arc and mood of the session. Don't invent details — summarize only what the transcript supports.

## Encounters

One row per combat or significant skill challenge. Resolve creature level + XP *from the transcript only*. If the transcript never mentions a creature's level or the XP it awards, write "unclear" in those cells rather than inventing.

| # | Encounter | Creatures | Level(s) | XP Awarded | Outcome |
|---|-----------|-----------|----------|------------|---------|
| 1 |           |           |          |            |         |

### Encounter Notes

Short narrative per encounter, in order.

## Treasure

Anything that changed hands, was looted, sold, gifted, or otherwise shifted possession. Capture only what the transcript states; do not estimate prices unless they are said aloud.

### Currency

- the currency that was explicitly mentioned

### Items

- item name (level, rarity) — only if those details are stated

### Other

- favors, debts, map locations learned, downtime activities, and other possessions

## Story Beats

Bulleted, chronological, plot-relevant. Skip shopping, banter, and recap-of-the-recap.

- event one
- event two

## NPCs

Every named character who was mentioned. If the party meets a runtime-named NPC ("the bartender"), capture them too. Always one row per NPC; if a field is unknown, write "unclear" rather than skipping the row.

| Name | Role | Disposition | First Action or Quote |
|------|------|-------------|----------------------|

## Locations Visited

One bullet per distinct place the party went to in this session (including re-entries).

- location name — brief note

## Cliffhanger / Next Session

What the party said they are doing next; how the GM intends to open the next session; any scheduled downtime, contacts to follow up on, etc.

## Mechanics & Advancement

Anything mechanical that happened — even when the transcript doesn't give final numbers. Capture level-ups, condition effects, quest progression, and any rulings the GM made on the fly.

- level-ups:
- conditions & status effects applied or removed:
- treasure mechanically distributed to characters:
- any rules or rulings made ad-hoc (including the GM's reasoning):

## Notes for the GM

Out-of-character moments: ruling debates, player table talk, restroom breaks, anything you want to remember that wasn't a story event. May be a single sentence, or "No OOC notes from this session." if the transcript was entirely in-fiction.

## Out-of-Game / Real Life

Player absences, schedule changes, life updates, or "No real-life updates." if the transcript doesn't contain any.

## Highlight Reel Video Prompt (MiniMax H3)

Below is a ready-to-paste prompt for **MiniMax-H3** in text-to-video mode. The summarizer distills the recap above into a single 15-second cinematic highlight using H3's three required fields. After the recap is generated, copy the filled block, paste it into H3 (model `MiniMax-H3`, T2VA, no reference media, 5–15s, 16:9, 1080P), and a video comes back. For a longer reel, repeat with the next-best moment and stitch the clips — each invocation is one continuous 15-second video.

Hard rules for the summarizer when filling this section:

- Begin `integrated_multimodal_description:` exactly with the token `[Shot 1]` — no preamble, no lead-in sentence, no quotes. Every later shot starts with `[Shot N] At MM:SS.sss, the camera cuts to ...` and timestamps must be strictly increasing inside the 15-second window.
- The whole prompt must stay under 7,000 Unicode code points. If it would exceed that, drop shots from the end; do not abbreviate shots already written.
- Do NOT mention H3, MiniMax, the model name, the duration, the aspect ratio, or "this video" inside the prompt body — those are configured outside the prompt.
- Pull concrete visual details only from the recap above (locations, NPCs, creatures, weather, lighting, props). Do not invent costumes, items, or effects the recap doesn't support.
- Default visual style for this template: cinematic fantasy, painterly, dramatic rim lighting, shallow depth of field. Replace the style clause in `[Shot 1]` if the GM prefers a different look (noir, anime, storybook, woodcut, etc.).
- Pick 3–5 of the most visually striking moments from Story Beats and Encounters. Each shot covers roughly 3–4 seconds. Each shot must include: composition/scale, camera movement (use the H3 vocabulary: Push In/Pull Out, Pan, Truck, Tilt, Arc Shot, Tracking Shot, Static Shot, etc.), subject identity, setting, and the action or state change on screen.
- Speakers in the video use `(S1)`, `(S2)`, … IDs in vocal order. In-fiction dialogue (rare in a highlight reel — usually none) goes inside `<d>[English] ...</d>` tags and only there.
- `overall_soundscape` is 1–4 sentences of diegetic ambience and physical SFX. No dialogue, no music.
- `non_diegetic_music` is 1–3 sentences of audience-only score. Use `N/A` if the GM wants no score.
- The wrapped code block is part of the recap output — the summarizer fills in the three fields inside it and keeps the surrounding ``` fences intact.

```
integrated_multimodal_description: [Shot 1] ...

overall_soundscape: ...

non_diegetic_music: ...
```

---

End of session.
