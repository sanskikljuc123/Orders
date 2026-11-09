@regression
Feature: Placing an order
  Signed-in customers order products; the payment result decides the order status.
  Validation rules in detail are covered by unit tests; one representative case is here.

  Background:
    Given I am signed in as "ana"

  @smoke
  Scenario: Order is paid when payment is approved
    Given the payment service approves payments
    When I order 2 of "Book"
    Then I see the result "Order Paid"
    And my orders show that order as "Paid"

  Scenario: Order is marked as failed when payment is declined
    Given the payment service declines payments
    When I order 1 of "Book"
    Then I see the result "Order PaymentFailed"
    And my orders show that order as "PaymentFailed"

  Scenario: Order is never shown as paid when the payment service fails
    Given the payment service is failing
    When I order 1 of "Book"
    Then I see the result "Order PaymentFailed"

  Scenario: Order without a product name is rejected
    When I order 1 without a product name
    Then I see the result "Validation error"
