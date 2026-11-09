# JS/TS unit and integration tests (Vitest / Jest)

Use whichever runner the repo already has. New repo: Vitest for Vite/ESM projects, Jest where the
ecosystem expects it (older CRA, React Native). Look up versions with `npm view <pkg> version`.

## Config

```ts
// vitest.config.ts
import { defineConfig } from 'vitest/config';
export default defineConfig({
  test: {
    environment: 'node',            // 'jsdom' / 'happy-dom' for components
    restoreMocks: true,             // no mock leaks between tests
    sequence: { shuffle: true },    // random order exposes coupling
    testTimeout: 5_000,
    coverage: { provider: 'v8', thresholds: { branches: 70 } },   // floor = current level
  },
});
```
Jest equivalents: `restoreMocks: true`, `randomize: true` (Jest ≥ 29.2), `coverageThreshold`.

## Unit

```ts
describe('calculateTotal', () => {
  it.each([
    { qty: 1, price: 10, expected: 10 },
    { qty: 0, price: 10, expected: 0 },
    { qty: 3, price: 0.1, expected: 0.3 },
  ])('qty $qty × $price = $expected', ({ qty, price, expected }) => {
    expect(calculateTotal(qty, price)).toBeCloseTo(expected);
  });

  it('rejects negative quantity with a clear message', () => {
    expect(() => calculateTotal(-1, 10)).toThrow('Quantity must be >= 0');
  });
});
```

- Time: `vi.useFakeTimers(); vi.setSystemTime(new Date('2026-01-01'))` (Jest: `jest.useFakeTimers()`).
- Randomness: inject a seeded generator; don't assert on `Math.random()` output.
- Mock at module boundaries (`vi.mock('./payment-client')`), not the code under test.

## Components (Testing Library)

Query like a user: `getByRole`, `getByLabelText`, `findByText`; interact with `user-event`.
Assert what's rendered, not component state or props.

```ts
it('shows a validation error when product is empty', async () => {
  const user = userEvent.setup();
  render(<OrderForm />);
  await user.click(screen.getByRole('button', { name: 'Place order' }));
  expect(await screen.findByRole('alert')).toHaveTextContent('Product is required');
});
```

## HTTP integration

- In-process server: Supertest against the exported app (`request(app).post('/orders')`).
- Outbound HTTP: MSW (`setupServer`) with handlers per test, `server.resetHandlers()` in
  `afterEach`, `onUnhandledRequest: 'error'` so no real network slips through.
- Real DB: `testcontainers` npm package (`new PostgreSqlContainer('postgres:17').start()` in
  `beforeAll`, `stop()` in `afterAll`), unique data per test.
