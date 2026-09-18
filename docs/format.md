# The VideoNow disc format

This is what `vxp` implements, derived by analysing retail VideoNow XP discs, two
original black and white discs and two discs that mix Color and XP tracks, and
cross-checked against [PVDTools](https://github.com/saramibreak/PVDTools), the earlier
C decoder by saramibreak, and the format notes it grew out of.

Everything in the "Established" sections is confirmed against real discs. Everything in
"Not yet established" is flagged as such — the emulator is written so those parts can be
corrected without disturbing the rest.

## The disc

A VideoNow disc is a physically small CD pressed as an ordinary **audio CD**. The player
has no filesystem and no MPEG decoder: it reads raw CD-DA sectors and feeds them almost
directly to an LCD controller and a DAC.

- Sectors are the usual raw **2352 bytes**, at **75 sectors per second**.
- That gives a constant disc byte rate of **176 400 bytes/s**.
- Each disc holds up to 99 tracks. Titles use tracks as the unit of narrative structure:
  one track per scene, and interactive titles cut alternative branches as separate tracks.
- A trailing `fill` track usually pads out the disc and contains no video stream.
- Some tracks carry a valid stream in which **every frame is blank**: all-zero pixels
  (black) and audio pinned at `0x80` (silence), under an ordinary `Linear` header. On
  *Batman vs The Joker* tracks 3 and 4 are 217 frames of this, sitting between the title
  sequence and the first choice, and the title's register `0x4F` names track 5 — the
  disc's own pointer steps over them. Paged through, or played in disc order, they look
  like a hang, so `vxp` passes over them in disc order and track skipping as it does
  fill, and still plays one if it is selected directly. `vxp map`, `vxp tracks` and the
  track browser mark them `[blank]`, and the branch graph leaves them out of disc order. Blank segments that offer a
  choice (Batman tracks 22 and 23) or redirect are not skipped. Tracks that are black but
  carry sound, such as Teen Titans track 2, are content and are not skipped either.
- The four linear discs each hold a 181-frame black track with a little sound just
  before their menu (Kids Next Door 16, Jimmy Neutron 19, the Teen Robot discs 16 and
  20). Nothing branches to them and every `0x4F` steps past them, so a disc played the
  way it declares never shows them.

Ripping tools produce a cue sheet plus either one `.bin` per track or a single `.bin`.
`vxp` handles both. The cue sheet's `TITLE` fields often preserve the mastering source
file names (`04-Something_CODE.vn5`), which is pleasant metadata but carries no playback
meaning.

## Interleaving (established)

The bytes on the disc are not audio samples. The stream is split in a fixed ratio:

```
| 9 video bytes | 1 audio byte | 9 video bytes | 1 audio byte | ...
```

So of the 176 400 bytes/s, nine tenths are video and one tenth is audio: exactly
**17 640 audio bytes a second** at one-times CD speed. The XP reads at twice that, so its
sound plays at 35 280 Hz (see [Audio](#audio-established)).

That is Color and XP. The original black and white discs use four-byte groups instead;
see [Black and white](#black-and-white-established).

## Frames (established)

The stream is divided into fixed-size frames. The frame size is the one place where the
disc variants differ:

| Variant | Frame on disc | Video | Audio | Header | Sync words | At 1x CD | Played at |
|---------|--------------:|------:|------:|-------:|-----------:|---------:|----------:|
| Color   | 19 600 B      | 17 640 B | 1 960 B | 360 B | 24 | 9.0000 fps | 18.0000 fps (2x) |
| XP      | 19 760 B      | 17 784 B | 1 976 B | 504 B | 12 | 8.9271 fps | 17.8543 fps (2x) |
| Black and white | 11 760 B | 5 880 B | 2 940 B | 1 340 B | none | 15.0000 fps | 15.0000 fps (1x) |

Color and XP carry the same **144 x 80** picture; XP's improvement was a larger, better
screen and longer running time, not more pixels. In both cases:

```
video bytes = header + 17 280 bytes of packed pixel data
```

The frame rate follows from arithmetic rather than being stored anywhere:
`176 400 / 19 760 = 8.9271 fps` for XP at one-times CD speed, and twice that, 17.8543 fps,
at the speed the XP reads. Audio is the honest master clock — one frame is exactly 1976
samples, or 56.009 ms at 35 280 Hz.

### Finding frames

Every frame header opens with a nine-byte sync word repeated back to back:

```
81 E3 E3 C7 C7 81 81 E3 C7
```

Because it sits in the video half, consecutive copies appear every ten bytes in the raw
stream, with an audio byte between them. **Counting the repeats is how Color and XP are
told apart**: 24 repeats means Color, 12 means XP. Retail tracks begin exactly on a frame
boundary, but `vxp` searches for the sync word rather than assuming it.

## Audio (established)

Unsigned 8-bit mono PCM, 17 640 bytes to a second of disc at one-times CD speed, played
at 35 280 Hz on XP and Color. Silence is `0x80`. There is no compression and no in-band control
data.

> **The XP plays at twice CD speed.** 17 640 Hz is the rate at one-times CD speed, and
> at that rate the retail XP discs run twice as long as they should: the two cartoon
> segments on the Jimmy Neutron disc (tracks 4–10) come to 45.2 minutes, where the
> broadcast segments are about 11 minutes each. At double speed, **35 280 Hz** and
> **17.854 fps**, they come to 22.6 minutes, and the sound is right by ear. Pitch alone
> could never show this, since a factor of two is exactly one octave: at 35 280 Hz the
> theme music sits within a few cents of the studio's own uploads, as it did at 17 640.
> A rate 0.8 % higher (35 568 Hz, an even 18 fps) would put it 14 cents off them, so
> exactly 2x is preferred.
>
> A music disc settles it independently. On *Aly & AJ: No One* the music video (track 2,
> 3 173 frames) runs **2:57.7** at 35 280 Hz. The released song runs 2:58. At 1x it would
> be 5:55, and at an even 18 fps 2:56.3.
>
> `vxp` keeps the two apart: the stream facts (1976 audio bytes a frame, 17 640 a second
> of disc at 1x) stay as they are, and a playback rate on top of them
> (`FrameLayout.PlaybackSampleRate`, the stream rate times the variant's disc speed) is
> what every time is shown and written in. Running times in `vxp info`, `tracks` and the menus, the
> position in the overlay, `vxp run` durations and `--max-seconds`, and every WAV that
> `audio`, `export` and `run --wav` write are at 35 280 Hz for XP and Color (44 100 Hz for black and white), so a WAV plays at the
> right speed. Live playback answers to the `emulation.discSampleRate` setting (`--rate`),
> whose default is that same 35 280, so it can be trimmed until hardware confirms it.
>
> **Color frames play at 2x too.** `vxp` reads the Color format at twice CD speed,
> **35 280 Hz** and **18.000 fps**, on the strength of two discs that put Color-format
> tracks beside 2x content:
>
> - *Aly & AJ: No One* comes from a VideoNow Color archive. Its content is XP-format
>   frames and plays at 2x (the song's running time, above). Its end-of-disc clip
>   (track 5, `eod.vn5`) is Color-format, and its fill track is named `vnc_dummy_30min`,
>   which reads as a VideoNow Color mastering name.
> - *Thomas & Friends: Thomas' Rescue Adventures*, a VideoNow Jr disc, opens with a
>   Color-format "VideoNow COLOR" logo of 106 frames. The XP discs' XP-format logo is also
>   106 frames long, so the two last the same time only if both play at the same speed.
>   The Color logo is followed by XP-format episodes whose narration sounds right at
>   35 280 Hz: the envelope of track 3's sound swells and fades about 4.1 times a
>   second, the pace of syllables in ordinary speech, where 1x would make it about 2.
>   It ends with two more Color-format tracks (12 and 13).
>
> So a player of the Color era decodes XP-size frames and reads at 2x, and Color-format
> tracks share discs with 2x content. Every Color track examined is short (logos, an
> end-of-disc clip, closing cards); what remains unchecked is a disc whose programme
> itself is Color-format, and a real Color player.
>
> **Mixed discs.** A disc takes its variant from the majority of its tracks, but each
> track is read and timed with its own layout. A Color track on an XP disc, such as Aly &
> AJ track 5 (102 frames, 5.67 s), shows the same running time in `vxp info`, the track
> browser and a `vxp run` status, and plays at its own 18 fps. Were a track's rate ever
> to differ from the disc's, the player would resample it to the disc's rate; with Color
> and XP both at 35 280 Hz, none does.

Note that PVDTools writes its WAV header as two channels; that appears to be a slip, and
its own header fields are internally inconsistent about it. Sample-to-sample correlation
on real discs decays smoothly (r = 0.98 at lag 1, 0.93 at lag 2, 0.87 at lag 3), which is
what a single mono signal looks like and not what interleaved stereo looks like.

## Picture (established)

17 280 packed bytes hold 144 x 80 pixels at **three 4-bit channel samples per pixel**.
Two packed bytes hold four channel samples; six packed bytes hold four whole pixels.

The samples are not stored left to right. A displayed row is split across **two 108-byte
half-rows**, and four output pixels are assembled from three bytes of each. Writing `a0
a1 a2` for the upper half-row bytes, `b0 b1 b2` for the lower, and `lo`/`hi` for nibbles:

```
pixel 0 = (a0.lo, b0.lo, b0.hi)
pixel 1 = (b1.lo, a0.hi, a1.lo)
pixel 2 = (a1.hi, b1.hi, b2.lo)
pixel 3 = (b2.hi, a2.lo, a2.hi)
```

This zig-zag reflects how the panel's column drivers were wired. 4-bit samples expand to
8 bits by nibble replication, so `0xF` becomes `0xFF`.

The three samples map to R, G, B in that order. This was confirmed by decoding the disc's
own logo and title sequences, where the expected colours are known. `vxp export
--swizzle` can render the other five permutations if you want to check for yourself.

> PVDTools has a bug here: in its pixel loop the seventh output byte takes its high
> nibble from `video[idx]` instead of `video[idx + 1]`, which tints one channel of every
> third pixel. `vxp` uses the corrected form.

Fine colour speckle in decoded frames is in the source, not the decoder: the mastering
tools dithered to 4 bits per channel, which is invisible on a 1.8-inch panel and obvious
at 5x on a monitor.

### The pixels are not square

144 x 80 is the shape of the *storage*, not of the picture. Drawn with square pixels a
frame comes out at 1.80:1 and everyone in it is about a third too wide. Two independent
lines put the real pixel aspect near **0.72**, and `vxp` defaults to **0.74**:

- The titles are 4:3 broadcast animation. Putting a 144 x 80 frame back at 4:3 needs
  `(4/3) / (144/80)` = **0.74**, and at that ratio faces and figures come out right.
- The Color player's screen is quoted as 1.85 x 1.45 inches, which is 1.28:1. Spreading
  216 x 160 panel dots over it gives nearly square dots, and one image pixel spans 1.5
  dots across and 2 down, so `(1.5 x 1.85/216) / (2 x 1.45/160)` = **0.71**. That lands
  the picture at 1.28:1 — the shape of the screen.

Set `video.pixelAspect` to `1.0` to see the stored grid instead, which is what you want
when studying the layout and not when watching anything.

### Where the published resolutions come from

Retail material and reference pages quote higher numbers than 144 x 80. They are
measuring something else.

**216 x 160** is the count of 4-bit colour *samples*, not of pixels. The pixel payload is
17 280 bytes = 34 560 nibbles, laid out as 160 half-rows of 108 bytes, and each half-row
is 216 nibbles: `216 x 160` = 34 560 exactly. Three samples make one full-colour pixel,
so `34 560 / 3` = 11 520 = 144 x 80. It is most likely a genuine count of panel dots —
which fits the zig-zag, since a pixel's three samples come from two different half-rows
and two adjacent columns, the signature of a delta or mosaic colour LCD rather than
stripes.

**240 x 160** appears on Wikipedia for both Color and XP, uncited. The claims around it
do not survive contact with a real disc: it says the video is compressed (there is no
codec at all), that it runs at 15 fps (it is `176 400 / 19 760` = 8.93 at one-times CD
speed and 17.85 at the twice CD speed the XP reads at, and 18.00 on Color at the same
speed), and that video sits on the left audio channel with sound on the right (it is a
9:1 byte interleave, not a stereo split). Those last two are true of the original black
and white format, which does run at 15 fps with its picture in the left channel (see
[Black and white](#black-and-white-established)), and its black and white figure of
80 x 80 non-square with 16 greys matches too. The page seems to have carried black and
white facts over to the later players, so nothing on it should be preferred to the disc.

## The frame header (established)

The header is not a struct. It is a small command stream aimed at the display controller:
after the repeated sync words it is a run of **`(value, register)` byte pairs**, grouped
four pairs at a time with an `0xFF` separator, and padded out with `0xFF`. The XP header
is two such 252-byte blocks (108 bytes of sync + 144 bytes of payload each).

Registers below `0x49` are panel setup — a 16-entry gamma ramp and timing values — and
are identical on every disc examined. The interesting block is at the end:

| Register | Meaning |
|----------|---------|
| `0x49` / `0x4A` | Current frame index within the track, little-endian 16-bit |
| `0x4B` | Segment kind (see below) |
| `0x4C` | Set only on the title track; see below |
| `0x4D` / `0x4E` | Total frames in this track, little-endian 16-bit |
| `0x4F` | The track to play next when no branch is taken; see below |
| `0x50`, `0x54`, `0x58`, `0x5C`, `0x60`, `0x64` | Branch table, six entries of four registers |
| `0x77` | Current track number |

Registers `0x4D`/`0x4E` and `0x77` are a useful self-check: they should agree with the
frame count and track number worked out from the cue sheet and file sizes. `vxp headers`
prints the whole register file.

### Segment kinds (register `0x4B`)

No published specification exists; these meanings come from surveying six retail discs
(two interactive adventures and four episode discs with a quiz).

| Value | Name | What it does |
|------:|------|--------------|
| 1 | `Linear` | Nothing special |
| 2 | `Choice` | Puts a choice to the viewer; the branch table holds the answers |
| 3 | `TaggedChoice` | Branches on the score (see below); the viewer is not asked |
| 4 | `ScoreUp` | Adds one to the score as it starts |
| 5 | `ScoreDown` | Takes one from the score as it starts |
| 6 | `ScoreReset` | Puts the score back to `0x64` as it starts |

Kinds 4, 5 and 6 were first read as "stop", "hub" and "restart", from where they sit.
None of those holds up: every kind-4 segment on the quiz discs is the "right!" clip of a
question, called from the question with the next question queued behind it (below), so
it cannot stop; kind 5 is the "missed it" clip after a timed prompt on both adventures
and the "wrong" clip in the Teen Titans quiz; kind 6 sits just before each quiz starts
and after each result, and at the very end of Batman. What they share is what they do
to a counter.

### The branch table

Six entries, four registers apart, from `0x50`. Each entry is a **play list**: a
destination track, then up to three more tracks to play after it, ending at the first
zero. Entries naming track 0 are unused.

On a `TaggedChoice` segment the first register of each entry is a **score threshold**
and the play list starts in the second. Reading a tagged table the plain way produces
destinations past the end of the disc, which is exactly what makes the two cases
distinguishable.

**The table belongs to the frame, not the segment.** It changes within a track, and
that is how timed prompts are cut. Batman track 32 offers nothing but the menu key
(below) for its first 926 frames; for the next 12, key 5 goes to track 33 and every
other key to 48; for the last 12, every key goes to 48; and `0x4F` names 48 for no
press at all. Tracks 32-42,
62-69 and 71-81 on Batman, and 4-8, 37-43 and 71-81 on Teen Titans, are chains of these:
hit the prompt and the next scene plays, miss it and the failure clip does. `vxp map`
reads every frame, so a prompt shows as two destinations under one key:

```
 32     950         Linear    48  1:48  2:48  3:48  4:48  5:33  5:48  6:24
```

The sixth entry is usually a lone standing entry on otherwise plain segments — `6:24` on
Batman, `6:17` on Kids Next Door — which is the button back to the disc's menu. `vxp`
does not count it as a choice point.

From *Batman vs The Joker*:

```
 14      35         Choice     -  1:15>14  2:16>14  3:17  4:15>14  6:24
 17     162         Choice     -  1:18  2:20>24  3:20>24  4:20>24  6:14
 22      36   TaggedChoice     -  1:58(score>=6D)  2:57(score>=6C)  ...  6:23(score>=64)
 27     252         Linear    27  5:31>24  5:28>29>30>22  6:24
```

`2:20>24` plays track 20 and then 24; `5:28>29>30>22` plays three kind-4 clips — three
points — and then 22.

**Play lists.** The quiz discs show what the extra bytes are for. Each question is a
`Choice` whose entries send the right key to a kind-4 clip and the wrong keys to a
plain one, each followed by the next question: on Kids Next Door, question 30 is
`1:37>31  2:37>31  3:36>31  4:37>31`. The last question's entries continue to the
results. When a track in a play list ends and names no track of its own in `0x4F`,
the next track of the list plays.

**The score.** The player keeps one counter, which starts at `0x64` (100). Kinds 4, 5
and 6 add one, take one, and reset it. A `TaggedChoice` segment takes the first entry,
in slot order, whose threshold the score meets. The evidence:

- Every tagged table in the corpus, nineteen of them, lists its thresholds in descending
  order, and most end in a catch-all: `0x64`, the starting score, or `0x01`.
- Where a table needs more than six outcomes it is split in two, the first ending in
  `0x64` pointing at the second: Kids Next Door 40/41, Batman 22/23 and 24/25.
- The number of outcomes matches the number of questions. Kids Next Door asks 8
  questions and its results table has 9 outcomes, thresholds `0x64`-`0x6C`, one for each
  possible count of right answers; *Raggedy Android*, *Teen Robot 3* and *Jimmy Neutron*
  each ask 10 and have 11 outcomes, `0x64`-`0x6E`.
- Teen Titans counts down instead: missed prompts and wrong answers are kind 5, and track
  15's table reads `0x64`, `0x62`, `0x60`, `0x01` — no misses, one or two, three or
  four, more.

That the tag was an input code was the earlier reading, from tracks 22 and 24 sharing
tags while pointing at different places. Thresholds explain that equally well: the same
score bands, with different endings for each branch of the story.

### Where a segment goes next

A key press takes its branch at once (see [below](#when-a-key-press-takes-effect)).
When a segment ends with no key pressed, the player takes the first of these that
applies:

1. Register **`0x4F`**, if it names a track. A segment naming itself repeats until the
   viewer acts: Batman track 60 loops until key 3 is pressed, and track 27 until 5 or 6.
2. The rest of the play list that led here.
3. On a `TaggedChoice` segment, the score branch.
4. On a `Choice` segment, the segment again: an unanswered question is held until a key
   is pressed (see [below](#an-unanswered-choice)).
5. The next track in disc order, passing over blank tracks.

With `emulation.instantChoices` turned off, a press is instead remembered and taken ahead
of all of these when the segment ends, looked up in the frame on screen when the key was
pressed.

`0x4F` holds this rule on every track of all six discs. It is constant within every
track, it is never set on a choice segment, and wherever it is set it names the right
place:

| Where | `0x4F` names | So |
|-------|--------------|----|
| The title sequence (track 2) | 5 on Batman, 4 on Teen Titans, the menu on the other four | Past Batman's blank tracks, and past the episodes to the menu |
| End credits on the episode discs | The menu | Back to the menu |
| A timed prompt | The failure clip, the same track the prompt's closing frames send every key to | No press is a miss |
| Teen Titans' missed-prompt clips 10-14 | The next scene, 5-9 | 4 → 10 → 5 → 11 → 6 ... → 9 → 15 |
| Batman's missed-prompt clips 70 and 83 | The start of the chain, 62 and 71 | Try again |
| Batman 48, 56-58 | 24 | Back to the story map |
| Batman 91-94 | 95 | Every ending leads to the epilogue |
| Batman 96, kind 6 | 2 | Start again from the title |
| Batman 27 and 60 | Themselves | Wait for a key |

The one track that breaks the pattern is Batman 79, a prompt in the 71-81 chain whose
`0x4F` is 0 where its neighbours have 83; its closing frames still send every key to 83.
It reads as a mastering slip, and with `0x4F` unset it falls through to disc order.

The earlier worry about `0x4F` — that honouring it "would skip large stretches of a disc"
— was right about the effect and wrong about the intent. What gets skipped is the
episodes, which the title sends past to the menu that plays them, and the rest of a
chain of prompts after a miss. `vxp` follows it by default; `--navigation discOrder` plays
every segment in turn instead, which is useful for watching everything on a disc.

Teen Titans, played in disc order, looped for ever: 4, 5 ... 9, 10, and 10 (a
missed-prompt clip naming 5) back to 5. Followed, it runs
4 → 10 → 5 → 11 → 6 → 12 → 7 → 13 → 8 → 14 → 9 → 15, and track 15's score branch picks
the ending for five misses.

Register `0x4C` is set only on the title track: 3 on both adventures, and on the episode
discs the first track after the episodes and credits (16 on Kids Next Door and
*Raggedy Android*, 18 on Jimmy Neutron, 19 on *Teen Robot 3*). What reads it is
not known.

### An unanswered choice

No `Choice` segment names a track in `0x4F`, so what the hardware does when one ends
unanswered is not written on the disc. What the discs rule out is any automatic answer.
Taking the first entry, `vxp`'s earlier default, answers every quiz question with key 1,
makes Batman's `Choice`-kind prompt at track 84 succeed on its own, and sends the episode
discs' chapter screens (Kids Next Door 19-26, Jimmy Neutron 22-29) cycling for ever,
since each one's first entry is the next chapter. Carrying on in disc order is no better:
it walks from a menu into whatever track happens to follow it.

So `vxp` holds. An unanswered `Choice` plays again from its first frame, picture and
sound together, until a key is pressed, exactly as a segment naming itself in `0x4F`
already does (Batman 27 and 60). Repeating was preferred to freezing on the last frame
because that is how the disc's own "wait for a key" segments behave, and because many
choice segments offer their keys only part of the way through: Batman 84-87 put the
question up for their last 24-33 frames, and a frozen last frame would leave the viewer
with nothing on screen to answer. The quiz questions' own key 5, which names the
question itself (Kids Next Door `5:30` on track 30), is the "ask me again" the hold makes
automatic.

A `TaggedChoice` asks the viewer nothing, so it is never held; one whose score bands all
miss carries on in disc order. `emulation.choiceTimeout` (`--choice-timeout`) still
offers `firstBranch` and `discOrder`.

### When a key press takes effect

The player takes a branch **the moment its key is pressed**, rather than holding the
press until the segment ends. `vxp` did the latter at first; the discs argue against it
on four counts.

- **The menu key on the episodes.** Every episode segment on the four linear discs
  carries the back-to-menu entry in slot 6 on every frame (`6:17` on Kids Next Door),
  and nearly all of those segments run 2,500 to 4,360 frames, two to four minutes each. Held to the
  end of the segment, the menu key would answer minutes after it was pressed.
- **Timed prompts cut from the window, not from the end.** A prompt offers the next
  scene for about twelve frames and then gives every key to the failure clip for the
  twelve that close the segment. Across Batman's thirty prompts (tracks 32-42, 62-69,
  71-81), the first frame of each success clip matches the source most closely at or
  before the end of its prompt window, never in the closing frames, and on average it is
  twice as close to that frame as to the segment's last one (mean nibble difference 3.8
  against 8.0). The success clip is cut to follow the picture where the key is pressed,
  which only an immediate jump shows.
- **Entries that name their own segment.** Quiz questions give key 5 to the question
  itself (Kids Next Door 30-39, *Teen Robot 3* 34-45, Jimmy Neutron 33-42, Teen Titans
  24-29 and 55-61), and Teen Titans track 23, a 1,905-frame `Choice`, gives key 5 to 23.
  At the end of the segment that is the same as pressing nothing. On the press, it
  restarts the question: a replay key.
- **A window in the middle of a scene.** Teen Titans track 4 offers `3:71` for nine
  frames, 440-448, of a 1,727-frame scene that has its own timed prompt at 1,704. Held to
  the end, a press there would be decided some seventy seconds later, after that prompt
  had come and gone.

Nothing on the discs argues the other way: timed prompts work under either reading,
because their closing frames send every key to the failure clip. `emulation.instantChoices`
is on by default, and turning it off restores the deferred behaviour. The `vxp run`
script keeps both, as `choose N` (at once) and `choice N` (at the end of the segment).

## Black and white (established)

The original VideoNow is a different format from the ground up: no sync word, no register
file, no branching, a smaller grey picture and a faster stream of sound. What follows is
read from two retail discs, *Rugrats: All Growed Up* parts 1 and 2, each a single `.bin`
of ten tracks, and checked against PVDTools and its `format.txt`.

### Groups

The disc is read as ordinary 16-bit stereo CD audio, and each four-byte stereo sample is
one group. The left channel holds two video bytes and the right a marker byte and then an
audio byte:

```
| video | video | marker | audio | video | video | marker | audio | ...
```

So of the 176 400 bytes/s, half is video, a quarter is markers and a quarter is sound:
**44 100 audio bytes a second** at one-times CD speed. Tracks start on a group boundary.

### Frames

The marker says what the group's video bytes are part of, and it marks out every frame:

| Groups | Marker | Video bytes |
|-------:|--------|-------------|
| 670 | header: `E1`, `C3` or `A5` | the marker again, 1 340 bytes |
| 1 600 | `5A`, picture | 3 200 bytes of picture |
| 670 | footer: `D2`, `B4` or `96`, pairing with the header | the marker again, 1 340 bytes |

A frame is **2 940 groups, 11 760 bytes** of disc: 5 880 video bytes, 2 940 marker bytes
and 2 940 audio samples, or **15 frames a second** at one-times speed.

The three pairs differ in how they are used, and their bit patterns form a series
(`E1 D2 C3 B4 A5 96`, high nibble falling as the low one rises):

- `C3`/`B4` mark ordinary frames, all but a handful.
- `E1`/`D2` mark the first five frames of the VideoNow logo on track 1, and one cut-off
  header at the start of part 2's track 2. The logo's `E1` headers are the only ones
  whose video bytes carry anything but the marker: 68 bytes in which the pairs `(00, 00)`
  and then `(2k+1, k)` for k = 1 to 15 each appear twice, then `(06, 23)` twice. That reads like the
  Color and XP headers' `(value, register)` pairs loading a 16-step grey ramp, but no
  player has been watched reading it.
- `A5`/`96` mark the last frame of each programme: the end of the logo (track 1), of the
  feature (part 1 track 8, part 2 track 8) and of the end card (track 9). Only padding
  follows them.

The markers are what `vxp` detects a black and white track by, and finds its frames by.
The earlier notes call the right channel's extra byte "a constant stream of `0x5a`"
sync data; it is constant only across the picture.

### Tracks cut through frames

A long programme is mastered as one stream and then cut into tracks wherever the index
falls, so tracks do not start or end on frame boundaries. On part 1, tracks 3 to 8 are
one continuous stream: track 4 opens with the last 238 picture groups of the frame whose
header ended track 3, and track 3's last whole frame and track 4's first are two frames
apart in the same shot, with the split frame between them. The first header after a boundary is often short
(213 groups at the start of part 1 track 7, 125 on part 2 track 4), so the frames are
not on a fixed grid from the start of a track either.

Separate programmes are padded instead: tracks 1, 2 and 9 open with 776 groups of zeros
(video, marker and audio all zero) and the last track of each programme closes with
20 980 of them, about half a second.

`vxp` reads a black and white track through once to index it, counts only the frames
whose picture lies wholly inside the track, and gives each frame the sound from its own
header to the next frame's. The first and last frames of a track also take the sound
beyond them, so the partial frame at a cut loses its picture (one frame in fifteen of a
second) but none of its sound. The zeros of the padding are played as silence rather
than as full-scale negative samples.

### Picture

**80 x 80 pixels of 16 greys**, 40 bytes to a row, two pixels to a byte with the **high
nibble on the left**, 0 black and 15 white. The rows run top to bottom with no
interleaving, which the title cards confirm: the "END OF DISC, PRESS STOP" card and the
Klasky Csupo logo decode as clean, unsheared text. Swapping the nibbles instead tears
every vertical edge into teeth.

The pixels are not square either. 80 x 80 of 4:3 broadcast animation needs a pixel
aspect of 4/3, which is exactly the shape `vxp` gives Color and XP (144 x 80 at 0.74 is
also 4:3), so the window and `video.pixelAspect` are shared and a black and white picture
fills the same frame. It checks out on the disc: the ball in part 1 track 2, frame 200
spans 25 pixels by 33, which is round at 4/3; the logo's speaker cone and the STOP sign
come out round and regular.

### Sound and speed

Unsigned 8-bit mono, one sample a group, silence at `0x80`, continuous across the header
and footer (the step between samples is the same inside a picture run as across its
edges). The marker is not sound.

The discs play at **one-times CD speed: 44 100 Hz and 15 fps.** Two lines agree:

- **Running time.** *All Growed Up* runs about 45 minutes. Each disc holds 24 minutes
  at 1x, and taking away the logo, the opening titles and the end card that both carry,
  the feature comes to 22:48 on part 1 (tracks 3 to 8) and 22:52 on part 2, or 45:40
  together. At 2x the whole special would be under 23 minutes, and a two-disc release
  would be half empty. Part 2 ends on the Klasky Csupo production logo, so the discs
  are the first and second halves.
- **Speech rate.** At 44 100 Hz the soundtrack's envelope swells and fades about 3.9 to
  4.0 times a second (part 1 and part 2), the pace of syllables in ordinary speech; the
  same measure gives 4.6 on the Aly & AJ disc's bonus track and 4.1 on Thomas, both XP
  at 35 280 Hz. At 2x it would be near 8. Voiced pitch comes out around 330 to 370 Hz, the
  register of the babies' voices, which at 2x would be implausibly high.

PVDTools' own WAV header also says 44 100 Hz mono 8-bit for black and white.

### What PVDTools does differently

PVDTools' black and white path (`handleBlackAndWhite`) splits the groups the same way
and takes audio from the fourth byte, discarding the marker. It then treats frames as a
fixed 5 880 video bytes from the first header, finding headers by the video bytes rather
than by the markers, so a pixel that happens to equal a header byte, or a short header,
throws it off. It writes each 3 200-byte picture as a 40 x 80 image with one byte to a
pixel, without splitting the nibbles, so its frames are half width and show only the
high-nibble pixel clearly.

## Not yet established

These are open questions. `vxp` is deliberately conservative about them.

**Which key is which.** The six slots are clearly six controls. On the episode discs'
chapter screens slot 2 steps forward, slot 4 back, slot 5 plays and slot 6 returns to
the menu, which fits four directions, a select and a menu key. `vxp` maps slots to keys
1-6 and to the controller's D-pad, X and Y.

**The hardware itself.** The hold at an unanswered choice and the jump on a key press
are read from how the discs are cut, not observed on a player. Both fit every case in
the corpus.

**The Color player's speed.** Color-format frames are read at 2x because every one
examined shares a disc with 2x content (see [Audio](#audio-established)), but no disc
whose programme is Color-format has been checked, and no Color player has been measured.
If one turns up that runs twice as long as it should, Color's `DiscSpeed` is the one
number to change.

**Black and white details.** The format and the speed are settled by two discs of one
title. What the `E1` and `A5` frames tell the player (a grey ramp to load, the end of a
programme to stop at) is inferred from where they sit, and whether any black and white
disc cuts its tracks on frame boundaries, or holds more than one kind of header ramp, is
unknown.

## Sources

- [PVDTools](https://github.com/saramibreak/PVDTools) by saramibreak — prior C decoder for
  all three variants; the source of the sync word, frame sizes and the pixel zig-zag.
- [pvdtools.sourceforge.net/format.txt](https://pvdtools.sourceforge.net/format.txt) —
  notes on the original black and white format: the left and right channel split and the
  1 340 + 3 200 + 1 340 byte frame, which the Rugrats discs bear out.
- Everything about the header register map, the branch table and the segment kinds was
  derived for this project by differential analysis across tracks and discs.
