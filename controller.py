"""Deterministic Focus Session orchestration and timing."""

from __future__ import annotations

import threading
import time
from dataclasses import dataclass, replace
from datetime import datetime, timedelta
from pathlib import Path
from typing import Any, Mapping, Protocol

from artifacts import ArtifactClaim, FilesystemSessionArtifacts
from domain import (
    DeviationProfile,
    FocusSession,
    Goal,
    GoalStatus,
    ObservationRecord,
    ReasoningPort,
    ReasoningDecisionKind,
    ReasoningProposal,
    ReasoningRequest,
    ReasoningMode,
    ScheduledBreak,
    Sensitivity,
    SessionContract,
    SessionContractDraft,
    SessionSnapshot,
    SessionState,
    SnapshotStatus,
)
from storage import ActiveSessionError, NotFoundError, SQLiteRepository


class Clock(Protocol):
    def now(self) -> datetime: ...

    def monotonic(self) -> float: ...


class SystemClock:
    def now(self) -> datetime:
        return datetime.now().astimezone()

    def monotonic(self) -> float:
        return time.monotonic()


class InvalidReasoningProposal(RuntimeError):
    pass


@dataclass
class _RuntimeTimer:
    last_monotonic: float


class FocusSessionController:
    """The only component allowed to mutate authoritative session state."""

    def __init__(
        self,
        repository: SQLiteRepository,
        *,
        clock: Clock | None = None,
        reasoning_port: ReasoningPort | None = None,
        artifact_store: FilesystemSessionArtifacts | None = None,
        recent_observation_limit: int = 20,
        reconcile_interrupted: bool = True,
    ) -> None:
        if recent_observation_limit <= 0:
            raise ValueError("recent observation limit must be greater than zero")
        self.repository = repository
        self.clock = clock or SystemClock()
        self.reasoning_port = reasoning_port
        self.artifact_store = artifact_store or FilesystemSessionArtifacts()
        self.recent_observation_limit = recent_observation_limit
        self._lock = threading.RLock()
        self._runtime: dict[str, _RuntimeTimer] = {}
        if reconcile_interrupted:
            self.repository.interrupt_active_sessions(self.clock.now())

    def create_goal(self, title: str, description: str | None = None) -> Goal:
        return self.repository.create_goal(
            Goal(title=title, description=description, created_at=self.clock.now())
        )

    def update_goal(
        self, goal_id: str, *, title: str, description: str | None = None
    ) -> Goal:
        current = self.repository.get_goal(goal_id)
        if current.status != GoalStatus.ACTIVE:
            raise ValueError("completed goals cannot be edited")
        return self.repository.update_goal(
            replace(current, title=title, description=description)
        )

    def delete_goal(self, goal_id: str, *, confirmed: bool) -> None:
        if not confirmed:
            raise ValueError("goal deletion requires explicit confirmation")
        with self._lock:
            sessions = self.repository.list_sessions_for_goal(goal_id)
            if any(session.state.is_active for session in sessions):
                raise ActiveSessionError("goal has an active focus session")
            for session in sessions:
                self.artifact_store.validate(session.id, session.session_dir)
            self.repository.delete_goal(goal_id)
            for session in sessions:
                self.artifact_store.delete(session.id, session.session_dir)

    def delete_session(self, session_id: str, *, confirmed: bool) -> None:
        if not confirmed:
            raise ValueError("session deletion requires explicit confirmation")
        with self._lock:
            session = self.repository.get_session(session_id)
            if session.state.is_active:
                raise ActiveSessionError("cannot delete an active focus session")
            self.artifact_store.validate(session.id, session.session_dir)
            self.repository.delete_session(session.id)
            self.artifact_store.delete(session.id, session.session_dir)

    def save_deviation_profile(
        self, profile: DeviationProfile
    ) -> DeviationProfile:
        return self.repository.save_deviation_profile(profile)

    def prepare_contract(
        self,
        goal_id: str,
        *,
        target_focus_seconds: int,
        scheduled_breaks: tuple[ScheduledBreak, ...] = (),
        deviation_profile: DeviationProfile | None = None,
        reasoning_mode: ReasoningMode = ReasoningMode.PROFILE_ONLY,
        sensitivity: Sensitivity = Sensitivity.BALANCED,
    ) -> SessionContractDraft:
        goal = self.repository.get_goal(goal_id)
        if goal.status != GoalStatus.ACTIVE:
            raise ValueError("a Focus Session requires an active Goal")
        return SessionContractDraft(
            goal_id=goal.id,
            goal_title=goal.title,
            goal_description=goal.description,
            target_focus_seconds=target_focus_seconds,
            scheduled_breaks=scheduled_breaks,
            deviation_profile_id=(deviation_profile.id if deviation_profile else None),
            deviation_snapshot=(
                tuple(deviation_profile.deviations) if deviation_profile else ()
            ),
            reasoning_mode=reasoning_mode,
            sensitivity=sensitivity,
        )

    def confirm_contract(
        self, draft: SessionContractDraft, *, confirmed_at: datetime | None = None
    ) -> SessionContract:
        return SessionContract(
            id=draft.id,
            goal_id=draft.goal_id,
            goal_title=draft.goal_title,
            goal_description=draft.goal_description,
            target_focus_seconds=draft.target_focus_seconds,
            scheduled_breaks=draft.scheduled_breaks,
            deviation_profile_id=draft.deviation_profile_id,
            deviation_snapshot=draft.deviation_snapshot,
            reasoning_mode=draft.reasoning_mode,
            sensitivity=draft.sensitivity,
            confirmed_at=confirmed_at or self.clock.now(),
        )

    def start_monitoring(
        self,
        contract: SessionContract,
        *,
        session_dir: Path | str | None = None,
        started_at: datetime | None = None,
    ) -> FocusSession:
        """Create durable state only after contract and preflight confirmation."""
        with self._lock:
            goal = self.repository.get_goal(contract.goal_id)
            if goal.status != GoalStatus.ACTIVE:
                raise ValueError("a Focus Session requires an active Goal")
            started = started_at or self.clock.now()
            next_break = 0 if contract.scheduled_breaks else None
            session = FocusSession(
                goal_id=goal.id,
                contract=contract,
                state=SessionState.FOCUSING,
                version=1,
                created_at=started,
                started_at=started,
                current_break_index=next_break,
                session_dir=None,
            )
            claim: ArtifactClaim | None = None
            if session_dir is not None:
                claim = self.artifact_store.claim(session.id, session_dir)
                session = replace(session, session_dir=str(claim.path))
            try:
                self.repository.create_session(session)
            except Exception:
                if claim is not None:
                    self.artifact_store.rollback_claim(claim)
                raise
            self._runtime[session.id] = _RuntimeTimer(self.clock.monotonic())
            return session

    def get_session(self, session_id: str) -> FocusSession:
        return self.repository.get_session(session_id)

    def advance_time(
        self,
        session_id: str,
        *,
        monotonic_now: float | None = None,
        wall_now: datetime | None = None,
    ) -> FocusSession:
        """Consume elapsed time exactly across focus and scheduled-break boundaries."""
        with self._lock:
            session = self.repository.get_session(session_id)
            if not session.state.is_active:
                return session
            runtime = self._runtime.get(session_id)
            if runtime is None:
                raise RuntimeError("active session has no runtime timer")
            current_monotonic = (
                self.clock.monotonic() if monotonic_now is None else monotonic_now
            )
            if current_monotonic < runtime.last_monotonic:
                raise ValueError("monotonic time cannot move backwards")
            elapsed = current_monotonic - runtime.last_monotonic
            current_wall = wall_now or self.clock.now()
            cursor_wall = current_wall - timedelta(seconds=elapsed)
            runtime.last_monotonic = current_monotonic
            remaining = elapsed
            epsilon = 1e-9

            while session.state.is_active and (
                remaining > epsilon or self._boundary_due(session, epsilon)
            ):
                if session.state == SessionState.FOCUSING:
                    distance_to_target = max(
                        0.0,
                        session.contract.target_focus_seconds
                        - session.accumulated_focus_seconds,
                    )
                    distance_to_break = self._distance_to_break(session)
                    distance = min(distance_to_target, distance_to_break)
                    consumed = min(remaining, distance)
                    if consumed > 0:
                        session = replace(
                            session,
                            accumulated_focus_seconds=(
                                session.accumulated_focus_seconds + consumed
                            ),
                        )
                        remaining -= consumed
                        cursor_wall += timedelta(seconds=consumed)

                    if (
                        session.accumulated_focus_seconds
                        >= session.contract.target_focus_seconds - epsilon
                    ):
                        session = self._transition(
                            session,
                            state=SessionState.FULFILLED,
                            occurred_at=cursor_wall,
                            event="focus_target_reached",
                            end_reason="target_reached",
                        )
                        break
                    if self._distance_to_break(session) <= epsilon:
                        session = self._transition(
                            session,
                            state=SessionState.SCHEDULED_BREAK,
                            occurred_at=cursor_wall,
                            event="scheduled_break_started",
                            current_break_elapsed_seconds=0.0,
                        )
                        continue
                    if remaining <= epsilon:
                        break

                elif session.state == SessionState.SCHEDULED_BREAK:
                    if session.current_break_index is None:
                        raise RuntimeError("scheduled break is missing its index")
                    scheduled_break = session.contract.scheduled_breaks[
                        session.current_break_index
                    ]
                    break_remaining = max(
                        0.0,
                        scheduled_break.duration_seconds
                        - session.current_break_elapsed_seconds,
                    )
                    consumed = min(remaining, break_remaining)
                    if consumed > 0:
                        session = replace(
                            session,
                            current_break_elapsed_seconds=(
                                session.current_break_elapsed_seconds + consumed
                            ),
                        )
                        remaining -= consumed
                        cursor_wall += timedelta(seconds=consumed)
                    if (
                        session.current_break_elapsed_seconds
                        >= scheduled_break.duration_seconds - epsilon
                    ):
                        next_index = session.current_break_index + 1
                        if next_index >= len(session.contract.scheduled_breaks):
                            next_index = None
                        session = self._transition(
                            session,
                            state=SessionState.FOCUSING,
                            occurred_at=cursor_wall,
                            event="scheduled_break_ended",
                            current_break_index=next_index,
                            current_break_elapsed_seconds=0.0,
                        )
                        continue
                    if remaining <= epsilon:
                        break
                else:
                    # Recovery states are reserved for the teammate's integration.
                    break

            if session.state.is_active:
                self.repository.save_session_progress(session)
            else:
                self._runtime.pop(session_id, None)
            return session

    def _distance_to_break(self, session: FocusSession) -> float:
        if session.current_break_index is None:
            return float("inf")
        scheduled_break = session.contract.scheduled_breaks[session.current_break_index]
        return max(
            0.0,
            scheduled_break.focus_offset_seconds - session.accumulated_focus_seconds,
        )

    def _boundary_due(self, session: FocusSession, epsilon: float) -> bool:
        if session.state == SessionState.FOCUSING:
            return (
                session.accumulated_focus_seconds
                >= session.contract.target_focus_seconds - epsilon
                or self._distance_to_break(session) <= epsilon
            )
        if session.state == SessionState.SCHEDULED_BREAK:
            if session.current_break_index is None:
                return False
            scheduled_break = session.contract.scheduled_breaks[
                session.current_break_index
            ]
            return (
                session.current_break_elapsed_seconds
                >= scheduled_break.duration_seconds - epsilon
            )
        return False

    def _transition(
        self,
        session: FocusSession,
        *,
        state: SessionState,
        occurred_at: datetime,
        event: str,
        payload: dict[str, Any] | None = None,
        end_reason: str | None = None,
        current_break_index: int | None | object = ...,
        current_break_elapsed_seconds: float | None = None,
    ) -> FocusSession:
        changes: dict[str, Any] = {
            "state": state,
            "version": session.version + 1,
        }
        if current_break_index is not ...:
            changes["current_break_index"] = current_break_index
        if current_break_elapsed_seconds is not None:
            changes["current_break_elapsed_seconds"] = current_break_elapsed_seconds
        if not state.is_active:
            changes.update(
                ended_at=occurred_at,
                end_reason=end_reason or state.value,
                current_break_index=None,
                current_break_elapsed_seconds=0.0,
            )
        updated = replace(session, **changes)
        return self.repository.transition_session(
            session,
            updated,
            occurred_at=occurred_at,
            event=event,
            payload=payload,
        )

    def end_early(
        self,
        session_id: str,
        *,
        reason: str = "user_ended",
        monotonic_now: float | None = None,
        ended_at: datetime | None = None,
    ) -> FocusSession:
        with self._lock:
            final_wall = ended_at or self.clock.now()
            session = self.advance_time(
                session_id,
                monotonic_now=monotonic_now,
                wall_now=final_wall,
            )
            if not session.state.is_active:
                return session
            ended = self._transition(
                session,
                state=SessionState.ENDED_EARLY,
                occurred_at=final_wall,
                event="session_ended_early",
                end_reason=reason,
            )
            self._runtime.pop(session_id, None)
            return ended

    def fulfill(
        self, session_id: str, *, reason: str = "goal_completed"
    ) -> FocusSession:
        with self._lock:
            session = self.advance_time(session_id)
            if not session.state.is_active:
                return session
            fulfilled = self._transition(
                session,
                state=SessionState.FULFILLED,
                occurred_at=self.clock.now(),
                event="session_fulfilled",
                end_reason=reason,
            )
            self._runtime.pop(session_id, None)
            return fulfilled

    def complete_goal(self, session_id: str) -> tuple[FocusSession, Goal]:
        session = self.fulfill(session_id, reason="goal_completed")
        if session.state != SessionState.FULFILLED:
            raise ValueError("a Goal can only be completed from an active Focus Session")
        goal = self.repository.get_goal(session.goal_id)
        if goal.status == GoalStatus.COMPLETED:
            return session, goal
        completed = replace(
            goal,
            status=GoalStatus.COMPLETED,
            completed_at=self.clock.now(),
        )
        return session, self.repository.update_goal(completed)

    def record_snapshot(
        self,
        session_id: str,
        *,
        sequence: int,
        captured_at: datetime,
        image: str,
    ) -> SessionSnapshot:
        with self._lock:
            session = self.advance_time(session_id)
            if not session.state.is_active:
                raise ActiveSessionError("cannot capture after a Focus Session ends")
            snapshot = SessionSnapshot(
                session_id=session.id,
                sequence=sequence,
                captured_at=captured_at,
                image=image,
                session_version=session.version,
                captured_state=session.state,
                reasoning_eligible=session.state == SessionState.FOCUSING,
            )
            return self.repository.create_snapshot(snapshot)

    def finalize_snapshot(
        self,
        snapshot_id: str,
        status: SnapshotStatus,
        *,
        error: str | None = None,
    ) -> SessionSnapshot:
        return self.repository.finalize_snapshot(
            snapshot_id,
            status,
            finalized_at=self.clock.now(),
            error=error,
        )

    def persist_observation(
        self,
        snapshot_id: str,
        observation: dict[str, Any],
        *,
        processed_at: datetime | None = None,
    ) -> ObservationRecord:
        with self._lock:
            snapshot = self.repository.get_snapshot(snapshot_id)
            session = self.repository.get_session(snapshot.session_id)
            eligible = (
                snapshot.reasoning_eligible
                and session.state == SessionState.FOCUSING
                and session.version == snapshot.session_version
            )
            record = ObservationRecord(
                session_id=snapshot.session_id,
                snapshot_id=snapshot.id,
                sequence=snapshot.sequence,
                captured_at=snapshot.captured_at,
                processed_at=processed_at or self.clock.now(),
                image=snapshot.image,
                session_version=snapshot.session_version,
                captured_state=snapshot.captured_state,
                reasoning_eligible=eligible,
                observation=observation,
            )
            return self.repository.complete_snapshot_with_observation(record)

    def evaluate_observation(
        self, observation: ObservationRecord
    ) -> ReasoningProposal | None:
        if self.reasoning_port is None or not observation.reasoning_eligible:
            return None
        with self._lock:
            try:
                stored_observation = self.repository.get_observation(observation.id)
            except NotFoundError as error:
                raise InvalidReasoningProposal(
                    "reasoning input observation is not persisted"
                ) from error
            if stored_observation != observation:
                raise InvalidReasoningProposal(
                    "reasoning input observation does not match persisted data"
                )
            observation = stored_observation
            session = self.repository.get_session(observation.session_id)
            if (
                session.state != SessionState.FOCUSING
                or session.version != observation.session_version
            ):
                return None
            recent = self.repository.recent_observations(
                session.id,
                limit=self.recent_observation_limit,
                reasoning_eligible_only=True,
            )
            request = ReasoningRequest(
                session_id=session.id,
                session_version=session.version,
                contract=session.contract,
                observation=observation,
                recent_observations=recent,
            )
        proposal = self.reasoning_port.evaluate(request)
        if not isinstance(proposal, ReasoningProposal):
            raise InvalidReasoningProposal("reasoning port returned an invalid proposal type")
        if (
            proposal.session_id != request.session_id
            or proposal.session_version != request.session_version
            or proposal.observation_id != observation.id
        ):
            raise InvalidReasoningProposal(
                "reasoning proposal does not match its request identifiers"
            )
        with self._lock:
            current = self.repository.get_session(observation.session_id)
            if (
                current.state != SessionState.FOCUSING
                or current.version != proposal.session_version
            ):
                raise InvalidReasoningProposal("reasoning proposal became stale")
            kind, validated_payload = self._validate_reasoning_proposal(
                current, observation, proposal
            )
            event_payload = {
                "observation_id": observation.id,
                "kind": kind.value,
                **validated_payload,
            }
            if kind == ReasoningDecisionKind.CONTINUE_OBSERVING:
                self.repository.append_session_event(
                    current,
                    occurred_at=self.clock.now(),
                    event="reasoning_proposal_received",
                    payload=event_payload,
                )
            else:
                self._transition(
                    current,
                    state=SessionState.RECOVERY_CHECK_IN,
                    occurred_at=self.clock.now(),
                    event="intervention_admitted",
                    payload=event_payload,
                )
        return proposal

    def _validate_reasoning_proposal(
        self,
        session: FocusSession,
        triggering_observation: ObservationRecord,
        proposal: ReasoningProposal,
    ) -> tuple[ReasoningDecisionKind, dict[str, Any]]:
        try:
            kind = ReasoningDecisionKind(proposal.kind)
        except ValueError as error:
            raise InvalidReasoningProposal(
                f"unsupported reasoning proposal kind: {proposal.kind}"
            ) from error

        payload = dict(proposal.payload)
        if kind == ReasoningDecisionKind.CONTINUE_OBSERVING:
            if payload:
                raise InvalidReasoningProposal(
                    "continue_observing does not accept a payload"
                )
            return kind, {}

        allowed_fields = {
            "deviation_id",
            "evidence_start_observation_id",
            "evidence_latest_observation_id",
            "key_observation_ids",
            "contradictory_or_indeterminate_observation_ids",
            "rationale",
        }
        if set(payload) != allowed_fields:
            missing = sorted(allowed_fields - set(payload))
            unknown = sorted(set(payload) - allowed_fields)
            details = []
            if missing:
                details.append(f"missing fields: {', '.join(missing)}")
            if unknown:
                details.append(f"unknown fields: {', '.join(unknown)}")
            raise InvalidReasoningProposal("; ".join(details))

        deviation_id = self._required_payload_text(payload, "deviation_id")
        rationale = self._required_payload_text(payload, "rationale")
        evidence_start_id = self._required_payload_text(
            payload, "evidence_start_observation_id"
        )
        evidence_latest_id = self._required_payload_text(
            payload, "evidence_latest_observation_id"
        )
        key_ids = self._observation_id_list(payload, "key_observation_ids", required=True)
        contrary_ids = self._observation_id_list(
            payload,
            "contradictory_or_indeterminate_observation_ids",
            required=False,
        )
        if set(key_ids) & set(contrary_ids):
            raise InvalidReasoningProposal(
                "supporting and contradictory observation references must be disjoint"
            )

        listed_deviation_ids = {
            deviation.id for deviation in session.contract.deviation_snapshot
        }
        if session.contract.reasoning_mode == ReasoningMode.PROFILE_ONLY:
            if deviation_id not in listed_deviation_ids:
                raise InvalidReasoningProposal(
                    "Profile Only interventions require a listed Deviation"
                )
        elif deviation_id != "unlisted" and deviation_id not in listed_deviation_ids:
            raise InvalidReasoningProposal(
                "Exploratory interventions require a listed Deviation or 'unlisted'"
            )

        referenced_ids = {
            evidence_start_id,
            evidence_latest_id,
            *key_ids,
            *contrary_ids,
        }
        referenced: dict[str, ObservationRecord] = {}
        for observation_id in referenced_ids:
            try:
                record = self.repository.get_observation(observation_id)
            except NotFoundError as error:
                raise InvalidReasoningProposal(
                    f"referenced observation does not exist: {observation_id}"
                ) from error
            if record.session_id != session.id:
                raise InvalidReasoningProposal(
                    f"referenced observation belongs to another session: {observation_id}"
                )
            if not record.reasoning_eligible:
                raise InvalidReasoningProposal(
                    f"referenced observation is not eligible evidence: {observation_id}"
                )
            if record.sequence > triggering_observation.sequence:
                raise InvalidReasoningProposal(
                    f"referenced observation is newer than the request: {observation_id}"
                )
            referenced[observation_id] = record

        start_sequence = referenced[evidence_start_id].sequence
        latest_sequence = referenced[evidence_latest_id].sequence
        if start_sequence > latest_sequence:
            raise InvalidReasoningProposal(
                "evidence start must not occur after the latest evidence"
            )
        for observation_id in (*key_ids, *contrary_ids):
            sequence = referenced[observation_id].sequence
            if not start_sequence <= sequence <= latest_sequence:
                raise InvalidReasoningProposal(
                    "episode references must fall between evidence start and latest"
                )

        return kind, {
            "deviation_id": deviation_id,
            "evidence_start_observation_id": evidence_start_id,
            "evidence_latest_observation_id": evidence_latest_id,
            "key_observation_ids": list(key_ids),
            "contradictory_or_indeterminate_observation_ids": list(contrary_ids),
            "rationale": rationale,
        }

    @staticmethod
    def _required_payload_text(payload: Mapping[str, Any], field: str) -> str:
        value = payload.get(field)
        if not isinstance(value, str) or not value.strip():
            raise InvalidReasoningProposal(f"{field} must be a nonempty string")
        return value.strip()

    @staticmethod
    def _observation_id_list(
        payload: Mapping[str, Any], field: str, *, required: bool
    ) -> tuple[str, ...]:
        value = payload.get(field)
        if not isinstance(value, (list, tuple)):
            raise InvalidReasoningProposal(f"{field} must be a list")
        if required and not value:
            raise InvalidReasoningProposal(f"{field} must not be empty")
        if len(value) > 5:
            raise InvalidReasoningProposal(f"{field} must contain at most five references")
        if any(not isinstance(item, str) or not item.strip() for item in value):
            raise InvalidReasoningProposal(f"{field} contains an invalid identifier")
        cleaned = tuple(item.strip() for item in value)
        if len(cleaned) != len(set(cleaned)):
            raise InvalidReasoningProposal(f"{field} contains duplicate identifiers")
        return cleaned

    def record_reasoning_error(
        self, observation: ObservationRecord, error: Exception
    ) -> None:
        with self._lock:
            session = self.repository.get_session(observation.session_id)
            self.repository.append_session_event(
                session,
                occurred_at=self.clock.now(),
                event="reasoning_error",
                payload={"observation_id": observation.id, "error": str(error)},
            )
