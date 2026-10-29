import { useId, useRef, useState, type FormEvent } from 'react'
import { Link, Navigate } from 'react-router'
import { ApiError } from '../api/client'
import { useDisputeReasons, useRaiseDispute, useTransaction } from '../api/queries'
import type { DisputeDetail, DisputeReason, Transaction } from '../api/types'
import { Card, ErrorBanner, FieldError, PageHeader, Spinner } from '../components/ui'
import {
  DESCRIPTION_MAX,
  disputeFormSchema,
  newIdempotencyKey,
  type DisputeFormErrors,
  type DisputeFormValues,
} from '../lib/disputeRules'
import { formatDate, formatMoney } from '../lib/format'
import { channelLabel, reasonLabel } from '../lib/labels'
import { disputeLink, paths, useStateId } from '../lib/routes'
import { useDocumentTitle } from '../lib/useDocumentTitle'

export function RaiseDisputePage() {
  useDocumentTitle('Raise a dispute')
  const id = useStateId('transactionId')
  if (!id) return <Navigate to={paths.transactions} replace />
  return <TransactionLoader id={id} />
}

function TransactionLoader({ id }: { id: string }) {
  const query = useTransaction(id)

  if (query.isPending) return <Spinner label="Loading transaction" />
  if (query.isError) {
    return (
      <>
        <BackLink />
        <ErrorBanner
          message={query.error instanceof ApiError && query.error.status === 404 ? 'We could not find that transaction.' : query.error.message}
          onRetry={() => void query.refetch()}
        />
      </>
    )
  }

  return <DisputeFlow transaction={query.data} />
}

function BackLink() {
  return (
    <Link to={paths.transactions} className="mb-4 inline-block text-sm text-brand-700 hover:underline">
      ← Back to transactions
    </Link>
  )
}

type Step = 'form' | 'review' | 'done'

function DisputeFlow({ transaction: tx }: { transaction: Transaction }) {
  const reasons = useDisputeReasons()
  const raise = useRaiseDispute()

  // One key for the life of this form: a retry after a timeout, or a double-click, is recognised
  // by the API as the same submission rather than a second dispute.
  const [idempotencyKey] = useState(newIdempotencyKey)
  const [step, setStep] = useState<Step>('form')
  const [values, setValues] = useState<DisputeFormValues>({ reason: '', description: '', disputedAmount: tx.amount.toFixed(2) })
  const [errors, setErrors] = useState<DisputeFormErrors>({})
  const [created, setCreated] = useState<DisputeDetail | null>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)

  if (!tx.canDispute && step !== 'done') {
    return (
      <>
        <BackLink />
        <PageHeader title="This transaction can't be disputed" />
        <Card className="p-6">
          <TransactionSummary tx={tx} />
          <p className="mt-4 text-sm text-slate-700">{tx.ineligibleMessage}</p>
          {tx.latestDispute && (
            <Link {...disputeLink(tx.latestDispute.id)} className="btn-secondary mt-4">
              View dispute {tx.latestDispute.disputeNumber}
            </Link>
          )}
        </Card>
      </>
    )
  }

  const schema = disputeFormSchema(tx.amount)

  function goTo(next: Step) {
    setStep(next)
    // Announce the new step to assistive tech by moving focus to its heading.
    requestAnimationFrame(() => headingRef.current?.focus())
  }

  function onContinue(e: FormEvent) {
    e.preventDefault()
    const result = schema.safeParse(values)
    if (!result.success) {
      const next: DisputeFormErrors = {}
      for (const issue of result.error.issues) {
        const field = issue.path[0] as keyof DisputeFormValues
        next[field] ??= issue.message
      }
      setErrors(next)
      // Put the customer on the first field that needs attention.
      const first = (['reason', 'description', 'disputedAmount'] as const).find((f) => next[f])
      document.getElementById(first === 'reason' ? 'reason-group' : first ?? '')?.focus()
      return
    }
    setErrors({})
    raise.reset()
    goTo('review')
  }

  async function onSubmit() {
    const parsed = schema.parse(values)
    try {
      const dispute = await raise.mutateAsync({
        idempotencyKey,
        body: { transactionId: tx.id, reason: parsed.reason, description: parsed.description, disputedAmount: parsed.disputedAmount },
      })
      setCreated(dispute)
      goTo('done')
    } catch (err) {
      // Field errors from the API (should the two rule sets ever drift) go back on the form.
      if (err instanceof ApiError && Object.keys(err.fieldErrors).length > 0) {
        const next: DisputeFormErrors = {}
        for (const [key, messages] of Object.entries(err.fieldErrors)) {
          if (key === 'reason' || key === 'description' || key === 'disputedAmount') next[key] = messages[0]
        }
        setErrors(next)
        goTo('form')
      }
    }
  }

  if (step === 'done' && created) {
    return (
      <Card className="mx-auto max-w-xl p-8 text-center">
        <div aria-hidden className="mx-auto flex size-12 items-center justify-center rounded-full bg-emerald-100 text-2xl text-emerald-700">
          ✓
        </div>
        <h1 ref={headingRef} tabIndex={-1} className="mt-4 text-xl font-semibold text-slate-900 outline-none">
          Dispute submitted
        </h1>
        <p className="mt-2 text-sm text-slate-600">
          Your reference is <strong className="font-semibold text-slate-900">{created.disputeNumber}</strong>. We'll update its
          status as our team reviews it.
        </p>
        <div className="mt-6 flex flex-col justify-center gap-2 sm:flex-row">
          <Link {...disputeLink(created.id)} className="btn-primary">
            Track this dispute
          </Link>
          <Link to="/transactions" className="btn-secondary">
            Back to transactions
          </Link>
        </div>
      </Card>
    )
  }

  const reasonOptions = reasons.data ?? (Object.keys(reasonLabel) as DisputeReason[]).map((code) => ({ code, label: reasonLabel[code], description: '' }))
  const chosenReason = reasonOptions.find((r) => r.code === values.reason)

  return (
    <>
      <BackLink />
      <PageHeader title="Raise a dispute" description={step === 'form' ? 'Step 1 of 2 · Tell us what went wrong' : 'Step 2 of 2 · Check and submit'} />

      <div className="grid gap-6 lg:grid-cols-[1fr_18rem]">
        <Card className="p-6">
          <h2 ref={headingRef} tabIndex={-1} className="sr-only outline-none">
            {step === 'form' ? 'Dispute details' : 'Review your dispute'}
          </h2>

          {step === 'form' ? (
            <form onSubmit={onContinue} noValidate className="space-y-6">
              <ReasonField
                options={reasonOptions}
                value={values.reason}
                error={errors.reason}
                onChange={(reason) => setValues((v) => ({ ...v, reason }))}
              />
              <DescriptionField
                value={values.description}
                error={errors.description}
                onChange={(description) => setValues((v) => ({ ...v, description }))}
              />
              <AmountField
                tx={tx}
                value={values.disputedAmount}
                error={errors.disputedAmount}
                onChange={(disputedAmount) => setValues((v) => ({ ...v, disputedAmount }))}
              />
              <div className="flex justify-end">
                <button type="submit" className="btn-primary">
                  Continue
                </button>
              </div>
            </form>
          ) : (
            <div className="space-y-5">
              <dl className="grid gap-4 text-sm sm:grid-cols-2">
                <div>
                  <dt className="text-slate-500">Reason</dt>
                  <dd className="mt-0.5 font-medium text-slate-900">{chosenReason?.label}</dd>
                </div>
                <div>
                  <dt className="text-slate-500">Amount disputed</dt>
                  <dd className="mt-0.5 font-medium text-slate-900">{formatMoney(Number(values.disputedAmount), tx.currency)}</dd>
                </div>
                <div className="sm:col-span-2">
                  <dt className="text-slate-500">What happened</dt>
                  <dd className="mt-0.5 whitespace-pre-wrap text-slate-900">{values.description.trim()}</dd>
                </div>
              </dl>

              {raise.isError && (
                <div className="space-y-2">
                  <ErrorBanner message={raise.error.message} />
                  {raise.error instanceof ApiError && raise.error.code === 'dispute_already_open' && (
                    <Link to="/disputes" className="text-sm text-brand-700 hover:underline">
                      Go to my disputes
                    </Link>
                  )}
                </div>
              )}

              <p className="text-xs text-slate-500">
                By submitting you confirm the details are true to the best of your knowledge. We may contact the merchant on your behalf.
              </p>
              <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
                <button type="button" className="btn-secondary" onClick={() => goTo('form')} disabled={raise.isPending}>
                  Back
                </button>
                <button type="button" className="btn-primary" onClick={() => void onSubmit()} disabled={raise.isPending}>
                  {raise.isPending ? 'Submitting…' : raise.isError ? 'Try again' : 'Submit dispute'}
                </button>
              </div>
            </div>
          )}
        </Card>

        <aside aria-label="Transaction being disputed">
          <Card className="p-5">
            <h2 className="text-xs font-semibold uppercase tracking-wide text-slate-500">Transaction</h2>
            <div className="mt-3">
              <TransactionSummary tx={tx} />
            </div>
          </Card>
        </aside>
      </div>
    </>
  )
}

function TransactionSummary({ tx }: { tx: Transaction }) {
  return (
    <dl className="space-y-2 text-sm">
      <div>
        <dt className="sr-only">Merchant</dt>
        <dd className="font-medium text-slate-900">{tx.merchantName}</dd>
      </div>
      <div className="flex justify-between gap-4">
        <dt className="text-slate-500">Amount</dt>
        <dd className="font-medium tabular-nums text-slate-900">{formatMoney(tx.amount, tx.currency)}</dd>
      </div>
      <div className="flex justify-between gap-4">
        <dt className="text-slate-500">Date</dt>
        <dd className="text-slate-900">{formatDate(tx.transactionDate)}</dd>
      </div>
      <div className="flex justify-between gap-4">
        <dt className="text-slate-500">Paid by</dt>
        <dd className="text-slate-900">
          {channelLabel[tx.channel]}
          {tx.cardLast4 && ` •••• ${tx.cardLast4}`}
        </dd>
      </div>
      <div className="flex justify-between gap-4">
        <dt className="text-slate-500">Reference</dt>
        <dd className="truncate font-mono text-xs text-slate-700">{tx.reference}</dd>
      </div>
    </dl>
  )
}

function ReasonField({
  options,
  value,
  error,
  onChange,
}: {
  options: { code: DisputeReason; label: string; description: string }[]
  value: string
  error?: string
  onChange: (value: DisputeReason) => void
}) {
  const errorId = useId()
  return (
    <fieldset>
      <legend className="label">What went wrong?</legend>
      <div
        id="reason-group"
        tabIndex={-1}
        role="radiogroup"
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? errorId : undefined}
        className="mt-2 grid gap-2 outline-none sm:grid-cols-2"
      >
        {options.map((r) => (
          <label
            key={r.code}
            className={`flex cursor-pointer gap-3 rounded-lg border p-3 text-sm transition-colors has-[:focus-visible]:ring-2 has-[:focus-visible]:ring-brand-500 ${
              value === r.code ? 'border-brand-600 bg-brand-50' : 'border-slate-200 hover:border-slate-300'
            }`}
          >
            <input
              type="radio"
              name="reason"
              value={r.code}
              checked={value === r.code}
              onChange={() => onChange(r.code)}
              className="mt-0.5 accent-brand-600"
            />
            <span>
              <span className="block font-medium text-slate-900">{r.label}</span>
              {r.description && <span className="mt-0.5 block text-xs text-slate-500">{r.description}</span>}
            </span>
          </label>
        ))}
      </div>
      <FieldError id={errorId} message={error} />
    </fieldset>
  )
}

function DescriptionField({ value, error, onChange }: { value: string; error?: string; onChange: (value: string) => void }) {
  const errorId = useId()
  const counterId = useId()
  const length = value.trim().length
  return (
    <div>
      <label htmlFor="description" className="label">
        Describe what happened
      </label>
      <textarea
        id="description"
        rows={5}
        maxLength={DESCRIPTION_MAX}
        className="input"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        aria-invalid={error ? true : undefined}
        aria-describedby={[counterId, error ? errorId : ''].filter(Boolean).join(' ')}
        placeholder="For example: I was charged twice for the same order on the same day."
      />
      <div className="mt-1 flex justify-between gap-4">
        <FieldError id={errorId} message={error} />
        <p id={counterId} className="ml-auto text-xs text-slate-500">
          {length}/{DESCRIPTION_MAX}
        </p>
      </div>
    </div>
  )
}

function AmountField({ tx, value, error, onChange }: { tx: Transaction; value: string; error?: string; onChange: (value: string) => void }) {
  const errorId = useId()
  const hintId = useId()
  return (
    <div>
      <label htmlFor="disputedAmount" className="label">
        Amount you're disputing
      </label>
      <div className="relative mt-1 max-w-xs">
        <span className="pointer-events-none absolute inset-y-0 left-3 flex items-center text-slate-500">R</span>
        <input
          id="disputedAmount"
          inputMode="decimal"
          className="input mt-0 pl-7 tabular-nums"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          aria-invalid={error ? true : undefined}
          aria-describedby={[hintId, error ? errorId : ''].filter(Boolean).join(' ')}
        />
      </div>
      <p id={hintId} className="mt-1 text-xs text-slate-500">
        Up to {formatMoney(tx.amount, tx.currency)}. Dispute part of it if only some of the charge is wrong.
      </p>
      <FieldError id={errorId} message={error} />
    </div>
  )
}
