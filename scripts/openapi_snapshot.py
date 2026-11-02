from __future__ import annotations

import argparse
import json
from pathlib import Path
from urllib.request import urlopen

import yaml


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("url")
    parser.add_argument("output", type=Path)
    arguments = parser.parse_args()
    with urlopen(arguments.url, timeout=10) as response:
        document = json.load(response)
    document.pop("servers", None)
    document["info"] = {"title": "SnowShot API", "version": "current"}
    arguments.output.write_text(
        yaml.safe_dump(document, sort_keys=False, allow_unicode=True),
        encoding="utf-8",
        newline="\n",
    )


if __name__ == "__main__":
    main()
