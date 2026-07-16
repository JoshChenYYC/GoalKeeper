# AI Accountability

This context describes a local accountability application that observes a user during bounded focus sessions and decides when a supportive intervention is warranted.

## Language

**Goal**:
A persistent, user-defined intention pursued through one or more Focus Sessions.
_Avoid_: Task, contract, session goal

**Focus Session**:
One bounded, monitored attempt to work toward a Goal.
_Avoid_: Goal, timer, monitoring job

**Session Contract**:
The immutable commitment governing one Focus Session, including its goal snapshot, duration, scheduled breaks, deviation snapshot, reasoning mode, and sensitivity.
_Avoid_: Goal, preferences, live settings

**Deviation Profile**:
A reusable user-owned list of behavior descriptions that may justify intervention. Each Session Contract retains an immutable snapshot of the active profile.
_Avoid_: Constraint list, Focus Agreement

**Deviation**:
A user-defined behavior pattern regarded as potentially inconsistent with focused work.
_Avoid_: Violation, infraction, distraction label

**Observation**:
A neutral structured description of facts and visible cues derived from one room snapshot.
_Avoid_: Decision, deviation, intervention

**Evidence Episode**:
A temporally related sequence of observations that may support one possible Deviation.
_Avoid_: Violation, distraction period

**Intervention**:
The decision to provisionally pause focus time and begin a Recovery Check-in because observed behavior may warrant it.
_Avoid_: Punishment, alert, violation

**Recovery Check-in**:
A bounded voice conversation that explains an Intervention and helps the user clarify, recommit, request limited coaching, or end the Focus Session.
_Avoid_: Reminder, command prompt, interrogation

**Behavior Clarification**:
A natural-language response indicating that disputed behavior was consistent with the Goal.
_Avoid_: False-alarm keyword, override

**Deviation Override**:
An explicit session-scoped instruction that a Deviation does not apply to the current Evidence Episode or, when clearly confirmed, the remainder of the Focus Session.
_Avoid_: Contract edit, global exception

**Recovery Window**:
A bounded observation period after recommitment during which the system watches whether the cited Deviation clears without immediately repeating a Recovery Check-in.
_Avoid_: Break, pause, cooldown

**Scheduled Break**:
A fixed, precommitted interruption that begins after a specified amount of active focus time and pauses the Focus Timer for a specified duration.
_Avoid_: Ad hoc break, pause monitoring

**Focus Timer**:
The measure of committed active focus duration, excluding Scheduled Breaks and confirmed disputed intervals.
_Avoid_: Wall-clock session length

**Fulfilled**:
A final Focus Session outcome reached when its target focus duration is completed or the user explicitly declares the associated Goal complete.
_Avoid_: Successful observation, task verified

**Ended Early**:
A final Focus Session outcome reached without fulfillment, together with a recorded reason.
_Avoid_: Failed goal, abandoned user

**Session Review**:
An optional lightweight reflection completed after a Focus Session ends.
_Avoid_: Coaching conversation, performance grade
