# VR-EEG-GazeInteraction

Brain–computer interaction in VR: select and manipulate objects using **eye/head gaze
combined with EEG signals** from an [OpenBCI Cyton](https://shop.openbci.com/products/cyton-biosensing-board-8-channel)
board. Built in **Unity 6.3 LTS** for **Quest 3 / PCVR**.

This is a thesis project. The EEG layer is packaged as a reusable Unity plugin
([`Assets/OpenBCI`](Assets/OpenBCI)) so it can be dropped into other projects.

---

## How it works

EEG acquisition and signal processing run in a small **Python process** (using
[BrainFlow](https://brainflow.org), the officially supported OpenBCI library). It reads
the board, filters the signal, computes band powers, and streams them to Unity over
**local UDP**. Unity stays pure C# — no native libraries, no Android build pain.

```
Cyton ──(RFduino dongle, serial)──> Python / BrainFlow
                                       │  filter + band-power extraction
                                       ▼
                                  UDP JSON (localhost / LAN)
                                       ▼
                                   Unity (C#)
                                       │  gaze raycast + EEG control value
                                       ▼
                                 Select / Choose / Move objects
```

Why a Python bridge instead of all-in-Unity: BrainFlow's native C# binding is tested on
Windows x64 only, while Quest 3 is ARM64 Android. Keeping the native code in a companion
Python process (on the PC or a Raspberry Pi) sidesteps that entirely and keeps the full
Python analysis ecosystem available. See [Assets/OpenBCI/README.md](Assets/OpenBCI/README.md)
for the plugin internals.

---

## Requirements

- **Unity** 6000.3.x (6.3 LTS) with the VR template
- **Python** 3.9+ with `brainflow` and `numpy`
- **OpenBCI Cyton** board + RFduino USB dongle (or run fully synthetic, no hardware)
- A **Meta Quest 3** for VR (PCVR over Meta Quest Link on Windows); a Mac can develop
  everything except in-headset PCVR (use the XR Device Simulator)

---

## Quick start

### 1. Python bridge

```bash
python -m venv eeg
source eeg/bin/activate          # Windows: eeg\Scripts\activate
pip install brainflow numpy

# no hardware — synthetic signal:
python cyton_bridge.py --synthetic

# real board:
python cyton_bridge.py --serial-port /dev/cu.usbserial-XXXX --mains 50   # macOS
python cyton_bridge.py --serial-port COM3 --mains 60                     # Windows
```

> The serial port is exclusive — **fully quit the OpenBCI GUI** before running the bridge.
> Use `--mains 50` (EU/UK/TR) or `--mains 60` (US) to match your power-line frequency.

**Selecting channels.** Band powers are averaged across channels, so dead or railed
electrodes drag the average toward noise. Use only the electrodes with good contact
(numbered as in the OpenBCI GUI):

```bash
python cyton_bridge.py --serial-port COM3 --channels 3,6,8 --quality-interval 2
```

`--quality-interval` prints an RMS report for **all** channels every N seconds, marking
the selected ones with `*`, so you can see which electrodes are usable:

```
[quality]  1:   0.00µV DEAD |  2:   0.00µV DEAD | *3:  12.40µV ok | ... | *8:  15.20µV ok
```

`DEAD` (<0.1 µV RMS) means no contact / railed; `NOISY` (>100 µV RMS) means artifacts or
a loose electrode. Aim for roughly 5–50 µV RMS.

### 2. Unity

1. Open the project in Unity 6.3.
2. Add an empty GameObject and attach **`EEGCubeDemo`** (from `OpenBCI.Examples`).
3. Press **Play**. Set the demo's **Phase** to walk through the milestones.

Add **`EEGDebugHUD`** to any GameObject for a live on-screen readout of band powers,
metrics, and interaction state.

---

## Interaction phases

| Phase | What you do |
|-------|-------------|
| **Select** | Gaze at a single cube, hold EEG focus to select it |
| **Choose** | Head-gaze picks among several cubes, focus confirms |
| **Move**   | The selected cube rises/falls with your focus level |

**Selection model:** head-gaze chooses the candidate; an EEG metric
(`Engagement = beta / (alpha + theta)`) is normalized to a 0–1 **control value**;
holding it above a threshold for a dwell time confirms. The `IGazeProvider` seam
allows swapping head-gaze for Quest 3 eye-tracking later.

### Calibration

On entering Play mode the interactor measures your **resting baseline for ~5 seconds**
— sit still until the HUD stops showing `CALIBRATING`. The baseline is then **frozen**,
so a sustained rise in the metric produces a sustained high control value.

Press **R** to re-measure the baseline at any time (after adjusting electrodes, or if
control feels stuck high/low after a long session).

> Normalizer modes: `Calibrated` (default, frozen baseline — use this for control),
> `Adaptive` (baseline keeps chasing the signal, so output reflects *change* and decays
> back to 0.5), and `FixedRange` (direct linear map of the raw metric).

If you can't reach the threshold, lower `spreadStds`. If the value jitters, raise
`outputSmoothing` or `hysteresis`.

---

## Testing without electrodes

- **`KeyboardFocusSimulator`** — hold **Space** to simulate focus (value ramps up),
  release to unfocus. Drives the full selection/move logic with no board needed.
  **R** recalibrates (works with the real board too).
- Or tick **Use Manual Control** on the interactor and drag the slider in Play mode.

---

## Project layout

```
cyton_bridge.py            Python EEG → UDP bridge (BrainFlow)
Assets/
  OpenBCI/                 the reusable plugin (UPM-ready: package.json + asmdefs)
    Runtime/
      Logging/             color-coded logger with history + events
      Core/                UDP receiver, band/metric access, adaptive normalizer
      Interaction/         gaze + EEG selection / movement
    Examples/              demo, keyboard simulator, debug HUD
```

---

## Roadmap

- [ ] Eye-gaze provider (Quest 3 eye tracking via OpenXR)
- [ ] Discrete blink / jaw-clench "click" trigger
- [ ] In-VR (world-space) signal & log panel
- [ ] Per-user calibration UI

---

## License

MIT
