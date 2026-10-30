# ADR-0011: The world frame runs in a fixed pass order, with world shadows baked ahead of colour and creature shadows a frame behind.

Category: Architecture.

Decided 2026-08-20.

Because baking at the present made the colour pass sample last frame's maps, creature poses exist only after it, and a third GL bracket was unaffordable.
