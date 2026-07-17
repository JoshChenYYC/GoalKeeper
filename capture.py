"""Capture periodic webcam snapshots and send them to the perception API.

This module deliberately stops at perception. It captures a JPEG, sends the
image to the OpenAI Responses API, and appends the returned observation to a
JSONL file for the reasoning component to consume later.
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import sys
import threading
import time
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any

import cv2
from openai import OpenAI


DEFAULT_INTERVAL_SECONDS = 10.0
DEFAULT_MODEL = "gpt-5.6-luna"
WARMUP_FRAMES = 8

PERCEPTION_PROMPT = """\
You are the perception component of an accountability application. Describe
only what is directly observable in this single webcam snapshot. Do not infer
the person's intent, judge whether they are following a goal, recommend an
intervention, or use observations from other snapshots. If no person is
visible, set user_present to false. Only list a possible distraction when it
is actually visible in this image.
"""

OBSERVATION_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "user_present": {"type": "boolean"},
        "user_state": {
            "type": "string",
            "description": "Visible posture/location, such as sitting at desk or absent.",
        },
        "objects": {
            "type": "array",
            "items": {"type": "string"},
            "description": "Notable visible objects relevant to the current activity.",
        },
        "activity": {
            "type": "string",
            "description": "The directly observable activity, without guessing intent.",
        },
        "possible_distractions": {
            "type": "array",
            "items": {"type": "string"},
            "description": "Visible potential distractions; empty when none are visible.",
        },
        "confidence": {
            "type": "number",
            "minimum": 0,
            "maximum": 1,
        },
    },
    "required": [
        "user_present",
        "user_state",
        "objects",
        "activity",
        "possible_distractions",
        "confidence",
    ],
    "additionalProperties": False,
}


@dataclass(frozen=True)
class Snapshot:
    """One saved webcam frame waiting for perception."""

    sequence: int
    captured_at: datetime
    image_name: str
    jpeg: bytes


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


def encode_jpeg(frame: Any, quality: int) -> bytes:
    """Encode one OpenCV frame to the exact JPEG bytes saved and uploaded."""
    success, encoded = cv2.imencode(
        ".jpg", frame, [int(cv2.IMWRITE_JPEG_QUALITY), quality]
    )
    if not success:
        raise RuntimeError("OpenCV could not encode the captured frame as JPEG")
    return encoded.tobytes()


def jpeg_data_url(jpeg: bytes) -> str:
    encoded = base64.b64encode(jpeg).decode("ascii")
    return f"data:image/jpeg;base64,{encoded}"


def observe_snapshot(
    client: OpenAI,
    *,
    jpeg: bytes,
    model: str,
    detail: str,
) -> dict[str, Any]:
    """Send one JPEG to OpenAI and return its schema-constrained observation."""
    response = client.responses.create(
        model=model,
        input=[
            {"role": "system", "content": PERCEPTION_PROMPT},
            {
                "role": "user",
                "content": [
                    {
                        "type": "input_text",
                        "text": "Return the structured observation for this snapshot.",
                    },
                    {
                        "type": "input_image",
                        "image_url": jpeg_data_url(jpeg),
                        "detail": detail,
                    },
                ],
            },
        ],
        text={
            "format": {
                "type": "json_schema",
                "name": "room_observation",
                "schema": OBSERVATION_SCHEMA,
                "strict": True,
            }
        },
    )
    if not response.output_text:
        raise RuntimeError("the perception API returned no text output")
    try:
        return json.loads(response.output_text)
    except json.JSONDecodeError as error:
        raise RuntimeError("the perception API returned invalid JSON") from error


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
    distractions = ", ".join(observation["possible_distractions"]) or "none"
    print(
        f"[{record['captured_at']}] {record['image']}: "
        f"{observation['user_state']} | {observation['activity']} | "
        f"distractions: {distractions}",
        flush=True,
    )


def open_camera(index: int) -> cv2.VideoCapture:
    camera = cv2.VideoCapture(index)
    if not camera.isOpened():
        camera.release()
        raise RuntimeError(
            f"could not open camera {index}; check that it is connected and not in use"
        )

    for _ in range(WARMUP_FRAMES):
        success, _ = camera.read()
        if not success:
            camera.release()
            raise RuntimeError(f"camera {index} stopped responding during warmup")
    return camera


def process_jpeg(
    client: OpenAI,
    *,
    sequence: int,
    jpeg: bytes,
    captured_at: datetime,
    image_name: str,
    model: str,
    detail: str,
    observation_log: JsonlLog | None = None,
) -> dict[str, Any]:
    observation = observe_snapshot(
        client, jpeg=jpeg, model=model, detail=detail
    )
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
    client: OpenAI,
    *,
    buffer: LatestSnapshotBuffer,
    observation_log: JsonlLog,
    event_log: JsonlLog,
    model: str,
    detail: str,
    stats: UploadStats,
) -> None:
    """Process snapshots serially until the closed buffer has been drained."""
    while True:
        snapshot = buffer.take()
        if snapshot is None:
            return

        try:
            record = process_jpeg(
                client,
                sequence=snapshot.sequence,
                jpeg=snapshot.jpeg,
                captured_at=snapshot.captured_at,
                image_name=snapshot.image_name,
                model=model,
                detail=detail,
                observation_log=observation_log,
            )
        except Exception as error:
            stats.api_errors += 1
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

        stats.observed += 1
        event_log.append(make_capture_event(snapshot, "observed"))
        print_observation(record)


def run_image_smoke_test(client: OpenAI, args: argparse.Namespace) -> None:
    image_path = args.image
    if not image_path.is_file():
        raise RuntimeError(f"image does not exist: {image_path}")
    if image_path.suffix.lower() not in {".jpg", ".jpeg"}:
        raise RuntimeError("--image currently accepts JPEG files only")

    record = process_jpeg(
        client,
        sequence=1,
        jpeg=image_path.read_bytes(),
        captured_at=datetime.now().astimezone(),
        image_name=image_path.name,
        model=args.model,
        detail=args.detail,
    )
    print(json.dumps(record, indent=2, ensure_ascii=False))


def run_capture_session(client: OpenAI, args: argparse.Namespace) -> None:
    started_at = datetime.now().astimezone()
    session_dir = args.output_dir / f"session-{started_at:%Y%m%d-%H%M%S}"
    session_dir.mkdir(parents=True, exist_ok=False)
    observation_log = JsonlLog(session_dir / "observations.jsonl")
    event_log = JsonlLog(session_dir / "capture_events.jsonl")

    camera = open_camera(args.camera)
    buffer = LatestSnapshotBuffer()
    upload_stats = UploadStats()
    uploader = threading.Thread(
        target=upload_snapshots,
        kwargs={
            "client": client,
            "buffer": buffer,
            "observation_log": observation_log,
            "event_log": event_log,
            "model": args.model,
            "detail": args.detail,
            "stats": upload_stats,
        },
        name="perception-uploader",
    )
    uploader.start()

    deadline = (
        time.monotonic() + args.duration * 60 if args.duration is not None else None
    )
    next_capture = time.monotonic()
    captured = 0
    superseded = 0

    print(
        f"Capturing camera {args.camera} every {args.interval:g}s to {session_dir}. "
        "Press Ctrl+C to stop.",
        flush=True,
    )

    try:
        while deadline is None or time.monotonic() < deadline:
            wait_seconds = next_capture - time.monotonic()
            if wait_seconds > 0:
                if deadline is not None:
                    wait_seconds = min(wait_seconds, deadline - time.monotonic())
                if wait_seconds > 0:
                    time.sleep(wait_seconds)
            if deadline is not None and time.monotonic() >= deadline:
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

            jpeg = encode_jpeg(frame, args.jpeg_quality)
            image_name = f"{captured_at:%Y%m%d-%H%M%S-%f}.jpg"
            (session_dir / image_name).write_bytes(jpeg)
            captured += 1
            snapshot = Snapshot(
                sequence=captured,
                captured_at=captured_at,
                image_name=image_name,
                jpeg=jpeg,
            )
            replaced = buffer.submit(snapshot)
            if replaced is not None:
                superseded += 1
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
        print("\nStopping capture...", flush=True)
    finally:
        camera.release()
        buffer.close()
        if uploader.is_alive():
            print("Finishing the active and newest pending API request...", flush=True)
        uploader.join()

    print(
        f"Session complete: {captured} snapshot(s) captured, "
        f"{upload_stats.observed} observed, {superseded} superseded, "
        f"{upload_stats.api_errors} API error(s). Results: {observation_log.path}",
        flush=True,
    )


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    if not os.environ.get("OPENAI_API_KEY"):
        print("error: OPENAI_API_KEY is not set", file=sys.stderr)
        return 2

    client = OpenAI(timeout=args.api_timeout, max_retries=args.max_retries)
    try:
        if args.image is not None:
            run_image_smoke_test(client, args)
        else:
            run_capture_session(client, args)
    except Exception as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
