import base64
import json
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock

import capture


SAMPLE_OBSERVATION = {
    "user_present": True,
    "user_state": "sitting at desk",
    "objects": ["laptop", "notebook"],
    "activity": "typing on laptop",
    "possible_distractions": [],
    "confidence": 0.91,
}


class CaptureTests(unittest.TestCase):
    def test_jpeg_data_url_round_trips_bytes(self):
        jpeg = b"\xff\xd8test-jpeg\xff\xd9"

        data_url = capture.jpeg_data_url(jpeg)

        prefix, encoded = data_url.split(",", maxsplit=1)
        self.assertEqual(prefix, "data:image/jpeg;base64")
        self.assertEqual(base64.b64decode(encoded), jpeg)

    def test_observe_snapshot_sends_image_and_structured_schema(self):
        client = Mock()
        client.responses.create.return_value = SimpleNamespace(
            output_text=json.dumps(SAMPLE_OBSERVATION)
        )

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

    def test_process_jpeg_appends_reasoning_ready_jsonl_record(self):
        client = Mock()
        client.responses.create.return_value = SimpleNamespace(
            output_text=json.dumps(SAMPLE_OBSERVATION)
        )
        captured_at = datetime(2026, 7, 15, 20, 30, tzinfo=timezone.utc)

        with tempfile.TemporaryDirectory() as directory:
            log_path = Path(directory) / "observations.jsonl"
            record = capture.process_jpeg(
                client,
                jpeg=b"jpeg bytes",
                captured_at=captured_at,
                image_name="snapshot.jpg",
                model="test-model",
                detail="low",
                log_path=log_path,
            )
            stored = json.loads(log_path.read_text(encoding="utf-8"))

        self.assertEqual(stored, record)
        self.assertEqual(stored["image"], "snapshot.jpg")
        self.assertEqual(stored["observation"], SAMPLE_OBSERVATION)
        self.assertEqual(stored["captured_at"], "2026-07-15T20:30:00.000+00:00")

    def test_cli_rejects_non_positive_interval(self):
        with self.assertRaises(SystemExit):
            capture.parse_args(["--interval", "0"])


if __name__ == "__main__":
    unittest.main()
