import { FormEvent, useCallback, useEffect, useMemo, useState } from "react";
import {
  Activity,
  AlertTriangle,
  CheckCircle2,
  Clock3,
  Database,
  Filter,
  MessageSquarePlus,
  Plus,
  Search,
  ShieldCheck,
  Sparkles,
  UserRoundCheck,
  Zap
} from "lucide-react";
import {
  addComment,
  assignTicket,
  createTicket,
  escalateTicket,
  getHealth,
  listComments,
  listTickets,
  resolveTicket
} from "./api";
import type { HealthResponse, SupportTicketCommentResponse, SupportTicketResponse, TicketStatus } from "./types";

const seedTickets = [
  {
    ticketNumber: "TCK-1001",
    customerId: "acme",
    subject: "Invoice export returns an empty file",
    description: "Month-end invoice exports complete successfully but the generated CSV has no rows.",
    priority: "high",
    slaDueAt: "2026-06-12T17:00:00Z"
  },
  {
    ticketNumber: "TCK-1002",
    customerId: "northwind",
    subject: "Webhook retries are backing up",
    description: "Outbound delivery retries are stacking up after the customer rotated their endpoint certificate.",
    priority: "urgent",
    slaDueAt: "2026-06-12T13:30:00Z"
  },
  {
    ticketNumber: "TCK-1003",
    customerId: "globex",
    subject: "Workflow designer fails to render comments",
    description: "Existing workflow comments disappear after loading a published definition into the designer.",
    priority: "normal",
    slaDueAt: "2026-06-13T09:00:00Z"
  }
];

const queueFilters: Array<{ label: string; value: "all" | TicketStatus }> = [
  { label: "All tickets", value: "all" },
  { label: "Open", value: "open" },
  { label: "Assigned", value: "assigned" },
  { label: "Escalated", value: "escalated" },
  { label: "Resolved", value: "resolved" }
];

const assignees = ["agent-alex", "agent-sam", "agent-mira", "agent-jo"];

export function App() {
  const [health, setHealth] = useState<HealthResponse | null>(null);
  const [tickets, setTickets] = useState<SupportTicketResponse[]>([]);
  const [selectedNumber, setSelectedNumber] = useState<string | null>(null);
  const [comments, setComments] = useState<SupportTicketCommentResponse[]>([]);
  const [statusFilter, setStatusFilter] = useState<"all" | TicketStatus>("all");
  const [query, setQuery] = useState("");
  const [newComment, setNewComment] = useState("");
  const [draft, setDraft] = useState({
    ticketNumber: "TCK-1004",
    customerId: "initech",
    subject: "Approval handoff misses a notification",
    description: "Approvers do not receive the second-stage handoff notification when a prior reviewer skips.",
    priority: "high",
    slaDueAt: "2026-06-14T15:00"
  });
  const [isLoading, setIsLoading] = useState(true);
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const selectedTicket = useMemo(
    () => tickets.find((ticket) => ticket.ticket.ticketNumber === selectedNumber) ?? tickets[0] ?? null,
    [selectedNumber, tickets]
  );

  const metrics = useMemo(() => {
    const byStatus = new Map<TicketStatus, number>();
    for (const ticket of tickets) {
      byStatus.set(ticket.ticket.status, (byStatus.get(ticket.ticket.status) ?? 0) + 1);
    }

    return {
      open: byStatus.get("open") ?? 0,
      assigned: byStatus.get("assigned") ?? 0,
      escalated: byStatus.get("escalated") ?? 0,
      resolved: byStatus.get("resolved") ?? 0
    };
  }, [tickets]);

  const visibleTickets = useMemo(() => {
    const normalized = query.trim().toLowerCase();
    return tickets.filter(({ ticket }) => {
      const matchesStatus = statusFilter === "all" || ticket.status === statusFilter;
      const matchesQuery =
        normalized.length === 0 ||
        [ticket.ticketNumber, ticket.customerId, ticket.subject, ticket.assigneeId, ticket.priority].some((value) =>
          value.toLowerCase().includes(normalized)
        );

      return matchesStatus && matchesQuery;
    });
  }, [query, statusFilter, tickets]);

  const refreshTickets = useCallback(async () => {
    const nextTickets = await listTickets();
    setTickets(nextTickets);
    setSelectedNumber((current) => current ?? nextTickets[0]?.ticket.ticketNumber ?? null);
    return nextTickets;
  }, []);

  const load = useCallback(async () => {
    setIsLoading(true);
    setError(null);

    try {
      const [nextHealth, nextTickets] = await Promise.all([getHealth(), listTickets()]);
      setHealth(nextHealth);
      if (nextTickets.length === 0) {
        await seedDemoTickets();
      }

      await refreshTickets();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not load tickets.");
    } finally {
      setIsLoading(false);
    }
  }, [refreshTickets]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (!selectedTicket) {
      setComments([]);
      return;
    }

    let isCurrent = true;
    listComments(selectedTicket.ticket.ticketNumber)
      .then((nextComments) => {
        if (isCurrent) {
          setComments(nextComments);
        }
      })
      .catch((exception) => setError(exception instanceof Error ? exception.message : "Could not load comments."));

    return () => {
      isCurrent = false;
    };
  }, [selectedTicket]);

  async function handleCreateTicket(event: FormEvent) {
    event.preventDefault();
    setIsSaving(true);
    setError(null);

    try {
      const created = await createTicket({
        ...draft,
        slaDueAt: new Date(draft.slaDueAt).toISOString()
      });
      await refreshTickets();
      setSelectedNumber(created.ticket.ticketNumber);
      setDraft((current) => ({
        ...current,
        ticketNumber: nextTicketNumber(current.ticketNumber),
        subject: "",
        description: ""
      }));
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not create ticket.");
    } finally {
      setIsSaving(false);
    }
  }

  async function mutateSelected(action: (ticket: SupportTicketResponse) => Promise<SupportTicketResponse>) {
    if (!selectedTicket) {
      return;
    }

    setIsSaving(true);
    setError(null);

    try {
      const updated = await action(selectedTicket);
      await refreshTickets();
      setSelectedNumber(updated.ticket.ticketNumber);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Ticket update failed.");
    } finally {
      setIsSaving(false);
    }
  }

  async function handleAddComment(event: FormEvent) {
    event.preventDefault();
    if (!selectedTicket || newComment.trim().length === 0) {
      return;
    }

    setIsSaving(true);
    setError(null);

    try {
      const saved = await addComment(selectedTicket.ticket.ticketNumber, "agent-alex", newComment.trim(), selectedTicket.version);
      setComments((current) => [...current, saved]);
      setNewComment("");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not add comment.");
    } finally {
      setIsSaving(false);
    }
  }

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand-lockup">
          <div className="brand-mark">G</div>
          <div>
            <strong>Groundwork</strong>
            <span>Support</span>
          </div>
        </div>

        <nav className="queue-nav" aria-label="Ticket queues">
          {queueFilters.map((filter) => (
            <button
              className={filter.value === statusFilter ? "queue-item selected" : "queue-item"}
              key={filter.value}
              onClick={() => setStatusFilter(filter.value)}
              type="button"
            >
              <span>{filter.label}</span>
              <strong>{filter.value === "all" ? tickets.length : metrics[filter.value]}</strong>
            </button>
          ))}
        </nav>

        <div className="provider-card">
          <Database size={18} />
          <div>
            <span>Provider</span>
            <strong>{health?.provider ?? "Loading"}</strong>
          </div>
        </div>
        <div className="provider-card quiet">
          <ShieldCheck size={18} />
          <div>
            <span>Physicalization</span>
            <strong>{formatPhysicalization(health?.physicalization)}</strong>
          </div>
        </div>
      </aside>

      <main className="workspace">
        <header className="topbar">
          <div className="search-box">
            <Search size={18} />
            <input
              aria-label="Search tickets"
              onChange={(event) => setQuery(event.target.value)}
              placeholder="Search ticket, customer, assignee..."
              value={query}
            />
          </div>
          <div className="topbar-actions">
            <span className="sync-state">
              <Activity size={16} />
              {isLoading ? "Syncing" : "Live storage"}
            </span>
            <a className="api-link" href="/healthz">
              API health
            </a>
          </div>
        </header>

        {error && (
          <div className="error-banner" role="alert">
            <AlertTriangle size={18} />
            {error}
          </div>
        )}

        <section className="metric-strip" aria-label="Queue metrics">
          <Metric label="Open" value={metrics.open} icon={<Clock3 size={19} />} tone="teal" />
          <Metric label="Assigned" value={metrics.assigned} icon={<UserRoundCheck size={19} />} tone="blue" />
          <Metric label="Escalated" value={metrics.escalated} icon={<Zap size={19} />} tone="amber" />
          <Metric label="Resolved" value={metrics.resolved} icon={<CheckCircle2 size={19} />} tone="green" />
        </section>

        <div className="content-grid">
          <section className="ticket-panel" aria-label="Ticket list">
            <div className="panel-heading">
              <div>
                <h1>Ticket operations</h1>
                <p>{visibleTickets.length} provider-backed records in view</p>
              </div>
              <Filter size={18} />
            </div>

            <div className="ticket-table" role="table" aria-label="Support tickets">
              <div className="ticket-row header" role="row">
                <span>Ticket</span>
                <span>Customer</span>
                <span>Status</span>
                <span>Priority</span>
                <span>Owner</span>
                <span>SLA</span>
              </div>
              {visibleTickets.map((item) => (
                <button
                  className={
                    selectedTicket?.ticket.ticketNumber === item.ticket.ticketNumber ? "ticket-row selected" : "ticket-row"
                  }
                  key={item.ticket.ticketNumber}
                  onClick={() => setSelectedNumber(item.ticket.ticketNumber)}
                  role="row"
                  type="button"
                >
                  <span>
                    <strong>{item.ticket.ticketNumber}</strong>
                    <small>{item.ticket.subject}</small>
                  </span>
                  <span>{item.ticket.customerId}</span>
                  <span>
                    <StatusBadge status={item.ticket.status} />
                  </span>
                  <span>
                    <PriorityBadge priority={item.ticket.priority} />
                  </span>
                  <span>{item.ticket.assigneeId}</span>
                  <span>{formatDate(item.ticket.slaDueAt)}</span>
                </button>
              ))}
            </div>
          </section>

          <aside className="detail-pane" aria-label="Selected ticket details">
            {selectedTicket ? (
              <>
                <div className="detail-header">
                  <div>
                    <span>{selectedTicket.ticket.ticketNumber}</span>
                    <h2>{selectedTicket.ticket.subject}</h2>
                  </div>
                  <StatusBadge status={selectedTicket.ticket.status} />
                </div>
                <p className="detail-description">{selectedTicket.ticket.description}</p>

                <div className="detail-actions">
                  <button
                    onClick={() => mutateSelected((ticket) => assignTicket(ticket.ticket.ticketNumber, nextAssignee(ticket.ticket.assigneeId), ticket.version))}
                    type="button"
                    disabled={isSaving}
                  >
                    Assign
                  </button>
                  <button
                    onClick={() => mutateSelected((ticket) => escalateTicket(ticket.ticket.ticketNumber, ticket.version))}
                    type="button"
                    disabled={isSaving}
                  >
                    Escalate
                  </button>
                  <button
                    onClick={() => mutateSelected((ticket) => resolveTicket(ticket.ticket.ticketNumber, ticket.version))}
                    type="button"
                    disabled={isSaving}
                  >
                    Resolve
                  </button>
                </div>

                <dl className="ticket-facts">
                  <div>
                    <dt>Version</dt>
                    <dd>{selectedTicket.version}</dd>
                  </div>
                  <div>
                    <dt>Customer</dt>
                    <dd>{selectedTicket.ticket.customerId}</dd>
                  </div>
                  <div>
                    <dt>Assignee</dt>
                    <dd>{selectedTicket.ticket.assigneeId}</dd>
                  </div>
                  <div>
                    <dt>Opened</dt>
                    <dd>{formatDate(selectedTicket.ticket.openedAt)}</dd>
                  </div>
                </dl>

                <div className="timeline">
                  <h3>Timeline</h3>
                  <TimelineItem label="Opened" value={formatDate(selectedTicket.ticket.openedAt)} />
                  {selectedTicket.ticket.escalatedAt && <TimelineItem label="Escalated" value={formatDate(selectedTicket.ticket.escalatedAt)} />}
                  {selectedTicket.ticket.resolvedAt && <TimelineItem label="Resolved" value={formatDate(selectedTicket.ticket.resolvedAt)} />}
                </div>

                <div className="comments">
                  <h3>Comments</h3>
                  {comments.length === 0 && <p className="empty-note">No comments yet.</p>}
                  {comments.map(({ comment }) => (
                    <article className="comment" key={comment.commentId}>
                      <strong>{comment.authorId}</strong>
                      <p>{comment.body}</p>
                      <span>{formatDate(comment.createdAt)}</span>
                    </article>
                  ))}
                  <form className="comment-form" onSubmit={handleAddComment}>
                    <MessageSquarePlus size={17} />
                    <input
                      aria-label="New comment"
                      onChange={(event) => setNewComment(event.target.value)}
                      placeholder="Add a provider-backed comment"
                      value={newComment}
                    />
                    <button disabled={isSaving || newComment.trim().length === 0} type="submit">
                      Add
                    </button>
                  </form>
                </div>
              </>
            ) : (
              <div className="empty-selection">Select a ticket to inspect provider-backed state.</div>
            )}
          </aside>
        </div>

        <form className="create-dock" onSubmit={handleCreateTicket}>
          <div>
            <span>
              <Plus size={16} />
              Create ticket
            </span>
            <strong>{draft.ticketNumber}</strong>
          </div>
          <input
            aria-label="Customer"
            onChange={(event) => setDraft((current) => ({ ...current, customerId: event.target.value }))}
            placeholder="customer"
            value={draft.customerId}
          />
          <input
            aria-label="Subject"
            onChange={(event) => setDraft((current) => ({ ...current, subject: event.target.value }))}
            placeholder="subject"
            value={draft.subject}
          />
          <select
            aria-label="Priority"
            onChange={(event) => setDraft((current) => ({ ...current, priority: event.target.value }))}
            value={draft.priority}
          >
            <option value="low">low</option>
            <option value="normal">normal</option>
            <option value="high">high</option>
            <option value="urgent">urgent</option>
          </select>
          <button disabled={isSaving || draft.subject.trim().length === 0} type="submit">
            <Sparkles size={16} />
            Save
          </button>
        </form>
      </main>
    </div>
  );
}

function Metric({ label, value, icon, tone }: { label: string; value: number; icon: React.ReactNode; tone: string }) {
  return (
    <article className={`metric ${tone}`}>
      <span>{icon}</span>
      <div>
        <strong>{value}</strong>
        <small>{label}</small>
      </div>
    </article>
  );
}

function StatusBadge({ status }: { status: TicketStatus }) {
  return <span className={`status-badge ${status}`}>{status}</span>;
}

function PriorityBadge({ priority }: { priority: string }) {
  return <span className={`priority-badge ${priority}`}>{priority}</span>;
}

function TimelineItem({ label, value }: { label: string; value: string }) {
  return (
    <div className="timeline-item">
      <span />
      <div>
        <strong>{label}</strong>
        <small>{value}</small>
      </div>
    </div>
  );
}

async function seedDemoTickets() {
  const created = [];
  for (const ticket of seedTickets) {
    try {
      created.push(await createTicket(ticket));
    } catch {
      // A previous run may already have seeded the backing store.
    }
  }

  const first = created[0];
  const second = created[1];
  if (first) {
    const assigned = await assignTicket(first.ticket.ticketNumber, "agent-alex", first.version);
    await addComment(
      assigned.ticket.ticketNumber,
      "agent-alex",
      "Confirmed with finance: export rows are present in the source system but not in the generated artifact.",
      assigned.version
    );
  }

  if (second) {
    const assigned = await assignTicket(second.ticket.ticketNumber, "agent-mira", second.version);
    await escalateTicket(assigned.ticket.ticketNumber, assigned.version);
  }
}

function formatDate(value?: string | null) {
  if (!value) {
    return "Not set";
  }

  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit"
  }).format(new Date(value));
}

function formatPhysicalization(value?: string) {
  if (!value) {
    return "Loading";
  }

  return value.includes("Optimized") ? "Optimized" : "Portable";
}

function nextAssignee(current: string) {
  const index = assignees.indexOf(current);
  return assignees[(index + 1) % assignees.length] ?? assignees[0];
}

function nextTicketNumber(current: string) {
  const match = current.match(/^(.*?)(\d+)$/);
  if (!match) {
    return "TCK-1005";
  }

  return `${match[1]}${String(Number(match[2]) + 1).padStart(match[2].length, "0")}`;
}
