import base64
import io
import json
import tempfile
import threading
import time
import unittest
from contextlib import redirect_stderr, redirect_stdout
from datetime import datetime, timedelta, timezone
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

import capture


SAMPLE_OBSERVATION = {
    "image_quality": {"value": "adequate", "limitations": []},
    "people_count": 1,
    "objects": ["laptop", "notebook"],
    "observations": [
        {
            "subject": "visible_person",
            "behavior": "typing_on_laptop",
            "support": "direct",
            "visual_basis": "Hands are positioned over the laptop keyboard.",
            "limitations": [],
        }
    ],
}


def response_with_observation():
    return SimpleNamespace(output_text=json.dumps(SAMPLE_OBSERVATION))


def snapshot(sequence: int) -> capture.Snapshot:
    return capture.Snapshot(
        sequence=sequence,
        captured_at=datetime(2026, 7, 15, 20, 30, tzinfo=timezone.utc)
        + timedelta(seconds=sequence * 10),
        image_name=f"snapshot-{sequence}.jpg",
        jpeg=f"jpeg-{sequence}".encode(),
    )


def confirmed_preflight() -> capture.PreflightCapture:
    captured_at = datetime(2026, 7, 15, 20, 29, tzinfo=timezone.utc)
    return capture.PreflightCapture(
        captured_at=captured_at,
        confirmed_at=captured_at + timedelta(seconds=2),
        jpeg=b"preflight jpeg",
        observation=SAMPLE_OBSERVATION,
    )


def read_jsonl(path: Path):
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]


class FakeCamera:
    def __init__(self):
        self.reads = 0
        self.released = False

    def read(self):
        self.reads += 1
        return True, object()

    def release(self):
        self.released = True


class CaptureTests(unittest.TestCase):
    def test_jpeg_data_url_round_trips_bytes(self):
        jpeg = b"\xff\xd8test-jpeg\xff\xd9"

        data_url = capture.jpeg_data_url(jpeg)

        prefix, encoded = data_url.split(",", maxsplit=1)
        self.assertEqual(prefix, "data:image/jpeg;base64")
        self.assertEqual(base64.b64decode(encoded), jpeg)

    def test_observe_snapshot_sends_image_and_structured_schema(self):
        client = Mock()
        client.responses.create.return_value = response_with_observation()

        result = capture.observe_snapshot(
            client,
            jpeg=b"jpeg bytes",
            model="test-model",
            detail="low",
        )

        self.assertEqual(result, SAMPLE_OBSERVATION)
        request = client.responses.create.call_args.kwargs
        self.assertEqual(request["model"], "test-model")
        image = request["input"][1]["content"][1]
        self.assertEqual(image["type"], "input_image")
        self.assertEqual(image["detail"], "low")
        self.assertTrue(image["image_url"].startswith("data:image/jpeg;base64,"))
        self.assertEqual(request["text"]["format"]["type"], "json_schema")
        self.assertTrue(request["text"]["format"]["strict"])
        schema = request["text"]["format"]["schema"]
        self.assertEqual(
            set(schema["required"]),
            {"image_quality", "people_count", "objects", "observations"},
        )
        self.assertNotIn("possible_distractions", schema["properties"])
        self.assertNotIn("confidence", schema["properties"])

    def test_process_jpeg_appends_sequence_aware_observation(self):
        client = Mock()
        client.responses.create.return_value = response_with_observation()
        captured_at = datetime(2026, 7, 15, 20, 30, tzinfo=timezone.utc)

        with tempfile.TemporaryDirectory() as directory:
            observation_log = capture.JsonlLog(Path(directory) / "observations.jsonl")
            record = capture.process_jpeg(
                client,
                sequence=7,
                jpeg=b"jpeg bytes",
                captured_at=captured_at,
                image_name="snapshot.jpg",
                model="test-model",
                detail="low",
                observation_log=observation_log,
            )
            stored = read_jsonl(observation_log.path)[0]

        self.assertEqual(stored, record)
        self.assertEqual(stored["sequence"], 7)
        self.assertEqual(stored["image"], "snapshot.jpg")
        self.assertEqual(stored["observation"], SAMPLE_OBSERVATION)
        self.assertEqual(stored["captured_at"], "2026-07-15T20:30:00.000+00:00")

    def test_jsonl_log_exists_before_first_record(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "observations.jsonl"
            capture.JsonlLog(path)

            self.assertTrue(path.is_file())
            self.assertEqual(path.read_text(encoding="utf-8"), "")

    def test_preflight_accepts_only_adequate_single_person_scene(self):
        self.assertIsNone(capture.preflight_validation_error(SAMPLE_OBSERVATION))

        cases = [
            (
                {
                    **SAMPLE_OBSERVATION,
                    "image_quality": {
                        "value": "limited",
                        "limitations": ["room is too dark"],
                    },
                },
                "image quality is limited",
            ),
            ({**SAMPLE_OBSERVATION, "people_count": None}, "indeterminate"),
            ({**SAMPLE_OBSERVATION, "people_count": 0}, "no person"),
            ({**SAMPLE_OBSERVATION, "people_count": 2}, "exactly one"),
        ]
        for observation, expected in cases:
            with self.subTest(expected=expected):
                self.assertIn(
                    expected,
                    capture.preflight_validation_error(observation),
                )

    def test_preview_space_captures_and_escape_cancels(self):
        camera = FakeCamera()
        with (
            patch("capture.draw_preflight_status"),
            patch("capture.encode_jpeg", return_value=b"setup jpeg"),
            patch("capture.cv2.waitKey", return_value=32),
            redirect_stdout(io.StringIO()),
        ):
            setup = capture.capture_setup_frame(camera, 85)

        self.assertEqual(setup[1], b"setup jpeg")

        with (
            patch("capture.draw_preflight_status"),
            patch("capture.cv2.waitKey", return_value=27),
            redirect_stdout(io.StringIO()),
        ):
            self.assertIsNone(capture.capture_setup_frame(camera, 85))

    def test_preflight_retries_rejected_frame_then_confirms_valid_frame(self):
        client = Mock()
        camera = FakeCamera()
        rejected = {**SAMPLE_OBSERVATION, "people_count": 2}
        setup = (
            datetime(2026, 7, 15, 20, 29, tzinfo=timezone.utc),
            b"setup jpeg",
            object(),
        )
        args = SimpleNamespace(jpeg_quality=85, model="test-model", detail="low")

        with (
            patch("capture.cv2.namedWindow"),
            patch("capture.cv2.destroyWindow"),
            patch("capture.cv2.waitKey", return_value=1),
            patch("capture.draw_preflight_status"),
            patch("capture.capture_setup_frame", side_effect=[setup, setup]),
            patch(
                "capture.observe_snapshot",
                side_effect=[rejected, SAMPLE_OBSERVATION],
            ),
            redirect_stdout(io.StringIO()),
            redirect_stderr(io.StringIO()),
        ):
            result = capture.run_camera_preflight(
                client,
                camera,
                args,
                input_func=lambda _prompt: "y",
            )

        self.assertIsNotNone(result)
        self.assertEqual(result.jpeg, b"setup jpeg")
        self.assertEqual(result.observation, SAMPLE_OBSERVATION)

    def test_user_can_retry_an_accepted_preflight_frame(self):
        client = Mock()
        camera = FakeCamera()
        setup = (
            datetime(2026, 7, 15, 20, 29, tzinfo=timezone.utc),
            b"setup jpeg",
            object(),
        )
        choices = iter(["r", "y"])
        args = SimpleNamespace(jpeg_quality=85, model="test-model", detail="low")

        with (
            patch("capture.cv2.namedWindow"),
            patch("capture.cv2.destroyWindow"),
            patch("capture.cv2.waitKey", return_value=1),
            patch("capture.draw_preflight_status"),
            patch("capture.capture_setup_frame", side_effect=[setup, setup]),
            patch("capture.observe_snapshot", return_value=SAMPLE_OBSERVATION),
            redirect_stdout(io.StringIO()),
        ):
            result = capture.run_camera_preflight(
                client,
                camera,
                args,
                input_func=lambda _prompt: next(choices),
            )

        self.assertIsNotNone(result)

    def test_confirmed_preflight_artifacts_are_separate_from_observations(self):
        with tempfile.TemporaryDirectory() as directory:
            session_dir = Path(directory)
            capture.write_preflight_artifacts(session_dir, confirmed_preflight())
            observations = capture.JsonlLog(session_dir / "observations.jsonl")

            preflight_record = json.loads(
                (session_dir / "preflight.json").read_text(encoding="utf-8")
            )

            self.assertEqual(
                (session_dir / "preflight.jpg").read_bytes(), b"preflight jpeg"
            )
            self.assertEqual(preflight_record["observation"], SAMPLE_OBSERVATION)
            self.assertEqual(observations.path.read_text(encoding="utf-8"), "")

    def test_latest_buffer_replaces_only_pending_snapshot(self):
        buffer = capture.LatestSnapshotBuffer()
        first = snapshot(1)
        second = snapshot(2)

        self.assertIsNone(buffer.submit(first))
        self.assertEqual(buffer.submit(second), first)
        self.assertEqual(buffer.take(), second)

        buffer.close()
        self.assertIsNone(buffer.take())

    def test_closed_buffer_drains_newest_pending_snapshot(self):
        buffer = capture.LatestSnapshotBuffer()
        latest = snapshot(3)
        buffer.submit(latest)

        buffer.close()

        self.assertEqual(buffer.take(), latest)
        self.assertIsNone(buffer.take())

    def test_worker_finishes_in_flight_and_newest_pending_snapshot(self):
        first_started = threading.Event()
        release_first = threading.Event()
        call_count = 0

        def create_response(**_kwargs):
            nonlocal call_count
            call_count += 1
            if call_count == 1:
                first_started.set()
                self.assertTrue(release_first.wait(timeout=2))
            return response_with_observation()

        client = Mock()
        client.responses.create.side_effect = create_response
        buffer = capture.LatestSnapshotBuffer()
        stats = capture.UploadStats()

        with tempfile.TemporaryDirectory() as directory:
            observation_log = capture.JsonlLog(Path(directory) / "observations.jsonl")
            event_log = capture.JsonlLog(Path(directory) / "capture_events.jsonl")
            buffer.submit(snapshot(1))
            worker = threading.Thread(
                target=capture.upload_snapshots,
                kwargs={
                    "client": client,
                    "buffer": buffer,
                    "observation_log": observation_log,
                    "event_log": event_log,
                    "model": "test-model",
                    "detail": "low",
                    "stats": stats,
                },
            )
            with redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
                worker.start()
                self.assertTrue(first_started.wait(timeout=2))
                buffer.submit(snapshot(2))
                replaced = buffer.submit(snapshot(3))
                event_log.append(capture.make_capture_event(replaced, "superseded"))
                buffer.close()
                release_first.set()
                worker.join(timeout=2)

            observations = read_jsonl(observation_log.path)
            events = read_jsonl(event_log.path)

        self.assertFalse(worker.is_alive())
        self.assertEqual([record["sequence"] for record in observations], [1, 3])
        self.assertEqual(
            [(event["sequence"], event["status"]) for event in events],
            [(2, "superseded"), (1, "observed"), (3, "observed")],
        )
        self.assertEqual(stats.observed, 2)

    def test_worker_logs_api_failure_and_continues_with_newest(self):
        first_started = threading.Event()
        release_first = threading.Event()
        call_count = 0

        def create_response(**_kwargs):
            nonlocal call_count
            call_count += 1
            if call_count == 1:
                first_started.set()
                self.assertTrue(release_first.wait(timeout=2))
                raise RuntimeError("temporary failure")
            return response_with_observation()

        client = Mock()
        client.responses.create.side_effect = create_response
        buffer = capture.LatestSnapshotBuffer()
        stats = capture.UploadStats()

        with tempfile.TemporaryDirectory() as directory:
            observation_log = capture.JsonlLog(Path(directory) / "observations.jsonl")
            event_log = capture.JsonlLog(Path(directory) / "capture_events.jsonl")
            buffer.submit(snapshot(1))
            worker = threading.Thread(
                target=capture.upload_snapshots,
                kwargs={
                    "client": client,
                    "buffer": buffer,
                    "observation_log": observation_log,
                    "event_log": event_log,
                    "model": "test-model",
                    "detail": "low",
                    "stats": stats,
                },
            )
            with redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
                worker.start()
                self.assertTrue(first_started.wait(timeout=2))
                buffer.submit(snapshot(2))
                buffer.close()
                release_first.set()
                worker.join(timeout=2)

            observations = read_jsonl(observation_log.path)
            events = read_jsonl(event_log.path)

        self.assertEqual([record["sequence"] for record in observations], [2])
        self.assertEqual(
            [(event["sequence"], event["status"]) for event in events],
            [(1, "api_error"), (2, "observed")],
        )
        self.assertEqual(events[0]["error"], "temporary failure")
        self.assertEqual(stats.api_errors, 1)
        self.assertEqual(stats.observed, 1)

    def test_fixed_schedule_skips_missed_ticks_without_bursting(self):
        self.assertEqual(capture.advance_capture_tick(0, 10, 5), 10)
        self.assertEqual(capture.advance_capture_tick(0, 10, 25), 30)
        self.assertEqual(capture.advance_capture_tick(20, 10, 30), 30)

    def test_slow_api_does_not_block_capture_and_shutdown_releases_camera(self):
        camera = FakeCamera()
        client = Mock()

        def slow_response(**_kwargs):
            time.sleep(0.04)
            return response_with_observation()

        client.responses.create.side_effect = slow_response

        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(
                output_dir=Path(directory),
                camera=0,
                duration=0.0012,
                interval=0.01,
                jpeg_quality=85,
                model="test-model",
                detail="low",
            )
            with (
                patch("capture.open_camera", return_value=camera),
                patch(
                    "capture.run_camera_preflight",
                    return_value=confirmed_preflight(),
                ),
                patch("capture.encode_jpeg", return_value=b"jpeg bytes"),
                redirect_stdout(io.StringIO()),
                redirect_stderr(io.StringIO()),
            ):
                capture.run_capture_session(client, args)

            session_dir = next(Path(directory).glob("session-*"))
            images = [
                path for path in session_dir.glob("*.jpg")
                if path.name != "preflight.jpg"
            ]
            observations = read_jsonl(session_dir / "observations.jsonl")
            events = read_jsonl(session_dir / "capture_events.jsonl")

        self.assertTrue(camera.released)
        self.assertGreaterEqual(len(images), 4)
        self.assertLess(client.responses.create.call_count, len(images))
        self.assertEqual(
            len(observations),
            sum(event["status"] == "observed" for event in events),
        )
        self.assertGreater(
            sum(event["status"] == "superseded" for event in events), 0
        )

    def test_canceling_preflight_releases_camera_without_session_directory(self):
        camera = FakeCamera()
        client = Mock()
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(output_dir=Path(directory), camera=0)
            with (
                patch("capture.open_camera", return_value=camera),
                patch("capture.run_camera_preflight", return_value=None),
                redirect_stdout(io.StringIO()),
            ):
                started = capture.run_capture_session(client, args)

            self.assertFalse(started)
            self.assertTrue(camera.released)
            self.assertEqual(list(Path(directory).glob("session-*")), [])

    def test_preflight_error_releases_camera_without_session_directory(self):
        camera = FakeCamera()
        client = Mock()
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(output_dir=Path(directory), camera=0)
            with (
                patch("capture.open_camera", return_value=camera),
                patch(
                    "capture.run_camera_preflight",
                    side_effect=RuntimeError("preview failed"),
                ),
            ):
                with self.assertRaisesRegex(RuntimeError, "preview failed"):
                    capture.run_capture_session(client, args)

            self.assertTrue(camera.released)
            self.assertEqual(list(Path(directory).glob("session-*")), [])

    def test_cli_rejects_non_positive_interval(self):
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            capture.parse_args(["--interval", "0"])


if __name__ == "__main__":
    unittest.main()
