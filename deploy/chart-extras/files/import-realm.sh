#!/bin/bash
# Creates the production "orders" realm in Keycloak if it does not exist yet. Never changes or
# deletes an existing realm (changes made in the admin console are kept).
# Env: KEYCLOAK_URL, KC_BOOTSTRAP_ADMIN_USERNAME, KC_BOOTSTRAP_ADMIN_PASSWORD, REALM_FILE.
set -euo pipefail
KCADM=/opt/keycloak/bin/kcadm.sh
CONFIG=/tmp/kcadm.config

for attempt in $(seq 1 30); do
  if "$KCADM" config credentials --config "$CONFIG" --server "$KEYCLOAK_URL" --realm master \
       --user "$KC_BOOTSTRAP_ADMIN_USERNAME" --password "$KC_BOOTSTRAP_ADMIN_PASSWORD" >/dev/null 2>&1; then
    break
  fi
  if [ "$attempt" -eq 30 ]; then echo "Keycloak admin login failed at $KEYCLOAK_URL" >&2; exit 1; fi
  echo "Waiting for Keycloak at $KEYCLOAK_URL ($attempt/30)"
  sleep 10
done

if "$KCADM" get realms/orders --config "$CONFIG" >/dev/null 2>&1; then
  echo "Realm 'orders' already exists: left unchanged."
else
  "$KCADM" create realms --config "$CONFIG" -f "$REALM_FILE"
  echo "Realm 'orders' created from $REALM_FILE."
fi
