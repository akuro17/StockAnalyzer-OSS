"""Regenerate ``Tests/CpcvParity.json``: the CPCV counting / enumeration vectors the C# contract must reproduce.

The vectors are produced by the production Python code (``dataset.cpcv_combination_count`` /
``dataset.cpcv_combination``), so ``TrainingCpcvContract`` is verified against the trainer that actually enumerates the
combinations. Rejected shapes are listed too: both sides must refuse exactly the same requests.

Run with the training interpreter:
    StockAnalyzer.Python/.venv/Scripts/python.exe Tests/StockAnalyzer.Core.Tests/Assets/generate_cpcv_parity.py
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parents[2] / "StockAnalyzer.Python" / "training"))
import dataset as ds  # noqa: E402


def main() -> None:
    accepted, rejected, enumerations = [], [], []
    for groups in range(1, ds.CPCV_MAX_GROUPS + 3):
        for test_groups in range(-1, groups + 2):
            try:
                accepted.append({"groups": groups, "test_groups": test_groups,
                                 "count": ds.cpcv_combination_count(groups, test_groups)})
            except ValueError:
                rejected.append({"groups": groups, "test_groups": test_groups})
    for groups, test_groups in ((6, 2), (6, 3), (5, 1), (7, 2), (4, 3), (10, 2)):
        total = ds.cpcv_combination_count(groups, test_groups)
        enumerations.append({"groups": groups, "test_groups": test_groups,
                             "combinations": [list(ds.cpcv_combination(groups, test_groups, i)) for i in range(total)]})
    parity = {
        "default_groups": ds.CPCV_DEFAULT_GROUPS, "default_test_groups": ds.CPCV_DEFAULT_TEST_GROUPS,
        "max_groups": ds.CPCV_MAX_GROUPS, "max_combinations": ds.CPCV_MAX_COMBINATIONS,
        "accepted": accepted, "rejected": rejected, "enumerations": enumerations,
    }
    target = HERE.parents[1] / "CpcvParity.json"
    target.write_bytes((json.dumps(parity, indent=1, allow_nan=False) + chr(10)).encode("utf-8"))
    print("wrote", target.name, len(accepted), "accepted", len(rejected), "rejected", len(enumerations), "enumerations")


if __name__ == "__main__":
    main()
