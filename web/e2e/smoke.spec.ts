import { expect, test, type Browser, type Page } from '@playwright/test'

// The real portal, API, Postgres, Redis and Kafka. Accounts come from DbSeeder, which only the
// throwaway stack in scripts/e2e.sh seeds for these tests. Each flow keeps to its own customer.
const PASSWORD = 'Demo@12345!'
const THANDI = 'thandi@example.com'
const SIPHO = 'sipho@example.com'
const LERATO = 'lerato@example.com'
const AGENT = 'agent@example.com'
const SIPHO_CARD = '•••• 7390' // DbSeeder gives each customer their own card

async function signIn(page: Page, email: string, password = PASSWORD) {
  await page.goto('/login')
  await page.getByLabel('Email address').fill(email)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
}

async function signedInPage(browser: Browser, email: string) {
  // A context of its own is a separate cookie jar, like a separate browser.
  const page = await (await browser.newContext()).newPage()
  await signIn(page, email)
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible()
  return page
}

/** Raises a dispute on the first eligible transaction, ends on its detail page, and returns its number. */
async function raiseDispute(page: Page) {
  await page.goto('/transactions')
  await page.getByRole('link', { name: /^Dispute .+ payment of / }).first().click()
  await page.getByRole('radio', { name: /charged more than once/i }).check()
  await page.getByLabel('Describe what happened').fill('I was charged twice for the same purchase in this test.')
  await page.getByRole('button', { name: 'Continue' }).click()
  await page.getByRole('button', { name: 'Submit dispute' }).click()
  await expect(page.getByRole('heading', { name: 'Dispute submitted' })).toBeVisible()

  await page.getByRole('link', { name: 'Track this dispute' }).click()
  const heading = page.getByRole('heading', { name: /^DSP-\d{4}-\d{6}$/ })
  await expect(heading).toBeVisible()
  return (await heading.textContent())!.trim()
}

async function sendDecision(agent: Page, disputeNumber: string, decision: 'UnderReview' | 'RefundIssued') {
  await agent.getByRole('button', { name: `Update status of ${disputeNumber}` }).click()
  const dialog = agent.getByRole('dialog', { name: `Update ${disputeNumber}` })
  await dialog.getByLabel('Decision').selectOption(decision)
  await dialog.getByRole('button', { name: 'Send decision' }).click()
  await expect(agent.getByRole('status').first()).toContainText(`Sent ${decision}`)
}

test('a customer signs in and sees their transactions; a wrong password is refused', async ({ page }) => {
  await signIn(page, THANDI, 'not-the-password')
  await expect(page.getByRole('alert')).toContainText('Email or password is incorrect.')

  await signIn(page, THANDI)
  await expect(page.getByRole('heading', { name: 'Transactions' })).toBeVisible()
  await expect(page.getByText('Hi, Thandi')).toBeVisible()
  await expect(page.getByRole('list', { name: 'Transactions' }).getByRole('listitem').first()).toBeVisible()
})

test('a customer raises a dispute, finds it in My disputes, then withdraws it', async ({ browser }) => {
  const page = await signedInPage(browser, THANDI)
  const number = await raiseDispute(page)

  await page.getByRole('link', { name: 'My disputes' }).click()
  const row = page.getByRole('list', { name: 'Disputes' }).getByRole('link', { name: new RegExp(number) })
  await expect(row).toContainText('Submitted')

  await row.click()
  await page.getByRole('button', { name: 'Withdraw dispute' }).click()
  const dialog = page.getByRole('dialog', { name: 'Withdraw this dispute?' })
  await dialog.getByLabel('Reason (optional)').fill('Sorted out with the merchant.')
  await dialog.getByRole('button', { name: 'Withdraw dispute' }).click()

  await expect(page.getByText('Your dispute has been withdrawn.')).toBeVisible()
  const timeline = page.getByRole('list', { name: 'Dispute timeline' })
  await expect(timeline).toContainText('Sorted out with the merchant.')
  await expect(page.getByRole('button', { name: 'Withdraw dispute' })).toHaveCount(0)
})

test("an agent's decisions travel through Kafka to the customer's dispute", async ({ browser }) => {
  const customer = await signedInPage(browser, SIPHO)
  const number = await raiseDispute(customer)
  const agent = await signedInPage(browser, AGENT)

  const reloadUntilStatus = (status: string) =>
    expect(async () => {
      await customer.reload()
      await expect(customer.getByRole('list', { name: 'Dispute timeline' })).toContainText(status, { timeout: 1_000 })
    }).toPass({ timeout: 30_000 })

  await sendDecision(agent, number, 'UnderReview')
  await reloadUntilStatus('Under review')

  await expect(agent.getByRole('list', { name: 'Disputes' }).getByRole('listitem').filter({ hasText: number })).toContainText('Under review', {
    timeout: 15_000,
  })
  await sendDecision(agent, number, 'RefundIssued')
  await reloadUntilStatus('Resolved')
  await expect(customer.getByRole('button', { name: 'Withdraw dispute' })).toHaveCount(0)
})

test('an agent signing in on the same browser signs the customer tab out, with the reason', async ({ browser }) => {
  const context = await browser.newContext()
  const customerTab = await context.newPage()
  await signIn(customerTab, LERATO)
  await expect(customerTab.getByText('Hi, Lerato')).toBeVisible()

  const agentTab = await context.newPage()
  await signIn(agentTab, AGENT)
  await expect(agentTab.getByRole('heading', { name: 'All disputes' })).toBeVisible()

  await customerTab.getByRole('link', { name: 'My disputes' }).click()

  await expect(customerTab.getByRole('heading', { name: 'Sign in' })).toBeVisible()
  await expect(customerTab.getByRole('status')).toContainText('Another account signed in on this browser')

  // That tab did not sign the other account out.
  await agentTab.reload()
  await expect(agentTab.getByRole('heading', { name: 'All disputes' })).toBeVisible()
})

test("a second customer signing in on the same browser never puts their data under the first one's name", async ({ browser }) => {
  // Known bug: the cookie moves to the second customer, but the first tab keeps its stored name, and
  // nothing in the response says whose data it is. Reloading shows Lerato's name over Sipho's card.
  test.fixme()

  const context = await browser.newContext()
  const firstTab = await context.newPage()
  await signIn(firstTab, LERATO)
  await expect(firstTab.getByText('Hi, Lerato')).toBeVisible()

  const secondTab = await context.newPage()
  await signIn(secondTab, SIPHO)
  await expect(secondTab.getByText('Hi, Sipho')).toBeVisible()
  await expect(secondTab.getByText(SIPHO_CARD).first()).toBeVisible()

  await firstTab.reload()
  await firstTab.waitForLoadState('networkidle')

  // Signing this tab out would be fine. Still showing Lerato's name over Sipho's card is not.
  const stillLerato = await firstTab.getByText('Hi, Lerato').isVisible()
  const showsSiphosCard = await firstTab.getByText(SIPHO_CARD).first().isVisible()
  expect({ stillLerato, showsSiphosCard }).not.toEqual({ stillLerato: true, showsSiphosCard: true })
})
