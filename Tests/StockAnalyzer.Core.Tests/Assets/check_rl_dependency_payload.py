"""Real isolated distribution payload drift; installed managed files are never edited."""
import importlib.metadata
import json
from pathlib import Path
import sys
import tempfile
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "StockAnalyzer.Python" / "training"))
import rl_dependencies as dependencies

with tempfile.TemporaryDirectory(prefix="sa_m01_payload_", dir=Path(__file__).parent) as temporary:
    root = Path(temporary)
    metadata = root / "fixture-1.dist-info"
    metadata.mkdir()
    (metadata / "METADATA").write_text("Name: fixture\nVersion: 1\n", encoding="utf-8")
    (metadata / "RECORD").write_text("module.py,,\nfixture-1.dist-info/METADATA,,\n", encoding="utf-8")
    module = root / "module.py"
    module.write_text("value = 1\n", encoding="utf-8")
    distribution = importlib.metadata.PathDistribution(metadata)
    with patch.object(importlib.metadata, "distributions", return_value=[distribution]), \
         patch.object(dependencies, "execution_roots", return_value=[root], create=True):
        before = dependencies.inspect()
        snapshot = root / "captured.json"
        snapshot.write_text(json.dumps(before), encoding="utf-8")
        # Keep the snapshot outside the execution tree used by the fixture.
        captured = snapshot.read_bytes()
        snapshot.unlink()
        module.write_text("value = 2\n", encoding="utf-8")
        after = dependencies.inspect()
        assert before != after, "Same metadata concealed changed executable payload"
        outside = root.parent / (root.name + "-snapshot.json")
        try:
            outside.write_bytes(captured)
            try:
                dependencies.validate(outside)
            except RuntimeError:
                pass
            else:
                raise AssertionError("Changed module accepted before worker execution")
        finally:
            outside.unlink(missing_ok=True)
print("M01 dependency payload drift: PASS")
