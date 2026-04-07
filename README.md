# tool-search-testing

Test harnesses for **server-side tool search** on Anthropic Claude and OpenAI gpt-5.4. Both providers shipped this feature in the last few weeks. Almost nobody is talking about it.

The harnesses run the same 21-tool catalog and the same agent task against each provider, capture every HTTP request and response, and report what tool search actually costs in tokens and dollars.

For the full analysis (methodology, provider-specific footguns, integration code, MCP server implications, recommendations), see [`WRITEUP.md`](./WRITEUP.md).

## Headline result

Same task, same 21 verbose tools, two-turn agent loop with one local tool execution. Cost per 1 million conversations at list pricing.

| Provider | Inline | Best deferred | Saving |
|---|---:|---:|---:|
| Anthropic Claude Sonnet 4.6, BM25 | $25,512 | $21,465 | $4,047 (16%) |
| Anthropic Claude Sonnet 4.6, regex | $25,512 | **$16,104** | **$9,408 (37%)** |
| OpenAI gpt-5.4, namespace | $9,700 | **$6,520** | $3,180 (33%) |

Three things worth knowing before you ship this:

1. **On Anthropic, regex tool search beats BM25 by a wide margin on Sonnet 4.6** if your catalog has consistent naming. Sonnet 4.6 issues broader BM25 queries than 4.5 did, which loads more candidates per search; regex stays narrow regardless of model behaviour.
2. **On OpenAI, flat `defer_loading: true` on individual functions is a footgun.** It costs *more* than not deferring at all because OpenAI still ships compact stubs for every flagged function. You must wrap functions in a `namespace` for tool search to actually defer anything.
3. **On Anthropic, your message-history serializer must round-trip the `tool_search_tool_result` content block** every turn. If your agent loop only handles `text`, `tool_use`, and `tool_result`, the loaded tool definitions silently disappear and the model re-pays the search cost on every turn.

## Repository layout

```
csharp/
  anthropic/   C# harness using the official Anthropic NuGet SDK
  openai/      C# harness using raw REST against /v1/responses
python/
  anthropic/   Python harness, stdlib only (urllib)
  openai/      Python harness, stdlib only (urllib)
WRITEUP.md     full technical writeup
```

Each subdirectory is self-contained. The Python directories carry their own copy of the mock tool catalog.

## Running the harnesses

| Harness | Command | Required env |
|---|---|---|
| Anthropic C# | `cd csharp/anthropic && dotnet run` | `ANTHROPIC_API_KEY` |
| OpenAI C# | `cd csharp/openai && dotnet run` | `OPENAI_API_KEY` |
| Anthropic Python | `cd python/anthropic && python3 anthropic_test.py` | `ANTHROPIC_API_KEY` |
| OpenAI Python | `cd python/openai && python3 openai_test.py` | `OPENAI_API_KEY` |

Each run produces a `flow.json` (or `flow_anthropic.json` / `flow_openai.json`) alongside the script with the full request and response bodies for every HTTP exchange, tagged by scenario. The flow files are gitignored. Inspect them with `jq` to see the actual `tool_search_call` arguments, the matched tool definitions injected mid-turn, and the per-turn token usage.

## What each harness measures

Each harness runs two phases:

1. **Token counting**, to isolate the static cost of each configuration. Anthropic via `count_tokens`, OpenAI via a `tool_choice: none` shim against `/v1/responses`.
2. **Live two-turn agent run**, where the model actually invokes a local tool, the harness returns a mock result, and the model produces a final answer. This is the only way to see the post-search cost (the cost of definitions that the search tool injects mid-turn).

Scenarios per harness:

- Prompt only (baseline)
- Prompt + 21 tools inline
- Prompt + 21 tools deferred (BM25 on Anthropic, namespace on OpenAI)
- Prompt + 21 tools deferred (regex on Anthropic, flat per-function on OpenAI)
- 3-tool variants of the above to expose the slope
- Two live agent runs (inline 21 vs deferred 21 best variant)

## Notes

- Models tested: `claude-sonnet-4-6`, `gpt-5.4-2026-03-05`. Sonnet 4.6 results differ noticeably from 4.5 because 4.6 issues broader BM25 queries; the writeup covers this.
- Anthropic beta header required: `advanced-tool-use-2025-11-20`.
- OpenAI tool_search is gated to `gpt-5.4` and later.
- The OpenAI harnesses hit the REST endpoint directly because the published OpenAI SDKs had not been updated for `tool_search` when this was written.
- Pricing references Anthropic and OpenAI list pricing as of April 2026. Cached input is billed at roughly 10% of normal on both providers.

## License

MIT.
