"""OpenCV implementations of the GoalKeeper camera ports."""

from __future__ import annotations

from typing import Any

import cv2


PREFLIGHT_WINDOW = "GoalKeeper Camera Preflight"
WARMUP_FRAMES = 8


class OpenCVCameraAdapter:
    def __init__(self, capture: cv2.VideoCapture) -> None:
        self._capture = capture

    def read(self) -> tuple[bool, Any]:
        return self._capture.read()

    def release(self) -> None:
        self._capture.release()


class OpenCVPreviewAdapter:
    def open(self) -> None:
        cv2.namedWindow(PREFLIGHT_WINDOW, cv2.WINDOW_NORMAL)

    def draw_status(
        self,
        frame: Any,
        message: str,
        *,
        color: tuple[int, int, int] = (255, 255, 255),
    ) -> None:
        preview = frame.copy()
        cv2.rectangle(preview, (0, 0), (preview.shape[1], 52), (0, 0, 0), -1)
        cv2.putText(
            preview,
            message,
            (12, 34),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.65,
            color,
            2,
            cv2.LINE_AA,
        )
        cv2.imshow(PREFLIGHT_WINDOW, preview)

    def wait_key(self, milliseconds: int) -> int:
        return cv2.waitKey(milliseconds) & 0xFF

    def encode_jpeg(self, frame: Any, quality: int) -> bytes:
        success, encoded = cv2.imencode(
            ".jpg", frame, [int(cv2.IMWRITE_JPEG_QUALITY), quality]
        )
        if not success:
            raise RuntimeError("OpenCV could not encode the captured frame as JPEG")
        return encoded.tobytes()

    def close(self) -> None:
        try:
            cv2.destroyWindow(PREFLIGHT_WINDOW)
        except cv2.error:
            pass


def open_camera(index: int) -> OpenCVCameraAdapter:
    capture = cv2.VideoCapture(index)
    if not capture.isOpened():
        capture.release()
        raise RuntimeError(
            f"could not open camera {index}; close other camera apps or choose --camera"
        )
    for _ in range(WARMUP_FRAMES):
        success, _frame = capture.read()
        if not success:
            capture.release()
            raise RuntimeError(f"camera {index} stopped responding during warmup")
    return OpenCVCameraAdapter(capture)
