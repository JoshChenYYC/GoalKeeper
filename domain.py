"""Immutable domain types shared by the GoalKeeper controller and agents."""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime
from enum import Enum
from types import MappingProxyType
from typing import Any, Mapping, Protocol
from uuid import uuid4


def new_id() -> str:
    return str(uuid4())


def require_aware(value: datetime, name: str) -> None:
    if value.tzinfo is None or value.utcoffset() is None:
        raise ValueError(f"{name} must be timezone-aware")


def _clean_required(value: str, name: str) -> str:
    cleaned = value.strip()
    if not cleaned:
        raise ValueError(f"{name} is required")
    return cleaned


class GoalStatus(str, Enum):
    ACTIVE = "active"
    COMPLETED = "completed"


class Observability(str, Enum):
    OBSERVABLE = "observable"
    PARTIAL = "partially_observable"
    NOT_OBSERVABLE = "not_visually_observable"


class ReasoningMode(str, Enum):
    PROFILE_ONLY = "profile_only"
    EXPLORATORY = "exploratory"


class Sensitivity(str, Enum):
    STRICT = "strict"
    BALANCED = "balanced"
    RELAXED = "relaxed"


class SessionState(str, Enum):
    FOCUSING = "focusing"
    SCHEDULED_BREAK = "scheduled_break"
    RECOVERY_CHECK_IN = "recovery_check_in"
    RECOVERY_WINDOW = "recovery_window"
    AWAITING_RESPONSE = "awaiting_response"
    FULFILLED = "fulfilled"
    ENDED_EARLY = "ended_early"

    @property
    def is_active(self) -> bool:
        return self not in {SessionState.FULFILLED, SessionState.ENDED_EARLY}


class SnapshotStatus(str, Enum):
    CAPTURED = "captured"
    OBSERVED = "observed"
    SUPERSEDED = "superseded"
    API_ERROR = "api_error"


class ReasoningDecisionKind(str, Enum):
    CONTINUE_OBSERVING = "continue_observing"
    BEGIN_RECOVERY_CHECK_IN = "begin_recovery_check_in"


@dataclass(frozen=True, slots=True)
class Goal:
    title: str
    description: str | None = None
    id: str = field(default_factory=new_id)
    status: GoalStatus = GoalStatus.ACTIVE
    created_at: datetime = field(default_factory=lambda: datetime.now().astimezone())
    completed_at: datetime | None = None

    def __post_init__(self) -> None:
        object.__setattr__(self, "title", _clean_required(self.title, "goal title"))
        if self.description is not None:
            description = self.description.strip()
            object.__setattr__(self, "description", description or None)
        require_aware(self.created_at, "created_at")
        if self.completed_at is not None:
            require_aware(self.completed_at, "completed_at")
        if self.status == GoalStatus.COMPLETED and self.completed_at is None:
            raise ValueError("completed goals require completed_at")
        if self.status == GoalStatus.ACTIVE and self.completed_at is not None:
            raise ValueError("active goals cannot have completed_at")


@dataclass(frozen=True, slots=True)
class Deviation:
    description: str
    observability: Observability = Observability.OBSERVABLE
    id: str = field(default_factory=new_id)

    def __post_init__(self) -> None:
        object.__setattr__(
            self, "description", _clean_required(self.description, "deviation description")
        )


@dataclass(frozen=True, slots=True)
class DeviationProfile:
    deviations: tuple[Deviation, ...] = ()
    id: str = field(default_factory=new_id)
    created_at: datetime = field(default_factory=lambda: datetime.now().astimezone())
    updated_at: datetime = field(default_factory=lambda: datetime.now().astimezone())

    def __post_init__(self) -> None:
        object.__setattr__(self, "deviations", tuple(self.deviations))
        require_aware(self.created_at, "created_at")
        require_aware(self.updated_at, "updated_at")
        ids = [deviation.id for deviation in self.deviations]
        if len(ids) != len(set(ids)):
            raise ValueError("deviation identifiers must be unique")


@dataclass(frozen=True, slots=True)
class ScheduledBreak:
    focus_offset_seconds: int
    duration_seconds: int
    id: str = field(default_factory=new_id)

    def __post_init__(self) -> None:
        if self.focus_offset_seconds <= 0:
            raise ValueError("scheduled break offset must be greater than zero")
        if self.duration_seconds <= 0:
            raise ValueError("scheduled break duration must be greater than zero")


@dataclass(frozen=True, slots=True)
class SessionContractDraft:
    goal_id: str
    goal_title: str
    goal_description: str | None
    target_focus_seconds: int
    scheduled_breaks: tuple[ScheduledBreak, ...] = ()
    deviation_profile_id: str | None = None
    deviation_snapshot: tuple[Deviation, ...] = ()
    reasoning_mode: ReasoningMode = ReasoningMode.PROFILE_ONLY
    sensitivity: Sensitivity = Sensitivity.BALANCED
    id: str = field(default_factory=new_id)

    def __post_init__(self) -> None:
        object.__setattr__(self, "goal_title", _clean_required(self.goal_title, "goal title"))
        if self.goal_description is not None:
            description = self.goal_description.strip()
            object.__setattr__(self, "goal_description", description or None)
        if self.target_focus_seconds <= 0:
            raise ValueError("target focus duration must be greater than zero")
        breaks = tuple(sorted(self.scheduled_breaks, key=lambda item: item.focus_offset_seconds))
        object.__setattr__(self, "scheduled_breaks", breaks)
        object.__setattr__(self, "deviation_snapshot", tuple(self.deviation_snapshot))
        offsets = [item.focus_offset_seconds for item in breaks]
        if len(offsets) != len(set(offsets)):
            raise ValueError("scheduled break offsets must be unique")
        if any(offset >= self.target_focus_seconds for offset in offsets):
            raise ValueError("scheduled breaks must begin before the focus target")
        deviation_ids = [item.id for item in self.deviation_snapshot]
        if len(deviation_ids) != len(set(deviation_ids)):
            raise ValueError("deviation snapshot identifiers must be unique")


@dataclass(frozen=True, slots=True)
class SessionContract(SessionContractDraft):
    confirmed_at: datetime = field(default_factory=lambda: datetime.now().astimezone())

    def __post_init__(self) -> None:
        super(SessionContract, self).__post_init__()
        require_aware(self.confirmed_at, "confirmed_at")


@dataclass(frozen=True, slots=True)
class FocusSession:
    goal_id: str
    contract: SessionContract
    state: SessionState
    version: int
    created_at: datetime
    started_at: datetime
    accumulated_focus_seconds: float = 0.0
    current_break_index: int | None = None
    current_break_elapsed_seconds: float = 0.0
    ended_at: datetime | None = None
    end_reason: str | None = None
    session_dir: str | None = None
    id: str = field(default_factory=new_id)

    def __post_init__(self) -> None:
        require_aware(self.created_at, "created_at")
        require_aware(self.started_at, "started_at")
        if self.ended_at is not None:
            require_aware(self.ended_at, "ended_at")
        if self.version < 1:
            raise ValueError("session version must be at least one")
        if self.accumulated_focus_seconds < 0:
            raise ValueError("accumulated focus time cannot be negative")
        if self.contract.goal_id != self.goal_id:
            raise ValueError("session goal must match its contract")
        if self.state.is_active and self.ended_at is not None:
            raise ValueError("active sessions cannot have ended_at")
        if not self.state.is_active and self.ended_at is None:
            raise ValueError("final sessions require ended_at")


@dataclass(frozen=True, slots=True)
class SessionSnapshot:
    session_id: str
    sequence: int
    captured_at: datetime
    image: str
    session_version: int
    captured_state: SessionState
    reasoning_eligible: bool
    id: str = field(default_factory=new_id)
    status: SnapshotStatus = SnapshotStatus.CAPTURED
    error: str | None = None

    def __post_init__(self) -> None:
        require_aware(self.captured_at, "captured_at")
        if self.sequence <= 0:
            raise ValueError("snapshot sequence must be greater than zero")
        if self.status == SnapshotStatus.API_ERROR and not self.error:
            raise ValueError("api_error snapshots require an error message")
        if self.status != SnapshotStatus.API_ERROR and self.error is not None:
            raise ValueError("only api_error snapshots may contain an error")


@dataclass(frozen=True, slots=True)
class ObservationRecord:
    session_id: str
    snapshot_id: str
    sequence: int
    captured_at: datetime
    processed_at: datetime
    image: str
    session_version: int
    captured_state: SessionState
    reasoning_eligible: bool
    observation: Mapping[str, Any]
    id: str = field(default_factory=new_id)

    def __post_init__(self) -> None:
        require_aware(self.captured_at, "captured_at")
        require_aware(self.processed_at, "processed_at")
        object.__setattr__(self, "observation", MappingProxyType(dict(self.observation)))


@dataclass(frozen=True, slots=True)
class ReasoningRequest:
    session_id: str
    session_version: int
    contract: SessionContract
    observation: ObservationRecord
    recent_observations: tuple[ObservationRecord, ...]


@dataclass(frozen=True, slots=True)
class ReasoningProposal:
    session_id: str
    session_version: int
    observation_id: str
    kind: str
    payload: Mapping[str, Any] = field(default_factory=dict)

    def __post_init__(self) -> None:
        object.__setattr__(self, "kind", _clean_required(self.kind, "proposal kind"))
        object.__setattr__(self, "payload", MappingProxyType(dict(self.payload)))


class ReasoningPort(Protocol):
    def evaluate(self, request: ReasoningRequest) -> ReasoningProposal:
        """Return a proposal only; the controller remains authoritative."""
