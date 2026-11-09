Feature: Placing an order

  Background:
    Given I am signed in as "ana"

  Scenario: Order is paid when payment is approved
    Given the payment service approves payments
    When I order 2 of "Book"
    Then I see the result "Order Paid"

  Scenario: Order fails when payment is declined
    Given the payment service declines payments
    When I order 2 of "Book"
    Then I see the result "Order PaymentFailed"

  Scenario: Empty product name is rejected
    Given the payment service approves payments
    When I order 1 without a product name
    Then I see the result "Validation error"

  Scenario: Users see only their own orders
    Given the payment service approves payments
    And "bob" has ordered "Bobs secret order"
    When I order 1 of "Anas book"
    Then my order list contains "Anas book"
    And my order list does not contain "Bobs secret order"