using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    ?? throw new InvalidOperationException("OPENAI_API_KEY not set");

const string ModelId = "gpt-5.4";
const string Instructions =
    "You are a helpful enterprise operations assistant for ACME Corp. " +
    "You help employees query internal systems including HR, finance, " +
    "inventory, ticketing, deployments, monitoring, and customer data. " +
    "Always confirm potentially destructive actions before invoking tools.";
const string UserPrompt =
    "Find any open P1 incidents from the last 24 hours and tell me which " +
    "service they belong to.";

var capture = new CapturingHandler(new HttpClientHandler());
var http = new HttpClient(capture) { Timeout = TimeSpan.FromMinutes(2) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
http.BaseAddress = new Uri("https://api.openai.com/");

var tools = MockTools.Build();
Console.WriteLine($"Built {tools.Count} mock tools.\n");

JsonObject MakeFunctionTool(MockTools.ToolDef t) => new()
{
    ["type"] = "function",
    ["name"] = t.Name,
    ["description"] = t.Description,
    ["parameters"] = JsonNode.Parse(t.Schema),
};

JsonArray BuildInlineTools(IEnumerable<MockTools.ToolDef> ts)
{
    var arr = new JsonArray();
    foreach (var t in ts) arr.Add(MakeFunctionTool(t));
    return arr;
}

// defer_loading flag on each function definition + tool_search tool.
JsonArray BuildDeferredToolsFlat(IEnumerable<MockTools.ToolDef> ts)
{
    var arr = new JsonArray();
    foreach (var t in ts)
    {
        var f = MakeFunctionTool(t);
        f["defer_loading"] = true;
        arr.Add(f);
    }
    arr.Add(new JsonObject { ["type"] = "tool_search" });
    return arr;
}

// Namespace pattern: defer_loading lives on each child function (the namespace
// container itself rejects defer_loading). The tool_search tool is a sibling.
JsonArray BuildDeferredToolsViaNamespace(IEnumerable<MockTools.ToolDef> ts)
{
    var nsTools = new JsonArray();
    foreach (var t in ts)
    {
        var f = MakeFunctionTool(t);
        f["defer_loading"] = true;
        nsTools.Add(f);
    }
    return new JsonArray
    {
        new JsonObject
        {
            ["type"] = "namespace",
            ["name"] = "acme_ops",
            ["description"] = "ACME Corp internal operations toolset (HR, finance, " +
                              "inventory, ticketing, deployments, monitoring, customers, calendar, docs).",
            ["tools"] = nsTools,
        },
        new JsonObject { ["type"] = "tool_search" },
    };
}

JsonObject BaseRequest(JsonArray? toolsArray, JsonNode? input = null)
{
    var req = new JsonObject
    {
        ["model"] = ModelId,
        ["instructions"] = Instructions,
        ["input"] = input ?? UserPrompt,
        ["max_output_tokens"] = 16,
    };
    if (toolsArray is not null && toolsArray.Count > 0)
    {
        req["tools"] = toolsArray;
        req["tool_choice"] = "none";
    }
    return req;
}

async Task<long> CountInputTokens(string label, JsonObject request, string scenario)
{
    capture.CurrentScenario = scenario;
    using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
    using var resp = await http.PostAsync("/v1/responses", content);
    var body = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
    {
        Console.WriteLine($"  {label} : ERROR {(int)resp.StatusCode} {body}");
        return -1;
    }
    var json = JsonNode.Parse(body)!.AsObject();
    var inT = json["usage"]?["input_tokens"]?.GetValue<long>() ?? -1;
    Console.WriteLine($"  {label} : {inT,6}");
    return inT;
}

Console.WriteLine("Counting tokens via /v1/responses (max_output_tokens=16, tool_choice=none)...\n");

var fewTools = tools.Take(3).ToList();

var t1 = await CountInputTokens("1. Prompt only                            ",
    BaseRequest(null), "count_prompt_only");
var t2 = await CountInputTokens("2. Prompt + 21 tools inline               ",
    BaseRequest(BuildInlineTools(tools)), "count_inline_21");
var t3 = await CountInputTokens("3. Prompt + 21 tools deferred (flat)      ",
    BaseRequest(BuildDeferredToolsFlat(tools)), "count_deferred_flat_21");
var t3b = await CountInputTokens("3b. Prompt + 21 tools deferred (namespace)",
    BaseRequest(BuildDeferredToolsViaNamespace(tools)), "count_deferred_ns_21");
var t4 = await CountInputTokens("4. Prompt + 3 tools inline                ",
    BaseRequest(BuildInlineTools(fewTools)), "count_inline_3");
var t5 = await CountInputTokens("5. Prompt + 3 tools deferred (flat)       ",
    BaseRequest(BuildDeferredToolsFlat(fewTools)), "count_deferred_flat_3");
var t5b = await CountInputTokens("5b. Prompt + 3 tools deferred (namespace) ",
    BaseRequest(BuildDeferredToolsViaNamespace(fewTools)), "count_deferred_ns_3");

Console.WriteLine();
Console.WriteLine("─────────────────────────────────────────────");
if (t1 > 0)
{
    Console.WriteLine($"21 tools — inline overhead       : {t2 - t1,6}");
    Console.WriteLine($"21 tools — deferred flat         : {(t3 > 0 ? (t3 - t1).ToString() : "n/a"),6}");
    Console.WriteLine($"21 tools — deferred namespace    : {(t3b > 0 ? (t3b - t1).ToString() : "n/a"),6}");
    Console.WriteLine($" 3 tools — inline overhead       : {t4 - t1,6}");
    Console.WriteLine($" 3 tools — deferred flat         : {(t5 > 0 ? (t5 - t1).ToString() : "n/a"),6}");
    Console.WriteLine($" 3 tools — deferred namespace    : {(t5b > 0 ? (t5b - t1).ToString() : "n/a"),6}");
}

Console.WriteLine();
Console.WriteLine("═════════════════════════════════════════════");
Console.WriteLine("Live agent runs (real /v1/responses, mock local tool execution)");
Console.WriteLine("═════════════════════════════════════════════");

await RunAgent("6. Inline 21 tools (live)         ", BuildInlineTools(tools), "live_inline_21");
Console.WriteLine();
await RunAgent("7. Deferred 21 tools (flat)       ", BuildDeferredToolsFlat(tools), "live_deferred_flat_21");
Console.WriteLine();
await RunAgent("8. Deferred 21 tools (namespace)  ", BuildDeferredToolsViaNamespace(tools), "live_deferred_ns_21");

var flowPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "flow.json");
flowPath = Path.GetFullPath(flowPath);
File.WriteAllText(flowPath, JsonSerializer.Serialize(capture.Exchanges,
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"\nWrote flow log: {flowPath} ({capture.Exchanges.Count} exchanges)");

async Task RunAgent(string label, JsonArray toolsArray, string scenario)
{
    Console.WriteLine($"\n--- {label} ---");
    capture.CurrentScenario = scenario;

    var input = new JsonArray
    {
        new JsonObject
        {
            ["type"] = "message",
            ["role"] = "user",
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "input_text", ["text"] = UserPrompt },
            },
        },
    };

    long totalIn = 0, totalOut = 0;

    for (int turn = 1; turn <= 5; turn++)
    {
        var req = new JsonObject
        {
            ["model"] = ModelId,
            ["instructions"] = Instructions,
            ["input"] = input.DeepClone(),
            ["tools"] = toolsArray.DeepClone(),
            ["max_output_tokens"] = 1024,
        };

        using var content = new StringContent(req.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync("/v1/responses", content);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"  turn {turn}: HTTP {(int)resp.StatusCode}: {body}");
            return;
        }

        var json = JsonNode.Parse(body)!.AsObject();
        var inT = json["usage"]?["input_tokens"]?.GetValue<long>() ?? 0;
        var outT = json["usage"]?["output_tokens"]?.GetValue<long>() ?? 0;
        var status = json["status"]?.GetValue<string>() ?? "?";
        totalIn += inT;
        totalOut += outT;
        Console.WriteLine($"  turn {turn}: in={inT,6}  out={outT,5}  status={status}");

        var output = json["output"]?.AsArray() ?? new JsonArray();
        var localCalls = new List<(string callId, string name)>();

        foreach (var item in output)
        {
            if (item is null) continue;
            var itemObj = item.AsObject();
            var type = itemObj["type"]?.GetValue<string>() ?? "";
            switch (type)
            {
                case "message":
                    var contentArr = itemObj["content"]?.AsArray();
                    if (contentArr is not null)
                    {
                        foreach (var c in contentArr)
                        {
                            var text = c?["text"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(text))
                                Console.WriteLine($"    text: {Truncate(text, 120)}");
                        }
                    }
                    break;
                case "function_call":
                    var callId = itemObj["call_id"]?.GetValue<string>() ?? "";
                    var name = itemObj["name"]?.GetValue<string>() ?? "";
                    var args = itemObj["arguments"]?.GetValue<string>() ?? "";
                    localCalls.Add((callId, name));
                    Console.WriteLine($"    function_call: {name}({Truncate(args, 100)})");
                    break;
                case "tool_search_call":
                    Console.WriteLine($"    tool_search_call: {Truncate(itemObj.ToJsonString(), 140)}");
                    break;
                default:
                    Console.WriteLine($"    {type}: {Truncate(itemObj.ToJsonString(), 120)}");
                    break;
            }

            input.Add(JsonNode.Parse(itemObj.ToJsonString())!);
        }

        if (localCalls.Count == 0) break;

        foreach (var (callId, name) in localCalls)
        {
            input.Add(new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = callId,
                ["output"] = MockExecute(name),
            });
            Console.WriteLine($"    [local exec] {name} -> mock result");
        }
    }

    Console.WriteLine($"  TOTAL : in={totalIn}  out={totalOut}  combined={totalIn + totalOut}");
}

static string MockExecute(string toolName) => toolName switch
{
    "monitoring_list_incidents" =>
        "[{\"id\":\"INC-4421\",\"title\":\"checkout-api 5xx spike\",\"severity\":\"P1\",\"status\":\"open\",\"started_at\":\"2026-04-07T08:14:00Z\",\"services_affected\":[\"checkout-api\"]}]",
    _ => "{\"ok\":true,\"result\":\"mock\"}",
};

static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";

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
        var buffered = new ByteArrayContent(Encoding.UTF8.GetBytes(respBody));
        foreach (var h in response.Content.Headers)
            buffered.Headers.TryAddWithoutValidation(h.Key, h.Value);
        response.Content = buffered;

        Exchanges.Add(new Exchange(
            CurrentScenario,
            request.Method.Method,
            request.RequestUri?.AbsolutePath ?? "",
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
            list.Add(new ToolDef(name, desc,
                "{\"type\":\"object\",\"properties\":{" + props + "},\"additionalProperties\":false}"));

        Add("hr_lookup_employee",
            "Look up an employee record from the HR system by employee ID, email address, or full name. Returns the employee's department, manager, hire date, job title, location, work phone, employment status, cost center, and employee classification. Use when a user needs verified personnel data, when verifying authorisation for HR-restricted actions, or when populating templates that need HR fields.",
            "\"query\":{\"type\":\"string\"},\"include_compensation\":{\"type\":\"boolean\"}");

        Add("hr_create_pto_request",
            "Create a paid-time-off request on behalf of an employee. The request will route to the employee's manager for approval and post to the team calendar. Validates remaining PTO balance before submission and supports vacation, sick, bereavement, jury duty, parental, and unpaid leave categories.",
            "\"employee_id\":{\"type\":\"string\"},\"start_date\":{\"type\":\"string\"},\"end_date\":{\"type\":\"string\"},\"category\":{\"type\":\"string\"},\"notes\":{\"type\":\"string\"}");

        Add("finance_get_budget",
            "Retrieve the current fiscal-year budget allocation, year-to-date spend, encumbrances, and remaining balance for a given cost center or project code. Includes monthly burn rate, projected end-of-year position, and any active spending freezes or approval thresholds.",
            "\"cost_center\":{\"type\":\"string\"},\"fiscal_year\":{\"type\":\"integer\"}");

        Add("finance_submit_expense",
            "Submit an expense report for reimbursement. Accepts itemised line entries with categories, amounts, currencies, receipts (as URLs), business justification, and project allocation. Performs policy checks for per-diem limits, missing receipts, alcohol restrictions, and split-billing violations before submission.",
            "\"employee_id\":{\"type\":\"string\"},\"items\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"justification\":{\"type\":\"string\"}");

        Add("inventory_check_stock",
            "Check current on-hand and available-to-promise inventory for a given SKU across one or all warehouses. Returns quantity on hand, allocated, in-transit, on-order, reorder point, lead time, and the next expected restock date.",
            "\"sku\":{\"type\":\"string\"},\"warehouse_id\":{\"type\":\"string\"}");

        Add("inventory_create_transfer",
            "Create an inter-warehouse stock transfer order to move inventory from a source warehouse to a destination warehouse. Validates source availability, destination capacity, lane restrictions, and customs requirements for cross-border transfers.",
            "\"sku\":{\"type\":\"string\"},\"quantity\":{\"type\":\"integer\"},\"from_warehouse\":{\"type\":\"string\"},\"to_warehouse\":{\"type\":\"string\"}");

        Add("ticket_search",
            "Search the support ticketing system using a free-text query, status filter, priority filter, assignee, and date range. Returns matching tickets with id, title, status, priority, requester, assignee, last update time, and SLA status.",
            "\"query\":{\"type\":\"string\"},\"status\":{\"type\":\"string\"},\"priority\":{\"type\":\"string\"},\"assignee\":{\"type\":\"string\"},\"since\":{\"type\":\"string\"}");

        Add("ticket_create",
            "Create a new support ticket with title, description, requester, priority, category, and optional attachments. Auto-routes to the appropriate queue using the category taxonomy and applies SLA policy.",
            "\"title\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"},\"requester\":{\"type\":\"string\"},\"priority\":{\"type\":\"string\"},\"category\":{\"type\":\"string\"}");

        Add("ticket_update_status",
            "Update the status of an existing support ticket and optionally add a resolution comment, change assignee, or close the ticket with a resolution code. Triggers SLA recalculation and customer notification.",
            "\"ticket_id\":{\"type\":\"string\"},\"status\":{\"type\":\"string\"},\"comment\":{\"type\":\"string\"}");

        Add("deployment_list_environments",
            "List all deployment environments (production, staging, qa, dev, sandbox) for a given service, including their current deployed version, last deploy time, deploy actor, health status, and active feature flags.",
            "\"service\":{\"type\":\"string\"}");

        Add("deployment_trigger",
            "Trigger a new deployment of a specified build artifact (by git SHA or tag) to a target environment. Performs pre-flight checks for change-management approval, deploy windows, dependency readiness, and rollback availability.",
            "\"service\":{\"type\":\"string\"},\"environment\":{\"type\":\"string\"},\"ref\":{\"type\":\"string\"},\"skip_canary\":{\"type\":\"boolean\"}");

        Add("deployment_rollback",
            "Roll a service back to its previously deployed version in the specified environment. Captures the rollback reason for the post-incident review and notifies the on-call channel.",
            "\"service\":{\"type\":\"string\"},\"environment\":{\"type\":\"string\"},\"reason\":{\"type\":\"string\"}");

        Add("monitoring_query_metric",
            "Run a metrics query against the time-series monitoring system. Supports PromQL syntax. Use for latency percentiles, error rates, saturation, throughput, and any custom service KPI. Returns evaluated series with timestamps and values.",
            "\"promql\":{\"type\":\"string\"},\"start\":{\"type\":\"string\"},\"end\":{\"type\":\"string\"},\"step\":{\"type\":\"string\"}");

        Add("monitoring_list_incidents",
            "List active and recent incidents from the on-call/paging system. Filters by severity (P1-P5), status (open, acknowledged, resolved), service, and time window. Returns incident id, title, severity, status, started_at, services_affected, and current responder.",
            "\"severity\":{\"type\":\"string\"},\"status\":{\"type\":\"string\"},\"since\":{\"type\":\"string\"}");

        Add("monitoring_acknowledge_incident",
            "Acknowledge a paging incident, claiming responder ownership and silencing further re-pages for the configured grace window. Posts an ack to the incident channel.",
            "\"incident_id\":{\"type\":\"string\"},\"responder\":{\"type\":\"string\"}");

        Add("customer_lookup",
            "Look up a customer account from the CRM by customer id, email, domain, or company name. Returns the account owner, tier, MRR, contract end date, primary contacts, support entitlements, and any active escalations.",
            "\"query\":{\"type\":\"string\"}");

        Add("customer_add_note",
            "Append a timestamped account note to a customer record visible to all account-team members. Supports markdown and @mentions of internal users.",
            "\"customer_id\":{\"type\":\"string\"},\"note\":{\"type\":\"string\"}");

        Add("calendar_find_slot",
            "Find a meeting slot that works for a list of attendees within a given window. Honours working hours, declared focus blocks, and timezone preferences. Returns the earliest mutually-available slot of the requested duration.",
            "\"attendees\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"duration_minutes\":{\"type\":\"integer\"},\"earliest\":{\"type\":\"string\"},\"latest\":{\"type\":\"string\"}");

        Add("calendar_book_meeting",
            "Create a calendar event with attendees, title, description, location/conferencing link, and optional recurrence rule. Sends invites and adds to attendees' calendars.",
            "\"title\":{\"type\":\"string\"},\"attendees\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"start\":{\"type\":\"string\"},\"end\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"}");

        Add("doc_search",
            "Full-text search the internal documentation, runbooks, RFCs, ADRs, postmortems, and wiki. Returns ranked hits with title, breadcrumb, last-updated date, owning team, and a content snippet.",
            "\"query\":{\"type\":\"string\"},\"space\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}");

        Add("doc_create_page",
            "Create a new wiki page under a given parent space with title, body (markdown), labels, and owning team. Returns the new page id and URL.",
            "\"space\":{\"type\":\"string\"},\"parent_id\":{\"type\":\"string\"},\"title\":{\"type\":\"string\"},\"body_markdown\":{\"type\":\"string\"},\"labels\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}");

        return list;
    }
}
