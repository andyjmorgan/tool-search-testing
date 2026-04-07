# Server-side tool search on Anthropic and OpenAI

A practical guide and measured comparison.

## Why this document exists

Open r/mcp on any given day and there is a fresh "revolutionary tool search MCP" post. Embedding-based retrieval. LLM-based routing. Fuzzy matching. BM25 wrapped in a server. Hierarchical category trees. Vector databases bolted onto stdio transports. The pattern repeats roughly every six hours, each iteration solving the same problem in a slightly different way, none of them widely adopted, none of them solving it at the layer where it actually belongs.

In the last few weeks both Anthropic and OpenAI quietly shipped server-side tool search directly into the API. Almost nobody is talking about it. The features are documented but not promoted, gated behind beta headers and brand-new model versions, and the existing SDKs have not yet caught up. Most agent frameworks I have looked at do not handle the new content-block types correctly out of the box. This is the kind of capability that becomes table stakes the moment people realise it exists, the same way structured outputs and prompt caching did.

Google has not committed to anything equivalent yet, but every sign points to this becoming a staple feature alongside web search, code execution, and structured outputs. Expect the rest of the major providers to fall in line within the next quarter.

I built test harnesses in both C# and Python for each provider, ran them against the same 21-tool catalog and the same agent task, and captured every HTTP exchange. This document covers what these features do, what they actually cost in tokens and dollars, how to integrate them, what to do about MCP servers, and the surprising number of footguns hiding in both implementations.

## The problem they solve

Every time you call a model with tools, you ship the entire tool catalog as part of the request. Names, descriptions, JSON Schemas, the lot. The model never sees them as "tools", it sees them as input tokens, and you pay for them on every single turn of every single conversation.

For a small agent with 3 or 4 tools this is fine. For a real enterprise integration with 20, 50, 200 tools across HR, finance, monitoring, CRM, ticketing, deployments and so on, the tool block becomes the dominant cost of every request. A typical verbose tool with a paragraph description and a real schema runs around 150 tokens on Anthropic and around 70 tokens on OpenAI. Twenty of those is several thousand tokens of pure overhead, paid on every turn, before the model has done any actual work.

The standard mitigation has been to pre-filter tools client-side: route by intent, embed and rank, build a tool-picker LLM call in front of the real one. That is what all the r/mcp posts are doing, and it works, but it adds an extra hop, an extra failure mode, an extra thing to maintain, and an extra place where the wrong tool can get picked. Server-side tool search moves the same problem inside the model provider, where the model itself decides what it needs mid-turn, and the matched definitions are injected directly into its context without a round trip. Same outcome, fewer moving parts, no extra inference step.

## How server-side tool search works

The shape is the same on both providers, even though the names differ.

1. You declare your tool catalog as normal, but mark tools as deferred. The full definitions stay registered with the request, but they are held server-side and not shown to the model.
2. You include a "search" tool in your tools array. This is the only tool the model sees in its context: a server-side tool that takes a query and returns matching tool definitions.
3. When the model decides it needs a capability it cannot see, it issues a search call. The provider runs retrieval over your deferred catalog, picks the matches, and injects only those definitions into the context for the rest of the turn.
4. The model then calls the matched tool normally, your client receives a regular tool-use block, you execute it, and you reply with the tool result.
5. Loaded definitions persist into subsequent turns via the conversation transcript, so the search cost is paid once per tool rather than once per turn.

The retrieval mechanisms differ:

- **Anthropic** ships two variants today: `tool_search_tool_bm25_20251119` (BM25 keyword ranking across tool descriptions) and `tool_search_tool_regex_20251119` (regex pattern matching against tool names and descriptions). Both are server tools and they are configured identically. A third semantic or embedding variant has not been announced but fits the pattern.
- **OpenAI** exposes a single `tool_search` tool with an undocumented retrieval mechanism. Inspecting the request captures shows it using path-style arguments against your namespace structure rather than free-text queries, so the retrieval is effectively driven by your catalog's organisation rather than by description keywords.

That difference matters when you design your catalog, and I will come back to it.

## The test

A single agent task (*"Find any open P1 incidents from the last 24 hours and tell me which service they belong to"*) executed against a catalog of 21 verbose, realistic tool definitions covering HR, finance, inventory, ticketing, deployments, monitoring, customers, calendar, and docs. Each tool has a multi-sentence description and a JSON Schema, shaped like a real enterprise toolset rather than stubs. The full catalog lives in `python/anthropic/mock_tools.py` and is duplicated verbatim in `python/openai/mock_tools.py`.

Three test surfaces, all in Python with stdlib only:

- **Anthropic** Claude Sonnet 4.6 (`claude-sonnet-4-6`) via the Beta Messages API. Beta header `advanced-tool-use-2025-11-20`.
- **OpenAI** `gpt-5.4-2026-03-05` via the Responses API.
- **OpenAI ChatCompletions** for the same model, used by the scale test only because the published OpenAI SDKs had not yet exposed `tool_search` when I ran this and ChatCompletions does not support `tool_search` at all.

Each harness measures the same workload in two modes:

1. **Token counting**, to isolate the static cost of each configuration. **Anthropic exposes a dedicated `/v1/messages/count_tokens` endpoint** that returns input token counts without invoking the model. **OpenAI does not have this**, so I send a real `/v1/responses` (or `/v1/chat/completions`) call with `tool_choice: "none"` and `max_output_tokens: 16` and read `usage.input_tokens` off the response. Both numbers are the exact server-side counts the providers will bill, not client-side estimates from a local tokenizer.
2. **Live agent run**, a real two-turn call where the model actually invokes a local tool, the harness returns a mock result, and the model produces a final answer. This is the only way to see the post-search cost (the cost of definitions that the search tool has just injected mid-turn).

For each provider I measured: prompt only, prompt + 21 tools inline, prompt + 21 tools deferred, plus 3-tool variants to expose the slope. On Anthropic I measured both BM25 and regex deferred modes. On OpenAI I measured both the flat per-function `defer_loading` and the namespace-wrapped form.

A separate **scale test** (`python/scale_test.py`) hits all three API surfaces across N inline tools for N in {0, 1, 2, 5, 10, 15, 20}, then runs a least-squares linear fit to isolate fixed per-request overhead from per-tool marginal cost. That data is in the "Per-tool cost analysis" section below.

All requests and responses from the live runs are captured to `flow.json` files in each harness directory.

## Anthropic results

Token counts via `count_tokens`. Costs computed at Claude Sonnet 4.6 list pricing ($3/MTok input, $15/MTok output) per 1 million requests, so that fractional cents become legible.

| Scenario | Tokens | Δ over baseline | Cost / 1M req | Δ vs inline |
|---|---:|---:|---:|---:|
| Prompt only | 80 | 0 | $240 | — |
| 21 tools inline | 3,245 | 3,165 | $9,735 | — |
| 21 tools deferred (BM25) | **765** | **685** | **$2,295** | **−$7,440** |
| 21 tools deferred (regex) | **790** | **710** | **$2,370** | **−$7,365** |
| 3 tools inline | 1,052 | 972 | $3,156 | — |
| 3 tools deferred (BM25) | 765 | 685 | $2,295 | −$861 |
| 3 tools deferred (regex) | 790 | 710 | $2,370 | −$786 |

The deferred overhead is **constant regardless of catalog size** for both variants. That tells you something about what the model actually sees:

- **BM25 costs 685 tokens flat**. These are entirely the BM25 tool definition plus the system-injected instructions. No name index, no per-tool stub, nothing that scales with catalog size.
- **Regex costs 710 tokens flat**, 25 tokens more than BM25. The extra is presumably the system prompt teaching the model how to write regexes correctly rather than free-text queries.
- That makes Anthropic's tool_search system prompt **by far the chunkiest server tool they ship**. For comparison, `web_search` adds about 200 tokens of overhead on the same model.
- Break-even versus inline is reached at roughly 2 of these verbose tools. Below that, the entry fee plus the model's narration cost more than you save.

Live two-turn agent run on Sonnet 4.6 (Anthropic does not get any prompt cache hits in these runs because no `cache_control` breakpoints are set):

| Configuration | Total in | Total out | Combined | Cost / 1M conv | Saving |
|---|---:|---:|---:|---:|---:|
| Inline 21 | 6,684 | 357 | 7,041 | **$25,407** | — |
| Deferred BM25 | 3,707 | 453 | 4,160 | **$17,916** | **$7,491 (29%)** |
| Deferred regex | 4,002 | 417 | 4,419 | **$18,261** | **$7,146 (28%)** |

What the live run reveals that `count_tokens` cannot:

- **Deferred turn 1 is much larger than the 765-token static count.** The extra is the **post-search injection**: tool_search actually fires mid-turn, matches a handful of tools, and their definitions are loaded into the same turn before the model produces its tool call. BM25 turn 1 lands around 2,000-2,500 tokens depending on how many candidates the search returned, regex around 1,800-2,300.
- **Turn 2 input drops sharply** because only the matched tool definitions persist in the transcript via the `tool_search_tool_result` content block. The other tools are gone forever.
- **Output tokens are notably higher on the deferred path** because the model narrates its search ("Let me find the right tool to query incidents…"). Sonnet 4.6 narrates around 150 to 250 tokens per search invocation. You can suppress this with a short system instruction, at some risk to reliability.

### BM25 versus regex

In *some* runs regex beats BM25 substantially. In other runs they come out almost identical, or BM25 wins. Across the runs I've captured on the same task, same catalog, same model:

| Run | Model | BM25 combined | Regex combined | Winner |
|---|---|---:|---:|---|
| A | Sonnet 4.5 | 4,324 | 3,518 | regex by 806 |
| B | Sonnet 4.6 | 5,183 | 3,784 | regex by 1,399 |
| C | Sonnet 4.6 | 4,160 | 4,419 | BM25 by 259 |

**The variance is large enough that any single measurement is unreliable as a recommendation.** The model is non-deterministic about how it issues the search query. Sometimes regex picks a tight pattern like `incident` and returns one matching tool; other times it picks something broader and loads several. Sometimes BM25 issues a focused free-text query; other times it goes wide. On this 21-tool catalog with the model picking, both variants land in the same ballpark when averaged across multiple runs.

The mechanism difference still matters in design:

- **BM25** ranks free-text queries against tool descriptions. Robust on heterogeneous catalogs where you cannot rely on naming conventions, because synonyms and word overlap recover from vocabulary mismatches. Brittle if your tool descriptions use different vocabulary than the user's request.
- **Regex** matches a pattern against tool names and descriptions. Tightest possible retrieval *if* the model can infer your naming convention from the catalog (e.g. every monitoring tool prefixed `monitoring_`). Returns nothing and forces a re-search if the model's pattern is wrong.

My provisional recommendation: default to **BM25** for catalogs assembled from third-party MCP servers or anywhere you cannot guarantee naming discipline. Default to **regex** if you fully control the catalog and your tool names follow predictable prefixes. Better still, run both against your real catalog and your real workload over a few hundred turns and pick the winner empirically. Single-run measurements (including the ones in this writeup) are inadequate signal.

## OpenAI results

Token counts via `/v1/responses` with `tool_choice: none` and `max_output_tokens: 16`. Costs computed at gpt-5.4 list pricing ($2.50/MTok input, $15/MTok output) per 1 million requests.

| Scenario | Tokens | Δ over baseline | Cost / 1M req | Δ vs inline |
|---|---:|---:|---:|---:|
| Prompt only | 76 | 0 | $190 | — |
| 21 tools inline | 1,532 | 1,456 | $3,830 | — |
| 21 tools deferred (flat per-function) | 1,786 | 1,710 | $4,465 | **+$635 (worse)** |
| 21 tools deferred (namespace wrapper) | **475** | **399** | **$1,188** | **−$2,642** |
| 3 tools inline | 354 | 278 | $885 | — |
| 3 tools deferred (flat per-function) | 675 | 599 | $1,688 | **+$803 (worse)** |
| 3 tools deferred (namespace wrapper) | 475 | 399 | $1,188 | **+$303 (worse)** |

The namespace overhead (399 tokens) is smaller than Anthropic's 685 but larger than 3 inline OpenAI tools (278 tokens), so on a tiny catalog you actually pay more for tool search than you save. Break-even is around 4 to 5 of these verbose tools.

Live two-turn agent run, with OpenAI's automatic prompt cache hits visible. Costs use the **uncached list rate** ($2.50/MTok input) for both input and cached input — i.e. a dry-run cost as if caching were disabled, for honest comparison against Anthropic which currently has no breakpoints set. The `cached` column shows how many of those input tokens **were** served from cache, so you can see the cache discount that *would* apply in steady state.

| Configuration | Total in | Cached | Out | Combined | Cost / 1M conv (dry) | Saving |
|---|---:|---:|---:|---:|---:|---:|
| Inline 21 | 3,172 | **2,816** | 119 | 3,291 | **$9,715** | — |
| Deferred flat 21 | 4,132 | 3,328 | 122 | 4,254 | **$12,160** | **−$2,445 (worse)** |
| Deferred namespace 21 | **1,786** | **0** | 135 | 1,921 | **$6,490** | **$3,225 (33%)** |

### The 1,024-token cache threshold (and why it inverts the conclusion in steady state)

OpenAI's automatic prompt cache requires a **stable prefix of at least 1,024 tokens** to fire. Below that threshold, no caching happens at all. The scale test below confirms this exactly: the cache started firing at N=15 inline tools (1,024 tokens cached) and N=20 (1,280 cached), and never fired at N=10 or below.

This has a critical and counter-intuitive consequence for the live runs:

- **Inline 21 (1,532-token static prefix)** crosses the threshold. 92% of its input tokens hit the cache in turn 2 (`cached: 2,816 / 3,172`). With the cache discount applied at ~10% of normal, the *real* billed cost in steady state is dramatically lower than the $9,715 dry-run number.
- **Deferred namespace 21 (475-token static prefix, 843 turn-1 input)** is **below the threshold**. Both turns show `cached: 0`. Namespace-deferred never participates in OpenAI's automatic prompt cache because its prefix is too small.

In steady state with cache discount applied, the inline path becomes effectively cheaper than the namespace-deferred path:

```
Inline 21 effective billed cost ≈ 356 uncached × $2.50 + 2,816 cached × $0.25 + 119 × $15
                                ≈ $0.89 + $0.70 + $1.79
                                ≈ $3.38 per 1k conversations
                                ≈ $3,380 per 1M conversations

Deferred namespace effective billed cost ≈ 1,786 × $2.50 + 135 × $15
                                         ≈ $4.47 + $2.03
                                         ≈ $6.50 per 1k conversations
                                         ≈ $6,500 per 1M conversations
```

**Inline becomes ~2x cheaper than namespace-deferred in cache-warm steady state.** The "namespace deferred saves 33%" headline only holds for cold-cache, dry-run measurements.

The honest framing: server-side tool search on OpenAI is a clean win on **cold cache** and on workloads where the catalog is too large to cache effectively, but on a warm-cache workload with a stable inline prefix, automatic prompt caching beats it.

### OpenAI's biggest footgun: flat defer_loading

### OpenAI's biggest footgun: flat defer_loading

This is the most important thing in this document. **Setting `defer_loading: true` on individual function tools at the top level of the `tools` array is worse than not deferring at all.**

In flat mode OpenAI still ships compact stubs for every flagged function (around 62 tokens each, scaling linearly with catalog size) and adds the `tool_search` tool definition on top. The result is a deferred mode that costs more than inline. I measured 1,786 tokens for 21 tools flat-deferred, against 1,532 inline, in both the static count and the live run. Confirmed on `gpt-5.4-2026-03-05` as of the test date.

The only configuration that actually defers is wrapping your functions in a namespace:

```json
{
  "tools": [
    {
      "type": "namespace",
      "name": "acme_ops",
      "description": "ACME Corp internal operations toolset (HR, finance, inventory, ticketing, deployments, monitoring, customers, calendar, docs).",
      "tools": [
        { "type": "function", "name": "monitoring_list_incidents", "description": "...", "parameters": { }, "defer_loading": true },
        { "type": "function", "name": "ticket_search", "description": "...", "parameters": { }, "defer_loading": true }
      ]
    },
    { "type": "tool_search" }
  ]
}
```

The `defer_loading` flag goes on each child function, not on the namespace container itself (the container rejects `defer_loading` with `unknown parameter`). The namespace's `name` and `description` are the only thing the model sees about your catalog until `tool_search` fires, so write them well.

## Per-tool cost analysis

OpenAI's inline tool block is roughly half the size of Anthropic's for the same catalog. To prove this is structural rather than two-point measurement noise, I ran a scale test (`python/scale_test.py`) hitting all three API surfaces across N inline tools for N in {0, 1, 2, 5, 10, 15, 20}, then ran a least-squares linear fit `cost = fixed + per_tool * N` on each.

| N | Anthropic | OpenAI Resp | OpenAI Chat | A-Δ | Resp-Δ | Chat-Δ |
|---:|---:|---:|---:|---:|---:|---:|
| 0 | 80 | 76 | 76 | — | — | — |
| 1 | 718 | 191 | 272 | +638 | +115 | +196 |
| 2 | 879 | 284 | 365 | +161 | +93 | +93 |
| 5 | 1,266 | 498 | 579 | +387 | +214 | +214 |
| 10 | 1,865 | 833 | 914 | +599 | +335 | +335 |
| 15 | 2,456 | 1,162 | 1,243 | +591 | +329 | +329 |
| 20 | 3,038 | 1,466 | 1,547 | +582 | +304 | +304 |

Linear fit:

| | Per-tool marginal | Fixed per-request | R² |
|---|---:|---:|---:|
| Anthropic Sonnet 4.6 | **133.0** | 464.5 | 0.967 |
| OpenAI gpt-5.4 (Responses) | **68.4** | 126.7 | 0.997 |
| OpenAI gpt-5.4 (ChatCompletions) | **70.1** | 183.0 | 0.990 |

OpenAI per-tool marginal as a percentage of Anthropic: **51.4%** (Responses) and **52.7%** (ChatCompletions). Anthropic's R² is slightly worse than OpenAI's because of the first-tool jump anomaly described below; if you exclude N=1 the Anthropic fit gets close to 1.0 too. **OpenAI's per-tool cost is roughly half of Anthropic's, regardless of which OpenAI API you use.**

### What the deltas reveal

Three things jump out of the per-N delta columns:

1. **Anthropic has a massive "first tool" penalty.** Going from 0 tools to 1 tool costs **638** tokens on Anthropic, but going from 1 tool to 2 tools costs only 161, and subsequent tools settle at ~117 tokens each. The 0→1 jump is the entire tool-use system preamble (~520 tokens) that Anthropic injects the moment you supply any tools at all, plus the actual cost of the first tool. OpenAI has the same pattern but much milder: first tool +115 (Responses) or +196 (Chat), subsequent tools ~65.
2. **ChatCompletions and Responses charge identical per-tool cost from N=2 onward.** The deltas are byte-identical across all three APIs from the second tool on (Anthropic excluded). The only difference between the two OpenAI APIs is the first-tool overhead: ChatCompletions costs 81 more tokens for the first tool because of the extra `{"function": {...}}` wrapper layer in the ChatCompletions tool shape. After that, the wrapper appears to be tokenized via merges or collapsed server-side and the per-tool cost is identical.
3. **The OpenAI fit is essentially perfectly linear.** R² = 0.997 (Responses) and 0.990 (Chat). This is as clean as scaling laws get in production APIs.

### Where the 2x gap comes from

Three contributing factors stack:

1. **Tokenizer efficiency.** OpenAI's `o200k` vocabulary has aggressive merges for common JSON constructs (`{"type":"`, `"properties":`, `"description":`, schema keywords). Anthropic's tokenizer is less optimised for JSON. Side experiments suggest this accounts for around a third of the gap.
2. **Tool format wrapper.** Anthropic wraps each tool in `{name, description, input_schema}`. OpenAI Responses uses `{type, name, description, parameters}`. The shapes are similar in JSON length, but Anthropic appears to render each tool with more explicit structural framing in the model's actual prompt context.
3. **System-injected tool-use instructions.** Both providers prepend hidden system instructions teaching the model how to call the tools you supplied. Anthropic's preamble is ~520 tokens versus OpenAI's ~50 tokens (you can see this in the 0→1 jump). Some of that verbosity also scales per tool. The same 1.7x ratio shows up in the tool_search system prompts themselves (685 tokens for Anthropic's BM25 versus 399 for OpenAI's `tool_search`), which strongly suggests the same root cause.

### What this means in practice

- **Anthropic has more to gain from tool search** because its inline cost is bigger to start with. You can see this in the dollar savings: the deferred path saves more in absolute terms on Anthropic than on OpenAI, despite OpenAI's smaller starting cost.
- **The Anthropic premium is partly buying you something.** Some of those extra tokens are tool-use system instructions and stronger schema-following discipline, not pure waste. This single-task setup does not exercise that, but on workloads with many similar tools the model has to disambiguate between, the premium may be paying for itself in correctness.
- **ChatCompletions cannot use server-side tool search at all.** `tool_search` is documented as Responses-only. ChatCompletions users have no choice but inline tools or client-side filtering, so the entire deferred-tool story in this writeup does not apply to them. Migrating from ChatCompletions to Responses is a prerequisite for using server-side tool search.
- **Prompt caching narrows the gap.** Cached input is billed at roughly 10% on both providers. A steady-state inline workload with high cache hit rates closes most of the dollar gap, because the bulky Anthropic tool block becomes mostly cached. The numbers in this writeup are uncached worst-case.

## Provider-specific details

### Anthropic

- **Beta header required**: `anthropic-beta: advanced-tool-use-2025-11-20`. Without it both tool_search types are rejected.
- **No namespace concept**. You set `defer_loading: true` on each individual tool and add a `tool_search_tool_bm25_20251119` or `tool_search_tool_regex_20251119` server tool. Flat is the only shape, and it works.
- **Round-tripping is mandatory**. When the model invokes tool_search, the response contains a `server_tool_use` block followed by a `tool_search_tool_result` content block holding the matched definitions. Your message-history serializer must preserve both blocks when constructing the next request. If you only handle `text`, `tool_use`, and `tool_result`, you will silently lose the loaded tool definitions and the model will re-invoke tool_search on every turn, doubling your cost. For a reference implementation see [donkeywork-agents `AnthropicProvider.cs:618`](https://github.com/andyjmorgan/donkeywork-agents/blob/main/src/actors/DonkeyWork.Agents.Actors.Core/Providers/Anthropic/AnthropicProvider.cs#L618).
- **Loaded definitions are sticky**. They accumulate in the transcript over the lifetime of the session and effectively re-inline themselves over a long conversation. Plan an eviction policy for long sessions. Anthropic's `context-management-2025-06-27` beta can prune tool-result-class blocks by age.
- **Caching is opt-in**. You must annotate the BM25/regex tool block (or the system prompt above it) with `cache_control` breakpoints to get the cached-token discount.

### OpenAI

- **Model gating**: tool_search is `gpt-5.4` and later only. Older models reject the `tool_search` tool type.
- **No `count_tokens` endpoint**. You cannot estimate cost without making a real call. `tool_choice: none` and `max_output_tokens: 16` is the cheapest shim.
- **Prompt caching is automatic**. The inline 21-tool live run shows `cached_tokens: 1408` on turn 2, meaning the bulky tool block was recognised and 92% of it was billed at the cached rate (roughly 10% of normal). This narrows the apparent gap between inline and deferred for steady-state workloads.
- **Path-style retrieval**. The `tool_search_call` arguments in the logs show `paths: ["acme_ops/monitoring_list_incidents", ...]` rather than free-text queries. The model navigates by namespace path, so your namespace structure is doing the retrieval work. Burying everything in a single namespace called `tools` gives the search nothing to discriminate on.
- **Multiple namespaces are allowed and encouraged**. Group tools by domain (`acme_hr`, `acme_finance`, `acme_monitoring`) and the model picks one. This gives you tighter retrieval and easier per-domain access control.
- **Documentation is sparse**. Parts of the namespace shape used here were reverse-engineered from API error messages. Expect changes before GA.

## How to integrate

The integration shape is the same on both sides: a normal agent loop with two new content-block types to handle. Your existing `tool_use` / `tool_result` round-trip stays exactly the same.

1. **Construct the tools array correctly.** Anthropic: every existing tool gains `defer_loading: true`, plus a `tool_search_tool_bm25_20251119` or `tool_search_tool_regex_20251119` entry. OpenAI: wrap functions in a `namespace`, set `defer_loading: true` on each child function, add `{"type": "tool_search"}` as a sibling.
2. **Handle the new server-tool blocks in your message serializer.** When you walk the assistant's response content you will now also see `server_tool_use` and `tool_search_tool_result` (Anthropic) or `tool_search_call` and `tool_search_output` (OpenAI). These are paired, server-executed blocks. You do not provide a result for them, but you must round-trip them into the next request's history for the loaded definitions to persist.
3. **Nothing else changes.** The model still emits a regular `tool_use` block for the matched local tool, you execute it as before, you reply with a regular `tool_result`. The agent loop is unchanged.

Minimal Anthropic shape using the official C# SDK:

```csharp
var bm25 = new BetaToolSearchToolBm25_20251119 { Type = "tool_search_tool_bm25_20251119" };

var tools = new List<BetaToolUnion> { bm25 };
foreach (var t in myCatalog) {
    tools.Add(new BetaTool {
        Name = t.Name,
        Description = t.Description,
        InputSchema = MakeSchema(t.Schema),
        DeferLoading = true,
    });
}

httpClient.DefaultRequestHeaders.Add("anthropic-beta", "advanced-tool-use-2025-11-20");

// In your assistant content walker, alongside text/tool_use:
if (block.TryPickServerToolUse(out var stu)) {
    var raw = JsonSerializer.Serialize(stu);
    var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw)!;
    assistantBlocks.Add(BetaServerToolUseBlockParam.FromRawUnchecked(dict));
}
else if (block.TryPickToolSearchToolResult(out var tsr)) {
    var raw = JsonSerializer.Serialize(tsr);
    var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw)!;
    assistantBlocks.Add(BetaToolSearchToolResultBlockParam.FromRawUnchecked(dict));
}
```

Minimal OpenAI shape using raw REST:

```csharp
var nsTools = new JsonArray();
foreach (var t in myCatalog) {
    nsTools.Add(new JsonObject {
        ["type"] = "function",
        ["name"] = t.Name,
        ["description"] = t.Description,
        ["parameters"] = JsonNode.Parse(t.Schema),
        ["defer_loading"] = true,
    });
}

var tools = new JsonArray {
    new JsonObject {
        ["type"] = "namespace",
        ["name"] = "acme_ops",
        ["description"] = "ACME Corp internal operations toolset",
        ["tools"] = nsTools,
    },
    new JsonObject { ["type"] = "tool_search" },
};

// In your output-item walker, alongside message/function_call:
case "tool_search_call":    // server-side, no action needed
case "tool_search_output":  // server-side result
    input.Add(JsonNode.Parse(itemObj.ToJsonString())!);
    break;
```

Both code paths live in `csharp/anthropic/Program.cs` and `csharp/openai/Program.cs` in this repo, and are mirrored in Python at `python/anthropic/anthropic_test.py` and `python/openai/openai_test.py`.

## Updating your agent loop

The tool array changes are the easy part. The agent loop changes are where most teams will trip up, because tool search introduces new content-block types that have to be handled across turns or the loaded tool definitions silently disappear and the model re-pays the search cost on every turn. This section walks through what changes and what does not.

### What does not change

- **The outer loop shape.** You still send a request, walk the response, execute any local tools, append results, and recurse until the model emits an `end_turn` stop reason. The control flow is identical.
- **Local tool execution.** When the model emits a normal `tool_use` block (Anthropic) or `function_call` item (OpenAI), you execute it the same way as before and reply with a `tool_result` (Anthropic) or `function_call_output` item (OpenAI).
- **The tools array on every request.** You still send the catalog on every turn. The deferred entries are stable, so you can reuse the same array across turns without rebuilding it.
- **The system prompt and messages.** No new fields are required on the request beyond the tools array.

### What changes

A pre-tool-search agent loop typically iterates the assistant content like this:

```
for each block in response.content:
    if block is text:        accumulate to display
    if block is tool_use:    schedule for execution
    if block is thinking:    optionally surface
    (done)
```

With tool search enabled, the same iterator must now handle two additional block types per provider, and they come in matched pairs.

**Anthropic** introduces:

- `server_tool_use` — the model's call to BM25 or regex. Contains the query/pattern as input. Server-executed.
- `tool_search_tool_result` — the matched tool definitions, returned by the server immediately after the corresponding `server_tool_use`.

**OpenAI** introduces:

- `tool_search_call` — the model's search invocation, with `paths` arguments derived from your namespace structure.
- `tool_search_output` — the matched function definitions, returned immediately after the corresponding `tool_search_call`.

The new loop shape is:

```
for each block in response.content:
    if block is text:                    accumulate to display
    if block is tool_use:                schedule for execution
    if block is server_tool_use:         preserve in assistant content (no client action)
    if block is tool_search_tool_result: preserve in assistant content (no client action)
    if block is thinking:                optionally surface
    (done)
```

The two new block types do not require any client-side action. The provider has already executed them. Your only job is to **preserve them, in order, alongside the rest of the assistant's content** when you append the assistant turn to the conversation history.

### The pairing rule

`server_tool_use` and `tool_search_tool_result` are emitted as a matched pair, in that order. Same on the OpenAI side with `tool_search_call` and `tool_search_output`. **You must round-trip both blocks together.** The provider validates this on the next request: if you send the call without the result, or the result without the call, the API rejects the request as malformed.

In practice this means:

- Do not filter or rewrite assistant content in transit. Pass through every block in order.
- If you compact or summarise old turns to reduce context, evict the entire pair atomically. Never split them.
- If your message store has a typed model (e.g., separate columns for `text_content`, `tool_uses`, `tool_results`), add new columns for the server-tool blocks rather than dropping them on the floor. The donkeywork-agents reference at `AnthropicProvider.cs:618` shows the round-trip code on Anthropic.

### Multi-turn behaviour

The model will issue a fresh `tool_search` call any time a new turn needs a tool that has not been loaded yet. Each new call appends another (`server_tool_use`, `tool_search_tool_result`) pair to the assistant content for that turn. Over the lifetime of a session, the loaded set grows monotonically, and the cost of each subsequent turn is the baseline plus the search-tool entry fee plus all the previously loaded definitions sitting in the transcript.

A few consequences:

- **Search cost is paid once per tool, not once per turn.** The first turn that needs a tool pays for the search. Every subsequent turn pays the (much smaller) cost of the loaded definitions in transcript history.
- **The loop does not need to know any of this.** The model decides when to search, the provider executes it, and your loop just round-trips the resulting blocks. There is no client-side bookkeeping to track which tools are loaded.
- **`stop_reason: tool_use` may now indicate either a local tool call or a search call followed by a local tool call in the same turn.** Both of those resolve to the same client behaviour: walk the content, execute any local tools, append results, recurse. Do not branch on whether the turn contained a search call.

### Eviction and pruning

For long sessions on Anthropic, the loaded `tool_search_tool_result` blocks are sticky and will accumulate. After enough turns the deferred catalog has effectively re-inlined itself in the transcript. You have three options:

1. **Do nothing.** Acceptable if your sessions are short or your catalog is small enough that the accumulated cost stays bounded.
2. **Prune by age.** Drop assistant turns older than N from the transcript. Make sure you drop the search-call/search-result pair atomically, and ideally also drop any local `tool_use`/`tool_result` pairs that referenced tools loaded by those evicted searches.
3. **Use Anthropic's `context-management-2025-06-27` beta.** This is a server-side compaction feature that prunes tool-result-class blocks (including `tool_search_tool_result`) by age. It composes cleanly with deferred tool loading.

OpenAI is less affected because the namespace handle stays small and the loaded function definitions are also more compact. For most workloads you can defer this concern.

### Streaming considerations

If you stream responses (most production agents do), the new block types arrive as `content_block_start` and `content_block_delta` events on Anthropic, and as item events on OpenAI's stream. Your stream handler needs new branches:

- **Anthropic**: in your `content_block_start` handler, branch on `BetaToolUseBlock` (existing), `BetaServerToolUseBlock` (new), `BetaToolSearchToolResult` (new), and `BetaWebSearchToolResult` (existing if you also use web search). The donkeywork-agents `AnthropicProvider.StreamCore` method has a working example covering all these block types.
- **OpenAI**: the Responses API stream emits `response.output_item.added` events for each output item. New item types to handle: `tool_search_call` and `tool_search_output`. They do not have content deltas (the server tool runs synchronously inside the turn) but they do have `id` fields you should preserve for round-tripping.

The non-streaming code path is much simpler because you only walk `response.content` once at the end. If you can get away with non-streaming for the agent loop and stream only the final user-facing text, that is by far the easiest integration path.

### Error handling

Tool search can fail in a few ways:

- **Empty match.** The model issues a search and the server returns no matches. The model usually narrates this and tries another search with different terms. You see a `tool_search_tool_result` block with no loaded tools, then another `server_tool_use` block. No client action needed beyond round-tripping both pairs.
- **Search-tool error.** Rare, but the result block can carry an error payload. Surface it in your logs. The model will typically narrate and retry.
- **Unknown tool call.** The model occasionally hallucinates a tool name that isn't in any matched set. Handle this exactly as you would handle an unknown tool name on a normal `tool_use` block: return an error in the `tool_result` and let the model recover. This is rare in practice.

### Diff against your existing loop

If you have an agent loop today that handles `text`, `tool_use`, and `tool_result`, the minimum change to support tool search is:

1. Extend your assistant-content walker with two new branches that preserve the new server-tool block types verbatim. No execution, no parsing, just preserve.
2. Make sure your message-history serializer round-trips those blocks unchanged, in order, alongside everything else.
3. Mark your existing tools with `defer_loading: true` (Anthropic) or wrap them in a namespace (OpenAI).
4. Add the search tool to your tools array.

That is it. Local tool execution, the outer loop, the stop-reason handling, and the system prompt all stay exactly the same. Most agent loops can be updated in well under 100 lines of code.

## MCP servers and tool search

If your tools come from MCP servers, the integration story is meaningfully different on each provider, and most existing MCP server descriptions are now wrong in a way that costs you money.

**On OpenAI**, MCP servers are first-class citizens for tool search. You set `defer_loading: true` on the MCP server tool definition itself, and the server's `name` and `description` from its `initialize` handshake become the only thing the model sees until tool_search fires. The server is effectively pre-built as a namespace, with one fixed-cost entry instead of N per-tool stubs. The OpenAI docs explicitly recommend this pattern over manually wrapping functions in a `namespace`.

Five domain-scoped MCP servers (HR, finance, monitoring, ticketing, CRM) with ten tools each cost roughly `5 × 80 + 399 ≈ 800` tokens of static overhead with `defer_loading`, versus roughly 3,100 tokens for the same 50 functions inline. The model picks which server to search based on the descriptions, then loads only the matched tool definitions.

**On Anthropic**, there is no MCP-server-as-unit concept. BM25 and regex both rank across all tool descriptions globally regardless of which server they came from, and `defer_loading: true` is set on each individual tool. Server boundaries are invisible to retrieval. The *tool* descriptions are doing the work, not the server descriptions. The benefit of grouping by MCP server is operational (auth, sandboxing, provider rotation) rather than retrieval-driven.

The practical consequences:

- **Most MCP servers in the wild have terse one-liner descriptions** like "various utilities" or "ACME tools" that were fine when the model saw the full tool list anyway. With tool_search on OpenAI, those descriptions are now retrieval prompts: the model decides whether to even look inside your server based on them. Rewrite them to include the domain, the entities, the verbs, and the words your users will actually say. "Incidents, alerts, on-call schedules, and metrics for ACME production services" beats "monitoring stuff" every time.
- **Server granularity is now an architectural decision.** One mega-MCP-server with 200 tools gives the model nothing to discriminate on. Five domain-scoped servers give it a real choice. If you control your own MCP servers, split them by domain. If you are consuming third-party servers, prefer the ones that scope tightly.
- **The same MCP catalog is more efficient on OpenAI than on Anthropic** for the deferred case, because OpenAI gets the per-server fixed cost while Anthropic still considers every individual tool during retrieval. This is the opposite of how the providers compare on most other dimensions.
- **On Anthropic, write tool descriptions for keyword matching.** Synonyms matter. If your monitoring server has a tool called `list_incidents` with description "Returns paging events from the on-call system", BM25 will not match a user query about "incidents" unless you put the word "incidents" in the description. Include both vocabularies. Regex is more lenient here if you can rely on naming conventions.
- **Cross-provider portability is complicated by this asymmetry.** A well-tuned MCP catalog for OpenAI tool_search (great server descriptions, dense per-server tool lists) is not automatically well-tuned for Anthropic BM25 (great per-tool descriptions, vocabulary-rich). If you support both providers, you need both pieces.

If you are building MCP servers today, the highest-leverage thing you can do is rewrite your server description to read like a retrieval prompt and your tool descriptions to read like BM25 documents. The default templates from most MCP server scaffolding optimise for human readability, not for either of these.

## Operational tips

A few non-obvious things worth keeping in mind once the integration is working.

- **Sort your tool catalog deterministically.** On both providers, deferred tool blocks are most effective when combined with stable request prefixes. Interpolating per-request data into tool descriptions, or iterating over an unordered collection, blows prompt-caching hit rates. Sort by name and be done with it.
- **Combine with prompt caching.** On Anthropic, put a `cache_control` breakpoint at the end of the tool block or the system prompt. On OpenAI it is automatic, but verify it is firing by checking `usage.input_tokens_details.cached_tokens` in responses. Cached input is billed at roughly 10% of normal on both sides.
- **Measure on the live run, not the count.** Both providers' `count_tokens` pathways underestimate the deferred path because they do not simulate the search firing. The post-search injection on Anthropic was double the `count_tokens` estimate in this test.
- **Budget for narration.** Models burn 50 to 200 output tokens narrating their search on the first turn. A short system instruction suppressing narration ("do not announce tool searches, just execute them") recovers most of that cost with no apparent reliability impact.
- **Multiple search calls per session are fine.** The model will issue another `tool_search_call` if a later turn needs a tool it has not loaded yet. Total cost grows linearly with the number of distinct tools used, not with the size of the catalog.

## Why not just do this client-side?

The obvious alternative to server-side tool search is to do retrieval client-side: take the user query, run your own filtering, and send only the relevant tools inline. This is what most "tool search MCP" projects on r/mcp are doing. There are two distinct ways to implement it and they have very different cost profiles.

### Approach 1: pre-filter before the first call

You take the user query, run local retrieval (BM25 index, embedding similarity, LLM router), pick the top N tools, and send only those inline. The model never sees the deferred catalog, never round-trips through a search tool. There is no extra round-trip with the model, just one extra step on your side before the API call.

If your retrieval is good and you pick 5 out of 21 tools correctly, the per-turn cost on Sonnet 4.6 is roughly `80 + 5 * 151 ≈ 835` input tokens, which is **lower than server-side regex**. If retrieval works, this is the cheapest possible shape.

The catches:

- **Retrieval has its own cost.** Local BM25 or embedding similarity is essentially free at runtime but you pay maintenance and infrastructure. An LLM router costs around $0.0001 per request on a small model. Embedding-based retrieval needs an embedding API call per query and a pre-embedded tool index.
- **The user message alone is rarely a good retrieval query.** Conversations have context. Turn 1 says "tell me about ducks" and turn 2 says "cool, tell me more". The string "cool, tell me more" carries no signal for a retrieval system, but the model needs the same `wildlife_lookup` tool it used on turn 1. To pick the right tools you have to feed the *whole conversation* (or a model-summarised version of it) into the retrieval step on every turn. That means either running an LLM router that reads the transcript (expensive, slow) or maintaining a separate context-tracking layer that summarises the conversation into a retrieval query. Either way it is its own piece of infrastructure with its own failure modes. Server-side tool search avoids this entirely because the model itself is composing the search query with full conversation context already in scope.
- **Multi-turn behaviour breaks.** Server-side tool search persists loaded tools across turns via the transcript. With pre-filtering you have to decide on every turn what tools to send, and if turn 2 needs a tool turn 1 didn't, your choices are all bad: swap the tools array mid-conversation (undefined behaviour, the model may emit calls to tools it can no longer see), accumulate every tool ever picked (re-inlines your catalog), or re-run retrieval over the whole transcript (slow and expensive).
- **Prompt caching dies.** A stable inline tool block caches well. A dynamic per-request tool block does not. You lose the roughly 90% discount on cached input across the entire tool block, on every request, for the lifetime of the session. For high-volume workloads this can dwarf any per-request savings from sending fewer tools.
- **Wrong picks are unrecoverable.** If your retrieval misses the right tool, the model just cannot do the task. It does not know to ask for more tools because it does not know they exist. Server-side tool search lets the model issue another search if the first one missed.

### Approach 2: search as a client-side tool

You expose a `search_tools` function tool to the model. The model emits a tool call, your client runs retrieval, returns the matched tool definitions in the tool result, and the model picks one and calls it. This is what most "tool search MCP" projects are doing.

**This is the round-trip case, and yes, it adds full extra request-response cycles to every search.**

For a single "search then call" task the model produces a `search_tools` call on turn 1, you round-trip retrieval as a `tool_result`, the model parses the matched definitions out of natural language on turn 2 (or you mutate the tools array between turns, which breaks caching), and the model finally calls the matched tool on turn 3. Compared to server-side tool search you pay at least one extra full request-response cycle, the matched tool definitions live in the transcript as text on every subsequent turn (no eviction story unless you build one), and prompt caching is broken because the tools array keeps changing.

For our 21-tool task, modelled cost lands around 5,000 to 6,000 combined tokens. Worse than Anthropic's BM25 result and only marginally better than inline. Nowhere near regex tool search.

### When client-side actually wins

Pre-filter is better than server-side tool search if all four of these are true:

1. Your retrieval is reliable enough that misses are rare and recoverable. Usually means a curated catalog with a controlled query distribution, not a long-tail enterprise mess.
2. Your conversations are short (one or two turns) so the lack of cross-turn persistence does not matter.
3. You can absorb the loss of prompt caching, or you have enough request volume to make caching irrelevant.
4. You are willing to maintain the retrieval system (and a conversation-context summariser) as long-term pieces of your stack.

Server-side tool search is better if any of these hold:

1. Conversations are multi-turn and the loaded set evolves.
2. You want the model to be able to recover from a missed first match by issuing another search.
3. You want prompt caching to amortise the search cost across requests in a session.
4. You don't want to operate a retrieval index or a context summariser.

For most production agent workloads those points all favour server-side. The exception is genuinely large catalogs (200+ tools across many domains) where neither BM25 nor namespace-path retrieval is precise enough and you need a hand-tuned router that knows your domain. That is a real use case but it is not the median.

The short answer: doing tool search client-side as a tool round-trip adds a full extra round-trip of cost and breaks prompt caching, which together usually wipes out any token savings. Pre-filtering can be cheaper but only in single-turn workloads with reliable retrieval, and you trade away robustness, recoverability, conversation-context handling, and cache benefits to get there.

## Cross-provider summary

For the same 21-tool catalog and the same agent task, combined input + output tokens across the full two-turn live run, with cost per 1 million conversations at list pricing. **All numbers are dry-run cost (no cache discount applied)** so the comparison is fair to Anthropic, which has no `cache_control` breakpoints set in this test:

| Provider | Best inline | Best deferred | Inline cost / 1M | Deferred cost / 1M | Saving |
|---|---:|---:|---:|---:|---:|
| Anthropic Sonnet 4.6 (BM25) | 7,041 | 4,160 | $25,407 | $17,916 | $7,491 (29%) |
| Anthropic Sonnet 4.6 (regex) | 7,041 | 4,419 | $25,407 | $18,261 | $7,146 (28%) |
| OpenAI gpt-5.4 (namespace) | 3,291 | **1,921** | $9,715 | **$6,490** | **$3,225 (33%)** |

Observations beyond the headline numbers:

- **OpenAI gpt-5.4 is roughly 2.6 times cheaper than Anthropic Sonnet 4.6 for this workload at list pricing**, before either side enables tool search. Most of that gap is the lower per-tool serialization cost (~51% per tool, see the per-tool cost analysis section) combined with a slightly cheaper input rate.
- **The dollar savings from tool search are larger on Anthropic in absolute terms** (~$7,500 versus $3,225 on OpenAI), even though OpenAI's tool block is smaller to start with. Anthropic users have more to gain because their inline cost is higher.
- **BM25 vs regex on Anthropic is run-to-run noisy.** This particular run had BM25 narrowly ahead. Other runs have regex ahead by up to 35%. Treat them as roughly equivalent on this catalog and run your own measurements before committing to either.
- **Prompt caching changes the OpenAI ranking entirely.** The inline OpenAI run shows ~92% of input tokens served from cache automatically. With cache discount applied (cached input billed at ~10% of normal), the inline path drops to roughly $3,400 per 1M conversations, **cheaper than namespace-deferred** because namespace-deferred falls below the 1,024-token cache threshold and gets zero caching at all. The "namespace saves 33%" headline only holds on cold-cache, dry-run measurements. On a warm-cache workload the relationship inverts.
- **Anthropic has no caching in this test by design.** Anthropic's prompt cache is opt-in via `cache_control` breakpoints which I have not added. The Anthropic numbers are honest full-price. With breakpoints set on the system prompt or tool block, Anthropic can also achieve ~90% cached input on warm runs, which would similarly close the inline-vs-deferred gap.
- **At volume the dry-run savings compound fast.** An agent platform doing 10 million conversations a month would save ~$74,910 on Anthropic and $32,250 on OpenAI by switching from inline to deferred tools, before any prompt-caching effects.

The providers have different strengths beyond raw price. Anthropic gives you two retrieval primitives (BM25 and regex) to pick from, both forgiving on heterogeneous catalogs if chosen correctly. OpenAI's namespace-path approach is faster and cheaper at the deferred entry fee, but the static cost is small enough to fall below the cache threshold, which makes it a net loss on warm-cache workloads.

## Caveats

- All numbers are from a single task on a single 21-tool catalog. The "best deferred" winner depends on the ratio of total catalog size to tools-actually-needed-per-turn. A workload that needs a different tool every turn defeats both deferred-loading mechanisms because each turn pays the search cost again.
- **Regex vs BM25 on Anthropic varies significantly run to run.** I have captured runs where regex wins by up to 35%, runs where they tie, and runs where BM25 wins narrowly. The model is non-deterministic about how it issues the search query. Single-measurement claims about which variant wins are not reliable. Run multiple iterations against your real catalog before committing.
- **OpenAI prompt caching changes the inline-vs-deferred ranking.** OpenAI's automatic prompt cache requires a stable prefix of at least 1,024 tokens. Inline 21 tools (1,532 tokens) crosses the threshold and caches; namespace-deferred (475 static, 843 turn-1) does not, and gets zero cache hits. In steady state with the cache discount applied, inline becomes cheaper than namespace-deferred. The dry-run numbers in this writeup do not apply that discount, so the headline "33% saving" only holds on cold caches.
- **Anthropic gets no caching at all in these numbers** because I have not added `cache_control` breakpoints. The Anthropic numbers are full-price honest, but they are not directly comparable to OpenAI's *steady-state-with-cache* cost. To put both providers on equal footing in steady state, you would need to add Anthropic cache breakpoints and rerun.
- **OpenAI's `gpt-5.4` is brand new and the tool_search feature is partly undocumented.** The namespace shape used here was reverse-engineered from API error messages and may change before GA.
- **Anthropic's `count_tokens` endpoint underestimates the deferred path** because it does not simulate the search firing. The live run is the only way to see the real post-search cost.
- **ChatCompletions cannot use server-side tool search at all.** It is Responses-only on OpenAI. Most production OpenAI integrations are still on ChatCompletions and have to migrate to Responses before any of the deferred-tool story applies.

## Reproducing

All harnesses are Python with stdlib only, no external dependencies. Run from each subdirectory:

| Harness | Command | Required env |
|---|---|---|
| Anthropic | `cd python/anthropic && python3 anthropic_test.py` | `ANTHROPIC_API_KEY` |
| OpenAI Responses | `cd python/openai && python3 openai_test.py` | `OPENAI_API_KEY` |
| Cross-provider scale test | `cd python && python3 scale_test.py` | both keys |

The Anthropic and OpenAI harnesses each write a `flow.json` (or `flow_anthropic.json` / `flow_openai.json`) alongside the script, containing full request and response bodies for every HTTP exchange, tagged by scenario, suitable for `jq` inspection. The scale test writes its results to stdout only and prints the linear-fit numbers used in the per-tool cost analysis section.
