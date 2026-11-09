Feature: Signing in

  Scenario: Visitor must log in first
    When I open the app
    Then I see the log in button

  Scenario: User signs in through Keycloak
    When I open the app
    And I sign in as "ana" with password "ana123"
    Then I see that I am signed in as "ana"

  Scenario: Wrong password is rejected
    When I open the app
    And I sign in as "ana" with password "wrong"
    Then I see a login error

  Scenario: Ordering through the API without logging in is rejected
    When I place an order without logging in
    Then the request is rejected with status 401