# Service auth is a shared API secret, not OAuth/JWT

This service has exactly one caller — the existing backend — so we authenticate with a shared API secret sent as a request header (REST and the SignalR hub handshake alike), instead of OAuth2 client credentials with JWT validation. User context (doctorId, patientId, …) is passed explicitly in each request payload; there are no user tokens at this boundary, and authorization — which doctor may upload, view, or un-ingest what — is enforced by the backend, which this service fully trusts.

Why: for a single trusted internal caller, token infrastructure adds a runtime dependency on an identity provider and integration ceremony without adding a security property we use — per-user claims would duplicate what the payload already carries explicitly. A header check is simpler to build, test, and reason about.

## Consequences

- Two secrets, not one: a standard key for normal operations and a separate admin key required for GDPR Erasure, so a leaked everyday key cannot erase patients. Both live in the estate's secret store; validation accepts two active keys so rotation is zero-downtime.
- This service must never be exposed beyond the backend. If a second caller, public exposure, or per-user authorization at this boundary ever appears, this decision is the first thing to revisit — do not bolt scopes onto the secret.
- Do not "upgrade" this to OAuth/JWT for conformity; the deviation is deliberate.
