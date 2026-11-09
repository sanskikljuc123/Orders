@regression
Feature: Order privacy
  Customers only ever see their own orders.

  Scenario Outline: A user never sees another user's orders
    Given "<other>" has ordered "<secret>"
    And I am signed in as "<user>"
    And I have ordered "<mine>"
    When I open my orders
    Then my order list contains my order
    And my order list does not contain "<secret>"

    Examples:
      | user | other | mine       | secret            |
      | ana  | bob   | Anas order | Bobs secret order |
      | bob  | ana   | Bobs order | Anas secret order |
