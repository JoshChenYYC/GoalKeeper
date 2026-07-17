"""Capture snapshots and run the optional controller processing cycle.

Standalone use captures a JPEG, sends it to perception, and appends the neutral
observation to JSONL. A configured FocusSessionController additionally persists
authoritative state and invokes its typed ReasoningPort in the same worker.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import threading
import time
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any, Callable

from camera_adapter import OpenCVPreviewAdapter, open_camera
from controller import FocusSessionController
from domain import SessionContract, SessionState, SnapshotStatus
from perception import (
    OBSERVATION_SCHEMA,
    PERCEPTION_PROMPT,
    OpenAIPerceptionAdapter,
    jpeg_data_url,
)
from ports import CameraPort, CameraPreviewPort, PerceptionPort


DEFAULT_INTERVAL_SECONDS = 10.0
DEFAULT_MODEL = "gpt-5.6-luna"


@dataclass(frozen=True)
class Snapshot:
    """One saved webcam frame waiting for perception."""

    sequence: int
    captured_at: datetime
    image_name: str
    jpeg: bytes
    controller_snapshot_id: str | None = None
    session_id: str | None = None
    session_version: int | None = None
    captured_state: SessionState | None = None


@dataclass(frozen=True)
class PreflightCapture:
    """A setup snapshot that passed validation and user confirmation."""

    captured_at: datetime
    confirmed_at: datetime
    jpeg: bytes
    observation: dict[str, Any]


@dataclass
class UploadStats:
    observed: int = 0
    api_errors: int = 0


class LatestSnapshotBuffer:
    """A thread-safe, one-slot buffer in which the newest snapshot wins."""

    def __init__(self) -> None:
        self._condition = threading.Condition()
        self._pending: Snapshot | None = None
        self._closed = False

    def submit(self, snapshot: Snapshot) -> Snapshot | None:
        """Store snapshot and return the older pending snapshot it replaced."""
        with self._condition:
            if self._closed:
                raise RuntimeError("cannot submit a snapshot after the buffer is closed")
            replaced = self._pending
            self._pending = snapshot
            self._condition.notify()
            return replaced

    def take(self) -> Snapshot | None:
        """Wait for work; after close, drain the final pending snapshot then stop."""
        with self._condition:
            self._condition.wait_for(lambda: self._pending is not None or self._closed)
            if self._pending is None:
                return None
            snapshot = self._pending
            self._pending = None
            return snapshot

    def close(self) -> None:
        with self._condition:
            self._closed = True
            self._condition.notify_all()


class JsonlLog:
    """Small locked JSONL writer shared by capture and upload threads."""

    def __init__(self, path: Path) -> None:
        self.path = path
        self._lock = threading.Lock()
        self.path.touch(exist_ok=True)

    def append(self, record: dict[str, Any]) -> None:
        line = json.dumps(record, ensure_ascii=False, separators=(",", ":"))
        with self._lock, self.path.open("a", encoding="utf-8") as log:
            log.write(line)
            log.write("\n")


def positive_number(value: str) -> float:
    number = float(value)
    if number <= 0:
        raise argparse.ArgumentTypeError("must be greater than zero")
    return number


def camera_index(value: str) -> int:
    index = int(value)
    if index < 0:
        raise argparse.ArgumentTypeError("must be zero or greater")
    return index


def advance_capture_tick(previous_tick: float, interval: float, now: float) -> float:
    """Return the next fixed-grid tick after now without scheduling a burst."""
    next_tick = previous_tick + interval
    if next_tick < now:
        missed_ticks = int((now - next_tick) // interval) + 1
        next_tick += missed_ticks * interval
    return next_tick


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Capture webcam snapshots and turn them into JSON observations"
    )
    parser.add_argument(
        "--interval",
        type=positive_number,
        default=DEFAULT_INTERVAL_SECONDS,
        help=f"seconds between snapshots (default: {DEFAULT_INTERVAL_SECONDS:g})",
    )
    parser.add_argument(
        "--duration",
        type=positive_number,
        help="session length in minutes (default: run until Ctrl+C)",
    )
    parser.add_argument(
        "--camera",
        type=camera_index,
        default=0,
        help="OpenCV camera index (default: 0)",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=Path("captures"),
        help="directory in which session folders are created (default: captures)",
    )
    parser.add_argument(
        "--model",
        default=os.environ.get("OPENAI_MODEL", DEFAULT_MODEL),
        help=f"vision-capable OpenAI model (default: OPENAI_MODEL or {DEFAULT_MODEL})",
    )
    parser.add_argument(
        "--detail",
        choices=("low", "high", "auto", "original"),
        default="low",
        help="image detail sent to the API (default: low)",
    )
    parser.add_argument(
        "--jpeg-quality",
        type=int,
        choices=range(1, 101),
        default=85,
        metavar="1-100",
        help="saved/sent JPEG quality (default: 85)",
    )
    parser.add_argument(
        "--api-timeout",
        type=positive_number,
        default=30.0,
        help="timeout for each API attempt in seconds (default: 30)",
    )
    parser.add_argument(
        "--max-retries",
        type=int,
        choices=range(0, 6),
        default=2,
        metavar="0-5",
        help="SDK retries for transient API failures (default: 2)",
    )
    parser.add_argument(
        "--image",
        type=Path,
        help="send one existing JPEG instead of opening the webcam (API smoke test)",
    )
    return parser.parse_args(argv)


DEFAULT_CAMERA_PREVIEW = OpenCVPreviewAdapter()


def encode_jpeg(
    frame: Any,
    quality: int,
    *,
    preview: CameraPreviewPort | None = None,
) -> bytes:
    """Encode through the configured camera adapter."""
    return (preview or DEFAULT_CAMERA_PREVIEW).encode_jpeg(frame, quality)


def draw_preflight_status(
    frame: Any,
    message: str,
    *,
    color: tuple[int, int, int] = (255, 255, 255),
    preview: CameraPreviewPort | None = None,
) -> None:
    (preview or DEFAULT_CAMERA_PREVIEW).draw_status(frame, message, color=color)


def capture_setup_frame(
    camera: CameraPort,
    jpeg_quality: int,
    *,
    preview: CameraPreviewPort | None = None,
) -> tuple[datetime, bytes, Any] | None:
    """Show live preview until Space captures a frame or Esc cancels."""
    print(
        "Camera preflight: press Space to capture this view or Esc to cancel.",
        flush=True,
    )
    camera_preview = preview or DEFAULT_CAMERA_PREVIEW
    while True:
        success, frame = camera.read()
        if not success:
            raise RuntimeError("camera stopped responding during preflight")

        draw_preflight_status(
            frame, "SPACE: capture   ESC: cancel", preview=camera_preview
        )
        key = camera_preview.wait_key(30)
        if key == 27:
            return None
        if key == 32:
            captured_at = datetime.now().astimezone()
            jpeg = encode_jpeg(frame, jpeg_quality, preview=camera_preview)
            draw_preflight_status(
                frame, "Analyzing camera setup...", preview=camera_preview
            )
            camera_preview.wait_key(1)
            return captured_at, jpeg, frame


def preflight_validation_error(observation: dict[str, Any]) -> str | None:
    """Return an actionable rejection reason, or None when setup is valid."""
    quality = observation["image_quality"]
    if quality["value"] != "adequate":
        limitations = "; ".join(quality["limitations"]) or "unspecified limitation"
        return f"image quality is {quality['value']}: {limitations}"

    people_count = observation["people_count"]
    if people_count is None:
        return "the number of visible people is indeterminate"
    if people_count == 0:
        return "no person is visible"
    if people_count != 1:
        return f"expected exactly one visible person, found {people_count}"
    return None


def run_camera_preflight(
    perception: PerceptionPort,
    camera: CameraPort,
    args: argparse.Namespace,
    *,
    input_func: Callable[[str], str] = input,
    preview: CameraPreviewPort | None = None,
) -> PreflightCapture | None:
    """Capture, validate, and explicitly confirm the camera setup."""
    camera_preview = preview or DEFAULT_CAMERA_PREVIEW
    window_created = False
    try:
        camera_preview.open()
        window_created = True
        while True:
            setup = capture_setup_frame(
                camera, args.jpeg_quality, preview=camera_preview
            )
            if setup is None:
                return None
            captured_at, jpeg, frame = setup

            try:
                observation = dict(perception.observe(jpeg))
            except Exception as error:
                message = f"camera validation failed: {error}"
                print(message, file=sys.stderr, flush=True)
                draw_preflight_status(
                    frame,
                    "Validation error - returning to preview",
                    color=(0, 0, 255),
                    preview=camera_preview,
                )
                camera_preview.wait_key(750)
                continue

            validation_error = preflight_validation_error(observation)
            if validation_error is not None:
                print(f"Camera setup rejected: {validation_error}", flush=True)
                draw_preflight_status(
                    frame,
                    "Setup rejected - returning to preview",
                    color=(0, 0, 255),
                    preview=camera_preview,
                )
                camera_preview.wait_key(750)
                continue

            draw_preflight_status(
                frame,
                "Setup valid - confirm in the terminal",
                color=(0, 255, 0),
                preview=camera_preview,
            )
            camera_preview.wait_key(1)
            print("Camera setup is usable and exactly one person is visible.", flush=True)
            while True:
                choice = input_func(
                    "Use this camera view? [y]es / [r]etry / [q]uit: "
                ).strip().lower()
                if choice in {"y", "yes"}:
                    return PreflightCapture(
                        captured_at=captured_at,
                        confirmed_at=datetime.now().astimezone(),
                        jpeg=jpeg,
                        observation=observation,
                    )
                if choice in {"r", "retry"}:
                    break
                if choice in {"q", "quit"}:
                    return None
                print("Enter y, r, or q.", flush=True)
    finally:
        if window_created:
            camera_preview.close()


def write_preflight_artifacts(session_dir: Path, preflight: PreflightCapture) -> None:
    """Persist the confirmed setup image separately from monitoring evidence."""
    image_name = "preflight.jpg"
    (session_dir / image_name).write_bytes(preflight.jpeg)
    record = {
        "captured_at": preflight.captured_at.isoformat(timespec="milliseconds"),
        "confirmed_at": preflight.confirmed_at.isoformat(timespec="milliseconds"),
        "image": image_name,
        "observation": preflight.observation,
    }
    (session_dir / "preflight.json").write_text(
        json.dumps(record, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )


def make_record(
    *,
    sequence: int,
    captured_at: datetime,
    image_name: str,
    observation: dict[str, Any],
) -> dict[str, Any]:
    return {
        "sequence": sequence,
        "captured_at": captured_at.isoformat(timespec="milliseconds"),
        "image": image_name,
        "observation": observation,
    }


def make_capture_event(
    snapshot: Snapshot, status: str, *, error: str | None = None
) -> dict[str, Any]:
    event: dict[str, Any] = {
        "sequence": snapshot.sequence,
        "captured_at": snapshot.captured_at.isoformat(timespec="milliseconds"),
        "image": snapshot.image_name,
        "status": status,
    }
    if status == "api_error":
        event["error"] = error or "unknown API error"
    return event


def print_observation(record: dict[str, Any]) -> None:
    observation = record["observation"]
    behaviors = ", ".join(
        item["behavior"] for item in observation["observations"]
    ) or "none"
    print(
        f"[{record['captured_at']}] {record['image']}: "
        f"quality={observation['image_quality']['value']} | "
        f"people={observation['people_count']} | cues: {behaviors}",
        flush=True,
    )


def process_jpeg(
    perception: PerceptionPort,
    *,
    sequence: int,
    jpeg: bytes,
    captured_at: datetime,
    image_name: str,
    observation_log: JsonlLog | None = None,
) -> dict[str, Any]:
    observation = dict(perception.observe(jpeg))
    record = make_record(
        sequence=sequence,
        captured_at=captured_at,
        image_name=image_name,
        observation=observation,
    )
    if observation_log is not None:
        observation_log.append(record)
    return record


def upload_snapshots(
    perception: PerceptionPort,
    *,
    buffer: LatestSnapshotBuffer,
    observation_log: JsonlLog,
    event_log: JsonlLog,
    stats: UploadStats,
    controller: FocusSessionController | None = None,
) -> None:
    """Process complete perception/reasoning cycles until the buffer is drained."""
    while True:
        snapshot = buffer.take()
        if snapshot is None:
            return

        try:
            record = process_jpeg(
                perception,
                sequence=snapshot.sequence,
                jpeg=snapshot.jpeg,
                captured_at=snapshot.captured_at,
                image_name=snapshot.image_name,
            )
        except Exception as error:
            stats.api_errors += 1
            if controller is not None and snapshot.controller_snapshot_id is not None:
                try:
                    controller.finalize_snapshot(
                        snapshot.controller_snapshot_id,
                        SnapshotStatus.API_ERROR,
                        error=str(error),
                    )
                except Exception as controller_error:
                    print(
                        f"controller could not persist API failure: {controller_error}",
                        file=sys.stderr,
                        flush=True,
                    )
            event_log.append(
                make_capture_event(snapshot, "api_error", error=str(error))
            )
            print(
                f"[{snapshot.captured_at.isoformat(timespec='seconds')}] "
                f"{snapshot.image_name}: API error: {error}",
                file=sys.stderr,
                flush=True,
            )
            continue

        controlled_observation = None
        if controller is not None and snapshot.controller_snapshot_id is not None:
            try:
                controlled_observation = controller.persist_observation(
                    snapshot.controller_snapshot_id,
                    record["observation"],
                )
                record.update(
                    {
                        "session_id": controlled_observation.session_id,
                        "session_version": controlled_observation.session_version,
                        "snapshot_id": controlled_observation.snapshot_id,
                        "observation_id": controlled_observation.id,
                        "processed_at": controlled_observation.processed_at.isoformat(
                            timespec="milliseconds"
                        ),
                        "captured_state": controlled_observation.captured_state.value,
                        "reasoning_eligible": controlled_observation.reasoning_eligible,
                    }
                )
            except Exception as controller_error:
                print(
                    f"controller could not persist observation: {controller_error}",
                    file=sys.stderr,
                    flush=True,
                )

        observation_log.append(record)
        stats.observed += 1
        event_log.append(make_capture_event(snapshot, "observed"))
        print_observation(record)

        if controlled_observation is not None:
            try:
                controller.evaluate_observation(controlled_observation)
            except Exception as reasoning_error:
                try:
                    controller.record_reasoning_error(
                        controlled_observation, reasoning_error
                    )
                except Exception as controller_error:
                    print(
                        "controller could not persist reasoning failure: "
                        f"{controller_error}",
                        file=sys.stderr,
                        flush=True,
                    )
                print(
                    f"[{record['captured_at']}] reasoning error: {reasoning_error}",
                    file=sys.stderr,
                    flush=True,
                )


def run_image_smoke_test(
    perception: PerceptionPort, args: argparse.Namespace
) -> None:
    image_path = args.image
    if not image_path.is_file():
        raise RuntimeError(f"image does not exist: {image_path}")
    if image_path.suffix.lower() not in {".jpg", ".jpeg"}:
        raise RuntimeError("--image currently accepts JPEG files only")

    record = process_jpeg(
        perception,
        sequence=1,
        jpeg=image_path.read_bytes(),
        captured_at=datetime.now().astimezone(),
        image_name=image_path.name,
    )
    print(json.dumps(record, indent=2, ensure_ascii=False))


def run_capture_session(
    perception: PerceptionPort,
    args: argparse.Namespace,
    *,
    controller: FocusSessionController | None = None,
    contract: SessionContract | None = None,
    camera_factory: Callable[[int], CameraPort] | None = None,
    preview: CameraPreviewPort | None = None,
) -> bool:
    if (controller is None) != (contract is None):
        raise ValueError("controller and contract must be provided together")
    camera = (camera_factory or open_camera)(args.camera)
    buffer: LatestSnapshotBuffer | None = None
    uploader: threading.Thread | None = None
    upload_stats = UploadStats()
    observation_log: JsonlLog | None = None
    captured = 0
    superseded = 0
    session_dir: Path | None = None
    controller_session = None

    try:
        preflight = run_camera_preflight(
            perception, camera, args, preview=preview
        )
        if preflight is None:
            print("Camera preflight canceled; monitoring did not start.", flush=True)
            return False

        started_at = datetime.now().astimezone()
        session_dir = args.output_dir / f"session-{started_at:%Y%m%d-%H%M%S}"
        session_dir.mkdir(parents=True, exist_ok=False)
        write_preflight_artifacts(session_dir, preflight)
        if controller is not None and contract is not None:
            controller_session = controller.start_monitoring(
                contract,
                session_dir=session_dir,
                started_at=started_at,
            )
        observation_log = JsonlLog(session_dir / "observations.jsonl")
        event_log = JsonlLog(session_dir / "capture_events.jsonl")

        buffer = LatestSnapshotBuffer()
        uploader = threading.Thread(
            target=upload_snapshots,
            kwargs={
                "perception": perception,
                "buffer": buffer,
                "observation_log": observation_log,
                "event_log": event_log,
                "stats": upload_stats,
                "controller": controller,
            },
            name="perception-uploader",
        )
        uploader.start()

        deadline = (
            time.monotonic() + args.duration * 60
            if controller_session is None and args.duration is not None
            else None
        )
        next_capture = time.monotonic()
        print(
            f"Capturing camera {args.camera} every {args.interval:g}s to "
            f"{session_dir}. Press Ctrl+C to stop.",
            flush=True,
        )

        while deadline is None or time.monotonic() < deadline:
            if controller is not None and controller_session is not None:
                current_session = controller.advance_time(controller_session.id)
                if not current_session.state.is_active:
                    break
            wait_seconds = next_capture - time.monotonic()
            if wait_seconds > 0:
                if deadline is not None:
                    wait_seconds = min(wait_seconds, deadline - time.monotonic())
                if wait_seconds > 0:
                    time.sleep(wait_seconds)
            if deadline is not None and time.monotonic() >= deadline:
                break
            if controller is not None and controller_session is not None:
                current_session = controller.advance_time(controller_session.id)
                if not current_session.state.is_active:
                    break

            success, frame = camera.read()
            captured_at = datetime.now().astimezone()
            if not success:
                print(
                    f"[{captured_at.isoformat(timespec='seconds')}] camera read failed; "
                    "will retry at the next interval",
                    file=sys.stderr,
                    flush=True,
                )
                next_capture = advance_capture_tick(
                    next_capture, args.interval, time.monotonic()
                )
                continue

            jpeg = encode_jpeg(frame, args.jpeg_quality, preview=preview)
            image_name = f"{captured_at:%Y%m%d-%H%M%S-%f}.jpg"
            (session_dir / image_name).write_bytes(jpeg)
            captured += 1
            controlled_snapshot = None
            if controller is not None and controller_session is not None:
                controlled_snapshot = controller.record_snapshot(
                    controller_session.id,
                    sequence=captured,
                    captured_at=captured_at,
                    image=image_name,
                )
            snapshot = Snapshot(
                sequence=captured,
                captured_at=captured_at,
                image_name=image_name,
                jpeg=jpeg,
                controller_snapshot_id=(
                    controlled_snapshot.id if controlled_snapshot else None
                ),
                session_id=(
                    controlled_snapshot.session_id if controlled_snapshot else None
                ),
                session_version=(
                    controlled_snapshot.session_version if controlled_snapshot else None
                ),
                captured_state=(
                    controlled_snapshot.captured_state if controlled_snapshot else None
                ),
            )
            replaced = buffer.submit(snapshot)
            if replaced is not None:
                superseded += 1
                if controller is not None and replaced.controller_snapshot_id is not None:
                    controller.finalize_snapshot(
                        replaced.controller_snapshot_id, SnapshotStatus.SUPERSEDED
                    )
                event_log.append(make_capture_event(replaced, "superseded"))
                print(
                    f"[{captured_at.isoformat(timespec='seconds')}] "
                    f"{replaced.image_name}: superseded by {image_name}",
                    flush=True,
                )

            next_capture = advance_capture_tick(
                next_capture, args.interval, time.monotonic()
            )
    except KeyboardInterrupt:
        if uploader is None:
            print("\nCamera preflight canceled; monitoring did not start.", flush=True)
        else:
            print("\nStopping capture...", flush=True)
    finally:
        stopped_monotonic = time.monotonic()
        stopped_at = datetime.now().astimezone()
        camera.release()
        if buffer is not None:
            buffer.close()
        if uploader is not None:
            if uploader.is_alive():
                print(
                    "Finishing the active and newest pending API request...",
                    flush=True,
                )
            uploader.join()
        if controller is not None and controller_session is not None:
            current_session = controller.get_session(controller_session.id)
            if current_session.state.is_active:
                controller.end_early(
                    controller_session.id,
                    reason="capture_stopped",
                    monotonic_now=stopped_monotonic,
                    ended_at=stopped_at,
                )

    if session_dir is None or observation_log is None:
        return False
    print(
        f"Session complete: {captured} snapshot(s) captured, "
        f"{upload_stats.observed} observed, {superseded} superseded, "
        f"{upload_stats.api_errors} API error(s). Results: {observation_log.path}",
        flush=True,
    )
    return True


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    if not os.environ.get("OPENAI_API_KEY"):
        print("error: OPENAI_API_KEY is not set", file=sys.stderr)
        return 2

    from openai import OpenAI

    client = OpenAI(timeout=args.api_timeout, max_retries=args.max_retries)
    perception = OpenAIPerceptionAdapter(
        client, model=args.model, detail=args.detail
    )
    try:
        if args.image is not None:
            run_image_smoke_test(perception, args)
        else:
            run_capture_session(perception, args)
    except Exception as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
