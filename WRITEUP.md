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

A single agent task (*"Find any open P1 incidents from the last 24 hours and tell me which service they belong to"*) executed against a catalog of 21 verbose, realistic tool definitions covering HR, finance, inventory, ticketing, deployments, monitoring, customers, calendar, and docs. Each tool has a multi-sentence description and a JSON Schema, shaped like a real enterprise toolset rather than stubs. The full catalog lives in `csharp/anthropic/Program.cs` in the `MockTools` class and is duplicated verbatim in the other harnesses.

Two providers, four harnesses:

- **Anthropic** Claude Sonnet 4.6 (`claude-sonnet-4-6`) via the Beta Messages API. Beta header `advanced-tool-use-2025-11-20`. C# harness uses the official `Anthropic` NuGet SDK, Python harness uses stdlib `urllib`.
- **OpenAI** `gpt-5.4-2026-03-05` via the Responses API. Both harnesses hit the REST endpoint directly because the published SDKs had not yet been updated for `tool_search` when I ran this.

Each provider was measured in two modes:

1. **Token counting**, to isolate the static cost of each configuration. Anthropic exposes a `count_tokens` endpoint. OpenAI does not, so I make a real `/v1/responses` call with `tool_choice: none` and `max_output_tokens: 16` and read `usage.input_tokens` off the response.
2. **Live agent run**, a real two-turn call where the model actually invokes a local tool, the harness returns a mock result, and the model produces a final answer. This is the only way to see the post-search cost (the cost of definitions that the search tool has just injected mid-turn).

For each provider I measured: prompt only, prompt + 21 tools inline, prompt + 21 tools deferred, plus 3-tool variants to expose the slope. On Anthropic I measured both BM25 and regex deferred modes. On OpenAI I measured both the flat per-function `defer_loading` and the namespace-wrapped form.

All requests and responses are captured to `flow.json` files in each harness directory.

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

Live two-turn agent run on Sonnet 4.6:

| Configuration | Total in | Total out | Combined | Cost / 1M conv | Saving |
|---|---:|---:|---:|---:|---:|
| Inline 21 | 6,684 | 364 | 7,048 | **$25,512** | — |
| Deferred BM25 | 4,690 | 493 | 5,183 | **$21,465** | **$4,047 (16%)** |
| Deferred regex | **3,388** | **396** | **3,784** | **$16,104** | **$9,408 (37%)** |

What the live run reveals that `count_tokens` cannot:

- **Deferred turn 1 is much larger than the 765-token static count.** The extra is the **post-search injection**: tool_search actually fires mid-turn, matches a handful of tools, and their definitions are loaded into the same turn before the model produces its tool call. BM25 turn 1 came in around 2,500 tokens, regex around 2,000.
- **Regex returned a smaller matched set than BM25.** BM25 ranked several monitoring and ticketing candidates and loaded them all. Regex issued `{"pattern": "incident"}` and the server returned only the tools whose names actually contained that substring. That precision compounds across turns.
- **Turn 2 input drops sharply for both deferred variants** because only the matched tool definitions persist in the transcript via the `tool_search_tool_result` content block. The other tools are gone forever.
- **Output tokens are notably higher on the deferred path** because the model narrates its search ("Let me find the right tool to query incidents…"). Sonnet 4.6 narrates more than 4.5 did in my earlier runs, around 150 to 250 tokens per search invocation. You can suppress this with a short system instruction, at some risk to reliability.
- **BM25 savings collapsed from 25% on Sonnet 4.5 to 16% on Sonnet 4.6 on the same task.** Sonnet 4.6 issues broader BM25 queries and loads more candidate tools per search than 4.5 did. Regex is unaffected because the pattern stays narrow regardless of model verbosity. If you are on 4.6 and your catalog supports it, regex is now the clear default.

### BM25 versus regex

**Regex beats BM25 by a wide margin on this task on Sonnet 4.6** ($9,408 vs $4,047 saved per million conversations), despite its 25-token-higher entry fee. The reason is precision:

- **BM25** issued a free-text query like `{"query": "P1 incidents open last 24 hours"}` and the server returned several ranked candidates across monitoring and ticketing. All of them got injected.
- **Regex** issued `{"pattern": "incident"}` and the server returned only the tools whose names actually contained that substring. The injected set was much smaller.

That precision is a double-edged sword. Regex requires the model to write a pattern that hits your catalog's actual naming. A typo, a case mismatch, or a vocabulary mismatch returns nothing and the model has to search again. For a well-curated internal catalog with consistent naming (every monitoring tool prefixed with `monitoring_`, every ticket tool with `ticket_`, and so on) regex is the right default because the model can infer the pattern from the prefix convention. For a heterogeneous catalog assembled from many MCP servers with inconsistent naming, BM25's ranked keyword matching is more forgiving.

My recommendation: default to regex if you control the catalog and can enforce naming conventions. Default to BM25 if your catalog is assembled from third-party sources or if you cannot guarantee naming discipline. Nothing stops you from offering both tools in a single request; the model will pick one per turn.

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

Live two-turn agent run:

| Configuration | Total in | Total out | Combined | Cost / 1M conv | Saving |
|---|---:|---:|---:|---:|---:|
| Inline 21 | 3,172 | 118 | 3,290 | **$9,700** | — |
| Deferred flat 21 | 4,132 | 125 | 4,257 | **$12,205** | **−$2,505 (worse)** |
| Deferred namespace 21 | **1,786** | **137** | **1,923** | **$6,520** | **$3,180 (33%)** |

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

## Cross-provider summary

For the same 21-tool catalog and the same agent task, combined input + output tokens across the full two-turn run, with cost per 1 million conversations at list prices:

| Provider | Best inline | Best deferred | Inline cost / 1M | Deferred cost / 1M | Saving |
|---|---:|---:|---:|---:|---:|
| Anthropic Sonnet 4.6 (BM25) | 7,048 | 5,183 | $25,512 | $21,465 | $4,047 (16%) |
| Anthropic Sonnet 4.6 (regex) | 7,048 | **3,784** | $25,512 | **$16,104** | **$9,408 (37%)** |
| OpenAI gpt-5.4 (namespace) | 3,290 | **1,923** | $9,700 | **$6,520** | $3,180 (33%) |

Observations beyond the headline numbers:

- **OpenAI gpt-5.4 is roughly 2.6 times cheaper than Anthropic Sonnet 4.6 for this workload at list pricing**, before either side enables tool search. Most of that gap is the lower per-tool serialization cost combined with a slightly cheaper input rate.
- **The dollar savings from tool search are larger on Anthropic in absolute terms** ($9,408 with regex, $4,047 with BM25, versus $3,180 on OpenAI), even though OpenAI's tool block is smaller to start with. Anthropic users have more to gain because their inline cost is higher.
- **Anthropic's regex variant is the biggest percentage saving on this task** (37%), narrowly ahead of OpenAI's namespace mode (33%) and well ahead of BM25 (16%). The BM25 result on Sonnet 4.6 is notably worse than what 4.5 achieved on the same task because 4.6 issues broader queries and loads more candidates per search. Regex is unaffected by this behavioural change.
- **Prompt caching narrows the gap further on the inline side.** The inline OpenAI run shows `cached_tokens: 1408` automatically, billed at roughly 10% of normal. A steady-state inline workload with good cache hit rates is much closer in real cost to a deferred workload than the raw numbers suggest. On Anthropic you must add `cache_control` breakpoints to get the same effect.
- **At volume the savings compound fast.** An agent platform doing 10 million conversations a month would save $94,080 a month on Anthropic with regex and $31,800 a month on OpenAI by switching from inline to deferred tools, before any prompt-caching effects.

The providers have different strengths beyond raw price. Anthropic's BM25 and regex give you two retrieval primitives to pick from, both forgiving on heterogeneous catalogs if chosen correctly. OpenAI's namespace-path approach is faster and cheaper but assumes you can structure your catalog hierarchically.

## Caveats

- All numbers are from a single task on a single 21-tool catalog. The "best deferred" winner depends on the ratio of total catalog size to tools-actually-needed-per-turn. A workload that needs a different tool every turn defeats both deferred-loading mechanisms because each turn pays the search cost again.
- **Regex vs BM25 on Anthropic is both model-dependent and catalog-dependent.** Regex won on this task because the model inferred a clean `incident` pattern from the catalog's naming convention, and because Sonnet 4.6 happens to issue broad BM25 queries. Older Sonnet 4.5 closed the BM25 gap to ~25%. On a catalog where the model has to guess the naming, regex will miss and fall back to another search. Measure both against your own catalog and your target model before committing.
- OpenAI's `gpt-5.4` is brand new and the tool_search feature is partly undocumented. The namespace shape used here was reverse-engineered from API error messages and may change before GA.
- Anthropic's `count_tokens` endpoint underestimates the deferred path because it does not simulate the search firing. The live run is the only way to see the real post-search cost.
- Prompt caching skews any inline-vs-deferred comparison in the deferred path's disfavour over time. A stable inline tool block with caching enabled may be cheaper per request in steady state than a deferred block that pays the search cost on the first request of each new conversation. Measure your real workload.

## Reproducing

| Harness | Run from | Required env |
|---|---|---|
| Anthropic C# | `csharp/anthropic/` | `ANTHROPIC_API_KEY` |
| OpenAI C# | `csharp/openai/` | `OPENAI_API_KEY` |
| Anthropic Python | `python/anthropic/` | `ANTHROPIC_API_KEY` |
| OpenAI Python | `python/openai/` | `OPENAI_API_KEY` |

C# harnesses: `dotnet run`. Python harnesses: `python3 anthropic_test.py` or `python3 openai_test.py` (no dependencies, stdlib only). Each writes a `flow.json` file alongside the script containing the full request and response bodies for every HTTP exchange, tagged by scenario, suitable for `jq` inspection.
