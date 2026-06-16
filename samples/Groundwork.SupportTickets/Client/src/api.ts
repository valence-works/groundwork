import type {
  CreateTicketRequest,
  ExternalModuleFitResponse,
  HealthResponse,
  InboxAdmissionResponse,
  SupportTicketCommentResponse,
  SupportTicketResponse
} from "./types";

const statuses = ["open", "assigned", "escalated", "resolved"] as const;

export async function getHealth(): Promise<HealthResponse> {
  return readJson(await fetch("/healthz"));
}

export async function getExternalModuleFit(): Promise<ExternalModuleFitResponse> {
  return readJson(await fetch("/modules/inbox/fit"));
}

export async function admitInboxMessage(consumer: string, messageKey: string): Promise<InboxAdmissionResponse> {
  return readJson(await fetch("/modules/inbox/admit", jsonRequest("POST", { consumer, messageKey })));
}

export async function listTickets(): Promise<SupportTicketResponse[]> {
  const groups = await Promise.all(statuses.map((status) => listTicketsBy("status", status)));
  return uniqueByTicket(groups.flat()).sort((a, b) => a.ticket.ticketNumber.localeCompare(b.ticket.ticketNumber));
}

export async function listTicketsBy(field: "status" | "priority" | "customerId" | "assigneeId", value: string) {
  const params = new URLSearchParams({ [field]: value });
  return readJson<SupportTicketResponse[]>(await fetch(`/tickets?${params.toString()}`));
}

export async function createTicket(request: CreateTicketRequest): Promise<SupportTicketResponse> {
  return readJson(await fetch("/tickets", jsonRequest("POST", request)));
}

export async function assignTicket(ticketNumber: string, assigneeId: string, expectedVersion: number) {
  return readJson<SupportTicketResponse>(
    await fetch(`/tickets/${encodeURIComponent(ticketNumber)}/assign`, jsonRequest("POST", { assigneeId, expectedVersion }))
  );
}

export async function escalateTicket(ticketNumber: string, expectedVersion: number) {
  return readJson<SupportTicketResponse>(
    await fetch(`/tickets/${encodeURIComponent(ticketNumber)}/escalate`, jsonRequest("POST", { expectedVersion }))
  );
}

export async function resolveTicket(ticketNumber: string, expectedVersion: number) {
  return readJson<SupportTicketResponse>(
    await fetch(`/tickets/${encodeURIComponent(ticketNumber)}/resolve`, jsonRequest("POST", { expectedVersion }))
  );
}

export async function listComments(ticketNumber: string) {
  return readJson<SupportTicketCommentResponse[]>(await fetch(`/tickets/${encodeURIComponent(ticketNumber)}/comments`));
}

export async function addComment(ticketNumber: string, authorId: string, body: string, expectedTicketVersion: number) {
  return readJson<SupportTicketCommentResponse>(
    await fetch(
      `/tickets/${encodeURIComponent(ticketNumber)}/comments`,
      jsonRequest("POST", { authorId, body, expectedTicketVersion })
    )
  );
}

function jsonRequest(method: "POST", body: unknown): RequestInit {
  return {
    method,
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body)
  };
}

async function readJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const body = await response.text();
    throw new Error(body || `Request failed with ${response.status}`);
  }

  return response.json() as Promise<T>;
}

function uniqueByTicket(tickets: SupportTicketResponse[]) {
  return Array.from(new Map(tickets.map((ticket) => [ticket.ticket.ticketNumber, ticket])).values());
}
