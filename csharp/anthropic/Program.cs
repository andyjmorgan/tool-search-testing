using System.Text.Json;
using Anthropic;
using Beta = Anthropic.Models.Beta.Messages;

var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
    ?? throw new InvalidOperationException("ANTHROPIC_API_KEY not set");

const string ModelId = "claude-sonnet-4-6";
const string SystemPrompt =
    "You are a helpful enterprise operations assistant for ACME Corp. " +
    "You help employees query internal systems including HR, finance, " +
    "inventory, ticketing, deployments, monitoring, and customer data. " +
    "Always confirm potentially destructive actions before invoking tools.";
const string UserPrompt =
    "Find any open P1 incidents from the last 24 hours and tell me which " +
    "service they belong to.";

var betaHeader = Environment.GetEnvironmentVariable("ANTHROPIC_BETA")
    ?? "advanced-tool-use-2025-11-20";
var capture = new CapturingHandler(new HttpClientHandler());
var http = new HttpClient(capture) { Timeout = TimeSpan.FromMinutes(2) };
http.DefaultRequestHeaders.Add("anthropic-beta", betaHeader);
var client = new AnthropicClient { ApiKey = apiKey, HttpClient = http };
Console.WriteLine($"Using anthropic-beta: {betaHeader}");

var tools = MockTools.Build();
Console.WriteLine($"Built {tools.Count} mock tools.\n");

var userMessage = new Beta.BetaMessageParam
{
    Role = Beta.Role.User,
    Content = UserPrompt,
};

Beta.MessageCountTokensParams MakeParams(IReadOnlyList<Beta.Tool>? toolsList) => new()
{
    Model = ModelId,
    System = SystemPrompt,
    Messages = new[] { userMessage },
    Tools = toolsList!,
};

Beta.BetaTool ToInlineTool(MockTools.ToolDef t) => new()
{
    Name = t.Name,
    Description = t.Description,
    InputSchema = MakeSchema(t.Schema),
};

Beta.BetaTool ToDeferredTool(MockTools.ToolDef t) => new()
{
    Name = t.Name,
    Description = t.Description,
    InputSchema = MakeSchema(t.Schema),
    DeferLoading = true,
};

List<Beta.Tool> InlineList(IEnumerable<MockTools.ToolDef> ts) =>
    ts.Select(t => (Beta.Tool)ToInlineTool(t)).ToList();

List<Beta.Tool> DeferredList(IEnumerable<MockTools.ToolDef> ts, string searcher = "bm25")
{
    var list = new List<Beta.Tool>
    {
        searcher == "regex"
            ? new Beta.BetaToolSearchToolRegex20251119 { Type = "tool_search_tool_regex_20251119" }
            : (Beta.Tool)new Beta.BetaToolSearchToolBm25_20251119 { Type = "tool_search_tool_bm25_20251119" },
    };
    list.AddRange(ts.Select(t => (Beta.Tool)ToDeferredTool(t)));
    return list;
}

List<Beta.BetaToolUnion> InlineUnion(IEnumerable<MockTools.ToolDef> ts) =>
    ts.Select(t => (Beta.BetaToolUnion)ToInlineTool(t)).ToList();

List<Beta.BetaToolUnion> DeferredUnion(IEnumerable<MockTools.ToolDef> ts, string searcher = "bm25")
{
    var list = new List<Beta.BetaToolUnion>
    {
        searcher == "regex"
            ? new Beta.BetaToolSearchToolRegex20251119 { Type = "tool_search_tool_regex_20251119" }
            : (Beta.BetaToolUnion)new Beta.BetaToolSearchToolBm25_20251119 { Type = "tool_search_tool_bm25_20251119" },
    };
    list.AddRange(ts.Select(t => (Beta.BetaToolUnion)ToDeferredTool(t)));
    return list;
}

var fewTools = tools.Take(3).ToList();

Console.WriteLine("Counting tokens...\n");

var t1 = await Count("1. Prompt only                            ", MakeParams(null));
var t2 = await Count("2. Prompt + 21 tools inline               ", MakeParams(InlineList(tools)));
var t3 = await Count("3. Prompt + 21 tools deferred (BM25)      ", MakeParams(DeferredList(tools, "bm25")));
var t3r = await Count("3r. Prompt + 21 tools deferred (regex)    ", MakeParams(DeferredList(tools, "regex")));
var t4 = await Count("4. Prompt + 3 tools inline                ", MakeParams(InlineList(fewTools)));
var t5 = await Count("5. Prompt + 3 tools deferred (BM25)       ", MakeParams(DeferredList(fewTools, "bm25")));
var t5r = await Count("5r. Prompt + 3 tools deferred (regex)     ", MakeParams(DeferredList(fewTools, "regex")));

Console.WriteLine();
Console.WriteLine("─────────────────────────────────────────────");
Console.WriteLine($"21 tools — inline overhead         : {t2 - t1,6}");
Console.WriteLine($"21 tools — deferred BM25 overhead  : {t3 - t1,6}");
Console.WriteLine($"21 tools — deferred regex overhead : {t3r - t1,6}");
Console.WriteLine($" 3 tools — inline overhead         : {t4 - t1,6}");
Console.WriteLine($" 3 tools — deferred BM25 overhead  : {t5 - t1,6}");
Console.WriteLine($" 3 tools — deferred regex overhead : {t5r - t1,6}");

Console.WriteLine();
Console.WriteLine("═════════════════════════════════════════════");
Console.WriteLine("Live agent runs (real messages.create, mock local tool execution)");
Console.WriteLine("═════════════════════════════════════════════");

capture.CurrentScenario = "inline_21";
await RunAgent("6. Inline 21 tools (live)         ", InlineUnion(tools));
Console.WriteLine();
capture.CurrentScenario = "deferred_bm25_21";
await RunAgent("7. Deferred 21 tools (BM25, live) ", DeferredUnion(tools, "bm25"));
Console.WriteLine();
capture.CurrentScenario = "deferred_regex_21";
await RunAgent("8. Deferred 21 tools (regex, live)", DeferredUnion(tools, "regex"));

var flowPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "flow.json");
flowPath = Path.GetFullPath(flowPath);
File.WriteAllText(flowPath, JsonSerializer.Serialize(capture.Exchanges,
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"\nWrote flow log: {flowPath} ({capture.Exchanges.Count} exchanges)");

async Task RunAgent(string label, IReadOnlyList<Beta.BetaToolUnion> toolsList)
{
    Console.WriteLine($"\n--- {label} ---");
    var conversation = new List<Beta.BetaMessageParam> { userMessage };
    long totalIn = 0, totalOut = 0, totalCacheRead = 0, totalCacheCreation = 0;

    for (int turn = 1; turn <= 5; turn++)
    {
        var req = new Beta.MessageCreateParams
        {
            Model = ModelId,
            MaxTokens = 1024,
            System = SystemPrompt,
            Messages = conversation,
            Tools = toolsList,
        };

        var resp = await client.Beta.Messages.Create(req);
        var inT = resp.Usage?.InputTokens ?? 0;
        var outT = resp.Usage?.OutputTokens ?? 0;
        var cacheRead = resp.Usage?.CacheReadInputTokens ?? 0;
        var cacheCreation = resp.Usage?.CacheCreationInputTokens ?? 0;
        totalCacheRead += cacheRead;
        totalCacheCreation += cacheCreation;
        totalIn += inT;
        totalOut += outT;
        Console.WriteLine($"  turn {turn}: in={inT,6}  cache_read={cacheRead,5}  cache_create={cacheCreation,5}  out={outT,5}  stop={resp.StopReason}");

        var assistantBlocks = new List<Beta.BetaContentBlockParam>();
        var localCalls = new List<(string id, string name, string inputJson)>();

        foreach (var block in resp.Content)
        {
            if (block.TryPickText(out var textBlock))
            {
                assistantBlocks.Add(new Beta.BetaTextBlockParam { Text = textBlock.Text });
                if (!string.IsNullOrWhiteSpace(textBlock.Text))
                    Console.WriteLine($"    text: {Truncate(textBlock.Text, 120)}");
            }
            else if (block.TryPickToolUse(out var toolUse))
            {
                var inputJson = JsonSerializer.Serialize(toolUse.Input);
                var inputDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(inputJson)
                    ?? new Dictionary<string, JsonElement>();
                assistantBlocks.Add(new Beta.BetaToolUseBlockParam
                {
                    ID = toolUse.ID,
                    Name = toolUse.Name,
                    Input = inputDict,
                });
                localCalls.Add((toolUse.ID, toolUse.Name, inputJson));
                Console.WriteLine($"    tool_use: {toolUse.Name}({Truncate(inputJson, 100)})");
            }
            else if (block.TryPickServerToolUse(out var serverToolUse))
            {
                var raw = JsonSerializer.Serialize(serverToolUse);
                var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw) ?? new();
                assistantBlocks.Add(Beta.BetaServerToolUseBlockParam.FromRawUnchecked(dict));
                Console.WriteLine($"    server_tool_use: {serverToolUse.Name} {Truncate(raw, 120)}");
            }
            else if (block.TryPickToolSearchToolResult(out var searchResult))
            {
                var rawDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    JsonSerializer.Serialize(searchResult)) ?? new();
                assistantBlocks.Add(Beta.BetaToolSearchToolResultBlockParam.FromRawUnchecked(rawDict));
                Console.WriteLine($"    tool_search_result: (matched tools loaded)");
            }
        }

        conversation.Add(new Beta.BetaMessageParam
        {
            Role = Beta.Role.Assistant,
            Content = assistantBlocks,
        });

        if (localCalls.Count == 0) break;

        var userBlocks = new List<Beta.BetaContentBlockParam>();
        foreach (var (id, name, _) in localCalls)
        {
            userBlocks.Add(new Beta.BetaToolResultBlockParam
            {
                ToolUseID = id,
                Content = MockExecute(name),
            });
            Console.WriteLine($"    [local exec] {name} -> mock result");
        }
        conversation.Add(new Beta.BetaMessageParam
        {
            Role = Beta.Role.User,
            Content = userBlocks,
        });
    }

    var rawCost = totalIn * 3.0 + totalOut * 15.0;
    Console.WriteLine($"  TOTAL : in={totalIn}  cache_read={totalCacheRead}  cache_create={totalCacheCreation}  out={totalOut}  combined={totalIn + totalOut}");
    Console.WriteLine($"  COST  : ${rawCost,8:N2} / 1M conv (dry run, no cache discount)");
}

static string MockExecute(string toolName) => toolName switch
{
    "monitoring_list_incidents" =>
        "[{\"id\":\"INC-4421\",\"title\":\"checkout-api 5xx spike\",\"severity\":\"P1\",\"status\":\"open\",\"started_at\":\"2026-04-07T08:14:00Z\",\"services_affected\":[\"checkout-api\"]}]",
    _ => "{\"ok\":true,\"result\":\"mock\"}",
};

static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";

async Task<long> Count(string label, Beta.MessageCountTokensParams p)
{
    var resp = await client.Beta.Messages.CountTokens(p);
    Console.WriteLine($"  {label} : {resp.InputTokens,6}");
    return resp.InputTokens;
}

static Beta.InputSchema MakeSchema(string json)
{
    var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    return Beta.InputSchema.FromRawUnchecked(dict);
}

sealed class CapturingHandler : DelegatingHandler
{
    public string CurrentScenario { get; set; } = "";
    public List<Exchange> Exchanges { get; } = new();

    public CapturingHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? reqBody = null;
        if (request.Content is not null)
            reqBody = await request.Content.ReadAsStringAsync(cancellationToken);

        var response = await base.SendAsync(request, cancellationToken);

        var respBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var buffered = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(respBody));
        foreach (var h in response.Content.Headers)
            buffered.Headers.TryAddWithoutValidation(h.Key, h.Value);
        response.Content = buffered;

        var endpoint = request.RequestUri?.AbsolutePath ?? "";
        Exchanges.Add(new Exchange(
            CurrentScenario,
            request.Method.Method,
            endpoint,
            (int)response.StatusCode,
            TryParse(reqBody),
            TryParse(respBody)));

        return response;
    }

    static JsonElement? TryParse(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(body); }
        catch { return null; }
    }

    public record Exchange(
        string Scenario,
        string Method,
        string Endpoint,
        int Status,
        JsonElement? Request,
        JsonElement? Response);
}

static class MockTools
{
    public record ToolDef(string Name, string Description, string Schema);

    public static List<ToolDef> Build()
    {
        var list = new List<ToolDef>();
        void Add(string name, string desc, string props) =>
            list.Add(new ToolDef(name, desc, "{\"type\":\"object\",\"properties\":{" + props + "}}"));

        Add("hr_lookup_employee",
            "Look up an employee record from the HR system by employee ID, email address, or full name. Returns the employee's department, manager, hire date, job title, location, work phone, employment status, cost center, and employee classification. Use when a user needs verified personnel data, when verifying authorisation for HR-restricted actions, or when populating templates that need HR fields.",
            """ "query": {"type":"string","description":"Employee ID, email, or full name to look up"}, "include_compensation":{"type":"boolean","description":"Whether to include sensitive compensation data; requires HR-admin scope"} """);

        Add("hr_create_pto_request",
            "Create a paid-time-off request on behalf of an employee. The request will route to the employee's manager for approval and post to the team calendar. Validates remaining PTO balance before submission and supports vacation, sick, bereavement, jury duty, parental, and unpaid leave categories.",
            """ "employee_id":{"type":"string"}, "start_date":{"type":"string","format":"date"}, "end_date":{"type":"string","format":"date"}, "category":{"type":"string","enum":["vacation","sick","bereavement","jury","parental","unpaid"]}, "notes":{"type":"string"} """);

        Add("finance_get_budget",
            "Retrieve the current fiscal-year budget allocation, year-to-date spend, encumbrances, and remaining balance for a given cost center or project code. Includes monthly burn rate, projected end-of-year position, and any active spending freezes or approval thresholds.",
            """ "cost_center":{"type":"string"}, "fiscal_year":{"type":"integer"} """);

        Add("finance_submit_expense",
            "Submit an expense report for reimbursement. Accepts itemised line entries with categories, amounts, currencies, receipts (as URLs), business justification, and project allocation. Performs policy checks for per-diem limits, missing receipts, alcohol restrictions, and split-billing violations before submission.",
            """ "employee_id":{"type":"string"}, "items":{"type":"array","items":{"type":"object","properties":{"date":{"type":"string"},"amount":{"type":"number"},"currency":{"type":"string"},"category":{"type":"string"},"receipt_url":{"type":"string"}}}}, "justification":{"type":"string"} """);

        Add("inventory_check_stock",
            "Check current on-hand and available-to-promise inventory for a given SKU across one or all warehouses. Returns quantity on hand, allocated, in-transit, on-order, reorder point, lead time, and the next expected restock date.",
            """ "sku":{"type":"string"}, "warehouse_id":{"type":"string"} """);

        Add("inventory_create_transfer",
            "Create an inter-warehouse stock transfer order to move inventory from a source warehouse to a destination warehouse. Validates source availability, destination capacity, lane restrictions, and customs requirements for cross-border transfers.",
            """ "sku":{"type":"string"}, "quantity":{"type":"integer"}, "from_warehouse":{"type":"string"}, "to_warehouse":{"type":"string"} """);

        Add("ticket_search",
            "Search the support ticketing system using a free-text query, status filter, priority filter, assignee, and date range. Returns matching tickets with id, title, status, priority, requester, assignee, last update time, and SLA status.",
            """ "query":{"type":"string"}, "status":{"type":"string"}, "priority":{"type":"string"}, "assignee":{"type":"string"}, "since":{"type":"string","format":"date-time"} """);

        Add("ticket_create",
            "Create a new support ticket with title, description, requester, priority, category, and optional attachments. Auto-routes to the appropriate queue using the category taxonomy and applies SLA policy.",
            """ "title":{"type":"string"}, "description":{"type":"string"}, "requester":{"type":"string"}, "priority":{"type":"string","enum":["P1","P2","P3","P4"]}, "category":{"type":"string"} """);

        Add("ticket_update_status",
            "Update the status of an existing support ticket and optionally add a resolution comment, change assignee, or close the ticket with a resolution code. Triggers SLA recalculation and customer notification.",
            """ "ticket_id":{"type":"string"}, "status":{"type":"string"}, "comment":{"type":"string"} """);

        Add("deployment_list_environments",
            "List all deployment environments (production, staging, qa, dev, sandbox) for a given service, including their current deployed version, last deploy time, deploy actor, health status, and active feature flags.",
            """ "service":{"type":"string"} """);

        Add("deployment_trigger",
            "Trigger a new deployment of a specified build artifact (by git SHA or tag) to a target environment. Performs pre-flight checks for change-management approval, deploy windows, dependency readiness, and rollback availability.",
            """ "service":{"type":"string"}, "environment":{"type":"string"}, "ref":{"type":"string","description":"Git SHA or release tag"}, "skip_canary":{"type":"boolean"} """);

        Add("deployment_rollback",
            "Roll a service back to its previously deployed version in the specified environment. Captures the rollback reason for the post-incident review and notifies the on-call channel.",
            """ "service":{"type":"string"}, "environment":{"type":"string"}, "reason":{"type":"string"} """);

        Add("monitoring_query_metric",
            "Run a metrics query against the time-series monitoring system. Supports PromQL syntax. Use for latency percentiles, error rates, saturation, throughput, and any custom service KPI. Returns evaluated series with timestamps and values.",
            """ "promql":{"type":"string"}, "start":{"type":"string","format":"date-time"}, "end":{"type":"string","format":"date-time"}, "step":{"type":"string"} """);

        Add("monitoring_list_incidents",
            "List active and recent incidents from the on-call/paging system. Filters by severity (P1-P5), status (open, acknowledged, resolved), service, and time window. Returns incident id, title, severity, status, started_at, services_affected, and current responder.",
            """ "severity":{"type":"string"}, "status":{"type":"string"}, "since":{"type":"string","format":"date-time"} """);

        Add("monitoring_acknowledge_incident",
            "Acknowledge a paging incident, claiming responder ownership and silencing further re-pages for the configured grace window. Posts an ack to the incident channel.",
            """ "incident_id":{"type":"string"}, "responder":{"type":"string"} """);

        Add("customer_lookup",
            "Look up a customer account from the CRM by customer id, email, domain, or company name. Returns the account owner, tier, MRR, contract end date, primary contacts, support entitlements, and any active escalations.",
            """ "query":{"type":"string"} """);

        Add("customer_add_note",
            "Append a timestamped account note to a customer record visible to all account-team members. Supports markdown and @mentions of internal users.",
            """ "customer_id":{"type":"string"}, "note":{"type":"string"} """);

        Add("calendar_find_slot",
            "Find a meeting slot that works for a list of attendees within a given window. Honours working hours, declared focus blocks, and timezone preferences. Returns the earliest mutually-available slot of the requested duration.",
            """ "attendees":{"type":"array","items":{"type":"string"}}, "duration_minutes":{"type":"integer"}, "earliest":{"type":"string","format":"date-time"}, "latest":{"type":"string","format":"date-time"} """);

        Add("calendar_book_meeting",
            "Create a calendar event with attendees, title, description, location/conferencing link, and optional recurrence rule. Sends invites and adds to attendees' calendars.",
            """ "title":{"type":"string"}, "attendees":{"type":"array","items":{"type":"string"}}, "start":{"type":"string","format":"date-time"}, "end":{"type":"string","format":"date-time"}, "description":{"type":"string"} """);

        Add("doc_search",
            "Full-text search the internal documentation, runbooks, RFCs, ADRs, postmortems, and wiki. Returns ranked hits with title, breadcrumb, last-updated date, owning team, and a content snippet.",
            """ "query":{"type":"string"}, "space":{"type":"string"}, "limit":{"type":"integer"} """);

        Add("doc_create_page",
            "Create a new wiki page under a given parent space with title, body (markdown), labels, and owning team. Returns the new page id and URL.",
            """ "space":{"type":"string"}, "parent_id":{"type":"string"}, "title":{"type":"string"}, "body_markdown":{"type":"string"}, "labels":{"type":"array","items":{"type":"string"}} """);

        return list;
    }
}
