"""Thread-safe SQLite persistence for authoritative GoalKeeper state."""

from __future__ import annotations

import json
import sqlite3
import threading
from dataclasses import replace
from datetime import datetime
from pathlib import Path
from typing import Any

from domain import (
    Deviation,
    DeviationProfile,
    FocusSession,
    Goal,
    GoalStatus,
    ObservationRecord,
    Observability,
    ReasoningMode,
    ScheduledBreak,
    Sensitivity,
    SessionContract,
    SessionSnapshot,
    SessionState,
    SnapshotStatus,
)


class StorageError(RuntimeError):
    pass


class NotFoundError(StorageError):
    pass


class ActiveSessionError(StorageError):
    pass


class ConcurrentUpdateError(StorageError):
    pass


ACTIVE_STATES = tuple(state.value for state in SessionState if state.is_active)


def _iso(value: datetime | None) -> str | None:
    return value.isoformat(timespec="milliseconds") if value is not None else None


def _datetime(value: str | None) -> datetime | None:
    return datetime.fromisoformat(value) if value is not None else None


def _deviation_to_dict(value: Deviation) -> dict[str, Any]:
    return {
        "id": value.id,
        "description": value.description,
        "observability": value.observability.value,
    }


def _deviation_from_dict(value: dict[str, Any]) -> Deviation:
    return Deviation(
        id=value["id"],
        description=value["description"],
        observability=Observability(value["observability"]),
    )


def _contract_to_dict(value: SessionContract) -> dict[str, Any]:
    return {
        "id": value.id,
        "goal_id": value.goal_id,
        "goal_title": value.goal_title,
        "goal_description": value.goal_description,
        "target_focus_seconds": value.target_focus_seconds,
        "scheduled_breaks": [
            {
                "id": item.id,
                "focus_offset_seconds": item.focus_offset_seconds,
                "duration_seconds": item.duration_seconds,
            }
            for item in value.scheduled_breaks
        ],
        "deviation_profile_id": value.deviation_profile_id,
        "deviation_snapshot": [
            _deviation_to_dict(item) for item in value.deviation_snapshot
        ],
        "reasoning_mode": value.reasoning_mode.value,
        "sensitivity": value.sensitivity.value,
        "confirmed_at": _iso(value.confirmed_at),
    }


def _contract_from_dict(value: dict[str, Any]) -> SessionContract:
    return SessionContract(
        id=value["id"],
        goal_id=value["goal_id"],
        goal_title=value["goal_title"],
        goal_description=value.get("goal_description"),
        target_focus_seconds=value["target_focus_seconds"],
        scheduled_breaks=tuple(
            ScheduledBreak(
                id=item["id"],
                focus_offset_seconds=item["focus_offset_seconds"],
                duration_seconds=item["duration_seconds"],
            )
            for item in value["scheduled_breaks"]
        ),
        deviation_profile_id=value.get("deviation_profile_id"),
        deviation_snapshot=tuple(
            _deviation_from_dict(item) for item in value["deviation_snapshot"]
        ),
        reasoning_mode=ReasoningMode(value["reasoning_mode"]),
        sensitivity=Sensitivity(value["sensitivity"]),
        confirmed_at=datetime.fromisoformat(value["confirmed_at"]),
    )


class SQLiteRepository:
    """One locked SQLite connection suitable for controller and worker threads."""

    SCHEMA_VERSION = 1

    def __init__(self, path: Path | str) -> None:
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._lock = threading.RLock()
        self._connection = sqlite3.connect(
            self.path,
            check_same_thread=False,
            isolation_level=None,
        )
        self._connection.row_factory = sqlite3.Row
        with self._lock:
            self._connection.execute("PRAGMA foreign_keys = ON")
            self._connection.execute("PRAGMA busy_timeout = 5000")
            self._connection.execute("PRAGMA journal_mode = WAL")
            self._create_schema()

    def close(self) -> None:
        with self._lock:
            self._connection.close()

    def _create_schema(self) -> None:
        active_sql = ",".join(f"'{state}'" for state in ACTIVE_STATES)
        self._connection.executescript(
            f"""
            BEGIN IMMEDIATE;
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER NOT NULL
            );
            INSERT INTO schema_version(version)
            SELECT {self.SCHEMA_VERSION}
            WHERE NOT EXISTS (SELECT 1 FROM schema_version);

            CREATE TABLE IF NOT EXISTS goals (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                description TEXT,
                status TEXT NOT NULL CHECK(status IN ('active', 'completed')),
                created_at TEXT NOT NULL,
                completed_at TEXT
            );

            CREATE TABLE IF NOT EXISTS deviation_profiles (
                id TEXT PRIMARY KEY,
                profile_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS session_contracts (
                id TEXT PRIMARY KEY,
                goal_id TEXT NOT NULL REFERENCES goals(id) ON DELETE CASCADE,
                contract_json TEXT NOT NULL,
                confirmed_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS focus_sessions (
                id TEXT PRIMARY KEY,
                goal_id TEXT NOT NULL REFERENCES goals(id) ON DELETE CASCADE,
                contract_id TEXT NOT NULL UNIQUE
                    REFERENCES session_contracts(id) ON DELETE CASCADE,
                state TEXT NOT NULL,
                version INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                started_at TEXT NOT NULL,
                accumulated_focus_seconds REAL NOT NULL,
                current_break_index INTEGER,
                current_break_elapsed_seconds REAL NOT NULL,
                ended_at TEXT,
                end_reason TEXT,
                session_dir TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS one_active_focus_session
            ON focus_sessions((1)) WHERE state IN ({active_sql});

            CREATE TABLE IF NOT EXISTS session_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL REFERENCES focus_sessions(id) ON DELETE CASCADE,
                session_version INTEGER NOT NULL,
                occurred_at TEXT NOT NULL,
                event TEXT NOT NULL,
                from_state TEXT,
                to_state TEXT,
                payload_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS snapshots (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL REFERENCES focus_sessions(id) ON DELETE CASCADE,
                sequence INTEGER NOT NULL,
                captured_at TEXT NOT NULL,
                image TEXT NOT NULL,
                session_version INTEGER NOT NULL,
                captured_state TEXT NOT NULL,
                reasoning_eligible INTEGER NOT NULL,
                status TEXT NOT NULL,
                error TEXT,
                finalized_at TEXT,
                UNIQUE(session_id, sequence)
            );

            CREATE TABLE IF NOT EXISTS observations (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL REFERENCES focus_sessions(id) ON DELETE CASCADE,
                snapshot_id TEXT NOT NULL UNIQUE REFERENCES snapshots(id) ON DELETE CASCADE,
                sequence INTEGER NOT NULL,
                captured_at TEXT NOT NULL,
                processed_at TEXT NOT NULL,
                image TEXT NOT NULL,
                session_version INTEGER NOT NULL,
                captured_state TEXT NOT NULL,
                reasoning_eligible INTEGER NOT NULL,
                observation_json TEXT NOT NULL,
                UNIQUE(session_id, sequence)
            );

            CREATE TRIGGER IF NOT EXISTS prevent_active_goal_update
            BEFORE UPDATE ON goals
            WHEN EXISTS (
                SELECT 1 FROM focus_sessions
                WHERE goal_id = OLD.id AND state IN ({active_sql})
            )
            BEGIN
                SELECT RAISE(ABORT, 'goal has an active focus session');
            END;

            CREATE TRIGGER IF NOT EXISTS prevent_active_goal_delete
            BEFORE DELETE ON goals
            WHEN EXISTS (
                SELECT 1 FROM focus_sessions
                WHERE goal_id = OLD.id AND state IN ({active_sql})
            )
            BEGIN
                SELECT RAISE(ABORT, 'goal has an active focus session');
            END;
            COMMIT;
            """
        )
        version = self._connection.execute(
            "SELECT version FROM schema_version"
        ).fetchone()["version"]
        if version != self.SCHEMA_VERSION:
            raise StorageError(
                f"unsupported database schema {version}; expected {self.SCHEMA_VERSION}"
            )

    def create_goal(self, goal: Goal) -> Goal:
        with self._lock, self._connection:
            self._connection.execute(
                """INSERT INTO goals
                   (id, title, description, status, created_at, completed_at)
                   VALUES (?, ?, ?, ?, ?, ?)""",
                (
                    goal.id,
                    goal.title,
                    goal.description,
                    goal.status.value,
                    _iso(goal.created_at),
                    _iso(goal.completed_at),
                ),
            )
        return goal

    def get_goal(self, goal_id: str) -> Goal:
        with self._lock:
            row = self._connection.execute(
                "SELECT * FROM goals WHERE id = ?", (goal_id,)
            ).fetchone()
        if row is None:
            raise NotFoundError(f"goal does not exist: {goal_id}")
        return Goal(
            id=row["id"],
            title=row["title"],
            description=row["description"],
            status=GoalStatus(row["status"]),
            created_at=_datetime(row["created_at"]),
            completed_at=_datetime(row["completed_at"]),
        )

    def update_goal(self, goal: Goal) -> Goal:
        try:
            with self._lock, self._connection:
                cursor = self._connection.execute(
                    """UPDATE goals SET title = ?, description = ?, status = ?,
                       completed_at = ? WHERE id = ?""",
                    (
                        goal.title,
                        goal.description,
                        goal.status.value,
                        _iso(goal.completed_at),
                        goal.id,
                    ),
                )
                if cursor.rowcount != 1:
                    raise NotFoundError(f"goal does not exist: {goal.id}")
        except sqlite3.IntegrityError as error:
            if "active focus session" in str(error):
                raise ActiveSessionError(str(error)) from error
            raise
        return goal

    def delete_goal(self, goal_id: str) -> None:
        try:
            with self._lock, self._connection:
                cursor = self._connection.execute(
                    "DELETE FROM goals WHERE id = ?", (goal_id,)
                )
                if cursor.rowcount != 1:
                    raise NotFoundError(f"goal does not exist: {goal_id}")
        except sqlite3.IntegrityError as error:
            if "active focus session" in str(error):
                raise ActiveSessionError(str(error)) from error
            raise

    def save_deviation_profile(self, profile: DeviationProfile) -> DeviationProfile:
        payload = json.dumps(
            {
                "id": profile.id,
                "created_at": _iso(profile.created_at),
                "updated_at": _iso(profile.updated_at),
                "deviations": [
                    _deviation_to_dict(item) for item in profile.deviations
                ],
            },
            separators=(",", ":"),
        )
        with self._lock, self._connection:
            self._connection.execute(
                """INSERT INTO deviation_profiles
                   (id, profile_json, created_at, updated_at) VALUES (?, ?, ?, ?)
                   ON CONFLICT(id) DO UPDATE SET
                   profile_json = excluded.profile_json,
                   updated_at = excluded.updated_at""",
                (profile.id, payload, _iso(profile.created_at), _iso(profile.updated_at)),
            )
        return profile

    def get_deviation_profile(self, profile_id: str) -> DeviationProfile:
        with self._lock:
            row = self._connection.execute(
                "SELECT profile_json FROM deviation_profiles WHERE id = ?", (profile_id,)
            ).fetchone()
        if row is None:
            raise NotFoundError(f"deviation profile does not exist: {profile_id}")
        payload = json.loads(row["profile_json"])
        return DeviationProfile(
            id=payload["id"],
            created_at=datetime.fromisoformat(payload["created_at"]),
            updated_at=datetime.fromisoformat(payload["updated_at"]),
            deviations=tuple(
                _deviation_from_dict(item) for item in payload["deviations"]
            ),
        )

    def create_session(self, session: FocusSession) -> FocusSession:
        contract_json = json.dumps(_contract_to_dict(session.contract), separators=(",", ":"))
        try:
            with self._lock, self._connection:
                self._connection.execute(
                    """INSERT INTO session_contracts
                       (id, goal_id, contract_json, confirmed_at) VALUES (?, ?, ?, ?)""",
                    (
                        session.contract.id,
                        session.goal_id,
                        contract_json,
                        _iso(session.contract.confirmed_at),
                    ),
                )
                self._connection.execute(
                    """INSERT INTO focus_sessions
                       (id, goal_id, contract_id, state, version, created_at, started_at,
                        accumulated_focus_seconds, current_break_index,
                        current_break_elapsed_seconds, ended_at, end_reason, session_dir)
                       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                    (
                        session.id,
                        session.goal_id,
                        session.contract.id,
                        session.state.value,
                        session.version,
                        _iso(session.created_at),
                        _iso(session.started_at),
                        session.accumulated_focus_seconds,
                        session.current_break_index,
                        session.current_break_elapsed_seconds,
                        _iso(session.ended_at),
                        session.end_reason,
                        session.session_dir,
                    ),
                )
                self._insert_event(
                    session.id,
                    session.version,
                    session.started_at,
                    "session_started",
                    None,
                    session.state,
                    {},
                )
        except sqlite3.IntegrityError as error:
            if "one_active_focus_session" in str(error):
                raise ActiveSessionError("another focus session is already active") from error
            raise
        return session

    def _session_from_row(self, row: sqlite3.Row) -> FocusSession:
        return FocusSession(
            id=row["id"],
            goal_id=row["goal_id"],
            contract=_contract_from_dict(json.loads(row["contract_json"])),
            state=SessionState(row["state"]),
            version=row["version"],
            created_at=_datetime(row["created_at"]),
            started_at=_datetime(row["started_at"]),
            accumulated_focus_seconds=row["accumulated_focus_seconds"],
            current_break_index=row["current_break_index"],
            current_break_elapsed_seconds=row["current_break_elapsed_seconds"],
            ended_at=_datetime(row["ended_at"]),
            end_reason=row["end_reason"],
            session_dir=row["session_dir"],
        )

    def get_session(self, session_id: str) -> FocusSession:
        with self._lock:
            row = self._connection.execute(
                """SELECT focus_sessions.*, session_contracts.contract_json
                   FROM focus_sessions JOIN session_contracts
                   ON session_contracts.id = focus_sessions.contract_id
                   WHERE focus_sessions.id = ?""",
                (session_id,),
            ).fetchone()
        if row is None:
            raise NotFoundError(f"focus session does not exist: {session_id}")
        return self._session_from_row(row)

    def list_active_sessions(self) -> list[FocusSession]:
        placeholders = ",".join("?" for _ in ACTIVE_STATES)
        with self._lock:
            rows = self._connection.execute(
                f"""SELECT focus_sessions.*, session_contracts.contract_json
                    FROM focus_sessions JOIN session_contracts
                    ON session_contracts.id = focus_sessions.contract_id
                    WHERE focus_sessions.state IN ({placeholders})""",
                ACTIVE_STATES,
            ).fetchall()
        return [self._session_from_row(row) for row in rows]

    def save_session_progress(self, session: FocusSession) -> None:
        with self._lock, self._connection:
            cursor = self._connection.execute(
                """UPDATE focus_sessions SET accumulated_focus_seconds = ?,
                   current_break_index = ?, current_break_elapsed_seconds = ?
                   WHERE id = ? AND version = ? AND state = ?""",
                (
                    session.accumulated_focus_seconds,
                    session.current_break_index,
                    session.current_break_elapsed_seconds,
                    session.id,
                    session.version,
                    session.state.value,
                ),
            )
            if cursor.rowcount != 1:
                raise ConcurrentUpdateError(f"focus session changed: {session.id}")

    def transition_session(
        self,
        previous: FocusSession,
        updated: FocusSession,
        *,
        occurred_at: datetime,
        event: str,
        payload: dict[str, Any] | None = None,
    ) -> FocusSession:
        if updated.version != previous.version + 1:
            raise ValueError("state transitions must increment the session version once")
        with self._lock, self._connection:
            cursor = self._connection.execute(
                """UPDATE focus_sessions SET state = ?, version = ?,
                   accumulated_focus_seconds = ?, current_break_index = ?,
                   current_break_elapsed_seconds = ?, ended_at = ?, end_reason = ?
                   WHERE id = ? AND version = ? AND state = ?""",
                (
                    updated.state.value,
                    updated.version,
                    updated.accumulated_focus_seconds,
                    updated.current_break_index,
                    updated.current_break_elapsed_seconds,
                    _iso(updated.ended_at),
                    updated.end_reason,
                    updated.id,
                    previous.version,
                    previous.state.value,
                ),
            )
            if cursor.rowcount != 1:
                raise ConcurrentUpdateError(f"focus session changed: {updated.id}")
            self._insert_event(
                updated.id,
                updated.version,
                occurred_at,
                event,
                previous.state,
                updated.state,
                payload or {},
            )
        return updated

    def _insert_event(
        self,
        session_id: str,
        version: int,
        occurred_at: datetime,
        event: str,
        from_state: SessionState | None,
        to_state: SessionState | None,
        payload: dict[str, Any],
    ) -> None:
        self._connection.execute(
            """INSERT INTO session_events
               (session_id, session_version, occurred_at, event, from_state,
                to_state, payload_json) VALUES (?, ?, ?, ?, ?, ?, ?)""",
            (
                session_id,
                version,
                _iso(occurred_at),
                event,
                from_state.value if from_state else None,
                to_state.value if to_state else None,
                json.dumps(payload, separators=(",", ":")),
            ),
        )

    def append_session_event(
        self,
        session: FocusSession,
        *,
        occurred_at: datetime,
        event: str,
        payload: dict[str, Any] | None = None,
    ) -> None:
        with self._lock, self._connection:
            self._insert_event(
                session.id,
                session.version,
                occurred_at,
                event,
                session.state,
                session.state,
                payload or {},
            )

    def interrupt_active_sessions(self, occurred_at: datetime) -> int:
        sessions = self.list_active_sessions()
        for session in sessions:
            ended = replace(
                session,
                state=SessionState.ENDED_EARLY,
                version=session.version + 1,
                ended_at=occurred_at,
                end_reason="process_interrupted",
                current_break_index=None,
                current_break_elapsed_seconds=0.0,
            )
            self.transition_session(
                session,
                ended,
                occurred_at=occurred_at,
                event="process_interrupted",
            )
        return len(sessions)

    def create_snapshot(self, snapshot: SessionSnapshot) -> SessionSnapshot:
        with self._lock, self._connection:
            self._connection.execute(
                """INSERT INTO snapshots
                   (id, session_id, sequence, captured_at, image, session_version,
                    captured_state, reasoning_eligible, status, error)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                (
                    snapshot.id,
                    snapshot.session_id,
                    snapshot.sequence,
                    _iso(snapshot.captured_at),
                    snapshot.image,
                    snapshot.session_version,
                    snapshot.captured_state.value,
                    int(snapshot.reasoning_eligible),
                    snapshot.status.value,
                    snapshot.error,
                ),
            )
        return snapshot

    def get_snapshot(self, snapshot_id: str) -> SessionSnapshot:
        with self._lock:
            row = self._connection.execute(
                "SELECT * FROM snapshots WHERE id = ?", (snapshot_id,)
            ).fetchone()
        if row is None:
            raise NotFoundError(f"snapshot does not exist: {snapshot_id}")
        return SessionSnapshot(
            id=row["id"],
            session_id=row["session_id"],
            sequence=row["sequence"],
            captured_at=_datetime(row["captured_at"]),
            image=row["image"],
            session_version=row["session_version"],
            captured_state=SessionState(row["captured_state"]),
            reasoning_eligible=bool(row["reasoning_eligible"]),
            status=SnapshotStatus(row["status"]),
            error=row["error"],
        )

    def finalize_snapshot(
        self,
        snapshot_id: str,
        status: SnapshotStatus,
        *,
        finalized_at: datetime,
        error: str | None = None,
    ) -> SessionSnapshot:
        if status == SnapshotStatus.CAPTURED:
            raise ValueError("captured is not a final snapshot status")
        if status == SnapshotStatus.API_ERROR and not error:
            raise ValueError("api_error snapshots require an error message")
        if status != SnapshotStatus.API_ERROR:
            error = None
        with self._lock, self._connection:
            cursor = self._connection.execute(
                """UPDATE snapshots SET status = ?, error = ?, finalized_at = ?
                   WHERE id = ? AND status = 'captured'""",
                (status.value, error, _iso(finalized_at), snapshot_id),
            )
            if cursor.rowcount != 1:
                raise ConcurrentUpdateError(
                    f"snapshot is missing or already finalized: {snapshot_id}"
                )
        return replace(self.get_snapshot(snapshot_id), status=status, error=error)

    def complete_snapshot_with_observation(
        self, observation: ObservationRecord
    ) -> ObservationRecord:
        """Atomically mark a captured snapshot observed and store its result."""
        with self._lock, self._connection:
            cursor = self._connection.execute(
                """UPDATE snapshots SET status = 'observed', error = NULL,
                   finalized_at = ? WHERE id = ? AND status = 'captured'""",
                (_iso(observation.processed_at), observation.snapshot_id),
            )
            if cursor.rowcount != 1:
                raise ConcurrentUpdateError(
                    "snapshot is missing or already finalized: "
                    f"{observation.snapshot_id}"
                )
            self._connection.execute(
                """INSERT INTO observations
                   (id, session_id, snapshot_id, sequence, captured_at, processed_at,
                    image, session_version, captured_state, reasoning_eligible,
                    observation_json) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                (
                    observation.id,
                    observation.session_id,
                    observation.snapshot_id,
                    observation.sequence,
                    _iso(observation.captured_at),
                    _iso(observation.processed_at),
                    observation.image,
                    observation.session_version,
                    observation.captured_state.value,
                    int(observation.reasoning_eligible),
                    json.dumps(dict(observation.observation), separators=(",", ":")),
                ),
            )
        return observation

    def recent_observations(
        self,
        session_id: str,
        *,
        limit: int = 20,
        reasoning_eligible_only: bool = False,
    ) -> tuple[ObservationRecord, ...]:
        if limit <= 0:
            raise ValueError("observation limit must be greater than zero")
        eligibility = "AND reasoning_eligible = 1" if reasoning_eligible_only else ""
        with self._lock:
            rows = self._connection.execute(
                f"""SELECT * FROM observations WHERE session_id = ? {eligibility}
                    ORDER BY sequence DESC LIMIT ?""",
                (session_id, limit),
            ).fetchall()
        records = [self._observation_from_row(row) for row in reversed(rows)]
        return tuple(records)

    def _observation_from_row(self, row: sqlite3.Row) -> ObservationRecord:
        return ObservationRecord(
            id=row["id"],
            session_id=row["session_id"],
            snapshot_id=row["snapshot_id"],
            sequence=row["sequence"],
            captured_at=_datetime(row["captured_at"]),
            processed_at=_datetime(row["processed_at"]),
            image=row["image"],
            session_version=row["session_version"],
            captured_state=SessionState(row["captured_state"]),
            reasoning_eligible=bool(row["reasoning_eligible"]),
            observation=json.loads(row["observation_json"]),
        )

    def count_rows(self, table: str) -> int:
        allowed = {
            "goals",
            "deviation_profiles",
            "session_contracts",
            "focus_sessions",
            "session_events",
            "snapshots",
            "observations",
        }
        if table not in allowed:
            raise ValueError("unsupported table")
        with self._lock:
            return self._connection.execute(
                f"SELECT COUNT(*) AS count FROM {table}"
            ).fetchone()["count"]
