"""OpenAI adapter for the provider-neutral Perception port."""

from __future__ import annotations

import base64
import json
from typing import Any, Mapping


PERCEPTION_PROMPT = """\
You are the neutral perception component of an accountability application.
Describe only visible facts and cues in this single room snapshot. You do not
know the user's goal, deviation profile, sensitivity, session history, or
intervention state. Do not judge whether behavior is appropriate, distracting,
or goal-consistent, and do not recommend an action.

Report image limitations explicitly. Count every visible person; use null when
image quality or occlusion makes the count indeterminate. Use direct, partial,
inferred, or unavailable to describe visual support rather than a probability.
Keep behavior labels neutral and concise. An empty observations list is valid
when no supported behavioral cue is visible.
"""

OBSERVATION_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "image_quality": {
            "type": "object",
            "properties": {
                "value": {
                    "type": "string",
                    "enum": ["adequate", "limited", "unusable"],
                    "description": "Whether the scene is usable for neutral monitoring.",
                },
                "limitations": {
                    "type": "array",
                    "items": {"type": "string"},
                    "description": "Visible quality, framing, lighting, or occlusion limits.",
                },
            },
            "required": ["value", "limitations"],
            "additionalProperties": False,
        },
        "people_count": {
            "anyOf": [{"type": "integer", "minimum": 0}, {"type": "null"}],
            "description": "Number of visible people, or null when indeterminate.",
        },
        "objects": {
            "type": "array",
            "items": {"type": "string"},
            "description": "Neutral names of notable visible objects.",
        },
        "observations": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "subject": {
                        "type": "string",
                        "description": "Neutral subject reference, such as visible_person.",
                    },
                    "behavior": {
                        "type": "string",
                        "description": "Neutral visible behavior label.",
                    },
                    "support": {
                        "type": "string",
                        "enum": ["direct", "partial", "inferred", "unavailable"],
                    },
                    "visual_basis": {
                        "type": "string",
                        "description": "Visible evidence supporting the behavior label.",
                    },
                    "limitations": {"type": "array", "items": {"type": "string"}},
                },
                "required": [
                    "subject",
                    "behavior",
                    "support",
                    "visual_basis",
                    "limitations",
                ],
                "additionalProperties": False,
            },
        },
    },
    "required": ["image_quality", "people_count", "objects", "observations"],
    "additionalProperties": False,
}


def jpeg_data_url(jpeg: bytes) -> str:
    encoded = base64.b64encode(jpeg).decode("ascii")
    return f"data:image/jpeg;base64,{encoded}"


class OpenAIPerceptionAdapter:
    def __init__(self, client: Any, *, model: str, detail: str) -> None:
        self._client = client
        self.model = model
        self.detail = detail

    def observe(self, jpeg: bytes) -> Mapping[str, Any]:
        response = self._client.responses.create(
            model=self.model,
            input=[
                {"role": "system", "content": PERCEPTION_PROMPT},
                {
                    "role": "user",
                    "content": [
                        {
                            "type": "input_text",
                            "text": "Return the structured observation for this snapshot.",
                        },
                        {
                            "type": "input_image",
                            "image_url": jpeg_data_url(jpeg),
                            "detail": self.detail,
                        },
                    ],
                },
            ],
            text={
                "format": {
                    "type": "json_schema",
                    "name": "room_observation",
                    "schema": OBSERVATION_SCHEMA,
                    "strict": True,
                }
            },
        )
        if not response.output_text:
            raise RuntimeError("the perception API returned no text output")
        try:
            value = json.loads(response.output_text)
        except json.JSONDecodeError as error:
            raise RuntimeError("the perception API returned invalid JSON") from error
        if not isinstance(value, dict):
            raise RuntimeError("the perception API returned a non-object observation")
        return value
