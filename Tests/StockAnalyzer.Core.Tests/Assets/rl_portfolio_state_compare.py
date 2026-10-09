"""Compares the native actor weights of two portfolio runs (opt-in xUnit fine-tune smoke test, managed venv).

The exported actor is a torch Sequential(shared encoder, empty policy trunk, slot logit head), so its state dict keys start with "0." (encoder),
"1." (trunk, no keys) or "2." (head). Prints one JSON line: {"keys_equal": bool, "encoder_equal": bool, "head_equal": bool}; "equal" means
bit-for-bit equal tensors. Usage: python rl_portfolio_state_compare.py --base <policy.pt> --tuned <policy.pt>
"""
import argparse
import json


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", required=True)
    parser.add_argument("--tuned", required=True)
    args = parser.parse_args()
    import torch
    base = torch.load(args.base, map_location="cpu", weights_only=True)
    tuned = torch.load(args.tuned, map_location="cpu", weights_only=True)
    keys_equal = list(base.keys()) == list(tuned.keys())

    def same(prefix):
        keys = [key for key in base if key.startswith(prefix)]
        return keys_equal and bool(keys) and all(torch.equal(base[key], tuned[key]) for key in keys)

    print(json.dumps(dict(keys_equal=keys_equal, encoder_equal=same("0."), head_equal=same("2.")), allow_nan=False), flush=True)


if __name__ == "__main__":
    main()
