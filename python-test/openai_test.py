"""OpenAI gpt-5.4 tool-search test, mirroring openai-test/Program.cs."""

import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path

from mock_tools import SYSTEM_PROMPT, USER_PROMPT, build, mock_execute

API_KEY = os.environ.get("OPENAI_API_KEY")
if not API_KEY:
    sys.exit("OPENAI_API_KEY not set")

MODEL = "gpt-5.4"
BASE = "https://api.openai.com"

EXCHANGES: list[dict] = []
CURRENT_SCENARIO = ""


def post(path: str, body: dict) -> tuple[int, dict | None]:
    data = json.dumps(body).encode()
    req = urllib.request.Request(
        BASE + path,
        data=data,
        headers={
            "authorization": f"Bearer {API_KEY}",
            "content-type": "application/json",
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(req) as resp:
            resp_body = resp.read().decode()
            status = resp.status
    except urllib.error.HTTPError as e:
        resp_body = e.read().decode()
        status = e.code
    parsed = None
    try:
        parsed = json.loads(resp_body)
    except json.JSONDecodeError:
        pass
    EXCHANGES.append({
        "scenario": CURRENT_SCENARIO,
        "method": "POST",
        "endpoint": path,
        "status": status,
        "request": body,
        "response": parsed,
    })
    return status, parsed


def make_function(t: dict, defer: bool = False) -> dict:
    f = {
        "type": "function",
        "name": t["name"],
        "description": t["description"],
        "parameters": t["schema"],
    }
    if defer:
        f["defer_loading"] = True
    return f


def inline_tools(catalog):
    return [make_function(t) for t in catalog]


def deferred_flat_tools(catalog):
    return [make_function(t, defer=True) for t in catalog] + [{"type": "tool_search"}]


def deferred_namespace_tools(catalog):
    return [
        {
            "type": "namespace",
            "name": "acme_ops",
            "description": (
                "ACME Corp internal operations toolset (HR, finance, "
                "inventory, ticketing, deployments, monitoring, customers, "
                "calendar, docs)."
            ),
            "tools": [make_function(t, defer=True) for t in catalog],
        },
        {"type": "tool_search"},
    ]


def count_tokens(label: str, tools: list | None, scenario: str) -> int:
    global CURRENT_SCENARIO
    CURRENT_SCENARIO = scenario
    body = {
        "model": MODEL,
        "instructions": SYSTEM_PROMPT,
        "input": USER_PROMPT,
        "max_output_tokens": 16,
    }
    if tools:
        body["tools"] = tools
        body["tool_choice"] = "none"
    status, resp = post("/v1/responses", body)
    if status >= 400 or resp is None:
        msg = (resp or {}).get("error", {}).get("message", "?") if resp else "?"
        print(f"  {label} : ERROR {status} {msg[:120]}")
        return -1
    n = resp.get("usage", {}).get("input_tokens", -1)
    print(f"  {label} : {n:>6}")
    return n


def run_agent(label: str, tools: list, scenario: str) -> None:
    global CURRENT_SCENARIO
    CURRENT_SCENARIO = scenario
    print(f"\n--- {label} ---")
    input_items: list[dict] = [
        {"type": "message", "role": "user",
         "content": [{"type": "input_text", "text": USER_PROMPT}]},
    ]
    total_in = total_out = 0

    for turn in range(1, 6):
        body = {
            "model": MODEL,
            "instructions": SYSTEM_PROMPT,
            "input": list(input_items),
            "tools": tools,
            "max_output_tokens": 1024,
        }
        status, resp = post("/v1/responses", body)
        if status >= 400 or resp is None:
            msg = (resp or {}).get("error", {}).get("message", "?") if resp else "?"
            print(f"  turn {turn}: HTTP {status}: {msg}")
            return

        usage = resp.get("usage") or {}
        in_t = usage.get("input_tokens", 0)
        out_t = usage.get("output_tokens", 0)
        total_in += in_t
        total_out += out_t
        print(f"  turn {turn}: in={in_t:>6}  out={out_t:>5}  status={resp.get('status')}")

        local_calls: list[tuple[str, str]] = []
        for item in resp.get("output", []):
            t = item.get("type", "")
            if t == "message":
                for c in item.get("content", []):
                    text = c.get("text")
                    if text:
                        print(f"    text: {text[:120]}")
            elif t == "function_call":
                call_id = item.get("call_id", "")
                name = item.get("name", "")
                args = item.get("arguments", "")
                local_calls.append((call_id, name))
                print(f"    function_call: {name}({args[:100]})")
            elif t == "tool_search_call":
                print(f"    tool_search_call: {json.dumps(item)[:140]}")
            else:
                print(f"    {t}: {json.dumps(item)[:120]}")
            input_items.append(item)

        if not local_calls:
            break

        for call_id, name in local_calls:
            input_items.append({
                "type": "function_call_output",
                "call_id": call_id,
                "output": mock_execute(name),
            })
            print(f"    [local exec] {name} -> mock result")

    print(f"  TOTAL : in={total_in}  out={total_out}  combined={total_in + total_out}")


def main() -> None:
    catalog = build()
    print(f"Built {len(catalog)} mock tools.\n")

    print("Counting tokens via /v1/responses...\n")
    few = catalog[:3]
    t1 = count_tokens("1. Prompt only                             ", None, "count_prompt_only")
    t2 = count_tokens("2. Prompt + 21 tools inline                ", inline_tools(catalog), "count_inline_21")
    t3 = count_tokens("3. Prompt + 21 tools deferred (flat)       ", deferred_flat_tools(catalog), "count_deferred_flat_21")
    t3b = count_tokens("3b. Prompt + 21 tools deferred (namespace) ", deferred_namespace_tools(catalog), "count_deferred_ns_21")
    t4 = count_tokens("4. Prompt + 3 tools inline                 ", inline_tools(few), "count_inline_3")
    t5 = count_tokens("5. Prompt + 3 tools deferred (flat)        ", deferred_flat_tools(few), "count_deferred_flat_3")
    t5b = count_tokens("5b. Prompt + 3 tools deferred (namespace)  ", deferred_namespace_tools(few), "count_deferred_ns_3")

    print()
    print("---------------------------------------------")
    if t1 > 0:
        def fmt(v):
            return str(v - t1) if v > 0 else "n/a"
        print(f"21 tools - inline overhead       : {t2 - t1:>6}")
        print(f"21 tools - deferred flat         : {fmt(t3):>6}")
        print(f"21 tools - deferred namespace    : {fmt(t3b):>6}")
        print(f" 3 tools - inline overhead       : {t4 - t1:>6}")
        print(f" 3 tools - deferred flat         : {fmt(t5):>6}")
        print(f" 3 tools - deferred namespace    : {fmt(t5b):>6}")

    print()
    print("=============================================")
    print("Live agent runs (real /v1/responses, mock local tool execution)")
    print("=============================================")

    run_agent("6. Inline 21 tools (live)         ", inline_tools(catalog), "live_inline_21")
    run_agent("7. Deferred 21 tools (flat)       ", deferred_flat_tools(catalog), "live_deferred_flat_21")
    run_agent("8. Deferred 21 tools (namespace)  ", deferred_namespace_tools(catalog), "live_deferred_ns_21")

    flow_path = Path(__file__).parent / "flow_openai.json"
    flow_path.write_text(json.dumps(EXCHANGES, indent=2))
    print(f"\nWrote flow log: {flow_path} ({len(EXCHANGES)} exchanges)")


if __name__ == "__main__":
    main()
