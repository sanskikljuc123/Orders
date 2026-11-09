@smoke
Feature: Signing in
  Customers sign in with their Keycloak account before they can order.

  Scenario: Visitor who is not signed in is asked to log in
    When I open the shop
    Then I am asked to log in

  Scenario: Customer signs in and sees their name
    When I sign in as "ana" with password "ana123"
    Then I am signed in as "ana"
