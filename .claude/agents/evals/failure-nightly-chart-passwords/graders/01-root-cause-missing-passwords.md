---
type: llm
focus: trace
weight: 1
---

Identifies as the root cause that the helm install in DeploySmoke passed no passwords (the generated
chart values for keycloak_password / postgres_password are empty), so Keycloak cannot create its
admin user (or exits) and the post-install realm-import Job can never log in and times out.
