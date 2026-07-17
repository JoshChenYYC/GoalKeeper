import io
import json
import tempfile
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor
from dataclasses import FrozenInstanceError
from datetime import datetime, timedelta, timezone
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

import capture
from artifacts import ArtifactOwnershipError
from controller import FocusSessionController, InvalidReasoningProposal
from domain import (
    Deviation,
    DeviationProfile,
    Goal,
    ReasoningMode,
    ReasoningProposal,
    ScheduledBreak,
    SessionState,
    SnapshotStatus,
)
from perception import OpenAIPerceptionAdapter
from storage import ActiveSessionError, SQLiteRepository


OBSERVATION = {
    "image_quality": {"value": "adequate", "limitations": []},
    "people_count": 1,
    "objects": ["laptop"],
    "observations": [],
}


class FakeClock:
    def __init__(self) -> None:
        self.wall = datetime(2026, 7, 16, 12, 0, tzinfo=timezone.utc)
        self.monotonic_value = 100.0
        self._lock = threading.Lock()

    def now(self) -> datetime:
        with self._lock:
            return self.wall

    def monotonic(self) -> float:
        with self._lock:
            return self.monotonic_value

    def advance(self, seconds: float) -> None:
        with self._lock:
            self.wall += timedelta(seconds=seconds)
            self.monotonic_value += seconds


class RecordingReasoner:
    def __init__(self) -> None:
        self.requests = []

    def evaluate(self, request):
        self.requests.append(request)
        return ReasoningProposal(
            session_id=request.session_id,
            session_version=request.session_version,
            observation_id=request.observation.id,
            kind="continue_observing",
        )


class FakeCamera:
    def __init__(self) -> None:
        self.released = False

    def release(self) -> None:
        self.released = True


class ControllerTestCase(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.repository = SQLiteRepository(self.root / "goalkeeper.db")
        self.clock = FakeClock()
        self.controller = FocusSessionController(
            self.repository,
            clock=self.clock,
            reconcile_interrupted=False,
        )

    def tearDown(self) -> None:
        self.repository.close()
        self.temp.cleanup()

    def make_contract(self, *, target=60, breaks=()):
        goal = self.controller.create_goal("Write the paper", "Draft the introduction")
        draft = self.controller.prepare_contract(
            goal.id,
            target_focus_seconds=target,
            scheduled_breaks=tuple(breaks),
        )
        return goal, self.controller.confirm_contract(draft)

    def start_session(self, *, target=60, breaks=()):
        goal, contract = self.make_contract(target=target, breaks=breaks)
        session = self.controller.start_monitoring(
            contract, session_dir=self.root / "session"
        )
        return goal, contract, session

    def persist_observation(self, session, sequence=1):
        snapshot = self.controller.record_snapshot(
            session.id,
            sequence=sequence,
            captured_at=self.clock.now(),
            image=f"{sequence}.jpg",
        )
        return self.controller.persist_observation(snapshot.id, OBSERVATION)

    @staticmethod
    def intervention_payload(observation, deviation_id):
        return {
            "deviation_id": deviation_id,
            "evidence_start_observation_id": observation.id,
            "evidence_latest_observation_id": observation.id,
            "key_observation_ids": [observation.id],
            "contradictory_or_indeterminate_observation_ids": [],
            "rationale": "The cited visible pattern may conflict with the contract.",
        }

    def test_domain_models_validate_and_are_immutable(self):
        goal = Goal(title="  Study  ", created_at=self.clock.now())
        self.assertEqual(goal.title, "Study")
        with self.assertRaises(FrozenInstanceError):
            goal.title = "Changed"
        with self.assertRaisesRegex(ValueError, "required"):
            Goal(title=" ", created_at=self.clock.now())
        with self.assertRaisesRegex(ValueError, "unique"):
            self.make_contract(
                target=30,
                breaks=(
                    ScheduledBreak(10, 2),
                    ScheduledBreak(10, 3),
                ),
            )
        with self.assertRaisesRegex(ValueError, "before the focus target"):
            self.make_contract(target=10, breaks=(ScheduledBreak(10, 2),))

    def test_contract_contains_an_immutable_profile_snapshot(self):
        goal = self.controller.create_goal("Study")
        original = Deviation("Sustained attention to a phone")
        profile = DeviationProfile(
            deviations=(original,),
            created_at=self.clock.now(),
            updated_at=self.clock.now(),
        )
        draft = self.controller.prepare_contract(
            goal.id,
            target_focus_seconds=60,
            deviation_profile=profile,
        )
        updated_profile = DeviationProfile(
            id=profile.id,
            deviations=(Deviation("Leaving camera view"),),
            created_at=profile.created_at,
            updated_at=self.clock.now(),
        )
        self.controller.save_deviation_profile(updated_profile)
        contract = self.controller.confirm_contract(draft)

        self.assertEqual(contract.deviation_snapshot, (original,))
        self.assertNotEqual(
            contract.deviation_snapshot,
            self.repository.get_deviation_profile(profile.id).deviations,
        )

    def test_sqlite_round_trip_and_single_active_session_constraint(self):
        _, _, first = self.start_session()
        second_goal = self.controller.create_goal("Read")
        second_contract = self.controller.confirm_contract(
            self.controller.prepare_contract(
                second_goal.id, target_focus_seconds=30
            )
        )

        stored = self.repository.get_session(first.id)
        self.assertEqual(stored.contract.goal_title, "Write the paper")
        failed_session_dir = self.root / "failed-second-session"
        with self.assertRaises(ActiveSessionError):
            self.controller.start_monitoring(
                second_contract, session_dir=failed_session_dir
            )
        self.assertFalse(failed_session_dir.exists())

    def test_active_goal_is_locked_and_confirmed_delete_cascades(self):
        goal, _, session = self.start_session()
        session_dir = Path(session.session_dir)
        owned_image = session_dir / "owned.jpg"
        owned_image.write_bytes(b"jpeg")
        with self.assertRaises(ActiveSessionError):
            self.controller.update_goal(goal.id, title="Changed")
        with self.assertRaisesRegex(ValueError, "confirmation"):
            self.controller.delete_goal(goal.id, confirmed=False)
        with self.assertRaises(ActiveSessionError):
            self.controller.delete_goal(goal.id, confirmed=True)

        self.controller.end_early(session.id)
        self.controller.delete_goal(goal.id, confirmed=True)
        self.assertFalse(session_dir.exists())
        self.assertEqual(self.repository.count_rows("goals"), 0)
        self.assertEqual(self.repository.count_rows("focus_sessions"), 0)
        self.assertEqual(self.repository.count_rows("session_contracts"), 0)

    def test_confirmed_session_delete_removes_artifacts_and_preserves_goal(self):
        goal, _, session = self.start_session()
        session_dir = Path(session.session_dir)
        (session_dir / "snapshot.jpg").write_bytes(b"jpeg")
        self.controller.end_early(session.id)

        with self.assertRaisesRegex(ValueError, "confirmation"):
            self.controller.delete_session(session.id, confirmed=False)
        self.controller.delete_session(session.id, confirmed=True)

        self.assertFalse(session_dir.exists())
        self.assertEqual(self.repository.get_goal(goal.id), goal)
        self.assertEqual(self.repository.count_rows("focus_sessions"), 0)
        self.assertEqual(self.repository.count_rows("session_contracts"), 0)

    def test_artifact_deletion_refuses_unowned_directory_before_metadata_delete(self):
        goal, _, session = self.start_session()
        session_dir = Path(session.session_dir)
        self.controller.end_early(session.id)
        (session_dir / ".goalkeeper-session.json").unlink()

        with self.assertRaises(ArtifactOwnershipError):
            self.controller.delete_session(session.id, confirmed=True)

        self.assertEqual(self.repository.get_session(session.id).goal_id, goal.id)
        self.assertTrue(session_dir.exists())

    def test_monitoring_refuses_to_claim_a_nonempty_unowned_directory(self):
        _, contract = self.make_contract()
        unowned = self.root / "documents"
        unowned.mkdir()
        existing = unowned / "keep.txt"
        existing.write_text("keep", encoding="utf-8")

        with self.assertRaisesRegex(ArtifactOwnershipError, "nonempty unowned"):
            self.controller.start_monitoring(contract, session_dir=unowned)

        self.assertEqual(existing.read_text(encoding="utf-8"), "keep")
        self.assertEqual(self.repository.count_rows("focus_sessions"), 0)

    def test_monitoring_claims_a_confirmed_capture_directory(self):
        _, contract = self.make_contract()
        capture_dir = self.root / "session-20260716-120000"
        capture_dir.mkdir()
        (capture_dir / "preflight.jpg").write_bytes(b"jpeg")
        (capture_dir / "preflight.json").write_text("{}", encoding="utf-8")

        session = self.controller.start_monitoring(contract, session_dir=capture_dir)

        marker = capture_dir / ".goalkeeper-session.json"
        self.assertTrue(marker.is_file())
        self.assertIn(session.id, marker.read_text(encoding="utf-8"))

    def test_timer_crosses_break_boundaries_without_counting_break_time(self):
        _, _, session = self.start_session(
            target=30, breaks=(ScheduledBreak(10, 5),)
        )
        self.clock.advance(8)
        session = self.controller.advance_time(session.id)
        self.assertEqual(session.accumulated_focus_seconds, 8)
        self.assertEqual(session.state, SessionState.FOCUSING)

        self.clock.advance(4)
        session = self.controller.advance_time(session.id)
        self.assertEqual(session.state, SessionState.SCHEDULED_BREAK)
        self.assertEqual(session.accumulated_focus_seconds, 10)
        self.assertEqual(session.current_break_elapsed_seconds, 2)

        self.clock.advance(5)
        session = self.controller.advance_time(session.id)
        self.assertEqual(session.state, SessionState.FOCUSING)
        self.assertEqual(session.accumulated_focus_seconds, 12)

        self.clock.advance(18)
        session = self.controller.advance_time(session.id)
        self.assertEqual(session.state, SessionState.FULFILLED)
        self.assertEqual(session.accumulated_focus_seconds, 30)
        self.assertEqual(session.version, 4)
        self.assertEqual(
            session.ended_at,
            datetime(2026, 7, 16, 12, 0, 35, tzinfo=timezone.utc),
        )

    def test_one_delayed_tick_can_cross_the_whole_break_and_fulfill(self):
        _, _, session = self.start_session(
            target=30, breaks=(ScheduledBreak(10, 5),)
        )
        self.clock.advance(35)

        session = self.controller.advance_time(session.id)

        self.assertEqual(session.state, SessionState.FULFILLED)
        self.assertEqual(session.accumulated_focus_seconds, 30)
        self.assertEqual(session.version, 4)
        self.assertNotIn(session.id, self.controller._runtime)

    def test_manual_end_and_explicit_goal_completion_are_final(self):
        _, _, session = self.start_session()
        self.clock.advance(7)
        ended = self.controller.end_early(session.id, reason="user_stopped")
        self.assertEqual(ended.state, SessionState.ENDED_EARLY)
        self.assertEqual(ended.accumulated_focus_seconds, 7)
        self.assertEqual(ended.end_reason, "user_stopped")

        second_goal = self.controller.create_goal("Read")
        second_contract = self.controller.confirm_contract(
            self.controller.prepare_contract(
                second_goal.id, target_focus_seconds=30
            )
        )
        second = self.controller.start_monitoring(second_contract)
        fulfilled, completed_goal = self.controller.complete_goal(second.id)
        self.assertEqual(fulfilled.state, SessionState.FULFILLED)
        self.assertEqual(completed_goal.status.value, "completed")

    def test_new_controller_marks_an_abandoned_active_session_interrupted(self):
        _, _, session = self.start_session()
        self.clock.advance(4)
        self.controller.advance_time(session.id)

        FocusSessionController(self.repository, clock=self.clock)
        stored = self.repository.get_session(session.id)

        self.assertEqual(stored.state, SessionState.ENDED_EARLY)
        self.assertEqual(stored.end_reason, "process_interrupted")
        self.assertEqual(stored.accumulated_focus_seconds, 4)

    def test_snapshot_observation_fields_and_reasoning_boundary(self):
        reasoner = RecordingReasoner()
        self.controller.reasoning_port = reasoner
        _, _, session = self.start_session()
        snapshot = self.controller.record_snapshot(
            session.id,
            sequence=1,
            captured_at=self.clock.now(),
            image="one.jpg",
        )
        observation = self.controller.persist_observation(snapshot.id, OBSERVATION)

        proposal = self.controller.evaluate_observation(observation)

        self.assertEqual(proposal.kind, "continue_observing")
        self.assertEqual(observation.session_id, session.id)
        self.assertEqual(observation.sequence, 1)
        self.assertEqual(observation.captured_state, SessionState.FOCUSING)
        self.assertEqual(len(reasoner.requests), 1)
        self.assertEqual(reasoner.requests[0].recent_observations, (observation,))

    def test_break_observation_and_api_error_never_reach_reasoning(self):
        reasoner = RecordingReasoner()
        self.controller.reasoning_port = reasoner
        _, _, session = self.start_session(
            target=30, breaks=(ScheduledBreak(5, 5),)
        )
        self.clock.advance(6)
        snapshot = self.controller.record_snapshot(
            session.id,
            sequence=1,
            captured_at=self.clock.now(),
            image="break.jpg",
        )
        observation = self.controller.persist_observation(snapshot.id, OBSERVATION)
        self.assertFalse(observation.reasoning_eligible)
        self.assertIsNone(self.controller.evaluate_observation(observation))

        failed = self.controller.record_snapshot(
            session.id,
            sequence=2,
            captured_at=self.clock.now(),
            image="failed.jpg",
        )
        self.controller.finalize_snapshot(
            failed.id, SnapshotStatus.API_ERROR, error="network down"
        )
        self.assertEqual(self.repository.count_rows("observations"), 1)
        self.assertEqual(reasoner.requests, [])

        self.clock.advance(4)
        self.controller.advance_time(session.id)
        focused = self.controller.record_snapshot(
            session.id,
            sequence=3,
            captured_at=self.clock.now(),
            image="focused.jpg",
        )
        focused_observation = self.controller.persist_observation(
            focused.id, OBSERVATION
        )
        self.controller.evaluate_observation(focused_observation)
        self.assertEqual(
            [item.sequence for item in reasoner.requests[0].recent_observations],
            [3],
        )

    def test_mismatched_reasoning_proposal_is_rejected(self):
        class BadReasoner:
            def evaluate(self, request):
                return ReasoningProposal(
                    session_id="wrong",
                    session_version=request.session_version,
                    observation_id=request.observation.id,
                    kind="continue_observing",
                )

        self.controller.reasoning_port = BadReasoner()
        _, _, session = self.start_session()
        snapshot = self.controller.record_snapshot(
            session.id,
            sequence=1,
            captured_at=self.clock.now(),
            image="one.jpg",
        )
        observation = self.controller.persist_observation(snapshot.id, OBSERVATION)
        with self.assertRaises(InvalidReasoningProposal):
            self.controller.evaluate_observation(observation)

    def test_reasoning_rejects_unknown_kind_and_unlisted_profile_only_deviation(self):
        deviation = Deviation("Sustained attention to a phone")
        profile = DeviationProfile(
            deviations=(deviation,),
            created_at=self.clock.now(),
            updated_at=self.clock.now(),
        )
        goal = self.controller.create_goal("Study")
        contract = self.controller.confirm_contract(
            self.controller.prepare_contract(
                goal.id,
                target_focus_seconds=60,
                deviation_profile=profile,
            )
        )
        session = self.controller.start_monitoring(contract)
        observation = self.persist_observation(session)

        class ProposalReasoner:
            def __init__(inner_self, kind, payload):
                inner_self.kind = kind
                inner_self.payload = payload

            def evaluate(inner_self, request):
                return ReasoningProposal(
                    session_id=request.session_id,
                    session_version=request.session_version,
                    observation_id=request.observation.id,
                    kind=inner_self.kind,
                    payload=inner_self.payload,
                )

        self.controller.reasoning_port = ProposalReasoner("invented_action", {})
        with self.assertRaisesRegex(InvalidReasoningProposal, "unsupported"):
            self.controller.evaluate_observation(observation)

        self.controller.reasoning_port = ProposalReasoner(
            "begin_recovery_check_in",
            self.intervention_payload(observation, "unlisted"),
        )
        with self.assertRaisesRegex(InvalidReasoningProposal, "Profile Only"):
            self.controller.evaluate_observation(observation)
        self.assertEqual(
            self.controller.get_session(session.id).state, SessionState.FOCUSING
        )

    def test_valid_listed_intervention_is_admitted_atomically(self):
        deviation = Deviation("Sustained attention to a phone")
        profile = DeviationProfile(
            deviations=(deviation,),
            created_at=self.clock.now(),
            updated_at=self.clock.now(),
        )
        goal = self.controller.create_goal("Study")
        contract = self.controller.confirm_contract(
            self.controller.prepare_contract(
                goal.id,
                target_focus_seconds=60,
                deviation_profile=profile,
            )
        )
        session = self.controller.start_monitoring(contract)
        observation = self.persist_observation(session)
        payload = self.intervention_payload(observation, deviation.id)

        class InterventionReasoner:
            def evaluate(_self, request):
                return ReasoningProposal(
                    session_id=request.session_id,
                    session_version=request.session_version,
                    observation_id=request.observation.id,
                    kind="begin_recovery_check_in",
                    payload=payload,
                )

        self.controller.reasoning_port = InterventionReasoner()
        valid_payload = payload
        payload = {**payload, "key_observation_ids": ["missing-observation"]}
        with self.assertRaisesRegex(InvalidReasoningProposal, "does not exist"):
            self.controller.evaluate_observation(observation)

        payload = valid_payload
        proposal = self.controller.evaluate_observation(observation)

        self.assertEqual(proposal.kind, "begin_recovery_check_in")
        stored = self.controller.get_session(session.id)
        self.assertEqual(stored.state, SessionState.RECOVERY_CHECK_IN)
        self.assertEqual(stored.version, session.version + 1)

    def test_exploratory_mode_admits_grounded_unlisted_intervention(self):
        goal = self.controller.create_goal("Study")
        contract = self.controller.confirm_contract(
            self.controller.prepare_contract(
                goal.id,
                target_focus_seconds=60,
                reasoning_mode=ReasoningMode.EXPLORATORY,
            )
        )
        session = self.controller.start_monitoring(contract)
        observation = self.persist_observation(session)
        payload = self.intervention_payload(observation, "unlisted")

        class InterventionReasoner:
            def evaluate(_self, request):
                return ReasoningProposal(
                    session_id=request.session_id,
                    session_version=request.session_version,
                    observation_id=request.observation.id,
                    kind="begin_recovery_check_in",
                    payload=payload,
                )

        self.controller.reasoning_port = InterventionReasoner()
        self.controller.evaluate_observation(observation)

        self.assertEqual(
            self.controller.get_session(session.id).state,
            SessionState.RECOVERY_CHECK_IN,
        )

    def test_reasoning_recording_holds_controller_lock_against_state_changes(self):
        entered_append = threading.Event()
        release_append = threading.Event()
        end_finished = threading.Event()
        reasoner = RecordingReasoner()
        self.controller.reasoning_port = reasoner
        _, _, session = self.start_session()
        observation = self.persist_observation(session)
        original_append = self.repository.append_session_event

        def blocking_append(*args, **kwargs):
            entered_append.set()
            self.assertTrue(release_append.wait(timeout=2))
            return original_append(*args, **kwargs)

        self.repository.append_session_event = blocking_append
        evaluation = threading.Thread(
            target=self.controller.evaluate_observation, args=(observation,)
        )

        def end_session():
            self.controller.end_early(session.id)
            end_finished.set()

        ending = threading.Thread(target=end_session)
        evaluation.start()
        self.assertTrue(entered_append.wait(timeout=2))
        ending.start()
        self.assertFalse(end_finished.wait(timeout=0.05))
        release_append.set()
        evaluation.join(timeout=2)
        ending.join(timeout=2)

        self.assertFalse(evaluation.is_alive())
        self.assertFalse(ending.is_alive())
        self.assertTrue(end_finished.is_set())
        self.assertEqual(
            self.controller.get_session(session.id).state, SessionState.ENDED_EARLY
        )

    def test_reasoning_rejects_unknown_actions_and_malformed_payloads(self):
        class InvalidReasoner:
            def __init__(inner_self):
                inner_self.kind = "invented_action"

            def evaluate(inner_self, request):
                return ReasoningProposal(
                    session_id=request.session_id,
                    session_version=request.session_version,
                    observation_id=request.observation.id,
                    kind=inner_self.kind,
                )

        reasoner = InvalidReasoner()
        self.controller.reasoning_port = reasoner
        _, _, session = self.start_session()
        snapshot = self.controller.record_snapshot(
            session.id,
            sequence=1,
            captured_at=self.clock.now(),
            image="one.jpg",
        )
        observation = self.controller.persist_observation(snapshot.id, OBSERVATION)

        with self.assertRaisesRegex(InvalidReasoningProposal, "unsupported"):
            self.controller.evaluate_observation(observation)

        reasoner.kind = "begin_recovery_check_in"
        with self.assertRaisesRegex(InvalidReasoningProposal, "missing fields"):
            self.controller.evaluate_observation(observation)

    def test_repository_handles_snapshot_writes_from_multiple_threads(self):
        _, _, session = self.start_session()

        def write(sequence):
            return self.controller.record_snapshot(
                session.id,
                sequence=sequence,
                captured_at=self.clock.now(),
                image=f"{sequence}.jpg",
            )

        with ThreadPoolExecutor(max_workers=4) as executor:
            snapshots = list(executor.map(write, range(1, 21)))

        self.assertEqual(len({item.id for item in snapshots}), 20)
        self.assertEqual(self.repository.count_rows("snapshots"), 20)

    def test_canceled_preflight_creates_no_controller_session_or_directory(self):
        _, contract = self.make_contract()
        camera = FakeCamera()
        args = SimpleNamespace(output_dir=self.root, camera=0)
        with (
            patch("capture.open_camera", return_value=camera),
            patch("capture.run_camera_preflight", return_value=None),
            patch("sys.stdout", new=io.StringIO()),
        ):
            started = capture.run_capture_session(
                Mock(), args, controller=self.controller, contract=contract
            )

        self.assertFalse(started)
        self.assertTrue(camera.released)
        self.assertEqual(self.repository.count_rows("focus_sessions"), 0)
        self.assertEqual(list(self.root.glob("session-*")), [])

    def test_slow_reasoning_keeps_only_the_newest_pending_snapshot(self):
        first_started = threading.Event()
        release_first = threading.Event()

        class SlowReasoner(RecordingReasoner):
            def evaluate(inner_self, request):
                inner_self.requests.append(request)
                if len(inner_self.requests) == 1:
                    first_started.set()
                    self.assertTrue(release_first.wait(timeout=2))
                return ReasoningProposal(
                    session_id=request.session_id,
                    session_version=request.session_version,
                    observation_id=request.observation.id,
                    kind="continue_observing",
                )

        reasoner = SlowReasoner()
        self.controller.reasoning_port = reasoner
        _, _, session = self.start_session()
        client = Mock()
        client.responses.create.return_value = SimpleNamespace(
            output_text=json.dumps(OBSERVATION)
        )
        buffer = capture.LatestSnapshotBuffer()
        stats = capture.UploadStats()
        observation_log = capture.JsonlLog(self.root / "observations.jsonl")
        event_log = capture.JsonlLog(self.root / "capture_events.jsonl")

        def controlled_capture(sequence):
            stored = self.controller.record_snapshot(
                session.id,
                sequence=sequence,
                captured_at=self.clock.now(),
                image=f"{sequence}.jpg",
            )
            return capture.Snapshot(
                sequence=sequence,
                captured_at=stored.captured_at,
                image_name=stored.image,
                jpeg=b"jpeg",
                controller_snapshot_id=stored.id,
                session_id=stored.session_id,
                session_version=stored.session_version,
                captured_state=stored.captured_state,
            )

        buffer.submit(controlled_capture(1))
        worker = threading.Thread(
            target=capture.upload_snapshots,
            kwargs={
                "perception": OpenAIPerceptionAdapter(
                    client, model="test-model", detail="low"
                ),
                "buffer": buffer,
                "observation_log": observation_log,
                "event_log": event_log,
                "stats": stats,
                "controller": self.controller,
            },
        )
        with patch("sys.stdout", new=io.StringIO()), patch(
            "sys.stderr", new=io.StringIO()
        ):
            worker.start()
            self.assertTrue(first_started.wait(timeout=2))
            buffer.submit(controlled_capture(2))
            replaced = buffer.submit(controlled_capture(3))
            self.controller.finalize_snapshot(
                replaced.controller_snapshot_id, SnapshotStatus.SUPERSEDED
            )
            event_log.append(capture.make_capture_event(replaced, "superseded"))
            buffer.close()
            release_first.set()
            worker.join(timeout=2)

        records = [
            json.loads(line)
            for line in observation_log.path.read_text(encoding="utf-8").splitlines()
        ]
        self.assertFalse(worker.is_alive())
        self.assertEqual([item["sequence"] for item in records], [1, 3])
        self.assertTrue(all(item["session_id"] == session.id for item in records))
        self.assertTrue(all(item["reasoning_eligible"] for item in records))
        self.assertTrue(all(item["snapshot_id"] for item in records))
        self.assertTrue(all(item["observation_id"] for item in records))
        self.assertEqual([request.observation.sequence for request in reasoner.requests], [1, 3])
        self.assertEqual(self.repository.count_rows("observations"), 2)


if __name__ == "__main__":
    unittest.main()
