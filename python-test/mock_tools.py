"""Shared mock tool catalog mirroring the C# tests in Program.cs."""

SYSTEM_PROMPT = (
    "You are a helpful enterprise operations assistant for ACME Corp. "
    "You help employees query internal systems including HR, finance, "
    "inventory, ticketing, deployments, monitoring, and customer data. "
    "Always confirm potentially destructive actions before invoking tools."
)

USER_PROMPT = (
    "Find any open P1 incidents from the last 24 hours and tell me which "
    "service they belong to."
)


def _schema(props_json: str) -> dict:
    import json
    return {
        "type": "object",
        "properties": json.loads("{" + props_json + "}"),
        "additionalProperties": False,
    }


def build() -> list[dict]:
    raw = [
        ("hr_lookup_employee",
         "Look up an employee record from the HR system by employee ID, email address, or full name. Returns the employee's department, manager, hire date, job title, location, work phone, employment status, cost center, and employee classification. Use when a user needs verified personnel data, when verifying authorisation for HR-restricted actions, or when populating templates that need HR fields.",
         '"query":{"type":"string"},"include_compensation":{"type":"boolean"}'),
        ("hr_create_pto_request",
         "Create a paid-time-off request on behalf of an employee. The request will route to the employee's manager for approval and post to the team calendar. Validates remaining PTO balance before submission and supports vacation, sick, bereavement, jury duty, parental, and unpaid leave categories.",
         '"employee_id":{"type":"string"},"start_date":{"type":"string"},"end_date":{"type":"string"},"category":{"type":"string"},"notes":{"type":"string"}'),
        ("finance_get_budget",
         "Retrieve the current fiscal-year budget allocation, year-to-date spend, encumbrances, and remaining balance for a given cost center or project code. Includes monthly burn rate, projected end-of-year position, and any active spending freezes or approval thresholds.",
         '"cost_center":{"type":"string"},"fiscal_year":{"type":"integer"}'),
        ("finance_submit_expense",
         "Submit an expense report for reimbursement. Accepts itemised line entries with categories, amounts, currencies, receipts (as URLs), business justification, and project allocation. Performs policy checks for per-diem limits, missing receipts, alcohol restrictions, and split-billing violations before submission.",
         '"employee_id":{"type":"string"},"items":{"type":"array","items":{"type":"object"}},"justification":{"type":"string"}'),
        ("inventory_check_stock",
         "Check current on-hand and available-to-promise inventory for a given SKU across one or all warehouses. Returns quantity on hand, allocated, in-transit, on-order, reorder point, lead time, and the next expected restock date.",
         '"sku":{"type":"string"},"warehouse_id":{"type":"string"}'),
        ("inventory_create_transfer",
         "Create an inter-warehouse stock transfer order to move inventory from a source warehouse to a destination warehouse. Validates source availability, destination capacity, lane restrictions, and customs requirements for cross-border transfers.",
         '"sku":{"type":"string"},"quantity":{"type":"integer"},"from_warehouse":{"type":"string"},"to_warehouse":{"type":"string"}'),
        ("ticket_search",
         "Search the support ticketing system using a free-text query, status filter, priority filter, assignee, and date range. Returns matching tickets with id, title, status, priority, requester, assignee, last update time, and SLA status.",
         '"query":{"type":"string"},"status":{"type":"string"},"priority":{"type":"string"},"assignee":{"type":"string"},"since":{"type":"string"}'),
        ("ticket_create",
         "Create a new support ticket with title, description, requester, priority, category, and optional attachments. Auto-routes to the appropriate queue using the category taxonomy and applies SLA policy.",
         '"title":{"type":"string"},"description":{"type":"string"},"requester":{"type":"string"},"priority":{"type":"string"},"category":{"type":"string"}'),
        ("ticket_update_status",
         "Update the status of an existing support ticket and optionally add a resolution comment, change assignee, or close the ticket with a resolution code. Triggers SLA recalculation and customer notification.",
         '"ticket_id":{"type":"string"},"status":{"type":"string"},"comment":{"type":"string"}'),
        ("deployment_list_environments",
         "List all deployment environments (production, staging, qa, dev, sandbox) for a given service, including their current deployed version, last deploy time, deploy actor, health status, and active feature flags.",
         '"service":{"type":"string"}'),
        ("deployment_trigger",
         "Trigger a new deployment of a specified build artifact (by git SHA or tag) to a target environment. Performs pre-flight checks for change-management approval, deploy windows, dependency readiness, and rollback availability.",
         '"service":{"type":"string"},"environment":{"type":"string"},"ref":{"type":"string"},"skip_canary":{"type":"boolean"}'),
        ("deployment_rollback",
         "Roll a service back to its previously deployed version in the specified environment. Captures the rollback reason for the post-incident review and notifies the on-call channel.",
         '"service":{"type":"string"},"environment":{"type":"string"},"reason":{"type":"string"}'),
        ("monitoring_query_metric",
         "Run a metrics query against the time-series monitoring system. Supports PromQL syntax. Use for latency percentiles, error rates, saturation, throughput, and any custom service KPI. Returns evaluated series with timestamps and values.",
         '"promql":{"type":"string"},"start":{"type":"string"},"end":{"type":"string"},"step":{"type":"string"}'),
        ("monitoring_list_incidents",
         "List active and recent incidents from the on-call/paging system. Filters by severity (P1-P5), status (open, acknowledged, resolved), service, and time window. Returns incident id, title, severity, status, started_at, services_affected, and current responder.",
         '"severity":{"type":"string"},"status":{"type":"string"},"since":{"type":"string"}'),
        ("monitoring_acknowledge_incident",
         "Acknowledge a paging incident, claiming responder ownership and silencing further re-pages for the configured grace window. Posts an ack to the incident channel.",
         '"incident_id":{"type":"string"},"responder":{"type":"string"}'),
        ("customer_lookup",
         "Look up a customer account from the CRM by customer id, email, domain, or company name. Returns the account owner, tier, MRR, contract end date, primary contacts, support entitlements, and any active escalations.",
         '"query":{"type":"string"}'),
        ("customer_add_note",
         "Append a timestamped account note to a customer record visible to all account-team members. Supports markdown and @mentions of internal users.",
         '"customer_id":{"type":"string"},"note":{"type":"string"}'),
        ("calendar_find_slot",
         "Find a meeting slot that works for a list of attendees within a given window. Honours working hours, declared focus blocks, and timezone preferences. Returns the earliest mutually-available slot of the requested duration.",
         '"attendees":{"type":"array","items":{"type":"string"}},"duration_minutes":{"type":"integer"},"earliest":{"type":"string"},"latest":{"type":"string"}'),
        ("calendar_book_meeting",
         "Create a calendar event with attendees, title, description, location/conferencing link, and optional recurrence rule. Sends invites and adds to attendees' calendars.",
         '"title":{"type":"string"},"attendees":{"type":"array","items":{"type":"string"}},"start":{"type":"string"},"end":{"type":"string"},"description":{"type":"string"}'),
        ("doc_search",
         "Full-text search the internal documentation, runbooks, RFCs, ADRs, postmortems, and wiki. Returns ranked hits with title, breadcrumb, last-updated date, owning team, and a content snippet.",
         '"query":{"type":"string"},"space":{"type":"string"},"limit":{"type":"integer"}'),
        ("doc_create_page",
         "Create a new wiki page under a given parent space with title, body (markdown), labels, and owning team. Returns the new page id and URL.",
         '"space":{"type":"string"},"parent_id":{"type":"string"},"title":{"type":"string"},"body_markdown":{"type":"string"},"labels":{"type":"array","items":{"type":"string"}}'),
    ]
    return [{"name": n, "description": d, "schema": _schema(p)} for n, d, p in raw]


def mock_execute(tool_name: str) -> str:
    if tool_name == "monitoring_list_incidents":
        return ('[{"id":"INC-4421","title":"checkout-api 5xx spike",'
                '"severity":"P1","status":"open",'
                '"started_at":"2026-04-07T08:14:00Z",'
                '"services_affected":["checkout-api"]}]')
    return '{"ok":true,"result":"mock"}'
