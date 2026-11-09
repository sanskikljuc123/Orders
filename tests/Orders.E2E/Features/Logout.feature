@regression
Feature: Signing out
  A customer who logs out is signed out of the shop and of Keycloak, and their browser can no
  longer place orders.

  Background:
    Given I have just signed in as "ana"

  @smoke
  Scenario: Customer who logs out is asked to log in again
    When I log out
    Then I am asked to log in

  Scenario: Customer who logs out can no longer place an order
    When I log out
    Then I can no longer place an order

  Scenario: Customer who logs out must enter their password again
    When I log out
    And I start logging in again
    Then I am asked for my password

  Scenario: A copy of the session cookie stops working after logout
    Given I keep a copy of my session cookie
    Then the copied session cookie is accepted
    When I log out
    Then the copied session cookie is rejected
