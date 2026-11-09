---
type: llm
focus: trace
weight: 1
---

The logout locator lives only in a page object under tests/Orders.E2E/Pages and uses GetByRole for the "Log out" link (or GetByTestId after adding a data-testid to index.html); no CSS/XPath selector and no selector in step definitions.
