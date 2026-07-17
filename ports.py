"""Provider-neutral application ports for capture and perception."""

from __future__ import annotations

from typing import Any, Mapping, Protocol


class CameraPort(Protocol):
    def read(self) -> tuple[bool, Any]: ...

    def release(self) -> None: ...


class CameraPreviewPort(Protocol):
    def open(self) -> None: ...

    def draw_status(
        self,
        frame: Any,
        message: str,
        *,
        color: tuple[int, int, int] = (255, 255, 255),
    ) -> None: ...

    def wait_key(self, milliseconds: int) -> int: ...

    def encode_jpeg(self, frame: Any, quality: int) -> bytes: ...

    def close(self) -> None: ...


class PerceptionPort(Protocol):
    def observe(self, jpeg: bytes) -> Mapping[str, Any]:
        """Return a neutral schema-constrained observation for one JPEG."""
