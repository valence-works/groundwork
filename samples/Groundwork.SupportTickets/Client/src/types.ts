export type TicketStatus = "open" | "assigned" | "escalated" | "resolved";

export interface SupportTicket {
  ticketNumber: string;
  customerId: string;
  subject: string;
  description: string;
  status: TicketStatus;
  priority: "low" | "normal" | "high" | "urgent";
  assigneeId: string;
  openedAt: string;
  resolvedAt?: string | null;
  slaDueAt?: string | null;
  escalatedAt?: string | null;
}

export interface SupportTicketResponse {
  ticket: SupportTicket;
  version: number;
}

export interface SupportTicketComment {
  commentId: string;
  ticketNumber: string;
  authorId: string;
  body: string;
  createdAt: string;
}

export interface SupportTicketCommentResponse {
  comment: SupportTicketComment;
  version: number;
}

export interface HealthResponse {
  status: string;
  provider: string;
  physicalization: string;
}

export interface CreateTicketRequest {
  ticketNumber: string;
  customerId: string;
  subject: string;
  description: string;
  priority: string;
  slaDueAt?: string;
}
