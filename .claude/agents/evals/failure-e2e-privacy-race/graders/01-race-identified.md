---
type: llm
focus: trace
weight: 1
---

Identifies that OrderPage.PlaceOrderAsync (tests/Orders.E2E/Pages/OrderPage.cs at c7e063f) returns
right after clicking without waiting for the POST /orders response, and that the following step
navigates/reloads the page (OpenMyOrdersAsync / page.GotoAsync), which can cancel the request in
flight.
