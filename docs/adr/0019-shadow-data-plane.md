# ADR-0019: A shadow caster is an ownership slot read from Alloy's own draw records, with no data plane of its own.

Category: Architecture.

Decided 2026-08-30.

Because the shadow pass had built private copies of tables Alloy already keeps current, and the copies could disagree.
