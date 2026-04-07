"""Anthropic Claude Sonnet 4.5 tool-search test, mirroring Program.cs."""

import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path

from mock_tools import SYSTEM_PROMPT, USER_PROMPT, build, mock_execute

API_KEY = os.environ.get("ANTHROPIC_API_KEY")
if not API_KEY:
    sys.exit("ANTHROPIC_API_KEY not set")

MODEL = "claude-sonnet-4-6"
BETA_HEADER = os.environ.get("ANTHROPIC_BETA", "advanced-tool-use-2025-11-20")
BASE = "https://api.anthropic.com"

EXCHANGES: list[dict] = []
CURRENT_SCENARIO = ""


def post(path: str, body: dict) -> dict:
    data = json.dumps(body).encode()
    req = urllib.request.Request(
        BASE + path,
        data=data,
        headers={
            "x-api-key": API_KEY,
            "anthropic-version": "2023-06-01",
            "anthropic-beta": BETA_HEADER,
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
    if parsed is None:
        raise RuntimeError(f"non-json response: {resp_body}")
    if status >= 400:
        raise RuntimeError(f"http {status}: {resp_body}")
    return parsed


def inline_tools(catalog):
    return [
        {
            "name": t["name"],
            "description": t["description"],
            "input_schema": t["schema"],
        }
        for t in catalog
    ]


def deferred_tools(catalog):
    return [
        {"type": "tool_search_tool_bm25_20251119", "name": "tool_search_tool_bm25"}
    ] + [
        {
            "name": t["name"],
            "description": t["description"],
            "input_schema": t["schema"],
            "defer_loading": True,
        }
        for t in catalog
    ]


def count_tokens(label: str, tools: list | None, scenario: str) -> int:
    global CURRENT_SCENARIO
    CURRENT_SCENARIO = scenario
    body = {
        "model": MODEL,
        "system": SYSTEM_PROMPT,
        "messages": [{"role": "user", "content": USER_PROMPT}],
    }
    if tools is not None:
        body["tools"] = tools
    resp = post("/v1/messages/count_tokens", body)
    n = resp["input_tokens"]
    print(f"  {label} : {n:>6}")
    return n


def run_agent(label: str, tools: list, scenario: str) -> None:
    global CURRENT_SCENARIO
    CURRENT_SCENARIO = scenario
    print(f"\n--- {label} ---")
    messages = [{"role": "user", "content": USER_PROMPT}]
    total_in = total_out = 0

    for turn in range(1, 6):
        body = {
            "model": MODEL,
            "max_tokens": 1024,
            "system": SYSTEM_PROMPT,
            "messages": messages,
            "tools": tools,
        }
        resp = post("/v1/messages", body)
        usage = resp.get("usage") or {}
        in_t = usage.get("input_tokens", 0)
        out_t = usage.get("output_tokens", 0)
        total_in += in_t
        total_out += out_t
        stop = resp.get("stop_reason")
        print(f"  turn {turn}: in={in_t:>6}  out={out_t:>5}  stop={stop}")

        assistant_blocks = []
        local_calls = []
        for block in resp.get("content", []):
            t = block.get("type")
            if t == "text":
                text = block.get("text", "")
                if text.strip():
                    print(f"    text: {text[:120]}")
                assistant_blocks.append(block)
            elif t == "tool_use":
                local_calls.append(block)
                args = json.dumps(block.get("input", {}))
                print(f"    tool_use: {block['name']}({args[:100]})")
                assistant_blocks.append(block)
            elif t == "server_tool_use":
                args = json.dumps(block.get("input", {}))
                print(f"    server_tool_use: {block.get('name')} {args[:100]}")
                assistant_blocks.append(block)
            elif t in ("tool_search_tool_result", "web_search_tool_result"):
                print(f"    {t}: (matched tools loaded)")
                assistant_blocks.append(block)
            else:
                assistant_blocks.append(block)

        messages.append({"role": "assistant", "content": assistant_blocks})

        if not local_calls:
            break

        user_blocks = []
        for call in local_calls:
            user_blocks.append({
                "type": "tool_result",
                "tool_use_id": call["id"],
                "content": mock_execute(call["name"]),
            })
            print(f"    [local exec] {call['name']} -> mock result")
        messages.append({"role": "user", "content": user_blocks})

    print(f"  TOTAL : in={total_in}  out={total_out}  combined={total_in + total_out}")


def main() -> None:
    catalog = build()
    print(f"Built {len(catalog)} mock tools.")
    print(f"Using anthropic-beta: {BETA_HEADER}\n")

    print("Counting tokens...\n")
    few = catalog[:3]
    t1 = count_tokens("1. Prompt only                            ", None, "count_prompt_only")
    t2 = count_tokens("2. Prompt + 21 tools inline               ", inline_tools(catalog), "count_inline_21")
    t3 = count_tokens("3. Prompt + 21 tools deferred (BM25)      ", deferred_tools(catalog), "count_deferred_21")
    t4 = count_tokens("4. Prompt + 3 tools inline                ", inline_tools(few), "count_inline_3")
    t5 = count_tokens("5. Prompt + 3 tools deferred (BM25)       ", deferred_tools(few), "count_deferred_3")

    print()
    print("---------------------------------------------")
    print(f"21 tools - inline overhead   : {t2 - t1:>6}")
    print(f"21 tools - deferred overhead : {t3 - t1:>6}")
    print(f" 3 tools - inline overhead   : {t4 - t1:>6}")
    print(f" 3 tools - deferred overhead : {t5 - t1:>6}")

    print()
    print("=============================================")
    print("Live agent runs (real /v1/messages, mock local tool execution)")
    print("=============================================")

    run_agent("6. Inline 21 tools (live)  ", inline_tools(catalog), "live_inline_21")
    run_agent("7. Deferred 21 tools (live)", deferred_tools(catalog), "live_deferred_21")

    flow_path = Path(__file__).parent / "flow_anthropic.json"
    flow_path.write_text(json.dumps(EXCHANGES, indent=2))
    print(f"\nWrote flow log: {flow_path} ({len(EXCHANGES)} exchanges)")


if __name__ == "__main__":
    main()
