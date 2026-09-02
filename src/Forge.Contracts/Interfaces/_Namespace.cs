namespace Forge.Contracts.Interfaces;

// The Forge.Contracts.Interfaces namespace holds the public, client-facing contract
// surface that the Game (and other clients) may reference. In Phase 1 the Game is a
// pure rendering / human-in-the-loop layer: it consumes the immutable DTOs in
// Forge.Contracts.Dtos, receives event schemas from Forge.Contracts.Events over the
// real-time channel, and triggers all behavior by calling Api endpoints (Req 2.1, 20.1).
//
// Consequently there is intentionally no client-side service abstraction that leaks
// domain types across this boundary — the Game computes no business rules locally
// (Req 24.6). The shared contract surface is therefore the DTOs + event schemas + the
// operator-parameter contract in Forge.Contracts.OperatorParameters, plus the marker
// below that lets clients discover the versioned contract surface without depending on
// any Domain/Application/Infrastructure type.
