#!/usr/bin/env python3
"""
cyton_bridge.py — BrainFlow Cyton → UDP bridge for Unity

Usage:
  python cyton_bridge.py --synthetic                          # test without hardware
  python cyton_bridge.py --serial-port COM3 --mains 60       # Windows, 60 Hz power
  python cyton_bridge.py --serial-port /dev/cu.usbserial-DM  # macOS

  # only use electrodes 3, 6 and 8 (numbered as in the OpenBCI GUI), and print a
  # per-channel quality report every 2 s so you can spot dead/railed electrodes:
  python cyton_bridge.py --serial-port COM3 --channels 3,6,8 --quality-interval 2

Streams JSON to udp://127.0.0.1:12345 (override with --host / --port).
"""

import argparse
import json
import socket
import time

import numpy as np
from brainflow.board_shim import BoardShim, BrainFlowInputParams, BoardIds
from brainflow.data_filter import DataFilter, FilterTypes, DetrendOperations, WindowOperations


SAMPLE_RATE = 250          # Cyton hardware sample rate (Hz)
WINDOW_SECS = 2            # seconds of data used for band-power FFT
SEND_HZ = 20               # how often to send a UDP packet


def compute_band_powers(data: np.ndarray, sr: int) -> dict:
    """Compute all band powers from a single Welch PSD pass."""
    nfft = DataFilter.get_nearest_power_of_two(sr)
    psd = DataFilter.get_psd_welch(data, nfft, nfft // 2, sr,
                                   WindowOperations.BLACKMAN_HARRIS.value)
    bands = {
        "delta": (1.0, 4.0),
        "theta": (4.0, 8.0),
        "alpha": (8.0, 13.0),
        "beta":  (13.0, 30.0),
        "gamma": (30.0, 50.0),
    }
    return {name: float(DataFilter.get_band_power(psd, low, high))
            for name, (low, high) in bands.items()}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--synthetic", action="store_true",
                        help="Use BrainFlow synthetic board (no hardware needed)")
    parser.add_argument("--serial-port", default="",
                        help="Serial port of the Cyton dongle, e.g. COM3 or /dev/cu.usbserial-XX")
    parser.add_argument("--mains", type=int, default=50,
                        help="Mains frequency for notch filter: 50 (EU/UK) or 60 (US)")
    parser.add_argument("--host", default="127.0.0.1",
                        help="UDP destination host (use Quest LAN IP for standalone streaming)")
    parser.add_argument("--port", type=int, default=12345,
                        help="UDP destination port")
    parser.add_argument("--channels", default="",
                        help="Comma-separated channels to use, numbered as in the OpenBCI "
                             "GUI (1-8), e.g. --channels 3,6,8. Default: all channels.")
    parser.add_argument("--quality-interval", type=float, default=0.0,
                        help="Seconds between per-channel quality reports (0 = off). "
                             "Prints RMS per channel so you can spot dead/railed electrodes.")
    args = parser.parse_args()

    BoardShim.enable_dev_board_logger()

    params = BrainFlowInputParams()
    if args.synthetic:
        board_id = BoardIds.SYNTHETIC_BOARD
        print("[bridge] Using SYNTHETIC board — no hardware needed")
    else:
        if not args.serial_port:
            parser.error("--serial-port is required unless --synthetic is set")
        board_id = BoardIds.CYTON_BOARD
        params.serial_port = args.serial_port
        print(f"[bridge] Connecting to Cyton on {args.serial_port}")

    board = BoardShim(board_id, params)
    all_eeg_channels = BoardShim.get_eeg_channels(board_id)
    sr = BoardShim.get_sampling_rate(board_id)
    window = sr * WINDOW_SECS

    # Resolve --channels (1-based, matching the OpenBCI GUI) to data-array rows.
    if args.channels.strip():
        try:
            picked = [int(c) for c in args.channels.split(",") if c.strip()]
        except ValueError:
            parser.error("--channels must be comma-separated integers, e.g. 3,6,8")
        for c in picked:
            if not 1 <= c <= len(all_eeg_channels):
                parser.error(f"channel {c} out of range 1..{len(all_eeg_channels)}")
        if not picked:
            parser.error("--channels selected no channels")
        channel_numbers = picked
    else:
        channel_numbers = list(range(1, len(all_eeg_channels) + 1))

    eeg_channels = [all_eeg_channels[c - 1] for c in channel_numbers]
    print(f"[bridge] Using channels {channel_numbers} "
          f"({len(channel_numbers)} of {len(all_eeg_channels)})")

    board.prepare_session()
    board.start_stream()
    print(f"[bridge] Streaming → udp://{args.host}:{args.port}  ({SEND_HZ} Hz)")

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    interval = 1.0 / SEND_HZ
    last_send = time.monotonic()
    last_quality = 0.0

    try:
        while True:
            now = time.monotonic()
            if now - last_send < interval:
                time.sleep(0.001)
                continue
            last_send = now

            data = board.get_current_board_data(window)
            if data.shape[1] < window:
                continue  # not enough samples yet

            payload = {"timestamp": time.time()}
            band_accum = {"delta": [], "theta": [], "alpha": [], "beta": [], "gamma": []}

            for ch in eeg_channels:
                ch_data = data[ch].copy()
                DataFilter.detrend(ch_data, DetrendOperations.CONSTANT.value)
                DataFilter.perform_bandpass(ch_data, sr, 1.0, 50.0, 4,
                                            FilterTypes.BUTTERWORTH.value, 0)
                DataFilter.perform_bandstop(ch_data, sr,
                                            float(args.mains) - 2.0,
                                            float(args.mains) + 2.0, 4,
                                            FilterTypes.BUTTERWORTH.value, 0)
                bp = compute_band_powers(ch_data, sr)
                for band, val in bp.items():
                    band_accum[band].append(val)

            # average across channels, log-scale so Unity gets manageable numbers
            for band, vals in band_accum.items():
                payload[band] = float(np.log10(np.mean(vals) + 1e-10))

            # latest raw sample (µV) for the selected channels, for waveform display
            payload["raw"] = [float(data[ch, -1]) for ch in eeg_channels]
            payload["channels"] = channel_numbers

            msg = json.dumps(payload).encode()
            sock.sendto(msg, (args.host, args.port))

            # periodic per-channel quality report over ALL channels, so you can see
            # which electrodes are usable and adjust --channels accordingly
            if args.quality_interval > 0 and now - last_quality >= args.quality_interval:
                last_quality = now
                parts = []
                for i, ch in enumerate(all_eeg_channels, start=1):
                    sig = data[ch]
                    rms = float(np.sqrt(np.mean(np.square(sig - np.mean(sig)))))
                    if rms < 0.1:
                        state = "DEAD"      # flat / railed / no contact
                    elif rms > 100.0:
                        state = "NOISY"     # artifact or bad contact
                    else:
                        state = "ok"
                    mark = "*" if i in channel_numbers else " "
                    parts.append(f"{mark}{i}:{rms:7.2f}µV {state}")
                print("[quality] " + " | ".join(parts))

    except KeyboardInterrupt:
        print("\n[bridge] Stopping...")
    finally:
        board.stop_stream()
        board.release_session()
        sock.close()


if __name__ == "__main__":
    main()
