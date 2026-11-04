# Blockparty

A living neighbourhood life simulation built in Unity. Residents of a small
residential block wake up, go to work and school, cook, exercise, make friends
and look after their homes on their own. You can watch, guide a resident, or
just talk to the game.

Demo video: VIDEO_URL

## What it is

- A small block of houses and apartments with a café, workshop, office and
  school, starting with eight residents across several households.
- Every resident has needs, traits, feelings and relationships, and chooses
  what to do next without the player: they chat, make friends, plan parties
  with invitations, take jobs and courses, grow older and have families.
- Faces show each resident's current mood, and the HUD shows their live portrait.
- The player can pause, speed up time, select a resident, queue actions, or
  give spoken commands.
- Save and load keep the whole block, including both households.

## Meta AI inside the game

| Feature | What it does in play |
| --- | --- |
| Muse Voice natural commands | Hold the talk key and speak naturally ("Eli, go to work", "have Cara chat with Dev", "invite Bea tonight"). Muse Voice transcribes the speech and Muse Spark 1.3 maps unusual phrasing to game commands. The model only proposes; the simulation checks every command and decides. An optional "Hey Meta" activator keeps the microphone open for hands-free play. |
| Muse Spark for residents | When a resident has several good options, Muse Spark 1.3 suggests which one fits the resident's traits and situation. The engine has final authority and uses its own choice when the model is slow or the proposal is not a legal action. |
| Block Gazette | Every in-game morning a newspaper opens (or press Y) with yesterday's real events, a SAM cut-out photo and a Muse Image illustration. |
| Muse Image | Generates residents' artwork and illustrates the Gazette front page. |
| SAM (Segment Anything) | Say or type what you are looking for ("find the woman in the red shirt", "show me a bike"). SAM outlines it on screen; the game identifies what is there, then follows that resident with the camera or highlights the object and what you can do there. SAM also cuts out the photo used on the Gazette front page. |

Each AI feature runs behind a transport interface with a local fallback, so the
game stays playable without network access.

## How it's built

- **Deterministic simulation.** The life simulation runs in plain C# with a
  fixed clock, so the same inputs give the same block. Rendering, audio and AI
  sit outside it and talk to it only through commands and events.
- **Save and load.** A versioned save format with schema checks; settings and
  input remaps are saved separately.
- **Accessibility.** Every action is remappable, no action needs a held key or
  a chord, menus never time out, and there are caption, text-size,
  reduced-motion and high-contrast cursor options.
- **Checked packages.** The game was built as small production packages,
  each with a test suite under `tests/production/` written before the code.
  [EVIDENCE.md](EVIDENCE.md) summarises what was verified. The full
  verification evidence lives in the development repository.
- **Art pipeline.** Characters, clothing and buildings are authored in Blender
  and exported as glTF into Unity URP (the `.blend` sources are kept in the
  development repository).

### Build from source

Requirements: Unity 6000.3.10f1 with Linux build support.

```bash
# Linux player build
Unity -batchmode -projectPath . -executeMethod ToolchainBuild.BuildLinux -quit -logFile build.log
```

Or open the project in the Unity Editor and press Play.

## Run it

1. Download the build zip from this repository's Releases page.
2. Unzip it and run the player: `Blockparty.exe` (Windows), `Blockparty.app` (macOS: the first time, open System Settings > Privacy & Security and choose "Open Anyway"), or the `.x86_64` file (Linux).

### Meta AI features

The live Meta AI features (voice, SAM Look, the Gazette pictures, resident choices) need your own Meta API key. Set the `MODEL_API_KEY` environment variable before launching, for example `MODEL_API_KEY=... ./Alpha.x86_64`. Without a key the game still runs and uses offline fallbacks. Settings > Free will turns residents' own choices on or off.

### Controls (defaults, all remappable in Settings)

| Action | Key |
| --- | --- |
| Pan camera | W A S D |
| Zoom in / out | E / Q |
| Rotate camera | R / F |
| Next / previous resident | N / B |
| Open / close action menu | Space / X |
| Confirm / cancel | Enter / Backspace |
| Speed up / slow down time | Page Up / Page Down |
| Pause | Escape |
| Map | M |
| Settings | O |
| Do now / add to queue | I / U |
| Inspect | H |
| Talk (voice commands) | Hold V |
| Look (SAM: find something on screen) | G |
| Block Gazette | Y |
| Select a resident | Left-click them or their portrait |
| Pan by dragging | Left-drag |

## Licence

Apache License 2.0. See [LICENSE](LICENSE).
