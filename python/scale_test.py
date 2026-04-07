"""Scale test: measure inline tool block cost across N tools on both providers.

Hits Anthropic count_tokens and OpenAI /v1/responses with the same catalog
truncated to N tools, for N in {0, 1, 2, 5, 10, 15, 20, 25}, and computes a
linear fit to isolate per-request fixed overhead from per-tool marginal cost.

Run from this directory with both ANTHROPIC_API_KEY and OPENAI_API_KEY set:
    python3 scale_test.py
"""

import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent / "anthropic"))
from mock_tools import SYSTEM_PROMPT, USER_PROMPT, build  # noqa: E402

ANTHROPIC_KEY = os.environ.get("ANTHROPIC_API_KEY")
OPENAI_KEY = os.environ.get("OPENAI_API_KEY")
if not ANTHROPIC_KEY or not OPENAI_KEY:
    sys.exit("set both ANTHROPIC_API_KEY and OPENAI_API_KEY")

ANTHROPIC_MODEL = "claude-sonnet-4-6"
OPENAI_MODEL = "gpt-5.4"
ANTHROPIC_BETA = "advanced-tool-use-2025-11-20"

CATALOG = build()
N_VALUES = [0, 1, 2, 5, 10, 15, 20]


def post(url: str, headers: dict, body: dict) -> dict:
    req = urllib.request.Request(
        url, data=json.dumps(body).encode(), headers=headers, method="POST",
    )
    try:
        with urllib.request.urlopen(req) as resp:
            return json.loads(resp.read().decode())
    except urllib.error.HTTPError as e:
        raise RuntimeError(f"http {e.code}: {e.read().decode()}")


def anthropic_count(n: int) -> int:
    tools = [
        {"name": t["name"], "description": t["description"], "input_schema": t["schema"]}
        for t in CATALOG[:n]
    ]
    body = {
        "model": ANTHROPIC_MODEL,
        "system": SYSTEM_PROMPT,
        "messages": [{"role": "user", "content": USER_PROMPT}],
    }
    if tools:
        body["tools"] = tools
    resp = post(
        "https://api.anthropic.com/v1/messages/count_tokens",
        {
            "x-api-key": ANTHROPIC_KEY,
            "anthropic-version": "2023-06-01",
            "anthropic-beta": ANTHROPIC_BETA,
            "content-type": "application/json",
        },
        body,
    )
    return resp["input_tokens"]


def openai_count(n: int) -> tuple[int, int]:
    tools = [
        {
            "type": "function",
            "name": t["name"],
            "description": t["description"],
            "parameters": t["schema"],
        }
        for t in CATALOG[:n]
    ]
    body = {
        "model": OPENAI_MODEL,
        "instructions": SYSTEM_PROMPT,
        "input": USER_PROMPT,
        "max_output_tokens": 16,
    }
    if tools:
        body["tools"] = tools
        body["tool_choice"] = "none"
    resp = post(
        "https://api.openai.com/v1/responses",
        {
            "authorization": f"Bearer {OPENAI_KEY}",
            "content-type": "application/json",
        },
        body,
    )
    usage = resp["usage"]
    cached = usage.get("input_tokens_details", {}).get("cached_tokens", 0)
    return usage["input_tokens"], cached


def linear_fit(xs: list[int], ys: list[int]) -> tuple[float, float]:
    """Least-squares fit y = a + b*x. Returns (a, b)."""
    n = len(xs)
    sx, sy = sum(xs), sum(ys)
    sxx = sum(x * x for x in xs)
    sxy = sum(x * y for x, y in zip(xs, ys))
    b = (n * sxy - sx * sy) / (n * sxx - sx * sx)
    a = (sy - b * sx) / n
    return a, b


def main() -> None:
    if max(N_VALUES) > len(CATALOG):
        sys.exit(f"catalog only has {len(CATALOG)} tools; cannot request {max(N_VALUES)}")

    print(f"Scale test: N tools inline, both providers")
    print(f"Catalog size: {len(CATALOG)}, requesting N in {N_VALUES}\n")
    print(f"{'N':>4} | {'Anthropic':>10} | {'OpenAI':>10} | {'O cached':>10} | {'A-Δ':>8} | {'O-Δ':>8} | {'O/A':>6}")
    print("-" * 75)

    a_results: list[int] = []
    o_results: list[int] = []
    a_prev = o_prev = None
    for n in N_VALUES:
        a = anthropic_count(n)
        o, o_cached = openai_count(n)
        a_results.append(a)
        o_results.append(o)
        a_delta = "" if a_prev is None else str(a - a_prev)
        o_delta = "" if o_prev is None else str(o - o_prev)
        ratio = f"{o/a:.2f}" if a else "n/a"
        print(f"{n:>4} | {a:>10} | {o:>10} | {o_cached:>10} | {a_delta:>8} | {o_delta:>8} | {ratio:>6}")
        a_prev, o_prev = a, o

    print()
    print("Linear fit  (cost = fixed + per_tool * N)")
    print("-" * 60)
    a_fixed, a_per = linear_fit(N_VALUES, a_results)
    o_fixed, o_per = linear_fit(N_VALUES, o_results)
    print(f"  Anthropic : fixed = {a_fixed:>8.1f}    per_tool = {a_per:>6.2f}")
    print(f"  OpenAI    : fixed = {o_fixed:>8.1f}    per_tool = {o_per:>6.2f}")
    print()
    print(f"  OpenAI per-tool as % of Anthropic per-tool : {100 * o_per / a_per:.1f}%")
    print(f"  OpenAI fixed   as % of Anthropic fixed     : {100 * o_fixed / a_fixed:.1f}%")
    print()
    print("R^2 sanity check (closer to 1.0 = better linear fit):")
    print(f"  Anthropic R^2 = {r_squared(N_VALUES, a_results, a_fixed, a_per):.5f}")
    print(f"  OpenAI    R^2 = {r_squared(N_VALUES, o_results, o_fixed, o_per):.5f}")


def r_squared(xs: list[int], ys: list[int], a: float, b: float) -> float:
    mean_y = sum(ys) / len(ys)
    ss_tot = sum((y - mean_y) ** 2 for y in ys)
    ss_res = sum((y - (a + b * x)) ** 2 for x, y in zip(xs, ys))
    return 1 - ss_res / ss_tot if ss_tot else 1.0


if __name__ == "__main__":
    main()
