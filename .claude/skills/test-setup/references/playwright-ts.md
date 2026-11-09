# Playwright Test (TypeScript/JavaScript)

Install with `npm init playwright@latest` (or add `@playwright/test` at the version `npm view
@playwright/test version` reports) and `npx playwright install --with-deps chromium`. Check the
installed version's docs if an option below is missing.

## Config — timeouts, baseURL, parallelism, artifacts live here

```ts
// playwright.config.ts
import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,                 // requires real isolation, see below
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,     // retries surface flakiness in the report; fix, don't hide
  workers: process.env.CI ? 4 : undefined,
  timeout: 30_000,
  expect: { timeout: 5_000 },
  reporter: [['html', { open: 'never' }], ['list']],
  use: {
    baseURL: process.env.BASE_URL ?? 'http://localhost:3000',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  projects: [
    { name: 'setup', testMatch: /.*\.setup\.ts/ },
    { name: 'chromium', use: { ...devices['Desktop Chrome'], storageState: 'playwright/.auth/user.json' }, dependencies: ['setup'] },
  ],
  webServer: { command: 'npm run start', url: 'http://localhost:3000', reuseExistingServer: !process.env.CI },
});
```

Add `playwright/.auth/` to `.gitignore`.

## Authenticated state — log in once

```ts
// e2e/auth.setup.ts
import { test as setup, expect } from '@playwright/test';

setup('authenticate', async ({ page }) => {
  await page.goto('/login');
  await page.getByLabel('Email').fill(process.env.E2E_USER!);
  await page.getByLabel('Password').fill(process.env.E2E_PASSWORD!);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('button', { name: 'Account' })).toBeVisible();
  await page.context().storageState({ path: 'playwright/.auth/user.json' });
});
```

## Custom fixtures — shared setup with automatic teardown

```ts
// e2e/fixtures.ts
import { test as base, expect } from '@playwright/test';
import { OrdersPage } from './pages/orders-page';

type Fixtures = { ordersPage: OrdersPage; seededOrder: { id: string; product: string } };

export const test = base.extend<Fixtures>({
  ordersPage: async ({ page }, use) => { await use(new OrdersPage(page)); },
  seededOrder: async ({ request }, use) => {
    const product = `Book-${crypto.randomUUID()}`;          // unique per test
    const res = await request.post('/api/orders', { data: { product, quantity: 1 } });
    expect(res.status()).toBe(201);
    const order = await res.json();
    await use({ id: order.id, product });
    await request.delete(`/api/orders/${order.id}`);       // teardown runs even if the test fails
  },
});
export { expect };
```

## Page object — selectors live only here

```ts
export class OrdersPage {
  constructor(private readonly page: Page) {}
  readonly result = () => this.page.getByTestId('result');
  async open() { await this.page.goto('/orders'); }        // relative to baseURL
  async placeOrder(product: string, quantity: number) {
    await this.page.getByLabel('Product').fill(product);
    await this.page.getByLabel('Quantity').fill(String(quantity));
    await this.page.getByRole('button', { name: 'Place order' }).click();
  }
}
```

## Tests — describe, tags, soft assertions, parameterization

```ts
import { test, expect } from './fixtures';

test.describe('Placing an order', () => {
  test.beforeEach(async ({ ordersPage }) => { await ordersPage.open(); });   // minimal

  test('paid order shows confirmation', { tag: ['@smoke', '@critical'] }, async ({ ordersPage, page }) => {
    await ordersPage.placeOrder('Book', 2);
    await expect(ordersPage.result()).toHaveText('Order Paid');
    await expect.soft(page.getByTestId('total')).toHaveText('€20.00');
    await expect.soft(page.getByTestId('orders-list')).toContainText('Book');
  });

  test('declined payment shows failure @regression', async ({ ordersPage, page }) => {
    await page.route('**/payments/charge', r => r.fulfill({ status: 200, json: { approved: false } }));
    await ordersPage.placeOrder('Book', 1);
    await expect(ordersPage.result(), 'declined payment must not look successful').toHaveText('Payment failed');
  });
});

const roles = [
  { role: 'admin', canDelete: true },
  { role: 'viewer', canDelete: false },
];
test.describe('Delete permission', () => {
  for (const { role, canDelete } of roles) {
    test(`${role} ${canDelete ? 'can' : 'cannot'} delete an order`, async ({ page, seededOrder }) => {
      // sign in as role (per-role storageState fixture), open the order...
      const del = page.getByRole('button', { name: 'Delete' });
      canDelete ? await expect(del).toBeVisible() : await expect(del).toHaveCount(0);
    });
  }
});
```

Tags work either in the title (`'checkout flow @smoke @critical'`) or via `{ tag: [...] }`; run
with `npx playwright test --grep @smoke`.

## Waiting — never `page.waitForTimeout()`

- Web-first assertions retry: `await expect(locator).toHaveText(...)`.
- Wait for the event you mean: `page.waitForResponse('**/api/orders')`, `page.waitForURL('/done')`,
  `expect.poll(() => fetchStatus())`.
- Third-party calls made by the browser: `page.route()` to fulfill them; never hit the real provider.

## Debugging

- `npx playwright test --ui`, `--headed`, `--debug`.
- Trace: `npx playwright show-trace test-results/<test>/trace.zip`.
- Explore the live UI to pick locators before writing them (`npx playwright codegen <url>`).

## CI

```yaml
- run: npm ci
- run: npx playwright install --with-deps chromium
- run: npx playwright test
- uses: actions/upload-artifact@v4
  if: ${{ !cancelled() }}
  with: { name: playwright-report, path: playwright-report/ }
```
If you use the Playwright Docker image, its tag must match the `@playwright/test` version.
