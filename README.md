<div align="center">

<img src="docs/banner.svg" alt="vxp" width="760">

**Play VideoNow XP discs on your PC.**

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![Discs](https://img.shields.io/badge/discs-VideoNow_XP_+_Color-f2a33c?style=flat-square)
![Picture](https://img.shields.io/badge/picture-144x80_@_8.93_fps-4fd2e3?style=flat-square)
![Audio](https://img.shields.io/badge/audio-17640_Hz_mono-4fd2e3?style=flat-square)
![Interactive](https://img.shields.io/badge/branching-read_from_the_disc-8f7ae8?style=flat-square)
![License](https://img.shields.io/badge/license-MIT-3f8f66?style=flat-square)

</div>

# vxp

An emulator for the **VideoNow XP**, the Hasbro/Tiger personal video player that read
video off small pressed CDs.

`vxp` mounts a disc image, decodes the picture and sound itself, and plays titles at the
disc's true rate — with in-app menus, a track browser, rebindable controls, and a full
command line for driving it headlessly.

```
vxp "Some Title.cue"
```

## What it does

- **VideoNow XP and VideoNow Color** discs, detected automatically.
- **Picture** — 144 x 80, three 4-bit channels per pixel, decoded natively.
- **Sound** — 17 640 Hz mono, in sync and at full speed.
- **Transport** — play, pause, seek, frame step, variable speed, fast forward, loop.
- **Interactive titles** — the branch table is read out of each segment's header, so
  decision points offer the destinations the disc actually declares. Take a wrong turn
  and you can step back through the segments you came from.
- **Menus** — settings, a track browser and control rebinding, all in the window.
- **Headless CLI** — inspect, verify, export, graph the branch structure, and drive
  scripted playback with no window at all.

## The disc is not a video file

A VideoNow disc is a physically small CD pressed as an **ordinary audio CD**: no
filesystem, no container, no standard codec. The player reads raw 2352-byte CD-DA sectors
and feeds them almost straight to an LCD controller and a DAC, so there is nothing here for
a general-purpose media library to open. Decoding it is a few hundred lines, and `Vxp.Core`
is those lines.

Everything else falls out of that one fact:

- The stream is interleaved **nine video bytes to one audio byte**. A CD reads 176 400
  bytes a second, so the audio is 17 640 Hz — a tenth of the disc — and that is why.
- Frames are a fixed size on disc, 19 760 bytes on XP and 19 600 on Color, both carrying
  the same 144 x 80 picture. The frame rate is not stored anywhere; it is
  `176 400 / 19 760` = **8.9271 fps**, arithmetic rather than metadata.
- Which variant a disc is comes from **counting sync-word repeats** in the frame header:
  24 means Color, 12 means XP.
- Audio is therefore the honest master clock, and playback is clocked by it — the player
  decodes exactly as many frames as the sound card consumes, so picture and sound cannot
  drift apart. A headless run is the same loop with no sound card, which is what makes
  scripted playback deterministic.
- Interactive titles are not a separate feature of the player. Branches are cut as
  ordinary tracks and the destinations are declared in each segment's header, so following
  a story is reading the disc rather than guessing at it.

## Requirements

- [.NET SDK 8.0 or later](https://dotnet.microsoft.com/download)
- SDL2, which arrives through NuGet on Windows, macOS and Linux

## Building

```sh
dotnet build -c Release
dotnet test
```

The binary lands in `src/Vxp/bin/Release/net8.0/`.

## Discs

`vxp` reads the cue-sheet-plus-binary images that CD ripping tools produce, in either the
one-file-per-track or single-file layout. Point it at the `.cue`.

No disc images are included in this repository, and none are needed to build or test it.

## Playing

```
vxp <disc.cue> [options]
```

| Option | Effect |
|--------|--------|
| `--track N` | Start on this track |
| `--frame N` | Start at this frame of that track |
| `--scale N` | Window scale factor |
| `--fullscreen` / `--windowed` | Override the saved window mode |
| `--speed N` | Playback rate as a percentage, 25 to 800 |
| `--volume N` | Volume, 0 to 100 |
| `--mute` | Start silent |
| `--loop MODE` | `none`, `track` or `disc` |
| `--navigation MODE` | `discOrder` or `followHeader` |
| `--choice-timeout MODE` | `firstBranch`, `discOrder` or `wait` |
| `--no-config` | Ignore the settings file and use defaults |

Command-line options override the saved settings for that run without changing what is
stored.

### Default controls

Every one of these can be rebound, to the keyboard or to a game controller.

| Key | Action |
|-----|--------|
| Space | Play / pause |
| Left / Right | Previous / next track |
| Shift + Left / Right | Seek back / forward |
| `,` / `.` | Step one frame back / forward |
| F (hold) | Fast forward |
| `-` / `=` / `0` | Slower / faster / normal speed |
| B | Back a segment |
| L | Cycle loop mode |
| 1 - 6 | Take a branch at a decision point |
| Up / Down | Volume |
| M | Mute |
| Escape | Open the menu (and back out of it) |
| T | Track browser |
| Tab | Cycle the status overlay |
| F11 | Full screen |
| F12 | Screenshot |
| Backspace | Stop and rewind |
| Ctrl + Q | Quit |

A control can mean two things without conflict: the arrow keys work the transport during
playback and move the highlight once a menu is open.

### Menus

Escape opens the menu. Arrows move and adjust, Enter selects, Delete restores a setting to
its default, Escape backs out.

- **Tracks** — every segment with its running time, branch structure and a jump-to.
- **Disc information** — format, timing and whether the title is interactive.
- **Picture** — scaling mode, filtering, window scale, pixel aspect, brightness, contrast,
  saturation, gamma, channel order, and simulated LCD grid and scanlines.
- **Sound** — volume, mute, buffer size, background playback.
- **Playback** — speed, fast-forward rate, seek step, loop mode, and how interactive
  choices behave.
- **On-screen display** — overlay mode, corner, timeout, text size and what it shows.
- **Controls** — rebind anything. Enter captures the next control pressed, Left clears a
  binding, Delete restores the default. Conflicts are reported rather than silently
  overwriting.

Settings are saved as you change them.

## Inspecting a disc

```
vxp info    <disc.cue> [--json]              Format, timing and the track list
vxp tracks  <disc.cue> [--playable] [--json] Just the track list
vxp map     <disc.cue> [--json]              Segment kinds and the branch table
vxp graph   <disc.cue> [--format dot|mermaid|json] [--out FILE]
vxp headers <disc.cue> [--track N] [--frames N] [--all]
vxp verify  <disc.cue> [--deep] [--json]     Check the disc reads back cleanly
```

`vxp graph` writes the branch structure of an interactive title as a graph:

```sh
vxp graph "Some Title.cue" --format dot | dot -Tpng -o story.png
```

## Exporting

```
vxp export <disc.cue> --out DIR [--track N] [--start N] [--frames N]
                                [--every N] [--scale N] [--no-audio] [--no-video]
vxp frame  <disc.cue> --track N [--frame N] --out FILE.png [--scale N]
vxp audio  <disc.cue> --out FILE.wav [--track N]
```

`export` and `frame` also accept `--brightness`, `--contrast`, `--gamma`, `--saturation`
and `--swizzle`.

```sh
vxp export "Some Title.cue" --track 2 --out frames
```

## Driving it headlessly

`vxp run` executes the emulator with no window, as fast as it decodes. Because playback is
clocked by the audio the player generates, a headless run is the same loop without a sound
card, which makes scripted runs deterministic.

```sh
vxp run "Some Title.cue" --commands "track 6; choice 2; play track; expect track 32"
```

| Option | Effect |
|--------|--------|
| `--script FILE` | Read commands from a file, or `-` for standard input |
| `--commands "a; b; c"` | Commands inline |
| `--wav FILE` | Record the session soundtrack |
| `--frames-out DIR` | Write every decoded frame as a PNG |
| `--max-frames N` | Stop writing frames after N |
| `--max-seconds N` | Ceiling on `play all` |
| `--json` | Machine-readable `status` output |

Script commands, one per line or separated by semicolons, with `#` for comments:

| Command | Meaning |
|---------|---------|
| `track N` | Jump to a track |
| `play [5s\|90f\|track\|all]` | Play for a duration, to the end of the track, or to the end |
| `pause` / `resume` / `stop` | Transport |
| `next` / `prev` / `back` | Segment navigation |
| `seek 5s` / `seek -2s` / `frame N` | Move within a track |
| `step [N]` | Step frames and pause |
| `speed 2.0` | Playback rate |
| `choice N` | Queue a branch for the end of the segment |
| `choose N` | Take a branch immediately |
| `screenshot FILE` | Write a PNG |
| `status` | Print the player state |
| `expect FIELD VALUE` | Check `track`, `frame`, `state`, `kind` or `choices` |
| `echo TEXT` | Print a line |

A failed `expect` sets the exit code, so a script doubles as a regression test for a disc.

## Settings from the command line

Every setting the menus expose is readable and writable from the shell.

```sh
vxp config list [filter] [--json]
vxp config get <setting> [--json]
vxp config set <setting> <value>
vxp config reset [<setting>|all]
vxp config recent [clear]
vxp config path

vxp bind list [--json]
vxp bind set <action> <control>      # vxp bind set TogglePause Space
vxp bind add <action> <control>      # vxp bind add TogglePause Pad:A
vxp bind clear <action>
vxp bind reset [<action>|all]
vxp bind keys                        # every bindable key name
```

Controls are written as `Space`, `Ctrl+Q`, `Shift+Left`, `F11`, `Pad:A`, `Pad:DPadUp` or
`Pad:LeftTrigger+`.

Settings live in `%APPDATA%\vxp` on Windows and `~/.config/vxp` elsewhere; `VXP_CONFIG_DIR`
overrides that. Set `VXP_TRACE_INPUT=1` to log raw input events, which is useful when a
binding is not doing what you expect.

## How it works

```
src/Vxp.Core        Disc mounting, format detection, decoders, player state machine
  Discs/            Cue sheet parsing and track-level disc access
  Format/           Interleaving, frame layout, video and audio decode, header parsing
  Emulation/        Transport, timing, interactive branch logic, disc survey
src/Vxp             The vxp binary
  Cli/              Subcommands and the headless session driver
  Config/           Settings model and storage
  Input/            Actions, bindings and input routing
  Ui/               Bitmap font, drawing surface, menus, status overlay
  Video/            Picture adjustment, PNG and WAV writers
tests/              Vxp.Core.Tests and Vxp.App.Tests
```

`Vxp.Core` has no dependencies beyond the base class library and no reference to SDL, so
it can be reused headlessly. The SDL front end is a host: it pulls PCM, puts pixels on
screen and routes input, and holds no emulation logic of its own.

The disc format, including the register map and the branch table, is written up in
[docs/format.md](docs/format.md) — along with an explicit list of what is confirmed and
what is still guesswork.

## Status

Playback, sound, transport, menus and the command line are solid. The interactive model is
good enough to navigate retail interactive titles, but parts of the segment header are
still being worked out; `docs/format.md` says which. Black and white VideoNow discs are
detected but not yet decoded.

## Credits

Builds on [PVDTools](https://github.com/saramibreak/PVDTools) by saramibreak, which
established the sync word, the frame sizes and the pixel layout for the Color and XP
formats.

## Licence

MIT. See [LICENSE](LICENSE).

VideoNow is a trademark of Hasbro. This project is not affiliated with or endorsed by
Hasbro or Tiger Electronics.
