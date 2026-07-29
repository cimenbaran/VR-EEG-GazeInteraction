# OpenBCI Unity

A Unity plugin for streaming **OpenBCI Cyton** EEG data into VR and driving
**gaze + EEG** interaction. Built for a thesis project on EEG-gaze interaction in VR
(Quest 3 / PCVR).

EEG acquisition + signal processing runs in a small Python process
([`cyton_bridge.py`](../../cyton_bridge.py), using [BrainFlow](https://brainflow.org)),
which streams band powers over local UDP. Unity stays pure C# — no native libraries.

```
Cyton ──(dongle, serial)──> Python / BrainFlow ──(UDP JSON, localhost)──> Unity
```

## Layout

```
OpenBCI/
  Runtime/
    Logging/      OpenBCILogger      color-coded logging + history buffer + events
    Core/         OpenBCIReceiver    UDP listener, band powers, composite metrics
                  EEGData            bands, metrics, AdaptiveNormalizer
    Interaction/  IGazeProvider      gaze abstraction (head-gaze now, eye-gaze later)
                  HeadGazeProvider   headset forward-ray gaze
                  EEGSelectable      gaze-targetable, EEG-selectable object
                  GazeEEGInteractor  orchestrates Select / Choose / Move phases
                  EEGMover           moves the selected object from an EEG control value
  Examples/
    EEGCubeDemo   one-component demo that spawns cubes and wires everything
```

The runtime is an assembly (`OpenBCI.Runtime`). Your own game scripts that use the
plugin need an asmdef referencing it (the included `OpenBCI.Examples` shows how).

## Quick start

1. **Run the bridge** (synthetic, no hardware):
   ```bash
   python cyton_bridge.py --synthetic
   ```
   Real board: `--serial-port COM3 --mains 60` (Windows) or
   `--serial-port /dev/cu.usbserial-XXXX --mains 50` (macOS).

2. **In Unity**: add an empty GameObject, attach `EEGCubeDemo`, press Play.
   Set its **Phase** to step through the milestones:
   - **Select** (`cubeCount = 1`) — focus to select the cube.
   - **Choose** — head-gaze picks a cube, focus confirms it.
   - **Move** — the selected cube rises/falls with your focus level.

On the Mac, add the **XR Device Simulator** to look around; on Windows over Quest
Link, just look at a cube and concentrate.

## How selection works

- **Gaze** (head-ray) chooses the *candidate* cube.
- An **EEG control metric** (default `Engagement = beta / (alpha + theta)`) is
  normalized to `[0,1]` by a `SignalNormalizer`, so a fixed threshold works across people.
- Holding focus above `confirmThreshold` on a cube for `dwellTime` seconds **confirms**.

Swap the metric (`RelaxIndex`, `Alpha`, …) on the interactor to change what "focus" means.

### Normalization

`SignalNormalizer` has three modes:

| Mode | Baseline | Use for |
|------|----------|---------|
| `Calibrated` *(default)* | measured for `calibrationDuration`s, then **frozen** | **control** — sustained signal → sustained output |
| `Adaptive` | continuously chases the signal | exploring a signal; output reflects *change*, decays to 0.5 |
| `FixedRange` | none — linear map of `[rawMin, rawMax]` | fully direct, predictable mapping |

Sit still during calibration. Call `interactor.Recalibrate()` (or press **R** with
`KeyboardFocusSimulator` in the scene) to re-measure.

Stability knobs: `spreadStds` (sensitivity), `outputSmoothing` (jitter vs. lag),
`hysteresis` (threshold flicker), `dwellDecayRate` (how fast dwell drains on a dip).

## Roadmap

- [ ] Eye-gaze `IGazeProvider` (Quest 3 eye tracking via OpenXR) — drop-in replacement.
- [ ] In-VR log panel subscribing to `OpenBCILogger.OnLogged`.
- [ ] Real-board calibration UI.
