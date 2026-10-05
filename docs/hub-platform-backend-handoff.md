# Hub platform backend handoff

This Core slice hardens the existing member application assignment routes. It
does not change route templates or response shapes, and it does not modify the
canonical `@aevocado/contracts` checkout.

## Contract decisions still needed

- **Member application roles and states:** `aevo_application_assignments` stores
  only `active` and `disabled`, and its permissions come from the organization
  role. Define the app-role authority and the public status values before Core
  adds a separate app-role projection or distinguishes suspended from revoked.
- **Billing self-service:** Core has tenant-scoped subscription and entitlement
  reads plus a webhook projection. Define the provider-owned portal boundary,
  trusted return-URL configuration, and which service verifies Stripe webhook
  signatures before adding self-service billing operations. Payment success must
  continue to come from verified provider events.
- **Query Platform and jobs:** Define versioned dataset/field metadata, allowed
  filter and sort operators, import/export preview semantics, job states,
  idempotency-key scope, worker and artifact-storage ownership, and audit/quota
  outcomes before adding query execution or asynchronous report jobs.

No browser-selected SQL, tenant identifiers, or field identifiers are accepted
by this Core change.
