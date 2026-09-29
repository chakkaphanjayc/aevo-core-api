# ADR 0001: Core API is the authority boundary

Status: accepted for the polyrepo transition

The application Workers are presentation and edge boundaries. They may proxy or call the Core API, but they cannot decide organization/store membership, platform role, subscription entitlement, or privileged audit outcome. The Core API resolves those facts from the app-scoped session and server-side assignment tables.

The first implementation keeps the host safe while dependencies are absent: health is observable, readiness is explicit, and protected endpoints fail closed instead of manufacturing local data.

