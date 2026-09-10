# vxp

An emulator for the **VideoNow XP**, the Hasbro/Tiger personal video player that read
video off small pressed CDs.

`vxp` mounts a disc image, decodes the picture and sound itself, and plays titles at the
disc's true rate with working transport controls and interactive branching.

```
vxp "Some Title.cue"
```

## What works

- **VideoNow XP and VideoNow Color** discs, detected automatically.
- **Picture** — 144 x 80, three 4-bit channels per pixel, decoded natively.
- **Sound** — 17 640 Hz mono, in sync and at full speed.
- **Transport** — play, pause, track skip, stop, volume.
- **Interactive titles** — the branch table is read out of each segment's header, so
  decision points offer the destinations the disc actually declares.
- **Tooling** — inspect a disc, dump its segment map, export frames and audio.

Playback is clocked by the audio: the player decodes exactly as many frames as the sound
card consumes, so picture and sound cannot drift apart.

There is no FFmpeg or libVLC dependency. VideoNow is not a container format around a
standard codec — the disc is raw CD audio sectors carrying an interleaved video and audio
stream aimed straight at an LCD controller — so a general-purpose media library has
nothing to decode. The whole codec is a few hundred lines. FFmpeg is still handy for
turning `vxp export` output into a normal video file, which is why export writes PNG and
WAV.

## Requirements

- [.NET SDK 8.0 or later](https://dotnet.microsoft.com/download)
- SDL2, which arrives through NuGet on Windows, macOS and Linux

## Building

```sh
dotnet build -c Release
dotnet test
```

The player binary lands in `src/Vxp/bin/Release/net8.0/`.

## Discs

`vxp` reads the cue-sheet-plus-binary images that CD ripping tools produce, in either the
one-file-per-track or single-file layout. Point it at the `.cue`:

```sh
vxp "Some Title.cue"
```

No disc images are included in this repository, and none are needed to build or test it.

## Usage

```
vxp <disc.cue> [options]            Play a disc.
vxp info    <disc.cue>              Format, frame rate, track list and durations.
vxp map     <disc.cue>              Segment kinds and the interactive branch table.
vxp headers <disc.cue> [--track N] [--all]
                                    Dump the per-frame controller register file.
vxp export  <disc.cue> --out <dir> [--track N] [--frames N] [--scale N] [--swizzle RGB]
                                    Write PNG frames and a WAV soundtrack.
```

Play options:

| Option | Effect |
|--------|--------|
| `--track N` | Start on this track instead of the first |
| `--scale N` | Window scale factor, default 5 (720 x 400) |
| `--fullscreen` | Start full screen |
| `--follow-header` | Follow register `0x4F` as a next-track pointer (experimental) |

### Controls

| Key | Action |
|-----|--------|
| Space | Play / pause |
| Left / Right | Previous / next track |
| 1 - 6 | Take a branch at an interactive decision point |
| Up / Down | Volume |
| Backspace | Stop and rewind to the start of the disc |
| F | Toggle full screen |
| Tab | Toggle the status overlay |
| Esc | Quit |

At a decision point the overlay lists the destinations the segment offers. Press the
matching number; the jump happens when the segment ends. Do nothing and the story takes
the first branch, as the hardware does on a timeout.

### Making a normal video file

```sh
vxp export "Some Title.cue" --track 2 --out frames
ffmpeg -framerate 176400/19760 -i frames/t02_f%05d.png -i frames/audio.wav out.mp4
```

## How it works

```
src/Vxp.Core        Disc mounting, format detection, decoders, player state machine
  Discs/            Cue sheet parsing and track-level disc access
  Format/           Interleaving, frame layout, video and audio decode, header parsing
  Emulation/        Transport, timing and interactive branch logic
src/Vxp             The vxp binary: SDL front end plus the inspection subcommands
tests/Vxp.Core.Tests
```

`Vxp.Core` has no dependencies beyond the base class library and no reference to SDL, so
it can be reused headlessly. The SDL front end is a host: it pulls PCM and puts pixels on
screen, and holds no emulation logic of its own.

The disc format, including the register map and the branch table, is written up in
[docs/format.md](docs/format.md) — along with an explicit list of what is confirmed and
what is still guesswork.

## Status

Playback, sound and transport are solid. The interactive model is good enough to navigate
retail interactive titles, but parts of the segment header are still being worked out;
`docs/format.md` says which. Black and white VideoNow discs are detected but not yet
decoded.

## Credits

Builds on [PVDTools](https://github.com/saramibreak/PVDTools) by saramibreak, which
established the sync word, the frame sizes and the pixel layout for the Color and XP
formats.

## Licence

MIT. See [LICENSE](LICENSE).

VideoNow is a trademark of Hasbro. This project is not affiliated with or endorsed by
Hasbro or Tiger Electronics.
