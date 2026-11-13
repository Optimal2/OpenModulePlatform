# Context Guard Module

A first-party OMP web module that accepts a one-time hand-off from a calling system, redeems it only after OMP authentication, and keeps one live target-application context per configured owner scope.

The complete operator and integration contract, including every setting and the deliberate v1 limits, is in [`docs/CONTEXT_GUARD_MODULE.md`](../docs/CONTEXT_GUARD_MODULE.md).

The module has no SQL objects and no identity-provider integration. It trusts the OMP principal and its `omp:user_id` claim. Version 1 keeps both stores in process memory and therefore supports one deployed instance per route.
