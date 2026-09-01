#!/usr/bin/env python3
"""Check that upstream still defines the GenAI convention names CopilotScope reads.

semantic-conventions v1.42.0 (2026-06-12) deprecated the whole ``gen_ai.*`` set in the main
repository and federated it to ``open-telemetry/semantic-conventions-genai``, where nothing is
Stable yet. When one of those names changes, nothing in this repository notices: ingest keeps
returning 200 while the counters the name feeds go quietly to zero.

That is the worst failure mode a measurement tool has — wrong, not broken — so this script makes
a rename loud. It parses the ``gen_ai.*`` constants out of ``Domain/Sem.cs`` (the single place
that names them), fetches the upstream model, and reports any name upstream no longer defines.

Deliberately advisory: it reports, it does not gate. A convention can disappear upstream while a
shipped emitter still sends it for months, so dropping support on upstream's schedule would break
real users. The right response to a hit is to map the new name *alongside* the old one.

Usage:
    python3 scripts/semconv_canary.py                # human-readable
    python3 scripts/semconv_canary.py --format github  # missing=a,b,c for $GITHUB_OUTPUT
"""

from __future__ import annotations

import argparse
import json
import pathlib
import re
import sys
import urllib.error
import urllib.request

REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
SEM_CS = REPO_ROOT / "src" / "CopilotScope.Collector" / "Domain" / "Sem.cs"

# The upstream registry, as YAML in the genai repository. Fetched raw so the check needs no
# tooling beyond the standard library.
# All four model files, because the names are split across them: attributes live in registry,
# metric names only in metrics, event names only in events. Fetching a subset produces false
# "missing" reports, and a canary that cries wolf is a canary nobody reads.
_GENAI = "https://raw.githubusercontent.com/open-telemetry/semantic-conventions-genai/main/model/gen-ai"
UPSTREAM_SOURCES = [
    f"{_GENAI}/registry.yaml",
    f"{_GENAI}/metrics.yaml",
    f"{_GENAI}/events.yaml",
    f"{_GENAI}/spans.yaml",
]

CONST_RE = re.compile(r'public\s+const\s+string\s+\w+\s*=\s*"(gen_ai\.[^"]+)"')

# Names in the gen_ai.* namespace that upstream does not define, and that we consume on purpose.
# Without this the weekly job would file the same non-actionable report forever, and a canary
# nobody reads is worse than no canary. Each entry needs a reason; an unexplained one is drift
# hiding behind an allowlist.
KNOWN_NOT_UPSTREAM = {
    "gen_ai.completion":
        "legacy content key, superseded upstream by gen_ai.output.messages. Kept because shipped "
        "emitters still send it; Sem.cs reads both.",
    "gen_ai.prompt":
        "legacy content key, superseded upstream by gen_ai.input.messages. Same reason.",
    "gen_ai.usage.cache_creation.input_tokens":
        "Anthropic prompt-cache extension, never an upstream convention. Claude Code emits it and "
        "the cache economics analyzer needs it.",
    "gen_ai.server.time_to_first_token":
        "TTFT was proposed upstream and has moved around; kept as one of three accepted spellings "
        "in Sem.cs so a client using any of them still reports latency.",
}


def consumed_names() -> set[str]:
    """Every gen_ai.* name Sem.cs declares. Only gen_ai.* — vendor namespaces
    (copilot_chat.*, github.copilot.*, claude_code.*) are not upstream's to define."""
    if not SEM_CS.exists():
        sys.exit(f"Sem.cs not found at {SEM_CS}")
    return set(CONST_RE.findall(SEM_CS.read_text(encoding="utf-8")))


def upstream_text() -> str:
    """Concatenated upstream registries. Tries each source; a 404 just means that layout moved."""
    chunks = []
    for url in UPSTREAM_SOURCES:
        try:
            with urllib.request.urlopen(url, timeout=30) as response:
                chunks.append(response.read().decode("utf-8"))
        except urllib.error.HTTPError as err:
            if err.code != 404:
                print(f"warning: {url} returned {err.code}", file=sys.stderr)
        except Exception as err:  # network hiccup, DNS, TLS — advisory check, keep going
            print(f"warning: could not fetch {url}: {err}", file=sys.stderr)
    if not chunks:
        sys.exit("could not fetch any upstream registry; not treating that as drift")
    return "\n".join(chunks)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--format", choices=["text", "github"], default="text")
    args = parser.parse_args()

    names = consumed_names()
    haystack = upstream_text()

    # Substring match against the raw registry rather than a YAML parse: the registry expresses
    # names as an `id:` plus a namespace prefix, and the exact nesting has already changed once.
    # A looser check that survives a restructure is worth more than a precise one that breaks on
    # every reorganisation and cries wolf.
    absent = {n for n in names if n not in haystack}
    # Report only surprises: an expected-absent name is documented, not drift.
    missing = sorted(absent - KNOWN_NOT_UPSTREAM.keys())
    expected = sorted(absent & KNOWN_NOT_UPSTREAM.keys())

    if args.format == "github":
        print(f"missing={','.join(missing)}")
        print(f"checked={len(names)}")
        return 0

    print(f"Checked {len(names)} gen_ai.* names consumed by Domain/Sem.cs.")
    if expected:
        print(f"\n{len(expected)} absent upstream, knowingly (see KNOWN_NOT_UPSTREAM):")
        for name in expected:
            print(f"  - {name}: {KNOWN_NOT_UPSTREAM[name]}")
    if not missing:
        print("\nNo unexpected drift: every other consumed name is still defined upstream.")
        return 0

    print(f"\n{len(missing)} consumed but NOT defined upstream, and not expected:")
    for name in missing:
        print(f"  - {name}")
    print(
        "\nIngest will keep succeeding while whatever these feed goes to zero.\n"
        "Map the new name alongside the old one — emitters upgrade at their own pace, so\n"
        "both have to keep working — and add a captured fixture covering it."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
