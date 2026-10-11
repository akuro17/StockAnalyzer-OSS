"""Machine-readable value domains of the trainer hyperparameters (``hyperparameter_domains.json``) and the validator that reads them.

The wizard (``TrainingHyperparameterCatalog`` in the C# core) refuses a hyperparameter value only when it lies outside the MATHEMATICAL domain of its key and
stays permissive otherwise. This file pair is the shared definition of those domains: the JSON holds them as data, ``validate`` is the Python reading of
the rule, and the C# catalog tests read the same JSON and compare every domain and every probe. A domain that changes on one side only fails on the other.

* A domain is ``kind`` (``integer`` | ``real``) and optional ``minimum`` / ``maximum`` with ``minimum_inclusive`` / ``maximum_inclusive``.
* ``probes`` are ``[value, problem]`` pairs (text -> the problem name the validator must give); both languages evaluate all of them.
* ``trainer_checks`` quote the rule text a trainer source still carries, and ``bound_constants`` name the module constant that holds a bound; a rename or a
  changed value in the trainer fails the selfcheck (and the C# test) here.

No trainer imports this module: it changes no trainer behavior. Problem names equal ``HyperparameterProblem`` of the C# core.
"""

from __future__ import annotations

import json
import math
import re
import sys
from pathlib import Path
from typing import Dict, List

HERE: Path = Path(__file__).resolve().parent
DOMAINS_FILE: Path = HERE / "hyperparameter_domains.json"
SCHEMA_VERSION: int = 1

KIND_INTEGER: str = "integer"
KIND_REAL: str = "real"

PROBLEM_NONE: str = "None"
PROBLEM_NOT_INTEGER: str = "NotInteger"
PROBLEM_NOT_NUMBER: str = "NotNumber"
PROBLEM_NOT_FINITE: str = "NotFinite"
PROBLEM_BELOW_MINIMUM: str = "BelowMinimum"
PROBLEM_ABOVE_MAXIMUM: str = "AboveMaximum"

# The C# side parses an integer as a 64-bit number; a text beyond that range is below / above the domain whatever its bounds.
INT64_MIN: int = -(2 ** 63)
INT64_MAX: int = 2 ** 63 - 1

# Syntax of the C# ``NumberStyles.Float`` in the invariant culture; the words are its infinity / NaN symbols (case-insensitive).
_WHOLE_NUMBER = re.compile(r"[+-]?[0-9]+", re.ASCII)
_REAL_NUMBER = re.compile(r"[+-]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?", re.ASCII)
_NON_FINITE_WORD = re.compile(r"[+-]?(infinity|nan)", re.ASCII | re.IGNORECASE)


def load_domains(path: Path = DOMAINS_FILE) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def validate(domain: dict, value: str | None) -> str:
    """The problem name of ``value`` for ``domain`` (``None`` when acceptable), by the rule of ``TrainingHyperparameterCatalog.Validate``."""
    text = (value if value is not None else "").strip()
    if domain["kind"] == KIND_INTEGER:
        if not _WHOLE_NUMBER.fullmatch(text):
            return PROBLEM_NOT_INTEGER
        whole = int(text)
        if whole < INT64_MIN:
            return PROBLEM_BELOW_MINIMUM
        if whole > INT64_MAX:
            return PROBLEM_ABOVE_MAXIMUM
        number = float(whole)
    else:
        if _NON_FINITE_WORD.fullmatch(text):
            return PROBLEM_NOT_FINITE
        if not _REAL_NUMBER.fullmatch(text):
            return PROBLEM_NOT_NUMBER
        number = float(text)
        if not math.isfinite(number):
            return PROBLEM_NOT_FINITE
    minimum, maximum = domain.get("minimum"), domain.get("maximum")
    if minimum is not None and (number < minimum if domain["minimum_inclusive"] else number <= minimum):
        return PROBLEM_BELOW_MINIMUM
    if maximum is not None and (number > maximum if domain["maximum_inclusive"] else number >= maximum):
        return PROBLEM_ABOVE_MAXIMUM
    return PROBLEM_NONE


def _module_constant(file: str, name: str) -> float:
    match = re.search(rf"^{name}\s*:\s*\w+\s*=\s*(?P<v>[-+0-9.eE]+)\s*(#.*)?$", (HERE / file).read_text(encoding="utf-8"), re.MULTILINE)
    if not match:
        raise AssertionError(f"{file} has no numeric literal {name}")
    return float(match.group("v"))


def check_document(document: dict) -> List[str]:
    """The reasons the document disagrees with itself, with the validator or with the trainer sources (empty when it holds)."""
    problems: List[str] = []
    if document.get("schema_version") != SCHEMA_VERSION:
        problems.append(f"schema_version must be {SCHEMA_VERSION}")
    domains: Dict[str, dict] = document.get("domains", {})
    if not domains:
        problems.append("no domains")
    for key, domain in domains.items():
        if domain.get("kind") not in (KIND_INTEGER, KIND_REAL):
            problems.append(f"{key}: unknown kind")
            continue
        for side in ("minimum", "maximum"):
            if side not in domain or f"{side}_inclusive" not in domain:
                problems.append(f"{key}: {side} and {side}_inclusive are both required (null = no bound)")
        if not domain.get("probes"):
            problems.append(f"{key}: no probes")
        for value, expected in domain.get("probes", []):
            got = validate(domain, value)
            if got != expected:
                problems.append(f"{key}: probe {value!r} expected {expected} got {got}")
        for check in domain.get("trainer_checks", []):
            if check["text"] not in (HERE / check["file"]).read_text(encoding="utf-8"):
                problems.append(f"{key}: {check['file']} no longer carries {check['text']!r}")
        for constant in domain.get("bound_constants", []):
            if _module_constant(constant["file"], constant["name"]) != domain[constant["bound"]]:
                problems.append(f"{key}: {constant['file']}:{constant['name']} differs from the {constant['bound']}")
    return problems


def _run_selfcheck() -> None:
    document = load_domains()
    problems = check_document(document)
    assert not problems, problems
    domains = document["domains"]
    # known answers, independent of the probes in the file
    known = [("epochs", "0", PROBLEM_BELOW_MINIMUM), ("epochs", "1", PROBLEM_NONE), ("epochs", " 15 ", PROBLEM_NONE), ("epochs", "1.5", PROBLEM_NOT_INTEGER),
             ("epochs", "", PROBLEM_NOT_INTEGER), ("epochs", "99999999999999999999", PROBLEM_ABOVE_MAXIMUM),
             ("seed", "-99999999999999999999", PROBLEM_BELOW_MINIMUM), ("heads", "17", PROBLEM_ABOVE_MAXIMUM), ("heads", "16", PROBLEM_NONE),
             ("dropout", "1", PROBLEM_ABOVE_MAXIMUM), ("dropout", "0", PROBLEM_NONE), ("dropout", "0.9999", PROBLEM_NONE),
             ("lr", "0", PROBLEM_BELOW_MINIMUM), ("lr", "1e-3", PROBLEM_NONE), ("lr", "abc", PROBLEM_NOT_NUMBER), ("lr", "NaN", PROBLEM_NOT_FINITE),
             ("lr", "1e999", PROBLEM_NOT_FINITE), ("lr", "Infinity", PROBLEM_NOT_FINITE), ("lr", ".5", PROBLEM_NONE), ("lr", "5.", PROBLEM_NONE),
             ("subsample", "1", PROBLEM_NONE), ("subsample", "1.0001", PROBLEM_ABOVE_MAXIMUM), ("max-depth", "-1", PROBLEM_NONE),
             ("max-depth", "-2", PROBLEM_BELOW_MINIMUM), ("num-leaves", "1", PROBLEM_BELOW_MINIMUM), ("threshold", "0", PROBLEM_NONE),
             ("threshold", "-0.001", PROBLEM_BELOW_MINIMUM)]
    for key, value, expected in known:
        assert validate(domains[key], value) == expected, (key, value, expected, validate(domains[key], value))
    assert validate(domains["epochs"], None) == PROBLEM_NOT_INTEGER, "a missing value is not a number"
    # the shipped document is not trivially satisfiable: a probe that expected the wrong problem is reported
    broken = json.loads(json.dumps(document))
    broken["domains"]["epochs"]["probes"].append(["0", PROBLEM_NONE])
    assert any("epochs: probe '0'" in p for p in check_document(broken)), "a wrong probe must be reported"
    renamed = json.loads(json.dumps(document))
    renamed["domains"]["heads"]["trainer_checks"].append({"file": "attention_export.py", "text": "no such rule text anywhere"})
    assert any("no longer carries" in p for p in check_document(renamed)), "a vanished trainer rule must be reported"
    drifted = json.loads(json.dumps(document))
    drifted["domains"]["heads"]["maximum"] = 17
    assert any("MAX_HEADS" in p for p in check_document(drifted)), "a bound that differs from its trainer constant must be reported"
    print(f"hyperparameter_domains.py selfcheck: OK ({len(domains)} domains, {sum(len(d['probes']) for d in domains.values())} probes)")


if __name__ == "__main__":
    if "--selfcheck" in sys.argv[1:]:
        _run_selfcheck()
    else:
        raise SystemExit("hyperparameter_domains.py has no command line; use --selfcheck")
