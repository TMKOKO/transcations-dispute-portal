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
