Feature: Placing an order

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

    
  Scenario: Ordering without logging in is rejected
    When I place an order without logging in
    Then the request is rejected with status 401