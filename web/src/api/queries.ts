import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { get, request } from './client'
import type {
  AgentDecisionBody,
  AgentDecisionResult,
  AgentDispute,
  DisputeDetail,
  DisputeReasonInfo,
  DisputeStatus,
  DisputeSummary,
  Me,
  Paged,
  RaiseDisputeBody,
  Transaction,
} from './types'

export interface TransactionFilters {
  search?: string
  from?: string
  to?: string
  page: number
  pageSize: number
}

export const queryKeys = {
  me: ['me'] as const,
  transactions: (f: TransactionFilters) => ['transactions', f] as const,
  transaction: (id: string) => ['transaction', id] as const,
  disputes: (status: DisputeStatus | undefined, page: number) => ['disputes', status ?? 'all', page] as const,
  dispute: (id: string) => ['dispute', id] as const,
  reasons: ['dispute-reasons'] as const,
  agentDisputes: (status: DisputeStatus | undefined, page: number) => ['agent-disputes', status ?? 'all', page] as const,
}

function qs(params: Record<string, string | number | undefined>): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== '') search.set(key, String(value))
  }
  const s = search.toString()
  return s ? `?${s}` : ''
}

export function useMe(enabled: boolean) {
  return useQuery({ queryKey: queryKeys.me, queryFn: ({ signal }) => get<Me>('/api/me', signal), enabled })
}

export function useTransactions(filters: TransactionFilters) {
  return useQuery({
    queryKey: queryKeys.transactions(filters),
    queryFn: ({ signal }) => get<Paged<Transaction>>(`/api/transactions${qs({ ...filters })}`, signal),
    // Keep the current page on screen while the next one loads, instead of flashing a spinner.
    placeholderData: keepPreviousData,
  })
}

export function useTransaction(id: string) {
  return useQuery({
    queryKey: queryKeys.transaction(id),
    // POST with the id in the body, not GET /api/transactions/{id}: keeps the id out of URLs and logs.
    queryFn: async ({ signal }) =>
      (await request<Transaction>('/api/transactions/lookup', { method: 'POST', body: { transactionId: id }, signal })).data,
  })
}

export function useDisputes(status: DisputeStatus | undefined, page: number) {
  return useQuery({
    queryKey: queryKeys.disputes(status, page),
    queryFn: ({ signal }) => get<Paged<DisputeSummary>>(`/api/disputes${qs({ status, page, pageSize: 20 })}`, signal),
    placeholderData: keepPreviousData,
  })
}

export function useDispute(id: string) {
  return useQuery({
    queryKey: queryKeys.dispute(id),
    // POST with the id in the body, like useTransaction: keeps the id out of URLs and logs.
    queryFn: async ({ signal }) =>
      (await request<DisputeDetail>('/api/disputes/lookup', { method: 'POST', body: { disputeId: id }, signal })).data,
  })
}

/** `pollMs`: while the page waits for a decision to come back from Kafka. Off otherwise. */
export function useAgentDisputes(status: DisputeStatus | undefined, page: number, pollMs?: number) {
  return useQuery({
    queryKey: queryKeys.agentDisputes(status, page),
    queryFn: ({ signal }) => get<Paged<AgentDispute>>(`/api/agent/disputes${qs({ status, page, pageSize: 20 })}`, signal),
    placeholderData: keepPreviousData,
    refetchInterval: pollMs ?? false,
  })
}

export function useSendDecision() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: async (body: AgentDecisionBody) =>
      (await request<AgentDecisionResult>('/api/agent/disputes/decision', { method: 'POST', body })).data,
    onSuccess: () => void client.invalidateQueries({ queryKey: ['agent-disputes'] }),
  })
}

export function useDisputeReasons() {
  return useQuery({
    queryKey: queryKeys.reasons,
    queryFn: ({ signal }) => get<DisputeReasonInfo[]>('/api/dispute-reasons', signal),
    staleTime: Infinity,
  })
}

/** Raising or withdrawing changes eligibility flags on transactions as well as the disputes lists. */
function useInvalidateDisputeViews() {
  const client = useQueryClient()
  return (detail: DisputeDetail) => {
    client.setQueryData(queryKeys.dispute(detail.id), detail)
    void client.invalidateQueries({ queryKey: ['disputes'] })
    void client.invalidateQueries({ queryKey: ['transactions'] })
    void client.invalidateQueries({ queryKey: queryKeys.transaction(detail.transaction.id) })
  }
}

export function useRaiseDispute() {
  const invalidate = useInvalidateDisputeViews()
  return useMutation({
    mutationFn: async ({ body, idempotencyKey }: { body: RaiseDisputeBody; idempotencyKey: string }) =>
      (await request<DisputeDetail>('/api/disputes', {
        method: 'POST',
        body,
        headers: { 'Idempotency-Key': idempotencyKey },
      })).data,
    onSuccess: invalidate,
  })
}

export function useWithdrawDispute(id: string) {
  const invalidate = useInvalidateDisputeViews()
  return useMutation({
    mutationFn: async (note: string | undefined) =>
      (await request<DisputeDetail>('/api/disputes/withdraw', {
        method: 'POST',
        body: note ? { disputeId: id, note } : { disputeId: id },
      })).data,
    onSuccess: invalidate,
  })
}
