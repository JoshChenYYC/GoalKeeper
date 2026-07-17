"""Safe ownership and deletion of Focus Session filesystem artifacts."""

from __future__ import annotations

import json
import re
import shutil
from dataclasses import dataclass
from pathlib import Path


class ArtifactOwnershipError(RuntimeError):
    """Raised when a directory cannot be proven to belong to a session."""


@dataclass(frozen=True, slots=True)
class ArtifactClaim:
    session_id: str
    path: Path
    directory_created: bool
    marker_created: bool


class FilesystemSessionArtifacts:
    """Own session directories using an identifier marker before deleting them."""

    MARKER_NAME = ".goalkeeper-session.json"
    _CAPTURE_DIRECTORY = re.compile(r"session-\d{8}-\d{6}")

    def claim(self, session_id: str, directory: Path | str) -> ArtifactClaim:
        path = Path(directory).expanduser().resolve()
        directory_created = not path.exists()
        path.mkdir(parents=True, exist_ok=True)
        if not path.is_dir():
            raise ArtifactOwnershipError(f"session artifact path is not a directory: {path}")

        marker = path / self.MARKER_NAME
        marker_created = False
        has_entries = any(path.iterdir())
        is_capture_directory = (
            self._CAPTURE_DIRECTORY.fullmatch(path.name) is not None
            and (path / "preflight.jpg").is_file()
            and (path / "preflight.json").is_file()
        )
        if (
            not directory_created
            and not marker.exists()
            and has_entries
            and not is_capture_directory
        ):
            raise ArtifactOwnershipError(
                f"refusing to claim a nonempty unowned artifact directory: {path}"
            )
        try:
            with marker.open("x", encoding="utf-8") as output:
                output.write(
                    json.dumps({"session_id": session_id}, separators=(",", ":"))
                )
            marker_created = True
        except FileExistsError:
            owner = self._read_owner(marker)
            if owner != session_id:
                raise ArtifactOwnershipError(
                    f"session artifact directory is already owned by {owner}: {path}"
                )
        except Exception:
            if directory_created:
                try:
                    path.rmdir()
                except OSError:
                    pass
            raise
        return ArtifactClaim(session_id, path, directory_created, marker_created)

    def rollback_claim(self, claim: ArtifactClaim) -> None:
        marker = claim.path / self.MARKER_NAME
        if (
            claim.marker_created
            and marker.is_file()
            and self._read_owner(marker) == claim.session_id
        ):
            marker.unlink()
        if claim.directory_created:
            try:
                claim.path.rmdir()
            except OSError:
                pass

    def validate(self, session_id: str, directory: Path | str | None) -> Path | None:
        if directory is None:
            return None
        path = Path(directory).expanduser().resolve()
        if not path.exists():
            return None
        if not path.is_dir():
            raise ArtifactOwnershipError(f"session artifact path is not a directory: {path}")
        marker = path / self.MARKER_NAME
        if not marker.is_file() or self._read_owner(marker) != session_id:
            raise ArtifactOwnershipError(
                f"refusing to delete an unowned session artifact directory: {path}"
            )
        return path

    def delete(self, session_id: str, directory: Path | str | None) -> None:
        path = self.validate(session_id, directory)
        if path is not None:
            shutil.rmtree(path)

    @staticmethod
    def _read_owner(marker: Path) -> str | None:
        try:
            value = json.loads(marker.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return None
        owner = value.get("session_id") if isinstance(value, dict) else None
        return owner if isinstance(owner, str) and owner else None
