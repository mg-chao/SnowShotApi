"""Unprivileged systemd runner; all launch specifications are owned by root."""
import json
import os
from pathlib import Path
import re
import sys

if len(sys.argv) != 3 or sys.argv[1] not in ("worker", "tunnel") or not re.fullmatch(
        r"(table|latex)-(blue|green)-[a-z0-9][a-z0-9-]{0,62}", sys.argv[2]):
    sys.exit("Invalid deployment instance")
name = "launch.json" if sys.argv[1] == "worker" else "tunnel.json"
specification = json.loads((Path("/var/lib/snowshot-deploy/slots") / sys.argv[2] / name).read_text())
os.environ.update(specification["environment"])
os.execv(specification["arguments"][0], specification["arguments"])
