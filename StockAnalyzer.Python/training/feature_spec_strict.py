"""Strict composed FeatureSpec wire validation for new training diagnostics.

The older dataset structural guard remains unchanged. JSON object member order is
kept because the C# PredictionModelMetadata comparison preserves that order.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

import dataset as ds


def _unique_object(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"feature_spec: duplicate JSON member {key!r}")
        result[key] = value
    return result


def _read_indicator_names(source: Path) -> set[str]:
    """Parse the canonical C# enum, including a final member without a comma."""
    contents = source.read_text(encoding="utf-8")
    declaration = re.search(r"\bpublic\s+enum\s+IndicatorType\s*\{([^}]*)\}", contents, re.DOTALL)
    if declaration is None:
        raise ValueError("feature_spec: IndicatorType enum declaration is unreadable")
    body = declaration.group(1)
    # Preserve quoted attribute text while removing comments (including commented-out members).
    body = re.sub(
        r'("(?:\\.|[^"\\])*")|//[^\n]*|/\*.*?\*/',
        lambda match: match.group(1) or "",
        body,
        flags=re.DOTALL,
    )
    # Attributes can contain commas, so remove them before separating enum members.
    body = re.sub(r"\[[^\]]*\]", "", body)
    entries = [entry.strip() for entry in body.split(",") if entry.strip()]
    names = set()
    for entry in entries:
        member = re.fullmatch(r"([A-Za-z_][A-Za-z_0-9]*)(?:\s*=\s*\d+)?", entry)
        if member is None:
            raise ValueError(f"feature_spec: unreadable IndicatorType member {entry!r}")
        names.add(member.group(1))
    if not names or len(names) != len(entries):
        raise ValueError("feature_spec: IndicatorType enum has no readable unique entries")
    return names


def _indicator_names() -> set[str]:
    """Read the distributed copy of the C# enum SSoT, or the development source."""
    packaged = Path(__file__).resolve().parent / "contracts/IndicatorType.cs"
    source = packaged if packaged.is_file() else (
        Path(__file__).resolve().parents[2] / "StockAnalyzer.Core/Models/IndicatorType.cs"
    )
    return _read_indicator_names(source)


def _without_json_whitespace(raw: str) -> str:
    """Remove whitespace outside JSON strings without changing any token spelling."""
    compact = []
    in_string = False
    escaped = False
    for char in raw:
        if in_string:
            compact.append(char)
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                in_string = False
        elif char == '"':
            compact.append(char)
            in_string = True
        elif char not in " \t\r\n":
            compact.append(char)
    return "".join(compact)


def parse_feature_spec(raw: str) -> tuple[dict, str]:
    """Return a validated spec and JSON with only insignificant whitespace removed."""
    try:
        spec = json.loads(raw, object_pairs_hook=_unique_object)
    except (TypeError, json.JSONDecodeError) as exc:
        raise ValueError(f"feature_spec: invalid JSON: {exc}") from exc
    if not isinstance(spec, dict):
        raise ValueError("feature_spec: root must be an object")
    version = spec.get("schema_version", 1)
    if type(version) is not int or version <= 0:
        raise ValueError("feature_spec: schema_version must be a positive integer")
    channels = spec.get("channels")
    if not isinstance(channels, list) or not channels:
        raise ValueError("feature_spec: channels must be a non-empty array")
    if len(channels) > ds.MAX_COMPOSED_CHANNELS:
        raise ValueError(f"feature_spec: channel count exceeds {ds.MAX_COMPOSED_CHANNELS}")

    indicator_names = None
    for index, channel in enumerate(channels):
        prefix = f"feature_spec: channels[{index}]"
        if not isinstance(channel, dict):
            raise ValueError(f"{prefix} must be an object")
        kind = channel.get("kind")
        if kind not in ("price", "indicator"):
            raise ValueError(f"{prefix}: unknown kind {kind!r}")
        normalization = channel.get("normalization", "none")
        if normalization not in ds.COMPOSED_NORMALIZATIONS:
            raise ValueError(f"{prefix}: unknown normalization {normalization!r}")
        params = channel.get("params", {})
        if params is not None and (not isinstance(params, dict) or
                                   any(not isinstance(k, str) or not isinstance(v, str)
                                       for k, v in params.items())):
            raise ValueError(f"{prefix}: params must map strings to strings")
        if kind == "price":
            if channel.get("price") not in ds.COMPOSED_PRICE_TYPES or channel.get("indicator") is not None:
                raise ValueError(f"{prefix}: invalid price payload")
        else:
            if indicator_names is None:
                indicator_names = _indicator_names()
            if channel.get("indicator") not in indicator_names or channel.get("price") is not None:
                raise ValueError(f"{prefix}: invalid indicator payload")
            if params is None:
                raise ValueError(f"{prefix}: indicator params must not be null")
    return spec, _without_json_whitespace(raw)
