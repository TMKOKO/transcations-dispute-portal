// All display formatting in one place: ZAR amounts and South African local time,
// regardless of the viewer's browser locale or time zone.

const TIME_ZONE = 'Africa/Johannesburg'

const money = new Intl.NumberFormat('en-ZA', { style: 'currency', currency: 'ZAR' })

export function formatMoney(amount: number, currency = 'ZAR'): string {
  if (currency === 'ZAR') return money.format(amount)
  return new Intl.NumberFormat('en-ZA', { style: 'currency', currency }).format(amount)
}

const dateFmt = new Intl.DateTimeFormat('en-ZA', { timeZone: TIME_ZONE, day: 'numeric', month: 'short', year: 'numeric' })
const dateTimeFmt = new Intl.DateTimeFormat('en-ZA', {
  timeZone: TIME_ZONE,
  day: 'numeric',
  month: 'short',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
})

export const formatDate = (iso: string) => dateFmt.format(new Date(iso))
export const formatDateTime = (iso: string) => dateTimeFmt.format(new Date(iso))

/** Today's date in SAST as yyyy-MM-dd, for date inputs and API filters. */
export function todaySast(): string {
  // en-CA formats as yyyy-MM-dd.
  return new Intl.DateTimeFormat('en-CA', { timeZone: TIME_ZONE }).format(new Date())
}

/** yyyy-MM-dd to dd/mm/yyyy, the way customers read and type dates. */
export function isoToDmy(iso: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(iso)
  return m ? `${m[3]}/${m[2]}/${m[1]}` : ''
}

/** dd/mm/yyyy (or with - or .) to yyyy-MM-dd; null when it is not a real date. */
export function dmyToIso(text: string): string | null {
  const m = /^(\d{1,2})[/.-](\d{1,2})[/.-](\d{4})$/.exec(text.trim())
  if (!m) return null
  const [day, month, year] = [Number(m[1]), Number(m[2]), Number(m[3])]
  // Date.UTC rolls 31/02 over into March, so a date that does not round-trip does not exist.
  const date = new Date(Date.UTC(year, month - 1, day))
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day) return null
  return `${year}-${String(month).padStart(2, '0')}-${String(day).padStart(2, '0')}`
}
