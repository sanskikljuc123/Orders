@regression
Feature: An order that is not confirmed yet
  When the shop cannot confirm an order yet, the customer is told so and can safely place it again.
  The shop's first answer is stubbed in the browser; the server side (Pending orders, nothing
  charged twice) is covered by integration tests.

  Background:
    Given I am signed in as "ana"
    And the payment service approves payments

  Scenario: Customer is told an order is waiting for payment confirmation and can retry it
    Given the shop answers my next order as pending
    When I order 1 of "Book"
    Then I see the result "Order Pending: payment not confirmed yet. Place the order again to retry."
    When I place the same order again
    Then I see the result "Order Paid"
    And both order attempts were sent as the same order attempt
    And my orders contain that order exactly once

  Scenario: Customer is told nothing was charged when the shop fails, and can retry
    Given the shop fails my next order with a server error
    When I order 1 of "Book"
    Then I see the result "Order not placed, nothing was charged. Please try again."
    When I place the same order again
    Then I see the result "Order Paid"
    And both order attempts were sent as the same order attempt
    And my orders contain that order exactly once
