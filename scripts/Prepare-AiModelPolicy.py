"""Prepare a reviewable production settings file; never modify the running deployment."""

import argparse
import copy
import json
import os
from pathlib import Path

OWNED_MODELS = (
    "ai-model", "qwen3.7-flash", "qwen3.8-flash", "qwen-mt-flash",
    "qwen3-vl-flash", "qwen-flash", "qwen-plus", "deepseek-v4-flash",
)


def prepare(settings, overlay, active_revision, revision):
    if revision <= max(active_revision, settings.get("Policy", {}).get("Revision", 0)):
        raise ValueError("The new revision must exceed both the active and configured revisions.")
    if revision < 15:
        raise ValueError("Revision 14 belongs to the historical CPU recovery script; use 15 or higher.")
    result = copy.deepcopy(settings)
    policy = result.setdefault("Policy", {})
    providers = result.setdefault("Providers", {})
    # .NET configuration merges dictionary children across layers. Merely adding
    # the new model leaves obsolete resources and native translation flags alive.
    for section in ("Resources", "Pricing", "ModelPricing"):
        values = policy.setdefault(section, {})
        for model in OWNED_MODELS:
            values.pop(model, None)
        values.update(copy.deepcopy(overlay["Policy"][section]))
    for key, value in overlay["Policy"].items():
        if key not in ("Resources", "Pricing", "ModelPricing", "Revision"):
            policy[key] = copy.deepcopy(value)
    policy["Revision"] = revision
    for section in ("Models", "PublicModels"):
        values = providers.setdefault(section, {})
        for model in OWNED_MODELS:
            values.pop(model, None)
        values.update(copy.deepcopy(overlay["Providers"][section]))
    providers.setdefault("Translation", {}).update(copy.deepcopy(overlay["Providers"]["Translation"]))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--settings", type=Path, required=True, help="Existing mounted production settings")
    parser.add_argument("--output", type=Path, required=True, help="New file, reviewed before installation")
    parser.add_argument("--active-revision", type=int, required=True, help="Verified database active revision")
    parser.add_argument("--revision", type=int, default=15)
    parser.add_argument("--overlay", type=Path, default=Path(__file__).resolve().parents[1] /
                        "deployment/policy/policy-revision-15.json")
    args = parser.parse_args()
    settings = json.loads(args.settings.read_text(encoding="utf-8-sig"))
    overlay = json.loads(args.overlay.read_text(encoding="utf-8-sig"))
    result = prepare(settings, overlay, args.active_revision, args.revision)
    # Exclusive creation also prevents accidentally replacing the input or a
    # previous reviewed candidate. Contents may contain secrets: do not print.
    descriptor = os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as output:
        json.dump(result, output, ensure_ascii=False, indent=2)
        output.write("\n")
    print(f"Prepared revision {args.revision}. Review the output and all other configuration layers before rollout.")


if __name__ == "__main__":
    main()
