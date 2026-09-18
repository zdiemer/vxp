# The VideoNow disc format

This is what `vxp` implements, derived by analysing retail VideoNow XP discs and
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
  *Batman vs The Joker* tracks 3 and 4 are 24 seconds of this, sitting between the title
  sequence and the first choice, and the title's register `0x4F` names track 5 — the
  disc's own pointer steps over them. Played in disc order they look like a hang, so
  `vxp` passes over them in disc order and track skipping as it does fill, and still
  plays one if it is selected directly. Blank segments that offer a choice (Batman
  tracks 22 and 23) or redirect are not skipped. Tracks that are black but carry sound,
  such as Teen Titans track 2, are content and are not skipped either.

Ripping tools produce a cue sheet plus either one `.bin` per track or a single `.bin`.
`vxp` handles both. The cue sheet's `TITLE` fields often preserve the mastering source
file names (`04-Something_CODE.vn5`), which is pleasant metadata but carries no playback
meaning.

## Interleaving (established)

The bytes on the disc are not audio samples. The stream is split in a fixed ratio:

```
| 9 video bytes | 1 audio byte | 9 video bytes | 1 audio byte | ...
```

So of the 176 400 bytes/s, nine tenths are video and one tenth is audio. This is why the
audio rate comes out at exactly **17 640 Hz**: one tenth of the disc byte rate.

## Frames (established)

The stream is divided into fixed-size frames. The frame size is the one place where the
disc variants differ:

| Variant | Frame on disc | Video | Audio | Header | Sync words | Frame rate |
|---------|--------------:|------:|------:|-------:|-----------:|-----------:|
| Color   | 19 600 B      | 17 640 B | 1 960 B | 360 B | 24 | 9.0000 fps |
| XP      | 19 760 B      | 17 784 B | 1 976 B | 504 B | 12 | 8.9271 fps |

Both carry the same **144 x 80** picture; XP's improvement was a larger, better screen
and longer running time, not more pixels. In both cases:

```
video bytes = header + 17 280 bytes of packed pixel data
```

The frame rate follows from arithmetic rather than being stored anywhere:
`176 400 / 19 760 = 8.9271 fps` for XP. Audio is the honest master clock — one frame is
exactly 1976 samples, or 112.018 ms.

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

Unsigned 8-bit mono PCM at 17 640 Hz. Silence is `0x80`. There is no compression and no
in-band control data.

> **The XP plays at twice CD speed.** 17 640 Hz is the rate at one-times CD speed, and
> at that rate the retail XP discs run twice as long as they should: the two cartoon
> segments on the Jimmy Neutron disc (tracks 4–10) come to 45.2 minutes, where the
> broadcast segments are about 11 minutes each. At double speed, **35 280 Hz** and
> **17.854 fps**, they come to 22.6 minutes, and the sound is right by ear. Pitch alone
> could never show this, since a factor of two is exactly one octave: at 35 280 Hz the
> theme music sits within a few cents of the studio's own uploads, as it did at 17 640.
> A rate 0.8 % higher (35 568 Hz, an even 18 fps) would put it 14 cents off them, so
> exactly 2x is preferred. The Color figures below are unchecked against this.
> `vxp` plays at 35 280 Hz by default, and the rate is the `emulation.discSampleRate`
> setting (`--rate`) until hardware confirms it. Exports and the durations `vxp info`
> reports are still at the one-times rate.

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
codec at all), that it runs at 15 fps (it is `176 400 / 19 760` = 8.93, and 9.00 on
Color), and that video sits on the left audio channel with sound on the right (it is a
9:1 byte interleave, not a stereo split). Its black and white figure — 80 x 80 non-square
with 16 greys — does match, so the page is not uniformly wrong, but nothing on it should
be preferred to the disc.

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
| `0x4D` / `0x4E` | Total frames in this track, little-endian 16-bit |
| `0x4F` | A track pointer; see "Not yet established" |
| `0x50`, `0x54`, `0x58`, `0x5C`, `0x60`, `0x64` | Branch table, six entries of four registers |
| `0x77` | Current track number |

Registers `0x4D`/`0x4E` and `0x77` are a useful self-check: they should agree with the
frame count and track number worked out from the cue sheet and file sizes. `vxp headers`
prints the whole register file.

### Segment kinds (register `0x4B`)

These names describe observed behaviour; no published specification exists.

| Value | Name | Behaviour |
|------:|------|-----------|
| 1 | `Linear` | Plays straight through, no viewer choice |
| 2 | `Choice` | Offers a choice; branch table holds destinations |
| 3 | `TaggedChoice` | Offers a choice using the tagged branch encoding |
| 4 | `Terminal` | Plays through and stops |
| 5 | `Hub` | Returns to the track named by `0x4F` |
| 6 | `Restart` | End of title; returns to the track named by `0x4F` |

### The branch table

Six entries, four registers apart, of which only the first two bytes of each are used.
Entries naming track 0 are unused.

**The segment kind selects how the two bytes are read.** On `TaggedChoice` segments the
first byte is a selector tag and the second is the destination track. On every other kind
the first byte is the destination and the second is something else. Reading a tagged
table the plain way produces destinations past the end of the disc, which is exactly what
makes the two cases distinguishable — and what makes it clear the rule is real rather
than fitted.

From *Batman vs The Joker*, decoded by `vxp map`:

```
  5     266         Choice     -  1:13  2:32  3:49  4:26
  6      78         Choice     -  1:13  2:32  3:49  4:26
  7      78         Choice     -  1:13  3:52  4:26
 17     162         Choice     -  1:18  2:20(tag 18)  3:20(tag 18)  4:20(tag 18)
 22      36   TaggedChoice     -  1:58(tag 6D)  2:57(tag 6C)  3:57(tag 6A)  4:56(tag 69)
 24      36   TaggedChoice     -  1:59(tag 6D)  2:12(tag 6C)  3:10(tag 6A)  4:7(tag 69)
```

The shape of an interactive title is visible in the disc layout itself: runs of tracks
with byte-identical lengths are parallel branches of the same scene. On this disc tracks
6-12 are seven identical 78-frame segments and tracks 91-94 are four identical 289-frame
segments.

Tracks 22 and 24 use the **same tags in the same order** (`6D 6C 6A 69 68 64`) while
pointing at completely different destinations. A value that is constant across segments
while the destinations vary is behaving like an input code, which is the main reason to
think the tag identifies the control the viewer presses.

## Not yet established

These are open questions. `vxp` is deliberately conservative about them.

**Register `0x4F`.** A valid track number, but its role is unclear. Groups of tracks share
one value — tracks 32-42 all name 48, tracks 62-69 all name 70, tracks 71-81 all name 83
— and the named track sometimes points back into the group. That is consistent with a
"continue here after this group" pointer, and also with a chapter or hub identifier.
On every disc examined the title sequence (track 2) sets it: to the first scene on the
interactive titles (Batman names 5, past its blank tracks 3 and 4) and to the episode
menu on the linear ones (a choice segment after the episodes). Two Batman tracks (27 and
60) name themselves.
Treating it as an unconditional next-track pointer would skip large stretches of a disc
in linear playback, so `vxp` ignores it by default and honours it only for `Hub` and
`Restart` segments. `--follow-header` enables the aggressive reading for experimentation.

**The selector tag.** On `TaggedChoice` segments the tags come from a small repeating set
(`0x01`, `0x64`, `0x65`, `0x67`-`0x6D`). Ten or so distinct values suggests a keypad, but
which physical control each one means has not been pinned down. `vxp` currently maps
branch **slots** to number keys 1-6 rather than trying to interpret the tag.

**The second byte on non-tagged segments.** Usually the segment's own track number, and
sometimes a different track — plausibly "where to continue after the branch finishes".

**When a choice is committed.** `vxp` records a keypress and jumps at the end of the
segment, which fits how these discs are cut: the choice window is the tail of a short
segment and every destination is a whole separate track. Whether the hardware jumps
immediately on press has not been determined.

**Black and white VideoNow.** A different interleave and a 4-bit greyscale 80 x 80
picture. Detected and reported but not yet decoded.

## Sources

- [PVDTools](https://github.com/saramibreak/PVDTools) by saramibreak — prior C decoder for
  all three variants; the source of the sync word, frame sizes and the pixel zig-zag.
- [pvdtools.sourceforge.net/format.txt](https://pvdtools.sourceforge.net/format.txt) —
  notes on the original black and white format.
- Everything about the header register map, the branch table and the segment kinds was
  derived for this project by differential analysis across tracks and discs.
